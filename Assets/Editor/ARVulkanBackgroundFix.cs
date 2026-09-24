using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Adds AR Foundation's <c>ARCommandBufferSupportRendererFeature</c> to the Android renderer.
///
/// Under the Vulkan graphics API, ARCore does not draw the camera background through the normal URP path: the
/// passthrough is rendered from command-buffer events that this feature signals. Without it the feed is simply
/// never drawn and the device shows a black screen with virtual content floating on top of nothing - which looks
/// exactly like "the camera is broken" but is a missing renderer feature.
///
/// This is only required because the project moved to Vulkan (needed for the swarm: Adreno exposes no
/// vertex-stage SSBOs under OpenGL ES, so SwarmGPUArchitect disables itself there).
/// </summary>
public static class ARVulkanBackgroundFix
{
    const string FeatureTypeName = "UnityEngine.XR.ARFoundation.ARCommandBufferSupportRendererFeature";

    [MenuItem("Funobotz/Fix AR Camera Background (Vulkan)")]
    public static void Fix()
    {
        var type = System.AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } })
            .FirstOrDefault(t => t.FullName == FeatureTypeName);

        if (type == null)
        {
            Debug.LogError($"[ARVulkanBackgroundFix] '{FeatureTypeName}' not found. Is AR Foundation installed?");
            return;
        }

        int changed = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(path);
            if (data == null) continue;

            // Only the renderer Android actually uses. The Editor runs on D3D11, where the feature is dead weight.
            if (!path.Contains("Mobile_Renderer")) continue;

            if (AddFeature(data, type, path)) changed++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[ARVulkanBackgroundFix] Done. Renderers updated: {changed}.");
    }

    static bool AddFeature(ScriptableRendererData data, System.Type type, string path)
    {
        var so = new SerializedObject(data);
        var features = so.FindProperty("m_RendererFeatures");
        var featureMap = so.FindProperty("m_RendererFeatureMap");

        for (int i = 0; i < features.arraySize; i++)
        {
            var existing = features.GetArrayElementAtIndex(i).objectReferenceValue;
            if (existing != null && existing.GetType() == type)
            {
                Debug.Log($"[ARVulkanBackgroundFix] '{data.name}' already has {type.Name}; left alone.");
                return false;
            }
        }

        var feature = (ScriptableObject)ScriptableObject.CreateInstance(type);
        feature.name = type.Name;
        Undo.RegisterCreatedObjectUndo(feature, "Add AR command buffer feature");
        AssetDatabase.AddObjectToAsset(feature, data);

        features.arraySize++;
        features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;

        // The map stores each feature's local file id; URP uses it to rebind features after a reimport, and a
        // feature missing from it silently stops being applied.
        featureMap.arraySize++;
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
            featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssetIfDirty(data);

        Debug.Log($"[ARVulkanBackgroundFix] Added {type.Name} to '{data.name}' ({path}).");
        return true;
    }
}
