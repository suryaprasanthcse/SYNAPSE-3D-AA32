using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Aligns the project with the Phase 2 GDD (Theme #2 Adventure Quest World, Category #4 Engineering
/// Puzzle Game): retires Quacky, Tolly and Tiko and keeps Diorama_Petalo as the sole narrative focus.
/// </summary>
/// <remarks>
/// Nothing is hard-deleted. Each retired file (and its .meta) is copied to
/// <c>_RemovedAssets/</c> in the project root, outside Assets so Unity never imports it, before
/// being removed through the AssetDatabase. An asset is only removed when nothing outside the
/// retired set still depends on it.
/// </remarks>
public static class FunobotzGddCleanup
{
    const string LogPrefix = "[FunobotzGddCleanup]";
    const string ArchiveDir = "_RemovedAssets";

    static readonly string[] RetiredCharacters = { "Quacky", "Tolly", "Tiko" };

    // Asset types that can hold references to materials, meshes or textures.
    static readonly string[] ReferencingExtensions = { ".unity", ".prefab", ".mat", ".asset", ".controller", ".overrideController", ".playable" };

    [MenuItem("Funobotz/GDD Cleanup (Petalo Only)", priority = 20)]
    public static void CleanupMenu()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            Cleanup();
    }

    [MenuItem("Funobotz/GDD Cleanup (Petalo Only)", validate = true)]
    static bool ValidateCleanup() => !EditorApplication.isPlayingOrWillChangePlaymode;

    /// <summary>Returns false if Petalo's diorama is missing, since nothing would be left to review.</summary>
    public static bool Cleanup()
    {
        try
        {
            EditorUtility.DisplayProgressBar("Funobotz GDD Cleanup", "Isolating Diorama_Petalo", 0.2f);
            if (!CleanScene())
                return false;

            EditorUtility.DisplayProgressBar("Funobotz GDD Cleanup", "Retiring assets", 0.6f);
            RetireAssets();

            EditorUtility.DisplayProgressBar("Funobotz GDD Cleanup", "Retiring screenshots", 0.9f);
            RetireScreenshots();

            AssetDatabase.SaveAssets();
            Debug.Log($"{LogPrefix} Project now centers on Petalo in {FunobotzDioramaBuilder.EnvironmentName}. Retired files are in {ArchiveDir}/.");
            return true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static bool CleanScene()
    {
        if (!File.Exists(FunobotzDioramaBuilder.ScenePath))
        {
            Debug.LogError($"{LogPrefix} {FunobotzDioramaBuilder.ScenePath} not found.");
            return false;
        }

        var scene = EditorSceneManager.OpenScene(FunobotzDioramaBuilder.ScenePath, OpenSceneMode.Single);
        var petaloName = FunobotzDioramaBuilder.DioramaPrefix + FunobotzDioramaBuilder.FocalCharacter;
        var retiredNames = new HashSet<string>(RetiredCharacters.Select(c => FunobotzDioramaBuilder.DioramaPrefix + c));

        GameObject petalo = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name == petaloName)
            {
                petalo = root;
            }
            else if (retiredNames.Contains(root.name))
            {
                var removedName = root.name;
                Object.DestroyImmediate(root);
                Debug.Log($"{LogPrefix} Removed '{removedName}' from the scene.");
            }
        }

        if (petalo == null)
        {
            Debug.LogError($"{LogPrefix} '{petaloName}' is missing from {FunobotzDioramaBuilder.ScenePath}. Run Funobotz > Build Dioramas; nothing was removed.");
            return false;
        }

        // Carry the GDD naming into the existing diorama without rebuilding it.
        var environment = petalo.transform.Find("Environment");
        if (environment != null)
            environment.name = FunobotzDioramaBuilder.EnvironmentName;

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            return false;

        // Refresh the scene's dependency record so the asset scan below no longer sees the removed roots.
        AssetDatabase.ImportAsset(FunobotzDioramaBuilder.ScenePath, ImportAssetOptions.ForceSynchronousImport);
        return true;
    }

    static void RetireAssets()
    {
        var candidates = new HashSet<string>();
        foreach (var character in RetiredCharacters)
        {
            AddIfExists(candidates, $"Assets/Funobotz/Materials/{character}_Cutout.mat");
            AddIfExists(candidates, $"Assets/Funobotz/Meshes/{character}_Card.asset");

            var textureName = $"{character.ToLowerInvariant()}_transparent";
            foreach (var path in AssetDatabase.FindAssets($"{textureName} t:Texture2D")
                         .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
                         .Where(p => Path.GetFileNameWithoutExtension(p) == textureName))
                candidates.Add(path);
        }

        if (candidates.Count == 0)
        {
            Debug.Log($"{LogPrefix} No retired assets left to remove.");
            return;
        }

        // Anything outside the retired set that still points at a candidate keeps that candidate alive.
        var blocked = new Dictionary<string, string>();
        foreach (var path in AssetDatabase.GetAllAssetPaths())
        {
            if (!path.StartsWith("Assets/") || candidates.Contains(path) || !ReferencingExtensions.Contains(Path.GetExtension(path)))
                continue;

            foreach (var dependency in AssetDatabase.GetDependencies(path, false))
            {
                if (candidates.Contains(dependency) && !blocked.ContainsKey(dependency))
                    blocked[dependency] = path;
            }
        }

        foreach (var pair in blocked)
            Debug.LogWarning($"{LogPrefix} Kept {pair.Key}: still referenced by {pair.Value}.");

        foreach (var path in candidates.Where(p => !blocked.ContainsKey(p)).OrderBy(p => p))
        {
            Archive(path);
            Archive(path + ".meta");
            if (AssetDatabase.DeleteAsset(path))
                Debug.Log($"{LogPrefix} Retired {path}.");
            else
                Debug.LogError($"{LogPrefix} Failed to remove {path}; it is still archived.");
        }
    }

    static void RetireScreenshots()
    {
        foreach (var character in RetiredCharacters)
        {
            var file = $"Funobotz_{character}_BeautyShot.png";
            if (!File.Exists(file))
                continue;

            var destination = Path.Combine(ArchiveDir, file);
            Directory.CreateDirectory(ArchiveDir);
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(file, destination);
            Debug.Log($"{LogPrefix} Moved {file} to {ArchiveDir}/.");
        }
    }

    static void AddIfExists(HashSet<string> set, string path)
    {
        if (File.Exists(path))
            set.Add(path);
    }

    static void Archive(string projectRelativePath)
    {
        if (!File.Exists(projectRelativePath))
            return;

        var destination = Path.Combine(ArchiveDir, projectRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(projectRelativePath, destination, true);
    }
}
