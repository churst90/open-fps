namespace OpenFPS.Common;

/// <summary>
/// A level crossing's gate mechanism: the arm, the motor that lifts it and the stop it lands on.
///
/// What a gate does is fixed by the rules a crossing is built to (49 CFR 234.223 and the AREMA
/// signal manual): the arm starts down no sooner than three seconds after the lights and bells start,
/// is down in ten to fifteen seconds, and is horizontal at least five seconds before the train. It goes
/// down under its own weight — the counterweights leave it a little arm-heavy — with the motor run as a
/// brake (snubbing) so it settles onto its rest rather than falling onto it; and it is driven up by the
/// motor through a reduction gearbox, faster than it came down, until it reaches the vertical and the
/// hold-clear device takes it.
///
/// What is heard is the mechanism in its case at the foot of the mast: a small DC motor (a commutator
/// buzz and the first gear mesh, both following its speed), its brushes, and the clunk of the arm
/// arriving at each end. Levels are an assumption, to be judged by ear: no measured figure for a gate
/// mechanism was found. The motor's figure is a small geared DC motor in a closed steel case.
/// </summary>
public sealed record CrossingGateSpec
{
    public required string Name { get; init; }
    /// <summary>From the bells starting to the arm starting down, seconds (no less than 3).</summary>
    public float DelaySeconds { get; init; } = 4f;
    /// <summary>Vertical to horizontal, seconds (10 to 15).</summary>
    public float DownSeconds { get; init; } = 12f;
    /// <summary>Horizontal to vertical, seconds.</summary>
    public float UpSeconds { get; init; } = 9f;
    /// <summary>The motor at full speed, rpm: a 12 V gear motor's.</summary>
    public float MotorRpm { get; init; } = 1800f;
    /// <summary>Commutator segments: the buzz is this many pulses a turn.</summary>
    public int CommutatorBars { get; init; } = 12;
    /// <summary>Teeth on the motor's pinion: the first gear mesh is this many a turn.</summary>
    public int PinionTeeth { get; init; } = 11;
    /// <summary>The case's own resonance, Hz: a steel box about 0.4 m across.</summary>
    public float CaseHz { get; init; } = 650f;
    /// <summary>The motor driving the arm up, dB SPL at a metre from the case.</summary>
    public float MotorDb { get; init; } = 62f;
    /// <summary>The arm landing on its rest or reaching its stop, peak dB SPL at a metre.</summary>
    public float ClunkDb { get; init; } = 86f;

    public static CrossingGateSpec Standard => new() { Name = "crossing_gate" };

    public static IReadOnlyDictionary<string, Func<CrossingGateSpec>> Presets { get; } =
        new Dictionary<string, Func<CrossingGateSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            ["crossing_gate"] = () => Standard,
        };

    public static CrossingGateSpec ByName(string name)
        => Presets.TryGetValue(name, out var make) ? make() : throw new ArgumentException($"No crossing gate preset '{name}'.");

    /// <summary>What a gate is placed at, for the earshot: the motor's level.</summary>
    public float SourceLevelDb => MotorDb;
}

/// <summary>
/// Where a gate's arm is and what its motor is doing, from the crossing's one signal: closed or not.
/// Both the client's voice and a test run the same motion.
/// </summary>
public sealed class GateArm
{
    public enum Phase { Up, Waiting, Lowering, Down, Raising }

    public CrossingGateSpec Spec { get; }
    public Phase State { get; private set; }
    /// <summary>1 vertical (open), 0 horizontal (across the road).</summary>
    public float Raised { get; private set; } = 1f;
    /// <summary>The motor's speed as a share of full: up while it drives the arm up, the snubbing
    /// generator's speed while the arm falls, zero at rest.</summary>
    public float MotorSpeed { get; private set; }
    /// <summary>Whether the motor is driving (lifting) rather than braking a falling arm.</summary>
    public bool Driving { get; private set; }
    /// <summary>Set for the one update the arm reaches its rest (true) or its stop at the top (false); null otherwise.</summary>
    public bool? Arrived { get; private set; }

    private float _t;
    private float _from;

    public GateArm(CrossingGateSpec spec, bool closed = false)
    {
        Spec = spec;
        if (closed) { State = Phase.Down; Raised = 0f; }
    }

    public void Update(bool closed, float dt)
    {
        Arrived = null;
        _t += dt;
        switch (State)
        {
            case Phase.Up:
                MotorSpeed = 0f;
                if (closed) { State = Phase.Waiting; _t = 0f; }
                break;
            case Phase.Waiting:
                if (!closed) { State = Phase.Up; break; }
                if (_t >= Spec.DelaySeconds) { State = Phase.Lowering; _t = 0f; _from = Raised; }
                break;
            case Phase.Lowering:
            {
                if (!closed) { State = Phase.Raising; _t = 0f; _from = Raised; Driving = true; break; }
                // Under its own weight, snubbed: a smooth start and a smooth arrival, the whole way in
                // DownSeconds (from part way up, in that share of it).
                float span = MathF.Max(0.1f, Spec.DownSeconds * _from);
                float f = Math.Clamp(_t / span, 0f, 1f);
                Raised = _from * 0.5f * (1f + MathF.Cos(MathF.PI * f));
                MotorSpeed = MathF.Sin(MathF.PI * f) * 0.6f;
                Driving = false;
                if (f >= 1f) { State = Phase.Down; Raised = 0f; MotorSpeed = 0f; Arrived = true; }
                break;
            }
            case Phase.Down:
                MotorSpeed = 0f;
                if (!closed) { State = Phase.Raising; _t = 0f; _from = Raised; Driving = true; }
                break;
            case Phase.Raising:
            {
                if (closed) { State = Phase.Lowering; _t = 0f; _from = Raised; Driving = false; break; }
                // Driven: up to speed in a third of a second, steady, and easing into the stop.
                float span = MathF.Max(0.1f, Spec.UpSeconds * (1f - _from));
                float f = Math.Clamp(_t / span, 0f, 1f);
                Raised = _from + (1f - _from) * f;
                float ramp = MathF.Min(1f, _t / 0.3f) * MathF.Min(1f, (span - _t) / 0.5f + 0.15f);
                MotorSpeed = Math.Clamp(ramp, 0f, 1f);
                Driving = true;
                if (f >= 1f) { State = Phase.Up; Raised = 1f; MotorSpeed = 0f; Arrived = false; Driving = false; }
                break;
            }
        }
    }
}
