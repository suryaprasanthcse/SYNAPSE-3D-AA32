using System.Collections.Generic;
using UnityEngine;

namespace MAAYAI.HackMatrix
{
    /// <summary>A drone route in AR metres, local to the layout root (y = height above the floor).</summary>
    public sealed class FlightPath
    {
        public readonly List<Vector3> Waypoints = new();
        /// <summary>Distance along the path at each waypoint; same length as <see cref="Waypoints"/>.</summary>
        public readonly List<float> Cumulative = new();
        /// <summary>Survey lanes flown (the lawnmower's passes).</summary>
        public int Lanes;
        /// <summary>Plan index of each survey zone, with the path distance at which it is fully imaged.</summary>
        public readonly List<(int node, string type, int tier, float coveredAt)> Dispatches = new();

        public float TotalLength => Cumulative.Count > 0 ? Cumulative[Cumulative.Count - 1] : 0f;

        public Vector3 PointAt(float distance)
        {
            if (Waypoints.Count == 0) return Vector3.zero;
            if (distance <= 0f) return Waypoints[0];
            for (int k = 1; k < Waypoints.Count; k++)
            {
                if (distance > Cumulative[k]) continue;
                float leg = Cumulative[k] - Cumulative[k - 1];
                float t = leg > 1e-6f ? (distance - Cumulative[k - 1]) / leg : 1f;
                return Vector3.Lerp(Waypoints[k - 1], Waypoints[k], t);
            }
            return Waypoints[Waypoints.Count - 1];
        }
    }

    /// <summary>
    /// The on-board planner. The model never outputs waypoints: it outputs a zone map, and this turns the zone
    /// map into a route, deterministically, with no randomness, so the same zones always fly the same path.
    ///
    ///   take off     climb from the LaunchPad to transit altitude (above every structure on the site)
    ///   sweep        boustrophedon (lawnmower) lanes across the bounding box of the survey zones, spaced one
    ///                sensor swath apart, so the swaths tile it without gaps
    ///   clear        on each lane, the drone CLIMBS over structures instead of detouring around them: at every
    ///                structure edge it steps to that stretch's required altitude (top + clearance), so a
    ///                detour can never hit another zone or leave the area
    ///   land         climb back to transit altitude, fly to the RescueLZ, descend onto it
    ///
    /// Coverage is defined once, in <see cref="CoverageFraction"/>, and both this planner (dispatch timing) and
    /// the verifier use it.
    /// </summary>
    public static class FlightPlanner
    {
        const int CoverageSamples = 5;      // per axis, over each survey zone

        public static FlightPath Plan(SiteModel site)
        {
            var path = new FlightPath();
            float k = site.KitScale;
            float half = site.HalfFootprint;
            float swath = SpatialRecipes.SwathDesign * k;
            float clearance = SpatialRecipes.ClearanceDesign * k;
            float cruise = SpatialRecipes.CruiseDesign * k;

            float transit = cruise;
            foreach (var z in site.Zones)
                if (SiteModel.IsObstacle(z.Type)) transit = Mathf.Max(transit, z.Height + clearance);

            var launchZone = site.First(SpatialNodeTypes.LaunchPad);
            var lzZone = site.First(SpatialNodeTypes.RescueLZ);
            Vector2 launch = launchZone?.Centre ?? new Vector2(0f, -half + 0.05f);
            float launchTop = launchZone?.Height ?? 0f;

            var w = new List<Vector3>
            {
                new(launch.x, launchTop, launch.y),
                new(launch.x, transit, launch.y)
            };

            // Bounding box of everything that must be imaged.
            bool any = false;
            float minX = 0f, maxX = 0f, minZ = 0f, maxZ = 0f;
            foreach (var z in site.Zones)
            {
                if (!SpatialNodeTypes.IsSurveyZone(z.Type)) continue;
                if (!any) { minX = z.MinX; maxX = z.MaxX; minZ = z.MinZ; maxZ = z.MaxZ; any = true; continue; }
                minX = Mathf.Min(minX, z.MinX); maxX = Mathf.Max(maxX, z.MaxX);
                minZ = Mathf.Min(minZ, z.MinZ); maxZ = Mathf.Max(maxZ, z.MaxZ);
            }

            if (any)
            {
                // Lanes centred on the box; n * swath >= its depth, so the swaths tile it. A lane pushed back
                // inside the area only moves toward its neighbour, which can widen an overlap but never open a gap.
                int n = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / swath - 1e-4f));
                float firstZ = (minZ + maxZ) * 0.5f - n * swath * 0.5f + swath * 0.5f;
                float xLeft = Mathf.Max(minX - swath * 0.5f, -half);
                float xRight = Mathf.Min(maxX + swath * 0.5f, half);
                path.Lanes = n;

