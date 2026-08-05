Shader "Custom/URP/TextureEmission"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)

        _EmissionMap("Emission Map", 2D) = "black" {}
        [HDR]_EmissionColor("Emission Tint", Color) = (1,1,1,1)
        _EmissionIntensity("Emission Intensity", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Opaque"
            "Queue"="Geometry"
        }

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

            // MANDATORY for anything the dungeon kit draws. Kit and prop geometry goes through
            // Graphics.RenderMeshInstanced, where each instance's transform lives in an
            // instancing buffer that only UNITY_SETUP_INSTANCE_ID reads — TransformObjectToHClip
            // otherwise uses whatever single unity_ObjectToWorld is bound, so every instance
            // lands on top of one another instead of at its own wall.
            //
            // IT FAILS SILENTLY: InstancedDungeonRenderer force-sets enableInstancing on every
            // material it harvests, which satisfies Unity's runtime check and suppresses the
            // "material does not support instancing" error. The symptom is a submesh that
            // simply isn't where it should be — here, a stained-glass pane that never glowed
            // because it was never drawn at the wall at all.
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _EmissionColor;
                float _EmissionIntensity;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                // SETUP must precede any use of unity_ObjectToWorld — that is what points the
                // matrix at THIS instance. TRANSFER carries the id to the fragment stage.
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half3 baseColor = baseTex.rgb * _BaseColor.rgb;

                half3 emissiveTex = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).rgb;

                half3 emission =
                    emissiveTex *
                    _EmissionColor.rgb *
                    _EmissionIntensity;

                return half4(baseColor + emission, baseTex.a);
            }

            ENDHLSL
        }
    }
}