using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Composites: group what stands near you into one thing, ungroup it, save it as a template, place a
/// template. Members carry a <see cref="ParentComponent"/> to the root and ParentSystem moves them, so
/// a house and a car you built are the same structure. See docs/SERVER_NOTES.md, "Composites".
/// </summary>
public class CompositeService
{
    private readonly MapManager _maps;
    private readonly PrefabRepository _prefabs;
    private readonly CompositeRepository _composites;

    public CompositeService(MapManager maps, PrefabRepository prefabs, CompositeRepository composites)
    {
        _maps = maps;
        _prefabs = prefabs;
        _composites = composites;
    }

    public CompositeRepository Templates => _composites;
    public PrefabRepository Prefabs => _prefabs;

    /// <summary>Whether a grouping sweep may take a thing: not players, vehicles, or anything already in a composite.</summary>
    public static bool CanBeGrouped(World world, Entity e)
        => world.IsAlive(e)
        && world.Has<Transform>(e)
        && !world.Has<PlayerComponent>(e)
        && !world.Has<VehicleComponent>(e)
        && !world.Has<CompositeComponent>(e)
        && !world.Has<ParentComponent>(e);

    /// <summary>
    /// Makes one thing out of everything standing near a point. Its origin is the middle of what was
    /// taken at its lowest point, so a house placed again at your feet has its floor at your feet.
    /// </summary>
    public int Group(string mapId, Vector3 near, float radius, string name, bool anchored,
                     string owner, out int partCount)
    {
        partCount = 0;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return -1;

        var members = new List<Entity>();
        var query = new QueryDescription().WithAll<Transform>();
        float r2 = radius * radius;
        world.Query(in query, (Entity e, ref Transform t) =>
        {
            if (Vector3.DistanceSquared(t.Position, near) > r2) return;
            if (!CanBeGrouped(world, e)) return;
            if (IsBiggerThanTheSweep(world, e, radius)) return;
            members.Add(e);
        });
        if (members.Count == 0) return -1;

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var m in members)
        {
            var p = world.Get<Transform>(m).Position;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var origin = new Vector3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);

        var root = _maps.SpawnEntity(mapId, w => w.Create(
            new Transform { Position = origin, Rotation = Quaternion.Identity, IsDirty = true },
            new CompositeComponent { Name = name, Anchored = anchored, TemplateId = "", Owner = owner },
            new NameComponent { Name = name },
            new IdentityComponent { Name = name, Announce = true },
            EntityType.StaticObject));
        if (root == Entity.Null) return -1;

