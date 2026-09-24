using UnityEngine;
using UnityEngine.Rendering;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The "Dark AR" rule: the real room keeps its camera passthrough, but the virtual world has no light of its
    /// own. Ambient is zero, reflections are zero, the SwarmSurface ambient floor is zero, and the key light is
    /// off - so a virtual surface is pitch black until Petalo's beacon or the swarm's VPLs physically reach it.
    ///
    /// Enforced every frame rather than once: scene lighting is global state that editor tooling and old
    /// automation scripts in this project have all written at one time or another.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    [AddComponentMenu("Funobotz/Dark AR Lighting")]
    public sealed class DarkARLighting : MonoBehaviour
    {
        static readonly int ID_SwarmAmbient = Shader.PropertyToID("_SwarmAmbient");

        [Tooltip("Lights that would otherwise light the virtual architecture directly.")]
        [SerializeField] Light[] disabledLights = System.Array.Empty<Light>();
        [Tooltip("Re-assert every frame. Off applies once, leaving the diagnostics' ambient presets usable for tuning.")]
        [SerializeField] bool enforceEveryFrame = true;

        void OnEnable() => Apply();

        void LateUpdate()
        {
            if (enforceEveryFrame) Apply();
        }

        void Apply()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.ambientIntensity = 0f;
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.fog = false;
            Shader.SetGlobalFloat(ID_SwarmAmbient, 0f);

            foreach (var l in disabledLights)
                if (l != null && l.enabled) l.enabled = false;
        }
    }
}
