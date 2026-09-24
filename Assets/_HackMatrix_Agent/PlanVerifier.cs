using System.Collections.Generic;
using UnityEngine;

namespace MAAYAI.HackMatrix
{
    /// <summary>
    /// The geometry every zone type occupies, in one place. The compiler builds from it, the planner routes over
    /// it, the verifier checks against it and the prompt quotes it to the model, so none of them can disagree
    /// about how big a HazardZone is.
    ///
    /// Extents are SITE metres (the real disaster site); multiply by kit scale for the AR tabletop. At the
    /// default 0.03 the 1.5 m footprint is a 50 m site and one storey is 9 cm.
    /// </summary>
    public static class SpatialRecipes
    {
        public const float StoreyDesign = 3.0f;          // one storey of a damaged structure
        public const float SwathDesign = 4.0f;           // sensor footprint width: the lawnmower's lane spacing
        public const float ClearanceDesign = 3.0f;       // how far above any structure the drone must fly
        public const float CruiseDesign = 3.0f;          // lowest survey altitude over open ground
        public const float LzBufferDesign = 2.0f;        // minimum gap between a landing zone and any collapse
        public const float RadioRangeDesign = 50f / 3f;  // one radio hop: 0.50 m on the tabletop at kit scale 0.03

        /// <summary>Half extents (x, z) of a zone, in site metres.</summary>
        public static Vector2 HalfExtentDesign(string type) => type switch
        {
            SpatialNodeTypes.HazardZone => new Vector2(4.0f, 4.0f),   // 8 m collapsed block
            SpatialNodeTypes.ShoringSite => new Vector2(3.0f, 3.0f),  // 6 m damaged house
            SpatialNodeTypes.RescueLZ => new Vector2(3.0f, 3.0f),     // 6 m clear landing square
            SpatialNodeTypes.LaunchPad => new Vector2(1.5f, 1.5f),
            _ => new Vector2(0.5f, 0.5f)                              // RelayNode: a mast
        };

        /// <summary>Height of what stands in a zone, in site metres.</summary>
        public static float HeightDesign(string type, int tier) => type switch
        {
            SpatialNodeTypes.HazardZone or SpatialNodeTypes.ShoringSite => (Mathf.Clamp(tier, 0, SpatialNodeTypes.MaxTier) + 1) * StoreyDesign,
            SpatialNodeTypes.RelayNode => 5.0f,
            SpatialNodeTypes.LaunchPad => 0.3f,
            _ => 0.2f                                                  // RescueLZ: a marked pad
        };

        /// <summary>Kept for the evaluation tool: zones have no plinth, so this is the zone itself.</summary>
        public static Vector2 FootprintHalfExtentDesign(string type, int tier) => HalfExtentDesign(type);

        /// <summary>
        /// Where the compiler will actually put a zone's centre, in AR metres: clamped so the whole zone stays
        /// inside the square. Isotropic, because the layout's facing is chosen at placement time.
        /// </summary>
        public static Vector2 ClampCentre(Vector2 requested, string type, int tier, float kitScale, float footprintMetres)
        {
            float allowed = AllowedHalfRange(type, tier, kitScale, footprintMetres);
            return new Vector2(Mathf.Clamp(requested.x, -allowed, allowed), Mathf.Clamp(requested.y, -allowed, allowed));
        }

        public static float AllowedHalfRange(string type, int tier, float kitScale, float footprintMetres)
        {
            Vector2 h = HalfExtentDesign(type);
            return Mathf.Max(footprintMetres * 0.5f - Mathf.Max(h.x, h.y) * kitScale, 0f);
        }
    }

    /// <summary>One zone as built: clamped centre, half extents and height, all in AR metres.</summary>
    public struct SiteZone
    {
        public int Index;           // the node's index in the plan, as the model numbered it
        public string Type;
        public int Tier;
        public Vector2 Requested;
        public Vector2 Centre;
        public Vector2 Half;
        public float Height;

        public float MinX => Centre.x - Half.x;
        public float MaxX => Centre.x + Half.x;
        public float MinZ => Centre.y - Half.y;
        public float MaxZ => Centre.y + Half.y;

        /// <summary>Strictly inside the footprint: a point on the edge is not over the structure.</summary>
        public bool StrictlyContains(Vector2 p) =>
            Mathf.Abs(p.x - Centre.x) < Half.x - 1e-4f && Mathf.Abs(p.y - Centre.y) < Half.y - 1e-4f;
    }

