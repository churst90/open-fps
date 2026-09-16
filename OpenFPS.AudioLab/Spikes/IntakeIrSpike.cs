using System;
using System.IO;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The intake tract on its own, asked what note it makes.
///
///   --intake-ir [preset ...] [thr=1] [sec=0.6] [out=DIR]
///
/// An airbox on a snorkel is a Helmholtz resonator, and on a formula car it is the lowest thing on
/// the whole vehicle by an order of magnitude — around fifty hertz against a firing frequency of
/// thirteen hundred. Whether the model actually PRODUCES that resonance is not something a drive
/// render can answer, because a drive render only ever excites the tract at firing frequency and its
/// harmonics: a resonator with nothing driving it at its own note is silent however well it is built.
///
/// So this shuts every valve, holds the throttle where it is asked to, thumps the plenum once
/// through one runner, and measures what comes out of the snorkel. What it prints is the tract's own
/// modes, which is a property of the geometry and of nothing else.
/// </summary>
public static class IntakeIrSpike
{
    private const int Sr = 48000;

    public static int Run(string[] args)
    {
        float thr = Arg(args, "thr", 1f);
        float seconds = Arg(args, "sec", 0.6f);
        string outDir = ArgString(args, "out", ".");
        Directory.CreateDirectory(outDir);

        var names = args.Where(a => !a.StartsWith("--") && !a.Contains('='))
                        .Where(a => EngineProfile.Presets.ContainsKey(a)).ToList();
        if (names.Count == 0) names.Add("f1_v10");

        foreach (string name in names)
        {
            var e = EngineProfile.Presets[name]();
            var s = e.Intake;
            float[] ir = Impulse(e, thr, seconds);

            string path = Path.Combine(outDir, $"intake_{name}.wav");
            WriteWav(path, ir, Sr);

            // What the geometry PREDICTS, so the measurement has something to disagree with.
            float snorkelArea = Circle(s.SnorkelDiameterMm);
            float throttleArea = Circle(s.ThrottleDiameterMm);
            float boxArea = throttleArea * 5f;
            float boxLen = MathF.Max(0.08f, s.AirboxLitres * 1e-3f / boxArea);
            float neckEff = s.SnorkelLengthMetres + 0.85f * MathF.Sqrt(snorkelArea / MathF.PI);
            float helmholtz = 340f / (2f * MathF.PI)
                            * MathF.Sqrt(snorkelArea / MathF.Max(1e-6f, s.AirboxLitres * 1e-3f * neckEff));

            Console.WriteLine();
            Console.WriteLine($"── {name}: {e.Name} ──");
            Console.WriteLine($"  airbox {s.AirboxLitres:0.#} L modelled as {boxLen * 100:0.#} cm x {boxArea * 1e4f:0} cm^2;"
                            + $" snorkel {s.SnorkelLengthMetres * 100:0} cm x {snorkelArea * 1e4f:0} cm^2");
            Console.WriteLine($"  lumped Helmholtz prediction: {helmholtz:0.#} Hz");
            Console.WriteLine($"  throttle {thr * 100:0}% open");
            Console.WriteLine($"  measured peaks: {Peaks(ir)}");
            Console.WriteLine($"  band balance:   {Bands(ir)}");
            Console.WriteLine($"  written: {path}");
        }
        return 0;
    }

