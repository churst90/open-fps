using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Every round is flown, from the hip as through the scope, and what it meets is what is in its way.
///
/// Cody, 2026-10-04: "if I'm on the second floor of the apartment build and I shoot, why do i hit
/// pedestrians? I should be hitting the wall and things in between." / "when I shoot sometimes it says
/// 'hit audience'... why?" / "there's no delay time between when i shoot and hear the hit or kill and
/// it seems to stop after so far ... if the bullet lands on the ground or roof, maybe I should know
/// about that too?" / "yes i do want the bullet whizzing and cracking sound, make it realistic."
/// </summary>
public class BulletFlightTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-bullets-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _o;
    public BulletFlightTests(ITestOutputHelper o) { _o = o; }
    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    private static readonly Air Still = Air.Standard;

    // ── What a round meets ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brandt Court, floor 2, at the east wall, firing down at somebody in the street. The wall on that
    /// floor is one part 88 m long whose centre is 40 m up the street; the hit-scan tested centres in a
    /// cone and never saw it, and hit the pedestrian. Flown, the round meets the wall a few centimetres
    /// from the muzzle.
    /// </summary>
    [Fact]
    public void FromTheSecondFloorTheWallStopsTheRoundNotThePedestrianBelow()
    {
        var g = new Range(_dir, "city");
        var shooter = g.Player("cody", new Vector3(-10f, 6.4f, 152f));
        g.Arm(shooter, "akm");
        var walker = g.Walker(new Vector3(5f, 0f, 152f));
        g.AimAt(shooter, new Vector3(5f, 1.2f, 152f));

        g.Fire(shooter);
        g.Run(0.5);

        Assert.Equal(100, g.World.Get<HealthComponent>(walker).Current);
        Assert.Empty(g.Confirms(shooter));
        string said = Assert.Single(g.Said(shooter));
        Assert.StartsWith("Hit Brandt Court east wall at ", said);
        _o.WriteLine(said);
    }

    // ── Aim assistance ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A pedestrian 37 m off, the aim 5 degrees to the side of them: a body is 0.7 degrees across there
    /// and a fine turn is 1, so without help Cody would almost never hit. Assisted, the gun is turned onto
    /// their chest and the round flown as ever; with the assist off it goes where it was pointed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AShotFiveDegreesOffAt37MetresIsAssistedOntoThem(bool assisted)
    {
        var g = new Range(_dir, "default");
        var feet = new Vector3(140f, 0f, 140f);
        var shooter = g.Player("shooter", feet);
        shooter.AimAssist = assisted;
        g.Arm(shooter, "akm");
        var walker = g.Walker(feet + new Vector3(0f, 0f, 37f));
        g.Face(shooter, 5f * MathF.PI / 180f, 0f);
        g.Fire(shooter);
        g.Run(0.3);
        if (assisted)
        {
            Assert.Equal(walker.Id, Assert.Single(g.Confirms(shooter)).TargetEntityId);
            Assert.Equal("Hit pedestrian at 37 metres.", Assert.Single(g.Said(shooter)));
        }
        else
        {
            Assert.Empty(g.Confirms(shooter));
            Assert.Equal(100, g.World.Get<HealthComponent>(walker).Current);
        }
    }

    /// <summary>The Brandt Court case with the assist on, as it is by default: the pedestrian is inside
    /// the cone, but the wall is between, so there is no assisting and the wall takes the round.</summary>
    [Fact]
    public void NoAssistThroughAWall()
    {
        var g = new Range(_dir, "city");
        var shooter = g.Player("cody", new Vector3(-10f, 6.4f, 152f));
        Assert.True(shooter.AimAssist);
        g.Arm(shooter, "akm");
        var walker = g.Walker(new Vector3(5f, 0f, 152f));
        // Level and 4 degrees off: the pedestrian is in the cone, the wall is in the way.
        g.Face(shooter, (90f + 4f) * MathF.PI / 180f, 0f);
        g.Fire(shooter);
        g.Run(0.5);
        Assert.Empty(g.Confirms(shooter));
        Assert.Equal(100, g.World.Get<HealthComponent>(walker).Current);
        Assert.StartsWith("Hit Brandt Court east wall at ", Assert.Single(g.Said(shooter)));
    }

    /// <summary>Of two people in the cone, the one nearest the aim line, though further away; an aim
    /// that already passes above the shoulders goes to the head.</summary>
    [Fact]
    public void TheAssistPrefersTheAimLineAndKeepsAHeadShotAHeadShot()
    {
        var g = new Range(_dir, "default");
        var feet = new Vector3(140f, 0f, 140f);
        var shooter = g.Player("shooter", feet);
        g.Arm(shooter, "akm");
        var far = g.Walker(feet + new Vector3(30f * MathF.Sin(1f * MathF.PI / 180f), 0f, 30f * MathF.Cos(1f * MathF.PI / 180f)));
        var near = g.Walker(feet + new Vector3(15f * MathF.Sin(4f * MathF.PI / 180f), 0f, 15f * MathF.Cos(4f * MathF.PI / 180f)));
        // Level from the hip at 1.5 m: chest height, under the shoulders.
        g.Face(shooter, 0f, 0f);
        g.Fire(shooter);
        g.Run(0.3);
        var hit = Assert.Single(g.Confirms(shooter));
        Assert.Equal(far.Id, hit.TargetEntityId);
        Assert.False(hit.Headshot);
        Assert.Equal(100, g.World.Get<HealthComponent>(near).Current);

        // Pointed a little up, so the aim passes 1.7 m up at the far one: the head.
        g.Now += 1;
        g.Face(shooter, 0f, -MathF.Atan(0.2f / 30f));
        g.Fire(shooter);
        g.Run(0.3);
        Assert.True(g.Confirms(shooter)[^1].Headshot);
        Assert.Equal(far.Id, g.Confirms(shooter)[^1].TargetEntityId);
    }

    /// <summary>With the hip's own scatter on top, the assist puts the round on a 37 m target about
    /// half to three quarters of the time: it turns the gun, it does not steady the hand.</summary>
    [Fact]
    public void AssistedHipFireAt37MetresStillScatters()
    {
        var g = new Range(_dir, "default");
        g.Combat.HipDispersionRadians = 0.007f;
        g.Combat.Scatter = new Random(11);
        var feet = new Vector3(140f, 0f, 140f);
        var shooter = g.Player("shooter", feet);
        var gun = g.Arm(shooter, "akm");
        var walker = g.Walker(feet + new Vector3(0f, 0f, 37f));
        g.World.Get<HealthComponent>(walker) = new HealthComponent { Current = 1_000_000, Max = 1_000_000 };
        g.Face(shooter, 3f * MathF.PI / 180f, 0f);
        int fired = 80;
        for (int i = 0; i < fired; i++)
        {
            Arms.Ammo(g.World, gun, WeaponRegistry.Akm).Rounds = 30;
            g.Fire(shooter);
            g.Run(0.2);
        }
        float rate = g.Confirms(shooter).Count / (float)fired;
        _o.WriteLine($"{g.Confirms(shooter).Count} hits in {fired} shots: {rate:P0}");
        Assert.InRange(rate, 0.4f, 0.85f);
    }

    [Fact]
    public void AimAssistIsASessionSettingTheServerHonours()
    {
        var session = new UserSession();
        Assert.True(session.AimAssist);
        Assert.StartsWith("Aim assist off", CommandHandler.AimAssistCommand(session, new[] { "off" }));
        Assert.False(session.AimAssist);
        Assert.StartsWith("Aim assist off", CommandHandler.AimAssistCommand(session, Array.Empty<string>()));
        Assert.Equal("Say /aimassist on or /aimassist off.", CommandHandler.AimAssistCommand(session, new[] { "maybe" }));
        Assert.StartsWith("Aim assist on", CommandHandler.AimAssistCommand(session, new[] { "on", "quiet" }));
        Assert.True(session.AimAssist);

        // The client keeps the choice, saved, and tells the server.
        bool was = OpenFPS.Client.Core.NavigationAids.AimAssist;
        try
        {
            int saved = 0;
            Assert.Null(OpenFPS.Client.Core.Session.ClientGameSession.AimAssistCommand(new[] { "off" }, out var send, () => saved++));
            Assert.False(OpenFPS.Client.Core.NavigationAids.AimAssist);
            Assert.Equal(1, saved);
            Assert.Equal("aimassist", send!.Command);
            Assert.Equal(new[] { "off" }, send.Args);
            Assert.NotNull(OpenFPS.Client.Core.Session.ClientGameSession.AimAssistCommand(new[] { "sideways" }, out _, () => saved++));
            Assert.Equal(1, saved);
        }
        finally { OpenFPS.Client.Core.NavigationAids.AimAssist = was; }
    }

    [Fact]
    public void ARoundThatEndsInTheGroundOrARoofSaysWhereByName()
    {
        var g = new Range(_dir, "city");
        // Open ground at the west edge of the city: the map's own dirt. At 25 degrees a round goes in.
        var a = g.Player("a", new Vector3(-400f, 0f, 300f));
        a.AimAssist = false;
        g.Arm(a, "akm");
        g.Face(a, 0f, 25f * MathF.PI / 180f);
        g.Fire(a);
        g.Run(0.2);
        Assert.Equal("Hit the ground at 4 metres.", Assert.Single(g.Said(a)));
        // ...and at 10 degrees a round skips off concrete: the airport's apron. (This shot used to be
        // made at the west edge too, where the loader once laid a concrete foundation flush under the
        // map's own dirt, and the server met the concrete. The loader lays nothing under a map with its
        // own ground now, and lays dirt where it does: docs/GEOMETRY.md.)
        var skip = g.Player("skip", new Vector3(258f, 0.08f, 40f));
        skip.AimAssist = false;
        g.Arm(skip, "akm");
        g.Face(skip, 0f, 10f * MathF.PI / 180f);
        g.Fire(skip);
        g.Run(0.2);
        Assert.Equal("Ricochet off Apron.", g.Said(skip)[0]);

        // Brandt Court's roof, a few metres in front of you.
        var b = g.Player("b", new Vector3(-20f, 18.25f, 200f));
        g.Arm(b, "akm");
        g.Face(b, 0f, 30f * MathF.PI / 180f);
        g.Fire(b);
        g.Run(0.2);
        Assert.Equal("Hit Brandt Court roof at 3 metres.", Assert.Single(g.Said(b)));
    }

    /// <summary>"Hit audience" was a sofa: its acoustic material is Audience (soft, like a grandstand of
    /// people), and the words said the material. They say the thing.</summary>
    [Fact]
    public void ASofaIsASofaNotAnAudience()
    {
        var g = new Range(_dir, "city");
        var shooter = g.Player("cody", new Vector3(-26f, 0f, 150.45f));
        g.Arm(shooter, "glock");
        g.AimAt(shooter, new Vector3(-28.9f, 0.5f, 150.45f));
        g.Fire(shooter);
        g.Run(0.2);
        string said = Assert.Single(g.Said(shooter));
        Assert.StartsWith("Hit Brandt Court flat 00B sofa at ", said);
        Assert.DoesNotContain("audience", said, StringComparison.OrdinalIgnoreCase);

        // Something with no name at all is "something", never its material.
        var world = World.Create();
        var thing = world.Create(new MaterialComponent { Material = "Audience" });
        Assert.Equal("Hit something at 12 metres.", CombatService.StruckWords(world, thing, 12.3f));
        World.Destroy(world);
    }

    /// <summary>A pedestrian 400 m off is hit when the bullet gets there, 0.5-0.6 s after the shot,
    /// and the chime and the words come then, not with the shot.</summary>
    [Fact]
    public void AHitIsToldWhenTheBulletArrives()
    {
        var g = new Range(_dir, "default");
        var feet = new Vector3(140f, 0f, 140f);
        var shooter = g.Player("shooter", feet);
        g.Arm(shooter, "m700");
        var walker = g.Walker(feet + new Vector3(0f, 0f, 400f));
        // Held up for the drop, from the hip at 1.5 m to the chest at 1.2 m, 400 m off.
        float up = ExternalBallistics.ZeroAngle(WeaponRegistry.M700, 400f, -(CombatService.HipHeight - 1.2f));
        g.Face(shooter, 0f, -up);

        g.Fire(shooter);
        double fired = g.Now;
        double? arrived = null;
        for (int i = 0; i < 40 && arrived == null; i++)
        {
            g.Run(PhysicsConstants.FixedDeltaTime);
            if (g.Confirms(shooter).Count > 0) arrived = g.Now - fired;
        }
        Assert.NotNull(arrived);
        _o.WriteLine($"hit told {arrived:F3} s after the shot");
        Assert.InRange(arrived!.Value, 0.5, 0.62);
        Assert.Equal(walker.Id, Assert.Single(g.Confirms(shooter)).TargetEntityId);
        Assert.Equal("Hit pedestrian at 400 metres.", Assert.Single(g.Said(shooter)));
    }

    /// <summary>The scatter of a shot from the hip: 7 mrad a side, about 0.4 degrees.</summary>
    [Fact]
    public void AShotFromTheHipScattersAboutSevenMilliradians()
    {
        var rng = new Random(5);
        var aim = Vector3.Normalize(new Vector3(0.3f, -0.1f, 1f));
        var angles = Enumerable.Range(0, 4000)
            .Select(_ => MathF.Acos(Math.Clamp(Vector3.Dot(aim, CombatService.Disperse(aim, 0.007f, rng)), -1f, 1f)))
            .ToList();
        // The angle off a 2-D Gaussian is Rayleigh: its rms is sigma times root two.
        float rms = MathF.Sqrt(angles.Average(a => a * a) / 2f);
        Assert.InRange(rms, 0.0065f, 0.0075f);
        Assert.Equal(aim, CombatService.Disperse(aim, 0f, rng));
    }

    // ── The crack ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A .308 passing 6 m from somebody 250 m downrange: the crack arrives from the point on the flight
    /// where the Mach cone met them (the ray to them leaves at acos(c/v) to the flight), at the time the
    /// cone got there, which is the least of t + |ear − P(t)|/c over a finely flown path, and long before
    /// the report.
    /// </summary>
    [Fact]
    public void TheCrackComesFromWhereTheConeMetTheListenerBeforeTheReport()
    {
        var w = WeaponRegistry.M700;
        var path = Flown(w, Vector3.Zero, Vector3.UnitZ, 0.01f);
        var ear = new Vector3(6f, 0f, 250f);
        float c = Still.SpeedOfSound;

        Assert.True(BulletFlyby.FindCrack(path, ear, c, out var e));

        // Independent: a 0.05 ms flight, and the first sound to arrive by brute force.
        var fine = Flown(w, Vector3.Zero, Vector3.UnitZ, 0.00005f);
        var best = fine.MinBy(s => s.Seconds + Vector3.Distance(ear, s.Position) / c);
        float expected = best.Seconds + Vector3.Distance(ear, best.Position) / c;
        float arrives = e.Seconds + Vector3.Distance(ear, e.Position) / c;
        _o.WriteLine($"emitted at {e.Position} after {e.Seconds:F4} s at {e.Velocity.Length():F0} m/s; arrives {arrives:F4} s (brute force {expected:F4})");
        Assert.InRange(arrives - expected, -0.0002f, 0.0002f);
        Assert.True(Vector3.Distance(e.Position, best.Position) < 0.5f);

        // From the cone: the ray to the ear leaves at the Mach angle to the flight.
        float angle = MathF.Acos(Vector3.Dot(Vector3.Normalize(ear - e.Position), Vector3.Normalize(e.Velocity)));
        float mach = MathF.Acos(c / e.Velocity.Length());
        Assert.InRange(angle - mach, -0.01f, 0.01f);
        // Upstream of the nearest point, by about b / sqrt(M² − 1).
        float m = e.Velocity.Length() / c;
        Assert.InRange(250f - e.Position.Z, 0.8f * 6f / MathF.Sqrt(m * m - 1f), 1.2f * 6f / MathF.Sqrt(m * m - 1f));

        // Long before the report.
        float report = ear.Length() / c;
        Assert.True(arrives < report - 0.3f, $"crack {arrives:F3} s, report {report:F3} s");

        // And what is sent: placed there, delayed by the bullet's time to get there.
        var sound = Assert.Single(BulletFlyby.Sounds(path, ear, w, Still));
        Assert.True(BulletFlyby.TryParseCrack(sound.SynthKey, out float T));
        Assert.Equal(e.Seconds, sound.DelaySeconds, 4);
        Assert.Equal(e.Position, sound.Position);
        _o.WriteLine($"N-wave {T * 1e6:F0} us, declared {sound.LevelDb:F1} dB at 1 m");
    }

    /// <summary>
    /// Whitham's law, checked against the figures the literature gives for a rifle round: a .308 at
    /// Mach 2.2 passing at 5 m makes about 350 Pa (145 dB) for about 0.2 ms; the pressure falls as the
    /// miss distance to the 3/4 and the length grows as its 1/4.
    /// </summary>
    [Fact]
    public void WhithamsNWave()
    {
        float p0 = 101325f, c = 343f;
        float p5 = BulletFlyby.CrackPeakPascals(2.2f, 5f, 0.0078f, 0.0309f, p0);
        float t5 = BulletFlyby.CrackSeconds(2.2f, 5f, 0.0078f, 0.0309f, c);
        _o.WriteLine($".308 M2.2 at 5 m: {p5:F0} Pa ({BulletFlyby.Spl(p5):F0} dB), {t5 * 1e6:F0} us");
        Assert.InRange(p5, 300f, 420f);
        Assert.InRange(t5, 170e-6f, 230e-6f);
        Assert.Equal(MathF.Pow(16f, -0.75f), BulletFlyby.CrackPeakPascals(2.2f, 80f, 0.0078f, 0.0309f, p0) / p5, 3);
        Assert.Equal(MathF.Pow(16f, 0.25f), BulletFlyby.CrackSeconds(2.2f, 80f, 0.0078f, 0.0309f, c) / t5, 3);
    }

    /// <summary>
    /// The NIJ recordings (inbox/gunfire-references-2026-09-24, measured 2026-10-04 from the first two
    /// events of each take): 30 degrees off the line at 40 m the crack leads the report by 21.4 ms for
    /// the M16 and 15.5 ms for the AK. Flown, the AK comes out at 16.8 ms. The AR-15 comes out at 25.3,
    /// 4 ms over: its slowing over the first 35 m (G7 0.12, about 30 m/s lost) is worth half a
    /// millisecond, so whatever else the recording holds (the M16's own muzzle velocity, where the
    /// microphone stood) is not in this model. Held at what it is. The N-waves come out 235 us (AR-15)
    /// and 293 us (AK) long, their peak-to-trough 208 and 250 us; the takes measure 167 and 250 us.
    /// </summary>
    [Theory]
    [InlineData("ar15", 30f, 40f, 21.4f, 4.5f)]
    [InlineData("akm", 30f, 40f, 15.5f, 1.5f)]
    public void TheCrackLeadsTheReportAsRecorded(string weapon, float degrees, float metres, float recordedMs, float toleranceMs)
    {
        var w = WeaponRegistry.Get(weapon)!;
        var path = Flown(w, Vector3.Zero, Vector3.UnitZ, 0.01f);
        float a = degrees * MathF.PI / 180f;
        var ear = new Vector3(metres * MathF.Sin(a), 0f, metres * MathF.Cos(a));
        float c = Still.SpeedOfSound;
        Assert.True(BulletFlyby.FindCrack(path, ear, c, out var e));
        float lead = (ear.Length() / c - (e.Seconds + Vector3.Distance(ear, e.Position) / c)) * 1000f;
        var sound = Assert.Single(BulletFlyby.Sounds(path, ear, w, Still));
        Assert.True(BulletFlyby.TryParseCrack(sound.SynthKey, out float T));
        _o.WriteLine($"{w.DisplayName} {degrees} deg {metres} m: crack leads by {lead:F1} ms (recorded {recordedMs}); N-wave {T * 1e6:F0} us");
        Assert.InRange(lead, recordedMs - toleranceMs, recordedMs + toleranceMs);
    }

    [Fact]
    public void NoCrackBehindTheMuzzleOrPastWhereTheRoundStopped()
    {
        var w = WeaponRegistry.M700;
        var path = Flown(w, Vector3.Zero, Vector3.UnitZ, 0.01f);
        float c = Still.SpeedOfSound;
        Assert.False(BulletFlyby.FindCrack(path, new Vector3(3f, 0f, -10f), c, out _));
        Assert.False(BulletFlyby.FindCrack(path, new Vector3(5f, 0f, 0f), c, out _));
        // Stopped in a wall at 100 m: somebody 200 m downrange hears no crack.
        var stopped = path.TakeWhile(s => s.Position.Z < 100f).ToList();
        Assert.False(BulletFlyby.FindCrack(stopped, new Vector3(4f, 0f, 200f), c, out _));
    }

    // ── The whizz ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A .45 is subsonic: no crack, only the whizz of it going by, in three pieces from three
    /// places along the path, heard one after another.</summary>
    [Fact]
    public void ASubsonicRoundWhizzesAndDoesNotCrack()
    {
        var w = WeaponRegistry.ServicePistol;
        var path = Flown(w, Vector3.Zero, Vector3.UnitZ, 0.01f);
        var ear = new Vector3(2f, 0f, 30f);
        float c = Still.SpeedOfSound;
        var sounds = BulletFlyby.Sounds(path, ear, w, Still);
        Assert.DoesNotContain(sounds, s => s.SynthKey.StartsWith(BulletFlyby.CrackPrefix));
        Assert.Equal(3, sounds.Count);
        Assert.All(sounds, s => Assert.True(BulletFlyby.TryParseWhizz(s.SynthKey, out _)));
        // Approaching, abeam, going away: placed in that order down the path, and heard in that order.
        Assert.True(sounds[0].Position.Z < sounds[1].Position.Z && sounds[1].Position.Z < sounds[2].Position.Z);
        var heard = sounds.Select(s => s.DelaySeconds + Vector3.Distance(ear, s.Position) / c).ToList();
        Assert.True(heard[0] < heard[1] && heard[1] < heard[2]);
        // Abeam is heard when the bullet is level with you, plus the hop.
        Assert.True(BulletFlyby.FindPass(path, ear, out var n, out float miss, out _, out _));
        Assert.True(BulletFlyby.TryParseWhizz(sounds[1].SynthKey, out var abeam));
        Assert.True(BulletFlyby.TryPiece(abeam, out float start, out float end, out _));
        Assert.InRange(n.Seconds + miss / c, heard[1] + 0f, heard[1] + (end - start));
        Assert.All(sounds, s => Assert.Equal(sounds[0].LevelDb, s.LevelDb));
        _o.WriteLine($"whizz declared {sounds[0].LevelDb:F1} dB at 1 m; pass at {miss:F1} m, {n.Velocity.Length():F0} m/s");

        // A rifle round passing as close has no whizz, only the crack.
        var rifle = BulletFlyby.Sounds(Flown(WeaponRegistry.M700, Vector3.Zero, Vector3.UnitZ, 0.01f), ear, WeaponRegistry.M700, Still);
        Assert.Single(rifle);
        Assert.StartsWith(BulletFlyby.CrackPrefix, rifle[0].SynthKey);
    }

    // ── The renders ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCrackRendersAsAnNWave()
    {
        Assert.True(BulletFlyby.TryParseCrack(BulletFlyby.CrackKey(200e-6f), out float T));
        var pcm = BulletFlyby.RenderCrack(T, 48000);
        Assert.All(pcm, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) <= 1f));
        int top = Array.IndexOf(pcm, pcm.Max()), bottom = Array.IndexOf(pcm, pcm.Min());
        // Up, then down to as far below, a pulse's length later.
        Assert.InRange(pcm.Max(), 0.6f, 1f);
        Assert.InRange(pcm.Min(), -1f, -0.6f);
        Assert.InRange((bottom - top) / 48000f, 0.6f * T, 1.4f * T);
        Assert.InRange(MathF.Abs(pcm.Sum()), 0f, 0.5f);   // no DC: as much under as over
    }

    [Fact]
    public void TheWhizzFallsInPitchAsItGoesBy()
    {
        var w = WeaponRegistry.ServicePistol;
        var path = Flown(w, Vector3.Zero, Vector3.UnitZ, 0.01f);
        var sounds = BulletFlyby.Sounds(path, new Vector3(2f, 0f, 30f), w, Still);
        var renders = sounds.Select(s =>
        {
            Assert.True(BulletFlyby.TryParseWhizz(s.SynthKey, out var k));
            return BulletFlyby.RenderWhizz(k, 48000, seed: 1);
        }).ToList();
        foreach (var r in renders)
        {
            Assert.All(r, v => Assert.True(float.IsFinite(v) && MathF.Abs(v) <= 1f));
            Assert.True(r.Max(MathF.Abs) > 0.01f, "a piece rendered silent");
        }
        float approach = ZeroCrossingHz(renders[0]), away = ZeroCrossingHz(renders[2]);
        // Louder coming than going: the convective factor (1 − M cos θ)^-2.
        float Rms(float[] r) => MathF.Sqrt(r.Average(v => v * v));
        _o.WriteLine($"approach ~{approach:F0} Hz, going away ~{away:F0} Hz; pieces {string.Join(", ", renders.Select(r => $"{r.Length / 48.0:F0} ms peak {r.Max(MathF.Abs):F2} rms {Rms(r):F3}"))}");
        Assert.True(approach > 1.5f * away);
        Assert.True(renders[0].Max(MathF.Abs) > renders[2].Max(MathF.Abs));
    }

    private static float ZeroCrossingHz(float[] pcm)
    {
        int crossings = 0;
        for (int i = 1; i < pcm.Length; i++) if ((pcm[i - 1] < 0f) != (pcm[i] < 0f)) crossings++;
        return crossings * 48000f / (2f * pcm.Length);
    }

    // ── On the server: who hears what ───────────────────────────────────────────────────────────

    /// <summary>
    /// Somebody standing 5 m off the line 200 m downrange is sent the crack, alone, with the shot; the
    /// shooter is not. The crack reaches them before the report does.
    /// </summary>
    [Fact]
    public void AnotherPlayerDownrangeIsSentTheCrackWithTheShot()
    {
        var g = new Range(_dir, "default");
        var feet = new Vector3(140f, 0f, 140f);
        var shooter = g.Player("shooter", feet);
        var other = g.Player("other", feet + new Vector3(5f, 0f, 200f));
        g.Arm(shooter, "m700");
        g.Face(shooter, 0f, 0f);
        g.Fire(shooter);

        var heard = g.SentTo(other).OfType<WorldAudioEvent>().SelectMany(e => e.Sounds).ToList();
        var crack = Assert.Single(heard, s => s.SynthKey.StartsWith(BulletFlyby.CrackPrefix));
        Assert.DoesNotContain(g.SentTo(shooter).OfType<WorldAudioEvent>().SelectMany(e => e.Sounds),
                              s => s.SynthKey.StartsWith("bullet:"));
        float c = Still.SpeedOfSound;
        Vector3 ear = feet + new Vector3(5f, CombatService.EyeHeight, 200f);
        Vector3 muzzle = feet + new Vector3(0f, CombatService.HipHeight, 0.5f);
        float crackAt = crack.DelaySeconds + Vector3.Distance(ear, crack.Position) / c;
        float reportAt = Vector3.Distance(ear, muzzle) / c;
        _o.WriteLine($"crack at {crackAt:F3} s from {crack.Position}, report at {reportAt:F3} s");
        Assert.True(crackAt < reportAt - 0.2f);
        Assert.InRange(crack.Position.Z, feet.Z + 180f, feet.Z + 200f);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A round flown from <paramref name="from"/> along <paramref name="dir"/> in still,
    /// standard air, sampled every <paramref name="step"/> seconds for two seconds.</summary>
    private static List<FlightSample> Flown(WeaponDefinition w, Vector3 from, Vector3 dir, float step)
    {
        var s = new BulletState { Position = from, Velocity = Vector3.Normalize(dir) * w.MuzzleVelocity };
        var path = new List<FlightSample> { new(0f, s.Position, s.Velocity) };
        float bc = ExternalBallistics.CoefficientOf(w);
        while (s.Seconds < 2f)
        {
            ExternalBallistics.Advance(ref s, step, bc, Still, Vector3.Zero);
            path.Add(new FlightSample(s.Seconds, s.Position, s.Velocity));
        }
        return path;
    }

    /// <summary>A real map with the real combat service, its clock in the test's hand and the scatter
    /// of a shot from the hip set to nothing.</summary>
    private sealed class Range
    {
        public readonly MapManager Maps;
        public readonly HandsService Hands;
        public readonly SessionManager Sessions = new();
        public readonly GameServer Server;
        public readonly CombatService Combat;
        public readonly string MapId;
        public World World = null!;
        public SpatialGrid<Entity> Grid = null!;
        public double Now = 100;
        public readonly List<(UserSession To, IMessage Message)> ServerSent = new();
        private int _next = 1;

        public Range(string dir, string mapId)
        {
            MapId = mapId;
            string mapDir = Path.Combine(dir, Guid.NewGuid().ToString("N"), "maps");
            Directory.CreateDirectory(mapDir);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", mapId + ".json"), Path.Combine(mapDir, mapId + ".json"));
            Maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            Maps.Initialize();
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out Grid, out _));
            Hands = new HandsService(Maps);
            Server = new GameServer(new NoUsers());
            Server.Attach(Maps, Sessions, new OccupancyService(Maps), Hands);
            Server.Sent = (to, m) => ServerSent.Add((to, m));
            Combat = new CombatService(Maps, Server, Sessions) { Clock = () => Now, HipDispersionRadians = 0f };
        }

        public UserSession Player(string name, Vector3 feet)
        {
            int connection = _next++;
            var entity = World.Create(
                new PlayerComponent { ConnectionId = connection, Username = name },
                EntityType.Player,
                new Transform { Position = feet, Rotation = Quaternion.Identity },
                new Velocity(), new MaterialComponent { Material = "Generic" },
                new NameComponent { Name = name },
                new HealthComponent { Current = 100, Max = 100 },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            Maps.IndexEntity(MapId, entity);
            var session = new UserSession { ConnectionId = connection, Username = name, Entity = entity, CurrentMapId = MapId, Welcomed = true };
            Sessions.AddSession(connection, session);
            return session;
        }

        public void Face(UserSession who, float yaw, float pitch)
            => World.Get<Transform>(who.Entity).Rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f);

        /// <summary>Points a player's gun at a point, from the hip. Increasing pitch looks down.</summary>
        public void AimAt(UserSession who, Vector3 point)
        {
            Vector3 to = point - (World.Get<Transform>(who.Entity).Position + new Vector3(0f, CombatService.HipHeight, 0f));
            Face(who, MathF.Atan2(to.X, to.Z), -MathF.Atan2(to.Y, new Vector2(to.X, to.Z).Length()));
        }

        public Entity Walker(Vector3 at) => Maps.SpawnEntity(MapId, w => w.Create(
            EntityType.NPC,
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new Velocity(),
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false },
            new NameComponent { Name = "someone walking" },
            new IdentityComponent { Name = "someone walking" },
            new Pedestrian { Voice = "", Pair = "" },
            new HealthComponent { Current = 100, Max = 100 }));

        public Entity Arm(UserSession who, string weaponId)
        {
            var w = WeaponRegistry.Get(weaponId)!;
            var gun = Maps.SpawnEntity(MapId, wd => wd.Create(
                new Transform { Position = World.Get<Transform>(who.Entity).Position + new Vector3(0.3f, 0, 0), Rotation = Quaternion.Identity },
                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.1f, 0.1f, 1f), IsSolid = false },
                new MaterialComponent { Material = "Metal" },
                new IdentityComponent { Name = w.DisplayName },
                new ItemComponent { MassKg = 3.5f, Hands = 2, WeaponId = weaponId },
                EntityType.Item));
            Assert.True(Hands.Take(who, w.DisplayName, out string message), message);
            return gun;
        }

        public void Fire(UserSession who)
        {
            RefreshGrid();
            Combat.Fire(who, Array.Empty<string>(), m => ServerSent.Add((who, m)), canNameAny: false);
        }

        public void Run(double seconds)
        {
            int ticks = (int)Math.Ceiling(seconds / PhysicsConstants.FixedDeltaTime - 1e-6);
            for (int i = 0; i < ticks; i++)
            {
                Now += PhysicsConstants.FixedDeltaTime;
                RefreshGrid();
                Combat.Update(MapId, World);
            }
        }

        private void RefreshGrid()
        {
            Grid.Clear();
            World.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                (Entity e, ref Transform t, ref ColliderComponent c) => Grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
        }

        public IEnumerable<IMessage> SentTo(UserSession who) => ServerSent.Where(s => s.To == who).Select(s => s.Message);
        public List<string> Said(UserSession who) => SentTo(who).OfType<TextEvent>().Select(t => t.Text).ToList();
        public List<HitConfirm> Confirms(UserSession who) => SentTo(who).OfType<HitConfirm>().ToList();
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => false;
        public bool VerifyPassword(string username, string password) => false;
    }
}
