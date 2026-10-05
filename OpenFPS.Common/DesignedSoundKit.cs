using System;

namespace OpenFPS.Common;

/// <summary>
/// The few pieces of signal processing the DESIGNED sounds are made of (the admin gun, the teleporter,
/// the hand-over sounds): filters, noise, envelopes and the finishing every one-shot needs. Not a
/// model of anything; a model lives with its thing. Everything here renders offline into a buffer, off
/// the mixer thread, and nothing here throws on a strange number: a non-finite sample becomes silence.
/// </summary>
internal static class DesignedSoundKit
{
    /// <summary>An RBJ biquad. Its coefficients can be changed between samples (a swept band) without
    /// losing its state.</summary>
    internal struct Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public static Biquad BandPass(float hz, float q, float sr) { var b = new Biquad(); b.SetBandPass(hz, q, sr); return b; }
        public static Biquad LowPass(float hz, float q, float sr) { var b = new Biquad(); b.SetLowPass(hz, q, sr); return b; }
        public static Biquad HighPass(float hz, float q, float sr) { var b = new Biquad(); b.SetHighPass(hz, q, sr); return b; }

        /// <summary>Unity at the centre.</summary>
        public void SetBandPass(float hz, float q, float sr)
        {
            Prepare(hz, q, sr, out float cos, out float alpha, out float a0);
            Set(alpha / a0, 0f, -alpha / a0, -2f * cos / a0, (1f - alpha) / a0);
        }

        public void SetLowPass(float hz, float q, float sr)
        {
            Prepare(hz, q, sr, out float cos, out float alpha, out float a0);
            float b = (1f - cos) / 2f;
            Set(b / a0, 2f * b / a0, b / a0, -2f * cos / a0, (1f - alpha) / a0);
        }

        public void SetHighPass(float hz, float q, float sr)
        {
            Prepare(hz, q, sr, out float cos, out float alpha, out float a0);
            float b = (1f + cos) / 2f;
            Set(b / a0, -2f * b / a0, b / a0, -2f * cos / a0, (1f - alpha) / a0);
        }

        private static void Prepare(float hz, float q, float sr, out float cos, out float alpha, out float a0)
        {
            float f = Math.Clamp(float.IsFinite(hz) ? hz : 1000f, 5f, 0.45f * sr);
            float w = 2f * MathF.PI * f / sr;
            cos = MathF.Cos(w);
            alpha = MathF.Sin(w) / (2f * MathF.Max(0.05f, q));
            a0 = 1f + alpha;
        }

        private void Set(float b0, float b1, float b2, float a1, float a2) { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }

        public float Run(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            if (!float.IsFinite(y)) { y = 0f; _x1 = _x2 = _y1 = _y2 = 0f; }
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }

    /// <summary>White noise, uniform, unit variance.</summary>
    internal static float White(Random rng) => ((float)rng.NextDouble() * 2f - 1f) * 1.7320508f;

    /// <summary>A buffer of so many seconds.</summary>
    internal static float[] Buffer(float seconds, int sr) => new float[Math.Max(16, (int)(seconds * sr))];

    /// <summary>Adds a decaying sine from <paramref name="at"/> seconds: a mode ringing after a strike.</summary>
    internal static void AddRing(float[] y, int sr, float at, float hz, float amplitude, float t60, float attackSeconds = 0.0005f, float phase = 0f)
    {
        if (hz <= 0f || hz >= 0.45f * sr || amplitude == 0f) return;
        int a = (int)(at * sr);
        int n = Math.Min(y.Length - Math.Max(0, a), (int)(t60 * 1.2f * sr));
        float k = 6.9f / MathF.Max(1e-3f, t60);
        float w = 2f * MathF.PI * hz / sr;
        for (int i = 0; i < n; i++)
        {
            int at2 = a + i;
            if (at2 < 0) continue;
            float t = i / (float)sr;
            float env = MathF.Min(1f, t / MathF.Max(1e-5f, attackSeconds)) * MathF.Exp(-k * t);
            y[at2] += amplitude * env * MathF.Sin(w * i + phase);
        }
    }

