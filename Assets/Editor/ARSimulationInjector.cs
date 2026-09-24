using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Simulation;
using Object = UnityEngine.Object;

/// <summary>
/// Rebuilds the XR Simulation tracking target with zero Inspector interaction.
/// </summary>
/// <remarks>
/// XR Simulation only tracks a SimulatedTrackedImage whose scene is the instantiated
/// environment scene (SimulationUtils.IsInSimulationEnvironment). A card placed in a regular
/// scene renders but never tracks, so the card is written into an editable copy of the active
/// simulation environment prefab, and any copies in open scenes are purged.
/// </remarks>
public static class ARSimulationInjector
{
    const string k_LogPrefix = "[ARSimulationInjector]";
    const string k_CardName = "Simulated_Card";
    const string k_UnlitShader = "Unlit/Texture";
    const string k_EnvironmentPath = "Assets/XR/SimulationEnvironments/MatrixSimulationEnvironment.prefab";
    const string k_MaterialPath = "Assets/Materials/Simulated_Card_Unlit.mat";
    const string k_PreferencesFallbackPath = "Assets/XR/UserSimulationSettings/Resources/XRSimulationPreferences.asset";

    // The card floats this far down the simulation camera's starting view ray, facing the lens.
    // A 0.1 m image tracks perfectly inside 1.0 m (TrackedImageDiscoveryStrategy.ComputeDistanceQuality).
    const float k_CardDistance = 0.5f;
    static readonly Vector2 k_PhysicalSize = new(0.1f, 0.1f);

    // Used only if the environment has no SimulationEnvironment component: flat, face up, above y = 0.
    static readonly Pose k_FallbackPose = new(new Vector3(0f, 0.51f, 0f), Quaternion.identity);

