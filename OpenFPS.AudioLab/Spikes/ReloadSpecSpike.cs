using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// The handling noises of a gun, measured: the spec <see cref="OpenFPS.Common.WeaponHandling"/> is
/// synthesised to, and the same measurement run over what it renders.
///
///   --reload-spec [refs=DIR] [only=TEXT]          the recordings (inbox/weapons), measured
///   --reload-sounds [out=DIR]                     every reload and dry fire, rendered and measured
///
/// The recordings are a SPEC and nothing else: nothing here is played or shipped. For each file it
/// finds the separate contacts (a release, a magazine seating, a bolt going home), and says for each
/// when it came, how loud it was against the loudest, how long it took to fall 20 dB, and its octave
/// bands against its own loudest band. Those are the numbers the synthesis is fitted to.
/// </summary>
public static class ReloadSpecSpike
{
    /// <summary>Octave centres measured, Hz.</summary>
    public static readonly float[] Centres = { 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    public static int Run(string[] args)
    {
        string refs = Arg(args, "refs") ?? LabPaths.InRepo("inbox", "weapons");
        string? only = Arg(args, "only");
        if (!Directory.Exists(refs)) { Console.Error.WriteLine($"No folder {refs}."); return 1; }
        Console.WriteLine("  file | contacts: at ms, dB re loudest, ms to -20 dB, octave bands 63..16k dB re loudest band\n");
        foreach (string file in Directory.GetFiles(refs, "*.wav", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (only != null && !file.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var (pcm, sr) = ReadWav(file);
            Console.WriteLine($"{Path.GetFileName(file)}");
            Console.WriteLine(Describe(pcm, sr));
        }
        return 0;
    }

    /// <summary>Every reload and dry fire as the game asks for them, written as WAVs at -1 dBFS peak
    /// and measured as the recordings were.</summary>
    public static int Render(string[] args)
    {
        string dir = Arg(args, "out") ?? LabPaths.InRepo("inbox", "reload-sounds-2026-10-03");
        Directory.CreateDirectory(dir);
        const int sr = 48000;
        var made = new List<(string Name, float[] Pcm, float Seconds, float LevelDb)>();
        foreach (var w in OpenFPS.Common.WeaponRegistry.All.OrderBy(w => w.Id, StringComparer.Ordinal))
        {
            var keys = new List<(string Name, string Key)>();
            if (w.Feed == OpenFPS.Common.WeaponFeed.Tube)
            {
                keys.Add(($"reload-{w.Id}-1-shell", OpenFPS.Common.WeaponHandling.ReloadKey(w, 1, fromEmpty: false)));
                keys.Add(($"reload-{w.Id}-empty", OpenFPS.Common.WeaponHandling.ReloadKey(w, w.MagazineCapacity, fromEmpty: true)));
            }
            else
            {
                keys.Add(($"reload-{w.Id}-partial", OpenFPS.Common.WeaponHandling.ReloadKey(w, w.MagazineCapacity, fromEmpty: false)));
                keys.Add(($"reload-{w.Id}-empty", OpenFPS.Common.WeaponHandling.ReloadKey(w, w.MagazineCapacity, fromEmpty: true)));
            }
            keys.Add(($"dryfire-{w.Id}", OpenFPS.Common.WeaponHandling.DryFireKey(w)));
            foreach (var (name, key) in keys)
            {
                Assert(OpenFPS.Common.WeaponHandling.TryParseKey(key, out var spec), key);
                var pcm = OpenFPS.Common.WeaponHandling.Render(spec, sr, seed: 7);
                made.Add((name, pcm, OpenFPS.Common.WeaponHandling.Seconds(spec), OpenFPS.Common.WeaponHandling.LevelDb(spec)));
            }
        }
        foreach (var (name, pcm, seconds, levelDb) in made)
        {
            float peak = pcm.Max(v => Math.Abs(v));
            float gain = 0.891f / Math.Max(1e-9f, peak);   // -1 dBFS
            string path = Path.Combine(dir, name + ".wav");
            WriteWav(path, pcm, sr, gain);
            float written = peak * gain;
            Console.WriteLine($"{name}: {pcm.Length / (float)sr:F2} s long, the action takes {seconds:F2} s, "
                            + $"peak {20 * Math.Log10(written):F1} dBFS, {levelDb:F0} dB SPL at 1 m in game");
            Console.WriteLine(Describe(pcm, sr));
        }
        Console.WriteLine($"wrote {made.Count} files to {dir}");
        return 0;
    }

    private static void Assert(bool ok, string what) { if (!ok) throw new InvalidOperationException($"key did not parse: {what}"); }

    /// <summary>The contacts in a recording, each with its time, level, fall and bands.</summary>
    public static string Describe(float[] x, int sr)
    {
        var text = new StringBuilder();
        int hop = Math.Max(1, sr / 1000);                    // 1 ms frames
        int frames = x.Length / hop;
        var env = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double s = 0;
            for (int i = 0; i < hop; i++) { float v = x[f * hop + i]; s += v * v; }
            env[f] = (float)(10 * Math.Log10(s / hop + 1e-20));
        }
        float top = env.Length == 0 ? -200f : env.Max();
        int last = Array.FindLastIndex(env, e => e > top - 40f);
        int first = Array.FindIndex(env, e => e > top - 40f);
        text.Append($"    {x.Length / (float)sr:F2} s; sound from {first} to {last} ms ({last - first} ms)\n");

        // A contact: the 1 ms level jumps 12 dB over the quietest of the 6 ms before it, within 30 dB of
        // the loudest, and at least 25 ms after the last one.
        var onsets = new List<int>();
        for (int f = 6; f < frames; f++)
        {
            float floor = float.MaxValue;
            for (int k = f - 6; k < f; k++) floor = Math.Min(floor, env[k]);
            if (env[f] - floor < 12f || env[f] < top - 30f) continue;
            if (onsets.Count > 0 && f - onsets[^1] < 25) continue;
            onsets.Add(f);
        }
        foreach (int on in onsets.Take(12))
        {
            // Its peak within 5 ms, and how long it takes to fall 20 dB from there.
            int pk = on;
            for (int f = on; f < Math.Min(frames, on + 5); f++) if (env[f] > env[pk]) pk = f;
            int fall = pk;
            while (fall < frames - 1 && env[fall] > env[pk] - 20f) fall++;
            var bands = Bands(x, sr, on * hop, Math.Min(x.Length, (on + 20) * hop));
            float maxBand = bands.Max();
            text.Append($"    {on,5} ms {env[pk] - top,6:F1} dB  -20 in {fall - pk,3} ms  |");
            foreach (float b in bands) text.Append($" {b - maxBand,5:F0}");
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Energy per octave over a window, dB.</summary>
    public static float[] Bands(float[] x, int sr, int from, int to)
    {
        var result = new float[Centres.Length];
        // Start the filters 20 ms early so they are full when the window opens.
        int pre = Math.Max(0, from - sr / 50);
        for (int b = 0; b < Centres.Length; b++)
        {
            if (Centres[b] * 1.41f >= sr * 0.49f) { result[b] = -120f; continue; }
            var bp = new Biquad(Centres[b], sr);
            var bp2 = new Biquad(Centres[b], sr);
            double e = 1e-20;
            for (int i = pre; i < to; i++)
            {
                float y = bp2.Run(bp.Run(x[i]));
                if (i >= from) e += y * y;
            }
            result[b] = (float)(10 * Math.Log10(e));
        }
        return result;
    }

    /// <summary>An RBJ band-pass, constant peak gain, an octave wide; run twice for steeper skirts.</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;
        public Biquad(double f, double sr)
        {
            double w = 2 * Math.PI * f / sr, q = 1.41;
            double alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            _b0 = alpha / a0; _b2 = -alpha / a0; _a1 = -2 * Math.Cos(w) / a0; _a2 = (1 - alpha) / a0;
        }
        public float Run(float x)
        {
            double y = _b0 * x + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return (float)y;
        }
    }

    public static (float[] Pcm, int Rate) ReadWav(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(12);
        int rate = 44100, channels = 1, bits = 16;
        while (r.BaseStream.Position < r.BaseStream.Length - 8)
        {
            string id = Encoding.ASCII.GetString(r.ReadBytes(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                int n = size / (bits / 8) / channels;
                var pcm = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float s = 0f;
                    for (int c = 0; c < channels; c++)
                        s += bits == 16 ? r.ReadInt16() / 32768f
                           : bits == 24 ? ((r.ReadByte() | r.ReadByte() << 8 | (sbyte)r.ReadByte() << 16) / 8388608f)
                           : r.ReadSingle();
                    pcm[i] = s / channels;
                }
                return (pcm, rate);
            }
            else r.ReadBytes(size + (size & 1));
        }
        return (Array.Empty<float>(), rate);
    }

    private static void WriteWav(string path, float[] pcm, int sr, float gain)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * gain * 32767f, -32768f, 32767f));
    }

    private static string? Arg(string[] args, string name)
        => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
}
