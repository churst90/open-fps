using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// Two things meeting, and a window going out — both speaking the same four characters as everything
/// else, and neither of them knowing what a car or a rifle is.
///
/// The point being held here is the GENERALITY. `ImpactAcoustics.Between` is handed two materials,
/// two masses, a size and a closing speed; a car hitting a wall, a ball hitting a floor and a crate
/// coming off a lorry are that one function called three times. If it ever grows a case for cars,
/// this is where that should start failing.
/// </summary>
public class ImpactAndGlassSoundTests
{
    public ImpactAndGlassSoundTests() => AcousticRegistry.Initialize();

    private static MaterialProperties Of(string name) => AcousticRegistry.GetProperties(name);

    // ── Impacts ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A car creeping into a kerb at walking pace should not announce itself like a crash.</summary>
    [Fact]
    public void ACrawlIsNotACrash()
    {
        Assert.Empty(Hit(speed: 0.1f));
        Assert.NotEmpty(Hit(speed: 6f));
    }

    /// <summary>Twice the closing speed is six decibels, the same law as everything else that
    /// arrives with energy.</summary>
    [Fact]
    public void TwiceTheClosingSpeedIsSixDecibels()
    {
        float slow = Hit(speed: 2f).First(s => s.Character == SoundCharacter.Knock).LevelDb;
        float fast = Hit(speed: 4f).First(s => s.Character == SoundCharacter.Knock).LevelDb;
        Assert.Equal(6f, fast - slow, 1);
    }

    /// <summary>
    /// A lorry hitting a drink can and a drink can hitting a lorry are the SAME collision, and both
    /// are governed by the lighter of the two. Using the heavier would make a truck brushing a
    /// bollard sound like the end of the world.
    /// </summary>
    [Fact]
    public void TheLighterOfTheTwoGovernsIt()
    {
        float lorryIntoCan = PanelAcoustics.ImpactJoules(14000f, 0.02f, 10f);
        float canIntoLorry = PanelAcoustics.ImpactJoules(0.02f, 14000f, 10f);
        Assert.Equal(lorryIntoCan, canIntoLorry, 4);

        // ...and it is nearer the can's energy than the lorry's, by a very long way.
        Assert.True(lorryIntoCan < 0.5f * 0.021f * 100f, $"the can's collision released {lorryIntoCan:F1} J");
    }

    /// <summary>The blow takes the character of the SOFTER of the pair: hitting a carpeted wall is a
    /// dull thump whatever you hit it with.</summary>
    [Fact]
    public void TheSofterOfTheTwoDecidesTheBlow()
    {
        float hard = Hit(speed: 5f, struck: "Concrete").First(s => s.Character == SoundCharacter.Knock).Hz;
        float soft = Hit(speed: 5f, struck: "Carpet").First(s => s.Character == SoundCharacter.Knock).Hz;
        Assert.True(soft < hard, $"the carpet came out at {soft:F0} Hz and the concrete at {hard:F0}");
    }

    /// <summary>...and the ring afterwards belongs to whichever of them actually rings, which is why
    /// a hammer on a bell is a bell.</summary>
    [Fact]
    public void WhicheverOfThemRingsIsTheOneYouHear()
    {
        Assert.Contains(Hit(speed: 5f, struck: "Metal"), s => s.Character == SoundCharacter.Ring);
        Assert.DoesNotContain(Hit(speed: 5f, struck: "Carpet"), s => s.Character == SoundCharacter.Ring);
    }

    /// <summary>Something bolted to the world gives all the energy back; something that can move
    /// takes some away with it. Same blow, different aftermath.</summary>
    [Fact]
    public void AThingThatCanMoveRingsLessThanOneBoltedDown()
    {
        var fixedRing = ImpactAcoustics.Between(Of("Metal"), Of("Metal"), Vector3.Zero, 5f, 1500f, 80000f,
                                                1f, 1f, 0.05f, struckIsFixed: true)
                                       .First(s => s.Character == SoundCharacter.Ring);
        var looseRing = ImpactAcoustics.Between(Of("Metal"), Of("Metal"), Vector3.Zero, 5f, 1500f, 80000f,
                                                1f, 1f, 0.05f, struckIsFixed: false)
                                       .First(s => s.Character == SoundCharacter.Ring);
        Assert.True(looseRing.DecaySeconds > fixedRing.DecaySeconds);
    }

