using System.Numerics;
using OpenFPS.Client.Core;
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
