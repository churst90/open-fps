namespace OpenFPS.Common;

/// <summary>
/// The person and the world the sound models assume: how tall a person is, how fast they walk and run,
/// and how fast things fall. Glass, breathing, the wind at the ears and a bullet meeting a body read them
/// here; the game's movement (<c>PhysicsConstants</c>) takes the same values from here, so the sound and
/// the movement cannot disagree.
/// </summary>
public static class BodyConstants
{
    /// <summary>A person's height to the top of the head, metres.</summary>
    public const float PersonHeight = 1.8f;

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
    /// The Earth's, m/s² (Cody, 2026-10-04: "shouldn't you fall at the speed of gravity on earth?"). A
    /// fall is heard, so it is a claim about the world: the eighteen metres off the Brandt Court roof
    /// take 1.92 s, against 1.55 s at the 15 once chosen for how a jump felt.
    /// </summary>
    public const float Gravity = 9.81f;
}
