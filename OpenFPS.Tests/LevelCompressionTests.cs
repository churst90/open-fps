using System;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>Tests that change the process-wide level compression run alone and put it back.</summary>
[CollectionDefinition(nameof(LevelCompressionSetting), DisableParallelization = true)]
public class LevelCompressionSetting { }

/// <summary>
/// How much of the real difference in loudness reaches the mix: one setting for every sound in the
/// game. Cody, 2026-09-25: "the louder something is the further it can be heard", and "I can hear the
/// police car hit the gas but I have to be right there next to it".
/// </summary>
[Collection(nameof(LevelCompressionSetting))]
public class LevelCompressionTests
{
    private static T With<T>(float compression, Func<T> f)
    {
        float was = Loudness.DynamicRangeCompression;
        try { Loudness.DynamicRangeCompression = compression; return f(); }
        finally { Loudness.DynamicRangeCompression = was; }
    }

    [Fact]
    public void AtRealLevelsAHotRodIsItsWholeDifferenceLouder()
    {
        float real = With(1f, () => Loudness.PlacedDb(113f) - Loudness.PlacedDb(94f));
        float squeezed = With(0.45f, () => Loudness.PlacedDb(113f) - Loudness.PlacedDb(94f));
        Assert.InRange(real, 18.5f, 19.5f);
        Assert.InRange(squeezed, 8f, 9.5f);
    }

    [Theory]
    [InlineData(0.45f)]
    [InlineData(0.75f)]
    [InlineData(1f)]
    public void LouderCarriesFurtherAtAnySetting(float compression)
    {
        With(compression, () =>
        {
            float prev = 0f;
            foreach (float db in new[] { 60f, 80f, 95f, 105f, 115f, 125f })
            {
                float range = Loudness.AudibleRange(db);
                // Further, until both reach the 3 km the renderer stops counting at.
                Assert.True(range > prev || range >= 2999f, $"at {compression}: {db} dB carries {range:F0} m, no further than {prev:F0}");
                prev = range;
            }
            return 0;
        });
    }

    /// <summary>The engine's lift: the old share for an ordinary car, nothing at real levels, and not
    /// the flat share for a car declared over the mix's ceiling.</summary>
    [Fact]
    public void TheLiftFollowsTheLaw()
    {
        float ordinary = With(0.45f, () => EngineVoiceState.LiftDb(79f, 94f));
        Assert.InRange(ordinary, 0.55f * 15f - 0.2f, 0.55f * 15f + 0.2f);
        Assert.Equal(0f, With(1f, () => EngineVoiceState.LiftDb(79f, 94f)), 3);
        float loud = With(0.45f, () => EngineVoiceState.LiftDb(107f, 132f));
        Assert.InRange(loud, 2.5f, 5f);             // the flat 55 % said 13.75
        Assert.Equal(0f, EngineVoiceState.LiftDb(120f, 100f));     // running over its declared level
    }

    [Fact]
    public void TheLevelsCommand()
    {
        int saves = 0;
        void Save() => saves++;
        float was = Loudness.DynamicRangeCompression;
        try
        {
            Assert.Contains("percent", ClientGameSession.LevelsCommand(Array.Empty<string>(), Save));
            ClientGameSession.LevelsCommand(new[] { "real" }, Save);
            Assert.Equal(1f, Loudness.DynamicRangeCompression);
            ClientGameSession.LevelsCommand(new[] { "70" }, Save);
            Assert.Equal(0.7f, Loudness.DynamicRangeCompression, 4);
            ClientGameSession.LevelsCommand(new[] { "0.6" }, Save);
            Assert.Equal(0.6f, Loudness.DynamicRangeCompression, 4);
            ClientGameSession.LevelsCommand(new[] { "default" }, Save);
            Assert.Equal(Loudness.DefaultCompression, Loudness.DynamicRangeCompression);
            ClientGameSession.LevelsCommand(new[] { "5" }, Save);
            Assert.Equal(Loudness.MinCompression, Loudness.DynamicRangeCompression);
            Assert.Contains("not a level", ClientGameSession.LevelsCommand(new[] { "loud" }, Save));
            Assert.Equal(5, saves);
        }
        finally { Loudness.DynamicRangeCompression = was; }
    }
}
