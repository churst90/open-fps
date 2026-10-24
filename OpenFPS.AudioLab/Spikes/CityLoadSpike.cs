using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Arch.Core;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Audio.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Environment;
using OpenFPS.Geometry;
using OpenFPS.Materials;
using OpenFPS.Models;
using OpenFPS.Native.Platform;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Sound;
using OpenFPS.Sound.Core;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --city-load [out=DIR] [at=x,y,z] [sec=30] [repeat=2] [prerun=20] [warm=1]: the city arriving at a login,
/// as the client receives it (BusyStreetSpike's server, the whole map sent at once, then the client world,
/// its acoustic bake, the spawn, and ClientAudioSystem through the real mixer), reported second by second:
/// the mixer's load and overrun blocks, capture gaps, the worst placement pass and client loop iteration
/// with their named parts, voices started by kind, and garbage collection pauses. repeat=2 loads the city
/// again in the same process: the difference between the two is first-time cost. warm=1 runs AudioWarmUp,
/// as the login screen does, before the first load.
/// </summary>
public static class CityLoadSpike
{
    private static readonly OpenFPS.Sound.Hearing.HearingSettings LabHearing = OpenFPS.Sound.Hearing.HearingSettings.FromEnvironment();

    private static readonly string[] LoopNames = { "messages", "interpolate", "snapshot", "others", "player", "audio", "pump" };
    private const int LoopMessages = 0, LoopInterpolate = 1, LoopSnapshot = 2, LoopOthers = 3,
                      LoopPlayer = 4, LoopAudio = 5, LoopPump = 6;

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    private static string? Arg(string[] args, string key)
    {
        foreach (var a in args) if (a.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return a[(key.Length + 1)..];
        return null;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>One second of one load.</summary>
    private sealed class SecondRow
    {
        public int Second;
        public double StartedAt;
        public double MixerMax, MixerSum;
        public int MixerSamples, MixerOverSamples;
        public long LateBlocks;
        public double WorstBlockGapMs;
        public long FrameGap;
        public double WorstPassMs, WorstGapMs, WorstEmittersMs, WorstPartPassMs;
        public double TickSumMs;
        public int TickCount;
        public readonly double[] PassStages;
        public readonly double[] PassParts;
        public double WorstLoopMs;
        public readonly double[] WorstLoopParts;
        public readonly double[] WorstLoopOtherParts;
        public int OtherLandCount, OtherStepCount;
        public double OtherWorstLandMs, OtherWorstStepMs;
        public double AudioOtherStepsMs;
        public int AudioOtherStepsCount;
        public int Started;
        public string StartedKinds = "";
        public int GcCount, GcGen2;
        public double GcPauseMs, GcLongestMs;
        public int Active, NoHrtf, Refused, BinauralFree;

        public SecondRow(int second, double startedAt, int stages, int parts)
        {
            Second = second;
            StartedAt = startedAt;
            PassStages = new double[stages];
            PassParts = new double[parts];
            WorstLoopParts = new double[LoopNames.Length];
            WorstLoopOtherParts = new double[OtherBodies.LabPartCount];
        }
    }

    private sealed class LoadReport
    {
        public int Load;
        public double RegistrationMs, BakeMs;
        public int Definitions;
        public double SpawnAt;
        public double MinBinauralFree = double.MaxValue;
        public int MaxNoHrtf;
        public int RefusedAtEnd;
        public long LateBlocks;
        public long FrameGap;
        public double WorstBlockGapMs;
        public double MixerMax;
        public long GcCount, GcGen2;
        public double GcPauseMs, GcLongestMs;
        public int StartedTotal;
        public readonly Dictionary<string, int> StartedKinds = new(StringComparer.Ordinal);
        public readonly List<SecondRow> Seconds = new();
    }

    private sealed class BakeState
    {
        public volatile bool Done;
        public double Ms;
        public string? Error;
    }

    public static int Run(string[] args)
    {
        string outDir = Arg(args, "out") ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".cache", "openfps-build", "warm1", "city-load");
        double seconds = double.Parse(Arg(args, "sec") ?? "30", CultureInfo.InvariantCulture);
        double prerun = double.Parse(Arg(args, "prerun") ?? "20", CultureInfo.InvariantCulture);
        int repeat = int.Parse(Arg(args, "repeat") ?? "2", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(outDir);

        // The capture files sit in the run's own folder: /tmp is RAM, and the handoff keeps every artifact
        // under the build cache.
        System.Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "mix.wav"));
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", Path.Combine(outDir, "capture-float.wav"));
        System.Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        System.Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");

        AcousticRegistry.Initialize();

        // The login screen's warm-up, before the real mixer: every kind of voice rendered once and thrown
        // away, so load 1 is measured against load 2 rather than against the JIT.
        if (args.Contains("warm=1"))
        {
            var warm = new AudioWarmUp(new AudioDataPaths(LabPaths.Sounds(), null, null));
            var warmClock = Stopwatch.StartNew();
            bool completed = warm.RunToCompletion();
            Console.WriteLine($"  warm-up: {(completed ? "completed" : "stopped")} in {warmClock.Elapsed.TotalSeconds:F1} s");
        }

        string baseDir = AppContext.BaseDirectory;
        var prefabs = new PrefabRepository(Path.Combine(baseDir, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(baseDir, "maps")), prefabs);
        maps.Initialize();
        if (!maps.TryGetMap("city", out var ecs, out var size, out var grid, out var lookup) || !maps.TryGetMapData("city", out var data))
        { Console.WriteLine("FAIL: no city map"); return 1; }

        var composites = new CompositeService(maps, prefabs,
            new CompositeRepository(Path.Combine(outDir, "no-composites-" + Guid.NewGuid().ToString("N"))));
        composites.PlaceRecorded(maps);
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions, new OccupancyService(maps), new HandsService(maps));
        server.Vehicles.Spawn(maps, composites);
        server.Rail.Spawn(maps);
        server.Vehicles.Heard = (map, id, label, sounds) => server.EmitWorldAudio(map, id, label, sounds);
        server.Rail.Heard = (map, id, label, sounds) => server.EmitWorldAudio(map, id, label, sounds);
        maps.RefreshEarshotRanges();

