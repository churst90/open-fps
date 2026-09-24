using System;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// A vehicle standing between you and a sound.
///
/// The acoustic scene is built once, from what does not move, so a bus parked across the line from
/// you to a car was never in it: the car behind was heard as though the bus were glass. This puts the
/// moving bodies back as what they are acoustically — a BARRIER, a box the sound has to bend over or
/// round — using the textbook result for one (Maekawa 1968): the extra distance the sound must travel
/// to get round the easiest edge, in wavelengths, sets how much is lost. So it is frequency-dependent
/// by construction. A bus takes most of the top off a car behind it and very little of the bottom, a
/// low rumble walks straight round a hatchback, and nothing here knows what either of them is.
///
/// Worked out every frame, for every voiced source, against every body big enough to matter, because
/// both ends and the barrier all move. It is a handful of box tests per source.
/// </summary>
public static class VehicleShadow
{
    /// <summary>Bands the mixer's three-band EQ is centred on, Hz.</summary>
    private const float LowHz = 150f, MidHz = 1000f, HighHz = 4000f;

    /// <summary>
    /// The most a vehicle takes off, dB. A thin screen of infinite extent reaches 20-25; a body a few
    /// metres long with a gap under it and sound reaching round both ends does not, and field
    /// measurements of a lorry between a road and a microphone come out around ten to fifteen.
    /// </summary>
    private const float MaxLossDb = 15f;

    /// <summary>Anything smaller than this is not worth asking about — a person, a mower.</summary>
    private const float MinVolumeCubicMetres = 2.5f;

    /// <summary>Ground clearance: the body starts this far above where it rests.</summary>
    private const float UnderbodyMetres = 0.2f;

    /// <summary>
    /// Takes whatever the vehicles between <paramref name="listener"/> and <paramref name="source"/>
    /// cost off the path's three bands. <paramref name="sourceId"/> and <paramref name="ridingId"/>
    /// are never their own barrier. Returns the worst loss applied, dB, for diagnostics.
    /// </summary>
    public static float Apply(ref AcousticPathData path, WorldSnapshot world, int sourceId,
                              Vector3 source, Vector3 listener, int ridingId)
    {
        float low = 0f, mid = 0f, high = 0f;
        foreach (var body in world.DynamicEntities)
        {
            if (body.Id == sourceId || body.Id == ridingId) continue;
            var def = body.Definition;
            if (!def.Collider.IsSolid) continue;
            var size = def.Collider.Size;
            if (size.X * size.Y * size.Z < MinVolumeCubicMetres) continue;
            float detour = Detour(body.Transform.Position, body.Transform.Rotation, size, listener, source);
            if (detour <= 0f) continue;
            // Barriers do not add like that in the field, but the second one in a line of traffic
            // does take something more; the worst one plus a little of the rest is honest enough.
            low = Combine(low, Loss(detour, LowHz));
            mid = Combine(mid, Loss(detour, MidHz));
            high = Combine(high, Loss(detour, HighHz));
        }
        if (high <= 0f) return 0f;
        path.EqLow *= DbToGain(-low);
        path.EqMid *= DbToGain(-mid);
        path.EqHigh *= DbToGain(-high);
        return high;
    }

    /// <summary>Maekawa: 10 log10(3 + 20 N) for a Fresnel number N = 2 delta / lambda, capped.</summary>
    internal static float Loss(float detourMetres, float hz)
    {
        float n = 2f * detourMetres * hz / 343f;
        return MathF.Min(MaxLossDb, 10f * MathF.Log10(3f + 20f * n) - 4.77f);   // 0 dB at grazing
    }

    private static float Combine(float worst, float next)
        => MathF.Max(worst, next) + 0.25f * MathF.Min(worst, next);

    private static float DbToGain(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>
    /// How much further sound must go to get round a box that stands across the straight line, over
    /// its roof or round either end, whichever is least. Zero if the line misses it.
    /// </summary>
    internal static float Detour(Vector3 restsAt, Quaternion rotation, Vector3 size, Vector3 a, Vector3 b)
    {
        var inv = Quaternion.Inverse(rotation);
        var half = new Vector3(size.X * 0.5f, (size.Y - UnderbodyMetres) * 0.5f, size.Z * 0.5f);
        var centre = restsAt + Vector3.Transform(new Vector3(0f, UnderbodyMetres + half.Y, 0f), rotation);
        var la = Vector3.Transform(a - centre, inv);
        var lb = Vector3.Transform(b - centre, inv);
        if (!Clip(la, lb, half, out float t0, out float t1)) return 0f;
        // Neither end may be inside it: a source in a car is that car's business, and the listener's
        // own seat is excluded by id.
        if (t0 <= 1e-4f || t1 >= 1f - 1e-4f) return 0f;

        var entry = Vector3.Lerp(la, lb, t0);
        var exit = Vector3.Lerp(la, lb, t1);
        float direct = Vector3.Distance(la, lb);

        float Via(Vector3 p, Vector3 q) => Vector3.Distance(la, p) + Vector3.Distance(p, q) + Vector3.Distance(q, lb) - direct;

        var over = Via(new Vector3(entry.X, half.Y, entry.Z), new Vector3(exit.X, half.Y, exit.Z));
        // Round an end: along the box's length, off whichever end is nearer the crossing point.
        float endZ = (entry.Z + exit.Z) >= 0f ? half.Z : -half.Z;
        var aroundEnd = Via(new Vector3(entry.X, entry.Y, endZ), new Vector3(exit.X, exit.Y, endZ));
        // ...or its side, for a line running along it.
        float sideX = (entry.X + exit.X) >= 0f ? half.X : -half.X;
        var aroundSide = Via(new Vector3(sideX, entry.Y, entry.Z), new Vector3(sideX, exit.Y, exit.Z));
        return MathF.Max(0f, MathF.Min(over, MathF.Min(aroundEnd, aroundSide)));
    }

    private static bool Clip(Vector3 a, Vector3 b, Vector3 half, out float t0, out float t1)
    {
        var d = b - a;
        t0 = 0f; t1 = 1f;
        for (int i = 0; i < 3; i++)
        {
            float ai = i == 0 ? a.X : i == 1 ? a.Y : a.Z;
            float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float hi = i == 0 ? half.X : i == 1 ? half.Y : half.Z;
            if (MathF.Abs(di) < 1e-6f) { if (ai < -hi || ai > hi) return false; continue; }
            float ta = (-hi - ai) / di, tb = (hi - ai) / di;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return true;
    }
}
