// 特務指令室: Blender から書き出したモデルの Unity(URP) セットアップ
//   Tools > 特務指令室 > すべてセットアップ   … インポート設定・マテリアル・シーン作成
//   Tools > 特務指令室 > マテリアルのみ       … FBX インポート設定とマテリアルのみ
//   Tools > 特務指令室 > シーンのみ作成       … シーン（ライト・カメラ・ポスト・反射）を作り直す
// フォルダ内に .setup_pending があると、コンパイル後に一度だけ自動実行します（中身＝確認用キャプチャの出力先）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class TokumuSetup
{
    const string Root = "Assets/TokumuShireishitsu";
    const string Fbx = Root + "/meetingRoom_Claude.fbx";
    const string Marker = Root + "/.setup_pending";
    const string ScenePath = Root + "/Scenes/TokumuShireishitsu.unity";
    const string Log = "[特務指令室] ";

    // ライト強度の全体補正（Blender W → URP 強度の換算後に掛ける）
    const float LightScale = 1.0f;
    // 反射プローブの強さ（1点のボックス投影なので映り込みが強く出すぎるのを抑える）
    const float ProbeIntensity = 0.6f;

    static TokumuSetup()
    {
        EditorApplication.delayCall += AutoRun;
        // スクリプトの再読み込みが無くても、置かれたマーカーを 2 秒ごとに拾う
        EditorApplication.update += PollMarker;
    }

    static double s_NextPoll;
    static void PollMarker()
    {
        if (EditorApplication.timeSinceStartup < s_NextPoll) return;
        s_NextPoll = EditorApplication.timeSinceStartup + 2.0;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (Lightmapping.isRunning) return;
        if (File.Exists(Marker)) AutoRun();
    }

    static void AutoRun()
    {
        if (!File.Exists(Marker)) return;
        string capture = File.ReadAllText(Marker).Trim();
        File.Delete(Marker);
        string mode = "all";
        int bar = capture.IndexOf('|');
        if (bar >= 0) { mode = capture.Substring(0, bar); capture = capture.Substring(bar + 1); }
        string dir = string.IsNullOrEmpty(capture) ? null : capture;
        try
        {
            if (mode == "scene") BuildScene(dir);
            else if (mode == "review") Review(dir);
            else if (mode == "light") LightBake(dir);
            else if (mode == "capture") CaptureAll(dir);
            else if (mode == "perf") Perf(dir);
            else if (mode == "perfpresets") PerfPresets(dir);
            else if (mode == "mixedlights") SetAllLightsMixed();
            else SetupAll(dir);
        }
        catch (Exception e) { Debug.LogError(Log + "セットアップ失敗: " + e); }
    }

    [MenuItem("Tools/特務指令室/すべてセットアップ")] static void MenuAll() => SetupAll(null);
    [MenuItem("Tools/特務指令室/マテリアルのみ")] static void MenuMat() => SetupModelAndMaterials();
    [MenuItem("Tools/特務指令室/シーンのみ作成")] static void MenuScene() => BuildScene(null);
    [MenuItem("Tools/特務指令室/ガラスを再適用して確認カメラを撮影")] static void MenuReview() =>
        Review(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "TokumuReview"));

    [MenuItem("Tools/特務指令室/間接光をベイク（APV）")] static void MenuLight() =>
        LightBake(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "TokumuLight"));

    // ------------------------------------------------------------- lighting
    // 間接光（照り返し・発光体の光）を Adaptive Probe Volumes に焼く。
    // 直接光と影はリアルタイムのまま（Mixed / Baked Indirect）。ライトマップ UV は不要。
    static string s_LightCaptureDir;

    static bool OpenOurScene()
    {
        if (SceneManager.GetActiveScene().path == ScenePath) return true;
        if (SceneManager.GetActiveScene().isDirty) { Debug.LogError(Log + "開いているシーンに未保存の変更があるため中止"); return false; }
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        return true;
    }

    static void ReapplyAllMaterials()
    {
        var list = JsonUtility.FromJson<MatList>(File.ReadAllText(Root + "/materials.json"));
        var all = AssetDatabase.FindAssets("t:Material", new[] { Root })
            .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(x => x != null).GroupBy(x => x.name).ToDictionary(g => g.Key, g => g.First());
        foreach (var m in list.materials)
            if (all.TryGetValue(Sanitize(m.name), out var mat)) { ApplyLit(mat, m); EditorUtility.SetDirty(mat); }
        AssetDatabase.SaveAssets();
    }

    static void ImportLinearEmission()
    {
        var list = JsonUtility.FromJson<MatList>(File.ReadAllText(Root + "/materials.json"));
        foreach (var rel in list.materials.Select(m => LinearEmission(m.emissionMap)).Where(r => r != null && r.EndsWith(".exr")).Distinct())
        {
            AssetDatabase.ImportAsset(Root + "/" + rel, ImportAssetOptions.ForceSynchronousImport);
            SetTex(rel, false, false);
        }
    }

    // ベイクできない間の退避: すべて Mixed に戻す（Baked にしたまま古いベイクデータだと、そのライトが消える）
    static void SetAllLightsMixed()
    {
        if (!OpenOurScene()) return;
        foreach (var l in GameObject.Find("Lights_FromBlender").GetComponentsInChildren<Light>(true))
            l.lightmapBakeType = LightmapBakeType.Mixed;
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Debug.Log(Log + "ライトをすべて Mixed に戻しました");
    }

    // 直接光も焼くライト（名前の先頭で判定）
    static readonly string[] BakedLightPrefixes = { "エリア_トラス", "作業灯エリア" };

    static void LightBake(string captureDir)
    {
        ConfigurePipeline();
        if (!OpenOurScene()) return;
        ImportLinearEmission();
        ReapplyAllMaterials();
        var scene = SceneManager.GetActiveScene();

        // モデル: GI に寄与し、間接光はプローブから受け取る
        var model = GameObject.Find("特務指令室");
        foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
        {
            GameObjectUtility.SetStaticEditorFlags(r.gameObject,
                StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            r.receiveGI = ReceiveGI.LightProbes;
            PrefabUtility.RecordPrefabInstancePropertyModifications(r);
            PrefabUtility.RecordPrefabInstancePropertyModifications(r.gameObject);
        }
        // ライト: 基本は Mixed（直接光はリアルタイム、間接光をベイク）。
        // 天井向きのトラス照明とサーバー室の天井照明は広く柔らかく照らすだけなので Baked（直接光も APV に焼く）にし、
        // 毎フレームのライト計算から外す
        int nBaked = 0;
        foreach (var l in GameObject.Find("Lights_FromBlender").GetComponentsInChildren<Light>(true))
        {
            bool baked = BakedLightPrefixes.Any(pre => l.name.StartsWith(pre));
            l.lightmapBakeType = baked ? LightmapBakeType.Baked : LightmapBakeType.Mixed;
            l.bounceIntensity = 1f;
            if (baked) nBaked++;
        }
        Debug.Log(Log + $"Baked ライト: {nBaked} 灯（{string.Join("・", BakedLightPrefixes)}）");

        var ls = Lightmapping.lightingSettings;
        ls.bakedGI = true;
        ls.realtimeGI = false;
        ls.mixedBakeMode = MixedLightingMode.IndirectOnly;
        // 内蔵 GPU（共有 VRAM 2GB）では GPU ライトマッパーがメモリ不足で落ちるので CPU で焼く
        ls.lightmapper = LightingSettings.Lightmapper.ProgressiveCPU;
        ls.directSampleCount = 32;
        ls.indirectSampleCount = 192;
        ls.environmentSampleCount = 64;
        ls.lightProbeSampleCountMultiplier = 1f;
        ls.maxBounces = 3;
        ls.directionalityMode = LightmapsMode.NonDirectional;
        EditorUtility.SetDirty(ls);

        // APV: 全体を覆うグローバルボリュームと、シーン専用のベイキングセット
        var pvGo = GameObject.Find("AdaptiveProbeVolume");
        if (pvGo == null) pvGo = new GameObject("AdaptiveProbeVolume");
        var pv = pvGo.GetComponent<ProbeVolume>();
        if (pv == null) pv = pvGo.AddComponent<ProbeVolume>();
        pv.mode = ProbeVolume.Mode.Global;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        string dir = Path.GetDirectoryName(ScenePath).Replace(Path.DirectorySeparatorChar, '/') + "/TokumuShireishitsu";
        string setPath = dir + "/TokumuShireishitsu Baking Set.asset";
        var set = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(setPath);
        if (set == null)
        {
            set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            typeof(ProbeVolumeBakingSet).GetMethod("SetDefaults", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(set, null);
            set.name = "TokumuShireishitsu Baking Set";
            AssetDatabase.CreateAsset(set, setPath);
        }
        bool added = set.TryAddScene(AssetDatabase.AssetPathToGUID(ScenePath));
        set.minDistanceBetweenProbes = 1.0f;   // 細部は 1m 間隔、何もない空間は 27m まで粗く
        set.simplificationLevels = 3;
        EditorUtility.SetDirty(set);
        AssetDatabase.SaveAssets();
        Debug.Log(Log + $"APV ベイキングセット: {setPath}（シーン追加 {added}）");

        s_LightCaptureDir = captureDir;
        Lightmapping.bakeCompleted -= OnLightBaked;
        Lightmapping.bakeCompleted += OnLightBaked;
        s_BakeStart = DateTime.Now;
        bool started = Lightmapping.BakeAsync();
        Debug.Log(Log + "間接光ベイク開始: " + started);
        if (!started) { Lightmapping.bakeCompleted -= OnLightBaked; return; }
        EditorApplication.update -= BakeGuard;
        EditorApplication.update += BakeGuard;
    }

    static DateTime s_BakeStart;
    static double s_NextGuard;

    // ベイク中にディスクの空きが減りすぎたら中止する（前回はページファイルの膨張で空きが尽きた）
    static void BakeGuard()
    {
        if (!Lightmapping.isRunning && !AdaptiveProbeVolumes.isRunning) { EditorApplication.update -= BakeGuard; return; }
        if (EditorApplication.timeSinceStartup < s_NextGuard) return;
        s_NextGuard = EditorApplication.timeSinceStartup + 5;
        long free = new DriveInfo(Path.GetPathRoot(Application.dataPath)).AvailableFreeSpace;
        if (free < 2L * 1024 * 1024 * 1024)
        {
            EditorApplication.update -= BakeGuard;
            Lightmapping.bakeCompleted -= OnLightBaked;
            Lightmapping.Cancel();
            AdaptiveProbeVolumes.Cancel();
            Debug.LogError(Log + $"ディスク空き {free / 1048576} MB のためベイクを中止");
        }
    }

    static void OnLightBaked()
    {
        Lightmapping.bakeCompleted -= OnLightBaked;
        Debug.Log(Log + $"間接光ベイク完了 {(DateTime.Now - s_BakeStart).TotalSeconds:F0} 秒");
        // 反射プローブも間接光込みで焼き直す
        ShaderUtil.allowAsyncCompilation = false;
        try
        {
            string dir = Path.GetDirectoryName(ScenePath).Replace(Path.DirectorySeparatorChar, '/') + "/TokumuShireishitsu";
            foreach (var p in UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None))
                Lightmapping.BakeReflectionProbe(p, dir + "/" + (p.name == "ReflectionProbe_Room" ? "ReflectionProbe_Room" : p.name) + ".exr");
        }
        finally { ShaderUtil.allowAsyncCompilation = true; }
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        CaptureAll(s_LightCaptureDir);
    }

    // ------------------------------------------------------------ optimize
    // 計測（2026-11-18、1920x1080）で重かった 3 つを軽くする: 平面反射・追加ライトの影・ボリューメトリック
    static void OptimizePerf()
    {
        // 1) 平面反射: 不透明物だけを描く軽いレンダラー（PC_Renderer の複製から機能と透明物を外す）
        var rpa = (QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
        var rso = new SerializedObject(rpa);
        var list = rso.FindProperty("m_RendererDataList");
        var src = list.GetArrayElementAtIndex(0).objectReferenceValue as UniversalRendererData;
        string srcPath = AssetDatabase.GetAssetPath(src);
        string dstPath = Path.GetDirectoryName(srcPath).Replace(Path.DirectorySeparatorChar, '/') + "/PC_PlanarRenderer.asset";
        var planarData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(dstPath);
        if (planarData == null)
        {
            AssetDatabase.CopyAsset(srcPath, dstPath);
            planarData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(dstPath);
        }
        // 機能は持たせない（以前の処理で溜まった分も含めて毎回掃除）
        int removed = 0;
        foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(dstPath).OfType<ScriptableRendererFeature>().ToList())
        { UnityEngine.Object.DestroyImmediate(sub, true); removed++; }
        if (removed > 0) Debug.Log(Log + $"平面反射レンダラーの不要な機能を削除: {removed}");
        var pso = new SerializedObject(planarData);
        pso.FindProperty("m_RendererFeatures").arraySize = 0;
        pso.FindProperty("m_RendererFeatureMap").arraySize = 0;
        // 透明物はホログラム（情報球・投射光）だけ映す。ガラスは不透明テクスチャが無いと正しく描けないので外す
        int holoLayer = EnsureLayer("TokumuHologram");
        foreach (var n in new[] { "球形ディスプレイ_球体", "情報球_投射光" })
        {
            var g = GameObject.Find(n);
            if (g == null) continue;
            foreach (var t in g.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = holoLayer;
            PrefabUtility.RecordPrefabInstancePropertyModifications(g);
        }
        pso.FindProperty("m_TransparentLayerMask").FindPropertyRelative("m_Bits").uintValue = holoLayer >= 0 ? (1u << holoLayer) : 0u;
        pso.ApplyModifiedProperties();
        EditorUtility.SetDirty(planarData);
        int index = -1;
        for (int i = 0; i < list.arraySize; i++) if (list.GetArrayElementAtIndex(i).objectReferenceValue == planarData) index = i;
        if (index < 0)
        {
            list.arraySize++; index = list.arraySize - 1;
            list.GetArrayElementAtIndex(index).objectReferenceValue = planarData;
            rso.ApplyModifiedProperties();
            EditorUtility.SetDirty(rpa);
        }
        var pr = UnityEngine.Object.FindAnyObjectByType<TokumuPlanarReflection>();
        if (pr != null)
        {
            pr.rendererIndex = index; pr.resolutionScale = 0.5f; pr.mergeTolerance = 0.15f; pr.minScreenCoverage = 0.03f;
            EditorUtility.SetDirty(pr);
            pr.enabled = false; pr.enabled = true;
        }

        // 2) 影: 床置きのアップライトと暗いライトは影なし（影マップ 1 枚ごとにシーン全体を描き直すため）
        int kept = 0, cut = 0;
        foreach (var l in GameObject.Find("Lights_FromBlender").GetComponentsInChildren<Light>(true))
        {
            if (l.type != LightType.Spot || l.shadows == LightShadows.None) continue;
            if (l.transform.position.y < 1.0f || l.intensity < 5f) { l.shadows = LightShadows.None; cut++; } else kept++;
        }
        //    小物（25cm 未満）は影を落とさない
        int small = 0;
        foreach (var r in GameObject.Find("特務指令室").GetComponentsInChildren<MeshRenderer>(true))
        {
            if (r.name.StartsWith(OverlayPrefix)) continue;
            var sz = r.bounds.size;
            if (Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z)) < 0.25f && r.shadowCastingMode != ShadowCastingMode.Off)
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                PrefabUtility.RecordPrefabInstancePropertyModifications(r);
                small++;
            }
        }
        // ほこりは影を落とすライトの子にだけ置くので、影を切ったライトのほこりは外す
        foreach (var l in GameObject.Find("Lights_FromBlender").GetComponentsInChildren<Light>(true))
            if (l.shadows == LightShadows.None) { var d = l.transform.Find("Dust"); if (d) UnityEngine.Object.DestroyImmediate(d.gameObject); }

        AssetDatabase.SaveAssets();
        Debug.Log(Log + $"最適化: 平面反射レンダラー #{index} / 影ありスポット {kept}（影を外した {cut}）/ 影を落とさない小物 {small}");
    }

    static int EnsureLayer(string name)
    {
        int existing = LayerMask.NameToLayer(name);
        if (existing >= 0) return existing;
        var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tm.FindProperty("layers");
        for (int i = 8; i < layers.arraySize; i++)
        {
            var el = layers.GetArrayElementAtIndex(i);
            if (string.IsNullOrEmpty(el.stringValue)) { el.stringValue = name; tm.ApplyModifiedProperties(); return i; }
        }
        Debug.LogWarning(Log + "空きレイヤーがありません: " + name);
        return -1;
    }

    // ----------------------------------------------------- graphics settings
    // ゲーム内グラフィック設定（F1/Esc）。シーンに置いて、切り替え対象への参照を渡す
    static TokumuGraphicsSettings EnsureGraphicsSettings()
    {
        var go = GameObject.Find("GraphicsSettings") ?? new GameObject("GraphicsSettings");
        if (!go.TryGetComponent<TokumuGraphicsSettings>(out var gs)) gs = go.AddComponent<TokumuGraphicsSettings>();
        var feats = RendererFeatures();
        gs.volumetricFeature = feats.OfType<TokumuVolumetricFeature>().FirstOrDefault();
        gs.floorOverlayFeature = feats.OfType<TokumuPreTransparentFeature>().FirstOrDefault();
        gs.ssaoFeature = feats.OfType<ScreenSpaceAmbientOcclusion>().FirstOrDefault();
        gs.planarReflection = UnityEngine.Object.FindAnyObjectByType<TokumuPlanarReflection>();
        // 影の候補: 暗いデスクライト以外のスポット。今影を落としているもの（重要）→ 床置きアップライトの順、それぞれ明るい順
        var spots = GameObject.Find("Lights_FromBlender").GetComponentsInChildren<Light>(true)
            .Where(l => l.type == LightType.Spot && l.spotAngle < 120f && l.intensity >= 5f).ToList();   // 150° はエリアライトの代用
        gs.shadowLights = spots.OrderByDescending(l => l.shadows != LightShadows.None ? 1 : 0).ThenByDescending(l => l.intensity).ToList();
        gs.dustObjects = GameObject.Find("Lights_FromBlender").GetComponentsInChildren<ParticleSystem>(true)
            .Where(ps => ps.name == "Dust").Select(ps => ps.gameObject).ToList();
        EditorUtility.SetDirty(gs);
        Debug.Log(Log + $"グラフィック設定: 影の候補 {gs.shadowLights.Count} 灯 / ほこり {gs.dustObjects.Count}");
        return gs;
    }

    static List<ScriptableRendererFeature> RendererFeatures()
    {
        var rpa = (QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
        var data = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(rpa) as ScriptableRendererData[];
        return data.Where(d => d != null).SelectMany(d => d.rendererFeatures).ToList();
    }

    // プリセットごとの負荷（1920x1080、6 カメラ平均）。シーンとアセットは計測後に元へ戻す
    static void PerfPresets(string dir)
    {
        if (!OpenOurScene()) return;
        Directory.CreateDirectory(dir);
        var gs = EnsureGraphicsSettings();
        var cams = GameObject.Find("Cameras_FromBlender").GetComponentsInChildren<Camera>(true).ToList();
        var rpa = (QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
        // 元の状態
        float scale0 = rpa.renderScale; var filter0 = rpa.upscalingFilter;
        var vol = gs.volumetricFeature;
        bool volOn0 = vol != null && vol.isActive;
        int st0 = vol ? vol.settings.steps : 0, ds0 = vol ? vol.settings.downsample : 4;
        bool ovOn0 = gs.floorOverlayFeature == null || gs.floorOverlayFeature.isActive;
        bool aoOn0 = gs.ssaoFeature == null || gs.ssaoFeature.isActive;
        bool prOn0 = gs.planarReflection && gs.planarReflection.enabled;
        float prRes0 = gs.planarReflection ? gs.planarReflection.resolutionScale : 0.5f;
        var shadows0 = gs.shadowLights.Select(l => l.shadows).ToList();
        var dust0 = gs.dustObjects.Select(d => d.activeSelf).ToList();
        var aa0 = cams.Select(c => c.GetUniversalAdditionalCameraData().antialiasing).ToList();

        var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        void Sync()
        {
            var prev = RenderTexture.active; RenderTexture.active = rt;
            probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); probe.Apply();
            RenderTexture.active = prev;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("preset," + string.Join(",", cams.Select((c, i) => "cam" + i)) + ",avg");
        ShaderUtil.allowAsyncCompilation = false;
        try
        {
            var cases = new List<(string, TokumuGraphicsSettings.Options)>();
            foreach (var pr in new[] { TokumuGraphicsSettings.Preset.Low, TokumuGraphicsSettings.Preset.Medium, TokumuGraphicsSettings.Preset.High, TokumuGraphicsSettings.Preset.Ultra })
                cases.Add((pr.ToString(), TokumuGraphicsSettings.PresetOptions(pr)));
            var mStp = TokumuGraphicsSettings.PresetOptions(TokumuGraphicsSettings.Preset.Medium); mStp.upscaler = TokumuGraphicsSettings.Upscaler.STP;
            cases.Add(("Medium_STP", mStp));
            var mNoAo = TokumuGraphicsSettings.PresetOptions(TokumuGraphicsSettings.Preset.Medium); mNoAo.ssao = TokumuGraphicsSettings.SSAO.Off;
            cases.Add(("Medium_noSSAO", mNoAo));
            var hStp = TokumuGraphicsSettings.PresetOptions(TokumuGraphicsSettings.Preset.High); hStp.upscaler = TokumuGraphicsSettings.Upscaler.STP;
            cases.Add(("High_STP", hStp));
            foreach (var (p, opt) in cases)
            {
                gs.Apply(opt);
                var row = new List<double>();
                for (int ci = 0; ci < cams.Count; ci++)
                {
                    var cam = cams[ci];
                    bool was = cam.gameObject.activeSelf; cam.gameObject.SetActive(true);
                    cam.targetTexture = rt;
                    for (int w = 0; w < 4; w++) cam.Render();
                    Sync();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int k = 0; k < 15; k++) cam.Render();
                    Sync(); sw.Stop();
                    row.Add(sw.Elapsed.TotalMilliseconds / 15);
                    if (ci == 0 || ci == 3 || ci == 5)
                    {
                        // 見た目の確認用に保存
                        cam.Render();
                        var prev = RenderTexture.active; RenderTexture.active = rt;
                        var tex = new Texture2D(1920, 1080, TextureFormat.RGBA32, false, false);
                        tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); tex.Apply();
                        RenderTexture.active = prev;
                        File.WriteAllBytes(Path.Combine(dir, $"{p}_cam{ci}.png"), tex.EncodeToPNG());
                        UnityEngine.Object.DestroyImmediate(tex);
                    }
                    cam.targetTexture = null; cam.gameObject.SetActive(was);
                }
                sb.AppendLine(p + "," + string.Join(",", row.Select(x => x.ToString("F1"))) + "," + row.Average().ToString("F1"));
                Debug.Log(Log + $"プリセット {p}: 平均 {row.Average():F1} ms");
            }
        }
        finally
        {
            ShaderUtil.allowAsyncCompilation = true;
            rpa.renderScale = scale0; rpa.upscalingFilter = filter0;
            if (vol) { vol.SetActive(volOn0); vol.settings.steps = st0; vol.settings.downsample = ds0; }
            if (gs.floorOverlayFeature) gs.floorOverlayFeature.SetActive(ovOn0);
            if (gs.ssaoFeature) { gs.ssaoFeature.SetActive(aoOn0); TuneSSAO(gs.ssaoFeature); }
            if (gs.planarReflection) { gs.planarReflection.resolutionScale = prRes0; gs.planarReflection.enabled = prOn0; }
            for (int i = 0; i < gs.shadowLights.Count; i++) gs.shadowLights[i].shadows = shadows0[i];
            for (int i = 0; i < gs.dustObjects.Count; i++) gs.dustObjects[i].SetActive(dust0[i]);
            for (int i = 0; i < cams.Count; i++) cams[i].GetUniversalAdditionalCameraData().antialiasing = aa0[i];
            rt.Release(); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(probe);
        }
        File.WriteAllText(Path.Combine(dir, "presets.csv"), sb.ToString(), new System.Text.UTF8Encoding(true));
        Debug.Log(Log + "プリセット計測完了");
    }

    // ------------------------------------------------------------------ perf
    // 機能ごとの負荷を測る: 各シーンカメラで 1920x1080 に N 回描画し、GPU 完了待ちまでの平均 ms
    static void Perf(string dir)
    {
        if (!OpenOurScene()) return;
        Directory.CreateDirectory(dir);
        var cams = GameObject.Find("Cameras_FromBlender").GetComponentsInChildren<Camera>(true).ToList();
        var rpa = (QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
        var data = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(rpa) as ScriptableRendererData[];
        Debug.Log(Log + "負荷計測: パイプライン " + rpa.name);
        var feats = data.Where(d => d != null).SelectMany(d => d.rendererFeatures).ToList();
        var vol = feats.OfType<TokumuVolumetricFeature>().FirstOrDefault();
        var pre = feats.OfType<TokumuPreTransparentFeature>().FirstOrDefault();
        var planar = UnityEngine.Object.FindAnyObjectByType<TokumuPlanarReflection>();
        var shadowLights = GameObject.Find("Lights_FromBlender").GetComponentsInChildren<Light>(true).Where(l => l.shadows != LightShadows.None).ToList();
        var holo = new[] { GameObject.Find("球形ディスプレイ_球体"), GameObject.Find("情報球_投射光") }.Where(g => g != null).ToList();
        var dust = GameObject.Find("Lights_FromBlender").GetComponentsInChildren<ParticleSystem>(true).Select(p => p.gameObject).ToList();
        var glassMats = AssetDatabase.FindAssets("t:Material", new[] { Root }).Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(m => m != null && m.shader != null && m.shader.name == "Tokumu/Glass").ToList();
        var glassSteps = glassMats.Select(m => m.GetFloat("_SSRSteps")).ToList();

        void All(bool on)
        {
            vol?.SetActive(on); pre?.SetActive(on);
            if (planar) planar.enabled = on;
            foreach (var l in shadowLights) l.shadows = on ? LightShadows.Soft : LightShadows.None;
            foreach (var g in holo) g.SetActive(on);
            foreach (var g in dust) g.SetActive(on);
            for (int i = 0; i < glassMats.Count; i++) glassMats[i].SetFloat("_SSRSteps", on ? glassSteps[i] : 0f);
        }

        var cases = new List<(string name, Action off, Action on)>
        {
            ("全部あり", () => { }, () => { }),
            ("ボリューメトリックなし", () => vol?.SetActive(false), () => vol?.SetActive(true)),
            ("平面反射なし", () => { if (planar) planar.enabled = false; }, () => { if (planar) planar.enabled = true; }),
            ("床オーバーレイなし", () => pre?.SetActive(false), () => pre?.SetActive(true)),
            ("ガラスSSRなし", () => { foreach (var m in glassMats) m.SetFloat("_SSRSteps", 0f); }, () => { for (int i = 0; i < glassMats.Count; i++) glassMats[i].SetFloat("_SSRSteps", glassSteps[i]); }),
            ("追加ライトの影なし", () => { foreach (var l in shadowLights) l.shadows = LightShadows.None; }, () => { foreach (var l in shadowLights) l.shadows = LightShadows.Soft; }),
            ("ホログラム・ほこりなし", () => { foreach (var g in holo.Concat(dust)) g.SetActive(false); }, () => { foreach (var g in holo.Concat(dust)) g.SetActive(true); }),
            ("全部なし", () => All(false), () => All(true)),
        };

        var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        void Sync() { var prev = RenderTexture.active; RenderTexture.active = rt; probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); probe.Apply(); RenderTexture.active = prev; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("case," + string.Join(",", cams.Select((c, i) => "cam" + i)) + ",avg");
        ShaderUtil.allowAsyncCompilation = false;
        try
        {
            foreach (var (name, off, on) in cases)
            {
                off();
                var row = new List<double>();
                foreach (var cam in cams)
                {
                    bool was = cam.gameObject.activeSelf; cam.gameObject.SetActive(true);
                    cam.targetTexture = rt;
                    for (int w = 0; w < 4; w++) cam.Render();
                    Sync();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    const int N = 15;
                    for (int k = 0; k < N; k++) cam.Render();
                    Sync();
                    sw.Stop();
                    row.Add(sw.Elapsed.TotalMilliseconds / N);
                    cam.targetTexture = null; cam.gameObject.SetActive(was);
                }
                on();
                sb.AppendLine(name + "," + string.Join(",", row.Select(x => x.ToString("F1"))) + "," + row.Average().ToString("F1"));
                Debug.Log(Log + $"負荷 {name}: 平均 {row.Average():F1} ms");
            }
        }
        finally
        {
            ShaderUtil.allowAsyncCompilation = true;
            All(true);
            rt.Release(); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(probe);
        }
        File.WriteAllText(Path.Combine(dir, "perf.csv"), sb.ToString(), new System.Text.UTF8Encoding(true));
        Debug.Log(Log + "負荷計測完了");
    }

    static void CaptureAll(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !OpenOurScene()) return;
        ConfigurePipeline();
        AssignFloorOverlay();
        EnsurePlanarReflection();
        ReapplyAllMaterials();
        EnsureHologramLight();
        EnsureDust();
        EnsureHoloBeam();
        OptimizePerf();
        EnsureGraphicsSettings();
        TuneLook();
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        ShaderUtil.allowAsyncCompilation = false;
        try
        {
            var main = GameObject.Find("Cameras_FromBlender").GetComponentsInChildren<Camera>(true).ToList();
            var review = GameObject.Find("ReviewCameras").GetComponentsInChildren<Camera>(true).ToList();
            var pr = UnityEngine.Object.FindAnyObjectByType<TokumuPlanarReflection>();
            if (pr != null) pr.renderCount = 0;
            Capture(main, dir, "scam");
            if (pr != null)
            {
                // 検証: 最後に描いた反射テクスチャを書き出す
                Debug.Log(Log + $"平面反射の描画回数: {pr.renderCount}");
                for (int i = 0; i < pr.planeCount; i++)
                {
                    var rt = pr.PlaneTexture(i);
                    if (rt == null) continue;
                    var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(rt, tmp);
                    var prev = RenderTexture.active; RenderTexture.active = tmp;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
                    RenderTexture.active = prev; RenderTexture.ReleaseTemporary(tmp);
                    File.WriteAllBytes(Path.Combine(dir, $"planar{i}.png"), tex.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }
            Capture(review, dir, "review");
            CaptureCalibration(dir);
            if (File.Exists(Path.Combine(dir, "debug_display")))
            {
                // 検証: 望遠で画面に寄る（粒の模様が見える倍率）と、中くらいの倍率
                var shots = new List<(string target, int fromCam, float fov, Vector3 offset)>
                {
                    ("湾曲ディスプレイ", 0, 2.5f, Vector3.zero),
                    ("湾曲ディスプレイ", 0, 9f, Vector3.zero),
                    ("湾曲ディスプレイ", 3, 6f, Vector3.zero),
                    ("補助モニター_東", 0, 2.5f, Vector3.zero),
                    ("司令官デスク_画面", 2, 4f, Vector3.zero),
                    ("司令官デスク_画面", 0, 0.6f, new Vector3(0, 0.3f, 0)),
                    ("補助モニター_東", 0, 0.35f, Vector3.zero),
                };
                int si = 0;
                foreach (var (target, fromCam, fov, off) in shots)
                {
                    var tg = GameObject.Find(target);
                    if (tg == null) { si++; continue; }
                    var b = tg.GetComponent<Renderer>().bounds;
                    var cgo = new GameObject("__dispcam") { hideFlags = HideFlags.DontSave };
                    var c = cgo.AddComponent<Camera>();
                    c.CopyFrom(main[Mathf.Min(fromCam, main.Count - 1)]);
                    cgo.transform.position = main[Mathf.Min(fromCam, main.Count - 1)].transform.position;
                    cgo.transform.LookAt(b.center + off);
                    c.fieldOfView = fov;
                    try { Capture(new List<Camera> { c }, dir, $"disp{si}_"); } finally { UnityEngine.Object.DestroyImmediate(cgo); }
                    si++;
                }
            }
            if (File.Exists(Path.Combine(dir, "debug_holo")))
            {
                // 検証: 情報球を近くから撮る（cam5 の方向から 4.5m）
                var sb = GameObject.Find("球形ディスプレイ_球体").GetComponent<Renderer>().bounds;
                var cgo = new GameObject("__holocam") { hideFlags = HideFlags.DontSave };
                var c = cgo.AddComponent<Camera>();
                c.CopyFrom(main[Mathf.Min(5, main.Count - 1)]);
                var from = main[Mathf.Min(5, main.Count - 1)].transform.position - sb.center; from.y = 0;
                cgo.transform.position = sb.center + from.normalized * 4.5f + Vector3.down * 0.5f;
                cgo.transform.LookAt(sb.center + Vector3.down * 0.35f);
                c.fieldOfView = 45f;
                try { Capture(new List<Camera> { c }, dir, "holo"); } finally { UnityEngine.Object.DestroyImmediate(cgo); }
            }
            if (File.Exists(Path.Combine(dir, "debug_dust")))
            {
                // 検証: 情報球を照らすスポットの光の柱を近くから撮る（保存しない一時カメラ）
                var cgo = new GameObject("__dustcam") { hideFlags = HideFlags.DontSave };
                var c = cgo.AddComponent<Camera>();
                c.CopyFrom(main[0]);
                c.fieldOfView = 50f;
                var spot = GameObject.Find("Lights_FromBlender").GetComponentsInChildren<Light>(true)
                    .Where(x => x.type == LightType.Spot && x.shadows != LightShadows.None)
                    .OrderByDescending(x => x.name.Contains("司令") ? 1 : 0).ThenByDescending(x => x.intensity).First();
                var mid = spot.transform.position + spot.transform.forward * 5f;
                cgo.transform.position = mid + spot.transform.right * 2.2f - spot.transform.forward * 0.5f;
                cgo.transform.LookAt(mid);
                Debug.Log(Log + "ほこり検証カメラ: " + spot.name + " " + cgo.transform.position);
                try { Capture(new List<Camera> { c }, dir, "dust"); } finally { UnityEngine.Object.DestroyImmediate(cgo); }
            }
            if (File.Exists(Path.Combine(dir, "debug_planar")))
            {
                Shader.SetGlobalFloat("_TokumuPlanarDebug", 1f);
                try { Capture(main, dir, "dbg"); } finally { Shader.SetGlobalFloat("_TokumuPlanarDebug", 0f); }
            }
        }
        finally { ShaderUtil.allowAsyncCompilation = true; }
        Debug.Log(Log + "ライティング撮影完了");
    }

    // ------------------------------------------------------------------ glass
    // 透過のある材質は Tokumu/Glass に置き換える（材質ごとの厚み・汚れ等）
    class GlassPreset { public float thickness = 0.012f, wave = 0.35f, smudge = 0.25f, scatter = 0f, dispersion = 0.02f, frost = 1f; }
    static GlassPreset PresetFor(string name)
    {
        switch (name)
        {
            case "ガラス_曇り": return new GlassPreset { thickness = 0.008f, wave = 0f, smudge = 0.1f, scatter = 0.2f, dispersion = 0f, frost = 2.6f };
            case "黒ガラス床": return new GlassPreset { thickness = 0.02f, wave = 0.1f, smudge = 0.35f, scatter = 0f, dispersion = 0.01f };
            case "レンズ_眼鏡": return new GlassPreset { thickness = 0.002f, wave = 0f, smudge = 0.3f, dispersion = 0.03f };
            default: return new GlassPreset();
        }
    }

    // 下が空洞の黒ガラス床は、透過しても黒になるだけなので「拡散＋鏡面」の不透明として描く
    // （不透明テクスチャ／深度に入るので、他のガラス越しにも見え、URP の SSR で映り込みが付く）
    static readonly string[] OpaqueGlass = { "黒ガラス床" };
    static bool IsGlass(MatEntry m) => m.transmission > 0.5f && !OpaqueGlass.Contains(m.name);

    static void ApplyGlass(Material mat, MatEntry m)
    {
        var sh = Shader.Find("Tokumu/Glass");
        if (sh == null) { Debug.LogError(Log + "Tokumu/Glass シェーダーが見つかりません"); return; }
        mat.shader = sh;
        var p = PresetFor(m.name);
        // Blender(Cycles) は屈折のたびに Base Color を掛ける → 1 枚（入射・出射の 2 面）で 2 乗
        mat.SetColor("_TintColor", LinToGamma(m.baseColor[0] * m.baseColor[0], m.baseColor[1] * m.baseColor[1], m.baseColor[2] * m.baseColor[2]));
        mat.SetFloat("_TintDistance", p.thickness);
        mat.SetFloat("_Thickness", p.thickness);
        mat.SetFloat("_TransmissionWeight", Mathf.Clamp01(m.transmission));
        mat.SetColor("_BaseColor", LinToGamma(m.baseColor[0], m.baseColor[1], m.baseColor[2]));
        mat.SetFloat("_IOR", 1.5f);
        mat.SetFloat("_Dispersion", p.dispersion);
        mat.SetFloat("_Roughness", m.roughness);
        mat.SetFloat("_FrostBlur", p.frost);
        mat.SetFloat("_Scatter", p.scatter);
        mat.SetFloat("_WaveStrength", p.wave);
        mat.SetFloat("_SmudgeStrength", p.smudge);
        mat.renderQueue = (int)RenderQueue.Transparent;
        mat.shaderKeywords = new string[0];
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
    }

    static void ConfigurePipeline()
    {
        var rp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (rp == null) return;
        var so = new SerializedObject(rp);
        so.FindProperty("m_RequireOpaqueTexture").boolValue = true;
        so.FindProperty("m_RequireDepthTexture").boolValue = true;
        so.FindProperty("m_OpaqueDownsampling").intValue = 0;   // 屈折の解像度を落とさない
        // 影ありスポット 10 灯が 2048 のアトラスに収まらず、URP が毎フレーム「解像度を下げた」ログを出していた（ログ肥大）。
        // 4096 にすると全灯が本来の解像度で入り、光の筋の影の段差も減る
        so.FindProperty("m_AdditionalLightsShadowmapResolution").intValue = 4096;
        // AA は後処理（FXAA/SMAA/STP）で行う。MSAA 8x は内蔵 GPU に重く、STP/TAA とも両立しない
        so.FindProperty("m_MSAA").intValue = 1;
        so.FindProperty("m_LightProbeSystem").intValue = 1;     // Adaptive Probe Volumes（間接光をプローブで焼く）
        so.FindProperty("m_ProbeVolumeSHBands").intValue = 2;   // L2
        if (so.ApplyModifiedProperties()) { EditorUtility.SetDirty(rp); AssetDatabase.SaveAssets(); }

        // 透明物直前の重ね描き＋不透明テクスチャ取り直し（床の映り込みをガラス越しにも見せる）
        // ボリューメトリック光（光の筋と空気感）
        var list = so.FindProperty("m_RendererDataList");
        for (int i = 0; i < list.arraySize; i++)
        {
            var data = list.GetArrayElementAtIndex(i).objectReferenceValue as UniversalRendererData;
            if (data == null || data.name.Contains("Planar")) continue;   // 平面反射用の軽いレンダラーには足さない
            EnsureFeature<TokumuPreTransparentFeature>(data, "TokumuPreTransparent");
            var vol = EnsureFeature<TokumuVolumetricFeature>(data, "TokumuVolumetric");
            TuneVolumetric(vol);
            TuneSSAO(EnsureFeature<ScreenSpaceAmbientOcclusion>(data, "SSAO"));
        }
    }

    static T EnsureFeature<T>(UniversalRendererData data, string name) where T : ScriptableRendererFeature
    {
        var existing = data.rendererFeatures.OfType<T>().FirstOrDefault();
        if (existing != null) return existing;
        var feat = ScriptableObject.CreateInstance<T>();
        feat.name = name;
        AssetDatabase.AddObjectToAsset(feat, data);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feat, out _, out long localId);
        var dso = new SerializedObject(data);
        var feats = dso.FindProperty("m_RendererFeatures");
        var map = dso.FindProperty("m_RendererFeatureMap");
        feats.arraySize++; feats.GetArrayElementAtIndex(feats.arraySize - 1).objectReferenceValue = feat;
        map.arraySize++; map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
        dso.ApplyModifiedProperties();
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
        Debug.Log(Log + $"レンダラー機能を追加: {name} → {data.name}");
        return feat;
    }

    // 光の中に浮かぶほこり: 影を落とすスポットライトごとに、光の円錐に沿ったパーティクルを置く。
    // Particles/Simple Lit（加算）なので、光が当たっている粒だけが光る
    static void EnsureDust()
    {
        string genDir = Root + "/Generated";
        if (!AssetDatabase.IsValidFolder(genDir)) AssetDatabase.CreateFolder(Root, "Generated");
        string texPath = genDir + "/DustDot.png";
        if (!File.Exists(texPath))
        {
            var t = new Texture2D(32, 32, TextureFormat.RGBA32, false);
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
            {
                float r = new Vector2(x - 15.5f, y - 15.5f).magnitude / 15.5f;
                float a = Mathf.Clamp01(Mathf.Exp(-r * r * 4f) * 1.1f - 0.05f);
                t.SetPixel(x, y, new Color(1, 1, 1, a));
            }
            File.WriteAllBytes(texPath, t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(texPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.alphaIsTransparency = true; ti.mipmapEnabled = true; ti.wrapMode = TextureWrapMode.Clamp; ti.SaveAndReimport();
        }
        string matPath = genDir + "/DustMote.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Simple Lit")); AssetDatabase.CreateAsset(mat, matPath); }
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
        mat.SetColor("_BaseColor", new Color(0.85f, 0.85f, 0.85f, 0.55f));
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 2f);            // Additive
        mat.SetFloat("_ReceiveShadows", 1f);
        mat.SetFloat("_SoftParticlesEnabled", 0f);
        BaseShaderGUI.SetMaterialKeywords(mat, SimpleLitGUI.SetMaterialKeywords, ParticleGUI.SetMaterialKeywords);
        EditorUtility.SetDirty(mat);

        int n = 0;
        foreach (var l in GameObject.Find("Lights_FromBlender").GetComponentsInChildren<Light>(true))
        {
            if (l.type != LightType.Spot || l.shadows == LightShadows.None) continue;
            var tr = l.transform.Find("Dust");
            var go = tr ? tr.gameObject : new GameObject("Dust");
            go.transform.SetParent(l.transform, false);
            go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity;
            if (!go.TryGetComponent<ParticleSystem>(out var ps)) ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            float len = Mathf.Min(l.range, 11f);
            int count = Mathf.RoundToInt(Mathf.Clamp(len * Mathf.Tan(l.spotAngle * 0.5f * Mathf.Deg2Rad) * len * 18f, 150, 700));
            var main = ps.main;
            main.loop = true; main.prewarm = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(20f, 40f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.05f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.005f, 0.014f);
            main.startColor = new Color(1, 1, 1, 1);
            main.gravityModifier = -0.0004f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = count;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var em = ps.emission; em.rateOverTime = count / 30f;
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.ConeVolume;   // 円錐の体積全体から出す
            sh.angle = l.spotAngle * 0.5f;
            sh.radius = 0.15f;
            sh.length = len;
            var noise = ps.noise;
            noise.enabled = true; noise.strength = 0.04f; noise.frequency = 0.25f; noise.scrollSpeed = 0.05f; noise.quality = ParticleSystemNoiseQuality.Medium;
            var col = ps.colorOverLifetime; col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                         new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.15f), new GradientAlphaKey(1, 0.85f), new GradientAlphaKey(0, 1) });
            col.color = grad;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
            r.minParticleSize = 0f; r.maxParticleSize = 0.02f;
            r.sortMode = ParticleSystemSortMode.None;
            ps.Play(true);
            ps.Simulate(30f, true, true, true);   // 編集モードの撮影でも舞っている状態にする
            n++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log(Log + $"ほこり: スポットライト {n} 灯に配置");
    }

    // SSAO: APV の間接光はなめらかすぎて物が床から浮いて見えるので、接地と隙間の暗がりを足す
    static void TuneSSAO(ScreenSpaceAmbientOcclusion f)
    {
        var so = new SerializedObject(f);
        var st = so.FindProperty("m_Settings");
        st.FindPropertyRelative("Downsample").boolValue = true;        // 半解像度（設定メニューで等倍にもできる）
        st.FindPropertyRelative("AfterOpaque").boolValue = false;      // 間接光にだけ効かせる（直接光は弱く）
        st.FindPropertyRelative("Source").intValue = 0;                // Depth（法線の追加パスを描かない）
        st.FindPropertyRelative("NormalSamples").intValue = 1;         // Medium
        st.FindPropertyRelative("Intensity").floatValue = 2.2f;
        st.FindPropertyRelative("DirectLightingStrength").floatValue = 0.4f;
        st.FindPropertyRelative("Radius").floatValue = 0.4f;
        st.FindPropertyRelative("Samples").intValue = 1;               // Medium（8 サンプル）
        st.FindPropertyRelative("BlurQuality").intValue = 0;           // High（バイラテラル）
        st.FindPropertyRelative("Falloff").floatValue = 60f;
        st.FindPropertyRelative("AOMethod").intValue = 1;              // Interleaved Gradient（時間方向の AA と相性が良い）
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(f);
    }

    // 空気感の調整値（ここを変えて capture で確認する）
    static void TuneVolumetric(TokumuVolumetricFeature f)
    {
        var s = f.settings;
        s.scattering = 0.024f;
        s.extinction = 0.03f;
        s.anisotropy = 0.45f;
        s.intensity = 1.4f;
        s.steps = 20;
        s.maxDistance = 40f;
        s.noiseAmount = 0.55f;
        s.noiseFrequency = 0.35f;
        s.downsample = 4;
        EditorUtility.SetDirty(f);
        f.Create();
    }

    // ガラス周りの局所反射プローブ（部屋全体の 1 基では窓に正しい映り込みが出ないため）
    static void EnsureLocalProbe(string name, float[] eyeB, float[] boxMinB, float[] boxMaxB, int importance, Color bg)
    {
        var root = GameObject.Find("ReflectionProbes");
        if (root == null) root = new GameObject("ReflectionProbes");
        var t = root.transform.Find(name);
        var go = t ? t.gameObject : new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.transform.position = B2U(eyeB);
        var a = B2U(boxMinB); var b = B2U(boxMaxB);
        var bounds = new Bounds((a + b) * 0.5f, new Vector3(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
        var p = go.GetComponent<ReflectionProbe>();
        if (p == null) p = go.AddComponent<ReflectionProbe>();
        p.mode = ReflectionProbeMode.Baked;
        p.boxProjection = true;
        p.center = bounds.center - go.transform.position;
        p.size = bounds.size;
        p.importance = importance;
        p.blendDistance = 1.0f;
        p.resolution = 512;
        p.hdr = true;
        p.intensity = 1f;
        p.clearFlags = ReflectionProbeClearFlags.SolidColor;
        p.backgroundColor = bg;
        string dir = Path.GetDirectoryName(ScenePath).Replace(Path.DirectorySeparatorChar, '/') + "/TokumuShireishitsu";
        bool ok = Lightmapping.BakeReflectionProbe(p, dir + "/" + name + ".exr");
        Debug.Log(Log + $"局所反射プローブ {name}: {(ok ? "ベイク成功" : "ベイク失敗")}");
    }

    // 不透明の黒ガラス床に自前 SSR のオーバーレイを重ねる（同じ面を追加マテリアルでもう一度描く）
    static void AssignFloorOverlay()
    {
        var sh = Shader.Find("Tokumu/SSROverlay");
        if (sh == null) { Debug.LogError(Log + "Tokumu/SSROverlay が見つかりません"); return; }
        var list = JsonUtility.FromJson<MatList>(File.ReadAllText(Root + "/materials.json"));
        var rough = list.materials.GroupBy(m => Sanitize(m.name)).ToDictionary(g => g.Key, g => g.First().roughness);
        string genDir = Root + "/Generated";
        if (!AssetDatabase.IsValidFolder(genDir)) AssetDatabase.CreateFolder(Root, "Generated");

        int n = 0;
        foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include))
        {
            if (r == null || r.name.StartsWith(OverlayPrefix)) continue;
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            // 旧方式（同じレンダラーへのマテリアル追加）の名残を外す
            var mats = r.sharedMaterials;
            if (mats.Any(x => x != null && x.shader == sh))
            {
                r.sharedMaterials = mats.Where(x => x == null || x.shader != sh).ToArray();
                mats = r.sharedMaterials;
                PrefabUtility.RecordPrefabInstancePropertyModifications(r);
            }
            for (int i = 0; i < Mathf.Min(mats.Length, mf.sharedMesh.subMeshCount); i++)
            {
                var src = mats[i];
                if (src == null || !SSRTargets.Contains(src.name)) continue;
                // 対象サブメッシュを抜き出し、床の高さごとに分けた子オブジェクトに映り込みオーバーレイを載せる
                // （1 枚のメッシュに 0m・-0.2m・4m などの床が混ざっていると、平面反射が 1 面分しか合わない）
                foreach (Transform old in r.transform.Cast<Transform>().Where(c => c.name.StartsWith(OverlayPrefix)).ToList())
                    UnityEngine.Object.DestroyImmediate(old.gameObject);
                var parts = SplitByFloorHeight(ExtractSubmesh(mf.sharedMesh, i), mf.sharedMesh.name + "_sub" + i);
                var grs = new List<MeshRenderer>();
                for (int k = 0; k < parts.Count; k++)
                {
                    var go = new GameObject($"{OverlayPrefix}{i}_{k}");
                    go.transform.SetParent(r.transform, false);
                    string meshPath = $"{genDir}/{Sanitize(r.name)}_{i}_{k}_SSR.asset";
                    AssetDatabase.DeleteAsset(meshPath);
                    AssetDatabase.CreateAsset(parts[k], meshPath);
                    go.AddComponent<MeshFilter>().sharedMesh = parts[k];
                    var gr = go.AddComponent<MeshRenderer>();
                    gr.shadowCastingMode = ShadowCastingMode.Off;
                    gr.receiveShadows = false;
                    gr.lightProbeUsage = LightProbeUsage.Off;
                    gr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    grs.Add(gr);
                }

                string path = $"{genDir}/{src.name}_SSR.mat";
                var ov = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (ov == null) { ov = new Material(sh); AssetDatabase.CreateAsset(ov, path); }
                ov.shader = sh;
                ov.SetFloat("_Roughness", rough.TryGetValue(src.name, out var rv) ? rv : 0.05f);
                ov.SetFloat("_SmudgeStrength", src.name == "床_黒研磨" ? 0.5f : 0.35f);
                ov.SetFloat("_PlanarBlur", 9f);
                ov.SetFloat("_PlanarDistort", 0.004f);
                ov.renderQueue = (int)RenderQueue.Transparent - 1;
                EditorUtility.SetDirty(ov);
                foreach (var gr in grs) gr.sharedMaterial = ov;
                n += grs.Count;
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log(Log + $"床の反射オーバーレイを割り当て: {n} 件");
    }

    // 床の平面反射（SSR オーバーレイの子オブジェクトを受け手にする）
    static void EnsurePlanarReflection()
    {
        var go = GameObject.Find("PlanarReflections") ?? new GameObject("PlanarReflections");
        if (!go.TryGetComponent<TokumuPlanarReflection>(out var pr)) pr = go.AddComponent<TokumuPlanarReflection>();
        pr.receivers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include)
            .Where(r => r.name.StartsWith(OverlayPrefix)).ToList();
        pr.resolutionScale = 0.6f;
        EditorUtility.SetDirty(pr);
        pr.enabled = false; pr.enabled = true;   // 受け手を組み直す
        Debug.Log(Log + $"平面反射: 受け手 {pr.receivers.Count} / 面 {pr.Describe()}");
    }

    const string OverlayPrefix = "__SSROverlay_";
    // 自前 SSR を重ねる艶あり不透明面（黒ガラス床＋メインの黒研磨床）
    static readonly string[] SSRTargets = { "黒ガラス床", "床_黒研磨" };

    // 上向きの三角形を床の高さ（2cm 刻み）ごとにまとめる。1m² 未満の高さと上向きでない面は「その他」（SSR）に
    static List<Mesh> SplitByFloorHeight(Mesh m, string baseName)
    {
        var v = m.vertices; var nrm = m.normals; var t = m.triangles;
        var groups = new Dictionary<int, List<int>>(); var area = new Dictionary<int, float>();
        const int Rest = int.MinValue;
        for (int k = 0; k < t.Length; k += 3)
        {
            Vector3 a = v[t[k]], b = v[t[k + 1]], c = v[t[k + 2]];
            var cr = Vector3.Cross(b - a, c - a); float ar = cr.magnitude * 0.5f;
            int key = (ar > 1e-6f && cr.y / (2f * ar) > 0.995f) ? Mathf.RoundToInt((a.y + b.y + c.y) / 3f * 50f) : Rest;
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<int>();
            l.Add(k); area[key] = (area.TryGetValue(key, out var s0) ? s0 : 0f) + ar;
        }
        var merged = new Dictionary<int, List<int>>();
        foreach (var kv in groups)
        {
            int key = (kv.Key != Rest && area[kv.Key] >= 1f) ? kv.Key : Rest;
            if (!merged.TryGetValue(key, out var l)) merged[key] = l = new List<int>();
            l.AddRange(kv.Value);
        }
        var result = new List<Mesh>();
        foreach (var kv in merged.OrderBy(x => x.Key))
        {
            var map = new Dictionary<int, int>(); var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nt = new List<int>();
            foreach (int k in kv.Value)
                for (int j = 0; j < 3; j++)
                {
                    int vi = t[k + j];
                    if (!map.TryGetValue(vi, out int ni)) { ni = nv.Count; map[vi] = ni; nv.Add(v[vi]); nn.Add(nrm[vi]); }
                    nt.Add(ni);
                }
            var pm = new Mesh { name = baseName + (kv.Key == Rest ? "_rest" : $"_y{kv.Key / 50f:F2}") };
            if (nv.Count > 65000) pm.indexFormat = IndexFormat.UInt32;
            pm.SetVertices(nv); pm.SetNormals(nn); pm.SetTriangles(nt, 0); pm.RecalculateBounds();
            result.Add(pm);
        }
        return result;
    }

    static Mesh ExtractSubmesh(Mesh src, int index)
    {
        var tris = src.GetTriangles(index);
        var map = new Dictionary<int, int>();
        var v = src.vertices; var nrm = src.normals;
        var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nt = new int[tris.Length];
        for (int k = 0; k < tris.Length; k++)
        {
            if (!map.TryGetValue(tris[k], out int j)) { j = nv.Count; map[tris[k]] = j; nv.Add(v[tris[k]]); nn.Add(nrm[tris[k]]); }
            nt[k] = j;
        }
        var m = new Mesh { name = src.name + "_sub" + index };
        if (nv.Count > 65000) m.indexFormat = IndexFormat.UInt32;
        m.SetVertices(nv); m.SetNormals(nn); m.SetTriangles(nt, 0); m.RecalculateBounds();
        return m;
    }

    // 参照（Cycles・グレアなし）に合わせてブルームを控えめに
    static void TuneLook()
    {
        var vol = UnityEngine.Object.FindAnyObjectByType<Volume>();
        if (vol == null || vol.sharedProfile == null) return;
        if (vol.sharedProfile.TryGet(out Bloom bl))
        {
            bl.threshold.Override(1.3f);
            bl.intensity.Override(0.25f);
            EditorUtility.SetDirty(vol.sharedProfile);
        }
    }

    // サーバー室の観察窓は二重ガラス → 専用マテリアル（_Panes = 2）をシーン上で割り当て
    static void AssignDoubleGlazing()
    {
        var go = GameObject.Find("サーバー室_観察窓");
        var r = go ? go.GetComponent<MeshRenderer>() : null;
        if (r == null) { Debug.LogWarning(Log + "観察窓が見つかりません"); return; }
        var mats = r.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
        {
            if (mats[i] == null || mats[i].shader.name != "Tokumu/Glass") continue;
            string baseName = mats[i].name.EndsWith("_二重") ? mats[i].name.Substring(0, mats[i].name.Length - 3) : mats[i].name;
            var src = AssetDatabase.FindAssets("t:Material", new[] { Root })
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(x => x != null && x.name == baseName);
            if (src == null) continue;
            string path = Path.GetDirectoryName(AssetDatabase.GetAssetPath(src)).Replace('\\', '/') + "/" + baseName + "_二重.mat";
            var dbl = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (dbl == null) { dbl = new Material(src); AssetDatabase.CreateAsset(dbl, path); }
            dbl.CopyPropertiesFromMaterial(src);
            dbl.shader = src.shader;
            dbl.SetFloat("_Panes", 2f);
            dbl.renderQueue = src.renderQueue;
            EditorUtility.SetDirty(dbl);
            mats[i] = dbl;
        }
        r.sharedMaterials = mats;
        PrefabUtility.RecordPrefabInstancePropertyModifications(r);
        AssetDatabase.SaveAssets();
        Debug.Log(Log + "二重ガラスを割り当て: " + string.Join(",", mats.Select(x => x ? x.name : "null")));
    }

    static void SetupCamera(Camera cam, Color bg)
    {
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = bg;
        cam.allowHDR = true;
        var acd = cam.GetUniversalAdditionalCameraData();
        acd.renderPostProcessing = true;
        acd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        acd.antialiasingQuality = AntialiasingQuality.High;
        acd.requiresColorOption = CameraOverrideOption.On;
        acd.requiresDepthOption = CameraOverrideOption.On;
    }

    [Serializable] class ReviewList { public ReviewCam[] cameras; }
    [Serializable] class ReviewCam { public string name; public float[] eye, target; public float fovY; }
    static Vector3 B2U(float[] b) => new Vector3(-b[0], b[2], -b[1]);

    static List<Camera> EnsureReviewCameras(Color bg)
    {
        var list = JsonUtility.FromJson<ReviewList>(File.ReadAllText(Root + "/review_cams.json"));
        var root = GameObject.Find("ReviewCameras");
        if (root == null) root = new GameObject("ReviewCameras");
        var cams = new List<Camera>();
        foreach (var rc in list.cameras)
        {
            var t = root.transform.Find(rc.name);
            var go = t ? t.gameObject : new GameObject(rc.name);
            go.transform.SetParent(root.transform, false);
            go.transform.position = B2U(rc.eye);
            go.transform.rotation = Quaternion.LookRotation(B2U(rc.target) - B2U(rc.eye), Vector3.up);
            var cam = go.GetComponent<Camera>();
            if (cam == null) cam = go.AddComponent<Camera>();
            cam.fieldOfView = rc.fovY;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            SetupCamera(cam, bg);
            go.SetActive(false);
            cams.Add(cam);
        }
        return cams;
    }

    // ガラス材質だけ再適用 → 確認カメラを撮影（フィードバック用）
    static void Review(string captureDir)
    {
        ConfigurePipeline();
        var list = JsonUtility.FromJson<MatList>(File.ReadAllText(Root + "/materials.json"));
        foreach (var m in list.materials.Where(x => x.transmission > 0.5f))
        {
            var mat = AssetDatabase.FindAssets("t:Material", new[] { Root })
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(x => x != null && x.name == Sanitize(m.name));
            if (mat != null) { ApplyLit(mat, m); EditorUtility.SetDirty(mat); Debug.Log(Log + "ガラス適用: " + mat.name + " → " + mat.shader.name); }
            else Debug.LogWarning(Log + "ガラス材質が見つかりません: " + m.name);
        }
        AssetDatabase.SaveAssets();
        if (SceneManager.GetActiveScene().path != ScenePath)
        {
            if (SceneManager.GetActiveScene().isDirty) { Debug.LogError(Log + "開いているシーンに未保存の変更があるため撮影を中止"); return; }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
        var w = JsonUtility.FromJson<SceneData>(File.ReadAllText(Root + "/scene_lights.json")).world;
        Color bg = LinToGamma(w.color[0] * w.strength, w.color[1] * w.strength, w.color[2] * w.strength);
        foreach (var c in GameObject.Find("Cameras_FromBlender")?.GetComponentsInChildren<Camera>(true) ?? new Camera[0]) SetupCamera(c, bg);
        var cams = EnsureReviewCameras(bg);
        AssignDoubleGlazing();
        AssignFloorOverlay();
        // 観察窓の前（メインルーム側）。窓に映る大画面・柱などを正しい位置で拾う
        EnsureLocalProbe("Probe_観察窓前", new[] { 17.5f, -5.0f, 2.0f }, new[] { 9.0f, -31.5f, 0.0f }, new[] { 23.0f, 0.3f, 12.0f }, 2, bg);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        if (captureDir != null)
        {
            ShaderUtil.allowAsyncCompilation = false;
            try { Capture(cams, captureDir, "review"); }
            finally { ShaderUtil.allowAsyncCompilation = true; }
        }
        Debug.Log(Log + "レビュー撮影完了");
    }

    static void SetupAll(string captureDir)
    {
        SetupModelAndMaterials();
        BuildScene(captureDir);
        Debug.Log(Log + "すべて完了");
    }

    // ------------------------------------------------------------------ data
    [Serializable] class MatList { public MatEntry[] materials; }
    [Serializable] class MatEntry
    {
        public string name;
        public float[] baseColor, emission;
        public float metallic, roughness, alpha, transmission, emissionStrength, normalStrength = 1f;
        public string baseColorMap, normalMap, metallicSmoothnessMap, emissionMap;
    }
    [Serializable] class SceneData { public LightE[] lights; public CamE[] cameras; public WorldE world; public ViewE view; }
    [Serializable] class LightE
    {
        public string name, type, collection, shape;
        public float[] position, forward, up, color;
        public float energy, radius, spotSize, spotBlend, sizeX, sizeY;
        public bool shadow;
    }
    [Serializable] class CamE
    {
        public string name;
        public float[] position, forward, up;
        public float fovY, near, far, fstop, focusDist;
        public bool isMain, dof;
    }
    [Serializable] class WorldE { public float[] color; public float strength; }
    [Serializable] class ViewE { public string transform; public float exposure; }

    static Vector3 V(float[] a) => new Vector3(a[0], a[1], a[2]);
    static Color LinToGamma(float r, float g, float b, float a = 1f) => new Color(r, g, b, a).gamma;

    // ------------------------------------------------------- model/materials
    static void SetupModelAndMaterials()
    {
        var list = JsonUtility.FromJson<MatList>(File.ReadAllText(Root + "/materials.json"));

        // テクスチャのインポート設定（まとめて）
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var m in list.materials)
            {
                SetTex(m.baseColorMap, true, false);
                SetTex(m.emissionMap, true, false);
                if (LinearEmission(m.emissionMap) != m.emissionMap) SetTex(LinearEmission(m.emissionMap), false, false);
                SetTex(m.metallicSmoothnessMap, false, false);
                SetTex(m.normalMap, false, true);
            }
        }
        finally { AssetDatabase.StopAssetEditing(); }

        // FBX インポート設定
        var imp = (ModelImporter)AssetImporter.GetAtPath(Fbx);
        imp.globalScale = 1f;
        imp.useFileScale = true;
        imp.bakeAxisConversion = false;
        imp.importCameras = false;
        imp.importLights = false;
        imp.importVisibility = true;
        imp.importBlendShapes = false;
        imp.importAnimation = false;
        imp.animationType = ModelImporterAnimationType.None;
        imp.meshCompression = ModelImporterMeshCompression.Off;
        imp.isReadable = false;
        imp.optimizeMeshPolygons = true;
        imp.optimizeMeshVertices = true;
        imp.indexFormat = ModelImporterIndexFormat.Auto;
        imp.importNormals = ModelImporterNormals.Import;
        imp.importTangents = ModelImporterTangents.Import;
        imp.generateSecondaryUV = false;
        imp.addCollider = false;
        imp.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        imp.materialLocation = ModelImporterMaterialLocation.InPrefab;

        string matDir = Root + "/Materials";
        if (!AssetDatabase.IsValidFolder(matDir)) AssetDatabase.CreateFolder(Root, "Materials");
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        ConfigurePipeline();
        foreach (var m in list.materials)
        {
            string path = $"{matDir}/{Sanitize(m.name)}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(lit) { name = m.name }; AssetDatabase.CreateAsset(mat, path); }
            else if (!IsGlass(m)) mat.shader = lit;
            ApplyLit(mat, m);
            EditorUtility.SetDirty(mat);
            imp.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), m.name), mat);
        }
        AssetDatabase.SaveAssets();
        imp.SaveAndReimport();

        var unmapped = AssetDatabase.LoadAllAssetsAtPath(Fbx).OfType<Material>().Select(x => x.name).ToArray();
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
        var rends = model.GetComponentsInChildren<MeshRenderer>(true);
        long tris = 0;
        foreach (var f in model.GetComponentsInChildren<MeshFilter>(true))
            if (f.sharedMesh) for (int i = 0; i < f.sharedMesh.subMeshCount; i++) tris += f.sharedMesh.GetIndexCount(i) / 3;
        var b = new Bounds(); bool first = true;
        foreach (var r in rends) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
        int missing = rends.Sum(r => r.sharedMaterials.Count(x => x == null));
        Debug.Log(Log + $"マテリアル {list.materials.Length} 件作成 / 未リマップ {unmapped.Length} 件 [{string.Join(",", unmapped)}] / " +
                  $"レンダラー {rends.Length} / 三角形 {tris} / 空スロット {missing} / bounds min{b.min} max{b.max}");
    }

    static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    static Texture2D Tex(string rel) =>
        string.IsNullOrEmpty(rel) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/" + rel);

    // 画面の発光テクスチャはリニア EXR 版を優先する。
    // 8bit sRGB のままだとミップマップがガンマ空間で平均され、遠目に LED の粒が暗く潰れる（Cycles の約 0.6 倍）
    static string LinearEmission(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return rel;
        string exr = Path.ChangeExtension(rel, null) + "_Linear.exr";
        return File.Exists(Root + "/" + exr) ? exr : rel;
    }

    static void SetTex(string rel, bool srgb, bool normal)
    {
        if (string.IsNullOrEmpty(rel)) return;
        var ti = AssetImporter.GetAtPath(Root + "/" + rel) as TextureImporter;
        if (ti == null) return;
        ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        ti.sRGBTexture = srgb;
        ti.wrapMode = TextureWrapMode.Repeat;
        ti.maxTextureSize = 4096;
        ti.mipmapEnabled = true;
        ti.anisoLevel = 8;
        ti.alphaSource = TextureImporterAlphaSource.FromInput;
        ti.alphaIsTransparency = false;
        ti.textureCompression = TextureImporterCompression.CompressedHQ;
        ti.SaveAndReimport();
    }

    // ------------------------------------------------------------ hologram
    static readonly string[] HologramMats = { "情報球_表示" };

    static void ApplyHologram(Material mat, MatEntry m)
    {
        var sh = Shader.Find("Tokumu/Hologram");
        if (sh == null) { Debug.LogError(Log + "Tokumu/Hologram シェーダーが見つかりません"); return; }
        mat.shader = sh;
        mat.SetTexture("_MapTex", Tex(LinearEmission(m.emissionMap)));
        mat.SetColor("_Color", new Color(0.22f, 0.68f, 1.9f));
        mat.SetFloat("_Intensity", 2.6f);
        mat.SetFloat("_Core", 0.04f);
        mat.SetFloat("_Grid", 0f);          // 経緯線はテクスチャ側にある
        mat.SetFloat("_Coast", 1.6f);
        mat.SetFloat("_BackFace", 0.22f);
        mat.SetFloat("_Rim", 1.2f);
        mat.renderQueue = (int)RenderQueue.Transparent + 10;
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
    }

    // 情報球: ホログラムの光で周囲（台座・床）を淡く照らすポイントライト
    static void EnsureHologramLight()
    {
        var sphere = GameObject.Find("球形ディスプレイ_球体");
        if (sphere == null) { Debug.LogWarning(Log + "情報球が見つかりません"); return; }
        if (sphere.TryGetComponent<MeshRenderer>(out var sr)) { sr.shadowCastingMode = ShadowCastingMode.Off; sr.receiveGI = ReceiveGI.LightProbes; }
        var go = GameObject.Find("Light_情報球") ?? new GameObject("Light_情報球");
        go.transform.position = sphere.GetComponent<Renderer>().bounds.center;
        if (!go.TryGetComponent<Light>(out var li)) li = go.AddComponent<Light>();
        li.type = LightType.Point;
        li.color = new Color(0.45f, 0.78f, 1f);
        li.intensity = 1.6f;
        li.range = 7f;
        li.shadows = LightShadows.None;
        li.lightmapBakeType = LightmapBakeType.Realtime;
        if (!go.TryGetComponent<TokumuHologramLight>(out var hl)) hl = go.AddComponent<TokumuHologramLight>();
        hl.baseIntensity = li.intensity;
        Debug.Log(Log + "情報球ホログラム: ライト " + go.transform.position);
    }

    // 情報球の投射光: 基部リングから立ち昇り、球の下半分を包む光のカーテン（円錐台）＋立ち昇る光の粒
    static void EnsureHoloBeam()
    {
        var sphere = GameObject.Find("球形ディスプレイ_球体");
        var ring = GameObject.Find("情報球_基部リング");
        if (sphere == null || ring == null) { Debug.LogWarning(Log + "情報球または基部リングが見つかりません"); return; }
        var sb = sphere.GetComponent<Renderer>().bounds;
        var rbnd = ring.GetComponent<Renderer>().bounds;
        float R = sb.extents.y;   // 球は Y 軸回りに回転して置かれているので、外接箱の x は √2 倍に膨らむ
        float rBottom = rbnd.extents.x * 0.96f;
        float yBottom = rbnd.max.y - 0.02f;
        float yTop = sb.center.y + R * 0.35f;
        float rTop = R * 1.16f;
        string genDir = Root + "/Generated";
        if (!AssetDatabase.IsValidFolder(genDir)) AssetDatabase.CreateFolder(Root, "Generated");

        // 円錐台メッシュ（継ぎ目は頂点を複製、UV.x = 周方向、UV.y = 高さ）
        const int seg = 96, rings = 16;
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
        float slope = (rTop - rBottom) / (yTop - yBottom);
        for (int j = 0; j <= rings; j++)
        {
            float h = j / (float)rings;
            float y = Mathf.Lerp(0f, yTop - yBottom, h);
            float r = Mathf.Lerp(rBottom, rTop, h);
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                verts.Add(dir * r + Vector3.up * y);
                norms.Add((dir - Vector3.up * slope).normalized);
                uvs.Add(new Vector2(i / (float)seg, h));
            }
        }
        for (int j = 0; j < rings; j++)
            for (int i = 0; i < seg; i++)
            {
                int a = j * (seg + 1) + i, b = a + 1, c = a + seg + 1, d = c + 1;
                tris.AddRange(new[] { a, c, b, b, c, d });
            }
        string meshPath = genDir + "/HoloBeam.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (mesh == null) { mesh = new Mesh { name = "HoloBeam" }; AssetDatabase.CreateAsset(mesh, meshPath); }
        mesh.Clear();
        mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0); mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);

        string matPath = genDir + "/HoloBeam.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null) { mat = new Material(Shader.Find("Tokumu/HoloBeam")); AssetDatabase.CreateAsset(mat, matPath); }
        mat.shader = Shader.Find("Tokumu/HoloBeam");
        mat.renderQueue = (int)RenderQueue.Transparent + 5;   // ホログラム球より先に
        mat.SetFloat("_Intensity", 2.0f);
        mat.SetFloat("_Rays", 0.9f);
        EditorUtility.SetDirty(mat);

        var go = GameObject.Find("情報球_投射光") ?? new GameObject("情報球_投射光");
        go.transform.position = new Vector3(sb.center.x, yBottom, sb.center.z);
        go.transform.rotation = Quaternion.identity;
        if (!go.TryGetComponent<MeshFilter>(out var mf)) mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        if (!go.TryGetComponent<MeshRenderer>(out var mr)) mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off; mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

        // 立ち昇る光の粒
        string pmPath = genDir + "/HoloSpark.mat";
        var pm = AssetDatabase.LoadAssetAtPath<Material>(pmPath);
        if (pm == null) { pm = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit")); AssetDatabase.CreateAsset(pm, pmPath); }
        pm.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(genDir + "/DustDot.png"));
        pm.SetColor("_BaseColor", new Color(0.5f, 1.3f, 3.2f, 1f));
        pm.SetFloat("_Surface", 1f); pm.SetFloat("_Blend", 2f);
        BaseShaderGUI.SetMaterialKeywords(pm, null, ParticleGUI.SetMaterialKeywords);
        EditorUtility.SetDirty(pm);

        var t = go.transform.Find("Sparks");
        var pgo = t ? t.gameObject : new GameObject("Sparks");
        pgo.transform.SetParent(go.transform, false);
        pgo.transform.localPosition = Vector3.zero;
        pgo.transform.localRotation = Quaternion.Euler(-90f, 0, 0);   // 形状の +Z を上へ
        if (!pgo.TryGetComponent<ParticleSystem>(out var ps)) ps = pgo.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.loop = true; main.prewarm = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 2.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.7f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.035f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 400;
        var em = ps.emission; em.rateOverTime = 160f;
        var sh = ps.shape;
        sh.shapeType = ParticleSystemShapeType.Cone;
        sh.angle = Mathf.Atan(slope) * Mathf.Rad2Deg;
        sh.radius = rBottom;
        sh.radiusThickness = 0.05f;   // 縁から出す
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                  new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.1f), new GradientAlphaKey(0.6f, 0.6f), new GradientAlphaKey(0, 1) });
        col.color = g;
        var sz = ps.sizeOverLifetime; sz.enabled = true; sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 1, 1, 0.3f));
        var pr = pgo.GetComponent<ParticleSystemRenderer>();
        pr.sharedMaterial = pm; pr.renderMode = ParticleSystemRenderMode.Billboard;
        pr.shadowCastingMode = ShadowCastingMode.Off; pr.receiveShadows = false;
        pr.maxParticleSize = 0.03f;
        ps.Play(true);
        ps.Simulate(3f, true, true, true);
        AssetDatabase.SaveAssets();
        Debug.Log(Log + $"投射光: 下 r={rBottom:F2} y={yBottom:F2} / 上 r={rTop:F2} y={yTop:F2}");
    }

    // ------------------------------------------------------------- displays
    // (種類 0=LED / 1=液晶, 画素数, ビデオウォールのパネル数)
    static readonly Dictionary<string, (int type, Vector2 cells, Vector2 panels)> DisplayMats = new Dictionary<string, (int, Vector2, Vector2)>
    {
        { "モニター_表示",     (0, new Vector2(4096, 1024), new Vector2(1, 1)) },   // 湾曲大画面 41m×10m → 約 10mm ピッチの LED ビジョン
        { "補助画面_UI",       (1, new Vector2(3840, 2160), new Vector2(2, 2)) },   // 補助モニター 5m×2.6m → 2×2 枚の液晶ビデオウォール
        { "デスク画面_UI",     (1, new Vector2(2560, 1440), new Vector2(1, 1)) },   // デスクの液晶モニター
        { "デスク画面_工作員", (1, new Vector2(2560, 1440), new Vector2(1, 1)) },
    };

    static void ApplyDisplay(Material mat, MatEntry m)
    {
        var sh = Shader.Find("Tokumu/Display");
        if (sh == null) { Debug.LogError(Log + "Tokumu/Display が見つかりません"); return; }
        var d = DisplayMats[m.name];
        mat.shader = sh;
        mat.SetTexture("_MainTex", Tex(LinearEmission(m.emissionMap)));
        var c = m.emission ?? new float[] { 1, 1, 1 };
        mat.SetColor("_Tint", new Color(c[0], c[1], c[2]).gamma);
        // 以前の Lit と同じ実効の明るさにする。Lit では発光色を Color.gamma で渡しており、1 を超える値は
        // そのまま（ガンマ空間の値のまま）使われていた（強さ 5.5 → 実効 約 2.17）。ライティングはこの明るさで合わせてある
        mat.SetFloat("_Emission", Mathf.LinearToGammaSpace(m.emissionStrength));
        // パネル面の反射率は元のマテリアルの値（約 0.004）。大きいと、画面自身の光を焼いた APV を拾って暗部が浮く
        mat.SetColor("_PanelColor", LinToGamma(m.baseColor[0], m.baseColor[1], m.baseColor[2]));
        mat.SetFloat("_Type", d.type);
        mat.SetVector("_Cells", new Vector4(d.cells.x, d.cells.y, 0, 0));
        mat.SetVector("_Panels", new Vector4(d.panels.x, d.panels.y, 0, 0));
        // LED は艶の少ない黒いマスク、液晶はカバーガラス
        mat.SetFloat("_Smoothness", d.type == 0 ? 0.55f : 0.9f);
        mat.SetFloat("_F0", d.type == 0 ? 0.03f : 0.04f);
        mat.SetFloat("_ViewFalloff", d.type == 0 ? 0.35f : 0.45f);
        mat.renderQueue = -1;
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
    }

    static void ApplyLit(Material mat, MatEntry m)
    {
        if (HologramMats.Contains(m.name)) { ApplyHologram(mat, m); return; }
        if (DisplayMats.ContainsKey(m.name)) { ApplyDisplay(mat, m); return; }
        if (IsGlass(m)) { ApplyGlass(mat, m); return; }
        var litShader = Shader.Find("Universal Render Pipeline/Lit");
        if (mat.shader != litShader) mat.shader = litShader;
        var albedo = Tex(m.baseColorMap);
        var normal = Tex(m.normalMap);
        var ms = Tex(m.metallicSmoothnessMap);
        var emisTex = Tex(LinearEmission(m.emissionMap));
        bool opaqueGlass = OpaqueGlass.Contains(m.name);
        bool glass = m.transmission > 0.5f && !opaqueGlass;
        bool transparent = glass || m.alpha < 0.99f;

        float bscale = opaqueGlass ? 1f - m.transmission : 1f;   // 透過分は空洞（黒）
        Color baseCol = albedo != null ? Color.white : LinToGamma(m.baseColor[0] * bscale, m.baseColor[1] * bscale, m.baseColor[2] * bscale);
        float alpha = m.alpha;
        if (glass) alpha = Mathf.Min(alpha, m.roughness < 0.1f ? 0.12f : 0.35f);
        baseCol.a = alpha;

        mat.SetFloat("_WorkflowMode", 1f);
        mat.SetTexture("_BaseMap", albedo);
        mat.SetColor("_BaseColor", baseCol);
        mat.SetFloat("_Metallic", m.metallic);
        mat.SetFloat("_Smoothness", ms != null ? 1f : Mathf.Clamp01(1f - m.roughness));
        mat.SetTexture("_MetallicGlossMap", ms);
        mat.SetFloat("_SmoothnessTextureChannel", 0f);
        mat.SetTexture("_BumpMap", normal);
        mat.SetFloat("_BumpScale", m.normalStrength);

        // 発光: Blender の color*strength（リニア）をそのまま HDR 値に
        bool emissive = (m.emissionStrength > 0f && m.emission != null && m.emission.Max() > 0f) || emisTex != null;
        Color e = Color.black;
        if (emissive)
        {
            float s = Mathf.Max(m.emissionStrength, emisTex != null ? 1f : 0f);
            var c = m.emission ?? new float[] { 1, 1, 1 };
            e = LinToGamma(c[0] * s, c[1] * s, c[2] * s);
        }
        mat.SetColor("_EmissionColor", e);
        mat.SetTexture("_EmissionMap", emisTex);
        // ベイク時に発光体（大画面・LED・間接照明）が周囲を照らすように
        mat.globalIlluminationFlags = emissive ? MaterialGlobalIlluminationFlags.BakedEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;

        mat.SetFloat("_Surface", transparent ? 1f : 0f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_BlendModePreserveSpecular", 1f);
        mat.SetFloat("_AlphaClip", 0f);
        mat.SetFloat("_Cull", 2f);
        BaseShaderGUI.SetMaterialKeywords(mat, LitGUI.SetMaterialKeywords);
    }

    // ------------------------------------------------------------------ scene
    static void BuildScene(string captureDir)
    {
        var data = JsonUtility.FromJson<SceneData>(File.ReadAllText(Root + "/scene_lights.json"));
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
        if (prefab == null) { Debug.LogError(Log + "FBX が見つかりません: " + Fbx); return; }

        var prev = SceneManager.GetActiveScene();
        bool keepPrev = prev.isDirty || prev.path == ScenePath;
        if (prev.path == ScenePath) keepPrev = false;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, keepPrev ? NewSceneMode.Additive : NewSceneMode.Single);
        SceneManager.SetActiveScene(scene);

        // モデル
        var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        model.name = "特務指令室";
        foreach (var t in model.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.ReflectionProbeStatic);
        var bounds = new Bounds(); bool first = true;
        foreach (var r in model.GetComponentsInChildren<Renderer>())
        { if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }

        // 環境
        var w = data.world;
        Color bg = LinToGamma(w.color[0] * w.strength, w.color[1] * w.strength, w.color[2] * w.strength);
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = bg;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.sun = null;
        RenderSettings.fog = false;
        string setDir = Root + "/Settings";
        if (!AssetDatabase.IsValidFolder(setDir)) AssetDatabase.CreateFolder(Root, "Settings");
        string lsPath = setDir + "/TokumuLighting.lighting";
        var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(lsPath);
        if (ls == null) { ls = new LightingSettings { name = "TokumuLighting" }; AssetDatabase.CreateAsset(ls, lsPath); }
        ls.bakedGI = false; ls.realtimeGI = false;
        Lightmapping.lightingSettings = ls;

        // ライト
        var lroot = new GameObject("Lights_FromBlender");
        int nShadow = 0;
        foreach (var l in data.lights)
        {
            var go = new GameObject(l.name);
            go.transform.SetParent(lroot.transform, false);
            go.transform.position = V(l.position);
            go.transform.rotation = Quaternion.LookRotation(V(l.forward), V(l.up));
            var li = go.AddComponent<Light>();
            li.color = LinToGamma(l.color[0], l.color[1], l.color[2]);
            li.lightmapBakeType = LightmapBakeType.Realtime;
            float inten;
            switch (l.type)
            {
                case "POINT":
                    li.type = LightType.Point; inten = l.energy / (4f * Mathf.PI); break;
                case "SPOT":
                    li.type = LightType.Spot; inten = l.energy / (4f * Mathf.PI);
                    li.spotAngle = Mathf.Clamp(l.spotSize, 1f, 179f);
                    li.innerSpotAngle = li.spotAngle * Mathf.Clamp01(1f - l.spotBlend);
                    break;
                case "SUN":
                    li.type = LightType.Directional; inten = l.energy; break;
                default: // AREA → URP はリアルタイムのエリアライト非対応のため広角スポットで近似
                    li.type = LightType.Spot; inten = l.energy / Mathf.PI;
                    li.spotAngle = 150f; li.innerSpotAngle = 40f;
                    break;
            }
            li.intensity = inten * LightScale;
            li.range = Mathf.Clamp(Mathf.Sqrt(li.intensity / 0.02f), 4f, 45f);
            if (l.type == "SPOT" && l.shadow)
            {
                li.shadows = LightShadows.Soft; li.shadowStrength = 1f; nShadow++;
            }
            else li.shadows = LightShadows.None;
        }

        // カメラ
        var croot = new GameObject("Cameras_FromBlender");
        var cams = new List<Camera>();
        foreach (var c in data.cameras)
        {
            var go = new GameObject(c.name);
            go.transform.SetParent(croot.transform, false);
            go.transform.position = V(c.position);
            go.transform.rotation = Quaternion.LookRotation(V(c.forward), V(c.up));
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = c.fovY;
            cam.nearClipPlane = Mathf.Max(0.01f, c.near);
            cam.farClipPlane = Mathf.Max(200f, c.far);
            SetupCamera(cam, bg);
            if (c.isMain) { go.tag = "MainCamera"; go.AddComponent<AudioListener>(); }
            else go.SetActive(false);
            cams.Add(cam);
        }

        // ポストプロセス
        string profPath = setDir + "/TokumuVolumeProfile.asset";
        AssetDatabase.DeleteAsset(profPath);
        var prof = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(prof, profPath);
        var tm = prof.Add<Tonemapping>(true); tm.mode.value = TonemappingMode.Neutral;
        var bl = prof.Add<Bloom>(true); bl.threshold.value = 1.0f; bl.intensity.value = 0.6f; bl.scatter.value = 0.65f; bl.highQualityFiltering.value = true;
        var ca = prof.Add<ColorAdjustments>(true); ca.postExposure.value = data.view.exposure;
        foreach (var comp in prof.components) { comp.name = comp.GetType().Name; AssetDatabase.AddObjectToAsset(comp, prof); }
        EditorUtility.SetDirty(prof);
        AssetDatabase.SaveAssets();
        var vgo = new GameObject("Global Volume");
        var vol = vgo.AddComponent<Volume>(); vol.isGlobal = true; vol.sharedProfile = prof;

        // 反射プローブ（部屋全体をボックス投影）
        var pgo = new GameObject("ReflectionProbe_Room");
        pgo.transform.position = new Vector3(bounds.center.x, bounds.min.y + 2.0f, bounds.center.z);
        var probe = pgo.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Baked;
        probe.boxProjection = true;
        probe.size = bounds.size + Vector3.one * 0.5f;
        probe.center = bounds.center - pgo.transform.position;
        probe.resolution = 256;
        probe.intensity = ProbeIntensity;
        probe.hdr = true;
        probe.clearFlags = ReflectionProbeClearFlags.SolidColor;
        probe.backgroundColor = bg;

        // 保存
        string sceneDir = Path.GetDirectoryName(ScenePath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(sceneDir)) AssetDatabase.CreateFolder(Root, "Scenes");
        EditorSceneManager.SaveScene(scene, ScenePath);
        ShaderUtil.allowAsyncCompilation = false;
        try
        {
            string probeDir = sceneDir + "/TokumuShireishitsu";
            if (!AssetDatabase.IsValidFolder(probeDir)) AssetDatabase.CreateFolder(sceneDir, "TokumuShireishitsu");
            bool ok = Lightmapping.BakeReflectionProbe(probe, probeDir + "/ReflectionProbe_Room.exr");
            Debug.Log(Log + "反射プローブのベイク: " + (ok ? "成功" : "失敗"));
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            var review = EnsureReviewCameras(bg);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            if (!string.IsNullOrEmpty(captureDir)) { Capture(cams, captureDir, "unity_cam"); Capture(review, captureDir, "review"); }
        }
        finally { ShaderUtil.allowAsyncCompilation = true; }

        Debug.Log(Log + $"シーン作成: {ScenePath} / ライト {data.lights.Length}（影あり {nShadow}）/ カメラ {data.cameras.Length} / bounds {bounds.min}..{bounds.max}");
        if (keepPrev)
        {
            EditorSceneManager.CloseScene(scene, true);
            Debug.Log(Log + "開いていたシーンに未保存の変更があったため、新しいシーンは保存のみ行いました");
        }
    }

    // 撮影経路の検証用: 既知のリニア値を出す板を、シーンから離れた場所で撮る（保存はしない）
    static void CaptureCalibration(string dir)
    {
        var root = new GameObject("__calib") { hideFlags = HideFlags.DontSave };
        root.transform.position = new Vector3(0, 5000, 0);
        float[] vals = { 0.05f, 0.18f, 1.0f, 4.0f };
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        var mats = new List<Material>();
        for (int i = 0; i < vals.Length; i++)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.hideFlags = HideFlags.DontSave;
            q.transform.SetParent(root.transform, false);
            q.transform.localPosition = new Vector3((i - 1.5f) * 1.1f, 0, 5);
            var m = new Material(unlit);
            m.SetColor("_BaseColor", new Color(vals[i], vals[i], vals[i]).gamma);
            q.GetComponent<MeshRenderer>().sharedMaterial = m;
            mats.Add(m);
        }
        var cgo = new GameObject("cam") { hideFlags = HideFlags.DontSave };
        cgo.transform.SetParent(root.transform, false);
        var cam = cgo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        cam.fieldOfView = 40; cam.farClipPlane = 50;
        var src = GameObject.Find("Cameras_FromBlender").GetComponentInChildren<Camera>(true);
        var acd = cam.GetUniversalAdditionalCameraData();
        acd.renderPostProcessing = src.GetUniversalAdditionalCameraData().renderPostProcessing;
        try { Capture(new List<Camera> { cam }, dir, "calib"); }
        finally { foreach (var m in mats) UnityEngine.Object.DestroyImmediate(m); UnityEngine.Object.DestroyImmediate(root); }
    }

    static void Capture(List<Camera> cams, string dir, string prefix)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < cams.Count; i++)
        {
            var cam = cams[i];
            bool wasActive = cam.gameObject.activeSelf;
            cam.gameObject.SetActive(true);
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            for (int f = 0; f < 3; f++) cam.Render();   // SSR の履歴を作るため数フレーム描画
            var prevRT = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
            RenderTexture.active = prevRT;
            cam.targetTexture = null;
            File.WriteAllBytes(Path.Combine(dir, $"{prefix}{i}.png"), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
            cam.gameObject.SetActive(wasActive);
            Debug.Log(Log + $"キャプチャ {prefix}{i}.png = {cam.name}");
        }
    }
}
#endif
