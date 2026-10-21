using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LolaRetro
{
    /// <summary>
    /// URP draws Lola Retro through its renderer feature: without it on the camera's renderer the
    /// picture stays as it is. The Inspector says so and adds it with one button.
    /// </summary>
    [InitializeOnLoad]
    public static class LolaRetroUrpSetup
    {
        static LolaRetroUrpSetup() => LolaRetroSetup.Checks.Add(new Check());

        sealed class Check : ILolaRetroSetupCheck
        {
            public string Problem(Camera camera)
            {
                var data = RendererOf(camera);
                if (data == null || HasFeature(data))
                    return null;
                return $"URP: the renderer \"{data.name}\" has no Lola Retro renderer feature yet, so this camera shows its picture without the CRT.";
            }

            public MessageType Severity => MessageType.Warning;

            public string FixLabel => "Fix it: add Lola Retro to the renderer";

            public void Fix(Camera camera) => AddFeature(RendererOf(camera));
        }

        /// <summary>The renderer this camera uses in the active URP asset (null outside URP).</summary>
        public static ScriptableRendererData RendererOf(Camera camera)
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset))
                return null;
            var assetObject = new SerializedObject(asset);
            var list = assetObject.FindProperty("m_RendererDataList");
            int index = assetObject.FindProperty("m_DefaultRendererIndex").intValue;
            if (camera != null && camera.TryGetComponent(out UniversalAdditionalCameraData cameraData))
            {
                int own = new SerializedObject(cameraData).FindProperty("m_RendererIndex").intValue;
                if (own >= 0)
                    index = own;
            }
            if (list == null || index < 0 || index >= list.arraySize)
                return null;
            return list.GetArrayElementAtIndex(index).objectReferenceValue as ScriptableRendererData;
        }

        public static bool HasFeature(ScriptableRendererData data)
        {
            foreach (var f in data.rendererFeatures)
                if (f is LolaRetroFeature)
                    return true;
            return false;
        }

        /// <summary>Adds the Lola Retro renderer feature to a renderer, as its Add Renderer Feature button does.</summary>
        public static void AddFeature(ScriptableRendererData data)
        {
            if (data == null || HasFeature(data))
                return;
            var feature = ScriptableObject.CreateInstance<LolaRetroFeature>();
            feature.name = "Lola Retro";
            Undo.RegisterCreatedObjectUndo(feature, "Add Lola Retro");
            if (EditorUtility.IsPersistent(data))
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
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            Debug.Log($"Lola Retro: added the renderer feature to \"{data.name}\".", data);
        }
    }
}
