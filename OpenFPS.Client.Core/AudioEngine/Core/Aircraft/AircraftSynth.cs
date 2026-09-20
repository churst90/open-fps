using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Core.Aircraft;

/// <summary>
/// An aircraft, integrated sample by sample from the mechanisms in <see cref="AircraftProfile"/>:
/// rows of blades as pulse trains whose sharpness follows the tip Mach toward the listener, jets as
/// Lighthill mixing noise, a core as rumble and whine, and — for a piston aircraft — the car engine
/// itself with a propeller for a load.
///
/// The outputs are pascals at one metre in the machine's frame, like an engine's. Nothing in here
/// knows about distance, Doppler or air; that is the renderer's or the mixer's business, and it is
/// what makes the same object serve a flyover, a helicopter hovering overhead and a jet at the gate.
/// </summary>
public sealed class AircraftSynth
{
    public readonly AircraftProfile Profile;
    private readonly float _rate, _dt;

    /// <summary>The power lever, 0..1.</summary>
    public float Lever { get; set; } = 1f;
    /// <summary>How hard a rotor is meeting its own wake, 0..1 — a helicopter descending or in fast
    /// forward flight slaps, one in a hover does not. Ignored by anything without a rotor.</summary>
    public float Descending { get; set; }

    // Outputs, pascals at one metre, valid after Step().
    public float Blades { get; private set; }
    public float Jet { get; private set; }
    public float Core { get; private set; }
    public float Engine { get; private set; }
    public float Total { get; private set; }
    public float Rpm { get; private set; }
    /// <summary>Spool speed as a fraction of maximum (gas turbines).</summary>
    public float Spool => _spool;

    private readonly BladeRow? _prop, _tail, _fan;
    private readonly JetStream? _coreJet, _bypassJet;
    private readonly EngineSynth? _piston;
    private readonly Random _rng;
    private float _spool;
    private Vector3 _listener = new(0f, -30f, -100f);
    private float _rumbleLp1, _rumbleLp2, _rumbleGain;
    private double _whinePhase;
    private float _whineGain;
    private float _aftGain = 1f, _fwdGain = 1f;
    private int _slowTick;
    private const int SlowEvery = 64;

    public AircraftSynth(AircraftProfile p, float rate = 44100f, int seed = 3)
    {
        Profile = p;
        _rate = rate;
        _dt = 1f / rate;
        _rng = new Random(seed);
        if (p.Propeller != null)
            _prop = new BladeRow(p.Propeller, rate, p.Power == AircraftPower.Turboshaft ? Vector3.UnitY : Vector3.UnitZ, seed + 1);
        if (p.TailRotor != null)
            _tail = new BladeRow(p.TailRotor, rate, Vector3.UnitX, seed + 2);
        if (p.Turbine != null)
        {
            var t = p.Turbine;
            if (t.Fan != null) _fan = new BladeRow(t.Fan, rate, Vector3.UnitZ, seed + 3);
            _coreJet = new JetStream(rate, t.CoreNozzleDiameterMetres, t.CoreExitKelvin, seed + 4, t.CoreJetTrimDb);
            if (t.BypassNozzleDiameterMetres > 0f)
                _bypassJet = new JetStream(rate, t.BypassNozzleDiameterMetres, 330f, seed + 5, t.BypassJetTrimDb);
            _spool = t.IdleFraction;
        }
        if (p.Power == AircraftPower.Piston && p.EngineKey != null)
        {
            _piston = new EngineSynth(EngineProfile.ByName(p.EngineKey), rate, seed + 6);
            _piston.ExternalInertia = p.PropInertiaKgM2;
            _piston.Ignition = true;
        }
    }

