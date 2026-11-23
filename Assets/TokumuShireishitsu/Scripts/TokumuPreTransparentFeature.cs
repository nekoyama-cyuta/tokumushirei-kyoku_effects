// 特務指令室: 透明物の直前に「不透明面への重ね描き（床の自前 SSR など）」を行い、
// その結果を _CameraOpaqueTexture に取り直すレンダラー機能。
// これにより、ガラス越しに見える床にも映り込みが入る。
// 対象は LightMode = "TokumuPreTransparent" のパスを持つマテリアル。
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public class TokumuPreTransparentFeature : ScriptableRendererFeature
{
    class OverlayPass : ScriptableRenderPass
    {
        static readonly ShaderTagId k_Tag = new ShaderTagId("TokumuPreTransparent");
        static readonly int k_OpaqueTex = Shader.PropertyToID("_CameraOpaqueTexture");

        class DrawData { public RendererListHandle list; }

        public OverlayPass() { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents; }

        public override void RecordRenderGraph(RenderGraph rg, ContextContainer frameData)
        {
            var res = frameData.Get<UniversalResourceData>();
            var rd = frameData.Get<UniversalRenderingData>();
            var cam = frameData.Get<UniversalCameraData>();
            var light = frameData.Get<UniversalLightData>();
            if (!res.cameraOpaqueTexture.IsValid() || !res.cameraDepthTexture.IsValid()) return;

            var draw = RenderingUtils.CreateDrawingSettings(k_Tag, rd, cam, light, SortingCriteria.CommonTransparent);
            var filter = new FilteringSettings(RenderQueueRange.all);
            var param = new RendererListParams(rd.cullResults, draw, filter);

            using (var b = rg.AddRasterRenderPass<DrawData>("Tokumu PreTransparent Overlay", out var pd))
            {
                pd.list = rg.CreateRendererList(param);
                b.UseRendererList(pd.list);
                b.SetRenderAttachment(res.activeColorTexture, 0, AccessFlags.Write);
                b.SetRenderAttachmentDepth(res.activeDepthTexture, AccessFlags.Read);
                b.UseTexture(res.cameraOpaqueTexture, AccessFlags.Read);
                b.UseTexture(res.cameraDepthTexture, AccessFlags.Read);
                b.AllowGlobalStateModification(true);
                b.SetRenderFunc(static (DrawData d, RasterGraphContext ctx) => ctx.cmd.DrawRendererList(d.list));
            }

            // 重ね描き後の色を不透明テクスチャとして取り直す（以降のガラスが参照）
            var desc = rg.GetTextureDesc(res.cameraOpaqueTexture);
            desc.name = "_TokumuOpaqueWithOverlay";
            desc.clearBuffer = false;
            var dst = rg.CreateTexture(desc);
            using (var b = rg.AddBlitPass(res.activeColorTexture, dst, Vector2.one, Vector2.zero, returnBuilder: true, passName: "Tokumu Recopy Opaque"))
            {
                b.SetGlobalTextureAfterPass(dst, k_OpaqueTex);
            }
        }
    }

    OverlayPass m_Pass;

    public override void Create() { m_Pass = new OverlayPass(); }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var t = renderingData.cameraData.cameraType;
        if (t == CameraType.Preview || t == CameraType.Reflection) return;
        renderer.EnqueuePass(m_Pass);
    }
}
