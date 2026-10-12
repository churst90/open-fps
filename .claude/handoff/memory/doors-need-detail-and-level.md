---
name: doors-need-detail-and-level
description: "Cody 10-03 round 3 verdict on the physical doors — too quiet, too short, sterile, tonal, events run together; push bar housing is hollow; wants noise not sines; asked for deep research"
metadata:
  node_type: memory
  type: feedback
  originSessionId: f81a1dbe-770b-4ab5-8b47-4dc3748f9cdb
  modified: 2026-10-03T16:53:59.947Z
---

Cody, 2026-10-03, after doors round 3 (knob latch bracket + push bar round 2):
- "way way way too quiet ... if I'm 5 feet away from a door at these levels I'd barely know someone
  opened a door". The true-levels clips are too quiet. Do NOT take level off the doors with a
  "calibration" (the −12 dB `LevelCalibrationDb` was the wrong call); normalise listening files by
  loudness, not by the peak of a crack.
- "too abbreviated ... no detail ... too sterile ... too tonal and synthetic". Sparse modes ring as
  sines; he expects broadband (noise-like) content where a real door has dense modes and rattles.
- Knob door: "the door and the latch are too close together"; "you can hear the audible separation
  between the latch and door"; "when a knobbed door closes you can hear it jiggle a little after it
  shuts"; "the closer they are, the harder the door shuts". Knob "too clicky".
- Push bar: open was "2 thin clicks, real thin and tiny"; "the push bar is usually hollow, not a thin
  metal bar" (the device housing is a hollow box that resonates).
- "these sound nothing really like the reference clips" and "can you not do additional deep research
  on how to model doors correctly?"

**Why:** each round fixed one mechanism but the renders stayed dry, short, sparse and peak-normalised;
the reference recordings have long, dense, noisy, multi-event structure (and their room).
**How to apply:** compare against the references in TIME first (event sequence, gaps, how long each
part lasts, decay envelopes, spectral flatness), not only octave bands; research how published
door/impact synthesis gets density (modal + stochastic residual); give doors real level. See
[[knob-door-pilot]], [[model-the-thing-not-the-sound]], [[render-check-before-sending]].

**Round 4/5 (same day):** research done (reports/Realistic door sound synthesis.md). Built dense fields
through ports (energy-bounded), acceleration noise, hand closes at 0.08/0.22/0.4 m/s, knob jiggle,
hollow push bar. Cody on round 4: push bar "too tonal, knocking on a pan", then "doesn't need
dampening, needs LESS dampening, it's dull", "more clack on push in and on release, release shorter,
not a rattle"; knob squeak inaudible (variant was only 0.55 worn) and "knocking" = knobs rattling
while held. Round 5 in inbox/doors-2026-10-03-round5. Lessons: sox `fir` centres the filter (pad the
IR with N-1 leading zeros or the direct sound is lost); listening files: level each by LAF, never one
gain for a 40 dB range; don't add damping to cure "pan" — he means tonal/dull, not long.

**Rounds 6-8 (same day):** round 7 "mechanics and sound is there now, just get the clack"; then "no
clack ... like a heavily padded pushbar on a wooden hollow door ... if you're including the hollowness
of the door, don't. I simply want to hear the push bar ... white noise type transient". The hollow =
the leaf's few bending modes (300-1000 Hz, 94 % of their energy in 6 peaks) and its air pump; the
padded clunk = plastic landings lasting ~0.9 ms. Round 8 (inbox/doors-2026-10-03-round8, commit
b27e2511): opening is the device alone, the bolt's 0.1 ms steel stop rings the case, leaf modes muted
on close. Trap: a "parts thrown by the blow" model fed the whole contact impulse fired a hand's 330 ms
HOLD as one burst on release; take the first millisecond only. Waiting on Cody before merging.
