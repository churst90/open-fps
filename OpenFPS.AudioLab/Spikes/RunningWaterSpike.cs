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
/// --running-water: creeks, gutters, drains, downpipes and overflows (RunningWaterSynth), rendered from
/// their models and measured (docs/RUNNING_WATER.md).
///
///   --running-water levels [preset ...] [sec=30] [rain=5] [flow=L/s] [dry=1] [parts=sites,spray,falls,drips,rain]
///        each model's hydraulics (depth, speed, Froude), its level at a metre (Leq, LAeq, octaves,
///        the headroom its peaks need), its texture statistics, its 10 ms 4-16 kHz kurtosis and crest,
///        and what it costs a core. SourceLevelDb and PeakHeadroomDb are read from this.
///   --running-water runoff                    each rain-fed model's flow at every rain class, and how
///                                             it runs on after the rain stops
///   --running-water render out=DIR [sec=30] [rain=] [flow=]
///        one mono float WAV per preset at a metre, dry (−20 dBFS is 94 dB SPL)
///   --running-water cycle PRESET [on=10] [off=20] [flow=] [out=DIR] [parts=sites,falls,drips,gurgle,plate]
///        a tap turned on and shut (see TapCycle)
///   --running-water game out=DIR [set=all|creek|rain|after|downpipes|fountain|taps|roof|round2|overflow]
///        [sec=30] [noise=1]
///        the game's own path: a ClientAudioSystem over the FMOD provider with the HRTF, the ear model
///        and the loudness law, each source a map entity as the server would send it, the listener on
///        foot; captured from the master in float (DIR/capture.post.wav) with DIR/segments.csv. No map:
///        no walls, echoes or reverb, flat asphalt. The wind at the ears off.
/// </summary>
public static class RunningWaterSpike
{
    private const int Rate = 48000;
    private const float PascalsToFull = 0.1f;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.Contains("game")) return Game(args);
        if (args.Contains("runoff")) return RunoffTable();
        if (args.Contains("cycle")) return TapCycle(args);
        float sec = Arg(args, "sec=", 30f);
        float rain = Arg(args, "rain=", float.NaN);
        float flow = Arg(args, "flow=", float.NaN);
        string[]? parts = args.FirstOrDefault(a => a.StartsWith("parts=", StringComparison.Ordinal))?[6..].Split(',');
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);
        var wanted = args.Where(a => !a.StartsWith("--") && !a.Contains('=') && a is not ("levels" or "render")).ToList();
        var presets = RunningWaterSpec.Presets.Keys.Where(k => wanted.Count == 0 || wanted.Any(w => k.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var key in presets)
        {
            var spec = RunningWaterSpec.ByName(key);
            float q = !float.IsNaN(flow) ? flow : !float.IsNaN(rain) ? spec.FlowFor(rain) : spec.ReferenceFlow;
            // The rain falling on it as it runs: the rain given, or its reference rain if it is rain-fed;
            // dry=1 for the run-off after the rain has stopped.
            float falling = args.Contains("dry=1") ? 0f : !float.IsNaN(rain) ? rain : spec.CatchmentSquareMetres > 0f ? spec.ReferenceRainMmPerHour : 0f;
            var sw = Stopwatch.StartNew();
            var pa = Render(spec, q, sec, 7, parts, falling);
            double cost = sw.Elapsed.TotalSeconds / sec;
            var st = Hydraulics.Of(spec, q);
            Console.WriteLine();
            Console.WriteLine($"== flow:{key} ({spec.Name}): {q:0.####} L/s; declared {spec.SourceLevelDb:F1} dB at 1 m; render costs {cost * 100:F1} % of a core");
            if (spec.Channel != FlowChannel.None)
            {
                Console.WriteLine($"  channel: depth {st.DepthMetres * 100:F2} cm, speed {st.SpeedMetresPerSecond:F2} m/s, Froude {st.Froude:F2}, wetted width {st.WettedWidthMetres:F2} m");
                var census = new RunningWaterSynth(spec, Rate, 7) { Flow = q };
                census.Control(1f);
                Console.WriteLine(census.Census());
            }
            foreach (var f in spec.Falls)
            {
                var wf = RunningWaterSynth.FallFor(spec, f, q);
                Console.WriteLine($"  fall '{f.Name}': {wf.FlowLitresPerSecond:0.####} L/s over {wf.FallMetres:F2} m, drop share {wf.DropShare:F2}, drops {wf.MeanDropRadiusMm:F1} mm, lumps {wf.ChunkRadiusMm:F1} mm");
            }
            NatureSpike.Report("flow:" + key, pa, Rate, calibrated: true);
            var tex = TextureStatistics.Analyse(pa).Summary();
            Console.WriteLine("  texture: " + string.Join(", ", TextureStatistics.Keys.Where(tex.ContainsKey).Select(k => $"{k} {tex[k]:F3}")));
            var (kurt, crest) = TextureStatistics.Waveform(pa);
            Console.WriteLine($"  4-16 kHz in 10 ms: kurtosis {kurt:F2}, crest median {crest:F1} dB");
            if (dir != null)
            {
                string path = Path.Combine(dir, "flow_" + key + ".wav");
                WriteFloatWav(path, pa.Select(p => p * PascalsToFull).ToArray(), 1);
                Console.WriteLine($"  wrote {path}");
            }
        }
        return 0;
    }

    /// <summary>A model at this flow, pascals at a metre, every place summed.</summary>
    public static float[] Render(RunningWaterSpec spec, float flow, float sec, int seed, string[]? parts = null, float rainOnWater = 0f)
    {
        var s = new RunningWaterSynth(spec, Rate, seed) { Flow = flow, RainOnWater = rainOnWater };
        if (parts != null)
        {
            s.SitePart = parts.Contains("sites") ? 1f : 0f;
            s.SprayPart = parts.Contains("spray") ? 1f : 0f;
            s.FallPart = parts.Contains("falls") ? 1f : 0f;
            s.DripPart = parts.Contains("drips") ? 1f : 0f;
            s.RainPart = parts.Contains("rain") ? 1f : 0f;
        }
        // Let it settle: the flow glides, the bursts' clocks start.
        for (int i = 0; i < Rate * 2; i++) { if (i % 256 == 0) s.Control(256f / Rate); s.Next(); }
        int n = (int)(sec * Rate);
        var pa = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (i % 256 == 0) s.Control(256f / Rate);
            pa[i] = s.Next();
        }
        return pa;
    }

    /// <summary>
    /// --running-water cycle PRESET [on=10] [off=20] [out=DIR] [flow=]: a tap turned on for on= seconds and
    /// shut for off=, the basin's level, what leaves through the inlet and how much it gurgles every half
    /// second, and the whole as a mono float WAV at a metre (flow_PRESET_cycle.wav). For a rain-fed outlet,
    /// flow= sets the flow for the on= part and nothing runs in the off= part.
    /// </summary>
    private static int TapCycle(string[] args)
    {
        string key = args.First(a => !a.StartsWith("--") && !a.Contains('=') && a != "cycle");
        var spec = RunningWaterSpec.ByName(key);
        float on = Arg(args, "on=", 10f), off = Arg(args, "off=", 20f);
        float flowOn = Arg(args, "flow=", spec.Tap?.OpenLitresPerSecond ?? spec.ReferenceFlow);
        float flowOff = spec.Tap?.LeakLitresPerSecond ?? 0f;
        var s = new RunningWaterSynth(spec, Rate, 7) { Flow = 0f };
        if (args.FirstOrDefault(a => a.StartsWith("parts=", StringComparison.Ordinal))?[6..].Split(',') is { } parts)
        {
            s.SitePart = parts.Contains("sites") ? 1f : 0f;
            s.FallPart = parts.Contains("falls") ? 1f : 0f;
            s.DripPart = parts.Contains("drips") ? 1f : 0f;
            s.GurglePart = parts.Contains("gurgle") ? 1f : 0f;
            s.PlatePart = parts.Contains("plate") ? 1f : 0f;
        }
        int n = (int)((on + off) * Rate);
        var pa = new float[n];
        int lead = Rate / 2;
        var sw = Stopwatch.StartNew();
        for (int i = -lead; i < n; i++)
        {
            float t = i / (float)Rate;
            if ((i & 255) == 0)
            {
                s.Flow = t >= 0f && t < on ? flowOn : flowOff;
                s.Control(256f / Rate);
                if (i >= 0 && i % (Rate / 2) == 0)
                    Console.WriteLine($"  {t,5:F1} s  flow {s.Flow * 1000,6:F1} mL/s  level {s.Level * 1000,6:F1} mm  over the inlet {s.InletDepth * 1000,6:F1} mm"
                                      + $"  out {s.InletFlow * 1000,6:F1} mL/s  gurgle {(spec.Inlet != null ? RunningWaterSynth.GurgleShare(spec.Inlet, s.InletDepth, s.InletFlow) : 0f):F2}");
            }
            float y = s.Next();
            if (i >= 0) pa[i] = y;
        }
        Console.WriteLine($"  rendered {(on + off):F0} s in {sw.Elapsed.TotalSeconds:F1} s");
        NatureSpike.Report("flow:" + key + " cycle", pa, Rate, calibrated: true);
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"flow_{key}_cycle.wav");
            WriteFloatWav(path, pa.Select(p => p * PascalsToFull).ToArray(), 1);
            Console.WriteLine($"  wrote {path}");
        }
        return 0;
    }

    private static int RunoffTable()
    {
        var rates = new (string Name, float Rate)[] { ("drizzle", 0.5f), ("light", Rainfall.LightRate), ("moderate", Rainfall.ModerateRate),
                                                      ("heavy", Rainfall.HeavyRate), ("violent", Rainfall.ViolentRate) };
        foreach (var (key, make) in RunningWaterSpec.Presets)
        {
            var spec = make();
            if (spec.CatchmentSquareMetres <= 0f) continue;
            Console.WriteLine($"flow:{key}: {spec.CatchmentSquareMetres:F0} m² at {spec.RunoffCoefficient:F2}, τ {spec.CatchmentSeconds:F0} s");
            foreach (var (name, r) in rates)
            {
                float q = spec.FlowFor(r);
                var st = Hydraulics.Of(spec, q);
                Console.WriteLine($"  {name,-9} {r,5:F1} mm/h: {q * 1000:F0} mL/s" + (spec.Channel != FlowChannel.None
                    ? $", {st.DepthMetres * 1000:F0} mm deep, {st.SpeedMetresPerSecond:F2} m/s, {st.WettedWidthMetres * 100:F0} cm wide, Fr {st.Froude:F2}" : ""));
            }
            // After a heavy shower stops: the reservoir's recession.
            Runoff.Reset();
            Runoff.Update(Rainfall.HeavyRate, 0);
            var line = new StringBuilder("  after heavy rain stops:");
            for (int t = 0; t <= 1800; t += 1)
            {
                if (t > 0) Runoff.Update(0f, t);
                if (t % 300 == 0) line.Append(CultureInfo.InvariantCulture, $"  {t / 60} min {spec.FlowFor(Runoff.Through(spec.CatchmentSeconds)) * 1000:F1} mL/s");
            }
            Console.WriteLine(line);
        }
        Runoff.Reset();
        return 0;
    }

    // ── Through the game ─────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds, double CpuPercent, float MixerLoad, int Voices);

    private static int Game(string[] args)
    {
        string outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "/tmp/openfps-running-water";
        string set = args.FirstOrDefault(a => a.StartsWith("set=", StringComparison.Ordinal))?[4..] ?? "all";
        double sec = Arg(args, "sec=", 30f);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        Console.WriteLine($"Extended sources {(ExtendedSources.Enabled ? "spread" : "one point")}; ear model {(OpenFPS.Common.Hearing.EarModel.Enabled ? "on" : "off")}");
        // noise=1: every place of every running water source plays its own white noise instead of its
        // water, through the same voices, places, ground and HRTF: what the game's path does on its own.
        RunningWaterSynth.LabNoise = args.Contains("noise=1");
        if (RunningWaterSynth.LabNoise) Console.WriteLine("Running water replaced by noise (noise=1)");
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
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(600f, 1f, 600f), IsSolid = true },
            Material = new MaterialComponent { Material = "Asphalt" },
        });
        WindField.Weather = WindWeather.Steady(1.5f, 250f, 0.2f);

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
        var defs = new Dictionary<int, EntityDefinition>();
        int Add(string soundId, Vector3 at, float range = 120f, float minDistance = 1f)
        {
            int id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            def.SoundEmitter = new SoundEmitterComponent
            {
                IsSynth = true, SoundId = soundId, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = range, MinDistance = minDistance,
            };
            world.RegisterDefinition(def);
            defs[id] = def;
            return id;
        }
        // A tap turned on or off: the server's SynthRunning, the definition sent again.
        void Turn(int id, bool on)
        {
            var def = defs[id];
            var em = def.SoundEmitter;
            em.SynthRunning = on;
            def.SoundEmitter = em;
            world.RegisterDefinition(def);
        }
        void Remove(params int[] ids)
        {
            world.RemoveEntities(ids);
            foreach (int id in ids) audio.ForgetEntity(id);
            Pump(2.0);
        }
        void Weather(float rainMmPerHour)
        {
            world.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 12f, Humidity = rainMmPerHour > 0f ? 0.9f : 0.75f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f,
                PrecipitationIntensity = rainMmPerHour > 0f ? Rainfall.IntensityFor(rainMmPerHour) : 0f,
                RainRateMmPerHour = rainMmPerHour,
            });
        }
        // The run-off held where it is told, whatever falls: a drain heard running with or without the rain.
        void Runoff(float mmPerHour)
        {
            OpenFPS.Common.Runoff.Settle(mmPerHour);
            OpenFPS.Common.Runoff.Held = true;
        }

        try
        {
            Weather(0f);
            Runoff(0f);
            Pump(2.0);
            Record("silence", 2.0);
            Vector3 o = new(0f, 0f, 0f);

            if (set is "all" or "creek")
            {
                // A creek running east-west, its middle at o; the listener on the bank to its south.
                int id = Add("flow:creek", o + new Vector3(0f, 0.05f, 0f), 160f, 2f);
                foreach (float d in new[] { 2f, 8f })
                {
                    Stand(o + new Vector3(0f, 0f, -1.25f - d), o);
                    Pump(4.0);
                    Record($"creek bank {d:0}m", sec);
                }
                // Walking along the bank, 2 m off the water, the way it flows.
                Stand(o + new Vector3(-14f, 0f, -3.25f), o + new Vector3(10f, 0f, -3.25f));
                Pump(3.0);
                double t0 = clock.Elapsed.TotalSeconds;
                perFrame = t => Stand(o + new Vector3(-14f + 1.1f * (float)(t - t0), 0f, -3.25f), o + new Vector3(100f, 0f, -3.25f));
                Record("creek walk along", 28.0 / 1.1);
                perFrame = null;
                Remove(id);
            }

            // A street: the gutter along the kerb (east-west, the road to the north), its drain at the
            // gutter's east end, and a house's downpipe on the building line 9.5 m south of the kerb.
            Vector3 kerb = new(60f, 0f, 0f);
            Vector3 grate = kerb + new Vector3(7.5f + 0.4f, 0.0f, 0.2f);
            Vector3 pipe = kerb + new Vector3(3f, 0.15f, -3.3f);
            int[] Street(bool gutter, bool drain, bool downpipe)
            {
                var ids = new List<int>();
                if (gutter) ids.Add(Add("flow:gutter", kerb + new Vector3(0f, 0.02f, 0.15f), 80f, 1f));
                if (drain) ids.Add(Add("flow:drain_grate", grate, 80f, 1f));
                if (downpipe) ids.Add(Add("flow:downpipe", pipe, 80f, 1f));
                return ids.ToArray();
            }
            void StreetScene(string label, float rain, float runoff, bool raining, Vector3 feet, Vector3 facing, bool gutter, bool drain, bool downpipe)
            {
                Weather(raining ? rain : 0f);
                Runoff(runoff);
                var ids = Street(gutter, drain, downpipe);
                Stand(feet, facing);
                Pump(5.0);
                Record(label, sec);
                Remove(ids);
            }
            Vector3 onPavement = kerb + new Vector3(4f, 0f, -1.5f);
            if (set is "all" or "rain")
            {
                foreach (var (name, r) in new[] { ("moderate", Rainfall.ModerateRate), ("heavy", Rainfall.HeavyRate) })
                {
                    StreetScene($"gutter and drain {name} no rain heard", r, r, false, onPavement, grate, true, true, false);
                    StreetScene($"gutter and drain {name} in the rain", r, r, true, onPavement, grate, true, true, false);
                    StreetScene($"drain {name} 1.5m, no rain heard", r, r, false, grate + new Vector3(0f, 0f, -1.5f), grate, false, true, false);
                    StreetScene($"gutter {name} 1.5m, no rain heard", r, r, false, kerb + new Vector3(-2f, 0f, -1.5f), kerb + new Vector3(-2f, 0f, 0f), true, false, false);
                    StreetScene($"downpipe {name} 1.5m, no rain heard", r, r, false, pipe + new Vector3(0f, -0.15f, 1.5f), pipe, false, false, true);
                    StreetScene($"downpipe {name} 1.5m, in the rain", r, r, true, pipe + new Vector3(0f, -0.15f, 1.5f), pipe, false, false, true);
                }
            }
            if (set is "downpipes")
            {
                // The downpipe scenes alone, for measuring what the game's path does to them: the street's
                // downpipe from 1.5 m in moderate and heavy rain (the rain not heard), then the house's
                // downpipe and its outlet together and the outlet alone, in heavy rain.
                foreach (var (name, r) in new[] { ("moderate", Rainfall.ModerateRate), ("heavy", Rainfall.HeavyRate) })
                    StreetScene($"downpipe {name} 1.5m, no rain heard", r, r, false, pipe + new Vector3(0f, -0.15f, 1.5f), pipe, false, false, true);
                Vector3 shoe = o + new Vector3(30f, 0.15f, 60f);
                Vector3 outlet = shoe + new Vector3(0f, 2.65f, -0.1f);
                Weather(0f);
                Runoff(Rainfall.HeavyRate);
                int a = Add("flow:downpipe", shoe, 50f, 0.6f), b = Add("flow:gutter_outlet", outlet, 50f, 0.6f);
                Stand(new(shoe.X - 1f, 0f, shoe.Z + 2f), shoe);
                Pump(5.0);
                Record("downpipe and gutter outlet heavy", sec);
                Remove(a, b);
                int c = Add("flow:gutter_outlet", outlet, 50f, 0.6f);
                Stand(new Vector3(outlet.X - 1f, 0f, outlet.Z + 1.5f), outlet);
                Pump(5.0);
                Record("gutter outlet alone heavy", sec);
                Remove(c);
            }
            if (set is "all" or "after")
            {
                // After a heavy shower: what is left in the catchments, no rain falling. The street's
                // ten minutes on, the roof's (a faster catchment) three.
                float After(float seconds, string preset)
                {
                    OpenFPS.Common.Runoff.Held = false;
                    OpenFPS.Common.Runoff.Reset();
                    OpenFPS.Common.Runoff.Update(Rainfall.HeavyRate, 0);
                    for (int t = 1; t <= seconds; t++) OpenFPS.Common.Runoff.Update(0f, t);
                    return OpenFPS.Common.Runoff.Through(RunningWaterSpec.ByName(preset).CatchmentSeconds);
                }
                float gutterAfter = After(600f, "gutter");
                float pipeAfter = After(180f, "downpipe");
                Console.WriteLine($"  after heavy rain: street catchment 10 min on {gutterAfter:F3} mm/h, roof 3 min on {pipeAfter:F4} mm/h");
                StreetScene("drain after the rain 1.5m", 0f, gutterAfter, false, grate + new Vector3(0f, 0f, -1.5f), grate, true, true, false);
                StreetScene("downpipe after the rain 1.5m", 0f, pipeAfter, false, pipe + new Vector3(0f, -0.15f, 1.5f), pipe, false, false, true);
                // Later still: the downpipe down to a drip.
                StreetScene("downpipe dripping 1.5m", 0f, 0.15f, false, pipe + new Vector3(0f, -0.15f, 1.5f), pipe, false, false, true);
            }
            if (set is "fountain")
            {
                // The Elm Park fountain (its five taps where the city map has them), standing 4 m south of
                // its kerb facing it: as it is, and with a basin overflow in the south kerb's inside face.
                Runoff(0f);
                Weather(0f);
                WindField.Weather = WindWeather.Steady(2f, 250f, 0.2f);
                Vector3 centre = o + new Vector3(-120f, 0f, 60f);
                (int Tap, Vector3 At)[] taps = { (0, new(0f, 1.9f, 0f)), (1, new(0f, 0.8f, 2.3f)), (2, new(2.3f, 0.8f, 0f)), (3, new(0f, 0.8f, -2.3f)), (4, new(-2.3f, 0.8f, 0f)) };
                var ids = taps.Select(t => Add($"water:park_fountain/elm_park/{t.Tap}", centre + t.At, 160f, 1.5f)).ToList();
                Vector3 feet = centre + new Vector3(0f, 0f, -5.5f - 4f);
                Stand(feet, centre);
                Pump(5.0);
                Record("fountain 4m as it is", sec);
                // The overflow: in the south kerb's inside face, a 1.2 m weir, its sump under the paving.
                int overflow = Add("flow:basin_overflow", centre + new Vector3(0f, 0.3f, -5.1f), 80f, 1f);
                Pump(5.0);
                Record("fountain 4m with its overflow", sec);
                Remove(ids.Append(overflow).ToArray());
            }
            if (set is "taps" or "round2")
            {
                // A kitchen in a flat (no walls here: the open path). Standing at the sink, facing it.
                Runoff(0f);
                Weather(0f);
                Vector3 k = o + new Vector3(-30f, 0f, 30f);
                void TapScene(string label, string sound, Vector3 at, float on, float off, bool startOn = true)
                {
                    int id = Add(sound, at, 30f, 0.5f);
                    Turn(id, false);
                    Stand(new Vector3(at.X, 0f, at.Z - 0.6f), at);
                    Pump(2.0);
                    if (startOn) Turn(id, true);
                    if (on > 0f) Record($"{label} running", on);
                    Turn(id, false);
                    if (off > 0f) Record($"{label} shut draining", off);
                    Remove(id);
                }
                TapScene("kitchen sink", "flow:kitchen_sink", k + new Vector3(0f, 0.9f, 0f), 30f, 30f);
                TapScene("washbasin", "flow:washbasin", k + new Vector3(4f, 0.85f, 0f), 25f, 25f);
                TapScene("shower", "flow:shower", k + new Vector3(8f, 0.05f, 0f), 30f, 10f);
                TapScene("dripping tap", "flow:dripping_sink", k + new Vector3(12f, 0.9f, 0f), 0f, 30f, startOn: false);
            }
            if (set is "roof" or "round2")
            {
                // A house's downpipe and, 2.7 m over it, its roof gutter's outlet; the listener in the garden
                // 2 m from the wall, in heavy and in violent rain, then after the rain.
                Vector3 shoe = o + new Vector3(30f, 0.15f, 60f);
                Vector3 outlet = shoe + new Vector3(0f, 2.65f, -0.1f);
                Vector3 garden = new(shoe.X - 1f, 0f, shoe.Z + 2f);
                foreach (var (name, r) in new[] { ("heavy", Rainfall.HeavyRate), ("violent", Rainfall.ViolentRate) })
                {
                    Weather(0f);
                    Runoff(r);
                    int a = Add("flow:downpipe", shoe, 50f, 0.6f), b = Add("flow:gutter_outlet", outlet, 50f, 0.6f);
                    Stand(garden, shoe);
                    Pump(5.0);
                    Record($"downpipe and gutter outlet {name}", sec);
                    Remove(a, b);
                    int c = Add("flow:gutter_outlet", outlet, 50f, 0.6f);
                    Stand(new Vector3(outlet.X - 1f, 0f, outlet.Z + 1.5f), outlet);
                    Pump(5.0);
                    Record($"gutter outlet alone {name}", sec);
                    Remove(c);
                }
                // The long drip: the roof's run-off through its two stores, 10 and 25 minutes after a heavy
                // shower.
                foreach (float minutes in new[] { 10f, 25f })
                {
                    OpenFPS.Common.Runoff.Held = false;
                    OpenFPS.Common.Runoff.Reset();
                    OpenFPS.Common.Runoff.Update(Rainfall.HeavyRate, 0);
                    for (int t = 1; t <= minutes * 60f; t++) OpenFPS.Common.Runoff.Update(0f, t);
                    OpenFPS.Common.Runoff.Held = true;
                    var dp = RunningWaterSpec.ByName("downpipe");
                    Console.WriteLine($"  {minutes:F0} min after heavy rain: downpipe {dp.FlowNow() * 1000:F2} mL/s");
                    int a = Add("flow:downpipe", shoe, 50f, 0.6f);
                    Stand(garden, shoe);
                    Pump(4.0);
                    Record($"downpipe {minutes:F0} min after the rain", sec);
                    Remove(a);
                }
            }
            if (set is "all" or "overflow")
            {
                Runoff(0f);
                Weather(0f);
                int id = Add("flow:basin_overflow", o + new Vector3(-60f, 0.2f, 0f), 80f, 1f);
                Stand(o + new Vector3(-60f, 0f, -1.5f), o + new Vector3(-60f, 0f, 0f));
                Pump(4.0);
                Record("basin overflow 1.5m", sec);
                Remove(id);
            }
            Record("silence end", 1.0);
        }
        finally
        {
            OpenFPS.Common.Runoff.Held = false;
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
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = interleaved.Length * 4;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)3); w.Write((short)channels);
        w.Write(Rate); w.Write(Rate * 4 * channels); w.Write((short)(4 * channels)); w.Write((short)32); w.Write("data"u8); w.Write(bytes);
        foreach (float v in interleaved) w.Write(v);
    }
}
