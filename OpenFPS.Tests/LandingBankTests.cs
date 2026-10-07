using System.Numerics;

namespace OpenFPS.Tests;

/// <summary>
/// A jump's landing plays a take from the LANDING bank. It asked for an impact of force 0, which is a
/// footstep, so every landing was a step take (probable bug 3 of 2026-10-07).
/// </summary>
public class LandingBankTests
{
    private static string Sounds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS"))) dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS")
                           : "/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS";
    }

    [Theory]
    [InlineData("Concrete")]
    [InlineData("Wood")]
    public void YourOwnLandingIsALandingTake(string surface)
    {
        var h = new ClientAudioHarness(Sounds());
        h.StandAt(Vector3.Zero);
        h.Tick();
        h.Audio.OnOwnLand(Vector3.Zero, surface, "0");
        h.Tick(3);
        var landing = Assert.Single(h.Mixer.Started, e => e.EntityId == -50);
        Assert.StartsWith($"LANDING/{surface}/", landing.SoundId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SomebodyElsesLandingIsALandingTakeToo()
    {
        var h = new ClientAudioHarness(Sounds());
        h.StandAt(Vector3.Zero);
        h.Tick();
        h.Audio.OnPlayerLand(new Vector3(3f, 0f, 2f), "Tile", "0");
        h.Tick(3);
        var landing = Assert.Single(h.Mixer.Started, e => e.EntityId == -50);
        Assert.StartsWith("LANDING/Tile/", landing.SoundId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStepIsStillAStepTake()
    {
        var h = new ClientAudioHarness(Sounds());
        h.StandAt(Vector3.Zero);
        h.Tick();
        h.Audio.OnOwnFootstep(new Vector3(0f, 0f, 0.2f), "Concrete", "0");
        h.Tick(3);
        Assert.Contains(h.Mixer.Started, e => e.SoundId.StartsWith("FOOTSTEPS/Concrete/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(h.Mixer.Started, e => e.SoundId.StartsWith("LANDING/", StringComparison.OrdinalIgnoreCase));
    }
}
