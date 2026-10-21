using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LolaRetro
{
    /// <summary>
    /// A check of the render pipeline's setup for a camera with Lola Retro (the URP and HDRP
    /// editor assemblies add theirs): what is missing, and a button that fixes it.
    /// </summary>
    public interface ILolaRetroSetupCheck
    {
        /// <summary>What is missing for this camera, or null when nothing is.</summary>
        string Problem(Camera camera);
        /// <summary>Warning: Lola Retro does not show without the fix; Info: it shows, a little less well.</summary>
        MessageType Severity { get; }
        string FixLabel { get; }
        void Fix(Camera camera);
    }

    public static class LolaRetroSetup
    {
        public static readonly List<ILolaRetroSetupCheck> Checks = new List<ILolaRetroSetupCheck>();
    }

    /// <summary>Lola Retro's Inspector: the settings, and above them what the pipeline still needs.</summary>
    [CustomEditor(typeof(LolaRetroEffect))]
    sealed class LolaRetroEffectEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var camera = ((LolaRetroEffect)target).GetComponent<Camera>();
            foreach (var check in LolaRetroSetup.Checks)
            {
                string problem = check.Problem(camera);
                if (problem == null)
                    continue;
                EditorGUILayout.HelpBox(problem, check.Severity);
                if (GUILayout.Button(check.FixLabel, GUILayout.Height(28)))
                    check.Fix(camera);
                EditorGUILayout.Space();
            }
            DrawDefaultInspector();
        }
    }
}
