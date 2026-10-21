using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LolaRetro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Renders the demo scene's display camera into a PNG, a few frames in a row (for the afterglow),
/// without the ball and the help text. For checking the package and making pictures of it from
/// the command line:
///   Unity -batchmode -projectPath unity/test-project -executeMethod LolaRetroTestCapture.Capture
///         -lolaOut out.png [-lolaSize 1440x1080] [-lolaFrames 3] [-lolaPipeline urp|builtin]
///         [-lolaPreset OldTVRF] [-lolaFlat] [-lolaPicture waterfall] [-lolaTube Fill]
///         [-lolaSet field=value ...] -quit
/// -lolaPipeline first switches the render pipeline, as the menu Lola Retro Test does (urp needs URP
/// installed). -lolaSet sets a field of the settings (LolaRetroSettings), for example
/// aroundTheTube=Synthwave or cable=RGB; it can be given more than once.
/// </summary>
public static class LolaRetroTestCapture
{
    public static void Capture()
    {
        string[] args = Environment.GetCommandLineArgs();
        string Arg(string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        string outPath = Arg("-lolaOut", "LolaRetroTest.png");
        string[] size = Arg("-lolaSize", "1440x1080").Split('x');
        int w = int.Parse(size[0]), h = int.Parse(size[1]);
        int frames = int.Parse(Arg("-lolaFrames", "3"));

        string pipeline = Arg("-lolaPipeline", "");
        if (pipeline == "urp")
            Type.GetType("LolaRetroTestUrp, LolaRetroTest.URP.Editor", true).GetMethod("SetUp").Invoke(null, null);
        else if (pipeline == "builtin")
            LolaRetroTestSetup.UseBuiltIn();
        string scenePath = LolaRetroTestSetup.DemoScene();
        if (scenePath == null)
            throw new Exception("the demo is not imported (Lola Retro Test, Import the demo)");
        EditorSceneManager.OpenScene(scenePath);

        var effect = UnityEngine.Object.FindAnyObjectByType<LolaRetroEffect>();
        var demo = (MonoBehaviour)UnityEngine.Object.FindAnyObjectByType(LolaRetroTestSetup.DemoType);
        var display = effect.GetComponent<Camera>();
        var fields = LolaRetroTestSetup.DemoType;
        ((SpriteRenderer)fields.GetField("ball").GetValue(demo)).enabled = false;

        if (Arg("-lolaPreset", null) is string preset)
            effect.ApplyPreset((LolaRetroPreset)Enum.Parse(typeof(LolaRetroPreset), preset), args.Contains("-lolaFlat"));
        else if (args.Contains("-lolaFlat"))
            effect.ApplyPreset(effect.preset, true);
        if (Arg("-lolaTube", null) is string tube)
            effect.tubeShape = (TubeShape)Enum.Parse(typeof(TubeShape), tube);
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "-lolaSet")
                continue;
            string[] kv = args[i + 1].Split('=');
            FieldInfo f = typeof(LolaRetroSettings).GetField(kv[0]) ?? throw new Exception("no setting " + kv[0]);
            object v = f.FieldType.IsEnum ? Enum.Parse(f.FieldType, kv[1])
                     : f.FieldType == typeof(bool) ? bool.Parse(kv[1])
                     : f.FieldType == typeof(int) ? int.Parse(kv[1])
                     : (object)float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
            f.SetValue(effect.settings, v);
        }
        if (Arg("-lolaPicture", null) is string pictureName)
        {
            var pictures = (Sprite[])fields.GetField("pictures").GetValue(demo);
            int k = Array.FindIndex(pictures, s => s.texture.name == pictureName);
            if (k < 0)
                throw new Exception("no picture " + pictureName + " (" + string.Join(", ", pictures.Select(s => s.texture.name)) + ")");
            fields.GetMethod("ShowPicture", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(demo, new object[] { k });
        }

        var target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        display.targetTexture = target;
        var game = (Camera)fields.GetField("gameCamera").GetValue(demo);
        for (int f = 0; f < frames; f++)
        {
            if (game.enabled)
                game.Render();
            display.Render();
        }
        RenderTexture.active = target;
        var png = new Texture2D(w, h, TextureFormat.RGB24, false, false);
        png.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        png.Apply();
        RenderTexture.active = null;
        display.targetTexture = null;
        File.WriteAllBytes(outPath, png.EncodeToPNG());
        var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        Debug.Log("Lola Retro test: wrote " + Path.GetFullPath(outPath) + " (" + (rp != null ? rp.GetType().Name : "Built-in") +
                  ", " + SystemInfo.graphicsDeviceType + ", " + effect.preset + ")");
    }
}

/// <summary>
/// A web build of the demo scene, uncompressed so that any local web server can serve it:
///   Unity -batchmode -projectPath unity/test-project -buildTarget WebGL -executeMethod LolaRetroTestBuild.WebGL -quit
/// The build is in Builds/WebGL; -lolaDevelopment makes a development build.
/// </summary>
public static class LolaRetroTestBuild
{
    public static void WebGL()
    {
        string scene = LolaRetroTestSetup.DemoScene() ?? throw new Exception("the demo is not imported");
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        var report = BuildPipeline.BuildPlayer(new[] { scene }, "Builds/WebGL", BuildTarget.WebGL,
                                               Array.IndexOf(Environment.GetCommandLineArgs(), "-lolaDevelopment") >= 0
                                                   ? BuildOptions.Development : BuildOptions.None);
        Debug.Log("Lola Retro test: web build " + report.summary.result + " (" + report.summary.totalErrors + " errors)");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
