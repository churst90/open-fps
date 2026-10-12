---
name: soft-ceiling-and-muffler-steepening
description: 09-24 — the engine soft clipper jumped 0.8->0.664 at its knee (a click per backfire); mufflers ignored Steepening; bikes never reach full load on --voice-levels
metadata:
  type: project
---

Found 2026-09-24 in the cleansing pass. APPROVED by ear 09-24: "the mufflers and doors do sound better, especially the mufflers, noticeably better" (renders in
inbox/mufflers-2026-09-24 and doors-2026-09-24).

- **Muffler sections now steepen with the profile's `Steepening`** (chambers and passages were 0,
  the packed tube 0.3). Fleet change on `--voice-levels`: at most +0.6 dB (i4_economy, v6); bands move
  by 3 dB at most. Do NOT divide by ExpansionRatio: the law is amplitude-dependent and the chamber's
  area already drops the pressure.
- **`SoftCeiling.Apply`** (EngineProcessor.cs) replaced `|y|>0.8 ? tanh(y) : y` in both
  EngineVoiceState and PhysicalVoiceState. The old one was DISCONTINUOUS: 0.8 -> 0.664 at the knee, a
  one-sample step on both edges of every backfire and pop. Now continuous, slope-matched, ceiling
  +2 dB over full scale (level-neutral: placement is SourceLevelDb only; the mix is float and ends
  in the master limiter). The +2 dB is for the 450 single, whose 99.9th percentile is 1.6 dB over
  full scale once its glasspack steepens.
- **`single` and `sportbike` show NaN on `--voice-levels`**: "never reached full load". An
  instrument gap, not NaN audio — but it means the bikes' live level has never been measured. For
  the vehicle audit (step 5). See [[live-voice-is-not-the-bench]], [[audio-lab-instruments]].

**Vehicle audit, first findings (09-24, same session):**
- Bikes read NaN on --voice-levels because VirtualDriver has NO KICKDOWN: floored from 3rd at 3,300
  rpm it never changes down (single) — and sportbike cannot reach its own 13,800 upshift at all.
- Fleet check (all presets floored): only `v10` (gearbox UpshiftRpm 8200 on a 6200 redline — a
  copied gearbox; FIXED to 5900, test EveryGearboxChangesUpBelowItsEnginesRedline; drive renders in
  inbox/v10-shift-2026-09-24, APPROVED by ear, committed) and `sportbike` fail.
- Sportbike friction 12.5+5/krpm = 8.5 bar FMEP at peak (fleet 2-4) was raised to calm an idle flare.
  Setting it physical (6+1.8/krpm) exposes the real fault: the ENGINE MAKES NET TORQUE WITH THE
  THROTTLE SHUT at high rpm (holds 8,450 unloaded at 0.03 throttle, MAP 0.03 bar). Kickdown and the
  friction change were REVERTED until that is found. Dyno: Driveline.DynoRpm; trace: --engine-trace.
