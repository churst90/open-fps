using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Core.Rail;

/// <summary>
/// The front end of a steam locomotive: what happens between the cylinders and the top of the
/// chimney, plus everything else the machine leaks and clanks.
///
/// THE CHUFF IS A JET, not a drum. When the piston reaches the end of its stroke the exhaust valve
/// opens on a cylinder still holding a couple of bar, and that cylinder empties up the blast pipe
/// and out of a nozzle about a hand's breadth across, at the speed of sound in steam. So it is
/// Lighthill's eighth power like any other jet — which is exactly why an engine working hard is a
/// cannon and the same engine drifting is nearly silent, and why the difference is not a volume
/// control but the exhaust pressure. Each burst decays as the cylinder empties, and how fast that is
/// is the cylinder's volume divided by the nozzle area times the speed of sound: about thirty
/// milliseconds for a big engine, which is what makes a chuff a chuff.
///
/// THE BARK IS THE CHIMNEY. The burst goes out through a pipe open at both ends, so it is shaped by
/// the resonances of c/2L of the stack — two hundred hertz for a metre of chimney. A short wide
/// stack barks; a tall narrow one rings. Nothing else in this file decides the pitch of the exhaust.
///
/// THE RATE IS GEOMETRY. Two cylinders, each double-acting, means four beats per turn of the
/// drivers, and the drivers turn at the road speed over their circumference. A 1.85 m wheel at 25
/// m/s turns 4.3 times a second, so it barks seventeen times a second — fast enough that the beats
/// have run together into a roar, which is what a big engine at speed really sounds like and not
/// what anybody expects. At walking pace the same engine gives you the four separate beats. And
/// because no valve gear was ever square, the four are not evenly spaced: the limp in the beat is
/// ValveSettingError and it is the difference between a locomotive and a metronome.
///
/// AND IT IS NEVER QUIET. The blower, the glands, the injector overflow and every joint in it hiss
/// continuously — a steam engine standing still is a loud machine — and the rods, crossheads and
/// axleboxes all have play in them and clank once a revolution.
/// </summary>
public sealed class SteamFrontEnd
{
    private readonly SteamLocoSpec _s;
    private readonly float _rate, _dt;
    private readonly Random _rng;
    private readonly float[] _beatPhase;      // where in a revolution each beat happens
    private readonly float[] _beatGain;
    private Mode[] _stack;
    private readonly float _leakAmp, _motionAmp;
    private readonly float _decayTau;
    private double _rev;                      // driver revolutions
    private float _env;                       // the blast envelope now
    private float _jetLp1, _jetLp2, _leak1, _leak2, _draught, _hp;
    private Mode _clank;
    private float _clankRing;
    private int _lastBeat = -1;
    private double _lastClankRev = -1;

    /// <summary>Regulator and cut-off together, 0..1: how hard it is being worked. This is the
    /// pressure left in the cylinder at release, and because the jet obeys an eighth power it is
    /// worth forty decibels between drifting and full.</summary>
    public float Effort { get; set; } = 0.8f;
    /// <summary>Driver speed over road speed. One is rolling; four is a slip, and the bark runs away
    /// with it.</summary>
    public float Slip { get; set; } = 1f;
    /// <summary>Cylinder cocks open: two great plumes of wet steam at rail level on starting.</summary>
    public bool CocksOpen { get; set; }
    /// <summary>The safety valves lifting: the loudest thing on the locomotive and nothing to do
    /// with the exhaust.</summary>
    public bool SafetyValve { get; set; }

    public float ChuffHz { get; private set; }
    public float StackHz => _s.StackHz;

    public SteamFrontEnd(SteamLocoSpec s, float rate, int seed)
    {
        _s = s; _rate = rate; _dt = 1f / rate; _rng = new Random(seed);

        int beats = 2 * Math.Max(1, s.Cylinders);
        _beatPhase = new float[beats];
        _beatGain = new float[beats];
        var rng = new Random(seed + 5);
        for (int i = 0; i < beats; i++)
        {
            // Where the beat SHOULD be, plus how far out this engine's valve gear actually is.
            float err = (float)(rng.NextDouble() * 2 - 1) * s.ValveSettingError;
            _beatPhase[i] = (i / (float)beats + err + 1f) % 1f;
            _beatGain[i] = 1f + 0.18f * (float)(rng.NextDouble() * 2 - 1);
        }
        Array.Sort(_beatPhase, _beatGain);

        BuildStack();

        // The cylinder empties through the nozzle at the speed of sound in steam: volume over area
        // over speed. That is the decay of one beat, and it is why a big engine's exhaust is soft
        // and a small one's is a crack.
        float cylVol = MathF.PI * 0.25f * s.CylinderBoreMetres * s.CylinderBoreMetres * s.CylinderStrokeMetres;
        float nozzleArea = MathF.PI * 0.25f * s.BlastNozzleMetres * s.BlastNozzleMetres;
        _decayTau = Math.Clamp(cylVol / MathF.Max(1e-4f, nozzleArea * 480f), 0.006f, 0.12f);

        _leakAmp = Db(s.LeakageDb);
        _motionAmp = Db(s.MotionDb);
        _clank = new Mode(420f, 14f, rate);
    }

    private void BuildStack()
    {
        // A pipe open at both ends: every harmonic of c/2L, damped hard by the gas tearing through it.
        float f1 = _s.StackHz;
        int n = Math.Max(1, (int)MathF.Min(5f, 0.4f * _rate / f1));
        _stack = new Mode[n];
        for (int k = 0; k < n; k++) _stack[k] = new Mode(f1 * (k + 1), 7f / (1f + 0.5f * k), _rate);
    }

