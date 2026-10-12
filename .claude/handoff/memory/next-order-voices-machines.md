---
name: next-order-voices-machines
description: "The agreed order of work after session 9 (superseded in part by city-map-plan, 2026-09-19): parts-as-data, audibility ranking, aggregation, then the city map and aircraft"
metadata:
  type: project
---

Agreed with Cody at the end of 2026-09-16 (session 9). Steps 1 and 2 are done; **step 3 is next**. The argument,
the numbers and the acceptance checks are in **`docs/VOICES_MACHINES_AND_THE_CITY.md`**; `todo.md` ends
with the index. His specification in one sentence: *"this stuff needs to be done without the user
thinking about it"* — a map author places a tree, a fountain or a racetrack and the acoustics follow.

1. ~~**Parts as data.**~~ **DONE session 10 (commit `cfb09eb`)**, including the two-voice car — see
   [[machines-as-parts]]. Next session starts at step 2.
2. ~~**Extent on every emitter + audibility ranking.**~~ **DONE session 10 (commit `ca7b193`)** — see
   [[audibility-ranking]]. Next session starts at step 3 (aggregation), unless the diesel source-level
   question in todo.md comes first.
3. **Aggregation** — many like sources collapse into one extended source past the distance nobody can
   tell them apart. The crowd is the built example. This is what makes a city fit in 96 HRTF slots.
4. **City map** — materials (brick, foliage, water) before geometry; traffic AI does not exist
   (`VehicleSystem` follows a racing line); NPC speech waits on recordings.
5. **Aircraft** — jet mixing noise as v^8, rotor blade-pass at 10-25 Hz with blade-vortex thump.

**The ceilings that matter:** FMOD 256 real / 512 virtual channels, but the binding one is
`FmodAudioProvider.SaPoolSize` = 96 HRTF voices, and behind it DSP time. Do not let FMOD do the
rationing — a virtualised SYNTHESISED engine jumps in pitch when revived.

See [[a-source-has-a-size]], [[a-surface-scatters]], [[no-special-cases]].