    private static List<TransientSound> Hit(float speed, string struck = "Concrete")
        => ImpactAcoustics.Between(Of("Metal"), Of(struck), Vector3.Zero, speed,
                                   1500f, 75000f, 2f, 2f, 0.3f);

    // ── Glass ───────────────────────────────────────────────────────────────────────────────────

    private static List<GlassEvent> Resolve(GlassPane pane, WeaponDefinition weapon, int seed)
    {
        Span<GlassEvent> buffer = stackalloc GlassEvent[48];
        int n = GlassBreak.Resolve(pane, pane.Centre, weapon, seed, buffer);
        var events = new List<GlassEvent>();
        for (int i = 0; i < n; i++) events.Add(buffer[i]);
        return events;
    }

    private static GlassFracture.Spec Spec(GlassFracture.Part part, GlassType type, float w, float h, float t,
                                           float drop = 0.9f, string ground = "Concrete", WeaponDefinition? weapon = null)
    {
        var (kg, pellets) = GlassFracture.BulletOf(weapon ?? WeaponRegistry.Glock);
        return new GlassFracture.Spec(part, type, w, h, t, kg, (weapon ?? WeaponRegistry.Glock).MuzzleVelocity, pellets, drop, ground, 0);
    }

    private static double EnergyDb(double[] x, int from = 0, int to = int.MaxValue)
    {
        double e = 1e-30;
        for (int i = Math.Max(0, from); i < Math.Min(x.Length, to); i++) e += x[i] * x[i];
        return 10 * Math.Log10(e);
    }

    /// <summary>Energy above <paramref name="hz"/>: the signal less a one-pole low-pass of itself.</summary>
    private static double EnergyAboveDb(double[] x, double hz, int rate = 48000)
    {
        double a = Math.Exp(-2 * Math.PI * hz / rate), lp = 0, e = 1e-30;
        foreach (double v in x) { lp = (1 - a) * v + a * lp; e += (v - lp) * (v - lp); }
        return 10 * Math.Log10(e);
    }

    /// <summary>
    /// The whole reason GlassBreak was worth writing: a window shot out five floors up makes two
    /// sounds most of two seconds apart, from two different places. The break is at the window; the
    /// glass arrives at the FOOT of the wall. The gap is sqrt(2h/g) — which floor the shot was on.
    /// Now each is the glass model's own render (GlassFracture), and the landing's starts at the bottom
    /// edge's fall time, so the first piece to arrive still carries the height.
    /// </summary>
    [Fact]
    public void AWindowUpstairsIsHeardTwiceAndTheGapSaysHowHigh()
    {
        var pane = new GlassPane(new Vector3(0, 15f, 0), new Vector2(1.2f, 1.5f), Vector3.UnitZ,
                                 GlassType.Tempered, HeightAboveGround: 15f);
        Assert.True(WeaponRegistry.TryGet("akm", out var weapon));
        var sounds = GlassSound.From(Resolve(pane, weapon, 4), pane, weapon, 0.006f, "Concrete", 4);
        Assert.Equal(2, sounds.Count);

        var brk = sounds.Single(s => s.DelaySeconds == 0f);
        var landing = sounds.Single(s => s.DelaySeconds > 0f);
        Assert.True(GlassFracture.TryParseKey(brk.SynthKey, out var b) && b.Part == GlassFracture.Part.Break);
        Assert.True(GlassFracture.TryParseKey(landing.SynthKey, out var l) && l.Part == GlassFracture.Part.Land);

        // A tenth of a second before the bottom edge's free fall: a piece thrown downwards arrives first.
        float ideal = GlassBreak.FallSeconds(15f);
        Assert.InRange(landing.DelaySeconds, ideal - 0.11f, ideal);
        Assert.InRange(GlassBreak.HeightFromFallDelay(landing.DelaySeconds), 13f, 16f);
        Assert.True(landing.Position.Y < pane.Centre.Y - 5f, "the glass landed at the window it fell out of");
        Assert.Equal(pane.Centre, brk.Position);

        // ...and inside the landing's own render the first piece arrives within a few per cent of the fall
        // time of its start (drag and the moment it left the frame), so the gap still reads as the floor.
        var pcm = GlassFracture.Render(l, 48000);
        double peak = pcm.Max(Math.Abs);
        int first = Array.FindIndex(pcm, v => Math.Abs(v) > peak * 0.01);
        Assert.InRange(first / 48000.0, 0.0, 0.12 * ideal);
    }

