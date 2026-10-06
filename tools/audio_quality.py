#!/usr/bin/env python3
"""Measures the captures the AudioLab's --quality mode writes (OpenFPS.AudioLab/Spikes/QualitySpike.cs).

    audio_quality.py resampler DIR    tone purity (THD+N), droop and aliasing per resampler
    audio_quality.py orbit DIR        steps at the binaural stage's 1024-sample frames, per orbit rate
    audio_quality.py scene PRE POST   what the master limiter does: gain reduction, clipping, distortion
    audio_quality.py scenes DIR       the same for every scene-*.pre.wav / .post.wav pair in DIR
    audio_quality.py blocks FILE...   steps locked to the 1024-sample mixer block (a profile of |2nd difference|)
    audio_quality.py jumps FILE...    discontinuities: second-difference spikes far over the local level
    audio_quality.py texture FILE...  spectrum and band-envelope statistics (what a texture is made of)
    audio_quality.py check FILE...    level, peak, clipping and silent gaps of files given to a listener
    audio_quality.py limiter DIR TAG  the master limiter on --quality limiter's tones: THD+N, IMD, true peak, recovery
    audio_quality.py flattops FILE... runs of identical samples near the top: what a limiter without look-ahead leaves

Needs numpy and scipy (/home/cody/drumsynth/venv/bin/python). See docs/AUDIO_QUALITY_2026-10-06.md.
"""
import csv
import os
import sys

import numpy as np
import scipy.io.wavfile as wavfile
from scipy.signal import get_window, welch, butter, sosfilt


def read(path):
    sr, x = wavfile.read(path)
    if x.dtype == np.int16:
        x = x.astype(np.float32) / 32768.0
    elif x.dtype == np.int32:
        x = x.astype(np.float32) / 2147483648.0
    x = x.astype(np.float64)
    if x.ndim == 1:
        x = x[:, None]
    return sr, x


def db(v):
    return 10 * np.log10(np.maximum(v, 1e-30))


def envelope(x, sr, ms=10):
    n = int(sr * ms / 1000)
    m = len(x) // n * n
    return np.sqrt(np.mean(x[:m].reshape(-1, n) ** 2, axis=1)), n


def first_onset(x, sr, t_from, t_to, rel_db=-40):
    """First time after t_from where the 10 ms level comes within rel_db of the window's loudest."""
    a, b = int(t_from * sr), int(t_to * sr)
    e, n = envelope(x[max(0, a):b], sr)
    if len(e) == 0 or e.max() <= 0:
        return None
    on = np.flatnonzero(db(e ** 2) > db(e.max() ** 2) + rel_db)
    return (max(0, a) + on[0] * n) / sr if len(on) else None


def tone_metrics(seg, sr, f):
    """THD+N of a segment that should hold one tone at f: everything that is not the tone, against
    the tone, 20 Hz-20 kHz. Also the strongest spurious line."""
    n = len(seg)
    w = get_window("blackmanharris", n)
    spec = np.abs(np.fft.rfft(seg * w)) ** 2
    freqs = np.fft.rfftfreq(n, 1 / sr)
    band = (freqs >= 20) & (freqs <= 20000)
    near = np.abs(freqs - f) <= 8 * sr / n
    tone = spec[near].sum()
    rest_mask = band & ~near
    rest = spec[rest_mask].sum()
    k = np.argmax(np.where(rest_mask, spec, 0))
    return tone, rest, freqs[k], spec[k]


