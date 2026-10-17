using OpenFPS.Schema;

namespace OpenFPS.Physics;

/// <summary>A manual gearbox: the ratios, and how long the driver's left foot takes.</summary>
public sealed record Gearbox
{
    /// <summary>Ratio per gear, first at index 0.</summary>
    public required float[] Ratios { get; init; }
    [Tunable("", 1.5, 15, "Ratio of the final drive: turns of the gearbox output for one turn of the wheel. It includes any chain or primary reduction.", Label = "final drive ratio", Step = 0.01)]
    public required float FinalDrive { get; init; }
    /// <summary>Rolling radius of the driven wheel, metres.</summary>
    [Tunable("m", 0.15, 0.8, "Rolling radius of the driven wheel.", Label = "wheel radius", Step = 0.005)]
    public required float WheelRadiusMetres { get; init; }

    /// <summary>How long the clutch is down during a shift: the silence in the middle of it, most of
    /// what makes a change sound like a person. A quick change is about a quarter of a second.</summary>
    [Tunable("s", 0.02, 2, "How long the clutch is down during a shift: the gap in the sound. A quick change is about a quarter of a second.", Label = "shift time", Step = 0.01)]
    public float ShiftSeconds { get; init; } = 0.28f;
    /// <summary>RPM the driver shifts up at when accelerating hard.</summary>
    [Tunable("rpm", 500, 20000, "Engine speed the driver changes up at when accelerating hard.", Label = "upshift speed", Step = 50)]
    public float UpshiftRpm { get; init; } = 6100f;
    /// <summary>...and drops to when coasting down.</summary>
    [Tunable("rpm", 300, 15000, "Engine speed the driver changes down at when coasting.", Label = "downshift speed", Step = 50)]
    public float DownshiftRpm { get; init; } = 1500f;
    /// <summary>RPM the driver changes up at on a light throttle. Unset, 85 % of the torque peak: right
    /// for a car, but a litre bike (peak at 11,000, first gear to 150 km/h) never left first in town.</summary>
    public float? CruiseUpshiftRpm { get; init; }
    /// <summary>RPM held while the clutch slips pulling away. Unset, twice idle or 40 % of the torque
    /// peak, whichever is higher.</summary>
    public float? LaunchRpm { get; init; }

    public int TopGear => Ratios.Length;

    /// <summary>Engine RPM for a road speed in a given gear.</summary>
    public float RpmFor(float speedMetresPerSecond, int gear)
    {
        if (gear < 1 || gear > Ratios.Length) return 0f;
        float wheelRevsPerSec = speedMetresPerSecond / (2f * MathF.PI * WheelRadiusMetres);
        return MathF.Max(0f, wheelRevsPerSec * Ratios[gear - 1] * FinalDrive * 60f);
    }

    /// <summary>Road speed at which a gear reaches a given RPM.</summary>
    public float SpeedFor(float rpm, int gear)
    {
        if (gear < 1 || gear > Ratios.Length) return 0f;
        return rpm / 60f * 2f * MathF.PI * WheelRadiusMetres / (Ratios[gear - 1] * FinalDrive);
    }

    /// <summary>A six-speed sports car on 255/40R19s.</summary>
    public static Gearbox SixSpeedSports => new()
    {
        Ratios = new[] { 2.97f, 2.07f, 1.43f, 1.00f, 0.84f, 0.56f },
        FinalDrive = 3.42f,
        WheelRadiusMetres = 0.337f,
        ShiftSeconds = 0.26f,
        UpshiftRpm = 6150f,
        DownshiftRpm = 1450f,
    };
}