    /// <summary>
    /// Starts the aircraft as one ALREADY at this power setting, rather than one that has to get
    /// there.
    ///
    /// The same thing EngineVoiceState.PlaceAtSpeed does for a car, and for the same reason. A
    /// turbofan spools on a five-second time constant and a turboprop on two and a half: an airliner
    /// that comes into earshot at cruise starts at IDLE and takes five seconds to become an airliner,
    /// every time. That is not a spool-up anybody is listening to — the aeroplane has been at cruise
    /// for an hour — it is the voice being born, and it is audible as a jet fading in from nothing at
    /// exactly the moment it should be most itself.
    ///
    /// Measured by the level check: rendered a second and a half after construction, the airliner
    /// came out at 102 dB against the 142 it declares. That is the spool, not the model.
    /// </summary>
    public void PlaceAtLever(float lever)
    {
        Lever = Math.Clamp(lever, 0f, 1f);
        if (Profile.Turbine is not { } t) return;
        _spool = t.IdleFraction + (1f - t.IdleFraction) * Lever;
        float u = MathHelper.Lerp(t.CoreExitVelocityIdle, t.CoreExitVelocityMax,
                                  Math.Clamp((_spool - t.IdleFraction) / MathF.Max(0.01f, 1f - t.IdleFraction), 0f, 1f));
        _coreJet?.SetVelocity(u);
        _bypassJet?.SetVelocity(t.BypassExitVelocityMax * _spool);
    }

    /// <summary>Where the listener stands in the aircraft's frame: x to starboard, y up, z toward
    /// the nose, origin at the engine. Sets every directivity and the tip Mach toward the ear.</summary>
    public void SetListener(Vector3 aircraftFrame)
    {
        if (aircraftFrame.LengthSquared() < 1e-4f) return;
        _listener = aircraftFrame;
    }

    private void UpdateSlow()
    {
        var p = Profile;
        Vector3 dir = Vector3.Normalize(_listener);
        // Jets and the combustor radiate aft; the compressor whine and a ducted fan, forward.
        float aftCos = Vector3.Dot(dir, -Vector3.UnitZ);
        _aftGain = JetStream.Directivity(aftCos);
        _fwdGain = 0.55f + 0.45f * Math.Clamp(Vector3.Dot(dir, Vector3.UnitZ), -1f, 1f);

        if (p.Turbine is { } t)
        {
            float want = t.IdleFraction + (1f - t.IdleFraction) * Math.Clamp(Lever, 0f, 1f);
            float tau = want > _spool ? t.SpoolSeconds : t.SpoolSeconds * 0.6f;
            _spool += (want - _spool) * MathF.Min(1f, _dt * SlowEvery / MathF.Max(0.05f, tau));
            float u = MathHelper.Lerp(t.CoreExitVelocityIdle, t.CoreExitVelocityMax, Math.Clamp((_spool - t.IdleFraction) / MathF.Max(0.01f, 1f - t.IdleFraction), 0f, 1f));
            _coreJet!.SetVelocity(u);
            _bypassJet?.SetVelocity(t.BypassExitVelocityMax * _spool);
            // Fuel flow goes roughly as the cube of spool speed; the rumble follows it.
            _rumbleGain = Db(t.CombustorDb) * _spool * _spool * _spool;
            _whineGain = Db(t.WhineDb) * MathF.Pow(_spool, 3f);
            _fan?.SetSpeed(t.Fan!.RpmMax * _spool, Math.Clamp(Lever, 0.2f, 1f), dir);
        }

        switch (p.Power)
        {
            case AircraftPower.Piston:
                _piston!.Throttle = Lever;
                // The prop is a fan law: torque with the square of speed, sized so full throttle runs
                // out at the redline.
                float rpm = _piston.Rpm;
                float k = 0.9f * _piston.Profile.PeakTorqueNm / (_piston.Profile.RedlineRpm * _piston.Profile.RedlineRpm);
                _piston.LoadTorque = k * rpm * rpm;
                _piston.Starter = rpm < 300f;
                _piston.SetListener(_listener);
                Rpm = rpm * p.PropGearRatio;
                _prop?.SetSpeed(Rpm, Math.Clamp(0.25f + 0.75f * Lever, 0f, 1f), dir);
                break;
            case AircraftPower.Turboprop:
            {
                var row = p.Propeller!;
                // Constant-speed: the governor holds the prop near its rated speed once the core is
                // up, and the lever changes the blade LOADING, not the note.
                float frac = Math.Clamp((_spool - p.Turbine!.IdleFraction) / MathF.Max(0.01f, 1f - p.Turbine.IdleFraction), 0f, 1f);
                Rpm = MathHelper.Lerp(row.RpmIdle, row.RpmMax, MathF.Min(1f, 0.6f + 0.4f * frac));
                _prop?.SetSpeed(Rpm, Math.Clamp(0.2f + 0.8f * Lever, 0f, 1f), dir);
                break;
            }
            case AircraftPower.Turboshaft:
            {
                var row = p.Propeller!;
                float up = Math.Clamp(_spool / MathF.Max(0.05f, p.Turbine!.IdleFraction), 0f, 1f);
                Rpm = row.RpmMax * up;
                _prop?.SetSpeed(Rpm, Math.Clamp(0.5f + 0.5f * Lever, 0f, 1f), dir, Descending);
                _tail?.SetSpeed(p.TailRotor!.RpmMax * up, Math.Clamp(0.5f + 0.5f * Lever, 0f, 1f), dir);
                break;
            }
            case AircraftPower.Turbofan:
                Rpm = p.Turbine!.Fan!.RpmMax * _spool;
                break;
        }
    }

