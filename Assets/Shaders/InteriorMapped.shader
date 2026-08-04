Shader "Dungeon/InteriorMapped"
{
    // INTERIOR MAPPING (van Dongen 2008) — a flat quad that appears to contain a deep room.
    //
    // The fragment shader casts the view ray into a VIRTUAL box behind the surface and finds
    // which of its walls the ray hits, so the space costs no geometry and — the whole reason
    // this exists — cannot protrude into the level above, the level below, or through a wall.
    // A real 3D cell behind a floor grate would punch straight through whatever is beneath it.
    //
    // PURELY THE INTERIOR. The grate/bars are real geometry sitting in front of this plane, so
    // there is no bar layer here: the quad is the fake room and nothing else. Set the plane
    // back inside the opening, behind whatever grate the kit places.
    //
    // Fitted to this project rather than generic:
    //  - Shares ToonLit's conventions (_ShadowTint ambient floor, Ramp() banding, Forward+
    //    LIGHT_LOOP) so an interior does not read as pasted in from another game.
    //  - The interior samples the SAME wall/floor textures the dungeon already uses, so it
    //    matches its surroundings with no new art.
    //  - Variation comes from hashing WORLD POSITION inside the shader, not from a
    //    MaterialPropertyBlock. That is not a style choice: kit pieces render through
    //    Graphics.RenderMeshInstanced, which has no per-renderer block to write to (§5), so
    //    procedural variation is the only kind available on the instanced path.
    Properties
    {
        [Header(Interior)]
        _InteriorWallMap ("Interior Walls", 2D) = "gray" {}
        _InteriorFloorMap ("Interior Floor and Ceiling", 2D) = "gray" {}
        _InteriorColor ("Interior Tint", Color) = (0.55, 0.52, 0.48, 1)
        _InteriorDepth ("Depth in tile widths", Range(0.05, 6)) = 2
        _InteriorTiling ("Rooms Across the Tile XY", Vector) = (1, 1, 0, 0)
        _DepthFalloff ("Darken With Depth", Range(0, 4)) = 1.6
        _InteriorTexScale ("Interior Texture Scale", Range(0.1, 8)) = 1

        [Header(Variation)]
        _VariationAmount ("Per Room Variation", Range(0, 1)) = 0.6
        _DepthVariation ("Depth Variation", Range(0, 1)) = 0.35

        [Header(Lighting)]
        _ShadowTint ("Shadow and Ambient Tint", Color) = (0.50, 0.5, 0.5, 1)
        _Bands ("Light Bands", Range(1, 6)) = 2
        _BandSoftness ("Band Softness", Range(0.01, 1)) = 1.0
        _LightBleed ("Light Bleed Into Interior", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            // Forward+ / clustered lighting — keyword name varies by URP version, so both are
            // declared. Forward+ is REQUIRED by this project (§6): plain Forward caps
            // additional lights per object and starves the big instanced batch of torchlight.
            #pragma multi_compile_fragment _ _FORWARD_PLUS _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float  fogFactor  : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_InteriorWallMap);  SAMPLER(sampler_InteriorWallMap);
            TEXTURE2D(_InteriorFloorMap);

            CBUFFER_START(UnityPerMaterial)
                half4  _InteriorColor;
                half   _InteriorDepth;
                float4 _InteriorTiling;
                half   _DepthFalloff;
                half   _InteriorTexScale;
                half   _VariationAmount;
                half   _DepthVariation;
                half4  _ShadowTint;
                half   _Bands;
                half   _BandSoftness;
                half   _LightBleed;
            CBUFFER_END

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

            // Cheap stable hash. Fed WORLD-SPACE room coordinates so two grates in different
            // places differ while the SAME grate stays identical from frame to frame and from
            // any viewing angle — a per-fragment or per-frame random would boil.
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.tangentWS = float4(TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w);
                o.uv = input.uv;
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half3 tangentWS = normalize(input.tangentWS.xyz);
                half3 normalWS = normalize(input.normalWS);
                half3 bitangentWS = input.tangentWS.w * cross(normalWS, tangentWS);
                half3x3 tbn = half3x3(tangentWS, bitangentWS, normalWS);
                half3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));

                // ---------------- Interior ray cast ----------------
                //
                // Everything happens in TANGENT space, which is what makes one shader work on
                // both a wall grate (looking sideways into a cell) and a floor grate (looking
                // down a shaft) with no orientation setting: the mesh's own tangent frame says
                // which way "into the surface" is. It also means the MESH MUST HAVE TANGENTS —
                // without them the ray points somewhere arbitrary and the interior swims.
                float3 viewTS = normalize(mul(tbn, viewDir));   // surface -> eye
                float3 rd = -viewTS;                            // eye -> into the wall, rd.z < 0

                // Subdivide the tile into rooms so one plane can hold several small cells.
                float2 tiling = max(_InteriorTiling.xy, float2(0.001, 0.001));
                float2 roomUV = frac(input.uv * tiling);
                float2 roomId = floor(input.uv * tiling);

                // Room identity in WORLD space, so two planes side by side get different
                // interiors while one plane stays stable. Quantised so float drift on a large
                // map cannot flip a room's identity mid-surface.
                float2 worldId = roomId + floor(input.positionWS.xz * 0.5 + 0.25);
                float h = Hash21(worldId);

                float depth = _InteriorDepth * (1.0 + (h - 0.5) * 2.0 * _DepthVariation);
                depth = max(depth, 0.02);

                // Ray-box: origin on the front face at (roomUV, 0); box spans x,y in [0,1] and
                // z in [-depth, 0].
                float3 ro = float3(roomUV, 0.0);
                // A ray exactly parallel to an axis makes that divide infinite — correct in
                // meaning (it never hits that plane) but it must not become a NaN, which would
                // poison the min() below and blow out the whole fragment. Nudge each component
                // off zero while KEEPING ITS SIGN, since the sign is what selects near vs far
                // plane. Note sign(0) is 0, so it cannot be used for this.
                float3 s = rd >= 0.0 ? 1.0 : -1.0;
                float3 inv = 1.0 / (s * max(abs(rd), 1e-5));

                float tx = ((rd.x > 0.0 ? 1.0 : 0.0) - ro.x) * inv.x;
                float ty = ((rd.y > 0.0 ? 1.0 : 0.0) - ro.y) * inv.y;
                float tz = (-depth) * inv.z;
                tx = tx <= 0.0 ? 1e6 : tx;
                ty = ty <= 0.0 ? 1e6 : ty;
                tz = tz <= 0.0 ? 1e6 : tz;

                float t = min(min(tx, ty), tz);
                float3 hit = ro + rd * t;

                // Which face, and the UV on it. Depth runs 0..1 from the opening inward so a
                // texture on a side wall recedes correctly instead of stretching.
                float depth01 = saturate(-hit.z / depth);
                float2 iuv;
                bool isFloorOrCeil = (t == ty);
                if (t == tz)      iuv = hit.xy;                    // back wall
                else if (t == tx) iuv = float2(hit.y, depth01);    // side wall
                else              iuv = float2(hit.x, depth01);    // floor / ceiling

                // Per-room texture offset and optional axis swap, so neighbouring cells do not
                // read as copies of each other.
                float2 varOffset = float2(h, Hash21(worldId + 17.0)) * _VariationAmount;
                if (_VariationAmount > 0.001 && Hash21(worldId + 71.0) > 0.5) iuv = iuv.yx;
                iuv = iuv * _InteriorTexScale + varOffset;

                half3 interior = isFloorOrCeil
                    ? SAMPLE_TEXTURE2D(_InteriorFloorMap, sampler_InteriorWallMap, iuv).rgb
                    : SAMPLE_TEXTURE2D(_InteriorWallMap, sampler_InteriorWallMap, iuv).rgb;
                interior *= _InteriorColor.rgb;

                // Darken with distance travelled. This does most of the work selling the
                // depth — more than the texture does, for a space glimpsed through bars.
                interior *= exp(-t * _DepthFalloff);

                // ---------------- Lighting ----------------
                half3 diffuse = _ShadowTint.rgb * (0.5h + 0.5h * SampleSH(normalWS));

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                diffuse += mainLight.color * Ramp(saturate(dot(normalWS, mainLight.direction))
                                                  * mainLight.distanceAttenuation)
                           * smoothstep(0.25, 0.45, mainLight.shadowAttenuation);

                #if defined(_ADDITIONAL_LIGHTS) || defined(_FORWARD_PLUS) || defined(_CLUSTER_LIGHT_LOOP)
                // The macros read `inputData` BY NAME, so it must exist with these fields.
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDir;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                    diffuse += light.color * Ramp(saturate(dot(normalWS, light.direction))
                                                  * light.distanceAttenuation);
                LIGHT_LOOP_END
                #endif

                // The interior stays mostly self-contained — a sealed cell is not really lit by
                // the corridor — but takes SOME of the surface's light so standing beside it
                // with a torch doesn't leave a flat black patch on a lit wall.
                half3 color = interior * lerp(half3(1, 1, 1), diffuse, _LightBleed);

                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }

    // ShadowCaster / DepthOnly / DepthNormals come from the fallback. That gives exactly the
    // right behaviour: shadows and depth describe the FLAT QUAD, not the virtual interior. The
    // plane must not cast a room-shaped shadow, and the depth buffer has to agree with the real
    // geometry or anything sampling it — the ground fog's soft particles (§6) — reads a hole
    // that isn't there.
    FallBack "Universal Render Pipeline/Lit"
}
