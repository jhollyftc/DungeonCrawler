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
        [ToggleUI] _OutlineEnabled ("Outline", Float) = 1
        _OutlineColor ("Outline Color", Color) = (0.02, 0.02, 0.03, 1)
        _OutlineWidth ("Outline Width (meters)", Range(0, 0.06)) = 0.015

        [Header(Rim (off by default))]
        _RimColor ("Rim Color", Color) = (1.0, 0.93, 0.80, 1)
        _RimAmount ("Rim Amount", Range(0, 1)) = 0.0
        _RimThreshold ("Rim Threshold", Range(0, 1)) = 0.72

        // Emission is OFF by default (_EmissionColor black), so every existing material
        // is untouched. Property NAMES match URP/Lit deliberately — a material can be
        // switched from URP/Lit to this shader and keeps its authored emission.
        // _EmissionMap defaults to WHITE, not black, for the same reason: URP/Lit treats
        // colour-only emission (no map) as emitting everywhere, and a black default would
        // silently kill emission on any material that never assigned a map.
        [Header(Emission (off by default))]
        [HDR] _EmissionColor ("Emission Color (black = off)", Color) = (0, 0, 0, 1)
        _EmissionMap ("Emission Mask", 2D) = "white" {}

        // Per-instance temporal flicker on the emission. 0 = off, and off costs one
        // uniform branch in the vertex stage.
        [Header(Emission Flicker)]
        _FlickerAmount ("Flicker Amount", Range(0, 1)) = 0.0
        _FlickerSpeed ("Flicker Speed", Float) = 1.5
        _FlickerCellSize ("Flicker Cell Size (0 = whole mesh)", Float) = 0.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        TEXTURE2D(_MaskMap);
        TEXTURE2D(_BumpMap);
        TEXTURE2D(_EmissionMap);
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
            half   _OutlineEnabled;
            half4  _RimColor;
            half   _RimAmount;
            half   _RimThreshold;
            half4  _EmissionColor;
            half   _FlickerAmount;
            half   _FlickerSpeed;
            float  _FlickerCellSize;
        CBUFFER_END

        // ---------------- Emission flicker ----------------
        // A candle placed by the kit is usually PropTier.StaticDecor: mesh instanced, NO
        // GameObject, therefore no Light and no TorchFlicker to animate it — and a
        // MaterialPropertyBlock cannot reach the instanced path at all. So for those
        // pieces the shader is the ONLY place a flicker can live.
        //
        // The phase comes from the instance's WORLD ORIGIN, hashed. That is what makes
        // each candle burn independently instead of a whole room pulsing in lockstep, and
        // it survives batching because it is DERIVED rather than supplied — there is no
        // per-instance channel to put a value in. Requires UNITY_SETUP_INSTANCE_ID to have
        // run, which is why this is called from Vert and not from the struct initialiser.
        float ToonHash21(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        // THREE SINES AT INCOMMENSURATE RATIOS, the same fBm-style layering NpcFace uses
        // for jaw and brow and TextureEmission uses for stained glass. A single sine reads
        // as a mechanical pulse within seconds. The phase offset is scaled PER OCTAVE
        // (h, 2.3h, 4.7h) rather than shared — with one offset every candle shows the
        // IDENTICAL waveform merely time-shifted, obvious the moment two are on screen.
        // Weights sum to 1 so the result stays in [-1,1] and _FlickerAmount keeps meaning
        // "peak deviation".
        // `positionOS` and `vertexColor` differentiate elements WITHIN one mesh — a
        // candelabra whose flames are all part of a single instance, where the instance
        // origin is identical for every flame and they would otherwise pulse in unison.
        //
        // TWO SOURCES, because neither alone is both precise and free:
        //
        //  - VERTEX COLOUR (red channel) is the exact one. Author each flame a distinct
        //    value in Blender and every vertex of that flame agrees, whatever its shape or
        //    spacing. A mesh with no vertex colours reads as white, i.e. a constant, so
        //    this term costs nothing and changes nothing until it is authored.
        //
        //  - QUANTIZED OBJECT POSITION is the zero-authoring approximation. Raw positionOS
        //    cannot be used: it varies per VERTEX, so each vertex of one flame would pick
        //    its own phase and the flame would shear rather than pulse. Flooring to a cell
        //    collapses a whole flame onto one value. Set _FlickerCellSize LARGER than a
        //    single flame and SMALLER than the gap between flames; a flame straddling a
        //    cell boundary is the failure mode, and it looks like a torn flame.
        //    0 disables it and the whole mesh flickers as one, which is the old behaviour.
        half EmissionFlicker(float3 positionOS, half4 vertexColor)
        {
            if (_FlickerAmount <= 0.0001h) return 1.0h;   // uniform branch — free when off

            float3 originWS = TransformObjectToWorld(float3(0, 0, 0));
            float2 id = originWS.xz;
            if (_FlickerCellSize > 0.0001f)
            {
                float3 cell = floor(positionOS / _FlickerCellSize);
                id += cell.xz * 7.13f + cell.y * 3.71f;
            }
            id += vertexColor.r * 137.0h;
            float h = ToonHash21(id);
            float t = _Time.y * _FlickerSpeed;
            float phase = h * 6.2831853;
            float wave = sin(t        + phase      ) * 0.60
                       + sin(t * 1.7  + phase * 2.3) * 0.30
                       + sin(t * 3.1  + phase * 4.7) * 0.10;
            return 1.0h + wave * _FlickerAmount;
        }
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
                // Per-element flicker id (see EmissionFlicker). A mesh without vertex
                // colours supplies white, so declaring this is free and inert.
                half4  color      : COLOR;
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
                // Constant across the piece (it is per-INSTANCE, not per-vertex), so it is
                // resolved once in the vertex stage rather than three sines per pixel.
                half   emissionFlicker : TEXCOORD5;
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
                // After UNITY_SETUP_INSTANCE_ID — it reads unity_ObjectToWorld.
                o.emissionFlicker = EmissionFlicker(input.positionOS.xyz, input.color);
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

                // EMISSION LAST, before fog. It is unaffected by the light bands on purpose
                // — a candle flame is a source, not a lit surface, so banding it would make
                // it darken in shadow. Fogged with everything else so a glowing piece still
                // recedes with distance rather than reading as a decal (the mistake
                // TextureEmission shipped with).
                color += SAMPLE_TEXTURE2D(_EmissionMap, sampler_BaseMap, input.uv).rgb
                         * _EmissionColor.rgb * input.emissionFlicker;

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

                // OFF COLLAPSES THE HULL TO A POINT — it does NOT draw it at width zero, and the
                // difference is the whole reason this toggle exists. A zero-width hull is not
                // "no outline": it is a shell exactly COINCIDENT with the mesh, and with
                // Cull Front + ZWrite On the two surfaces z-fight, so the outline colour wins a
                // speckle of fragments across the entire surface. That reads as the mesh
                // changing colour rather than as an outline, and it is worst on thin,
                // doubly-curved geometry — chain links, wire, foliage — where nearly every
                // fragment is close to the silhouette. Setting the width to 0 was therefore the
                // WORST case, not the off switch it looks like, which is why it gets folded in
                // here as well.
                //
                // Collapsing all three vertices onto one clip-space point makes a zero-area
                // triangle, which rasterizes nothing at all.
                //
                // A UNIFORM BRANCH, DELIBERATELY NOT A shader_feature. The condition is constant
                // across every fragment of a draw, so it costs essentially nothing — while a
                // keyword would add a variant, and this project preloads its variant collections
                // by GUID and has already lost multiple sessions to variants arriving late (§5's
                // invisible NPCs). The emission feature was added under the same constraint and
                // for the same reason.
                if (_OutlineEnabled < 0.5h || _OutlineWidth <= 0)
                {
                    o.positionCS = float4(0, 0, 0, 1);
                    o.fogFactor = 0;
                    return o;
                }

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

        // ------------------------------------------------------------------
        // DepthNormals — REQUIRED, and its absence is invisible until something
        // reads the depth texture.
        //
        // URP does not always build _CameraDepthTexture from the DepthOnly pass. With
        // SSAO enabled and its Source set to DepthNormals (and AfterOpaque off), the
        // renderer runs a DEPTH-NORMALS PREPASS instead, drawing only shaders that have
        // THIS pass — and populates the depth texture from it. A shader with a perfect
        // DepthOnly pass and no DepthNormals pass therefore vanishes from the depth
        // texture entirely.
        //
        // Nothing complains. The surfaces still render, still cast shadows, still look
        // correct. What silently stops working is everything DOWNSTREAM of scene depth:
        // ToonWater's foam and shallow/deep blend found nothing behind them, and SSAO was
        // not being applied to a single toon-shaded surface in the dungeon — which is to
        // say, to almost the whole game.
        //
        // Diagnosed by a controlled test worth copying: two identical cubes with vertical
        // sides intersecting the water, one ToonLit and one URP/Lit. Same geometry, same
        // intersection; only the URP/Lit one produced foam. Holding the geometry constant
        // is what ruled out an earlier (wrong) explanation about surface slope.
        // ------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            // Instancing, for the same reason every other pass here declares it: the kit
            // draws through Graphics.RenderMeshInstanced, and a pass without it collapses
            // every instance onto one transform — silently (§5).
            #pragma multi_compile_instancing

            struct AttributesDN
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct VaryingsDN
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
            };

            VaryingsDN DepthNormalsVert(AttributesDN input)
            {
                VaryingsDN o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }

            half4 DepthNormalsFrag(VaryingsDN input) : SV_Target
            {
                // Geometric normal, not the normal MAP. SSAO and the water read this for
                // occlusion and surface orientation, where per-vertex is plenty; feeding
                // the bump map in would also make the crawling-band tradeoff (§6) show up
                // in ambient occlusion, which is not a trade worth making here.
                return half4(normalize(input.normalWS), 0.0);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Lit"
}