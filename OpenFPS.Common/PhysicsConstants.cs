using System.Numerics;

namespace OpenFPS.Common;

public static class PhysicsConstants
{
    public static readonly Vector3 PlayerSize = new(0.6f, 1.8f, 0.6f);
    public const float PlayerRadius = 0.3f;
    public const float PlayerHeight = 1.8f;

    /// <summary>
    /// What a person is made of to anything that meets one: soft and lossy (AcousticRegistry "Skin").
    /// Not "Generic", the knock of a stone pillar, and not the floor under them, which movement once
    /// wrote into their material (Cody, 2026-10-05: "am I made of concrete too?").
    /// </summary>
    public const string PersonMaterial = "Skin";

    /// <summary>A person's mass, kilograms: an adult, what a body over your shoulder weighs.</summary>
    public const float PersonMassKg = 70f;

    public const float WalkSpeed = 4.5f;

    /// <summary>
    /// The run as a multiple of <see cref="WalkSpeed"/>: a claim about a body, since it decides how
    /// often and how loud it is heard. 4.5 m/s is a brisk jog; 7.2 a hard run a fit person sustains.
    /// </summary>
    public const float SprintMultiplier = 1.6f;

    /// <summary>Metres per second at a run, client and server.</summary>
    public const float SprintSpeed = WalkSpeed * SprintMultiplier;

    /// <summary>
    /// Metres per second at most, walking or running, with a person over your shoulder (anything heavier
    /// than HandsService.CarryCapacityKg). About three quarters of an unloaded walk (1.3 to 1.4 m/s; the
    /// city's walkers go at 1.35): an adult is about the carrier's weight, beyond any pack in the
    /// load-carriage studies, which show the pace falling with load. An estimate, not a measurement.
    /// </summary>
    public const float CarryingSpeed = 1.0f;

    /// <summary>
    /// How fast a body moves on foot this step: the walk or the run, held under a positive
    /// <paramref name="limit"/>. Asked by client and server alike, or prediction is pulled back every step.
    /// </summary>
    public static float FootSpeed(bool sprint, float limit)
    {
        float speed = sprint ? SprintSpeed : WalkSpeed;
        return limit > 0f && limit < speed ? limit : speed;
    }

    /// <summary>
    /// Metres per second straight up as a standing jump leaves the ground: sqrt(2 g h) for a rise of
    /// half a metre, about what a person manages from a standstill.
    /// </summary>
    public const float JumpPower = 3.13f;

    /// <summary>
    /// The Earth's, m/s² (Cody, 2026-10-04: "shouldn't you fall at the speed of gravity on earth?"). A
    /// fall is heard, so it is a claim about the world: the eighteen metres off the Brandt Court roof
    /// take 1.92 s, against 1.55 s at the 15 once chosen for how a jump felt.
    /// </summary>
    public const float Gravity = 9.81f;
    public const float StepHeight = 0.4f;
    public const float RotationSpeed = 1.5f; // SHARED: Radians per second at full stick/key

    // --- Simulation Bounds & Environment ---
    public const float MapMinimumY = -10.0f;
    public const float DefaultGroundCheckLimit = -900.0f;
    public const float InteractionRange = 5.0f;
    /// <summary>How far E reaches for something on the ground, metres: an arm and a step. The client
    /// chooses which thing within it (PickUp); the server counts what is left within it.</summary>
    public const float PickUpReach = 2.0f;
    public const float CollisionSearchRadius = 5.0f;

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
