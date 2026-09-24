using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Follows the swarm's anchor card, so world content (the Mystery Cave diorama, the Lumenforge, Petalo) sits on
    /// the physical card at tabletop scale. Hidden while the card is not tracked, matching the GDD's rule that
    /// losing the card is never a fail state: the content pauses and returns when the card is seen again.
    ///
    /// Never parented to the trackable itself: AR Foundation destroys trackables, and that would take the content
    /// with it. Pose is copied each frame with the same smoothing the swarm uses.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CardAnchoredContent : MonoBehaviour
    {
        [SerializeField] SwarmGPUArchitect swarm;
        [Tooltip("Offset from the card's centre, in card-local metres (Y is the card's surface normal).")]
        [SerializeField] Vector3 localOffset = Vector3.zero;
        [Tooltip("Pose follow rate (1/s): smooths AR tracking jitter.")]
        [SerializeField, Min(1f)] float followRate = 20f;
        [Tooltip("Hide the content whenever the anchor card is not tracked.")]
        [SerializeField] bool hideWhenUntracked = true;

        Transform content;
        bool snapped;

        void Awake()
        {
            if (swarm == null) swarm = FindAnyObjectByType<SwarmGPUArchitect>();
            content = transform;
            if (swarm == null)
                Debug.LogWarning("[CardAnchoredContent] No SwarmGPUArchitect: content will not follow a card.", this);
        }

        void LateUpdate()
        {
            if (swarm == null) return;

            Transform anchor = swarm.Anchor;
            bool tracked = swarm.IsAnchorTracking;

            if (hideWhenUntracked)
            {
                for (int i = 0; i < content.childCount; i++)
                {
                    var child = content.GetChild(i).gameObject;
                    if (child.activeSelf != tracked) child.SetActive(tracked);
                }
            }

            if (anchor == null || !tracked)
            {
                snapped = false;
                return;
            }

            Vector3 targetPos = anchor.TransformPoint(localOffset);
            Quaternion targetRot = anchor.rotation;

            if (!snapped)
            {
                content.SetPositionAndRotation(targetPos, targetRot);
                snapped = true;
                return;
            }

            float t = 1f - Mathf.Exp(-followRate * Time.deltaTime);
            content.SetPositionAndRotation(
                Vector3.Lerp(content.position, targetPos, t),
                Quaternion.Slerp(content.rotation, targetRot, t));
        }
    }
}
