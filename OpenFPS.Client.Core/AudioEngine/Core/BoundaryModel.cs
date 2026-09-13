using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>One probe of the space immediately around the listener's head: what was hit, how far away
/// it is, and what it is made of. The direction is in HEAD space (+X right, +Y up, +Z forward), so it
/// says where the surface is relative to the way the player is facing, not where it is on the map.</summary>
public readonly record struct BoundaryProbe(Vector3 HeadDirection, float Distance, string Material);

/// <summary>One early reflection, ready for the mixer: when its copy of the signal arrives at each ear,
/// how loud, and how dull. Two delays rather than one because a surface off to one side reflects to the
/// near ear FIRST, and that arrival-time difference is most of what makes a wall feel like it is on your
/// left rather than simply "nearby".</summary>
public readonly record struct BoundaryTap(
    float DelayLSeconds,
    float DelayRSeconds,
    float GainL,
    float GainR,
    float LowpassAlpha);

/// <summary>
/// Turns "there is a wall half a metre to my left" into the reflection that actually makes it audible.
///
/// A nearby surface does not simply make things louder: it returns a delayed copy of everything you can
/// hear, and direct-plus-delayed is a comb filter. The extra path is twice the distance to the surface,
/// so the delay is 2d/c and the notches sit at odd multiples of c/4d — half a metre away that is a
/// first notch near 170 Hz and peaks every 340 Hz, which is the "boxy" colouration you hear walking
/// along a corridor wall. Get closer and the whole pattern slides UP in pitch; step away and it slides
/// down and fades. That sliding is the cue: it is what tells a player how far they are from a wall
/// without touching it, and it is why this cannot be a fixed-delay effect.
///
/// The previous implementation was a fixed-ish 0.1–1.2 ms FMOD echo with 45% feedback, which is neither
/// the right delay (2d/c at 1.5 m is 8.7 ms, seven times longer) nor the right topology — feedback makes
/// a resonator that rings on one pitch instead of a single reflection that tracks the geometry — and it
/// collapsed every direction into one scalar, so a ceiling and a wall behind you sounded identical.
/// </summary>
public static class BoundaryModel
{
    /// <summary>Beyond this, a surface is no longer "near" and contributes nothing. Past a couple of
    /// metres the reflection is late and weak enough to belong to the reverb, not to the head.</summary>
    public const float MaxDistance = 3.0f;

    /// <summary>Overall level of the reflected copy relative to the direct signal, before material and
    /// distance. Below 1 because a real surface is neither perfectly flat nor perfectly reflective.</summary>
    public const float ReflectionGain = 0.6f;

    /// <summary>Largest arrival-time difference between the ears for a surface directly to one side,
    /// in seconds. Roughly the real head-width ITD.</summary>
    public const float MaxInterauralDelay = 0.00065f;

    /// <summary>Cutoff of a fully absorbent-in-the-highs surface's reflection, Hz.</summary>
    private const float DullestCutoffHz = 900f;

    /// <summary>Cutoff of a perfectly bright surface's reflection, Hz — effectively open.</summary>
    private const float BrightestCutoffHz = 18000f;

    /// <summary>
    /// Builds the reflection for one probe. Returns false when the surface is too far to matter, so the
    /// caller can leave that tap silent rather than paying for it.
    /// </summary>
    /// <param name="probe">What the head-relative ray hit.</param>
    /// <param name="speedOfSound">m/s — from the world's air temperature, so a cold night moves every
    /// comb notch up together, exactly as it moves every Doppler shift.</param>
    /// <param name="sampleRate">Mixer sample rate, for the damping coefficient.</param>
    public static bool TryBuildTap(BoundaryProbe probe, float speedOfSound, int sampleRate, out BoundaryTap tap)
    {
        tap = default;
        if (!(probe.Distance >= 0f) || probe.Distance >= MaxDistance) return false;
        if (speedOfSound <= 1f || sampleRate <= 0) return false;

        var props = AcousticRegistry.GetProperties(string.IsNullOrEmpty(probe.Material) ? "Generic" : probe.Material);

        // Energy comes back in proportion to what is not absorbed, and weakens as the surface recedes.
        // The falloff is deliberately gentle — the reflection off a wall a metre and a half away is not
        // far off the direct sound in level, which is exactly why a corridor colours so strongly — and
        // the window's only job is to retire the tap smoothly at the edge of probe range.
        float window = Math.Clamp(1f - (probe.Distance / MaxDistance), 0f, 1f);
        float reflectivity = Math.Clamp(1f - props.AbsorptionMid, 0f, 1f);
        float gain = ReflectionGain * reflectivity * window;
        if (gain <= 0.0005f) return false;

        // The round trip: out to the surface and back.
        float delay = 2f * probe.Distance / speedOfSound;

        // Lateralization. The near ear hears the reflection first and slightly louder.
        float lateral = Math.Clamp(probe.HeadDirection.X, -1f, 1f);
        float rightLead = Math.Max(0f, lateral) * MaxInterauralDelay;  // surface on the right
        float leftLead = Math.Max(0f, -lateral) * MaxInterauralDelay;  // surface on the left
        float delayL = delay + rightLead;
        float delayR = delay + leftLead;

        // Constant-power pan so turning your head past a wall does not change how loud the room is.
        float angle = (lateral + 1f) * (MathF.PI / 4f);
        float gainL = gain * MathF.Cos(angle) * MathF.Sqrt(2f);
        float gainR = gain * MathF.Sin(angle) * MathF.Sqrt(2f);

        // Material timbre: carpet returns a dull thump, concrete returns the whole spectrum. This is the
        // difference between a corridor and a stairwell, and it is worth more than the level is.
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

    /// <summary>The frequency of the first cancellation notch a surface at this distance produces, Hz.
    /// Not used by the mixer — it is what the effect is FOR, and what its tests assert against.</summary>
    public static float FirstNotchHz(float distance, float speedOfSound = AudioPhysics.SpeedOfSound)
    {
        if (distance <= 0f) return float.PositiveInfinity;
        return speedOfSound / (4f * distance);
    }

    /// <summary>The six directions probed around the head, in HEAD space. Right, left, up, down,
    /// forward, back — so the picture turns with the player, which is what makes it navigable.</summary>
    public static readonly Vector3[] ProbeDirections =
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };
}
