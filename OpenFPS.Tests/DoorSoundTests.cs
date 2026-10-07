using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// A door's sounds from its leaf's material, size, thickness and speed alone, with no per-door
/// setting. The tests pin relationships (steel rings longer than wood, a slam is louder than a close by
/// the right amount, carpet does not ring), not absolute values.
/// </summary>
public class DoorSoundTests
{
    public DoorSoundTests() => AcousticRegistry.Initialize();

    private static MaterialProperties Of(string name) => AcousticRegistry.GetProperties(name);

    // ── The note ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The note is stiffness against weight: steel is twenty times stiffer than wood and twelve times
    /// heavier, so same-thickness leaves ring close together. How long they ring tells them apart.
    /// </summary>
    [Fact]
    public void WhatItIsMadeOfDecidesTheNote()
    {
        const float w = 0.9f, h = 2.1f, t = 0.04f;
        float steel = DoorAcoustics.PanelHz(Of("Metal"), w, h, t);
        float wood = DoorAcoustics.PanelHz(Of("Wood"), w, h, t);
        float glass = DoorAcoustics.PanelHz(Of("Glass"), w, h, t);
        float plastic = DoorAcoustics.PanelHz(Of("Plastic"), w, h, t);

        Assert.True(steel > wood, $"steel rang at {steel:F0} Hz and wood at {wood:F0}");
        Assert.True(glass > steel, $"glass rang at {glass:F0} Hz and steel at {steel:F0}");
        Assert.True(plastic < wood, $"plastic rang at {plastic:F0} Hz and wood at {wood:F0}");

        // A knock or a clang, not a hum or a whistle: the plate law's units are the right way up.
        foreach (float hz in new[] { steel, wood, glass, plastic })
            Assert.InRange(hz, 40f, 400f);
    }

    /// <summary>The note goes as thickness over span squared (the plate law): a big thin panel booms
    /// and a small thick one knocks.</summary>
    [Fact]
    public void ShapeDecidesTheNoteTooAndTheSpanDominates()
    {
        var wood = Of("Wood");
        float door = DoorAcoustics.PanelHz(wood, 0.9f, 2.1f, 0.04f);
        float cupboard = DoorAcoustics.PanelHz(wood, 0.4f, 0.5f, 0.04f);
        float thin = DoorAcoustics.PanelHz(wood, 0.9f, 2.1f, 0.01f);

        Assert.True(cupboard > door, $"the small door rang at {cupboard:F0} Hz and the big one at {door:F0}");
        Assert.True(thin < door, $"the thin leaf rang at {thin:F0} Hz and the thick one at {door:F0}");
    }

    /// <summary>A carpet has a note but it dies with the blow: it does not ring audibly, though it still
    /// hits the frame.</summary>
    [Fact]
    public void SomeThingsDoNotRingAtAll()
    {
        var carpet = Of("Carpet");
        float hz = DoorAcoustics.PanelHz(carpet, 0.9f, 2.1f, 0.04f);
        Assert.False(PanelAcoustics.RingsAudibly(carpet, hz),
                     $"a carpet was found to ring for {PanelAcoustics.RingSeconds(carpet, hz):F2} s");

        // Something with no stiffness at all has no note to begin with.
        Assert.Equal(0f, DoorAcoustics.PanelHz(Of("None"), 0.9f, 2.1f, 0.04f));

        var sounds = DoorAcoustics.Closing(carpet, Vector3.Zero, Vector3.Zero,
                                           0.9f, 2.1f, 0.04f, 20f, 1.5f, hasSeal: false);
        Assert.DoesNotContain(sounds, s => s.Kind == DoorSoundKind.Panel);
        Assert.Contains(sounds, s => s.Kind == DoorSoundKind.Impact);   // it still hits the frame
    }

    // ── How long it rings ───────────────────────────────────────────────────────────────────────

    /// <summary>How long it rings is internal damping, not airborne absorption: steel and carpet absorb
    /// about the same from the air and differ by orders of magnitude here.</summary>
    [Fact]
    public void HowLongItRingsIsDampingAndNotAbsorption()
    {
        // At one frequency, so only the damping differs.
        const float sameNote = 200f;
        float steelRing = DoorAcoustics.RingSeconds(Of("Metal"), sameNote);
        float plasticRing = DoorAcoustics.RingSeconds(Of("Plastic"), sameNote);

        Assert.True(steelRing > plasticRing * 2f,
                    $"steel rang for {steelRing:F2} s and plastic for {plasticRing:F2} s");

        // A free steel plate rings for most of a minute; a hung door clangs for a moment, because the
        // frame carries energy out through the edges far faster than the steel loses it.
        Assert.InRange(steelRing, 0.15f, 1.0f);
    }

