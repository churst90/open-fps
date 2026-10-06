using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>The kinds of place somebody with nowhere to live spends their day.</summary>
public enum HauntKind
{
    /// <summary>A bus stop: a shelter with a roof, people waiting, and a bus to ride.</summary>
    BusStop,
    /// <summary>The pavement outside a building's front entrance (a shop's, when the map has shops):
    /// people going in and out to ask.</summary>
    Doorway,
    /// <summary>Just inside an apartment building's front entrance: warm and dry, and somewhere to be
    /// at night.</summary>
    Lobby,
    /// <summary>An open square or a park: people crossing it, somewhere to sit by day.</summary>
    Square,
}

/// <summary>
/// One place a character goes. <see cref="Stand"/> is where they stand there, on the ground.
/// <see cref="Access"/> is where it joins the pavements; the way there ends at it and then steps to
/// <see cref="Stand"/>. A lobby has its door: <see cref="Outside"/> on the street and <see cref="Inside"/>
/// past it. A bus stop has the point on the road where the bus stands (<see cref="StopAt"/>).
/// </summary>
public sealed class Haunt
{
    public required string Name;
    public required HauntKind Kind;
    public required Vector3 Stand;
    /// <summary>Where they face, standing there: the street, mostly.</summary>
    public float Facing;
    public Vector2 Access;
    public int AccessEdge = -1;
    public Entity Door = Entity.Null;
    public Vector3 Outside, Inside;
    public Vector3 StopAt;

    public override string ToString() => Name;
}

/// <summary>
/// Finds the places on a map a homeless man would spend his day, from what the map is: its bus stops
/// (RoadStops of kind bus_stop, with the shelter beside them), its buildings' front entrances (the
/// pavement outside each, and, where the door opens onto a stairwell, lobby or hall, the inside of it),
/// and its open squares and parks. Nothing on the map names a place for him.
/// </summary>
public static class HauntFinder
{
    /// <summary>A shelter's parts this near a bus stop are that stop's shelter, metres.</summary>
    public const float ShelterMetres = 8f;

