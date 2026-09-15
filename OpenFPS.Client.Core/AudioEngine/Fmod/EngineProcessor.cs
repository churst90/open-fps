using System;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The state of one live vehicle: the engine, its driveline, and the driver that follows whatever
/// speed the world reports. Written by the game thread, read by the mixer thread; the only shared
/// fields are plain floats and bools, which are atomic on every platform this runs on, and they are
/// smoothed inside the callback so a 30 Hz network update never steps the throttle.
/// </summary>
public sealed class EngineVoiceState
{
    public readonly VehicleProfile Vehicle;
    public readonly EngineSynth Engine;
    public readonly Driveline Driveline;
    public readonly VirtualDriver Driver;
    private readonly Random _rng;
    private VehicleSynth.TyreVoice _tyre;
    private float _tyreChirp;
    private int _tyreGear;

    /// <summary>Road speed the world says the vehicle is doing, m/s. Game thread writes.</summary>
    public volatile float TargetSpeed;

    /// <summary>
    /// How hard the ROAD is working this car's tyres, as a fraction of the grip they have.
    ///
    /// Set by the game from the car's own motion — see ClientAudioSystem — because only the game can
    /// see the corner. The DSP knows how fast the car is going and what gear it is in; it has no idea
    /// whether it is going round anything. What it adds on top is the part the game cannot see: the
    /// instant of slip a gear change puts through the driven wheels, which happens inside this
    /// synthesis and lasts a tenth of a second.
    /// </summary>
    public volatile float RoadSlip;
    /// <summary>Whether the engine should be running. Game thread writes.</summary>
    public volatile bool Running = true;

    /// <summary>
    /// Where the voice's own envelope is heading, 0 or 1. Game thread writes.
    ///
    /// A synthesized engine cannot simply be switched on and off. There is no zero-crossing to stop
    /// at — the waveform is wherever the crank happens to be — so cutting a voice mid-cycle leaves a
    /// step, and a step is a click. On a track where cars pass in and out of the voice budget every
    /// few seconds that is a click every few seconds, which is exactly what "slight popping as they
    /// drive around" sounds like. The envelope below slews it instead.
    /// </summary>
    public volatile float TargetEnvelope = 1f;

    /// <summary>True once a fade-out has finished and the voice can be released.</summary>
    public volatile bool FadedOut;

    /// <summary>
    /// Brings a voice that was fading back to full, and is the other half of FadeOutEngine.
    ///
    /// Its absence was a silent car. A voice is faded when it loses its slot and, if it wins the slot
    /// back before the fade finishes, it was simply taken off the retiring list — with its envelope
    /// still heading for zero and nothing anywhere to turn it round. The car kept its engine, kept
    /// its position, kept being updated every frame, and was inaudible for the rest of its life.
    /// Heard as cars that stop passing in front of you while the rest of the field still circulates.
    /// </summary>
    public void Revive()
    {
        TargetEnvelope = 1f;
        FadedOut = false;
    }

    private float _envelope;
    /// <summary>
    /// Output gain from pascals at one metre to full scale: one over the pressure that maps to
    /// 0 dBFS. The emitter's placement then handles distance and level.
    ///
    /// It comes from the VEHICLE, and it has to. A fixed 40 Pa — 126 dB — was right for a road car
    /// and wrong by more than an order of magnitude for a race one: an unsilenced V10 peaks at 149 dB
    /// at a metre, thirteen times over that reference, and everything past it goes through the tanh
    /// below. The result is not a loud engine, it is a square wave, and it was heard exactly that way
    /// — "everything is overloaded, crackling and breaking up" — the first time a field of race cars
    /// was put on a map.
    /// </summary>
    public float PascalsAtFullScale = 40f;
    /// <summary>How much of the front of the car (intake and block) is mixed into this one voice,
    /// 0..1. The rig's separation is lost in a single emitter; the timbre is kept.</summary>
    public float FrontMix = 0.35f;
    public float TyreMix = 0.5f;
    public float SampleRate = 44100f;

