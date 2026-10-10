using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --fire spread: fire spreading through what can burn (FireSpread, docs/FIRE.md 12).
///   timeline [scene=trees|stumps_wind|stumps_calm|car_pile|rain] [until=s]   the spread alone, fast: who caught when, and why.
///   game out=DIR [scene=all|pit|trees|stumps|car_pile|rain]   through ClientAudioSystem as FireSpike's game set: the
///        spread stepped in real time, each burning part a fire emitter as the server makes it; long scenes are
///        heard in windows, the spread run ahead between them. Captured to DIR/capture.post.wav and DIR/segments.csv,
///        with DIR/timeline.txt.
/// </summary>
public static class FireSpreadSpike
{
    /// <summary>A scene: its things, its weather, where the listener stands and faces, what happens at the
    /// start, and the windows heard (start in the spread's seconds, length).</summary>
    private sealed record Scene(string Name, Func<FireSpread, List<int>> Build, FireWeather Weather, Vector3 Feet, Vector3 Facing,
                                Action<FireSpread, List<int>, double> Start, (double At, double Seconds)[] Windows, double Until);

    private static FireWeather Air(float wind, float rain = 0f) => new(WindWeather.Steady(wind, 270f, 0.2f), rain, 24f, 35f);

    private static Scene Get(string name) => name switch
    {
        // Eight conifers in a row west to east, their crowns a metre apart; 25 m of bare ground; three more.
        // A wind of 6 m/s from the west. Lightning comes down on the first at 5 s.
        "trees" => new Scene("trees", s =>
            {
                var ids = new List<int>();
                for (int i = 0; i < 8; i++) ids.Add(s.Add(new FuelObject($"tree {i + 1}", new Vector3(6f * i, 0f, 0f), 0f, FuelCatalog.Tree(5f, 2f, 12f, $"tree {i + 1}"))));
                for (int i = 0; i < 3; i++) ids.Add(s.Add(new FuelObject($"tree {i + 9} (across the gap)", new Vector3(72f + 6f * i, 0f, 0f), 0f, FuelCatalog.Tree(5f, 2f, 12f))));
                // One upwind of where the lightning strikes.
                ids.Add(s.Add(new FuelObject("tree 0 (upwind)", new Vector3(-6f, 0f, 0f), 0f, FuelCatalog.Tree(5f, 2f, 12f))));
                return ids;
            }, Air(6f), new Vector3(21f, 0f, -30f), new Vector3(21f, 0f, 0f),
            (s, ids, t) => s.Strike(new Vector3(0f, 0f, 0f), t, force: true),
            new[] { (0.0, 240.0) }, 1800),
        "stumps_wind" or "stumps_calm" => new Scene(name, s =>
            {
                var ids = new List<int>();
                for (int i = 0; i < 6; i++) ids.Add(s.Add(new FuelObject($"stump {i + 1}", new Vector3(1.0f * i, 0f, 0f), 0f, FuelCatalog.Stump(0.6f, 0.4f, $"stump {i + 1}"))));
                return ids;
            }, Air(name == "stumps_wind" ? 6f : 0.5f), new Vector3(2.5f, 0f, -4f), new Vector3(2.5f, 0f, 0f),
            (s, ids, t) => s.Light(ids[0], t),
            new[] { (60.0, 30.0), (600.0, 30.0), (1500.0, 30.0) }, 3600),
        // A car with its side 1.5 m from a pile of logs, a breeze of 3 m/s from the car to the pile.
        "car_pile" => new Scene("car_pile", s => new List<int>
            {
                s.Add(new FuelObject("the car", new Vector3(0f, 0f, 0f), 0f, FuelCatalog.Car(1.8f, 4.5f))),
                s.Add(new FuelObject("the pile of logs", new Vector3(0.9f + 1.5f + 0.5f, 0f, 0f), 0f, FuelCatalog.WoodPile(1f, 2f, 1f))),
            }, Air(3f), new Vector3(-4f, 0f, -9f), new Vector3(1.5f, 0f, 0f),
            (s, ids, t) => s.Light(ids[0], t),
            new[] { (600.0, 40.0), (1320.0, 60.0), (2100.0, 40.0) }, 3600),
        // A bonfire burning when the rain comes: light rain, then a downpour.
        "rain" => new Scene("rain", s => new List<int>
            {
                s.Add(new FuelObject("the campfire", new Vector3(0f, 0f, 0f), 0f, FuelCatalog.ForFire("campfire"))),
            }, Air(2f), new Vector3(0f, 0f, -3f), new Vector3(0f, 0f, 0f),
            (s, ids, t) => s.Light(ids[0], t - 1200),
            new[] { (0.0, 25.0), (60.0, 35.0), (130.0, 35.0), (200.0, 100.0) }, 400),
        _ => throw new ArgumentException("no scene " + name),
    };

