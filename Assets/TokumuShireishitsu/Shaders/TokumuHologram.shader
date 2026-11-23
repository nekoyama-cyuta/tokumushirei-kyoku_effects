// 特務指令室: 情報球のホログラム
// 球の中心からの方向で正距円筒の地図（ドットの陸地＋経緯線）を引き、加算で両面描画する。
//  - 自転、縁の発光（フレネル）、内部の淡い光（視線が球を通る長さ）
//  - 海岸線の発光、経緯線、上下に走るスキャン帯、細かい走査線
//  - ちらつき、ときどき横にずれるグリッチ、警報地点の波紋
Shader "Tokumu/Hologram"
{
    Properties
    {
        [NoScaleOffset] _MapTex ("Map (equirect, linear)", 2D) = "black" {}
        [HDR] _Color ("Color", Color) = (0.35, 0.78, 1.6, 1)
        [HDR] _AlertColor ("Alert Color", Color) = (2.4, 0.35, 0.25, 1)
        _Intensity ("Intensity", Range(0, 8)) = 2.4
        _BackFace ("Back Hemisphere", Range(0, 1)) = 0.35
        _SpinSpeed ("Spin (turns/min)", Float) = 1.5
        _Rim ("Rim", Range(0, 4)) = 1.4
        _RimPower ("Rim Power", Range(0.5, 8)) = 3
        _Core ("Inner Haze", Range(0, 1)) = 0.12
        _Coast ("Coastline Glow", Range(0, 4)) = 1.2
        _Grid ("Grid", Range(0, 2)) = 0.35
        _GridDeg ("Grid Spacing (deg)", Float) = 15
        _Scan ("Scan Band", Range(0, 4)) = 1.2
        _ScanSpeed ("Scan Speed (m/s)", Float) = 0.45
        _Scanlines ("Scanlines", Range(0, 1)) = 0.25
        _Flicker ("Flicker", Range(0, 1)) = 0.12
        _Glitch ("Glitch", Range(0, 1)) = 0.5
        _Pulse ("Alert Pulses", Range(0, 4)) = 1.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Hologram"
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

            TEXTURE2D(_MapTex); SAMPLER(sampler_MapTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _AlertColor;
                float _Intensity, _BackFace, _SpinSpeed, _Rim, _RimPower, _Core, _Coast;
                float _Grid, _GridDeg, _Scan, _ScanSpeed, _Scanlines, _Flicker, _Glitch, _Pulse;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 centerWS   : TEXCOORD2;
                float  radius     : TEXCOORD3;
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
                o.centerWS = TransformObjectToWorld(float3(0, 0, 0));
                o.radius = length(o.positionWS - o.centerWS);
                return o;
            }

            static const float3 kAlerts[3] = { float3(0.62, 0.36, 0), float3(0.18, 0.58, 1.7), float3(0.84, 0.48, 3.1) }; // (lon, lat, 位相)

            // 経度の継ぎ目でミップが飛ばないよう、2 通りの経度の微分から小さい方を使う
            float3 SampleMap(float2 uv, float lod)
            {
                float2 uvAlt = float2(frac(uv.x + 0.5) - 0.5, uv.y);
                float2 dx = ddx(uv), dy = ddy(uv), dxa = ddx(uvAlt), dya = ddy(uvAlt);
                if (dot(dxa, dxa) + dot(dya, dya) < dot(dx, dx) + dot(dy, dy)) { dx = dxa; dy = dya; }
                if (lod >= 0) return SAMPLE_TEXTURE2D_LOD(_MapTex, sampler_MapTex, uv, lod).rgb;
                return SAMPLE_TEXTURE2D_GRAD(_MapTex, sampler_MapTex, uv, dx, dy).rgb;
            }

            half4 Frag(Varyings i, bool isFront : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float t = _Time.y;
                float3 P = i.positionWS;
                float3 C = i.centerWS;
                float R = max(i.radius, 1e-3);
                float3 V = GetWorldSpaceNormalizeViewDir(P);
                float3 N = normalize(i.normalWS);
                float facing = abs(dot(N, V));

                // ---- ちらつき・グリッチ
                float flick = 1.0 - _Flicker * (0.5 + 0.5 * sin(t * 37.0) * sin(t * 11.3)) * Hash13(float3(floor(t * 24.0), 3, 7));
                float gslot = floor(t * 3.0);
                float glitchOn = step(1.0 - 0.12 * _Glitch, Hash13(float3(gslot, 1, 2)));
                float bandY = Hash13(float3(gslot, 5, 9)) * 2.0 - 1.0;

                // ---- 球面座標（自転）
                float3 d = (P - C) / R;
                float spin = t * _SpinSpeed / 60.0 * TWO_PI;
                float cs = cos(spin), sn = sin(spin);
                float3 dr = float3(cs * d.x - sn * d.z, d.y, sn * d.x + cs * d.z);
                float lon = atan2(dr.z, dr.x) / TWO_PI + 0.5;
                float lat = asin(clamp(dr.y, -1, 1)) / PI + 0.5;
                // グリッチ: 横帯だけ経度をずらす
                float inBand = glitchOn * step(abs(d.y - bandY), 0.06);
                lon += inBand * (Hash13(float3(gslot, 8, 1)) - 0.5) * 0.08;
                float2 uv = float2(frac(lon), lat);

                // ---- 地図（ドットの陸地）と海岸線
                float3 map = SampleMap(uv, -1);
                float landLo = dot(SampleMap(uv, 5.5), float3(0.3, 0.4, 0.3));   // ぼかした陸地マスク
                float land = saturate(landLo * 6.0);
                float coast = saturate(1.0 - abs(land - 0.5) * 2.0);
                float dots = dot(map, float3(0.3, 0.4, 0.3));

                // ---- 経緯線（ピクセル幅で AA）
                float2 deg = float2(lon * 360.0, (lat - 0.5) * 180.0) / _GridDeg;
                float2 gd = abs(frac(deg + 0.5) - 0.5) / max(fwidth(deg), 1e-4);
                float grid = saturate(1.0 - min(gd.x, gd.y) * 0.7) * (1.0 - land * 0.6);

                // ---- スキャン帯（世界座標で上下に往復）と走査線
                float scanPos = C.y + R * sin(t * _ScanSpeed / R);
                float scan = exp(-Sq((P.y - scanPos) / (0.06 * R)));
                float lines = 1.0 - _Scanlines * (0.5 + 0.5 * sin(P.y * 260.0 + t * 6.0));

                // ---- 警報地点の波紋（大円距離で広がるリング）
                float pulse = 0;
                [unroll] for (int k = 0; k < 3; k++)
                {
                    float2 a = kAlerts[k].xy;
                    float plon = (a.x - 0.5) * TWO_PI, plat = (a.y - 0.5) * PI;
                    float3 q = float3(cos(plat) * cos(plon), sin(plat), cos(plat) * sin(plon));
                    float3 pr = float3(cos((lat - 0.5) * PI) * cos((lon - 0.5) * TWO_PI), sin((lat - 0.5) * PI), cos((lat - 0.5) * PI) * sin((lon - 0.5) * TWO_PI));
                    float ang = acos(clamp(dot(pr, q), -1, 1));
                    float ph = frac(t * 0.45 + kAlerts[k].z * 0.16);
                    float ringR = ph * 0.35;
                    float ring = exp(-Sq((ang - ringR) / 0.012)) * (1.0 - ph);
                    float core = exp(-Sq(ang / 0.018)) * (0.6 + 0.4 * sin(t * 8.0 + k));
                    pulse += (ring + core) * (k == 0 ? 1.0 : 0.55);
                }

                // ---- 縁の発光と内部の淡い光
                float rim = pow(1.0 - facing, _RimPower) * _Rim;
                float b2 = dot(P - C, P - C) - Sq(dot(P - C, -V));            // 視線と中心の距離²
                float chord = 2.0 * sqrt(max(R * R - b2, 0.0)) / (2.0 * R);   // 視線が球を通る長さ（0..1）
                float haze = isFront ? _Core * chord : 0.0;

                float3 col = _Color.rgb;
                // 陸地のドットをくっきり、海は暗く（コントラスト）。海岸線と陸地の淡い面で形を読ませる
                float dotsC = pow(saturate(dots * 1.6), 1.3);
                float3 e = col * (dotsC * 1.4 + land * 0.05 + coast * _Coast * 0.18 + grid * _Grid * 0.35 + haze)
                         + col * scan * _Scan * (0.12 + dotsC * 1.6)
                         + _AlertColor.rgb * pulse * _Pulse * 0.6
                         + col * rim * 0.3;
                e *= lines * flick * (1.0 + inBand * 0.8);
                e *= isFront ? 1.0 : _BackFace;
                e *= _Intensity * 0.42;
                return half4(max(e, 0), 0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