    private float _speedSmooth;

    // ── Produced ahead on a worker, consumed by the mixer ───────────────────────────────────────
    //
    // The engine used to be integrated INSIDE the FMOD callback, which meant every car in the world
    // was synthesized one after another on a single thread while the mixer's deadline ran down. On a
    // machine with twenty-four cores that is one core doing all of it, and it is the reason the
    // number of cars had to be rationed at all.
    //
    // Now a worker renders ahead into this ring and the callback only copies out of it. The producer
    // can be any thread and there can be as many of them as there are engines; the consumer is
    // whatever the mixer is doing. Echo and borrowed voices read further back in the same ring, which
    // they already did — they simply read behind the PLAY position now rather than behind the write
    // position, so they no longer depend on which DSP the mixer happens to call first.
    private const int RingBits = 17;                       // about three seconds at 44.1 kHz
    private readonly float[] _ring = new float[1 << RingBits];
    private long _written;                                 // producer writes, consumer only reads
    private long _played;                                  // consumer writes, producer only reads

    /// <summary>
    /// One producer at a time, claimed without ever WAITING for one.
    ///
    /// The pool may hand the same voice to two workers for an instant — the voice array is
    /// republished rather than mutated, so a worker can be walking the old one while another walks
    /// the new — and two threads integrating the same engine would corrupt it. A lock would be
    /// correct and is not needed: the second worker has nothing to gain by waiting, because whatever
    /// the first is doing is exactly the work it came to do. It leaves instead.
    /// </summary>
    private int _producing;

    /// <summary>Samples the mixer has taken — the position of "now" for anything reading back.</summary>
    public long Played => Volatile.Read(ref _played);

    /// <summary>How far ahead the producer is, in samples.</summary>
    public long Lead => Volatile.Read(ref _written) - Volatile.Read(ref _played);

    /// <summary>Blocks the mixer asked for that the producer had not rendered yet.</summary>
    public int Starves => _starves;
    private int _starves;

    /// <summary>Every voice's starves since the client started, for the mixer load line.</summary>
    internal static int GlobalStarves;

    private long _consumedSincePrimed;
    private float _lastOut;

    /// <summary>
    /// How far ahead of the mixer THIS voice's producer tries to stay, in seconds.
    ///
    /// Per voice, and that matters. It used to be one global figure that every voice shared, grown
    /// by 35 % the moment ANY voice was caught short — so one car that had just been created, whose
    /// ring was empty because it had existed for four milliseconds, made all thirty of the others
    /// owe a third more audio, on a machine that was already behind. The lead ratcheted to its
    /// ceiling within a second of a map load and stayed there. A voice now buys its own headroom
    /// with its own starvation, and only after it has been playing for at least one lead — before
    /// that there is nothing to diagnose, only a ring that is still filling.
    ///
    /// Long enough to absorb a scheduling hiccup on a busy machine, short enough that a change the
    /// game thread makes — the throttle, the speed the car is doing — is not heard noticeably late.
    /// </summary>
    public float LeadSeconds => _leadSeconds;
    private volatile float _leadSeconds = MinLeadSeconds;
    public const float MinLeadSeconds = 0.25f;
    /// <summary>The deepest the buffer will go when the machine cannot keep up.</summary>
    public const float MaxLeadSeconds = 0.7f;

    /// <summary>The sample played <paramref name="back"/> samples ago, linearly interpolated.</summary>
    public float ReadBack(double back)
    {
        double pos = Volatile.Read(ref _played) - back;
        long i0 = (long)Math.Floor(pos);
        float f = (float)(pos - i0);
        int mask = _ring.Length - 1;
        float a = _ring[(int)(i0 & mask)], b = _ring[(int)((i0 + 1) & mask)];
        return a + (b - a) * f;
    }

