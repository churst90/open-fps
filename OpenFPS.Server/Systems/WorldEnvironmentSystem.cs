using System;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Authoritatively simulates game time, seasons, and atmospheric weather.
///
/// What this owns is <b>sky</b>: time of day, season, temperature, humidity, precipitation, and the
/// sustained wind plus its gustiness. Those are the same everywhere on the server, so one instance
/// drives every map. What it deliberately does NOT own is the two properties that belong to a
/// <i>place</i> rather than to the weather — air pressure (altitude) and the authored air-absorption
/// multiplier. Those come off the map's zone and are overlaid per map when the state is broadcast;
/// see <see cref="GetStateForMap"/>.
/// </summary>
public class WorldEnvironmentSystem
{
    /// <summary>The temperature the daily/seasonal curve is written around, and the baseline a map's
    /// authored <c>Temperature</c> is read as an offset from.</summary>
    public const float BaselineTemperature = 20.0f;

    /// <summary>The humidity a map's authored <c>Humidity</c> is read as an offset from.</summary>
    public const float BaselineHumidity = 0.5f;

    /// <summary>Standard sea-level atmospheric pressure, millibars. Also the fallback when a map
    /// authors a pressure that is not plausibly millibars.</summary>
    public const float SeaLevelPressureMb = 1013.25f;

    private WorldEnvironmentComponent _env = new() {
        GameTime = 8.0f,
        DayOfYear = 1,
        Temperature = BaselineTemperature,
        Humidity = BaselineHumidity,
        AirPressure = SeaLevelPressureMb,
        AirAbsorptionMultiplier = 1.0f,
        WindVelocity = new Vector3(2.0f, 0.0f, 1.0f),
        WindGustiness = 0.2f,
        PrecipitationIntensity = 0.0f
    };

    private float _timeMultiplier = 60.0f; // 1 real second = 1 game minute

    // Scenario targets. Temperature is expressed as an OFFSET from the seasonal/daily curve plus an
    // optional ceiling, not as an absolute target: the previous `_targetTemp -= 2` both never reached
    // the temperature (nothing read the field) and compounded — two rain fronts in a row would have
    // cooled the world by 4 degrees and never given them back.
    private float _targetHumidity = BaselineHumidity;
    private float _targetPrecipitation = 0.0f;
    private Vector3 _targetWind = new Vector3(2.0f, 0.0f, 1.0f);
    private float _targetGustiness = 0.2f;
    private float _scenarioTempOffset = 0.0f;
    private float _scenarioTempCeiling = float.MaxValue;

    private WeatherType _currentScenario = WeatherType.Clear;
    private readonly Random _random;

    /// <summary>How far the air has carried the wind's eddy pattern, metres east and north: the
    /// integral of the wind over time (WindField). Broadcast with the wind, so every client displaces
    /// the same pattern by the same amount and hears the same gust at the same tree.</summary>
    private double _travelEast, _travelNorth;

    /// <summary>How much faster than a front the weather moves after somebody set it by hand. A front
    /// takes a minute to arrive; a tester who has typed /weather storm wants to hear it within
    /// seconds, and still without a step.</summary>
    private float _weatherRate = 1f;
    private const float SetByHandRate = 5f;

    /// <summary>The chance per tick of a new front, when the weather is rolling on its own.</summary>
    public const double DefaultFrontProbabilityPerTick = 0.0005;

    /// <summary>The weather front currently in effect.</summary>
    public WeatherType CurrentScenario => _currentScenario;

    /// <summary>Chance per tick that a new weather front rolls in. Zero pins the current scenario,
    /// which is what the tests want when they are measuring the response to one.</summary>
    public double FrontProbabilityPerTick { get; set; } = DefaultFrontProbabilityPerTick;

    /// <summary>Whether the weather stays as it is (OPENFPS_WEATHER or /weather) rather than rolling.</summary>
    public bool Pinned => FrontProbabilityPerTick <= 0;

    public WorldEnvironmentSystem() : this(null) { }

