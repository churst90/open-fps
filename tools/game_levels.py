#!/usr/bin/env python3
"""Measure what the game's mixer put out, one scene at a time (AudioLab --game-levels).

    game_levels.py DIR [--cut OUTDIR]  [--json FILE]

Reads DIR/capture.wav (the game's own output, full scale) and DIR/segments.csv, and for each
segment reports, over its middle (the first 0.6 s and the last 0.2 s are left out, for the capture's
latency and the scene's settling):

  rms      unweighted RMS, dBFS (both ears' power averaged)
  peak     sample peak, dBFS; clip = samples at or over 0.999
  lufs     integrated loudness, BS.1770-4 (K-weighted, gated), LUFS
  laeq     A-weighted RMS, dBFS(A)
  lafmax   A-weighted, 125 ms exponential (fast), the highest, dBFS(A)
  l60      weighted by the inverse ISO 226:2003 60-phon contour (how loud it is at a moderate
           listening level, where the bass counts for more than A-weighting gives it), dBFS
  <100Hz   share of the unweighted power below 100 Hz, percent
  gap      the longest run of 50 ms blocks under -80 dBFS inside the segment, seconds

With --cut, writes each segment (whole, unscaled) to OUTDIR/<name>.wav.
"""
import csv
import json
import os
import sys

import numpy as np
import soundfile as sf
from scipy import signal

HEAD, TAIL = 0.6, 0.2


def a_weighting(fs):
    f1, f2, f3, f4 = 20.598997, 107.65265, 737.86223, 12194.217
    a1000 = 1.9997
    nums = [(2 * np.pi * f4) ** 2 * (10 ** (a1000 / 20)), 0, 0, 0, 0]
    dens = np.polymul([1, 4 * np.pi * f4, (2 * np.pi * f4) ** 2], [1, 4 * np.pi * f1, (2 * np.pi * f1) ** 2])
    dens = np.polymul(np.polymul(dens, [1, 2 * np.pi * f3]), [1, 2 * np.pi * f2])
    return signal.bilinear(nums, dens, fs)


def k_weighting_48k():
    pre = ([1.53512485958697, -2.69169618940638, 1.19839281085285], [1.0, -1.69065929318241, 0.73248077421585])
    rlb = ([1.0, -2.0, 1.0], [1.0, -1.99004745483398, 0.99007225036621])
    return pre, rlb


def lufs(x, fs):
    """BS.1770-4 integrated loudness of a stereo signal (N x 2)."""
    y = signal.resample_poly(x, 160, 147, axis=0) if fs == 44100 else x
    pre, rlb = k_weighting_48k()
    y = signal.lfilter(*pre, y, axis=0)
    y = signal.lfilter(*rlb, y, axis=0)
    n, hop = int(0.4 * 48000), int(0.1 * 48000)
    blocks = []
    for s in range(0, len(y) - n + 1, hop):
        z = np.mean(y[s:s + n] ** 2, axis=0).sum()
        blocks.append(z)
    blocks = np.array(blocks)
    if len(blocks) == 0:
        return float('nan')
    l = -0.691 + 10 * np.log10(blocks + 1e-30)
    g = blocks[l > -70]
    if len(g) == 0:
        return float('nan')
    rel = -0.691 + 10 * np.log10(g.mean()) - 10
    g2 = g[(-0.691 + 10 * np.log10(g)) > rel]
    return -0.691 + 10 * np.log10(g2.mean()) if len(g2) else float('nan')


ISO_F = np.array([20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000,
                  1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500])
ISO_AF = np.array([0.532, 0.506, 0.480, 0.455, 0.432, 0.409, 0.387, 0.367, 0.349, 0.330, 0.315, 0.301, 0.288,
                   0.276, 0.267, 0.259, 0.253, 0.250, 0.246, 0.244, 0.243, 0.243, 0.243, 0.242, 0.242, 0.245,
                   0.254, 0.271, 0.301])
ISO_LU = np.array([-31.6, -27.2, -23.0, -19.1, -15.9, -13.0, -10.3, -8.1, -6.2, -4.5, -3.1, -2.0, -1.1, -0.4,
                   0.0, 0.3, 0.5, 0.0, -2.7, -4.1, -1.0, 1.7, 2.5, 1.2, -2.1, -7.1, -11.2, -10.7, -3.1])
ISO_TF = np.array([78.5, 68.7, 59.5, 51.1, 44.0, 37.5, 31.5, 26.5, 22.1, 17.9, 14.4, 11.4, 8.6, 6.2, 4.4, 3.0,
                   2.2, 2.4, 3.5, 1.7, -1.3, -4.2, -6.0, -5.4, -1.5, 6.0, 12.6, 13.9, 12.3])


