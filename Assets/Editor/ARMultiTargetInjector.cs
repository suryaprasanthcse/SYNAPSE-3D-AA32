using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARSubsystems;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Registers every Omni-Matrix target in the reference library and configures the scene's
/// ARTrackedImageManager to track all of them simultaneously.
/// </summary>
public static class ARMultiTargetInjector
{
    const string k_LogPrefix = "[ARMultiTargetInjector]";
    const string k_LibraryPath = "Assets/AR_Targets.asset";
    const float k_PhysicalWidthMeters = 0.1f;
    const int k_MaxMovingImages = 5;

    static readonly string[] k_TargetNames =
    {
        "all_knowing_eye",
        "joker",
        "valentoro",
        "queen_of_hearts",
        "king of spades",
    };

    [MenuItem("Tools/Inject Omni-Targets")]
    public static void InjectOmniTargets()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError($"{k_LogPrefix} Exit Play mode before injecting targets.");
            return;
        }

        var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(k_LibraryPath);
        if (library == null)
        {
            Debug.LogError($"{k_LogPrefix} No XRReferenceImageLibrary at {k_LibraryPath}.");
            return;
        }

        // Resolve everything first so a missing texture leaves the library untouched.
        var textures = new Texture2D[k_TargetNames.Length];
        for (var i = 0; i < k_TargetNames.Length; i++)
        {
            textures[i] = FindTexture(k_TargetNames[i]);
            if (textures[i] == null)
            {
                Debug.LogError($"{k_LogPrefix} Texture '{k_TargetNames[i]}' not found. No changes made.");
                return;
            }
        }

        Undo.RecordObject(library, "Inject Omni-Targets");
        foreach (var texture in textures)
            UpsertReferenceImage(library, texture);

        EditorUtility.SetDirty(library);
        AssetDatabase.SaveAssets();

        ConfigureSceneManager(library);
    }

    // FindAssets matches each search word as a substring, so "joker" would also match "joker_back".
    // Narrow to an exact file-name match, extension ignored.
    static Texture2D FindTexture(string textureName)
    {
        var path = AssetDatabase.FindAssets($"{textureName} t:Texture2D")
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), textureName, StringComparison.OrdinalIgnoreCase));

        return path != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(path) : null;
    }

    // Idempotent: an entry already bound to this texture is updated in place rather than duplicated.
    static void UpsertReferenceImage(XRReferenceImageLibrary library, Texture2D texture)
    {
        var textureGuid = new Guid(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(texture)));

        var index = -1;
        for (var i = 0; i < library.count; i++)
        {
            if (library[i].textureGuid == textureGuid)
            {
                index = i;
                break;
            }
        }

        var added = index < 0;
        if (added)
        {
            library.Add();
            index = library.count - 1;
        }

        library.SetName(index, texture.name);
        library.SetTexture(index, texture, keepTexture: false);
        library.SetSpecifySize(index, true);
        library.SetSize(index, new Vector2(k_PhysicalWidthMeters, k_PhysicalWidthMeters * GetSourceAspect(texture)));

        Debug.Log($"{k_LogPrefix} {(added ? "Added" : "Updated")} '{texture.name}' at {k_PhysicalWidthMeters}m wide.");
    }

    // Height follows the source image so ARKit/ARCore validation and pose estimation stay consistent.
    // Source dimensions are used because the imported texture may be resized to a power of two.
    static float GetSourceAspect(Texture2D texture)
    {
        if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)
        {
            importer.GetSourceTextureWidthAndHeight(out var width, out var height);
            if (width > 0)
                return (float)height / width;
        }

        return texture.width > 0 ? (float)texture.height / texture.width : 1f;
    }

    static void ConfigureSceneManager(XRReferenceImageLibrary library)
    {
        var scene = SceneManager.GetActiveScene();
        var manager = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<ARTrackedImageManager>(true))
            .FirstOrDefault();

        if (manager == null)
        {
            Debug.LogError($"{k_LogPrefix} No ARTrackedImageManager in '{scene.name}'. Library saved; scene unchanged.");
            return;
        }

        Undo.RecordObject(manager, "Configure ARTrackedImageManager");
        manager.requestedMaxNumberOfMovingImages = k_MaxMovingImages;

        if (!ReferenceEquals(manager.referenceLibrary, library))
        {
            if (manager.referenceLibrary != null)
                Debug.LogWarning($"{k_LogPrefix} Replacing the manager's reference library with {k_LibraryPath}.", manager);
            manager.referenceLibrary = library;
        }

        EditorUtility.SetDirty(manager);
        EditorSceneManager.MarkSceneDirty(scene);

        if (!EditorSceneManager.SaveScene(scene))
        {
            Debug.LogError($"{k_LogPrefix} Failed to save '{scene.path}'.");
            return;
        }

        Debug.Log($"{k_LogPrefix} {library.count} targets in {k_LibraryPath}; '{manager.name}' tracks up to {k_MaxMovingImages} moving images. Saved '{scene.path}'.", manager);
    }
}
