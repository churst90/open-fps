using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using MemoryPack;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A state update too big for one packet is sent as several, and each piece must still say everything
/// the whole did about the player it is for. It did not: the halves were rebuilt from three fields
/// and RidingEntityId fell back to -1, so on the city — which splits every tick — a driver was told
/// every tick that they were on foot. Checked by reflection, so the next field appended to the
/// message is covered without anyone remembering this test exists.
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
