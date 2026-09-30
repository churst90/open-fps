using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Is the flat's traced tail boomy, or is it the rendering? The listener's trace of flat 01F, read
/// back as the game reads it, per octave: its decay (T20, Schroeder) and its late energy, against
/// Sabine and Eyring from the flat's real surfaces and the material table's band absorption.
///
///   --tail-bands
/// </summary>
public static class TailBandsSpike
{
    private static readonly Quaternion Q = Quaternion.Identity;
    private const int Fs = 44100;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no context"); return 1; }
        // Flat 01F, as the research measured it: carpet floor, plaster ceiling, one brick and three plaster walls.
        const float W = 8.65f, D = 17.86f, H = 2.73f;
        var boxes = new List<SteamAudioScene.Box>
        {
            new(new Vector3(0, 0.04f, 0), new Vector3(W, 0.04f, D), Q, "Carpet"),
            new(new Vector3(0, -0.07f, 0), new Vector3(21f, 0.17f, 90f), Q, "Concrete"),
            new(new Vector3(0, 2.735f, 0), new Vector3(20.5f, 0.03f, 89.3f), Q, "Plaster"),
            new(new Vector3(4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 80f), Q, "Brick"),
            new(new Vector3(-4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, D), Q, "Plaster"),
            new(new Vector3(0, 1.4f, -9.0f), new Vector3(W, 2.73f, 0.2f), Q, "Plaster"),
            new(new Vector3(0, 1.4f, 9.0f), new Vector3(W, 2.73f, 0.2f), Q, "Plaster"),
        };
        // The sofa, as in the research's flat: soft furnishing scatters the sideways-running sound into
        // the absorbent floor. args "bare" leaves it out.
        if (!args.Contains("bare")) boxes.Add(new(new Vector3(3.2f, 0.32f, -3.4f), new Vector3(1.8f, 0.6f, 1.8f), Q, "Audience"));
        var ear = new Vector3(0.175f, 1.7f, 0.16f);
        if (args.Contains("corridor"))
        {
            // 60 m long (north-south), 3 m wide, 3 m high, concrete; standing in the middle.
            boxes = new List<SteamAudioScene.Box>
            {
                new(new Vector3(0, -0.1f, 0), new Vector3(3.4f, 0.2f, 60f), Q, "Concrete"),
                new(new Vector3(0, 3.1f, 0), new Vector3(3.4f, 0.2f, 60f), Q, "Concrete"),
                new(new Vector3(-1.6f, 1.5f, 0), new Vector3(0.2f, 3f, 60f), Q, "Concrete"),
                new(new Vector3(1.6f, 1.5f, 0), new Vector3(0.2f, 3f, 60f), Q, "Concrete"),
                new(new Vector3(0, 1.5f, -30.1f), new Vector3(3.4f, 3f, 0.2f), Q, "Concrete"),
                new(new Vector3(0, 1.5f, 30.1f), new Vector3(3.4f, 3f, 0.2f), Q, "Concrete"),
            };
            ear = new Vector3(0f, 1.6f, 0f);
        }
        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        using var tr = new TracedReverb(ctx) { ExtractLate = true };
        tr.SetScene(scene);
        tr.SetListener(ear);
        for (int t = 0; t < 300 && (tr.LastReadBack == null || tr.Runs < 3); t++) Thread.Sleep(100);
        var ir = tr.LastReadBack;
        if (ir == null) { Console.WriteLine("FAIL: no read-back"); return 1; }