    private static float Db(float db) => 20e-6f * MathF.Pow(10f, db / 20f);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        if (_slowTick == 0) UpdateSlow();
        _slowTick = _slowTick + 1 == SlowEvery ? 0 : _slowTick + 1;

        float blades = 0f, jet = 0f, core = 0f, engine = 0f;

        if (_piston != null)
        {
            _piston.Step();
            engine = _piston.Exhaust + _piston.Intake + _piston.Block;
        }
        if (_prop != null) blades += _prop.Step();
        if (_tail != null) blades += _tail.Step();
        if (_fan != null) blades += _fan.Step() * _fwdGain;

        if (_coreJet != null)
        {
            jet += _coreJet.Step() * _aftGain;
            if (_bypassJet != null) jet += _bypassJet.Step() * _aftGain;

            // Combustion rumble: low broadband, from the back.
            float n = (float)(_rng.NextDouble() * 2 - 1);
            float a = OnePole.AlphaFor(140f, _rate);
            _rumbleLp1 += a * (n - _rumbleLp1);
            _rumbleLp2 += a * (_rumbleLp1 - _rumbleLp2);
            core += _rumbleLp2 * _rumbleGain * 6f * (0.6f + 0.4f * _aftGain);

            // The one turbine tone inside hearing, and its octave.
            var t = Profile.Turbine!;
            _whinePhase += t.WhineHz * _spool / _rate;
            if (_whinePhase > 1.0) _whinePhase -= 1.0;
            core += (float)(Math.Sin(_whinePhase * 2 * Math.PI) + 0.25 * Math.Sin(_whinePhase * 4 * Math.PI))
                  * _whineGain * (0.5f + 0.5f * _fwdGain);
        }

        Blades = blades; Jet = jet; Core = core; Engine = engine;
        Total = blades + jet + core + engine;
    }

    /// <summary>Console lines about what was built.</summary>
    public System.Collections.Generic.IEnumerable<string> Describe()
    {
        var p = Profile;
        yield return $"{p.Name}: {p.Power}, {p.SourceLevelDb:F0} dB at 1 m";
        if (p.Propeller is { } r)
            yield return $"{(p.Power == AircraftPower.Turboshaft ? "main rotor" : "propeller")}: {r.Blades} blades x {r.DiameterMetres:F2} m, "
                       + $"{r.RpmMax:F0} rpm -> blade-pass {r.BladePassHz(r.RpmMax):F0} Hz, tip {r.TipSpeed(r.RpmMax):F0} m/s (Mach {r.TipSpeed(r.RpmMax) / 340f:F2})";
        if (p.TailRotor is { } tr)
            yield return $"tail rotor: {tr.Blades} blades x {tr.DiameterMetres:F2} m, {tr.RpmMax:F0} rpm -> {tr.BladePassHz(tr.RpmMax):F0} Hz, tip Mach {tr.TipSpeed(tr.RpmMax) / 340f:F2}";
        if (p.Turbine is { } t)
        {
            if (t.Fan is { } f)
                yield return $"fan: {f.Blades} blades x {f.DiameterMetres:F2} m, N1 {f.RpmIdle:F0}-{f.RpmMax:F0} rpm -> blade-pass {f.BladePassHz(f.RpmMax):F0} Hz, tip Mach {f.TipSpeed(f.RpmMax) / 340f:F2} at full power";
            yield return $"core jet: {t.CoreNozzleDiameterMetres:F2} m, {t.CoreExitVelocityIdle:F0}-{t.CoreExitVelocityMax:F0} m/s at {t.CoreExitKelvin:F0} K -> mixing noise peaks near {0.2f * t.CoreExitVelocityMax / t.CoreNozzleDiameterMetres:F0} Hz, {JetStream.LevelDb(t.CoreNozzleDiameterMetres, t.CoreExitVelocityMax, t.CoreExitKelvin) + t.CoreJetTrimDb:F0} dB at 1 m (Lighthill {t.CoreJetTrimDb:F0} dB, settled by ear)";
            if (t.BypassNozzleDiameterMetres > 0f)
                yield return $"bypass jet: {t.BypassNozzleDiameterMetres:F2} m, {t.BypassExitVelocityMax:F0} m/s -> peaks near {0.2f * t.BypassExitVelocityMax / t.BypassNozzleDiameterMetres:F0} Hz, {JetStream.LevelDb(t.BypassNozzleDiameterMetres, t.BypassExitVelocityMax, 330f) + t.BypassJetTrimDb:F0} dB at 1 m (Lighthill {t.BypassJetTrimDb:F0} dB)";
            yield return $"core: rumble {t.CombustorDb:F0} dB, whine {t.WhineHz:F0} Hz at {t.WhineDb:F0} dB, spool {t.SpoolSeconds:F1} s";
        }
        if (_piston != null) foreach (var line in _piston.Describe()) yield return "engine: " + line;
    }
}

