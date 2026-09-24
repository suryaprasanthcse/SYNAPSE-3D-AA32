using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Re-points the swarm's anchor from the printed card to Petalo itself.
    ///
    /// <see cref="SwarmGPUArchitect"/> already knows how to follow an arbitrary transform and to pause when that
    /// transform stops being trustworthy - that machinery was written for a tracked image, and none of it cares
    /// that the pose now comes from a character the player is driving. So the card is simply replaced as the
    /// anchor rather than the placement system being rewritten.
    ///
    /// The swarm keeps its own world-space bounds: it is a cloud around Petalo at real tabletop scale, not
    /// something that shrinks with the world root's table fit.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Funobotz/Swarm Tether")]
    public sealed class SwarmTether : MonoBehaviour
    {
        [SerializeField] SwarmGPUArchitect swarm;
        [Tooltip("Transform the swarm follows. Left empty, this object is used.")]
        [SerializeField] Transform target;
        [Tooltip("Gate the swarm on the world having found a surface, so it is not lit up in empty space.")]
        [SerializeField] PlaneAnchoredWorld world;

        [Header("Aura")]
        [Tooltip("Size the swarm volume to Petalo instead of leaving it at the card-scale default. The swarm " +
                 "follows Petalo's pose but not its scale, so without this a 0.6 m cloud hangs around a 2 cm " +
                 "avatar and covers the whole table, including any cards on it.")]
        [SerializeField] bool scaleToAvatar = true;
        [Tooltip("Cloud radius as a multiple of Petalo's world height.")]
        [SerializeField, Min(0.5f)] float auraRadiusInAvatars = 4f;
        [Tooltip("How flat the cloud is: vertical extent as a fraction of the horizontal one.")]
        [SerializeField, Range(0.1f, 1f)] float auraFlatten = 0.5f;
        [SerializeField] PetaloBeacon petalo;

        bool attached;
        float lastAvatarSize = -1f;
        float auraScale = 1f;
        float lastAuraScale = -1f;

        /// <summary>
        /// Multiplies the aura radius on top of the avatar fit. The act transition widens it so the swarm reads as
        /// a current crossing the room instead of a halo around the drone it has swallowed.
        /// </summary>
        public float AuraScale
        {
            get => auraScale;
            set => auraScale = Mathf.Clamp(value, 0.1f, 20f);
        }

        void Awake()
        {
            if (swarm == null) swarm = FindAnyObjectByType<SwarmGPUArchitect>();
            if (target == null) target = transform;
            if (world == null) world = FindAnyObjectByType<PlaneAnchoredWorld>();
            if (petalo == null) petalo = GetComponentInChildren<PetaloBeacon>(true);

            if (swarm == null)
                Debug.LogWarning("[SwarmTether] No SwarmGPUArchitect: Petalo will render but cast no swarm light.", this);
        }

        void OnDisable()
        {
            if (swarm != null && attached) swarm.DetachAnchor();
            attached = false;
        }

        void LateUpdate()
        {
            if (swarm == null) return;

            // Tracking is true once the world is standing on a real surface: that is this build's equivalent of
            // "the card is visible", and it keeps the GDD's rule that losing tracking pauses rather than fails.
            bool live = world == null || world.IsPlaced;

            // Petalo is the swarm's only anchor. SpatialMatrixCardRig would also write it whenever the King of
            // Spades is visible, so its driveSwarmAnchor is off; re-asserting here would not have been enough on
            // its own, because the swarm reads the anchor in Update and this runs in LateUpdate - the card won
            // whichever frame its trackable event landed last. Re-assigning the same transform is a no-op, so
            // this stays as a cheap guard rather than a fix.
            swarm.AttachToAnchor(target);
            attached = true;
            swarm.SetAnchorTracking(live);

            if (scaleToAvatar) MatchAuraToAvatar();
        }

        /// <summary>
        /// Keep the simulation volume proportional to Petalo's world size. Recomputed only when that size
        /// actually changes, because writing the bounds re-pushes the compute shader's static parameters.
        /// </summary>
        void MatchAuraToAvatar()
        {
            if (petalo == null) return;

            float avatar = petalo.transform.lossyScale.x;
            if (avatar <= 1e-5f) return;
            bool auraChanged = Mathf.Abs(auraScale - lastAuraScale) > 0.01f;
            if (!auraChanged && Mathf.Abs(avatar - lastAvatarSize) < avatar * 0.02f) return;   // 2% hysteresis

            lastAvatarSize = avatar;
            lastAuraScale = auraScale;
            float radius = avatar * auraRadiusInAvatars * auraScale;
            swarm.BoundsExtents = new Vector3(radius, radius * auraFlatten, radius);
        }
    }
}
