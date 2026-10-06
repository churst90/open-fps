using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --distant-woods [set=sum|walk|all] [out=DIR] [trees=40] [sec=30]: a wood heard from 300 to 800 m.
///
/// set=sum (offline, no mixer): every tree of the wood its own FoliageSynth reading the wind at its own
/// crown, each heard as 1/d from where it stands with its own delay, summed at a listener; against the wood
/// heard as one (WoodChorus: one synth standing for its trees, reading the wind across the wood, at the gain
/// WoodChorus.Weigh gives). Broadband level, octave bands 125 Hz to 8 kHz, and how much the level swings
/// (the standard deviation of its 400 ms level, which the gusts set), at 300, 500 and 800 m; and what each
/// costs to render. No air absorption in either (it is the same path for both), no ground.
///
/// set=walk (through the game's client path and mixer, captured from the master): walking up to the wood
/// from 800 m to 100 m from its middle along the ground at 10 m/s, before (each tree its own voice, as in
/// the game until now: past 90 m every tree is too quiet to have one) and after (the woods, WoodChorus).
/// Writes DIR/before/capture.post.wav and DIR/after/capture.post.wav, and the level every 50 m to
/// DIR/levels.csv.
/// </summary>
public static class DistantWoodsSpike
{
    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];

    /// <summary>The wood: a stand of park trees over 160 by 120 m, crowns at their height, as gen_osm
    /// lays them (a crown every 20-30 m, jittered), its middle at the origin.</summary>
    internal static List<Vector3> Wood(int trees, int seed = 5)
    {
        var rng = new Random(seed);
        var spec = FoliageSpec.ParkTree;
        var at = new List<Vector3>();
        for (int i = 0; i < trees; i++)
            at.Add(new Vector3((float)(rng.NextDouble() - 0.5) * 160f, spec.CrownHeightMetres, (float)(rng.NextDouble() - 0.5) * 120f));
        return at;
    }

    public static int Run(string[] args)
    {
        string set = Arg(args, "set=") ?? "all";
        int trees = int.Parse(Arg(args, "trees=") ?? "40", CultureInfo.InvariantCulture);
        float seconds = float.Parse(Arg(args, "sec=") ?? "30", CultureInfo.InvariantCulture);
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-distant-woods";
        AcousticRegistry.Initialize();
        if (set is "sum" or "all") Sum(trees, seconds);
        if (set is "walk" or "all") return Walk(trees, outDir);
        return 0;
    }

    // ── The sum against the wood heard as one ────────────────────────────────────────────────────

    private static void Sum(int trees, float seconds)
    {
        const int Rate = 48000;
        var spec = FoliageSpec.ParkTree;
        var crowns = Wood(trees);
        int nextId = WoodChorus.FirstId;
        var chorus = WoodChorus.Build(crowns.Select((c, i) => new WoodChorus.Crown(i + 1, c, "park_tree")).ToList(), (_, _) => nextId--);
        float[] distances = { 300f, 500f, 800f };
        var ears = distances.Select(d => new Vector3(0f, 1.7f, -d)).ToArray();
        WindField.Weather = WindWeather.Steady(4.5f, 250f, 0.25f);
        double t0 = 1000.0;
        Console.WriteLine($"  {trees} park trees over 160 x 120 m, {chorus.Woods.Count} wood(s) by 200 m cell ({string.Join(", ", chorus.Woods.Select(w => $"{w.Crowns.Length} trees {w.Key}"))}); " +
                          $"wind 4.5 m/s from 250°, turbulence 0.25; {seconds:F0} s");

        // The trees, each its own synth, and the delay lines to each ear.
        var synths = Enumerable.Range(0, trees).Select(i => new FoliageSynth(spec, Rate, 1000 + i * 31)).ToArray();
        int n = (int)(seconds * Rate);
        var sum = ears.Select(_ => new float[n]).ToArray();
        var wood1 = ears.Select(_ => new float[n]).ToArray();
        int[,] delay = new int[ears.Length, trees];
        float[,] gainT = new float[ears.Length, trees];
        float refT = Loudness.Place(spec.SourceLevelDb, spec.ExtentMetres).ReferenceDistance;
        for (int e = 0; e < ears.Length; e++)
            for (int i = 0; i < trees; i++)
            {
                float d = Vector3.Distance(ears[e], crowns[i]);
                delay[e, i] = (int)(d / 343f * Rate);
                gainT[e, i] = refT / MathF.Max(d, refT);
            }
        int minDelay = delay.Cast<int>().Min();
        // Each wood as one: its synth, its wind places, and per ear its gain (WoodChorus.Weigh, on the
        // wood's own law) and delay.
        var weights = new WoodChorus.Weights();
        var woods = chorus.Woods.Select((w, j) => new
        {
            Wood = w,
            Synth = new FoliageSynth(spec, Rate, 7 + j),
            WindAt = ExtendedSources.Layout(w.Key)![1..],
            Gain = new float[ears.Length],
            Delay = new int[ears.Length],
        }).ToArray();
        for (int e = 0; e < ears.Length; e++)
        {
            chorus.Weigh(ears[e], weights);
            foreach (var w in woods)
            {
                var (tr, g) = weights.Woods[w.Wood.Id];
                w.Synth.Trees = tr;   // the same at every one of these distances: all past the hand-over
                float dw = MathF.Max(Vector3.Distance(ears[e], w.Wood.Centre), MathF.Max(refT, w.Wood.Extent));
                w.Gain[e] = g * refT / dw;
                w.Delay[e] = (int)(Vector3.Distance(ears[e], w.Wood.Centre) / 343f * Rate);
            }
        }

        // Rendered tree by tree, each sample written to every ear at its delay.
        var clock = Stopwatch.StartNew();
        int block = 256;
        for (int i = 0; i < trees; i++)
        {
            var s = synths[i];
            for (int k = 0; k < n; k++)
            {
                if (k % block == 0)
                {
                    s.ReadWind(crowns[i].X, crowns[i].Z, t0 + k / (double)Rate);
                    s.Control(block / (float)Rate);
                }
                float y = s.Next();
                for (int e = 0; e < ears.Length; e++)
                {
                    int at = k + delay[e, i] - minDelay;
                    if (at < n) sum[e][at] += y * gainT[e, i];
                }
            }
        }
        double treesMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        foreach (var w in woods)
            for (int k = 0; k < n; k++)
            {
                if (k % block == 0)
                {
                    w.Synth.ReadWindAt(w.Wood.Centre.X, w.Wood.Centre.Z, t0 + k / (double)Rate, w.WindAt);
                    w.Synth.Control(block / (float)Rate);
                }
                float y = w.Synth.Next();
                for (int e = 0; e < ears.Length; e++)
                {
                    int at = k + w.Delay[e] - minDelay;
                    if (at < n) wood1[e][at] += y * w.Gain[e];
                }
            }
        double oneMs = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  render cost for {seconds:F0} s: {trees} trees {treesMs:F0} ms ({treesMs / seconds / 10:F1} % of a core), the woods as one each {oneMs:F0} ms ({oneMs / seconds / 10:F2} %)");

        int skip = 3 * Rate;   // the boughs settling, and the delay lines filling
        float[] bands = { 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f };
        for (int e = 0; e < ears.Length; e++)
        {
            var a = sum[e].AsSpan(skip).ToArray();
            var b = wood1[e].AsSpan(skip).ToArray();
            double la = Db(Rms(a)), lb = Db(Rms(b));
            var sb = new StringBuilder();
            foreach (float f in bands)
                sb.Append(CultureInfo.InvariantCulture, $" {f / 1000f:0.###}k {Db(Rms(Band(a, f, Rate))) - la:+0.0;-0.0}/{Db(Rms(Band(b, f, Rate))) - lb:+0.0;-0.0}");
            Console.WriteLine($"  {distances[e]:F0} m: sum of trees {la + 94 - 20 * Math.Log10(refT):F1} dB SPL, the woods as one {lb + 94 - 20 * Math.Log10(refT):F1} dB ({lb - la:+0.0;-0.0} dB); " +
                              $"swing (sd of 400 ms level) {Swing(a, Rate):F1} / {Swing(b, Rate):F1} dB; bands re broadband, sum/wood:{sb}");
        }
    }

    private static double Rms(float[] x) { double s = 0; foreach (var v in x) s += v * (double)v; return Math.Sqrt(s / Math.Max(1, x.Length)); }
    private static double Db(double rmsPa) => 20 * Math.Log10(Math.Max(1e-12, rmsPa / 1.0)) ;

    /// <summary>The standard deviation of the 400 ms level, dB.</summary>
    private static double Swing(float[] x, int rate)
    {
        int w = rate * 4 / 10;
        var levels = new List<double>();
        for (int i = 0; i + w <= x.Length; i += w)
        {
            double s = 0;
            for (int k = i; k < i + w; k++) s += x[k] * (double)x[k];
            levels.Add(10 * Math.Log10(Math.Max(1e-24, s / w)));
        }
        double mean = levels.Average();
        return Math.Sqrt(levels.Sum(l => (l - mean) * (l - mean)) / levels.Count);
    }

    /// <summary>An octave band, by a fourth-order band-pass (two RBJ band-pass biquads at Q √2).</summary>
    private static float[] Band(float[] x, float centre, int rate)
    {
        var y = (float[])x.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            double w0 = 2 * Math.PI * centre / rate, alpha = Math.Sin(w0) / (2 * Math.Sqrt(2));
            double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w0), a2 = 1 - alpha;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double v = (b0 * y[i] + b2 * x2 - a1 * y1 - a2 * y2) / a0;
                x2 = x1; x1 = y[i]; y2 = y1; y1 = v;
                y[i] = (float)v;
            }
        }
        return y;
    }

    // ── Walking up to it through the game ────────────────────────────────────────────────────────

    private static int Walk(int trees, string outDir)
    {
        var results = new List<string> { "mode,distance_m,level_db,hrtf_voices" };
        foreach (bool after in new[] { false, true })
        {
            string dir = Path.Combine(outDir, after ? "after" : "before");
            Directory.CreateDirectory(dir);
            if (WalkOnce(trees, dir, after, results) != 0) return 1;
        }
        File.WriteAllLines(Path.Combine(outDir, "levels.csv"), results);
        Console.WriteLine($"Wrote {outDir}/before/capture.post.wav, {outDir}/after/capture.post.wav and levels.csv");
        return 0;
    }

    private static int WalkOnce(int trees, string dir, bool after, List<string> results)
    {
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(dir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(dir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
        var clock = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();
        try
        {
            var world = new ClientWorldState();
            world.Clear(new Vector3(4000, 400, 4000));
            var player = new LocalPlayerState();
            var mapping = new SoundMappingService(player);
            mapping.Initialize(sounds);
            var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);
            world.RegisterDefinition(new EntityDefinition
            {
                EntityId = 1, Type = EntityType.StaticObject,
                Transform = new Transform { Position = new Vector3(0f, -0.5f, -400f), Rotation = Quaternion.Identity, Scale = Vector3.One },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1200f, 1f, 1800f), IsSolid = true },
                Material = new MaterialComponent { Material = "Grass" },
            });
            int id = 100;
            var crowns = Wood(trees);
            float refT = Loudness.Place(FoliageSpec.ParkTree.SourceLevelDb, FoliageSpec.ParkTree.ExtentMetres).ReferenceDistance;
            foreach (var at in crowns)
            {
                var def = new EntityDefinition
                {
                    EntityId = id++, Type = EntityType.StaticObject,
                    Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
                };
                def.Identity.Name = "Trees";
                def.SoundEmitter = new SoundEmitterComponent();
                def.SoundEmitter.IsSynth = true;
                def.SoundEmitter.SoundId = "foliage:park_tree";
                def.SoundEmitter.Mode = PlaybackMode.LoopOne;
                def.SoundEmitter.Volume = 1f;
                def.SoundEmitter.Range = 90f;
                def.SoundEmitter.MinDistance = 4f;
                world.RegisterDefinition(def);
            }
            if (after) world.RefreshWoods();
            WindField.Weather = WindWeather.Steady(4.5f, 250f, 0.25f);

            void Stand(float d)
            {
                player.Position = new Vector3(0f, 0f, -d);
                player.Yaw = 0f;
                player.Rotation = Quaternion.Identity;
            }
            void Pump(double seconds, Action<double>? each = null)
            {
                var until = clock.Elapsed.TotalSeconds + seconds;
                while (clock.Elapsed.TotalSeconds < until)
                {
                    each?.Invoke(clock.Elapsed.TotalSeconds);
                    audio.Update(world.GetSnapshot());
                    facade.PumpForTest();
                    Thread.Sleep(4);
                }
            }
            const float From = 800f, To = 100f, Speed = 10f;
            Stand(From);
            Pump(3.0);
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            double start = clock.Elapsed.TotalSeconds;
            double walk = (From - To) / Speed;
            int maxVoices = 0;
            var marks = new StringBuilder("seconds,distance_m\n");
            float lastMark = float.MaxValue;
            // What the mixer places the trees at (every tree and wood voice's power, from its gains:
            // LoudestVoices), against what all the trees would be at their own distances (as 1/d): the
            // difference stays put if nothing is lost or doubled on the way in.
            var placed = new List<(float D, double Db)>();
            int frame = 0;
            Pump(walk, t =>
            {
                float d = From - Speed * (float)(t - start);
                Stand(MathF.Max(To, d));
                maxVoices = Math.Max(maxVoices, 160 - provider.SpatialVoicesFree);
                if (lastMark - d >= 50f || lastMark == float.MaxValue) { marks.Append(CultureInfo.InvariantCulture, $"{t:F3},{d:F0}\n"); lastMark = d; }
                if (++frame % 25 != 0) return;
                double p = 0;
                foreach (var v in facade.LoudestVoices(160))
                    if (!v.Reflection && (v.SoundId.StartsWith("foliage:") || v.SoundId.StartsWith("wood:"))) p += Math.Pow(10, v.Mid / 10.0);
                var ear = new Vector3(0f, 1.7f, -MathF.Max(To, d));
                double phys = 0;
                foreach (var c in crowns) { float dd = MathF.Max(Vector3.Distance(ear, c), refT); phys += 1.0 / (dd * dd); }
                if (p > 0) placed.Add((d, 10 * Math.Log10(p) - 10 * Math.Log10(phys)));
            });
            Pump(4.0);
            proc.Refresh();
            double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / (clock.Elapsed.TotalSeconds - start) * 100.0;
            File.WriteAllText(Path.Combine(dir, "marks.csv"), marks.ToString());
            File.WriteAllText(Path.Combine(dir, "start.txt"), start.ToString("F3", CultureInfo.InvariantCulture));
            Console.WriteLine($"  {(after ? "after" : "before")}: walked {From:F0} m to {To:F0} m at {Speed} m/s; CPU {cpu:F0} % of a core; up to {maxVoices} HRTF voices");
            results.Add($"{(after ? "after" : "before")},cpu,{cpu:F0},{maxVoices}");
            var line = new StringBuilder($"  {(after ? "after" : "before")}: placed against all trees as 1/d, dB:");
            for (float m = From; m >= To - 1f; m -= 50f)
            {
                var near = placed.Where(x => MathF.Abs(x.D - m) <= 12.5f).Select(x => x.Db).ToList();
                if (near.Count > 0) line.Append(CultureInfo.InvariantCulture, $" {m:F0} m {near.Average():+0.0;-0.0}");
                results.Add(FormattableString.Invariant($"{(after ? "after" : "before")},{m:F0},{(near.Count > 0 ? near.Average() : double.NaN):F2},"));
            }
            Console.WriteLine(line);
        }
        finally
        {
            facade.Dispose();
        }
        return 0;
    }
}