/// <summary>
/// A row of blades sweeping past the listener, as the pulse train it is.
///
/// Each blade passage is one pulse. Its shape is the two classical terms of rotor noise: THICKNESS,
/// the blade's own volume pushing air aside, which is the second derivative of the swept section and
/// so a symmetric pulse; and LOADING, the lift the blade carries, which is the first derivative and
/// antisymmetric. Its WIDTH is the blade's passage time, chord over tip speed, compressed by
/// 1/(1 - M_r) where M_r is the tip Mach toward the listener — the Doppler of a source swinging
/// toward you — so the same blade is a soft thump at Mach 0.5 and a crack at 0.9, and its harmonics
/// climb with it. That compression IS why a propeller at full power buzzes and one at cruise hums, and
/// no table says so.
///
/// The blades are not identical. A fixed scatter of a per cent or two in each blade's pitch and
/// track puts energy at the SHAFT rate and its multiples under the blade-passing tone — the
/// once-per-revolution wobble in every real propeller note — and on a fan whose tips are supersonic
/// the scatter is in the shock strengths and is large, which is the buzz-saw: a comb at the shaft
/// frequency with dozens of teeth, emerging from blade differences rather than declared.
///
/// Blade-vortex interaction is a third, much shorter pulse a fraction of a revolution behind each
/// passage, when a rotor's blades cut through the tip vortices the blades ahead left in the air.
/// A helicopter in a descent slaps; hovering it does not. The level of it is the one number here
/// that is a flight state rather than a part.
/// </summary>
internal sealed class BladeRow
{
    private readonly BladeRowSpec _s;
    private readonly float _rate, _dt;
    private readonly Vector3 _axis;
    private readonly float[] _gainScatter, _trackScatter;
    private double _phase;               // revolutions
    private float _rpm, _rpmTarget, _loading = 1f;
    private float _machToward, _inPlane, _offPlane, _bvi;
    private bool _forward = true;
    private readonly float _refAmp;
    private float _hp, _hpAlpha;

    // The broadband half: turbulence off the trailing edge and the tip, band-limited by a Strouhal
    // number on the blade's thickness. Zero-cost for a row that declares none.
    private readonly float _selfAmp;
    private readonly Random? _selfRng;
    private float _selfLo1, _selfLo2, _selfHi1, _selfHi2, _selfAlphaLo, _selfAlphaHi, _selfNow;

    private struct Pulse { public double T0; public float Tau, AmpT, AmpL, AmpB; public bool Live; }
    private readonly Pulse[] _pulses = new Pulse[24];
    private double _t;

