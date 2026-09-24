using System;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for the crack-and-thump, which is a gameplay instrument rather than a decoration.
///
/// A supersonic round makes two sounds: the shock as it passes the listener, and the muzzle report
/// chasing it at the speed of sound. The round outruns its own report, so the crack lands FIRST, and the
/// gap between them is d·(1/c − 1/v) — a direct readout of how far away the shooter is, available to a
/// player who cannot see them. These tests hold that relationship to the physics rather than to a
/// designer's taste, because the moment it stops being the physics it stops being trustworthy, and a
/// player who has learned to read range by ear would be quietly misled.
/// </summary>
public class BallisticsTests
{
    private const float C = AudioPhysics.SpeedOfSound;   // 343 m/s
    private const float RifleV = 880f;

    [Theory]
    [InlineData(25f)]
    [InlineData(100f)]
    [InlineData(400f)]
    public void TheCrackAlwaysArrivesBeforeTheReport(float distance)
    {
        float crack = Ballistics.CrackArrival(distance, 1.5f, RifleV, C);
        float report = Ballistics.ReportArrival(distance, C);
        Assert.True(crack < report,
            $"at {distance} m the crack ({crack * 1000:F1} ms) must precede the report ({report * 1000:F1} ms)");
    }

    [Theory]
    [InlineData(25f)]
    [InlineData(50f)]
    [InlineData(100f)]
    [InlineData(200f)]
    [InlineData(400f)]
    public void TheGapReadsBackTheRangeItEncodes(float distance)
    {
        // The round trip: if a player could measure the gap exactly, they would recover the range
        // exactly. Anything else means the cue is lying about something.
        float gap = Ballistics.CrackToReportSeconds(distance, RifleV, C);
        float recovered = Ballistics.DistanceFromCrackToReport(gap, RifleV, C);
        Assert.Equal(distance, recovered, 1);
    }

    [Fact]
    public void TheGapIsBigEnoughToHearAndGrowsWithRange()
    {
        // Two sounds under about 30 ms apart fuse into one for most listeners. The cue is only useful
        // if it clears that at the ranges the game will actually use.
        float at25 = Ballistics.CrackToReportSeconds(25f, RifleV, C);
        float at100 = Ballistics.CrackToReportSeconds(100f, RifleV, C);
        float at400 = Ballistics.CrackToReportSeconds(400f, RifleV, C);

        Assert.True(at25 > 0.030f, $"at 25 m the gap is only {at25 * 1000:F0} ms, which would fuse");
        Assert.True(at100 > at25 && at400 > at100);
        // Linear in range, so the ear's estimate scales the whole way out.
        Assert.Equal(at100 * 4f, at400, 3);
    }

    [Fact]
    public void ASubsonicRoundMakesNoCrackAtAll()
    {
        // A 340 m/s pistol round never outruns its own sound, so there is nothing to hear passing —
        // and a suppressed weapon's silence is a gameplay fact, not an omission.
        Assert.False(Ballistics.MakesCrack(340f, 1.5f, C));
        Assert.True(Ballistics.MakesCrack(RifleV, 1.5f, C));
    }

    [Fact]
    public void ARoundThatPassesFarAwayMakesNoCrackEither()
    {
        Assert.True(Ballistics.MakesCrack(RifleV, Ballistics.MaxCrackMissDistance - 1f, C));
        Assert.False(Ballistics.MakesCrack(RifleV, Ballistics.MaxCrackMissDistance + 1f, C));
    }

    [Fact]
    public void AFasterRoundDragsATighterCone()
    {
        float fast = Ballistics.MachConeAngle(900f, C);
        float slow = Ballistics.MachConeAngle(450f, C);
        Assert.True(fast < slow);
        // At Mach 1 the cone opens out flat — the shock is no longer trailing anything.
        Assert.Equal(MathF.PI / 2f, Ballistics.MachConeAngle(C, C), 3);
    }

    [Fact]
    public void ColdAirStretchesTheGap()
    {
        // Sound slows in cold air while the bullet does not care, so the report takes longer to arrive
        // and the gap widens. The same temperature that moves every Doppler shift moves this too.
        float cold = Ballistics.CrackToReportSeconds(100f, RifleV, AudioPhysics.SpeedOfSoundAt(-20f));
        float warm = Ballistics.CrackToReportSeconds(100f, RifleV, AudioPhysics.SpeedOfSoundAt(40f));
        Assert.True(cold > warm);
    }

