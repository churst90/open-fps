using System.Numerics;

namespace OpenFPS.Tests;

/// <summary>
/// Your own footsteps against a street full of other people's. Reported 2026-09-27, with 310 people
/// on the city: "I'm not hearing my own footsteps when I walk outside". Everybody's steps shared one
/// pool of twelve voice ids, taken in turn, and a submission under an id replaces the one waiting.
/// </summary>
public class FootstepPoolTests
{
    [Fact]
    public void A_crowd_walking_round_you_does_not_take_your_own_step()
    {
        var h = new ClientAudioHarness(Sounds());
        h.StandAt(Vector3.Zero);
        h.Tick();

        h.Audio.OnOwnFootstep(new Vector3(0f, 0f, 0.2f), "Concrete", "0");
        // Forty strangers' steps in the same frame, a few metres off.
        for (int i = 0; i < 40; i++)
            h.Audio.OnPlayerFootstep(new Vector3(3f + i * 0.1f, 0f, 2f), "Concrete", "0");
        h.Tick(3);

        var own = h.Mixer.Started.Where(e => e.EntityId <= -100 && e.EntityId > -100 - 12).ToList();
        Assert.NotEmpty(own);
        Assert.All(own, e => Assert.True(e.FollowsListener, "a stranger's step in your own feet's slot"));
    }

    [Fact]
    public void A_step_too_far_away_to_hear_is_not_made()
    {
        var h = new ClientAudioHarness(Sounds());
        h.StandAt(Vector3.Zero);
        h.Tick();
        h.Audio.OnPlayerFootstep(new Vector3(60f, 0f, 0f), "Concrete", "0");
        h.Tick(3);
        Assert.DoesNotContain(h.Mixer.Started, e => e.EntityId <= -300 && e.EntityId > -364);
    }

    [Fact]
    public void Only_your_own_steps_are_pinned_above_the_voice_budget()
    {
        var h = new ClientAudioHarness(Sounds());
        h.StandAt(Vector3.Zero);
        h.Tick();
        h.Audio.OnOwnFootstep(new Vector3(0f, 0f, 0.2f), "Concrete", "0");
        h.Audio.OnPlayerFootstep(new Vector3(3f, 0f, 2f), "Concrete", "0");
        h.Tick(3);
        Assert.Contains(h.Mixer.Started, e => e.FollowsListener && e.Essential);
        Assert.Contains(h.Mixer.Started, e => !e.FollowsListener && e.EntityId <= -300 && e.EntityId > -364 && !e.Essential);
    }

    private static string Sounds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS"))) dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS")
                           : "/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS";
    }
}
