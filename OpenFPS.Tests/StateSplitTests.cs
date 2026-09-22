using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// A state update too big for one packet is sent as two, and each half must still say everything
/// the whole did about the player it is for. It did not: the halves were rebuilt from three fields
/// and RidingEntityId fell back to -1, so on the city — which splits every tick — a driver was told
/// every tick that they were on foot. Checked by reflection, so the next field appended to the
/// message is covered without anyone remembering this test exists.
/// </summary>
public class StateSplitTests
{
    [Fact]
    public void EachHalfCarriesEverythingButTheStates()
    {
        var whole = new ServerStateUpdate
        {
            Tick = 1234, LastProcessedSequenceId = 99, RidingEntityId = 5858, RidingControls = true,
            States = Enumerable.Range(0, 10).Select(i => new EntityState { EntityId = i }).ToList(),
        };
        var half = NetworkService.Half(whole, 5, 5);
        Assert.Equal(5, half.States.Count);
        Assert.Equal(5, half.States[0].EntityId);

        foreach (var f in typeof(ServerStateUpdate).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.Name == nameof(ServerStateUpdate.States)) continue;
            Assert.True(Equals(f.GetValue(whole), f.GetValue(half)), $"{f.Name} was not carried into the half");
        }
        foreach (var p in typeof(ServerStateUpdate).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.Name == nameof(ServerStateUpdate.States)) continue;
            Assert.True(Equals(p.GetValue(whole), p.GetValue(half)), $"{p.Name} was not carried into the half");
        }
    }
}
