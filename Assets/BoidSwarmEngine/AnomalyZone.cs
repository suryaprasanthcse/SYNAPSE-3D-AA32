using UnityEngine;
using UnityEngine.Events;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The place in the world where Petalo stops being a character and becomes the Anomaly.
    ///
    /// Driving into the zone morphs the avatar from State A to State B - the shader lerps the two distance
    /// fields, so the mascot physically unfolds into the titanium mandala rather than cross-fading to it. The
    /// pilot releases control on its own once the blend passes halfway, which is what turns arrival into a
    /// cutscene without a separate camera rig or timeline.
    ///
    /// Put one on the Lumenforge. <see cref="latch"/> is the GDD's rule that progress is never taken back.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    [AddComponentMenu("Funobotz/Anomaly Zone")]
    public sealed class AnomalyZone : MonoBehaviour
    {
        [SerializeField] PetaloBeacon petalo;
        [Tooltip("Once entered, stay in the Anomaly state even if the player drives back out.")]
        [SerializeField] bool latch = true;
        [Tooltip("Swarm and Petalo alignment to drive towards while inside: 1 is the ignited gold.")]
        [SerializeField, Range(0f, 1f)] float alignmentOnEnter = 1f;

        [Header("Events")]
        public UnityEvent entered;
        public UnityEvent exited;

        bool inside;
        bool latched;

        void Reset()
        {
            var collider = GetComponent<Collider>();
            if (collider != null) collider.isTrigger = true;
        }

        void Awake()
        {
            if (petalo == null) petalo = FindAnyObjectByType<PetaloBeacon>();

            var collider = GetComponent<Collider>();
            if (collider != null && !collider.isTrigger)
            {
                Debug.LogWarning("[AnomalyZone] Collider was not a trigger; forcing it, or Petalo would bounce off " +
                                 "the forge instead of entering it.", this);
                collider.isTrigger = true;
            }
        }

        void OnTriggerEnter(Collider other)
        {
            if (inside || latched) return;
            if (other.GetComponentInParent<PetaloPilot>() == null) return;

            inside = true;
            if (latch) latched = true;

            if (petalo != null)
            {
                petalo.EnterAnomaly();
                petalo.Alignment = alignmentOnEnter;
                petalo.Ping();
            }
            entered?.Invoke();
        }

        void OnTriggerExit(Collider other)
        {
            if (!inside) return;
            if (other.GetComponentInParent<PetaloPilot>() == null) return;

            inside = false;
            if (latched) return;

            if (petalo != null) petalo.EnterExploration();
            exited?.Invoke();
        }
    }
}
