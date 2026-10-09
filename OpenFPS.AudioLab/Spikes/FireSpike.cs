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
/// --fire: fires from a campfire to a crown fire (FireSynth, docs/FIRE.md), rendered from their models.
///   levels [preset ...] [sec=30] [wind=] [age=] [seed=7] [heard=D] [parts=roar,crackle,fizz,steam,settle,torch,fall,glass,burst]
///        each preset developed (or age= s after lighting): level at a metre with every place summed (Leq,
///        LAeq, octaves, peak headroom), texture statistics against its recordings, 10 ms 4-16 kHz kurtosis,
///        and its cost. heard=D: a microphone D m off, each place by its distance, 1/r and ISO 9613-1 air.
///        SourceLevelDb and PeakHeadroomDb are read from the plain run.
///   render out=DIR [preset ...] [sec=30] [heard=D] [places=1]   mono float WAVs (-20 dBFS is 94 dB SPL);
///        places=1 also writes every place as its own channel.
///   game out=DIR [set=all|near|far|walk] [sec=30]   through ClientAudioSystem with the HRTF, ear model and
///        loudness law, each fire a map entity, the listener facing it; captured to DIR/capture.post.wav with
///        DIR/segments.csv. Flat grass, no walls, no wind at the ears.
///   hrtf [out=DIR]   FireHrtfProbe.
/// Fitting knobs: swing= alpha= flicker= beta= trees= crown= structure= vehicle= vfizz=.
/// </summary>
public static class FireSpike
{
    private const int Rate = 48000;
    private const float PascalsToFull = 0.1f;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.Contains("game")) return Game(args);
        if (args.Contains("hrtf")) return FireHrtfProbe.Run(args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "/tmp/openfps-fire-hrtf");
        float sec = Arg(args, "sec=", 30f);
        float wind = Arg(args, "wind=", float.NaN);
        float age = Arg(args, "age=", float.NaN);
        float heard = Arg(args, "heard=", 0f);
        int seed = (int)Arg(args, "seed=", 7f);
        // For fitting: the roar's swing and tail, and the crackle rates by fuel.
        FireSynth.PuffSwing = Arg(args, "swing=", FireSynth.PuffSwing);
        FireSynth.RoarTailExponent = Arg(args, "alpha=", FireSynth.RoarTailExponent);
        FireSynth.Flicker = Arg(args, "flicker=", FireSynth.Flicker);
        FireSynth.CrackleHeatExponent = Arg(args, "beta=", FireSynth.CrackleHeatExponent);
        FireSynth.TreeCrackle = Arg(args, "trees=", FireSynth.TreeCrackle);
        FireSynth.CrownCrackle = Arg(args, "crown=", FireSynth.CrownCrackle);
        FireSynth.StructureCrackle = Arg(args, "structure=", FireSynth.StructureCrackle);
        FireSynth.VehicleCrackle = Arg(args, "vehicle=", FireSynth.VehicleCrackle);
        FireSynth.VehicleFizz = Arg(args, "vfizz=", FireSynth.VehicleFizz);
        string[]? parts = args.FirstOrDefault(a => a.StartsWith("parts=", StringComparison.Ordinal))?[6..].Split(',');
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);
        var wanted = args.Where(a => !a.StartsWith("--") && !a.Contains('=') && a is not ("levels" or "render")).ToList();
        var presets = FireSpec.Presets.Keys.Where(k => wanted.Count == 0 || wanted.Any(w => k.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var key in presets)
        {
            var spec = FireSpec.ByName(key);
            var sw = Stopwatch.StartNew();
            var (pa, census) = Render(spec, sec, seed, wind, age, heard, parts);
            double cost = sw.Elapsed.TotalSeconds / (sec + Settle());
            var (nx, nz, d) = FireSynth.CellGrid(spec);
            Console.WriteLine();
            Console.WriteLine($"== fire:{key} ({spec.Name}): {spec.HeatReleaseKw / 1000f:F2} MW over {spec.AreaWidth:F1} x {spec.AreaDepth:F1} m; " +
                              $"{nx * nz} bodies {d:F1} m across; declared {spec.SourceLevelDb:F1} dB at 1 m; render costs {cost * 100:F1} % of a core" +
                              (heard > 0f ? $"; heard from {heard:F0} m" : ""));
            Console.WriteLine(census);
            NatureSpike.Report("fire:" + key, pa, Rate, calibrated: true);
            var oct = WavesSpike.OctavesRe1k(pa);
            Console.WriteLine("  octaves re 1 kHz, 63 Hz-16 kHz: " + string.Join(" ", oct.Select(v => $"{v:F1}")));
            var tex = TextureStatistics.Analyse(pa).Summary();
            string kind = FireReferences.KindFor(key);
            bool old = !FireReferences.Has(kind) && kind == "campfire";
            if (FireReferences.Has(kind) || old)
            {
                int inside = 0;
                var outside = new List<string>();
                foreach (var k in FireReferences.FittedKeys)
                {
                    var (lo, hi) = old ? TextureStatistics.Range("fire", k) : FireReferences.Range(kind, k);
                    if (tex[k] >= lo && tex[k] <= hi) inside++;
                    else outside.Add($"{k} {tex[k]:F3} [{lo:F3}, {hi:F3}]");
                }
                Console.WriteLine($"  texture against '{kind}': {inside}/{FireReferences.FittedKeys.Length} inside" + (outside.Count > 0 ? "; outside: " + string.Join("; ", outside) : ""));
            }
            else Console.WriteLine("  texture: " + string.Join(", ", TextureStatistics.Keys.Where(tex.ContainsKey).Select(k => $"{k} {tex[k]:F3}")));
            var (kurt, crest) = TextureStatistics.Waveform(pa);
            Console.WriteLine($"  4-16 kHz in 10 ms: kurtosis {kurt:F2}, crest median {crest:F1} dB" +
                              (FireReferences.WaveformKurtosis.TryGetValue(kind, out var kr) ? $"; recordings {kr.Min:F2}-{kr.Max:F2}" : ""));
            if (FireReferences.Octaves.TryGetValue(kind, out var or))
            {
                var miss = Enumerable.Range(0, oct.Length).Where(i => i != 4 && (oct[i] < or.Min[i] - 0.5 || oct[i] > or.Max[i] + 0.5))
                                     .Select(i => $"{(i < 4 ? 63 << i : 1000 << (i - 4))} Hz {oct[i]:F1} [{or.Min[i]:F0}, {or.Max[i]:F0}]").ToList();
                Console.WriteLine($"  octaves inside the recordings': {8 - miss.Count}/8" + (miss.Count > 0 ? "; outside: " + string.Join("; ", miss) : ""));
            }
            if (dir != null && args.Contains("places=1") && LastPlaces != null)
            {
                // Every place its own channel, pascals at a metre from it scaled as the mono file.
                int ch = LastPlaces.Length, len = LastPlaces[0].Length;
                var inter = new float[ch * len];
                for (int i = 0; i < len; i++) for (int c = 0; c < ch; c++) inter[i * ch + c] = LastPlaces[c][i] * PascalsToFull;
                string pp = Path.Combine(dir, "fire_" + key + "_places.wav");
                WavesSpike.WriteFloatWav(pp, inter, ch);
                Console.WriteLine($"  wrote {pp}");
            }
            if (dir != null)
            {
                string path = Path.Combine(dir, "fire_" + key + (heard > 0f ? $"_{heard:0}m" : "") + ".wav");
                WavesSpike.WriteFloatWav(path, pa.Select(p => p * PascalsToFull).ToArray(), 1);
                Console.WriteLine($"  wrote {path}");
            }
        }
        return 0;
    }

    private static float Settle() => 8f;

    /// <summary>The last render's places, each its own stream, for the lab to write out.</summary>
    public static float[][]? LastPlaces;

    /// <summary>A fire at a metre (every place summed), or heard from <paramref name="heard"/> m: pascals.</summary>
    public static (float[] Pa, string Census) Render(FireSpec spec, float sec, int seed, float wind, float age, float heard, string[]? parts = null)
    {
        var layout = FireSynth.Layout(spec);
        var s = new FireSynth(spec, Rate, seed, layout.Length) { Spread = 1f };
        if (parts != null)
        {
            float On(string name) => parts.Contains(name) ? 1f : 0f;
            s.RoarPart = On("roar"); s.CracklePart = On("crackle"); s.FizzPart = On("fizz"); s.SteamPart = On("steam"); s.SettlePart = On("settle");
            s.TorchPart = On("torch"); s.FallPart = On("fall"); s.GlassPart = On("glass"); s.BurstPart = On("burst");
        }
        // The glass renders off the audio threads; a short render waits for it.
        for (int i = 0; i < 400 && !s.GlassReady; i++) Thread.Sleep(50);
        int n = (int)(sec * Rate), lead = (int)(Settle() * Rate);
        var places = new float[layout.Length][];
        for (int p = 0; p < places.Length; p++) places[p] = new float[n];
        var out1 = new float[layout.Length];
        for (int i = 0; i < lead + n; i++)
        {
            if (i % 256 == 0)
            {
                double t = i / (double)Rate;
                if (float.IsNaN(wind)) s.ReadWind(0f, 0f, t); else s.Wind = wind;
                if (!float.IsNaN(age)) s.Age = age - Settle() + t;
                s.Control(256f / Rate);
            }
            s.NextPlaces(out1);
            if (i >= lead) for (int p = 0; p < out1.Length; p++) places[p][i - lead] = out1[p];
        }
        LastPlaces = places;
        var pa = new float[n];
        if (heard <= 0f)
        {
            foreach (var pl in places) for (int i = 0; i < n; i++) pa[i] += pl[i];
        }
        else
        {
            // A microphone at head height on the near side; each place by its own distance.
            var mic = new Vector3(0f, 1.6f, -heard);
            float h = Height(spec);
            for (int p = 0; p < places.Length; p++)
            {
                float r = Vector3.Distance(mic, new Vector3(layout[p].X, h, layout[p].Z));
                var carried = Air(places[p], r);
                for (int i = 0; i < n; i++) pa[i] += carried[i] / MathF.Max(1f, r);
            }
        }
        return (pa, s.Census());
    }

    /// <summary>Where a fire's sound is placed above the ground, m: its flames' middle, under its fuel's top.</summary>
    public static float Height(FireSpec spec) => MathF.Max(0.4f, MathF.Min(spec.FlameHeightMetres * 0.5f, MathF.Max(0.4f, spec.FuelHeightMetres)));

    /// <summary>ISO 9613-1 air over <paramref name="metres"/> (15 °C, 70 %), by FFT.</summary>
    private static float[] Air(float[] x, float metres)
    {
        int pad = Rate / 10, n = 1;
        while (n < x.Length + 2 * pad) n <<= 1;
        var buf = new Complex[n];
        for (int i = 0; i < x.Length; i++) buf[pad + i] = new Complex(x[i], 0);
        Spectrum.Fft(buf);
        for (int k = 0; k <= n / 2; k++)
        {
            float f = k * (float)Rate / n;
            double g = Math.Pow(10, -AudioPhysics.AirAttenuationDbPerMetre(f, 15f, 0.7f) * metres / 20);
            buf[k] *= g;
            if (k > 0 && k < n / 2) buf[n - k] *= g;
        }
        for (int k = 0; k < n; k++) buf[k] = Complex.Conjugate(buf[k]);
        Spectrum.Fft(buf);
        var y = new float[x.Length];
        for (int i = 0; i < y.Length; i++) y[i] = (float)(buf[pad + i].Real / n);
        return y;
    }

    // ── Through the game ─────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds, double CpuPercent, float MixerLoad, int Voices);

    /// <summary>How long into its life each fire is heard in the game renders, s: fully developed, at a
    /// moment its own events are going (a car's struts, a house's windows and ceilings).</summary>
    private static double GameAge(FireSpec spec) => spec.Fuel switch
    {
        FireFuel.Vehicle => 900,
        FireFuel.Structure => spec.GrowthSeconds + 120,
        _ => double.NaN,
    };

    private static int Game(string[] args)
    {
        string outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "/tmp/openfps-fire";
        string set = args.FirstOrDefault(a => a.StartsWith("set=", StringComparison.Ordinal))?[4..] ?? "all";
        double sec = Arg(args, "sec=", 30f);
        float windSpeed = Arg(args, "wind=", 3f);
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
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(3000f, 1f, 3000f), IsSolid = true },
            Material = new MaterialComponent { Material = "Grass" },
        });
        world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.7f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });
        WindField.Weather = WindWeather.Steady(windSpeed, 0f, 0.25f);

        var segments = new List<Segment>();
        int nextId = 100;
        double loadSum = 0; int loadCount = 0;
        Action<double>? perFrame = null;

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
        // A fire as the server would send it: its box the burning area, not solid, lit `age` seconds ago if
        // its life matters (FireSpec.KeyFor).
        int Add(string preset, Vector3 at, float yawDegrees = 0f)
        {
            var spec = FireSpec.ByName(preset);
            double age = GameAge(spec);
            string key = double.IsNaN(age) ? FireSpec.KeyFor(preset) : FireSpec.KeyFor(preset, WindField.Now() - age);
            int id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = at, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDegrees * MathF.PI / 180f), Scale = Vector3.One },
                Collider = new ColliderComponent
                {
                    Shape = ColliderShape.Box, IsSolid = false,
                    Size = new Vector3(spec.AreaWidth, MathF.Max(0.5f, spec.FlameHeightMetres), spec.AreaDepth),
                },
                Material = new MaterialComponent { Material = "None" },
            };
            def.SoundEmitter = new SoundEmitterComponent
            {
                IsSynth = true, SoundId = key, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 3000f, MinDistance = 1f,
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
        // One preset heard from each distance (m from its middle, on its near side), facing it.
        void Fire(string preset, params float[] distances) => FireTurned(preset, 0f, distances);
        // Turned by yaw: a car turned 90 degrees is seen side-on, its length across the way you face.
        void FireTurned(string preset, float yawDegrees, params float[] distances)
        {
            var spec = FireSpec.ByName(preset);
            var middle = new Vector3(0f, Height(spec), 0f);
            int id = Add(preset, middle, yawDegrees);
            Pump(4.0);
            foreach (float d in distances)
            {
                Stand(new Vector3(0f, 0f, -d), middle);
                Pump(3.0);
                Record($"{preset} {d:0.#}m", sec);
            }
            Remove(id);
        }

        try
        {
            Pump(2.0);
            Record("silence", 2.0);
            if (set is "scale")
            {
                // The house at 30 m with its places spread 1, 2 and 4 times as wide: how the ears follow the angle.
                foreach (float k in new[] { 1f, 2f, 4f })
                {
                    ExtendedSources.LayoutScale = k;
                    Fire("house_fire", 30f);
                    segments[^1] = segments[^1] with { Name = $"house_fire x{k:0} 30m" };
                }
                ExtendedSources.LayoutScale = 1f;
            }
            if (set is "check")
            {
                // Do the ears hear the geometry? The house at 30 m as it is, its places at its middle, and as one
                // voice; then standing among a crown fire's front.
                Fire("house_fire", 30f);
                ExtendedSources.LayoutScale = 0f;
                Fire("house_fire", 30f);
                segments[^1] = segments[^1] with { Name = "house_fire collapsed 30m" };
                ExtendedSources.LayoutScale = 1f;
                ExtendedSources.Enabled = false;
                Fire("house_fire", 30f);
                segments[^1] = segments[^1] with { Name = "house_fire one voice 30m" };
                ExtendedSources.Enabled = true;
                var crown = FireSpec.ByName("crown_fire");
                var cm = new Vector3(0f, Height(crown), 0f);
                int cid = Add("crown_fire", cm);
                Pump(4.0);
                Stand(new Vector3(21f, 0f, -30f), new Vector3(21f, 0f, 100f));
                Pump(3.0);
                Record("crown_fire among the front 30m", sec);
                Remove(cid);
            }
            if (set is "all" or "near")
            {
                Fire("campfire", 2f);
                Fire("fire_pit", 2f);
                Fire("bonfire", 5f);
                FireTurned("burning_car", 90f, 10f);
            }
            if (set is "all" or "far")
            {
                Fire("house_fire", 12f, 30f, 150f);
                Fire("burning_trees", 50f);
                Fire("crown_fire", 300f, 1000f);
            }
            if (set is "all" or "walk")
            {
                // Walking up to a burning wood from 150 m to 10 m from its middle (2.5 m from its edge).
                var spec = FireSpec.ByName("burning_trees");
                var middle = new Vector3(0f, Height(spec), 0f);
                int id = Add("burning_trees", middle);
                Stand(new Vector3(0f, 0f, -150f), middle);
                Pump(4.0);
                double t0 = clock.Elapsed.TotalSeconds;
                perFrame = t => Stand(new Vector3(0f, 0f, -150f + 1.4f * (float)(t - t0)), middle);
                Record("walk toward a burning wood", 140.0 / 1.4);
                perFrame = null;
                Remove(id);
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
}
