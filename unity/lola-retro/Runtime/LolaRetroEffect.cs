using UnityEngine;
using UnityEngine.Rendering;

namespace LolaRetro
{
    public enum TubeShape
    {
        [InspectorName("Auto (4:3, or the picture's shape when it is wider)")] Auto,
        [InspectorName("4:3")] Standard,
        [InspectorName("16:9 (a widescreen TV)")] Widescreen,
        [InspectorName("Fill the screen")] Fill,
    }

    /// <summary>
    /// Lola Retro on this camera: what the camera renders (or the Input texture) is shown on the
    /// CRT of the chosen preset. Built-in render pipeline: this is all. URP: add Lola Retro to the
    /// renderer too (the Universal Renderer asset: Add Renderer Feature, Lola Retro).
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    [AddComponentMenu("Rendering/Lola Retro")]
    [HelpURL("https://github.com/zoran123456/lola-retro/tree/main/unity/lola-retro")]
    public class LolaRetroEffect : MonoBehaviour
    {
        [Tooltip("The TV or monitor. Choosing one sets every value below; change them after that as you like.")]
        public LolaRetroPreset preset = LolaRetroPreset.ConsumerTVComposite;

        [Tooltip("A flat screen with square corners (curvature and corners 0).")]
        public bool flatScreen;

        [Header("The console's picture")]
        [Tooltip("Show this texture instead of what this camera renders: a render texture at the console's " +
                 "resolution that another camera renders the game into. Every texel is then one console pixel.")]
        public Texture input;

        [Tooltip("Lines of the console's picture, when it is made from this camera's image (no Input). " +
                 "224 or 240 for most 8- and 16-bit consoles, 480 for an interlaced picture.")]
        [Range(120, 576)] public int lines = 240;

        [Tooltip("Pixels per line, when it is made from this camera's image. 0: from the camera's shape " +
                 "(lines x width / height), so the pixels are square.")]
        [Range(0, 1280)] public int width;

        [Tooltip("The shape of the tube. The picture area sits in the middle of the screen, black around it. " +
                 "Around the tube (other than Black) always fills the screen and draws a 4:3 tube in it.")]
        public TubeShape tubeShape = TubeShape.Auto;

        public LolaRetroSettings settings = LolaRetroPresets.Get(LolaRetroPreset.ConsumerTVComposite);

        [SerializeField, HideInInspector] LolaRetroPreset appliedPreset = LolaRetroPreset.ConsumerTVComposite;
        [SerializeField, HideInInspector] bool appliedFlat;
        [SerializeField, HideInInspector] Shader shader;

        // one chain per eye in XR (each keeps its own afterglow)
        readonly LolaRetroChain[] chains = new LolaRetroChain[2];
        CommandBuffer cmd;

        /// <summary>Sets every value to the preset's.</summary>
        public void ApplyPreset(LolaRetroPreset p, bool flat = false)
        {
            preset = appliedPreset = p;
            flatScreen = appliedFlat = flat;
            settings = LolaRetroPresets.Get(p, flat);
        }

        void OnValidate()
        {
            // a new preset in the Inspector sets the values
            if (preset != appliedPreset || flatScreen != appliedFlat)
                ApplyPreset(preset, flatScreen);
        }

        void Reset() => ApplyPreset(preset, flatScreen);

        void OnDisable()
        {
            for (int i = 0; i < chains.Length; i++)
            {
                chains[i]?.Dispose();
                chains[i] = null;
            }
            cmd?.Release();
            cmd = null;
        }

        /// <summary>The shader, when this platform can run it.</summary>
        internal bool Supported
        {
            get
            {
                if (shader == null)
                    shader = Resources.Load<Shader>("LolaRetro");
                return shader != null && shader.isSupported;
            }
        }

        /// <summary>The chain of this camera's eye (0 without XR), made when it is first needed.</summary>
        LolaRetroChain ChainFor(int eye)
        {
            eye = Mathf.Clamp(eye, 0, chains.Length - 1);
            return chains[eye] ??= new LolaRetroChain(shader);
        }

        /// <summary>The size of the console's frame for an input of this size.</summary>
        internal Vector2Int ConsoleSize(Vector2Int inputSize)
        {
            if (input != null)
                return new Vector2Int(input.width, input.height);
            int h = Mathf.Max(1, lines);
            int w = width > 0 ? width : Mathf.Max(1, Mathf.RoundToInt(h * (float)inputSize.x / Mathf.Max(1, inputSize.y)));
            return new Vector2Int(w, h);
        }

        /// <summary>The picture area in an output of this size: the tube's shape, as large as fits, in the middle.</summary>
        internal RectInt PictureArea(Vector2Int output, Vector2Int console)
        {
            float frame = (float)console.x / console.y;
            float aspect = tubeShape switch
            {
                TubeShape.Standard => 4f / 3f,
                TubeShape.Widescreen => 16f / 9f,
                TubeShape.Fill => (float)output.x / output.y,
                _ => frame <= 1.5f ? 4f / 3f : frame,
            };
            if (settings.aroundTheTube != AroundTheTube.Black)
                aspect = (float)output.x / output.y;    // the shader puts the tube in the screen itself
            int w = output.x, h = output.y;
            if (w > h * aspect)
                w = Mathf.RoundToInt(h * aspect);
            else
                h = Mathf.RoundToInt(w / aspect);
            return new RectInt((output.x - w) / 2, (output.y - h) / 2, w, h);
        }

        /// <summary>Records the chain from the camera's image (or Input) into output, for one eye.</summary>
        internal void Record(CommandBuffer buffer, in LolaRetroImage image, in LolaRetroImage output, int eye, in LolaRetroHdr hdr)
        {
            LolaRetroImage src = input != null
                ? new LolaRetroImage(new RenderTargetIdentifier(input), -1, new RectInt(0, 0, input.width, input.height))
                : image;
            Vector2Int console = ConsoleSize(src.rect.size);
            Vector2Int outSize = output.rect.size;
            ChainFor(eye).Render(buffer, src, console, output, PictureArea(outSize, console), settings, hdr);
        }

        // Built-in render pipeline (the render pipelines do not call it)
        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (!Supported)
            {
                Graphics.Blit(src, dst);
                return;
            }
            cmd ??= new CommandBuffer { name = "Lola Retro" };
            cmd.Clear();
            // the chain draws into a render texture (8 bits hold the last pass's picture exactly);
            // for the screen, Graphics.Blit then turns it the right way up where needed
            RenderTexture target = dst;
            if (dst == null)
                target = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32,
                    QualitySettings.activeColorSpace == ColorSpace.Linear ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            Record(cmd, new LolaRetroImage(src, -1, new RectInt(0, 0, src.width, src.height)),
                   new LolaRetroImage(target, -1, new RectInt(0, 0, target.width, target.height)), 0, default);
            Graphics.ExecuteCommandBuffer(cmd);
            if (dst == null)
            {
                Graphics.Blit(target, dst);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
