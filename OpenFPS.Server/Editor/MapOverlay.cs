using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Serilog;

using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Server.Editor;

/// <summary>Where a map's spawn point was moved to.</summary>
public sealed class OverlaySpawn
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
}

/// <summary>
/// A thing from the map file that the editor changed: what it is now. <see cref="Prefab"/> and
/// <see cref="Was"/> (where the map file has it) find it again if a generator renumbered the file.
/// </summary>
public sealed class OverlayChange
{
    public int Id { get; set; }
    public string Prefab { get; set; } = "";
    public Vector3 Was { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;
    /// <summary>Its own settings (EntitySettings), by field path, as stored ("0.8", "Fountain").</summary>
    public Dictionary<string, string>? Settings { get; set; }
    /// <summary>How the map file has it turned and scaled, so an entry that has been undone back to the
    /// file can be dropped.</summary>
    public Quaternion? WasRotation { get; set; }
    public Vector3? WasScale { get; set; }
}

/// <summary>A thing from the map file that the editor removed.</summary>
public sealed class OverlayRemoval
{
    public int Id { get; set; }
    public string Prefab { get; set; } = "";
    public Vector3 Was { get; set; }
    /// <summary>What it was called when it was removed, for the list of things changed on the map. Null on
    /// entries kept before 2026-10-10: the prefab's name is said instead.</summary>
    public string? Name { get; set; }
}

/// <summary>A thing the editor placed: the whole of it, as a map file would have it, and its settings.</summary>
public sealed class OverlayAddition
{
    public EntityData Entity { get; set; } = new();
    public Dictionary<string, string>? Settings { get; set; }
    /// <summary>The group placing it was put down by ("yard@900000004": the group, and the number of its
    /// first part), shared by every part of that placing so they can be held and moved as one. Null for a
    /// thing placed on its own.</summary>
    public string? Placement { get; set; }
    /// <summary>Who placed it, and when (UTC). Null on entries kept before 2026-10-09: "placed earlier".</summary>
    public string? PlacedBy { get; set; }
    public DateTime? PlacedAt { get; set; }

    /// <summary>A parked vehicle ("vehicle:PRESET" as its prefab): made by the editor after the map's
    /// composites, never laid into the map's entities.</summary>
    [JsonIgnore] public bool IsVehicle => WorldEditor.IsVehicleId(Entity.PrefabId, out _);
}

/// <summary>
/// The world editor's edits to one map, as state: what each thing it changed is now, what it removed and
/// what it added. Laid over the map's own data when it loads (docs/WORLD_EDITOR.md section 7), so the map
/// file is never written by the editor and a generated one stays what its generator wrote.
/// </summary>
public sealed class MapOverlay
{
    /// <summary>The first id given to a thing the editor places: far above any generator's numbers.</summary>
    public const int FirstAddedId = 900_000_000;

    public string MapId { get; set; } = "";
    public int Format { get; set; } = 1;
    public int NextId { get; set; } = FirstAddedId;
    public OverlaySpawn? Spawn { get; set; }
    public List<OverlayChange> Changed { get; set; } = new();
    public List<OverlayRemoval> Removed { get; set; } = new();
    public List<OverlayAddition> Added { get; set; } = new();
    /// <summary>Models this map holds at a version other than the current one (phase 2).</summary>
    public Dictionary<string, int> Pins { get; set; } = new();
    /// <summary>The map's own settings (MapSettings): "Weather", "Hour", "Ground", "Beacon.door". Null when none.</summary>
    public Dictionary<string, string>? Settings { get; set; }
    /// <summary>Roads, paths and railways laid with the editor (OverlayRoute). Null when none.</summary>
    public List<OverlayRoute>? Routes { get; set; }

    [JsonIgnore] public bool IsEmpty => Spawn == null && Changed.Count == 0 && Removed.Count == 0 && Added.Count == 0 && Pins.Count == 0
                                        && (Settings == null || Settings.Count == 0) && (Routes == null || Routes.Count == 0);

    public OverlayChange? ChangeFor(int id) => Changed.FirstOrDefault(c => c.Id == id);
    public OverlayAddition? AdditionFor(int id) => Added.FirstOrDefault(a => a.Entity.EntityId == id);
}

/// <summary>
/// The overlays of every map: read from maps/overlays/&lt;id&gt;.json, laid over each map as it loads, and
/// written whenever the editor changes one. A null folder keeps them in memory only (a test rig).
///
/// Laying an overlay is idempotent: positions and settings are absolute, a removal that finds nothing is
/// skipped, and an addition whose id the map already has is skipped. So a map that has been /savemap'd
/// with its edits in it loads the same.
/// </summary>
public sealed class MapOverlayStore
{
    private readonly string? _directory;
    private readonly Dictionary<string, MapOverlay> _overlays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How close a thing must be to where the overlay says the map file had it, metres.</summary>
    public const float FindTolerance = 0.05f;

