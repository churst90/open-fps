using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Engine;

namespace OpenFPS.Client.AudioEngine.Core.Rail;

/// <summary>
/// The traction package of an electric train: a motor, the gears between it and the axle, and the
/// inverter feeding it. Three sounds, and all three are counts of teeth and poles.
///
/// THE GEARS. A pinion of a few teeth drives a wheel of many, and every tooth entering mesh is a
/// little step in the torque. So there is a tone at the pinion's tooth count times the motor's
/// revolutions a second — a couple of kilohertz at speed — sliding smoothly up and down with the
/// train, with sidebands a shaft-rate apart because no gear is perfectly round. That whine is the
/// single most recognisable thing about an electric train and it is one integer.
///
/// THE MOTOR. The magnetic pull across the air gap does not care which way round the field is, so it
/// pulses at TWICE the electrical frequency: a four-pole motor at 3,000 rpm hums at 200 Hz. On top
/// of that the rotor's teeth sweep past the stator's slots, which is a much higher and thinner tone.
///
/// THE INVERTER, which is the modern part and the strange one. A drive switching at a fixed carrier
/// makes tones at that carrier and at sidebands a couple of output-frequencies away: a standing
/// train at a platform whistling on one steady note is switching asynchronously. As the train speeds
/// up, holding a fixed carrier would mean more and more switchings per output cycle and more and
/// more heat, so the drive LOCKS the carrier to a whole number of pulses per cycle and steps that
/// number down — 27, 15, 9, 5, 3, 1 — as the frequency climbs. Inside each mode the tone rises with
/// the train; at each change it drops. That staircase is not a sound effect anybody designed, it is
/// an engineer keeping switching losses down, and it comes out of this model because the model
/// chooses the pulse count the way the drive does.
/// </summary>
internal sealed class ElectricDrive
{
    private readonly ElectricDriveSpec _s;
    private readonly float _rate, _dt, _wheelDiameter;
    private readonly Random _rng;
    private double _meshPhase, _shaftPhase, _humPhase, _slotPhase;
    private double _carrierPhase, _sideLowPhase, _sideHighPhase;
    private float _blow1, _blow2, _hp;
    private readonly float _meshAmp, _humAmp, _slotAmp, _invAmp, _blowerAmp;
    private float _carrierNow;
    private int _pulses = 1;
    private float _gearWobble;

    /// <summary>How hard the motors are working, -1 braking to +1 motoring. The magnetic tones go
    /// with the current; the gear whine goes with the torque, and a coasting train is much quieter
    /// than one under power at the same speed.</summary>
    public float Effort { get; set; } = 1f;

    public float MotorRpm { get; private set; }
    public float MeshHz { get; private set; }
    public float CarrierHz => _carrierNow;
    public int PulseMode => _pulses;

    public ElectricDrive(ElectricDriveSpec s, float wheelDiameter, float rate, int seed)
    {
        _s = s; _rate = rate; _dt = 1f / rate; _wheelDiameter = MathF.Max(0.2f, wheelDiameter);
        _rng = new Random(seed);
        _meshAmp = Db(s.GearLevelDb);
        _humAmp = Db(s.MotorHumDb);
        _slotAmp = Db(s.MotorHumDb - 8f);
        _invAmp = s.InverterLevelDb > 1f ? Db(s.InverterLevelDb) : 0f;
        _blowerAmp = Db(s.BlowerDb);
    }

    private static float Db(float db) => 20e-6f * MathF.Pow(10f, db / 20f);

