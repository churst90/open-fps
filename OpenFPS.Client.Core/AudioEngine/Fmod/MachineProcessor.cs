using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Yard;
using OpenFPS.Client.AudioEngine.Core.Aircraft;
using OpenFPS.Client.AudioEngine.Core.Signals;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>Anything the render pool can keep ahead of the mixer.</summary>
public interface IRenderedVoice
{
    /// <summary>Renders ahead until this voice is one lead in front of the mixer. Worker thread.</summary>
    void Produce();
}

/// <summary>
/// A physical synthesiser given a voice: one output, rendered ahead of the mixer into a ring. The
/// smaller sibling of <see cref="EngineVoiceState"/> for any model whose output is one pressure at one
/// point (standing machines, aircraft, sirens, horns, bells, a train's taps). Its ring rules are the
/// engine's, written out again because the two differ in shape; the behaviour must not differ:
/// <list type="bullet">
/// <item>A starved block is a gap, never a delay: Consume advances by the whole block and Produce
/// resyncs to it. Keeping your place plays the voice slow (900 of 1024 samples is 88 % speed, a tone and
/// a half flat) and drifts it behind its source (docs/AUDIO_LOAD_DROPOUTS.md).</item>
/// <item>A new voice gives silence until primed, and is warmed with its output discarded: sixty cold
/// cabinets pressurising from nothing at a map load is a crackle.</item>
/// <item>Never cut a voice; it fades on an envelope. Air conditioners passing in and out of the budget
/// as you walk would otherwise click every few steps.</item>
/// <item>Nothing in Consume may block or synthesize: it runs on the mixer thread.</item>
/// </list>
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

    /// <summary>True once a fade-out has been HEARD to its end and the voice can be released.</summary>
    public volatile bool FadedOut;

    /// <summary>Where in the ring the fade-out reached silence, or -1. The producer renders up to 0.7 s
    /// ahead of the mixer, so the fade was rendered long before it played: released when it was rendered,
    /// every voice the budget let go was cut at full level a quarter to three quarters of a second before
    /// its fade would have been heard (the fountain, the crossing bell, a car out of budget "cutting out").</summary>
    private long _silentFrom = -1;

    /// <summary>Brings a fading voice back to full. Without it a source that loses and regains a slot
    /// stays silent for ever (see the engine's Revive).</summary>
    public void Revive()
    {
        TargetEnvelope = 1f;
        FadedOut = false;
        Volatile.Write(ref _silentFrom, -1);
    }

    /// <summary>The pressure that maps to full scale, pascals: the declared level plus the one shared
    /// headroom, as a vehicle's, so machines, aircraft and cars reach Loudness.Place on the same terms
    /// and differ by their levels, not the shape of their pulses.</summary>
    public float PascalsAtFullScale { get; }

    public float SampleRate { get; }

    /// <summary>One sample of the model, in pascals at a metre. Producer thread only.</summary>
    protected abstract float StepSynth();

    /// <summary>
    /// Whatever moves in seconds rather than samples (a governor's load, a thermostat, a power lever),
    /// once per rendered block, with the voice's elapsed time and the block's length. Slew by
    /// <paramref name="dt"/>: a rate written per call changes with the block size.
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

    /// <summary>How far ahead this voice renders, seconds; grown by its own starves, as an engine's
    /// (EngineVoiceState), and eased back while it keeps up.</summary>
    private volatile float _leadSeconds = MinLeadSeconds;
    public const float MinLeadSeconds = 0.25f;
    public const float MaxLeadSeconds = 0.7f;

    private volatile bool _primed;

    protected const float WarmupSeconds = 0.08f;
    private bool _warmed;
    private int _warmLeft = -1;

    /// <summary>How many of <paramref name="want"/> samples the model can make now without waiting on
    /// anything. All of them, unless it renders from something rendered elsewhere (TrainSlotState).</summary>
    protected virtual int Ready(int want) => want;

    /// <summary>The ring skipped <paramref name="samples"/> it never rendered (the voice starved and the
    /// mixer ran on): a model keeping its own timeline moves it on as far.</summary>
    protected virtual void Skipped(long samples) { }

    private float _listenerX, _listenerY, _listenerZ;
    private volatile bool _listenerKnown;

    protected PhysicalVoiceState(float sourceLevelDb, float sampleRate)
        : this(sourceLevelDb, sampleRate, VehicleProfile.PeakHeadroomDb) { }

    /// <summary>
    /// A voice whose peaks stand further over its level than the shared headroom allows (a fire's
    /// crackles are 40 dB over its mean). It renders with that room, so its peaks are not squared off,
    /// and the mixer gives the difference back as gain (<see cref="HeadroomGain"/>) so it is still placed
    /// by its level: declared by its peaks, the loudness law played it fourteen decibels under.
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

    /// <summary>The listener's place in the model's own frame, so a fan on top and a grille down one
    /// side (or a jet radiating aft and a fan forward) each sound from their own side.</summary>
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
                // A model that renders from something else (a train's voice reading its lanes) may not
                // have all of it yet: the warm-up finishes on a later pass.
                if (_warmLeft < 0) _warmLeft = (int)(WarmupSeconds * SampleRate);
                while (_warmLeft > 0)
                {
                    int n = Ready(Math.Min(512, _warmLeft));
                    if (n <= 0) return;
                    Synthesize(n);
                    _warmLeft -= n;
                }
                _warmed = true;
                Volatile.Write(ref _played, Volatile.Read(ref _written));
            }

            long played = Volatile.Read(ref _played);
            long written = Volatile.Read(ref _written);
            if (played > written)
            {
                Skipped(played - written);
                Volatile.Write(ref _written, played);
                // Starved while fading: nothing of it is left to play.
                if (TargetEnvelope <= 0f) FadedOut = true;
            }

            int room = _ring.Length - 8;
            int want = Math.Min((int)(_leadSeconds * SampleRate), room);
            while (Volatile.Read(ref _written) - Volatile.Read(ref _played) < want)
            {
                int n = Ready(Math.Min(512, want));
                if (n <= 0) break;
                Synthesize(n);
            }

            if (!_primed && Volatile.Read(ref _written) - Volatile.Read(ref _played) >= want / 2) _primed = true;

            float lead = _leadSeconds;
            if (lead > MinLeadSeconds) _leadSeconds = MathF.Max(MinLeadSeconds, lead - 0.0005f);
        }
        finally { Volatile.Write(ref _producing, 0); }
    }

    /// <summary>The ground between this machine and the listener (see GroundReflection), made with the
    /// voice at its rate.</summary>
    public readonly OpenFPS.Client.AudioEngine.Acoustics.GroundReflection Ground;

    /// <summary>The mixer's block, out of what the producer has rendered. Mixer thread: nothing here
    /// may block or synthesize.</summary>
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
        long silent = Volatile.Read(ref _silentFrom);
        if (silent >= 0 && at + mono.Length >= silent) FadedOut = true;
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

    /// <summary>Renders a block synchronously. Offline only: the lab and the tests.</summary>
    public void Render(Span<float> mono)
    {
        long at = Volatile.Read(ref _played);
        long avail = Volatile.Read(ref _written) - at;
        // Asked first, so a model that renders from something else gets it rendered (TrainSlotState, Offline).
        if (avail < mono.Length) Synthesize(Math.Max(0, Ready((int)(mono.Length - avail))));
        int mask = _ring.Length - 1;
        for (int i = 0; i < mono.Length; i++) mono[i] = _ring[(int)((at + i) & mask)];
        Volatile.Write(ref _played, at + mono.Length);
        long silent = Volatile.Read(ref _silentFrom);
        if (silent >= 0 && at + mono.Length >= silent) FadedOut = true;
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
        if (envTarget <= 0f && _envelope <= 1e-4f)
        {
            // Released once the mixer has played this far (Consume), not now.
            if (Volatile.Read(ref _silentFrom) < 0) Volatile.Write(ref _silentFrom, w);
        }
        else if (Volatile.Read(ref _silentFrom) >= 0) Volatile.Write(ref _silentFrom, -1);
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

    // What the machine is doing, which nothing on the wire says (a mower's grass, a thermostat's
    // call), is driven here from a seed taken from the entity id: two clients hear the same mower
    // the same way, and forty window units on a wall are forty machines, not one forty times.
    // An air conditioner follows the weather (Thermostat; CompressorSpec.LoadAt: hotter air, harder
    // pumping, slower turning); its own are its cycle phase, its house, a refrigerant charge a few
    // per cent off and a fan motor a per cent or two off its nameplate.
    private readonly float _loadBase, _loadSwing, _loadHz, _loadPhase;
    private readonly Thermostat? _thermostat;
    private readonly float _fanTrim, _wanderHz1, _wanderHz2, _wanderPhase1, _wanderPhase2;

    /// <summary>The air round the machine, °C. NaN (the default) takes the world's
    /// (AudioPhysics.CurrentAirCelsius, from the server's weather); a test sets it outright.</summary>
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
            // A few per cent of load either way over a minute or two (charge, a dirty coil, the sun):
            // two incommensurate swings, so it never repeats.
            _wanderHz1 = 0.008f + (float)rng.NextDouble() * 0.006f;
            _wanderHz2 = 0.019f + (float)rng.NextDouble() * 0.011f;
            _wanderPhase1 = (float)rng.NextDouble() * MathF.Tau;
            _wanderPhase2 = (float)rng.NextDouble() * MathF.Tau;
        }
        // A PSC fan motor turns within a per cent or two of its nameplate, no two at one speed.
        _fanTrim = 1f + ((float)rng.NextDouble() * 2f - 1f) * 0.015f;
        // A mower's load wanders a lot with the grass: the bog into a thick patch and the recovery
        // out of it are what the governor's droop is for.
        _loadBase = 0.42f;
        _loadSwing = 0.34f;
        _loadHz = 0.19f + (float)rng.NextDouble() * 0.12f;
        _loadPhase = (float)rng.NextDouble() * MathF.Tau;
    }

    /// <summary>
    /// The entity's own ground speed, m/s, set from the game thread; the synth scales the cutting
    /// torque and stalk rate with it. A constant here made a mower sound the same pushing, turning and
    /// standing. Zero for anything standing still.
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
        // Slewed: positions arrive thirty times a second and a governor would hunt on the staircase.
        // A second to get going is about what a push takes.
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
/// An aircraft, rendered live. Its power lever comes from what it is doing, set by the game from the
/// entity's climb angle (ClientAudioSystem): climbing is near full power, level is cruise, descending
/// is idle, which is why one airliner overhead and on approach sounds completely different.
/// </summary>
public sealed class AircraftVoiceState : PhysicalVoiceState
{
    public readonly AircraftSynth Aircraft;

