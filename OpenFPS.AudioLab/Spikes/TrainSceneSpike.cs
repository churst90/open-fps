using System.Globalization;
using System.Numerics;
using System.Text;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --train-scene out=DIR [preset=freight] [sec=80] [lead=30] [at=X,Z] [horn=0|1] [cars=N]
///
/// A train on the city's rail loop as the server runs it (RailSystem, CrossingSystem: its speed from the
/// track, its horn for the crossing 18 s out, the crossing's bell), heard through the whole client
/// (ClientAudioSystem, the render pool, the binaural stage, the loudness law) by somebody standing where
/// Cody stood on 2026-10-07, by the Main Street crossing (-3.9, -183). The train is put on the loop so
/// that its head reaches the crossing <c>lead</c> seconds in. cars=N runs N ordinary cars on a road loop
/// round the listener, for the load around it.
///
/// Writes DIR/capture.post.wav (the master, float) and DIR/census.csv twice a second: where the head is,
/// the voices playing (the train's and all), how many play without HRTF, how many were refused for want
/// of a binaural voice, the binaural voices free, the mixer load and the starves. horn=0 leaves the horn
/// and bell out (a run to subtract from one with them).
/// </summary>
public static class TrainSceneSpike
{
    private static string? Arg(string[] args, string key) => args.FirstOrDefault(x => x.StartsWith(key, StringComparison.Ordinal))?[key.Length..];

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string outDir = Arg(args, "out=") ?? "/tmp/openfps-train-scene";
        string preset = Arg(args, "preset=") ?? "freight";
        double seconds = double.Parse(Arg(args, "sec=") ?? "80", CultureInfo.InvariantCulture);
        double lead = double.Parse(Arg(args, "lead=") ?? "30", CultureInfo.InvariantCulture);
        bool horn = (Arg(args, "horn=") ?? "1") != "0";
        int cars = int.Parse(Arg(args, "cars=") ?? "0", CultureInfo.InvariantCulture);
        var spot = new Vector3(-3.9f, 0.09f, -183f);
        if (Arg(args, "at=") is { } at)
        {
            var p = at.Split(',');
            spot = new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), 0.09f, float.Parse(p[1], CultureInfo.InvariantCulture));
        }
        Directory.CreateDirectory(outDir);
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture.post.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");

        // The server's half: the city, its rail loop, its crossings.
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        if (!maps.TryGetMap("city", out var city, out _, out _, out _) || !maps.TryGetMapData("city", out var cityData))
        { Console.WriteLine("FAIL: no city map"); return 1; }
        var rail = new RailSystem();
        var resend = new HashSet<int>();
        var crossings = new CrossingSystem(rail, id => resend.Add(id));

        // The map's own light rail sets stay off: one train, the one under test.
        var trackData = cityData.Tracks!.First(t => t.Id == "rail_loop");
        var probe = new RaceLine(trackData.Waypoints, 0f, 45f / 3.6f, 0.1f, 1.0f, trackData.BankingDegrees);
        // Where the Main Street crossing is round the loop, and how far back the head starts.
        float crossingAt = 0f, bestD = float.MaxValue;
        for (float s = 0; s < probe.Length; s += 0.5f)
        {
            probe.Sample(s, out var q, out _, out _);
            float d = MathF.Sqrt((q.X - 0f) * (q.X - 0f) + (q.Z + 180f) * (q.Z + 180f));
            if (d < bestD) { bestD = d; crossingAt = s; }
        }
        // The track's own speed near the crossing, for how far back to start.
        probe.Sample(crossingAt, out _, out _, out float vCross);
        float start = crossingAt - (float)lead * MathF.Max(3f, vCross);
        if (start < 0) start += probe.Length;
        var td = new TrainData { Name = "Scene train", Preset = preset, Track = "rail_loop", TopSpeedKmh = 45f, AccelerationMps2 = 0.9f, BrakingMps2 = 1.0f, StartOffsetMetres = start };
        cityData.Trains = new List<TrainData>();
        if (!rail.SpawnOne(maps, "city", td)) { Console.WriteLine("FAIL: the train could not be put on the track"); return 1; }
        crossings.Spawn(maps);
        var signals = new List<(double At, string What)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Open asphalt for the acoustics, as the driving lab: the train and the listener, nothing in the way.
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("FAIL: no Steam Audio context"); return 1; }
        var scene = new SteamAudioScene(ctx);
        scene.Build(new List<SteamAudioScene.Box> { new(new Vector3(0f, -0.5f, 0f), new Vector3(4000f, 1f, 4000f), Quaternion.Identity, "Asphalt") });
        TracedReverbSet.Configure(ctx, scene);

        var provider = new FmodAudioProvider();
        var facade = new AudioEngineFacade(provider);
        string sounds = OpenFPS.AudioLab.LabPaths.Sounds();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        provider.EarWindEnabled = false;

        var world = new ClientWorldState();
        world.Clear(new Vector3(6000, 600, 6000));
        world.SetAcousticMap(new AcousticMap(new Vector3(4000, 400, 4000), new Vector3(-2000, -10, -2000)) { GlobalEnvironmentId = AcousticConstants.GlobalRegionId });
        var player = new LocalPlayerState();
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds);
        world.RegisterDefinition(new EntityDefinition
        {
            EntityId = 1,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.5f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(4000f, 1f, 4000f), IsSolid = true },
            Material = new MaterialComponent { Material = "Concrete" },
        });
        WindField.Weather = WindWeather.Steady(0f, 250f, 0f);
        world.UpdateAtmosphere(new WorldStateUpdate { Temperature = 15f, Humidity = 0.6f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f });
        rail.Heard = (map, source, kind, list) =>
        {
            foreach (var s in list)
                if (TrainSignal.TryParse(s.SynthKey, out string train, out var warning, out float bell))
                {
                    signals.Add((clock.Elapsed.TotalSeconds, $"{kind} {train} {string.Join(",", warning)} bell {bell:F0} s"));
                    if (horn) audio.WorldAudio.TrainSignalReceived!(train, warning, bell);
                }
        };
        // As the server wires them (Program): the rail system asks the crossings where they are.
        rail.CrossingsOn = crossings.PositionsOn;

        // What the client is told: the train's sources and the Main Street crossing's bell and gates.
        const int Offset = 100_000;
        var bridged = new List<Entity>();
        city.Query(new QueryDescription().WithAll<SoundEmitterComponent, Transform>(), (Entity e, ref SoundEmitterComponent em, ref Transform t) =>
        {
            string id = em.SoundId ?? "";
            bool train = id.StartsWith("rail:" + preset + "/Scene_train/", StringComparison.OrdinalIgnoreCase);
            bool crossing = (id.StartsWith("bell:", StringComparison.OrdinalIgnoreCase) || id.StartsWith("gate:", StringComparison.OrdinalIgnoreCase))
                            && Vector3.Distance(new Vector3(t.Position.X, 0f, t.Position.Z), new Vector3(0f, 0f, -180f)) < 30f;
            if (train || crossing) bridged.Add(e);
        });
        void Define(Entity e)
        {
            var def = EntityDefinitionFactory.From(city, e);
            def.EntityId = e.Id + Offset;
            world.RegisterDefinition(def);
        }
        foreach (var e in bridged) Define(e);
        Console.WriteLine($"Train '{preset}' starts {probe.Length - start:F0} m... head at {start:F0} m round the loop, the crossing at {crossingAt:F0} m ({lead:F0} s at {vCross * 3.6f:F0} km/h); {bridged.Count} entities bridged; listener at ({spot.X}, {spot.Z}).");

        // Cars on a road loop round the listener, for the load the city puts round a train.
        var carIds = new List<(int Id, float Phase)>();
        for (int i = 0; i < cars; i++)
        {
            int id = 900_000 + i;
            string carPreset = i % 3 == 0 ? "v8_muscle" : i % 3 == 1 ? "i4_midsize" : "transit_bus";
            var profile = MachineRegistry.VehicleFor(carPreset);
            var def = new EntityDefinition
            {
                EntityId = id, Type = EntityType.NPC, Moves = true,
                Transform = new Transform { Position = spot, Rotation = Quaternion.Identity },
                Collider = new ColliderComponent { Shape = ColliderShape.Box, IsSolid = true, Size = new Vector3(profile.WidthMetres, profile.HeightMetres, profile.LengthMetres) },
            };
            def.SoundEmitter = new SoundEmitterComponent
            {
                IsSynth = true, SoundId = "engine:" + carPreset, Mode = PlaybackMode.LoopOne, Volume = 1f,
                Range = Loudness.AudibleRange(profile.SourceLevelDb), MinDistance = 3f,
            };
            world.RegisterDefinition(def);
            carIds.Add((id, i / (float)Math.Max(1, cars)));
        }

        var census = new StringBuilder("t,head_to_crossing_m,active,train_voices,no_hrtf,refused,binaural_free,mixer_load,starves\n");
        double nextCensus = 0, last = 0;
        int startStarves = EngineVoiceState.GlobalStarves + PhysicalVoiceState.GlobalStarves;
        int mostTrain = 0, mostActive = 0, worstNoHrtf = 0, leastFree = int.MaxValue;
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Clamp(now - last, 0.001, 0.05);
                last = now;
                rail.Update("city", city, dt);
                crossings.Update("city", city, dt);
                foreach (int id in resend)
                {
                    var e = bridged.FirstOrDefault(b => b.Id == id);
                    if (e != Entity.Null) Define(e);
                }
                resend.Clear();
                var states = new List<EntityState>(bridged.Count + carIds.Count);
                foreach (var e in bridged)
                {
                    if (!city.IsAlive(e)) continue;
                    var t = city.Get<Transform>(e);
                    var v = city.Has<Velocity>(e) ? city.Get<Velocity>(e).Linear : Vector3.Zero;
                    states.Add(new EntityState { EntityId = e.Id + Offset, Transform = QuantizedTransform.FromTransform(t), LinearVelocity = v });
                }
                foreach (var (id, phase) in carIds)
                {
                    // A 70 m-radius loop round the listener at 40 km/h.
                    float ang = phase * MathF.Tau + (float)now * (11f / 70f);
                    var pos = spot + new Vector3(70f * MathF.Cos(ang), 0.5f, 70f * MathF.Sin(ang));
                    var vel = new Vector3(-MathF.Sin(ang), 0f, MathF.Cos(ang)) * 11f;
                    var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(-vel.X, -vel.Z));
                    states.Add(new EntityState { EntityId = id, Transform = QuantizedTransform.FromTransform(new Transform { Position = pos, Rotation = heading }), LinearVelocity = vel });
                }
                world.SyncState(states.ToArray());

                player.RidingEntityId = -1;
                player.Position = spot;
                player.Yaw = 0f;
                player.Rotation = Quaternion.Identity;
                audio.Update(world.GetSnapshot());
                facade.PumpForTest();

                if (now >= nextCensus)
                {
                    nextCensus = now + 0.5;
                    var (active, noHrtf, refused, free) = provider.VoiceCensus();
                    int trainVoices = provider.GetActiveSpatialSoundIds().Count(i => i <= ClientAudioSystem.TrainVoiceBase && i > ClientAudioSystem.TrainVoiceBase - 10_000);
                    var heads = rail.HeadsOn("city", "rail_loop", out float lap);
                    float toGo = heads.Count > 0 ? crossingAt - heads[0] : 0f;
                    if (toGo < -lap / 2) toGo += lap;
                    int starves = EngineVoiceState.GlobalStarves + PhysicalVoiceState.GlobalStarves - startStarves;
                    census.Append(CultureInfo.InvariantCulture, $"{now:F2},{toGo:F1},{active},{trainVoices},{noHrtf},{refused},{free},{facade.MixerLoad:F3},{starves}\n");
                    mostTrain = Math.Max(mostTrain, trainVoices);
                    mostActive = Math.Max(mostActive, active);
                    worstNoHrtf = Math.Max(worstNoHrtf, noHrtf);
                    leastFree = Math.Min(leastFree, free);
                }
                Thread.Sleep(4);
            }
        }
        finally
        {
            facade.Dispose();
            TracedReverbSet.Dispose();
            scene.Dispose();
            Phonon.iplContextRelease(ref ctx);
        }
        File.WriteAllText(Path.Combine(outDir, "census.csv"), census.ToString());
        File.WriteAllLines(Path.Combine(outDir, "signals.txt"), signals.Select(s => $"{s.At:F2} {s.What}"));
        Console.WriteLine($"Signals: {string.Join("; ", signals.Select(s => $"{s.At:F1} s {s.What}"))}");
        Console.WriteLine($"Voices: at most {mostTrain} for the train, {mostActive} in all; without HRTF at most {worstNoHrtf}; binaural free at least {leastFree}; starves {EngineVoiceState.GlobalStarves + PhysicalVoiceState.GlobalStarves - startStarves}.");
        Console.WriteLine($"Wrote {outDir}/capture.post.wav, census.csv, signals.txt.");
        return 0;
    }
}