    // ── The rendered layers ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRenderedShotIsDryAndCentred()
    {
        // Dry because the engine makes its own room: a layer that rings on would put a second room
        // inside the one the player is standing in. Centred because a DC offset is wasted headroom and
        // a step in the waveform every time a voice starts or stops.
        foreach (var pcm in new[]
        {
            WeaponSynth.MuzzleBlast(WeaponProfile.Rifle),
            WeaponSynth.SupersonicCrack(WeaponProfile.Rifle, 1.5f),
            WeaponSynth.MechanicalAction(WeaponProfile.Rifle),
        })
        {
            Assert.NotEmpty(pcm);
            Assert.Equal(0f, Mean(pcm), 2);
            Assert.True(TailPeak(pcm) < 0.02f, "the layer is still ringing when its buffer ends");
            Assert.Equal(0f, pcm[^1], 3);
        }
    }

    /// <summary>
    /// A shot's energy is balanced the way a real one is: 20-45 per cent of it below 500 Hz.
    ///
    /// Measured on the NIJ recordings, the first 20 ms of clean shots at 20-40 m: 16-40 per cent below
    /// 500 Hz across the M16, the AK, the Glock and the Colt, about a quarter typically (a handheld
    /// recorder rolls off below 80 Hz, so a little more in truth). This test used to demand MORE than
    /// half, from a listening test that called a white-noise-heavy synthesis "a burst of white noise";
    /// the complaint was right and the number it produced was not, and the measurement replaces it.
    /// Both ends matter: under 20 per cent is a hiss, over 45 a thud. The shotgun, never recorded, is
    /// the lowest-weighted of the five and still under 60.
    /// </summary>
    [Fact]
    public void AShotIsBalancedTheWayARealOneIs()
    {
        float Share(WeaponDefinition w)
        {
            var pcm = WeaponSynth.MuzzleBlast(WeaponProfile.From(w));
            float below500 = BandEnergy(pcm, 10f, 500f);
            float above500 = BandEnergy(pcm, 500f, 20000f);
            return below500 / (below500 + above500);
        }
        // The four that were measured, in the range they measured.
        foreach (var w in new[] { WeaponRegistry.Akm, WeaponRegistry.Ar15, WeaponRegistry.Glock, WeaponRegistry.ServicePistol })
        {
            float share = Share(w);
            Assert.True(share >= 0.20f && share <= 0.45f, $"{w.Id}: {share * 100f:F0}% of the energy below 500 Hz");
        }
        // The shotgun has no recording: the biggest bore and charge, so the lowest-weighted of all,
        // and still not a thud.
        float shotgun = Share(WeaponRegistry.Shotgun);
        Assert.True(shotgun < 0.60f, $"shotgun: {shotgun * 100f:F0}% below 500 Hz");
        foreach (var w in WeaponRegistry.All.Where(w => w != WeaponRegistry.Shotgun))
            Assert.True(shotgun > Share(w), $"{w.Id} is weighted lower than a 12 gauge");
    }

    /// <summary>Energy in a band, by one-pole filtering. Crude and adequate: the question is whether
    /// two thirds of the energy is in the bottom two octaves, not where a notch sits.</summary>
    private static float BandEnergy(float[] pcm, float lowHz, float highHz)
    {
        const float sampleRate = WeaponSynth.SampleRate;
        float a = 1f - MathF.Exp(-2f * MathF.PI * lowHz / sampleRate);
        float b = 1f - MathF.Exp(-2f * MathF.PI * highHz / sampleRate);
        float s1 = 0f, s2 = 0f, energy = 0f;
        foreach (float x in pcm)
        {
            s2 += b * (x - s2);
            s1 += a * (s2 - s1);
            float y = s2 - s1;
            energy += y * y;
        }
        return energy;
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        // The same shot has to sound the same on every machine, or a player cannot learn it.
        Assert.Equal(WeaponSynth.MuzzleBlast(WeaponProfile.Rifle),
                     WeaponSynth.MuzzleBlast(WeaponProfile.Rifle));
    }

    [Fact]
    public void TwoWeaponsDoNotSoundTheSame()
    {
        // The profile has to actually reach the waveform: identifying a weapon by ear is the whole
        // reason for having more than one.
        var rifle = WeaponSynth.MuzzleBlast(WeaponProfile.Rifle);
        var pistol = WeaponSynth.MuzzleBlast(WeaponProfile.Pistol);
        Assert.NotEqual(rifle.Length, pistol.Length);
    }

    [Fact]
    public void ANearMissIsShorterAndSharperThanADistantOne()
    {
        var near = WeaponSynth.SupersonicCrack(WeaponProfile.Rifle, 1f);
        var far = WeaponSynth.SupersonicCrack(WeaponProfile.Rifle, 20f);
        Assert.True(far.Length > near.Length);
    }

    private static float Mean(float[] v)
    {
        double s = 0;
        foreach (var x in v) s += x;
        return (float)(s / v.Length);
    }

    private static float TailPeak(float[] v)
    {
        float m = 0f;
        for (int i = v.Length - v.Length / 10; i < v.Length; i++) m = MathF.Max(m, MathF.Abs(v[i]));
        return m;
    }
}
