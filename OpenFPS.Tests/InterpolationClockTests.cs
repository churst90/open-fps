using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core;

namespace OpenFPS.Tests;

/// <summary>
/// The playback clock stays on the server's clock. Advanced by the client's own frame delta, it drifted
/// until no two snapshots bracketed it, and the interpolator then updated nothing: every entity froze,
/// heard on the speedway as cars stopping for about a second and carrying on.
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

        // The client renders with a delta 6 % longer, ordinary for two unsynchronised clocks: uncorrected, a third
        // of a second out of step within ten seconds.
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

        // A run of identical positions is the world frozen; more than a handful of frames is the failure.
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
