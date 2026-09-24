# LUMINARA — 5-Slide Pitch Draft

**Team Nandha - ML** · Funobotz Discovery World, Phase 3

Speaker notes are the indented lines. Target: about 20 seconds per slide, 100 seconds total.

---

## Slide 1 — The Problem

### Mobile puzzles ask children to think, but never to *move*

- Tap, drag, swipe: the whole game lives under a thumb on a flat pane of glass.
- Spatial reasoning (where a thing sits, which way it faces, how far apart two parts are) is the
  foundation of engineering thinking, and a touchscreen trains almost none of it.
- Screen-space tutorials fill the gap with words, which is the opposite of how a 10-year-old learns
  a mechanism.

> Ask a child to explain a lever and they reach for one. Mobile puzzle games never let them.

**Visual:** split image. Left: a finger dragging a 2D puzzle piece. Right: two hands turning a
physical card on a table with a phone above it.

---

## Slide 2 — The LUMINARA Solution

### A puzzle you solve with your hands, answered by light

- **Tangible input.** Printed Funobotz cards on a real table are the controls. Position, proximity
  and rotation are the verbs.
- **Diegetic output.** Petalo's GPU swarm is the entire interface: shape shows the state, colour
  shows correctness, absence shows lost tracking.
- **Zero flat HUD during play.** No menus, no instruction text, no on-screen buttons. The phone is a
  window into Aethyra, not a control panel.
- **Failure teaches.** A wrong angle produces a hint (scattered cyan), never a penalty; picking a
  card up costs nothing.

**Visual:** the 90-second capture, paused at the cyan-to-gold transition.

---

## Slide 3 — Core Engineering

### Physical geometry becomes compute-shader state

- **Proximity** — the distance between the anchor and conduit cards is evaluated every frame with a
  squared-magnitude test, with hysteresis so a shaky hand cannot flicker the link.
- **Yaw** — the signed angle between the two cards around the anchor's surface normal, held for a
  short dwell so a sweep through the angle does not count as a solution.
- **These two numbers drive the simulation directly:** they retune the boids' separation, alignment
  and cohesion weights, move and stretch the simulation volume to span both cards, and interpolate
  the swarm's HDR emission from cyan to gold.
- **The result is a closed loop with no UI in the middle:** hands move → compute parameters change →
  15 ms later the light in front of the player changes.

**Visual:** three stills of the swarm (loose cloud, stretched bridge, converged gold stream) under
labels *Dormant / Linked / Ignited*.

---

## Slide 4 — The Tech Stack

### Built lean for mid-range Android

| | |
|---|---|
| **Engine** | Unity 6000.6.0f1, URP 17.6, HDR |
| **AR** | AR Foundation 6.6.2, ARCore XR Plugin 6.6.2, 5 tracked images |
| **Swarm** | Custom compute pipeline: spatial hash grid, bitonic sort, indirect draw; no per-agent GameObjects, no CPU readback |
| **Build** | Android ARM64, IL2CPP, min API 27, 35.4 MB APK |

- **Lighting cost held flat:** no point or spot lights. The glow is HDR emission plus one bloom pass.
- **Memory is not the constraint:** about 0.14 MB of GPU buffers at the shipped profile, about
  2.0 MB at the 15,000-agent target.
- **Fails safe:** devices that cannot read structured buffers in the vertex stage are detected at
  start-up and run the game without the swarm rather than crashing.

**Visual:** the scene-structure snap (hierarchy plus systems).

---

## Slide 5 — The Road to Phase 4

### From vertical slice to the full expo build

| Priority | Work | Outcome |
|---|---|---|
| 1 | Lumenforge ignition sequence | Forge model, crystal reveal and progression event replace the current swarm-only completion |
| 2 | Act 1 Gateway and Act 2 Bridge | Build the two deferred zones on the documented layouts; multi-card linking becomes the bridge puzzle |
| 3 | Scale the swarm | Vulkan path, retuned neighbour cap, and a measured climb from 1,024 toward 15,000 agents |
| 4 | Playtest with 10–14s | Five first-time players; convert observations into one design change before the expo |
| 5 | Art and audio pass | Replace placeholder holograms; add the forge's ignition sound |

> We have proved the hardest part: a physical action on a table drives a GPU swarm that reads as
> light, on a mid-range phone. Phase 4 is the world around it.

**Visual:** the three-zone top-down map with Act 3 marked *built* and Acts 1–2 marked *next*.
