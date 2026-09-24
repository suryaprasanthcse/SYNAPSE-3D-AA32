using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_EDITOR || DEBUG
using UnityEngine.InputSystem;
#endif

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The hybrid gameplay loop: joystick traversal between physical AR card triggers.
    ///
    ///   AwaitingFloor  the player points at the floor; the first act is dropped in front of them
    ///   ActIntro       the act materialises out of the dark, the title card plays
    ///   Exploring      joystick only - Petalo crosses colonnades and staircases to the act's gate
    ///   Scanning       Petalo is at the gate: the world freezes and the player shows the one card it wants
    ///   GateReacting   TASK COMPLETED, then the world answers (arch restores, doors open, forge fires)
    ///   Transition     the act dissolves, the swarm pours across the real room to the next anchor while the
    ///                  loading screen is up, and the next act materialises where it lands
    ///   Finale         the Lumenforge is lit and Petalo has entered it
    ///
    /// Only the director moves the world root or toggles acts; gates and exits merely report to it. That keeps
    /// every transition in one place, where the ordering between them can actually be read.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(50)]
    [AddComponentMenu("Funobotz/Hybrid Loop Director")]
    public sealed class HybridLoopDirector : MonoBehaviour
    {
        public enum Phase { AwaitingFloor, ActIntro, Exploring, Scanning, GateReacting, Transition, Finale }

        public static HybridLoopDirector Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        [Header("Wiring")]
        [SerializeField] PlaneAnchoredWorld world;
        [SerializeField] PetaloPilot pilot;
        [SerializeField] PetaloBeacon petalo;
        [SerializeField] SwarmGPUArchitect swarm;
        [SerializeField] SwarmTether tether;
        [SerializeField] ARTrackedImageManager trackedImages;
        [SerializeField] HybridLoopUI ui;
        [SerializeField] MissionHeader header;
        [SerializeField] SwarmDepthField depthField;
        [SerializeField] Camera viewCamera;

        [Header("Acts (in order)")]
        [SerializeField] HybridAct[] acts = Array.Empty<HybridAct>();

        [Header("Placement")]
        [Tooltip("How far in front of the player (m) each act's spawn point is dropped onto the floor.")]
        [SerializeField, Min(0.2f)] float actPlacementDistance = 0.8f;
        [Tooltip("Floor must be tracked this long (s) before the first act is dropped, so ARCore can find the " +
                 "floor under a table before the act commits to the table.")]
        [SerializeField, Min(0f)] float floorSettleSeconds = 1.2f;

        [Header("Presentation")]
        [SerializeField, Min(0.5f)] float titleHoldSeconds = 3f;

        [Header("Card Scan")]
        [Tooltip("The card must stay tracked this long (s), so a single false-positive frame never opens a gate.")]
        [SerializeField, Min(0f)] float scanConfirmSeconds = 0.4f;

        [Header("Swarm Transition")]
        [SerializeField, Min(0.1f)] float flightSpeed = 0.45f;
        [SerializeField, Min(0.5f)] float minFlightSeconds = 3.5f;
        [SerializeField, Min(0.5f)] float maxFlightSeconds = 6.5f;
        [Tooltip("Height (m) of the swarm's arc across the room, on top of a share of the distance travelled.")]
        [SerializeField, Min(0f)] float flightArc = 0.35f;
        [SerializeField, Min(1f)] float flightAuraScale = 3f;
        [SerializeField, Min(1f)] float flightFlowBoost = 2.2f;

        [Header("Development")]
        [Tooltip("Editor and development builds only: press C during a scan to stand in for the physical card.")]
        [SerializeField] bool devCardShortcut = true;

        [Header("Progress")]
        [Tooltip("Relaunching resumes at the last act reached, instead of replaying from Act 1. Cleared by the finale.")]
        [SerializeField] bool resumeProgress = true;

        const string ProgressKey = "MAAYAI.HybridLoop.ActIndex";

        Phase phase = Phase.AwaitingFloor;
        int actIndex = -1;
        HybridAct currentAct;
        CardGate pendingGate;
        MeshRenderer petaloRenderer;
        float celebrate;            // decaying gold pulse on the swarm and Petalo
        bool devCardPressed;

        public Phase CurrentPhase => phase;
        public HybridAct CurrentAct => currentAct;
        public int ActIndex => actIndex;

        void Awake()
        {
            Instance = this;
            if (world == null) world = FindAnyObjectByType<PlaneAnchoredWorld>();
            if (pilot == null) pilot = FindAnyObjectByType<PetaloPilot>(FindObjectsInactive.Include);
            if (petalo == null) petalo = FindAnyObjectByType<PetaloBeacon>(FindObjectsInactive.Include);
            if (swarm == null) swarm = FindAnyObjectByType<SwarmGPUArchitect>();
            if (tether == null && pilot != null) tether = pilot.GetComponent<SwarmTether>();
            if (trackedImages == null) trackedImages = FindAnyObjectByType<ARTrackedImageManager>();
            if (ui == null) ui = GetComponent<HybridLoopUI>();
            if (header == null) header = FindAnyObjectByType<MissionHeader>();
            if (depthField == null) depthField = FindAnyObjectByType<SwarmDepthField>();
            if (viewCamera == null) viewCamera = Camera.main;

            if (world == null || pilot == null || acts.Length == 0)
                Debug.LogError("[HybridLoop] Needs a PlaneAnchoredWorld, a PetaloPilot and at least one act.", this);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            foreach (var act in acts)
            {
                if (act == null) continue;
                act.EnsureMaterial();
                act.gameObject.SetActive(false);
            }
            if (world != null && pilot != null && acts.Length > 0) StartCoroutine(Run());
        }

        void Update()
        {
#if UNITY_EDITOR || DEBUG
            if (devCardShortcut && phase == Phase.Scanning && Keyboard.current != null &&
                Keyboard.current.cKey.wasPressedThisFrame)
                devCardPressed = true;
#endif
#if UNITY_EDITOR
            // XR Simulation: point the simulated phone at the card this gate wants, exactly as a player would.
            if (phase == Phase.Scanning && pendingGate != null && swarm != null && Keyboard.current != null &&
                Keyboard.current.fKey.wasPressedThisFrame)
                swarm.FocusSimulatedCard(SimulatedCardName(pendingGate.RequiredCard));
#endif
            if (phase == Phase.Finale) return;

            // The celebration pulse is the only writer of swarm/Petalo alignment during play.
            celebrate = Mathf.MoveTowards(celebrate, 0f, Time.deltaTime * 0.6f);
            float gold = Mathf.SmoothStep(0f, 1f, celebrate);
            if (swarm != null) swarm.Alignment = gold;
            if (petalo != null && petalo.StateBlend < 0.5f) petalo.Alignment = gold;
        }

        // ==========================================================================================
        // Reports from the world
        // ==========================================================================================
        /// <summary>Petalo is standing in a gate's approach volume.</summary>
        public void RequestScan(CardGate gate)
        {
            if (phase != Phase.Exploring || gate == null || gate.State != CardGate.GateState.Sealed) return;
            if (currentAct == null || currentAct.Gate != gate) return;
            StartCoroutine(ScanRoutine(gate));
        }

        /// <summary>Petalo is standing in an act's exit volume.</summary>
        public void ReportExit(HybridAct act)
        {
            if (phase != Phase.Exploring || act == null || act != currentAct) return;
            if (act.Gate != null && act.Gate.State != CardGate.GateState.Solved) return;
            if (act.IsFinale) return;
            StartCoroutine(TransitionRoutine());
        }

        // ==========================================================================================
        // Flow
        // ==========================================================================================
        IEnumerator Run()
        {
            phase = Phase.AwaitingFloor;
            SetPilotControl(false);
            if (header != null) header.SetObjective("Find the floor", "SCAN");
            if (ui != null) ui.ShowHint("Point your camera at the floor and move it slowly");

            // Wait for a surface, then give ARCore a moment to prefer the floor under any table it saw first.
            float settled = 0f;
            while (settled < floorSettleSeconds)
            {
                settled = world.IsPlaced ? settled + Time.deltaTime : 0f;
                yield return null;
            }
            if (ui != null) ui.HideHint();

            actIndex = resumeProgress ? Mathf.Clamp(PlayerPrefs.GetInt(ProgressKey, 0), 0, acts.Length - 1) : 0;
            currentAct = acts[actIndex];
            if (actIndex > 0) Debug.Log($"[HybridLoop] Resuming at act {actIndex + 1} ({currentAct.Title}).", this);
            SaveProgress();
            PlaceAct(currentAct);
            world.Lock();
            TeleportPetaloToSpawn(currentAct);
            SetPetaloVisible(true);

            yield return IntroduceAct(currentAct);
        }

        /// <summary>Materialise the current act, play its title, hand control back.</summary>
        IEnumerator IntroduceAct(HybridAct act)
        {
            phase = Phase.ActIntro;
            act.gameObject.SetActive(true);
            act.SetDissolve(1f, world.WorldScale);
            yield return act.Materialize(world.WorldScale);

            if (header != null) header.SetObjective(act.ApproachObjective, "SCAN");
            if (petalo != null) petalo.Ping(0.8f);

            // Control returns as the title comes up: the title is a beat, not a lock-out.
            SetPilotControl(true);
            phase = Phase.Exploring;
            if (ui != null) yield return ui.PlayActTitle($"ACT {act.ActNumber}", act.Title, act.Subtitle, titleHoldSeconds);
        }

        IEnumerator ScanRoutine(CardGate gate)
        {
            phase = Phase.Scanning;
            pendingGate = gate;
            gate.MarkScanning();
            SetPilotControl(false);

            // Pin the world before the player swings the phone away toward a card on the table.
            world.HoldForScan(true);
            if (header != null) header.SetObjective($"Show the {Title(gate.CardRole)} card", "SCAN");
            if (ui != null) ui.HideActTitle();
            if (ui != null) ui.ShowScanPrompt(gate.CardRole, gate.CardDisplayName, FindCardArt(gate.RequiredCard));
#if UNITY_EDITOR
            if (ui != null) ui.SetScanStatus("Play mode: press F to aim the simulated phone at the card");
#endif

            devCardPressed = false;
            float seen = 0f;
            while (seen < scanConfirmSeconds)
            {
                seen = IsCardTracked(gate.RequiredCard) ? seen + Time.deltaTime : 0f;
                if (devCardPressed) break;
                yield return null;
            }

            if (ui != null) ui.MarkScanDetected();
            yield return new WaitForSeconds(0.35f);
#if UNITY_EDITOR
            // Put the simulated phone back where the player was standing before it was aimed at the card.
            if (swarm != null) swarm.RestoreSimulatedView();
#endif
            if (ui != null) ui.HideScanPrompt();
            world.HoldForScan(false);

            phase = Phase.GateReacting;
            celebrate = 1f;
            if (petalo != null) petalo.Ping();
            if (ui != null) yield return ui.PlayTaskCompleted($"{gate.CardRole} CARD ACCEPTED");

            if (header != null) header.SetObjective("Watch the world respond", "RESTORE");
            yield return gate.Open();
            pendingGate = null;

            if (header != null) header.SetObjective(currentAct.ExitObjective, currentAct.IsFinale ? "IGNITE" : "ONWARD");
            SetPilotControl(true);
            phase = Phase.Exploring;

            if (currentAct.IsFinale) HookFinale(currentAct);
        }

        IEnumerator TransitionRoutine()
        {
            phase = Phase.Transition;
            SetPilotControl(false);
            if (header != null) header.SetObjective("Follow the swarm", "TRAVEL");

            HybridAct from = currentAct;
            HybridAct next = acts[Mathf.Min(actIndex + 1, acts.Length - 1)];

            // 1. The act is unmade; Petalo dissolves into its own swarm, which swells into a travelling current.
            celebrate = 1f;
            if (petalo != null) petalo.Ping();
            yield return from.Unmake(world.WorldScale);
            SetPetaloVisible(false);
            if (tether != null) tether.AuraScale = flightAuraScale;
            if (swarm != null) swarm.FlowBoost = flightFlowBoost;
            if (depthField != null) depthField.Active = true;
            if (ui != null) ui.ShowLoading(true);

            // 2. The next act's anchor: a fresh patch of floor in front of the player. The root moves there, so
            //    Petalo (a child of it) is pinned back to where the swarm actually is before the flight starts.
            Transform rig = pilot.transform;
            Vector3 start = rig.position;
            from.gameObject.SetActive(false);

            actIndex = Array.IndexOf(acts, next);
            currentAct = next;
            SaveProgress();
            PlaceAct(next);
            next.gameObject.SetActive(true);
            next.SetDissolve(1f, world.WorldScale);
            rig.position = start;

            // 3. The swarm pours across the room along an arc, the depth field steering it off real furniture.
            Vector3 end = world.transform.TransformPoint(SpawnDesignPoint(next, withHeight: true));
            float distance = Vector3.Distance(start, end);
            float duration = Mathf.Clamp(distance / flightSpeed, minFlightSeconds, maxFlightSeconds);
            Vector3 control = (start + end) * 0.5f + Vector3.up * (flightArc + 0.3f * distance);

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                float u = 1f - e;
                rig.position = u * u * start + 2f * u * e * control + e * e * end;
                if (ui != null) ui.SetLoadingProgress(e * 0.8f);
                yield return null;
            }

            // 4. Arrived: the swarm settles and the environment is generated around it.
            TeleportPetaloToSpawn(next);
            if (tether != null) tether.AuraScale = 1f;
            if (swarm != null) swarm.FlowBoost = 1f;
            if (depthField != null) depthField.Active = false;

            yield return next.Materialize(world.WorldScale);
            if (ui != null)
            {
                ui.SetLoadingProgress(1f);
                ui.ShowLoading(false);
            }
            SetPetaloVisible(true);
            yield return new WaitForSeconds(0.5f);

            phase = Phase.ActIntro;
            if (header != null) header.SetObjective(next.ApproachObjective, "SCAN");
            SetPilotControl(true);
            phase = Phase.Exploring;
            if (ui != null) yield return ui.PlayActTitle($"ACT {next.ActNumber}", next.Title, next.Subtitle, titleHoldSeconds);
        }

        void HookFinale(HybridAct act)
        {
            var reaction = act.GetComponentInChildren<ForgeIgniteReaction>(true);
            var zone = reaction != null ? reaction.Forge : act.GetComponentInChildren<AnomalyZone>(true);
            if (zone == null)
            {
                StartCoroutine(FinaleRoutine());
                return;
            }
            zone.entered.AddListener(OnForgeEntered);
        }

        void OnForgeEntered()
        {
            if (phase == Phase.Finale) return;
            StartCoroutine(FinaleRoutine());
        }

        IEnumerator FinaleRoutine()
        {
            phase = Phase.Finale;

            // GDD climax: the swarm merges with the forge's output and Petalo's sensor turns from cyan to gold.
            if (swarm != null) swarm.Alignment = 1f;
            if (petalo != null)
            {
                petalo.Alignment = 1f;
                petalo.Ping();
            }
            if (header != null) header.SetCompleted();

            // The story is finished: the next launch starts from the Grand Gateway again.
            PlayerPrefs.DeleteKey(ProgressKey);
            PlayerPrefs.Save();
            yield return new WaitForSeconds(1.2f);
            if (ui != null)
            {
                yield return ui.PlayActTitle("EPILOGUE", "LUMINARA RESTORED", "The Lost Radiance flows again", 5f, golden: true);
                ui.ShowNewGame(StartNewGame);
            }
        }

        /// <summary>
        /// Back to the Grand Gateway. The scene is reloaded rather than rewound: every gate, arch segment, door,
        /// forge and Petalo's anomaly form start from exactly their authored state, with nothing left to un-latch.
        /// </summary>
        public void StartNewGame()
        {
            if (restarting) return;
            restarting = true;
            ResetProgress();
            StartCoroutine(RestartRoutine());
        }

        bool restarting;

        /// <summary>
        /// Stop tracking in order before the scene goes: image tracking and plane detection first, then the session.
        /// Reloading with them still running leaves their background discovery loops holding trackables the reload
        /// has already destroyed (XR Simulation throws on exactly that and error-pauses the Editor).
        /// </summary>
        IEnumerator RestartRoutine()
        {
            SetPilotControl(false);
            if (trackedImages != null) trackedImages.enabled = false;
            var planes = FindAnyObjectByType<ARPlaneManager>();
            if (planes != null) planes.enabled = false;
            var session = FindAnyObjectByType<ARSession>();
            if (session != null) session.enabled = false;

            yield return null;
            yield return null;
#if UNITY_EDITOR
            RestartSimulatedXR();
#endif

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (scene.buildIndex >= 0) UnityEngine.SceneManagement.SceneManager.LoadScene(scene.buildIndex);
            else UnityEngine.SceneManagement.SceneManager.LoadScene(scene.name);
        }

        // ==========================================================================================
        // Helpers
        // ==========================================================================================
        /// <summary>Drop the act's spawn point onto the floor in front of the player, facing away from them.</summary>
        void PlaceAct(HybridAct act)
        {
            Transform viewer = viewCamera != null ? viewCamera.transform : null;
            if (!world.TryGetPointInFront(viewer, actPlacementDistance, out Vector3 point, out Vector3 forward))
            {
                point = world.transform.position;
                forward = world.transform.forward;
            }
            world.PlaceDesignPoint(SpawnDesignPoint(act, withHeight: false), point, forward);
        }

        /// <summary>
        /// The spawn point in the world root's design space. Without height, it is the act's ground datum under
        /// the spawn - that, not the raised spawn deck, is what sits on the real floor.
        /// </summary>
        Vector3 SpawnDesignPoint(HybridAct act, bool withHeight)
        {
            Vector3 p = world.transform.InverseTransformPoint(act.SpawnPoint.position);
            if (!withHeight) p.y = 0f;
            return p;
        }

        void TeleportPetaloToSpawn(HybridAct act)
        {
            Transform rig = pilot.transform;
            rig.localPosition = SpawnDesignPoint(act, withHeight: true);
            rig.localRotation = Quaternion.identity;
        }

