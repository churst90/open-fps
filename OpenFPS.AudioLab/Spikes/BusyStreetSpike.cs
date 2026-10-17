using System.Globalization;
using System.Numerics;
using System.Text;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --busy-street [out=DIR] [at=x,z] [yaw=DEG] [sec=40] [warm=10] [rain=10] [every=1] [prerun=20] [rule=old] [all] [debug]: the city's street as the
/// server runs it (its traffic, walkers and trains, the horns its drivers sound), handed to a client's world as the
/// broadcast sends it, heard through the whole client (ClientAudioSystem, other bodies' steps, the rain, the
/// FmodAudioProvider and Steam Audio's binaural stages) in real time, by somebody standing where Cody stood on Main
/// Street on 2026-10-17 (game x 7, z 121). Each <c>every</c> seconds after <c>warm</c>: the binaural pool (free, reverb
/// buses and their heads), the mixer's load, and a census of who holds a stage and who was refused one, by kind
/// (VoiceRegistry's), with the loudest and quietest of each at the ear (dB under the loudest voice playing).
/// rule=old: the binaural pool first come, first served, as before 2026-10-17 (LabLevers.FirstComeStages).
/// Writes DIR/census.txt, the last census printed with the run's totals.
/// </summary>
public static class BusyStreetSpike
{
    private static readonly OpenFPS.Common.Hearing.HearingSettings LabHearing = OpenFPS.Common.Hearing.HearingSettings.FromEnvironment();

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

    /// <summary>One kind's tally in a census.</summary>
    private sealed class Tally
    {
        public int Staged, Panned, Refused, Unheard, Outranked;
        public float Loudest, Quietest = float.MaxValue, RefusedLoudest;
        public double Distance;
    }

    public static int Run(string[] args)
    {
        if (args.Contains("cost")) return Cost(args);
        if (args.Contains("handover")) return Handover(args);
        string outDir = Arg(args, "out") ?? "/tmp/openfps-busy-street";
        double seconds = double.Parse(Arg(args, "sec") ?? "40", CultureInfo.InvariantCulture);
        double warm = double.Parse(Arg(args, "warm") ?? "10", CultureInfo.InvariantCulture);
        double every = double.Parse(Arg(args, "every") ?? "1", CultureInfo.InvariantCulture);
        float rain = F(Arg(args, "rain") ?? "10");
        float yawDeg = F(Arg(args, "yaw") ?? "0");
        var spotXz = new Vector2(7f, 121f);
        if (Arg(args, "at") is { } at) { var p = at.Split(','); spotXz = new Vector2(F(p[0]), F(p[1])); }
        Directory.CreateDirectory(outDir);
        AcousticRegistry.Initialize();
        if (!args.Contains("debug")) Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();

        // The server: the city with its traffic, walkers and trains, as DistantUpdatesRig runs it.
        string baseDir = AppContext.BaseDirectory;
        var prefabs = new PrefabRepository(Path.Combine(baseDir, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(baseDir, "maps")), prefabs);
        maps.Initialize();
        if (!maps.TryGetMap("city", out var ecs, out var size, out var grid, out var lookup) || !maps.TryGetMapData("city", out var data))
        { Console.WriteLine("FAIL: no city map"); return 1; }

        // The client's world: the map's static things and the acoustic map the client makes of them, before
        // anything moves on it (PathProbeSpike.LoadAsClient).
        var statics = EntityDefinitionFactory.StaticDefinitions(ecs);
        var world = new ClientWorldState();
        world.Clear(size);
        foreach (var def in statics) world.RegisterDefinition(def, deferAcoustics: true);
        world.SetAcousticMap(EntityAcoustics.GenerateRegions(statics, size, data.MinBound, data.VoxelResolution, data.OcclusionFloor));
        world.UpdateAtmosphere(new WorldStateUpdate
        {
            Temperature = 15f, Humidity = 0.8f, AirPressure = 1013.25f, AirAbsorptionMultiplier = 1f,
            PrecipitationIntensity = rain > 0f ? 0.5f : 0f, RainRateMmPerHour = rain,
        });

        var composites = new CompositeService(maps, prefabs,
            new CompositeRepository(Path.Combine(Path.GetTempPath(), "openfps-no-composites-" + Guid.NewGuid())));
        composites.PlaceRecorded(maps);
        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions, new OccupancyService(maps), new HandsService(maps));
        server.Vehicles.Spawn(maps, composites);
        server.Rail.Spawn(maps);
        server.Vehicles.Heard = (map, id, label, sounds) => server.EmitWorldAudio(map, id, label, sounds);
        server.Rail.Heard = (map, id, label, sounds) => server.EmitWorldAudio(map, id, label, sounds);
        maps.RefreshEarshotRanges();

