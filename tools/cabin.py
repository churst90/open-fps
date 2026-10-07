#!/usr/bin/env python3
"""Sitting in a vehicle (CabinPaths): the level checks and the listening files.

    cabin.py model DIR                    the interior model alone (AudioLab --cabin model): per vehicle and
                                          condition, the level with the paths summed against the one signal,
                                          A-weighted and per octave
    cabin.py levels BEFORE AFTER          two game captures (AudioLab --cabin game, paths=off and on): per
                                          scene, the level at the ears (the two ears' mean power), A-weighted
                                          and per octave, and the difference
    cabin.py cut DIR OUT PREFIX           each scene of a game capture as its own 24-bit WAV, named
                                          PREFIX_<scene>.wav, with 30 ms faded ends
    cabin.py hrtf DIR                     the HRTF in each path's direction (AudioLab --cabin hrtf): the two
                                          ears' mean power per octave, against the one interior voice's direction
    cabin.py check FILE...                every file: RMS, peak, samples at or over full scale, gaps
                                          (50 ms or more under -90 dBFS), and IACC per octave

Octaves 63 Hz to 8 kHz, fourth-order Butterworth band-passes. A-weighting from IEC 61672's curve applied
to a Welch spectrum. Needs numpy, scipy and soundfile (/home/cody/drumsynth/venv/bin/python).
"""
import csv
import os
import sys

import numpy as np
import soundfile as sf
from scipy.signal import butter, sosfiltfilt, welch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import interaural  # noqa: E402

OCTAVES = [63, 125, 250, 500, 1000, 2000, 4000, 8000]
HEAD, TAIL = 0.6, 0.2


def a_weight_db(f):
    f = np.maximum(f, 1e-3)
    f2 = f * f
    ra = (12194.0 ** 2 * f2 * f2) / ((f2 + 20.6 ** 2) * np.sqrt((f2 + 107.7 ** 2) * (f2 + 737.9 ** 2)) * (f2 + 12194.0 ** 2))
    return 20 * np.log10(ra) + 2.0


def levels(x, sr, ref=1.0):
    """x: (n,) or (n, ch). Mean power over channels. Returns (A-weighted dB, {octave: dB}, flat dB)."""
    if x.ndim == 1:
        x = x[:, None]
    x = x / ref
    f, p = welch(x, fs=sr, nperseg=8192, axis=0)
    p = p.mean(axis=1)
    df = f[1] - f[0]
    a = 10 * np.log10(np.sum(p * 10 ** (a_weight_db(f) / 10)) * df + 1e-30)
    flat = 10 * np.log10(np.mean(x ** 2) + 1e-30)
    octs = {}
    for fc in OCTAVES:
        lo, hi = fc / np.sqrt(2), min(fc * np.sqrt(2), 0.45 * sr)
        sos = butter(4, [lo, hi], btype="band", fs=sr, output="sos")
        y = sosfiltfilt(sos, x, axis=0)
        octs[fc] = 10 * np.log10(np.mean(y ** 2) + 1e-30)
    return a, octs, flat


def fmt_oct(o):
    return " ".join(f"{o[f]:6.1f}" for f in OCTAVES)


def oct_header():
    return " ".join(f"{(str(f // 1000) + 'k') if f >= 1000 else f:>6}" for f in OCTAVES)


def model(d):
    rows = list(csv.DictReader(open(os.path.join(d, "model.csv"))))
    pa_ref = 20e-6 * 0.05      # the lab wrote pascals x 0.05: this is 0 dB SPL
    print(f"{'condition':<28} {'dB(A)':>6} {'dB':>6} | octaves dB SPL {oct_header()}")
    byk = {}
    for r in rows:
        x, sr = sf.read(os.path.join(d, r["file"]), dtype="float64")
        a, o, flat = levels(x, sr, ref=pa_ref)
        key = r["file"].rsplit("_", 1)[0]
        byk.setdefault(key, {})[r["paths"]] = (a, o, flat)
        print(f"{r['file'][:-4]:<28} {a:6.1f} {flat:6.1f} |                {fmt_oct(o)}")
    print("\npaths minus one signal, dB")
    print(f"{'condition':<28} {'dB(A)':>6} {'dB':>6} |                {oct_header()}")
    for k, v in byk.items():
        if "on" in v and "off" in v:
            (a1, o1, f1), (a0, o0, f0) = v["on"], v["off"]
            print(f"{k:<28} {a1 - a0:6.2f} {f1 - f0:6.2f} |                " + " ".join(f"{o1[f] - o0[f]:6.2f}" for f in OCTAVES))


def scenes(d):
    cap = None
    for name in ("capture.post.wav", "capture.wav"):
        if os.path.exists(os.path.join(d, name)):
            cap = os.path.join(d, name)
            break
    x, sr = sf.read(cap, always_2d=True, dtype="float64")
    out = []
    for r in csv.DictReader(open(os.path.join(d, "segments.csv"))):
        a = int((float(r["start"]) + HEAD) * sr)
        b = int((float(r["start"]) + float(r["seconds"]) - TAIL) * sr)
        out.append((r["name"], x[a:b, :2], sr, r))
    return out


