using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using Serilog;
using OpenFPS.Geometry;
using OpenFPS.Physics;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Game time, seasons and weather: the sky, one instance for every map. What belongs to a place (air
/// pressure, air absorption, a map's offsets and held sky) is overlaid per map in
/// <see cref="GetStateForMap"/>.
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
        WindVelocity = FrontWind(WeatherType.Clear, StartingWindSpeed, WindWeather.PrevailingFromDegrees),
        WindGustiness = 0.2f,
        PrecipitationIntensity = 0.0f
    };

    private float _timeMultiplier = 60.0f; // 1 real second = 1 game minute

    // Scenario targets. Temperature is an offset from the seasonal and daily curve plus an optional
    // ceiling, so two fronts in a row do not compound.
    private float _targetHumidity = BaselineHumidity;
    private float _targetPrecipitation = 0.0f;
    private Vector3 _targetWind = FrontWind(WeatherType.Clear, StartingWindSpeed, WindWeather.PrevailingFromDegrees);

    /// <summary>The wind the world starts with, m/s: a light air, as the start always had (it was 2 east and 1 north).</summary>
    private static readonly float StartingWindSpeed = MathF.Sqrt(5f);

    /// <summary>Whether the wind was set by hand (/weather wind): held exactly as said, without the day's turn.</summary>
    private bool _windByHand;
    private float _targetGustiness = 0.2f;
    private float _scenarioTempOffset = 0.0f;
    private float _scenarioTempCeiling = float.MaxValue;

    private WeatherType _currentScenario = WeatherType.Clear;
    private readonly Random _random;

    /// <summary>How far the air has carried the wind's eddy pattern, metres east and north: the
    /// integral of the wind over time (WindField). Broadcast with the wind, so every client displaces
    /// the same pattern by the same amount and hears the same gust at the same tree.</summary>
    private double _travelEast, _travelNorth;

    /// <summary>How much faster than a front the weather moves when set by hand: seconds rather than a
    /// minute, still without a step.</summary>
    private float _weatherRate = 1f;
    private const float SetByHandRate = 5f;

    /// <summary>Rain fading under a dry front has stopped below this, mm/h: a tenth of the lightest drizzle
    /// (<see cref="Rainfall.DrizzleRate"/>).</summary>
    public const float StoppedBelowMmPerHour = Rainfall.DrizzleRate * 0.1f;

    /// <summary>How long the weather rolling on its own waits for a new front, on average, in seconds:
    /// five minutes (Cody, 2026-10-09; it was about one, and the weather never settled).</summary>
    public const double MeanSecondsBetweenFronts = 300.0;

    /// <summary>The chance per tick of a new front, when the weather is rolling on its own.</summary>
    public const double DefaultFrontProbabilityPerTick = 1.0 / (MeanSecondsBetweenFronts * PhysicsConstants.TickRate);

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
    /// Pins the weather from OPENFPS_WEATHER (any WeatherType name: OPENFPS_WEATHER=Clear ./run-server.sh)
    /// and stops fronts rolling in. Weather changes the ground underfoot, which reads as a bug in
    /// whatever else is being listened to.
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

        // 15 C plus or minus 20 over the year: summer peaks at day 172, winter at day 355.
        float seasonalFactor = (float)Math.Cos((_env.DayOfYear - 172.0f) / 365.0f * Math.PI * 2.0f);
        float seasonalBaseTemp = 15.0f + (seasonalFactor * 20.0f);

        // Plus or minus 5 C over the day: coldest at 2 AM, hottest at 2 PM.
        float dailyFactor = (float)Math.Sin((_env.GameTime - 8.0f) / 24.0f * Math.PI * 2.0f);

        float targetTemp = seasonalBaseTemp + (dailyFactor * 5.0f) + _scenarioTempOffset;
        if (targetTemp > _scenarioTempCeiling) targetTemp = _scenarioTempCeiling;

        _env.Temperature = MathHelper.Lerp(_env.Temperature, targetTemp, dt * 0.1f);
        float rate = dt * _weatherRate;
        _env.Humidity = MathHelper.Lerp(_env.Humidity, _targetHumidity, MathF.Min(1f, rate * 0.05f));
        _env.PrecipitationIntensity = MathHelper.Lerp(_env.PrecipitationIntensity, _targetPrecipitation, MathF.Min(1f, rate * 0.02f));
        // Easing toward a dry sky never reaches it: the last front's rain fell, ever lighter, for good.
        if (_targetPrecipitation <= 0f && Rainfall.RateFromIntensity(_env.PrecipitationIntensity) < StoppedBelowMmPerHour)
            _env.PrecipitationIntensity = 0f;
        // Toward the front's wind turned by the hour (TargetWind): a direction that changes only with the weather and
        // the clock, the same for every client because they all hear it from here.
        _env.WindVelocity = Vector3.Lerp(_env.WindVelocity, TargetWind, MathF.Min(1f, rate * 0.05f));
        // Gustiness fades too: set straight, a front snapped calm to a gale in one tick.
        _env.WindGustiness = MathHelper.Lerp(_env.WindGustiness, _targetGustiness, MathF.Min(1f, rate * 0.05f));

        var (carryEast, carryNorth) = WindField.Carry(_env.WindVelocity.X, _env.WindVelocity.Z);
        _travelEast += carryEast * (double)dt;
        _travelNorth += carryNorth * (double)dt;

        // Freezing turns rain to snow; rain asked for by hand stays rain.
        if (_env.Temperature < 0 && _currentScenario == WeatherType.Rain && _held == null)
        {
            Log.Information("WorldEnvironment: Rain turning to Snow due to freezing temperatures.");
            SetScenario(WeatherType.Snow);
        }

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
        _windByHand = false;
        switch (scenario)
        {
            case WeatherType.Clear:
                _targetHumidity = 0.4f;
                _targetPrecipitation = 0.0f;
                // A fair day's strength, drawn as it always was (0 to 7 m/s); its direction the prevailing wind's.
                _targetWind = FrontWind(scenario, new Vector2(_random.NextSingle() * 5f, _random.NextSingle() * 5f).Length(), PrevailingFromDegrees);
                _targetGustiness = 0.1f;
                _scenarioTempOffset = 0.0f;
                _scenarioTempCeiling = float.MaxValue;
                break;
            case WeatherType.Rain:
                _targetHumidity = 0.9f;
                _targetPrecipitation = 0.6f;
                _targetWind = FrontWind(scenario, FrontSpeed(scenario), PrevailingFromDegrees);
                _targetGustiness = 0.3f;
                _scenarioTempOffset = -2.0f; // Rain cools the air
                _scenarioTempCeiling = float.MaxValue;
                break;
            case WeatherType.Storm:
                _targetHumidity = 1.0f;
                _targetPrecipitation = 1.0f;
                _targetWind = FrontWind(scenario, FrontSpeed(scenario), PrevailingFromDegrees);
                _targetGustiness = 0.8f;
                _scenarioTempOffset = -4.0f;
                _scenarioTempCeiling = float.MaxValue;
                break;
            case WeatherType.Snow:
                _targetHumidity = 0.6f;
                _targetPrecipitation = 0.5f;
                _targetWind = FrontWind(scenario, FrontSpeed(scenario), PrevailingFromDegrees);
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
        _windByHand = true;
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
        _windByHand = false;
        FrontProbabilityPerTick = DefaultFrontProbabilityPerTick;
        _weatherRate = 1f;
    }

    /// <summary>Where the wind is heading for, before it gets there: the front's wind turned by the hour
    /// (<see cref="DiurnalTurnDegrees"/>), or the wind set by hand as it was said.</summary>
    public Vector3 TargetWind => _windByHand ? _targetWind : Veered(_targetWind, DiurnalTurnDegrees(_env.GameTime));

    // ── Where the wind blows from ──────────────────────────────────────────────────────────────

    /// <summary>Where the prevailing wind blows from, degrees clockwise from north (<see cref="WindWeather.PrevailingFromDegrees"/>).
    /// Each front's wind blows from round it (<see cref="FrontWind"/>). Real weather, where a place has it, would
    /// give the direction instead (docs/REAL_WEATHER.md).</summary>
    public float PrevailingFromDegrees { get; set; } = WindWeather.PrevailingFromDegrees;

    /// <summary>
    /// How far a front's wind is turned from the prevailing wind, degrees, positive veered (clockwise). The
    /// northern mid-latitude depression as the Norwegian school drew it, with the prevailing wind its warm sector's:
    /// ahead of a warm front, where the long rain falls, the wind is backed toward the south; at a cold front, where
    /// the squalls and thunder are, it veers to the west-north-west; the polar air behind it, which brings the snow,
    /// comes from the north-west; a fair day blows from the prevailing quarter. The turns are a judgement of the
    /// textbook figures, not measured.
    /// </summary>
    public static float FrontTurnDegrees(WeatherType w) => w switch
    {
        WeatherType.Rain => -60f,
        WeatherType.Storm => 45f,
        WeatherType.Snow => 60f,
        _ => 0f,
    };

    /// <summary>A front's own strength, m/s at ten metres, as the fronts always had it (a fair day's is drawn).</summary>
    public static float FrontSpeed(WeatherType w) => w switch
    {
        WeatherType.Rain => MathF.Sqrt(50f),     // it was 5 east and 5 north
        WeatherType.Storm => MathF.Sqrt(325f),   // 15 east and 10 south
        WeatherType.Snow => MathF.Sqrt(68f),     // 8 east and 2 north
        _ => MathF.Sqrt(12.5f),                  // the middle of a fair day's draw, 2.5 east and 2.5 north
    };

    /// <summary>A front's wind at a strength, from round the prevailing direction: map axes, x east, z north, m/s.</summary>
    public static Vector3 FrontWind(WeatherType w, float speed, float prevailingFromDegrees)
    {
        var air = WindAir.FromCompass(speed, prevailingFromDegrees + FrontTurnDegrees(w), 0f);
        return new Vector3(air.East, 0f, air.North);
    }

    /// <summary>How far the wind near the ground turns over the day, degrees either way.</summary>
    public const float DiurnalTurnAmplitudeDegrees = 10f;

    /// <summary>
    /// The day's turn of the wind near the ground at an hour, degrees, positive veered. By day the sun stirs the
    /// lowest kilometre and brings the upper wind down, which blows veered of the ground's (the Ekman spiral); at
    /// night the ground's air settles and backs. The turn follows the same day as the temperature: veered most at
    /// two in the afternoon, backed most at two in the morning, by <see cref="DiurnalTurnAmplitudeDegrees"/> (the
    /// ground's wind turns 20 to 30 degrees from the upper by day and 40 or more on a still night: a judgement of
    /// the textbook figures).
    /// </summary>
    public static float DiurnalTurnDegrees(float gameHour)
        => DiurnalTurnAmplitudeDegrees * MathF.Sin((gameHour - 8f) / 24f * MathF.PI * 2f);

    /// <summary>A wind turned clockwise (veered) by some degrees: where it blows from moves round that far.</summary>
    public static Vector3 Veered(Vector3 wind, float degrees)
    {
        float a = degrees * MathF.PI / 180f, c = MathF.Cos(a), sn = MathF.Sin(a);
        return new Vector3(wind.X * c + wind.Z * sn, wind.Y, wind.Z * c - wind.X * sn);
    }
    public float TargetGustiness => _targetGustiness;

    /// <summary>How far the air has carried the eddy pattern, metres east and north.</summary>
    public (double East, double North) WindTravel => (_travelEast, _travelNorth);

    /// <summary>Sets the world clock; the season is the largest term in the temperature curve.</summary>
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
    /// The world state on one map: the global weather with the map's properties overlaid. Authored
    /// temperature and humidity are offsets from <see cref="BaselineTemperature"/> and
    /// <see cref="BaselineHumidity"/>; air pressure and air absorption are taken as authored.
    /// </summary>
    public WorldEnvironmentComponent GetStateForMap(in MapAtmosphere map)
    {
        var state = _env;
        state.Temperature = _env.Temperature + (map.Temperature - BaselineTemperature);
        state.Humidity = Math.Clamp(_env.Humidity + (map.Humidity - BaselineHumidity), 0f, 1f);
        state.AirPressure = map.AirPressure;
        state.AirAbsorptionMultiplier = map.AirAbsorptionMultiplier;
        // A map that holds its own sky (world editor map settings): its hour, and its weather at the
        // front's own settled values, whatever the server's sky is doing.
        if (map.HeldHour is float hour) state.GameTime = Math.Clamp(hour, 0f, 24f) % 24f;
        if (map.HeldWeather is WeatherType held)
        {
            var (humidity, precipitation, wind, gustiness, offset, ceiling) = HeldFront(held);
            state.Humidity = humidity;
            state.PrecipitationIntensity = precipitation;
            state.WindVelocity = Veered(FrontWind(held, wind, PrevailingFromDegrees), DiurnalTurnDegrees(state.GameTime));
            state.WindGustiness = gustiness;
            state.Temperature = MathF.Min(state.Temperature + offset, ceiling);
        }
        return state;
    }

    /// <summary>A front's settled values, as <see cref="SetScenario"/> sets its targets (a clear sky's
    /// wind is the middle of its random range). Its wind blows from round the prevailing direction, turned by the
    /// hour, as a front's does (<see cref="GetStateForMap"/>).</summary>
    internal static (float Humidity, float Precipitation, float WindSpeed, float Gustiness, float TempOffset, float TempCeiling) HeldFront(WeatherType w) => w switch
    {
        WeatherType.Rain => (0.9f, 0.6f, FrontSpeed(w), 0.3f, -2f, float.MaxValue),
        WeatherType.Storm => (1.0f, 1.0f, FrontSpeed(w), 0.8f, -4f, float.MaxValue),
        WeatherType.Snow => (0.6f, 0.5f, FrontSpeed(w), 0.4f, -5f, -1f),
        _ => (0.4f, 0f, FrontSpeed(w), 0.1f, 0f, float.MaxValue),
    };
}

/// <summary>The atmospheric properties a map authors, without the whole map record.</summary>
public readonly record struct MapAtmosphere(
    float Temperature,
    float Humidity,
    float AirPressure,
    float AirAbsorptionMultiplier,
    float? HeldHour = null,
    WeatherType? HeldWeather = null)
{
    /// <summary>What a map authors, and what the world editor's map settings hold (a weather, an hour).</summary>
    public static MapAtmosphere Of(OpenFPS.Server.Repositories.MapData m) => new(
        m.Temperature, m.Humidity, m.AirPressure, m.AirAbsorptionMultiplier, m.HeldHour,
        OpenFPS.Server.Editor.MapSettings.WeatherOf(m.HeldWeather));

    /// <summary>A map that authors nothing: the global weather, unmodified, at sea level.</summary>
    public static MapAtmosphere Default => new(
        WorldEnvironmentSystem.BaselineTemperature,
        WorldEnvironmentSystem.BaselineHumidity,
        WorldEnvironmentSystem.SeaLevelPressureMb,
        1.0f);
}

// No MathHelper of its own here: one in this namespace shadowed OpenFPS.Geometry.MathHelper for every
// file in OpenFPS.Server.Systems.