    // ── How hard it was shut ────────────────────────────────────────────────────────────────────

    /// <summary>The impact is kinetic energy arriving: twice the speed is about 6 dB, which is what makes
    /// a slam a slam rather than a louder close.</summary>
    [Fact]
    public void TwiceTheSpeedIsAboutSixDecibels()
    {
        float gentle = Impact(speed: 1.0f);
        float harder = Impact(speed: 2.0f);
        Assert.Equal(6.0f, harder - gentle, 1);

        float slam = Impact(speed: 4.0f);
        Assert.Equal(12.0f, slam - gentle, 1);
    }

    [Fact]
    public void AHeavierLeafLandsHarderAtTheSameSpeed()
    {
        Assert.True(Impact(speed: 1.5f, mass: 40f) > Impact(speed: 1.5f, mass: 10f));
    }

    private static float Impact(float speed, float mass = 25f, bool hasSeal = false)
        => DoorAcoustics.Closing(Of("Wood"), Vector3.Zero, Vector3.Zero, 0.9f, 2.1f, 0.04f, mass, speed, hasSeal)
            .Single(s => s.Kind == DoorSoundKind.Impact).LevelDb;

    // ── The seal ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A seal takes the last of the energy into rubber and expelled air: a sealed leaf is
    /// quieter than the same leaf bare and loses most of its ring.</summary>
    [Fact]
    public void ASealMakesItQuieterAndTakesTheRingOutOfIt()
    {
        var bare = DoorAcoustics.Closing(Of("Metal"), Vector3.Zero, Vector3.Zero, 1.0f, 1.2f, 0.01f, 25f, 2f, hasSeal: false);
        var sealed_ = DoorAcoustics.Closing(Of("Metal"), Vector3.Zero, Vector3.Zero, 1.0f, 1.2f, 0.01f, 25f, 2f, hasSeal: true);

        float bareImpact = bare.Single(s => s.Kind == DoorSoundKind.Impact).LevelDb;
        float sealedImpact = sealed_.Single(s => s.Kind == DoorSoundKind.Impact).LevelDb;
        Assert.True(sealedImpact < bareImpact - 2f, $"sealed {sealedImpact:F1} dB against bare {bareImpact:F1} dB");

        float bareRing = bare.Single(s => s.Kind == DoorSoundKind.Panel).LevelDb;
        float sealedRing = sealed_.Single(s => s.Kind == DoorSoundKind.Panel).LevelDb;
        Assert.True(sealedRing < bareRing - 8f, "the seal should take most of the ring with it");

        // And there is a seal sound where there is a seal, and none where there is not.
        Assert.Contains(sealed_, s => s.Kind == DoorSoundKind.Seal);
        Assert.DoesNotContain(bare, s => s.Kind == DoorSoundKind.Seal);
    }

    // ── The order of it ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Closing, the leaf meets the frame first and the bolt snaps home more than 50 ms later: forward
    /// masking from a broadband impact runs past 100 ms, and a click 20 ms behind the thump was heard
    /// as "just a thump" while every test passed.
    /// </summary>
    [Fact]
    public void TheLatchComesFirstAndTheRingComesLast()
    {
        var sounds = DoorAcoustics.Closing(Of("Metal"), Vector3.Zero, Vector3.Zero,
                                           0.9f, 2.1f, 0.04f, 30f, 2f, hasSeal: false);

        // Three: the bolt rides the keeper's ramp, drops in, and the leaf answers. A recorded latch has
        // 42 % of its energy below 200 Hz; rendered as a pure click it read as a puff of air.
        var latches = sounds.Where(s => s.Kind == DoorSoundKind.Latch).OrderBy(s => s.DelaySeconds).ToList();
        Assert.Equal(3, latches.Count);
        var latch = latches[^1];

        // The body is the loudest of the three, as in the recording.
        Assert.True(latches.OrderByDescending(l => l.LevelDb).First().Hz < 400f,
                    "the loudest part of the latch is a click, so there is nothing underneath it");
        var impact = sounds.Single(s => s.Kind == DoorSoundKind.Impact);
        var panel = sounds.Single(s => s.Kind == DoorSoundKind.Panel);

        // Far enough apart to be two events and close enough to be one mechanism.
        float gap = latches[1].DelaySeconds - latches[0].DelaySeconds;
        Assert.InRange(gap, 0.012f, 0.06f);

        // Steel on steel rings briefly: mostly tone. At three quarters noise it rendered as a hiss.
        Assert.All(latches, l => Assert.True(l.Noisiness <= 0.6f, $"the latch is {l.Noisiness:F2} noise"));

        Assert.True(latch.DelaySeconds > impact.DelaySeconds, "the bolt snaps home after the leaf seats");
        Assert.True(latch.DelaySeconds - impact.DelaySeconds > 0.05f,
                    $"the latch is only {(latch.DelaySeconds - impact.DelaySeconds) * 1000f:F0} ms behind the "
                  + "impact, which is inside its masking and will not be heard as a separate sound");
        Assert.True(panel.DelaySeconds >= impact.DelaySeconds, "the panel rings after the blow, not before");
        Assert.True(panel.DecaySeconds > impact.DecaySeconds, "the ring outlasts the blow");
    }

