using System.IO;
using System.Linq;
using LolaRetro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the test scene the first time the project opens (and from the menu Lola Retro Test):
/// the test pictures as sprites, a game camera that renders them at their own resolution into a
/// render texture, and the display camera with Lola Retro showing that texture.
/// </summary>
[InitializeOnLoad]
static class LolaRetroTestSetup
{
    const string Folder = "Assets/LolaRetroTest";
    const string ScenePath = Folder + "/LolaRetroTest.unity";
    const string UrpRequest = "LolaRetroTest.SetUpUrp";

    static LolaRetroTestSetup()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists(ScenePath))
                Build();
        };
    }

    [MenuItem("Lola Retro Test/Rebuild the test scene", priority = 0)]
    static void Build()
    {
        // linear colour space, as most projects (the shader handles both)
        PlayerSettings.colorSpace = ColorSpace.Linear;

        // the pictures: sprites at one unit per pixel, unfiltered and uncompressed
        var paths = Directory.GetFiles(Folder + "/Images", "*.png").Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToList();
        string ballPath = Folder + "/Ball.png";
        if (!File.Exists(ballPath))
            File.WriteAllBytes(ballPath, BallTexture().EncodeToPNG());
        AssetDatabase.Refresh();
        foreach (var p in paths.Append(ballPath))
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(p);
            importer.textureType = TextureImporterType.Sprite;
            importer.spritePixelsPerUnit = 1;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.SaveAndReimport();
        }
        // the platformer first
        var sprites = paths.OrderBy(p => p.Contains("platformer") ? 0 : 1).Select(AssetDatabase.LoadAssetAtPath<Sprite>).ToArray();

        // the game camera's target: the console's resolution
        string rtPath = Folder + "/Console.renderTexture";
        var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
        if (rt == null)
        {
            rt = new RenderTexture(256, 224, 16) { filterMode = FilterMode.Point, name = "Console" };
            AssetDatabase.CreateAsset(rt, rtPath);
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var picture = new GameObject("Picture").AddComponent<SpriteRenderer>();
        picture.sprite = sprites[0];
        var ball = new GameObject("Ball").AddComponent<SpriteRenderer>();
        ball.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ballPath);
        ball.sortingOrder = 1;

        var game = new GameObject("Game camera (console resolution)").AddComponent<Camera>();
        game.transform.position = new Vector3(0, 0, -10);
        game.orthographic = true;
        game.clearFlags = CameraClearFlags.SolidColor;
        game.backgroundColor = Color.black;
        game.depth = -1;
        game.targetTexture = rt;

        var display = new GameObject("Display camera (Lola Retro)").AddComponent<Camera>();
        display.tag = "MainCamera";
        display.transform.position = new Vector3(0, 0, -10);
        display.orthographic = true;
        display.clearFlags = CameraClearFlags.SolidColor;
        display.backgroundColor = Color.black;
        display.cullingMask = 0;
        display.depth = 0;
        var effect = display.gameObject.AddComponent<LolaRetroEffect>();
        effect.ApplyPreset(LolaRetroPreset.SystemSNES);
        effect.input = rt;

        var controls = display.gameObject.AddComponent<LolaRetroTestControls>();
        controls.effect = effect;
        controls.gameCamera = game;
        controls.console = rt;
        controls.picture = picture;
        controls.ball = ball;
        controls.pictures = sprites;
        controls.enabled = false;      // OnEnable sets the scene up for the first picture
        controls.enabled = true;

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("Lola Retro test: scene built (" + ScenePath + "). Press Play; the keys are on the screen.");
    }

    // a white ball, 12 pixels across, with a darker rim
    static Texture2D BallTexture()
    {
        var t = new Texture2D(12, 12, TextureFormat.RGBA32, false);
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 12; x++)
            {
                float d = new Vector2(x - 5.5f, y - 5.5f).magnitude;
                t.SetPixel(x, y, d > 6f ? Color.clear : d > 4.6f ? new Color(0.9f, 0.2f, 0.2f) : Color.white);
            }
        t.Apply();
        return t;
    }

    [MenuItem("Lola Retro Test/Use URP", priority = 20)]
    static void UseUrp()
    {
        // the URP part (Editor/URP) compiles once URP is installed; it finishes the setup then
        SessionState.SetBool(UrpRequest, true);
        if (!Directory.Exists("Library/PackageCache") || !Directory.GetDirectories("Library/PackageCache", "com.unity.render-pipelines.universal*").Any())
        {
            Debug.Log("Lola Retro test: installing URP, then setting it up.");
            UnityEditor.PackageManager.Client.Add("com.unity.render-pipelines.universal");
        }
        else
        {
            EditorApplication.ExecuteMenuItem("Lola Retro Test/Set up URP (after installing it)");
        }
    }

    [MenuItem("Lola Retro Test/Use the Built-in render pipeline", priority = 21)]
    static void UseBuiltIn()
    {
        GraphicsSettings.defaultRenderPipeline = null;
        int level = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = null;
        }
        QualitySettings.SetQualityLevel(level, false);
        RefreshSprites();
        Debug.Log("Lola Retro test: Built-in render pipeline.");
    }

    // the sprites get the pipeline's sprite shader
    internal static void RefreshSprites()
    {
        foreach (var c in Object.FindObjectsByType<LolaRetroTestControls>(FindObjectsSortMode.None))
        {
            c.enabled = false;
            c.enabled = true;
        }
    }
}
