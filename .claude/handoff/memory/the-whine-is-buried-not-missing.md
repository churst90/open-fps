---
name: the-whine-is-buried-not-missing
description: Measured 2026-09-20 — the turbofan's compressor tone is worth 0.2-0.5 dB at any power, and the fan's blade tone loses 15 dB of share as it spools up
metadata:
  type: project
---

Cody: "jet whine is there but it seems to get quieter when it spools up." Session 19 had already
raised `WhineDb` 84 → 118 for exactly this and it did not help. `--spool` says why — it renders the
aeroplane twice, once with the tone silenced, and differences the total:

- The compressor tone at `WhineHz` is worth **0.5 dB at idle and 0.2 dB at takeoff** from ahead, and
  **0.0 dB astern from half power up**. Raising `WhineDb` further is pushing on a rope: the jets go
  as U⁸ and the tone as spool³, so the tone loses 6 dB of share per doubling of spool whatever it
  is anchored at.
- The tone he is listening for at power is the **FAN**, and **the fan is fine**: its blade-passing
  line measures 4.5-7 dB under the row's own energy and is FLAT from idle to takeoff.

  **CORRECTION, same session.** I first reported that line collapsing from −11 dB to −25.6 dB and
  blamed the missing duct. That was my instrument, not the aeroplane: a single Goertzel bin at the
  nominal blade rate, against a TWIN whose two fans are trimmed 0.5 % apart — 10 Hz at 2 kHz, ten
  bins of a one-second window — so it sat between the two lines and saw neither. `--spool` uses a
  41-bin bank across ±3 % now. I also added a rotor-stator interaction tone on the strength of the
  bad reading and REVERTED it: it fixed nothing (the tone was never broken) and cost 2-3 dB of the
  row to phase cancellation against the passage pulse's own fundamental.
- What IS left, and is correct physics: the compressor tone's FREQUENCY scales with the spool, so
  it sits at 1.4 kHz at idle and 6.2 kHz at takeoff, and the air's corner at 500 m is ~1.6 kHz. The
  gate whine carries to the ground and the takeoff whine cannot. A real turbofan does that.
- A lined inlet low-pass at 3x the blade rate WAS added and kept — a nacelle liner is real and the
  model had only the duct's high-pass half — but it is worth about a decibel and it rescued nothing.
- **The measurement trap to remember: "worth in dB of the total" is the wrong metric for a TONE.**
  The ear is a spectrum analyser, not a power meter; a pure tone 12 dB under a broadband total
  spread over 10 kHz is well above the noise in its own critical band. Judge a tone against the
  broadband in its own third-octave, not against the whole machine.

**Measurement trap that cost a wrong answer first time:** a blade row's speed follows its target on
a HALF-SECOND time constant. Settling for 0.5 s meters a fan that is still accelerating, and a tone
whose frequency is sliding has no spectral line at all — it read −48 dB. `--spool` settles 4 s now.

See [[aircraft-model]], [[an-aeroplane-has-more-than-one-engine]], [[synthesis-by-listening]].
