// 特務指令室: ボリューメトリック光のレンダラー機能
// 透明物の後・ポストプロセスの前に、低解像度でレイマーチ → 深度を見て拡大合成する。
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class TokumuVolumetricFeature : ScriptableRendererFeature
{
    [Serializable]
    public class Settings
    {
        [Tooltip("散乱係数 σs (1/m)。空気中のほこり・湿気の量")] public float scattering = 0.012f;
        [Tooltip("消散係数 σt (1/m)。遠くがかすむ量")] public float extinction = 0.014f;
        [Range(-0.9f, 0.9f), Tooltip("位相関数 g。正で光源側を見たときに明るい")] public float anisotropy = 0.45f;
        [Range(0, 8)] public float intensity = 1f;
        [Range(8, 64)] public int steps = 28;
        public float maxDistance = 40f;
        [Range(0, 1)] public float noiseAmount = 0.55f;
        public float noiseFrequency = 0.35f;
        public float heightBase = 1.5f;
        public float heightFalloff = 0.05f;
        public Vector3 wind = new Vector3(0.05f, 0.015f, 0.03f);
        public Color ambient = new Color(0.08f, 0.12f, 0.2f);
        [Range(0, 4)] public float ambientScattering = 1f;
        [Range(2, 4)] public int downsample = 4;
    }

    public Settings settings = new Settings();
    [SerializeField, HideInInspector] Shader m_Shader;
    Material m_Material;
    Pass m_Pass;

    class Pass : ScriptableRenderPass
    {
        readonly Material m_Mat;
        Settings m_S;
        class PassData { public Material mat; public TextureHandle low; }

        public Pass(Material m) { m_Mat = m; renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing; }
        public void Setup(Settings s) { m_S = s; }

        public override void RecordRenderGraph(RenderGraph rg, ContextContainer frameData)
        {
            var res = frameData.Get<UniversalResourceData>();
            var cam = frameData.Get<UniversalCameraData>();
            var light = frameData.Get<UniversalLightData>();
            if (!res.cameraDepthTexture.IsValid()) return;

            var s = m_S;
            m_Mat.SetVector("_VolParams", new Vector4(s.scattering, s.extinction, s.anisotropy, s.intensity));
            m_Mat.SetVector("_VolParams2", new Vector4(s.steps, s.maxDistance, s.noiseAmount, s.noiseFrequency));
            m_Mat.SetVector("_VolParams3", new Vector4(s.heightBase, s.heightFalloff, Mathf.Min(light.additionalLightsCount, 32), s.ambientScattering));
            m_Mat.SetVector("_VolWind", s.wind);
            m_Mat.SetVector("_VolAmbient", s.ambient.linear);

            var desc = cam.cameraTargetDescriptor;
            var td = new TextureDesc(Mathf.Max(1, desc.width / s.downsample), Mathf.Max(1, desc.height / s.downsample))
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat,
                name = "_TokumuVolLowRes",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                clearBuffer = false
            };
            var low = rg.CreateTexture(td);

            using (var b = rg.AddRasterRenderPass<PassData>("Tokumu Volumetric March", out var pd))
            {
                pd.mat = m_Mat;
                b.SetRenderAttachment(low, 0, AccessFlags.WriteAll);
                b.UseTexture(res.cameraDepthTexture, AccessFlags.Read);
                if (res.additionalShadowsTexture.IsValid()) b.UseTexture(res.additionalShadowsTexture, AccessFlags.Read);
                b.AllowGlobalStateModification(true);
                b.SetRenderFunc(static (PassData d, RasterGraphContext ctx) => Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), d.mat, 0));
            }

            using (var b = rg.AddRasterRenderPass<PassData>("Tokumu Volumetric Composite", out var pd))
            {
                pd.mat = m_Mat; pd.low = low;
                b.SetRenderAttachment(res.activeColorTexture, 0, AccessFlags.ReadWrite);
                b.UseTexture(low, AccessFlags.Read);
                b.UseTexture(res.cameraDepthTexture, AccessFlags.Read);
                b.AllowGlobalStateModification(true);
                b.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    d.mat.SetTexture("_VolLowRes", (RTHandle)d.low);
                    Blitter.BlitTexture(ctx.cmd, new Vector4(1, 1, 0, 0), d.mat, 1);
                });
            }
        }
    }

    public override void Create()
    {
        if (m_Shader == null) m_Shader = Shader.Find("Hidden/Tokumu/Volumetric");
        if (m_Shader == null) return;
        m_Material = CoreUtils.CreateEngineMaterial(m_Shader);
        m_Pass = new Pass(m_Material);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_Pass == null) return;
        var t = renderingData.cameraData.cameraType;
        if (t == CameraType.Preview || t == CameraType.Reflection) return;
        m_Pass.Setup(settings);
        m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing) { CoreUtils.Destroy(m_Material); }
}
