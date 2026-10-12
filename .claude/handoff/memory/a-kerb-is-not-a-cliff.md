---
name: a-kerb-is-not-a-cliff
description: A 12 cm lip made the body airborne and LAND — a heavy bang, one every half second, for as long as you stood on it
metadata:
  type: project
---

`SharedMovementEngine` could step UP a full `StepHeight` and had **no allowance going down**. A lip of
twelve centimetres — a pavement edge, the end of a road surface — put the body in the air for two
ticks and then **landed** it. A landing plays out of the FOOTSTEP bank and is much heavier than a
step, and `StrideAccumulator.MinSecondsBetweenLandings` gates it to one per half second.

Standing on such a lip is worse: the ground probe samples five points around the feet, so it
straddles the edge, and any jitter — server reconciliation keeps nudging the position after you stop
— flips the answer, drops the body and lands it again. **A bang every half second for as long as you
stand there.** Reported as *"walk a few steps, stop, and for like 10 seconds, periodic bangs."*

Fixed 2026-09-19: the floor may be a full step BELOW you and still be the floor you are on, but only
if you are not already rising or falling, so walking off a roof is still walking off a roof.
`StepDownTests` holds both halves. The city map's bare ground was also made flush with the road —
the kerb stays, because a kerb is real and a body can now step off one.

**The instrument that found it is `--walk`** (`WalkSpike`): it drives the REAL movement engine and
the REAL ground probe over a real map's boxes, tick for tick, and prints every footfall, every
landing and every time the ground moved. `--walk map=city from=0,-20 to=20,-20 seconds=14 stand=6`.
Nothing in it is a model of the movement; it IS the movement, which is why it reproduced in one run
what three listening sessions could not pin down.

**Why:** a fault that is inaudible as geometry can be loud as audio. Movement bugs reach the ear
before they reach the eye in this game, so "it bangs" is often a question for the physics.
**How to apply:** when a sound fires that nothing should have triggered, walk the map with `--walk`
before touching the audio. See [[instrument-must-walk-like-the-game]], [[gait-from-the-body]].
