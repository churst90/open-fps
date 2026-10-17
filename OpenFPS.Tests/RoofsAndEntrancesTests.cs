using System.Numerics;
using Arch.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Geometry;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;
using OpenFPS.Acoustics;

namespace OpenFPS.Tests;

/// <summary>
/// "On a roof" and "an entrance" over the shipped maps, from geometry and what openings join, against what the names
/// said before (docs/SPECIAL_CASES_AUDIT.md S9): the same answer everywhere but where a name was misleading.
/// </summary>
public class RoofsAndEntrancesTests
{
    private readonly ITestOutputHelper _o;
    public RoofsAndEntrancesTests(ITestOutputHelper o) => _o = o;

    /// <summary>A map as a client sent all of it holds it: its triangles, and its rooms flooded through them.</summary>
    internal static WorldSnapshot ClientSnapshot(World ecs, MapData data)
    {
        var defs = EntityDefinitionFactory.StaticDefinitions(ecs);
        var statics = new List<SolidSpec>(); var movers = new List<SolidSpec>();
        foreach (var def in defs)
        {
            var role = EntityGeometry.RoleOf(def);
            if (role is GeometryRole.Static or GeometryRole.SightOnly) statics.Add(EntityGeometry.SpecOf(def, def.Transform, role));
            else if (role == GeometryRole.Mover) movers.Add(EntityGeometry.SpecOf(def, def.Transform, role));
        }
        var geometry = new TriangleWorldBuilder(250f).Build(statics, movers);
        var map = ClientWorldState.BuildAcousticMap(defs, data.Size, data.MinBound, data.VoxelResolution, data.OcclusionFloor,
                                                    streamed: false, report: false, geometry: geometry);
        var snap = new WorldSnapshot { AcousticMap = map, Geometry = geometry };
        foreach (var def in defs)
        {
            snap.Entities[def.EntityId] = new EntitySnapshot { Id = def.EntityId, Definition = def, Transform = def.Transform };
            if (def.Region.RoomSize.X > 0f) snap.RegionEntityIds.Add(def.EntityId);
        }
        return snap;
    }

