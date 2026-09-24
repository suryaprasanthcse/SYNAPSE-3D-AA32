using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Binds the AR_Targets reference library to every ARTrackedImageManager in the open scenes and sets the moving
/// image budget to 5. Runs on every domain reload (report only when already correct) and on demand from the menu.
/// </summary>
[InitializeOnLoad]
public static class ForceLibraryBind
{
    const string LibraryName = "AR_Targets";
    const int MaxMovingImages = 5;

    static ForceLibraryBind() => EditorApplication.delayCall += () => Run(false);

    [MenuItem("Funobotz/Force Library Bind")]
    public static void ForceBind() => Run(true);

    static void Run(bool verbose)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        var library = FindLibrary();
        if (library == null)
        {
            if (verbose) Debug.LogError($"[ForceLibraryBind] No XRReferenceImageLibrary named '{LibraryName}' in the project.");
            return;
        }

        var managers = Object.FindObjectsByType<ARTrackedImageManager>(FindObjectsInactive.Include);
        if (managers.Length == 0)
        {
            if (verbose) Debug.LogError("[ForceLibraryBind] No ARTrackedImageManager in the open scenes.");
            return;
        }

        int changed = 0;
        foreach (var manager in managers)
        {
            var so = new SerializedObject(manager);
            SerializedProperty libraryProp = so.FindProperty("m_SerializedLibrary");
            SerializedProperty movingProp = so.FindProperty("m_MaxNumberOfMovingImages");

            bool needsLibrary = libraryProp != null && libraryProp.objectReferenceValue != library;
            bool needsMoving = movingProp != null && movingProp.intValue != MaxMovingImages;

            if (needsLibrary) libraryProp.objectReferenceValue = library;
            if (needsMoving) movingProp.intValue = MaxMovingImages;

            if (needsLibrary || needsMoving)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(manager);
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                changed++;
                Debug.Log($"[ForceLibraryBind] Bound '{library.name}' ({library.count} images) to '{manager.name}', " +
                          $"max moving images {MaxMovingImages}.", manager);
            }
            else if (verbose)
            {
                Debug.Log($"[ForceLibraryBind] '{manager.name}' already bound to '{library.name}' " +
                          $"({library.count} images: {string.Join(", ", Enumerable.Range(0, library.count).Select(i => library[i].name))}), " +
                          $"max moving images {movingProp.intValue}. No change needed.", manager);
            }
        }

        if (changed > 0) EditorSceneManager.SaveOpenScenes();
    }

    static XRReferenceImageLibrary FindLibrary()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:XRReferenceImageLibrary"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(path);
            if (library != null && library.name == LibraryName) return library;
        }
        return null;
    }
}
