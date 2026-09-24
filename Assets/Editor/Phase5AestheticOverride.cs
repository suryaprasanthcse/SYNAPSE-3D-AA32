using System.IO;
using System.Linq;
using MAAYAI.Swarm;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The aesthetic pass for Phase5_TrojanHorse: make the map read as moulded plaster standing on a real desk,
/// matching Gemini_Generated_game_image.png.
///
/// The central change is that the blockout stops being lit by the swarm and starts being lit for real. The void
/// model - every surface lit only by VPL points - cannot ground anything, because point lights with no occlusion
/// produce no contact shadow, and contact is the whole of what makes virtual geometry sit on a table. So the
/// blockout moves to URP/Lit under a warm key light, and the swarm goes back to being what it looks like in the
/// reference: a cloud of distinct glowing beads, not the thing illuminating the world.
///
/// Refuses to run on any scene but the fork, so the stable build cannot be restyled by accident.
/// </summary>
public static class Phase5AestheticOverride
{
    const string ForkPath = "Assets/Scenes/Phase5_TrojanHorse.unity";
    const string MaterialFolder = "Assets/MatrixPrefabs/Materials";
    const string WorldRootName = "Luminara_World";

    // Reference-matched framing. The arch reads about 45 cm tall against a 9 cm mug, and the walls run off both
    // edges of frame - so the desk holds ONE ACT, not the whole 134 m map. Fitting the entire map is what made
    // the world look like a tabletop model instead of architecture.
    const float FramedSpanMetres = 40f;
    const float FramedWidthMetres = 26f;
    const float PetaloDesignMetres = 4f;

    static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ID_Smoothness = Shader.PropertyToID("_Smoothness");
    static readonly int ID_Metallic = Shader.PropertyToID("_Metallic");

