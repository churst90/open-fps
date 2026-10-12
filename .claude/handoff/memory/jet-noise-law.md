---
name: jet-noise-law
description: Exhaust and aircraft jets share one Lighthill law since 2026-09-18; how it was found (mute the jet on every preset), and the small diesel that is still jet-heavy
metadata:
  type: project
---

`JetNoise` (Waveguide.cs) was hand-calibrated 17 dB above Lighthill with a U³ law. Found by running
`--engine-orders <p> jet=0` against default on all 28 vehicle presets: the 2.8 turbo diesel's exhaust
at 2,600–4,000 rpm was 15–20 dB of jet (`structure` 1.5 dB vs 60 muted) — the "white noise through a
pipe" complaint; V6, economy four, school bus 4 dB of it; race engines and V8s unmoved. Now
`JetNoise.LighthillPressure(D, U, K)` (K = 1e-4, density ratio for hot gas, U capped 600), band
normalised to unit RMS by `BandNormaliser(α) = 0.285√α` (measured), velocity smoothed over the mixing
time 5D/U so millisecond pulses at the lip do not make millisecond eighth-power roars. Approved presets
unchanged to 0.1 dB; structure equal or better everywhere.

**Still open:** the 2.8 diesel measures 89 dB total against 74 dB of engine at 3,960 rpm — its smoothed
exit velocity is >100 m/s through a 57 mm pipe. Check `ExitVelocity` mean/RMS against mass flow (a DC
offset in the arriving wave would pass straight into `u`) before touching the preset.

**How to apply:** when an engine reads as hiss, mute sources one at a time (`jet=0`, `port=`, `knock=`,
`valves=`, `turbo=`) and read `structure`, not the level — the level barely moved while the jet was
20 dB over the engine. See [[audio-lab-instruments]], [[synthesis-by-listening]].
