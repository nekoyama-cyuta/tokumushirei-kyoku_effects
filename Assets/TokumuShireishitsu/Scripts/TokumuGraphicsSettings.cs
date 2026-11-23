// 特務指令室: ゲーム内グラフィック設定
// プリセット（低／中／高／最高）と個別項目で、描画解像度・アップスケーラー・空気感・床の反射・影・ほこり・AA を切り替える。
// F1 または Esc でメニュー。設定は PlayerPrefs に保存。
// エディタで再生した場合は、終了時にアセット（URP 設定・レンダラー機能）の値を元に戻す。
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DefaultExecutionOrder(-100)]
public class TokumuGraphicsSettings : MonoBehaviour
{
    public enum Preset { Low, Medium, High, Ultra, Custom }
    public enum Upscaler { Bilinear, FSR, STP }
    public enum SSAO { Off, Half, Full }
    public enum Volumetric { Off, Low, High, Ultra }
    public enum Reflections { Off, SSR, Planar }
    public enum AA { Off, FXAA, SMAA }

    [Serializable]
    public class Options
    {
        public float renderScale = 0.75f;
        public Upscaler upscaler = Upscaler.FSR;
        public Volumetric volumetric = Volumetric.Low;
        public Reflections reflections = Reflections.Planar;
        public float planarResolution = 0.4f;
        public int shadowLights = 4;
        public bool softShadows = true;
        public bool dust = true;
        public AA antiAliasing = AA.FXAA;
        public SSAO ssao = SSAO.Half;
        public Options Clone() => (Options)MemberwiseClone();
    }

    public static Options PresetOptions(Preset p)
    {
        switch (p)
        {
            case Preset.Low:    return new Options { renderScale = 0.6f,  upscaler = Upscaler.FSR, volumetric = Volumetric.Off,  reflections = Reflections.Off,    planarResolution = 0.4f,  shadowLights = 2,  softShadows = false, dust = false, antiAliasing = AA.FXAA, ssao = SSAO.Off };
            case Preset.Medium: return new Options { renderScale = 0.75f, upscaler = Upscaler.FSR, volumetric = Volumetric.Low,  reflections = Reflections.Planar, planarResolution = 0.4f,  shadowLights = 4,  softShadows = true,  dust = true,  antiAliasing = AA.FXAA, ssao = SSAO.Half };
            case Preset.High:   return new Options { renderScale = 1.0f,  upscaler = Upscaler.FSR, volumetric = Volumetric.High, reflections = Reflections.Planar, planarResolution = 0.5f,  shadowLights = 6,  softShadows = true,  dust = true,  antiAliasing = AA.SMAA, ssao = SSAO.Half };
            default:            return new Options { renderScale = 1.0f,  upscaler = Upscaler.FSR, volumetric = Volumetric.Ultra, reflections = Reflections.Planar, planarResolution = 0.75f, shadowLights = 10, softShadows = true,  dust = true,  antiAliasing = AA.SMAA, ssao = SSAO.Full };
        }
    }

    public static readonly string[] PresetNames = { "低", "中（0.75倍＋FSR）", "高", "最高", "カスタム" };

    [Header("参照（セットアップが設定）")]
    public TokumuVolumetricFeature volumetricFeature;
    public TokumuPreTransparentFeature floorOverlayFeature;
    public ScreenSpaceAmbientOcclusion ssaoFeature;
    public TokumuPlanarReflection planarReflection;
    [Tooltip("影を落とせるスポットライト（重要な順）")]
    public List<Light> shadowLights = new List<Light>();
    public List<GameObject> dustObjects = new List<GameObject>();

    [Header("状態")]
    public Preset preset = Preset.Medium;
    public Options current = PresetOptions(Preset.Medium);
    public bool showMenu;
    public bool showFps = true;

    const string PrefKey = "Tokumu.Graphics.v1";

    // エディタ再生時に元へ戻す値
    struct AssetBackup
    {
        public float renderScale; public UpscalingFilterSelection filter;
        public bool volActive; public int volSteps; public int volDownsample; public bool overlayActive;
        public bool aoActive; public bool aoDownsample;
    }
    AssetBackup m_Backup; bool m_HasBackup;
    float m_FpsSmoothed; Vector2 m_Scroll;

