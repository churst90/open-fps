using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// A clap in Marlow Tower flat 01F, through the whole mixer: the listener's traced response of the
/// flat, the reverb sends, the decorrelator, the master limiter, written to a WAV and read back.
///
///   --clap-room [out=path] [claps=4]
///
/// Written because "a bathroom stall rather than a carpeted room" was read off captures full of city
/// noise, where the room's answer could only be estimated. Here nothing else is playing. What it
/// reports is the room's answer against the clap itself, in the windows the ear separates: the
/// clap (0-6 ms), the early part (6-50 ms), the late part (50-300 ms). The traced flat on its own
/// (--traced-reverb) answers -4.5 dB against a one-metre impulse, so a clap half a metre from the
/// ear should have the room about ten decibels under it.
/// </summary>
public static class ClapRoomSpike
{
    private const int RoomId = 87;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string outPath = Arg(args, "out") ?? "/tmp/openfps-clap-room.wav";
        int claps = int.TryParse(Arg(args, "claps"), out int c) ? c : 4;

        // The flat, in its own frame: 8.65 x 17.86 x 2.7 m, carpet on concrete, plaster over, plaster
        // walls and the brick outer wall, a sofa. The ear where /tp -14 -85 0.5 puts it.
        var q = Quaternion.Identity;
        var boxes = new List<SteamAudioScene.Box>
        {
            new(new Vector3(0, 0.04f, 0), new Vector3(8.65f, 0.04f, 17.86f), q, "Carpet"),
            new(new Vector3(0, -0.07f, 0), new Vector3(21f, 0.17f, 90f), q, "Concrete"),
            new(new Vector3(0, 2.735f, 0), new Vector3(20.5f, 0.03f, 89.3f), q, "Plaster"),
            new(new Vector3(4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 80f), q, "Brick"),
            new(new Vector3(-4.5f, 1.4f, 0), new Vector3(0.35f, 2.73f, 17.86f), q, "Plaster"),
            new(new Vector3(0, 1.4f, -9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
            new(new Vector3(0, 1.4f, 9.0f), new Vector3(8.65f, 2.73f, 0.2f), q, "Plaster"),
            new(new Vector3(3.2f, 0.32f, -3.4f), new Vector3(1.8f, 0.6f, 1.8f), q, "Audience"),
        };
        var ear = new Vector3(0.175f, 1.7f, 0.16f);
        var hands = ear + new Vector3(0f, -0.39f, 0.3f);    // where the server puts a clap: 0.5 m off

        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no Steam Audio context"); return 1; }
        var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        TracedReverbSet.Configure(ctx, scene);
        TracedReverbSet.SetListener(ear);

        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", outPath);
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }
        var placed = Loudness.Place(92f);
        var clapTimes = new List<double>();
        try
        {
            provider.SetAcousticMap(BuildMap());
            // sound=click: one sample, flat in spectrum and with no resonance of its own, so whatever
            // rings in the answer is the room's (or the renderer's), not the clap's.
            float[] pcm;
            if (Arg(args, "sound") == "click") { pcm = new float[TransientSynth.SampleRate / 10]; pcm[0] = 1f; }
            else pcm = Applause.RenderClap(TransientSynth.SampleRate, 1);
            provider.RegisterSynthesisedSound("synth:clap:lab", TransientSynth.ToPcm16(pcm), TransientSynth.SampleRate);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double next = 3.0;
            int played = 0;
            // Every other clap with the head turned to face east (game +x): the room must not turn with it.
            var facing = Quaternion.Identity;
            while (sw.Elapsed.TotalSeconds < 3.0 + claps * 1.5 + 1.0)
            {
                double t = sw.Elapsed.TotalSeconds;
                provider.UpdateListener(ear, facing, Vector3.Zero, RoomId);
                if (played < claps && t >= next)
                {
                    next += 1.5;
                    clapTimes.Add(t);
                    facing = (played % 2 == 1) ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) : Quaternion.Identity;
                    provider.UpdateListener(ear, facing, Vector3.Zero, RoomId);
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = -100 - played++, SoundId = "synth:clap:lab",
                        Mode = PlaybackMode.Single, Type = EmitterType.WorldLocked,
                        Position = hands, ApparentPosition = hands,
                        Volume = placed.Gain, Range = 200f, MinDistance = placed.ReferenceDistance, Pitch = 1f,
                        ConeInside = 360f, ConeOutside = 360f, ConeOutsideVolume = 1f,
                        EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
                        IsEvent = true, Essential = true, EnableReverb = true, TargetRegionId = RoomId,
                    });
                }
                provider.Update();
                if (clapTimes.Count > 0 && t - clapTimes[^1] is > 0.02 and < 0.035) Console.WriteLine($"  clap {clapTimes.Count} sends: {provider.ListSends(RoomId)}");

