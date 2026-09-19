using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Whether there is a reverberant field here, which is a different question from how long one would
/// last, and the one the engine was not asking.
///
/// Reported 2026-09-18 from the speedway's front straight: the geometry reverb read up to 1579 ms and
/// swung by more than a second while the listener stood still, where the infield correctly read 101 ms.
/// The wet level was being taken from the decay time, and the decay time cannot carry it: Steam Audio's
/// parametric estimator fits an exponential to whatever energy its rays bring home and cannot report
/// that there was hardly any. Measured with AudioLab --sim-reverbfield, a walled yard with NO CEILING
/// fitted a 1.00 s tail where the same walls with a roof on fitted 0.60 s — the roofless one reading as
/// the more reverberant of the two, which is backwards, and which no threshold could have fixed because
/// both sit on the same side of every threshold.
///
/// These hold the replacement to the thing that made it work: two bounces, so a plane is not a room.
/// </summary>
public class EnclosureTests
{
    private static readonly Quaternion Q = Quaternion.Identity;

    private static Enclosure.Solid Floor(string material = "Concrete")
        => new(new Vector3(0, -0.25f, 0), new Vector3(400, 0.5f, 400), Q, material);

    private static List<Enclosure.Solid> SealedRoom(string material = "Concrete") => new()
    {
        new(new Vector3(0, -0.25f, 0), new Vector3(11, 0.5f, 11), Q, material),   // floor
        new(new Vector3(0, 4.25f, 0),  new Vector3(11, 0.5f, 11), Q, material),   // ceiling
        new(new Vector3(0, 2, 5.25f),  new Vector3(11, 4, 0.5f), Q, material),    // north
        new(new Vector3(0, 2, -5.25f), new Vector3(11, 4, 0.5f), Q, material),    // south
        new(new Vector3(5.25f, 2, 0),  new Vector3(0.5f, 4, 11), Q, material),    // east
        new(new Vector3(-5.25f, 2, 0), new Vector3(0.5f, 4, 11), Q, material),    // west
    };

    /// <summary>
    /// The fault, in one assertion. Bare hard ground is not half a room.
    ///
    /// Half of every direction from a standing listener ends in the ground, and concrete returns 98% of
    /// what reaches it, so counting one bounce scores a bare plaza at 48% enclosed — measured, on the
    /// battle spike's own geometry. It is not enclosed at all: a plane reflects sound AWAY, once, and
    /// that energy is never heard again. Following the ray past the ground is what says so.
    /// </summary>
    [Fact]
    public void BareGroundIsNotEnclosed()
    {
        float e = Enclosure.Measure(new Vector3(0, 1.7f, 0), new[] { Floor() });
        Assert.True(e < 0.05f, $"open concrete ground measured {e:P0} enclosed; one bounce called it 48%");
    }

    /// <summary>And the inside of a hard box is, which is the other end of the same scale.</summary>
    [Fact]
    public void ASealedHardRoomIsFullyEnclosed()
    {
        float e = Enclosure.Measure(new Vector3(0, 1.7f, 0), SealedRoom());
        Assert.True(e > 0.9f, $"a sealed concrete room measured only {e:P0} enclosed");
    }

    /// <summary>
    /// Take the roof off the same room and it is markedly less enclosed — the measurement the decay
    /// time got exactly backwards.
    /// </summary>
    [Fact]
    public void TakingTheRoofOffOpensTheRoom()
    {
        var room = SealedRoom();
        float sealedRoom = Enclosure.Measure(new Vector3(0, 1.7f, 0), room);
        room.RemoveAt(1);                                  // the ceiling
        float yard = Enclosure.Measure(new Vector3(0, 1.7f, 0), room);

        Assert.True(yard < sealedRoom * 0.75f,
            $"roofless {yard:P0} against sealed {sealedRoom:P0} — a yard has to read as the opener of the two");
        Assert.True(yard > 0.15f, $"it still has four hard walls; {yard:P0} is too open");
    }