        // Standing on the street: the ground under the spot, as /tp puts a body there.
        var feet = StandingSpots.In(ecs, grid, new Vector3(spotXz.X, 1.4f, spotXz.Y), Quaternion.Identity, new Vector3(1.2f, 3f, 1.2f))
                   ?? new Vector3(spotXz.X, 0.1f, spotXz.Y);
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

        // The client, silent: FMOD's real-time file writer in place of the sound card.
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "mix.wav"));
        // rule=old: the pool first come, first served, as before 2026-10-17.
        var provider = new FmodAudioProvider { Hearing = LabHearing, Lab = new LabLevers { FirstComeStages = Arg(args, "rule") == "old" } };
        string sounds = LabPaths.Sounds();
        var facade = new AudioEngineFacade(provider, paths: new AudioDataPaths(sounds, null, null));
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        var player = new LocalPlayerState { Position = feet, Yaw = yawDeg * MathF.PI / 180f };
        player.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, player.Yaw);
        var mapping = new SoundMappingService(player);
        mapping.Initialize(sounds);
        var beacons = BeaconPreferences.InMemory();
        foreach (var category in Beacons.Categories) beacons.Set(category, false);
        world.Clock = facade.Clock; world.Hearing = facade.Hearing;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var audio = new ClientAudioSystem(facade, mapping, player, () => clock.Elapsed.TotalSeconds, beacons: beacons);
        audio.WorldAudio.Sounds.SelfId = self;
        audio.WorldAudio.Sounds.Self = () => (player.Position, player.Rotation);
        var others = new OtherBodies { Clock = facade.Clock };
        others.OnStepTriggered += audio.OnPlayerFootstep;
        long steps = 0;
        others.OnStepTriggered += (_, _, _, _, _) => steps++;
        others.OnLandTriggered += audio.OnPlayerLand;

        // What the broadcast sends this one player, handed over at once.
        // The server runs on a thread of its own, as on the server's machine; what it sends waits in a queue for the
        // client's frame, as off the socket.
        var inbox = new System.Collections.Concurrent.ConcurrentQueue<IMessage>();
        void Deliver(UserSession to, IMessage message)
        {
            if (to != session) return;
            if (message is ServerStateUpdate update)
                foreach (var piece in NetworkService.Pieces(update, 1431))
                    inbox.Enqueue(MemoryPack.MemoryPackSerializer.Deserialize<IMessage>(MemoryPack.MemoryPackSerializer.Serialize<IMessage>(piece))!);
            else inbox.Enqueue(MemoryPack.MemoryPackSerializer.Deserialize<IMessage>(MemoryPack.MemoryPackSerializer.Serialize(message))!);
        }
        void Receive(IMessage message)
        {
            switch (message)
            {
                case ServerStateUpdate update: world.SyncState(update); break;
                case EntityDefinition def: world.RegisterDefinition(def); break;
                case EntityRemoved gone: world.RemoveEntities(gone.EntityIds); break;
                case WorldAudioEvent heard: audio.WorldAudio.Receive(heard, facade.Clock.Now); break;
            }
        }
        server.Broadcasted = Deliver;
        server.Sent = Deliver;

        // Who was refused a stage since the last census, by kind.
        var refused = new Dictionary<string, Tally>();
        string KindOf(int id) => audio.World.Voices.TryKeyOf(id, out var key) ? key.Kind.ToString() : id >= 0 ? "Entity" : "unregistered";
        long refusedAll = 0, shortAll = 0;
        provider.RefusedForLab = (e, heard, why) =>
        {
            refusedAll++;
            if (why == BinauralStages.Answer.Short) shortAll++;
            string k = KindOf(e.EntityId);
            if (!refused.TryGetValue(k, out var t)) refused[k] = t = new Tally();
            switch (why)
            {
                case BinauralStages.Answer.Unheard: t.Unheard++; break;
                case BinauralStages.Answer.Outranked: t.Outranked++; break;
                default: t.Refused++; t.RefusedLoudest = MathF.Max(t.RefusedLoudest, heard); break;
            }
        };

        var report = new StringBuilder();
        void Say(string line) { Console.WriteLine(line); report.AppendLine(line); }
        Say($"busy street: feet at ({feet.X:F2}, {feet.Y:F2}, {feet.Z:F2}), yaw {yawDeg}, rain {rain} mm/h, {seconds} s after {warm} s warming; "
          + $"/tp {StandingSpots.Typed(feet)}");

        float dt = PhysicsConstants.FixedDeltaTime;
        long tick = 0;
        var occupancy = new OccupancySystem();
        void ServerTick()
        {
            tick++;
            MovementSystem.Update(ecs, data.WalkMin, data.WalkMax, grid, lookup, sessions, maps, dt);
            server.Vehicles.Update("city", ecs, dt);
            server.Rail.Update("city", ecs, dt);
            DrivingSystem.Update(ecs, grid, data.WalkMin, data.WalkMax, dt);
            ParentSystem.Update(ecs, lookup);
            occupancy.Update(ecs, lookup);
            server.BroadcastForTest(tick);
        }
        // The street already under way: walkers part way along their walks, the traffic spread out.
        double prerun = double.Parse(Arg(args, "prerun") ?? "20", CultureInfo.InvariantCulture);
        for (int i = 0; i < prerun / dt; i++) ServerTick();
        while (inbox.TryDequeue(out var early)) Receive(early);
        // The first updates build the scenes and the worker's world (seconds): the street starts after them.
        for (int i = 0; i < 3; i++) { audio.Update(world.GetSnapshot()); facade.PumpForTest(); }
        double start = clock.Elapsed.TotalSeconds;
        double nextCensus = start + warm, end = start + warm + seconds;
        warm += start;
        int worstFree = int.MaxValue, censuses = 0;
        double loadSum = 0, loadMax = 0, refusedAudible = 0;
        long refusedAtWarm = -1;
        bool running = true;
        var serverThread = new Thread(() =>
        {
            double next = clock.Elapsed.TotalSeconds;
            while (Volatile.Read(ref running))
            {
                ServerTick();
                next += dt;
                double wait = next - clock.Elapsed.TotalSeconds;
                if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
                else if (wait < -dt) next = clock.Elapsed.TotalSeconds;
            }
        }) { IsBackground = true, Name = "server" };
        serverThread.Start();
        double lastInterpolation = clock.Elapsed.TotalSeconds;
        try
        {
            while (clock.Elapsed.TotalSeconds < end)
            {
                double now = clock.Elapsed.TotalSeconds;
                while (inbox.TryDequeue(out var message)) Receive(message);
                world.UpdateInterpolation((float)(now - lastInterpolation), self);
                lastInterpolation = now;
                var snapshot = world.GetSnapshot();
                others.Update(snapshot, self);
                audio.Update(snapshot);
                facade.PumpForTest();

                if (now >= nextCensus)
                {
                    if (refusedAtWarm < 0) refusedAtWarm = shortAll;
                    nextCensus += every;
                    censuses++;
                    var census = Census(provider, KindOf, refused, out int free, out float load, out string headline, out int audibleRefused);
                    worstFree = Math.Min(worstFree, free);
                    loadSum += load; loadMax = Math.Max(loadMax, load);
                    refusedAudible += audibleRefused;
                    Console.WriteLine($"{now - warm,5:F0} s  {headline}");
                    if (censuses == 1)
                    {
                        int npcs = 0, moving = 0, walkers = 0, near = 0;
                        foreach (var b in snapshot.DynamicEntities)
                        {
                            if (b.Definition.Type != EntityType.NPC) continue;
                            npcs++;
                            if (b.Velocity.Length() > 0.3f) moving++;
                            if (ModelKindTable.BodyOf(b.Definition.SoundEmitter.SoundId) is not { } k || k == ModelBody.Walks)
                            {
                                walkers++;
                                if (Vector3.Distance(b.Transform.Position, feet) < 200f) near++;
                            }
                        }
                        Console.WriteLine($"      the client sees {npcs} NPCs, {moving} moving, {walkers} with legs ({near} within 200 m); {others.Count} bodies listened to; {steps} steps so far; {tick} server ticks in {now - start:F1} s");
                    }
                    if (clock.Elapsed.TotalSeconds + every >= end || args.Contains("all")) { report.AppendLine($"at {now - warm:F0} s: {headline}"); report.Append(census); if (args.Contains("all") || clock.Elapsed.TotalSeconds + every >= end) Console.Write(census); }
                    refused.Clear();
                }
                Thread.Sleep(2);
            }
        }
        finally { Volatile.Write(ref running, false); serverThread.Join(); facade.Dispose(); }
        Say($"totals: {censuses} censuses; binaural free at least {worstFree}; mixer {100 * loadSum / Math.Max(1, censuses):F0} % on average, "
          + $"{100 * loadMax:F0} % at most; {shortAll - Math.Max(0, refusedAtWarm)} refused short of a stage after warming, "
          + $"{refusedAudible} of them within 30 dB of the loudest voice playing");
        File.WriteAllText(Path.Combine(outDir, "census.txt"), report.ToString());
        return 0;
    }

    /// <summary>
    /// --busy-street cost [stages=160] [blocks=2000]: what one binaural stage costs the mixer, as the provider builds
    /// them (Steam Audio's binaural effect at 48 kHz and 1024 samples, bilinear, and the ground image's second effect),
    /// each stage a moving direction, one thread, timed over every stage's block in turn: microseconds a block, and the
    /// share of the block's real time.
    /// </summary>
    private static int Cost(string[] args)
    {
        int stages = int.Parse(Arg(args, "stages") ?? "160", CultureInfo.InvariantCulture);
        int blocks = int.Parse(Arg(args, "blocks") ?? "2000", CultureInfo.InvariantCulture);
        const int Frame = 1024, Rate = 48000;
        var cs = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.DefaultContextSettings();
        if (OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.iplContextCreate(ref cs, out IntPtr ctx) != 0) { Console.WriteLine("FAIL: no Steam Audio"); return 1; }
        var au = new OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLAudioSettings { samplingRate = Rate, frameSize = Frame };
        var hs = new OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLHRTFSettings { type = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPL_HRTFNORMTYPE_NONE };
        OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var es = new OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        var effects = new IntPtr[stages * 2];
        for (int i = 0; i < effects.Length; i++) OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out effects[i]);
        var inB = new OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLAudioBuffer(); var outB = new OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLAudioBuffer();
        OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.iplAudioBufferAllocate(ctx, 1, Frame, ref inB);
        OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.iplAudioBufferAllocate(ctx, 2, Frame, ref outB);
        var mono = new float[Frame]; var rng = new Random(1);
        for (int i = 0; i < Frame; i++) mono[i] = (float)(rng.NextDouble() * 2 - 1) * 0.1f;
        OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inB);
        var watch = new System.Diagnostics.Stopwatch();
        void Apply(IntPtr effect, float angle)
        {
            var prm = new OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLBinauralEffectParams
            {
                direction = new OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3 { x = MathF.Sin(angle), y = -0.1f, z = -MathF.Cos(angle) },
                interpolation = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = hrtf, peakDelays = IntPtr.Zero,
            };
            OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.iplBinauralEffectApply(effect, ref prm, ref inB, ref outB);
        }
        for (int b = 0; b < 50; b++) for (int i = 0; i < effects.Length; i++) Apply(effects[i], b * 0.01f + i);
        foreach (bool ground in new[] { false, true })
        {
            watch.Restart();
            for (int b = 0; b < blocks; b++)
                for (int i = 0; i < stages; i++)
                {
                    Apply(effects[2 * i], b * 0.01f + i);
                    if (ground) Apply(effects[2 * i + 1], -b * 0.01f + i);
                }
            double us = watch.Elapsed.TotalMilliseconds * 1000.0 / (blocks * (double)stages);
            double block = Frame * 1e6 / Rate;
            Console.WriteLine($"  a stage {(ground ? "with its ground image" : "alone")}: {us:F1} us a block of {Frame}, {100 * us / block:F3} % of the block's {block:F0} us; "
                            + $"{stages} stages {100 * us * stages / block:F1} % of one core");
        }
        return 0;
    }

    /// <summary>
    /// --busy-street handover [out=DIR]: whether a voice let go of its binaural stage leaves click-free, through the
    /// whole mixer. A 440 Hz tone 2 m off holds a stage for 1.5 s, then the pool is held down to 10 free (under its
    /// headroom), so the rank lets the tone go: faded and stopped. For comparison a second tone is stopped outright, as
    /// nothing in the rule does. Each end is measured in the master capture: how long the fall took (-1 to -60 dB under
    /// its level) and the largest second difference (a step's mark) within 5 ms of the end against the tone's own
    /// before it. A click stands tens of decibels over the tone's own; a fade adds nothing.
    /// </summary>
    private static int Handover(string[] args)
    {
        string outDir = Arg(args, "out") ?? "/tmp/openfps-handover";
        Directory.CreateDirectory(outDir);
        if (!args.Contains("debug")) Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string wav = Path.Combine(outDir, "capture-float.wav");
        Environment.SetEnvironmentVariable("OPENFPS_FMOD_WAV", Path.Combine(outDir, "mix.wav"));
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", wav);
        Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_FLOAT", "1");
        Environment.SetEnvironmentVariable("OPENFPS_DITHER", "0");
        AcousticRegistry.Initialize();
        var provider = new FmodAudioProvider { Hearing = LabHearing };
        string sounds = LabPaths.Sounds();
        var facade = new AudioEngineFacade(provider, paths: new AudioDataPaths(sounds, null, null));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        facade.InitializeForTest(sounds);
        if (!facade.IsInitialized) { Console.WriteLine("FAIL: provider init failed"); return 1; }
        double tapStart = clock.Elapsed.TotalSeconds;
        const int Rate = 48000;
        var tone = new float[Rate * 2];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.3f * MathF.Sin(2f * MathF.PI * 440f * i / Rate);
        facade.RegisterSynthesisedSoundFloat("lab:tone", tone, Rate);
        var ear = new Vector3(0f, 1.6f, 0f);
        facade.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
        SpatialEmitter Tone(int id) => new()
        {
            EntityId = id, SoundId = "lab:tone", Type = EmitterType.WorldLocked, Mode = PlaybackMode.LoopOne,
            Position = ear + new Vector3(1.4f, 0f, 1.4f), Volume = 0.5f, MinDistance = 1f, Range = 200f, Pitch = 1f,
            EqLow = 1f, EqMid = 1f, EqHigh = 1f, ApertureFactor = 1f,
        };
        void Pump(double seconds)
        {
            double until = clock.Elapsed.TotalSeconds + seconds;
            while (clock.Elapsed.TotalSeconds < until) { facade.PumpForTest(); Thread.Sleep(2); }
        }
        double letGoAt, stoppedAt;
        try
        {
            Pump(0.5);
            facade.PlayPhysicalSoundDirect(Tone(7001));
            Pump(1.5);
            letGoAt = clock.Elapsed.TotalSeconds - tapStart;
            provider.HoldBinauralVoicesForLab(10);
            Pump(1.0);
            Console.WriteLine($"  the first tone {(provider.IsPlaying(7001) ? "still plays: FAIL" : "was let go")}");
            provider.ReleaseHeldBinauralVoicesForLab();
            facade.PlayPhysicalSoundDirect(Tone(7002));
            Pump(1.5);
            stoppedAt = clock.Elapsed.TotalSeconds - tapStart;
            provider.StopSound(7002);
            Pump(1.0);
        }
        finally { facade.Dispose(); }
        var (l, r, sr) = OpenFPS.Client.Core.AudioEngine.Fmod.ClapRoomSpike.ReadStereo(wav);
        Console.WriteLine("  end              | fall -1 to -60 dB | largest second difference within 5 ms of the end, dB over the tone's own");
        foreach (var (name, at) in new[] { ("let go (rule)", letGoAt), ("stopped outright", stoppedAt) })
        {
            int from = Math.Max(2, (int)((at - 0.4) * sr)), to = Math.Min(l.Length, (int)((at + 0.8) * sr));
            double level = 0, own = 0;
            int n0 = (int)(0.1 * sr);
            for (int i = from; i < from + n0; i++)
            {
                level = Math.Max(level, Math.Abs(l[i]) + Math.Abs(r[i]));
                own = Math.Max(own, Math.Abs(l[i] - 2 * l[i - 1] + l[i - 2]) + Math.Abs(r[i] - 2 * r[i - 1] + r[i - 2]));
            }
            if (level < 1e-6) { Console.WriteLine($"  {name,-16} | no tone found"); continue; }
            int w = sr / 1000, fallFrom = -1, fallTo = -1;
            for (int i = from; i + w < to; i += w)
            {
                double pk = 0; for (int j = i; j < i + w; j++) pk = Math.Max(pk, Math.Abs(l[j]) + Math.Abs(r[j]));
                if (fallFrom < 0 && pk < level * 0.891) fallFrom = i;
                if (fallFrom >= 0 && pk < level * 0.001) { fallTo = i; break; }
            }
            if (fallTo < 0) { Console.WriteLine($"  {name,-16} | never fell to -60 dB"); continue; }
            double worst = 0;
            for (int i = Math.Max(2, fallTo - 5 * w); i < Math.Min(l.Length, fallTo + 5 * w); i++)
                worst = Math.Max(worst, Math.Abs(l[i] - 2 * l[i - 1] + l[i - 2]) + Math.Abs(r[i] - 2 * r[i - 1] + r[i - 2]));
            Console.WriteLine($"  {name,-16} | {(fallTo - fallFrom) * 1000.0 / sr,8:F0} ms         | {20 * Math.Log10(worst / own + 1e-12),6:F1}");
        }
        return 0;
    }

    /// <summary>Who holds a binaural stage now and who was refused one since the last census, by kind.</summary>
    private static string Census(FmodAudioProvider provider, Func<int, string> kindOf, Dictionary<string, Tally> refused,
                                 out int free, out float load, out string headline, out int audibleRefused)
    {
        var holders = provider.StagesForLab();
        free = provider.SpatialVoicesFree;
        load = provider.MixerLoad;
        var (buses, heads) = provider.ReverbHeadsForLab;
        float loudest = 1e-12f;
        foreach (var h in holders) if (!h.Reflection) loudest = MathF.Max(loudest, h.Heard);
        var kinds = new SortedDictionary<string, Tally>(StringComparer.Ordinal);
        int staged = 0, panned = 0;
        foreach (var h in holders)
        {
            string k = kindOf(h.Id) + (h.Reflection ? " (copy)" : "");
            if (!kinds.TryGetValue(k, out var t)) kinds[k] = t = new Tally();
            if (h.Stage) { t.Staged++; staged++; } else { t.Panned++; panned++; }
            t.Loudest = MathF.Max(t.Loudest, h.Heard);
            t.Quietest = MathF.Min(t.Quietest, h.Heard);
            t.Distance += h.Distance;
        }
        audibleRefused = 0;
        foreach (var (k, r) in refused)
        {
            if (!kinds.TryGetValue(k, out var t)) kinds[k] = t = new Tally();
            t.Refused = r.Refused; t.Unheard = r.Unheard; t.Outranked = r.Outranked;
            t.RefusedLoudest = r.RefusedLoudest;
            if (r.RefusedLoudest > loudest * 0.0316f) audibleRefused += r.Refused;
        }
        static string Db(float level, float loudest) => level <= 0f || level == float.MaxValue ? "   -" : $"{20f * MathF.Log10(MathF.Max(level, 1e-12f) / loudest),5:F0}";
        var sb = new StringBuilder();
        sb.AppendLine("    kind                  staged  panned  short unheard outranked | loudest quietest short-loudest dB under the loudest | mean m");
        foreach (var (k, t) in kinds)
        {
            int n = t.Staged + t.Panned;
            sb.AppendLine($"    {k,-22} {t.Staged,6} {t.Panned,7} {t.Refused,6} {t.Unheard,7} {t.Outranked,9} | {Db(t.Loudest, loudest),7} {Db(t.Quietest, loudest),8} {Db(t.RefusedLoudest, loudest),13} | {(n > 0 ? t.Distance / n : 0),6:F0}");
        }
        int refusedNow = 0, unheard = 0, outranked = 0;
        foreach (var r in refused.Values) { refusedNow += r.Refused; unheard += r.Unheard; outranked += r.Outranked; }
        headline = $"{holders.Count} playing, {staged} with a stage, {panned} without, {refusedNow} refused short ({audibleRefused} within 30 dB of the loudest), {unheard} unheard, {outranked} outranked; "
                 + $"{free} free; {heads} of {buses} reverb buses with a head; mixer {100 * load:F0} %";
        return sb.ToString();
    }
}