    private static float Db(float db) => 20e-6f * MathF.Pow(10f, db / 20f);

    /// <summary>Pascals at one metre. <paramref name="speedMps"/> is the road speed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Step(float speedMps)
    {
        float driverHz = MathF.Abs(speedMps) * MathF.Max(0.2f, Slip) / (MathF.PI * MathF.Max(0.3f, _s.DriverDiameterMetres));
        ChuffHz = driverHz * _beatPhase.Length;

        double before = _rev;
        _rev += driverHz * _dt;

        // Has a beat gone past?
        for (int i = 0; i < _beatPhase.Length; i++)
        {
            double at = Math.Floor(before) + _beatPhase[i];
            if (at < before) at += 1.0;
            if (at <= _rev && i != _lastBeat)
            {
                _env = _beatGain[i];
                _lastBeat = i;
                break;
            }
        }
        if (_rev >= 1.0) { _rev -= 1.0; _lastBeat = -1; }

        // The cylinder emptying.
        _env -= _env * _dt / _decayTau;

        // The jet. The exhaust pressure at release is what the engine is being worked at, and the
        // eighth power does the rest: a drifting engine's blast is forty decibels under a working
        // one's and that is not a fader, it is the law.
        float effort = Math.Clamp(Effort, 0.02f, 1.2f);
        float u = 150f + 330f * effort;
        float jetLevel = JetNoise.LighthillPressure(_s.BlastNozzleMetres, u, 700f);

        float n1 = (float)(_rng.NextDouble() * 2 - 1);
        float a = OnePole.AlphaFor(Math.Clamp(0.2f * u / _s.BlastNozzleMetres, 200f, 6000f), _rate);
        _jetLp1 += a * (n1 - _jetLp1); _jetLp2 += a * (_jetLp1 - _jetLp2);
        float jet = (_jetLp1 - _jetLp2) / JetNoise.BandNormaliser(a) * jetLevel;

        // The draught: between beats the fire is still being pulled through, and the smokebox is
        // still roaring. It follows the blast but never goes away while the engine is alive.
        _draught += ((0.18f + 0.82f * _env) - _draught) * MathF.Min(1f, 60f * _dt);

        float through = jet * _draught;
        float stack = 0f;
        for (int k = 0; k < _stack.Length; k++) stack += _stack[k].Process(through) / (1f + 0.6f * k);
        float y = stack * 2.2f + through * 0.35f;

        // Everything that leaks. A steam locomotive standing at a platform is not a quiet machine.
        float n3 = (float)(_rng.NextDouble() * 2 - 1);
        float la = OnePole.AlphaFor(2600f, _rate);
        _leak1 += la * (n3 - _leak1);
        _leak2 += OnePole.AlphaFor(300f, _rate) * (_leak1 - _leak2);
        float leak = (_leak1 - _leak2) * 2.6f * _leakAmp;
        if (CocksOpen) leak += (_leak1 - _leak2) * 9f * _leakAmp;
        if (SafetyValve)
        {
            // A safety valve is a choked jet through a much smaller hole than the blast nozzle, so
            // it is far higher and far more violent than anything else on the engine.
            float sv = JetNoise.LighthillPressure(0.045f, 480f, 460f);
            leak += (_leak1 - _leak2) * 3.5f * sv;
        }

        // Rods and motion: play in every joint, once a revolution each side.
        if (driverHz > 0.05f)
        {
            double half = Math.Floor(_rev * 2.0);
            if (half != _lastClankRev)
            {
                _lastClankRev = half;
                _clankRing = _motionAmp * (0.6f + 0.8f * (float)_rng.NextDouble()) * MathF.Min(1f, driverHz / 2.5f);
            }
        }
        float clank = _clank.Process(_clankRing) * 6f;
        _clankRing *= 0.988f;

        float outp = y + leak + clank;
        _hp += OnePole.AlphaFor(28f, _rate) * (outp - _hp);
        return outp - _hp;
    }

    public IEnumerable<string> Describe(float speed)
    {
        yield return $"front end: {_s.Cylinders} cylinders x {_s.CylinderBoreMetres * 1000f:F0}/{_s.CylinderStrokeMetres * 1000f:F0} mm on {_s.DriverDiameterMetres:F2} m drivers";
        yield return $"  blast nozzle {_s.BlastNozzleMetres * 1000f:F0} mm -> {JetNoise.LighthillDb(_s.BlastNozzleMetres, 460f, 700f):F0} dB at 1 m at full effort, "
                   + $"each beat decaying in {_decayTau * 1000f:F0} ms";
        yield return $"  chimney {_s.StackLengthMetres:F2} m x {_s.StackDiameterMetres * 1000f:F0} mm -> bark at {_s.StackHz:F0} Hz and its harmonics";
        yield return $"  at {speed * 3.6f:F0} km/h: drivers {speed / (MathF.PI * _s.DriverDiameterMetres):F2} rev/s, "
                   + $"{_s.ChuffHz(speed):F1} beats/s (a beat every {1000f / MathF.Max(0.1f, _s.ChuffHz(speed)):F0} ms against a {_decayTau * 1000f:F0} ms decay)";
        yield return $"  valve setting out by {_s.ValveSettingError * 100f:F1}% of a beat";
    }
}
