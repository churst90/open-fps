#!/usr/bin/env python3
"""How alike the two ears are, by octave band: the interaural cross-correlation of a binaural file.

    interaural.py files FILE...                 each whole file (a recording, a render)
    interaural.py segments DIR [--json FILE]    each scene of an AudioLab capture (DIR/capture.wav or
                                                DIR/capture.post.wav, and DIR/segments.csv)
    interaural.py windows FILE [SECONDS]        the same over consecutive windows (a walk past)

Per octave band (125 Hz to 8 kHz), after a fourth-order Butterworth band-pass of the octave:

  IACC   the largest |normalised cross-correlation| of the two ears within 1 ms either way (ISO 3382-1's
         IACC, here of a running signal rather than an impulse response). A point source in free field is
         1.0 in every band whatever its direction: the far ear hears the near ear's signal, later and
         filtered. A diffuse field at a real head is about 0.95 at 125 Hz, 0.8 at 250, 0.4 at 500, 0.2 at
         1 kHz and under 0.15 above (Lindevald and Benade 1986; Jacobsen and Roisin 2000; the curve
         TailIaccSpike measures the tail against).
  r0     the correlation at zero lag: the same, without looking for the delay.

Summary columns:
  ASW    1 - IACC averaged over the 500 Hz, 1 kHz and 2 kHz octaves: the apparent-source-width measure of
         concert-hall acoustics (1 - IACC_E3, Hidaka, Beranek and Okano 1995; Okano, Beranek and Hidaka
         1998), taken here over the whole running signal rather than an impulse response's first 80 ms.
         0 is a point; a diffuse field is about 0.8.
  hi     IACC averaged over 2, 4 and 8 kHz: the band where identical noise at both ears is heard as
         narrow and in the head.
  L-R    the level difference of the ears, dB (positive: the left is louder).

Needs numpy, scipy and soundfile (/home/cody/drumsynth/venv/bin/python).
"""
import csv
import json
import os
import sys

import numpy as np
import soundfile as sf
from scipy.signal import butter, sosfiltfilt

OCTAVES = [125, 250, 500, 1000, 2000, 4000, 8000]
HEAD, TAIL = 0.6, 0.2
MAX_LAG_S = 0.001


def read(path):
    x, sr = sf.read(path, always_2d=True, dtype="float64")
    if x.shape[1] == 1:
        x = np.repeat(x, 2, axis=1)
    return sr, x[:, :2]


def xcorr_max(l, r, max_lag):
    """The largest |normalised cross-correlation| within +-max_lag samples, and the zero-lag value."""
    n = len(l)
    size = 1 << int(np.ceil(np.log2(2 * n)))
    L = np.fft.rfft(l, size)
    R = np.fft.rfft(r, size)
    c = np.fft.irfft(L * np.conj(R), size)
    c = np.concatenate([c[-max_lag:], c[:max_lag + 1]])
    norm = np.sqrt(np.dot(l, l) * np.dot(r, r)) + 1e-30
    c = c / norm
    return float(np.max(np.abs(c))), float(c[max_lag])


def bands(x, sr):
    lag = int(round(MAX_LAG_S * sr))
    out = []
    for f in OCTAVES:
        lo, hi = f / np.sqrt(2), min(f * np.sqrt(2), 0.45 * sr)
        sos = butter(4, [lo, hi], btype="band", fs=sr, output="sos")
        l = sosfiltfilt(sos, x[:, 0])
        r = sosfiltfilt(sos, x[:, 1])
        if np.dot(l, l) < 1e-20 or np.dot(r, r) < 1e-20:
            out.append((f, float("nan"), float("nan")))
            continue
        out.append((f,) + xcorr_max(l, r, lag))
    return out


def summary(x, sr):
    b = bands(x, sr)
    iacc = {f: v for f, v, _ in b}
    asw = 1.0 - np.nanmean([iacc[500], iacc[1000], iacc[2000]])
    hi = np.nanmean([iacc[2000], iacc[4000], iacc[8000]])
    pl, pr = np.mean(x[:, 0] ** 2), np.mean(x[:, 1] ** 2)
    rms = 10 * np.log10((pl + pr) / 2 + 1e-30)
    lr = 10 * np.log10((pl + 1e-30) / (pr + 1e-30))
    return {"bands": b, "asw": float(asw), "hi": float(hi), "rms": float(rms), "lr": float(lr)}


def header():
    return (f"  {'':<40} {'rms':>6} {'L-R':>5} | IACC " + " ".join(f"{(str(f // 1000) + 'k') if f >= 1000 else f:>5}" for f in OCTAVES)
            + f" | {'ASW':>5} {'hi':>5} | r0 " + " ".join(f"{(str(f // 1000) + 'k') if f >= 1000 else f:>5}" for f in OCTAVES))


def row(name, s):
    return (f"  {name[:40]:<40} {s['rms']:6.1f} {s['lr']:5.1f} |      " + " ".join(f"{v:5.2f}" for _, v, _ in s["bands"])
            + f" | {s['asw']:5.2f} {s['hi']:5.2f} |    " + " ".join(f"{z:5.2f}" for _, _, z in s["bands"]))


def files(paths):
    print(header())
    res = {}
    for p in paths:
        sr, x = read(p)
        s = summary(x, sr)
        res[os.path.basename(p)] = s
        print(row(os.path.basename(p), s))
    return res


def segments(d, json_path=None):
    cap = None
    for name in ("capture.post.wav", "capture.wav"):
        if os.path.exists(os.path.join(d, name)):
            cap = os.path.join(d, name)
            break
    sr, x = read(cap)
    print(f"{cap}: {len(x) / sr:.1f} s at {sr} Hz")
    print(header())
    res = {}
    with open(os.path.join(d, "segments.csv")) as f:
        for r in csv.DictReader(f):
            a = int((float(r["start"]) + HEAD) * sr)
            b = int((float(r["start"]) + float(r["seconds"]) - TAIL) * sr)
            seg = x[a:b]
            if len(seg) < sr or np.mean(seg ** 2) < 1e-14:
                continue
            s = summary(seg, sr)
            res[r["name"]] = s
            print(row(r["name"], s))
    if json_path:
        with open(json_path, "w") as f:
            json.dump(res, f, indent=1)
    return res


def windows(path, seconds=2.0):
    sr, x = read(path)
    n = int(seconds * sr)
    print(header())
    for i in range(0, len(x) - n + 1, n):
        s = summary(x[i:i + n], sr)
        print(row(f"{os.path.basename(path)[:28]} {i / sr:5.1f}-{(i + n) / sr:5.1f} s", s))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    cmd = sys.argv[1]
    if cmd == "files":
        files(sys.argv[2:])
    elif cmd == "segments":
        j = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
        segments(sys.argv[2], j)
    elif cmd == "windows":
        windows(sys.argv[2], float(sys.argv[3]) if len(sys.argv) > 3 else 2.0)
    else:
        print(__doc__)
        sys.exit(1)
