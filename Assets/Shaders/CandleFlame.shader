Shader "DungeonGen/Candle Emission Flicker"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Texture", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)

        [HDR] _EmissionColor("Emission Color", Color) = (1, 0.25, 0.02, 1)

        _MinEmission("Minimum Emission", Range(0, 20)) = 2.5
        _MaxEmission("Maximum Emission", Range(0, 20)) = 4.0

        _FlickerSpeed("Flicker Speed", Range(1, 40)) = 18
        _FlickerSmoothness("Flicker Smoothness", Range(0.01, 1)) = 0.65
        _BrightnessBias("Brightness Bias", Range(0.1, 5)) = 2.5

        [Toggle] _UseAlphaClip("Use Alpha Clipping", Float) = 0
        _AlphaCutoff("Alpha Cutoff", Range(0, 1)) = 0.1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "CandleEmissionFlicker"

            Tags
            {
                "LightMode" = "UniversalForward"
            }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            #pragma shader_feature_local _USEALPHACLIP_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _EmissionColor;

                float _MinEmission;
                float _MaxEmission;
                float _FlickerSpeed;
                float _FlickerSmoothness;
                float _BrightnessBias;

                float _UseAlphaClip;
                float _AlphaCutoff;
            CBUFFER_END

            /*
             * Returns a repeatable pseudo-random value from 0 to 1.
             */
            float Hash(float value)
            {
                return frac(sin(value * 12.9898) * 43758.5453);
            }

            /*
             * Produces smoothly interpolated random noise.
             *
             * Unlike a sine wave, this does not create an obvious,
             * repeating bright-dim-bright pattern.
             */
            float SmoothRandomNoise(float timeValue)
            {
                float whole = floor(timeValue);
                float fraction = frac(timeValue);

                float currentValue = Hash(whole);
                float nextValue = Hash(whole + 1.0);

                float smoothFraction =
                    fraction * fraction * (3.0 - 2.0 * fraction);

                return lerp(
                    currentValue,
                    nextValue,
                    smoothFraction
                );
            }

            float CandleFlicker(float timeValue)
            {
                /*
                 * Primary candle fluctuation.
                 */
                float primaryNoise =
                    SmoothRandomNoise(timeValue * _FlickerSpeed);

                /*
                 * Add a slower noise layer so the flame occasionally
                 * remains brighter or dimmer for a little longer.
                 */
                float slowNoise =
                    SmoothRandomNoise(
                        timeValue * _FlickerSpeed * 0.23 + 17.4
                    );

                /*
                 * Add a small, faster layer for tiny rapid fluctuations.
                 */
                float fastNoise =
                    SmoothRandomNoise(
                        timeValue * _FlickerSpeed * 2.7 + 41.8
                    );

                float combinedNoise =
                    primaryNoise * 0.65 +
                    slowNoise * 0.25 +
                    fastNoise * 0.10;

                /*
                 * BrightnessBias above 1 keeps the flame near its normal
                 * brightness while allowing occasional dips.
                 */
                combinedNoise = pow(
                    saturate(combinedNoise),
                    1.0 / max(_BrightnessBias, 0.001)
                );

                /*
                 * Blend toward 0.5 to reduce sharp changes.
                 *
                 * Higher FlickerSmoothness values produce gentler flicker.
                 */
                combinedNoise = lerp(
                    combinedNoise,
                    0.5,
                    _FlickerSmoothness * 0.35
                );

                return saturate(combinedNoise);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs positionInputs =
                    GetVertexPositionInputs(input.positionOS.xyz);

                output.positionCS = positionInputs.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 textureColor =
                    SAMPLE_TEXTURE2D(
                        _BaseMap,
                        sampler_BaseMap,
                        input.uv
                    );

                half4 baseColor = textureColor * _BaseColor;

                if (_UseAlphaClip > 0.5)
                {
                    clip(baseColor.a - _AlphaCutoff);
                }

                float flicker = CandleFlicker(_Time.y);

                float emissionIntensity = lerp(
                    _MinEmission,
                    _MaxEmission,
                    flicker
                );

                half3 emission =
                    _EmissionColor.rgb *
                    baseColor.rgb *
                    emissionIntensity;

                /*
                 * Because this is unlit, the visible result is the base
                 * color plus the flickering emission.
                 */
                half3 finalColor =
                    baseColor.rgb + emission;

                return half4(finalColor, baseColor.a);
            }

            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"

            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM

            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _EmissionColor;

                float _MinEmission;
                float _MaxEmission;
                float _FlickerSpeed;
                float _FlickerSmoothness;
                float _BrightnessBias;

                float _UseAlphaClip;
                float _AlphaCutoff;
            CBUFFER_END

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output;

                VertexPositionInputs positionInputs =
                    GetVertexPositionInputs(input.positionOS.xyz);

                output.positionCS = positionInputs.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                if (_UseAlphaClip > 0.5)
                {
                    half4 textureColor =
                        SAMPLE_TEXTURE2D(
                            _BaseMap,
                            sampler_BaseMap,
                            input.uv
                        );

                    half alpha =
                        textureColor.a * _BaseColor.a;

                    clip(alpha - _AlphaCutoff);
                }

                return 0;
            }

            ENDHLSL
        }
    }
}