using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// Which way a tailpipe throws its sound, and what the vehicle's own body does to it.
///
/// Reported: "a car facing you with the tail pipes away should give you reflections off whatever the
/// sound is bouncing off, while the tail pipe facing you should be loud — you should hear the
/// crackling." The pipe radiated as a bare point, the same in every direction, and nothing of the car
/// stood between it and a listener in front, so a car was as bright from its nose as from its tail.
///
/// Two mechanisms, both per band:
///
/// THE PIPE. An open pipe radiates evenly while it is small against the wavelength and beams along its
/// axis as it grows (Levine and Schwinger's unflanged pipe). The share that goes directional is
/// w = (ka)² / (1 + (ka)²) for a pipe of radius a at wavenumber k, and a listener at angle θ off the
/// axis gets 1 - w (1 - cos θ) / 2 of it. From the pipe's real radius: a 63 mm tailpipe straight
/// behind — toward the car's nose — keeps almost all of its bass, loses about 3 dB at 1.25 kHz and
/// about 26 at 8 kHz, in line with measured unflanged-pipe patterns.
///
/// THE BODY. When the vehicle stands between the pipe's exit and the listener, the sound goes over it,
/// under it or round it: every route round the vehicle's own box, each with the same diffraction the
/// walls use (Diffraction.BandGains), their energies added (see BodyShadow).
///
/// The result is three band gains at the model's band centres, applied to the pipe's signal by a split
/// at the mixer's own crossovers (400 Hz and 4 kHz) that sums back to the input when all three are one.
/// </summary>
public sealed class ExhaustRadiation
{
    private const float CrossLowHz = 400f, CrossHighHz = 4000f;
    /// <summary>How long a change of aim takes, seconds: a car turning past you, not a click.</summary>
    private const float SlewSeconds = 0.05f;

    private readonly Vector3 _axis, _exit, _body;
    private readonly float _radius;
    private readonly float _aLow, _aHigh, _slew;
    private float _lp1, _lp2;
    private float _gL = 1f, _gM = 1f, _gH = 1f;
    private float _tL = 1f, _tM = 1f, _tH = 1f;

    /// <summary>The gains the pipe is heading for, per band. For tests and instruments.</summary>
    public (float Low, float Mid, float High) Target => (_tL, _tM, _tH);

    /// <summary>Where the sound leaves the vehicle, in its own frame (x right, y up, z forward).</summary>
    public Vector3 Exit => _exit;

    public ExhaustRadiation(VehicleProfile v, float sampleRate)
    {
        _axis = v.ExhaustAxis.LengthSquared() > 1e-6f ? Vector3.Normalize(v.ExhaustAxis) : -Vector3.UnitZ;
        _radius = MathF.Max(0.005f, v.Engine.Exhaust.TailpipeDiameterMm * 0.0005f);
        _body = new Vector3(v.WidthMetres, v.HeightMetres, v.LengthMetres);
        _exit = ExitPoint(new Vector3(0f, v.ExhaustHeight, v.ExhaustOffsetZ), _axis, _body);
        float dt = 1f / sampleRate;
        _aLow = 1f - MathF.Exp(-2f * MathF.PI * CrossLowHz * dt);
        _aHigh = 1f - MathF.Exp(-2f * MathF.PI * CrossHighHz * dt);
        _slew = MathF.Min(1f, dt / SlewSeconds);
    }

    /// <summary>
    /// The slot pushed out along the pipe's axis until it clears the body, plus a few centimetres: a
    /// tailpipe ends at the bumper, not inside the car. A slot below the floor is already clear.
    /// </summary>
    internal static Vector3 ExitPoint(Vector3 slot, Vector3 axis, Vector3 body)
    {
        const float Clear = 0.05f, Underbody = 0.2f;
        if (slot.Y < Underbody) return slot + axis * Clear;
        float t = float.MaxValue;
        if (MathF.Abs(axis.X) > 1e-4f) t = MathF.Min(t, ((axis.X > 0 ? body.X : -body.X) * 0.5f - slot.X) / axis.X);
        if (MathF.Abs(axis.Y) > 1e-4f) t = MathF.Min(t, ((axis.Y > 0 ? body.Y : Underbody) - slot.Y) / axis.Y);
        if (MathF.Abs(axis.Z) > 1e-4f) t = MathF.Min(t, ((axis.Z > 0 ? body.Z : -body.Z) * 0.5f - slot.Z) / axis.Z);
        return slot + axis * (MathF.Max(0f, t) + Clear);
    }

