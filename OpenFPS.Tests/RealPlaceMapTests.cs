using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Systems;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The maps of real places (tools/gen_osm.py, OpenFPS.Server/maps/places). They are kept out of the
/// test output's maps folder, so the tests that load every shipped map do not each load a town; these
/// copy one into a folder of its own and load it the way the server and the client do.
/// </summary>
public class RealPlaceMapTests : IClassFixture<RealPlaceMapTests.Loaded>
{
    private readonly ITestOutputHelper _o;
    private readonly Loaded _maps;
    public RealPlaceMapTests(ITestOutputHelper o, Loaded maps) { _o = o; _maps = maps; }

    /// <summary>One load per map for the whole class: a town is several seconds.</summary>
    public sealed class Loaded : IDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<Place>> _places = new();
        private readonly List<string> _dirs = new();
        public Place Get(string id) => _places.GetOrAdd(id, k => new Lazy<Place>(() => Load(k))).Value;

        private Place Load(string id)
        {
            string dir = Path.Combine(Path.GetTempPath(), "openfps-place-" + Guid.NewGuid().ToString("N"));
            lock (_dirs) _dirs.Add(dir);
            string maps = Path.Combine(dir, "maps");
            Directory.CreateDirectory(Path.Combine(maps, "places"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "places", id + ".json"), Path.Combine(maps, "places", id + ".json"));
            AcousticRegistry.Initialize();
            var p = new Place();
            var sw = Stopwatch.StartNew();
            p.Manager = new MapManager(new MapRepository(maps), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            p.Manager.Initialize();
            p.ServerLoad = sw.Elapsed;
            Assert.True(p.Manager.TryGetMap(id, out var ecs, out Vector3 size, out _, out _));
            Assert.True(p.Manager.TryGetMapData(id, out var data));
            p.Ecs = ecs;
            p.Data = data;
            var defs = EntityDefinitionFactory.StaticDefinitions(ecs);
            p.Definitions = defs.Count;
            var world = new WorldSnapshot { StaticGrid = new SpatialGrid<int>(10.0f) };
            sw.Restart();
            foreach (var def in defs)
            {
                world.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
                if (def.Type == EntityType.StaticObject && !def.Moves && def.Collider.IsSolid)
                    world.StaticGrid.AddOverlapping(def.Transform.Position, def.Collider.Size, def.Transform.Rotation, def.EntityId, isStatic: true);
                if (def.Region.RoomSize.X > 0) world.RegionEntityIds.Add(def.EntityId);
                if (OpenFPS.Client.Core.ClientWorldState.IsMarker(def)) world.MarkerEntityIds.Add(def.EntityId);
            }
            p.ClientGrid = sw.Elapsed;
            sw.Restart();
            world.AcousticMap = AcousticVolumeGenerator.GenerateRegions(defs, size, data.MinBound, data.VoxelResolution, data.OcclusionFloor);
            p.AcousticBake = sw.Elapsed;
            p.World = world;
            return p;
        }

        public void Dispose()
        {
            foreach (var d in _dirs)
                try { Directory.Delete(d, true); } catch (IOException) { }
        }
    }

    public sealed class Place
    {
        public MapManager Manager = null!;
        public World Ecs = null!;
        public MapData Data = null!;
        public WorldSnapshot World = null!;
        public int Definitions;
        public TimeSpan ServerLoad, ClientGrid, AcousticBake;
    }

