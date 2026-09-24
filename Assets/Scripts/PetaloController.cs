using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

// Tap/click-to-move for Petalo. Uses the Input System (legacy input is disabled in this project).
// While travelling it owns position and scale; an idle ProceduralHop, if present, is paused.
[DisallowMultipleComponent]
[AddComponentMenu("Funobotz/Petalo Controller")]
public sealed class PetaloController : MonoBehaviour
{
    public const string FloorLayerName = "AR_Floor";
    public const string PlayerTag = "Player";

    [SerializeField, Min(0.05f)] float m_MoveDuration = 0.75f;
    [SerializeField, Min(0f)] float m_HopHeight = 0.06f;
    [SerializeField, Range(0f, 0.6f)] float m_Stretch = 0.22f;
    [SerializeField, Range(0f, 0.6f)] float m_Squash = 0.3f;
    [SerializeField, Range(0.01f, 0.5f)] float m_ContactWindow = 0.14f;
    [SerializeField, Min(0.1f)] float m_MaxRayDistance = 5f;

    Camera m_Camera;
    int m_FloorMask;
    ProceduralHop m_IdleHop;
    Coroutine m_Move;
    Vector3 m_RestScale;

    public bool IsMoving => m_Move != null;

    void Awake()
    {
        gameObject.tag = PlayerTag;
        m_RestScale = transform.localScale;
        m_FloorMask = LayerMask.GetMask(FloorLayerName);
        m_IdleHop = GetComponent<ProceduralHop>();

        if (m_FloorMask == 0)
            Debug.LogWarning($"{nameof(PetaloController)}: layer '{FloorLayerName}' does not exist; taps cannot hit the floor.", this);
    }

    void Update()
    {
        var pointer = Pointer.current;
        if (pointer == null || !pointer.press.wasPressedThisFrame || m_FloorMask == 0)
            return;

        if (m_Camera == null)
            m_Camera = Camera.main;
        if (m_Camera == null)
            return;

        var ray = m_Camera.ScreenPointToRay(pointer.position.ReadValue());
        if (!Physics.Raycast(ray, out var hit, m_MaxRayDistance, m_FloorMask, QueryTriggerInteraction.Ignore))
            return;

        if (m_Move != null)
            StopCoroutine(m_Move);
        m_Move = StartCoroutine(HopTo(hit.point, hit.normal));
    }

    IEnumerator HopTo(Vector3 floorPoint, Vector3 floorNormal)
    {
        if (m_IdleHop != null)
        {
            m_IdleHop.SetRestLocalPosition(transform.localPosition);
            m_IdleHop.enabled = false;   // restores rest scale before we take over
        }

        var parentScaleY = transform.parent != null ? transform.parent.lossyScale.y : 1f;
        var restHeight = m_RestScale.y * parentScaleY;

        // The pivot is the body's centre, so the target sits half a body above the floor.
        var start = transform.position;
        var end = floorPoint + floorNormal * (restHeight * 0.5f);

        var elapsed = 0f;
        while (elapsed < m_MoveDuration)
        {
            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / m_MoveDuration);

            // Arc: sin(progress·π) is 0 at take-off and landing, 1 at the apex.
            var arc = Mathf.Sin(progress * Mathf.PI);
            // Vertical speed is proportional to |cos(progress·π)|: fastest at the ends, still at the apex.
            var speed = Mathf.Abs(Mathf.Cos(progress * Mathf.PI));
            // Ground contact fades in over the first and last slice of the move.
            var contact = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Min(progress, 1f - progress) / m_ContactWindow);

            var yFactor = 1f + m_Stretch * speed * (1f - contact) - m_Squash * contact;
            var xzFactor = 1f / Mathf.Sqrt(Mathf.Max(0.05f, yFactor));   // volume preserving
            transform.localScale = new Vector3(m_RestScale.x * xzFactor, m_RestScale.y * yFactor, m_RestScale.z * xzFactor);

            // Keep the feet on the floor while the body compresses or extends.
            var footCorrection = (yFactor - 1f) * restHeight * 0.5f;
            transform.position = Vector3.Lerp(start, end, progress)
                                 + Vector3.up * (arc * m_HopHeight + footCorrection);
            yield return null;
        }

        transform.position = end;
        transform.localScale = m_RestScale;

        if (m_IdleHop != null)
        {
            m_IdleHop.SetRestLocalPosition(transform.localPosition);
            m_IdleHop.enabled = true;
        }

        m_Move = null;
    }

    void OnDisable()
    {
        if (m_Move == null)
            return;

        StopCoroutine(m_Move);
        m_Move = null;
        transform.localScale = m_RestScale;
        if (m_IdleHop != null)
            m_IdleHop.enabled = true;
    }
}
