---
name: synthesis-by-listening
description: How Cody and I tune a synthesised sound — he listens, I measure, and his words map onto specific physics
metadata:
  type: feedback
---

Every audio model in this project gets settled the same way and it works: **I measure, Cody listens,
and his description names a mechanism rather than a knob.** Never tune to the words directly — find
what physical thing produces that impression and fix that.

Worked examples, all real, all from 2026-09-15/16:

- **"Pouring water"** (applause) → I had built a water droplet. A short noise burst through a sharp
  resonator IS a drip. A clap is broadband with a crack; the trapped air colours it, it does not ring.
- **"Crackling static"** → arrivals were one Poisson process, which is MEMORYLESS and therefore has
  no rhythm by construction. Real applause is quasi-periodic per person: each clapper needs an
  identity and a tempo.
- **"Leaves blowing"** → missing ATTACK. A clap is nearly a step change in pressure; that
  discontinuity is the crack, and one decaying burst averages it away at a few hundred a second.
- **"Too quiet"** → I was normalising a dense texture to its PEAK, so the busier it got the quieter
  it rendered. Normalise to energy.
- **"Deeper / it needs to be wider, not longer"** (muffler can) → **span is a resonator's NOTE, the
  loss factor is its Q.** I spent two rounds lengthening the ring when he wanted the pitch lowered.
- **"Muffled"** → adding low end pulls the top down, because the render normalises by peak.
- **"Loud white noise, I expected to hear more of the engine"** (diesels) → the block was 94 % below
  200 Hz. Two transmission paths had been scaled as one: structure-borne thud through rubber mounts
  vs. knock and clatter radiating straight off the block.
- **"Just rumble, I expect popping and growling"** (V-twin) → a slow engine's harmonics are spaced by
  its firing rate, so a twin needs its 20th harmonic where a V8 needs its 5th. Per-harmonic rolloff
  hits it four times as hard. Fixed with wall loss and STEEPENING, not EQ.
- **"They're all screeching"** → see [[racing-line-banking]] and the tyre-demand entry there.

**How to apply:** render, measure band balance and harmonic-to-noise BEFORE changing anything, make
one physically-argued change, re-measure, then play it to him. Play files directly with
`paplay` and announce each variant with `espeak` first — he is a screen reader user and a spoken
label beats reading a filename. Always give a BRACKET (three variants spanning the range) rather than
one guess; he picks, and picking is faster and more accurate than describing.

See [[audio-lab-instruments]], [[no-special-cases]].
