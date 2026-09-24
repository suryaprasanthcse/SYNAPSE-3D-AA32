using System;
using System.Collections.Generic;
using UnityEngine;

namespace MAAYAI.HackMatrix
{
    // ==============================================================================================
    // Schema. Field names are snake_case on purpose: JsonUtility maps JSON keys to field names verbatim, and
    // these are the keys the model is told to emit.
    // ==============================================================================================

    /// <summary>One element of a generated layout.</summary>
    [Serializable]
    public sealed class SpatialNode
    {
        /// <summary>One of <see cref="SpatialNodeTypes.All"/> (matched case-insensitively, then canonicalised).</summary>
        public string node_type;

        /// <summary>Offset from the layout centre, in real-world metres. Y is ignored: height is <see cref="tier"/>.</summary>
        public Vector3 coordinates;

        /// <summary>Height level, 0 = floor. Clamped to 0..<see cref="SpatialNodeTypes.MaxTier"/>.</summary>
        public int tier;
    }

    /// <summary>The whole plan the agent returns.</summary>
    [Serializable]
    public sealed class SpatialPlan
    {
        public string plan_name;
        public SpatialNode[] nodes;
    }

    /// <summary>The vocabulary shared by the prompt, the validator, the verifier and the compiler.</summary>
    public static class SpatialNodeTypes
    {
        public const string LaunchPad = "LaunchPad";       // where the drone takes off
        public const string HazardZone = "HazardZone";     // critical collapse: fly over, never land
        public const string ShoringSite = "ShoringSite";   // standing but unstable: needs shoring crews
        public const string RescueLZ = "RescueLZ";         // clear ground a rescue helicopter can land on
        public const string RelayNode = "RelayNode";       // radio relay mast

        public static readonly string[] All = { LaunchPad, HazardZone, ShoringSite, RescueLZ, RelayNode };
        /// <summary>For HazardZone and ShoringSite, tier is the structure's storeys above one (0 = single storey).</summary>
        public const int MaxTier = 3;
        public const int MaxNodes = 24;

        /// <summary>Zones the drone must survey: the coverage planner sweeps these.</summary>
        public static bool IsSurveyZone(string type) => type == HazardZone || type == ShoringSite;

        /// <summary>Canonical spelling for a model-emitted type, or null if it is not in the vocabulary.</summary>
        public static string Canonicalise(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string trimmed = raw.Trim();
            foreach (var type in All)
                if (string.Equals(type, trimmed, StringComparison.OrdinalIgnoreCase)) return type;
            return null;
        }
    }

    public enum PlanSource
    {
        None,
        Model,              // a model plan that passed PlanVerifier
        ModelUnverified,    // the repair budget ran out: the model plan with the fewest violations, flagged as such
        OfflineFallback     // the built-in plan: no key, forced offline, network failure, timeout or no usable reply
    }

    /// <summary>
    /// The model's endpoint and the single-shot path to it: one brief in, one validated <see cref="SpatialPlan"/>
    /// out. The closed-loop agent (<see cref="SpatialPlannerAgent"/>) uses this component's configuration and
    /// offline switch, and the same transport.
    ///
    /// It is built to never fail in front of an evaluator. Every failure mode - no key, forced offline, DNS,
    /// TLS, an http URL Player Settings refuses, HTTP errors, a reply slower than the deadline, prose instead of
    /// JSON, JSON that does not match the schema, types outside the vocabulary - ends in the same place: the
    /// deterministic offline plan, emitted through the same event, with <see cref="LastSource"/> and
    /// <see cref="LastError"/> saying what happened. Callers never see an exception or a null plan.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Hack Matrix/Agentic LLM Bridge")]
    public sealed class AgenticLLM_Bridge : MonoBehaviour
    {
        public const string ApiKeyEnvironmentVariable = "HACKMATRIX_LLM_API_KEY";

        [Header("Endpoint (OpenAI-compatible)")]
        [Tooltip("Full URL of the chat completions route, e.g. https://api.deepseek.com/v1/chat/completions, or a " +
                 "local server such as http://192.168.1.20:1234/v1/chat/completions.")]
        [SerializeField] string endpointUrl = "https://api.deepseek.com/v1/chat/completions";

