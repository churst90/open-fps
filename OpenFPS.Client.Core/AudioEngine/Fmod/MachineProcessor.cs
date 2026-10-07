using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Yard;
using OpenFPS.Client.AudioEngine.Core.Aircraft;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Anything the render pool can keep ahead of the mixer.
///
/// The pool was written for vehicle engines and there is nothing about it that is a vehicle: it owns
/// dedicated threads, walks a nearest-first list, and calls one method that tops a ring buffer up.
/// A machine that stands still needs exactly that and nothing else, so it asks for exactly that.
/// </summary>
public interface IRenderedVoice
{
    /// <summary>Renders ahead until this voice is one lead in front of the mixer. Worker thread.</summary>
    void Produce();
}

/// <summary>
/// A physical synthesiser given a voice: one output, rendered ahead of the mixer into a ring.
///
/// WHY THIS EXISTS. Three models were measured and approved by ear in September and then could not
/// be placed on a map — standing machines, aircraft and trains — because nothing gave a voice to
/// anything that was not a vehicle engine. A vehicle's engine gets one from ClientAudioSystem; a
/// mower, an airliner and a condenser got none. This is that voice, and it is deliberately the
/// smaller sibling of <see cref="EngineVoiceState"/> rather than a new idea.
///
/// What it covers is every model whose output is ONE pressure at one point: SmallMachineSynth and
/// AircraftSynth both are. A train is not — it is a line of bogies, each radiating from its own
/// place along the consist — and it will need this plus a way to place several taps along a curve.
///
/// THE RING RULES ARE THE ENGINE'S RULES, and they are repeated here rather than shared because the
/// two voices differ in shape — an engine has two outlets, a readback history for echoes and
/// borrowed voices, and a front/back crossfade; this has one tap and none of that. What must NOT
/// differ is the behaviour, so each rule is written out with the fault it exists to prevent:
///
///   * A STARVED BLOCK IS A GAP, NEVER A DELAY. Consume advances the play position by the whole
///     block whether or not the ring could fill it, and Produce resyncs to wherever the consumer
///     got to. Taking only what is there and keeping your place plays the voice slow — 900 of 1024
///     samples is 88 % speed, a tone and a half flat — and walks the sound further and further
///     behind the thing making it. It does not sound like a dropout.
///   * A NEW VOICE HANDS OUT SILENCE UNTIL PRIMED, and is warmed with its output discarded. A cold
///     machine's first samples are a cabinet or a duct pressurising from nothing, and sixty of those
///     at a map load is the crackle this design exists to avoid.
///   * NEVER CUT A VOICE. There is no zero-crossing to stop at — the crank is wherever it is — so
///     it fades on an envelope. A city block's worth of air conditioners passing in and out of the
///     voice budget as you walk would otherwise click every few steps.
///   * NOTHING IN Consume MAY BLOCK OR SYNTHESIZE. It runs on the mixer thread with the deadline of
///     the whole mix running down.
/// </summary>
public abstract class PhysicalVoiceState : IRenderedVoice, IGuardedUnit
{
    /// <summary>The non-finite guard's flag and name for this unit (NonFinite).</summary>
    public NonFiniteUnit Guard { get; } = new();

    /// <summary>The mixer callback's mono buffer, made with the voice so the callback never allocates.</summary>
    internal readonly float[] MixScratch = new float[DspCallback.MaxBlock];

    /// <summary>Running at all. Game thread writes.</summary>
    public volatile bool Running = true;

    /// <summary>Where the voice's own envelope is heading, 0 or 1. Game thread writes.</summary>
    public volatile float TargetEnvelope = 1f;

    /// <summary>True once a fade-out has finished and the voice can be released.</summary>
    public volatile bool FadedOut;

    /// <summary>Brings a voice that was fading back to full. Its absence is a source that goes
    /// silent for ever the moment it loses and regains a slot — see the engine's Revive.</summary>
    public void Revive()
    {
        TargetEnvelope = 1f;
        FadedOut = false;
    }

    /// <summary>The pressure that maps to full scale, pascals. Derived exactly as a vehicle's is —
    /// the declared level plus ONE shared headroom — so that a machine, an aircraft and a car arrive
    /// at Loudness.Place on the same terms and their relative loudness is their levels rather than
    /// the shape of their pulses.</summary>
    public float PascalsAtFullScale { get; }

    public float SampleRate { get; }