    /// <summary>A plan resolved into zones. Built identically by the compiler, the planner and the verifier.</summary>
    public sealed class SiteModel
    {
        public readonly List<SiteZone> Zones = new();
        public float KitScale;
        public float FootprintMetres;
        public float HalfFootprint => FootprintMetres * 0.5f;

        public static SiteModel Build(SpatialPlan plan, float kitScale, float footprintMetres)
        {
            var site = new SiteModel { KitScale = kitScale, FootprintMetres = footprintMetres };
            var nodes = plan?.nodes ?? System.Array.Empty<SpatialNode>();
            for (int i = 0; i < nodes.Length; i++)
            {
                string type = SpatialNodeTypes.Canonicalise(nodes[i].node_type);
                if (type == null) continue;
                int tier = Mathf.Clamp(nodes[i].tier, 0, SpatialNodeTypes.MaxTier);
                var requested = new Vector2(nodes[i].coordinates.x, nodes[i].coordinates.z);
                site.Zones.Add(new SiteZone
                {
                    Index = i,
                    Type = type,
                    Tier = tier,
                    Requested = requested,
                    Centre = SpatialRecipes.ClampCentre(requested, type, tier, kitScale, footprintMetres),
                    Half = SpatialRecipes.HalfExtentDesign(type) * kitScale,
                    Height = SpatialRecipes.HeightDesign(type, tier) * kitScale
                });
            }
            return site;
        }

        public SiteZone? First(string type)
        {
            foreach (var z in Zones) if (z.Type == type) return z;
            return null;
        }

        /// <summary>Structures the drone must clear. Landing surfaces (LaunchPad, RescueLZ) are not obstacles.</summary>
        public static bool IsObstacle(string type) =>
            type == SpatialNodeTypes.HazardZone || type == SpatialNodeTypes.ShoringSite || type == SpatialNodeTypes.RelayNode;
    }

    public enum ViolationCode
    {
        TooFewNodes,
        OutOfBounds,
        Overlap,
        NoLaunchPad,
        NoRescueLZ,
        NoSurveyZone,
        LzTooCloseToHazard,
        AltitudeClearance,
        PathOutOfBounds,
        CoverageGap,
        RelayIsolated
    }

    public sealed class PlanViolation
    {
        public ViolationCode Code;
        /// <summary>Written for the model: says what is wrong AND what would fix it.</summary>
        public string Message;
    }

    public sealed class VerificationReport
    {
        public readonly List<PlanViolation> Violations = new();
        public int ClampedNodes;
        /// <summary>The on-board planner's route for this zone map, as checked.</summary>
        public FlightPath Path;
        /// <summary>The radio network as checked; null when there is no LaunchPad or no RescueLZ to connect.</summary>
        public RelayNetwork Relay;
        public bool Passed => Violations.Count == 0;
    }

    /// <summary>
    /// The radio graph. Vertices are the LaunchPads, RelayNodes and RescueLZs; an edge joins two vertices whose
    /// centres are within one radio range. Only LaunchPads and RelayNodes FORWARD traffic: a RescueLZ is an
    /// endpoint, so a chain may end at a landing zone but never pass through one.
    ///
    /// Breadth-first search from every LaunchPad over the forwarding vertices gives each one its exact hop count
    /// (the fixpoint of "reached if within range of a reached forwarder", computed layer by layer, so the first
    /// layer that reaches a vertex is its shortest chain). A RescueLZ's hop count is one more than the nearest-in-
    /// hops forwarder within range of it. The search visits each vertex once, so it terminates on any input.
    /// </summary>
    public sealed class RelayNetwork
    {
        public float RangeMetres;
        /// <summary>Links on the shortest chains actually used, for drawing.</summary>
        public readonly List<(Vector2 a, Vector2 b)> Links = new();
        /// <summary>Hop count per RescueLZ (plan index -> hops, or -1 if isolated).</summary>
        public readonly Dictionary<int, int> LzHops = new();
        /// <summary>Hop count per LaunchPad (0) and RelayNode (plan index -> hops, or -1 if unreachable).</summary>
        public readonly Dictionary<int, int> ForwarderHops = new();
        public int RelayCount;

        public bool Connected
        {
            get
            {
                foreach (var h in LzHops.Values) if (h < 0) return false;
                return LzHops.Count > 0;
            }
        }

        /// <summary>Longest chain any landing zone needs (0 when none is connected).</summary>
        public int MaxHops
        {
            get
            {
                int max = 0;
                foreach (var h in LzHops.Values) max = Mathf.Max(max, h);
                return max;
            }
        }

