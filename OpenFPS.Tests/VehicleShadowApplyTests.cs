using System;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// VehicleShadow.Apply on the world as the client builds it (ClientWorldState), rather than a
/// hand-filled snapshot: which bodies the snapshot offers it as barriers, that a body that moves takes
/// its shadow with it, and that the loss is laid on top of whatever the path already carried.
/// The Maekawa maths and the box geometry are VehicleShadowMutationTests'.
///
/// Coordinates are the engine's: x east, y up, z north.
/// </summary>
public class VehicleShadowApplyTests
{
    private static readonly Vector3 Ear = new(0f, 1.6f, 0f);
    private static readonly Vector3 Source = new(0f, 0.6f, 20f);
    private static readonly Vector3 BusSize = new(2.5f, 3.2f, 12f);
    private const int SourceId = 50, BusId = 60;

    private static ClientWorldState World()
    {
        var w = new ClientWorldState();
        w.Clear(new Vector3(400, 100, 400));
        return w;
    }

    /// <summary>A body registered as the server registers a vehicle: a moving entity with a solid box.</summary>
    private static void AddBody(ClientWorldState w, int id, Vector3 restsAt, Vector3 size, Quaternion? rotation = null,
                                EntityType type = EntityType.NPC, bool moves = true)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = type,
            Moves = moves,
            Transform = new Transform { Position = restsAt, Rotation = rotation ?? Quaternion.Identity, Scale = Vector3.One },
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
            Material = new MaterialComponent { Material = "Metal" },
        };
        w.RegisterDefinition(def);
    }

    private static void MoveTo(ClientWorldState w, int id, Vector3 position, Quaternion? rotation = null)
        => w.SyncState(new[] { new EntityState
        {
            EntityId = id,
            Transform = QuantizedTransform.FromTransform(new Transform
            {
                Position = position, Rotation = rotation ?? Quaternion.Identity, Scale = Vector3.One,
            }),
        } });

    private static AcousticPathData Clear() => new(0f, Source, 20f, eqL: 1f, eqM: 1f, eqH: 1f);

    /// <summary>
    /// A bus parked broadside across the line, half way, as the client's own snapshot carries it: the
    /// path comes back darker in every band, most at the top, and the loss reported is the top band's.
    /// </summary>
    [Fact]
    public void ABusInTheClientsWorldShadowsACarBehindIt()
    {
        var w = World();
        // Turned a quarter, so its 12 m length lies across the line to the car.
        AddBody(w, BusId, new Vector3(0f, 0f, 10f), BusSize, Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f));
        var snap = w.GetSnapshot();
        Assert.Contains(snap.DynamicEntities, e => e.Id == BusId);

        var path = Clear();
        float worst = VehicleShadow.Apply(ref path, snap, SourceId, Source, Ear, ridingId: -1);

        Assert.True(worst > 3f, $"a bus across the line took {worst:F1} dB off the top");
        Assert.True(path.EqHigh < path.EqMid && path.EqMid < path.EqLow && path.EqLow < 1f,
            $"bands {path.EqLow:F3}/{path.EqMid:F3}/{path.EqHigh:F3}: a barrier takes most off the top");
        Assert.Equal(-worst, 20f * MathF.Log10(path.EqHigh), 3);
    }

    /// <summary>
    /// The barrier moves. The same bus driven 30 m east is off the line and costs nothing; driven back
    /// it costs what it did. Worked out every frame from where the body is now, never cached.
    /// </summary>
    [Fact]
    public void TheShadowGoesWhereTheBusGoes()
    {
        var w = World();
        var across = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);
        AddBody(w, BusId, new Vector3(0f, 0f, 10f), BusSize, across);

        var before = Clear();
        float lossBefore = VehicleShadow.Apply(ref before, w.GetSnapshot(), SourceId, Source, Ear, -1);
        Assert.True(lossBefore > 0f);

        MoveTo(w, BusId, new Vector3(30f, 0f, 10f), across);
        var away = Clear();
        Assert.Equal(0f, VehicleShadow.Apply(ref away, w.GetSnapshot(), SourceId, Source, Ear, -1));
        Assert.Equal(1f, away.EqHigh);

        MoveTo(w, BusId, new Vector3(0f, 0f, 10f), across);
        var back = Clear();
        Assert.Equal(lossBefore, VehicleShadow.Apply(ref back, w.GetSnapshot(), SourceId, Source, Ear, -1), 3);
        Assert.Equal(before.EqHigh, back.EqHigh, 4);
    }

    /// <summary>
    /// A building is not a vehicle. A static wall of the same size in the same place is not in the
    /// snapshot's moving bodies, so Apply leaves it to the acoustic scene that already has it: counting
    /// it here as well would take the wall off twice.
    /// </summary>
    [Fact]
    public void AStaticWallIsLeftToTheSceneAndNotCountedTwice()
    {
        var w = World();
        AddBody(w, 70, new Vector3(0f, 0f, 10f), new Vector3(12f, 3.2f, 2.5f), type: EntityType.StaticObject, moves: false);
        var snap = w.GetSnapshot();
        Assert.DoesNotContain(snap.DynamicEntities, e => e.Id == 70);

        var path = Clear();
        Assert.Equal(0f, VehicleShadow.Apply(ref path, snap, SourceId, Source, Ear, -1));
        Assert.Equal(1f, path.EqLow);
        Assert.Equal(1f, path.EqMid);
        Assert.Equal(1f, path.EqHigh);
    }

    /// <summary>
    /// The loss is laid on top of the path, not in place of it: a car already behind a wall (bands
    /// 0.5/0.3/0.1 from the scene) and then behind a bus is darker by exactly the bus's loss, and
    /// nothing else about the path — its occlusion, where it seems to come from, its air — is touched.
    /// </summary>
    [Fact]
    public void TheBusesLossIsLaidOnTopOfWhatThePathAlreadyHad()
    {
        var w = World();
        AddBody(w, BusId, new Vector3(0f, 0f, 10f), BusSize, Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f));
        var snap = w.GetSnapshot();

        var open = Clear();
        VehicleShadow.Apply(ref open, snap, SourceId, Source, Ear, -1);

        var apparent = new Vector3(3f, 1f, 18f);
        var walled = new AcousticPathData(0.6f, apparent, 23f, eqL: 0.5f, eqM: 0.3f, eqH: 0.1f)
        {
            AirLowDb = 0.1f, AirMidDb = 0.4f, AirHighDb = 2f, RoomGain = 0.7f,
        };
        VehicleShadow.Apply(ref walled, snap, SourceId, Source, Ear, -1);

        Assert.Equal(0.5f * open.EqLow, walled.EqLow, 5);
        Assert.Equal(0.3f * open.EqMid, walled.EqMid, 5);
        Assert.Equal(0.1f * open.EqHigh, walled.EqHigh, 5);
        Assert.Equal(0.6f, walled.Occlusion);
        Assert.Equal(apparent, walled.ApparentPosition);
        Assert.Equal(23f, walled.EffectiveDistance);
        Assert.Equal(2f, walled.AirHighDb);
        Assert.Equal(0.7f, walled.RoomGain);
    }

    /// <summary>
    /// Sitting in the bus, the bus is not between you and anything: the riding id is excluded, so the
    /// car outside is heard as the cabin model has it and not shadowed by your own seat a second time.
    /// And a sound the bus itself makes is never shadowed by the bus.
    /// </summary>
    [Fact]
    public void YourOwnRideAndTheSourceItselfAreNeverTheBarrier()
    {
        var w = World();
        var across = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);
        AddBody(w, BusId, new Vector3(0f, 0f, 10f), BusSize, across);
        var snap = w.GetSnapshot();

        var riding = Clear();
        Assert.Equal(0f, VehicleShadow.Apply(ref riding, snap, SourceId, Source, Ear, ridingId: BusId));
        Assert.Equal(1f, riding.EqHigh);

        var itself = Clear();
        Assert.Equal(0f, VehicleShadow.Apply(ref itself, snap, BusId, Source, Ear, ridingId: -1));
        Assert.Equal(1f, itself.EqHigh);
    }
}
