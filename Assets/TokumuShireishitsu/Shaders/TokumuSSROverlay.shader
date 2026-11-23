// 特務指令室: 不透明の艶あり面（黒ガラス床など）に重ねる映り込み
// TokumuPlanarReflection が平面反射テクスチャを渡した床はそれを使い、無ければ自前 SSR で代用する。
// 同じメッシュに追加マテリアルとして載せ、Lit の反射プローブ分を SSR で置き換えるように合成する。
// TokumuPreTransparentFeature が透明物の直前に描画し、不透明テクスチャを取り直す。
//   out = SSR * F * hit + dst * (1 - F * hit)
Shader "Tokumu/SSROverlay"
{
    Properties
    {
        _IOR ("IOR", Range(1.0, 2.0)) = 1.5
        _Roughness ("Roughness", Range(0, 1)) = 0.035
        _Strength ("Strength", Range(0, 2)) = 1
        _SmudgeStrength ("Smudge / Scuffs", Range(0, 1)) = 0.35
        _SmudgeScale ("Smudge Frequency (1/m)", Float) = 1.5
        _SSRSteps ("SSR Steps", Range(1, 96)) = 64
        _SSRMaxDistance ("SSR Max Distance (m)", Float) = 45
        [Header(Planar Reflection)]
        _PlanarBlur ("Planar Blur (mip per roughness)", Float) = 9
        _PlanarDistort ("Planar Distortion", Range(0, 0.05)) = 0.004
        [HideInInspector] _PlanarTex ("Planar", 2D) = "black" {}
        [HideInInspector] _HasPlanar ("Has Planar", Float) = 0
        [HideInInspector] _PlanarHeight ("Planar Height", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-1" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "SSROverlay"
            Tags { "LightMode" = "TokumuPreTransparent" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "TokumuCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _IOR;
                half _Roughness;
                half _Strength;
                half _SmudgeStrength;
                float _SmudgeScale;
                float _SSRSteps;
                float _SSRMaxDistance;
                float _PlanarBlur;
                float _PlanarDistort;
                float _HasPlanar;
                float _PlanarHeight;
            CBUFFER_END

            TEXTURE2D(_PlanarTex); SAMPLER(sampler_PlanarTex);
            float _TokumuPlanarDebug;   // 検証用（グローバル）
            float4 _PlanarTex_TexelSize;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 P = i.positionWS;
                float3 N = normalize(i.normalWS);
                float3 V = GetWorldSpaceNormalizeViewDir(P);
                float2 pix = i.positionCS.xy;

                float smudge = _SmudgeStrength > 0.0 ? SmudgeMask(P, _SmudgeScale) * _SmudgeStrength : 0.0;
                half rough = saturate(_Roughness + smudge * 0.15);

                float f0 = Sq((_IOR - 1.0) / (_IOR + 1.0));
                float F = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(N, V)), 5.0);
                F *= lerp(1.0, 0.75, smudge);              // 擦れた所は映り込みが鈍る

                if (_TokumuPlanarDebug > 0.5)
                {
                    bool ok = abs(P.y - _PlanarHeight) < 0.02 && N.y > 0.99;
                    return _HasPlanar > 0.5 ? (ok ? half4(0, 0.6, 0, 0.6) : half4(0.6, 0, 0, 0.6)) : half4(0, 0, 0.6, 0.6);
                }
                float3 refl;
                float w;
                // 反射テクスチャの床面と同じ高さ・上向きの所だけ平面反射（段差や別の高さは SSR）
                if (_HasPlanar > 0.5 && abs(P.y - _PlanarHeight) < 0.02 && N.y > 0.99)
                {
                    // 平面反射: 鏡像カメラの画像を画面座標で引く。擦れ・うねりで少し揺らし、粗さに応じてミップでぼかす
                    float2 suv = WorldToScreenUV(P);
                    // 床のうねり（大きくゆっくり）＋擦れでわずかに揺らす。細かいノイズだと反射が点線状にちぎれる
                    float2 wob = float2(Fbm(float3(P.xz * 0.35, 1.7)) - 0.5, Fbm(float3(P.xz * 0.35, 9.2)) - 0.5);
                    suv += wob * _PlanarDistort * (1.0 + smudge * 1.5);
                    float mips = log2(max(_PlanarTex_TexelSize.z, _PlanarTex_TexelSize.w));
                    float lod = clamp(rough * _PlanarBlur, 0.0, mips - 1.0);
                    refl = SAMPLE_TEXTURE2D_LOD(_PlanarTex, sampler_PlanarTex, saturate(suv), lod).rgb;
                    w = saturate(F * _Strength);
                }
                else
                {
                    float hit;
                    refl = TraceReflection(P, reflect(-V, N), rough, pix, _SSRSteps, _SSRMaxDistance, hit);
                    w = saturate(F * hit * _Strength);
                }
                return half4(refl * w, w);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
