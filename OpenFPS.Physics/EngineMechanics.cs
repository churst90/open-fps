namespace OpenFPS.Common;

/// <summary>
/// An engine's mechanics: the physics' half of an engine preset, under the same id as the sound's half
/// (EngineProfile, which holds one: <c>EngineProfile.Mechanics</c>). What a car is moved with: where its engine
/// idles, how far it revs, the most torque it makes and the speed it makes it at. Everything else of an engine
/// (its cylinders, cams, gas path, the tick of its valves) is its sound. Its rotating inertia and its friction
/// are read only by the sound's own engine today, so they are the sound's until a car's drivetrain is stepped
/// from the engine's own torque (docs/PHYSICS_BOUNDARY.md, stage 6).
/// </summary>
public sealed record EngineMechanics
{
    /// <summary>The speed it settles at with the throttle shut, rpm. Nought until the engine that holds these
    /// mechanics is given one.</summary>
    public float IdleRpm { get; init; }

    /// <summary>The highest speed it is run to, rpm. Nought until the engine that holds these mechanics is
    /// given one.</summary>
    public float RedlineRpm { get; init; }

    /// <summary>The most torque it makes, Nm.</summary>
    public float PeakTorqueNm { get; init; } = 500f;

    /// <summary>The speed at which it makes its peak torque, rpm.</summary>
    public float PeakTorqueRpm { get; init; } = 4200f;
}
