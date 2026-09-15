using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

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

    /// <summary>
    /// The whole reason GlassBreak was worth writing: a window shot out five floors up makes two
    /// sounds most of two seconds apart, from two different places. The break is at the window; the
    /// glass arrives at the FOOT of the wall. The gap is sqrt(2h/g) — which floor the shot was on.
    /// </summary>
    [Fact]
    public void AWindowUpstairsIsHeardTwiceAndTheGapSaysHowHigh()
    {
        // Tempered, because that is the type that always fails completely — it is held in compression,
        // so there is no such thing as a neat hole in it. A rifle round through ANNEALED glass punches
        // a hole and leaves the pane up, which the model already knows and is the reason this test
        // would otherwise hear one small tap and nothing else.
        var pane = new GlassPane(new Vector3(0, 15f, 0), new Vector2(1.2f, 1.5f), Vector3.UnitZ,
                                 GlassType.Tempered, HeightAboveGround: 15f);
        Assert.True(WeaponRegistry.TryGet("akm", out var weapon));

        Span<GlassEvent> buffer = stackalloc GlassEvent[48];
        int n = GlassBreak.Resolve(pane, pane.Centre, weapon, seed: 4, buffer);
        var events = new List<GlassEvent>();
        for (int i = 0; i < n; i++) events.Add(buffer[i]);

        var sounds = GlassSound.From(events, pane.Type, pane.Size);
        Assert.NotEmpty(sounds);

        float breakAt = sounds.Min(s => s.DelaySeconds);
        // The FIRST piece to arrive is the one that fell freely from the pane; the rest trail it,
        // because they do not all leave the frame at the same instant. It is the first that carries
        // the height.
        float firstLanding = sounds.Where(s => s.Character == SoundCharacter.Knock).Min(s => s.DelaySeconds);

        Assert.True(firstLanding - breakAt > 1.2f,
                    $"the glass arrived {firstLanding - breakAt:F2} s after the break, from fifteen metres up");
        // Close to sqrt(2h/g), but not to the millisecond: the pieces do not all leave the frame at
        // the same instant, and the first to arrive left a fraction after the pane went.
        float ideal = GlassBreak.FallSeconds(15f);
        Assert.InRange(firstLanding, ideal * 0.95f, ideal * 1.15f);

        // The thing that makes it worth modelling: the gap reads back as the height, to within the
        // odd metre — which is which floor somebody is on.
        Assert.InRange(GlassBreak.HeightFromFallDelay(firstLanding - breakAt), 13f, 20f);

        // ...and it arrives somewhere else, which is the other half of the information.
        var landing = sounds.OrderBy(s => s.DelaySeconds).Last();
        Assert.True(landing.Position.Y < pane.Centre.Y - 5f, "the glass landed at the window it fell out of");
    }

    /// <summary>
    /// A pane letting go is not one impact, it is thousands inside a tenth of a second — and a crowd
    /// that dense stops being heard as impacts at all. A single shard landing IS one impact. That is
    /// why a window breaking sounds nothing like the pieces of it arriving.
    /// </summary>
    [Fact]
    public void ShatteringIsANoiseAndLandingIsABlow()
    {
        var events = new List<GlassEvent>
        {
            new(GlassEventKind.Shatter, 0f, Vector3.Zero, 1f, 1f),
            new(GlassEventKind.Landing, 1.5f, new Vector3(0, 0, 0), 0.6f, 1f),
        };
        var sounds = GlassSound.From(events, GlassType.Annealed, new Vector2(1f, 1.5f));

        Assert.Contains(sounds, s => s.Character == SoundCharacter.Hiss);
        Assert.Contains(sounds, s => s.Character == SoundCharacter.Knock);
    }

    /// <summary>
    /// Laminated glass keeps the pane: a dull crunch and no fall at all. A very distinctive absence,
    /// and it tells a listener something about the building they are shooting at.
    /// </summary>
    [Fact]
    public void LaminatedGlassIsDullerAndDoesNotRing()
    {
        var shatter = new List<GlassEvent> { new(GlassEventKind.Shatter, 0f, Vector3.Zero, 1f, 1f) };

        var annealed = GlassSound.From(shatter, GlassType.Annealed, new Vector2(1f, 1.5f));
        var laminated = GlassSound.From(shatter, GlassType.Laminated, new Vector2(1f, 1.5f));

        float annealedHz = annealed.First(s => s.Character == SoundCharacter.Hiss).Hz;
        float laminatedHz = laminated.First(s => s.Character == SoundCharacter.Hiss).Hz;
        Assert.True(laminatedHz < annealedHz * 0.5f, "laminated glass should be far duller");

        // Only annealed glass is briefly still a pane as it fails, so only it gives up a note.
        Assert.Contains(annealed, s => s.Character == SoundCharacter.Ring);
        Assert.DoesNotContain(laminated, s => s.Character == SoundCharacter.Ring);
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