    public BladeRow(BladeRowSpec s, float rate, Vector3 axis, int seed)
    {
        _s = s; _rate = rate; _dt = 1f / rate; _axis = axis;
        var rng = new Random(seed);
        _gainScatter = new float[s.Blades];
        _trackScatter = new float[s.Blades];
        for (int b = 0; b < s.Blades; b++)
        {
            _gainScatter[b] = (float)(rng.NextDouble() * 2 - 1);
            _trackScatter[b] = (float)(rng.NextDouble() * 2 - 1);
        }
        // The anchor: the pulse amplitude that makes the train's RMS equal ReferenceDb at RpmMax,
        // in plane, fully loaded. For the shapes below the energy per pulse is about 1.33 A^2 tau.
        float refTip = s.TipSpeed(s.RpmMax);
        float refMach = MathF.Min(0.95f, refTip / 340f);
        float refTau = s.ChordMetres / MathF.Max(1f, refTip) * (1f - refMach);
        float refBpf = s.BladePassHz(s.RpmMax);
        float pRms = 20e-6f * MathF.Pow(10f, s.ReferenceDb / 20f);
        _refAmp = pRms / MathF.Sqrt(1.33f * refTau * refBpf);
        _hpAlpha = OnePole.AlphaFor(20f, rate);

        if (s.SelfNoiseDb > 1f)
        {
            _selfRng = new Random(seed + 991);
            // The band the vortices are shed in: f = St U / t, St = 0.2, t the blade's own
            // thickness. Two octaves of it, because a turbulent wake is not a resonance.
            float thickness = MathF.Max(1e-3f, s.ChordMetres * s.ThicknessRatio);
            float centre = Math.Clamp(0.2f * refTip / thickness, 80f, rate * 0.38f);
            _selfAlphaHi = OnePole.AlphaFor(Math.Clamp(centre * 2f, 120f, rate * 0.42f), rate);
            _selfAlphaLo = OnePole.AlphaFor(Math.Clamp(centre * 0.5f, 40f, rate * 0.2f), rate);
            // Two two-pole lowpasses differenced is the band; its gain against white noise depends
            // on where the corners landed, so it is MEASURED here rather than assumed, and the
            // declared level is then the level it really makes.
            float sum = 0f;
            var probe = new Random(7);
            float a1 = 0f, a2 = 0f, b1 = 0f, b2 = 0f;
            for (int i = 0; i < 8192; i++)
            {
                float n = (float)(probe.NextDouble() * 2 - 1) * 1.732f;   // unit variance
                a1 += _selfAlphaHi * (n - a1); a2 += _selfAlphaHi * (a1 - a2);
                b1 += _selfAlphaLo * (n - b1); b2 += _selfAlphaLo * (b1 - b2);
                float y = a2 - b2;
                if (i > 2048) sum += y * y;
            }
            float bandRms = MathF.Sqrt(sum / 6144f);
            float pSelf = 20e-6f * MathF.Pow(10f, s.SelfNoiseDb / 20f);
            _selfAmp = pSelf / MathF.Max(1e-6f, bandRms);
        }
    }