    public static IEnumerable<object[]> Places()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "places");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            yield return new object[] { Path.GetFileNameWithoutExtension(f) };
    }

    /// <summary>What it costs to load, for the record: the entity count, the server's load and the
    /// client's acoustic map. Fails only if it is out of all proportion with the city.</summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void It_loads_and_says_what_it_costs(string id)
    {
        var p = _maps.Get(id);
        long bytes = new FileInfo(Path.Combine(AppContext.BaseDirectory, "places", id + ".json")).Length;
        _o.WriteLine($"{id} ('{p.Data.DisplayName}'): {p.Data.Entities.Count} map entities, {p.Definitions} definitions, {bytes / 1048576.0:F1} MB; " +
                     $"server load {p.ServerLoad.TotalSeconds:F1} s, client grid {p.ClientGrid.TotalSeconds:F1} s, " +
                     $"acoustic map {p.AcousticBake.TotalSeconds:F1} s, {p.World.AcousticMap!.Regions.Count - 1} regions, {p.World.AcousticMap.Portals.Count} openings");
        Assert.False(p.Data.IsDefault, "a real place must not claim the landing map");
        Assert.True(p.Data.Entities.Count < 50000, $"{p.Data.Entities.Count} entities");
    }

    /// <summary>The spawn is on the ground, outdoors, in the place the map is centred on, and
    /// "where am I" there names it.</summary>
    [Theory]
    [InlineData("magnolia_tx", "31907 Bobcat Lane")]
    [InlineData("albany_or", "1042 Belmont Avenue Southwest")]
    public void The_spawn_is_where_the_place_says(string id, string expected)
    {
        if (!Places().Any(o => (string)o[0] == id)) return;
        var p = _maps.Get(id);
        var at = p.Data.SpawnPoint.Position;
        var eye = at + new Vector3(0, 1.6f, 0);
        string place = CommandHandler.PlaceAt(p.Ecs, eye);
        _o.WriteLine($"spawn ({at.X:F2}, {at.Z:F2}, {at.Y:F2}) /tp {at.X:F1} {at.Z:F1} {at.Y:F1}: '{place}'");
        Assert.Equal(expected, place);
        var acoustics = new SpatialAcoustics();
        int room = acoustics.GetRegionAt(p.World, eye);
        Assert.False(p.World.AcousticMap!.Regions.TryGetValue(room, out var r) && r.IsIndoor, "the spawn is indoors");
        // Nothing solid where a body stands.
        var hits = p.World.StaticGrid!.GetItemsInRadius(at, 30f).Distinct();
        var inside = hits.Where(h => p.World.Entities.TryGetValue(h, out var e)
                                     && GeometryUtils.IsPointInOBB(at + new Vector3(0, 1.0f, 0), e.Transform.Position, e.Definition.Collider.Size, e.Transform.Rotation))
                         .Select(h => p.World.Entities[h].Definition.Identity.Name).ToList();
        Assert.True(inside.Count == 0, "the spawn stands in " + string.Join(", ", inside));
    }

    /// <summary>The roads make a network with nothing wrong in it.</summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void The_roads_are_a_sound_network(string id)
    {
        var p = _maps.Get(id);
        var net = new RoadNetwork(p.Data.Roads, p.Data.Junctions);
        foreach (var problem in net.Problems.Take(20)) _o.WriteLine(problem);
        _o.WriteLine($"{p.Data.Roads!.Count} roads, {p.Data.Junctions!.Count} junctions, {net.Segments.Count} lane segments, {net.DeadEnds} dead ends");
        Assert.Empty(net.Problems);
    }

    /// <summary>
    /// Every door joins the two places either side of it: a step inside its leaf is the room it belongs
    /// to and a step outside is the other one, so nobody walks out of a front door into the wrong yard
    /// or through a door into a wall.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Every_door_joins_the_places_either_side_of_it(string id)
    {
        var p = _maps.Get(id);
        var acoustics = new SpatialAcoustics();
        var bad = new List<string>();
        int doors = 0;
        foreach (var e in p.World.Entities.Values)
        {
            var d = e.Definition;
            if (!DoorPrefabs.Contains(d.Identity.PrefabId) || d.Portal.RegionAId == d.Portal.RegionBId) continue;
            doors++;
            var normal = Vector3.Transform(Vector3.UnitZ, e.Transform.Rotation);
            var at = new Vector3(e.Transform.Position.X, 1.6f, e.Transform.Position.Z);
            int front = acoustics.GetRoomAt(p.World, at + normal * 0.6f), back = acoustics.GetRoomAt(p.World, at - normal * 0.6f);
            var sides = new HashSet<int> { front, back };
            if (!sides.Contains(d.Portal.RegionAId) || !sides.Contains(d.Portal.RegionBId))
                bad.Add($"{d.Identity.Name}: joins {Name(p, d.Portal.RegionAId)} and {Name(p, d.Portal.RegionBId)}, has {Name(p, front)} and {Name(p, back)} either side");
        }
        foreach (var b in bad.Take(15)) _o.WriteLine(b);
        _o.WriteLine($"{doors} doors, {bad.Count} wrong");
        Assert.True(bad.Count <= doors / 100, $"{bad.Count} of {doors} doors do not join the places either side of them");
    }

    /// <summary>
    /// A yard, a road or a junction is never drawn through a room. They are named places, and a named
    /// place over a room is what the room is called: a house with its yard's name inside it is a house
    /// you are told you are outside of. Measured at each room's middle, at head height.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void No_named_place_holds_a_room(string id)
    {
        var p = _maps.Get(id);
        var map = p.World.AcousticMap!;
        var bad = new List<string>();
        int rooms = 0;
        foreach (var (rid, r) in map.Regions)
        {
            if (!r.IsIndoor || !map.RegionPositions.TryGetValue(rid, out var c)) continue;
            rooms++;
            var head = c with { Y = c.Y - r.RoomSize.Y / 2 + 1.6f };
            if (OpenFPS.Client.Core.NamedPlaces.At(p.World, head) is int place)
                bad.Add($"'{p.World.Entities[place].Definition.Identity.Name}' holds the room '{r.FriendlyName}'");
        }
        foreach (var b in bad.Take(15)) _o.WriteLine(b);
        _o.WriteLine($"{rooms} rooms, {bad.Count} under a named place");
        Assert.True(bad.Count <= rooms / 100, $"{bad.Count} rooms under named places, of {rooms}");
    }

    /// <summary>
    /// A building is tagged whole with one tile, the one its middle is in. Cut by a 250 m tile edge, a
    /// loader streaming by the generator's "Tile" got half a house: a room whose doorway was in the next
    /// tile, walls on one side and not the other. Resonance counted 104 such on Magnolia (2026-10-06);
    /// gen_osm.py counts 156 there and 138 on Albany by where each part's own centre stands.
    ///
    /// Held two ways: every group of rooms joined by doors and doorways (a house, as MapTiles groups
    /// one) carries one tile, and so does every wall, roof, floor and door named for a building whose
    /// name no other group shares.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void A_building_is_tagged_whole_with_one_tile(string id)
    {
        var data = _maps.Get(id).Data;
        var byId = data.Entities.ToDictionary(e => e.EntityId);
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int a, int b) { parent.TryAdd(a, a); parent.TryAdd(b, b); int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
        foreach (var e in data.Entities)
        {
            if (e.RegionAId is not int a || e.RegionBId is not int b || a == b) continue;
            foreach (int r in new[] { a, b })
                if (byId.TryGetValue(r, out var room) && room.Layer == "rooms") Union(e.EntityId, r);
        }
        var groups = parent.Keys.GroupBy(Find).Select(g => g.Select(i => byId[i]).ToList()).ToList();
        Assert.NotEmpty(groups);

        var split = groups.Where(g => g.Select(e => e.Tile).Distinct().Count() > 1).ToList();
        foreach (var g in split.Take(5))
            _o.WriteLine($"split: {g.First(e => e.Layer == "rooms").Name} over {string.Join(" ", g.Select(e => e.Tile).Distinct())}");

        // The parts named for each building: "<name> north wall", "<name> roof", "<name> front door".
        string Building(string room) => room.LastIndexOf(", ", StringComparison.Ordinal) is int c and > 0 ? room[..c] : room;
        var names = groups.Select(g => (Group: g, Names: g.Where(e => e.Layer == "rooms" && e.Name != null)
                                                          .Select(e => Building(e.Name!)).Distinct().ToList()))
                          .Where(x => x.Names.Count == 1).ToList();
        // Addresses only: "Shed off Service road" names every shed down that road, rooms or none.
        var unique = names.GroupBy(x => x.Names[0]).Where(n => n.Count() == 1 && char.IsDigit(n.Key[0]))
                          .ToDictionary(n => n.Key, n => n.Single().Group);
        var parts = data.Entities.Where(e => e.Name != null && e.Layer is "structure" or "interiors").ToList();
        int checkedParts = 0, strayParts = 0;
        foreach (var e in parts)
        {
            // The building's name is everything before the part's own words; try each cut.
            for (int at = e.Name!.LastIndexOf(' '); at > 0; at = e.Name.LastIndexOf(' ', at - 1))
            {
                if (!unique.TryGetValue(e.Name[..at], out var g)) continue;
                checkedParts++;
                if (e.Tile != g[0].Tile) { strayParts++; if (strayParts <= 5) _o.WriteLine($"stray: {e.Name} in {e.Tile}, its rooms in {g[0].Tile}"); }
                break;
            }
        }
        _o.WriteLine($"{id}: {groups.Count} houses by their rooms and doorways, {split.Count} over more than one tile; "
                   + $"{checkedParts} named parts checked, {strayParts} in another tile than their rooms");
        Assert.True(checkedParts > groups.Count, $"only {checkedParts} parts were found by name");
        Assert.Empty(split);
        Assert.Equal(0, strayParts);
    }

    /// <summary>Anybody can rebuild the map from the inputs in tools/places: the generator writes the
    /// shipped file byte for byte.</summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void The_generator_reproduces_the_shipped_map(string id)
    {
        string repo = RepoRoot();
        string outFile = Path.Combine(Path.GetTempPath(), $"{id}-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new ProcessStartInfo("python3", $"tools/gen_osm.py tools/places/{id} --out={outFile}")
            {
                WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            string err = proc.StandardError.ReadToEnd();
            _o.WriteLine(proc.StandardOutput.ReadToEnd());
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, err);
            var shipped = File.ReadAllBytes(Path.Combine(repo, "OpenFPS.Server", "maps", "places", id + ".json"));
            Assert.True(shipped.AsSpan().SequenceEqual(File.ReadAllBytes(outFile)), $"{id}.json differs from what tools/gen_osm.py makes");
        }
        finally { File.Delete(outFile); }
    }

    /// <summary>The map equivalent of the unknown-prefab-field rule (PrefabSpecTests): every key the
    /// generator writes is one the loader reads, so nothing it says is dropped in silence.</summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void It_uses_only_fields_the_loader_reads(string id)
    {
        var mapFields = typeof(MapData).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityFields = typeof(OpenFPS.Server.Repositories.EntityData).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roadFields = typeof(RoadData).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var junctionFields = typeof(JunctionData).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "places", id + ".json")));
        var unknown = new HashSet<string>();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (!mapFields.Contains(p.Name)) unknown.Add($"map field '{p.Name}'");
        void Check(string list, HashSet<string> known)
        {
            if (!doc.RootElement.TryGetProperty(list, out var items)) return;
            foreach (var item in items.EnumerateArray())
                foreach (var p in item.EnumerateObject())
                    if (!known.Contains(p.Name)) unknown.Add($"{list} field '{p.Name}'");
        }
        Check("Entities", entityFields);
        Check("Roads", roadFields);
        Check("Junctions", junctionFields);
        Assert.True(unknown.Count == 0, string.Join(", ", unknown));
    }

    /// <summary>"/join magnolia tx" finds magnolia_tx: a map answers to its id and to its listed name.</summary>
    [Theory]
    [InlineData("magnolia_tx", "magnolia tx")]
    [InlineData("magnolia_tx", "Magnolia_TX")]
    [InlineData("magnolia_tx", "magnolia-tx")]
    public void A_map_answers_to_its_name(string id, string said)
    {
        if (!Places().Any(o => (string)o[0] == id)) return;
        var p = _maps.Get(id);
        Assert.Equal(id, p.Manager.ResolveMapId(said));
        Assert.Equal(p.Data.Name, p.Manager.DisplayName(id));
        Assert.Null(p.Manager.ResolveMapId("no such place"));
    }

    private static readonly HashSet<string> DoorPrefabs = new()
        { "door", "steel_door", "glass_front_door", "glass_pull_door", "auto_sliding_door", "patio_door", "elevator_door" };

    private static string Name(Place p, int region)
        => region == AcousticConstants.GlobalRegionId ? "outdoors"
         : p.World.AcousticMap!.Regions.TryGetValue(region, out var r) ? $"'{r.FriendlyName}'" : $"region {region}";

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));
}