    /// <summary>The weather at a moment of a scene: the rain scene's rain comes at 60 s (light, 2 mm/h), 120 s
    /// (heavy, 20 mm/h) and 190 s (a cloudburst, 60 mm/h).</summary>
    private static FireWeather WeatherAt(Scene sc, double t) => sc.Name != "rain" ? sc.Weather
        : sc.Weather with { RainMmPerHour = t < 60 ? 0f : t < 120 ? 2f : t < 190 ? 20f : 60f, HumidityPercent = t < 60 ? 50f : 95f };

    public static int Run(string[] args)
    {
        string scene = args.FirstOrDefault(a => a.StartsWith("scene=", StringComparison.Ordinal))?[6..] ?? "all";
        if (args.Contains("game")) return Game(args, scene);
        if (args.Contains("cost")) return Cost(args);
        foreach (var name in Names(scene)) Timeline(Get(name), args);
        return 0;
    }

    private static IEnumerable<string> Names(string scene) => scene == "all"
        ? new[] { "trees", "stumps_wind", "stumps_calm", "car_pile", "rain" }
        : scene == "stumps" ? new[] { "stumps_wind", "stumps_calm" } : new[] { scene };

    private static string Timeline(Scene sc, string[] args)
    {
        float until = Arg(args, "until=", (float)sc.Until);
        var s = new FireSpread(7);
        var ids = sc.Build(s);
        sc.Start(s, ids, 0);
        float trace = Arg(args, "trace=", 0f);
        for (double t = 1; t <= until; t += 1)
        {
            s.Step(t, 1f, WeatherAt(sc, t));
            if (trace > 0f && t % trace == 0)
                Console.WriteLine($"  {t,6:F0} s  " + string.Join("; ", ids.Select(i => s.Objects[i].Name + ": " +
                    string.Join(", ", s.State(i).Select(p => $"{p.Part} {(p.Burning ? $"{p.HeatKw:F0} kW" : $"{p.Readiness:P0}")}")))));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"== {sc.Name}: {until / 60:F0} minutes; {s.BrandsLaunched} brands sent up");
        foreach (var e in s.Events) sb.AppendLine("  " + FireSpread.Describe(e, 0, s.Objects));
        for (int i = 0; i < ids.Count; i++)
        {
            var parts = string.Join(", ", s.State(ids[i]).Select(p => $"{p.Part} {(p.Burning ? "burning" : "not burning")} M={p.Moisture:F2} heat {p.Readiness:P0} of what it needs"));
            sb.AppendLine($"  at the end, {s.Objects[ids[i]].Name}: {parts}");
        }
        Console.Write(sb.ToString());
        return sb.ToString();
    }

