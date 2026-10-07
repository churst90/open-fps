using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --waves: lake shores, sea surf, shingle, a harbour wall, a river bank and a moored boat's hull
/// (ShoreSynth), rendered from their models and measured (docs/WAVES_AND_SHORES.md).
///
///   --waves levels [preset ...] [sec=30] [wind=5] [fetch=m] [heard=D] [parts=cloud,plume,crash,front,spray,foam,vent,stones,pocket,slap,hull,whitecaps]
///        each model's sea (Hs, Tp, Iribarren, breaker), its level at a metre (Leq, LAeq, octaves, the
///        headroom its peaks need), its texture statistics against the recordings of its kind, its 10 ms
///        4-16 kHz kurtosis and crest, and what it costs a core. SourceLevelDb and PeakHeadroomDb are
///        read from this.
///   --waves sea                                 every preset's sea at a range of winds
///   --waves render out=DIR [sec=30] [wind=]     one mono float WAV per preset at a metre, dry
///                                               (−20 dBFS is 94 dB SPL)
///   --waves game out=DIR [set=all|lake|sea|river|wall|hull|walk|start] [sec=30]
///        the game's own path: a ClientAudioSystem over the FMOD provider with the HRTF, the ear model
///        and the loudness law, each shore a map entity as the server would send it (its box the
///        stretch of edge and its fetch, its +Z to the water), the listener on foot on the land side;
///        captured from the master in float (DIR/capture.post.wav) with DIR/segments.csv. No map round
///        it: flat asphalt, no walls, echoes or reverb. The wind at the ears off.
/// </summary>
public static class WavesSpike
{
    private const int Rate = 48000;
    private const float PascalsToFull = 0.1f;

