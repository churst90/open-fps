using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Instruments;

/// <summary>
/// The acceptance test for sending far things less often (docs/CODY_ASKS_2026-10-08.md section 4), shared by
/// AudioLab (--distant-updates) and OpenFPS.Tests (DistantUpdatesTests).
///
/// <para>The city runs on the real server: its traffic, walkers and trains, plus a car shuttling past at
/// 108 km/h and an airliner flying over, both spawned as /spawn would. Two players stand side by side 40 m
/// from the railway: one is sent everything every tick, the other far things less often. Each one's states go
/// through the wire as they would (packed, split into packets, read back) and arrive 30 to 90 ms late, the
/// same lateness for both; each feeds a client's interpolation at the 30 Hz step, and between steps the
/// position is carried on its velocity at the mixer's 250 Hz, as the mixer does (FmodAudioProvider.DeadReckon).
/// What the two clients would hand the audio is compared at every one of those instants, from the second
/// player's ears: the bearing, the pitch (Doppler, and the engine's road speed), and how much each changes
/// from one instant to the next, which is where a step would show.</para>
///
/// <para>Two more players at the city's spawn point (one each way) measure the bytes on another street.</para>
/// </summary>
public sealed class DistantUpdatesRig
{
    /// <summary>Where the comparing players stand: 40 m south of the railway's straight on Harbour Street, by
    /// the 228 m mark of the loop. Light rail 1 sets off 213 m away and passes; a stop waits 170 m on.</summary>
    public static readonly Vector3 Listener = new(-35f, 0.15f, 412f);

    /// <summary>The ear, above the feet.</summary>
    private static readonly Vector3 Ear = new(0f, 1.6f, 0f);

    /// <summary>The mixer's attribute rate and its dead-reckoning cap (FmodAudioProvider).</summary>
    private const double MixerStep = 0.004, MaxDeadReckon = 0.08;

    /// <summary>A packet's largest size: LiteNetLib's largest MTU less its header.</summary>
    private const int MaxPacket = 1431;

    /// <summary>What each datagram costs besides its payload: IPv4 and UDP headers and LiteNetLib's byte.</summary>
    private const int DatagramOverhead = 28 + 1;

    public sealed class Scene
    {
        public string Name = "";
        public HashSet<int> Entities = new();
        public long Instants, FarInstants;
        public double WorstBearing, WorstPitch, WorstDoppler, WorstSpeed, WorstPosition;
        public string WorstBearingAt = "", WorstPitchAt = "";
        /// <summary>The largest change from one mixer instant to the next while far (at least 150 m), each
        /// stream: position (m), bearing (degrees), pitch (fraction).</summary>
        public double FullStepPosition, FullStepBearing, FullStepPitch;
        public double LessStepPosition, LessStepBearing, LessStepPitch;
        /// <summary>The largest difference between the two streams' changes over one instant, far.</summary>
        public double StepDifferencePosition, StepDifferenceBearing, StepDifferencePitch;
        public string StepAt = "", FullStepAt = "", LessStepAt = "";
        /// <summary>States sent to each player while far.</summary>
        public long FullStates, LessStates;
        /// <summary>Each stream against the truth: the server's own track at the playback time, carried by the
        /// mixer's step as the streams are, worst bearing (degrees) and pitch (fraction).</summary>
        public double TruthBearingFull, TruthBearingLess, TruthPitchFull, TruthPitchLess;
        public string TruthPitchLessAt = "";
        /// <summary>Train notch (the lever the client works out from the speed's change): steps where the two differ.</summary>
        public long LeverSteps, LeverDiffer;
        /// <summary>The longest the two notches stayed apart, client steps: one is a notch changing a step
        /// earlier or later, as the speed's change crosses a threshold.</summary>
        public int LeverRun;
    }

    public sealed class Bandwidth
    {
        public string Where = "";
        public long FullBytes, LessBytes, FullDatagrams, LessDatagrams, FullStates, LessStates;
        public double Seconds;
        public double FullMbit => (FullBytes + FullDatagrams * (double)DatagramOverhead) * 8 / Seconds / 1e6;
        public double LessMbit => (LessBytes + LessDatagrams * (double)DatagramOverhead) * 8 / Seconds / 1e6;
    }

