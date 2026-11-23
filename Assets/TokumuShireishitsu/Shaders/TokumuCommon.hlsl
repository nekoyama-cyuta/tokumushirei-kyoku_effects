// 特務指令室シェーダー共通: ノイズ／画面座標／自前スクリーンスペース反射
// 先に Core.hlsl, DeclareOpaqueTexture.hlsl, DeclareDepthTexture.hlsl を include しておくこと。
#ifndef TOKUMU_COMMON_INCLUDED
#define TOKUMU_COMMON_INCLUDED

float Hash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

float ValueNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = Hash13(i);
    float n100 = Hash13(i + float3(1, 0, 0));
    float n010 = Hash13(i + float3(0, 1, 0));
    float n110 = Hash13(i + float3(1, 1, 0));
    float n001 = Hash13(i + float3(0, 0, 1));
    float n101 = Hash13(i + float3(1, 0, 1));
    float n011 = Hash13(i + float3(0, 1, 1));
    float n111 = Hash13(i + float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
}

float Fbm(float3 p)
{
    float s = 0.0, a = 0.5;
    [unroll] for (int k = 0; k < 4; k++)
    {
        s += a * ValueNoise(p);
        p = p * 2.03 + 17.17;
        a *= 0.5;
    }
    return s;
}

// 指紋・ほこり・擦れのむら（0..1）
float SmudgeMask(float3 P, float scale)
{
    float s = Fbm(P * scale + 3.1);
    float streak = Fbm(float3(P.x * scale * 3.0, P.y * scale * 0.4, P.z * scale * 3.0));
    return saturate((s * 0.7 + streak * 0.3) * 1.9 - 0.85);
}

float IGN(float2 pix)
{
    return frac(52.9829189 * frac(dot(pix, float2(0.06711056, 0.00583715))));
}

float2 ClipToUV(float4 cs) { float4 sp = ComputeScreenPos(cs); return sp.xy / sp.w; }
float2 WorldToScreenUV(float3 positionWS) { return ClipToUV(TransformWorldToHClip(positionWS)); }
float EyeDepthAt(float2 uv) { return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams); }

// 不透明画像＋深度に対する反射レイマーチ。hitMask = 当たりの確からしさ（画面端・距離・向きでフェード）
float3 TraceReflection(float3 P, float3 R, half rough, float2 pix, float steps, float maxDist, out float hitMask)
{
    hitMask = 0.0;
    float3 fromCam = normalize(P - _WorldSpaceCameraPos);
    float facing = dot(R, fromCam);            // カメラ側へ戻るレイは画面外に出やすい
    if (facing < -0.35 || steps < 1) return 0;

    float dt = 0.04 * (0.75 + 0.5 * IGN(pix));
    float t = dt, prevT = 0.0;
    [loop] for (int k = 0; k < (int)steps; k++)
    {
        float3 Q = P + R * t;
        float4 cs = TransformWorldToHClip(Q);
        if (cs.w <= 0.01) break;
        float2 uv = ClipToUV(cs);
        if (any(uv < 0.0) || any(uv > 1.0)) break;
        float diff = cs.w - EyeDepthAt(uv);
        if (diff > 0.0 && diff < max(0.25, dt * 3.0))
        {
            float a = prevT, b = t;
            [unroll] for (int j = 0; j < 5; j++)
            {
                float m = 0.5 * (a + b);
                float4 cm = TransformWorldToHClip(P + R * m);
                if (cm.w - EyeDepthAt(ClipToUV(cm)) > 0.0) b = m; else a = m;
            }
            float2 huv = ClipToUV(TransformWorldToHClip(P + R * b));
            float2 edge = min(huv, 1.0 - huv);
            hitMask = saturate(min(edge.x, edge.y) * 10.0)
                    * saturate(1.0 - b / maxDist)
                    * saturate((facing + 0.35) * 2.5);
            float3 c = SampleSceneColor(huv);
            if (rough > 0.04)
            {
                // 粗さに応じて反射をぼかす（反射距離が遠いほど広がる）
                float r = rough * rough * b * UNITY_MATRIX_P[1][1] / max(cs.w, 0.1) * 0.5;
                float rot = IGN(pix + 17.0) * TWO_PI;
                [unroll] for (int s = 0; s < 6; s++)
                {
                    float ang = rot + s * 1.0471976;
                    c += SampleSceneColor(saturate(huv + float2(cos(ang), sin(ang)) * r));
                }
                c /= 7.0;
            }
            return c;
        }
        prevT = t;
        t += dt;
        dt *= 1.13;
        if (t > maxDist) break;
    }
    return 0;
}

#endif
