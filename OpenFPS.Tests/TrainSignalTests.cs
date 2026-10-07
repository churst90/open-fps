using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A train's horn, whistle and bell are its own sources, sounded by the train's one synth when the
/// server says so (TrainSignal). See RailRunTests for the server half.
/// </summary>
public class TrainSignalTests
{
    private readonly ITestOutputHelper _o;
    public TrainSignalTests(ITestOutputHelper o) => _o = o;

    private const float Rate = 48000f;

    private static double Rms(TrainVoiceState v, int source, long from, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++) { double y = v.Sample(source, from + i, 1f / Rate); sum += y * y; }
        return Math.Sqrt(sum / count);
    }

    [Theory]
    [InlineData("light_rail")]
    [InlineData("amtrak")]
    [InlineData("steam")]
    public void A_train_sounds_its_warning_and_bell_on_its_own_outlets_and_only_when_told(string preset)
    {
        var v = new TrainVoiceState(preset + "/t", TrainProfile.ByName(preset), Rate, 3);
        int warn = TrainSignal.WarningSource(v.Layout), bell = TrainSignal.BellSource(v.Layout);
        Assert.True(warn >= 0, "every preset has a horn or a whistle");
        Assert.True(bell >= 0, "every preset here has a bell");

        int half = (int)(0.5f * Rate);
        double quietWarn = Rms(v, warn, 0, half), quietBell = Rms(v, bell, 0, half);

        // One second on, a second off, a second on; the bell for three.
        v.Signal(new[] { 1.0f, 1.0f, 1.0f }, 3.0f, 0.0);
        long start = v.Newest;
        double blast = Rms(v, warn, start + half / 2, half);
        double gapLate = Rms(v, warn, start + (long)(1.8f * Rate), (int)(0.15f * Rate));
        double ringing = Rms(v, bell, start, (int)(2f * Rate));
        double after = Rms(v, bell, start + (long)(4.5f * Rate), half);
        _o.WriteLine($"{preset}: warning {Db(quietWarn):F0} -> {Db(blast):F0} dB, end of the gap {Db(gapLate):F0} dB; "
                   + $"bell {Db(quietBell):F0} -> {Db(ringing):F0} dB, after {Db(after):F0} dB");

        Assert.True(quietWarn < 0.02, $"the {preset}'s horn sounds before it is blown ({Db(quietWarn):F0} dB)");
        Assert.True(quietBell < 0.02, $"the {preset}'s bell rings before it is rung ({Db(quietBell):F0} dB)");
        Assert.True(Db(blast) > 110, $"the {preset}'s horn blast is {Db(blast):F0} dB at a metre");
        Assert.True(Db(blast) - Db(gapLate) > 20, $"the {preset}'s horn does not stop between blasts");
        Assert.True(Db(ringing) > 80, $"the {preset}'s bell rings at {Db(ringing):F0} dB at a metre");
        Assert.True(Db(ringing) - Db(after) > 12, $"the {preset}'s bell does not stop after its time");
        Assert.False(v.IsSignalling && after > 0, "the signal was let go");
    }

    [Fact]
    public void A_signal_key_round_trips_and_a_road_horn_is_not_one()
    {
        var (warning, bell) = TrainSignal.ForCrossing(18f);
        string key = TrainSignal.Key("amtrak", "Northbound_1", warning, bell);
        Assert.True(TrainSignal.TryParse(key, out string train, out var back, out float bellBack));
        Assert.Equal("amtrak/Northbound_1", train);
        Assert.Equal(warning, back);
        Assert.Equal(18f, bellBack);
        Assert.Equal(8f, back[^1]);                             // the last long held to the crossing
        Assert.False(TrainSignal.TryParse(Honk.Key("air:k5la", warning), out _, out _, out _));
        Assert.False(TrainSignal.TryParse("railsignal:nopreset:1:2", out _, out _, out _));
    }

    [Theory]
    [InlineData("light_rail", "lrv_two_chime")]
    [InlineData("amtrak", "k5la")]
    public void A_horn_source_is_placed_at_its_own_models_level(string preset, string horn)
    {
        var layout = TrainLayout.Sources(TrainProfile.ByName(preset));
        Assert.Equal(ModelLibrary.Horn(horn).ReferenceDb, layout[TrainSignal.WarningSource(layout)].LevelDb);
        var bell = layout[TrainSignal.BellSource(layout)];
        Assert.True(bell.HeadroomDb > VehicleProfile.PeakHeadroomDb, "a bell renders with its own room");
    }

    private static double Db(double pa) => 20 * Math.Log10(Math.Max(1e-9, pa) / 20e-6);
}
