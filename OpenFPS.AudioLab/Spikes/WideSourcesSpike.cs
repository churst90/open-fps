using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --wide-sources [out=DIR] [set=measure|roofs|tree|render|level|all] [wide=on|off] [sec=] [turbulence=]
/// [collapse=on] [spread=] | cost: a tree, the Elm Park fountain (its five map taps), the fire pit and
/// street rain through GameLevelsSpike's client path with the HRTF, captured from the master in float, for
/// how alike the ears are (tools/interaural.py) and for listening. No map (no walls, reflections or
/// reverb), flat asphalt; the wind at the ears is off, since its own noise at each ear would read as width.
/// wide=off plays every extended source from one point. DIR/segments.csv names each scene beside
/// DIR/capture.post.wav (after the master limiter), with CPU and mixer load.
/// </summary>
public static class WideSourcesSpike
{
    private sealed record Segment(string Name, double Start, double Seconds, double CpuPercent, float MixerLoad, int Voices, double PlacedDb);

    // Where the sources stand, relative to their own middle, as on the city map (tools/gen_city.py).
    private static readonly (int Tap, Vector3 At)[] FountainTaps =
    {
        (0, new Vector3(0f, 1.9f, 0f)),
        (1, new Vector3(0f, 0.8f, 2.3f)),
        (2, new Vector3(2.3f, 0.8f, 0f)),
        (3, new Vector3(0f, 0.8f, -2.3f)),
        (4, new Vector3(-2.3f, 0.8f, 0f)),
    };
    private const float KerbMetres = 5.5f;