        [Tooltip("WARNING: a value here is saved into the scene, committed to git and shipped inside the APK. " +
                 "Leave it empty in the Editor and set the " + ApiKeyEnvironmentVariable + " environment variable " +
                 "instead; for a device build, route through your own proxy rather than embedding a key.")]
        [SerializeField] string apiKey = "";

        [SerializeField] string model = "deepseek-chat";
        [SerializeField, Range(0f, 1f)] float temperature = 0.2f;
        [Tooltip("Ask for response_format: json_object. Turn off for servers that reject the field.")]
        [SerializeField] bool requestJsonMode = true;
        [Tooltip("Run the agent as a tool-calling ReAct loop: the model calls calculate_flight_path and reads back " +
                 "the verifier's verdict. If the endpoint refuses tool requests, the agent reruns in plain JSON mode.")]
        [SerializeField] bool useToolCalling = true;

        [Header("Resilience")]
        [Tooltip("Hard deadline (seconds) for EACH model call, in JSON and tool-calling mode alike. Past it the call " +
                 "is aborted. A tool loop makes up to (repair rounds + 1) calls, so its worst case is that many times this.")]
        [SerializeField, Min(1f)] float timeoutSeconds = 60f;
        [Tooltip("Skip the network entirely. Use for demos on venue Wi-Fi.")]
        [SerializeField] bool forceOffline;

        [Header("Geometry the prompt describes (normally the compiler's)")]
        [SerializeField] float kitScale = 0.03f;
        [SerializeField] float footprintMetres = 1.5f;

        /// <summary>Raised exactly once per single-shot request, always with a valid plan.</summary>
        public event Action<SpatialPlan, PlanSource> PlanReady;

        public bool IsBusy { get; private set; }
        public PlanSource LastSource { get; private set; } = PlanSource.None;
        /// <summary>Why the offline plan was used, or null when the model's plan was accepted.</summary>
        public string LastError { get; private set; }
        public float LastLatencySeconds { get; private set; }
        public SpatialPlan LastPlan { get; private set; }

        public bool ForceOffline
        {
            get => forceOffline;
            set => forceOffline = value;
        }

        public float TimeoutSeconds => timeoutSeconds;
        public bool UseToolCalling => useToolCalling;

        /// <summary>The endpoint exactly as serialized, before normalisation. For build-time checks.</summary>
        public string RawEndpointUrl => endpointUrl;
        public string Model => model;
        /// <summary>True when a key is serialized into the scene, and therefore into every build of it.</summary>
        public bool HasEmbeddedApiKey => !string.IsNullOrWhiteSpace(apiKey);

        int requestGeneration;
        float watchdogDeadline;
        const float WatchdogGraceSeconds = 1f;

        /// <summary>
        /// The endpoint as the transport sees it. The key comes from the field, or else from the
        /// <see cref="ApiKeyEnvironmentVariable"/> environment variable (Editor and desktop; not Android).
        /// </summary>
        public LlmConfig BuildConfig() => new()
        {
            endpointUrl = LlmChatClient.TryNormalizeEndpoint(endpointUrl, out string url, out _) ? url : endpointUrl,
            apiKey = ResolveApiKey(),
            model = model?.Trim(),
            temperature = temperature,
            timeoutSeconds = timeoutSeconds,
            jsonMode = requestJsonMode,
            useTools = useToolCalling
        };