    /// <summary>
    /// One thump into a shut tract.
    ///
    /// Every valve is rigid, so nothing but the impulse ever enters: what is measured is the plenum,
    /// the throttle plate, the airbox and the snorkel, and not the engine breathing through them.
    /// The tract is let settle first because the plenum starts at atmosphere and the throttle then
    /// drags it to wherever a shut engine leaves it, and that transient is not a mode.
    /// </summary>
    private static float[] Impulse(EngineProfile e, float throttle, float seconds)
    {
        var net = new IntakeNetwork(e, Sr);
        net.SetThrottle(throttle);
        net.UpdateGas(305f, Gas.Atmosphere);

        for (int i = 0; i < Sr; i++) { Shut(net, e.Cylinders); net.Step(); }

        int n = (int)(Sr * seconds);
        var ir = new float[n];
        // A pressure wave into one runner, sized so the plenum sees a real pulse rather than a
        // rounding error: a tenth of an atmosphere, which is the order of a genuine intake pulse.
        Shut(net, e.Cylinders);
        net.PushFromValve(0, 10_000f);
        net.Step();
        for (int i = 0; i < n; i++) { Shut(net, e.Cylinders); net.Step(); ir[i] = net.Radiated; }

        // The impulse also shifts the mean, and a step is not a mode: take the DC out.
        float mean = 0f;
        foreach (float v in ir) mean += v;
        mean /= MathF.Max(1, n);
        for (int i = 0; i < n; i++) ir[i] -= mean;
        return ir;
    }

    private static void Shut(IntakeNetwork net, int cylinders)
    {
        for (int c = 0; c < cylinders; c++) net.PushFromValve(c, net.ArrivedAtValve(c));
    }

    private static string Peaks(float[] x)
    {
        // A log sweep, because the interesting span is 20 Hz to 2 kHz and a linear one spends all of
        // its resolution at the top, where this tract has nothing to say.
        const int steps = 400;
        var f = new float[steps];
        var mag = new float[steps];
        for (int i = 0; i < steps; i++)
        {
            f[i] = 20f * MathF.Pow(100f, i / (float)(steps - 1));   // 20 Hz .. 2 kHz
            mag[i] = Goertzel(x, f[i]);
        }
        float top = mag.Max();
        if (top <= 0f) return "silent";

        var found = new System.Collections.Generic.List<string>();
        for (int i = 2; i < steps - 2 && found.Count < 5; i++)
        {
            if (mag[i] <= mag[i - 1] || mag[i] < mag[i + 1]) continue;
            if (mag[i] < top * 0.08f) continue;
            found.Add($"{f[i]:0.#} Hz ({20f * MathF.Log10(mag[i] / top):0.#} dB)");
        }
        return found.Count == 0 ? "no peak" : string.Join(", ", found);
    }

    private static string Bands(float[] x)
    {
        double lo = 0, mid = 0, hi = 0;
        for (int i = 0; i < 300; i++)
        {
            float f = 20f * MathF.Pow(1000f, i / 299f);   // 20 Hz .. 20 kHz
            float m = Goertzel(x, f);
            double p = m * m;
            if (f < 200f) lo += p; else if (f < 1500f) mid += p; else hi += p;
        }
        double t = lo + mid + hi;
        if (t <= 0) return "silent";
        return $"{100 * lo / t:0.0}% below 200 Hz, {100 * mid / t:0.0}% to 1.5 kHz, {100 * hi / t:0.0}% above";
    }

    private static float Goertzel(float[] x, float freq)
    {
        double w = 2 * Math.PI * freq / Sr;
        double sr = 0, si = 0, norm = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1));
            double a = w * i;
            sr += x[i] * win * Math.Cos(a);
            si -= x[i] * win * Math.Sin(a);
            norm += win;
        }
        return (float)(2 * Math.Sqrt(sr * sr + si * si) / Math.Max(1, norm));
    }

    private static float Circle(float diameterMm)
    {
        float r = diameterMm * 0.5e-3f;
        return MathF.PI * r * r;
    }

    private static float Arg(string[] args, string key, float fallback)
    {
        foreach (string a in args)
            if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                && float.TryParse(a.AsSpan(key.Length + 1), out float v)) return v;
        return fallback;
    }

    private static string ArgString(string[] args, string key, string fallback)
    {
        foreach (string a in args)
            if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return fallback;
    }

    private static void WriteWav(string path, float[] samples, int sampleRate)
    {
        float peak = 0f;
        foreach (float v in samples) peak = MathF.Max(peak, MathF.Abs(v));
        float scale = peak > 0f ? 0.99f / peak : 1f;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + dataBytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(dataBytes);
        foreach (float v in samples)
            w.Write((short)Math.Clamp((int)(v * scale * 32767f), short.MinValue, short.MaxValue));
    }
}
