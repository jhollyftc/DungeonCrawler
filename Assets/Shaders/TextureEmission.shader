Shader "Custom/URP/TextureEmission"
{
    // Unlit emissive surface for glowing kit geometry — stained glass windows above all.
    // Deliberately unlit: the glass IS a light source, so torches must not shade it. Do not
    // put this on stonework, which needs ToonLit's banded lighting to belong to the world (§6).
    //
    // THREE THINGS MAKE IT READ AS GLASS RATHER THAN A GLOWING STICKER:
    //  1. The glow MASK is separated from the glow COLOUR, so intensity can be pushed high
    //     enough to bloom without washing every texel to white (see _MaskThreshold).
    //  2. Per-window variation and flicker derived from WORLD POSITION, so a corridor of
    //     identical windows stops reading as cloned and each looks independently backlit.
    //  3. A DepthOnly pass, so the pane exists in the depth texture that ToonWater and
    //     GroundFog sample.
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)

        [Header(Emission)]
        [HDR]_EmissionColor("Emission Tint", Color) = (1,1,1,1)
        _EmissionIntensity("Emission Intensity", Float) = 1

        [Header(Glow mask)]
        _EmissionMap("Emission Mask (R)", 2D) = "white" {}
        _MaskFromLuminance("Mask From Base Luminance", Range(0,1)) = 1
        _MaskThreshold("Mask Threshold", Range(0,1)) = 0.25
        _MaskSoftness("Mask Softness", Range(0.001,1)) = 0.35

        [Header(Glass shaping)]
        _FresnelPower("Grazing Falloff", Range(0.5,8)) = 3
        _FresnelBoost("Grazing Boost", Range(1,4)) = 1.8

        [Header(Distance)]
        _GlowFadeDistance("Glow Fade Distance (0 = off)", Float) = 0
        _GlowFadeFloor("Glow Fade Floor", Range(0,1)) = 0.35

        [Header(Per window variation)]
        _VariationAmount("Brightness Variation", Range(0,1)) = 0.15
        _FlickerAmount("Flicker Amount", Range(0,1)) = 0.08
        _FlickerSpeed("Flicker Speed", Float) = 1.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Opaque"
            "Queue"="Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // MANDATORY for anything the dungeon kit draws. Kit and prop geometry goes through
        // Graphics.RenderMeshInstanced, where each instance's transform lives in an instancing
        // buffer that only UNITY_SETUP_INSTANCE_ID reads — TransformObjectToHClip otherwise
        // uses whatever single unity_ObjectToWorld is bound, so every instance lands on top of
        // one another instead of at its own wall.
        //
        // IT FAILS SILENTLY: InstancedDungeonRenderer force-sets enableInstancing on every
        // material it harvests, which satisfies Unity's runtime check and suppresses the
        // "material does not support instancing" error. The symptom is a submesh that simply
        // isn't where it should be — here, a stained-glass pane that never glowed because it
        // was never drawn at the wall at all.
        #pragma multi_compile_instancing

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseColor;
            float4 _EmissionColor;
            float _EmissionIntensity;
            float _MaskFromLuminance;
            float _MaskThreshold;
            float _MaskSoftness;
            float _FresnelPower;
            float _FresnelBoost;
            float _GlowFadeDistance;
            float _GlowFadeFloor;
            float _VariationAmount;
            float _FlickerAmount;
            float _FlickerSpeed;
        CBUFFER_END

        // Cheap stable hash, same one InteriorMapped uses. Fed this instance's WORLD ORIGIN so
        // two windows in different walls vary independently while one window is stable frame to
        // frame — derived variation rather than supplied, which is what lets it survive batching
        // (there is no per-instance channel to put a value in, §5).
        float Hash21(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        // Per-instance brightness offset + flicker phase, resolved once in the vertex stage
        // (it is constant across the pane, so computing it per pixel would be waste).
        float InstanceGlow()
        {
            float3 originWS = TransformObjectToWorld(float3(0, 0, 0));
            float h = Hash21(originWS.xz);

            // Variation is CENTRED on 1: half the windows read dimmer, half brighter, and the
            // average brightness is unchanged — so tuning _EmissionIntensity still means what
            // it says rather than drifting as variation is dialled in.
            float variation = 1.0 + (h - 0.5) * 2.0 * _VariationAmount;

            // THREE SINES AT INCOMMENSURATE FREQUENCIES, the same fBm-style layering NpcFace
            // uses for jaw and brow — a single sine reads as a mechanical pulse, and the ear
            // and eye both pick up its period within seconds. Ratios (1, 1.7, 3.1) are
            // deliberately not small integers: the composite only repeats at their common
            // multiple, which is long enough never to be seen.
            //
            // The PHASE OFFSET IS SCALED PER OCTAVE (h, 2.3h, 4.7h) rather than shared. With
            // one offset every window would show the IDENTICAL waveform merely time-shifted,
            // which is visible as soon as two windows are on screen together; scaling it makes
            // each window's flicker genuinely its own shape.
            //
            // Weights sum to 1, so the result stays within [-1, 1] and _FlickerAmount keeps
            // meaning "peak deviation" rather than drifting as octaves are retuned.
            float t = _Time.y * _FlickerSpeed;
            float phase = h * 6.2831853;
            float wave = sin(t        + phase      ) * 0.60
                       + sin(t * 1.7  + phase * 2.3) * 0.30
                       + sin(t * 3.1  + phase * 4.7) * 0.10;

            // Slow and shallow by default: this should imply something burning BEHIND the
            // glass, not make the glass itself look like a flame.
            float flicker = 1.0 + wave * _FlickerAmount;
            return variation * flicker;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"

            Tags
            {
                "LightMode"="UniversalForward"
            }

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            // FOG IS NOT AUTOMATIC — a shader that omits it is the one surface in the dungeon
            // that never recedes, so it reads as a UI decal pasted over the world at distance
            // while looking perfect up close. It matters more here than anywhere else: fog
            // colour is DRIVEN per room by DungeonFogController from the torch palette (§7),
            // so ignoring it also opts out of the room's whole atmosphere. Same three-part
            // shape ToonLit uses — pragma, vertex factor, MixFog on the final colour.
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float glow : TEXCOORD1;   // per-instance brightness, constant across the pane
                float fogFactor : TEXCOORD2;
                float3 normalWS : TEXCOORD3;
                float3 positionWS : TEXCOORD4;  // per-pixel view dir; a flat pane has one normal
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                // SETUP must precede any use of unity_ObjectToWorld — that is what points the
                // matrix at THIS instance, and InstanceGlow reads it too.
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv = IN.uv;
                OUT.glow = InstanceGlow();
                OUT.fogFactor = ComputeFogFactor(OUT.positionHCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half3 baseColor = baseTex.rgb * _BaseColor.rgb;

                // WHERE it glows. Derived from base LUMINANCE by default, so a stained-glass
                // window needs no second authored texture and the lead came stays dark for
                // free (it is dark in the base map). Assign _EmissionMap and drop
                // _MaskFromLuminance to 0 to drive it explicitly instead; the two blend, so a
                // painted mask can be mixed with the luminance rather than replacing it.
                half lum = dot(baseTex.rgb, half3(0.2126, 0.7152, 0.0722));
                half painted = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).r;
                half mask = lerp(painted, lum, _MaskFromLuminance);

                // REMAPPED, not used raw. Multiplying the whole map by a large intensity drives
                // every non-black texel to white and the glass loses its colour exactly where it
                // is brightest — the same saturation that made every candle palette read white
                // at 25x. Thresholding means intensity can go high enough to bloom hard while
                // only the GLASS saturates.
                mask = smoothstep(_MaskThreshold, _MaskThreshold + _MaskSoftness, mask);

                // COLOUR comes from the base texture, so the window glows in its own colours.
                // GRAZING BOOST. Real glass brightens toward its silhouette, and this is the
                // main cue separating a pane from a glowing sticker. NB the view direction is
                // computed PER PIXEL — a flat pane has a single normal, so a vertex-stage
                // fresnel would be constant across it and do nothing at all.
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));
                half fres = pow(1.0 - saturate(dot(N, V)), _FresnelPower);
                half shaping = lerp(1.0, _FresnelBoost, fres);

                // DISTANCE FADE, off by default (_GlowFadeDistance 0). Fog dims the surface but
                // BLOOM IS POST-PROCESS and runs after fog, so a bright HDR pane keeps blooming
                // however thick the haze — which is the other half of reading as a decal from
                // across a room. This attenuates the emission itself, which bloom then sees.
                half fade = 1.0;
                if (_GlowFadeDistance > 0.001)
                {
                    half d = length(_WorldSpaceCameraPos - IN.positionWS) / _GlowFadeDistance;
                    fade = lerp(1.0, _GlowFadeFloor, saturate(d));
                }

                half3 emission = baseColor * _EmissionColor.rgb * mask
                                 * _EmissionIntensity * IN.glow * shaping * fade;

                // Fog applied to the FINAL colour, emission included. Fogging only the base
                // would leave the glow punching through undimmed, which is the decal look
                // again in a subtler form — a distant window would sit in haze with a
                // crisp bright pane inside it.
                half3 color = MixFog(baseColor + emission, IN.fogFactor);
                return half4(color, baseTex.a);
            }

            ENDHLSL
        }

        // Writes to the URP DEPTH TEXTURE. Without this the pane is absent from it, and
        // ToonWater's depth ramp and GroundFog's soft-particle fade both see straight through a
        // window (§6). Costs nothing visually — colour is masked off.
        // NB: no DepthNormals pass. Add one if SSAO is ever enabled, or the glass will be the
        // one surface with no ambient occlusion around it.
        Pass
        {
            Name "DepthOnly"

            Tags
            {
                "LightMode"="DepthOnly"
            }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM

            #pragma vertex depthVert
            #pragma fragment depthFrag

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            DepthVaryings depthVert(DepthAttributes IN)
            {
                DepthVaryings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 depthFrag(DepthVaryings IN) : SV_Target
            {
                return 0;
            }

            ENDHLSL
        }
    }
}
