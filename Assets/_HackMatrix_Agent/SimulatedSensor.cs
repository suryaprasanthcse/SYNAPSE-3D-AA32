using UnityEngine;

namespace MAAYAI.HackMatrix
{
    public enum DamageClass { Safe, Unstable, Critical }

    /// <summary>What the drone's (simulated) sensor reads over one structure.</summary>
    public struct StructureReading
    {
        public float TiltDegrees;       // lean of the structure from vertical
        public float CrackWidthMm;      // widest visible crack
        public float CollapsedFraction; // share of the roof line that has come down, 0..1

        public StructureReading(float tilt, float crack, float collapsed)
        {
            TiltDegrees = tilt;
            CrackWidthMm = crack;
            CollapsedFraction = collapsed;
        }
    }

    /// <summary>
    /// STAGE 1 STAND-IN FOR A VISION MODEL. A fixed threshold rule over three structural readings, and a
    /// benchmark of 20 synthetic, hand-labelled cases. The accuracy it prints measures this rule on these
    /// cases, not any AI model on real imagery; the HUD says so. It exists to demonstrate the incremental
    /// dispatch pipeline end to end; real classification (Gemini vision on labelled images) replaces
    /// <see cref="Classify"/> in Stage 2.
    /// </summary>
    public static class SimulatedSensor
    {
        public const string Disclosure = "SIMULATED: RULE CLASSIFIER ON 20 SYNTHETIC CASES, NOT A VISION MODEL";

        /// <summary>The rule. Thresholds follow common rapid-assessment practice: collapse or a big lean is critical.</summary>
        public static DamageClass Classify(StructureReading r)
        {
            if (r.CollapsedFraction >= 0.30f || r.TiltDegrees >= 8f) return DamageClass.Critical;
            if (r.CrackWidthMm >= 3f || r.TiltDegrees >= 3f || r.CollapsedFraction >= 0.05f) return DamageClass.Unstable;
            return DamageClass.Safe;
        }

        /// <summary>
        /// The reading a zone produces in the simulation, derived deterministically from what the model mapped it
        /// as. Taller structures read slightly worse. Used for the in-flight dispatch messages.
        /// </summary>
        public static StructureReading ReadingFor(string type, int tier) => type switch
        {
            SpatialNodeTypes.HazardZone => new StructureReading(6f + tier, 12f, 0.35f + 0.1f * tier),
            SpatialNodeTypes.ShoringSite => new StructureReading(3.5f + 0.5f * tier, 4f + tier, 0.02f),
            _ => new StructureReading(0.5f, 0.5f, 0f)
        };

        // Ground truth as a structural surveyor labelled it. Several cases sit on or across the rule's
        // thresholds on purpose (pancake collapses with no lean, a leaning but intact tower, hairline cracks
        // on a soft storey), so the rule is expected to miss some.
        static readonly (StructureReading reading, DamageClass truth)[] Cases =
        {
            (new StructureReading(12f, 20f, 0.60f), DamageClass.Critical),
            (new StructureReading(2f, 15f, 0.45f), DamageClass.Critical),
            (new StructureReading(9f, 6f, 0.10f), DamageClass.Critical),
            (new StructureReading(1f, 8f, 0.25f), DamageClass.Critical),   // pancake: low lean, partial roof loss
            (new StructureReading(7f, 10f, 0.20f), DamageClass.Critical),  // just under both critical thresholds
            (new StructureReading(15f, 3f, 0.05f), DamageClass.Critical),
            (new StructureReading(0.5f, 25f, 0.80f), DamageClass.Critical),
            (new StructureReading(4f, 5f, 0.02f), DamageClass.Unstable),
            (new StructureReading(3f, 2f, 0.00f), DamageClass.Unstable),
            (new StructureReading(1f, 4f, 0.00f), DamageClass.Unstable),
            (new StructureReading(5f, 1f, 0.00f), DamageClass.Unstable),
            (new StructureReading(2f, 6f, 0.08f), DamageClass.Unstable),
            (new StructureReading(8.5f, 2f, 0.00f), DamageClass.Unstable), // leaning tower, intact: surveyor says shore it
            (new StructureReading(2f, 2.5f, 0.00f), DamageClass.Unstable), // soft storey, cracks under threshold
            (new StructureReading(0.5f, 0.5f, 0.00f), DamageClass.Safe),
            (new StructureReading(1f, 1f, 0.00f), DamageClass.Safe),
            (new StructureReading(0f, 0f, 0.00f), DamageClass.Safe),
            (new StructureReading(2f, 1.5f, 0.00f), DamageClass.Safe),
            (new StructureReading(1.5f, 2f, 0.00f), DamageClass.Safe),
            (new StructureReading(2.5f, 3.2f, 0.00f), DamageClass.Safe)    // cosmetic plaster crack reads as structural
        };

        public static int CaseCount => Cases.Length;

        /// <summary>Run the rule over every case. Deterministic: the same number every run.</summary>
        public static (int correct, int total) RunBenchmark()
        {
            int correct = 0;
            foreach (var (reading, truth) in Cases)
                if (Classify(reading) == truth) correct++;
            return (correct, Cases.Length);
        }
    }
}
