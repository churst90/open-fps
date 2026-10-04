using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// What the client says about stairs: "Stairs up, 17 steps, to floor 3" when you reach the foot of a
/// flight facing up it, and whether you are on a flight at all.
///
/// Everything it knows comes from the map. Each end of a flight is a stair marker the map put on the
/// landing (prefabs/stair_marker.json): standing <see cref="MarkerHeightMetres"/> above the floor a
/// step back from the end riser, turned to face the way you walk to take the flight from there, and
/// named with the line to say. Nothing here works out where a flight is from the geometry.
///
/// <b>Said once per arrival, and never about the flight you came by.</b> Reaching a marker facing
/// along it says its line, once. It is not said again while you stay on that landing — standing,
/// turning about, stepping sideways to the flight beside it and back, walking off across the floor
/// and returning — only once you have been somewhere else: another place (zone), another floor, or
/// well away across a big one (<see cref="LeaveMetres"/>). And while your feet are on a flight both
/// its ends go quiet, so stepping off at the top or the bottom says nothing about the flight you have
/// just walked: you know where it goes.
///
/// That replaced a rule of reach and leave radii a metre and a half apart (Cody, 2026-10-04: "I hear
/// indicators repeating several times"). On a landing the top of one flight and the foot of the next
/// stand side by side facing the same way, so a sidestep from one lane to the other left the one and
/// reached the other, and "Stairs up, Stairs down, Stairs up" came out of shuffling on one spot; any
/// two-metre walk away and back said it again, nine times in three minutes; and arriving off a flight
/// walking backwards — facing up it — announced the flight just walked down.
///
/// <b>On a flight.</b> A storey's zone stops at its ceiling and the next one starts at its floor, so
/// at eye height the name changes about halfway up the stairs — "stairwell, floor 3" while you are
/// still climbing to it, and on the way down "floor 2" a few treads from the top. The zone
/// announcer waits while <see cref="OnFlight"/> is true and says where you are when you step off.
///
/// <b>The ends of the stairwell.</b> A landing that only one flight reaches is the bottom or the top
/// of the whole stair (<see cref="IsStairwellEnd"/>); the stairs beacon blips from those and not from
/// every landing in between.
/// </summary>
public sealed class StairCues
{
    /// <summary>How far above its floor the map stands a stair marker, metres: handrail height, which
    /// is also where its beacon is heard from. tools/gen_city.py puts them there.</summary>
    public const float MarkerHeightMetres = 1.0f;

    /// <summary>How near a marker, across the floor, is "at the stairs", metres.</summary>
    public const float ReachMetres = 1.0f;

    /// <summary>How far across its floor you must go from a marker before coming back is a new arrival,
    /// metres, in a place with no zones to say you left. Wider than a stairwell's landing, so walking
    /// about on one is not leaving it; a stairwell in a zone of its own is left by its door first.</summary>
    public const float LeaveMetres = 10f;

    /// <summary>How far above or below a marker's floor you must be to be on another floor, metres:
    /// half a storey. A jump goes under a metre.</summary>
    public const float LeaveRiseMetres = 1.5f;

    /// <summary>How square to the flight you must be facing: within about fifty degrees of along it.</summary>
    public const float FacingCos = 0.64f;

    /// <summary>How near two markers on one floor are one landing, metres: the two flights of a dog-leg
    /// either side of their well are two and a half metres apart.</summary>
    public const float LandingMetres = 4f;

    /// <summary>How far above or below a marker's floor your feet may be and still be on that floor.
    /// A tile on a slab is three centimetres; one riser is eighteen.</summary>
    private const float SameFloorMetres = 0.15f;

    /// <summary>Markers that are quiet — said on this visit, or the ends of a flight you were on — and
    /// the place you were in when they went quiet.</summary>
    private readonly Dictionary<int, int> _quiet = new();
    private readonly HashSet<int> _flightEnds = new();
    private readonly List<int> _scratch = new();

    /// <summary>True while your feet are on the treads of a flight: between its two ends, and between
    /// the two floors it joins.</summary>
    public bool OnFlight { get; private set; }

    /// <summary>
    /// One update. Returns the line to say, or null. <paramref name="feet"/> is the body's position
    /// (its feet); <paramref name="facing"/> its rotation, of which only the heading counts;
    /// <paramref name="place"/> the zone you are in, if the map has zones — leaving it for another is
    /// leaving the landing.
    /// </summary>
    public string? Update(WorldSnapshot world, Vector3 feet, Quaternion facing, int place = int.MinValue)
    {
        var forward = Flat(Vector3.Transform(Vector3.UnitZ, facing));

        // The flight under your feet, if any: both its ends are quiet while you are on it.
        _flightEnds.Clear();
        foreach (int id in world.MarkerEntityIds)
        {
            if (!TryMarker(world, id, out var at, out var along, out _)) continue;
            float across = Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(at.X, at.Z));
            if (across > MaxFlightMetres + LaneHalfWidth) continue;
            if (OnTreads(world, id, at, along, feet, out int other))
            {
                _flightEnds.Add(id);
                _flightEnds.Add(other);
            }
        }
        OnFlight = _flightEnds.Count > 0;

        string? say = null;
        int sayId = -1;
        float sayDistance = float.MaxValue;
        foreach (int id in world.MarkerEntityIds)
        {
            if (!TryMarker(world, id, out var at, out var along, out string line)) continue;
            if (_flightEnds.Contains(id)) { _quiet[id] = place; continue; }

            float floor = at.Y - MarkerHeightMetres;
            float across = Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(at.X, at.Z));
            float above = MathF.Abs(feet.Y - floor);
            if (_quiet.TryGetValue(id, out int quietIn))
            {
                // Been somewhere else since: another zone, another floor, or right away across this one.
                if (quietIn != place || above > LeaveRiseMetres || across > LeaveMetres) _quiet.Remove(id);
                else continue;
            }

