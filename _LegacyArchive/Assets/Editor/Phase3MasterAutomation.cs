using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARSubsystems;
using Random = System.Random;

public static class Phase3MasterAutomation
{
    const string MenuPath = "Funobotz/EXECUTE PHASE 3 ZERO-TOUCH";
    const string LogPrefix = "[Phase3MasterAutomation]";

    const string PrefabFolder = "Assets/MatrixPrefabs";
    const string MaterialFolder = PrefabFolder + "/Materials";
    const string CavernPath = PrefabFolder + "/Tabletop_Cavern.prefab";
    const string PetaloPath = PrefabFolder + "/Petalo_Hologram.prefab";
    const string RockMaterialPath = MaterialFolder + "/Cavern_Rock.mat";
    const string CoreMaterialPath = MaterialFolder + "/Lumenforge_Core.mat";
    const string LibraryPath = "Assets/AR_Targets.asset";
    const string ShardPath = "Assets/BoidSwarmEngine/Swarm/Meshes/SM_EnergyShard.asset";
    const string SwarmPrefabPath = "Assets/BoidSwarmEngine/Resources/BoidComputeManager.prefab";

    const string OriginName = "XR Origin (Mobile AR)";
    const string CavernImage = "all_knowing_eye";
    const string PetaloImage = "king_of_spades";

    const string LitShader = "Universal Render Pipeline/Lit";

    // The cavern is modelled in 2 x 2 units and scaled so it sits on a tabletop: 0.15 → 0.30 m wide.
    const float CavernScale = 0.15f;

    [MenuItem(MenuPath, validate = true)]
    static bool Validate() => !EditorApplication.isPlayingOrWillChangePlaymode;