    /// <summary>
    /// Tempered glass dices: every piece counted, and there are tens of thousands of them (EN 12150 asks at
    /// least 40 in a 50 mm square). Annealed glass breaks into a few hundred shards and slivers.
    /// </summary>
    [Fact]
    public void TemperedGlassDicesAndAnnealedGlassShards()
    {
        var tempered = GlassFracture.Census(Spec(GlassFracture.Part.Break, GlassType.Tempered, 1.2f, 1.6f, 0.006f));
        Assert.InRange(tempered.Dice, (int)(1.2 * 1.6 * 16000), (int)(1.2 * 1.6 * 80000));
        Assert.Equal(0, tempered.Shards);

        var annealed = GlassFracture.Census(Spec(GlassFracture.Part.Break, GlassType.Annealed, 1.2f, 1.6f, 0.006f));
        Assert.Equal(0, annealed.Dice);
        Assert.InRange(annealed.Shards + annealed.Slivers, 50, 2000);
    }

    /// <summary>
    /// A key names everything the render depends on, and the same key renders the same sound on every
    /// client (the workers' shares are summed in a fixed order).
    /// </summary>
    [Fact]
    public void AGlassKeyRoundTripsAndRendersTheSameEveryTime()
    {
        var spec = Spec(GlassFracture.Part.Land, GlassType.Annealed, 1.2f, 1.6f, 0.006f, drop: 3.9f, ground: "Asphalt");
        string key = GlassFracture.Key(spec);
        Assert.True(GlassFracture.TryParseKey(key, out var back));
        Assert.Equal(key, GlassFracture.Key(back));
        Assert.Equal("Asphalt", back.Ground);
        Assert.Equal(3.9f, back.Drop, 3);

        var a = GlassFracture.RenderKey(key, 48000, out float da);
        var b = GlassFracture.RenderKey(key, 48000, out float db);
        Assert.Equal(da, db);
        Assert.Equal(a, b);
        // Full scale is the render's own peak, and that is a real level: between a dropped cup and a gunshot.
        Assert.InRange(da, 110f, 155f);
    }

    /// <summary>
    /// What the glass lands on decides what is heard: on grass a piece meets something with an elastic
    /// modulus of a few megapascals, a contact of milliseconds instead of microseconds, so the click is dull
    /// and the piece hardly rings; on concrete both are bright.
    /// </summary>
    [Fact]
    public void GlassOnGrassIsDullerThanOnConcrete()
    {
        var concrete = GlassFracture.Render(Spec(GlassFracture.Part.Land, GlassType.Annealed, 1.2f, 1.6f, 0.006f, ground: "Concrete"), 48000);
        var grass = GlassFracture.Render(Spec(GlassFracture.Part.Land, GlassType.Annealed, 1.2f, 1.6f, 0.006f, ground: "Grass"), 48000);
        Assert.True(EnergyAboveDb(concrete, 4000) > EnergyAboveDb(grass, 4000) + 3,
                    $"above 4 kHz: concrete {EnergyAboveDb(concrete, 4000):F1}, grass {EnergyAboveDb(grass, 4000):F1}");
    }

    /// <summary>
    /// How much glass there was decides how much is heard: a shop front coming down is not a car's side
    /// window coming down, by the energy in the whole landing.
    /// </summary>
    [Fact]
    public void MoreGlassLandsLouder()
    {
        var shop = GlassFracture.Render(Spec(GlassFracture.Part.Land, GlassType.Tempered, 2.0f, 2.5f, 0.010f), 48000);
        var car = GlassFracture.Render(Spec(GlassFracture.Part.Land, GlassType.Tempered, 0.8f, 0.45f, 0.004f), 48000);
        Assert.True(EnergyDb(shop) > EnergyDb(car) + 6, $"shop {EnergyDb(shop):F1} dB, car window {EnergyDb(car):F1} dB");
    }

