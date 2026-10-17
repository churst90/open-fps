using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// A car heard through two voices has its length modelled once: as one voice it is placed with its
/// extent, and split, each end is a point at its own end. Widening the ends as well left a hatchback's
/// tailpipe 4 dB short at 2 m and 9 dB at 1 m (Cody, 2026-10-05: "even the police interceptor I can
/// hardly hear").
/// </summary>
public class SplitVoicePlacementTests
{
    /// <summary>The law as a world with the shipped hearing places by.</summary>
    private static readonly OpenFPS.Sound.Hearing.HearingSettings Law = new();

    [Fact]
    public void EachEndOfACloseCarIsPlacedAsAPoint()
    {
        const string preset = "i4_economy";
        var profile = MachineRegistry.VehicleFor(preset);
        var h = new ClientAudioHarness();
        float half = profile.LengthMetres * 0.5f;
        h.StandAt(new Vector3(0f, 0f, -half - 2f));
        h.AddCar(7, preset, Vector3.Zero);
        int intake = h.Voice(VoiceKind.Intake, 7);
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(7) && h.Mixer.Latest.ContainsKey(intake)),
                    "a car 2 m away did not get a voice at each end");

        var point = Law.Place(profile.SourceLevelDb);
        foreach (int id in new[] { 7, intake })
        {
            var e = h.Mixer.Latest[id];
            Assert.Equal(point.ReferenceDistance, e.MinDistance, 3);
            Assert.Equal(point.Gain, e.Volume, 4);
        }
    }

    [Fact]
    public void AFarCarHeardAsOneVoiceKeepsItsLength()
    {
        const string preset = "i4_economy";
        var profile = MachineRegistry.VehicleFor(preset);
        var h = new ClientAudioHarness();
        h.StandAt(new Vector3(0f, 0f, -150f));
        h.AddCar(7, preset, Vector3.Zero);
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(7)), "the car never got a voice");
        h.Tick(5);
        Assert.Empty(h.Mixer.StartedAs(VoiceKind.Intake));

        float extent = Vector3.Distance(profile.ExhaustSlot, new Vector3(0f, profile.FrontTapHeight, profile.FrontTapZ));
        var wide = Law.Place(profile.SourceLevelDb, extent);
        var e = h.Mixer.Latest[7];
        Assert.Equal(wide.ReferenceDistance, e.MinDistance, 3);
        Assert.Equal(wide.Gain, e.Volume, 4);
        // ...and beyond the extent the two placements are the same thing (Widen holds the product).
        var point = Law.Place(profile.SourceLevelDb);
        Assert.Equal(point.Gain * point.ReferenceDistance, wide.Gain * wide.ReferenceDistance, 4);
    }
}
