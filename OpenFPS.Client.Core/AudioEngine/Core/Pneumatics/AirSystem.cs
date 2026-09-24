using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Core.Pneumatics;

/// <summary>
/// One port letting air out: the whole of an air brake sound in one object.
///
/// The vessel behind the hole holds a pressure. While the ratio across the hole is past 1.893 the
/// flow is CHOKED — sonic at the throat, and the mass leaving depends only on the upstream pressure,
/// so the vessel empties exponentially with a time constant of its volume over the effective area
/// over a constant. That is the fat part of the hiss, and it is why a trailer takes three seconds
/// and a bus door takes half of one.
///
/// The jet that comes out is underexpanded and goes supersonic — about Mach 1.6 at 120 psi — so on
/// top of ordinary mixing noise there is broadband SHOCK-ASSOCIATED noise from the train of cells
/// standing in it, which is the rasp in the middle of a hard release. When the pressure ratio falls
/// under 1.893 the cells vanish, the jet goes subsonic, and Lighthill's eighth power takes it away
/// in a hurry: that knee is why the sound has a distinct end rather than fading out.
///
/// The muffler screwed into the port is the only reason any of it is bearable. An eight millimetre
/// hole peaks at fifteen kilohertz; the muffler is what makes it a hiss instead of a shriek, and
/// changing it is the difference between a truck and a train.
/// </summary>
public sealed class AirPort
{
    private readonly AirPortSpec _p;
    private readonly float _rate, _dt, _trim, _clackAmp;
    private readonly Random _rng;
    private Mode _clack;
    private float _clackRing;
    private float _mix1, _mix2, _shock1, _shock2, _muff1, _muff2, _hp;
    private float _pressure;               // kPa gauge in the vessel behind the port
    private bool _open;
    private readonly float _volumeM3, _areaM2;

    /// <summary>Pascals at one metre, valid after Step().</summary>
    public float Out { get; private set; }
    public float PressureKPa => _pressure;
    public bool Venting => _open && _pressure > 3f;
    public AirPortSpec Spec => _p;

    public AirPort(AirPortSpec p, float jetTrimDb, float rate, int seed)
    {
        _p = p; _rate = rate; _dt = 1f / rate;
        _rng = new Random(seed);
        _trim = MathF.Pow(10f, jetTrimDb / 20f);
        _clackAmp = 20e-6f * MathF.Pow(10f, p.ValveClackDb / 20f);
        _clack = new Mode(1900f, 11f, rate);
        _volumeM3 = MathF.Max(1e-4f, p.VolumeLitres * 1e-3f);
        _areaM2 = MathF.PI * 0.25f * p.OrificeMetres * p.OrificeMetres * p.DischargeCoefficient;
    }

    /// <summary>Charge the vessel to a pressure without making a sound about it.</summary>
    public void Charge(float kPaGauge) => _pressure = MathF.Max(0f, kPaGauge);

    /// <summary>Open the port. Everything else follows.</summary>
    public void Vent(float fromKPaGauge)
    {
        _pressure = MathF.Max(_pressure, fromKPaGauge);
        _open = true;
        // The valve moving, before the air has said anything. Two milliseconds ahead of the hiss and
        // it is what makes a release sound MECHANICAL rather than like a tap being turned on.
        _clackRing = _clackAmp * (0.75f + 0.5f * (float)_rng.NextDouble());
    }

    public void Close() => _open = false;

    /// <summary>How long this port takes to empty from full, seconds — volume over area over the
    /// choked-flow constant. Printed because it is the shape of the sound.</summary>
    public float BlowdownSeconds => _volumeM3 / MathF.Max(1e-9f, _areaM2 * 198.5f);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        float pAbs = _pressure + 101.3f;
        float ratio = pAbs / 101.3f;
        float jet = 0f;

