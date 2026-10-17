using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Geometry;

namespace OpenFPS.Sound.Core;

/// <summary>One probe round the listener's head: what was hit, how far, and its material. The direction
/// is in head space (+X right, +Y up, +Z forward), not the map's. <see cref="Share"/>: how much of a
/// whole wall the surface is round the point hit (<see cref="BoundaryModel.Share"/>), 1 for a wall.</summary>
public readonly record struct BoundaryProbe(Vector3 HeadDirection, float Distance, string Material, float Share = 1f);

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
        if (IsFloor(probe.HeadDirection)) return false;
        if (speedOfSound <= 1f || sampleRate <= 0) return false;

        var props = AcousticRegistry.GetProperties(string.IsNullOrEmpty(probe.Material) ? "Generic" : probe.Material);

        // A gentle falloff (a wall 1.5 m away is near the direct sound's level, which is why a corridor
        // colours so strongly); the window only retires the tap smoothly at the probe's range.
        float window = Math.Clamp(1f - (probe.Distance / MaxDistance), 0f, 1f);
        float reflectivity = Math.Clamp(1f - props.AbsorptionMid, 0f, 1f);
        float gain = ReflectionGain * reflectivity * window * Math.Clamp(probe.Share, 0f, 1f);
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

    /// <summary>The probe that never makes a tap (the floor's, see <see cref="TryBuildTap"/>), so nothing
    /// need be looked at round it.</summary>
    public static bool IsFloor(Vector3 headDirection) => headDirection.Y < -0.5f;

    /// <summary>The frequency whose first Fresnel zone is the patch of surface a copy needs, Hz.</summary>
    public const float PatchHz = 1000f;

    /// <summary>How far behind the surface's plane a look may land and still be on it, metres: a wall built
    /// of pieces is not flush to the millimetre, and a recess deeper than this is not the same face.</summary>
    public const float SameSurfaceMetres = 0.1f;

    /// <summary>Where each look aims across the surface, in first-Fresnel-zone radii from the point hit.</summary>
    private static readonly float[] LookOffsets = { -1f, -0.5f, 0.5f, 1f };

    /// <summary>Looks per probe: <see cref="LookOffsets"/> along each of two axes across the surface.</summary>
    public const int LooksPerProbe = 8;

    /// <summary>The first Fresnel zone's radius on a surface <paramref name="distance"/> metres off, for a
    /// reflection back to its own point at <see cref="PatchHz"/>: the path through a point that far from the
    /// one hit is half a wavelength longer, so a whole copy needs about that much surface.</summary>
    public static float FresnelRadius(float distance, float speedOfSound)
        => MathF.Sqrt(MathF.Max(0f, speedOfSound / PatchHz * distance * 0.5f));

    /// <summary>
    /// The looks that measure how much surface surrounds a probe's hit: from the ear to points on the plane
    /// the hit lies in (its point and <paramref name="normal"/>), half and a whole Fresnel radius each way on
    /// two axes across it. In the plane, not across the probe's line: the probes turn with the head, and a
    /// flat wall met at an angle must stay whole however the head is turned.
    /// </summary>
    /// <param name="ear">Where the probe was cast from.</param>
    /// <param name="direction">The probe's direction in the world, unit.</param>
    /// <param name="distance">How far along it the surface was hit.</param>
    /// <param name="normal">The surface's normal at the hit, either way round.</param>
    /// <param name="speedOfSound">m/s.</param>
    /// <param name="lookDirections">Unit directions from the ear, <see cref="LooksPerProbe"/> of them.</param>
    /// <param name="lookReach">How far each look must go to reach the plane.</param>
    public static void Looks(Vector3 ear, Vector3 direction, float distance, Vector3 normal, float speedOfSound,
                             Span<Vector3> lookDirections, Span<float> lookReach)
    {
        var hit = ear + direction * distance;
        var n = Facing(direction, normal);
        // Two axes in the plane: level along a wall and up it; on a ceiling, two level ones.
        var u = Vector3.Cross(n, Vector3.UnitY);
        if (u.LengthSquared() < 1e-4f) u = Vector3.Cross(n, Vector3.UnitZ);
        u = Vector3.Normalize(u);
        var v = Vector3.Normalize(Vector3.Cross(n, u));
        float r = FresnelRadius(distance, speedOfSound);
        for (int k = 0; k < LooksPerProbe; k++)
        {
            var axis = k < LookOffsets.Length ? u : v;
            var to = hit + axis * (LookOffsets[k % LookOffsets.Length] * r) - ear;
            float reach = to.Length();
            lookDirections[k] = reach > 1e-6f ? to / reach : direction;
            lookReach[k] = reach;
        }
    }

    /// <summary>
    /// How much of a whole wall a probe's surface is: per axis across it, (1 + the looks that landed on it)
    /// / 5, the two axes multiplied. A look lands when what it meets is no more than
    /// <see cref="SameSurfaceMetres"/> behind the plane; nearer counts, since a corner reflects as well as
    /// the wall does. A wall of any number of pieces is 1; a door's jamb a foot deep a fifth.
    /// </summary>
    /// <param name="lookHits">How far along each of <see cref="Looks"/>' directions something was met (the
    /// cast's reach when nothing was).</param>
    public static float Share(Vector3 ear, Vector3 direction, float distance, Vector3 normal,
                              ReadOnlySpan<Vector3> lookDirections, ReadOnlySpan<float> lookHits)
    {
        var hit = ear + direction * distance;
        var n = Facing(direction, normal);
        int perAxis = LookOffsets.Length;
        float share = 1f;
        for (int axis = 0; axis < 2; axis++)
        {
            int landed = 0;
            for (int i = axis * perAxis; i < (axis + 1) * perAxis; i++)
            {
                // Positive toward the ear: how far in front of the plane the look stopped.
                var met = ear + lookDirections[i] * lookHits[i];
                if (Vector3.Dot(met - hit, n) >= -SameSurfaceMetres) landed++;
            }
            share *= (1f + landed) / (1f + perAxis);
        }
        return share;
    }

    /// <summary>The normal turned back along the probe (a mesh's may face either way); none known, the
    /// surface is taken square to the probe, as every probe was before.</summary>
    private static Vector3 Facing(Vector3 direction, Vector3 normal)
    {
        if (!(normal.LengthSquared() > 1e-8f) || !float.IsFinite(normal.X + normal.Y + normal.Z)) return -direction;
        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, direction) > 0f ? -normal : normal;
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
