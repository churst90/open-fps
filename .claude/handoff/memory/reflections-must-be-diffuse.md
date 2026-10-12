---
name: reflections-must-be-diffuse
description: "Engine echoes were coherent copies (phasing, laser); since 2026-09-23 night each is smeared by the wall's roughness (EchoDiffuser) and they are ON again"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 0567fe39-d6c7-4ad7-95a6-f379f6c28251
  modified: 2026-09-23T14:08:26.140Z
---

Cody (2026-09-23): reflections must be "a wash off in the distance", "not a laser beam into my ears";
sirens sounded "inside out" (phasing).

Cause: an engine echo read the engine's ring sample-for-sample, a few ms late -> direct + copy = a
comb with EXACTLY regular notches (a pitch of its own, a flanger when moving), from a pinpoint.

Fix (evening 09-23): `EchoDiffuser` (EngineProcessor.cs) — 4 Schroeder all-passes, delays scaled by
the surface's Scattering (now carried on `Reflection.Scattering`): mirror ~1.7 ms, brick ~12 ms.
Level exactly preserved; comb periodicity 0.98 -> 0.00-0.11 (EchoDiffusionTests). Reflection gain
slews over ~0.18 s, release 0.8 s. `EchoCeiling` default back to 2; `OPENFPS_ENGINE_ECHOES=0` = off.
Cody asked for this ("keep the effect"); NOT yet heard.

**Rules still true:** higher-order (2nd/3rd) discrete echoes are for ONE-OFF sounds outdoors only
(WorldAudioPlayer.QueueHigherOrderEchoes); for sustained sources the copies-of-copies are the reverb.
Never add a coherent (un-smeared) copy of a sustained source.
Related: [[a-reflection-is-the-room-answering]], [[no-ambience-beds]].
