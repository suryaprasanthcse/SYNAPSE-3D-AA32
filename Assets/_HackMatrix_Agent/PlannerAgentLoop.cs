using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MAAYAI.HackMatrix
{
    /// <summary>One model call and what the symbolic side made of its reply.</summary>
    public sealed class AgentRound
    {
        public int Index;                   // 0 = the first plan, 1..N = repairs
        public bool TransportOk;
        public string TransportError;
        public bool ParseOk;                // reply was a schema-valid SpatialPlan
        public string ParseError;
        public SpatialPlan Plan;
        public VerificationReport Report;   // null unless ParseOk
        public float LatencySeconds;
        /// <summary>Tool mode: the model acted by calling the planner tool (false: it replied with text).</summary>
        public bool ToolCalled;

        public bool Verified => ParseOk && Report != null && Report.Passed;
    }

    public sealed class AgentOutcome
    {
        public SpatialPlan Plan;            // never null
        public PlanSource Source;
        public bool Verified;
        public string FallbackReason;       // set when Source is OfflineFallback
        public readonly List<AgentRound> Rounds = new();
        public VerificationReport FinalReport;
        public float TotalSeconds;
        /// <summary>The run used the tool-calling loop (false: plain JSON replies).</summary>
        public bool ToolMode;
        /// <summary>Why a requested tool-calling run fell back to plain JSON, or null.</summary>
        public string ToolModeFallback;

        /// <summary>Repair rounds actually spent: 0 when the first plan verified.</summary>
        public int RepairRounds => Mathf.Max(0, Rounds.Count - 1);
        public bool FirstReplySchemaValid => Rounds.Count > 0 && Rounds[0].ParseOk;
        public bool FirstPlanVerified => Rounds.Count > 0 && Rounds[0].Verified;
    }

    /// <summary>
    /// The agent: an LLM planner in a closed loop with a symbolic verifier.
    ///
    ///   plan     the model proposes a SpatialPlan from the brief
    ///   verify   PlanVerifier checks it against constraints the schema cannot express, using the compiler's
    ///            own geometry
    ///   repair   every violation goes back to the model in plain language, with the conversation so far, and
    ///            it revises the whole plan
    ///   stop     on the first verified plan, or after the repair budget
    ///
    /// A reply that cannot be parsed costs a round too, and is repaired the same way. Transport failures are not
    /// retried: the budget is time as well as rounds, and a dead network does not get better by asking again.
    ///
    /// Outcomes, always with a buildable plan:
    ///   Model            verified
    ///   ModelUnverified  the budget ran out; the plan with the fewest violations is returned, and says so
    ///   OfflineFallback  no usable reply at all; the built-in plan
    ///
    /// A plain class rather than a MonoBehaviour so the Editor evaluation runs exactly this code.
    /// </summary>
    public static class PlannerAgentLoop
    {
        public static async Task<AgentOutcome> RunAsync(string brief, LlmConfig config, float kitScale, float footprintMetres,
                                                        int maxRepairRounds, Action<AgentRound> onRound = null,
                                                        CancellationToken cancel = default)
        {
            if (!config.useTools) return await RunJsonAsync(brief, config, kitScale, footprintMetres, maxRepairRounds, onRound, cancel);

            var tooled = await RunToolsAsync(brief, config, kitScale, footprintMetres, maxRepairRounds, onRound, cancel);

            // An endpoint that refuses the tool request itself (a 4xx on the very first call) is refusing the
            // feature, not failing: rerun the proven plain-JSON loop rather than dropping to the offline map.
            var first = tooled.Rounds.Count > 0 ? tooled.Rounds[0] : null;
            if (tooled.Rounds.Count == 1 && first != null && !first.TransportOk &&
                first.TransportError != null && first.TransportError.Contains("(HTTP 4"))
            {
                var plain = await RunJsonAsync(brief, config, kitScale, footprintMetres, maxRepairRounds, onRound, cancel);
                plain.ToolModeFallback = first.TransportError;
                return plain;
            }
            return tooled;
        }

        /// <summary>
        /// ReAct with a real tool. The model REASONS over the report and ACTS by calling calculate_flight_path with
        /// a zone map; the tool runs the verifier and the coverage planner - deterministic C#, on the device - and
        /// its result is the OBSERVATION: VERIFIED with the route, or REJECTED with every violation and its fix.
        /// The loop stops on the first verified call, so a good first map costs exactly one model call.
        /// </summary>
        static async Task<AgentOutcome> RunToolsAsync(string brief, LlmConfig config, float kitScale, float footprintMetres,
                                                      int maxRepairRounds, Action<AgentRound> onRound, CancellationToken cancel)
        {
            var outcome = new AgentOutcome { ToolMode = true };
            float started = Time.realtimeSinceStartup;
            string tools = PlannerPrompt.ToolsJson();

            var conversation = new List<AgentMessage>
            {
                new("system", PlannerPrompt.System(kitScale, footprintMetres, toolMode: true)),
                new("user", string.IsNullOrWhiteSpace(brief) ? "Map the damage in the field report." : brief.Trim())
            };

            AgentRound best = null;
            for (int r = 0; r <= maxRepairRounds; r++)
            {
                var result = await LlmChatClient.CompleteWithToolsAsync(config, conversation, tools, cancel);
                var round = new AgentRound
                {
                    Index = r,
                    TransportOk = result.Ok,
                    TransportError = result.Error,
                    LatencySeconds = result.LatencySeconds
                };
                outcome.Rounds.Add(round);

                if (!result.Ok)
                {
                    onRound?.Invoke(round);
                    break;                                  // no retries on transport failure
                }

                // The model's own turn goes back into the history verbatim, tool calls included.
                conversation.Add(new AgentMessage("assistant", result.Content) { RawToolCalls = result.RawToolCalls });

                ToolCall call = null;
                foreach (var c in result.ToolCalls)
                    if (c.Name == PlannerPrompt.ToolName) { call = c; break; }

                string observation;
                if (call != null)
                {
                    round.ToolCalled = true;
                    round.ParseOk = AgenticLLM_Bridge.TryParsePlanJson(call.Arguments, out round.Plan, out round.ParseError);
                    if (round.ParseOk)
                    {
                        round.Report = PlanVerifier.Verify(round.Plan, kitScale, footprintMetres);
                        observation = PlannerPrompt.ToolResult(round.Report);
                    }
                    else
                    {
                        observation = PlannerPrompt.ToolArgumentsRepair(round.ParseError);
                    }
                }
                else
                {
                    // A text reply instead of a call: still accept a zone map written as JSON.
                    round.ParseOk = AgenticLLM_Bridge.TryParsePlanJson(result.Content, out round.Plan, out round.ParseError);
                    if (round.ParseOk) round.Report = PlanVerifier.Verify(round.Plan, kitScale, footprintMetres);
                    observation = null;
                }

                if (round.ParseOk && (best == null || round.Report.Violations.Count < best.Report.Violations.Count)) best = round;
                onRound?.Invoke(round);

                if (round.Verified)
                {
                    Finish(outcome, round.Plan, PlanSource.Model, round.Report, started);
                    return outcome;
                }
                if (r == maxRepairRounds) break;

                // Every tool call must be answered, even ones for tools we do not have, or the next request is invalid.
                foreach (var c in result.ToolCalls)
                    conversation.Add(new AgentMessage("tool", c == call ? observation : $"Unknown tool '{c.Name}'.")
                    {
                        ToolCallId = c.Id
                    });
                if (call == null)
                    conversation.Add(new AgentMessage("user", round.ParseOk
                        ? PlannerPrompt.VerificationRepair(round.Report.Violations) + PlannerPrompt.CallTheTool
                        : PlannerPrompt.CallTheTool));
            }

            if (best != null)
            {
                Finish(outcome, best.Plan, PlanSource.ModelUnverified, best.Report, started);
                return outcome;
            }
            FinishOffline(outcome, kitScale, footprintMetres, started);
            return outcome;
        }

        static void FinishOffline(AgentOutcome outcome, float kitScale, float footprintMetres, float started)
        {
            var last = outcome.Rounds.Count > 0 ? outcome.Rounds[outcome.Rounds.Count - 1] : null;
            outcome.FallbackReason = last == null ? "no attempt made"
                : !last.TransportOk ? last.TransportError
                : $"no usable plan after {outcome.Rounds.Count} replies ({last.ParseError})";
            AgenticLLM_Bridge.TryParsePlanJson(AgenticLLM_Bridge.OfflinePlanJson, out var offline, out _);
            Finish(outcome, offline, PlanSource.OfflineFallback, PlanVerifier.Verify(offline, kitScale, footprintMetres), started);
        }

        /// <summary>The Round 1 loop: the model replies with the zone map as JSON text.</summary>
        static async Task<AgentOutcome> RunJsonAsync(string brief, LlmConfig config, float kitScale, float footprintMetres,
                                                     int maxRepairRounds, Action<AgentRound> onRound, CancellationToken cancel)
        {
            var outcome = new AgentOutcome();
            float started = Time.realtimeSinceStartup;

            var conversation = new List<ChatTurn>
            {
                new("system", PlannerPrompt.System(kitScale, footprintMetres)),
                new("user", string.IsNullOrWhiteSpace(brief) ? "Map the damage in the field report." : brief.Trim())
            };

            AgentRound best = null;
            for (int r = 0; r <= maxRepairRounds; r++)
            {
                var result = await LlmChatClient.CompleteAsync(config, conversation, cancel);
                var round = new AgentRound
                {
                    Index = r,
                    TransportOk = result.Ok,
                    TransportError = result.Error,
                    LatencySeconds = result.LatencySeconds
                };
                outcome.Rounds.Add(round);

                if (!result.Ok)
                {
                    onRound?.Invoke(round);
                    break;                                  // no retries on transport failure
                }

                round.ParseOk = AgenticLLM_Bridge.TryParsePlanJson(result.Content, out round.Plan, out round.ParseError);
                if (round.ParseOk)
                {
                    round.Report = PlanVerifier.Verify(round.Plan, kitScale, footprintMetres);
                    if (best == null || round.Report.Violations.Count < best.Report.Violations.Count) best = round;
                }
                onRound?.Invoke(round);

                if (round.Verified)
                {
                    Finish(outcome, round.Plan, PlanSource.Model, round.Report, started);
                    return outcome;
                }

                if (r == maxRepairRounds) break;

                // Keep the model's own words in the history: it repairs better against what it actually said.
                conversation.Add(new ChatTurn("assistant", result.Content));
                conversation.Add(new ChatTurn("user", round.ParseOk
                    ? PlannerPrompt.VerificationRepair(round.Report.Violations)
                    : PlannerPrompt.ParseRepair(round.ParseError)));
            }

            if (best != null)
            {
                Finish(outcome, best.Plan, PlanSource.ModelUnverified, best.Report, started);
                return outcome;
            }

            var last = outcome.Rounds.Count > 0 ? outcome.Rounds[outcome.Rounds.Count - 1] : null;
            outcome.FallbackReason = last == null ? "no attempt made"
                : !last.TransportOk ? last.TransportError
                : $"no usable plan after {outcome.Rounds.Count} replies ({last.ParseError})";
            AgenticLLM_Bridge.TryParsePlanJson(AgenticLLM_Bridge.OfflinePlanJson, out var offline, out _);
            Finish(outcome, offline, PlanSource.OfflineFallback, PlanVerifier.Verify(offline, kitScale, footprintMetres), started);
            return outcome;
        }

        static void Finish(AgentOutcome outcome, SpatialPlan plan, PlanSource source, VerificationReport report, float started)
        {
            outcome.Plan = plan;
            outcome.Source = source;
            outcome.FinalReport = report;
            outcome.Verified = report != null && report.Passed;
            outcome.TotalSeconds = Time.realtimeSinceStartup - started;
        }
    }
}