#if UNITY_EDITOR
        /// <summary>
        /// XR Simulation only. Its image tracker outlives the scene: on reload the simulation rebuilds its room,
        /// but the tracker resumes with the card list of the room it just destroyed and throws every tick. The
        /// subsystems themselves are recreated here, so the reloaded scene starts on a clean simulation. On device
        /// the ARCore loader is left alone - a session stop and scene reload is all ARCore needs.
        /// </summary>
        void RestartSimulatedXR()
        {
            var manager = UnityEngine.XR.Management.XRGeneralSettings.Instance != null
                ? UnityEngine.XR.Management.XRGeneralSettings.Instance.Manager
                : null;
            if (manager == null || !(manager.activeLoader is UnityEngine.XR.Simulation.SimulationLoader)) return;

            // Nothing in the outgoing scene may touch a subsystem while they are torn down and rebuilt.
            var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null) origin.gameObject.SetActive(false);

            manager.StopSubsystems();
            manager.DeinitializeLoader();
            manager.InitializeLoaderSync();
            manager.StartSubsystems();
        }
#endif

        /// <summary>Written the moment an act begins, so a crash or a closed app never costs more than that act.</summary>
        void SaveProgress()
        {
            if (!resumeProgress) return;
            PlayerPrefs.SetInt(ProgressKey, actIndex);
            PlayerPrefs.Save();
        }

        /// <summary>Start the next launch from Act 1.</summary>
        public static void ResetProgress()
        {
            PlayerPrefs.DeleteKey(ProgressKey);
            PlayerPrefs.Save();
        }

        void SetPilotControl(bool enabled)
        {
            if (pilot != null && pilot.enabled != enabled) pilot.enabled = enabled;
        }

        void SetPetaloVisible(bool visible)
        {
            // Resolved lazily: PetaloBeacon adds its renderer in its own Awake, which has not run yet when this
            // director wakes, because the world root keeps Petalo inactive until a floor is found.
            if (petaloRenderer == null && petalo != null) petaloRenderer = petalo.GetComponent<MeshRenderer>();
            if (petaloRenderer != null) petaloRenderer.enabled = visible;
        }

        bool IsCardTracked(string cardName)
        {
            if (trackedImages == null) return false;
            foreach (var image in trackedImages.trackables)
            {
                if (image.trackingState != TrackingState.Tracking) continue;
                if (string.Equals(image.referenceImage.name?.Trim(), cardName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        Texture FindCardArt(string cardName)
        {
            var library = trackedImages != null ? trackedImages.referenceLibrary : null;
            if (library == null) return null;
            for (int i = 0; i < library.count; i++)
            {
                var reference = library[i];
                if (string.Equals(reference.name?.Trim(), cardName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return reference.texture;
            }
            return null;
        }

        /// <summary>
        /// The XR Simulation stand-in for a reference image. The original King of Spades keeps its historical
        /// name; the others are named after the reference image they carry.
        /// </summary>
        static string SimulatedCardName(string referenceImage)
        {
            string specific = "Simulated_Card_" + referenceImage.Trim().Replace(' ', '_');
            return GameObject.Find(specific) != null ? specific : "Simulated_Card";
        }

        static string Title(string role) =>
            string.IsNullOrEmpty(role) ? role : char.ToUpperInvariant(role[0]) + role.Substring(1).ToLowerInvariant();
    }
}
