namespace OpenFPS.Common;

/// <summary>
/// The air, from its temperature and pressure: its density (what a bullet's and a car's drag push through,
/// and what a vibrating surface pushes against as it radiates) and its speed of sound. One law for each
/// (docs/PHYSICS_BOUNDARY.md 16.6), here with the materials so the acoustics, the physics and the sound
/// all read it.
/// </summary>
public readonly record struct Air(float TemperatureC, float PressureMb)
{
    /// <summary>The ICAO standard day at sea level, 15 °C and 1013.25 mb: what published tables assume
    /// and what a rifle is zeroed in. 1.225 kg/m³.</summary>
    public static readonly Air Standard = new(15f, 1013.25f);

    /// <summary>
    /// The air the heard laws are written in: 20 °C at sea level, where sound goes the 343 m/s of
    /// <c>AcousticConstants.SpeedOfSound</c>. 1.204 kg/m³. The radiation and the loads of the sounds' own
    /// mechanisms (doors, walls, glass, pipes, drops, the hob) read its density; they wrote 1.2 and 1.21.
    /// Their renders are stored and cached, so they do not follow the weather.
    /// </summary>
    public static readonly Air Reference = new(20f, 1013.25f);

    /// <summary>Dry air, kg/m³: the ideal gas, R = 287.05 J/(kg K). Humidity moves it by under one per cent
    /// and is left out.</summary>
    public float Density => PressureMb * 100f / (287.05f * (TemperatureC + 273.15f));

    /// <summary>m/s, an ideal gas: 331.3 at 0 °C, as the square root of the absolute temperature. The
    /// one law for the speed of sound (AudioPhysics.SpeedOfSoundAt reads it).</summary>
    public float SpeedOfSound => 331.3f * MathF.Sqrt(1f + TemperatureC / 273.15f);
}
