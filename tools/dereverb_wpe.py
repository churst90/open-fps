#!/usr/bin/env python3
"""
Take the room out of close recordings: single-channel WPE (weighted prediction error) dereverberation,
Nakatani et al. 2010 / Yoshioka & Nakatani 2012, as in nara_wpe.

In the STFT domain each frequency's late reverberation is predicted from its own past frames (from
DELAY frames back, so the direct sound and the first ~10 ms of the object's own ringing are never
predicted away) and subtracted. The prediction filter is re-solved a few times with each frame weighted
by the inverse of its current dry power, which is what makes it a dereverberator rather than a whitening
filter. Nothing is gated or faded: what is left is what the recording had before the room answered.

Then the late reverberation WPE cannot reach (its filter looks back ~130 ms; a gunshot's room rings for
most of a second) is suppressed spectrally, Lebart et al. 2001 / Habets 2007: the clip's own decay time is
measured from its energy decay curve, the late reverberant power at each frame is predicted as the power
LATE seconds earlier decayed by that time, and each STFT bin is turned down by the share that prediction
explains, never below FLOOR (so the sound is not hollowed out into musical noise).

Usage: dereverb_wpe.py IN_DIR OUT_DIR [--skip NAME ...]
Writes every WAV under IN_DIR to the same relative path under OUT_DIR, same rate and sample format, and
prints each file's decay before and after: the time to fall 20 and 40 dB from its loudest 5 ms.
"""
import argparse, os, sys
import numpy as np
import soundfile as sf
from scipy.signal import stft, istft

NFFT, HOP = 1024, 256          # 21 ms window, 5.3 ms hop at 48 kHz
DELAY = 2                      # frames left alone: the direct sound and its first ringing
TAPS = 24                      # frames of past used to predict: reaches ~130 ms back
ITERATIONS = 3
EPS = 1e-10
LATE = 0.05                    # seconds: what arrives later than this after its source is "late"
FLOOR_DB = -18.0               # the most any bin is turned down
SMOOTH = 0.6                   # recursive smoothing of the power estimates, per frame


def wpe_one(x, sr):
    _, _, X = stft(x, sr, nperseg=NFFT, noverlap=NFFT - HOP, boundary="zeros", padded=True)
    F, T = X.shape
    D = X.copy()
    for f in range(F):
        y = X[f]
        # Rows of past frames for each frame t: y[t-DELAY-k], k = 0..TAPS-1.
        past = np.zeros((TAPS, T), dtype=complex)
        for k in range(TAPS):
            s = DELAY + k
            if s < T:
                past[k, s:] = y[:T - s]
        d = y.copy()
        for _ in range(ITERATIONS):
            lam = np.maximum(np.abs(d) ** 2, EPS * (np.abs(y) ** 2).max() + 1e-20)
            w = 1.0 / lam
            R = (past * w) @ past.conj().T
            r = (past * w) @ y.conj()
            R += np.eye(TAPS) * 1e-6 * np.trace(R).real / TAPS + 1e-20 * np.eye(TAPS)
            g = np.linalg.solve(R, r)
            d = y - g.conj() @ past
        D[f] = d
    _, out = istft(D, sr, nperseg=NFFT, noverlap=NFFT - HOP, boundary=True)
    return out[: len(x)]


def t60_of(x, sr):
    """The clip's reverberation time from its energy decay curve after the loudest moment: Schroeder
    backward integration, the slope fitted from -5 to -25 dB and extended to 60."""
    p = int(np.argmax(np.abs(x)))
    e = x[p:] ** 2
    edc = np.cumsum(e[::-1])[::-1]
    edc_db = 10 * np.log10(edc / (edc[0] + 1e-20) + 1e-20)
    i5 = np.argmax(edc_db < -5); i25 = np.argmax(edc_db < -25)
    if i25 <= i5 + 10:
        return 0.3
    t = np.arange(i5, i25) / sr
    slope = np.polyfit(t, edc_db[i5:i25], 1)[0]           # dB per second, negative
    return float(np.clip(-60.0 / slope, 0.05, 3.0)) if slope < 0 else 0.3


def suppress_late(x, sr):
    t60 = t60_of(x, sr)
    _, _, X = stft(x, sr, nperseg=NFFT, noverlap=NFFT - HOP, boundary="zeros", padded=True)
    P = np.abs(X) ** 2
    # Smoothed power, so single frames do not flutter the gain.
    S = P.copy()
    for t in range(1, P.shape[1]):
        S[:, t] = SMOOTH * S[:, t - 1] + (1 - SMOOTH) * P[:, t]
    lag = max(1, int(round(LATE * sr / HOP)))
    decay = np.exp(-6.0 * np.log(10) / t60 * lag * HOP / sr)   # power decay over LATE seconds
    late = np.zeros_like(S)
    late[:, lag:] = decay * S[:, :-lag]
    gain = 1.0 - late / (S + 1e-20)
    floor = 10 ** (FLOOR_DB / 20)
    gain = np.clip(np.sqrt(np.clip(gain, 0, 1)), floor, 1.0)
    _, out = istft(X * gain, sr, nperseg=NFFT, noverlap=NFFT - HOP, boundary=True)
    return out[: len(x)], t60


def decay_ms(x, sr):
    hop = int(0.005 * sr)
    e = np.array([np.sqrt((x[i:i + hop] ** 2).mean()) for i in range(0, max(1, len(x) - hop), hop)])
    db = 20 * np.log10(e + 1e-9)
    p = int(db.argmax())
    after = db[p:]
    def t(drop):
        k = np.where(after < db[p] - drop)[0]
        return int(k[0] * 5) if len(k) else None
    return t(20), t(40), float(db[p])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("inp"); ap.add_argument("out"); ap.add_argument("--skip", nargs="*", default=[])
    a = ap.parse_args()
    print(f"{'file':60} {'-20 dB before/after':>20} {'-40 dB before/after':>20} {'peak change':>11}  measured T60")
    for root, _, files in os.walk(a.inp):
        rel = os.path.relpath(root, a.inp)
        if any(rel == s or rel.startswith(s + os.sep) for s in a.skip):
            continue
        for name in sorted(files):
            if not name.lower().endswith(".wav"):
                continue
            src = os.path.join(root, name)
            info = sf.info(src)
            x, sr = sf.read(src, always_2d=True)
            chans, t60s = [], []
            for c in range(x.shape[1]):
                z, t60 = suppress_late(wpe_one(x[:, c], sr), sr)
                chans.append(z); t60s.append(t60)
            y = np.stack(chans, axis=1)
            # Never louder than the original, never clipped.
            peak_in, peak_out = np.abs(x).max(), np.abs(y).max()
            if peak_out > 0.999:
                y *= 0.999 / peak_out
            dst = os.path.join(a.out, rel, name)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            sf.write(dst, y, sr, subtype=info.subtype)
            b20, b40, bp = decay_ms(x.mean(1), sr)
            a20, a40, ap_ = decay_ms(y.mean(1), sr)
            print(f"{os.path.join(rel, name)[-60:]:60} {str(b20):>9} / {str(a20):<9} {str(b40):>9} / {str(a40):<9} {ap_ - bp:+6.1f} dB  T60 {t60s[0]:.2f} s")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
