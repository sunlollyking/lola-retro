using LolaRetro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Sets the test project up for URP (compiles only once URP is installed): a Universal Renderer
/// with the Lola Retro renderer feature, and a URP asset that uses it, for every quality level.
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

            // the renderer feature, as the renderer's Add Renderer Feature button adds it
            var feature = ScriptableObject.CreateInstance<LolaRetroFeature>();
            feature.name = "Lola Retro";
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long id);
            var so = new SerializedObject(data);
            var features = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = id;
            so.ApplyModifiedProperties();

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

        // the test scene's sprites get URP's sprite shader (LolaRetroTestControls.OnEnable)
        foreach (var b in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (b.GetType().Name != "LolaRetroTestControls")
                continue;
            b.enabled = false;
            b.enabled = true;
        }
        var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        if (scene.path == "Assets/LolaRetroTest/LolaRetroTest.unity")
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log("Lola Retro test: URP set up (" + Folder + "), with the Lola Retro renderer feature.");
    }
}
