using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>One probe round the listener's head: what was hit, how far, and its material. The direction
/// is in head space (+X right, +Y up, +Z forward), not the map's.</summary>
public readonly record struct BoundaryProbe(Vector3 HeadDirection, float Distance, string Material);

/// <summary>One near reflection for the mixer: its arrival at each ear, how loud and how dull. Two
/// delays because a surface to one side reaches the near ear first, which is most of what puts a wall
/// on your left rather than just "nearby".</summary>
public readonly record struct BoundaryTap(
    float DelayLSeconds,
    float DelayRSeconds,
    float GainL,
    float GainR,
    float LowpassAlpha);

/// <summary>
/// The reflection off a surface near the head: a copy delayed 2d/c, whose comb (first notch c/4d, near
/// 170 Hz at half a metre) slides with the distance and tells a player how far the wall is. See
/// docs/CLIENT_NOTES.md, "The near-boundary reflection".
/// </summary>
public static class BoundaryModel
{
    /// <summary>Past this, metres, a reflection is late and weak enough to belong to the reverb.</summary>
    public const float MaxDistance = 3.0f;

    /// <summary>The reflected copy against the direct signal, before material and distance.</summary>
    public const float ReflectionGain = 0.6f;

    /// <summary>The largest interaural delay, for a surface straight to one side, seconds (about a
    /// head's width).</summary>
    public const float MaxInterauralDelay = 0.00065f;

    /// <summary>Cutoff of a fully absorbent-in-the-highs surface's reflection, Hz.</summary>
    private const float DullestCutoffHz = 900f;

    /// <summary>Cutoff of a perfectly bright surface's reflection, Hz: effectively open.</summary>
    private const float BrightestCutoffHz = 18000f;

    /// <summary>The reflection for one probe; false when the surface is too far (or is the floor) and
    /// the tap stays silent.</summary>
    /// <param name="probe">What the head-relative ray hit.</param>
    /// <param name="speedOfSound">m/s, from the world's air temperature.</param>
    /// <param name="sampleRate">Mixer sample rate, for the damping coefficient.</param>
    /// <param name="tap">The reflection, when true.</param>
    public static bool TryBuildTap(BoundaryProbe probe, float speedOfSound, int sampleRate, out BoundaryTap tap)
    {
        tap = default;
        if (!(probe.Distance >= 0f) || probe.Distance >= MaxDistance) return false;

        // Not the floor: the image-source pass mirrors each source through it at its own delay. Stamped
        // from the head, it was a copy of every sound 10 ms late at a fifth of its level everywhere
        // ("outside ... it still sounds like I hear reflections from my footsteps").
        if (probe.HeadDirection.Y < -0.5f) return false;
        if (speedOfSound <= 1f || sampleRate <= 0) return false;

        var props = AcousticRegistry.GetProperties(string.IsNullOrEmpty(probe.Material) ? "Generic" : probe.Material);

        // A gentle falloff (a wall 1.5 m away is near the direct sound's level, which is why a corridor
        // colours so strongly); the window only retires the tap smoothly at the probe's range.
        float window = Math.Clamp(1f - (probe.Distance / MaxDistance), 0f, 1f);
        float reflectivity = Math.Clamp(1f - props.AbsorptionMid, 0f, 1f);
        float gain = ReflectionGain * reflectivity * window;
        if (gain <= 0.0005f) return false;

        float delay = 2f * probe.Distance / speedOfSound;

        float lateral = Math.Clamp(probe.HeadDirection.X, -1f, 1f);
        float rightLead = Math.Max(0f, lateral) * MaxInterauralDelay;
        float leftLead = Math.Max(0f, -lateral) * MaxInterauralDelay;
        float delayL = delay + rightLead;
        float delayR = delay + leftLead;

        // Constant power, so turning past a wall does not change how loud the room is.
        float angle = (lateral + 1f) * (MathF.PI / 4f);
        float gainL = gain * MathF.Cos(angle) * MathF.Sqrt(2f);
        float gainR = gain * MathF.Sin(angle) * MathF.Sqrt(2f);

        // Carpet returns a dull thump, concrete the whole spectrum: worth more than the level.
        float brightness = Math.Clamp(1f - props.AbsorptionHigh, 0f, 1f);
        float cutoff = MathHelper.Lerp(DullestCutoffHz, BrightestCutoffHz, brightness);
        tap = new BoundaryTap(delayL, delayR, gainL, gainR, OnePoleAlpha(cutoff, sampleRate));
        return true;
    }

    /// <summary>Per-sample coefficient of a one-pole low-pass at the given cutoff: y += alpha·(x − y).</summary>
    public static float OnePoleAlpha(float cutoffHz, int sampleRate)
    {
        if (sampleRate <= 0) return 1f;
        float nyquist = sampleRate * 0.5f;
        if (cutoffHz >= nyquist) return 1f;
        float x = MathF.Exp(-2f * MathF.PI * cutoffHz / sampleRate);
        return Math.Clamp(1f - x, 0f, 1f);
    }

    /// <summary>The first comb notch a surface at this distance makes, Hz. For the tests, not the mixer.</summary>
    public static float FirstNotchHz(float distance, float speedOfSound = AudioPhysics.SpeedOfSound)
    {
        if (distance <= 0f) return float.PositiveInfinity;
        return speedOfSound / (4f * distance);
    }

    /// <summary>The six directions probed round the head, in head space, so the picture turns with the
    /// player.</summary>
    public static readonly Vector3[] ProbeDirections =
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };
}
