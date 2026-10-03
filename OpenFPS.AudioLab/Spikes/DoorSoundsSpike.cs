using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Every door kind opened and shut through the real <see cref="DoorSystem"/>, its events rendered by
/// <see cref="DoorMechanisms"/> at the times and levels the server sends them, dry.
///
///   --door-sounds [out=DIR] [seed=N]
///
/// Writes DIR/KIND/sequence.wav (the whole open and shut, each event at its level against the
/// door's main hit), DIR/KIND/NN-event.wav (each event alone, at its own level) and DIR/measure.txt:
/// per event its band balance in the 30 ms from its loudest onset, d20 (full band and 4 kHz), the
/// most prominent spectral lines, and how long it took to render.
/// </summary>
public static class DoorSoundsSpike
{
    private const int Sr = 48000;
    private const float Dt = PhysicsConstants.FixedDeltaTime;

    /// <summary>Each kind, and the room its best reference was recorded in (late decay, C50), which
    /// its render is measured through so the two lines are read over the same kind of tail.</summary>
    private static readonly (string Prefab, string Name, bool FromOutside, bool HandShut, float T60, float C50)[] Doors =
    {
        ("door", "knob", false, true, 0.4f, 13f),                         // kyles light wood
        ("steel_door", "pushbar", false, false, 0.8f, 5f),                // berumen
        ("glass_front_door", "glass-pushbar/outside-key", true, false, 0.5f, 8f), // vaztur
        ("glass_front_door", "glass-pushbar/inside-bar", false, false, 0.5f, 8f),
        ("glass_pull_door", "glass-pull", false, false, 0.3f, 14f),       // kraftaggregat
        ("auto_sliding_door", "auto-slide", false, false, 0.6f, 8f),      // a shop entrance
        ("patio_door", "patio-slide", false, true, 0.5f, 8f),             // goblinjack, kijjaz
        ("elevator_door", "elevator", false, false, 1.0f, 5f),            // a lift lobby
    };

