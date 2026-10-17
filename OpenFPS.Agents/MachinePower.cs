using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// How hard a driven machine is working, from what its driver is making it do: a train's notch and an
/// aeroplane's power lever (docs/SPECIAL_CASES_AUDIT.md, S16 and P7). The server drives both, so the server works
/// these out and sends them (EntityState.Lever, as StatePacking.EncodeLever writes it). The client guessed them from motion: a notch from a speed
/// differenced between two network states, so a train braking down a grade read as coasting; a lever from the
/// climb angle and an acceleration, so the roll-out's reversers were a threshold on noise.
/// </summary>
public static class MachinePower
{
    /// <summary>A train pulling away, and one holding its speed.</summary>
    public const float TrainPulling = 0.9f, TrainHolding = 0.3f;

    /// <summary>Under this a train is standing, m/s.</summary>
    public const float TrainMovingMps = 0.5f;

    /// <summary>
    /// A train's notch: full while its driver wants more speed than it has, none while braking or standing at
    /// a platform, a little while holding the speed the line allows.
    /// </summary>
    /// <param name="dwelling">Standing at a platform.</param>
    /// <param name="speed">Its speed, m/s.</param>
    /// <param name="wanted">The speed its driver is making for, m/s.</param>
    public static float TrainNotch(bool dwelling, float speed, float wanted)
    {
        if (dwelling) return 0f;
        if (wanted > speed) return TrainPulling;
        if (wanted < speed) return 0f;
        return speed > TrainMovingMps ? TrainHolding : 0f;
    }

    /// <summary>A piston aeroplane's lever rolling out, taxiing or in the flare: idle. A piston engine has no
    /// reverser, so after touchdown it idles and its tyres are heard.</summary>
    public const float PistonIdle = 0.06f;

    /// <summary>An aeroplane standing, taxiing, and slowing on its reversers.</summary>
    public const float GroundStanding = 0.25f, GroundTaxi = 0.3f, GroundReversing = 0.55f;

    /// <summary>The reversers come out above this on the roll-out, m/s; below it the brakes finish the job.</summary>
    public const float ReversersAboveMps = 30f;

    /// <summary>
    /// The lever in the air, from the flight path: climbing is full power, level is cruise, descending is idle,
    /// so a descending path is an approach without saying so. Full by about six degrees up, idle by about four
    /// down: a climb needs everything and a descent nothing, so the lever falls faster than it rises.
    /// </summary>
    public static float FlightLever(Vector3 velocity)
    {
        float speed = velocity.Length();
        if (speed < 0.5f) return GroundStanding;               // sitting on the apron at idle
        float climb = velocity.Y / speed;                      // sine of the flight path angle
        return climb >= 0f
            ? Math.Clamp(0.62f + climb * 3.6f, 0f, 1f)
            : Math.Clamp(0.62f + climb * 8.0f, PistonIdle, 1f);
    }

    /// <summary>
    /// The lever on the wheels, from what the ground run is doing: the take-off roll is full power; the
    /// roll-out from speed is the reversers, which run the engines up again; taxiing is idle with a little
    /// breakaway thrust; holding is idle. A piston aeroplane idles through all of it but the take-off roll.
    /// </summary>
    public static float GroundLever(AircraftGroundRun.Phase phase, float speed, AircraftPower power)
    {
        if (phase is AircraftGroundRun.Phase.TakeoffRoll or AircraftGroundRun.Phase.Done) return 1f;
        if (power == AircraftPower.Piston) return PistonIdle;
        if (phase == AircraftGroundRun.Phase.Rollout && speed > ReversersAboveMps) return GroundReversing;
        return speed > 0.5f ? GroundTaxi : GroundStanding;
    }

    /// <summary>Sinking faster than this an aeroplane is on its way down, m/s.</summary>
    public const float SinkingMps = -0.2f;

    /// <summary>
    /// The lever an aeroplane is being flown at: its ground run's on its wheels; a piston aeroplane's closed
    /// throttle in the flare (landing, within a wingspan of the runway, on the way down), where at approach
    /// power its exhaust stood within 5 dB of the touchdown in the squeal's band; its flight path's otherwise.
    /// </summary>
    /// <param name="run">Its ground run while it is on its wheels, or null.</param>
    /// <param name="landing">Coming down onto a runway.</param>
    /// <param name="heightOverRunway">Its height over where it will touch down, m.</param>
    public static float AircraftLever(AircraftProfile air, AircraftGroundRun? run, bool landing, float heightOverRunway, Vector3 velocity)
    {
        if (run != null) return GroundLever(run.State, run.Speed, air.Power);
        if (air.Power == AircraftPower.Piston && landing && heightOverRunway < air.WingspanMetres && velocity.Y < SinkingMps)
            return PistonIdle;
        return FlightLever(velocity);
    }
}
