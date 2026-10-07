using System.Runtime.CompilerServices;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>
/// What the water on a road does to the sound of a vehicle's tyres: the ejection hiss (power the
/// kinetic energy of the water thrown, so with the water and the cube of speed, pulsing as the grooves
/// empty), drops striking the arch and body, the bow wave of a film, and the splash entering a
/// puddle. See docs/WET_ROADS.md, "The sound".
///
/// One per vehicle voice. <see cref="Block"/> once a block with each wheel's water (WheelState.Water)
/// and gain; <see cref="Step"/> once a sample on the producer, leaving the front and rear taps and the
/// cabin, Pa at a metre. No allocation after construction.
/// </summary>
public sealed class WetTyres
{
    // ── The laws' constants (fitted: docs/WET_ROADS.md, "Fitting") ──────────────────────────────

    /// <summary>The speed and water the levels below are quoted at: 50 km/h on a millimetre of water
    /// (an asphalt texture full and a thin film on it: moderate rain).</summary>
    public const float ReferenceSpeed = 50f / 3.6f, ReferenceWaterMm = 1f;
    /// <summary>The tyre width the levels are quoted for, metres: a 205.</summary>
    public const float ReferenceWidth = 0.205f;

    /// <summary>One tyre's ejection hiss at the reference, dB SPL at a metre, broadband.</summary>
    public static float EjectionDb = 83f;
    /// <summary>p² ∝ W^this: one is all the water swept, thrown.</summary>
    public static float WaterExponent = 1f;
    /// <summary>p² ∝ u^this: three is the kinetic energy of the water thrown.</summary>
    public static float SpeedExponent = 3f;
    /// <summary>The hiss's two-pole high-pass, Hz.</summary>
    public static float EjectionLowHz = 900f;
    /// <summary>Its low-pass, Hz, times (u / reference)^<see cref="BrightnessExponent"/>: recorded wet
    /// pass-bys fall 5-10 dB an octave above 4 kHz (docs/WET_ROADS.md, "Fitting").</summary>
    public static float EjectionHighHz = 3500f;
    public static float BrightnessExponent = 0.5f;
    /// <summary>How deep the groove pulses modulate the hiss, 0..1.</summary>
    public static float GrooveModulation = 0.35f;

    /// <summary>Drops striking the arch and the body, per second per tyre at the reference.</summary>
    public static float ImpactsPerSecond = 3000f;
    /// <summary>Their share of the ejection's power at the reference, as dB below it.</summary>
    public static float ImpactDb = -6f;
    /// <summary>One impact's contact (s): a millimetre drop at road speed.</summary>
    public static float ImpactRiseSeconds = 6e-5f, ImpactTailSeconds = 2.5e-4f;

    /// <summary>The bow wave's swish at the reference speed on a millimetre of film above the texture,
    /// dB SPL at a metre, and its band, Hz.</summary>
    public static float BowDb = 66f;
    public static float BowLowHz = 150f, BowHighHz = 1800f;

    /// <summary>The splash entering a puddle: the burst's level for a 10 mm step at the reference speed,
    /// dB SPL at a metre (rms at its top), and the drops falling back (count per 10 mm step).</summary>
    public static float SplashDb = 92f;
    public static float FallbackDrops = 120f;

    /// <summary>What reaches the cabin: the arch impacts and a share of the hiss through the wheelhouse
    /// and the floor's trim, a low-pass at this corner (Hz) and this gain (dB) re the outside at a metre.</summary>
    public static float CabinCornerHz = 2500f, CabinDb = -14f;

    /// <summary>The smallest water that makes any of this, mm: under it the road is dry to a tyre.</summary>
    public const float DryBelowMm = 0.02f;

    // ── State ───────────────────────────────────────────────────────────────────────────────────

    private readonly float _rate;
    private readonly int _n;
    private readonly bool[] _front;
    private readonly float[] _widthShare;     // width / reference, power
    private readonly float[] _radius;
    private readonly float[] _water, _gain, _film, _lastWater;
    private readonly float[] _hp1, _hp2, _hpIn1, _hpIn2, _lp1, _lp2, _bowLp, _bowHp;
    private readonly float[] _ampE, _ampB;
    private readonly double[] _groove;
    private readonly int[] _grooves;
    private readonly EventSum _frontEvents, _rearEvents;
    private readonly float[] _cabinShare;
    private uint _rng;
    private float _hpA, _lpA, _bowLpA, _bowHpA, _cabinA, _cabinLp;
    private float _speed;
    private bool _active;
    private readonly float _cabinGain;