    /// <summary>One sample of the model, in pascals at a metre. Producer thread only.</summary>
    protected abstract float StepSynth();

    /// <summary>
    /// Whatever moves on the scale of seconds rather than samples — a governor's load, a
    /// thermostat, a power lever. Called once per rendered block with the voice's own elapsed time
    /// AND how much of it this block is, because a block is eleven milliseconds and a governor
    /// responds at a few hertz.
    ///
    /// <paramref name="dt"/> is not optional dressing: anything that slews here slews per CALL, and
    /// a rate written per call rather than per second is a rate that changes with the block size.
    /// </summary>
    protected abstract void Control(float seconds, float dt);

    /// <summary>Hands the model the listener's place in its own frame. Producer thread only.</summary>
    protected abstract void PushListener(Vector3 frame);

    private const int RingBits = 16;                       // about a second and a half at 44.1 kHz
    private readonly float[] _ring = new float[1 << RingBits];
    private long _written;                                 // producer writes, consumer only reads
    private long _played;                                  // consumer writes, producer only reads
    private int _producing;
    private double _seconds;


    private long _consumedSincePrimed;
    private float _lastOut;
    private float _envelope;

    private volatile float _leadSeconds = MinLeadSeconds;
    public const float MinLeadSeconds = 0.25f;
    public const float MaxLeadSeconds = 0.7f;

    private volatile bool _primed;

    protected const float WarmupSeconds = 0.08f;
    private bool _warmed;

    private float _listenerX, _listenerY, _listenerZ;
    private volatile bool _listenerKnown;

    protected PhysicalVoiceState(float sourceLevelDb, float sampleRate)
        : this(sourceLevelDb, sampleRate, VehicleProfile.PeakHeadroomDb) { }

    /// <summary>
    /// A voice whose peaks stand further over its level than the fleet's shared headroom allows: a
    /// fire, whose loud crackles are 40 dB over its mean. It renders with that much room, so its
    /// peaks are not squared off on the soft ceiling, and the mixer gives the difference back as gain
    /// (<see cref="HeadroomGain"/>), so it is still PLACED by its level — declaring it by its peaks
    /// instead would have the loudness law play it fourteen decibels under what it is.
    /// </summary>
    protected PhysicalVoiceState(float sourceLevelDb, float sampleRate, float headroomDb)
    {
        SampleRate = sampleRate;
        Ground = new OpenFPS.Client.AudioEngine.Acoustics.GroundReflection(sampleRate);
        PascalsAtFullScale = 20e-6f * MathF.Pow(10f, (sourceLevelDb + headroomDb) / 20f);
    }

    /// <summary>The channel gain that gives back a voice's extra headroom over the shared one.</summary>
    public static float HeadroomGain(float headroomDb)
        => MathF.Pow(10f, (MathF.Max(VehicleProfile.PeakHeadroomDb, headroomDb) - VehicleProfile.PeakHeadroomDb) / 20f);

    /// <summary>Tells the model where the listener is, in its own frame, so a cabinet with a fan on
    /// top and a grille down one side — or a jet that radiates aft and a fan that radiates forward —
    /// each comes from its own place.</summary>
    public void SetListener(Vector3 frame)
    {
        Volatile.Write(ref _listenerX, frame.X);
        Volatile.Write(ref _listenerY, frame.Y);
        Volatile.Write(ref _listenerZ, frame.Z);
        _listenerKnown = true;
    }

    /// <summary>See <see cref="EngineVoiceState.Produce"/> — same contract, same reasons. Takes no
    /// lock and holds nothing the mixer could ever want.</summary>
    public void Produce()
    {
        if (Interlocked.CompareExchange(ref _producing, 1, 0) != 0) return;
        try
        {
            if (!_warmed)
            {
                _warmed = true;
                int warm = (int)(WarmupSeconds * SampleRate);
                while (warm > 0) { int n = Math.Min(512, warm); Synthesize(n); warm -= n; }
                Volatile.Write(ref _played, Volatile.Read(ref _written));
            }

            long played = Volatile.Read(ref _played);
            if (played > Volatile.Read(ref _written)) Volatile.Write(ref _written, played);

            int room = _ring.Length - 8;
            int want = Math.Min((int)(_leadSeconds * SampleRate), room);
            while (Volatile.Read(ref _written) - Volatile.Read(ref _played) < want)
                Synthesize(Math.Min(512, want));

            if (!_primed && Volatile.Read(ref _written) - Volatile.Read(ref _played) >= want / 2) _primed = true;

            float lead = _leadSeconds;
            if (lead > MinLeadSeconds) _leadSeconds = MathF.Max(MinLeadSeconds, lead - 0.0005f);
        }
        finally { Volatile.Write(ref _producing, 0); }
    }

