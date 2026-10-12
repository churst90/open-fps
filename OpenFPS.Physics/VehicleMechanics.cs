namespace OpenFPS.Common;

/// <summary>
/// A vehicle's mechanics: the physics' half of a vehicle preset, under the same id as the sound's half
/// (VehicleProfile, which holds one: <c>VehicleProfile.Mechanics</c>). What holds it on the road and moves it:
/// its mass, how it meets the air and the road, its size, where its axles are, its running gear, its gearbox and
/// its tyres' and its engine's mechanics. The wheel model (<see cref="WheelDynamics"/>) is made from it.
/// </summary>
public sealed record VehicleMechanics
{
    /// <summary>Its mass, kg.</summary>
    public float MassKg { get; init; } = 1620f;

    /// <summary>Its drag area, m²: the drag coefficient times the frontal area.</summary>
    public float DragArea { get; init; } = 0.62f;

    /// <summary>Its rolling resistance, as a share of its weight.</summary>
    public float RollingResistance { get; init; } = 0.013f;

    /// <summary>Where the front axle is, metres ahead of the body's middle.</summary>
    public float FrontAxleZ { get; init; } = 1.25f;

    /// <summary>Where the rear axle is, metres ahead of the body's middle (negative).</summary>
    public float RearAxleZ { get; init; } = -1.35f;

    /// <summary>The body's outside size, metres: bumper to bumper, mirror-less width, ground to roof.</summary>
    public float LengthMetres { get; init; } = 4.6f;
    public float WidthMetres { get; init; } = 1.9f;
    public float HeightMetres { get; init; } = 1.4f;

    /// <summary>How many tyres are on the road.</summary>
    public int TyreCount { get; init; } = 4;

    /// <summary>The running gear (<see cref="ChassisSpec"/>), or null for <see cref="ChassisSpec.Default"/>,
    /// built from the numbers above.</summary>
    public ChassisSpec? Chassis { get; init; }

    /// <summary>The gearbox. Null only until the vehicle that holds these mechanics is given one.</summary>
    public Gearbox? Gearbox { get; init; }

    /// <summary>The tyres' mechanics.</summary>
    public TyreMechanics Tyres { get; init; } = new();

    /// <summary>The engine's mechanics: where it idles, how far it revs, the torque it pulls with.</summary>
    public EngineMechanics Engine { get; init; } = new();

    /// <summary>The running gear with every axle placed: its own, or the one the other numbers imply.</summary>
    public ChassisSpec Running => (Chassis ?? ChassisSpec.Default(TyreCount, WidthMetres, MassKg)).PlacedOn(FrontAxleZ, RearAxleZ);
}
