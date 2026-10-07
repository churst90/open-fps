using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// Where the engine bay's noise comes out, and what the vehicle's own body does to it on the way to a
/// listener: the treatment <see cref="ExhaustRadiation"/> gives the tailpipe.
///
/// A bay lets its noise out of two holes (<see cref="EngineBaySpec"/>), added as energies in the
/// proportion of their areas:
///
///   The grille, at the face of the vehicle: the body shadow per band, by the finite-barrier routes
///   the pipe uses (<see cref="ExhaustRadiation.BodyShadow"/>).
///
///   The open floor: the sound in the gap under the car gets out at the edge nearest the listener
///   (front bumper, sill, or back bumper after the car's length), losing
///   <see cref="EngineBaySpec.UnderbodyLossDbPerMetre"/> per metre of underbody crossed.
///
/// With nobody outside listening it passes the bay's sound unchanged; how much gets out at all is
/// <see cref="VehicleProfile.EngineBayLeakage"/>.
///
/// This is what makes the front of an idling car louder than its back by the margin NHTSA measured
/// (6 to 10 dB; FMVSS 141 final rule, 81 FR 90416, 2016). A bay radiating evenly from its middle can
/// only be louder in front by the extra distance to the back of the car, about 6 dB.
///
/// Inactive (unity) for a vehicle with no bay model, an open frame, or an engine in the back: those
/// leak evenly.
/// </summary>
public sealed class BayRadiation
{
    private const float CrossLowHz = 400f, CrossHighHz = 4000f;
    private const float SlewSeconds = 0.05f;

    /// <summary>How far the bay's open floor runs either side of the engine, metres: a car's bay is
    /// about a metre long.</summary>
    private const float BayHalfLengthMetres = 0.5f;
    /// <summary>Where the sound under the car gets out, metres above the road: in the gap under the
    /// body (ExhaustRadiation's underbody is at 0.2 m).</summary>
    private const float GapHeightMetres = 0.1f;

    private readonly bool _active;
    private readonly Vector3 _grille, _body;
    private readonly float _grilleShare, _gapLossDbPerMetre, _bayZ0, _bayZ1;
    private readonly float _aLow, _aHigh, _slew;
    private float _lp1, _lp2;
    private float _gL = 1f, _gM = 1f, _gH = 1f;
    private float _tL = 1f, _tM = 1f, _tH = 1f;

    /// <summary>The gains the bay is heading for, per band. For tests and instruments.</summary>
    public (float Low, float Mid, float High) Target => (_tL, _tM, _tH);
    public bool Active => _active;

    public BayRadiation(VehicleProfile v, float sampleRate)
    {
        _body = new Vector3(v.WidthMetres, v.HeightMetres, v.LengthMetres);
        bool openFrame = (v.Body?.CabinLengthM ?? 0f) <= 0f;
        _active = v.EngineBay != null && !openFrame && !v.EngineAtRear;
        _grilleShare = v.EngineBay?.GrilleShare ?? 0f;
        _gapLossDbPerMetre = v.EngineBay?.UnderbodyLossDbPerMetre ?? 0f;
        // The engine, at the front voice's slot; its grille faces the nearer end of the car (forward
        // for a front engine, back for a mid-engined car breathing through its rear deck); its open
        // floor is under it, a metre long (see UnderbodyExit).
        var engine = new Vector3(0f, v.FrontTapHeight, v.FrontTapZ);
        var ahead = v.FrontTapZ >= 0f ? Vector3.UnitZ : -Vector3.UnitZ;
        _grille = ExhaustRadiation.ExitPoint(engine, ahead, _body);
        float half = v.LengthMetres * 0.5f;
        _bayZ0 = Math.Clamp(v.FrontTapZ - BayHalfLengthMetres, -half, half);
        _bayZ1 = Math.Clamp(v.FrontTapZ + BayHalfLengthMetres, -half, half);
        float dt = 1f / sampleRate;
        _aLow = 1f - MathF.Exp(-2f * MathF.PI * CrossLowHz * dt);
        _aHigh = 1f - MathF.Exp(-2f * MathF.PI * CrossHighHz * dt);
        _slew = MathF.Min(1f, dt / SlewSeconds);
    }

    /// <summary>Where the listener is, in the vehicle's own frame from its origin. Null when nobody
    /// outside is listening: then the bay is heard as it is.</summary>
    public void Aim(Vector3? listener, float speedOfSound = 343f)
    {
        if (!_active || listener is not { } l) { _tL = _tM = _tH = 1f; return; }
        var (gL, gM, gH) = ExhaustRadiation.BodyShadow(_grille, l, _body, speedOfSound);
        var exit = UnderbodyExit(l, out float underMetres);
        float under = MathF.Pow(10f, -_gapLossDbPerMetre * underMetres / 20f);
        var (fL, fM, fH) = ExhaustRadiation.BodyShadow(exit, l, _body, speedOfSound);
        float s = _grilleShare, r = 1f - s;
        _tL = MathF.Sqrt(s * gL * gL + r * under * under * fL * fL);
        _tM = MathF.Sqrt(s * gM * gM + r * under * under * fM * fM);
        _tH = MathF.Sqrt(s * gH * gH + r * under * under * fH * fH);
    }

    /// <summary>
    /// Where the sound in the gap under the car gets out towards this listener — the point of the
    /// body's footprint nearest them, just under the floor — and how many metres of underbody it
    /// crossed from the bay's open floor to get there.
    /// </summary>
    internal Vector3 UnderbodyExit(Vector3 listener, out float underMetres)
    {
        float hx = _body.X * 0.5f, hz = _body.Z * 0.5f;
        float x = Math.Clamp(listener.X, -hx, hx), z = Math.Clamp(listener.Z, -hz, hz);
        // A listener over the footprint itself: out at the nearest edge.
        if (MathF.Abs(listener.X) <= hx && MathF.Abs(listener.Z) <= hz)
        {
            if (hx - MathF.Abs(x) < hz - MathF.Abs(z)) x = MathF.CopySign(hx, x == 0f ? 1f : x);
            else z = MathF.CopySign(hz, z == 0f ? 1f : z);
        }
        // From the bay's floor, which spans the width, to that point.
        float dz = z < _bayZ0 ? _bayZ0 - z : z > _bayZ1 ? z - _bayZ1 : 0f;
        underMetres = dz;
        return new Vector3(x, GapHeightMetres, z);
    }

    /// <summary>One sample of what leaves the bay, shaped for where the listener is.</summary>
    public float Process(float x)
    {
        if (!_active) return x;
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
