# LUMINARA — One-Page GDD

**Team Nandha - ML** · Funobotz Discovery World, Phase 3
**Theme #2:** Adventure Quest World · **Category #4:** Engineering Puzzle Game
**Platform:** Android ARM64 handheld AR · **Build:** `Matrix_Test.apk`, 35.4 MB, arm64-v8a, IL2CPP

---

## Concept

Aethyra's light has failed. Its machines sit dark, and the only diagnostic tool left is **Petalo**, a
light-signaling drone whose body is a swarm of luminous micro-agents. The player wakes Petalo by
placing a physical card on a real table, then repairs Aethyra's structures by moving and turning
cards in front of the phone.

The puzzle is an engineering one: **position and orientation carry the answer.** Where a card sits,
how close it is to another card, and which way it faces are the inputs. The swarm is the readout.
Nothing is solved from a menu; the player solves it with their hands on a table.

## Core Playable Loop (proved in this build)

| Step | Player action | System response |
|---|---|---|
| **1. Scan** | Points the phone at the table | ARCore image tracking acquires the Funobotz cards and wakes Petalo on the anchor card |
| **2. Discover** | Finds the dormant node | Petalo's swarm hangs loose and cyan over the anchor: unpowered, no route |
| **3. Link** | Slides the conduit card toward the anchor | Inside the link radius the swarm stops orbiting and stretches to span both cards |
| **4. Rotate** | Turns the conduit card | Yaw is measured against the anchor's normal; while off-angle the swarm stays cyan and unsettled, which is the hint |
| **5. Ignite** | Holds the correct angle | The swarm converges into a fast stream and its HDR emission drives from cyan to gold through URP Bloom; the `LumenforgeIgnited` event fires |

The loop is repeatable and reversible. Breaking the link or turning the card away returns the swarm
to cyan; losing a card to occlusion pauses the swarm rather than failing the player.

## Diegetic UI

LUMINARA has **no flat gameplay HUD**. Every piece of state is carried by the two physical cards and
the swarm between them:

- **Where you are in the puzzle** — swarm shape: a loose cloud (dormant), a stretched bridge (linked,
  wrong angle), a tight stream (solved).
- **Whether you are right** — colour: cyan while unsolved, gold once ignited, interpolated so the
  player sees the answer arrive rather than being told.
- **Whether the system can see you** — the swarm pauses and disappears when tracking is lost, which
  reads immediately as "move your hand".
- **Which card is which** — each reference image spawns its own hologram, with Petalo's cutout on the
  anchor card.

Flat interface is reserved for the moments that are not play: the start screen and the completion
confirmation. During play the phone is a window, not a control panel. Feedback is never colour alone:
density, motion and shape carry the same signal for colour-blind players.

## Why it fits the brief

- **Adventure Quest World.** The tabletop is a window into Aethyra: the player crosses the Grand
  Gateway and the Rainbow Bridge to reach the Mystery Cave, where the Lumenforge waits.
- **Engineering Puzzle Game.** The player reasons about proximity, alignment and flow, then tests a
  physical hypothesis and reads a measurable result.
- **Ages 10–14.** One objective at a time, no reading during play, and a failure state that hints
  rather than punishes.
