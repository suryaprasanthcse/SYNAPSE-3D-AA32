using MAAYAI.Matrix.Swarm;
using UnityEngine;

/// <summary>
/// Makes Petalo's GPU swarm pour out of her two eyes. Lives on the Petalo hologram prefab.
/// </summary>
/// <remarks>
/// The swarm keeps a fixed population, so "birth" means recycling: every frame a slice of the
/// boid ring buffer is rewritten at the two eye points with a launch velocity, through
/// <see cref="SwarmManager.Emit"/>. On first contact the whole population is flushed through the
/// eyes, so from then on every boid on screen was born at an eye. The flocking rules then shape
/// the streams; the compute pipeline is unchanged.
///
/// Per frame: two TransformPoint calls and one Emit (at most two small SetData uploads).
/// </remarks>
[DisallowMultipleComponent]
[AddComponentMenu("Funobotz/Eye Emitter Bridge")]
public sealed class EyeEmitterBridge : MonoBehaviour
{
    [Tooltip("The Petalo quad the eye offsets are measured on. Found automatically as a child named 'Quad'.")]
    [SerializeField] Transform m_Quad;

    // Measured from Assets/petalo_transparent.png (682 x 1024): LED centres at pixels (291, 199) and
    // (381, 198), converted to Unity Quad space where x, y run from -0.5 to 0.5 and the visible
    // face is -Z. The small negative Z lifts the emitters just in front of the image.
    [Tooltip("Left eye in the quad's local space.")]
    [SerializeField] Vector3 m_LeftEye = new(-0.0729f, 0.3054f, -0.05f);
    [Tooltip("Right eye in the quad's local space.")]
    [SerializeField] Vector3 m_RightEye = new(0.0592f, 0.3068f, -0.05f);

    [Header("Streams")]
    [Tooltip("Boids re-born at the eyes per second, shared between both eyes.")]
    [SerializeField, Min(0f)] float m_EmitPerSecond = 900f;
    [Tooltip("Launch speed in metres per second. The swarm's own speed limits still apply.")]
    [SerializeField, Min(0f)] float m_LaunchSpeed = 0.12f;
    [Tooltip("How far the streams tilt upward from straight out of the face (0 = straight out, 1 = straight up).")]
    [SerializeField, Range(0f, 1f)] float m_UpwardTilt = 0.35f;
    [Tooltip("Scatter radius around each eye, in metres. Small values give tight, concentrated streams.")]
    [SerializeField, Min(0f)] float m_Jitter = 0.002f;
    [Tooltip("Re-birth the entire population at the eyes as soon as the swarm is ready.")]
    [SerializeField] bool m_FlushOnAcquire = true;
    [Tooltip("Emission multiplier while the puzzle is Ignited. Lower keeps boids alive long enough " +
             "to flow across to the conduit card instead of being recycled back to the eyes.")]
    [SerializeField, Range(0f, 1f)] float m_IgnitedEmitScale = 0.25f;

    readonly Vector3[] m_Points = new Vector3[2];
    PuzzleStateController m_Puzzle;
    ARBoidBridge m_Bridge;
    SwarmManager m_Swarm;
    float m_Budget;
    bool m_Flushed;

    void Reset() => m_Quad = transform.Find("Quad");

    void Awake()
    {
        if (m_Quad == null)
            m_Quad = transform.Find("Quad");
        if (m_Quad == null)
            m_Quad = transform;
    }

    void OnEnable()
    {
        // Tracked images are parented under the XR Origin, which carries the bridge.
        m_Bridge = GetComponentInParent<ARBoidBridge>(true);
        if (m_Bridge == null)
            m_Bridge = FindAnyObjectByType<ARBoidBridge>();
        m_Puzzle = m_Bridge != null ? m_Bridge.GetComponent<PuzzleStateController>() : null;

        if (m_Bridge != null)
        {
            m_Bridge.SwarmChanged += OnSwarmChanged;
            OnSwarmChanged(m_Bridge.ActiveSwarm);
        }
        else
        {
            Debug.LogWarning($"{nameof(EyeEmitterBridge)}: no {nameof(ARBoidBridge)} in the scene; the eyes have no swarm to emit.", this);
        }
    }

    void OnDisable()
    {
        if (m_Bridge != null)
            m_Bridge.SwarmChanged -= OnSwarmChanged;
        m_Swarm = null;
    }

    void OnSwarmChanged(SwarmManager swarm)
    {
        m_Swarm = swarm;
        m_Flushed = false;
        m_Budget = 0f;
    }

    void LateUpdate()
    {
        // The swarm host is deactivated while its card is lost; emitting into a paused swarm is wasted work.
        if (m_Swarm == null || !m_Swarm.isActiveAndEnabled || !m_Swarm.IsReady)
            return;

        m_Points[0] = m_Quad.TransformPoint(m_LeftEye);
        m_Points[1] = m_Quad.TransformPoint(m_RightEye);

        // Out of the face (the quad's visible side is -Z), tilted up so streams rise off the card.
        var outward = -m_Quad.forward;
        var direction = Vector3.Slerp(outward, m_Quad.up, m_UpwardTilt);
        var velocity = direction * m_LaunchSpeed;

        int count;
        if (m_FlushOnAcquire && !m_Flushed)
        {
            count = m_Swarm.boidCount;
            m_Flushed = true;
        }
        else
        {
            var rate = m_EmitPerSecond;
            if (m_Puzzle != null && m_Puzzle.State == PuzzleStateController.PuzzleState.Ignited)
                rate *= m_IgnitedEmitScale;

            m_Budget += rate * Time.deltaTime;
            count = (int)m_Budget;
            if (count <= 0)
                return;
            m_Budget -= count;
        }

        m_Swarm.Emit(m_Points, 2, velocity, count, m_Jitter);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        var quad = m_Quad != null ? m_Quad : transform.Find("Quad") ?? transform;
        Gizmos.color = new Color(0.1f, 0.85f, 1f);
        var radius = 0.004f;
        Gizmos.DrawSphere(quad.TransformPoint(m_LeftEye), radius);
        Gizmos.DrawSphere(quad.TransformPoint(m_RightEye), radius);
    }
#endif
}