    /// <summary>Seedable for tests; production passes nothing and gets <see cref="Random.Shared"/>.</summary>
    public WorldEnvironmentSystem(Random? random)
    {
        _random = random ?? Random.Shared;
        ApplyPinnedWeather();
        ApplyClock();
    }

    /// <summary>
    /// The clock for testing what happens at an hour of the day, or over a day in a few minutes:
    ///
    ///     OPENFPS_GAME_HOUR=21.5 OPENFPS_TIME_MULTIPLIER=600 ./run-server.sh city
    ///
    /// starts the day at half past nine at night and runs it ten minutes a second. Unset, the day
    /// starts at eight and runs a minute a second.
    /// </summary>
    private void ApplyClock()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_GAME_HOUR"), System.Globalization.NumberStyles.Float, inv, out float hour))
        {
            _env.GameTime = Math.Clamp(hour, 0f, 23.999f);
            Log.Information("WorldEnvironment: the day starts at {Hour:F2} (OPENFPS_GAME_HOUR).", _env.GameTime);
        }
        if (float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_TIME_MULTIPLIER"), System.Globalization.NumberStyles.Float, inv, out float rate) && rate > 0f)
        {
            _timeMultiplier = rate;
            Log.Information("WorldEnvironment: game time runs {Rate}x real time (OPENFPS_TIME_MULTIPLIER).", rate);
        }
    }

    /// <summary>
    /// Pins the weather from OPENFPS_WEATHER, and stops fronts rolling in while it is set.
    ///
    /// Weather is not decoration here: precipitation and temperature swap the material under the
    /// player's feet (see SoundMappingService — rain turns Concrete into Wet_Concrete, freezing turns
    /// everything into Snow). That is correct, and it is also the last thing you want happening on its
    /// own in the middle of testing something else, because the ground changing underfoot reads as a
    /// bug in whatever you were actually listening to.
    ///
    ///     OPENFPS_WEATHER=Clear   ./run-server.sh
    ///
    /// Accepts any WeatherType name — Clear, Rain, Snow, Storm. Unset, the weather rolls as before.
    /// </summary>
    private void ApplyPinnedWeather()
    {
        string? pinned = Environment.GetEnvironmentVariable("OPENFPS_WEATHER");
        if (string.IsNullOrWhiteSpace(pinned)) return;

        if (!Enum.TryParse<WeatherType>(pinned.Trim(), ignoreCase: true, out var scenario))
        {
            Log.Warning("OPENFPS_WEATHER='{Value}' is not a weather type. Expected one of: {Names}. " +
                        "The weather will roll normally.", pinned, string.Join(", ", Enum.GetNames<WeatherType>()));
            return;
        }

        SetScenario(scenario);
        _held = FrontPrecipitation(scenario);
        FrontProbabilityPerTick = 0;
        Log.Information("WorldEnvironment: PINNED to {Scenario} by OPENFPS_WEATHER — no fronts will roll in.",
                        scenario);
    }