    public static int Run(string[] args)
    {
        string? measure = args.FirstOrDefault(a => a.StartsWith("measure=", StringComparison.Ordinal))?[8..];
        if (measure != null) return MeasureFile(measure);
        string outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "door-sounds";
        int seed = int.TryParse(args.FirstOrDefault(a => a.StartsWith("seed="))?[5..], out int sd) ? sd : 1;
        AcousticRegistry.Initialize();
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var report = new StringBuilder();
        foreach (var (prefab, name, outside, handShut, roomT60, roomC50) in Doors)
        {
            var world = World.Create();
            try
            {
                var doors = new DoorSystem();
                var e = prefabs.Spawn(world, prefab, new Vector3(0, 1.05f, 0), Quaternion.Identity, Vector3.One);
                var heard = new List<(float T, string Key, TransientSound Sound)>();
                float now = 0f;
                void Tick(float seconds)
                {
                    for (int i = 0; i < (int)MathF.Ceiling(seconds / Dt); i++, now += Dt)
                        doors.Update(world, Dt, _ => { }, (_, key, sounds) => { foreach (var s in sounds) heard.Add((now, key, s)); });
                }
                Tick(0.1f);
                float t0 = now;
                DoorSystem.Set(world, e, open: true, by: new Vector3(0.2f, 1.6f, outside ? 1.5f : -1.5f));
                var d = world.Get<DoorComponent>(e);
                Tick(d.SwingSeconds + 1.0f);
                if (handShut) DoorSystem.Set(world, e, open: false);
                Tick(d.CloseAfterSeconds + MathF.Max(d.CloseSeconds, d.SwingSeconds) + 3.5f);

                // "kind/tag": a kind heard two ways shares its folder, each file named by the way.
                string dir = Path.Combine(outDir, name.Split('/')[0]);
                string tag = name.Contains('/') ? name.Split('/')[1] + "-" : "";
                Directory.CreateDirectory(dir);
                report.AppendLine($"== {name} ({prefab})");
                var leafSpec = DoorSystem.Spec(world, e, world.Get<DoorComponent>(e), (DoorKind)world.Get<DoorComponent>(e).Kind);
                var modes = DoorMechanisms.LeafModes(leafSpec);
                report.AppendLine($"   leaf {leafSpec.Material} {leafSpec.Width}x{leafSpec.Height}x{leafSpec.Thickness} skin {leafSpec.SkinMetres} pane {leafSpec.PaneMetres}: "
                                + $"{modes.Count} modes, {DoorMechanisms.LeafMassKg(leafSpec):F0} kg, first " + string.Join(" ", modes.Take(6).Select(q => $"{q.Hz:0}")));
                float top = heard.Count == 0 ? 0f : heard.Max(h => h.Sound.LevelDb);
                var rendered = new List<(float At, float[] Pcm, float Db)>();
                int k = 0;
                foreach (var (t, key, s) in heard)
                {
                    if (!DoorMechanisms.TryParseKey(s.SynthKey, out var spec)) continue;
                    var clock = Stopwatch.StartNew();
                    var pcm = DoorMechanisms.Render(spec, Sr, seed + k);
                    double ms = clock.Elapsed.TotalMilliseconds;
                    float at = t - t0 + s.DelaySeconds;
                    rendered.Add((at, pcm, s.LevelDb));
                    string ev = key[(key.LastIndexOf(':') + 1)..];
                    Wav(Path.Combine(dir, $"{tag}{k:00}-{ev}.wav"), pcm, 0.89f / MathF.Max(1e-6f, pcm.Max(MathF.Abs)));
                    report.AppendLine(Describe(k, at, ev, s, pcm, ms, roomT60, roomC50));
                    k++;
                }
                // The whole sequence, each event at its level against the loudest.
                float len = rendered.Count == 0 ? 0.1f : rendered.Max(r => r.At + (float)r.Pcm.Length / Sr) + 0.2f;
                var seq = new float[(int)(len * Sr)];
                foreach (var (at, pcm, db) in rendered)
                {
                    float g = MathF.Pow(10f, (db - top) / 20f);
                    int a = (int)(at * Sr);
                    for (int i = 0; i < pcm.Length && a + i < seq.Length; i++) seq[a + i] += pcm[i] * g;
                }
                float peak = seq.Length == 0 ? 1f : MathF.Max(1e-6f, seq.Max(MathF.Abs));
                Wav(Path.Combine(dir, $"{tag}sequence.wav"), seq, 0.89f / peak);
                report.AppendLine($"   sequence {len:F2} s, events: " + string.Join(", ", rendered.Select((r, i) => $"{r.At:F2}s {r.Db - top:+0;-0} dB")));
            }
            finally { World.Destroy(world); }
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "measure.txt"), report.ToString());
        Console.Write(report.ToString());
        return 0;
    }

    /// <summary>
    /// A recording measured the same way: measure=PATH@T1,T2,... with each T an event onset, seconds.
    /// The recording keeps its own room, so its lines are read as they are.
    /// </summary>
    private static int MeasureFile(string arg)
    {
        int at = arg.LastIndexOf('@');
        string path = arg[..at];
        var (pcm, rate) = ReadWav(path);
        foreach (string t in arg[(at + 1)..].Split(','))
        {
            if (t.Contains('-'))
            {
                // A span: the octave levels of a steady sound over it, and its strongest lines.
                var ab = t.Split('-');
                float a = float.Parse(ab[0], CultureInfo.InvariantCulture), b = float.Parse(ab[1], CultureInfo.InvariantCulture);
                Console.WriteLine($"  {Path.GetFileNameWithoutExtension(path)} {a:F2}-{b:F2}s | run {Run(pcm, rate, a, b)}");
                continue;
            }
            float t0 = float.Parse(t, CultureInfo.InvariantCulture);
            var bands = SoundMeasure.Bands(pcm, rate, t0);
            float d20 = SoundMeasure.D20(pcm, rate, t0), d4k = SoundMeasure.D20(pcm, rate, t0, 4000f);
            var dry = SoundMeasure.Peaks(pcm, rate, t0 + 0.002f, 0.186f, 60f, 2000f, 6f, 4);
            var all = SoundMeasure.Peaks(pcm, rate, t0 + 0.002f, 0.186f, 60f, 12000f, 6f, 5);
            Console.WriteLine($"  {Path.GetFileNameWithoutExtension(path)} @{t0:F3} | "
                + string.Join(" ", bands.Select(v => v.ToString("0", CultureInfo.InvariantCulture).PadLeft(4)))
                + $" | d20 {d20 * 1000:0} 4k {d4k * 1000:0} | <2k " + string.Join(" ", dry.Select(q => $"{q.Hz:0}/{q.ProminenceDb:0}"))
                + " | all " + string.Join(" ", all.Select(q => $"{q.Hz:0}/{q.ProminenceDb:0}/{q.LevelDb:0}")));
        }
        return 0;
    }

    internal static (float[] Pcm, int Rate) ReadWav(string path)
    {
        var b = File.ReadAllBytes(path);
        int rate = BitConverter.ToInt32(b, 24), channels = BitConverter.ToInt16(b, 22);
        int i = 12;
        while (i + 8 <= b.Length && !(b[i] == 'd' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a'))
            i += 8 + BitConverter.ToInt32(b, i + 4);
        int len = BitConverter.ToInt32(b, i + 4), start = i + 8;
        int frames = Math.Min(len, b.Length - start) / (2 * channels);
        var pcm = new float[frames];
        for (int f = 0; f < frames; f++) pcm[f] = BitConverter.ToInt16(b, start + f * 2 * channels) / 32768f;
        return (pcm, rate);
    }

    /// <summary>A steady sound's octave levels over a span (dB re its loudest octave) and its most
    /// prominent lines in a 186 ms window at the middle.</summary>
    internal static string Run(float[] pcm, int rate, float a, float b)
    {
        var lv = SoundMeasure.BandLevels(pcm, rate, a, b);
        float top = lv.Max();
        var lines = SoundMeasure.Peaks(pcm, rate, 0.5f * (a + b) - 0.093f, 0.186f, 200f, 6000f, 6f, 4);
        return string.Join(" ", lv.Select(v => (v - top).ToString("0", CultureInfo.InvariantCulture).PadLeft(4)))
             + " | lines " + string.Join(" ", lines.Select(q => $"{q.Hz:0}/{q.ProminenceDb:0}"));
    }

    /// <summary>One event's numbers, from its loudest onset.</summary>
    private static string Describe(int k, float at, string ev, TransientSound s, float[] pcm, double ms, float roomT60, float roomC50)
    {
        var env = SoundMeasure.Envelope(pcm, Sr);
        int j = 0;
        for (int i = 0; i < env.Length; i++) if (env[i] > env[j]) j = i;
        // The first frame within 6 dB of the loudest: the main hit, not a rattle that happened to
        // peak a decibel higher after it.
        int first = 0;
        while (first < j && env[first] < env[j] - 6f) first++;
        float onset = MathF.Max(0f, first * 0.002f - 0.004f);
        var bands = SoundMeasure.Bands(pcm, Sr, onset);
        if (Environment.GetEnvironmentVariable("DOORSND_FFT") == "1")
        {
            // The same balance from one FFT of the window, to check the band filters against.
            var ps = SoundMeasure.Spectrum(pcm, Sr, onset, 0.03f, out int nfft);
            var fb = SoundMeasure.Centres.Select(c => 10f * MathF.Log10(1e-20f + (float)Enumerable.Range(0, ps.Length)
                .Where(q => q * (float)Sr / nfft >= c / MathF.Sqrt(2f) && q * (float)Sr / nfft < c * MathF.Sqrt(2f)).Sum(q => ps[q]))).ToArray();
            Console.WriteLine($"     fft {ev}: " + string.Join(" ", fb.Select(v => (v - fb.Max()).ToString("0").PadLeft(4))));
        }
        float d20 = SoundMeasure.D20(pcm, Sr, onset), d4k = SoundMeasure.D20(pcm, Sr, onset, 4000f);
        if (DoorMechanisms.TryParseKey(s.SynthKey, out var sp) && DoorMechanisms.IsMotionEvent(sp.Event) && sp.Seconds > 0.8f)
            return $"  {k:00} {at,6:F2}s {ev,-14} {s.LevelDb,5:F1} dB  {pcm.Length / (float)Sr:F2}s {ms,4:F0}ms | run {Run(pcm, Sr, 0.35f, sp.Seconds - 0.3f)}";
        var dry = SoundMeasure.Peaks(pcm, Sr, onset + 0.002f, 0.186f, 60f, 2000f, 6f, 4);
        var roomed = Room(pcm, roomT60, roomC50);
        var wet = SoundMeasure.Peaks(roomed, Sr, onset + 0.002f, 0.186f, 60f, 12000f, 6f, 5);
        string b = string.Join(" ", bands.Select(v => v.ToString("0", CultureInfo.InvariantCulture).PadLeft(4)));
        string p = string.Join(" ", dry.Select(q => $"{q.Hz:0}/{q.ProminenceDb:0}"));
        string w = string.Join(" ", wet.Select(q => $"{q.Hz:0}/{q.ProminenceDb:0}/{q.LevelDb:0}"));
        return $"  {k:00} {at,6:F2}s {ev,-14} {s.LevelDb,5:F1} dB  {pcm.Length / (float)Sr:F2}s {ms,4:F0}ms | {b} | d20 {d20 * 1000:0} 4k {d4k * 1000:0} | dry<2k {p} | room {w}";
    }

    /// <summary>The render through the room its reference was recorded in (SoundMeasure.ThroughRoom).</summary>
    internal static float[] Room(float[] x, float t60, float c50Db) => SoundMeasure.ThroughRoom(x, Sr, t60, c50Db);

    private static void Wav(string path, float[] pcm, float gain)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Sr); w.Write(Sr * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * gain * 32767f, -32768f, 32767f));
    }
}
