using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Arch.Core;

namespace OpenFPS.Server.Core;

/// <summary>Building: composites, the build cursor, prefabs and saving the map.</summary>
public partial class CommandHandler
{
    // ── Building ────────────────────────────────────────────────────────────────────────────────

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
    /// /group name [radius] [free] — makes one thing out of everything standing near you.
    ///
    /// A radius rather than a selection, because a selection needs pointing at things and pointing is
    /// the one thing a player here cannot do. "Everything within twelve metres of me" is a selection
    /// anybody can make, and can widen or narrow until it is the right one.
    /// </summary>
    private void HandleGroup(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /group name [radius] [free]"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        string name = args[0];
        float radius = args.Length > 1 && float.TryParse(args[1], out float r) ? r : DefaultGroupRadius;
        // "free" is the word for a composite that is not fixed down: a caravan rather than a house.
        bool anchored = !args.Contains("free", StringComparer.OrdinalIgnoreCase);

        int root = svc.Group(session.CurrentMapId, position, radius, name, anchored, session.Username, out int parts);
        if (root < 0) { Say(reply, $"Nothing within {radius:F0} m could be grouped."); return; }
        Say(reply, $"Grouped {parts} part(s) within {radius:F0} m into '{name}', "
                 + $"{(anchored ? "fixed in place" : "free to be moved")}, yours. Save it with /saveas.");
    }

    /// <summary>/ungroup — takes the nearest composite apart, leaving its parts exactly where they are.</summary>
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

    /// <summary>/saveas id — writes the nearest composite to disk so anyone can place it again.</summary>
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

    /// <summary>/place id [yaw] — puts a saved composite down at your feet, facing where you like.</summary>
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

    // ── Building where you cannot point ─────────────────────────────────────────────────────────
    //
    // Walking to the spot works for a wall and not for a roof, and walking to twenty wall positions
    // in a row does not reliably produce a straight wall. So there is a CURSOR: moved in metres from
    // an origin the player chose, announcing itself and what is already there every time it moves.
    // That is a review cursor, which a screen-reader user has navigated documents with for years.

    /// <summary>/origin — puts the build origin at your feet, facing the way you face.</summary>
    private void HandleOrigin(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;
        session.Build.SetOrigin(position, yaw);
        Say(reply, $"Build origin set at your feet, forward is {Compass(yaw)}. The cursor is at the origin.");
    }

    /// <summary>
    /// /at [right up forward] — moves the cursor there and says what is there, or just reads it out.
    ///
    /// Also takes a named direction and a distance (`/at forward 3`), which is the form you want when
    /// you are running a wall: it moves RELATIVE to where the cursor already is, and remembers the
    /// direction so a `/put ... run` can follow it.
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

    /// <summary>
    /// What is already where the cursor is.
    ///
    /// The half of the cursor that makes it usable. Moving to a spot and being told nothing is the
    /// same as not moving; being told "concrete wall" is how you find the wall you placed a minute
    /// ago and build the next one against it.
    /// </summary>
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
    /// /put prefab [turn degrees] [run n] — places a prefab at the cursor.
    ///
    /// The run is the important half. A wall is not one part, it is a line of them, and a line placed
    /// by hand from a cursor is only as straight as the arithmetic somebody did in their head. Asking
    /// for a run steps by the part's OWN footprint along the direction the cursor last travelled, so
    /// the panels touch, the wall is straight, and it is one command instead of eight.
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
                // A direction on the run itself — "run 4 right" — which is what anybody would say.
                // Without it the only way to aim a run is to move the cursor zero metres in the
                // direction you want first, which works and is a riddle.
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
        // Leave the cursor at the end of the run, where the next thing goes.
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
        // A prefab need not have a body at all (a region volume, a bare emitter). One metre is a
        // sane pitch for a run of those, and the alternative — a run that never advances — would
        // stack the whole lot in one place with nothing to say why.
        var size = prefabs.Prefabs[prefabId.ToLowerInvariant()].ColliderSize ?? Vector3.One;
        if (size == Vector3.Zero) return 1f;
        // The part is rotated into the world; the step is in the builder's axes. Measure the part's
        // extent along the step by taking both into the same frame.
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

    /// <summary>
    /// /undo — takes back the last thing you placed.
    ///
    /// Not a convenience. A part in the wrong place is invisible to somebody who cannot see it, so
    /// the mistake is not merely unfixed, it is undetectable until they walk into it — and by then
    /// they have built three more things around it.
    /// </summary>
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
    /// /room [radius] — says whether what is around you encloses a room, and if not, what is missing.
    ///
    /// A dry run of the rule `/group` will apply, so it can be asked BEFORE committing. This is the
    /// feedback loop the whole of building without sight hangs off: a sighted builder stands back and
    /// sees that the roof is missing, and this is the replacement for standing back.
    /// </summary>
    private void HandleRoom(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        float radius = args.Length > 0 && float.TryParse(args[0], out float r) ? r : DefaultGroupRadius;

        // Exactly what a sweep of this radius would take, measured about where it would put the origin.
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

    /// <summary>/prefabs — what there is to put down.</summary>
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

    /// <summary>/composites — what there is to place.</summary>
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
    /// /savemap — writes this map to disk exactly as it now stands.
    ///
    /// Explicit rather than automatic on purpose. A world that rewrites its own map file every time
    /// somebody experiments cannot be experimented with, and the first thing anyone does with a
    /// building tool is put something in the wrong place.
    /// </summary>
    private void HandleSaveMap(UserSession session, Action<IMessage> reply)
    {
        if (!_maps.SaveMap(session.CurrentMapId, out string error))
        { Say(reply, $"Could not save the map: {error}."); return; }
        Say(reply, $"Map '{session.CurrentMapId}' saved. Anything you placed is now permanent.");
    }
}