    public void Update(float dt)
    {
        _env.GameTime += (dt * _timeMultiplier) / 3600.0f;

        if (_env.GameTime >= 24.0f)
        {
            _env.GameTime = 0;
            _env.DayOfYear++;
            if (_env.DayOfYear > 365) _env.DayOfYear = 1;
        }

        // --- SEASONAL CALCULATION ---
        // Seasonal Temperature: 15C base, +/- 20C variance over the year.
        // Deep winter around day 355 (late Dec), Peak summer around day 172 (late June)
        float seasonalFactor = (float)Math.Cos((_env.DayOfYear - 172.0f) / 365.0f * Math.PI * 2.0f);
        float seasonalBaseTemp = 15.0f + (seasonalFactor * 20.0f);

        // --- DAILY CALCULATION ---
        // Daily Temperature Cycle (Coldest at 4AM, Hottest at 2PM)
        float dailyFactor = (float)Math.Sin((_env.GameTime - 8.0f) / 24.0f * Math.PI * 2.0f);

        // --- SCENARIO ---
        // The front's contribution: rain cools, a storm cools harder, snow caps the air below freezing.
        float targetTemp = seasonalBaseTemp + (dailyFactor * 5.0f) + _scenarioTempOffset;
        if (targetTemp > _scenarioTempCeiling) targetTemp = _scenarioTempCeiling;

        // --- WEATHER INTERPOLATION ---
        _env.Temperature = MathHelper.Lerp(_env.Temperature, targetTemp, dt * 0.1f);
        float rate = dt * _weatherRate;
        _env.Humidity = MathHelper.Lerp(_env.Humidity, _targetHumidity, MathF.Min(1f, rate * 0.05f));
        _env.PrecipitationIntensity = MathHelper.Lerp(_env.PrecipitationIntensity, _targetPrecipitation, MathF.Min(1f, rate * 0.02f));
        _env.WindVelocity = Vector3.Lerp(_env.WindVelocity, _targetWind, MathF.Min(1f, rate * 0.05f));
        // Gustiness used to be assigned straight onto the state, so a front snapped the air from calm to
        // a gale between one tick and the next. It fades like everything else it travels with.
        _env.WindGustiness = MathHelper.Lerp(_env.WindGustiness, _targetGustiness, MathF.Min(1f, rate * 0.05f));

        // The air carries the eddy pattern on at the wind it has now.
        var (carryEast, carryNorth) = WindField.Carry(_env.WindVelocity.X, _env.WindVelocity.Z);
        _travelEast += carryEast * (double)dt;
        _travelNorth += carryNorth * (double)dt;

        // Check for Freezing (affects precipitation type). Rain asked for by hand at a rate stays rain.
        if (_env.Temperature < 0 && _currentScenario == WeatherType.Rain && _held == null)
        {
            Log.Information("WorldEnvironment: Rain turning to Snow due to freezing temperatures.");
            SetScenario(WeatherType.Snow);
        }

        // Random Weather Fronts (Simplified Scenario Engine)
        if (FrontProbabilityPerTick > 0 && _random.NextDouble() < FrontProbabilityPerTick)
        {
            var next = (WeatherType)_random.Next(0, 4);
            SetScenario(next);
            _weatherRate = 1f;
            Log.Information("WorldEnvironment: Atmospheric Front shifting to {Scenario}", next);
        }
    }

    /// <summary>Moves the world onto a weather front. Public so a command (or a test) can force one.</summary>
    public void SetScenario(WeatherType scenario)
    {
        _currentScenario = scenario;
        switch (scenario)
        {
            case WeatherType.Clear:
                _targetHumidity = 0.4f;
                _targetPrecipitation = 0.0f;
                _targetWind = new Vector3(_random.NextSingle() * 5f, 0, _random.NextSingle() * 5f);
                _targetGustiness = 0.1f;
                _scenarioTempOffset = 0.0f;
                _scenarioTempCeiling = float.MaxValue;
                break;
            case WeatherType.Rain:
                _targetHumidity = 0.9f;
                _targetPrecipitation = 0.6f;
                _targetWind = new Vector3(5f, 0, 5f);
                _targetGustiness = 0.3f;
                _scenarioTempOffset = -2.0f; // Rain cools the air
                _scenarioTempCeiling = float.MaxValue;
                break;
            case WeatherType.Storm:
                _targetHumidity = 1.0f;
                _targetPrecipitation = 1.0f;
                _targetWind = new Vector3(15f, 0, -10f); // High wind
                _targetGustiness = 0.8f;
                _scenarioTempOffset = -4.0f;
                _scenarioTempCeiling = float.MaxValue;
                break;
            case WeatherType.Snow:
                _targetHumidity = 0.6f;
                _targetPrecipitation = 0.5f;
                _targetWind = new Vector3(8f, 0, 2f);
                _targetGustiness = 0.4f;
                _scenarioTempOffset = -5.0f;
                _scenarioTempCeiling = -1.0f; // Snow does not fall above freezing
                break;
        }
    }