def compare(before, after):
    b = {n: (x, sr) for n, x, sr, _ in scenes(before)}
    print(f"{'scene':<32} {'before':>7} {'after':>7} {'diff':>6} | octave difference after - before, dB")
    print(f"{'':<32} {'dB(A)':>7} {'dB(A)':>7} {'':>6} | {oct_header()}")
    for n, x, sr, _ in scenes(after):
        if n not in b or n.startswith("silence"):
            continue
        a1, o1, _ = levels(x, sr)
        a0, o0, _ = levels(b[n][0], b[n][1])
        print(f"{n:<32} {a0:7.1f} {a1:7.1f} {a1 - a0:6.2f} | " + " ".join(f"{o1[f] - o0[f]:6.2f}" for f in OCTAVES))


def cut(d, out, prefix):
    os.makedirs(out, exist_ok=True)
    for n, x, sr, _ in scenes(d):
        if n.startswith("silence"):
            continue
        name = f"{prefix}_{n.replace(' ', '_')}.wav"
        # 30 ms raised-cosine ends: a scene cut out of a running capture starts and stops mid-waveform.
        x = x.copy()
        k = int(0.03 * sr)
        ramp = 0.5 - 0.5 * np.cos(np.linspace(0, np.pi, k))[:, None]
        x[:k] *= ramp
        x[-k:] *= ramp[::-1]
        sf.write(os.path.join(out, name), x, sr, subtype="PCM_24")
        print(os.path.join(out, name))


def hrtf(d):
    rows = list(csv.DictReader(open(os.path.join(d, "hrtf.csv"))))
    gains = {}
    for r in rows:
        x, sr = sf.read(os.path.join(d, r["file"]), always_2d=True, dtype="float64")
        n = 1 << 15
        H = np.fft.rfft(x, n, axis=0)
        f = np.fft.rfftfreq(n, 1 / sr)
        p = (np.abs(H) ** 2).mean(axis=1)
        o = {}
        for fc in OCTAVES:
            m = (f >= fc / np.sqrt(2)) & (f < fc * np.sqrt(2))
            o[fc] = 10 * np.log10(np.mean(p[m]) + 1e-30)
        gains[(r["vehicle"], r["seat"], r["path"])] = (r["kind"], o, (float(r["x"]), float(r["y"]), float(r["z"])))
    print(f"{'vehicle seat path':<34} {'kind':<9} {'direction x,y,z':<20} | ears' mean power, dB re the one voice's direction  {oct_header()}")
    for (v, seat, path), (kind, o, dirn) in gains.items():
        ref = gains[(v, seat, "one")][1]
        print(f"{v + ' ' + seat + ' ' + path:<34} {kind:<9} {dirn[0]:5.2f},{dirn[1]:5.2f},{dirn[2]:5.2f}    | "
              + " ".join(f"{o[f] - ref[f]:6.2f}" for f in OCTAVES))


def check(paths):
    print(f"{'file':<52} {'s':>5} {'rms':>6} {'peak':>6} {'clip':>5} {'gaps':>5} | IACC " + " ".join(f"{(str(f // 1000) + 'k') if f >= 1000 else f:>5}" for f in interaural.OCTAVES))
    for p in paths:
        x, sr = sf.read(p, always_2d=True, dtype="float64")
        rms = 10 * np.log10(np.mean(x ** 2) + 1e-30)
        peak = 20 * np.log10(np.max(np.abs(x)) + 1e-30)
        clip = int(np.sum(np.abs(x) >= 0.999))
        # Gaps: 50 ms windows (hop 10 ms) whose power is under -90 dBFS.
        win, hop = int(0.05 * sr), int(0.01 * sr)
        m = x.mean(axis=1) ** 2
        c = np.concatenate([[0], np.cumsum(m)])
        gaps = 0
        inside = False
        for i in range(0, len(m) - win, hop):
            q = (c[i + win] - c[i]) / win
            g = q < 1e-9
            if g and not inside:
                gaps += 1
            inside = g
        s = interaural.summary(x, sr)
        print(f"{os.path.basename(p)[:52]:<52} {len(x) / sr:5.1f} {rms:6.1f} {peak:6.1f} {clip:5d} {gaps:5d} |      "
              + " ".join(f"{v:5.2f}" for _, v, _ in s["bands"]))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    cmd = sys.argv[1]
    if cmd == "model":
        model(sys.argv[2])
    elif cmd == "levels":
        compare(sys.argv[2], sys.argv[3])
    elif cmd == "cut":
        cut(sys.argv[2], sys.argv[3], sys.argv[4])
    elif cmd == "hrtf":
        hrtf(sys.argv[2])
    elif cmd == "check":
        check(sys.argv[2:])
    else:
        print(__doc__)
        sys.exit(1)
