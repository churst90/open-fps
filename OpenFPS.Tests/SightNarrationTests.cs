using System.Diagnostics;
using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The eyes (Cody, 2026-10-04): "when I strafe sideways or walk forward, when my line of sight changes,
/// also narrate what changed ... if a vehicle passes in front I should know about it ... if I'm moving
/// sideways and I'm looking for the stairs to go to the next level I need to hear them."
///
/// Each test drives <see cref="SightWatch"/> a simulation tick at a time, as the session does, with a
/// speech log that remembers the last line, and reads back what was said.
/// </summary>
public class SightNarrationTests
{
    private readonly ITestOutputHelper _o;
    public SightNarrationTests(ITestOutputHelper o) => _o = o;

    private const float Dt = PhysicsConstants.FixedDeltaTime;
    private const float Walk = 4.5f;     // the game's walk, m/s: one tap is one tick, 15 cm

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

    /// <summary>A door leaf shut in its doorway: solid, and a portal, which is what makes it a door.</summary>
    private static EntitySnapshot Door(int id, Vector3 centre, float width, string name)
    {
        var d = Box(id, centre, new Vector3(width, 2.1f, 0.06f), name, "Wood");
        d.Definition.Portal = new PortalComponent { RegionAId = 1, RegionBId = -1 };
        d.Definition.Transform = d.Transform;
        return d;
    }

    private static EntitySnapshot Car(int id, Vector3 centre, Vector3 velocity, string name = "Hatchback 1")
    {
        var c = Box(id, centre, new Vector3(1.8f, 1.4f, 4.5f), name, "Metal",
                    Quaternion.CreateFromYawPitchRoll(MathF.Atan2(velocity.X, velocity.Z), 0, 0));
        c.Definition.Type = EntityType.NPC;
        c.Definition.Moves = true;
        c.Velocity = velocity;
        return c;
    }

    private static WorldSnapshot World(params EntitySnapshot[] things)
    {
        var w = new WorldSnapshot();
        foreach (var b in things)
        {
            w.Entities[b.Id] = b;
            if (b.Definition.Type != EntityType.StaticObject || b.Definition.Moves) w.DynamicEntities.Add(b);
        }
        return w;
    }

    private static EntitySnapshot Floor() => Box(1, new Vector3(0, -0.1f, 0), new Vector3(80, 0.2f, 80), "Concrete Floor", "Concrete");

    /// <summary>A corridor wall along z = 4 with a door in it at x = 0, 1.1 m wide.</summary>
    private static WorldSnapshot WallWithADoor() => World(
        Floor(),
        Box(10, new Vector3(-4.275f, 1.5f, 4f), new Vector3(7.45f, 3f, 0.12f), "Corridor wall"),
        Box(11, new Vector3(4.275f, 1.5f, 4f), new Vector3(7.45f, 3f, 0.12f), "Corridor wall"),
        Box(12, new Vector3(0f, 2.55f, 4f), new Vector3(1.1f, 0.9f, 0.12f), "Corridor wall"),
        Door(20, new Vector3(0f, 1.05f, 4f), 1.1f, "Flat 1 door"));

    /// <summary>The session's side: a speech log, the tick, and every line said.</summary>
    private sealed class Eyes
    {
        public readonly SightWatch Watch = new();
        public readonly List<(double T, string Line, string Cause)> Said = new();
        public int Interrupts;
        public string? LastSpoken;
        public double Now;
        public Vector3 Feet;
        public float Yaw, Pitch;
        public WorldSnapshot World;

        public Eyes(WorldSnapshot world, Vector3 feet, float yaw = 0f) { World = world; Feet = feet; Yaw = yaw; }

