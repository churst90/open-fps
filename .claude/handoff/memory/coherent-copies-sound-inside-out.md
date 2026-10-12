---
name: coherent-copies-sound-inside-out
description: 09-24 — "passing inside out / phased" = the same signal from two places; tyre noise shared by both taps, and facade echoes added by the nearest-face search (engines reverted to centre search)
metadata:
  type: project
---

Cody (09-24, city at (60,122), (-190,-99), (-226,-101)): cars and the train passing "sound inside out,
phased", "like passing behind me". It appeared right after 2353df0 (faces found by nearest part).

Causes found (25d73ac):
1. The front and rear engine taps carried the SAME tyre signal (`halfTyre` into both) — one roar from
   two places 3-4 m apart combs against itself as the car passes. Now independent noise per axle
   (`_tyreFront`), power kept (0.6*sqrt2 each).
2. Nearest-face surface search gave every car echoes off long facades that had been out of range —
   coherent copies of the engine. Engines back on centre search (`FirstOrderNear(nearestPart:false)`);
   one-off sounds keep nearest-part (ground wash).
Also 9d06bec: echo air absorption was the car's, not the echo path's (echo brighter than the car).

**How to apply:** any time one signal is emitted from two positions, it WILL comb and sound inside
out when the geometry sweeps. Decorrelate (independent noise, different components) or don't split.
Engine echoes are still coherent copies (all-pass diffuser only) — the open question if phasing returns.
See [[reflections-must-be-diffuse]], [[machines-as-parts]], [[distant-voices-had-no-path]].
