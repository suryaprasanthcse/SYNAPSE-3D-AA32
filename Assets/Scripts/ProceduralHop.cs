using UnityEngine;

/// <summary>
/// Continuous hop on the local Y axis with volume-preserving squash and stretch.
/// </summary>
/// <remarks>
/// Height follows |sin(t · hopSpeed)|, so each hop is a sine arch that touches down at zero.
/// Its vertical speed is proportional to |cos(t · hopSpeed)|: fastest at take-off and landing,
/// zero at the apex. The body stretches with that speed while airborne and squashes in a short
/// window around touch-down. Width and depth scale by 1/√(height factor), so volume is conserved.
/// Uses unscaled local transforms only and allocates nothing per frame.
/// </remarks>
[DisallowMultipleComponent]
[AddComponentMenu("Funobotz/Procedural Hop")]
public sealed class ProceduralHop : MonoBehaviour
{
    [Tooltip("Hops per second, as angular speed of the sine in radians per second.")]
    [SerializeField, Min(0f)] float m_HopSpeed = 4f;

    [Tooltip("Peak lift above the rest position, as a fraction of the object's rest height (local scale Y).")]
    [SerializeField, Min(0f)] float m_HopHeight = 0.35f;

    [Tooltip("Extra Y scale at full vertical speed while airborne. 0.2 = 20% taller.")]
    [SerializeField, Range(0f, 0.6f)] float m_Stretch = 0.18f;

    [Tooltip("Y scale lost at touch-down. 0.3 = 30% shorter.")]
    [SerializeField, Range(0f, 0.6f)] float m_Squash = 0.28f;

    [Tooltip("How much of each hop (in |sin| units) counts as ground contact for the squash.")]
    [SerializeField, Range(0.01f, 0.5f)] float m_ContactWindow = 0.18f;

    [Tooltip("Randomise the phase so several hopping objects don't move in lockstep.")]
    [SerializeField] bool m_RandomPhase = true;

    Vector3 m_StartPosition;
    Vector3 m_StartScale;
    float m_Phase;

    /// <summary>Rest local position cached on Awake.</summary>
    public Vector3 StartPosition => m_StartPosition;

    /// <summary>Rest local scale cached on Awake.</summary>
    public Vector3 StartScale => m_StartScale;

    /// <summary>Moves the spot the hop bounces around, e.g. after the object travels somewhere new.</summary>
    public void SetRestLocalPosition(Vector3 localPosition) => m_StartPosition = localPosition;

    void Awake()
    {
        m_StartPosition = transform.localPosition;
        m_StartScale = transform.localScale;
        m_Phase = m_RandomPhase ? Random.value * Mathf.PI : 0f;
    }

    void OnDisable()
    {
        // Leave the object exactly at rest if the component is switched off mid-hop.
        transform.localPosition = m_StartPosition;
        transform.localScale = m_StartScale;
    }

    void Update()
    {
        var angle = Time.time * m_HopSpeed + m_Phase;
        var height01 = Mathf.Abs(Mathf.Sin(angle));      // 0 on the ground, 1 at the apex
        var speed01 = Mathf.Abs(Mathf.Cos(angle));       // 1 at take-off/landing, 0 at the apex

        // Ground contact fades in over the last slice of the fall and the first of the rise.
        var contact = 1f - Mathf.SmoothStep(0f, 1f, height01 / m_ContactWindow);

        var yFactor = 1f + m_Stretch * speed01 * (1f - contact) - m_Squash * contact;
        var xzFactor = 1f / Mathf.Sqrt(Mathf.Max(0.05f, yFactor));

        transform.localScale = new Vector3(m_StartScale.x * xzFactor, m_StartScale.y * yFactor, m_StartScale.z * xzFactor);

        // Lift is measured in the object's own rest height, so it reads the same at any scale.
        var lift = height01 * m_HopHeight * m_StartScale.y;

        // Keep the feet planted while squashing: the bottom edge stays at the rest position.
        var squashDrop = (1f - yFactor) * m_StartScale.y * 0.5f;
        transform.localPosition = m_StartPosition + new Vector3(0f, lift - squashDrop, 0f);
    }
}
