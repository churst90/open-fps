using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Engine;

/// <summary>
/// The coolant's temperature, and the fan clutch that answers it (<see cref="VehicleProfile.FanClutch"/>).
///
/// A heat balance with one lump of metal and water in it. Heat goes in from the engine, in proportion
/// to the power it is making plus what it loses to its own friction: coolant heat is a fixed share of
/// the fuel burned, and the fuel follows the power. Heat goes out through the radiator in proportion
/// to how far the coolant is above the air, how open the thermostat is, and how much air is going
/// through the core, which is the road's ram air and the fan's together.
///
/// Everything is in units of the engine's DESIGN heat: what it rejects at rated power. The radiator
/// is sized the way a real one is, to carry that at 100 C with the fan locked up and the vehicle
/// standing still, the worst case it is specified for. That leaves no free constants per vehicle:
/// the rated power comes from the engine's own peak torque and redline, the thermal mass from its
/// displacement.
///
/// What it gives, as a result rather than as a rule: at a cruise the ram air carries the heat and
/// the thermostat is part closed, so the fan stays disengaged; pulling a load up to speed from a
/// stop, the heat outruns a radiator with no air through it and, after twenty or thirty seconds of
/// it, the clutch goes in.
///
/// A car's fan is the other kind (<see cref="ElectricFanSpec"/>): a motor on a relay, its speed
/// nothing to do with the crank's. The same heat balance switches it, in two stages, and so does the
/// air conditioning: the condenser sits in front of the radiator and the fan has to pull air through
/// it whenever the compressor runs and the road is not doing it, so on a warm day an idling or
/// crawling car runs its fan whatever the coolant is doing.
/// </summary>
public sealed class CoolingSystem
{
    /// <summary>Ram air through the core at this road speed equals the fan's at full speed, m/s.</summary>
    private const float RamEqualsFanSpeed = 25f;
    /// <summary>Radiator heat transfer grows as the air mass flow to this power (a finned core's
    /// air-side coefficient; 0.5-0.8 in the literature).</summary>
    private const float AirExponent = 0.6f;
    /// <summary>The thermostat: shut below the first, fully open at the second, degrees C.</summary>
    private const float ThermostatOpens = 82f, ThermostatFull = 92f;
    /// <summary>The design point: rated heat carried at this coolant temperature.</summary>
    private const float DesignCelsius = 100f;
    /// <summary>Metal and coolant per litre of displacement, kJ/K: an engine is about 90 kg of
    /// iron and a litre and a half of coolant per litre swept.</summary>
    private const float HeatCapacityPerLitre = 50f;
    /// <summary>Share of the fuel's power that goes to the coolant over the share that comes out as
    /// work: about 0.3 against 0.42 on a diesel.</summary>
    private const float CoolantShare = 0.7f;

    private readonly EngineProfile _e;
    // The switch, whichever kind it is: a clutch engages at one temperature and releases at a lower
    // one; an electric fan's relay does the same at each of its two stages.
    private readonly float _engageC, _releaseC, _idleFraction, _engageSeconds;
    private readonly ElectricFanSpec? _electric;
    private readonly float _airConAmbient = float.MaxValue;
    private bool _stage1, _airCon;
    private readonly float _ratedW, _designW, _capacityJ;
    private float _celsius;
    private bool _engaged;
    private float _drive;

    /// <summary>Coolant temperature, degrees C.</summary>
    public float Celsius => _celsius;
    /// <summary>Whether the clutch is in.</summary>
    public bool Engaged => _engaged;
    /// <summary>Fan speed over its driven speed, from <see cref="FanClutchSpec.DisengagedFraction"/>
    /// to one.</summary>
    public float FanSpeedFraction => _drive;
    /// <summary>The air round the vehicle, degrees C.</summary>
    public float AmbientCelsius { get; set; } = 20f;

    public CoolingSystem(FanClutchSpec clutch, EngineProfile e, int seed) : this(e, seed)
    {
        _engageC = clutch.EngageCelsius; _releaseC = clutch.ReleaseCelsius;
        _idleFraction = clutch.DisengagedFraction; _engageSeconds = clutch.EngageSeconds;
        _drive = clutch.DisengagedFraction;
    }

    /// <summary>
    /// An electric fan on a relay. Deterministic per vehicle from <paramref name="seed"/>: where its
    /// coolant sits to start with, and how warm it has to be before its driver turns the air
    /// conditioning on (<see cref="ElectricFanSpec.AirConAmbientCelsius"/>, give or take
    /// <see cref="ElectricFanSpec.AirConAmbientSpread"/>).
    /// </summary>
    public CoolingSystem(ElectricFanSpec fan, EngineProfile e, int seed) : this(e, seed)
    {
        _electric = fan;
        _engageC = fan.HighOnCelsius; _releaseC = fan.HighOffCelsius;
        _idleFraction = 0f; _engageSeconds = fan.SpinUpSeconds;
        _drive = 0f;
        uint h = unchecked((uint)seed * 2654435761u);
        h ^= h >> 15; h = unchecked(h * 2246822519u); h ^= h >> 13;
        float u = (h & 0xFFFF) / 65535f * 2f - 1f;
        _airConAmbient = fan.AirConAmbientCelsius + u * fan.AirConAmbientSpread;
    }

