using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Signals;

/// <summary>
/// A head as a rigid sphere of 87.5 mm radius, enough to listen through at the bench.
///
/// The time difference (up to about 0.7 ms) is geometry: a renderer that deposits each sample when
/// it arrives at each ear gets it for nothing. The level difference is diffraction, a filter and not
/// a fader: Brown and Duda's one-pole-one-zero sphere, the pole fixed at 2c/a and the zero moving with
/// the angle, from a boost at the near ear to a deep shelf across the head, with the bright spot
/// behind it. No pinna, so no elevation and no front-back cue: that wants a measured HRTF.
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
        // Turning back up a little in the very middle of the shadow.
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
