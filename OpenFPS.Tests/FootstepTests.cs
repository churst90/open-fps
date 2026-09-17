using System;
using System.Linq;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A footstep falls out of what the foot and the ground are made of.
///
/// The claim these hold is not "it sounds good" — nothing headless can check that — but that the
/// MECHANISM is wired the right way round. If a soft sole does not produce a longer contact than a
/// hard one, or if grass is not quieter than concrete, then no amount of listening will fix it,
/// because the thing being listened to is not the model.
/// </summary>
public class FootstepTests
{
    private readonly ITestOutputHelper _o;
    public FootstepTests(ITestOutputHelper o) { _o = o; AcousticRegistry.Initialize(); }

    /// <summary>
    /// A soft sole is in contact longer than a hard one, and therefore cannot be as bright.
    ///
    /// The single chain the whole model hangs off: stiffness sets contact time, contact time bounds
    /// the spectrum. It is why a trainer cannot click however hard you stamp in it.
    /// </summary>
    [Fact]
    public void ASofterSoleIsInContactLongerAndSoCannotBeAsBright()
    {
        var ground = AcousticRegistry.GetProperties("Concrete");
        float mass = 78f * Footsteps.EffectiveMassFraction;
        float v = 1.4f * Footsteps.HeelVelocityRatio;

        float Tau(Shoe shoe)
        {
            var sole = AcousticRegistry.GetProperties(shoe.SoleMaterial);
            return Footsteps.ContactSeconds(mass + shoe.MassKg, shoe.HeelRadiusM,
                                            Footsteps.ContactModulus(sole, ground), v);
        }

        float bare = Tau(Shoe.Bare), sneaker = Tau(Shoe.Sneaker);
        float boot = Tau(Shoe.Boot), dress = Tau(Shoe.DressShoe);
        _o.WriteLine($"contact: bare {bare * 1000:F1} ms, sneaker {sneaker * 1000:F1}, "
                   + $"boot {boot * 1000:F1}, dress {dress * 1000:F1}");

        Assert.True(bare > sneaker, "a bare foot is softer than a trainer and must be in contact longer");
        Assert.True(sneaker > boot, "a trainer is softer than a work boot");
        Assert.True(boot > dress, "a work boot's rubber is softer than a leather board sole");
        // ...and the spread is worth something: if every shoe were within a hair of every other,
        // the model would be right in direction and useless in practice.
        Assert.True(sneaker / dress > 1.8f, $"a trainer's contact is only {sneaker / dress:F2}x a dress shoe's");
    }

    /// <summary>
    /// The SOFTER of the two things that meet decides the contact.
    ///
    /// Which is why a trainer sounds much the same on granite and on chipboard — the rubber is doing
    /// all the deforming either way — and why the ground matters far more to a hard shoe.
    /// </summary>
    [Fact]
    public void TheSofterOfTheTwoSurfacesDecidesTheContact()
    {
        var rubber = AcousticRegistry.GetProperties("Rubber");
        var leather = AcousticRegistry.GetProperties("Leather");
        var concrete = AcousticRegistry.GetProperties("Concrete");
        var wood = AcousticRegistry.GetProperties("Wood");

        float softOnHard = Footsteps.ContactModulus(rubber, concrete);
        float softOnSoft = Footsteps.ContactModulus(rubber, wood);
        float hardOnHard = Footsteps.ContactModulus(leather, concrete);

        _o.WriteLine($"rubber/concrete {softOnHard:G3} Pa, rubber/wood {softOnSoft:G3}, leather/concrete {hardOnHard:G3}");

        // A trainer barely notices the difference between a slab and a floorboard.
        Assert.True(MathF.Abs(softOnHard - softOnSoft) / softOnHard < 0.05f);
        // A leather sole is an order of magnitude stiffer a contact than a rubber one on the same slab.
        Assert.True(hardOnHard > softOnHard * 8f);
    }

