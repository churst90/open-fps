---
name: a-breath-is-turbulence
description: RenderHiss was a resonance with an onset — the "random banging" chased for six sessions was breathing
metadata:
  type: project
---

**The banging was the player's own breathing.** Cody found it himself: *"it is 2 different bangs so it
makes me think it's breathing in and out... I don't hear the breathing either... can you play the
breathing sound for me?"* Both halves are one fault — two bangs because an inhale and an exhale sit at
different frequencies, no breathing because neither sounded like one.

It fits every detail that was chased for six sessions: two alternating sounds ~1 s apart, starting
after effort, **continuing 15 s after you stop moving** (`Breathing.RecoverySeconds` — it is meant to
outlast the running), and worst in the stairwell because stairs are the hardest work on the map.

`TransientSynth.RenderHiss` — which every breath, door seal and air hiss goes through — was noise
through **ONE resonator at Q 0.9** with an attack a **twelfth of its own length**. A resonance is not
turbulence and a sharp attack is a transient. Measured: an exhale peaked at 250-1000 Hz with **2-4 kHz
23 dB down and 4-8 kHz 32 dB down**, which is where a breath lives.

Now a broad band (two poles down at the top, one up at the bottom, around `hz` rather than resonated
at it) with a **raised-cosine swell over the first 35 %**, so there is no corner for an ear to hear as
an onset. The top came up 24 dB. `BreathTests` holds the bands, the inhale/exhale brightness, the
absence of an onset, and that breathing outlasts the running.

Instrument: **`--breath [effort= seconds=]`** renders the real model through the real synth at real
times and levels, prints every breath, and measures the band balance before anybody listens.

**Why:** six sessions went into the acoustics for a fault in a 12-line render function. The listener
diagnosed it from the pattern (two sounds, alternating, outlasting the effort) when no amount of
looking at reverb could.
**How to apply:** when a sound is unidentifiable, RENDER IT ALONE and hand it over. See
[[synthesis-failures]] (measure bands before playing), [[instrument-must-walk-like-the-game]].
