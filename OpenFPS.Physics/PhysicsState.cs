using System.Numerics;
using OpenFPS.Common.Networking;

namespace OpenFPS.Common;

/// <summary>Whether a body on foot stands on something, as its last step left it. Unknown for anything no
/// step stands (a car, a crate).</summary>
public enum Grounding : byte
{
    Unknown = 0,
    Grounded = 1,
    Airborne = 2,
}

/// <summary>
/// A body as the physics leaves it after a step (docs/PHYSICS_BOUNDARY.md 4.5): what the host copies onto the
/// components the wire carries and the sound reads. Angular velocity, mass and shape are carried for the rigid
/// bodies of stage 4; a body on foot has none of the first and its mass from <see cref="BodyConstants"/>.
/// </summary>
public readonly record struct BodyState(
    int Id, Vector3 Position, Quaternion Orientation, Vector3 Velocity, Vector3 AngularVelocity,
    float MassKg, int ShapeId, Grounding Grounding, bool Sleeping)
{
    /// <summary>No shape named yet: geometry stage 6 gives each body a piece both the contacts and the
    /// acoustic scene read.</summary>
    public const int NoShape = -1;

    /// <summary>A person on foot after a step: feet at <paramref name="feet"/>, facing
    /// <paramref name="facing"/>.</summary>
    public static BodyState OnFoot(int id, Vector3 feet, Quaternion facing, Vector3 velocity, bool grounded)
        => new(id, feet, facing, velocity, Vector3.Zero, BodyConstants.PersonMassKg, NoShape,
               grounded ? Grounding.Grounded : Grounding.Airborne, Sleeping: false);
}

/// <summary>Whether a contact is new this tick, was there last tick too, or has just gone.</summary>
public enum ContactPhase : byte
{
    Began = 0,
    Continued = 1,
    Ended = 2,
}

/// <summary>
/// One contact between a body and something else (docs/PHYSICS_BOUNDARY.md 4.5): the two, where they met,
/// the way out of the other thing (its outward normal), the impulse that passed between them, how fast they
/// closed and how fast they slid along each other there, and the two surfaces. A sound is a contact with a
/// closing speed, two materials and two masses (<c>ImpactAcoustics.Between</c>), so the host turns these into
/// what is heard.
/// </summary>
/// <param name="Body">The body that was stepped (an entity id).</param>
/// <param name="Other">What it met: an entity id (a moving body or the owner of a solid), or
/// <see cref="NoOther"/> for the edge of the map or a tile not built yet.</param>
/// <param name="NormalImpulse">N s along the normal, what the body lost into the other thing; zero where the
/// step does not exchange momentum (a body on foot slides along a wall).</param>
public readonly record struct ContactEvent(
    int Body, int Other, Vector3 Point, Vector3 Normal, float NormalImpulse, float ClosingSpeed, float SlipSpeed,
    string Surface, string OtherSurface, ContactPhase Phase)
{
    /// <summary>The other side of a contact with no body: the map's edge, a tile not built yet.</summary>
    public const int NoOther = -1;
}

/// <summary>
/// A vehicle as the physics leaves it after a step (docs/PHYSICS_BOUNDARY.md 4.5): its motion, what its
/// engine and driver are doing and its wheels. The host copies it onto the components the wire carries
/// (<c>DriveComponent</c>, <c>VehicleComponent</c>, the entity's state); the sound reads it.
/// </summary>
public struct VehicleState
{
    /// <summary>Along its heading, m/s; negative backing.</summary>
    public float Speed;
    /// <summary>Radians clockwise from north (forward is sin, cos in east and north).</summary>
    public float Heading;
    /// <summary>How fast the heading turns, rad/s.</summary>
    public float YawRate;
    /// <summary>Along and across the heading, m/s², from this step's change of velocity.</summary>
    public float LongitudinalAcceleration, LateralAcceleration;
    /// <summary>The steering wheel's position, -1 full left to 1 full right.</summary>
    public float Steer;
    /// <summary>What the driver has on the pedals: throttle -1 (reverse) to 1, brake 0 to 1.</summary>
    public float Throttle, Brake;
    /// <summary>The gear the engine pulls through, 1 the lowest, and its speed.</summary>
    public int Gear;
    public float Rpm;
    /// <summary>How far the clutch is in, 0 down to 1 up. The server's car shifts with the clutch up
    /// (stage 6 gives it one).</summary>
    public float Clutch;
    /// <summary>What the engine puts into the gearbox, N m: positive pulling, negative braking on a closed
    /// throttle.</summary>
    public float LoadTorqueNm;
    /// <summary>How hard its tyres are worked, 0..2 with 1 the limit.</summary>
    public float TyreDemand;
    /// <summary>Each wheel as the wire carries it, front axle first.</summary>
    public WheelState[]? Wheels;
}

/// <summary>
/// The air at a place and moment (docs/PHYSICS_BOUNDARY.md 4.5 and 5.2): its velocity in three dimensions,
/// how turbulent it is, and how far its pressure is from the mean. Today only the weather's wind
/// (<see cref="WindField"/>): stage 5 adds what moving bodies and blasts push, and the readers (the ear's
/// wind, foliage, fire) take it then.
/// </summary>
public readonly record struct AirSample(Vector3 At, double Seconds, Vector3 Velocity, float Turbulence, float PressureDeviationPa)
{
    /// <summary>The weather's air at <paramref name="at"/> (engine x east, y up, z north), at
    /// <paramref name="heightAboveGround"/> over the ground, at <see cref="WindField.Now"/>'s
    /// <paramref name="seconds"/>. Horizontal: the weather's field has no vertical part.</summary>
    public static AirSample Of(WindWeather weather, Vector3 at, float heightAboveGround, double seconds)
    {
        var v = WindField.VelocityAt(weather, at.X, heightAboveGround, at.Z, seconds);
        return new AirSample(at, seconds, new Vector3(v.X, 0f, v.Y), weather.At(seconds).Turbulence, 0f);
    }
}
