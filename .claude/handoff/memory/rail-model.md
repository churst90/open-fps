---
name: rail-model
description: The train/rail synthesis built 2026-09-18 — what is anchored, what was approved by ear, and what is not in the game yet
metadata:
  type: project
---

Built 2026-09-18, commit `224ad05`. `OpenFPS.Common/Trains.cs` (wheelsets, track, traction, five
presets: amtrak / freight / steam / light_rail / metro), `AudioEngine/Core/Rail/` (`BogieVoice`,
`TrackResponse`, `ElectricDrive`, `SteamFrontEnd`, `TrainSynth`), `docs/TRAINS.md`, 19 tests in
`RailAndSignalTests`. New engine presets `ge_7fdl16` (4-stroke V16) and `emd_645e3` (**two-stroke**
V16 — `Strokes = 2` works in EngineSynth; the cam-angle convention wraps correctly for a 360° cycle).

**The rules that must not be undone:**

- **A train is a line of sources**, one per BOGIE (axles of a bogie share a rail and a sleeper bay).
  That is what makes the level plateau instead of peak, the clatter sweep, and the far end of a long
  freight duller than the near end. Measured: 7.5 m → 25 m costs 6 dB, not 10.
- **`RollingReferenceDb` is anchored against a PASS-BY, not against one axle.** Deriving it by
  extrapolating a single axle to 1 m and back put it 12 dB light. 104 dB/axle at 1 m at 100 km/h
  gives 82 dB at 7.5 m at 80 km/h, which is the figure that actually exists.
- **The speed law is not declared** — it falls out of the roughness exponent, the contact filter
  and the radiation efficiency, and measures 28·log10(V) against the real 30. If it drifts, one of
  those three is wrong; do not add a speed term.
- Tread braking is +9 dB and lives on `WheelsetSpec.TreadBraked` — a property of the BRAKE, per
  [[no-special-cases]]. There is no "freight is louder" rule.

**Instrument bugs that cost real time** (all now commented at the site):
the pass-by level meter AVERAGED each source's contribution instead of summing (read 20 dB low);
`Calibrate()` normalised the axle-count gain away; the squeal had a gain factor outside its feedback
loop so every wheel squealed equally however well damped; `Spectrum.BandsDb` on a pass-by analyses
the first 4096 samples, which is the train still a mile away — use `AverageBandsDb` with offsets.

**Not in the game.** No emitter, no machine parts, no route. Order of work is in the doc: parts
`bogie`/`rail`/`traction`/`chime` on `MachineDefinition`, a `RailVoiceState` on the render pool with
`SetListener`, a server `RailSystem`. See [[signals-and-air]], [[machines-as-parts]],
[[audio-lab-instruments]].