def resampler(dirname):
    rows = []
    for name in sorted(os.listdir(dirname)):
        if not (name.startswith("resampler-") and name.endswith(".post.wav")):
            continue
        method = name[len("resampler-"):-len(".post.wav")]
        sr, x = read(os.path.join(dirname, name))
        left = x[:, 0]
        sched = list(csv.DictReader(open(os.path.join(dirname, f"resampler-{method}.schedule.csv"))))
        # The capture starts before the lab's clock: find the first case's onset and use the schedule
        # for the rest, relative to it.
        t_first = first_onset(left, sr, 0, float(sched[0]["start_s"]) + 2.0)
        offset = t_first - (float(sched[0]["start_s"]) + 0.25)   # a ladder starts with a 0.25 s gap
        print(f"\n== {method}  (capture offset {offset * 1000:.0f} ms)")
        for c in sched:
            start = float(c["start_s"]) + offset
            pitch = float(c["pitch"])
            rate = int(c["rate"])
            if c["case"].startswith("noise"):
                a = int((start + 0.3) * sr)
                b = int((start + float(c["seconds"]) - 0.3) * sr)
                seg = left[a:b]
                f, p = welch(seg, sr, nperseg=4096)
                ref = p[(f > 1000) & (f < 4000)].mean()
                bands = [(8000, 12000), (12000, 16000), (16000, 20000)]
                droop = [db(p[(f >= lo) & (f < hi)].mean() / ref) for lo, hi in bands]
                # How much the top octave's level moves from one 3 ms frame to the next: white noise's
                # own fluctuation plus whatever the resampler adds.
                sos = butter(8, [12000, 20000], btype="band", fs=sr, output="sos")
                hf = sosfilt(sos, seg)
                e, _ = envelope(hf, sr, ms=3)
                cv = e.std() / e.mean()
                rows.append((method, c["case"], droop, cv))
                print(f"  {c['case']:<14} white noise: 8-12k {droop[0]:+.1f} dB, 12-16k {droop[1]:+.1f} dB, 16-20k {droop[2]:+.1f} dB re 1-4k; "
                      f"top-octave level fluctuation {cv * 100:.1f} % (3 ms frames)")
                continue
            freqs = [float(v) for v in c["freqs"].split()]
            step = 0.85 / pitch
            ref_tone = None
            for k, f in enumerate(freqs):
                a = start + (0.25 + k * 0.85) / pitch + 0.1
                seg = left[int(a * sr): int(a * sr) + 16384]
                fo = f * pitch
                if fo * 1.0 > sr / 2:
                    # Above the mixer's Nyquist: nothing should come out; anything that does is alias.
                    spec = np.abs(np.fft.rfft(seg * get_window("blackmanharris", len(seg)))) ** 2
                    freqs_ = np.fft.rfftfreq(len(seg), 1 / sr)
                    band = (freqs_ >= 20) & (freqs_ <= 20000)
                    tot = spec[band].sum()
                    kk = np.argmax(np.where(band, spec, 0))
                    print(f"  {c['case']:<14} {f:7.0f} Hz (above the mixer's Nyquist): {db(tot / ref_tone):+.1f} dB re the 1 kHz tone, "
                          f"strongest at {freqs_[kk]:.0f} Hz")
                    rows.append((method, c["case"], f, db(tot / ref_tone)))
                    continue
                tone, rest, fs, ps = tone_metrics(seg, sr, fo)
                if ref_tone is None:
                    ref_tone = tone
                print(f"  {c['case']:<14} {f:7.0f} Hz: THD+N {db(rest / tone):6.1f} dB, level {db(tone / ref_tone):+5.1f} dB re 1 kHz, "
                      f"worst spur {fs:7.0f} Hz at {db(ps / tone):6.1f} dB")
                rows.append((method, c["case"], f, db(rest / tone)))
    return rows


def residual(x):
    """Second difference: a smooth low tone leaves almost nothing, a step or a kink leaves a spike."""
    return x[2:] - 2 * x[1:-1] + x[:-2]


def orbit(dirname):
    names = sorted(n for n in os.listdir(dirname) if n.startswith("orbit-") and n.endswith(".post.wav"))
    for name in names:
        sr, x = read(os.path.join(dirname, name))
        out = []
        for ch in range(2):
            s = x[:, ch]
            on = first_onset(s, sr, 0, len(s) / sr, rel_db=-30)
            if on is None:
                continue
            a, b = int((on + 0.6) * sr), int((on + 5.4) * sr)
            s = s[a:b]
            if "tone" in name:
                # 300 Hz: remove it by high-passing hard at 2 kHz; what is left is what the stage added.
                sos = butter(10, 2000, btype="high", fs=sr, output="sos")
                r = sosfilt(sos, s)[2048:]
                lvl = np.sqrt(np.mean(s ** 2))
                e = np.sqrt(np.mean(r ** 2))
                # Where is it: at the 1024-sample block edges, or spread?
                blocks = len(r) // 1024
                rb = np.abs(r[:blocks * 1024].reshape(blocks, 1024))
                prof = rb.mean(axis=0)
                peakiness = prof.max() / np.median(prof)
                out.append(f"ch{ch}: added above 2 kHz {db(e ** 2 / lvl ** 2):6.1f} dB re the tone; "
                           f"block-phase profile max/median {peakiness:.1f} at offset {np.argmax(prof)}")
            else:
                r = residual(s)
                blocks = len(r) // 1024
                rb = np.abs(r[:blocks * 1024].reshape(blocks, 1024))
                prof = rb.mean(axis=0)
                out.append(f"ch{ch}: 2nd-difference block-phase profile max/median {prof.max() / np.median(prof):.2f}")
        print(f"  {name}: " + "; ".join(out))


