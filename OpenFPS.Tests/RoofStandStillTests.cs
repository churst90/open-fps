using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Tests;

/// <summary>
/// Somebody standing still stays where they stand. Sean, on Kestrel House's roof beside Cody
/// (2026-10-05), with no input at all, was moved half a metre a tick along the north parapet and then,
/// in the north-east corner, half a metre INTO the parapet and back out on alternate ticks for a minute
/// and a half, until he came out of its far side and fell eighteen metres onto the dirt. What moved him
/// was the player beside him: movement took a player's whole orientation for their box, look pitch
/// included, so Cody looking down tipped a 1.8 m box over and swept it through Sean; and the collision
/// passes, given a push into a wall, left the body inside the wall on every other tick.
///
/// These stand a player still on the real city through the real server movement, with somebody beside
/// them looking up, down and round, against every parapet and in every corner of all five tower roofs,
/// and beside every roof door as it is opened and shut.
/// </summary>
public class RoofStandStillTests : IClassFixture<RoofStandStillTests.City>
{
    private readonly City _city;
    private readonly ITestOutputHelper _o;
    public RoofStandStillTests(City city, ITestOutputHelper o) { _city = city; _o = o; }

    public sealed class City
    {
        public readonly MapManager Maps;
        public readonly World World;
        public readonly SpatialGrid<Entity> Grid;
        public readonly Dictionary<int, Entity> Lookup;
        public readonly MapData Data;
        public readonly SessionManager Sessions = new();
        /// <summary>Each tower's roof: the inside faces of its parapets, and the roof's height.</summary>
        public readonly List<(string Tower, float X0, float X1, float Z0, float Z1, float Y)> Roofs = new();
        public readonly List<(string Tower, Entity Door)> RoofDoors = new();
        private int _conn = 900;

        public City()
        {
            AcousticRegistry.Initialize();
            Maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            Maps.Initialize();
            Assert.True(Maps.TryGetMap("city", out var world, out _, out var grid, out var lookup));
            Assert.True(Maps.TryGetMapData("city", out var data));
            World = world; Grid = grid; Lookup = lookup; Data = data;

            var parapets = new List<(string Tower, string Side, Vector3 Min, Vector3 Max)>();
            var doors = new List<(string Tower, Entity Door)>();
            World.Query(new QueryDescription().WithAll<Transform, IdentityComponent>(), (Entity e, ref Transform t, ref IdentityComponent id) =>
            {
                string name = id.Name ?? "";
                int at = name.IndexOf(" roof parapet, ", StringComparison.Ordinal);
                if (at > 0 && World.Has<ColliderComponent>(e))
                {
                    var half = World.Get<ColliderComponent>(e).Size * 0.5f;
                    parapets.Add((name[..at], name[(at + 15)..].Split(' ')[0], t.Position - half, t.Position + half));
                }
                if (name.EndsWith(" roof access door", StringComparison.Ordinal) && World.Has<DoorComponent>(e))
                    doors.Add((name[..^" roof access door".Length], e));
            });
            foreach (var tower in parapets.GroupBy(p => p.Tower))
            {
                float x0 = tower.Where(p => p.Side == "west").Max(p => p.Max.X);
                float x1 = tower.Where(p => p.Side == "east").Min(p => p.Min.X);
                float z0 = tower.Where(p => p.Side == "south").Max(p => p.Max.Z);
                float z1 = tower.Where(p => p.Side == "north").Min(p => p.Min.Z);
                Roofs.Add((tower.Key, x0, x1, z0, z1, tower.Min(p => p.Min.Y)));
            }
            RoofDoors.AddRange(doors);
        }

        public (Entity Body, UserSession Session) Place(Vector3 at)
        {
            int conn = ++_conn;
            var session = new UserSession { ConnectionId = conn, Username = "p" + conn, CurrentMapId = "city" };
            Sessions.AddSession(conn, session);
            var body = World.Create(
                new PlayerComponent { Username = "p" + conn, ConnectionId = conn },
                new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
                new Velocity(), new MaterialComponent(),
                new ColliderComponent { Shape = ColliderShape.Cylinder, Size = PlayerSize, IsSolid = true });
            Lookup[body.Id] = body;
            return (body, session);
        }

