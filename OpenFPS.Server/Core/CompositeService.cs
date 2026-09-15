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
    public int Group(string mapId, Vector3 near, float radius, string name, bool anchored, out int partCount)
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
            new CompositeComponent { Name = name, Anchored = anchored, TemplateId = "" },
            new NameComponent { Name = name },
            new IdentityComponent { Name = name, Announce = true },
            EntityType.StaticObject));
        if (root == Entity.Null) return -1;

        Attach(world, root, members, origin);
        partCount = members.Count;
        Log.Information("Composite '{Name}' grouped {Count} entit(ies) on '{Map}' at {Origin}.", name, partCount, mapId, origin);
        return root.Id;
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
    }

    /// <summary>
    /// Undoes a grouping without moving anything.
    ///
    /// The parts keep the world transforms they had a moment ago, because those transforms were never
    /// derived from the root while it stood still — the root was put where they already were. That is
    /// what makes group and ungroup safe to use while experimenting, which is what anyone building
    /// something will actually be doing.
    /// </summary>
    public bool Ungroup(string mapId, int rootId, out string name, out int partCount)
    {
        name = ""; partCount = 0;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return false;
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<CompositeComponent>(root)) return false;

        name = world.Get<CompositeComponent>(root).Name;
        foreach (var member in MembersOf(world, rootId))
        {
            world.Remove<ParentComponent>(member);
            partCount++;
        }
        lookup.Remove(rootId);
        world.Destroy(root);
        Log.Information("Composite '{Name}' ungrouped into {Count} loose entit(ies).", name, partCount);
        return true;
    }

    /// <summary>Every entity currently parented to this root.</summary>
    public static List<Entity> MembersOf(World world, int rootId)
    {
        var found = new List<Entity>();
        var q = new QueryDescription().WithAll<ParentComponent>();
        world.Query(in q, (Entity e, ref ParentComponent p) => { if (p.ParentEntityId == rootId) found.Add(e); });
        return found;
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
    public bool SaveAsTemplate(string mapId, int rootId, string templateId, out int partCount, out string error)
    {
        partCount = 0; error = "";
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) { error = "map not loaded"; return false; }
        if (!lookup.TryGetValue(rootId, out var root) || !world.IsAlive(root) || !world.Has<CompositeComponent>(root))
        { error = "that is not a composite"; return false; }

        var composite = world.Get<CompositeComponent>(root);
        var rootT = world.Get<Transform>(root);
        var inverse = Quaternion.Inverse(rootT.Rotation);

        var template = new CompositeTemplate
        {
            Id = templateId,
            Name = string.IsNullOrWhiteSpace(composite.Name) ? templateId : composite.Name,
            Anchored = composite.Anchored,
        };

        foreach (var member in MembersOf(world, rootId))
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
        if (!_composites.TryGet(templateId, out var template)) { error = $"no composite called '{templateId}'"; return -1; }
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) { error = "map not loaded"; return -1; }

        int rootId = Instantiate(mapId, template, position, rotation, out partCount);
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
                if (!_composites.TryGet(p.TemplateId, out var template))
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
                if (Instantiate(mapId, t, p.Position, p.Rotation, out _) >= 0) placed++; else failed++;
            }
            if (placed > 0 || failed > 0)
                Log.Information("Map '{Map}': {Placed} composite(s) placed, {Failed} failed.", mapId, placed, failed);
        }
    }

    /// <summary>Builds an instance without recording a placement — used by map load, which is
    /// replaying placements that are already recorded.</summary>
    public int Instantiate(string mapId, CompositeTemplate template, Vector3 position, Quaternion rotation,
                           out int partCount)
    {
        partCount = 0;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out _)) return -1;

        var root = _maps.SpawnEntity(mapId, w => w.Create(
            new Transform { Position = position, Rotation = rotation, IsDirty = true },
            new CompositeComponent { Name = template.Name, Anchored = template.Anchored, TemplateId = template.Id },
            new NameComponent { Name = template.Name },
            new IdentityComponent { Name = template.Name, Description = template.Description, Announce = true },
            EntityType.StaticObject));
        if (root == Entity.Null) return -1;

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

        Log.Information("Placed composite '{Id}' on '{Map}' at {Pos} — {Parts} part(s).", template.Id, mapId, position, partCount);
        return root.Id;
    }
}