    /// <summary>
    /// A piece of glass rings at its own modes, a free plate's: f = lambda^2 / (2 pi a^2) sqrt(D / m''), so
    /// for one shape the note goes with the thickness. A 3 mm piece rings an octave under a 6 mm one of the
    /// same size, and it keeps ringing: glass loses almost nothing (the recordings' pieces on cement ring with
    /// a loss factor near 0.0006), so a single drop is still there a tenth of a second later.
    /// </summary>
    [Fact]
    public void ADroppedPieceRingsAtItsPlateModes()
    {
        double Strongest(double[] x)
        {
            // The strongest line between 2 and 20 kHz over 5-100 ms after the contact: a plain DFT scan.
            int from = (int)(0.015 * 48000), to = (int)(0.11 * 48000);
            double best = 0, bestHz = 0;
            for (double hz = 2000; hz < 20000; hz *= 1.01)
            {
                double re = 0, im = 0, w = 2 * Math.PI * hz / 48000;
                for (int i = from; i < to; i++) { re += x[i] * Math.Cos(w * i); im += x[i] * Math.Sin(w * i); }
                double m = re * re + im * im;
                if (m > best) { best = m; bestHz = hz; }
            }
            return bestHz;
        }
        var thin = GlassFracture.RenderDrop(0.06, 0.045, 0.003, 0.3, "Concrete", 48000, 1);
        var thick = GlassFracture.RenderDrop(0.06, 0.045, 0.006, 0.3, "Concrete", 48000, 1);
        double ft = Strongest(thin), fk = Strongest(thick);
        Assert.InRange(fk / ft, 1.5, 2.6);

        // Still ringing 100-200 ms on, within 30 dB of its first 50 ms.
        Assert.True(EnergyDb(thick, 4800, 9600) > EnergyDb(thick, 480, 2880) - 30,
                    $"{EnergyDb(thick, 4800, 9600):F1} against {EnergyDb(thick, 480, 2880):F1}");
    }

    /// <summary>
    /// Laminated glass keeps the pane: a hole and no fall at all, a very distinctive absence that tells a
    /// listener something about the building. Its interlayer damps the pane, so the hole dies away far
    /// quicker than one through plain glass.
    /// </summary>
    [Fact]
    public void LaminatedGlassTakesAHoleAndKeepsThePane()
    {
        var pane = new GlassPane(new Vector3(0, 2f, 0), new Vector2(1f, 1.5f), Vector3.UnitZ, GlassType.Laminated, 1.2f);
        var sounds = GlassSound.From(Resolve(pane, WeaponRegistry.Shotgun, 1), pane, WeaponRegistry.Shotgun, 0.0076f, "Concrete", 1);
        var only = Assert.Single(sounds);
        Assert.True(GlassFracture.TryParseKey(only.SynthKey, out var spec) && spec.Part == GlassFracture.Part.Hole);

        double Tail(double[] x) => EnergyDb(x, 4800, 24000) - EnergyDb(x, 0, 4800);
        var laminated = GlassFracture.Render(Spec(GlassFracture.Part.Hole, GlassType.Laminated, 1f, 1.5f, 0.0076f), 48000);
        var annealed = GlassFracture.Render(Spec(GlassFracture.Part.Hole, GlassType.Annealed, 1f, 1.5f, 0.006f, weapon: WeaponRegistry.Akm), 48000);
        Assert.True(Tail(laminated) < Tail(annealed) - 6, $"laminated tail {Tail(laminated):F1} dB, annealed {Tail(annealed):F1} dB");
    }