        if (_open && _pressure > 0.5f)
        {
            // Choked while the ratio is past 1.893; below that the flow falls away with it.
            float chokeFactor = ratio > 1.893f ? 1f : MathF.Max(0f, (ratio - 1f) / 0.893f);
            float dpdt = _pressure * _areaM2 * 198.5f / _volumeM3 * chokeFactor;
            _pressure = MathF.Max(0f, _pressure - dpdt * _dt);

            // The fully expanded jet velocity for this pressure ratio: sqrt(2 cp T (1 - r^-0.2857)).
            float r = MathF.Max(1.001f, ratio);
            float u = MathF.Sqrt(2f * 1005f * 293f * (1f - MathF.Pow(r, -0.2857f)));
            float mach = u / 343f;

            // Mixing noise: Lighthill, peaking at Strouhal 0.2 on the hole.
            float amp = JetNoise.LighthillPressure(_p.OrificeMetres, u, 293f) * _trim;
            float fc = Math.Clamp(0.2f * u / _p.OrificeMetres, 300f, 16000f);
            float a = OnePole.AlphaFor(MathF.Min(fc, _rate * 0.45f), _rate);
            float n = (float)(_rng.NextDouble() * 2 - 1);
            _mix1 += a * (n - _mix1); _mix2 += a * (_mix1 - _mix2);
            jet = (_mix1 - _mix2) / JetNoise.BandNormaliser(a) * amp;

            // Shock cells, only while it is actually supersonic. Their spacing is 1.31 D
            // sqrt(M^2-1), and what they radiate is a band an octave or so wide around the speed
            // they convect past at — the rasp in the middle of a hard release.
            if (mach > 1.02f)
            {
                float beta = MathF.Sqrt(mach * mach - 1f);
                float cell = 1.31f * _p.OrificeMetres * beta;
                float fShock = Math.Clamp(0.7f * u / MathF.Max(1e-4f, cell) * 0.35f, 500f, 16000f);
                float sa = OnePole.AlphaFor(MathF.Min(fShock, _rate * 0.45f), _rate);
                float sn = (float)(_rng.NextDouble() * 2 - 1);
                _shock1 += sa * (sn - _shock1); _shock2 += sa * (_shock1 - _shock2);
                jet += (_shock1 - _shock2) / JetNoise.BandNormaliser(sa) * amp * 0.55f * MathF.Min(1f, beta);
            }

            // The muffler in the port: a straightforward loss of the top.
            float ma = OnePole.AlphaFor(_p.MufflerCornerHz, _rate);
            _muff1 += ma * (jet - _muff1); _muff2 += ma * (_muff1 - _muff2);
            jet = jet * (1f - _p.MufflerAbsorption) + _muff2 * _p.MufflerAbsorption * 1.6f;
        }

        float clack = _clack.Process(_clackRing) * 7f;
        _clackRing *= 0.9955f;

        float y = jet + clack;
        _hp += OnePole.AlphaFor(60f, _rate) * (y - _hp);
        Out = y - _hp;
    }

    public IEnumerable<string> Describe()
    {
        float r = 1f + 827f / 101.3f;
        float u = MathF.Sqrt(2f * 1005f * 293f * (1f - MathF.Pow(r, -0.2857f)));
        yield return $"{_p.Name}: {_p.OrificeMetres * 1000f:F1} mm hole on {_p.VolumeLitres:F1} L "
                   + $"-> empties in {BlowdownSeconds:F2} s, jet Mach {u / 343f:F2} ({u:F0} m/s), "
                   + $"mixing peak {0.2f * u / _p.OrificeMetres:F0} Hz, muffler takes {_p.MufflerAbsorption * 100f:F0}% above {_p.MufflerCornerHz:F0} Hz";
    }
}

/// <summary>
/// A vehicle's whole air system: the reservoir, the governor and the compressor that keeps it up,
/// and every port that lets it out again.
///
/// The governor is worth having because it is the reason a parked truck makes a noise every couple
/// of minutes for no visible reason: the compressor is geared to the engine and runs whenever the
/// engine does, so the governor loads and unloads it between a hundred and a hundred and twenty
/// pounds, and every time it unloads, the air dryer blows its accumulated water out through a hole.
/// Bang, then a sigh. Nobody tells it to; it is a pressure switch.
/// </summary>
public sealed class AirSystem
{
    private readonly AirSystemSpec _s;
    private readonly float _dt;
    private readonly Dictionary<string, AirPort> _ports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _rng;
    private float _reservoir;
    private bool _loaded = true;
    private float _compKnock;
    private Mode _comp;
    private readonly float _compAmp;
    private double _compPhase;
    private float _purgeArmed;

