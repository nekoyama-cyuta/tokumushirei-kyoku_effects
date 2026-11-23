// 特務指令室: ボリューメトリック光（光の筋と空気感）
// Pass 0: 低解像度でレイマーチ。URP の追加ライト（位置・色・減衰・スポット角）と影マップをそのまま使う。
//         出力 rgb = 散乱光、a = その画素の視線距離（拡大時の深度判定用）
// Pass 1: 深度を見ながら拡大して合成。dst * 透過率 + 散乱光
Shader "Hidden/Tokumu/Volumetric"
{
    HLSLINCLUDE
    #pragma target 4.5
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

    float4 _VolParams;     // x: 散乱係数 σs (1/m), y: 消散係数 σt (1/m), z: 位相 g, w: 強さ
    float4 _VolParams2;    // x: ステップ数, y: 最大距離 (m), z: ノイズ量, w: ノイズ周波数 (1/m)
    float4 _VolParams3;    // x: 高さ減衰の基準高さ (m), y: 高さ減衰 (1/m), z: ライト数, w: 環境光の散乱
    float4 _VolWind;       // xyz: 風（ノイズの流れ m/s）
    float4 _VolAmbient;    // 環境の散乱色（大画面などの照り返しで空気が明るむ分）
    TEXTURE2D(_VolLowRes); SAMPLER(sampler_VolLowRes);
    float4 _VolLowRes_TexelSize;
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "Raymarch"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMarch
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            // 空気中の影はくっきりでも見た目が変わらないので、ソフトシャドウ（9 サンプル）は使わない
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "TokumuCommon.hlsl"

            float Density(float3 p)
            {
                float d = exp(-max(p.y - _VolParams3.x, 0.0) * _VolParams3.y);
                float n = ValueNoise(p * _VolParams2.w + _VolWind.xyz * _Time.y);   // 1 段（負荷計測で 2 段 → 1 段に）
                return d * lerp(1.0, saturate(n * 1.8 - 0.25), _VolParams2.z);
            }

            float PhaseHG(float cosT, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosT, 1e-4), 1.5));
            }

            half4 FragMarch(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                float raw = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                    float3 Pend = ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
                #else
                    float3 Pend = ComputeWorldSpacePosition(uv, lerp(UNITY_NEAR_CLIP_VALUE, 1, raw), UNITY_MATRIX_I_VP);
                #endif
                float3 O = _WorldSpaceCameraPos;
                float3 ray = Pend - O;
                float dist = min(length(ray), _VolParams2.y);
                float3 D = normalize(ray);

                int steps = (int)_VolParams2.x;
                float dt = dist / steps;
                float jitter = IGN(i.positionCS.xy + frac(_Time.y * 7.0) * 64.0);
                float sigmaS = _VolParams.x, sigmaT = _VolParams.y, g = _VolParams.z;
                int nLights = (int)_VolParams3.z;

                // 視線から遠く、光が届かないライトを先に除外（最大 32）
                uint mask = 0;
                for (int li = 0; li < min(nLights, 32); li++)
                {
                #if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
                    float4 lp = _AdditionalLightsBuffer[li].position;
                    float4 att = _AdditionalLightsBuffer[li].attenuation;
                #else
                    float4 lp = _AdditionalLightsPosition[li];
                    float4 att = _AdditionalLightsAttenuation[li];
                #endif
                    if (lp.w < 0.5) { mask |= 1u << li; continue; }          // 平行光源
                    float range = sqrt(1.0 / max(att.x, 1e-6));             // URP は x = 1/range²
                    float tc = clamp(dot(lp.xyz - O, D), 0.0, dist);
                    float3 cp = O + D * tc;
                    if (dot(cp - lp.xyz, cp - lp.xyz) < range * range) mask |= 1u << li;
                }

                float3 scat = 0;
                float T = 1.0;
                for (int s = 0; s < steps; s++)
                {
                    float t = (s + jitter) * dt;
                    float3 P = O + D * t;
                    float dens = Density(P);
                    float3 Ls = _VolAmbient.rgb * _VolParams3.w;
                    if (mask != 0)
                    {
                        for (int k = 0; k < min(nLights, 32); k++)
                        {
                            if ((mask & (1u << k)) == 0) continue;
                            Light l = GetAdditionalPerObjectLight(k, P);
                            if (l.distanceAttenuation <= 1e-5) continue;
                            half sh = AdditionalLightRealtimeShadow(k, P, l.direction);
                            // 影のないライト（エリアライトの近似）は空気を一様に明るくするだけなので弱める
                            float w = GetAdditionalLightShadowParams(k).x > 0.0 ? 1.0 : 0.35;
                            Ls += l.color * (l.distanceAttenuation * sh * w * PhaseHG(dot(D, -l.direction), g) * 4.0 * PI);
                        }
                    }
                    float st = sigmaT * dens;
                    float stepT = exp(-st * dt);
                    // 1 ステップ内の散乱を解析的に積分（エネルギー保存）
                    scat += T * Ls * sigmaS * dens * (1.0 - stepT) / max(st, 1e-6) * (1.0 / (4.0 * PI));
                    T *= stepT;
                }
                return half4(scat * _VolParams.w, length(ray));
            }
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            Blend One SrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            half4 FragComposite(Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                float raw = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                    float3 P = ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
                #else
                    float3 P = ComputeWorldSpacePosition(uv, lerp(UNITY_NEAR_CLIP_VALUE, 1, raw), UNITY_MATRIX_I_VP);
                #endif
                float dist = length(P - _WorldSpaceCameraPos);

                // 深度を見た 4x4 拡大＋ぼかし（レイマーチのディザを消し、奥行きの違う画素は混ぜない）
                float2 st = uv * _VolLowRes_TexelSize.zw - 0.5;
                float2 f = frac(st);
                float2 base = (floor(st) + 0.5) * _VolLowRes_TexelSize.xy;
                float3 acc = 0; float wsum = 0;
                [unroll] for (int y = -1; y <= 2; y++)
                [unroll] for (int x = -1; x <= 2; x++)
                {
                    float2 o = float2(x, y);
                    float4 s = SAMPLE_TEXTURE2D_LOD(_VolLowRes, sampler_VolLowRes, base + o * _VolLowRes_TexelSize.xy, 0);
                    float2 dd = o - f;
                    float gw = exp(-dot(dd, dd) * 0.55);
                    float dw = 1.0 / (1e-3 + abs(s.a - dist) / max(dist, 0.5) * 20.0);
                    float w = gw * dw + 1e-6;
                    acc += s.rgb * w; wsum += w;
                }
                float3 scat = acc / wsum;
                float T = exp(-_VolParams.y * min(dist, _VolParams2.y) * 0.85);
                return half4(scat, T);
            }
            ENDHLSL
        }
    }
}
