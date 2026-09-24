using System;
using System.Collections.Generic;
using MAAYAI.Matrix.Swarm;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Phase 3 puzzle loop: link Petalo's anchor card to the Lumenforge conduit card, align it, and
/// read the answer in the GPU swarm. Attach to XR Origin (Mobile AR), next to
/// <see cref="ARTrackedImageManager"/>, <see cref="ARBoidBridge"/> and <see cref="ARMultiMatrixPayload"/>.
/// </summary>
/// <remarks>
/// All feedback is carried by the existing swarm: no extra prefabs, textures or audio.
/// The swarm host is moved and retuned; its material is cloned once at runtime so the shared
/// asset in Assets/ is never modified.
///
/// Per-frame cost: two transform reads, one sqrMagnitude, one SignedAngle and (only while the
/// colour is still travelling) one Lerp. Reference-image names are matched once per tracking
/// event, never in Update.
/// </remarks>
[RequireComponent(typeof(ARTrackedImageManager))]
[DisallowMultipleComponent]
[AddComponentMenu("XR/AR Matrix/Puzzle State Controller")]
public sealed class PuzzleStateController : MonoBehaviour
{
    public enum PuzzleState
    {
        /// <summary>Anchor tracked, not linked: scattered swarm, cyan.</summary>
        Dormant = 0,
        /// <summary>Within link range but off-angle: swarm bridges both cards, still cyan.</summary>
        LinkedMisaligned = 1,
        /// <summary>Linked and aligned: swarm converges and flows, colour drives to gold.</summary>
        Ignited = 2,
    }

    /// <summary>One swarm behaviour preset. Applied only when the state changes.</summary>
    [Serializable]
    public struct SwarmBehaviour
    {
        [Min(0f)] public float separationWeight;
        [Min(0f)] public float alignmentWeight;
        [Min(0f)] public float cohesionWeight;
        [Min(0f)] public float boundsWeight;
        [Min(0f)] public float minSpeed;
        [Min(0f)] public float maxSpeed;
        [Min(0f)] public float maxSteerForce;
        [Tooltip("Half-size of the simulation volume around the swarm host, in metres.")]
        public Vector3 boundsExtents;

        /// <summary>Loose cloud hanging over the anchor card.</summary>
        public static SwarmBehaviour Scattered => new()
        {
            separationWeight = 2.2f, alignmentWeight = 0.6f, cohesionWeight = 0.35f, boundsWeight = 3f,
            minSpeed = 0.03f, maxSpeed = 0.09f, maxSteerForce = 0.06f,
            boundsExtents = new Vector3(0.10f, 0.06f, 0.10f),
        };

        /// <summary>Stretched between the two cards, still unsettled: the "you are close" hint.</summary>
        public static SwarmBehaviour Bridged => new()
        {
            separationWeight = 1.4f, alignmentWeight = 1.1f, cohesionWeight = 0.9f, boundsWeight = 5f,
            minSpeed = 0.05f, maxSpeed = 0.13f, maxSteerForce = 0.10f,
            boundsExtents = new Vector3(0.09f, 0.05f, 0.09f),
        };

        /// <summary>Tight, fast stream pouring into the conduit.</summary>
        public static SwarmBehaviour Converged => new()
        {
            separationWeight = 0.7f, alignmentWeight = 1.8f, cohesionWeight = 2.2f, boundsWeight = 7f,
            minSpeed = 0.10f, maxSpeed = 0.22f, maxSteerForce = 0.18f,
            boundsExtents = new Vector3(0.06f, 0.035f, 0.06f),
        };
    }

    /// <summary>Designed difficulty, kept for reference and for the demo-tuning check.</summary>
    public const float DesignLinkDistance = 0.10f;
    public const float DesignAlignTolerance = 15f;
    public const float DesignIgnitionHold = 0.35f;

    // Shader properties on MAAYAI/Swarm/BoidIndirectLit. The swarm has no _EmissionColor: its glow
    // is the HDR speed rim, scaled by speed / _SpeedTintRange.
    static readonly int k_RimColor = Shader.PropertyToID("_RimColor");
    static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int k_SpeedTintRange = Shader.PropertyToID("_SpeedTintRange");

