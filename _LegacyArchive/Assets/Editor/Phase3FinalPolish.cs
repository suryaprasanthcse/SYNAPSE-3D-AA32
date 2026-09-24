using System.IO;
using System.Linq;
using MAAYAI.Matrix.Swarm;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Funobotz > Apply Phase 3 Final Polish:
/// builds the Energy Shard boid mesh and swaps it in for the pyramid, and adds
/// <see cref="ProceduralHop"/> and <see cref="EyeEmitterBridge"/> to the Petalo hologram prefab.
/// </summary>
/// <remarks>
/// Every edit goes through the AssetDatabase, PrefabUtility or SerializedObject, so Unity writes
/// the YAML. Re-running is safe: the mesh is rebuilt in place (GUID kept) and components are only
/// added when missing. The old pyramid asset is left on disk because SwarmTestBuilder recreates it.
/// </remarks>
public static class Phase3FinalPolish
{
    const string MenuPath = "Funobotz/Apply Phase 3 Final Polish";
    const string LogPrefix = "[Phase3FinalPolish]";

    const string ShardPath = "Assets/BoidSwarmEngine/Swarm/Meshes/SM_EnergyShard.asset";
    const string SwarmPrefabPath = "Assets/BoidSwarmEngine/Resources/BoidComputeManager.prefab";
    const string PetaloPrefabPath = "Assets/MatrixPrefabs/Petalo_Hologram.prefab";
    const string OriginName = "XR Origin (Mobile AR)";

    [MenuItem(MenuPath, validate = true)]
    static bool Validate() => !EditorApplication.isPlayingOrWillChangePlaymode;

    [MenuItem(MenuPath, priority = 32)]
    public static void Apply()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var report = new System.Text.StringBuilder();
        try
        {
            EditorUtility.DisplayProgressBar("Phase 3 Final Polish", "Building Energy Shard mesh", 0.2f);
            var shard = EnergyShardMesh.CreateOrUpdate(ShardPath);
            report.AppendLine($"Energy Shard mesh: {ShardPath} ({shard.vertexCount} vertices, {shard.triangles.Length / 3} triangles).");

            EditorUtility.DisplayProgressBar("Phase 3 Final Polish", "Applying shard to the swarm", 0.45f);
            report.AppendLine(ApplyShardToSwarmPrefab(shard));
            report.AppendLine(ApplyShardToSceneBridge(shard));

            EditorUtility.DisplayProgressBar("Phase 3 Final Polish", "Upgrading Petalo hologram", 0.75f);
            report.AppendLine(UpgradePetaloPrefab());

            AssetDatabase.SaveAssets();
            Debug.Log($"{LogPrefix} Done.\n{report}");
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            EditorUtility.DisplayDialog("Phase 3 Final Polish", $"Stopped: {e.Message}\n\nSee the Console for details.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // -------------------------------------------------------------------- swarm mesh

    static string ApplyShardToSwarmPrefab(Mesh shard)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(SwarmPrefabPath) == null)
            return $"Swarm prefab: {SwarmPrefabPath} not found (run Matrix > Auto-Wire AR Boids); skipped.";

        var root = PrefabUtility.LoadPrefabContents(SwarmPrefabPath);
        try
        {
            var swarm = root.GetComponent<SwarmManager>();
            if (swarm == null)
                return $"Swarm prefab: no SwarmManager on {SwarmPrefabPath}; skipped.";

            swarm.boidMesh = shard;
            PrefabUtility.SaveAsPrefabAsset(root, SwarmPrefabPath);
            return $"Swarm prefab: {SwarmPrefabPath} now uses {shard.name}.";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ARBoidBridge keeps its own mesh reference as the fallback when no prefab is found.
    static string ApplyShardToSceneBridge(Mesh shard)
    {
        var scene = SceneManager.GetActiveScene();
        var bridge = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<ARBoidBridge>(true))
            .FirstOrDefault();
        if (bridge == null)
            return $"Scene: no ARBoidBridge in '{scene.name}'; fallback mesh not changed.";

        var so = new SerializedObject(bridge);
        var meshProp = so.FindProperty("m_BoidMesh");
        if (meshProp == null)
            return "Scene: ARBoidBridge has no m_BoidMesh field; skipped.";

        meshProp.objectReferenceValue = shard;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(bridge);
        EditorSceneManager.MarkSceneDirty(scene);

        var saved = !string.IsNullOrEmpty(scene.path) && EditorSceneManager.SaveScene(scene);
        return $"Scene: ARBoidBridge fallback mesh set to {shard.name}; {(saved ? $"saved '{scene.path}'" : "scene not saved")}.";
    }

    // -------------------------------------------------------------------- Petalo

    static string UpgradePetaloPrefab()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PetaloPrefabPath) == null)
            return $"Petalo: {PetaloPrefabPath} not found (run Funobotz > Inject Petalo first); skipped.";

        var root = PrefabUtility.LoadPrefabContents(PetaloPrefabPath);
        try
        {
            var added = new System.Collections.Generic.List<string>();

            if (root.GetComponent<ProceduralHop>() == null)
            {
                root.AddComponent<ProceduralHop>();
                added.Add(nameof(ProceduralHop));
            }

            var emitter = root.GetComponent<EyeEmitterBridge>();
            if (emitter == null)
            {
                emitter = root.AddComponent<EyeEmitterBridge>();
                added.Add(nameof(EyeEmitterBridge));
            }

            // Bind the eye offsets to the quad they were measured on.
            var quad = root.transform.Find("Quad");
            var so = new SerializedObject(emitter);
            so.FindProperty("m_Quad").objectReferenceValue = quad != null ? quad : root.transform;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, PetaloPrefabPath);
            return added.Count > 0
                ? $"Petalo: added {string.Join(" and ", added)} to {PetaloPrefabPath}{(quad == null ? " (warning: no 'Quad' child, eyes measured on the root)" : "")}."
                : $"Petalo: {PetaloPrefabPath} already had both components; quad reference refreshed.";
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}