    /// <summary>Whether the driver has the air conditioning on and the fan is running for it
    /// (electric fans only).</summary>
    public bool AirConditioning => _airCon;
    /// <summary>The air temperature at or above which this car's driver runs the air conditioning.</summary>
    public float AirConFromCelsius => _airConAmbient;

    private CoolingSystem(EngineProfile e, int seed)
    {
        _e = e;
        float redlineW = e.RedlineRpm * MathF.PI / 30f;
        // A diesel's power curve falls from the torque peak to the governor; 0.7 of peak torque at the
        // governed speed is what a 2400 Nm, 2100 rpm truck six is rated at (about 370 kW).
        _ratedW = 0.7f * e.PeakTorqueNm * redlineW;
        _designW = CoolantShare * (_ratedW + FrictionW(e.RedlineRpm));
        _capacityJ = HeatCapacityPerLitre * 1000f * MathF.Max(0.2f, EngineDisplacementLitres(e));
        // Where a warm engine sits at a light load, a degree or two either side so a fleet does not
        // all reach the threshold together.
        _celsius = 86f + (seed % 5 - 2) * 0.8f;
    }

    private static float EngineDisplacementLitres(EngineProfile e)
        => MathF.PI / 4f * (e.BoreMm * 1e-3f) * (e.BoreMm * 1e-3f) * (e.StrokeMm * 1e-3f) * 1000f * e.Cylinders;

    private float FrictionW(float rpm) => (_e.FrictionNm + _e.FrictionNmPerKrpm * rpm / 1000f) * rpm * MathF.PI / 30f;

    /// <summary>
    /// Advance by <paramref name="dt"/> seconds with the engine at <paramref name="rpm"/> delivering
    /// <paramref name="loadTorqueNm"/> to the driveline, the vehicle at <paramref name="roadSpeed"/>.
    /// </summary>
    public void Step(float dt, float rpm, float loadTorqueNm, float roadSpeed)
    {
        rpm = MathF.Max(0f, rpm);
        float w = rpm * MathF.PI / 30f;
        float heatIn = rpm > 50f ? CoolantShare * (MathF.Max(0f, loadTorqueNm) * w + FrictionW(rpm)) / _designW : 0f;

        // The air through the core: the road's and the fan's, which add as flows through one core.
        float ram = MathF.Abs(roadSpeed) / RamEqualsFanSpeed;
        // A fan on the crank moves air in proportion to the crank's speed; a fan on a motor, to its own.
        float fan = _electric != null ? _drive : _drive * MathF.Min(1f, rpm / MathF.Max(1f, _e.RedlineRpm));
        float air = MathF.Sqrt(ram * ram + fan * fan);
        float thermostat = Math.Clamp((_celsius - ThermostatOpens) / (ThermostatFull - ThermostatOpens), 0.03f, 1f);
        float heatOut = MathF.Pow(air, AirExponent) * thermostat
                      * MathF.Max(0f, _celsius - AmbientCelsius) / (DesignCelsius - AmbientCelsius);

        _celsius += (heatIn - heatOut) * _designW / _capacityJ * dt;

        if (!_engaged && _celsius >= _engageC) _engaged = true;
        else if (_engaged && _celsius <= _releaseC) _engaged = false;
        float target = _engaged ? 1f : _idleFraction;
        if (_electric is { } ef)
        {
            // The first stage: low speed, on the lower pair of temperatures.
            if (!_stage1 && _celsius >= ef.LowOnCelsius) _stage1 = true;
            else if (_stage1 && _celsius <= ef.LowOffCelsius) _stage1 = false;
            // The air conditioning: on if the day is warm enough for this driver, and the fan pulling
            // air through the condenser while the road is too slow to. Off again only above half as
            // fast again, so a car crawling at the threshold does not flicker.
            float speed = MathF.Abs(roadSpeed);
            if (AmbientCelsius < _airConAmbient || rpm < 50f) _airCon = false;
            else if (speed < ef.AirConBelowMetresPerSecond) _airCon = true;
            else if (speed > ef.AirConBelowMetresPerSecond * 1.5f) _airCon = false;
            if (_stage1 || _airCon) target = MathF.Max(target, ef.LowSpeedFraction);
            // The ignition is off, and so is the relay.
            if (rpm < 50f) target = 0f;
        }
        // Up over the engagement time; down as fast as the fan's own drag lets it run down.
        float rate = (target > _drive ? 1f : 0.5f) / MathF.Max(0.05f, _engageSeconds) * dt;
        _drive += Math.Clamp(target - _drive, -rate, rate);
    }

    /// <summary>For instruments and tests: put the coolant at a temperature.</summary>
    public void SetCelsius(float c) => _celsius = c;
}
