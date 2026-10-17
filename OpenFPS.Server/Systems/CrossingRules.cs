using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Somebody on foot crossing a road. Nothing on the map declares a crossing: where a walker's line
/// passes over a carriageway is one, found at load. With no signals yet, every crossing is uncontrolled:
///
/// - The walker stops at the kerb and waits for a gap in the traffic long enough to walk across, the
///   Highway Capacity Manual's pedestrian critical headway t_c = L / S_p + t_s (StreetLifeData). A
///   vehicle that is stopping for the crossing is not a vehicle in the way.
/// - A driver stops for anybody on the crossing, and for somebody who has stood at the kerb for a while
///   (StreetLifeData.PedestrianAssertSeconds), if it can stop at its ordinary braking. One that cannot
///   stop goes through; the walker's gap check is what keeps that from meeting anyone. One that has
///   started stopping keeps stopping, harder if it must.
/// - A driver waiting at a junction's line stands short of a crossing there, not on it.
///
/// The laws alone: which crossings there are, who is on each and who is coming is the host's
/// (VehicleSystem.Crosswalks), which hands in the distances along each line.
/// </summary>
public static class CrossingRules
{
    /// <summary>Metres a walker stands back from the carriageway's edge while waiting.</summary>
    public const float KerbStandBackMetres = 0.3f;
    /// <summary>Metres a vehicle's nose stays short of a crossing it stops for.</summary>
    public const float StandOffMetres = 1f;
    /// <summary>A walker's line that runs further than this inside a carriageway is walking along the
    /// road, not across it, and is left alone.</summary>
    public const float LongestCrossingMetres = 40f;
    /// <summary>Two walkers' crossings of one road this close along it are one crossing.</summary>
    public const float SameCrossingMetres = 3f;
    /// <summary>How far past each kerb a vehicle's line still counts as crossing a crossing, metres.</summary>
    public const float KerbOverrunMetres = 1.5f;
    /// <summary>How far past where it meant to stand a driver stopping for a crossing may run on its
    /// ordinary braking before braking harder, metres: half the stand-off.</summary>
    public const float StandOverrunMetres = 0.5f;

    public static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>The stretches of the line a to b that lie on a road's carriageway, metres from a.</summary>
    public static List<(float From, float To)> InsideCarriageway(RoadData road, Vector3 a, Vector3 b, float step = 0.25f)
    {
        var o = new List<(float, float)>();
        if (road.Centreline.Count < 2) return o;
        float length = Vector3.Distance(Flat(a), Flat(b));
        float roadLength = RoadNetwork.Length(road.Centreline);
        float half = road.WidthMetres * 0.5f;
        var dir = length > 1e-4f ? (b - a) / length : Vector3.Zero;
        float? start = null;
        int n = (int)(length / step);
        for (int i = 0; i <= n + 1; i++)
        {
            float d = MathF.Min(i * step, length);
            bool inside = false;
            if (i <= n)
            {
                var (along, off) = RoadNetwork.Project(road.Centreline, a + dir * d);
                inside = off < half && along > 0.01f && along < roadLength - 0.01f;
            }
            if (inside && start == null) start = d;
            else if (!inside && start != null) { o.Add((start.Value, d - step)); start = null; }
        }
        return o;
    }

    /// <summary>Where the segment p to q meets a to b in plan, as a fraction of p to q.</summary>
    public static bool Meet(Vector3 p, Vector3 q, Vector3 a, Vector3 b, out float t)
    {
        t = 0f;
        float rx = q.X - p.X, rz = q.Z - p.Z, sx = b.X - a.X, sz = b.Z - a.Z;
        float den = rx * sz - rz * sx;
        if (MathF.Abs(den) < 1e-9f) return false;
        float ax = a.X - p.X, az = a.Z - p.Z;
        t = (ax * sz - az * sx) / den;
        float u = (ax * rz - az * rx) / den;
        return t >= 0f && t < 1f && u >= 0f && u <= 1f;
    }

    /// <summary>How far ahead round its line a vehicle is of a point at <paramref name="at"/>, metres, across
    /// the lap's seam: behind by more than half a lap is ahead.</summary>
    public static float Ahead(float at, float lap, float lineLength)
    {
        float ahead = at - lap;
        if (ahead < -lineLength * 0.5f) ahead += lineLength;
        return ahead;
    }

    /// <summary>The fastest a walker may walk to stand at the kerb <paramref name="left"/> metres on, m/s.</summary>
    public static float KerbApproach(float brake, float left) => MathF.Sqrt(2f * brake * left);

    /// <summary>The gap a walker needs, seconds: the time to walk across and, unless it has stood long enough
    /// to take a risk, the time to start.</summary>
    public static float WalkerNeeds(StreetLifeData life, float walkSeconds, float waited)
        => walkSeconds + (waited > life.PedestrianRiskSeconds ? 0f : life.PedestrianStartUpSeconds);

    /// <summary>How far either side of a crossing's middle a vehicle's body is over it, metres.</summary>
    public static float Reach(float lengthMetres, float halfBand) => 0.5f * lengthMetres + halfBand + 0.5f;

    /// <summary>A driver who has stood for a crossing this long has given its turn; one that has stood at the
    /// kerb this long asserts it.</summary>
    public static bool TurnTaken(StreetLifeData life, float waited)
        => waited > life.PedestrianStartUpSeconds + life.PedestrianAssertSeconds;

    /// <summary>Whether a vehicle coming reaches the strip before somebody who needs this long is across.
    /// Until its body reaches the strip, not its middle: a bus crawling round a corner with its nose at the
    /// strip is there at once (traced 2026-10-02). One standing still is not coming.</summary>
    public static bool ArrivesWithin(float ahead, float reach, float speed, float need)
        => (ahead - reach) / MathF.Max(speed, 1e-3f) < need;

    /// <summary>Whether a driver lets somebody waiting at the kerb across: only before the junction (a driver
    /// stopping inside one for the far kerb locked the whole junction, traced 2026-09-28), and only one that
    /// has not already stood a while.</summary>
    /// <param name="laneLeft">How much of its lane is left, metres, or negative in a junction.</param>
    /// <param name="waited">How long the driver has stood for crossings.</param>
    /// <param name="longestWait">The longest anybody has waited at the crossing's kerbs.</param>
    public static bool LetsAcross(StreetLifeData life, float ahead, float laneLeft, float waited, float longestWait)
        => ahead <= laneLeft && !TurnTaken(life, waited) && longestWait > life.PedestrianAssertSeconds;

    /// <summary>Where a vehicle's middle stands for a crossing <paramref name="ahead"/> metres on, metres.</summary>
    public static float Stand(float ahead, float halfBand, float lengthMetres)
        => ahead - halfBand - StandOffMetres - 0.5f * lengthMetres;

    /// <summary>How hard a driver already stopping for a crossing brakes: as hard as standing no more than a
    /// little past its mark takes, up to an emergency stop, because the walkers stepped out on the strength
    /// of it (traced 2026-10-02).</summary>
    /// <param name="hardest">Its emergency stop (StreetEvents.HardestBrake).</param>
    public static float StoppingBrake(float stand, float speed, float brake, float hardest)
    {
        float room = stand + StandOverrunMetres;
        float need = room > 0.05f ? speed * speed / (2f * room) : float.MaxValue;
        return Math.Clamp(need, brake, MathF.Max(brake, hardest));
    }
}