    public static int Run(string[] args)
    {
        if (args.Contains("cost")) return WideSourcesCost.Run();
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-wide-sources";
        string set = Arg(args, "set=") ?? "measure";
        bool wide = (Arg(args, "wide=") ?? "on") is not ("off" or "0");
        float renderSeconds = float.Parse(Arg(args, "sec=") ?? "30", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);
        ExtendedSources.Enabled = wide;
        if (Arg(args, "collapse=") is "1" or "on") ExtendedSources.LayoutScale = 0f;
        if (Arg(args, "spread=") is { } forced) ExtendedSources.ForceSpread = float.Parse(forced, CultureInfo.InvariantCulture);
        // A steady breeze for the level rows: the tree's level then holds still while it is measured.
        if (Arg(args, "turbulence=") is { } turb) _turbulence = float.Parse(turb, CultureInfo.InvariantCulture);
        Console.WriteLine($"Extended sources {(wide ? "spread" : "one point")}; ear model {(OpenFPS.Common.Hearing.EarModel.Enabled ? "on" : "off")}");

        AcousticRegistry.Initialize();
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "capture.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var proc = System.Diagnostics.Process.GetCurrentProcess();

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

        var segments = new List<Segment>();
        Action<double>? perFrame = null;
        int nextId = 100;
        double loadSum = 0; int loadCount = 0;

        // The source's placed power from its voices' gains (LoudestVoices), each weighted by its stream's
        // share: its channel power whatever its spread, without its own wandering.
        string measuredSound = "";
        int measuredMiddle = 0;
        float measuredSpread = 0f;
        int measuredPlaces = 1;
        double placedSum = 0; int placedCount = 0, pumpCount = 0;
        void SamplePlaced()
        {
            if (measuredSound.Length == 0 || (++pumpCount % 25) != 0) return;
            Span<float> shares = stackalloc float[Math.Max(1, measuredPlaces)];
            ExtendedSources.Shares(measuredSpread, shares);
            double p = 0;
            foreach (var v in facade.LoudestVoices(64))
            {
                if (v.SoundId != measuredSound || v.Reflection) continue;
                float share = v.EntityId == measuredMiddle ? shares[0] : (shares.Length > 1 ? shares[1] : 0f);
                p += share * Math.Pow(10, v.Mid / 10.0);
            }
            if (p > 0) { placedSum += p; placedCount++; }
        }
        void Measure(string soundId, int middle, Vector3 at, Vector3 ear)
        {
            measuredSound = soundId;
            measuredMiddle = middle;
            var layout = ExtendedSources.Layout(soundId);
            measuredPlaces = layout?.Length ?? 1;
            measuredSpread = layout == null ? 0f
                : float.IsNaN(ExtendedSources.ForceSpread) ? ExtendedSources.SpreadFor(ExtendedSources.Reach(layout), Vector3.Distance(ear, at))
                : ExtendedSources.ForceSpread;
        }

        void Pump(double seconds)
        {
            var until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until)
            {
                perFrame?.Invoke(clock.Elapsed.TotalSeconds);
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();
                loadSum += facade.MixerLoad; loadCount++;
                SamplePlaced();
                Thread.Sleep(4);
            }
        }
        void Record(string name, double seconds, Action<double>? pump = null)
        {
            double start = clock.Elapsed.TotalSeconds;
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            loadSum = 0; loadCount = 0; placedSum = 0; placedCount = 0;
            (pump ?? Pump)(seconds);
            proc.Refresh();
            double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / seconds * 100.0;
            int used = 160 - provider.SpatialVoicesFree;
            double placed = placedCount > 0 ? 10 * Math.Log10(placedSum / placedCount) : double.NaN;
            segments.Add(new Segment(name, start, seconds, cpu, loadCount > 0 ? (float)(loadSum / loadCount) : 0f, used, placed));
            Console.WriteLine($"  {start,7:F2} s  {name} ({seconds:F1} s)  cpu {cpu:F0} %  mixer {(loadCount > 0 ? loadSum / loadCount : 0):P1}  HRTF voices in use {used}"
                              + (double.IsNaN(placed) ? "" : $"  placed {placed:F2} dB (spread {measuredSpread:F2})"));
        }
        void Stand(Vector3 feet, float yaw)
        {
            player.Position = feet;
            player.Yaw = yaw;
            player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        }
        float YawTo(Vector3 from, Vector3 to) => MathF.Atan2(to.X - from.X, to.Z - from.Z);
        int AddEmitter(string soundId, Vector3 at, float range, float minDistance)
        {
            int id = nextId++;
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.StaticObject,
                Transform = new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            def.SoundEmitter = new SoundEmitterComponent();
            def.SoundEmitter.IsSynth = true;
            def.SoundEmitter.SoundId = soundId;
            def.SoundEmitter.Mode = PlaybackMode.LoopOne;
            def.SoundEmitter.Volume = 1f;
            def.SoundEmitter.Range = range;
            def.SoundEmitter.MinDistance = minDistance;
            world.RegisterDefinition(def);
            return id;
        }
        void Remove(IEnumerable<int> ids)
        {
            var list = ids.ToArray();
            world.RemoveEntities(list);
            foreach (int id in list) audio.ForgetEntity(id);
        }

        var calm = WindWeather.Steady(0f, 250f, 0f);
        var breeze = WindWeather.Steady(4.5f, 250f, _turbulence);
        WindField.Weather = calm;

        // A park tree: its emitter at the middle of its crown, as the map places it (7 m up). d is from
        // the trunk, along the ground.
        void Tree(IEnumerable<float> distances, double seconds, string label = "tree")
        {
            Vector3 trunk = new(-60f, 0f, -60f);
            WindField.Weather = breeze;
            Stand(trunk + new Vector3(0f, 0f, -distances.First()), 0f);
            int id = AddEmitter("foliage:park_tree", trunk + new Vector3(0f, 7f, 0f), 90f, 4f);
            Pump(4.0);
            foreach (float d in distances)
            {
                var feet = trunk + new Vector3(0f, 0f, -d);
                Stand(feet, YawTo(feet, trunk));
                Measure("foliage:park_tree", id, trunk + new Vector3(0f, 7f, 0f), feet + new Vector3(0f, 1.7f, 0f));
                Pump(2.5);
                Record($"{label} {d:0.#}m", seconds);
            }
            measuredSound = "";
            Remove(new[] { id });
            WindField.Weather = calm;
            Pump(1.5);
        }