    /// <summary>
    /// The weather set by hand (/weather): this front, held until somebody says otherwise, arriving
    /// over a few seconds rather than a minute.
    /// </summary>
    public void PinScenario(WeatherType scenario)
    {
        SetScenario(scenario);
        FrontProbabilityPerTick = 0;
        _weatherRate = SetByHandRate;
        // Rain, a storm or snow asked for by hand is that, at the front's own rate, whatever the season.
        _held = FrontPrecipitation(scenario);
    }

    /// <summary>What a front set by hand holds falling: rain for Rain and Storm, snow for Snow.</summary>
    private Precipitation? FrontPrecipitation(WeatherType scenario) => scenario switch
    {
        WeatherType.Rain or WeatherType.Storm => new Precipitation(PrecipitationKind.Rain, Rainfall.RateFromIntensity(_targetPrecipitation)),
        WeatherType.Snow => new Precipitation(PrecipitationKind.Snow, Rainfall.RateFromIntensity(_targetPrecipitation)),
        _ => null,
    };

    /// <summary>
    /// The wind set by hand: a mean velocity (map axes, x east, z north, m/s) and a gustiness (0..1).
    /// Holds the weather too, or the next front would blow it away.
    /// </summary>
    public void PinWind(Vector3 velocity, float gustiness)
    {
        _targetWind = new Vector3(velocity.X, 0f, velocity.Z);
        _targetGustiness = Math.Clamp(gustiness, 0f, 1f);
        FrontProbabilityPerTick = 0;
        _weatherRate = SetByHandRate;
    }

    /// <summary>
    /// Rain set by hand at a rate (/weather rain heavy, /weather rain 12): the Rain front with its
    /// precipitation set to give that rate, held, and rain whatever the season, so a tester asking for
    /// rain hears rain.
    /// </summary>
    public void PinRain(float rateMmPerHour) => PinPrecipitation(new Precipitation(PrecipitationKind.Rain, rateMmPerHour));

    /// <summary>
    /// Any precipitation set by hand (/weather rain heavy drops 3 mm, /weather hail golf, /weather
    /// snow): its front (Snow for snow, Rain for the rest), its precipitation brought to the rate, and
    /// the kind and sizes held whatever the season and the map's air.
    /// </summary>
    public void PinPrecipitation(Precipitation p)
    {
        PinScenario(p.Kind == PrecipitationKind.Snow ? WeatherType.Snow : WeatherType.Rain);
        float rate = p.Kind == PrecipitationKind.Hail && !(p.RateMmPerHour > 0f) ? Hydrometeors.HailRainRate : p.RateMmPerHour;
        _targetPrecipitation = Rainfall.IntensityFor(rate);
        _held = p with { RateMmPerHour = rate };
    }

    /// <summary>The precipitation set by hand, or null.</summary>
    public Precipitation? HeldPrecipitation => _held;
    private Precipitation? _held;

    /// <summary>The rate liquid rain (rain, freezing rain, the rain hail falls in) was set to by hand, or null.</summary>
    public float? HeldRainRate => _held is { Kind: not PrecipitationKind.Snow and not PrecipitationKind.Sleet } h ? h.RateMmPerHour : null;

    /// <summary>
    /// The rain falling on one map, mm/h: <see cref="RainRateFor"/>, except that rain set by hand at a
    /// rate is rain however cold the map's air is.
    /// </summary>
    public float RainRate(in WorldEnvironmentComponent state)
        => _held != null ? Rainfall.RateFromIntensity(state.PrecipitationIntensity) : RainRateFor(state);

    /// <summary>
    /// What is falling on one map: the kind, its water-equivalent rate as the weather has it now
    /// (rising and falling with the front), and its sizes. Set by hand, what was set; otherwise rain,
    /// or snow when the map's air is below <see cref="Rainfall.SnowBelowCelsius"/>.
    /// </summary>
    public Precipitation PrecipitationFor(in WorldEnvironmentComponent state)
    {
        float rate = Rainfall.RateFromIntensity(state.PrecipitationIntensity);
        if (_held is { } h) return h with { RateMmPerHour = rate };
        return new Precipitation(state.Temperature < Rainfall.SnowBelowCelsius ? PrecipitationKind.Snow : PrecipitationKind.Rain, rate);
    }

