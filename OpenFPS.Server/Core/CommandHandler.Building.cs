using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Arch.Core;

namespace OpenFPS.Server.Core;

/// <summary>
/// Building: composites, the build cursor, prefabs and saving the map. Every verb works without
/// pointing; see docs/SERVER_NOTES.md, "Building without pointing".
/// </summary>
public partial class CommandHandler
{
    /// <summary>How far a grouping sweep reaches by default, metres. About a room.</summary>
    private const float DefaultGroupRadius = 12f;
    /// <summary>How far away a composite may be and still count as "this one".</summary>
    private const float CompositeReachRadius = 30f;

    private CompositeService? Composites(Action<IMessage> reply)
    {
        if (_composites == null) Say(reply, "Building is not available on this server.");
        return _composites;
    }

    /// <summary>
    /// /group name [radius] [free]: one thing out of everything near you. A radius, because a selection
    /// needs pointing.
    /// </summary>
    private void HandleGroup(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /group name [radius] [free]"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        string name = args[0];
        float radius = args.Length > 1 && float.TryParse(args[1], out float r) ? r : DefaultGroupRadius;
        // "free": not fixed down, a caravan rather than a house.
        bool anchored = !args.Contains("free", StringComparer.OrdinalIgnoreCase);

        int root = svc.Group(session.CurrentMapId, position, radius, name, anchored, session.Username, out int parts);
        if (root < 0) { Say(reply, $"Nothing within {radius:F0} m could be grouped."); return; }
        Say(reply, $"Grouped {parts} part(s) within {radius:F0} m into '{name}', "
                 + $"{(anchored ? "fixed in place" : "free to be moved")}, yours. Save it with /saveas.");
    }

    /// <summary>/ungroup: takes the nearest composite apart, its parts left where they are.</summary>
    private void HandleUngroup(UserSession session, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m."); return; }
        if (!svc.Ungroup(session.CurrentMapId, root, session.Username, Elevated(session),
                         out string name, out int parts, out string error))
        { Say(reply, $"That could not be ungrouped: {error}."); return; }
        Say(reply, $"'{name}' is now {parts} loose part(s), all where they were.");
    }

    /// <summary>/saveas id: writes the nearest composite to disk so anyone can place it again.</summary>
    private void HandleSaveAs(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /saveas id"); return; }
        // The id is the design's file name.
        if (!SafeText.IsFileName(args[0])) { Say(reply, "A design's name is letters, digits, _ and -, up to 64."); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m. Use /group first."); return; }
        // Designs are everybody's: saving one on your own map may add a design, not replace one.
        if (!session.Can("saveas") && svc.Templates.TryGet(args[0], out _))
        { Say(reply, $"There is already a design called '{args[0]}'. Choose another name."); return; }
        if (!svc.SaveAsTemplate(session.CurrentMapId, root, args[0], session.Username, Elevated(session),
                                out int parts, out string error))
        { Say(reply, $"Could not save: {error}."); return; }
        Say(reply, $"Saved '{args[0]}' with {parts} part(s). Place another with /place {args[0]}.");
    }

    /// <summary>/place id [yaw]: puts a saved composite down at your feet.</summary>
    private void HandlePlace(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /place id [yaw degrees]"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        float yaw = args.Length > 1 && float.TryParse(args[1], out float y) ? y : 0f;
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw * (MathF.PI / 180f), 0f, 0f);

        int root = svc.Place(session.CurrentMapId, args[0], position, rotation, session.Username,
                             out int parts, out string error);
        if (root < 0) { Say(reply, $"Could not place: {error}."); return; }
        Say(reply, $"Placed '{args[0]}' here, {parts} part(s), facing {yaw:F0} degrees. "
                 + "It will be gone after a restart until you /savemap.");
    }

    /// <summary>Whether somebody may change what other people built here: anywhere with edit-any, and
    /// on a map of their own.</summary>
    private bool Elevated(UserSession session) => session.Can(Permissions.EditAny) || OwnsHere(session);

    // ── The build cursor: a review cursor moved in metres from an origin the player chose ───────

    /// <summary>/origin: the build origin at your feet, facing the way you face.</summary>
    private void HandleOrigin(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;
        session.Build.SetOrigin(position, yaw);
        Say(reply, $"Build origin set at your feet, forward is {Compass(yaw)}. The cursor is at the origin.");
    }

