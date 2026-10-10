using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Stove;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --stove: the gas hob (GasHobSynth, docs/GAS_HOB.md), from its model.
///   levels [preset] [seed=7]   each scene at a metre: the ticks (rate, regularity, peak, energy), the flames by
///        setting (Leq, LAeq, octaves), the hiss and the roar apart, the light-ups and the pop, and what each
///        burner did (failed sparks, delay, joules). SourceLevelDb and PeakHeadroomDb are read from it.
///   render out=DIR [preset] [seed=7]   the scenes as mono float WAVs, pascals at a metre (-20 dBFS is 94 dB SPL).
///   game out=DIR [preset] [dist=1]   through ClientAudioSystem with the HRTF, ear model, loudness law and a
///        kitchen's room (3.6 x 2.6 x 3.2 m, tiled floor, plaster), the hob on the worktop, the listener
///        standing at it; captured to DIR/capture.post.wav with DIR/segments.csv.
/// Scenes: light (the knob turned and held through the ticks until it lights), slow (turned only part way,
/// sparks failing while gas gathers, then on to full: a bigger light-up), simmer_up (low, medium, high),
/// off (turned off from full, the pop, the safety valve's click), four (the four burners lit one after another).
/// Fitting knobs: eff= lueff= height= slope= trim= heat= us= cloud=.
/// </summary>
public static class StoveSpike
{
    private const int Rate = 48000;
    private const float PascalsToFull = 0.1f;   // -20 dBFS is 94 dB SPL

    private sealed record Scene(string Name, float Seconds, Action<GasHobSynth> Setup, (float At, Action<GasHobSynth> Do)[] Events);

    private static GasHobSpec Tuned(GasHobSpec spec, string[] args) => spec with
    {
        FlameEfficiency = Arg(args, "eff=", spec.FlameEfficiency),
        LightUpEfficiency = Arg(args, "lueff=", spec.LightUpEfficiency),
        FlameHeightMm = Arg(args, "height=", spec.FlameHeightMm),
        FlameSlope = Arg(args, "slope=", spec.FlameSlope),
        JetTrimDb = Arg(args, "trim=", spec.JetTrimDb),
        SparkHeatMj = Arg(args, "heat=", spec.SparkHeatMj),
        SparkMicroseconds = Arg(args, "us=", spec.SparkMicroseconds),
        CloudSeconds = Arg(args, "cloud=", spec.CloudSeconds),
    };