    /// <summary>Lets the weather roll on its own again from what it is now.</summary>
    public void Unpin()
    {
        _held = null;
        FrontProbabilityPerTick = DefaultFrontProbabilityPerTick;
        _weatherRate = 1f;
    }

    /// <summary>Where the wind is heading for, before it gets there.</summary>
    public Vector3 TargetWind => _targetWind;
    public float TargetGustiness => _targetGustiness;

    /// <summary>How far the air has carried the eddy pattern, metres east and north.</summary>
    public (double East, double North) WindTravel => (_travelEast, _travelNorth);

    /// <summary>
    /// Sets the world clock. The season is the largest single term in the temperature curve — day 1 is
    /// deep winter and day 172 is high summer — so anything reasoning about the weather (an admin
    /// command, a test) needs to be able to say when it is.
    /// </summary>
    public void SetDate(float gameTimeHours, int dayOfYear)
    {
        _env.GameTime = Math.Clamp(gameTimeHours, 0f, 23.999f);
        _env.DayOfYear = Math.Clamp(dayOfYear, 1, 365);
    }

    /// <summary>
    /// The rain the precipitation is, mm/h, as it falls on one map: the intensity as a rain rate
    /// (Rainfall.RateFromIntensity: a drizzle at 0.1, moderate at the Rain front's 0.6, violent at the
    /// Storm's 1.0), and nothing when the map's air is cold enough that what falls is snow.
    /// </summary>
    public static float RainRateFor(in WorldEnvironmentComponent state)
        => Rainfall.RateFor(state.PrecipitationIntensity, state.Temperature);

    /// <summary>The global sky state, with no map applied. Callers that are about to send this to a
    /// player want <see cref="GetStateForMap"/> instead.</summary>
    public WorldEnvironmentComponent GetCurrentState() => _env;

    /// <summary>
    /// The world state as it is experienced on one map: the global weather, with the map's own
    /// physical properties overlaid.
    ///
    /// A map's authored <c>Temperature</c> / <c>Humidity</c> are read as offsets from the baselines
    /// (<see cref="BaselineTemperature"/> / <see cref="BaselineHumidity"/>), so a map that authors
    /// nothing behaves exactly as the global sim while a map authored at 35 C stays 15 degrees hotter
    /// than the season all year. <c>AirPressure</c> and <c>AirAbsorptionMultiplier</c> are static
    /// properties of the place and are taken as authored — the sim has no opinion about altitude.
    /// </summary>
    public WorldEnvironmentComponent GetStateForMap(in MapAtmosphere map)
    {
        var state = _env;
        state.Temperature = _env.Temperature + (map.Temperature - BaselineTemperature);
        state.Humidity = Math.Clamp(_env.Humidity + (map.Humidity - BaselineHumidity), 0f, 1f);
        state.AirPressure = map.AirPressure;
        state.AirAbsorptionMultiplier = map.AirAbsorptionMultiplier;
        return state;
    }
}

/// <summary>
/// The atmospheric properties a map authors, as the environment system reads them. Kept separate
/// from <c>MapData</c> so the system does not need the whole map record (and so tests can hand it
/// one without loading a file).
/// </summary>
public readonly record struct MapAtmosphere(
    float Temperature,
    float Humidity,
    float AirPressure,
    float AirAbsorptionMultiplier)
{
    /// <summary>A map that authors nothing: the global weather, unmodified, at sea level.</summary>
    public static MapAtmosphere Default => new(
        WorldEnvironmentSystem.BaselineTemperature,
        WorldEnvironmentSystem.BaselineHumidity,
        WorldEnvironmentSystem.SeaLevelPressureMb,
        1.0f);
}

// The local MathHelper that used to live here was a byte-for-byte copy of OpenFPS.Common.MathHelper.Lerp,
// and being in this namespace it SHADOWED the shared one for every file in OpenFPS.Server.Systems —
// so anything here reaching for WrapAngle or ToYawPitch found a class with neither. Deleted; the
// shared one is in scope through `using OpenFPS.Common` above and does the same arithmetic.
