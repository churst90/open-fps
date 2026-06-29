using System;

namespace OpenFPS.Client.Core.AudioEngine.Tools;

/// <summary>
/// Generates a realistic electronic police-siren "wail" as raw PCM and as a 16-bit WAV, replacing the
/// old thin synthesized beep. The wail is a single smooth frequency sweep between <see cref="FreqMin"/>
/// and <see cref="FreqMax"/> with a few harmonics for the brassy electronic-siren timbre. The buffer is
/// exactly one wail cycle and is engineered to LOOP SEAMLESSLY: the period and average frequency are
/// chosen so the accumulated phase over the loop is an integer number of cycles (T·favg ∈ ℤ), so the
/// last sample joins the first with no click. Pure/deterministic so it can be unit-tested and re-emitted.
/// </summary>
public static class PoliceSirenGenerator
{
    public const int SampleRate = 44100;
    public const float FreqMin = 600f;
    public const float FreqMax = 1400f;     // favg = 1000 Hz
    public const float WailSeconds = 4.0f;  // 4 s × 1000 Hz avg = 4000 whole cycles → seamless loop

    /// <summary>Generates one seamless wail cycle as mono float samples in [-1, 1].</summary>
    public static float[] Generate()
    {
        int n = (int)(SampleRate * WailSeconds);
        var samples = new float[n];

        float favg = 0.5f * (FreqMin + FreqMax);
        float half = 0.5f * (FreqMax - FreqMin);
        double phase = 0.0;                 // fundamental phase accumulator
        const double twoPi = 2.0 * Math.PI;

        // Harmonic mix (fundamental + 2nd + 3rd) — the same phase accumulator keeps every harmonic an
        // exact integer multiple, so they all wrap seamlessly at the loop boundary too.
        const float a1 = 0.60f, a2 = 0.27f, a3 = 0.13f;

        float peak = 0f;
        for (int i = 0; i < n; i++)
        {
            // Instantaneous frequency sweeps fmin→fmax→fmin over the cycle (raised cosine, smooth at ends).
            double sweep = 0.5 - 0.5 * Math.Cos(twoPi * i / n);
            double f = favg - half + (FreqMax - FreqMin) * sweep; // == FreqMin + (FreqMax-FreqMin)*sweep
            phase += twoPi * f / SampleRate;

            float s = a1 * (float)Math.Sin(phase)
                    + a2 * (float)Math.Sin(2.0 * phase)
                    + a3 * (float)Math.Sin(3.0 * phase);
            samples[i] = s;
            float abs = MathF.Abs(s);
            if (abs > peak) peak = abs;
        }

        // Normalize to a safe headroom level.
        if (peak > 1e-6f)
        {
            float g = 0.90f / peak;
            for (int i = 0; i < n; i++) samples[i] *= g;
        }
        return samples;
    }

    /// <summary>Encodes mono float samples as a 16-bit PCM WAV (RIFF) byte array.</summary>
    public static byte[] ToWav16(float[] samples, int sampleRate = SampleRate)
    {
        int n = samples.Length;
        int dataBytes = n * 2;
        var buf = new byte[44 + dataBytes];
        int p = 0;
        void Str(string s) { foreach (char c in s) buf[p++] = (byte)c; }
        void U32(uint v) { buf[p++] = (byte)v; buf[p++] = (byte)(v >> 8); buf[p++] = (byte)(v >> 16); buf[p++] = (byte)(v >> 24); }
        void U16(ushort v) { buf[p++] = (byte)v; buf[p++] = (byte)(v >> 8); }

        Str("RIFF"); U32((uint)(36 + dataBytes)); Str("WAVE");
        Str("fmt "); U32(16); U16(1); U16(1);               // PCM, mono
        U32((uint)sampleRate); U32((uint)(sampleRate * 2));  // byte rate (mono 16-bit)
        U16(2); U16(16);                                     // block align, bits/sample
        Str("data"); U32((uint)dataBytes);
        for (int i = 0; i < n; i++)
        {
            float c = Math.Clamp(samples[i], -1f, 1f);
            short v = (short)Math.Round(c * short.MaxValue);
            buf[p++] = (byte)v; buf[p++] = (byte)(v >> 8);
        }
        return buf;
    }

    /// <summary>Generates the siren and writes it as a 16-bit WAV to <paramref name="path"/>.</summary>
    public static void WriteWav(string path) => System.IO.File.WriteAllBytes(path, ToWav16(Generate()));
}
