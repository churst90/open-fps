using System;
using System.Numerics;

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
}