    /// <summary>Engine speed, rpm — the compressor is geared to it.</summary>
    public float EngineRpm { get; set; } = 700f;
    public float ReservoirKPa => _reservoir;
    public bool CompressorLoaded => _loaded;
    public IReadOnlyDictionary<string, AirPort> Ports => _ports;
    public AirSystemSpec Spec => _s;

    public AirSystem(AirSystemSpec s, float rate = 44100f, int seed = 61)
    {
        _s = s; _dt = 1f / rate; _rng = new Random(seed);
        _reservoir = s.CutOutKPa;
        int i = 0;
        foreach (var p in s.Ports) _ports[p.Name] = new AirPort(p, s.JetTrimDb, rate, seed + 10 * ++i);
        foreach (var p in _ports.Values) p.Charge(_reservoir);
        _comp = new Mode(320f, 6f, rate);
        _compAmp = 20e-6f * MathF.Pow(10f, s.CompressorDb / 20f);
    }

    /// <summary>Open a port and let it go.</summary>
    public void Vent(string port, float fraction = 1f)
    {
        if (!_ports.TryGetValue(port, out var p)) return;
        p.Vent(_reservoir * Math.Clamp(fraction, 0.05f, 1f));
        // The vessel came out of the reservoir, so the reservoir feels it.
        _reservoir = MathF.Max(150f, _reservoir - p.Spec.VolumeLitres / MathF.Max(1f, _s.ReservoirLitres) * 45f);
    }

    public void Close(string port) { if (_ports.TryGetValue(port, out var p)) p.Close(); }

    /// <summary>
    /// A service application: air goes INTO the chambers, so what you hear is a shorter, quieter
    /// version of the same thing from the treadle valve rather than the loud exhaust. The loud one
    /// is the RELEASE, which is the opposite of what most people assume.
    /// </summary>
    public void Apply() => Vent("service_release", 0.28f);
    public void Release() => Vent("service_release");

    /// <summary>Sum of every port, pascals at one metre, plus the compressor.</summary>
    public float Step()
    {
        // The governor. Loaded, the compressor fills the reservoir; at cut-out it unloads, and the
        // dryer blows down.
        float rpm = MathF.Max(0f, EngineRpm);
        if (rpm > 200f)
        {
            if (_loaded)
            {
                _reservoir += 9.5f * (rpm / 900f) * _dt;
                if (_reservoir >= _s.CutOutKPa)
                {
                    _loaded = false;
                    _purgeArmed = 0.02f;                  // the dryer goes a moment later
                }
            }
            else if (_reservoir <= _s.CutInKPa) _loaded = true;
        }
        if (_purgeArmed > 0f)
        {
            _purgeArmed -= _dt;
            if (_purgeArmed <= 0f && _ports.ContainsKey("dryer_purge")) Vent("dryer_purge");
        }

        float y = 0f;
        foreach (var p in _ports.Values) { p.Step(); y += p.Out; }

        // The compressor: a little two-cylinder pump knocking away at twice the speed it is geared
        // to. Only while it is LOADED — when the governor unloads it, it goes quiet, and that change
        // is audible from across a car park.
        if (_loaded && rpm > 200f)
        {
            double f = rpm / 60.0 * _s.CompressorOrder;
            _compPhase += f * _dt;
            if (_compPhase >= 1.0)
            {
                _compPhase -= 1.0;
                _compKnock = _compAmp * (0.7f + 0.6f * (float)_rng.NextDouble());
            }
            y += _comp.Process(_compKnock) * 5f;
            _compKnock *= 0.994f;
        }
        return y;
    }

    public IEnumerable<string> Describe()
    {
        yield return $"{_s.Name}: {_s.ReservoirLitres:F0} L reservoir, governor {_s.CutInKPa:F0}-{_s.CutOutKPa:F0} kPa "
                   + $"({_s.CutInKPa / 6.895f:F0}-{_s.CutOutKPa / 6.895f:F0} psi), jets {_s.JetTrimDb:F0} dB against Lighthill";
        foreach (var p in _ports.Values) foreach (var l in p.Describe()) yield return "  " + l;
    }
}
