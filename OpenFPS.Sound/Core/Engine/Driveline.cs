using OpenFPS.Common;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// The car behind the engine: gearbox, clutch, mass, drag, brakes. One degree of freedom when the
/// clutch is up (crank and wheels one shaft through the ratio), two when it is down.
///
/// The engine integrates its own speed; the driveline tells it the inertia it drags and the torque
/// resisting, and reads road speed back off the crank. So an upshift drops the revs by the ratio
/// step, and lifting off at speed spins the engine down through the gearing, not to idle.
/// </summary>
public sealed class Driveline
{
    public readonly VehicleProfile Vehicle;
    private readonly Gearbox _gb;

    /// <summary>Road speed, m/s.</summary>
    public float Speed { get; private set; }
    public float Distance { get; private set; }
    /// <summary>Gear engaged, 0 for neutral.</summary>
    public int Gear { get; set; }
    /// <summary>Clutch engagement, 0 (down) to 1 (up).</summary>
    public float Clutch { get; set; } = 1f;
    /// <summary>Brake, 0..1 of maximum deceleration.</summary>
    public float Brake { get; set; }
    /// <summary>Whether the engine is locked to the wheels this sample.</summary>
    public bool Locked { get; private set; }
    /// <summary>A dynamometer: when above zero, the crank is held near this speed by a viscous brake
    /// and a large inertia, whatever the throttle. For measurement only.</summary>
    public float DynoRpm { get; set; }

    /// <summary>Peak braking deceleration, m/s^2.</summary>
    public float MaxBrake { get; set; } = 7.5f;
    /// <summary>What the clutch can pass when fully up, Nm — well above the engine's peak.</summary>
    public float ClutchCapacityNm { get; set; }

    public Driveline(VehicleProfile v)
    {
        Vehicle = v;
        _gb = v.Gearbox;
        ClutchCapacityNm = v.Engine.PeakTorqueNm * 1.6f;
    }

    /// <summary>Engine speed the wheels demand in a gear at the current road speed.</summary>
    public float GearRpm(int gear) => _gb.RpmFor(Speed, gear);
    public float Ratio(int gear) => gear >= 1 && gear <= _gb.TopGear ? _gb.Ratios[gear - 1] * _gb.FinalDrive : 0f;

    /// <summary>One sample: couples the engine to the road, steps the engine, and moves the car.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Step(EngineSynth engine, float dt)
    {
        var v = Vehicle;
        float r = _gb.WheelRadiusMetres;
        float i = Ratio(Gear);
        float drag = 0.5f * 1.225f * v.DragArea * Speed * Speed;
        float roll = Speed > 0.05f ? v.RollingResistance * v.MassKg * 9.81f : 0f;
        float brake = Brake * MaxBrake * v.MassKg;
        float resist = drag + roll + brake;                 // N, against motion

        float omegaEngine = engine.Rpm * 2f * MathF.PI / 60f;
        float omegaWheelAtCrank = Speed / r * i;             // what the crank would do if locked

        if (DynoRpm > 0f)
        {
            float omegaTarget = DynoRpm * 2f * MathF.PI / 60f;
            // Stiff enough to hold within a few rpm at full throttle, in both directions.
            float k = MathF.Max(2f, v.Engine.PeakTorqueNm * 1.5f / (omegaTarget * 0.03f));
            engine.ExternalInertia = 2f;
            engine.LoadTorque = Math.Clamp(k * (omegaEngine - omegaTarget), -v.Engine.PeakTorqueNm * 6f, v.Engine.PeakTorqueNm * 6f);
            engine.Step();
            Locked = false;
            Speed = 0f;
            return;
        }

        bool engaged = Gear >= 1 && Clutch > 0.02f;
        // Locked when the clutch is up and the two speeds agree; slipping otherwise.
        bool locked = engaged && Clutch > 0.98f && MathF.Abs(omegaEngine - omegaWheelAtCrank) < MathF.Max(6f, 0.03f * omegaEngine);
        Locked = locked;

        if (locked)
        {
            // One shaft. The engine carries the car's mass reflected through the ratio, and the
            // road's resistance reflected the same way.
            engine.ExternalInertia = v.MassKg * r * r / (i * i);
            engine.LoadTorque = resist * r / i;
            engine.Step();
            float omega = engine.Rpm * 2f * MathF.PI / 60f;
            Speed = MathF.Max(0f, omega * r / i);
        }
        else if (engaged)
        {
            // Slipping clutch: it passes a torque set by how far it is engaged, in the direction that
            // pulls the two speeds together. The engine sees that torque; the car sees it through
            // the ratio. This is what a launch is: revs held while the road catches up.
            float capacity = ClutchCapacityNm * Clutch;
            float sign = omegaEngine > omegaWheelAtCrank ? 1f : -1f;
            float passed = capacity * sign;
            engine.ExternalInertia = 0f;
            engine.LoadTorque = passed;
            engine.Step();
            float drive = passed * i / r;
            float accel = (drive - resist) / v.MassKg;
            Speed = MathF.Max(0f, Speed + accel * dt);
        }
        else
        {
            engine.ExternalInertia = 0f;
            engine.LoadTorque = 0f;
            engine.Step();
            float accel = -resist / v.MassKg;
            Speed = MathF.Max(0f, Speed + accel * dt);
        }
        Distance += Speed * dt;
    }

