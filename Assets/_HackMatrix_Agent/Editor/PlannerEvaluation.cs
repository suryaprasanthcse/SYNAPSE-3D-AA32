using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MAAYAI.HackMatrix;
using UnityEditor;
using UnityEngine;

/// <summary>The fixed brief set. Changing it changes every number the report prints: version it, do not tweak it.</summary>
public static class PlannerEvalPrompts
{
    public const string Version = "v1";

    public static readonly string[] All =
    {
        // Single tier: layout sense.
        "A single plaza with an entrance gate and a monument at its centre.",
        "An entrance gate leading to a plaza flanked by two colonnades.",
        "A monument garden: three monuments standing on a plaza behind a gate.",
        "A processional way: a gate, then two colonnades one after the other, then a plaza.",
        "Two plazas side by side behind a single gate.",
        "A courtyard framed by four colonnades, entered through a gate.",
        "A gate, a plaza, and a colonnade on the left side only.",
        "A simple ruin: one gate and two colonnades.",
        "A plaza with a monument, reached through a gate, with a colonnade behind it.",
        "A small shrine: a gate, a stair, and a raised plaza with a monument on it.",

        // Multiple tiers: reachability.
        "A two-level temple: a lower plaza with colonnades and a stair up to an upper plaza with a monument.",
        "A three-tier stepped sanctuary, each tier a plaza joined to the next by a stair, with a monument at the top.",
        "An acropolis: a gate at the front, a stair up to a raised plaza, and a colonnade on the raised level.",
        "A terrace garden: two raised plazas on tier 1 side by side, each reached by its own stair.",
        "A stepped ascent from tier 0 to tier 3, with a small plaza on every tier.",
        "A lower forum with two colonnades and an upper shrine on tier 2 reached by stairs.",
        "A raised monument platform on tier 1 in the middle, with colonnades on the ground around it.",
        "An entrance gate, a plaza, and two separate stairs leading up to two raised shrines.",
        "A hilltop temple: everything important is on tier 2, with only the gate and stairs at ground level.",
        "A split-level market: a ground plaza on the left and a raised plaza on the right joined by a stair.",

        // Density and structure: constraint pressure.
        "Fit as many colonnades as possible around a central plaza without anything overlapping.",
        "A compact layout that uses every node type exactly once.",
        "A dense ceremonial complex with twelve nodes.",
        "A layout mirrored left to right about the centre line.",
        "A gate, a plaza and a monument all on one straight line from front to back.",
        "A minimal layout with only three nodes.",

        // Adversarial: the brief conflicts with the constraints.
        "A colossal temple thirty metres wide with a hundred columns.",
        "A pyramid with an obelisk and a moat.",
        "A shrine on tier 3 with no stairs at all.",
        "Put the gate at the back of the layout, behind the shrine."
    };
}

/// <summary>One run of the agent on one brief.</summary>
public sealed class EvalRecord
{
    public int PromptIndex, Run;
    public string Prompt;
    public AgentOutcome Outcome;
    public bool FootprintHeld;          // the built layout provably inside the square
}

/// <summary>
/// Runs <see cref="PlannerAgentLoop"/> - the exact code the device runs - over the fixed brief set and reports
/// what a rubric can check: how often the model's first reply is schema-valid, how often its first plan passes
/// verification, how often the repair loop rescues the rest and in how many rounds, how often the system falls
/// back, what the model gets wrong, and how long it all takes.
/// </summary>
public static class PlannerEvaluation
{
    public sealed class Settings
    {
        public LlmConfig Config;
        public float KitScale = 0.03f;
        public float FootprintMetres = 1.5f;
        public int MaxRepairRounds = 3;
        public int RunsPerPrompt = 1;
        public int PromptLimit = int.MaxValue;   // for smoke tests; the report says when it was cut short
        /// <summary>
        /// Minimum wall time per model call, enforced between briefs. Free API tiers cap requests per minute
        /// (Gemini's is about 15), and an unpaced run measures the quota rather than the planner. 0 = unpaced.
        /// </summary>
        public float MinSecondsPerCall = 0f;
        /// <summary>Re-runs of a brief whose first call never reached the model (network outage), not model errors.</summary>
        public int TransportRetries = 0;
        public float TransportRetryWaitSeconds = 20f;
    }

