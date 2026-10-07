using System.Numerics;
using Arch.Core;
using MemoryPack;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Another player's footsteps, end to end (Cody and Sean on the VPS, 2026-10-05: "I cannot hear his
/// footsteps while he's walking, only his beacon, same with him. also, when I'm a passenger in a car and
/// he's driving, I hear footsteps like his footsteps while he's driving").
///
/// The real server pieces: one player walks by input through MovementSystem, or drives a parked car of
/// the city through DrivingSystem with OccupancySystem carrying him; the broadcast picks, trims, packs
/// and splits his state exactly as it goes to the other player's socket; that client reads it back,
/// interpolates it at 60 Hz with each tick arriving 40 ms late, and OtherBodies listens for his feet.
/// </summary>
public class RemoteFootstepsTests
{
    private readonly ITestOutputHelper _o;
    public RemoteFootstepsTests(ITestOutputHelper o) => _o = o;

    private sealed class Rig
    {
        public World World = null!;
        public SpatialGrid<Entity> Grid = null!;
        public Dictionary<int, Entity> Lookup = null!;
        public MapManager Maps = null!;
        public MapData Data = null!;
        public SessionManager Sessions = new();
        public GameServer Server = null!;
        public OccupancyService Occupancy = null!;
        public OccupancySystem Seats = new();
        public UserSession Cody = null!, Sean = null!;
        public ClientWorldState Client = new();
        public readonly List<(double ArrivesAt, ServerStateUpdate Update)> InFlight = new();
        public long Tick;
        /// <summary>As it is played: traffic and walkers on the map, so the tick is split over many
        /// packets; each packet arriving 30 to 90 ms after it was sent; the client's world advanced by
        /// its 30 Hz SimStep and the feet listened for every 5 ms, as ClientRunner's loop does.</summary>
        public bool Realistic;
        public readonly Random Jitter = new(5);
        public double NextSim;
    }

