using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PandaGrab : ScriptableRendererFeature
{
    const string GrabTextureName = "_PandaGrabTex";
    static readonly int GrabTextureId = Shader.PropertyToID(GrabTextureName);

    // Pass 1：复制当前相机颜色，供 PandaPass 的折射采样使用。
    class GrabPass : ScriptableRenderPass
    {
        RTHandle source;
        RTHandle destination;

        public void Setup(RTHandle cameraColor, RTHandle grabTexture)
        {
            source = cameraColor;
            destination = grabTexture;
        }

        public override void Execute(
            ScriptableRenderContext context,
            ref RenderingData renderingData)
        {
            if (source == null || destination == null)
                return;

            CommandBuffer cmd = CommandBufferPool.Get("PandaGrab");
            Blitter.BlitCameraTexture(cmd, source, destination);
            cmd.SetGlobalTexture(GrabTextureId, destination);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    // Pass 2：绘制所有 LightMode="PandaPass" 的折射面片。
    class DrawPandaPass : ScriptableRenderPass
    {
        static readonly ShaderTagId ShaderTag = new ShaderTagId("PandaPass");
        FilteringSettings filtering = new FilteringSettings(RenderQueueRange.all);

        public override void Execute(
            ScriptableRenderContext context,
            ref RenderingData renderingData)
        {
            DrawingSettings drawing = CreateDrawingSettings(
                ShaderTag,
                ref renderingData,
                SortingCriteria.CommonTransparent);

            context.DrawRenderers(
                renderingData.cullResults,
                ref drawing,
                ref filtering);
        }
    }

    GrabPass grabPass;
    DrawPandaPass drawPass;
    RTHandle grabTexture;

    public override void Create()
    {
        grabPass = new GrabPass
        {
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents
        };

        drawPass = new DrawPandaPass
        {
            // 保证折射面片在屏幕颜色复制完成后绘制。
            renderPassEvent = (RenderPassEvent)(
                (int)RenderPassEvent.AfterRenderingTransparents + 1)
        };
    }

    public override void SetupRenderPasses(
        ScriptableRenderer renderer,
        in RenderingData renderingData)
    {
        RenderTextureDescriptor descriptor =
            renderingData.cameraData.cameraTargetDescriptor;
        descriptor.depthBufferBits = 0;
        descriptor.msaaSamples = 1;

        RenderingUtils.ReAllocateIfNeeded(
            ref grabTexture,
            descriptor,
            FilterMode.Bilinear,
            TextureWrapMode.Clamp,
            name: GrabTextureName);

        grabPass.Setup(renderer.cameraColorTargetHandle, grabTexture);
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        renderer.EnqueuePass(grabPass);
        renderer.EnqueuePass(drawPass);
    }

    protected override void Dispose(bool disposing)
    {
        grabTexture?.Release();
        grabTexture = null;
    }
}