    /// <summary>
    /// True once the producer has filled the ring far enough for the mixer to start taking from it.
    ///
    /// Until then the voice hands out SILENCE rather than synthesizing on demand, and that distinction
    /// is the whole of what a map load sounds like. Thirty cars come into earshot at the same instant,
    /// every one of them with an empty ring; asked to fill itself on the spot, each renders inline on
    /// the mixer thread, and thirty engines integrating inside one callback is precisely the hundred
    /// per cent this design exists to avoid — for a second or two, right at the moment everything
    /// else is loading too. Waiting instead costs one lead's worth of silence, eighty milliseconds,
    /// which is less than the envelope takes to fade the car in anyway.
    /// </summary>
    public bool Primed => _primed;
    private volatile bool _primed;

    /// <summary>
    /// How long a new engine is run with its output thrown away before anyone hears it, seconds.
    ///
    /// PlaceAtSpeed sets the crank turning at the right rate, but it cannot fill the pipes: a new
    /// engine's waveguides start EMPTY, and the first thing that comes out of them is the transient
    /// of an exhaust system pressurising from silence. One car doing that is a click. Thirty cars
    /// doing it at the instant a map loads is the second of mess this was heard as.
    ///
    /// A tenth of a second is several engine cycles and more than the longest pipe's round trip, so
    /// by the time the ring holds anything the system is running as it would have been all along. It
    /// is discarded work, on a worker thread, before the voice is audible — it costs nothing anybody
    /// can hear.
    /// </summary>
    private const float WarmupSeconds = 0.1f;
    private bool _warmed;

