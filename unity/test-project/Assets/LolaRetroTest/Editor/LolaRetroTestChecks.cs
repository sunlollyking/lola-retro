using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Compiles every pass of Lola Retro's shader for this editor's graphics API and lists the
/// compiler's errors and warnings (the Asset Store wants none):
///   Unity -batchmode -projectPath unity/test-project -executeMethod LolaRetroTestChecks.ShaderMessages -quit
/// </summary>
public static class LolaRetroTestChecks
{
    public static void ShaderMessages()
    {
        var shader = Shader.Find("Hidden/Lola Retro");
        if (shader == null)
        {
            Debug.LogError("Lola Retro test: shader not found");
            EditorApplication.Exit(1);
            return;
        }
        var material = new Material(shader);
        for (int p = 0; p < material.passCount; p++)
            ShaderUtil.CompilePass(material, p, true);
        var messages = ShaderUtil.GetShaderMessages(shader);
        foreach (var m in messages)
            Debug.Log($"Lola Retro test: shader {m.severity} ({m.platform}) {m.message} at {m.file}:{m.line}");
        Debug.Log($"Lola Retro test: {material.passCount} passes, {messages.Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)} errors, " +
                  $"{messages.Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Warning)} warnings, supported {shader.isSupported}");
        Object.DestroyImmediate(material);
    }
}
