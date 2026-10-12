---
name: open-fps-patches
description: ../open-fps-patches holds Cody's fixes found while porting to Resonance Engine; all 8 applied by 09-30 and the files deleted; README keeps a "found but not patched" list
metadata:
  type: reference
---

`/home/cody/external-rescue/Github/open-fps-patches/` (not a git repo): patches Cody makes while
building Resonance Engine from open-fps. Check new ones with `git apply --check` / `--reverse --check`;
patches that no longer apply may still be in (code moved on) — grep for their tests/functions.
09-30: 0001-0008 all confirmed applied and the .patch files deleted on Cody's word; README.md left.
Its "found but not patched" list was fixed 09-30 (92d58767) except: ThreadPriority does nothing on
Linux (cosmetic), and the pathing bake runs but nothing in play reads it. The acoustic worker is
never disposed at shutdown (process exit covers it). See [[mixer-callbacks-must-not-free]].

10-03: new 0001 (muffler case read each chamber's far end once, ExhaustNetwork) applied and pushed
as 9d20206c on Cody's word; listen/ renders measured, match the README table within 0.2 dB. The
.patch file is still in the folder.

10-04: 0002 (diesel locomotives: crank unloaded, EngineSynth.GovernedRpm, TrainSynth governs every
unit, EMD TurboIdleSpool 0.45) applied with git am as cc1855a8 on main, NOT pushed. Rail/engine/
starter/turbo/small-machine classes: 99 passed, 1 skipped. Unheard. Common changed (Engines.cs), so
a new Windows zip + VPS update. The .patch file is still in the folder.

10-05: new 0001-0006 (tyres at device rate, rear compressor, rail axles in files, machine starts as
its base + exhaust x, road air horn from ModelLibrary, bell headroom) applied with `git am -3` on main
as 5c7061bb..cad6a089. Patch 6 conflicted as its README predicted: kept the nature work's
PhysicalHeadroom/HeadroomGain and added the `bell:` case. Their tests: 128/128. The .patch files are
still in the folder (Cody deletes them).
10-06: all six confirmed in main (5c7061bb, b831f8b0, 72f8934e, f51224e4, aa73fb84, cad6a089) and the .patch files deleted on Cody's word; README.md left.

**10-06 later: NEW location `Resonance Engine/open-fps-patches/`** (not ../open-fps-patches). 0001 binaural stage
input -3.01 dB fixed (process callback; master makeup 6, trim 0; ear wind + UI 3 dB quieter; traced reverb 3 dB
lower vs direct; short knocks get ground), 0002 OverloadDb minus air loss, 0003 loop echo level (LoopEchoLevel),
0004 OpeningRoutes CrossesTwice — applied with git am -3 (only changes.md conflicted), merged + pushed.
--binaural-input reads 0.00 dB. WideSourcesTests.ATreesPlaceVoicesAddUpToItsOneVoice flaked once in a mixed batch
(passes alone and on reruns) — shared wind state. Patch files NOT deleted: Cody 10-06 "leave them, the Resonance agent takes care of them" — never delete
files in Resonance Engine/open-fps-patches; just apply.
