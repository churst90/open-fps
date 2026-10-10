using System.Threading;

namespace OpenFPS.Common;

/// <summary>
/// The rain that has landed and is on its way to a gutter, a drain or a downpipe, and keeps them running
/// after it stops (docs/RUNNING_WATER.md, "Run-off"). Each catchment is a linear reservoir (Nash 1957; a
/// small urban catchment's unit hydrograph is close to its exponential): dO/dt = (i − O) / τ, τ about a
/// minute for a roof, several for a street. One reservoir per rung of a ladder of τ, a catchment reading
/// between the rungs either side, so every source agrees and a voice made mid-downpour starts running.
/// Fed by the client once a frame (ClientAudioSystem), read from the render threads. Snow is not run-off.
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