    /// <summary>
    /// A grain of grit is a contact two decades smaller than a heel, so it lands two decades higher.
    ///
    /// This is what a footstep on a hard floor actually sounds like. The bulk impact for a soft shoe
    /// is at forty hertz, which nobody hears from a foot; if the model had only that, a trainer on a
    /// pavement would be silent.
    /// </summary>
    [Fact]
    public void TheGrainIsHeardWhereTheBulkImpactCannotReach()
    {
        var ground = AcousticRegistry.GetProperties("Concrete");
        var sole = AcousticRegistry.GetProperties("Rubber");
        var shoe = Shoe.Sneaker;
        float tau = Footsteps.ContactSeconds(78f * Footsteps.EffectiveMassFraction + shoe.MassKg,
                                             shoe.HeelRadiusM,
                                             Footsteps.ContactModulus(sole, ground),
                                             1.4f * Footsteps.HeelVelocityRatio);
        var (grainMm, _) = Footsteps.ContactTexture("Concrete");
        float grainTau = Footsteps.GrainContactSeconds(tau, shoe.HeelRadiusM, grainMm);

        float bulkHz = Footsteps.ImpactCornerHz(tau);
        float grainHz = Footsteps.ImpactCornerHz(grainTau);
        _o.WriteLine($"bulk corner {bulkHz:F0} Hz, grain corner {grainHz:F0} Hz — {grainHz / bulkHz:F0}x");

        Assert.True(bulkHz < 120f, "the bulk impact of a soft shoe should be down in the thump");
        Assert.True(grainHz > 1500f, "the grain should reach where a footstep is actually heard");
    }

    /// <summary>
    /// A hard sole puts more of the ground's texture into the sound than a soft one, because it does
    /// not flow into it. That, not loudness, is what makes a dress shoe a click.
    /// </summary>
    [Fact]
    public void AHardSoleRattlesOverTheGritAndASoftOneFlowsIntoIt()
    {
        float Scuff(Shoe shoe, string surface)
        {
            var sole = AcousticRegistry.GetProperties(shoe.SoleMaterial);
            var (_, coverage) = Footsteps.ContactTexture(surface);
            return coverage * (1f - MathF.Max(Footsteps.Conformity(sole), shoe.TreadGrip));
        }

        float dress = Scuff(Shoe.DressShoe, "Concrete");
        float sneaker = Scuff(Shoe.Sneaker, "Concrete");
        float bare = Scuff(Shoe.Bare, "Concrete");
        _o.WriteLine($"grit content on concrete: dress {dress:F2}, sneaker {sneaker:F2}, bare {bare:F2}");

        Assert.True(dress > sneaker * 1.5f);
        Assert.True(sneaker > bare * 3f);

        // And a smooth floor gives a hard shoe far less to rattle over than a cast slab does.
        Assert.True(Scuff(Shoe.DressShoe, "Marble") < Scuff(Shoe.DressShoe, "Concrete") * 0.5f);
    }

    /// <summary>
    /// A wooden floor rings and a concrete one does not — which is what "hollow" means, and it comes
    /// from the same <see cref="PanelAcoustics"/> that rings a door and a car's wing.
    /// </summary>
    [Fact]
    public void AWoodenFloorRingsAndASlabDoesNot()
    {
        Assert.True(Footsteps.FreeSpanM("Wood") > 0f, "a board deck spans its joists");
        Assert.Equal(0f, Footsteps.FreeSpanM("Concrete"));
        Assert.Equal(0f, Footsteps.FreeSpanM("Grass"));

        var wood = AcousticRegistry.GetProperties("Wood");
        float hz = PanelAcoustics.RingHz(wood, Footsteps.FreeSpanM("Wood"),
                                         Footsteps.FreeSpanM("Wood") * 2.4f, Footsteps.DeckThicknessM("Wood"));
        _o.WriteLine($"a floorboard deck between joists rings at {hz:F0} Hz");
        // A note, not a rumble and not a whistle: floorboards land in the low mids.
        Assert.InRange(hz, 80f, 900f);
    }

