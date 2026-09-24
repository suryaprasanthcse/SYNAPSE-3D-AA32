using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = System.Random;

/// <summary>
/// Builds the Phase 2 narrative diorama: Petalo, the light-signaling diagnostic drone, inside the
/// Mystery Cave, the Aethyra subterranean engineering node (Theme #2 Adventure Quest World,
/// Category #4 Engineering Puzzle Game).
/// Idempotent: re-running rebuilds the scene contents and updates generated assets in place.
/// </summary>
public static class FunobotzDioramaBuilder
{
    public const string ScenePath = "Assets/Scenes/FunobotzDioramas.unity";
    public const string DioramaPrefix = "Diorama_";
    public const string ShotPointName = "ShotPoint";
    public const string FocalCharacter = "Petalo";
    public const string EnvironmentName = "Aethyra_MysteryCave_Node";
    public const string FocusName = "Focus";

    const string LogPrefix = "[FunobotzDioramaBuilder]";
    const string GeneratedPrefix = "Funobotz_";
    const string RootDir = "Assets/Funobotz";
    const string MaterialDir = RootDir + "/Materials";
    const string MeshDir = RootDir + "/Meshes";
    const string TextureDir = RootDir + "/Textures";
    const string SettingsDir = RootDir + "/Settings";
    const string SoftDotPath = TextureDir + "/Funobotz_SoftDot.png";
    const string PostFxPath = SettingsDir + "/Funobotz_PostFX.asset";

    const string LitShader = "Universal Render Pipeline/Lit";
    const string UnlitShader = "Universal Render Pipeline/Unlit";
    const string ParticleShader = "Universal Render Pipeline/Particles/Unlit";

    // The cutout threshold. Petalo's body sits at alpha 192-254 with a faint 1-15 haze around it,
    // so clipping at 0.5 yields a fully opaque character with no halo.
    const float AlphaCutoff = 0.5f;
    const float DioramaSpacing = 40f;
    const float WallTop = 10f;
    const float ShotFieldOfView = 32f;
    const float ShotAspect = 16f / 9f;

    static readonly Color Cyan = new(0.1f, 0.85f, 1f);
    static readonly Color Gold = new(1f, 0.7f, 0.2f);

    readonly struct Character
    {
        public readonly string Name;
        public readonly string TextureName;
        public readonly float Height;   // meters, of the opaque content after cropping
        public readonly Color Key;
        public readonly Color Accent;

        public Character(string name, string textureName, float height, Color key, Color accent)
        {
            Name = name;
            TextureName = textureName;
            Height = height;
            Key = key;
            Accent = accent;
        }
    }

    static readonly Character[] Characters =
    {
        new(FocalCharacter, "petalo_transparent", 1.8f, Cyan, Gold),
    };

    sealed class SharedMaterials
    {
        public Material Wall;
        public Material Floor;
        public Material GlowCyan;
        public Material GlowGold;
        public Material Mote;

        public Material Glow(Color color) => color == Cyan ? GlowCyan : GlowGold;
    }

