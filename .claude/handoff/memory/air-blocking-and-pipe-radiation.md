---
name: air-blocking-and-pipe-radiation
description: 2026-09-25 — ISO 9613-1 air per band, a path's blocking applied once (PathEqDb), tailpipe directivity + body shadow (ExhaustRadiation); unheard
metadata:
  type: project
---

Committed 483114f and 1bf44bc on 2026-09-25, pushed. Cody has not heard these yet.

- **Air:** the old linear "muffle" was about 6x too strong: 32 dB high and 16 dB mid at 135 m. It is now `AudioPhysics.AirLossDb`, the ISO 9613-1 formula at the band centres 200/1250/8000 Hz. It is carried as `AirLowDb/MidDb/HighDb` on paths, emitters and voices. One-shots never had air before.
- **Blocking:** band gains (EqLow/Mid/High) are the WHOLE path transfer, applied once as 20log10 in `FmodAudioProvider.PathEqDb`. The old code applied blocking three ways (1-occlusion volume, band gains used as lerp weights, and an extra 40/20 dB muffle), plus a bleed floor and a +10 dB bass lift; all removed. Engine echoes no longer copy the car's blocking.
- **Tailpipe:** `ExhaustRadiation` shapes Engine.Exhaust per band. Levine-Schwinger directivity w=(ka)²/(1+(ka)²) from TailpipeDiameterMm, plus the body shadow (energy sum over routes round the car box). `VehicleProfile.ExhaustAxis` is the pipe direction; the stock car exits left. The idle lift reads the unshaped exhaust.

**How to apply:** if blocked sounds now seem too loud, or distant cars too bright, measure PathEqDb and AirLossDb first, not the old constants. Remaining acoustics flaws: the loudness compression 0.45 and the 144 dB clamp; one-shot echo re-compression; idle lift overshoot; reverb uses the listener's enclosure for every source; two diffraction formulas. See [[the-mixer-does-not-use-the-loudness-law]], [[machines-as-parts]].

**Loudness setting and pivot (2026-09-25, eeed886 and d489dbe):**
- `Loudness.DynamicRangeCompression` is a live setting (`/levels`, saved in ClientSettings, OPENFPS_LEVEL_COMPRESSION). Cody uses `/levels real` (1.0).
- It turns about `PivotDb` 70 dB (at the 1.2 m reference). `RenderCeilingDb` follows: 112 at 0.45, about 89 at 1.0.
- The engine lift is `EngineVoiceState.LiftDb`, from `Loudness.PlacedDb`.
- If "real" feels quiet again, check the pivot before any source level.
- At a 12 m/s cruise the tailpipe end dominates on nearly every vehicle (`--tap-balance parts`).