    UniversalRenderPipelineAsset Asset => (QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;

    void Awake()
    {
        if (!Application.isPlaying) return;
        Backup();
        Load();
        Apply();
    }

    void OnDestroy() { if (Application.isPlaying) Restore(); }
    void OnApplicationQuit() { Restore(); }

    // ------------------------------------------------------------ apply
    public void SetPreset(Preset p)
    {
        preset = p;
        if (p != Preset.Custom) current = PresetOptions(p);
        Apply(); Save();
    }

    public void Apply() => Apply(current);

    public void Apply(Options o)
    {
        var asset = Asset;
        if (asset != null)
        {
            asset.renderScale = Mathf.Clamp(o.renderScale, 0.5f, 1f);
            asset.upscalingFilter = o.upscaler == Upscaler.FSR ? UpscalingFilterSelection.FSR
                                  : o.upscaler == Upscaler.STP ? UpscalingFilterSelection.STP : UpscalingFilterSelection.Linear;
            asset.msaaSampleCount = 1;   // AA は後処理で行う
        }

        if (volumetricFeature != null)
        {
            volumetricFeature.SetActive(o.volumetric != Volumetric.Off);
            var s = volumetricFeature.settings;
            switch (o.volumetric)
            {
                case Volumetric.Low:   s.steps = 14; s.downsample = 4; break;
                case Volumetric.High:  s.steps = 20; s.downsample = 4; break;
                case Volumetric.Ultra: s.steps = 28; s.downsample = 3; break;
            }
        }

        if (floorOverlayFeature != null) floorOverlayFeature.SetActive(o.reflections != Reflections.Off);
        if (ssaoFeature != null)
        {
            ssaoFeature.SetActive(o.ssao != SSAO.Off);
            SetAODownsample(o.ssao != SSAO.Full);
        }
        if (planarReflection != null)
        {
            planarReflection.resolutionScale = Mathf.Clamp(o.planarResolution, 0.25f, 1f);
            planarReflection.enabled = o.reflections == Reflections.Planar;
        }

        for (int i = 0; i < shadowLights.Count; i++)
        {
            var l = shadowLights[i];
            if (l == null) continue;
            l.shadows = i < o.shadowLights ? (o.softShadows ? LightShadows.Soft : LightShadows.Hard) : LightShadows.None;
        }

        foreach (var d in dustObjects) if (d != null) d.SetActive(o.dust);

        foreach (var cam in Camera.allCameras)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null) continue;
            // STP は TAA の仕組みの上で動くので、選んだときは AA を TAA にする（STP が AA も兼ねる）
            data.antialiasing = o.upscaler == Upscaler.STP ? AntialiasingMode.TemporalAntiAliasing
                              : o.antiAliasing == AA.SMAA ? AntialiasingMode.SubpixelMorphologicalAntiAliasing
                              : o.antiAliasing == AA.FXAA ? AntialiasingMode.FastApproximateAntialiasing : AntialiasingMode.None;
            data.antialiasingQuality = AntialiasingQuality.High;
        }
    }

    // ------------------------------------------------------------ persistence
    [Serializable] class Saved { public Preset preset; public Options options; }

    void Save()
    {
        PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(new Saved { preset = preset, options = current }));
        PlayerPrefs.Save();
    }

    void Load()
    {
        var json = PlayerPrefs.GetString(PrefKey, "");
        if (string.IsNullOrEmpty(json)) { preset = Preset.Medium; current = PresetOptions(preset); return; }
        try
        {
            var s = JsonUtility.FromJson<Saved>(json);
            preset = s.preset;
            current = s.preset == Preset.Custom && s.options != null ? s.options : PresetOptions(s.preset);
        }
        catch { preset = Preset.Medium; current = PresetOptions(preset); }
    }

    // ------------------------------------------------------------ editor safety
    void Backup()
    {
        var a = Asset;
        if (a == null) return;
        m_Backup = new AssetBackup
        {
            renderScale = a.renderScale, filter = a.upscalingFilter,
            volActive = volumetricFeature != null && volumetricFeature.isActive,
            volSteps = volumetricFeature != null ? volumetricFeature.settings.steps : 20,
            volDownsample = volumetricFeature != null ? volumetricFeature.settings.downsample : 4,
            overlayActive = floorOverlayFeature == null || floorOverlayFeature.isActive,
            aoActive = ssaoFeature == null || ssaoFeature.isActive,
            aoDownsample = GetAODownsample(),
        };
        m_HasBackup = true;
    }

    void Restore()
    {
        if (!m_HasBackup || !Application.isEditor) return;
        m_HasBackup = false;
        var a = Asset;
        if (a != null) { a.renderScale = m_Backup.renderScale; a.upscalingFilter = m_Backup.filter; }
        if (volumetricFeature != null)
        {
            volumetricFeature.SetActive(m_Backup.volActive);
            volumetricFeature.settings.steps = m_Backup.volSteps;
            volumetricFeature.settings.downsample = m_Backup.volDownsample;
        }
        if (floorOverlayFeature != null) floorOverlayFeature.SetActive(m_Backup.overlayActive);
        if (ssaoFeature != null) { ssaoFeature.SetActive(m_Backup.aoActive); SetAODownsample(m_Backup.aoDownsample); }
    }

    // SSAO の解像度（URP の設定は非公開なので、シリアライズ済みのフィールドを直接書き換える）
    static readonly System.Reflection.FieldInfo s_AOSettings =
        typeof(ScreenSpaceAmbientOcclusion).GetField("m_Settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    bool GetAODownsample()
    {
        var st = ssaoFeature != null ? s_AOSettings?.GetValue(ssaoFeature) : null;
        var f = st?.GetType().GetField("Downsample", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return f != null && (bool)f.GetValue(st);
    }
    void SetAODownsample(bool v)
    {
        var st = ssaoFeature != null ? s_AOSettings?.GetValue(ssaoFeature) : null;
        var f = st?.GetType().GetField("Downsample", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (f != null) f.SetValue(st, v);
    }

    // ------------------------------------------------------------ menu
    void Update()
    {
        if (!Application.isPlaying) return;
        float dt = Time.unscaledDeltaTime;
        if (dt > 0) m_FpsSmoothed = m_FpsSmoothed <= 0 ? 1f / dt : Mathf.Lerp(m_FpsSmoothed, 1f / dt, 0.05f);
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && (kb.f1Key.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame)) showMenu = !showMenu;
#else
        if (Input.GetKeyDown(KeyCode.F1) || Input.GetKeyDown(KeyCode.Escape)) showMenu = !showMenu;
#endif
    }

    GUIStyle m_Panel, m_Title, m_Label, m_Button, m_ButtonOn, m_Small;

    void Styles()
    {
        if (m_Panel != null) return;
        Texture2D Tex(Color c) { var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave }; t.SetPixel(0, 0, c); t.Apply(); return t; }
        m_Panel = new GUIStyle { padding = new RectOffset(22, 22, 18, 18) };
        m_Panel.normal.background = Tex(new Color(0.03f, 0.05f, 0.08f, 0.92f));
        m_Title = new GUIStyle { fontSize = 22, fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 0, 12) };
        m_Title.normal.textColor = new Color(0.55f, 0.85f, 1f);
        m_Label = new GUIStyle { fontSize = 15, alignment = TextAnchor.MiddleLeft, fixedWidth = 190, fixedHeight = 30 };
        m_Label.normal.textColor = new Color(0.85f, 0.9f, 0.95f);
        m_Small = new GUIStyle(m_Label) { fontSize = 13, fixedWidth = 0 };
        m_Small.normal.textColor = new Color(0.6f, 0.7f, 0.8f);
        m_Button = new GUIStyle { fontSize = 14, alignment = TextAnchor.MiddleCenter, fixedHeight = 30, padding = new RectOffset(10, 10, 4, 4), margin = new RectOffset(3, 3, 2, 2) };
        m_Button.normal.background = Tex(new Color(0.10f, 0.14f, 0.20f, 1f));
        m_Button.hover.background = Tex(new Color(0.16f, 0.24f, 0.34f, 1f));
        m_Button.normal.textColor = m_Button.hover.textColor = new Color(0.8f, 0.88f, 0.95f);
        m_ButtonOn = new GUIStyle(m_Button);
        m_ButtonOn.normal.background = m_ButtonOn.hover.background = Tex(new Color(0.12f, 0.45f, 0.75f, 1f));
        m_ButtonOn.normal.textColor = m_ButtonOn.hover.textColor = Color.white;
    }

    bool Choice<T>(string label, ref T value, T[] values, string[] names)
    {
        bool changed = false;
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, m_Label);
        for (int i = 0; i < values.Length; i++)
        {
            bool on = EqualityComparer<T>.Default.Equals(value, values[i]);
            if (GUILayout.Button(names[i], on ? m_ButtonOn : m_Button) && !on) { value = values[i]; changed = true; }
        }
        GUILayout.EndHorizontal();
        return changed;
    }

    void OnGUI()
    {
        if (!Application.isPlaying) return;
        Styles();
        float ui = Mathf.Max(1f, Screen.height / 1080f);
        var m = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(ui, ui, 1));
        float W = Screen.width / ui, H = Screen.height / ui;

        if (showFps)
        {
            float ms = m_FpsSmoothed > 0 ? 1000f / m_FpsSmoothed : 0;
            GUI.Label(new Rect(W - 240, 10, 230, 24), $"{m_FpsSmoothed:F0} fps  ({ms:F1} ms)", m_Small);
            if (!showMenu) GUI.Label(new Rect(W - 240, 32, 230, 24), "F1: グラフィック設定", m_Small);
        }
        if (!showMenu) { GUI.matrix = m; return; }

        var rect = new Rect(W * 0.5f - 430, H * 0.5f - 330, 860, 660);
        GUILayout.BeginArea(rect, m_Panel);
        GUILayout.Label("グラフィック設定", m_Title);
        m_Scroll = GUILayout.BeginScrollView(m_Scroll);

        var p = preset;
        if (Choice("プリセット", ref p, new[] { Preset.Low, Preset.Medium, Preset.High, Preset.Ultra },
                   new[] { "低", "中（0.75倍＋FSR）", "高", "最高" })) SetPreset(p);
        if (preset == Preset.Custom) GUILayout.Label("　　カスタム（個別に変更中）", m_Small);
        GUILayout.Space(10);

        var o = current.Clone();
        bool ch = false;
        ch |= Choice("描画解像度", ref o.renderScale, new[] { 0.5f, 0.6f, 0.67f, 0.75f, 0.85f, 1f }, new[] { "50%", "60%", "67%", "75%", "85%", "100%" });
        ch |= Choice("拡大方式", ref o.upscaler, new[] { Upscaler.STP, Upscaler.FSR, Upscaler.Bilinear }, new[] { "STP（時間方向）", "FSR 1.0", "バイリニア" });
        ch |= Choice("光の筋・空気感", ref o.volumetric, new[] { Volumetric.Off, Volumetric.Low, Volumetric.High, Volumetric.Ultra }, new[] { "オフ", "低", "高", "最高" });
        ch |= Choice("床の映り込み", ref o.reflections, new[] { Reflections.Off, Reflections.SSR, Reflections.Planar }, new[] { "オフ", "画面内のみ(SSR)", "平面反射" });
        if (o.reflections == Reflections.Planar)
            ch |= Choice("　反射の解像度", ref o.planarResolution, new[] { 0.25f, 0.4f, 0.5f, 0.75f }, new[] { "25%", "40%", "50%", "75%" });
        int maxShadows = Mathf.Max(1, shadowLights.Count);
        ch |= Choice("影を落とすライト", ref o.shadowLights, new[] { 0, 2, 4, 6, maxShadows }, new[] { "なし", "2", "4", "6", maxShadows.ToString() });
        ch |= Choice("影の輪郭", ref o.softShadows, new[] { true, false }, new[] { "ソフト", "くっきり（軽い）" });
        ch |= Choice("接地の陰り（SSAO）", ref o.ssao, new[] { SSAO.Off, SSAO.Half, SSAO.Full }, new[] { "オフ", "半解像度", "等倍" });
        ch |= Choice("ほこり", ref o.dust, new[] { true, false }, new[] { "あり", "なし" });
        if (o.upscaler == Upscaler.STP)
            GUILayout.Label("　アンチエイリアス: STP が兼ねる（時間方向）", m_Small);
        else
            ch |= Choice("アンチエイリアス", ref o.antiAliasing, new[] { AA.Off, AA.FXAA, AA.SMAA }, new[] { "オフ", "FXAA", "SMAA" });
        if (ch) { current = o; preset = Preset.Custom; Apply(); Save(); }

        GUILayout.Space(10);
        GUILayout.BeginHorizontal();
        var fps = showFps;
        Choice("FPS 表示", ref fps, new[] { true, false }, new[] { "表示", "非表示" });
        showFps = fps;
        GUILayout.EndHorizontal();
        GUILayout.EndScrollView();
        GUILayout.Label("F1 / Esc で閉じる", m_Small);
        GUILayout.EndArea();
        GUI.matrix = m;
    }
}
