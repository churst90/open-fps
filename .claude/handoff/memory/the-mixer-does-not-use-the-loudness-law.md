---
name: the-mixer-does-not-use-the-loudness-law
description: HISTORICAL — FMOD ran LINEAR rolloff until 09-22; INVERSE is the default now (OPENFPS_ROLLOFF=linear for A/B)
metadata:
  type: project
---

Found 2026-09-21 chasing "the only thing that's loud is that police car" and "you have to be right
on the runway to hear the planes".

`Loudness.RenderedGain` says of itself that it is *"the law the mixer actually applies, in one
place"*, and it is an INVERSE law (`min/d`) with an edge fade. Every balance decision and every
audio test in the project is written against it. **The mixer applies a different law**: every
channel in `FmodAudioProvider` is created `FMOD_3D_LINEARROLLOFF` — full volume at MinDistance,
silence at Range, straight line between.

Measured on the city's own numbers, a bus (gain −26.6 dBFS, reference 9.8 m, range 2,292 m):

| distance | model (inverse) | FMOD (linear) |
|---|---|---|
| 30 m | −36.3 dBFS | −26.7 dBFS |

Linear barely attenuates anything until the listener is near the range limit. So **distance stops
carrying information**: near and far sources sit at nearly the same level and what is loud is
decided entirely by each source's *placement gain*. That is how one over-declared vehicle became
the loudest thing everywhere on a 2 km map, and why fixing declarations matters so much more than
it should.

**Switchable, not switched** — it moves every sound at once and the ear has to judge:
`OPENFPS_ROLLOFF=inverse` / `=linear` (default, unchanged). `Rolloff.Mode` in FmodAudioProvider.
My recommendation is inverse: it is the physical law, it is what the model claims, and distance is
the cue a player listening to this world needs most.

See [[live-voice-is-not-the-bench]], [[audibility-ranking]].