    /// <summary>The latch and the blow come from the latch edge, the ring from the leaf's centre.</summary>
    [Fact]
    public void EachSoundComesFromWhereItActuallyHappens()
    {
        var latchEdge = new Vector3(5, 1, 0);
        var centre = new Vector3(4.5f, 1, 0);
        var sounds = DoorAcoustics.Closing(Of("Metal"), latchEdge, centre, 0.9f, 2.1f, 0.04f, 30f, 2f, false);

        Assert.All(sounds.Where(s => s.Kind == DoorSoundKind.Latch), l => Assert.Equal(latchEdge, l.Position));
        Assert.Equal(latchEdge, sounds.Single(s => s.Kind == DoorSoundKind.Impact).Position);
        Assert.Equal(centre, sounds.Single(s => s.Kind == DoorSoundKind.Panel).Position);
    }

    // ── Opening ─────────────────────────────────────────────────────────────────────────────────

    private const float WoodDoorKg = 0.9f * 2.1f * 0.04f * 650f;

    /// <summary>Opening, the leaf comes away from the frame: a latch and a seal, no impact.</summary>
    [Fact]
    public void OpeningIsADifferentEventAndNotAQuieterClose()
    {
        var sounds = DoorAcoustics.Opening(Of("Metal"), Vector3.Zero, Vector3.One,
                                           0.9f, 2.1f, 0.004f, 45f, 0.9f, hingeDryness: 0f, hasSeal: true);

        Assert.Contains(sounds, s => s.Kind == DoorSoundKind.Latch);
        Assert.Contains(sounds, s => s.Kind == DoorSoundKind.Seal);
        Assert.DoesNotContain(sounds, s => s.Kind == DoorSoundKind.Impact);
    }

    /// <summary>The leaf rings as the bolt lets go, a steel one several times longer than a wooden one
    /// (0.7 s against 0.14). Opening once ignored the material, and the two were identical.</summary>
    [Fact]
    public void ASteelLeafRingsLongerThanAWoodenOneWhenItsLatchIsWorked()
    {
        var steel = DoorAcoustics.Opening(Of("Metal"), Vector3.Zero, Vector3.One, 0.9f, 2.1f, 0.004f, 45f, 0.9f, 0f, true);
        var wood = DoorAcoustics.Opening(Of("Wood"), Vector3.Zero, Vector3.One, 0.9f, 2.1f, 0.04f, WoodDoorKg, 0.9f, 0f, false);
        float Ring(List<DoorSound> d) => d.Where(s => s.Kind == DoorSoundKind.Panel).Sum(s => s.DecaySeconds);
        Assert.True(Ring(steel) > 3f * Ring(wood), $"steel rings {Ring(steel):F2} s, wood {Ring(wood):F2} s");
    }

    /// <summary>
    /// The latch spring kicks every leaf about the same, so the answer goes as one over the mass (10 dB
    /// for ten times the weight); a light leaf's dry hinge sings higher, the pin a spring and the leaf
    /// its mass.
    /// </summary>
    [Fact]
    public void ALightLeafAnswersTheLatchLouderAndCreaksHigher()
    {
        Vector3 z = Vector3.Zero, one = Vector3.One;
        var light = DoorAcoustics.Opening(Of("Wood"), z, one, 0.9f, 2.1f, 0.04f, WoodDoorKg / 10f, 0.9f, 1f, false);
        var heavy = DoorAcoustics.Opening(Of("Wood"), z, one, 0.9f, 2.1f, 0.04f, WoodDoorKg, 0.9f, 1f, false);
        float Body(List<DoorSound> d) => d.Where(s => s.Kind == DoorSoundKind.Latch).MinBy(s => s.Hz)!.LevelDb;
        Assert.InRange(Body(light) - Body(heavy), 9.5f, 10.5f);
        float Hinge(List<DoorSound> d) => d.Single(s => s.Kind == DoorSoundKind.Hinge).Hz;
        Assert.InRange(Hinge(heavy), 470f, 482f);                      // the reference door keeps its note
        Assert.InRange(Hinge(light) / Hinge(heavy), 3.0f, 3.3f);       // root ten
    }

