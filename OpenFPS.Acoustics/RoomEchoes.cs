using System.Numerics;

namespace OpenFPS.Common;

// A one-off sound in a room is not one copy (the traced tail took 0.85-0.95 of its energy in a capture). What
// places a room round you is its first reflections, so a one-off sound's early reflections are voices of their
// own at their images, through the HRTF, and the traced stage plays only the late tail (TracedReverbDsp,
// LateTailIr). The floor under the source is left out: the voice carries its own ground reflection.

/// <summary>
/// One placed reflection of a one-off sound in a room: the arrival, and what it returns with the trim
/// (EarlyReflections.PlacedCopyGain times /copies). <paramref name="InVoice"/>: the floor under the source, which
/// the voice carries itself (GroundReflection) and nothing places.
/// </summary>
public readonly record struct RoomEcho(EarlyReflections.Arrival Arrival, float Returned, bool InVoice)
{
    public float Scattering => Math.Clamp(Arrival.Scattering, 0f, 1f);
    /// <summary>The clean copy: the mirror share of what the surfaces return.</summary>
    public float MirrorGain => Returned * RoomEchoes.MirrorShare(Scattering, Arrival.Order);
    /// <summary>A first-order wall's scattered share, placed beside the copy as its wash: zero for a wall that
    /// scatters next to nothing, and for higher orders (their scatter is the tail).</summary>
    public float WashGain => Arrival.Order == 1 && Scattering > 0.05f ? Returned * MathF.Sqrt(Scattering) : 0f;
}

/// <summary>Which reflections a room places for a one-off sound (the host's player queues them; the listener's
/// trace leaves their early energy to them, EarlyCopies).</summary>
public static class RoomEchoes
{
    /// <summary>How long the placed reflections run before the tail takes over, seconds. The traced tail fades
    /// in from 50 to 100 ms after the sound (LateTailIr); the two overlap a little.</summary>
    public const float WindowSeconds = 0.08f;

    /// <summary>At most this many placed reflections per sound: the first order of a box room is six and the
    /// loudest second orders follow.</summary>
    public const int MaxEchoes = 12;

    /// <summary>
    /// Second-order clean copies per sound, beyond its first order; the rest are the traced tail. Twelve clean
    /// copies of a dry clap were twelve separate clicks: the ear hears copies of an impulse as echoes from a few
    /// milliseconds on, where real reflections fuse.
    /// </summary>
    public const int MaxSecondOrderCopies = 4;

    /// <summary>
    /// What a surface returns as a clean copy, of what it returns at all: the mirror share, sqrt(1 - s) of the
    /// pressure per bounce. The scattered sqrt(s) is not a copy; it is the wash, and for a first-order wall it is
    /// placed beside the mirror.
    /// </summary>
    public static float MirrorShare(float scattering, int order)
        => MathF.Pow(MathF.Sqrt(1f - Math.Clamp(scattering, 0f, 1f)), Math.Max(1, order));

    /// <summary>
    /// Which of <paramref name="found"/> (a search from <paramref name="src"/> to <paramref name="listener"/> out
    /// to <see cref="WindowSeconds"/>) a room places, loudest first: every first-order surface in the window, at
    /// most <see cref="MaxSecondOrderCopies"/> second orders, at most <see cref="MaxEchoes"/> copies in all, and
    /// the floor under the source marked as the voice's.
    ///
    /// One rule for the copies a sound gets (the player's early echoes, <paramref name="audible"/>: a copy under
    /// ImageSource.MinGain is not placed and does not count against the cap) and for the early energy the
    /// listener's trace leaves to them (EarlyCopies, not audible: the trace stands for a sound at the listener,
    /// and every surface in the window counts). <paramref name="found"/> is sorted.
    /// </summary>
    public static void Plan(List<EarlyReflections.Arrival> found, Vector3 src, Vector3 listener,
                            float direct, float reference, float trim, bool audible, List<RoomEcho> into)
    {
        into.Clear();
        // Loudest first: in surface order, with more in the window than voices (twenty-two in flat
        // 01F, twelve voices), which walls answered depended on their order in the map.
        found.Sort(static (a, b) => (b.GainMid, a.SurfaceId, a.Order, a.PathLength).CompareTo((a.GainMid, b.SurfaceId, b.Order, b.PathLength)));
        int added = 0, secondOrder = 0;
        foreach (var a in found)
        {
            if (a.ExtraDelaySeconds > WindowSeconds) continue;
            // The ground under the source: already inside the voice (GroundReflection).
            if (a.Order == 1 && a.HitPoint.Y < MathF.Min(src.Y, listener.Y) - 0.2f)
            {
                into.Add(new RoomEcho(a, 0f, InVoice: true));
                continue;
            }
            if (a.Order >= 2 && ++secondOrder > MaxSecondOrderCopies) continue;
            float returned = audible ? EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, direct, reference) * trim : trim;
            var echo = new RoomEcho(a, returned, InVoice: false);
            into.Add(echo);
            // A copy too quiet to place is not one of the twelve (its wash may still play).
            if (audible && echo.MirrorGain < ImageSource.MinGain) continue;
            if (++added >= MaxEchoes) break;
        }
    }
}