    /// <summary>A forest of 3,000 trees six metres apart, struck in the middle in a 6 m/s wind: what a
    /// step of the spread costs as it burns, and how many parts burn at once.</summary>
    private static int Cost(string[] args)
    {
        int nx = (int)Arg(args, "nx=", 60f), nz = (int)Arg(args, "nz=", 50f);
        var s = new FireSpread(3);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
                s.Add(new FuelObject($"tree {i},{j}", new Vector3(6f * i, 0f, 6f * j), 0f, FuelCatalog.Tree(5f, 2f, 12f)));
        s.Strike(new Vector3(6f * (nx / 4), 0f, 6f * (nz / 2)), 0, force: true);
        var w = Air(6f);
        var sw = new Stopwatch();
        double worst = 0, total = 0;
        int steps = (int)Arg(args, "sec=", 900f), most = 0;
        for (int t = 1; t <= steps; t++)
        {
            sw.Restart();
            s.Step(t, 1f, w);
            double ms = sw.Elapsed.TotalMilliseconds;
            total += ms;
            worst = Math.Max(worst, ms);
            int burning = s.Burning(t, 0f).Count(b => b.Running);
            most = Math.Max(most, burning);
            if (t % 60 == 0) Console.WriteLine($"  {t / 60,3} min: {burning} parts burning, {s.BrandsInAir} brands in the air, step {ms:F2} ms");
        }
        Console.WriteLine($"{nx * nz} trees: step mean {total / steps:F2} ms, worst {worst:F2} ms; at most {most} parts burning at once; {s.BrandsLaunched} brands");
        return 0;
    }

    // ── Through the game ─────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds, double CpuPercent, float MixerLoad, int Voices);