    /// <summary>Dry hinges sing, oiled ones do not, and the singing lasts as long as the swing.</summary>
    [Fact]
    public void OnlyDryHingesSing()
    {
        var oiled = DoorAcoustics.Opening(Of("Wood"), Vector3.Zero, Vector3.One, 0.9f, 2.1f, 0.04f, WoodDoorKg, 0.9f, 0f, false);
        var dry = DoorAcoustics.Opening(Of("Wood"), Vector3.Zero, Vector3.One, 0.9f, 2.1f, 0.04f, WoodDoorKg, 0.9f, 1f, false);

        Assert.DoesNotContain(oiled, s => s.Kind == DoorSoundKind.Hinge);
        var hinge = dry.Single(s => s.Kind == DoorSoundKind.Hinge);
        Assert.True(hinge.DecaySeconds > 0.5f, "the hinges should sing for most of the swing");

        // From the hinge, not the handle.
        Assert.Equal(Vector3.One, hinge.Position);
    }

    // ── The edge speed ──────────────────────────────────────────────────────────────────────────

    /// <summary>A wide leaf's edge travels further in the same swing time, so it lands harder.</summary>
    [Fact]
    public void AWideLeafLandsHarderThanANarrowOneInTheSameTime()
    {
        float gate = DoorAcoustics.EdgeSpeed(1.2f, MathF.PI / 2f, 0.9f);
        float cupboard = DoorAcoustics.EdgeSpeed(0.35f, MathF.PI / 2f, 0.9f);
        Assert.True(gate > cupboard * 3f, $"gate edge {gate:F2} m/s against cupboard {cupboard:F2} m/s");

        Assert.Equal(0f, DoorAcoustics.EdgeSpeed(1f, MathF.PI / 2f, 0f));   // a swing of no duration
    }

    // ── Telling doors apart ─────────────────────────────────────────────────────────────────────

    /// <summary>A steel fire door and a plywood one, same size and swing, differ in ring, seal and
    /// level, with nobody having authored either.</summary>
    [Fact]
    public void AListenerCanTellASteelDoorFromAWoodenOne()
    {
        float speed = DoorAcoustics.EdgeSpeed(0.9f, MathF.PI / 2f, 0.9f);
        var steel = DoorAcoustics.Closing(Of("Metal"), Vector3.Zero, Vector3.Zero, 0.9f, 2.1f, 0.04f, 60f, speed, true);
        var ply = DoorAcoustics.Closing(Of("Wood"), Vector3.Zero, Vector3.Zero, 0.9f, 2.1f, 0.04f, 18f, speed, false);

        var steelRing = steel.Single(s => s.Kind == DoorSoundKind.Panel);
        var plyRing = ply.Single(s => s.Kind == DoorSoundKind.Panel);

        // Not the pitch: the two are within a fifth. Longer, but not by the materials' own loss
        // factors: hung in a frame, the frame is most of the damping and damps both alike.
        Assert.True(steelRing.DecaySeconds > plyRing.DecaySeconds * 1.4f,
                    $"steel rang for {steelRing.DecaySeconds:F2} s and ply for {plyRing.DecaySeconds:F2} s");
        Assert.Contains(steel, s => s.Kind == DoorSoundKind.Seal);
        Assert.DoesNotContain(ply, s => s.Kind == DoorSoundKind.Seal);

        // The sealed steel door lands within 2 dB of the ply despite three times the mass.
        float steelImpact = steel.Single(s => s.Kind == DoorSoundKind.Impact).LevelDb;
        float plyImpact = ply.Single(s => s.Kind == DoorSoundKind.Impact).LevelDb;
        Assert.True(steelImpact < plyImpact + 2f,
                    $"sealed steel {steelImpact:F1} dB against bare ply {plyImpact:F1} dB");
    }
}
