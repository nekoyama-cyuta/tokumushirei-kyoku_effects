// 特務指令室: 床の平面反射
// 対象の床（Tokumu/SSROverlay を載せた子オブジェクト）を高さごとにまとめ、
// メインカメラの描画直前に、床で鏡像にした反射カメラを描いて反射テクスチャを作る。
// SSR と違い、画面の外にある物（天井のリング照明など）も映る。
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[ExecuteAlways]
public class TokumuPlanarReflection : MonoBehaviour
{
    [Tooltip("反射を受ける床（SSR オーバーレイの子オブジェクト）")]
    public List<MeshRenderer> receivers = new List<MeshRenderer>();
    [Range(0.25f, 1f)] public float resolutionScale = 0.6f;
    [Tooltip("反射カメラの近クリップを床から少し上にずらす量（m）")]
    public float clipPlaneOffset = 0.01f;
    public bool renderShadows = false;
    public LayerMask reflectLayers = ~0;
    [Tooltip("反射カメラに使うレンダラーの番号（不透明物だけを描く軽いレンダラー）。-1 で既定")]
    public int rendererIndex = -1;
    [Tooltip("この高さの差までの床は 1 枚の反射でまとめる（m）")]
    public float mergeTolerance = 0.15f;
    [Range(0f, 0.2f), Tooltip("床が画面に占める割合がこれ未満なら反射を描かず SSR に任せる")]
    public float minScreenCoverage = 0.03f;

    class Plane
    {
        public float height;
        public readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        public readonly List<float> ownHeights = new List<float>();   // 各床の実際の高さ（シェーダーの判定用）
        public RenderTexture rt;
        public Bounds bounds;
    }

    readonly List<Plane> m_Planes = new List<Plane>();
    Camera m_ReflCam;
    MaterialPropertyBlock m_Mpb;
    static bool s_Rendering;
    static readonly int k_PlanarTex = Shader.PropertyToID("_PlanarTex");
    static readonly int k_HasPlanar = Shader.PropertyToID("_HasPlanar");
    static readonly int k_PlanarHeight = Shader.PropertyToID("_PlanarHeight");

    void OnEnable()
    {
        BuildPlanes();
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        foreach (var p in m_Planes)
        {
            SetPlanar(p, null);
            if (p.rt != null) { p.rt.Release(); DestroyImmediate(p.rt); }
        }
        m_Planes.Clear();
        if (m_ReflCam != null) DestroyImmediate(m_ReflCam.gameObject);
    }

    public void BuildPlanes()
    {
        m_Planes.Clear();
        foreach (var r in receivers)
        {
            if (r == null) continue;
            if (!TryDominantHeight(r, out float h, out Bounds b)) continue;
            var p = m_Planes.Find(x => Mathf.Abs(x.height - h) < mergeTolerance);
            if (p == null) { p = new Plane { height = h, bounds = b }; m_Planes.Add(p); }
            else p.bounds.Encapsulate(b);
            p.renderers.Add(r);
            p.ownHeights.Add(h);
        }
    }

    // 上向き三角形の面積を高さ（2cm 刻み）ごとに集計し、一番広い高さを床面とする
    static bool TryDominantHeight(MeshRenderer r, out float height, out Bounds bounds)
    {
        height = 0; bounds = r.bounds;
        var mf = r.GetComponent<MeshFilter>();
        var mesh = mf ? mf.sharedMesh : null;
        if (mesh == null || !mesh.isReadable) return false;
        var l2w = r.transform.localToWorldMatrix;
        var v = mesh.vertices; var t = mesh.triangles;
        var area = new Dictionary<int, float>();
        var bmin = new Dictionary<int, Vector3>(); var bmax = new Dictionary<int, Vector3>();
        for (int k = 0; k < t.Length; k += 3)
        {
            Vector3 a = l2w.MultiplyPoint3x4(v[t[k]]), b = l2w.MultiplyPoint3x4(v[t[k + 1]]), c = l2w.MultiplyPoint3x4(v[t[k + 2]]);
            var cr = Vector3.Cross(b - a, c - a);
            float ar = cr.magnitude * 0.5f;
            if (ar < 1e-6f || cr.y / (2f * ar) < 0.995f) continue;
            int key = Mathf.RoundToInt((a.y + b.y + c.y) / 3f * 50f);
            area[key] = (area.TryGetValue(key, out var s0) ? s0 : 0f) + ar;
            var mn = Vector3.Min(a, Vector3.Min(b, c)); var mx = Vector3.Max(a, Vector3.Max(b, c));
            bmin[key] = bmin.TryGetValue(key, out var m0) ? Vector3.Min(m0, mn) : mn;
            bmax[key] = bmax.TryGetValue(key, out var m1) ? Vector3.Max(m1, mx) : mx;
        }
        if (area.Count == 0) return false;
        int best = 0; float bestA = -1;
        foreach (var kv in area) if (kv.Value > bestA) { bestA = kv.Value; best = kv.Key; }
        height = best / 50f;
        bounds = new Bounds(); bounds.SetMinMax(bmin[best] - Vector3.one * 0.01f, bmax[best] + Vector3.one * 0.01f);
        return bestA > 1f;   // 1m² 未満は対象外
    }

