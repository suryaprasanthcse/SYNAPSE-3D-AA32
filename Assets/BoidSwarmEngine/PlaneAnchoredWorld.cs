using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Floods a physical table with the world. This is the successor to CardAnchoredContent: instead of hanging
    /// the experience off a single printed card, it takes the largest horizontal plane ARCore has found - the
    /// table, the floor, the desk - and lays the whole spatial map across it.
    ///
    /// The three acts are authored at the GDD's real dimensions (a 40 m gateway arch, a 60 m bridge, an 18 m
    /// cavern), so the root is scaled down until that span fits the surface that was actually detected. Tuning
    /// therefore happens in design metres, and the table decides the rest.
    ///
    /// The anchor is re-fitted while the player is still scanning, because ARCore grows a plane outward as it
    /// sees more of the table, and a world that snapped to the first 20 cm found would sit in a corner of it.
    /// It locks the moment the player starts driving: a world that kept re-centring under a moving character
    /// would be unplayable. <see cref="Rescan"/> re-opens it if the session is restarted.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Funobotz/Plane Anchored World")]
    public sealed class PlaneAnchoredWorld : MonoBehaviour
    {
        public enum FitMode
        {
            FitToSurface,   // shrink the whole map onto the detected table
            RoomScale       // fixed, large scale on the floor; acts are placed one at a time where the player stands
        }

        [Header("Mode")]
        [Tooltip("RoomScale ignores the plane's size: the world is laid on the floor at a fixed scale, and each act " +
                 "is placed in front of the player by the hybrid loop director.")]
        [SerializeField] FitMode fitMode = FitMode.FitToSurface;
        [Tooltip("World metres per design metre in RoomScale. 0.03 makes Petalo's 4 m design size the GDD's 12 cm drone.")]
        [SerializeField, Range(0.005f, 0.2f)] float roomScale = 0.03f;
        [Tooltip("RoomScale: a lower plane (the floor under a table) replaces the current one if it is this much lower (m).")]
        [SerializeField, Min(0.02f)] float floorPreferenceMargin = 0.2f;
        [Tooltip("RoomScale: distance (m) in front of the camera the world is first dropped at.")]
        [SerializeField, Min(0.2f)] float roomPlacementDistance = 1.0f;

        [Header("Wiring")]
        [SerializeField] ARPlaneManager planeManager;
        [Tooltip("Needed for the lock to mean anything physical. Without an anchor the world is fixed to Unity's " +
                 "origin rather than to the room, and the session re-estimates that origin constantly.")]
        [SerializeField] ARAnchorManager anchorManager;
        [Tooltip("Hidden until a surface is found, so the world is never seen floating in space.")]
        [SerializeField] Transform content;

        [Header("Fit")]
        [Tooltip("Length of the authored world along its +Z axis, in design metres. The GDD's map runs about " +
                 "130 m from the Gateway plateau to the back wall of the cave.")]
        [SerializeField, Min(1f)] float worldSpanMetres = 130f;
        [Tooltip("Width of the authored world across its X axis, in design metres.")]
        [SerializeField, Min(1f)] float worldWidthMetres = 26f;
        [Tooltip("Fraction of the detected surface the world is allowed to cover. Under 1 leaves a visible margin " +
                 "so the player can see where the table ends.")]
        [SerializeField, Range(0.3f, 1f)] float surfaceFill = 0.9f;
        [Tooltip("Smallest surface worth using, in square metres. Rejects the scraps ARCore finds early on.")]
        [SerializeField, Min(0.01f)] float minimumPlaneArea = 0.08f;
        [Tooltip("Clamp on the final scale, so a huge floor does not produce a world the player cannot see across.")]
        [SerializeField] Vector2 scaleRange = new(0.002f, 0.05f);

        [Header("Behaviour")]
        [Tooltip("Re-fit while a better (larger) surface keeps being discovered.")]
        [SerializeField] bool refitWhileScanning = true;
        [Tooltip("A new surface must beat the current one by this factor before the world moves, so the map does " +
                 "not twitch between two similar planes.")]
        [SerializeField, Min(1f)] float refitAreaMargin = 1.25f;
        [Tooltip("Pose follow rate (1/s) while still fitting: smooths AR tracking jitter.")]
        [SerializeField, Min(1f)] float followRate = 8f;

        [Header("Hard Lock")]
        [Tooltip("Once locked, anchor re-estimates smaller than this (m) are ignored, so the world never shimmers " +
                 "while the phone is carried around it.")]
        [SerializeField, Min(0f)] float lockDeadbandMetres = 0.015f;
        [SerializeField, Min(0f)] float lockDeadbandDegrees = 1f;
        [Tooltip("How fast (1/s) a larger, genuine anchor correction is eased in.")]
        [SerializeField, Min(0.1f)] float lockCorrectionRate = 3f;

        [Header("Player Control")]
        [Tooltip("Multiplies the fitted scale. The plane fit decides how big the world is by default; this is " +
                 "the player deciding they want it bigger or smaller than that.")]
        [SerializeField, Range(0.1f, 20f)] float sizeMultiplier = 1f;

        ARPlane anchorPlane;
        float anchorArea;
        bool locked;
        bool placed;

        // The scale the plane fit asked for, before the player's multiplier. Kept apart so re-fitting does not
        // silently discard a resize, and resizing does not get overwritten by the next fit.
        float fittedScale = -1f;

        // Set once the player uses the lock button. After that their choice wins and nothing auto-locks over it.
        bool manualLock;

        // Card scanning: the pose is frozen outright while the player points the phone away at a physical card,
        // and plane detection is paused so no plane merge can drag the plane-attached anchor. On release the world
        // eases back onto the anchor instead of snapping to whatever correction accumulated meanwhile.
        bool scanHold;
        bool planeManagerWasEnabled;
        float reconvergeUntil;
        const float ReconvergeSeconds = 0.6f;

        // The room-registered pin. Unity world coordinates are not a fixed frame in AR: the session keeps
        // re-estimating where its origin sits as it learns the room, so content at constant coordinates drifts
        // with the device. An ARAnchor is the only thing the platform promises to hold over a real location, and
        // tracking corrections land on it instead of on the content.
        ARAnchor worldAnchor;

        /// <summary>The surface the world is currently laid across, or null while still scanning.</summary>
        public ARPlane AnchorPlane => anchorPlane;

        /// <summary>True once a surface has been found and the world is visible.</summary>
        public bool IsPlaced => placed;

        /// <summary>True once the anchor has stopped re-fitting.</summary>
        public bool IsLocked => locked;

        /// <summary>True once the player has set the lock themselves, which stops anything auto-locking over it.</summary>
        public bool ManualLockOverride => manualLock;

        /// <summary>The player's size multiplier on top of the plane fit.</summary>
        public float SizeMultiplier => sizeMultiplier;

        /// <summary>World metres per design metre at the current fit: the table scale the map was fitted at.</summary>
        public float WorldScale => transform.localScale.x;

        public FitMode Mode => fitMode;

        /// <summary>True while the pose is frozen for a card scan.</summary>
        public bool IsHeldForScan => scanHold;

        /// <summary>World height of the surface the world stands on.</summary>
        public float SurfaceHeight => anchorPlane != null ? anchorPlane.transform.position.y : transform.position.y;

        void Awake()
        {
            if (planeManager == null) planeManager = FindAnyObjectByType<ARPlaneManager>();
            if (anchorManager == null) anchorManager = FindAnyObjectByType<ARAnchorManager>();
            if (content == null) content = transform;

            if (anchorManager == null)
                Debug.LogWarning("[PlaneAnchoredWorld] No ARAnchorManager: locking will hold the world at fixed " +
                                 "Unity coordinates, which drift against the room as tracking refines.", this);

            if (planeManager == null)
                Debug.LogError("[PlaneAnchoredWorld] No ARPlaneManager in the scene: no surface can ever be found.", this);

            SetContentVisible(false);
        }

        void OnEnable()
        {
            if (planeManager == null) return;
            planeManager.trackablesChanged.AddListener(OnPlanesChanged);
            foreach (var plane in planeManager.trackables) Consider(plane);
        }

        void OnDisable()
        {
            if (planeManager != null) planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
        }

        void LateUpdate()
        {
            if (locked)
            {
                // Frozen for a card scan: nothing moves the world, whatever the session re-estimates meanwhile.
                if (scanHold) return;

                // The anchor was lost, or never took: re-pin where the world is now.
                if (AnchorNeedsRepair)
                {
                    worldAnchor = null;
                    CreateAnchor();
                }

                // Locked and anchored: the anchor is the truth. AR Foundation corrects its pose as the session
                // re-localises, and copying that onto the world is what keeps the map nailed to the real table
                // while the player walks around it.
                // Hard lock. The player walks the phone all around the world to look at it, and nothing about that
                // may make it move:
                //  - Only a FULLY tracked anchor is listened to. Sweeping the phone fast drops ARCore to Limited,
                //    and the anchor pose it reports then is a guess that snaps back a moment later.
                //  - Re-estimates smaller than the dead band are ignored outright, so the world never shimmers.
                //  - A real correction (the session relocalising) is eased in, never snapped.
                if (worldAnchor != null && worldAnchor.trackingState == TrackingState.Tracking)
                {
                    Vector3 p = worldAnchor.transform.position;
                    Quaternion r = worldAnchor.transform.rotation;
                    bool reconverging = Time.time < reconvergeUntil;

                    float offset = Vector3.Distance(transform.position, p);
                    float twist = Quaternion.Angle(transform.rotation, r);
                    if (!reconverging && offset < lockDeadbandMetres && twist < lockDeadbandDegrees) return;

                    float k = 1f - Mathf.Exp(-(reconverging ? 10f : lockCorrectionRate) * Time.deltaTime);
                    transform.SetPositionAndRotation(Vector3.Lerp(transform.position, p, k),
                                                     Quaternion.Slerp(transform.rotation, r, k));
                }
                return;
            }

            if (anchorPlane == null) return;

            // Room scale is placed deliberately, by the director, in front of the player. It never drifts after
            // the plane the way a table fit does.
            if (fitMode == FitMode.RoomScale) return;

            // Unlocked, the world tracks the plane - and a plane's centre migrates toward whatever the camera has
            // been looking at, because that is where the session keeps extending it. Correct while still choosing
            // a surface, and exactly wrong once the player wants to stand still and inspect the map.
            if (refitWhileScanning) Fit(anchorPlane, snap: false);
        }

        /// <summary>
        /// Stop re-fitting. Called the moment the player takes control, so the ground cannot move under them.
        /// </summary>
        public void Lock()
        {
            locked = true;
            CreateAnchor();
        }

        /// <summary>
        /// The player's own lock. Locked means the world stops re-fitting and stops following the plane entirely,
        /// so they can walk around it and inspect it from any angle without it sliding or rescaling under them.
        /// Unlocking hands it back to the plane fit.
        /// </summary>
        public void SetLocked(bool value)
        {
            locked = value;
            manualLock = true;

            if (value)
            {
                CreateAnchor();
            }
            else
            {
                ClearAnchor();
                placed = true;              // unlocking re-opens the fit without hiding the world again
            }
        }

        /// <summary>True when the lock is backed by a real anchor rather than just frozen coordinates.</summary>
        public bool IsAnchored => worldAnchor != null;

        /// <summary>
        /// Pin the world at its current pose. Attached to the plane it was fitted to, so the platform keeps the
        /// two consistent. Re-created rather than moved whenever the world is deliberately repositioned, because
        /// an anchor pose belongs to the session and is not ours to write.
        /// </summary>
        // Anchor requests are asynchronous. Each one carries a generation number; a result that comes back after
        // the world has been moved or unlocked again belongs to a pose nobody wants any more and is discarded.
        int anchorGeneration;
        bool anchorPending;
        int anchorFailures;
        float anchorRetryAt;
        const int MaxAnchorFailures = 3;

        /// <summary>
        /// A free-standing anchor, not one attached to a plane: ARCore merges planes as it learns the floor, and
        /// a merged-away ARPlane is destroyed together with anything attached to it. A free anchor holds the same
        /// real location whichever plane survives.
        /// </summary>
        void CreateAnchor()
        {
            if (anchorManager == null || !anchorManager.enabled) return;
            ClearAnchor();
            RequestAnchor(new Pose(transform.position, transform.rotation), anchorGeneration);
        }

        async void RequestAnchor(Pose pose, int generation)
        {
            anchorPending = true;
            Result<ARAnchor> result;
            try
            {
                result = await anchorManager.TryAddAnchorAsync(pose);
            }
            catch (System.Exception e)
            {
                if (generation == anchorGeneration) FailAnchor(e.Message);
                return;
            }

            bool success = result.status.IsSuccess() && result.value != null;
            if (this == null || generation != anchorGeneration)
            {
                if (success) Destroy(result.value.gameObject);
                return;
            }

            if (!success)
            {
                FailAnchor(result.status.ToString());
                return;
            }

            anchorPending = false;
            anchorFailures = 0;
            worldAnchor = result.value;
        }

        void FailAnchor(string reason)
        {
            anchorPending = false;
            anchorFailures++;
            anchorRetryAt = Time.time + 2f;
            if (anchorFailures == 1 || anchorFailures == MaxAnchorFailures)
                Debug.LogWarning($"[PlaneAnchoredWorld] Anchor request failed ({reason}); the world holds its pose " +
                                 $"but may drift against the room. Attempt {anchorFailures}/{MaxAnchorFailures}.", this);
        }

        void ClearAnchor()
        {
            anchorGeneration++;             // any request still in flight is now stale
            anchorPending = false;
            if (ReferenceEquals(worldAnchor, null)) return;
            if (worldAnchor != null) Destroy(worldAnchor.gameObject);
            worldAnchor = null;
        }

        /// <summary>
        /// Locked, but with no live anchor behind it: the last one was destroyed by the session, or a request failed
        /// and its retry is due.
        /// </summary>
        bool AnchorNeedsRepair =>
            (!ReferenceEquals(worldAnchor, null) && worldAnchor == null) ||
            (ReferenceEquals(worldAnchor, null) && !anchorPending && anchorFailures > 0 &&
             anchorFailures < MaxAnchorFailures && Time.time >= anchorRetryAt);

        /// <summary>Re-pin after the world has been deliberately moved or resized while locked.</summary>
        void RefreshAnchorIfLocked()
        {
            if (locked) CreateAnchor();
        }

        public void ToggleLocked() => SetLocked(!locked);

        /// <summary>
        /// Resize about a pivot - normally Petalo - so the thing the player is looking at stays put while the
        /// world grows around it. Scaling about the root instead would swing the map off-screen, because the
        /// root sits at the centre of a map that is mostly off the table.
        /// </summary>
        public void SetSizeMultiplier(float multiplier, Transform pivot)
        {
            multiplier = Mathf.Clamp(multiplier, 0.1f, 20f);
            if (Mathf.Approximately(multiplier, sizeMultiplier)) return;

            // Latch the basis BEFORE the multiplier changes. Deriving it afterwards divides the current scale by
            // the new multiplier and then multiplies straight back by it, which is a no-op: the multiplier moves
            // and the world never actually resizes.
            EnsureFittedScale();

            Vector3 keep = pivot != null ? pivot.position : transform.position;
            sizeMultiplier = multiplier;
            ApplyScale();
            if (pivot != null) transform.position += keep - pivot.position;

            // The anchor still holds the old pose; without this LateUpdate drags the world straight back.
            RefreshAnchorIfLocked();
        }

        /// <summary>One notch bigger or smaller, in even ratio steps.</summary>
        public void StepSize(int direction, Transform pivot)
        {
            const float Step = 1.25f;
            SetSizeMultiplier(direction > 0 ? sizeMultiplier * Step : sizeMultiplier / Step, pivot);
        }

        /// <summary>
        /// Recover the plane fit's scale when none has been recorded - resized before placement, or driven from
        /// the Editor where no fit has run. Latched once so it cannot drift as the multiplier moves.
        /// </summary>
        void EnsureFittedScale()
        {
            if (fittedScale > 0f) return;
            fittedScale = Mathf.Max(transform.localScale.x / Mathf.Max(sizeMultiplier, 1e-4f), 1e-5f);
        }

        void ApplyScale()
        {
            EnsureFittedScale();
            transform.localScale = Vector3.one * Mathf.Max(fittedScale * sizeMultiplier, 1e-5f);
        }

        /// <summary>
        /// Shift the world so <paramref name="focus"/> sits a given distance straight in front of the viewer,
        /// keeping the current scale, yaw and surface height.
        ///
        /// The world anchors to the largest horizontal plane found, which on a real table is often not the
        /// surface the player is looking at - a floor beats a desk on area every time. That leaves the map
        /// correctly placed and completely off-screen, which is indistinguishable from "nothing rendered".
        /// This is the escape hatch: put the map where the player is actually pointing.
        /// </summary>
        public void RecentreOn(Transform focus, Transform viewer, float distance = 0.5f)
        {
            if (focus == null || viewer == null) return;

            Vector3 forward = Vector3.ProjectOnPlane(viewer.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(viewer.up, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            Vector3 target = viewer.position + forward * distance;
            target.y = transform.position.y;        // stay on the surface the world was fitted to

            transform.position += target - focus.position;
            placed = true;
            locked = true;                          // never re-fit out from under a manual placement
            SetContentVisible(true);
            CreateAnchor();                         // re-pin at the pose the player actually chose
        }

        /// <summary>
        /// Put a point of the authored world (in this root's local design space) at <paramref name="worldPoint"/>,
        /// with the root's +Z running along <paramref name="worldForward"/>. This is how each act is dropped onto
        /// its own spot of the real floor: the root is moved, never the act inside it, so every act keeps the
        /// layout it was authored with. Re-pins the anchor if the world is locked.
        /// </summary>
        public void PlaceDesignPoint(Vector3 designLocal, Vector3 worldPoint, Vector3 worldForward)
        {
            Vector3 forward = Vector3.ProjectOnPlane(worldForward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = transform.forward;
            Quaternion rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);

            if (fitMode == FitMode.RoomScale)
            {
                fittedScale = roomScale;
                transform.localScale = Vector3.one * Mathf.Max(roomScale * sizeMultiplier, 1e-5f);
            }

            Vector3 offset = rotation * Vector3.Scale(designLocal, transform.localScale);
            transform.SetPositionAndRotation(worldPoint - offset, rotation);
            reconvergeUntil = 0f;

            if (!placed)
            {
                placed = true;
                SetContentVisible(true);
            }
            RefreshAnchorIfLocked();
        }

        /// <summary>
        /// A point on the world's surface <paramref name="distance"/> metres in front of <paramref name="viewer"/>,
        /// and the flattened direction the viewer is facing. Used to decide where the next act goes.
        /// </summary>
        public bool TryGetPointInFront(Transform viewer, float distance, out Vector3 point, out Vector3 forward)
        {
            point = Vector3.zero;
            forward = Vector3.forward;
            if (viewer == null) return false;

            forward = Vector3.ProjectOnPlane(viewer.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(viewer.up, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            point = viewer.position + forward * distance;
            point.y = SurfaceHeight;
            return true;
        }

        /// <summary>
        /// Freeze the world for a card scan. The player is about to point the phone away from the map at a physical
        /// card; the map must not twitch while they do, and plane updates must not drag the anchor under it.
        /// </summary>
        public void HoldForScan(bool hold)
        {
            if (hold == scanHold) return;
            scanHold = hold;

            if (hold)
            {
                if (!locked) Lock();
                if (planeManager != null)
                {
                    planeManagerWasEnabled = planeManager.enabled;
                    planeManager.enabled = false;
                }
            }
            else
            {
                if (planeManager != null && planeManagerWasEnabled) planeManager.enabled = true;
                reconvergeUntil = Time.time + ReconvergeSeconds;
            }
        }

        /// <summary>Re-open the anchor so a new surface can be chosen (e.g. after the session is reset).</summary>
        public void Rescan()
        {
            locked = false;
            placed = false;
            anchorPlane = null;
            anchorArea = 0f;
            ClearAnchor();
            SetContentVisible(false);
        }

        void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (var plane in args.added) Consider(plane);
            foreach (var plane in args.updated) Consider(plane);

            foreach (var pair in args.removed)
            {
                if (ReferenceEquals(anchorPlane, null) || pair.Key != anchorPlane.trackableId) continue;

                // Locked, the world keeps its pose: the anchor (re-pinned if need be) holds it, and the surface
                // height falls back to the root's own, which sits on the floor.
                if (locked) anchorPlane = null;
                else Rescan();
            }
        }

        void Consider(ARPlane plane)
        {
            if (locked || plane == null) return;
            if (plane.alignment != PlaneAlignment.HorizontalUp) return;      // the world lies on a table, not a wall
            if (plane.trackingState != TrackingState.Tracking) return;

            float area = plane.size.x * plane.size.y;
            if (area < minimumPlaneArea) return;

            bool isCurrent = anchorPlane != null && plane.trackableId == anchorPlane.trackableId;

            if (fitMode == FitMode.RoomScale)
            {
                // The floor is the lowest large surface. A table found first is replaced by the floor under it
                // as soon as the floor shows up; between two surfaces at the same height, the bigger one wins.
                if (!isCurrent && anchorPlane != null)
                {
                    float lower = anchorPlane.transform.position.y - plane.transform.position.y;
                    bool clearlyLower = lower > floorPreferenceMargin;
                    bool sameLevelBigger = Mathf.Abs(lower) <= floorPreferenceMargin && area > anchorArea * refitAreaMargin;
                    if (!clearlyLower && !sameLevelBigger) return;
                }
            }
            // A different plane has to be clearly better before the map relocates.
            else if (!isCurrent && anchorPlane != null && area < anchorArea * refitAreaMargin) return;

            bool firstPlacement = anchorPlane == null;
            anchorPlane = plane;
            anchorArea = area;

            if (fitMode == FitMode.RoomScale)
            {
                // Only a change of surface moves a room-scale world; growth of the same floor does not.
                if (firstPlacement || !isCurrent) PlaceOnFloor(plane);
            }
            else
            {
                Fit(plane, snap: firstPlacement || !isCurrent);
            }

            if (!placed)
            {
                placed = true;
                SetContentVisible(true);
            }
        }

        /// <summary>
        /// Room scale: the root lands on the floor in front of the camera at the fixed scale. The director then
        /// moves it so the current act's spawn point sits exactly there.
        /// </summary>
        void PlaceOnFloor(ARPlane plane)
        {
            fittedScale = roomScale;
            transform.localScale = Vector3.one * Mathf.Max(roomScale * sizeMultiplier, 1e-5f);

            Camera cam = Camera.main;
            if (cam == null)
            {
                transform.SetPositionAndRotation(plane.transform.TransformPoint(plane.center), Quaternion.identity);
                return;
            }

            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(cam.transform.up, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            Vector3 point = cam.transform.position + forward * roomPlacementDistance;
            point.y = plane.transform.position.y;
            transform.SetPositionAndRotation(point, Quaternion.LookRotation(forward, Vector3.up));
        }

        void Fit(ARPlane plane, bool snap)
        {
            if (plane == null) return;

            Transform t = plane.transform;
            Vector3 targetPosition = t.TransformPoint(plane.center);

            // The map is long and narrow, so it is laid along the table rather than across it: the world's +Z is
            // aligned to the surface's longer edge. A plane's local X and Z span the surface; its local Y is the
            // normal, which is why the long axis is picked from size rather than from a fixed axis.
            bool xIsLong = plane.size.x >= plane.size.y;
            float longEdge = (xIsLong ? plane.size.x : plane.size.y) * surfaceFill;
            float shortEdge = (xIsLong ? plane.size.y : plane.size.x) * surfaceFill;

            // Both axes have to fit, so the tighter of the two constraints wins.
            float scale = Mathf.Min(longEdge / Mathf.Max(worldSpanMetres, 1e-3f),
                                    shortEdge / Mathf.Max(worldWidthMetres, 1e-3f));
            scale = Mathf.Clamp(scale, scaleRange.x, scaleRange.y);

            // Yaw only: the world stands upright on the surface regardless of how the plane's own basis is rolled.
            Vector3 longAxis = Vector3.ProjectOnPlane(xIsLong ? t.right : t.forward, Vector3.up);
            if (longAxis.sqrMagnitude < 1e-6f) longAxis = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            Quaternion targetRotation = longAxis.sqrMagnitude < 1e-6f
                ? Quaternion.identity
                : Quaternion.LookRotation(longAxis.normalized, Vector3.up);

            // The fit decides the basis; the player's multiplier rides on top of it, so a resize survives the
            // world being re-fitted to a bigger plane.
            fittedScale = scale;
            float applied = scale * sizeMultiplier;

            if (snap)
            {
                transform.SetPositionAndRotation(targetPosition, targetRotation);
                transform.localScale = Vector3.one * applied;
                return;
            }

            float k = 1f - Mathf.Exp(-followRate * Time.deltaTime);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, targetPosition, k),
                Quaternion.Slerp(transform.rotation, targetRotation, k));
            transform.localScale = Vector3.one * Mathf.Lerp(transform.localScale.x, applied, k);
        }

        void SetContentVisible(bool visible)
        {
            if (content == null) return;

            // Toggle children rather than this object: disabling the root would stop this component running, and
            // it would never see the plane that was about to arrive.
            for (int i = 0; i < content.childCount; i++)
            {
                var child = content.GetChild(i).gameObject;

                // Acts are shown one at a time by the hybrid loop director; revealing the world must not switch
                // every act on at once.
                if (visible && child.GetComponent<HybridAct>() != null) continue;
                if (child.activeSelf != visible) child.SetActive(visible);
            }
        }
    }
}
