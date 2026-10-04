using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Core.Session;
using OpenFPS.Client.Gtk.Game;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Comma and period: the things of one kind near you, nearest first; Shift with either changes the
/// kind, as Shift and the brackets change the chat ring (Cody, 2026-10-04). See MapTracker.
/// </summary>
public class MapTrackerTests
{
    private readonly ITestOutputHelper _o;
    public MapTrackerTests(ITestOutputHelper o) => _o = o;

    /// <summary>
    /// The real city, from the pavement outside Brandt Court's glass front door: the door is the
    /// nearest entrance, no flat's door is an entrance, and every category answers in a sentence.
    /// </summary>
    [Fact]
    public void OnTheCityBrandtCourtsFrontDoorIsTheNearestEntrance()
    {
        AcousticRegistry.Initialize();
        var prefabs = new PrefabRepository(System.IO.Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(System.IO.Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMap("city", out var ecs, out _, out _, out _));
        var world = new WorldSnapshot();
        foreach (var def in EntityDefinitionFactory.StaticDefinitions(ecs))
        {
            world.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
            if (def.Region.RoomSize.X > 0f) world.RegionEntityIds.Add(def.EntityId);
        }
        var feet = new Vector3(-6.5f, 0.05f, 157.1f);   // outside, east of the door, facing north
        var entrances = MapTracker.Gather(world, feet, TrackCategory.Entrances, -1);
        Assert.Equal("Brandt Court front entrance", entrances[0].Name);
        Assert.DoesNotContain(entrances, e => e.Name.Contains(" flat ", StringComparison.Ordinal));
        foreach (var c in MapTracker.Ring)
        {
            var t = new MapTracker(c);
            _o.WriteLine(t.CycleCategory(0, world, feet, -1));
            for (int i = 0; i < 3; i++) _o.WriteLine("  " + t.Step(1, world, feet, 0f, -1));
        }
    }

    private static EntitySnapshot Thing(int id, string name, Vector3 at, string beacon = "",
                                        EntityType type = EntityType.StaticObject, Vector3? size = null)
    {
        var def = new EntityDefinition { EntityId = id, Type = type };
        def.Identity = new IdentityComponent { Name = name, BeaconCategory = beacon };
        def.Collider = new ColliderComponent { Size = size ?? new Vector3(0.9f, 2.1f, 0.05f), IsSolid = true };
        def.Transform = new Transform { Position = at, Rotation = Quaternion.Identity };
        return new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
    }

    private static WorldSnapshot World(params EntitySnapshot[] things)
    {
        var w = new WorldSnapshot();
        foreach (var t in things) w.Entities[t.Id] = t;
        return w;
    }

    private static EntitySnapshot Door(int id, string name, Vector3 at) => Thing(id, name, at + new Vector3(0, 1.05f, 0), Beacons.Door);

    [Fact]
    public void PeriodStepsNearestFirstAndCommaStepsBack()
    {
        var world = World(Door(1, "Far door", new Vector3(0, 0, 30)),
                          Door(2, "Near door", new Vector3(0, 0, 5)),
                          Door(3, "Middle door", new Vector3(12, 0, 0)));
        var t = new MapTracker(TrackCategory.Doors);
        var said = new List<string>();
        for (int i = 0; i < 4; i++) said.Add(t.Step(1, world, Vector3.Zero, 0f, -1));
        Assert.StartsWith("Near door", said[0]);
        Assert.StartsWith("Middle door", said[1]);
        Assert.StartsWith("Far door", said[2]);
        Assert.StartsWith("Farthest. Far door", said[3]);
        Assert.StartsWith("Middle door", t.Step(-1, world, Vector3.Zero, 0f, -1));
        Assert.StartsWith("Near door", t.Step(-1, world, Vector3.Zero, 0f, -1));
        Assert.StartsWith("Nearest. Near door", t.Step(-1, world, Vector3.Zero, 0f, -1));
    }

    [Fact]
    public void AFloorUpIsFurtherThanItsStraightLine()
    {
        // The roof door is 18 metres overhead and 4 along; the next building's door 16 metres down the
        // pavement. On foot the pavement one is nearer, and is said first.
        var world = World(Door(1, "Roof access door", new Vector3(-4, 18, 0)), Door(2, "Selby House front entrance", new Vector3(16, 0, 0)));
        var t = new MapTracker(TrackCategory.Doors);
        Assert.Equal("Selby House front entrance, right, 16 metres.", t.Step(1, world, Vector3.Zero, 0f, -1));
        Assert.Equal("Roof access door, left, 4 metres, 6 floors up.", t.Step(1, world, Vector3.Zero, 0f, -1));
    }

    [Fact]
    public void TheFirstKeyOfEitherKindSaysTheNearest()
    {
        var world = World(Door(1, "Far door", new Vector3(0, 0, 30)), Door(2, "Near door", new Vector3(0, 0, 5)));
        Assert.StartsWith("Near door", new MapTracker().Step(-1, world, Vector3.Zero, 0f, -1));
        Assert.StartsWith("Near door", new MapTracker().Step(1, world, Vector3.Zero, 0f, -1));
    }

    [Fact]
    public void WalkingAFewMetresWorksTheOrderOutAgain()
    {
        var world = World(Door(1, "West door", new Vector3(-10, 0, 0)), Door(2, "East door", new Vector3(10, 0, 0)));
        var t = new MapTracker();
        Assert.StartsWith("East door", t.Step(1, World(Door(1, "West door", new Vector3(-10, 0, 0)),
                                                         Door(2, "East door", new Vector3(10, 0, 0))), new Vector3(1, 0, 0), 0f, -1));
        // Walked eight metres west: the west door is nearest now, and the next key starts from it.
        Assert.StartsWith("West door", t.Step(1, world, new Vector3(-7, 0, 0), 0f, -1));
        // Half a metre more is not a new order: period goes on to the next.
        Assert.StartsWith("East door", t.Step(1, world, new Vector3(-7.5f, 0, 0), 0f, -1));
    }

    [Fact]
    public void ShiftChangesTheCategoryAndSaysHowMany()
    {
        var world = World(Door(1, "Flat 3A door", new Vector3(0, 0, 5)), Door(2, "Flat 3B door", new Vector3(3, 0, 5)),
                          Thing(3, "Rifle", new Vector3(2, 0.1f, 0), Beacons.Item, EntityType.Item, new Vector3(0.1f, 0.1f, 1f)));
        var t = new MapTracker(TrackCategory.Doors);
        Assert.Equal("Entrances, none nearby.", t.CycleCategory(1, world, Vector3.Zero, -1));
        Assert.Equal("Stairs, none nearby.", t.CycleCategory(1, world, Vector3.Zero, -1));
        Assert.Equal("Items, 1 nearby.", t.CycleCategory(1, world, Vector3.Zero, -1));
        Assert.StartsWith("Rifle, right, 2 metres", t.Step(1, world, Vector3.Zero, 0f, -1));
        Assert.Equal("Stairs, none nearby.", t.CycleCategory(-1, world, Vector3.Zero, -1));
        t.CycleCategory(-1, world, Vector3.Zero, -1);
        Assert.Equal("Doors, 2 nearby.", t.CycleCategory(-1, world, Vector3.Zero, -1));
        // Round the ring from the first to the last.
        Assert.Equal("Places, none nearby.", t.CycleCategory(-1, world, Vector3.Zero, -1));
        Assert.Equal(TrackCategory.Places, t.Category);
    }

    [Fact]
    public void AnEmptyCategorySaysSo()
    {
        var world = World(Door(1, "A door", new Vector3(0, 0, 5)));
        var t = new MapTracker(TrackCategory.Vehicles);
        Assert.Equal("No vehicles within 100 metres.", t.Step(1, world, Vector3.Zero, 0f, -1));
        Assert.Null(t.Selected);
        // Nothing past the range either.
        var far = World(Door(1, "A door", new Vector3(0, 0, 150)));
        Assert.Equal("No doors within 100 metres.", new MapTracker(TrackCategory.Doors).Step(1, far, Vector3.Zero, 0f, -1));
    }

    [Theory]
    [InlineData(0f, 10f, "in front")]
    [InlineData(7f, 7f, "right in front")]
    [InlineData(10f, 0f, "right")]
    [InlineData(7f, -7f, "right behind")]
    [InlineData(0f, -10f, "behind")]
    [InlineData(-7f, -7f, "left behind")]
    [InlineData(-10f, 0f, "left")]
    [InlineData(-7f, 7f, "left in front")]
    public void DirectionsAreTheServersEightWords(float east, float north, string word)
    {
        // Facing north (+Z), the way the game starts you; east (+X) is on your right.
        Assert.Equal(word, DirectionWords.Relative(0f, new Vector3(east, 0f, north)));
        // The server's own words come from the same place.
        Assert.Equal(word, DirectionWords.Relative(Quaternion.Identity, new Vector3(east, 0f, north)));
    }

    [Fact]
    public void DirectionsTurnWithYou()
    {
        // L turns right, which increases yaw: a quarter turn faces east, and north is then on your left.
        float east = MathF.PI / 2f;
        Assert.Equal("in front", DirectionWords.Relative(east, new Vector3(10, 0, 0)));
        Assert.Equal("left", DirectionWords.Relative(east, new Vector3(0, 0, 10)));
        Assert.Equal("behind", DirectionWords.Relative(east, new Vector3(-10, 5, 0)));
    }

    [Fact]
    public void ALineIsNameDirectionDistanceAndFloor()
    {
        var feet = new Vector3(0, 3.02f, 0);
        Assert.Equal("Roof access door, left in front, 14 metres, one floor up.",
            MapTracker.Describe(new MapTracker.Tracked(1, "Roof access door", new Vector3(-10, 7.1f, 10), 6.02f, 0f), feet, 0f));
        Assert.Equal("Lobby door, behind, 3 metres, 2 floors down.",
            MapTracker.Describe(new MapTracker.Tracked(1, "Lobby door", new Vector3(0, -1.9f, -3), -2.98f, 0f), feet, 0f));
        Assert.Equal("Bench, right, 4 metres.",
            MapTracker.Describe(new MapTracker.Tracked(1, "Bench", new Vector3(4, 3.5f, 0), 3.02f, 0f), feet, 0f));
        Assert.Equal("Stairs up, right here, one floor down.",
            MapTracker.Describe(new MapTracker.Tracked(1, "Stairs up", new Vector3(0.2f, 1f, 0), 0f, 0f), feet, 0f));
        // A kerb, a ramp, a car roof: not a floor.
        Assert.Equal("", MapTracker.Floors(1.4f));
        Assert.Equal("", MapTracker.Floors(-2.4f));
    }

    [Fact]
    public void TheTwoLeavesOfABiPartingDoorAreOneEntrance()
    {
        var world = World(Thing(1, "Terminal apron entrance, south", new Vector3(-0.5f, 1, 10), Beacons.Door),
                          Thing(2, "Terminal apron entrance, south", new Vector3(0.5f, 1, 10), Beacons.Door));
        Assert.Single(MapTracker.Gather(world, Vector3.Zero, TrackCategory.Entrances, -1));
        Assert.Single(MapTracker.Gather(world, Vector3.Zero, TrackCategory.Doors, -1));
    }

    [Fact]
    public void AnEntranceIsADoorWithTheOpenAirOnOneSide()
    {
        var hall = Thing(10, "", new Vector3(0, 1.5f, 0), size: new Vector3(10, 3, 10));
        hall.Definition.Region = new RegionComponent { FriendlyName = "Hall", IsIndoor = true, RoomSize = new Vector3(10, 3, 10) };
        var flat = Thing(11, "", new Vector3(10, 1.5f, 0), size: new Vector3(10, 3, 10));
        flat.Definition.Region = new RegionComponent { FriendlyName = "Flat", IsIndoor = true, RoomSize = new Vector3(10, 3, 10) };
        var front = Door(1, "12 Elm Street front door", new Vector3(0, 0, -5));
        front.Definition.Portal = new PortalComponent { RegionAId = 10, RegionBId = AcousticConstants.GlobalRegionId };
        var inner = Door(2, "Flat door", new Vector3(5, 0, 0));
        inner.Definition.Portal = new PortalComponent { RegionAId = 10, RegionBId = 11 };
        var world = World(hall, flat, front, inner);
        world.RegionEntityIds.AddRange(new[] { 10, 11 });

        var entrances = MapTracker.Gather(world, Vector3.Zero, TrackCategory.Entrances, -1);
        Assert.Equal(new[] { "12 Elm Street front door" }, entrances.Select(e => e.Name));
        Assert.Equal(2, MapTracker.Gather(world, Vector3.Zero, TrackCategory.Doors, -1).Count);

        // Places: the one you are in is right here, the next one along is to your right.
        var places = MapTracker.Gather(world, new Vector3(1, 0, 0), TrackCategory.Places, -1);
        Assert.Equal(new[] { "Hall", "Flat" }, places.Select(p => p.Name));
        Assert.Equal(0f, places[0].Distance);
    }

    [Fact]
    public void PeopleAreOtherPlayersAndWalkersButNeverYou()
    {
        var me = Thing(1, "cody", Vector3.Zero, Beacons.Player, EntityType.Player);
        var friend = Thing(2, "sean01", new Vector3(0, 0, 8), Beacons.Player, EntityType.Player);
        var walker = Thing(3, "Pedestrian", new Vector3(-4, 0, 0), "", EntityType.NPC);
        var car = Thing(4, "Hatchback", new Vector3(6, 0, 0), "", EntityType.NPC);
        car.Definition.SoundEmitter = new SoundEmitterComponent { SoundId = "engine:hatchback" };
        var world = World(me, friend, walker, car);
        Assert.Equal(new[] { "person", "sean01" }, MapTracker.Gather(world, Vector3.Zero, TrackCategory.People, 1).Select(p => p.Name));
        Assert.Equal(new[] { "Hatchback" }, MapTracker.Gather(world, Vector3.Zero, TrackCategory.Vehicles, 1).Select(p => p.Name));
    }

    [Fact]
    public void CategoriesAreParsedAsTyped()
    {
        Assert.Equal(TrackCategory.Doors, MapTracker.Parse("door"));
        Assert.Equal(TrackCategory.People, MapTracker.Parse("People"));
        Assert.Equal(TrackCategory.Entrances, MapTracker.Parse("exits"));
        Assert.Null(MapTracker.Parse("cheese"));
        Assert.Equal(new[] { "Doors", "Entrances", "Stairs", "Items", "People", "Vehicles", "Places" },
                     MapTracker.Ring.Select(MapTracker.NameOf));
    }

    // ── The keys ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommaPeriodAndTheirShiftsReachTheTracker()
    {
        var was = NavigationAids.Track;
        try
        {
            NavigationAids.Track = TrackCategory.Doors;
            var (session, speech) = NewClient();
            int saves = 0;
            session.SaveTrackedCategory = () => saves++;
            session.HandleMessage(new PlayerSpawned { EntityId = 1, SpawnTransform = new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity } });
            var door = Door(50, "Back door", new Vector3(0, 0, 6));
            session.World.RegisterDefinition(door.Definition);
            var rifle = Thing(51, "Rifle", new Vector3(-3, 0.1f, 0), Beacons.Item, EntityType.Item, new Vector3(0.1f, 0.1f, 1f));
            session.World.RegisterDefinition(rifle.Definition);

            Assert.True(session.Press(GameKey.Period));
            Assert.Equal("Back door, in front, 6 metres.", speech.Said.Last());
            Assert.True(session.Press(GameKey.Comma));
            Assert.Equal("Nearest. Back door, in front, 6 metres.", speech.Said.Last());

            Assert.True(session.Press(GameKey.Period, KeyModifiers.Shift));
            Assert.Equal("Entrances, none nearby.", speech.Said.Last());
            session.Press(GameKey.Period, KeyModifiers.Shift);
            session.Press(GameKey.Period, KeyModifiers.Shift);
            Assert.Equal("Items, 1 nearby.", speech.Said.Last());
            Assert.Equal(TrackCategory.Items, NavigationAids.Track);
            Assert.Equal(3, saves);
            session.Press(GameKey.Comma);
            Assert.Equal("Rifle, left, 3 metres.", speech.Said.Last());
            session.Press(GameKey.Comma, KeyModifiers.Shift);
            Assert.Equal("Stairs, none nearby.", speech.Said.Last());

            // /track says it and sets it.
            Assert.StartsWith("Comma and period step through people.", ClientGameSession.TrackCommand(new[] { "people" }, save: () => { }));
            Assert.Equal(TrackCategory.People, NavigationAids.Track);
            Assert.StartsWith("cheese is not something to track.", ClientGameSession.TrackCommand(new[] { "cheese" }, save: () => { }));
        }
        finally { NavigationAids.Track = was; }
    }

