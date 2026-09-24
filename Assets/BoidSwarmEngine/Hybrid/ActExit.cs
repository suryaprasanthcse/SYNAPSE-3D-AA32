using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>The far side of an act. Reaching it once the gate is open completes the act.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    [AddComponentMenu("Funobotz/Act Exit")]
    public sealed class ActExit : MonoBehaviour
    {
        [SerializeField] HybridAct act;

        void Awake()
        {
            if (act == null) act = GetComponentInParent<HybridAct>();
            var trigger = GetComponent<Collider>();
            if (trigger != null) trigger.isTrigger = true;
        }

        void OnTriggerStay(Collider other)
        {
            if (other.GetComponentInParent<PetaloPilot>() == null) return;
            var director = HybridLoopDirector.Instance;
            if (director != null) director.ReportExit(act);
        }
    }
}
