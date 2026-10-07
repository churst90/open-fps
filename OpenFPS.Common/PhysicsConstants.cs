using System.Numerics;

namespace OpenFPS.Common;

public static class PhysicsConstants
{
    public static readonly Vector3 PlayerSize = new(0.6f, 1.8f, 0.6f);
    public const float PlayerRadius = 0.3f;
    public const float PlayerHeight = 1.8f;

    /// <summary>
    /// What a person is made of, to anything that meets one: a body, soft and lossy (AcousticRegistry
    /// "Skin"), as a dead one already was. A player used to be "Generic" — five gigapascals and
    /// ringing, the knock of a stone pillar — and then took on whatever floor they stood on, because
    /// movement wrote the floor under them into their own material: bumping into somebody on a
    /// concrete roof was bumping into concrete (Cody, 2026-10-05: "am I made of concrete too?").
    /// </summary>
    public const string PersonMaterial = "Skin";

    /// <summary>A person's mass, kilograms: an adult, what a body over your shoulder weighs.</summary>
    public const float PersonMassKg = 70f;

    public const float WalkSpeed = 4.5f;

    /// <summary>
    /// How much faster a body moves while it is running, as a multiple of <see cref="WalkSpeed"/>.
    ///
    /// Running is not simply "walking, but sooner". It is what decides how loud and how often a body
    /// is heard — its footfalls come twice as often, it breathes afterwards, and it is audible from
    /// further away — so the number is a physical claim about a body and not a tuning knob for the
    /// feel of the keyboard. 4.5 m/s is a brisk jog; 7.2 is a hard run and about what a fit person
    /// sustains.
    /// </summary>
    public const float SprintMultiplier = 1.6f;

    /// <summary>Metres per second at a run. One number, client and server, exactly as with the walk.</summary>
    public const float SprintSpeed = WalkSpeed * SprintMultiplier;

    /// <summary>
    /// Metres per second at most with a person over your shoulder (anything in your arms heavier than
    /// you could sling on your back: HandsService.CarryCapacityKg). Walking and running are the same
    /// pace with one: nobody runs carrying a body.
    ///
    /// 1.0 m/s is about three quarters of an unloaded person's own walking pace (1.3 to 1.4 m/s; the
    /// people walking the city go at 1.35), which is where self-selected speed falls under a load that
    /// heavy: an adult casualty is about the carrier's own weight, beyond any pack in the load-carriage
    /// studies, which already show the pace dropping as the load grows. An estimate, not a measurement.
    /// </summary>
    public const float CarryingSpeed = 1.0f;

    /// <summary>
    /// How fast a body moves on foot this step: the walk or the run, held under <paramref name="limit"/>
    /// when there is one (more than zero). The client and the server both ask this, so a player carrying
    /// a body predicts the same pace the server allows and is not pulled back every step.
    /// </summary>
    public static float FootSpeed(bool sprint, float limit)
    {
        float speed = sprint ? SprintSpeed : WalkSpeed;
        return limit > 0f && limit < speed ? limit : speed;
    }

    /// <summary>
    /// Metres per second straight up at the moment a standing jump leaves the ground: sqrt(2 g h)
    /// for a rise of half a metre, about what a person manages from a standstill. It was 5.0, which
    /// under the old gravity of 15 rose 0.83 m.
    /// </summary>
    public const float JumpPower = 3.13f;

    /// <summary>
    /// The Earth's, m/s². It was 15, chosen for how a jump felt; Cody (2026-10-04): "if you fall from
    /// somewhere, shouldn't you fall at the speed of gravity on earth?" A fall is heard — how long
    /// the drop takes before the landing, how hard the landing is — so it is a claim about the world.
    /// The eighteen metres off the Brandt Court roof take 1.92 s at this, 1.55 s at the old 15.
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
