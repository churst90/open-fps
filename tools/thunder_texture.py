"""How continuous a thunder file is, the same way for renders and recordings.

  gaps      runs of |x| below -90 dBFS lasting 5 ms or more, inside the sound (between the first and
            last moments the Fast envelope is within 40 dB of its loudest; a run reaching the end
            is the end, not a gap)
  crest     median crest factor (peak / RMS, dB) of 10 ms windows over the rumble (Fast envelope
            within 20 dB of its loudest)
  kurt      median excess kurtosis of the same windows (0 for Gaussian noise; large for a few clicks)
  jump      median |change| of the 10 ms RMS level from one window to the next, dB
  drop%     share of those windows more than 30 dB under the median of the second round them
  hfkurt    the same kurtosis above 500 Hz, where crackle lives (clicks with nothing between: large)
  hfjump    the 10 ms level jumps above 500 Hz
  jump+, hfjump+   each against steady noise with the file's own spectrum: what the sound adds to the
            jumpiness its spectrum alone gives (a low rumble jumps more than a hiss however smooth)

usage: thunder_texture.py FILE...   (anything ffmpeg reads; channels averaged)
"""
import subprocess
import sys

import numpy as np


def load(path, fs=48000):
    raw = subprocess.run(["ffmpeg", "-v", "quiet", "-i", path, "-ac", "1", "-ar", str(fs), "-f", "f32le", "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).astype(np.float64), fs


def fast_env(x, fs):
    a = 1 - np.exp(-1 / (0.125 * fs))
    # one-pole on x^2, vectorised by scipy if present, else loop
    try:
        from scipy.signal import lfilter
        return lfilter([a], [1, -(1 - a)], x * x)
    except ImportError:
        e = np.empty_like(x); s = 0.0
        for i, v in enumerate(x * x):
            s += a * (v - s); e[i] = s
        return e


def texture(x, fs):
    env = fast_env(x, fs)
    m = env.max()
    idx40 = np.nonzero(env >= m * 1e-4)[0]
    a, b = idx40[0], idx40[-1]
    seg = x[a:b + 1]
    quiet = np.abs(seg) < 10 ** (-90 / 20)
    gaps = 0
    run = 0
    for q in quiet:
        if q:
            run += 1
        else:
            if run >= 0.005 * fs:
                gaps += 1
            run = 0
    # A quiet run that reaches the end of the span is the sound's end (the Fast envelope lingers
    # 125 ms after it), not a gap.
    w = int(0.010 * fs)
    nwin = len(x) // w
    X = x[:nwin * w].reshape(nwin, w)
    E = env[:nwin * w].reshape(nwin, w).mean(axis=1)
    rumble = E >= m * 0.01
    rms = np.sqrt((X ** 2).mean(axis=1)) + 1e-12
    peak = np.abs(X).max(axis=1) + 1e-12
    crest = 20 * np.log10(peak / rms)
    mu = X.mean(axis=1, keepdims=True)
    var = ((X - mu) ** 2).mean(axis=1) + 1e-30
    kurt = ((X - mu) ** 4).mean(axis=1) / var ** 2 - 3
    lv = 20 * np.log10(rms)
    jump = np.abs(np.diff(lv))
    jr = rumble[1:] & rumble[:-1]
    half = 50
    med = np.array([np.median(lv[max(0, i - half):i + half + 1]) for i in range(nwin)])
    drop = (lv < med - 30) & rumble
    from scipy.signal import butter, lfilter
    bh, ah = butter(2, 500 / (fs / 2), 'high')
    h = lfilter(bh, ah, x)
    H = h[:nwin * w].reshape(nwin, w)
    hv = (H ** 2).mean(axis=1) + 1e-30
    hk = (H ** 4).mean(axis=1) / hv ** 2 - 3
    hl = 10 * np.log10(hv)
    hj = np.abs(np.diff(hl))
    return (gaps, np.median(crest[rumble]), np.median(kurt[rumble]), np.median(jump[jr]), 100 * drop.sum() / max(1, rumble.sum()),
            np.median(hk[rumble]), np.median(hj[jr]))


def twin(x, fs):
    """Steady noise with the same long-term spectrum over the same span: as continuous as that
    spectrum allows. Its jump and hfjump are the floor the measures read for this spectrum."""
    env = fast_env(x, fs)
    idx = np.nonzero(env >= env.max() * 0.01)[0]
    seg = x[idx[0]:idx[-1] + 1]
    X = np.fft.rfft(seg)
    rng = np.random.default_rng(1)
    t = np.fft.irfft(np.abs(X) * np.exp(1j * rng.uniform(0, 2 * np.pi, len(X))), len(seg))
    return t * (np.abs(seg).max() / np.abs(t).max())


if __name__ == "__main__":
    print(f"{'file':46s} {'gaps':>5s} {'crest':>6s} {'kurt':>5s} {'jump':>5s} {'jump+':>6s} {'hfkurt':>6s} {'hfjump':>6s} {'hfjump+':>7s}")
    for path in sys.argv[1:]:
        x, fs = load(path)
        g, c, k, j, d, hk, hj = texture(x, fs)
        _, _, _, tj, _, _, thj = texture(twin(x, fs), fs)
        print(f"{path.split('/')[-1][:46]:46s} {g:5d} {c:6.1f} {k:5.1f} {j:5.2f} {j - tj:+6.2f} {hk:6.1f} {hj:6.2f} {hj - thj:+7.2f}")
