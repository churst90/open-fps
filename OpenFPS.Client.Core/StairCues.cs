using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// What the client says about stairs ("Stairs up, 17 steps, to floor 3" at the foot of a flight, facing
/// up it) and whether you are on a flight, all from the map's stair markers (prefabs/stair_marker.json).
/// A line is said once per arrival and never about the flight you came by: a marker stays quiet until
/// you have been to another zone, another floor or <see cref="LeaveMetres"/> away, and both ends of
/// the flight under your feet are quiet (Cody, 2026-10-04: "I hear indicators repeating several
/// times"). The cue speaks for a flight's and a landing's zone names (<see cref="CoversZone"/>), and
/// the zone announcer holds a room's name while <see cref="OnFlight"/>.
/// See docs/CLIENT_NOTES.md, "Stair cues".
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

    /// <summary>How far above or below your ear a floor's stairs beacon is on another floor, metres:
    /// half a storey. Its marker stands a metre over its floor and your ear about 1.6.</summary>
    public const float OtherFloorMetres = 1.5f;

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

    /// <summary>True while your feet are on a flight whose line was said as you came to it: you were
    /// told where these stairs go, and telling you their name as you step on is saying it twice.</summary>
    public bool FlightAnnounced => OnFlight && _flightEnds.Contains(_lastSaid);

    /// <summary>How long before you entered a zone a cue still speaks for it, seconds. The cue is said
    /// as you reach a marker, half a metre short of the flight; the landing round the marker can be
    /// entered a stride before, and the flight a stride after.</summary>
    public const double CueLeadSeconds = 1.0;

    /// <summary>
    /// Whether the stair cue has already said what a zone's name would: you are on a flight it told
    /// you about, or it spoke since a moment before you entered the zone. <paramref name="lastCueAt"/>
    /// is when the last cue was said, <paramref name="enteredAt"/> when you crossed into the zone, on
    /// one clock. Only for named parts of rooms, a flight or a landing: a ROOM is always said.
    /// </summary>
    public static bool CoversZone(bool flightAnnounced, double lastCueAt, double enteredAt)
        => flightAnnounced || lastCueAt >= enteredAt - CueLeadSeconds;

    /// <summary>The marker whose line was said last, until you step off a flight.</summary>
    private int _lastSaid = -1;

    /// <summary>
    /// One update: the line to say, or null. Only the heading of <paramref name="facing"/> counts;
    /// <paramref name="place"/> is the zone you are in, and leaving it is leaving the landing.
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
        bool wasOnFlight = OnFlight;
        OnFlight = _flightEnds.Count > 0;
        // Off a flight, what you were told on the way to it is spent: coming back down it later,
        // without a cue, is news.
        if (wasOnFlight && !OnFlight) _lastSaid = -1;

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

        if (sayId >= 0) { _quiet[sayId] = place; _lastSaid = sayId; }
        return say;
    }

    /// <summary>Forgets every arrival, for a teleport or a new map: arriving somewhere is not having
    /// walked there.</summary>
    public void Reset()
    {
        _quiet.Clear();
        _flightEnds.Clear();
        OnFlight = false;
        _lastSaid = -1;
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

    /// <summary>Whether a stair marker is one end of the whole stair: the only marker on its landing.
    /// Every landing in between has the top of one flight and the foot of the next side by side.</summary>
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
    /// The markers the stairs beacon blips from, one a floor (Cody, 2026-10-04): the foot of every
    /// flight up, and the top of the whole stair (on the city's towers, onto the roof). Which end a
    /// marker is comes from pairing the markers from the bottom up, since in a dog-leg position and
    /// facing do not say it. See docs/CLIENT_NOTES.md, "Stair cues".
    /// </summary>
    public static HashSet<int> FloorBeacons(WorldSnapshot world)
    {
        var ms = new List<(int Id, Vector3 At, Vector3 Along)>();
        foreach (int id in world.MarkerEntityIds)
            if (TryMarker(world, id, out var at, out var along, out _)) ms.Add((id, at, along));
        ms.Sort((a, b) => a.At.Y != b.At.Y ? a.At.Y.CompareTo(b.At.Y) : a.Id.CompareTo(b.Id));

        var tops = new HashSet<int>();
        var beacons = new HashSet<int>();
        foreach (var m in ms)
        {
            if (tops.Contains(m.Id)) continue;                       // reached from below: a top
            var from = new Vector2(m.At.X, m.At.Z);
            var dir = new Vector2(m.Along.X, m.Along.Z);
            var side = new Vector2(-dir.Y, dir.X);
            int top = -1;
            float bestRise = float.MaxValue;
            foreach (var o in ms)
            {
                if (o.Id == m.Id || tops.Contains(o.Id) || Vector3.Dot(o.Along, m.Along) > -0.9f) continue;
                float rise = o.At.Y - m.At.Y;
                if (rise < 0.5f || rise >= bestRise) continue;
                var q = new Vector2(o.At.X, o.At.Z) - from;
                float a = Vector2.Dot(q, dir);
                if (a <= 0.5f || a > MaxFlightMetres || MathF.Abs(Vector2.Dot(q, side)) > 0.5f) continue;
                bestRise = rise;
                top = o.Id;
            }
            if (top >= 0) tops.Add(top);
            // The foot of a flight up; or, with nothing above facing back down to it and nothing
            // reaching it from below, a marker on its own, which is still a way onto some stairs.
            beacons.Add(m.Id);
        }
        // ...and the top of the whole stair: a top with no foot beside it on its landing.
        foreach (int id in tops)
            if (IsStairwellEnd(world, id)) beacons.Add(id);
        return beacons;
    }

    /// <summary>
    /// Whether the feet are on the flight that starts at this marker: past it, short of
    /// <paramref name="otherEnd"/>, within a lane of the line between them and between the two floors.
    /// The other end is the marker facing back along the same line on the side the feet are (above or
    /// below): stairwells stack, and straight ahead of a dog-leg's foot are both its own top a storey up
    /// and another flight's top a storey down.
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
