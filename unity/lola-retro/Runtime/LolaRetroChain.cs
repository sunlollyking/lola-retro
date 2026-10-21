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

    /// <summary>A texture the chain reads or draws into.</summary>
    public struct LolaRetroImage
    {
        /// <summary>The texture.</summary>
        public RenderTargetIdentifier texture;
        /// <summary>Its slice when it is a texture array (one eye in XR; HDRP's camera image), -1 for a 2D texture.</summary>
        public int slice;
        /// <summary>The camera's part of it, in texels from the bottom left (render textures can be larger, as HDRP's are).</summary>
        public RectInt rect;

        public LolaRetroImage(RenderTargetIdentifier texture, int slice, RectInt rect)
        {
            this.texture = texture;
            this.slice = slice;
            this.rect = rect;
        }
    }

    /// <summary>
    /// HDR display output. Unity's own last pass codes the picture for the display, and the
    /// picture before it is linear light in nits in the display's colour space (URP with
    /// post-processing, HDRP) or linear Rec.709 (URP without post-processing).
    /// </summary>
    public struct LolaRetroHdr
    {
        public bool active;
        /// <summary>How bright a full white field is, and the most the display shows (nits).</summary>
        public float paperWhite, peak;
        /// <summary>Linear Rec.709 light (1.0 = paper white) -> the pipeline's picture (the 3 x 3 part).</summary>
        public Matrix4x4 fromRec709;

        // Rec.709 to the display's primaries, both D65 (as Unity's HDROutput.hlsl)
        static readonly Matrix4x4 rec709ToRec2020 = Rows(new Vector3(0.627402f, 0.329292f, 0.043306f),
                                                         new Vector3(0.069095f, 0.919544f, 0.011360f),
                                                         new Vector3(0.016394f, 0.088028f, 0.895578f));
        static readonly Matrix4x4 rec709ToP3 = Rows(new Vector3(0.822462f, 0.177538f, 0.0f),
                                                    new Vector3(0.033194f, 0.966806f, 0.0f),
                                                    new Vector3(0.017083f, 0.072397f, 0.910520f));

        /// <summary>
        /// HDR output to a display with these primaries. inNits: the pipeline's picture is in nits
        /// in the display's colour space; otherwise it stays linear Rec.709 relative to paper white.
        /// </summary>
        public static LolaRetroHdr For(ColorGamut gamut, float paperWhite, float peak, bool inNits)
        {
            Matrix4x4 m = Matrix4x4.identity;
            if (inNits)
            {
                ColorPrimaries primaries = ColorGamutUtility.GetColorPrimaries(gamut);
                if (primaries == ColorPrimaries.Rec2020)
                    m = rec709ToRec2020;
                else if (primaries == ColorPrimaries.P3)
                    m = rec709ToP3;
                m = Matrix4x4.Scale(new Vector3(paperWhite, paperWhite, paperWhite)) * m;
            }
            return new LolaRetroHdr { active = true, paperWhite = Mathf.Max(paperWhite, 1f), peak = Mathf.Max(peak, paperWhite), fromRec709 = m };
        }

        static Matrix4x4 Rows(Vector3 a, Vector3 b, Vector3 c)
        {
            var m = Matrix4x4.identity;
            m.SetRow(0, new Vector4(a.x, a.y, a.z, 0f));
            m.SetRow(1, new Vector4(b.x, b.y, b.z, 0f));
            m.SetRow(2, new Vector4(c.x, c.y, c.z, 0f));
            return m;
        }
    }

    /// <summary>
    /// Runs Lola Retro's passes for one camera (one eye in XR) into a command buffer: Unity's source
    /// pass makes the console's frame, then the passes of the RetroArch version run as there. It
    /// keeps the targets between the passes and the CRT pass's last frame (the phosphor's afterglow).
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
        static readonly int idInputArray = Shader.PropertyToID("LR_InputArray");
        static readonly int idInputSlice = Shader.PropertyToID("LR_InputSlice");
        static readonly int idInputRect = Shader.PropertyToID("LR_InputRect");
        static readonly int idInputLinear = Shader.PropertyToID("LR_InputLinear");
        static readonly int[] idInputRows = { Shader.PropertyToID("LR_InputRow0"), Shader.PropertyToID("LR_InputRow1"), Shader.PropertyToID("LR_InputRow2") };
        static readonly int[] idOutputRows = { Shader.PropertyToID("LR_OutputRow0"), Shader.PropertyToID("LR_OutputRow1"), Shader.PropertyToID("LR_OutputRow2") };
        static readonly int idNitsWhite = Shader.PropertyToID("LR_NITS_WHITE");
        static readonly int idNitsPeak = Shader.PropertyToID("LR_NITS_PEAK");
        static readonly int idSource = Shader.PropertyToID("Source");
        static readonly int idOriginal = Shader.PropertyToID("Original");
        static readonly int idDecoded = Shader.PropertyToID("LR_Decoded");
        static readonly int idGlow = Shader.PropertyToID("LR_Glow");
        static readonly int idBeam = Shader.PropertyToID("LR_Beam");
        static readonly int idFeedback = Shader.PropertyToID("LR_CRTFeedback");

        readonly Material material;
        readonly MaterialPropertyBlock[] blocks = new MaterialPropertyBlock[LolaRetroPasses.Count];
        // the targets of the RetroArch chain's passes (Source to Present), by the SDR pass's number
        readonly RenderTexture[] targets = new RenderTexture[LolaRetroPasses.Present + 1];
        readonly RenderTexture[] afterglow = new RenderTexture[2];   // the CRT pass: this frame and the last
        uint frame;

        public LolaRetroChain(Shader shader)
        {
            material = CoreUtilsLite.CreateMaterial(shader);
            for (int i = 0; i < blocks.Length; i++)
                blocks[i] = new MaterialPropertyBlock();
        }

        /// <summary>
        /// Records the passes into cmd. input: what Unity rendered (any size); console: the size of
        /// the console's frame made from it; output: the target, picture: the part of output's rect
        /// the tube's picture area fills (the rest is cleared to black).
        /// </summary>
        public void Render(CommandBuffer cmd, in LolaRetroImage input, Vector2Int console, in LolaRetroImage output,
                           RectInt picture, LolaRetroSettings settings, in LolaRetroHdr hdr)
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            settings.Apply(material);
            material.SetFloat(idUVTop, SystemInfo.graphicsUVStartsAtTop ? 1f : 0f);
            material.SetFloat(idOutputLinear, linear ? 1f : 0f);
            material.SetFloat(idInputLinear, linear ? 1f : 0f);
            // HDR: the light the pipeline has around Lola Retro, and the display's levels
            Matrix4x4 toPipeline = hdr.active ? hdr.fromRec709 : Matrix4x4.identity;
            Matrix4x4 fromPipeline = toPipeline.inverse;
            for (int r = 0; r < 3; r++)
            {
                material.SetVector(idOutputRows[r], toPipeline.GetRow(r));
                material.SetVector(idInputRows[r], fromPipeline.GetRow(r));
            }
            material.SetFloat(idNitsWhite, hdr.active ? hdr.paperWhite : 200f);
            material.SetFloat(idNitsPeak, hdr.active ? hdr.peak : 400f);

            // which passes run, and how large each output is
            bool show = settings.cable == Cable.TVShow;
            bool monitor = settings.consoleTiming == ConsoleTiming.PCVGA || settings.monitorInMillimetres;
            var size = new Vector2Int[LolaRetroPasses.Present + 1];
            var next = new int[LolaRetroPasses.Present + 1];     // the pass that reads each one's output
            size[LolaRetroPasses.Source] = console;
            int prev = LolaRetroPasses.Source;
            for (int i = LolaRetroPasses.Native; i <= LolaRetroPasses.Present; i++)
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
            for (int i = 0; i < LolaRetroPasses.Crt; i++)
            {
                if (i != LolaRetroPasses.ShowLines || show)
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

            // Unity's source pass: the console's frame, from a 2D texture or one slice of an array
            int sourcePass = input.slice >= 0 ? LolaRetroPasses.SourceArray : LolaRetroPasses.Source;
            var b = blocks[sourcePass];
            if (input.slice >= 0)
            {
                cmd.SetGlobalTexture(idInputArray, input.texture);
                b.SetFloat(idInputSlice, input.slice);
            }
            else
            {
                cmd.SetGlobalTexture(idInput, input.texture);
            }
            b.SetVector(idInputRect, new Vector4(input.rect.x, input.rect.y, input.rect.width, input.rect.height));
            Draw(cmd, sourcePass, targets[LolaRetroPasses.Source], console, console, console, b);

            // the RetroArch passes; on an HDR display the CRT pass for HDR and Unity's HDR last pass
            RenderTexture original = targets[LolaRetroPasses.Source];
            RenderTexture source = original;
            Vector2Int sourceSize = console;
            for (int i = LolaRetroPasses.Native; i <= LolaRetroPasses.Present; i++)
            {
                if (i == LolaRetroPasses.ShowLines && !show)
                    continue;
                int pass = !hdr.active ? i : i == LolaRetroPasses.Crt ? LolaRetroPasses.CrtHdr
                                           : i == LolaRetroPasses.Present ? LolaRetroPasses.OutputHdr : i;
                b = blocks[pass];
                b.SetTexture(idSource, source);
                b.SetTexture(idOriginal, original);
                b.SetTexture(idDecoded, targets[LolaRetroPasses.Decode] ?? original);
                b.SetTexture(idGlow, targets[LolaRetroPasses.GlowV] ?? original);
                b.SetTexture(idBeam, (show ? targets[LolaRetroPasses.ShowLines] : targets[LolaRetroPasses.Beam]) ?? original);
                b.SetTexture(idFeedback, last);
                if (i == LolaRetroPasses.Present)
                {
                    // into the picture area of the output, black around it
                    cmd.SetRenderTarget(output.texture, 0, CubemapFace.Unknown, Math.Max(output.slice, 0));
                    if (picture.width != output.rect.width || picture.height != output.rect.height)
                    {
                        cmd.SetViewport(new Rect(output.rect.x, output.rect.y, output.rect.width, output.rect.height));
                        cmd.ClearRenderTarget(false, true, Color.black);
                    }
                    cmd.SetViewport(new Rect(output.rect.x + picture.x, output.rect.y + picture.y, picture.width, picture.height));
                    SetSizes(b, sourceSize, console, new Vector2Int(picture.width, picture.height));
                    cmd.DrawProcedural(Matrix4x4.identity, material, pass, MeshTopology.Triangles, 3, 1, b);
                }
                else
                {
                    Draw(cmd, pass, targets[i], sourceSize, console, size[i], b);
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
