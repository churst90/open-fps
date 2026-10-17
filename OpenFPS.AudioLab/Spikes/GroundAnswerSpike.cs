using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --ground-answer [map=city] ear=x,y,z [ear=...] src=x,y,z [src=...] [car=preset] [speed=km/h] [out=DIR] [cost]
///
/// What the ground hands back of each source at the ear on a real map, asked as ClientAudioSystem.ApplyGround asks it
/// (the same two rays down, then GroundAnswer): what the bounce meets and where, the copy, what is taken from each of
/// the voice's bands, and the bands over free field before (sound and the copy Cody approved) and now, with what the
/// real ground carries. Game coordinates (y up), not /tp's.
///
/// car=preset renders that car's live voice (EngineVoiceState, the tailpipe voice with its tyre share, as the game
/// plays it) at speed= km/h (50) for each source: with no ground, with the copy (before) and with the copy and what the
/// ground takes (now, as band gains like the mixer's THREE_EQ), and says each band's level over the dry voice. The two
/// with ground are written to out=DIR (default ~/openfps-listen/ground-answer) at one gain, so they compare by level.
/// cost times GroundAnswer.Reflect for the first source, the per-voice price of the ground on the placement thread.
/// </summary>
public static class GroundAnswerSpike
{
    const int Rate = MixerQuality.DefaultRate, Block = 512;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string mapId = args.FirstOrDefault(a => a.StartsWith("map="))?[4..] ?? "city";
        static Vector3 P(string s) { var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new Vector3(f[0], f[1], f[2]); }
        var ears = args.Where(a => a.StartsWith("ear=")).Select(a => P(a[4..])).ToList();
        var srcs = args.Where(a => a.StartsWith("src=")).Select(a => P(a[4..])).ToList();
        string root = args.FirstOrDefault(a => a.StartsWith("root="))?[5..] ?? AppContext.BaseDirectory;
        string? car = args.FirstOrDefault(a => a.StartsWith("car="))?[4..];
        float speed = float.Parse(args.FirstOrDefault(a => a.StartsWith("speed="))?[6..] ?? "50", CultureInfo.InvariantCulture) / 3.6f;
        string outDir = args.FirstOrDefault(a => a.StartsWith("out="))?[4..]
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "openfps-listen", "ground-answer");

        var world = PathProbeSpike.LoadAsClient(root, mapId);
        var spatial = new SpatialService();
        var down = new[] { -Vector3.UnitY };
        float[] dist = new float[1], abs = new float[1];
        string[] mat = new string[1];
        float c = AudioPhysics.SpeedOfSound;
        if (args.Contains("survey")) return Survey(world, spatial, mapId, c);
        double[] real = new double[3], copy = new double[3];
        Vector3? heading = null;
        foreach (var (ear, src) in ears.SelectMany(e => srcs.Select(s => (e, s))))
        {
            if (heading != ear)
                Console.WriteLine($"  {mapId}: ear ({ear.X:F1}, {ear.Y:F2}, {ear.Z:F1}); bands low/middle/high, dB over free field");
            heading = ear;
            if (!AudioWorld.LookForGround(world, src, ear, down, dist, abs, mat, out float gb, out string material, out var at))
            {
                Console.WriteLine($"  src ({src.X:F1}, {src.Y:F2}, {src.Z:F1}): no ground within reach");
                continue;
            }
            var m = AcousticRegistry.GetProperties(material);
            var was = m;
            was.FlowResistivityKPa = null;
            float d = Vector3.Distance(src, ear);
            if (GroundAnswer.Reflect(gb, m, src, ear, 0f, c) is not { } now || GroundAnswer.Reflect(gb, was, src, ear, 0f, c) is not { } old)
            {
                Console.WriteLine($"  {d,6:F1} m: the surface met ({material}, {gb:F2}) is above source or ear: no bounce");
                continue;
            }
            Console.WriteLine($"  {d,6:F1} m from ({src.X:F1}, {src.Y:F2}, {src.Z:F1}): bounces on {material} at ({at.X:F1}, {at.Z:F1}), "
                            + $"{gb:F2} up; copy {now.LowGain:F2}/{now.HighGain:F2} (was {old.LowGain:F2}/{old.HighGain:F2}), "
                            + $"{now.DelaySeconds * 1000f:F3} ms");
            Console.WriteLine($"           taken {Db(now.ExcessLow),5:F1} {Db(now.ExcessMid),5:F1} {Db(now.ExcessHigh),5:F1}");
            if (GroundAnswer.Powers(gb, m, src, ear, c, real, copy))
            {
                Console.WriteLine($"           before {P10(copy[0]),5:F1} {P10(copy[1]),5:F1} {P10(copy[2]),5:F1}   "
                                + $"now {P10(copy[0]) + Db(now.ExcessLow),5:F1} {P10(copy[1]) + Db(now.ExcessMid),5:F1} {P10(copy[2]) + Db(now.ExcessHigh),5:F1}   "
                                + $"real ground {P10(real[0]),5:F1} {P10(real[1]),5:F1} {P10(real[2]),5:F1}");
            }
            if (car != null) Render(car, speed, now, d, outDir);
        }
        var ear0 = ears[0];

        if (args.Contains("cost") && srcs.Count > 0
            && AudioWorld.LookForGround(world, srcs[0], ear0, down, dist, abs, mat, out float h0, out string m0, out _))
        {
            var props = AcousticRegistry.GetProperties(m0);
            var plain = props;
            plain.FlowResistivityKPa = null;
            foreach (var (name, p) in new[] { (m0, props), ($"{m0} without its flow resistivity", plain) })
            {
                for (int i = 0; i < 2000; i++) GroundAnswer.Reflect(h0, p, srcs[0], ear0, 0f, c);
                const int N = 200000;
                var t = Stopwatch.StartNew();
                for (int i = 0; i < N; i++) GroundAnswer.Reflect(h0, p, srcs[0] + new Vector3(i * 1e-6f, 0f, 0f), ear0, 0f, c);
                Console.WriteLine($"  cost on {name}: {t.Elapsed.TotalMilliseconds * 1000.0 / N:F2} µs a voice");
            }
        }
        return 0;
    }

    /// <summary>
    /// survey: every sounding thing the map places (its emission point), heard by a standing ear 25, 50, 100 and 200 m
    /// off in eight directions on whatever ground is there: what the ground under it is, and the most each band loses
    /// now at each distance. Only what loses half a decibel or more anywhere is listed, by sound.
    /// </summary>
    static int Survey(WorldSnapshot world, SpatialService spatial, string mapId, float c)
    {
        var down = new[] { -Vector3.UnitY };
        float[] dist = new float[1], abs = new float[1];
        string[] mat = new string[1];
        float[] ranges = { 25f, 50f, 100f, 200f };
        var rows = new SortedDictionary<string, (int Count, string Under, float[] Worst)>(StringComparer.Ordinal);
        int sources = 0;
        foreach (var e in world.Entities.Values)
        {
            var def = e.Definition;
            if (def?.SoundEmitter == null || string.IsNullOrEmpty(def.SoundEmitter.SoundId)) continue;
            var src = EntityEmission.PointFor(e);
            sources++;
            spatial.RaycastAll(world, src + new Vector3(0f, 0.05f, 0f), down, 30f, dist, abs, mat, staticOnly: true);
            string under = dist[0] < 30f ? mat[0] ?? "Generic" : "nothing";
            float ground = dist[0] < 30f ? src.Y + 0.05f - dist[0] : src.Y;
            var worst = new float[ranges.Length * 3];
            for (int r = 0; r < ranges.Length; r++)
                for (int k = 0; k < 8; k++)
                {
                    float a = k * MathF.PI / 4f;
                    var flat = new Vector3(src.X + ranges[r] * MathF.Cos(a), 0f, src.Z + ranges[r] * MathF.Sin(a));
                    // The ear stands on what is there, unless that is a roof: more than 2 m over the source's ground.
                    spatial.RaycastAll(world, flat + new Vector3(0f, ground + 3f, 0f), down, 30f, dist, abs, mat, staticOnly: true);
                    if (!(dist[0] < 30f)) continue;
                    var ear = flat + new Vector3(0f, ground + 3f - dist[0] + 1.7f, 0f);
                    if (!AudioWorld.LookForGround(world, src, ear, down, dist, abs, mat, out float gb, out string m, out _)) continue;
                    if (GroundAnswer.Reflect(gb, AcousticRegistry.GetProperties(m), src, ear, 0f, c) is not { } b) continue;
                    worst[r * 3] = MathF.Min(worst[r * 3], Db(b.ExcessLow));
                    worst[r * 3 + 1] = MathF.Min(worst[r * 3 + 1], Db(b.ExcessMid));
                    worst[r * 3 + 2] = MathF.Min(worst[r * 3 + 2], Db(b.ExcessHigh));
                }
            if (worst.Min() > -0.5f) continue;
            string key = def.SoundEmitter.SoundId;
            if (rows.TryGetValue(key, out var row))
            {
                for (int i = 0; i < worst.Length; i++) row.Worst[i] = MathF.Min(row.Worst[i], worst[i]);
                rows[key] = (row.Count + 1, row.Under.Contains(under) ? row.Under : row.Under + "," + under, row.Worst);
            }
            else rows[key] = (1, under, worst);
        }
        Console.WriteLine($"  {mapId}: {sources} sounding things; those that lose half a decibel or more (worst of eight directions), "
                        + "low/middle/high dB at " + string.Join(", ", ranges.Select(r => $"{r:F0}")) + " m");
        foreach (var (sound, row) in rows)
            Console.WriteLine($"  {sound} x{row.Count} on {row.Under}: "
                            + string.Join("  ", Enumerable.Range(0, ranges.Length).Select(r =>
                                $"{row.Worst[r * 3],5:F1}/{row.Worst[r * 3 + 1],5:F1}/{row.Worst[r * 3 + 2],5:F1}")));
        return 0;
    }

    static float Db(float gain) => 20f * MathF.Log10(MathF.Max(1e-6f, gain));
    static float P10(double power) => (float)(10.0 * Math.Log10(power));

    /// <summary>The car's live voice three ways (no ground, the copy, the copy and what is taken) and each band's level
    /// over the dry voice; the two with ground written as WAVs.</summary>
    static void Render(string preset, float speed, GroundAnswer.Bounce bounce, float distance, string outDir)
    {
        float[] Voice(bool ground)
        {
            var v = MachineRegistry.VehicleFor(preset);
            var voice = new EngineVoiceState(v, Rate, 11) { TargetSpeed = speed, SplitVoices = true, CompensateLevel = true };
            voice.PlaceAtSpeed(speed);
            voice.Revive();
            var tap = new EngineTapState(voice);
            voice.SetListener(new Vector3(0f, 1.7f, -distance) - v.ExhaustSlot);
            voice.Ground.Set(ground ? bounce.DelaySeconds : 0f, ground ? bounce.LowGain : 0f, ground ? bounce.HighGain : 0f);
            int warm = Rate / Block, blocks = 4 * Rate / Block;
            var fb = new float[Block]; var rb = new float[Block];
            var x = new float[blocks * Block];
            for (int b = 0; b < warm + blocks; b++)
            {
                voice.Produce(); tap.Render(fb); voice.Consume(rb);
                if (b >= warm) Array.Copy(rb, 0, x, (b - warm) * Block, Block);
            }
            return x;
        }
        var dry = Voice(false);
        var before = Voice(true);
        var now = Banded(before, bounce);
        var d0 = Bands(dry); var d1 = Bands(before); var d2 = Bands(now);
        Console.WriteLine($"           {preset} at {speed * 3.6f:F0} km/h: before {P10(d1[0] / d0[0]),5:F1} {P10(d1[1] / d0[1]),5:F1} {P10(d1[2] / d0[2]),5:F1}   "
                        + $"now {P10(d2[0] / d0[0]),5:F1} {P10(d2[1] / d0[1]),5:F1} {P10(d2[2] / d0[2]),5:F1}");
        Directory.CreateDirectory(outDir);
        float peak = 1e-9f;
        foreach (float s in before) peak = MathF.Max(peak, MathF.Abs(s));
        float gain = 0.5f / peak;
        Write(Path.Combine(outDir, $"{preset}_{distance:F0}m_1_before.wav"), before, gain);
        Write(Path.Combine(outDir, $"{preset}_{distance:F0}m_2_now.wav"), now, gain);
    }

    /// <summary>The signal with each of the voice's bands scaled by what the ground takes (whole-signal FFT, so the
    /// bands meet as sharply as a band's edge allows).</summary>
    static float[] Banded(float[] x, GroundAnswer.Bounce b)
    {
        int n = 1; while (n < x.Length) n <<= 1;
        var a = new Complex[n];
        for (int i = 0; i < x.Length; i++) a[i] = x[i];
        Spectrum.Fft(a);
        for (int k = 0; k < n; k++)
        {
            double hz = Math.Min(k, n - k) * (double)Rate / n;
            float g = hz < AcousticBands.LowCrossoverHz ? b.ExcessLow : hz < AcousticBands.HighCrossoverHz ? b.ExcessMid : b.ExcessHigh;
            a[k] *= g;
        }
        for (int k = 0; k < n; k++) a[k] = Complex.Conjugate(a[k]);
        Spectrum.Fft(a);
        var y = new float[x.Length];
        for (int i = 0; i < y.Length; i++) y[i] = (float)(a[i].Real / n);
        return y;
    }

    /// <summary>The power in each of the voice's bands (AcousticBands' ranges).</summary>
    internal static double[] Bands(float[] x)
    {
        int n = 1; while (n < x.Length) n <<= 1;
        var a = new Complex[n];
        for (int i = 0; i < x.Length; i++) a[i] = x[i];
        Spectrum.Fft(a);
        double lo = AcousticBands.LowThirdsHz[0] / Math.Pow(2.0, 1.0 / 6.0), hi = AcousticBands.HighThirdsHz[^1] * Math.Pow(2.0, 1.0 / 6.0);
        var p = new double[3];
        for (int k = 1; k < n / 2; k++)
        {
            double hz = k * (double)Rate / n;
            if (hz < lo || hz > hi) continue;
            int band = hz < AcousticBands.LowCrossoverHz ? 0 : hz < AcousticBands.HighCrossoverHz ? 1 : 2;
            p[band] += a[k].Real * a[k].Real + a[k].Imaginary * a[k].Imaginary;
        }
        return p;
    }

    internal static void Write(string path, float[] x, float gain)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = x.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(bytes);
        foreach (float v in x) w.Write((short)Math.Clamp((int)(v * gain * 32767f), short.MinValue, short.MaxValue));
    }
}