        // The Elm Park fountain, its five taps where the map has them. d is from the kerb (the basin is
        // 11 m square), standing south of it facing north.
        int[] AddFountain(Vector3 centre)
            => FountainTaps.Select(t => AddEmitter($"water:park_fountain/elm_park/{t.Tap}", centre + t.At, 160f, 1.5f)).ToArray();
        void Fountain(IEnumerable<float> distances, double seconds, string label = "fountain")
        {
            Vector3 centre = new(60f, 0f, -60f);
            WindField.Weather = WindWeather.Steady(2f, 250f, 0.2f);
            Stand(centre + new Vector3(0f, 0f, -KerbMetres - distances.First()), 0f);
            var ids = AddFountain(centre);
            Pump(4.0);
            foreach (float d in distances)
            {
                var feet = centre + new Vector3(0f, 0f, -KerbMetres - d);
                Stand(feet, YawTo(feet, centre));
                Pump(2.5);
                Record($"{label} {d:0.#}m", seconds);
            }
            Remove(ids);
            WindField.Weather = calm;
            Pump(1.5);
        }

        // Walking past the fountain at a slow walk, 4 m off its kerb, facing the way you walk.
        void FountainWalk(float halfLength, float speed)
        {
            Vector3 centre = new(60f, 0f, 60f);
            WindField.Weather = WindWeather.Steady(2f, 250f, 0.2f);
            float z = -KerbMetres - 4f;
            Stand(centre + new Vector3(-halfLength, 0f, z), MathF.PI / 2f);
            var ids = AddFountain(centre);
            Pump(4.0);
            double t0 = clock.Elapsed.TotalSeconds;
            double seconds = 2f * halfLength / speed;
            perFrame = t => Stand(centre + new Vector3(-halfLength + speed * (float)(t - t0), 0f, z), MathF.PI / 2f);
            Record($"fountain walk {speed:0.#}ms", seconds);
            perFrame = null;
            Remove(ids);
            WindField.Weather = calm;
            Pump(1.5);
        }

        // The garden fire pit, its emitter 0.6 m up as on the map. d from its middle.
        void Fire(IEnumerable<float> distances, double seconds, string label = "fire")
        {
            Vector3 pit = new(-60f, 0f, 60f);
            WindField.Weather = WindWeather.Steady(1.5f, 250f, 0.2f);
            Stand(pit + new Vector3(0f, 0f, -distances.First()), 0f);
            int id = AddEmitter("fire:fire_pit", pit + new Vector3(0f, 0.6f, 0f), 120f, 1f);
            Pump(4.0);
            foreach (float d in distances)
            {
                var feet = pit + new Vector3(0f, 0f, -d);
                Stand(feet, YawTo(feet, pit));
                Measure("fire:fire_pit", id, pit + new Vector3(0f, 0.6f, 0f), feet + new Vector3(0f, 1.7f, 0f));
                Pump(2.5);
                Record($"{label} {d:0.#}m", seconds);
            }
            measuredSound = "";
            Remove(new[] { id });
            WindField.Weather = calm;
            Pump(1.5);
        }