def iso226(phon):
    af = 4.47e-3 * (10 ** (0.025 * phon) - 1.15) + (0.4 * 10 ** (((ISO_TF + ISO_LU) / 10) - 9)) ** ISO_AF
    return (10 / ISO_AF) * np.log10(af) - ISO_LU + 94


def contour_weight_db(freqs, phon=60):
    """A weighting that is the inverse of the equal-loudness contour, 0 dB at 1 kHz."""
    c = iso226(phon)
    w = -(c - c[ISO_F == 1000][0])
    lf = np.log10(np.clip(freqs, 20, 12500))
    out = np.interp(lf, np.log10(ISO_F), w)
    out[freqs < 20] = -60
    out[freqs > 16000] = -60
    return out


def measure(x, fs):
    mono_power = np.mean(x ** 2, axis=1)
    rms = 10 * np.log10(mono_power.mean() + 1e-30)
    peak = 20 * np.log10(np.abs(x).max() + 1e-30)
    clip = int((np.abs(x) >= 0.999).sum())
    b, a = a_weighting(fs)
    xa = signal.lfilter(b, a, x, axis=0)
    pa = np.mean(xa ** 2, axis=1)
    laeq = 10 * np.log10(pa.mean() + 1e-30)
    # fast: 125 ms exponential
    alpha = 1 - np.exp(-1 / (0.125 * fs))
    env = signal.lfilter([alpha], [1, alpha - 1], pa)
    lafmax = 10 * np.log10(env[int(0.125 * fs):].max() + 1e-30) if len(env) > fs // 4 else float('nan')
    # spectrum
    f, p = signal.welch(x, fs, nperseg=8192, axis=0)
    p = p.mean(axis=1)
    total = p.sum()
    below = p[f < 100].sum() / max(total, 1e-30) * 100
    w = contour_weight_db(f, 60)
    l60 = 10 * np.log10((p * 10 ** (w / 10)).sum() / max(total, 1e-30) * mono_power.mean() + 1e-30)
    # gaps
    blk = int(0.05 * fs)
    nb = len(mono_power) // blk
    lv = 10 * np.log10(mono_power[:nb * blk].reshape(nb, blk).mean(axis=1) + 1e-30)
    run = best = 0
    for v in lv:
        run = run + 1 if v < -80 else 0
        best = max(best, run)
    bands = {}
    for c in [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000]:
        sel = (f >= c / np.sqrt(2)) & (f < c * np.sqrt(2))
        bands[str(c)] = 10 * np.log10(p[sel].sum() / max(total, 1e-30) * mono_power.mean() + 1e-30)
    return dict(rms=rms, peak=peak, clip=clip, lufs=lufs(x, fs), laeq=laeq, lafmax=lafmax, l60=l60,
                below100=below, gap=best * 0.05, bands=bands)


def main():
    d = sys.argv[1]
    cut = sys.argv[sys.argv.index('--cut') + 1] if '--cut' in sys.argv else None
    jout = sys.argv[sys.argv.index('--json') + 1] if '--json' in sys.argv else None
    x, fs = sf.read(os.path.join(d, 'capture.wav'), dtype='float64', always_2d=True)
    rows = list(csv.DictReader(open(os.path.join(d, 'segments.csv'))))
    if cut:
        os.makedirs(cut, exist_ok=True)
    print(f"{'segment':42s} {'rms':>6s} {'peak':>6s} {'clip':>4s} {'lufs':>6s} {'laeq':>6s} {'lafmax':>6s} {'l60':>6s} {'<100':>5s} {'gap':>4s}")
    out = {}
    for r in rows:
        s, n = float(r['start']), float(r['seconds'])
        a, b = int((s + HEAD) * fs), int((s + n - TAIL) * fs)
        seg = x[a:b]
        if len(seg) < fs // 2:
            continue
        m = measure(seg, fs)
        out[r['name']] = m
        print(f"{r['name']:42s} {m['rms']:6.1f} {m['peak']:6.1f} {m['clip']:4d} {m['lufs']:6.1f} {m['laeq']:6.1f} "
              f"{m['lafmax']:6.1f} {m['l60']:6.1f} {m['below100']:5.0f} {m['gap']:4.1f}")
        if cut:
            name = ''.join(c if c.isalnum() or c in '-_.' else '_' for c in r['name'])
            sf.write(os.path.join(cut, name + '.wav'), x[int(s * fs):int((s + n) * fs)], fs, subtype='FLOAT')
    if jout:
        json.dump(out, open(jout, 'w'), indent=1)


if __name__ == '__main__':
    main()
