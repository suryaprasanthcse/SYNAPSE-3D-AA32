using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace MAAYAI.Swarm
{
    /// <summary>
    /// On-screen proof for the spatial matrix: reads the VPL grid back from the GPU once per interval and reports
    /// every link of the chain (tracking -> anchor -> swarm -> lights -> surfaces), so "nothing is visible" is never
    /// a guess. Also toggles stress mode (65,536 particles) and the SwarmSurface debug views.
    /// OnGUI only: no input-system dependency, works on device.
    /// </summary>
    public sealed class SwarmMatrixDiagnostics : MonoBehaviour
    {
        [SerializeField] SwarmGPUArchitect swarm;
        [SerializeField] SpatialMatrixCardRig cardRig;
        [SerializeField] bool showOnStart = true;
        [SerializeField, Min(0.1f)] float readbackInterval = 1f;

        static readonly int ID_SwarmSurfaceDebug = Shader.PropertyToID("_SwarmSurfaceDebug");
        static readonly string[] DebugViewNames = { "Lit", "Albedo only", "Swarm light only" };

        // World-light presets, cycled on device. An Android build is ~8 minutes, so the only sane way to land
        // these is to walk them up and down while looking at the real table.
        //
        // Each step drives two things at once: how much flux Petalo pours into the VPL grid, and the global
        // ambient floor the surface shaders add before shading. The floor matters because the void's lighting is
        // purely multiplicative - with no VPL in range a surface resolves to exactly black, so the far end of the
        // map is not dim, it is invisible. Index 0 is the original pure-void look with no floor at all.
        static readonly float[] BeaconPresets = { 0.5f, 1f, 2f, 4f, 8f, 16f };
        // Tuned against the blockout, not against Petalo. The avatar's housing is 0.94 albedo and reads at a much
        // lower floor than the 0.44 stone, so a level that makes Petalo visible still leaves the map black.
        static readonly float[] AmbientPresets = { 0f, 0.06f, 0.14f, 0.25f, 0.40f, 0.60f };
        const int DefaultLightPreset = 3;

        static readonly int ID_SwarmAmbient = Shader.PropertyToID("_SwarmAmbient");

        const string KeywordLegacyLighting = "SWARM_LEGACY_VPL_LOOP";
        const string KeywordLegacyParticles = "SWARM_LEGACY_PARTICLE_SIZE";

        bool visible;
        bool legacyLighting;
        bool legacyParticles;
        int debugView;

        static void SetKeyword(string keyword, bool enabled)
        {
            if (enabled) Shader.EnableKeyword(keyword);
            else Shader.DisableKeyword(keyword);
        }
        PetaloBeacon beacon;
        PlaneAnchoredWorld anchoredWorld;
        float nextReadback;
        bool readbackPending;
        float smoothedDt = 1f / 30f;

        // Last VPL readback.
        float readbackTime = -1f;
        int activeLights;
        int activeBeacons;
        float totalFlux;
        float maxFlux;
        float nearestLightToAnchor = -1f;

        // Sized from the live buffer, never from a constant: the buffer carries the swarm's cells PLUS the beacon
        // slots, and a fixed-size scratch throws "source and destination length must be the same" the moment that
        // count changes.
        Vector4[] syncScratch = System.Array.Empty<Vector4>();

        GUIStyle labelStyle;
        GUIStyle boxStyle;

        // IMGUI builds the same widget list twice per frame - once for Layout, once for Repaint - and demands the
        // two match exactly. Every one of these gates a block of controls, and every one of them can change
        // between those two passes: the async GPU readback callback lands whenever the driver is ready, Petalo
        // and the world root appear the moment a plane is found, and IsReady flips mid-frame on the first
        // dispatch. A gate flipping mid-frame throws "Getting control N's position in a group with only N
        // controls" and aborts the whole GUI pass, which takes the joystick and every button with it.
        //
        // So the gates are sampled once per frame in Update and OnGUI reads nothing else.
        bool guiHasSwarm;
        bool guiHasCardRig;
        bool guiReadbackReady;
        bool guiHasGap;
        bool guiHasWorld;
        bool guiHasBeacon;
        bool guiHasCamera;
        bool guiWorldPlaced;
        Camera guiCamera;

        void Awake()
        {
            visible = showOnStart;
            if (swarm == null) swarm = FindAnyObjectByType<SwarmGPUArchitect>();
            if (cardRig == null) cardRig = FindAnyObjectByType<SpatialMatrixCardRig>();

            // Push a usable floor before the first frame is drawn. Without this the ambient global is 0 until
            // somebody presses a button, which is exactly the "everything is black" state this is here to avoid.
            Shader.SetGlobalFloat(ID_SwarmAmbient, AmbientPresets[DefaultLightPreset]);
        }

        void OnDestroy()
        {
            Shader.SetGlobalFloat(ID_SwarmSurfaceDebug, 0f);
            Shader.SetGlobalFloat(ID_SwarmAmbient, 0f);
            SetKeyword(KeywordLegacyLighting, false);
            SetKeyword(KeywordLegacyParticles, false);
            PetaloBeacon.LegacyProfiling = false;
        }

        void Update()
        {
            smoothedDt = Mathf.Lerp(smoothedDt, Time.unscaledDeltaTime, 0.05f);
            SampleGuiGates();

            if (swarm == null || !swarm.LightingActive || readbackPending || Time.unscaledTime < nextReadback) return;
            nextReadback = Time.unscaledTime + readbackInterval;

            ComputeBuffer buffer = swarm.VPLBuffer;
            if (buffer == null || !buffer.IsValid()) return;

            int needed = buffer.count * 2;          // two float4 per light entry
            if (syncScratch.Length != needed) syncScratch = new Vector4[needed];

            if (SystemInfo.supportsAsyncGPUReadback)
            {
                readbackPending = true;
                AsyncGPUReadback.Request(buffer, OnReadback);
            }
            else
            {
                buffer.GetData(syncScratch);   // 2 KB once per interval: acceptable stall in a debug overlay
                Analyse(syncScratch);
            }
        }

        /// <summary>
        /// Freeze every condition OnGUI branches on. Also does the lazy lookups, which must not happen inside
        /// OnGUI: finding Petalo on the Repaint pass after missing it on Layout is exactly the mismatch that
        /// aborts the layout.
        /// </summary>
        void SampleGuiGates()
        {
            if (beacon == null) beacon = FindAnyObjectByType<PetaloBeacon>();
            if (anchoredWorld == null) anchoredWorld = FindAnyObjectByType<PlaneAnchoredWorld>();
            guiCamera = Camera.main;

            guiHasSwarm = swarm != null && swarm.IsReady;
            guiHasCardRig = cardRig != null;
            guiReadbackReady = readbackTime >= 0f;
            guiHasGap = nearestLightToAnchor >= 0f;
            guiHasWorld = anchoredWorld != null;
            guiHasBeacon = beacon != null;
            guiHasCamera = guiCamera != null;
            guiWorldPlaced = guiHasWorld && anchoredWorld.IsPlaced;
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            readbackPending = false;
            if (request.hasError || this == null) return;
            NativeArray<Vector4> data = request.GetData<Vector4>();
            if (data.Length != syncScratch.Length) syncScratch = new Vector4[data.Length];
            NativeArray<Vector4>.Copy(data, syncScratch, data.Length);
            Analyse(syncScratch);
        }

        void Analyse(Vector4[] vpl)
        {
            activeLights = 0;
            activeBeacons = 0;
            totalFlux = 0f;
            maxFlux = 0f;
            nearestLightToAnchor = -1f;

            Transform anchor = swarm != null ? swarm.Anchor : null;
            float nearestSq = float.MaxValue;

            int entries = vpl.Length / 2;
            for (int i = 0; i < entries; i++)
            {
                Vector4 pos = vpl[i * 2];
                Vector4 col = vpl[i * 2 + 1];
                float flux = 0.2126f * col.x + 0.7152f * col.y + 0.0722f * col.z;
                if (flux <= 1e-6f) continue;

                // Entries past the swarm's cells are beacons (Petalo, and later the Lumenforge).
                if (i >= SwarmGPUArchitect.VPLCells) activeBeacons++;
                else activeLights++;
                totalFlux += flux;
                maxFlux = Mathf.Max(maxFlux, flux);

                if (anchor != null)
                {
                    float d2 = ((Vector3)pos - anchor.position).sqrMagnitude;
                    if (d2 < nearestSq) nearestSq = d2;
                }
            }

            if (nearestSq < float.MaxValue) nearestLightToAnchor = Mathf.Sqrt(nearestSq);
            readbackTime = Time.unscaledTime;
        }

        void OnGUI()
        {
            float scale = Mathf.Max(1f, (Screen.dpi > 0f ? Screen.dpi : 160f) / 160f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            EnsureStyles();

            if (!visible)
            {
                if (GUI.Button(new Rect(8, 8, 90, 30), "Matrix HUD")) visible = true;
                return;
            }

            GUILayout.BeginArea(new Rect(8, 8, 390, 760), boxStyle);
            GUILayout.Label($"<b>SPATIAL MATRIX</b>   {1f / Mathf.Max(smoothedDt, 1e-4f):0} fps", labelStyle);

            bool hasSwarm = guiHasSwarm;
            Line("Swarm", hasSwarm, hasSwarm ? $"{swarm.ParticleCount:N0} particles{(swarm.StressMode ? "  [STRESS]" : "")}" : "not running");

            if (guiHasCardRig)
            {
                Line("Cards tracked", cardRig.VisibleCardCount > 0, $"{cardRig.VisibleCardCount} visible / {cardRig.TrackedCardCount} known");
                Line("Planes", cardRig.PlaneCount > 0, $"{cardRig.PlaneCount}");
            }
            else
            {
                Line("Card rig", false, "missing");
            }

            if (hasSwarm)
            {
                bool anchored = swarm.Placement != SwarmGPUArchitect.PlacementMode.CardAnchor || swarm.IsAnchorTracking;
                Line("Anchor", anchored, swarm.Placement == SwarmGPUArchitect.PlacementMode.CardAnchor
                    ? (swarm.IsAnchorTracking ? "King of Spades tracked" : "waiting for King of Spades (void)")
                    : "free placement");
                Line("Lighting", swarm.LightingActive, swarm.LightingActive ? "VPL grid bound" : "OFF (see console)");
            }

            if (guiReadbackReady)
            {
                float age = Time.unscaledTime - readbackTime;
                Line("VPLs lit", activeLights > 0,
                    $"{activeLights}/{SwarmGPUArchitect.VPLCells}   beacons {activeBeacons}   " +
                    $"total {totalFlux:0.000}   max {maxFlux:0.000}   ({age:0.0}s ago)");
                if (guiHasGap)
                    Line("Light-card gap", nearestLightToAnchor < 0.5f, $"{nearestLightToAnchor * 100f:0} cm to nearest VPL");
            }
            else
            {
                GUILayout.Label("VPL readback: pending", labelStyle);
            }

            // Where the world actually is. "Everything reports OK but I see nothing" is usually not a render
            // failure at all - the map anchors to the largest horizontal plane, which is often the floor rather
            // than the table being pointed at, so it ends up behind the player or under their feet.
            if (guiHasWorld)
            {
                Line("World", anchoredWorld.IsPlaced,
                     anchoredWorld.IsPlaced
                        ? $"{WorldState(anchoredWorld)}, 1:{(1f / Mathf.Max(anchoredWorld.WorldScale, 1e-6f)):0}, " +
                          $"arch {14f * anchoredWorld.WorldScale * 100f:0} cm"
                        : "NOT placed - still scanning for a surface");

                Camera cam = guiCamera;
                if (guiHasCamera && guiHasBeacon && guiWorldPlaced)
                {
                    Vector3 toPetalo = beacon.transform.position - cam.transform.position;
                    float distance = toPetalo.magnitude;
                    Vector3 view = cam.WorldToViewportPoint(beacon.transform.position);
                    bool onScreen = view.z > 0f && view.x > 0f && view.x < 1f && view.y > 0f && view.y < 1f;

                    string where = view.z <= 0f ? "BEHIND you"
                                 : onScreen ? "on screen"
                                 : view.x < 0f ? "off screen, to your LEFT"
                                 : view.x > 1f ? "off screen, to your RIGHT"
                                 : view.y > 1f ? "off screen, ABOVE" : "off screen, BELOW";

                    Line("Petalo", onScreen, $"{distance:0.00} m away, {where}");
                }
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (hasSwarm && GUILayout.Button(swarm.StressMode ? "Stress: 65,536 ON" : "Stress: OFF", GUILayout.Height(34)))
                swarm.StressMode = !swarm.StressMode;
            if (GUILayout.Button($"View: {DebugViewNames[debugView]}", GUILayout.Height(34)))
            {
                debugView = (debugView + 1) % DebugViewNames.Length;
                Shader.SetGlobalFloat(ID_SwarmSurfaceDebug, debugView);
            }
            if (GUILayout.Button("Hide", GUILayout.Height(34), GUILayout.Width(60))) visible = false;
            GUILayout.EndHorizontal();

            // Fill-rate A/B, for capturing both paths from one build on device. LEGACY is the pre-optimisation
            // renderer: every VPL integrated per fragment, and world-sized particle billboards with no clamp.
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(legacyLighting ? "Lighting: LEGACY loop" : "Lighting: Volume", GUILayout.Height(34)))
            {
                legacyLighting = !legacyLighting;
                SetKeyword(KeywordLegacyLighting, legacyLighting);
            }
            if (GUILayout.Button(legacyParticles ? "Particles: LEGACY size" : "Particles: Clamped", GUILayout.Height(34)))
            {
                legacyParticles = !legacyParticles;
                SetKeyword(KeywordLegacyParticles, legacyParticles);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(PetaloBeacon.LegacyProfiling ? "Petalo: LEGACY (2 layers, no early-Z)"
                                                              : "Petalo: Culled + conservative Z",
                                 GUILayout.Height(34)))
                PetaloBeacon.LegacyProfiling = !PetaloBeacon.LegacyProfiling;

            // One press for the whole baseline, so a capture session cannot end up half optimised.
            if (GUILayout.Button("ALL LEGACY", GUILayout.Height(34), GUILayout.Width(130)))
            {
                legacyLighting = legacyParticles = PetaloBeacon.LegacyProfiling = true;
                SetKeyword(KeywordLegacyLighting, true);
                SetKeyword(KeywordLegacyParticles, true);
            }
            if (GUILayout.Button("ALL FAST", GUILayout.Height(34), GUILayout.Width(110)))
            {
                legacyLighting = legacyParticles = PetaloBeacon.LegacyProfiling = false;
                SetKeyword(KeywordLegacyLighting, false);
                SetKeyword(KeywordLegacyParticles, false);
            }
            GUILayout.EndHorizontal();

            // Live beacon brightness. The VPL readout above is the feedback loop: step this until the map reads.
            if (guiHasBeacon)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"World light: {beacon.BeaconIntensity:0.##}", labelStyle,
                                GUILayout.Width(190f));
                if (GUILayout.Button("Dimmer", GUILayout.Height(34))) StepBeacon(-1);
                if (GUILayout.Button("Brighter", GUILayout.Height(34))) StepBeacon(+1);
                GUILayout.EndHorizontal();
            }

            // Freeze the dual-state morph anywhere along its range. Half-open is the pose the reference art
            // uses, and it only exists mid-transition, so it has to be holdable rather than passed through.
            if (guiHasBeacon)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Morph: {beacon.StateBlend:0.00}", labelStyle, GUILayout.Width(150f));
                if (GUILayout.Button("Petalo", GUILayout.Height(32))) beacon.StateBlend = 0f;
                if (GUILayout.Button("Split", GUILayout.Height(32))) beacon.StateBlend = 0.5f;
                if (GUILayout.Button("Anomaly", GUILayout.Height(32))) beacon.StateBlend = 1f;
                GUILayout.EndHorizontal();
            }

            // World size and lock. Resizing pivots on Petalo so the avatar stays where the player is looking
            // while the map grows around it; locking freezes the world in the room so they can walk around it.
            if (guiHasWorld)
            {
                var pivot = guiHasBeacon ? beacon.transform : null;

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Size: {anchoredWorld.SizeMultiplier:0.00}x", labelStyle, GUILayout.Width(120f));
                if (GUILayout.Button("Smaller", GUILayout.Height(34))) anchoredWorld.StepSize(-1, pivot);
                if (GUILayout.Button("Bigger", GUILayout.Height(34))) anchoredWorld.StepSize(+1, pivot);
                if (GUILayout.Button("Reset", GUILayout.Height(34), GUILayout.Width(70)))
                    anchoredWorld.SetSizeMultiplier(1f, pivot);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                bool locked = anchoredWorld.IsLocked;
                if (GUILayout.Button(locked ? "LOCKED - tap to unlock" : "Unlocked - tap to LOCK",
                                     GUILayout.Height(38)))
                {
                    anchoredWorld.ToggleLocked();
                    Debug.Log($"[Diagnostics] World {(anchoredWorld.IsLocked ? "locked" : "unlocked")}.");
                }
                if (guiHasBeacon && guiHasCamera &&
                    GUILayout.Button("Bring here", GUILayout.Height(38), GUILayout.Width(120f)))
                {
                    anchoredWorld.RecentreOn(beacon.transform, guiCamera.transform, 0.5f);
                    Debug.Log("[Diagnostics] World recentred on the camera.");
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.EndArea();
        }

        /// <summary>Move the world light to the next preset up or down from wherever it currently sits.</summary>
        void StepBeacon(int direction)
        {
            if (beacon == null) return;

            float current = beacon.BeaconIntensity;
            int nearest = 0;
            for (int i = 1; i < BeaconPresets.Length; i++)
                if (Mathf.Abs(BeaconPresets[i] - current) < Mathf.Abs(BeaconPresets[nearest] - current))
                    nearest = i;

            ApplyLightPreset(Mathf.Clamp(nearest + direction, 0, BeaconPresets.Length - 1));
        }

        void ApplyLightPreset(int index)
        {
            index = Mathf.Clamp(index, 0, BeaconPresets.Length - 1);
            if (beacon != null) beacon.BeaconIntensity = BeaconPresets[index];
            Shader.SetGlobalFloat(ID_SwarmAmbient, AmbientPresets[index]);
            Debug.Log($"[Diagnostics] World light -> beacon {BeaconPresets[index]}, ambient {AmbientPresets[index]}");
        }

        /// <summary>
        /// Distinguishes a lock that is actually pinned to the room from one that only froze the coordinates.
        /// Unity world space is not a fixed frame in AR - the session re-estimates its origin as it learns the
        /// room - so a lock without an anchor still drifts, and that difference has to be visible.
        /// </summary>
        static string WorldState(PlaneAnchoredWorld world)
        {
            if (!world.IsLocked) return "tracking plane";
            return world.IsAnchored ? "LOCKED + pinned" : "LOCKED (no anchor - will drift)";
        }

        void Line(string label, bool ok, string value)
        {
            string tag = ok ? "<color=#5CFF9A>OK </color>" : "<color=#FF6B6B>!! </color>";
            GUILayout.Label($"{tag} <b>{label}</b>: {value}", labelStyle);
        }

        void EnsureStyles()
        {
            if (labelStyle != null) return;
            labelStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13, wordWrap = true };
            labelStyle.normal.textColor = new Color(0.85f, 0.95f, 1f);
            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 8, 8) };
        }
    }
}
