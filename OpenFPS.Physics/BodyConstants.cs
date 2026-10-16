namespace OpenFPS.Common;

/// <summary>
/// The person the sound models assume: how tall a person is, how fast they walk and run. Glass,
/// breathing, the wind at the ears and a bullet meeting a body read them here (how fast things fall is
/// <see cref="Earth.StandardGravity"/>); the game's movement (<c>PhysicsConstants</c>) takes the same
/// values from here, so the sound and the movement cannot disagree.
/// </summary>
public static class BodyConstants
{
    /// <summary>A person's height to the top of the head, metres.</summary>
    public const float PersonHeight = 1.8f;

    /// <summary>The width of a person's torso, metres: what a shotgun's pattern has to land on, and twice the
    /// radius of the upright cylinder a person is hit as.</summary>
    public const float TorsoWidth = 0.45f;

    /// <summary>The radius of the upright capsule a body on foot walks as, metres.</summary>
    public const float PlayerRadius = 0.3f;

    /// <summary>The box a body on foot takes up, metres: as wide and deep as twice its radius, as tall as it is.</summary>
    public static readonly System.Numerics.Vector3 PlayerSize = new(0.6f, 1.8f, 0.6f);

    /// <summary>
    /// What a person is made of to anything that meets one: soft and lossy (the material table's "Skin").
    /// Not "Generic", the knock of a stone pillar, and not the floor under them, which movement once
    /// wrote into their material (Cody, 2026-10-05: "am I made of concrete too?").
    /// </summary>
    public const string PersonMaterial = "Skin";

    /// <summary>A person's mass, kilograms: an adult, what a body over your shoulder weighs.</summary>
    public const float PersonMassKg = 70f;

    /// <summary>The highest step a body on foot takes without jumping, metres, and how far it steps down.</summary>
    public const float StepHeight = 0.4f;

    /// <summary>How far round a body the walk gathers what it might meet in a tick, metres.</summary>
    public const float CollisionSearchRadius = 5.0f;

    /// <summary>The height a ground probe answers when there is no ground under a body: far below any map.</summary>
    public const float DefaultGroundCheckLimit = -900.0f;

    /// <summary>The walk, metres per second, client and server.</summary>
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
    /// than the hands' carrying capacity). About three quarters of an unloaded walk (1.3 to 1.4 m/s; the
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

    /// <summary>How fast a body on foot turns with the key or the stick held right over, radians per second.</summary>
    public const float RotationSpeed = 1.5f;
}