    private static Entity Body(World world, int connection, string name, Vector3 at) => world.Create(
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
            IsSolid = true,
        });

    private static Rig Build(Vector3? seanAt = null, bool realistic = false)
    {
        var r = new Rig { Realistic = realistic };
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        r.Maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        r.Maps.Initialize();
        // The four cars parked in the garage are composites the map places at startup.
        var composites = new CompositeService(r.Maps, prefabs,
            new CompositeRepository(Path.Combine(Path.GetTempPath(), "openfps-no-composites-" + Guid.NewGuid())));
        composites.PlaceRecorded(r.Maps);
        Assert.True(r.Maps.TryGetMap("city", out r.World, out _, out r.Grid, out r.Lookup));
        Assert.True(r.Maps.TryGetMapData("city", out var data));
        r.Data = data;

        r.Occupancy = new OccupancyService(r.Maps);
        var hands = new HandsService(r.Maps);
        r.Server = new GameServer(new NoUsers());
        r.Server.Attach(r.Maps, r.Sessions, r.Occupancy, hands);
        if (realistic) r.Server.Vehicles.Spawn(r.Maps, composites);

        var spawn = r.Maps.GetSpawnPoint("city").Position;
        var seanPos = seanAt ?? spawn;
        var codyBody = Body(r.World, 1, "cody", seanPos + new Vector3(3f, 0f, 0f));
        var seanBody = Body(r.World, 2, "sean", seanPos);
        r.Maps.IndexEntity("city", codyBody);
        r.Maps.IndexEntity("city", seanBody);
        r.Cody = new UserSession { ConnectionId = 1, Username = "cody", Entity = codyBody, CurrentMapId = "city", Welcomed = true };
        r.Sean = new UserSession { ConnectionId = 2, Username = "sean", Entity = seanBody, CurrentMapId = "city", Welcomed = true,
                                   Role = UserRole.Admin };
        r.Sessions.AddSession(1, r.Cody);
        r.Sessions.AddSession(2, r.Sean);

        // Cody's client as it stands after arriving: the map streamed, and the server's record of what
        // it was sent the same set.
        r.Client.Clear(data.Size);
        foreach (var e in EntityDefinitionFactory.StaticEntities(r.World))
        {
            r.Client.RegisterDefinition(EntityDefinitionFactory.From(r.World, e));
            r.Cody.KnownEntities.Add(e.Id);
        }

        // ...and from then on only what the broadcast sends him, the states through the wire as they go.
        r.Server.Broadcasted = (to, message) =>
        {
            if (to != r.Cody) return;
            switch (message)
            {
                case EntityDefinition def: r.Client.RegisterDefinition(def); break;
                case EntityRemoved gone: r.Client.RemoveEntities(gone.EntityIds); break;
                case ServerStateUpdate update when update.States.Count > 0:
                    double sent = update.Tick * PhysicsConstants.FixedDeltaTime;
                    ServerStateUpdate? merged = null;
                    foreach (var piece in NetworkService.Pieces(update, 1024))
                    {
                        var back = (ServerStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(MemoryPackSerializer.Serialize<IMessage>(piece))!;
                        if (r.Realistic) r.InFlight.Add((sent + 0.03 + 0.06 * r.Jitter.NextDouble(), back));
                        else if (merged == null) merged = back; else merged.States.AddRange(back.States);
                    }
                    if (merged != null) r.InFlight.Add((sent + 0.04, merged));
                    break;
            }
        };
        return r;
    }

    /// <summary>One server tick: Sean's input taken (two half-tick inputs, as a 60 Hz client sends), the
    /// world moved in the tick's order, and the broadcast.</summary>
    private static void ServerTick(Rig r, Vector3 move)
    {
        r.Tick++;
        for (int i = 0; i < 2; i++)
            r.Sean.InputQueue.Enqueue(new ClientInputUpdate
            {
                SequenceId = r.Tick * 2 + i, MoveDirection = move, DeltaTime = PhysicsConstants.FixedDeltaTime / 2f,
            });
        float dt = PhysicsConstants.FixedDeltaTime;
        MovementSystem.Update(r.World, r.Data.WalkMin, r.Data.WalkMax, r.Grid, r.Lookup, r.Sessions, r.Maps, dt);
        if (r.Realistic) r.Server.Vehicles.Update("city", r.World, dt);
        DrivingSystem.Update(r.World, r.Grid, r.Data.WalkMin, r.Data.WalkMax, dt);
        ParentSystem.Update(r.World, r.Lookup);
        r.Seats.Update(r.World, r.Lookup);
        r.Server.BroadcastForTest(r.Tick);
    }

    /// <summary>Plays Cody's client up to the current server time, at 60 Hz, counting Sean's footfalls.</summary>
    private static void PlayClient(Rig r, OtherBodies others, ref double clientTime, List<string>? trace = null)
    {
        if (r.Realistic) { PlayClientAsRun(r, others, ref clientTime, trace); return; }
        const double frame = 1.0 / 60.0;
        double until = r.Tick * PhysicsConstants.FixedDeltaTime;
        while (clientTime + frame <= until)
        {
            clientTime += frame;
            for (int i = 0; i < r.InFlight.Count; i++)
                if (r.InFlight[i].ArrivesAt <= clientTime) { r.Client.SyncState(r.InFlight[i].Update); r.InFlight.RemoveAt(i--); }
            r.Client.UpdateInterpolation((float)frame, r.Cody.Entity.Id);
            var snap = r.Client.GetSnapshot();
            others.Update(snap, r.Cody.Entity.Id);
            if (trace != null && snap.Entities.TryGetValue(r.Sean.Entity.Id, out var s))
                trace.Add($"t={clientTime:F3} pos={s.Transform.Position} vel={s.Velocity}");
        }
    }

    /// <summary>ClientRunner's loop: the world advanced by SimStep at 30 Hz, the feet every 5 ms.</summary>
    private static void PlayClientAsRun(Rig r, OtherBodies others, ref double clientTime, List<string>? trace)
    {
        const double loop = 0.005, sim = PhysicsConstants.FixedDeltaTime;
        double until = r.Tick * PhysicsConstants.FixedDeltaTime;
        while (clientTime + loop <= until)
        {
            clientTime += loop;
            for (int i = 0; i < r.InFlight.Count; i++)
                if (r.InFlight[i].ArrivesAt <= clientTime) { r.Client.SyncState(r.InFlight[i].Update); r.InFlight.RemoveAt(i--); }
            if (r.NextSim == 0) r.NextSim = clientTime + 0.013;
            while (clientTime >= r.NextSim)
            {
                r.Client.UpdateInterpolation((float)sim, r.Cody.Entity.Id);
                r.NextSim += sim;
                var at = r.Client.GetSnapshot();
                if (trace != null && at.Entities.TryGetValue(r.Sean.Entity.Id, out var s))
                    trace.Add($"t={clientTime:F3} pos={s.Transform.Position} vel={s.Velocity}");
            }
            others.Update(r.Client.GetSnapshot(), r.Cody.Entity.Id);
        }
    }

    /// <summary>Sean's footfalls only: on the city everybody else walking near him has feet too.</summary>
    private static Action<Vector3, string, string, StepSlope, int> Near(Rig r, Action counted)
        => (p, _, _, _, _) =>
        {
            if (r.Client.GetSnapshot().Entities.TryGetValue(r.Sean.Entity.Id, out var s)
                && Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(s.Transform.Position.X, s.Transform.Position.Z)) < 1.5f)
                counted();
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherPlayerWalkingIsHeardStepping(bool realistic)
    {
        var r = Build(realistic: realistic);
        var others = new OtherBodies();
        int steps = 0;
        others.OnStepTriggered += Near(r, () => steps++);
        // ...and what each of his steps has between it and Cody's ears, asked as the audio system asks
        // it (ClientAudioSystem.CarryThePath): a clear street, so nothing but his own body could be.
        var spatial = new SpatialService { OwnEntityId = r.Cody.Entity.Id };
        var acoustics = new OpenFPS.Client.AudioEngine.Acoustics.SpatialAcoustics(spatial);
        var heard = new List<(float Distance, float Mid, float Occlusion)>();
        others.OnStepTriggered += (p, _, _, _, body) =>
        {
            if (body != r.Sean.Entity.Id) return;
            var snap = r.Client.GetSnapshot();
            var ear = r.World.Get<Transform>(r.Cody.Entity).Position + new Vector3(0, 1.7f, 0);
            var foot = p + new Vector3(0, 0.1f, 0);
            var path = acoustics.CalculateAcousticPath(snap, body, ear, foot);
            heard.Add((Vector3.Distance(ear, foot), path.EqMid, path.Occlusion));
        };
        double clientTime = 0;
        var trace = new List<string>();

        // Standing a second (so he is known and at rest), walking three, standing again.
        for (int k = 0; k < 30; k++) { ServerTick(r, Vector3.Zero); PlayClient(r, others, ref clientTime); }
        int before = steps;
        var start = r.World.Get<Transform>(r.Sean.Entity).Position;
        for (int k = 0; k < 90; k++) { ServerTick(r, new Vector3(0, 0, 1)); PlayClient(r, others, ref clientTime, trace); }
        var end = r.World.Get<Transform>(r.Sean.Entity).Position;
        for (int k = 0; k < 30; k++) { ServerTick(r, Vector3.Zero); PlayClient(r, others, ref clientTime); }

        float walked = Vector2.Distance(new Vector2(start.X, start.Z), new Vector2(end.X, end.Z));
        _o.WriteLine($"Sean walked {walked:F2} m on the server ({start} to {end}); Cody heard {steps - before} footfalls");
        foreach (var line in trace.Where((_, i) => i % 15 == 0)) _o.WriteLine(line);
        Assert.True(walked > 8f, $"the rig did not walk him: {walked:F2} m");
        // 13.5 m at 4.5 m/s is ten steps of 1.38 m.
        Assert.InRange(steps - before, 7, 13);
        foreach (var (d, mid, occ) in heard) _o.WriteLine($"  a step {d:F1} m away: mid band {mid:F2}, occlusion {occ:F2}");
        Assert.NotEmpty(heard);
        // His feet are inside his own body, which is a solid cylinder of whatever he last stood on: it
        // took three of the five rays and left every step 8 dB down and 0.6 occluded. One ray of the
        // five ends under the pavement and is the ground's (0.8, 0.2), as it is for anybody's step.
        Assert.All(heard, h => Assert.True(h.Mid > 0.75f && h.Occlusion < 0.25f,
            $"a step {h.Distance:F1} m away came through at {h.Mid:F2} in the mid band, occlusion {h.Occlusion:F2}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherPlayerDrivingMakesNoFootsteps(bool realistic)
    {
        // The nearest car of the city a player can drive.
        var probe = Build();
        Entity car = Entity.Null;
        var q = new QueryDescription().WithAll<Transform, DriveComponent, OccupancyComponent>();
        probe.World.Query(in q, (Entity e, ref Transform _, ref DriveComponent d, ref OccupancyComponent o) =>
        {
            if (car == Entity.Null && !string.IsNullOrEmpty(d.Preset) && o.Seats is { Count: > 0 }) car = e;
        });
        Assert.True(car != Entity.Null, "the city has no car a player can drive");
        var carAt = probe.World.Get<Transform>(car).Position;

        var r = Build(carAt + new Vector3(2f, 0f, 0f), realistic);
        var others = new OtherBodies();
        int steps = 0;
        others.OnStepTriggered += Near(r, () => steps++);
        double clientTime = 0;
        var trace = new List<string>();

        for (int k = 0; k < 15; k++) { ServerTick(r, Vector3.Zero); PlayClient(r, others, ref clientTime); }
        Assert.True(r.Occupancy.Enter(r.Sean, car.Id, null, out string said), said);
        Assert.True(r.World.Get<OccupantComponent>(r.Sean.Entity).Controls, "Sean is not in the driving seat");
        DrivingSystem.SetIgnition(r.World, car, true, null);
        for (int k = 0; k < 30; k++) { ServerTick(r, Vector3.Zero); PlayClient(r, others, ref clientTime); }
        int before = steps;
        var start = r.World.Get<Transform>(car).Position;
        for (int k = 0; k < 150; k++) { ServerTick(r, new Vector3(0, 0, 1)); PlayClient(r, others, ref clientTime, trace); }
        var end = r.World.Get<Transform>(car).Position;
        float driven = Vector3.Distance(start, end);
        _o.WriteLine($"the car went {driven:F1} m; Cody heard {steps - before} footfalls from Sean in it");
        foreach (var line in trace.Where((_, i) => i % 30 == 0)) _o.WriteLine(line);
        Assert.True(driven > 5f, $"the rig did not drive the car: {driven:F1} m");
        Assert.Equal(0, steps - before);
    }

    // ── Heard, through the audio system ───────────────────────────────────────────────────────

    private const int Me = 1, Sean = 42;

    /// <summary>A player's body as the broadcast defines it: a solid cylinder, of the floor it last
    /// stood on (MovementSystem writes the floor to the body's material).</summary>
    private static void AddBody(ClientAudioHarness h, int id, Vector3 feet)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.Player,
            Moves = true,
            Transform = new Transform { Position = feet, Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent
            {
                Shape = ColliderShape.Cylinder,
                Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                IsSolid = true,
            },
            Material = new MaterialComponent { Material = "Concrete", Variant = "0" },
        };
        h.World.RegisterDefinition(def);
    }

    private static ClientAudioHarness Street(Vector3 seanFeet)
    {
        var h = new ClientAudioHarness(Sounds());
        var ground = new EntityDefinition
        {
            EntityId = 7,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = new Vector3(0f, -0.25f, 0f), Rotation = Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(200f, 0.5f, 200f), IsSolid = true },
            Material = new MaterialComponent { Material = "Asphalt", Variant = "0" },
        };
        h.World.RegisterDefinition(ground);
        AddBody(h, Me, Vector3.Zero);
        AddBody(h, Sean, seanFeet);
        h.Audio.OwnEntityId = Me;
        h.StandAt(Vector3.Zero);
        h.Tick();
        return h;
    }

    /// <summary>
    /// Another player's step, from OtherBodies through the audio system to the mixer, is not heard
    /// through his own body.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    [InlineData(9f)]
    public void AnotherPlayersStepIsNotMuffledByHisOwnBody(float metres)
    {
        var feet = new Vector3(0f, 0f, metres);
        var h = Street(feet);
        var others = new OtherBodies();
        others.OnStepTriggered += h.Audio.OnPlayerFootstep;   // as ClientGameSession wires it

        // Sean walks a few steps toward where he stands, his velocity his own.
        for (int i = 0; i < 12; i++)
        {
            var at = feet + new Vector3(0f, 0f, (i - 11) * 0.15f);
            h.World.SyncState(new[] { new EntityState
            {
                EntityId = Sean,
                Transform = QuantizedTransform.FromTransform(new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One }),
                LinearVelocity = new Vector3(0f, 0f, PhysicsConstants.WalkSpeed),
            } });
            others.Update(h.World.GetSnapshot(), Me);
            h.Tick();
        }
        h.Tick(3);

        var steps = h.Mixer.Started.Where(e => e.EntityId <= -300 && e.EntityId > -364).ToList();
        Assert.NotEmpty(steps);
        foreach (var e in steps)
            _o.WriteLine($"step at {e.Position}: mid {e.EqMid:F2}, occlusion {e.Occlusion:F2}");
        // The ground takes the one ray of five that ends under it (0.8, 0.2); his body took three more.
        Assert.All(steps, e => Assert.True(e.EqMid > 0.75f && e.Occlusion < 0.25f,
            $"Sean's step at {Vector3.Distance(e.Position, new Vector3(0, h.Player.EyeHeight, 0)):F1} m came through at {e.EqMid:F2}, occlusion {e.Occlusion:F2}"));
    }

    private static string Sounds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS"))) dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS")
                           : "/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS";
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }
}
