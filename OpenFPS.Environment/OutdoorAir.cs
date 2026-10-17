using System.Numerics;
using OpenFPS.Materials;

namespace OpenFPS.Environment;

/// <summary>
/// The air outdoors at a place now, as one value (docs/ENGINE_PACKAGES.md, 3.2: one atmosphere): its
/// temperature, humidity and pressure, and the wind through it with its gustiness. The world's weather
/// carries it (WorldEnvironmentComponent.Outdoors, which the broadcast sends field by field); the laws read
/// what they need of it: a bullet's and a car's drag the gas (its Air), thunder all of it.
///
/// The gas law itself is the materials' <see cref="OpenFPS.Materials.Air"/> (density, speed of sound), below the
/// physics; an audio world's live temperature and speed of sound are its Atmosphere, which its host sets from
/// this one's temperature.
/// </summary>
/// <param name="TemperatureC">°C.</param>
/// <param name="Humidity">Relative humidity, 0..1.</param>
/// <param name="PressureMb">Millibars; sea level is 1013.25.</param>
/// <param name="Wind">The mean wind, m/s, the way it blows to (x east, z north).</param>
/// <param name="Gustiness">The server's 0..1 figure: with the wind it sets how turbulent the air is.</param>
public readonly record struct OutdoorAir(float TemperatureC, float Humidity, float PressureMb, Vector3 Wind, float Gustiness)
{
    /// <summary>The gas: its density and its speed of sound, by the one law.</summary>
    public Air Air => new(TemperatureC, PressureMb);
}
