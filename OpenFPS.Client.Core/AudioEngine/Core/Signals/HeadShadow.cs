using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// A head, as the sphere it very nearly is — enough of one to listen through.
///
/// Two ears on a sphere of about 87 mm radius give you the two cues that matter most, and they are
/// quite different animals. The TIME difference is geometry and nothing else: the two ears are at
/// different distances from the source, so the sound arrives at different moments, up to about
/// 0.7 ms. A renderer that deposits every sample at the moment it ARRIVES gets that for nothing and
/// exactly right, including the way it changes as a source goes past — which is most of why a train
/// sweeps rather than pans.
///
/// The LEVEL difference is diffraction. Below about six hundred hertz the wavelength is bigger than
/// the head and the sound simply bends round it; above it the far ear sits in a shadow that gets
/// deeper with frequency. So the shadow is a FILTER and not a fader, which is why panning a mono
/// signal never sounds like anything is beside you. This is Brown and Duda's one-pole-one-zero
/// approximation to the rigid sphere: the pole is fixed at 2c/a, and the zero moves with the angle
/// between the source and the ear, from a boost when the source is at that ear to a deep shelf when
/// it is on the far side. It even keeps the little recovery directly behind the head — the bright
/// spot a sphere focuses into its own shadow, which is real and is one of the things that stops a
/// sound directly to one side from collapsing.
///
/// What this does NOT have is a pinna, so it has no elevation and no front-back discrimination: a
/// source ahead and the same source behind sound alike. That wants a measured HRTF, and the engine
/// has a path for one; this is for auditioning geometry at the bench.
/// </summary>
public struct HeadShadow
{
    /// <summary>Head radius, metres. The one dimension in it.</summary>
    public const float Radius = 0.0875f;
    private const float Beta = 2f * 343f / Radius;
    private const float AlphaMin = 0.1f;

    private float _b0, _b1, _a1, _x1, _y1;

    public HeadShadow(float rate)
    {
        _b0 = 1f; _b1 = _a1 = _x1 = _y1 = 0f;
        SetAngle(0f, rate);
    }

    /// <summary>
    /// The angle between the source and this ear's outward axis, radians: 0 when the source is
    /// straight out of this ear, pi when it is out of the other one. Changes the zero and leaves the
    /// state alone, so a source can sweep past without clicking.
    /// </summary>
    public void SetAngle(float incidence, float rate)
    {
        float theta = Math.Clamp(incidence, 0f, MathF.PI);
        // Brown and Duda: the zero swings from a boost at the ear to a deep shelf across the head,
        // turning back up a little in the very middle of the shadow.
        float alpha = (1f + AlphaMin * 0.5f) + (1f - AlphaMin * 0.5f) * MathF.Cos(theta / (150f * MathF.PI / 180f) * MathF.PI);
        float k = 2f * rate;
        float den = k + Beta;
        _b0 = (alpha * k + Beta) / den;
        _b1 = (Beta - alpha * k) / den;
        _a1 = (Beta - k) / den;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Process(float x)
    {
        float y = _b0 * x + _b1 * _x1 - _a1 * _y1;
        _x1 = x; _y1 = y;
        return y;
    }

    /// <summary>Where the two ears are, given where the head is and which way it is facing.</summary>
    public static (Vector3 Left, Vector3 Right) Ears(Vector3 head, Vector3 forward, Vector3 up)
    {
        var f = Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(up, f));
        return (head - right * Radius, head + right * Radius);
    }
}
