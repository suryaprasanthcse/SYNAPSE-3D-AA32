using System;
using MAAYAI.Matrix.Swarm;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
#endif

/// <summary>
/// Anchors the GPU boid swarm (the Mystery Cave) to one tracked reference image. Self-wiring:
/// it attaches itself to the scene's XR Origin at startup and resolves the swarm prefab from
/// Resources, so no Inspector setup is required.
/// </summary>
/// <remarks>
/// Swarm source, in order: the serialized prefab, then <c>Resources/BoidComputeManager</c>,
/// then the three serialized raw assets. Use <c>Matrix &gt; Auto-Wire AR Boids</c> to generate
/// the Resources prefab and bake every reference into the scene.
///
/// One swarm exists at most. It is created when the anchor image is detected, paused (no
/// dispatches, buffers retained) while the image is not actively tracked, and destroyed when
/// the image is removed or this component is destroyed. SwarmManager.OnDestroy releases every
/// GraphicsBuffer and its per-instance compute shader, so destroying the host is the complete
/// teardown path.
/// </remarks>
[RequireComponent(typeof(XROrigin))]
[RequireComponent(typeof(ARTrackedImageManager))]
[DisallowMultipleComponent]
[AddComponentMenu("XR/AR Matrix/AR Boid Bridge")]
public sealed class ARBoidBridge : MonoBehaviour
{
    /// <summary>Resources path of the generated swarm prefab (no extension).</summary>
    public const string ResourcesPrefabPath = "BoidComputeManager";

    // CSBuildGrid and CSSwarmUpdate each bind five structured buffers; the render shader reads one in its vertex stage.
    const int k_RequiredComputeBuffers = 5;
    const int k_RequiredVertexBuffers = 1;
    const string k_HostName = "Boid Compute Manager";

    /// <summary>
    /// SwarmManager ships tuned for a 50 m volume. These values fit a tabletop cave above a playing card.
    /// Applied before the swarm's first frame, so buffers and the grid are only ever sized for this profile.
    /// </summary>
    [Serializable]
    public struct MobileProfile
    {
        [Min(1)] public int boidCount;
        public Vector3 boundsExtents;
        [Min(0f)] public float spawnRadius;
        [Min(0.0001f)] public float boidScale;
        [Min(0f)] public float minSpeed;
        [Min(0f)] public float maxSpeed;
        [Min(0f)] public float maxSteerForce;
        [Min(0f)] public float separationRadius;
        [Min(0f)] public float alignmentRadius;
        [Min(0f)] public float cohesionRadius;
        [Min(0)] public int maxNeighbours;

        // Ratios mirror SwarmManager's defaults; the grid comes out at 8x6x8 = 384 cells.
        public static MobileProfile Tabletop => new()
        {
            boidCount = 1024,
            boundsExtents = new Vector3(0.15f, 0.08f, 0.15f),
            spawnRadius = 0.06f,
            boidScale = 0.006f,
            minSpeed = 0.04f,
            maxSpeed = 0.1f,
            maxSteerForce = 0.07f,
            separationRadius = 0.015f,
            alignmentRadius = 0.036f,
            cohesionRadius = 0.05f,
            maxNeighbours = 24,
        };
    }

    [Header("Anchor")]
    [Tooltip("Reference image that anchors the swarm. Case, spaces and underscores are ignored when matching.")]
    [SerializeField] string m_AnchorImageName = "king of spades";

    [Tooltip("Lift the simulation volume by its half-height so it sits on the card instead of half below it.")]
    [SerializeField] bool m_RestVolumeOnCard = true;

    [Header("Swarm Source (auto-resolved)")]
    [Tooltip("Pre-configured SwarmManager prefab. Resolved from Resources/" + ResourcesPrefabPath + " when empty.")]
    [SerializeField] SwarmManager m_SwarmPrefab;
    [SerializeField] ComputeShader m_SwarmCompute;
    [SerializeField] Mesh m_BoidMesh;
    [SerializeField] Material m_BoidMaterial;

    [Header("Mobile")]
    [SerializeField] bool m_ApplyMobileProfile = true;
    [SerializeField] MobileProfile m_Profile = MobileProfile.Tabletop;

    /// <summary>The live swarm, or null while no anchor card is tracked.</summary>
    public SwarmManager ActiveSwarm => m_Swarm;

    /// <summary>The reference image this bridge anchors the swarm to.</summary>
    public string AnchorImageName => m_AnchorImageName;

    /// <summary>Raised after a swarm is created and configured, and when it is destroyed (null).</summary>
    public event Action<SwarmManager> SwarmChanged;

