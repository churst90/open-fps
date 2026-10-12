---
name: no-special-cases
description: Audio behaviour must fall out of materials and geometry, never out of per-map or per-entity-type special cases
metadata:
  type: feedback
---

Cody rejected an `EchoCarLimit` constant that capped how many CARS could have reflections:

> "it sounds like we've made a specific variable that should be being done by the loudness of the
> vehicles and the effect they have bouncing the sound off the walls, we shouldn't need to make
> special cases. What we do on this map to get things working we need to generalize... cars are
> loud, so car echo limit shouldn't be a special case for what should already be what sound should
> do in the audio engine."

**Why:** a rule about cars is right on the speedway and silently wrong on the next map. What a thing
is made of and where it is should produce the effect on their own; the engine should not know what a
car is. Budgets are allowed when they are a REAL resource (voices, CPU), but they must be spent by
ranking on physical audibility, not by counting objects of a type.

**How to apply:** replaced with `EngineReflections.MaxReflectionVoices` (a voice budget) plus an
`AudibilityFloor` recomputed each frame as the Nth loudest candidate reflection across the whole map:
`direct.Volume * surfaceGain * minDistance / pathLength`. A loud car by a hard wall keeps several; a
quiet distant one off a soft wall gets none — because nobody can hear it, not because it was counted.

**His vocabulary:** when Cody says "volume" or "loud" about this engine he means RELATIVE loudness and
its consequences (a louder source reflects more, and its reflections are heard sooner and from
further), not a calibrated dB SPL figure. Don't correct him toward dB; he is describing the effect.

**Demo value:** a racetrack full of loud cars is a good showcase for reflections — keep it as a test
case, but never let the map's needs become the engine's rules. See [[speedway-map]].
