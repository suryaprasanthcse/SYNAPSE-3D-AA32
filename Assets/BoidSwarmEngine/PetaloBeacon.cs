using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// Petalo, the procedural avatar. No model, no rig, no vertex geometry: a bounding cube exists only to start
    /// rays, and the drone itself is solved per-pixel by MAAYAI/PetaloSingularity's signed distance field.
    ///
    /// It is also a real light. Petalo registers as an <see cref="ISwarmBeacon"/>, so its emission is uploaded
    /// into the tail of the swarm's VPL buffer and lights the cave through the same grid the particles feed.
    /// Brightness, pulse and the cyan -> gold alignment state are driven from here, so gameplay can speak through
    /// Petalo without touching the shader.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PetaloBeacon : MonoBehaviour, ISwarmBeacon
    {
        const string ShaderName = "MAAYAI/PetaloSingularity";

        [Header("Avatar")]
        [Tooltip("Bounding volume edge (m). The SDF is authored in the unit cube, so this is Petalo's diameter.")]
        [SerializeField, Min(0.01f)] float size = 0.12f;      // GDD: Petalo is a 12 cm drone
        [SerializeField, Range(8, 48)] int raymarchSteps = 28;
        [Tooltip("Trace steps once Petalo fills the screen. Cost per pixel times pixels is what melts the GPU: " +
                 "when the avatar is a thumbnail the full step count is nearly free, and when it swallows the " +
                 "frame the steps are what have to give.")]
        [SerializeField, Range(6, 48)] int raymarchStepsClose = 12;
        [SerializeField] bool adaptiveSteps = true;
        [SerializeField, Range(1, 6)] int fractalFolds = 3;
        [Tooltip("0 = closed bud, 1 = fully open flower. Breathes slowly on its own.")]
        [SerializeField, Range(0f, 1f)] float bloom = 0.65f;
        [Tooltip("How far the bloom drifts around its set point while idling.")]
        [SerializeField, Range(0f, 0.5f)] float bloomBreath = 0.18f;
        [SerializeField, Range(3, 12)] int petalsPerRing = 6;
        [SerializeField, Range(0f, 4f)] float spinSpeed = 0.6f;

        [Header("Emission")]
        [ColorUsage(false, true)] [SerializeField] Color coreColor = new(0.40f, 2.20f, 3.00f);
        [ColorUsage(false, true)] [SerializeField] Color shellColor = new(0.10f, 0.80f, 1.40f);
        [ColorUsage(false, true)] [SerializeField] Color alignedColor = new(3.00f, 1.90f, 0.50f);

        [Header("Light Cast Into The Cave")]
        [Tooltip("Flux Petalo contributes to the VPL grid, in the same units as the swarm's light.")]
        [SerializeField, Min(0f)] float beaconIntensity = 0.5f;
        [Tooltip("Soft-core radius (m): keeps 1/d^2 finite for surfaces inside the singularity.")]
        [SerializeField, Min(0.001f)] float beaconSoftRadius = 0.06f;

        [Header("Behaviour")]
        [Tooltip("Idle breathing rate (Hz) of the pulse that rides on the emission.")]
        [SerializeField, Min(0f)] float breathRate = 0.35f;
        [SerializeField, Range(0f, 1f)] float alignment;

        [Header("Dual State")]
        [Tooltip("0 = Exploration (the mascot the player drives). 1 = Anomaly (the titanium mandala). " +
                 "The shader morphs the two distance fields, so intermediate values are real geometry, not a fade.")]
        [SerializeField, Range(0f, 1f)] float stateBlend;
        [Tooltip("Seconds for a SetState transition to cross the full 0..1 range.")]
        [SerializeField, Min(0.01f)] float stateTransitionSeconds = 1.2f;

        static readonly int ID_CoreColor = Shader.PropertyToID("_CoreColor");
        static readonly int ID_ShellColor = Shader.PropertyToID("_ShellColor");
        static readonly int ID_AlignedColor = Shader.PropertyToID("_AlignedColor");
        static readonly int ID_Alignment = Shader.PropertyToID("_Alignment");
        static readonly int ID_Pulse = Shader.PropertyToID("_Pulse");
        static readonly int ID_FoldCount = Shader.PropertyToID("_FoldCount");
        static readonly int ID_Bloom = Shader.PropertyToID("_Bloom");
        static readonly int ID_Petals = Shader.PropertyToID("_Petals");
        static readonly int ID_SpinSpeed = Shader.PropertyToID("_SpinSpeed");
        static readonly int ID_Steps = Shader.PropertyToID("_Steps");
        static readonly int ID_StateBlend = Shader.PropertyToID("_StateBlend");
        static readonly int ID_Cull = Shader.PropertyToID("_Cull");
        const string KeywordConservativeDepth = "PETALO_CONSERVATIVE_DEPTH";

        /// <summary>
        /// Profiling A/B: the pre-optimisation avatar - both faces rasterised, plain SV_Depth (no early-Z), and a
        /// fixed step count. Driven by the Matrix HUD so one build can capture both states.
        /// </summary>
        public static bool LegacyProfiling { get; set; }

        static Mesh s_UnitCube;

        Material material;
        MeshRenderer meshRenderer;
        float pulse;
        float stateTarget;

        /// <summary>
        /// Authored edge of the avatar's ray volume, in design metres. Read this rather than the transform's
        /// scale: the scale is pinned here at runtime, so before the first Update - and at edit time - it is
        /// still 1 and anything sizing itself off it lands an avatar-height out.
        /// </summary>
        public float Size => size;

        /// <summary>0 = diagnostic cyan, 1 = ignited gold (GDD's aligned state).</summary>
        public float Alignment
        {
            get => alignment;
            set => alignment = Mathf.Clamp01(value);
        }

        /// <summary>One-shot brightness surge: Petalo's way of speaking without changing shape.</summary>
        public void Ping(float strength = 1f) => pulse = Mathf.Clamp01(Mathf.Max(pulse, strength));

        /// <summary>
        /// Flux Petalo pours into the VPL grid, in the units the swarm's own light uses. Exposed so the on-device
        /// diagnostics can tune it live: at eight minutes per Android build, guessing this from the Editor is not
        /// a workable loop.
        /// </summary>
        public float BeaconIntensity
        {
            get => beaconIntensity;
            set => beaconIntensity = Mathf.Max(0f, value);
        }

        /// <summary>
        /// 0 = the mascot the player drives, 1 = the titanium anomaly. Setting this jumps; use
        /// <see cref="SetState"/> to let the two distance fields morph into each other over time.
        /// </summary>
        public float StateBlend
        {
            get => stateBlend;
            set { stateBlend = Mathf.Clamp01(value); stateTarget = stateBlend; }
        }

        /// <summary>Drive Petalo towards a state. The shader lerps the fields, so this is a real transformation.</summary>
        public void SetState(float target) => stateTarget = Mathf.Clamp01(target);

        /// <summary>Exploration form: what the player steers across the AR planes.</summary>
        public void EnterExploration() => SetState(0f);

        /// <summary>Anomaly form: the cutscene / puzzle-completion body.</summary>
        public void EnterAnomaly() => SetState(1f);

        // --- ISwarmBeacon: Petalo lights the cave through the swarm's own grid ---
        public bool BeaconActive => isActiveAndEnabled && beaconIntensity > 0f;
        public Vector3 BeaconPosition => transform.position;
        public Color BeaconFlux
        {
            get
            {
                Color emission = Color.Lerp(coreColor, alignedColor, alignment);

                // Brightness is an absolute, art-directed value - it is deliberately NOT derived from Petalo's
                // current size.
                //
                // Two earlier attempts both failed on device. Squaring the serialized `size` over-drove the grid
                // by the square of the table fit (~600x, every surface white). Squaring the live lossyScale
                // collapsed it the other way: the table fit makes Petalo about 2 cm, so the avatar's own area
                // became a ~600x attenuator and the map went black. Both bugs existed only because the avatar's
                // scale was allowed to drive the light at all.
                //
                // The reference area below is the original 12 cm drone the intensity was calibrated against, so
                // beaconIntensity keeps its old meaning and the light no longer moves when the world is refitted
                // to a different table.
                const float ReferenceSizeMetres = 0.12f;
                const float ReferenceArea = ReferenceSizeMetres * ReferenceSizeMetres;
                float gain = beaconIntensity * (1f + 0.8f * pulse) * ReferenceArea;
                return emission * gain;
            }
        }
        public float BeaconSoftRadius => beaconSoftRadius;

        void Awake()
        {
            stateTarget = stateBlend;
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError($"[PetaloBeacon] '{ShaderName}' not found (add it to Always Included Shaders).", this);
                enabled = false;
                return;
            }

            var filter = GetComponent<MeshFilter>();
            if (filter == null) filter = gameObject.AddComponent<MeshFilter>();
            filter.sharedMesh = GetUnitCube();

            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            material = new Material(shader) { name = "PetaloSingularity (Runtime)" };
            meshRenderer.sharedMaterial = material;

            transform.localScale = Vector3.one * size;
        }

        void OnEnable() => SwarmLightingGlobals.RegisterBeacon(this);

        void OnDisable() => SwarmLightingGlobals.UnregisterBeacon(this);

        void OnDestroy()
        {
            SwarmLightingGlobals.UnregisterBeacon(this);
            if (material != null) Destroy(material);
        }

        void Update()
        {
            if (material == null) return;

            // Idle breath plus decay of any Ping.
            float breath = 0.5f + 0.5f * Mathf.Sin(Time.time * breathRate * Mathf.PI * 2f);
            pulse = Mathf.Max(pulse * Mathf.Exp(-3f * Time.deltaTime), breath * 0.25f);

            // Constant-rate morph, so a transition always takes the same wall time regardless of where it starts.
            stateBlend = Mathf.MoveTowards(stateBlend, stateTarget, Time.deltaTime / stateTransitionSeconds);
            material.SetFloat(ID_StateBlend, stateBlend);

            material.SetColor(ID_CoreColor, coreColor);
            material.SetColor(ID_ShellColor, shellColor);
            material.SetColor(ID_AlignedColor, alignedColor);
            material.SetFloat(ID_Alignment, alignment);
            material.SetFloat(ID_Pulse, pulse);
            material.SetFloat(ID_FoldCount, fractalFolds);
            // The flower opens and closes as it breathes; alignment pushes it fully open.
            float openness = Mathf.Clamp01(bloom + bloomBreath * Mathf.Sin(Time.time * breathRate * Mathf.PI * 2f)
                                           + 0.3f * alignment);
            material.SetFloat(ID_Bloom, openness);
            material.SetFloat(ID_Petals, petalsPerRing);
            material.SetFloat(ID_SpinSpeed, spinSpeed);
            ApplyPixelBudget();

            if (!Mathf.Approximately(transform.localScale.x, size)) transform.localScale = Vector3.one * size;
        }

        /// <summary>
        /// Petalo is the most expensive surface in the frame: every one of its pixels sphere-traces a distance
        /// field. Three things are decided here, once per frame on the CPU, so the shader pays nothing for them.
        ///
        ///   Culling      Back faces only, so each pixel is traced ONCE. With Cull Off the front and back of the
        ///                ray volume both rasterise and both trace, and because the shader writes SV_Depth there
        ///                is no early-Z to reject the second layer.
        ///   Depth        Culling back faces means the rasterised depth is always in front of the traced hit, so
        ///                the fragment can promise "my depth is greater or equal" and keep hardware early-Z.
        ///   Steps       Scaled by how much of the screen Petalo covers. Far away, the full count is a rounding
        ///                error; pressed against the lens, it is the whole frame budget.
        ///
        /// Inside the ray volume the front faces are behind the near plane, so both go back to the safe path.
        /// </summary>
        void ApplyPixelBudget()
        {
            if (material == null) return;

            Camera cam = Camera.main;
            float worldRadius = transform.lossyScale.x * 0.866f;      // half-diagonal of the unit ray volume
            bool inside = true;
            float coverage = 1f;

            if (cam != null)
            {
                float distance = Vector3.Distance(cam.transform.position, transform.position);
                // Front faces start clipping once the eye is within (radius + near plane) of the centre; the 1.25
                // is margin for the frame the camera moves in, not a guess.
                inside = distance < worldRadius + cam.nearClipPlane * 1.25f;

                // Projected radius as a fraction of half the screen height: 1 means Petalo spans the viewport.
                float tanHalfFov = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                coverage = worldRadius / Mathf.Max(distance * tanHalfFov, 1e-4f);
            }

            bool legacy = LegacyProfiling;
            bool cullBack = !legacy && !inside;

            material.SetFloat(ID_Cull, (float)(cullBack ? UnityEngine.Rendering.CullMode.Back
                                                        : UnityEngine.Rendering.CullMode.Off));
            if (cullBack) material.EnableKeyword(KeywordConservativeDepth);
            else material.DisableKeyword(KeywordConservativeDepth);

            int steps = raymarchSteps;
            if (adaptiveSteps && !legacy)
            {
                float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.85f, coverage));
                steps = Mathf.RoundToInt(Mathf.Lerp(raymarchSteps, Mathf.Min(raymarchStepsClose, raymarchSteps), t));
            }
            material.SetFloat(ID_Steps, steps);
        }

        static Mesh GetUnitCube()
        {
            if (s_UnitCube != null) return s_UnitCube;

            // A plain unit cube: rasterised only to generate rays. Nothing of Petalo's shape lives in it.
            var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            s_UnitCube = Instantiate(temp.GetComponent<MeshFilter>().sharedMesh);
            s_UnitCube.name = "PetaloRayVolume";
            DestroyImmediate(temp);
            return s_UnitCube;
        }
    }
}