                Thread.Sleep(10);
            }
            var m = provider.TracedMeter(RoomId);
            Console.WriteLine($"  traced stage: {10 * Math.Log10(m.Out / Math.Max(1e-20, m.In)):F1} dB out per ear against its mono input; "
                            + $"{10 * Math.Log10(m.Out / Math.Max(1e-20, m.PerChannel)):F1} dB against the mean of its {m.Channels} input channels; head blend {m.Blend:F2}");
            Console.WriteLine($"  reverb: traced, listener trace {(TracedReverbSet.Listener != null ? "ready" : "MISSING")}, reflections {FmodAudioProvider.TailDb:F0} tail, {FmodAudioProvider.CopiesDb:F0} copies dB, makeup {FmodAudioProvider.MasterMakeupDb:F0} dB");
        }
        finally { provider.Dispose(); TracedReverbSet.Dispose(); scene.Dispose(); Phonon.iplContextRelease(ref ctx); }

        return Measure(outPath);
    }

    /// <summary>Each clap in the WAV: found by its onset, then the room's answer against it.</summary>
    private static int Measure(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine($"  FAIL: no {path}"); return 1; }
        var (l, r, sr) = ReadStereo(path);
        double Db(double e) => 10 * Math.Log10(e + 1e-20);
        double E(int from, double a, double b)
        {
            double e = 0; int i0 = from + (int)(a * sr), i1 = Math.Min(l.Length, from + (int)(b * sr));
            for (int i = i0; i < i1; i++) e += (l[i] * (double)l[i] + r[i] * (double)r[i]) / 2;
            return e;
        }
        Console.WriteLine("  clap | peak dBFS | room against the clap (0-6 ms): 6-50 ms   50-300 ms   total | tail 50-200 ms: L/R dB, IACC 150-300 / 300-600 / 600-1200 / 1200-2400 / 2400-4800 Hz");
        int at = 0, found = 0;
        while (at < l.Length && found < 20)
        {
            int on = -1;
            for (int i = at; i < l.Length; i++) if (Math.Abs(l[i]) + Math.Abs(r[i]) > 0.1f) { on = i; break; }
            if (on < 0) break;
            on = Math.Max(0, on - sr / 2000);
            double pk = 0; for (int i = on; i < Math.Min(l.Length, on + sr / 100); i++) pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i])));
            double d = E(on, 0, 0.006), early = E(on, 0.006, 0.05), late = E(on, 0.05, 0.3);
            int t0 = on + (int)(0.05 * sr), t1 = Math.Min(l.Length, on + (int)(0.2 * sr));
            double el = 0, er = 0; for (int i = t0; i < t1; i++) { el += l[i] * (double)l[i]; er += r[i] * (double)r[i]; }
            var iacc = new System.Text.StringBuilder();
            foreach (var (lo, hi) in new[] { (150f, 300f), (300f, 600f), (600f, 1200f), (1200f, 2400f), (2400f, 4800f) })
                iacc.Append($" {Iacc(Band(l, t0, t1, lo, hi, sr), Band(r, t0, t1, lo, hi, sr)),5:F2}");
            Console.WriteLine($"  {++found,4} | {20 * Math.Log10(pk + 1e-20),8:F1} | {Db(early) - Db(d),31:F1} {Db(late) - Db(d),11:F1} {Db(early + late) - Db(d),7:F1} | {Db(el) - Db(d),6:F1}/{Db(er) - Db(d),6:F1} {iacc}");
            at = on + sr;       // the next clap is 1.5 s on
        }
        if (found == 0) Console.WriteLine("  FAIL: no clap in the capture");
        return found == 0 ? 1 : 0;
    }

    /// <summary>A band of one ear's tail: two second-order band-passes in a row, run forwards.</summary>
    private static double[] Band(float[] x, int from, int to, float lo, float hi, int sr)
    {
        var y = new double[to - from];
        for (int i = 0; i < y.Length; i++) y[i] = x[from + i];
        foreach (var pass in new[] { 0, 1 })
        {
            float f0 = MathF.Sqrt(lo * hi), bw = MathF.Log2(hi / lo);
            double w0 = 2 * Math.PI * f0 / sr, alpha = Math.Sin(w0) * Math.Sinh(Math.Log(2) / 2 * bw * w0 / Math.Sin(w0));
            double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w0), a2 = 1 - alpha;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double xi = y[i];
                double yi = (b0 * xi + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = xi; y2 = y1; y1 = yi; y[i] = yi;
            }
        }
        return y;
    }

    /// <summary>The largest normalised cross-correlation within ±1 ms.</summary>
    private static double Iacc(double[] a, double[] b)
    {
        double best = -1; int max = 44;
        double ea = 0, eb = 0; for (int i = 0; i < a.Length; i++) { ea += a[i] * a[i]; eb += b[i] * b[i]; }
        double norm = Math.Sqrt(ea * eb) + 1e-20;
        for (int lag = -max; lag <= max; lag++)
        {
            double s = 0;
            for (int i = Math.Max(0, -lag); i < a.Length && i + lag < b.Length; i++) s += a[i] * b[i + lag];
            best = Math.Max(best, s / norm);
        }
        return best;
    }

    private static (float[] L, float[] R, int Sr) ReadStereo(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadBytes(12);
        int sr = 44100, ch = 2, bits = 16;
        while (br.BaseStream.Position < br.BaseStream.Length - 8)
        {
            string id = new string(br.ReadChars(4)); int size = br.ReadInt32();
            if (id == "fmt ")
            {
                int fmt = br.ReadInt16(); ch = br.ReadInt16(); sr = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                br.ReadBytes(size - 16);
                if (fmt != 1 && fmt != 3) Console.WriteLine($"  (format {fmt})");
            }
            else if (id == "data")
            {
                int frames = size / (ch * bits / 8);
                var L = new float[frames]; var R = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float a = bits == 32 ? br.ReadSingle() : br.ReadInt16() / 32768f;
                    float b = ch > 1 ? (bits == 32 ? br.ReadSingle() : br.ReadInt16() / 32768f) : a;
                    for (int k = 2; k < ch; k++) br.ReadBytes(bits / 8);
                    L[i] = a; R[i] = b;
                }
                return (L, R, sr);
            }
            else br.ReadBytes(size);
        }
        return (Array.Empty<float>(), Array.Empty<float>(), sr);
    }

    private static AcousticMap BuildMap()
    {
        var map = new AcousticMap(new Vector3(100, 40, 100), new Vector3(-50, 0, -50)) { GlobalEnvironmentId = AcousticConstants.GlobalRegionId };
        int I(string m) => AcousticRegistry.GetProperties(m).ResonanceIndex;
        map.Regions[RoomId] = new RegionComponent
        {
            FriendlyName = "Marlow Tower flat 01F",
            IsIndoor = true,
            RoomSize = new Vector3(8.65f, 2.7f, 17.86f),
            ReverbTimeScale = 1.0f,
            Materials = new[] { I("Plaster"), I("Brick"), I("Carpet"), I("Plaster"), I("Plaster"), I("Plaster") },
        };
        map.RegionPositions[RoomId] = new Vector3(0f, 1.35f, 0f);
        map.RegionRotations[RoomId] = Quaternion.Identity;
        return map;
    }

    private static string? Arg(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }
}