    public static List<Haunt> Find(World world, SpatialGrid<Entity> grid, MapData data, Pavements paths,
                                   IReadOnlyList<Pavements.Solid> solids)
    {
        var found = new List<Haunt>();
        float Ground(Vector3 p)
        {
            float g = PhysicsUtils.GetGroundHeight(world, grid, new Vector3(p.X, MathF.Max(p.Y, 0.3f), p.Z), out _);
            return g < -900f ? p.Y : g;
        }

        // ── Bus stops ───────────────────────────────────────────────────────────────────────────
        var shelterParts = new List<Vector3>();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithNone<Velocity>(),
            (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                string name = NameOf(world, e);
                if (name.Contains("shelter", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("roof", StringComparison.OrdinalIgnoreCase) && c.IsSolid)
                    shelterParts.Add(t.Position);
            });
        foreach (var stop in data.RoadStops ?? new List<RoadStopData>())
        {
            if (!string.Equals(stop.Kind, "bus_stop", StringComparison.OrdinalIgnoreCase)) continue;
            var s2 = new Vector2(stop.Position.X, stop.Position.Z);
            var near = shelterParts.Where(p => Vector2.Distance(new Vector2(p.X, p.Z), s2) < ShelterMetres).ToList();
            Vector2 stand;
            if (near.Count > 0)
                stand = near.Aggregate(Vector2.Zero, (sum, p) => sum + new Vector2(p.X, p.Z)) / near.Count;
            else
            {
                // No shelter: on the pavement beside the stop.
                stand = paths.IsEmpty ? s2 : paths.Nearest(s2, out _);
            }
            stand = NearestClear(stand, solids);
            var toRoad = s2 - stand;
            var h = new Haunt
            {
                Name = string.IsNullOrWhiteSpace(stop.Name) ? "the bus stop" : $"the bus stop, {stop.Name}",
                Kind = HauntKind.BusStop,
                Stand = new Vector3(stand.X, 0f, stand.Y),
                Facing = MathF.Atan2(toRoad.X, toRoad.Y),
                StopAt = stop.Position,
            };
            h.Stand = h.Stand with { Y = Ground(h.Stand) };
            found.Add(h);
        }

        // ── Front entrances: the pavement outside, and the lobby in ─────────────────────────────
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent d) =>
        {
            if (!DoorSystem.OpensByHand(d)) return;
            string name = NameOf(world, e);
            if (!name.Contains("entrance", StringComparison.OrdinalIgnoreCase)) return;
            DoorSystem.Doorway(world, e, out var centre, out var rotation);
            if (centre.Y > 2.5f) return;                                  // an upstairs door
            var through = Vector3.Transform(Vector3.UnitZ, rotation); through.Y = 0f;
            if (through.LengthSquared() < 1e-6f) return;
            through = Vector3.Normalize(through);
            // The street side: the key side of a door that has one, else the side nearer a pavement.
            float street = d.KeyedSide != 0f ? MathF.Sign(d.KeyedSide) : 0f;
            if (street == 0f && !paths.IsEmpty)
            {
                var plus = centre + through * 2f; var minus = centre - through * 2f;
                street = Vector2.Distance(paths.Nearest(new Vector2(plus.X, plus.Z), out _), new Vector2(plus.X, plus.Z))
                       <= Vector2.Distance(paths.Nearest(new Vector2(minus.X, minus.Z), out _), new Vector2(minus.X, minus.Z)) ? 1f : -1f;
            }
            if (street == 0f) street = 1f;
            var n = through * street;                                     // out, toward the street
            float floor = centre.Y - (world.Has<ColliderComponent>(e) ? world.Get<ColliderComponent>(e).Size.Y * 0.5f : 1.05f);
            // Out of the leaf's way: a front door swings out over the pavement, and a door does not
            // open through somebody standing in its sweep (DoorSystem), so he waits a leaf's width off.
            float leaf = world.Has<ColliderComponent>(e) ? MathF.Max(world.Get<ColliderComponent>(e).Size.X, world.Get<ColliderComponent>(e).Size.Z) : 1f;
            var outside = centre + n * MathF.Max(0.9f, leaf + 0.6f); outside.Y = Ground(outside);
            var inside = centre - n * 1.4f;
            if (Pavements.Blocked(solids, new Vector2(inside.X, inside.Z), floor)) inside = centre - n * 0.9f;
            inside.Y = Ground(inside with { Y = floor + 0.3f });
            string building = BuildingOf(name);

            // Beside the door, back against the wall: a doorway is somewhere to stand out of the way,
            // and clear of the leaf's sweep, which reaches a leaf's width past its hinge.
            var along = new Vector3(n.Z, 0f, -n.X);
            Vector3? beside = null;
            float clear = 1.5f * leaf + 0.4f;
            foreach (float a in new[] { clear, -clear, clear + 1f, -clear - 1f })
            {
                var p = centre + n * 0.55f + along * a;
                if (!Pavements.Blocked(solids, new Vector2(p.X, p.Z), floor)) { beside = p; break; }
            }
            if (beside is { } b)
                found.Add(new Haunt
                {
                    Name = $"outside {building} front entrance",
                    Kind = HauntKind.Doorway,
                    Stand = b with { Y = Ground(b) },
                    Facing = MathF.Atan2(n.X, n.Z),
                    Door = e, Outside = outside, Inside = inside,
                });

            // Inside, if inside is a hall of some kind and not somebody's flat.
            string? room = CommandHandler.PlaceAt(world, inside + new Vector3(0f, 1f, 0f));
            if (room == null || !IsLobby(room))
                Serilog.Log.Debug("Haunts: {Door} opens onto {Room}, not a lobby.", name, room ?? "nowhere named");
            if (room != null && IsLobby(room))
                found.Add(new Haunt
                {
                    Name = $"the lobby of {building}",
                    Kind = HauntKind.Lobby,
                    Stand = inside,
                    // Facing the door, as somebody waiting out of the weather does.
                    Facing = MathF.Atan2(n.X, n.Z),
                    Door = e, Outside = outside, Inside = inside,
                });
        });

        // ── Squares and parks ───────────────────────────────────────────────────────────────────
        world.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (ref Transform t, ref RegionComponent r) =>
        {
            if (r.IsIndoor || !IsSquare(r.FriendlyName)) return;
            var c = new Vector2(t.Position.X, t.Position.Z);
            Vector2? clear = null;
            foreach (float radius in new[] { 0f, 4f, 7f, 10f })
                for (int k = 0; k < (radius == 0f ? 1 : 8) && clear == null; k++)
                {
                    float a = k * MathF.PI / 4f;
                    var p = c + new Vector2(MathF.Sin(a), MathF.Cos(a)) * radius;
                    if (!Pavements.Blocked(solids, p)) clear = p;
                }
            if (clear is not { } spot) return;
            var stand = new Vector3(spot.X, 0f, spot.Y);
            found.Add(new Haunt { Name = r.FriendlyName, Kind = HauntKind.Square, Stand = stand with { Y = Ground(stand) } });
        });

        // Where each joins the pavements: the nearest point on them it can walk to in a straight line.
        foreach (var h in found)
        {
            // A lobby is reached through its door, from the pavement outside it.
            var from = h.Kind == HauntKind.Lobby ? new Vector2(h.Outside.X, h.Outside.Z) : new Vector2(h.Stand.X, h.Stand.Z);
            if (paths.IsEmpty) { h.Access = from; continue; }
            (Vector2 At, int Edge) pick = (paths.Nearest(from, out int edge), edge);
            foreach (var cand in paths.NearestPoints(from).Take(12))
                if (!Pavements.LineBlocked(solids, from, cand.At)) { pick = cand; break; }
            h.Access = pick.At;
            h.AccessEdge = pick.Edge;
        }
        // In a fixed order, so every choice made from the list is the same every time.
        return found.OrderBy(h => h.Kind).ThenBy(h => h.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>"Marlow Tower" from "Marlow Tower front entrance".</summary>
    public static string BuildingOf(string doorName)
    {
        int at = doorName.IndexOf(" front entrance", StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = doorName.IndexOf(" entrance", StringComparison.OrdinalIgnoreCase);
        return at > 0 ? doorName[..at] : doorName;
    }

    /// <summary>A hall inside a front door: a stairwell (its stairs and landings), a lobby, a foyer, a hall or a corridor, and not a flat.</summary>
    public static bool IsLobby(string room)
    {
        string r = room.ToLowerInvariant();
        if (r.Contains("flat") || r.Contains("apartment ")) return false;
        return r.Contains("stair") || r.Contains("landing") || r.Contains("lobby") || r.Contains("foyer") || r.Contains("hall") || r.Contains("corridor");
    }

    /// <summary>An open place people cross and sit in: a square, a plaza, a park (not its path or its halt).</summary>
    public static bool IsSquare(string name)
    {
        string r = name.ToLowerInvariant();
        if (r.Contains("halt") || r.Contains("station") || r.Contains("path") || r.Contains("parking")) return false;
        return r.Contains("square") || r.Contains("plaza") || r.EndsWith(" park") || r == "park";
    }

    private static Vector2 NearestClear(Vector2 p, IReadOnlyList<Pavements.Solid> solids)
    {
        if (!Pavements.Blocked(solids, p)) return p;
        foreach (float r in new[] { 0.4f, 0.8f, 1.2f, 1.6f, 2.0f })
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4f;
                var q = p + new Vector2(MathF.Sin(a), MathF.Cos(a)) * r;
                if (!Pavements.Blocked(solids, q)) return q;
            }
        return p;
    }

    private static string NameOf(World world, Entity e)
        => world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) ? world.Get<NameComponent>(e).Name ?? "" : "";
}
