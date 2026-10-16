namespace OpenFPS.Common;

/// <summary>
/// The friction circle: how hard a tyre is being asked to work against what it can give. One number for the
/// wheel model, the driver and the sound (which hears a squeal where the patch begins to slide).
/// </summary>
public static class TyreGrip
{
    /// <summary>The fraction of available grip in use: 0 coasting straight, 1 the limit, above 1
    /// sliding. Longitudinal and lateral combine as a vector (the friction circle), not a sum. A grip in g is
    /// a force over the load the tyre presses with, so it is worth the map's <paramref name="gravity"/>.</summary>
    public static float Demand(float longitudinalMps2, float lateralMps2, float gripG, float gravity = Earth.StandardGravity)
    {
        float capacity = MathF.Max(0.05f, gripG) * gravity;
        float used = MathF.Sqrt(longitudinalMps2 * longitudinalMps2 + lateralMps2 * lateralMps2);
        return used / capacity;
    }

    /// <summary>Where the contact patch begins to slide, as a fraction of capacity. Below this it grips almost
    /// everywhere and the tyre just rolls; a driver who keeps the tyres quiet works them up to here, and the
    /// squeal begins here.</summary>
    public const float PatchSlidesFrom = 0.78f;
}

/// <summary>
/// A tyre's mechanics: the physics' half of a tyre preset, under the same id as the sound's half
/// (TyreProfile). What it pushes on the road with, for the wheel model (<see cref="WheelDynamics"/>):
/// Pacejka's Magic Formula, F = D sin(C atan(B x - E (B x - atan(B x)))), with the peak D = mu Fz from
/// <see cref="PeakGripG"/> and the rest from a published parameter set: the 205/60R15 91V passenger tyre at
/// 2.2 bar in Pacejka, Tire and Vehicle Dynamics (2nd ed. 2006, appendix 3), as reproduced in
/// github.com/jcmadsen/PacTire_Matlab (205_60_R15_91V_2-2bar.tire). Only the first coefficient of each term is
/// used (PCY1, PEY1, PKY1, PKY2, PCX1, PEX1, PKX1, PKX2, PDY2), so camber, the curvature's dependence on load
/// and the curve offsets are left out. The nominal load those coefficients are relative to is taken as the
/// wheel's own static load on its vehicle, which scales the passenger tyre to a truck's or a motorcycle's: no
/// other tyre has published data.
/// </summary>
public sealed record TyreMechanics
{
    /// <summary>
    /// Peak friction this tyre can deliver, in g, before the contact patch slides. A road tyre on dry
    /// asphalt is about 0.95, a performance tyre 1.1, a loaded truck tyre 0.75.
    /// </summary>
    public float PeakGripG { get; init; } = 0.95f;

    /// <summary>Lateral shape factor C (PCY1).</summary>
    public float LateralShape { get; init; } = 1.193f;
    /// <summary>Lateral curvature factor E (PEY1).</summary>
    public float LateralCurvature { get; init; } = -1.003f;
    /// <summary>Cornering stiffness at the nominal load, over that load, before the saturation
    /// (PKY1): K = PKY1 Fz0 sin(2 atan(Fz / (PKY2 Fz0))), per radian.</summary>
    public float CorneringStiffness { get; init; } = 14.95f;
    /// <summary>The load, in nominal loads, at which the cornering stiffness stops rising (PKY2).</summary>
    public float CorneringStiffnessLoad { get; init; } = 2.130f;
    /// <summary>Longitudinal shape factor C (PCX1).</summary>
    public float LongitudinalShape { get; init; } = 1.685f;
    /// <summary>Longitudinal curvature factor E (PEX1).</summary>
    public float LongitudinalCurvature { get; init; } = 0.344f;
    /// <summary>Longitudinal slip stiffness over load, per unit slip (PKX1).</summary>
    public float SlipStiffness { get; init; } = 21.51f;
    /// <summary>The slip stiffness's change with load (PKX2).</summary>
    public float SlipStiffnessLoad { get; init; } = -0.163f;
    /// <summary>How the friction coefficient changes with load, per nominal load: PDY2 over PDY1,
    /// 0.145 / 0.990, falling. Loaded half as hard again it grips about 7 % less per newton, which is
    /// why load transfer in a corner costs the axle grip.</summary>
    public float LoadSensitivity { get; init; } = -0.146f;

    // What decides how a tyre meets a wet road (RoadGripLaw, docs/WET_ROADS.md): its pressure sets the speed it
    // aquaplanes at (Horne's 6.34 sqrt(p kPa) km/h for a flooded smooth tyre; Gallaway's equation for a
    // treaded one), and its tread's grooves carry the water out of the contact patch, so a worn tyre or a
    // slick lets a film lift it sooner.

    /// <summary>Inflation pressure, kPa. The force data above are for the tyre at 2.2 bar.</summary>
    public float InflationKPa { get; init; } = 220f;

    /// <summary>Tread depth, mm: a car tyre is 8 mm new and 1.6 at the legal limit; a part-worn one, 5.</summary>
    public float TreadDepthMm { get; init; } = 5f;

    /// <summary>A decent road tyre on dry asphalt.</summary>
    public static TyreMechanics SportsOnAsphalt => new();

    /// <summary>A sports motorcycle's road tyre: 2.5 bar front and 2.9 rear; 5-6 mm of tread new, 3
    /// part-worn.</summary>
    public static TyreMechanics SportBikeRoad => new() { PeakGripG = 1.1f, InflationKPa = 270f, TreadDepthMm = 3f };

    /// <summary>
    /// A racing slick. Effective grip on a banked oval, not the slick's 1.75 g on the flat: the client measures
    /// lateral acceleration in the world frame, banking included, and cannot see a bank angle. No grooves at
    /// all, run at 1.4-1.8 bar hot: it aquaplanes on any film.
    /// </summary>
    // TODO: send the server's friction demand from the racing line instead (a protocol change).
    public static TyreMechanics RaceSlick => new() { PeakGripG = 3.1f, InflationKPa = 160f, TreadDepthMm = 0f };

    /// <summary>A loaded truck tyre: 7.6 bar (110 psi) and 12 mm of tread part-worn (a drive tyre is 20-26
    /// mm new).</summary>
    public static TyreMechanics TruckOnAsphalt => new() { PeakGripG = 0.75f, InflationKPa = 760f, TreadDepthMm = 12f };

    /// <summary>Every tyre's mechanics by the preset's id, the same ids as the sound's half (TyreProfile.Presets).</summary>
    public static IReadOnlyDictionary<string, Func<TyreMechanics>> Presets { get; } =
        new Dictionary<string, Func<TyreMechanics>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sports_asphalt"] = () => SportsOnAsphalt,
            ["race_slick"] = () => RaceSlick,
            ["truck_asphalt"] = () => TruckOnAsphalt,
        };

    public static TyreMechanics ByName(string key)
        => Presets.TryGetValue(key, out var make) ? make()
         : throw new ArgumentException($"No tyre preset '{key}'. Known: {string.Join(", ", Presets.Keys)}");
}