/// <summary>
/// Low-poly "Energy Shard": an elongated diamond (stretched octahedron) pointing down +Z,
/// which is the forward axis SwarmManager and the swarm shader expect.
/// </summary>
/// <remarks>
/// 6 shared vertices, 8 triangles, 16-bit indices, position and normal only: the swarm shader
/// reads nothing else and rebuilds flat face normals per pixel, so shared vertices cost nothing
/// visually. That is one more vertex and two more triangles than the old pyramid, and drawn once
/// per boid through RenderMeshIndirect, so it adds no draw calls or compute-buffer memory.
/// </remarks>
public static class EnergyShardMesh
{
    // Nose sits on the shader's _SwimHeadZ (0.6) so the whole body follows the swim wave.
    const float NoseZ = 0.62f;
    const float TailZ = -0.58f;
    const float GirdleZ = 0.14f;      // girdle forward of centre: a dart, not a spindle
    const float HalfWidth = 0.17f;    // wider than tall reads as a blade from above
    const float HalfHeight = 0.11f;

    public static Mesh CreateOrUpdate(string assetPath)
    {
        var vertices = new[]
        {
            new Vector3(0f, 0f, NoseZ),                 // 0 nose
            new Vector3(HalfWidth, 0f, GirdleZ),        // 1 right
            new Vector3(0f, HalfHeight, GirdleZ),       // 2 top
            new Vector3(-HalfWidth, 0f, GirdleZ),       // 3 left
            new Vector3(0f, -HalfHeight, GirdleZ),      // 4 bottom
            new Vector3(0f, 0f, TailZ),                 // 5 tail
        };

        var triangles = new[]
        {
            0, 1, 2,   0, 2, 3,   0, 3, 4,   0, 4, 1,   // front cone
            5, 2, 1,   5, 3, 2,   5, 4, 3,   5, 1, 4,   // rear cone
        };

        EnforceOutwardWinding(vertices, triangles);

        var folder = Path.GetDirectoryName(assetPath)!.Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(folder))
            throw new DirectoryNotFoundException($"Mesh folder '{folder}' does not exist.");

        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        var mesh = existing != null ? existing : new Mesh();

        mesh.Clear();
        mesh.name = Path.GetFileNameWithoutExtension(assetPath);
        mesh.indexFormat = IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0, true);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        if (existing == null)
            AssetDatabase.CreateAsset(mesh, assetPath);
        else
            EditorUtility.SetDirty(mesh);

        AssetDatabase.SaveAssetIfDirty(mesh);
        return mesh;
    }

    // Unity's front faces are clockwise, so cross(b - a, c - a) points out of the visible side.
    // Flip any triangle whose normal points back toward the centroid.
    static void EnforceOutwardWinding(Vector3[] vertices, int[] triangles)
    {
        var centroid = Vector3.zero;
        foreach (var v in vertices)
            centroid += v;
        centroid /= vertices.Length;

        for (var t = 0; t < triangles.Length; t += 3)
        {
            var a = vertices[triangles[t]];
            var b = vertices[triangles[t + 1]];
            var c = vertices[triangles[t + 2]];
            var normal = Vector3.Cross(b - a, c - a);
            var outward = (a + b + c) / 3f - centroid;
            if (Vector3.Dot(normal, outward) < 0f)
                (triangles[t + 1], triangles[t + 2]) = (triangles[t + 2], triangles[t + 1]);
        }
    }
}
