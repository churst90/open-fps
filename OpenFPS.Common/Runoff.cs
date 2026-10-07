using System.Threading;

namespace OpenFPS.Common;

/// <summary>
/// The rain that has landed and is on its way somewhere: what fills a gutter, a drain and a downpipe,
/// and what keeps them running after the rain stops (docs/RUNNING_WATER.md, "Run-off").
///
/// A catchment (a roof, a stretch of road) turns rain into flow through a store: water wets the
/// surface, runs across it, collects at its low edge and leaves. The simplest store that behaves like
/// one is the LINEAR RESERVOIR of hydrology, outflow proportional to what is held (Nash 1957; the
/// unit hydrograph of a small urban catchment is close to its exponential): its outflow O follows the
/// rain i through dO/dt = (i − O) / τ. So a gutter takes a few τ to come up to the rain after a shower
/// starts, and after it stops runs on, falling by e every τ: a downpipe dribbling for minutes, the
/// gutter trickling long after the rain has gone quiet.
///
/// τ is the catchment's own: about a minute for a house roof and its gutter, several for a street,
/// longer for a creek's whole valley (which is why a creek's base flow is declared, not run off).
/// The field keeps one reservoir per rung of a ladder of time constants, all fed the same rain, and a
/// catchment reads between the two rungs either side of its own, so every source on the map agrees on
/// what the rain has done and a voice made in the middle of a downpour starts already running.
///
/// Fed by the client once a frame from the world's rain (ClientAudioSystem); read from the render
/// threads. Snow lands and stays: it is not run-off (snowmelt is not modelled).
/// </summary>
public static class Runoff
{
    /// <summary>The ladder of time constants, s: half a minute to an hour.</summary>
    public static readonly float[] Rungs = { 30f, 60f, 120f, 240f, 480f, 960f, 1920f, 3840f };

    private static readonly float[] _held = new float[Rungs.Length];
    private static float _rain;
    private static double _last = double.NaN;

    /// <summary>For the lab: the reservoirs hold where <see cref="Settle"/> put them, whatever the world's
    /// rain, so a drain can be heard running with no rain falling on the listener.</summary>
    public static volatile bool Held;

    /// <summary>The rain landing now, mm/h of water.</summary>
    public static float RainMmPerHour => Volatile.Read(ref _rain);

    /// <summary>
    /// Advances every reservoir to <paramref name="now"/> (s, any monotonic clock) with this rain. The
    /// first call settles them at the rain's own rate, as if it had been raining like this for an hour:
    /// a player who arrives in a downpour finds the gutters already running.
    /// </summary>
    public static void Update(float rainMmPerHour, double now)
    {
        if (Held) return;
        float i = float.IsFinite(rainMmPerHour) ? MathF.Max(0f, rainMmPerHour) : 0f;
        Volatile.Write(ref _rain, i);
        if (double.IsNaN(_last)) { Settle(i); _last = now; return; }
        float dt = (float)Math.Clamp(now - _last, 0.0, 5.0);
        _last = now;
        if (dt <= 0f) return;
        for (int k = 0; k < Rungs.Length; k++)
        {
            float held = Volatile.Read(ref _held[k]);
            held += (i - held) * (1f - MathF.Exp(-dt / Rungs[k]));
            Volatile.Write(ref _held[k], held);
        }
    }

    /// <summary>Every reservoir at the rain's own rate: a steady state, for the lab and the tests.</summary>
    public static void Settle(float rainMmPerHour)
    {
        float i = MathF.Max(0f, rainMmPerHour);
        Volatile.Write(ref _rain, i);
        for (int k = 0; k < Rungs.Length; k++) Volatile.Write(ref _held[k], i);
    }

    /// <summary>Forgets the history (a new map, a reconnect): the next update settles again.</summary>
    public static void Reset()
    {
        _last = double.NaN;
        Settle(0f);
    }

    /// <summary>
    /// What a catchment of time constant <paramref name="seconds"/> is passing now, as mm/h over its
    /// area: the rain itself in a steady shower, less while it fills, more than the rain (for a while)
    /// after it eases. Interpolated in log τ between the rungs either side.
    /// </summary>
    public static float Through(float seconds)
    {
        if (!(seconds > Rungs[0])) return Volatile.Read(ref _held[0]);
        for (int k = 1; k < Rungs.Length; k++)
        {
            if (seconds > Rungs[k]) continue;
            float t = MathF.Log(seconds / Rungs[k - 1]) / MathF.Log(Rungs[k] / Rungs[k - 1]);
            float a = Volatile.Read(ref _held[k - 1]), b = Volatile.Read(ref _held[k]);
            return a + (b - a) * t;
        }
        return Volatile.Read(ref _held[^1]);
    }

    /// <summary>The flow off a catchment, L/s: the rational method's Q = C i A, with i through the
    /// catchment's reservoir. 1 mm/h on a square metre is 1/3600 L/s.</summary>
    public static float FlowLitresPerSecond(float areaSquareMetres, float runoffCoefficient, float seconds)
        => MathF.Max(0f, areaSquareMetres) * Math.Clamp(runoffCoefficient, 0f, 1f) * Through(seconds) / 3600f;
}