    /// <summary>
    /// Finishes a one-shot: no non-finite sample, no DC, a few milliseconds of fade at each end so it
    /// neither starts nor stops on a step, and its peak brought to <paramref name="peak"/>.
    /// </summary>
    internal static float[] Finish(float[] y, int sr, float peak = 1f, float fadeInMs = 0.5f, float fadeOutMs = 8f)
    {
        if (y.Length == 0) return y;
        double mean = 0;
        for (int i = 0; i < y.Length; i++)
        {
            if (!float.IsFinite(y[i])) y[i] = 0f;
            mean += y[i];
        }
        mean /= y.Length;
        // A one-pole high-pass at 15 Hz as well as the mean: a long sound's drift is not its mean.
        float r = 1f - 2f * MathF.PI * 15f / sr, lastIn = 0f, lastOut = 0f;
        for (int i = 0; i < y.Length; i++)
        {
            float x = y[i] - (float)mean;
            float o = x - lastIn + r * lastOut;
            lastIn = x; lastOut = o;
            y[i] = o;
        }
        int fin = Math.Min(y.Length, (int)(fadeInMs * 1e-3f * sr));
        for (int i = 0; i < fin; i++) y[i] *= i / (float)fin;
        int fout = Math.Min(y.Length, (int)(fadeOutMs * 1e-3f * sr));
        for (int i = 0; i < fout; i++) y[y.Length - 1 - i] *= i / (float)fout;
        return Peak(y, peak);
    }

    /// <summary>Scales to a peak; silence stays silence.</summary>
    internal static float[] Peak(float[] y, float peak)
    {
        float max = 0f;
        foreach (float v in y) max = MathF.Max(max, MathF.Abs(v));
        if (max < 1e-9f || !float.IsFinite(max)) return y;
        float g = peak / max;
        for (int i = 0; i < y.Length; i++) y[i] *= g;
        return y;
    }

    /// <summary>A hand, glove or sleeve brushing the thing: band noise in grains, swelling and fading
    /// over <paramref name="seconds"/>. The quietest part of handling anything.</summary>
    internal static void AddRustle(float[] y, int sr, float at, float seconds, float amplitude, Random rng, float lowHz = 600f, float highHz = 5000f)
    {
        var hp = Biquad.HighPass(lowHz, 0.7f, sr);
        var lp = Biquad.LowPass(highHz, 0.7f, sr);
        int a = (int)(at * sr), n = (int)(seconds * sr);
        int grain = Math.Max(1, (int)(0.006f * sr));
        float from = 0.3f, to = 0.3f;
        for (int i = 0; i < n && a + i < y.Length; i++)
        {
            if (i % grain == 0) { from = to; to = 0.15f + 0.85f * (float)rng.NextDouble(); }
            float g = from + (to - from) * (i % grain) / grain;
            float x = i / (float)n;
            float swell = MathF.Sin(MathF.PI * x);
            if (a + i >= 0) y[a + i] += amplitude * swell * g * lp.Run(hp.Run(White(rng)));
        }
    }

    /// <summary>A click: a millisecond or so of bright noise falling away fast.</summary>
    internal static void AddClick(float[] y, int sr, float at, float amplitude, Random rng, float hz = 3500f, float fallMs = 1.5f)
    {
        var bp = Biquad.BandPass(hz, 1.2f, sr);
        int a = (int)(at * sr), n = (int)(fallMs * 6e-3f * sr);
        float k = 6.9f / MathF.Max(1e-4f, fallMs * 3e-3f * sr);
        for (int i = 0; i < n && a + i < y.Length; i++)
            if (a + i >= 0) y[a + i] += amplitude * MathF.Exp(-k * i) * bp.Run(White(rng));
    }
}