    /// <summary>
    /// Renders ahead until the producer is one lead in front of the mixer. Worker thread.
    ///
    /// Takes no lock and holds nothing the mixer could ever want, which is the point. The previous
    /// version held a lock for the WHOLE top-up — the warm-up plus every chunk up to the full lead,
    /// which at load-time speed is around a hundred and seventy-five milliseconds of wall clock —
    /// and the mixer callback took that same lock whenever it came up short. FMOD's buffer is
    /// ninety-three milliseconds deep. One such wait was a dropout, and a deeper buffer made it
    /// worse rather than better, because the top-up the mixer might have to wait for got longer with
    /// every millisecond of lead. That is why buffer depth, thread priority and dedicated threads
    /// all helped a little and none of them fixed it: the consumer was not short of audio, it was
    /// frozen.
    /// </summary>
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
                // Thrown away: the play position is moved up to meet it, so none of it is ever heard.
                Volatile.Write(ref _played, Volatile.Read(ref _written));
            }

            // The consumer may have run past what was written — a starved block advances the play
            // position through audio that was never rendered, because a real-time voice keeps wall
            // clock. Nothing will ever read that hole, so the producer simply resumes from where the
            // mixer has got to. This is the only place _written moves other than Synthesize, and it
            // is still the producer moving it, so the single-writer rule holds.
            long played = Volatile.Read(ref _played);
            if (played > Volatile.Read(ref _written)) Volatile.Write(ref _written, played);

            int room = _ring.Length - 8;
            int want = Math.Min((int)(_leadSeconds * SampleRate), room);
            while (Volatile.Read(ref _written) - Volatile.Read(ref _played) < want)
                Synthesize(Math.Min(512, want));

            if (!_primed && Volatile.Read(ref _written) - Volatile.Read(ref _played) >= want / 2) _primed = true;

            // Give the headroom back when it is not being used. Deep buffers cost response: a car
            // answers the throttle this much later. That is the right thing to spend on a load
            // screen and the wrong thing to spend for ever.
            float lead = _leadSeconds;
            if (lead > MinLeadSeconds) _leadSeconds = MathF.Max(MinLeadSeconds, lead - 0.0005f);
        }
        finally { Volatile.Write(ref _producing, 0); }
    }

    /// <summary>
    /// Hands the mixer its block, out of whatever the producer has already rendered.
    ///
    /// NOTHING IN HERE MAY BLOCK OR SYNTHESIZE. It runs on FMOD's mixer thread with the deadline of
    /// the whole mix running down, and a callback that waits on a producer — or, worse, finishes the
    /// block itself by integrating an engine — is not a late voice, it is a hole in every voice.
    /// When the ring is short the voice takes what is there, ramps the rest to silence so the gap
    /// has no step in it, and says so in <see cref="Starves"/>; the producer will be a little
    /// further ahead next time.
    ///
    /// A STARVED BLOCK IS A GAP, NEVER A DELAY. The play position advances by the whole block
    /// whether or not the ring could fill it, and the producer picks up from wherever the consumer
    /// has got to — so a voice always plays at wall-clock rate.
    ///
    /// Taking only what was there and leaving the position behind is the obvious thing to write and
    /// it is wrong, audibly and in a way that does not sound like a dropout at all. A voice that is
    /// handed 900 samples of a 1024-sample block and keeps its place is playing at 88 % speed, which
    /// is a tone and a half FLAT; a field of cars all doing it at once does not sound like missing
    /// audio, it sounds like every engine on the track winding down together. It also puts each
    /// car's sound progressively further behind where the car actually is — up to a lead, which at
    /// 250 km/h is fifty metres — so the field smears into a wash instead of thirty separate cars.
    /// Both were heard on the speedway before this was fixed, and neither reads as "a dropout".
    /// </summary>
    public void Consume(Span<float> mono)
    {
        long at = Volatile.Read(ref _played);
        // Clamped at zero at BOTH ends: a voice that starved last block has a play position ahead of
        // the write position, so the available count is legitimately negative until the producer
        // comes round and resyncs.
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

        // Short. Ramp out of the last sample rather than stepping off it — the waveform is wherever
        // the crank happened to be, and a step is a click.
        int ramp = Math.Min(mono.Length - take, 64);
        for (int i = 0; i < ramp; i++) mono[take + i] = _lastOut * (1f - (i + 1) / (float)ramp);
        mono[(take + ramp)..].Clear();
        _lastOut = 0f;

        if (!_primed) return;
        _starves++;
        Interlocked.Increment(ref GlobalStarves);
        // Buy headroom — but not on a voice that has not yet been playing for one lead. A young
        // voice is not a starved one; it is one whose producer has not caught up yet, and growing
        // the lead for it only asks the producer for more of what it is already behind on.
        float lead = _leadSeconds;
        if (_consumedSincePrimed > lead * SampleRate)
            _leadSeconds = MathF.Min(MaxLeadSeconds, lead * 1.35f);
    }

    /// <summary>Renders a block synchronously, filling in whatever the ring lacks. OFFLINE USE ONLY
    /// — the lab, the spikes and the tests. Never from a mixer callback.</summary>
    public void Render(Span<float> mono)
    {
        long at = Volatile.Read(ref _played);
        long avail = Volatile.Read(ref _written) - at;
        if (avail < mono.Length) Synthesize((int)(mono.Length - avail));
        int mask = _ring.Length - 1;
        for (int i = 0; i < mono.Length; i++) mono[i] = _ring[(int)((at + i) & mask)];
        Volatile.Write(ref _played, at + mono.Length);
    }

    public EngineVoiceState(VehicleProfile v, float sampleRate, int seed)
    {
        Vehicle = v;
        PascalsAtFullScale = v.PascalsAtFullScale;
        SampleRate = sampleRate;
        Engine = new EngineSynth(v.Engine, sampleRate, seed);
        Driveline = new Driveline(v);
        Driver = new VirtualDriver(Driveline, Engine);
        _rng = new Random(seed);
    }

    /// <summary>
    /// Starts the voice as a car ALREADY DOING this speed, rather than as one that has to get there.
    ///
    /// Without it, a car entering the voice budget at speed starts from a dead engine and a stopped
    /// driveline, and the driver then floors it to catch up: a whole spin-up compressed into the
    /// eighty milliseconds the speed filter takes, every time. Teleporting the driveline, choosing
    /// the gear the speed implies and spinning the crank to match means the first sample it renders
    /// is already the sound the car is making.
    /// </summary>
    public void PlaceAtSpeed(float metresPerSecond)
    {
        _speedSmooth = MathF.Max(0f, metresPerSecond);
        Driveline.Teleport(_speedSmooth);
        Driver.TargetSpeed = _speedSmooth;

        // The highest gear that keeps the engine under its upshift point — what a driver would be in.
        var gb = Vehicle.Gearbox;
        int gear = 1;
        for (int g = gb.TopGear; g >= 1; g--)
        {
            if (gb.RpmFor(_speedSmooth, g) <= gb.UpshiftRpm) { gear = g; break; }
        }
        Driveline.Gear = gear;
        Driveline.Clutch = 1f;
        float rpm = MathF.Max(Vehicle.Engine.IdleRpm, gb.RpmFor(_speedSmooth, gear));
        Engine.SpinTo(rpm);
    }

    /// <summary>Integrates <paramref name="count"/> samples of engine into the ring.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Synthesize(int count)
    {
        float dt = 1f / SampleRate;
        float gain = 1f / MathF.Max(1f, PascalsAtFullScale);
        float target = TargetSpeed;
        Driver.Running = Running;
        // About 60 ms either way: long enough that no step survives it, short enough that a car
        // arriving is still a car arriving.
        float envStep = 1f / (0.06f * SampleRate);
        float envTarget = TargetEnvelope;
        int mask = _ring.Length - 1;
        long w = _written;
        for (int i = 0; i < count; i++)
        {
            // Smooth the network's speed steps over about 80 ms.
            _speedSmooth += (target - _speedSmooth) * MathF.Min(1f, dt * 12f);
            Driver.TargetSpeed = _speedSmooth;
            Driver.Apply(dt);
            Driveline.Step(Engine, dt);
            // The shift chirp, which the game cannot see because the gearbox lives in here. A big
            // ratio step with the throttle open puts the driven wheels briefly out of step with the
            // road, and that is the sound a shift kit is bought for.
            if (Driveline.Gear != _tyreGear && _tyreGear >= 1 && Driveline.Gear >= 1)
            {
                float from = Driveline.Ratio(_tyreGear), to = Driveline.Ratio(Driveline.Gear);
                if (from > 0f && to > 0f)
                    _tyreChirp = MathF.Max(_tyreChirp, TyreFriction.ShiftChirp(from / to, Engine.Throttle));
            }
            _tyreGear = Driveline.Gear;
            _tyreChirp *= 0.99985f;
            float tyre = VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, RoadSlip + _tyreChirp, _rng, ref _tyre);
            // Tyres are in arbitrary units; place them about 30 dB under a loud exhaust.
            float pa = Engine.Exhaust + (Engine.Intake + Engine.Block * 0.4f) * FrontMix + tyre * 1.2f * TyreMix;
            float y = pa * gain;
            // A soft ceiling: the physics can spike past any fixed reference on a backfire.
            float o = y > 0.8f || y < -0.8f ? MathF.Tanh(y) : y;
            _envelope += Math.Clamp(envTarget - _envelope, -envStep, envStep);
            o *= _envelope;
            _ring[(int)(w & mask)] = o;
            w++;
        }
        Volatile.Write(ref _written, w);
        if (envTarget <= 0f && _envelope <= 1e-4f) FadedOut = true;
    }
}

