using MAAYAI.Swarm;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SwarmCalibrator
{
    const string CoreName = "Anomaly_Core";
    const string MaterialPath = "Assets/Anomaly_Material.mat";

    static readonly Vector3 BoundsExtents = new(5f, 5f, 5f);
    const float SpawnRadiusMetres = 3.0f;
    const float ParticleScale = 0.005f;

    // Low-intensity cyan: bright enough to cross the 0.9 bloom threshold, dim enough for 65k additive quads.
    static readonly Color SlowEmission = new Color(0f, 1f, 1f, 1f) * 0.8f;
    static readonly Color FastEmission = new Color(0.6f, 1f, 1f, 1f) * 1.6f;
    const float EmissionIntensity = 1.0f;

    [MenuItem("Funobotz/Calibrate Swarm Optics")]
    public static void Calibrate()
    {
        var core = GameObject.Find(CoreName);
        if (core == null)
        {
            Debug.LogError($"[SwarmCalibrator] '{CoreName}' not found in the active scene.");
            return;
        }

        var architect = core.GetComponent<SwarmGPUArchitect>();
        if (architect == null)
        {
            Debug.LogError($"[SwarmCalibrator] '{CoreName}' has no SwarmGPUArchitect component.");
            return;
        }

        // spawnRadius is a fraction of boundsExtents; convert the metre target so spawns stay inside the
        // containment shell instead of beyond hardLimit (where they would be recycled every frame).
        float minExtent = Mathf.Min(BoundsExtents.x, Mathf.Min(BoundsExtents.y, BoundsExtents.z));
        float spawnFraction = Mathf.Clamp(SpawnRadiusMetres / minExtent, 0.05f, 1f);

        var so = new SerializedObject(architect);
        so.FindProperty("boundsExtents").vector3Value = BoundsExtents;
        so.FindProperty("spawnRadius").floatValue = spawnFraction;
        so.FindProperty("particleScale").floatValue = ParticleScale;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(architect);

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            Debug.LogError($"[SwarmCalibrator] Material not found at '{MaterialPath}'.");
        }
        else
        {
            Undo.RecordObject(material, "Calibrate Swarm Optics");
            material.SetColor("_ColorSlow", SlowEmission);
            material.SetColor("_ColorFast", FastEmission);
            material.SetFloat("_EmissionIntensity", EmissionIntensity);
            material.SetFloat("_ParticleScale", ParticleScale);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
        }

        EditorSceneManager.MarkSceneDirty(core.scene);
        Debug.Log($"[SwarmCalibrator] Calibrated: extents {BoundsExtents}, spawn radius {SpawnRadiusMetres} m " +
                  $"(fraction {spawnFraction:0.##}), particle scale {ParticleScale}, low-intensity cyan emission.");
    }
}
