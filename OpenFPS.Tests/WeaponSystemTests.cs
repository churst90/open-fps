using System;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for the weapons: each one renders as itself, and its report matches the measured ones.
/// </summary>
public class WeaponSystemTests
{
    private const float C = AudioPhysics.SpeedOfSound;   // 343 m/s

    [Fact]
    public void EveryWeaponSoundsLikeItselfAndNotLikeAnother()
    {
        // Five weapons must be five sounds. When the blast came from a table of three profiles keyed
        // by name, the Glock and the .45 rendered byte-identical — and a 9 mm and a .45 are about as
        // different as two handguns get.
        var rendered = WeaponRegistry.All
            .ToDictionary(w => w.Id, w => WeaponSynth.MuzzleBlast(WeaponProfile.From(w)));

        foreach (var (idA, a) in rendered)
            foreach (var (idB, b) in rendered)
            {
                if (string.CompareOrdinal(idA, idB) >= 0) continue;
                Assert.False(a.Length == b.Length && a.SequenceEqual(b),
                             $"{idA} and {idB} render an identical blast");
            }

        // And the profile always carries the weapon's own velocity, so a crack that is scheduled is a
        // crack that renders.
        foreach (var w in WeaponRegistry.All)
        {
            Assert.Equal(w.MuzzleVelocity, WeaponProfile.From(w).MuzzleVelocity);
            if (w.IsSupersonic(C))
                Assert.NotEmpty(WeaponSynth.SupersonicCrack(WeaponProfile.From(w), 2f));
        }
    }

    /// <summary>
    /// The reports are what the recordings say they are. Every rifle and pistol in the NIJ set has a
    /// positive phase of 0.35-0.56 ms at 20-40 m and a spectrum that falls from somewhere near 2.5-3
    /// kHz; the shotgun, with no recording, is the largest bore and charge here and so the longest
    /// pulse and the darkest report. (This used to order a body resonance and a thump that no
    /// recording measured.)
    /// </summary>
    [Fact]
    public void TheReportsAreTheMeasuredOnesAndTheShotgunIsTheBiggest()
    {
        foreach (var w in new[] { WeaponRegistry.Akm, WeaponRegistry.Ar15, WeaponRegistry.Glock, WeaponRegistry.ServicePistol })
        {
            Assert.InRange(w.ReportPositivePhaseMs, 0.35f, 0.56f);
            Assert.InRange(w.ReportCornerHz, 2500f, 3500f);
        }
        foreach (var w in WeaponRegistry.All.Where(w => w != WeaponRegistry.Shotgun))
        {
            Assert.True(WeaponRegistry.Shotgun.ReportPositivePhaseMs > w.ReportPositivePhaseMs, $"{w.Id} has a longer pulse than a 12 gauge");
            Assert.True(WeaponRegistry.Shotgun.ReportCornerHz < w.ReportCornerHz, $"{w.Id} is darker than a 12 gauge");
        }
    }

    /// <summary>
    /// The report is as short as a real one. At 20 m a real rifle's report falls 20 dB within 2.5-3.5
    /// ms of its peak and 30 dB within 4-7; the synthesis before this one took 18-26 ms to fall 20 dB,
    /// which was most of why it did not sound like a gun. Measured at the source here, in 0.25 ms
    /// windows: down 20 dB inside 5 ms, 30 dB inside 9, for every weapon.
    /// </summary>
    [Fact]
    public void AReportIsAsShortAsARealOne()
    {
        foreach (var w in WeaponRegistry.All)
        {
            var x = WeaponSynth.MuzzleBlast(WeaponProfile.From(w));
            int win = WeaponSynth.SampleRate / 4000;
            var env = Enumerable.Range(0, x.Length / win)
                .Select(k => MathF.Sqrt(x.Skip(k * win).Take(win).Sum(v => v * v) / win)).ToArray();
            int peak = Array.IndexOf(env, env.Max());
            float Reach(float db)
            {
                for (int k = peak; k < env.Length; k++)
                    if (20 * MathF.Log10(env[k] / env[peak] + 1e-12f) < db) return (k - peak) * 0.25f;
                return float.MaxValue;
            }
            Assert.True(Reach(-20f) < 5f, $"{w.Id} takes {Reach(-20f)} ms to fall 20 dB");
            Assert.True(Reach(-30f) < 9f, $"{w.Id} takes {Reach(-30f)} ms to fall 30 dB");
        }
    }

    [Fact]
    public void OnlyTheFortyFiveIsSubsonic()
    {
        Assert.False(WeaponRegistry.ServicePistol.IsSupersonic(C));
        Assert.True(WeaponRegistry.Akm.IsSupersonic(C));
        Assert.True(WeaponRegistry.Ar15.IsSupersonic(C));
        Assert.True(WeaponRegistry.Glock.IsSupersonic(C));
    }
}
