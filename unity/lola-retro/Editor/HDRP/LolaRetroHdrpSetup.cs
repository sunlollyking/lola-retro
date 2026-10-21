using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LolaRetro
{
    /// <summary>
    /// HDRP runs a custom post-process only when it is in the Custom Post Process Orders (Project
    /// Settings, Graphics, HDRP Global Settings). The Inspector says so and adds Lola Retro there
    /// (After Post Process) with one button. No Volume is needed: its Enable is on by default.
    /// </summary>
    [InitializeOnLoad]
    public static class LolaRetroHdrpSetup
    {
        static LolaRetroHdrpSetup()
        {
            LolaRetroSetup.Checks.Add(new Check());
            LolaRetroSetup.Checks.Add(new BufferCheck());
        }

        sealed class Check : ILolaRetroSetupCheck
        {
            public string Problem(Camera camera)
            {
                if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset) || IsRegistered())
                    return null;
                return "HDRP: Lola Retro is not in the Custom Post Process Orders yet, so this camera shows its picture without the CRT.";
            }

            public MessageType Severity => MessageType.Warning;

            public string FixLabel => "Fix it: add Lola Retro to the Custom Post Process Orders";

            public void Fix(Camera camera) => Register();
        }

        // HDRP's post-processing buffer is 32-bit by default (R11G11B10: 6 or 7 bits a channel), and
        // Lola Retro's picture goes through it: about one step of 8-bit colour is lost
        sealed class BufferCheck : ILolaRetroSetupCheck
        {
            public string Problem(Camera camera)
            {
                var format = BufferFormat(out _);
                if (format == null || format.intValue == (int)PostProcessBufferFormat.R16G16B16A16 ||
                    format.intValue == (int)PostProcessBufferFormat.R32G32B32A32)
                    return null;
                return "HDRP: the post-processing buffer keeps only 6 or 7 bits a channel (R11G11B10), so Lola Retro's " +
                       "picture loses about one step of 8-bit colour on the way to the screen.";
            }

            public MessageType Severity => MessageType.Info;

            public string FixLabel => "Fix it: post-processing buffer R16G16B16A16";

            public void Fix(Camera camera)
            {
                var format = BufferFormat(out var asset);
                if (format == null)
                    return;
                format.intValue = (int)PostProcessBufferFormat.R16G16B16A16;
                asset.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                Debug.Log("Lola Retro: HDRP's post-processing buffer is R16G16B16A16 now.", asset.targetObject);
            }
        }

        static SerializedProperty BufferFormat(out SerializedObject asset)
        {
            asset = null;
            if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset hdrp))
                return null;
            asset = new SerializedObject(hdrp);
            return asset.FindProperty("m_RenderPipelineSettings.postProcessSettings.bufferFormat");
        }

        static readonly string TypeName = typeof(LolaRetroVolume).AssemblyQualifiedName;

        // the After Post Process list of HDRP's Global Settings, as it is saved: in its settings list
        // (m_Settings; the asset also keeps an old list of that name for migration, which HDRP
        // no longer reads)
        static SerializedProperty AfterPostProcessList(out SerializedObject settings)
        {
            settings = null;
            var asset = GraphicsSettings.GetSettingsForRenderPipeline<HDRenderPipeline>();
            if (asset == null)
                return null;
            settings = new SerializedObject(asset);
            var it = settings.GetIterator();
            while (it.Next(true))
            {
                if (it.name == "m_AfterPostProcessCustomPostProcesses" && it.propertyPath.StartsWith("m_Settings."))
                    return it.FindPropertyRelative("m_CustomPostProcessTypesAsString");
            }
            return null;
        }

        public static bool IsRegistered()
        {
            var list = AfterPostProcessList(out _);
            if (list == null)
                return true;                    // nothing to fix that can be fixed here
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).stringValue == TypeName)
                    return true;
            return false;
        }

        /// <summary>Adds Lola Retro to HDRP's Custom Post Process Orders, After Post Process.</summary>
        public static void Register()
        {
            var list = AfterPostProcessList(out var settings);
            if (list == null || IsRegistered())
                return;
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).stringValue = TypeName;
            settings.ApplyModifiedProperties();
            EditorUtility.SetDirty(settings.targetObject);
            AssetDatabase.SaveAssets();
            Debug.Log("Lola Retro: added to HDRP's Custom Post Process Orders (After Post Process).", settings.targetObject);
        }
    }
}
