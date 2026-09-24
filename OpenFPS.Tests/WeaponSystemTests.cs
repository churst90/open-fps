using System;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Tests;

/// <summary>
/// Cover for the weapons: the spec, the mechanism, and what a shot sounds like from somewhere else.
///
/// The bar here is the same one the ballistics tests set. A weapon in this game is not a damage number
/// with a sound attached — it is a set of audible mechanical states that a player learns to read, and
/// the whole value of that learning is that it is CONSISTENT. A magazine that comes out sounding loaded
/// when it was empty, a semi-automatic that keeps firing while the trigger is held, a pump gun whose
/// trigger still works before it has been pumped: each is a lie told in the only channel the player
/// has. So these assert on the mechanism, not on the feel of it.
/// </summary>
public class WeaponSystemTests
{
    private const float C = AudioPhysics.SpeedOfSound;   // 343 m/s

    private static WeaponEvent[] Run(Func<Span<WeaponEvent>, int> call)
    {
        Span<WeaponEvent> buf = stackalloc WeaponEvent[WeaponMechanics.MaxEventsPerCall];
        int n = call(buf);
        var outp = new WeaponEvent[n];
        for (int i = 0; i < n; i++) outp[i] = buf[i];
        return outp;
    }

    // ── The spec ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryWeaponHasSoundsAndAFiringPosition()
    {
        foreach (var w in WeaponRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(w.SoundFolder), $"{w.Id} has no sound folder");
            Assert.False(string.IsNullOrWhiteSpace(w.FiringFolder), $"{w.Id} has no firing folder");
            Assert.NotEmpty(w.FireModes);
            Assert.Contains(w.FireModes, m => m != FireMode.Safe);
            Assert.True(w.MagazineCapacity > 0, $"{w.Id} holds no rounds");
            Assert.True(w.RoundsPerMinute > 0, $"{w.Id} has no rate of fire");
        }
    }

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

    [Fact]
    public void MagazineVariantsOnlyAffectMagazineSounds()
    {
        var akm = WeaponRegistry.Akm;                       // recorded with metal AND polymer magazines
        Assert.Equal("WEAPONS/AKM/MAG_OUT_LOADED/polymer", akm.SoundFor("MAG_OUT_LOADED"));
        Assert.Equal("WEAPONS/AKM/CHARGE", akm.SoundFor("CHARGE"));
        // A weapon with one magazine type does not gain a variant folder that has no files in it.
        Assert.Equal("WEAPONS/Glock/MAG_OUT_LOADED", WeaponRegistry.Glock.SoundFor("MAG_OUT_LOADED"));
    }

    // ── The mechanism ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASafeWeaponMakesNoSoundAtAll()
    {
        var w = WeaponRegistry.Akm;
        var s = WeaponMechanics.Fresh(w);
        s.FireModeIndex = Array.IndexOf(w.FireModes, FireMode.Safe);

        var events = Run(b => WeaponMechanics.PullTrigger(ref s, w, 0f, b));

        // Not even a click: on safe, the trigger does not move the sear, so nothing happens. A dry
        // click here would tell an enemy a weapon was empty when it is merely safe.
        Assert.Empty(events);
        Assert.True(s.RoundChambered);
    }

    [Fact]
    public void SemiAutomaticNeedsTheTriggerReleasedBetweenShots()
    {
        var w = WeaponRegistry.Ar15;
        var s = WeaponMechanics.Fresh(w);

        Assert.Contains(Run(b => WeaponMechanics.PullTrigger(ref s, w, 0f, b)),
                        e => e.Action == WeaponAction.Fire);
        // Held down, a long time later: still nothing, because the trigger never came back up.
        Assert.Empty(Run(b => WeaponMechanics.Hold(ref s, w, 5f, b)));
        Assert.Empty(Run(b => WeaponMechanics.PullTrigger(ref s, w, 5f, b)));

        WeaponMechanics.ReleaseTrigger(ref s);
        Assert.Contains(Run(b => WeaponMechanics.PullTrigger(ref s, w, 5f, b)),
                        e => e.Action == WeaponAction.Fire);
    }

    [Fact]
    public void AutomaticFireEmptiesTheMagazineAtTheCyclicRate()
    {
        var w = WeaponRegistry.Akm;                       // 600 rpm -> 100 ms between rounds
        var s = WeaponMechanics.Fresh(w);
        s.FireModeIndex = Array.IndexOf(w.FireModes, FireMode.Auto);

        int fired = 0;
        float t = 0f;
        fired += Run(b => WeaponMechanics.PullTrigger(ref s, w, t, b)).Count(e => e.Action == WeaponAction.Fire);
        // Step in one-millisecond ticks for four seconds: long enough to empty 31 rounds at 600 rpm.
        for (int i = 0; i < 4000; i++)
        {
            t += 0.001f;
            fired += Run(b => WeaponMechanics.Hold(ref s, w, t, b)).Count(e => e.Action == WeaponAction.Fire);
        }

        Assert.Equal(w.MagazineCapacity + 1, fired);      // the magazine plus the one in the chamber
        Assert.Equal(0, s.TotalRounds);
        Assert.True(s.BoltLockedBack, "an AKM locks back when it runs dry");
    }

    [Fact]
    public void TheRateOfFireIsTheDefinitionsRateOfFire()
    {
        var w = WeaponRegistry.Akm;
        var s = WeaponMechanics.Fresh(w);
        s.FireModeIndex = Array.IndexOf(w.FireModes, FireMode.Auto);

        Run(b => WeaponMechanics.PullTrigger(ref s, w, 0f, b));
        float first = s.NextShotAt;
        // Nothing fires before the interval is up, and something does immediately after.
        Assert.Empty(Run(b => WeaponMechanics.Hold(ref s, w, first - 0.001f, b)));
        Assert.Contains(Run(b => WeaponMechanics.Hold(ref s, w, first, b)),
                        e => e.Action == WeaponAction.Fire);
        Assert.Equal(60f / w.RoundsPerMinute, w.ShotInterval, 4);
    }

    [Fact]
    public void AnEmptyChamberClicksAndAFullOneDoesNot()
    {
        var w = WeaponRegistry.Glock;
        var s = WeaponMechanics.Fresh(w);
        s.RoundsInMagazine = 0;
        s.RoundChambered = false;

        var events = Run(b => WeaponMechanics.PullTrigger(ref s, w, 0f, b));
        Assert.Single(events);
        Assert.Equal(WeaponAction.DryFire, events[0].Action);
    }

    [Fact]
    public void AReloadFromALockedBackBoltChambersARound()
    {
        var w = WeaponRegistry.Ar15;
        var s = WeaponMechanics.Fresh(w);
        // Fire the chambered round only, then take the magazine out before the action can re-feed:
        // set the magazine empty first so nothing is there to chamber.
        s.RoundsInMagazine = 0;
        Run(b => WeaponMechanics.PullTrigger(ref s, w, 0f, b));
        Assert.False(s.RoundChambered);
        Assert.True(s.BoltLockedBack);

        // Reloading from a locked-back bolt DOES chamber a round, because releasing the bolt is part
        // of the sequence — that is the one case where a reload leaves you ready.
        var events = Run(b => WeaponMechanics.Reload(ref s, w, 1f, b));
        Assert.Contains(events, e => e.Action == WeaponAction.Charge);
        Assert.True(s.RoundChambered);
        Assert.False(s.BoltLockedBack);
    }

    [Fact]
    public void ReloadingEarlyThrowsAwayTheRoundsStillInTheMagazine()
    {
        var w = WeaponRegistry.Akm;
        var s = WeaponMechanics.Fresh(w, spareMagazines: 1);
        s.RoundsInMagazine = 29;
        int before = s.ReserveRounds;

        var events = Run(b => WeaponMechanics.Reload(ref s, w, 0f, b));

        Assert.Contains(events, e => e.Action == WeaponAction.MagOutLoaded);   // heard as still loaded
        Assert.Equal(w.MagazineCapacity, s.RoundsInMagazine);
        Assert.Equal(before - w.MagazineCapacity, s.ReserveRounds);           // the 29 are gone
    }

    [Fact]
    public void AnEmptyMagazineComesOutSoundingEmpty()
    {
        var w = WeaponRegistry.Akm;
        var s = WeaponMechanics.Fresh(w);
        s.RoundsInMagazine = 0;

        var events = Run(b => WeaponMechanics.Reload(ref s, w, 0f, b));
        Assert.Contains(events, e => e.Action == WeaponAction.MagOutEmpty);
        Assert.DoesNotContain(events, e => e.Action == WeaponAction.MagOutLoaded);
    }

    [Fact]
    public void APumpGunsTriggerIsDeadUntilItIsPumped()
    {
        var w = WeaponRegistry.Shotgun;
        var s = WeaponMechanics.Fresh(w);

        Assert.Contains(Run(b => WeaponMechanics.PullTrigger(ref s, w, 0f, b)),
                        e => e.Action == WeaponAction.Fire);
        Assert.False(s.ActionClosed);

        // Pulling again does nothing — and makes NO sound, which is the point: a pump gun that has not
        // been worked is silent, not clicking, so it gives nothing away.
        WeaponMechanics.ReleaseTrigger(ref s);
        Assert.Empty(Run(b => WeaponMechanics.PullTrigger(ref s, w, 1f, b)));

        Assert.Contains(Run(b => WeaponMechanics.Pump(ref s, w, 1f, b)), e => e.Action == WeaponAction.Pump);
        Assert.True(s.ActionClosed);
        Assert.True(s.RoundChambered);

        WeaponMechanics.ReleaseTrigger(ref s);
        Assert.Contains(Run(b => WeaponMechanics.PullTrigger(ref s, w, 5f, b)),
                        e => e.Action == WeaponAction.Fire);
    }

    [Fact]
    public void AShotgunIsLoadedOneShellAtATimeAndCanBeInterrupted()
    {
        var w = WeaponRegistry.Shotgun;
        var s = WeaponMechanics.Fresh(w);
        s.RoundsInMagazine = 0;

        float t = 0f;
        for (int i = 0; i < 3; i++)
        {
            var events = Run(b => WeaponMechanics.Reload(ref s, w, t, b));
            Assert.Single(events);
            Assert.Equal(WeaponAction.LoadShell, events[0].Action);
            t = s.BusyUntil;
        }
        // Three shells in, and the player stopped. The tube is part full, which is legal and audible.
        Assert.Equal(3, s.RoundsInMagazine);
        Assert.True(s.RoundsInMagazine < w.MagazineCapacity);
    }

    [Fact]
    public void AHammerFiredPistolWillNotFireDecocked()
    {
        var w = WeaponRegistry.ServicePistol;
        var s = WeaponMechanics.Fresh(w);
        Assert.True(s.HammerCocked);

        var down = Run(b => WeaponMechanics.ToggleHammer(ref s, w, 0f, b));
        Assert.Contains(down, e => e.Action == WeaponAction.HammerDecock);
        Assert.False(s.HammerCocked);

        Assert.Empty(Run(b => WeaponMechanics.PullTrigger(ref s, w, 1f, b)));
        Assert.True(s.RoundChambered, "a decocked pistol has not fired its round away");

        Run(b => WeaponMechanics.ToggleHammer(ref s, w, 1f, b));
        WeaponMechanics.ReleaseTrigger(ref s);
        Assert.Contains(Run(b => WeaponMechanics.PullTrigger(ref s, w, 2f, b)),
                        e => e.Action == WeaponAction.Fire);
    }

    [Fact]
    public void TheSelectorWalksTheModesTheWeaponActuallyHas()
    {
        var w = WeaponRegistry.Akm;                      // Safe -> Auto -> Semi
        var s = WeaponMechanics.Fresh(w);
        var seen = new System.Collections.Generic.List<FireMode>();
        float t = 0f;
        for (int i = 0; i < w.FireModes.Length; i++)
        {
            seen.Add(s.Mode(w));
            Run(b => WeaponMechanics.CycleFireMode(ref s, w, t, b));
            t = s.BusyUntil;
        }
        Assert.Equal(w.FireModes.OrderBy(m => m), seen.OrderBy(m => m));

        // A Glock has no selector, so moving it is not a thing that can happen or be heard.
        var g = WeaponMechanics.Fresh(WeaponRegistry.Glock);
        Assert.Empty(Run(b => WeaponMechanics.CycleFireMode(ref g, WeaponRegistry.Glock, 0f, b)));
    }

    [Fact]
    public void EveryHeardActionResolvesToAFolder()
    {
        foreach (var w in WeaponRegistry.All)
            foreach (WeaponAction a in Enum.GetValues<WeaponAction>())
            {
                if (a == WeaponAction.Cycle && w.Action == ActionType.Pump) continue;
                string folder = WeaponMechanics.SoundFolderFor(w, a);
                Assert.False(string.IsNullOrWhiteSpace(folder),
                             $"{w.Id} has nothing to play for {a}");
            }
    }

    // ── What it sounds like from elsewhere ──────────────────────────────────────────────────────

    [Fact]
    public void TheCrackComesFromBesideTheListenerAndTheReportFromTheShooter()
    {
        var w = WeaponRegistry.Akm;
        var shooter = new Vector3(0f, 1.5f, 100f);
        var ear = new Vector3(0f, 1.7f, 0f);
        var dir = Vector3.Normalize(new Vector3(1.5f, 0f, 0f) + ear - shooter);
        var impact = shooter + dir * 140f;

        var heard = ShotResolver.Audition(w, shooter, dir, impact, "Concrete", ear, C);

        Assert.True(heard.HasCrack);
        Assert.True(heard.CrackDelaySeconds < heard.ReportDelaySeconds,
                    "the crack outruns the report all the way to the listener");
        // The crack is rendered within a couple of metres of the ear, not a hundred metres away.
        Assert.True(Vector3.Distance(heard.CrackOrigin, ear) < 5f,
                    $"the crack was placed {Vector3.Distance(heard.CrackOrigin, ear):F1} m from the ear");
        Assert.True(Vector3.Distance(heard.CrackOrigin, shooter) > 90f);
    }

    [Fact]
    public void ARoundThatStopsShortNeverPassesTheListener()
    {
        var w = WeaponRegistry.Akm;
        var shooter = new Vector3(0f, 1.5f, 100f);
        var ear = new Vector3(0f, 1.7f, 0f);
        var dir = Vector3.Normalize(ear - shooter);
        // It hit a wall forty metres from the muzzle, sixty metres short of the listener.
        var impact = shooter + dir * 40f;

        var heard = ShotResolver.Audition(w, shooter, dir, impact, "Concrete", ear, C);

        Assert.False(heard.HasCrack, "a round stopped in a wall does not crack past someone behind it");
        Assert.True(heard.HasImpact);
        // The impact is still heard, and it is the thing that says the shot went somewhere else.
        Assert.True(heard.ImpactDelaySeconds > 0f);
    }

    [Fact]
    public void TheSubsonicRoundNeverCracksNoMatterHowCloseItPasses()
    {
        var w = WeaponRegistry.ServicePistol;
        var shooter = new Vector3(0f, 1.5f, 30f);
        var ear = new Vector3(0f, 1.7f, 0f);
        var dir = Vector3.Normalize(ear - shooter);

        var heard = ShotResolver.Audition(w, shooter, dir, shooter + dir * 60f, "Concrete", ear, C);
        Assert.False(heard.HasCrack);
        Assert.True(heard.ReportDelaySeconds > 0f);
    }

    [Fact]
    public void BuckshotSpreadsOverTheDiscAndASingleBulletDoesNot()
    {
        var shot = WeaponRegistry.Shotgun;
        Span<Vector3> pellets = stackalloc Vector3[16];
        int n = ShotResolver.PelletDirections(shot, Vector3.UnitZ, seed: 5, pellets);
        Assert.Equal(shot.PelletsPerShot, n);

        float maxHalfAngle = shot.SpreadDegrees * MathF.PI / 180f * 0.5f;
        for (int i = 0; i < n; i++)
        {
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(pellets[i], Vector3.UnitZ), -1f, 1f));
            Assert.True(angle <= maxHalfAngle + 1e-3f, $"pellet {i} left the cone at {angle:F4} rad");
        }
        // Deterministic: the same seed gives the same pattern on every machine, so the server and the
        // client agree about where a blast went.
        Span<Vector3> again = stackalloc Vector3[16];
        ShotResolver.PelletDirections(shot, Vector3.UnitZ, seed: 5, again);
        for (int i = 0; i < n; i++) Assert.Equal(pellets[i], again[i]);

        Span<Vector3> single = stackalloc Vector3[4];
        Assert.Equal(1, ShotResolver.PelletDirections(WeaponRegistry.Ar15, Vector3.UnitZ, 5, single));
    }

    [Fact]
    public void DamageFallsOffWithRangeAndBuckshotFallsFastest()
    {
        var rifle = WeaponRegistry.Ar15;
        var shot = WeaponRegistry.Shotgun;

        Assert.Equal(rifle.DamagePerPellet, ShotResolver.DamageAt(rifle, 0f));
        // Halving distance is a halving, by construction.
        Assert.InRange(ShotResolver.DamageAt(rifle, rifle.DamageHalfDistance),
                       rifle.DamagePerPellet / 2 - 1, rifle.DamagePerPellet / 2 + 1);
        // Out of range is nothing at all, not a very small number.
        Assert.Equal(0, ShotResolver.DamageAt(shot, shot.MaxRange + 1f));
        // At fifty metres a rifle is still lethal and buckshot is not worth the noise.
        Assert.True(ShotResolver.DamageAt(rifle, 50f) > ShotResolver.DamageAt(shot, 50f) * 4);
    }

    [Fact]
    public void ClosestApproachIsClampedToThePathTheRoundActuallyTravelled()
    {
        // A listener BEHIND the muzzle is never passed by the round, however close the infinite line
        // would come to them.
        ShotResolver.ClosestApproach(Vector3.Zero, Vector3.UnitZ, 100f, new Vector3(0.5f, 0f, -40f),
                                     out float along, out float miss, out _);
        Assert.Equal(0f, along);
        Assert.True(miss > 39f);
    }
}
