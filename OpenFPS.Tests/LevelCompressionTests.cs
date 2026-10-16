using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.Session;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>Tests that set the ear model's measured timbres (EarTimbres, one per process) run alone and put them
/// back. The hearing they place by is each test's own (HearingSettings); it was process-wide until 2026-10-16.</summary>
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
    /// <summary>The law as a world with the shipped hearing places by.</summary>
    private static readonly OpenFPS.Common.Hearing.HearingSettings Law = new();

    private static T With<T>(float compression, Func<T> f)
    {
        float was = Law.Compression;
        try { Law.Compression = compression; return f(); }
        finally { Law.Compression = was; }
    }

    [Fact]
    public void AtRealLevelsAHotRodIsItsWholeDifferenceLouder()
    {
        float real = With(1f, () => Law.PlacedDb(113f) - Law.PlacedDb(94f));
        float squeezed = With(0.45f, () => Law.PlacedDb(113f) - Law.PlacedDb(94f));
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
                float range = Law.AudibleRange(db);
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
        float ordinary = With(0.45f, () => EngineVoiceState.LiftDb(Law, 79f, 94f));
        Assert.InRange(ordinary, 0.55f * 15f - 0.2f, 0.55f * 15f + 0.2f);
        Assert.Equal(0f, With(1f, () => EngineVoiceState.LiftDb(Law, 79f, 94f)), 3);
        float loud = With(0.45f, () => EngineVoiceState.LiftDb(Law, 107f, 132f));
        Assert.InRange(loud, 2.5f, 5f);             // the flat 55 % said 13.75
        Assert.Equal(0f, EngineVoiceState.LiftDb(Law, 120f, 100f));     // running over its declared level
    }

    [Fact]
    public void TheLevelsCommand()
    {
        int saves = 0;
        void Save() => saves++;
        float was = Law.Compression;
        try
        {
            Assert.Contains("percent", ClientGameSession.LevelsCommand(Law, Array.Empty<string>(), Save));
            Assert.Contains("not a level", ClientGameSession.LevelsCommand(Law, new[] { "real" }, Save));
            ClientGameSession.LevelsCommand(Law, new[] { "100" }, Save);
            Assert.Equal(1f, Law.Compression);
            ClientGameSession.LevelsCommand(Law, new[] { "70" }, Save);
            Assert.Equal(0.7f, Law.Compression, 4);
            ClientGameSession.LevelsCommand(Law, new[] { "0.6" }, Save);
            Assert.Equal(0.6f, Law.Compression, 4);
            Assert.Contains("the default", ClientGameSession.LevelsCommand(Law, new[] { "default" }, Save));
            Assert.Equal(Loudness.DefaultCompression, Law.Compression);
            ClientGameSession.LevelsCommand(Law, new[] { "5" }, Save);
            Assert.Equal(Loudness.MinCompression, Law.Compression);
            Assert.Contains("not a level", ClientGameSession.LevelsCommand(Law, new[] { "loud" }, Save));
            Assert.Equal(5, saves);
        }
        finally { Law.Compression = was; }
    }

    /// <summary>
    /// The compression turns about an everyday level: a 70 dB sound is placed the same at any setting,
    /// and at real levels ordinary sounds are not pushed down — loud ones go up. It used to pivot on
    /// the 112 dB ceiling, so "real" took footsteps and beacons to 60 dB under full scale.
    /// </summary>
    [Fact]
    public void RealLevelsTurnAboutAnEverydaySound()
    {
        // 70 dB at its reference distance, which for a sound this quiet is the 1.2 m minimum.
        float pivot = Loudness.PivotDb + 20f * MathF.Log10(Loudness.MinReferenceDistance);
        Assert.Equal(With(0.45f, () => Law.PlacedDb(pivot)), With(1f, () => Law.PlacedDb(pivot)), 2);
        Assert.Equal(112f, With(0.45f, () => Law.RenderCeilingDb), 2);                 // the shipped mix is unmoved
        Assert.True(With(1f, () => Law.PlacedDb(88f)) > With(0.45f, () => Law.PlacedDb(88f)), "a door is quieter at real levels");
        Assert.True(With(1f, () => Law.PlacedDb(66f)) > With(0.45f, () => Law.PlacedDb(66f)) - 4f, "a beacon fell away at real levels");
        Assert.True(With(1f, () => Law.PlacedDb(122f)) > With(0.45f, () => Law.PlacedDb(122f)) + 15f, "a V8 is no louder at real levels");
    }
}
