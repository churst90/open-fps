# Car door fit (2026-09-28)

A prototype of a car door opening and closing, fitted to `inbox/door sounds/Car Door Open and Close -
Sound Effect (HD).mp3`. It is the spec for the game's model, which is not written yet.

- `model.py`: `close()` and `opening()`. Closing is the air pushed ahead of the door, a cluster of
  four hits (first touch, secondary catch, seat and primary catch, rebound) through the door skin's
  modes, the latch claw tones, the cabin's standing waves (c/2L, c/2W, c/2H and three more, from the
  cabin's size) driven by the seal pushing a volume into the cabin, and the body settling. Opening
  is the handle and rod taps, the latch letting go with a short low thump, and the check strap's
  detent about a third of a second later.
- `cmp.py REF SYNTH [loudest]`: band levels in four windows (before the impact, 0-50 ms, 50-140 ms,
  140-420 ms), both relative to the impact window.
- `fit.py N`, `fito.py N`: random search over the named parameters against the recording.
- `render.py OUTDIR`: the listening set.

Fitted: closing 3.4 dB RMS across bands after the impact (the game's model today: 20.1 dB).
Needs `ref.wav`, `ref_close.wav` (3.6-5.0 s) and `ref_open.wav` (0.5-1.7 s) cut from the recording
at 48 kHz mono, and numpy, scipy and soundfile (`~/qwentts/venv`).

## Second version: noise, not resonators

Cody, 2026-09-28: the first version "sounds like an instrument... too tonal and not mechanical".
The band levels matched, but a handful of fixed resonators ringing for up to 0.7 s is a chord.
`tonal.py` measures it: how far the strongest narrow peaks stand above the local spectral floor.
50-140 ms after the slam: recording 9.3 dB, first version 22.4, the game's old door 7.3.

`model3.py` builds everything from octave-band noise: each hit has a spectrum (a level per band)
and a decay per band, the cabin boom is the low bands swelling and dying away, nothing rings at a
fixed pitch. `fit3.py N close|open` fits it with a penalty for being more tonal than the recording.
Closing: 1.9 dB RMS over the bands after the impact, peaks 8.9 dB over the floor at 50-140 ms.
Opening: 6.4 dB. Parameters in `close_noise.json` and `open_noise.json`.
