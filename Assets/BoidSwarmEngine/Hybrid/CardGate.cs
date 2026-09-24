using System.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// A binary lock. Petalo drives into the approach volume, the director freezes the world and asks the player
    /// for one specific physical card, and the moment AR Foundation reports that card as tracked the lock opens.
    /// There is no rotation, alignment or ordering to get right: seeing the card IS the solution.
    ///
    /// The gate itself only knows its card, its barrier and its reaction. Everything about the scan - the frozen
    /// world, the UI, the TASK COMPLETED beat - belongs to <see cref="HybridLoopDirector"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    [AddComponentMenu("Funobotz/Card Gate")]
    public sealed class CardGate : MonoBehaviour
    {
        public enum GateState { Sealed, Scanning, Solved }

        [Header("Card")]
        [Tooltip("Reference image name in the AR library (case-insensitive).")]
        [SerializeField] string requiredCard = "king of spades";
        [Tooltip("The card's role in the fiction (GDD card classes: Keystone, Prism, Dynamo, Anchor).")]
        [SerializeField] string cardRole = "KEYSTONE";
        [Tooltip("What the player is told to look for.")]
        [SerializeField] string cardDisplayName = "King of Spades";

        [Header("World")]
        [Tooltip("Invisible wall across the gate. Petalo's ledge clamp reads its top as a rise too tall to climb.")]
        [SerializeField] Collider barrier;
        [SerializeField] GateReaction reaction;

        [Header("Events")]
        public UnityEvent solved;

        GateState state = GateState.Sealed;

        public GateState State => state;
        public string RequiredCard => requiredCard;
        public string CardRole => cardRole;
        public string CardDisplayName => cardDisplayName;

        void Reset()
        {
            var trigger = GetComponent<Collider>();
            if (trigger != null) trigger.isTrigger = true;
        }

        void Awake()
        {
            var trigger = GetComponent<Collider>();
            if (trigger != null && !trigger.isTrigger) trigger.isTrigger = true;
        }

        // Stay rather than Enter: if the director was busy (a title card still playing) when Petalo crossed the
        // edge, an Enter would be lost for good and the player would stand at a gate that never asks for a card.
        void OnTriggerStay(Collider other)
        {
            if (state != GateState.Sealed) return;
            if (other.GetComponentInParent<PetaloPilot>() == null) return;

            var director = HybridLoopDirector.Instance;
            if (director != null) director.RequestScan(this);
        }

        public void MarkScanning() => state = GateState.Scanning;

        /// <summary>Plays the world's response, then lifts the barrier. Runs once.</summary>
        public IEnumerator Open()
        {
            if (state == GateState.Solved) yield break;
            state = GateState.Solved;
            solved?.Invoke();

            if (reaction != null) yield return reaction.Play();
            if (barrier != null) barrier.enabled = false;
        }
    }
}
