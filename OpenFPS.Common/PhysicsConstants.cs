using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// The game's rules of movement and the network's: how far a hand reaches, how far a map goes down, the
/// tick, and what the server takes of a client's input. The body's own numbers (its size, mass, walk, run,
/// jump, step and gravity) are <see cref="BodyConstants"/>', in the physics; the names here read them there,
/// for the 1,100 places that ask the game.
/// </summary>
public static class PhysicsConstants
{
    /// <summary>The box a body on foot takes up, metres (<see cref="BodyConstants.PlayerSize"/>).</summary>
    public static Vector3 PlayerSize => BodyConstants.PlayerSize;
    public const float PlayerRadius = BodyConstants.PlayerRadius;
    /// <summary>A person's height, metres (<see cref="BodyConstants.PersonHeight"/>).</summary>
    public const float PlayerHeight = BodyConstants.PersonHeight;

    /// <summary>What a person is made of to anything that meets one (<see cref="BodyConstants.PersonMaterial"/>).</summary>
    public const string PersonMaterial = BodyConstants.PersonMaterial;

    /// <summary>A person's mass, kilograms (<see cref="BodyConstants.PersonMassKg"/>).</summary>
    public const float PersonMassKg = BodyConstants.PersonMassKg;

    /// <summary>Metres per second at a walk (<see cref="BodyConstants.WalkSpeed"/>).</summary>
    public const float WalkSpeed = BodyConstants.WalkSpeed;

    /// <summary>The run as a multiple of <see cref="WalkSpeed"/> (<see cref="BodyConstants.SprintMultiplier"/>).</summary>
    public const float SprintMultiplier = BodyConstants.SprintMultiplier;

    /// <summary>Metres per second at a run, client and server (<see cref="BodyConstants.SprintSpeed"/>).</summary>
    public const float SprintSpeed = BodyConstants.SprintSpeed;

    /// <summary>Metres per second at most with a person over your shoulder, anything heavier than
    /// HandsService.CarryCapacityKg (<see cref="BodyConstants.CarryingSpeed"/>).</summary>
    public const float CarryingSpeed = BodyConstants.CarryingSpeed;

    /// <summary>How fast a body moves on foot this step (<see cref="BodyConstants.FootSpeed"/>).</summary>
    public static float FootSpeed(bool sprint, float limit) => BodyConstants.FootSpeed(sprint, limit);

    /// <summary>Metres per second straight up as a standing jump leaves the ground (<see cref="BodyConstants.JumpPower"/>).</summary>
    public const float JumpPower = BodyConstants.JumpPower;

    /// <summary>The Earth's, m/s² (<see cref="BodyConstants.Gravity"/>).</summary>
    public const float Gravity = BodyConstants.Gravity;
    /// <summary>The highest step a body on foot takes without jumping, metres (<see cref="BodyConstants.StepHeight"/>).</summary>
    public const float StepHeight = BodyConstants.StepHeight;
    /// <summary>How fast a body on foot turns, radians per second (<see cref="BodyConstants.RotationSpeed"/>).</summary>
    public const float RotationSpeed = BodyConstants.RotationSpeed;

    // --- Simulation Bounds & Environment ---
    public const float MapMinimumY = -10.0f;
    public const float DefaultGroundCheckLimit = BodyConstants.DefaultGroundCheckLimit;
    public const float InteractionRange = 5.0f;
    /// <summary>How far E reaches for something on the ground, metres: an arm and a step. The client
    /// chooses which thing within it (PickUp); the server counts what is left within it.</summary>
    public const float PickUpReach = 2.0f;
    public const float CollisionSearchRadius = BodyConstants.CollisionSearchRadius;

    // --- Simulation Timing ---
    // One rate for the server's tick, the client's prediction step and the interpolator: a mismatch
    // makes the two disagree about how far a held key moves you.
    public const int TickRate = 30; // 30 ticks per second, client and server
    public const float FixedDeltaTime = 1.0f / TickRate; // ~0.0333s

    /// <summary>
    /// Longest real interval a fixed-step loop banks before it stops catching up: after a GC pause or a
    /// suspended laptop it would run hundreds of ticks back to back, the world fast-forwarding. Both
    /// client heads and the server clamp here.
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
