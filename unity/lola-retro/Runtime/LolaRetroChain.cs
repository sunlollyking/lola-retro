using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LolaRetro
{
    enum ScaleKind { Source, Absolute, Viewport }

    /// <summary>Size of a pass's output along one axis, as in a RetroArch preset.</summary>
    readonly struct PassScale
    {
        public readonly ScaleKind kind;
        public readonly float value;
        public PassScale(ScaleKind kind, float value) { this.kind = kind; this.value = value; }

        // RetroArch rounds to the nearest whole pixel
        public int Size(int source, int viewport)
        {
            float s = kind == ScaleKind.Absolute ? value : (kind == ScaleKind.Source ? source : viewport) * value;
            return Math.Max(1, (int)Math.Floor(s + 0.5f));
        }
    }

    /// <summary>
    /// Runs Lola Retro's passes for one camera into a command buffer: Unity's source pass makes
    /// the console's frame, then the passes of the RetroArch version run as there. It keeps the
    /// targets between the passes and the CRT pass's last frame (the phosphor's afterglow).
    /// </summary>
    public sealed class LolaRetroChain : IDisposable
    {
        static readonly int idSourceSize = Shader.PropertyToID("LR_SourceSize");
        static readonly int idOriginalSize = Shader.PropertyToID("LR_OriginalSize");
        static readonly int idOutputSize = Shader.PropertyToID("LR_OutputSize");
        static readonly int idFrameCount = Shader.PropertyToID("LR_FrameCountI");
        static readonly int idUVTop = Shader.PropertyToID("LR_UVTop");
        static readonly int idOutputLinear = Shader.PropertyToID("LR_OutputLinear");
        static readonly int idInput = Shader.PropertyToID("LR_Input");
        static readonly int idInputRect = Shader.PropertyToID("LR_InputRect");
        static readonly int idInputLinear = Shader.PropertyToID("LR_InputLinear");
        static readonly int idSource = Shader.PropertyToID("Source");
        static readonly int idOriginal = Shader.PropertyToID("Original");
        static readonly int idDecoded = Shader.PropertyToID("LR_Decoded");
        static readonly int idGlow = Shader.PropertyToID("LR_Glow");
        static readonly int idBeam = Shader.PropertyToID("LR_Beam");
        static readonly int idFeedback = Shader.PropertyToID("LR_CRTFeedback");

        readonly Material material;
        readonly MaterialPropertyBlock[] blocks = new MaterialPropertyBlock[LolaRetroPasses.Count];
        readonly RenderTexture[] targets = new RenderTexture[LolaRetroPasses.Count];
        readonly RenderTexture[] afterglow = new RenderTexture[2];   // the CRT pass: this frame and the last
        uint frame;

        public LolaRetroChain(Shader shader)
        {
            material = CoreUtilsLite.CreateMaterial(shader);
            for (int i = 0; i < blocks.Length; i++)
                blocks[i] = new MaterialPropertyBlock();
        }

        /// <summary>
        /// Records the passes into cmd.
        /// input: what Unity rendered (any size), inputRect: the part of it to use, in texels from
        /// the bottom left; console: the size of the console's frame made from it; output: the
        /// target, picture: the part of it the tube's picture area fills (the rest is cleared to
        /// black); outputSize: the output's size.
        /// </summary>
        public void Render(CommandBuffer cmd, RenderTargetIdentifier input, Rect inputRect, Vector2Int console,
                           RenderTargetIdentifier output, Vector2Int outputSize, RectInt picture, LolaRetroSettings settings)
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            settings.Apply(material);
            material.SetFloat(idUVTop, SystemInfo.graphicsUVStartsAtTop ? 1f : 0f);
            material.SetFloat(idOutputLinear, linear ? 1f : 0f);
            material.SetFloat(idInputLinear, linear ? 1f : 0f);

            // which passes run, and how large each output is
            bool show = settings.cable == Cable.TVShow;
            bool monitor = settings.consoleTiming == ConsoleTiming.PCVGA || settings.monitorInMillimetres;
            var size = new Vector2Int[LolaRetroPasses.Count];
            var next = new int[LolaRetroPasses.Count];     // the pass that reads each one's output
            size[LolaRetroPasses.Source] = console;
            int prev = LolaRetroPasses.Source;
            for (int i = LolaRetroPasses.Native; i < LolaRetroPasses.Count; i++)
            {
                if (i == LolaRetroPasses.ShowLines && !show)
                    continue;
                PassScale sx = i == LolaRetroPasses.Encode && monitor ? LolaRetroPasses.EncodeMonitorX : LolaRetroPasses.X[i];
                // the beam passes are 8192 wide where the GPU allows it (a narrower beam pass only
                // samples the line's profile more coarsely)
                size[i] = new Vector2Int(Math.Min(sx.Size(size[prev].x, picture.width), SystemInfo.maxTextureSize),
                                         LolaRetroPasses.Y[i].Size(size[prev].y, picture.height));
                next[prev] = i;
                prev = i;
            }

            // each target gets the filter of the pass that reads it (OpenGL ES and WebGL have no
            // samplers of their own in the shader)
            for (int i = 0; i < LolaRetroPasses.Present; i++)
            {
                if (i != LolaRetroPasses.Crt && (i != LolaRetroPasses.ShowLines || show))
                    Allocate(ref targets[i], size[i], LolaRetroPasses.Float[i], LolaRetroPasses.Linear[next[i]], i);
            }
            bool fresh = Allocate(ref afterglow[0], size[LolaRetroPasses.Crt], true, LolaRetroPasses.Linear[LolaRetroPasses.Present], LolaRetroPasses.Crt);
            fresh |= Allocate(ref afterglow[1], size[LolaRetroPasses.Crt], true, LolaRetroPasses.Linear[LolaRetroPasses.Present], LolaRetroPasses.Crt);
            if (fresh)
            {
                // no afterglow from before
                for (int k = 0; k < 2; k++)
                {
                    cmd.SetRenderTarget(afterglow[k]);
                    cmd.ClearRenderTarget(false, true, Color.clear);
                }
            }
            RenderTexture crt = afterglow[frame & 1], last = afterglow[(frame + 1) & 1];
            targets[LolaRetroPasses.Crt] = crt;

            // Unity's source pass: the console's frame
            cmd.SetGlobalTexture(idInput, input);
            var b = blocks[LolaRetroPasses.Source];
            b.SetVector(idInputRect, new Vector4(inputRect.x, inputRect.y, inputRect.width, inputRect.height));
            Draw(cmd, LolaRetroPasses.Source, targets[LolaRetroPasses.Source], size[LolaRetroPasses.Source], console, console, b);

            // the RetroArch passes
            RenderTexture original = targets[LolaRetroPasses.Source];
            RenderTexture source = original;
            Vector2Int sourceSize = console;
            for (int i = LolaRetroPasses.Native; i <= LolaRetroPasses.Present; i++)
            {
                if (i == LolaRetroPasses.ShowLines && !show)
                    continue;
                b = blocks[i];
                b.SetTexture(idSource, source);
                b.SetTexture(idOriginal, original);
                b.SetTexture(idDecoded, targets[LolaRetroPasses.Decode] ?? original);
                b.SetTexture(idGlow, targets[LolaRetroPasses.GlowV] ?? original);
                b.SetTexture(idBeam, (show ? targets[LolaRetroPasses.ShowLines] : targets[LolaRetroPasses.Beam]) ?? original);
                b.SetTexture(idFeedback, last);
                if (i == LolaRetroPasses.Present)
                {
                    // into the picture area of the output, black around it
                    cmd.SetRenderTarget(output);
                    if (picture.width != outputSize.x || picture.height != outputSize.y)
                        cmd.ClearRenderTarget(false, true, Color.black);
                    cmd.SetViewport(new Rect(picture.x, picture.y, picture.width, picture.height));
                    SetSizes(b, sourceSize, console, new Vector2Int(picture.width, picture.height));
                    cmd.DrawProcedural(Matrix4x4.identity, material, i, MeshTopology.Triangles, 3, 1, b);
                }
                else
                {
                    Draw(cmd, i, targets[i], sourceSize, console, size[i], b);
                }
                source = targets[i];
                sourceSize = size[i];
            }
            frame++;
        }

        void Draw(CommandBuffer cmd, int pass, RenderTexture target, Vector2Int source, Vector2Int original,
                  Vector2Int outSize, MaterialPropertyBlock b)
        {
            cmd.SetRenderTarget(target, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            SetSizes(b, source, original, outSize);
            cmd.DrawProcedural(Matrix4x4.identity, material, pass, MeshTopology.Triangles, 3, 1, b);
        }

        void SetSizes(MaterialPropertyBlock b, Vector2Int source, Vector2Int original, Vector2Int output)
        {
            b.SetVector(idSourceSize, SizeVector(source));
            b.SetVector(idOriginalSize, SizeVector(original));
            b.SetVector(idOutputSize, SizeVector(output));
            b.SetInteger(idFrameCount, (int)(frame & 0x7fffffff));
        }

        static Vector4 SizeVector(Vector2Int s) => new Vector4(s.x, s.y, 1f / s.x, 1f / s.y);

        // a target of this size and format, read with this filter; true when it had to be made
        static bool Allocate(ref RenderTexture rt, Vector2Int size, bool isFloat, bool linear, int pass)
        {
            var format = isFloat ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R8G8B8A8_UNorm;
            var filter = linear ? FilterMode.Bilinear : FilterMode.Point;
            if (rt != null && rt.width == size.x && rt.height == size.y && rt.graphicsFormat == format)
            {
                rt.filterMode = filter;          // the beam pass's reader changes with the cable
                return false;
            }
            Release(ref rt);
            rt = new RenderTexture(new RenderTextureDescriptor(size.x, size.y, format, 0))
            {
                name = "Lola Retro pass " + pass,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return true;
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            CoreUtilsLite.Destroy(rt);
            rt = null;
        }

        public void Dispose()
        {
            for (int i = 0; i < targets.Length; i++)
            {
                if (i != LolaRetroPasses.Crt)
                    Release(ref targets[i]);
                targets[i] = null;
            }
            Release(ref afterglow[0]);
            Release(ref afterglow[1]);
            CoreUtilsLite.Destroy(material);
        }
    }

    // What this package needs of CoreUtils, which only the render pipelines have
    static class CoreUtilsLite
    {
        public static Material CreateMaterial(Shader shader) => new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

        public static void Destroy(UnityEngine.Object o)
        {
            if (o == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(o);
            else
                UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
