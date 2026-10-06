using System;
using System.Numerics;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A car heard through two voices has its length modelled once, not twice.
///
/// Heard as one voice, a car is placed as a thing three metres long (Loudness.Place with its extent):
/// inside that the level is flat, because a metre nearer the intake is a metre further from the
/// exhaust. Close enough for its two ends to be told apart, each end gets a voice of its own at the
/// end it comes from, and that geometry is then modelled outright. Widening each end as well made the
/// tailpipe stop getting louder inside 3.3 m: about 4 dB short at 2 m behind a hatchback and 9 dB at
/// 1 m (Cody, 2026-10-05: "even the police interceptor I can hardly hear").
/// </summary>
public class SplitVoicePlacementTests
{
    [Fact]
    public void EachEndOfACloseCarIsPlacedAsAPoint()
    {
        const string preset = "i4_economy";
        var profile = MachineRegistry.VehicleFor(preset);
        var h = new ClientAudioHarness();
        float half = profile.LengthMetres * 0.5f;
        h.StandAt(new Vector3(0f, 0f, -half - 2f));
        h.AddCar(7, preset, Vector3.Zero);
        int intake = ClientAudioSystem.IntakeVoiceBase - 7;
        Assert.True(h.TickUntil(() => h.Mixer.Latest.ContainsKey(7) && h.Mixer.Latest.ContainsKey(intake)),
                    "a car 2 m away did not get a voice at each end");

        var point = Loudness.Place(profile.SourceLevelDb);
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
        Assert.False(h.Mixer.Latest.ContainsKey(ClientAudioSystem.IntakeVoiceBase - 7), "a car 150 m off was split");

        float extent = Vector3.Distance(profile.ExhaustSlot, new Vector3(0f, profile.FrontTapHeight, profile.FrontTapZ));
        var wide = Loudness.Place(profile.SourceLevelDb, extent);
        var e = h.Mixer.Latest[7];
        Assert.Equal(wide.ReferenceDistance, e.MinDistance, 3);
        Assert.Equal(wide.Gain, e.Volume, 4);
        // ...and beyond the extent the two placements are the same thing (Widen holds the product).
        var point = Loudness.Place(profile.SourceLevelDb);
        Assert.Equal(point.Gain * point.ReferenceDistance, wide.Gain * wide.ReferenceDistance, 4);
    }
}
