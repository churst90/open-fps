using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Core.Pneumatics;

/// <summary>
/// One port letting air out: the whole of an air brake sound in one object.
///
/// While the pressure ratio is past 1.893 the flow is choked and the vessel empties exponentially,
/// volume over effective area over a constant: a trailer in three seconds, a bus door in half of one.
/// The underexpanded jet goes supersonic (about Mach 1.6 at 120 psi) and its shock cells add the
/// rasp in the middle of a hard release; under 1.893 they vanish and Lighthill's eighth power ends
/// the sound distinctly. An eight millimetre hole peaks at fifteen kilohertz, and the muffler in the
/// port makes it a hiss instead of a shriek.
/// </summary>
public sealed class AirPort
{
    private readonly AirPortSpec _p;
    private readonly float _rate, _dt, _trim, _clackAmp;
    private readonly Random _rng;
    private Mode _clack;
    private float _clackRing;
    private readonly float _clackDecay;   // 0.9955 a sample at 44.1 kHz: 5 ms
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
        _p = p; _rate = rate; _dt = 1f / rate; _clackDecay = At44k.Decay(0.9955f, rate);
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
        // From the pressure it is given, not the larger of that and the vessel's: every vessel is
        // charged when a voice is made, and each vehicle's first release was a full dump whatever
        // the stop. Only a port still venting keeps its higher pressure.
        _pressure = Venting ? MathF.Max(_pressure, fromKPaGauge) : MathF.Max(0f, fromKPaGauge);
        Opened++;
        _open = true;
        // The valve moving, before the air: what makes a release sound mechanical, not a tap.
        _clackRing = _clackAmp * (0.75f + 0.5f * (float)_rng.NextDouble());
    }

    public void Close() => _open = false;

    /// <summary>How many times this valve has opened.</summary>
    public int Opened { get; private set; }

    /// <summary>How long this port takes to empty from full, seconds: volume over area over the
    /// choked-flow constant.</summary>
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

            // Shock cells while supersonic, spaced 1.31 D √(M²−1), radiating a band an octave or so
            // wide at the rate they convect past.
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

            float ma = OnePole.AlphaFor(_p.MufflerCornerHz, _rate);
            _muff1 += ma * (jet - _muff1); _muff2 += ma * (_muff1 - _muff2);
            jet = jet * (1f - _p.MufflerAbsorption) + _muff2 * _p.MufflerAbsorption * 1.6f;
        }

        float clack = _clack.Process(_clackRing) * 7f;
        _clackRing *= _clackDecay;

        float y = jet + clack;
        _hp += OnePole.AlphaFor(60f, _rate) * (y - _hp);
        Out = y - _hp;
    }

    // Stryker disable all : diagnostic text for the lab
    public IEnumerable<string> Describe()
    {
        float r = 1f + 827f / 101.3f;
        float u = MathF.Sqrt(2f * 1005f * 293f * (1f - MathF.Pow(r, -0.2857f)));
        yield return $"{_p.Name}: {_p.OrificeMetres * 1000f:F1} mm hole on {_p.VolumeLitres:F1} L "
                   + $"-> empties in {BlowdownSeconds:F2} s, jet Mach {u / 343f:F2} ({u:F0} m/s), "
                   + $"mixing peak {0.2f * u / _p.OrificeMetres:F0} Hz, muffler takes {_p.MufflerAbsorption * 100f:F0}% above {_p.MufflerCornerHz:F0} Hz";
    }
    // Stryker restore all
}

/// <summary>
/// A vehicle's whole air system: the reservoir, the governor and the compressor that keeps it up,
/// and every port that lets it out again. The governor loads and unloads the engine-geared
/// compressor between a hundred and a hundred and twenty psi, and each unload purges the air dryer:
/// why a parked truck bangs and sighs every couple of minutes.
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
    private readonly float _knockDecay;   // 0.994 a sample at 44.1 kHz: 3.8 ms
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

    /// <summary>
    /// The part of the last <see cref="Step"/> that came from the FRONT of the vehicle: the ports
    /// <see cref="PlaceAtFront"/> named, and the compressor, which is bolted to the engine. The rest
    /// is the back. Zero until something says where the front is.
    /// </summary>
    public float FrontOut { get; private set; }
    private readonly HashSet<AirPort> _atFront = new();
    private bool _compressorAtFront;

    /// <summary>Which ports are nearer the front outlet than the back, and whether the engine (and
    /// so the compressor) is. A vehicle is a body with two ends; its valves are on one or the other.</summary>
    public void PlaceAtFront(Func<AirPortSpec, bool> atFront, bool compressorAtFront)
    {
        _atFront.Clear();
        foreach (var p in _ports.Values) if (atFront(p.Spec)) _atFront.Add(p);
        _compressorAtFront = compressorAtFront;
    }

    public AirSystem(AirSystemSpec s, float rate = OpenFPS.Client.AudioEngine.Fmod.MixerQuality.DefaultRate, int seed = 61)
    {
        _s = s; _dt = 1f / rate; _rng = new Random(seed); _knockDecay = At44k.Decay(0.994f, rate);
        _reservoir = s.CutOutKPa;
        int i = 0;
        foreach (var p in s.Ports) _ports[p.Name] = new AirPort(p, s.JetTrimDb, rate, seed + 10 * ++i);
        // Charged as on the move: spring brakes, door engines and bags full, service chambers empty.
        foreach (var p in _ports.Values)
            p.Charge(string.Equals(p.Spec.Name, "service_release", StringComparison.OrdinalIgnoreCase) ? 0f : _reservoir);
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
    /// A service application: air goes into the chambers, so it is a shorter, quieter sound from the
    /// treadle valve. The loud one is the release.
    /// </summary>
    public void Apply() => Vent("service_release", 0.28f);
    public void Release() => Vent("service_release");

    /// <summary>Sum of every port, pascals at one metre, plus the compressor.</summary>
    public float Step()
    {
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

        float y = 0f, front = 0f;
        foreach (var p in _ports.Values)
        {
            p.Step(); y += p.Out;
            if (_atFront.Contains(p)) front += p.Out;
        }

        // The compressor knocks only while loaded: the change when it unloads is audible across a
        // car park.
        if (_loaded && rpm > 200f)
        {
            double f = rpm / 60.0 * _s.CompressorOrder;
            _compPhase += f * _dt;
            if (_compPhase >= 1.0)
            {
                _compPhase -= 1.0;
                _compKnock = _compAmp * (0.7f + 0.6f * (float)_rng.NextDouble());
            }
            float knock = _comp.Process(_compKnock) * 5f;
            y += knock;
            if (_compressorAtFront) front += knock;
            _compKnock *= _knockDecay;
        }
        FrontOut = front;
        return y;
    }

    // Stryker disable all : diagnostic text for the lab
    public IEnumerable<string> Describe()
    {
        yield return $"{_s.Name}: {_s.ReservoirLitres:F0} L reservoir, governor {_s.CutInKPa:F0}-{_s.CutOutKPa:F0} kPa "
                   + $"({_s.CutInKPa / 6.895f:F0}-{_s.CutOutKPa / 6.895f:F0} psi), jets {_s.JetTrimDb:F0} dB against Lighthill";
        foreach (var p in _ports.Values) foreach (var l in p.Describe()) yield return "  " + l;
    }
    // Stryker restore all
}
