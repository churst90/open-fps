using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Door kinds (Cody, 2026-10-02): a knob or lever, a push bar with a closer, a keyed glass front door,
/// a pulled glass door, an automatic slider, a patio slider and a lift's doors. Each moves as it
/// does, does what it does by itself, and names its mechanical events.
/// </summary>
public class DoorTypeTests : IDisposable
{
    private const float Dt = PhysicsConstants.FixedDeltaTime;
    private readonly ITestOutputHelper _o;
    private readonly World _world = World.Create();
    private readonly DoorSystem _doors = new();
    private readonly PrefabRepository _prefabs = new(Path.Combine(AppContext.BaseDirectory, "prefabs"));
    private readonly List<(string Key, int Sounds)> _heard = new();

    public DoorTypeTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }
    public void Dispose() => World.Destroy(_world);

    /// <summary>A door standing on its own at the origin: its doorway runs along X, through along Z.</summary>
    private Entity Door(string prefab)
    {
        var e = _prefabs.Spawn(_world, prefab, new Vector3(0, 1.05f, 0), Quaternion.Identity, Vector3.One);
        Tick(1);
        return e;
    }

    private void Tick(int n = 1)
    {
        for (int i = 0; i < n; i++)
            _doors.Update(_world, Dt, _ => { }, (_, key, sounds) => _heard.Add((key, sounds.Count)));
    }

    private void TickSeconds(float s) => Tick((int)MathF.Ceiling(s / Dt));

    private DoorComponent D(Entity e) => _world.Get<DoorComponent>(e);

    private Entity Player(Vector3 at)
        => _world.Create(new PlayerComponent { Username = "tester" }, new Transform { Position = at, Rotation = Quaternion.Identity });

    private Entity Walker(Vector3 at)
        => _world.Create(new Pedestrian { Voice = "", Pair = "" }, new Transform { Position = at, Rotation = Quaternion.Identity });

    private void MoveTo(Entity person, Vector3 at) => _world.Get<Transform>(person).Position = at;

    // ── Motion and aperture ─────────────────────────────────────────────────────────────────────

    /// <summary>Every kind opens over its own travel time, leaving an opening in proportion: a swinging
    /// leaf turns about its hinged edge, a sliding one moves its own width without turning.</summary>
    [Theory]
    [InlineData("door", DoorKind.Hinged, false)]
    [InlineData("steel_door", DoorKind.PushBar, false)]
    [InlineData("glass_front_door", DoorKind.GlassPushBar, false)]
    [InlineData("glass_pull_door", DoorKind.GlassPull, false)]
    [InlineData("auto_sliding_door", DoorKind.AutoSliding, true)]
    [InlineData("patio_door", DoorKind.PatioSliding, true)]
    [InlineData("elevator_door", DoorKind.Elevator, true)]
    public void EachKindMovesAsItDoesAndTheOpeningFollows(string prefab, DoorKind kind, bool slides)
    {
        var e = Door(prefab);
        var d = D(e);
        Assert.Equal(kind, (DoorKind)d.Kind);
        Assert.Equal(slides, d.Slides);
        float width = _world.Get<ColliderComponent>(e).Size.X;

        Assert.True(DoorSystem.Set(_world, e, open: true));
        float prev = 0f;
        int full = -1;
        for (int i = 1; i <= (int)(d.SwingSeconds / Dt) + 3; i++)
        {
            Tick();
            d = D(e);
            Assert.True(d.Openness >= prev, "it went backwards");
            prev = d.Openness;
            Assert.Equal(d.Openness * d.Aperture, _world.Get<PortalComponent>(e).ApertureSize, 4);
            var t = _world.Get<Transform>(e);
            MathHelper.ToYawPitch(t.Rotation, out float yaw, out _);
            if (slides)
            {
                Assert.Equal(new Vector3(d.Openness * width * d.HingeSide, 1.05f, 0f), t.Position, new Near(0.01f));
                Assert.Equal(0f, MathHelper.WrapAngle(yaw), 3);
            }
            else
                Assert.Equal(-d.Openness * d.SwingRadians * d.HingeSide * d.PushSide, MathHelper.WrapAngle(yaw), 2);
            if (full < 0 && d.Openness >= 1f) full = i;
        }
        _o.WriteLine($"{prefab}: open after {full * Dt:F2} s (travel {d.SwingSeconds} s)");
        Assert.InRange(full * Dt, d.SwingSeconds - Dt, d.SwingSeconds + 2 * Dt);
    }

    // ── Closers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A closer waits for a clear doorway, then shuts the door, slow through the sweep and faster
    /// at the end to carry the latch.</summary>
    [Theory]
    [InlineData("steel_door")]
    [InlineData("glass_front_door")]
    [InlineData("glass_pull_door")]
    public void ACloserWaitsForAClearDoorwayAndShutsAtItsOwnSpeed(string prefab)
    {
        var e = Door(prefab);
        var d = D(e);
        // In the doorway, on the side it is pushed from: not in the way of it opening.
        var player = Player(new Vector3(0.2f, 1.0f, 0.6f * d.PushSide));
        DoorSystem.Set(_world, e, open: true);
        TickSeconds(12f);
        Assert.Equal(1f, D(e).Openness, 3);                              // held open by being there

        MoveTo(player, new Vector3(0f, 1.0f, 6f));                       // clear of it
        int ticks = 0;
        float sweepStep = 0f, latchStep = 0f;
        while (D(e).Openness > 0f && ticks < 30 * 30)
        {
            float before = D(e).Openness;
            Tick(); ticks++;
            float after = D(e).Openness;
            if (before is < 0.6f and > 0.4f) sweepStep = before - after;
            if (before is < 0.1f and > 0.05f) latchStep = before - after;
        }
        float expected = d.CloseAfterSeconds + (1f - DoorSystem.LatchZone) * d.CloseSeconds + DoorSystem.LatchZone * d.SwingSeconds;
        _o.WriteLine($"{prefab}: shut {ticks * Dt:F2} s after the doorway cleared (expected {expected:F2})");
        Assert.InRange(ticks * Dt, expected - 3 * Dt, expected + 3 * Dt);
        Assert.Equal(Dt / d.CloseSeconds, sweepStep, 4);
        Assert.Equal(Dt / d.SwingSeconds, latchStep, 4);
        Assert.Contains(_heard, h => h.Key == DoorEvents.Of((DoorKind)D(e).Kind, DoorEvents.Closer));
    }

    /// <summary>Somebody stepping into the doorway while a closer is shutting it holds it where it is;
    /// it carries on once they have gone.</summary>
    [Fact]
    public void ACloserNeverShutsOnSomebodyInTheDoorway()
    {
        var e = Door("steel_door");
        var player = Player(new Vector3(0f, 1.0f, 6f));
        DoorSystem.Set(_world, e, open: true);
        while (D(e).Openness > 0.6f || D(e).Target > 0f) Tick();       // open, wait, and part shut
        float held = D(e).Openness;
        Assert.InRange(held, 0.4f, 0.6f);

        MoveTo(player, new Vector3(-0.3f, 1.0f, 0.4f));                  // steps into it
        for (int i = 0; i < 30 * 10; i++)
        {
            Tick();
            Assert.True(D(e).Openness >= held - 1e-4f, $"it shut on them: {D(e).Openness:F3}");
        }
        MoveTo(player, new Vector3(0f, 1.0f, 6f));
        TickSeconds(6f);
        Assert.Equal(0f, D(e).Openness);
    }

    // ── Automatic doors ─────────────────────────────────────────────────────────────────────────

    /// <summary>An automatic door opens for anyone (player or walker) within 2.5 m either side, stays open
    /// while they are there, and closes 2 s after they have gone.</summary>
    [Theory]
    [InlineData(true, 1f)]
    [InlineData(true, -1f)]
    [InlineData(false, 1f)]
    [InlineData(false, -1f)]
    public void AnAutomaticDoorOpensForAnyoneAndClosesAfterThem(bool player, float side)
    {
        var e = Door("auto_sliding_door");
        var d = D(e);
        var who = player ? Player(new Vector3(0.3f, 1.0f, side * 3.2f)) : Walker(new Vector3(0.3f, 1.0f, side * 3.2f));
        TickSeconds(2f);
        Assert.Equal(0f, D(e).Openness);                                 // 3.2 m away: not yet

        MoveTo(who, new Vector3(0.3f, 1.0f, side * 2.2f));
        TickSeconds(d.SwingSeconds + 0.1f);
        Assert.Equal(1f, D(e).Openness, 3);
        TickSeconds(10f);
        Assert.Equal(1f, D(e).Openness, 3);                              // while they are there

        MoveTo(who, new Vector3(0.3f, 1.0f, -side * 8f));                // through, and gone
        int ticks = 0;
        while (D(e).Openness > 0f && ticks < 30 * 30) { Tick(); ticks++; }
        float expected = d.CloseAfterSeconds + d.CloseSeconds;
        Assert.InRange(ticks * Dt, expected - 3 * Dt, expected + 3 * Dt);

        var keys = _heard.Select(h => h.Key).ToList();
        Assert.Equal(new[] { "motor-start", "rollers", "stop", "motor-start", "rollers", "shut" }
                         .Select(ev => $"door:auto-slide:{ev}"), keys);
    }

    /// <summary>A motor closing on somebody in the doorway reverses, as a door's safety beam makes it.</summary>
    [Theory]
    [InlineData("auto_sliding_door")]
    [InlineData("elevator_door")]
    public void APoweredDoorReversesForSomebodyInTheDoorway(string prefab)
    {
        var e = Door(prefab);
        var person = Player(new Vector3(0f, 1.0f, 9f));
        DoorSystem.Set(_world, e, open: true);
        while (D(e).Openness > 0.5f || D(e).Target > 0f) Tick();        // open, wait, half shut
        MoveTo(person, new Vector3(0.1f, 1.0f, 0.3f));                   // steps in, beside the beam
        Tick(2);
        Assert.Equal(1f, D(e).Target);
        Assert.Contains(_heard, h => h.Key.EndsWith(":reopen"));
        TickSeconds(D(e).SwingSeconds);
        Assert.Equal(1f, D(e).Openness, 3);
    }

    /// <summary>Nobody opens a motor's door by hand; a patio slider is opened by hand like any other.</summary>
    [Fact]
    public void WhoOpensWhat()
    {
        Assert.False(DoorSystem.OpensByHand(D(Door("auto_sliding_door"))));
        Assert.False(DoorSystem.OpensByHand(D(Door("elevator_door"))));
        Assert.True(DoorSystem.OpensByHand(D(Door("patio_door"))));
        Assert.True(DoorSystem.OpensByHand(D(Door("door"))));
        Assert.Equal("slides", DoorSystem.Verb(D(Door("patio_door"))));
        Assert.Equal("swings", DoorSystem.Verb(D(Door("steel_door"))));
    }

    /// <summary>A lift's doors stay shut until the lift opens them, then close themselves after a dwell.</summary>
    [Fact]
    public void ALiftsDoorsWaitForTheLift()
    {
        var e = Door("elevator_door");
        Player(new Vector3(0f, 1.0f, 1.5f));                             // waiting in front of it
        TickSeconds(5f);
        Assert.Equal(0f, D(e).Openness);                                 // no sensor: the lift decides
        DoorSystem.Set(_world, e, open: true);                           // the lift has arrived
        TickSeconds(D(e).SwingSeconds + 0.1f);
        Assert.Equal(1f, D(e).Openness, 3);
        TickSeconds(D(e).CloseAfterSeconds + D(e).CloseSeconds + 0.2f);
        Assert.Equal(0f, D(e).Openness);
    }

    // ── Events ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One open and one shut of every kind, as its hardware's events in order; those without a
    /// model render are silent until synthesised.</summary>
    [Theory]
    // The knob door's close is a hand shutting it, sent as the leaf arrives.
    // Opened from nowhere in particular is opened from the push side (DoorSidesTests has both sides).
    [InlineData("door", "latch-retract+ push | swing latch+")]
    [InlineData("steel_door", "bar+ push | closer latch+")]
    [InlineData("glass_pull_door", "push+ | closer latch+")]
    // A sliding door's sound is its whole run, sent as the run starts, both ways.
    [InlineData("patio_door", "latch-retract+ rollers stop | rollers+ latch")]
    [InlineData("auto_sliding_door", "motor-start+ rollers stop | motor-start+ rollers shut")]
    [InlineData("elevator_door", "motor-start+ rollers stop | motor-start+ rollers shut")]
    public void EachKindNamesItsEvents(string prefab, string expected)
    {
        var e = Door(prefab);
        var d = D(e);
        string slug = DoorEvents.Slug((DoorKind)d.Kind);
        DoorSystem.Set(_world, e, open: true);
        TickSeconds(d.SwingSeconds + 0.2f);
        int opened = _heard.Count;
        if (d.CloseAfterSeconds <= 0f) DoorSystem.Set(_world, e, open: false);     // nothing shuts it but a hand
        TickSeconds(d.CloseAfterSeconds + (d.Powered ? 0f : DoorSystem.ReachSeconds) + MathF.Max(d.CloseSeconds, d.SwingSeconds) + 0.5f);

        string Say(IEnumerable<(string Key, int Sounds)> hs) => string.Join(" ", hs.Select(h =>
        {
            Assert.True(DoorEvents.TryParse(h.Key, out var k, out string ev), h.Key);
            Assert.Equal(slug, DoorEvents.Slug(k));
            return ev + (h.Sounds > 0 ? "+" : "");
        }));
        string got = Say(_heard.Take(opened)) + " | " + Say(_heard.Skip(opened));
        _o.WriteLine($"{prefab}: {got}");
        Assert.Equal(expected, got);
    }

    /// <summary>A glass front door is unlocked with a key and pulled open from its keyed side, and pushed
    /// open by its bar from the other; either way it is the same leaf on the same closer.</summary>
    [Fact]
    public void AFrontDoorTakesAKeyFromOutsideAndABarFromInside()
    {
        var e = Door("glass_front_door");
        Assert.Equal(1f, D(e).KeyedSide);
        Assert.True(DoorSystem.Set(_world, e, open: true, by: new Vector3(0.2f, 1.6f, 1.5f)));   // outside
        Assert.True(D(e).KeyTurned);
        TickSeconds(DoorSystem.KeySequenceSeconds + 0.1f);
        Assert.Equal(new[] { "door:glass-pushbar:key-insert", "door:glass-pushbar:key-turn", "door:glass-pushbar:unlock",
                             "door:glass-pushbar:pull" },
                     _heard.Select(h => h.Key));
        DoorSystem.Set(_world, e, open: false);
        TickSeconds(3f);
        _heard.Clear();

        Assert.True(DoorSystem.Set(_world, e, open: true, by: new Vector3(0.2f, 1.6f, -1.5f)));  // inside
        Assert.False(D(e).KeyTurned);
        Tick();
        Assert.Equal(new[] { "door:glass-pushbar:bar", "door:glass-pushbar:push" }, _heard.Select(h => h.Key));
    }

    // ── The routes see the leaf where it is ─────────────────────────────────────────────────────

    /// <summary>A sliding leaf shut blocks the doorway as a swinging one does, passes more as it slides
    /// away, and everything fully open; its pose comes from the real door system.</summary>
    [Fact]
    public void TheRoutesSeeASlidingLeafWhereItIs()
    {
        float TauAt(bool slides, float openness)
        {
            var world = World.Create();
            try
            {
                var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
                var leaf = world.Create(
                    new Transform { Position = new Vector3(-0.15f, 1.075f, 3.5f), Rotation = rot },
                    new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(1.1f, 2.15f, 0.05f), IsSolid = true },
                    new PortalComponent { RegionAId = Lobby, RegionBId = AcousticConstants.GlobalRegionId },
                    new DoorComponent { Slides = slides, SwingSeconds = 1f });
                var doors = new DoorSystem();
                doors.Update(world, Dt, _ => { });
                DoorSystem.Set(world, leaf, open: true);
                while (world.Get<DoorComponent>(leaf).Openness < openness - 1e-4f) doors.Update(world, Dt, _ => { });
                var t = world.Get<Transform>(leaf);
                var portal = world.Get<PortalComponent>(leaf);
                var routes = new SpatialAcoustics().RoutesFor(Lobby1(t.Position, t.Rotation, portal))!;
                var door = Assert.Single(routes.Openings, o => o.Kind == "door");
                Assert.Null(door.Problem);
                return door.Tau.Y;
            }
            finally { World.Destroy(world); }
        }

        float shutSlider = TauAt(true, 0f), shutSwing = TauAt(false, 0f);
        float third = TauAt(true, 0.34f), twoThirds = TauAt(true, 0.67f), open = TauAt(true, 1f);
        _o.WriteLine($"mid-band tau: shut slider {shutSlider:F3}, shut swing {shutSwing:F3}, "
                   + $"a third {third:F3}, two thirds {twoThirds:F3}, open {open:F3}");
        Assert.Equal(shutSwing, shutSlider, 4);
        Assert.True(shutSlider < 0.5f, "a shut slider lets the doorway through");
        Assert.True(third > shutSlider && twoThirds > third && open > twoThirds);
        Assert.Equal(1f, open, 3);
        Assert.Equal(1f, TauAt(false, 1f), 3);
    }

    private const int Lobby = 701;

    /// <summary>A lobby x 0..6, z 0..8, with a doorway in its west wall at z 3..4 and the leaf as given.</summary>
    private static WorldSnapshot Lobby1(Vector3 leafAt, Quaternion leafRotation, PortalComponent portal)
    {
        AcousticRegistry.EnsureInitialized();
        var defs = new List<EntityDefinition>();
        int id = 1;
        void Box(float x0, float x1, float y0, float y1, float z0, float z1, string material = "Brick")
            => defs.Add(Solid(id++, new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2),
                              new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.Identity, material));
        Box(-30, 40, -0.2f, 0, -20, 30, "Concrete");
        Box(-0.3f, 6.3f, 3.0f, 3.2f, -0.3f, 8.3f, "Concrete");
        Box(-0.3f, 0, 0, 3, -0.3f, 3);
        Box(-0.3f, 0, 0, 3, 4, 8.3f);
        Box(-0.3f, 0, 2.1f, 3, 3, 4);
        Box(6, 6.3f, 0, 3, -0.3f, 8.3f);
        Box(-0.3f, 6.3f, 0, 3, 8, 8.3f);
        Box(-0.3f, 6.3f, 0, 3, -0.3f, 0);
        var leaf = Solid(801, leafAt, new Vector3(1.1f, 2.15f, 0.05f), leafRotation, "Wood");
        leaf.Portal = portal;
        defs.Add(leaf);
        int c = AcousticRegistry.GetProperties("Concrete").ResonanceIndex, b = AcousticRegistry.GetProperties("Brick").ResonanceIndex;
        defs.Add(new EntityDefinition
        {
            EntityId = Lobby,
            Transform = new Transform { Position = new Vector3(3, 1.5f, 4), Rotation = Quaternion.Identity },
            Region = new RegionComponent { FriendlyName = "lobby", RoomSize = new Vector3(6, 3, 8), Materials = new[] { c, c, b, b, b, b }, IsIndoor = true },
        });
        var world = new WorldSnapshot();
        foreach (var d in defs)
            world.Entities[d.EntityId] = new EntitySnapshot { Id = d.EntityId, Definition = d, Transform = d.Transform };
        world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, new Vector3(100, 20, 100), new Vector3(-50, -5, -50));
        return world;
    }

    private static EntityDefinition Solid(int id, Vector3 at, Vector3 size, Quaternion rot, string material) => new()
    {
        EntityId = id,
        Type = EntityType.StaticObject,
        Transform = new Transform { Position = at, Rotation = rot, Scale = Vector3.One },
        Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
        Material = new MaterialComponent { Material = material },
    };

    // ── The city ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The city's doors by kind: knobs on flats and house fronts, push bars on the hangar's and
    /// terminal's service doors and each tower's roof, a keyed glass front door on each tower (key side
    /// to the street), automatic pairs at the terminal, and a patio door onto every back garden.
    /// </summary>
    [Fact]
    public void TheCitysDoorsByKind()
    {
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), _prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out var lookup));
        var counts = new Dictionary<DoorKind, int>();
        int frontDoorsFacingOut = 0;
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent, PortalComponent>(),
            (Entity e, ref Transform t, ref DoorComponent d, ref PortalComponent p) =>
            {
                var kind = (DoorKind)d.Kind;
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                if (kind != DoorKind.GlassPushBar) return;
                var room = lookup[p.RegionAId];
                var key = Vector3.Transform(Vector3.UnitZ, t.Rotation) * d.KeyedSide;
                if (Vector3.Dot(world.Get<Transform>(room).Position - t.Position, key) < 0f) frontDoorsFacingOut++;
            });
        _o.WriteLine(string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}")));
        Assert.Equal(397, counts.GetValueOrDefault(DoorKind.Hinged));
        Assert.Equal(7, counts.GetValueOrDefault(DoorKind.PushBar));     // and one onto each tower's roof
        Assert.Equal(5, counts.GetValueOrDefault(DoorKind.GlassPushBar));
        Assert.Equal(0, counts.GetValueOrDefault(DoorKind.GlassPull));
        Assert.Equal(4, counts.GetValueOrDefault(DoorKind.AutoSliding));
        Assert.Equal(64, counts.GetValueOrDefault(DoorKind.PatioSliding));
        Assert.Equal(0, counts.GetValueOrDefault(DoorKind.Elevator));
        Assert.Equal(5, frontDoorsFacingOut);
    }

    private sealed class Near : IEqualityComparer<Vector3>
    {
        private readonly float _tolerance;
        public Near(float tolerance) => _tolerance = tolerance;
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < _tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }
}
