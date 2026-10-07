using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using Microsoft.EntityFrameworkCore;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

/// <summary>
/// Entering a composite: a seat carries its occupant, turning the vehicle turns them, a driver's inputs
/// reach the wheels, the car drives from its profile and not from constants in the driving code, and
/// ownership gates only what it should. A house and a car are the same structure; a car has a seat with
/// <see cref="Seat.Controls"/> and something willing to move the root.
/// </summary>
public class OccupancyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"openfps-occupancy-{Guid.NewGuid():N}");
    private readonly Xunit.Abstractions.ITestOutputHelper _o;
    public OccupancyTests(Xunit.Abstractions.ITestOutputHelper o) => _o = o;

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

        // With no seat named the first allowed one wins, and the driver's seat is declared first.
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

        // The passenger seat is still free, and an unnamed request takes it.
        Assert.True(f.Seats.Enter(second, root, null, out string c), c);
        Assert.Equal(1, f.World.Get<OccupantComponent>(second.Entity).SeatIndex);
        Assert.False(f.World.Get<OccupantComponent>(second.Entity).Controls);
    }

    /// <summary>Getting out puts you back where you got in, ground you fitted on a moment ago, not in
    /// the engine bay.</summary>
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

    /// <summary>If it has driven off since, you step out beside where it is now.</summary>
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
        // Back while moving forward brakes, and once stopped it is reverse: let go at walking pace.
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

    /// <summary>Turn the car and the driver turns with it: the world is heard relative to the
    /// listener's facing, so a head left pointing north would hear everything swing round each corner.</summary>
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

    /// <summary>You step off facing the way the vehicle was going, whichever way your head was turned
    /// in the seat (Cody: "when I get out I should be facing that direction").</summary>
    [Fact]
    public void GettingOutFacesTheWayItWasGoing()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("rider", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out _));
        float heading = 2.1f;
        f.World.Get<Transform>(f.Entity(root)).Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
        f.World.Get<PlayerComponent>(session.Entity).Yaw = -1.3f;           // looking out of the side
        Assert.True(f.Seats.Exit(session, out string message), message);
        Assert.Equal(heading, f.World.Get<PlayerComponent>(session.Entity).Yaw, 3);
        MathHelper.ToYawPitch(f.World.Get<Transform>(session.Entity).Rotation, out float yaw, out _);
        Assert.Equal(heading, yaw, 3);
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

    /// <summary>How a thing drives comes from its profile, not the driving code: the same shape with a
    /// lorry's profile (fourteen tonnes, a barn door of drag) accelerates like a lorry.</summary>
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

    /// <summary>A driver whose client has gone quiet coasts to a stop: not instantly, so ordinary packet
    /// loss does not stutter the throttle, but it does not drive on for ever.</summary>
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

        // The passenger seat of somebody else's car is allowed.
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

    /// <summary>Seats and an engine are saved with the template and come back with every
    /// instance.</summary>
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

        // And the instance drives without being made drivable again.
        var session = f.Player("cody", new Vector3(-29, 0, -30));
        Assert.True(f.Seats.Enter(session, second, null, out string message), message);
        f.StartEngine(second);                       // placed again, it is parked: key first
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

    /// <summary>An engine is refused on something nobody can drive: nothing could ever ask it to
    /// move.</summary>
    [Fact]
    public void SomethingWithNoDrivingSeatIsNotDrivable()
    {
        var f = new Fixture(_dir);
        int root = f.BuildShell(new Vector3(-20, 0, 40));

        Assert.False(f.Composites.MakeDrivable(f.MapId, root, "v8_sports", "cody", true, out string error));
        Assert.Contains("nothing in it drives", error);

        // Passenger seats are not enough: somebody has to be able to steer.
        Assert.True(f.Composites.AddSeat(f.MapId, root, "bench", false, new Vector3(-20, 0, 40), 0f,
                                         "cody", true, out error), error);
        Assert.False(f.Composites.MakeDrivable(f.MapId, root, "v8_sports", "cody", true, out error));

        Assert.True(f.Composites.AddSeat(f.MapId, root, "driver", true, new Vector3(-20, 0, 40), 0f,
                                         "cody", true, out error), error);
        Assert.True(f.Composites.MakeDrivable(f.MapId, root, "v8_sports", "cody", true, out error), error);
    }

    /// <summary>Every refusal names what is free, so a player need not walk round a car trying
    /// doors.</summary>
    [Fact]
    public void BeingTurnedAwayFromASeatTellsYouWhichOnesAreFree()
    {
        var f = new Fixture(_dir);
        int root = f.BuildCar(new Vector3(20, 0, 20), owner: "cody");
        var owner = f.Player("cody", new Vector3(21, 0, 20));
        var friend = f.Player("mate", new Vector3(21, 0, 21));

        // Not theirs to drive, and empty.
        Assert.False(f.Seats.Enter(friend, root, "driver", out string notYours));
        Assert.Contains("cannot drive it", notYours);
        Assert.Contains("passenger", notYours);

        // Somebody already in it: that is the fact named.
        Assert.True(f.Seats.Enter(owner, root, null, out _));
        Assert.False(f.Seats.Enter(friend, root, "driver", out string taken));
        Assert.Contains("taken", taken);
        Assert.Contains("passenger", taken);
    }

    /// <summary>Owning permits and never compels: an owner asking for the passenger seat gets the
    /// passenger seat.</summary>
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

    /// <summary>"vehicle:i4_economy" is built from its profile with no file: a closed cabin that grows
    /// a room, four seats with the driver's first, and an engine.</summary>
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

        // The room is the 2.4 m cabin, not the car's bounding box: a room from the box was the whole
        // 4.1 m car, three times the cabin's volume.
        var roomEntity = CompositeService.MembersOf(f.World, root).Find(m => f.World.Has<RegionComponent>(m));
        Assert.NotEqual(Arch.Core.Entity.Null, roomEntity);
        var size = f.World.Get<RegionComponent>(roomEntity).RoomSize;
        Assert.InRange(size.Z, 2.2f, 2.6f);
        Assert.InRange(size.Y, 1.0f, 1.3f);
    }

    /// <summary>Get in, hold W for three seconds: it goes forwards and stays on the road. A car's ground
    /// probe once found its own floor within the step height and climbed onto it every tick.</summary>
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
        f.StartEngine(root);

        f.Hold(driver, forward: 1f);
        f.Tick(90);
        var at = f.RootTransform(root).Position;
        Assert.True(at.Z - 20f > 3f, $"it moved {at.Z - 20f:F1} m forward in three seconds of full throttle");
        Assert.True(MathF.Abs(at.Y - startY) < 0.1f, $"it rose from {startY:F2} to {at.Y:F2} — standing on its own floor");
    }

    /// <summary>A tap on a steering key is a small correction, not full lock; holding tightens the turn
    /// steadily; letting go straightens up.</summary>
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

        Assert.InRange(tap, 0.02f, 0.25f);
        Assert.True(held > 0.9f, $"a second of holding turned the wheel to {held:F2} of lock");
        Assert.True(MathF.Abs(released) < 0.05f, $"let go, the wheel stayed at {released:F2}");
    }

    /// <summary>At speed, full lock on the keys is as far as the tyres can hold and a little more, not
    /// full lock at the wheels.</summary>
    [Fact]
    public void FullLockAtSpeedIsWhatTheTyresCanHold()
    {
        var f = new Fixture(_dir);
        int root = f.Composites.Place(f.MapId, "vehicle:i4_economy", new Vector3(20, 0, 20), Quaternion.Identity,
                                      "", out _, out string error);
        Assert.True(root >= 0, error);
        var e = f.Entity(root);
        var driver = f.Player("driver_one", new Vector3(19, 0, 20));
        Assert.True(f.Seats.Enter(driver, root, null, out string message), message);
        ref var d = ref f.World.Get<DriveComponent>(e);
        d.Speed = 25f;                               // ninety kilometres an hour
        d.Steer = 1f; d.SteerTarget = 1f; d.Throttle = 0.2f;
        f.Hold(driver, forward: 0.2f, lateral: 1f);
        float h0 = f.World.Get<DriveComponent>(e).Heading;
        f.Tick(30);
        var after = f.World.Get<DriveComponent>(e);
        // Full lock at 25 m/s would be a demand of 2 (the ceiling) every tick; limited, it sits just
        // over the edge: the tyres squeal a little and the car goes round.
        Assert.InRange(after.TyreDemand, 0.9f, 1.4f);
        Assert.True(MathF.Abs(after.Heading - h0) > 0.2f, "it did not turn");
    }

    /// <summary>A parked car's engine is off: W does nothing until the key is turned and the starter has
    /// had its second, and the voice is told.</summary>
    [Fact]
    public void AParkedCarNeedsTheKey()
    {
        var f = new Fixture(_dir);
        int root = f.Composites.Place(f.MapId, "vehicle:i4_economy", new Vector3(20, 0, 20), Quaternion.Identity,
                                      "", out _, out string error);
        Assert.True(root >= 0, error);
        var e = f.Entity(root);
        Assert.False(f.World.Get<SoundEmitterComponent>(e).SynthRunning, "a parked car was left running");
        var driver = f.Player("driver_one", new Vector3(19, 0, 20));
        Assert.True(f.Seats.Enter(driver, root, null, out string message), message);

        f.Hold(driver, forward: 1f);
        f.Tick(60);
        Assert.True(f.RootTransform(root).Position.Z - 20f < 0.05f, "it drove with the engine off");

        int resent = -1;
        DrivingSystem.SetIgnition(f.World, e, true, id => resent = id);
        Assert.Equal(root, resent);
        Assert.True(f.World.Get<SoundEmitterComponent>(e).SynthRunning);
        f.Tick(15);                                  // half a second: still cranking
        Assert.True(f.RootTransform(root).Position.Z - 20f < 0.05f, "it pulled away on the starter");
        f.Tick(90);
        Assert.True(f.RootTransform(root).Position.Z - 20f > 2f, "the engine started and nothing happened");
    }

    /// <summary>Key and W drive a garage car, parked nose out between the street-side piers, onto Main
    /// Street without touching anything (Cody could not get out of the garage); a failure names what it
    /// hit.</summary>
    [Fact]
    public void AParkedCarDrivesStraightOutOfTheGarage()
    {
        var f = new Fixture(_dir, "city");
        f.Composites.PlaceRecorded(f.Maps);
        int root = f.Composites.NearestRoot(f.MapId, new Vector3(-16f, 0.25f, 29f), 3f);
        Assert.True(root >= 0, "no car in the first bay");
        var driver = f.Player("driver_one", new Vector3(-16f, 0.3f, 31.2f));
        Assert.True(f.Seats.Enter(driver, root, null, out string message), message);
        f.StartEngine(root);
        f.Hold(driver, forward: 1f);
        float lastX = f.RootTransform(root).Position.X;
        for (int second = 0; second < 5; second++)
        {
            f.Tick(30);
            float x = f.RootTransform(root).Position.X;
            _o.WriteLine($"t={second + 1}s x={x:F1} speed={f.World.Get<DriveComponent>(f.Entity(root)).Speed:F1}");
            lastX = x;
        }
        Assert.True(lastX > -6f, $"five seconds of full throttle and the car is at x={lastX:F1}, still in or at the garage");
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

    // ── Leaving ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Disconnecting in a car leaves what you carry on the map for somebody else. The things
    /// used to keep a HeldComponent on the destroyed body, so nobody could pick them up, and a
    /// ParentComponent on an id Arch would reuse.</summary>
    [Fact]
    public void DisconnectingPutsDownWhatYouCarryAndGetsYouOut()
    {
        var f = new Fixture(_dir);
        var hands = new HandsService(f.Maps);
        var server = new OpenFPS.Server.GameServer(new NoUsers());
        server.Attach(f.Maps, f.Sessions, f.Seats, hands);

        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("leaver", new Vector3(21, 0, 20));
        var torch = Item(f, "Torch", new Vector3(21.3f, 0, 20));
        var crowbar = Item(f, "Crowbar", new Vector3(21.4f, 0, 20));
        Assert.True(hands.Take(session, "torch", out string m), m);
        Assert.True(hands.Stow(session, "", out m), m);
        Assert.True(hands.Take(session, "crowbar", out m), m);
        Assert.True(f.Seats.Enter(session, root, null, out m), m);
        var body = session.Entity;

        Assert.True(f.Sessions.TryRemoveSession(session.ConnectionId, out _));
        server.DespawnSession(session);
        server.DrainCommandBuffer();

        Assert.False(f.World.IsAlive(body));
        Assert.Equal(Arch.Core.Entity.Null, session.Entity);
        foreach (var item in new[] { torch, crowbar })
        {
            Assert.True(f.World.IsAlive(item));
            Assert.False(f.World.Has<HeldComponent>(item), $"{item.Id} is still held by a body that is gone");
            Assert.False(f.World.Has<ParentComponent>(item), $"{item.Id} still rides on a body that is gone");
        }

        // And somebody else can pick them up.
        var other = f.Player("finder", f.World.Get<Transform>(crowbar).Position);
        Assert.True(hands.Take(other, "crowbar", out m), m);
        Assert.True(hands.Take(other, "torch", out m), m);
    }

    /// <summary>Logging out in the passenger seat of a moving car is not refused, and the next login puts
    /// you beside the car.</summary>
    [Fact]
    public void LeavingTheWorldFromAMovingCarKeepsAPlaceBesideIt()
    {
        var f = new Fixture(_dir);
        var repo = new SqliteUserRepository(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_dir, "accounts.db")}").Options, workFactor: 4);
        Assert.True(repo.AddUser("passenger", "correct horse", UserRole.Player));
        var hands = new HandsService(f.Maps);
        var server = new OpenFPS.Server.GameServer(repo);
        server.Attach(f.Maps, f.Sessions, f.Seats, hands);

        int root = f.BuildCar(new Vector3(20, 0, 20));
        var session = f.Player("passenger", new Vector3(21, 0, 20));
        Assert.True(f.Seats.Enter(session, root, null, out string m), m);
        f.World.Get<DriveComponent>(f.Entity(root)).Speed = 25f;
        var car = f.RootTransform(root).Position;

        Assert.True(f.Sessions.TryRemoveSession(session.ConnectionId, out _));
        server.DespawnSession(session);
        server.DrainCommandBuffer();

        var state = PlayerState.Parse(repo.GetUser("passenger")!.PlayerState);
        var place = Assert.IsType<SavedPlace>(state.PlaceOn(f.MapId));
        var at = new Vector3(place.X, place.Y, place.Z);
        float toCar = Vector3.Distance(Flat(at), Flat(car));
        Assert.True(toCar < 8f, $"kept a place {toCar:F1} m from the car");
        Assert.False(MovementSystem.CheckCollision(f.World, f.Grid, at, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight),
                     "the place kept is inside the car or something else");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static Arch.Core.Entity Item(Fixture f, string name, Vector3 at)
    {
        var e = f.Maps.SpawnEntity(f.MapId, w => w.Create(
            new Transform { Position = at, Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.2f, 0.1f, 0.5f), IsSolid = false },
            new MaterialComponent { Material = "Metal", Variant = "0" },
            new IdentityComponent { Name = name },
            new ItemComponent { MassKg = 1f, Hands = 1 },
            EntityType.StaticObject));
        Assert.NotEqual(Arch.Core.Entity.Null, e);
        return e;
    }

    private sealed class NoUsers : IUserRepository
    {
        public UserData? GetUser(string username) => null;
        public bool AddUser(string username, string password, UserRole role) => true;
        public bool VerifyPassword(string username, string password) => false;
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A map on disk, a world, and the four services that touch it: the real MapManager over
    /// real prefab and map files, so a composite is made of the map's own entities.</summary>
    private sealed class Fixture
    {
        public readonly MapManager Maps;
        public readonly CompositeService Composites;
        public readonly OccupancyService Seats;
        public readonly OccupancySystem Occupancy = new();
        public readonly SessionManager Sessions = new();
        public readonly string MapId;

        public World World = null!;
        public Dictionary<int, Entity> Lookup = null!;
        public SpatialGrid<Entity> Grid = null!;

        private readonly PrefabRepository _prefabs;
        private int _nextConnection = 1;
        private readonly List<UserSession> _drivers = new();

        public Fixture(string dir, string mapId = "default")
        {
            MapId = mapId;
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
            StartEngine(root);
            return root;
        }

        /// <summary>The key turned and the engine caught. A drivable thing is parked with it off.</summary>
        public void StartEngine(int root)
        {
            ref var d = ref World.Get<DriveComponent>(Entity(root));
            d.EngineOn = true;
            d.EngineOnFor = 10f;
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

        /// <summary>Ticks the server's own systems in the server's order, so a test proves a player can
        /// reach the physics, not only the physics.</summary>
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
