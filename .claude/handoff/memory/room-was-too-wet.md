---
name: room-was-too-wet
description: 09-29 — "bathroom stall" flat: traced HRTF frame mismatch, double send, send not scaled by distance; --clap-room measures it; doors are NOT in the SA scene dynamically
metadata:
  type: project
---

Cody: "/tp -14 -85 0.5, clap, sounds like a bathroom stall, not a carpeted room".

**Instrument:** `--clap-room` (AudioLab) plays a clap through the whole FMOD chain in a model of flat
01F (traced listener, sends, decorrelator, limiter) and prints the room's energy against the clap
(0-6 ms). It was +1.0 dB where physics says about -10. The captures were +3..+5.

**Causes, each measured:**
- The traced decode used `_saHrtf`, made for a 1024 frame, at the 256 sub-frame. That was about
  +3.5 dB and the wrong colour. Now `_saHrtfTraced`. Offline check:
  `SA_HRTF_FRAME=1024 --traced-reverb`.
- Double send: own and cross into the same unit, both at unity in traced mode. Now crossMix is 0
  when the two buses are the same handle.
- The send was the arrival, but the IR is normalised to 1 m. It is now × min(d,1)·max(d,1)^enclosure
  for the listener's room. After all three: -4.6 dB with the limiter, -6.6 without.
- The limiter is only about 2 dB of it. The ear decorrelator was a separate earlier fault
  ([[decorrelator-was-a-reverb]]).

**Not faults:**
- The traced IR level: the flat decodes at -4 dB re a 1 m impulse, and the room equation agrees.
- Every sound's reference distance is at least 1.2 m (flat inside it). That is by design.

**Still open:**
- In the listener's own room, the bus-head HRTF voice (`_reverbSaVoices`) blends in an HRTF copy
  of the traced stereo, weighted by the survey anisotropy. That is double spatialisation left over
  from room mode.
- Early reflections plus traced tail cannot be done by cutting the SA IR: `IPLReflectionEffectIR` is
  opaque in this SDK. It would need our own convolution. See [[traced-reverb]].
- Doors: SteamAudioScene is built once at map load with every leaf shut, and never updated. An open
  door is still a wall to occlusion and the trace. It needs instanced meshes, which are not bound yet.

**Walkers heard through the wall:** the one-shot's first path came from the hand-rolled
SpatialAcoustics (a route through the flat's door, -4 dB), not from SA (-24 dB through brick). Fixed
with AsyncAcousticWorker.TryGetNearby, fed to WorldAudioPlayer.Worker.

**Round 2, 09-29 02:05.** Cody: "still narrow, nothing moves when I turn, fluttery like waves,
outside rumble clips".
- The clipping was my regression: the atDistance scaling was also applied to crossMix, so outdoor
  traffic went into the flat's reverb about 19 dB hot and the mix ran at -8 LUFS. Now it applies to
  ownMix only when the source is in the listener's region.
- Narrow: the capture's IACC was 0.85-0.95. Built early reflections + tail:
  - WorldAudioPlayer.QueueRoomEchoes: EarlyReflections order <=3, keep 24, <=80 ms, 12 voices; the
    first-order floor is skipped (the voice has its own ground).
  - The listener's enclosed room stage uses TracedReverbState.TailEffect, the SA PARAMETRIC effect;
    the simulator is now HYBRID for its reverbTimes.
  - The parametric output is W only and silent for the first 50 ms. Its level matches the
    convolution late part to about 1 dB (SA_PARAMETRIC=1 --traced-reverb).
  - Test: RoomEchoTests.
  - Unheard.
- The traced-stage effects are never released on bus teardown. That leak predates this change.

**Round 3, 09-29 02:20.**
- Room echoes were a frame late (0.02-0.05 s after due): the _pending loop walks backwards and
  echoes are appended during the pass. The loop now repeats up to 3 passes.
- Walkers through brick: the barrier detour of 156 m was capped at Diffraction.MaxInsertionLossDb
  (24) and beat transmission. The worker now multiplies by dist/(dist+delta).
  `--path-probe ear= src=` shows what the worker hands the mixer; `OPENFPS_AUDIO_DEBUG=1` prints
  [SAPATH] raw vis/trans/barrierDelta.
- Still masked:
  - Brick transmission (-24/-30/-36) is per material, not per thickness. A 35 cm wall should be
    about -50. Mass law from DensityKgM3 × box thickness, except porous materials (Fence, Foliage).
  - The barrier search measures one box at a time.
  - OcclusionCap 0.95 floors the occlusion share at -26 dB.
- "Heavy echoes on the left no matter how I turn": no L/R bias in the capture (±2 dB every window,
  every heading). Only bus 87 was audible, with head blend 0.04-0.09. Unexplained; re-measure after
  the timing fix.

**Round 4, 09-29 02:35 — the fundamental faults, all measured.**
- Copies went through Loudness.Place with their lowered LevelDb, which keeps 45 % of level
  differences, so every echo was 4-8 dB too hot. Now Pending.CopyGain/SourceLevelDb and
  placement as the source. This is likely why "reflections of reflections were too much".
- EarDecorrelator: the right chain's delays summed 32 samples more than the left's, the tail led on
  the left ear by 0.7 ms, and the room sat on the left. Both now sum to 260. Test:
  NeitherEarHearsTheRoomFirst.
- Wall TL: AcousticRegistry.MassLawTransmission(material, thickness) with MaterialProperties.Porous.
  The per-face value is the sqrt, and numTransmissionRays = 8. SA output appears to floor at about
  0.009 (-41 dB), unexplained.
- Barrier route: only when verified; otherwise the SA pathing eq, else transmission.
- gen_city: flat doorways had no lintel and a 0.9 leaf in a 1.0 opening. Fixed; city.json
  regenerated (6034 -> 6404 entities, so region ids may have shifted).
- REGRESSION I caused and fixed at 02:45: the SA pathing eqCoeffs used as a level is NOT the loss
  (about 1.0 on a 150 m route). Blocked sirens and walkers played at full level from below, head-locked.
  Unverified routes now give transmission only with the source's own bearing, and no probe
  redirect. `--path-probe` waits for PathingReady; before that it measured an engine without pathing.

**Round 5, 09-29 03:30:** see [[soundfield-faced-backwards]] — the mirror, the far walls, others' steps, and the room's real size.
