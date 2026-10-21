using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Renders the test scene's display camera into a PNG, a few frames in a row (for the afterglow),
/// without the ball and the help text. For checking the package from the command line:
///   Unity -batchmode -projectPath unity/test-project -executeMethod LolaRetroTestCapture.Capture
///         -lolaOut out.png [-lolaSize 1440x1080] [-lolaFrames 3] [-lolaPipeline urp|builtin] -quit
/// -lolaPipeline first switches the render pipeline, as the menu Lola Retro Test does (urp needs URP installed).
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
        if (!File.Exists("Assets/LolaRetroTest/LolaRetroTest.unity"))
            LolaRetroTestSetup.Build();
        EditorSceneManager.OpenScene("Assets/LolaRetroTest/LolaRetroTest.unity");
        var controls = UnityEngine.Object.FindAnyObjectByType<LolaRetroTestControls>();
        var display = controls.effect.GetComponent<Camera>();
        controls.ball.enabled = false;
        controls.enabled = false;
        controls.enabled = true;

        var target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        display.targetTexture = target;
        for (int f = 0; f < frames; f++)
        {
            controls.gameCamera.Render();
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
                  ", " + SystemInfo.graphicsDeviceType + ")");
    }
}

/// <summary>
/// A web build of the test scene, uncompressed so that any local web server can serve it:
///   Unity -batchmode -projectPath unity/test-project -buildTarget WebGL -executeMethod LolaRetroTestBuild.WebGL -quit
/// The build is in Builds/WebGL.
/// </summary>
public static class LolaRetroTestBuild
{
    public static void WebGL()
    {
        if (!File.Exists("Assets/LolaRetroTest/LolaRetroTest.unity"))
            LolaRetroTestSetup.Build();
        // the sprites with this pipeline's material, saved in the scene
        EditorSceneManager.OpenScene("Assets/LolaRetroTest/LolaRetroTest.unity");
        LolaRetroTestSetup.RefreshSprites();
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/LolaRetroTest/LolaRetroTest.unity" }, "Builds/WebGL",
                                               BuildTarget.WebGL,
                                               Array.IndexOf(Environment.GetCommandLineArgs(), "-lolaDevelopment") >= 0
                                                   ? BuildOptions.Development : BuildOptions.None);
        Debug.Log("Lola Retro test: web build " + report.summary.result + " (" + report.summary.totalErrors + " errors)");
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
