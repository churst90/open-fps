using OpenFPS.Audio.Fmod;
using OpenFPS.Sound;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>A car switched off must stand silent: what its voice renders may not hold a steady level (a DC offset).</summary>
public class EngineSwitchOffTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _out;
    public EngineSwitchOffTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("i4_economy")]
    [InlineData("single")]
    [InlineData("v8_muscle")]
    [InlineData("diesel_truck")]
    public void ASwitchedOffEngineRendersNoSteadyLevel(string preset)
    {
        var voice = new EngineVoiceState(VehicleProfile.Presets[preset](), Rate, 4) { TargetSpeed = 0f, AmbientCelsius = 15f };
        voice.PlaceAtSpeed(0f);
        voice.Revive();
        var block = new float[480];
        void Run(double seconds, out double mean, out double rms)
        {
            double sum = 0, sq = 0; long n = 0;
            for (int b = 0; b < seconds * Rate / block.Length; b++)
            {
                voice.Render(block);
                foreach (float x in block) { sum += x; sq += (double)x * x; n++; }
            }
            mean = sum / n; rms = Math.Sqrt(sq / n);
        }
        Run(3, out double idleMean, out double idleRms);
        voice.Running = false;
        Run(20, out _, out _);
        Run(2, out double mean, out double rms);
        _out.WriteLine($"{preset}: idling mean {idleMean:E2} rms {idleRms:E2}; switched off and standing mean {mean:E2} rms {rms:E2}");
        // Before the fix a standing hatchback held 7e-3 of full scale steadily (-43 dB, pure DC) and a bike 1.7e-3.
        Assert.True(Math.Abs(mean) < 1e-4, $"{preset} stands with a steady level of {mean:E2} of full scale once switched off");
    }
}