    /// <summary>
    /// The network between server and both clients, the same for each: every tick's packets are late by
    /// <see cref="BaseSeconds"/> plus up to <see cref="SpreadSeconds"/>, and a fraction of ticks never
    /// arrive. A spread over a tick (33 ms) reorders ticks.
    /// </summary>
    public sealed record Network(string Name, double BaseSeconds, double SpreadSeconds, double Loss, int Seed = 0)
    {
        /// <summary>A home connection: 40 ms and up to 20 more, nothing lost, nothing out of order.</summary>
        public static readonly Network Typical = new("typical", 0.04, 0.02, 0);
        /// <summary>A poor one: 30 to 90 ms, so one tick in ten overtakes the one before, and 2 % lost.</summary>
        public static readonly Network Poor = new("poor", 0.03, 0.06, 0.02);
    }

    public Network Net = Network.Typical;
    public readonly Dictionary<string, Scene> Scenes = new();
    /// <summary>An entity to trace at every client step (the instrument's trace=ID), the pass-by (-2), the
    /// fly-over (-3), or none (-1).</summary>
    public int TraceId = -1;
    public readonly List<string> Trace = new();
    public readonly List<Bandwidth> Bytes = new();
    public int PassById, AircraftId;

    private sealed class Player
    {
        public UserSession Session = null!;
        public ClientWorldState Client = new();
        public readonly List<(double ArrivesAt, ServerStateUpdate Update)> InFlight = new();
        public long Bytes, Datagrams, States;
        public readonly Dictionary<int, long> StatesById = new();
    }

    private sealed class Last
    {
        public bool Have;
        public Vector3 FullPos, LessPos;
        public double FullBearing, LessBearing, FullPitch, LessPitch;
        public float FullSpeed, LessSpeed;
        public double FullSpeedAt;
        public int LeverRun;
    }

    /// <summary>Runs the city for <paramref name="seconds"/> and compares. Bytes are counted from
    /// <paramref name="warmup"/> on, after everything has been introduced.</summary>
    public static DistantUpdatesRig Run(double seconds = 45, double warmup = 5, Action<string>? log = null, int trace = -1,
                                        Network? network = null)
    {
        var rig = new DistantUpdatesRig { TraceId = trace, Net = network ?? Network.Typical };
        rig.Go(seconds, warmup, log ?? (_ => { }));
        return rig;
    }