    internal static (World Ecs, MapData Data) City()
    {
        AcousticRegistry.Initialize();
        string dir = Path.Combine(Path.GetTempPath(), "openfps-roofs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "maps"));
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", "city.json"), Path.Combine(dir, "maps", "city.json"));
            var m = new MapManager(new MapRepository(Path.Combine(dir, "maps")), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            m.Initialize();
            Assert.True(m.TryGetMap("city", out var ecs, out _, out _, out _));
            Assert.True(m.TryGetMapData("city", out var data));
            return (ecs, data);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>What a name said before: the thing under you called "...roof".</summary>
    private static bool RoofByName(in EntitySnapshot under) => (under.Definition.Identity.Name ?? "").Contains("roof", StringComparison.OrdinalIgnoreCase);

    /// <summary>The top of every fixed solid box of the map wide enough to stand on, where nothing stands on it, each
    /// with the thing under those feet.</summary>
    internal static IEnumerable<(EntitySnapshot Under, Vector3 Feet)> Tops(WorldSnapshot snap, SpatialService spatial)
    {
        foreach (var e in snap.Entities.Values)
        {
            var def = e.Definition;
            if (def.Type != EntityType.StaticObject || !def.Collider.IsSolid || def.Collider.Shape != ColliderShape.Box) continue;
            if (def.Region.RoomSize.X > 0f || def.Portal.RegionAId != 0 || def.Portal.RegionBId != 0) continue;
            var size = def.Collider.Size;
            if (size.X < 0.8f || size.Z < 0.8f) continue;
            var up = Vector3.Transform(Vector3.UnitY, e.Transform.Rotation);
            if (up.Y < 0.99f) continue;
            var feet = e.Transform.Position + new Vector3(0f, size.Y * 0.5f + 0.01f, 0f);
            if (!spatial.CastSight(snap, feet + new Vector3(0f, 0.3f, 0f), -Vector3.UnitY, 1.5f, out var under, out _, glassBlocks: true)) continue;
            if (under.Id != e.Id) continue;
            yield return (under, feet);
        }
    }

    /// <summary>Each top of a map stood on, "on a roof" before (by its name) against now: how many agree, and each that
    /// does not, by name, prefab and where.</summary>
    internal static (int Same, List<string> Lost, List<string> Gained) RoofChanges(WorldSnapshot snap)
    {
        var spatial = new SpatialService();
        int same = 0;
        var lost = new List<string>(); var gained = new List<string>();
        foreach (var (under, feet) in Tops(snap, spatial))
        {
            bool before = RoofByName(under), now = ScopeView.StandsOnRoof(snap, spatial, feet);
            if (before == now) { same++; continue; }
            string line = $"{under.Definition.Identity.Name} ({under.Definition.Identity.PrefabId}) at {StandingSpots.Typed(feet)}";
            (before ? lost : gained).Add(line);
        }
        lost.Sort(StringComparer.Ordinal); gained.Sort(StringComparer.Ordinal);
        return (same, lost, gained);
    }

    internal static void Say(ITestOutputHelper o, string map, (int Same, List<string> Lost, List<string> Gained) c)
    {
        o.WriteLine($"{map}: {c.Same} tops the same, {c.Lost.Count} no longer a roof, {c.Gained.Count} a roof now");
        foreach (var l in c.Lost) o.WriteLine("  no longer a roof: " + l);
        foreach (var g in c.Gained) o.WriteLine("  a roof now: " + g);
    }

    /// <summary>
    /// On the city a roof is a slab under the sky with a room's air under it. Every slab the generator calls a
    /// building's roof still is one. What changed: the two bus shelters' canopies (metal_roof), which have no room
    /// under them; a shelter is street furniture, not a room.
    /// </summary>
    [Fact]
    public void On_the_city_a_roof_is_what_a_room_is_under()
    {
        var (ecs, data) = City();
        var c = RoofChanges(ClientSnapshot(ecs, data));
        Say(_o, "city", c);
        Assert.True(c.Same > 1000, $"{c.Same} tops");
        Assert.All(c.Lost, l => Assert.StartsWith("Bus shelter roof (metal_roof)", l));
        Assert.Equal(2, c.Lost.Count);
        Assert.Empty(c.Gained);
    }

    /// <summary>What a name said before (MapTracker.IsEntrance until 2026-10-11): an exit beacon, anything named an
    /// entrance, or a door with the open air on one side and a room on the other.</summary>
    private static bool EntranceByName(WorldSnapshot world, in EntitySnapshot e)
    {
        var def = e.Definition;
        string beacon = def.Identity.BeaconCategory ?? "";
        if (string.Equals(beacon, Beacons.Exit, StringComparison.OrdinalIgnoreCase)) return true;
        if ((def.Identity.Name ?? "").Contains("entrance", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(beacon, Beacons.Door, StringComparison.OrdinalIgnoreCase)) return false;
        int a = def.Portal.RegionAId, b = def.Portal.RegionBId;
        if (a == b) return false;
        bool Out(int id) => id == AcousticConstants.GlobalRegionId || (world.Entities.TryGetValue(id, out var r) && !r.Definition.Region.IsIndoor);
        return Out(a) != Out(b);
    }

    /// <summary>Every entity of a map an entrance before (by name) against now (by what it joins), each that differs
    /// by name, prefab and where.</summary>
    internal static (int Same, List<string> Lost, List<string> Gained) EntranceChanges(WorldSnapshot snap)
    {
        int same = 0;
        var lost = new List<string>(); var gained = new List<string>();
        foreach (var e in snap.Entities.Values)
        {
            bool before = EntranceByName(snap, e), now = MapTracker.IsEntrance(snap, e);
            if (before == now) { if (now) same++; continue; }
            string line = $"{MapTracker.EntranceName(snap, e)} ({e.Definition.Identity.PrefabId}, #{e.Id}) at {StandingSpots.Typed(e.Transform.Position)}";
            (before ? lost : gained).Add(line);
        }
        lost.Sort(StringComparer.Ordinal); gained.Sort(StringComparer.Ordinal);
        return (same, lost, gained);
    }

    internal static void SayEntrances(ITestOutputHelper o, string map, (int Same, List<string> Lost, List<string> Gained) c)
    {
        o.WriteLine($"{map}: {c.Same} entrances the same, {c.Lost.Count} no longer, {c.Gained.Count} new");
        foreach (var l in c.Lost) o.WriteLine("  no longer an entrance: " + l);
        foreach (var g in c.Gained) o.WriteLine("  an entrance now: " + g);
    }

    /// <summary>
    /// On the city every door the names called an entrance still is one, by what it joins. What changed: the
    /// hangar's front, the wall over the opening called "Hangar front entrance" before and the opening under it now;
    /// the garage's open east side onto Main Street at street level and the tunnel's two mouths, which a person walks
    /// through, are entrances now; the garage's sides over their parapets and its upper decks' east sides are not.
    /// </summary>
    [Fact]
    public void On_the_city_an_entrance_is_what_an_opening_joins()
    {
        var (ecs, data) = City();
        var c = EntranceChanges(ClientSnapshot(ecs, data));
        SayEntrances(_o, "city", c);
        Assert.True(c.Same > 50, $"{c.Same} entrances");
        Assert.Equal(new[] { "Hangar front entrance" }, c.Lost.Select(l => l[..l.IndexOf(" (", StringComparison.Ordinal)]));
        Assert.Contains(c.Gained, g => g.StartsWith("Hangar entrance (portal", StringComparison.Ordinal));
        Assert.DoesNotContain(c.Gained, g => g.Contains("level 1", StringComparison.Ordinal) || g.Contains("level 2", StringComparison.Ordinal));
    }
}
