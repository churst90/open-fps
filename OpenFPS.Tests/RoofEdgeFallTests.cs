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
/// A fall ends on the floor, where the body came down. Sean (2026-10-04) was just outside the west
/// parapet of Brandt Court at roof height, fell the eighteen metres, and on landing was put 489 m
/// west at the edge of the map in a single tick: the fall carried his feet a tick deep into the
/// ground box the city stands on, and collision pushed him out of it sideways to its nearest edge.
/// Cody's drop onto the same roof was put 0.85 m off the slab's end the same way. These walk the
/// real city through the real server movement, from where Sean was.
/// </summary>
public class RoofEdgeFallTests
{
    private readonly ITestOutputHelper _o;
    public RoofEdgeFallTests(ITestOutputHelper o) => _o = o;

    // Brandt Court's outer faces, engine X east / Z north, and its roof.
    private const float West = -30.75f, East = -9.5f, South = 148f, North = 236f, Roof = 18.25f;

    private sealed class City
    {
        public required MapManager Maps;
        public required World World;
        public required SpatialGrid<Entity> Grid;
        public required Dictionary<int, Entity> Lookup;
        public required MapData Data;
        public readonly SessionManager Sessions = new();
        private int _conn = 700;

        public (Entity Body, UserSession Session) Place(Vector3 at)
        {
            int conn = ++_conn;
            var session = new UserSession { ConnectionId = conn, Username = "w" + conn, CurrentMapId = "city" };
            Sessions.AddSession(conn, session);
            var body = World.Create(
                new PlayerComponent { Username = "w" + conn, ConnectionId = conn },
                new Transform { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
                new Velocity(), new MaterialComponent(),
                new ColliderComponent { Shape = ColliderShape.Cylinder, Size = PlayerSize, IsSolid = true });
            return (body, session);
        }

        public void Remove(Entity body, UserSession session)
        {
            World.Destroy(body);
            Sessions.TryRemoveSession(session.ConnectionId, out _);
        }

        /// <summary>One server tick with one input, as Program.cs runs it: the moving things re-gridded,
        /// then MovementSystem.</summary>
        public void Tick(UserSession session, long seq, Vector3 move, bool sprint, bool jump)
        {
            session.InputQueue.Enqueue(new ClientInputUpdate
            {
                SequenceId = seq, MoveDirection = move, DeltaTime = FixedDeltaTime, Sprint = sprint, Jump = jump,
            });
            Grid.Clear();
            World.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                (Entity e, ref Transform t, ref ColliderComponent c) => Grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
            MovementSystem.Update(World, Data.WalkMin, Data.WalkMax, Grid, Lookup, Sessions, Maps, FixedDeltaTime);
        }
    }

