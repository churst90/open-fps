#!/usr/bin/env python3
"""Measure the wet-road renders (AudioLab --wet-roads; docs/WET_ROADS.md).

    wet_roads.py levels DIR              one voice at a metre, dry against wet (levels.csv)
    wet_roads.py game DIR [--cut OUT]    the game's capture, scene by scene (segments.csv)
    wet_roads.py files FILE...           any WAVs (e.g. reference recordings), same measures

Per file or scene:
  rms / peak   dBFS (both ears' power averaged), peak sample; clip = samples at or over 0.999
  gap          the longest run of 50 ms blocks under -80 dBFS, seconds
  kurt         the 4-16 kHz waveform's kurtosis in 10 ms windows, the median over windows (3 is
               Gaussian; a crackle is more) - over the loudest 2 s for a pass-by
  iacc         the interaural cross-correlation, broadband and 500 Hz-4 kHz, peak within +-1 ms
  oct          octave-band levels 125 Hz-16 kHz (dB; at a metre for 'levels', dBFS for 'game')
For 'levels', each wet condition's octave levels minus the dry ones at the same speed: the increase
the literature quotes (Sandberg and Ejsmont 2002: +4-7 dB above 2 kHz typical, up to 10-15).
Needs numpy, scipy, soundfile (/home/cody/drumsynth/venv/bin/python).
"""
import csv
import os
import sys

import numpy as np
import soundfile as sf
from scipy import signal

OCT = [125, 250, 500, 1000, 2000, 4000, 8000, 16000]


def octaves(x, fs):
    """Octave-band levels (power, dB re 1) of a mono signal, by FFT power summed in each band."""
    n = len(x)
    if n < 1024:
        return [float('nan')] * len(OCT)
    f, p = signal.welch(x, fs, nperseg=8192 if n >= 8192 else 1024, scaling='spectrum')
    out = []
    for c in OCT:
        lo, hi = c / np.sqrt(2), min(c * np.sqrt(2), fs / 2 * 0.999)
        m = (f >= lo) & (f < hi)
        out.append(10 * np.log10(np.sum(p[m]) + 1e-30))
    return out


def a_weighted_db(x, fs):
    f1, f2, f3, f4 = 20.598997, 107.65265, 737.86223, 12194.217
    nums = [(2 * np.pi * f4) ** 2 * (10 ** (1.9997 / 20)), 0, 0, 0, 0]
    dens = np.polymul([1, 4 * np.pi * f4, (2 * np.pi * f4) ** 2], [1, 4 * np.pi * f1, (2 * np.pi * f1) ** 2])
    dens = np.polymul(np.polymul(dens, [1, 2 * np.pi * f3]), [1, 2 * np.pi * f2])
    b, a = signal.bilinear(nums, dens, fs)
    y = signal.lfilter(b, a, x)
    return 10 * np.log10(np.mean(y ** 2) + 1e-30)


def kurtosis_10ms(x, fs):
    sos = signal.butter(4, [4000, min(16000, fs * 0.45)], btype='band', fs=fs, output='sos')
    y = signal.sosfilt(sos, x)
    w = int(0.01 * fs)
    ks = []
    for s in range(0, len(y) - w, w):
        seg = y[s:s + w]
        v = np.var(seg)
        if v <= 1e-20:
            continue
        ks.append(np.mean((seg - seg.mean()) ** 4) / v ** 2)
    return float(np.median(ks)) if ks else float('nan')


def iacc(l, r, fs, band=None):
    if band:
        sos = signal.butter(4, band, btype='band', fs=fs, output='sos')
        l, r = signal.sosfilt(sos, l), signal.sosfilt(sos, r)
    lag = int(0.001 * fs)
    n = len(l)
    if n < 4 * lag:
        return float('nan')
    den = np.sqrt(np.sum(l * l) * np.sum(r * r)) + 1e-30
    best = 0.0
    for k in range(-lag, lag + 1):
        if k >= 0:
            c = np.sum(l[k:] * r[:n - k])
        else:
            c = np.sum(l[:n + k] * r[-k:])
        best = max(best, abs(c) / den)
    return best


def gap(x, fs):
    b = int(0.05 * fs)
    longest = run = 0
    for s in range(0, len(x) - b, b):
        if 10 * np.log10(np.mean(x[s:s + b] ** 2) + 1e-30) < -80:
            run += 1
            longest = max(longest, run)
        else:
            run = 0
    return longest * 0.05


