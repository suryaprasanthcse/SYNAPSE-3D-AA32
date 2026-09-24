using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Builds the Mystery Cave diorama (authored at room scale in FunobotzDioramas) into a tabletop prefab that the
/// swarm can light: geometry converted to MAAYAI/SwarmSurface with a reflective albedo floor, uniformly scaled so
/// its footprint matches the swarm volume on the card, and recentred so the card sits at the cave floor's centre.
///
/// The source scene is never modified. Converted materials are written beside the prefab.
/// </summary>
public static class MysteryCaveAssembler
{
    const string SourceScene = "Assets/Scenes/FunobotzDioramas.unity";
    const string PrefabPath = "Assets/MatrixPrefabs/MysteryCave_Tabletop.prefab";
    const string MaterialFolder = "Assets/Funobotz/Materials/SwarmSurface";
    const string SurfaceShaderName = "MAAYAI/SwarmSurface";

    // Tabletop footprint (m). The cave is authored ~18 m across; on a card it must live inside the swarm volume.
    const float TargetFootprint = 0.60f;
    const float AlbedoFloor = 0.25f;
    const float AlbedoTarget = 0.32f;

    static readonly int ID_BaseMap = Shader.PropertyToID("_BaseMap");
    static readonly int ID_MainTex = Shader.PropertyToID("_MainTex");
    static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ID_Color = Shader.PropertyToID("_Color");
    static readonly int ID_Smoothness = Shader.PropertyToID("_Smoothness");
    static readonly int ID_SpecularStrength = Shader.PropertyToID("_SpecularStrength");
    static readonly int ID_RenderMode = Shader.PropertyToID("_RenderMode");

    [MenuItem("Funobotz/Assemble Mystery Cave (Tabletop)")]
    public static void Assemble()
    {
        var shader = Shader.Find(SurfaceShaderName);
        if (shader == null)
        {
            Debug.LogError($"[MysteryCaveAssembler] Shader '{SurfaceShaderName}' not found.");
            return;
        }

        Directory.CreateDirectory(MaterialFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));