    /// <summary>The slow light's hand: pushed in, turned only a third of the way to full and held there while
    /// the sparks fail and the gas gathers, then on to full.</summary>
    public static void SlowLight(GasHobSynth h, int burner = 0)
    {
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Push, Burner = burner, Seconds = 0.15f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = burner, Degrees = 36f, Seconds = 0.9f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Wait, Seconds = 2.6f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = burner, Degrees = GasHobSpec.FullDegrees, Seconds = 0.4f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.WaitLit, Burner = burner, Hold = h.Spec.HoldAfterLightSeconds, Seconds = 12f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Release, Burner = burner, Seconds = 0.15f });
    }

    private static Scene[] Scenes(int burners)
    {
        string off = HobKey.Off(burners);
        string One(int setting) => setting + off[1..];
        var scenes = new List<Scene>
        {
            new("light", 9f, h => h.Begin(off, off, 0f), new (float, Action<GasHobSynth>)[] { (1f, h => h.Change(off, One(3))) }),
            new("slow", 12f, h => h.Begin(off, off, 0f), new (float, Action<GasHobSynth>)[] { (1f, h => SlowLight(h)) }),
            new("simmer_up", 16f, h => h.Begin(One(1), One(1), 0f), new (float, Action<GasHobSynth>)[]
                { (5f, h => h.Change(One(1), One(2))), (10f, h => h.Change(One(2), One(3))) }),
            new("off", 24f, h => h.Begin(One(3), One(3), 0f), new (float, Action<GasHobSynth>)[] { (3f, h => h.Change(One(3), off)) }),
        };
        if (burners == 4)
            scenes.Add(new("four", 28f, h => h.Begin(off, off, 0f), new (float, Action<GasHobSynth>)[]
            {
                (1f, h => h.Change("0000", "3000")), (7f, h => h.Change("3000", "3300")),
                (13f, h => h.Change("3300", "3330")), (19f, h => h.Change("3330", "3333")),
            }));
        return scenes.ToArray();
    }

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        if (args.Contains("game")) return Game(args);
        string preset = args.FirstOrDefault(a => !a.StartsWith("--") && !a.Contains('=') && a is not ("levels" or "render")) ?? "hob4";
        var spec = Tuned(GasHobSpec.ByName(preset), args);
        int seed = (int)Arg(args, "seed=", 7f);
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);

        Console.WriteLine($"== stove:{preset} ({spec.Name}); {spec.Fuel.Name} at {spec.SupplyKPa:F1} kPa, injector jets {spec.InjectorVelocity():F1} m/s");
        foreach (var b in spec.Burners)
            Console.WriteLine($"   {b.Name,-11} injector {b.InjectorMm:F2} mm: {spec.FullFlow(b) * 3.6e6f:F0} l/h, {spec.RatedKw(b):F2} kW gross ({spec.FullHeatWatts(b) / 1000f:F2} kW net), " +
                              $"ports {spec.PortVelocity(b):F2} m/s, roar peak {spec.FlamePeakHz(b):F0} Hz, hiss peak {0.2f * spec.InjectorVelocity() / (b.InjectorMm * 1e-3f) / 1000f:F1} kHz, " +
                              $"Lighthill {JetDb(b.InjectorMm * 1e-3f, spec.InjectorVelocity()):F1} dB");
        Console.WriteLine($"   mixture {spec.PortFraction * 100f:F1} % gas at the ports; spark {spec.SparkEnergyMj:F0} mJ ({spec.SparkHeatMj:F2} mJ heat in {spec.SparkMicroseconds:F0} µs); " +
                          $"module every {MathF.Round(spec.MainsHz / spec.SparkRateHz):F0} mains cycles");

        foreach (var scene in Scenes(spec.Burners.Length))
        {
            var clock = Stopwatch.StartNew();
            var (pa, sparks, synth) = Render(spec, scene, seed, 1f, 1f, 1f, 1f, 1f);
            double cost = clock.Elapsed.TotalSeconds / scene.Seconds * 100;
            Console.WriteLine();
            Console.WriteLine($"-- {scene.Name} ({scene.Seconds:F0} s), rendering costs {cost:F1} % of a core");
            Ticks(pa, sparks);
            for (int i = 0; i < synth.BurnerCount; i++)
                if (synth.LastLightUpJoules(i) > 0f || synth.FailedSparksBeforeLight(i) > 0)
                    Console.WriteLine($"   {spec.Burners[i].Name}: lit after {synth.FailedSparksBeforeLight(i)} failed spark(s), {synth.LastLightDelay(i):F2} s of gas, light-up {synth.LastLightUpJoules(i):F0} J");
            if (scene.Name == "simmer_up")
            {
                Window(pa, "low", 2f, 5f);
                Window(pa, "medium", 7f, 10f);
                Window(pa, "high", 12f, 16f);
                var (hiss, _, _) = Render(spec, scene, seed, 0f, 0f, 1f, 0f, 0f);
                var (roar, _, _) = Render(spec, scene, seed, 0f, 0f, 0f, 1f, 0f);
                Window(hiss, "high, hiss alone", 12f, 16f);
                Window(roar, "high, roar alone", 12f, 16f);
                Window(hiss, "low, hiss alone", 2f, 5f);
                Window(roar, "low, roar alone", 2f, 5f);
            }
            if (scene.Name is "light" or "slow")
            {
                var (up, _, _) = Render(spec, scene, seed, 0f, 0f, 0f, 0f, 1f);
                double peak = up.Max(v => Math.Abs((double)v));
                int at = Array.FindIndex(up, v => Math.Abs(v) >= 0.5 * peak);
                Console.WriteLine($"   light-up alone: peak {Db(peak):F1} dB at {at / (double)Rate:F2} s; " +
                                  $"its 300 ms {Db(Rms(up, Math.Max(0, at - Rate / 20), Rate * 3 / 10)):F1} dB Leq");
                Window(pa, "steady after", scene.Seconds - 3f, scene.Seconds);
            }
            if (scene.Name == "off")
            {
                Window(pa, "full before", 0.5f, 2.5f);
                PeakIn(pa, "turned off (knob, pop)", 3f, 5f);
                PeakIn(pa, "safety valve", 6f, 24f);
            }
            if (scene.Name == "four")
            {
                Window(pa, "all four on full (the declared level)", 24f, 28f);
                double leq = Db(Rms(pa, 24 * Rate, 4 * Rate));
                double tick = sparks.Select(s => Peak(pa, s, Rate * 12 / 1000)).DefaultIfEmpty(0).Max();
                Console.WriteLine($"   spark peaks reach {Db(tick):F1} dB: {Db(tick) - leq:F1} dB over the declared level");
            }
            if (dir != null)
            {
                string path = Path.Combine(dir, $"stove_{scene.Name}.wav");
                WavesSpike.WriteFloatWav(path, pa.Select(p => p * PascalsToFull).ToArray(), 1);
                Console.WriteLine($"   wrote {path}");
            }
        }
        return 0;
    }

    private static (float[] Pa, List<int> Sparks, GasHobSynth Synth) Render(GasHobSpec spec, Scene scene, int seed,
        float sparkPart, float clickPart, float hissPart, float flamePart, float lightPart)
    {
        var h = new GasHobSynth(spec, Rate, seed)
        {
            SparkPart = sparkPart, ClickPart = clickPart, HissPart = hissPart, FlamePart = flamePart, LightUpPart = lightPart,
        };
        scene.Setup(h);
        int n = (int)(scene.Seconds * Rate);
        var pa = new float[n];
        var sparks = new List<int>();
        int ev = 0, last = h.Sparks;
        for (int i = 0; i < n; i++)
        {
            while (ev < scene.Events.Length && i >= (int)(scene.Events[ev].At * Rate)) scene.Events[ev++].Do(h);
            pa[i] = h.Next();
            if (h.Sparks != last) { last = h.Sparks; sparks.Add(i); }
        }
        return (pa, sparks, h);
    }

    private static void Ticks(float[] pa, List<int> sparks)
    {
        if (sparks.Count == 0) return;
        var iv = sparks.Zip(sparks.Skip(1), (a, b) => (b - a) / (double)Rate).ToList();
        var peaks = sparks.Select(s => Peak(pa, s, Rate * 12 / 1000)).ToList();
        // The tick's energy: 5 ms from the spark, as a sound exposure level.
        var sel = sparks.Select(s => 10 * Math.Log10(Math.Max(1e-20, Energy(pa, s, Rate * 12 / 1000) / Rate) / 4e-10)).ToList();
        string rate = iv.Count > 0 ? $"{1 / iv.Average():F2}/s, intervals {iv.Min() * 1000:F0}-{iv.Max() * 1000:F0} ms, CV {Std(iv) / iv.Average() * 100:F1} %" : "one";
        Console.WriteLine($"   {sparks.Count} sparks, {rate}; peaks {Db(peaks.Min()):F1}-{Db(peaks.Max()):F1} dB at a metre, SEL {sel.Average():F1} dB");
    }

    private static void Window(float[] pa, string label, float from, float to)
    {
        int a = (int)(from * Rate), n = (int)((to - from) * Rate);
        if (a + n > pa.Length) n = pa.Length - a;
        var seg = pa.AsSpan(a, n).ToArray();
        var oct = WavesSpike.OctavesRe1k(seg);
        Console.WriteLine($"   {label}: Leq {Db(Rms(pa, a, n)):F1} dB, LAeq {Db(Math.Sqrt(AWeightedPower(seg))):F1} dB(A); octaves re 1 kHz 63 Hz-16 kHz: "
                          + string.Join(" ", oct.Select(v => $"{v:F1}")));
    }

    private static void PeakIn(float[] pa, string label, float from, float to)
    {
        int a = (int)(from * Rate), b = Math.Min(pa.Length, (int)(to * Rate));
        int at = a;
        for (int i = a; i < b; i++) if (Math.Abs(pa[i]) > Math.Abs(pa[at])) at = i;
        Console.WriteLine($"   {label}: loudest {Db(Math.Abs(pa[at])):F1} dB peak at {at / (double)Rate:F2} s");
    }

    private static double Peak(float[] x, int from, int n)
    {
        double p = 0;
        for (int i = from; i < Math.Min(x.Length, from + n); i++) p = Math.Max(p, Math.Abs(x[i]));
        return p;
    }

    private static double Energy(float[] x, int from, int n)
    {
        double e = 0;
        for (int i = from; i < Math.Min(x.Length, from + n); i++) e += (double)x[i] * x[i];
        return e;
    }

    private static double Rms(float[] x, int from, int n) => Math.Sqrt(Energy(x, from, n) / Math.Max(1, n));
    private static double Db(double pa) => 20 * Math.Log10(Math.Max(1e-12, pa) / 20e-6);
    private static double Std(List<double> v) { double m = v.Average(); return Math.Sqrt(v.Select(x => (x - m) * (x - m)).Average()); }

    /// <summary>Mean square after IEC 61672 A-weighting, by FFT.</summary>
    private static double AWeightedPower(float[] x)
    {
        int n = 1;
        while (n < x.Length) n <<= 1;
        var buf = new Complex[n];
        for (int i = 0; i < x.Length; i++) buf[i] = x[i];
        Spectrum.Fft(buf);
        double sum = 0;
        for (int k = 1; k < n / 2; k++)
        {
            double f = k * (double)Rate / n;
            double f2 = f * f;
            double ra = 12194.0 * 12194 * f2 * f2 / ((f2 + 20.6 * 20.6) * Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9)) * (f2 + 12194.0 * 12194));
            double g = ra * 1.2589;
            sum += 2 * buf[k].Magnitude * buf[k].Magnitude * g * g;
        }
        return sum / ((double)n * x.Length);
    }

    private static float JetDb(float d, float u)
    {
        double w = 1e-4 * 1.2 * Math.Pow(u, 8) * d * d / Math.Pow(343, 5);
        return (float)(10 * Math.Log10(w * 1.2 * 343 / (4 * Math.PI) / 4e-10));
    }

    // ── Through the game ─────────────────────────────────────────────────────────────────────────

    private sealed record Segment(string Name, double Start, double Seconds);

    private static int Game(string[] args)
    {
        string outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? "/tmp/openfps-stove";
        string preset = args.FirstOrDefault(a => !a.StartsWith("--") && !a.Contains('=') && a != "game") ?? "hob4";
        float dist = Arg(args, "dist=", 1f);
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var spec = GasHobSpec.ByName(preset);
        int n = spec.Burners.Length;
        string off = HobKey.Off(n);
        string One(int setting) => setting + off[1..];

        // The kitchen: 3.6 m across, 3.2 deep, 2.6 high; a worktop along the back wall with the hob in it.
        var room = new Vector3(3.6f, 2.6f, 3.2f);
        var centre = new Vector3(0f, room.Y / 2f, 0.4f);
        float back = centre.Z + room.Z / 2f;
        var hobAt = new Vector3(0f, 0.92f, back - 0.32f);
        var boxes = new List<(Vector3 C, Vector3 S, string M)>
        {
            (new Vector3(0f, -0.1f, centre.Z), new Vector3(room.X + 0.4f, 0.2f, room.Z + 0.4f), "Tile"),
            (new Vector3(0f, room.Y + 0.1f, centre.Z), new Vector3(room.X + 0.4f, 0.2f, room.Z + 0.4f), "Plaster"),
            (new Vector3(-room.X / 2f - 0.1f, room.Y / 2f, centre.Z), new Vector3(0.2f, room.Y, room.Z), "Plaster"),
            (new Vector3(room.X / 2f + 0.1f, room.Y / 2f, centre.Z), new Vector3(0.2f, room.Y, room.Z), "Plaster"),
            (new Vector3(0f, room.Y / 2f, back + 0.1f), new Vector3(room.X, room.Y, 0.2f), "Tile"),
            (new Vector3(0f, room.Y / 2f, centre.Z - room.Z / 2f - 0.1f), new Vector3(room.X, room.Y, 0.2f), "Plaster"),
            // The worktop and the cupboards under it.
            (new Vector3(0f, 0.45f, back - 0.3f), new Vector3(room.X, 0.9f, 0.6f), "Wood"),
        };
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no Steam Audio context"); return 1; }
        var scene = new SteamAudioScene(ctx);
        scene.Build(boxes.Select(b => new SteamAudioScene.Box(b.C, b.S, Quaternion.Identity, b.M)).ToList());
        TracedReverbSet.Configure(ctx, scene);

        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
        var clock = Stopwatch.StartNew();
        var world = new ClientWorldState();
        world.Clear(new Vector3(400, 100, 400));
        world.SetAcousticMap(new AcousticMap(new Vector3(200, 40, 200), new Vector3(-100, -5, -100)) { GlobalEnvironmentId = AcousticConstants.GlobalRegionId });
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);
        int id = 1;
        foreach (var b in boxes)
        {
            world.RegisterDefinition(new EntityDefinition
            {
                EntityId = id++, Type = EntityType.StaticObject,
                Transform = new Transform { Position = b.C, Rotation = Quaternion.Identity, Scale = Vector3.One },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = b.S, IsSolid = true },
                Material = new MaterialComponent { Material = b.M },
            });
        }
        int I(string m) => AcousticRegistry.TryGetResonanceIndex(m, out int k) ? k : 0;
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 50, Type = EntityType.Trigger,
            Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = room, IsSolid = false },
            Region = new RegionComponent
            {
                FriendlyName = "Kitchen", IsIndoor = true, RoomSize = room, ReverbTimeScale = 1f,
                Materials = new[] { I("Tile"), I("Plaster"), I("Tile"), I("Plaster"), I("Plaster"), I("Plaster") },
            },
        });
        world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 20f, Humidity = 0.5f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });
        WindField.Weather = WindWeather.Steady(0f, 0f, 0f);

        // The cook standing at the hob, facing it, the ear `dist` metres from the hob's middle.
        float earY = 1.55f;
        float back2 = MathF.Sqrt(MathF.Max(0.01f, dist * dist - (earY - hobAt.Y) * (earY - hobAt.Y)));
        player.Position = new Vector3(0f, 0f, hobAt.Z - back2);
        player.Yaw = 0f;
        player.Rotation = Quaternion.Identity;

        var segments = new List<Segment>();
        var hobDef = new EntityDefinition
        {
            EntityId = 100, Type = EntityType.StaticObject,
            Transform = new Transform { Position = hobAt, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.58f, 0.05f, 0.5f), IsSolid = false },
            Material = new MaterialComponent { Material = "Metal" },
        };
        void Key(string from, string to, double ago = 0)
        {
            var em = new SoundEmitterComponent
            {
                IsSynth = true, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 25f, MinDistance = 0.5f,
                SoundId = new HobKey(preset, from, to, WindField.Now() - ago).Format(),
                SynthRunning = to.Any(c => c != '0'),
            };
            hobDef.SoundEmitter = em;
            world.RegisterDefinition(hobDef);
        }
        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                Thread.Sleep(4);
            }
        }
        void Record(string name, double seconds, params (double At, Action Do)[] events)
        {
            double start = clock.Elapsed.TotalSeconds;
            int ev = 0;
            while (clock.Elapsed.TotalSeconds - start < seconds)
            {
                while (ev < events.Length && clock.Elapsed.TotalSeconds - start >= events[ev].At) events[ev++].Do();
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                Thread.Sleep(4);
            }
            segments.Add(new Segment(name, start, seconds));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)");
        }
        void Gone()
        {
            world.RemoveEntities(new[] { 100 });
            audio.ForgetEntity(100);
            Pump(1.5);
        }

        try
        {
            Pump(2.0);
            Record("silence", 2.0);
            // 1. Knob turned and held through the ticks until it lights.
            Key(off, off, 100);
            Pump(1.0);
            Record("light", 9.0, (0.5, () => Key(off, One(3))));
            Gone();
            // 2. A slow light: the knob only part way while the sparks fail, then on to full.
            StoveVoiceState.LabCreated = h => SlowLight(h);
            Record("slow", 12.0, (0.5, () => Key(off, off)));
            StoveVoiceState.LabCreated = null;
            Gone();
            // 3. On low, turned up to medium and to full.
            Key(One(1), One(1), 300);
            Pump(1.5);
            Record("simmer_up", 16.0, (5.0, () => Key(One(1), One(2))), (10.0, () => Key(One(2), One(3))));
            Gone();
            // 4. Turned off from full.
            Key(One(3), One(3), 300);
            Pump(1.5);
            Record("off", 24.0, (2.0, () => Key(One(3), off)));
            Gone();
            // 5. All four lit one after another.
            if (n == 4)
            {
                Key(off, off, 100);
                Pump(1.0);
                Record("four", 28.0, (1.0, () => Key("0000", "3000")), (7.0, () => Key("3000", "3300")),
                       (13.0, () => Key("3300", "3330")), (19.0, () => Key("3330", "3333")));
                Gone();
            }
            Record("silence end", 1.0);
        }
        finally
        {
            facade.Dispose();
        }
        var sb = new StringBuilder("name,start,seconds\n");
        foreach (var s in segments) sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3}\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav and segments.csv ({segments.Count} segments)");
        return 0;
    }

    private static float Arg(string[] args, string prefix, float fallback)
        => float.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length),
                          NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
}
