#!/usr/bin/env python3
"""Sound-texture statistics after McDermott and Simoncelli (2011), for fitting synthesised textures
(fountain, rain, leaves, fire) to recordings.

McDermott, J. H. and Simoncelli, E. P. (2011). Sound texture perception via statistics consistent with
peripheral auditory processing. Neuron 71, 926-940. Listeners recognise a texture from a small set of
time-averaged statistics of a cochlear model, and synthetic sounds that share them sound like the real
thing. Gaussian noise with the right spectrum does not: its band envelopes are too steady.

The model here (the same as OpenFPS.Client.Core/AudioEngine/Core/Nature/TextureStatistics.cs, which the
tests and the AudioLab use; keep the two in step):
  * 30 bands, half-cosine filters equally spaced on the ERB-number scale from 50 Hz to 16 kHz (capped
    below 0.45 of the sample rate), applied in the frequency domain.
  * Everything is analysed at 48 kHz (a file at another rate is resampled first, polyphase).
  * Each band's envelope is the magnitude of its analytic signal, low-passed by two 120-sample moving
    averages (a triangle; first null at 400 Hz), taken every 120th sample (400 Hz), then compressed by a
    power of 0.3 (the cochlea's compression). Two box filters rather than a long FIR so the C# twin is
    cheap and identical.
  * Per band: mean, coefficient of variation (std / mean), skewness and kurtosis of the compressed
    envelope.
  * Cross-band: the correlation between band envelopes, averaged at band spacings 1-2 (neighbours),
    5-10 (an octave or two apart) and 15+ (far).
  * Modulation: the envelope's power in octave bands centred 0.5, 1, 2 ... 128 Hz (half-cosine on
    log frequency), each as a share of the envelope's variance.

Summary features (what fits and tests use): the band statistics averaged over four regions
(200-1000 Hz, 1-3 kHz, 3-6 kHz, 6-12 kHz; 12-16 kHz is printed but not used, because the fountain,
fire and wind references are 128 kbps MP3 previews that are low-passed near 16 kHz), the three
correlation summaries, and the modulation shares over 1-12 kHz grouped as slow (0.5-2 Hz),
syllabic (4-16 Hz) and fast (32-128 Hz).

    texture_stats.py stats FILE...                       per-band table for each file
    texture_stats.py compare REF... -- MODEL...          summary features: references' range, each model
                                                         against it (* = outside), Gaussian noise of the
                                                         model's own spectrum for scale
    texture_stats.py json FILE...                        summary features as JSON (for tests and READMEs)
    texture_stats.py wave FILE...                        inside 10 ms: the 4-16 kHz waveform's kurtosis and crest

Options: sec=30 (seconds read, from offset), off=0, hp=0 (high-pass Hz before analysis).
Needs numpy, scipy, soundfile (/home/cody/drumsynth/venv/bin/python).
"""
import json
import os
import sys

import numpy as np
import soundfile as sf
from scipy.signal import butter, sosfilt, resample_poly

N_BANDS = 30
LO_HZ, HI_HZ = 50.0, 16000.0
RATE = 48000
ENV_RATE = 400
HOP = RATE // ENV_RATE
COMPRESS = 0.3
MOD_CENTRES = [0.5 * 2 ** k for k in range(9)]  # 0.5 .. 128 Hz
REGIONS = [("0.2-1k", 200, 1000), ("1-3k", 1000, 3000), ("3-6k", 3000, 6000), ("6-12k", 6000, 12000)]
EXTRA_REGION = ("12-16k", 12000, 16000)


def erb_number(f):
    return 21.4 * np.log10(1 + 0.00437 * f)


def erb_to_hz(e):
    return (10 ** (e / 21.4) - 1) / 0.00437


def band_centres(sr):
    hi = min(HI_HZ, 0.45 * sr)
    e = np.linspace(erb_number(LO_HZ), erb_number(hi), N_BANDS)
    return erb_to_hz(e), e