        Scene scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);
        try
        {
            var root = new GameObject("MysteryCave_Tabletop");
            SceneManager.MoveGameObjectToScene(root, SceneManager.GetActiveScene());

            // Copy only renderable geometry: the source scene's lights and cameras have no place in a void lit
            // solely by the swarm.
            var sources = scene.GetRootGameObjects()
                .Where(go => go.GetComponentsInChildren<Renderer>(true).Length > 0)
                .ToList();

            var copies = new List<GameObject>();
            foreach (var source in sources)
            {
                var copy = Object.Instantiate(source);
                copy.name = source.name;
                copy.transform.SetParent(root.transform, true);
                copies.Add(copy);
                foreach (var light in copy.GetComponentsInChildren<Light>(true)) Object.DestroyImmediate(light);
                foreach (var cam in copy.GetComponentsInChildren<Camera>(true)) Object.DestroyImmediate(cam);

                // Legacy unlit motes/lines belong to the old diorama: the swarm is the only particle system now,
                // and a ParticleSystem's runtime bounds also wreck any measurement taken from it.
                foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true)) Object.DestroyImmediate(ps.gameObject);
                foreach (var lr in copy.GetComponentsInChildren<LineRenderer>(true)) Object.DestroyImmediate(lr.gameObject);
            }

            // Measure the authored footprint, then scale and recentre so the card sits at the floor's centre.
            // Measure from solid geometry only.
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0)
            {
                Debug.LogError("[MysteryCaveAssembler] Source scene has no renderers.");
                Object.DestroyImmediate(root);
                return;
            }

            Bounds bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);

            float footprint = Mathf.Max(bounds.size.x, bounds.size.z);
            float scale = footprint > 1e-4f ? TargetFootprint / footprint : 1f;

            var pivot = new GameObject("CaveGeometry");
            pivot.transform.SetParent(root.transform, false);
            foreach (var copy in copies) copy.transform.SetParent(pivot.transform, true);

            // Recentre horizontally on the cave's middle, vertically on its floor.
            pivot.transform.position = -new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            root.transform.localScale = Vector3.one * scale;

            int converted = ConvertMaterials(root, shader);

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();

            Debug.Log($"[MysteryCaveAssembler] '{prefab.name}': {renderers.Length} renderers, {converted} materials " +
                      $"converted. Authored footprint {footprint:0.0} m -> {TargetFootprint:0.00} m (scale {scale:0.0000}), " +
                      $"height {bounds.size.y * scale:0.00} m. Saved to {PrefabPath}", prefab);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
        }
    }

    static int ConvertMaterials(GameObject root, Shader shader)
    {
        var cache = new Dictionary<Material, Material>();
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            var source = renderer.sharedMaterials;
            var replacement = new Material[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                Material src = source[i];
                if (src == null) { replacement[i] = null; continue; }
                if (src.shader == shader) { replacement[i] = src; continue; }

                if (!cache.TryGetValue(src, out var converted))
                {
                    converted = Convert(src, shader);
                    cache[src] = converted;
                }
                replacement[i] = converted;
            }

            renderer.sharedMaterials = replacement;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }
        return cache.Count;
    }

    static bool dropDarkTextures = true;

    static Material Convert(Material src, Shader shader)
    {
        string path = $"{MaterialFolder}/{Sanitize(src.name)}_Cave.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool isNew = material == null;
        if (isNew) material = new Material(shader);
        else material.shader = shader;

        int texId = src.HasProperty(ID_BaseMap) && src.GetTexture(ID_BaseMap) != null ? ID_BaseMap
                  : src.HasProperty(ID_MainTex) ? ID_MainTex
                  : -1;
        if (texId != -1)
        {
            material.SetTexture(ID_BaseMap, src.GetTexture(texId));
            material.SetTextureScale(ID_BaseMap, src.GetTextureScale(texId));
            material.SetTextureOffset(ID_BaseMap, src.GetTextureOffset(texId));
        }

        Color tint = src.HasProperty(ID_BaseColor) ? src.GetColor(ID_BaseColor)
                   : src.HasProperty(ID_Color) ? src.GetColor(ID_Color)
                   : Color.white;

        // The diorama's rock textures are near-black by design (authored for a lit scene). A dark tint can be
        // raised, but a dark TEXTURE cannot: it would multiply the swarm's light back down to nothing. Cave rock
        // becomes flat reflectance so the swarm can actually paint it into view.
        if (dropDarkTextures)
        {
            material.SetTexture(ID_BaseMap, null);
            tint = new Color(AlbedoTarget, AlbedoTarget, AlbedoTarget, 1f);
        }

        // "Near-black PBR" cave walls reflect nothing when the swarm is the only light: the cave would stay
        // invisible. Darkness must come from the absence of light, not from black albedo.
        float lum = 0.2126f * tint.r + 0.7152f * tint.g + 0.0722f * tint.b;
        if (lum < AlbedoFloor)
            tint = tint.maxColorComponent < 0.02f
                ? new Color(AlbedoTarget, AlbedoTarget, AlbedoTarget, tint.a)
                : tint * (AlbedoTarget / Mathf.Max(lum, 1e-4f));

        material.SetColor(ID_BaseColor, new Color(Mathf.Clamp01(tint.r), Mathf.Clamp01(tint.g), Mathf.Clamp01(tint.b), tint.a));
        material.SetFloat(ID_Smoothness, 0.2f);
        material.SetFloat(ID_SpecularStrength, 0.08f);
        material.SetFloat(ID_RenderMode, 0f);   // Opaque: the cave is virtual geometry and must occlude

        if (isNew) AssetDatabase.CreateAsset(material, path);
        else EditorUtility.SetDirty(material);
        return material;
    }

    static string Sanitize(string name)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Replace(" (Instance)", string.Empty).Replace(" (Clone)", string.Empty);
    }
}