    /// <summary>
    /// Grass swallows a footstep and concrete returns it. Not a mixing decision — it is
    /// <see cref="MaterialProperties.Absorption"/>, which was already there for every other reason.
    /// </summary>
    [Fact]
    public void SoftGroundIsQuieterThanHardGround()
    {
        float Level(string surface) => Footsteps.MeasuredLevelDb(new Footstep
        {
            Surface = surface, Shoe = Shoe.Sneaker, BodyMassKg = 78f, SpeedMps = 1.4f, Seed = 1,
        });

        float concrete = Level("Concrete"), grass = Level("Grass"), carpet = Level("Carpet");
        _o.WriteLine($"concrete {concrete:F0} dB, grass {grass:F0}, carpet {carpet:F0}");

        Assert.True(concrete > grass, "grass takes three quarters of what reaches it");
        Assert.True(concrete > carpet);
        // And everything is in the range a footstep actually measures at a metre.
        foreach (float db in new[] { concrete, grass, carpet }) Assert.InRange(db, 40f, 85f);
    }

    /// <summary>Running is louder than walking, because the foot arrives faster — not because
    /// anything is scaled by a "running" flag.</summary>
    [Fact]
    public void RunningIsLouderThanWalkingBecauseTheFootArrivesFaster()
    {
        float Level(float speed) => Footsteps.MeasuredLevelDb(new Footstep
        {
            Surface = "Concrete", Shoe = Shoe.Sneaker, BodyMassKg = 78f, SpeedMps = speed, Seed = 1,
        });
        float walk = Level(1.4f), run = Level(4.2f);
        _o.WriteLine($"walking {walk:F0} dB, running {run:F0} dB");
        Assert.True(run > walk + 3f, $"a run is only {run - walk:F1} dB over a walk");
    }

    /// <summary>
    /// Two steps of the same kind are not the same buffer, and the same step twice IS.
    ///
    /// Both halves matter: the first is why a corridor does not sound like a list being played, and
    /// the second is why the engine can cache a handful of buffers instead of rendering per footfall.
    /// </summary>
    [Fact]
    public void StepsVaryButAreRepeatable()
    {
        var a = new Footstep { Surface = "Gravel", Shoe = Shoe.Boot, Seed = 1 };
        var b = a with { Seed = 2 };

        var ra = Footsteps.Render(a);
        var again = Footsteps.Render(a);
        var rb = Footsteps.Render(b);

        Assert.Equal(ra, again);                      // same step, same buffer
        Assert.False(ra.Length == rb.Length && ra.SequenceEqual(rb),
                     "two different steps rendered identically");
    }

    /// <summary>A step can name itself, and be read back — which is how the client renders one the
    /// server never had to ship.</summary>
    [Fact]
    public void AStepCanBeNamedAndReadBack()
    {
        var step = new Footstep
        {
            Surface = "Gravel", Shoe = Shoe.DressShoe, BodyMassKg = 82f, SpeedMps = 1.4f, Seed = 3,
        };
        string key = Footsteps.Key(step);
        _o.WriteLine(key);

        Assert.True(Footsteps.TryParseKey(key, out var back));
        Assert.Equal("gravel", back.Surface);
        Assert.Equal(Shoe.DressShoe.SoleMaterial, back.Shoe.SoleMaterial);
        Assert.Equal(3, back.Seed);

        Assert.False(Footsteps.TryParseKey("weapon:akm", out _));
        Assert.False(Footsteps.TryParseKey("", out _));
    }

    /// <summary>Every sole and every surface the model names is actually in the registry — a
    /// material that does not exist silently becomes Generic, and the shoe stops being that shoe.</summary>
    [Fact]
    public void EverySoleAndSurfaceTheModelNamesExists()
    {
        foreach (var make in Shoe.Presets.Values)
        {
            var shoe = make();
            Assert.True(AcousticRegistry.IsKnown(shoe.SoleMaterial),
                        $"the {shoe.Name}'s sole material '{shoe.SoleMaterial}' is not in the registry");
        }
        foreach (string surface in new[] { "Concrete", "Wood", "Grass", "Gravel", "Dirt", "Metal", "Carpet", "Marble" })
            Assert.True(AcousticRegistry.IsKnown(surface), $"'{surface}' is not in the registry");
    }
}