    /// <summary>This sample's pressure at the front tap, the rear tap, and in the cabin, Pa at a metre.</summary>
    public float Front, Rear, Cabin;

    // Each wheel into the cabin through its own arch (CabinPaths): its own event sum and low-pass. The
    // sum over the wheels is exactly the one Cabin signal (the low-pass is linear). Made the first time
    // anyone sits in the vehicle.
    private sealed class Corners
    {
        public readonly EventSum[] Events;
        public readonly float[] Lp, Out;
        public Corners(int n, float rate, int seed)
        {
            Events = new EventSum[n];
            for (int i = 0; i < n; i++) Events[i] = new EventSum(rate, seed + 7 + i);
            Lp = new float[n]; Out = new float[n];
        }
    }
    private Corners? _corners;
    /// <summary>The corners latched in <see cref="Block"/>, so a block never changes its mind half way.</summary>
    private Corners? _cornersNow;
    private readonly int _seed;

    /// <summary>Keeps each wheel's share of the cabin apart from now on. Game thread; allocates.</summary>
    public void EnableCorners()
    {
        if (Volatile.Read(ref _corners) == null) Volatile.Write(ref _corners, new Corners(_n, _rate, _seed));
    }

    /// <summary>This sample's spray in the cabin through wheel <paramref name="i"/>'s arch, Pa (zero
    /// before <see cref="EnableCorners"/>, when it is all in <see cref="Cabin"/>).</summary>
    public float CabinWheel(int i) => _cornersNow is { } c && i < c.Out.Length ? c.Out[i] : 0f;

    /// <summary>What the cabin still hears of the drops thrown before the wheels were kept apart.</summary>
    public float CabinTail;

    /// <summary>Whether anything is wet: when not, <see cref="Step"/> costs a branch.</summary>
    public bool Active => _active;

    /// <param name="v">The vehicle: its running gear lays out the wheels as the server's model does.</param>
    /// <param name="wheelFront">Per wheel, in the server's order (WheelDynamics), whether it is in the front group.</param>
    /// <param name="wheelAxle">Per wheel, its axle's index in the chassis.</param>
    /// <param name="sampleRate">The producer's rate, Hz.</param>
    /// <param name="seed">Seeds the noise and the event sums.</param>
    public WetTyres(VehicleProfile v, bool[] wheelFront, int[] wheelAxle, float sampleRate, int seed)
    {
        _rate = sampleRate;
        _n = wheelFront.Length;
        _front = (bool[])wheelFront.Clone();
        _widthShare = new float[_n]; _radius = new float[_n]; _grooves = new int[_n];
        _water = new float[_n]; _gain = new float[_n]; _film = new float[_n]; _lastWater = new float[_n];
        _hp1 = new float[_n]; _hp2 = new float[_n]; _hpIn1 = new float[_n]; _hpIn2 = new float[_n];
        _lp1 = new float[_n]; _lp2 = new float[_n]; _bowLp = new float[_n]; _bowHp = new float[_n];
        _ampE = new float[_n]; _ampB = new float[_n]; _groove = new double[_n]; _cabinShare = new float[_n];
        var axles = v.Running.Axles;
        for (int i = 0; i < _n; i++)
        {
            var axle = axles[Math.Clamp(wheelAxle[i], 0, axles.Length - 1)];
            // Duals throw twice the water from one place.
            _widthShare[i] = axle.Tyre.WidthMm / 1000f / ReferenceWidth * Math.Max(1, axle.TyresPerWheel);
            _radius[i] = MathF.Max(0.1f, axle.Tyre.RollingRadiusMetres);
            // The grooves empty once per tread block.
            _grooves[i] = Math.Max(0, v.Tyres.TreadBlocks);
            _gain[i] = 1f;
            _cabinShare[i] = 1f;
        }
        _frontEvents = new EventSum(sampleRate, seed);
        _rearEvents = new EventSum(sampleRate, seed + 1);
        _rng = (uint)seed * 2654435761u | 1u;
        _seed = seed;
        _cabinGain = MathF.Pow(10f, CabinDb / 20f);
        _cabinA = 1f - MathF.Exp(-2f * MathF.PI * CabinCornerHz / sampleRate);
        _bowLpA = 1f - MathF.Exp(-2f * MathF.PI * BowHighHz / sampleRate);
        _bowHpA = 1f - MathF.Exp(-2f * MathF.PI * BowLowHz / sampleRate);
        _hpA = MathF.Exp(-2f * MathF.PI * EjectionLowHz / sampleRate);
    }

