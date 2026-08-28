using System.Numerics;

namespace OpenFPS.Common;

public static class PhysicsConstants
{
    public static readonly Vector3 PlayerSize = new(0.6f, 1.8f, 0.6f);
    public const float PlayerRadius = 0.3f;
    public const float PlayerHeight = 1.8f;
    public const float WalkSpeed = 4.5f;
    public const float JumpPower = 5.0f;
    public const float Gravity = 15.0f;
    public const float StepHeight = 0.4f;
    public const float RotationSpeed = 1.5f; // SHARED: Radians per second at full stick/key

    // --- Simulation Bounds & Environment ---
    public const float MapMinimumY = -10.0f;
    public const float DefaultGroundCheckLimit = -900.0f;
    public const float InteractionRange = 5.0f;
    public const float EarshotRange = 200.0f;
    public const float CollisionSearchRadius = 5.0f;

    // --- Simulation Timing ---
    // ONE rate for the whole game: the server's authoritative tick, the client's fixed
    // prediction step, and the tick period the interpolator reconstructs server time from.
    // A mismatch here is not a smoothness problem, it is a divergence problem — the client
    // integrates WalkSpeed over its own step while the server integrates it over the tick,
    // so the two disagree about how far a held key moves you.
    public const int TickRate = 30; // 30 ticks per second, client and server
    public const float FixedDeltaTime = 1.0f / TickRate; // ~0.0333s

    /// <summary>
    /// Longest real interval a fixed-step loop may bank before it stops trying to catch up.
    /// A GC pause, a debugger break or a suspended laptop hands the loop an arbitrarily large
    /// elapsed time; without this the next iteration runs hundreds of ticks back to back, which
    /// looks to every connected player like the world fast-forwarding. Both client heads already
    /// clamp here — the server clamps to the same number so the three agree about the worst case.
    /// </summary>
    public const float MaxCatchUpSeconds = 0.2f;

    // --- Input Integrity (server-side) ---
    /// <summary>Longest simulated step a single client input may claim (guards a forged DeltaTime).</summary>
    public const float MaxInputDeltaTime = FixedDeltaTime * 1.5f;
    /// <summary>Hard cap on inputs drained per player per tick, so a flood cannot stall the loop.</summary>
    public const int MaxInputsPerTick = 4;
    /// <summary>Backlog of simulated time a player may bank while lagging, in ticks.</summary>
    public const float MaxInputBudgetTicks = 3.0f;
    /// <summary>Depth of a session's pending-input queue; excess arrivals are dropped.</summary>
    public const int MaxQueuedInputs = 64;
    /// <summary>Unacknowledged inputs the client keeps for reconciliation (~3 seconds).</summary>
    public const int MaxInputHistory = TickRate * 3;
}
