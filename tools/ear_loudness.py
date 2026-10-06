#!/usr/bin/env python3
"""Loudness of the game's output, and the ear model's before/after (docs/EAR_MODEL.md).

    ear_loudness.py selftest
    ear_loudness.py compare BEFORE_DIR AFTER_DIR [--speech-db 56.3] [--cut OUTDIR] [--json FILE]

BEFORE_DIR and AFTER_DIR are AudioLab --game-levels captures (capture.wav, segments.csv), made with
ear=off and ear=on. For each segment, over its middle (as tools/game_levels.py), in each capture:

  rms     unweighted RMS, dBFS          lufs   BS.1770 integrated loudness
  laeq    A-weighted RMS, dBFS(A)       <100   share of power below 100 Hz, percent
  phon    ISO 532-1 (Zwicker, stationary, free field) loudness level of the long-term one-third-octave
          spectrum, with the output mapped to dB SPL so that the 'before' talker at 2 m reads
          --speech-db (56.3: ANSI S3.5 normal effort, 62.35 dB at 1 m, at 2 m). The binaural output
          is read as it is (the HRTF's colour included), both ears' power averaged.
  peak    sample peak, dBFS; clip = samples at or over 0.999; gap = longest run of 50 ms under -80 dBFS
  low/hi  the 63 Hz and 8 kHz octave bands relative to the 1 kHz octave, dB (the compensation's work)

and the change after - before. An independent port of the standard's procedure, not the game's C#:
'selftest' checks it against ISO 532-1 annex B.2 (83.296 sone).
"""
import csv
import json
import os
import sys

import numpy as np
import soundfile as sf

sys.path.insert(0, os.path.dirname(__file__))
import game_levels as gl  # noqa: E402

FC = np.array([25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
               2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500.])
RAP = [45, 55, 65, 71, 80, 90, 100, 120]
DLL = [[-32, -24, -16, -10, -5, 0, -7, -3, 0, -2, 0], [-29, -22, -15, -10, -4, 0, -7, -2, 0, -2, 0],
       [-27, -19, -14, -9, -4, 0, -6, -2, 0, -2, 0], [-25, -17, -12, -9, -3, 0, -5, -2, 0, -2, 0],
       [-23, -16, -11, -7, -3, 0, -4, -1, 0, -1, 0], [-20, -14, -10, -6, -3, 0, -4, -1, 0, -1, 0],
       [-18, -12, -9, -6, -2, 0, -3, -1, 0, -1, 0], [-15, -10, -8, -4, -2, 0, -3, -1, 0, -1, 0]]
LTQ = [30, 18, 12, 8, 7, 6, 5, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3]
A0 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -0.5, -1.6, -3.2, -5.4, -5.6, -4, -1.5, 2, 5, 12]
DCB = [-0.25, -0.6, -0.8, -0.8, -0.5, 0, 0.5, 1.1, 1.5, 1.7, 1.8, 1.8, 1.7, 1.6, 1.4, 1.2, 0.8, 0.5, 0, -0.5]
ZUP = [0.9, 1.8, 2.8, 3.5, 4.4, 5.4, 6.6, 7.9, 9.2, 10.6, 12.3, 13.8, 15.2, 16.7, 18.1, 19.3, 20.6, 21.8, 22.7, 23.6, 24.0]
RNS = [21.5, 18, 15.1, 11.5, 9, 6.1, 4.4, 3.1, 2.13, 1.36, 0.82, 0.42, 0.30, 0.22, 0.15, 0.10, 0.035, 0]
USL = [[13, 8.2, 6.3, 5.5, 5.5, 5.5, 5.5, 5.5], [9, 7.5, 6, 5.1, 4.5, 4.5, 4.5, 4.5],
       [7.8, 6.7, 5.6, 4.9, 4.4, 3.9, 3.9, 3.9], [6.2, 5.4, 4.6, 4.0, 3.5, 3.2, 3.2, 3.2],
       [4.5, 3.8, 3.6, 3.2, 2.9, 2.7, 2.7, 2.7], [3.7, 3.0, 2.8, 2.35, 2.2, 2.2, 2.2, 2.2],
       [2.9, 2.3, 2.1, 1.9, 1.8, 1.7, 1.7, 1.7], [2.4, 1.7, 1.5, 1.35, 1.3, 1.3, 1.3, 1.3],
       [1.95, 1.45, 1.3, 1.15, 1.1, 1.1, 1.1, 1.1], [1.5, 1.2, 0.94, 0.86, 0.82, 0.82, 0.82, 0.82],
       [0.72, 0.67, 0.64, 0.63, 0.62, 0.62, 0.62, 0.62], [0.59, 0.53, 0.51, 0.50, 0.42, 0.42, 0.42, 0.42],
       [0.40, 0.33, 0.26, 0.24, 0.24, 0.22, 0.22, 0.22], [0.27, 0.21, 0.20, 0.18, 0.17, 0.17, 0.17, 0.17],
       [0.16, 0.15, 0.14, 0.12, 0.11, 0.11, 0.11, 0.11], [0.12, 0.11, 0.10, 0.08, 0.08, 0.08, 0.08, 0.08],
       [0.09, 0.08, 0.07, 0.06, 0.06, 0.06, 0.06, 0.05], [0.06, 0.05, 0.03, 0.02, 0.02, 0.02, 0.02, 0.02]]


