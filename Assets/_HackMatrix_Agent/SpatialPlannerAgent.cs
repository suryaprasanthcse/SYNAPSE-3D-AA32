using System;
using System.Threading;
using UnityEngine;

namespace MAAYAI.HackMatrix
{
    /// <summary>
    /// The agent on the device: runs <see cref="PlannerAgentLoop"/> - plan, verify, repair - against the bridge's
    /// endpoint, then hands the result to the compiler. "The agent is the planner; the swarm is its renderer."
    ///
    /// Everything that can go wrong still ends in a built layout: forced offline, transport failure, a budget
    /// spent without a verified plan, even an exception from somewhere unforeseen. The outcome always says which
    /// of those happened, round by round, so the console can show the loop working rather than claim it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Hack Matrix/Spatial Planner Agent")]
    public sealed class SpatialPlannerAgent : MonoBehaviour
    {
        [SerializeField] AgenticLLM_Bridge bridge;
        [SerializeField] GenerativeCompiler compiler;
        [Tooltip("Repairs after the first plan. 3 means up to 4 model calls.")]
        [SerializeField, Range(0, 5)] int maxRepairRounds = 3;

        /// <summary>Raised once per run, after the compiler has been handed the plan.</summary>
        public event Action<AgentOutcome> OutcomeReady;

        /// <summary>Raised after every model call, while the loop is still running.</summary>
        public event Action<AgentRound> RoundCompleted;

        public bool IsBusy { get; private set; }
        public AgentOutcome LastOutcome { get; private set; }
        public AgentRound LastRound { get; private set; }
        public int MaxRepairRounds => maxRepairRounds;
        public AgenticLLM_Bridge Bridge => bridge;

        CancellationTokenSource cancel;
        int generation;

        void Awake()
        {
            if (bridge == null) bridge = FindAnyObjectByType<AgenticLLM_Bridge>();
            if (compiler == null) compiler = FindAnyObjectByType<GenerativeCompiler>();
        }

        void OnDisable() => CancelRun();

        /// <summary>Run the loop on a brief. A run already in progress is cancelled: the newest brief wins.</summary>
        public void Run(string brief)
        {
            CancelRun();
            cancel = new CancellationTokenSource();
            RunAsync(brief, ++generation, cancel.Token);
        }

        void CancelRun()
        {
            cancel?.Cancel();
            cancel?.Dispose();
            cancel = null;
            IsBusy = false;
        }

        async void RunAsync(string brief, int run, CancellationToken token)
        {
            IsBusy = true;
            LastRound = null;
            AgentOutcome outcome;

            float kitScale = compiler != null ? compiler.KitScale : 0.03f;
            float footprint = compiler != null ? compiler.FootprintMetres : 1.5f;

            try
            {
                if (bridge == null || bridge.ForceOffline)
                {
                    outcome = OfflineOutcome(bridge == null ? "no bridge" : "forced offline", kitScale, footprint);
                }
                else
                {
                    outcome = await PlannerAgentLoop.RunAsync(brief, bridge.BuildConfig(), kitScale, footprint,
                                                              maxRepairRounds, OnRound, token);
                }
            }
            catch (Exception e)
            {
                outcome = OfflineOutcome("unexpected: " + e.Message, kitScale, footprint);
            }

            if (this == null || run != generation || token.IsCancellationRequested) return;   // superseded

            IsBusy = false;
            LastOutcome = outcome;
            if (compiler != null) compiler.Compile(outcome.Plan);

            Debug.Log($"[PlannerAgent] {outcome.Source}{(outcome.Verified ? " VERIFIED" : "")} after " +
                      $"{outcome.Rounds.Count} call(s), {outcome.RepairRounds} repair(s), {outcome.TotalSeconds:0.0} s" +
                      (outcome.FallbackReason != null ? $" ({outcome.FallbackReason})" : "") + ".", this);
            OutcomeReady?.Invoke(outcome);

            void OnRound(AgentRound round)
            {
                if (run != generation) return;
                LastRound = round;
                RoundCompleted?.Invoke(round);
            }
        }

        static AgentOutcome OfflineOutcome(string reason, float kitScale, float footprint)
        {
            var plan = AgenticLLM_Bridge.OfflinePlan();
            var report = PlanVerifier.Verify(plan, kitScale, footprint);
            return new AgentOutcome
            {
                Plan = plan,
                Source = PlanSource.OfflineFallback,
                FallbackReason = reason,
                FinalReport = report,
                Verified = report.Passed
            };
        }
    }
}