        void Rain(float mmPerHour, string label, double seconds)
        {
            Stand(new Vector3(0f, 0f, -150f), 0f);
            world.UpdateAtmosphere(new WorldStateUpdate
            {
                Temperature = 15f, Humidity = 0.8f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f,
                PrecipitationIntensity = 0.5f, RainRateMmPerHour = mmPerHour,
            });
            Pump(5.0);
            Record($"rain {label}", seconds);
            world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.6f, AirPressure = 101325f, AirAbsorptionMultiplier = 1f });
            Pump(3.0);
        }

        // Rain round a listener standing in one of the rain lab's scenes (RainSpike.Scenes: a bus shelter,
        // a room under a steel roof, the driver's seat of a parked car), played by the game's RainField
        // through this provider. roofOnly: the roof over the ear and its near drops alone.
        void Roof(string scene, bool roofOnly, double seconds, string label)
        {
            RainSpike._riding = -1;
            var (w, ear, _) = RainSpike.Scenes().First(x => x.Name == scene).Make();
            int riding = RainSpike._riding;
            w.Temperature = 15f; w.Humidity = 0.8f; w.AirPressure = 1013.25f; w.AirAbsorptionMultiplier = 1f;
            w.PrecipitationIntensity = 0.5f;
            w.RainRateMmPerHour = Rainfall.ModerateRate;
            w.Precipitation = new Precipitation(PrecipitationKind.Rain, Rainfall.ModerateRate);
            var field = new OpenFPS.Client.Core.RainField(facade, new SpatialAcoustics()) { OnlySlot = roofOnly ? OpenFPS.Client.Core.RainSurvey.OverheadSlot : -1 };
            void RainPump(double sec)
            {
                var until = clock.Elapsed.TotalSeconds + sec;
                while (clock.Elapsed.TotalSeconds < until)
                {
                    facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
                    field.Update(w, ear, clock.Elapsed.TotalSeconds, -1, -1, riding);
                    facade.PumpForTest();
                    loadSum += facade.MixerLoad; loadCount++;
                    Thread.Sleep(4);
                }
            }
            RainPump(5.0);
            Record(label, seconds, RainPump);
            if (field.LastSurvey is { } survey) Console.WriteLine("      " + survey.Describe().Replace("\n", "\n      "));
            field.Stop();
            Pump(2.0);
        }

        try
        {
            Pump(2.0);
            Record("silence", 2.0);
            var spots = new[] { 2f, 5f, 10f, 20f };
            if (set is "measure" or "all")
            {
                Tree(spots, 12.0);
                Fountain(spots, 10.0);
                Fire(spots, 12.0);
                Rain(Rainfall.ModerateRate, "street moderate", 12.0);
            }
            if (set is "roofs" or "measure" or "all")
            {
                Roof("street", false, 12.0, "rain street (lab scene)");
                foreach (var scene in new[] { "shelter", "attic", "incar" })
                {
                    Roof(scene, true, 12.0, $"roof only {scene}");
                    Roof(scene, false, 12.0, $"rain {scene}");
                }
            }
            if (set is "tree")
            {
                Tree(spots, 12.0);
            }
            if (set is "render" or "all")
            {
                Tree(new[] { 3f }, renderSeconds, "render tree");
                Fountain(new[] { 4f }, renderSeconds, "render fountain");
                Fire(new[] { 3f }, renderSeconds, "render fire");
                Rain(Rainfall.ModerateRate, "render street moderate", renderSeconds);
                FountainWalk(18f, 1.1f);
                Roof("shelter", false, renderSeconds, "render rain shelter");
                Roof("attic", false, renderSeconds, "render rain attic");
                Roof("incar", false, renderSeconds, "render rain incar");
            }
            if (set is "level")
            {
                // The total at 1, 5 and 20 m from each source's middle, spread or not.
                Tree(new[] { 1f, 5f, 20f }, 20.0, "level tree");
                Fire(new[] { 1f, 5f, 20f }, 20.0, "level fire");
            }
            Record("silence end", 1.0);
        }
        finally
        {
            facade.Dispose();
        }

        var sb = new StringBuilder("name,start,seconds,cpu_percent,mixer_load,hrtf_voices,placed_db\n");
        foreach (var s in segments)
            sb.Append(CultureInfo.InvariantCulture, $"{s.Name},{s.Start:F3},{s.Seconds:F3},{s.CpuPercent:F1},{s.MixerLoad:F4},{s.Voices},{s.PlacedDb:F3}\n");
        File.WriteAllText(Path.Combine(outDir, "segments.csv"), sb.ToString());
        Console.WriteLine($"Wrote {outDir}/capture.post.wav and segments.csv ({segments.Count} segments)");
        return 0;
    }

    private static float _turbulence = 0.25f;

    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];
}