/// <summary>
/// One reflection of a live engine: the same signal, read back from the engine's ring buffer at
/// the delay the mirrored path implies, and placed at the mirrored source. The delay is slewed rather
/// than stepped, so as the car moves and the path length changes the echo glides in pitch — which is
/// the Doppler of the reflection, and the reason its emitter carries no velocity of its own.
/// </summary>
public sealed class EngineEchoState
{
    public readonly EngineVoiceState Source;
    public volatile float TargetDelaySeconds;
    public volatile float TargetGain;
    private double _delay = -1;
    private float _gain;
    public float SampleRate = 44100f;
    /// <summary>
    /// Floor on the echo's delay, as a MULTIPLE OF THE MIXER'S BLOCK, not as a time.
    ///
    /// An echo reads its source's ring buffer relative to how much that source has written, which
    /// silently assumes the two DSPs are called once each per mixer block and always in the same
    /// order. FMOD guarantees neither: the order depends on the shape of the graph, and the graph
    /// changes shape every time a voice is added or removed — which on a racetrack is several times
    /// a second. When the order flips, the echo reads a whole block early or late, and a block-sized
    /// jump in the read position is a click.
    ///
    /// Two blocks of slack absorbs the flip either way. It used to be written as a fixed 26 ms,
    /// which IS two blocks at the 512-sample size the lab rig was built against and only ONE AND A
    /// TENTH at the 1024 FMOD actually chose — so on the real mixer the margin was not there, and
    /// the clicks scaled with the number of echo voices. Derived from the block the mixer hands us,
    /// so it cannot drift out of step with the buffer size again.
    /// </summary>
    public const int MinDelayBlocks = 2;