def read(path, sec=30.0, off=0.0):
    x, sr = sf.read(path, dtype="float64", always_2d=True)
    a = int(off * sr)
    x = x[a:a + int(sec * sr)].mean(axis=1)
    if sr != RATE:
        g = np.gcd(RATE, sr)
        x = resample_poly(x, RATE // g, sr // g)
    return x, RATE


def box(x, w):
    """A centred running mean of w samples (the C# twin computes it the same way)."""
    c = np.concatenate(([0.0], np.cumsum(x)))
    out = np.empty_like(x)
    h = w // 2
    i = np.arange(len(x))
    lo = np.clip(i - h, 0, len(x))
    hi = np.clip(i - h + w, 0, len(x))
    out[:] = (c[hi] - c[lo]) / w
    return out


def decimate(env):
    """Low-pass a 48 kHz envelope by two moving averages and take every HOP-th sample."""
    return box(box(env, HOP), HOP)[::HOP]


def analyse(x, sr, hp=0.0):
    """The statistics of one signal. Returns a dict of arrays."""
    if hp:
        x = sosfilt(butter(4, hp, btype="high", fs=sr, output="sos"), x)
    n = len(x)
    nfft = 1 << int(np.ceil(np.log2(n)))
    X = np.fft.rfft(x, nfft)
    f = np.fft.rfftfreq(nfft, 1 / sr)
    centres, e_centres = band_centres(sr)
    spacing = e_centres[1] - e_centres[0]
    ef = erb_number(np.maximum(f, 1e-6))
    assert sr == RATE, "analyse() wants 48 kHz; read() resamples"
    envs, power = [], []
    for k in range(N_BANDS):
        # Half-cosine on the ERB scale, one spacing either side of the centre: neighbours overlap by
        # half, and the squared responses sum to about one across the bank.
        d = (ef - e_centres[k]) / spacing
        h = np.where(np.abs(d) < 1, np.cos(d * np.pi / 2), 0.0)
        B = X * h
        power.append(np.sum(np.abs(B) ** 2))
        # Analytic signal: the one-sided spectrum doubled, inverse transformed.
        full = np.zeros(nfft, dtype=complex)
        full[:len(B)] = B
        full[1:len(B) - 1] *= 2
        env = np.abs(np.fft.ifft(full))[:n]
        envs.append(np.maximum(decimate(env), 0) ** COMPRESS)
    E = np.array(envs)
    # Drop a quarter-second each end: filter edges.
    cut = ENV_RATE // 4
    E = E[:, cut:-cut] if E.shape[1] > 4 * cut else E
    mean = E.mean(axis=1)
    sd = E.std(axis=1)
    z = (E - mean[:, None]) / (sd[:, None] + 1e-30)
    skew = (z ** 3).mean(axis=1)
    kurt = (z ** 4).mean(axis=1)
    cv = sd / (mean + 1e-30)
    C = np.corrcoef(z)
    # Modulation power, octave bands on the envelope.
    m = E.shape[1]
    m2 = 1 << int(np.ceil(np.log2(m)))
    Z = np.fft.rfft(E - mean[:, None], m2, axis=1)
    fm = np.fft.rfftfreq(m2, 1 / ENV_RATE)
    var = (np.abs(Z) ** 2).sum(axis=1) + 1e-30
    mod = np.zeros((N_BANDS, len(MOD_CENTRES)))
    for j, c in enumerate(MOD_CENTRES):
        d = np.log2(np.maximum(fm, 1e-6) / c)
        h = np.where(np.abs(d) < 1, np.cos(d * np.pi / 2), 0.0)
        mod[:, j] = (np.abs(Z) ** 2 * h ** 2).sum(axis=1) / var
    return {"centres": centres, "power": np.array(power), "mean": mean, "cv": cv, "skew": skew,
            "kurt": kurt, "corr": C, "mod": mod, "rate": sr}


def summary(a):
    """The features fits and tests compare."""
    c = a["centres"]
    out = {}
    for name, lo, hi in REGIONS + [EXTRA_REGION]:
        sel = (c >= lo) & (c < hi)
        if not sel.any():
            continue
        out[f"cv {name}"] = float(a["cv"][sel].mean())
        out[f"skew {name}"] = float(a["skew"][sel].mean())
        out[f"kurt {name}"] = float(a["kurt"][sel].mean())
    C = a["corr"]
    n = C.shape[0]
    usable = c < 12000

    def offsets(lo, hi):
        vals = [C[i, i + d] for d in range(lo, hi + 1) for i in range(n - d) if usable[i] and usable[i + d] and c[i] >= 200]
        return float(np.mean(vals)) if vals else float("nan")

    out["corr near"] = offsets(1, 2)
    out["corr octave"] = offsets(5, 10)
    out["corr far"] = offsets(15, 29)
    sel = (c >= 1000) & (c < 12000)
    mod = a["mod"][sel].mean(axis=0)
    out["mod slow"] = float(mod[0:3].sum())   # 0.5, 1, 2 Hz
    out["mod mid"] = float(mod[3:6].sum())    # 4, 8, 16 Hz
    out["mod fast"] = float(mod[6:9].sum())   # 32, 64, 128 Hz
    return out


def waveform(x, sr, lo=4000.0, hi=16000.0, ms=10.0):
    """What happens INSIDE 10 ms, which the envelope statistics (2.5 ms envelope, compressed) cannot
    see: the 4-16 kHz band's waveform kurtosis and crest per 10 ms window. Gaussian noise is 3 and
    about 9-10 dB; a window holding a few needle-sharp clicks is far more. Windows more than 40 dB
    under the median window's level are skipped (silence). Returns (mean kurtosis, median kurtosis,
    median crest dB, 95th percentile crest dB)."""
    hi = min(hi, 0.45 * sr)
    y = sosfilt(butter(4, [lo, hi], btype="band", fs=sr, output="sos"), x)
    w = int(sr * ms / 1000)
    m = len(y) // w
    Y = y[: m * w].reshape(m, w)
    m2 = (Y ** 2).mean(axis=1)
    keep = m2 > np.median(m2) * 1e-4
    Y, m2 = Y[keep], m2[keep]
    k = (Y ** 4).mean(axis=1) / (m2 ** 2 + 1e-60)
    crest = 20 * np.log10(np.abs(Y).max(axis=1) / np.sqrt(m2 + 1e-60))
    return float(k.mean()), float(np.median(k)), float(np.median(crest)), float(np.percentile(crest, 95))


def phase_randomised(x, seed=1):
    """Gaussian noise with exactly this signal's long-term spectrum."""
    X = np.fft.rfft(x)
    rng = np.random.default_rng(seed)
    ph = np.exp(2j * np.pi * rng.random(len(X)))
    ph[0] = 1
    return np.fft.irfft(np.abs(X) * ph, len(x))


def stats_cmd(paths, opts):
    for p in paths:
        x, sr = read(p, opts["sec"], opts["off"])
        a = analyse(x, sr, opts["hp"])
        print(f"== {os.path.basename(p)}  ({sr} Hz, {len(x) / sr:.1f} s)")
        print("    band Hz   level  mean    cv   skew   kurt   mod 0.5 1 2 4 8 16 32 64 128 (share of variance)")
        lv = 10 * np.log10(a["power"] / a["power"].max() + 1e-30)
        for k in range(N_BANDS):
            print(f"  {a['centres'][k]:8.0f} {lv[k]:6.1f} {a['mean'][k]:6.3f} {a['cv'][k]:5.2f} {a['skew'][k]:6.2f} {a['kurt'][k]:6.2f}   "
                  + " ".join(f"{v:.2f}" for v in a["mod"][k]))
        s = summary(a)
        print("  summary: " + ", ".join(f"{k} {v:.3f}" for k, v in s.items()))


FEATURES_SHOWN = None


def compare_cmd(refs, models, opts):
    rs = []
    for p in refs:
        x, sr = read(p, opts["sec"], opts["off"])
        rs.append((os.path.basename(p), summary(analyse(x, sr, opts["hp"]))))
    ms = []
    for p in models:
        x, sr = read(p, opts["sec"], opts["off"])
        ms.append((os.path.basename(p), summary(analyse(x, sr, opts["hp"]))))
        ms.append(("  (noise, same spectrum)", summary(analyse(phase_randomised(x), sr, opts["hp"]))))
    keys = list(rs[0][1].keys())
    width = max(len(n) for n, _ in rs + ms) + 2
    print(" " * width + " ".join(f"{k:>12}" for k in keys))
    for n, s in rs:
        print(f"{n:<{width}}" + " ".join(f"{s.get(k, float('nan')):12.3f}" for k in keys))
    lo = {k: min(s.get(k, np.inf) for _, s in rs) for k in keys}
    hi = {k: max(s.get(k, -np.inf) for _, s in rs) for k in keys}
    print(f"{'refs min':<{width}}" + " ".join(f"{lo[k]:12.3f}" for k in keys))
    print(f"{'refs max':<{width}}" + " ".join(f"{hi[k]:12.3f}" for k in keys))
    for n, s in ms:
        cells = []
        inside = 0
        for k in keys:
            v = s.get(k, float("nan"))
            ok = lo[k] <= v <= hi[k]
            inside += ok
            cells.append(f"{v:11.3f}{' ' if ok else '*'}")
        print(f"{n:<{width}}" + " ".join(cells) + f"   {inside}/{len(keys)} inside")


def json_cmd(paths, opts):
    out = {}
    for p in paths:
        x, sr = read(p, opts["sec"], opts["off"])
        out[os.path.basename(p)] = summary(analyse(x, sr, opts["hp"]))
    print(json.dumps(out, indent=1))


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        sys.exit(1)
    opts = {"sec": 30.0, "off": 0.0, "hp": 0.0}
    rest = []
    for a in args[1:]:
        if "=" in a and a.split("=")[0] in opts:
            opts[a.split("=")[0]] = float(a.split("=")[1])
        else:
            rest.append(a)
    cmd = args[0]
    if cmd == "stats":
        stats_cmd(rest, opts)
    elif cmd == "compare":
        i = rest.index("--")
        compare_cmd(rest[:i], rest[i + 1:], opts)
    elif cmd == "wave":
        for p in rest:
            x, sr = read(p, opts["sec"], opts["off"])
            if opts["hp"]:
                x = sosfilt(butter(4, opts["hp"], btype="high", fs=sr, output="sos"), x)
            km, kmed, c50, c95 = waveform(x, sr)
            print(f"{os.path.basename(p):48} 4-16 kHz in 10 ms: kurtosis mean {km:5.2f} median {kmed:5.2f}, crest median {c50:5.1f} dB, 95th pct {c95:5.1f} dB")
    elif cmd == "json":
        json_cmd(rest, opts)
    else:
        print(__doc__)
        sys.exit(1)
