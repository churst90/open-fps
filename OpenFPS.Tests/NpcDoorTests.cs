using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// What people the server walks do with the doors they go through (Cody, 2026-10-08, docs/CODY_ASKS
/// section 9): a door with a closer shuts itself after them; an outside door without one is shut behind
/// them; an inside door is left as found going in and shut going out. Never on anybody in the doorway.
/// </summary>
public class NpcDoorTests : IDisposable
{
    private const float Dt = 1f / 30f;
    private const string Map = "test";
    private readonly ITestOutputHelper _o;
    private readonly World _world = World.Create();
    private readonly DoorSystem _doors = new();
    private readonly DoorManners _manners = new();
    private readonly PrefabRepository _prefabs = new(Path.Combine(AppContext.BaseDirectory, "prefabs"));
    private readonly List<(double T, string Key, int Sounds)> _heard = new();
    private double _now;
    /// <summary>When a hand started shutting a door with somebody in its doorway: never.</summary>
    private readonly List<string> _shutOnSomebody = new();

    public NpcDoorTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }
    public void Dispose() => World.Destroy(_world);

    // ── A building ──────────────────────────────────────────────────────────────────────────────

    private Entity Region(string name, Vector3 centre, Vector3 size)
        => _world.Create(new Transform { Position = centre, Rotation = Quaternion.Identity },
                         new RegionComponent { FriendlyName = name, IsIndoor = true, RoomSize = size });

    /// <summary>
    /// A door at the origin, its doorway along X and through it along Z. A room on its -Z side; on its
    /// +Z side, the street (<paramref name="outside"/>) or a hall that opens onto the street elsewhere.
    /// </summary>
    private Entity Door(string prefab, bool outside)
    {
        var room = Region("room", new Vector3(0f, 1.5f, -4f), new Vector3(6f, 3f, 8f));
        var door = _prefabs.Spawn(_world, prefab, new Vector3(0f, 1.05f, 0f), Quaternion.Identity, Vector3.One);
        if (!_world.Has<PortalComponent>(door)) _world.Add(door, new PortalComponent());
        ref var portal = ref _world.Get<PortalComponent>(door);
        portal.RegionAId = room.Id;
        if (outside) portal.RegionBId = AcousticConstants.GlobalRegionId;
        else
        {
            var hall = Region("hall", new Vector3(0f, 1.5f, 6f), new Vector3(20f, 3f, 10f));
            portal.RegionBId = hall.Id;
            _world.Create(new PortalComponent { RegionAId = hall.Id, RegionBId = AcousticConstants.GlobalRegionId });
        }
        Tick();
        return door;
    }

    private static readonly Vector3 InRoom = new(0f, 0.5f, -2.5f), InRoomPast = new(0f, 0.5f, -1.4f), InRoomFar = new(0.5f, 0.5f, -6f);
    private static readonly Vector3 OnStreet = new(0f, 0.5f, 2.5f), OnStreetPast = new(0f, 0.5f, 1.4f), OnStreetFar = new(-0.5f, 0.5f, 7f);

    private Entity Npc(Vector3 at)
        => _world.Create(new Pedestrian { Voice = "", Pair = "" }, new Transform { Position = at, Rotation = Quaternion.Identity }, new Velocity());

    private Entity Player(Vector3 at)
        => _world.Create(new PlayerComponent { Username = "tester" }, new Transform { Position = at, Rotation = Quaternion.Identity });

    private DoorComponent D(Entity e) => _world.Get<DoorComponent>(e);

    private void Tick(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            var before = new Dictionary<int, float>();
            _world.Query(new QueryDescription().WithAll<DoorComponent>(), (Entity e, ref DoorComponent d) => before[e.Id] = d.Target);
            _manners.Update(Map, _world, Dt);
            // A hand that has just asked a door to shut: nobody may be in its doorway.
            _world.Query(new QueryDescription().WithAll<DoorComponent>(), (Entity e, ref DoorComponent d) =>
            {
                if (before.TryGetValue(e.Id, out float was) && was > 0f && d.Target <= 0f && !d.SelfClosing)
                {
                    var door = e;
                    _world.Query(new QueryDescription().WithAll<Transform>().WithAny<PlayerComponent, Pedestrian>(), (Entity p, ref Transform t) =>
                    {
                        if (DoorSystem.InDoorway(_world, door, t.Position)) _shutOnSomebody.Add($"door {door.Id} shut with {p.Id} in its doorway at {_now:F2} s");
                    });
                }
            });
            _doors.Update(_world, Dt, _ => { }, (_, key, sounds) => _heard.Add((_now, key, sounds.Count)));
            _now += Dt;
        }
    }

    private void TickSeconds(float s) => Tick((int)MathF.Ceiling(s / Dt));

    /// <summary>Walks somebody straight to a point at a walking pace, the world going on round them.</summary>
    private void WalkTo(Entity who, Vector3 to, float speed = 1.2f)
    {
        for (int guard = 0; guard < 2000; guard++)
        {
            ref var t = ref _world.Get<Transform>(who);
            var d = to - t.Position;
            float dist = d.Length();
            if (dist < 0.02f) { t.Position = to; return; }
            t.Position += d / dist * MathF.Min(dist, speed * Dt);
            Tick();
        }
        Assert.Fail("never got there");
    }

    /// <summary>
    /// Somebody going through a door as the server's people do: up to it, opening it if it is shut, waiting
    /// for it, through to just past it (Through), and on away from it; then a while for the door to settle.
    /// </summary>
    private DoorManners.Passage Pass(Entity door, Entity npc, Vector3 from, Vector3 past, Vector3 far)
    {
        _world.Get<Transform>(npc).Position = from;
        var passage = _manners.Reach(_world, door, from, past, npc)!;
        Assert.NotNull(passage);
        for (int i = 0; i < 300 && !DoorSystem.OpenEnough(_world, door); i++) Tick();
        Assert.True(DoorSystem.OpenEnough(_world, door), "it never opened for them");
        WalkTo(npc, past);
        _manners.Through(Map, _world, passage, npc);
        WalkTo(npc, far);
        TickSeconds(15f);
        return passage;
    }

    // ── The rule ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_rule_closers_go_by_themselves_outside_doors_are_shut_inside_doors_as_found_in_and_shut_out()
    {
        foreach (bool found in new[] { false, true })
            foreach (bool goingIn in new[] { false, true })
            {
                Assert.Equal(DoorManners.Then.LetGo, DoorManners.Decide(DoorManners.Kind.ShutsItself, goingIn, found));
                Assert.Equal(DoorManners.Then.Shut, DoorManners.Decide(DoorManners.Kind.Outside, goingIn, found));
            }
        Assert.Equal(DoorManners.Then.Shut, DoorManners.Decide(DoorManners.Kind.Inside, goingIn: true, foundOpen: false));
        Assert.Equal(DoorManners.Then.Leave, DoorManners.Decide(DoorManners.Kind.Inside, goingIn: true, foundOpen: true));
        Assert.Equal(DoorManners.Then.Shut, DoorManners.Decide(DoorManners.Kind.Inside, goingIn: false, foundOpen: false));
        Assert.Equal(DoorManners.Then.Shut, DoorManners.Decide(DoorManners.Kind.Inside, goingIn: false, foundOpen: true));
    }

    [Theory]
    [InlineData("door", true, DoorManners.Kind.Outside)]
    [InlineData("door", false, DoorManners.Kind.Inside)]
    [InlineData("patio_door", true, DoorManners.Kind.Outside)]
    [InlineData("steel_door", true, DoorManners.Kind.ShutsItself)]
    [InlineData("glass_front_door", true, DoorManners.Kind.ShutsItself)]
    [InlineData("auto_sliding_door", true, DoorManners.Kind.ShutsItself)]
    public void Each_door_is_known_for_what_it_is(string prefab, bool outside, DoorManners.Kind kind)
    {
        var door = Door(prefab, outside);
        Assert.Equal(kind, DoorManners.KindOf(_world, door));
        if (!outside)
        {
            Assert.True(DoorManners.GoingIn(_world, OnStreet, InRoom), "hall to room is going in");
            Assert.False(DoorManners.GoingIn(_world, InRoom, OnStreet), "room to hall is going out");
        }
    }

    // ── Each kind of door ───────────────────────────────────────────────────────────────────────

    /// <summary>A knob door to the street, no closer: shut behind them either way, however it was found,
    /// with its own close sound, after they are out of the doorway.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void An_outside_door_without_a_closer_is_shut_behind_them(bool goingIn, bool foundOpen)
    {
        var door = Door("door", outside: true);
        if (foundOpen) { DoorSystem.Set(_world, door, true); TickSeconds(2f); }
        var npc = Npc(goingIn ? OnStreet : InRoom);
        var passage = goingIn ? Pass(door, npc, OnStreet, InRoomPast, InRoomFar) : Pass(door, npc, InRoom, OnStreetPast, OnStreetFar);
        Assert.Equal(foundOpen, passage.FoundOpen);
        Assert.Equal(goingIn, passage.GoingIn);
        Assert.Equal(0f, D(door).Openness);
        Assert.Equal(0f, D(door).Target);
        var latch = _heard.Where(h => h.Key == "door:knob:latch").ToList();
        Assert.NotEmpty(latch);
        Assert.True(latch[^1].Sounds > 0, "the shut made no sound");
        Assert.Empty(_shutOnSomebody);
        _o.WriteLine($"going {(goingIn ? "in" : "out")}, found {(foundOpen ? "open" : "shut")}: latched at {latch[^1].T:F2} s");
    }

    /// <summary>A push-bar door with a closer: nobody shuts it, and it shuts itself once they are clear.</summary>
    [Theory]
    [InlineData("steel_door")]
    [InlineData("glass_front_door")]
    [InlineData("glass_pull_door")]
    public void A_door_with_a_closer_shuts_itself_after_them(string prefab)
    {
        var door = Door(prefab, outside: true);
        var npc = Npc(InRoom);
        var passage = Pass(door, npc, InRoom, OnStreetPast, OnStreetFar);
        Assert.Equal(DoorManners.Then.LetGo, passage.Then);
        Assert.Equal(0, _manners.Waiting);
        Assert.Equal(0f, D(door).Openness);
        Assert.Contains(_heard, h => h.Key.EndsWith(":closer"));
        Assert.Empty(_shutOnSomebody);
    }

    /// <summary>A spring-loaded door starts back about a second after the last person leaves its doorway
    /// (Cody, 2026-10-10: "should close on their own after a second of them leaving"), and its closer then
    /// sweeps and latches at its own speeds.</summary>
    [Theory]
    [InlineData("steel_door")]
    [InlineData("glass_front_door")]
    [InlineData("glass_pull_door")]
    public void A_closer_starts_back_about_a_second_after_the_last_person_leaves(string prefab)
    {
        var door = Door(prefab, outside: true);
        var npc = Npc(InRoom);
        _manners.Reach(_world, door, InRoom, OnStreetPast, npc);
        for (int i = 0; i < 300 && !DoorSystem.OpenEnough(_world, door); i++) Tick();
        double left = -1, starts = -1, latched = -1;
        var to = OnStreetFar;
        for (int i = 0; i < 30 * 30 && latched < 0; i++)
        {
            ref var t = ref _world.Get<Transform>(npc);
            var d = to - t.Position;
            if (d.Length() > 0.02f) t.Position += Vector3.Normalize(d) * MathF.Min(d.Length(), 1.2f * Dt);
            bool inIt = DoorSystem.InDoorway(_world, door, t.Position);
            Tick();
            if (inIt) { left = -1; starts = -1; }
            else if (left < 0) left = _now;
            if (left >= 0 && starts < 0 && D(door).Target <= 0f) starts = _now;
            if (starts >= 0 && D(door).Openness <= 0f) latched = _now;
        }
        _o.WriteLine($"{prefab}: out of the doorway at {left:F2} s; the closer starts back {starts - left:F2} s later and latches {latched - starts:F2} s after that");
        Assert.True(starts > 0 && latched > 0, "it never shut");
        Assert.InRange(starts - left, 0.8, 1.3);
    }

    /// <summary>The closer is held off only while somebody is in the doorway: an NPC who stops there
    /// holds it, and it shuts as soon as they move on.</summary>
    [Fact]
    public void A_closer_is_held_only_while_somebody_stands_in_the_doorway()
    {
        var door = Door("steel_door", outside: true);
        var npc = Npc(InRoom);
        _manners.Reach(_world, door, InRoom, OnStreetPast, npc);
        WalkTo(npc, new Vector3(0.1f, 0.5f, 0.5f));
        TickSeconds(10f);
        Assert.Equal(1f, D(door).Openness, 3);
        WalkTo(npc, OnStreetFar);
        TickSeconds(D(door).CloseAfterSeconds + D(door).CloseSeconds + 2f);
        Assert.Equal(0f, D(door).Openness);
    }

    /// <summary>An inside door found shut: shut again behind them, going in or out.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_inside_door_found_shut_is_shut_behind_them(bool goingIn)
    {
        var door = Door("door", outside: false);
        var npc = Npc(goingIn ? OnStreet : InRoom);
        var passage = goingIn ? Pass(door, npc, OnStreet, InRoomPast, InRoomFar) : Pass(door, npc, InRoom, OnStreetPast, OnStreetFar);
        Assert.Equal(DoorManners.Kind.Inside, passage.Kind);
        Assert.Equal(goingIn, passage.GoingIn);
        Assert.Equal(0f, D(door).Openness);
        Assert.Empty(_shutOnSomebody);
    }

    /// <summary>An inside door found open: left open going into the room, shut coming out of it.</summary>
    [Theory]
    [InlineData(true, 1f)]
    [InlineData(false, 0f)]
    public void An_inside_door_found_open_is_left_going_in_and_shut_going_out(bool goingIn, float ends)
    {
        var door = Door("door", outside: false);
        DoorSystem.Set(_world, door, true);
        TickSeconds(2f);
        var npc = Npc(goingIn ? OnStreet : InRoom);
        var passage = goingIn ? Pass(door, npc, OnStreet, InRoomPast, InRoomFar) : Pass(door, npc, InRoom, OnStreetPast, OnStreetFar);
        Assert.True(passage.FoundOpen);
        Assert.Equal(ends, D(door).Openness);
        Assert.Empty(_shutOnSomebody);
    }

    /// <summary>An automatic door opens for them by its sensor and shuts by its motor; a patio door to the
    /// garden is slid shut behind them.</summary>
    [Theory]
    [InlineData("auto_sliding_door", DoorManners.Then.LetGo)]
    [InlineData("patio_door", DoorManners.Then.Shut)]
    public void A_sliding_door_ends_shut_by_its_motor_or_by_hand(string prefab, DoorManners.Then then)
    {
        var door = Door(prefab, outside: true);
        var npc = Npc(InRoom);
        var passage = Pass(door, npc, InRoom, OnStreetPast, OnStreetFar);
        Assert.Equal(then, passage.Then);
        Assert.Equal(0f, D(door).Openness);
        Assert.Empty(_shutOnSomebody);
    }

    // ── Who is about does not change the rule; only a body in the way does ──────────────────────

    /// <summary>A player standing in the doorway: the leaf cannot go through them, so it waits, never
    /// moving onto them, and is shut a moment after they step out.</summary>
    [Fact]
    public void A_door_waits_for_a_player_in_the_doorway_and_is_shut_once_they_step_out()
    {
        var door = Door("door", outside: true);
        var player = Player(new Vector3(-0.3f, 0.5f, 0.6f));
        var npc = Npc(InRoom);
        Pass(door, npc, InRoom, OnStreetPast, OnStreetFar);
        Assert.Equal(1f, D(door).Target);
        Assert.Equal(1f, D(door).Openness, 3);
        Assert.Equal(1, _manners.Waiting);
        Assert.DoesNotContain(_heard, h => h.Key == "door:knob:latch");
        _world.Get<Transform>(player).Position = new Vector3(-0.3f, 0.5f, 4f);
        TickSeconds(4f);
        Assert.Equal(0f, D(door).Openness);
        Assert.Equal(0, _manners.Waiting);
        Assert.Empty(_shutOnSomebody);
    }

    /// <summary>Players near the door but out of its way, found open or shut, going in or out: the NPC's
    /// rule decides, and an outside door is shut (Cody, 2026-10-10; this reverses the 2026-10-02
    /// accommodation for Brandt Court's door).</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void A_player_near_the_door_does_not_change_the_rule(bool foundOpen, bool goingIn)
    {
        var door = Door("door", outside: true);
        if (foundOpen) { DoorSystem.Set(_world, door, true); TickSeconds(2f); }
        Player(new Vector3(1.5f, 0.5f, -5f));                // in the room, listening to the street
        Player(new Vector3(1.4f, 0.5f, 1.3f));               // on the step outside, beside the doorway
        var npc = Npc(goingIn ? OnStreet : InRoom);
        if (goingIn) Pass(door, npc, OnStreet, InRoomPast, new Vector3(-1.5f, 0.5f, -3f));
        else Pass(door, npc, InRoom, OnStreetPast, new Vector3(-1.5f, 0.5f, 4f));
        Assert.Equal(0f, D(door).Openness);
        Assert.Empty(_shutOnSomebody);
    }

    /// <summary>Somebody who stops in the doorway after they are through is not shut on either: the door
    /// waits until they have moved, and is shut a moment after.</summary>
    [Fact]
    public void They_shut_it_only_once_they_are_out_of_the_doorway()
    {
        var door = Door("door", outside: true);
        var npc = Npc(InRoom);
        var passage = _manners.Reach(_world, door, InRoom, OnStreetPast, npc)!;
        TickSeconds(1.5f);
        WalkTo(npc, new Vector3(0f, 0.5f, 0.5f));
        _manners.Through(Map, _world, passage, npc);
        TickSeconds(5f);
        Assert.Equal(1f, D(door).Openness, 3);              // still in it
        WalkTo(npc, OnStreetFar);
        double clearAt = _now;
        TickSeconds(5f);
        Assert.Equal(0f, D(door).Openness);
        var shut = _heard.First(h => h.Key == "door:knob:swing");
        _o.WriteLine($"out of the doorway by {clearAt:F2} s; the swing shut began at {shut.T:F2} s");
        Assert.Empty(_shutOnSomebody);
    }

    // ── An hour on the maps ─────────────────────────────────────────────────────────────────────

    private string TempMaps(params (string From, string To)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-npcdoors-" + Guid.NewGuid().ToString("N"));
        foreach (var (from, to) in files)
        {
            string dest = Path.Combine(dir, to);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, from), dest);
        }
        return dir;
    }

    private sealed class DoorWatch
    {
        public readonly Dictionary<int, double> OpenSince = new();
        public readonly Dictionary<int, double> Longest = new();
        public readonly HashSet<int> OpenAtStart = new();
        public int Openings;

        public int CountOpen(World world)
        {
            int n = 0;
            world.Query(new QueryDescription().WithAll<DoorComponent>(), (ref DoorComponent d) => { if (d.Target > 0f || d.Openness > 0f) n++; });
            return n;
        }

        public void Look(World world, double now)
        {
            world.Query(new QueryDescription().WithAll<DoorComponent>(), (Entity e, ref DoorComponent d) =>
            {
                bool open = d.Target > 0f || d.Openness > 0f;
                if (open && !OpenSince.ContainsKey(e.Id)) { OpenSince[e.Id] = now; Openings++; }
                else if (!open) OpenSince.Remove(e.Id);
                if (OpenSince.TryGetValue(e.Id, out double since))
                    Longest[e.Id] = Math.Max(Longest.GetValueOrDefault(e.Id), now - since);
            });
        }
    }

    /// <summary>
    /// An hour on the city with Alex keeping a cold night's hours and somebody parking every twenty
    /// seconds or so: every door anybody went through is shut again within a minute, and as many stand
    /// open at the end as at the start. The 352 walkers keep to the pavements and go through none.
    /// </summary>
    [Fact]
    public void Over_an_hour_on_the_city_no_door_is_left_open_behind_anybody()
    {
        string dir = TempMaps(("maps/city.json", "city.json"));
        try
        {
            var maps = new MapManager(new MapRepository(dir), _prefabs);
            maps.Initialize();
            Assert.True(maps.TryGetMapData("city", out var data));
            data.StreetLife = new StreetLifeData { ParkEverySeconds = 20 };
            var vehicles = new VehicleSystem();
            vehicles.Spawn(maps);
            var characters = new CharacterSystem { Pace = 6 };
            characters.Spawn(maps);
            Assert.True(maps.TryGetMap("city", out World world, out _, out var grid, out var lookup));
            var env = new WorldEnvironmentComponent { GameTime = 23f, Temperature = 3f, DayOfYear = 40 };
            var doors = new DoorSystem();
            var watch = new DoorWatch();
            var heard = new List<string>();

            // Where each doorway is, once captured, for watching the walkers.
            var walkers = new HashSet<int>();
            world.Query(new QueryDescription().WithAll<Pedestrian>(), (Entity e) => walkers.Add(e.Id));
            var last = new Dictionary<int, Vector3>();
            int crossings = 0;
            List<(Entity Door, Vector3 Centre, Vector3 Across, Vector3 Through, float Half)>? doorways = null;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            int openAtStart = -1;
            const double Seconds = 3600;
            for (int tick = 0; tick < Seconds / Dt; tick++)
            {
                double now = tick * Dt;
                vehicles.Update("city", world, Dt);
                characters.Update("city", world, grid, Dt, env, WeatherType.Clear);
                doors.Update(world, Dt, _ => { }, (_, key, sounds) => heard.Add(key));
                ParentSystem.Update(world, lookup);
                if (tick % 15 != 0) continue;
                if (openAtStart < 0 && now >= 2.0)
                {
                    openAtStart = watch.CountOpen(world);
                    world.Query(new QueryDescription().WithAll<DoorComponent>(), (Entity e, ref DoorComponent d) =>
                    {
                        if (d.Target > 0f || d.Openness > 0f) watch.OpenAtStart.Add(e.Id);
                    });
                    var found = new List<(Entity, Vector3, Vector3, Vector3, float)>();
                    world.Query(new QueryDescription().WithAll<DoorComponent, ColliderComponent>(), (Entity e, ref ColliderComponent c) =>
                    {
                        DoorSystem.Doorway(world, e, out var centre, out var rot);
                        found.Add((e, centre, Vector3.Transform(Vector3.UnitX, rot), Vector3.Transform(Vector3.UnitZ, rot), c.Size.X * 0.5f));
                    });
                    doorways = found;
                }
                if (openAtStart >= 0) watch.Look(world, now);
                if (doorways is { } dws)
                    world.Query(new QueryDescription().WithAll<Pedestrian, Transform>(), (Entity e, ref Transform t) =>
                    {
                        if (!walkers.Contains(e.Id)) return;
                        if (last.TryGetValue(e.Id, out var was))
                            foreach (var dw in dws)
                            {
                                if (Vector3.DistanceSquared(dw.Centre, t.Position) > 9f || MathF.Abs(t.Position.Y - dw.Centre.Y) > 1.8f) continue;
                                float a = Vector3.Dot(was - dw.Centre, dw.Through), b = Vector3.Dot(t.Position - dw.Centre, dw.Through);
                                if (MathF.Sign(a) != MathF.Sign(b) && MathF.Abs(Vector3.Dot(t.Position - dw.Centre, dw.Across)) <= dw.Half) crossings++;
                            }
                        last[e.Id] = t.Position;
                    });
            }
            int openAtEnd = watch.CountOpen(world);
            // A door opened in the last minute is somebody going through it as the hour ends, not one left open.
            double end = Seconds - Dt;
            int inUse = watch.OpenSince.Count(kv => !watch.OpenAtStart.Contains(kv.Key) && end - kv.Value < 60);
            var left = watch.Longest.Where(kv => !watch.OpenAtStart.Contains(kv.Key) && kv.Value > 60).ToList();
            _o.WriteLine($"an hour in {clock.Elapsed.TotalSeconds:F0} s: doors open at the start {openAtStart}, at the end {openAtEnd} ({inUse} in use); "
                       + $"{watch.Openings} openings of {watch.Longest.Count} doors; longest open {(watch.Longest.Count > 0 ? watch.Longest.Values.Max() : 0):F0} s; "
                       + $"left open over a minute {left.Count}; latches {heard.Count(k => k.EndsWith(":latch"))}, closers {heard.Count(k => k.EndsWith(":closer"))}; "
                       + $"walkers {walkers.Count}, doorway crossings by walkers {crossings}");
            foreach (var (id, s) in left) _o.WriteLine($"  door {id} open {s:F0} s");
            world.Query(new QueryDescription().WithAll<DoorComponent>(), (Entity e) =>
            {
                if (!watch.Longest.TryGetValue(e.Id, out double longest)) return;
                string name = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name : "";
                DoorSystem.Doorway(world, e, out var at, out _);
                _o.WriteLine($"  opened: {name} (entity {e.Id}, {DoorManners.KindOf(world, e)}, {(DoorKind)world.Get<DoorComponent>(e).Kind}) "
                           + $"at x {at.X:F1} east, y {at.Z:F1} north, z {at.Y:F1}; longest open {longest:F0} s");
            });
            Assert.True(watch.Openings > 0, "nobody went through a door in an hour");
            Assert.Empty(left);
            Assert.True(openAtEnd - inUse <= openAtStart, $"{openAtEnd} open at the end ({inUse} in use), {openAtStart} at the start");
            Assert.Equal(0, crossings);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Magnolia: its traffic for an hour, and every house door as it was. Nobody there goes
    /// indoors yet (no characters, no parking), so nothing is opened and nothing left open.</summary>
    [Fact]
    public void Over_an_hour_on_magnolia_no_door_is_left_open()
    {
        string dir = TempMaps(("places/magnolia_tx.json", "places/magnolia_tx.json"));
        try
        {
            RealPlaceMapTests.CopyGround("magnolia_tx", Path.Combine(dir, "places"));
            var maps = new MapManager(new MapRepository(dir), _prefabs);
            maps.Initialize();
            var vehicles = new VehicleSystem();
            vehicles.Spawn(maps);
            var characters = new CharacterSystem();
            characters.Spawn(maps);
            Assert.True(maps.TryGetMap("magnolia_tx", out World world, out _, out var grid, out var lookup));
            var doors = new DoorSystem();
            var watch = new DoorWatch();
            var env = new WorldEnvironmentComponent { GameTime = 23f, Temperature = 3f, DayOfYear = 40 };
            int before = watch.CountOpen(world);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int tick = 0; tick < 3600 / Dt; tick++)
            {
                vehicles.Update("magnolia_tx", world, Dt);
                characters.Update("magnolia_tx", world, grid, Dt, env, WeatherType.Clear);
                doors.Update(world, Dt, _ => { });
                ParentSystem.Update(world, lookup);
                if (tick % 30 == 0) watch.Look(world, tick * Dt);
            }
            int after = watch.CountOpen(world);
            int total = 0;
            world.Query(new QueryDescription().WithAll<DoorComponent>(), (ref DoorComponent _) => total++);
            _o.WriteLine($"an hour in {clock.Elapsed.TotalSeconds:F0} s: {total} doors, open before {before}, after {after}, openings {watch.Openings}");
            Assert.True(total > 0);
            Assert.True(after <= before);
            Assert.DoesNotContain(watch.Longest, kv => kv.Value > 60);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