        public void Tick(bool looking = false, string cardinal = "North")
        {
            Now += Dt;
            bool current = Watch.LastLine != null && LastSpoken == Watch.LastLine;
            var r = Watch.Update(World, new SightWatch.Pose(Feet, Yaw, Pitch, 1.7f), 999, Now, looking, cardinal, current);
            if (r.Act == SightWatch.Act.Interrupt) Interrupts++;
            if (r.Act == SightWatch.Act.Say) { Said.Add((Now, r.Line!, r.Cause)); LastSpoken = r.Line; }
        }

        public void Ticks(int n) { for (int i = 0; i < n; i++) Tick(); }

        /// <summary>Held: <paramref name="ticks"/> ticks at walking pace along <paramref name="dir"/>.</summary>
        public void Hold(Vector3 dir, int ticks)
        {
            for (int i = 0; i < ticks; i++) { Feet += Vector3.Normalize(dir) * Walk * Dt; Tick(); }
        }

        /// <summary>Tapped: <paramref name="taps"/> single-tick steps, <paramref name="gap"/> ticks apart.</summary>
        public void Tap(Vector3 dir, int taps, int gap = 8)
        {
            for (int i = 0; i < taps; i++) { Feet += Vector3.Normalize(dir) * Walk * Dt; Tick(); Ticks(gap - 1); }
        }
    }

    private void Dump(Eyes e)
    {
        foreach (var s in e.Said) _o.WriteLine($"{s.T:F2} s ({s.Cause}): {s.Line}");
    }

    // ── Walking and strafing ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrafingPastADoorwaySaysItOnce(bool tapping)
    {
        var eyes = new Eyes(WallWithADoor(), new Vector3(-6f, 0f, 0f));
        eyes.Ticks(10);
        if (tapping) eyes.Tap(Vector3.UnitX, 80); else eyes.Hold(Vector3.UnitX, 80);   // twelve metres east, facing north
        eyes.Ticks(60);
        Dump(eyes);
        var door = eyes.Said.Where(s => s.Line.StartsWith("Flat 1 door")).ToList();
        Assert.Single(door);
        Assert.Equal("Flat 1 door, 4 metres", door[0].Line);
        // ...and the wall either side of it is said at most once, when the door has gone by.
        Assert.True(eyes.Said.Count(s => s.Line.StartsWith("Corridor wall")) <= 1);
        // Strafing straight back past it within a few seconds: it was just said, and is not said again.
        // (Tapped, the pass took twenty seconds, and coming back to it after that is news again.)
        if (tapping) return;
        int before = eyes.Said.Count;
        eyes.Hold(-Vector3.UnitX, 80);
        eyes.Ticks(30);
        Dump(eyes);
        Assert.DoesNotContain(eyes.Said.Skip(before), s => s.Line.StartsWith("Flat 1 door"));
    }

