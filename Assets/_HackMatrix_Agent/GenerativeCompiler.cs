using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace MAAYAI.HackMatrix
{
    /// <summary>One incremental dispatch: a survey zone reported the moment the sweep finished imaging it.</summary>
    public sealed class DispatchEvent
    {
        public int Order;               // 1 = first report sent
        public int Node;
        public string Type;
        public DamageClass Class;       // from the simulated sensor
        public float FlightSeconds;     // measured: seconds after take-off
    }

    /// <summary>
    /// Compiles a zone map into the AR disaster site, then flies it.
    ///
    /// The agent plans the zones; this builds them deterministically from unit primitives scaled from
    /// <see cref="SpatialRecipes"/>, runs the on-board <see cref="FlightPlanner"/> over them, draws the route
    /// with a local-space LineRenderer, and flies a drone marker along it. As each survey zone is fully imaged
    /// it is dispatched immediately (incremental dispatch), and the times are MEASURED from the running flight.
    ///
    /// Materials and meshes are serialized assets (Hack Matrix > AA-32 > Setup Assets and Wire Scene assigns
    /// them), never created at runtime: Shader.Find and runtime materials can return null or magenta on a
    /// device build that stripped the shader. The layout root is unscaled, so everything is in AR metres.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Hack Matrix/Generative Compiler")]
    public sealed class GenerativeCompiler : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] AgenticLLM_Bridge bridge;
        [Tooltip("Compile every plan the bridge emits, as soon as it arrives.")]
        [SerializeField] bool compileOnPlan = true;
        [SerializeField] ARPlaneManager planeManager;
        [Tooltip("Optional. With it the layout lands where the screen centre meets the floor; without it, on the " +
                 "largest horizontal plane.")]
        [SerializeField] ARRaycastManager raycastManager;
        [SerializeField] ARAnchorManager anchorManager;
        [SerializeField] Camera viewCamera;

        [Header("Materials (URP Unlit, HDR colour for bloom)")]
        [SerializeField] Material hazardMaterial;     // red
        [SerializeField] Material shoringMaterial;    // amber
        [SerializeField] Material rescueMaterial;     // green
        [SerializeField] Material launchMaterial;     // white-blue; also the relay mast
        [SerializeField] Material pathMaterial;       // neon cyan; also the drone

        [Header("Meshes (built-in primitives, serialized so the build keeps them)")]
        [SerializeField] Mesh cubeMesh;
        [SerializeField] Mesh cylinderMesh;

        [Header("Composite prefabs (optional; Hack Matrix > AA-32 > Build Composite Prefabs)")]
        [Tooltip("Authored in a UNIT box: x and z in -0.5..0.5, y in 0..1, pivot at the ground centre. The compiler " +
                 "scales it to the zone's footprint and height, so what you see is exactly the box the verifier checks.")]
        [SerializeField] GameObject hazardPrefab;
        [SerializeField] GameObject shoringPrefab;
        [SerializeField] GameObject rescuePrefab;
        [Tooltip("Real size, pivot at its centre, nose along +z.")]
        [SerializeField] GameObject dronePrefab;

        [Header("Scale and Bounds")]
        [Tooltip("AR metres per site metre. 0.03 maps a 50 m site onto the 1.5 m footprint.")]
        [SerializeField, Range(0.005f, 0.2f)] float kitScale = 0.03f;
        [Tooltip("Edge of the square every zone must fit inside, in AR metres.")]
        [SerializeField, Min(0.1f)] float footprintMetres = 1.5f;
        [Tooltip("Used when no AR plane is available (Editor without XR Simulation): the layout is dropped this " +
                 "far in front of the camera, this far below it.")]
        [SerializeField] Vector2 fallbackForwardAndDrop = new(1.0f, 1.2f);

        [Header("Flight")]
        [Tooltip("Drone marker speed in AR metres per second (demo speed).")]
        [SerializeField, Range(0.02f, 1f)] float droneSpeed = 0.4f;
        [Tooltip("Real drone speed used for the site-scale flight-time estimate, in m/s.")]
        [SerializeField, Range(1f, 25f)] float realDroneSpeed = 8f;
        [SerializeField, Range(0.001f, 0.02f)] float pathWidth = 0.005f;

        [Header("Events")]
        public UnityEvent<int> compiled;             // pieces spawned

        Transform layoutRoot;
        ARAnchor layoutAnchor;
        int anchorRequest;

        Transform drone;
        float flown;                                 // path distance flown so far
        int nextDispatch;
        readonly List<DispatchEvent> dispatches = new();

        public float KitScale => kitScale;
        public float FootprintMetres => footprintMetres;
        public float RealDroneSpeed => realDroneSpeed;
        public int PiecesSpawned { get; private set; }
        public int NodesClamped { get; private set; }
        public int NodesSkipped { get; private set; }
        public bool IsAnchored => layoutAnchor != null;
        public Transform LayoutRoot => layoutRoot;
        public SiteModel Site { get; private set; }
        public FlightPath Path { get; private set; }
        /// <summary>The radio network of the built site; null when there is no LaunchPad or no RescueLZ.</summary>
        public RelayNetwork Relay { get; private set; }

        /// <summary>Realtime at take-off, first dispatch and landing; negative until they happen.</summary>
        public float FlightStartedAt { get; private set; } = -1f;
        public float FirstDispatchAt { get; private set; } = -1f;
        public float SurveyCompleteAt { get; private set; } = -1f;
        public IReadOnlyList<DispatchEvent> Dispatches => dispatches;
        public bool Flying => drone != null && SurveyCompleteAt < 0f;

        void Awake()
        {
            if (bridge == null) bridge = GetComponent<AgenticLLM_Bridge>();
            if (bridge == null) bridge = FindAnyObjectByType<AgenticLLM_Bridge>();
            if (planeManager == null) planeManager = FindAnyObjectByType<ARPlaneManager>();
            if (raycastManager == null) raycastManager = FindAnyObjectByType<ARRaycastManager>();
            if (anchorManager == null) anchorManager = FindAnyObjectByType<ARAnchorManager>();
            if (viewCamera == null) viewCamera = Camera.main;
        }

        void OnEnable()
        {
            if (bridge != null) bridge.PlanReady += OnPlanReady;
        }

        void OnDisable()
        {
            if (bridge != null) bridge.PlanReady -= OnPlanReady;
        }

        void OnPlanReady(SpatialPlan plan, PlanSource source)
        {
            if (compileOnPlan) Compile(plan);
        }

        // ==========================================================================================
        // Compile
        // ==========================================================================================
        /// <summary>Replace the current site with <paramref name="plan"/> and start the survey flight.</summary>
        public void Compile(SpatialPlan plan)
        {
            if (plan?.nodes == null || plan.nodes.Length == 0)
            {
                Debug.LogWarning("[GenerativeCompiler] Empty plan: nothing to build.", this);
                return;
            }
            if (!AssetsComplete())
            {
                Debug.LogError("[GenerativeCompiler] Materials or meshes are not assigned. Run Hack Matrix > AA-32 > " +
                               "Setup Assets and Wire Scene.", this);
                return;
            }

            Clear();
            Pose pose = ResolvePlacement(out bool onPlane);

            var rootObject = new GameObject($"SurveySite ({plan.plan_name})");
            layoutRoot = rootObject.transform;
            layoutRoot.SetPositionAndRotation(pose.position, pose.rotation);

            Site = SiteModel.Build(plan, kitScale, footprintMetres);
            NodesSkipped = plan.nodes.Length - Site.Zones.Count;
            NodesClamped = 0;
            foreach (var zone in Site.Zones)
            {
                if ((zone.Centre - zone.Requested).sqrMagnitude > 1e-8f) NodesClamped++;
                BuildZone(zone);
            }

            Path = FlightPlanner.Plan(Site);
            BuildFlightLine(Path);
            Relay = Site.First(SpatialNodeTypes.LaunchPad) != null && Site.First(SpatialNodeTypes.RescueLZ) != null
                ? RelayNetwork.Analyse(Site)
                : null;
            if (Relay != null) BuildRadioLinks(Relay);
            StartFlight();

            if (onPlane) RequestAnchor(pose);

            Debug.Log($"[GenerativeCompiler] '{plan.plan_name}': {Site.Zones.Count} zones ({NodesClamped} clamped, " +
                      $"{NodesSkipped} skipped), {Path.Waypoints.Count} waypoints over {Path.Lanes} lanes, " +
                      $"{Path.TotalLength:0.00} m path, {(onPlane ? "on an AR plane" : "in front of the camera (no plane)")}.", this);
            compiled?.Invoke(PiecesSpawned);
        }

        /// <summary>Remove the current site, its flight and its anchor.</summary>
        public void Clear()
        {
            anchorRequest++;                                        // any anchor still being created is stale
            if (layoutRoot != null) Destroy(layoutRoot.gameObject);
            if (layoutAnchor != null) Destroy(layoutAnchor.gameObject);
            layoutRoot = null;
            layoutAnchor = null;
            drone = null;
            Site = null;
            Path = null;
            Relay = null;
            dispatches.Clear();
            FlightStartedAt = FirstDispatchAt = SurveyCompleteAt = -1f;
            PiecesSpawned = 0;
        }

        void BuildZone(SiteZone zone)
        {
            var size = new Vector3(zone.Half.x * 2f, zone.Height, zone.Half.y * 2f);
            var centre = new Vector3(zone.Centre.x, zone.Height * 0.5f, zone.Centre.y);
            string name = $"{zone.Index}: {zone.Type} (tier {zone.Tier})";

            GameObject composite = zone.Type switch
            {
                SpatialNodeTypes.HazardZone => hazardPrefab,
                SpatialNodeTypes.ShoringSite => shoringPrefab,
                SpatialNodeTypes.RescueLZ => rescuePrefab,
                _ => null
            };
            if (composite != null)
            {
                // One parent per zone, scaled from the unit box to the zone's own box: the verifier's geometry.
                var instance = Instantiate(composite, layoutRoot, false);
                instance.name = name;
                instance.transform.localPosition = new Vector3(zone.Centre.x, 0f, zone.Centre.y);
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = size;
                PiecesSpawned++;
                return;
            }

            switch (zone.Type)
            {
                case SpatialNodeTypes.HazardZone: Piece(name, cubeMesh, hazardMaterial, centre, size); break;
                case SpatialNodeTypes.ShoringSite: Piece(name, cubeMesh, shoringMaterial, centre, size); break;
                case SpatialNodeTypes.RescueLZ: Piece(name, cubeMesh, rescueMaterial, centre, size); break;
                case SpatialNodeTypes.LaunchPad: Piece(name, cubeMesh, launchMaterial, centre, size); break;
                case SpatialNodeTypes.RelayNode:
                    // Unity's cylinder is 2 units tall: y scale is half the height.
                    Piece(name, cylinderMesh, launchMaterial, centre, new Vector3(size.x, zone.Height * 0.5f, size.z));
                    break;
            }
        }

        Transform Piece(string name, Mesh mesh, Material material, Vector3 localPosition, Vector3 localScale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(layoutRoot, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            PiecesSpawned++;
            return go.transform;
        }

        void BuildFlightLine(FlightPath path)
        {
            var go = new GameObject("FlightPath");
            go.transform.SetParent(layoutRoot, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;                             // travels with the layout and its anchor
            line.sharedMaterial = pathMaterial;
            line.widthMultiplier = pathWidth;
            line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.positionCount = path.Waypoints.Count;
            line.SetPositions(path.Waypoints.ToArray());
        }

        /// <summary>
        /// One thin local-space line per radio hop on the chains actually used, at mast height, so the relay
        /// network the verifier accepted is visible in the room.
        /// </summary>
        void BuildRadioLinks(RelayNetwork net)
        {
            float y = SpatialRecipes.HeightDesign(SpatialNodeTypes.RelayNode, 0) * kitScale;
            foreach (var (a, b) in net.Links)
            {
                var go = new GameObject("RadioLink");
                go.transform.SetParent(layoutRoot, false);
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.sharedMaterial = launchMaterial;
                line.widthMultiplier = pathWidth * 0.4f;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.positionCount = 2;
                line.SetPosition(0, new Vector3(a.x, y, a.y));
                line.SetPosition(1, new Vector3(b.x, y, b.y));
            }
        }

        // ==========================================================================================
        // Flight and incremental dispatch
        // ==========================================================================================
        void StartFlight()
        {
            if (dronePrefab != null)
            {
                drone = Instantiate(dronePrefab, layoutRoot, false).transform;
                drone.name = "Drone";
                drone.localPosition = Path.PointAt(0f);
                PiecesSpawned++;
            }
            else
            {
                float d = footprintMetres * 0.03f;
                drone = Piece("Drone", cubeMesh, pathMaterial, Path.PointAt(0f), new Vector3(d, d * 0.35f, d));
            }
            flown = 0f;
            nextDispatch = 0;
            FlightStartedAt = Time.realtimeSinceStartup;
        }

        void Update()
        {
            if (drone == null || Path == null || SurveyCompleteAt >= 0f) return;

            flown = Mathf.Min(flown + droneSpeed * Time.deltaTime, Path.TotalLength);
            Vector3 previous = drone.localPosition;
            drone.localPosition = Path.PointAt(flown);
            Vector3 heading = drone.localPosition - previous;
            heading.y = 0f;
            if (heading.sqrMagnitude > 1e-10f) drone.localRotation = Quaternion.LookRotation(heading, Vector3.up);

            while (nextDispatch < Path.Dispatches.Count && Path.Dispatches[nextDispatch].coveredAt <= flown)
            {
                var d = Path.Dispatches[nextDispatch++];
                var e = new DispatchEvent
                {
                    Order = dispatches.Count + 1,
                    Node = d.node,
                    Type = d.type,
                    Class = SimulatedSensor.Classify(SimulatedSensor.ReadingFor(d.type, d.tier)),
                    FlightSeconds = Time.realtimeSinceStartup - FlightStartedAt
                };
                dispatches.Add(e);
                if (FirstDispatchAt < 0f) FirstDispatchAt = Time.realtimeSinceStartup;
                Debug.Log($"[Dispatch] #{e.Order} node {e.Node} {e.Type} -> {e.Class} (simulated sensor) at " +
                          $"T+{e.FlightSeconds:0.0} s of flight.", this);
            }

            if (flown >= Path.TotalLength)
            {
                SurveyCompleteAt = Time.realtimeSinceStartup;
                Debug.Log($"[Dispatch] Survey complete at T+{SurveyCompleteAt - FlightStartedAt:0.0} s of flight; first " +
                          $"dispatch was at T+{(FirstDispatchAt >= 0f ? FirstDispatchAt - FlightStartedAt : 0f):0.0} s.", this);
            }
        }

        // ==========================================================================================
        // Placement and anchoring
        // ==========================================================================================
        static readonly List<ARRaycastHit> s_Hits = new();

        /// <summary>
        /// Where the site goes: the floor under the screen centre, else the largest horizontal plane, else (no
        /// AR at all) a point in front of the camera. The site faces away from the viewer, so +z (north in the
        /// field report) points away from the person holding the phone.
        /// </summary>
        Pose ResolvePlacement(out bool onPlane)
        {
            onPlane = false;
            Transform cam = viewCamera != null ? viewCamera.transform : null;

            Vector3 forward = cam != null ? Vector3.ProjectOnPlane(cam.forward, Vector3.up) : Vector3.forward;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            Quaternion facing = Quaternion.LookRotation(forward.normalized, Vector3.up);

            if (raycastManager != null && viewCamera != null)
            {
                var centre = new Vector2(viewCamera.pixelWidth * 0.5f, viewCamera.pixelHeight * 0.5f);
                if (raycastManager.Raycast(centre, s_Hits, TrackableType.PlaneWithinPolygon))
                {
                    foreach (var hit in s_Hits)
                    {
                        if (hit.trackable is ARPlane plane && plane.alignment == PlaneAlignment.HorizontalUp)
                        {
                            onPlane = true;
                            return new Pose(hit.pose.position, facing);
                        }
                    }
                }
            }

            if (planeManager != null)
            {
                ARPlane best = null;
                float bestArea = 0f;
                foreach (var plane in planeManager.trackables)
                {
                    if (plane.alignment != PlaneAlignment.HorizontalUp || plane.trackingState != TrackingState.Tracking) continue;
                    float area = plane.size.x * plane.size.y;
                    if (area > bestArea) { bestArea = area; best = plane; }
                }
                if (best != null)
                {
                    onPlane = true;
                    return new Pose(best.transform.TransformPoint(best.center), facing);
                }
            }

            Vector3 origin = cam != null ? cam.position : Vector3.zero;
            return new Pose(origin + forward.normalized * fallbackForwardAndDrop.x + Vector3.down * fallbackForwardAndDrop.y,
                            facing);
        }

        /// <summary>
        /// Pin the site to the room. Parented under the anchor once it exists, so AR Foundation's corrections
        /// move the site (and its local-space flight line) with the real floor.
        /// </summary>
        async void RequestAnchor(Pose pose)
        {
            if (anchorManager == null || !anchorManager.enabled) return;
            int request = ++anchorRequest;

            Result<ARAnchor> result;
            try { result = await anchorManager.TryAddAnchorAsync(pose); }
            catch (System.Exception e)
            {
                Debug.LogWarning("[GenerativeCompiler] Anchor request threw: " + e.Message, this);
                return;
            }

            bool success = result.status.IsSuccess() && result.value != null;
            if (this == null || request != anchorRequest || layoutRoot == null)
            {
                if (success) Destroy(result.value.gameObject);      // a newer layout replaced this one
                return;
            }
            if (!success)
            {
                Debug.LogWarning($"[GenerativeCompiler] Anchor request failed ({result.status}); the site holds " +
                                 "its pose but may drift against the room.", this);
                return;
            }

            layoutAnchor = result.value;
            layoutRoot.SetParent(layoutAnchor.transform, true);
        }

        bool AssetsComplete() =>
            hazardMaterial != null && shoringMaterial != null && rescueMaterial != null && launchMaterial != null &&
            pathMaterial != null && cubeMesh != null && cylinderMesh != null;

#if UNITY_EDITOR
        [ContextMenu("Build Offline Plan Now")]
        void BuildOfflinePlanNow()
        {
            if (AgenticLLM_Bridge.TryParsePlanJson(AgenticLLM_Bridge.OfflinePlanJson, out var plan, out _))
                Compile(plan);
        }
#endif
    }
}