    [MenuItem(MenuPath, priority = 0)]
    public static void Execute()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var report = new StringBuilder();
        try
        {
            EditorUtility.DisplayProgressBar("Final Deployment", "Checking AR_Floor layer", 0.05f);
            var floorLayer = EnsureFloorLayer(report);

            EnsureFolder(PrefabFolder);
            EnsureFolder(MaterialFolder);

            EditorUtility.DisplayProgressBar("Final Deployment", "Building Tabletop_Cavern", 0.25f);
            var cavern = BuildCavern(floorLayer, report);

            EditorUtility.DisplayProgressBar("Final Deployment", "Energy Shard swarm mesh", 0.45f);
            ApplyEnergyShard(report);

            EditorUtility.DisplayProgressBar("Final Deployment", "Upgrading Petalo", 0.6f);
            var petalo = UpgradePetalo(report);

            EditorUtility.DisplayProgressBar("Final Deployment", "Wiring AR payloads", 0.8f);
            AssignPayloads(cavern, petalo, report);

            AssetDatabase.SaveAssets();
            Debug.Log($"{LogPrefix} Final deployment complete.\n{report}");
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            EditorUtility.DisplayDialog("Final Deployment", $"Stopped: {e.Message}\n\nSee the Console.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ------------------------------------------------------------------ 1. layer

    static int EnsureFloorLayer(StringBuilder report)
    {
        var existing = LayerMask.NameToLayer(PetaloController.FloorLayerName);
        if (existing >= 0)
        {
            report.AppendLine($"Layer '{PetaloController.FloorLayerName}' exists (index {existing}).");
            return existing;
        }

        Debug.LogWarning($"{LogPrefix} Layer '{PetaloController.FloorLayerName}' does not exist; creating it in the first free user layer.");

        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tagManager.FindProperty("layers");
        for (var i = 8; i < layers.arraySize; i++)
        {
            var slot = layers.GetArrayElementAtIndex(i);
            if (!string.IsNullOrEmpty(slot.stringValue))
                continue;

            slot.stringValue = PetaloController.FloorLayerName;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
            report.AppendLine($"Layer '{PetaloController.FloorLayerName}' created at index {i}.");
            return i;
        }

        Debug.LogWarning($"{LogPrefix} No free user layer; create '{PetaloController.FloorLayerName}' in Project Settings > Tags and Layers. The floor stays on Default.");
        report.AppendLine($"Layer '{PetaloController.FloorLayerName}' missing and no free slot: taps will not work until it is created.");
        return 0;
    }

    // ------------------------------------------------------------------ 2-3. cavern

    static GameObject BuildCavern(int floorLayer, StringBuilder report)
    {
        var rock = GetOrCreateMaterial(RockMaterialPath, new Color(0.07f, 0.065f, 0.075f), 0.35f, emissive: false);
        var coreMaterial = GetOrCreateMaterial(CoreMaterialPath, new Color(0.12f, 0.1f, 0.08f), 0.8f, emissive: true);

        var root = new GameObject("Tabletop_Cavern");
        try
        {
            root.transform.localScale = Vector3.one * CavernScale;

            // Floor: 2 x 2 units, top face at y = 0 so the cavern sits on the card.
            var floor = Block(root.transform, "Floor", new Vector3(0f, -0.025f, 0f), new Vector3(2f, 0.05f, 2f), rock, keepCollider: true);
            floor.layer = floorLayer;

            // Cave walls: an irregular ring of stone slabs around the rim.
            var rng = new Random(2026);
            const int wallCount = 12;
            for (var i = 0; i < wallCount; i++)
            {
                var angle = i * Mathf.PI * 2f / wallCount + Range(rng, -0.08f, 0.08f);
                var radius = Range(rng, 0.82f, 0.92f);
                var height = Range(rng, 0.35f, 0.9f);
                var pos = new Vector3(Mathf.Cos(angle) * radius, height * 0.5f, Mathf.Sin(angle) * radius);
                var wall = Block(root.transform, $"Wall_{i:00}", pos,
                    new Vector3(Range(rng, 0.35f, 0.55f), height, Range(rng, 0.14f, 0.24f)), rock);
                wall.transform.localRotation = Quaternion.Euler(Range(rng, -6f, 6f), -angle * Mathf.Rad2Deg + 90f, Range(rng, -8f, 8f));
            }

            // A few boulders inside the ring for depth.
            for (var i = 0; i < 4; i++)
            {
                var angle = Range(rng, 0f, Mathf.PI * 2f);
                var radius = Range(rng, 0.45f, 0.65f);
                var s = Range(rng, 0.12f, 0.22f);
                var boulder = Block(root.transform, $"Boulder_{i}",
                    new Vector3(Mathf.Cos(angle) * radius, s * 0.4f, Mathf.Sin(angle) * radius), new Vector3(s, s * 0.8f, s), rock);
                boulder.transform.localRotation = Quaternion.Euler(Range(rng, -15f, 15f), Range(rng, 0f, 90f), Range(rng, -15f, 15f));
            }

            // Lumenforge Core: a diamond-oriented cube on a short plinth at the centre.
            Block(root.transform, "Core_Plinth", new Vector3(0f, 0.06f, 0f), new Vector3(0.3f, 0.12f, 0.3f), rock);
            var core = Block(root.transform, "Lumenforge Core", new Vector3(0f, 0.32f, 0f), Vector3.one * 0.24f, coreMaterial);
            core.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);

            var ignition = root.AddComponent<ForgeIgnition>();
            ignition.coreRenderer = core.GetComponent<MeshRenderer>();

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, CavernPath, out var saved);
            if (!saved)
                throw new IOException($"Failed to save {CavernPath}.");

            report.AppendLine($"Cavern: {CavernPath} ({CavernScale * 2f:0.##} m wide, floor on layer {LayerMask.LayerToName(floorLayer)}, ForgeIgnition wired to 'Lumenforge Core').");
            return prefab;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    static GameObject Block(Transform parent, string name, Vector3 localPosition, Vector3 localScale, Material material, bool keepCollider = false)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (!keepCollider)
            Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localScale = localScale;
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        return go;
    }

    // The core ships with _EMISSION on (at black) so URP keeps the emissive variant in the build;
    // ForgeIgnition then only has to raise the colour.
    static Material GetOrCreateMaterial(string path, Color baseColor, float smoothness, bool emissive)
    {
        var shader = Shader.Find(LitShader) ?? throw new System.InvalidOperationException($"Shader '{LitShader}' not found. Is URP active?");
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(material, path);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetColor("_BaseColor", baseColor);
        material.SetFloat("_Smoothness", smoothness);
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Surface", 0f);
        BaseShaderGUI.SetMaterialKeywords(material);

        if (emissive)
        {
            material.SetColor("_EmissionColor", Color.black);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            material.EnableKeyword("_EMISSION");
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    // ------------------------------------------------------------------ 3. swarm mesh

    // Elongated diamond ("Energy Shard") pointing down +Z, the forward axis the swarm expects.
    // 6 shared vertices, 8 triangles, 16-bit indices, position + normal only (all the shader reads).
    static void ApplyEnergyShard(StringBuilder report)
    {
        var vertices = new[]
        {
            new Vector3(0f, 0f, 0.62f),      // nose
            new Vector3(0.17f, 0f, 0.14f),   // right
            new Vector3(0f, 0.11f, 0.14f),   // top
            new Vector3(-0.17f, 0f, 0.14f),  // left
            new Vector3(0f, -0.11f, 0.14f),  // bottom
            new Vector3(0f, 0f, -0.58f),     // tail
        };
        var triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1, 5, 2, 1, 5, 3, 2, 5, 4, 3, 5, 1, 4 };

        // Unity front faces are clockwise: flip any triangle whose normal points at the centroid.
        var centroid = vertices.Aggregate(Vector3.zero, (acc, v) => acc + v) / vertices.Length;
        for (var t = 0; t < triangles.Length; t += 3)
        {
            Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - centroid) < 0f)
                (triangles[t + 1], triangles[t + 2]) = (triangles[t + 2], triangles[t + 1]);
        }

        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(ShardPath);
        var isNew = mesh == null;
        if (isNew)
            mesh = new Mesh();

        mesh.Clear();
        mesh.name = Path.GetFileNameWithoutExtension(ShardPath);
        mesh.indexFormat = IndexFormat.UInt16;
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0, true);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        if (isNew)
            AssetDatabase.CreateAsset(mesh, ShardPath);
        else
            EditorUtility.SetDirty(mesh);
        AssetDatabase.SaveAssetIfDirty(mesh);
        report.AppendLine($"Swarm mesh: {ShardPath} (6 vertices, 8 triangles).");

