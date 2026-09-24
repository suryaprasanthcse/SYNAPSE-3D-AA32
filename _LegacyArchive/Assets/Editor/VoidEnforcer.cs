using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class VoidEnforcer
{
    const string LitShaderName = "Universal Render Pipeline/Lit";
    const string SimulationPreferencesPath = "Assets/XR/UserSimulationSettings/Resources/XRSimulationPreferences.asset";

    [MenuItem("Funobotz/Enforce Absolute Void")]
    public static void Enforce()
    {
        var litShader = Shader.Find(LitShaderName);
        if (litShader == null)
        {
            Debug.LogError($"[VoidEnforcer] Shader '{LitShaderName}' not found.");
            return;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Enforce Absolute Void");

        Scene scene = SceneManager.GetActiveScene();

        // 1. Lighting matrix hard override.
        var renderSettingsObject = typeof(RenderSettings)
            .GetMethod("GetRenderSettings", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(null, null) as Object;
        if (renderSettingsObject != null)
            Undo.RecordObject(renderSettingsObject, "Enforce Absolute Void");

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.ambientIntensity = 0f;
        RenderSettings.reflectionIntensity = 0f;
        RenderSettings.skybox = null;
        DynamicGI.UpdateEnvironment();

        // 2. Every MeshRenderer in the active scene.
        var materials = new HashSet<Material>();
        foreach (var renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include))
        {
            if (renderer.gameObject.scene != scene) continue;
            materials.UnionWith(renderer.sharedMaterials.Where(m => m != null));
        }

        // The XR Simulation environment (simulated planes / image cards) renders from its own prefab,
        // not from the active scene, so its renderers are collected explicitly.
        GameObject envPrefab = LoadSimulationEnvironmentPrefab();
        if (envPrefab != null)
        {
            foreach (var renderer in envPrefab.GetComponentsInChildren<MeshRenderer>(true))
                materials.UnionWith(renderer.sharedMaterials.Where(m => m != null));
        }

        // 3. Convert every non-Lit material to URP Lit.
        int converted = 0;
        var skipped = new List<string>();
        foreach (var material in materials)
        {
            if (material.shader == litShader) continue;

            string path = AssetDatabase.GetAssetPath(material);
            if (!string.IsNullOrEmpty(path) && !path.StartsWith("Assets/"))
            {
                skipped.Add($"{material.name} ({path})");
                continue;
            }

            Undo.RecordObject(material, "Enforce Absolute Void");
            string previous = material.shader != null ? material.shader.name : "<none>";
            material.shader = litShader;
            BaseShaderGUI.SetMaterialKeywords(material, LitGUI.SetMaterialKeywords);
            EditorUtility.SetDirty(material);
            converted++;
            Debug.Log($"[VoidEnforcer] {material.name}: {previous} -> {LitShaderName}", material);
        }

        if (skipped.Count > 0)
            Debug.LogWarning("[VoidEnforcer] Immutable package materials skipped:\n" + string.Join("\n", skipped));

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();

        // 4. Confirm.
        Debug.Log($"Absolute void enforced. All materials converted to physical Lit models. ({converted} converted)");
    }

    static GameObject LoadSimulationEnvironmentPrefab()
    {
        var prefs = AssetDatabase.LoadAssetAtPath<ScriptableObject>(SimulationPreferencesPath);
        if (prefs == null) return null;

        var so = new SerializedObject(prefs);
        Object reference = so.FindProperty("m_EnvironmentPrefab")?.objectReferenceValue;
        return reference as GameObject ?? (reference as Component)?.gameObject;
    }
}