    /// <summary>Puts the car at a road speed without driving it there — for placing a scene.</summary>
    public void Teleport(float speed) { Speed = MathF.Max(0f, speed); }
}

/// <summary>What the driver is doing, moment to moment.</summary>
public enum DriverAction
{
    /// <summary>Engine off and still.</summary>
    Off,
    /// <summary>Starter turning it over.</summary>
    Cranking,
    /// <summary>Running, clutch in, not going anywhere.</summary>
    Idling,
    /// <summary>Throttle open, accelerating through the gears to a target speed.</summary>
    Accelerating,
    /// <summary>Holding a speed.</summary>
    Cruising,
    /// <summary>Off the throttle, in gear.</summary>
    Coasting,
    /// <summary>On the brakes.</summary>
    Braking,
    /// <summary>Ignition off.</summary>
    ShuttingDown,
    /// <summary>Held at an exact engine speed on a dyno at a chosen throttle. Exists so the
    /// synthesis can be measured at a steady state.</summary>
    Holding,
    /// <summary>Free-revving in neutral to a target rpm, held, then released.</summary>
    Revving,
}

/// <summary>One instruction in a drive: do this for this long. TargetSpeed is m/s for driving
/// orders and rpm for Holding and Revving. Throttle below zero means "whatever it takes".</summary>
public readonly record struct DriveOrder(DriverAction Action, float Seconds, float TargetSpeed = 0f, float Throttle = -1f);

/// <summary>
/// The right foot and the left hand: turns a drive order into throttle, clutch, gear and brake,
/// sample by sample. Shifts are a real sequence — throttle off, clutch down, gear, clutch up — with
/// the engine falling on its own inertia in the middle, which is where the gap in the note comes from.
/// </summary>
public sealed class Driver
{
    private readonly Driveline _dl;
    private readonly EngineSynth _engine;
    private readonly Gearbox _gb;
    private float _shiftTimer;
    private int _shiftTo;
    private float _launchRpm;
    private float _holdIntegral;
    private float _revIntegral;
    private float _revPrevRpm, _revRate;

    /// <summary>How long the plate takes to answer the pedal — the lag EngineSynth puts on it. Here
    /// because a governor that does not lead its own actuator is not a governor.</summary>
    private const float PedalLagSeconds = 0.04f;
    public bool Shifting => _shiftTimer > 0f;

    public Driver(Driveline dl, EngineSynth engine)
    {
        _dl = dl;
        _engine = engine;
        _gb = dl.Vehicle.Gearbox;
        _launchRpm = MathF.Max(engine.Profile.IdleRpm * 2.2f, engine.Profile.PeakTorqueRpm * 0.45f);
    }