def sones(levels):
    """ISO 532-1 stationary loudness, free field, from 28 one-third-octave levels (25 Hz-12.5 kHz)."""
    lv = [min(max(float(x), -100.0), 160.0) for x in levels]
    ti = []
    for i in range(11):
        j = 0
        while j < 7 and lv[i] > RAP[j] - DLL[j][i]:
            j += 1
        ti.append(10 ** ((lv[i] + DLL[j][i]) / 10))
    g = [sum(ti[0:6]), sum(ti[6:9]), sum(ti[9:11])]
    nm = []
    for i in range(20):
        le = (10 * np.log10(g[i]) if g[i] > 0 else -100.0) if i < 3 else lv[i + 8]
        le -= A0[i]
        n = 0.0
        if le > LTQ[i]:
            le -= DCB[i]
            n = max(0.0, 0.0635 * 10 ** (0.025 * LTQ[i]) * ((0.75 + 0.25 * 10 ** ((le - LTQ[i]) / 10)) ** 0.25 - 1))
        nm.append(n)
    nm.append(0.0)
    korry = 0.4 + 0.32 * nm[0] ** 0.2
    if korry <= 1:
        nm[0] *= korry
    total, z1, n1, n2, r = 0.0, 0.0, 0.0, 0.0, 17
    for i in range(21):
        zup = ZUP[i] + 0.0001
        ig = max(min(i - 1, 7), 0)
        guard = 0
        while z1 < zup and guard < 64:
            guard += 1
            if n1 <= nm[i]:
                if n1 < nm[i]:
                    r = 0
                    while r < 17 and RNS[r] > nm[i]:
                        r += 1
                z2, n2 = zup, nm[i]
                total += n2 * (z2 - z1)
            else:
                n2 = max(RNS[r], nm[i])
                dz = (n1 - n2) / USL[r][ig]
                z2 = z1 + dz
                if z2 > zup:
                    z2 = zup
                    dz = z2 - z1
                    n2 = n1 - dz * USL[r][ig]
                total += dz * (n1 + n2) / 2
            while r < 17 and n2 <= RNS[r]:
                r += 1
            z1, n1 = z2, n2
    return max(total, 0.0)


def phons(n):
    return 40 + 10 * np.log2(n) if n >= 1 else 40 * (n + 0.0005) ** 0.35


def third_octaves(x, fs):
    """Mean-square band powers, bins weighted by an order-3 one-third-octave band shape."""
    from scipy import signal
    f, p = signal.welch(x, fs, nperseg=min(len(x), 32768))
    df = f[1] - f[0]
    q = 1 / (2 ** (1 / 6) - 2 ** (-1 / 6))
    out = np.zeros(28)
    ff = np.maximum(f, 1e-9)
    for i, c in enumerate(FC):
        w = 1 / (1 + (q * (ff / c - c / ff)) ** 6)
        out[i] = np.sum(p * w) * df
    return out


