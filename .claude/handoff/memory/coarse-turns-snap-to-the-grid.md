---
name: coarse-turns-snap-to-the-grid
description: J/K/L/O tap now goes to the next 45-degree mark rather than adding 45 to an off-angle heading — so walking straight moves exactly one coordinate
metadata:
  type: project
---

Cody 2026-09-21: *"if I press j or l to go facing north and I walk straight, both the x and the y
change when they shouldn't."*

A coarse tap ADDED `TurnStepDegrees` (45) to the current yaw. Once a fine nudge (shift, 1 degree) or
a held sweep had left the heading at, say, 47 degrees, every coarse tap after it landed on 92, 137,
182 — off the grid for ever. Walking then moved both coordinates, and the one thing the key exists
for became impossible.

`ClientGameSession.SnapDegrees` now returns the distance to the next multiple of 45 IN THE
DIRECTION PRESSED — a whole step when already on a mark (within 0.25 degrees), less than one when
not. Shift is unchanged: 1 degree, deliberately off-grid, for lining something up by ear. Applies
to pitch (K/O) on the same rule.

**The sign conventions are the trap** and each is individually plausible: the physics does
`Yaw -= LookDelta.X * ...` so a POSITIVE x (J) DECREASES yaw, and `Pitch += LookDelta.Y * ...` so a
positive y (K) increases pitch. `SnapDegrees` returns a positive magnitude and the key's own axis
sign carries the direction.

`TurnSnapTests` holds it: eight taps from an off-angle start, and after every one the forward
vector is either axis-aligned or a true diagonal.

See [[screen-reader-keys-are-not-game-keys]], [[cody-taps-movement-keys]].