    [MenuItem("Funobotz/Build Dioramas", priority = 0)]
    public static void BuildMenu()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            Build();
    }

    [MenuItem("Funobotz/Build Dioramas + Capture Beauty Shots", priority = 2)]
    public static void BuildAndCaptureMenu()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo() && Build())
            FunobotzBeautyShots.Capture();
    }

    [MenuItem("Funobotz/Build Dioramas", validate = true)]
    [MenuItem("Funobotz/Build Dioramas + Capture Beauty Shots", validate = true)]
    static bool ValidateNotPlaying() => !EditorApplication.isPlayingOrWillChangePlaymode;

    /// <summary>Builds everything and saves the scene. Returns false if a required asset is missing.</summary>
    public static bool Build()
    {
        try
        {
            EditorUtility.DisplayProgressBar("Funobotz", "Configuring textures", 0.05f);
            EnsureFolder(MaterialDir);
            EnsureFolder(MeshDir);
            EnsureFolder(TextureDir);
            EnsureFolder(SettingsDir);

            var textures = new Texture2D[Characters.Length];
            for (var i = 0; i < Characters.Length; i++)
            {
                textures[i] = ConfigureCutoutTexture(Characters[i].TextureName);
                if (textures[i] == null)
                    return false;
            }

            EditorUtility.DisplayProgressBar("Funobotz", "Preparing scene", 0.25f);
            var shared = CreateSharedMaterials();
            if (shared == null)
                return false;

            var scene = OpenOrCreateScene();
            ClearGenerated(scene);
            RemoveDirectionalLights(scene);
            ConfigureEnvironment();
            var camera = EnsureCamera(scene);
            EnsurePostProcessing();

            Transform firstShot = null;
            for (var i = 0; i < Characters.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Funobotz", $"Building {Characters[i].Name}'s diorama", 0.3f + 0.6f * i / Characters.Length);
                var shot = BuildDiorama(i, Characters[i], textures[i], shared);
                if (shot == null)
                    return false;
                firstShot ??= shot;
            }

            camera.transform.SetPositionAndRotation(firstShot.position, firstShot.rotation);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError($"{LogPrefix} Failed to save {ScenePath}.");
                return false;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"{LogPrefix} Built {FocalCharacter}'s {EnvironmentName} diorama in {ScenePath}.");
            return true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ------------------------------------------------------------------ Step 1: textures

    static Texture2D ConfigureCutoutTexture(string textureName)
    {
        var path = AssetDatabase.FindAssets($"{textureName} t:Texture2D")
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == textureName);

        if (path == null || AssetImporter.GetAtPath(path) is not TextureImporter importer)
        {
            Debug.LogError($"{LogPrefix} Texture '{textureName}' not found.");
            return null;
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        settings.spriteAlignment = (int)SpriteAlignment.BottomCenter;
        importer.SetTextureSettings(settings);

        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;          // dilates color under clear pixels: no dark fringe
        importer.sRGBTexture = true;
        importer.mipmapEnabled = true;
        importer.mipMapsPreserveCoverage = true;      // keeps cutout silhouettes solid in distant mips
        importer.alphaTestReferenceValue = AlphaCutoff;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Trilinear;
        importer.anisoLevel = 4;
        importer.maxTextureSize = 2048;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>
    /// UV rect of the pixels that survive the cutout, read from the source PNG so the texture
    /// does not need to be CPU-readable. Cropping puts each character's feet exactly on the floor.
    /// </summary>
    static Rect OpaqueUvRect(Texture2D texture, out float aspect)
    {
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!source.LoadImage(File.ReadAllBytes(AssetDatabase.GetAssetPath(texture))))
            {
                aspect = (float)texture.width / texture.height;
                return new Rect(0f, 0f, 1f, 1f);
            }

            int w = source.width, h = source.height;
            var pixels = source.GetPixels32();
            var threshold = (byte)(AlphaCutoff * 255f);
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (var y = 0; y < h; y++)
            {
                var row = y * w;
                for (var x = 0; x < w; x++)
                {
                    if (pixels[row + x].a < threshold)
                        continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0)
            {
                aspect = (float)w / h;
                return new Rect(0f, 0f, 1f, 1f);
            }

            const int pad = 2;
            minX = Mathf.Max(0, minX - pad);
            minY = Mathf.Max(0, minY - pad);
            maxX = Mathf.Min(w - 1, maxX + pad);
            maxY = Mathf.Min(h - 1, maxY + pad);

            var pw = maxX - minX + 1;
            var ph = maxY - minY + 1;
            aspect = (float)pw / ph;
            return new Rect((float)minX / w, (float)minY / h, (float)pw / w, (float)ph / h);
        }
        finally
        {
            Object.DestroyImmediate(source);
        }
    }

    // Pivot at bottom-center, visible face -Z (matching Unity's Quad winding).
    static Mesh SaveCardMesh(string characterName, Rect uv, float height, float aspect)
    {
        var path = $"{MeshDir}/{characterName}_Card.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        var isNew = mesh == null;
        if (isNew)
            mesh = new Mesh();

        var halfWidth = height * aspect * 0.5f;
        mesh.Clear();
        mesh.name = $"{characterName}_Card";
        mesh.vertices = new[]
        {
            new Vector3(-halfWidth, 0f, 0f),
            new Vector3(halfWidth, 0f, 0f),
            new Vector3(-halfWidth, height, 0f),
            new Vector3(halfWidth, height, 0f),
        };
        mesh.uv = new[]
        {
            new Vector2(uv.xMin, uv.yMin),
            new Vector2(uv.xMax, uv.yMin),
            new Vector2(uv.xMin, uv.yMax),
            new Vector2(uv.xMax, uv.yMax),
        };
        mesh.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
        mesh.triangles = new[] { 0, 3, 1, 3, 0, 2 };
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();

        if (isNew)
            AssetDatabase.CreateAsset(mesh, path);
        else
            EditorUtility.SetDirty(mesh);
        return mesh;
    }

    // ------------------------------------------------------------------ Materials

    static SharedMaterials CreateSharedMaterials()
    {
        if (Shader.Find(LitShader) == null || Shader.Find(UnlitShader) == null || Shader.Find(ParticleShader) == null)
        {
            Debug.LogError($"{LogPrefix} URP shaders not found. Is the Universal Render Pipeline active?");
            return null;
        }

        // Pure black albedo would absorb all diffuse light and leave only specular glints, so the
        // "pitch black" surfaces keep a sliver of albedo plus wet-stone smoothness to catch the lights.
        var wall = LoadOrCreateMaterial("Funobotz_PitchBlack_Wall", LitShader);
        wall.SetColor("_BaseColor", new Color(0.018f, 0.018f, 0.022f));
        wall.SetFloat("_Metallic", 0f);
        wall.SetFloat("_Smoothness", 0.45f);
        FinalizeMaterial(wall, surface: 0f);

        var floor = LoadOrCreateMaterial("Funobotz_PitchBlack_Floor", LitShader);
        floor.SetColor("_BaseColor", new Color(0.012f, 0.012f, 0.015f));
        floor.SetFloat("_Metallic", 0f);
        floor.SetFloat("_Smoothness", 0.8f);
        FinalizeMaterial(floor, surface: 0f);

        var mote = LoadOrCreateMaterial("Funobotz_Mote", ParticleShader);
        mote.SetTexture("_BaseMap", GetOrCreateSoftDot());
        mote.SetColor("_BaseColor", Color.white);
        mote.SetFloat("_Blend", 2f);   // BaseShaderGUI.BlendMode.Additive
        mote.SetFloat("_ZWrite", 0f);
        FinalizeMaterial(mote, surface: 1f);

        return new SharedMaterials
        {
            Wall = wall,
            Floor = floor,
            GlowCyan = CreateGlowMaterial("Funobotz_Glow_Cyan", Cyan),
            GlowGold = CreateGlowMaterial("Funobotz_Glow_Gold", Gold),
            Mote = mote,
        };
    }

    static Material CreateGlowMaterial(string materialName, Color color)
    {
        var glow = LoadOrCreateMaterial(materialName, LitShader);
        glow.SetColor("_BaseColor", Color.black);
        glow.SetFloat("_Smoothness", 0f);
        // HDR above the bloom threshold so the orbs visibly glow.
        glow.SetColor("_EmissionColor", color * 8f);
        glow.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        FinalizeMaterial(glow, surface: 0f);
        return glow;
    }

    static Material CreateCutoutMaterial(string characterName, Texture2D texture)
    {
        var material = LoadOrCreateMaterial($"{characterName}_Cutout", UnlitShader);
        material.SetTexture("_BaseMap", texture);
        material.SetColor("_BaseColor", Color.white);
        material.SetFloat("_AlphaClip", 1f);
        material.SetFloat("_Cutoff", AlphaCutoff);
        material.SetFloat("_Cull", 0f);   // two-sided, so an off-axis viewer never sees a culled card
        FinalizeMaterial(material, surface: 0f);
        return material;
    }

    static Material LoadOrCreateMaterial(string materialName, string shaderName)
    {
        var path = $"{MaterialDir}/{materialName}.mat";
        var shader = Shader.Find(shaderName);
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader) { name = materialName };
            AssetDatabase.CreateAsset(material, path);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }

        return material;
    }

    // URP's own inspector routine: derives keywords, blend state and render queue from the properties.
    static void FinalizeMaterial(Material material, float surface)
    {
        material.SetFloat("_Surface", surface);
        BaseShaderGUI.SetMaterialKeywords(material);
        EditorUtility.SetDirty(material);
    }

    static Texture2D GetOrCreateSoftDot()
    {
        if (!File.Exists(SoftDotPath))
        {
            const int size = 64;
            var dot = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[size * size];
                var center = (size - 1) * 0.5f;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var d = Mathf.Clamp01(Vector2.Distance(new Vector2(x, y), new Vector2(center, center)) / center);
                    var a = Mathf.Pow(1f - d, 2.2f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }

                dot.SetPixels32(pixels);
                dot.Apply();
                File.WriteAllBytes(SoftDotPath, dot.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(dot);
            }

            AssetDatabase.ImportAsset(SoftDotPath);
            if (AssetImporter.GetAtPath(SoftDotPath) is TextureImporter importer)
            {
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(SoftDotPath);
    }

    // ------------------------------------------------------------------ Scene, lights, environment

    // A dedicated scene: the active AR scene must not get walls around its origin or lose its lights.
    static Scene OpenOrCreateScene()
    {
        if (File.Exists(ScenePath))
            return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        EnsureFolder(Path.GetDirectoryName(ScenePath)!.Replace('\\', '/'));
        EditorSceneManager.SaveScene(scene, ScenePath);
        return scene;
    }

    static void ClearGenerated(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name.StartsWith(DioramaPrefix) || root.name.StartsWith(GeneratedPrefix))
                Object.DestroyImmediate(root);
        }
    }

    static void RemoveDirectionalLights(Scene scene)
    {
        var removed = 0;
        foreach (var light in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Light>(true)).ToList())
        {
            if (light.type != LightType.Directional)
                continue;

            // URP's UniversalAdditionalLightData requires the Light, so it has to go first.
            var lightOnly = light.transform.childCount == 0 && light.GetComponents<Component>()
                .All(c => c is Transform || c is Light || c is UniversalAdditionalLightData);
            if (lightOnly)
            {
                Object.DestroyImmediate(light.gameObject);
            }
            else
            {
                var additional = light.GetComponent<UniversalAdditionalLightData>();
                if (additional != null)
                    Object.DestroyImmediate(additional);
                Object.DestroyImmediate(light);
            }
            removed++;
        }

        RenderSettings.sun = null;
        if (removed > 0)
            Debug.Log($"{LogPrefix} Removed {removed} directional light(s).");
    }

    static void ConfigureEnvironment()
    {
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.006f, 0.008f, 0.014f);
        RenderSettings.reflectionIntensity = 0f;
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = Color.black;
        RenderSettings.fogDensity = 0.03f;
    }

    static Camera EnsureCamera(Scene scene)
    {
        var camera = scene.GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<Camera>(true))
            .OrderByDescending(c => c.CompareTag("MainCamera"))
            .FirstOrDefault();

        if (camera == null)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            camera = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }

        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.allowHDR = true;
        camera.fieldOfView = ShotFieldOfView;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 200f;

        var data = camera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        data.antialiasingQuality = AntialiasingQuality.High;
        return camera;
    }

    static void EnsurePostProcessing()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(PostFxPath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, PostFxPath);
        }

        // Threshold above 1 so only the HDR glow orbs bloom, not the white paper robots.
        var bloom = GetOrAdd<Bloom>(profile);
        bloom.threshold.Override(1.1f);
        bloom.intensity.Override(1.4f);
        bloom.scatter.Override(0.75f);
        bloom.highQualityFiltering.Override(true);

        // Neutral keeps the white cards white; ACES would grey them.
        GetOrAdd<Tonemapping>(profile).mode.Override(TonemappingMode.Neutral);

        var vignette = GetOrAdd<Vignette>(profile);
        vignette.color.Override(Color.black);
        vignette.intensity.Override(0.38f);
        vignette.smoothness.Override(0.45f);

        EditorUtility.SetDirty(profile);

        var go = new GameObject(GeneratedPrefix + "PostFX");
        var volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.sharedProfile = profile;
    }

    static T GetOrAdd<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (profile.TryGet(out T component))
        {
            component.active = true;
            return component;
        }

        component = profile.Add<T>(true);
        component.name = typeof(T).Name;
        AssetDatabase.AddObjectToAsset(component, profile);
        return component;
    }

    // ------------------------------------------------------------------ Dioramas

    static Transform BuildDiorama(int index, Character character, Texture2D texture, SharedMaterials shared)
    {
        var root = new GameObject(DioramaPrefix + character.Name).transform;
        root.position = new Vector3(index * DioramaSpacing, 0f, 0f);
        var rng = new Random(1000 + index);

        // Environment shell: the Aethyra cave chamber.
        var env = CreateChild(root, EnvironmentName, Vector3.zero);
        var wallHeight = WallTop;
        var wallY = WallTop * 0.5f;
        Block(env, "Floor", new Vector3(0f, -0.1f, 0f), new Vector3(18f, 0.2f, 18f), shared.Floor);
        Block(env, "Wall_Back", new Vector3(0f, wallY, 7f), new Vector3(18f, wallHeight, 1.5f), shared.Wall);
        Block(env, "Wall_Left", new Vector3(-8f, wallY, 0f), new Vector3(1.5f, wallHeight, 18f), shared.Wall);
        Block(env, "Wall_Right", new Vector3(8f, wallY, 0f), new Vector3(1.5f, wallHeight, 18f), shared.Wall);

        BuildCave(env, rng, shared.Wall);

        // Character cutout
        var uv = OpaqueUvRect(texture, out var aspect);
        var mesh = SaveCardMesh(character.Name, uv, character.Height, aspect);
        var card = new GameObject(character.Name);
        card.transform.SetParent(root, false);
        card.AddComponent<MeshFilter>().sharedMesh = mesh;
        var cardRenderer = card.AddComponent<MeshRenderer>();
        cardRenderer.sharedMaterial = CreateCutoutMaterial(character.Name, texture);
        cardRenderer.shadowCastingMode = ShadowCastingMode.Off;
        cardRenderer.receiveShadows = false;
        var billboard = card.AddComponent<FunobotzBillboard>();

        // Focus and a framed shot for the capture tool.
        var focus = CreateChild(root, FocusName, new Vector3(0f, character.Height * 0.55f, 0f));
        var width = character.Height * aspect;
        var halfFovV = ShotFieldOfView * 0.5f * Mathf.Deg2Rad;
        var halfFovH = Mathf.Atan(Mathf.Tan(halfFovV) * ShotAspect);
        var distance = Mathf.Max(0.8f * character.Height / Mathf.Tan(halfFovV), 0.8f * width / Mathf.Tan(halfFovH));
        var shot = CreateChild(root, ShotPointName, Vector3.zero);
        shot.position = focus.position + new Vector3(0.22f, 0.1f, -1f).normalized * distance;
        shot.LookAt(focus);
        billboard.Face(shot.position);

        // Key light exactly at the character's focal point: a halo on floor and walls around the unlit card.
        CreatePointLight(root, "Light_Key", focus.localPosition, character.Key, 3.5f, 9f, LightShadows.Soft);

        // Glowing lanterns behind the character throw colored light onto the back wall.
        CreatePointLight(root, "Light_Accent_A", new Vector3(-2.6f, 2.2f, 3.2f), character.Accent, 5f, 8f, LightShadows.None, shared.Glow(character.Accent));
        CreatePointLight(root, "Light_Accent_B", new Vector3(2.8f, 1.4f, 4f), character.Key, 4f, 7f, LightShadows.None, shared.Glow(character.Key));

        CreateMotes(root, character, shared.Mote, index);
        return shot;
    }

    static void BuildCave(Transform env, Random rng, Material wall)
    {
        Block(env, "Ceiling", new Vector3(0f, 8.5f, 0f), new Vector3(18f, 1.5f, 18f), wall);

        for (var i = 0; i < 16; i++)
        {
            var h = Range(rng, 1f, 3.5f);
            var w = Range(rng, 0.3f, 0.9f);
            var pos = new Vector3(Range(rng, -6.5f, 6.5f), 7.75f - h * 0.5f, Range(rng, -2f, 6f));
            Block(env, $"Stalactite_{i}", pos, new Vector3(w, h, w), wall, Range(rng, 0f, 90f));
        }

        for (var i = 0; i < 8; i++)
        {
            var s = Range(rng, 0.6f, 1.8f);
            var pos = RingPoint(rng, 2.2f, 6.5f);
            Block(env, $"Boulder_{i}", new Vector3(pos.x, s * 0.4f, pos.y), new Vector3(s, s * 0.8f, s), wall,
                Range(rng, 0f, 90f), Range(rng, -12f, 12f));
        }
    }

    static Light CreatePointLight(Transform root, string lightName, Vector3 localPosition, Color color,
        float intensity, float range, LightShadows shadows, Material glow = null)
    {
        var go = new GameObject(lightName);
        go.transform.SetParent(root, false);
        go.transform.localPosition = localPosition;

        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
        light.shadows = shadows;
        light.renderMode = LightRenderMode.ForcePixel;

        if (glow != null)
        {
            var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            orb.name = "Glow_Orb";
            Object.DestroyImmediate(orb.GetComponent<Collider>());
            orb.transform.SetParent(go.transform, false);
            orb.transform.localScale = Vector3.one * 0.16f;
            var r = orb.GetComponent<MeshRenderer>();
            r.sharedMaterial = glow;
            // The light sits inside the orb; letting it cast shadows would black out the light.
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        return light;
    }

    static void CreateMotes(Transform root, Character character, Material material, int index)
    {
        var go = new GameObject("Ambient_Motes");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, 2.2f, 2.4f);   // behind the card

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ps.useAutoRandomSeed = false;       // reproducible beauty shots
        ps.randomSeed = (uint)(4242 + index);

        var main = ps.main;
        main.duration = 10f;
        main.loop = true;
        main.prewarm = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 12f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.12f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.07f);
        main.startColor = new ParticleSystem.MinMaxGradient(character.Key * 0.9f, character.Accent * 0.9f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = 350;

        var emission = ps.emission;
        emission.rateOverTime = 30f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(7f, 4f, 3f);

        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        colorOverLifetime.color = fade;

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.15f;
        noise.frequency = 0.3f;
        noise.scrollSpeed = 0.1f;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        ps.Play();
    }

    // ------------------------------------------------------------------ Helpers

    static Transform CreateChild(Transform parent, string childName, Vector3 localPosition)
    {
        var child = new GameObject(childName).transform;
        child.SetParent(parent, false);
        child.localPosition = localPosition;
        return child;
    }

    static void Block(Transform parent, string blockName, Vector3 localPosition, Vector3 size, Material material,
        float yaw = 0f, float pitch = 0f, float roll = 0f)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = blockName;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;
        go.transform.localRotation = Quaternion.Euler(pitch, yaw, roll);
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = material;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
    }

    static float Range(Random rng, float min, float max) => min + (float)rng.NextDouble() * (max - min);

    // A point on the floor between two radii, kept out of the camera lane in front of the character.
    static Vector2 RingPoint(Random rng, float minRadius, float maxRadius)
    {
        for (var i = 0; i < 50; i++)
        {
            var angle = Range(rng, 0f, Mathf.PI * 2f);
            var p = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * Range(rng, minRadius, maxRadius);
            if (!(p.y < 0.5f && Mathf.Abs(p.x) < 2.8f))
                return p;
        }

        return new Vector2(maxRadius, maxRadius * 0.5f);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        var parent = Path.GetDirectoryName(path)!.Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
