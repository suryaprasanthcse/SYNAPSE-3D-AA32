using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class PurgeFiatLighting
{
    [MenuItem("Funobotz/Purge Fiat Lighting")]
    public static void Purge()
    {
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Purge Fiat Lighting");

        Scene scene = SceneManager.GetActiveScene();

        // 1. Destroy every directional light in the active scene.
        var directionalObjects = Object.FindObjectsByType<Light>(FindObjectsInactive.Include)
            .Where(l => l != null && l.type == LightType.Directional && l.gameObject.scene == scene)
            .Select(l => l.gameObject)
            .Distinct()
            .ToList();

        foreach (var go in directionalObjects)
            Undo.DestroyObjectImmediate(go);

        // RenderSettings is scene-owned but static; record its backing object so the change is undoable.
        var renderSettingsObject = typeof(RenderSettings)
            .GetMethod("GetRenderSettings", BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, null) as Object;
        if (renderSettingsObject != null)
            Undo.RecordObject(renderSettingsObject, "Purge Fiat Lighting");

        // 2-4. Kill skybox and ambient.
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        DynamicGI.UpdateEnvironment();

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(scene);

        // 5. Confirm.
        Debug.Log("Fiat lighting purged. The matrix is dark.");
    }
}