    ARTrackedImageManager m_Manager;
    ARMultiMatrixPayload m_Router;
    Camera m_ARCamera;
    SwarmManager m_Swarm;
    TrackableId m_AnchorId = TrackableId.invalidId;
    string m_AnchorKey;
    bool m_Supported;
    bool m_Subscribed;

    // Attaches the bridge to the XR Origin in any loaded scene that lacks one, provided a swarm
    // source ships in Resources. Scenes without AR image tracking are left untouched.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var manager = Object.FindAnyObjectByType<ARTrackedImageManager>();
        if (manager == null || manager.GetComponent<ARBoidBridge>() != null)
            return;

        if (Resources.Load<SwarmManager>(ResourcesPrefabPath) == null)
            return;

        manager.gameObject.AddComponent<ARBoidBridge>();
        Debug.Log($"{nameof(ARBoidBridge)} auto-attached to '{manager.name}'.", manager);
    }

    void Awake()
    {
        m_Manager = GetComponent<ARTrackedImageManager>();
        m_ARCamera = GetComponent<XROrigin>().Camera;
        m_Router = GetComponent<ARMultiMatrixPayload>();
        m_AnchorKey = Normalize(m_AnchorImageName);

        ResolveSwarmSource();
        m_Supported = CheckSupport();

        if (m_Supported && m_Router != null && m_Router.enabled)
            Debug.Log($"{nameof(ARBoidBridge)}: {nameof(ARMultiMatrixPayload)} is also active on '{name}'; any hologram it maps to '{m_AnchorImageName}' will render inside the swarm.", this);
    }

    void OnEnable()
    {
        if (!m_Supported)
            return;

        m_Manager.trackablesChanged.AddListener(OnTrackablesChanged);
        m_Subscribed = true;

        // When attached late (bootstrap or re-enable), the anchor may already be tracked and
        // will never raise another "added" event, so adopt it from the live collection.
        if (m_Swarm == null)
        {
            foreach (var image in m_Manager.trackables)
            {
                if (TryAdopt(image))
                    break;
            }
        }
    }

    void OnDisable()
    {
        if (m_Subscribed)
        {
            m_Manager.trackablesChanged.RemoveListener(OnTrackablesChanged);
            m_Subscribed = false;
        }

        SetSwarmRunning(false);
    }

    void OnDestroy() => DespawnSwarm();

    // GLES can drop GPU buffer contents across a context loss while backgrounded, so the swarm
    // is rebuilt from a fresh seed on resume rather than trusting stale state.
    void OnApplicationPause(bool paused)
    {
        if (paused || m_Swarm == null)
            return;

        var anchor = m_Swarm.transform.parent;
        var wasRunning = m_Swarm.gameObject.activeSelf;
        DespawnSwarm(keepAnchor: true);
        if (anchor != null)
        {
            SpawnSwarm(anchor);
            SetSwarmRunning(wasRunning);
        }
    }

    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
    {
        foreach (var image in changes.added)
            TryAdopt(image);

        foreach (var image in changes.updated)
        {
            if (image.trackableId == m_AnchorId)
                SetSwarmRunning(image.trackingState == TrackingState.Tracking);
        }

        foreach (var removed in changes.removed)
        {
            if (removed.Key == m_AnchorId)
                DespawnSwarm();
        }
    }

    bool TryAdopt(ARTrackedImage image)
    {
        if (m_Swarm != null || !IsAnchor(image))
            return false;

        m_AnchorId = image.trackableId;
        SpawnSwarm(image.transform);
        SetSwarmRunning(image.trackingState == TrackingState.Tracking);
        return true;
    }

    bool IsAnchor(ARTrackedImage image) => Normalize(image.referenceImage.name) == m_AnchorKey;

    static string Normalize(string imageName) =>
        string.IsNullOrEmpty(imageName) ? string.Empty : imageName.Trim().Replace('_', ' ').ToLowerInvariant();

    void ResolveSwarmSource()
    {
        if (m_SwarmPrefab == null)
            m_SwarmPrefab = Resources.Load<SwarmManager>(ResourcesPrefabPath);
    }

    bool CheckSupport()
    {
        if (!SystemInfo.supportsComputeShaders)
            return Unsupported("compute shaders are unavailable");

        if (SystemInfo.maxComputeBufferInputsCompute < k_RequiredComputeBuffers)
            return Unsupported($"compute stage allows {SystemInfo.maxComputeBufferInputsCompute} buffers, needs {k_RequiredComputeBuffers}");

        // Common on Mali under OpenGL ES; Vulkan does not have this limit.
        if (SystemInfo.maxComputeBufferInputsVertex < k_RequiredVertexBuffers)
            return Unsupported($"vertex stage allows {SystemInfo.maxComputeBufferInputsVertex} buffers on {SystemInfo.graphicsDeviceType}, needs {k_RequiredVertexBuffers}");

        if (m_SwarmPrefab == null && (m_SwarmCompute == null || m_BoidMesh == null || m_BoidMaterial == null))
            return Unsupported($"no swarm source; run Matrix > Auto-Wire AR Boids to generate Resources/{ResourcesPrefabPath}");

        var material = m_SwarmPrefab != null ? m_SwarmPrefab.boidMaterial : m_BoidMaterial;
        if (material == null)
            return Unsupported("the swarm prefab has no boid material");
        if (!material.shader.isSupported)
            return Unsupported($"shader '{material.shader.name}' is not supported on {SystemInfo.graphicsDeviceName}");

        return true;
    }

    bool Unsupported(string reason)
    {
        Debug.LogWarning($"{nameof(ARBoidBridge)} disabled: {reason}.", this);
        return false;
    }

    void SpawnSwarm(Transform anchor)
    {
        if (m_SwarmPrefab != null)
        {
            // SwarmManager has no Awake/OnEnable and its Start runs after this callback returns,
            // so everything configured below lands before the first buffer allocation.
            m_Swarm = Instantiate(m_SwarmPrefab, anchor, false);
        }
        else
        {
            var go = new GameObject(k_HostName);
            go.SetActive(false);
            go.transform.SetParent(anchor, false);
            m_Swarm = go.AddComponent<SwarmManager>();
            m_Swarm.swarmCompute = m_SwarmCompute;
            m_Swarm.boidMesh = m_BoidMesh;
            m_Swarm.boidMaterial = m_BoidMaterial;
        }

        var host = m_Swarm.gameObject;
        host.name = k_HostName;
        host.layer = anchor.gameObject.layer;

        // Exactly on the anchor. The simulation volume is world-axis-aligned around this
        // position, so only a vertical lift is needed to keep it above the card.
        var lift = m_RestVolumeOnCard && m_ApplyMobileProfile ? Mathf.Abs(m_Profile.boundsExtents.y) : 0f;
        host.transform.localPosition = new Vector3(0f, lift, 0f);
        host.transform.localRotation = Quaternion.identity;
        host.transform.localScale = Vector3.one;

        if (m_ApplyMobileProfile)
            ApplyProfile(m_Swarm);

        // Shadows double the draw cost and AR scenes rarely have a receiver for them.
        m_Swarm.castShadows = ShadowCastingMode.Off;
        m_Swarm.receiveShadows = false;
        m_Swarm.renderInAllCameras = false;
        m_Swarm.renderCamera = m_ARCamera;
        m_Swarm.randomSeed = Environment.TickCount;

        SwarmChanged?.Invoke(m_Swarm);
    }

    void ApplyProfile(SwarmManager swarm)
    {
        swarm.boidCount = m_Profile.boidCount;
        swarm.boundsExtents = m_Profile.boundsExtents;
        swarm.spawnRadius = m_Profile.spawnRadius;
        swarm.boidScale = m_Profile.boidScale;
        swarm.minSpeed = m_Profile.minSpeed;
        swarm.maxSpeed = m_Profile.maxSpeed;
        swarm.maxSteerForce = m_Profile.maxSteerForce;
        swarm.separationRadius = m_Profile.separationRadius;
        swarm.alignmentRadius = m_Profile.alignmentRadius;
        swarm.cohesionRadius = m_Profile.cohesionRadius;
        swarm.maxNeighbours = m_Profile.maxNeighbours;
    }

    // Deactivating skips SwarmManager.Update, so no compute dispatch or draw is issued while
    // the card is lost. Buffers stay allocated (about 0.2 MB at the default profile) to avoid
    // reallocation churn every time tracking flickers.
    void SetSwarmRunning(bool running)
    {
        if (m_Swarm != null && m_Swarm.gameObject.activeSelf != running)
            m_Swarm.gameObject.SetActive(running);
    }

    void DespawnSwarm(bool keepAnchor = false)
    {
        var had = m_Swarm != null;
        if (had)
            Destroy(m_Swarm.gameObject);

        m_Swarm = null;
        if (!keepAnchor)
            m_AnchorId = TrackableId.invalidId;

        if (had)
            SwarmChanged?.Invoke(null);
    }

