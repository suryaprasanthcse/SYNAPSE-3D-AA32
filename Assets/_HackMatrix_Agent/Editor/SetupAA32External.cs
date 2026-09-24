using System.IO;
using MAAYAI.HackMatrix;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Brings the two staged third-party models into the AA-32 scene without their original shaders:
///
///   drone      Assets/ExternalModels/drone_v1 (FBX extracted from Drone_021.zip; imported on an Arnold shader graph)
///   buildings  Assets/ExternalModels/stylized-low-poly-buildings-pack (Edificios.fbx: four buildings on one atlas,
///              imported on a transparent Autodesk Interactive shader)
///
/// Every renderer is re-materialed with saved URP Lit assets (Assets/Materials/Extracted), so nothing depends on a
/// shader the Android build could strip, and every collider is removed. Then each model is wrapped and fitted:
///
///   buildings  on their own palette (untinted), each on a glowing base pad in the zone colour, fitted into the unit box the compiler scales to the zone (proportions kept in plan, height filled,
///              since the compiler sets height from the zone's storeys anyway). The HazardZone building is tilted
///              and ringed with rubble and the ShoringSite building leans, so the zones
///              still SHOW collapse and instability - the pack's buildings are intact.
///   drone      real size (11 cm across; the compiler places the drone unscaled), pivot at the bottom of the
///              model so it stands on the landing pad, nose along +z as the compiler expects.
///
/// Measured bounds keep every part inside the zone's box, so PlanVerifier's geometry still describes what is drawn.
///
/// Credits (CC BY 4.0, https://creativecommons.org/licenses/by/4.0/; changes listed in CREDITS.md):
///   "Stylized Low Poly Buildings Pack" (https://skfb.ly/pz778) by Mauio2369
///   "Drone" (https://skfb.ly/oUv78) by ROHIT3DMODELS
///   "Simple Low Poly Abandoned Brick Building" (https://skfb.ly/oSpOD) by jimbogies (CC Attribution)
/// </summary>
public static partial class SetupAA32Assets
{
    const string DroneFbx = "Assets/ExternalModels/drone_v1/source/Drone.fbx";
    const string DroneTextures = "Assets/ExternalModels/drone_v1/textures";
    const string BuildingsFbx = "Assets/ExternalModels/stylized-low-poly-buildings-pack/source/Edificios.fbx";
    const string BuildingsAtlas = "Assets/ExternalModels/stylized-low-poly-buildings-pack/textures/Atlasedificio.png";
    const string BrickFolder = "Assets/ExternalModels/simple_low_poly_abandoned_brick_building";
    const string ExtractedFolder = "Assets/Materials/Extracted";

    // The pack building that plays the collapsed school: Box001 (the mid-rise ruin, ~1.9:1). The ShoringSite is the
    // intact brick building (BuildBrickShoring): unstable, not collapsed.
    const string HazardBuilding = "Box001";
    const float DroneSpanMetres = 0.11f;