def loudest(x, fs, seconds=2.0):
    """The loudest stretch of a (mono or N x 2) signal."""
    m = x if x.ndim == 1 else x.mean(axis=1)
    w = int(seconds * fs)
    if len(m) <= w:
        return x
    e = np.convolve(m ** 2, np.ones(int(0.1 * fs)), 'same')
    c = int(np.argmax(e))
    s = max(0, min(len(m) - w, c - w // 2))
    return x[s:s + w]


def measure(x, fs, passby=False):
    st = x if x.ndim == 2 else np.stack([x, x], axis=1)
    mono = st.mean(axis=1)
    r = {}
    r['rms'] = 10 * np.log10(np.mean(st ** 2) + 1e-30)
    r['peak'] = 20 * np.log10(np.max(np.abs(st)) + 1e-30)
    r['clip'] = int(np.sum(np.abs(st) >= 0.999))
    r['gap'] = gap(mono, fs)
    part = loudest(st, fs) if passby else st
    r['kurt'] = kurtosis_10ms(part.mean(axis=1), fs)
    r['iacc'] = iacc(part[:, 0], part[:, 1], fs)
    r['iacc_mid'] = iacc(part[:, 0], part[:, 1], fs, [500, 4000])
    r['oct'] = octaves(part.mean(axis=1), fs)
    r['la'] = a_weighted_db(part.mean(axis=1), fs)
    return r


def fmt_oct(o):
    return " ".join(f"{v:6.1f}" for v in o)


def levels(d):
    rows = list(csv.DictReader(open(os.path.join(d, 'levels.csv'))))
    res = {}
    print(f"{'file':34} {'water':>6} {'LA@1m':>6} {'kurt':>5}  octaves " + " ".join(f"{c:>6}" for c in OCT))
    for row in rows:
        x, fs = sf.read(os.path.join(d, row['file']), dtype='float64')
        x = x / 0.05            # pascals at a metre
        o = [v - 20 * np.log10(20e-6) for v in octaves(x, fs)]
        la = a_weighted_db(x, fs) - 20 * np.log10(20e-6)
        k = kurtosis_10ms(x, fs)
        res[(row['vehicle'], row['kmh'], row['condition'])] = (o, la, k, float(row['water_mm']))
        print(f"{row['file']:34} {float(row['water_mm']):6.2f} {la:6.1f} {k:5.2f}  {fmt_oct(o)}")
    print("\nwet minus dry, dB (LA, then octaves)")
    for (veh, kmh, cond), (o, la, k, w) in res.items():
        if cond == 'dry':
            continue
        do, dla, _, _ = res[(veh, kmh, 'dry')]
        print(f"{veh:14} {kmh:>3} km/h {cond:9} water {w:5.2f}  LA {la - dla:+5.1f}   " + " ".join(f"{a - b:+6.1f}" for a, b in zip(o, do)))


def game(d, cut=None):
    x, fs = sf.read(os.path.join(d, 'capture.post.wav'), dtype='float64')
    rows = list(csv.DictReader(open(os.path.join(d, 'segments.csv'))))
    if cut:
        os.makedirs(cut, exist_ok=True)
    print(f"{'scene':42} {'rms':>6} {'peak':>6} {'clip':>4} {'gap':>4} {'kurt':>5} {'iacc':>5} {'iaccM':>5} {'LA':>6}  octaves(loudest 2 s) " + " ".join(f"{c:>6}" for c in OCT))
    out = {}
    for row in rows:
        s, n = float(row['start']), float(row['seconds'])
        a, b = int((s + 0.6) * fs), int((s + n - 0.2) * fs)
        seg = x[a:b]
        if len(seg) < fs // 2:
            continue
        passby = row['name'].startswith(('car', 'bus', 'puddle', 'drying'))
        r = measure(seg, fs, passby)
        out[row['name']] = r
        print(f"{row['name'][:42]:42} {r['rms']:6.1f} {r['peak']:6.1f} {r['clip']:4d} {r['gap']:4.2f} {r['kurt']:5.2f} {r['iacc']:5.2f} {r['iacc_mid']:5.2f} {r['la']:6.1f}  {fmt_oct(r['oct'])}")
        if cut:
            name = row['name'].replace(' ', '_')
            sf.write(os.path.join(cut, name + '.wav'), x[int(s * fs):int((s + n) * fs)], fs, subtype='FLOAT')
    pairs = [(k, k.replace('light rain', 'dry').replace('heavy rain', 'dry')) for k in out if ('rain' in k and k.startswith(('car', 'bus', 'cabin')))]
    if pairs:
        print("\nwet minus dry, loudest 2 s (cabin: whole scene), dB: LA, then octaves")
        for wet, dry in pairs:
            if dry in out and wet != dry:
                print(f"{wet[:42]:42} LA {out[wet]['la'] - out[dry]['la']:+5.1f}   " + " ".join(f"{p - q:+6.1f}" for p, q in zip(out[wet]['oct'], out[dry]['oct'])))
    return out


def files(paths):
    print(f"{'file':40} {'rms':>6} {'kurt':>5} {'LA':>6}  octaves(loudest 2 s, re 1 kHz) " + " ".join(f"{c:>6}" for c in OCT))
    for p in paths:
        x, fs = sf.read(p, dtype='float64')
        r = measure(x, fs, passby=True)
        ref = r['oct'][3]
        print(f"{os.path.basename(p)[:40]:40} {r['rms']:6.1f} {r['kurt']:5.2f} {r['la']:6.1f}  " + " ".join(f"{v - ref:+6.1f}" for v in r['oct']))


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    if sys.argv[1] == 'levels':
        levels(sys.argv[2])
    elif sys.argv[1] == 'game':
        cut = sys.argv[sys.argv.index('--cut') + 1] if '--cut' in sys.argv else None
        game(sys.argv[2], cut)
    else:
        files(sys.argv[2:])
