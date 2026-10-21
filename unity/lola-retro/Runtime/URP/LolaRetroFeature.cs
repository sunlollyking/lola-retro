using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace LolaRetro
{
    /// <summary>
    /// Lola Retro in URP: runs on every camera of this renderer that has a Lola Retro component,
    /// after URP's post-processing, and its picture becomes the camera's. In XR each eye gets its
    /// own tube; on an HDR display the tube uses the display's headroom.
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
            public int slices, firstEye;
            public LolaRetroHdr hdr;
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
            if (effect == null || !effect.Supported || resources.isActiveTargetBackBuffer || !cameraData.resolveFinalTarget)
                return;

            TextureHandle image = resources.cameraColor;
            TextureDesc desc = renderGraph.GetTextureDesc(image);
            LolaRetroHdr hdr = Hdr(cameraData);
            desc.name = "Lola Retro";
            desc.clearBuffer = false;
            desc.msaaSamples = MSAASamples.None;
            if (!hdr.active)
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
                // XR: one slice of the camera's texture array per eye (single-pass), or one eye per
                // camera pass (multi-pass)
                data.slices = desc.dimension == TextureDimension.Tex2DArray ? Mathf.Max(1, desc.slices) : 0;
                data.firstEye = cameraData.xr.enabled ? cameraData.xr.multipassId : 0;
                data.hdr = hdr;
                builder.UseTexture(image, AccessFlags.Read);
                builder.UseTexture(output, AccessFlags.WriteAll);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData d, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    var rect = new RectInt(0, 0, d.size.x, d.size.y);
                    RTHandle image = d.image, output = d.output;
                    if (d.slices == 0)
                    {
                        d.effect.Record(cmd, new LolaRetroImage(image, -1, rect), new LolaRetroImage(output, -1, rect), 0, d.hdr);
                        return;
                    }
                    for (int s = 0; s < d.slices; s++)
                        d.effect.Record(cmd, new LolaRetroImage(image, s, rect), new LolaRetroImage(output, s, rect),
                                        d.firstEye + s, d.hdr);
                });
            }
            resources.cameraColor = output;
        }

        // The HDR display's levels as URP uses them (UniversalRenderPipeline.GetHDROutputLuminanceParameters):
        // the display's own, unless the Tonemapping override sets paper white or the peak by hand
        static LolaRetroHdr Hdr(UniversalCameraData cameraData)
        {
            if (!cameraData.isHDROutputActive)
                return default;
            HDROutputUtils.HDRDisplayInformation display = cameraData.hdrDisplayInformation;
            float paperWhite = display.paperWhiteNits, peak = display.maxToneMapLuminance;
            var tonemapping = VolumeManager.instance.stack.GetComponent<Tonemapping>();
            if (tonemapping != null)
            {
                if (!tonemapping.detectPaperWhite.value)
                    paperWhite = tonemapping.paperWhite.value;
                if (!tonemapping.detectBrightnessLimits.value)
                    peak = tonemapping.maxNits.value;
            }
            // with post-processing, URP's picture is already in nits in the display's colour space
            // (its final blit only codes it); without, the final blit converts it as well
            return LolaRetroHdr.For(cameraData.hdrDisplayColorGamut, paperWhite, peak, cameraData.postProcessEnabled);
        }
    }
}
