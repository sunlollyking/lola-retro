using System;
using System.IO;
using System.Linq;
using LolaRetro;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The test project shows the package's Demo sample. On the first start it imports the sample
/// as a user would (Package Manager, Samples, Import) and opens its scene. Lola Retro Test, Make
/// the demo, builds the sample's scene and assets from its sources in the package (for the
/// package's developers), in two steps: the sources are copied into this project, and once Unity
/// has compiled the demo's script, the scene is built (from the command line: MakeDemoCopy, then
/// MakeDemoBuild, each in a run of its own).
/// </summary>
[InitializeOnLoad]
public static class LolaRetroTestSetup
{
    public const string Package = "com.zoran123456.lola-retro";
    const string SceneName = "Lola Retro Demo.unity";
    const string DevFolder = "Assets/LolaRetroDemo";
    const string BuildRequest = "LolaRetroTest.BuildDemo";

    // the demo's script, once the sample (or its sources) are in the project
    public static Type DemoType => Type.GetType("LolaRetro.Demo.LolaRetroDemo, Assembly-CSharp");

    static LolaRetroTestSetup()
    {
        if (Application.isBatchMode)
            return;                     // command-line runs call the steps themselves
        EditorApplication.delayCall += () =>
        {
            if (SessionState.GetBool(BuildRequest, false))
                MakeDemoBuild();
            else if (DemoScene() == null && !Directory.Exists(DevFolder))
                ImportDemo();
        };
    }

    static Sample Demo() => Sample.FindByPackage(Package, null).FirstOrDefault(s => s.displayName == "Demo");

    /// <summary>The imported demo's scene, or null.</summary>
    public static string DemoScene()
    {
        var sample = Demo();
        if (sample.importPath == null || !Directory.Exists(sample.importPath))
            return null;
        var scene = Directory.GetFiles(sample.importPath, SceneName, SearchOption.AllDirectories).FirstOrDefault();
        return scene == null ? null : FileUtil.GetProjectRelativePath(scene.Replace('\\', '/'));
    }

    [MenuItem("Lola Retro Test/Import the demo", priority = 0)]
    public static void ImportDemo()
    {
        var sample = Demo();
        if (!File.Exists(Path.Combine(sample.resolvedPath, SceneName)))
        {
            MakeDemo();
            return;                     // ImportDemo runs again once the demo is made
        }
        sample.Import(Sample.ImportOptions.OverridePreviousImports | Sample.ImportOptions.HideImportWindow);
        AssetDatabase.Refresh();
        string scene = DemoScene();
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scene, true) };
        EditorSceneManager.OpenScene(scene);
        Debug.Log("Lola Retro test: demo imported (" + scene + "). Press Play; the keys are on the screen.");
    }

    /// <summary>
    /// Builds the Demo sample's scene and assets from its sources in the package (scripts,
    /// shader, pictures): in a folder of this project, so that Unity makes them and their .meta
    /// files, which then go back into the package's Samples~/Demo.
    /// </summary>
    [MenuItem("Lola Retro Test/Make the demo (package developers)", priority = 40)]
    public static void MakeDemo()
    {
        SessionState.SetBool(BuildRequest, true);
        MakeDemoCopy();
    }

    // step 1: the sources into this project (Unity then compiles the demo's script)
    public static void MakeDemoCopy()
    {
        string src = Demo().resolvedPath;
        foreach (var s in Sample.FindByPackage(Package, null))
            if (s.isImported && s.importPath != null)
                FileUtil.DeleteFileOrDirectory(s.importPath);       // no second copy with the same GUIDs
        if (Directory.Exists(DevFolder))
            AssetDatabase.DeleteAsset(DevFolder);
        CopyFolder(src, DevFolder);
        AssetDatabase.Refresh();
    }

    // step 2: the scene and its assets, then all of it back into the package
    public static void MakeDemoBuild()
    {
        SessionState.SetBool(BuildRequest, false);
        string src = Demo().resolvedPath;

        // the pictures: sprites at one unit per pixel, unfiltered and uncompressed
        string ballPath = DevFolder + "/Ball.png";
        if (!File.Exists(ballPath))
        {
            File.WriteAllBytes(ballPath, BallTexture().EncodeToPNG());
            AssetDatabase.Refresh();
        }
        var paths = Directory.GetFiles(DevFolder + "/Images", "*.png").Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToList();
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
        var sprites = paths.OrderBy(p => p.Contains("platformer") ? 0 : 1).Select(AssetDatabase.LoadAssetAtPath<Sprite>).ToArray();

        string matPath = DevFolder + "/LolaRetroDemoSprite.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (material == null)
        {
            material = new Material(Shader.Find("Lola Retro Demo/Sprite"));
            AssetDatabase.CreateAsset(material, matPath);
        }
        string rtPath = DevFolder + "/Console.renderTexture";
        var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(rtPath);
        if (rt == null)
        {
            rt = new RenderTexture(256, 224, 16) { filterMode = FilterMode.Point, name = "Console" };
            AssetDatabase.CreateAsset(rt, rtPath);
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var picture = new GameObject("Picture").AddComponent<SpriteRenderer>();
        picture.sprite = sprites[0];
        picture.sharedMaterial = material;
        var ball = new GameObject("Ball").AddComponent<SpriteRenderer>();
        ball.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ballPath);
        ball.sharedMaterial = material;
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

        var demo = (MonoBehaviour)display.gameObject.AddComponent(DemoType);
        var so = new SerializedObject(demo);
        so.FindProperty("effect").objectReferenceValue = effect;
        so.FindProperty("gameCamera").objectReferenceValue = game;
        so.FindProperty("console").objectReferenceValue = rt;
        so.FindProperty("picture").objectReferenceValue = picture;
        so.FindProperty("ball").objectReferenceValue = ball;
        var list = so.FindProperty("pictures");
        list.arraySize = sprites.Length;
        for (int i = 0; i < sprites.Length; i++)
            list.GetArrayElementAtIndex(i).objectReferenceValue = sprites[i];
        so.ApplyModifiedPropertiesWithoutUndo();
        demo.enabled = false;      // OnEnable sets the scene up for the first picture
        demo.enabled = true;
        EditorSceneManager.SaveScene(scene, DevFolder + "/" + SceneName);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AssetDatabase.SaveAssets();

        // back into the package, with Unity's .meta files; then out of this project, so that the
        // imported sample is the only copy (the same GUIDs)
        CopyFolder(DevFolder, src);
        AssetDatabase.DeleteAsset(DevFolder);
        Debug.Log("Lola Retro test: demo made in " + src);
        if (!Application.isBatchMode)
            EditorApplication.delayCall += ImportDemo;
    }

    static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from))
            CopyFolder(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // a white ball, 12 pixels across, with a red rim
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
        SessionState.SetBool("LolaRetroTest.SetUpUrp", true);
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
    public static void UseBuiltIn()
    {
        GraphicsSettings.defaultRenderPipeline = null;
        int level = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = null;
        }
        QualitySettings.SetQualityLevel(level, false);
        Debug.Log("Lola Retro test: Built-in render pipeline.");
    }
}