        if (AssetDatabase.LoadAssetAtPath<GameObject>(SwarmPrefabPath) != null)
        {
            var root = PrefabUtility.LoadPrefabContents(SwarmPrefabPath);
            try
            {
                var swarm = root.GetComponent<MAAYAI.Matrix.Swarm.SwarmManager>();
                if (swarm != null)
                {
                    swarm.boidMesh = mesh;
                    PrefabUtility.SaveAsPrefabAsset(root, SwarmPrefabPath);
                    report.AppendLine($"Swarm prefab: {SwarmPrefabPath} now uses {mesh.name}.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        else
        {
            report.AppendLine($"Swarm prefab: {SwarmPrefabPath} not found; run Matrix > Auto-Wire AR Boids.");
        }

        // ARBoidBridge's own mesh field is the fallback when no prefab is available.
        var bridge = SceneManager.GetActiveScene().GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<ARBoidBridge>(true))
            .FirstOrDefault();
        if (bridge != null)
        {
            var so = new SerializedObject(bridge);
            var meshProp = so.FindProperty("m_BoidMesh");
            if (meshProp != null)
            {
                meshProp.objectReferenceValue = mesh;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(bridge);
                EditorSceneManager.MarkSceneDirty(bridge.gameObject.scene);
                report.AppendLine("Scene: ARBoidBridge fallback mesh set to the Energy Shard.");
            }
        }
    }

    // ------------------------------------------------------------------ 4. Petalo

    static GameObject UpgradePetalo(StringBuilder report)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(PetaloPath) == null)
            throw new FileNotFoundException($"{PetaloPath} not found. Run Funobotz > Inject Petalo first.");

        var root = PrefabUtility.LoadPrefabContents(PetaloPath);
        try
        {
            var changes = new List<string>();
            if (root.GetComponent<PetaloController>() == null)
            {
                root.AddComponent<PetaloController>();
                changes.Add(nameof(PetaloController));
            }

            var emitter = root.GetComponent<EyeEmitterBridge>();
            if (emitter == null)
            {
                emitter = root.AddComponent<EyeEmitterBridge>();
                changes.Add(nameof(EyeEmitterBridge));
            }

            var eyeQuad = root.transform.Find("Quad");
            var emitterSo = new SerializedObject(emitter);
            emitterSo.FindProperty("m_Quad").objectReferenceValue = eyeQuad != null ? eyeQuad : root.transform;
            emitterSo.ApplyModifiedPropertiesWithoutUndo();

            if (root.GetComponentInChildren<Collider>(true) == null)
            {
                // Sized to the aspect-corrected quad child, thin in depth.
                var quad = root.transform.Find("Quad");
                var width = quad != null ? quad.localScale.x : 1f;
                var box = root.AddComponent<BoxCollider>();
                box.size = new Vector3(width, 1f, 0.1f);
                box.center = Vector3.zero;
                changes.Add(nameof(BoxCollider));
            }

            root.tag = PetaloController.PlayerTag;
            PrefabUtility.SaveAsPrefabAsset(root, PetaloPath);

            report.AppendLine(changes.Count > 0
                ? $"Petalo: added {string.Join(", ", changes)} to {PetaloPath}; tagged '{PetaloController.PlayerTag}'."
                : $"Petalo: {PetaloPath} already had a controller, eye emitter and collider.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        return AssetDatabase.LoadAssetAtPath<GameObject>(PetaloPath);
    }

    // ------------------------------------------------------------------ 5-7. payloads

    static void AssignPayloads(GameObject cavern, GameObject petalo, StringBuilder report)
    {
        var scene = SceneManager.GetActiveScene();
        var origin = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject)
            .FirstOrDefault(go => go.name == OriginName);
        if (origin == null)
            throw new System.InvalidOperationException($"No '{OriginName}' in the open scene '{scene.name}'.");

        var router = origin.GetComponent<ARMultiMatrixPayload>();
        if (router == null)
            throw new System.InvalidOperationException($"'{OriginName}' has no {nameof(ARMultiMatrixPayload)}.");

        var so = new SerializedObject(router);
        var payloads = so.FindProperty("m_Payloads");

        // The router matches reference names exactly, so write the library's own spelling
        // ("king of spades"), not the requested alias ("king_of_spades").
        foreach (var (image, prefab) in new[] { (CavernImage, cavern), (PetaloImage, petalo) })
        {
            var canonical = CanonicalImageName(image);
            var index = -1;
            for (var i = 0; i < payloads.arraySize; i++)
            {
                if (Normalize(payloads.GetArrayElementAtIndex(i).FindPropertyRelative("imageName").stringValue) == Normalize(canonical))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                index = payloads.arraySize;
                payloads.InsertArrayElementAtIndex(index);
            }

            var element = payloads.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("imageName").stringValue = canonical;
            element.FindPropertyRelative("prefab").objectReferenceValue = prefab;
            report.AppendLine($"Payload: '{canonical}' → {prefab.name}.");
        }

        Undo.RecordObject(router, "Final Deployment Payloads");
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(router);
        EditorSceneManager.MarkSceneDirty(scene);

        if (!string.IsNullOrEmpty(scene.path) && EditorSceneManager.SaveScene(scene))
            report.AppendLine($"Scene saved: {scene.path}.");
        else
            report.AppendLine("Scene NOT saved: save it manually.");
    }

    static string CanonicalImageName(string requested)
    {
        var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
        if (library != null)
        {
            foreach (var image in library)
            {
                if (Normalize(image.name) == Normalize(requested))
                    return image.name;
            }
        }

        Debug.LogWarning($"{LogPrefix} '{requested}' is not in {LibraryPath}; writing it as given.");
        return requested;
    }

    // ------------------------------------------------------------------ helpers

    static string Normalize(string s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Trim().Replace('_', ' ').ToLowerInvariant();

    static float Range(Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        var parent = Path.GetDirectoryName(path)!.Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