    /// <summary>The ground between this machine and the listener (see GroundReflection). Created on
    /// first use, before any mixer call, at the voice's own rate.</summary>
    public readonly OpenFPS.Client.AudioEngine.Acoustics.GroundReflection Ground;

    /// <summary>Hands the mixer its block out of what the producer has already rendered. Mixer
    /// thread. NOTHING IN HERE MAY BLOCK OR SYNTHESIZE.</summary>
    public void Consume(Span<float> mono)
    {
        long at = Volatile.Read(ref _played);
        long avail = _primed ? Volatile.Read(ref _written) - at : 0;
        int take = (int)Math.Clamp(avail, 0, mono.Length);
        int mask = _ring.Length - 1;
        for (int i = 0; i < take; i++) mono[i] = Ground.Process(_ring[(int)((at + i) & mask)]);
        if (take > 0)
        {
            _lastOut = mono[take - 1];
            if (_primed) _consumedSincePrimed += take;
        }
        // Wall clock, always: the whole block is gone whether or not it had audio in it.
        Volatile.Write(ref _played, at + mono.Length);
        if (take == mono.Length) return;

        int ramp = Math.Min(mono.Length - take, 64);
        for (int i = 0; i < ramp; i++) mono[take + i] = _lastOut * (1f - (i + 1) / (float)ramp);
        mono[(take + ramp)..].Clear();
        _lastOut = 0f;

        if (!_primed) return;
        Interlocked.Increment(ref GlobalStarves);
        float lead = _leadSeconds;
        if (_consumedSincePrimed > lead * SampleRate)
            _leadSeconds = MathF.Min(MaxLeadSeconds, lead * 1.35f);
    }

    /// <summary>Every such voice's starves since the client started, for the mixer load line.</summary>
    internal static int GlobalStarves;

