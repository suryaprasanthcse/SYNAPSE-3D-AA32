using UnityEngine;
using UnityEngine.InputSystem;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Drives Petalo across the AR planes: a continuous, physical character controller for the raymarched avatar.
    ///
    /// The rig is split so nothing fights over a transform:
    ///   Petalo_Rig    (this component)  owns position and heading.
    ///   Petalo_Gait   (child)           owns the walk bob, the squash/stretch and the ledge teeter.
    ///   Petalo        (grandchild)      owns nothing but its own scale, which <see cref="PetaloBeacon"/> pins to
    ///                                   the avatar's size every frame. Driving the beacon's transform directly
    ///                                   would be overwritten, which is why the gait node exists.
    ///
    /// EVERYTHING HERE IS IN DESIGN METRES, not world metres. The rig is a child of the plane-anchored world root,
    /// which is scaled to roughly 1:100 so a 130 m map fits a table. Motion therefore happens in the parent's
    /// local space and speeds are authored against the GDD's dimensions; the only place the world scale appears is
    /// the ground probes, which have to be cast in world space because physics is.
    ///
    /// Movement is simulated in FixedUpdate so the ledge probes see the same physics scene every step, and the
    /// transform is interpolated between the last two steps in Update so a 50 Hz simulation does not judder on a
    /// 60/120 Hz screen.
    ///
    /// LEDGE CLAMP. There are no invisible walls: the constraint lives entirely in the movement math. Every step
    /// a spherecast probes the ground just ahead of Petalo's footprint in the direction of travel. Ground that is
    /// missing, too far down, or too far up (a plinth, an urn) is not walkable. On a miss, a ring of probes around
    /// Petalo finds the boundary, the velocity loses its component into the boundary and keeps the component
    /// along it - so pushing diagonally into a rim slides Petalo along the rim instead of stopping it dead. As a
    /// last line of defence, a step that would leave the centre unsupported is rejected outright.
    ///
    /// Input is a floating virtual joystick: the first touch anywhere inside the control band plants an origin and
    /// the drag from it is the stick. The stick is read in SCREEN space and mapped through the camera onto the
    /// ground at Petalo's feet, so dragging toward any point on the screen walks Petalo toward what is drawn
    /// there - whatever angle, roll or side of the table the phone is held from. Keyboard and gamepad are accepted
    /// too, so the loop is playable in the Editor under XR Simulation without a touchscreen.
    ///
    /// Control is released as Petalo morphs into the Anomaly: State B is a cutscene body, not a playable one.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Funobotz/Petalo Pilot")]
    public sealed class PetaloPilot : MonoBehaviour
    {
        [Header("Rig")]
        [Tooltip("Child that carries the bob and squash. Left empty, one is created at startup.")]
        [SerializeField] Transform gait;
        [SerializeField] PetaloBeacon petalo;
        [Tooltip("Camera that movement is made relative to. Left empty, Camera.main is used.")]
        [SerializeField] Camera viewCamera;

        [Header("Movement (design metres)")]
        [Tooltip("Top speed in DESIGN metres per second. The map is ~130 m long, so ~6 reads as a brisk walk.")]
        [SerializeField, Min(0.01f)] float moveSpeed = 6f;
        [Tooltip("Acceleration and braking rate (1/s). Higher feels snappier, lower feels heavier.")]
        [SerializeField, Min(1f)] float accelRate = 8f;
        [Tooltip("Turn rate in degrees per second. Petalo's face is on +Z, so heading is what the player reads.")]
        [SerializeField, Min(30f)] float turnDegreesPerSecond = 540f;
        [Tooltip("Ignore stick deflections below this, so a resting thumb does not creep.")]
        [SerializeField, Range(0f, 0.5f)] float deadZone = 0.12f;

        [Header("Ground (design metres)")]
        [Tooltip("Blockout collision layers. Nothing selected means every layer is tested.")]
        [SerializeField] LayerMask groundLayers = ~0;
        [Tooltip("How far above and below the rig to look for ground, in design metres.")]
        [SerializeField, Min(0.1f)] float groundProbe = 8f;
        [Tooltip("Ride height above the surface, in design metres.")]
        [SerializeField, Min(0f)] float hoverHeight = 0.05f;
        [Tooltip("Where the avatar's feet are, as a fraction of its height below its own origin. The SDF is " +
                 "authored in a unit cube centred on the origin, so the plinth sits half a body-height below it " +
                 "- 0.5 puts the base of the pot on the floor instead of two metres under it.")]
        [SerializeField, Range(0f, 1f)] float footOffsetFraction = 0.5f;
        [Tooltip("How fast the rig settles onto a new ground height (1/s). Low values smooth out step edges.")]
        [SerializeField, Min(1f)] float groundFollowRate = 12f;

        [Header("Ledge Clamp (design metres)")]
        [Tooltip("Keep Petalo on walkable geometry. Off restores free movement over the table plane.")]
        [SerializeField] bool ledgeClamp = true;
        [Tooltip("How far ahead of Petalo's centre the ground is checked, as a fraction of the avatar's height. " +
                 "Petalo stops with its centre (this - probe radius) inside the rim, so the pot's lip can hang " +
                 "over the edge the way a real foot teeters.")]
        [SerializeField, Range(0.05f, 0.6f)] float footprintFraction = 0.25f;
        [Tooltip("Radius of the ground spherecast. Wide enough to bridge the hairline seams between kit pieces, " +
                 "which a thin ray would fall through and read as a cliff.")]
        [SerializeField, Min(0.01f)] float probeRadius = 0.15f;
        [Tooltip("Cosine of the steepest contact that still counts as floor. 0.5 = 60 degrees.")]
        [SerializeField, Range(0f, 1f)] float minGroundNormal = 0.5f;
        [Tooltip("Deepest drop Petalo will step down. Matches the step-up limit so every climb can be walked " +
                 "back; the void below the decks has no collider at all, so it is never walkable whatever this is.")]
        [SerializeField, Min(0f)] float maxStepDown = 1.28f;
        [Tooltip("Highest rise Petalo will step up. The circuit's tallest real risers are the atrium rim (1.05) " +
                 "and the Tholos platform off the viaduct (1.26); every prop is 1.30 or more - plinth tiers, " +
                 "pedestals, urns - so 1.28 keeps the route open and makes the props walls to slide along " +
                 "rather than ledges to climb and be stranded on.")]
        [SerializeField, Min(0f)] float maxStepUp = 1.28f;
        [Tooltip("Drops up to this are stairs. Deeper drops also need a landing at least 'Landing Depth' deep, so " +
                 "Petalo cannot hop down onto a pier capital poking up through a broken span.")]
        [SerializeField, Min(0f)] float stairRise = 0.5f;
        [SerializeField, Min(0.1f)] float landingDepth = 2f;
        [Tooltip("Probes in the ring that finds the edge's direction. The boundary is then refined by bisection, " +
                 "so this sets robustness, not precision.")]
        [SerializeField, Range(8, 32)] int edgeSamples = 16;
        [Tooltip("Speed cap, as a fraction of top speed, while the stick pushes straight into the void.")]
        [SerializeField, Range(0f, 1f)] float edgeCrawlFraction = 0.18f;

        [Header("Ledge Teeter")]
        [Tooltip("Lean out over the edge at full push, in degrees.")]
        [SerializeField, Range(0f, 30f)] float teeterDegrees = 11f;
        [Tooltip("Amplitude of the loss-of-footing wobble, in degrees.")]
        [SerializeField, Range(0f, 20f)] float teeterWobbleDegrees = 5f;
        [SerializeField, Range(0.5f, 12f)] float teeterWobbleHz = 5.5f;
        [Tooltip("How fast the teeter blends in and out (1/s).")]
        [SerializeField, Min(1f)] float teeterBlendRate = 10f;

        [Header("Gait")]
        [Tooltip("Hops per second at full speed.")]
        [SerializeField, Min(0f)] float hopRate = 2.4f;
        [Tooltip("Peak lift of a hop as a fraction of the avatar's own height.")]
        [SerializeField, Range(0f, 0.5f)] float hopHeight = 0.13f;
        [Tooltip("Squash at touch-down. Width and depth compensate, so the avatar's volume is preserved.")]
        [SerializeField, Range(0f, 0.5f)] float squash = 0.16f;
        [Tooltip("Lean into the direction of travel, in degrees at top speed.")]
        [SerializeField, Range(0f, 25f)] float leanDegrees = 9f;

        [Header("Input")]
        [Tooltip("Fraction of the screen height, measured from the bottom, in which a touch plants the joystick. " +
                 "1 = anywhere on screen.")]
        [SerializeField, Range(0.2f, 1f)] float controlBand = 1f;
        [Tooltip("Drag distance for full deflection, as a fraction of the shorter screen edge.")]
        [SerializeField, Range(0.05f, 0.4f)] float stickRadius = 0.12f;
        [Tooltip("Speed response past the dead zone. 1 is linear; above 1 gives fine control near the centre and " +
                 "keeps full speed at full deflection.")]
        [SerializeField, Range(1f, 3f)] float responseExponent = 1.4f;
        [Tooltip("When the thumb drags past the rim, drag the stick's origin along behind it. Reversing direction " +
                 "then answers immediately instead of first crossing the whole stick.")]
        [SerializeField] bool stickFollowsThumb = true;
        [Tooltip("Map the stick through the camera's projection at Petalo's position, so a drag toward a point on " +
                 "screen walks toward what is drawn there. Off uses the camera's flattened forward axis.")]
        [SerializeField] bool perspectiveSteering = true;
        [SerializeField] bool keyboardFallback = true;
        [SerializeField] bool gamepadFallback = true;
        [Tooltip("Draw the floating stick where the player's thumb planted it.")]
        [SerializeField] bool drawStick = true;

        Transform space;            // the world root the rig moves inside; null means the rig is unparented
        PlaneAnchoredWorld world;   // locked the first time the player actually drives
        Vector3 velocity;           // design metres per second, in `space`, planar
        Vector3 intent;             // desired planar direction in `space`, magnitude 0..1, set in Update
        Vector2 stick;
        Vector2 stickOrigin;
        Vector2 stickTip;
        bool stickActive;
        int stickTouchId = -1;

        // Simulation state, in `space`. The transform only ever shows an interpolation of these.
        Vector3 simPosition;
        Vector3 simPrevious;
        Vector3 lastWritten;
        bool simInitialised;
        float groundY;              // rig origin height, smoothed
        float footLevel;            // the surface under Petalo's centre, unsmoothed: the reference for step limits
        bool supported;             // has stood on real geometry; from then on the clamp is enforced

        // Ledge state.
        bool atLedge;
        Vector3 ledgeNormal;        // planar, points from Petalo out over the edge
        float ledgePush;            // 0..1, how squarely the stick is pushing over the edge
        float teeter;               // blended 0..1 for the visual
        bool[] ringWalkable;
        static readonly RaycastHit[] s_Hits = new RaycastHit[16];
        float strandedUntil;        // fixed time until which drops are relaxed
        bool relaxDrops;            // this step may drop any height onto real geometry - never into the void

        float hopPhase;
        Vector3 gaitRestScale = Vector3.one;
        float avatarHeight = 3f;
        static Texture2D s_Disc;

        /// <summary>Stick deflection this frame, in the range -1..1 on each axis.</summary>
        public Vector2 Stick => stick;

        /// <summary>Current planar speed, in design metres per second.</summary>
        public float Speed => new Vector2(velocity.x, velocity.z).magnitude;

        /// <summary>False once Petalo has begun morphing into the Anomaly: State B is not a playable body.</summary>
        public bool ControlEnabled => petalo == null || petalo.StateBlend < 0.5f;

        /// <summary>World metres per design metre, from the plane fit. 1 when the rig is not inside a world root.</summary>
        public float WorldScale => space != null ? Mathf.Max(space.lossyScale.x, 1e-6f) : 1f;

        /// <summary>True while the last step was clamped against a ledge or a too-tall obstacle.</summary>
        public bool AtLedge => atLedge;

        /// <summary>Planar direction, in the world root's space, pointing out over the current ledge.</summary>
        public Vector3 LedgeNormal => ledgeNormal;

        /// <summary>True once Petalo is standing on real geometry and the ledge clamp is enforcing.</summary>
        public bool Grounded => supported;

        /// <summary>
        /// Drive Petalo from code instead of the stick - automated traversal tests, scripted beats. A planar
        /// direction in the world root's local space with magnitude 0..1; null hands control back to the player.
        /// </summary>
        public Vector3? ScriptedDrive { get; set; }

        float Footprint => Mathf.Max(avatarHeight * footprintFraction, probeRadius + 0.05f);

        void Awake()
        {
            space = transform.parent;
            world = GetComponentInParent<PlaneAnchoredWorld>();
            if (viewCamera == null) viewCamera = Camera.main;
            if (petalo == null) petalo = GetComponentInChildren<PetaloBeacon>(true);

            if (gait == null)
            {
                // Insert the gait node between this rig and whatever it is already carrying, so an avatar wired up
                // by hand in the Editor still ends up correctly nested.
                var node = new GameObject("Petalo_Gait");
                gait = node.transform;
                gait.SetParent(transform, false);
                for (int i = transform.childCount - 1; i >= 0; i--)
                {
                    var child = transform.GetChild(i);
                    if (child == gait) continue;
                    child.SetParent(gait, false);
                }
            }

            gaitRestScale = gait.localScale;
            if (petalo != null) avatarHeight = Mathf.Max(petalo.Size, 1e-3f);

            if (viewCamera == null)
                Debug.LogWarning("[PetaloPilot] No camera: movement cannot be made camera-relative.", this);
        }

        void OnEnable()
        {
            // The world root hides its children until a plane is found, so the rig is re-enabled into a world that
            // may have moved. Re-read the transform rather than trusting a stale simulation.
            simInitialised = false;
        }

        void Update()
        {
            float dt = Time.deltaTime;

            // The authored size, not the transform scale: the beacon only pins its scale from its own Update, so
            // on the first frames the scale is still 1 and the foot offset would be a whole avatar out.
            if (petalo != null) avatarHeight = Mathf.Max(petalo.Size, 1e-3f);

            stick = ControlEnabled ? ReadStick() : Vector2.zero;

            // The world stops re-fitting itself the instant the player drives: a map that kept re-centring on a
            // growing plane would slide the ground out from under a moving character. Skipped once the player has
            // worked the lock button themselves - having deliberately unlocked it to re-fit, they would not
            // expect the first nudge of the stick to lock it straight back.
            if (world != null && !world.IsLocked && !world.ManualLockOverride && stick.sqrMagnitude > 1e-6f)
                world.Lock();

            // The camera is sampled here, at render rate, and handed to the fixed step as a direction.
            if (!ControlEnabled) intent = Vector3.zero;
            else if (ScriptedDrive.HasValue) intent = Flatten(ScriptedDrive.Value, ScriptedDrive.Value.magnitude);
            else intent = StickToGround(stick);

            if (!simInitialised) SyncSimulation();

            // Interpolate between the last two fixed steps. The one-step lag is invisible; judder would not be.
            float alpha = Mathf.Clamp01((Time.time - Time.fixedTime) / Mathf.Max(Time.fixedDeltaTime, 1e-4f));
            lastWritten = Vector3.Lerp(simPrevious, simPosition, alpha);
            transform.localPosition = lastWritten;

            Face(dt);
            Gait(dt);
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (!simInitialised || (transform.localPosition - lastWritten).sqrMagnitude > 1e-6f)
                SyncSimulation();   // first step, or something else teleported the rig: adopt its position

            simPrevious = simPosition;

            // Stranded on a top whose only way on is further up (the spawn sits on the stacked atrium plinth):
            // the drop limits are relaxed for a moment so Petalo can climb down. Only real geometry counts - the
            // void under the decks has no collider, so it stays unwalkable either way.
            relaxDrops = Time.fixedTime < strandedUntil;

            Vector3 desired = intent * moveSpeed;
            velocity = Vector3.Lerp(velocity, desired, 1f - Mathf.Exp(-accelRate * dt));
            velocity.y = 0f;

            Vector3 step = velocity * dt;
            atLedge = false;
            ledgePush = 0f;

            if (ledgeClamp && supported)
            {
                step = ClampToLedges(step, dt);

                // Last line of defence: whatever the probes concluded, never finish a step with the centre over
                // the void. A step that would is dropped rather than half-taken.
                Vector3 next = simPosition + step;
                if (step.sqrMagnitude > 1e-10f && !CentreSupported(next))
                {
                    step = Vector3.zero;
                    velocity = Vector3.zero;
                    atLedge = true;
                }
            }

            simPosition.x += step.x;
            simPosition.z += step.z;
            Ground(dt);
        }

        void SyncSimulation()
        {
            simPosition = transform.localPosition;
            simPrevious = simPosition;
            lastWritten = simPosition;
            groundY = simPosition.y;
            footLevel = simPosition.y - hoverHeight - avatarHeight * footOffsetFraction;
            supported = false;
            simInitialised = true;
        }

        // ------------------------------------------------------------------------------------------
        // Ledge clamp
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns the step Petalo may actually take. Leaves `velocity` holding the clamped velocity so the next
        /// step accelerates from what really happened, not from the speed the stick asked for.
        /// </summary>
        Vector3 ClampToLedges(Vector3 step, float dt)
        {
            float length = step.magnitude;
            Vector3 intentDirection = intent.sqrMagnitude > 1e-6f ? intent.normalized : Vector3.zero;

            // Look where Petalo is going; if it has stalled against the edge, look where the player wants it to go
            // - that is what keeps a head-on push detected, and teetering, once the velocity has bled away.
            Vector3 look = length > 1e-5f ? step / length : intentDirection;
            if (look == Vector3.zero) return step;

            float reach = Footprint + length;
            if (FootprintClear(simPosition + step, look, true)) return step;

            // An edge. The ring tells us which way it runs; the probe ahead only told us that it is there.
            Vector3 normal = EdgeNormal(simPosition, reach, look);
            atLedge = true;
            ledgeNormal = normal;

            Vector3 pushing = intentDirection != Vector3.zero ? intentDirection : look;
            ledgePush = Mathf.Max(0f, Vector3.Dot(pushing, normal));

            // Tangent projection: strip the velocity's component into the void and keep the slide along the rim.
            Vector3 v = velocity;
            float into = Vector3.Dot(v, normal);
            if (into > 0f) v -= normal * into;

            // Pushing squarely over the edge reduces Petalo to a cautious crawl. A glancing push - anything much
            // past 45 degrees off the normal - keeps its speed, so sliding along a rim stays slick.
            float crawl = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.7f, 0.97f, ledgePush));
            v = Vector3.ClampMagnitude(v, moveSpeed * Mathf.Lerp(1f, edgeCrawlFraction, crawl));
            v.y = 0f;

            Vector3 slide = v * dt;
            float slideLength = slide.magnitude;
            if (slideLength > 1e-6f)
            {
                Vector3 slideDirection = slide / slideLength;
                if (!FootprintClear(simPosition + slide, slideDirection, false))
                {
                    // An inside corner, or a spur too narrow to follow: the tangent is blocked too. Stop.
                    v = Vector3.zero;
                    slide = Vector3.zero;
                }
            }

            velocity = v;
            return slide;
        }

        /// <summary>
        /// Direction from `centre` out over the nearest ledge. A ring of probes finds the widest arc of unwalkable
        /// ground, each end of the arc is bisected to within a couple of degrees, and the normal is the arc's
        /// bisector. A straight rim gives a half-circle arc whose bisector is exactly perpendicular to the rim, so
        /// the slide runs parallel to it instead of creeping in or out.
        /// </summary>
        Vector3 EdgeNormal(Vector3 centre, float reach, Vector3 fallback)
        {
            int n = Mathf.Clamp(edgeSamples, 8, 32);
            if (ringWalkable == null || ringWalkable.Length != n) ringWalkable = new bool[n];

            int firstGood = -1;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = Ring(i * (Mathf.PI * 2f / n));
                ringWalkable[i] = Walkable(centre + d * reach, d);
                if (ringWalkable[i] && firstGood < 0) firstGood = i;
            }

            if (!relaxDrops && IsStranded(centre)) strandedUntil = Time.fixedTime + 0.3f;

            // All clear: the probe ahead caught something smaller than the ring's spacing. All blocked: Petalo is
            // on an island. Either way the travel direction is the best normal there is.
            if (firstGood < 0) return fallback;

            // The widest run of blocked samples, scanning from a walkable one so no run wraps past the end.
            int bestStart = -1, bestLength = 0, runStart = -1, runLength = 0;
            for (int k = 1; k <= n; k++)
            {
                int i = (firstGood + k) % n;
                if (ringWalkable[i]) { runLength = 0; continue; }
                if (runLength == 0) runStart = i;
                runLength++;
                if (runLength > bestLength) { bestLength = runLength; bestStart = runStart; }
            }
            if (bestLength == 0) return fallback;

            float spacing = Mathf.PI * 2f / n;
            float firstBlocked = bestStart * spacing;
            float lastBlocked = firstBlocked + (bestLength - 1) * spacing;

            // Bisect each boundary between a walkable sample and a blocked one.
            float lo = firstBlocked - spacing, hi = firstBlocked;          // lo walkable, hi blocked
            for (int k = 0; k < 3; k++)
            {
                float mid = 0.5f * (lo + hi);
                Vector3 d = Ring(mid);
                if (Walkable(centre + d * reach, d)) lo = mid; else hi = mid;
            }
            float arcStart = 0.5f * (lo + hi);

            lo = lastBlocked; hi = lastBlocked + spacing;                  // lo blocked, hi walkable
            for (int k = 0; k < 3; k++)
            {
                float mid = 0.5f * (lo + hi);
                Vector3 d = Ring(mid);
                if (Walkable(centre + d * reach, d)) hi = mid; else lo = mid;
            }
            float arcEnd = 0.5f * (lo + hi);

            return Ring(0.5f * (arcStart + arcEnd));
        }

        /// <summary>
        /// Is the ground under Petalo's footprint walkable at `destination`? Probed straight ahead and 45 degrees
        /// either side - one probe dead ahead cannot see an edge approached at a shallow angle, and a slide
        /// along a rim would creep outward a fraction every step until the pot hung off it. The flanks at 90
        /// degrees are checked on a shorter reach for the same reason, when `flanks` is set; the slide's own
        /// check leaves them out, or the rim it is sliding along would veto it.
        /// </summary>
        bool FootprintClear(Vector3 destination, Vector3 direction, bool flanks)
        {
            float reach = Footprint;
            if (!Walkable(destination + direction * reach, direction)) return false;

            Vector3 side = new Vector3(direction.z, 0f, -direction.x);
            Vector3 diagonalA = (direction + side).normalized;
            Vector3 diagonalB = (direction - side).normalized;
            if (!Walkable(destination + diagonalA * reach, diagonalA)) return false;
            if (!Walkable(destination + diagonalB * reach, diagonalB)) return false;

            if (!flanks) return true;
            float flankReach = reach * 0.75f;
            return Walkable(destination + side * flankReach, side) &&
                   Walkable(destination - side * flankReach, -side);
        }

        /// <summary>
        /// On a top too small to walk anywhere from - a plinth tier, an urn - with every way off it deeper than a
        /// step. A deck edge is never this: somewhere a landing-depth away there is always more deck.
        /// </summary>
        bool IsStranded(Vector3 centre)
        {
            float reach = Footprint + landingDepth;
            for (int i = 0; i < 12; i++)
            {
                Vector3 d = Ring(i * (Mathf.PI * 2f / 12));
                if (Walkable(centre + d * reach, d)) return false;
            }
            return true;
        }

        static Vector3 Ring(float angle) => new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

        /// <summary>
        /// Can Petalo's foot go to `point` (in `space`) from the current footing? Missing ground, a drop deeper
        /// than a step, or a rise taller than a step all say no. A drop deeper than a stair riser additionally
        /// needs somewhere to land beyond it.
        /// </summary>
        bool Walkable(Vector3 point, Vector3 direction) => Walkable(point, direction, out _);

        bool Walkable(Vector3 point, Vector3 direction, out float rise)
        {
            rise = 0f;
            if (!ProbeSurface(point, out float surface)) return false;

            rise = surface - footLevel;
            if (rise > maxStepUp) return false;
            if (relaxDrops) return true;
            if (-rise > maxStepDown) return false;

            if (-rise > stairRise)
            {
                if (!ProbeSurface(point + direction * landingDepth, out float beyond)) return false;
                if (Mathf.Abs(beyond - surface) > stairRise) return false;
            }
            return true;
        }

        /// <summary>The centre check: support directly underneath within the step limits, no landing needed.</summary>
        bool CentreSupported(Vector3 point)
        {
            return ProbeSurface(point, out float surface) && CentreWithinLimits(surface);
        }

        /// <summary>
        /// Height, in `space`, of the highest walkable surface under `point`. Cast from a probe-height above the
        /// current footing, so the entablature overhead in the viaduct is above the cast and never read as floor.
        ///
        /// Only upward-facing contacts count. The Doric shafts taper, so a sweep that starts just outside one
        /// grazes its flank metres up; reading that as a surface made columns into walls on some probes and
        /// thin air on others (a sweep that starts inside a hull does not see it at all), and Petalo stalled
        /// beside them. Rims and seams still register: the sphere touches a slab's edge with a near-vertical
        /// normal until its centre is well past it.
        /// </summary>
        bool ProbeSurface(Vector3 point, out float surface)
        {
            float scale = WorldScale;
            Vector3 top = new Vector3(point.x, footLevel + groundProbe, point.z);
            Vector3 origin = space != null ? space.TransformPoint(top) : top;
            Vector3 up = space != null ? space.up : Vector3.up;

            int count = Physics.SphereCastNonAlloc(origin, probeRadius * scale, -up, s_Hits, groundProbe * 2f * scale,
                                                   groundLayers, QueryTriggerInteraction.Ignore);
            bool found = false;
            float best = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                var hit = s_Hits[i];
                if (hit.distance <= 0f) continue;                              // overlapping at the start
                if (Vector3.Dot(hit.normal, up) < minGroundNormal) continue;   // a flank, not a floor
                float y = space != null ? space.InverseTransformPoint(hit.point).y : hit.point.y;
                if (y > best) { best = y; found = true; }
            }
            surface = found ? best : 0f;
            return found;
        }

        // ------------------------------------------------------------------------------------------
        // Input
        // ------------------------------------------------------------------------------------------
        Vector2 ReadStick()
        {
            Vector2 touch = ReadTouchStick();
            if (stickActive) return touch;

            if (keyboardFallback && Keyboard.current != null)
            {
                var k = Keyboard.current;
                Vector2 keys = Vector2.zero;
                if (k.wKey.isPressed || k.upArrowKey.isPressed) keys.y += 1f;
                if (k.sKey.isPressed || k.downArrowKey.isPressed) keys.y -= 1f;
                if (k.dKey.isPressed || k.rightArrowKey.isPressed) keys.x += 1f;
                if (k.aKey.isPressed || k.leftArrowKey.isPressed) keys.x -= 1f;
                if (keys.sqrMagnitude > 1e-4f) return Vector2.ClampMagnitude(keys, 1f);
            }

            if (gamepadFallback && Gamepad.current != null)
            {
                Vector2 pad = Gamepad.current.leftStick.ReadValue();
                if (pad.magnitude > deadZone) return Response(pad);
            }

            return Vector2.zero;
        }

        Vector2 ReadTouchStick()
        {
            float radius = Mathf.Min(Screen.width, Screen.height) * stickRadius;
            if (radius < 1f) return Vector2.zero;

            var screen = Touchscreen.current;
            if (screen != null)
            {
                // Track one finger by id, so a second finger elsewhere on the screen never steals the stick.
                if (stickActive)
                {
                    foreach (var t in screen.touches)
                    {
                        if (t.touchId.ReadValue() != stickTouchId) continue;
                        if (!t.press.isPressed) break;
                        stickTip = t.position.ReadValue();
                        return Deflection(radius);
                    }
                    ReleaseStick();
                    return Vector2.zero;
                }

                foreach (var t in screen.touches)
                {
                    if (!t.press.wasPressedThisFrame) continue;
                    Vector2 pos = t.position.ReadValue();
                    if (pos.y > Screen.height * controlBand) continue;
                    stickActive = true;
                    stickTouchId = t.touchId.ReadValue();
                    stickOrigin = pos;
                    stickTip = pos;
                    return Vector2.zero;
                }
                return Vector2.zero;
            }

            // Editor / desktop: the mouse stands in for a finger.
            var pointer = Pointer.current;
            if (pointer == null) return Vector2.zero;

            if (pointer.press.isPressed)
            {
                Vector2 pos = pointer.position.ReadValue();
                if (!stickActive)
                {
                    if (pos.y > Screen.height * controlBand) return Vector2.zero;
                    stickActive = true;
                    stickOrigin = pos;
                    stickTip = pos;
                    return Vector2.zero;
                }
                stickTip = pos;
                return Deflection(radius);
            }

            ReleaseStick();
            return Vector2.zero;
        }

        Vector2 Deflection(float radius)
        {
            Vector2 drag = stickTip - stickOrigin;

            // A floating stick that stays put makes a reversal cost a full stick-width of thumb travel before the
            // direction flips. Towing the origin keeps the thumb at most one radius from it.
            if (stickFollowsThumb && drag.magnitude > radius)
            {
                stickOrigin = stickTip - drag.normalized * radius;
                drag = stickTip - stickOrigin;
            }

            return Response(drag / radius);
        }

        /// <summary>Radial dead zone, then a power curve: fine control near the centre, full speed at the rim.</summary>
        Vector2 Response(Vector2 raw)
        {
            float magnitude = raw.magnitude;
            if (magnitude < deadZone || magnitude < 1e-6f) return Vector2.zero;

            // Rescale past the dead zone so the very first millimetre of usable travel is not a jump to speed.
            float scaled = Mathf.InverseLerp(deadZone, 1f, Mathf.Min(magnitude, 1f));
            return raw / magnitude * Mathf.Pow(scaled, responseExponent);
        }

        void ReleaseStick()
        {
            stickActive = false;
            stickTouchId = -1;
        }

        /// <summary>
        /// Stick space to the world root's ground plane, relative to where the player is standing. The camera is
        /// in world space and the rig moves in the root's local space, so the direction is converted across.
        /// </summary>
        Vector3 StickToGround(Vector2 input)
        {
            if (input.sqrMagnitude < 1e-6f) return Vector3.zero;
            float magnitude = Mathf.Min(input.magnitude, 1f);

            Vector3 direction;
            if (viewCamera == null)
            {
                direction = new Vector3(input.x, 0f, input.y);
            }
            else if (!(perspectiveSteering && TryScreenDirection(input / input.magnitude, out direction)))
            {
                Vector3 up = space != null ? space.up : Vector3.up;
                Vector3 forward = Vector3.ProjectOnPlane(viewCamera.transform.forward, up);
                Vector3 camUp = Vector3.ProjectOnPlane(viewCamera.transform.up, up);

                // Looking steeply down at the table the flattened forward shrinks toward noise while the camera's
                // up still points "away" across the surface; weight the two by how well each is conditioned.
                forward = forward + camUp * Mathf.Clamp01(1f - forward.magnitude);
                if (forward.sqrMagnitude < 1e-6f) return Flatten(new Vector3(input.x, 0f, input.y), magnitude);

                forward.Normalize();
                Vector3 right = Vector3.Cross(up, forward);
                direction = right * input.x + forward * input.y;       // world space
                if (space != null) direction = space.InverseTransformDirection(direction);
            }

            // Renormalise and re-apply the stick's own magnitude: the world root's rotation and the camera's
            // perspective must change the heading, never the speed.
            return Flatten(direction, magnitude);
        }

        /// <summary>
        /// The ground direction that appears, on screen, to leave Petalo in the stick's direction. Two points a
        /// short distance either side of Petalo's screen position are cast onto the ground plane through its
        /// feet; the difference between the hits is the answer. This folds in camera pitch, roll and perspective
        /// convergence, so steering stays true at any viewing angle and any position on screen. Returns a
        /// direction in `space`.
        /// </summary>
        bool TryScreenDirection(Vector2 screenDirection, out Vector3 direction)
        {
            direction = Vector3.zero;

            Vector3 up = space != null ? space.up : Vector3.up;
            float feetDrop = (hoverHeight + avatarHeight * footOffsetFraction) * WorldScale;
            Vector3 feet = transform.position - up * feetDrop;

            Vector3 onScreen = viewCamera.WorldToScreenPoint(feet);
            if (onScreen.z <= viewCamera.nearClipPlane) return false;          // behind the phone

            float nudge = Mathf.Max(6f, Mathf.Min(Screen.width, Screen.height) * 0.04f);
            Vector2 centre = onScreen;
            var plane = new Plane(up, feet);

            bool ahead = CastToPlane(plane, centre + screenDirection * nudge, out Vector3 a);
            bool behind = CastToPlane(plane, centre - screenDirection * nudge, out Vector3 b);

            Vector3 world;
            if (ahead && behind) world = a - b;                                // central difference
            else if (ahead) world = a - feet;
            else if (behind) world = feet - b;
            else return false;                                                  // grazing: past the horizon

            world = Vector3.ProjectOnPlane(world, up);
            if (world.sqrMagnitude < 1e-12f) return false;

            direction = space != null ? space.InverseTransformDirection(world) : world;
            return true;
        }

        bool CastToPlane(Plane plane, Vector2 screenPoint, out Vector3 point)
        {
            Ray ray = viewCamera.ScreenPointToRay(screenPoint);
            if (plane.Raycast(ray, out float distance))
            {
                point = ray.GetPoint(distance);
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        static Vector3 Flatten(Vector3 direction, float magnitude)
        {
            direction.y = 0f;
            float length = direction.magnitude;
            if (length < 1e-6f) return Vector3.zero;
            return direction / length * Mathf.Clamp01(magnitude);
        }

        // ------------------------------------------------------------------------------------------
        // Ground, heading, gait
        // ------------------------------------------------------------------------------------------
        void Ground(float dt)
        {
            if (ProbeSurface(simPosition, out float surface) && (!supported || CentreWithinLimits(surface)))
            {
                footLevel = surface;
                supported = true;
            }
            else if (!supported)
            {
                // Not on anything yet (the world root is still fitting itself to a plane): stand on the root's
                // own plane, as before the clamp existed.
                footLevel = 0f;
            }
            // else: supported, and the centre check already refused any step that would lose the ground - keep
            // the last footing rather than dropping to the table.

            // Petalo's origin is the CENTRE of its ray volume, not its base: the SDF spans -0.5..+0.5 of a unit
            // cube scaled by the avatar's size. Grounding the origin therefore buries the bottom half of the
            // plinth, which at size 4 is two design metres of pot under the floor.
            float target = footLevel + hoverHeight + avatarHeight * footOffsetFraction;

            // Smoothed, so a step edge is climbed rather than teleported onto.
            groundY = Mathf.Lerp(groundY, target, 1f - Mathf.Exp(-groundFollowRate * dt));
            simPosition.y = groundY;
        }

        bool CentreWithinLimits(float surface)
        {
            float rise = surface - footLevel;
            return rise <= maxStepUp && (relaxDrops || -rise <= maxStepDown);
        }

        void Face(float dt)
        {
            // Stalled against a ledge the velocity is gone but the intent is not: turn to look over the edge.
            Vector3 heading = new Vector3(velocity.x, 0f, velocity.z);
            if (heading.sqrMagnitude < 0.04f && intent.sqrMagnitude > 1e-4f) heading = intent;
            if (heading.sqrMagnitude < 1e-6f) return;

            Quaternion want = Quaternion.LookRotation(heading.normalized, Vector3.up);
            transform.localRotation = Quaternion.RotateTowards(transform.localRotation, want,
                                                              turnDegreesPerSecond * dt);
        }

        void Gait(float dt)
        {
            if (gait == null) return;

            float speed01 = Mathf.Clamp01(Speed / Mathf.Max(moveSpeed, 1e-4f));

            // The hop only advances while moving, so a standing Petalo settles rather than bouncing on the spot.
            hopPhase += dt * hopRate * Mathf.PI * 2f * speed01;
            float height01 = Mathf.Abs(Mathf.Sin(hopPhase)) * speed01;
            float contact = (1f - height01) * speed01;

            float yFactor = 1f + 0.5f * squash * height01 - squash * contact;
            float xzFactor = 1f / Mathf.Sqrt(Mathf.Max(0.05f, yFactor));   // volume-preserving, as ProceduralHop
            gait.localScale = new Vector3(gaitRestScale.x * xzFactor,
                                          gaitRestScale.y * yFactor,
                                          gaitRestScale.z * xzFactor);

            float lift = height01 * hopHeight * avatarHeight;
            gait.localPosition = new Vector3(0f, lift, 0f);

            // Lean is around the rig's local X, which is already aligned to the heading by Face().
            Quaternion lean = Quaternion.Euler(leanDegrees * speed01, 0f, 0f);

            // Teeter: only when the player is actually pushing over the edge, not when merely sliding along it.
            float want = atLedge ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 0.95f, ledgePush)) : 0f;
            teeter = Mathf.Lerp(teeter, want, 1f - Mathf.Exp(-teeterBlendRate * dt));

            if (teeter > 1e-3f && ledgeNormal.sqrMagnitude > 1e-6f)
            {
                // The ledge normal lives in the world root's space; the gait hangs off the rig, whose local rotation
                // is its heading in that same space.
                Vector3 outward = Quaternion.Inverse(transform.localRotation) * ledgeNormal;
                outward.y = 0f;
                outward.Normalize();
                Vector3 tipAxis = Vector3.Cross(Vector3.up, outward);          // rotating about this tips +Y outward

                // Two incommensurate frequencies, so the wobble never settles into a readable loop: it reads as
                // footing being lost and caught, not as an animation.
                float t = Time.time * teeterWobbleHz * Mathf.PI * 2f;
                float pitch = teeterDegrees * teeter + teeterWobbleDegrees * teeter * Mathf.Sin(t);
                float roll = teeterWobbleDegrees * 0.6f * teeter * Mathf.Sin(t * 1.37f + 1.1f);

                gait.localRotation = Quaternion.AngleAxis(pitch, tipAxis) *
                                     Quaternion.AngleAxis(roll, outward) * lean;
            }
            else
            {
                gait.localRotation = lean;
            }
        }

        // ------------------------------------------------------------------------------------------
        // The stick's only visual. Drawn in IMGUI so the loop needs no Canvas wiring, matching the way the
        // Lumenforge readout is drawn.
        // ------------------------------------------------------------------------------------------
        void OnGUI()
        {
            if (!drawStick || !stickActive || Event.current.type != EventType.Repaint) return;

            float radius = Mathf.Min(Screen.width, Screen.height) * stickRadius;
            Vector2 origin = new Vector2(stickOrigin.x, Screen.height - stickOrigin.y);
            Vector2 tip = new Vector2(stickTip.x, Screen.height - stickTip.y);
            tip = origin + Vector2.ClampMagnitude(tip - origin, radius);

            var disc = GetDisc();
            var previous = GUI.color;

            GUI.color = new Color(1f, 1f, 1f, 0.16f);
            GUI.DrawTexture(new Rect(origin.x - radius, origin.y - radius, radius * 2f, radius * 2f), disc);

            float knob = radius * 0.42f;
            GUI.color = new Color(1f, 0.85f, 0.35f, 0.55f);
            GUI.DrawTexture(new Rect(tip.x - knob, tip.y - knob, knob * 2f, knob * 2f), disc);

            GUI.color = previous;
        }

        static Texture2D GetDisc()
        {
            if (s_Disc != null) return s_Disc;

            const int size = 64;
            s_Disc = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            float centre = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = new Vector2(x - centre, y - centre).magnitude / centre;
                    float a = Mathf.SmoothStep(1f, 0.88f, d);   // soft edge, so the ring does not alias
                    s_Disc.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            s_Disc.Apply();
            return s_Disc;
        }
    }
}