        Attach(world, root, members, origin);
        RefreshRoom(mapId, world, root);
        if (!anchored) MakeDynamic(mapId, world, root, MembersOf(world, root.Id));
        partCount = members.Count;
        Log.Information("Composite '{Name}' grouped {Count} entit(ies) on '{Map}' at {Origin}.", name, partCount, mapId, origin);
        return root.Id;
    }

    /// <summary>
    /// Moves a composite and its members into the grid's dynamic half by giving them a
    /// <see cref="Velocity"/>. Left static, a moving body leaves a ghost where it was built. The
    /// rebuild at the end is the only way the static half forgets an entry.
    /// </summary>
    private void MakeDynamic(string mapId, World world, Entity root, List<Entity> members)
    {
        bool anyWasStatic = false;
        if (!world.Has<Velocity>(root)) { world.Add(root, new Velocity()); anyWasStatic = true; }
        foreach (var m in members)
            if (!world.Has<Velocity>(m)) { world.Add(m, new Velocity()); anyWasStatic = true; }
        if (anyWasStatic) _maps.RefreshGrid(mapId);
    }

    /// <summary>The reverse: parts that stop belonging to something that moves go back to being scenery.</summary>
    private void MakeStatic(string mapId, World world, IEnumerable<Entity> entities)
    {
        bool any = false;
        foreach (var e in entities)
            if (world.IsAlive(e) && world.Has<Velocity>(e)) { world.Remove<Velocity>(e); any = true; }
        if (any) _maps.RefreshGrid(mapId);
    }

    /// <summary>
    /// Whether somebody may take this composite apart, save it, change it or make it drive: its owner,
    /// anybody when it has none, or an elevated role. Going into it or sitting in it is never gated.
    /// </summary>
    public static bool MayModify(World world, Entity root, string requester, bool elevated)
    {
        if (elevated) return true;
        if (!world.Has<CompositeComponent>(root)) return false;
        string owner = world.Get<CompositeComponent>(root).Owner ?? "";
        return string.IsNullOrWhiteSpace(owner)
            || string.Equals(owner, requester, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a thing is wider than the sweep itself, and so not what somebody standing there meant:
    /// the floor under them, the field round them. Widen the radius and it comes into scope.
    /// </summary>
    public static bool IsBiggerThanTheSweep(World world, Entity e, float radius)
    {
        if (!world.Has<ColliderComponent>(e)) return false;
        var size = world.Get<ColliderComponent>(e).Size;
        return MathF.Max(size.X, size.Z) > radius * 2f;
    }

    private static void Attach(World world, Entity root, List<Entity> members, Vector3 origin)
    {
        var inverse = Quaternion.Inverse(world.Get<Transform>(root).Rotation);
        foreach (var m in members)
        {
            var t = world.Get<Transform>(m);
            world.Add(m, new ParentComponent
            {
                ParentEntityId = root.Id,
                LocalPosition = Vector3.Transform(t.Position - origin, inverse),
                LocalRotation = inverse * t.Rotation,
            });
        }
        ShutAndForget(world, members);
    }

    /// <summary>
    /// Shuts every door in a set and makes it forget where "shut" was. A door keeps its shut pose in
    /// the frame it lives in, and grouping changes that frame: a door that kept a world pose and then
    /// swung as a part flung its leaf out of the world.
    /// </summary>
    private static void ShutAndForget(World world, IEnumerable<Entity> entities)
    {
        foreach (var e in entities)
        {
            if (!world.IsAlive(e) || !world.Has<DoorComponent>(e)) continue;
            ref var door = ref world.Get<DoorComponent>(e);
            door.Openness = 0f;
            door.Target = 0f;
            door.Travel = 0;
            door.SelfClosing = false;
            door.ClearSeconds = 0f;
            door.KeySeconds = 0f;
            door.KeyTurned = false;
            door.HandId = 0;
            door.OpenedFrom = 0;
            door.Captured = false;
            if (world.Has<PortalComponent>(e)) world.Get<PortalComponent>(e).ApertureSize = 0f;
        }
    }

    /// <summary>Undoes a grouping without moving anything: the parts keep the world transforms they have.</summary>
    public bool Ungroup(string mapId, int rootId, string requester, bool elevated,
                        out string name, out int partCount, out string error)
    {
        name = ""; partCount = 0; error = "";
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) { error = "map not loaded"; return false; }
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<CompositeComponent>(root))
        { error = "that is not a composite"; return false; }

        var composite = world.Get<CompositeComponent>(root);
        if (!MayModify(world, root, requester, elevated))
        { error = $"'{composite.Name}' belongs to {composite.Owner}"; return false; }

        name = composite.Name;
        var members = new List<Entity>();
        foreach (var member in MembersOf(world, rootId))
        {
            // Left behind, the derived room would be a field that still sounds like a room.
            if (world.Has<DerivedRoomComponent>(member)) { _maps.DestroyEntity(mapId, member); continue; }
            world.Remove<ParentComponent>(member);
            members.Add(member);
            partCount++;
            if (world.Has<DoorComponent>(member)) ShutAndForget(world, new[] { member });
        }
        foreach (var occupant in OccupantsOf(world, rootId)) Disembark(world, occupant);

        if (!composite.Anchored) MakeStatic(mapId, world, members);
        lookup.Remove(rootId);
        world.Destroy(root);
        // The root may be in the grid's static half; only a rebuild forgets it.
        if (composite.Anchored) _maps.RefreshGrid(mapId);
        Log.Information("Composite '{Name}' ungrouped into {Count} loose entit(ies).", name, partCount);
        return true;
    }

    /// <summary>Every entity parented to this root, the derived room included.</summary>
    public static List<Entity> MembersOf(World world, int rootId)
    {
        var found = new List<Entity>();
        var q = new QueryDescription().WithAll<ParentComponent>();
        world.Query(in q, (Entity e, ref ParentComponent p) => { if (p.ParentEntityId == rootId) found.Add(e); });
        return found;
    }

    /// <summary>The parts a composite is built from: its members less the room it derived, which nobody
    /// built, cannot be saved, and would be measuring the answer.</summary>
    public static List<Entity> PartsOf(World world, int rootId)
    {
        var parts = MembersOf(world, rootId);
        parts.RemoveAll(e => world.Has<DerivedRoomComponent>(e));
        return parts;
    }

    /// <summary>Everyone sitting in this root.</summary>
    public static List<Entity> OccupantsOf(World world, int rootId)
    {
        var found = new List<Entity>();
        var q = new QueryDescription().WithAll<OccupantComponent>();
        world.Query(in q, (Entity e, ref OccupantComponent o) => { if (o.RootEntityId == rootId) found.Add(e); });
        return found;
    }

    /// <summary>Takes somebody out of whatever they were in, without moving them: where they go is the caller's to say.</summary>
    public static void Disembark(World world, Entity occupant)
    {
        if (!world.IsAlive(occupant) || !world.Has<OccupantComponent>(occupant)) return;
        world.Remove<OccupantComponent>(occupant);
        if (world.Has<PlayerComponent>(occupant))
        {
            ref var p = ref world.Get<PlayerComponent>(occupant);
            p.IsInVehicle = false;
        }
    }

    /// <summary>The composite root nearest a point, or -1: how a player says "this house".</summary>
    public int NearestRoot(string mapId, Vector3 near, float radius)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return -1;
        int best = -1; float bestD2 = radius * radius;
        var q = new QueryDescription().WithAll<Transform, CompositeComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref CompositeComponent _) =>
        {
            float d2 = Vector3.DistanceSquared(t.Position, near);
            if (d2 <= bestD2) { bestD2 = d2; best = e.Id; }
        });
        return best;
    }

    /// <summary>Writes a live composite to disk as a template anyone can place, its parts, seats and
    /// vehicle in its own frame.</summary>
    public bool SaveAsTemplate(string mapId, int rootId, string templateId, string requester, bool elevated,
                               out int partCount, out string error)
    {
        partCount = 0; error = "";
        if (!SafeText.IsFileName(templateId)) { error = "a design's name is letters, digits, _ and -, up to 64"; return false; }
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) { error = "map not loaded"; return false; }
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<CompositeComponent>(root))
        { error = "that is not a composite"; return false; }

        var composite = world.Get<CompositeComponent>(root);
        if (!MayModify(world, root, requester, elevated))
        { error = $"'{composite.Name}' belongs to {composite.Owner}"; return false; }

        var rootT = world.Get<Transform>(root);
        var inverse = Quaternion.Inverse(rootT.Rotation);

        var template = new CompositeTemplate
        {
            Id = templateId,
            Name = string.IsNullOrWhiteSpace(composite.Name) ? templateId : composite.Name,
            Anchored = composite.Anchored,
        };

        if (world.Has<OccupancyComponent>(root))
            foreach (var seat in world.Get<OccupancyComponent>(root).Seats)
                template.Seats.Add(new SeatDefinition
                {
                    Name = seat.Name,
                    Position = seat.LocalPosition,
                    YawDegrees = seat.LocalYaw * (180f / MathF.PI),
                    Controls = seat.Controls,
                });
        if (world.Has<DriveComponent>(root))
            template.VehiclePreset = world.Get<DriveComponent>(root).Preset;

        foreach (var member in PartsOf(world, rootId))
        {
            if (!world.Has<Transform>(member)) continue;
            var t = world.Get<Transform>(member);
            string prefabId = world.Has<IdentityComponent>(member) ? world.Get<IdentityComponent>(member).PrefabId : "";
            if (string.IsNullOrWhiteSpace(prefabId))
            {
                // Refused rather than dropped: a house missing a wall with nothing to say why.
                error = $"a part has no prefab id and cannot be saved; ungroup and rebuild it from prefabs";
                return false;
            }
            template.Parts.Add(new CompositePart
            {
                PrefabId = prefabId,
                Position = Vector3.Transform(t.Position - rootT.Position, inverse),
                Rotation = inverse * t.Rotation,
            });
            partCount++;
        }

        if (partCount == 0) { error = "it has no parts"; return false; }

        _composites.Save(template);
        ref var live = ref world.Get<CompositeComponent>(root);
        live.TemplateId = templateId;
        return true;
    }

    /// <summary>
    /// A composite by name: one saved to disk, or a vehicle built from its profile
    /// ("vehicle:i4_economy" — see <see cref="VehicleShell"/>). A saved file of the same name wins,
    /// so a map can still carry a hand-built car under that id if somebody wants one.
    /// </summary>
    public bool TryGetTemplate(string id, out CompositeTemplate template)
    {
        if (_composites.TryGet(id, out template)) return true;
        if (VehicleShell.TryParse(id, out string preset))
        {
            template = VehicleShell.Build(preset, prefab =>
                _prefabs.Prefabs.TryGetValue(prefab.ToLowerInvariant(), out var t) && t.ColliderSize.HasValue
                    ? t.ColliderSize.Value : Vector3.One);
            return true;
        }
        template = null!;
        return false;
    }

    /// <summary>
    /// A vehicle shell for the map's own traffic to drive (a bus you can get on): passenger seats only and
    /// no DriveComponent, or DrivingSystem would drive it too. Not recorded on the map: the route spawns it.
    /// </summary>
    public Entity InstantiateForTraffic(string mapId, string preset, Vector3 position, Quaternion rotation)
    {
        if (!TryGetTemplate(VehicleShell.Prefix + preset, out var shell)) return Entity.Null;
        var passengers = new CompositeTemplate
        {
            Id = shell.Id, Name = shell.Name, Description = shell.Description, Anchored = false,
            Parts = shell.Parts, Seats = shell.Seats.FindAll(s => !s.Controls), VehiclePreset = "",
        };
        int rootId = Instantiate(mapId, passengers, position, rotation, "", out _);
        if (rootId < 0 || !_maps.TryGetMap(mapId, out _, out _, out _, out var lookup)) return Entity.Null;
        return lookup.TryGetValue(rootId, out var root) ? root : Entity.Null;
    }

    /// <summary>
    /// Puts a saved composite into the world and records the placement in the map's data, which
    /// <see cref="MapManager.SaveMap"/> commits to disk.
    /// </summary>
    /// <param name="record">False for something that lasts until a restart: a vehicle spawned on a
    /// shipped map (<see cref="MapManager.IsShipped"/>).</param>
    public int Place(string mapId, string templateId, Vector3 position, Quaternion rotation,
                     string owner, out int partCount, out string error, bool record = true)
    {
        partCount = 0; error = "";
        if (!TryGetTemplate(templateId, out var template)) { error = $"no composite called '{templateId}'"; return -1; }
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) { error = "map not loaded"; return -1; }

        int rootId = Instantiate(mapId, template, position, rotation, owner, out partCount);
        if (rootId < 0) { error = "nothing could be placed"; return -1; }

        if (record && _maps.TryGetMapData(mapId, out var data))
        {
            data.Composites ??= new List<CompositePlacement>();
            data.Composites.Add(new CompositePlacement
            {
                TemplateId = templateId, Position = position, Rotation = rotation, Owner = owner,
            });
        }
        return rootId;
    }

    /// <summary>
    /// Rebuilds every composite each loaded map records. Once at startup, after the maps load and before
    /// anything measures them: the acoustics, grid and broadcast radius must include the buildings.
    /// </summary>
    public void PlaceRecorded(MapManager maps)
    {
        foreach (string mapId in maps.LoadedMapIds)
        {
            if (!maps.TryGetMapData(mapId, out var data) || data.Composites == null) continue;
            int placed = 0, failed = 0;
            foreach (var p in data.Composites)
            {
                if (p.TemplateId.StartsWith(AircraftPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (ParkAircraft(mapId, p.TemplateId[AircraftPrefix.Length..], p.Position, p.Rotation, p.Owner, record: false) == Entity.Null) failed++;
                    else placed++;
                    continue;
                }
                if (!TryGetTemplate(p.TemplateId, out var template))
                {
                    Log.Error("Map '{Map}' places composite '{Id}', which does not exist. That building "
                            + "will be missing from the world.", mapId, p.TemplateId);
                    failed++;
                    continue;
                }
                var t = template;
                if (p.Anchored.HasValue && p.Anchored.Value != template.Anchored)
                    t = new CompositeTemplate { Id = template.Id, Name = template.Name, Description = template.Description,
                                                Anchored = p.Anchored.Value, Parts = template.Parts };
                if (Instantiate(mapId, t, p.Position, p.Rotation, p.Owner, out _) >= 0) placed++; else failed++;
            }
            if (placed > 0 || failed > 0)
                Log.Information("Map '{Map}': {Placed} composite(s) placed, {Failed} failed.", mapId, placed, failed);
        }
    }

    /// <summary>What a parked aircraft is recorded under on a map: "aircraft:" and its preset.</summary>
    public const string AircraftPrefix = "aircraft:";

    /// <summary>
    /// An aircraft parked on the ground: a body with a name, since nobody can fly one yet. Recorded on the
    /// map when <paramref name="record"/>, so /savemap keeps it as it keeps a parked car.
    /// </summary>
    public Entity ParkAircraft(string mapId, string preset, Vector3 position, Quaternion rotation, string owner, bool record)
    {
        if (!AircraftProfile.Presets.ContainsKey(preset)) return Entity.Null;
        var air = AircraftProfile.ByName(preset);
        string word = AircraftWord(preset);
        string name = char.ToUpperInvariant(word[0]) + word[1..];
        var e = _maps.SpawnEntity(mapId, w => w.Create(
            new Transform { Position = position, Rotation = rotation, IsDirty = true },
            // The fuselage is the solid part; a wing you could walk under is not a wall.
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(MathF.Min(2.5f, air.WingspanMetres), 2.8f, air.LengthMetres), IsSolid = true },
            new CompositeComponent { Name = name, Anchored = true, TemplateId = "", Owner = owner },
            new NameComponent { Name = name },
            new IdentityComponent
            {
                Name = name, Description = $"A {air.Name}, parked. Nobody can fly one yet.", Announce = true,
                BeaconCategory = OpenFPS.Common.Beacons.Vehicle,
            },
            new VehicleComponent { VehicleType = preset, MaxSeats = 0 },
            EntityType.StaticObject));
        if (e != Entity.Null && record && _maps.TryGetMapData(mapId, out var data))
            (data.Composites ??= new List<CompositePlacement>()).Add(new CompositePlacement
            {
                TemplateId = AircraftPrefix + preset, Position = position, Rotation = rotation, Owner = owner,
            });
        return e;
    }

    /// <summary>What an aircraft preset is called aloud: "light aeroplane", "helicopter".</summary>
    public static string AircraftWord(string preset) => preset switch
    {
        "helicopter" => "helicopter",
        "piston_single" => "light aeroplane",
        "turboprop" => "turboprop aeroplane",
        _ => preset.Replace('_', ' '),
    };

    /// <summary>Builds an instance without recording a placement (map load replays recorded ones).</summary>
    public int Instantiate(string mapId, CompositeTemplate template, Vector3 position, Quaternion rotation,
                           string owner, out int partCount)
    {
        partCount = 0;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return -1;

        var root = _maps.SpawnEntity(mapId, w => w.Create(
            new Transform { Position = position, Rotation = rotation, IsDirty = true },
            new CompositeComponent { Name = template.Name, Anchored = template.Anchored, TemplateId = template.Id, Owner = owner ?? "" },
            new NameComponent { Name = template.Name },
            new IdentityComponent
            {
                Name = template.Name, Description = template.Description, Announce = true,
                BeaconCategory = string.IsNullOrWhiteSpace(template.VehiclePreset) ? "" : OpenFPS.Common.Beacons.Vehicle,
            },
            EntityType.StaticObject));
        if (root == Entity.Null) return -1;

        if (template.Seats.Count > 0)
        {
            var seats = new OccupancyComponent();
            foreach (var d in template.Seats)
                seats.Seats.Add(new Seat
                {
                    Name = d.Name,
                    LocalPosition = d.Position,
                    LocalYaw = d.YawDegrees * (MathF.PI / 180f),
                    Controls = d.Controls,
                });
            world.Add(root, seats);
        }

        foreach (var part in template.Parts)
        {
            Vector3 world_ = position + Vector3.Transform(part.Position, rotation);
            Quaternion rot = rotation * part.Rotation;
            Entity e;
            try { e = _maps.SpawnEntity(mapId, w => _prefabs.Spawn(w, part.PrefabId, world_, rot, part.Scale)); }
            catch (Exception ex)
            {
                Log.Error("Composite '{Id}': part '{Prefab}' failed to spawn: {Error}", template.Id, part.PrefabId, ex.Message);
                continue;
            }
            if (e == Entity.Null) continue;
            world.Add(e, new ParentComponent
            {
                ParentEntityId = root.Id, LocalPosition = part.Position, LocalRotation = part.Rotation,
            });
            partCount++;
        }

        RefreshRoom(mapId, world, root);
        if (!string.IsNullOrWhiteSpace(template.VehiclePreset))
            MakeDrivable(mapId, world, root, template.VehiclePreset, out _);
        else if (!template.Anchored)
            MakeDynamic(mapId, world, root, MembersOf(world, root.Id));

        Log.Information("Placed composite '{Id}' on '{Map}' at {Pos} — {Parts} part(s){Extra}.",
                        template.Id, mapId, position, partCount,
                        string.IsNullOrWhiteSpace(template.VehiclePreset) ? "" : $", driving as a {template.VehiclePreset}");
        return root.Id;
    }

    // ── The inside of it ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gives a composite the room it encloses, or takes away the one it no longer does. Call it whenever
    /// the shape changes; the room is made again from what is there. It is a part, so it travels with
    /// the building.
    /// </summary>
    public bool RefreshRoom(string mapId, int rootId)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return false;
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root)) return false;
        return RefreshRoom(mapId, world, root);
    }

    private bool RefreshRoom(string mapId, World world, Entity root)
    {
        foreach (var member in MembersOf(world, root.Id))
            if (world.Has<DerivedRoomComponent>(member))
                _maps.DestroyEntity(mapId, member);

        var parts = PartsOf(world, root.Id);
        string name = world.Has<CompositeComponent>(root) ? world.Get<CompositeComponent>(root).Name : "";
        string templateId = world.Has<CompositeComponent>(root) ? world.Get<CompositeComponent>(root).TemplateId ?? "" : "";
        RegionComponent room;
        Vector3 centre;
        if (VehicleShell.TryParse(templateId, out string preset) && VehicleShell.TryCabin(preset, out centre, out var cabin))
        {
            // A vehicle built from its profile: the room is its cabin, which the shell knows.
            if (!CompositeAcoustics.DeriveInBox(world, parts, name, centre, cabin, out room)) return false;
        }
        else if (!CompositeAcoustics.Derive(world, parts, name, out room, out centre)) return false;

        var rootT = world.Get<Transform>(root);
        var e = _maps.SpawnEntity(mapId, w => w.Create(
            new Transform
            {
                Position = rootT.Position + Vector3.Transform(centre, rootT.Rotation),
                Rotation = rootT.Rotation,
                IsDirty = true,
            },
            new ParentComponent { ParentEntityId = root.Id, LocalPosition = centre, LocalRotation = Quaternion.Identity },
            room,
            // Not solid, but it needs a collider: the broadcast walks the grid, which only carries things with one.
            new ColliderComponent { Shape = ColliderShape.Box, Size = room.RoomSize, IsSolid = false },
            new DerivedRoomComponent(),
            new NameComponent { Name = room.FriendlyName },
            EntityType.Trigger));
        if (e == Entity.Null) return false;

        // Every door in the building now joins this room to the outside.
        int doors = 0;
        foreach (var part in parts)
        {
            if (!world.Has<PortalComponent>(part) || !world.Has<DoorComponent>(part)) continue;
            ref var portal = ref world.Get<PortalComponent>(part);
            portal.RegionAId = e.Id;
            portal.RegionBId = AcousticConstants.GlobalRegionId;
            doors++;
        }

        Log.Information("Composite {Root} ('{Name}') encloses a room {Size} with {Doors} door(s) — floor {Floor}, walls {Walls}.",
                        root.Id, name, room.RoomSize, doors, room.Materials[0], room.Materials[2]);
        return true;
    }

    // ── Seats, and driving ──────────────────────────────────────────────────────────────────────

    /// <summary>Puts a seat where somebody is standing, facing their way, in the composite's own frame so it
    /// survives saving, placing again and turning.</summary>
    public bool AddSeat(string mapId, int rootId, string seatName, bool controls, Vector3 worldPosition,
                        float worldYaw, string requester, bool elevated, out string error)
    {
        error = "";
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) { error = "map not loaded"; return false; }
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<CompositeComponent>(root))
        { error = "that is not a composite"; return false; }
        if (!MayModify(world, root, requester, elevated))
        { error = $"'{world.Get<CompositeComponent>(root).Name}' belongs to {world.Get<CompositeComponent>(root).Owner}"; return false; }

        var rootT = world.Get<Transform>(root);
        var inverse = Quaternion.Inverse(rootT.Rotation);
        MathHelper.ToYawPitch(rootT.Rotation, out float rootYaw, out _);

        var seat = new Seat
        {
            Name = seatName,
            LocalPosition = Vector3.Transform(worldPosition - rootT.Position, inverse),
            LocalYaw = MathHelper.WrapAngle(worldYaw - rootYaw),
            Controls = controls,
        };

        if (!world.Has<OccupancyComponent>(root)) world.Add(root, new OccupancyComponent());
        ref var occupancy = ref world.Get<OccupancyComponent>(root);
        occupancy.Seats ??= new List<Seat>();
        int existing = occupancy.Seats.FindIndex(x => string.Equals(x.Name, seatName, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) occupancy.Seats[existing] = seat; else occupancy.Seats.Add(seat);

        Log.Information("Composite {Root}: seat '{Seat}' at {Local} ({Controls}).",
                        rootId, seatName, seat.LocalPosition, controls ? "drives" : "passenger");
        return true;
    }

    /// <summary>Forgets a seat. Anyone sitting in it is put out of the composite first.</summary>
    public bool RemoveSeat(string mapId, int rootId, string seatName, string requester, bool elevated, out string error)
    {
        error = "";
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) { error = "map not loaded"; return false; }
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<OccupancyComponent>(root))
        { error = "that has no seats"; return false; }
        if (!MayModify(world, root, requester, elevated))
        { error = $"'{world.Get<CompositeComponent>(root).Name}' belongs to {world.Get<CompositeComponent>(root).Owner}"; return false; }

        ref var occupancy = ref world.Get<OccupancyComponent>(root);
        int index = occupancy.Seats.FindIndex(x => string.Equals(x.Name, seatName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) { error = $"no seat called '{seatName}'"; return false; }

        // Everyone at or beyond the removed seat is put out: the indices shift, and a list edit must not
        // move a passenger into the driver's seat.
        foreach (var occupant in OccupantsOf(world, rootId))
            if (world.Get<OccupantComponent>(occupant).SeatIndex >= index) Disembark(world, occupant);

        occupancy.Seats.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Makes a free composite with a driving seat drive: the profile, velocity, body and engine voice the
    /// map's own traffic has, so a car built from walls is the same kind of object as one the map spawned.
    /// </summary>
    public bool MakeDrivable(string mapId, int rootId, string preset, string requester, bool elevated, out string error)
    {
        error = "";
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) { error = "map not loaded"; return false; }
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<CompositeComponent>(root))
        { error = "that is not a composite"; return false; }
        if (!MayModify(world, root, requester, elevated))
        { error = $"'{world.Get<CompositeComponent>(root).Name}' belongs to {world.Get<CompositeComponent>(root).Owner}"; return false; }
        if (world.Get<CompositeComponent>(root).Anchored)
        { error = "it is fixed in place; regroup it with 'free' first"; return false; }
        if (!HasDrivingSeat(world, root))
        { error = "nothing in it drives; add a seat with /addseat driver drive first"; return false; }

        return MakeDrivable(mapId, world, root, preset, out error);
    }

    /// <summary>Whether anybody could drive this: it has a seat that controls it. Checked when somebody asks
    /// for it to drive, the one moment the refusal can still be acted on.</summary>
    public static bool HasDrivingSeat(World world, Entity root)
        => world.Has<OccupancyComponent>(root)
        && world.Get<OccupancyComponent>(root).Seats is { Count: > 0 } seats
        && seats.Exists(s => s.Controls);

    private bool MakeDrivable(string mapId, World world, Entity root, string preset, out string error)
    {
        error = "";
        if (!MachineRegistry.Knows(preset))
        {
            error = $"'{preset}' is not a vehicle; try one of {string.Join(", ", MachineRegistry.Ids)}";
            return false;
        }
        var profile = MachineRegistry.VehicleFor(preset);
        var parts = PartsOf(world, root.Id);
        MathHelper.ToYawPitch(world.Get<Transform>(root).Rotation, out float yaw, out _);

        SetOrAdd(world, root, new DriveComponent { Preset = preset, Heading = yaw });
        SetOrAdd(world, root, new VehicleComponent
        {
            VehicleType = preset,
            MaxSeats = world.Has<OccupancyComponent>(root) ? world.Get<OccupancyComponent>(root).Seats.Count : 0,
        });
        SetOrAdd(world, root, new SoundEmitterComponent
        {
            // "engine:" makes the client run the engine itself from the speed this entity reports.
            IsSynth = true,
            SoundId = "engine:" + preset,
            Mode = PlaybackMode.LoopOne,
            Volume = 1f,
            Range = Loudness.AudibleRange(profile.SourceLevelDb),
            MinDistance = 3f,
            // Parked with the engine off; the key (DrivingSystem.SetIgnition) flips this and the client cranks it.
            SynthRunning = false,
        });
        // A body so the grid carries it into earshot; not solid, since its parts are.
        SetOrAdd(world, root, new ColliderComponent
        {
            Shape = ColliderShape.Box,
            Size = CompositeAcoustics.Bounds(world, parts, out _),
            IsSolid = false,
        });
        MakeDynamic(mapId, world, root, MembersOf(world, root.Id));
        Log.Information("Composite {Root} drives as a {Preset} ({Engine}).", root.Id, preset, profile.Engine.Name);
        return true;
    }

    private static void SetOrAdd<T>(World world, Entity e, T component) where T : struct
    {
        if (world.Has<T>(e)) world.Set(e, component); else world.Add(e, component);
    }

}