                float previousZ = 0f;
                for (int lane = 0; lane < n; lane++)
                {
                    float z = Mathf.Clamp(firstZ + lane * swath, -half, half);
                    bool eastward = lane % 2 == 0;
                    float from = eastward ? xLeft : xRight;
                    float to = eastward ? xRight : xLeft;
                    var stretches = LaneStretches(site, from, to, z - swath * 0.5f, z + swath * 0.5f, cruise, clearance);

                    if (lane == 0)
                    {
                        w.Add(new Vector3(from, transit, z));
                    }
                    else
                    {
                        // Turn at the end of the previous lane: up to whatever the connector needs, across, then
                        // onto this lane's first stretch.
                        float connector = RequiredAltitude(site, from - 1e-4f, from + 1e-4f,
                                                           Mathf.Min(previousZ, z), Mathf.Max(previousZ, z), cruise, clearance);
                        float turn = Mathf.Max(w[w.Count - 1].y, connector);
                        w.Add(new Vector3(from, turn, previousZ));
                        w.Add(new Vector3(from, turn, z));
                    }

                    float x = from;
                    foreach (var (end, altitude) in stretches)
                    {
                        w.Add(new Vector3(x, altitude, z));     // step to this stretch's altitude at its edge
                        w.Add(new Vector3(end, altitude, z));
                        x = end;
                    }
                    previousZ = z;
                }
            }

            // Back up to transit altitude, then down onto the landing zone (or home, if there is none).
            Vector3 last = w[w.Count - 1];
            w.Add(new Vector3(last.x, transit, last.z));
            Vector2 land = lzZone?.Centre ?? launch;
            float landTop = lzZone?.Height ?? launchTop;
            w.Add(new Vector3(land.x, transit, land.y));
            w.Add(new Vector3(land.x, landTop, land.y));

            foreach (var p in w)
            {
                if (path.Waypoints.Count > 0 && (path.Waypoints[path.Waypoints.Count - 1] - p).sqrMagnitude < 1e-10f) continue;
                path.Cumulative.Add(path.Waypoints.Count == 0 ? 0f
                    : path.Cumulative[path.Cumulative.Count - 1] + Vector3.Distance(path.Waypoints[path.Waypoints.Count - 1], p));
                path.Waypoints.Add(p);
            }

