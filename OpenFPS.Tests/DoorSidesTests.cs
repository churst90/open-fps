using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Push and pull, the key, and nobody swept aside (Cody, 2026-10-05): "some doors are pull open while
/// others are push. e.g. the push bar fire doors you push the bar to get out of the building, but, in
/// the case of my apartment building, you have to unlock it with a key then pull the door open. as
/// long as you are standing in the way of the leaf of certain doors the door will not shut until you
/// move."
///
/// Every door here stands at the origin, unturned: local X runs across the doorway, local Z through it,
/// and its +Z face is its outside.
/// </summary>
public class DoorSidesTests : IDisposable
{
    private const float Dt = PhysicsConstants.FixedDeltaTime;
    private readonly ITestOutputHelper _o;
    private readonly World _world = World.Create();
    private readonly DoorSystem _doors = new();
    private readonly PrefabRepository _prefabs = new(Path.Combine(AppContext.BaseDirectory, "prefabs"));
    private readonly List<(string Key, int Sounds, float At)> _heard = new();
    private float _clock;

    public DoorSidesTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }
    public void Dispose() => World.Destroy(_world);

    private Entity Door(string prefab)
    {
        var e = _prefabs.Spawn(_world, prefab, new Vector3(0, 1.05f, 0), Quaternion.Identity, Vector3.One);
        Tick(1);
        _heard.Clear();
        return e;
    }

    private void Tick(int n = 1)
    {
        for (int i = 0; i < n; i++)
        {
            _doors.Update(_world, Dt, _ => { }, (_, key, sounds) => _heard.Add((key, sounds.Count, _clock)));
            _clock += Dt;
        }
    }

    private void TickSeconds(float s) => Tick((int)MathF.Ceiling(s / Dt));

    private DoorComponent D(Entity e) => _world.Get<DoorComponent>(e);

    private Entity Player(Vector3 at)
        => _world.Create(new PlayerComponent { Username = "tester" }, new Transform { Position = at, Rotation = Quaternion.Identity });

    private Entity Walker(Vector3 at)
        => _world.Create(new Pedestrian { Voice = "", Pair = "" }, new Transform { Position = at, Rotation = Quaternion.Identity });

    private void MoveTo(Entity person, Vector3 at) => _world.Get<Transform>(person).Position = at;

    /// <summary>Where the leaf's middle is through the doorway (local Z): which side it has swung to.</summary>
    private float Through(Entity e) => _world.Get<Transform>(e).Position.Z;

    private string Events(int from = 0) => string.Join(" ", _heard.Skip(from).Select(h =>
    {
        Assert.True(DoorEvents.TryParse(h.Key, out _, out string ev), h.Key);
        return ev + (h.Sounds > 0 ? "+" : "");
    }));

    // ── Push and pull ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every hinged door is pushed from one face and pulled from the other: pushed, it swings away from
    /// whoever pushed it; pulled, toward them. Its first event after its latch is "push" or "pull", and
    /// a push bar is only ever on the side it is pushed from: from the other side a steel door's pull
    /// handle draws the latch.
    /// </summary>
    [Theory]
    [InlineData("door", 1f, "latch-retract+ push", "latch-retract+ pull")]
    [InlineData("steel_door", -1f, "bar+ push", "latch-retract+ pull")]
    [InlineData("glass_pull_door", -1f, "push+", "pull+")]
    public void EachHingedDoorIsPushedFromOneSideAndPulledFromTheOther(string prefab, float pushSide,
                                                                        string pushed, string pulled)
    {
        var e = Door(prefab);
        Assert.Equal(pushSide, D(e).PushSide);
        foreach (bool push in new[] { true, false })
        {
            float side = push ? pushSide : -pushSide;
            var opener = Player(new Vector3(0.2f, 1.0f, 0.9f * side));
            Assert.True(DoorSystem.Set(_world, e, open: true, who: opener));
            Assert.Equal(push ? 1 : -1, D(e).OpenedFrom);
            int start = _heard.Count;
            TickSeconds(D(e).SwingSeconds + 0.2f);
            Assert.Equal(1f, D(e).Openness, 3);
            // Pushed, the leaf ends up on the far side from the hand; pulled, on the hand's side.
            Assert.True(Through(e) * side * (push ? -1f : 1f) > 0.3f, $"{prefab} {(push ? "pushed" : "pulled")}: leaf at z {Through(e):F2}");
            string got = string.Join(" ", Events(start).Split(' ').TakeWhile(ev => !ev.StartsWith("closer")));
            _o.WriteLine($"{prefab} {(push ? "pushed" : "pulled")}: {got}, leaf at z {Through(e):F2}");
            Assert.Equal(push ? pushed : pulled, got);
            _world.Destroy(opener);
            DoorSystem.Set(_world, e, open: false);
            TickSeconds(D(e).CloseAfterSeconds + MathF.Max(D(e).SwingSeconds, D(e).CloseSeconds) + 1f);
            Assert.Equal(0f, D(e).Openness);
        }
    }

    /// <summary>The swing follows the push side however the leaf is hinged.</summary>
    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(-1f, 1f)]
    [InlineData(1f, -1f)]
    [InlineData(-1f, -1f)]
    public void TheSwingFollowsThePushSideOnEitherHinge(float hinge, float pushSide)
    {
        var e = Door("door");
        _world.Get<DoorComponent>(e).HingeSide = hinge;
        _world.Get<DoorComponent>(e).PushSide = pushSide;
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, pushSide));
        TickSeconds(1.2f);
        var t = _world.Get<Transform>(e);
        // Fully open: a quarter turn, its middle half a leaf out on the side away from the push face,
        // and its hinged edge where it was.
        Assert.Equal(-0.45f * pushSide, t.Position.Z, 2);
        Assert.Equal(0.45f * hinge, t.Position.X, 2);
    }

    /// <summary>Not knowing where the hand is, a door is opened from its push side, as every door was.</summary>
    [Fact]
    public void ADoorOpenedFromNowhereIsPushed()
    {
        var e = Door("steel_door");
        DoorSystem.Set(_world, e, open: true);
        Assert.Equal(1, D(e).OpenedFrom);
        Tick();
        Assert.Equal("bar+ push", Events());
    }

    /// <summary>Sliding leaves are neither pushed nor pulled.</summary>
    [Fact]
    public void ASlidingDoorHasNoPushSide()
    {
        var e = Door("patio_door");
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, -1f));
        Assert.Equal(0, D(e).OpenedFrom);
        Tick();
        Assert.Equal("latch-retract+ rollers", Events());
    }

    /// <summary>What a player is told, by how the door was opened.</summary>
    [Fact]
    public void WhatOpeningADoorSays()
    {
        var knob = Door("door");
        DoorSystem.Set(_world, knob, open: true, by: new Vector3(0f, 1f, 1f));
        Assert.Equal("You push the flat door open", DoorSystem.OpenedPhrase(D(knob), "flat door"));
        DoorSystem.Set(_world, knob, open: false);
        TickSeconds(2f);
        DoorSystem.Set(_world, knob, open: true, by: new Vector3(0f, 1f, -1f));
        Assert.Equal("You pull the flat door open", DoorSystem.OpenedPhrase(D(knob), "flat door"));

        var steel = Door("steel_door");
        DoorSystem.Set(_world, steel, open: true, by: new Vector3(0f, 1f, -1f));
        Assert.Equal("You push the bar and the stair door swings open", DoorSystem.OpenedPhrase(D(steel), "stair door"));

        var front = Door("glass_front_door");
        DoorSystem.Set(_world, front, open: true, by: new Vector3(0f, 1f, 1.5f));
        Assert.Equal("You unlock the front entrance with your key and pull it open", DoorSystem.OpenedPhrase(D(front), "front entrance"));

        var patio = Door("patio_door");
        DoorSystem.Set(_world, patio, open: true, by: new Vector3(0f, 1f, 1f));
        Assert.Equal("The back door slides open", DoorSystem.OpenedPhrase(D(patio), "back door"));
    }

    // ── The key ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// From the street, an apartment building's front door is locked: the key goes in, turns, draws the
    /// latch, and only then is the leaf pulled open, toward the street. Each is its own event at its own
    /// moment. Shut again, it is locked again; from inside its bar pushes it open with no key at all.
    /// </summary>
    [Fact]
    public void TheFrontDoorIsUnlockedWithAKeyAndPulledFromTheStreet()
    {
        var e = Door("glass_front_door");
        var street = new Vector3(0.3f, 1.0f, 1.2f);
        float start = _clock;
        Assert.True(DoorSystem.Set(_world, e, open: true, by: street));
        Assert.True(D(e).KeyTurned);
        TickSeconds(DoorSystem.KeySequenceSeconds - 0.1f);
        Assert.Equal(0f, D(e).Openness);                                 // the key is still turning
        TickSeconds(D(e).SwingSeconds + 0.3f);
        Assert.Equal(1f, D(e).Openness, 3);
        Assert.True(Through(e) > 0.3f, "pulled toward the street");

        var keyed = _heard.TakeWhile(h => !h.Key.EndsWith(":closer")).ToList();
        foreach (var h in keyed) _o.WriteLine($"{h.At - start:F2} s  {h.Key}{(h.Sounds > 0 ? " +sound" : "")}");
        Assert.Equal("key-insert+ key-turn unlock pull+", Events().Split(" closer")[0]);
        Assert.InRange(keyed[0].At - start, 0f, 0.01f);
        Assert.InRange(keyed[1].At - start, DoorSystem.KeyTurnSeconds - 0.05f, DoorSystem.KeyTurnSeconds + 0.01f);
        Assert.InRange(keyed[2].At - start, DoorSystem.UnlockSeconds - 0.05f, DoorSystem.UnlockSeconds + 0.01f);
        Assert.InRange(keyed[3].At - start, DoorSystem.KeySequenceSeconds - 0.05f, DoorSystem.KeySequenceSeconds + 0.01f);

        // The closer shuts it, and it is locked again: the key, again.
        TickSeconds(D(e).CloseAfterSeconds + D(e).CloseSeconds + 1f);
        Assert.Equal(0f, D(e).Openness);
        _heard.Clear();
        Assert.True(DoorSystem.Set(_world, e, open: true, by: street));
        Assert.True(D(e).KeyTurned);
        TickSeconds(D(e).CloseAfterSeconds + D(e).CloseSeconds + D(e).SwingSeconds + 2f);
        Assert.StartsWith("key-insert+ key-turn unlock pull+", Events());
        Assert.Equal(0f, D(e).Openness);

        // From inside: the bar, and it swings out all the same.
        _heard.Clear();
        Assert.True(DoorSystem.Set(_world, e, open: true, by: new Vector3(0.3f, 1.0f, -1.2f)));
        Assert.False(D(e).KeyTurned);
        TickSeconds(D(e).SwingSeconds + 0.2f);
        Assert.Equal("bar+ push", Events());
        Assert.True(Through(e) > 0.3f, "pushed out toward the street");
    }

    /// <summary>A door already standing open is not locked: no key from either side.</summary>
    [Fact]
    public void AnOpenDoorNeedsNoKey()
    {
        var e = Door("glass_front_door");
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, -1f));
        TickSeconds(0.5f);
        DoorSystem.Set(_world, e, open: false);
        Tick(3);
        _heard.Clear();
        Assert.True(D(e).Openness > 0f);
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, 1.5f));
        Assert.False(D(e).KeyTurned);
        Tick();
        Assert.DoesNotContain(_heard, h => h.Key.Contains(":key"));
    }

    /// <summary>Shutting it while the key is still turning leaves it shut, and the leaf never moved.</summary>
    [Fact]
    public void ShuttingItWhileTheKeyTurnsLeavesItShut()
    {
        var e = Door("glass_front_door");
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, 1.5f));
        TickSeconds(0.3f);
        Assert.True(DoorSystem.Set(_world, e, open: false));
        TickSeconds(2f);
        Assert.Equal(0f, D(e).Openness);
        Assert.Equal(0f, D(e).KeySeconds);
        Assert.DoesNotContain(_heard, h => h.Key.EndsWith(":pull") || h.Key.EndsWith(":unlock"));
    }

    /// <summary>
    /// The map can lock any door from either side over its prefab, and turn which way it is pushed: a fire
    /// exit keyed outside is unlocked with a key and pulled by its handle, and still pushed by its bar
    /// from inside.
    /// </summary>
    [Fact]
    public void AMapCanKeyADoorAndTurnItsPush()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"doors-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "doortest.json"), """
            {
              "Id": "doortest",
              "Size": { "X": 40, "Y": 20, "Z": 40 },
              "MinBound": { "X": -20, "Y": -5, "Z": -20 },
              "MaxBound": { "X": 20, "Y": 15, "Z": 20 },
              "SpawnPoint": { "Position": { "X": 0, "Y": 1, "Z": 5 }, "Rotation": { "X": 0, "Y": 0, "Z": 0, "W": 1 } },
              "Entities": [
                { "EntityId": 1, "PrefabId": "concrete_floor", "Position": { "X": 0, "Y": -0.1, "Z": 0 } },
                { "EntityId": 2, "PrefabId": "steel_door", "Position": { "X": 0, "Y": 1.05, "Z": 0 }, "Name": "fire exit", "KeyedSide": 1 },
                { "EntityId": 3, "PrefabId": "door", "Position": { "X": 6, "Y": 1.05, "Z": 0 }, "Name": "cupboard", "PushSide": -1, "KeyedSide": -1 }
              ]
            }
            """);
            var maps = new MapManager(new MapRepository(dir), _prefabs);
            maps.Initialize();
            Assert.True(maps.TryGetMap("doortest", out World world, out _, out _, out _));
            Entity exit = default, cupboard = default;
            world.Query(new QueryDescription().WithAll<DoorComponent, IdentityComponent>(), (Entity e, ref IdentityComponent id) =>
            {
                if (id.Name == "fire exit") exit = e;
                if (id.Name == "cupboard") cupboard = e;
            });
            Assert.Equal(1f, world.Get<DoorComponent>(exit).KeyedSide);
            Assert.Equal(-1f, world.Get<DoorComponent>(exit).PushSide);      // the prefab's
            Assert.Equal(-1f, world.Get<DoorComponent>(cupboard).KeyedSide);
            Assert.Equal(-1f, world.Get<DoorComponent>(cupboard).PushSide);

            var doors = new DoorSystem();
            var heard = new List<string>();
            void Run(float seconds)
            {
                for (int i = 0; i < (int)(seconds / Dt); i++)
                    doors.Update(world, Dt, _ => { }, (_, key, s) => heard.Add(key + (s.Count > 0 ? "+" : "")));
            }
            Run(0.1f);
            heard.Clear();
            Assert.True(DoorSystem.Set(world, exit, open: true, by: new Vector3(0f, 1f, 1.2f)));     // outside
            Run(DoorSystem.KeySequenceSeconds + 0.2f);
            Assert.Equal(new[] { "door:pushbar:key-insert+", "door:pushbar:key-turn", "door:pushbar:unlock", "door:pushbar:pull" }, heard);

            heard.Clear();
            Assert.True(DoorSystem.Set(world, cupboard, open: true, by: new Vector3(6f, 1f, -1.2f)));  // keyed, and the push side
            Run(DoorSystem.KeySequenceSeconds + 0.2f);
            Assert.Equal(new[] { "door:knob:key-insert+", "door:knob:key-turn", "door:knob:unlock", "door:knob:latch-retract+", "door:knob:push" }, heard);
            Assert.Equal("You unlock the cupboard with your key and push it open",
                         DoorSystem.OpenedPhrase(world.Get<DoorComponent>(cupboard) with { KeyTurned = true }, "cupboard"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Nobody is swept aside ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A door with no closer, shut by hand, never sweeps through anybody: with somebody in the way of the
    /// leaf it is not shut at all, and the player is told who is in the way; once they have moved it
    /// shuts. A person standing behind the open leaf, between it and the wall, is not in its way.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHandNeverShutsADoorThroughAnybody(bool player)
    {
        var e = Door("door");                                    // swings to -Z, hinged at +X
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, 1f));
        TickSeconds(1.2f);
        Assert.Equal(1f, D(e).Openness, 3);

        var closer = Player(new Vector3(-0.2f, 1f, 1.3f));       // on the push side, out of the doorway
        // In front of the open leaf, in the quarter it sweeps shutting: half a metre into the room.
        var inTheWay = player ? Player(new Vector3(0f, 1f, -0.5f)) : Walker(new Vector3(0f, 1f, -0.5f));
        Assert.Equal(inTheWay, DoorSystem.InTheWay(_world, e, 0f, closer));
        Assert.Equal("Someone is in the way of the door.", DoorSystem.InTheWayLine(inTheWay, closer, "door"));
        Assert.False(DoorSystem.Set(_world, e, open: false, who: closer));
        TickSeconds(2f);
        Assert.Equal(1f, D(e).Openness, 3);

        // Behind the open leaf, against the wall the leaf has swung to: the leaf moves away from them.
        MoveTo(inTheWay, new Vector3(0.75f, 1f, -0.6f));
        Assert.Null(DoorSystem.InTheWay(_world, e, 0f, closer));
        Assert.True(DoorSystem.Set(_world, e, open: false, who: closer));
        TickSeconds(1.5f);
        Assert.Equal(0f, D(e).Openness);
    }

    /// <summary>
    /// Somebody stepping into a leaf's way while a hand is shutting it stops it against them, and it
    /// goes on shutting once they have moved: for a swinging leaf and a sliding one alike.
    /// </summary>
    [Theory]
    [InlineData("door", -0.35f)]
    [InlineData("patio_door", 0f)]
    public void AHandClosingStopsAgainstSomebodyAndGoesOnWhenTheyMove(string prefab, float z)
    {
        var e = Door(prefab);
        var hand = Player(new Vector3(0f, 1f, 1.4f));
        DoorSystem.Set(_world, e, open: true, who: hand);
        TickSeconds(D(e).SwingSeconds + 0.2f);
        Assert.True(DoorSystem.Set(_world, e, open: false, who: hand));
        while (D(e).Openness > 0.6f) Tick();
        var walker = Walker(new Vector3(0f, 1f, z));            // steps into the doorway
        float held = D(e).Openness;
        TickSeconds(3f);
        Assert.True(D(e).Openness >= held - 0.05f, $"{prefab} went on through them: {D(e).Openness:F2}");
        Assert.True(D(e).Openness > 0.3f);
        MoveTo(walker, new Vector3(3f, 1f, 3f));
        TickSeconds(D(e).SwingSeconds + 0.5f);
        Assert.Equal(0f, D(e).Openness);
    }

    /// <summary>
    /// Whoever is shutting a door walks it shut, so they are never in their own way, unless they are
    /// standing in the doorway it shuts into: then they are told to step out of it.
    /// </summary>
    [Fact]
    public void YouAreInTheWayOnlyStandingInTheDoorway()
    {
        var e = Door("door");
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, 1f));
        TickSeconds(1.2f);

        var you = Player(new Vector3(-0.1f, 1f, -0.6f));         // in the room, in the quarter it sweeps
        Assert.Null(DoorSystem.InTheWay(_world, e, 0f, you));
        Assert.NotNull(DoorSystem.InTheWay(_world, e, 0f));      // anybody else there would be

        MoveTo(you, new Vector3(-0.1f, 1f, 0.05f));             // in the doorway itself
        var blocker = DoorSystem.InTheWay(_world, e, 0f, you);
        Assert.Equal(you, blocker);
        Assert.Equal("You are in the way of the door. Step out of the doorway first.", DoorSystem.InTheWayLine(blocker!.Value, you, "door"));
        Assert.False(DoorSystem.Set(_world, e, open: false, who: you));

        MoveTo(you, new Vector3(-0.1f, 1f, -0.6f));
        Assert.True(DoorSystem.Set(_world, e, open: false, who: you));
        TickSeconds(1.2f);
        Assert.Equal(0f, D(e).Openness);
    }

    /// <summary>
    /// A door pushed open into somebody standing behind it stops against them, and opens the rest of the
    /// way once they move. Whoever pulls a door open steps back with it, and is not in its way.
    /// </summary>
    [Fact]
    public void ALeafOpeningStopsAgainstSomebodyBehindIt()
    {
        var e = Door("door");                                    // pushed from +Z, swings to -Z
        var behind = Walker(new Vector3(-0.3f, 1f, -0.7f));
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, 1f));
        TickSeconds(2f);
        float stopped = D(e).Openness;
        _o.WriteLine($"stopped at {stopped:F2} open against somebody behind it");
        Assert.InRange(stopped, 0.05f, 0.9f);
        MoveTo(behind, new Vector3(3f, 1f, -3f));
        TickSeconds(1.2f);
        Assert.Equal(1f, D(e).Openness, 3);

        // Pulled toward the hand, standing where the leaf will swing through.
        var steel = _prefabs.Spawn(_world, "steel_door", new Vector3(5, 1.05f, 0), Quaternion.Identity, Vector3.One);
        Tick();
        var puller = Player(new Vector3(5f, 1f, 0.7f));         // outside: the pull side, in the sweep
        Assert.True(DoorSystem.Set(_world, steel, open: true, who: puller));
        TickSeconds(D(steel).SwingSeconds + 0.2f);
        Assert.Equal(1f, D(steel).Openness, 3);
    }

    /// <summary>
    /// A closer is held by anybody in the way of its leaf, even outside the doorway itself: here, out
    /// in front of where the open leaf's edge sweeps. A motor closing on somebody in its leaf's path opens
    /// again.
    /// </summary>
    [Fact]
    public void ACloserIsHeldByAnybodyInItsSweep()
    {
        var e = Door("steel_door");                              // swings out, to +Z, hinged at +X
        DoorSystem.Set(_world, e, open: true, by: new Vector3(0f, 1f, -1f));
        TickSeconds(D(e).SwingSeconds + 0.2f);
        // Through the doorway further than the old doorway box reached (1 m), inside the leaf's reach.
        var waiting = Walker(new Vector3(-0.1f, 1f, 1.1f));
        TickSeconds(D(e).CloseAfterSeconds + D(e).CloseSeconds + 2f);
        Assert.True(D(e).Openness > 0.3f, $"the closer swept through them: {D(e).Openness:F2}");
        MoveTo(waiting, new Vector3(4f, 1f, 4f));
        TickSeconds(D(e).CloseAfterSeconds + D(e).CloseSeconds + 2f);
        Assert.Equal(0f, D(e).Openness);
    }

    /// <summary>The swept ground, checked point by point for a quarter-turn leaf 0.9 m wide.</summary>
    [Fact]
    public void TheSweptGround()
    {
        var e = Door("door");                                    // hinge at x +0.45, swings to -Z
        var d = D(e);
        d.Openness = 1f;
        bool Shutting(float x, float z) => DoorSystem.Sweeps(_world, e, d, 1f, 0f, new Vector3(x, 1f, z));
        bool Opening(float x, float z) => DoorSystem.Sweeps(_world, e, d, 0f, 1f, new Vector3(x, 1f, z));

        Assert.True(Shutting(0f, -0.5f));                        // in the room, in front of the leaf
        Assert.True(Shutting(0f, 0f));                           // in the doorway
        Assert.True(Shutting(-0.3f, 0.2f));                      // just outside it, within a body
        Assert.False(Shutting(0f, 0.8f));                        // outside, clear of it
        Assert.False(Shutting(0.8f, -0.5f));                     // behind the open leaf, by the wall
        Assert.False(Shutting(-0.2f, -1.5f));                    // further in than the leaf reaches
        Assert.False(Shutting(0f, -0.5f) && DoorSystem.Sweeps(_world, e, d, 1f, 1f, new Vector3(0f, 1f, -0.5f)));

        Assert.True(Opening(0f, -0.5f));                         // behind it as it opens
        Assert.True(Opening(0.7f, -0.5f));                       // against the wall it opens to
        Assert.False(Opening(0f, 0.6f));                         // the side it is pushed from
        Assert.False(Opening(0f, -0.5f) && DoorSystem.Sweeps(_world, e, d, 0f, 1f, new Vector3(0f, 4.5f, -0.5f)));
    }

    // ── The city ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The city's doors swing the right way: a room door into its room, and an exit (a push bar, a
    /// building's front entrance) out of it, toward the street or the roof. Each door's first place is
    /// the room it belongs to.
    /// </summary>
    [Fact]
    public void TheCitysDoorsSwingTheRightWay()
    {
        var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), _prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out World world, out _, out _, out var lookup));
        int into = 0, outOf = 0;
        var wrong = new List<string>();
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent, PortalComponent>(),
            (Entity e, ref Transform t, ref DoorComponent d, ref PortalComponent p) =>
            {
                if (d.Slides) return;
                var kind = (DoorKind)d.Kind;
                var swingsTo = Vector3.Transform(Vector3.UnitZ, t.Rotation) * -d.PushSide;
                bool towardRoom = Vector3.Dot(world.Get<Transform>(lookup[p.RegionAId]).Position - t.Position, swingsTo) > 0f;
                bool exit = DoorSystem.HasBar(kind);
                if (towardRoom == !exit) { if (exit) outOf++; else into++; }
                else wrong.Add($"{(world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name : e.Id.ToString())} ({DoorEvents.Slug(kind)})");
            });
        _o.WriteLine($"{into} doors swing into their rooms, {outOf} exits swing out; wrong: {string.Join(", ", wrong.Take(10))}");
        Assert.Empty(wrong);
        Assert.Equal(397, into);
        Assert.Equal(12, outOf);
    }
}