    public MapOverlayStore(string? directory)
    {
        _directory = directory == null ? null : Path.GetFullPath(directory);
    }

    public string? Directory => _directory;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(),
            new OpenFPS.Common.Networking.Vector3Converter(),
            new OpenFPS.Common.Networking.QuaternionConverter(),
        },
    };

    /// <summary>The file a map's overlay is in.</summary>
    public string? PathFor(string mapId) => _directory == null ? null : System.IO.Path.Combine(_directory, mapId + ".json");

    /// <summary>A map's overlay, made empty if it has none.</summary>
    public MapOverlay Get(string mapId)
    {
        if (_overlays.TryGetValue(mapId, out var o)) return o;
        o = Read(mapId) ?? new MapOverlay { MapId = mapId };
        _overlays[mapId] = o;
        return o;
    }

    private MapOverlay? Read(string mapId)
    {
        string? path = PathFor(mapId);
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var o = JsonSerializer.Deserialize<MapOverlay>(File.ReadAllText(path), MapRepository.JsonOptions);
            if (o == null) return null;
            o.MapId = mapId;
            return o;
        }
        catch (Exception ex)
        {
            // Kept aside, never overwritten: a file that will not read is somebody's work.
            Log.Error("MapOverlayStore: {Path} could not be read ({Error}); the map loads without its edits and the file is copied to .bad.", path, ex.Message);
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
            return new MapOverlay { MapId = mapId };
        }
    }

    /// <summary>Overlay files written, for the tests.</summary>
    public int Writes { get; private set; }

    private int _deferred;
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Holds every Save until the returned scope ends, then writes each changed map once.</summary>
    // A row of fifty, a group or a replacement saved the whole file once per thing, on the tick thread.
    public IDisposable Defer()
    {
        _deferred++;
        return new Scope(this);
    }

    private sealed class Scope(MapOverlayStore store) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--store._deferred > 0) return;
            var maps = store._dirty.ToList();
            store._dirty.Clear();
            foreach (var mapId in maps) store.Save(mapId);
        }
    }

    /// <summary>Writes a map's overlay now (a temporary file, then a rename), or at the end of a <see cref="Defer"/>.</summary>
    public void Save(string mapId)
    {
        if (_deferred > 0) { _dirty.Add(mapId); return; }
        string? path = PathFor(mapId);
        if (path == null || !_overlays.TryGetValue(mapId, out var o)) return;
        Writes++;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(o, WriteOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("MapOverlayStore: could not write {Path}: {Error}", path, ex.Message);
        }
    }

    /// <summary>The overlay as it would be written, for a test.</summary>
    public static string ToJson(MapOverlay o) => JsonSerializer.Serialize(o, WriteOptions);

    // ── Laying it over a map ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Before the map is built: every thing given an id (a hand-written map may leave them out; they are
    /// numbered after the highest, in file order, in memory only), then the spawn, the removals, the
    /// changes and the additions laid over the data.
    /// </summary>
    public void ApplyBefore(MapData map)
    {
        AssignMissingIds(map);
        Lay(map, Get(map.Id));
    }

    /// <summary>An overlay laid over a map's data: what loading does, and what baking writes into a file.</summary>
    internal static void Lay(MapData map, MapOverlay o)
    {
        if (o.IsEmpty) return;

        if (o.Spawn != null)
            map.SpawnPoint = new Transform { Position = o.Spawn.Position, Rotation = o.Spawn.Rotation };
        if (o.Settings != null)
            foreach (var (path, value) in o.Settings) MapSettings.Apply(map, path, value);

        var byId = new Dictionary<int, EntityData>();
        foreach (var e in map.Entities) byId.TryAdd(e.EntityId, e);

        int skipped = 0;
        foreach (var r in o.Removed)
        {
            var found = Find(map, byId, r.Id, r.Prefab, r.Was, null);
            if (found == null) { skipped++; Log.Warning("MapOverlayStore: '{Map}': the {Prefab} removed at {Was} (#{Id}) is not in the map file now; nothing removed.", map.Id, r.Prefab, r.Was, r.Id); continue; }
            r.Id = found.EntityId;
            map.Entities.Remove(found);
            byId.Remove(found.EntityId);
        }
        foreach (var c in o.Changed)
        {
            var found = Find(map, byId, c.Id, c.Prefab, c.Was, c.Position);
            if (found == null) { skipped++; Log.Warning("MapOverlayStore: '{Map}': the {Prefab} changed at {Was} (#{Id}) is not in the map file now; the change is not applied.", map.Id, c.Prefab, c.Was, c.Id); continue; }
            c.Id = found.EntityId;
            found.Position = c.Position;
            found.Rotation = c.Rotation;
            found.Scale = c.Scale;
        }
        foreach (var a in o.Added)
        {
            if (a.Entity.EntityId >= o.NextId) o.NextId = a.Entity.EntityId + 1;
            if (a.IsVehicle) continue;   // parked by WorldEditor.ParkKeptVehicles, after the composites
            if (byId.ContainsKey(a.Entity.EntityId)) continue;   // already in the file (/savemap'd)
            var copy = Clone(a.Entity);
            map.Entities.Add(copy);
            byId[copy.EntityId] = copy;
        }
        // Roads and railways as the map's own data, so traffic and trains find them as they always do.
        WorldEditor.LayRoutes(map, o);
        Log.Information("MapOverlayStore: '{Map}' has the editor's changes: {Changed} changed, {Removed} removed, {Added} added{Spawn}{Skipped}.",
            map.Id, o.Changed.Count, o.Removed.Count, o.Added.Count, o.Spawn != null ? ", spawn moved" : "",
            skipped > 0 ? $", {skipped} not found" : "");
    }

    /// <summary>After the map is built: each thing's own settings, onto its components.</summary>
    public void ApplyAfter(MapManager maps, string mapId)
    {
        var o = Get(mapId);
        if (o.IsEmpty) return;
        if (!maps.TryGetMap(mapId, out var world, out _, out _, out _)) return;
        var authored = maps.AuthoredEntities(mapId);
        void Apply(int id, Dictionary<string, string>? settings)
        {
            if (settings == null || settings.Count == 0) return;
            if (!authored.TryGetValue(id, out var e) || !world.IsAlive(e)) return;
            foreach (var (path, value) in settings) EntitySettings.TrySet(world, e, path, value, out _);
        }
        foreach (var c in o.Changed) Apply(c.Id, c.Settings);
        foreach (var a in o.Added) Apply(a.Entity.EntityId, a.Settings);
    }

    /// <summary>Gives every entity without an id the next free one, in file order.</summary>
    public static void AssignMissingIds(MapData map)
    {
        int next = 0;
        foreach (var e in map.Entities)
            if (e.EntityId > next && e.EntityId < MapOverlay.FirstAddedId) next = e.EntityId;
        foreach (var e in map.Entities)
            if (e.EntityId <= 0) e.EntityId = ++next;
    }

    /// <summary>
    /// The map-file thing an overlay entry means: the one with its id, if that is still the same prefab
    /// where the entry says the file had it (or already where the change put it); otherwise any thing of
    /// that prefab at that place; otherwise none.
    /// </summary>
    internal static EntityData? Find(MapData map, Dictionary<int, EntityData> byId, int id, string prefab, Vector3 was, Vector3? now)
    {
        bool Same(EntityData e, Vector3 at) => e.PrefabId.Equals(prefab, StringComparison.OrdinalIgnoreCase)
                                              && Vector3.Distance(e.Position, at) <= FindTolerance;
        if (byId.TryGetValue(id, out var byNumber) && (Same(byNumber, was) || (now.HasValue && Same(byNumber, now.Value))))
            return byNumber;
        return map.Entities.FirstOrDefault(e => Same(e, was));
    }

    public static EntityData Clone(EntityData e) => new()
    {
        EntityId = e.EntityId, PrefabId = e.PrefabId, Position = e.Position, Rotation = e.Rotation, Scale = e.Scale,
        RegionAId = e.RegionAId, RegionBId = e.RegionBId, IsIndoor = e.IsIndoor, Name = e.Name,
        ApertureSize = e.ApertureSize, KeyedSide = e.KeyedSide, PushSide = e.PushSide,
        RoomMaterials = e.RoomMaterials?.ToArray(), Materials = e.Materials?.ToArray(), Tile = e.Tile, Layer = e.Layer,
        Form = e.Form is { } f
            ? new OpenFPS.Common.Geometry.ShapeSpec { Kind = f.Kind, Steps = f.Steps, Landing = f.Landing, Thickness = f.Thickness, Segments = f.Segments }
            : null,
    };
}
