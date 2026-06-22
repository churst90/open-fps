using System;
using System.Numerics;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Authoritatively simulates game time, seasons, and atmospheric weather.
/// </summary>
public class WorldEnvironmentSystem
{
    private WorldEnvironmentComponent _env = new() { 
        GameTime = 8.0f, 
        DayOfYear = 1,
        Temperature = 20.0f, 
        Humidity = 0.5f, 
        AirPressure = 1013.25f, // Sea level default
        WindVelocity = new Vector3(2.0f, 0.0f, 1.0f),
        WindGustiness = 0.2f,
        PrecipitationIntensity = 0.0f
    };
    
    private float _timeMultiplier = 60.0f; // 1 real second = 1 game minute
    private float _targetTemp = 20.0f;
    private float _targetHumidity = 0.5f;
    private float _targetPrecipitation = 0.0f;
    private Vector3 _targetWind = new Vector3(2.0f, 0.0f, 1.0f);
    
    private WeatherType _currentScenario = WeatherType.Clear;

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
        float targetBaseTemp = seasonalBaseTemp + (dailyFactor * 5.0f);

        // --- WEATHER INTERPOLATION ---
        _env.Temperature = MathHelper.Lerp(_env.Temperature, targetBaseTemp, dt * 0.1f);
        _env.Humidity = MathHelper.Lerp(_env.Humidity, _targetHumidity, dt * 0.05f);
        _env.PrecipitationIntensity = MathHelper.Lerp(_env.PrecipitationIntensity, _targetPrecipitation, dt * 0.02f);
        _env.WindVelocity = Vector3.Lerp(_env.WindVelocity, _targetWind, dt * 0.05f);

        // Check for Freezing (affects precipitation type)
        if (_env.Temperature < 0 && _currentScenario == WeatherType.Rain)
        {
            _currentScenario = WeatherType.Snow;
            Log.Information("WorldEnvironment: Rain turning to Snow due to freezing temperatures.");
        }

        // Random Weather Fronts (Simplified Scenario Engine)
        if (Random.Shared.NextDouble() < 0.0005) 
        {
            _currentScenario = (WeatherType)Random.Shared.Next(0, 4);
            ApplyScenario(_currentScenario);
            Log.Information("WorldEnvironment: Atmospheric Front shifting to {Scenario}", _currentScenario);
        }
    }

    private void ApplyScenario(WeatherType scenario)
    {
        switch (scenario)
        {
            case WeatherType.Clear:
                _targetHumidity = 0.4f;
                _targetPrecipitation = 0.0f;
                _targetWind = new Vector3(Random.Shared.NextSingle() * 5f, 0, Random.Shared.NextSingle() * 5f);
                _env.WindGustiness = 0.1f;
                break;
            case WeatherType.Rain:
                _targetHumidity = 0.9f;
                _targetPrecipitation = 0.6f;
                _targetTemp -= 2.0f; // Rain cools the air
                _targetWind = new Vector3(5f, 0, 5f);
                _env.WindGustiness = 0.3f;
                break;
            case WeatherType.Storm:
                _targetHumidity = 1.0f;
                _targetPrecipitation = 1.0f;
                _targetTemp -= 4.0f;
                _targetWind = new Vector3(15f, 0, -10f); // High wind
                _env.WindGustiness = 0.8f;
                break;
            case WeatherType.Snow:
                _targetHumidity = 0.6f;
                _targetPrecipitation = 0.5f;
                _targetTemp = -5.0f; // Force freezing
                _targetWind = new Vector3(8f, 0, 2f);
                _env.WindGustiness = 0.4f;
                break;
        }
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

    public WorldEnvironmentComponent GetCurrentState() => _env;
    public float CurrentTime => _env.GameTime;
}

internal static class MathHelper
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}