    /// <summary>
    /// What a room is made of decides it, not how big it is. The same box in carpet is a fraction of the
    /// same box in concrete, which is the property that lets this work on a map nobody has written yet.
    /// </summary>
    [Fact]
    public void WhatTheWallsAreMadeOfDecidesIt()
    {
        var at = new Vector3(0, 1.7f, 0);
        float hard = Enclosure.Measure(at, SealedRoom("Concrete"));
        float soft = Enclosure.Measure(at, SealedRoom("Carpet"));
        Assert.True(soft < hard * 0.5f, $"carpet {soft:P0} against concrete {hard:P0}");
    }

    /// <summary>
    /// A single wall beside you is not a room either, however big it is. This is the speedway case: the
    /// listener stood next to a ninety-metre concrete wall and the engine put a cathedral on the race.
    /// </summary>
    [Fact]
    public void OneLongWallBesideYouIsNotARoom()
    {
        var wall = new Enclosure.Solid(new Vector3(0, 1.75f, -2f), new Vector3(90, 3.5f, 0.6f), Q, "Concrete");
        float e = Enclosure.Measure(new Vector3(0, 1.7f, 0), new[] { Floor("Grass"), wall });
        Assert.True(e < 0.2f, $"a wall and the ground measured {e:P0} enclosed");
    }

    /// <summary>
    /// The level of the field is the sum over every generation of return, which is what puts a room a
    /// long way above a wall rather than a little above it.
    /// </summary>
    [Fact]
    public void TheFieldIsTheSumOfEveryReturn()
    {
        // e/(1-e): at 1/2 the series is worth 1 (0 dB); at 9/10 it is worth 9 (9.5 dB).
        Assert.Equal(0f, Enclosure.ReverberantGainDb(0.5f), 2);
        Assert.Equal(9.54f, Enclosure.ReverberantGainDb(0.9f), 2);
        Assert.True(Enclosure.ReverberantGainDb(0.98f) > Enclosure.ReverberantGainDb(0.5f) + 15f,
            "a sealed room must be far above a half-open one, not a little above it");
        // Nothing comes back: silence, not a divide by zero.
        Assert.Equal(-80f, Enclosure.ReverberantGainDb(0f), 2);
        Assert.True(float.IsFinite(Enclosure.ReverberantGainDb(1f)), "fully enclosed must not be infinite");
    }

    /// <summary>
    /// The same place measures the same every time. The rays are a fixed Fibonacci sphere rather than a
    /// random sample precisely so that a listener standing still does not hear the room breathe.
    /// </summary>
    [Fact]
    public void TheSamePlaceMeasuresTheSameTwice()
    {
        var room = SealedRoom();
        var at = new Vector3(1.5f, 1.7f, -2f);
        Assert.Equal(Enclosure.Measure(at, room), Enclosure.Measure(at, room), 6);
    }

    /// <summary>Nothing in the world is an open field, not a crash.</summary>
    [Fact]
    public void AnEmptyWorldIsOpen()
    {
        Assert.Equal(0f, Enclosure.Measure(Vector3.Zero, Array.Empty<Enclosure.Solid>()), 6);
    }

    /// <summary>
    /// The ray-box test underneath it reports the face that was actually struck. Everything above
    /// depends on the second bounce going the right way, and the direction comes from this normal.
    /// </summary>
    [Fact]
    public void ARayReportsTheFaceItStruck()
    {
        // Straight down onto a slab: the top face, pointing up, half a metre away.
        Assert.True(GeometryUtils.RayHitsOBB(new Vector3(0, 1f, 0), -Vector3.UnitY, 60f,
                                             new Vector3(0, 0f, 0), new Vector3(10, 1f, 10), Q,
                                             out float d, out Vector3 n));
        Assert.Equal(0.5f, d, 3);
        Assert.Equal(1f, n.Y, 3);

        // And a ray pointing away from it hits nothing.
        Assert.False(GeometryUtils.RayHitsOBB(new Vector3(0, 1f, 0), Vector3.UnitY, 60f,
                                              new Vector3(0, 0f, 0), new Vector3(10, 1f, 10), Q, out _, out _));
    }
}
