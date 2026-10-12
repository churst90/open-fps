---
name: reverb-tail-smoothed
description: 10-03 APPROVED by ear — traced tail's directional split averaged over traces (SmoothTail) and independent per-direction late carriers (DiffuseLate) ended the pulsing and the metallic ring; clap-to-tail gap (20-46 ms silence) still open
metadata:
  type: project
---

Cody 2026-10-03, after main 49e743a2: "the reverb issue I think has been resolved I've just listened
to it". What fixed it (do not undo):
- SmoothTail.cs: the pulsing was the DIRECTIONAL split (first-order channels) reshuffling every 250 ms
  trace (one direction 0-30 %), not W (identical between traces). Energies per octave / direction cell
  averaged over traces (reset on region change, > 1 m, 6 dB), fixed noise carriers.
- DiffuseLate.cs: the metallic ring was DiffuseTail's 20 velvet branches being filtered copies of one W
  plus the ear velvet: a product of random spectra (11 % of bins 10 dB proud). Now independent
  per-direction carriers, no velvet: 0.1 %. Lows now flat (were 4-6 dB thin 125-500 Hz).
Lab: --tail-steady, --clap-room tail=raw|late=velvet, --tail-cost. Still open 10-03: the clap-to-tail
gap (traced tail fades in 50-100 ms; silence 20-46 ms in --clap-room), helper on branch early-tail.
See [[traced-reverb]], [[reflections-trim-under-review]].