    [Fact]
    public void AnOpenDoorwayIsSaidRatherThanTheWallSeenThroughIt()
    {
        var world = WallWithADoor();
        // The leaf swung open, square to the wall; its doorway is where it stood shut.
        var leaf = world.Entities[20];
        leaf.Transform = new Transform { Position = new Vector3(0.55f, 1.05f, 4.5f), Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0), Scale = Vector3.One };
        world.Entities[20] = leaf;
        world.Entities[30] = Box(30, new Vector3(0, 1.5f, 12f), new Vector3(10f, 3f, 0.12f), "Back wall");
        var index = new SightIndex();
        index.Refresh(world, 0);
        var seen = SightCone.Look(index, world, new Vector3(0.3f, 0, 0), 0f, 0f, 1.7f, 999);
        Assert.Equal("Flat 1 door", seen?.Name);
        Assert.InRange(seen!.Value.Distance, 3.9f, 4.1f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WalkingTowardAWallSaysItOnlyAtTheSteps(bool tapping)
    {
        var world = World(Floor(), Box(10, new Vector3(0, 1.5f, 16f), new Vector3(12f, 3f, 0.12f), "Corridor wall"));
        var eyes = new Eyes(world, Vector3.Zero);
        eyes.Ticks(10);
        if (tapping) eyes.Tap(Vector3.UnitZ, 98, gap: 5); else eyes.Hold(Vector3.UnitZ, 98);   // to a metre short of it
        eyes.Ticks(30);
        Dump(eyes);
        Assert.InRange(eyes.Feet.Z, 14.5f, 15f);
        // Three lines at most, one at each of 10, 5 and 2 metres; the last two as bare distances.
        Assert.InRange(eyes.Said.Count, 2, 3);
        Assert.Equal("Corridor wall, 10 metres", eyes.Said[0].Line);
        Assert.Equal("5 metres", eyes.Said[^2].Line);
        Assert.Equal("2 metres", eyes.Said[^1].Line);
        for (int i = 1; i < eyes.Said.Count; i++)
            Assert.True(eyes.Said[i].T - eyes.Said[i - 1].T >= SightWatch.MinGapSeconds - 1e-6);
        // Tapping back and forth across two metres says nothing more.
        int before = eyes.Said.Count;
        for (int i = 0; i < 6; i++) { eyes.Tap(-Vector3.UnitZ, 3); eyes.Tap(Vector3.UnitZ, 3); }
        Assert.Equal(before, eyes.Said.Count);
    }

    // ── Looking ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LookingDownAtTheFloorSaysNothing()
    {
        var eyes = new Eyes(World(Floor()), Vector3.Zero);
        eyes.Ticks(10);
        eyes.Pitch = MathF.PI / 4f;          // K: increasing pitch looks DOWN
        eyes.Tick(looking: true);
        eyes.Ticks(60);
        eyes.Pitch = MathF.PI / 2f * 0.99f;  // and straight down
        eyes.Tick(looking: true);
        eyes.Ticks(60);
        Dump(eyes);
        Assert.Empty(eyes.Said);
    }

    [Fact]
    public void LookingUpAndBackSaysWhatTheSightLineMeets()
    {
        var world = World(Floor(), Box(10, new Vector3(0, 1.5f, 6f), new Vector3(12f, 3f, 0.12f), "Corridor wall"),
                          Box(11, new Vector3(0, 3.1f, 0f), new Vector3(20f, 0.2f, 20f), "Corridor ceiling"));
        var eyes = new Eyes(world, Vector3.Zero);
        eyes.Ticks(10);
        eyes.Pitch = -MathF.PI / 4f;         // O: up
        eyes.Tick(looking: true);
        eyes.Ticks(30);
        eyes.Pitch = 0f;                     // K: level again
        eyes.Tick(looking: true);
        eyes.Ticks(30);
        Dump(eyes);
        Assert.Equal(new[] { "Corridor ceiling, 2 metres", "Corridor wall, 6 metres" }, eyes.Said.Select(s => s.Line));
        Assert.Equal(new[] { "look", "turn" }, eyes.Said.Select(s => s.Cause));
    }

    // ── Things passing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACarCrossingInFrontIsSaidOnceWithItsDirection()
    {
        var world = World(Floor(), Car(50, new Vector3(-20f, 0.7f, 6f), new Vector3(10f, 0, 0)));
        var eyes = new Eyes(world, Vector3.Zero);
        for (int i = 0; i < 150; i++)        // five seconds: forty metres, right across
        {
            var c = world.Entities[50];
            c.Transform.Position += c.Velocity * Dt;
            world.Entities[50] = c;
            world.DynamicEntities[0] = c;
            eyes.Tick();
        }
        Dump(eyes);
        Assert.Single(eyes.Said);
        Assert.Equal("Hatchback passing, left to right, 6 metres", eyes.Said[0].Line);
        Assert.Equal("pass", eyes.Said[0].Cause);

        // The other way, facing south: it is still crossing from your left.
        var back = World(Floor(), Car(51, new Vector3(20f, 0.7f, 6f), new Vector3(-10f, 0, 0), "Police car"));
        var facing = new Eyes(back, Vector3.Zero);
        for (int i = 0; i < 150; i++)
        {
            var c = back.Entities[51];
            c.Transform.Position += c.Velocity * Dt;
            back.Entities[51] = c;
            back.DynamicEntities[0] = c;
            facing.Tick();
        }
        Dump(facing);
        Assert.Equal(new[] { "Police car passing, right to left, 6 metres" }, facing.Said.Select(s => s.Line));
    }

    [Fact]
    public void ACarCrossingBehindAWallIsNotSaid()
    {
        var world = World(Floor(), Box(10, new Vector3(0, 1.5f, 3f), new Vector3(30f, 3f, 0.2f), "Garden wall"),
                          Car(50, new Vector3(-20f, 0.7f, 6f), new Vector3(10f, 0, 0)));
        var eyes = new Eyes(world, Vector3.Zero);
        for (int i = 0; i < 150; i++)
        {
            var c = world.Entities[50];
            c.Transform.Position += c.Velocity * Dt;
            world.Entities[50] = c;
            world.DynamicEntities[0] = c;
            eyes.Tick();
        }
        Dump(eyes);
        Assert.DoesNotContain(eyes.Said, s => s.Line.Contains("passing"));
    }

    /// <summary>On a pavement facing the road, the people walking by one after another: the first is
    /// said, and the next few seconds of the same kind are not — but a car among them is.</summary>
    [Fact]
    public void AStreamOfPeopleIsOneLineAndACarAmongThemIsAnother()
    {
        var things = new List<EntitySnapshot> { Floor() };
        for (int i = 0; i < 4; i++)
        {
            var p = Box(60 + i, new Vector3(-6f - 1.4f * i, 0.9f, 3f), new Vector3(0.5f, 1.8f, 0.5f),
                        $"Pedestrian, Main Street, west side {i}", solid: false);
            p.Definition.Type = EntityType.NPC;
            p.Definition.Moves = true;
            p.Velocity = new Vector3(1.4f, 0, 0);
            things.Add(p);
        }
        things.Add(Car(70, new Vector3(-30f, 0.7f, 8f), new Vector3(10f, 0, 0)));
        var world = World(things.ToArray());
        var eyes = new Eyes(world, Vector3.Zero);
        for (int t = 0; t < 240; t++)
        {
            for (int k = 0; k < world.DynamicEntities.Count; k++)
            {
                var e = world.DynamicEntities[k];
                e.Transform.Position += e.Velocity * Dt;
                world.DynamicEntities[k] = e;
                world.Entities[e.Id] = e;
            }
            eyes.Tick();
        }
        Dump(eyes);
        Assert.Single(eyes.Said, s => s.Line.StartsWith("Pedestrian passing, left to right"));
        Assert.Single(eyes.Said, s => s.Line.StartsWith("Hatchback passing, left to right, 8 metres"));
    }

    [Fact]
    public void KindsAreSaidWithoutTheMapsNumbers()
    {
        EntitySnapshot Npc(string name) { var e = Box(1, Vector3.Zero, Vector3.One, name); e.Definition.Type = EntityType.NPC; return e; }
        Assert.Equal("Hatchback", Sightline.KindOf(Npc("Hatchback 1")));
        Assert.Equal("Pedestrian", Sightline.KindOf(Npc("Pedestrian, Main Street, west side 14")));
        Assert.Equal("Light rail", Sightline.KindOf(Npc("Light rail 2")));
        Assert.Equal("Police car", Sightline.KindOf(Npc("Police car")));
        var player = Npc("cody2");
        player.Definition.Type = EntityType.Player;
        Assert.Equal("cody2", Sightline.KindOf(player));
    }

    // ── The city ────────────────────────────────────────────────────────────────────────────────

    private static readonly object MarkerLock = new();

    /// <summary>The city as the client holds it, with its stair markers listed as ClientWorldState lists them.</summary>
    private static WorldSnapshot City()
    {
        var world = WallBumpAndNarrationTests.City();
        lock (MarkerLock)
        {
            if (world.MarkerEntityIds.Count == 0)
                foreach (var e in world.Entities.Values)
                    if (e.Definition.Type == EntityType.StaticObject && !e.Definition.Moves && !e.Definition.Collider.IsSolid
                        && !string.IsNullOrEmpty(e.Definition.Identity.BeaconCategory))
                        world.MarkerEntityIds.Add(e.Id);
        }
        return world;
    }

    /// <summary>
    /// Brandt Court's ground-floor stairwell: standing three metres in front of the foot of the flight,
    /// facing it (south), and strafing west to east across the lobby. The flight comes into the sight
    /// line and is said by its marker's name; the flight was not ahead at the start.
    /// </summary>
    [Fact]
    public void StairsComingIntoViewWhileStrafingInTheCityAreSaid()
    {
        var world = City();
        float feet = PhysicsUtils.GetGroundHeight(world, new Vector3(-17f, 1f, 159.5f), -1, out _);
        var eyes = new Eyes(world, new Vector3(-17f, feet, 159.5f), yaw: MathF.PI);
        eyes.Ticks(10);
        Assert.Empty(eyes.Said);
        eyes.Hold(Vector3.UnitX, 40);       // six metres east
        eyes.Ticks(30);
        Dump(eyes);
        var stairs = eyes.Said.Where(s => s.Line.StartsWith("Stairs up")).ToList();
        Assert.Single(stairs);
        Assert.StartsWith("Stairs up, 19 steps, to floor 1, ", stairs[0].Line);

        // And tapping across, one 15 cm step at a time: said once too.
        var tapping = new Eyes(world, new Vector3(-17f, feet, 159.5f), yaw: MathF.PI);
        tapping.Ticks(10);
        tapping.Tap(Vector3.UnitX, 40);
        tapping.Ticks(30);
        Dump(tapping);
        Assert.Single(tapping.Said, s => s.Line.StartsWith("Stairs up"));
    }

    /// <summary>On floor 1 of the same stairwell, on the landing at the head of the flight: the way
    /// down is ahead in one lane and the way up in the other.</summary>
    [Theory]
    [InlineData(-13.05f, "Stairs down, 19 steps, to floor 0")]
    [InlineData(-10.65f, "Stairs up, 17 steps, to floor 2")]
    public void OnALandingTheFlightsAreSaid(float x, string expected)
    {
        var world = City();
        var index = new SightIndex();
        index.Refresh(world, 0);
        float feet = PhysicsUtils.GetGroundHeight(world, new Vector3(x, 4.5f, 148.6f), -1, out _);
        _o.WriteLine($"floor 1 landing at {feet:F2}");
        Assert.InRange(feet, 3f, 3.5f);
        var seen = SightCone.Look(index, world, new Vector3(x, feet, 148.6f), 0f, 0f, 1.7f, -1);
        _o.WriteLine($"facing north: {seen?.Name} {seen?.Distance:F1}");
        Assert.Equal(expected, seen?.Name);
    }

    /// <summary>The sight grid answers as the client's own ray cast does — the same thing at the same
    /// distance — over a thousand rays through the city, level, tilted and steep.</summary>
    [Fact]
    public void TheSightGridAgreesWithTheSpatialService()
    {
        var world = City();
        var index = new SightIndex();
        index.Refresh(world, 0);
        _o.WriteLine($"grid: {index.Grid.Size.Things} things, {index.Grid.Size.Slabs} slabs");
        var spatial = new SpatialService();
        var rng = new Random(11);
        Func<EntitySnapshot, bool> stops = e => Sightline.Stops(e, -1);
        int hits = 0;
        for (int i = 0; i < 1000; i++)
        {
            var origin = new Vector3(rng.NextSingle() * 240f - 120f, rng.NextSingle() * 12f, rng.NextSingle() * 320f - 60f);
            float yaw = rng.NextSingle() * MathF.Tau, pitch = (rng.NextSingle() - 0.5f) * (i % 3 == 0 ? 3f : 0.6f);
            var dir = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f));
            index.Prepare(world, origin);
            bool a = spatial.RaycastSingle(world, origin, dir, 20f, stops, out var ha, out float da);
            bool b = index.Grid.Cast(world, origin, dir, 20f, stops, out var hb, out float db);
            Assert.True(a == b, $"ray {i} from {origin} along {dir}: spatial {(a ? $"'{Sightline.NameOf(ha)}' at {da:F2}" : "nothing")}, grid {(b ? $"'{Sightline.NameOf(hb)}' at {db:F2}" : "nothing")}");
            if (!a) continue;
            hits++;
            Assert.InRange(db, da - 1e-3f, da + 1e-3f);
        }
        _o.WriteLine($"{hits} of 1000 rays met something");
        Assert.True(hits > 300);
    }

    /// <summary>
    /// What it costs: a look (centre line and cone) and a passing check, at places in the city and with
    /// three hundred and fifty people walking about it. The budget is half a millisecond of the game loop.
    /// </summary>
    [Trait("Category", "Timing")] // depends on this machine's speed or on real time; not run on CI
    [Fact]
    public void ALookCostsLessThanHalfAMillisecond()
    {
        var city = City();
        var world = new WorldSnapshot { StaticGrid = city.StaticGrid };
        foreach (var (id, e) in city.Entities) world.Entities[id] = e;
        world.MarkerEntityIds.AddRange(city.MarkerEntityIds);
        var rng = new Random(7);
        for (int i = 0; i < 350; i++)
        {
            var p = Box(900000 + i, new Vector3(rng.NextSingle() * 200f - 100f, 0.9f, rng.NextSingle() * 300f - 50f),
                        new Vector3(0.5f, 1.8f, 0.5f), $"Pedestrian, Main Street, west side {i}", solid: false);
            p.Definition.Type = EntityType.NPC;
            p.Definition.Moves = true;
            p.Velocity = new Vector3(1.4f, 0, 0);
            world.Entities[p.Id] = p;
            world.DynamicEntities.Add(p);
        }
        var index = new SightIndex();
        var clock = Stopwatch.StartNew();
        index.Refresh(world, 0);
        _o.WriteLine($"{index.Marks.Count} doorways and stair ends and {index.Grid.Size.Things} fixed things indexed in {clock.Elapsed.TotalMilliseconds:F1} ms");
        var passing = new PassingWatch();
        var spots = new (float X, float Z)[] { (-19.95f, 192f), (-13.05f, 158f), (0f, 100f), (-17f, 159.5f), (10f, 150f), (40f, 60f) };
        var samples = new List<double>();
        for (int round = 0; round < 3; round++)
            foreach (var (x, z) in spots)
            {
                float feet = PhysicsUtils.GetGroundHeight(world, new Vector3(x, 1f, z), -1, out _);
                for (int k = 0; k < 16; k++)
                {
                    float yaw = k * MathF.PI / 8f;
                    var at = new Vector3(x, feet, z);
                    clock.Restart();
                    SightCone.Look(index, world, at, yaw, 0f, 1.7f, -1);
                    passing.Update(index, world, at + new Vector3(0, 1.7f, 0), feet, yaw, -1, k * 0.1);
                    clock.Stop();
                    if (round > 0) samples.Add(clock.Elapsed.TotalMilliseconds);
                }
            }
        samples.Sort();
        double mean = samples.Average(), p95 = samples[(int)(samples.Count * 0.95)], max = samples[^1];
        _o.WriteLine($"a look: mean {mean:F3} ms, 95th percentile {p95:F3} ms, max {max:F3} ms over {samples.Count}");
        Assert.True(mean < 0.5, $"a look costs {mean:F3} ms on average");
    }
}
