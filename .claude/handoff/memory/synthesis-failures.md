---
name: synthesis-failures
description: "Footstep synth: rejected by ear, then calibrated against a real recording from 32 dB off to 10; and the measurement bug that misdiagnosed it"
metadata:
  type: project
---

Session 10, commit `6efc594`. Cody's verdict on the first footstep renders: **"none of them pass"**,
and gravel **"sounds like walking on broken glass"**. He chose to keep using samples.

**FIRST I MISDIAGNOSED IT WITH A BROKEN INSTRUMENT.** My band analysis decimated inside its own DFT,
which aliases everything above sr/8 back down — so "the synth peaks at 8-16 kHz" was an artifact. A
measurement that has not itself been checked is not evidence. `OpenFPS.Common/Spectrum.cs` now does it
properly (real FFT, Hann window) and carries that warning.

**Measured properly against Cody's own recording** (`inbox/foot steps sounds/`, split with
`tools/split_footsteps.py`, 46 clean concrete steps): a real footstep peaks at 1-2 kHz and 30-60 Hz is
its QUIETEST band. The synth was +18 dB at 30-60 and -10 at 125-250. Three missing mechanisms, all
found by measuring, none findable by listening:

1. **Radiation efficiency (ka)².** The model computed the force right and radiated it as though a
   sole were a perfect loudspeaker. The radiator is the SOLE (9 cm), **not** the Hertzian contact
   patch (7 mm) — confusing those cost 50 dB.
2. **Two paths out.** The sole pays (ka)²; the GROUND does not, because a point load on a stiff plate
   spreads over a bending wavelength (metres of concrete at 100 Hz). Why you hear someone upstairs
   but not the grit under their shoes.
3. **A third contact scale.** Heel (35 mm, 43 Hz) and grit (0.45 mm, 3.4 kHz) had nothing between;
   the sole's tread blocks (8 mm) land at ~200 Hz, exactly where the hole was. And the bulk contact
   is the ENVELOPE of the tread contacts, not a source beside them.

Worst band 32 dB → **9.9 dB**. `FootstepTests.TheModelMatchesTheShapeOfARealFootstep` ratchets at 11.

**Rules learned:** measure band balance against a real reference BEFORE playing anything to Cody; he
rejects the whole thing (correctly) otherwise. Fitted constants are named and visible
(`GroundCoupling`, `ThumpShare`, `TreadShare`, `ShoeRingShare`) — re-fit, never nudge. And when a test
that used to pass starts failing, record it skipped with the numbers rather than weakening it
(`SoftGroundIsQuieterThanHardGround` is failing now: grass and carpet measure louder than concrete).

Kept and worth keeping: the mechanisms (Hertzian contact where the softer material decides; grain
contact ∝ size, so grit lands two decades above the bulk impact; floor as a PanelAcoustics panel; a
heap as many small events) and the five new materials — Gravel, Rubber, Leather, BootRubber, Skin.
`--footsteps`, `--footsteps table`. NOT wired into the game; the client still plays samples.

See [[synthesis-by-listening]], [[audio-lab-instruments]], [[no-special-cases]].

**09-28, footsteps again (from Cody's Foley packs):** three synth attempts, all failed by ear:
(1) band-envelope resynthesis of real steps — "watery" (26 narrow noise bands, smeared attack: rise
1.5 ms vs real 0.4, crest 10.5 vs 13.5 dB); (2) contacts + minimum-phase click + long noise decay +
scrape fitted to the recordings' tails — "snare drum", "fake white noise between heel and toe", "no
depth" (the recordings' ROOM was fitted into the dry step; no body/weight); (3) grain shower for
gravel/sand/snow — "static, grains too far apart, all three sound the same". Lessons: never fit a dry
source to a recording's reverberant tail; band-noise vocoding sounds watery; one generic grain process
cannot tell gravel from sand from snow — each needs its own mechanism. Octave-band metrics passed
while the ear failed every time: the metrics were not measuring what he hears.
Research done 09-28: docs/FOOTSTEP_SYNTHESIS_RESEARCH.md (Turchet 2016, Cook PhISEM 2002, Avanzini
Hunt-Crossley, Visell 2009 dF/dt grain rate). Recognition of synthesised loose surfaces is 25-40 %
even in the literature; real recordings within category only 27-51 %.
Attempt 4 (09-28, concrete+cement, the published way): tools/footstep_synth_hard.py — Hunt-Crossley
heel/toe contacts with 1-4 sub-contacts over ~7 ms (heel roll), force radiates as thump, strikes 40
short modes (tau <= 10 ms, redrawn each step), grit burst ∝ force, damped leg resonance ~140 Hz. Fitted
to the first 15 ms of real strikes (quarter-octave spectra, rise/crest, TONALITY in the cost). A
version with 14 modes ringing up to 100 ms measured 30 dB tonal (real 9) and was NOT played: always
check tonality before rendering for Cody. Demo in inbox/footstep-synth-demo-2026-09-28, awaiting ear.
Attempt 4 verdict (09-28): "very bad, sounds like metal, very dry, concrete and cement the same".
Synthetic footsteps are DONE — do not propose again unless Cody asks.