    [Header("Reference images (AR_Targets)")]
    [Tooltip("Petalo's anchor card. Must match ARBoidBridge's anchor.")]
    [SerializeField] string m_AnchorImage = "king of spades";
    [Tooltip("The Lumenforge conduit card.")]
    [SerializeField] string m_ConduitImage = "all_knowing_eye";
    [Tooltip("Every card the puzzle tracks. Names are matched ignoring case, spaces and underscores.")]
    [SerializeField] string[] m_PuzzleImages =
    {
        "king of spades", "queen_of_hearts", "joker", "valentoro", "all_knowing_eye",
    };

    // Demonstration defaults. The designed values are 0.10 m link, 15° tolerance and 0.35 s hold
    // (see DesignLinkDistance / DesignAlignTolerance / DesignIgnitionHold below); these are loosened
    // so hand occlusion during a capture cannot stall the ignition.
    [Header("Act 2 · proximity link")]
    [Tooltip("Link distance between the two card centres, in metres. Design value: 0.10. Demo value: 0.5.")]
    [SerializeField, Min(0.01f)] float m_LinkDistance = 0.5f;
    [Tooltip("Extra distance before an established link drops, so a shaky hand doesn't flicker the state.")]
    [SerializeField, Min(0f)] float m_UnlinkMargin = 0.02f;

    [Header("Act 3 · alignment")]
    [Tooltip("Yaw error allowed around the anchor card's normal, in degrees: a ±value cone. " +
             "Design value: 15 (30° cone). Demo value: 90, which accepts half of all card rotations.")]
    [SerializeField, Min(0f)] float m_AlignTolerance = 90f;
    [Tooltip("Yaw offset that counts as aligned, in degrees. 0 = both cards facing the same way.")]
    [SerializeField, Range(-180f, 180f)] float m_TargetYawOffset;
    [Tooltip("Seconds the alignment must hold before ignition. Design value: 0.35. Demo value: 0.05.")]
    [SerializeField, Min(0f)] float m_IgnitionHold = 0.05f;

    [Header("Feedback · GPU swarm only")]
    [ColorUsage(false, true)] [SerializeField] Color m_CyanGlow = new(0.10f, 3.2f, 4.0f);
    [ColorUsage(false, true)] [SerializeField] Color m_GoldGlow = new(4.4f, 2.4f, 0.5f);
    [SerializeField] Color m_CyanBase = new(0.10f, 0.20f, 0.26f);
    [SerializeField] Color m_GoldBase = new(0.28f, 0.20f, 0.07f);
    [Tooltip("Colour units per second while interpolating to the target state colour.")]
    [SerializeField, Min(0.1f)] float m_GlowLerpSpeed = 3f;
    [Tooltip("Multiplies the RGB of both glow colours before they reach the swarm material. " +
             "Values above 1 push the rim into HDR so URP Bloom picks it up.")]
    [SerializeField, Min(0f)] float m_EmissionIntensity = 4f;
    [Tooltip("Turns on post-processing for the AR camera at start-up. Without it URP renders no " +
             "bloom at all, so the HDR overdrive would be invisible. Costs a full-screen pass.")]
    [SerializeField] bool m_EnableCameraPostProcessing = true;

    [Header("Behaviour presets")]
    [SerializeField] SwarmBehaviour m_Scattered = SwarmBehaviour.Scattered;
    [SerializeField] SwarmBehaviour m_Bridged = SwarmBehaviour.Bridged;
    [SerializeField] SwarmBehaviour m_Converged = SwarmBehaviour.Converged;

    /// <summary>Raised whenever the puzzle state changes.</summary>
    public event Action<PuzzleState> StateChanged;

    /// <summary>Raised once each time the conduit ignites.</summary>
    public event Action LumenforgeIgnited;

    /// <summary>Current puzzle state.</summary>
    public PuzzleState State { get; private set; } = PuzzleState.Dormant;

    /// <summary>Cards currently tracked, keyed by their normalised reference-image name.</summary>
    public IReadOnlyDictionary<string, ARTrackedImage> TrackedCards => m_TrackedByName;

    /// <summary>Centre distance between anchor and conduit, in metres. Negative when either is untracked.</summary>
    public float LinkDistance { get; private set; } = -1f;

    /// <summary>Absolute yaw error against the target offset, in degrees. Negative when unlinked.</summary>
    public float YawError { get; private set; } = -1f;

    /// <summary>True while the two cards are inside link range.</summary>
    public bool IsLinked { get; private set; }

    /// <summary>
    /// True when the tuning is looser than the designed difficulty, so a recording made with these
    /// settings is not a demonstration of the shipped puzzle.
    /// </summary>
    public bool IsDemoTuned =>
        m_LinkDistance > DesignLinkDistance || m_AlignTolerance > DesignAlignTolerance || m_IgnitionHold < DesignIgnitionHold;

