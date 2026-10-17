using System.Numerics;

namespace OpenFPS.Sound;

/// <summary>
/// Where a thing's sound comes out, one answer for every consumer: the voice, the occlusion probe and
/// map validation. Derived separately, the voice went to the tailpipe while the probe stayed at the
/// car's origin on the road, asking what could be heard where no sound was coming from. The host hands
/// it the thing's pose and its emitter's offset (open-fps: EntityEmission, from an entity, which also says
/// the offset a thing's kind implies, since that is the catalogue's).
/// </summary>
public static class AudioEmission
{
    /// <summary>
    /// Radius of the occlusion probe, metres, for an emitter with room for it: sampling a sphere makes
    /// occlusion a fraction, not a switch that flips as a fence post goes past. Half a metre is about the
    /// acoustic size of the things that make noise here.
    /// </summary>
    public const float DefaultOcclusionRadius = 0.5f;

    /// <summary>Smallest the probe shrinks to without room for the full radius. Not zero: a point probe is
    /// the switch again.</summary>
    public const float MinOcclusionRadius = 0.1f;

    /// <summary>Where the sound comes out, in world space, of a thing at <paramref name="position"/> turned by
    /// <paramref name="rotation"/>, its emitter at <paramref name="local"/> (the host's: open-fps's EntityEmission.LocalOffset).</summary>
    public static Vector3 PointFor(Vector3 position, Quaternion rotation, Vector3 local)
    {
        if (local == Vector3.Zero) return position;
        return position + Vector3.Transform(local, rotation);
    }

    /// <summary>
    /// How big a sphere may be probed at the emission point <paramref name="point"/> without reaching through
    /// what the emitter stands on: its height above the thing's origin. A tailpipe a third of a metre up gets a
    /// third of a metre; a half-metre sphere had half its samples inside the road. An emitter at its origin gets
    /// the minimum, to be fixed by authoring the offset, not by lifting the probe.
    /// </summary>
    public static float OcclusionRadiusFor(Vector3 position, Vector3 point)
    {
        float clearance = MathF.Max(0f, point.Y - position.Y);
        return Math.Clamp(clearance, MinOcclusionRadius, DefaultOcclusionRadius);
    }
}
