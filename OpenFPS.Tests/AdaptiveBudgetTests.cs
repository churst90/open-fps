using System.Numerics;
using System.Reflection;
using OpenFPS.Client.Core;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The budgets that give voices up when the mixer is short, and take them back. On 2026-10-07 the city's
/// mixer cost about 55 % with nothing in particular playing; the budget gave up the machines from ten to
/// one in the first thirty seconds after the map loaded and only ever took a voice back under 45 %, so the
/// fountain, the crossing bell, the trees and every train shared one voice for the rest of the hour
/// ("the city is more dead").
/// </summary>
public class AdaptiveBudgetTests
{
    private readonly ITestOutputHelper _o;
    public AdaptiveBudgetTests(ITestOutputHelper o) => _o = o;

    private static int Field(ClientAudioSystem audio, string name)
        => (int)typeof(ClientAudioSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio)!;

    private static void Run(Replay r, float load, double seconds)
    {
        ((StreamMixer)typeof(Replay).GetField("Mixer")!.GetValue(r)!).Load = load;
        int frames = (int)(seconds * ClientAudioSystem.UpdateHz);
        for (int f = 0; f < frames; f++)
        {
            r.Listener(Vector3.Zero, Vector3.Zero, 0f);
            r.Tick();
        }
    }

    [Fact]
    public void What_is_given_up_over_the_ceiling_comes_back_at_the_citys_ordinary_load()
    {
        int machinesFull = 0, machinesShed = 0, machinesBack = 0, placesShed = 0, placesBack = 0;
        Replay.RunScript(r =>
        {
            machinesFull = Field(r.Audio, "_adaptiveMachines");
            // Twenty seconds at 76 %: the first seconds on the city.
            Run(r, 0.76f, 20);
            machinesShed = Field(r.Audio, "_adaptiveMachines");
            placesShed = Field(r.Audio, "_adaptivePlaces");
            // Then the city's ordinary 58 %, for three minutes.
            Run(r, 0.58f, 180);
            machinesBack = Field(r.Audio, "_adaptiveMachines");
            placesBack = Field(r.Audio, "_adaptivePlaces");
        });
        _o.WriteLine($"machine voices {machinesFull} -> {machinesShed} over the ceiling -> {machinesBack} at 58 %; places 36 -> {placesShed} -> {placesBack}");
        Assert.True(machinesShed < machinesFull || placesShed < 36, "nothing was given up at 76 %");
        Assert.True(machinesShed >= 6, $"the machines went down to {machinesShed} voices");
        Assert.Equal(machinesFull, machinesBack);
        Assert.Equal(36, placesBack);
    }

    [Fact]
    public void At_the_ceiling_a_voice_does_not_come_and_go_every_few_seconds()
    {
        // A mixer that crosses the ceiling whenever it is given a voice back: each restore is followed by
        // a shed. The restores must slow down, not keep the budget churning.
        int restores = 0;
        Replay.RunScript(r =>
        {
            Run(r, 0.76f, 6);
            int Voices() => Field(r.Audio, "_adaptivePlaces") + Field(r.Audio, "_adaptiveMachines") + Field(r.Audio, "_adaptiveFront")
                          + Field(r.Audio, "_adaptiveDistant") + Field(r.Audio, "_adaptiveBudget");
            int last = Voices();
            bool given = false;
            for (int s = 0; s < 300; s++)
            {
                // Given one back: over the ceiling until it gives one up again.
                Run(r, given ? 0.76f : 0.6f, 1);
                int now = Voices();
                if (now > last) { restores++; given = true; }
                else if (now < last) given = false;
                last = now;
            }
        });
        _o.WriteLine($"{restores} restore(s) in five minutes of a mixer at its limit");
        Assert.True(restores <= 8, $"{restores} restores in five minutes: the budget churns at the ceiling");
    }

    [Fact]
    public void An_empty_binaural_pool_gives_voices_up_like_a_full_mixer()
    {
        int before = 0, after = 0;
        Replay.RunScript(r =>
        {
            var mixer = (StreamMixer)typeof(Replay).GetField("Mixer")!.GetValue(r)!;
            before = Field(r.Audio, "_adaptivePlaces") + Field(r.Audio, "_adaptiveMachines");
            mixer.Free = 2;
            Run(r, 0.3f, 8);
            after = Field(r.Audio, "_adaptivePlaces") + Field(r.Audio, "_adaptiveMachines");
        });
        _o.WriteLine($"places and machine voices {before} -> {after} with two binaural voices free");
        Assert.True(after < before, "a nearly empty binaural pool gave nothing up");
    }
}
