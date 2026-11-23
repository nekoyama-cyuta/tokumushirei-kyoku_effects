// 特務指令室: 情報球ホログラムの光。シェーダーのちらつきに合わせて明るさをわずかに揺らす（再生中のみ）
using UnityEngine;

[RequireComponent(typeof(Light))]
public class TokumuHologramLight : MonoBehaviour
{
    public float baseIntensity = 1.6f;
    [Range(0, 1)] public float flicker = 0.12f;
    [Range(0, 1)] public float breathe = 0.08f;

    Light m_Light;

    void OnEnable() { m_Light = GetComponent<Light>(); }

    void Update()
    {
        if (!Application.isPlaying || m_Light == null) return;
        float t = Time.time;
        float f = 1f - flicker * (0.5f + 0.5f * Mathf.Sin(t * 37f) * Mathf.Sin(t * 11.3f)) * Mathf.PerlinNoise(t * 24f, 0.3f);
        float b = 1f + breathe * Mathf.Sin(t * 1.3f);
        m_Light.intensity = baseIntensity * f * b;
    }

    void OnDisable() { if (m_Light != null) m_Light.intensity = baseIntensity; }
}
