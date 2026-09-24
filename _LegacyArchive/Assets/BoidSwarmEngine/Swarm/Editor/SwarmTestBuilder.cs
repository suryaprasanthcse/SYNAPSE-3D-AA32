using System.IO;
using MAAYAI.Matrix.Telemetry;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MAAYAI.Matrix.Swarm.Editor
{
    /// <summary>
    /// One-click, idempotent construction of a fully wired GPU swarm test rig.
    /// Tools > Build GPU Swarm Test
    /// </summary>
    public static class SwarmTestBuilder
    {
        private const string MenuPath = "Tools/Build GPU Swarm Test";
        private const string HostName = "GPU_Swarm_Host";
        private const string ComputeAssetName = "SwarmCompute";
        private const string BoidShaderName = "MAAYAI/Swarm/BoidIndirectLit";
        private const string MaterialFolder = "Assets/BoidSwarmEngine/Swarm/Materials";
        private const string MaterialPath = MaterialFolder + "/M_SwarmBoid.mat";
        private const string MeshFolder = "Assets/BoidSwarmEngine/Swarm/Meshes";
        private const string MeshPath = MeshFolder + "/SM_BoidPyramid.asset";

        // +Z pyramid: tip forward, square base behind. Length 1, base 0.7 x 0.7.
        private const float PyramidTipZ = 0.6f;
        private const float PyramidBaseZ = -0.4f;
        private const float PyramidBaseHalfSize = 0.35f;

        [MenuItem(MenuPath, priority = 0)]
        public static void BuildSwarmTest()
        {
            ComputeShader compute = LocateComputeShader();
            if (compute == null)
            {
                Fail($"Could not find '{ComputeAssetName}.compute' in the AssetDatabase.");
                return;
            }

            Material material = CreateOrResetMaterial();
            if (material == null)
                return;

            Mesh mesh = CreateOrUpdatePyramidMesh();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Build GPU Swarm Test");

            DestroyExistingHosts();

            var host = new GameObject(HostName);
            Undo.RegisterCreatedObjectUndo(host, "Create " + HostName);
            host.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var manager = Undo.AddComponent<SwarmManager>(host);
            manager.swarmCompute = compute;
            manager.boidMaterial = material;
            manager.boidMesh = mesh;

            // Raw IMGUI frame telemetry (FPS, frame time, CPU/GPU ms) on the same host.
            Undo.AddComponent<MatrixTelemetry>(host);

            Undo.CollapseUndoOperations(undoGroup);

            EditorSceneManager.MarkSceneDirty(host.scene);
            Selection.activeGameObject = host;
            EditorGUIUtility.PingObject(host);

            Debug.Log($"[SwarmTestBuilder] Built '{HostName}' " +
                      $"(compute: {AssetDatabase.GetAssetPath(compute)}, material: {MaterialPath}, mesh: {mesh.name}, telemetry: on). " +
                      "Enter Play mode to run the swarm.", host);
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool ValidateBuildSwarmTest()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static ComputeShader LocateComputeShader()
        {
            foreach (string guid in AssetDatabase.FindAssets($"{ComputeAssetName} t:ComputeShader"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets matches substrings; require the exact file name.
                if (Path.GetFileNameWithoutExtension(path) == ComputeAssetName)
                    return AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }

            return null;
        }

        private static Material CreateOrResetMaterial()
        {
            Shader shader = Shader.Find(BoidShaderName);
            if (shader == null)
            {
                Fail($"Shader '{BoidShaderName}' not found. Check SwarmBoidIndirect.shader compiled without errors.");
                return null;
            }

            EnsureFolder(MaterialFolder);

            // RenderMeshIndirect + SV_InstanceID needs no instancing variants.
            var freshMaterial = new Material(shader) { name = Path.GetFileNameWithoutExtension(MaterialPath) };
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);

            if (existing == null)
            {
                AssetDatabase.CreateAsset(freshMaterial, MaterialPath);
                AssetDatabase.SaveAssets();
                return freshMaterial;
            }

            // Rebuild in place: resets to shader defaults while keeping the asset GUID,
            // so any other references to the material stay valid.
            existing.shader = shader;
            existing.CopyPropertiesFromMaterial(freshMaterial);
            Object.DestroyImmediate(freshMaterial);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssetIfDirty(existing);
            return existing;
        }

        /// <summary>
        /// Builds a 5-vertex, 6-triangle pyramid pointing down +Z and saves it as an asset.
        /// Re-running rebuilds the existing asset in place so its GUID (and references) survive.
        /// </summary>
        private static Mesh CreateOrUpdatePyramidMesh()
        {
            var vertices = new[]
            {
                new Vector3(0f, 0f, PyramidTipZ),                                    // 0 tip
                new Vector3(-PyramidBaseHalfSize, -PyramidBaseHalfSize, PyramidBaseZ), // 1
                new Vector3( PyramidBaseHalfSize, -PyramidBaseHalfSize, PyramidBaseZ), // 2
                new Vector3( PyramidBaseHalfSize,  PyramidBaseHalfSize, PyramidBaseZ), // 3
                new Vector3(-PyramidBaseHalfSize,  PyramidBaseHalfSize, PyramidBaseZ), // 4
            };

            var triangles = new[]
            {
                0, 1, 2,   0, 2, 3,   0, 3, 4,   0, 4, 1,   // sides
                1, 4, 3,   1, 3, 2                          // base
            };

            EnforceOutwardWinding(vertices, triangles);

            EnsureFolder(MeshFolder);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            Mesh mesh = existing != null ? existing : new Mesh();

            mesh.Clear();
            mesh.name = Path.GetFileNameWithoutExtension(MeshPath);
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            // Shared vertices give smoothed normals; the swarm shader rebuilds flat
            // face normals per pixel, and these still orient them outward.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, MeshPath);
            }
            else
            {
                EditorUtility.SetDirty(mesh);
            }

            AssetDatabase.SaveAssetIfDirty(mesh);
            return mesh;
        }

        /// <summary>
        /// Unity treats clockwise triangles as front-facing, which makes
        /// cross(b - a, c - a) point out of the visible side. Flips any triangle
        /// whose normal points back toward the mesh centroid.
        /// </summary>
        private static void EnforceOutwardWinding(Vector3[] vertices, int[] triangles)
        {
            Vector3 centroid = Vector3.zero;
            foreach (Vector3 v in vertices)
                centroid += v;
            centroid /= vertices.Length;

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t]];
                Vector3 b = vertices[triangles[t + 1]];
                Vector3 c = vertices[triangles[t + 2]];

                Vector3 normal = Vector3.Cross(b - a, c - a);
                Vector3 outward = (a + b + c) / 3f - centroid;

                if (Vector3.Dot(normal, outward) < 0f)
                    (triangles[t + 1], triangles[t + 2]) = (triangles[t + 2], triangles[t + 1]);
            }
        }

        private static void DestroyExistingHosts()
        {
            // Scan root objects of every loaded scene so inactive hosts are caught too.
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name == HostName)
                        Undo.DestroyObjectImmediate(root);
                }
            }
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folderPath));
        }

        private static void Fail(string message)
        {
            Debug.LogError("[SwarmTestBuilder] " + message);
            EditorUtility.DisplayDialog("Build GPU Swarm Test", message, "OK");
        }
    }
}