    [Fact]
    public void NoTrackerKeyIsAScreenReadersKey()
    {
        foreach (var k in new[] { GameKey.Comma, GameKey.Period })
            Assert.DoesNotContain(k, ClientGameSession.ScreenReaderKeys);
        var (session, _) = NewClient();
        Assert.True(session.IsBound(InputContext.Gameplay, GameKey.Comma));
        Assert.True(session.IsBound(InputContext.Gameplay, GameKey.Period));
        Assert.Contains("Shift comma and Shift period change the kind", ClientGameSession.KeyHelp);
    }

    /// <summary>
    /// GTK reports a character, which Shift changes; the game binds the key. Shift-comma is "&lt;" on a
    /// US layout and ";" on a German one, and both must arrive as comma. Windows reports the virtual key
    /// (Keys.Oemcomma), which Shift does not change.
    /// </summary>
    [Fact]
    public void GtkSendsTheKeyNotTheCharacter()
    {
        const uint comma = 0x2c, period = 0x2e, less = 0x3c, greater = 0x3e, semicolon = 0x3b, colon = 0x3a;
        Assert.Equal(GameKey.Comma, GtkKeyMap.Map(comma));
        Assert.Equal(GameKey.Period, GtkKeyMap.Map(period));
        // US: shift-comma and shift-period, with or without the key's own unshifted character.
        Assert.Equal(GameKey.Comma, GtkKeyMap.Map(less));
        Assert.Equal(GameKey.Period, GtkKeyMap.Map(greater));
        Assert.Equal(GameKey.Comma, GtkKeyMap.Map(less, comma));
        Assert.Equal(GameKey.Period, GtkKeyMap.Map(greater, period));
        // German: shift-comma is ";", shift-period ":". The key says comma and period.
        Assert.Equal(GameKey.Comma, GtkKeyMap.Map(semicolon, comma));
        Assert.Equal(GameKey.Period, GtkKeyMap.Map(colon, period));
        // A plain semicolon is still a semicolon.
        Assert.Equal(GameKey.Semicolon, GtkKeyMap.Map(semicolon, semicolon));
        // Letters, digits and the keypad keep what was reported: French top row unshifted is "&".
        Assert.Equal(GameKey.D1, GtkKeyMap.Map(0x31, 0x26));
        Assert.Equal(GameKey.W, GtkKeyMap.Map(0x57, 0x77));
        Assert.Equal(GameKey.Numpad5, GtkKeyMap.Map(0xffb5, 0xff9d));
        Assert.Equal(GameKey.NumpadDecimal, GtkKeyMap.Map(0xffae, 0xff9f));
    }

    private static (ClientGameSession, Speech) NewClient()
    {
        AcousticRegistry.Initialize();
        var network = new ClientNetworkService();
        network.Sending = _ => { };
        var speech = new Speech();
        var session = new ClientGameSession(network, speech, new Shell(), new AudioEngineFacade(),
            microphone: new NullMicrophoneCapture("none"), enableAudio: false);
        return (session, speech);
    }

    private sealed class Speech : ISpeechOutput
    {
        public readonly List<string> Said = new();
        public string BackendName => "test";
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Said.Add(text);
        public void Interrupt() { }
        public void Dispose() { }
    }

    private sealed class Shell : IClientShell
    {
        public bool IsGameInputActive { get; set; } = true;
        public bool? NumLockOn => true;
        public event Action<string>? CommandEntered { add { } remove { } }
        public void ShowLoading(string status, bool speak = true) { }
        public void UpdateLoadingStatus(string text, int percent) { }
        public void EnterGame() { }
        public void OpenCommandConsole() { }
        public void ShowGameMenu(Action<GameMenuChoice> chosen) { }
        public void ReturnToMenu() { }
        public void Quit() { }
    }
}