def segment_figures(x, fs, offset_db):
    m = gl.measure(x, fs)
    mono = np.sqrt(np.mean(x ** 2, axis=1)) * np.sign(x[:, 0] + 1e-30)
    bands = (third_octaves(x[:, 0], fs) + third_octaves(x[:, 1], fs)) / 2
    levels = 10 * np.log10(bands + 1e-30) + offset_db
    n = sones(levels)
    oct_ = m['bands']
    k1 = oct_['1000']
    return dict(rms=m['rms'], lufs=m['lufs'], laeq=m['laeq'], below100=m['below100'], peak=m['peak'],
                clip=m['clip'], gap=m['gap'], sone=n, phon=phons(n), low=oct_['63'] - k1, hi=oct_['8000'] - k1)


def segments(d):
    x, fs = sf.read(os.path.join(d, 'capture.wav'), dtype='float64', always_2d=True)
    rows = list(csv.DictReader(open(os.path.join(d, 'segments.csv'))))
    end = max(float(r['start']) + float(r['seconds']) for r in rows)
    rate = (len(x) / fs) / end
    out = {}
    for r in rows:
        s, n = float(r['start']) * rate, float(r['seconds']) * rate
        a, b = int((s + gl.HEAD) * fs), int((s + n - gl.TAIL) * fs)
        whole = x[int(s * fs):int((s + n - gl.TAIL) * fs)]
        out[r['name']] = (x[a:b], whole)
    return out, fs


def compare(before, after, speech_db, cut, jout):
    bs, fs = segments(before)
    as_, fs2 = segments(after)
    talker = next(k for k in bs if k.startswith('speech'))
    # The mapping from the output to the ear: the 'before' talker at 2 m reads speech_db, unweighted.
    offset = speech_db - gl.measure(bs[talker][0], fs)['rms']
    print(f"output mapped so the before talker reads {speech_db} dB: 0 dBFS RMS = {offset:.1f} dB SPL")
    hdr = f"{'segment':34s} {'rms':>12s} {'lufs':>12s} {'dB(A)':>12s} {'phon':>12s} {'63Hz/1k':>12s} {'8k/1k':>12s} {'<100%':>9s} {'peak':>6s} {'clip':>4s} {'gap':>4s}"
    print(hdr)
    table = {}
    for name in bs:
        if name.startswith('silence') or name not in as_:
            continue
        b = segment_figures(bs[name][0], fs, offset)
        a = segment_figures(as_[name][0], fs2, offset)
        table[name] = dict(before=b, after=a)

        def pair(k, f='{:6.1f}'):
            return f"{f.format(b[k])}>{f.format(a[k]).strip():>5s}"
        print(f"{name:34s} {pair('rms')} {pair('lufs')} {pair('laeq')} {pair('phon')} {pair('low')} {pair('hi')} "
              f"{b['below100']:4.0f}>{a['below100']:3.0f} {a['peak']:6.1f} {a['clip']:4d} {a['gap']:4.1f}")
        if cut:
            for tag, segs, rate in (('before', bs, fs), ('after', as_, fs2)):
                os.makedirs(os.path.join(cut, tag), exist_ok=True)
                fn = ''.join(c if c.isalnum() or c in '-_.' else '_' for c in name)
                sf.write(os.path.join(cut, tag, fn + '.wav'), segs[name][1], rate, subtype='PCM_24')
    if jout:
        json.dump(dict(offset=offset, table=table), open(jout, 'w'), indent=1)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == 'selftest':
        n = sones([-60, -60, 78, 79, 89, 72, 80, 89, 75, 87, 85, 79, 86, 80, 71, 70, 72, 71, 72, 74, 69, 65, 67, 77, 68, 58, 45, 30])
        print(f"ISO 532-1 annex B.2 test signal 1: {n:.3f} sone (standard 83.296)")
        sys.exit(0 if abs(n - 83.296) < 0.05 * 83.296 else 1)
    if len(sys.argv) >= 4 and sys.argv[1] == 'compare':
        args = sys.argv[4:]

        def opt(k, d=None):
            return args[args.index(k) + 1] if k in args else d
        compare(sys.argv[2], sys.argv[3], float(opt('--speech-db', 56.3)), opt('--cut'), opt('--json'))
        return
    print(__doc__)


if __name__ == '__main__':
    main()