    /// <summary>The floor as a time, for callers that have no block to measure. Only a fallback.</summary>
    public const float MinDelaySeconds = 0.026f;

    private int _blockSlack;

    public EngineEchoState(EngineVoiceState source) { Source = source; SampleRate = source.SampleRate; }

    public void Render(Span<float> mono)
    {
        // The largest block we have ever been handed. Taking the maximum rather than the current
        // length means a short block (FMOD hands out partial ones) cannot shrink the margin.
        int slack = MinDelayBlocks * mono.Length;
        if (slack > _blockSlack) _blockSlack = slack;

        double floorSamples = Math.Max(MinDelaySeconds * SampleRate, _blockSlack);
        double target = Math.Max(floorSamples, TargetDelaySeconds * SampleRate);
        if (_delay < 0) _delay = target;
        float gTarget = TargetGain;
        // Slew: up to 12% per sample of drift, which covers the Doppler of a fast pass.
        for (int i = 0; i < mono.Length; i++)
        {
            double diff = target - _delay;
            _delay += Math.Clamp(diff * 0.002, -0.12, 0.12);
            _gain += (gTarget - _gain) * 0.0015f;
            // Reading "back" from the source's current write position: the source rendered its
            // block before or after this one; the minimum delay covers either order.
            // Clamped to the slack as well as the target, so a delay that is being slewed downward
            // can never cross into the block the source may not have written yet.
            double back = Math.Max(_delay, floorSamples) + (mono.Length - i);
            mono[i] = Source.ReadBack(back) * _gain;
        }
    }
}

public static class EchoProcessor
{
    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, EngineEchoState state, out FMOD.DSP dsp, out GCHandle handle)
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

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData;
        unsafe
        {
            var dsp = new FMOD.DSP(dsp_state.instance);
            dsp.getUserData(out userData);
        }
        if (userData == IntPtr.Zero) return RESULT.OK;
        var state = (EngineEchoState?)GCHandle.FromIntPtr(userData).Target;
        if (state == null) return RESULT.OK;
        if (outchannels == 0) outchannels = 1;
        int ch = outchannels, n = (int)length;
        if (_scratch == null || _scratch.Length < n) _scratch = new float[Math.Max(n, 1024)];
        var mono = _scratch.AsSpan(0, n);
        try { state.Render(mono); } catch { mono.Clear(); }
        unsafe
        {
            float* outBuf = (float*)outbuffer;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < ch; c++) outBuf[i * ch + c] = mono[i];
        }
        return RESULT.OK;
    }
}

/// <summary>
/// An FMOD DSP that IS a vehicle: the engine synthesis runs inside the mixer callback, so a vehicle
/// in the world has an engine that follows its speed live rather than a recording moved through
/// space. Modelled on <see cref="SynthProcessor"/>.
/// </summary>
public static class EngineProcessor
{
    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, EngineVoiceState state, out FMOD.DSP dsp, out GCHandle handle)
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

    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData;
        unsafe
        {
            var dsp = new FMOD.DSP(dsp_state.instance);
            dsp.getUserData(out userData);
        }
        if (userData == IntPtr.Zero) return RESULT.OK;
        var state = (EngineVoiceState?)GCHandle.FromIntPtr(userData).Target;
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