    /// <summary>The level a quoted dB at a metre is, Pa rms.</summary>
    private static float Pa(float db) => 20e-6f * MathF.Pow(10f, db / 20f);

    /// <summary>
    /// Each wheel's water (mm, RoadWater), gain against its tap and texture depth, and the road speed;
    /// places this block's drops and splashes in the next <paramref name="count"/> samples.
    /// </summary>
    public void Block(ReadOnlySpan<float> waterMm, ReadOnlySpan<float> gain, ReadOnlySpan<float> textureMm, float speed, int count)
    {
        _speed = MathF.Abs(speed);
        _cornersNow = Volatile.Read(ref _corners);
        bool any = false;
        float u = _speed / ReferenceSpeed;
        float hiHz = MathF.Min(0.45f * _rate, EjectionHighHz * MathF.Pow(MathF.Max(0.2f, u), BrightnessExponent));
        _lpA = 1f - MathF.Exp(-2f * MathF.PI * hiHz / _rate);
        // The band's noise bandwidth, so the rms comes out as declared.
        float band = MathF.Max(1e-4f, (hiHz * MathF.PI / 4f - EjectionLowHz * 0.5f) / (0.5f * _rate));
        float bowBand = MathF.Max(1e-4f, (BowHighHz * MathF.PI / 2f - BowLowHz) / (0.5f * _rate));
        float eRef = Pa(EjectionDb), bRef = Pa(BowDb);
        float impactShare = MathF.Pow(10f, ImpactDb / 10f);
        for (int i = 0; i < _n; i++)
        {
            float w = i < waterMm.Length && float.IsFinite(waterMm[i]) ? MathF.Max(0f, waterMm[i]) : 0f;
            float tex = i < textureMm.Length ? textureMm[i] : 0.7f;
            _gain[i] = i < gain.Length ? gain[i] : 1f;
            float last = _lastWater[i];
            _lastWater[i] = w;
            _water[i] = w;
            _film[i] = MathF.Max(0f, w - tex);
            if (w < DryBelowMm || _speed < 0.5f) { _ampE[i] = _ampB[i] = 0f; continue; }
            any = true;
            float power = _widthShare[i] * MathF.Pow(w / ReferenceWaterMm, WaterExponent) * MathF.Pow(u, SpeedExponent);
            // The dense part carries what the impacts do not.
            _ampE[i] = eRef * MathF.Sqrt(power * (1f - impactShare) / band) * 1.7320508f;
            _ampB[i] = _film[i] > 0f ? bRef * MathF.Sqrt(_widthShare[i] * _film[i] * u * u * u / bowBand) * 1.7320508f : 0f;

            // A Poisson count of drops: their number follows the water swept, each one's size the speed.
            var sum = _cornersNow != null ? _cornersNow.Events[i] : _front[i] ? _frontEvents : _rearEvents;
            float rate = ImpactsPerSecond * _widthShare[i] * (w / ReferenceWaterMm) * u;
            int drops = sum.Poisson(rate * count / _rate);
            if (drops > 0)
            {
                // A pulse of peak P, rise s and tail t carries about P² (s √π + t / 2).
                float energyEach = ImpactRiseSeconds * 1.7724539f + 0.5f * ImpactTailSeconds;
                float meanSq = eRef * eRef * power * impactShare;
                float peak = MathF.Sqrt(meanSq / MathF.Max(1f, rate) / energyEach) * _gain[i];
                for (int d = 0; d < drops; d++)
                {
                    // Lognormal sizes: a spray's drops spread over a decade (the impacts' peaks over two).
                    float g = Gaussian(sum);
                    float p = peak * MathF.Exp(0.8f * g - 0.32f);
                    int at = (int)(sum.Uniform() * count);
                    float rise = ImpactRiseSeconds * MathF.Exp(0.4f * Gaussian(sum)) / MathF.Max(0.3f, u);
                    sum.Impact(at, rise, ImpactTailSeconds, p);
                }
            }

            // A puddle: the water under it rose by more than a texture's worth in a block.
            float step = w - last;
            if (step > 2f && _speed > 1.5f)
            {
                float k = step / 10f;
                float pa = Pa(SplashDb) * MathF.Sqrt(_widthShare[i] * k) * MathF.Pow(u, 1.5f) * _gain[i];
                int at = (int)(sum.Uniform() * count);
                sum.Burst(at, 0.004f, 0.05f + 0.02f * MathF.Min(3f, k), pa, 250f, 7000f);
                // The sheet falls back as drops for as long as it flies (2 v sin / g, thrown up at about
                // a tenth of the road speed): a few tenths of a second.
                float flight = MathF.Min(0.6f, 2f * 0.12f * _speed / 9.81f + 0.1f);
                int n = sum.Poisson(FallbackDrops * MathF.Min(3f, k) * MathF.Min(1.5f, u));
                for (int d = 0; d < n; d++)
                {
                    int t = at + (int)((0.05f + flight * MathF.Sqrt(sum.Uniform())) * _rate);
                    if (t >= sum.Horizon - 4096) continue;
                    float r = 0.0005f + 0.0015f * sum.Uniform();            // bubble radius, m
                    float hz = 3.26f / r;
                    float amp = pa * 0.05f * (0.3f + sum.Uniform());
                    if (sum.Uniform() < 0.4f) sum.Bubble(t, hz, 0.04f, amp, 0.1f);
                    sum.Impact(t, 1e-4f, 3e-4f, amp * 0.7f);
                }
            }
        }
        _active = any || _tailSamples > 0;
        _tailSamples = any ? (int)(0.8f * _rate) : Math.Max(0, _tailSamples - count);
    }