            // Incremental dispatch: each survey zone is reported the moment the sweep has imaged all of it.
            foreach (var z in site.Zones)
                if (SpatialNodeTypes.IsSurveyZone(z.Type))
                    path.Dispatches.Add((z.Index, z.Type, z.Tier, CoveredAtDistance(z, path, site)));
            path.Dispatches.Sort((a, b) => a.coveredAt.CompareTo(b.coveredAt));
            return path;
        }

        /// <summary>
        /// Split one lane at every structure edge within its swath, and give each stretch the altitude that clears
        /// everything under it. Consecutive stretches at the same altitude are merged.
        /// </summary>
        static List<(float end, float altitude)> LaneStretches(SiteModel site, float from, float to, float bandMin, float bandMax,
                                                                float cruise, float clearance)
        {
            float lo = Mathf.Min(from, to), hi = Mathf.Max(from, to);
            var cuts = new List<float> { from, to };
            foreach (var z in site.Zones)
            {
                if (!SiteModel.IsObstacle(z.Type) || z.MaxZ <= bandMin || z.MinZ >= bandMax) continue;
                if (z.MinX > lo && z.MinX < hi) cuts.Add(z.MinX);
                if (z.MaxX > lo && z.MaxX < hi) cuts.Add(z.MaxX);
            }
            cuts.Sort();
            if (from > to) cuts.Reverse();

            var stretches = new List<(float end, float altitude)>();
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                if (Mathf.Abs(cuts[i + 1] - cuts[i]) < 1e-6f) continue;
                float a = Mathf.Min(cuts[i], cuts[i + 1]), b = Mathf.Max(cuts[i], cuts[i + 1]);
                float altitude = RequiredAltitude(site, a, b, bandMin, bandMax, cruise, clearance);
                if (stretches.Count > 0 && Mathf.Abs(stretches[stretches.Count - 1].altitude - altitude) < 1e-6f)
                    stretches[stretches.Count - 1] = (cuts[i + 1], altitude);
                else
                    stretches.Add((cuts[i + 1], altitude));
            }
            return stretches;
        }

        /// <summary>Lowest safe altitude over a rectangle: cruise, or the top of anything under it plus clearance.</summary>
        static float RequiredAltitude(SiteModel site, float minX, float maxX, float minZ, float maxZ, float cruise, float clearance)
        {
            float altitude = cruise;
            foreach (var z in site.Zones)
            {
                if (!SiteModel.IsObstacle(z.Type)) continue;
                if (z.MaxX <= minX || z.MinX >= maxX || z.MaxZ <= minZ || z.MinZ >= maxZ) continue;
                altitude = Mathf.Max(altitude, z.Height + clearance);
            }
            return altitude;
        }

        // ==========================================================================================
        // Coverage: one definition, used by the planner and the verifier.
        // A point on the ground is imaged when some leg of the path passes within half a swath of it.
        // ==========================================================================================
        public static float CoverageFraction(SiteZone zone, FlightPath path, SiteModel site)
        {
            float radius = SpatialRecipes.SwathDesign * site.KitScale * 0.5f + 1e-3f;
            int covered = 0, total = 0;
            foreach (var p in Samples(zone))
            {
                total++;
                if (FirstCoverage(p, path, radius) >= 0f) covered++;
            }
            return total == 0 ? 1f : covered / (float)total;
        }

        /// <summary>Path distance at which every sample of the zone has been imaged (the path's end if never).</summary>
        static float CoveredAtDistance(SiteZone zone, FlightPath path, SiteModel site)
        {
            float radius = SpatialRecipes.SwathDesign * site.KitScale * 0.5f + 1e-3f;
            float latest = 0f;
            foreach (var p in Samples(zone))
            {
                float at = FirstCoverage(p, path, radius);
                latest = Mathf.Max(latest, at < 0f ? path.TotalLength : at);
            }
            return latest;
        }

        static IEnumerable<Vector2> Samples(SiteZone zone)
        {
            for (int i = 0; i < CoverageSamples; i++)
                for (int j = 0; j < CoverageSamples; j++)
                {
                    float u = i / (float)(CoverageSamples - 1) * 2f - 1f;
                    float v = j / (float)(CoverageSamples - 1) * 2f - 1f;
                    yield return zone.Centre + new Vector2(u * zone.Half.x, v * zone.Half.y);
                }
        }

        /// <summary>Distance along the path where the point first falls inside the swath, or -1 if never.</summary>
        static float FirstCoverage(Vector2 point, FlightPath path, float radius)
        {
            var w = path.Waypoints;
            for (int k = 0; k + 1 < w.Count; k++)
            {
                var a = new Vector2(w[k].x, w[k].z);
                var b = new Vector2(w[k + 1].x, w[k + 1].z);
                Vector2 ab = b - a;
                float lengthSq = ab.sqrMagnitude;
                float t = lengthSq > 1e-12f ? Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSq) : 0f;
                if ((a + ab * t - point).sqrMagnitude > radius * radius) continue;
                return Mathf.Lerp(path.Cumulative[k], path.Cumulative[k + 1], t);
            }
            return -1f;
        }
    }
}