        // Brandt Court's stairwell, floor 0, from the map's own region and standing-spot rule. at= is
        // typed as /tp takes it: east, north, floor height.
        Vector3 feet;
        string spotNote;
        if (Arg(args, "at") is { } atArg)
        {
            var p = atArg.Split(',');
            feet = PlayerCoordinates.ToWorld(F(p[0]), F(p[1]), F(p[2]));
            spotNote = "at= override";
        }
        else
        {
            Entity stair = Entity.Null;
            ecs.Query(new QueryDescription().WithAll<RegionComponent>(),
                (Entity e, ref RegionComponent r) =>
                {
                    if (r.FriendlyName.Equals("Brandt Court stairwell, floor 0", StringComparison.OrdinalIgnoreCase))
                        stair = e;
                });
            if (stair == Entity.Null)
            {
                Console.WriteLine("FAIL: no Brandt Court stairwell, floor 0 region");
                return 1;
            }
            feet = StandingSpots.InRoom(ecs, grid, stair)
                   ?? throw new InvalidOperationException("Brandt Court stairwell floor 0 has no standing spot");
            spotNote = "StandingSpots.InRoom over the stairwell's region and boxes";
        }

        var body = ecs.Create(
            new PlayerComponent { ConnectionId = 1, Username = "listener" },
            EntityType.Player,
            new Transform { Position = feet, Rotation = Quaternion.Identity },
            new Velocity(), new MaterialComponent { Material = "Generic" },
            new NameComponent { Name = "listener" },
            new HealthComponent { Current = 100, Max = 100 },
            new ColliderComponent
            {
                Shape = ColliderShape.Cylinder,
                Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                IsSolid = false,
            });
        maps.IndexEntity("city", body);
        var session = new UserSession { ConnectionId = 1, Username = "listener", Entity = body, CurrentMapId = "city", Welcomed = true };
        sessions.AddSession(1, session);
        int self = body.Id;

        // The client, silent: FMOD's real-time WAV writer in place of the sound card, a master tap for the
        // capture's cadence. Nothing is prewarmed: the browser's thunder and the map's doors are start-up
        // work, and the lab keeps no render cache to make the doors cheap.
        var provider = new FmodAudioProvider { Hearing = LabHearing, Lab = new LabLevers() };
        string sounds = LabPaths.Sounds();
        var facade = new AudioEngineFacade(provider, paths: new AudioDataPaths(sounds, null, null));
        facade.InitializeSteppedByHand(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }

        var player = new LocalPlayerState { Position = feet };
        player.Rotation = Quaternion.Identity;
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var beacons = BeaconPreferences.InMemory();
        foreach (var category in Beacons.Categories) beacons.Set(category, false);

