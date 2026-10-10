using System.Numerics;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>Audio physics with no FMOD or Steam Audio state: the speed of sound, Doppler, air
/// absorption.</summary>
public static class AudioPhysics
{
    /// <summary>Default speed of sound in air, m/s (~20 °C).</summary>
    public const float SpeedOfSound = 343f;

    /// <summary>The speed of sound in the world's air now, m/s: one figure for Doppler, echo delays, the
    /// ground reflection and flight time (echoes once ran at 343 while Doppler followed the weather).</summary>
    public static float CurrentSpeedOfSound
    {
        get => System.Threading.Volatile.Read(ref _current);
        set => System.Threading.Volatile.Write(ref _current, value);
    }
    private static float _current = SpeedOfSound;

    /// <summary>The world's air temperature now, °C, from the server's weather: what a vehicle's cooling
    /// system runs against.</summary>
    public static float CurrentAirCelsius
    {
        get => System.Threading.Volatile.Read(ref _currentCelsius);
        set => System.Threading.Volatile.Write(ref _currentCelsius, value);
    }
    private static float _currentCelsius = 20f;

    /// <summary>Speed of sound in dry air, m/s: c = 331.3 + 0.606·T(°C). About 4 % across −20 to
    /// +40 °C, shifting every Doppler factor the same way.</summary>
    /// <param name="celsius">Air temperature. Clamped to a range the linear fit still holds over.</param>
    public static float SpeedOfSoundAt(float celsius)
    {
        float t = Math.Clamp(celsius, -60f, 60f);
        return 331.3f + 0.606f * t;
    }

    /// <summary>
    /// Doppler pitch multiplier, f' = f · (c − v_listener·û) / (c − v_source·û), û from source to
    /// listener; above 1 closing. For Steam Audio voices, whose 2D channels bypass FMOD's Doppler; a
    /// native-3D fallback voice keeps FMOD's and must not use this too.
    /// </summary>
    /// <param name="listenerPos">Listener position.</param>
    /// <param name="listenerVel">Listener velocity, m/s.</param>
    /// <param name="sourcePos">Source position.</param>
    /// <param name="sourceVel">Source velocity, m/s.</param>
    /// <param name="scale">Exaggeration factor (1 = physically correct, matches FMOD's dopplerscale).</param>
    /// <param name="speedOfSound">m/s.</param>
    /// <param name="min">Lowest factor returned.</param>
    /// <param name="max">Highest factor returned.</param>
    public static float DopplerFactor(
        Vector3 listenerPos, Vector3 listenerVel,
        Vector3 sourcePos, Vector3 sourceVel,
        float scale = 1f, float speedOfSound = SpeedOfSound,
        float min = 0.5f, float max = 2.0f)
    {
        Vector3 d = listenerPos - sourcePos;
        float dist = d.Length();
        if (dist < 1e-4f || speedOfSound <= 1e-3f) return 1f;
        Vector3 u = d / dist;

        float vL = Vector3.Dot(listenerVel, u);
        float vS = Vector3.Dot(sourceVel, u);

        // Below the speed of sound, so the factor cannot blow up or go negative.
        float lim = speedOfSound * 0.95f;
        vL = Math.Clamp(vL, -lim, lim);
        vS = Math.Clamp(vS, -lim, lim);

        float factor = (speedOfSound - vL) / (speedOfSound - vS);
        if (scale != 1f) factor = 1f + (factor - 1f) * scale;
        return Math.Clamp(factor, min, max);
    }

    /// <summary>
    /// Absorption of sound in air, dB per metre, by ISO 9613-1 (the pure-tone formula): molecular
    /// relaxation of oxygen and nitrogen, above all set by the water in the air. Indoors too. No excess
    /// for obstacles, which are modelled on their own. See docs/CLIENT_NOTES.md, "Air absorption: what
    /// ISO 9613-1 replaced".
    /// </summary>
    /// <param name="frequency">Hz.</param>
    /// <param name="temperatureC">Air temperature, °C.</param>
    /// <param name="relativeHumidity">0..1.</param>
    /// <param name="pressureMillibars">Ambient pressure, mbar (hPa).</param>
    public static float AirAttenuationDbPerMetre(float frequency, float temperatureC, float relativeHumidity,
                                                 float pressureMillibars = 1013.25f)
    {
        double f = Math.Max(1.0, frequency);
        double t = Math.Clamp(temperatureC, -40f, 50f) + 273.15;
        const double t0 = 293.15, t01 = 273.16, pr = 101.325;
        double pa = Math.Clamp(pressureMillibars, 500f, 1100f) / 10.0;          // kPa
        double hr = Math.Clamp(relativeHumidity, 0.001f, 1f) * 100.0;           // percent
        // Molar concentration of water vapour, from the saturation pressure.
        double c = -6.8346 * Math.Pow(t01 / t, 1.261) + 4.6151;
        double h = hr * Math.Pow(10.0, c) * (pr / pa);
        // Relaxation frequencies of oxygen and nitrogen.
        double frO = (pa / pr) * (24.0 + 4.04e4 * h * (0.02 + h) / (0.391 + h));
        double frN = (pa / pr) * Math.Pow(t / t0, -0.5) * (9.0 + 280.0 * h * Math.Exp(-4.170 * (Math.Pow(t / t0, -1.0 / 3.0) - 1.0)));
        double alpha = 8.686 * f * f * (1.84e-11 * (pr / pa) * Math.Sqrt(t / t0)
                     + Math.Pow(t / t0, -2.5) * (0.01275 * Math.Exp(-2239.1 / t) / (frO + f * f / frO)
                                               + 0.1068 * Math.Exp(-3352.0 / t) / (frN + f * f / frN)));
        return (float)alpha;
    }

    /// <summary>
    /// What the air takes off a sound over <paramref name="distance"/> metres, dB (positive), in the
    /// diffraction model's three bands (<see cref="OpenFPS.Common.Diffraction.LowBandHz"/> and its
    /// siblings), so the pipeline agrees on what "the high band" is.
    /// </summary>
    /// <param name="distance">Metres.</param>
    /// <param name="humidity">Relative humidity, 0..1.</param>
    /// <param name="temperatureC">Air temperature, °C.</param>
    /// <param name="airPressureMillibars">Ambient pressure, mbar; unset (0) means standard.</param>
    /// <param name="multiplier">The map's AirAbsorptionMultiplier: 1 is the standard atmosphere; an
    /// unset (zero) field means 1, not "none".</param>
    public static (float Low, float Mid, float High) AirLossDb(float distance, float humidity, float temperatureC,
                                                             float airPressureMillibars, float multiplier = 1f)
    {
        float d = MathF.Max(0f, distance);
        float m = multiplier > 0.01f ? multiplier : 1.0f;
        float p = airPressureMillibars > 1f ? airPressureMillibars : 1013.25f;
        return (AirAttenuationDbPerMetre(OpenFPS.Common.Diffraction.LowBandHz, temperatureC, humidity, p) * d * m,
                AirAttenuationDbPerMetre(OpenFPS.Common.Diffraction.MidBandHz, temperatureC, humidity, p) * d * m,
                AirAttenuationDbPerMetre(OpenFPS.Common.Diffraction.HighBandHz, temperatureC, humidity, p) * d * m);
    }
}