    private void Go(double seconds, double warmup, Action<string> log)
    {
        string baseDir = AppContext.BaseDirectory;
        var prefabs = new PrefabRepository(Path.Combine(baseDir, "prefabs"));
        var maps = new MapManager(new MapRepository(Path.Combine(baseDir, "maps")), prefabs);
        maps.Initialize();
        var composites = new CompositeService(maps, prefabs,
            new CompositeRepository(Path.Combine(Path.GetTempPath(), "openfps-no-composites-" + Guid.NewGuid())));
        composites.PlaceRecorded(maps);
        if (!maps.TryGetMap("city", out var world, out _, out var grid, out var lookup) || !maps.TryGetMapData("city", out var data))
            throw new InvalidOperationException("no city map");

        var sessions = new SessionManager();
        var server = new GameServer(new NoUsers());
        server.Attach(maps, sessions, new OccupancyService(maps), new HandsService(maps));
        server.Vehicles.Spawn(maps, composites);
        server.Rail.Spawn(maps);

        // The pass-by: a car shuttling 450 m either side of the players, 20 m off, at 108 km/h.
        var road = Listener + new Vector3(0f, 0f, -20f);
        PassById = server.Vehicles.SpawnOne(maps, composites, "city", new VehicleData
        {
            Name = "Pass-by", Preset = "v6", RoadStart = road + new Vector3(-450f, 0f, 0f), RoadEnd = road + new Vector3(450f, 0f, 0f),
            SpeedsKmh = new[] { 108f }, AccelerationMps2 = 3.2f, BrakingMps2 = 6f, WaitSeconds = 1f,
        }).Id;
        // The fly-over: an airliner at 120 m, 60 m to the side, across the whole map at 100 m/s.
        var air = new Vector3(Listener.X, 120f, Listener.Z - 60f);
        AircraftId = server.Vehicles.SpawnOne(maps, composites, "city", new VehicleData
        {
            Name = "Fly-over", Preset = "airliner", RoadStart = new Vector3(-890f, air.Y, air.Z), RoadEnd = new Vector3(890f, air.Y, air.Z),
            SpeedsKmh = new[] { 360f }, AccelerationMps2 = 20f, BrakingMps2 = 20f, WaitSeconds = 1f,
        }).Id;

        // Every emitter is on the map now: the broadcast radius from the loudest, as the server's start does.
        maps.RefreshEarshotRanges();

        // Each pair stands on one spot (the bodies are not solid), so the two are told of the same things.
        var full = Join(world, maps, sessions, 1, "full", Listener, lessOften: false);
        var less = Join(world, maps, sessions, 2, "less", Listener, lessOften: true);
        var spawn = maps.GetSpawnPoint("city").Position;
        var spawnFull = Join(world, maps, sessions, 3, "spawn-full", spawn, lessOften: false);
        var spawnLess = Join(world, maps, sessions, 4, "spawn-less", spawn, lessOften: true);
        var everyone = new[] { full, less, spawnFull, spawnLess };
        bool counting = false;

        server.Broadcasted = (to, message) =>
        {
            var p = Array.Find(everyone, x => x.Session == to);
            if (p == null) return;
            if (message is ServerStateUpdate update)
            {
                // Each tick's lateness and loss, the same for both players.
                double late = Net.BaseSeconds + Net.SpreadSeconds * Hash(update.Tick + 1_000_003L * Net.Seed);
                bool lost = Hash(update.Tick * 7 + 3 + 1_000_003L * Net.Seed) < Net.Loss;
                foreach (var piece in NetworkService.Pieces(update, MaxPacket))
                {
                    byte[] bytes = MemoryPackSerializer.Serialize<IMessage>(piece);
                    if (counting) { p.Bytes += bytes.Length; p.Datagrams++; }
                    var back = (ServerStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(bytes)!;
                    if ((p == full || p == less) && !lost)
                        p.InFlight.Add((update.Tick * PhysicsConstants.FixedDeltaTime + late, back));
                }
                foreach (var s in update.States)
                {
                    if (counting) p.States++;
                    p.StatesById[s.EntityId] = p.StatesById.GetValueOrDefault(s.EntityId) + 1;
                }
                return;
            }
            // Everything else goes reliably: a datagram of its own at most, and here at once.
            byte[] b = MemoryPackSerializer.Serialize(message);
            if (counting) { p.Bytes += b.Length; p.Datagrams += 1 + b.Length / MaxPacket; }
            if (p != full && p != less) return;
            switch (MemoryPackSerializer.Deserialize<IMessage>(b))
            {
                case EntityDefinition def: p.Client.RegisterDefinition(def); break;
                case EntityRemoved gone: p.Client.RemoveEntities(gone.EntityIds); break;
            }
        };

        var occupancy = new OccupancySystem();
        var last = new Dictionary<int, Last>();
        var farBefore = new Dictionary<int, long>();
        long ticks = (long)(seconds * PhysicsConstants.TickRate);
        double clientTime = 0, nextSim = 0.013, simAt = 0;
        Vector3 ear = Listener + Ear;
        float dt = PhysicsConstants.FixedDeltaTime;
        log($"city: {ticks} ticks; pass-by entity {PassById}, fly-over {AircraftId}; network {Net}");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (long tick = 1; tick <= ticks; tick++)
        {
            if (!counting && tick * dt >= warmup)
            {
                counting = true;
                foreach (var p in everyone) { p.Bytes = p.Datagrams = p.States = 0; }
                foreach (var p in new[] { full, less }) p.StatesById.Clear();
            }
            MovementSystem.Update(world, data.WalkMin, data.WalkMax, grid, lookup, sessions, maps, dt);
            server.Vehicles.Update("city", world, dt);
            server.Rail.Update("city", world, dt);
            DrivingSystem.Update(world, grid, data.WalkMin, data.WalkMax, dt);
            ParentSystem.Update(world, lookup);
            occupancy.Update(world, lookup);
            server.BroadcastForTest(tick);
            var now = new Dictionary<int, (Vector3, Vector3)>();
            world.Query(new QueryDescription().WithAll<Transform, Velocity>(), (Entity e, ref Transform t, ref Velocity v) =>
                now[e.Id] = (t.Position, v.Linear));
            _truth[tick] = now;

            double until = tick * dt;
            while (clientTime + MixerStep <= until + 1e-9)
            {
                clientTime += MixerStep;
                foreach (var p in new[] { full, less })
                    for (int i = 0; i < p.InFlight.Count; i++)
                        if (p.InFlight[i].ArrivesAt <= clientTime) { p.Client.SyncState(p.InFlight[i].Update); p.InFlight.RemoveAt(i--); }
                bool stepped = false;
                while (clientTime >= nextSim)
                {
                    full.Client.UpdateInterpolation(dt, full.Session.Entity.Id);
                    less.Client.UpdateInterpolation(dt, less.Session.Entity.Id);
                    nextSim += dt;
                    simAt = clientTime;
                    stepped = true;
                    int traced = TraceId == -2 ? PassById : TraceId == -3 ? AircraftId : TraceId;
                    if (traced >= 0 && lookup.TryGetValue(traced, out var te) && world.IsAlive(te))
                    {
                        full.Client.TryGetInterpolatedMotion(traced, out var a, out var av);
                        less.Client.TryGetInterpolatedMotion(traced, out var b, out var bv);
                        var truth = world.Get<Transform>(te).Position;
                        Trace.Add($"{clientTime:F3} tick {tick} truth {truth.X:F2},{truth.Y:F2},{truth.Z:F2} | full {a.Position.X:F2},{a.Position.Y:F2},{a.Position.Z:F2} v {av.Length():F2} | "
                                + $"less {b.Position.X:F2},{b.Position.Y:F2},{b.Position.Z:F2} v {bv.Length():F2} | sent full {full.StatesById.GetValueOrDefault(traced)} less {less.StatesById.GetValueOrDefault(traced)}");
                    }
                }
                double age = Math.Min(clientTime - simAt, MaxDeadReckon);
                Compare(world, lookup, full, less, ear, age, stepped, clientTime, last, full.Client.PlaybackTime);
            }
            if (tick % (10 * PhysicsConstants.TickRate) == 0)
                log($"  {tick * dt:F0} s simulated in {clock.Elapsed.TotalSeconds:F0} s");
        }

        double counted = seconds - warmup;
        Bytes.Add(Tally("40 m from the railway", full, less, counted));
        Bytes.Add(Tally("the spawn point", spawnFull, spawnLess, counted));
        foreach (var scene in Scenes.Values)
            foreach (int id in scene.Entities)
            {
                scene.FullStates += full.StatesById.GetValueOrDefault(id);
                scene.LessStates += less.StatesById.GetValueOrDefault(id);
            }
    }

    private static Bandwidth Tally(string where, Player full, Player less, double seconds) => new()
    {
        Where = where, Seconds = seconds,
        FullBytes = full.Bytes, LessBytes = less.Bytes, FullDatagrams = full.Datagrams, LessDatagrams = less.Datagrams,
        FullStates = full.States, LessStates = less.States,
    };

    /// <summary>The server's own track, tick by tick: what every moving thing was and how fast.</summary>
    private readonly Dictionary<long, Dictionary<int, (Vector3 Position, Vector3 Velocity)>> _truth = new();

    private bool Truth(int id, double time, out Vector3 position, out Vector3 velocity)
    {
        position = velocity = default;
        long k = (long)Math.Floor(time / PhysicsConstants.FixedDeltaTime);
        if (!_truth.TryGetValue(k, out var a) || !_truth.TryGetValue(k + 1, out var b)) return false;
        if (!a.TryGetValue(id, out var sa) || !b.TryGetValue(id, out var sb)) return false;
        float f = (float)(time / PhysicsConstants.FixedDeltaTime - k);
        position = Vector3.Lerp(sa.Position, sb.Position, f);
        velocity = Vector3.Lerp(sa.Velocity, sb.Velocity, f);
        return true;
    }

    private static double Pitch(Vector3 ear, Vector3 at, Vector3 velocity, bool engine)
        => AudioPhysics.DopplerFactor(ear, Vector3.Zero, at, velocity) * (engine ? Math.Max(velocity.Length(), 1f) : 1f);

    private void Compare(World world, Dictionary<int, Entity> lookup, Player full, Player less, Vector3 ear, double age,
                         bool stepped, double now, Dictionary<int, Last> lasts, double heard)
    {
        foreach (int id in less.Client.InterpolatedIds())
        {
            if (id == full.Session.Entity.Id || id == less.Session.Entity.Id) continue;
            if (!full.Client.TryGetInterpolatedMotion(id, out var ft, out var fv)) continue;
            if (!less.Client.TryGetInterpolatedMotion(id, out var lt, out var lv)) continue;
            var scene = SceneOf(world, lookup, id);
            if (scene == null) continue;
            scene.Entities.Add(id);

            // Carried on as the mixer carries it between steps.
            Vector3 fp = ft.Position + fv * (float)age, lp = lt.Position + lv * (float)age;
            double distance = Vector3.Distance(fp, ear);
            if (distance < 1.0) continue;
            bool far = distance >= DistantMotion.FullRateMetres;
            scene.Instants++;
            if (far) scene.FarInstants++;

            double fb = Bearing(fp - ear), lb = Bearing(lp - ear);
            double bearing = Angle(fp - ear, lp - ear);
            float fd = AudioPhysics.DopplerFactor(ear, Vector3.Zero, fp, fv);
            float ld = AudioPhysics.DopplerFactor(ear, Vector3.Zero, lp, lv);
            float fs = fv.Length(), ls = lv.Length();
            bool engine = scene.Name != "walker";
            // The pitch the ear gets: the Doppler, and for anything with an engine its road speed.
            double fpitch = fd * (engine ? Math.Max(fs, 1f) : 1f), lpitch = ld * (engine ? Math.Max(ls, 1f) : 1f);
            double pitch = Math.Abs(lpitch / fpitch - 1);
            double doppler = Math.Abs((double)ld / fd - 1);
            // The road speed's share alone, where an engine is off idle (above a metre a second).
            double speed = fs > 1f ? Math.Abs(ls / fs - 1) : 0;
            if (bearing > scene.WorstBearing) { scene.WorstBearing = bearing; scene.WorstBearingAt = $"entity {id} at {now:F2} s, {distance:F0} m"; }
            if (pitch > scene.WorstPitch) { scene.WorstPitch = pitch; scene.WorstPitchAt = $"entity {id} at {now:F2} s, {distance:F0} m, {fs:F1} m/s"; }
            scene.WorstDoppler = Math.Max(scene.WorstDoppler, doppler);
            scene.WorstSpeed = Math.Max(scene.WorstSpeed, speed);
            scene.WorstPosition = Math.Max(scene.WorstPosition, Vector3.Distance(fp, lp));
            if (Truth(id, heard, out var tp, out var tv))
            {
                tp += tv * (float)age;
                double tpitch = Pitch(ear, tp, tv, engine);
                scene.TruthBearingFull = Math.Max(scene.TruthBearingFull, Angle(fp - ear, tp - ear));
                scene.TruthBearingLess = Math.Max(scene.TruthBearingLess, Angle(lp - ear, tp - ear));
                scene.TruthPitchFull = Math.Max(scene.TruthPitchFull, Math.Abs(fpitch / tpitch - 1));
                if (Math.Abs(lpitch / tpitch - 1) > scene.TruthPitchLess)
                    scene.TruthPitchLessAt = $"entity {id} at {now:F2} s, {distance:F0} m, {tv.Length():F2} m/s true, {ls:F2} less, {fs:F2} full";
                scene.TruthPitchLess = Math.Max(scene.TruthPitchLess, Math.Abs(lpitch / tpitch - 1));
            }

            if (!lasts.TryGetValue(id, out var was)) lasts[id] = was = new Last();
            if (id == (TraceId == -2 ? PassById : TraceId == -3 ? AircraftId : TraceId) && was.Have)
                Trace.Add($"  {now:F3} mix full {fp.X:F3},{fp.Z:F3} step {Vector3.Distance(fp, was.FullPos) * 1000:F1} mm v {fv.Length():F3} | "
                        + $"less {lp.X:F3},{lp.Z:F3} step {Vector3.Distance(lp, was.LessPos) * 1000:F1} mm v {lv.Length():F3}{(stepped ? " *" : "")}");
            if (was.Have && far)
            {
                double fstep = Vector3.Distance(fp, was.FullPos), lstep = Vector3.Distance(lp, was.LessPos);
                double fbs = Turn(fb, was.FullBearing), lbs = Turn(lb, was.LessBearing);
                double fps = Math.Abs(fpitch / was.FullPitch - 1), lps = Math.Abs(lpitch / was.LessPitch - 1);
                if (fstep > scene.FullStepPosition) scene.FullStepAt = $"entity {id} at {now:F3} s, {distance:F0} m";
                if (lstep > scene.LessStepPosition) scene.LessStepAt = $"entity {id} at {now:F3} s, {distance:F0} m";
                scene.FullStepPosition = Math.Max(scene.FullStepPosition, fstep);
                scene.LessStepPosition = Math.Max(scene.LessStepPosition, lstep);
                scene.FullStepBearing = Math.Max(scene.FullStepBearing, fbs);
                scene.LessStepBearing = Math.Max(scene.LessStepBearing, lbs);
                scene.FullStepPitch = Math.Max(scene.FullStepPitch, fps);
                scene.LessStepPitch = Math.Max(scene.LessStepPitch, lps);
                if (Math.Abs(lstep - fstep) > scene.StepDifferencePosition)
                    scene.StepAt = $"entity {id} at {now:F3} s, {distance:F0} m: {fstep * 1000:F1} mm full, {lstep * 1000:F1} mm less";
                scene.StepDifferencePosition = Math.Max(scene.StepDifferencePosition, Math.Abs(lstep - fstep));
                scene.StepDifferenceBearing = Math.Max(scene.StepDifferenceBearing, Math.Abs(lbs - fbs));
                scene.StepDifferencePitch = Math.Max(scene.StepDifferencePitch, Math.Abs(lps - fps));
            }
            // A train's notch, as the client works it out at each step (ClientAudioSystem: pulling away full,
            // holding a little, braking none).
            if (stepped && scene.Name == "train")
            {
                if (was.Have && now > was.FullSpeedAt)
                {
                    float span = (float)Math.Max(0.02, now - was.FullSpeedAt);
                    float fl = Lever((fs - was.FullSpeed) / span, fs), ll = Lever((ls - was.LessSpeed) / span, ls);
                    scene.LeverSteps++;
                    if (fl != ll) { scene.LeverDiffer++; was.LeverRun++; scene.LeverRun = Math.Max(scene.LeverRun, was.LeverRun); }
                    else was.LeverRun = 0;
                }
                was.FullSpeed = fs; was.LessSpeed = ls; was.FullSpeedAt = now;
            }
            was.Have = true;
            was.FullPos = fp; was.LessPos = lp;
            was.FullBearing = fb; was.LessBearing = lb;
            was.FullPitch = fpitch; was.LessPitch = lpitch;
        }
    }

    private static float Lever(float accel, float speed) => accel > 0.08f ? 0.9f : accel < -0.15f ? 0f : speed > 0.5f ? 0.3f : 0f;

    private readonly Dictionary<int, string?> _sceneOf = new();

    /// <summary>Which scene a thing belongs to, from what the server made it: the pass-by, the fly-over, a
    /// train's source, a walker, or the rest of the traffic. Anything else (a door, a bus's panels) is not
    /// compared.</summary>
    private Scene? SceneOf(World world, Dictionary<int, Entity> lookup, int id)
    {
        if (!_sceneOf.TryGetValue(id, out var name))
        {
            name = null;
            if (id == PassById) name = "pass-by";
            else if (id == AircraftId) name = "fly-over";
            else if (lookup.TryGetValue(id, out var e) && world.IsAlive(e))
            {
                string sound = world.Has<SoundEmitterComponent>(e) ? world.Get<SoundEmitterComponent>(e).SoundId ?? "" : "";
                if (sound.StartsWith("rail:", StringComparison.Ordinal)) name = "train";
                else if (world.Has<Pedestrian>(e)) name = "walker";
                else if (sound.StartsWith("engine:", StringComparison.Ordinal) && !world.Has<ParentComponent>(e)) name = "traffic";
            }
            _sceneOf[id] = name;
        }
        if (name == null) return null;
        if (!Scenes.TryGetValue(name, out var scene)) Scenes[name] = scene = new Scene { Name = name };
        return scene;
    }

    private static double Bearing(Vector3 d) => Math.Atan2(d.X, d.Z) * 180 / Math.PI;

    /// <summary>The change in a bearing, degrees, the short way round.</summary>
    private static double Turn(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>The angle between two directions, degrees, in doubles.</summary>
    private static double Angle(Vector3 a, Vector3 b)
    {
        double ax = a.X, ay = a.Y, az = a.Z, bx = b.X, by = b.Y, bz = b.Z;
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ax * bx + ay * by + az * bz) * 180 / Math.PI;
    }

    private static double Hash(long tick)
    {
        ulong x = (ulong)tick * 0x9E3779B97F4A7C15UL;
        x ^= x >> 31; x *= 0xBF58476D1CE4E5B9UL; x ^= x >> 29;
        return (x >> 11) / (double)(1UL << 53);
    }

    private static Player Join(World world, MapManager maps, SessionManager sessions, int connection, string name, Vector3 at, bool lessOften)
    {
        var body = world.Create(
            new PlayerComponent { ConnectionId = connection, Username = name },
            EntityType.Player,
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity(), new MaterialComponent { Material = "Generic" },
            new NameComponent { Name = name },
            new HealthComponent { Current = 100, Max = 100 },
            new ColliderComponent
            {
                Shape = ColliderShape.Cylinder,
                Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                IsSolid = false,
            });
        maps.IndexEntity("city", body);
        var session = new UserSession
        {
            ConnectionId = connection, Username = name, Entity = body, CurrentMapId = "city", Welcomed = true,
            DistantLessOften = lessOften,
        };
        sessions.AddSession(connection, session);
        var p = new Player { Session = session };
        maps.TryGetMapData("city", out var data);
        p.Client.Clear(data!.Size);
        return p;
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }

    /// <summary>The comparison as lines of text.</summary>
    public IEnumerable<string> Report()
    {
        yield return "scene      things  far s   bearing   pitch  (doppler speed>1m/s)  position   step pos m full/less   step bearing deg full/less   step pitch % full/less   states far full->less   notch differs";
        foreach (var s in Scenes.Values.OrderBy(s => s.Name))
            yield return $"{s.Name,-10} {s.Entities.Count,6} {s.FarInstants * MixerStep / Math.Max(1, s.Entities.Count),6:F0} "
                       + $"{s.WorstBearing,8:F3}° {s.WorstPitch * 100,6:F3} % ({s.WorstDoppler * 100:F3} {s.WorstSpeed * 100:F3}) {s.WorstPosition,7:F3} m  "
                       + $"{s.FullStepPosition,8:F4}/{s.LessStepPosition,-8:F4}  {s.FullStepBearing,10:F4}/{s.LessStepBearing,-10:F4}  "
                       + $"{s.FullStepPitch * 100,9:F4}/{s.LessStepPitch * 100,-9:F4}  {s.FullStates,8} -> {s.LessStates,-8}  "
                       + $"{s.LeverDiffer}/{s.LeverSteps} (longest {s.LeverRun})";
        yield return "against the server's own track: bearing full / less, pitch full / less";
        foreach (var s in Scenes.Values.OrderBy(s => s.Name))
            yield return $"  {s.Name,-10} {s.TruthBearingFull:F3}° / {s.TruthBearingLess:F3}°   {s.TruthPitchFull * 100:F3} % / {s.TruthPitchLess * 100:F3} % ({s.TruthPitchLessAt})";
        foreach (var s in Scenes.Values.OrderBy(s => s.Name))
            yield return $"  {s.Name}: worst bearing {s.WorstBearingAt}; worst pitch {s.WorstPitchAt}; largest step difference "
                       + $"{s.StepDifferencePosition * 1000:F2} mm, {s.StepDifferenceBearing:F4}°, {s.StepDifferencePitch * 100:F4} % ({s.StepAt}); largest step full {s.FullStepAt}, less {s.LessStepAt}";
        foreach (var b in Bytes)
            yield return $"bytes at {b.Where}: every tick {b.FullMbit:F2} Mbit/s ({b.FullDatagrams / b.Seconds:F0} datagrams/s, "
                       + $"{b.FullStates / b.Seconds:F0} states/s); far less often {b.LessMbit:F2} Mbit/s ({b.LessDatagrams / b.Seconds:F0} datagrams/s, "
                       + $"{b.LessStates / b.Seconds:F0} states/s): {100 * (1 - b.LessMbit / b.FullMbit):F0} % less";
    }
}
