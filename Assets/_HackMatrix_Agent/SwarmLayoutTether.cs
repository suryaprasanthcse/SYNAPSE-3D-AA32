using MAAYAI.Swarm;
using UnityEngine;

namespace MAAYAI.HackMatrix
{
    /// <summary>
    /// Puts the GPU particle field inside the compiled layout, sized to its footprint. The layout is built on
    /// Marble_DarkAR, which has no light of its own: the field's irradiance volume is what makes the generated
    /// architecture visible at all, so the field goes wherever the compiler builds.
    ///
    /// The swarm follows an unscaled proxy rather than the layout root itself: the root carries the kit scale
    /// (0.03), and the swarm lifts itself by its bounds through its anchor's transform, which would shrink the
    /// lift to a centimetre.
    ///
    /// No compiled layout means no anchor: the field is paused and dark until the first compile.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Hack Matrix/Swarm Layout Tether")]
    public sealed class SwarmLayoutTether : MonoBehaviour
    {
        [SerializeField] GenerativeCompiler compiler;
        [SerializeField] SwarmGPUArchitect swarm;
        [Tooltip("Half extents of the particle field in real metres. The containment's hard limit lets particles " +
                 "reach 1.5x this, so 0.55 covers the 1.5 m footprint without spilling far past it.")]
        [SerializeField] Vector3 fieldHalfExtentsMetres = new(0.55f, 0.22f, 0.55f);

        Transform proxy;

        void Awake()
        {
            if (compiler == null) compiler = FindAnyObjectByType<GenerativeCompiler>();
            if (swarm == null) swarm = FindAnyObjectByType<SwarmGPUArchitect>();
            proxy = new GameObject("SwarmLayoutAnchor").transform;
            proxy.SetParent(transform, false);
        }

        void LateUpdate()
        {
            if (swarm == null || compiler == null) return;

            Transform layout = compiler.LayoutRoot;
            if (layout == null)
            {
                swarm.SetAnchorTracking(false);
                return;
            }

            proxy.SetPositionAndRotation(layout.position, layout.rotation);
            swarm.AttachToAnchor(proxy);
            swarm.SetAnchorTracking(true);
            swarm.BoundsExtents = fieldHalfExtentsMetres;       // no-op unless it changed
        }
    }
}
