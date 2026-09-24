using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace MAAYAI.HackMatrix
{
    /// <summary>
    /// The demo surface: a prompt, one button, and a readout that states exactly where the plan came from.
    ///
    /// Brutalist on purpose - black slabs, white hairlines, capitals - and built from code on the built-in font,
    /// the same way the game's UI was, so there is no TextMeshPro import and no scene wiring to break.
    ///
    /// The readout is part of the argument, not decoration. SOURCE says MODEL or OFFLINE FALLBACK with the
    /// reason, so nobody watching can mistake the built-in plan for a live model reply; BUILD shows how many
    /// nodes the compiler had to clamp; FRAME shows GPU time, which is what the A/B toggles actually move (a
    /// frame-locked FPS counter cannot show a GPU saving at all).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Hack Matrix/Spatial Compiler Console")]
    public sealed class SpatialCompilerConsole : MonoBehaviour
    {
        [SerializeField] AgenticLLM_Bridge bridge;
        [SerializeField] SpatialPlannerAgent agent;
        [SerializeField] GenerativeCompiler compiler;
        [TextArea(2, 4)]
        [SerializeField] string defaultPrompt =
            "M6.8 aftershock, Sector 7. Three-storey school collapsed to the north-east, people trapped. Two-storey " +
            "block down to the north-west. Houses cracked and leaning along the west road and east of the square. " +
            "Open field south of the square is clear. Team is at the south edge.";
        [SerializeField] int sortingOrder = 50;

        static readonly Color Ink = Color.white;
        static readonly Color Slab = new(0f, 0f, 0f, 0.86f);
        static readonly Color Dim = new(1f, 1f, 1f, 0.55f);
        static readonly Color Alarm = new(1f, 0.72f, 0.2f, 1f);

        Font font;
        CanvasScaler scaler;
        bool landscape;
        InputField promptField;
        Text statusText;
        Text offlineLabel;
        Button executeButton;
        Text executeLabel;

        readonly FrameTiming[] timings = new FrameTiming[1];
        float smoothedCpuMs, smoothedGpuMs;
        float requestStarted = -1f;
        string accuracyLine;

        void Awake()
        {
            if (bridge == null) bridge = FindAnyObjectByType<AgenticLLM_Bridge>();
            if (agent == null) agent = FindAnyObjectByType<SpatialPlannerAgent>();
            if (compiler == null) compiler = FindAnyObjectByType<GenerativeCompiler>();
            var (correct, total) = SimulatedSensor.RunBenchmark();
            accuracyLine = $"ACCURACY {100f * correct / total:0}% ({correct}/{total})  //  <color=#FFB833>{SimulatedSensor.Disclosure}</color>";
            Debug.Log($"[SimulatedSensor] Benchmark: {correct}/{total} correct. {SimulatedSensor.Disclosure}.", this);
            Build();
        }

        void Update()
        {
            FitOrientation();
            SampleFrameTiming();
            RefreshStatus();
        }

        // ==========================================================================================
        // Actions
        // ==========================================================================================
        void Execute()
        {
            string prompt = string.IsNullOrWhiteSpace(promptField.text) ? defaultPrompt : promptField.text.Trim();
            requestStarted = Time.realtimeSinceStartup;

            // The closed loop when an agent is present; the single-shot bridge otherwise.
            if (agent != null) agent.Run(prompt);
            else if (bridge != null) bridge.RequestSpatialLayout(prompt);
        }

        void ToggleOffline()
        {
            if (bridge == null) return;
            bridge.ForceOffline = !bridge.ForceOffline;
        }

        void ClearLayout()
        {
            if (compiler != null) compiler.Clear();
        }

        // ==========================================================================================
        // Readout
        // ==========================================================================================
        void RefreshStatus()
        {
            if (statusText == null || bridge == null) return;

            bool busy = agent != null ? agent.IsBusy : bridge.IsBusy;
            executeButton.interactable = !busy;
            executeLabel.text = busy ? "MAPPING ZONES..." : "EXECUTE SURVEY";
            offlineLabel.text = bridge.ForceOffline ? "OFFLINE: ON" : "OFFLINE: OFF";

            var lines = new List<string>(8);
            if (agent != null) AgentLines(lines, busy);
            else BridgeLines(lines, busy);

            if (compiler != null && compiler.LayoutRoot != null) SurveyLines(lines);
            lines.Add(accuracyLine);

            string gpu = smoothedGpuMs > 0f ? $"{smoothedGpuMs:0.0} MS GPU" : "GPU N/A (ENABLE FRAME TIMING STATS)";
            lines.Add($"FRAME    {smoothedCpuMs:0.0} MS CPU  //  {gpu}");

            statusText.text = string.Join("\n", lines);
        }

        /// <summary>
        /// The loop, made visible: which round it is on, what the verifier found each time, and whether the plan
        /// that got built was verified. A claim of "closed-loop verification" is only as good as this readout.
        /// </summary>
        void AgentLines(List<string> lines, bool busy)
        {
            var outcome = agent.LastOutcome;
            if (busy)
            {
                var round = agent.LastRound;
                float elapsed = Time.realtimeSinceStartup - requestStarted;
                if (bridge.UseToolCalling && !bridge.ForceOffline)
                {
                    // The ReAct loop, live: the model reasons, calls the C# planner, and reads its verdict.
                    string step = round == null
                        ? "ROUND 1: GEMINI REASONING OVER THE REPORT"
                        : $"R{round.Index + 1} {Describe(round)}  //  ROUND {round.Index + 2}: RE-PLANNING";
                    lines.Add($"AGENT STATE  <color=#00FFFF>TOOL CALLING {PlannerPrompt.ToolName}()</color>  //  {step}  //  {elapsed:0.0} S");
                    return;
                }
                string where = round == null
                    ? "ROUND 1: PLANNING"
                    : $"ROUND {round.Index + 2}: REPAIRING  ({Describe(round)})";
                lines.Add($"AGENT    {where}  //  {elapsed:0.0} S");
                return;
            }
            if (outcome == null)
            {
                lines.Add("AGENT    IDLE  //  AIM AT THE FLOOR, THEN EXECUTE");
                return;
            }

            string source = outcome.Source switch
            {
                PlanSource.Model => "LIVE CLOUD  //  <color=#7CFFB2>ZONE MAP VERIFIED</color>",
                PlanSource.ModelUnverified =>
                    $"LIVE CLOUD  //  <color=#FFB833>UNVERIFIED: {outcome.FinalReport.Violations.Count} VIOLATION(S) LEFT</color>",
                _ => $"<color=#FFB833>UPLINK LOST: ON-BOARD ZONE MAP</color>  ({outcome.FallbackReason})"
            };
            string mode = outcome.Source == PlanSource.OfflineFallback ? ""
                : outcome.ToolMode ? "  //  REACT TOOL LOOP"
                : outcome.ToolModeFallback != null ? "  //  <color=#FFB833>JSON MODE (ENDPOINT REFUSED TOOLS)</color>"
                : "  //  JSON MODE";
            lines.Add($"LINK     {source}{mode}");

            if (outcome.Rounds.Count > 0)
            {
                var trace = new System.Text.StringBuilder("LOOP     ");
                for (int i = 0; i < outcome.Rounds.Count; i++)
                    trace.Append(i == 0 ? "" : "  >  ").Append($"R{i + 1} ").Append(Describe(outcome.Rounds[i]));
                lines.Add(trace.ToString());
            }

            if (outcome.Source == PlanSource.ModelUnverified && outcome.FinalReport.Violations.Count > 0)
                lines.Add($"CHECK    {Truncate(outcome.FinalReport.Violations[0].Message, 110)}");

            lines.Add($"ZONES    {outcome.Plan?.plan_name?.ToUpperInvariant()}  //  {outcome.Plan?.nodes?.Length ?? 0} NODES" +
                      $"  //  {outcome.TotalSeconds:0.0} S");
        }

        /// <summary>
        /// The flight and the latency argument. Both times are MEASURED wall-clock from the EXECUTE press: zone
        /// mapping (the model, or the on-board map) plus the flight so far. Incremental dispatch reports each zone
        /// the moment it is imaged; a batch survey would report nothing until it lands.
        /// </summary>
        void SurveyLines(List<string> lines)
        {
            var path = compiler.Path;
            var site = compiler.Site;
            if (path == null || site == null) return;

            int hazard = 0, shoring = 0;
            foreach (var z in site.Zones)
            {
                if (z.Type == SpatialNodeTypes.HazardZone) hazard++;
                else if (z.Type == SpatialNodeTypes.ShoringSite) shoring++;
            }
            float siteMetres = path.TotalLength / Mathf.Max(compiler.KitScale, 1e-4f);
            lines.Add($"PATH     {hazard} HAZARD  //  {shoring} SHORING  //  {path.Lanes} LANES  //  {path.Waypoints.Count} WAYPOINTS" +
                      $"  //  {siteMetres:0} M AT SITE SCALE (EST. {siteMetres / compiler.RealDroneSpeed:0} S @ {compiler.RealDroneSpeed:0} M/S)");

            float origin = requestStarted >= 0f && requestStarted <= compiler.FlightStartedAt ? requestStarted : compiler.FlightStartedAt;
            string first = compiler.FirstDispatchAt >= 0f ? $"{compiler.FirstDispatchAt - origin:0.0} S" : "...";
            string full = compiler.SurveyCompleteAt >= 0f ? $"{compiler.SurveyCompleteAt - origin:0.0} S"
                                                          : $"IN FLIGHT {Time.realtimeSinceStartup - origin:0.0} S";
            string gain = compiler.FirstDispatchAt >= 0f && compiler.SurveyCompleteAt >= 0f
                ? $"  //  <color=#7CFFB2>FIRST REPORT {(compiler.SurveyCompleteAt - origin) / Mathf.Max(compiler.FirstDispatchAt - origin, 1e-3f):0.0}X SOONER</color>"
                : "";
            lines.Add($"LATENCY  TIME-TO-FIRST-DISPATCH {first}  vs  FULL-SURVEY {full}{gain}  (MEASURED, DEMO FLIGHT SPEED)");

            var d = compiler.Dispatches;
            if (d.Count > 0)
            {
                var last = d[d.Count - 1];
                string colour = last.Class == DamageClass.Critical ? "#FF5A5A" : last.Class == DamageClass.Unstable ? "#FFB833" : "#7CFFB2";
                lines.Add($"DISPATCH {d.Count}/{path.Dispatches.Count}  //  #{last.Order} NODE {last.Node} {last.Type.ToUpperInvariant()} -> " +
                          $"<color={colour}>{last.Class.ToString().ToUpperInvariant()}</color> (SIM SENSOR)  T+{last.FlightSeconds:0.0} S FLIGHT");
            }
            else
            {
                lines.Add($"DISPATCH 0/{path.Dispatches.Count}  //  SWEEPING");
            }

            var relay = compiler.Relay;
            if (relay == null)
            {
                lines.Add("RELAY    NO LAUNCHPAD OR LZ TO LINK");
            }
            else if (relay.Connected)
            {
                lines.Add($"RELAY    <color=#7CFFB2>NETWORK VERIFIED ({relay.MaxHops} HOP{(relay.MaxHops == 1 ? "" : "S")})</color>  //  " +
                          $"{relay.RelayCount} RELAY NODE(S)  //  RANGE {relay.RangeMetres:0.00} M PER HOP");
            }
            else
            {
                var isolated = new List<string>();
                foreach (var kv in relay.LzHops) if (kv.Value < 0) isolated.Add($"NODE {kv.Key}");
                lines.Add($"RELAY    <color=#FF5A5A>ISOLATED: RESCUE LZ {string.Join(", ", isolated)}</color>  //  " +
                          $"NO CHAIN WITHIN {relay.RangeMetres:0.00} M PER HOP");
            }

            lines.Add($"BUILD    {compiler.PiecesSpawned} PIECES  //  {compiler.NodesClamped} CLAMPED  //  " +
                      $"{(compiler.IsAnchored ? "ANCHORED" : "UNANCHORED")}");
        }

        void BridgeLines(List<string> lines, bool busy)
        {
            if (busy)
            {
                lines.Add($"STATE    REQUESTING PLAN  {Time.realtimeSinceStartup - requestStarted:0.0} S");
                return;
            }
            if (bridge.LastSource == PlanSource.None)
            {
                lines.Add("STATE    IDLE  //  AIM AT THE FLOOR, THEN EXECUTE");
                return;
            }
            var plan = bridge.LastPlan;
            lines.Add("SOURCE   " + (bridge.LastSource == PlanSource.Model
                ? "MODEL (SINGLE SHOT, NOT VERIFIED)"
                : $"<color=#FFB833>OFFLINE FALLBACK</color>  ({bridge.LastError})"));
            lines.Add($"PLAN     {plan?.plan_name?.ToUpperInvariant()}  //  {plan?.nodes?.Length ?? 0} NODES  //  " +
                      $"{bridge.LastLatencySeconds * 1000f:0} MS");
        }

        static string Describe(AgentRound round)
        {
            string call = round.ToolCalled ? "TOOL -> " : "";
            return !round.TransportOk ? "NETWORK FAIL"
                : !round.ParseOk ? call + "UNPARSEABLE"
                : round.Verified ? call + (round.ToolCalled ? "VERIFIED" : "PASS")
                : call + (round.ToolCalled ? $"REJECTED ({round.Report.Violations.Count})" : $"{round.Report.Violations.Count} VIOLATION(S)");
        }

        static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 3) + "...";

        void SampleFrameTiming()
        {
            // FrameTimingManager reports real GPU time where the platform supports it (Player Settings > Frame
            // Timing Stats). That is the number the overdraw work changes; the frame rate, locked at 30, does not.
            FrameTimingManager.CaptureFrameTimings();
            float k = 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime);
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
            {
                smoothedCpuMs = Mathf.Lerp(smoothedCpuMs, (float)timings[0].cpuFrameTime, k);
                if (timings[0].gpuFrameTime > 0.0)
                    smoothedGpuMs = Mathf.Lerp(smoothedGpuMs, (float)timings[0].gpuFrameTime, k);
            }
            else
            {
                smoothedCpuMs = Mathf.Lerp(smoothedCpuMs, Time.unscaledDeltaTime * 1000f, k);
            }
        }

        // ==========================================================================================
        // Construction
        // ==========================================================================================
        void Build()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            EnsureEventSystem();

            var canvasGo = new GameObject("SpatialCompiler_Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            landscape = Screen.width <= Screen.height;          // force the first fit
            FitOrientation();
            canvasGo.AddComponent<GraphicRaycaster>();

            // One slab across the top, clear of the diagnostics HUD's button in the top-left corner. The soft
            // keyboard rises from the bottom, so the prompt must never live down there.
            var panel = Box(canvasGo.transform, "Panel", Slab, Ink);
            var panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(0.5f, 1f);
            panelRect.offsetMin = new Vector2(24f, -600f);
            panelRect.offsetMax = new Vector2(-24f, -70f);

            var header = Label(panel.transform, "SYNAPSE-3D // UAV DISASTER AGENT (AA-32)", 30, FontStyle.Bold, Ink, TextAnchor.UpperLeft);
            Stretch(header.rectTransform, 24f, 20f, 24f, 0f, 44f);

            promptField = BuildInputField(panel.transform);
            Stretch((RectTransform)promptField.transform, 24f, 74f, 24f, 0f, 110f);

            // Button row.
            executeButton = BuildButton(panel.transform, "EXECUTE SURVEY", Ink, Color.black, Execute, out executeLabel);
            Place((RectTransform)executeButton.transform, 24f, 200f, 0.58f, 76f);
            var offline = BuildButton(panel.transform, "OFFLINE: OFF", Color.black, Ink, ToggleOffline, out offlineLabel);
            PlaceRight((RectTransform)offline.transform, 0.6f, 0.8f, 200f, 76f, 12f);
            var clear = BuildButton(panel.transform, "CLEAR", Color.black, Ink, ClearLayout, out _);
            PlaceRight((RectTransform)clear.transform, 0.8f, 1f, 200f, 76f, 12f);

            statusText = Label(panel.transform, "", 19, FontStyle.Normal, Ink, TextAnchor.UpperLeft);
            statusText.supportRichText = true;
            Stretch(statusText.rectTransform, 24f, 292f, 24f, 0f, 290f);
        }

        InputField BuildInputField(Transform parent)
        {
            var root = Box(parent, "Prompt", Color.black, Ink);
            var image = root.GetComponent<Image>();
            image.raycastTarget = true;

            var text = Label(root.transform, "", 26, FontStyle.Normal, Ink, TextAnchor.UpperLeft);
            text.supportRichText = false;
            Inset(text.rectTransform, 16f);
            var placeholder = Label(root.transform, "PASTE AN EMERGENCY FIELD REPORT...", 26, FontStyle.Italic, Dim, TextAnchor.UpperLeft);
            Inset(placeholder.rectTransform, 16f);

            var field = root.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.targetGraphic = image;
            field.lineType = InputField.LineType.MultiLineSubmit;
            field.characterLimit = 800;
            field.caretColor = Ink;
            field.customCaretColor = true;
            field.selectionColor = new Color(1f, 1f, 1f, 0.3f);
            field.text = defaultPrompt;
            return field;
        }

        Button BuildButton(Transform parent, string label, Color face, Color ink, UnityEngine.Events.UnityAction onClick, out Text caption)
        {
            var root = Box(parent, label, face, Ink);
            var image = root.GetComponent<Image>();
            image.raycastTarget = true;
            var button = root.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(0.85f, 0.85f, 0.85f);
            colors.pressedColor = Alarm;
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.8f);
            button.colors = colors;
            button.onClick.AddListener(onClick);

            caption = Label(root.transform, label, 26, FontStyle.Bold, ink, TextAnchor.MiddleCenter);
            Inset(caption.rectTransform, 6f);
            return button;
        }

        /// <summary>
        /// A flat slab with a 2-unit hairline drawn as four edge strips. (An Outline effect would redraw the whole
        /// quad in the line colour behind a translucent face and turn black slabs grey.) The face is the root's
        /// own Image, so a Selectable tinting it recolours the face and leaves the line alone.
        /// </summary>
        GameObject Box(Transform parent, string name, Color face, Color line)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = face;
            image.raycastTarget = false;

            const float w = 2f;
            Edge(go.transform, line, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -w), Vector2.zero);   // top
            Edge(go.transform, line, new Vector2(0f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, w));    // bottom
            Edge(go.transform, line, new Vector2(0f, 0f), new Vector2(0f, 1f), Vector2.zero, new Vector2(w, 0f));    // left
            Edge(go.transform, line, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-w, 0f), Vector2.zero);   // right
            return go;
        }

        static void Edge(Transform parent, Color color, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject("Hairline", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        Text Label(Transform parent, string content, int size, FontStyle style, Color color, TextAnchor anchor)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = font;
            text.text = content;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        static void Inset(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        /// <summary>Full width minus side margins, at a fixed distance from the parent's top.</summary>
        static void Stretch(RectTransform rect, float left, float top, float right, float _, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(left, -top - height);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>From the left margin to a fraction of the width.</summary>
        static void Place(RectTransform rect, float left, float top, float rightFraction, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(rightFraction, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(left, -top - height);
            rect.offsetMax = new Vector2(0f, -top);
        }

        /// <summary>Between two fractions of the width, with a gap on the left.</summary>
        static void PlaceRight(RectTransform rect, float fromFraction, float toFraction, float top, float height, float gap)
        {
            rect.anchorMin = new Vector2(fromFraction, 1f);
            rect.anchorMax = new Vector2(toFraction, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(gap, -top - height);
            rect.offsetMax = new Vector2(toFraction >= 1f ? -24f : 0f, -top);
        }

        /// <summary>Short screen edge = 1080 units in either orientation, as in the game's UI.</summary>
        void FitOrientation()
        {
            if (scaler == null) return;
            bool nowLandscape = Screen.width > Screen.height;
            if (nowLandscape == landscape) return;
            landscape = nowLandscape;
            scaler.referenceResolution = landscape ? new Vector2(1920f, 1080f) : new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = landscape ? 1f : 0f;
        }

        static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null) return;
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }
    }
}
