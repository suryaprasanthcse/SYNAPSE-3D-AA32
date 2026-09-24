using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Wires the Omni-Matrix router on the XR Origin: swaps out the single-target payload,
/// generates one colored hologram prefab per target, and fills the routing table.
/// </summary>
public static class ARMatrixMapper
{
    const string k_LogPrefix = "[ARMatrixMapper]";
    const string k_OriginName = "XR Origin (Mobile AR)";
    const string k_PrefabFolder = "Assets/MatrixPrefabs";
    const string k_MaterialFolder = k_PrefabFolder + "/Materials";
    const string k_Shader = "Universal Render Pipeline/Unlit";
    const string k_FallbackShader = "Unlit/Color";

    static readonly Vector3 k_Scale = new(0.05f, 0.05f, 0.05f);

    // Lifts each hologram by half its height so it rests on the card instead of intersecting it.
    static readonly Vector3 k_Offset = new(0f, 0.025f, 0f);

    readonly struct Target
    {
        public readonly string ImageName;
        public readonly string PrefabName;
        public readonly PrimitiveType Shape;
        public readonly Color Color;

        public Target(string imageName, string prefabName, PrimitiveType shape, Color color)
        {
            ImageName = imageName;
            PrefabName = prefabName;
            Shape = shape;
            Color = color;
        }
    }

    static readonly Target[] k_Targets =
    {
        new("all_knowing_eye", "Red_Cube",        PrimitiveType.Cube,     Color.red),
        new("joker",           "Blue_Sphere",     PrimitiveType.Sphere,   Color.blue),
        new("valentoro",       "Green_Capsule",   PrimitiveType.Capsule,  Color.green),
        new("queen_of_hearts", "Yellow_Cylinder", PrimitiveType.Cylinder, Color.yellow),
        new("king of spades",  "Purple_Quad",     PrimitiveType.Quad,     new Color(0.5f, 0f, 1f)),
    };

    [MenuItem("Tools/Map Omni-Matrix")]
    public static void MapOmniMatrix()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError($"{k_LogPrefix} Exit Play mode before mapping.");
            return;
        }

        // 1. Locate the origin, including inactive objects, which GameObject.Find would miss.
        var scene = SceneManager.GetActiveScene();
        var origin = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject)
            .FirstOrDefault(go => go.name == k_OriginName);

        if (origin == null)
        {
            Debug.LogError($"{k_LogPrefix} No GameObject named '{k_OriginName}' in '{scene.name}'.");
            return;
        }

        var shader = Shader.Find(k_Shader) ?? Shader.Find(k_FallbackShader);
        if (shader == null)
        {
            Debug.LogError($"{k_LogPrefix} Neither '{k_Shader}' nor '{k_FallbackShader}' is available.");
            return;
        }

        // 2. Remove the single-target payload so targets don't spawn a cube on top of their hologram.
        foreach (var legacy in origin.GetComponents<ARMatrixPayload>())
            Undo.DestroyObjectImmediate(legacy);

        // 3. Attach the router.
        var router = origin.GetComponent<ARMultiMatrixPayload>();
        if (router == null)
            router = Undo.AddComponent<ARMultiMatrixPayload>(origin);

        // 4. Folders.
        EnsureFolder(k_PrefabFolder);
        EnsureFolder(k_MaterialFolder);

        // 5. Prefabs.
        var prefabs = new GameObject[k_Targets.Length];
        for (var i = 0; i < k_Targets.Length; i++)
            prefabs[i] = CreatePrefab(k_Targets[i], shader);

        // 6. Routing table. m_Payloads is private, so it is written through serialization.
        var so = new SerializedObject(router);
        var payloads = so.FindProperty("m_Payloads");
        payloads.arraySize = k_Targets.Length;
        for (var i = 0; i < k_Targets.Length; i++)
        {
            var element = payloads.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("imageName").stringValue = k_Targets[i].ImageName;
            element.FindPropertyRelative("prefab").objectReferenceValue = prefabs[i];
        }
        so.ApplyModifiedProperties();

        // 7. Persist.
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
        {
            Debug.LogError($"{k_LogPrefix} Failed to save '{scene.path}'.");
            return;
        }

        Selection.activeGameObject = origin;
        Debug.Log($"{k_LogPrefix} Mapped {k_Targets.Length} targets on '{k_OriginName}' and saved '{scene.path}'.", router);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        var slash = path.LastIndexOf('/');
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }

    static GameObject CreatePrefab(Target target, Shader shader)
    {
        var instance = GameObject.CreatePrimitive(target.Shape);
        instance.name = target.PrefabName;

        // Holograms only need to render; colliders would intercept AR raycasts.
        Object.DestroyImmediate(instance.GetComponent<Collider>());

        instance.transform.localPosition = k_Offset;
        instance.transform.localScale = k_Scale;

        // A Quad stands upright facing -Z; tip it flat so it faces up off the card like the others.
        if (target.Shape == PrimitiveType.Quad)
            instance.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        var renderer = instance.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = GetOrCreateMaterial(target, shader);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        // Overwriting in place keeps the prefab GUID, so existing references survive re-runs.
        var path = $"{k_PrefabFolder}/{target.PrefabName}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
        Object.DestroyImmediate(instance);

        if (prefab == null)
            Debug.LogError($"{k_LogPrefix} Failed to save prefab {path}.");
        return prefab;
    }

    // Prefabs can only reference materials that exist as assets.
    static Material GetOrCreateMaterial(Target target, Shader shader)
    {
        var path = $"{k_MaterialFolder}/{target.PrefabName}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = shader;
        }

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", target.Color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", target.Color);

        EditorUtility.SetDirty(material);
        return material;
    }
}