            if (above <= SameFloorMetres && across <= ReachMetres && across < sayDistance
                && Vector3.Dot(forward, along) >= FacingCos)
            {
                say = line;
                sayId = id;
                sayDistance = across;
            }
        }

        // A marker that has gone from the world (a map change) is not one you were at.
        _scratch.Clear();
        foreach (int id in _quiet.Keys)
            if (!world.Entities.ContainsKey(id)) _scratch.Add(id);
        foreach (int id in _scratch) _quiet.Remove(id);

        if (sayId >= 0) _quiet[sayId] = place;
        return say;
    }

    /// <summary>Forgets every arrival, for a teleport or a new map: arriving somewhere is not having
    /// walked there.</summary>
    public void Reset()
    {
        _quiet.Clear();
        _flightEnds.Clear();
        OnFlight = false;
    }

    /// <summary>A stair marker: where it stands, the way it faces (level, unit), and what it says.</summary>
    internal static bool TryMarker(WorldSnapshot world, int id, out Vector3 at, out Vector3 along, out string line)
    {
        at = default; along = default; line = "";
        if (!world.Entities.TryGetValue(id, out var e)
            || !string.Equals(e.Definition.Identity.BeaconCategory, Beacons.Stairs, StringComparison.OrdinalIgnoreCase))
            return false;
        at = e.Transform.Position;
        along = Flat(Vector3.Transform(Vector3.UnitZ, e.Transform.Rotation));
        line = e.Definition.Identity.Name ?? "";
        return along != Vector3.Zero && line.Length > 0;
    }

    /// <summary>
    /// Whether a stair marker is one end of the whole stair: the only flight's end on its landing. The
    /// foot of the ground floor's flight and the top of the flight onto the roof are; every landing in
    /// between has the top of the flight below and the foot of the flight above, side by side.
    /// </summary>
    public static bool IsStairwellEnd(WorldSnapshot world, int id)
    {
        if (!TryMarker(world, id, out var at, out _, out _)) return false;
        foreach (int other in world.MarkerEntityIds)
        {
            if (other == id || !TryMarker(world, other, out var o, out _, out _)) continue;
            if (MathF.Abs(o.Y - at.Y) <= SameFloorMetres
                && Vector2.Distance(new Vector2(o.X, o.Z), new Vector2(at.X, at.Z)) <= LandingMetres)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the feet are on the flight that starts at this marker: past it along the way it faces,
    /// short of the marker at the other end, within a lane's width of the line between them, and
    /// between the two floors. <paramref name="otherEnd"/> is that other marker.
    ///
    /// The other end is the marker facing back the opposite way along the same line, on the side of
    /// this one the feet are: above it if they are above its floor, below if below. That side has to be
    /// asked, because stairwells stack. A dog-leg puts every other flight in the same lane, so straight
    /// ahead of the foot of one flight are both its own top, a storey up, and the top of the flight two
    /// below it, a storey down, at the same distance along.
    /// </summary>
    private static bool OnTreads(WorldSnapshot world, int id, Vector3 at, Vector3 along, Vector3 feet, out int otherEnd)
    {
        otherEnd = -1;
        var from = new Vector2(at.X, at.Z);
        var dir = new Vector2(along.X, along.Z);
        var side = new Vector2(-dir.Y, dir.X);
        var p = new Vector2(feet.X, feet.Z) - from;
        float t = Vector2.Dot(p, dir);
        if (t <= 0f || t >= MaxFlightMetres || MathF.Abs(Vector2.Dot(p, side)) > LaneHalfWidth) return false;

        float floor = at.Y - MarkerHeightMetres;
        if (MathF.Abs(feet.Y - floor) <= 0.1f) return false;
        bool up = feet.Y > floor;
        float bestRise = float.MaxValue, ahead = float.NaN, otherFloor = float.NaN;
        foreach (int other in world.MarkerEntityIds)
        {
            if (other == id || !TryMarker(world, other, out var o, out var oAlong, out _)) continue;
            if (Vector3.Dot(oAlong, along) > -0.9f) continue;
            float rise = (o.Y - at.Y) * (up ? 1f : -1f);
            if (rise < 0.5f) continue;
            var q = new Vector2(o.X, o.Z) - from;
            float a = Vector2.Dot(q, dir);
            if (a <= 0.5f || a > MaxFlightMetres || MathF.Abs(Vector2.Dot(q, side)) > 0.5f) continue;
            if (rise < bestRise) { bestRise = rise; ahead = a; otherFloor = o.Y - MarkerHeightMetres; otherEnd = other; }
        }
        if (float.IsNaN(otherFloor) || t >= ahead) return false;
        float lo = MathF.Min(floor, otherFloor), hi = MathF.Max(floor, otherFloor);
        return feet.Y > lo + 0.1f && feet.Y < hi - 0.1f;
    }

    /// <summary>Half a stair's width and a body's radius: how far to the side of the line between a
    /// flight's markers a body can be and still be on it.</summary>
    private const float LaneHalfWidth = 1.1f;

    /// <summary>The longest flight between two markers, metres along: nothing on a map runs further
    /// without a landing. The city's ground floor flight is nineteen 28 cm treads and a marker half a
    /// metre off each end, 6.3 m.</summary>
    private const float MaxFlightMetres = 8f;

    private static Vector3 Flat(Vector3 v)
    {
        v.Y = 0f;
        return v.LengthSquared() < 1e-6f ? Vector3.Zero : Vector3.Normalize(v);
    }
}