    readonly Dictionary<string, ARTrackedImage> m_TrackedByName = new(StringComparer.Ordinal);
    readonly Dictionary<TrackableId, string> m_NameById = new();

    ARTrackedImageManager m_Manager;
    ARBoidBridge m_Bridge;
    SwarmManager m_Swarm;
    Transform m_SwarmHost;
    Material m_SwarmMaterial;      // runtime clone; the asset is never touched
    Transform m_Anchor;
    Transform m_Conduit;

    string m_AnchorKey;
    string m_ConduitKey;
    HashSet<string> m_PuzzleKeys;

    float m_LinkSqr;
    float m_UnlinkSqr;
    float m_AlignedFor;
    Color m_Glow;
    Color m_Base;
    bool m_ColourSettled;

    void Awake()
    {
        m_Manager = GetComponent<ARTrackedImageManager>();
        m_Bridge = GetComponent<ARBoidBridge>();

        m_AnchorKey = Normalize(m_AnchorImage);
        m_ConduitKey = Normalize(m_ConduitImage);
        m_PuzzleKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in m_PuzzleImages)
        {
            var key = Normalize(image);
            if (key.Length > 0)
                m_PuzzleKeys.Add(key);
        }

        // Keys the puzzle needs are always tracked, even if the inspector list is edited down.
        m_PuzzleKeys.Add(m_AnchorKey);
        m_PuzzleKeys.Add(m_ConduitKey);

        CacheThresholds();
        m_Glow = Overdrive(m_CyanGlow);
        m_Base = m_CyanBase;

        if (m_EnableCameraPostProcessing)
            EnableCameraPostProcessing();

        if (IsDemoTuned)
        {
            Debug.LogWarning(
                $"{nameof(PuzzleStateController)}: demo tuning active. Link {m_LinkDistance:0.##} m " +
                $"(design {DesignLinkDistance:0.##}), tolerance ±{m_AlignTolerance:0}° (design ±{DesignAlignTolerance:0}°), " +
                $"hold {m_IgnitionHold:0.##} s (design {DesignIgnitionHold:0.##}). The puzzle solves far more easily " +
                "than the shipped design; label any capture made with this build accordingly.", this);
        }

