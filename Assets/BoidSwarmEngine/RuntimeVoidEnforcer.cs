using System.Collections.Generic;
using Unity.XR.CoreUtils;
using Unity.XR.CoreUtils.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Moves every AR Foundation trackable (planes, tracked images and anything spawned under them) onto the
    /// SwarmSurface shader the moment it is created. Original artwork (base map, tiling, tint, alpha cut-out) is
    /// preserved, so cards reveal their art only where the swarm's VPLs light them. The void comes from the
    /// absence of light, not from black albedo.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XROrigin))]
    public sealed class RuntimeVoidEnforcer : MonoBehaviour
    {
        const string SurfaceShaderName = "MAAYAI/SwarmSurface";

        [SerializeField] ARPlaneManager planeManager;
        [SerializeField] ARTrackedImageManager trackedImageManager;

        [Header("Planes")]
        [Tooltip("Planes use a neutral albedo instead of AR Foundation's debug texture.")]
        [SerializeField] bool preservePlaneTextures;
        [SerializeField] Color planeAlbedo = new(0.3f, 0.3f, 0.3f, 1f);
        [Tooltip("AR plane boundary lines are unlit debug visuals that would glow in the void.")]
        [SerializeField] bool hidePlaneBoundaryLines = true;

        [Header("Surface Response")]
        [SerializeField, Range(0f, 1f)] float diffuseWrap = 0.15f;
        [SerializeField, Range(0f, 1f)] float smoothness = 0.35f;
        [SerializeField, Range(0f, 1f)] float specularStrength = 0.2f;

        [Header("Camera Feed")]
        [Tooltip("XR Simulation paints the simulated environment as the camera feed; it ignores scene lighting.")]
        [SerializeField] bool suppressCameraFeedInEditor;
        [Tooltip("Removes the real-world passthrough entirely. OFF = the player sees their desk and hands while every virtual surface stays lit only by the swarm.")]
        [SerializeField] bool suppressCameraFeedOnDevice;

        static readonly int ID_BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int ID_MainTex = Shader.PropertyToID("_MainTex");
        static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int ID_Color = Shader.PropertyToID("_Color");
        static readonly int ID_AlphaClip = Shader.PropertyToID("_AlphaClip");
        static readonly int ID_Cutoff = Shader.PropertyToID("_Cutoff");
        static readonly int ID_Surface = Shader.PropertyToID("_Surface");
        static readonly int ID_Wrap = Shader.PropertyToID("_Wrap");
        static readonly int ID_Smoothness = Shader.PropertyToID("_Smoothness");
        static readonly int ID_SpecularStrength = Shader.PropertyToID("_SpecularStrength");
        static readonly int ID_Cull = Shader.PropertyToID("_Cull");

        Shader surfaceShader;
        Material planeMaterial;

        // One SwarmSurface material per distinct source material: artwork is preserved without creating an
        // instance per renderer (keeps SRP Batcher batches intact).
        readonly Dictionary<Material, Material> convertedMaterials = new();
        readonly HashSet<Material> ownedMaterials = new();

        // Renderer count per trackable: 'updated' events catch children spawned after 'added' (e.g. payload
        // holograms under a tracked image) without re-walking every hierarchy every frame.
        readonly Dictionary<TrackableId, int> enforcedRendererCounts = new();
        readonly List<Renderer> rendererScratch = new();

        void Awake()
        {
            // Explicit Unity null checks: GetComponent returns a fake-null object in the Editor, which '??' ignores.
            if (planeManager == null) planeManager = GetComponent<ARPlaneManager>();
            if (planeManager == null) planeManager = FindAnyObjectByType<ARPlaneManager>();
            if (trackedImageManager == null) trackedImageManager = GetComponent<ARTrackedImageManager>();
            if (trackedImageManager == null) trackedImageManager = FindAnyObjectByType<ARTrackedImageManager>();

            if (planeManager == null && trackedImageManager == null)
                Debug.LogError("[RuntimeVoidEnforcer] No ARPlaneManager or ARTrackedImageManager in the scene: nothing to enforce.", this);

            SwarmLightingGlobals.EnsureBound();

            surfaceShader = Shader.Find(SurfaceShaderName);
            if (surfaceShader == null)
                Debug.LogError($"[RuntimeVoidEnforcer] '{SurfaceShaderName}' not found (add it to Always Included Shaders).", this);
        }

        void OnEnable()
        {
            if (planeManager != null)
            {
                planeManager.trackablesChanged.AddListener(OnPlanesChanged);
                foreach (var plane in planeManager.trackables) Enforce(plane);
            }

            if (trackedImageManager != null)
            {
                trackedImageManager.trackablesChanged.AddListener(OnTrackedImagesChanged);
                foreach (var image in trackedImageManager.trackables) Enforce(image);
            }

            ApplyCameraFeedPolicy();
        }

        void OnDisable()
        {
            if (planeManager != null) planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
            if (trackedImageManager != null) trackedImageManager.trackablesChanged.RemoveListener(OnTrackedImagesChanged);
        }

        void OnDestroy()
        {
            foreach (var material in ownedMaterials)
                if (material != null) Destroy(material);
            ownedMaterials.Clear();
            convertedMaterials.Clear();
        }

        void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args) => Process(args.added, args.updated, args.removed);

        void OnTrackedImagesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args) => Process(args.added, args.updated, args.removed);

        void Process<T>(ReadOnlyList<T> added, ReadOnlyList<T> updated, ReadOnlyList<KeyValuePair<TrackableId, T>> removed)
            where T : ARTrackable
        {
            foreach (var trackable in added) Enforce(trackable);

            foreach (var trackable in updated)
            {
                trackable.GetComponentsInChildren(true, rendererScratch);
                if (!enforcedRendererCounts.TryGetValue(trackable.trackableId, out int count) || count != rendererScratch.Count)
                    Enforce(trackable);
            }

            foreach (var pair in removed) enforcedRendererCounts.Remove(pair.Key);
        }

        /// <summary>Apply SwarmSurface to a trackable right now (e.g. after renderers were added to it).</summary>
        public void EnforceNow(ARTrackable trackable) => Enforce(trackable);

        void Enforce(ARTrackable trackable)
        {
            if (trackable == null || surfaceShader == null) return;

            bool isPlane = trackable is ARPlane;
            trackable.GetComponentsInChildren(true, rendererScratch);

            foreach (var renderer in rendererScratch)
            {
                if (renderer is LineRenderer)
                {
                    if (isPlane && hidePlaneBoundaryLines) renderer.enabled = false;
                    continue;
                }

                if (renderer is not MeshRenderer && renderer is not SkinnedMeshRenderer) continue;

                var source = renderer.sharedMaterials;
                int n = Mathf.Max(1, source.Length);
                var replacement = new Material[n];
                for (int i = 0; i < n; i++)
                {
                    Material src = i < source.Length ? source[i] : null;
                    replacement[i] = isPlane && !preservePlaneTextures ? GetPlaneMaterial() : Convert(src);
                }
                renderer.sharedMaterials = replacement;

                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            enforcedRendererCounts[trackable.trackableId] = rendererScratch.Count;
        }

        Material GetPlaneMaterial()
        {
            if (planeMaterial != null) return planeMaterial;

            planeMaterial = CreateSurfaceMaterial("SwarmSurface_Plane");
            planeMaterial.SetColor(ID_BaseColor, planeAlbedo);
            return planeMaterial;
        }

        Material Convert(Material source)
        {
            if (source == null) return GetPlaneMaterial();
            if (source.shader == surfaceShader) return source;                  // already converted
            if (convertedMaterials.TryGetValue(source, out var cached) && cached != null) return cached;

            var material = CreateSurfaceMaterial($"SwarmSurface_{source.name}");

            // Artwork: URP (_BaseMap) first, then legacy / unlit (_MainTex).
            int texId = source.HasProperty(ID_BaseMap) && source.GetTexture(ID_BaseMap) != null ? ID_BaseMap
                      : source.HasProperty(ID_MainTex) ? ID_MainTex
                      : -1;
            if (texId != -1)
            {
                material.SetTexture(ID_BaseMap, source.GetTexture(texId));
                material.SetTextureScale(ID_BaseMap, source.GetTextureScale(texId));
                material.SetTextureOffset(ID_BaseMap, source.GetTextureOffset(texId));
            }

            // Tint: keep hue/brightness, but never let a black-albedo source (old void material) kill reflection.
            Color tint = source.HasProperty(ID_BaseColor) ? source.GetColor(ID_BaseColor)
                       : source.HasProperty(ID_Color) ? source.GetColor(ID_Color)
                       : Color.white;
            if (tint.maxColorComponent < 0.02f) tint = new Color(1f, 1f, 1f, tint.a);
            material.SetColor(ID_BaseColor, tint);

            // Transparent / cut-out artwork (e.g. transparent PNG cards) -> alpha clip, keeps it opaque-sorted.
            bool clip = (source.HasProperty(ID_AlphaClip) && source.GetFloat(ID_AlphaClip) > 0.5f)
                     || (source.HasProperty(ID_Surface) && source.GetFloat(ID_Surface) > 0.5f)
                     || source.renderQueue >= (int)RenderQueue.AlphaTest;
            if (clip)
            {
                material.SetFloat(ID_AlphaClip, 1f);
                material.SetFloat(ID_Cutoff, source.HasProperty(ID_Cutoff) ? source.GetFloat(ID_Cutoff) : 0.5f);
                material.EnableKeyword("_ALPHATEST_ON");
                material.renderQueue = (int)RenderQueue.AlphaTest;
            }

            convertedMaterials[source] = material;
            return material;
        }

        Material CreateSurfaceMaterial(string name)
        {
            var material = new Material(surfaceShader) { name = name };
            material.SetFloat(ID_Wrap, diffuseWrap);
            material.SetFloat(ID_Smoothness, smoothness);
            material.SetFloat(ID_SpecularStrength, specularStrength);
            material.SetFloat(ID_Cull, (float)CullMode.Off);   // cards / planes are thin: light both faces
            ownedMaterials.Add(material);
            return material;
        }

        void ApplyCameraFeedPolicy()
        {
#if UNITY_EDITOR
            bool suppress = suppressCameraFeedInEditor;
#else
            bool suppress = suppressCameraFeedOnDevice;
#endif
            if (!suppress) return;

            foreach (var background in GetComponentsInChildren<ARCameraBackground>(true))
            {
                background.enabled = false;
                var cam = background.GetComponent<Camera>();
                if (cam == null) continue;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
            }
        }
    }
}