    /// <summary>The recordings each preset is measured against (TextureStatistics references).</summary>
    public static string ReferenceFor(string preset) => preset switch
    {
        "lake_sand" or "pond_bank" or "reed_shore" => "lake_sand",
        "lake_rock" => "lake_rock",
        "river_bank" => "river_bank",
        "sea_sand" => "surf_sand",
        "shingle" => "shingle",
        "harbour_wall" => "harbour_wall",
        _ => "hull",
    };

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.Contains("game")) return Game(args);
        if (args.Contains("sea")) return SeaTable();
        float sec = Arg(args, "sec=", 30f);
        float wind = Arg(args, "wind=", float.NaN);
        float fetch = Arg(args, "fetch=", float.NaN);
        // heard=D: as a recording hears a shore, five stretches in a row (100 m of a lake's edge) from D m
        // back from the middle, each place by its own distance; else one stretch at a metre, all summed.
        // TODO: Heard does not apply parts=; it renders every part.
        float heard = Arg(args, "heard=", 0f);
        string[]? parts = args.FirstOrDefault(a => a.StartsWith("parts=", StringComparison.Ordinal))?[6..].Split(',');
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);
        var wanted = args.Where(a => !a.StartsWith("--") && !a.Contains('=') && a is not ("levels" or "render")).ToList();
        var presets = ShoreSpec.Presets.Keys.Where(k => wanted.Count == 0 || wanted.Any(w => k.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var key in presets)
        {
            var spec = ShoreSpec.ByName(key);
            float u = float.IsNaN(wind) ? spec.ReferenceWind : wind;
            var geo = spec.DefaultGeometry with { FetchMetres = float.IsNaN(fetch) ? spec.FetchMetres : fetch };
            var sw = Stopwatch.StartNew();
            var (pa, census) = heard > 0f ? Heard(spec, geo, u, sec, 7, heard, parts) : Render(spec, geo, u, sec, 7, parts);
            double cost = sw.Elapsed.TotalSeconds / sec;
            var (hs, tp, _) = spec.WindSea(geo, u, 0f);
            Console.WriteLine();
            Console.WriteLine($"== shore:{key} ({spec.Name}): wind {u:F1} m/s onshore over {geo.FetchMetres:F0} m; declared {spec.SourceLevelDb:F1} dB at 1 m; render costs {cost * 100:F1} % of a core");
            if (hs > 0f)
            {
                float xi = WindWaves.Iribarren(spec.BeachSlope, hs, tp);
                Console.WriteLine($"  wind sea: Hs {hs * 100:F1} cm, Tp {tp:F2} s, L0 {WindWaves.DeepWavelength(tp):F2} m; Iribarren {xi:F2} ({(xi < 0.5f ? "spilling" : xi < 3.3f ? "plunging" : "surging")}); " +
                                  $"breaks at {WindWaves.BreakerHeight(hs, tp) * 100:F1} cm; run-up {WindWaves.RunUp(spec.BeachSlope, hs, tp) * 100:F1} cm; whitecaps {WindWaves.BreakingProbability(hs, tp, u):P0} of crests");
            }
            if (spec.SwellHeightMetres > 0f)
            {
                float xi = WindWaves.Iribarren(spec.BeachSlope, spec.SwellHeightMetres, spec.SwellPeriodSeconds);
                Console.WriteLine($"  swell: {spec.SwellHeightMetres:F2} m at {spec.SwellPeriodSeconds:F0} s; Iribarren {xi:F2}; breaks at {WindWaves.BreakerHeight(spec.SwellHeightMetres, spec.SwellPeriodSeconds):F2} m " +
                                  $"in {WindWaves.BreakerDepth(WindWaves.BreakerHeight(spec.SwellHeightMetres, spec.SwellPeriodSeconds)):F2} m of water; run-up {WindWaves.RunUp(spec.BeachSlope, spec.SwellHeightMetres, spec.SwellPeriodSeconds):F2} m");
            }
            if (spec.Hull is { } hull)
            {
                var modes = new List<string>();
                for (int m = 1; m <= 3; m++) for (int n = 1; n <= 3; n++) modes.Add($"({m},{n}) {hull.Plate.ModeHz(m, n):F0}->{hull.WetModeHz(m, n):F0}");
                Console.WriteLine($"  hull {hull.Name}: {hull.Plate.SurfaceDensity:F1} kg/m², coincidence {hull.Plate.CriticalHz:F0} Hz; modes dry->wet Hz " + string.Join(", ", modes));
            }
            Console.WriteLine(census);
            NatureSpike.Report("shore:" + key, pa, Rate, calibrated: true);
            Console.WriteLine("  octaves re 1 kHz, 63 Hz-16 kHz: " + string.Join(" ", OctavesRe1k(pa).Select(v => $"{v:F1}")));
            var tex = TextureStatistics.Analyse(pa).Summary();
            string refKey = ReferenceFor(key);
            if (ShoreReferences.Has(refKey))
            {
                int inside = 0;
                var outside = new List<string>();
                foreach (var k in ShoreReferences.FittedKeys)
                {
                    var (lo, hi) = ShoreReferences.Range(refKey, k);
                    if (tex[k] >= lo && tex[k] <= hi) inside++;
                    else outside.Add($"{k} {tex[k]:F3} [{lo:F3}, {hi:F3}]");
                }
                Console.WriteLine($"  texture against '{refKey}': {inside}/{ShoreReferences.FittedKeys.Length} inside" + (outside.Count > 0 ? "; outside: " + string.Join("; ", outside) : ""));
            }
            else Console.WriteLine("  texture: " + string.Join(", ", TextureStatistics.Keys.Where(tex.ContainsKey).Select(k => $"{k} {tex[k]:F3}")));
            var (kurt, crest) = TextureStatistics.Waveform(pa);
            Console.WriteLine($"  4-16 kHz in 10 ms: kurtosis {kurt:F2}, crest median {crest:F1} dB" +
                              (ShoreReferences.WaveformKurtosis.TryGetValue(refKey, out var kr) ? $"; recordings {kr.Min:F2}-{kr.Max:F2}" : ""));
            if (ShoreReferences.Octaves.TryGetValue(refKey, out var or))
            {
                var o = OctavesRe1k(pa);
                var miss = Enumerable.Range(0, o.Length).Where(i => i != 4 && (o[i] < or.Min[i] - 0.5 || o[i] > or.Max[i] + 0.5))
                                     .Select(i => $"{(i < 4 ? 63 << i : 1000 << (i - 4))} Hz {o[i]:F1} [{or.Min[i]:F0}, {or.Max[i]:F0}]").ToList();
                Console.WriteLine($"  octaves inside the recordings': {8 - miss.Count}/8" + (miss.Count > 0 ? "; outside: " + string.Join("; ", miss) : ""));
            }
            if (dir != null)
            {
                string path = Path.Combine(dir, "shore_" + key + ".wav");
                WriteFloatWav(path, pa.Select(p => p * PascalsToFull).ToArray(), 1);
                Console.WriteLine($"  wrote {path}");
            }
        }
        return 0;
    }

    /// <summary>A model at this wind (onshore, from the north, the water to the north), pascals at a
    /// metre, every place summed.</summary>
    public static (float[] Pa, string Census) Render(ShoreSpec spec, ShoreGeometry geo, float wind, float sec, int seed, string[]? parts = null)
    {
        var s = new ShoreSynth(spec, Rate, seed, geo) { WindSpeed = wind, WindFromDegrees = geo.WaterBearingDegrees };
        if (parts != null)
        {
            float On(string name) => parts.Contains(name) ? 1f : 0f;
            s.CloudPart = On("cloud"); s.PlumePart = On("plume"); s.CrashPart = On("crash"); s.FrontPart = On("front"); s.SprayPart = On("spray");
            s.FoamPart = On("foam"); s.VentPart = On("vent"); s.StonePart = On("stones"); s.PocketPart = On("pocket");
            s.SlapPart = On("slap"); s.HullPart = On("hull"); s.WhitecapPart = On("whitecaps");
        }
        // Let it settle: a surf bore takes a quarter of a minute to reach the edge.
        float settle = spec.BreakRowMetres > 0f ? 25f : 5f;
        for (int i = 0; i < Rate * settle; i++) { if (i % 256 == 0) s.Control(256f / Rate); s.Next(); }
        int n = (int)(sec * Rate);
        var pa = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (i % 256 == 0) s.Control(256f / Rate);
            pa[i] = s.Next();
        }
        return (pa, s.Census());
    }

    /// <summary>
    /// A shore as a microphone on it hears it: five stretches in a row, each its own synth (seeds apart),
    /// heard from <paramref name="back"/> m behind the middle of the middle one, every place by its own
    /// distance (pressure as 1 / r, no ears). The recordings are of long shores: a single stretch heard
    /// at a metre is a few columns of water where a recording hears dozens. Pascals at a metre's scale.
    /// </summary>
    public static (float[] Pa, string Census) Heard(ShoreSpec spec, ShoreGeometry geo, float wind, float sec, int seed, float back, string[]? parts = null)
    {
        const int stretches = 5;
        var synths = new ShoreSynth[stretches];
        var gains = new float[stretches][];
        var layout = ShoreSynth.Layout(spec, geo.LengthMetres);
        for (int j = 0; j < stretches; j++)
        {
            synths[j] = new ShoreSynth(spec, Rate, seed + 101 * j, geo) { WindSpeed = wind, WindFromDegrees = geo.WaterBearingDegrees, Spread = 1f };
            gains[j] = new float[layout.Length];
            float x0 = (j - (stretches - 1) / 2f) * geo.LengthMetres;
            for (int k = 0; k < layout.Length; k++)
                gains[j][k] = 1f / MathF.Max(1f, MathF.Sqrt((x0 + layout[k].X) * (x0 + layout[k].X) + (back + layout[k].Z) * (back + layout[k].Z)));
        }
        var places = new float[layout.Length];
        float settle = spec.BreakRowMetres > 0f ? 25f : 5f;
        int n = (int)(sec * Rate), lead = (int)(settle * Rate);
        var pa = new float[n];
        for (int i = 0; i < lead + n; i++)
        {
            float y = 0f;
            for (int j = 0; j < stretches; j++)
            {
                if (i % 256 == 0) synths[j].Control(256f / Rate);
                synths[j].NextPlaces(places);
                for (int k = 0; k < places.Length; k++) y += gains[j][k] * places[k];
            }
            if (i >= lead) pa[i - lead] = y;
        }
        return (pa, synths[2].Census());
    }

    /// <summary>Octave levels 63 Hz-16 kHz re the 1 kHz octave, as the recordings were measured.</summary>
    public static double[] OctavesRe1k(float[] x)
    {
        float[] centres = { 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        var e = new double[centres.Length];
        for (int b = 0; b < centres.Length; b++)
        {
            var band = NatureSpike.BandPass(x, Rate, centres[b] / MathF.Sqrt(2f), MathF.Min(centres[b] * MathF.Sqrt(2f), 0.45f * Rate));
            foreach (float v in band) e[b] += (double)v * v;
        }
        return e.Select(v => 10 * Math.Log10(Math.Max(1e-30, v) / Math.Max(1e-30, e[4]))).ToArray();
    }

    private static int SeaTable()
    {
        foreach (var (key, make) in ShoreSpec.Presets)
        {
            var spec = make();
            Console.WriteLine($"shore:{key}: {spec.Body}, fetch {spec.FetchMetres:F0} m, depth {spec.DepthMetres:F1} m, slope {spec.BeachSlope:F3}" +
                              (spec.SwellHeightMetres > 0f ? $", swell {spec.SwellHeightMetres:F2} m at {spec.SwellPeriodSeconds:F0} s" : "") +
                              (spec.CurrentMetresPerSecond > 0f ? $", current {spec.CurrentMetresPerSecond:F1} m/s" : ""));
            foreach (float u in new[] { 2f, 3f, 5f, 8f, 12f })
            {
                var (hs, tp, _) = spec.WindSea(spec.DefaultGeometry, u, 0f);
                float xi = hs > 0f ? WindWaves.Iribarren(spec.BeachSlope, hs, tp) : 0f;
                Console.WriteLine($"  {u,4:F0} m/s: Hs {hs * 100,6:F1} cm, Tp {tp,5:F2} s, Iribarren {xi,5:F2}, whitecaps {WindWaves.BreakingProbability(hs, tp, u),4:P0}, " +
                                  $"grows over {WindWaves.GrowthSeconds(u, spec.FetchMetres) / 60:F1} min");
            }
        }
        return 0;
    }

    // ── Through the game ─────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds, double CpuPercent, float MixerLoad, int Voices);

    private static int Game(string[] args)
    {
        string outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "/tmp/openfps-waves";
        string set = args.FirstOrDefault(a => a.StartsWith("set=", StringComparison.Ordinal))?[4..] ?? "all";
        double sec = Arg(args, "sec=", 30f);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        Console.WriteLine($"Extended sources {(ExtendedSources.Enabled ? "spread" : "one point")}; ear model {(OpenFPS.Common.Hearing.EarModel.Enabled ? "on" : "off")}");
        var provider = new OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
        var clock = Stopwatch.StartNew();
        var proc = Process.GetCurrentProcess();

        var world = new ClientWorldState();
        world.Clear(new Vector3(4000, 400, 4000));
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(900f, 1f, 900f), IsSolid = true },
            Material = new MaterialComponent { Material = "Asphalt" },
        });
        world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.7f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });

        var segments = new List<Segment>();
        int nextId = 100;
        double loadSum = 0; int loadCount = 0;
        Action<double>? perFrame = null;

        void Wind(float speed) => WindField.Weather = WindWeather.Steady(speed, 0f, 0.2f);
        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                perFrame?.Invoke(clock.Elapsed.TotalSeconds);
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                loadSum += facade.MixerLoad; loadCount++;
                Thread.Sleep(4);
            }
        }
        void Record(string name, double seconds)
        {
            double start = clock.Elapsed.TotalSeconds;
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            loadSum = 0; loadCount = 0;
            Pump(seconds);
            proc.Refresh();
            double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / seconds * 100.0;
            int used = 160 - provider.SpatialVoicesFree;
            segments.Add(new Segment(name, start, seconds, cpu, loadCount > 0 ? (float)(loadSum / loadCount) : 0f, used));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)  cpu {cpu:F0} %  mixer {(loadCount > 0 ? loadSum / loadCount : 0):P1}  HRTF voices in use {used}");
        }
        void Stand(Vector3 feet, Vector3 facing)
        {
            float yaw = MathF.Atan2(facing.X - feet.X, facing.Z - feet.Z);
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
        // A stretch of edge as the map would have it: its box the stretch (x) and the fetch (z), the water
        // to its +Z (north here), not solid.
        int Add(string preset, Vector3 at, float length = float.NaN, float fetch = float.NaN)
        {
            var spec = ShoreSpec.ByName(preset);
            int id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
                Collider = new ColliderComponent
                {
                    Shape = ColliderShape.Box, IsSolid = false,
                    Size = new Vector3(float.IsNaN(length) ? spec.LengthMetres : length, 0.2f, float.IsNaN(fetch) ? spec.FetchMetres : fetch),
                },
                Material = new MaterialComponent { Material = "Water" },
            };
            def.SoundEmitter = new SoundEmitterComponent
            {
                IsSynth = true, SoundId = "shore:" + preset, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 200f, MinDistance = 1f,
            };
            world.RegisterDefinition(def);
            return id;
        }
        void Remove(params int[] ids)
        {
            world.RemoveEntities(ids);
            foreach (int id in ids) audio.ForgetEntity(id);
            Pump(2.0);
        }
        // One preset heard from the land side at each distance (m from the edge), facing the water.
        void Shore(string preset, string label, float wind, params float[] distances)
        {
            Wind(wind);
            var spec = ShoreSpec.ByName(preset);
            Vector3 edge = new(0f, 0.05f, 0f);
            int id = Add(preset, edge);
            // A surf bore takes a while to come in; a lake's sea is there at once.
            Pump(spec.BreakRowMetres > 0f ? 20.0 : 4.0);
            foreach (float d in distances)
            {
                Stand(new Vector3(0f, 0f, -d), edge);
                Pump(3.0);
                Record($"{label} {d:0.#}m wind {wind:0}", sec);
            }
            Remove(id);
        }

        try
        {
            Wind(0f);
            Pump(2.0);
            Record("silence", 2.0);

            if (set is "all" or "lake")
            {
                Shore("lake_sand", "lake sand", 3f, 2f);
                Shore("lake_sand", "lake sand", 8f, 2f, 8f);
                Shore("lake_rock", "lake rock", 5f, 2f);
                Shore("lake_rock", "lake rock", 9f, 2f);
                Shore("pond_bank", "pond bank", 5f, 2f);
            }
            if (set is "all" or "river")
            {
                Shore("river_bank", "river bank", 0f, 2f);
                Shore("river_bank", "river bank", 6f, 2f);
            }
            if (set is "all" or "sea")
            {
                Shore("sea_sand", "sea sand", 5f, 5f, 40f);
                Shore("shingle", "shingle", 5f, 3f, 15f);
            }
            if (set is "start")
            {
                // A shore voice from the moment it starts: the sandy surf beach placed while you stand 10 m
                // back from its edge in a 4.5 m/s onshore wind, recorded from the update it is placed in.
                Wind(4.5f);
                Vector3 edge = new(0f, 0.05f, 0f);
                Stand(new Vector3(0f, 0f, -10f), edge);
                Pump(1.0);
                int id = Add("sea_sand", edge);
                Record("sea sand from its start 10m wind 4.5", sec);
                Remove(id);
            }
            if (set is "all" or "wall")
                Shore("harbour_wall", "harbour wall", 6f, 2f);
            if (set is "all" or "hull")
            {
                // A moored boat: sitting in it, half a metre from its side; standing on the dock 3 m off.
                Shore("hull_wood", "hull wood", 4f, 0.6f, 3f);
                Shore("hull_aluminium", "hull aluminium", 4f, 0.6f, 3f);
            }
            if (set is "all" or "walk")
            {
                // Eighty metres of a lake's sandy shore as four stretches, walked 3 m back from the edge at a
                // slow walk with a 6 m/s breeze onshore.
                Wind(6f);
                var ids = Enumerable.Range(0, 4).Select(i => Add("lake_sand", new Vector3(-30f + 20f * i, 0.05f, 0f))).ToArray();
                Stand(new Vector3(-50f, 0f, -3f), new Vector3(100f, 0f, -3f));
                Pump(5.0);
                double t0 = clock.Elapsed.TotalSeconds;
                perFrame = t => Stand(new Vector3(-50f + 1.2f * (float)(t - t0), 0f, -3f), new Vector3(200f, 0f, -3f));
                Record("lake shore walk along", 80.0 / 1.2);
                perFrame = null;
                Remove(ids);
            }
            Record("silence end", 1.0);
        }
        finally
        {
            facade.Dispose();
        }
        var sb = new StringBuilder("name,start,seconds,cpu_percent,mixer_load,hrtf_voices\n");
        foreach (var s in segments)
            sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3},{s.CpuPercent:F1},{s.MixerLoad:F4},{s.Voices}\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav and segments.csv ({segments.Count} segments)");
        return 0;
    }

    private static float Arg(string[] args, string prefix, float fallback)
        => float.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length),
                          NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    public static void WriteFloatWav(string path, float[] interleaved, int channels)
        => RunningWaterSpike.WriteFloatWav(path, interleaved, channels);
}