    /// <summary>Renders a block synchronously. OFFLINE USE ONLY — the lab, the spikes, the tests.</summary>
    public void Render(Span<float> mono)
    {
        long at = Volatile.Read(ref _played);
        long avail = Volatile.Read(ref _written) - at;
        if (avail < mono.Length) Synthesize((int)(mono.Length - avail));
        int mask = _ring.Length - 1;
        for (int i = 0; i < mono.Length; i++) mono[i] = _ring[(int)((at + i) & mask)];
        Volatile.Write(ref _played, at + mono.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Synthesize(int count)
    {
        float gain = 1f / MathF.Max(1e-6f, PascalsAtFullScale);
        float envStep = 1f / (0.06f * SampleRate);
        float envTarget = TargetEnvelope;
        int mask = _ring.Length - 1;
        long w = _written;
        if (_listenerKnown)
            PushListener(new Vector3(Volatile.Read(ref _listenerX),
                                     Volatile.Read(ref _listenerY),
                                     Volatile.Read(ref _listenerZ)));
        float dt = count / SampleRate;
        _seconds += dt;
        Control((float)_seconds, dt);

        for (int i = 0; i < count; i++)
        {
            _envelope += Math.Clamp(envTarget - _envelope, -envStep, envStep);
            float y = StepSynth() * gain * _envelope;
            // The soft ceiling the physics needs: a blade hitting something, or a compressor stall,
            // can spike past any fixed reference, and a step at full scale is a click.
            _ring[(int)(w & mask)] = SoftCeiling.Apply(y);
            w++;
        }
        Volatile.Write(ref _written, w);
        if (envTarget <= 0f && _envelope <= 1e-4f) FadedOut = true;
    }
}

/// <summary>
/// A machine that stands in one place and runs: a window air conditioner, a rooftop condenser, a
/// mower in a garden.
/// </summary>
public sealed class MachineVoiceState : PhysicalVoiceState
{
    public readonly SmallMachineSpec Spec;
    public readonly SmallMachineSynth Machine;

    // ── What the machine is doing, which nothing on the wire tells it ────────────────────────────
    //
    // A mower's load is how thick the grass is and an air conditioner's compressor is on when its
    // thermostat calls for it. Neither is world state anybody else needs to agree about, and making
    // them world state would mean a networked component for "how deep is the grass" — so they are
    // driven here, from a seed taken from the ENTITY ID. Two clients hearing the same mower hear the
    // same walk through the same grass, and forty window units on one wall are forty machines rather
    // than one machine forty times, which is what a chorus of identical waveforms would be.
    //
    // An air conditioner's is the WEATHER: its thermostat runs it for the share of the time the
    // outdoor air asks (Thermostat), and its compressor pumps harder, and so turns slower, the hotter
    // the air its condenser rejects heat into (CompressorSpec.LoadAt). What is its own: where in its
    // cycle it is, how well its house holds the cool, a refrigerant charge a few per cent either way
    // (the pump's load wanders slowly with it), and a fan motor a per cent or two off its nameplate.
    private readonly float _loadBase, _loadSwing, _loadHz, _loadPhase;
    private readonly Thermostat? _thermostat;
    private readonly float _fanTrim, _wanderHz1, _wanderHz2, _wanderPhase1, _wanderPhase2;

    /// <summary>
    /// The air round the machine, °C: what its thermostat and its condenser answer to. NaN (the
    /// default) takes the world's, AudioPhysics.CurrentAirCelsius, which the provider keeps from the
    /// server's weather; an instrument or a test sets it outright.
    /// </summary>
    public float AmbientCelsius = float.NaN;

    /// <summary>Whether the thermostat is calling for the compressor, after the last render.</summary>
    public bool CompressorCalled => _thermostat?.Calling ?? false;

    public MachineVoiceState(SmallMachineSpec spec, float sampleRate, int entityId, int seed)
        : base(spec.SourceLevelDb, sampleRate)
    {
        Spec = spec;
        // Its noise is its own too: two of one model given one seed are still two machines.
        Machine = new SmallMachineSynth(spec, sampleRate, unchecked(seed * 31 + entityId));

        var rng = new Random(entityId * 2654435761u.GetHashCode());
        if (spec.Compressor != null)
        {
            _thermostat = new Thermostat(spec.Thermostat ?? new ThermostatSpec(), rng.Next());
            // A few per cent of load either way, over a minute or two: charge, a dirty coil, the sun
            // coming off the cabinet. Two incommensurate swings, so it never repeats.
            _wanderHz1 = 0.008f + (float)rng.NextDouble() * 0.006f;
            _wanderHz2 = 0.019f + (float)rng.NextDouble() * 0.011f;
            _wanderPhase1 = (float)rng.NextDouble() * MathF.Tau;
            _wanderPhase2 = (float)rng.NextDouble() * MathF.Tau;
        }
        // A permanent-split-capacitor fan motor of one model turns within a per cent or two of its
        // nameplate, and no two at the same speed.
        _fanTrim = 1f + ((float)rng.NextDouble() * 2f - 1f) * 0.015f;
        // How hard a mower is working, and how much that wanders: the grass, which moves a lot, and is
        // what the governor's droop is FOR — the bog going into a thick patch and the recovery out of it.
        _loadBase = 0.42f;
        _loadSwing = 0.34f;
        _loadHz = 0.19f + (float)rng.NextDouble() * 0.12f;
        _loadPhase = (float)rng.NextDouble() * MathF.Tau;
    }

    /// <summary>
    /// How fast the machine is going over the ground, m/s — the entity's own speed, set from the game
    /// thread. It was a constant 0.95 for anything that mows, so a mower sounded exactly the same
    /// pushing a strip, turning at the end of it and standing waiting: it moved on the server and
    /// nothing in its voice said so. The synth already scales the cutting torque and the stalk rate
    /// with it; it only had to be told the truth. A machine that stands still reads zero, which is
    /// right for a condenser and right for a mower that is not mowing.
    /// </summary>
    public float TargetGroundSpeed
    {
        get => Volatile.Read(ref _targetGroundSpeed);
        set => Volatile.Write(ref _targetGroundSpeed, value);
    }
    private float _targetGroundSpeed;
    private float _groundSpeed;

    protected override void PushListener(Vector3 frame) => Machine.SetListener(frame);

    protected override void Control(float seconds, float dt)
    {
        Machine.Running = Running;
        // Slewed, because the position arrives thirty times a second and a governor hearing a
        // staircase of speeds would hunt on it. A second to get going is about what a push takes.
        _groundSpeed += Math.Clamp(TargetGroundSpeed - _groundSpeed, -1.5f * dt, 1.5f * dt);
        Machine.GroundSpeed = _groundSpeed;
        if (Spec.Cutting != null)
            Machine.Load = Math.Clamp(
                _loadBase + _loadSwing * MathF.Sin(_loadPhase + MathF.Tau * _loadHz * seconds), 0f, 1f);
        Machine.FanSpeedFraction = _fanTrim;
        if (_thermostat != null)
        {
            float air = float.IsNaN(AmbientCelsius) ? OpenFPS.Client.AudioEngine.Core.AudioPhysics.CurrentAirCelsius : AmbientCelsius;
            Machine.CompressorOn = _thermostat.Step(dt, air, AudioClock.Now);
            float wander = 0.6f * MathF.Sin(_wanderPhase1 + MathF.Tau * _wanderHz1 * seconds)
                         + 0.4f * MathF.Sin(_wanderPhase2 + MathF.Tau * _wanderHz2 * seconds);
            Machine.CompressorLoad = CompressorSpec.LoadAt(air) * (1f + 0.04f * wander);
        }
    }

    protected override float StepSynth()
    {
        Machine.Step();
        return Machine.Total;
    }
}

/// <summary>
/// An aircraft, rendered live: an airliner going over, a turboprop on approach, a light single in
/// the circuit, a helicopter.
///
/// The one thing it needs that a standing machine does not is a POWER LEVER, and the lever is a
/// function of what the aeroplane is doing rather than of a clock. It comes from the climb angle:
/// an aircraft going up is at or near full power, one holding height is at cruise, one coming down
/// is at idle with the drag doing the work — and that is why an airliner overhead and one on
/// approach sound completely different when nothing about the aeroplane has changed. The game sets
/// it from the entity's own velocity (see ClientAudioSystem), so it falls out of the flight path
/// rather than being scripted onto it.
/// </summary>
public sealed class AircraftVoiceState : PhysicalVoiceState
{
    public readonly AircraftSynth Aircraft;

    /// <summary>The power lever, 0..1. Game thread writes.</summary>
    public volatile float TargetLever = 1f;

    /// <summary>How hard a rotor is meeting its own wake — a descending helicopter slaps. Ignored by
    /// anything without a rotor. Game thread writes.</summary>
    public volatile float TargetDescending;

    /// <summary>
    /// The aeroplane is on its wheels, and how fast they are going over the ground. Game thread
    /// writes; the render thread turns the EDGE into a touchdown.
    ///
    /// An edge rather than a message, because a message can be sent twice or missed and a wheel
    /// cannot touch down twice. The game thread only reports what is true — wheels down or not —
    /// and the wheels spin up the first render after it becomes true.
    /// </summary>
    public volatile bool TargetOnGround;
    public volatile float TargetGroundSpeed;
    private bool _onGround;

    private float _lever = 1f;

    /// <summary>How long the power lever takes to travel its whole range, seconds.</summary>
    private const float LeverTravelSeconds = 1.5f;

    public AircraftVoiceState(AircraftProfile p, float sampleRate, int seed, float lever = 1f)
        : base(p.SourceLevelDb, sampleRate)
    {
        Aircraft = new AircraftSynth(p, sampleRate, seed);
        // Already at this power, not spooling up to it. See AircraftSynth.PlaceAtLever.
        _lever = Math.Clamp(lever, 0f, 1f);
        TargetLever = _lever;
        Aircraft.PlaceAtLever(_lever);
    }

    protected override void PushListener(Vector3 frame) => Aircraft.SetListener(frame);

    protected override void Control(float seconds, float dt)
    {
        // Slewed, not stepped. A turbine spools on its own time constant inside the model, but the
        // LEVER is a pilot's hand, and a network update that steps it is a hand that slams it.
        // A second and a half from idle to full, which is what a thrust lever takes — PER SECOND,
        // not per call: written per call it came out at two thirds of full travel every eleven
        // milliseconds, which is a step with extra arithmetic in front of it.
        float step = dt / LeverTravelSeconds;
        _lever += Math.Clamp(TargetLever - _lever, -step, step);
        Aircraft.Lever = Running ? Math.Clamp(_lever, 0f, 1f) : 0f;
        Aircraft.Descending = TargetDescending;

        // The wheels. Touching is an event and rolling is a state, and only the first transition
        // is the touchdown — everything after it is an aeroplane on a runway.
        bool down = TargetOnGround;
        if (down && !_onGround) Aircraft.Touchdown(TargetGroundSpeed);
        else if (!down && _onGround) Aircraft.Airborne();
        else if (down) Aircraft.GroundSpeed = TargetGroundSpeed;
        _onGround = down;
    }

    protected override float StepSynth()
    {
        Aircraft.Step();
        return Aircraft.Total;
    }
}

/// <summary>
/// A siren head, as its own voice on the car that carries it.
///
/// It is NOT folded into the engine voice, and that is a level argument rather than a tidiness
/// one. A patrol siren makes 130 dB at a metre and the car it is bolted to makes 95; one shared
/// <see cref="PhysicalVoiceState.PascalsAtFullScale"/> would have to be set for one of them, and
/// either choice is wrong — set it for the siren and the engine renders 35 dB under full scale and
/// vanishes, set it for the engine and the siren arrives as a square wave. Two sources 35 dB apart
/// need two references, which is what two voices are.
///
/// It is also physically a different place on the car: the horn is behind the grille and the
/// tailpipe is under the back bumper, and once you are close enough to tell, they separate — the
/// same reason the engine already has a front tap.
/// </summary>
public sealed class SirenVoiceState : PhysicalVoiceState
{
    public readonly ElectronicSiren Siren;

    /// <summary>Which sound the head is making. Game thread writes.</summary>
    public volatile int TargetMode = (int)SirenMode.Off;

    public SirenVoiceState(SirenSpec spec, float sampleRate)
        : base(spec.SourceLevelDb, sampleRate)
    {
        Siren = new ElectronicSiren(spec, sampleRate);
    }

    protected override void PushListener(Vector3 frame) => Siren.SetListener(frame);

    protected override void Control(float seconds, float dt)
    {
        // Switching modes is not slewed and must not be: a siren head changes sound between one
        // sweep and the next, and the oscillator carries straight on at the new rate. The synth
        // keeps its phase across the change, so there is nothing to smooth.
        Siren.Mode = Running ? (SirenMode)TargetMode : SirenMode.Off;
    }

    protected override float StepSynth()
    {
        Siren.Step();
        return Siren.Output;
    }
}

/// <summary>
/// A vehicle's horn, blown in the rhythm the server sent — see <see cref="Honk"/>.
///
/// The horn is the model the vehicle carries: an air horn is the approved <see cref="ChimeHorn"/>,
/// found in the <see cref="ModelLibrary"/> as a train's is, so an authored horn is the one that
/// blows; an electric one is an <see cref="ElectricHorn"/>. The rhythm is played in the voice's own
/// time, so a tap is exactly as long as the driver's thumb was on the button however the frames
/// fall. The warm-up the voice discards before it is heard is taken off the front, or it would eat
/// the first eighty milliseconds of every tap.
/// </summary>
public sealed class HornVoiceState : PhysicalVoiceState
{
    public readonly float[] Pattern;
    private readonly ChimeHorn? _air;
    private readonly ElectricHorn? _electric;

    public HornVoiceState(string horn, float[] pattern, float sampleRate, int seed)
        : base(Honk.LevelDb(horn), sampleRate)
    {
        Pattern = pattern;
        int colon = horn.IndexOf(':');
        string kind = colon > 0 ? horn[..colon] : "";
        string preset = colon > 0 ? horn[(colon + 1)..] : horn;
        if (string.Equals(kind, "air", StringComparison.OrdinalIgnoreCase))
            _air = new ChimeHorn(ModelLibrary.Horn(preset), sampleRate, seed);
        else
            _electric = new ElectricHorn(ElectricHornSpec.ByName(preset), sampleRate, seed);
    }

    protected override void PushListener(Vector3 frame)
    {
        _air?.SetListener(frame);
        _electric?.SetListener(frame);
    }

    protected override void Control(float seconds, float dt)
    {
        bool on = Running && Honk.BlowingAt(Pattern, seconds - WarmupSeconds);
        if (_air != null) _air.Blowing = on;
        if (_electric != null) _electric.Blowing = on;
    }

    protected override float StepSynth()
    {
        if (_air != null) { _air.Step(); return _air.Out; }
        _electric!.Step();
        return _electric.Out;
    }
}

/// <summary>
/// A struck bell that rings while it is told to — a level crossing's gong.
///
/// The simplest physical voice there is: the bell model already knows how to be rung over and over
/// (<see cref="StruckBell.Ringing"/>), so all this does is carry the server's word for whether it
/// should be. That word matters because a crossing bell is the first sound in this world that a
/// client CANNOT work out for itself: it rings because of where a train is on a line the listener
/// may be a kilometre from and cannot see. Everything else — an engine's revs, a siren's mode, an
/// aeroplane's power — is derivable from what the client can already observe. This one is not, so
/// it comes down the wire as SoundEmitterComponent.SynthRunning.
/// </summary>
public sealed class BellVoiceState : PhysicalVoiceState
{
    public readonly StruckBell Bell;

    /// <summary>Rendered with the bell's own headroom (StruckBellSpec.PeakHeadroomDb): under the
    /// shared one the soft ceiling took ten to twelve decibels off every blow and one and a half to
    /// three off the bell's level. An engine's backfire is one transient rounded; a bell is nothing
    /// but its blows.</summary>
    public BellVoiceState(StruckBellSpec spec, float sampleRate, int seed)
        : base(spec.ReferenceDb, sampleRate, spec.PeakHeadroomDb)
    {
        Bell = new StruckBell(spec, sampleRate, seed);
    }

    protected override void PushListener(Vector3 frame) { }

    protected override void Control(float seconds, float dt) => Bell.Ringing = Running;

    protected override float StepSynth()
    {
        Bell.Step();
        return Bell.Out;
    }
}

/// <summary>
/// A level crossing's gate mechanism (CrossingGateSpec): the arm going down under its own weight with
/// the motor braking it, the motor driving it back up, and the clunk at each end. Like the bell, its
/// Running flag is the server's word that the crossing is closed (SoundEmitterComponent.SynthRunning),
/// and the arm's motion follows from that one signal by the same rules a real gate keeps (GateArm).
///
/// The motor is a small DC gear motor in a steel case: a commutator buzz (a pulse per segment per turn)
/// and the pinion's mesh, eleven teeth against twelve segments so the two beat, brush noise, all
/// following the motor's speed, through the case's resonance. Driving the arm up it works; braking the
/// arm on the way down it whirs more quietly. Allocation-free once built.
/// </summary>
public sealed class GateVoiceState : PhysicalVoiceState
{
    public readonly CrossingGateSpec Spec;
    public readonly GateArm Arm;
    private readonly float _rate;
    private readonly float _motorPa, _clunkPa;
    private double _phase;
    private float _speed, _speedTarget, _work;
    private uint _noise = 0x2545F491u;
    private float _bpX1, _bpX2, _bpY1, _bpY2;
    private readonly float _b0, _b2, _a1, _a2;
    // The clunk: three struck modes of the mast, the arm's hub and the case.
    private float _clunkT = -1f, _clunkAmp;

    public GateVoiceState(CrossingGateSpec spec, float sampleRate, bool closed)
        : base(spec.MotorDb, sampleRate, spec.ClunkDb - spec.MotorDb + 6f)
    {
        Spec = spec;
        Arm = new GateArm(spec, closed);
        _rate = sampleRate;
        _motorPa = 20e-6f * MathF.Pow(10f, spec.MotorDb / 20f);
        _clunkPa = 20e-6f * MathF.Pow(10f, spec.ClunkDb / 20f);
        // The case: a two-pole band-pass at its resonance, Q 3 (RBJ cookbook, constant peak gain).
        float w0 = MathF.Tau * spec.CaseHz / sampleRate, alpha = MathF.Sin(w0) / (2f * 3f), a0 = 1f + alpha;
        _b0 = alpha / a0; _b2 = -alpha / a0; _a1 = -2f * MathF.Cos(w0) / a0; _a2 = (1f - alpha) / a0;
    }

    protected override void PushListener(Vector3 frame) { }

    protected override void Control(float seconds, float dt)
    {
        Arm.Update(Running, dt);
        _speedTarget = Arm.MotorSpeed;
        _work = Arm.Driving ? 1f : 0.45f;
        if (Arm.Arrived is { } down)
        {
            // Landing on the rest is the heavier blow: the arm's whole weight. Reaching the top, the
            // counterweights take most of it.
            _clunkT = 0f;
            _clunkAmp = _clunkPa * (down ? 1f : 0.6f);
        }
    }

    protected override float StepSynth()
    {
        float dt = 1f / _rate;
        // The motor spins up and down with its own inertia, about 50 ms.
        _speed += (_speedTarget - _speed) * MathF.Min(1f, dt / 0.05f);
        float outPa = 0f;
        if (_speed > 1e-3f)
        {
            float rps = Spec.MotorRpm / 60f * _speed;
            _phase += rps * dt;
            if (_phase > 1e6) _phase -= 1e6;
            float turn = (float)(_phase - Math.Floor(_phase)) * MathF.Tau;
            float bars = Spec.CommutatorBars, teeth = Spec.PinionTeeth;
            float comm = MathF.Sin(bars * turn) + 0.5f * MathF.Sin(2f * bars * turn) + 0.33f * MathF.Sin(3f * bars * turn)
                       + 0.25f * MathF.Sin(4f * bars * turn);
            float mesh = 0.5f * MathF.Sin(teeth * turn);
            _noise = _noise * 1664525u + 1013904223u;
            float brush = ((_noise >> 8) / 8388608f - 1f) * 0.3f;
            float raw = comm + mesh + brush;
            // Through the case's resonance, and a little straight out of the louvres.
            float y = _b0 * raw + _b2 * _bpX2 - _a1 * _bpY1 - _a2 * _bpY2;
            _bpX2 = _bpX1; _bpX1 = raw; _bpY2 = _bpY1; _bpY1 = y;
            // About 1 RMS at full speed for the raw sum: the motor's level, as hard as it is working.
            outPa = _motorPa * _speed * _work * (0.7f * y * 2.2f + 0.3f * raw);
        }
        if (_clunkT >= 0f)
        {
            float t = _clunkT;
            _clunkT += dt;
            if (t > 0.4f) _clunkT = -1f;
            _noise = _noise * 1664525u + 1013904223u;
            float n = (_noise >> 8) / 8388608f - 1f;
            float s = 0.55f * MathF.Sin(MathF.Tau * 140f * t) * MathF.Exp(-t / 0.06f)
                    + 0.35f * MathF.Sin(MathF.Tau * 420f * t) * MathF.Exp(-t / 0.025f)
                    + 0.25f * MathF.Sin(MathF.Tau * 1600f * t) * MathF.Exp(-t / 0.008f)
                    + 0.3f * n * MathF.Exp(-t / 0.003f);
            outPa += _clunkAmp * s * MathF.Min(1f, t / 0.0005f);
        }
        return outPa;
    }
}

/// <summary>
/// The FMOD side of a physical voice — machine or aircraft: a read callback that copies out of the
/// ring and nothing else.
/// Mirrors <c>EngineProcessor</c>, which is the point.
/// </summary>
public static class MachineProcessor
{
    /// <summary>The non-finite guard's flag for a state that is not an IGuardedUnit.</summary>
    private static int _nonFiniteOther;

    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, PhysicalVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new DSP_DESCRIPTION
        {
            pluginsdkversion = VERSION.number,
            numinputbuffers = 0,
            numoutputbuffers = 1,
            read = _readCallback,
        };
        RESULT res = system.createDSP(ref desc, out dsp);
        if (res == RESULT.OK)
        {
            handle = GCHandle.Alloc(state);
            dsp.setUserData(GCHandle.ToIntPtr(handle));
        }
        else handle = default;
        return res;
    }

    /// <summary>
    /// NOTHING MAY ESCAPE A DSP CALLBACK — see the same guard on EngineProcessor for what happens
    /// when one does. This one was copied from the engine's, gap and all: the line that actually
    /// throws, `GCHandle.FromIntPtr(userData).Target`, was outside the only try in the method.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        try
        {
            var r = ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
            NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "machine voice", ref _nonFiniteOther);
            return r;
        }
        catch (Exception ex)
        {
            DspFault.Record("MachineProcessor", ex);
            unsafe
            {
                if (outchannels == 0) outchannels = 1;
                float* outBuf = (float*)outbuffer;
                for (int i = 0; i < (int)length * outchannels; i++) outBuf[i] = 0f;
            }
            return RESULT.OK;
        }
    }

    private static RESULT ReadCallbackCore(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                           uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        if (userData == IntPtr.Zero) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var state = (PhysicalVoiceState?)GCHandle.FromIntPtr(userData).Target;
        if (state == null) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }

        if (outchannels == 0) outchannels = 1;
        int ch = outchannels;
        int n = (int)length;
        if (n > state.MixScratch.Length) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var mono = state.MixScratch.AsSpan(0, n);
        try { state.Consume(mono); }
        catch { mono.Clear(); }

        unsafe
        {
            float* outBuf = (float*)outbuffer;
            for (int i = 0; i < n; i++)
            {
                float v = mono[i];
                for (int c = 0; c < ch; c++) outBuf[i * ch + c] = v;
            }
        }
        return RESULT.OK;
    }
}
