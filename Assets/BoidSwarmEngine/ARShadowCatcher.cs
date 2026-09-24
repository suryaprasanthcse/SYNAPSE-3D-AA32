using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Puts the blockout's shadow onto the real table.
    ///
    /// A detected plane has no colour of its own worth drawing - the camera feed already shows the desk. What it
    /// can do is darken. This gives every plane an invisible surface running SwarmSurface in ShadowCatcher mode,
    /// which multiplies the feed down wherever the key light is occluded. Contact shadow is most of what makes
    /// virtual geometry read as physically present rather than pasted on, and it is the one cue the swarm's point
    /// lights can never provide, because they have no occlusion at all.
    ///
    /// Drawn before the opaque queue and writing no colour of its own beyond the multiply, so it never fights the
    /// blockout for depth.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Funobotz/AR Shadow Catcher")]
    public sealed class ARShadowCatcher : MonoBehaviour
    {
        const string SurfaceShaderName = "MAAYAI/SwarmSurface";

        [SerializeField] ARPlaneManager planeManager;
        [Tooltip("How dark a fully occluded patch of desk becomes. 1 is black, which never looks right against a " +
                 "camera feed that still carries ambient room light.")]
        [SerializeField, Range(0f, 1f)] float shadowStrength = 0.55f;

        static readonly int ID_RenderMode = Shader.PropertyToID("_RenderMode");
        static readonly int ID_SrcBlend = Shader.PropertyToID("_SrcBlend");
        static readonly int ID_DstBlend = Shader.PropertyToID("_DstBlend");
        static readonly int ID_ZWrite = Shader.PropertyToID("_ZWrite");
        static readonly int ID_Cull = Shader.PropertyToID("_Cull");
        static readonly int ID_ShadowStrength = Shader.PropertyToID("_ShadowStrength");
        static readonly int ID_KeyLightGain = Shader.PropertyToID("_KeyLightGain");

        Material catcher;

        void Awake()
        {
            if (planeManager == null) planeManager = FindAnyObjectByType<ARPlaneManager>();
            if (planeManager == null)
            {
                Debug.LogError("[ARShadowCatcher] No ARPlaneManager: there are no planes to catch shadows on.", this);
                enabled = false;
                return;
            }

            var shader = Shader.Find(SurfaceShaderName);
            if (shader == null)
            {
                Debug.LogError($"[ARShadowCatcher] '{SurfaceShaderName}' not found.", this);
                enabled = false;
                return;
            }

            catcher = new Material(shader) { name = "SwarmSurface_ShadowCatcher" };
            catcher.SetFloat(ID_RenderMode, 2f);                                   // ShadowCatcher
            catcher.SetFloat(ID_SrcBlend, (float)UnityEngine.Rendering.BlendMode.DstColor);
            catcher.SetFloat(ID_DstBlend, (float)UnityEngine.Rendering.BlendMode.Zero);
            catcher.SetFloat(ID_ZWrite, 0f);                                       // never occlude the feed
            catcher.SetFloat(ID_Cull, (float)UnityEngine.Rendering.CullMode.Off);
            catcher.SetFloat(ID_ShadowStrength, shadowStrength);
            catcher.SetFloat(ID_KeyLightGain, 1f);
            // Just before the blockout, so the multiply lands on the feed and the geometry draws over it.
            catcher.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry - 1;
        }

        void OnEnable()
        {
            if (planeManager == null) return;
            planeManager.trackablesChanged.AddListener(OnPlanesChanged);
            foreach (var plane in planeManager.trackables) Apply(plane);
        }

        void OnDisable()
        {
            if (planeManager != null) planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
        }

        void OnDestroy()
        {
            if (catcher != null) Destroy(catcher);
        }

        void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (var plane in args.added) Apply(plane);
            foreach (var plane in args.updated) Apply(plane);
        }

        void Apply(ARPlane plane)
        {
            if (plane == null || catcher == null) return;

            var renderer = plane.GetComponent<MeshRenderer>();
            if (renderer == null) return;
            if (renderer.sharedMaterial == catcher) return;

            renderer.sharedMaterial = catcher;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;   // the desk casts nothing
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }
    }
}
