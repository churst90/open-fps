using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Getting inside something, and it taking you with it.
///
/// The last of the four things a composite is for — saved, placed again, owned, ENTERED — and the one
/// that makes driving stop being a feature of its own. A house you can stand in and a car you can
/// drive away are the same structure; the only differences are whether a seat has
/// <see cref="Seat.Controls"/> set and whether anything is willing to move the root.
///
/// What these hold, in order: that a seat carries its occupant, that turning the vehicle turns the
/// person in it, that a driver's inputs reach the wheels, that the car's own behaviour comes out of
/// its profile rather than out of constants in the driving code, and that ownership gates the things
/// it should while gating none of the things it should not.
/// </summary>
public class OccupancyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-occupancy-{Guid.NewGuid():N}");

    public void Dispose() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }

    // ── Sitting down ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TakingASeatPutsYouInIt()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("driver_one", new Vector3(21, 0, 20));

        Assert.True(f.Seats.Enter(session, root, null, out string message), message);
        Assert.True(f.World.Has<OccupantComponent>(session.Entity));
        Assert.True(f.World.Get<PlayerComponent>(session.Entity).IsInVehicle);

        // With no seat named, the first one they are allowed into wins — and a driver's seat is
        // declared first, so getting into your own car puts you behind the wheel without asking.
        Assert.True(f.World.Get<OccupantComponent>(session.Entity).Controls);

        var seat = f.SeatOf(root, 0);
        Assert.Equal(OccupancyService.SeatPosition(f.RootTransform(root), seat),
                     f.World.Get<Transform>(session.Entity).Position);
    }

    [Fact]
    public void YouCannotGetIntoSomethingAcrossTheMap()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("distant", new Vector3(46, 0, 46));

        Assert.False(f.Seats.Enter(session, root, null, out string message));
        Assert.Contains("Get closer", message);
        Assert.False(f.World.Has<OccupantComponent>(session.Entity));
    }

    [Fact]
    public void TwoPeopleCannotShareASeat()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var first = f.Player("first", new Vector3(21, 0, 20));
        var second = f.Player("second", new Vector3(21, 0, 21));

        Assert.True(f.Seats.Enter(first, root, "driver", out string a), a);
        Assert.False(f.Seats.Enter(second, root, "driver", out string b));
        Assert.Contains("taken", b);

        // ...but the passenger seat is still free, and the second one falls into it unasked.
        Assert.True(f.Seats.Enter(second, root, null, out string c), c);
        Assert.Equal(1, f.World.Get<OccupantComponent>(second.Entity).SeatIndex);
        Assert.False(f.World.Get<OccupantComponent>(second.Entity).Controls);
    }

    /// <summary>
    /// Getting out puts you back where you got in, which is ground you demonstrably fitted on a moment
    /// ago. Standing in the engine bay of the car you just left is merely odd to look at and
    /// completely disorienting to listen to.
    /// </summary>
    [Fact]
    public void GettingOutPutsYouBackWhereYouGotIn()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var boardedFrom = new Vector3(21, 0, 20);
        var session = f.Player("leaver", boardedFrom);
        Assert.True(f.Seats.Enter(session, root, null, out _));

        Assert.True(f.Seats.Exit(session, out string message), message);
        Assert.False(f.World.Has<OccupantComponent>(session.Entity));
        Assert.False(f.World.Get<PlayerComponent>(session.Entity).IsInVehicle);

        var where = f.World.Get<Transform>(session.Entity).Position;
        Assert.True(Vector3.Distance(Flat(where), Flat(boardedFrom)) < 1.0f,
                    $"got out at {where}, not at the door they came in by");
    }

    /// <summary>
    /// ...unless it has driven off since. Then you step out beside where it NOW is, because the spot
    /// you climbed in from is half a mile back up the road.
    /// </summary>
    [Fact]
    public void GettingOutOfSomethingThatHasMovedPutsYouDownBesideIt()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var boardedFrom = new Vector3(21, 0, 20);
        var session = f.Player("driver_six", boardedFrom);
        Assert.True(f.Seats.Enter(session, root, null, out _));

        f.Hold(session, forward: 1f);
        f.Tick(45);
        // Back against forward motion is the brake — and back again once stopped is REVERSE, so let
        // go the moment it is down to walking pace rather than holding it through the change.
        f.Hold(session, forward: -1f);
        for (int i = 0; i < 200 && f.World.Get<DriveComponent>(f.Entity(root)).Speed > 0.2f; i++) f.Tick(1);
        f.Hold(session, forward: 0f);
        f.Tick(5);
        Assert.Equal(0f, f.World.Get<DriveComponent>(f.Entity(root)).Speed, 1);

        var car = f.RootTransform(root).Position;
        Assert.True(Vector3.Distance(Flat(car), Flat(boardedFrom)) > 5f, "it never went anywhere");

        Assert.True(f.Seats.Exit(session, out string message), message);
        var where = f.World.Get<Transform>(session.Entity).Position;
        float toCar = Vector3.Distance(Flat(where), Flat(car));
        Assert.True(toCar < 8f, $"got out {toCar:F1} m from the car it was actually in");
        Assert.True(Vector3.Distance(Flat(where), Flat(boardedFrom)) > 5f,
                    "got out back at the car park, having driven away from it");
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    [Fact]
    public void YouCannotStepOutOfSomethingThatIsMoving()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("passenger", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));

        f.World.Get<DriveComponent>(f.Entity(root)).Speed = 25f;
        Assert.False(f.Seats.Exit(session, out string message));
        Assert.Contains("still moving", message);
        Assert.True(f.World.Has<OccupantComponent>(session.Entity));
    }

    // ── Being carried ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRootCarriesItsOccupants()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("rider", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));

        var before = f.World.Get<Transform>(session.Entity).Position;
        f.World.Get<Transform>(f.Entity(root)).Position += new Vector3(0, 0, 40);
        f.Occupancy.Update(f.World, f.Lookup);

        Assert.Equal(before + new Vector3(0, 0, 40), f.World.Get<Transform>(session.Entity).Position);
    }

    /// <summary>
    /// Turn the car and the driver turns with it.
    ///
    /// Not cosmetic, and not optional: the whole world is rendered relative to where the listener is
    /// facing. A driver whose head stayed pointing north through a right-hander would hear the track,
    /// the crowd and their own engine swing around them every corner.
    /// </summary>
    [Fact]
    public void TurningTheVehicleTurnsWhoIsInIt()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("driver_two", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));

        f.Occupancy.Update(f.World, f.Lookup);          // establish the reference heading
        float before = f.World.Get<PlayerComponent>(session.Entity).Yaw;

        float quarterTurn = MathF.PI / 2f;
        f.World.Get<Transform>(f.Entity(root)).Rotation =
            Quaternion.CreateFromYawPitchRoll(quarterTurn, 0f, 0f);
        f.Occupancy.Update(f.World, f.Lookup);

        float after = f.World.Get<PlayerComponent>(session.Entity).Yaw;
        Assert.Equal(quarterTurn, MathHelper.WrapAngle(after - before), 2);
    }

    /// <summary>The floor disappearing is not the same as getting out, and neither is silent.</summary>
    [Fact]
    public void LosingTheThingYouAreInsidePutsYouOut()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("stranded", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));

        f.Maps.DestroyEntity(f.MapId, f.Entity(root));
        f.Occupancy.Update(f.World, f.Lookup);

        Assert.False(f.World.Has<OccupantComponent>(session.Entity));
        Assert.False(f.World.Get<PlayerComponent>(session.Entity).IsInVehicle);
    }

    [Fact]
    public void UngroupingSomethingPutsItsOccupantsOut()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("owner", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));

        Assert.True(f.Composites.Ungroup(f.MapId, root, "owner", false, out _, out _, out string error), error);
        Assert.False(f.World.Has<OccupantComponent>(session.Entity));
    }

    // ── Driving ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOccupantWhoIsNotDrivingMovesNothing()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("passenger", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, "passenger", out string message), message);

        f.Hold(session, forward: 1f);
        f.Tick(30);

        Assert.Equal(0f, f.World.Get<DriveComponent>(f.Entity(root)).Speed, 3);
    }

    [Fact]
    public void HoldingForwardDrivesItAndLettingGoCoastsItDown()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("driver_three", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));

        var startedAt = f.RootTransform(root).Position;
        f.Hold(session, forward: 1f);
        f.Tick(60);                                     // two seconds

        float speed = f.World.Get<DriveComponent>(f.Entity(root)).Speed;
        Assert.True(speed > 5f, $"two seconds of full throttle only reached {speed:F1} m/s");
        Assert.True(Vector3.Distance(f.RootTransform(root).Position, startedAt) > 5f,
                    "it revved but did not go anywhere");

        // A hand off the keys is a lift, not a brake: it slows on drag and rolling resistance alone.
        f.Hold(session, forward: 0f);
        f.Tick(60);
        float coasted = f.World.Get<DriveComponent>(f.Entity(root)).Speed;
        Assert.True(coasted < speed, "coasting did not slow it at all");
        Assert.True(coasted > 0f, "coasting stopped it dead, which is braking");
    }

    [Fact]
    public void ThePartsAndThePassengersComeWithIt()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var driver = f.Player("driver_four", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(driver, root, null, out _));

        var partsBefore = CompositeService.MembersOf(f.World, root)
            .ToDictionary(m => m, m => f.World.Get<Transform>(m).Position);
        var riderBefore = f.World.Get<Transform>(driver.Entity).Position;

        f.Hold(driver, forward: 1f);
        f.Tick(60);

        var moved = f.RootTransform(root).Position - new Vector3(20, 0, 20);
        Assert.True(moved.Length() > 5f);

        foreach (var kv in partsBefore)
        {
            var now = f.World.Get<Transform>(kv.Key).Position;
            Assert.True(Vector3.Distance(now, kv.Value + moved) < 0.2f,
                        "a part of the car stayed behind when it drove off");
        }
        Assert.True(Vector3.Distance(f.World.Get<Transform>(driver.Entity).Position, riderBefore + moved) < 0.2f,
                    "the driver stayed behind when the car drove off");
    }

    [Fact]
    public void SteeringOnlyTurnsItWhenItIsMoving()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("driver_five", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));

        float heading = f.World.Get<DriveComponent>(f.Entity(root)).Heading;
        f.Hold(session, forward: 0f, lateral: 1f);
        f.Tick(30);
        Assert.Equal(heading, f.World.Get<DriveComponent>(f.Entity(root)).Heading, 3);

        f.Hold(session, forward: 1f, lateral: 1f);
        f.Tick(60);
        Assert.NotEqual(heading, f.World.Get<DriveComponent>(f.Entity(root)).Heading, 3);
    }

    /// <summary>
    /// The point of the whole exercise: nothing about how a thing drives is written in the driving
    /// code. Give the same shape of car a lorry's profile and it accelerates like a lorry — not
    /// because anything tested for one, but because a lorry is fourteen tonnes pushing a barn door.
    /// </summary>
    [Fact]
    public void WhatItDrivesLikeComesOutOfWhatItIs()
    {
        var f = new Fixture(_dir);
        const string quick = "f1_v10";
        const string heavy = "diesel_truck";

        float quickSpeed = f.SpeedAfterTwoSeconds(quick, new Vector3(-35, 0, -40), "racer");
        float heavySpeed = f.SpeedAfterTwoSeconds(heavy, new Vector3(35, 0, -40), "trucker");

        Assert.True(quickSpeed > heavySpeed * 1.5f,
                    $"the {VehicleProfile.ByName(quick).MassKg:F0} kg {quick} reached {quickSpeed:F1} m/s and the "
                  + $"{VehicleProfile.ByName(heavy).MassKg:F0} kg {heavy} reached {heavySpeed:F1} m/s");
    }

    /// <summary>
    /// A driver whose client has gone quiet coasts to a stop. Not instantly — ordinary packet loss
    /// must not stutter the throttle — but a car that drives itself away forever because somebody's
    /// connection dropped is a car nobody can catch.
    /// </summary>
    [Fact]
    public void ASilentDriverCoastsToAStop()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("vanisher", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));

        f.Hold(session, forward: 1f);
        f.Tick(30);
        Assert.True(f.World.Get<DriveComponent>(f.Entity(root)).Speed > 3f);

        f.Silence();                                    // the client stops sending anything at all
        f.Tick(30 * 60);                                // a minute of nothing
        Assert.Equal(0f, f.World.Get<DriveComponent>(f.Entity(root)).Speed, 1);
    }

    // ── Ownership ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void YouCannotDriveSomebodyElsesCar()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20), owner: "cody");
        var stranger = f.Player("someone_else", new Vector3(21, 0, 20));

        Assert.False(f.Seats.Enter(stranger, root, "driver", out string refused));
        Assert.Contains("cody", refused);

        // ...but the passenger seat is not trespass. A world you cannot get a lift in is not a world.
        Assert.True(f.Seats.Enter(stranger, root, null, out string message), message);
        Assert.False(f.World.Get<OccupantComponent>(stranger.Entity).Controls);
    }

    [Fact]
    public void YouCannotTakeSomebodyElsesBuildingApart()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20), owner: "cody");

        Assert.False(f.Composites.Ungroup(f.MapId, root, "someone_else", false, out _, out _, out string error));
        Assert.Contains("cody", error);
        Assert.False(f.Composites.SaveAsTemplate(f.MapId, root, "stolen", "someone_else", false, out _, out error));
        Assert.Contains("cody", error);

        // The owner can, and so can a dev.
        Assert.True(f.Composites.SaveAsTemplate(f.MapId, root, "mine", "cody", false, out _, out error), error);
        Assert.True(f.Composites.Ungroup(f.MapId, root, "anybody", elevated: true, out _, out _, out error), error);
    }

    [Fact]
    public void AnythingWithNoOwnerIsPublicProperty()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20), owner: "");
        var anyone = f.Player("passer_by", new Vector3(21, 0, 20));

        Assert.True(f.Seats.Enter(anyone, root, "driver", out string message), message);
        Assert.True(f.World.Get<OccupantComponent>(anyone.Entity).Controls);
    }

    // ── Saving it ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seats and an engine are part of what a thing IS, so they go out with the template and come
    /// back with every instance. Place a bus twice and both have the same seats, the same way both
    /// have the same walls.
    /// </summary>
    [Fact]
    public void ACarSavedAsATemplateIsStillACarWhenItIsPlacedAgain()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20), owner: "cody");

        Assert.True(f.Composites.SaveAsTemplate(f.MapId, root, "hatchback", "cody", false, out _, out string error), error);
        Assert.True(f.Composites.Templates.TryGet("hatchback", out var template));
        Assert.Equal(2, template.Seats.Count);
        Assert.True(template.Seats[0].Controls);
        Assert.False(template.Anchored);
        Assert.NotEmpty(template.VehiclePreset);

        int second = f.Composites.Place(f.MapId, "hatchback", new Vector3(-30, 0, -30), Quaternion.Identity,
                                        "cody", out _, out error);
        Assert.True(second >= 0, error);

        var placed = f.Entity(second);
        Assert.True(f.World.Has<OccupancyComponent>(placed));
        Assert.Equal(2, f.World.Get<OccupancyComponent>(placed).Seats.Count);
        Assert.True(f.World.Has<DriveComponent>(placed));
        Assert.True(f.World.Has<Velocity>(placed));     // it moves, so the grid must treat it as moving

        // And it drives, as itself, with nobody having said so a second time.
        var session = f.Player("cody", new Vector3(-29, 0, -30));
        Assert.True(f.Seats.Enter(session, second, null, out string message), message);
        f.Hold(session, forward: 1f);
        f.Tick(60);
        Assert.True(f.World.Get<DriveComponent>(placed).Speed > 5f);
    }

    [Fact]
    public void AHouseIsNotDrivable()
    {
        var f = new Fixture(_dir);
        int root = f.BuildHouse(new Vector3(-20, 0, 40), owner: "cody");

        Assert.False(f.Composites.MakeDrivable(f.MapId, root, "v8_sports", "cody", false, out string error));
        Assert.Contains("fixed in place", error);
    }

    /// <summary>
    /// An engine in something nobody can drive is a shed with an engine in it: nothing can ever ask
    /// it to move, so all the engine buys it is a noise.
    /// </summary>
    [Fact]
    public void SomethingWithNoDrivingSeatIsNotDrivable()
    {
        var f = new Fixture(_dir);
        int root = f.BuildShell(new Vector3(-20, 0, 40));

        Assert.False(f.Composites.MakeDrivable(f.MapId, root, "v8_sports", "cody", true, out string error));
        Assert.Contains("nothing in it drives", error);

        // Passenger seats are not enough either — somebody has to be able to steer it.
        Assert.True(f.Composites.AddSeat(f.MapId, root, "bench", false, new Vector3(-20, 0, 40), 0f,
                                         "cody", true, out error), error);
        Assert.False(f.Composites.MakeDrivable(f.MapId, root, "v8_sports", "cody", true, out error));

        Assert.True(f.Composites.AddSeat(f.MapId, root, "driver", true, new Vector3(-20, 0, 40), 0f,
                                         "cody", true, out error), error);
        Assert.True(f.Composites.MakeDrivable(f.MapId, root, "v8_sports", "cody", true, out error), error);
    }

    /// <summary>
    /// Every refusal names what IS free. A no on its own is a no a player has to go and investigate,
    /// and investigating a car you cannot see means walking round it trying doors.
    /// </summary>
    [Fact]
    public void BeingTurnedAwayFromASeatTellsYouWhichOnesAreFree()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20), owner: "cody");
        var owner = f.Player("cody", new Vector3(21, 0, 20));
        var friend = f.Player("mate", new Vector3(21, 0, 21));

        // Refused because it is not theirs to drive, with nobody in it at all.
        Assert.False(f.Seats.Enter(friend, root, "driver", out string notYours));
        Assert.Contains("cannot drive it", notYours);
        Assert.Contains("passenger", notYours);

        // ...and refused because somebody is already in it, which is the more useful of the two facts.
        Assert.True(f.Seats.Enter(owner, root, null, out _));
        Assert.False(f.Seats.Enter(friend, root, "driver", out string taken));
        Assert.Contains("taken", taken);
        Assert.Contains("passenger", taken);
    }

    /// <summary>
    /// Owning a thing permits; it never compels. Wanting to ride in your own car is not a special
    /// case that has to be allowed for — asking for the passenger seat asks for the passenger seat.
    /// </summary>
    [Fact]
    public void AnOwnerMayRideInTheirOwnVehicle()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20), owner: "cody");
        var owner = f.Player("cody", new Vector3(21, 0, 20));

        Assert.True(f.Seats.Enter(owner, root, "passenger", out string message), message);
        Assert.False(f.World.Get<OccupantComponent>(owner.Entity).Controls);
    }

    // ── A car that was parked, not built ────────────────────────────────────────────────────────

    /// <summary>
    /// "vehicle:i4_economy" is a hatchback built from its profile: a closed cabin that grows a room,
    /// four seats with the driver's first, and an engine. Nobody wrote a file for it.
    /// </summary>
    [Fact]
    public void AParkedCarIsBuiltFromItsProfile()
    {
        var f = new Fixture(_dir);
        int root = f.Composites.Place(f.MapId, "vehicle:i4_economy", new Vector3(20, 0, 20), Quaternion.Identity,
                                      "", out int parts, out string error);
        Assert.True(root >= 0, error);
        var e = f.Entity(root);
        Assert.True(f.World.Has<DriveComponent>(e), "it does not drive");
        var seats = f.World.Get<OccupancyComponent>(e).Seats;
        Assert.Equal(4, seats.Count);
        Assert.True(seats[0].Controls);
        Assert.True(parts >= 9, $"only {parts} parts");

        // The cabin is a room — which is the whole of what makes sitting in it sound like a car. And it
        // is the CABIN: the bonnet and the boot sit inside the car's bounding box, and a room derived
        // from that box was the whole 4.1 m car, three times the volume of the 2.4 m cabin.
        var roomEntity = CompositeService.MembersOf(f.World, root).Find(m => f.World.Has<RegionComponent>(m));
        Assert.NotEqual(Arch.Core.Entity.Null, roomEntity);
        var size = f.World.Get<RegionComponent>(roomEntity).RoomSize;
        Assert.InRange(size.Z, 2.2f, 2.6f);
        Assert.InRange(size.Y, 1.0f, 1.3f);
    }

    /// <summary>
    /// Get in, hold W for three seconds: it goes, forwards, and stays on the road. The last half is
    /// the one that matters — a car asking where the ground is used to find its own floor inside the
    /// step height and climb onto it, every tick.
    /// </summary>
    [Fact]
    public void AParkedCarDrivesAwayWithoutClimbingItsOwnFloor()
    {
        var f = new Fixture(_dir);
        int root = f.Composites.Place(f.MapId, "vehicle:i4_economy", new Vector3(20, 0, 20), Quaternion.Identity,
                                      "", out _, out string error);
        Assert.True(root >= 0, error);
        float startY = f.RootTransform(root).Position.Y;
        var driver = f.Player("driver_one", new Vector3(19, 0, 20));
        Assert.True(f.Seats.Enter(driver, root, null, out string message), message);
        Assert.True(f.World.Get<OccupantComponent>(driver.Entity).Controls);

        f.Hold(driver, forward: 1f);
        f.Tick(90);
        var at = f.RootTransform(root).Position;
        Assert.True(at.Z - 20f > 3f, $"it moved {at.Z - 20f:F1} m forward in three seconds of full throttle");
        Assert.True(MathF.Abs(at.Y - startY) < 0.1f, $"it rose from {startY:F2} to {at.Y:F2} — standing on its own floor");
    }

    /// <summary>
    /// A tap on a steering key is a small correction, not a jolt of full lock; holding it tightens
    /// the turn steadily; and letting go straightens up. Taps are how a lot of people steer.
    /// </summary>
    [Fact]
    public void SteeringIsAHandOnTheWheelNotASwitch()
    {
        var f = new Fixture(_dir);
        int root = f.Composites.Place(f.MapId, "vehicle:i4_economy", new Vector3(20, 0, 20), Quaternion.Identity,
                                      "", out _, out string error);
        Assert.True(root >= 0, error);
        var driver = f.Player("driver_one", new Vector3(19, 0, 20));
        Assert.True(f.Seats.Enter(driver, root, null, out string message), message);
        float Steer() => f.World.Get<DriveComponent>(f.Entity(root)).Steer;

        f.Hold(driver, lateral: 1f);
        f.Tick(3);                                   // a tap: about a tenth of a second
        float tap = Steer();
        f.Tick(27);                                  // held for a second in all
        float held = Steer();
        f.Hold(driver);
        f.Tick(20);                                  // let go for two thirds of a second
        float released = Steer();

        Assert.InRange(tap, 0.02f, 0.2f);
        Assert.True(held > 0.9f, $"a second of holding turned the wheel to {held:F2} of lock");
        Assert.True(MathF.Abs(released) < 0.05f, $"let go, the wheel stayed at {released:F2}");
    }

    /// <summary>
    /// At speed, full lock on the keys is not full lock at the wheels: it is as far as the tyres can
    /// hold, and a little more. Otherwise holding a key at seventy is a spin.
    /// </summary>
    [Fact]
    public void FullLockAtSpeedIsWhatTheTyresCanHold()
    {
        var f = new Fixture(_dir);
        int root = f.Composites.Place(f.MapId, "vehicle:i4_economy", new Vector3(20, 0, 20), Quaternion.Identity,
                                      "", out _, out string error);
        Assert.True(root >= 0, error);
        var e = f.Entity(root);
        ref var d = ref f.World.Get<DriveComponent>(e);
        d.Speed = 25f;                               // ninety kilometres an hour
        d.Steer = 1f; d.SteerTarget = 1f; d.Throttle = 0.2f;
        var driver = f.Player("driver_one", new Vector3(19, 0, 20));
        Assert.True(f.Seats.Enter(driver, root, null, out string message), message);
        f.Hold(driver, forward: 0.2f, lateral: 1f);
        float h0 = f.World.Get<DriveComponent>(e).Heading;
        f.Tick(30);
        var after = f.World.Get<DriveComponent>(e);
        // Full lock at 25 m/s would ask the tyres for several times what they have — a demand of 2,
        // the ceiling, every tick. Limited to what they can hold, it sits just over the edge: the
        // tyres squeal a little and the car goes round.
        Assert.InRange(after.TyreDemand, 0.9f, 1.4f);
        Assert.True(MathF.Abs(after.Heading - h0) > 0.2f, "it did not turn");
    }

    /// <summary>You cannot walk through the side of it.</summary>
    [Fact]
    public void AParkedCarIsSolid()
    {
        var f = new Fixture(_dir);
        int root = f.Composites.Place(f.MapId, "vehicle:i4_economy", new Vector3(20, 0, 20), Quaternion.Identity,
                                      "", out _, out string error);
        Assert.True(root >= 0, error);
        f.Tick(1);
        Assert.True(MovementSystem.CheckCollision(f.World, f.Grid, new Vector3(20.8f, 0f, 20f),
                                                  PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
                    "a player standing in the door is not touching the car");
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A map on disk, a world, and the four things that touch it. Deliberately the real
    /// MapManager over real prefab and map files rather than a stub: a composite is only worth
    /// anything if it is made of the same entities the map itself is made of.
    /// </summary>
    private sealed class Fixture
    {
        public readonly MapManager Maps;
        public readonly CompositeService Composites;
        public readonly OccupancyService Seats;
        public readonly OccupancySystem Occupancy = new();
        public readonly SessionManager Sessions = new();
        public readonly string MapId = "default";

        public World World = null!;
        public Dictionary<int, Entity> Lookup = null!;
        public SpatialGrid<Entity> Grid = null!;

        private readonly PrefabRepository _prefabs;
        private int _nextConnection = 1;
        private readonly List<UserSession> _drivers = new();

        public Fixture(string dir)
        {
            string mapDir = Path.Combine(dir, "maps");
            Directory.CreateDirectory(mapDir);
            foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "maps"), "*.json"))
                File.Copy(file, Path.Combine(mapDir, Path.GetFileName(file)), overwrite: true);

            _prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            Maps = new MapManager(new MapRepository(mapDir), _prefabs);
            Maps.Initialize();
            Composites = new CompositeService(Maps, _prefabs, new CompositeRepository(Path.Combine(dir, "composites")));
            Seats = new OccupancyService(Maps);
            Assert.True(Maps.TryGetMap(MapId, out World, out _, out Grid, out Lookup));
        }

        public Entity Entity(int id) => Lookup[id];
        public Transform RootTransform(int rootId) => World.Get<Transform>(Entity(rootId));
        public Seat SeatOf(int rootId, int index) => World.Get<OccupancyComponent>(Entity(rootId)).Seats[index];

        /// <summary>Walls in a heap, grouped free, given two seats and an engine. A car.</summary>
        public int BuildCar(Vector3 where, string owner = "", string preset = "v8_sports")
        {
            int root = Group(where, owner, anchored: false, name: "car");
            Assert.True(Composites.AddSeat(MapId, root, "driver", true, where + new Vector3(-0.4f, 0, 0.5f), 0f,
                                           owner, true, out string error), error);
            Assert.True(Composites.AddSeat(MapId, root, "passenger", false, where + new Vector3(0.4f, 0, 0.5f), 0f,
                                           owner, true, out error), error);
            Assert.True(Composites.MakeDrivable(MapId, root, preset, owner, true, out error), error);
            return root;
        }

        /// <summary>The same walls, fixed down. A house.</summary>
        public int BuildHouse(Vector3 where, string owner = "") => Group(where, owner, anchored: true, name: "house");

        /// <summary>The same walls, free, and nothing to sit in. A body on a trolley.</summary>
        public int BuildShell(Vector3 where, string owner = "cody") => Group(where, owner, anchored: false, name: "shell");

        private int Group(Vector3 where, string owner, bool anchored, string name)
        {
            for (int i = 0; i < 4; i++)
            {
                var at = where + new Vector3((i - 1.5f) * 1.4f, 0f, 0f);
                Assert.NotEqual(Arch.Core.Entity.Null,
                    Maps.SpawnEntity(MapId, w => _prefabs.Spawn(w, "concrete_wall", at, Quaternion.Identity, Vector3.One)));
            }
            int root = Composites.Group(MapId, where, 6f, name, anchored, owner, out int parts);
            Assert.True(root >= 0, "nothing could be grouped");
            Assert.Equal(4, parts);
            return root;
        }

        public UserSession Player(string username, Vector3 at)
        {
            int connection = _nextConnection++;
            var entity = World.Create(
                new PlayerComponent { ConnectionId = connection, Username = username },
                EntityType.Player,
                new Transform { Position = at, Rotation = Quaternion.Identity },
                new Velocity { Linear = Vector3.Zero },
                new MaterialComponent { Material = "Generic" },
                new NameComponent { Name = username },
                new ColliderComponent
                {
                    Shape = ColliderShape.Cylinder,
                    Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight,
                                       PhysicsConstants.PlayerRadius * 2),
                    IsSolid = true,
                });
            Maps.IndexEntity(MapId, entity);

            var session = new UserSession { ConnectionId = connection, Username = username, Entity = entity, CurrentMapId = MapId };
            Sessions.AddSession(connection, session);
            return session;
        }

        /// <summary>What this player is holding down, from now until told otherwise.</summary>
        public void Hold(UserSession session, float forward = 0f, float lateral = 0f)
        {
            session.LastInput = new ClientInputUpdate
            {
                MoveDirection = new Vector3(lateral, 0f, forward),
                DeltaTime = PhysicsConstants.FixedDeltaTime,
            };
            if (!_drivers.Contains(session)) _drivers.Add(session);
        }

        /// <summary>Every client stops sending. Not a disconnect — a silence.</summary>
        public void Silence() => _drivers.Clear();

        /// <summary>
        /// Ticks the server the way the server ticks itself, in the same order and through the same
        /// systems. A driving test that drove the physics directly would prove the physics and
        /// nothing about whether a player can reach it.
        /// </summary>
        public void Tick(int ticks)
        {
            float dt = PhysicsConstants.FixedDeltaTime;
            Assert.True(Maps.TryGetMapData(MapId, out var data));
            for (int i = 0; i < ticks; i++)
            {
                foreach (var session in _drivers)
                {
                    var input = session.LastInput;
                    session.InputQueue.Enqueue(new ClientInputUpdate
                    {
                        SequenceId = ++_sequence,
                        MoveDirection = input.MoveDirection,
                        DeltaTime = dt,
                    });
                }

                Grid.Clear();
                World.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                    (Entity e, ref Transform t, ref ColliderComponent c) => Grid.AddOverlapping(t.Position, c.Size, e, false));

                MovementSystem.Update(World, data.MinBound, data.MaxBound, Grid, Lookup, Sessions, Maps, dt);
                DrivingSystem.Update(World, Grid, data.MinBound, data.MaxBound, dt);
                ParentSystem.Update(World, Lookup);
                Occupancy.Update(World, Lookup);
            }
        }

        private long _sequence;

        /// <summary>One car of a given profile, two seconds of full throttle, and how fast it got.</summary>
        public float SpeedAfterTwoSeconds(string preset, Vector3 where, string username)
        {
            int root = BuildCar(where, owner: username, preset: preset);
            var session = Player(username, where + new Vector3(1, 0, 0));
            Assert.True(Seats.Enter(session, root, null, out string message), message);
            Hold(session, forward: 1f);
            Tick(60);
            float speed = World.Get<DriveComponent>(Entity(root)).Speed;
            Hold(session, forward: 0f);
            return speed;
        }
    }
}
