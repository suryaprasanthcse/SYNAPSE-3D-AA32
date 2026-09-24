using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Removes the legacy placeholder hologram mapped to the anchor card from
/// <see cref="ARMultiMatrixPayload"/>, leaving the other four card payloads untouched.
/// </summary>
/// <remarks>
/// The edit goes through SerializedObject, so Unity writes the scene YAML itself: no text
/// surgery, and the change is undoable. The prefab asset is not deleted, only its mapping.
/// Re-running is a no-op once the entry is gone.
/// </remarks>
public static class MatrixArtifactCleaner
{
    const string MenuPath = "Funobotz/Purge Legacy Payload";
    const string LogPrefix = "[MatrixArtifactCleaner]";
    const string OriginName = "XR Origin (Mobile AR)";
    const string TargetImage = "king of spades";
    const string PayloadsField = "m_Payloads";
    const string ImageNameField = "imageName";
    const string PrefabField = "prefab";

    [MenuItem(MenuPath, validate = true)]
    static bool ValidatePurge() => !EditorApplication.isPlayingOrWillChangePlaymode;

    [MenuItem(MenuPath, priority = 30)]
    public static void PurgeLegacyPayload()
    {
        var scene = SceneManager.GetActiveScene();

        // 1. Locate the origin, including inactive objects, which GameObject.Find would skip.
        var origin = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject)
            .FirstOrDefault(go => go.name == OriginName);

        if (origin == null)
        {
            Fail($"No GameObject named '{OriginName}' in the open scene '{scene.name}'.");
            return;
        }

        // 2. The payload router.
        var router = origin.GetComponent<ARMultiMatrixPayload>();
        if (router == null)
        {
            Fail($"'{OriginName}' has no {nameof(ARMultiMatrixPayload)} component.");
            return;
        }

        var serialized = new SerializedObject(router);
        var payloads = serialized.FindProperty(PayloadsField);
        if (payloads == null || !payloads.isArray)
        {
            Fail($"{nameof(ARMultiMatrixPayload)} has no serialized '{PayloadsField}' list. Was the script renamed?");
            return;
        }

        // 3. Find the entry by name, ignoring case, spaces and underscores.
        var index = -1;
        var prefabName = "<none>";
        for (var i = 0; i < payloads.arraySize; i++)
        {
            var element = payloads.GetArrayElementAtIndex(i);
            var imageName = element.FindPropertyRelative(ImageNameField)?.stringValue ?? string.Empty;
            if (!Matches(imageName, TargetImage))
                continue;

            index = i;
            var prefab = element.FindPropertyRelative(PrefabField)?.objectReferenceValue;
            prefabName = prefab != null ? prefab.name : "<empty>";
            break;
        }

        if (index < 0)
        {
            Debug.Log($"{LogPrefix} Nothing to purge: no payload is mapped to '{TargetImage}'. " +
                      $"{payloads.arraySize} entr{(payloads.arraySize == 1 ? "y" : "ies")} left as-is.", router);
            return;
        }

        var before = payloads.arraySize;
        if (!EditorUtility.DisplayDialog(
                "Purge Legacy Payload",
                $"Remove the '{TargetImage}' → {prefabName} mapping from {nameof(ARMultiMatrixPayload)} on '{OriginName}'?\n\n" +
                $"The other {before - 1} card payload(s) stay. The prefab asset itself is not deleted.\n\n" +
                $"Scene: {(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path)}",
                "Purge", "Cancel"))
        {
            Debug.Log($"{LogPrefix} Cancelled; nothing was changed.");
            return;
        }

        Undo.RecordObject(router, "Purge Legacy Payload");
        payloads.DeleteArrayElementAtIndex(index);

        // On an object-reference array the first delete only nulls the slot, so remove it again.
        if (payloads.arraySize == before)
            payloads.DeleteArrayElementAtIndex(index);

        serialized.ApplyModifiedProperties();

        if (payloads.arraySize != before - 1)
        {
            Fail($"Expected {before - 1} payloads after the purge but found {payloads.arraySize}; nothing was saved.");
            return;
        }

        // 4. Persist.
        EditorUtility.SetDirty(router);
        EditorSceneManager.MarkSceneDirty(scene);

        if (string.IsNullOrEmpty(scene.path))
        {
            Debug.LogWarning($"{LogPrefix} Removed '{TargetImage}' → {prefabName}, but the scene has never been saved to disk. " +
                             "Use File > Save As to keep the change.", router);
            return;
        }

        if (!EditorSceneManager.SaveScene(scene))
        {
            Fail($"Removed the entry but could not save '{scene.path}'. The change is still in memory; save manually or undo.");
            return;
        }

        var remaining = string.Join(", ", Enumerable.Range(0, payloads.arraySize)
            .Select(i => payloads.GetArrayElementAtIndex(i).FindPropertyRelative(ImageNameField)?.stringValue)
            .Where(n => !string.IsNullOrEmpty(n)));

        Selection.activeGameObject = origin;
        Debug.Log($"{LogPrefix} Purged '{TargetImage}' → {prefabName}. Remaining payloads ({payloads.arraySize}): {remaining}. " +
                  $"Saved '{scene.path}'.", router);
    }

    static bool Matches(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), System.StringComparison.Ordinal);

    static string Normalize(string name) =>
        string.IsNullOrEmpty(name) ? string.Empty : name.Trim().Replace('_', ' ').ToLowerInvariant();

    static void Fail(string message)
    {
        Debug.LogError($"{LogPrefix} {message}");
        EditorUtility.DisplayDialog("Purge Legacy Payload", message, "OK");
    }
}
