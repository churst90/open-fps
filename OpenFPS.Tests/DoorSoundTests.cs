using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// What a door sounds like, and why it is not one sound.
///
/// Everything here comes out of four things already known about the leaf — its material, its size,
/// its thickness and how fast it is travelling. Nothing is a per-door setting, which is the only way
/// a door somebody builds out of a material somebody else invented can sound like anything at all.
///
/// These tests are about RELATIONSHIPS, not absolute values. It does not matter very much whether a
/// steel door rings at 480 Hz or 520; it matters enormously that it rings higher and far longer than
/// a wooden one, that a slam is louder than a close by about the right amount, and that a carpet
/// does not ring. Pin the physics, not the taste.
/// </summary>
public class DoorSoundTests
{
    public DoorSoundTests() => AcousticRegistry.Initialize();

    private static MaterialProperties Of(string name) => AcousticRegistry.GetProperties(name);

    // ── The note ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The note comes out of stiffness against WEIGHT, and that ordering is less dramatic than
    /// intuition expects: steel is twenty times stiffer than wood and twelve times heavier, so a
    /// steel and a wooden leaf of the same thickness ring surprisingly close together. What actually
    /// tells them apart is how LONG (see below), not how high — and a test that asserted otherwise
    /// would be asserting a folk belief rather than the physics.
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

