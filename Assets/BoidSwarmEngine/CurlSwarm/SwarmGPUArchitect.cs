using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
#if UNITY_EDITOR
using System.Collections;
using Unity.XR.CoreUtils;
using UnityEngine.InputSystem;
using UnityEngine.XR.Simulation;
#endif

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Owns the GPU curl-noise swarm: particle ComputeBuffer, procedural index buffer, per-frame dispatch and draw,
    /// plus the 4x4x4 VPL light grid consumed by SwarmSurface. The simulation lives in this transform's local
    /// space; in <see cref="PlacementMode.CardAnchor"/> the transform follows the anchor card's pose (it is never
    /// parented to the trackable, so AR Foundation destroying the trackable cannot destroy the swarm).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SwarmGPUArchitect : MonoBehaviour
    {
        public enum PlacementMode
        {
            CardAnchor,         // follow the anchor card (King of Spades); paused and dark until it is tracked
            InFrontOfCamera     // legacy free-floating placement
        }

        const int ThreadGroupSize = 64;             // must match THREADS in SwarmCompute.compute
        const int ParticleStride = sizeof(float) * 8;
        const float MaxStep = 1f / 30f;             // AR tracking hitches must not explode the integrator
        public const int StressParticleCount = 65536;

        static readonly Bounds UnculledBounds = new(Vector3.zero, Vector3.one * 100000f);

        [Header("Render")]
        [Tooltip("Particle quad half-extent (m). Tabletop scale: 1-3 mm.")]
        public float particleScale = 0.002f;

        [Header("Placement")]
        [SerializeField] PlacementMode placementMode = PlacementMode.CardAnchor;
        [Tooltip("Extra lift (m) above the card on top of boundsExtents.y, so the swarm sits on the table.")]
        [SerializeField] float anchorLift = 0.005f;
        [Tooltip("Pose follow rate (1/s): smooths AR tracking jitter of the anchor card.")]
        [SerializeField, Min(1f)] float anchorFollowRate = 20f;
        [SerializeField, Min(0.2f)] float spawnDistance = 1.5f;
        [SerializeField] Vector3 fallbackWorldPosition = new(0f, 2f, 5f);

        [Header("GPU Assets")]
        [SerializeField] ComputeShader swarmCompute;
        [SerializeField] Material swarmMaterial;

        [Header("Population")]
        [Tooltip("Default population: stable on Adreno 650 alongside AR tracking.")]
        [SerializeField, Range(1024, 262144)] int particleCount = 16384;
        [Tooltip("Stress mode: 65,536 particles for the final recording (thermal-limit run).")]
        [SerializeField] bool stressMode;
        [SerializeField] Vector2 lifeRange = new(4f, 9f);
        [SerializeField, Range(0.05f, 1f)] float spawnRadius = 0.6f;

        [Header("Curl Flow Field (tabletop scale)")]
        [SerializeField, Min(0.01f)] float noiseFrequency = 8f;
        [SerializeField, Min(0f)] float flowSpeed = 0.12f;
        [SerializeField, Min(0f)] float responsiveness = 4f;
        [SerializeField, Min(0.01f)] float maxSpeed = 0.4f;
        [SerializeField] Vector3 scrollA = new(0.11f, 0.23f, 0.07f);
        [SerializeField] Vector3 scrollB = new(-0.17f, 0.05f, 0.19f);
        [SerializeField] bool turbulenceOctave = true;

        [Header("Containment (local space, metres)")]
        [SerializeField] Vector3 boundsCenter = Vector3.zero;
        [SerializeField] Vector3 boundsExtents = new(0.3f, 0.15f, 0.3f);
        [SerializeField, Range(0.1f, 1f)] float softShell = 0.8f;
        [SerializeField, Min(0f)] float containStrength = 3f;
        [SerializeField, Range(1f, 3f)] float hardLimit = 1.5f;

        [Header("Anomaly Core")]
        [SerializeField] Transform attractor;
        [SerializeField] float attractorStrength = 0.05f;

#if UNITY_EDITOR
        [Header("XR Simulation")]
        [Tooltip("SPACE in Play mode: snap the simulated device camera above the simulated card.")]
        [SerializeField] bool simulationFocusHotkey = true;
        [SerializeField] string simulatedCardName = "Simulated_Card";
        [SerializeField, Min(0.05f)] float simulationFocusHeight = 0.4f;
        [Tooltip("How long the simulated phone is held on a card after focusing, unless the card is read first.")]
        [SerializeField, Min(0.5f)] float simulationFocusHoldSeconds = 6f;
#endif

        [Header("Swarm Lighting (4x4x4 VPL grid)")]
        [SerializeField] ComputeShader vplCompute;
        [Tooltip("Swarm light output. Scale-normalised: irradiance at one swarm radius is ~ intensity x average emission, " +
                 "whatever the swarm's size.")]
        [SerializeField, Min(0f)] float lightIntensity = 3f;
        [Tooltip("Max distance (m) a VPL reaches; light windows smoothly to zero here.")]
        [SerializeField, Min(0.05f)] float lightRange = 0.8f;
        [Tooltip("VPL temporal smoothing rate (1/s). Higher = snappier, lower = steadier.")]
        [SerializeField, Min(0.1f)] float lightSmoothingRate = 10f;

        [Header("Alignment (Lumenforge)")]
        [Tooltip("Swarm colour when the card is aligned (GDD: cyan misaligned -> gold aligned).")]
        [ColorUsage(false, true)] [SerializeField] Color alignedSlow = new(1.60f, 0.95f, 0.25f);
        [ColorUsage(false, true)] [SerializeField] Color alignedFast = new(3.00f, 2.10f, 0.70f);

        static readonly int ID_Particles = Shader.PropertyToID("_Particles");
        static readonly int ID_DeltaTime = Shader.PropertyToID("_DeltaTime");
        static readonly int ID_RelaxFactor = Shader.PropertyToID("_RelaxFactor");
        static readonly int ID_FrameSeed = Shader.PropertyToID("_FrameSeed");
        static readonly int ID_NoiseOffsetA = Shader.PropertyToID("_NoiseOffsetA");
        static readonly int ID_NoiseOffsetB = Shader.PropertyToID("_NoiseOffsetB");
        static readonly int ID_NoiseFrequency = Shader.PropertyToID("_NoiseFrequency");
        static readonly int ID_FlowSpeed = Shader.PropertyToID("_FlowSpeed");
        static readonly int ID_MaxSpeed = Shader.PropertyToID("_MaxSpeed");
        static readonly int ID_BoundsCenter = Shader.PropertyToID("_BoundsCenter");
        static readonly int ID_BoundsExtents = Shader.PropertyToID("_BoundsExtents");
        static readonly int ID_InvBoundsExtents = Shader.PropertyToID("_InvBoundsExtents");
        static readonly int ID_SoftShell = Shader.PropertyToID("_SoftShell");
        static readonly int ID_ContainStrength = Shader.PropertyToID("_ContainStrength");
        static readonly int ID_HardLimit = Shader.PropertyToID("_HardLimit");
        static readonly int ID_Attractor = Shader.PropertyToID("_Attractor");
        static readonly int ID_AttractorStrength = Shader.PropertyToID("_AttractorStrength");
        static readonly int ID_LifeRange = Shader.PropertyToID("_LifeRange");
        static readonly int ID_SpawnRadius = Shader.PropertyToID("_SpawnRadius");
        static readonly int ID_SwarmLocalToWorld = Shader.PropertyToID("_SwarmLocalToWorld");
        static readonly int ID_InvMaxSpeed = Shader.PropertyToID("_InvMaxSpeed");
        static readonly int ID_ParticleScale = Shader.PropertyToID("_ParticleScale");
        static readonly int ID_SwarmParticles = Shader.PropertyToID("_SwarmParticles");
        static readonly int ID_EnvDepth = Shader.PropertyToID("_EnvDepth");
        static readonly int ID_DepthParams = Shader.PropertyToID("_DepthParams");
        static readonly int ID_DepthTexSize = Shader.PropertyToID("_DepthTexSize");
        static readonly int ID_DepthDisplay = Shader.PropertyToID("_DepthDisplay");
        static readonly int ID_SwarmToClip = Shader.PropertyToID("_SwarmToClip");
        static readonly int ID_CameraPosLocal = Shader.PropertyToID("_CameraPosLocal");
        static readonly int ID_WorldToLocalScale = Shader.PropertyToID("_WorldToLocalScale");

        [Header("Real-World Avoidance (AR Depth)")]
        [Tooltip("Particles closer than this (m) to a real surface, along the camera ray, are pushed back out.")]
        [SerializeField, Min(0.005f)] float depthSkin = 0.04f;
        [Tooltip("Push acceleration (m/s^2) out of real surfaces.")]
        [SerializeField, Min(0f)] float depthPush = 3f;
        [Tooltip("Only this deep (m) past a real surface counts as 'inside' it; deeper is simply occluded.")]
        [SerializeField, Min(0.05f)] float depthBand = 0.35f;

        // Real-world depth, fed by SwarmDepthField. A 1x1 stand-in stays bound whenever it is not, because a
        // compute kernel that declares a texture refuses to dispatch without one.
        static Texture2D s_NoDepth;
        Texture depthTexture;
        Matrix4x4 depthDisplay = Matrix4x4.identity;
        Camera depthCamera;
        float flowBoost = 1f;

        const int VPLGrid = 4;
        public const int VPLCells = VPLGrid * VPLGrid * VPLGrid;    // must match CELLS in SwarmVPL.compute

        // Irradiance volume: must match VOL_X/Y/Z and VOL_THREADS in SwarmVPL.compute. 2,048 probes replace a
        // 64-light loop that every lit fragment used to run.
        const int VolumeX = 16, VolumeY = 8, VolumeZ = 16, VolumeThreads = 4;
        const int VPLFields = 7;                                    // must match FIELDS
        const int VPLAccumulateThreads = 256;                       // must match ACC_THREADS
        const int VPLParticlesPerThread = 4;
        const float VPLMaxContribution = 60f;                       // must match MAX_CONTRIB

        /// <summary>One VPL entry as the shader sees it: float4 posSoftSq + float4 colour.</summary>
        struct VPLEntry
        {
            public Vector4 PosSoftSq;
            public Vector4 Color;
        }

        readonly VPLEntry[] beaconScratch = new VPLEntry[SwarmLightingGlobals.BeaconSlots];

        static readonly int ID_VPLAccum = Shader.PropertyToID("_VPLAccum");
        static readonly int ID_VPLState = Shader.PropertyToID("_VPLState");
        static readonly int ID_VPLOut = Shader.PropertyToID("_VPLOut");
        static readonly int ID_ParticleCount = Shader.PropertyToID("_ParticleCount");
        static readonly int ID_ParticlesPerThread = Shader.PropertyToID("_ParticlesPerThread");
        static readonly int ID_FixedScale = Shader.PropertyToID("_FixedScale");
        static readonly int ID_GridMin = Shader.PropertyToID("_GridMin");
        static readonly int ID_GridCellSize = Shader.PropertyToID("_GridCellSize");
        static readonly int ID_GridInvCellSize = Shader.PropertyToID("_GridInvCellSize");
        static readonly int ID_ColorSlow = Shader.PropertyToID("_ColorSlow");
        static readonly int ID_ColorFast = Shader.PropertyToID("_ColorFast");
        static readonly int ID_EmissionIntensity = Shader.PropertyToID("_EmissionIntensity");
        static readonly int ID_EmissionSpeedBoost = Shader.PropertyToID("_EmissionSpeedBoost");
        static readonly int ID_FluxScale = Shader.PropertyToID("_FluxScale");
        static readonly int ID_Smoothing = Shader.PropertyToID("_Smoothing");
        static readonly int ID_SoftRadiusSq = Shader.PropertyToID("_SoftRadiusSq");
        static readonly int ID_IrrL0 = Shader.PropertyToID("_IrrL0");
        static readonly int ID_IrrL1 = Shader.PropertyToID("_IrrL1");
        static readonly int ID_VolumeMin = Shader.PropertyToID("_VolumeMin");
        static readonly int ID_VolumeCellSize = Shader.PropertyToID("_VolumeCellSize");
        static readonly int ID_VolumeInvRangeSq = Shader.PropertyToID("_VolumeInvRangeSq");
        static readonly int ID_VolumeLightCount = Shader.PropertyToID("_VolumeLightCount");

        /// <summary>The live swarm instance (read by tools, the card rig and diagnostics).</summary>
        public static SwarmGPUArchitect Active { get; private set; }

        /// <summary>The live particle buffer (float4 posLife, float4 velInvLife; local space). Null when no swarm runs.</summary>
        public static ComputeBuffer SharedParticleBuffer { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Required with Enter Play Mode Options (domain reload disabled).
            Active = null;
            SharedParticleBuffer = null;
        }

        ComputeBuffer particleBuffer;
        GraphicsBuffer indexBuffer;
        ComputeBuffer vplAccumBuffer;
        ComputeBuffer vplStateBuffer;
        ComputeBuffer vplOutBuffer;
        int kernelVPLAccumulate = -1;
        int kernelVPLResolve = -1;
        int kernelVPLVolume = -1;
        RenderTexture irradianceL0;
        RenderTexture irradianceL1;
        int vplGroups;
        MaterialPropertyBlock propertyBlock;
        RenderParams renderParams;
        LocalKeyword turbulenceKeyword;

        int kernelInit;
        int kernelUpdate;
        int threadGroups;
        int indexCount;
        uint frameIndex;
        Vector3 noiseOffsetA;
        Vector3 noiseOffsetB = new(17.3f, -9.1f, 41.7f);
        bool paramsDirty = true;
        bool initialized;

        Transform anchor;
        bool anchorTracking;
        bool anchorSnapped;

        public int ParticleCount => threadGroups * ThreadGroupSize;
        public bool IsReady => initialized && particleBuffer != null && particleBuffer.IsValid();
        public Material SwarmMaterial => swarmMaterial;
        public float MaxSpeed => maxSpeed;
        public Vector3 BoundsCenter => boundsCenter;
        /// <summary>Local-space radius enclosing every live particle (containment ellipsoid x hard limit).</summary>
        public float BoundsRadius => Mathf.Max(boundsExtents.x, Mathf.Max(boundsExtents.y, boundsExtents.z)) * hardLimit;
        public PlacementMode Placement => placementMode;
        public Transform Anchor => anchor;

        /// <summary>
        /// Half-extents of the simulation volume, in world metres. Settable so the cloud can be sized to whatever
        /// it is orbiting: the defaults were authored for a 10 cm card, and on a table-scaled world that same
        /// volume swallows the whole map and every card lying on it.
        /// </summary>
        public Vector3 BoundsExtents
        {
            get => boundsExtents;
            set
            {
                var next = new Vector3(Mathf.Max(value.x, 1e-3f), Mathf.Max(value.y, 1e-3f), Mathf.Max(value.z, 1e-3f));
                if (next == boundsExtents) return;
                boundsExtents = next;
                paramsDirty = true;
            }
        }
        public bool IsAnchorTracking => anchor != null && anchorTracking;
        /// <summary>Paused swarms neither simulate, draw nor emit light (card lost = void).</summary>
        public bool IsPaused => placementMode == PlacementMode.CardAnchor && !IsAnchorTracking;
        public ComputeBuffer VPLBuffer => vplOutBuffer;

        /// <summary>0 = diagnostic cyan, 1 = ignited gold. Tints both the rendered particles and the light they
        /// cast, so the swarm's colour and the cave's illumination can never disagree.</summary>
        public float Alignment { get; set; }
        public bool LightingActive => vplOutBuffer != null;

        /// <summary>
        /// Multiplies the flow field's speed and the speed cap. Raised while the swarm is travelling between act
        /// anchors, so it reads as a current pouring across the room rather than a cloud being carried.
        /// </summary>
        public float FlowBoost
        {
            get => flowBoost;
            set
            {
                float next = Mathf.Clamp(value, 0.1f, 10f);
                if (Mathf.Approximately(next, flowBoost)) return;
                flowBoost = next;
                paramsDirty = true;
            }
        }

        /// <summary>True while a real-world depth map is steering particles out of physical surfaces.</summary>
        public bool DepthAvoidanceActive => depthTexture != null && depthCamera != null;

        /// <summary>
        /// Hand the swarm this frame's environment depth. <paramref name="displayMatrix"/> is the AR camera's
        /// display transform: the same one the camera background samples the depth texture through.
        /// </summary>
        public void SetDepthField(Texture environmentDepth, Matrix4x4 displayMatrix, Camera viewCamera)
        {
            depthTexture = environmentDepth;
            depthDisplay = displayMatrix;
            depthCamera = viewCamera;
        }

        public void ClearDepthField()
        {
            depthTexture = null;
            depthCamera = null;
        }

        /// <summary>Stress mode (65,536 particles). Toggling at runtime rebuilds every GPU buffer.</summary>
        public bool StressMode
        {
            get => stressMode;
            set
            {
                if (stressMode == value) return;
                stressMode = value;
                if (initialized) Rebuild();
            }
        }

        // ------------------------------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------------------------------
        void Start()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.maxComputeBufferInputsVertex < 1)
            {
                Debug.LogError("[SwarmGPUArchitect] Device lacks compute or vertex-stage SSBO support. Swarm disabled.", this);
                enabled = false;
                return;
            }

            if (swarmCompute == null || swarmMaterial == null)
            {
                Debug.LogError("[SwarmGPUArchitect] Compute shader or material not assigned.", this);
                enabled = false;
                return;
            }

            if (placementMode == PlacementMode.CardAnchor && FindAnyObjectByType<ARTrackedImageManager>() == null)
            {
                Debug.LogError("[SwarmGPUArchitect] CardAnchor placement needs an ARTrackedImageManager in the scene; " +
                               "falling back to InFrontOfCamera.", this);
                placementMode = PlacementMode.InFrontOfCamera;
            }

            if (placementMode == PlacementMode.InFrontOfCamera) PlaceAtEyeLevel();

            Initialize();
        }

        void OnDestroy() => Teardown();

        void OnValidate()
        {
            paramsDirty = true;
#if UNITY_EDITOR
            // Auto-wire the VPL compute so existing scenes light up without setup.
            if (vplCompute == null)
            {
                string[] guids = UnityEditor.AssetDatabase.FindAssets("SwarmVPL t:ComputeShader");
                if (guids.Length > 0)
                {
                    vplCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
                    UnityEditor.EditorApplication.delayCall += MarkDirtyDeferred;
                }
            }
#endif
        }