        /// <summary>Somebody standing beside them, looking the way <paramref name="yaw"/> and
        /// <paramref name="pitch"/> say, as MovementSystem leaves a player's transform. No session: they
        /// do not move, they only look.</summary>
        public Entity Bystander(Vector3 at, float yaw, float pitch)
            => World.Create(
                new PlayerComponent { Username = "bystander", ConnectionId = -1, Yaw = yaw, Pitch = pitch },
                new Transform { Position = at, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f), Scale = Vector3.One },
                new Velocity(), new MaterialComponent(),
                new ColliderComponent { Shape = ColliderShape.Cylinder, Size = PlayerSize, IsSolid = true });

        public void Remove(Entity body, UserSession? session)
        {
            Lookup.Remove(body.Id);
            World.Destroy(body);
            if (session != null) Sessions.TryRemoveSession(session.ConnectionId, out _);
        }

        /// <summary>One server tick with no input, as Program.cs runs it: the moving things re-gridded,
        /// then MovementSystem.</summary>
        public void Tick(UserSession session, long seq)
        {
            session.InputQueue.Enqueue(new ClientInputUpdate { SequenceId = seq, MoveDirection = Vector3.Zero, DeltaTime = FixedDeltaTime });
            Grid.Clear();
            World.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                (Entity e, ref Transform t, ref ColliderComponent c) => Grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
            MovementSystem.Update(World, Data.WalkMin, Data.WalkMax, Grid, Lookup, Sessions, Maps, FixedDeltaTime);
        }
    }

    /// <summary>Where Sean stood for a minute and a half, in the north-east corner of Kestrel House's
    /// roof against its north parapet, and the 0.47 m it moved him by each tick.</summary>
    private static readonly Vector3 Sean = new(30.0986f, 18.25f, -26.651f);

    private const float StillMetres = 0.005f;

    /// <summary>The look of somebody beside you: every way round, level, down and up, and nearly
    /// straight down and up.</summary>
    private static IEnumerable<(float Yaw, float Pitch)> Looks()
    {
        foreach (float pitch in new[] { 0f, 0.785f, -0.785f, 1.5f, -1.5f })
            for (int d = 0; d < 360; d += 45)
                yield return (d * MathF.PI / 180f, pitch);
    }

    /// <summary>
    /// Stands a player at <paramref name="at"/> with somebody looking about at <paramref name="beside"/>,
    /// and says what went wrong, if anything: moved by more than <see cref="StillMetres"/> in a tick,
    /// off the roof, or into the parapet.
    /// </summary>
    private string? StandStill(Vector3 at, Vector3 beside, (float X0, float X1, float Z0, float Z1, float Y) roof, int ticks = 30)
    {
        var (body, session) = _city.Place(at);
        Entity? other = null;
        try
        {
            // Alone first: nothing on the roof itself moves a body standing there.
            for (int t = 0; t < 3; t++) _city.Tick(session, t + 1);
            var alone = _city.World.Get<Transform>(body).Position;
            if (Vector3.Distance(alone, at) > StillMetres) return $"alone at {at}, moved to {alone}";

            long seq = 10;
            foreach (var (yaw, pitch) in Looks())
            {
                if (other is { } o) _city.World.Destroy(o);
                other = _city.Bystander(beside, yaw, pitch);
                for (int t = 0; t < ticks; t++)
                {
                    var from = _city.World.Get<Transform>(body).Position;
                    _city.Tick(session, seq++);
                    var to = _city.World.Get<Transform>(body).Position;
                    string look = $"beside somebody at {beside} looking yaw {yaw:F2} pitch {pitch:F2}, tick {t}";
                    if (Vector3.Distance(from, to) > StillMetres) return $"standing at {at}, {look}: moved {from} to {to}";
                    if (to.Y < roof.Y - 0.01f) return $"standing at {at}, {look}: fell to {to}";
                    if (to.X < roof.X0 + PlayerRadius - 0.01f || to.X > roof.X1 - PlayerRadius + 0.01f
                        || to.Z < roof.Z0 + PlayerRadius - 0.01f || to.Z > roof.Z1 - PlayerRadius + 0.01f)
                        return $"standing at {at}, {look}: into the parapet at {to}";
                }
            }
            return null;
        }
        finally
        {
            if (other is { } o) _city.World.Destroy(o);
            _city.Remove(body, session);
        }
    }

    [Fact]
    public void SeanStandingInTheCornerIsNotMovedByCodyLookingAbout()
    {
        var roof = _city.Roofs.Single(r => r.Tower == "Kestrel House");
        Assert.Equal(18.25f, roof.Y, 2);
        // Cody 0.65 m south of him and a little west: clear of him while both stand upright.
        string? wrong = StandStill(Sean, Sean + new Vector3(-0.2f, 0f, -0.65f), (roof.X0, roof.X1, roof.Z0, roof.Z1, roof.Y));
        Assert.True(wrong == null, wrong);
    }

    /// <summary>Every tower: against the middle of each parapet and in each corner, with somebody a
    /// body's width further in.</summary>
    [Fact]
    public void NobodyStandingOnAnyRoofIsMovedBySomebodyBesideThem()
    {
        Assert.Equal(5, _city.Roofs.Count);
        var failures = new List<string>();
        foreach (var (tower, x0, x1, z0, z1, y) in _city.Roofs)
        {
            float r = PlayerRadius + 0.001f, gap = 0.65f;
            float xm = (x0 + x1) / 2f, zm = (z0 + z1) / 2f;
            var spots = new (Vector3 At, Vector3 In)[]
            {
                (new(x0 + r, y, zm), new(1, 0, 0)), (new(x1 - r, y, zm), new(-1, 0, 0)),
                (new(xm, y, z0 + r), new(0, 0, 1)), (new(xm, y, z1 - r), new(0, 0, -1)),
                (new(x0 + r, y, z0 + r), Vector3.Normalize(new(1, 0, 1))), (new(x1 - r, y, z0 + r), Vector3.Normalize(new(-1, 0, 1))),
                (new(x0 + r, y, z1 - r), Vector3.Normalize(new(1, 0, -1))), (new(x1 - r, y, z1 - r), Vector3.Normalize(new(-1, 0, -1))),
            };
            foreach (var (at, inward) in spots)
            {
                // Diagonally in from a corner a body's width is further than straight in.
                var beside = at + inward * (gap * (MathF.Abs(inward.X) > 0.1f && MathF.Abs(inward.Z) > 0.1f ? 1.45f : 1f));
                string? wrong = StandStill(at, beside, (x0, x1, z0, z1, y), ticks: 12);
                _o.WriteLine($"{tower} at {at}: {wrong ?? "held"}");
                if (wrong != null) failures.Add($"{tower}: {wrong}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Each roof door swung open and shut by somebody coming out, with a player standing still all
    /// round the outside of it: the leaf stops for whoever is in its way (DoorSystem.InTheWay), so
    /// nobody standing there is moved, and nobody is put into the housing or over the parapet.
    /// </summary>
    [Fact]
    public void ARoofDoorSwingingOpenAndShutMovesNobodyStandingBesideIt()
    {
        Assert.Equal(5, _city.RoofDoors.Count);
        var failures = new List<string>();
        foreach (var (tower, door) in _city.RoofDoors)
        {
            int stood = 0;
            float widest = 0f;
            var roof = _city.Roofs.Single(r => r.Tower == tower);
            DoorSystem.Doorway(_city.World, door, out var centre, out var rotation);
            var through = Vector3.Transform(Vector3.UnitZ, rotation);
            var across = Vector3.Transform(Vector3.UnitX, rotation);
            // Which way is the roof: the side of the doorway that is not the housing.
            var roofMiddle = new Vector3((roof.X0 + roof.X1) / 2f, centre.Y, (roof.Z0 + roof.Z1) / 2f);
            if (Vector3.Dot(roofMiddle - centre, through) < 0f) through = -through;
            var inside = centre - through * 1.2f;
            inside.Y = roof.Y;

            for (float out_ = 0.45f; out_ <= 1.5f; out_ += 0.35f)
            for (float side = -1.2f; side <= 1.21f; side += 0.4f)
            {
                var at = centre + through * out_ + across * side;
                at.Y = roof.Y;
                if (at.X < roof.X0 + PlayerRadius || at.X > roof.X1 - PlayerRadius
                    || at.Z < roof.Z0 + PlayerRadius || at.Z > roof.Z1 - PlayerRadius) continue;
                var (body, session) = _city.Place(at);
                var hand = _city.Bystander(inside, 0f, 0f);
                var doors = new DoorSystem();
                try
                {
                    for (int t = 0; t < 3; t++) _city.Tick(session, t + 1);
                    var start = _city.World.Get<Transform>(body).Position;
                    if (Vector3.Distance(start, at) > StillMetres) continue;   // in the housing's wall: not a place to stand
                    long seq = 10;
                    string? wrong = null;
                    stood++;
                    foreach (bool open in new[] { true, false })
                    {
                        DoorSystem.Set(_city.World, door, open, who: hand);
                        for (int t = 0; t < 90 && wrong == null; t++)
                        {
                            doors.Update(_city.World, FixedDeltaTime, _ => { });
                            widest = MathF.Max(widest, _city.World.Get<DoorComponent>(door).Openness);
                            var from = _city.World.Get<Transform>(body).Position;
                            _city.Tick(session, seq++);
                            var to = _city.World.Get<Transform>(body).Position;
                            if (Vector3.Distance(from, to) > StillMetres)
                                wrong = $"{tower}: standing at {at}, the door {(open ? "opening" : "shutting")} moved them {from} to {to}";
                        }
                    }
                    if (wrong != null) failures.Add(wrong);
                }
                finally
                {
                    DoorSystem.Set(_city.World, door, false);
                    for (int t = 0; t < 120; t++) doors.Update(_city.World, FixedDeltaTime, _ => { });
                    _city.World.Destroy(hand);
                    _city.Remove(body, session);
                }
            }
            _o.WriteLine($"{tower}: {stood} places beside the door, the leaf opened as far as {widest:F2}");
            Assert.True(stood >= 10, $"{tower}: only {stood} places to stand beside the roof door");
            Assert.True(widest > 0.9f, $"{tower}: the roof door never opened ({widest:F2})");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The engine on its own: a body touching a 35 cm wall, with something overlapping it from the other
    /// side by 0.47 m (what Cody's tipped box did to Sean). Pushed out of that into the wall, then out
    /// of the wall back into it, the third pass left it inside the wall, and the next tick the other
    /// way: half a metre a tick, for ever. It now stays where it stood, never in the wall.
    /// </summary>
    [Fact]
    public void APushIntoAWallLeavesTheBodyWhereItStood()
    {
        var colliders = new SharedMovementEngine.Collider[]
        {
            new() { Position = new Vector3(0f, 1f, 0.175f), Size = new Vector3(4f, 2f, 0.35f), Rotation = Quaternion.Identity, Material = "Brick" },
            new() { Position = new Vector3(0f, 0.9f, -0.434f), Size = PlayerSize, Rotation = Quaternion.Identity, Material = "Skin" },
        };
        var at = new Vector3(0f, 0f, -PlayerRadius - SharedMovementEngine.CollisionSkinWidth);
        var vel = Vector3.Zero;
        for (int tick = 0; tick < 20; tick++)
        {
            var (p, v, _) = SharedMovementEngine.Step(new SharedMovementEngine.MovementContext
            {
                Position = at, Velocity = vel, InputDirection = Vector3.Zero, DeltaTime = FixedDeltaTime,
                GroundHeight = 0f, Gravity = Gravity, JumpForce = JumpPower, Speed = WalkSpeed,
                PlayerRadius = PlayerRadius, PlayerHeight = PlayerHeight, StepHeight = StepHeight,
                MapMin = new Vector3(-600, -50, -600), MapMax = new Vector3(600, 900, 600),
            }, colliders);
            Assert.True(p.Z <= -PlayerRadius + 0.005f, $"tick {tick}: pushed into the wall, z {p.Z:F3}");
            Assert.True(Vector3.Distance(p, at) <= StillMetres, $"tick {tick}: moved {at} to {p}");
            at = p; vel = v;
        }
    }

    /// <summary>A person's collider stands upright whatever way they look; anything else keeps its
    /// own orientation.</summary>
    [Fact]
    public void APersonStandsUprightWhereverTheyLook()
    {
        var look = Quaternion.CreateFromYawPitchRoll(0.7f, 0.785f, 0f);
        Assert.Equal(Quaternion.Identity, SharedMovementEngine.StandingRotation(ColliderShape.Cylinder, look));
        Assert.Equal(look, SharedMovementEngine.StandingRotation(ColliderShape.Box, look));
    }
}
