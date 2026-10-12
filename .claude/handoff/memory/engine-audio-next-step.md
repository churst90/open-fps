---
name: engine-audio-next-step
description: The four load-dropout faults are FIXED (session 5); the field is back to 30 and the sample-bank plan is now optional
metadata:
  type: project
---

**`docs/AUDIO_LOAD_DROPOUTS.md` is the record — diagnosis (session 4) and what was done (session 5).**
The speedway cutting out on map load was never proof that live synthesis cannot scale. Four faults
stalled the FMOD mixer thread itself, which no ring depth can absorb. All fixed 2026-09-14:

1. `EngineVoiceState`'s ring is now lock-free SPSC. `Consume` never blocks and never synthesizes —
   it takes what is there, ramps out over 64 samples, counts a starve. `Produce` claims a single
   producer slot with a non-blocking CAS. The lead is PER VOICE, grown only by that voice's own
   starvation and only after it has played for one lead. Snapshot sorted nearest-first.
2. `AggressiveOptimization` on the per-sample path (not on small helpers — an optimized caller
   inlines those, and marking them stops it). Load-condition cost 4.0x -> ~6.9x realtime; steady
   state unchanged at ~8x. Verify with `DOTNET_TC_CallCountingDelayMs=100000 --engine-cost`.
3. `BackgroundPriority.RunLowered` nices the acoustic bake to +10 and takes it off the shared pool.
   `Thread.Priority` upward is a no-op on Linux — never explain behaviour by it.
4. `GCSettings.LatencyMode = SustainedLowLatency` in `FmodAudioProvider.Initialize`.
Plus: budget held 3 s after load/spawn and 0.75 s over the ceiling before shedding;
`setDSPBufferSize(1024, 8)`; `setSoftwareChannels(128)` — the 30-car map measures 59-62 REAL
channels, so the default 64 was genuinely being hit.

`FIELD_SIZE` in `tools/gen_speedway.py` is back to **30**. Spike: 60 voices, dsp 12-16%, starves
only in the first two seconds.

**Why:** the owner needs a stress-test track and city maps with many sources to load clean.
**How to apply:** the Mixer load line now carries `real/playing channels, N starve(s), gc N gen2 /
N ms paused` as deltas, and drops from 5 s to 1 s whenever either is non-zero. What is left is to
confirm it in the REAL client (the spike has no map load, no bake, no GC pressure) against an
`OPENFPS_AUDIO_CAPTURE` WAV. Pre-rendered sample banks are now an optional choice for a field of a
hundred, not a forced fix. See [[engine-render-pool]], [[speedway-map]].
