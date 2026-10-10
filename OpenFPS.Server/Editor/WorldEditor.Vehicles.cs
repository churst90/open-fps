using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;

using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Server.Editor;

/// <summary>
/// Vehicles in Place (docs/WORLD_EDITOR.md section 17): parked as /spawn vehicle parks them, kept in the
/// overlay as an addition whose prefab is "vehicle:PRESET", and parked again after the map's composites
/// when the server starts. The map file is never written.
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>What a vehicle is placed and kept under: "vehicle:" and the preset /spawn vehicle takes.</summary>
    public const string VehiclePrefix = VehicleShell.Prefix;
    public const string VehiclesCategory = "Vehicles";

    /// <summary>Whether a place id names a vehicle, and which preset. The prefix alone decides, so an entry
    /// for a preset that has gone is still kept off the map's entities.</summary>
    public static bool IsVehicleId(string? id, out string preset)
    {
        preset = "";
        if (id == null || !id.StartsWith(VehiclePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        preset = id[VehiclePrefix.Length..].ToLowerInvariant();
        return preset.Length > 0;
    }

    private static bool IsAircraft(string preset) => AircraftProfile.Presets.ContainsKey(preset);

    /// <summary>Whether /spawn vehicle would park this preset.</summary>
    internal static bool KnownVehicle(string preset)
        => !preset.Equals(CommandHandler.Jet, StringComparison.OrdinalIgnoreCase)
           && (IsAircraft(preset) || (MachineRegistry.Knows(preset) && VehicleShell.TryParse(VehiclePrefix + preset, out _)));

    /// <summary>Every preset /spawn vehicle takes: the aircraft that may be parked, then the road vehicles.</summary>
    internal static List<string> VehiclePresets()
        => AircraftProfile.Presets.Keys.Concat(MachineRegistry.Ids)
               .Where(KnownVehicle).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A vehicle's plain name, its size (width, height, length) and a line about it.</summary>
    internal static (string Name, Vector3 Size, string Help) VehicleOf(string preset)
    {
        if (IsAircraft(preset))
        {
            var a = AircraftProfile.ByName(preset);
            return (Capital(CompositeService.AircraftWord(preset)), new Vector3(a.WingspanMetres, 3f, a.LengthMetres),
                    $"A {a.Name}, parked on open ground. Nobody can fly one yet.");
        }
        var v = MachineRegistry.VehicleFor(preset);
        return (Capital(v.Name), new Vector3(v.WidthMetres, MathF.Max(1.2f, v.HeightMetres), v.LengthMetres),
                $"Parked with its engine off; anyone may get in and drive it. /spawn vehicle {preset} parks the same.");
    }

    /// <summary>/edit place vehicle:PRESET [at cursor]: parked beside you on clear ground, as /spawn vehicle
    /// parks one, or at the build cursor. One undo takes it away.</summary>
    private void PlaceVehicle(UserSession s, string preset, Action<IMessage> reply, bool atCursor)
    {
        if (!KnownVehicle(preset)) { Say(reply, $"There is no vehicle called {preset}. Place, Vehicles lists them."); return; }
        if (Composites == null) { Say(reply, "Vehicles cannot be parked on this server."); return; }
        if (Models.IsRetired(ModelLibrary.Kinds.Vehicle, preset)) { Say(reply, $"The vehicle {preset} is retired, so it is not offered for new things."); return; }
        if (Full(s, 1, out string full)) { Say(reply, full); return; }
        if (!TryBody(s, reply, out var world, out var feet, out float yaw)) return;
        var (name, size, _) = VehicleOf(preset);
        Vector3 at;
        Quaternion rotation;
        string where;
        if (atCursor)
        {
            if (!s.Build.Placed) { Say(reply, "There is no build cursor yet. /origin sets one where you stand, and /at moves it."); return; }
            at = s.Build.WorldCursor;
            rotation = s.Build.Facing(0f);
            where = $"at the build cursor, {s.Build.Describe()}";
        }
        else
        {
            if (!_maps.TryGetMap(s.CurrentMapId, out _, out _, out var grid, out _)) return;
            if (CommandHandler.ClearGroundBeside(world, grid, feet, yaw, size, openSky: IsAircraft(preset)) is not { } spot)
            { Say(reply, $"Not placed: there is no clear ground beside you big enough for {Article(name)}."); return; }
            at = spot;
            rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
            var toward = new Vector3(spot.X - feet.X, 0f, spot.Z - feet.Z);
            where = $"{Metres(toward.Length())} {DirectionWords.Relative(yaw, toward)}";
        }
        var o = Overlays.Get(s.CurrentMapId);
        int id = o.NextId++;
        var data = new EntityData { EntityId = id, PrefabId = VehiclePrefix + preset, Position = at, Rotation = rotation, Scale = Vector3.One };
        var thing = new Snapshot(id, data, null, Added: true, Change: null, Was: at);
        if (!Restore(s.CurrentMapId, thing, out string why)) { o.NextId--; Say(reply, $"Not placed: {why}"); return; }
        Push(s, new PlaceOp(s.CurrentMapId, thing, "parked", name));
        var hand = HandOf(s);
        hand.LastPlaced = VehiclePrefix + preset;
        hand.LastAtCursor = atCursor;
        string after = IsAircraft(preset) ? "Nobody can fly one yet." : "Anyone may drive it: get in with /enter.";
        Say(reply, $"Placed: {name}, parked {where}, facing {CompassOf(YawOf(rotation))}. {after} Undo takes it away.");
        Notify(s, $"{s.Username} parked {Article(name)}.");
        Refresh(s, reply);
    }

    /// <summary>A vehicle put on the map from a snapshot: parked, its settings, the overlay and the editor's
    /// index. Never laid into the map's entities, so /savemap does not write it into the map file.</summary>
    private bool RestoreVehicle(string mapId, Snapshot thing, out string why)
    {
        var d = thing.Data;
        IsVehicleId(d.PrefabId, out string preset);
        var root = Park(mapId, preset, d.Position, d.Rotation, out why);
        if (root == Entity.Null) return false;
        if (thing.Settings != null && _maps.TryGetMap(mapId, out var world, out _, out _, out _))
            foreach (var (path, value) in thing.Settings) EntitySettings.TrySet(world, root, path, value, out _);
        _maps.AuthoredEntities(mapId)[thing.Id] = root;
        var o = Overlays.Get(mapId);
        if (thing.Added) KeepAddition(o, thing);
        DataOf(mapId)[thing.Id] = MapOverlayStore.Clone(d);
        Overlays.Save(mapId);
        return true;
    }

    /// <summary>Parks a vehicle as /spawn vehicle does, belonging to the map (anyone may drive it), not
    /// recorded in the map's data.</summary>
    private Entity Park(string mapId, string preset, Vector3 at, Quaternion rotation, out string why)
    {
        why = "";
        if (Composites == null) { why = "vehicles cannot be parked on this server."; return Entity.Null; }
        if (!KnownVehicle(preset)) { why = $"there is no vehicle called {preset}."; return Entity.Null; }
        if (!_maps.TryGetMap(mapId, out _, out _, out _, out var lookup)) { why = "the map is not loaded."; return Entity.Null; }
        if (IsAircraft(preset))
        {
            var plane = Composites.ParkAircraft(mapId, preset, at, rotation, "", record: false);
            if (plane == Entity.Null) why = "it could not be made.";
            return plane;
        }
        int rootId = Composites.Place(mapId, VehiclePrefix + preset, at, rotation, "", out _, out string error, record: false);
        if (rootId < 0 || !lookup.TryGetValue(rootId, out var root)) { why = $"it could not be parked: {error}."; return Entity.Null; }
        _server.SyncAudioComponent(rootId);
        return root;
    }

    /// <summary>Takes a parked vehicle off the map: its parts and its root, from the world and every client.</summary>
    private void Unpark(string mapId, World world, Entity root)
    {
        if (!world.IsAlive(root)) return;
        int rootId = root.Id;
        foreach (var member in CompositeService.MembersOf(world, rootId))
        {
            int memberId = member.Id;
            _maps.DestroyEntity(mapId, member);
            _server.BroadcastRemoval(mapId, memberId);
        }
        _maps.DestroyEntity(mapId, root);
        _server.BroadcastRemoval(mapId, rootId);
    }

    /// <summary>Who is sitting in a vehicle, or null: a vehicle somebody is in is not taken away.</summary>
    private static string? Aboard(World world, Entity e)
    {
        if (!world.IsAlive(e) || !world.Has<CompositeComponent>(e)) return null;
        foreach (var o in CompositeService.OccupantsOf(world, e.Id))
            return world.Has<PlayerComponent>(o) ? world.Get<PlayerComponent>(o).Username : "somebody";
        return null;
    }

    /// <summary>
    /// Every vehicle the editor parked, on every loaded map, parked again where its overlay keeps it.
    /// Once at start, after the map's own composites (Program).
    /// </summary>
    public int ParkKeptVehicles()
    {
        int parked = 0, failed = 0;
        foreach (string mapId in _maps.LoadedMapIds.ToList())
        {
            var o = Overlays.Get(mapId);
            foreach (var a in o.Added.Where(a => a.IsVehicle).ToList())
            {
                IsVehicleId(a.Entity.PrefabId, out string preset);
                var root = Park(mapId, preset, a.Entity.Position, a.Entity.Rotation, out string why);
                if (root == Entity.Null)
                {
                    failed++;
                    Serilog.Log.Warning("WorldEditor: '{Map}': the {Vehicle} parked with the editor (#{Id}) could not be parked again: {Why}",
                                        mapId, a.Entity.PrefabId, a.Entity.EntityId, why);
                    continue;
                }
                if (a.Settings != null && _maps.TryGetMap(mapId, out var world, out _, out _, out _))
                    foreach (var (path, value) in a.Settings) EntitySettings.TrySet(world, root, path, value, out _);
                _maps.AuthoredEntities(mapId)[a.Entity.EntityId] = root;
                DataOf(mapId)[a.Entity.EntityId] = MapOverlayStore.Clone(a.Entity);
                parked++;
            }
        }
        if (parked > 0 || failed > 0) Serilog.Log.Information("WorldEditor: {Parked} vehicle(s) parked with the editor are back, {Failed} not.", parked, failed);
        return parked;
    }
}
