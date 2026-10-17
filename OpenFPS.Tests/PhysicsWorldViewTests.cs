using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;

namespace OpenFPS.Tests;

/// <summary>
/// The one view of the world both sides fill (physics stage 3, docs/PHYSICS_BOUNDARY.md 17): the server's from
/// its world and grid, a client's from its snapshot, and the ground probe, the gather and the memo asking it the
/// same question. The three divergences of section 3.7, each closed.
/// </summary>
public class PhysicsWorldViewTests
{
    private const int Own = 50;

    /// <summary>A floor, and boxes that move (parked cars), as the server holds them and as a client does.</summary>
    private sealed class Both : IDisposable
    {
        public readonly Arch.Core.World World = Arch.Core.World.Create();
        public readonly SpatialGrid<Arch.Core.Entity> Grid = new(10f);
        public readonly WorldSnapshot Snap = new() { StaticGrid = new SpatialGrid<int>(10f) };
        public readonly int Floor;
        private readonly List<(Arch.Core.Entity E, Vector3 Size)> _moving = new();

        public Both()
        {
            var floor = World.Create(new Transform { Position = new Vector3(0, -0.5f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
                new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(100, 1, 100), IsSolid = true },
                new MaterialComponent { Material = "Asphalt" });
            Floor = floor.Id;
            Grid.AddOverlapping(new Vector3(0, -0.5f, 0), new Vector3(100, 1, 100), floor, isStatic: true);
            Add(floor.Id, new Vector3(0, -0.5f, 0), new Vector3(100, 1, 100), "Asphalt", moves: false);
            Snap.StaticGrid!.AddOverlapping(new Vector3(0, -0.5f, 0), new Vector3(100, 1, 100), floor.Id, isStatic: true);
        }

        /// <summary>A parked car, filed as the server's tick files what moves.</summary>
        public Arch.Core.Entity Car(Vector3 centre, Vector3? size = null)
        {
            var s = size ?? new Vector3(1.8f, 1.5f, 4.6f);
            var e = World.Create(new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One }, new Velocity(),
                new ColliderComponent { Shape = ColliderShape.Box, Size = s, IsSolid = true }, new MaterialComponent { Material = "Metal" });
            _moving.Add((e, s));
            Refile();
            Add(e.Id, centre, s, "Metal", moves: true);
            return e;
        }

        /// <summary>A moving thing moved, on both sides.</summary>
        public void Move(Arch.Core.Entity e, Vector3 centre)
        {
            World.Get<Transform>(e).Position = centre;
            Refile();
            var s = Snap.Entities[e.Id];
            s.Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One };
            Snap.Entities[e.Id] = s;
            int i = Snap.DynamicEntities.FindIndex(d => d.Id == e.Id);
            Snap.DynamicEntities[i] = s;
        }

        /// <summary>The grid's moving half filed again, newest first: not the order of their ids.</summary>
        private void Refile()
        {
            Grid.Clear();
            for (int i = _moving.Count - 1; i >= 0; i--)
                Grid.AddOverlapping(World.Get<Transform>(_moving[i].E).Position, _moving[i].Size, _moving[i].E, false);
        }

