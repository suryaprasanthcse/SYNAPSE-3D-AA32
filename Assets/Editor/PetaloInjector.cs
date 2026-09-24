using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the Petalo hologram prefab from the character cutout and maps it to the anchor card in
/// <see cref="ARMultiMatrixPayload"/>. Menu: Funobotz > Inject Petalo.
/// </summary>
/// <remarks>
/// Idempotent: the material and prefab are rebuilt in place, so their GUIDs survive and any
/// existing references stay valid. The scene edit goes through SerializedObject, so Unity writes
/// the YAML and the change is undoable.
/// </remarks>
public static class PetaloInjector
{
    const string MenuPath = "Funobotz/Inject Petalo";
    const string LogPrefix = "[PetaloInjector]";

    const string TexturePath = "Assets/petalo_transparent.png";
    const string PrefabFolder = "Assets/MatrixPrefabs";
    const string MaterialFolder = PrefabFolder + "/Materials";
    const string MaterialPath = MaterialFolder + "/Petalo_Hologram.mat";
    const string PrefabPath = PrefabFolder + "/Petalo_Hologram.prefab";

    const string OriginName = "XR Origin (Mobile AR)";
    const string TargetImage = "king of spades";
    const string PayloadsField = "m_Payloads";
    const string ImageNameField = "imageName";
    const string PrefabField = "prefab";

    const string UnlitShader = "Universal Render Pipeline/Unlit";

    // Requested hologram size, applied to the prefab root.
    static readonly Vector3 RootScale = new(0.08f, 0.08f, 0.08f);

    // The source cutout is 2:3, so the quad child is narrowed to keep Petalo's proportions while the
    // root keeps the exact requested uniform scale. Set false for a square 8 x 8 cm quad.
    const bool PreserveAspect = true;

    // Kills the faint 1-15 alpha haze around the cutout without hardening the soft edges.
    const float HazeCutoff = 0.1f;

    [MenuItem(MenuPath, validate = true)]
    static bool Validate() => !EditorApplication.isPlayingOrWillChangePlaymode;

    [MenuItem(MenuPath, priority = 31)]
    public static void InjectPetalo()
    {
        // 1. Texture
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
        if (texture == null)
        {
            Fail($"'{TexturePath}' not found.");
            return;
        }

        var shader = Shader.Find(UnlitShader);
        if (shader == null)
        {
            Fail($"Shader '{UnlitShader}' not found. Is URP active?");
            return;
        }

        EnsureFolder(PrefabFolder);
        EnsureFolder(MaterialFolder);

        var material = CreateOrUpdateMaterial(texture, shader);
        var prefab = BuildPrefab(material, texture);
        if (prefab == null)
            return;

        AssetDatabase.SaveAssets();

        // 4. Map it to the anchor card.
        if (!AssignToPayload(prefab, out var message))
        {
            Fail(message);
            return;
        }

        Selection.activeObject = prefab;
        EditorGUIUtility.PingObject(prefab);
        Debug.Log($"{LogPrefix} {message}", prefab);
    }

    /// <summary>URP Unlit, transparent, two-sided, with a low alpha clip to drop the haze.</summary>
    static Material CreateOrUpdateMaterial(Texture2D texture, Shader shader)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material == null)
        {
            material = new Material(shader) { name = Path.GetFileNameWithoutExtension(MaterialPath) };
            AssetDatabase.CreateAsset(material, MaterialPath);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_Surface", 1f);        // Transparent
        material.SetFloat("_Blend", 0f);          // Alpha
        material.SetFloat("_ZWrite", 0f);
        material.SetFloat("_Cull", 0f);           // visible from both sides
        material.SetFloat("_AlphaClip", 1f);
        material.SetFloat("_Cutoff", HazeCutoff);

        // URP's own inspector routine: derives keywords, blend state and render queue.
        BaseShaderGUI.SetMaterialKeywords(material);
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>
    /// Root holds the requested uniform scale and the billboard; the quad child carries the mesh so
    /// the aspect fix never changes the root scale. Pivot sits on the card, so Petalo stands on it.
    /// </summary>
    static GameObject BuildPrefab(Material material, Texture2D texture)
    {
        var root = new GameObject("Petalo_Hologram");
        try
        {
            root.transform.localScale = RootScale;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localPosition = new Vector3(0f, RootScale.y * 0.5f, 0f);
            root.AddComponent<FunobotzBillboard>();

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Quad";
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            quad.transform.SetParent(root.transform, false);

            var aspect = PreserveAspect && texture.height > 0 ? (float)texture.width / texture.height : 1f;
            quad.transform.localScale = new Vector3(aspect, 1f, 1f);

            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out var saved);
            if (!saved || prefab == null)
            {
                Fail($"Failed to save '{PrefabPath}'.");
                return null;
            }

            return prefab;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    static bool AssignToPayload(GameObject prefab, out string message)
    {
        var scene = SceneManager.GetActiveScene();
        var origin = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .Select(t => t.gameObject)
            .FirstOrDefault(go => go.name == OriginName);

        if (origin == null)
        {
            message = $"No GameObject named '{OriginName}' in the open scene '{scene.name}'. The prefab was still created at {PrefabPath}.";
            return false;
        }

        var router = origin.GetComponent<ARMultiMatrixPayload>();
        if (router == null)
        {
            message = $"'{OriginName}' has no {nameof(ARMultiMatrixPayload)}. The prefab was still created at {PrefabPath}.";
            return false;
        }

        var serialized = new SerializedObject(router);
        var payloads = serialized.FindProperty(PayloadsField);
        if (payloads == null || !payloads.isArray)
        {
            message = $"{nameof(ARMultiMatrixPayload)} has no serialized '{PayloadsField}' list.";
            return false;
        }

        var index = -1;
        for (var i = 0; i < payloads.arraySize; i++)
        {
            var name = payloads.GetArrayElementAtIndex(i).FindPropertyRelative(ImageNameField)?.stringValue;
            if (Normalize(name) == Normalize(TargetImage))
            {
                index = i;
                break;
            }
        }

        var added = index < 0;
        if (added)
        {
            index = payloads.arraySize;
            payloads.InsertArrayElementAtIndex(index);
        }

        var element = payloads.GetArrayElementAtIndex(index);
        var previous = element.FindPropertyRelative(PrefabField)?.objectReferenceValue;
        element.FindPropertyRelative(ImageNameField).stringValue = TargetImage;
        element.FindPropertyRelative(PrefabField).objectReferenceValue = prefab;

        Undo.RecordObject(router, "Inject Petalo");
        serialized.ApplyModifiedProperties();
        EditorUtility.SetDirty(router);
        EditorSceneManager.MarkSceneDirty(scene);

        var saved = !string.IsNullOrEmpty(scene.path) && EditorSceneManager.SaveScene(scene);
        var action = added ? "Added" : $"Replaced {(previous != null ? previous.name : "<empty>")} in";
        message = $"{action} the '{TargetImage}' payload with {prefab.name} ({payloads.arraySize} payload(s) total). " +
                  (saved ? $"Saved '{scene.path}'." : "Scene not saved: save it manually.");
        return true;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        var parent = Path.GetDirectoryName(path)!.Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    static string Normalize(string name) =>
        string.IsNullOrEmpty(name) ? string.Empty : name.Trim().Replace('_', ' ').ToLowerInvariant();

    static void Fail(string message)
    {
        Debug.LogError($"{LogPrefix} {message}");
        EditorUtility.DisplayDialog("Inject Petalo", message, "OK");
    }
}