    private static City LoadCity()
    {
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")),
                                  new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var world, out _, out var grid, out var lookup));
        Assert.True(maps.TryGetMapData("city", out var data));
        return new City { Maps = maps, World = world, Grid = grid, Lookup = lookup, Data = data };
    }

    private static Vector3 Compass(int degrees)
    {
        float a = degrees * MathF.PI / 180f;
        return new Vector3(MathF.Sin(a), 0f, MathF.Cos(a));
    }

    private static bool OutsideTheBuilding(Vector3 p) => p.X < West || p.X > East || p.Z < South || p.Z > North;

    /// <summary>A body running round a building's corner is slid out of it by up to the depth it
    /// pressed in, so a tick can carry it a little further than a run. Nothing more than that.</summary>
    private const float MostInATick = SprintSpeed * 1.25f;

    /// <summary>
    /// From where Sean was when it happened, every way and at both paces: off the side, down, and
    /// standing on the ground beside the building — never faster across the ground than a run, never
    /// held up in the air beside the roof, never at the edge of the map.
    /// </summary>
    [Fact]
    public void WalkingOffTheWestSideFallsAndLandsBesideTheBuilding()
    {
        var city = LoadCity();
        // Free fall from the roof to fifteen metres, and a tick to spare.
        int fallTicks = (int)MathF.Ceiling(MathF.Sqrt(2f * (Roof - 15f) / Gravity) / FixedDeltaTime) + 2;
        var sean = new Vector3(-31.051f, 18.150002f, 191.884f);

        foreach (var start in new[] { sean, new Vector3(-31.4f, Roof, 191.884f) })
        for (int degrees = 0; degrees < 360; degrees += 45)
        foreach (bool sprint in new[] { false, true })
        {
            var (body, session) = city.Place(start);
            try
            {
                int high = 0, mostHigh = 0;
                for (int tick = 0; tick < 240; tick++)
                {
                    var from = city.World.Get<Transform>(body).Position;
                    city.Tick(session, tick + 1, Compass(degrees), sprint, jump: false);
                    var to = city.World.Get<Transform>(body).Position;
                    float flat = new Vector2(to.X - from.X, to.Z - from.Z).Length() / FixedDeltaTime;
                    Assert.True(flat <= MostInATick,
                        $"from {start} heading {degrees}, {(sprint ? "running" : "walking")}: {flat:F0} m/s across the ground at tick {tick}, {from} to {to}");
                    high = OutsideTheBuilding(to) && to.Y > 15f ? high + 1 : 0;
                    mostHigh = Math.Max(mostHigh, high);
                }
                var end = city.World.Get<Transform>(body).Position;
                Assert.True(mostHigh <= fallTicks, $"held up beside the roof for {mostHigh} ticks (a fall takes {fallTicks})");
                Assert.True(end.Y < 1f, $"heading {degrees}: still {end.Y:F1} m up after 8 s");
                Assert.True(end.X > city.Data.WalkMin.X + 5f, $"heading {degrees}: ended at the map's edge, {end}");
                Assert.True(Vector2.Distance(new(end.X, end.Z), new(start.X, start.Z)) <= SprintSpeed * 8f + 1f,
                    $"heading {degrees}: {end} is further than eight seconds' run from {start}");
            }
            finally { city.Remove(body, session); }
        }
    }

    /// <summary>
    /// Dropped onto the roof from high up, as /tp with a height does, the body lands where it fell:
    /// in the middle of the roof, and three-quarters of a metre in from the west edge, where the old
    /// push went out over the parapet to the nearest end of the slab.
    /// </summary>
    [Theory]
    [InlineData(-25f, 200f, 100f)]
    [InlineData(-30f, 200f, 100f)]   // 0.75 m from the slab's west end
    [InlineData(-25f, 234.9f, 40f)]  // 1.1 m from its north end
    public void ADropOntoTheRoofLandsWhereItFell(float x, float z, float height)
    {
        var city = LoadCity();
        var (body, session) = city.Place(new Vector3(x, height, z));
        try
        {
            for (int tick = 0; tick < 150; tick++)
                city.Tick(session, tick + 1, Vector3.Zero, sprint: false, jump: false);
            var end = city.World.Get<Transform>(body).Position;
            _o.WriteLine($"dropped from {height} m at {x}, {z}: {end}");
            Assert.Equal(Roof, end.Y, 2);
            Assert.Equal(x, end.X, 2);
            Assert.Equal(z, end.Z, 2);
            Assert.True(city.World.Get<PlayerComponent>(body).IsGrounded);
        }
        finally { city.Remove(body, session); }
    }

    private static SharedMovementEngine.MovementContext Falling(Vector3 at, float vy, float ground) => new()
    {
        Position = at, Velocity = new Vector3(0, vy, 0), InputDirection = Vector3.Zero, DeltaTime = FixedDeltaTime,
        GroundHeight = ground, Gravity = Gravity, JumpForce = JumpPower, Speed = WalkSpeed,
        PlayerRadius = PlayerRadius, PlayerHeight = PlayerHeight, StepHeight = StepHeight,
        MapMin = new Vector3(-600, -50, -600), MapMax = new Vector3(600, 900, 600),
    };

    /// <summary>The ground the whole city stands on: a kilometre of box, its west end 489 m away.</summary>
    private static readonly SharedMovementEngine.Collider[] Ground =
    {
        new() { Position = new Vector3(0f, -0.5f, 0f), Size = new Vector3(1040f, 1f, 1040f),
                Rotation = Quaternion.Identity, Material = "Dirt" },
    };

    /// <summary>A body arriving at nineteen metres a second, a tick above the ground, stops on it.</summary>
    [Fact]
    public void AFastFallStopsOnTheFloorItKnowsAbout()
    {
        var at = new Vector3(-31.051f, 0.4f, 191.884f);
        var (p, v, grounded) = SharedMovementEngine.Step(Falling(at, -19f, ground: 0f), Ground);
        Assert.True(grounded);
        Assert.Equal(0f, p.Y, 3);
        Assert.Equal(at.X, p.X, 3);
        Assert.Equal(at.Z, p.Z, 3);
        Assert.Equal(0f, v.Y);
    }

    /// <summary>And one already in a floor with its centre over it — the probe looked somewhere else, or
    /// something put it there — comes out of the top, not the nearest end.</summary>
    [Fact]
    public void ABodyInAFloorComesOutOfTheTop()
    {
        var at = new Vector3(-31.051f, -0.5f, 191.884f);
        var (p, _, grounded) = SharedMovementEngine.Step(Falling(at, -10f, ground: DefaultGroundCheckLimit - 1f), Ground);
        Assert.Equal(at.X, p.X, 3);
        Assert.Equal(at.Z, p.Z, 3);
        Assert.InRange(p.Y, -0.01f, 0.01f);
        Assert.True(grounded);
    }

    /// <summary>
    /// Walking and jumping about up there does not take you over the side: the parapet holds on all
    /// four faces, every way at both paces, and nobody is ever moved faster than a run.
    /// </summary>
    [Fact]
    public void TheParapetHoldsAWalkerOnTheRoof()
    {
        var city = LoadCity();
        foreach (var start in new[] { new Vector3(-25.7f, Roof, 160f), new Vector3(-29.5f, Roof, 191.9f), new Vector3(-20f, Roof, 234f) })
        for (int degrees = 0; degrees < 360; degrees += 45)
        foreach (bool sprint in new[] { false, true })
        {
            var (body, session) = city.Place(start);
            try
            {
                for (int tick = 0; tick < 300; tick++)
                {
                    var from = city.World.Get<Transform>(body).Position;
                    city.Tick(session, tick + 1, Compass(degrees), sprint, jump: tick % 12 == 0);
                    var to = city.World.Get<Transform>(body).Position;
                    float flat = new Vector2(to.X - from.X, to.Z - from.Z).Length() / FixedDeltaTime;
                    Assert.True(flat <= MostInATick, $"{flat:F0} m/s at tick {tick}, {from} to {to}");
                    Assert.False(OutsideTheBuilding(to), $"from {start} heading {degrees}: over the side at {to}");
                    Assert.True(to.Y >= Roof - 0.01f, $"from {start} heading {degrees}: sank to {to}");
                }
            }
            finally { city.Remove(body, session); }
        }
    }
}