        if (m_Bridge != null && !string.Equals(Normalize(m_Bridge.AnchorImageName), m_AnchorKey, StringComparison.Ordinal))
        {
            Debug.LogWarning(
                $"{nameof(PuzzleStateController)}: anchor '{m_AnchorImage}' does not match {nameof(ARBoidBridge)}'s " +
                $"'{m_Bridge.AnchorImageName}'. The swarm will spawn on a different card than the puzzle reads.", this);
        }
    }

    void OnValidate()
    {
        CacheThresholds();
        m_ColourSettled = false;   // re-drive the material after an inspector tweak
    }

    /// <summary>HDR overdrive: scales RGB only, so alpha and the colour's hue stay intact.</summary>
    Color Overdrive(Color c) => new(c.r * m_EmissionIntensity, c.g * m_EmissionIntensity, c.b * m_EmissionIntensity, c.a);

    /// <summary>
    /// URP only runs Bloom when the camera has post-processing enabled. The AR camera is created
    /// without UniversalAdditionalCameraData, so this adds it and switches the pass on.
    /// </summary>
    void EnableCameraPostProcessing()
    {
        var camera = GetComponent<Unity.XR.CoreUtils.XROrigin>()?.Camera;
        if (camera == null)
            return;

        var data = camera.GetUniversalAdditionalCameraData();
        if (data == null || data.renderPostProcessing)
            return;

        data.renderPostProcessing = true;
        Debug.Log($"{nameof(PuzzleStateController)}: enabled post-processing on '{camera.name}' so the swarm's HDR rim blooms.", this);
    }

    void CacheThresholds()
    {
        m_LinkSqr = m_LinkDistance * m_LinkDistance;
        var unlink = m_LinkDistance + m_UnlinkMargin;
        m_UnlinkSqr = unlink * unlink;
    }

    void OnEnable()
    {
        m_Manager.trackablesChanged.AddListener(OnTrackablesChanged);
        if (m_Bridge != null)
        {
            m_Bridge.SwarmChanged += OnSwarmChanged;
            OnSwarmChanged(m_Bridge.ActiveSwarm);
        }

        // Adopt anything already detected before this component woke up.
        foreach (var image in m_Manager.trackables)
            Register(image);
        RefreshEndpoints();
    }

    void OnDisable()
    {
        m_Manager.trackablesChanged.RemoveListener(OnTrackablesChanged);
        if (m_Bridge != null)
            m_Bridge.SwarmChanged -= OnSwarmChanged;

        m_TrackedByName.Clear();
        m_NameById.Clear();
        m_Anchor = m_Conduit = null;
    }

    void OnDestroy()
    {
        if (m_SwarmMaterial != null)
            Destroy(m_SwarmMaterial);
    }

    // ------------------------------------------------------------------ tracking

    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> changes)
    {
        var dirty = false;
        foreach (var image in changes.added)
            dirty |= Register(image);

        foreach (var image in changes.updated)
        {
            // A card that drops to Limited/None is treated as gone: stale poses must not solve the puzzle.
            if (!m_NameById.TryGetValue(image.trackableId, out var key))
                continue;

            var tracking = image.trackingState == TrackingState.Tracking;
            var known = m_TrackedByName.ContainsKey(key);
            if (tracking && !known)
            {
                m_TrackedByName[key] = image;
                dirty = true;
            }
            else if (!tracking && known)
            {
                m_TrackedByName.Remove(key);
                dirty = true;
            }
        }

        foreach (var removed in changes.removed)
        {
            if (!m_NameById.Remove(removed.Key, out var key))
                continue;
            dirty |= m_TrackedByName.Remove(key);
        }

        if (dirty)
            RefreshEndpoints();
    }

    bool Register(ARTrackedImage image)
    {
        if (image == null)
            return false;

        var key = Normalize(image.referenceImage.name);
        if (!m_PuzzleKeys.Contains(key))
            return false;

        m_NameById[image.trackableId] = key;
        if (image.trackingState != TrackingState.Tracking)
            return false;

        m_TrackedByName[key] = image;
        return true;
    }

    void RefreshEndpoints()
    {
        m_Anchor = m_TrackedByName.TryGetValue(m_AnchorKey, out var anchor) && anchor != null ? anchor.transform : null;
        m_Conduit = m_TrackedByName.TryGetValue(m_ConduitKey, out var conduit) && conduit != null ? conduit.transform : null;
    }

    void OnSwarmChanged(SwarmManager swarm)
    {
        m_Swarm = swarm;
        m_SwarmHost = swarm != null ? swarm.transform : null;
        if (swarm == null)
            return;

        // Clone the material so the shared asset is never edited, and make the HDR rim readable at
        // tabletop speeds: the stock _SpeedTintRange of 6 leaves the glow invisible below 1 m/s.
        if (m_SwarmMaterial == null && swarm.boidMaterial != null)
            m_SwarmMaterial = new Material(swarm.boidMaterial) { name = swarm.boidMaterial.name + " (Puzzle)" };

        if (m_SwarmMaterial != null)
        {
            swarm.boidMaterial = m_SwarmMaterial;
            m_SwarmMaterial.SetFloat(k_SpeedTintRange, Mathf.Max(0.05f, m_Converged.maxSpeed));
        }

        ApplyBehaviour(BehaviourFor(State));
        m_ColourSettled = false;
    }

    // ------------------------------------------------------------------ loop

    void Update()
    {
        var next = Evaluate();
        if (next != State)
        {
            var previous = State;
            State = next;
            ApplyBehaviour(BehaviourFor(next));
            m_ColourSettled = false;
            StateChanged?.Invoke(next);

            if (next == PuzzleState.Ignited && previous != PuzzleState.Ignited)
                LumenforgeIgnited?.Invoke();
        }

        PositionSwarm();
        DriveColour();
    }

    PuzzleState Evaluate()
    {
        if (m_Anchor == null || m_Conduit == null)
        {
            IsLinked = false;
            LinkDistance = -1f;
            YawError = -1f;
            m_AlignedFor = 0f;
            return PuzzleState.Dormant;
        }

        // Act 2: squared distance only; no square root on the hot path.
        var delta = m_Conduit.position - m_Anchor.position;
        var sqr = delta.sqrMagnitude;
        IsLinked = sqr <= (IsLinked ? m_UnlinkSqr : m_LinkSqr);
        LinkDistance = Mathf.Sqrt(sqr);

        if (!IsLinked)
        {
            YawError = -1f;
            m_AlignedFor = 0f;
            return PuzzleState.Dormant;
        }

        // Act 3: yaw around the anchor card's normal (+Y points out of a tracked image).
        var signed = Vector3.SignedAngle(m_Anchor.forward, m_Conduit.forward, m_Anchor.up) - m_TargetYawOffset;
        YawError = Mathf.Abs(Mathf.DeltaAngle(0f, signed));

        if (YawError > m_AlignTolerance)
        {
            m_AlignedFor = 0f;
            return PuzzleState.LinkedMisaligned;
        }

        m_AlignedFor += Time.deltaTime;
        return m_AlignedFor >= m_IgnitionHold ? PuzzleState.Ignited : PuzzleState.LinkedMisaligned;
    }

    SwarmBehaviour BehaviourFor(PuzzleState state) => state switch
    {
        PuzzleState.Ignited => m_Converged,
        PuzzleState.LinkedMisaligned => m_Bridged,
        _ => m_Scattered,
    };

    void ApplyBehaviour(in SwarmBehaviour b)
    {
        if (m_Swarm == null)
            return;

        m_Swarm.separationWeight = b.separationWeight;
        m_Swarm.alignmentWeight = b.alignmentWeight;
        m_Swarm.cohesionWeight = b.cohesionWeight;
        m_Swarm.boundsWeight = b.boundsWeight;
        m_Swarm.minSpeed = b.minSpeed;
        m_Swarm.maxSpeed = b.maxSpeed;
        m_Swarm.maxSteerForce = b.maxSteerForce;
        m_Swarm.boundsExtents = b.boundsExtents;
    }

    /// <summary>
    /// The swarm's simulation volume is centred on its host transform, so moving the host is what
    /// makes the swarm bridge the two cards. Unlinked, it sits over the anchor card.
    /// </summary>
    void PositionSwarm()
    {
        if (m_SwarmHost == null || m_Anchor == null)
            return;

        if (!IsLinked || m_Conduit == null)
        {
            m_SwarmHost.localPosition = new Vector3(0f, Mathf.Abs(m_Scattered.boundsExtents.y), 0f);
            return;
        }

        var mid = (m_Anchor.position + m_Conduit.position) * 0.5f + m_Anchor.up * m_Bridged.boundsExtents.y;
        m_SwarmHost.position = mid;

        // Stretch the volume along the link so the boids can actually span both cards.
        var half = LinkDistance * 0.5f;
        var b = BehaviourFor(State);
        m_Swarm.boundsExtents = new Vector3(
            Mathf.Max(b.boundsExtents.x, half + 0.03f),
            b.boundsExtents.y,
            Mathf.Max(b.boundsExtents.z, half + 0.03f));
    }

    /// <summary>Cyan while unsolved, gold once ignited. Stops assigning once the colour arrives.</summary>
    void DriveColour()
    {
        if (m_SwarmMaterial == null || m_ColourSettled)
            return;

        var targetGlow = Overdrive(State == PuzzleState.Ignited ? m_GoldGlow : m_CyanGlow);
        var targetBase = State == PuzzleState.Ignited ? m_GoldBase : m_CyanBase;
        var step = m_GlowLerpSpeed * Time.deltaTime;

        m_Glow = Color.Lerp(m_Glow, targetGlow, step);
        m_Base = Color.Lerp(m_Base, targetBase, step);
        m_SwarmMaterial.SetColor(k_RimColor, m_Glow);
        m_SwarmMaterial.SetColor(k_BaseColor, m_Base);

        // Within one 8-bit step of the target: snap and stop touching the material.
        if (Mathf.Abs(m_Glow.r - targetGlow.r) + Mathf.Abs(m_Glow.g - targetGlow.g) + Mathf.Abs(m_Glow.b - targetGlow.b) < 0.004f)
        {
            m_Glow = targetGlow;
            m_Base = targetBase;
            m_SwarmMaterial.SetColor(k_RimColor, m_Glow);
            m_SwarmMaterial.SetColor(k_BaseColor, m_Base);
            m_ColourSettled = true;
        }
    }

    static string Normalize(string name) =>
        string.IsNullOrEmpty(name) ? string.Empty : name.Trim().Replace('_', ' ').ToLowerInvariant();

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (m_Anchor == null || m_Conduit == null)
            return;

        Gizmos.color = State == PuzzleState.Ignited ? new Color(1f, 0.72f, 0.2f) : new Color(0.1f, 0.85f, 1f);
        Gizmos.DrawLine(m_Anchor.position, m_Conduit.position);
        Gizmos.DrawWireSphere(m_Anchor.position, m_LinkDistance);
    }
#endif
}
