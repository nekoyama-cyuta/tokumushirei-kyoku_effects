// 特務指令室: 情報球の投射光（基部リングから立ち昇る光のカーテン）
// 円錐台メッシュ（UV: x = 周方向 0..1, y = 高さ 0..1）に加算で描く。
//  - 下ほど明るく上で消える、縁ほど明るい（厚みのある光の層に見せる）
//  - 縦の光条がゆっくり流れ、横の帯が下から上へ昇る
//  - ホログラムと同じちらつき、リングとの接地は深度で柔らかく
Shader "Tokumu/HoloBeam"
{
    Properties
    {
        [HDR] _Color ("Color", Color) = (0.22, 0.68, 1.9, 1)
        _Intensity ("Intensity", Range(0, 8)) = 2.0
        _Falloff ("Vertical Falloff", Range(0.3, 6)) = 1.8
        _EdgeBoost ("Edge Boost", Range(0, 4)) = 1.6
        _Rays ("Rays", Range(0, 1)) = 0.9
        _RayCount ("Ray Count", Float) = 96
        _RaySpeed ("Ray Drift", Float) = 0.015
        _Bands ("Rising Bands", Range(0, 2)) = 0.8
        _BandCount ("Band Count", Float) = 5
        _BandSpeed ("Band Speed", Float) = 0.35
        _Flicker ("Flicker", Range(0, 1)) = 0.15
        _SoftDepth ("Soft Depth (m)", Float) = 0.12
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "HoloBeam"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "TokumuCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Intensity, _Falloff, _EdgeBoost, _Rays, _RayCount, _RaySpeed;
                float _Bands, _BandCount, _BandSpeed, _Flicker, _SoftDepth;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                float4 screenPos  : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // 周方向に周期 period で繋がる 2D 値ノイズ（円錐台の継ぎ目を出さない）
            float PNoise(float x, float y, float period, float seed)
            {
                float xi = floor(x), yi = floor(y);
                float2 f = frac(float2(x, y)); f = f * f * (3.0 - 2.0 * f);
                float x0 = fmod(xi, period), x1 = fmod(xi + 1.0, period);
                float a = Hash13(float3(x0, yi, seed)), b = Hash13(float3(x1, yi, seed));
                float c = Hash13(float3(x0, yi + 1.0, seed)), d = Hash13(float3(x1, yi + 1.0, seed));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.screenPos = ComputeScreenPos(o.positionCS);
                return o;
            }

            half4 Frag(Varyings i, bool isFront : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float t = _Time.y;
                float u = i.uv.x, h = i.uv.y;
                float3 V = GetWorldSpaceNormalizeViewDir(i.positionWS);
                float3 N = normalize(i.normalWS);
                float ndv = abs(dot(N, V));

                // 高さ方向: 根元が最も明るく、上で消える（最上部はさらに柔らかく）
                float vert = pow(saturate(1.0 - h), _Falloff) * smoothstep(0.0, 0.04, h);

                // 縁ほど明るい（薄い殻を斜めに見ると光の層が重なる）
                float edge = lerp(0.35, 1.0, pow(1.0 - ndv, 1.5)) * (1.0 + _EdgeBoost * pow(1.0 - ndv, 4.0));

                // 縦の光条: 周方向のノイズ（周期的に）をゆっくり流す。上へ行くほどにじむ
                float rc = max(round(_RayCount), 4.0);
                float ru = frac(u + t * _RaySpeed) * rc;
                float r1 = PNoise(ru, h * 1.5 - t * 0.2 + 50.0, rc, 0.0);
                float r2 = PNoise(ru * 2.0, h * 3.0 - t * 0.5 + 50.0, rc * 2.0, 4.0);
                float rays = lerp(1.0, saturate(pow(r1 * 0.7 + r2 * 0.3, 2.6) * 3.2), _Rays * lerp(1.0, 0.6, h));

                // 横の帯: 下から上へ昇る細い光の輪
                float bp = frac(h * _BandCount - t * _BandSpeed);
                float band = exp(-Sq((bp - 0.5) / 0.035)) * pow(saturate(1.0 - h), 2.2);   // 上では消える

                // ちらつき（ホログラムと同じ式）
                float flick = 1.0 - _Flicker * (0.5 + 0.5 * sin(t * 37.0) * sin(t * 11.3)) * Hash13(float3(floor(t * 24.0), 3, 7));

                // 接地（リング・台座）を深度で柔らかく
                float2 suv = i.screenPos.xy / i.screenPos.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                float fragEye = i.screenPos.w;
                float soft = saturate((sceneEye - fragEye) / max(_SoftDepth, 1e-3));

                // 上端は縁の強調も抑える（カーテンの上縁が線に見えないように）
                edge = lerp(1.0, edge, saturate(1.0 - h * 1.2));
                float3 e = _Color.rgb * (vert * edge * rays + band * _Bands * edge);
                e *= flick * soft * (isFront ? 1.0 : 0.6) * _Intensity * 0.25;
                return half4(max(e, 0), 0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