    [MenuItem("Funobotz/Apply Phase 5 Aesthetic Override")]
    public static void Apply()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != ForkPath)
        {
            Debug.LogError($"[Aesthetic] Active scene is '{scene.path}', not the fork. Open {ForkPath} first - " +
                           "this deliberately will not restyle the stable scene.");
            return;
        }

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Phase 5 Aesthetic Override");

        RefineBlockout();
        AddKeyLight();
        SetEnvironmentLighting();
        ConfigurePostProcessing();
        ClampSwarm();
        MatchReferenceScale();
        BuildHeader();
        AddShadowCatcher();

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[Aesthetic] Override applied to Phase5_TrojanHorse and saved.");
    }

    // ==============================================================================================
    // 1. Real PBR plaster
    // ==============================================================================================
    static void RefineBlockout()
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            Debug.LogError("[Aesthetic] URP/Lit not found.");
            return;
        }

        const string path = MaterialFolder + "/Blockout_Plaster.mat";
        var plaster = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (plaster == null)
        {
            Directory.CreateDirectory(MaterialFolder);
            plaster = new Material(lit) { name = "Blockout_Plaster" };
            AssetDatabase.CreateAsset(plaster, path);
        }

        // Swapping the shader on the existing asset keeps every renderer's reference intact.
        plaster.shader = lit;
        plaster.SetColor(ID_BaseColor, new Color(0.9412f, 0.9412f, 0.9412f, 1f));   // #F0F0F0
        plaster.SetFloat(ID_Smoothness, 0.15f);
        plaster.SetFloat(ID_Metallic, 0f);
        EditorUtility.SetDirty(plaster);

        var world = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == WorldRootName);
        if (world == null)
        {
            Debug.LogError("[Aesthetic] No Luminara_World in the fork; run the Phase 5 injector first.");
            return;
        }

        int count = 0;
        foreach (var renderer in world.GetComponentsInChildren<MeshRenderer>(true))
        {
            Undo.RecordObject(renderer, "Refine blockout");
            renderer.sharedMaterial = plaster;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            // URP/Lit takes its ambient from the environment, so these must be back on - they were disabled
            // when nothing but the swarm could light the geometry.
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            EditorUtility.SetDirty(renderer);
            count++;
        }
        Debug.Log($"[Aesthetic] {count} pieces -> URP/Lit plaster #F0F0F0, smoothness 0.15, metallic 0, casting + receiving.");
    }

    // ==============================================================================================
    // 2. Warm desk-lamp key
    // ==============================================================================================
    static void AddKeyLight()
    {
        var existing = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(l => l.name == "Key_Light");

        GameObject go;
        if (existing != null) go = existing.gameObject;
        else
        {
            go = new GameObject("Key_Light");
            Undo.RegisterCreatedObjectUndo(go, "Create key light");
        }

        var light = go.GetComponent<Light>() ?? Undo.AddComponent<Light>(go);
        Undo.RecordObject(light, "Tune key light");
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.8196f, 0.6431f, 1f);     // #FFD1A4 warm tungsten
        light.intensity = 1.5f;
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.75f;
        // Table scale: the defaults detach a centimetre-scale shadow from its caster entirely.
        light.shadowBias = 0.01f;
        light.shadowNormalBias = 0.02f;
        light.shadowNearPlane = 0.02f;
        go.transform.rotation = Quaternion.Euler(52f, 214f, 0f);
        EditorUtility.SetDirty(light);
        Debug.Log("[Aesthetic] Key light: #FFD1A4, intensity 1.5, soft shadows.");
    }

    /// <summary>
    /// URP/Lit reads ambient from the environment, and this scene has no skybox - it is an AR camera feed. With
    /// the default black ambient every shadowed face of the plaster would render pure black, which is exactly the
    /// failure the swarm-lit version had.
    /// </summary>
    static void SetEnvironmentLighting()
    {
        // Not wrapped in Undo: the lighting settings object behind RenderSettings is internal, and these values
        // are saved with the scene regardless.
        // Measured, not guessed. At the first pass these were LDR values and the ambient probe resolved to an
        // irradiance of ~0.05 against a key of ~1.13 - a 4% fill, which crushes every shadowed face to black and
        // lets the warm key tint the plaster tan. Ambient colours are HDR, so they are scaled to land the fill at
        // roughly a fifth of the key: enough to hold detail in shadow and to cool the off-white back toward
        // #F0F0F0 against a saturated tungsten key.
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.84f, 0.88f, 1.00f);     // cool from above
        RenderSettings.ambientEquatorColor = new Color(0.68f, 0.66f, 0.64f);
        RenderSettings.ambientGroundColor = new Color(0.44f, 0.38f, 0.32f);  // warm bounce off the desk
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = null;

        // Ambient reaches objects as spherical harmonics, and that projection is only recomputed when the
        // environment is refreshed. Without this the values above are stored but never applied, and every face
        // turned away from the key light renders black - which is the same "lit by exactly one thing" failure the
        // swarm-only version had, just with a different single light.
        DynamicGI.UpdateEnvironment();
        Debug.Log("[Aesthetic] Ambient set to trilight and environment refreshed, so shadowed plaster is not black.");
    }

    /// <summary>
    /// Adds a tonemapper, which the project has never had.
    ///
    /// This is the actual reason the swarm "nukes the exposure". HDR is enabled, so the swarm's emission and
    /// Petalo's core legitimately produce values above 1.0 - but with no tonemapping in the volume, everything
    /// above 1.0 is clipped flat to white and then fed to Bloom, which smears that clipped white over the frame.
    /// Lowering the emission only hides it; anything bright enough to matter would clip again.
    ///
    /// Neutral rather than ACES on purpose: this composites over a live camera feed, and ACES would grade the
    /// player's real desk along with the virtual geometry.
    /// </summary>
    static void ConfigurePostProcessing()
    {
        var volume = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault(v => v.isGlobal && v.sharedProfile != null);
        if (volume == null || volume.sharedProfile == null)
        {
            Debug.LogWarning("[Aesthetic] No global volume with a profile; tonemapping not applied.");
            return;
        }

        var profile = volume.sharedProfile;

        if (!profile.TryGet(out Tonemapping tonemapping))
            tonemapping = profile.Add<Tonemapping>(true);
        tonemapping.active = true;
        tonemapping.mode.overrideState = true;
        tonemapping.mode.value = TonemappingMode.Neutral;

        if (profile.TryGet(out Bloom bloom))
        {
            // Only things genuinely above white should bloom - the beads and Petalo's core, not lit plaster.
            bloom.active = true;
            bloom.threshold.overrideState = true;
            bloom.threshold.value = 1.05f;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = 0.55f;
            bloom.scatter.overrideState = true;
            bloom.scatter.value = 0.62f;
        }

        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssetIfDirty(profile);
        Debug.Log("[Aesthetic] Tonemapping (Neutral) added to " + profile.name + "; bloom threshold raised to 1.05.");
    }

    // ==============================================================================================
    // 3. Stop the swarm nuking the exposure
    // ==============================================================================================
    static void ClampSwarm()
    {
        var swarm = Object.FindAnyObjectByType<SwarmGPUArchitect>(FindObjectsInactive.Include);
        if (swarm != null && swarm.SwarmMaterial != null)
        {
            var m = swarm.SwarmMaterial;
            Undo.RecordObject(m, "Clamp swarm emission");

            // These were HDR values chosen to light a pitch-black void. With a real key light in the scene they
            // are several stops above everything else in frame and bloom straight to white.
            m.SetColor("_ColorSlow", new Color(0.12f, 0.62f, 1.00f));
            m.SetColor("_ColorFast", new Color(1.00f, 0.72f, 0.35f));
            m.SetFloat("_EmissionIntensity", 0.85f);
            m.SetFloat("_EmissionSpeedBoost", 0.9f);
            m.SetFloat("_BeadShape", 0.95f);      // hard-edged beads, as in the reference
            m.SetFloat("_BeadCore", 0.55f);
            m.SetFloat("_BeadRim", 0.55f);
            EditorUtility.SetDirty(m);
            Debug.Log("[Aesthetic] Swarm emission clamped to LDR beads (was HDR void-lighting values).");
        }

        if (swarm != null)
        {
            var so = new SerializedObject(swarm);
            // The VPL grid no longer has to light the world, so its contribution drops to a tint.
            so.FindProperty("lightIntensity").floatValue = 0.5f;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(swarm);
            Debug.Log("[Aesthetic] VPL contribution 2.2 -> 0.5.");
        }

        // Petalo stays bright: in the reference it is the one thing genuinely glowing, and it is warm, not cyan.
        var beacon = Object.FindAnyObjectByType<PetaloBeacon>(FindObjectsInactive.Include);
        if (beacon != null)
        {
            var so = new SerializedObject(beacon);
            so.FindProperty("coreColor").colorValue = new Color(2.40f, 1.55f, 0.55f);
            so.FindProperty("alignedColor").colorValue = new Color(3.00f, 1.90f, 0.50f);
            so.FindProperty("beaconIntensity").floatValue = 2f;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(beacon);
        }
    }

    // ==============================================================================================
    // 4. Reference framing: one act fills the desk
    // ==============================================================================================
    static void MatchReferenceScale()
    {
        var world = Object.FindAnyObjectByType<PlaneAnchoredWorld>(FindObjectsInactive.Include);
        if (world != null)
        {
            var so = new SerializedObject(world);
            so.FindProperty("worldSpanMetres").floatValue = FramedSpanMetres;
            so.FindProperty("worldWidthMetres").floatValue = FramedWidthMetres;
            // A single act at desk scale needs a much larger fit than the whole map did.
            so.FindProperty("scaleRange").vector2Value = new Vector2(0.004f, 0.12f);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(world);
        }

        var beacon = Object.FindAnyObjectByType<PetaloBeacon>(FindObjectsInactive.Include);
        if (beacon != null)
        {
            var so = new SerializedObject(beacon);
            so.FindProperty("size").floatValue = PetaloDesignMetres;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(beacon);
        }

        float fit = 1.2f * 0.9f / FramedSpanMetres;     // a ~1.2 m desk
        Debug.Log($"[Aesthetic] Framing one act ({FramedSpanMetres} m) to the surface. On a 1.2 m desk that is " +
                  $"1:{1f / fit:0}, giving a {14f * fit * 100f:0} cm arch and a {PetaloDesignMetres * fit * 100f:0} cm Petalo.");
    }

    // ==============================================================================================
    // 5. Sleek header
    // ==============================================================================================
    static void BuildHeader()
    {
        var scene = SceneManager.GetActiveScene();
        var existing = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "UI_Header");
        if (existing != null) Undo.DestroyObjectImmediate(existing);

        var canvasGo = new GameObject("UI_Header", typeof(RectTransform), typeof(Canvas),
                                      typeof(CanvasScaler), typeof(GraphicRaycaster));
        Undo.RegisterCreatedObjectUndo(canvasGo, "Create header canvas");

        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.matchWidthOrHeight = 0.5f;

        var panelGo = new GameObject("Header_Panel", typeof(RectTransform), typeof(Image),
                                     typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        panelGo.transform.SetParent(canvasGo.transform, false);

        var panel = panelGo.GetComponent<RectTransform>();
        panel.anchorMin = new Vector2(0.5f, 1f);
        panel.anchorMax = new Vector2(0.5f, 1f);
        panel.pivot = new Vector2(0.5f, 1f);
        panel.anchoredPosition = new Vector2(0f, -6f);      // hard against the top edge, as in the reference

        // 40% alpha: the header sits over the camera feed and must not block it.
        panelGo.GetComponent<Image>().color = new Color(0.05f, 0.05f, 0.06f, 0.40f);

        var layout = panelGo.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(22, 22, 7, 8);
        layout.spacing = 1f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;

        var fitter = panelGo.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var title = MakeLabel(panelGo.transform, "Title", 26, FontStyle.Normal,
                              new Color(0.97f, 0.97f, 0.98f), "SPATIAL MATRIX   30 FPS");
        var mission = MakeLabel(panelGo.transform, "Mission", 24, FontStyle.Normal,
                                new Color(0.90f, 0.91f, 0.94f),
                                "MISSION: Route Swarm → <color=#FFC64D>IGNITE</color>");

        var header = Undo.AddComponent<MissionHeader>(canvasGo);
        var so = new SerializedObject(header);
        so.FindProperty("titleLine").objectReferenceValue = title;
        so.FindProperty("missionLine").objectReferenceValue = mission;
        so.ApplyModifiedProperties();

        var zone = Object.FindAnyObjectByType<AnomalyZone>(FindObjectsInactive.Include);
        if (zone != null)
        {
            // Strip first. This rebuilds the header every run, and a persistent listener added each time leaves
            // the previous ones pointing at a destroyed object - they accumulate as dead entries on the event.
            for (int i = zone.entered.GetPersistentEventCount() - 1; i >= 0; i--)
                UnityEditor.Events.UnityEventTools.RemovePersistentListener(zone.entered, i);

            UnityEditor.Events.UnityEventTools.AddPersistentListener(zone.entered, header.SetCompleted);
            EditorUtility.SetDirty(zone);
        }

        Debug.Log("[Aesthetic] Header: 26/24 pt, 40% alpha, flush to the top edge.");
    }

    static Text MakeLabel(Transform parent, string name, int size, FontStyle style, Color colour, string content)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);

        var text = go.GetComponent<Text>();
        // The built-in font, so this needs no imported TMP essentials to render on device.
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = size;
        text.fontStyle = style;
        text.color = colour;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = true;
        text.raycastTarget = false;
        text.text = content;
        return text;
    }

    static void AddShadowCatcher()
    {
        var origin = Object.FindObjectsByType<XROrigin>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .FirstOrDefault();
        if (origin == null) return;

        if (origin.GetComponent<ARShadowCatcher>() == null)
        {
            Undo.AddComponent<ARShadowCatcher>(origin.gameObject);
            Debug.Log("[Aesthetic] AR shadow catcher present: the blockout darkens the real desk.");
        }

        // Anchors are what make "locked" mean locked to the ROOM rather than to Unity's origin. Without this the
        // world sits at fixed coordinates in a frame the session keeps re-estimating, so it swims with the device.
        if (origin.GetComponent<UnityEngine.XR.ARFoundation.ARAnchorManager>() == null)
        {
            Undo.AddComponent<UnityEngine.XR.ARFoundation.ARAnchorManager>(origin.gameObject);
            Debug.Log("[Aesthetic] ARAnchorManager added: the lock can now pin the world to the real table.");
        }
    }
}