    /// <summary>Where the listener is, in the vehicle's own frame from its origin (the contact patch
    /// under its centre). Null when nobody outside is listening: then the pipe is heard as it is.</summary>
    public void Aim(Vector3? listener, float speedOfSound = 343f)
    {
        if (listener is not { } l) { _tL = _tM = _tH = 1f; return; }
        var toListener = l - _exit;
        float len = toListener.Length();
        if (len < 1e-3f) { _tL = _tM = _tH = 1f; return; }
        float away = (1f - Vector3.Dot(_axis, toListener / len)) * 0.5f;       // 0 on the axis, 1 behind

        float Pipe(float hz)
        {
            float ka = 2f * MathF.PI * hz / speedOfSound * _radius;
            float w = ka * ka / (1f + ka * ka);
            return 1f - w * away;
        }

        var (bL, bM, bH) = BodyShadow(_exit, l, _body, speedOfSound);
        _tL = Pipe(Diffraction.LowBandHz) * bL;
        _tM = Pipe(Diffraction.MidBandHz) * bM;
        _tH = Pipe(Diffraction.HighBandHz) * bH;
    }

    /// <summary>
    /// What the vehicle's own body lets past, per band, between the pipe's exit and the listener.
    ///
    /// A car is not a wall. It is a box a couple of metres across, and at 200 Hz (1.7 m of wavelength)
    /// sound bends round it from every side at once — over the roof, under the floor, round both
    /// flanks — so treating it as an infinite screen with one route over the top took 14 dB off the bass
    /// in front of a car, which is not what standing in front of a car sounds like. Each route round is
    /// taken separately, the edge's loss for each from the same formula the walls use, and their
    /// energies added: the finite-barrier treatment (ISO 9613-2's lateral paths). A route counts only
    /// if its legs stay outside the body.
    /// </summary>
    internal static (float Low, float Mid, float High) BodyShadow(Vector3 from, Vector3 to, Vector3 body, float speedOfSound)
    {
        const float Underbody = 0.2f;
        var min = new Vector3(-body.X * 0.5f, Underbody, -body.Z * 0.5f);
        var max = new Vector3(body.X * 0.5f, body.Y, body.Z * 0.5f);
        var centre = (min + max) * 0.5f;
        var size = max - min;
        if (!Crosses(from, to, min, max, out var entry, out var exit)) return (1f, 1f, 1f);

        float direct = Vector3.Distance(from, to);
        float sumL = 0f, sumM = 0f, sumH = 0f;
        var inner = size * 0.998f;
        // Each face: carry the points where the line enters and leaves out onto that face's plane.
        for (int axis = 0; axis < 3; axis++)
        for (int side = 0; side < 2; side++)
        {
            float plane = side == 0 ? (axis == 0 ? min.X : axis == 1 ? min.Y : min.Z)
                                    : (axis == 0 ? max.X : axis == 1 ? max.Y : max.Z);
            if (axis == 1 && side == 0 && plane <= 0.01f) continue;                 // no gap under it
            var p = With(entry, axis, plane);
            var q = With(exit, axis, plane);
            if (GeometryUtils.LineIntersectsAABB(from, p, centre, inner) ||
                GeometryUtils.LineIntersectsAABB(p, q, centre, inner) ||
                GeometryUtils.LineIntersectsAABB(q, to, centre, inner)) continue;
            float detour = Vector3.Distance(from, p) + Vector3.Distance(p, q) + Vector3.Distance(q, to) - direct;
            var (gL, gM, gH) = Diffraction.BandGains(MathF.Max(0f, detour), speedOfSound);
            sumL += gL * gL; sumM += gM * gM; sumH += gH * gH;
        }
        return (MathF.Min(1f, MathF.Sqrt(sumL)), MathF.Min(1f, MathF.Sqrt(sumM)), MathF.Min(1f, MathF.Sqrt(sumH)));

        static Vector3 With(Vector3 v, int axis, float value)
            => axis == 0 ? new(value, v.Y, v.Z) : axis == 1 ? new(v.X, value, v.Z) : new(v.X, v.Y, value);
    }

    /// <summary>Whether the segment passes through the box, and where it goes in and comes out.</summary>
    private static bool Crosses(Vector3 a, Vector3 b, Vector3 min, Vector3 max, out Vector3 entry, out Vector3 exit)
    {
        entry = exit = default;
        var d = b - a;
        float t0 = 0f, t1 = 1f;
        for (int i = 0; i < 3; i++)
        {
            float ai = i == 0 ? a.X : i == 1 ? a.Y : a.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z, hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
            if (MathF.Abs(di) < 1e-6f) { if (ai < lo || ai > hi) return false; continue; }
            float ta = (lo - ai) / di, tb = (hi - ai) / di;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        if (t0 <= 1e-4f || t1 >= 1f - 1e-4f) return false;       // an end inside it: not a shadow
        entry = a + d * t0; exit = a + d * t1;
        return true;
    }

    /// <summary>One sample of the pipe's own radiation, shaped for where the listener is.</summary>
    public float Process(float x)
    {
        _gL += (_tL - _gL) * _slew;
        _gM += (_tM - _gM) * _slew;
        _gH += (_tH - _gH) * _slew;
        _lp1 += (x - _lp1) * _aLow;
        float low = _lp1, rest = x - low;
        _lp2 += (rest - _lp2) * _aHigh;
        float mid = _lp2, high = rest - mid;
        return _gL * low + _gM * mid + _gH * high;
    }
}