    /// <summary>Samples the event rings may still ring for after the last wet block.</summary>
    private int _tailSamples;

    /// <summary>One sample: <see cref="Front"/>, <see cref="Rear"/> and <see cref="Cabin"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step()
    {
        var corners = _cornersNow;
        if (!_active)
        {
            Front = Rear = Cabin = CabinTail = 0f;
            if (corners != null) Array.Clear(corners.Out);
            return;
        }
        float front = _frontEvents.Next(), rear = _rearEvents.Next();
        float cabinIn = front + rear;
        if (corners != null)
        {
            for (int i = 0; i < _n; i++)
            {
                float e = corners.Events[i].Next();
                if (_front[i]) front += e; else rear += e;
                corners.Out[i] = e;
            }
        }
        for (int i = 0; i < _n; i++)
        {
            if (_ampE[i] <= 0f && _ampB[i] <= 0f) continue;
            float x = Signed();
            _hp1[i] = _hpA * (_hp1[i] + x - _hpIn1[i]); _hpIn1[i] = x;
            _hp2[i] = _hpA * (_hp2[i] + _hp1[i] - _hpIn2[i]); _hpIn2[i] = _hp1[i];
            _lp1[i] += _lpA * (_hp2[i] - _lp1[i]);
            _lp2[i] += _lpA * (_lp1[i] - _lp2[i]);
            // The hiss pulses at the tread's pitch rate as the grooves empty.
            float mod = 1f;
            if (_grooves[i] > 0)
            {
                _groove[i] += _speed / (2f * MathF.PI * _radius[i]) * _grooves[i] / _rate;
                if (_groove[i] >= 1.0) _groove[i] -= Math.Floor(_groove[i]);
                mod = 1f + GrooveModulation * (1f - 4f * MathF.Abs((float)_groove[i] - 0.5f));
            }
            float y = _lp2[i] * _ampE[i] * mod;
            if (_ampB[i] > 0f)
            {
                float z = Signed();
                _bowLp[i] += _bowLpA * (z - _bowLp[i]);
                _bowHp[i] += _bowHpA * (_bowLp[i] - _bowHp[i]);
                y += (_bowLp[i] - _bowHp[i]) * _ampB[i];
            }
            y *= _gain[i];
            if (_front[i]) front += y; else rear += y;
            if (corners != null) corners.Out[i] += y * 0.5f * _cabinShare[i];
            else cabinIn += y * 0.5f * _cabinShare[i];
        }
        _cabinLp += _cabinA * (cabinIn - _cabinLp);
        Front = front;
        Rear = rear;
        if (corners == null)
        {
            Cabin = _cabinLp * _cabinGain;
            return;
        }
        // Each wheel through its own low-pass, plus the two old sums' last drops: together exactly Cabin.
        float sum = 0f;
        for (int i = 0; i < _n; i++)
        {
            corners.Lp[i] += _cabinA * (corners.Out[i] - corners.Lp[i]);
            corners.Out[i] = corners.Lp[i] * _cabinGain;
            sum += corners.Out[i];
        }
        CabinTail = _cabinLp * _cabinGain;
        Cabin = sum + CabinTail;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Signed()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (int)_rng * (1f / 2147483648f);
    }

    private static float Gaussian(EventSum s)
        => MathF.Sqrt(-2f * MathF.Log(MathF.Max(1e-7f, s.Uniform()))) * MathF.Cos(MathF.Tau * s.Uniform());
}