        public static RelayNetwork Analyse(SiteModel site)
        {
            var net = new RelayNetwork { RangeMetres = SpatialRecipes.RadioRangeDesign * site.KitScale };
            float rangeSq = (net.RangeMetres + 1e-6f) * (net.RangeMetres + 1e-6f);   // a hop of exactly the range counts
            var z = site.Zones;

            // Forwarders: LaunchPads are the sources (hop 0), RelayNodes are reached by the search.
            var hops = new int[z.Count];
            var parent = new int[z.Count];
            var frontier = new List<int>();
            for (int i = 0; i < z.Count; i++)
            {
                hops[i] = -1;
                parent[i] = -1;
                if (z[i].Type == SpatialNodeTypes.RelayNode) net.RelayCount++;
                if (z[i].Type == SpatialNodeTypes.LaunchPad) { hops[i] = 0; frontier.Add(i); }
            }

            while (frontier.Count > 0)
            {
                var next = new List<int>();
                foreach (int f in frontier)
                    for (int j = 0; j < z.Count; j++)
                    {
                        if (hops[j] >= 0 || z[j].Type != SpatialNodeTypes.RelayNode) continue;
                        if ((z[j].Centre - z[f].Centre).sqrMagnitude > rangeSq) continue;
                        hops[j] = hops[f] + 1;
                        parent[j] = f;
                        next.Add(j);
                    }
                frontier = next;
            }
            for (int i = 0; i < z.Count; i++)
                if (z[i].Type == SpatialNodeTypes.LaunchPad || z[i].Type == SpatialNodeTypes.RelayNode)
                    net.ForwarderHops[z[i].Index] = hops[i];

            var drawn = new HashSet<int>();
            for (int i = 0; i < z.Count; i++)
            {
                if (z[i].Type != SpatialNodeTypes.RescueLZ) continue;
                int best = -1;
                for (int f = 0; f < z.Count; f++)
                {
                    if (hops[f] < 0 || (z[f].Centre - z[i].Centre).sqrMagnitude > rangeSq) continue;
                    if (best < 0 || hops[f] < hops[best]) best = f;
                }
                net.LzHops[z[i].Index] = best < 0 ? -1 : hops[best] + 1;
                if (best < 0) continue;

                net.Links.Add((z[best].Centre, z[i].Centre));
                for (int v = best; parent[v] >= 0 && drawn.Add(v); v = parent[v])
                    net.Links.Add((z[parent[v]].Centre, z[v].Centre));
            }
            return net;
        }

        /// <summary>
        /// For an isolated landing zone: the connected forwarder nearest to it, and evenly spaced relay positions
        /// on the straight line between them so that no hop exceeds 95% of the range. The line lies between two
        /// points inside the area, so every suggested position is inside it too.
        /// </summary>
        public static (SiteZone from, List<Vector2> relays)? Bridge(SiteModel site, SiteZone lz)
        {
            var net = Analyse(site);
            float range = net.RangeMetres;
            SiteZone? nearest = null;
            foreach (var zone in site.Zones)
            {
                if (!net.ForwarderHops.TryGetValue(zone.Index, out int h) || h < 0) continue;
                if (nearest == null || (zone.Centre - lz.Centre).sqrMagnitude < (nearest.Value.Centre - lz.Centre).sqrMagnitude)
                    nearest = zone;
            }
            if (nearest == null) return null;

            float gap = Vector2.Distance(nearest.Value.Centre, lz.Centre);
            int segments = Mathf.Max(2, Mathf.CeilToInt(gap / (range * 0.95f)));
            var relays = new List<Vector2>();
            for (int s = 1; s < segments; s++)
                relays.Add(Vector2.Lerp(nearest.Value.Centre, lz.Centre, s / (float)segments));
            return (nearest.Value, relays);
        }
    }

    /// <summary>
    /// The symbolic half of the agent: checks the model's zone map, and the flight path the on-board planner
    /// computes over it, against rules the JSON schema cannot express. Messages go back to the model verbatim,
    /// so each one names the nodes involved and the fix, in the plan's own units.
    ///
    /// Zone map rules (the model can fix these):
    ///   bounds        every zone inside the 1.5 m AR footprint, or the compiler would clamp it
    ///   overlap       no two zones intersect
    ///   LZ buffer     a RescueLZ keeps a clear gap from every HazardZone
    ///   required      one LaunchPad, one RescueLZ, at least one HazardZone or ShoringSite; 3-12 nodes
    ///   radio         every RescueLZ reaches a LaunchPad through RelayNodes, no hop over the radio range
    /// Flight path rules (the planner guarantees these by construction; checked independently anyway):
    ///   altitude      every point of the path over a HazardZone, ShoringSite or RelayNode is above its top
    ///                 plus clearance
    ///   bounds        every waypoint inside the footprint
    ///   coverage      every point of every survey zone lies inside the sensor swath of some leg
    /// </summary>
    public static class PlanVerifier
    {
        const float OverlapToleranceMetres = 0.005f;
        const int SamplesPerLeg = 24;

