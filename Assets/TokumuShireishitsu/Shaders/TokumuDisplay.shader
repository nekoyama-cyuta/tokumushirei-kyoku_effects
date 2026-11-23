// 特務指令室: ディスプレイ（LED ビジョン／液晶）
//  - 近くでは画素の構造が見える: LED は RGB チップの粒と黒い隙間、液晶は RGB の縦縞サブピクセルとブラックマトリクス
//  - 画素が画面の 1〜2 ピクセルより細かくなる距離では模様を消して平均色に（モアレ防止、明るさの平均は不変）
//  - LED はモジュール（既定 64 粒）ごとのわずかな明るさのむら、液晶ビデオウォールはパネルの継ぎ目（ベゼル）
//  - 視野角: 斜めほど暗く、液晶はコントラストが落ちる
//  - 表面: カバーガラス（液晶）／黒いマスク（LED）に周囲が映り込む
//  - Meta パスで画面の光を APV に焼く
Shader "Tokumu/Display"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("Content (linear)", 2D) = "white" {}
        [HDR] _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Emission ("Emission Strength", Float) = 1
        [Enum(LED,0,LCD,1)] _Type ("Type", Float) = 0
        _Cells ("Pixels (x, y)", Vector) = (1920, 1080, 0, 0)
        [Header(LED)]
        _ChipSize ("Chip Size (cell)", Range(0.03, 0.2)) = 0.075
        _ChipSpacing ("Chip Spacing (cell)", Range(0, 0.25)) = 0.1
        _ModuleCells ("Module Size (pixels)", Float) = 64
        _ModuleVariation ("Module Brightness Variation", Range(0, 0.1)) = 0.035
        [Header(LCD)]
        _Aperture ("Subpixel Aperture", Range(0.4, 1)) = 0.8
        _RowAperture ("Row Aperture", Range(0.5, 1)) = 0.88
        _Panels ("Video Wall Panels (x, y)", Vector) = (1, 1, 0, 0)
        _Bezel ("Bezel Width (fraction of panel)", Range(0, 0.02)) = 0.0035
        _BlackLevel ("Backlight Leak", Range(0, 0.02)) = 0.004
        [Header(Surface)]
        _PanelColor ("Panel Albedo", Color) = (0.02, 0.022, 0.025, 1)
        _Smoothness ("Surface Smoothness", Range(0, 1)) = 0.9
        _F0 ("Surface F0", Range(0, 0.1)) = 0.04
        _ViewFalloff ("Off-axis Darkening", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #pragma target 4.5
        // GPU Resident Drawer（BatchRendererGroup）で描かれるので DOTS インスタンシングに対応させる
        #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_TexelSize;
            float4 _Tint;
            float _Emission, _Type;
            float4 _Cells;
            float _ChipSize, _ChipSpacing, _ModuleCells, _ModuleVariation;
            float _Aperture, _RowAperture;
            float4 _Panels;
            float _Bezel, _BlackLevel;
            float4 _PanelColor;
            float _Smoothness, _F0, _ViewFalloff;
        CBUFFER_END

        float Hash12(float2 p)
        {
            float3 p3 = frac(float3(p.xyx) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return frac((p3.x + p3.y) * p3.z);
        }

        // ビデオウォールの継ぎ目（外周は除く）。1 = 画面、0 = ベゼル
        float BezelMask(float2 uv)
        {
            if (_Panels.x * _Panels.y < 1.5) return 1.0;
            float2 p = uv * _Panels.xy;
            float2 r = round(p);
            float2 d = abs(p - r) / _Panels.xy;                         // 継ぎ目までの距離（uv 単位）
            float2 aa = max(fwidth(uv), 1e-5);
            float2 m = smoothstep(_Bezel * 0.5 - aa, _Bezel * 0.5 + aa, d);
            float2 inner = step(0.5, r) * step(r, _Panels.xy - 0.5);    // 内側の継ぎ目だけ
            m = lerp(1.0, m, inner);
            return m.x * m.y;
        }
        ENDHLSL

        Pass
        {
            Name "Display"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_instancing
            #pragma multi_compile _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile _ _FORWARD_PLUS
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
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
                o.uv = v.uv;
                return o;
            }

            // LED: 1 粒の中に R/G/B のチップ（縦に並ぶ小さなガウス）。各チャンネルのセル平均が 1 になるよう正規化
            float3 LEDPattern(float2 f)
            {
                float s2 = 2.0 * _ChipSize * _ChipSize;
                float norm = 1.0 / (PI * s2);
                float3 o = float3(_ChipSpacing, 0, -_ChipSpacing);
                return float3(exp(-dot(f - float2(0, o.x), f - float2(0, o.x)) / s2),
                              exp(-dot(f - float2(0, o.y), f - float2(0, o.y)) / s2),
                              exp(-dot(f - float2(0, o.z), f - float2(0, o.z)) / s2)) * norm;
            }

            // 液晶: R/G/B の縦縞（開口率 _Aperture）＋横のブラックマトリクス（_RowAperture）
            float3 LCDPattern(float2 g, float2 aa)
            {
                float3 c;
                float hw = _Aperture / 6.0;
                [unroll] for (int k = 0; k < 3; k++)
                {
                    float cx = (k + 0.5) / 3.0;
                    float dx = abs(g.x - cx);
                    c[k] = smoothstep(hw + aa.x, hw - aa.x, dx);
                }
                float dy = abs(g.y - 0.5);
                float row = smoothstep(_RowAperture * 0.5 + aa.y, _RowAperture * 0.5 - aa.y, dy);
                return c * row / (_Aperture / 3.0 * _RowAperture);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.uv;
                float3 P = i.positionWS;
                float3 N = normalize(i.normalWS);
                float3 V = GetWorldSpaceNormalizeViewDir(P);
                float ndv = saturate(dot(N, V));

                // ---- 画素（セル）
                float2 cc = uv * _Cells.xy;
                float2 fw = fwidth(cc);
                float ppc = 1.0 / max(max(fw.x, fw.y), 1e-5);            // 1 画素が何ピクセルに写っているか
                float pat = smoothstep(1.6, 4.0, ppc);                     // 模様を出す度合い（遠いと 0）

                float3 flatCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                float3 e = flatCol;
                if (pat > 0.001)
                {
                    float2 cellUV = (floor(cc) + 0.5) / _Cells.xy;
                    float lod = max(0.0, log2(_MainTex_TexelSize.z / _Cells.x));
                    float3 cellCol = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, cellUV, lod).rgb;
                    float3 mask = _Type < 0.5 ? LEDPattern(frac(cc) - 0.5) : LCDPattern(frac(cc), fw * 0.75);
                    e = lerp(flatCol, cellCol * mask, pat);
                }

                float surfaceGloss = _Smoothness;
                float panel = 1.0;
                if (_Type < 0.5)
                {
                    // LED モジュールの明るさのむら
                    float2 mid = floor(cc / max(_ModuleCells, 1.0));
                    e *= 1.0 + (Hash12(mid) - 0.5) * 2.0 * _ModuleVariation;
                    // 斜めでは暗く、わずかに色がずれる
                    e *= lerp(1.0 - _ViewFalloff, 1.0, smoothstep(0.0, 0.7, ndv)) * lerp(float3(1.04, 1.0, 0.9), 1.0, ndv);
                }
                else
                {
                    panel = BezelMask(uv);
                    e = e * panel + _BlackLevel * float3(0.8, 0.9, 1.1) * panel;   // バックライトの漏れ（黒が沈みきらない）
                    // 斜めでは暗く、コントラストが落ちる（IPS 液晶）
                    float off = pow(1.0 - ndv, 3.0);
                    float lum = dot(e, float3(0.2126, 0.7152, 0.0722));
                    e = lerp(e, lum * 0.55 + _BlackLevel * 2.0, off * 0.6);
                    e *= lerp(1.0 - _ViewFalloff * 0.6, 1.0, ndv);
                }
                float3 emis = e * _Tint.rgb * _Emission;

                // ---- 表面: 周囲の映り込み（フレネル）＋黒いパネル面の拡散
                float perceptualRough = 1.0 - surfaceGloss;
                float F = _F0 + (1.0 - _F0) * pow(1.0 - ndv, 5.0);
                float2 suv = GetNormalizedScreenSpaceUV(i.positionCS);
                float3 refl = GlossyEnvironmentReflection(reflect(-V, N), P, perceptualRough, 1.0, suv);
                #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
                    float3 amb = SampleProbeVolumePixel(SampleSH(N), P, N, V, i.positionCS.xy);
                #else
                    float3 amb = SampleSH(N);
                #endif
                float3 col = emis * (1.0 - F) + refl * F + amb * _PanelColor.rgb;
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }
            Cull Off
            HLSLPROGRAM
            #pragma vertex VertMeta
            #pragma fragment FragMeta
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma shader_feature EDITOR_VISUALIZATION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"
            struct A { float4 positionOS : POSITION; float2 uv0 : TEXCOORD0; float2 uv1 : TEXCOORD1; float2 uv2 : TEXCOORD2; };
            struct Vm
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            #ifdef EDITOR_VISUALIZATION
                float2 VizUV : TEXCOORD1;
                float4 LightCoord : TEXCOORD2;
            #endif
            };
            Vm VertMeta(A v)
            {
                Vm o = (Vm)0;
                o.positionCS = UnityMetaVertexPosition(v.positionOS.xyz, v.uv1, v.uv2);
                o.uv = v.uv0;
            #ifdef EDITOR_VISUALIZATION
                UnityEditorVizData(v.positionOS.xyz, v.uv0, v.uv1, v.uv2, o.VizUV, o.LightCoord);
            #endif
                return o;
            }
            half4 FragMeta(Vm i) : SV_Target
            {
                UnityMetaInput m = (UnityMetaInput)0;
                m.Albedo = _PanelColor.rgb;
                float panel = _Type < 0.5 ? 1.0 : BezelMask(i.uv);
                m.Emission = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv, 4).rgb * _Tint.rgb * _Emission * panel;
            #ifdef EDITOR_VISUALIZATION
                m.VizUV = i.VizUV; m.LightCoord = i.LightCoord;
            #endif
                return UnityMetaFragment(m);
            }
            ENDHLSL
        }

        // 影・深度・法線のパス（材質データの構成をメインのパスと揃えるため自前で持つ）
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment FS
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Vs { float4 positionCS : SV_POSITION; };
            Vs VS(A v)
            {
                Vs o; UNITY_SETUP_INSTANCE_ID(v);
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 l = normalize(_LightPosition - p);
            #else
                float3 l = _LightDirection;
            #endif
                o.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(p, n, l)));
                return o;
            }
            half4 FS(Vs i) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull Back
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment FS
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_instancing
            struct A { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Vs { float4 positionCS : SV_POSITION; };
            Vs VS(A v) { Vs o; UNITY_SETUP_INSTANCE_ID(v); o.positionCS = TransformObjectToHClip(v.positionOS.xyz); return o; }
            half FS(Vs i) : SV_Target { return i.positionCS.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On Cull Back
            HLSLPROGRAM
            #pragma vertex VS
            #pragma fragment FS
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Vs { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            Vs VS(A v)
            {
                Vs o; UNITY_SETUP_INSTANCE_ID(v);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }
            half4 FS(Vs i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 oct = saturate(PackNormalOctQuadEncode(n) * 0.5 + 0.5);
                return half4(PackFloat2To888(oct), 0);
            #else
                return half4(n, 0);
            #endif
            }
            ENDHLSL
        }
    }
    FallBack Off
}