    private static int Game(string[] args, string which)
    {
        string outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "/tmp/openfps-fire-spread";
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        AcousticRegistry.Initialize();
        var provider = new OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider();
        var facade = new OpenFPS.Client.AudioEngine.Core.AudioEngineFacade(provider);
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
            EntityId = 1, Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(3000f, 1f, 3000f), IsSolid = true },
            Material = new MaterialComponent { Material = "Grass" },
        });
        world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.7f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });

        var segments = new List<Segment>();
        var timeline = new StringBuilder();
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
        int nextId = 100;
        // A fire emitter as the server makes it (FireSystem.Sync): its key, its shape's collider, its state.
        int Emit(string key, Vector3 at, float yaw, FireShape shape, bool running, float quench, int id = 0)
        {
            var spec = FireSpec.ByName(key);
            if (id == 0) id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = at, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f), Scale = Vector3.One },
                Collider = new ColliderComponent
                {
                    Shape = shape.Kind == FireShapeKind.Circle ? ColliderShape.Cylinder : ColliderShape.Box, IsSolid = false,
                    Size = new Vector3(shape.Width, MathF.Max(0.5f, spec.FlameHeightMetres), shape.Depth),
                },
                Material = new MaterialComponent { Material = "None" },
            };
            def.SoundEmitter = new SoundEmitterComponent
            {
                IsSynth = true, SoundId = key, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 3000f, MinDistance = 1f,
                SynthRunning = running, Quench = quench,
            };
            world.RegisterDefinition(def);
            return id;
        }
        void Remove(IEnumerable<int> ids)
        {
            var list = ids.ToArray();
            if (list.Length == 0) return;
            world.RemoveEntities(list);
            foreach (int id in list) audio.ForgetEntity(id);
        }

        try
        {
            Pump(2.0);
            Record("silence", 2.0);
            var names = which == "all" ? new[] { "pit", "trees", "stumps_wind", "stumps_calm", "car_pile", "rain" }
                      : which == "stumps" ? new[] { "stumps_wind", "stumps_calm" } : new[] { which };
            foreach (var name in names)
            {
                if (name == "pit") { Pits(); continue; }
                var sc = Get(name);
                WindField.Weather = sc.Weather.Wind;
                var s = new FireSpread(7);
                var ids = sc.Build(s);
                sc.Start(s, ids, 0);
                double simNow = 0;
                int seen = 0;
                timeline.AppendLine($"== {sc.Name}");
                var emitters = new Dictionary<(int, int), (int Id, bool Running, float Quench)>();
                double offset = 0;
                void Sync()
                {
                    for (; seen < s.Events.Count; seen++) timeline.AppendLine("  " + FireSpread.Describe(s.Events[seen], 0, s.Objects));
                    var live = new HashSet<(int, int)>();
                    foreach (var b in s.Burning(simNow))
                    {
                        var k = (b.Object, b.Part);
                        live.Add(k);
                        if (emitters.TryGetValue(k, out var e))
                        {
                            if (e.Running != b.Running || MathF.Abs(e.Quench - b.Quench) > 0.05f)
                            {
                                Emit(FireSpread.KeyOf(b, offset), b.Position, b.Yaw, b.Shape, b.Running, b.Quench, e.Id);
                                emitters[k] = (e.Id, b.Running, b.Quench);
                            }
                            continue;
                        }
                        int id = Emit(FireSpread.KeyOf(b, offset), b.Position, b.Yaw, b.Shape, b.Running, b.Quench);
                        emitters[k] = (id, b.Running, b.Quench);
                    }
                    var gone = emitters.Where(kv => !live.Contains(kv.Key)).ToList();
                    Remove(gone.Select(g => g.Value.Id));
                    foreach (var g in gone) emitters.Remove(g.Key);
                }
                foreach (var (at, seconds) in sc.Windows)
                {
                    // The spread run ahead to the window, the voices made afresh at its point of life.
                    while (simNow + 1 <= at) { simNow += 1; s.Step(simNow, 1f, WeatherAt(sc, simNow)); }
                    Remove(emitters.Values.Select(v => v.Id));
                    emitters.Clear();
                    offset = WindField.Now() - simNow;
                    Sync();
                    Stand(sc.Feet, sc.Facing);
                    Pump(3.0);
                    // In the window the spread runs in real time.
                    double t0 = clock.Elapsed.TotalSeconds, s0 = simNow;
                    perFrame = now =>
                    {
                        double target = s0 + (now - t0);
                        bool stepped = false;
                        while (simNow + 1 <= target) { simNow += 1; s.Step(simNow, 1f, WeatherAt(sc, simNow)); stepped = true; }
                        if (stepped) Sync();
                    };
                    Record($"{sc.Name} from {at / 60:F1} min", seconds);
                    perFrame = null;
                    timeline.AppendLine($"  [heard {at / 60:F1}-{(at + seconds) / 60:F1} min: {segments[^1].Start:F1}-{segments[^1].Start + seconds:F1} s in the capture]");
                }
                Remove(emitters.Values.Select(v => v.Id));
                Pump(2.0);
                // And the rest of the scene's timeline, unheard.
                while (simNow + 1 <= sc.Until) { simNow += 1; s.Step(simNow, 1f, WeatherAt(sc, simNow)); }
                for (; seen < s.Events.Count; seen++) timeline.AppendLine("  " + FireSpread.Describe(s.Events[seen], 0, s.Objects));
                timeline.AppendLine($"  ({s.BrandsLaunched} brands sent up over {sc.Until / 60:F0} min)");
            }
            Record("silence end", 1.0);
        }
        finally
        {
            facade.Dispose();
        }

        // The garden pit as approved (a 0.9 m square bed), a round one of the same width, and a round one
        // twice as wide: what the placed size does.
        void Pits()
        {
            WindField.Weather = WindWeather.Steady(3f, 270f, 0.25f);
            foreach (var (label, shape) in new (string, FireShape?)[] { ("pit square 0.9 m (approved)", null), ("pit round 0.9 m", FireShape.Circle(0.9f)), ("pit round 1.8 m", FireShape.Circle(1.8f)) })
            {
                var own = FireSpec.GardenFirePit;
                var sh = shape ?? own.Outline;
                string key = shape == null ? "fire:fire_pit" : FireSpec.KeyFor("fire_pit", null, shape);
                int id = Emit(key, new Vector3(0f, 0.6f, 0f), 0f, sh, true, 0f);
                Stand(new Vector3(0f, 0f, -2f), new Vector3(0f, 0.6f, 0f));
                Pump(4.0);
                Record(label + " 2m", 30.0);
                Remove(new[] { id });
                Pump(2.0);
            }
        }

        var sb = new StringBuilder("name,start,seconds,cpu_percent,mixer_load,hrtf_voices\n");
        foreach (var sg in segments)
            sb.Append(CultureInfo.InvariantCulture, $"{sg.Name},{sg.Start:F3},{sg.Seconds:F3},{sg.CpuPercent:F1},{sg.MixerLoad:F4},{sg.Voices}\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        File.WriteAllText(Path.Combine(outDir, "timeline.txt"), timeline.ToString());
        Console.Write(timeline.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav, segments.csv and timeline.txt ({segments.Count} segments)");
        return 0;
    }

    private static float Arg(string[] args, string prefix, float fallback)
        => float.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length),
                          NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}
