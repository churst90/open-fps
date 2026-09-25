using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// Pure audio-physics helpers (no FMOD/Steam Audio state) so they can be unit-tested in isolation.
/// </summary>
public static class AudioPhysics
{
    /// <summary>Default speed of sound in air, m/s (~20 °C).</summary>
    public const float SpeedOfSound = 343f;

    /// <summary>
    /// Speed of sound in dry air at a given temperature, m/s: c = 331.3 + 0.606·T(°C).
    ///
    /// This is what makes the simulated temperature audible rather than decorative. It is a ~4%
    /// swing across a playable range (−20 °C to +40 °C), which is small on its own but shifts every
    /// Doppler factor in the world in the same direction — a siren on a winter night is measurably
    /// flatter than the same siren in high summer.
    /// </summary>
    /// <param name="celsius">Air temperature. Clamped to a range the linear fit still holds over.</param>
    public static float SpeedOfSoundAt(float celsius)
    {
        float t = Math.Clamp(celsius, -60f, 60f);
        return 331.3f + 0.606f * t;
    }

    /// <summary>
    /// Doppler pitch multiplier for a source heard by a listener, from their positions and velocities.
    /// &gt;1 = pitched up (closing), &lt;1 = pitched down (receding). Steam Audio voices are rendered on a 2D
    /// FMOD channel (so FMOD's own Doppler is bypassed); the provider applies this factor to the channel
    /// pitch instead. Native-3D fallback voices keep FMOD's Doppler and must NOT use this.
    /// f' = f · (c − v_listener·û) / (c − v_source·û), with û the unit vector source→listener.
    /// </summary>
    /// <param name="scale">Exaggeration factor (1 = physically correct, matches FMOD's dopplerscale).</param>
    public static float DopplerFactor(
        Vector3 listenerPos, Vector3 listenerVel,
        Vector3 sourcePos, Vector3 sourceVel,
        float scale = 1f, float speedOfSound = SpeedOfSound,
        float min = 0.5f, float max = 2.0f)
    {
        Vector3 d = listenerPos - sourcePos; // source -> listener
        float dist = d.Length();
        if (dist < 1e-4f || speedOfSound <= 1e-3f) return 1f;
        Vector3 u = d / dist;

        float vL = Vector3.Dot(listenerVel, u);
        float vS = Vector3.Dot(sourceVel, u);

        // Clamp closing speeds below the speed of sound so the factor can't blow up / go negative.
        float lim = speedOfSound * 0.95f;
        vL = Math.Clamp(vL, -lim, lim);
        vS = Math.Clamp(vS, -lim, lim);

        float factor = (speedOfSound - vL) / (speedOfSound - vS);
        if (scale != 1f) factor = 1f + (factor - 1f) * scale;
        return Math.Clamp(factor, min, max);
    }

    // ── Atmospheric absorption: ISO 9613-1 ──────────────────────────────────────────────────────
    //
    // Air takes the top end off a sound by molecular relaxation of oxygen and nitrogen, and how much
    // depends on the frequency, the temperature and — above all — the water in the air. ISO 9613-1
    // gives it in closed form, and that is what this is: no fixed coefficients and no fudge.
    //
    // It replaced a straight-line "muffle" that reached its limit at about 135 m and took 32 dB off
    // the high band and 16 off the mid there, where the standard says about 14 and under 1 — six
    // times too much — so a hot rod a block away arrived as a dull rumble with its crackle gone. It
    // also replaced a fixed-coefficient version that added an "urban excess" of up to 9 dB per
    // 100 m for obstacles the game already models on their own (walls, vehicles, diffraction), and
    // that halved the loss indoors. Air is air indoors too.

    /// <summary>
    /// Absorption of sound in air, dB per metre, by ISO 9613-1 (the pure-tone formula).
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
        // Molar concentration of water vapour, from the saturation pressure over ice/water.
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
    /// What the air takes off a sound over <paramref name="distance"/> metres, in dB (positive), in the
    /// game's three bands — the same band centres the diffraction model uses
    /// (<see cref="OpenFPS.Common.Diffraction.LowBandHz"/> and its siblings), so the whole pipeline
    /// agrees on what "the high band" is.
    /// </summary>
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