    /// <summary>Applies one sample of an order that has been running for <paramref name="phase"/> seconds.</summary>
    public void Apply(DriveOrder order, float phase, float dt)
    {
        var e = _engine.Profile;
        if (order.Action != DriverAction.Holding) _dl.DynoRpm = 0f;
        switch (order.Action)
        {
            case DriverAction.Off:
                _engine.Ignition = false; _engine.Starter = false; _engine.Throttle = 0f;
                _dl.Gear = 0; _dl.Clutch = 1f; _dl.Brake = 1f;
                return;
            case DriverAction.Cranking:
                _engine.Ignition = false; _engine.Starter = true; _engine.Throttle = 0f;
                _dl.Gear = 0; _dl.Clutch = 1f; _dl.Brake = 1f;
                return;
            case DriverAction.Idling:
                _engine.Ignition = true;
                // The starter stays in until it catches.
                _engine.Starter = _engine.Rpm < e.CrankingRpm * 1.6f && phase < 3f;
                _engine.Throttle = 0f;
                _dl.Gear = 0; _dl.Clutch = 1f; _dl.Brake = 1f;
                return;
            case DriverAction.ShuttingDown:
                _engine.Ignition = false; _engine.Starter = false; _engine.Throttle = 0f;
                _dl.Gear = 0; _dl.Clutch = 1f; _dl.Brake = 1f;
                return;
            case DriverAction.Holding:
            {
                // A dyno: a viscous brake about the target and a great deal of inertia, so the crank
                // sits where it is put and the throttle decides the load, not the speed.
                _engine.Ignition = true; _engine.Starter = _engine.Rpm < 100f;
                float target = order.TargetSpeed > 0 ? order.TargetSpeed : e.IdleRpm;
                _dl.Gear = 0; _dl.Clutch = 1f; _dl.Brake = 1f;
                if (order.Throttle >= 0f)
                {
                    _dl.DynoRpm = target;
                    _engine.Throttle = order.Throttle;
                }
                else
                {
                    // No brake: whatever throttle holds the target unloaded, by a slow integral.
                    _dl.DynoRpm = 0f;
                    float err = (target - _engine.Rpm) / target;
                    _holdIntegral = Math.Clamp(_holdIntegral + err * 2.5f * dt, 0f, 1f);
                    _engine.Throttle = Math.Clamp(_holdIntegral + err * 0.8f, 0f, 1f);
                }
                return;
            }
            case DriverAction.Revving:
            {
                _engine.Ignition = true; _engine.Starter = false;
                _dl.Gear = 0; _dl.Clutch = 1f; _dl.Brake = 1f;
                float target = order.TargetSpeed > 0 ? order.TargetSpeed : e.IdleRpm;
                float release = order.Seconds * 0.62f;
                if (phase < release)
                {
                    // Governed on where the crank is heading, not where it is. An unloaded F1 engine
                    // (0.035 kg m^2) gains rpm at five figures a second, so a governor on the needle
                    // put every blip at the limiter. The lead is the whole chain to torque: two
                    // revolutions of combustion (30 ms at 4,000 rpm, 8 ms at 15,500) plus the plate's
                    // 40 ms lag (PedalLagSeconds), the larger part; leading the combustion alone was
                    // worth almost nothing.
                    float rate = (_engine.Rpm - _revPrevRpm) / MathF.Max(dt, 1e-6f);
                    _revPrevRpm = _engine.Rpm;
                    _revRate += (rate - _revRate) * MathF.Min(1f, dt * 200f);
                    float lead = PedalLagSeconds + 2f * 60f / MathF.Max(400f, _engine.Rpm);
                    float predicted = _engine.Rpm + _revRate * lead;

                    // The band is on the engine's own scale: 900 rpm is a sixth of a diesel's range
                    // and a seventeenth of an F1's.
                    float err = (target - predicted) / MathF.Max(150f, 0.06f * e.RedlineRpm);
                    _revIntegral = Math.Clamp(_revIntegral + err * 1.5f * dt, -0.3f, 0.6f);
                    float t = Math.Clamp(err + _revIntegral, 0f, 1f);
                    _engine.Throttle = t * (1f + 0.03f * MathF.Sin(phase * 17f));
                }
                else
                {
                    _engine.Throttle = 0f;
                    _revIntegral = 0f;
                    _revRate = 0f;
                    _revPrevRpm = _engine.Rpm;
                }
                return;
            }
        }

        // ── Driving ─────────────────────────────────────────────────────────────────────────
        _engine.Ignition = true; _engine.Starter = false;
        _dl.Brake = order.Action == DriverAction.Braking ? 0.7f : 0f;
        float targetSpeed = order.TargetSpeed;

        if (_shiftTimer > 0f)
        {
            _shiftTimer -= dt;
            _engine.Throttle = 0f;
            _dl.Clutch = 0f;
            if (_shiftTimer <= 0f)
            {
                _dl.Gear = _shiftTo;
                _dl.Clutch = 1f;
            }
            return;
        }

        if (_dl.Gear == 0 && order.Action != DriverAction.Braking && order.Action != DriverAction.Coasting)
            _dl.Gear = 1;

        // Throttle by intent.
        float throttle = order.Action switch
        {
            DriverAction.Accelerating => _dl.Speed < targetSpeed ? 1f : 0.2f,
            DriverAction.Cruising => Math.Clamp(0.18f + (targetSpeed - _dl.Speed) * 0.25f, 0.05f, 0.6f),
            _ => 0f,
        };

        // The clutch: slipping to launch, otherwise up.
        float gearRpm = _dl.GearRpm(_dl.Gear);
        if (_dl.Gear == 1 && throttle > 0.1f && gearRpm < _launchRpm * 0.95f)
        {
            // Hold the engine near launch revs with the clutch, feeding it in as the road catches up.
            float over = (_engine.Rpm - _launchRpm) / _launchRpm;
            _dl.Clutch = Math.Clamp(0.25f + over * 3f, 0.08f, 1f);
            throttle = Math.Clamp(0.45f + (_launchRpm - _engine.Rpm) / 1500f, 0.2f, 0.9f);
        }
        else if (_dl.Gear >= 1 && gearRpm < e.IdleRpm * 0.9f && throttle < 0.05f)
        {
            // Rolling to a stop in gear: clutch in so it does not stall.
            _dl.Clutch = 0f;
        }
        else
        {
            _dl.Clutch = 1f;
        }
        _engine.Throttle = throttle;

        // Shifts.
        if (_dl.Locked && _engine.Rpm > _gb.UpshiftRpm && _dl.Gear < _gb.TopGear && throttle > 0.3f)
        {
            _shiftTo = _dl.Gear + 1;
            _shiftTimer = _gb.ShiftSeconds;
            _dl.Gear = 0;
            _engine.Throttle = 0f; _dl.Clutch = 0f;       // the shift starts with the foot off
        }
        else if (_dl.Gear > 1 && gearRpm < _gb.DownshiftRpm && _dl.Locked)
        {
            _shiftTo = _dl.Gear - 1;
            _shiftTimer = _gb.ShiftSeconds * 0.8f;
            _dl.Gear = 0;
            _engine.Throttle = 0f; _dl.Clutch = 0f;
        }
    }
}

