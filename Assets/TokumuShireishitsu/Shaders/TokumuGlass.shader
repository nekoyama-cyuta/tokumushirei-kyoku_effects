// 特務指令室: 物理ベースのガラス（URP 17 / Forward+）
// - 板厚による視線の横ずれ＋フロートガラスのうねり（くさび）による屈折
// - RGB 別 IOR による分散
// - 背景距離に比例する曇りガラスのぼかし
// - Beer-Lambert 吸収（斜めほど濃くなる）
// - 表裏2面のフレネル反射（反射プローブ / SSR）＋ GGX ハイライト
// - 指紋・ほこりのむら、曇りガラスの内部散乱
// カメラに Opaque Texture と Depth Texture が必要（TokumuSetup が設定）。
Shader "Tokumu/Glass"
{
    Properties
    {
        [Header(Transmission)]
        _TintColor ("Tint (transmittance at Tint Distance)", Color) = (0.92, 0.97, 0.96, 1)
        _TintDistance ("Tint Distance (m)", Float) = 0.012
        _Thickness ("Glass Thickness (m)", Range(0.0005, 0.1)) = 0.012
        _TransmissionWeight ("Transmission Weight", Range(0, 1)) = 1
        _BaseColor ("Opaque Base Color (when weight < 1)", Color) = (0.02, 0.02, 0.025, 1)
        _Panes ("Panes (2 = double glazing)", Range(1, 3)) = 1
        _IOR ("IOR", Range(1.0, 2.0)) = 1.5
        _Dispersion ("Dispersion", Range(0, 0.1)) = 0.02
        _Roughness ("Roughness (frost)", Range(0, 1)) = 0.015
        _FrostBlur ("Frost Blur Scale", Range(0, 3)) = 1
        _Scatter ("Internal Scatter (haze)", Range(0, 2)) = 0

        [Header(Surface)]
        [Normal][NoScaleOffset] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 0
        _WaveStrength ("Float Glass Waviness", Range(0, 2)) = 0.35
        _WaveScale ("Waviness Frequency (1/m)", Float) = 0.8
        _SmudgeStrength ("Smudge / Dust", Range(0, 1)) = 0.25
        _SmudgeScale ("Smudge Frequency (1/m)", Float) = 4

        [Header(Reflection)]
        _ReflectionStrength ("Reflection", Range(0, 2)) = 1
        _SpecularStrength ("Specular Highlight", Range(0, 4)) = 1
        _SSRSteps ("Screen Space Reflection Steps (0 = off)", Range(0, 96)) = 56
        _SSRMaxDistance ("SSR Max Distance (m)", Float) = 45

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardGlass"
            Tags { "LightMode" = "UniversalForward" }
            Blend Off
            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            // URP の SSR テクスチャは「ガラスの奥の不透明面」の反射なので使わない（ガラス専用 SSR を自前で行う）
            #define _SCREENSPACEREFLECTIONS_OFF 1
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _TintColor;
                float _TintDistance;
                float _Thickness;
                half _TransmissionWeight;
                half4 _BaseColor;
                float _Panes;
                float _IOR;
                float _Dispersion;
                half _Roughness;
                half _FrostBlur;
                half _Scatter;
                half _BumpScale;
                half _WaveStrength;
                float _WaveScale;
                half _SmudgeStrength;
                float _SmudgeScale;
                half _ReflectionStrength;
                half _SpecularStrength;
                float _SSRSteps;
                float _SSRMaxDistance;
                float _Cull;
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
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2;
                float2 uv         : TEXCOORD3;
                half   fogFactor  : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs n = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = n.normalWS;
                o.tangentWS = float4(n.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.uv = v.uv;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            #include "TokumuCommon.hlsl"

            // 背景が手前に回り込んだ（ガラスより前の物を拾う）場合は元の位置へ戻す
            float2 SafeOffsetUV(float2 baseUV, float2 offsetUV, float fragEye)
            {
                float2 uv = saturate(baseUV + offsetUV);
                return EyeDepthAt(uv) < fragEye ? baseUV : uv;
            }

            float3 SampleRefraction(float2 baseUV, float2 offsetUV, float fragEye, float disp)
            {
                float3 c;
                c.r = SampleSceneColor(SafeOffsetUV(baseUV, offsetUV * (1.0 - disp), fragEye)).r;
                c.g = SampleSceneColor(SafeOffsetUV(baseUV, offsetUV, fragEye)).g;
                c.b = SampleSceneColor(SafeOffsetUV(baseUV, offsetUV * (1.0 + disp), fragEye)).b;
                return c;
            }

            float3 SampleFrosted(float2 centerUV, float radiusUV, float2 pix, float fragEye)
            {
                const int TAPS = 32;
                const float GOLDEN = 2.39996323;
                float rot = IGN(pix) * TWO_PI;
                float aspect = _ScreenParams.y / _ScreenParams.x;
                float3 sum = 0;
                float wsum = 0;
                [loop] for (int k = 0; k < TAPS; k++)
                {
                    float r = sqrt((k + 0.5) / TAPS);
                    float a = k * GOLDEN + rot;
                    float2 o = float2(cos(a) * aspect, sin(a)) * r * radiusUV;
                    float2 uv = saturate(centerUV + o);
                    // ガラスより手前の物はぼかしに混ぜない
                    float w = exp(-2.0 * r * r) * (EyeDepthAt(uv) < fragEye ? 0.0 : 1.0);
                    sum += SampleSceneColor(uv) * w;
                    wsum += w;
                }
                return wsum > 1e-4 ? sum / wsum : SampleSceneColor(centerUV);
            }

            half4 Frag(Varyings i, bool isFront : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 P = i.positionWS;
                float3 Ng = normalize(i.normalWS) * (isFront ? 1.0 : -1.0);
                float3 V = GetWorldSpaceNormalizeViewDir(P);
                float2 suv = GetNormalizedScreenSpaceUV(i.positionCS);
                float2 pix = i.positionCS.xy;

                // ---- 法線（ノーマルマップ＋フロートガラスのうねり）
                float3 N = Ng;
                if (_BumpScale > 0.0)
                {
                    float3 nTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv), _BumpScale);
                    float3 T = normalize(i.tangentWS.xyz);
                    float3 B = cross(Ng, T) * i.tangentWS.w;
                    N = normalize(nTS.x * T + nTS.y * B + nTS.z * Ng);
                }
                if (_WaveStrength > 0.0)
                {
                    float3 q = P * _WaveScale;
                    const float e = 0.07;
                    float n0 = Fbm(q);
                    float3 g = float3(Fbm(q + float3(e, 0, 0)), Fbm(q + float3(0, e, 0)), Fbm(q + float3(0, 0, e))) - n0;
                    g /= e;
                    g -= Ng * dot(g, Ng);
                    N = normalize(N - g * (_WaveStrength * 0.004));
                }

                // ---- 汚れ（指紋・ほこり）
                float smudge = 0.0;
                if (_SmudgeStrength > 0.0)
                {
                    smudge = SmudgeMask(P, _SmudgeScale) * _SmudgeStrength;
                }
                half rough = saturate(_Roughness + smudge * 0.12);

                // ---- フレネル（板の表裏2面）
                float NdotV = saturate(dot(N, V));
                float f0 = Sq((_IOR - 1.0) / (_IOR + 1.0));
                float F = f0 + (1.0 - f0) * pow(1.0 - NdotV, 5.0);
                float Fslab = 2.0 * F / (1.0 + F);
                // 複数枚（二重ガラス等）: 枚数 N の積層反射率 N*r / (1 + (N-1)*r)
                Fslab = _Panes * Fslab / (1.0 + (_Panes - 1.0) * Fslab);

                // ---- 屈折
                float fragEye = -TransformWorldToView(P).z;
                float sceneEye = EyeDepthAt(suv);
                float behind = clamp(sceneEye - fragEye, 0.0, 40.0);

                float3 Rt = refract(-V, N, 1.0 / _IOR);
                float cosT = max(dot(Rt, -Ng), 0.2);
                float path = _Thickness / cosT;
                float3 shift = Rt * path;
                shift -= (-V) * dot(shift, -V);                // 視線に垂直な横ずれ成分（平行板）
                float3 wedge = (N - Ng) - Ng * dot(N - Ng, Ng); // 表裏が平行でない分の偏角
                float3 Bstraight = P - V * behind;
                float3 Bbent = Bstraight + shift - wedge * (_IOR - 1.0) * behind;
                float2 offsetUV = WorldToScreenUV(Bbent) - WorldToScreenUV(Bstraight);

                float3 refr;
                float alpha = rough * rough;
                float spread = alpha * 0.55 * _FrostBlur;               // 散乱コーンの開き
                float radiusUV = spread * behind * UNITY_MATRIX_P[1][1] / max(2.0 * sceneEye, 0.01);
                radiusUV = min(radiusUV, 0.12);
                if (radiusUV * _ScreenParams.y > 1.0)
                    refr = SampleFrosted(SafeOffsetUV(suv, offsetUV, fragEye), radiusUV, pix, fragEye);
                else
                    refr = SampleRefraction(suv, offsetUV, fragEye, _Dispersion);

                // ---- 吸収（Beer-Lambert）
                float3 sigma = -log(max(_TintColor.rgb, 1e-3)) / max(_TintDistance, 1e-4);
                float3 trans = exp(-sigma * path * _Panes);
                float3 transmitted = refr * trans * (1.0 - Fslab);

                // ---- 反射（プローブ / SSR）
                float3 R = reflect(-V, N);
                float3 probe = GlossyEnvironmentReflection(R, P, rough, 1.0, suv);
                float ssrMask;
                float3 ssr = TraceReflection(P, R, rough, pix, _SSRSteps, _SSRMaxDistance, ssrMask);
                float3 refl = lerp(probe, ssr, ssrMask) * Fslab * _ReflectionStrength;

                // ---- ライト（GGX ハイライト＋内部散乱）
                BRDFData brdf;
                half outAlpha = 1.0;
                InitializeBRDFData(half3(0, 0, 0), 0.0, half3(0, 0, 0), 1.0 - rough, outAlpha, brdf);
                InputData inputData = (InputData)0;
                inputData.positionWS = P;
                inputData.normalWS = N;
                inputData.viewDirectionWS = V;
                inputData.normalizedScreenSpaceUV = suv;
                inputData.shadowCoord = TransformWorldToShadowCoord(P);
                half4 shadowMask = half4(1, 1, 1, 1);
                AmbientOcclusionFactor ao; ao.indirectAmbientOcclusion = 1; ao.directAmbientOcclusion = 1;

                float3 spec = 0;
                float3 scatterLight = SampleSH(N) * 0.5 + SampleSH(-N) * 0.5;
                Light mainLight = GetMainLight(inputData.shadowCoord);
                spec += LightingPhysicallyBased(brdf, mainLight, N, V);
                scatterLight += mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation * (0.5 + 0.5 * abs(dot(mainLight.direction, N)));

                #if defined(_ADDITIONAL_LIGHTS)
                uint pixelLightCount = GetAdditionalLightsCount();
                #if USE_CLUSTER_LIGHT_LOOP
                [loop] for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); lightIndex++)
                {
                    CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
                    Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
                    spec += LightingPhysicallyBased(brdf, light, N, V);
                    scatterLight += light.color * light.distanceAttenuation * light.shadowAttenuation * (0.5 + 0.5 * abs(dot(light.direction, N)));
                }
                #endif
                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, ao);
                    spec += LightingPhysicallyBased(brdf, light, N, V);
                    scatterLight += light.color * light.distanceAttenuation * light.shadowAttenuation * (0.5 + 0.5 * abs(dot(light.direction, N)));
                LIGHT_LOOP_END
                #endif

                float haze = _Scatter * (0.15 + 0.85 * alpha) + smudge * 0.06;
                float3 scattered = scatterLight * _TintColor.rgb * haze * 0.25;

                // 不透明成分（黒ガラス床など透過が 1 未満の材質）
                float3 opaqueDiffuse = _BaseColor.rgb * (scatterLight * 0.5);
                float3 col = lerp(opaqueDiffuse * (1.0 - Fslab), transmitted, _TransmissionWeight)
                             + refl + spec * _SpecularStrength + scattered;

                col = MixFog(col, i.fogFactor);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
