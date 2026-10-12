---
name: engine-synthesis-state
description: Where the physical engine synthesis stands after the 2026-09-14 rewrite and which instruments to reach for
metadata:
  type: project
---

On 2026-09-14 the vehicle engine was rewritten as a physical model (cylinders + valve-flow boundaries + real-unit waveguides + lumped plenum) in `OpenFPS.Client.Core/AudioEngine/Core/Engine/`, with a general `EngineProfile` (`OpenFPS.Common/Engines.cs`, 14 presets) and a live FMOD DSP (`EngineProcessor`). Findings and bugs are in `docs/ENGINE_SYNTHESIS.md`.

Session 3 (2026-09-14) added `nascar_v8` and `f1_v10`, and fixed the long-standing "high-revving engines are 10 dB too loud and top-heavy" bug by capping radiation efficiency at ka=1 in `OpenEnd.Radiate`. `--engine-cost` reports what a voice costs a core.

**Why:** the owner's complaint was "fart in a PVC pipe / under a pillow"; the previous drawn-pulse model could not be fixed by tuning.
**2026-09-18:** tailpipes radiate from their own positions (`TailpipeExitsMetres` + `SetListener`), the
shock front is an equal-area merge in `WaveLine`, every jet is Lighthill's (`JetNoise.LighthillPressure`),
and aircraft exist (`AircraftSynth`, not in the game). Session commits `824a907` and the one after it.

**How to apply:** when a sound is wrong, run `--engine-trace <preset>` first (state per quarter second: rpm, MAP, torque, burn quality, dilution, port peak, levels) and `--engine-orders` second; do not tune constants against words. Listening verdicts from the owner on the new model were still pending at the end of that session — ask before assuming it is right. See [[build-on-tmpfs]] and [[speedway-map]].
