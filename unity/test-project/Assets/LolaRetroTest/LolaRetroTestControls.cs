using LolaRetro;
using UnityEngine;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// The test scene's controls (on the display camera, next to Lola Retro). The game camera
/// renders the picture at its own resolution into a render texture, the display camera shows it
/// through Lola Retro (Input). In Play mode:
///   Left / Right  previous / next preset          C  next cable (RF, composite, S-Video, RGB, TV show)
///   F             flat screen on / off            T  next test picture
///   M             Input texture / camera image    Space  ball on / off      H  this help
/// </summary>
[ExecuteAlways]
public class LolaRetroTestControls : MonoBehaviour
{
    public LolaRetroEffect effect;
    public Camera gameCamera;          // renders the picture at the console's resolution
    public RenderTexture console;      // what it renders into: Lola Retro's Input
    public SpriteRenderer picture;
    public SpriteRenderer ball;
    public Sprite[] pictures;

    int pictureIndex;
    bool cameraImage;                  // M: Lola Retro makes the console's picture from the display camera's image
    bool help = true;
    Vector2 ballPos = new Vector2(40, 30), ballVel = new Vector2(1, 1);

    static readonly LolaRetroPreset[] presets = (LolaRetroPreset[])System.Enum.GetValues(typeof(LolaRetroPreset));

    void OnEnable()
    {
        SetMaterials();
        ShowPicture(pictureIndex);
    }

    // Sprites need the pipeline's own sprite shader
    void SetMaterials()
    {
        bool srp = GraphicsSettings.currentRenderPipeline != null;
        var shader = Shader.Find(srp ? "Universal Render Pipeline/2D/Sprite-Unlit-Default" : "Sprites/Default");
        if (shader == null)
            return;
        foreach (var r in new[] { picture, ball })
            if (r != null && (r.sharedMaterial == null || r.sharedMaterial.shader != shader))
                r.sharedMaterial = new Material(shader) { name = "Sprite (" + shader.name + ")" };
    }

    // The picture at 1 unit per pixel, the game camera and its render texture exactly its size
    void ShowPicture(int k)
    {
        if (pictures == null || pictures.Length == 0 || picture == null)
            return;
        pictureIndex = (k + pictures.Length) % pictures.Length;
        Sprite s = pictures[pictureIndex];
        picture.sprite = s;
        int w = Mathf.RoundToInt(s.rect.width), h = Mathf.RoundToInt(s.rect.height);
        if (console != null && (console.width != w || console.height != h))
        {
            console.Release();
            console.width = w;
            console.height = h;
            console.Create();
        }
        foreach (var c in new[] { gameCamera, effect != null ? effect.GetComponent<Camera>() : null })
        {
            if (c == null)
                continue;
            c.orthographic = true;
            c.orthographicSize = h * 0.5f;
        }
        effect.lines = h;
        ballPos = new Vector2(w * 0.25f, h * 0.5f);
        SetMode(cameraImage);
    }

    void SetMode(bool fromCamera)
    {
        cameraImage = fromCamera;
        var display = effect.GetComponent<Camera>();
        gameCamera.enabled = !fromCamera;
        gameCamera.targetTexture = console;
        effect.input = fromCamera ? null : console;
        effect.width = 0;
        display.cullingMask = fromCamera ? ~0 : 0;
    }

    void Update()
    {
        if (effect == null || gameCamera == null)
            return;
        MoveBall();
        if (!Application.isPlaying)
            return;
        int p = System.Array.IndexOf(presets, effect.preset);
        if (Pressed(KeyCode.RightArrow)) effect.ApplyPreset(presets[(p + 1) % presets.Length], effect.flatScreen);
        if (Pressed(KeyCode.LeftArrow)) effect.ApplyPreset(presets[(p - 1 + presets.Length) % presets.Length], effect.flatScreen);
        if (Pressed(KeyCode.F)) effect.ApplyPreset(effect.preset, !effect.flatScreen);
        if (Pressed(KeyCode.C)) effect.settings.cable = (Cable)(((int)effect.settings.cable + 1) % 5);
        if (Pressed(KeyCode.T)) ShowPicture(pictureIndex + 1);
        if (Pressed(KeyCode.M)) SetMode(!cameraImage);
        if (Pressed(KeyCode.Space) && ball != null) ball.enabled = !ball.enabled;
        if (Pressed(KeyCode.H)) help = !help;
    }

    // a ball bouncing over the picture, a whole pixel per frame, for the afterglow and the dots that move
    void MoveBall()
    {
        if (ball == null || picture.sprite == null || !Application.isPlaying)
            return;
        Vector2 size = picture.sprite.rect.size;
        ballPos += ballVel;
        if (ballPos.x < 8 || ballPos.x > size.x - 8) ballVel.x = -ballVel.x;
        if (ballPos.y < 8 || ballPos.y > size.y - 8) ballVel.y = -ballVel.y;
        ball.transform.position = new Vector3(Mathf.Round(ballPos.x) - size.x * 0.5f, Mathf.Round(ballPos.y) - size.y * 0.5f, -1f);
    }

    static bool Pressed(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k == null)
            return false;
        switch (key)
        {
            case KeyCode.RightArrow: return k.rightArrowKey.wasPressedThisFrame;
            case KeyCode.LeftArrow: return k.leftArrowKey.wasPressedThisFrame;
            case KeyCode.Space: return k.spaceKey.wasPressedThisFrame;
            default: return k[(Key)System.Enum.Parse(typeof(Key), key.ToString())].wasPressedThisFrame;
        }
#else
        return Input.GetKeyDown(key);
#endif
    }

    void OnGUI()
    {
        if (!help || effect == null)
            return;
        string pipeline = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().Name : "Built-in";
        string text = $"Lola Retro test ({pipeline})\n" +
                      $"Preset: {effect.preset}{(effect.flatScreen ? ", flat" : "")}   Cable: {effect.settings.cable}\n" +
                      $"Picture: {(pictures != null && pictures.Length > 0 ? pictures[pictureIndex].name : "-")}   " +
                      $"Source: {(cameraImage ? "camera image" : "Input texture")}\n" +
                      (Application.isPlaying ? "Left/Right preset   C cable   F flat   T picture   M source   Space ball   H help"
                                             : "Press Play for the keys");
        var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = false };
        GUI.Box(new Rect(10, 10, 620, 84), text, style);
    }
}
