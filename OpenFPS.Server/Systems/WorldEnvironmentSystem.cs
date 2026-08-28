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

    /// <summary>The weather front currently in effect.</summary>
    public WeatherType CurrentScenario => _currentScenario;

    /// <summary>Chance per tick that a new weather front rolls in. Zero pins the current scenario,
    /// which is what the tests want when they are measuring the response to one.</summary>
    public double FrontProbabilityPerTick { get; set; } = 0.0005;

    public WorldEnvironmentSystem() : this(null) { }

    /// <summary>Seedable for tests; production passes nothing and gets <see cref="Random.Shared"/>.</summary>
    public WorldEnvironmentSystem(Random? random)
    {
        _random = random ?? Random.Shared;
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
        _env.Humidity = MathHelper.Lerp(_env.Humidity, _targetHumidity, dt * 0.05f);
        _env.PrecipitationIntensity = MathHelper.Lerp(_env.PrecipitationIntensity, _targetPrecipitation, dt * 0.02f);
        _env.WindVelocity = Vector3.Lerp(_env.WindVelocity, _targetWind, dt * 0.05f);
        // Gustiness used to be assigned straight onto the state, so a front snapped the air from calm to
        // a gale between one tick and the next. It fades like everything else it travels with.
        _env.WindGustiness = MathHelper.Lerp(_env.WindGustiness, _targetGustiness, dt * 0.05f);

        // Check for Freezing (affects precipitation type)
        if (_env.Temperature < 0 && _currentScenario == WeatherType.Rain)
        {
            Log.Information("WorldEnvironment: Rain turning to Snow due to freezing temperatures.");
            SetScenario(WeatherType.Snow);
        }

        // Random Weather Fronts (Simplified Scenario Engine)
        if (FrontProbabilityPerTick > 0 && _random.NextDouble() < FrontProbabilityPerTick)
        {
            var next = (WeatherType)_random.Next(0, 4);
            SetScenario(next);
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
    /// Sets the world clock. The season is the largest single term in the temperature curve — day 1 is
    /// deep winter and day 172 is high summer — so anything reasoning about the weather (an admin
    /// command, a test) needs to be able to say when it is.
    /// </summary>
    public void SetDate(float gameTimeHours, int dayOfYear)
    {
        _env.GameTime = Math.Clamp(gameTimeHours, 0f, 23.999f);
        _env.DayOfYear = Math.Clamp(dayOfYear, 1, 365);
    }

    public string GetSeason()
    {
        return _env.DayOfYear switch {
            < 90 => "Winter",
            < 180 => "Spring",
            < 270 => "Summer",
            _ => "Autumn"
        };
    }

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

internal static class MathHelper
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}
