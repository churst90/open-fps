using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// What the client says about stairs: "Stairs up, 10 steps, to floor 3" when you reach the foot of a
/// flight facing up it, and whether you are on a flight at all.
///
/// Everything it knows comes from the map. Each end of a flight is a stair marker the map put on the
/// landing (prefabs/stair_marker.json): standing <see cref="MarkerHeightMetres"/> above the floor a
/// step back from the end riser, turned to face the way you walk to take the flight from there, and
/// named with the line to say. Nothing here works out where a flight is from the geometry.
///
/// <b>Said once per approach.</b> Reaching a marker facing along it says its line; standing there,
/// turning about, stepping back and forth on the landing says nothing more. You have to walk away,
/// past <see cref="LeaveMetres"/>, before coming back says it again. Facing matters because a landing
/// is where two flights meet: arriving at the top of one you are standing on the marker that says
/// "Stairs down", facing away from it, and that is not news; turn round and it is.
///
/// <b>On a flight.</b> A storey's zone stops at its ceiling and the next one starts at its floor, so
/// at eye height the name changes about halfway up the stairs — "stairwell, floor 3" while you are
/// still climbing to it, and on the way down "floor 2" a couple of treads from the top. The zone
/// announcer waits while <see cref="OnFlight"/> is true and says where you are when you step off.
/// </summary>
public sealed class StairCues
{
    /// <summary>How far above its floor the map stands a stair marker, metres: handrail height, which
    /// is also where its beacon is heard from. tools/gen_city.py puts them there.</summary>
    public const float MarkerHeightMetres = 1.0f;

    /// <summary>How near a marker, across the floor, is "at the stairs", metres.</summary>
    public const float ReachMetres = 1.0f;

    /// <summary>How far from a marker you must go before coming back to it is a new approach, metres.
    /// Wider than <see cref="ReachMetres"/>, so swaying on the edge of it does not say it twice.</summary>
    public const float LeaveMetres = 1.5f;

    /// <summary>How square to the flight you must be facing: within about fifty degrees of along it.</summary>
    public const float FacingCos = 0.64f;

    /// <summary>How far above or below a marker's floor your feet may be and still be on that floor.
    /// A tile on a slab is three centimetres; one riser is thirty.</summary>
    private const float SameFloorMetres = 0.2f;

    /// <summary>Markers you are at now, and which of those have been said this visit.</summary>
    private readonly HashSet<int> _at = new(), _said = new();
    private readonly List<int> _scratch = new();

    /// <summary>True while your feet are on the treads of a flight: between its two ends, and between
    /// the two floors it joins.</summary>
    public bool OnFlight { get; private set; }

    /// <summary>
    /// One update. Returns the line to say, or null. <paramref name="feet"/> is the body's position
    /// (its feet); <paramref name="facing"/> its rotation, of which only the heading counts.
    /// </summary>
    public string? Update(WorldSnapshot world, Vector3 feet, Quaternion facing)
    {
        var forward = Flat(Vector3.Transform(Vector3.UnitZ, facing));
        string? say = null;
        int sayId = -1;
        float sayDistance = float.MaxValue;
        bool onFlight = false;

        _scratch.Clear();
        foreach (int id in _at) _scratch.Add(id);

        foreach (int id in world.MarkerEntityIds)
        {
            if (!TryMarker(world, id, out var at, out var along, out string line)) continue;
            float floor = at.Y - MarkerHeightMetres;
            float across = Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(at.X, at.Z));
            bool sameFloor = MathF.Abs(feet.Y - floor) <= SameFloorMetres;

            if (sameFloor && across <= ReachMetres)
            {
                if (_at.Add(id)) _said.Remove(id);
                if (!_said.Contains(id) && Vector3.Dot(forward, along) >= FacingCos && across < sayDistance)
                {
                    say = line;
                    sayId = id;
                    sayDistance = across;
                }
            }
            else if (!sameFloor || across > LeaveMetres)
            {
                _at.Remove(id);
                _said.Remove(id);
            }

            if (!onFlight && across <= MaxFlightMetres + LaneHalfWidth && OnTreads(world, id, at, along, feet)) onFlight = true;
        }

        // A marker that has gone from the world (a map change) is not one you are still at.
        foreach (int id in _scratch)
            if (!world.Entities.ContainsKey(id)) { _at.Remove(id); _said.Remove(id); }

        if (sayId >= 0) _said.Add(sayId);

        OnFlight = onFlight;
        return say;
    }

    /// <summary>Forgets every approach, for a teleport or a new map: arriving somewhere is not having
    /// walked away from where you were.</summary>
    public void Reset()
    {
        _at.Clear();
        _said.Clear();
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
    /// Whether the feet are on the flight that starts at this marker: past it along the way it faces,
    /// short of the marker at the other end, within a lane's width of the line between them, and
    /// between the two floors.
    ///
    /// The other end is the marker facing back the opposite way along the same line, on the side of
    /// this one the feet are: above it if they are above its floor, below if below. That side has to be
    /// asked, because stairwells stack. A dog-leg puts every other flight in the same lane, so straight
    /// ahead of the foot of one flight are both its own top, a storey up, and the top of the flight two
    /// below it, a storey down, at the same distance along.
    /// </summary>
    private static bool OnTreads(WorldSnapshot world, int id, Vector3 at, Vector3 along, Vector3 feet)
    {
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
            if (rise < bestRise) { bestRise = rise; ahead = a; otherFloor = o.Y - MarkerHeightMetres; }
        }
        if (float.IsNaN(otherFloor) || t >= ahead) return false;
        float lo = MathF.Min(floor, otherFloor), hi = MathF.Max(floor, otherFloor);
        return feet.Y > lo + 0.1f && feet.Y < hi - 0.1f;
    }

    /// <summary>Half a stair's width and a body's radius: how far to the side of the line between a
    /// flight's markers a body can be and still be on it.</summary>
    private const float LaneHalfWidth = 1.1f;

    /// <summary>The longest flight between two markers, metres along: nothing on a map runs further
    /// without a landing.</summary>
    private const float MaxFlightMetres = 8f;

    private static Vector3 Flat(Vector3 v)
    {
        v.Y = 0f;
        return v.LengthSquared() < 1e-6f ? Vector3.Zero : Vector3.Normalize(v);
    }
}
