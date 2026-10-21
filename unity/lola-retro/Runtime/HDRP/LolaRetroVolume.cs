using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LolaRetro
{
    /// <summary>
    /// Lola Retro in HDRP: a custom post-process, after HDRP's own, that shows every camera with a
    /// Lola Retro component on its tube. Add it under Custom Post Process Orders (After Post
    /// Process) in the HDRP Global Settings, and as an override (Post-processing, Custom, Lola Retro)
    /// on a Volume the camera sees. In XR each eye gets its own tube; on an HDR display the tube
    /// uses the display's headroom.
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/Custom/Lola Retro")]
    public sealed class LolaRetroVolume : CustomPostProcessVolumeComponent, IPostProcessComponent
    {
        [Tooltip("Show the cameras that have a Lola Retro component on its tube. The settings are on the component.")]
        public BoolParameter enable = new BoolParameter(true);

        // multi-pass XR renders each eye with an HDCamera of its own: their eye, by camera
        readonly Dictionary<HDCamera, int> eyes = new Dictionary<HDCamera, int>();

        public bool IsActive() => enable.value;

        public override CustomPostProcessInjectionPoint injectionPoint => CustomPostProcessInjectionPoint.AfterPostProcess;

        public override bool visibleInSceneView => false;

        public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
        {
            if (!camera.camera.TryGetComponent(out LolaRetroEffect effect) || !effect.isActiveAndEnabled || !effect.Supported)
            {
                HDUtils.BlitCameraTexture(cmd, source, destination);
                return;
            }
            if (!eyes.TryGetValue(camera, out int eye))
            {
                foreach (var e in eyes)
                    if (e.Key.camera == camera.camera)
                        eye++;
                eyes[camera] = eye;
            }
            // HDRP's targets are larger than the camera (dynamic resolution): its part is at the
            // bottom left. They are texture arrays: one slice per eye in single-pass XR.
            var rect = new RectInt(0, 0, camera.actualWidth, camera.actualHeight);
            int slices = source.rt != null && source.rt.dimension == TextureDimension.Tex2DArray ? Mathf.Max(1, source.rt.volumeDepth) : 0;
            LolaRetroHdr hdr = Hdr();
            if (slices == 0)
            {
                effect.Record(cmd, new LolaRetroImage(source, -1, rect), new LolaRetroImage(destination, -1, rect), eye, hdr);
                return;
            }
            for (int s = 0; s < slices; s++)
                effect.Record(cmd, new LolaRetroImage(source, s, rect), new LolaRetroImage(destination, s, rect), eye + s, hdr);
        }

        // HDRP's picture after its post-processing is in nits in the display's colour space on an
        // HDR display (its final pass only codes it): the display's levels, unless the Tonemapping
        // override sets paper white or the peak by hand
        static LolaRetroHdr Hdr()
        {
            HDROutputSettings display = HDROutputSettings.main;
            if (display == null || !display.active)
                return default;
            float paperWhite = display.paperWhiteNits, peak = display.maxToneMapLuminance;
            var tonemapping = VolumeManager.instance.stack.GetComponent<Tonemapping>();
            if (tonemapping != null)
            {
                if (!tonemapping.detectPaperWhite.value)
                    paperWhite = tonemapping.paperWhite.value;
                if (!tonemapping.detectBrightnessLimits.value)
                    peak = tonemapping.maxNits.value;
            }
            return LolaRetroHdr.For(display.displayColorGamut, paperWhite, peak, true);
        }
    }
}
