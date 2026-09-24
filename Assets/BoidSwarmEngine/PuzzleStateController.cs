using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The Lumenforge puzzle (Phase 2 GDD, Mystery Cave): the player rotates the physical King of Spades until the
    /// swarm aligns with the forge, then holds it there until the forge ignites.
    ///
    ///   DORMANT   card not tracked, or yaw outside the +/-10 deg window. Swarm and Petalo read cyan.
    ///   CHARGING  yaw inside the window. Charge accumulates while held; colour drifts cyan -> gold.
    ///   IGNITED   charge complete. Everything locks gold, Petalo blooms fully, progress is done.
    ///
    /// Losing the card is never a fail state (GDD): charge decays rather than resetting, so the player can lift a
    /// hand, re-show the card and carry on from where they were.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PuzzleStateController : MonoBehaviour
    {
        public enum PuzzleState { Dormant, Charging, Ignited }

        [Header("Wiring")]
        [SerializeField] SwarmGPUArchitect swarm;
        [SerializeField] PetaloBeacon petalo;
        [SerializeField] ARTrackedImageManager trackedImageManager;
        [SerializeField] string anchorImageName = "king of spades";

        [Header("Alignment")]
        [Tooltip("Card yaw (deg) that counts as solved. 0 = the yaw first seen when the card was placed.")]
        [SerializeField] float targetYawDegrees;
        [Tooltip("GDD: aligned within +/-10 degrees.")]
        [SerializeField, Range(1f, 45f)] float alignmentToleranceDegrees = 10f;
        [Tooltip("Capture the first tracked yaw as the reference, so any card orientation can start the puzzle.")]
        [SerializeField] bool calibrateOnFirstSight = true;

        [Header("Ignition")]
        [Tooltip("Seconds of sustained alignment needed to ignite the forge.")]
        [SerializeField, Min(0.1f)] float secondsToIgnite = 3f;
        [Tooltip("How fast charge bleeds away while misaligned or untracked (fraction per second).")]
        [SerializeField, Min(0f)] float chargeDecayRate = 0.35f;
        [Tooltip("Once ignited, stay ignited (GDD: progress is saved).")]
        [SerializeField] bool latchIgnition = true;

        [Header("Events")]
        public UnityEvent<PuzzleState> stateChanged;
        public UnityEvent ignited;

        ARTrackedImage anchorImage;
        float referenceYaw;
        bool calibrated;
        float charge;
        PuzzleState state = PuzzleState.Dormant;

        public PuzzleState State => state;
        /// <summary>0..1 ignition progress.</summary>
        public float Charge => charge;
        /// <summary>Signed yaw error against the target, in degrees.</summary>
        public float YawErrorDegrees { get; private set; }
        public bool IsAligned => Mathf.Abs(YawErrorDegrees) <= alignmentToleranceDegrees;

        void Awake()
        {
            if (swarm == null) swarm = FindAnyObjectByType<SwarmGPUArchitect>();
            if (trackedImageManager == null) trackedImageManager = FindAnyObjectByType<ARTrackedImageManager>();
            if (petalo == null)
            {
                var found = FindObjectsByType<PetaloBeacon>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (found.Length > 0) petalo = found[0];
            }

            if (swarm == null) Debug.LogError("[PuzzleStateController] No SwarmGPUArchitect: cannot drive swarm colour.", this);
            if (trackedImageManager == null) Debug.LogError("[PuzzleStateController] No ARTrackedImageManager: card yaw cannot be read.", this);
        }

        void Update()
        {
            ResolveAnchor();

            bool tracked = anchorImage != null && anchorImage.trackingState == TrackingState.Tracking;
            UpdateYawError(tracked);

            bool aligned = tracked && IsAligned;
            float dt = Time.deltaTime;

            if (state == PuzzleState.Ignited && latchIgnition)
            {
                charge = 1f;
            }
            else if (aligned)
            {
                charge = Mathf.Clamp01(charge + dt / secondsToIgnite);
            }
            else
            {
                // Never punitive: charge bleeds, it does not reset.
                charge = Mathf.Clamp01(charge - dt * chargeDecayRate);
            }

            PuzzleState next = charge >= 1f ? PuzzleState.Ignited
                             : aligned ? PuzzleState.Charging
                             : PuzzleState.Dormant;

            if (next != state)
            {
                state = next;
                stateChanged?.Invoke(state);
                if (state == PuzzleState.Ignited)
                {
                    ignited?.Invoke();
                    if (petalo != null) petalo.Ping();
                }
            }

            // One number drives every visual: cyan at 0, gold at 1.
            float goldness = state == PuzzleState.Ignited ? 1f : charge;
            if (swarm != null) swarm.Alignment = goldness;
            if (petalo != null) petalo.Alignment = goldness;
        }

        void ResolveAnchor()
        {
            if (anchorImage != null && anchorImage.gameObject != null) return;
            if (trackedImageManager == null) return;

            foreach (var image in trackedImageManager.trackables)
            {
                if (!string.Equals(image.referenceImage.name?.Trim(), anchorImageName.Trim(),
                        StringComparison.OrdinalIgnoreCase)) continue;
                anchorImage = image;
                return;
            }
        }

        void UpdateYawError(bool tracked)
        {
            if (!tracked)
            {
                // Hold the last error so a momentary loss does not slam the state machine to Dormant visuals.
                return;
            }

            // Yaw around the card's own normal: the angle of the card's forward axis in its own plane. Measuring
            // in the card's frame keeps this independent of where the player stands.
            Transform t = anchorImage.transform;
            Vector3 normal = t.up;
            Vector3 reference = Vector3.ProjectOnPlane(Vector3.forward, normal);
            if (reference.sqrMagnitude < 1e-6f) reference = Vector3.ProjectOnPlane(Vector3.right, normal);
            reference.Normalize();

            Vector3 cardForward = Vector3.ProjectOnPlane(t.forward, normal).normalized;
            float yaw = Vector3.SignedAngle(reference, cardForward, normal);

            if (calibrateOnFirstSight && !calibrated)
            {
                referenceYaw = yaw - targetYawDegrees;
                calibrated = true;
            }

            YawErrorDegrees = Mathf.DeltaAngle(referenceYaw + targetYawDegrees, yaw);
        }

        /// <summary>Re-zero the puzzle against the card's current orientation.</summary>
        public void Recalibrate()
        {
            calibrated = false;
            charge = 0f;
            state = PuzzleState.Dormant;
        }

        void OnGUI()
        {
            if (!Debug.isDebugBuild && !Application.isEditor) return;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            style.normal.textColor = state == PuzzleState.Ignited ? new Color(1f, 0.85f, 0.4f)
                                   : state == PuzzleState.Charging ? new Color(0.9f, 0.95f, 0.5f)
                                   : new Color(0.6f, 0.9f, 1f);
            float scale = Mathf.Max(1f, (Screen.dpi > 0f ? Screen.dpi : 160f) / 160f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUI.Label(new Rect(8, Screen.height / scale - 60f, 420f, 24f),
                $"<b>LUMENFORGE</b>  {state}   yaw {YawErrorDegrees:+0.0;-0.0}°  " +
                $"(±{alignmentToleranceDegrees:0}°)   charge {charge * 100f:0}%", style);
        }
    }
}