/// <summary>
/// A driver told where the car is rather than what to do: follows a target road speed with throttle,
/// brake and gears. This is how a networked vehicle gets an engine: the throttle is whatever matches
/// the speed the server reports.
/// </summary>
public sealed class VirtualDriver
{
    private readonly Driveline _dl;
    private readonly EngineSynth _engine;
    private readonly Gearbox _gb;
    private float _shiftTimer;
    private int _shiftTo;
    private int _shiftFrom;
    private float _integral;
    private float _lastTarget;
    private float _accelEstimate;
    private readonly float _launchRpm;
    private const float ClutchLetInPerSecond = 2f;
    private const float LaunchRollOnPerSecond = 3f;
    private readonly float _launchBite;

    public VirtualDriver(Driveline dl, EngineSynth engine)
    {
        _dl = dl;
        _engine = engine;
        _gb = dl.Vehicle.Gearbox;
        // The least the clutch is held at while pulling away: the bite that pushes the vehicle at about
        // 1 m/s^2. That is eight per cent for a car and three for a bike (eight drove a bike at 3 m/s^2).
        _launchBite = Math.Clamp(dl.Vehicle.MassKg * 1f * _gb.WheelRadiusMetres
                                 / MathF.Max(1f, dl.Ratio(1) * dl.ClutchCapacityNm), 0.02f, 0.08f);
        _launchRpm = _gb.LaunchRpm ?? MathF.Max(engine.Profile.IdleRpm * 2f, engine.Profile.PeakTorqueRpm * 0.4f);
    }

