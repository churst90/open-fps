using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Where the painted lines are, from the driver's seat.
///
/// A sighted driver keeps a lane by watching two things: the line beside them, which says how far
/// off centre they are, and the dashes going past, which say how fast. Both are GEOMETRY — a width, a
/// lane count, a paint pattern — and neither needs a picture to be useful. This works them out from
/// the carriageway the car is on, so the client can make them audible: the dashes as ticks from the
/// side they are on, and the two edges that matter as tones that rise as you close on them.
///
/// The road is the ASPHALT BOX the car is standing on, and its long axis is the way the road runs.
/// That is what a city map is made of today. When roads become data (docs/ROADS_BEACONS_AND_SCALE.md)
/// the lanes will come from the road's own declaration instead, and everything downstream of
/// <see cref="Locate"/> stays as it is.
/// </summary>
public static class LaneGuide
{
    /// <summary>A lane, metres. Three is a city lane; a highway's is three and a half and a
    /// residential street's often less, which the lane count absorbs by rounding.</summary>
    public const float LaneWidth = 3.0f;

    /// <summary>
    /// One dash and one gap, metres: ten feet of paint and thirty of road, which is the US standard
    /// for a broken lane line. At fifty kilometres an hour that is a tick a little over once a
    /// second, and at a hundred, twice — the rate IS the speedometer.
    /// </summary>
    public const float DashPeriod = 12.19f;

    public enum Line { Kerb, Centre, Lane }

    /// <summary>A carriageway: an asphalt box, and which way it runs.</summary>
    public readonly record struct Road(Vector3 Centre, Vector3 Size, Quaternion Rotation)
    {
        /// <summary>The road's own axes in the world: along its length, and across it to the right.</summary>
        public (Vector3 Along, Vector3 Across, float Length, float Width) Axes()
        {
            var x = Vector3.Transform(Vector3.UnitX, Rotation);
            var z = Vector3.Transform(Vector3.UnitZ, Rotation);
            x.Y = 0f; z.Y = 0f;
            x = Vector3.Normalize(x); z = Vector3.Normalize(z);
            return Size.Z >= Size.X ? (z, x, Size.Z, Size.X) : (x, -z, Size.X, Size.Z);
        }
    }

    /// <summary>The car on the road: how far across it, how far along, and which way it faces.</summary>
    public readonly record struct Position(float Across, float Along, float Width, int Lanes, bool TwoWay, float Facing);

    /// <summary>Where a car is on a road, and whether it is on it at all.</summary>
    public static bool Locate(Road road, Vector3 at, Vector3 forward, out Position p)
    {
        var (along, across, length, width) = road.Axes();
        var d = at - road.Centre;
        float u = Vector3.Dot(d, across), s = Vector3.Dot(d, along);
        p = default;
        if (MathF.Abs(u) > width * 0.5f || MathF.Abs(s) > length * 0.5f) return false;
        int lanes = Math.Max(1, (int)MathF.Round(width / LaneWidth));
        // A road wide enough for two lanes carries traffic both ways; one lane is a lane.
        bool twoWay = lanes >= 2;
        float facing = Vector3.Dot(new Vector3(forward.X, 0f, forward.Z), along) >= 0f ? 1f : -1f;
        p = new Position(u, s, width, lanes, twoWay, facing);
        return true;
    }

    /// <summary>Every painted line across the road, as (offset across it, what it is).</summary>
    public static List<(float At, Line Kind)> Lines(Position p)
    {
        var lines = new List<(float, Line)> { (-p.Width * 0.5f, Line.Kerb), (p.Width * 0.5f, Line.Kerb) };
        float lane = p.Width / p.Lanes;
        if (p.TwoWay)
        {
            lines.Add((0f, Line.Centre));
            int perSide = p.Lanes / 2;
            for (int k = 1; k < perSide; k++) { lines.Add((k * lane, Line.Lane)); lines.Add((-k * lane, Line.Lane)); }
        }
        else
            for (int k = 1; k < p.Lanes; k++) lines.Add((-p.Width * 0.5f + k * lane, Line.Lane));
        return lines;
    }

    /// <summary>
    /// The nearest line on each side of the car, as the DRIVER has them: the gap from the car's side
    /// to the line (negative once the car is over it), what it is, and how far to the driver's left
    /// or right it is from the middle of the car.
    /// </summary>
    public static ((float Gap, Line Kind, float Offset) Left, (float Gap, Line Kind, float Offset) Right)
        Sides(Position p, float carHalfWidth)
    {
        (float, Line, float) left = (float.MaxValue, Line.Kerb, 0f), right = (float.MaxValue, Line.Kerb, 0f);
        float nearestLeft = float.MaxValue, nearestRight = float.MaxValue;
        foreach (var (at, kind) in Lines(p))
        {
            // Across the road to the driver's right is +x; facing the other way, the road's right is
            // the driver's left.
            float x = (at - p.Across) * p.Facing;
            float gap = MathF.Abs(x) - carHalfWidth;
            if (x < 0f && -x < nearestLeft) { nearestLeft = -x; left = (gap, kind, x); }
            else if (x >= 0f && x < nearestRight) { nearestRight = x; right = (gap, kind, x); }
        }
        return (left, right);
    }

    /// <summary>How many dash starts the car passed going from one position along the road to the
    /// next, whichever way it is going.</summary>
    public static int DashesPassed(float alongFrom, float alongTo)
        => Math.Abs((int)MathF.Floor(alongTo / DashPeriod) - (int)MathF.Floor(alongFrom / DashPeriod));
}
