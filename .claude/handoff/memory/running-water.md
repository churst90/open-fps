---
name: running-water
description: 10-06 running-water model started (creeks, gutters, drains, downpipes, run-off, fountain overflow); design doc first, fit at both scales, wide from the start
metadata:
  type: project
---

Cody 2026-10-06 asked for a running-water model "so fountains and rain sound realistic". I advised testing
today's unheard work in game first (wide sources verdict decides whether the bit-crushed cause is solved),
then running water, then VPS + Windows zip. Agent started anyway (design first): docs/RUNNING_WATER.md,
RunningWaterSynth with EventSum + ExtendedSources, refs in ~/openfps-scratch-archive/running-water-2026-10-06,
renders inbox/running-water-2026-10-06. See [[texture-statistics]].

**MERGED+pushed 10-06.** RunningWater.cs/Runoff.cs/RunningWaterSynth; flow:creek|gutter|drain_grate|downpipe|
basin_overflow; hydraulics (Manning, HEC-22 kerb gutter, weir law), Froude>1.4 jumps entrain bubbles (Deane &
Stokes), rain catchment fills/drains over minutes. On the city: 15 m gutter + drain on Foundry St south kerb at
spawn (/tp 60 122 0.15), rain only. Fountain unchanged; overflow NOT connected (Cody to decide, +4.6 dB).
Open: gutter+drain ~8 dB over the rain at spawn (unbacked level; KerbGutter obstacle density), gutter stats 5-6/14,
downpipe dark at 4-8k, falls +3 dB/doubling vs Watts 6, single-exponential after-rain tail; no taps yet.
**APPROVED by ear 10-06:** Cody: "all of the samples sound good, I like them". Round 2 started: gutter-to-downpipe
gurgle, draining gurgle/vortex, taps and sinks (fill pitch, basin, drip, plug-out drain), flats' kitchens and
bathrooms, interact to turn taps; plus downpipe brightness, 6 dB/doubling falls, two-stage drip tail.
10-06: Cody asked for waves (shore lapping, surf, river wash, hull slap) -> separate agent started
(docs/WAVES_AND_SHORES.md, inbox/waves-2026-10-06, shorelines from OSM water in Albany/Magnolia via gen_osm).
Underwater listening + swimming + boats filed in todo, after geometry stages 1 and 3.
**Round 2 MERGED+pushed 10-06 (377d2f1e):** RunningWaterSynth.Basin (outlet vortex/glug by submergence: BS 6367 /
HR Wallingford capacity, Gordon critical submergence, Perez 2026 glug rhythm), taps over basins (fill, plunge,
steel/ceramic ring, aerator, worn-washer drip), shower, gutter outlet; E within 1.3 m toggles tap+plug (prefab
SynthRunning settable); 333 flats x (kitchen sink, washbasin, shower) = 999 fixtures, dripping tap Union 11F
(/tp 10.8 55.3 3.3); 64 houses got downpipe + gutter outlet (/tp -320.5 136.5 0.2 in rain); falls 4-7 dB/doubling,
downpipe brighter, slow-store drip tail. Open for Cody: one key tap+plug?, drain 9 dB over tap in washbasin,
no measured tap levels, toilet flush not built.
Waves MERGED+pushed 10-06 (c3a9eee6 + test fix): Waves.cs (JONSWAP fetch-limited, CEM, Iribarren, run-up),
ShoreSynth (columns of 16+12 components, breaking plumes Deane-Stokes, swash, foam, shingle Hertz clicks, wall
spray/pockets, hull plate modes with added mass), 10 presets, shore:<preset>/<fetch>/<bearing>/<length>; Albany 219
stretches, Magnolia 38 (pond outlines, reeds where wetland). Open: calm river silent -84 dBFS, shingle hits -1 dBTP
at 3 m, boats too clicky, lake lows 15-25 dB under refs (refs carry mic wind). LESSON: a new model kind needs a
case in MachineVoiceTests.EverySynthIdAShippedPrefabNamesIsAModelThatExists; and never `git push` in a command
that doesn't stop on a failing test (use && after the test grep).

Cody 10-06 by ear: water model "sounds fantastic" EXCEPT (a) wave crashes steppy/exact - "waves are a wash, not
jumpy events"; (b) gutter_outlet_heavy flanges = autocorr 0.31 at 8.08 ms (125 Hz comb), carried into
house_downpipe_and_gutter_outlet_heavy; others similar. Fix agent sent -> inbox/water-smoothing-2026-10-06.
Cody: "if you can't fix that issue reliably, don't worry about it."
2026-10-07 waves smoothing APPROVED by ear ("the new waves sound great now"): smooth Flicker (FlickerSeconds 0.1),
breaks spread along the beach, wind sea and swell found apart. Pushed d9e6a085. Gutter/downpipe comb fix
(GroundReflection.Texture, incoherent ground for running water) in the same push; not yet commented on by ear.
2026-10-07 Cody: "the down pipe drains still flange on the after files" -> per-channel cepstrum still shows a WANDERING
comb 0.33-0.65 ms in 40-65 % of frames (L+R mean hid it). Agent sent back (round2 to inbox/water-smoothing-2026-10-06/round2).
LESSON: check combs per ear and frame by frame; a flanger is a moving comb, a fixed-lag autocorrelation misses it.
Tyre squeals round 3 APPROVED same day ("tire squeals are good").
2026-10-07 downpipe round 2 MERGED locally (unheard): the remaining flange was the Tube's 32.3 ms round trip
(5.5 m air column) returning a fifth to a third of every splash up to 6 kHz; open ends now keep exp(-(ka)^2/2).
The wandering 0.3-0.65 ms cepstral peaks are HRTF pinna notches (white-noise reference shows them; HRTF off = 0).
2026-10-07 water perf MERGED locally: surf 19.3 -> 3.8 % of a 5.1 GHz core, shingle 24.9 -> 4.7, creek 6.5 -> 1.3;
EventSum.Bubble now sin of phase 8 at a time (polynomial sine; only non-bit-identical change, 90-125 dB under);
bursts in registers; ShoreSynth.RenderBlock per 128 samples. --water-cost lab tool. TRAP: benchmark on >= 2 cores
(1 core: JIT tier-up thread never runs). Shingle at limiter = plunging breakers (90.9 dB), not stones (70.5);
calm river silent because eddy waves never break (breaks = !eddy) - both in docs/WAVES_AND_SHORES.md 8.
10-10: water over terrain renders (inbox/water-over-terrain-2026-10-10) APPROVED by ear ('water over terrain sounds good').
