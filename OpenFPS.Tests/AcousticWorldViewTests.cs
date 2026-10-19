using System.Numerics;
using System.Text;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Client.Core;
using OpenFPS.Materials;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// The sound's world input (IAcousticWorld, docs/SOUND_LIBRARY_BOUNDARY.md 7.3 and stage 6's risk): the acoustic
/// worker runs on its own thread and holds the view it was given, so a view must never change once handed out. The
/// client's snapshot is that view; these hold it to that against a world that moves on (a door swung, a bus driven
/// off, a wall put up, the weather changed), and hold the worker to answering from the view it holds.
/// </summary>
public class AcousticWorldViewTests
{
    private const int WallId = 9001, DoorId = 9002, BusId = 9003, NewWallId = 9004, StairId = 9005, PlaceId = 9006, RegionId = 9007;
    private static readonly Vector3 Ear = new(0f, 1.6f, 0f);
    private static readonly Vector3 Source = new(0f, 1f, -30f);

    private static Transform At(Vector3 p, Quaternion? r = null) => new() { Position = p, Rotation = r ?? Quaternion.Identity, Scale = Vector3.One };

    private static ClientWorldState World()
    {
        AcousticRegistry.Initialize();
        var w = new ClientWorldState();
        w.Clear(new Vector3(400, 100, 400));
        // A brick wall to the east, off the line to the source.
        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = WallId, Type = EntityType.StaticObject, Transform = At(new Vector3(20f, 5f, -10f)),
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10f, 10f, 0.35f), IsSolid = true },
            Material = new MaterialComponent { Material = "Brick" },
        });
        // A door leaf in its doorway, joining the outside to itself (as the server gives every door a portal).
        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = DoorId, Type = EntityType.StaticObject, Transform = At(new Vector3(-20f, 1f, -10f)),
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.9f, 2f, 0.05f), IsSolid = true },
            Material = new MaterialComponent { Material = "Wood" },
            Portal = new PortalComponent { RegionAId = RegionId, RegionBId = -1, ApertureSize = 1f },
        });
        // The room on one side of the door.
        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = RegionId, Type = EntityType.StaticObject, Transform = At(new Vector3(-20f, 1f, -6f)),
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(8f, 3f, 8f) },
            Region = new RegionComponent { FriendlyName = "Hall", IsIndoor = true, RoomSize = new Vector3(8f, 3f, 8f) },
        });
        // A bus off to the west.
        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = BusId, Type = EntityType.NPC, Moves = true, Team = "north", Transform = At(new Vector3(-40f, 0f, 0f)),
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(2.5f, 3.2f, 12f), IsSolid = true },
            Material = new MaterialComponent { Material = "Metal" },
        });
        // A stair end and a named place: fixed, nothing can bump into them, so markers of their own.
        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = StairId, Type = EntityType.StaticObject, Transform = At(new Vector3(2f, 1f, -4f)),
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.2f, 0.2f, 0.2f), IsSolid = false },
            Identity = new IdentityComponent { Name = "Stairs up, 12 steps, to floor 1", BeaconCategory = "stairs" },
        });
        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = PlaceId, Type = EntityType.StaticObject, Transform = At(new Vector3(-4f, 1f, -4f)),
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(3f, 3f, 3f), IsSolid = false },
            Identity = new IdentityComponent { Name = "Landing", PrefabId = "named_place" },
        });
        return w;
    }

    private static void Move(ClientWorldState w, int id, Vector3 position, Quaternion rotation)
        => w.SyncState(new[] { new EntityState { EntityId = id, Transform = QuantizedTransform.FromTransform(At(position, rotation)) } });

    /// <summary>Everything the view answers, written down: what the sound reads of the world.</summary>
    private static string Read(IAcousticWorld view)
    {
        var sb = new StringBuilder();
        sb.Append($"air {view.Air} version {view.GeometryVersion} tiles {view.TileMetres} rooms {view.Rooms?.GetHashCode()} bodies {view.BodyCount}\n");
        var all = new List<AcousticBody>();
        view.AllBodies(all);
        foreach (var b in all) sb.Append($"body {b.Id} {b.Name} {b.Prefab} {b.Beacon} {b.Team} {b.Kind} {b.Named} {b.Announce} {b.HasWheels} {b.WheelSurface} {b.WheelWaterMm} {b.SynthRunning} {b.Geometry} {b.RegionName} {b.RegionIndoor} {b.RegionAId} {b.RegionBId} {b.Position} {b.Rotation} {b.Size} {b.Material} {b.Solid} {b.DoorLeaf}\n");
        for (int i = 0; i < view.MovingCount; i++) sb.Append($"moving {view.MovingAt(i).Id} {view.MovingAt(i).Position}\n");
        var leaves = new List<DoorLeaf>();
        view.DoorLeaves(leaves);
        foreach (var l in leaves) sb.Append($"leaf {l}\n");
        var markers = new List<AcousticBody>();
        view.Markers(markers);
        foreach (var m in markers) sb.Append($"marker {m.Id} {m.Name} {m.Prefab} {m.Beacon}\n");
        var near = new List<AcousticBody>();
        view.Near(Ear, 60f, staticOnly: false, near);
        foreach (var b in near) sb.Append($"near {b.Id}\n");
        foreach (var s in view.TerrainSolids()) sb.Append($"terrain {s.Owner}\n");
        sb.Append($"region at the ear {AcousticRays.RegionAt(view, Ear)}\n");
        return sb.ToString();
    }

    /// <summary>
    /// A view taken before the world moved on answers as it did: the door swung, the bus driven across, a wall
    /// put up and the air warmed after it was taken change the next view and not this one.
    /// </summary>
    [Fact]
    public void AViewHandedOutNeverChangesAsTheWorldMovesOn()
    {
        var w = World();
        IAcousticWorld before = w.GetSnapshot();
        string was = Read(before);

        Move(w, DoorId, new Vector3(-20.4f, 1f, -9.6f), Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f));
        Move(w, BusId, new Vector3(0f, 0f, -15f), Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f));
        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = NewWallId, Type = EntityType.StaticObject, Transform = At(new Vector3(0f, 5f, -20f)),
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(30f, 10f, 0.35f), IsSolid = true },
            Material = new MaterialComponent { Material = "Concrete" },
        });
        w.UpdateAtmosphere(new WorldStateUpdate { Temperature = 31f, Humidity = 0.2f, AirPressure = 990f, AirAbsorptionMultiplier = 1.5f });
        IAcousticWorld after = w.GetSnapshot();

        Assert.Equal(was, Read(before));
        Assert.NotEqual(was, Read(after));
        Assert.NotEqual(before.Air, after.Air);
        Assert.Equal(before.BodyCount + 1, after.BodyCount);
    }

    /// <summary>The markers are the fixed unsolid named things, answered with the names, prefabs and beacon
    /// categories the aids read of them; a solid wall and a moving bus are not markers.</summary>
    [Fact]
    public void TheMarkersAreNamedWithTheirPrefabsAndCategories()
    {
        IAcousticWorld view = World().GetSnapshot();
        var markers = new List<AcousticBody>();
        view.Markers(markers);
        Assert.Equal(new[] { StairId, PlaceId }, markers.Select(m => m.Id).OrderBy(i => i));
        Assert.Contains(markers, m => m.Id == StairId && m.Name == "Stairs up, 12 steps, to floor 1"
                                      && m.Beacon == "stairs" && m.Kind == BodyKind.StaticObject);
        Assert.Contains(markers, m => m.Id == PlaceId && m.Name == "Landing" && m.Prefab == "named_place");
        Assert.DoesNotContain(markers, m => m.Id == WallId || m.Id == BusId);
        Assert.True(view.TryGetBody(BusId, out var bus) && bus.Kind == BodyKind.Npc);
        Assert.False(bus.HasWheels);
        Assert.Equal("north", bus.Team);
        Assert.True(view.TryGetBody(WallId, out var wall) && wall.Geometry == BodyGeometry.Static);
        Assert.True(view.TryGetBody(DoorId, out var door) && door.Geometry == BodyGeometry.Mover
                    && door.RegionAId == RegionId && door.RegionBId == -1);
        Assert.True(view.TryGetBody(RegionId, out var region) && region.RegionName == "Hall" && region.RegionIndoor);
        var wheels = new OpenFPS.Physics.WheelState[4];
        wheels[0] = OpenFPS.Physics.WheelState.Encode(4000f, 0f, 0f, 0f, 3, 0.2f, waterMm: 2f);
        wheels[3] = OpenFPS.Physics.WheelState.Encode(4000f, 0f, 0f, 0f, 7, 0.2f, waterMm: 9f);
        var wet = EntityAcoustics.BodyOf(new EntitySnapshot { Id = 9007, Wheels = wheels });
        Assert.True(wet.HasWheels);
        // The worst wheel is the one with the most water under it, and its surface comes with it.
        Assert.Equal(7, wet.WheelSurface);
        Assert.Equal(9f, wet.WheelWaterMm, 2);
        Assert.False(EntityAcoustics.BodyOf(new EntitySnapshot { Id = 9008 }).HasWheels);
    }

    /// <summary>
    /// The worker answers from the view it holds, whatever the world has done since: a wall put up between the
    /// ear and the source after the view was handed over changes nothing until the next view is.
    /// </summary>
    [Fact]
    public void TheWorkerAnswersFromTheViewItHolds()
    {
        var w = World();
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics()) { Manual = true };
        var request = new AcousticRequest { EntityId = 7, ListenerPos = Ear, SourcePos = Source, SourceRadius = AudioEmission.MinOcclusionRadius };
        worker.UpdateWorld(w.GetSnapshot());
        worker.EnqueueRequest(request);
        worker.StepForTest();
        Assert.True(worker.TryGetResult(7, out var first));
        var clear = first.First(p => !p.IsReflection);

        w.RegisterDefinition(new EntityDefinition
        {
            EntityId = NewWallId, Type = EntityType.StaticObject, Transform = At(new Vector3(0f, 5f, -15f)),
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(60f, 10f, 0.35f), IsSolid = true },
            Material = new MaterialComponent { Material = "Brick" },
        });
        // The game builds the next view every frame, whether or not the worker has it yet.
        var next = w.GetSnapshot();
        worker.EnqueueRequest(request);
        worker.StepForTest();
        Assert.True(worker.TryGetResult(7, out var held));
        var still = held.First(p => !p.IsReflection);
        Assert.Equal(clear.EqMid, still.EqMid);
        Assert.Equal(clear.Occlusion, still.Occlusion);

        worker.UpdateWorld(next);
        worker.EnqueueRequest(request);
        worker.StepForTest();
        Assert.True(worker.TryGetResult(7, out var walled));
        var behind = walled.First(p => !p.IsReflection);
        Assert.True(behind.EqMid < clear.EqMid * 0.5f, $"the wall took {clear.EqMid:F3} to {behind.EqMid:F3}");
    }
}
