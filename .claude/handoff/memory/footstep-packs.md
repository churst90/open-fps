---
name: footstep-packs
description: 09-28 replacing the footstep bank from approved/footsteps "16"/"17" Foley packs — sets are LANDING FILE FIRST then two walks; Cody labels by ear
metadata:
  type: project
---

Cody wants the footstep bank replaced by the two Foley packs in `approved/footsteps/16 - Foley #1
Footsteps` and `17 - Footsteps`: split into walk, run, land, scuff, every material, then replace
`ASSETS/SOUNDS/FOOTSTEPS` (and LANDING). No metadata; 47 files named, 146 "Track NN"/numbers.
Structure found: sets of three — a LANDING file (landing, jog ~0.35 s, run ~0.27 s) then two WALK
files (walk ~0.55-0.7 s, then ~15 s of short gestures: maybe scuffs/pivots, asked Cody). I first
grouped walk,walk,land — WRONG; Cody's notes ("walk X then land/run Y", Y = next group's walk)
proved land-first. Acoustic nearest-neighbour labels: 18/47 exact, ~70% surface family — only a
starting guess. Pack 17 = wood (shoes/barefoot/heels), carpet, tile, heels on concrete, metal;
pack 16 = creaky wood, carpet over creaky wood, hollow wood, gravel, dry grass; 16-27..16-47 unknown.
Work files: scratchpad steps/ (seg.py, feat.py, preview2.py, labels.json); previews in
`inbox/footstep-sets-2026-09-28` (named "<set> <label>"). Next: Cody confirms labels → cut
walk/run/land/scuff per set, level like TakeLevels, listening check, then replace the bank
(tools/rebuild_footstep_bank.py is the current builder). See [[footsteps-are-the-bank]],
[[footstep-take-levels]], [[approved-sounds-leave-the-inbox]].

09-28 later: the 16/17 folders ARE Cody's complete archive (the loose files in approved/footsteps
were cut from them: 17/89 == 16/Track 94 == old mud, 17/95 sand, 16/97 leaves). The ~90-100 s files
at the end of each pack are ONE surface each; I first lumped 17/88-96 as "metal" — wrong, now split.
Builder: tools/build_footstep_bank.py + tools/footstep_sets.json (75 sets); step period by envelope
autocorrelation (gravel crunch halves onset spacing). Staged at /tmp/openfps-footstep-bank (14
materials, ~1,900 takes, 25 MB ogg). Waiting on Cody: 17-90..96 surfaces
(inbox/footstep-long-files-2026-09-28), the unsettled sets, and the bank check
(inbox/footstep-bank-check-2026-09-28). Then --install + code for gait/scuff/shoe.
09-28 later still: Cody: USE ALL STEPS (no thinning) → 11,589 takes, 147 MB ogg; must lazy-load
FOOTSTEPS per material (PreloadAll would decode ~800 MB). Equal MP3 durations are NOT duplicates
(checked by correlation: all ~0); old loose mud = 17/89 exactly, 16/94 is a different recording;
Cody hears "mud" as gravel — asked A (17/89) vs B (16/94). Long files: 17-90 mud, 91/92/94 sand,
93 snow (unsure), 95 sand, 96 barefoot sand.
Synthetic footsteps (Cody asked 09-28, 10 per surface, "never repeat"): tools/footstep_synth_model.py.
Mean+PCA of log band envelopes FAILED on concrete (heel-toe gap varies → average step 11 dB quiet,
dies too fast). Works: borrow a real step's 10 ms-smoothed 1/3-octave envelope (or a beta-blend of
two), warp ±8%, level ±1.5 dB, tilt ±2 dB, + AR(1) fine texture >500 Hz, on FRESH band noise.
Matches: shape 0.8-3.5 dB, spread and tonality as real; cement crunch kurtosis high (10.8 vs 4.8).
Demo: inbox/footstep-synth-demo-2026-09-28 (a = synthetic, b = real, same gain). Labels: 17/89 mud,
16/94 gravel.
INSTALLED 09-28: ASSETS/SOUNDS/FOOTSTEPS/<Mat>/<shoe>/<gait>/<mat>_<shoe>_<gait>_NN.ogg + flat
default walk per material; LANDING/<Mat>/<shoe>/; levels.json sidecar per folder (TakeLevels reads
it — it only measured WAV); takes >18 dB under folder median dropped (1.4 %); nested folders NOT
preloaded (AudioEngineFacade.IsFootstepVariant); ±3 % pitch ±1 dB per step; Grass→Dirt stand-in
removed. Client build output keeps stale files: rm its FOOTSTEPS/LANDING before rebuilding.
Source files RENAMED "<surface> - <shoe> - set N - <part>.mp3" (RENAMED-2026-09-28.csv); old loose
files were exact copies (removed); rebuild_footstep_bank.py retired. Old-name conflicts to ask Cody:
old "shoes on metal" = concrete-high-heels set 1 walk 4 (17-70 group of 5 is probably two sets);
old "shoes on carpet" = unlabelled 17-30; old "shoes on wood" = wood-barefoot set 2; old "shoes on
concrete" = cement-sneakers.
09-28 PROBLEM: Cody heard the "real" concrete/cement demo steps (pooled from the sets labelled
concrete/cement shoes) mixing several materials → the SET labels are unreliable: set previews only
played the landing + FIRST walk file, the other walk files were never heard, and set grouping came
from track order. File-to-file acoustic comparison can't audit it (flags 140/158; sessions differ
more than surfaces). Fix: per-FILE review by ear — inbox/footstep-file-review-2026-09-28 (193 clips,
numbered, named by current label, list.txt). Old bank restorable: git checkout -- ASSETS FOOTSTEPS
(518 wav) and LANDING (105). Relabel footstep_sets.json per file, rebuild, reinstall.