#if UNITY_EDITOR
        void MarkDirtyDeferred()
        {
            if (this != null) UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        void Rebuild()
        {
            Teardown();
            paramsDirty = true;
            Initialize();
        }

        void Initialize()
        {
            int requested = stressMode ? StressParticleCount : particleCount;
            threadGroups = (requested + ThreadGroupSize - 1) / ThreadGroupSize;
            int count = threadGroups * ThreadGroupSize;

            particleBuffer = new ComputeBuffer(count, ParticleStride, ComputeBufferType.Structured);
            indexBuffer = BuildQuadIndexBuffer(count, out indexCount);

            kernelInit = swarmCompute.FindKernel("CSInit");
            kernelUpdate = swarmCompute.FindKernel("CSUpdate");
            swarmCompute.SetBuffer(kernelInit, ID_Particles, particleBuffer);
            swarmCompute.SetBuffer(kernelUpdate, ID_Particles, particleBuffer);
            turbulenceKeyword = new LocalKeyword(swarmCompute, "SWARM_TURBULENCE_OCTAVE");

            // Bind on the material, the property block and globally so every draw path sees the SRV.
            swarmMaterial.SetBuffer(ID_Particles, particleBuffer);
            Shader.SetGlobalBuffer(ID_Particles, particleBuffer);
            propertyBlock = new MaterialPropertyBlock();
            propertyBlock.SetBuffer(ID_Particles, particleBuffer);

            renderParams = new RenderParams(swarmMaterial)
            {
                matProps = propertyBlock,
                worldBounds = UnculledBounds,
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
                motionVectorMode = MotionVectorGenerationMode.ForceNoMotion
            };

            PushStaticParams();
            swarmCompute.Dispatch(kernelInit, threadGroups, 1, 1);
            initialized = true;

            Active = this;
            SharedParticleBuffer = particleBuffer;
            Shader.SetGlobalBuffer(ID_SwarmParticles, particleBuffer);

            InitializeLighting(count);
            Debug.Log($"[SwarmGPUArchitect] {count:N0} particles ({(stressMode ? "STRESS" : "default")}), " +
                      $"placement {placementMode}, lighting {(LightingActive ? "on" : "OFF")}.", this);
        }

        void Teardown()
        {
            if (Active == this)
            {
                Active = null;
                SharedParticleBuffer = null;
            }

            if (vplOutBuffer != null) SwarmLightingGlobals.Unbind();
            ReleaseIrradianceVolume();
            vplAccumBuffer?.Release();
            vplAccumBuffer = null;
            vplStateBuffer?.Release();
            vplStateBuffer = null;
            vplOutBuffer?.Release();
            vplOutBuffer = null;

            initialized = false;
            particleBuffer?.Release();
            particleBuffer = null;
            indexBuffer?.Release();
            indexBuffer = null;
        }

        // ------------------------------------------------------------------------------------------
        // Anchoring (driven by SpatialMatrixCardRig)
        // ------------------------------------------------------------------------------------------
        /// <summary>Follow a card's pose. The swarm sits on the card: centre lifted by boundsExtents.y.</summary>
        public void AttachToAnchor(Transform card)
        {
            if (anchor != card) anchorSnapped = false;
            anchor = card;
        }

        public void SetAnchorTracking(bool tracking) => anchorTracking = tracking;

        public void DetachAnchor()
        {
            anchor = null;
            anchorTracking = false;
            anchorSnapped = false;
        }

        void FollowAnchor(float dt)
        {
            if (anchor == null) return;

            Vector3 targetPos = anchor.TransformPoint(new Vector3(0f, boundsExtents.y + anchorLift, 0f));
            Quaternion targetRot = anchor.rotation;

            if (!anchorSnapped)
            {
                transform.SetPositionAndRotation(targetPos, targetRot);
                anchorSnapped = true;
                return;
            }

            float t = 1f - Mathf.Exp(-anchorFollowRate * dt);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, targetPos, t),
                Quaternion.Slerp(transform.rotation, targetRot, t));
        }

        // ------------------------------------------------------------------------------------------
        // Frame
        // ------------------------------------------------------------------------------------------
        void Update()
        {
#if UNITY_EDITOR
            if (simulationFocusHotkey && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                FocusSimulatedCard();
#endif

            if (!initialized) return;

            float rawDt = Time.deltaTime;
            float dt = Mathf.Min(rawDt, MaxStep);

            if (placementMode == PlacementMode.CardAnchor)
            {
                if (anchor == null) anchorTracking = false;   // trackable destroyed by AR Foundation
                FollowAnchor(rawDt);
            }

            if (IsPaused)
            {
                // Card not visible: the swarm holds its state, emits nothing and the matrix returns to the void.
                if (LightingActive)
                {
                    SwarmLightingGlobals.UpdateParams(0, 1f, lightRange, 0);
                    SwarmLightingGlobals.ClearVolume();
                }
                return;
            }

            if (paramsDirty) PushStaticParams();

            noiseOffsetA += scrollA * dt;
            noiseOffsetB += scrollB * dt;
            frameIndex++;

            Vector3 core = attractor != null ? transform.InverseTransformPoint(attractor.position) : boundsCenter;

            swarmCompute.SetFloat(ID_DeltaTime, dt);
            swarmCompute.SetFloat(ID_RelaxFactor, 1f - Mathf.Exp(-responsiveness * dt));
            swarmCompute.SetInt(ID_FrameSeed, unchecked((int)(frameIndex * 2654435761u)));
            swarmCompute.SetVector(ID_NoiseOffsetA, noiseOffsetA);
            swarmCompute.SetVector(ID_NoiseOffsetB, noiseOffsetB);
            swarmCompute.SetVector(ID_Attractor, core);
            BindDepthField();
            swarmCompute.Dispatch(kernelUpdate, threadGroups, 1, 1);

            DispatchLighting(dt);

            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            swarmMaterial.SetBuffer(ID_Particles, particleBuffer);
            swarmMaterial.SetMatrix(ID_SwarmLocalToWorld, localToWorld);
            swarmMaterial.SetFloat(ID_ParticleScale, particleScale);
            propertyBlock.SetBuffer(ID_Particles, particleBuffer);
            propertyBlock.SetMatrix(ID_SwarmLocalToWorld, localToWorld);
            propertyBlock.SetColor(ID_ColorSlow, Color.Lerp(swarmMaterial.GetColor(ID_ColorSlow), alignedSlow, Alignment));
            propertyBlock.SetColor(ID_ColorFast, Color.Lerp(swarmMaterial.GetColor(ID_ColorFast), alignedFast, Alignment));
            renderParams.worldBounds = UnculledBounds;
            Graphics.RenderPrimitivesIndexed(renderParams, MeshTopology.Triangles, indexBuffer, indexCount);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Puts the XR camera in front of the simulated card so AR Foundation's discovery can register it.
        ///
        /// Two spaces are involved and both must be satisfied:
        ///  1. The simulated DEVICE pose (SimulationCameraPoseProvider's transform) feeds the XR input subsystem.
        ///  2. AR Foundation's simulated image discovery compares the XR camera's WORLD pose against the simulated
        ///     image's WORLD pose. The XR Origin's own offset sits between the two, so setting the device pose alone
        ///     leaves the camera wherever the origin puts it (here: ~14 m from the environment).
        /// The origin is therefore nudged over the next frames until the camera physically lands on the target.
        ///
        /// The aim uses the card's own surface normal (transform.up), not world down: discovery requires
        /// dot(cameraForward, image.up) &lt;= 0.1, and this card is tilted ~69 degrees off vertical.
        /// </summary>
        void FocusSimulatedCard() => FocusSimulatedCard(simulatedCardName);

        // Where the simulated phone was before it was aimed at a card, so the player can be put back.
        bool haveSavedView;
        Pose savedOriginPose, savedDevicePose;

        /// <summary>Editor only: aim the simulated phone at the named simulated card (see the card gates).</summary>
        public void FocusSimulatedCard(string cardObjectName)
        {
            var card = GameObject.Find(cardObjectName);
            if (card == null)
            {
                Debug.LogWarning($"[SwarmGPUArchitect] '{cardObjectName}' not found. Is XR Simulation running with " +
                                 "the MatrixSimulationEnvironment loaded?", this);
                return;
            }

            var origin = FindAnyObjectByType<XROrigin>();
            var poseProvider = FindAnyObjectByType<SimulationCameraPoseProvider>();
            if (!haveSavedView && origin != null && poseProvider != null)
            {
                savedOriginPose = new Pose(origin.transform.position, origin.transform.rotation);
                savedDevicePose = new Pose(poseProvider.transform.position, poseProvider.transform.rotation);
                haveSavedView = true;
            }

            StopAllCoroutines();
            StartCoroutine(FocusRoutine(card.transform));
        }

        /// <summary>Editor only: undo <see cref="FocusSimulatedCard(string)"/>. No-op if nothing was focused.</summary>
        public void RestoreSimulatedView()
        {
            if (!haveSavedView) return;
            haveSavedView = false;
            StopAllCoroutines();

            var origin = FindAnyObjectByType<XROrigin>();
            if (origin != null) origin.transform.SetPositionAndRotation(savedOriginPose.position, savedOriginPose.rotation);
            var poseProvider = FindAnyObjectByType<SimulationCameraPoseProvider>();
            if (poseProvider != null)
                poseProvider.transform.SetPositionAndRotation(savedDevicePose.position, savedDevicePose.rotation);
        }

        IEnumerator FocusRoutine(Transform card)
        {
            var origin = FindAnyObjectByType<XROrigin>();
            Camera cam = origin != null ? origin.Camera : Camera.main;
            if (origin == null || cam == null)
            {
                Debug.LogWarning("[SwarmGPUArchitect] No XR Origin camera to focus.", this);
                yield break;
            }

            Vector3 normal = card.up;                                   // image surface normal
            Vector3 target = card.position + normal * simulationFocusHeight;
            Vector3 lookDir = -normal;
            Vector3 upHint = Mathf.Abs(Vector3.Dot(lookDir, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            Quaternion look = Quaternion.LookRotation(lookDir, upHint);

            var poseProvider = FindAnyObjectByType<SimulationCameraPoseProvider>();
            if (poseProvider == null)
            {
                Debug.LogWarning("[SwarmGPUArchitect] No SimulationCameraPoseProvider: is XR Simulation running?", this);
                yield break;
            }

            // Two frames have to agree, and both are needed:
            //  1. The simulated DEVICE pose lives in the simulation room's space (and is clamped to that room), so
            //     it is set to the target directly - the room renders from there.
            //  2. Image discovery compares the XR camera's WORLD pose with the image's WORLD pose, and the XR
            //     Origin sits between device and world. The origin is therefore solved in closed form so that
            //     origin x (camera pose inside the origin) == target. The old approach nudged the origin after the
            //     camera a frame at a time, lagged, and could stop a metre short - so the card was never seen.
            // Moving the origin is safe here: a scan freezes the locked world, and RestoreSimulatedView puts the
            // origin back before the world starts listening to its anchor again.
            // The phone is then HELD on the card - re-solved every frame, because the simulated device pose keeps
            // settling for a few frames after it is set - until RestoreSimulatedView (card read) or the timeout.
            // A card only counts once it has been tracked continuously, so a camera that drifts off it after one
            // frame leaves it forever 'Limited' and the gate never opens.
            poseProvider.transform.SetPositionAndRotation(target, look);
            float holdUntil = Time.unscaledTime + simulationFocusHoldSeconds;
            while (Time.unscaledTime < holdUntil)
            {
                yield return null;                                       // let the pose driver apply the device pose
                if (cam == null) yield break;

                Transform o = origin.transform;
                Vector3 camInOrigin = o.InverseTransformPoint(cam.transform.position);
                Quaternion camRotInOrigin = Quaternion.Inverse(o.rotation) * cam.transform.rotation;
                Quaternion originRotation = look * Quaternion.Inverse(camRotInOrigin);
                o.SetPositionAndRotation(target - originRotation * camInOrigin, originRotation);
            }

            float finalDistance = Vector3.Distance(cam.transform.position, card.position);
            float normalDot = Vector3.Dot(cam.transform.forward, card.up);
            Debug.Log($"[SwarmGPUArchitect] Focus: camera {finalDistance:0.00} m from '{card.name}', " +
                      $"dot(camForward, image.up) {normalDot:0.00} (needs <= 0.10). " +
                      $"Camera {cam.transform.position}, card {card.position}.", this);
        }
#endif

        // ------------------------------------------------------------------------------------------
        // Swarm lighting: particles -> 4x4x4 virtual point lights -> global buffer for SwarmSurface.
        // ------------------------------------------------------------------------------------------
        void InitializeLighting(int count)
        {
            if (vplCompute == null)
            {
                Debug.LogError("[SwarmGPUArchitect] VPL compute not assigned: swarm will not emit light.", this);
                return;
            }

            kernelVPLAccumulate = vplCompute.FindKernel("CSAccumulate");
            kernelVPLResolve = vplCompute.FindKernel("CSResolve");
            kernelVPLVolume = vplCompute.FindKernel("CSVolume");
            if (!vplCompute.IsSupported(kernelVPLAccumulate) || !vplCompute.IsSupported(kernelVPLResolve) ||
                !vplCompute.IsSupported(kernelVPLVolume))
            {
                Debug.LogError("[SwarmGPUArchitect] SwarmVPL kernels failed to compile or are unsupported: swarm lighting disabled.", this);
                return;
            }

            vplAccumBuffer = new ComputeBuffer(VPLCells * VPLFields, sizeof(uint), ComputeBufferType.Structured);
            vplAccumBuffer.SetData(new uint[VPLCells * VPLFields]);
            vplStateBuffer = new ComputeBuffer(VPLCells * 2, sizeof(float) * 4, ComputeBufferType.Structured);
            vplStateBuffer.SetData(new Vector4[VPLCells * 2]);
            // Cells 0..63 are written by the compute pass; the tail slots are beacons written from the CPU,
            // so emissive content lights the cave through the same grid.
            vplOutBuffer = new ComputeBuffer(VPLCells + SwarmLightingGlobals.BeaconSlots,
                                             SwarmLightingGlobals.VPLStride, ComputeBufferType.Structured);
            vplOutBuffer.SetData(new Vector4[(VPLCells + SwarmLightingGlobals.BeaconSlots) * 2]);

            vplCompute.SetBuffer(kernelVPLAccumulate, ID_Particles, particleBuffer);
            vplCompute.SetBuffer(kernelVPLAccumulate, ID_VPLAccum, vplAccumBuffer);
            vplCompute.SetBuffer(kernelVPLResolve, ID_VPLAccum, vplAccumBuffer);
            vplCompute.SetBuffer(kernelVPLResolve, ID_VPLState, vplStateBuffer);
            vplCompute.SetBuffer(kernelVPLResolve, ID_VPLOut, vplOutBuffer);

            irradianceL0 = CreateIrradianceVolume("SwarmIrradiance_L0");
            irradianceL1 = CreateIrradianceVolume("SwarmIrradiance_L1");

            // If a device cannot give us a writable half-float 3D target, fall back to integrating the VPLs per
            // fragment rather than shading the world from an empty volume - slow beats black.
            if (irradianceL0 == null || irradianceL1 == null)
            {
                Debug.LogWarning("[SwarmGPUArchitect] No writable ARGBHalf 3D target: falling back to the per-pixel " +
                                 "VPL loop (SWARM_LEGACY_VPL_LOOP).", this);
                ReleaseIrradianceVolume();
                Shader.EnableKeyword("SWARM_LEGACY_VPL_LOOP");
            }
            else
            {
                Shader.DisableKeyword("SWARM_LEGACY_VPL_LOOP");
                vplCompute.SetBuffer(kernelVPLVolume, ID_VPLOut, vplOutBuffer);
                vplCompute.SetTexture(kernelVPLVolume, ID_IrrL0, irradianceL0);
                vplCompute.SetTexture(kernelVPLVolume, ID_IrrL1, irradianceL1);
                vplCompute.SetInt(ID_VolumeLightCount, VPLCells);
            }

            int perGroup = VPLAccumulateThreads * VPLParticlesPerThread;
            vplGroups = (count + perGroup - 1) / perGroup;
            vplCompute.SetInt(ID_ParticleCount, count);
            vplCompute.SetInt(ID_ParticlesPerThread, VPLParticlesPerThread);

            // Largest fixed-point scale that keeps count x MAX_CONTRIB x scale inside uint32.
            float fixedScale = Mathf.Min(512f, 4.0e9f / (count * VPLMaxContribution));
            vplCompute.SetFloat(ID_FixedScale, fixedScale);

            SwarmLightingGlobals.Bind(vplOutBuffer, IsPaused ? 0 : VPLCells, 1f, lightRange, 0);
            SwarmLightingGlobals.ClearVolume();      // nothing resolved yet: the first frame stays dark, not stale
        }

        /// <summary>
        /// A 16x8x16 half-float 3D render texture: 2,048 texels, 16 KB per channel set. Bilinear on a 3D texture
        /// is a hardware trilinear fetch, which is what makes the pixel-shader side two instructions of work.
        /// </summary>
        RenderTexture CreateIrradianceVolume(string name)
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ||
                !SystemInfo.supportsComputeShaders)
                return null;

            var volume = new RenderTexture(VolumeX, VolumeY, 0, RenderTextureFormat.ARGBHalf)
            {
                name = name,
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = VolumeZ,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            return volume.Create() ? volume : null;
        }

        void ReleaseIrradianceVolume()
        {
            if (irradianceL0 != null) { irradianceL0.Release(); Destroy(irradianceL0); irradianceL0 = null; }
            if (irradianceL1 != null) { irradianceL1.Release(); Destroy(irradianceL1); irradianceL1 = null; }
        }

        void DispatchLighting(float dt)
        {
            if (vplOutBuffer == null) return;

            // Grid spans the containment ellipsoid's box in local space; shell particles clamp to border cells.
            Vector3 ext = new(
                Mathf.Max(boundsExtents.x, 1e-3f),
                Mathf.Max(boundsExtents.y, 1e-3f),
                Mathf.Max(boundsExtents.z, 1e-3f));
            Vector3 cell = ext * (2f / VPLGrid);
            vplCompute.SetVector(ID_GridMin, boundsCenter - ext);
            vplCompute.SetVector(ID_GridCellSize, cell);
            vplCompute.SetVector(ID_GridInvCellSize, new Vector3(1f / cell.x, 1f / cell.y, 1f / cell.z));

            // Mirror the particle material's HDR emission (material colours reach shaders in linear space).
            Color slow = Color.Lerp(swarmMaterial.GetColor(ID_ColorSlow), alignedSlow, Alignment);
            Color fast = Color.Lerp(swarmMaterial.GetColor(ID_ColorFast), alignedFast, Alignment);
            vplCompute.SetVector(ID_ColorSlow, ToShaderColor(slow));
            vplCompute.SetVector(ID_ColorFast, ToShaderColor(fast));
            vplCompute.SetFloat(ID_EmissionSpeedBoost, swarmMaterial.GetFloat(ID_EmissionSpeedBoost));
            vplCompute.SetFloat(ID_InvMaxSpeed, 1f / maxSpeed);

            Vector3 s = transform.lossyScale;
            float worldScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));

            // Scale-normalised flux: total flux ~ intensity x emission x R^2 (R = world swarm radius), so the
            // irradiance a surface sees at ~R from the swarm is independent of the swarm's physical size.
            // Shrinking the swarm from room scale to tabletop no longer multiplies brightness by (R_old/R_new)^2.
            float worldRadius = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z)) * worldScale;
            float emissionIntensity = swarmMaterial.GetFloat(ID_EmissionIntensity);
            vplCompute.SetFloat(ID_FluxScale, lightIntensity * emissionIntensity * worldRadius * worldRadius / ParticleCount);

            vplCompute.SetFloat(ID_Smoothing, 1f - Mathf.Exp(-lightSmoothingRate * Mathf.Max(dt, 1e-4f)));

            // Soft core = half a cell (world): a surface inside a cell never sees a 1/d^2 spike.
            float soft = 0.5f * Mathf.Max(cell.x, Mathf.Max(cell.y, cell.z)) * worldScale;
            vplCompute.SetFloat(ID_SoftRadiusSq, soft * soft);
            vplCompute.SetMatrix(ID_SwarmLocalToWorld, transform.localToWorldMatrix);

            vplCompute.Dispatch(kernelVPLAccumulate, vplGroups, 1, 1);
            vplCompute.Dispatch(kernelVPLResolve, 1, 1, 1);

            int beacons = UploadBeacons();
            int lightCount = VPLCells + beacons;

            // The volume is an axis-aligned world box around the swarm, inflated by the light range so a surface
            // just outside the cloud still receives the falloff's tail. Axis aligned on purpose: the pixel shader
            // then maps world -> volume with three mads instead of a matrix multiply.
            // BoundsRadius, not the containment extent: particles live out to the hard limit.
            float volumeRadius = BoundsRadius * worldScale;
            Vector3 centre = transform.TransformPoint(boundsCenter);
            Vector3 half = Vector3.one * (volumeRadius + lightRange);
            Vector3 min = centre - half;
            Vector3 size = half * 2f;

            vplCompute.SetVector(ID_VolumeMin, min);
            vplCompute.SetVector(ID_VolumeCellSize, new Vector3(size.x / VolumeX, size.y / VolumeY, size.z / VolumeZ));
            vplCompute.SetFloat(ID_VolumeInvRangeSq, 1f / Mathf.Max(lightRange * lightRange, 1e-4f));
            vplCompute.Dispatch(kernelVPLVolume, VolumeX / VolumeThreads, VolumeY / VolumeThreads, VolumeZ / VolumeThreads);

            SwarmLightingGlobals.UpdateParams(lightCount, 1f, lightRange, beacons);
            SwarmLightingGlobals.BindVolume(irradianceL0, irradianceL1, min,
                                            new Vector3(1f / size.x, 1f / size.y, 1f / size.z));
        }

        /// <summary>Writes registered beacons into the tail of the VPL buffer. Returns how many are live.</summary>
        int UploadBeacons()
        {
            var beacons = SwarmLightingGlobals.Beacons;
            int n = 0;
            for (int i = 0; i < beacons.Count && n < SwarmLightingGlobals.BeaconSlots; i++)
            {
                var beacon = beacons[i];
                if (beacon == null || !beacon.BeaconActive) continue;

                Vector3 p = beacon.BeaconPosition;
                float soft = Mathf.Max(beacon.BeaconSoftRadius, 1e-3f);
                Color flux = beacon.BeaconFlux;
                if (QualitySettings.activeColorSpace == ColorSpace.Linear) flux = flux.linear;

                beaconScratch[n].PosSoftSq = new Vector4(p.x, p.y, p.z, soft * soft);
                beaconScratch[n].Color = new Vector4(flux.r, flux.g, flux.b, 0f);
                n++;
            }

            if (n > 0) vplOutBuffer.SetData(beaconScratch, 0, VPLCells, n);
            return n;
        }

        void BindDepthField()
        {
            bool live = depthTexture != null && depthCamera != null;
            if (!live)
            {
                if (s_NoDepth == null)
                {
                    s_NoDepth = new Texture2D(1, 1, TextureFormat.RFloat, false, true)
                    {
                        name = "SwarmNoDepth",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    s_NoDepth.SetPixel(0, 0, Color.clear);
                    s_NoDepth.Apply(false, true);
                }
                swarmCompute.SetTexture(kernelUpdate, ID_EnvDepth, s_NoDepth);
                swarmCompute.SetVector(ID_DepthParams, Vector4.zero);
                return;
            }

            Transform cam = depthCamera.transform;
            Matrix4x4 swarmToClip = depthCamera.projectionMatrix * depthCamera.worldToCameraMatrix *
                                    transform.localToWorldMatrix;
            float worldToLocal = 1f / Mathf.Max(transform.lossyScale.x, 1e-6f);

            swarmCompute.SetTexture(kernelUpdate, ID_EnvDepth, depthTexture);
            swarmCompute.SetVector(ID_DepthParams, new Vector4(1f, depthSkin, depthPush, depthBand));
            swarmCompute.SetVector(ID_DepthTexSize, new Vector4(depthTexture.width, depthTexture.height, 0f, 0f));
            swarmCompute.SetMatrix(ID_DepthDisplay, depthDisplay);
            swarmCompute.SetMatrix(ID_SwarmToClip, swarmToClip);
            swarmCompute.SetVector(ID_CameraPosLocal, transform.InverseTransformPoint(cam.position));
            swarmCompute.SetFloat(ID_WorldToLocalScale, worldToLocal);
        }

        static Vector4 ToShaderColor(Color c) =>
            QualitySettings.activeColorSpace == ColorSpace.Linear ? (Vector4)c.linear : (Vector4)c;

        // The XR Origin is not at the world origin, so a hard-coded world position can land behind the camera.
        void PlaceAtEyeLevel()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                transform.position = fallbackWorldPosition;
                return;
            }

            Transform camTransform = cam.transform;
            Vector3 forward = Vector3.ProjectOnPlane(camTransform.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(camTransform.up, Vector3.up);
            forward.Normalize();

            transform.SetPositionAndRotation(
                camTransform.position + forward * spawnDistance,
                Quaternion.LookRotation(forward, Vector3.up));
        }

        void PushStaticParams()
        {
            Vector3 ext = new(
                Mathf.Max(boundsExtents.x, 1e-3f),
                Mathf.Max(boundsExtents.y, 1e-3f),
                Mathf.Max(boundsExtents.z, 1e-3f));

            swarmCompute.SetFloat(ID_NoiseFrequency, noiseFrequency);
            swarmCompute.SetFloat(ID_FlowSpeed, flowSpeed * flowBoost);
            swarmCompute.SetFloat(ID_MaxSpeed, maxSpeed * flowBoost);
            swarmCompute.SetVector(ID_BoundsCenter, boundsCenter);
            swarmCompute.SetVector(ID_BoundsExtents, ext);
            swarmCompute.SetVector(ID_InvBoundsExtents, new Vector3(1f / ext.x, 1f / ext.y, 1f / ext.z));
            swarmCompute.SetFloat(ID_SoftShell, softShell);
            swarmCompute.SetFloat(ID_ContainStrength, containStrength);
            swarmCompute.SetFloat(ID_HardLimit, hardLimit);
            swarmCompute.SetFloat(ID_AttractorStrength, attractorStrength);
            swarmCompute.SetVector(ID_LifeRange, new Vector2(Mathf.Max(lifeRange.x, 0.1f), Mathf.Max(lifeRange.y, lifeRange.x, 0.1f)));
            swarmCompute.SetFloat(ID_SpawnRadius, spawnRadius);
            swarmCompute.SetKeyword(turbulenceKeyword, turbulenceOctave);

            propertyBlock.SetFloat(ID_InvMaxSpeed, 1f / maxSpeed);
            swarmMaterial.SetFloat(ID_InvMaxSpeed, 1f / maxSpeed);
            paramsDirty = false;
        }

        static GraphicsBuffer BuildQuadIndexBuffer(int quadCount, out int count)
        {
            count = quadCount * 6;
            var indices = new uint[count];
            for (uint q = 0, i = 0; q < quadCount; q++, i += 6)
            {
                uint v = q << 2;
                indices[i] = v;
                indices[i + 1] = v + 1;
                indices[i + 2] = v + 2;
                indices[i + 3] = v;
                indices[i + 4] = v + 2;
                indices[i + 5] = v + 3;
            }

            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, count, sizeof(uint));
            buffer.SetData(indices);
            return buffer;
        }
    }
}
