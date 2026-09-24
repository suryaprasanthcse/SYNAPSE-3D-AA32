# Known Limitations

**Team Nandha - ML** · LUMINARA · Funobotz Discovery World, Phase 3

This build is a vertical slice. It proves one complete loop end to end on a device and deliberately
defers everything that does not serve that proof.

---

## 1. Scope: one act, not three

**Act 1 (The Grand Gateway) and Act 2 (The Rainbow Bridge) exist as layout and design only.** Their
environment geometry, puzzle logic and progression gates are deferred. The vertical slice implements
the **Act 3 Mystery Cave core loop**: wake Petalo, link the conduit, align it, ignite.

The world map, zone dimensions and mechanic ramp for all three acts are documented, so the deferred
acts are a build task rather than an open design question. The Mystery Cave diorama exists as a
separate lighting and art reference scene and is not part of the shipped AR loop.

**The Lumenforge ignition is currently a swarm-state change, not a set-piece.** Completion is
signalled by the swarm converging and shifting cyan to gold. The forge model, the crystal reveal and
the progression event are Phase 4 work.

## 2. AR tracking needs line of sight

The loop is driven by ARCore image tracking, which requires the printed cards to be visible to the
camera. Consequences observed in testing:

- **Hands occlude cards during rotation.** The player's fingers naturally cover part of a card while
  turning it, which can drop the tracking state from `Tracking` to `Limited` and lose the pose.
- **Our handling is a pause, not a failure.** When tracking degrades, the swarm freezes in place and
  spawned content hides rather than teleporting to a stale pose. Play resumes the moment the card is
  visible again, with no loss of progress.
- **Practical guidance for players and judges:** hold cards by the edges or a corner, keep the phone
  20–40 cm from the table, and avoid strong direct glare on the card faces.
- **Recovery is not instant.** Re-acquisition after heavy occlusion typically takes a fraction of a
  second, which is visible as a brief gap in the swarm.

## 3. GPU swarm is capped at 1,024 agents

The shipped tabletop profile runs **1,024 agents**, not the 15,000-agent target described in our
Phase 1 design. Two separate reasons, which should not be conflated:

- **Thermal and frame-rate headroom.** 1,024 agents with a 30 cm simulation volume leaves the frame
  budget dominated by ARCore and the camera feed, which keeps sustained frame rate stable on
  mid-range chips. The architecture scales: buffers are about 0.14 MB at 1,024 and about 2.0 MB at
  15,000, with dispatches rising from 7 to 25 per frame.
- **A hard device capability limit, separate from the cap.** The swarm's render shader reads a
  structured buffer in its **vertex stage**. Under OpenGL ES, a number of mid-range GPUs (commonly
  Mali) expose **zero vertex-stage buffer slots**, which makes the swarm impossible to draw at any
  agent count. `ARBoidBridge` detects this at start-up and disables the swarm cleanly with a logged
  reason instead of rendering nothing or crashing. The fix for Phase 4 is enabling Vulkan for
  compute-capable devices, keeping OpenGL ES as a fallback path.

Scaling toward 15,000 requires the neighbour cap and volume size to be retuned so the update cost
stays linear, plus a device matrix measurement pass.

## 4. Demonstration tuning in this build

For the recorded demonstration the puzzle thresholds are loosened so hand occlusion cannot stall the
capture:

| Parameter | Design value | This build |
|---|---|---|
| Link distance | 0.10 m | **0.50 m** |
| Yaw tolerance | ±15° | **±90°** |
| Alignment hold | 0.35 s | **0.05 s** |

At ±90° roughly half of all card rotations count as aligned, so the intermediate "linked but
misaligned" hint state appears only briefly. The shipped design values are retained in code as
constants and the build logs a warning when the loosened tuning is active.

## 5. Not yet validated

- **No player playtest has been run.** The usability plan (5 first-time players, aged 10–14) is
  written but unexecuted, so all child-usability claims are design intent.
- **Device performance is not yet formally measured.** Frame time, GPU time and thermal behaviour
  have not been captured on a device matrix; the telemetry overlay exists for that purpose.
- **Audio is absent**, and the hologram prefabs for the four non-anchor cards are primitives standing
  in for final art.