    /// <summary>
    /// /at [right up forward]: moves the cursor and says what is there; on its own, reads it out.
    /// `/at forward 3` moves relative to the cursor and remembers the direction for `/put ... run`.
    /// </summary>
    private void HandleAt(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out var grid, out _)) return;
        var build = session.Build;
        if (!build.Placed) { Say(reply, "Set a build origin first with /origin."); return; }

        if (args.Length >= 2 && BuildSession.TryDirection(args[0], out var direction))
        {
            if (!float.TryParse(args[1], out float distance)) { Say(reply, $"'{args[1]}' is not a distance."); return; }
            build.Cursor += direction * distance;
            build.LastStep = direction;
        }
        else if (args.Length >= 3)
        {
            if (!float.TryParse(args[0], out float right) || !float.TryParse(args[1], out float up) || !float.TryParse(args[2], out float forward))
            { Say(reply, "Usage: /at right up forward, or /at <direction> <metres>"); return; }
            var moved = new Vector3(right, up, forward) - build.Cursor;
            if (moved.LengthSquared() > 0.0001f) build.LastStep = Vector3.Normalize(moved);
            build.Cursor = new Vector3(right, up, forward);
        }
        else if (args.Length != 0)
        {
            Say(reply, "Usage: /at right up forward, or /at <direction> <metres>, or /at on its own to read it out.");
            return;
        }

        Say(reply, $"Cursor {build.Describe()}. {WhatIsAt(world, grid, build.WorldCursor)}");
    }

    /// <summary>What is already where the cursor is: how a builder finds the wall placed a minute ago.</summary>
    private static string WhatIsAt(World world, SpatialGrid<Entity> grid, Vector3 at)
    {
        Entity? closest = null;
        float nearest = 1.5f;
        foreach (var e in grid.GetItemsInRadius(at, 3f))
        {
            if (!world.IsAlive(e) || !world.Has<Transform>(e) || world.Has<PlayerComponent>(e)) continue;
            float d = Vector3.Distance(world.Get<Transform>(e).Position, at);
            if (d < nearest) { nearest = d; closest = e; }
        }
        if (closest == null) return "Empty.";
        string name = world.Has<IdentityComponent>(closest.Value) ? world.Get<IdentityComponent>(closest.Value).Name
                    : world.Has<NameComponent>(closest.Value) ? world.Get<NameComponent>(closest.Value).Name
                    : "something";
        return nearest < 0.3f ? $"{name}, right here." : $"{name}, {nearest:F1} m off.";
    }

    /// <summary>
    /// /put prefab [turn degrees] [run n [direction]]: places a prefab at the cursor. A run steps by the
    /// part's own footprint along the cursor's last direction, so the panels of a wall touch.
    /// </summary>
    private void HandlePut(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1)
        { Say(reply, "Usage: /put prefab [turn degrees] [run count [direction]]. /prefabs lists them."); return; }
        if (!TryGetBody(session, reply, out var world, out var grid, out _)) return;

        var build = session.Build;
        if (!build.Placed) { Say(reply, "Set a build origin first with /origin."); return; }

        string prefabId = args[0];
        if (!svc.Prefabs.Prefabs.ContainsKey(prefabId.ToLowerInvariant()))
        { Say(reply, $"There is no prefab called '{prefabId}'. /prefabs lists them."); return; }

        float turn = 0f;
        int run = 1;
        var step = build.LastStep;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("turn", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                float.TryParse(args[i + 1], out turn);
            else if (args[i].Equals("run", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                int.TryParse(args[i + 1], out run);
                // "run 4 right": a direction on the run itself.
                if (i + 2 < args.Length && BuildSession.TryDirection(args[i + 2], out var aimed))
                {
                    step = aimed;
                    build.LastStep = aimed;
                }
            }
        }
        run = Math.Clamp(run, 1, 64);

        var rotation = build.Facing(turn);
        float spacing = FootprintAlong(svc.Prefabs, prefabId, rotation, build.Yaw, step);

        int placed = 0, blocked = 0;
        for (int i = 0; i < run; i++)
        {
            var local = build.Cursor + step * (spacing * i);
            var at = build.ToWorld(local);
            if (Occupied(world, grid, at, 0.3f)) { blocked++; continue; }

            Entity e;
            try { e = _maps.SpawnEntity(session.CurrentMapId, w => svc.Prefabs.Spawn(w, prefabId, at, rotation, Vector3.One)); }
            catch (Exception ex) { Say(reply, $"Could not place it: {ex.Message}"); return; }
            if (e == Entity.Null) { Say(reply, "Could not place it: the map is not loaded."); return; }
            build.Placed_Entities.Add(e.Id);
            placed++;
        }

        if (placed == 0) { Say(reply, "There is already something there. Nothing placed."); return; }
        if (placed > 1) build.Cursor += step * (spacing * placed);

        string what = svc.Prefabs.Prefabs[prefabId.ToLowerInvariant()].Name;
        Say(reply, placed == 1
            ? $"{what} placed {build.Describe()}.{(blocked > 0 ? " Something was already there." : "")}"
            : $"{placed} x {what} placed in a line, {spacing:0.##} m apart. Cursor now {build.Describe()}."
              + (blocked > 0 ? $" {blocked} skipped, something was already there." : ""));
    }

    /// <summary>How far along a direction one of these reaches, so a run of them touches.</summary>
    private static float FootprintAlong(PrefabRepository prefabs, string prefabId, Quaternion rotation,
                                        float buildYaw, Vector3 stepInBuildAxes)
    {
        // A prefab with no body (a region volume, a bare emitter) runs at one metre, or the whole run
        // would stack in one place.
        var size = prefabs.Prefabs[prefabId.ToLowerInvariant()].ColliderSize ?? Vector3.One;
        if (size == Vector3.Zero) return 1f;
        // The part is turned in the world and the step is in the builder's axes: one frame for both.
        var half = CompositeAcoustics.AxisAlignedHalfExtents(size * 0.5f,
            Quaternion.Concatenate(rotation, Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(buildYaw, 0f, 0f))));
        float along = MathF.Abs(stepInBuildAxes.X) * half.X
                    + MathF.Abs(stepInBuildAxes.Y) * half.Y
                    + MathF.Abs(stepInBuildAxes.Z) * half.Z;
        return MathF.Max(0.1f, along * 2f);
    }

    /// <summary>Whether something solid is already sitting where a part is about to go.</summary>
    private static bool Occupied(World world, SpatialGrid<Entity> grid, Vector3 at, float radius)
    {
        foreach (var e in grid.GetItemsInRadius(at, MathF.Max(radius, 1f)))
        {
            if (!world.IsAlive(e) || !world.Has<Transform>(e) || world.Has<PlayerComponent>(e)) continue;
            if (Vector3.Distance(world.Get<Transform>(e).Position, at) < radius) return true;
        }
        return false;
    }

    /// <summary>/undo: takes back the last thing you placed.</summary>
    private void HandleUndo(UserSession session, Action<IMessage> reply)
    {
        var build = session.Build;
        if (!TryGetBody(session, reply, out var world, out _, out _)) return;

        while (build.Placed_Entities.Count > 0)
        {
            int id = build.Placed_Entities[^1];
            build.Placed_Entities.RemoveAt(build.Placed_Entities.Count - 1);
            if (!_maps.TryGetMap(session.CurrentMapId, out _, out _, out _, out var lookup)) break;
            if (!lookup.TryGetValue(id, out var e) || !world.IsAlive(e)) continue;   // already gone

            string what = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name : "it";
            _maps.DestroyEntity(session.CurrentMapId, e);
            _server.BroadcastRemoval(session.CurrentMapId, id);
            Say(reply, $"Took back the {what}.");
            return;
        }
        Say(reply, "You have not placed anything.");
    }

    /// <summary>
    /// /room [radius]: whether what is around you encloses a room, and if not what is missing. A dry run
    /// of the rule /group applies.
    /// </summary>
    private void HandleRoom(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        float radius = args.Length > 0 && float.TryParse(args[0], out float r) ? r : DefaultGroupRadius;

        // Exactly what a sweep of this radius would take.
        var parts = new List<Entity>();
        var q = new QueryDescription().WithAll<Transform>();
        float r2 = radius * radius;
        world.Query(in q, (Entity e, ref Transform t) =>
        {
            if (Vector3.DistanceSquared(t.Position, position) > r2) return;
            if (!CompositeService.CanBeGrouped(world, e)) return;
            if (CompositeService.IsBiggerThanTheSweep(world, e, radius)) return;
            parts.Add(e);
        });

        if (parts.Count == 0) { Say(reply, $"Nothing within {radius:F0} m that could be built with."); return; }

        var survey = CompositeAcoustics.SurveyLoose(world, parts);
        Say(reply, $"{parts.Count} part(s) within {radius:F0} m. {survey.Explain()}");
    }

    /// <summary>/prefabs: what there is to put down.</summary>
    private void HandleListPrefabs(Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        var all = svc.Prefabs.Prefabs;
        if (all.Count == 0) { Say(reply, "No prefabs are loaded."); return; }
        Say(reply, $"{all.Count} prefab(s):");
        foreach (var kv in all)
        {
            var size = kv.Value.ColliderSize;
            Say(reply, $"  {kv.Key}: {kv.Value.Name}, {kv.Value.Material}"
                     + (size.HasValue ? $", {size.Value.X:0.##} by {size.Value.Y:0.##} by {size.Value.Z:0.##} m." : ", no body."));
        }
    }

    private static string Compass(float yaw)
    {
        float degrees = yaw * (180f / MathF.PI);
        while (degrees < 0) degrees += 360f;
        while (degrees >= 360f) degrees -= 360f;
        return degrees switch
        {
            < 22.5f or >= 337.5f => "north", < 67.5f => "north east", < 112.5f => "east",
            < 157.5f => "south east", < 202.5f => "south", < 247.5f => "south west",
            < 292.5f => "west", _ => "north west",
        };
    }

    /// <summary>/composites: what there is to place.</summary>
    private void HandleListComposites(Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        var all = svc.Templates.All;
        if (all.Count == 0) { Say(reply, "Nothing has been saved yet. Build something and use /group then /saveas."); return; }
        Say(reply, $"{all.Count} composite(s) available:");
        foreach (var kv in all)
            Say(reply, $"  {kv.Key}: {kv.Value.Name}, {kv.Value.Parts.Count} part(s), "
                     + $"{(kv.Value.Anchored ? "fixed" : "free")}.");
    }

    /// <summary>
    /// /savemap: writes this map to disk as it now stands. Never automatic, so a map can be
    /// experimented on.
    /// </summary>
    private void HandleSaveMap(UserSession session, Action<IMessage> reply)
    {
        if (!_maps.SaveMap(session.CurrentMapId, out string error))
        { Say(reply, $"Could not save the map: {error}."); return; }
        Say(reply, $"Map '{session.CurrentMapId}' saved. Anything you placed is now permanent.");
    }
}
