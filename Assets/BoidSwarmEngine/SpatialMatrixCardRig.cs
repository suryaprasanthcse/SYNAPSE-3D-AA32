using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The spatial matrix's bridge from AR tracking to swarm-lit geometry.
    ///  - Every tracked card gets a virtual card: a quad sized to the card's physical size, textured with its
    ///    reference art (library "keep texture" is on) and shaded by SwarmSurface, so in the void the art is
    ///    revealed only by the swarm's VPLs. Hidden while the card is not tracked (GDD: never a fail state).
    ///  - Every detected plane gets a mesh visual (the table / floor) so the swarm can paint it into view.
    ///  - The anchor card (King of Spades) can drive SwarmGPUArchitect's pose follow, but only while
    ///    driveSwarmAnchor is on. Once Petalo owns the swarm that must be off: the swarm has exactly one anchor,
    ///    and two writers produce a cloud that snaps between the card and the avatar every frame.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XROrigin))]
    public sealed class SpatialMatrixCardRig : MonoBehaviour
    {
        const string SurfaceShaderName = "MAAYAI/SwarmSurface";

        [SerializeField] ARTrackedImageManager trackedImageManager;
        [SerializeField] ARPlaneManager planeManager;
        [SerializeField] RuntimeVoidEnforcer voidEnforcer;
        [SerializeField] SwarmGPUArchitect swarm;
        [Tooltip("Runtime safety net: bound to the manager if its reference library is missing.")]
        [SerializeField] XRReferenceImageLibrary referenceLibraryFallback;

        [Header("Anchor")]
        [Tooltip("Reference image name the swarm anchors to (case-insensitive).")]
        [SerializeField] string anchorImageName = "king of spades";
        [Tooltip("OFF once the swarm belongs to Petalo instead of a card. Two components writing the swarm's " +
                 "anchor fight every frame: this one writes it from a trackable event during Update, and the " +
                 "swarm reads it in its own Update, so whichever ran last that frame won and the cloud snapped " +
                 "back and forth between the card and Petalo.")]
        [SerializeField] bool driveSwarmAnchor = true;

        [Header("Virtual Cards")]
        [Tooltip("Lift (m) above the tracked image plane.")]
        [SerializeField] float cardLift = 0.0005f;
        [SerializeField] Color cardTint = Color.white;
        [SerializeField, Range(0f, 1f)] float cardSmoothness = 0.45f;
        [SerializeField, Range(0f, 1f)] float cardSpecular = 0.25f;

        [Tooltip("The printed card is already in the camera feed. ON: the proxy adds only the swarm's light to it " +
                 "(hands and card stay visible). OFF: it draws the artwork opaquely, occluding the real world.")]
        [SerializeField] bool cardsAreLightReceivers = true;
        [Tooltip("How much swarm light the real card appears to catch. The feed already shows the card, so this is " +
                 "a tint, not full re-lighting.")]
        [SerializeField, Range(0f, 2f)] float cardReceiverGain = 0.45f;

        [Header("Planes")]
        [SerializeField] bool buildPlaneVisuals = true;

        static readonly int ID_BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int ID_Smoothness = Shader.PropertyToID("_Smoothness");
        static readonly int ID_SpecularStrength = Shader.PropertyToID("_SpecularStrength");
        static readonly int ID_Cull = Shader.PropertyToID("_Cull");
        static readonly int ID_RenderMode = Shader.PropertyToID("_RenderMode");
        static readonly int ID_SrcBlend = Shader.PropertyToID("_SrcBlend");
        static readonly int ID_DstBlend = Shader.PropertyToID("_DstBlend");
        static readonly int ID_ZWrite = Shader.PropertyToID("_ZWrite");
        static readonly int ID_ReceiverGain = Shader.PropertyToID("_ReceiverGain");

        static Mesh s_UnitCardMesh;

        Shader surfaceShader;
        readonly Dictionary<TrackableId, GameObject> cardProxies = new();
        readonly Dictionary<string, Material> cardMaterials = new();

        public int TrackedCardCount { get; private set; }
        public int VisibleCardCount { get; private set; }
        public int PlaneCount => planeManager != null ? planeManager.trackables.count : 0;
        public bool AnchorTracked { get; private set; }

        void Awake()
        {
            if (trackedImageManager == null) trackedImageManager = GetComponent<ARTrackedImageManager>();
            if (trackedImageManager == null) trackedImageManager = FindAnyObjectByType<ARTrackedImageManager>();
            if (planeManager == null) planeManager = GetComponent<ARPlaneManager>();
            if (planeManager == null) planeManager = FindAnyObjectByType<ARPlaneManager>();
            if (voidEnforcer == null) voidEnforcer = GetComponent<RuntimeVoidEnforcer>();
            if (voidEnforcer == null) voidEnforcer = FindAnyObjectByType<RuntimeVoidEnforcer>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmGPUArchitect>();

            if (trackedImageManager == null)
                Debug.LogError("[SpatialMatrixCardRig] No ARTrackedImageManager: cards can never appear or anchor the swarm.", this);
            else if (trackedImageManager.referenceLibrary == null)
            {
                if (referenceLibraryFallback != null)
                {
                    trackedImageManager.referenceLibrary = referenceLibraryFallback;
                    Debug.LogWarning($"[SpatialMatrixCardRig] Manager had no reference library: bound " +
                                     $"'{referenceLibraryFallback.name}' at runtime.", this);
                }
                else
                {
                    Debug.LogError("[SpatialMatrixCardRig] ARTrackedImageManager has no reference library and no fallback.", this);
                }
            }
            if (swarm == null && driveSwarmAnchor)
                Debug.LogWarning("[SpatialMatrixCardRig] No SwarmGPUArchitect: cards will render but stay dark.", this);

            SwarmLightingGlobals.EnsureBound();
            surfaceShader = Shader.Find(SurfaceShaderName);
            if (surfaceShader == null)
                Debug.LogError($"[SpatialMatrixCardRig] '{SurfaceShaderName}' not found (Always Included Shaders).", this);
        }

        void OnEnable()
        {
            if (trackedImageManager != null)
            {
                trackedImageManager.trackablesChanged.AddListener(OnImagesChanged);
                foreach (var image in trackedImageManager.trackables) Upsert(image);
            }

            if (planeManager != null)
            {
                planeManager.trackablesChanged.AddListener(OnPlanesChanged);
                foreach (var plane in planeManager.trackables) EnsurePlaneVisual(plane);
            }
        }

        void OnDisable()
        {
            if (trackedImageManager != null) trackedImageManager.trackablesChanged.RemoveListener(OnImagesChanged);
            if (planeManager != null) planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
        }

        void OnDestroy()
        {
            foreach (var material in cardMaterials.Values)
                if (material != null) Destroy(material);
            cardMaterials.Clear();
        }

        // ------------------------------------------------------------------------------------------
        // Cards
        // ------------------------------------------------------------------------------------------
        void OnImagesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
        {
            foreach (var image in args.added) Upsert(image);
            foreach (var image in args.updated) Upsert(image);

            foreach (var pair in args.removed)
            {
                if (cardProxies.TryGetValue(pair.Key, out var proxy) && proxy != null) Destroy(proxy);
                cardProxies.Remove(pair.Key);
                if (driveSwarmAnchor && pair.Value != null && IsAnchor(pair.Value) && swarm != null)
                    swarm.DetachAnchor();
            }

            RefreshCounts();
        }

        void Upsert(ARTrackedImage image)
        {
            if (image == null) return;

            bool tracking = image.trackingState == TrackingState.Tracking;

            if (!cardProxies.TryGetValue(image.trackableId, out var proxy) || proxy == null)
            {
                proxy = CreateCardProxy(image);
                cardProxies[image.trackableId] = proxy;
            }

            if (proxy != null)
            {
                // Physical size can be refined by the tracker; the unit mesh scales to it.
                Vector2 size = image.size.sqrMagnitude > 1e-8f ? image.size : image.referenceImage.size;
                proxy.transform.localScale = new Vector3(size.x, 1f, size.y);
                if (proxy.activeSelf != tracking) proxy.SetActive(tracking);
            }

            if (driveSwarmAnchor && IsAnchor(image) && swarm != null)
            {
                swarm.AttachToAnchor(image.transform);
                swarm.SetAnchorTracking(tracking);
            }

            RefreshCounts();
        }

        GameObject CreateCardProxy(ARTrackedImage image)
        {
            if (surfaceShader == null) return null;

            string imageName = image.referenceImage.name;
            var proxy = new GameObject($"VirtualCard_{imageName}");
            proxy.transform.SetParent(image.transform, false);
            proxy.transform.localPosition = new Vector3(0f, cardLift, 0f);
            proxy.transform.localRotation = Quaternion.identity;

            proxy.AddComponent<MeshFilter>().sharedMesh = GetUnitCardMesh();
            var renderer = proxy.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = GetCardMaterial(image.referenceImage);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            if (image.referenceImage.texture == null)
                Debug.LogWarning($"[SpatialMatrixCardRig] '{imageName}' has no runtime texture: enable 'Keep Texture at Runtime' " +
                                 "in the reference library. Card renders as flat albedo.", this);
            return proxy;
        }

        Material GetCardMaterial(XRReferenceImage reference)
        {
            string key = reference.name ?? string.Empty;
            if (cardMaterials.TryGetValue(key, out var cached) && cached != null) return cached;

            var material = new Material(surfaceShader) { name = $"SwarmSurface_Card_{key}" };
            if (reference.texture != null) material.SetTexture(ID_BaseMap, reference.texture);
            material.SetColor(ID_BaseColor, cardTint);
            material.SetFloat(ID_Smoothness, cardSmoothness);
            material.SetFloat(ID_SpecularStrength, cardSpecular);
            material.SetFloat(ID_Cull, (float)UnityEngine.Rendering.CullMode.Off);

            if (cardsAreLightReceivers)
            {
                // Additive, no depth write, drawn after opaques: the real card and the player's hands stay visible
                // through it and only the swarm's light lands on them.
                material.SetFloat(ID_RenderMode, 1f);
                material.SetFloat(ID_SrcBlend, (float)UnityEngine.Rendering.BlendMode.One);
                material.SetFloat(ID_DstBlend, (float)UnityEngine.Rendering.BlendMode.One);
                material.SetFloat(ID_ZWrite, 0f);
                material.SetFloat(ID_ReceiverGain, cardReceiverGain);
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else
            {
                material.SetFloat(ID_RenderMode, 0f);
                material.SetFloat(ID_SrcBlend, (float)UnityEngine.Rendering.BlendMode.One);
                material.SetFloat(ID_DstBlend, (float)UnityEngine.Rendering.BlendMode.Zero);
                material.SetFloat(ID_ZWrite, 1f);
            }
            cardMaterials[key] = material;
            return material;
        }

        bool IsAnchor(ARTrackedImage image) =>
            string.Equals(image.referenceImage.name?.Trim(), anchorImageName.Trim(), System.StringComparison.OrdinalIgnoreCase);

        void RefreshCounts()
        {
            int tracked = 0, visible = 0;
            bool anchorTracked = false;
            if (trackedImageManager != null)
            {
                foreach (var image in trackedImageManager.trackables)
                {
                    tracked++;
                    bool isTracking = image.trackingState == TrackingState.Tracking;
                    if (isTracking) visible++;
                    if (isTracking && IsAnchor(image)) anchorTracked = true;
                }
            }

            TrackedCardCount = tracked;
            VisibleCardCount = visible;
            AnchorTracked = anchorTracked;
        }

        // Unit quad in the image's XZ plane (+Y = image normal), UV v along +Z (image top), both faces lit by
        // SwarmSurface (Cull Off). Scaled per card to its physical width (x) and height (z).
        static Mesh GetUnitCardMesh()
        {
            if (s_UnitCardMesh != null) return s_UnitCardMesh;

            s_UnitCardMesh = new Mesh { name = "VirtualCardQuad" };
            s_UnitCardMesh.SetVertices(new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f)
            });
            s_UnitCardMesh.SetNormals(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            s_UnitCardMesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            s_UnitCardMesh.SetTriangles(new[] { 0, 3, 2, 0, 2, 1 }, 0);
            s_UnitCardMesh.RecalculateBounds();
            return s_UnitCardMesh;
        }

        // ------------------------------------------------------------------------------------------
        // Planes: give AR planes a mesh visual so the swarm can reveal the table / floor.
        // ------------------------------------------------------------------------------------------
        void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (var plane in args.added) EnsurePlaneVisual(plane);
        }

        void EnsurePlaneVisual(ARPlane plane)
        {
            if (!buildPlaneVisuals || plane == null) return;
            if (plane.GetComponent<ARPlaneMeshVisualizer>() != null) return;

            if (plane.GetComponent<MeshFilter>() == null) plane.gameObject.AddComponent<MeshFilter>();
            if (plane.GetComponent<MeshRenderer>() == null) plane.gameObject.AddComponent<MeshRenderer>();
            plane.gameObject.AddComponent<ARPlaneMeshVisualizer>();

            // Material immediately: never draw a frame with a missing (magenta) material.
            if (voidEnforcer != null) voidEnforcer.EnforceNow(plane);
        }
    }
}
