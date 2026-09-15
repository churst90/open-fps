using System;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The playback clock has to stay ON the server's clock, or the world stops moving.
///
/// Entity positions are interpolated between two buffered server snapshots that BRACKET a playback
/// time. That time used to be initialised once and then advanced by the client's own frame delta for
/// ever — an independent clock, with no correction anywhere. Two clocks drift; when the drift exceeds
/// the buffer, no pair brackets the playback time, and the interpolator's response to that is to
/// update nothing at all: every entity in the world holds its position until a pair exists again.
/// Silently. Heard from the speedway grandstand as some of the cars stopping in front of you for
/// about a second and then carrying on — "some", because a frozen car passing a few metres away
/// swings its bearing hugely and a frozen one across the infield barely moves at all.
/// </summary>
public class InterpolationClockTests
{
    private static ServerStateUpdate Snapshot(long tick, int entityId, Vector3 pos) => new()
    {
        Tick = tick,
        States = new System.Collections.Generic.List<EntityState>
        {
            new()
            {
                EntityId = entityId,
                Transform = QuantizedTransform.FromTransform(new Transform { Position = pos, Rotation = Quaternion.Identity }),
                LinearVelocity = new Vector3(0, 0, 10f),
            }
        },
    };

    [Fact]
    public void AClientClockThatRunsFastDoesNotFreezeTheWorld()
    {
        var world = new ClientWorldState();
        const int car = 77;

        // The server ticks at its fixed rate; the client renders with a delta 6 % longer, which is
        // an ordinary amount for two unsynchronised clocks and, uncorrected, walks a third of a
        // second out of step inside ten seconds.
        float serverDt = PhysicsConstants.FixedDeltaTime;
        float clientDt = serverDt * 1.06f;

        long tick = 0;
        float z = 0f;
        for (int i = 0; i < 4; i++) { world.SyncState(Snapshot(tick++, car, new Vector3(0, 0, z))); z += 10f * serverDt; }

        var seen = new System.Collections.Generic.List<float>();
        for (int frame = 0; frame < 600; frame++)          // 20 seconds
        {
            world.SyncState(Snapshot(tick++, car, new Vector3(0, 0, z)));
            z += 10f * serverDt;
            world.UpdateInterpolation(clientDt, localPlayerId: -1);
            seen.Add(world.TryGetInterpolatedTransform(car, out var tr) ? tr.Position.Z : float.NaN);
        }

        // The car must never stop. A run of identical positions is the world frozen; anything over a
        // handful of frames is the failure this test exists for.
        int longestStall = 0, stall = 0;
        for (int i = 1; i < seen.Count; i++)
        {
            if (MathF.Abs(seen[i] - seen[i - 1]) < 1e-4f) { stall++; if (stall > longestStall) longestStall = stall; }
            else stall = 0;
        }

        Assert.True(longestStall < 5,
            $"the car stopped for {longestStall} consecutive frames ({longestStall * clientDt:F2} s) — the world froze");
        Assert.True(seen[^1] > seen[0] + 100f, "the car did not actually travel");
    }
}
