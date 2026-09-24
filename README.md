# SYNAPSE-3D: Autonomous UAV Disaster-Assessment Agent (Hack Matrix AA-32)

A neuro-symbolic drone agent that runs on an Android device in Augmented Reality. A language model (Gemini) reads unstructured free-text earthquake field reports and autonomously generates a **spatial zone map**: structural collapses, unstable leaning buildings, helicopter landing zones, and radio relays.

Deterministic C# on the edge device cross-examines the LLM's map against physical safety and communications rules, plans a strict boustrophedon (lawnmower) survey route, and executes the drone flight path in AR. Telemetry is dispatched incrementally the moment each zone is imaged.

- **Problem Statement:** Hack Matrix, Agentic AI track, AA-32: Post-Earthquake Damage-Assessment Drone Agent
- **Engine:** Unity 6000.6.0f1, URP, AR Foundation (Android / ARCore, IL2CPP)
- **Scene:** `Assets/Scenes/AA32_DroneAgent.unity`

---

## 1. Agentic Architecture & ReAct Loop (`Assets/_HackMatrix_Agent`)
- **AA-32 Vocabulary & Prompt:** Tool-calling strictly constrained to `LaunchPad`, `HazardZone`, `ShoringSite`, `RescueLZ`, and `RelayNode`. The model is instructed to produce a structural zone map, never raw flight waypoints.
- **ReAct Tool-Calling Loop:** Gemini calls `calculate_flight_path(zone_map)`. The on-device verifier and planner execute and return either `VERIFIED` with the route, or `REJECTED` detailing structural violations and required fixes. The model autonomously repairs its map and calls again (max 4 iterations).
- **Infrastructure-Denied Fallback:** If the API endpoint refuses tools or the cloud connection drops (HTTP 503), the agent instantly falls back to a deterministic on-board offline loop.

## 2. Spatial Verifier & Flight Planning
- **Symbolic Verifier (`PlanVerifier.cs`):** Enforces strict zone-map rules (bounds, mesh overlap, LZ clearance from collapses, required zones). Path rules check altitude clearance and coverage bounds.
- **Radio-Relay Connectivity:** Every `RescueLZ` must chain back to the `LaunchPad` through `RelayNodes` with a maximum hop distance of 0.50m (AR scale). Exact hop counts are calculated via breadth-first search. Isolated LZs are rejected, forcing the LLM to calculate and place relay positions.
- **Boustrophedon Sweep (`FlightPlanner.cs`):** A deterministic lawnmower sweep with lanes exactly one sensor-swath apart. The drone dynamically climbs over structures instead of detouring.
- **Incremental Dispatch:** Zones are reported the exact moment they are fully imaged. The HUD tracks wall-clock time-to-first-dispatch versus full survey completion.

## 3. AR Simulation & Visuals
- **External URP Pipeline:** High-fidelity third-party models are ingested, converted to URP Lit materials, stripped of CPU-heavy colliders, and mathematically normalized to fit the verifier's 1x1x1 bounding boxes.
- **Tactical Identification:** Destroyed and leaning buildings retain their architectural textures but feature glowing base pads (Red for Hazard, Amber for Shoring) for instant evaluator identification.
- **Simulated Classifier:** A threshold rule benchmarked on 20 synthetic cases (15/20 accuracy). Labeled strictly as simulated logic on the HUD, not a vision model.

## 4. Execution Protocol

1. Open the project in Unity 6000.6.0f1 with the Android build module active.
2. Open `Assets/Scenes/AA32_DroneAgent.unity`.
3. **API Key Injection:** The key field in `HackMatrix_Agent > AgenticLLM_Bridge` is structurally empty. Paste a valid Gemini API key for live LLM routing. Without a key, the agent defaults to the hardcoded offline zone map (HUD will display "UPLINK LOST").
4. Press Play (XR Simulation) or compile the APK for Android.
5. Internal Diagnostics: Use `Hack Matrix > AA-32 > Self-Test Offline Zone Map` and `Self-Test Relay Network` in the Editor menu.

## 5. Credits
Third-party 3D models integrated under CC BY 4.0. Modifications include URP Lit conversion and bounding-box normalization. Full details in `CREDITS.md`.
- "Stylized Low Poly Buildings Pack" by Mauio2369
- "Drone" by ROHIT3DMODELS
- "Simple Low Poly Abandoned Brick Building" by jimbogies
