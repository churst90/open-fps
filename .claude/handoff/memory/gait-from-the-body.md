---
name: gait-from-the-body
description: Step length comes from speed by Alexander's relation; the game's "walk" is 4.5 m/s, a jog
metadata:
  type: project
---

`StrideAccumulator.StepLength(speed)` — dynamic similarity, **stride/L = 2.3 (v²/gL)^0.3**, with
L = 0.95 m of leg. Not a constant, because a leg is a pendulum: cadence saturates near four a second
and everything above a walk is bought with a longer step.

| speed | step | cadence |
|---|---|---|
| 1.4 m/s (a real walk) | 0.69 m | 2.0 /s |
| **4.5 m/s (`PhysicsConstants.WalkSpeed`, holding W)** | 1.38 m | 3.3 /s |
| 7.2 m/s (sprint, shift) | 1.82 m | 3.9 /s |

It was a flat 0.5 m, which made this game's walk **nine footfalls a second** and its sprint fourteen
— heard as *"sounds like cockroaches running"*, and, with 0.3 s samples and a 390 ms tail each, as
*"echoes of footsteps"*: three and a half steps and three tails overlapping at all times.

**The open question is the SPEED, not the gait.** 4.5 m/s is a jog, so holding W honestly gives a jog
cadence. Making it sound like walking means bringing `PhysicsConstants.WalkSpeed` down — a gameplay
change (how long it takes to cross a map) that is Cody's call, not one to make quietly.

`Loudness.FootstepDb` is **68**, derived through `PanelAcoustics.ImpactReferenceDb` from ~2 J of foot
and shank less ~10 dB for the sole and the floor. It was 55 — quieter than a dropped cartridge case.

See [[cody-taps-movement-keys]], [[instrument-must-walk-like-the-game]], [[synthesis-failures]].