        private void Add(int id, Vector3 centre, Vector3 size, string material, bool moves)
        {
            var snap = new EntitySnapshot
            {
                Id = id,
                Definition = new EntityDefinition
                {
                    EntityId = id, Type = moves ? EntityType.NPC : EntityType.StaticObject, Moves = moves,
                    Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
                    Material = new MaterialComponent { Material = material },
                },
                Transform = new Transform { Position = centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            Snap.Entities[id] = snap;
            // The client's list in the order it heard of them, the first first: not the grid's.
            if (moves) Snap.DynamicEntities.Add(snap);
        }

        public void Dispose() => Arch.Core.World.Destroy(World);
    }

    /// <summary>
    /// Divergence 1: on a parked car's roof the server stood the body on the roof and the client, which counted
    /// nothing that moves, on the road 1.5 m below; each round trip pulled the client back up and it fell again,
    /// a centimetre a tick (city_parked_car, 39 mm). Both count what moves now.
    /// </summary>
    [Fact]
    public void BothSidesStandOnAParkedCarsRoof()
    {
        using var w = new Both();
        w.Car(new Vector3(0, 0.75f, 0));
        var roof = new Vector3(0.2f, 1.5f, 1f);
        float server = PhysicsUtils.GetGroundHeight(w.World, w.Grid, roof, out string serverMaterial);
        float client = PhysicsUtils.GetGroundHeight(w.Snap, roof, Own, out string clientMaterial);
        Assert.Equal(1.5f, server, 4);
        Assert.Equal(server, client);
        Assert.Equal("Metal", serverMaterial);
        Assert.Equal(serverMaterial, clientMaterial);
        // Off the car, the road, on both.
        var road = new Vector3(5f, 0f, 0f);
        Assert.Equal(0f, PhysicsUtils.GetGroundHeight(w.World, w.Grid, road, out _), 4);
        Assert.Equal(PhysicsUtils.GetGroundHeight(w.World, w.Grid, road, out _), PhysicsUtils.GetGroundHeight(w.Snap, road, Own, out _));
    }

    /// <summary>
    /// Divergence 3: the server gathered the boxes from its grid's cells in the order they were filed, a client
    /// from its snapshot in the order it heard of them, by another rule of near. One gather now, by id.
    /// </summary>
    [Fact]
    public void BothSidesGatherTheSameBoxesInTheSameOrder()
    {
        using var w = new Both();
        var cars = new[] { w.Car(new Vector3(2f, 0.75f, 0f)), w.Car(new Vector3(-2.5f, 0.75f, 1f)), w.Car(new Vector3(0f, 0.75f, 5f)), w.Car(new Vector3(30f, 0.75f, 0f)) };
        var at = Vector3.Zero;
        var server = new List<PhysicsBody>();
        var client = new List<PhysicsBody>();
        OnFoot.Gather(ServerPhysicsView.Of(w.World, w.Grid), Own, at, server);
        OnFoot.Gather(SnapshotPhysicsView.Of(w.Snap), Own, at, client);
        Assert.Equal(server, client);
        // The floor and the three cars within reach, by id; the one 30 m off is not.
        Assert.Equal(cars.Take(3).Select(c => c.Id).Append(w.Floor).OrderBy(i => i), server.Select(b => b.Id));
    }

    /// <summary>
    /// Divergence 2: the server's memo handed back a floor for a point up to 5 cm away and 150 ms old, by the wall
    /// clock, so a car pushed under a body standing still was not under it for the server until then. It remembers
    /// the triangle world's floor for the same point now, and asks what moves every time.
    /// </summary>
    [Fact]
    public void TheMemoAnswersWhatAFreshProbeDoes()
    {
        using var w = new Both();
        var trailer = w.Car(new Vector3(10f, 0.25f, 0f), new Vector3(2f, 0.5f, 4f));   // a low trailer, off to one side
        var feet = new Vector3(0.1f, 0.3f, 0.2f);
        var memo = new GroundProbeMemo();
        Assert.Equal(0f, PhysicsUtils.GetGroundHeight(w.World, w.Grid, feet, Own, ref memo, out _), 4);
        w.Move(trailer, new Vector3(0f, 0.25f, 0f));                                       // pushed under the body
        float remembered = PhysicsUtils.GetGroundHeight(w.World, w.Grid, feet, Own, ref memo, out string material);
        Assert.Equal(1, memo.Hits);
        Assert.Equal(0.5f, remembered, 4);
        Assert.Equal("Metal", material);
        Assert.Equal(PhysicsUtils.GetGroundHeight(w.World, w.Grid, feet, out _), remembered);
        Assert.Equal(remembered, PhysicsUtils.GetGroundHeight(w.Snap, feet, Own, out _));
    }

    /// <summary>A body walked into a car stops against it on both sides, and the contact names the car.</summary>
    [Fact]
    public void AStepIntoACarIsTheSameContactOnBothSides()
    {
        using var w = new Both();
        var car = w.Car(new Vector3(0f, 0.75f, 1.5f));
        var ctx = new SharedMovementEngine.MovementContext
        {
            Position = new Vector3(0f, 0f, -1.4f), InputDirection = Vector3.UnitZ, DeltaTime = PhysicsConstants.FixedDeltaTime,
            GroundHeight = 0f, Gravity = Earth.StandardGravity, JumpForce = PhysicsConstants.JumpPower, Speed = 3f,
            PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
            MapMin = new Vector3(-50, -10, -50), MapMax = new Vector3(50, 50, 50),
        };
        OnFoot.Result server = default;
        ContactEvent? contact = null;
        for (int i = 0; i < 15 && contact == null; i++)
        {
            server = OnFoot.Step(ServerPhysicsView.Of(w.World, w.Grid), Own, ctx, new OnFoot.Scratch());
            var client = OnFoot.Step(SnapshotPhysicsView.Of(w.Snap), Own, ctx, new OnFoot.Scratch());
            Assert.Equal(server, client);
            ctx.Position = server.Position;
            ctx.Velocity = server.Velocity;
            contact = OnFoot.ContactOf(Own, PhysicsConstants.PersonMaterial, server);
        }
        Assert.NotNull(contact);
        Assert.Equal(car.Id, contact!.Value.Other);
        Assert.Equal("Metal", contact.Value.OtherSurface);
        Assert.True(contact.Value.Normal.Z < -0.9f, $"the way out of the car is back toward the body, {contact.Value.Normal}");
        Assert.Equal(3f, contact.Value.ClosingSpeed, 3);
        Assert.True(server.Position.Z < 1.5f - 2.3f, $"the body stopped short of the car, at z {server.Position.Z}");
    }
}