        public static VerificationReport Verify(SpatialPlan plan, float kitScale, float footprintMetres)
        {
            var report = new VerificationReport();
            int count = plan?.nodes?.Length ?? 0;
            if (count < 3 || count > 16)
                Add(report, ViolationCode.TooFewNodes, $"The zone map has {count} node(s); use between 3 and 16.");

            var site = SiteModel.Build(plan, kitScale, footprintMetres);
            CheckBounds(report, site);
            CheckRequired(report, site);
            CheckOverlapsAndBuffer(report, site);
            CheckRelayNetwork(report, site);

            report.Path = FlightPlanner.Plan(site);
            CheckPath(report, site, report.Path);
            return report;
        }

        static void CheckBounds(VerificationReport report, SiteModel site)
        {
            foreach (var z in site.Zones)
            {
                if ((z.Centre - z.Requested).sqrMagnitude <= 1e-8f) continue;
                report.ClampedNodes++;
                float allowed = SpatialRecipes.AllowedHalfRange(z.Type, z.Tier, site.KitScale, site.FootprintMetres);
                Add(report, ViolationCode.OutOfBounds,
                    $"node {z.Index} ({z.Type}) at x={z.Requested.x:0.00}, z={z.Requested.y:0.00} is outside the " +
                    $"{site.FootprintMetres:0.0} m survey area; its x and z must each lie within +-{allowed:0.00}.");
            }
        }

        static void CheckRequired(VerificationReport report, SiteModel site)
        {
            int launch = 0, lz = 0, survey = 0;
            foreach (var z in site.Zones)
            {
                if (z.Type == SpatialNodeTypes.LaunchPad) launch++;
                else if (z.Type == SpatialNodeTypes.RescueLZ) lz++;
                else if (SpatialNodeTypes.IsSurveyZone(z.Type)) survey++;
            }
            if (launch == 0) Add(report, ViolationCode.NoLaunchPad, "There is no LaunchPad; add exactly one, on open ground near the edge of the area.");
            if (lz == 0) Add(report, ViolationCode.NoRescueLZ, "There is no RescueLZ; add one on the clearest open ground in the report.");
            if (survey == 0) Add(report, ViolationCode.NoSurveyZone, "There is no HazardZone or ShoringSite; map every damaged structure the report mentions.");
        }

        static void CheckOverlapsAndBuffer(VerificationReport report, SiteModel site)
        {
            float buffer = SpatialRecipes.LzBufferDesign * site.KitScale;
            var z = site.Zones;
            for (int i = 0; i < z.Count; i++)
                for (int j = i + 1; j < z.Count; j++)
                {
                    var a = z[i];
                    var b = z[j];
                    bool lzHazard = (a.Type == SpatialNodeTypes.RescueLZ && b.Type == SpatialNodeTypes.HazardZone) ||
                                    (b.Type == SpatialNodeTypes.RescueLZ && a.Type == SpatialNodeTypes.HazardZone);
                    if (lzHazard)
                    {
                        float gap = Gap(a, b);
                        if (gap < buffer)
                        {
                            var lz = a.Type == SpatialNodeTypes.RescueLZ ? a : b;
                            var hz = a.Type == SpatialNodeTypes.RescueLZ ? b : a;
                            Add(report, ViolationCode.LzTooCloseToHazard,
                                $"the RescueLZ (node {lz.Index}) is {Mathf.Max(gap, 0f) * 100f:0} cm from HazardZone node " +
                                $"{hz.Index}; debris and downwash need at least {buffer * 100f:0} cm. Move the RescueLZ " +
                                "to open ground further away.");
                        }
                        continue;
                    }

                    float dx = a.Half.x + b.Half.x - Mathf.Abs(a.Centre.x - b.Centre.x);
                    float dz = a.Half.y + b.Half.y - Mathf.Abs(a.Centre.y - b.Centre.y);
                    if (dx <= OverlapToleranceMetres || dz <= OverlapToleranceMetres) continue;
                    Add(report, ViolationCode.Overlap,
                        $"node {a.Index} ({a.Type}) overlaps node {b.Index} ({b.Type}) by {Mathf.Min(dx, dz) * 100f:0} cm; " +
                        "move them apart.");
                }
        }