#if UNITY_EDITOR
    const string k_AutoWireMenu = "Matrix/Auto-Wire AR Boids";
    const string k_OriginName = "XR Origin (Mobile AR)";
    const string k_ResourcesFolder = "Assets/BoidSwarmEngine/Resources";
    const string k_PrefabAssetPath = k_ResourcesFolder + "/" + ResourcesPrefabPath + ".prefab";

    void Reset()
    {
        m_Profile = MobileProfile.Tabletop;
        ResolveEditorReferences();
    }

    void OnValidate() => ResolveEditorReferences();

    void ResolveEditorReferences()
    {
        if (m_SwarmPrefab == null)
            m_SwarmPrefab = AssetDatabase.LoadAssetAtPath<SwarmManager>(k_PrefabAssetPath);
        if (m_SwarmCompute == null)
            m_SwarmCompute = FindAsset<ComputeShader>("SwarmCompute");
        if (m_BoidMesh == null)
            m_BoidMesh = FindAsset<Mesh>("SM_BoidPyramid");
        if (m_BoidMaterial == null)
            m_BoidMaterial = FindAsset<Material>("M_SwarmBoid");
    }

    static T FindAsset<T>(string assetName) where T : Object =>
        AssetDatabase.FindAssets($"{assetName} t:{typeof(T).Name}")
            .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
            .Where(path => Path.GetFileNameWithoutExtension(path) == assetName)
            .Select(path => AssetDatabase.LoadAssetAtPath<T>(path))
            .FirstOrDefault(asset => asset != null);

    [MenuItem(k_AutoWireMenu, validate = true)]
    static bool ValidateAutoWire() => !EditorApplication.isPlayingOrWillChangePlaymode;

    /// <summary>
    /// Generates Resources/BoidComputeManager.prefab, attaches the bridge to the XR Origin in the
    /// active scene, bakes every reference, and saves.
    /// </summary>
    [MenuItem(k_AutoWireMenu)]
    static void AutoWire()
    {
        const string log = "[" + nameof(ARBoidBridge) + "]";

        var compute = FindAsset<ComputeShader>("SwarmCompute");
        var mesh = FindAsset<Mesh>("SM_BoidPyramid");
        var material = FindAsset<Material>("M_SwarmBoid");
        if (compute == null || mesh == null || material == null)
        {
            Debug.LogError($"{log} BoidSwarmEngine assets missing (compute: {compute != null}, mesh: {mesh != null}, material: {material != null}). Re-import the package.");
            return;
        }

        var scene = SceneManager.GetActiveScene();
        var origin = FindOrigin(scene);
        if (origin == null)
        {
            Debug.LogError($"{log} No XR Origin in '{scene.name}'. Expected '{k_OriginName}'.");
            return;
        }

        var prefab = SaveSwarmPrefab(compute, mesh, material);
        if (prefab == null)
            return;

        // RequireComponent adds ARTrackedImageManager if the origin lacks one.
        var bridge = origin.GetComponent<ARBoidBridge>();
        if (bridge == null)
            bridge = Undo.AddComponent<ARBoidBridge>(origin.gameObject);

        var so = new SerializedObject(bridge);
        so.FindProperty(nameof(m_SwarmPrefab)).objectReferenceValue = prefab;
        so.FindProperty(nameof(m_SwarmCompute)).objectReferenceValue = compute;
        so.FindProperty(nameof(m_BoidMesh)).objectReferenceValue = mesh;
        so.FindProperty(nameof(m_BoidMaterial)).objectReferenceValue = material;
        so.ApplyModifiedProperties();

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
        {
            Debug.LogError($"{log} Failed to save '{scene.path}'.");
            return;
        }

        Selection.activeGameObject = origin.gameObject;
        Debug.Log($"{log} Wired '{origin.name}' with {k_PrefabAssetPath} and saved '{scene.path}'.", bridge);
    }

    static XROrigin FindOrigin(Scene scene)
    {
        var origins = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<XROrigin>(true))
            .ToList();

        return origins.FirstOrDefault(o => o.name == k_OriginName) ?? origins.FirstOrDefault();
    }

    // Re-running overwrites in place, so the prefab keeps its GUID and existing references stay valid.
    static SwarmManager SaveSwarmPrefab(ComputeShader compute, Mesh mesh, Material material)
    {
        if (!AssetDatabase.IsValidFolder(k_ResourcesFolder))
            AssetDatabase.CreateFolder(Path.GetDirectoryName(k_ResourcesFolder)!.Replace('\\', '/'), Path.GetFileName(k_ResourcesFolder));

        var temp = new GameObject(k_HostName);
        try
        {
            var swarm = temp.AddComponent<SwarmManager>();
            swarm.swarmCompute = compute;
            swarm.boidMesh = mesh;
            swarm.boidMaterial = material;
            swarm.castShadows = ShadowCastingMode.Off;
            swarm.receiveShadows = false;

            var saved = PrefabUtility.SaveAsPrefabAsset(temp, k_PrefabAssetPath, out var success);
            if (!success)
            {
                Debug.LogError($"[{nameof(ARBoidBridge)}] Failed to save {k_PrefabAssetPath}.");
                return null;
            }

            return saved.GetComponent<SwarmManager>();
        }
        finally
        {
            Object.DestroyImmediate(temp);
        }
    }
#endif
}
