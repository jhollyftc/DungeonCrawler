// Dungeon toon-lit shader for URP — v2.
// Banded (cel) diffuse for main + additional lights (torches band into
// stepped pools), banded specular glints, tinted ambient floor, and an
// inverted-hull black outline pass. Textures pass through untouched.
// GPU-instancing ready (works with the InstancedKit renderer); casts and
// receives shadows.
Shader "Dungeon/ToonLit"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        // Packed PBR mask: G = roughness, B = metallic. Modulates the toon
        // glint per pixel (rough = matte, metal glints tinted by albedo).
        // Default black = uniform glint, identical to having no mask.
        _MaskMap ("Mask (G=Roughness, B=Metallic)", 2D) = "black" {}
        // Default "bump" = flat normal: identical to having no map assigned.
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Strength", Range(0, 2)) = 1

        [Header(Toon Bands)]
        _Bands ("Light Bands", Range(1, 6)) = 2
        _BandSoftness ("Band Softness", Range(0.01, 1)) = 1.0

        [Header(Darkness)]
        _ShadowTint ("Shadow / Ambient Tint", Color) = (0.50, 0.5, 0.5, 1)

        [Header(Specular Glint)]
        _SpecColor ("Specular Color (black = off)", Color) = (0.75, 0.75, 0.75, 1)
        _SpecPower ("Specular Tightness", Range(4, 128)) = 24
        _SpecSoftness ("Specular Edge Softness", Range(0.005, 0.3)) = 0.25

        [Header(Outline)]
        _OutlineColor ("Outline Color", Color) = (0.02, 0.02, 0.03, 1)
        _OutlineWidth ("Outline Width (meters)", Range(0, 0.06)) = 0.015

        [Header(Rim (off by default))]
        _RimColor ("Rim Color", Color) = (1.0, 0.93, 0.80, 1)
        _RimAmount ("Rim Amount", Range(0, 1)) = 0.0
        _RimThreshold ("Rim Threshold", Range(0, 1)) = 0.72
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        TEXTURE2D(_MaskMap);
        TEXTURE2D(_BumpMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4  _BaseColor;
            half   _BumpScale;
            half   _Bands;
            half   _BandSoftness;
            half4  _ShadowTint;
            half4  _SpecColor;
            half   _SpecPower;
            half   _SpecSoftness;
            half4  _OutlineColor;
            half   _OutlineWidth;
            half4  _RimColor;
            half   _RimAmount;
            half   _RimThreshold;
        CBUFFER_END
        ENDHLSL

        // ---------------- Lit pass ----------------
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
            // Forward+ / clustered lighting (keyword name varies by URP version)
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
                float4 tangentWS  : TEXCOORD4; // xyz = tangent, w = bitangent sign
                float3 positionWS : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

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

            void ShadeLight(Light light, half3 normalWS, half3 viewDir,
                            half smoothness, half3 specTint,
                            inout half3 diffuse, inout half3 specular)
            {
                half ndl = saturate(dot(normalWS, light.direction));
                half banded = Ramp(ndl * light.distanceAttenuation);
                half shadowStep = smoothstep(0.25, 0.45, light.shadowAttenuation);
                half lit = banded * shadowStep;
                diffuse += light.color * lit;

                // Toon glint: banded Blinn-Phong, only where the light lands.
                // Per-pixel smoothness (from the mask) tightens the highlight
                // and gates its strength — rough pixels go fully matte.
                half power = lerp(8.0h, _SpecPower, smoothness);
                half3 h = SafeNormalize(light.direction + viewDir);
                half spec = pow(saturate(dot(normalWS, h)), power);
                half glint = smoothstep(0.5 - _SpecSoftness, 0.5 + _SpecSoftness, spec);
                specular += light.color * specTint * (glint * lit * smoothness);
            }

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.tangentWS = float4(TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb
                               * _BaseColor.rgb;
                // Per-pixel normal: tangent-space map applied over the
                // geometric normal. Flat default map = geometric normal.
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BaseMap, input.uv), _BumpScale);
                half3 bitangentWS = input.tangentWS.w
                    * cross(normalize(input.normalWS), normalize(input.tangentWS.xyz));
                half3 normalWS = normalize(TransformTangentToWorld(normalTS,
                    half3x3(normalize(input.tangentWS.xyz), bitangentWS, normalize(input.normalWS))));
                half3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));

                // Packed mask: G = roughness, B = metallic. Black default
                // (no mask assigned) = smoothness 1, metal 0 -> uniform glint,
                // same as before the mask existed.
                half4 mask = SAMPLE_TEXTURE2D(_MaskMap, sampler_BaseMap, input.uv);
                half smoothness = 1.0h - mask.g;
                half metal = mask.b;
                // Metals glint in their own color (gold glints gold) and a
                // touch stronger; dielectrics use the material's spec color.
                half3 specTint = lerp(_SpecColor.rgb, albedo, metal) * (1.0h + metal);

                half3 diffuse = _ShadowTint.rgb * (0.5h + 0.5h * SampleSH(normalWS));
                half3 specular = 0;

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                ShadeLight(mainLight, normalWS, viewDir, smoothness, specTint, diffuse, specular);

                // Portable additional-light loop: LIGHT_LOOP_BEGIN iterates the
                // per-object list in Forward and the screen-space clusters in
                // Forward+ (which is what removes the per-object light cap that
                // starves a giant instanced batch). The macros read `inputData`
                // by name, so it must exist with these fields.
                #if defined(_ADDITIONAL_LIGHTS) || defined(_FORWARD_PLUS) || defined(_CLUSTER_LIGHT_LOOP)
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDir;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                    ShadeLight(light, normalWS, viewDir, smoothness, specTint, diffuse, specular);
                LIGHT_LOOP_END
                #endif

                half3 color = albedo * diffuse + specular;

                if (_RimAmount > 0.001h)
                {
                    half rimDot = 1.0h - saturate(dot(viewDir, normalWS));
                    half rim = smoothstep(_RimThreshold - 0.04h, _RimThreshold + 0.04h, rimDot);
                    color += _RimColor.rgb * (rim * _RimAmount);
                }

                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---------------- Inverted-hull outline ----------------
        Pass
        {
            Name "Outline"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Front
            ZWrite On

            HLSLPROGRAM
            #pragma vertex OutlineVert
            #pragma fragment OutlineFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  fogFactor  : TEXCOORD0;
            };

            Varyings OutlineVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                positionWS += normalWS * _OutlineWidth;
                o.positionCS = TransformWorldToHClip(positionWS);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 OutlineFrag(Varyings input) : SV_Target
            {
                half3 color = MixFog(_OutlineColor.rgb, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---------------- Shadow casting ----------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDir));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = positionCS;
                return o;
            }

            half4 ShadowFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }

        // ---------------- Depth prepass ----------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return o;
            }

            half4 DepthFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}