    /// <summary>Target road speed, m/s, as reported from outside.</summary>
    public float TargetSpeed { get; set; }
    /// <summary>Whether the engine should be running at all.</summary>
    public bool Running { get; set; } = true;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Apply(float dt)
    {
        var e = _engine.Profile;
        if (!Running)
        {
            _engine.Ignition = false; _engine.Starter = false; _engine.Throttle = 0f;
            _dl.Gear = 0; _dl.Clutch = 1f; _dl.Brake = 1f;
            return;
        }
        _engine.Ignition = true;
        // The key is held until it fires and pulls away from the starter's speed, where an engine
        // computer cuts the starter (about 300 rpm). Released on speed alone, the crank passed 1.5
        // times cranking speed before the computer had synchronised: a starter blip and a silent coast.
        _engine.Starter = !_engine.Firing || _engine.Rpm < e.CrankingRpm * 1.5f;

        // Estimate the target's acceleration, so the throttle can lead rather than lag.
        _accelEstimate += ((TargetSpeed - _lastTarget) / MathF.Max(dt, 1e-4f) - _accelEstimate) * MathF.Min(1f, dt * 4f);
        _lastTarget = TargetSpeed;

        float err = TargetSpeed - _dl.Speed;
        if (_shiftTimer > 0f)
        {
            _shiftTimer -= dt;
            // Going down, the revs must rise to the lower gear: a blip matches them. With the throttle
            // shut they fell to idle and the clutch yanked them from 780 to 1,600 at once (the tunnel
            // truck's hard step coming down from 54).
            float match = _shiftTo >= 1 ? _dl.GearRpm(_shiftTo) : 0f;
            _engine.Throttle = match > _engine.Rpm ? Math.Clamp((match - _engine.Rpm) / 400f, 0f, 0.7f) : 0f;
            _dl.Clutch = 0f;
            // A change up ends with the clutch in; a change down only if the blip landed the revs on
            // the gear, otherwise it is let in below. A light bike overshot the blip, and closing at
            // once shoved it forward 3 km/h under braking.
            if (_shiftTimer <= 0f)
            {
                float gr = _dl.GearRpm(_shiftTo);
                bool down = _shiftTo < _shiftFrom;
                _dl.Gear = _shiftTo;
                _dl.Clutch = down && MathF.Abs(_engine.Rpm - gr) > 150f + 0.1f * gr ? 0f : 1f;
            }
            return;
        }

        if (TargetSpeed < 0.3f && _dl.Speed < 0.5f)
        {
            // Stopped: idle in neutral.
            _dl.Gear = 0; _dl.Clutch = 1f; _dl.Brake = 1f;
            _engine.Throttle = 0f;
            _integral = 0f;
            return;
        }
        if (_dl.Gear == 0) _dl.Gear = 1;

        // Throttle: proportional on the speed error plus what the acceleration demands, with a slow
        // integral so a cruise settles.
        float wantAccel = _accelEstimate + err * 0.9f;
        float mass = _dl.Vehicle.MassKg;
        float need = wantAccel * mass + 0.5f * 1.225f * _dl.Vehicle.DragArea * _dl.Speed * _dl.Speed
                   + _dl.Vehicle.RollingResistance * mass * 9.81f;
        float available = MathF.Max(50f, e.PeakTorqueNm * 0.85f * _dl.Ratio(_dl.Gear) / _gb.WheelRadiusMetres);
        // The integral only while it can act: locked in gear, and not pushing past a throttle already
        // shut or wide open. Wound to -0.3 on a pull-away overshoot, it held a bike 10 km/h slow for 2 s.
        float unclamped = need / available + _integral;
        if (_dl.Locked && !(unclamped <= 0f && err < 0f) && !(unclamped >= 1f && err > 0f))
            _integral = Math.Clamp(_integral + err * 0.08f * dt, -0.3f, 0.5f);
        float throttle = Math.Clamp(need / available + _integral, 0f, 1f);
        float brake = 0f;
        if (wantAccel < -0.8f) { brake = Math.Clamp(-wantAccel / 6f, 0f, 1f); throttle = 0f; }
        _dl.Brake = brake;

        float gearRpm = _dl.GearRpm(_dl.Gear);
        // Slowing below what the gear does at idle: clutch in, or the idle drives it on (a bike asked
        // to stop crept at 12 km/h, first gear at 1,300 rpm).
        bool stopping = throttle < 0.05f && wantAccel < -0.3f && (_accelEstimate < -0.1f || TargetSpeed < 0.3f)
                        && _gb.RpmFor(TargetSpeed, _dl.Gear) < e.IdleRpm * 1.1f;
        float clutchWas = _dl.Clutch;
        if (stopping)
        {
            _dl.Clutch = 0f;
        }
        // Pulling away, the clutch is let in by slipping whatever the throttle asks; only a clutch
        // already in stays in. Otherwise a light throttle held it out while a bike revved free to
        // 7,000 rpm, and the plain `else` closed it at a 5,000 rpm mismatch: ten km/h in a tenth of a second.
        else if (_dl.Gear == 1 && gearRpm < _launchRpm * 0.95f && (throttle > 0.05f || !_dl.Locked))
        {
            // Clutch and throttle hold the revs at the launch speed. No throttle floor: a fifth held
            // open whatever the revs took a 200 kg bike to its limiter pulling away.
            float over = (_engine.Rpm - _launchRpm) / _launchRpm;
            // Ahead of pace, clutch back toward the bite and foot off, gradually over 1 m/s of lead.
            // As a switch it hunted: a bike's revs swung 3,100 to 4,700 every 0.8 s.
            float onPace = Math.Clamp(1f + err * 1f, 0f, 1f);
            float hold = Math.Clamp(0.25f + over * 3f, _launchBite, 1f);
            _dl.Clutch = MathHelper.Lerp(_launchBite, hold, onPace);
            float toLaunch = Math.Clamp(0.4f + (_launchRpm - _engine.Rpm) / 1500f, 0f, 0.9f);
            throttle = MathHelper.Lerp(MathF.Min(toLaunch, throttle), toLaunch, onPace);
            // Rolled on, not snapped: nine tenths at once flared a bike to 4,300 rpm and the clutch
            // that caught it jolted it forward.
            throttle = MathF.Min(throttle, _engine.Throttle + dt * LaunchRollOnPerSecond);
        }
        else if (gearRpm < e.IdleRpm * 0.9f && throttle < 0.05f)
        {
            _dl.Clutch = 0f;
        }
        else _dl.Clutch = 1f;
        // A clutch is let in, not dropped: half a second from open to shut unless both sides already
        // turn together. Closed at 4,000 rpm on a gear at 1,400, a bike leapt 8 km/h and the speed
        // loop's recovery was heard as a lurch.
        float mismatch = MathF.Abs(_engine.Rpm - gearRpm);
        if (_dl.Clutch > clutchWas && mismatch > 150f + 0.1f * gearRpm)
            _dl.Clutch = MathF.Min(_dl.Clutch, clutchWas + dt * ClutchLetInPerSecond);
        _engine.Throttle = throttle;

        // Up near the shift point under throttle, early when cruising; down when the revs sag, and
        // down when floored if the lower gear lands under 90 % of the upshift point (kickdown: without
        // it a bike floored at 3,300 rpm in third lugged there).
        // A change up must land above the change-down point: the 13 litre truck's light-throttle
        // shift (1,020) sat under its 1,100 change down, and it hunted between gears every couple of
        // seconds ("the pitch steps hard, not smooth").
        float upAt = MathHelper.Lerp(_gb.CruiseUpshiftRpm ?? e.PeakTorqueRpm * 0.85f, _gb.UpshiftRpm, throttle);
        float downAt = MathF.Max(_gb.DownshiftRpm, e.IdleRpm * 1.5f);
        bool floored = need > available && err > 0.5f;
        if (_dl.Locked && _engine.Rpm > upAt && _dl.Gear < _gb.TopGear
            && _engine.Rpm * _dl.Ratio(_dl.Gear + 1) / _dl.Ratio(_dl.Gear) > downAt * 1.1f)
        {
            _shiftFrom = _dl.Gear; _shiftTo = _dl.Gear + 1; _shiftTimer = _gb.ShiftSeconds; _dl.Gear = 0;
        }
        else if (_dl.Gear > 1 && gearRpm < downAt && _dl.Locked)
        {
            _shiftFrom = _dl.Gear; _shiftTo = _dl.Gear - 1; _shiftTimer = _gb.ShiftSeconds * 0.7f; _dl.Gear = 0;
        }
        else if (floored && _dl.Gear > 1 && _dl.Locked && _dl.GearRpm(_dl.Gear - 1) < _gb.UpshiftRpm * 0.9f)
        {
            _shiftFrom = _dl.Gear; _shiftTo = _dl.Gear - 1; _shiftTimer = _gb.ShiftSeconds * 0.7f; _dl.Gear = 0;
        }
    }
}