    /// <summary>Speed, loading (0..1), and where the listener is in the machine's frame.</summary>
    public void SetSpeed(float rpm, float loading, Vector3 listenerDir, float bvi = 0f)
    {
        _rpmTarget = MathF.Max(0f, rpm);
        _loading = loading;
        _bvi = Math.Clamp(bvi, 0f, 1f) * _s.BladeVortexInteraction;
        // The listener's elevation out of the disc plane, and whether it is on the forward side.
        float alongAxis = Vector3.Dot(listenerDir, _axis);
        _offPlane = MathF.Abs(alongAxis);
        _inPlane = MathF.Sqrt(MathF.Max(0f, 1f - _offPlane * _offPlane));
        _forward = alongAxis >= 0f;
        // The tip Mach toward the listener is the in-plane component of the tip speed.
        float tip = _s.TipSpeed(_rpm > 0 ? _rpm : _rpmTarget);
        _machToward = MathF.Min(0.95f, tip / 340f * _inPlane);
        // A duct will not carry anything below about half the blade-passing rate.
        if (_s.Ducted) _hpAlpha = OnePole.AlphaFor(MathF.Max(20f, 0.5f * _s.BladePassHz(MathF.Max(1f, _rpmTarget))), _rate);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Step()
    {
        // A rotor's speed changes slowly: its inertia is enormous against anything driving it.
        _rpm += (_rpmTarget - _rpm) * MathF.Min(1f, _dt * 2f);
        _t += _dt;
        if (_rpm > 1f)
        {
            double dphi = _rpm / 60.0 * _dt;
            double before = _phase;
            _phase += dphi;
            int b = _s.Blades;
            float tip = _s.TipSpeed(_rpm);
            float mach = MathF.Min(0.95f, tip / 340f * _inPlane);
            float machAbs = tip / 340f;
            // Supersonic tips: the per-blade shock strengths differ far more than the blades do.
            float scatter = _s.BladeScatter * (machAbs > 1f ? 1f + 12f * (machAbs - 1f) : 1f);
            for (int k = 0; k < b; k++)
            {
                double at = (k + _trackScatter[k] * scatter * 0.5) / b;
                double crossing = Math.Floor(before) + at;
                if (crossing < before) crossing += 1.0;
                if (crossing <= _phase)
                {
                    // Sub-sample time of the passage.
                    double t0 = _t - (_phase - crossing) / dphi * _dt;
                    Spawn(t0, k, mach, machAbs, scatter);
                }
            }
            if (_phase >= 1.0) _phase -= 1.0;
        }

        float y = 0f;
        for (int i = 0; i < _pulses.Length; i++)
        {
            ref var p = ref _pulses[i];
            if (!p.Live) continue;
            float x = (float)((_t - p.T0) / p.Tau);
            if (x > 4.5f) { p.Live = false; continue; }
            if (x < -4.5f) continue;
            float g = MathF.Exp(-0.5f * x * x);
            y += p.AmpT * (x * x - 1f) * g + p.AmpL * (-x) * g;
            if (p.AmpB != 0f)
            {
                // The slap: a much shorter pulse, a fixed fraction of a revolution on.
                float xb = (float)((_t - p.T0 - 0.12 * 60.0 / MathF.Max(1f, _rpm) / _s.Blades) / (1.1e-3));
                if (xb > -4f && xb < 4f) y += p.AmpB * (xb * xb - 1f) * MathF.Exp(-0.5f * xb * xb);
            }
        }
        if (_selfRng != null)
        {
            // Dipole: pressure with the cube of tip speed, so power with the sixth. The loading term
            // enters as a square root — half of this is the wake the blade drags whatever it is
            // doing, half is the turbulence its own lift makes.
            float u = _s.TipSpeed(_rpm) / MathF.Max(1f, _s.TipSpeed(_s.RpmMax));
            float n = (float)(_selfRng.NextDouble() * 2 - 1) * 1.732f;
            _selfHi1 += _selfAlphaHi * (n - _selfHi1); _selfHi2 += _selfAlphaHi * (_selfHi1 - _selfHi2);
            _selfLo1 += _selfAlphaLo * (n - _selfLo1); _selfLo2 += _selfAlphaLo * (_selfLo1 - _selfLo2);
            _selfNow = (_selfHi2 - _selfLo2) * _selfAmp * u * u * u
                     * MathF.Sqrt(Math.Clamp(0.4f + 0.6f * _loading, 0f, 1.6f))
                     // Trailing-edge noise is a dipole normal to the blade, so it is loudest out of
                     // the faces of the disc and a few decibels down in its plane — the opposite way
                     // round from thickness noise, and much gentler.
                     * (0.7f + 0.3f * _offPlane);
            y += _selfNow;
        }

        // Nothing below 20 Hz radiates from anything this size; a duct takes more.
        _hp += _hpAlpha * (y - _hp);
        return y - _hp;
    }

    private void Spawn(double t0, int blade, float mach, float machAbs, float scatter)
    {
        float tip = _s.TipSpeed(_rpm);
        float tau = _s.ChordMetres / MathF.Max(1f, tip) * (1f - mach);
        float refTip = _s.TipSpeed(_s.RpmMax);
        float refMach = MathF.Min(0.95f, refTip / 340f);
        // Thickness with the cube of tip Mach, loading with its square and the load carried. Between
        // them the share is set by how fast the tips go: a thin fast prop is mostly thickness, a slow
        // heavily loaded rotor mostly loading.
        float shareT = Math.Clamp((refMach - 0.4f) / 0.5f, 0.15f, 0.85f);
        float m = MathF.Min(1.2f, machAbs / MathF.Max(0.05f, refMach));
        float ampT = _refAmp * shareT * m * m * m * (0.25f + 0.75f * _inPlane);
        float ampL = _refAmp * (1f - shareT) * m * m * _loading * (0.4f + 0.6f * _offPlane);
        float g = 1f + scatter * _gainScatter[blade];
        // Behind a duct the fan is barely heard.
        float duct = _s.Ducted ? (_forward ? 1f : 0.12f) : 1f;
        float ampB = _bvi > 0f ? _bvi * _refAmp * 2.5f * (_forward ? 0.4f : 1f) : 0f;
        for (int i = 0; i < _pulses.Length; i++)
        {
            if (_pulses[i].Live) continue;
            _pulses[i] = new Pulse { T0 = t0, Tau = MathF.Max(1.5f / _rate, tau), AmpT = ampT * g * duct, AmpL = ampL * g * duct, AmpB = ampB * g, Live = true };
            return;
        }
    }
}

/// <summary>
/// A jet mixing with still air: Lighthill's eighth power, the same law and the same constant as the
/// exhaust's <see cref="JetNoise"/>. Broadband, peaking at Strouhal 0.2 on the nozzle, loudest
/// thirty to forty degrees off the axis behind the engine and quietest ahead of it. The spectrum is
/// wide — two decades — so two band sections an octave apart, and the amplitude breathes slowly,
/// which is the rumble of a big jet and the crackle of a fast one.
/// </summary>
internal sealed class JetStream
{
    private readonly float _rate, _d, _kelvin, _trim;
    private readonly Random _rng;
    private float _amp, _n1, _n2;
    private float _l1, _l2, _l3, _l4, _mod;
    private float _a1, _a2, _hp;

