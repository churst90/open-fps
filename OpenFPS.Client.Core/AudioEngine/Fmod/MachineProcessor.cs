using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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
public abstract class PhysicalVoiceState : IRenderedVoice
{
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

    public long Played => Volatile.Read(ref _played);
    public long Lead => Volatile.Read(ref _written) - Volatile.Read(ref _played);
    public int Starves => _starves;
    private int _starves;

    private long _consumedSincePrimed;
    private float _lastOut;
    private float _envelope;

    public float LeadSeconds => _leadSeconds;
    private volatile float _leadSeconds = MinLeadSeconds;
    public const float MinLeadSeconds = 0.25f;
    public const float MaxLeadSeconds = 0.7f;

    public bool Primed => _primed;
    private volatile bool _primed;

    private const float WarmupSeconds = 0.08f;
    private bool _warmed;

    private float _listenerX, _listenerY, _listenerZ;
    private volatile bool _listenerKnown;

    protected PhysicalVoiceState(float sourceLevelDb, float sampleRate)
    {
        SampleRate = sampleRate;
        PascalsAtFullScale =
            20e-6f * MathF.Pow(10f, (sourceLevelDb + VehicleProfile.PeakHeadroomDb) / 20f);
    }

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

    /// <summary>Hands the mixer its block out of what the producer has already rendered. Mixer
    /// thread. NOTHING IN HERE MAY BLOCK OR SYNTHESIZE.</summary>
    public void Consume(Span<float> mono)
    {
        long at = Volatile.Read(ref _played);
        long avail = _primed ? Volatile.Read(ref _written) - at : 0;
        int take = (int)Math.Clamp(avail, 0, mono.Length);
        int mask = _ring.Length - 1;
        for (int i = 0; i < take; i++) mono[i] = _ring[(int)((at + i) & mask)];
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
        _starves++;
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
            _ring[(int)(w & mask)] = y > 0.8f || y < -0.8f ? MathF.Tanh(y) : y;
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
    private readonly float _loadBase, _loadSwing, _loadHz, _loadPhase;
    private readonly float _dutyOn, _dutyOff, _dutyPhase;
    private readonly bool _cycles;

    public MachineVoiceState(SmallMachineSpec spec, float sampleRate, int entityId, int seed)
        : base(spec.SourceLevelDb, sampleRate)
    {
        Spec = spec;
        Machine = new SmallMachineSynth(spec, sampleRate, seed);

        var rng = new Random(entityId * 2654435761u.GetHashCode());
        _cycles = spec.Compressor != null;
        if (_cycles)
        {
            // A thermostat. Real cycles are minutes long; these are 100-220 s on and 60-140 s off,
            // which is short enough that a walk down a street crosses several and long enough that
            // none of them reads as a stutter. The phase is spread, so a wall of them is a wall.
            _dutyOn = 100f + (float)rng.NextDouble() * 120f;
            _dutyOff = 60f + (float)rng.NextDouble() * 80f;
            _dutyPhase = (float)rng.NextDouble() * (_dutyOn + _dutyOff);
        }
        // How hard it is working, and how much that wanders. A condenser's load is the weather and
        // barely moves; a mower's is the grass and moves a lot, which is what the governor's droop
        // is FOR — the bog going into a thick patch and the recovery out of it.
        bool mows = spec.Cutting != null;
        _loadBase = mows ? 0.42f : 0.55f;
        _loadSwing = mows ? 0.34f : 0.06f;
        _loadHz = mows ? 0.19f + (float)rng.NextDouble() * 0.12f : 0.03f;
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
        Machine.Load = Math.Clamp(
            _loadBase + _loadSwing * MathF.Sin(_loadPhase + MathF.Tau * _loadHz * seconds), 0f, 1f);
        if (_cycles)
        {
            float period = _dutyOn + _dutyOff;
            float phase = (seconds + _dutyPhase) % period;
            Machine.CompressorOn = phase < _dutyOn;
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
    public readonly AircraftProfile Profile;
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
        Profile = p;
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
    public readonly SirenSpec Spec;
    public readonly ElectronicSiren Siren;

    /// <summary>Which sound the head is making. Game thread writes.</summary>
    public volatile int TargetMode = (int)SirenMode.Off;

    public SirenVoiceState(SirenSpec spec, float sampleRate)
        : base(spec.SourceLevelDb, sampleRate)
    {
        Spec = spec;
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
    public readonly StruckBellSpec Spec;
    public readonly StruckBell Bell;

    public BellVoiceState(StruckBellSpec spec, float sampleRate, int seed)
        : base(spec.ReferenceDb, sampleRate)
    {
        Spec = spec;
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
/// The FMOD side of a physical voice — machine or aircraft: a read callback that copies out of the
/// ring and nothing else.
/// Mirrors <c>EngineProcessor</c>, which is the point.
/// </summary>
public static class MachineProcessor
{
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

    [ThreadStatic] private static float[]? _scratch;

    /// <summary>
    /// NOTHING MAY ESCAPE A DSP CALLBACK — see the same guard on EngineProcessor for what happens
    /// when one does. This one was copied from the engine's, gap and all: the line that actually
    /// throws, `GCHandle.FromIntPtr(userData).Target`, was outside the only try in the method.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        try { return ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels); }
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
        if (userData == IntPtr.Zero) return RESULT.OK;
        var state = (PhysicalVoiceState?)GCHandle.FromIntPtr(userData).Target;
        if (state == null) return RESULT.OK;

        if (outchannels == 0) outchannels = 1;
        int ch = outchannels;
        int n = (int)length;
        if (_scratch == null || _scratch.Length < n) _scratch = new float[Math.Max(n, 1024)];
        var mono = _scratch.AsSpan(0, n);
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
