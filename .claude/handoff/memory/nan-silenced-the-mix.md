---
name: nan-silenced-the-mix
description: 10-03 a pop then total silence = a NaN reached the master; Steam Audio binaural NaNs on a zero direction and holds it; every unit now guarded with [NONFINITE]
metadata:
  type: project
---

2026-10-03 02:59, on try-early-tail: Cody walked Marlow Tower corridor -> flat 00B (door opened), a pop,
then the whole mix silent until quit. Evidence: the provider's "Mix loudness" line vanished (skipped
only when the meter is non-finite or <= -200) while "Room:" lines went on.
Exact source not reproduced. Found and fixed (commit 40055c8d, branch early-tail):
- Steam Audio binaural outputs NaN for a zero/tiny/NaN direction at every frame size and HOLDS it;
  all binaural calls go through Phonon.SafeDirection. Likely trigger: a door's beacon now hangs on its
  face at 1.6 m (2026-10-02), so walking through a doorway puts the head on the source.
- SmoothTail's recursive average kept a NaN trace; non-finite traces rejected. LateField NaN ratio
  passed `<= 0`; ignored now.
- NonFinite guard after every custom DSP writing to the mix: zero the block, reset the unit, log
  `[NONFINITE] unit sound, region` once; the master's boundary stage zeroes non-finite blocks before
  the limiter. If the only line is "the mix arriving at the master bus", the NaN came from an FMOD
  built-in parameter (volume, EQ gain, send mix), not our DSP.
Lab: --nan-mix, --nan-walk. See [[mixer-callbacks-must-not-free]], [[reverb-tail-smoothed]].