def blocks(paths, block=1024):
    """Steps locked to the mixer's block: the mean |second difference| at each position within the
    1024-sample block, over the whole file. A capture starts on a block boundary, so a step that
    happens once a block piles up at one offset; a signal with no such steps gives a flat profile."""
    for p in paths:
        sr, x = read(p)
        out = []
        for ch in range(x.shape[1]):
            r = residual(x[:, ch])
            nb = len(r) // block
            prof = np.abs(r[:nb * block].reshape(nb, block)).mean(axis=0)
            k = int(np.argmax(prof))
            out.append(f"ch{ch} max/median {prof.max() / np.median(prof):.2f} at offset {k}")
        print(f"  {os.path.basename(p)}: " + "; ".join(out))


def jumps(paths, skip=1.0, z=8.0):
    """Discontinuities: samples whose second difference stands z times over its local rms (46 ms),
    merged within 1.5 ms. A smooth signal of any spectrum has none; a skip or repeat in a read has one
    each. Also how many of them land at one phase of the 1024-sample block."""
    from scipy.ndimage import uniform_filter1d
    for p in paths:
        sr, x = read(p)
        counts = []
        for ch in range(x.shape[1]):
            s = x[int(skip * sr):, ch]
            r = residual(s)
            loc = np.sqrt(uniform_filter1d(r ** 2, 2048)) + 1e-12
            hit = np.flatnonzero(np.abs(r) / loc > z)
            ev = []
            for i in hit:
                if not ev or i - ev[-1] > int(0.0015 * sr):
                    ev.append(i)
            secs = len(s) / sr
            counts.append(f"ch{ch} {len(ev)} ({len(ev) / secs:.2f}/s)")
        print(f"  {os.path.basename(p)}: discontinuities " + ", ".join(counts))


THIRDS = [63, 125, 250, 500, 1000, 2000, 4000, 6300, 8000, 10000, 12500, 16000, 20000]
ENV_BANDS = [250, 500, 1000, 2000, 4000, 8000, 12500]