        string ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(apiKey)) return apiKey.Trim();
            return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable)?.Trim();
        }

        /// <summary>
        /// Single-shot: ask for a layout once, validate it, emit it (or the offline plan). A request already in
        /// flight is abandoned - the newest brief wins. For the closed loop, use SpatialPlannerAgent.
        /// </summary>
        public void RequestSpatialLayout(string prompt) => RunSingleShot(prompt ?? string.Empty, ++requestGeneration);

        async void RunSingleShot(string prompt, int generation)
        {
            IsBusy = true;
            float started = Time.realtimeSinceStartup;
            watchdogDeadline = started + timeoutSeconds + WatchdogGraceSeconds;

            try
            {
                if (forceOffline)
                {
                    Emit(Fallback("forced offline"), PlanSource.OfflineFallback, started, generation);
                    return;
                }

                var messages = new[]
                {
                    new ChatTurn("system", PlannerPrompt.System(kitScale, footprintMetres)),
                    new ChatTurn("user", prompt)
                };
                ChatResult result = await LlmChatClient.CompleteAsync(BuildConfig(), messages);
                if (this == null || generation != requestGeneration) return;     // superseded or destroyed

                if (!result.Ok)
                    Emit(Fallback(result.Error), PlanSource.OfflineFallback, started, generation);
                else if (!TryParsePlanJson(result.Content, out SpatialPlan plan, out string parseError))
                    Emit(Fallback("reply rejected: " + parseError), PlanSource.OfflineFallback, started, generation);
                else
                    Emit(plan, PlanSource.Model, started, generation);
            }
            catch (Exception e)
            {
                // The contract is "a plan always arrives", so even an unforeseen exception ends in one.
                if (this != null && generation == requestGeneration)
                    Emit(Fallback("unexpected: " + e.Message), PlanSource.OfflineFallback, started, generation);
            }
        }

        // Belt and braces: if the request outlives its deadline by any margin, deliver the offline plan anyway.
        void Update()
        {
            if (!IsBusy || Time.realtimeSinceStartup < watchdogDeadline) return;
            requestGeneration++;                    // the late reply, if it ever comes, is now stale
            Emit(Fallback("watchdog: request did not complete"), PlanSource.OfflineFallback,
                 watchdogDeadline - timeoutSeconds - WatchdogGraceSeconds, requestGeneration);
        }

        void OnDisable()
        {
            requestGeneration++;
            IsBusy = false;
        }

        void Emit(SpatialPlan plan, PlanSource source, float started, int generation)
        {
            if (generation != requestGeneration) return;
            LastSource = source;
            LastPlan = plan;
            LastLatencySeconds = Time.realtimeSinceStartup - started;
            if (source == PlanSource.Model) LastError = null;
            IsBusy = false;

            Debug.Log($"[AgenticLLM] Plan '{plan.plan_name}' from {source}, {plan.nodes.Length} nodes, " +
                      $"{LastLatencySeconds * 1000f:0} ms" + (LastError != null ? $" ({LastError})" : "") + ".", this);
            PlanReady?.Invoke(plan, source);
        }

        SpatialPlan Fallback(string reason)
        {
            LastError = reason;
            return OfflinePlan();
        }

        /// <summary>The built-in plan, parsed through the same validator as any model reply. Never null.</summary>
        public static SpatialPlan OfflinePlan()
        {
            if (TryParsePlanJson(OfflinePlanJson, out SpatialPlan plan, out string error)) return plan;

            // Unreachable unless the constant is edited into something invalid - and even then a caller gets a
            // plan, never null.
            Debug.LogError("[AgenticLLM] Offline plan failed validation: " + error);
            return new SpatialPlan
            {
                plan_name = "Emergency Survey",
                nodes = new[]
                {
                    new SpatialNode { node_type = SpatialNodeTypes.LaunchPad, coordinates = new Vector3(0f, 0f, -0.6f) },
                    new SpatialNode { node_type = SpatialNodeTypes.HazardZone, coordinates = Vector3.zero },
                    new SpatialNode { node_type = SpatialNodeTypes.RescueLZ, coordinates = new Vector3(-0.5f, 0f, -0.5f) }
                }
            };
        }

        // ==========================================================================================
        // Parsing
        // ==========================================================================================
        /// <summary>Unwrap an OpenAI-style completion and validate the plan inside it.</summary>
        public static bool TryParseChatCompletion(string responseJson, out SpatialPlan plan, out string error)
        {
            plan = null;
            if (!LlmChatClient.TryExtractContent(responseJson, out string content, out error)) return false;
            return TryParsePlanJson(content, out plan, out error);
        }

        /// <summary>
        /// Parse and validate a plan. Tolerates markdown fences and prose around the object; rejects anything
        /// the compiler could not build faithfully.
        /// </summary>
        public static bool TryParsePlanJson(string json, out SpatialPlan plan, out string error)
        {
            plan = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "empty plan"; return false; }

            // Models wrap JSON in ```json fences or a sentence of preamble often enough to plan for it.
            int open = json.IndexOf('{');
            int close = json.LastIndexOf('}');
            if (open < 0 || close <= open) { error = "no JSON object found"; return false; }
            string body = json.Substring(open, close - open + 1);

            SpatialPlan parsed;
            try { parsed = JsonUtility.FromJson<SpatialPlan>(body); }
            catch (Exception e) { error = "plan is not JSON: " + e.Message; return false; }

            if (parsed?.nodes == null || parsed.nodes.Length == 0) { error = "plan has no nodes"; return false; }

            var kept = new List<SpatialNode>(Mathf.Min(parsed.nodes.Length, SpatialNodeTypes.MaxNodes));
            int dropped = 0;
            foreach (var node in parsed.nodes)
            {
                if (kept.Count >= SpatialNodeTypes.MaxNodes) { dropped++; continue; }

                string type = SpatialNodeTypes.Canonicalise(node?.node_type);
                if (type == null || !IsFinite(node.coordinates)) { dropped++; continue; }

                kept.Add(new SpatialNode
                {
                    node_type = type,
                    coordinates = node.coordinates,
                    tier = Mathf.Clamp(node.tier, 0, SpatialNodeTypes.MaxTier)
                });
            }
            if (kept.Count == 0) { error = $"no usable nodes ({dropped} outside the vocabulary or non-finite)"; return false; }

            // JsonUtility cannot read "coordinates": [x, y, z] - it silently leaves the Vector3 at zero. Several
            // nodes all sitting on exactly the same point is that failure, not a design, so it is refused here
            // instead of compiling into one pile of columns.
            if (kept.Count > 1 && AllSamePoint(kept)) { error = "all nodes share one point (coordinates not in {x,y,z} form?)"; return false; }

            plan = new SpatialPlan
            {
                plan_name = string.IsNullOrWhiteSpace(parsed.plan_name) ? "Untitled Plan" : parsed.plan_name.Trim(),
                nodes = kept.ToArray()
            };
            error = dropped > 0 ? $"{dropped} node(s) dropped" : null;
            return true;
        }

        static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
              float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        static bool AllSamePoint(List<SpatialNode> nodes)
        {
            Vector3 first = nodes[0].coordinates;
            for (int i = 1; i < nodes.Count; i++)
                if ((nodes[i].coordinates - first).sqrMagnitude > 1e-8f) return false;
            return true;
        }

        // ==========================================================================================
        // The offline zone map: what the on-board planner flies when the uplink is lost. Deterministic, and it
        // passes the same schema validation AND the same PlanVerifier as a model reply: nothing overlaps, the LZ
        // keeps its buffer from every collapse, and the swept path clears every structure.
        // Field report it encodes: a collapsed three-storey school to the north-east, a collapsed two-storey
        // block to the north-west, unstable houses along the west road and east of the square, the open field
        // south of the square as the landing zone, and a relay mast in the south-west corner.
        // ==========================================================================================
        public const string OfflinePlanJson = @"{
  ""plan_name"": ""Offline Zone Map: Sector 7"",
  ""nodes"": [
    { ""node_type"": ""LaunchPad"",   ""coordinates"": { ""x"":  0.00, ""y"": 0, ""z"": -0.60 }, ""tier"": 0 },
    { ""node_type"": ""HazardZone"",  ""coordinates"": { ""x"":  0.35, ""y"": 0, ""z"":  0.35 }, ""tier"": 2 },
    { ""node_type"": ""HazardZone"",  ""coordinates"": { ""x"": -0.30, ""y"": 0, ""z"":  0.40 }, ""tier"": 1 },
    { ""node_type"": ""ShoringSite"", ""coordinates"": { ""x"": -0.40, ""y"": 0, ""z"": -0.05 }, ""tier"": 1 },
    { ""node_type"": ""ShoringSite"", ""coordinates"": { ""x"":  0.30, ""y"": 0, ""z"": -0.10 }, ""tier"": 0 },
    { ""node_type"": ""RescueLZ"",    ""coordinates"": { ""x"":  0.00, ""y"": 0, ""z"": -0.30 }, ""tier"": 0 },
    { ""node_type"": ""RelayNode"",   ""coordinates"": { ""x"": -0.60, ""y"": 0, ""z"": -0.60 }, ""tier"": 0 }
  ]
}";
    }
}