    [MenuItem("Tools/Force Rebuild AR Target")]
    public static void ForceRebuildARTarget()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError($"{k_LogPrefix} Exit Play mode before rebuilding the AR target.");
            return;
        }

        // Locate the texture by name and type only; file extension is irrelevant.
        string[] guids = AssetDatabase.FindAssets("king of spades t:Texture2D");
        if (guids.Length == 0)
        {
            Debug.LogError("Matrix Error: Texture not found.");
            return;
        }
        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        Texture2D targetTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (targetTexture == null)
        {
            Debug.LogError($"Matrix Error: '{path}' matched the search but did not load as a Texture2D.");
            return;
        }

        // XR Simulation resolves a SimulatedTrackedImage by its texture's asset GUID against
        // XRReferenceImage.textureGuid (SimulationRuntimeImageLibrary.TryGetReferenceImageWithGuid).
        // Any miss falls back to a synthetic per-instance GUID that ARTrackedImageManager rejects.
        var libraryTextureGuids = CollectLibraryTextureGuids();
        var cardGuid = GetTextureGuid(targetTexture);
        if (!libraryTextureGuids.Contains(cardGuid))
        {
            Debug.LogError($"Matrix Error: '{path}' (texture GUID {cardGuid:N}) is not in any XRReferenceImageLibrary. Add it to AR_Targets before rebuilding.");
            return;
        }

        // Purge duplicates from every open scene, only once the rebuild is known to be viable.
        var purged = PurgeCardsFromOpenScenes();

        var environmentPath = EnsureEditableEnvironment();
        if (environmentPath == null)
            return;

        var material = ForgeMaterial(targetTexture);
        if (material == null)
            return;

        // Editing the prefab asset while its stage is open would be clobbered when the stage saves.
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && stage.assetPath == environmentPath)
            StageUtility.GoToMainStage();

        var root = PrefabUtility.LoadPrefabContents(environmentPath);
        try
        {
            foreach (var stale in FindCards(root.transform))
            {
                if (stale != null)
                    Object.DestroyImmediate(stale);
            }

            StripUnlistedImages(root, libraryTextureGuids);
            BuildCard(root.transform, ResolveCardPose(root), targetTexture, material);

            PrefabUtility.SaveAsPrefabAsset(root, environmentPath, out var saved);
            if (!saved)
            {
                Debug.LogError($"{k_LogPrefix} Failed to save {environmentPath}.");
                return;
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();

        Debug.Log($"{k_LogPrefix} Rebuilt '{k_CardName}' in {environmentPath} using {path} " +
                  $"({k_PhysicalSize.x}m x {k_PhysicalSize.y}m). Purged {purged} copy/copies from open scenes.",
                  AssetDatabase.LoadAssetAtPath<GameObject>(environmentPath));
    }

    // Scene purge
    static int PurgeCardsFromOpenScenes()
    {
        var count = 0;
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded)
                continue;

            var wasDirty = scene.isDirty;
            var removedHere = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var card in FindCards(root.transform))
                {
                    if (card == null)
                        continue; // already destroyed as a child of an earlier match

                    Undo.DestroyObjectImmediate(card);
                    removedHere++;
                }
            }

            if (removedHere == 0)
                continue;

            count += removedHere;
            EditorSceneManager.MarkSceneDirty(scene);

            // Persist the purge only when it can't sweep unrelated unsaved edits into the save.
            if (!wasDirty && !string.IsNullOrEmpty(scene.path))
                EditorSceneManager.SaveScene(scene);
            else
                Debug.LogWarning($"{k_LogPrefix} Removed {removedHere} '{k_CardName}' from '{scene.name}', which has other unsaved changes. Save the scene to persist the removal.");
        }

        return count;
    }

    static List<GameObject> FindCards(Transform root) =>
        root.GetComponentsInChildren<Transform>(true)
            .Where(t => t.name == k_CardName)
            .Select(t => t.gameObject)
            .ToList();

    // Every Unity texture asset GUID that some reference library in the project will accept.
    static HashSet<Guid> CollectLibraryTextureGuids()
    {
        var guids = new HashSet<Guid>();
        foreach (var libraryGuid in AssetDatabase.FindAssets("t:XRReferenceImageLibrary"))
        {
            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(AssetDatabase.GUIDToAssetPath(libraryGuid));
            if (library == null)
                continue;

            foreach (var referenceImage in library)
                guids.Add(referenceImage.textureGuid);
        }

        return guids;
    }

    // Same derivation as SimulationUtils.GetTextureGuid, which feeds SimulatedTrackedImage.imageAssetGuid.
    static Guid GetTextureGuid(Texture texture) =>
        texture != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out string guid, out long _)
            ? new Guid(guid)
            : Guid.Empty;

    // The cloned default environment ships Unity's own sample SimulatedTrackedImage. Its texture is not
    // in AR_Targets, so once it enters view it is reported under a fallback GUID on every tracking update.
    static void StripUnlistedImages(GameObject root, HashSet<Guid> libraryTextureGuids)
    {
        foreach (var image in root.GetComponentsInChildren<SimulatedTrackedImage>(true))
        {
            var texture = new SerializedObject(image).FindProperty("m_Image").objectReferenceValue as Texture;
            var guid = GetTextureGuid(texture);
            if (libraryTextureGuids.Contains(guid))
                continue;

            Debug.LogWarning($"{k_LogPrefix} Removed '{image.name}' from the environment: texture '{(texture != null ? texture.name : "<none>")}' ({guid:N}) is not in any reference library.");
            Object.DestroyImmediate(image.gameObject);
        }
    }

    // SimulationEnvironment is internal to AR Foundation, so its starting pose is read by serialized name.
    static Pose ResolveCardPose(GameObject root)
    {
        var environment = root.GetComponents<MonoBehaviour>().FirstOrDefault(c => c != null && c.GetType().Name == "SimulationEnvironment");
        var poseProp = environment != null ? new SerializedObject(environment).FindProperty("m_CameraStartingPose") : null;
        if (poseProp == null)
        {
            Debug.LogWarning($"{k_LogPrefix} No SimulationEnvironment camera pose found; placing the card at {k_FallbackPose.position}.");
            return k_FallbackPose;
        }

        var camPosition = poseProp.FindPropertyRelative("position").vector3Value;
        var camRotation = poseProp.FindPropertyRelative("rotation").quaternionValue;
        var camForward = camRotation * Vector3.forward;
        var camUp = camRotation * Vector3.up;

        // The tracker treats transform.up as the image normal and needs dot(camForward, up) <= 0.1.
        // up = -camForward is the ideal -1, and forward = camUp keeps the card upright and unmirrored.
        return new Pose(camPosition + camForward * k_CardDistance, Quaternion.LookRotation(camUp, -camForward));
    }

    // The package's fallback environment is read-only, so clone it into Assets and make it active.
    static string EnsureEditableEnvironment()
    {
        var prefsPath = AssetDatabase.FindAssets("t:XRSimulationPreferences")
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .FirstOrDefault() ?? k_PreferencesFallbackPath;

        var prefs = AssetDatabase.LoadAssetAtPath<ScriptableObject>(prefsPath);
        if (prefs == null)
        {
            Debug.LogError($"{k_LogPrefix} XRSimulationPreferences not found. Enter Play mode once with XR Simulation enabled to generate it, then run again.");
            return null;
        }

        var so = new SerializedObject(prefs);
        var environmentProp = so.FindProperty("m_EnvironmentPrefab");
        var fallbackProp = so.FindProperty("m_FallbackEnvironmentPrefab");
        if (environmentProp == null || fallbackProp == null)
        {
            Debug.LogError($"{k_LogPrefix} XRSimulationPreferences layout changed; m_EnvironmentPrefab/m_FallbackEnvironmentPrefab not found.");
            return null;
        }

        var current = environmentProp.objectReferenceValue;
        var currentPath = current != null ? AssetDatabase.GetAssetPath(current) : null;
        if (!string.IsNullOrEmpty(currentPath) && currentPath.StartsWith("Assets/", StringComparison.Ordinal))
            return currentPath;

        if (AssetDatabase.LoadAssetAtPath<GameObject>(k_EnvironmentPath) == null)
        {
            var source = current != null ? current : fallbackProp.objectReferenceValue;
            var sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            if (string.IsNullOrEmpty(sourcePath))
            {
                Debug.LogError($"{k_LogPrefix} No simulation environment prefab to copy from.");
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(k_EnvironmentPath));
            AssetDatabase.Refresh();
            if (!AssetDatabase.CopyAsset(sourcePath, k_EnvironmentPath))
            {
                Debug.LogError($"{k_LogPrefix} Failed to copy {sourcePath} to {k_EnvironmentPath}.");
                return null;
            }

            Debug.Log($"{k_LogPrefix} Cloned {sourcePath} to {k_EnvironmentPath}.");
        }

        environmentProp.objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(k_EnvironmentPath);
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(prefs);
        Debug.Log($"{k_LogPrefix} Active XR Simulation environment set to {k_EnvironmentPath}.");
        return k_EnvironmentPath;
    }

    // 5. Material, persisted because a prefab cannot reference an in-memory material.
    static Material ForgeMaterial(Texture2D texture)
    {
        var shader = Shader.Find(k_UnlitShader);
        if (shader == null)
        {
            Debug.LogError($"{k_LogPrefix} Shader '{k_UnlitShader}' not found.");
            return null;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(k_MaterialPath);
        if (material == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(k_MaterialPath));
            material = new Material(shader) { name = Path.GetFileNameWithoutExtension(k_MaterialPath) };
            AssetDatabase.CreateAsset(material, k_MaterialPath);
        }
        else
        {
            material.shader = shader;
        }

        material.mainTexture = texture;
        EditorUtility.SetDirty(material);
        return material;
    }

    // 3-8. Card construction
    static void BuildCard(Transform parent, Pose pose, Texture2D texture, Material material)
    {
        var card = GameObject.CreatePrimitive(PrimitiveType.Quad);
        card.name = k_CardName;

        // SimulatedTrackedImage swaps in its own XZ-plane mesh, so the XY quad collider would disagree with it.
        Object.DestroyImmediate(card.GetComponent<Collider>());

        card.transform.SetParent(parent, false); // also moves the card into the prefab's preview scene
        card.transform.SetLocalPositionAndRotation(pose.position, pose.rotation);

        var tracked = card.AddComponent<SimulatedTrackedImage>();

        // SimulatedTrackedImage exposes no settable 'image' or 'physicalSize' members: the public
        // 'texture' and 'size' are get-only over private serialized fields, so write the fields.
        // ApplyModifiedProperties fires OnValidate, which rebuilds the quad at the new size.
        var so = new SerializedObject(tracked);
        so.FindProperty("m_Image").objectReferenceValue = texture;
        so.FindProperty("m_ImagePhysicalSizeMeters").vector2Value = k_PhysicalSize;
        so.ApplyModifiedPropertiesWithoutUndo();

        // Assigned after OnValidate so the saved prefab references a persistent material.
        card.GetComponent<MeshRenderer>().sharedMaterial = material;
    }
}