        // All of them land somewhere a person would call a knock or a clang rather than a hum or a
        // whistle, which is the sanity check that the plate law has its units the right way up.
        foreach (float hz in new[] { steel, wood, glass, plastic })
            Assert.InRange(hz, 40f, 400f);
    }

    /// <summary>A big thin panel booms and a small thick one knocks — the note goes as thickness over
    /// span squared, which is the shape of the plate law and the reason a door and a cupboard front
    /// of the same wood sound nothing alike.</summary>
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

    /// <summary>
    /// A carpet does not ring, and the reason is worth being precise about: it is not that it has no
    /// modes — every slab of everything has modes — it is that whatever note it has dies with the
    /// blow that caused it. Having a note and ringing are different questions, and only the second
    /// one is audible.
    /// </summary>
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

    /// <summary>
    /// How long it rings is internal damping, which is a different property from how much airborne
    /// sound it absorbs. Steel and carpet absorb about the same from the air and differ by orders of
    /// magnitude here — which is exactly why one clangs and the other thuds.
    /// </summary>
    [Fact]
    public void HowLongItRingsIsDampingAndNotAbsorption()
    {
        // At ONE frequency, so the comparison is about damping and not muddled by the two materials
        // also ringing at different notes — which is what the name of this test claims.
        const float sameNote = 200f;
        float steelRing = DoorAcoustics.RingSeconds(Of("Metal"), sameNote);
        float plasticRing = DoorAcoustics.RingSeconds(Of("Plastic"), sameNote);

        Assert.True(steelRing > plasticRing * 2f,
                    $"steel rang for {steelRing:F2} s and plastic for {plasticRing:F2} s");

        // And the absolute matters as much as the ratio. Steel's own loss factor says a FREE steel
        // plate rings for the best part of a minute; a hung door clangs for a moment, because being
        // hung in a frame carries energy out through the edges far faster than the steel itself
        // loses it. A figure that only looks sane because it hit a clamp is not a sane figure.
        Assert.InRange(steelRing, 0.15f, 1.0f);
    }

    // ── How hard it was shut ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The impact is kinetic energy arriving, so twice the speed is about six decibels. That ratio is
    /// what makes a slam recognisable AS a slam rather than as a louder close.
    /// </summary>
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

    /// <summary>
    /// A seal absorbs the last of the energy into squeezing rubber and pushing air out, so a sealed
    /// door is QUIETER than the same leaf unsealed and it robs the panel of the ring. That is the
    /// difference between a car door and a garden gate, and it is why an expensive car sounds
    /// expensive.
    /// </summary>
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
    /// Closing, the leaf meets the frame FIRST and the bolt snaps home a moment later, once the seal
    /// has compressed. (Opening is the other way round, which is why it is a separate method.)
    ///
    /// And "a moment later" has to be long enough to be heard as a separate event: forward masking
    /// from a broadband impact runs well over a tenth of a second, so a click twenty milliseconds
    /// behind a thump is not a click, it is part of the thump. The first person to listen to this
    /// said exactly that — "just a thump" — while every test passed.
    /// </summary>
    [Fact]
    public void TheLatchComesFirstAndTheRingComesLast()
    {
        var sounds = DoorAcoustics.Closing(Of("Metal"), Vector3.Zero, Vector3.Zero,
                                           0.9f, 2.1f, 0.04f, 30f, 2f, hasSeal: false);

        // Three of them: the bolt rides the ramp of the keeper, it drops into it, and the door
        // itself answers. A recorded latch puts forty-two per cent of its energy below 200 Hz — it
        // is not a little click, it is a mechanism bolted through a leaf, and what you mostly hear
        // is the leaf. Rendered as pure click it read as a puff of air, because a bright transient
        // with nothing underneath it is a puff.
        var latches = sounds.Where(s => s.Kind == DoorSoundKind.Latch).OrderBy(s => s.DelaySeconds).ToList();
        Assert.Equal(3, latches.Count);
        var latch = latches[^1];

        // ...and the body is the loudest of the three, as it is in the recording.
        Assert.True(latches.OrderByDescending(l => l.LevelDb).First().Hz < 400f,
                    "the loudest part of the latch is a click, so there is nothing underneath it");
        var impact = sounds.Single(s => s.Kind == DoorSoundKind.Impact);
        var panel = sounds.Single(s => s.Kind == DoorSoundKind.Panel);

        // Far enough apart to be two events and close enough to be one mechanism.
        float gap = latches[1].DelaySeconds - latches[0].DelaySeconds;
        Assert.InRange(gap, 0.012f, 0.06f);

        // Nearly all tone and very little noise — steel on steel RINGS, briefly. At three quarters
        // noise the renderer made it a hiss, which is what the first listening test heard.
        Assert.All(latches, l => Assert.True(l.Noisiness <= 0.6f, $"the latch is {l.Noisiness:F2} noise"));

        Assert.True(latch.DelaySeconds > impact.DelaySeconds, "the bolt snaps home after the leaf seats");
        Assert.True(latch.DelaySeconds - impact.DelaySeconds > 0.05f,
                    $"the latch is only {(latch.DelaySeconds - impact.DelaySeconds) * 1000f:F0} ms behind the "
                  + "impact, which is inside its masking and will not be heard as a separate sound");
        Assert.True(panel.DelaySeconds >= impact.DelaySeconds, "the panel rings after the blow, not before");
        Assert.True(panel.DecaySeconds > impact.DecaySeconds, "the ring outlasts the blow");
    }

    /// <summary>The latch is at the latch EDGE and the ring is from the whole leaf. A door heard
    /// close to is not a point source, and where each part of it comes from is information.</summary>
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

    /// <summary>
    /// Opening is a different event, not a quieter version of the same one: the leaf comes AWAY from
    /// the frame, so nothing strikes the frame.
    /// </summary>
    [Fact]
    public void OpeningIsADifferentEventAndNotAQuieterClose()
    {
        var sounds = DoorAcoustics.Opening(Of("Metal"), Vector3.Zero, Vector3.One,
                                           0.9f, 2.1f, 0.004f, 45f, 0.9f, hingeDryness: 0f, hasSeal: true);

        Assert.Contains(sounds, s => s.Kind == DoorSoundKind.Latch);
        Assert.Contains(sounds, s => s.Kind == DoorSoundKind.Seal);
        Assert.DoesNotContain(sounds, s => s.Kind == DoorSoundKind.Impact);
    }

    /// <summary>
    /// What the leaf is made of is heard when it opens. The leaf rings a little as the bolt lets go,
    /// and a steel one rings several times longer than a wooden one of the same size (0.7 s against
    /// 0.14) — the clank of a
    /// fire door's push bar against the knock of a wooden door. Opening used to take the material
    /// and ignore it, so the two were identical.
    /// </summary>
    [Fact]
    public void ASteelLeafRingsLongerThanAWoodenOneWhenItsLatchIsWorked()
    {
        var steel = DoorAcoustics.Opening(Of("Metal"), Vector3.Zero, Vector3.One, 0.9f, 2.1f, 0.004f, 45f, 0.9f, 0f, true);
        var wood = DoorAcoustics.Opening(Of("Wood"), Vector3.Zero, Vector3.One, 0.9f, 2.1f, 0.04f, WoodDoorKg, 0.9f, 0f, false);
        float Ring(List<DoorSound> d) => d.Where(s => s.Kind == DoorSoundKind.Panel).Sum(s => s.DecaySeconds);
        Assert.True(Ring(steel) > 3f * Ring(wood), $"steel rings {Ring(steel):F2} s, wood {Ring(wood):F2} s");
    }

    /// <summary>
    /// The latch spring kicks every leaf about the same, so a light leaf answers it louder than a
    /// heavy one: the energy goes as one over the mass, ten decibels for ten times the weight. And a
    /// light leaf's dry hinge sings higher: the pin is a spring and the leaf the mass on it.
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

        // ...and it comes from the hinge, which is not where the handle is.
        Assert.Equal(Vector3.One, hinge.Position);
    }

    // ── The edge speed ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A wide door's edge travels further in the same swing, so it lands harder. That is why a gate
    /// bangs and a cupboard clicks even when both take the same second to close.
    /// </summary>
    [Fact]
    public void AWideLeafLandsHarderThanANarrowOneInTheSameTime()
    {
        float gate = DoorAcoustics.EdgeSpeed(1.2f, MathF.PI / 2f, 0.9f);
        float cupboard = DoorAcoustics.EdgeSpeed(0.35f, MathF.PI / 2f, 0.9f);
        Assert.True(gate > cupboard * 3f, $"gate edge {gate:F2} m/s against cupboard {cupboard:F2} m/s");

        Assert.Equal(0f, DoorAcoustics.EdgeSpeed(1f, MathF.PI / 2f, 0f));   // a swing of no duration
    }

    // ── The whole of it, as a listener would tell them apart ────────────────────────────────────

    /// <summary>
    /// The point of all of it: two doors somebody could actually confuse, told apart by ear. A steel
    /// fire door and a plywood one, same size, same swing — and everything about what you hear is
    /// different, with nobody having authored either.
    /// </summary>
    [Fact]
    public void AListenerCanTellASteelDoorFromAWoodenOne()
    {
        float speed = DoorAcoustics.EdgeSpeed(0.9f, MathF.PI / 2f, 0.9f);
        var steel = DoorAcoustics.Closing(Of("Metal"), Vector3.Zero, Vector3.Zero, 0.9f, 2.1f, 0.04f, 60f, speed, true);
        var ply = DoorAcoustics.Closing(Of("Wood"), Vector3.Zero, Vector3.Zero, 0.9f, 2.1f, 0.04f, 18f, speed, false);

        var steelRing = steel.Single(s => s.Kind == DoorSoundKind.Panel);
        var plyRing = ply.Single(s => s.Kind == DoorSoundKind.Panel);

        // Not the pitch — the two are within a fifth of each other, because steel's stiffness is
        // paid for in weight. What a listener actually has to go on is everything else.
        // Longer, but not by the order of magnitude the materials' own loss factors suggest: once
        // both are hung in a frame, the frame is most of the damping and it damps them equally.
        Assert.True(steelRing.DecaySeconds > plyRing.DecaySeconds * 1.4f,
                    $"steel rang for {steelRing.DecaySeconds:F2} s and ply for {plyRing.DecaySeconds:F2} s");
        Assert.Contains(steel, s => s.Kind == DoorSoundKind.Seal);
        Assert.DoesNotContain(ply, s => s.Kind == DoorSoundKind.Seal);

        // ...and the sealed steel door is the QUIETER of the two despite being three times the mass,
        // which is the whole of why an expensive car door sounds expensive.
        float steelImpact = steel.Single(s => s.Kind == DoorSoundKind.Impact).LevelDb;
        float plyImpact = ply.Single(s => s.Kind == DoorSoundKind.Impact).LevelDb;
        Assert.True(steelImpact < plyImpact + 2f,
                    $"sealed steel {steelImpact:F1} dB against bare ply {plyImpact:F1} dB");
    }
}