    /// <summary>Output, pascals at one metre.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public float Step(float speedMps)
    {
        float axleHz = MathF.Abs(speedMps) / (MathF.PI * _wheelDiameter);
        float ratio = _s.GearTeeth / (float)Math.Max(1, _s.PinionTeeth);
        float motorHz = axleHz * ratio;
        MotorRpm = motorHz * 60f;
        float fe = motorHz * _s.PolePairs;
        MeshHz = axleHz * _s.GearTeeth;    // = motorHz * pinionTeeth, the same number two ways

        float effort = Math.Clamp(MathF.Abs(Effort), 0f, 1.2f);

        // ── the gears ───────────────────────────────────────────────────────────────────────────
        _shaftPhase += motorHz * _dt; if (_shaftPhase > 1) _shaftPhase -= 1;
        // No gear is round and no shaft runs true: a slow wobble at the shaft rate, which is what
        // puts the sidebands either side of the mesh tone and stops it being a test signal.
        _gearWobble = 0.18f * MathF.Sin(MathF.Tau * (float)_shaftPhase);
        _meshPhase += MeshHz * _dt; if (_meshPhase > 1) _meshPhase -= 1;
        float gear = (MathF.Sin(MathF.Tau * (float)_meshPhase)
                    + 0.35f * MathF.Sin(2f * MathF.Tau * (float)_meshPhase)
                    + 0.12f * MathF.Sin(3f * MathF.Tau * (float)_meshPhase))
                   * (1f + _gearWobble) * _meshAmp * (0.25f + 0.75f * effort)
                   * MathF.Min(1f, MeshHz / 120f);

        // ── the motor ───────────────────────────────────────────────────────────────────────────
        _humPhase += 2f * fe * _dt; if (_humPhase > 1) _humPhase -= 1;
        _slotPhase += _s.StatorSlots * motorHz * _dt; if (_slotPhase > 1) _slotPhase -= 1;
        float motor = (MathF.Sin(MathF.Tau * (float)_humPhase) + 0.3f * MathF.Sin(2f * MathF.Tau * (float)_humPhase)) * _humAmp * effort
                    + MathF.Sin(MathF.Tau * (float)_slotPhase) * _slotAmp * effort * MathF.Min(1f, motorHz / 15f);

        // ── the inverter ────────────────────────────────────────────────────────────────────────
        float inv = 0f;
        if (_invAmp > 0f)
        {
            if (fe < _s.SyncFromHz || _s.CarrierHz <= 0f)
            {
                // Asynchronous: a fixed carrier, whatever the motor is doing. This is the steady
                // note a train sits on at a platform.
                _carrierNow = _s.CarrierHz;
                _pulses = 0;
            }
            else
            {
                // Synchronous: the largest whole number of pulses per output cycle that keeps the
                // switching frequency at or under the carrier. As the train speeds up the drive runs
                // out of room and steps down, and the tone drops.
                _pulses = _s.PulseModes[^1];
                foreach (int n in _s.PulseModes)
                    if (n * fe <= _s.CarrierHz * 1.15f) { _pulses = n; break; }
                _carrierNow = _pulses * fe;
            }
            _carrierPhase += _carrierNow * _dt; if (_carrierPhase > 1) _carrierPhase -= 1;
            _sideLowPhase += MathF.Max(1f, _carrierNow - 2f * fe) * _dt; if (_sideLowPhase > 1) _sideLowPhase -= 1;
            _sideHighPhase += (_carrierNow + 2f * fe) * _dt; if (_sideHighPhase > 1) _sideHighPhase -= 1;
            inv = (0.55f * MathF.Sin(MathF.Tau * (float)_carrierPhase)
                 + 0.4f * MathF.Sin(MathF.Tau * (float)_sideLowPhase)
                 + 0.4f * MathF.Sin(MathF.Tau * (float)_sideHighPhase)) * _invAmp * (0.3f + 0.7f * effort);
        }

        // ── the blower ──────────────────────────────────────────────────────────────────────────
        float n2 = (float)(_rng.NextDouble() * 2 - 1);
        float a = OnePole.AlphaFor(900f, _rate);
        _blow1 += a * (n2 - _blow1); _blow2 += a * (_blow1 - _blow2);
        float blower = (_blow1 - _blow2) * 9f * _blowerAmp;

        float y = gear + motor + inv + blower;
        _hp += OnePole.AlphaFor(30f, _rate) * (y - _hp);
        return y - _hp;
    }

    public IEnumerable<string> Describe(float speed)
    {
        float axleHz = speed / (MathF.PI * _wheelDiameter);
        float motorHz = axleHz * _s.GearTeeth / Math.Max(1, _s.PinionTeeth);
        yield return $"drive: {_s.PinionTeeth}:{_s.GearTeeth} ({_s.GearTeeth / (float)_s.PinionTeeth:F2}:1), "
                   + $"{_s.PolePairs * 2} pole, {_s.StatorSlots} slots";
        yield return $"  at {speed * 3.6f:F0} km/h: motor {motorHz * 60f:F0} rpm, mesh {axleHz * _s.GearTeeth:F0} Hz, "
                   + $"hum {2f * motorHz * _s.PolePairs:F0} Hz, slots {_s.StatorSlots * motorHz:F0} Hz";
        if (_s.InverterLevelDb > 1f)
            yield return $"  inverter: {_s.CarrierHz:F0} Hz carrier asynchronous below {_s.SyncFromHz:F0} Hz output, "
                       + $"then pulse modes {string.Join("-", _s.PulseModes)}";
    }
}
