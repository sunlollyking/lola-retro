using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace LolaRetro
{
    /// <summary>
    /// Lola Retro in URP: runs on every camera of this renderer that has a Lola Retro component,
    /// after URP's post-processing, and its picture becomes the camera's.
    /// </summary>
    [DisallowMultipleRendererFeature("Lola Retro")]
    public sealed class LolaRetroFeature : ScriptableRendererFeature
    {
        LolaRetroPass pass;

        public override void Create() => pass = new LolaRetroPass();

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType type = renderingData.cameraData.cameraType;
            if (type == CameraType.Preview || type == CameraType.Reflection)
                return;
            if (!renderingData.cameraData.camera.TryGetComponent(out LolaRetroEffect effect) || !effect.isActiveAndEnabled)
                return;
            pass.effect = effect;
            renderer.EnqueuePass(pass);
        }
    }

    sealed class LolaRetroPass : ScriptableRenderPass
    {
        public LolaRetroEffect effect;

        class PassData
        {
            public LolaRetroEffect effect;
            public TextureHandle image, output;
            public Vector2Int size;
        }

        public LolaRetroPass()
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            requiresIntermediateTexture = true;    // the camera's image must be a texture
            profilingSampler = new ProfilingSampler("Lola Retro");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            // only the last camera of a stack shows the picture
            if (effect == null || effect.Chain == null || resources.isActiveTargetBackBuffer || !cameraData.resolveFinalTarget)
                return;

            TextureHandle image = resources.cameraColor;
            TextureDesc desc = renderGraph.GetTextureDesc(image);
            desc.name = "Lola Retro";
            desc.clearBuffer = false;
            desc.msaaSamples = MSAASamples.None;
            if (!HDROutputSettings.main.active)
            {
                // the last pass's picture is the 8-bit picture RetroArch would show; 8 bits hold
                // it exactly (URP's 32-bit HDR format keeps only 6 or 7 bits per channel)
                GraphicsFormat format = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
#if LOLA_RETRO_URP_17_3
                desc.format = format;
#else
                desc.colorFormat = format;
#endif
            }
            TextureHandle output = renderGraph.CreateTexture(desc);

            using (var builder = renderGraph.AddUnsafePass<PassData>("Lola Retro", out var data))
            {
                data.effect = effect;
                data.image = image;
                data.output = output;
                data.size = new Vector2Int(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
                builder.UseTexture(image, AccessFlags.Read);
                builder.UseTexture(output, AccessFlags.WriteAll);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData d, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    d.effect.Record(cmd, (RTHandle)d.image, d.size, (RTHandle)d.output, d.size);
                });
            }
            resources.cameraColor = output;
        }
    }
}