    /// <summary>Retries spent on network outages during the last run.</summary>
    public static int TransportRetriesUsed { get; private set; }

    static bool NeverReachedModel(AgentOutcome o) => o.Rounds.Count == 0 || !o.Rounds[0].TransportOk;

    public static string OutputFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HackMatrixEval"));

    public static async Task<List<EvalRecord>> RunAsync(Settings settings, Action<int, int, EvalRecord> progress,
                                                        CancellationToken cancel)
    {
        var records = new List<EvalRecord>();
        TransportRetriesUsed = 0;
        int prompts = Math.Min(settings.PromptLimit, PlannerEvalPrompts.All.Length);
        int total = prompts * settings.RunsPerPrompt, done = 0;

        for (int p = 0; p < prompts; p++)
            for (int r = 0; r < settings.RunsPerPrompt; r++)
            {
                if (cancel.IsCancellationRequested) return records;

                var outcome = await PlannerAgentLoop.RunAsync(PlannerEvalPrompts.All[p], settings.Config,
                    settings.KitScale, settings.FootprintMetres, settings.MaxRepairRounds, null, cancel);

                // A brief whose first call never reached the model measures the Wi-Fi, not the planner: wait out
                // the outage and run it again. Every retry is counted in the report.
                for (int attempt = 0; attempt < settings.TransportRetries && NeverReachedModel(outcome); attempt++)
                {
                    TransportRetriesUsed++;
                    try { await Task.Delay(TimeSpan.FromSeconds(settings.TransportRetryWaitSeconds), cancel); }
                    catch (TaskCanceledException) { return records; }
                    outcome = await PlannerAgentLoop.RunAsync(PlannerEvalPrompts.All[p], settings.Config,
                        settings.KitScale, settings.FootprintMetres, settings.MaxRepairRounds, null, cancel);
                }

                var record = new EvalRecord
                {
                    PromptIndex = p,
                    Run = r,
                    Prompt = PlannerEvalPrompts.All[p],
                    Outcome = outcome,
                    FootprintHeld = FootprintHeld(outcome.Plan, settings.KitScale, settings.FootprintMetres)
                };
                records.Add(record);
                progress?.Invoke(++done, total, record);

                float wait = settings.MinSecondsPerCall * outcome.Rounds.Count - outcome.TotalSeconds;
                if (wait > 0f && done < total)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(wait), cancel); }
                    catch (TaskCanceledException) { return records; }
                }
            }
        return records;
    }

    /// <summary>
    /// Independently re-derives the built extent of a plan from SpatialRecipes and checks it against the square.
    /// Not a restatement of the clamp: a regression in ClampCentre or in the recipe extents would fail here.
    /// </summary>
    static bool FootprintHeld(SpatialPlan plan, float kitScale, float footprint)
    {
        float limit = footprint * 0.5f + 1e-4f;
        foreach (var n in plan.nodes)
        {
            string type = SpatialNodeTypes.Canonicalise(n.node_type);
            if (type == null) continue;
            int tier = Mathf.Clamp(n.tier, 0, SpatialNodeTypes.MaxTier);
            Vector2 c = SpatialRecipes.ClampCentre(new Vector2(n.coordinates.x, n.coordinates.z), type, tier, kitScale, footprint);
            Vector2 h = SpatialRecipes.FootprintHalfExtentDesign(type, tier) * kitScale;
            // The layout's facing is chosen at placement, so either axis of the recipe can land on either axis.
            float r = Mathf.Max(h.x, h.y);
            if (Mathf.Abs(c.x) + r > limit || Mathf.Abs(c.y) + r > limit) return false;
        }
        return true;
    }

    // =============================================================================================
    // Report
    // =============================================================================================
    public static (string markdownPath, string csvPath) Write(List<EvalRecord> records, Settings settings, bool cancelled)
    {
        Directory.CreateDirectory(OutputFolder);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string md = Path.Combine(OutputFolder, $"planner_eval_{stamp}.md");
        string csv = Path.Combine(OutputFolder, $"planner_eval_{stamp}.csv");
        File.WriteAllText(md, Markdown(records, settings, cancelled), Encoding.UTF8);
        File.WriteAllText(csv, Csv(records), Encoding.UTF8);
        return (md, csv);
    }

    static bool IsMock(LlmConfig c)
    {
        string url = c.endpointUrl ?? "";
        return url.Contains("127.0.0.1") || url.Contains("localhost") || (c.model ?? "").StartsWith("mock", StringComparison.OrdinalIgnoreCase);
    }

    static string Markdown(List<EvalRecord> rec, Settings s, bool cancelled)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        string Pct(int n, int d) => d == 0 ? "n/a" : $"{100.0 * n / d:0.0}% ({n}/{d})";

        int n = rec.Count;
        var firstReplied = rec.Where(r => r.Outcome.Rounds.Count > 0 && r.Outcome.Rounds[0].TransportOk).ToList();
        int d = firstReplied.Count;

        int schemaFirst = firstReplied.Count(r => r.Outcome.FirstReplySchemaValid);
        int verifiedFirst = firstReplied.Count(r => r.Outcome.FirstPlanVerified);
        int verifiedFinal = firstReplied.Count(r => r.Outcome.Source == PlanSource.Model);
        int unverified = firstReplied.Count(r => r.Outcome.Source == PlanSource.ModelUnverified);
        int fallback = rec.Count(r => r.Outcome.Source == PlanSource.OfflineFallback);
        int rescued = firstReplied.Count(r => !r.Outcome.FirstPlanVerified && r.Outcome.Source == PlanSource.Model);
        int neededRepair = firstReplied.Count(r => !r.Outcome.FirstPlanVerified);
        int footprint = rec.Count(r => r.FootprintHeld);

        var repairsWhenVerified = rec.Where(r => r.Outcome.Source == PlanSource.Model).Select(r => r.Outcome.RepairRounds).ToList();
        var endToEnd = rec.Where(r => r.Outcome.Rounds.Any(x => x.TransportOk)).Select(r => (double)r.Outcome.TotalSeconds).ToList();
        var perCall = rec.SelectMany(r => r.Outcome.Rounds).Where(x => x.TransportOk).Select(x => (double)x.LatencySeconds).ToList();

        if (IsMock(s.Config))
        {
            sb.AppendLine("> **MOCK ENDPOINT - THESE ARE NOT MODEL RESULTS.** This run exercised the pipeline against a");
            sb.AppendLine("> scripted local server. Do not present these numbers; re-run against the real endpoint.");
            sb.AppendLine();
        }
        if (cancelled) sb.AppendLine("> **Run cancelled before completion.** Partial results below.\n");
        if (s.PromptLimit < PlannerEvalPrompts.All.Length) sb.AppendLine($"> **Smoke test:** first {s.PromptLimit} prompts only.\n");

        sb.AppendLine("# Planner evaluation: LLM planner with a symbolic verifier");
        sb.AppendLine();
        sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Endpoint: `{HostOf(s.Config.endpointUrl)}`, model `{s.Config.model}`, temperature {s.Config.temperature.ToString("0.00", inv)}, " +
                      $"JSON mode {(s.Config.jsonMode ? "on" : "off")}, per-call timeout {s.Config.timeoutSeconds.ToString("0.#", inv)} s");
        sb.AppendLine($"- Loop: up to {s.MaxRepairRounds} repair rounds; kit scale {s.KitScale.ToString("0.###", inv)}; footprint {s.FootprintMetres.ToString("0.##", inv)} m square");
        if (s.MinSecondsPerCall > 0f)
            sb.AppendLine($"- Paced to at least {s.MinSecondsPerCall.ToString("0.#", inv)} s per model call (API rate limit); latencies exclude the pacing");
        if (s.TransportRetries > 0)
            sb.AppendLine($"- Network outages: up to {s.TransportRetries} re-run(s) per brief whose first call never reached the model; {TransportRetriesUsed} used");
        sb.AppendLine($"- Briefs: {PlannerEvalPrompts.All.Length} fixed prompts ({PlannerEvalPrompts.Version}) x {s.RunsPerPrompt} run(s) = {n} runs");
        sb.AppendLine();

        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| First call answered (transport) | {Pct(d, n)} |");
        sb.AppendLine($"| First reply schema-valid | {Pct(schemaFirst, d)} |");
        sb.AppendLine($"| First plan passed verification | {Pct(verifiedFirst, d)} |");
        sb.AppendLine($"| **Verified after closed-loop repair** | **{Pct(verifiedFinal, d)}** |");
        sb.AppendLine($"| Plans that failed first and were repaired to verified | {Pct(rescued, neededRepair)} |");
        sb.AppendLine($"| Repair budget exhausted (built unverified, flagged) | {Pct(unverified, d)} |");
        sb.AppendLine($"| Offline fallback (all runs) | {Pct(fallback, n)} |");
        sb.AppendLine($"| Mean repair rounds, verified plans | {(repairsWhenVerified.Count == 0 ? "n/a" : repairsWhenVerified.Average().ToString("0.00", inv))} |");
        sb.AppendLine($"| Built layout inside footprint | {Pct(footprint, n)} |");
        sb.AppendLine($"| End-to-end latency p50 / p95 | {Quantile(endToEnd, 0.5)} / {Quantile(endToEnd, 0.95)} |");
        sb.AppendLine($"| Per-call latency p50 / p95 | {Quantile(perCall, 0.5)} / {Quantile(perCall, 0.95)} |");
        sb.AppendLine();
        sb.AppendLine("Percentages marked with a denominator of answered first calls exclude runs whose first call never " +
                      "reached the model; those are counted under offline fallback.");
        sb.AppendLine();

        sb.AppendLine("## Repair rounds used by verified plans");
        sb.AppendLine();
        sb.AppendLine("| Repairs | Runs |");
        sb.AppendLine("|---|---|");
        for (int k = 0; k <= s.MaxRepairRounds; k++) sb.AppendLine($"| {k} | {repairsWhenVerified.Count(x => x == k)} |");
        sb.AppendLine();

        sb.AppendLine("## What the first plans got wrong");
        sb.AppendLine();
        sb.AppendLine("Share of schema-valid first plans with at least one violation of each kind.");
        sb.AppendLine();
        sb.AppendLine("| Constraint | First plans violating |");
        sb.AppendLine("|---|---|");
        var firstPlans = firstReplied.Where(r => r.Outcome.FirstReplySchemaValid).Select(r => r.Outcome.Rounds[0].Report).ToList();
        foreach (ViolationCode code in Enum.GetValues(typeof(ViolationCode)))
            sb.AppendLine($"| {code} | {Pct(firstPlans.Count(p => p.Violations.Any(v => v.Code == code)), firstPlans.Count)} |");
        sb.AppendLine();

        sb.AppendLine("## Per brief");
        sb.AppendLine();
        sb.AppendLine("| # | Brief | Outcome | Calls | Loop trace | Time |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rec)
        {
            string trace = string.Join(" > ", r.Outcome.Rounds.Select(Describe));
            string outcome = r.Outcome.Source == PlanSource.OfflineFallback
                ? $"fallback ({Escape(r.Outcome.FallbackReason)})"
                : r.Outcome.Source == PlanSource.Model ? "verified" : $"unverified ({r.Outcome.FinalReport.Violations.Count} left)";
            sb.AppendLine($"| {r.PromptIndex + 1}{(s.RunsPerPrompt > 1 ? "." + (r.Run + 1) : "")} | {Escape(r.Prompt)} | {outcome} | " +
                          $"{r.Outcome.Rounds.Count} | {trace} | {r.Outcome.TotalSeconds.ToString("0.0", inv)} s |");
        }
        return sb.ToString();
    }

    static string Csv(List<EvalRecord> rec)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("prompt_index,run,source,verified,first_reply_schema_valid,first_plan_verified,calls," +
                                   "repair_rounds,total_seconds,first_plan_violations,final_violations,final_nodes," +
                                   "footprint_held,fallback_reason,prompt\n");
        foreach (var r in rec)
        {
            var o = r.Outcome;
            var first = o.Rounds.Count > 0 && o.Rounds[0].Report != null
                ? string.Join("|", o.Rounds[0].Report.Violations.Select(v => v.Code.ToString()))
                : "";
            sb.Append(r.PromptIndex + 1).Append(',').Append(r.Run + 1).Append(',').Append(o.Source).Append(',')
              .Append(o.Verified).Append(',').Append(o.FirstReplySchemaValid).Append(',').Append(o.FirstPlanVerified).Append(',')
              .Append(o.Rounds.Count).Append(',').Append(o.RepairRounds).Append(',').Append(o.TotalSeconds.ToString("0.000", inv)).Append(',')
              .Append(first).Append(',').Append(o.FinalReport?.Violations.Count ?? 0).Append(',').Append(o.Plan?.nodes?.Length ?? 0).Append(',')
              .Append(r.FootprintHeld).Append(',').Append(CsvCell(o.FallbackReason)).Append(',').Append(CsvCell(r.Prompt)).Append('\n');
        }
        return sb.ToString();
    }

    static string Describe(AgentRound r) =>
        !r.TransportOk ? "net fail" : !r.ParseOk ? "unparseable" : r.Verified ? "PASS" : $"{r.Report.Violations.Count} viol";

    static string Quantile(List<double> values, double q)
    {
        if (values.Count == 0) return "n/a";
        var sorted = values.OrderBy(v => v).ToList();
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        double v = sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        return v.ToString("0.00", CultureInfo.InvariantCulture) + " s";
    }

    static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url ?? ""; }
    }

    static string Escape(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ");
    static string CsvCell(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    // =============================================================================================
    // Self-test
    // =============================================================================================
    [MenuItem("Hack Matrix/Self-Test: Offline Plan Passes Verifier")]
    public static void SelfTestOfflinePlan()
    {
        var plan = AgenticLLM_Bridge.OfflinePlan();
        var report = PlanVerifier.Verify(plan, 0.03f, 1.5f);
        if (report.Passed) Debug.Log($"[PlannerEval] Offline plan '{plan.plan_name}' passes the verifier ({plan.nodes.Length} nodes).");
        else Debug.LogError("[PlannerEval] Offline plan FAILS the verifier:\n- " + string.Join("\n- ", report.Violations.Select(v => v.Message)));
    }
}

/// <summary>Hack Matrix > Planner Evaluation: run the fixed brief set against the configured endpoint.</summary>
public sealed class PlannerEvaluationWindow : EditorWindow
{
    string endpoint = "https://api.deepseek.com/v1/chat/completions";
    string model = "deepseek-chat";
    float temperature = 0.2f;
    float timeoutSeconds = 20f;
    bool jsonMode = true;
    int maxRepairRounds = 3;
    int runsPerPrompt = 1;
    int promptLimit = 30;

    CancellationTokenSource cancel;
    bool running;
    int done, total;
    string status = "";
    string lastReport = "";

    [MenuItem("Hack Matrix/Planner Evaluation...")]
    static void Open() => GetWindow<PlannerEvaluationWindow>("Planner Evaluation");

    void OnEnable()
    {
        // Start from the scene's bridge settings when it is open, so the evaluation measures what the demo runs.
        var bridge = FindAnyObjectByType<AgenticLLM_Bridge>();
        if (bridge == null) return;
        var c = bridge.BuildConfig();
        if (!string.IsNullOrWhiteSpace(c.endpointUrl)) endpoint = c.endpointUrl;
        if (!string.IsNullOrWhiteSpace(c.model)) model = c.model;
        temperature = c.temperature;
        jsonMode = c.jsonMode;
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Endpoint", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(running))
        {
            endpoint = EditorGUILayout.TextField("URL", endpoint);
            model = EditorGUILayout.TextField("Model", model);
            temperature = EditorGUILayout.Slider("Temperature", temperature, 0f, 1f);
            timeoutSeconds = EditorGUILayout.Slider("Per-call timeout (s)", timeoutSeconds, 1f, 60f);
            jsonMode = EditorGUILayout.Toggle("JSON mode", jsonMode);
            maxRepairRounds = EditorGUILayout.IntSlider("Max repair rounds", maxRepairRounds, 0, 5);
            runsPerPrompt = EditorGUILayout.IntSlider("Runs per prompt", runsPerPrompt, 1, 5);
            promptLimit = EditorGUILayout.IntSlider("Prompts", promptLimit, 1, PlannerEvalPrompts.All.Length);
        }

        bool haveKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AgenticLLM_Bridge.ApiKeyEnvironmentVariable));
        EditorGUILayout.HelpBox(haveKey
            ? $"API key: read from {AgenticLLM_Bridge.ApiKeyEnvironmentVariable} (never written to disk)."
            : $"Set the {AgenticLLM_Bridge.ApiKeyEnvironmentVariable} environment variable and restart Unity. The key is never " +
              "stored in the project.", haveKey ? MessageType.Info : MessageType.Warning);

        EditorGUILayout.Space();
        if (!running)
        {
            using (new EditorGUI.DisabledScope(!haveKey))
                if (GUILayout.Button($"Run {promptLimit * runsPerPrompt} evaluation run(s)", GUILayout.Height(30))) Run();
        }
        else
        {
            EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 20f), total == 0 ? 0f : (float)done / total, $"{done} / {total}");
            if (GUILayout.Button("Cancel")) cancel?.Cancel();
        }

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
        if (!string.IsNullOrEmpty(lastReport) && GUILayout.Button("Reveal report")) EditorUtility.RevealInFinder(lastReport);
    }

    async void Run()
    {
        var settings = new PlannerEvaluation.Settings
        {
            Config = new LlmConfig
            {
                endpointUrl = endpoint,
                apiKey = Environment.GetEnvironmentVariable(AgenticLLM_Bridge.ApiKeyEnvironmentVariable),
                model = model,
                temperature = temperature,
                timeoutSeconds = timeoutSeconds,
                jsonMode = jsonMode
            },
            MaxRepairRounds = maxRepairRounds,
            RunsPerPrompt = runsPerPrompt,
            PromptLimit = promptLimit
        };

        running = true;
        done = 0;
        total = promptLimit * runsPerPrompt;
        cancel = new CancellationTokenSource();
        try
        {
            var records = await PlannerEvaluation.RunAsync(settings, (d, t, r) =>
            {
                done = d;
                status = $"#{r.PromptIndex + 1}: {r.Outcome.Source}, {r.Outcome.Rounds.Count} call(s)";
                Repaint();
            }, cancel.Token);

            var (md, _) = PlannerEvaluation.Write(records, settings, cancel.IsCancellationRequested);
            lastReport = md;
            status = $"Report written: {md}";
            Debug.Log("[PlannerEval] " + status);
        }
        catch (Exception e)
        {
            status = "Evaluation failed: " + e.Message;
            Debug.LogException(e);
        }
        finally
        {
            running = false;
            cancel.Dispose();
            cancel = null;
            Repaint();
        }
    }
}