    /// <summary>The power lever, 0..1. Game thread writes.</summary>
    public volatile float TargetLever = 1f;

    /// <summary>How hard a rotor meets its own wake (a descending helicopter slaps); ignored without a
    /// rotor. Game thread writes.</summary>
    public volatile float TargetDescending;

    /// <summary>
    /// On its wheels, and their ground speed. Game thread writes the state; the render thread turns its
    /// edge into the touchdown, which a message sent twice or missed could not do reliably.
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
        // The lever is a pilot's hand, slewed: a second and a half idle to full. Per second, not per
        // call: per call it moved two thirds of its travel every eleven milliseconds, a step.
        float step = dt / LeverTravelSeconds;
        _lever += Math.Clamp(TargetLever - _lever, -step, step);
        Aircraft.Lever = Running ? Math.Clamp(_lever, 0f, 1f) : 0f;
        Aircraft.Descending = TargetDescending;

        // Only the first transition is the touchdown; after it the wheels roll.
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
/// A siren head, as its own voice on the car. Not part of the engine voice: a patrol siren makes
/// 130 dB at a metre and its car 95, and one shared <see cref="PhysicalVoiceState.PascalsAtFullScale"/>
/// either buries the engine 35 dB down or squares the siren off. It also sits behind the grille, not
/// under the back bumper, and close to the car the two separate.
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
        // Not slewed, and must not be: a head changes sound between sweeps, and the synth keeps its
        // phase across the change, so there is nothing to smooth.
        Siren.Mode = Running ? (SirenMode)TargetMode : SirenMode.Off;
    }

    protected override float StepSynth()
    {
        Siren.Step();
        return Siren.Output;
    }
}