        var clock = Stopwatch.StartNew();
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds,
                                          prewarm: false, beacons: beacons);
        audio.WorldAudio.Sounds.SelfId = self;
        audio.WorldAudio.Sounds.Self = () => (player.Position, player.Rotation);
        var others = new OtherBodies { Clock = facade.Clock };
        others.OnStepTriggered += audio.OnPlayerFootstep;
        others.OnLandTriggered += audio.OnPlayerLand;
        var controller = new LocalPlayerController(player) { Clock = facade.Clock };
        controller.OnStepTriggered += audio.OnOwnFootstep;
        controller.OnLandTriggered += audio.OnOwnLand;

        // What the broadcast sends this one player, handed over at once, as off the socket.
        var inbox = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        void Deliver(UserSession to, IMessage message)
        {
            if (to != session) return;
            if (message is ServerStateUpdate update)
                foreach (var piece in NetworkService.Pieces(update, 1431))
                    inbox.Enqueue(MemoryPack.MemoryPackSerializer.Deserialize<IMessage>(MemoryPack.MemoryPackSerializer.Serialize<IMessage>(piece))!);
            else inbox.Enqueue(MemoryPack.MemoryPackSerializer.Deserialize<IMessage>(MemoryPack.MemoryPackSerializer.Serialize(message))!);
        }
        server.Sent = Deliver;
        server.Broadcasted = Deliver;

        string KindOf(int id) => audio.World.Voices.TryKeyOf(id, out var key) ? key.Kind.ToString() : id >= 0 ? "Entity" : "unregistered";

        var reports = new List<LoadReport>();
        long tick = 0;
        var occupancy = new OccupancySystem();
        void ServerTick()
        {
            tick++;
            MovementSystem.Update(ecs, data.WalkMin, data.WalkMax, grid, lookup, sessions, maps, PhysicsConstants.FixedDeltaTime);
            server.Vehicles.Update("city", ecs, PhysicsConstants.FixedDeltaTime);
            server.Rail.Update("city", ecs, PhysicsConstants.FixedDeltaTime);
            DrivingSystem.Update(ecs, grid, data.WalkMin, data.WalkMax, PhysicsConstants.FixedDeltaTime);
            ParentSystem.Update(ecs, lookup);
            occupancy.Update(ecs, lookup);
            server.BroadcastForTest(tick);
        }
        // The street already under way when the login arrives, as it is on the server.
        for (int i = 0; i < prerun / PhysicsConstants.FixedDeltaTime; i++) ServerTick();
        while (inbox.TryDequeue(out _)) { }

        Console.WriteLine($"city load: feet at ({feet.X:F2}, {feet.Y:F2}, {feet.Z:F2}), yaw 0, {seconds} s after each spawn, "
                        + $"repeat {repeat}; /tp {StandingSpots.Typed(feet)}");
        Console.WriteLine($"  spot: {spotNote}");

        for (int load = 1; load <= repeat; load++)
        {
            if (load > 1)
            {
                // The next load starts from the last one torn down, as /join does, not from its refuse.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            var report = new LoadReport { Load = load };
            reports.Add(report);
            // A fresh client world for each load: the map's own acoustic map is made again, and nothing of
            // the last load's rooms, woods or geometry is left to be cleared while this one arrives.
            var world = new ClientWorldState { Clock = facade.Clock, Hearing = facade.Hearing };
            world.Clear(size);
            var bake = new BakeState();
            int definitions = 0;
            double arrivalAt = clock.Elapsed.TotalSeconds;
            bool mapLoaded = false;

            long startedThisRun = 0;
            var startedKindsThisRun = new Dictionary<string, int>(StringComparer.Ordinal);
            provider.VoiceStartedForLab = (id, sound) =>
            {
                string kind = KindOf(id);
                // A voice started from the queues can outlive the registry entry the naming reads; its sound
                // id still says what kind of thing it is.
                if (kind is "Entity" or "unregistered") kind = SoundKind(sound);
                startedThisRun++;
                startedKindsThisRun[kind] = startedKindsThisRun.GetValueOrDefault(kind) + 1;
            };

            void Receive(IMessage message)
            {
                switch (message)
                {
                    case MapManifest manifest:
                        arrivalAt = clock.Elapsed.TotalSeconds;
                        world.Clear(manifest.WorldSize);
                        world.ConfigureAcoustics(manifest.MapMin, manifest.VoxelResolution, manifest.OcclusionFloor, manifest.TileMetres);
                        world.ApplyManifestAtmosphere(manifest);
                        player.MapMin = manifest.PlayMin;
                        player.MapMax = manifest.PlayMax;
                        break;

                    case EntityDefinition def:
                        world.RegisterDefinition(def);
                        definitions++;
                        break;

                    case EntityDefinitionBatch batch:
                        foreach (var d in batch.Definitions) world.RegisterDefinition(d);
                        definitions += batch.Definitions.Count;
                        break;

                    case EntityDefinitionPack pack:
                        if (pack.Unpack() is { } unpacked) Receive(unpacked);
                        break;

                    case MapRoads roads:
                        {
                            var roadsData = OpenFPS.Common.RoadMapData.FromJson(roads.Json);
                            audio.Driving.SetRoads(roadsData);
                            audio.SetCrossings(roadsData?.Crossings);
                        }
                        break;

                    case MapLoadComplete:
                        mapLoaded = true;
                        audio.NoteSceneLoading();
                        var bakeClock = Stopwatch.StartNew();
                        BackgroundPriority.RunLowered("AcousticBake", () =>
                        {
                            try
                            {
                                var snapshot = world.GetSnapshot();
                                var map = ClientWorldState.BuildAcousticMap(
                                    snapshot.Entities.Values.Select(e => e.Definition),
                                    size, data.MinBound, data.VoxelResolution, data.OcclusionFloor,
                                    streamed: false, report: true,
                                    geometry: TriangleGeometry.Enabled ? world.Geometry.Built : null);
                                world.SetAcousticMap(map);
                            }
                            catch (Exception ex)
                            {
                                bake.Error = ex.Message;
                            }
                            finally
                            {
                                bake.Ms = bakeClock.Elapsed.TotalMilliseconds;
                                bake.Done = true;
                            }
                        });
                        break;

                    case PlayerSpawned spawn:
                        audio.NoteSceneLoading();
                        self = spawn.EntityId;
                        audio.OwnEntityId = self;
                        audio.WorldAudio.Sounds.SelfId = self;
                        player.Position = spawn.SpawnTransform.Position;
                        player.Rotation = spawn.SpawnTransform.Rotation;
                        OpenFPS.Geometry.MathHelper.ToYawPitch(player.Rotation, out player.Yaw, out player.Pitch);
                        player.Velocity = Vector3.Zero;
                        controller.Teleported();
                        world.SyncState(new[] { new EntityState { EntityId = self, Transform = QuantizedTransform.FromTransform(spawn.SpawnTransform) } });
                        break;

                    case ServerStateUpdate update:
                        world.SyncState(update);
                        break;

                    case EntityRemoved removed:
                        foreach (int gone in world.RemoveEntities(removed.EntityIds))
                            audio.ForgetEntity(gone);
                        break;

                    case WorldAudioEvent heard:
                        audio.WorldAudio.Receive(heard, facade.Clock.Now);
                        break;

                    case WorldStateUpdate weather:
                        world.UpdateAtmosphere(weather);
                        facade.Weather.Wind = world.Wind;
                        facade.Weather.Ground.Load(weather.GroundWater);
                        facade.Weather.Layers = AirLayers.From(weather.GameTime,
                            weather.DayOfYear > 0 ? weather.DayOfYear : AirLayers.EquinoxDay,
                            RoadDrainageSpec.Default.LatitudeDegrees, weather.PrecipitationIntensity,
                            weather.Humidity, weather.Temperature, weather.AirPressure,
                            new Vector2(weather.WindVelocity.X, weather.WindVelocity.Z).Length(), weather.WindGustiness);
                        break;

                    case StatsUpdate stats:
                        player.CurrentMaterial = stats.CurrentMaterial;
                        player.CurrentVariant = stats.CurrentVariant;
                        audio.NotifyMaterialChange(stats.CurrentMaterial);
                        break;
                }
            }

            // The manifest, the whole map (statics), the roads and MapLoadComplete, then the dynamics and
            // the weather: the server's own join data, built exactly as SendMapData builds it.
            var manifest = new MapManifest
            {
                MapName = "city",
                WorldSize = size,
                MapMin = data.MinBound,
                MapMax = data.MaxBound,
                PlayMin = data.WalkMin,
                PlayMax = data.WalkMax,
                SpawnPoint = new Transform { Position = feet, Rotation = Quaternion.Identity },
                ExpectedEntityCount = 0,
                VoxelResolution = data.VoxelResolution,
                OcclusionFloor = data.OcclusionFloor,
                Temperature = data.Temperature,
                Humidity = data.Humidity,
                AirPressure = data.AirPressure,
                AirAbsorptionMultiplier = data.AirAbsorptionMultiplier,
                Gravity = data.Gravity,
                // No door renders: this lab has no render cache and the load under test is the voices'.
                DoorRenderKeys = Array.Empty<string>(),
            };
            server.SendToSession(session, manifest);
            server.SendMapData(session, new MapDataRequest { MapName = "city" });
            server.BroadcastEnvironment();
            server.BroadcastForTest(tick);

            long receiveAt = Stopwatch.GetTimestamp();
            while (!mapLoaded)
            {
                if (!inbox.TryDequeue(out var message))
                {
                    if (clock.Elapsed.TotalSeconds - arrivalAt > 120)
                    {
                        Console.WriteLine("FAIL: the map did not finish arriving in 120 s");
                        return 1;
                    }
                    Thread.Sleep(1);
                    continue;
                }
                Receive(message);
            }
            report.RegistrationMs = Stopwatch.GetElapsedTime(receiveAt).TotalMilliseconds;
            report.Definitions = definitions;

            while (!bake.Done)
            {
                while (inbox.TryDequeue(out var message)) Receive(message);
                Thread.Sleep(1);
            }
            report.BakeMs = bake.Ms;
            if (bake.Error != null) Console.WriteLine($"  acoustic bake failed: {bake.Error}");

            // The spawn the server sends once the client says it is ready, after the bake.
            Receive(new PlayerSpawned { EntityId = self, SpawnTransform = new Transform { Position = feet, Rotation = Quaternion.Identity } });
            double t0 = clock.Elapsed.TotalSeconds;
            double end = t0 + seconds;
            report.SpawnAt = t0 - arrivalAt;

            var parts = audio.World.LabPartLastMs;
            var stages = audio.World.LabStageLastMs;
            var row = new SecondRow(0, t0, stages.Length, parts.Length);
            double rowBoundary = t0 + 1;
            long lastStarted = 0;
            var lastKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            int lastGc = GC.CollectionCount(0), lastGen2 = GC.CollectionCount(2);
            double lastPause = GC.GetTotalPauseDuration().TotalMilliseconds;
            long lastLateBlocks = 0, lastFrames = 0;
            int seenGc = lastGc;
            double rowLongestGc = 0;
            double lastInterpolation = t0;
            var iterationParts = new double[LoopNames.Length];

            bool running = true;
            var serverThread = new Thread(() =>
            {
                double next = clock.Elapsed.TotalSeconds;
                while (Volatile.Read(ref running))
                {
                    ServerTick();
                    next += PhysicsConstants.FixedDeltaTime;
                    double wait = next - clock.Elapsed.TotalSeconds;
                    if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                    else if (wait < -PhysicsConstants.FixedDeltaTime) next = clock.Elapsed.TotalSeconds;
                }
            }) { IsBackground = true, Name = "server" };
            serverThread.Start();

            var tap = provider.MasterTapForLab;
            double captureRate = facade.MixerRate;
            if (tap != null)
            {
                var cadence = tap.LabCadence;
                lastLateBlocks = cadence.LateBlocks;
                lastFrames = cadence.Frames;
            }
            try
            {
                while (clock.Elapsed.TotalSeconds < end)
                {
                    double itStart = clock.Elapsed.TotalSeconds;
                    while (itStart >= rowBoundary)
                    {
                        CloseRow(row, clock.Elapsed.TotalSeconds, provider, tap, captureRate, ref lastStarted, startedThisRun,
                                 startedKindsThisRun, lastKinds, ref lastGc, ref lastGen2, ref lastPause,
                                 ref lastLateBlocks, ref lastFrames, ref rowLongestGc);
                        report.Seconds.Add(row);
                        row = new SecondRow(row.Second + 1, rowBoundary, stages.Length, parts.Length);
                        rowBoundary += 1;
                    }

                    long loopAt = Stopwatch.GetTimestamp();
                    long partAt2 = loopAt;
                    while (inbox.TryDequeue(out var message)) Receive(message);
                    iterationParts[LoopMessages] = Ms(partAt2);

                    partAt2 = Stopwatch.GetTimestamp();
                    double now = clock.Elapsed.TotalSeconds;
                    world.UpdateInterpolation((float)(now - lastInterpolation), self);
                    lastInterpolation = now;
                    iterationParts[LoopInterpolate] = Ms(partAt2);

                    partAt2 = Stopwatch.GetTimestamp();
                    var snapshot = world.GetSnapshot();
                    iterationParts[LoopSnapshot] = Ms(partAt2);

                    partAt2 = Stopwatch.GetTimestamp();
                    others.Update(snapshot, self);
                    iterationParts[LoopOthers] = Ms(partAt2);

                    partAt2 = Stopwatch.GetTimestamp();
                    controller.Update(player.Position, player.Velocity);
                    iterationParts[LoopPlayer] = Ms(partAt2);

                    partAt2 = Stopwatch.GetTimestamp();
                    int tickBefore = audio.World.LabTickCount;
                    audio.Update(snapshot);
                    iterationParts[LoopAudio] = Ms(partAt2);

                    partAt2 = Stopwatch.GetTimestamp();
                    facade.StepByHand();
                    iterationParts[LoopPump] = Ms(partAt2);

                    double loopMs = Ms(loopAt);
                    if (loopMs > row.WorstLoopMs)
                    {
                        row.WorstLoopMs = loopMs;
                        Array.Copy(iterationParts, row.WorstLoopParts, iterationParts.Length);
                        Array.Copy(others.LabPartLastMs, row.WorstLoopOtherParts, others.LabPartLastMs.Length);
                        row.OtherLandCount = others.LabLandCount;
                        row.OtherStepCount = others.LabStepCount;
                        row.OtherWorstLandMs = others.LabWorstLandMs;
                        row.OtherWorstStepMs = others.LabWorstStepMs;
                        row.AudioOtherStepsMs = audio.LabOtherStepsLastMs;
                        row.AudioOtherStepsCount = audio.LabOtherStepsLastCount;
                    }

                    if (audio.World.LabTickCount != tickBefore)
                    {
                        double tickMs = audio.World.LabLastTickMs;
                        row.TickSumMs += tickMs;
                        row.TickCount++;
                        row.WorstGapMs = Math.Max(row.WorstGapMs, audio.World.LabLastGapMs);
                        if (tickMs > row.WorstPassMs)
                        {
                            row.WorstPassMs = tickMs;
                            row.WorstEmittersMs = audio.World.LabStageLastMs[3];
                            row.WorstPartPassMs = audio.World.LabLastPartPassMs;
                            Array.Copy(audio.World.LabStageLastMs, row.PassStages, row.PassStages.Length);
                            Array.Copy(audio.World.LabPartLastMs, row.PassParts, row.PassParts.Length);
                        }
                    }

                    float mixer = provider.MixerLoad;
                    row.MixerMax = Math.Max(row.MixerMax, mixer);
                    row.MixerSum += mixer;
                    row.MixerSamples++;
                    if (mixer >= 1f) row.MixerOverSamples++;
                    if (tap != null)
                    {
                        var cadence = tap.LabCadence;
                        row.WorstBlockGapMs = Math.Max(row.WorstBlockGapMs, cadence.WorstGapMs);
                    }

                    int gc = GC.CollectionCount(0);
                    if (gc != seenGc)
                    {
                        seenGc = gc;
                        var info = GC.GetGCMemoryInfo();
                        foreach (var pause in info.PauseDurations)
                            rowLongestGc = Math.Max(rowLongestGc, pause.TotalMilliseconds);
                    }

                    Thread.Sleep(2);
                }
            }
            finally
            {
                Volatile.Write(ref running, false);
                serverThread.Join();
                CloseRow(row, clock.Elapsed.TotalSeconds, provider, tap, captureRate, ref lastStarted, startedThisRun,
                         startedKindsThisRun, lastKinds, ref lastGc, ref lastGen2, ref lastPause,
                         ref lastLateBlocks, ref lastFrames, ref rowLongestGc);
                report.Seconds.Add(row);
                provider.VoiceStartedForLab = null;
                while (inbox.TryDequeue(out _)) { }
                audio.LeaveWorld(world.GetSnapshot().Entities.Keys.ToList());
            }

            // Totals, for the report.
            foreach (var s in report.Seconds)
            {
                report.MixerMax = Math.Max(report.MixerMax, s.MixerMax);
                report.MinBinauralFree = Math.Min(report.MinBinauralFree, s.BinauralFree);
                report.MaxNoHrtf = Math.Max(report.MaxNoHrtf, s.NoHrtf);
                report.RefusedAtEnd = Math.Max(report.RefusedAtEnd, s.Refused);
                report.LateBlocks += s.LateBlocks;
                report.FrameGap += s.FrameGap;
                report.WorstBlockGapMs = Math.Max(report.WorstBlockGapMs, s.WorstBlockGapMs);
                report.GcCount += s.GcCount;
                report.GcGen2 += s.GcGen2;
                report.GcPauseMs += s.GcPauseMs;
                report.GcLongestMs = Math.Max(report.GcLongestMs, s.GcLongestMs);
            }
            report.StartedTotal = (int)startedThisRun;
            foreach (var (kind, count) in startedKindsThisRun) report.StartedKinds[kind] = count;
            if (report.MinBinauralFree == double.MaxValue) report.MinBinauralFree = 0;
        }

        WriteCsv(outDir, reports);
        string text = BuildReport(outDir, reports, feet, spotNote, args);
        File.WriteAllText(Path.Combine(outDir, "report.txt"), text);
        Console.Write(text);
        var starts = provider.StartCheck;
        Console.WriteLine($"recordings: {starts.Watched} started, {starts.Checked} judged by their play position, {starts.NeverStarted} never started");
        facade.Dispose();
        return 0;
    }

    private static void CloseRow(SecondRow row, double now, FmodAudioProvider provider, MasterTap? tap, double captureRate,
                                 ref long lastStarted, long startedTotal, Dictionary<string, int> startedKinds,
                                 Dictionary<string, int> lastKinds, ref int lastGc, ref int lastGen2,
                                 ref double lastPause, ref long lastLateBlocks, ref long lastFrames,
                                 ref double longestGc)
    {
        row.Started = (int)(startedTotal - lastStarted);
        lastStarted = startedTotal;
        var parts = new List<string>();
        foreach (var (kind, count) in startedKinds)
        {
            int had = lastKinds.GetValueOrDefault(kind);
            int delta = count - had;
            if (delta > 0) parts.Add($"{kind} {delta}");
        }
        row.StartedKinds = string.Join(";", parts);
        foreach (var (kind, count) in startedKinds) lastKinds[kind] = count;

        int gc = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        row.GcCount = gc - lastGc;
        row.GcGen2 = gen2 - lastGen2;
        lastGc = gc; lastGen2 = gen2;
        double pause = GC.GetTotalPauseDuration().TotalMilliseconds;
        row.GcPauseMs = pause - lastPause;
        lastPause = pause;
        row.GcLongestMs = longestGc;
        longestGc = 0;

        if (tap != null)
        {
            var cadence = tap.LabCadence;
            row.LateBlocks = cadence.LateBlocks - lastLateBlocks;
            lastLateBlocks = cadence.LateBlocks;
            long frames = cadence.Frames - lastFrames;
            lastFrames = cadence.Frames;
            double elapsed = Math.Max(0.05, now - row.StartedAt);
            row.FrameGap = Math.Max(0, (long)(elapsed * captureRate) - frames);
        }

        var census = provider.VoiceCensus();
        row.Active = census.Active;
        row.NoHrtf = census.NoHrtf;
        row.Refused = census.Refused;
        row.BinauralFree = census.BinauralFree;
    }

    private static void WriteCsv(string outDir, List<LoadReport> reports)
    {
        var sb = new StringBuilder();
        sb.Append("load,second,mixer_max,mixer_avg,mixer_over_samples,blocks_late,worst_block_gap_ms,capture_gap_frames,");
        sb.Append("worst_pass_ms,worst_gap_ms,worst_emitters_ms,worst_loop_ms,voices_started,started_kinds,");
        sb.Append("gc_count,gc_gen2,gc_pause_ms,gc_longest_ms,active,no_hrtf,refused,binaural_free");
        foreach (string name in AudioWorld.LabPartNames) sb.Append(',').Append(name.Replace(' ', '_').Replace('/', '_'));
        foreach (string name in LoopNames) sb.Append(",loop_").Append(name);
        sb.AppendLine();
        foreach (var load in reports)
            foreach (var s in load.Seconds)
            {
                sb.Append(load.Load).Append(',').Append(s.Second)
                  .Append(',').Append(N(s.MixerMax)).Append(',').Append(N(s.MixerSamples > 0 ? s.MixerSum / s.MixerSamples : 0))
                  .Append(',').Append(s.MixerOverSamples)
                  .Append(',').Append(s.LateBlocks).Append(',').Append(N(s.WorstBlockGapMs))
                  .Append(',').Append(s.FrameGap)
                  .Append(',').Append(N(s.WorstPassMs)).Append(',').Append(N(s.WorstGapMs)).Append(',').Append(N(s.WorstEmittersMs))
                  .Append(',').Append(N(s.WorstLoopMs))
                  .Append(',').Append(s.Started).Append(',').Append(Quote(s.StartedKinds))
                  .Append(',').Append(s.GcCount).Append(',').Append(s.GcGen2).Append(',').Append(N(s.GcPauseMs)).Append(',').Append(N(s.GcLongestMs))
                  .Append(',').Append(s.Active).Append(',').Append(s.NoHrtf).Append(',').Append(s.Refused).Append(',').Append(s.BinauralFree);
                foreach (double v in s.PassParts) sb.Append(',').Append(N(v));
                foreach (double v in s.WorstLoopParts) sb.Append(',').Append(N(v));
                sb.AppendLine();
            }
        File.WriteAllText(Path.Combine(outDir, "seconds.csv"), sb.ToString());
    }

    private static string BuildReport(string outDir, List<LoadReport> reports, Vector3 feet, string spotNote, string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine("city load: the city arriving at a login, through the client's audio world");
        sb.AppendLine($"  command: OpenFPS.AudioLab --city-load {string.Join(" ", args.Where(a => !a.Equals("--city-load")))}");
        sb.AppendLine($"  build {BuildKind()}; {ProcessorCount()} cores available to this process; spawn {spotNote}");
        sb.AppendLine($"  feet at ({feet.X:F2}, {feet.Y:F2}, {feet.Z:F2}); /tp {StandingSpots.Typed(feet)}");
        sb.AppendLine($"  out {outDir}; mix.wav is FMOD's writer, capture-float.wav the master tap");
        sb.AppendLine();
        foreach (var load in reports)
        {
            sb.AppendLine($"load {load.Load}: {load.Definitions} definitions, registration {load.RegistrationMs:F0} ms, "
                        + $"acoustic bake {load.BakeMs:F0} ms, spawn {load.SpawnAt:F2} s after the manifest arrived");
            sb.AppendLine($"  voices started {load.StartedTotal}: {Kinds(load.StartedKinds)}");
            sb.AppendLine($"  mixer at most {100 * load.MixerMax:F0} %, {load.LateBlocks} block(s) late (worst gap {load.WorstBlockGapMs:F0} ms), "
                        + $"capture {load.FrameGap} frame(s) short; position held up to {load.Seconds.Max(s => s.WorstGapMs):F0} ms; "
                        + $"binaural free at least {load.MinBinauralFree}, direct without HRTF at most {load.MaxNoHrtf}, refused {load.RefusedAtEnd}");
            sb.AppendLine($"  gc {load.GcCount} collection(s) ({load.GcGen2} gen2), paused {load.GcPauseMs:F0} ms, longest {load.GcLongestMs:F0} ms");
            sb.AppendLine();
            sb.AppendLine("  sec | mixer max | blocks late | worst pass | emitters | held | worst loop | voices | gc pauses");
            foreach (var s in load.Seconds)
            {
                if (s.Second >= 10 && s.Second % 5 != 0 && s.Second != load.Seconds.Count - 1) continue;
                sb.AppendLine($"  {s.Second,3} | {100 * s.MixerMax,8:F0} % | {s.LateBlocks,11} | {s.WorstPassMs,9:F0} ms | {s.WorstEmittersMs,7:F0} ms | {s.WorstGapMs,6:F0} ms | {s.WorstLoopMs,9:F0} ms | {s.Started,6} | {s.GcCount} / {s.GcPauseMs:F0} ms");
            }
            var steady = load.Seconds.Where(s => s.Second >= 10).ToList();
            if (steady.Count > 0)
            {
                sb.AppendLine($"  steady state (from 10 s): mixer at most {100 * steady.Max(s => s.MixerMax):F0} %, worst pass {steady.Max(s => s.WorstPassMs):F0} ms, "
                            + $"worst loop {steady.Max(s => s.WorstLoopMs):F0} ms, {steady.Sum(s => s.Started)} voice(s) started, {steady.Sum(s => s.GcCount)} collection(s)");
            }
            var worst = load.Seconds.OrderByDescending(s => s.WorstPassMs).FirstOrDefault();
            if (worst != null && worst.WorstPassMs > 0)
            {
                sb.AppendLine($"  worst placement pass ({worst.Second} s, {worst.WorstPassMs:F0} ms): " + Parts(worst.PassStages, worst.PassParts, worst.WorstEmittersMs, worst.WorstPassMs));
            }
            var worstLoop = load.Seconds.OrderByDescending(s => s.WorstLoopMs).FirstOrDefault();
            if (worstLoop != null && worstLoop.WorstLoopMs > 0)
            {
                double named = worstLoop.WorstLoopParts.Sum();
                sb.AppendLine($"  worst client loop iteration ({worstLoop.Second} s, {worstLoop.WorstLoopMs:F0} ms): "
                            + string.Join(", ", LoopNames.Select((n, i) => $"{n} {worstLoop.WorstLoopParts[i]:F0}"))
                            + $"; unnamed {worstLoop.WorstLoopMs - named:F1} ms");
                double namedOther = worstLoop.WorstLoopOtherParts.Sum();
                sb.AppendLine($"    others inside: "
                            + string.Join(", ", OtherBodies.LabPartNames.Select((n, i) => $"{n} {worstLoop.WorstLoopOtherParts[i]:F0}"))
                            + $"; unnamed {worstLoop.WorstLoopParts[LoopOthers] - namedOther:F1} ms");
                sb.AppendLine($"    others events: {worstLoop.OtherStepCount} step(s), worst {worstLoop.OtherWorstStepMs:F0} ms; "
                            + $"{worstLoop.OtherLandCount} land(s), worst {worstLoop.OtherWorstLandMs:F0} ms");
                sb.AppendLine($"    audio's other steps: {worstLoop.AudioOtherStepsCount} played in {worstLoop.AudioOtherStepsMs:F0} ms "
                            + $"of the {worstLoop.WorstLoopParts[LoopAudio]:F0} ms audio part");
            }
            var firstOthers = load.Seconds.Where(s => s.Second < 10).OrderByDescending(s => s.WorstLoopParts[LoopOthers]).FirstOrDefault();
            var steadyOthers = load.Seconds.Where(s => s.Second >= 10).OrderByDescending(s => s.WorstLoopParts[LoopOthers]).FirstOrDefault();
            if (firstOthers != null && steadyOthers != null)
            {
                sb.AppendLine($"  other bodies' worst pass: first ten seconds {firstOthers.WorstLoopParts[LoopOthers]:F0} ms "
                            + $"(second {firstOthers.Second}, loop {firstOthers.WorstLoopMs:F0} ms), steady state "
                            + $"{steadyOthers.WorstLoopParts[LoopOthers]:F0} ms (second {steadyOthers.Second}, loop {steadyOthers.WorstLoopMs:F0} ms)");
            }
            sb.AppendLine();
        }
        if (reports.Count >= 2)
        {
            sb.AppendLine("first-time cost against making-many-voices cost (repeat=2):");
            var a = reports[0];
            var b = reports[1];
            double FirstTotal(LoadReport r) => r.Seconds.Where(s => s.Second < 10).Sum(s => s.TickSumMs);
            int FirstPasses(LoadReport r) => r.Seconds.Where(s => s.Second < 10).Sum(s => s.TickCount);
            double FirstAvg(LoadReport r) => FirstPasses(r) > 0 ? FirstTotal(r) / FirstPasses(r) : 0;
            double FirstLoop(LoadReport r) => r.Seconds.Where(s => s.Second < 10).Select(s => s.WorstLoopMs).DefaultIfEmpty(0).Max();
            double FirstPass(LoadReport r) => r.Seconds.Where(s => s.Second < 10).Select(s => s.WorstPassMs).DefaultIfEmpty(0).Max();
            sb.AppendLine($"  worst placement pass: load 1 {FirstPass(a):F0} ms, load 2 {FirstPass(b):F0} ms; "
                        + $"first-time cost {FirstPass(a) - FirstPass(b):F0} ms, the rest {FirstPass(b):F0} ms is making the voices");
            sb.AppendLine($"  placement per pass in the first ten seconds: load 1 {FirstAvg(a):F1} ms over {FirstPasses(a)} pass(es), "
                        + $"load 2 {FirstAvg(b):F1} ms over {FirstPasses(b)}; the pass counts differ because the first load's stalls take wall time");
            sb.AppendLine($"  placement in the first ten seconds in all: load 1 {FirstTotal(a):F0} ms, load 2 {FirstTotal(b):F0} ms");
            sb.AppendLine($"  worst client loop iteration: load 1 {FirstLoop(a):F0} ms, load 2 {FirstLoop(b):F0} ms; "
                        + $"first-time cost {FirstLoop(a) - FirstLoop(b):F0} ms");
            sb.AppendLine();
        }
        sb.AppendLine("what the named parts say to move first: the largest part of the worst pass, and the largest part of the worst loop iteration, above.");
        return sb.ToString();
    }

    private static string Parts(double[] stages, double[] parts, double emittersMs, double passMs)
    {
        string[] stageNames = { "listener+map", "paths", "budget", "emitters", "mixer" };
        string stagesText = string.Join(", ", stageNames.Select((n, i) => $"{n} {stages[i]:F0}"));
        double named = parts.Sum();
        string partsText = string.Join(", ", AudioWorld.LabPartNames.Select((n, i) => $"{n} {parts[i]:F0}"));
        double unnamedEmitters = Math.Max(0, emittersMs - named);
        double unnamedPass = Math.Max(0, passMs - stages.Sum());
        return stagesText + "; inside emitters " + partsText
             + $"; unnamed in emitters {unnamedEmitters:F1} ms of {emittersMs:F0} ({(emittersMs > 0 ? 100 * unnamedEmitters / emittersMs : 0):F0} %), "
             + $"unnamed in the pass {unnamedPass:F1} ms of {passMs:F0} ({(passMs > 0 ? 100 * unnamedPass / passMs : 0):F0} %)";
    }

    private static string SoundKind(string sound)
    {
        if (sound.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) return "Engine";
        if (sound.Equals("engine-distant", StringComparison.OrdinalIgnoreCase)) return "Borrowed";
        if (sound.StartsWith("machine:", StringComparison.OrdinalIgnoreCase)) return "Machine";
        if (sound.StartsWith("train", StringComparison.OrdinalIgnoreCase)) return "Train";
        if (sound.Contains("voice chat", StringComparison.OrdinalIgnoreCase)) return "Talker";
        if (sound.StartsWith("bird", StringComparison.OrdinalIgnoreCase)) return "Bird";
        if (sound.StartsWith("synth:rain", StringComparison.OrdinalIgnoreCase)) return "Rain";
        if (sound.StartsWith("foliage", StringComparison.OrdinalIgnoreCase)) return "Tree";
        return "Entity";
    }

    private static string Kinds(Dictionary<string, int> kinds)
        => kinds.Count == 0 ? "none" : string.Join(", ", kinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static string N(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

    private static string BuildKind()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static int ProcessorCount() => System.Environment.ProcessorCount;
}