    [MenuItem("Hack Matrix/AA-32/Import External Models and Wire Scene")]
    public static void ImportExternalAndWire()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[AA32] Stop Play mode first.");
            return;
        }
        var droneModel = AssetDatabase.LoadAssetAtPath<GameObject>(DroneFbx);
        var buildingsModel = AssetDatabase.LoadAssetAtPath<GameObject>(BuildingsFbx);
        if (droneModel == null || buildingsModel == null)
        {
            Debug.LogError($"[AA32] Missing model: {(droneModel == null ? DroneFbx : BuildingsFbx)}.");
            return;
        }
        if (!AssetDatabase.IsValidFolder(ExtractedFolder)) AssetDatabase.CreateFolder(MaterialFolder, "Extracted");

        var lit = Shader.Find(LitShader);
        var droneMat = DroneMaterial(lit);
        // The pack's own palette - grey concrete, white trim, blue glass - untinted, so the facades stay legible. The
        // zone type is carried by a glowing base pad instead of by tinting the building.
        var buildingMat = BuildingMaterial(lit, "Mat_Ext_Building");
        foreach (var stale in new[] { "Mat_Ext_Hazard", "Mat_Ext_Shoring" })         // the earlier tinted versions
            AssetDatabase.DeleteAsset($"{ExtractedFolder}/{stale}.mat");

        // Zone colours for the base pads, and the rubble materials, from the procedural set.
        var glowRed = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/AA32_Hazard.mat");
        var glowAmber = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/AA32_Shoring.mat");
        var brick = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/Mat_Concrete_Hazard.mat");
        var concrete = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/Mat_Concrete_Slab.mat");
        if (glowRed == null || glowAmber == null || brick == null || concrete == null)
        {
            Debug.LogError("[AA32] Procedural materials missing: run Build Composite Prefabs and Wire Scene once first.");
            return;
        }
        AssetDatabase.SaveAssets();

        // HazardZone: the school, tilted 7 degrees and sunk into its own rubble.
        var hazardRoot = new GameObject("AA32_Ext_HazardZone").transform;
        var hazardTilt = new GameObject("Collapse").transform;
        hazardTilt.SetParent(hazardRoot, false);
        hazardTilt.localPosition = new Vector3(0f, -0.03f, 0f);
        hazardTilt.localRotation = Quaternion.Euler(7f, 0f, 4f);
        Fit(Building(buildingsModel, HazardBuilding, buildingMat), hazardTilt, 0.68f, 0.84f);
        RubbleApron(hazardRoot, brick, concrete);
        BasePad(hazardRoot, glowRed);
        var hazard = Save(hazardRoot.gameObject, "AA32_Ext_HazardZone");

        var shoring = BuildBrickShoring(lit, glowAmber);

        // Drone: real size, pivot at its lowest point.
        var droneRoot = new GameObject("AA32_Ext_Drone").transform;
        var droneModelInstance = Strip(Object.Instantiate(droneModel), droneMat);
        FitDrone(droneModelInstance.transform, droneRoot);
        var drone = Save(droneRoot.gameObject, "AA32_Ext_Drone");

        var compiler = Object.FindAnyObjectByType<GenerativeCompiler>();
        if (compiler == null)
        {
            Debug.LogError("[AA32] No GenerativeCompiler in the open scene; prefabs saved but not wired.");
            return;
        }
        var c = new SerializedObject(compiler);
        c.FindProperty("hazardPrefab").objectReferenceValue = hazard;
        c.FindProperty("shoringPrefab").objectReferenceValue = shoring;
        c.FindProperty("dronePrefab").objectReferenceValue = drone;      // rescuePrefab stays the procedural helipad
        c.ApplyModifiedPropertiesWithoutUndo();
        ConfigureLitScene(compiler);

        var scene = compiler.gameObject.scene;
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log($"[AA32] External models wired into {scene.path}: {hazard.name}, {shoring.name}, {drone.name}.");
    }

    /// <summary>
    /// Rebuild ONLY the ShoringSite prefab from the intact brick building and wire it; the HazardZone and drone
    /// prefabs are not touched.
    /// </summary>
    [MenuItem("Hack Matrix/AA-32/Rebuild Shoring Site (Leaning Brick Building)")]
    public static void RebuildShoringAndWire()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[AA32] Stop Play mode first.");
            return;
        }
        var glowAmber = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/AA32_Shoring.mat");
        var shoring = BuildBrickShoring(Shader.Find(LitShader), glowAmber);

        var compiler = Object.FindAnyObjectByType<GenerativeCompiler>();
        var c = new SerializedObject(compiler);
        c.FindProperty("shoringPrefab").objectReferenceValue = shoring;
        c.ApplyModifiedPropertiesWithoutUndo();
        var scene = compiler.gameObject.scene;
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
        Debug.Log($"[AA32] ShoringSite rebuilt from the brick building and wired into {scene.path}.");
    }

    /// <summary>
    /// ShoringSite: an INTACT building - standing but unstable, not collapsed - leaning 7 degrees about x and 4
    /// about z from its base, on the amber pad. The model's own pavement and ground plates are left out (the pad
    /// replaces them), and it keeps its own brick, concrete and roof materials.
    /// </summary>
    static GameObject BuildBrickShoring(Shader lit, Material glowAmber)
    {
        const int Roof = 0, Concrete = 1, Brick = 2, Pavement = 3, Ground = 4;       // glTF material order
        var (mesh, parts) = GltfMeshImporter.Import($"{BrickFolder}/scene.gltf", $"{BrickFolder}/BrickBuilding.asset",
                                                   new[] { Pavement, Ground });

        // glTF baseColorFactor is linear; a Unity material colour is sRGB.
        var roof = LitAsset(lit, "Mat_Ext_Brick_Roof");
        roof.SetTexture("_BaseMap", null);
        roof.SetColor("_BaseColor", new Color(0.018f, 0.018f, 0.018f).gamma);
        roof.SetFloat("_Smoothness", 0.2f);
        roof.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(roof);
        var byGltfMaterial = new System.Collections.Generic.Dictionary<int, Material>
        {
            [Roof] = roof,
            [Concrete] = TexturedLit(lit, "Mat_Ext_Brick_Concrete", "Concrete_albedo"),
            [Brick] = TexturedLit(lit, "Mat_Ext_Brick_Brick", "Brick_albedo")
        };
        var materials = new Material[parts.Count];
        for (int i = 0; i < parts.Count; i++) materials[i] = byGltfMaterial[parts[i].Material];

        var model = new GameObject("BrickBuilding");
        model.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = model.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        renderer.receiveShadows = true;

        var root = new GameObject("AA32_Ext_ShoringSite").transform;
        var lean = new GameObject("Lean").transform;
        lean.SetParent(root, false);
        lean.localRotation = Quaternion.Euler(7f, 0f, 4f);          // pivot at the base centre: the top swings out
        Fit(model.transform, lean, 0.72f, 0.90f);                   // leaves room for the lean inside +-0.5
        BasePad(root, glowAmber);
        AssetDatabase.SaveAssets();
        return Save(root.gameObject, "AA32_Ext_ShoringSite");
    }

    /// <summary>A glTF PBR material on URP Lit: base colour and normal map; metallic 0, rough (glTF metallic 0).</summary>
    static Material TexturedLit(Shader lit, string name, string texturePrefix)
    {
        string baseColor = $"{BrickFolder}/textures/{texturePrefix}_baseColor.png";
        string normal = $"{BrickFolder}/textures/{texturePrefix}_normal.png";
        ConfigureTexture(baseColor, false, true);
        ConfigureTexture(normal, true, false);
        var m = LitAsset(lit, name);
        m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(baseColor));
        m.SetColor("_BaseColor", Color.white);
        m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normal));
        m.EnableKeyword("_NORMALMAP");
        m.SetFloat("_Smoothness", 0.15f);
        m.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(m);
        return m;
    }

    // ---------------------------------------------------------------------------------------------
    // Models
    // ---------------------------------------------------------------------------------------------
    /// <summary>One named building from the pack, unpacked, re-materialed and collider-free.</summary>
    static Transform Building(GameObject pack, string child, Material material)
    {
        var instance = Object.Instantiate(pack);
        Transform keep = null;
        foreach (Transform t in instance.transform)
            if (t.name == child) keep = t;
        if (keep == null)
        {
            Object.DestroyImmediate(instance);
            throw new System.InvalidOperationException($"Building '{child}' not found in {BuildingsFbx}.");
        }
        keep.SetParent(null, true);
        Object.DestroyImmediate(instance);
        return Strip(keep.gameObject, material).transform;
    }

    static GameObject Strip(GameObject model, Material material)
    {
        foreach (var collider in model.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = material;
            renderer.sharedMaterials = mats;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }
        return model;
    }

    /// <summary>
    /// Fit a building into a centred footprint of <paramref name="width"/> (plan proportions kept) and a height of
    /// <paramref name="height"/>, standing on y = 0 of <paramref name="parent"/>. Measured from its renderers, so
    /// the FBX's own pivot, rotation and units do not matter.
    /// </summary>
    static void Fit(Transform model, Transform parent, float width, float height)
    {
        var fit = new GameObject(model.name).transform;
        model.SetParent(fit, true);
        Bounds b = RendererBounds(fit);
        float plan = width / Mathf.Max(b.size.x, b.size.z);
        float vertical = height / b.size.y;
        fit.localScale = new Vector3(plan, vertical, plan);
        fit.position = new Vector3(-b.center.x * plan, -b.min.y * vertical, -b.center.z * plan);
        fit.SetParent(parent, false);
    }

    static void FitDrone(Transform model, Transform root)
    {
        var fit = new GameObject("Model").transform;
        model.SetParent(fit, true);
        Bounds b = RendererBounds(fit);
        float s = DroneSpanMetres / Mathf.Max(b.size.x, b.size.z);
        fit.localScale = Vector3.one * s;
        fit.position = new Vector3(-b.center.x * s, -b.min.y * s, -b.center.z * s);
        fit.SetParent(root, false);
    }

    static Bounds RendererBounds(Transform t)
    {
        var renderers = t.GetComponentsInChildren<Renderer>();
        var b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    // ---------------------------------------------------------------------------------------------
    // Materials
    // ---------------------------------------------------------------------------------------------
    /// <summary>
    /// The drone's PBR set on URP Lit: base colour, normal, occlusion, emission, and a metallic-smoothness map
    /// packed from the separate metallic and roughness maps (URP reads metallic from R, smoothness from A).
    /// Textures are capped at 1024 for the phone; the source maps are 4K.
    /// </summary>
    static Material DroneMaterial(Shader lit)
    {
        string T(string map) => $"{DroneTextures}/Drn_Drn_{map}.png";
        foreach (var map in new[] { "BaseColor", "Normal", "AO", "Emission", "Metallic", "Roughness", "Opacity" })
            ConfigureTexture(T(map), map == "Normal", map is "BaseColor" or "Emission");

        string packed = $"{ExtractedFolder}/Drn_MetallicSmoothness.png";
        PackMetallicSmoothness(T("Metallic"), T("Roughness"), packed, 1024);

        var m = LitAsset(lit, "Mat_Ext_Drone");
        m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(T("BaseColor")));
        m.SetColor("_BaseColor", Color.white);
        m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(T("Normal")));
        m.EnableKeyword("_NORMALMAP");
        m.SetTexture("_OcclusionMap", AssetDatabase.LoadAssetAtPath<Texture2D>(T("AO")));
        m.EnableKeyword("_OCCLUSIONMAP");
        m.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(packed));
        m.SetFloat("_Smoothness", 1f);                                           // scales the packed smoothness
        m.EnableKeyword("_METALLICSPECGLOSSMAP");
        SetEmission(m, AssetDatabase.LoadAssetAtPath<Texture2D>(T("Emission")), Color.white * 2f);
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>The pack's own material on URP Lit: its atlas at white, as the original had it, with no emission.</summary>
    static Material BuildingMaterial(Shader lit, string name)
    {
        ConfigureTexture(BuildingsAtlas, false, true);
        var m = LitAsset(lit, name);
        m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(BuildingsAtlas));
        m.SetColor("_BaseColor", Color.white);
        m.SetFloat("_Smoothness", 0.2f);
        m.SetFloat("_Metallic", 0f);
        m.SetTexture("_EmissionMap", null);
        m.SetColor("_EmissionColor", Color.black);
        m.DisableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>
    /// The zone marker: a flat glowing quad over the whole unit footprint, just above the ground, facing up. The
    /// compiler scales it with the zone, so it outlines exactly the box the verifier checks.
    /// </summary>
    static void BasePad(Transform root, Material glow) =>
        Part(root, PrimitiveType.Quad, new Vector3(0f, 0.01f, 0f), new Vector3(90f, 0f, 0f), new Vector3(1f, 1f, 1f), glow);

    static Material LitAsset(Shader lit, string name)
    {
        string path = $"{ExtractedFolder}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(lit) { name = name };
            AssetDatabase.CreateAsset(m, path);
        }
        m.shader = lit;
        return m;
    }

    /// <summary>Emission on a saved material: the keyword is what keeps the emissive variant in the build.</summary>
    static void SetEmission(Material m, Texture map, Color colour)
    {
        m.SetTexture("_EmissionMap", map);
        m.SetColor("_EmissionColor", colour);
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
    }

    static void ConfigureTexture(string path, bool normalMap, bool colour)
    {
        if (AssetImporter.GetAtPath(path) is not TextureImporter importer) return;
        bool changed = false;
        if (importer.maxTextureSize != 1024) { importer.maxTextureSize = 1024; changed = true; }
        var type = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
        if (importer.textureType != type) { importer.textureType = type; changed = true; }
        if (!normalMap && importer.sRGBTexture != colour) { importer.sRGBTexture = colour; changed = true; }
        if (changed) importer.SaveAndReimport();
    }

    /// <summary>Metallic (R of the metallic map) and smoothness (1 - roughness) packed into one linear RGBA map.</summary>
    static void PackMetallicSmoothness(string metallicPath, string roughnessPath, string outPath, int size)
    {
        var metallic = ReadLinear(metallicPath, size);
        var roughness = ReadLinear(roughnessPath, size);
        var m = metallic.GetPixels32();
        var r = roughness.GetPixels32();
        var packed = new Color32[m.Length];
        for (int i = 0; i < m.Length; i++) packed[i] = new Color32(m[i].r, m[i].r, m[i].r, (byte)(255 - r[i].r));
        var output = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        output.SetPixels32(packed);
        output.Apply();
        File.WriteAllBytes(outPath, output.EncodeToPNG());
        Object.DestroyImmediate(metallic);
        Object.DestroyImmediate(roughness);
        Object.DestroyImmediate(output);

        AssetDatabase.ImportAsset(outPath);
        if (AssetImporter.GetAtPath(outPath) is TextureImporter importer)
        {
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
        }
    }

    /// <summary>A texture file decoded straight from disk (no import settings involved), resampled to size x size.</summary>
    static Texture2D ReadLinear(string assetPath, int size)
    {
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        source.LoadImage(File.ReadAllBytes(assetPath));
        var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(source, rt);
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        var result = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        result.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        Object.DestroyImmediate(source);
        return result;
    }
}