    public int planeCount => m_Planes.Count;
    public RenderTexture PlaneTexture(int i) => i < m_Planes.Count ? m_Planes[i].rt : null;
    public int renderCount;   // 検証用: 反射を描いた回数

    public string Describe() =>
        string.Join(", ", m_Planes.ConvertAll(p => $"y={p.height:F2}m×{p.renderers.Count}"));

    void SetPlanar(Plane p, Texture tex)
    {
        if (m_Mpb == null) m_Mpb = new MaterialPropertyBlock();
        for (int i = 0; i < p.renderers.Count; i++)
        {
            var r = p.renderers[i];
            if (r == null) continue;
            r.GetPropertyBlock(m_Mpb);
            if (tex != null) m_Mpb.SetTexture(k_PlanarTex, tex);
            m_Mpb.SetFloat(k_HasPlanar, tex != null ? 1f : 0f);
            m_Mpb.SetFloat(k_PlanarHeight, p.ownHeights[i]);
            r.SetPropertyBlock(m_Mpb);
        }
    }

    Camera ReflCam()
    {
        if (m_ReflCam != null) return m_ReflCam;
        var go = new GameObject("__TokumuPlanarReflectionCamera") { hideFlags = HideFlags.HideAndDontSave };
        m_ReflCam = go.AddComponent<Camera>();
        m_ReflCam.enabled = false;
        var d = go.AddComponent<UniversalAdditionalCameraData>();
        d.renderPostProcessing = false;
        d.renderShadows = renderShadows;
        // 反射は不透明物だけを描く軽いレンダラーで描く（透明物・ポスト用のコピーを省く）
        d.requiresColorOption = CameraOverrideOption.Off;
        d.requiresDepthOption = CameraOverrideOption.Off;
        if (rendererIndex >= 0) d.SetRenderer(rendererIndex);
        d.antialiasing = AntialiasingMode.None;
        return m_ReflCam;
    }

    void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (s_Rendering) return;
        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
        if (cam.name.StartsWith("__")) return;
        if (m_Planes.Count == 0) return;

