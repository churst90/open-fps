using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Keeping a gap to the vehicle in front, by the Intelligent Driver Model (Treiber, Hennecke and
/// Helbing 2000), whose braking term is
///
///   a_follow = a (1 - (s* / s)^2),   s* = s0 + max(0, v T + v (v - v_lead) / (2 sqrt(a b)))
///
/// with s the gap to the lead vehicle's tail, T the time headway and s0 the gap left standing (both
/// from the map's StreetLife), a this vehicle's acceleration and b its comfortable braking. The free
/// road term of the IDM is the speed the vehicle already chases (the line's limit, the next stop), so
/// following only ever takes speed away: the vehicle does whichever is slower.
///
/// The laws alone: who is in front and how far is the host's (VehicleSystem.Following), which hands them in.
/// </summary>
public static class CarFollowing
{
    /// <summary>
    /// The fastest a vehicle may go this tick for the one in front, m/s.
    /// </summary>
    /// <param name="rainHeadway">The share of time headway the weather adds (<see cref="RainCaution"/>).</param>
    /// <param name="wasSpeed">Its speed at the start of the tick, m/s.</param>
    /// <param name="accel">Its acceleration, m/s².</param>
    /// <param name="brake">Its comfortable braking, m/s².</param>
    /// <param name="hardest">The hardest it may brake (StreetEvents.HardestBrake): no harder than a staged
    /// hard stop, which leaves the wheels turning.</param>
    /// <param name="gap">To the lead's tail, metres.</param>
    /// <param name="leadSpeed">The lead's speed, m/s.</param>
    /// <param name="leadAccel">The lead's acceleration, m/s², or zero where it is held to its line.</param>
    public static float Allowed(StreetLifeData life, float rainHeadway, float wasSpeed, float accel, float brake, float hardest,
                                float gap, float leadSpeed, float leadAccel, float dt)
    {
        float a = MathF.Max(0.1f, accel), b = MathF.Max(0.1f, brake);
        float s0 = life.FollowMinGapMetres, headway = life.FollowHeadwaySeconds * (1f + rainHeadway);
        float s = MathF.Max(0.1f, gap);
        float sStar = s0 + MathF.Max(0f, wasSpeed * headway + wasSpeed * (wasSpeed - leadSpeed) / (2f * MathF.Sqrt(a * b)));
        float idm = a * (1f - (sStar / s) * (sStar / s));
        float acc = Acc(idm, wasSpeed, leadSpeed, leadAccel, s, a, b);
        acc = MathF.Max(acc, -MathF.Max(b, hardest));
        return MathF.Max(0f, wasSpeed + acc * dt);
    }

    /// <summary>
    /// How much drivers are holding back for the weather: the share of speed they give up and the share of
    /// time headway they add. Wet is the asphalt's texture full (RoadWater), heavy the rain against
    /// Rainfall.HeavyRate; each blends from the wet figures to the heavy ones.
    /// </summary>
    /// <param name="water">The map's water on its roads, or null for none.</param>
    public static (float Speed, float Headway) RainCaution(RoadWater? water, StreetLifeData life)
    {
        if (water == null) return (0f, 0f);
        byte asphalt = RoadSurfaces.IndexOf(RoadData.DefaultSurface);
        float wet = Math.Clamp(water.TextureMm(asphalt) / MathF.Max(0.05f, RoadGripLaw.HoldsMm(asphalt)), 0f, 1f);
        float heavy = Math.Clamp(water.RainMmPerHour / Rainfall.HeavyRate, 0f, 1f);
        float speed = wet * life.WetSpeedReduction + heavy * MathF.Max(0f, life.HeavyRainSpeedReduction - life.WetSpeedReduction);
        float headway = wet * life.WetHeadwayIncrease + heavy * MathF.Max(0f, life.HeavyRainHeadwayIncrease - life.WetHeadwayIncrease);
        return (Math.Clamp(speed, 0f, 0.5f), MathF.Max(0f, headway));
    }

    /// <summary>
    /// The ACC model of Kesting, Treiber and Helbing ("Enhanced intelligent driver model to access the
    /// impact of driving strategies on traffic capacity", Phil. Trans. R. Soc. A 368, 2010; Treiber and
    /// Kesting, Traffic Flow Dynamics, 2013, section 11.3.6): the IDM's braking with its overreaction
    /// taken out. Below its desired gap the IDM brakes as if the lead might stop dead at any moment,
    /// so creeping up a queue at walking pace it stamps on the brakes. The constant-acceleration
    /// heuristic says what the situation really asks, assuming the lead keeps its acceleration:
    ///
    ///   a_CAH = v^2 a~ / (v_l^2 - 2 s a~)            if v_l (v - v_l) &lt;= -2 s a~
    ///         = a~ - (v - v_l)^2 Theta(v - v_l) / (2 s)   otherwise,        a~ = min(a_l, a)
    ///
    /// and where the IDM asks for more braking than that, the two are blended:
    ///
    ///   a_ACC = (1 - c) a_IDM + c [a_CAH + b tanh((a_IDM - a_CAH) / b)],   c = 0.99
    ///
    /// so the braking stays near the comfortable b unless the situation really is critical.
    /// </summary>
    public static float Acc(float idm, float v, float vLead, float aLead, float s, float a, float b)
    {
        const float Coolness = 0.99f;
        float at = MathF.Min(aLead, a);
        float cah;
        float denominator = vLead * vLead - 2f * s * at;
        if (vLead * (v - vLead) <= -2f * s * at && denominator > 1e-3f) cah = v * v * at / denominator;
        else cah = at - (v > vLead ? (v - vLead) * (v - vLead) : 0f) / (2f * MathF.Max(0.1f, s));
        if (idm >= cah) return idm;
        return (1f - Coolness) * idm + Coolness * (cah + b * MathF.Tanh((idm - cah) / b));
    }
}
