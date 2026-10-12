---
name: clap-reference
description: "Cody's slow-clap recording (2026-09-19) measured against the synthesised clap: synth is +11 dB at 250-500 Hz, -9 dB at 1-2 kHz, and twice too slow; instrument is --applause compare=DIR"
metadata:
  type: project
---

2026-09-19: Cody supplied `inbox/Slow Clapping  HQ Sound Effects.mp3` (42 s, dry single claps with
gaps). `tools/split_footsteps.py` cuts it into 67 clean claps (it is a transient splitter, not a
footstep one). `--applause compare=DIR` (added to `ApplauseSpike`, same shape as the footstep compare)
holds `Applause.Render(CrowdApplause(1,…))` against them in Spectrum's nine bands, plus decay.

Measured, shape only: real clap peaks at **1-2 kHz** (like a footstep). Synth peaks at 250-1000 Hz:
**+11.1 dB at 250-500 Hz, -8.7 dB at 1-2 kHz, +10 dB at 30-60 Hz**. Real is gone by 20 dB in 5 ms,
synth 13.5 ms; by 40 dB real 19 ms (includes the recording's room), synth 29.5 ms. So the "pocket of
air" (Q≈3 cavity) is tuned about two octaves low and the clap is dull and slow. `ClapTests` asserted
Mid(200-1500) > 0.45 with no reference, which is what pushed it there — same failure as the first
footsteps ([[synthesis-failures]]).

**Not refitted yet** — Cody asked for the plan before continuing. When refitting: re-fit the cavity
frequency and damping and the ThumpLevel/CrackLevel shares from these numbers, bake the real bands into
`ClapTests` as a ratchet like `RealConcreteWalk`, and only then play the crowd to him.

See [[synthesis-by-listening]], [[a-surface-scatters]], [[city-map-plan]].
