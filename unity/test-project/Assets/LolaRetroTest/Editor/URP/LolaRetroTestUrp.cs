using LolaRetro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Sets the test project up for URP (compiles only once URP is installed): a URP asset with a
/// plain Universal Renderer, for every quality level, and then the package's own fix (the
/// Inspector's Fix it button) adds the Lola Retro renderer feature to the camera's renderer.
/// </summary>
[InitializeOnLoad]
public static class LolaRetroTestUrp
{
    const string Folder = "Assets/LolaRetroTest/URP";
    const string UrpRequest = "LolaRetroTest.SetUpUrp";

    static LolaRetroTestUrp()
    {
        // Use URP installed URP: finish the setup now that this compiles
        if (SessionState.GetBool(UrpRequest, false))
            EditorApplication.delayCall += SetUp;
    }

    [MenuItem("Lola Retro Test/Set up URP (after installing it)", priority = 22)]
    public static void SetUp()
    {
        SessionState.SetBool(UrpRequest, false);
        if (!AssetDatabase.IsValidFolder(Folder))
            AssetDatabase.CreateFolder("Assets/LolaRetroTest", "URP");
        var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(Folder + "/URP.asset");
        if (asset == null)
        {
            var data = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(data, Folder + "/Renderer.asset");
            asset = UniversalRenderPipelineAsset.Create(data);
            AssetDatabase.CreateAsset(asset, Folder + "/URP.asset");
            AssetDatabase.SaveAssets();
        }
        GraphicsSettings.defaultRenderPipeline = asset;
        int level = QualitySettings.GetQualityLevel();
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            QualitySettings.SetQualityLevel(i, false);
            QualitySettings.renderPipeline = asset;
        }
        QualitySettings.SetQualityLevel(level, false);

        // what a user gets from the Inspector's Fix it button
        var renderer = LolaRetroUrpSetup.RendererOf(Camera.main);
        LolaRetroUrpSetup.AddFeature(renderer);
        Debug.Log("Lola Retro test: URP set up (" + Folder + "), Lola Retro on " +
                  (renderer != null ? renderer.name : "no renderer") + ": " + (renderer != null && LolaRetroUrpSetup.HasFeature(renderer)));
    }
}
