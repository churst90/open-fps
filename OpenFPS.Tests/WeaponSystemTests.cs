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

        // And the profile always carries the weapon's own velocity.
        foreach (var w in WeaponRegistry.All)
            Assert.Equal(w.MuzzleVelocity, WeaponProfile.From(w).MuzzleVelocity);
    }

    /// <summary>
    /// The reports are what the recordings say they are, and the shotgun is the biggest.
    ///
    /// Updated 2026-10-02. This held the positive phase to 0.35-0.56 ms and the corner to 2.5-3.5 kHz,
    /// the values of the one-pole model; the bubble model (WeaponSynth.MuzzleBlast) sets its note from
    /// the shock's own zero crossing, 0.20-0.28 ms for the five measured guns, which renders at 0.26-0.32
    /// ms after 20-40 m (against 0.2-0.5 in the recordings), and its corner is the gas's alone. The band
    /// balance itself is held against the recordings in GunfireSpecTests. The shotgun, never recorded,
    /// is the largest bore and charge: the longest pulse, and the darkest render by spectral centroid.
    /// </summary>
    [Fact]
    public void TheReportsAreTheMeasuredOnesAndTheShotgunIsTheBiggest()
    {
        foreach (var w in WeaponRegistry.All.Where(w => w != WeaponRegistry.Shotgun))
        {
            Assert.InRange(w.ReportPositivePhaseMs, 0.2f, 0.3f);
            Assert.InRange(w.ReportDamping, 0.2f, 0.7f);
        }
        float Centroid(WeaponDefinition w) => ReportMeasure.Measure(WeaponSynth.MuzzleBlast(WeaponProfile.From(w)), WeaponSynth.SampleRate).CentroidHz;
        foreach (var w in WeaponRegistry.All.Where(w => w != WeaponRegistry.Shotgun))
        {
            Assert.True(WeaponRegistry.Shotgun.ReportPositivePhaseMs > w.ReportPositivePhaseMs, $"{w.Id} has a longer pulse than a 12 gauge");
            Assert.True(Centroid(WeaponRegistry.Shotgun) < Centroid(w), $"{w.Id} is darker than a 12 gauge");
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
        // 410 m/s from a 6-inch barrel: only just, like the 9 mm.
        Assert.True(WeaponRegistry.Revolver357.IsSupersonic(C));
    }
}
