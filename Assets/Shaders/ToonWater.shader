// Dungeon toon water for URP — companion to Dungeon/ToonLit.
//
// Deliberately shares ToonLit's lighting model rather than inventing its own: the
// same Ramp() banding, the same _ShadowTint ambient floor, and the same Forward+
// LIGHT_LOOP so TORCHES actually light it. That last point is the whole reason
// this isn't a stock water shader — the dungeon has NO directional light, so
// water lit any other way reads as a flat cutout while everything around it
// bands into torch pools.
//
// Surface motion comes from ONE normal map sampled twice at different scales,
// speeds and directions. Two layers beating against each other never visibly
// loop, which is the same trick NpcFace uses with incommensurate sines — and it
// costs one texture instead of two.
//
// Built for CONTAINED water (a well shaft, a fountain basin), not an ocean:
// depth fade and the foam line come from scene depth, which is what sells a well
// as deep rather than as a painted disc.
//
// REQUIRES: Depth Texture enabled on the URP Asset. Without it the depth-driven
// shallow/deep blend and the foam edge have nothing to read and the surface goes
// flat — set _DepthStrength to 0 for a deliberately flat look instead.
//
// No outline pass, unlike ToonLit: an inverted hull around a water plane draws a
// black ring on the surface. The BASIN gets the outline; the water inside doesn't.
Shader "Dungeon/ToonWater"
{
    Properties
    {
        [Header(Color)]
        _ShallowColor ("Shallow Color", Color) = (0.30, 0.62, 0.65, 0.55)
        _DeepColor ("Deep Color", Color) = (0.04, 0.13, 0.20, 0.95)
        [Tooltip] _DepthFade ("Depth Fade Distance (m)", Range(0.05, 8)) = 1.2
        _DepthStrength ("Depth Influence", Range(0, 1)) = 1

        [Header(Surface Motion)]
        _BumpMap ("Ripple Normal Map", 2D) = "bump" {}
        _BumpScale ("Ripple Strength", Range(0, 2)) = 0.55
        _RippleScaleA ("Ripple Scale A", Range(0.1, 20)) = 3
        _RippleScaleB ("Ripple Scale B", Range(0.1, 20)) = 5.5
        _RippleSpeedA ("Ripple Speed A", Vector) = (0.03, 0.02, 0, 0)
        _RippleSpeedB ("Ripple Speed B", Vector) = (-0.02, 0.035, 0, 0)

        [Header(Toon Bands)]
        _Bands ("Light Bands", Range(1, 6)) = 2
        _BandSoftness ("Band Softness", Range(0.01, 1)) = 1.0
        _ColorBands ("Depth Color Bands (0 = smooth)", Range(0, 8)) = 3

        [Header(Darkness)]
        _ShadowTint ("Shadow / Ambient Tint", Color) = (0.50, 0.50, 0.50, 1)

        [Header(Specular Glint)]
        // The signature of torch-lit water: a hard stepped highlight riding the
        // ripples. Tighter and stronger than ToonLit's by default.
        _SpecColor ("Specular Color (black = off)", Color) = (1, 0.93, 0.78, 1)
        _SpecPower ("Specular Tightness", Range(4, 256)) = 96
        _SpecSoftness ("Specular Edge Softness", Range(0.005, 0.3)) = 0.04

        [Header(Foam Edge)]
        _FoamColor ("Foam Color", Color) = (0.85, 0.95, 1, 1)
        // FULL width of the band, in metres of water depth. Foam only appears where
        // geometry is CLOSE BEHIND the surface, so if a water plane floats in open
        // space with nothing near it, no width will produce foam — turn on
        // _DebugDepth to see what the surface is actually reading.
        _FoamDistance ("Foam Width (m)", Range(0, 2)) = 0.3
        _FoamCutoff ("Foam Solidity (higher = more solid, less fade)", Range(0, 0.99)) = 0.5

        [Header(Debug)]
        // Shows the depth the foam and shallow/deep blend are driven by:
        // white = surface is right up against geometry, black = deep water behind it.
        // If the whole surface is black, nothing is close enough behind it to foam.
        // If it's a flat mid-grey with no gradient, the depth texture isn't arriving.
        [Toggle] _DebugDepth ("Debug: show water depth", Float) = 0

        [Header(Fresnel)]
        _FresnelColor ("Fresnel Color", Color) = (0.55, 0.80, 0.90, 1)
        _FresnelAmount ("Fresnel Amount", Range(0, 1)) = 0.35
        _FresnelPower ("Fresnel Tightness", Range(0.5, 8)) = 3

        [Header(Waves (vertex))]
        // Off by default — a well surface is still. Turn up for a fountain.
        _WaveAmplitude ("Wave Height (m)", Range(0, 0.15)) = 0
        _WaveFrequency ("Wave Frequency", Range(0.1, 12)) = 3
        _WaveSpeed ("Wave Speed", Range(0, 6)) = 1.4
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
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            // Forward+ / clustered lighting (keyword name varies by URP version).
            // Same set as ToonLit — without these the torch loop below never runs.
            #pragma multi_compile_fragment _ _FORWARD_PLUS _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BumpMap_ST;
                half4  _ShallowColor;
                half4  _DeepColor;
                half   _DepthFade;
                half   _DepthStrength;
                half   _BumpScale;
                half   _RippleScaleA;
                half   _RippleScaleB;
                float4 _RippleSpeedA;
                float4 _RippleSpeedB;
                half   _Bands;
                half   _BandSoftness;
                half   _ColorBands;
                half4  _ShadowTint;
                half4  _SpecColor;
                half   _SpecPower;
                half   _SpecSoftness;
                half4  _FoamColor;
                half   _FoamDistance;
                half   _FoamCutoff;
                half   _DebugDepth;
                half4  _FresnelColor;
                half   _FresnelAmount;
                half   _FresnelPower;
                half   _WaveAmplitude;
                half   _WaveFrequency;
                half   _WaveSpeed;
            CBUFFER_END

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
                float4 positionCS  : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float4 tangentWS   : TEXCOORD2;
                float3 positionWS  : TEXCOORD3;
                float4 screenPos   : TEXCOORD4;
                float  fogFactor   : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // Identical to ToonLit's — the two must band the same way or water
            // reads as belonging to a different game than the wall behind it.
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

            /// Quantize any 0..1 value into N steps. Used on the DEPTH gradient so
            /// shallow→deep steps like the lighting does instead of smoothly fading —
            /// a smooth gradient is the thing that most makes stylized water look
            /// realistic-but-wrong next to banded surroundings. 0 = leave smooth.
            half BandValue(half x, half bands)
            {
                if (bands < 0.5h) return x;
                return floor(saturate(x) * bands) / max(bands - 1.0h, 1.0h);
            }

            void ShadeLight(Light light, half3 normalWS, half3 viewDir,
                            inout half3 diffuse, inout half3 specular)
            {
                half ndl = saturate(dot(normalWS, light.direction));
                half banded = Ramp(ndl * light.distanceAttenuation);
                half shadowStep = smoothstep(0.25, 0.45, light.shadowAttenuation);
                half lit = banded * shadowStep;
                diffuse += light.color * lit;

                half3 h = SafeNormalize(light.direction + viewDir);
                half spec = pow(saturate(dot(normalWS, h)), _SpecPower);
                half glint = smoothstep(0.5 - _SpecSoftness, 0.5 + _SpecSoftness, spec);
                // Glint is gated by distanceAttenuation but NOT by the banded diffuse:
                // a highlight should still catch on a ripple facing away from the
                // torch, which is exactly what reads as a moving liquid surface.
                specular += light.color * _SpecColor.rgb * glint * light.distanceAttenuation;
            }

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);

                float3 positionOS = input.positionOS.xyz;

                // Vertex swell, along the surface normal so it works on a basin
                // that isn't axis-aligned. Two incommensurate sines again, so a
                // fountain never pulses in an obvious rhythm.
                if (_WaveAmplitude > 0.0001h)
                {
                    float t = _Time.y * _WaveSpeed;
                    float w = sin(positionOS.x * _WaveFrequency + t)
                            * cos(positionOS.z * _WaveFrequency * 0.83 + t * 1.31);
                    positionOS += input.normalOS * (w * _WaveAmplitude);
                }

                VertexPositionInputs pos = GetVertexPositionInputs(positionOS);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.screenPos = ComputeScreenPos(pos.positionCS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.tangentWS = float4(TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w);
                o.uv = TRANSFORM_TEX(input.uv, _BumpMap);
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // --- Ripples: one map, two scrolls. Averaging two tangent-space
                // normals is cheap and the beat between them hides the tiling.
                float2 uvA = input.uv * _RippleScaleA + _Time.y * _RippleSpeedA.xy;
                float2 uvB = input.uv * _RippleScaleB + _Time.y * _RippleSpeedB.xy;
                half3 nA = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvA), _BumpScale);
                half3 nB = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvB), _BumpScale);
                half3 normalTS = normalize(half3(nA.xy + nB.xy, nA.z * nB.z));

                half3 bitangentWS = input.tangentWS.w
                    * cross(normalize(input.normalWS), normalize(input.tangentWS.xyz));
                half3 normalWS = normalize(TransformTangentToWorld(normalTS,
                    half3x3(normalize(input.tangentWS.xyz), bitangentWS, normalize(input.normalWS))));
                half3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));

                // --- Depth: how much water is between this surface and whatever is
                // behind it. This is what makes a well shaft read as deep rather than
                // as a flat disc, and it's the same value the foam edge keys off.
                float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float surfaceEye = input.screenPos.w;
                float waterDepth = max(sceneEye - surfaceEye, 0);

                half depth01 = saturate(waterDepth / max(_DepthFade, 0.01h)) * _DepthStrength;
                half depthBanded = BandValue(depth01, _ColorBands);

                half4 water = lerp(_ShallowColor, _DeepColor, depthBanded);

                // --- Foam where the surface meets geometry.
                //
                // foamRaw is 1 right at contact, falling to 0 at _FoamDistance, so that
                // field IS the full band width. _FoamCutoff then decides how much of the
                // band is SOLID before it fades — a plain step() (the first version)
                // made the visible band _FoamDistance * _FoamCutoff, so the defaults
                // produced a ~5cm hard line that was easy to miss entirely.
                half foamRaw = 1.0h - saturate(waterDepth / max(_FoamDistance, 0.001h));
                half foam = saturate(foamRaw / max(1.0h - _FoamCutoff, 0.01h));
                foam *= step(0.0001h, _FoamDistance);   // width 0 = foam off

                // Depth readout. Deliberately CONTOUR BANDS over absolute metres rather
                // than a single normalized ramp: a plain ramp saturates to flat white the
                // moment anything is deeper than the scale, which looks identical whether
                // the water is genuinely deep or there is NO geometry behind it at all
                // (the read hits the far plane). Bands can't hide that — real depth
                // variation always produces visible rings, and a flat field means no
                // usable depth is arriving.
                //   RED   = within the foam band (this is where foam can appear)
                //   bands = 1m contour rings receding into the shaft
                //   flat  = nothing behind the surface, or no depth texture
                if (_DebugDepth > 0.5h)
                {
                    if (waterDepth < _FoamDistance) return half4(1, 0.15h, 0.1h, 1);
                    half band = frac(waterDepth) * 0.75h + 0.25h;
                    return half4(band.xxx, 1);
                }

                // --- Lighting, exactly ToonLit's model.
                half3 diffuse = _ShadowTint.rgb * (0.5h + 0.5h * SampleSH(normalWS));
                half3 specular = 0;

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                ShadeLight(mainLight, normalWS, viewDir, diffuse, specular);

                // The torch loop. Forward+ clusters, same as ToonLit — the macros read
                // `inputData` by name, so it must exist with these fields.
                #if defined(_ADDITIONAL_LIGHTS) || defined(_FORWARD_PLUS) || defined(_CLUSTER_LIGHT_LOOP)
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDir;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                uint pixelLightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));
                    ShadeLight(light, normalWS, viewDir, diffuse, specular);
                LIGHT_LOOP_END
                #endif

                half3 color = water.rgb * diffuse + specular;

                // --- Fresnel: brighter at grazing angles, banded so it steps.
                if (_FresnelAmount > 0.001h)
                {
                    half f = pow(1.0h - saturate(dot(viewDir, normalWS)), _FresnelPower);
                    color += _FresnelColor.rgb * Ramp(f) * _FresnelAmount;
                }

                color = lerp(color, _FoamColor.rgb, foam);

                // Opaque at the foam line and where it's deep; thin and see-through in
                // the shallows, which is what makes a basin read as actual water.
                half alpha = saturate(max(water.a, foam));

                color = MixFog(color, input.fogFactor);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
