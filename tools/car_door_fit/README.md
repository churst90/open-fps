# Car door fit

The fit behind `OpenFPS.Common.CarDoor`: a car door opening and closing, fitted to
`approved/car-door` (see approved/README.md) and approved by ear on 2026-09-28.

- `model3.py`: everything built from octave-band noise. Each hit has a spectrum (a level per band)
  and a decay per band; the cabin boom is the low bands swelling and dying away; nothing rings at a
  fixed pitch.
- `fit3.py N close|open`: random search over the model's parameters against the recording, with a
  penalty for being more tonal than it. Writes `m3_close.json` or `m3_open.json`.
- `close_noise.json`, `open_noise.json`: the fitted parameters the game's model was written from.
  Closing: 1.9 dB RMS over the bands after the impact, peaks 8.9 dB over the floor at 50-140 ms.
  Opening: 6.4 dB.
- `cmp.py REF SYNTH [loudest]`: band levels in four windows (before the impact, 0-50 ms, 50-140 ms,
  140-420 ms), both relative to the impact window.
- `tonal.py`: how far the strongest narrow peaks stand above the local spectral floor. The
  recording reads 9.3 dB at 50-140 ms after the slam.

`AudioLab --car-door out=DIR` renders the game's model for `cmp.py` and `tonal.py`.

Needs `ref.wav`, `ref_close.wav` (3.6-5.0 s) and `ref_open.wav` (0.5-1.7 s) cut from the recording
at 48 kHz mono, and numpy, scipy and soundfile (`~/qwentts/venv`).

The first version, a handful of resonators at the door skin's and the cabin's modes, matched the
band levels and was rejected as "too tonal and not mechanical"; it is in git history.