    /// <summary>
    /// The round's crack is the point-driven plate's own pulse, rho0 F / (2 pi m'' r) (Cremer and Heckl), and
    /// not a spike over it: the pane's modes start from nothing and add their swing, they are not all at their
    /// peak together at t = 0 (they were, and an intact pane shot through came out 10 dB over its own force).
    /// </summary>
    [Fact]
    public void TheStrikeIsThePlatesOwnPulse()
    {
        var spec = Spec(GlassFracture.Part.Hole, GlassType.Annealed, 1.2f, 1.6f, 0.006f, weapon: WeaponRegistry.Akm);
        var pcm = GlassFracture.Render(spec, 48000);
        double peak = pcm.Take(480).Max(Math.Abs);
        double j = spec.BulletKg * 0.1 * spec.BulletSpeed, tau = 2 * (0.016 + spec.Thickness) / spec.BulletSpeed;
        double analytic = 1.2 * (Math.PI / 2 * j / tau) / (2 * Math.PI * 2500 * spec.Thickness);
        Assert.InRange(20 * Math.Log10(peak / analytic), -3, 3);
    }

    /// <summary>
    /// How much of the falling glass's energy leaves as sound, two ways that share nothing but the contacts: the
    /// simulation (pieces ringing at their modes, glass's measured loss) and every contact as a point force on
    /// a plate radiating rho0 F / (2 pi m'' r). They agree within a few decibels, at a few per cent of the energy
    /// for a house window coming down on concrete, and grass takes most of it away.
    /// </summary>
    [Fact]
    public void TheLandingsSoundIsAFewPerCentOfItsEnergyBothWays()
    {
        var concrete = Spec(GlassFracture.Part.Land, GlassType.Annealed, 1.2f, 1.6f, 0.006f, ground: "Concrete");
        var grass = Spec(GlassFracture.Part.Land, GlassType.Annealed, 1.2f, 1.6f, 0.006f, ground: "Grass");
        double ke = GlassFracture.ArrivingJoules(concrete);
        double sim = GlassFracture.AcousticJoules(GlassFracture.Render(concrete, 48000), 48000, false);
        double force = GlassFracture.PointForceJoules(concrete);
        Assert.InRange(sim / ke, 0.003, 0.1);
        Assert.InRange(10 * Math.Log10(sim / force), -6, 6);
        double soft = GlassFracture.AcousticJoules(GlassFracture.Render(grass, 48000), 48000, false);
        Assert.True(soft < sim / 3, $"grass {soft:E2} J, concrete {sim:E2} J");
    }

    /// <summary>A piece falling through air arrives later than in a vacuum, and a fine dust of glass much
    /// later; a heavy piece over a few metres hardly differs (terminal speeds of 15-20 m/s).</summary>
    [Fact]
    public void DragDelaysTheSmallPieces()
    {
        double vacuum = GlassBreak.FallSeconds(10f);
        var heavy = GlassFracture.FallWithDrag(10, 0, 20);
        var fine = GlassFracture.FallWithDrag(10, 0, 5);
        Assert.InRange(heavy.Seconds, vacuum, vacuum * 1.15);
        Assert.True(fine.Seconds > vacuum * 1.4);
        Assert.InRange(fine.Speed, 4.5, 5.0);
    }

    // ── The named-model escape hatch ────────────────────────────────────────────────────────────

    /// <summary>
    /// A gunshot names its own model rather than being flattened into a knock. Four characters
    /// describe nearly everything; a blast wave with a body resonance, a brightness sweep and an
    /// action working is one of the few things they cannot, and a model for it already exists.
    /// </summary>
    [Fact]
    public void AGunshotNamesTheModelThatKnowsHowToMakeIt()
    {
        Assert.True(WeaponRegistry.TryGet("akm", out var akm));
        var shot = new TransientSound
        {
            Character = SoundCharacter.Knock,
            LevelDb = Loudness.MuzzleBlastDb(akm),
            SynthKey = "weapon:" + akm.Id,
            DecaySeconds = 0.6f,
        };

        Assert.StartsWith("weapon:", shot.SynthKey);
        // Loud enough to be a gunshot rather than a door, which is the thing the level has to carry.
        Assert.True(shot.LevelDb > 140f, $"the AKM came out at {shot.LevelDb:F0} dB");
    }
}
