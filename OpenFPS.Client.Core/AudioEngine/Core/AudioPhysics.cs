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

    /// <summary>
    /// How much of a source's high frequency the air has taken by the time it arrives, 0 to
    /// <see cref="AcousticConstants.AirAbsorptionMaxMuffle"/>.
    ///
    /// This is why a shot two streets away sounds like a distant thump and the same shot at twenty
    /// metres sounds like a crack: air is a low-pass filter with distance, and it eats the top end
    /// first. Without it, distance only changes loudness, and a quiet gunshot with all its highs
    /// intact reads as a small gunshot nearby rather than a big one far away — which is the single
    /// most misleading thing an audio-first game can do.
    ///
    /// Humidity, temperature and pressure all move it, which is why they are arguments rather than
    /// constants: absorption is strongest in cold dry air. Indoors the path is short and mostly
    /// wall rather than air, so the effective distance is doubled.
    ///
    /// Lifted verbatim out of <c>SpatialAcoustics</c> so that the Steam Audio path can use the same
    /// law. It could not before: the simulator's path set AirAbsorption to a hard zero with a comment
    /// calling it "a later phenomena pass", so switching the simulator on silently switched air
    /// absorption off for every source in the game.
    /// </summary>
    public static float AirAbsorptionFor(float distance, float humidity, float temperatureC,
                                         float airPressureMillibars, float multiplier, bool listenerIndoors)
    {
        // An absent or zero multiplier means "no scaling", not "scale by a tenth".
        float m = multiplier > 0.01f ? multiplier : 1.0f;
        float pressureNorm = Math.Clamp(airPressureMillibars / 1013.25f, 0.5f, 2.0f);
        float effective = MathF.Max(50.0f,
            (AcousticConstants.AirAbsorptionReferenceDist - (humidity * 100f)
             + (MathF.Max(0f, 20f - temperatureC) * 2f)) / m * pressureNorm);
        if (listenerIndoors) effective *= 2.0f;
        return Math.Clamp((distance - AcousticConstants.AirAbsorptionMinDist) / effective,
                          0.0f, AcousticConstants.AirAbsorptionMaxMuffle);
    }

    // ── Atmospheric absorption, per band ────────────────────────────────────────────────────────
    //
    // Air is a low-pass filter and the coefficients are published: these are the ISO 9613-1 figures
    // at roughly 20 C and 50% humidity, in decibels per metre. They rise steeply with frequency,
    // which is the whole point — at two hundred metres a gunshot has lost twenty decibels at 8 kHz
    // and almost nothing at 60. That is why distance turns a crack into a thump.
    public const float AirDbPerMetreLow = 0.0004f;    // ~125 Hz
    public const float AirDbPerMetreMid = 0.0037f;    // ~1 kHz
    public const float AirDbPerMetreHigh = 0.0328f;   // ~4 kHz

    /// <summary>
    /// Extra high-frequency loss per 100 m in a built-up area, beyond what the air alone takes.
    ///
    /// Air absorption on its own does not account for how dull a distant shot in a city really is,
    /// and the reason is that there is rarely a clean direct path. Between you and a shooter two
    /// blocks away are parked cars, kerbs, street furniture and the ground itself, and what reaches
    /// you has been scattered and diffracted rather than travelling straight. That excess attenuation
    /// is strongly frequency-dependent — long wavelengths bend round obstacles and short ones do not —
    /// so it lands almost entirely on the top end, which is exactly where it is wanted.
    ///
    /// This is the knob for "distant shots are too bright". It does nothing inside
    /// <see cref="UrbanExcessOnsetMetres"/>, so close shots stay sharp.
    /// </summary>
    public const float UrbanExcessHighDbPer100M = 9.0f;
    public const float UrbanExcessMidDbPer100M = 3.0f;
    public const float UrbanExcessOnsetMetres = 40f;

    /// <summary>
    /// Per-band linear gains for a sound that has travelled <paramref name="distance"/> metres.
    /// Low, mid and high, each 0-1, ready for <c>SpatialEmitter.EqLow/EqMid/EqHigh</c>.
    ///
    /// Cold dry air absorbs most, which is why temperature and humidity are arguments. Indoors the
    /// path is short and mostly wall rather than air, so the atmospheric part is halved and the urban
    /// term does not apply at all.
    /// </summary>
    public static (float Low, float Mid, float High) AtmosphericBands(
        float distance, float humidity, float temperatureC, float airPressureMillibars,
        float multiplier, bool listenerIndoors)
    {
        float d = MathF.Max(0f, distance);
        float m = multiplier > 0.01f ? multiplier : 1.0f;

        // Dry cold air absorbs more; the scaling here is a linear approximation over the range a
        // playable map will see rather than the full ISO relaxation model.
        float dryness = Math.Clamp(1.6f - humidity, 0.6f, 1.6f);
        float cold = Math.Clamp(1f + MathF.Max(0f, 20f - temperatureC) * 0.012f, 1f, 1.5f);
        float pressureNorm = Math.Clamp(airPressureMillibars / 1013.25f, 0.5f, 2.0f);
        float scale = dryness * cold * m / pressureNorm * (listenerIndoors ? 0.5f : 1f);

        float lowDb = AirDbPerMetreLow * d * scale;
        float midDb = AirDbPerMetreMid * d * scale;
        float highDb = AirDbPerMetreHigh * d * scale;

        if (!listenerIndoors)
        {
            float beyond = MathF.Max(0f, d - UrbanExcessOnsetMetres) / 100f;
            midDb += UrbanExcessMidDbPer100M * beyond;
            highDb += UrbanExcessHighDbPer100M * beyond;
        }

        static float ToGain(float db) => MathF.Max(0.02f, MathF.Pow(10f, -db / 20f));
        return (ToGain(lowDb), ToGain(midDb), ToGain(highDb));
    }
}