        // Surfaces and the table's absorption (low ~250 Hz, mid ~1 kHz, high ~4 kHz).
        float V = W * D * H;
        var surfaces = new (float Area, string Mat)[] { (W * D, "Carpet"), (W * D, "Plaster"), (D * H, "Brick"), (D * H, "Plaster"), (2 * W * H, "Plaster") };
        float S = surfaces.Sum(x => x.Area);
        string[] names = { "low", "mid", "high" };
        Console.WriteLine($"flat 01F: V {V:F0} m3, S {S:F0} m2; {tr.Runs} traces");
        for (int b = 0; b < 3; b++)
        {
            float A = surfaces.Sum(x => { var p = AcousticRegistry.GetProperties(x.Mat); return x.Area * (b == 0 ? p.AbsorptionLow : b == 1 ? p.AbsorptionMid : p.AbsorptionHigh); });
            float sabine = 0.161f * V / A, eyring = 0.161f * V / (-S * MathF.Log(1f - A / S));
            // Fitzroy: absorption concentrated on one pair of faces (the carpet) leaves the sound running
            // between the hard ones living long; Sabine assumes it is spread evenly.
            float Pair(float area, float a) => area / MathF.Max(1e-4f, -MathF.Log(1f - MathF.Min(0.99f, a)));
            float Ab(string m) { var p = AcousticRegistry.GetProperties(m); return b == 0 ? p.AbsorptionLow : b == 1 ? p.AbsorptionMid : p.AbsorptionHigh; }
            float floorCeil = (Ab("Carpet") + Ab("Plaster")) / 2f, eastWest = (Ab("Brick") + Ab("Plaster")) / 2f, northSouth = Ab("Plaster");
            float fitzroy = 0.161f * V / (S * S) * (Pair(2 * W * D, floorCeil) + Pair(2 * D * H, eastWest) + Pair(2 * W * H, northSouth));
            Console.WriteLine($"  {names[b],4}: mean absorption {A / S:F3}  Sabine {sabine:F2} s  Eyring {eyring:F2} s  Fitzroy {fitzroy:F2} s");
        }
        foreach (double f in new[] { 125.0, 250, 500, 1000, 2000, 4000 })
        {
            var x = Band(ir, f);
            // Schroeder backward integral, T20 from -5 to -25 dB.
            var e = new double[x.Length]; double acc = 0;
            for (int i = x.Length - 1; i >= 0; i--) { acc += x[i] * (double)x[i]; e[i] = acc; }
            double total = e[0] + 1e-30;
            int i5 = Array.FindIndex(e, v => 10 * Math.Log10(v / total) <= -5), i25 = Array.FindIndex(e, v => 10 * Math.Log10(v / total) <= -25);
            string t20 = i5 >= 0 && i25 > i5 ? $"{3.0 * (i25 - i5) / Fs:F2} s" : "  n/a";
            double late = 0; for (int i = (int)(0.08 * Fs); i < x.Length; i++) late += x[i] * (double)x[i];
            Console.WriteLine($"  {f,5:F0} Hz: T20 {t20}, late energy (80 ms on) {10 * Math.Log10(late + 1e-30):F1} dB");
        }
        // The directional part: which way the 50-300 ms of the tail arrives from, as SDM splits it.
        for (int t = 0; t < 100 && tr.LateSdm == null; t++) Thread.Sleep(100);
        if (tr.LateSdm is { } sdm)
        {
            var order = Enumerable.Range(0, sdm.Share.Length).OrderByDescending(i => sdm.Share[i]).ToArray();
            string Name(Vector3 d) => $"{(d.X >= 0 ? "E" : "W")}{MathF.Abs(d.X):F1} {(d.Z >= 0 ? "N" : "S")}{MathF.Abs(d.Z):F1} {(d.Y >= 0 ? "up" : "down")}{MathF.Abs(d.Y):F1}";
            float even = 1f / sdm.Share.Length;
            Console.WriteLine($"  directional part (SDM), share per direction, even would be {even:P0}:");
            foreach (var i in order.Take(8)) Console.WriteLine($"    {sdm.Share[i],6:P1}  {Name(DiffuseTail.Direction(i))}");
            // Horizontal lean: net pull of the directional energy.
            var net = Vector3.Zero; for (int i = 0; i < sdm.Share.Length; i++) net += sdm.Share[i] * DiffuseTail.Direction(i);
            Console.WriteLine($"    net pull {net.Length():F2} toward {Name(Vector3.Normalize(net))} (the ear is at x {ear.X:F2}, z {ear.Z:F2})");
            // The remainder, by axis: how much of it runs north-south, east-west, up-down.
            float ns = 0, ew = 0, ud = 0;
            for (int i = 0; i < sdm.LateShare.Length; i++)
            {
                var d = DiffuseTail.Direction(i);
                float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
                if (az >= ax && az >= ay) ns += sdm.LateShare[i]; else if (ax >= ay) ew += sdm.LateShare[i]; else ud += sdm.LateShare[i];
            }
            int nNs = 0, nEw = 0, nUd = 0;
            for (int i = 0; i < sdm.LateShare.Length; i++)
            {
                var d = DiffuseTail.Direction(i);
                float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
                if (az >= ax && az >= ay) nNs++; else if (ax >= ay) nEw++; else nUd++;
            }
            Console.WriteLine($"  remainder (after 0.3 s) by axis: north-south {ns:P0} ({nNs} directions), east-west {ew:P0} ({nEw}), up-down {ud:P0} ({nUd})");
        }
        else Console.WriteLine("  no directional part (OPENFPS_TAIL_SDM=0, or the ambisonic tail)");
        return 0;
    }

    private static float[] Band(float[] x, double f0)
    {
        var y = (float[])x.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            double w = 2 * Math.PI * f0 / Fs, q = Math.Sqrt(2), alpha = Math.Sin(w) / (2 * q);
            double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w), a2 = 1 - alpha;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double xi = y[i], yi = (b0 * xi + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi; y[i] = (float)yi;
            }
        }
        return y;
    }
}