def texture(paths, seconds=30.0, hp=150.0):
    """What a texture is made of, after McDermott and Simoncelli (2011): its spectrum, and the
    statistics of each band's envelope, which is what makes rain sound like rain and not like noise
    shaped like rain. Per file: the third-octave spectrum against the 1-4 kHz band; per band the
    envelope's spread (std/mean), skew and kurtosis; how the bands' envelopes move together; how much
    is above 16 kHz; and for two channels, how alike the ears are. Gaussian noise of any spectrum has
    envelope spread about 0.52, kurtosis about 3.2 and no comodulation; a crackle is a high kurtosis;
    a texture of separate events moves its bands together."""
    from scipy.signal import hilbert, sosfiltfilt
    print(f"  {'file':<44} {'sr':>6} {'rms':>6} | 3rd-oct re 1-4k: "
          + " ".join(f"{(str(b // 1000) + 'k') if b >= 1000 else str(b):>6}" for b in THIRDS))
    rows = []
    for p in paths:
        sr, x = read(p)
        n = min(len(x), int(seconds * sr))
        x = x[:n]
        mono = x.mean(axis=1)
        if hp:
            mono = sosfilt(butter(4, hp, btype="high", fs=sr, output="sos"), mono)
        f, P = welch(mono, sr, nperseg=8192)
        ref = P[(f >= 1000) & (f < 4000)].mean()
        spec = []
        for b in THIRDS:
            lo, hi = b / 2 ** (1 / 6), b * 2 ** (1 / 6)
            m = (f >= lo) & (f < hi)
            spec.append(db(P[m].mean() / ref) if m.any() and hi < sr / 2 else float("nan"))
        rms = 20 * np.log10(np.sqrt(np.mean(mono ** 2)) + 1e-30)
        print(f"  {os.path.basename(p)[:44]:<44} {sr:>6} {rms:6.1f} | " + " ".join(f"{v:6.1f}" for v in spec))
        envs, stats = [], []
        dec = max(1, sr // 400)
        for b in ENV_BANDS:
            lo, hi = b / 2 ** (1 / 6), min(b * 2 ** (1 / 6), 0.45 * sr)
            if lo >= hi:
                continue
            band = sosfiltfilt(butter(4, [lo, hi], btype="band", fs=sr, output="sos"), mono)
            e = np.abs(hilbert(band))
            e = np.maximum(sosfiltfilt(butter(2, 100, fs=sr, output="sos"), e)[::dec], 0)
            m, sd = e.mean(), e.std()
            z = (e - m) / (sd + 1e-30)
            stats.append((b, sd / (m + 1e-30), (z ** 3).mean(), (z ** 4).mean()))
            envs.append(z)
        cc = np.corrcoef(np.array(envs)) if len(envs) > 1 else np.ones((1, 1))
        como = (cc.sum() - len(envs)) / max(1, len(envs) * (len(envs) - 1))
        print("      envelope spread/skew/kurtosis: "
              + "  ".join(f"{(str(b // 1000) + 'k') if b >= 1000 else b} {c:.2f}/{sk:.1f}/{k:.1f}" for b, c, sk, k in stats)
              + f"   comodulation {como:.2f}")
        top = P[f >= 16000].sum() / P.sum() if (f >= 16000).any() else 0
        line = f"      above 16 kHz {db(top):.1f} dB of the total"
        if x.shape[1] == 2:
            l, r = x[:, 0], x[:, 1]
            line += f"; ears alike (zero-lag correlation) {np.dot(l, r) / np.sqrt(np.dot(l, l) * np.dot(r, r) + 1e-30):.2f}"
        print(line)
        rows.append((p, spec, stats, como))
    return rows


def lim_stats(pre_path, post_path):
    sr, pre = read(pre_path)
    _, post = read(post_path)
    n = min(len(pre), len(post))
    pre, post = pre[:n], post[:n]
    # Align: the tap before the limiter and the one after it see the same block, but check by
    # cross-correlating a stretch.
    seg = slice(n // 3, n // 3 + 1 << 15)
    best, lag = 0, 0
    for l in range(-2048, 2049, 1):
        if l < 0:
            c = np.dot(pre[seg.start - l: seg.start - l + 4096, 0], post[seg.start: seg.start + 4096, 0])
        else:
            c = np.dot(pre[seg.start: seg.start + 4096, 0], post[seg.start + l: seg.start + l + 4096, 0])
        if abs(c) > abs(best):
            best, lag = c, l
    if lag > 0:
        pre, post = pre[:-lag or None], post[lag:]
    elif lag < 0:
        pre, post = pre[-lag:], post[:lag]
    n = min(len(pre), len(post))
    pre, post = pre[:n], post[:n]
    # Gain the limiter applied, per 5 ms, against the makeup it would have applied had it done nothing.
    win = int(0.005 * sr)
    m = n // win * win
    p = pre[:m].reshape(-1, win, 2)
    q = post[:m].reshape(-1, win, 2)
    ep = (p ** 2).sum(axis=(1, 2))
    eq = (q ** 2).sum(axis=(1, 2))
    live = ep > 1e-10
    g = np.sqrt(eq[live] / ep[live])
    gdb = 20 * np.log10(g)
    makeup = np.percentile(gdb, 95)    # the gain when it is not reducing: the maximiser's makeup
    gr = makeup - gdb
    # Distortion the limiter adds: post minus the best scalar gain of pre within each 1 ms.
    w1 = int(0.001 * sr)
    m1 = n // w1 * w1
    a = pre[:m1].reshape(-1, w1, 2)
    b = post[:m1].reshape(-1, w1, 2)
    num = (a * b).sum(axis=(1, 2))
    den = (a * a).sum(axis=(1, 2))
    ok = den > 1e-10
    gg = np.where(ok, num / np.where(ok, den, 1), 0)
    resid = b - gg[:, None, None] * a
    er = (resid ** 2).sum(axis=(1, 2))
    eb = (b ** 2).sum(axis=(1, 2))
    loud = ok & (eb > 1e-8)
    dist_db = db(er[loud].sum() / eb[loud].sum())
    worst = np.sort(db(er[loud] / eb[loud]))[-max(1, loud.sum() // 100):]
    peak_pre = 20 * np.log10(np.abs(pre).max() + 1e-30)
    peak_post = 20 * np.log10(np.abs(post).max() + 1e-30)
    over = (np.abs(post) >= 1.0).sum()
    crest = lambda y: 20 * np.log10(np.abs(y).max() / (np.sqrt(np.mean(y ** 2)) + 1e-30))
    return dict(seconds=n / sr, lag=lag, makeup=makeup, gr_mean=gr.mean(), gr_p50=np.percentile(gr, 50),
                gr_p95=np.percentile(gr, 95), gr_max=gr.max(), frac_over_1db=(gr > 1).mean(), frac_over_3db=(gr > 3).mean(),
                dist_db=dist_db, dist_worst1pct=worst.mean(), peak_pre=peak_pre, peak_post=peak_post,
                samples_over_fs=int(over), crest_pre=crest(pre), crest_post=crest(post),
                rms_post=20 * np.log10(np.sqrt(np.mean(post ** 2)) + 1e-30))


def scene(pre_path, post_path):
    s = lim_stats(pre_path, post_path)
    print(f"  {os.path.basename(post_path)}: {s['seconds']:.1f} s; makeup seen {s['makeup']:.1f} dB; "
          f"gain reduction mean {s['gr_mean']:.2f} dB, median {s['gr_p50']:.2f}, 95th pct {s['gr_p95']:.2f}, max {s['gr_max']:.1f}; "
          f"time reducing >1 dB {s['frac_over_1db'] * 100:.1f} %, >3 dB {s['frac_over_3db'] * 100:.1f} %")
    print(f"     limiter distortion (post minus the best 1 ms gain of pre): {s['dist_db']:.1f} dB overall, "
          f"{s['dist_worst1pct']:.1f} dB in the worst 1 % of milliseconds; peak pre {s['peak_pre']:+.1f} dBFS, post {s['peak_post']:+.1f}; "
          f"samples at/over full scale {s['samples_over_fs']}; crest pre {s['crest_pre']:.1f} dB, post {s['crest_post']:.1f}; rms post {s['rms_post']:.1f} dBFS")
    return s


def true_peak_db(x, up=8):
    """The peak between samples too: 8x oversampled by a polyphase low-pass, dB re full scale."""
    from scipy.signal import resample_poly
    y = resample_poly(x, up, 1, axis=0)
    # The filter rings where the stretch is cut out of a longer signal: leave its edges out.
    edge = 64 * up
    if len(y) > 4 * edge:
        y = y[edge:-edge]
    return 20 * np.log10(np.abs(y).max() + 1e-30)


def flat_runs(x, min_len=3, top_db=6.0):
    """Runs of at least min_len samples that are exactly equal (per channel) and within top_db of the
    file's peak: a clipped or flat-limited top. Returns (runs, samples in them)."""
    peak = np.abs(x).max()
    if peak <= 0:
        return 0, 0
    floor = peak * 10 ** (-top_db / 20)
    runs = samples = 0
    for c in range(x.shape[1]):
        v = x[:, c]
        same = (v[1:] == v[:-1]) & (np.abs(v[1:]) >= floor)
        # run lengths of True in `same`; a run of k equal pairs is k + 1 samples
        d = np.diff(np.concatenate(([0], same.astype(np.int8), [0])))
        starts, ends = np.flatnonzero(d == 1), np.flatnonzero(d == -1)
        lens = ends - starts + 1
        keep = lens >= min_len
        runs += int(keep.sum())
        samples += int(lens[keep].sum())
    return runs, samples


def flattops(paths):
    for p in paths:
        sr, x = read(p)
        runs, samples = flat_runs(x)
        peak = 20 * np.log10(np.abs(x).max() + 1e-30)
        print(f"  {os.path.basename(p)}: {len(x) / sr:.1f} s; peak {peak:+.2f} dBFS, true peak {true_peak_db(x):+.2f} dBTP; "
              f"flat-topped runs (3+ equal samples within 6 dB of the peak) {runs}, {samples} samples")


def thd_n(seg, sr, f):
    """THD+N against a least-squares sine at f, dB."""
    t = np.arange(len(seg)) / sr
    a = np.column_stack([np.sin(2 * np.pi * f * t), np.cos(2 * np.pi * f * t), np.ones_like(t)])
    coef, *_ = np.linalg.lstsq(a, seg, rcond=None)
    fit = a[:, :2] @ coef[:2]
    rest = seg - a @ coef
    return 10 * np.log10((rest ** 2).sum() / max(1e-30, (fit ** 2).sum()))


def line_db(seg, sr, f):
    w = np.hanning(len(seg))
    t = np.arange(len(seg)) / sr
    return 20 * np.log10(abs(np.sum(seg * w * np.exp(-2j * np.pi * f * t))) + 1e-30)


def limiter(dirname, tag):
    base = os.path.join(dirname, f"limiter-{tag}")
    sr, pre = read(base + ".pre.wav")
    _, post = read(base + ".post.wav")
    n = min(len(pre), len(post))
    pre, post = pre[:n], post[:n]
    rows = list(csv.DictReader(open(base + ".csv")))
    # The tones start when the lab's clock said; find the first in the capture and line up from there.
    t0 = first_onset(pre[:, 0], sr, 0.0, 3.0)
    first = float(rows[0]["start"])
    off = (t0 - first) if t0 is not None else 0.0
    # The lag between the taps: the first sample of the first tone (out of digital silence) in each.
    # A cross-correlation on a steady tone is ambiguous by half its period.
    a0 = max(0, int((first + off - 0.1) * sr))
    on_pre = a0 + int(np.argmax(np.abs(pre[a0:, 0]) > 1e-5))
    on_post = a0 + int(np.argmax(np.abs(post[a0:, 0]) > 1e-5))
    lag = on_post - on_pre
    print(f"  {os.path.basename(base)}: {sr} Hz; the post capture trails the pre by {lag} samples ({lag / sr * 1000:.2f} ms: the limiter's latency)")
    print(f"  {'signal':<10} {'over':>5} {'in TP':>8} {'out TP':>8} {'out pk':>8} {'GR':>6} {'THD+N':>8} {'IMD':>8} {'flat runs':>9}")
    for r in rows:
        name, start, sec = r["name"], float(r["start"]) + off, float(r["seconds"])
        if name in ("bed", "burst", "roll"):
            continue
        a, b = int((start + sec * 0.5) * sr), int((start + sec * 0.9) * sr)
        xi, yo = pre[a:b, 0], post[a + lag:b + lag, 0]
        gr = 20 * np.log10(np.sqrt((yo ** 2).mean()) / max(1e-30, np.sqrt((xi ** 2).mean()))) - float(os.environ.get("MAKEUP_DB", "7"))
        hz1, hz2 = float(r["hz1"]), float(r["hz2"])
        thd = imd = float("nan")
        if hz2 == 0:
            thd = thd_n(yo, sr, hz1)
        elif hz1 < 1000:            # SMPTE: sidebands of the high tone at +-hz1, +-2 hz1
            c = line_db(yo, sr, hz2)
            side = max(line_db(yo, sr, hz2 + k * hz1) for k in (-2, -1, 1, 2))
            imd = side - c
        else:                       # CCIF: the difference tone and the third-order products
            c = line_db(yo, sr, hz1)
            d = hz2 - hz1
            imd = max(line_db(yo, sr, d), line_db(yo, sr, 2 * hz1 - hz2), line_db(yo, sr, 2 * hz2 - hz1) if 2 * hz2 - hz1 < sr / 2 else -300) - c
        full = post[int(start * sr) + lag:int((start + sec) * sr) + lag]
        runs, _ = flat_runs(full, top_db=1.0)
        print(f"  {name:<10} {r['over_db']:>5} {true_peak_db(pre[int(start * sr):int((start + sec) * sr)]) + float(os.environ.get("MAKEUP_DB", "7")):+8.2f} "
              f"{true_peak_db(full):+8.2f} {20 * np.log10(np.abs(full).max() + 1e-30):+8.2f} {-gr:6.2f} {thd:8.1f} {imd:8.1f} {runs:9d}")
    # Recovery: the bed's level in 5 ms windows after the burst and after the roll, against before.
    bed = next(r for r in rows if r["name"] == "bed")
    burst = next(r for r in rows if r["name"] == "burst")
    roll = next(r for r in rows if r["name"] == "roll")
    win = int(0.005 * sr)
    def level(t):
        a = int(t * sr) + lag
        return 20 * np.log10(np.sqrt((post[a:a + win, 0] ** 2).mean()) + 1e-30)
    ref = level(float(bed["start"]) + off + 0.5)
    for ev in (burst, roll):
        end = float(ev["start"]) + off + float(ev["seconds"])
        depth = min(level(end + k * 0.005) for k in range(0, 20)) - ref
        t1 = t01 = None
        for k in range(0, 600):
            t = end + 0.02 + k * 0.005
            dl = level(t) - ref
            if t1 is None and dl > -1.0:
                t1 = t - end
            if t01 is None and dl > -0.1:
                t01 = t - end
                break
        print(f"  after the {ev['name']} ({ev['seconds']} s, {ev['over_db']} dB over): the bed went down {-depth:.1f} dB; "
              f"back within 1 dB in {t1 * 1000 if t1 else float('nan'):.0f} ms, within 0.1 dB in {t01 * 1000 if t01 else float('nan'):.0f} ms")


def check(paths):
    for p in paths:
        sr, x = read(p)
        mono = x.mean(axis=1)
        rms = 20 * np.log10(np.sqrt(np.mean(x ** 2)) + 1e-30)
        peak = 20 * np.log10(np.abs(x).max() + 1e-30)
        clipped = int((np.abs(x) >= 0.999).sum())
        e, n = envelope(mono, sr, ms=50)
        silent = db(e ** 2) < -80
        # Silent runs longer than 200 ms inside the file (not counting lead-in/out).
        runs, cur = [], 0
        for v in silent:
            if v:
                cur += 1
            else:
                if cur:
                    runs.append(cur)
                cur = 0
        inner = [r * 0.05 for r in runs if r * 0.05 >= 0.2]
        print(f"  {os.path.basename(p)}: {len(x) / sr:.1f} s, {sr} Hz, {x.shape[1]} ch; rms {rms:.1f} dBFS, peak {peak:+.1f} dBFS, "
              f"clipped samples {clipped}, silent gaps >=0.2 s inside: {len(inner)}"
              + (f" (longest {max(inner):.1f} s)" if inner else "")
              + (f"; leading silence {np.argmax(~silent) * 0.05:.2f} s" if silent.any() else "")
              + f"; true peak {true_peak_db(x):+.2f} dBTP; flat-topped runs {flat_runs(x)[0]}")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    cmd = sys.argv[1]
    if cmd == "resampler":
        resampler(sys.argv[2])
    elif cmd == "orbit":
        orbit(sys.argv[2])
    elif cmd == "scene":
        scene(sys.argv[2], sys.argv[3])
    elif cmd == "scenes":
        d = sys.argv[2]
        for name in sorted(os.listdir(d)):
            if name.startswith("scene-") and name.endswith(".pre.wav"):
                scene(os.path.join(d, name), os.path.join(d, name[:-len(".pre.wav")] + ".post.wav"))
    elif cmd == "blocks":
        blocks(sys.argv[2:])
    elif cmd == "jumps":
        jumps(sys.argv[2:])
    elif cmd == "texture":
        texture(sys.argv[2:])
    elif cmd == "check":
        check(sys.argv[2:])
    elif cmd == "limiter":
        limiter(sys.argv[2], sys.argv[3])
    elif cmd == "flattops":
        flattops(sys.argv[2:])
    else:
        print(__doc__)
        sys.exit(1)