/// <summary>
/// A vehicle's horn, blown in the rhythm the server sent (<see cref="Honk"/>): an air horn is a
/// <see cref="ChimeHorn"/> from the <see cref="ModelLibrary"/>, an electric one an
/// <see cref="ElectricHorn"/>. The rhythm runs in the voice's own time, so a tap is as long as the thumb
/// was down however the frames fall; the discarded warm-up is taken off the front, or it would eat the
/// first eighty milliseconds of every tap.
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
/// A struck bell that rings while told to: a level crossing's gong (<see cref="StruckBell.Ringing"/>).
/// It rings for a train the client may be a kilometre from and cannot observe, so whether it rings comes
/// down the wire (SoundEmitterComponent.SynthRunning).
/// </summary>
public sealed class BellVoiceState : PhysicalVoiceState
{
    public readonly StruckBell Bell;

    /// <summary>Rendered with the bell's own headroom (StruckBellSpec.PeakHeadroomDb): under the shared
    /// one the soft ceiling took ten to twelve decibels off every blow and one and a half to three off
    /// the bell's level. A bell is nothing but its blows.</summary>
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
/// A level crossing's gate mechanism (CrossingGateSpec): the arm down under its own weight with the motor
/// braking it, driven back up, and a clunk at each end. Running is the server's word that the crossing is
/// closed (SoundEmitterComponent.SynthRunning); the arm follows it as a real gate does (GateArm). The
/// motor is a small DC gear motor in a steel case: commutator buzz, the pinion's mesh (its teeth against
/// the segments, so the two beat) and brush noise, through the case's resonance; quieter while braking.
/// Allocation-free once built.
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
/// The FMOD side of a physical voice: a read callback that copies out of the ring and nothing else,
/// as <c>EngineProcessor</c> does.
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
    /// Nothing may escape a DSP callback (see EngineProcessor's guard). The whole body is inside the
    /// try: `GCHandle.FromIntPtr(userData).Target`, the line that throws, was once outside it.
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
