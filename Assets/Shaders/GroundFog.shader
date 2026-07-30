// Stylized ground fog for CAMERA-FACING BILLBOARDS (a ParticleSystem), the
// no-compute, no-raymarch alternative to a volumetric sim.
//
// It was floor-parallel planes first, and that failed for a structural reason
// worth recording: a plane hugging the floor has only the floor behind it — a
// fixed ~20cm — so a soft-particle fade has no depth range to work across and
// every intersection with a wall, crate or goblin stays a hard line at ANY
// setting. Viewed from eye height the stacked planes also read as a pool, since
// looking edge-on accumulates every layer's alpha at once. Billboards fix both by
// construction: they always meet geometry at a grazing angle, and they have no
// edge-on orientation to become a waterline.
//
// Density still comes from procedural noise sampled in WORLD SPACE, so patches
// stay anchored to the dungeon rather than travelling with the particles.
//
// Shares ToonLit's lighting: the same Ramp() banding, the same _ShadowTint
// ambient floor, the same Forward+ LIGHT_LOOP. That's the whole reason to write
// this rather than use a stock fog plane — with no directional light in the
// dungeon, unlit fog reads as a flat grey sheet laid over a warmly banded scene,
// while this catches torch pools and belongs.
//
// Noise is generated in-shader (2-octave value noise), so there's no texture to
// author or assign.
//
// REQUIRES: Depth Texture on the URP Asset — used for the SOFT intersection that
// stops the plane cutting a hard line through walls, props and the floor. Set
// _DepthFade to 0 for hard edges if depth is ever unavailable.
Shader "Dungeon/GroundFog"
{
    Properties
    {
        [Header(Color)]
        _Tint ("Tint", Color) = (0.55, 0.60, 0.66, 1)
        _MaxAlpha ("Max Opacity", Range(0, 1)) = 0.5

        [Header(Density)]
        // Coverage is a THRESHOLD on the noise, not a multiplier: raising it eats
        // into the field so fog sits in patches with clear floor between, which
        // reads far better than a uniform veil at low opacity.
        _Coverage ("Coverage", Range(0, 1)) = 0.45
        _Edge ("Patch Edge Softness", Range(0.01, 0.6)) = 0.28
        _NoiseScaleA ("Noise Scale A", Range(0.005, 1)) = 0.06
        _NoiseScaleB ("Noise Scale B", Range(0.005, 1)) = 0.15
        _NoiseSpeedA ("Noise Speed A", Vector) = (0.02, 0.013, 0, 0)
        _NoiseSpeedB ("Noise Speed B", Vector) = (-0.031, 0.022, 0, 0)

        [Header(Motion)]
        // DOMAIN WARP is what makes fog look like fog. Scrolling a noise field linearly
        // reads as a texture sliding under you; warping WHERE it's sampled by a second,
        // slower noise makes the patches curl, stretch and fold in place — churning
        // rather than travelling. Costs two extra noise taps.
        _WarpStrength ("Swirl Strength (m)", Range(0, 20)) = 6
        _WarpScale ("Swirl Scale", Range(0.002, 0.2)) = 0.02
        _WarpSpeed ("Swirl Speed", Vector) = (0.01, -0.014, 0, 0)
        // Per-layer multiplier on all drift, set by GroundFog so stacked layers move at
        // different rates — that difference is what reads as depth instead of one sheet.
        _DriftScale ("Drift Scale (per layer)", Float) = 1

        [Header(Fades)]
        // Softens where geometry RISES THROUGH the plane, measured in world metres and
        // only against surfaces above it. Replaces a view-ray depth fade, which was both
        // angle-dependent (a wide band looking along the plane, collapsing to a hard line
        // looking down) and in direct conflict with the FLOOR sitting only baseHeight
        // behind the fog.
        // Fades the fog as it closes on ANY geometry — walls, NPCs, props alike.
        //
        // Measured as a FRACTION of the gap open floor would give at this view angle,
        // which is what makes it work at all. A raw metre threshold cannot: the floor sits
        // only _FloorGap behind the fog, so any width wide enough to soften a wall also
        // erases the fog over open ground, and the band's screen width swings wildly with
        // view angle. Normalising by the expected floor gap removes both problems — over
        // open floor the ratio is ~1 whatever the angle, and only genuine geometry drives
        // it toward 0.
        //
        // Soft particles. Works properly on a CAMERA-FACING quad, which is the whole
        // reason this is a billboard shader: the quad sits at roughly constant depth
        // while whatever is behind it recedes, so the gap ramps over a wide screen region.
        // On the old floor-parallel plane the only thing behind the fog was the floor, a
        // fixed ~20cm away, leaving no depth range to fade across — which is why contacts
        // stayed hard at every setting.
        _SoftFade ("Soft Intersection (m)", Range(0, 6)) = 1.6
        // Radial falloff of each billboard, so the quads have no hard edges of their own.
        // Higher = softer, wispier blobs; low values start showing the quad's circle.
        _BlobEdge ("Blob Edge Softness", Range(0.05, 1)) = 0.75
        _CameraFadeNear ("Camera Fade Near (m)", Range(0, 6)) = 0.6
        _CameraFadeFar ("Camera Fade Full (m)", Range(0, 12)) = 2.5
        // Driven by the GroundFog component — faded to 0 while the stack changes floors
        // so the vertical move is never seen.
        _Opacity ("Opacity (driven)", Range(0, 1)) = 1
        [Header(Toon Bands)]
        _Bands ("Light Bands", Range(1, 6)) = 2
        _BandSoftness ("Band Softness", Range(0.01, 1)) = 1.0

        [Header(Darkness)]
        _ShadowTint ("Shadow / Ambient Tint", Color) = (0.5, 0.5, 0.5, 1)
        // Ceiling on accumulated light. Every torch and candle adds to the fog's diffuse,
        // and in a room with a dozen of them the sum runs far past white — the fog then
        // glows regardless of how low Max Opacity is, which reads as a bright band rather
        // than as mist. Opaque surfaces get away with this because their albedo is dark;
        // fog has no albedo to hide behind.
        _MaxLight ("Max Light", Range(0.2, 4)) = 1.1

        [Header(Per layer (set by GroundFog))]
        // Offsets each stacked plane into a different part of the noise field, so
        // layers don't stack into one solid slab.
        _LayerPhase ("Layer Phase", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off          // visible from below too — you can walk onto a balcony above it

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _FORWARD_PLUS _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _Tint;
                half   _MaxAlpha;
                half   _Coverage;
                half   _Edge;
                half   _NoiseScaleA;
                half   _NoiseScaleB;
                float4 _NoiseSpeedA;
                float4 _NoiseSpeedB;
                half   _WarpStrength;
                half   _WarpScale;
                float4 _WarpSpeed;
                float  _DriftScale;
                half   _SoftFade;
                half   _BlobEdge;
                half   _CameraFadeNear;
                half   _CameraFadeFar;
                half   _Opacity;
                half   _Bands;
                half   _BandSoftness;
                half4  _ShadowTint;
                half   _MaxLight;
                float  _LayerPhase;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;   // quad UV — drives the round blob falloff
                float4 color      : COLOR;       // per-particle colour/alpha from the system
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
                float  fogFactor  : TEXCOORD2;
                float2 uv         : TEXCOORD3;
                float4 color      : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // Identical to ToonLit's, so fog bands the same way the walls behind it do.
            half Ramp(half x)
            {
                x = saturate(x);
                half q = x * _Bands;
                half f = floor(q);
                half r = q - f;
                half soft = max(_BandSoftness, 0.01);
                half edge = smoothstep(1.0 - soft, 1.0, r);
                return saturate((f + edge) / _Bands);
            }

            float Hash21(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
            }

            /// Value noise with a smoothstep'd lattice. Cheap, and enough for fog —
            /// nobody reads the individual octaves of a mist patch.
            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.screenPos = ComputeScreenPos(pos.positionCS);
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                o.uv = input.uv;
                o.color = input.color;
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // --- Density from WORLD XZ. This is the trick that lets the planes chase
                // the camera without the fog sliding: the field is anchored to the world,
                // so moving the mesh through it changes nothing about what's sampled.
                float2 w = input.positionWS.xz + _LayerPhase;
                float t = _Time.y * _DriftScale;

                // DOMAIN WARP: displace the sample position by a slow, coarse noise before
                // reading density. Two taps, and it's the difference between a texture
                // sliding past and vapour actually curling in place — the patches fold and
                // stretch rather than rigidly translating.
                if (_WarpStrength > 0.001h)
                {
                    float2 wu = w * _WarpScale + t * _WarpSpeed.xy;
                    float2 warp = float2(ValueNoise(wu), ValueNoise(wu + 37.21)) * 2.0 - 1.0;
                    w += warp * _WarpStrength;
                }

                float nA = ValueNoise(w * _NoiseScaleA + t * _NoiseSpeedA.xy);
                float nB = ValueNoise(w * _NoiseScaleB + t * _NoiseSpeedB.xy);
                half n = nA * 0.65h + nB * 0.35h;

                // Threshold, not scale — see _Coverage. Patches with clear floor between
                // read as fog; a uniform low-alpha wash reads as a dirty lens.
                half density = smoothstep(_Coverage - _Edge, _Coverage + _Edge, n);

                // --- Round soft blob from the quad's own UV, so a billboard has no straight
                // edges of its own. No texture needed.
                half radial = 1.0h - saturate(length(input.uv - 0.5h) * 2.0h);
                density *= smoothstep(0.0h, _BlobEdge, radial);

                // --- Per-particle alpha and colour (size-over-lifetime, fade-in/out, and
                // any Color over Lifetime authored on the system).
                density *= input.color.a;

                // --- SOFT PARTICLES. Now that the quad faces the CAMERA, this finally
                // works the way it's supposed to.
                //
                // On the old floor-parallel plane the only thing behind the fog was the
                // floor, a fixed ~20cm away, so there was no depth range to fade across and
                // every contact stayed a hard line however the fade was tuned. A
                // camera-facing quad sits at roughly constant depth while the wall, crate or
                // goblin behind it recedes, so the gap ramps smoothly over a wide screen
                // region — the fade has somewhere to work.
                float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                if (_SoftFade > 0.001h)
                {
                    float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                    float gap = max(sceneEye - input.screenPos.w, 0);
                    density *= saturate(gap / _SoftFade);
                }

                // --- Fade out close to the camera, so you never see the plane edge-on as
                // a hard horizontal line through the view when you walk into it.
                float camDist = distance(_WorldSpaceCameraPos, input.positionWS);
                density *= smoothstep(_CameraFadeNear, max(_CameraFadeFar, _CameraFadeNear + 0.01h), camDist);

                if (density <= 0.001h) return half4(0, 0, 0, 0);

                // --- Lighting. Fog has no real normal, so shade against UP: torchlight
                // pools on it from above and it bands like every other surface.
                half3 normalWS = half3(0, 1, 0);
                half3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));
                half3 diffuse = _ShadowTint.rgb * (0.5h + 0.5h * SampleSH(normalWS));

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                diffuse += mainLight.color * Ramp(saturate(dot(normalWS, mainLight.direction))
                                                 * mainLight.distanceAttenuation);

                #if defined(_ADDITIONAL_LIGHTS) || defined(_FORWARD_PLUS) || defined(_CLUSTER_LIGHT_LOOP)
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDir;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                    // Fog is diffuse only — a specular glint off mist is nonsense.
                    diffuse += light.color * Ramp(light.distanceAttenuation);
                LIGHT_LOOP_END
                #endif

                // Clamp before tinting — see _MaxLight. Without this the fog is brighter
                // than the walls around it and Max Opacity stops meaning anything.
                diffuse = min(diffuse, _MaxLight.xxx);

                half3 color = _Tint.rgb * input.color.rgb * diffuse;
                color = MixFog(color, input.fogFactor);
                return half4(color, saturate(density * _MaxAlpha * _Opacity));
            }
            ENDHLSL
        }
    }

    FallBack Off
}
