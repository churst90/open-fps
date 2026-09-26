using System;
using System.IO;
using System.Linq;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// "My foot steps seem to be dropping out ... I only hear foot steps on few key presses." The
/// footstep banks' takes spread over 22 dB, and on carpet the quiet third vanished under the room.
/// Corrected, every take in a bank is at the bank's median; a bank's level against another bank's
/// (carpet against wood) is left alone.
/// </summary>
public class TakeLevelsTests
{
    // From this file's own path: the tests build under /tmp, far from the assets.
    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;

    private static string? Footsteps()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Here())!);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            string c = Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS", "FOOTSTEPS");
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    [Theory]
    [InlineData("Carpet")]
    [InlineData("Concrete")]
    [InlineData("Wood")]
    [InlineData("Tile")]
    public void EveryTakeInABankPlaysAtTheBanksLevel(string material)
    {
        string? root = Footsteps();
        Assert.NotNull(root);
        string dir = Path.Combine(root!, material);
        if (!Directory.Exists(dir)) return;
        var raw = Directory.GetFiles(dir, "*.wav").Select(f => (File: f, Db: TakeLevels.ImpactDb(f)!.Value)).ToList();
        double rawSpread = raw.Max(r => r.Db) - raw.Min(r => r.Db);
        var corrected = raw.Select(r => r.Db + 20 * Math.Log10(TakeLevels.GainFor(r.File))).ToList();
        double spread = corrected.Max() - corrected.Min();
        Assert.True(rawSpread > 6, $"{material}: the bank was already even ({rawSpread:F1} dB) — the test proves nothing");
        Assert.True(spread < 1.0, $"{material}: takes still spread {spread:F1} dB after correction (were {rawSpread:F1})");
    }
}