    /// <param name="trimDb">Where this stream sits against the law: see GasTurbineSpec.CoreJetTrimDb.</param>
    public JetStream(float rate, float diameter, float kelvin, int seed, float trimDb)
    {
        _rate = rate; _d = MathF.Max(0.02f, diameter); _kelvin = kelvin;
        _trim = MathF.Pow(10f, trimDb / 20f);
        _rng = new Random(seed);
        SetVelocity(1f);
    }

    public static float LevelDb(float diameter, float u, float kelvin) => JetNoise.LighthillDb(diameter, u, kelvin);

    /// <summary>Peak at 40 degrees off the aft axis, about -9 dB straight ahead.</summary>
    public static float Directivity(float cosFromAft)
    {
        float theta = MathF.Acos(Math.Clamp(cosFromAft, -1f, 1f));
        float d = (theta - 0.7f) / 0.6f;
        return 0.35f + 1.0f * MathF.Exp(-d * d);
    }

    public void SetVelocity(float u)
    {
        float fc = Math.Clamp(0.2f * MathF.Max(0.5f, u) / _d, 25f, 8000f);
        _a1 = OnePole.AlphaFor(fc, _rate);
        _a2 = OnePole.AlphaFor(fc * 2.5f, _rate);
        _n1 = JetNoise.BandNormaliser(_a1);
        _n2 = JetNoise.BandNormaliser(_a2);
        _amp = JetNoise.LighthillPressure(_d, u, _kelvin) * _trim;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Step()
    {
        float n = (float)(_rng.NextDouble() * 2 - 1);
        _l1 += _a1 * (n - _l1); _l2 += _a1 * (_l1 - _l2);
        _l3 += _a2 * (n - _l3); _l4 += _a2 * (_l3 - _l4);
        // Two unit-RMS sections weighted 0.85 and 0.5: about unit RMS together.
        float bp = (_l1 - _l2) / _n1 * 0.85f + (_l3 - _l4) / _n2 * 0.5f;
        // Breathing: a few hertz.
        float m = (float)(_rng.NextDouble() * 2 - 1);
        _mod += 0.0004f * (m - _mod);
        float y = bp * _amp * (1f + 12f * _mod);
        _hp += OnePole.AlphaFor(30f, _rate) * (y - _hp);
        return y - _hp;
    }
}
