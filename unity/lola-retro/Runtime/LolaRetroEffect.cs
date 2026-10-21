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

        LolaRetroChain chain;
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
            chain?.Dispose();
            chain = null;
            cmd?.Release();
            cmd = null;
        }

        /// <summary>The chain of this camera, made when it is first needed (null without the shader).</summary>
        internal LolaRetroChain Chain
        {
            get
            {
                if (chain == null)
                {
                    if (shader == null)
                        shader = Resources.Load<Shader>("LolaRetro");
                    if (shader == null || !shader.isSupported)
                        return null;
                    chain = new LolaRetroChain(shader);
                }
                return chain;
            }
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

        /// <summary>Records the chain from the camera's image (or Input) into output.</summary>
        internal bool Record(CommandBuffer buffer, RenderTargetIdentifier image, Vector2Int imageSize,
                             RenderTargetIdentifier output, Vector2Int outputSize)
        {
            var c = Chain;
            if (c == null)
                return false;
            Vector2Int console = ConsoleSize(imageSize);
            RenderTargetIdentifier src = input != null ? new RenderTargetIdentifier(input) : image;
            Rect rect = input != null ? new Rect(0, 0, input.width, input.height) : new Rect(0, 0, imageSize.x, imageSize.y);
            c.Render(buffer, src, rect, console, output, outputSize, PictureArea(outputSize, console), settings);
            return true;
        }

        // Built-in render pipeline (the render pipelines do not call it)
        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (Chain == null)
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
            Record(cmd, src, new Vector2Int(src.width, src.height), target, new Vector2Int(target.width, target.height));
            Graphics.ExecuteCommandBuffer(cmd);
            if (dst == null)
            {
                Graphics.Blit(target, dst);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