        var frustum = GeometryUtility.CalculateFrustumPlanes(cam);
        foreach (var p in m_Planes)
        {
            // カメラが床より下、または床が画面に入っていなければ描かない
            // 画面にほとんど写っていない床も描かない（シェーダーは SSR に切り替わる）
            if (cam.transform.position.y < p.height + 0.02f || !GeometryUtility.TestPlanesAABB(frustum, p.bounds)
                || ScreenCoverage(cam, p.bounds) < minScreenCoverage)
            {
                SetPlanar(p, null);
                continue;
            }
            RenderPlane(ctx, cam, p);
            SetPlanar(p, p.rt);
        }
    }

    void RenderPlane(ScriptableRenderContext ctx, Camera cam, Plane p)
    {
        int w = Mathf.Max(64, (int)(cam.pixelWidth * resolutionScale));
        int h = Mathf.Max(64, (int)(cam.pixelHeight * resolutionScale));
        if (p.rt == null || p.rt.width != w || p.rt.height != h)
        {
            if (p.rt != null) { p.rt.Release(); DestroyImmediate(p.rt); }
            p.rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGBHalf)
            {
                name = "_TokumuPlanar_" + p.height.ToString("F2"),
                useMipMap = true,
                autoGenerateMips = true,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            p.rt.Create();
        }

        var rc = ReflCam();
        rc.CopyFrom(cam);
        rc.cameraType = CameraType.Reflection;
        rc.cullingMask = reflectLayers & cam.cullingMask;
        rc.targetTexture = p.rt;
        rc.useOcclusionCulling = false;
        rc.clearFlags = CameraClearFlags.SolidColor;
        rc.backgroundColor = Color.black;

        // 床（y = height、法線 +Y）で鏡像
        var n = Vector3.up;
        var pos = new Vector3(0, p.height, 0);
        var plane = new Vector4(n.x, n.y, n.z, -Vector3.Dot(n, pos));
        var refl = ReflectionMatrix(plane);
        rc.worldToCameraMatrix = cam.worldToCameraMatrix * refl;
        var camPos = cam.transform.position;
        rc.transform.position = refl.MultiplyPoint(camPos);
        var fwd = Vector3.Reflect(cam.transform.forward, n);
        var up = Vector3.Reflect(cam.transform.up, n);
        rc.transform.rotation = Quaternion.LookRotation(fwd, up);

        // 斜め近クリップ面で床より下を切る
        var clip = CameraSpacePlane(rc.worldToCameraMatrix, pos, n, clipPlaneOffset);
        rc.projectionMatrix = cam.CalculateObliqueMatrix(clip);
        rc.cullingMatrix = rc.projectionMatrix * rc.worldToCameraMatrix;

        renderCount++;
        s_Rendering = true;
        bool fog = RenderSettings.fog;
        GL.invertCulling = true;
        try
        {
#pragma warning disable CS0618
            UniversalRenderPipeline.RenderSingleCamera(ctx, rc);
#pragma warning restore CS0618
        }
        finally
        {
            GL.invertCulling = false;
            RenderSettings.fog = fog;
            s_Rendering = false;
        }
    }

    // 床の外接箱（上面）が画面に占める割合のおおよそ（投影した 4 隅の外接矩形、画面内に切り詰め）
    static float ScreenCoverage(Camera cam, Bounds b)
    {
        Vector2 mn = new Vector2(1, 1), mx = new Vector2(0, 0);
        int behind = 0;
        for (int i = 0; i < 4; i++)
        {
            var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, b.max.y, (i & 2) == 0 ? b.min.z : b.max.z);
            var v = cam.WorldToViewportPoint(c);
            if (v.z <= 0) { behind++; continue; }
            mn = Vector2.Min(mn, v); mx = Vector2.Max(mx, v);
        }
        if (behind > 0) return 1f;   // 隅がカメラの後ろ = 床の上に立っている
        mn = Vector2.Max(mn, Vector2.zero); mx = Vector2.Min(mx, Vector2.one);
        return Mathf.Max(0, mx.x - mn.x) * Mathf.Max(0, mx.y - mn.y);
    }

    static Vector4 CameraSpacePlane(Matrix4x4 worldToCam, Vector3 pos, Vector3 normal, float offset)
    {
        var cpos = worldToCam.MultiplyPoint(pos + normal * offset);
        var cnormal = worldToCam.MultiplyVector(normal).normalized;
        return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
    }

    static Matrix4x4 ReflectionMatrix(Vector4 p)
    {
        var m = Matrix4x4.identity;
        m.m00 = 1f - 2f * p.x * p.x; m.m01 = -2f * p.x * p.y; m.m02 = -2f * p.x * p.z; m.m03 = -2f * p.w * p.x;
        m.m10 = -2f * p.y * p.x; m.m11 = 1f - 2f * p.y * p.y; m.m12 = -2f * p.y * p.z; m.m13 = -2f * p.w * p.y;
        m.m20 = -2f * p.z * p.x; m.m21 = -2f * p.z * p.y; m.m22 = 1f - 2f * p.z * p.z; m.m23 = -2f * p.w * p.z;
        m.m30 = 0f; m.m31 = 0f; m.m32 = 0f; m.m33 = 1f;
        return m;
    }
}
