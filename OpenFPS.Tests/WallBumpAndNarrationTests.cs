using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Walking into things and turning to face them (Cody, 2026-10-04): "when you run into walls, we need
/// a bump sound and I need to hear what I ran into ... ground doesn't need to be announced, that's
/// what z is for ... maybe an auto narration so as i turn my head i know what is in front of me".
///
/// The bumps are driven through the client's own prediction — ClientPhysicsSystem over the shared
/// movement engine, through the reconciler, a tick at a time — so a contact is whatever the real step
/// reports, and the walls are the prefabs' own sizes: two-metre plasterboard panels 12 cm thick.
/// </summary>
public class WallBumpAndNarrationTests
{
    private readonly ITestOutputHelper _o;
    public WallBumpAndNarrationTests(ITestOutputHelper o) => _o = o;

    private const float Dt = PhysicsConstants.FixedDeltaTime;

    // ── A little world ──────────────────────────────────────────────────────────────────────────

    private static EntitySnapshot Box(int id, Vector3 centre, Vector3 size, string name = "", string material = "Plaster",
                                      Quaternion? rotation = null, bool solid = true)
        => new()
        {
            Id = id,
            Definition = new EntityDefinition
            {
                EntityId = id,
                Type = EntityType.StaticObject,
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = solid },
                Identity = new IdentityComponent { Name = name },
                Material = new MaterialComponent { Material = material },
            },
            Transform = new Transform { Position = centre, Rotation = rotation ?? Quaternion.Identity, Scale = Vector3.One },
        };

    private static WorldSnapshot World(params EntitySnapshot[] boxes)
    {
        var w = new WorldSnapshot();
        foreach (var b in boxes) w.Entities[b.Id] = b;
        return w;
    }

    /// <summary>The floor everything stands on: its top at y = 0.</summary>
    private static EntitySnapshot Floor() => Box(1, new Vector3(0, -0.1f, 0), new Vector3(60, 0.2f, 60), "Concrete Floor", "Concrete");

    /// <summary>A wall of plasterboard panels (the plaster_wall prefab, 2 x 3 x 0.12) along z = <paramref name="z"/>,
    /// from x = <paramref name="x0"/> for <paramref name="panels"/> panels.</summary>
    private static IEnumerable<EntitySnapshot> WallAlongX(int firstId, float z, float x0, int panels, string name)
        => Enumerable.Range(0, panels).Select(i =>
            Box(firstId + i, new Vector3(x0 + 1f + 2f * i, 1.5f, z), new Vector3(2f, 3f, 0.12f), name));

    /// <summary>A body walking, a tick at a time, as the session drives it: fresh prediction steps,
    /// each contact resolved and counted.</summary>
    private sealed class Walker
    {
        public readonly LocalPlayerState State = new();
        private readonly ClientPhysicsSystem _physics;
        private readonly PredictionReconciler _reconciler;
        private readonly WallBumps _bumps = new();
        private readonly WorldSnapshot _world;
        private long _seq;
        public readonly List<(int Id, string Name)> Bumps = new();
        public int Contacts;

        public Walker(WorldSnapshot world, Vector3 feet, float yaw = 0f)
        {
            _world = world;
            State.Position = feet;
            State.Yaw = yaw;
            State.Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0);
            _physics = new ClientPhysicsSystem(State, new SpatialService()) { OwnEntityId = 999, MapMin = new(-500, -50, -500), MapMax = new(500, 100, 500) };
            _reconciler = new PredictionReconciler(State, _physics);
        }

        public void Walk(Vector3 direction, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                var input = new ClientInputUpdate { SequenceId = ++_seq, DeltaTime = Dt,
                    MoveDirection = direction == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(direction) };
                _reconciler.Step(input, _world, Dt);
                var contact = WallBumps.Resolve(_reconciler.LastContact, _world, State.EyeHeight, out var struck);
                if (contact != null) Contacts++;
                if (_bumps.Update(contact, State.Position)) Bumps.Add((struck.Id, Sightline.NameOf(struck)));
            }
        }
    }

    private static readonly Vector3 Forward = new(0, 0, 1);
    private static readonly Vector3 Back = new(0, 0, -1);

    // ── Bumps ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WalkingIntoAWallBumpsOnceAndOnlyAgainAfterBackingAway()
    {
        var world = World(new[] { Floor() }.Concat(WallAlongX(10, 3f, -3f, 3, "Brandt Court, north wall")).ToArray());
        var w = new Walker(world, new Vector3(0, 0, 0));

        w.Walk(Forward, 60);                 // two seconds: into it, and on pressing
        Assert.Single(w.Bumps);
        Assert.Equal("Brandt Court, north wall", w.Bumps[0].Name);
        Assert.True(w.Contacts > 20, $"only {w.Contacts} ticks against the wall: the test never pressed on it");
        Assert.InRange(w.State.Position.Z, 2.6f, 2.65f);

        w.Walk(Vector3.Zero, 10);            // let go...
        w.Walk(Forward, 20);                 // ...and press again: the same contact
        Assert.Single(w.Bumps);

        w.Walk(Back, 1);                     // one tap back, 15 cm, and in again: still the same contact
        w.Walk(Forward, 10);
        Assert.Single(w.Bumps);

        w.Walk(Back, 8);                     // a metre clear, and in again: a new one
        w.Walk(Forward, 20);
        Assert.Equal(2, w.Bumps.Count);
    }

    /// <summary>One tap of a movement key — one tick, 15 cm — into a wall a hand's breadth away.</summary>
    [Fact]
    public void ATapIntoAWallBumps()
    {
        var world = World(new[] { Floor() }.Concat(WallAlongX(10, 3f, -3f, 3, "Partition Wall")).ToArray());
        var w = new Walker(world, new Vector3(0, 0, 2.94f - 0.3f - 0.08f));
        w.Walk(Forward, 1);
        Assert.Single(w.Bumps);
    }

    [Fact]
    public void BrushingAlongAWallNeverBumps()
    {
        // Twelve metres of wall, six panels; start a centimetre off it and walk along it, leaning in.
        var world = World(new[] { Floor() }.Concat(WallAlongX(10, 3f, -2f, 6, "Partition Wall")).ToArray());
        var w = new Walker(world, new Vector3(0, 0, 2.94f - 0.3f - 0.01f));
        w.Walk(new Vector3(1, 0, 0.2f), 60);
        Assert.Empty(w.Bumps);
        Assert.True(w.Contacts > 20, $"only {w.Contacts} ticks against the wall: the walk never touched it");
        Assert.True(w.State.Position.X > 6f, "the body did not slide along the wall");
    }

    /// <summary>Met at forty-five degrees it is met; sliding on along it across panel after panel is
    /// the same wall.</summary>
    [Fact]
    public void MeetingAWallAtAnAngleBumpsOnceThenSlides()
    {
        var world = World(new[] { Floor() }.Concat(WallAlongX(10, 3f, -2f, 6, "Partition Wall")).ToArray());
        var w = new Walker(world, new Vector3(0, 0, 1.5f));
        w.Walk(new Vector3(1, 0, 1), 60);
        Assert.Single(w.Bumps);
        Assert.True(w.State.Position.X > 5f, "the body did not slide along the wall");
    }

    /// <summary>A corner is two walls, and each is met once.</summary>
    [Fact]
    public void ACornerIsTwoThings()
    {
        var side = Box(30, new Vector3(1.56f, 1.5f, 2f), new Vector3(2f, 3f, 0.12f), "Brandt Court, east wall",
                       rotation: Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0, 0));
        var world = World(new[] { Floor(), side }.Concat(WallAlongX(10, 3f, -3f, 3, "Brandt Court, north wall")).ToArray());
        var w = new Walker(world, new Vector3(0, 0, 1.5f));
        w.Walk(new Vector3(1, 0, 1), 60);
        foreach (var b in w.Bumps) _o.WriteLine($"bumped {b.Name}");
        Assert.Equal(2, w.Bumps.Count);
        Assert.Contains(w.Bumps, b => b.Name.EndsWith("north wall"));
        Assert.Contains(w.Bumps, b => b.Name.EndsWith("east wall"));
    }

    /// <summary>Up a flight of stairs built as the city builds them — each step a column from the
    /// floor to its tread — and over a kerb: climbed, never bumped.</summary>
    [Fact]
    public void ClimbingStairsAndKerbsNeverBumps()
    {
        const float rise = 0.178f, going = 0.28f;
        var boxes = new List<EntitySnapshot> { Floor() };
        // A kerb: the pavement 12 cm proud of the road.
        boxes.Add(Box(5, new Vector3(0, 0.06f, 1.5f), new Vector3(4f, 0.12f, 1f), "Main Street sidewalk", "Concrete"));
        for (int i = 0; i < 10; i++)
        {
            float top = (i + 1) * rise;
            boxes.Add(Box(100 + i, new Vector3(0, top / 2f, 3f + going * (i + 0.5f)), new Vector3(1.2f, top, going), "Concrete Floor", "Concrete"));
        }
        var w = new Walker(World(boxes.ToArray()), new Vector3(0, 0, 0));
        float highest = 0f;
        for (int i = 0; i < 45; i++)          // up the flight, over its top and off the far end
        {
            w.Walk(Forward, 1);
            highest = MathF.Max(highest, w.State.Position.Y);
        }
        foreach (var b in w.Bumps) _o.WriteLine($"bumped {b.Name} e{b.Id}");
        Assert.Empty(w.Bumps);
        Assert.True(highest > 1.7f, $"the body did not climb the flight: highest feet {highest:F2}");
    }

    /// <summary>
    /// Off the edge of a roof and down onto the ground, walking: a fall that ends on a floor is pushed
    /// out of its top (the engine's landing), and that is never a bump — nor is the roof slab's edge
    /// brushed on the way down.
    /// </summary>
    [Fact]
    public void FallingOffARoofOntoTheGroundNeverBumps()
    {
        var roof = Box(2, new Vector3(0, 2.875f, 0), new Vector3(10f, 0.25f, 10f), "Brandt Court roof", "Concrete");
        var w = new Walker(World(Floor(), roof), new Vector3(0, 3f, 3f));
        float lowest = 3f;
        for (int i = 0; i < 90; i++)
        {
            w.Walk(Forward, 1);
            lowest = MathF.Min(lowest, w.State.Position.Y);
        }
        Assert.InRange(lowest, -0.01f, 0.01f);
        Assert.Empty(w.Bumps);
        // ...and dropped straight onto it from four metres, standing still.
        var drop = new Walker(World(Floor()), new Vector3(0, 4f, 0));
        drop.Walk(Vector3.Zero, 60);
        drop.Walk(Forward, 10);
        Assert.Empty(drop.Bumps);
    }

    /// <summary>The knock is the impact model's: a body against plasterboard, placed on the wall at
    /// about shoulder height, and no louder than a firm footstep's neighbourhood.</summary>
    [Fact]
    public void TheKnockIsABodyAgainstTheWall()
    {
        var panel = WallAlongX(10, 3f, -1f, 1, "Partition Wall").Single();
        var c = new BodyContact(10, new Vector3(0, 0, -1), PhysicsConstants.WalkSpeed, PhysicsConstants.WalkSpeed, new Vector3(0, 0, 2.63f));
        var where = WallBumps.TouchPoint(panel, c);
        Assert.InRange(where.Z, 2.9f, 2.95f);
        Assert.InRange(where.Y, 1.2f, 1.5f);
        var sounds = WallBumps.Sound(panel, c, where, running: false);
        Assert.NotEmpty(sounds);
        _o.WriteLine(string.Join("; ", sounds.Select(s => $"{s.Character} {s.Hz:F0} Hz {s.LevelDb:F1} dB {s.DecaySeconds:F2} s")));
        Assert.Equal(SoundCharacter.Knock, sounds[0].Character);
        Assert.InRange(sounds[0].LevelDb, 65f, 85f);
        // A glancing blow is softer than a square one.
        var glancing = WallBumps.Sound(panel, c with { IntoSpeed = PhysicsConstants.WalkSpeed * 0.5f }, where, running: false);
        Assert.True(glancing[0].LevelDb < sounds[0].LevelDb);
    }

    // ── Bumping into somebody ───────────────────────────────────────────────────────────────────
    //
    // "When I bump into someone it says 'something' and am I made of concrete too?" (Cody, 2026-10-05).
    // A player's definition carried no name, so the bump fell through to "something"; and their
    // material was "Generic" — five gigapascals, ringing — or, once they had moved, the floor under them.

    /// <summary>Somebody as the server now sends them: their name, a body's material, upright, and
    /// looking wherever they happen to be looking.</summary>
    private static EntitySnapshot Person(int id, Vector3 feet, string name, EntityType type = EntityType.Player,
                                         string material = PhysicsConstants.PersonMaterial)
        => new()
        {
            Id = id,
            Definition = new EntityDefinition
            {
                EntityId = id,
                Type = type,
                Collider = new ColliderComponent { Shape = ColliderShape.Cylinder, Size = PhysicsConstants.PlayerSize, IsSolid = true },
                Identity = new IdentityComponent { Name = name },
                Material = new MaterialComponent { Material = material },
                Moves = true,
            },
            Transform = new Transform { Position = feet, Rotation = Quaternion.CreateFromYawPitchRoll(2.5f, 0.6f, 0f), Scale = Vector3.One },
        };

    [Fact]
    public void WalkingIntoSomebodySaysWhoTheyAre()
    {
        var world = World(Floor(), Person(50, new Vector3(0, 0, 2f), "seanterry01"));
        var w = new Walker(world, Vector3.Zero);
        w.Walk(Forward, 30);
        var bump = Assert.Single(w.Bumps);
        Assert.Equal((50, "seanterry01"), bump);
    }

    [Fact]
    public void APersonIsSomebodyNotSomething()
    {
        Assert.Equal("Pedestrian", Sightline.NameOf(Person(51, Vector3.Zero, "Pedestrian, Main Street, west side 14", EntityType.NPC)));
        Assert.Equal("someone", Sightline.NameOf(Person(52, Vector3.Zero, "", EntityType.NPC)));
        Assert.Equal("someone", Sightline.NameOf(Person(53, Vector3.Zero, "")));
        // ...and an unnamed wall is still a thing made of something.
        Assert.Equal("something brick", Sightline.NameOf(Box(54, Vector3.Zero, new Vector3(2, 3, 0.2f), "", "Brick")));
    }

    /// <summary>A body against a body: one soft, low thud with nothing ringing after it. (The knock
    /// was soft before as well — the hand is the softer of the two and decides it — so what made Sean
    /// "concrete" was the client calling him "something concrete", his definition having carried the
    /// floor he stood on as his material.)</summary>
    [Fact]
    public void BumpingIntoSomebodyIsASoftBodyContact()
    {
        var c = new BodyContact(50, new Vector3(0, 0, -1), PhysicsConstants.WalkSpeed, PhysicsConstants.WalkSpeed, new Vector3(0, 0, 1.4f));
        var sean = Person(50, new Vector3(0, 0, 2f), "seanterry01");
        var where = WallBumps.TouchPoint(sean, c);
        var body = WallBumps.Sound(sean, c, where, running: false);
        _o.WriteLine(string.Join("; ", body.Select(s => $"{s.Character} {s.Hz:F0} Hz {s.LevelDb:F1} dB {s.DecaySeconds:F2} s")));
        var knock = Assert.Single(body);
        Assert.Equal(SoundCharacter.Knock, knock.Character);
        Assert.InRange(knock.Hz, 40f, 200f);
        Assert.InRange(knock.DecaySeconds, 0.1f, 0.6f);
        Assert.InRange(knock.LevelDb, 55f, 85f);
        // Unnamed and made of the floor, as the old definition had him, he was "something concrete".
        Assert.Equal("someone", Sightline.NameOf(Person(50, Vector3.Zero, "", material: "Concrete")));
    }

    /// <summary>The real city: Brandt Court's ground-floor corridor, walked across into its wall.</summary>
    [Fact]
    public void BrandtCourtCorridorWallBumpsOnce()
    {
        var world = City();
        float ground = PhysicsUtils.GetGroundHeight(world, new Vector3(-19.95f, 1f, 192f), -1, out _);
        Assert.InRange(ground, -0.5f, 0.5f);
        foreach (var dir in new[] { new Vector3(1, 0, 0), new Vector3(-1, 0, 0) })
        {
            var w = new Walker(world, new Vector3(-19.95f, ground, 192f), yaw: 0f);
            w.Walk(dir, 45);
            foreach (var b in w.Bumps) _o.WriteLine($"{dir.X:+0;-0}: bumped '{b.Name}' e{b.Id} at {w.State.Position}");
            Assert.Single(w.Bumps);
            Assert.True(w.Contacts > 10);
            Assert.False(world.Entities[w.Bumps[0].Id].Definition.Collider.Size.Y < 1f, "bumped something low");
        }
    }

    /// <summary>
    /// Turning round on the spot at places in the city — a corridor, a stairwell's foot, the roof Cody
    /// stood on, a pavement — never names a floor, a roof, a road or the ground: nothing built from a
    /// floor prefab is named unless it stands up like a wall.
    /// </summary>
    [Theory]
    [InlineData(-19.95f, 192f)]      // Brandt Court corridor, floor 0
    [InlineData(-13.05f, 155.3f)]    // Brandt Court stairwell, at the foot of the first flight
    [InlineData(-20.1f, 185f)]       // Brandt Court roof (feet found from the top down)
    [InlineData(0f, 100f)]           // the open ground in the middle of the map
    public void TurningRoundInTheCityNeverNamesTheGround(float x, float z)
    {
        var world = City();
        var spatial = new SpatialService();
        bool roof = x == -20.1f;
        float feet = PhysicsUtils.GetGroundHeight(world, new Vector3(x, roof ? 25f : 1f, z), -1, out _);
        if (roof) feet = PhysicsUtils.GetGroundHeight(world, new Vector3(x, 20f, z), -1, out _);
        for (int k = 0; k < 8; k++)
        {
            float yaw = k * MathF.PI / 4f;
            var seen = Sightline.AheadLevel(spatial, world, new Vector3(x, feet, z), yaw, 1.7f, -1);
            string line = Sightline.NarrationLine(seen, $"{k * 45} degrees");
            _o.WriteLine($"({x}, {feet:F2}, {z}) facing {k * 45}: {line}");
            if (seen is not { } s) continue;
            string prefab = s.Entity.Definition.Identity.PrefabId ?? "";
            if (prefab.EndsWith("_floor") || prefab.EndsWith("_road"))
            {
                var (min, max) = Sightline.WorldBounds(s.Entity);
                Assert.True(max.Y - min.Y > MathF.Min(max.X - min.X, max.Z - min.Z) && max.Y > feet + 1.8f,
                    $"named the {prefab} '{s.Name}' ({min} to {max}) facing {k * 45} from feet at {feet:F2}");
            }
        }
    }

    private static WorldSnapshot? _city;
    private static readonly object CityLock = new();

    internal static WorldSnapshot City()
    {
        lock (CityLock)
        {
            if (_city != null) return _city;
            AcousticRegistry.Initialize();
            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
            maps.Initialize();
            Assert.True(maps.TryGetMap("city", out var ecs, out _, out _, out _));
            var world = new WorldSnapshot { StaticGrid = new SpatialGrid<int>(10f) };
            foreach (var def in EntityDefinitionFactory.StaticDefinitions(ecs))
            {
                world.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
                if (def.Type == EntityType.StaticObject && !def.Moves && def.Collider.IsSolid)
                    world.StaticGrid.AddOverlapping(def.Transform.Position, def.Collider.Size, def.Transform.Rotation, def.EntityId, isStatic: true);
            }
            return _city = world;
        }
    }

    // ── The ground is never named ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheGroundIsDecidedByGeometry()
    {
        float feet = 0f, eye = 1.7f;
        Assert.True(Sightline.IsGround(Floor(), feet, eye));
        // The roof you stand on, named as the generator names it.
        Assert.True(Sightline.IsGround(Box(2, new Vector3(0, -0.125f, 0), new Vector3(21f, 0.25f, 47f), "Brandt Court roof", "Concrete"), feet, eye));
        // A kerb, and a pavement 12 cm up.
        Assert.True(Sightline.IsGround(Box(3, new Vector3(0, 0.06f, 2), new Vector3(4f, 0.12f, 1f), "Main Street sidewalk"), feet, eye));
        // A landing a storey below, looked down at over a balustrade.
        Assert.True(Sightline.IsGround(Box(4, new Vector3(0, -3.1f, 3), new Vector3(3f, 0.2f, 3f), "floor 0"), feet, eye));
        // Not ground: a wall, a parapet, a car-sized box, a ceiling over your head.
        Assert.False(Sightline.IsGround(Box(5, new Vector3(0, 1.5f, 3), new Vector3(2f, 3f, 0.12f), "Partition Wall"), feet, eye));
        Assert.False(Sightline.IsGround(Box(6, new Vector3(0, 0.55f, 3), new Vector3(10f, 1.1f, 0.2f), "parapet"), feet, eye));
        Assert.False(Sightline.IsGround(Box(7, new Vector3(0, 0.75f, 3), new Vector3(1.8f, 1.5f, 4.5f), "a car", "Metal"), feet, eye));
        Assert.False(Sightline.IsGround(Box(8, new Vector3(0, 3.1f, 0), new Vector3(10f, 0.2f, 10f), "floor 1"), feet, eye));
    }

    [Fact]
    public void LookingDownAtTheFloorNamesNothing()
    {
        var world = World(Floor(), Box(2, new Vector3(0, -0.1f, 4), new Vector3(4f, 0.2f, 4f), "Brandt Court roof", "Concrete"));
        var spatial = new SpatialService();
        Vector3 eye = new(0, 1.7f, 0);
        var down = Vector3.Normalize(new Vector3(0, -1f, 1f));
        ReadOnlySpan<Vector3> origin = stackalloc Vector3[] { eye };
        Assert.Null(Sightline.Ahead(spatial, world, origin, down, 20f, 0f, 1.7f, 999));
        Assert.Null(Sightline.AheadLevel(spatial, world, Vector3.Zero, 0f, 1.7f, 999));
    }

    [Fact]
    public void TurningToFaceStairsNamesNoStep()
    {
        const float rise = 0.178f, going = 0.28f;
        var boxes = new List<EntitySnapshot> { Floor() };
        for (int i = 0; i < 17; i++)
        {
            float top = (i + 1) * rise;
            boxes.Add(Box(100 + i, new Vector3(0, top / 2f, 2f + going * (i + 0.5f)), new Vector3(1.2f, top, going), "Concrete Floor", "Concrete"));
        }
        var seen = Sightline.AheadLevel(new SpatialService(), World(boxes.ToArray()), Vector3.Zero, 0f, 1.7f, 999);
        Assert.True(seen == null, $"named '{seen?.Name}' at {seen?.Distance:F1} m");
    }

    [Fact]
    public void TurnNarrationNamesWhatIsAheadAndHowFar()
    {
        var world = World(new[] { Floor() }.Concat(WallAlongX(10, 8.2f, -3f, 3, "Brandt Court front entrance")).ToArray());
        var spatial = new SpatialService();
        var seen = Sightline.AheadLevel(spatial, world, Vector3.Zero, 0f, 1.7f, 999);
        Assert.Equal("Brandt Court front entrance, 8 metres", Sightline.NarrationLine(seen, "North"));
        // Turned away from it, nothing in twenty metres: which way is open.
        var away = Sightline.AheadLevel(spatial, world, Vector3.Zero, MathF.PI, 1.7f, 999);
        Assert.Equal("Open, South", Sightline.NarrationLine(away, "South"));
        // A car's height is found at chest level although it is under the eyes.
        var car = Box(40, new Vector3(0, 0.7f, 5f), new Vector3(1.8f, 1.4f, 4.5f), "a hatchback", "Metal");
        var withCar = World(new[] { Floor(), car }.Concat(WallAlongX(10, 15f, -3f, 3, "Partition Wall")).ToArray());
        Assert.Equal("a hatchback", Sightline.AheadLevel(spatial, withCar, Vector3.Zero, 0f, 1.7f, 999)?.Name);
        Assert.Equal("0.6 metres", Sightline.SpokenDistance(0.62f));
        Assert.Equal("1 metre", Sightline.SpokenDistance(1.2f));
    }

    // ── When the narration speaks ───────────────────────────────────────────────────────────────

    [Fact]
    public void NarrationWaitsForTheHeadingToSettle()
    {
        var n = new TurnNarration();
        double t = 0;
        // A run of three taps a tenth of a second apart, then still.
        var steps = new List<(double T, TurnNarration.Step S)>();
        for (int tick = 0; tick < 30; tick++, t += Dt)
        {
            bool turned = tick is 0 or 3 or 6;
            var s = n.Update(t, turned);
            if (s != TurnNarration.Step.None) steps.Add((t, s));
        }
        Assert.Single(steps);
        Assert.Equal(TurnNarration.Step.Narrate, steps[0].S);
        Assert.InRange(steps[0].T - 6 * Dt, TurnNarration.SettleSeconds - 1e-6, TurnNarration.SettleSeconds + Dt + 1e-6);
        // Nothing more while the head stays still.
        for (int tick = 0; tick < 60; tick++, t += Dt) Assert.Equal(TurnNarration.Step.None, n.Update(t, false));
    }

    [Fact]
    public void TurningAgainCutsOffTheNarrationAndTheNextLineIsSaid()
    {
        var n = new TurnNarration();
        Assert.Equal(TurnNarration.Step.None, n.Update(0.0, true));
        Assert.Equal(TurnNarration.Step.Narrate, n.Update(0.3, false));
        Assert.True(n.Accept("Partition Wall, 3 metres", 0.3));

        // Turning again while it is still being said cuts it off...
        Assert.Equal(TurnNarration.Step.Interrupt, n.Update(0.6, true));
        // ...once, not on every tick of the same turn.
        Assert.Equal(TurnNarration.Step.None, n.Update(0.63, true));
        Assert.Equal(TurnNarration.Step.Narrate, n.Update(0.9, false));
        // The same line after a cut is said again: it was never heard whole.
        Assert.True(n.Accept("Partition Wall, 3 metres", 0.9));

        // Something else said since: the narration is not what is talking, and is not cut.
        Assert.Equal(TurnNarration.Step.None, n.Update(1.0, true, narrationStillCurrent: false));
        Assert.Equal(TurnNarration.Step.Narrate, n.Update(1.3, false));

        // Long after it finished, a turn interrupts nothing; the same line, unbroken and recent, is not repeated.
        Assert.Equal(TurnNarration.Step.None, n.Update(5.0, true));
        Assert.Equal(TurnNarration.Step.Narrate, n.Update(5.3, false));
        Assert.True(n.Accept("Open, North", 5.3));
        Assert.Equal(TurnNarration.Step.None, n.Update(9.0, true));
        Assert.Equal(TurnNarration.Step.Narrate, n.Update(9.3, false));
        Assert.True(n.Accept("Partition Wall, 3 metres", 9.3));
        Assert.Equal(TurnNarration.Step.None, n.Update(11.0, true));
        Assert.Equal(TurnNarration.Step.Narrate, n.Update(11.3, false));
        Assert.False(n.Accept("Partition Wall, 3 metres", 11.3));
    }

    [Fact]
    public void TheNavigationAidCommandsSwitchAndSave()
    {
        bool narration = NavigationAids.TurnNarration, bumps = NavigationAids.WallBumps;
        try
        {
            int saves = 0;
            Assert.Contains("off", OpenFPS.Client.Core.Session.ClientGameSession.NavigationAidCommand("narrate", new[] { "off" }, () => saves++));
            Assert.False(NavigationAids.TurnNarration);
            Assert.Contains("on", OpenFPS.Client.Core.Session.ClientGameSession.NavigationAidCommand("bumps", new[] { "on" }, () => saves++));
            Assert.True(NavigationAids.WallBumps);
            Assert.Equal(2, saves);
            Assert.StartsWith("Say", OpenFPS.Client.Core.Session.ClientGameSession.NavigationAidCommand("bumps", new[] { "loud" }, () => saves++));
            Assert.Equal(2, saves);
        }
        finally
        {
            NavigationAids.TurnNarration = narration;
            NavigationAids.WallBumps = bumps;
        }
    }
}
