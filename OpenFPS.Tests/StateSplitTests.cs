using System.Numerics;
using System.Reflection;
using MemoryPack;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;

namespace OpenFPS.Tests;

/// <summary>
/// Each piece of a split state update says everything the whole did about its player: halves rebuilt
/// from three fields told a driver on the city, which splits every tick, that they were on foot. Checked
/// by reflection, so a field appended later is covered too.
/// </summary>
public class StateSplitTests
{
    private static ServerStateUpdate Whole(int count) => new()
    {
        Tick = 1234, LastProcessedSequenceId = 99, RidingEntityId = 5858, RidingControls = true,
        States = Enumerable.Range(0, count).Select(i => new EntityState
        {
            EntityId = 1000 + i,
            Transform = QuantizedTransform.FromTransform(new Transform { Position = new Vector3(i, 2, -i), Rotation = Quaternion.Identity }),
            LinearVelocity = new Vector3(i % 3, 0, 1),
            Wheels = i % 4 == 0 ? new WheelState[4] : null,
        }).ToList(),
    };

    [Fact]
    public void EachPieceCarriesEverythingButTheStates()
    {
        var whole = Whole(10);
        var piece = NetworkService.Piece(whole, new byte[] { 1, 2, 3 });

        foreach (var f in typeof(ServerStateUpdate).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.Name is nameof(ServerStateUpdate.States) or nameof(ServerStateUpdate.Packed)) continue;
            Assert.True(Equals(f.GetValue(whole), f.GetValue(piece)), $"{f.Name} was not carried into the piece");
        }
        foreach (var p in typeof(ServerStateUpdate).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.Name is nameof(ServerStateUpdate.States) or nameof(ServerStateUpdate.Packed)) continue;
            Assert.True(Equals(p.GetValue(whole), p.GetValue(piece)), $"{p.Name} was not carried into the piece");
        }
    }

    /// <summary>Every state arrives, once, in order, and no piece is over the packet limit — and the
    /// pieces are full, not halves.</summary>
    [Fact]
    public void PiecesFitThePacketAndLoseNothing()
    {
        const int max = 1024;
        var whole = Whole(500);
        var pieces = NetworkService.Pieces(whole, max);

        var received = new List<EntityState>();
        int bytes = 0;
        foreach (var piece in pieces)
        {
            byte[] data = MemoryPackSerializer.Serialize<IMessage>(piece);
            Assert.True(data.Length <= max, $"a piece is {data.Length} bytes");
            bytes += data.Length;
            var back = (ServerStateUpdate)MemoryPackSerializer.Deserialize<IMessage>(data)!;
            Assert.Equal(whole.RidingEntityId, back.RidingEntityId);
            Assert.Null(back.Packed);
            received.AddRange(back.States);
        }
        Assert.Equal(whole.States.Select(s => s.EntityId), received.Select(s => s.EntityId));
        // Filled, not halved: each piece within its header and one state of full.
        Assert.True(pieces.Count <= bytes / (max - 100) + 1, $"{pieces.Count} pieces for {bytes} bytes");
    }

    [Fact]
    public void ATickWithNoStatesStillGoes()
    {
        var pieces = NetworkService.Pieces(Whole(0), 1024);
        Assert.Single(pieces);
        Assert.Equal(5858, pieces[0].RidingEntityId);
    }
}