        /// <summary>
        /// Infrastructure-denied comms: every RescueLZ needs an unbroken radio chain back to a LaunchPad, through
        /// RelayNodes, with no hop longer than the radio range. An isolated LZ fails with the exact relay
        /// positions that would connect it, so the model can repair it in one round.
        /// </summary>
        static void CheckRelayNetwork(VerificationReport report, SiteModel site)
        {
            if (site.First(SpatialNodeTypes.LaunchPad) == null || site.First(SpatialNodeTypes.RescueLZ) == null) return;

            var net = RelayNetwork.Analyse(site);
            report.Relay = net;
            foreach (var lz in site.Zones)
            {
                if (lz.Type != SpatialNodeTypes.RescueLZ || net.LzHops[lz.Index] >= 0) continue;

                var bridge = RelayNetwork.Bridge(site, lz);
                string fix = "";
                if (bridge != null)
                {
                    var (from, relays) = bridge.Value;
                    var positions = new List<string>();
                    foreach (var p in relays) positions.Add($"(x={p.x:0.00}, z={p.y:0.00})");
                    fix = $" The nearest connected radio is node {from.Index} ({from.Type}), " +
                          $"{Vector2.Distance(from.Centre, lz.Centre):0.00} m away: add {relays.Count} RelayNode(s) at " +
                          $"{string.Join(", ", positions)}, or nearby points that keep every hop within range and clear of other zones.";
                }
                Add(report, ViolationCode.RelayIsolated,
                    $"the RescueLZ (node {lz.Index}) at x={lz.Centre.x:0.00}, z={lz.Centre.y:0.00} has no radio link: every " +
                    $"LZ needs a chain of hops of at most {net.RangeMetres:0.00} m back to the LaunchPad through RelayNodes." + fix);
            }
        }

        static void CheckPath(VerificationReport report, SiteModel site, FlightPath path)
        {
            var w = path.Waypoints;
            float clearance = SpatialRecipes.ClearanceDesign * site.KitScale;
            float limit = site.HalfFootprint + 1e-4f;

            for (int k = 0; k < w.Count; k++)
                if (Mathf.Abs(w[k].x) > limit || Mathf.Abs(w[k].z) > limit)
                {
                    Add(report, ViolationCode.PathOutOfBounds,
                        $"flight waypoint {k} at x={w[k].x:0.00}, z={w[k].z:0.00} leaves the survey area; keep zones " +
                        "away from the very edge so the sweep can turn inside it.");
                    break;
                }

            // Altitude: sample every leg, including its endpoints, against every structure it passes over.
            bool altitudeFailed = false;
            for (int k = 0; k + 1 < w.Count && !altitudeFailed; k++)
                for (int s = 0; s <= SamplesPerLeg && !altitudeFailed; s++)
                {
                    Vector3 p = Vector3.Lerp(w[k], w[k + 1], s / (float)SamplesPerLeg);
                    var flat = new Vector2(p.x, p.z);
                    foreach (var z in site.Zones)
                    {
                        if (!SiteModel.IsObstacle(z.Type) || !z.StrictlyContains(flat)) continue;
                        if (p.y >= z.Height + clearance - 1e-4f) continue;
                        Add(report, ViolationCode.AltitudeClearance,
                            $"the flight path over node {z.Index} ({z.Type}) is at {p.y * 100f:0} cm, below its " +
                            $"{z.Height * 100f:0} cm top plus {clearance * 100f:0} cm clearance.");
                        altitudeFailed = true;
                        break;
                    }
                }

            foreach (var z in site.Zones)
            {
                if (!SpatialNodeTypes.IsSurveyZone(z.Type)) continue;
                if (FlightPlanner.CoverageFraction(z, path, site) >= 1f) continue;
                Add(report, ViolationCode.CoverageGap,
                    $"the survey sweep does not fully cover node {z.Index} ({z.Type}); move it away from the edge " +
                    "of the area.");
            }
        }

        /// <summary>Edge-to-edge distance between two zones (0 when they touch or overlap).</summary>
        static float Gap(SiteZone a, SiteZone b)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(a.Centre.x - b.Centre.x) - a.Half.x - b.Half.x);
            float dz = Mathf.Max(0f, Mathf.Abs(a.Centre.y - b.Centre.y) - a.Half.y - b.Half.y);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static void Add(VerificationReport report, ViolationCode code, string message) =>
            report.Violations.Add(new PlanViolation { Code = code, Message = message });
    }
}
