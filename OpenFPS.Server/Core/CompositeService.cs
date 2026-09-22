using System.Numerics;
using Arch.Core;
using Arch.Core.Extensions;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Turning a pile of entities into a thing, and back.
///
/// Four operations, and between them they answer four questions that looked separate: how does
/// somebody build a house, how do they customise it, can they later classify it as an object, and is
/// it permanent where they built it.
///
///   GROUP   takes what is already standing there and makes it one thing with an origin.
///   UNGROUP undoes that, leaving the same entities exactly where they were.
///   SAVE    writes that thing to disk as a template, so it can be placed again by anyone.
///   PLACE   instantiates a template into the world.
///
/// Customising is then not a feature at all — it is grouping, adding or moving parts, and saving
/// again. And "permanent" is a property of the PLACEMENT (the map records it), not of the walls.
///
/// None of this needed a new transform system. Members wear a <see cref="ParentComponent"/> pointing
/// at the root, and ParentSystem — which has run every tick since long before any of this — already
/// carries them with it. A house that never moves and a vehicle you can drive away are the same
/// structure; only whether anything moves the root differs.
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

    /// <summary>
    /// What a grouping sweep will and will not take.
    ///
    /// Players, vehicles and anything already in a composite are left alone — a house cannot swallow
    /// the person building it, or the car parked outside, or the shed next door. Everything else
    /// within reach is fair game, because "everything I can see around me" is the selection a person
    /// can actually make when they cannot point at anything.
    /// </summary>
    public static bool CanBeGrouped(World world, Entity e)
        => world.IsAlive(e)
        && world.Has<Transform>(e)
        && !world.Has<PlayerComponent>(e)
        && !world.Has<VehicleComponent>(e)
        && !world.Has<CompositeComponent>(e)
        && !world.Has<ParentComponent>(e);

    /// <summary>
    /// Makes one thing out of everything solid standing near a point.
    ///
    /// The origin is the CENTRE of what was taken, in the ground plane, and at the lowest point
    /// vertically — which is to say, where the thing meets the ground. That matters for placing it
    /// again later: a house placed at your feet should have its floor at your feet, not its middle.
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
    /// Makes a composite and everything in it move-able, as far as the spatial grid is concerned.
    ///
    /// The grid keeps two halves: a static one, built once and rebuilt only when geometry is created
    /// or destroyed, and a dynamic one rebuilt from scratch every tick. Which half a thing goes in is
    /// decided by whether it carries a <see cref="Velocity"/>. A free composite's walls MOVE, so
    /// leaving them in the static half would leave the car's body permanently parked where it was
    /// built: players colliding with a ghost of it, and the audio hearing it there.
    ///
    /// The rebuild at the end is what evicts those stale static entries; it is the only way the
    /// static half can forget anything.
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
    /// Whether somebody may take this composite apart, save it out, change what it is, or drive it.
    ///
    /// Owning something is not the same as having a fence round it. A composite with no owner is
    /// public property, an elevated role can do anything, and NOTHING here gates walking into a
    /// building or sitting in a passenger seat — a world where you cannot enter other people's
    /// houses is not a world, it is a street of locked doors.
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
    /// Whether a thing is too big to have been what somebody meant.
    ///
    /// A sweep is somebody standing in a place and saying "this, and everything round me". The floor
    /// they are standing on is within twelve metres of them and so is the field it sits in, and
    /// neither is what they meant — you cannot be SELECTING something whose far side is nowhere near
    /// you. So anything wider than the sweep itself is not in it.
    ///
    /// Geometric, not a list of things called floors. The same rule keeps a house out of a sweep
    /// meant for the table inside it and lets a twelve-metre sweep take a twelve-metre wall, which is
    /// exactly the line a person would draw. Widen the radius and the bigger thing comes into scope,
    /// which is also right: at thirty metres you plainly do mean the building.
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
    /// Shuts every door in a set and makes it forget where "shut" was.
    ///
    /// A door records its shut pose in whatever frame it lives in — world for one standing on its
    /// own, parent-local for one that is part of a building. Grouping and ungrouping CHANGE that
    /// frame, so a door that remembers a world position and is then asked to swing as a part
    /// computes its local pose from a world one and flings the leaf out of the world. Found exactly
    /// that way: a shed's door opened perfectly until the shed was grouped, and then vanished.
    ///
    /// Shutting it first rather than trying to carry the swing across is the honest answer. A door
    /// shuts when the building it belongs to is picked up or taken apart, which is both easy to say
    /// and what anybody would expect.
    /// </summary>
    private static void ShutAndForget(World world, IEnumerable<Entity> entities)
    {
        foreach (var e in entities)
        {
            if (!world.IsAlive(e) || !world.Has<DoorComponent>(e)) continue;
            ref var door = ref world.Get<DoorComponent>(e);
            door.Openness = 0f;
            door.Target = 0f;
            door.Captured = false;
            if (world.Has<PortalComponent>(e)) world.Get<PortalComponent>(e).ApertureSize = 0f;
        }
    }

    /// <summary>
    /// Undoes a grouping without moving anything.
    ///
    /// The parts keep the world transforms they had a moment ago, because those transforms were never
    /// derived from the root while it stood still — the root was put where they already were. That is
    /// what makes group and ungroup safe to use while experimenting, which is what anyone building
    /// something will actually be doing.
    /// </summary>
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
            // The room went with the building, so it goes with the building being taken apart. Left
            // behind it would be an invisible volume in a field that still sounds like a room.
            if (world.Has<DerivedRoomComponent>(member)) { _maps.DestroyEntity(mapId, member); continue; }
            world.Remove<ParentComponent>(member);
            members.Add(member);
            partCount++;
            // Its frame just changed from the building's to the world's; see ShutAndForget.
            if (world.Has<DoorComponent>(member)) ShutAndForget(world, new[] { member });
        }
        // Whoever was inside it is standing in the open now; the thing they were sitting in is gone.
        foreach (var occupant in OccupantsOf(world, rootId)) Disembark(world, occupant);

        if (!composite.Anchored) MakeStatic(mapId, world, members);
        lookup.Remove(rootId);
        world.Destroy(root);
        // The root itself may have been in the static half of the grid; only a rebuild forgets it.
        if (composite.Anchored) _maps.RefreshGrid(mapId);
        Log.Information("Composite '{Name}' ungrouped into {Count} loose entit(ies).", name, partCount);
        return true;
    }

    /// <summary>Every entity currently parented to this root — the parts it is MADE of.</summary>
    public static List<Entity> MembersOf(World world, int rootId)
    {
        var found = new List<Entity>();
        var q = new QueryDescription().WithAll<ParentComponent>();
        world.Query(in q, (Entity e, ref ParentComponent p) => { if (p.ParentEntityId == rootId) found.Add(e); });
        return found;
    }

    /// <summary>
    /// The parts a composite is BUILT from — its members, less the room it derived for itself.
    ///
    /// The derived room is a member in every mechanical sense (it is parented, it is carried, it is
    /// destroyed with the thing) and is not a part in any meaningful one: nobody built it, it cannot
    /// be saved, and measuring the building's own size by including it would be measuring the answer.
    /// </summary>
    public static List<Entity> PartsOf(World world, int rootId)
    {
        var parts = MembersOf(world, rootId);
        parts.RemoveAll(e => world.Has<DerivedRoomComponent>(e));
        return parts;
    }

    /// <summary>Everyone currently inside this root — the people it is CARRYING.</summary>
    public static List<Entity> OccupantsOf(World world, int rootId)
    {
        var found = new List<Entity>();
        var q = new QueryDescription().WithAll<OccupantComponent>();
        world.Query(in q, (Entity e, ref OccupantComponent o) => { if (o.RootEntityId == rootId) found.Add(e); });
        return found;
    }

    /// <summary>
    /// Takes somebody out of whatever they were in, wherever they now are.
    ///
    /// Deliberately does NOT move them: the caller knows whether this is getting out (put them down
    /// beside it) or the thing they were in ceasing to exist (leave them exactly where it left them).
    /// </summary>
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

    /// <summary>The composite root nearest a point, or -1. How a player refers to "this house" when
    /// they are standing in it and cannot click on anything.</summary>
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

    /// <summary>
    /// Writes a live composite to disk as something anyone can place again.
    ///
    /// The parts are recorded in the composite's OWN frame, which is why the root's origin mattered
    /// when it was grouped: everything here is relative to it, so placing the template anywhere
    /// reproduces the same arrangement rather than the same coordinates.
    /// </summary>
    public bool SaveAsTemplate(string mapId, int rootId, string templateId, string requester, bool elevated,
                               out int partCount, out string error)
    {
        partCount = 0; error = "";
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

        // Seats and the vehicle profile are part of what the thing IS, so they go out with it. Place
        // the template again and the second one has the same seats and the same engine, the same way
        // it has the same walls.
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
                // A part with no prefab behind it cannot be rebuilt from a template, and silently
                // dropping it would produce a house missing a wall with nothing to say why.
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
        // The composite now knows what it is an instance of — which is precisely the "build it out of
        // parts, then classify it as an object" step.
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
    /// A vehicle shell for the map's own traffic to drive: a bus you can get on.
    ///
    /// The same shell a parked car is, with two differences. It has no engine of its own to drive —
    /// VehicleSystem moves it along its route, exactly as it moves every other bus — so no
    /// DriveComponent, which would have DrivingSystem trying to drive it too. And it has no seat that
    /// drives: somebody is already driving it. Not recorded on the map: the route spawns it.
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
    /// Puts a saved composite into the world, and records that it is there.
    ///
    /// The recording is the important half. An instance that exists only in memory is a house until
    /// the server restarts; appending the placement to the map's own data is what makes it a house
    /// afterwards too. <see cref="MapManager.SaveMap"/> is what commits that to disk.
    /// </summary>
    public int Place(string mapId, string templateId, Vector3 position, Quaternion rotation,
                     string owner, out int partCount, out string error)
    {
        partCount = 0; error = "";
        if (!TryGetTemplate(templateId, out var template)) { error = $"no composite called '{templateId}'"; return -1; }
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) { error = "map not loaded"; return -1; }

        int rootId = Instantiate(mapId, template, position, rotation, owner, out partCount);
        if (rootId < 0) { error = "nothing could be placed"; return -1; }

        if (_maps.TryGetMapData(mapId, out var data))
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
    /// Rebuilds every composite each loaded map says is standing on it.
    ///
    /// Called once at startup, after the maps are loaded and before anything measures them — a placed
    /// building is geometry, and the acoustic scene, the spatial grid and the broadcast radius all
    /// have to be sized with it in place rather than around a hole where it will appear later.
    /// </summary>
    public void PlaceRecorded(MapManager maps)
    {
        foreach (string mapId in maps.LoadedMapIds)
        {
            if (!maps.TryGetMapData(mapId, out var data) || data.Composites == null) continue;
            int placed = 0, failed = 0;
            foreach (var p in data.Composites)
            {
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

    /// <summary>Builds an instance without recording a placement — used by map load, which is
    /// replaying placements that are already recorded.</summary>
    public int Instantiate(string mapId, CompositeTemplate template, Vector3 position, Quaternion rotation,
                           string owner, out int partCount)
    {
        partCount = 0;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return -1;

        var root = _maps.SpawnEntity(mapId, w => w.Create(
            new Transform { Position = position, Rotation = rotation, IsDirty = true },
            new CompositeComponent { Name = template.Name, Anchored = template.Anchored, TemplateId = template.Id, Owner = owner ?? "" },
            new NameComponent { Name = template.Name },
            new IdentityComponent { Name = template.Name, Description = template.Description, Announce = true },
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
    /// Gives a composite the room it encloses, or takes away the one it no longer does.
    ///
    /// Called every time the shape changes — grouped, placed, rebuilt. Cheap, and idempotent: the
    /// derived room is destroyed and made again from what is actually there now, so a wall added or a
    /// roof taken off is reflected without anyone having to say which change invalidates what.
    ///
    /// The room is a PART, not a flag on the root, so ParentSystem carries it with the building for
    /// free and a house you drive away takes its acoustics with it. It is exactly what somebody
    /// authoring a building by hand is already told to do.
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
            // Not solid — it is a volume, not an obstacle. It needs a collider all the same: the
            // broadcast walks the spatial grid, and the grid only carries things that have one, so a
            // room without a body is a room no client is ever told about.
            new ColliderComponent { Shape = ColliderShape.Box, Size = room.RoomSize, IsSolid = false },
            new DerivedRoomComponent(),
            new NameComponent { Name = room.FriendlyName },
            EntityType.Trigger));
        if (e == Entity.Null) return false;

        // Every door in the building now leads somewhere: from this room to the outside. Which room a
        // doorway joins is a property of WHERE IT IS, and until the room existed there was nothing
        // for it to be a doorway into — so this is the moment the link can be made, and it is remade
        // whenever the shape changes for the same reason.
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

    /// <summary>
    /// Puts a seat where somebody is standing.
    ///
    /// Authored by standing in the right place rather than by typing coordinates, for the same reason
    /// grouping is by radius: a player here cannot point at anything, but they can always walk to a
    /// spot and say "here". The seat is recorded in the composite's OWN frame, so it survives the
    /// thing being saved, placed again, and turned to face another way.
    /// </summary>
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

        // Everyone at or beyond the removed seat is put out: the indices behind it all shift, and a
        // passenger silently moved into the driver's seat by a list edit is not a thing that should
        // be able to happen.
        foreach (var occupant in OccupantsOf(world, rootId))
            if (world.Get<OccupantComponent>(occupant).SeatIndex >= index) Disembark(world, occupant);

        occupancy.Seats.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Makes a composite drive.
    ///
    /// Everything this adds is something the map's own traffic already has: a profile naming the
    /// engine and the tyres, a velocity so the world treats it as a thing that moves, a body the
    /// grid can see, and a synthesised engine the client runs itself from the speed it is told. That
    /// is the point — a car somebody built out of walls and a car the map spawned are the same kind
    /// of object, so every bit of machinery that already makes traffic audible works on this one
    /// without knowing it exists.
    ///
    /// It must be a FREE composite. A house that drives away is a caravan, and saying so is the one
    /// line of policy in here.
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

    /// <summary>
    /// Whether anybody could actually drive this.
    ///
    /// A vehicle with no driving seat is not a vehicle, it is a shed with an engine in it: nothing
    /// can ever ask it to move, so all an engine buys it is a noise. Refusing at the moment somebody
    /// says "make it drivable" is the only place the refusal helps — by the time it is a saved
    /// template being placed on a map at startup, the person who could have added a seat is long gone.
    /// </summary>
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
            // The client recognises the "engine:" prefix and runs the engine itself, following the
            // speed this entity reports. The server decides where the car is and how fast; it knows
            // nothing about exhausts.
            IsSynth = true,
            SoundId = "engine:" + preset,
            Mode = PlaybackMode.LoopOne,
            Volume = 1f,
            Range = Loudness.AudibleRange(profile.SourceLevelDb),
            MinDistance = 3f,
            // Parked with the engine off. The key starts it (DrivingSystem.SetIgnition), and the
            // client cranks it on the starter when this flips, because that is what Running does to
            // an engine voice that was stopped.
            SynthRunning = false,
        });
        // A body, so the grid carries it into earshot and a person can walk into it. Not solid: the
        // parts it is built from are the solid things, and they are already here.
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
