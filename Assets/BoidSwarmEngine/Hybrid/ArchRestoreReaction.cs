using System.Collections;
using UnityEngine;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// The Grand Gateway (GDD 5.1): the arch is fractured into segments hanging in mid-air, held by failing energy.
    /// The segments are authored in their broken poses; their rest poses are stored here. Until the gate is solved
    /// they drift and shiver; once it is, they fly home one after another, crown last.
    /// </summary>
    [AddComponentMenu("Funobotz/Gate Reactions/Arch Restore")]
    public sealed class ArchRestoreReaction : GateReaction
    {
        [SerializeField] Transform[] segments = System.Array.Empty<Transform>();
        [SerializeField] Vector3[] restPositions = System.Array.Empty<Vector3>();
        [SerializeField] Quaternion[] restRotations = System.Array.Empty<Quaternion>();

        [Header("Motion")]
        [SerializeField, Min(0.1f)] float segmentSeconds = 1.3f;
        [SerializeField, Min(0f)] float stagger = 0.35f;
        [Tooltip("Idle drift of the broken segments, in design metres.")]
        [SerializeField, Min(0f)] float driftAmplitude = 0.35f;
        [SerializeField, Min(0f)] float driftHz = 0.25f;

        Vector3[] brokenPositions;
        Quaternion[] brokenRotations;
        bool restoring;

        /// <summary>Editor wiring: register a segment with the pose it belongs in. Its current pose is the broken one.</summary>
        public void Configure(Transform[] pieces, Vector3[] restLocal, Quaternion[] restLocalRotation)
        {
            segments = pieces;
            restPositions = restLocal;
            restRotations = restLocalRotation;
        }

        void Awake()
        {
            int n = segments.Length;
            brokenPositions = new Vector3[n];
            brokenRotations = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                if (segments[i] == null) continue;
                brokenPositions[i] = segments[i].localPosition;
                brokenRotations[i] = segments[i].localRotation;
            }
        }

        void Update()
        {
            if (restoring) return;

            // Residual energy failing: each segment bobs and rocks on its own phase.
            float t = Time.time * driftHz * Mathf.PI * 2f;
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == null) continue;
                float phase = i * 1.7f;
                segments[i].localPosition = brokenPositions[i] + new Vector3(0f, Mathf.Sin(t + phase) * driftAmplitude, 0f);
                segments[i].localRotation = brokenRotations[i] *
                                            Quaternion.Euler(Mathf.Sin(t * 0.7f + phase) * 2f, 0f, Mathf.Sin(t * 1.3f + phase) * 3f);
            }
        }

        public override IEnumerator Play()
        {
            restoring = true;
            int n = segments.Length;
            var from = new Vector3[n];
            var fromRotation = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                if (segments[i] == null) continue;
                from[i] = segments[i].localPosition;
                fromRotation[i] = segments[i].localRotation;
            }

            // Outer segments first, the crown (keystone) last: the arch closes from the piers inward.
            int[] order = BuildOrder(n);
            float total = stagger * (n - 1) + segmentSeconds;
            float elapsed = 0f;
            while (elapsed < total)
            {
                elapsed += Time.deltaTime;
                for (int k = 0; k < n; k++)
                {
                    int i = order[k];
                    if (segments[i] == null) continue;
                    float local = Mathf.Clamp01((elapsed - k * stagger) / segmentSeconds);
                    float eased = EaseOutBack(local);
                    segments[i].localPosition = Vector3.LerpUnclamped(from[i], restPositions[i], eased);
                    segments[i].localRotation = Quaternion.SlerpUnclamped(fromRotation[i], restRotations[i], Mathf.Clamp01(eased));
                }
                yield return null;
            }

            for (int i = 0; i < n; i++)
            {
                if (segments[i] == null) continue;
                segments[i].localPosition = restPositions[i];
                segments[i].localRotation = restRotations[i];
            }
        }

        static int[] BuildOrder(int n)
        {
            var order = new int[n];
            int lo = 0, hi = n - 1, k = 0;
            while (lo <= hi)
            {
                order[k++] = lo++;
                if (lo <= hi) order[k++] = hi--;
            }
            return order;
        }
    }
}
