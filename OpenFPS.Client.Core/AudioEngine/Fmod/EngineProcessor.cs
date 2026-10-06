using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Core.Pneumatics;
using System.Runtime.CompilerServices;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The state of one live vehicle: the engine, its driveline, and the driver that follows whatever
/// speed the world reports. Written by the game thread, read by the mixer thread; the only shared
/// fields are plain floats and bools, which are atomic on every platform this runs on, and they are
/// smoothed inside the callback so a 30 Hz network update never steps the throttle.
/// </summary>
public sealed class EngineVoiceState : IRenderedVoice, IGuardedUnit
{
    /// <summary>The non-finite guard's flag and name for this unit (NonFinite).</summary>
    public NonFiniteUnit Guard { get; } = new();

    /// <summary>The mixer callback's mono buffer, made with the voice so the callback never allocates.</summary>
    internal readonly float[] MixScratch = new float[DspCallback.MaxBlock];

    public readonly VehicleProfile Vehicle;
    public readonly EngineSynth Engine;
    public readonly Driveline Driveline;
    public readonly VirtualDriver Driver;
    private readonly Random _rng;
    private VehicleSynth.TyreVoice _tyre;
    private VehicleSynth.TyreVoice _tyreFront;
    /// <summary>
    /// Every wheel's squeal, in the server's wheel order (see <see cref="WheelDynamics"/>): each is
    /// driven by that wheel's own demand, slip and load, and goes out through the tap at its end of
    /// the vehicle, weighted by how much nearer or further than that tap the wheel is from the
    /// listener. So the loaded outside front of a corner sings first, from the front, and louder on
    /// its own side.
    /// </summary>
    private readonly VehicleSynth.WheelSquealVoice[] _wheelSqueal;
    /// <summary>Per wheel: on the front axle group, driven, its static load (N), and where its contact
    /// patch is in the vehicle's frame (x right, y up, z forward).</summary>
    private readonly bool[] _wheelFront, _wheelDriven;
    private readonly float[] _wheelStatic;
    private readonly Vector3[] _wheelAt;
    /// <summary>This block's drive for each wheel: demand, slip velocity, load share, and its gain
    /// against the tap it goes out through.</summary>
    private readonly float[] _wheelDemand, _wheelSlipVelocity, _wheelLoad, _wheelGain, _wheelStickSlip;
    /// <summary>The slip velocity at which one tyre at the limit squeals at its share of the declared
    /// level: a tyre at its peak slip angle at <see cref="SquealReferenceSpeed"/>.</summary>
    private readonly float _squealSlipVelocity;
    /// <summary>The road speed at which the axle squeal reached its full level (its rub term saturated
    /// at 12 m/s), so the declared squeal level means what it meant.</summary>
    private const float SquealReferenceSpeed = 12f;
    private float _tyreChirp;
    private float _chirpDecay = 0.99985f;   // a sample at 44.1 kHz: 150 ms
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
    /// <summary>
    /// Each wheel as the server sent it, in <see cref="WheelDynamics"/>' order, or null. When there,
    /// every wheel squeals for itself from its own demand, slip and load (see
    /// <see cref="WheelsDrive"/>), and the axle voices only roll. Without it, both axle voices
    /// squeal from <see cref="RoadSlip"/>. Game thread writes.
    /// </summary>
    public volatile OpenFPS.Common.Networking.WheelState[]? Wheels;
    /// <summary>
    /// The water on the road under a vehicle whose wheels the server does not send, mm (RoadWater; the
    /// world's wheel-path figure). Wheels that are sent carry their own. Game thread writes.
    /// </summary>
    public volatile float RoadWaterMm;
    /// <summary>The water on the road: the tyres' hiss, the spray in the arches, the bow and the splash
    /// (WetTyres). Per wheel, from each wheel's own water.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.WetTyres _wet;
    private readonly float[] _wetWater, _wetTexture, _wheelWetSqueal;
    /// <summary>Whether the engine should be running. Game thread writes.</summary>
    public volatile bool Running = true;
    /// <summary>Standing at a stop that takes passengers: set the spring brakes, kneel, open the
    /// doors. Anywhere else a stopped vehicle just holds its service brake.</summary>
    public volatile bool ServingStop;
    /// <summary>How far down the side windows are, 0 shut to 1 fully down. Game thread writes; the
    /// voice heard from inside lets the outside in through them (see the interior mix).</summary>
    public volatile float WindowsOpen;
    /// <summary>The air system, for tests and instruments. Null on a vehicle without one.</summary>
    internal AirSystem? Air => _air;

    // Where the listener stands, in the machine's own frame. Game thread writes, producer reads;
    // the three floats may tear against each other by one update, which the network's slew absorbs.
    private float _listenerX, _listenerY, _listenerZ;
    private volatile bool _listenerKnown;

    /// <summary>
    /// Tells the engine where the listener is, in the machine's frame (x across, y up, z forward,
    /// origin at the exhaust part), so a machine with more than one tailpipe can radiate each from
    /// its own place. See <see cref="ExhaustNetwork.SetListener"/> for why that is not a detail.
    /// </summary>
    public void SetListener(Vector3 machineFrame)
    {
        Volatile.Write(ref _listenerX, machineFrame.X);
        Volatile.Write(ref _listenerY, machineFrame.Y);
        Volatile.Write(ref _listenerZ, machineFrame.Z);
        _listenerKnown = true;
    }

    /// <summary>
    /// True when this machine's front outlet has a voice of its own, so this one is the back alone.
    ///
    /// Written by the game when it decides a car is close enough for the two ends of it to be told
    /// apart (see Localisation.Resolvable). The change is not a switch: the front component slews
    /// out of this voice over about sixty milliseconds while the intake voice slews in, because the
    /// exhaust is a running waveform with no zero-crossing to step at — the same reason an engine is
    /// never simply stopped.
    /// </summary>
    public volatile bool SplitVoices;

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
    /// A voice is faded when it loses its slot. If it wins the slot back before the fade finishes it
    /// is taken off the retiring list, but its envelope is still heading for zero; without this
    /// nothing turns it round, and the car keeps its engine, its position and its updates and is
    /// inaudible for the rest of its life: cars that stop passing in front of you while the rest of
    /// the field still circulates.
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
    /// It comes from the VEHICLE, and it has to. A fixed 40 Pa (126 dB) is right for a road car and
    /// wrong by more than an order of magnitude for a race one: an unsilenced V10 peaks at 149 dB at
    /// a metre, thirteen times over that reference, and everything past it goes through the tanh
    /// below. The result is not a loud engine but a square wave: overloaded, crackling, breaking up.
    /// </summary>
    public float PascalsAtFullScale = 40f;
    /// <summary>
    /// How much of the front of the car reaches this voice, 0..1. It is ONE for a whole voice,
    /// because there is nothing for it to represent: an intake's route to the street is all declared
    /// and derived, IntakeSpec.AirboxLossDb (the box as an expansion chamber, from its own geometry)
    /// and IntakeSpec.Level (what escapes the bay). A constant below one here would attenuate on top
    /// of that declared path, saying the same thing again in a number nobody could look up.
    ///
    /// Still a field rather than a constant because a two-outlet vehicle hands its front to a
    /// SECOND voice when the listener is close enough to tell the ends apart, and that crossfade
    /// runs through here.
    /// </summary>
    public float FrontMix = 1f;
    /// <summary>
    /// How much of the tyre layer reaches the mix.
    ///
    /// The tyre model already works in physical levels: a squeal is scaled from the tyre's own
    /// SquealDb, which for a road tyre is 92 dB against a diesel truck's 104. This is not a taste
    /// constant on top of that. It is measured: the tyre voice on its own puts a full squeal at an
    /// RMS of 0.89 where the exhaust runs in pascals and reaches tens, which left the squeal
    /// twenty-odd decibels under an engine it should be about seven under. Buried that far, only its
    /// low shoulder is audible, and the tyres sound dull or absent.
    /// </summary>
    public float TyreMix = DefaultTyreMix;
    private const float DefaultTyreMix = 1.4f;
    /// <summary>One axle at each end, at a level that keeps the POWER of the pair what the single
    /// coherent signal had (0.6 at each end, summed in phase: 1.2, so 0.6 x root 2 each). The squeal
    /// is still quoted against this; the rolling noise is anchored through it.</summary>
    private const float PerAxle = 0.6f * 1.41421356f;
    public float SampleRate = MixerQuality.MixerRate;

    private float _speedSmooth;

    // ── Produced ahead on a worker, consumed by the mixer ───────────────────────────────────────
    //
    // A worker renders ahead into this ring and the FMOD callback only copies out of it. Integrating
    // the engine inside the callback would synthesize every car one after another on the mixer's
    // single thread while its deadline ran down, one core doing all of it however many there are.
    //
    // The producer can be any thread and there can be as many of them as there are engines; the
    // consumer is whatever the mixer is doing. Echo and borrowed voices read further back in the same
    // ring, behind the PLAY position rather than the write position, so they do not depend on which
    // DSP the mixer happens to call first.
    private const int RingBits = 17;                       // about three seconds at 44.1 kHz

    /// <summary>The back of the machine: the exhaust, and the body it shakes.</summary>
    private readonly float[] _ring = new float[1 << RingBits];

    /// <summary>
    /// The front of the machine: what it breathes through, and the block behind that.
    ///
    /// A second ring rather than a second engine. The engine is integrated ONCE and its two outlets
    /// are written separately, so a car that earns two voices costs one more copy-out and no more
    /// synthesis — which is the only reason a two-voice car is affordable at all.
    ///
    /// The two taps sum to exactly what a single voice plays (see <see cref="Consume"/>), so
    /// a car is the same loudness whether it is being heard through one voice or two. That is not a
    /// nicety: a level that changed when the mixer changed its mind about how many voices to spend
    /// would be heard as the car jumping, which is precisely the kind of thing a listener notices
    /// and cannot explain.
    /// </summary>
    private readonly float[] _front = new float[1 << RingBits];
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

    /// <summary>The ground between this voice and the listener; see GroundReflection. Applied to what
    /// the mixer takes, never to the ring, so echoes and borrowed voices read the car itself.</summary>
    public readonly OpenFPS.Client.AudioEngine.Acoustics.GroundReflection Ground;
    private float _nearShare;
    /// <summary>The front voice's ground, when there is one, so it hears the same tyre share.</summary>
    internal OpenFPS.Client.AudioEngine.Acoustics.GroundReflection? _frontGround;

    /// <summary>Samples the mixer has taken — the position of "now" for anything reading back.</summary>
    public long Played => Volatile.Read(ref _played);

    /// <summary>
    /// How fast the mixer takes this voice, relative to the mixer's own rate: its channel's pitch,
    /// which is its Doppler. The provider sets it whenever it pitches the channel.
    ///
    /// <see cref="Played"/> does NOT move at this rate, it moves in whole blocks. FMOD resamples a
    /// pitched DSP channel by calling the DSP more or fewer times per mixer block, 1024 samples each
    /// time (measured: a car closing at 60 km/h was called 4.9 % more often, never with a different
    /// length). So a reader that sits a fixed distance behind Played jumps a whole block, 23 ms of
    /// waveform, every time the source gets an extra call or misses one — about twice a second for a
    /// car passing at 60 km/h, more for its front voice. Readers that must stay in step with this
    /// voice keep a continuous clock that runs at this rate and leans only slowly on Played
    /// (<see cref="SourceClock"/>).
    /// </summary>
    public volatile float ConsumeRate = 1f;

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
    /// Per voice, and that matters. A global figure grown whenever ANY voice was caught short would
    /// let one car created four milliseconds ago, whose ring is empty only because it is new, make
    /// all thirty of the others owe more audio on a machine that is already behind; the lead would
    /// ratchet to its ceiling within a second of a map load and stay there. A voice buys its own
    /// headroom with its own starvation, and only after it has been playing for at least one lead —
    /// before that there is nothing to diagnose, only a ring that is still filling.
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
    public float ReadBack(double back) => ReadAt(Volatile.Read(ref _played) - back);

    /// <summary>
    /// One sample at an ABSOLUTE position in this voice's stream, linearly interpolated.
    ///
    /// The difference from <see cref="ReadBack"/> is the whole of the borrowed-voice Doppler fault.
    /// Reading BACK is relative to the play position, and the play position moves at whatever rate the
    /// mixer is consuming this voice — which is the rate its own channel is pitched at, which is ITS
    /// Doppler. Anything that reads back from it is therefore hearing this car's Doppler already, and
    /// a borrowed voice that then applies its own is applying two. A reader that keeps its own cursor
    /// and asks for an absolute position gets the audio at the rate it was synthesized.
    /// </summary>
    public float ReadAt(double position)
    {
        long i0 = (long)Math.Floor(position);
        float f = (float)(position - i0);
        int mask = _ring.Length - 1;
        int j0 = (int)(i0 & mask), j1 = (int)((i0 + 1) & mask);
        // BOTH taps: a borrowed voice and an echo are a whole car heard from somewhere else, not the
        // back half of one. Only a listener close enough to tell the two ends apart gets them apart.
        float a = _ring[j0] + _front[j0], b = _ring[j1] + _front[j1];
        // ...and, sitting in it, every path into the cabin.
        if (Volatile.Read(ref _cabinRings) is { } rings)
            foreach (var r in rings)
            {
                int m = r.Length - 1;
                a += r[(int)(i0 & m)]; b += r[(int)((i0 + 1) & m)];
            }
        return Soft(a + (b - a) * f);
    }

    /// <summary>One sample of the FRONT tap alone, absolute, interpolated — what an intake voice
    /// reads. The same cursor rules as a borrowed voice: see <see cref="EngineTapState"/>.</summary>
    public float ReadFrontAt(double position)
    {
        long i0 = (long)Math.Floor(position);
        float f = (float)(position - i0);
        int mask = _front.Length - 1;
        float a = _front[(int)(i0 & mask)], b = _front[(int)((i0 + 1) & mask)];
        return Soft(a + (b - a) * f);
    }

    /// <summary>The soft ceiling the physics needs: a backfire can spike past any fixed reference,
    /// and a step at full scale is a click. Applied where the taps are summed, once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Soft(float y) => SoftCeiling.Apply(y);

    /// <summary>How much of the front tap this voice is still carrying, 0..1. Consumer-side.</summary>
    private float _frontShare = 1f;

    /// <summary>How many samples the ring holds — the whole of the past a borrowed voice may read.</summary>
    public int RingLength => _ring.Length;

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
    /// doing it at the instant a map loads is a second of mess.
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
    /// Takes no lock and holds nothing the mixer could ever want, which is the point. A lock held
    /// for the whole top-up (the warm-up plus every chunk up to the full lead, around 175 ms of wall
    /// clock at load-time speed) and taken by the mixer callback when it came up short would freeze
    /// the mixer for longer than FMOD's 93 ms buffer: a dropout. A deeper buffer would make it worse,
    /// not better, because the top-up the mixer might wait for grows with every millisecond of lead.
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
    /// Neither reads as a dropout.
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
        // The front of the car belongs in this voice only while nothing else is carrying it. The
        // share slews rather than switches: sixty milliseconds, the same as the envelope, so handing
        // the intake over to its own voice is a crossfade and not a step.
        float shareTarget = SplitVoices ? 0f : 1f;
        float shareStep = 1f / (0.06f * SampleRate);
        // The cabin's paths whose taps are not playing (yet, or at all) are carried here.
        var cabin = Volatile.Read(ref _cabinRings);
        for (int i = 0; i < take; i++)
        {
            int j = (int)((at + i) & mask);
            _frontShare += Math.Clamp(shareTarget - _frontShare, -shareStep, shareStep);
            float carried = cabin != null ? CarriedCabin(cabin, at + i, shareStep) : 0f;
            // The ground AFTER the ceiling. The ceiling guards the synthesis — a backfire past the
            // voice's headroom — and the voice's headroom was set for the direct sound. With the
            // road's up-to-six-decibel bass lift inside it, every exhaust pulse of a loud V8 ran
            // into the knee and the car came out crunched ("really bad over sampling ... the v8
            // muscle car"). The mixer is floating point; the lift has room there.
            mono[i] = Ground.Process(Soft(_ring[j] + _front[j] * _frontShare + carried));
        }
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
        float shareTarget = SplitVoices ? 0f : 1f;
        float shareStep = 1f / (0.06f * SampleRate);
        var cabin = Volatile.Read(ref _cabinRings);
        for (int i = 0; i < mono.Length; i++)
        {
            int j = (int)((at + i) & mask);
            _frontShare += Math.Clamp(shareTarget - _frontShare, -shareStep, shareStep);
            if (cabin != null)
            {
                mono[i] = Ground.Process(Soft(_ring[j] + _front[j] * _frontShare + CarriedCabin(cabin, at + i, shareStep)));
                continue;
            }
            // The ground AFTER the ceiling. The ceiling guards the synthesis — a backfire past the
            // voice's headroom — and the voice's headroom was set for the direct sound. With the
            // road's up-to-six-decibel bass lift inside it, every exhaust pulse of a loud V8 ran
            // into the knee and the car came out crunched ("really bad over sampling ... the v8
            // muscle car"). The mixer is floating point; the lift has room there.
            mono[i] = Ground.Process(Soft(_ring[j] + _front[j] * _frontShare));
        }
        Volatile.Write(ref _played, at + mono.Length);
    }

    public EngineVoiceState(VehicleProfile v, float sampleRate, int seed)
    {
        Ground = new OpenFPS.Client.AudioEngine.Acoustics.GroundReflection(sampleRate);
        Vehicle = v;
        PascalsAtFullScale = v.PascalsAtFullScale;
        SampleRate = sampleRate;
        _chirpDecay = OpenFPS.Client.AudioEngine.Core.At44k.Decay(0.99985f, sampleRate);
        Engine = new EngineSynth(v.Engine, sampleRate, seed);
        Driveline = new Driveline(v);
        Driver = new VirtualDriver(Driveline, Engine);
        _rng = new Random(seed);
        _body = new BodyResonator(v.Body ?? VehicleBody.None, sampleRate);

        // The inside: the cabin's own modes at full weight and nothing else — the panels' ringing is
        // already in the outside body, and it is the AIR in the box that booms at the driver.
        var shell = v.Body ?? VehicleBody.None;
        _cabin = new BodyResonator(shell with { PanelSpansM = Array.Empty<float>(), CabinLeak = 1f, Coupling = 1f },
                                   sampleRate);
        var steel = AcousticRegistry.GetProperties(shell.PanelMaterial);
        float surfaceMass = MathF.Max(0.5f, steel.DensityKgM3 * shell.PanelThicknessM);   // kg/m^2
        _panelCorner = 415f / (MathF.PI * surfaceMass);                                   // rho*c / (pi*m)
        _sealLeak = Math.Clamp(shell.SealLeak, 0f, 1f);
        _starterPath = MathF.Pow(10f, -MathF.Max(0f, shell.StarterPathLossDb) / 20f);
        _windPaAt110 = 20e-6f * MathF.Pow(10f, shell.WindNoiseDbAt110 / 20f);
        // The side windows: what share of the cabin's wall they open fully down, how much louder the wind
        // is outside the glass than the anchor heard through it, and the cabin's note as a resonator
        // whose necks are the open windows.
        if (v.Body is { CabinLengthM: > 0f } && CarWindow.Measure(v) is { } openings)
        {
            _windowShareFull = openings.Count * openings.AreaEachM2 / openings.CabinSurfaceM2;
            // The wind anchor is what reaches the seat through shut glass and the seals at the wind's own
            // pitch, about 500 Hz: the glass's mass law there, plus the seals. Outside the glass the
            // turbulence is that much stronger, and an open window lets it in through the hole instead.
            float glassMass = AcousticRegistry.GetProperties("Glass").DensityKgM3 * CarWindow.GlassThicknessM;
            float t = 415f / (MathF.PI * 500f * MathF.Max(1f, glassMass));
            _windOutside = 1f / MathF.Sqrt(t * t + _sealLeak * _sealLeak);
            // Helmholtz: the cabin's air is the spring and the air in each open window the mass, its
            // effective length about 1.7 radii of an opening that size (both ends flanged by the door).
            float neck = 1.7f * MathF.Sqrt(openings.AreaEachM2 / MathF.PI);
            _cabinHelmholtzHz = 343f / (2f * MathF.PI)
                              * MathF.Sqrt(openings.Count * openings.AreaEachM2 / (openings.CabinVolumeM3 * neck));
            _windowRunM = openings.AreaEachM2 / (openings.GlassHeightM * CarWindow.ShapeShare);
            _windowCount = openings.Count;
            // The machine's voice is its level a metre from each source; the windows are further than that
            // from them, about half the vehicle's length from the engine bay, the pipe and the wheels taken
            // together, and its pressure falls as one over the distance.
            _windowFromSources = 1f / MathF.Max(1f, v.LengthMetres * 0.5f);
        }
        if (!string.IsNullOrEmpty(v.AirSystem))
        {
            try
            {
                _air = new AirSystem(ModelLibrary.Air(v.AirSystem), sampleRate, seed + 17);
                // Each valve is where the spec says it is, measured back from the nose; it is heard
                // from whichever outlet of this vehicle is nearer. The compressor is on the engine,
                // so it is at the front unless the engine is in the back.
                float nose = v.LengthMetres * 0.5f;
                _air.PlaceAtFront(p => NearerFront(v, nose - p.AlongMetres), compressorAtFront: !v.EngineAtRear);
                _chimeAtFront = _air.Ports.TryGetValue("door", out var door)
                    ? NearerFront(v, nose - door.Spec.AlongMetres) : true;
            }
            catch (Exception ex) { Serilog.Log.Warning("Vehicle '{Name}': air system '{Air}' — {Err}", v.Name, v.AirSystem, ex.Message); }
        }
        _bayLeak = Math.Clamp(v.EngineBayLeakage, 0f, 1f);
        _engineAtRear = v.EngineAtRear;
        _bayIntake = new EchoDiffuser(BayScattering, seed + 71, sampleRate);
        _radiation = new OpenFPS.Client.AudioEngine.Core.Engine.ExhaustRadiation(v, sampleRate);
        _bayRadiation = new OpenFPS.Client.AudioEngine.Core.Engine.BayRadiation(v, sampleRate);
        // Drums or discs, as the axle doing most of the stopping has.
        var chassis = v.Running;
        _squeal = new OpenFPS.Client.AudioEngine.Core.BrakeSqueal(sampleRate, drums: chassis.MainBrake == OpenFPS.Common.BrakeKind.Drum, seed: seed + 97);
        _frontWheels = chassis.Axles.Length > 0 ? chassis.Axles[0].Wheels : 1;
        _frontRadius = chassis.Axles.Length > 0 ? chassis.Axles[0].Tyre.RollingRadiusMetres : 0.337f;
        _rearRadius = chassis.Axles.Length > 0 ? chassis.Axles[^1].Tyre.RollingRadiusMetres : 0.337f;
        // The wheels as the server's model lays them out, so the wire's order, the static loads and
        // the positions agree with what it sends.
        var body = new WheelDynamics(v);
        int nw = body.Wheels.Length;
        _wheelSqueal = new VehicleSynth.WheelSquealVoice[nw];
        _wheelFront = new bool[nw]; _wheelDriven = new bool[nw];
        _wheelStatic = new float[nw]; _wheelAt = new Vector3[nw];
        _wheelDemand = new float[nw]; _wheelSlipVelocity = new float[nw]; _wheelLoad = new float[nw]; _wheelGain = new float[nw];
        _wheelStickSlip = new float[nw];
        _wetWater = new float[nw]; _wetTexture = new float[nw]; _wheelWetSqueal = new float[nw];
        var wheelAxle = new int[nw];
        for (int i = 0; i < nw; i++) { wheelAxle[i] = body.Wheels[i].Axle; _wheelWetSqueal[i] = 1f; _wheelGain[i] = 1f; }
        _wet = new OpenFPS.Client.AudioEngine.Core.WetTyres(v, body.Wheels.Select(w => w.Front).ToArray(), wheelAxle, sampleRate, seed + 131);
        float cogZ = chassis.CentreOfGravityZ;
        for (int i = 0; i < nw; i++)
        {
            var w = body.Wheels[i];
            _wheelFront[i] = w.Front; _wheelDriven[i] = w.Driven; _wheelStatic[i] = MathF.Max(1f, w.StaticLoad);
            _wheelAt[i] = new Vector3(w.Y, 0.05f, w.X + cogZ);
        }
        _squealSlipVelocity = SquealReferenceSpeed * MathF.Tan(MathF.Max(0.01f, body.SteeredPeakSlip()));
        if (!string.IsNullOrEmpty(v.AirSystem) && v.DoorChime)
        {
            _chime = OpenFPS.Common.DoorChimeSpec.TransitBus;
            _chimeAmp = 20e-6f * MathF.Pow(10f, _chime.ReferenceDb / 20f);
        }
        if (v.CoolingFan is { } fan)
        {
            // It blows FORWARD, through the radiator and out of the grille, so its axis is the
            // vehicle's. That is also why it belongs in the front tap and not with the tailpipe.
            _fan = new OpenFPS.Client.AudioEngine.Core.Aircraft.BladeRow(fan, sampleRate, Vector3.UnitZ, seed + 53);
            _fanRatio = v.FanDriveRatio > 0f ? v.FanDriveRatio : 1f;
            _fanMaxRpm = fan.RpmMax;
            if (v.FanClutch is { } clutch)
                _cooling = new OpenFPS.Client.AudioEngine.Core.Engine.CoolingSystem(clutch, v.Engine, seed);
            else if (v.ElectricFan is { } relay)
            {
                // A car's fan is on a motor: its speed is the relay's, never the crank's.
                _cooling = new OpenFPS.Client.AudioEngine.Core.Engine.CoolingSystem(relay, v.Engine, seed);
                _electricFan = true;
            }
        }
        // Rolling noise per axle, anchored: one tyre's declared level summed over the tyres at that
        // end — the steered pair at the front (one on a motorcycle), the rest at the back. Divided
        // by the gain the mix below applies to both tyre taps, which the squeal was set against and
        // keeps.
        int tyres = Math.Max(1, v.TyreCount);
        int frontTyres = Math.Clamp(chassis.SteeredTyres, 1, Math.Max(1, tyres - 1));
        float perTyrePa = 20e-6f * MathF.Pow(10f, v.Tyres.ReferenceDb / 20f);
        _rollingFrontPa = perTyrePa * MathF.Sqrt(frontTyres) / (PerAxle * DefaultTyreMix);
        _rollingRearPa = perTyrePa * MathF.Sqrt(Math.Max(1, tyres - frontTyres)) / (PerAxle * DefaultTyreMix);

        // The paths into the cabin, if it has one (CabinPaths). Each wheel rolls at its share of its
        // axle group's power, so the wheels together are the two axles.
        _cabinLayout = CabinPaths.For(v);
        if (_cabinLayout is { } cl && cl.PathOfWheel.Length == nw)
        {
            _cabinTapLive = new int[cl.Count];
            _cabinShare = new float[cl.Count];
            Array.Fill(_cabinShare, 1f);
            _pathLp = new float[cl.Count];
            _pathNow = new float[cl.Count];
            _cornerTyre = new VehicleSynth.TyreVoice[nw];
            _cornerPa = new float[nw]; _cornerRadius = new float[nw]; _wheelSq = new float[nw];
            for (int i = 0; i < nw; i++)
            {
                bool front = _wheelFront[i];
                _cornerPa[i] = (front ? _rollingFrontPa : _rollingRearPa) * MathF.Sqrt(cl.RollingShare[i]);
                _cornerRadius[i] = front ? _frontRadius : _rearRadius;
            }
        }
        else _cabinLayout = null;
    }

    /// <summary>
    /// This block's drive for every wheel's squeal, from the wheels as the server sent them: the
    /// demand, the speed the rubber is dragged over the road (u sqrt(kappa^2 + tan^2 alpha), with u
    /// the vehicle's speed, so a locked wheel slides at the road speed), and the load over the static
    /// load. And each wheel's gain against the tap it goes out through: the ratio of the listener's
    /// distance from that tap to its distance from the wheel, spherical spreading from where the wheel
    /// really is (distances held to half a metre, about the size of the source). False without
    /// wheels, or not this vehicle's count of them: then the axle voices squeal from the overall
    /// demand, as they always did.
    /// </summary>
    private bool WheelsDrive(OpenFPS.Common.Networking.WheelState[]? wheels, bool inside)
    {
        if (wheels == null || wheels.Length != _wheelSqueal.Length) return false;
        float u = MathF.Abs(Driveline.Speed);
        bool placed = _listenerKnown && !inside;
        Vector3 heard = default, rearTap = default, frontTap = default;
        if (placed)
        {
            var rel = new Vector3(Volatile.Read(ref _listenerX), Volatile.Read(ref _listenerY), Volatile.Read(ref _listenerZ));
            rearTap = SplitVoices ? Vehicle.ExhaustSlot : Vehicle.ExhaustOffset;
            frontTap = SplitVoices ? new Vector3(0f, Vehicle.FrontTapHeight, Vehicle.FrontTapZ) : rearTap;
            heard = rel + rearTap;
        }
        for (int i = 0; i < wheels.Length; i++)
        {
            var w = wheels[i];
            float kappa = w.SlipRatioValue, tanAlpha = MathF.Tan(w.SlipAngleRad);
            _wheelDemand[i] = w.DemandFraction;
            // Water in the contact lubricates the stick-snap the squeal is made of (RoadWaterLaw.SquealFactor).
            float wetSqueal = OpenFPS.Common.RoadWaterLaw.SquealFactor(w.Surface, w.WaterMm);
            _wheelWetSqueal[i] = wetSqueal;
            _wheelStickSlip[i] = OpenFPS.Common.RoadSurfaces.StickSlipOf(w.Surface) * wetSqueal;
            _wheelSlipVelocity[i] = u * MathF.Sqrt(kappa * kappa + tanAlpha * tanAlpha);
            _wheelLoad[i] = w.LoadNewtons / _wheelStatic[i];
            if (placed)
            {
                var tap = _wheelFront[i] ? frontTap : rearTap;
                _wheelGain[i] = MathF.Max(0.5f, Vector3.Distance(heard, tap)) / MathF.Max(0.5f, Vector3.Distance(heard, _wheelAt[i]));
            }
            else _wheelGain[i] = 1f;
        }
        return true;
    }

    /// <summary>
    /// The slip each tyre voice plays: the server's overall demand, shared between the axles in the
    /// proportion their worst wheels carry it. Without wheels (or not this vehicle's count of them)
    /// both play the overall figure, as they always did.
    /// </summary>
    private void AxleSlip(float overall, OpenFPS.Common.Networking.WheelState[]? wheels, out float front, out float rear)
    {
        front = rear = overall;
        if (wheels == null || wheels.Length <= _frontWheels) return;
        float f = 0f, r = 0f;
        for (int i = 0; i < wheels.Length; i++)
        {
            float d = wheels[i].DemandFraction;
            if (i < _frontWheels) f = MathF.Max(f, d); else r = MathF.Max(r, d);
        }
        float worst = MathF.Max(f, r);
        if (worst <= 1e-3f) return;
        front = overall * f / worst;
        rear = overall * r / worst;
    }

    /// <summary>The cooling fan's contribution, 0..1 — normally one. Writable so an instrument can
    /// mute it and read what it is worth, the same way the bay leak can be.</summary>
    public float FanMix = 1f;

    /// <summary>The coolant and the fan clutch, on a vehicle whose fan has one.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.Engine.CoolingSystem? _cooling;
    /// <summary>For instruments: the cooling system, or null.</summary>
    public OpenFPS.Client.AudioEngine.Core.Engine.CoolingSystem? Cooling => _cooling;
    private readonly float _rollingFrontPa, _rollingRearPa;
    /// <summary>Wheel positions on the front axle, and each end's rolling radius (its tread tone).</summary>
    private readonly int _frontWheels;
    private readonly float _frontRadius, _rearRadius;

    // ── Sitting in it ───────────────────────────────────────────────────────────────────────────
    //
    // The same engine, heard from the driver's seat instead of the pavement. Nothing new is
    // SYNTHESISED for it: the machine is the machine. What changes is the path — every source now
    // reaches the ear through the body rather than round it — and that path is three mechanisms,
    // each already declared on the vehicle:
    //
    //   * the PANELS, by their mass. A plate's transmission falls 6 dB an octave above
    //     rho*c / (pi*m) (the mass law); for a 0.8 mm steel door that corner is about 20 Hz, so the
    //     firing note gets through and the rasp does not. A first-order low-pass at that corner IS
    //     the mass law, not an approximation of it.
    //   * the SEALS, which have no mass and let everything through a little (VehicleBody.SealLeak).
    //   * the CABIN, a small box of air whose axial modes boom — the same modes the outside voice
    //     carries at CabinLeak, here at full weight, which is what that field's comment always said
    //     an interior mix would do.
    //
    // Plus the one source you only hear from inside because outside it is lost under everything
    // else: the wind over the body, whose power goes as the sixth power of speed.

    /// <summary>
    /// Whether the listener is sitting in this vehicle. Set from the game thread; read per block.
    /// </summary>
    public bool Interior
    {
        get => Volatile.Read(ref _interior) != 0;
        set
        {
            // The cabin's rings before the flag: the producer reads the flag and then the rings, so by
            // the time it renders anything inside, everything it writes into exists.
            if (value && _cabinLayout != null && Volatile.Read(ref _cabinRings) == null) EnsureCabin();
            Volatile.Write(ref _interior, value ? 1 : 0);
        }
    }
    private int _interior;

    // ── The cabin from where each path comes in (CabinPaths) ──────────────────────────────────
    //
    // Inside, the interior model is split into its paths: the bulkhead (this voice's own ring), the
    // exhaust under the floor, each wheel at its corner, the wind at each A-pillar, a bus's door.
    // Every path but the first is written to a ring of its own, and a tap voice reads it from where
    // the path comes in (EngineTapState with a cabin path). Until a path's tap is playing, this voice
    // carries it, so nothing is lost while the taps are being made or if one cannot be; the hand-over
    // is the same sixty-millisecond crossfade the front outlet uses.

    /// <summary>This vehicle's paths into its cabin, or null (no cabin, or OPENFPS_CABIN_PATHS=0).</summary>
    public CabinPaths.Layout? CabinLayout => _cabinLayout;
    private readonly CabinPaths.Layout? _cabinLayout;
    /// <summary>One ring per path after the first. Made on the game thread the first time the
    /// listener sits in this vehicle; the producer writes them, the mixer and the taps read.</summary>
    private float[][]? _cabinRings;
    private const int CabinRingBits = 16;                  // 1.4 s at 48 kHz: past the deepest lead
    /// <summary>Per path, whether a tap is playing it (game thread writes, mixer reads).</summary>
    private readonly int[] _cabinTapLive = Array.Empty<int>();
    /// <summary>Per path, how much of it this voice is still carrying, 0..1. Mixer thread only.</summary>
    private readonly float[] _cabinShare = Array.Empty<float>();
    /// <summary>Per wheel, its own tyre: the rolling noise, squeal and slide of that wheel alone.</summary>
    private readonly VehicleSynth.TyreVoice[] _cornerTyre = Array.Empty<VehicleSynth.TyreVoice>();
    private readonly float[] _cornerPa = Array.Empty<float>(), _cornerRadius = Array.Empty<float>(), _wheelSq = Array.Empty<float>();
    /// <summary>Per path, the panels' mass-law low-pass, and this sample's pressure on the path.</summary>
    private readonly float[] _pathLp = Array.Empty<float>(), _pathNow = Array.Empty<float>();
    /// <summary>The right-hand wind's own noise through the same filters as the left's.</summary>
    private float _windLpR, _windHpR, _windHpInR;
    /// <summary>Samples of the rings still to be cleared after getting out, so a ring read later
    /// never replays a ride.</summary>
    private int _cabinDirty;

    /// <summary>
    /// Where the listener's ear is across the cabin, metres right of its middle (the vehicle's frame).
    /// Game thread writes. Decides which side's open windows the outside comes in by for this ear.
    /// </summary>
    public volatile float CabinEarX;

    private void EnsureCabin()
    {
        var layout = _cabinLayout!;
        var rings = new float[Math.Max(0, layout.Count - 1)][];
        for (int i = 0; i < rings.Length; i++) rings[i] = new float[1 << CabinRingBits];
        _wet.EnableCorners();
        Volatile.Write(ref _cabinRings, rings);
    }

    /// <summary>Whether path <paramref name="path"/> has a voice of its own playing it. Game thread.</summary>
    public void SetCabinTapLive(int path, bool live)
    {
        if (path > 0 && path < _cabinTapLive.Length) Volatile.Write(ref _cabinTapLive[path], live ? 1 : 0);
    }

    /// <summary>One sample of cabin path <paramref name="path"/> (1 and up), absolute, interpolated:
    /// what a cabin tap reads, on the cursor rules of <see cref="EngineTapState"/>.</summary>
    public float ReadCabinAt(int path, double position)
    {
        var rings = Volatile.Read(ref _cabinRings);
        if (rings == null || path < 1 || path > rings.Length) return 0f;
        var r = rings[path - 1];
        long i0 = (long)Math.Floor(position);
        float f = (float)(position - i0);
        int mask = r.Length - 1;
        float a = r[(int)(i0 & mask)], b = r[(int)((i0 + 1) & mask)];
        return Soft(a + (b - a) * f);
    }

    /// <summary>The cabin paths this voice still carries at sample <paramref name="j"/> (absolute),
    /// sliding each path's share toward whether its tap is playing. Mixer thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float CarriedCabin(float[][] rings, long at, float step)
    {
        float sum = 0f;
        for (int p = 1; p < _cabinShare.Length && p - 1 < rings.Length; p++)
        {
            float target = Volatile.Read(ref _cabinTapLive[p]) != 0 ? 0f : 1f;
            _cabinShare[p] += Math.Clamp(target - _cabinShare[p], -step, step);
            if (_cabinShare[p] <= 0f) continue;
            var r = rings[p - 1];
            sum += r[(int)(at & (r.Length - 1))] * _cabinShare[p];
        }
        return sum;
    }

    private readonly BodyResonator _cabin;
    private readonly float _panelCorner, _sealLeak, _windPaAt110, _starterPath;
    private float _starterLp1, _starterLp2;
    /// <summary>What of the starter reaches the kerb past the sill and the wheels, as a pressure
    /// fraction: about six decibels of shielding.</summary>
    private const float StarterUnderbody = 0.5f;
    /// <summary>Where the starter's path through the mounts and the floor loses its top, Hz. The
    /// mounts pass the gear mesh's low partials and the floor's damping mat soaks up the rest: from
    /// the seat a starter is a muffled whirr, not the buzz it is at the bellhousing. Writable so an
    /// instrument can bracket it.</summary>
    public float StarterPathCornerHz = 150f;
    private float _panelLp, _windLp, _windHp, _windHpIn, _interiorMix;

    // ── The windows down ──────────────────────────────────────────────────────────────────────────
    //
    // A side window rolled down is a hole in the cabin's wall: a tenth of a hatchback's whole wall area
    // with all four down. A hole has no mass, so what is outside comes in through it at every frequency,
    // at the share of the wall it is (as power): the engine and the tyres as the street hears them, and
    // the wind as it is outside the glass rather than through it. And the cabin becomes a Helmholtz
    // resonator, the air in the box the spring and the air in the open windows its mass: the shear layer
    // over an opening sheds vortices at about 0.45 U / L, and where that meets the cabin's note it locks
    // in and the whole cabin throbs. One window open, it does at motorway speed; every window open, the
    // note climbs past anything the shear layer reaches on a road and the cabin only roars.
    private readonly float _windowShareFull, _windOutside, _cabinHelmholtzHz, _windowRunM, _windowFromSources;
    private readonly int _windowCount;
    private float _windowsNow, _buffetY1, _buffetY2;
    /// <summary>The shear layer's Strouhal number over an opening, by its length along the flow.</summary>
    private const float ShearStrouhal = 0.45f;
    /// <summary>A locked-in cabin at its worst is ten or twenty pascals (110-120 dB): about two per cent of
    /// the dynamic pressure over the opening, at the resonance.</summary>
    private const float BuffetShare = 0.02f;

    // ── The loudness law, applied to what the engine is doing now ─────────────────────────────
    //
    // The mix places every source by Loudness.Place, which compresses its DECLARED level toward the
    // ceiling (x0.45), so a 59 dB air conditioner is lifted a long way and a 94 dB car a little. An
    // engine's declared level is its loudest second — full load — and idling it is 25 dB under that,
    // uncompressed. So an idling hatchback three metres away rendered ELEVEN decibels under a window
    // air conditioner three metres away, when physically it is nine decibels over it: "I start the
    // car, get out, and it doesn't sound like it's running." It was running, at 750 rpm, the whole
    // time.
    //
    // The fix is the same law applied to the deficit: however far under its declared level the
    // machine is running, the voice is lifted by (1 - 0.45) of that, slowly (half a second), so an
    // idling car sits where an idling car's level would have been placed. On for live voices only
    // (the provider sets it): offline renders — the lab, the tests — still measure true pascals.
    public bool CompensateLevel;
    /// <summary>For the log: the last block's output, dBFS RMS; the envelope; the idle lift as a gain.</summary>
    public float LastOutputDb => Volatile.Read(ref _lastOutputDb);
    private float _lastOutputDb = -120f;
    public float EnvelopeNow => _envelope;
    public float LiftNow => _levelGain;
    private double _levelMs;
    private float _levelGain = 1f;
    private const float LevelSeconds = 0.5f;
    /// <summary>The unweighted lift's cap, which only the ear model switched off still uses (see
    /// <see cref="LiftDb(float, float)"/>). With the model on there is no cap: see the three-argument form.</summary>
    private const float UnweightedMaxLiftDb = 20f;

    /// <summary>
    /// The lift as it was before the ear model: the unweighted law applied to the deficit, held to
    /// 0..20 dB. What runs with the model off (/ear off, OPENFPS_EAR_MODEL=0).
    /// </summary>
    internal static float LiftDb(float runningDb, float declaredDb)
    {
        float running = MathF.Min(runningDb, declaredDb);
        float lift = Loudness.PlacedDb(running) - Loudness.PlacedDb(declaredDb) - (running - declaredDb);
        return Math.Clamp(lift, 0f, UnweightedMaxLiftDb);
    }

    /// <summary>
    /// How far to lift a voice placed as <paramref name="declaredDb"/> of the reference sound (the law's
    /// placement of its declared level) that is actually running at <paramref name="runningDb"/> with
    /// spectrum <paramref name="running"/>: so it is heard where the law, in loudness units, puts what
    /// it is doing now, level and tone (docs/EAR_MODEL.md). An idling engine is mostly bass the ear
    /// barely hears, and the unweighted law pulled it down for bass nobody hears.
    ///
    /// No cap: the 20 dB the unweighted lift was held to only bounded that law. Below the threshold of
    /// hearing there is nothing to place, and the caller holds the lift where it was (NaN here).
    /// </summary>
    internal static float LiftDb(float runningDb, OpenFPS.Common.Hearing.Timbre? running, float declaredDb)
    {
        if (!OpenFPS.Common.Hearing.EarModel.Enabled) return LiftDb(runningDb, declaredDb);
        var timbre = running ?? OpenFPS.Common.Hearing.Timbre.Speech;
        if (timbre.Sones(runningDb) <= 0f) return float.NaN;
        return Loudness.PlacedDb(runningDb, timbre) - Loudness.PlacedDb(declaredDb) - (runningDb - declaredDb);
    }

    // ── What the engine's own sound is made of, for the lift and the compensation ────────────
    //
    // Measured from `engineOnly` (the machine radiating, without the air brakes and the beeper and
    // without the listener's angle to the pipe), on this voice's render thread: the shape of the
    // spectrum, smoothed over about a second, analysed every quarter second.
    private OpenFPS.Common.Hearing.LiveBands? _bands;
    private readonly float[] _bandScratch = new float[1024];
    private int _bandFill;
    private OpenFPS.Common.Hearing.Timbre? _timbre, _liftTimbre;
    private float _liftDb, _liftAtDb, _liftCompression;
    private bool _liftKnown, _liftEar;

    /// <summary>The spectrum the engine is making now (null until it has sounded).</summary>
    public OpenFPS.Common.Hearing.Timbre? RunningTimbre => Volatile.Read(ref _timbre);

    /// <summary>The level the machine is running at, dB SPL at a metre, smoothed over half a second.</summary>
    public float RunningLevelDb => _levelMs > 0 ? 10f * MathF.Log10((float)_levelMs / (20e-6f * 20e-6f) + 1e-12f) : 0f;

    /// <summary>The lift in force, dB: what the law in loudness units adds to this voice.</summary>
    public float LiftDbNow => 20f * MathF.Log10(MathF.Max(1e-6f, _levelGain));

    private void FlushBands()
    {
        if (_bandFill == 0) return;
        _bands ??= new OpenFPS.Common.Hearing.LiveBands((int)SampleRate);
        _bands.Write(new ReadOnlySpan<float>(_bandScratch, 0, _bandFill));
        _bandFill = 0;
    }

    /// <summary>Pressure fraction through an open bus doorway: sqrt(2.4 m^2 / ~106 m^2) = 0.15.</summary>
    private const float DoorwayLeak = 0.15f;

    /// <summary>The speed the wind anchor is quoted at, m/s: 110 km/h.</summary>
    private const float WindReferenceSpeed = 110f / 3.6f;

    /// <summary>How much of the car's own body ringing reaches the mix, 0..1. One normally;
    /// writable so an instrument can mute it and read what the panels are worth.</summary>
    public float BodyMix = 1f;

    /// <summary>The car's own resonances. Built once: the modes are a property of the vehicle, not
    /// of what it is doing.</summary>
    private readonly BodyResonator _body;

    // ── The air a bus or a truck carries, and the brakes that use it ───────────────────────────
    //
    // Nothing on the wire says "the brakes came off". It does not have to: the voice already
    // follows the vehicle's speed, and a brake release is what happens at the END of a
    // deceleration, a park brake is what happens after a vehicle has stood still for a moment, and
    // the doors of a bus open when it has stopped and shut before it moves. So the events are read
    // off the speed history the voice keeps anyway, on the render thread, at sample rate.
    /// <summary>Whether the doors are standing open, which is what the beeper runs on. Exposed so
    /// a test can tell "the beeper is inaudible" from "the doors never opened" — two completely
    /// different faults that sound identical from outside.</summary>
    public bool DoorsOpen => _doorsOpen;

    /// <summary>Whether this voice built a door beeper at all.</summary>
    public bool HasDoorChime => _chime != null;

    /// <summary>The largest beeper sample this voice has produced, pascals. Diagnostic.</summary>
    public float PeakChimePa { get; private set; }

    private readonly AirSystem? _air;
    // The door beeper: a piezo behind a grille over the doorway. Runs while the bus is knelt with
    // its doors open, which is a state the voice already knows from its own speed history.
    private readonly OpenFPS.Common.DoorChimeSpec? _chime;
    private readonly float _chimeAmp;
    private double _chimePhase, _chimeCycle;
    private float _chimeEnv;
    /// <summary>The cooling fan, for a vehicle whose fan is on the engine rather than on a relay.
    /// The same BladeRow a propeller and a mower blade are; see VehicleProfile.CoolingFan.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.Aircraft.BladeRow? _fan;
    private readonly float _fanRatio, _fanMaxRpm;
    /// <summary>The fan runs on its own motor (VehicleProfile.ElectricFan), not off the crank.</summary>
    private readonly bool _electricFan;
    /// <summary>
    /// The air round the vehicle, degrees C, for its cooling system: what decides whether a car's
    /// driver has the air conditioning on, and so whether its fan runs at a standstill. NaN (the
    /// default) takes the world's, AudioPhysics.CurrentAirCelsius, which the provider keeps from the
    /// server's weather; an instrument or a test sets it outright.
    /// </summary>
    public float AmbientCelsius = float.NaN;
    private float _bayLeak;
    /// <summary>
    /// The intake as it leaves through the bay: the same noise as at the grille, but not the same
    /// waveform. What reaches the bay openings is the airbox and ducting heard off the block, the
    /// bulkheads and the underside of the bonnet — scattered, a few milliseconds of paths, so it is
    /// not the grille's waveform a second time.
    /// </summary>
    private readonly EchoDiffuser _bayIntake;
    private readonly bool _engineAtRear;
    /// <summary>Which way the tailpipe throws its sound and what the body does to it, for where the
    /// listener is. See ExhaustRadiation.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.Engine.ExhaustRadiation _radiation;
    /// <summary>Where the bay's noise leaves the vehicle, grille and open floor, and what the body does
    /// to it on the way to the listener. See BayRadiation.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.Engine.BayRadiation _bayRadiation;
    /// <summary>The bay's radiation, for tests and instruments.</summary>
    internal OpenFPS.Client.AudioEngine.Core.Engine.BayRadiation BayRadiation => _bayRadiation;
    /// <summary>The pipe's radiation, for tests and instruments.</summary>
    internal OpenFPS.Client.AudioEngine.Core.Engine.ExhaustRadiation Radiation => _radiation;
    /// <summary>The front brakes singing at the end of a stop, on a vehicle whose brakes do. See
    /// BrakeSqueal.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.BrakeSqueal _squeal;
    private float _squealDecel, _squealLastSpeed;
    /// <summary>Whether this vehicle's brakes squeal, and at what. For tests and instruments.</summary>
    internal OpenFPS.Client.AudioEngine.Core.BrakeSqueal Squeal => _squeal;
    /// <summary>The brake squeal's contribution, 0..1; writable so an instrument can take it out and
    /// read what it was.</summary>
    public float SquealMix = 1f;
    /// <summary>The door beeper hangs over the door, so it is heard from the end the door is at.</summary>
    private readonly bool _chimeAtFront = true;

    /// <summary>Is a point this far along the vehicle (its own frame, +Z forward) nearer the front
    /// outlet than the back one?</summary>
    internal static bool NearerFront(VehicleProfile v, float z)
        => MathF.Abs(z - v.FrontTapZ) < MathF.Abs(z - v.ExhaustOffsetZ);
    /// <summary>How rough the bay is to the intake noise: a cluttered cavity, about brick
    /// (EchoDiffuser's scale; roughly six milliseconds of smear).</summary>
    private const float BayScattering = 0.5f;

    /// <summary>
    /// What escapes the engine bay, 0..1 — normally the vehicle's own
    /// <see cref="VehicleProfile.EngineBayLeakage"/>, writable so an instrument can mute it.
    ///
    /// The only way to answer "how much of this bus am I hearing through the bonnet" is to render
    /// the same voice twice and difference the two, which is the same rule `--engine-orders jet=0`
    /// established for the gas path: read the CONTRIBUTION, not the constant.
    /// </summary>
    public float BayLeakage { get => _bayLeak; set => _bayLeak = Math.Clamp(value, 0f, 1f); }
    private float _lastSpeedForAir, _accelForAir;
    private float _brakedSeconds, _stoppedSeconds, _peakBrake;
    private bool _parked, _doorsOpen, _holding;
    /// <summary>m/s²: a foot on the pedal. Lifting off at city speed is drag and engine braking,
    /// a few tenths; slowing for a corner on the racing line is where the old 0.45 hissed.</summary>
    private const float BrakingDecel = 0.7f;
    /// <summary>What full service pressure stops a vehicle at, m/s². The chambers hold a pressure in
    /// proportion to the braking asked for, and a release vents what they hold.</summary>
    private const float FullServiceDecel = 6.0f;
    /// <summary>How long after coming to rest at a bus stop the spring brakes go on.</summary>
    private const float ParkAfterSeconds = 1.2f;

    private void AirEvents(float dt)
    {
        if (_air == null) return;
        _air.EngineRpm = Engine.Rpm;
        float accel = (_speedSmooth - _lastSpeedForAir) / dt;
        _lastSpeedForAir = _speedSmooth;
        _accelForAir += (accel - _accelForAir) * MathF.Min(1f, dt * 6f);
        bool moving = _speedSmooth > 0.15f;
        bool braking = moving && _accelForAir < -BrakingDecel;
        if (braking)
        {
            _brakedSeconds += dt;
            _peakBrake = MathF.Max(_peakBrake, -_accelForAir);
        }
        else if (!moving && _brakedSeconds > 0.25f)
        {
            // Came to rest on the brake: the driver keeps a foot on it. Nothing vents until the pedal
            // comes up — pulling away, or the spring brakes going on at a bus stop.
            _holding = true;
            _brakedSeconds = 0f;
        }
        else if (_brakedSeconds > 0.25f)
        {
            // A dab while rolling: the pedal comes up and the chambers exhaust what they held.
            ReleaseService();
            _brakedSeconds = 0f;
        }
        else _brakedSeconds = 0f;

        if (!moving)
        {
            _stoppedSeconds += dt;
            // Only at a stop that takes passengers. At a junction or a crossing the driver holds the
            // service brake and goes again; setting the park brake, kneeling and opening the doors
            // there was every give-way on the city ending in a long blow of air.
            if (!_parked && ServingStop && _stoppedSeconds > ParkAfterSeconds)
            {
                _parked = true;
                if (_holding) ReleaseService();             // foot off the pedal as the springs take it
                _air.Vent("parking");                       // spring brakes: the chambers dump
                // And then it kneels — the suspension bags on the kerb side dump and the body
                // drops a hundred millimetres. A long, low hiss with a great deal of volume behind
                // it, and the one sound that says "bus at a stop" rather than "vehicle stopped".
                // The port was declared on the transit bus from the start and nothing ever fired
                // it, because nothing on a track ever stood still.
                if (_air.Ports.ContainsKey("kneel")) _air.Vent("kneel");
                if (_air.Ports.ContainsKey("door")) { _air.Vent("door"); _doorsOpen = true; }
            }
        }
        else
        {
            if (_parked)
            {
                // Moving off again: the doors shut first, then the park brake is released — a
                // shorter hiss, since only the control line vents while the springs are pushed back.
                if (_doorsOpen) { _air.Vent("door", 0.6f); _doorsOpen = false; }
                _air.Vent("parking", 0.35f);
                _parked = false;
            }
            else if (_holding) ReleaseService();            // off the brake and away
            _stoppedSeconds = 0f;
        }
    }

    /// <summary>
    /// The service chambers exhausting, through the quick-release valve: a short puff, at the
    /// pressure the braking put in them. A gentle stop is a quarter of full service and a quiet
    /// "pssht"; it was vented by how LONG the pedal had been down, so a slow three-second stop to a
    /// junction dumped the full fourteen litres.
    /// </summary>
    private void ReleaseService()
    {
        _air!.Vent("service_release", Math.Clamp(_peakBrake / FullServiceDecel, 0.1f, 1f));
        _peakBrake = 0f;
        _holding = false;
    }

    /// <summary>
    /// One sample of the door beeper. Sounds only while the doors are open, which the voice knows
    /// already — nothing new has to be told to it.
    /// </summary>
    private float StepChime()
    {
        var c = _chime!;
        float dts = 1f / SampleRate;
        // The pulse train: a beep, then a gap, at the declared rate.
        _chimeCycle += c.RateHz * dts;
        if (_chimeCycle >= 1.0) _chimeCycle -= 1.0;
        bool on = _doorsOpen && _chimeCycle < c.Duty;
        // A piezo is light but not massless: it takes a few milliseconds to start and to stop, and
        // an instant edge is a click rather than a beep.
        float step = dts / MathF.Max(1e-4f, c.EdgeSeconds);
        _chimeEnv += Math.Clamp((on ? 1f : 0f) - _chimeEnv, -step, step);
        if (_chimeEnv <= 1e-4f) return 0f;

        _chimePhase += c.ToneHz * dts;
        if (_chimePhase >= 1.0) _chimePhase -= 1.0;
        double w = _chimePhase * 2.0 * Math.PI;
        float y = (float)(Math.Sin(w) + c.SecondHarmonic * Math.Sin(2.0 * w));
        // Held to the declared level whatever the harmonic content is.
        float norm = 1f / MathF.Sqrt(0.5f * (1f + c.SecondHarmonic * c.SecondHarmonic)) * 0.7071f;
        float outPa = y * norm * _chimeAmp * _chimeEnv;
        if (MathF.Abs(outPa) > PeakChimePa) PeakChimePa = MathF.Abs(outPa);
        return outPa;
    }

    /// <summary>How many body modes this voice is running. Diagnostic, for the cost report.</summary>
    public int BodyModeCount => _body.ModeCount;

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

        // The highest gear that keeps the engine under its upshift point and over the point the
        // driver would change down at — what a driver would be in. Without the second condition a
        // truck placed at 40 km/h went in eighth at 600 rpm and changed down twice the moment its
        // voice started, which is a burst of shifts every time a truck came within earshot.
        var gb = Vehicle.Gearbox;
        float downAt = MathF.Max(gb.DownshiftRpm, Vehicle.Engine.IdleRpm * 1.5f) * 1.1f;
        int gear = 0;
        for (int g = gb.TopGear; g >= 1 && gear == 0; g--)
        {
            float r = gb.RpmFor(_speedSmooth, g);
            if (r <= gb.UpshiftRpm && r >= downAt) gear = g;
        }
        if (gear == 0)
            for (int g = gb.TopGear; g >= 1; g--)
                if (gb.RpmFor(_speedSmooth, g) <= gb.UpshiftRpm) { gear = g; break; }
        if (gear == 0) gear = 1;
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
        bool inside = Interior;
        var cabinRings = _cabinLayout != null ? Volatile.Read(ref _cabinRings) : null;
        // Inside, the listener is AT the machine, and the outside-listener geometry (which tailpipe
        // is nearer, which way the fan blows) has nothing to say about a sound that comes through
        // the floor.
        if (_listenerKnown && !inside)
        {
            var heard = new Vector3(Volatile.Read(ref _listenerX), Volatile.Read(ref _listenerY), Volatile.Read(ref _listenerZ));
            Engine.SetListener(heard);
            // The listener arrives relative to where this voice is placed: the true tailpipe once the
            // two ends have voices of their own, the compromise point before (VehicleProfile.ExhaustOffset).
            var placedAt = SplitVoices ? Vehicle.ExhaustSlot : Vehicle.ExhaustOffset;
            _radiation.Aim(heard + placedAt);
            _bayRadiation.Aim(heard + placedAt);
        }
        else { _radiation.Aim(null); _bayRadiation.Aim(null); }
        float panelA = 1f - MathF.Exp(-2f * MathF.PI * _panelCorner * dt);
        float starterA = 1f - MathF.Exp(-2f * MathF.PI * StarterPathCornerHz * dt);
        // The lift for this block, from the level the machine has been running at lately.
        float liftTarget = 1f;
        if (CompensateLevel && _levelMs > 0)
        {
            float nowDb = 10f * MathF.Log10((float)_levelMs / (20e-6f * 20e-6f) + 1e-12f);
            // The voice is placed as a source of its DECLARED level; running below that, it should be
            // heard as the law places a source of the level it is actually running at, and of the
            // spectrum it is actually making (an idle is mostly bass). The difference is the lift. Below
            // the mix's ceiling that is about (1 - compression) of the shortfall in loudness; above it
            // the law is literal. Worked out when the spectrum is re-measured or the level has moved,
            // not every block: the level is a half-second average anyway.
            if (_bands != null && _bands.Update(0.25f, 1.0f))
                Volatile.Write(ref _timbre, OpenFPS.Common.Hearing.Timbre.FromBandPowers(_bands.BandPowers, "engine", live: true));
            bool ear = OpenFPS.Common.Hearing.EarModel.Enabled;
            float compression = Loudness.DynamicRangeCompression;
            if (!_liftKnown || MathF.Abs(nowDb - _liftAtDb) > 0.25f || !ReferenceEquals(_liftTimbre, _timbre)
                || ear != _liftEar || compression != _liftCompression)
            {
                float lift = LiftDb(nowDb, _timbre, Vehicle.SourceLevelDb);
                // Silence has no place in the law: hold what the sound had while it could be heard.
                if (!float.IsNaN(lift)) { _liftDb = lift; _liftKnown = true; }
                _liftAtDb = nowDb;
                _liftTimbre = _timbre;
                _liftEar = ear;
                _liftCompression = compression;
            }
            if (_liftKnown) liftTarget = MathF.Pow(10f, _liftDb / 20f);
        }
        // Either way: a floor on the step made a FALLING lift rise through the block and then snap down
        // to its target at the end of it, a step in the waveform every block while an engine revved.
        float liftStep = (liftTarget - _levelGain) / MathF.Max(1, count);
        var wheels = Wheels;
        bool perWheel = WheelsDrive(wheels, inside);
        // The water under each wheel this block: its own when the server sends the wheels, the road's
        // wheel-path figure when it does not.
        float fallbackWater = RoadWaterMm;
        for (int k = 0; k < _wetWater.Length; k++)
        {
            bool sent = perWheel && wheels != null;
            byte surface = sent ? wheels![k].Surface : OpenFPS.Common.RoadSurfaces.IndexOf(OpenFPS.Common.RoadData.DefaultSurface);
            _wetWater[k] = sent ? wheels![k].WaterMm : fallbackWater;
            _wetTexture[k] = OpenFPS.Common.RoadWaterLaw.HoldsMm(surface);
            if (!sent) _wheelGain[k] = 1f;
        }
        _wet.Block(_wetWater, _wheelGain, _wetTexture, Driveline.Speed, count);
        float wetMix = TyreMix / DefaultTyreMix;
        double blockSum = 0, tyreSum = 0, outSum = 0;
        float windLpA = 1f - MathF.Exp(-2f * MathF.PI * 1200f * dt);
        float windHpA = MathF.Exp(-2f * MathF.PI * 180f * dt);
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
            _tyreChirp *= _chirpDecay;
            // Two axles, two tyre noises. They are different tyres on different patches of road, so
            // their noise is INDEPENDENT. One signal written to both ends would be the same roar
            // coming from two places a few metres apart, which combs against itself as the car goes
            // by and is heard as a car passing inside out.
            float tyreRear, tyreFront;
            float frontSlip = 0f, rearSlip = 0f;
            if (perWheel)
            {
                // Each wheel squeals for itself; the axle voices roll, and carry the squeal of the
                // wheels at their end out through the same output stage.
                float frontSliding = 0f, rearSliding = 0f;
                for (int k = 0; k < _wheelSqueal.Length; k++)
                {
                    float demand = _wheelDemand[k] + (_wheelDriven[k] ? _tyreChirp : 0f);
                    float sq = VehicleSynth.WheelSqueal(Vehicle.Tyres, demand, _wheelSlipVelocity[k], _wheelLoad[k], _squealSlipVelocity,
                                                         _wheelSqueal.Length, _rng, ref _wheelSqueal[k], _wheelStickSlip[k], SampleRate)
                             * _wheelGain[k] * _wheelWetSqueal[k];
                    if (_wheelFront[k]) frontSliding += sq; else rearSliding += sq;
                    if (k < _wheelSq.Length) _wheelSq[k] = sq;
                }
                tyreRear = VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, 0f, _rng, ref _tyre, _rollingRearPa, _rearRadius, rearSliding, SampleRate);
                tyreFront = VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, 0f, _rng, ref _tyreFront, _rollingFrontPa, _frontRadius, frontSliding, SampleRate);
            }
            else
            {
                AxleSlip(RoadSlip, wheels, out frontSlip, out rearSlip);
                tyreRear = VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, rearSlip + _tyreChirp, _rng, ref _tyre, _rollingRearPa, _rearRadius, sampleRate: SampleRate);
                tyreFront = VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, frontSlip + _tyreChirp, _rng, ref _tyreFront, _rollingFrontPa, _frontRadius, sampleRate: SampleRate);
            }
            float rearTyre = tyreRear * PerAxle * TyreMix;
            float frontTyre = tyreFront * PerAxle * TyreMix;
            // The water: already pascals at a metre, each end's wheels at that end's tap.
            _wet.Step();
            rearTyre += _wet.Rear * wetMix;
            frontTyre += _wet.Front * wetMix;
            // The front of the machine: the tyres at that end, the fan, and what the bay lets out.
            //
            // The BLOCK is not added here directly. One mechanism, one route: the block gets outside
            // through the bay, and how much of it does is VehicleProfile.EngineBayLeakage, which every
            // vehicle declares from what is actually around its engine. A second, constant route
            // here would be a fixed attenuation with nothing behind it; on a car that hardly matters,
            // because a car is its exhaust, but a bus's block measures seven decibels above its
            // silenced tailpipe (`--voice-levels parts`), so it is most of the machine.
            //
            // The INTAKE leaves the same way. A snorkel draws from inside the wing or behind the
            // grille, under the same bonnet as the block, and pass-by source separations find the
            // block, not the intake, dominating a car's front microphone (ISMA 2014). Straight out of
            // the front at the full orifice level it would be 10-18 dB of the front of a V8 at full
            // throttle. So it leaves through the bay; a bike, with no bay, lets all of it out.
            float front = frontTyre;
            float fanOut = 0f;
            if (_fan != null)
            {
                // The fan is geared to the crank and has no throttle: it turns at engine speed through
                // its clutch, if it has one (CoolingSystem), and its loading is the air it is pushing,
                // which is all it ever pushes. Its speed is set on the slow tick like everything else
                // that does not change per sample.
                if ((i & 63) == 0)
                {
                    if (_cooling != null)
                    {
                        _cooling.AmbientCelsius = float.IsNaN(AmbientCelsius)
                            ? OpenFPS.Client.AudioEngine.Core.AudioPhysics.CurrentAirCelsius : AmbientCelsius;
                        _cooling.Step(dt * 64f, Engine.Rpm, Engine.LoadTorque, Driveline.Speed);
                    }
                    float fanRpm = _electricFan
                        ? _fanMaxRpm * (_cooling?.FanSpeedFraction ?? 0f)
                        : Engine.Rpm * _fanRatio * (_cooling?.FanSpeedFraction ?? 1f);
                    _fan.SetSpeed(fanRpm, 1f,
                                  _listenerKnown
                                      ? new Vector3(Volatile.Read(ref _listenerX), Volatile.Read(ref _listenerY), Volatile.Read(ref _listenerZ))
                                      : Vector3.UnitZ);
                }
                fanOut = _fan.Step() * FanMix;
                if (!_engineAtRear) front += fanOut;
            }
            // The pipe's radiation, thrown the way the pipe points and shaded by the body.
            float exhaustOut = _radiation.Process(Engine.Exhaust);
            float pa = exhaustOut + rearTyre;
            // An engine in the back (VehicleProfile.EngineAtRear) cools, breathes and leaks out of its
            // compartment there, beside the tailpipe.
            if (_engineAtRear) pa += fanOut;

            // ...and then the car it is all bolted into. The body is driven by everything above and
            // rings on its own account, so it is ADDED to the direct sound rather than replacing it:
            // the tailpipe still radiates straight at the listener, and the panels ring as well.
            // This is the one part of a vehicle that is linear and time-invariant, which is why it
            // can be a fixed impulse response while the gas path — whose resonances move 75 % with
            // exhaust temperature — cannot. See VehicleBody.
            // Driven by the EXHAUST, not by the finished mix: that is what physically shakes a
            // floorpan, and it is how the offline VehicleSynth render drives it too. Two renderers,
            // one rule — a body that coloured one and not the other is how a change can be measured
            // as working and heard as nothing.
            pa += _body.Process(Engine.Exhaust) * BodyMix;
            // What escapes the engine bay (VehicleProfile.EngineBayLeakage, from its EngineBay).
            // It leaves from the BAY, which is where the engine is — the intake slot marks it, nose
            // or mid-ship — so it goes out of the front tap, not the back with the tailpipe. On the
            // school bus the block is 97.7 dB against an 83 dB silenced pipe, so out of the back an
            // idling bus would be one sound at its tail. Once far enough to be one voice, nothing changes.
            // It leaves by the grille and the open floor, and the body shades both from a listener
            // behind the vehicle (BayRadiation); `bayLevel` is the same before any shading, for the
            // level the loudness law reads.
            float bayLevel = _bayLeak > 0f ? (Engine.Block - Engine.StarterOut + FrontMix * _bayIntake.Process(Engine.Intake)) * _bayLeak : 0f;
            float bay = _bayRadiation.Process(bayLevel);
            // The starter is not in the bay: it hangs under the car on the bellhousing, behind the
            // sill and the wheels but in no enclosure. Through the bay leak it was 16 dB down and a
            // big V8's start could not be heard from the kerb at all.
            float starterOut = Engine.StarterOut * MathF.Max(_bayLeak, StarterUnderbody);
            bay += starterOut;
            bayLevel += starterOut;
            if (_engineAtRear) pa += bay; else front += bay;
            // The air and the door beeper are their own sources at their own levels, each at its own
            // end of the vehicle: the door valve, the kneeling valve and the beeper at the front door,
            // the brake releases at the axles. None of them comes out of the tailpipe.
            float airOut = 0f, chimeOut = 0f, airFront = 0f, chimeFront = 0f;
            if (_air != null)
            {
                if ((i & 63) == 0) AirEvents(dt * 64f);
                airOut = _air.Step();
                airFront = _air.FrontOut;
                pa += airOut - airFront;
            }
            if (_chime != null)
            {
                chimeOut = StepChime();
                if (_chimeAtFront) chimeFront = chimeOut; else pa += chimeOut;
            }
            // How hard it is braking, from its own speed, every 64 samples like the air.
            if ((i & 63) == 0)
            {
                float rate = (_squealLastSpeed - _speedSmooth) / (64f * dt);
                _squealLastSpeed = _speedSmooth;
                _squealDecel += (rate - _squealDecel) * 0.3f;
            }
            // The front brakes do most of the stopping and most of the singing: the front tap.
            float squealOut = _squeal.Step(_speedSmooth, _squealDecel) * SquealMix;
            float frontExtras = airFront + chimeFront + squealOut;
            float rearExtras = airOut + chimeOut - (airFront + chimeFront);

            // What the ENGINE is radiating, before anything a listener's position does to it: the
            // level the loudness law is applied to. Not the brakes' air or the door beeper, which are
            // their own sources at their own levels and are not what idles.
            // The bay is the engine radiating even though it leaves by the front: the loudness law is
            // applied to the whole machine.
            // Measured WITHOUT the pipe's directivity: a car facing away from you is not a car running
            // quietly, and the idle lift must not turn it back up.
            // Both axles' tyres count: they are the machine radiating too, and at a cruise the larger
            // part of it. Only the rear one is in `pa`, so the front one is added here.
            float engineOnly = pa - rearExtras + (_engineAtRear ? -fanOut : bayLevel) + frontTyre + (Engine.Exhaust - exhaustOut);
            blockSum += (double)engineOnly * engineOnly;
            if (CompensateLevel)
            {
                _bandScratch[_bandFill++] = engineOnly;
                if (_bandFill == _bandScratch.Length) FlushBands();
            }
            tyreSum += (double)(rearTyre * rearTyre + frontTyre * frontTyre);

            // Crossfaded over ~60 ms rather than switched, so getting in or out is not a click.
            _interiorMix += Math.Clamp((inside ? 1f : 0f) - _interiorMix, -envStep, envStep);
            // What the cabin carries that the lift leaves alone (the air and the beeper), in this voice.
            float insideExtras = chimeOut + 0.5f * airOut;
            float doorUnlifted = 0f;
            bool split = false;
            if (_interiorMix > 0f && cabinRings != null)
            {
                // The same model, path by path (CabinPaths): each through its own panels, from where it
                // comes in. Linear throughout, so the paths sum to the one signal below, except that the
                // tyres are a noise per wheel and the wind a noise per side, at the same powers.
                split = true;
                var lay = _cabinLayout!;
                Array.Clear(_pathNow);
                // The bulkhead: the block and the intake through the firewall and the dash.
                float bulkSrc = Engine.Block + Engine.Intake;
                _pathLp[0] += (bulkSrc - _pathLp[0]) * panelA;
                _pathNow[0] = _pathLp[0] + _sealLeak * bulkSrc;
                // The exhaust along the floor.
                int ex = lay.Exhaust;
                float exSrc = 0.5f * Engine.Exhaust;
                _pathLp[ex] += (exSrc - _pathLp[ex]) * panelA;
                _pathNow[ex] += _pathLp[ex] + _sealLeak * exSrc;
                // Every wheel through its arch and the floor at its corner: its own tyre, squeal and spray.
                const float TyreToPanels = 0.6f * 0.70710678f;
                for (int q = 0; q < _cornerTyre.Length; q++)
                {
                    // The tread tone is one tone in phase on every wheel of a size (VehicleSynth.Tyre's
                    // toneScale): each wheel carries its share of it in amplitude, its roar in power.
                    float toneShare = MathF.Sqrt(lay.RollingShare[q]);
                    float corner = perWheel
                        ? VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, 0f, _rng, ref _cornerTyre[q], _cornerPa[q], _cornerRadius[q], _wheelSq[q], SampleRate,
                                            toneScale: toneShare)
                        : VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, (_wheelFront[q] ? frontSlip : rearSlip) + _tyreChirp, _rng,
                                            ref _cornerTyre[q], _cornerPa[q], _cornerRadius[q], sampleRate: SampleRate,
                                            squealScale: 1f / MathF.Sqrt(lay.GroupWheels[q]), toneScale: toneShare);
                    int pq = lay.PathOfWheel[q];
                    float src = corner * TyreToPanels * TyreMix;
                    _pathLp[pq] += (src - _pathLp[pq]) * panelA;
                    _pathNow[pq] += _pathLp[pq] + _sealLeak * src + _wet.CabinWheel(q) * wetMix;
                }
                if (lay.PathOfWheel.Length > 0) _pathNow[lay.PathOfWheel[0]] += _wet.CabinTail * wetMix;
                // The starter through the mounts and the floor of the footwell.
                _starterLp1 += (Engine.StarterOut * _starterPath - _starterLp1) * starterA;
                _starterLp2 += (_starterLp1 - _starterLp2) * starterA;
                _pathNow[0] += _starterLp2;
                // The cabin's own modes, driven by everything through the body, as before. Its boom is
                // the length of the cabin, low enough that where it is played from hardly matters.
                float through = 0f;
                for (int q = 0; q < _pathNow.Length; q++) through += _pathNow[q];
                _pathNow[0] += _cabin.Process(through);

                // The wind at each A-pillar, a noise each, half the power each.
                float vRatio = MathF.Abs(Driveline.Speed) / WindReferenceSpeed;
                float windPa = _windPaAt110 * vRatio * vRatio * vRatio;
                float n = (float)(_rng.NextDouble() * 2.0 - 1.0) * 1.7f;
                float nR = (float)(_rng.NextDouble() * 2.0 - 1.0) * 1.7f;
                _windLp += (n - _windLp) * windLpA;
                _windHp = windHpA * (_windHp + _windLp - _windHpIn);
                _windHpIn = _windLp;
                _windLpR += (nR - _windLpR) * windLpA;
                _windHpR = windHpA * (_windHpR + _windLpR - _windHpInR);
                _windHpInR = _windLpR;
                const float HalfPower = 0.70710678f;
                _pathNow[lay.WindLeft] += _windHp * windPa * 3.78f * HalfPower;
                _pathNow[lay.WindRight] += _windHpR * windPa * 3.78f * HalfPower;

                _windowsNow += (Math.Clamp(WindowsOpen, 0f, 1f) - _windowsNow) * MathF.Min(1f, dt * 10f);
                if (_windowsNow > 0.001f && _windowShareFull > 0f)
                {
                    float share = _windowsNow * _windowShareFull;
                    float hole = MathF.Sqrt(share);
                    // The outside through the window beside this ear: one signal, one place.
                    int nearSide = CabinEarX > 0f ? lay.WindRight : lay.WindLeft;
                    _pathNow[nearSide] += (pa - rearExtras + bay) * hole * _windowFromSources;
                    // Each side's wind in through its own windows.
                    _pathNow[lay.WindLeft] += _windLp * windPa * _windOutside * hole * 1.7f * HalfPower;
                    _pathNow[lay.WindRight] += _windLpR * windPa * _windOutside * hole * 1.7f * HalfPower;
                    // The cabin's throb is the whole cabin's air at once: the same at both ears whatever
                    // it is played from.
                    float speed = MathF.Abs(Driveline.Speed);
                    if (speed > 3f && _cabinHelmholtzHz > 0f)
                    {
                        float shedHz = ShearStrouhal * speed / MathF.Max(0.2f, _windowRunM);
                        float off = MathF.Log(shedHz / _cabinHelmholtzHz) / 0.25f;
                        float locked = MathF.Exp(-off * off);
                        if (locked > 1e-3f)
                        {
                            float w0 = 2f * MathF.PI * _cabinHelmholtzHz * dt;
                            float r = MathF.Exp(-w0 / (2f * (6f / MathF.Max(1, _windowCount))));
                            float y = n * (1f - r) + 2f * r * MathF.Cos(w0) * _buffetY1 - r * r * _buffetY2;
                            _buffetY2 = _buffetY1; _buffetY1 = y;
                            float q = 0.5f * 1.2f * speed * speed;
                            _pathNow[0] += y * locked * BuffetShare * q * _windowsNow;
                        }
                    }
                }

                // The door beeper, the door engines and an open doorway, at the door if there is one.
                int doorPath = lay.Door >= 0 ? lay.Door : 0;
                if (_doorsOpen) _pathNow[doorPath] += (pa - rearExtras + bay) * DoorwayLeak;
                if (lay.Door >= 0) { doorUnlifted = chimeOut + 0.5f * airOut; insideExtras = 0f; }
                else _pathNow[0] += chimeOut + 0.5f * airOut;
                float k = _interiorMix;
                pa = pa * (1f - k) + _pathNow[0] * k;
                front *= 1f - k;
            }
            else if (_interiorMix > 0f)
            {
                    // What arrives at the outside of the cabin: the engine bay just ahead of the
                    // firewall, the exhaust along the floor to a tailpipe a couple of metres back, and
                    // all four tyres under the floor.
                    // All four tyres, at the power the single signal had.
                    float atPanels = Engine.Block + Engine.Intake + 0.5f * Engine.Exhaust + (tyreRear + tyreFront) * 0.6f * 0.70710678f * TyreMix;
                    _panelLp += (atPanels - _panelLp) * panelA;
                    float inCabin = _panelLp + _sealLeak * atPanels;
                    // The starter through the mounts and the floor (VehicleBody.StarterPathLossDb).
                    // Through rubber and a damped floor the top is gone: two poles at the path's corner.
                    _starterLp1 += (Engine.StarterOut * _starterPath - _starterLp1) * starterA;
                    _starterLp2 += (_starterLp1 - _starterLp2) * starterA;
                    inCabin += _starterLp2;
                    // The spray on the arches and the floor, through the wheelhouses and the trim.
                    inCabin += _wet.Cabin * wetMix;
                    inCabin += _cabin.Process(inCabin);

                    // The wind: broadband turbulence, most of it between a couple of hundred hertz and a
                    // kilohertz by the time it is through the glass. Pressure goes as speed cubed.
                    float vRatio = MathF.Abs(Driveline.Speed) / WindReferenceSpeed;
                    float windPa = _windPaAt110 * vRatio * vRatio * vRatio;
                    float n = (float)(_rng.NextDouble() * 2.0 - 1.0) * 1.7f;     // ~unit RMS
                    _windLp += (n - _windLp) * windLpA;
                    _windHp = windHpA * (_windHp + _windLp - _windHpIn);
                    _windHpIn = _windLp;
                    inCabin += _windHp * windPa * 3.78f;         // the band-limited noise is 0.265 RMS; this is its inverse

                    // The windows. Followed over about a tenth of a second, so a glass moving is a glide.
                    _windowsNow += (Math.Clamp(WindowsOpen, 0f, 1f) - _windowsNow) * MathF.Min(1f, dt * 10f);
                    if (_windowsNow > 0.001f && _windowShareFull > 0f)
                    {
                        float share = _windowsNow * _windowShareFull;
                        float hole = MathF.Sqrt(share);                // pressure through the opening
                        // The outside, straight in: the engine bay, the exhaust, the tyres, as heard outside
                        // at the windows.
                        inCabin += (pa - rearExtras + bay) * hole * _windowFromSources;
                        // The wind outside the glass, in through the hole: broader than through the glass,
                        // because nothing has taken its bottom or its top away. (_windLp is low-passed
                        // noise of about 0.6 RMS.)
                        inCabin += _windLp * windPa * _windOutside * hole * 1.7f;
                        // The cabin's throb, where the shear layer's shedding meets its note.
                        float speed = MathF.Abs(Driveline.Speed);
                        if (speed > 3f && _cabinHelmholtzHz > 0f)
                        {
                            float shedHz = ShearStrouhal * speed / MathF.Max(0.2f, _windowRunM);
                            float off = MathF.Log(shedHz / _cabinHelmholtzHz) / 0.25f;
                            float locked = MathF.Exp(-off * off);
                            if (locked > 1e-3f)
                            {
                                // A resonator at the cabin's note, damped more the more windows vent it: each
                                // opening radiates the cabin's energy away.
                                float w0 = 2f * MathF.PI * _cabinHelmholtzHz * dt;
                                float r = MathF.Exp(-w0 / (2f * (6f / MathF.Max(1, _windowCount))));
                                float y = n * (1f - r) + 2f * r * MathF.Cos(w0) * _buffetY1 - r * r * _buffetY2;
                                _buffetY2 = _buffetY1; _buffetY1 = y;
                                float q = 0.5f * 1.2f * speed * speed;
                                inCabin += y * locked * BuffetShare * q * _windowsNow;
                            }
                        }
                    }

                    // What is INSIDE with you, not through the body: the door beeper hangs over the
                    // doorway, and the door engines vent into the step well — half of what the air
                    // system says is in here, the brakes under the floor are the other half.
                    inCabin += chimeOut + 0.5f * airOut;
                    // And with the doors open there is a hole in the side of the bus: the outside comes
                    // in through a doorway about 2.4 m^2 of a hundred-odd m^2 of cabin wall, which lets
                    // in a couple of per cent of the power (-16 dB), unfiltered.
                    if (_doorsOpen) inCabin += (pa - rearExtras + bay) * DoorwayLeak;
                    float k = _interiorMix;
                    pa = pa * (1f - k) + inCabin * k;
                    front *= 1f - k;
            }

            _envelope += Math.Clamp(envTarget - _envelope, -envStep, envStep);
            // Written WITHOUT the soft ceiling, which belongs to whoever sums the taps back up: a
            // limiter applied to each half separately is not the same limiter, and the single-voice
            // case has to come out bit for bit the same as the two taps summed.
            _levelGain += liftStep;
            // The lift is the ENGINE's: an idling bus's air brake release is exactly as loud as it
            // is, and lifting it with the idle made every bus stop audible across the city.
            // Inside, all of the air and the beeper arrive through the cabin (inCabin, in the back
            // tap); outside, each end carries its own.
            float extras = (1f - _interiorMix) * rearExtras
                         + _interiorMix * insideExtras;
            pa = (pa - extras) * _levelGain + extras;
            float outSample = pa * gain * _envelope;
            _ring[(int)(w & mask)] = outSample;
            outSum += (double)outSample * outSample;
            _front[(int)(w & mask)] = (front * _levelGain + (1f - _interiorMix) * frontExtras) * gain * _envelope;
            // Every other path into the cabin, into its own ring, lifted as the engine is (not the door's
            // air and beeper). Cleared for a ring's length after getting out.
            if (cabinRings != null && (split || _cabinDirty > 0))
            {
                float k = _interiorMix, ge = gain * _envelope;
                int door = _cabinLayout!.Door;
                for (int p = 1; p < _pathNow.Length && p - 1 < cabinRings.Length; p++)
                {
                    float v = split ? (_pathNow[p] * k * _levelGain + (p == door ? doorUnlifted * k : 0f)) * ge : 0f;
                    var r = cabinRings[p - 1];
                    r[(int)(w & (r.Length - 1))] = v;
                    outSum += (double)v * v;
                }
                _cabinDirty = split ? 1 << CabinRingBits : _cabinDirty - 1;
            }
            w++;
        }
        Volatile.Write(ref _written, w);
        _levelGain = liftTarget;
        if (count > 0) Volatile.Write(ref _lastOutputDb, 10f * MathF.Log10((float)(outSum / count) + 1e-12f));
        // How much of it came off the road surface itself: the tyres' share of the pressure, for the
        // ground reflection (GroundReflection.NearGroundShare). Smoothed over about half a second.
        if (blockSum > 1e-12)
        {
            float share = MathF.Sqrt((float)Math.Clamp(tyreSum / blockSum, 0.0, 1.0));
            _nearShare += (share - _nearShare) * MathF.Min(1f, count / (0.5f * SampleRate));
            Ground.SetNear(_nearShare);
            _frontGround?.SetNear(_nearShare);
        }
        FlushBands();
        float blockMs = (float)(blockSum / Math.Max(1, count));
        float a = 1f - MathF.Exp(-count / (LevelSeconds * SampleRate));
        // From the first block it hears, not from zero: an average that starts at nothing reads a
        // sounding engine as near silence for its first half second, and the lift chased that.
        if (_levelMs <= 0 && blockMs > 0 && OpenFPS.Common.Hearing.EarModel.Enabled) _levelMs = blockMs;
        else _levelMs += (blockMs - _levelMs) * a;
        if (envTarget <= 0f && _envelope <= 1e-4f) FadedOut = true;
    }
}

/// <summary>
/// The soft ceiling on a synthesized voice: untouched up to the knee, then bent smoothly towards a
/// ceiling it never reaches.
///
/// The bend joins the straight line at the knee with the same value and the same slope. A plain
/// tanh(y) past the knee is not continuous there (0.8 just under, tanh(0.8) = 0.664 just over), and
/// every backfire and overrun pop crossing it would put a one-sample step into the output: a click,
/// twice per pop.
///
/// The ceiling sits two decibels above full scale. A unit sample is still the declared level plus
/// <see cref="OpenFPS.Common.VehicleProfile.PeakHeadroomDb"/>, so nothing is placed any louder; the
/// extra room is for the peakiest pulse in the fleet — a 450 single's blowdown, 1.6 dB over full
/// scale at its 99.9th percentile — to be rounded rather than squared. The mix runs in floating
/// point and ends in the master limiter, so a sample a little over 1.0 is safe.
/// </summary>
public static class SoftCeiling
{
    public const float Knee = 0.8f;
    /// <summary>The ceiling above full scale, dB.</summary>
    public const float CeilingDb = 2f;
    public static readonly float Ceiling = MathF.Pow(10f, CeilingDb / 20f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Apply(float y)
    {
        float a = MathF.Abs(y);
        if (a <= Knee) return y;
        float room = Ceiling - Knee;
        float bent = Knee + room * MathF.Tanh((a - Knee) / room);
        return y < 0f ? -bent : bent;
    }
}

/// <summary>
/// The smear a rough surface puts on what it hands back: a short cascade of Schroeder all-passes,
/// flat in level and wandering in phase, spread over a time that grows with the roughness. See
/// <see cref="EngineEchoState.Scattering"/> for why an echo needs one.
/// </summary>
public sealed class EchoDiffuser
{
    private readonly float[][] _lines;
    private readonly int[] _at;
    private readonly float _g;

    public EchoDiffuser(float scattering, int seed, float sampleRate)
    {
        float s = Math.Clamp(scattering, 0f, 1f);
        var rng = new Random(seed * 7919 + 17);
        // Mutually prime-ish bases, ms, stretched by the roughness: about a millisecond in all for
        // polished steel, twenty or so for brick. Four stages leave no regular structure in the
        // phase; more would start to sound like a room, which is the reverb's job and not this one.
        //
        // The delays are the whole of the smear's LENGTH, and they are also time the echo arrives
        // late by, so a mirror must get almost none: under two milliseconds for polished metal and
        // glass (s ~ 0.05), a dozen for brick, twenty for a crowd.
        float[] baseMs = { 0.11f, 0.17f, 0.23f, 0.31f };
        _lines = new float[baseMs.Length][];
        _at = new int[baseMs.Length];
        for (int k = 0; k < baseMs.Length; k++)
        {
            float ms = baseMs[k] * (1f + 24f * s) * (0.8f + 0.4f * (float)rng.NextDouble());
            _lines[k] = new float[Math.Max(1, (int)(ms * 0.001f * sampleRate))];
        }
        _g = 0.45f + 0.2f * s;
    }

    public float Process(float x)
    {
        for (int k = 0; k < _lines.Length; k++)
        {
            var line = _lines[k];
            int at = _at[k];
            float delayed = line[at];
            float y = -_g * x + delayed;
            line[at] = x + _g * y;
            _at[k] = at + 1 >= line.Length ? 0 : at + 1;
            x = y;
        }
        return x;
    }
}

/// <summary>
/// One reflection of a live engine: the same signal, read back from the engine's ring buffer at
/// the delay the mirrored path implies, and placed at the mirrored source. The delay is slewed rather
/// than stepped, so as the car moves and the path length changes the echo glides in pitch — which is
/// the Doppler of the reflection, and the reason its emitter carries no velocity of its own.
/// </summary>
public sealed class EngineEchoState : IGuardedUnit
{
    /// <summary>The non-finite guard's flag and name for this unit (NonFinite).</summary>
    public NonFiniteUnit Guard { get; } = new();

    /// <summary>The mixer callback's mono buffer, made with the voice so the callback never allocates.</summary>
    internal readonly float[] MixScratch = new float[DspCallback.MaxBlock];

    public readonly EngineVoiceState Source;
    public volatile float TargetDelaySeconds;
    public volatile float TargetGain;
    private double _delay = -1;
    private float _gain;
    public float SampleRate = MixerQuality.MixerRate;
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
    /// Two blocks of slack absorbs the flip either way. It is counted in blocks, from the block the
    /// mixer hands us, because a fixed time cannot track the buffer size: 26 ms is two blocks at 512
    /// samples but only 1.1 at the 1024 FMOD actually chooses.
    /// </summary>
    public const int MinDelayBlocks = 2;

    /// <summary>The floor as a time, for callers that have no block to measure. Only a fallback.</summary>
    public const float MinDelaySeconds = 0.026f;

    private int _blockSlack;

    /// <summary>
    /// This voice keeps its OWN read cursor instead of following the source's play position.
    ///
    /// Set for a BORROWED voice — a distant car too far away to be worth its own engine, which is
    /// voiced by reading a near car's ring. False for a REFLECTION, which must follow: an echo of a
    /// car is that car's sound arriving late, so the source's own Doppler belongs in it and the delay
    /// slewing adds the rest of the mirrored path's on top. (That is why a reflection's channel pitch
    /// is pinned to 1 and a borrowed voice's is not.)
    ///
    /// A borrowed voice is a DIFFERENT car. It is placed at its own position, moves at its own
    /// velocity and is pitched by its own Doppler — so inheriting the source car's Doppler through the
    /// read rate and then applying its own gave it two of them, belonging to two cars going different
    /// ways. Heard as a car that is technically at the redline and sounds like it is cruising, and it
    /// gets worse the more of the field is borrowing.
    /// </summary>
    public bool OwnCursor;

    /// <summary>Where this voice has read up to, absolute, when it keeps its own cursor.</summary>
    private double _cursor = -1;

    /// <summary>
    /// Hardest this voice will pull its cursor back toward where it should sit, as a fraction of the
    /// sample rate. A rate error IS a pitch error, so the correction has to be inaudible: a hundredth
    /// is about a sixth of a semitone, applied only while the cursor is out of place, on a voice that
    /// by definition is too far away to tell apart from the car beside it. The thing it is correcting
    /// is the slow drift between our rate and the source's, which is the integral of the source car's
    /// Doppler and averages out over a lap.
    /// </summary>
    public const double MaxRateCorrection = 0.01;

    public EngineEchoState(EngineVoiceState source) { Source = source; SampleRate = source.SampleRate; }

    /// <summary>
    /// How rough the surface was, 0..1, or below zero for a voice that is not a reflection at all
    /// (a borrowed voice is a different car, not an echo, and is left exactly as it was). Set once,
    /// before the first block.
    ///
    /// WHY AN ECHO IS SMEARED. A reflection read straight out of the source's ring is the source's
    /// own waveform, sample for sample, a few milliseconds late — and a signal added to a delayed copy
    /// of itself is a comb filter: evenly spaced notches that sweep as either end moves. That phasing
    /// makes a source sound inside out or like a narrow beam, and it is not what a wall does. A
    /// real wall hands the sound back from a patch a few metres across (the Fresnel zone), every part
    /// of it a slightly different distance away, and what faces the wall is not what faces you — the
    /// tailpipe points one way and the intake another. So the copy that comes back is the same sound
    /// but not the same waveform, and the notches, if any, fall at no regular spacing.
    ///
    /// Modelled as a short cascade of Schroeder all-passes: flat in level, so the echo is exactly as
    /// loud as the image-source method says, but with a phase that wanders with frequency, spread over
    /// a time that grows with the roughness — about a millisecond for polished steel or glass, up to
    /// twenty or so for a brick facade or a crowd. The delays are different for every voice so no two
    /// walls smear alike. The arrival time, and so the direction and the slapback, are untouched.
    /// </summary>
    public float Scattering = -1f;
    /// <summary>Seeds the smear's delays, so each wall's is its own.</summary>
    public int Seed;

    private EchoDiffuser? _diffuser;

    /// <summary>
    /// How fast a reflection's level follows its target, per sample. A reflection comes and goes as
    /// the geometry does — gradually, as the patch of wall that is lit slides off the end of it — so
    /// it swells and dies over a few hundred milliseconds rather than switching. A borrowed voice
    /// keeps the old, fast rate: it is a car, and a car's level is the car's business.
    /// </summary>
    private float GainSlew => Scattering >= 0f ? 1f / (0.18f * SampleRate) : _k.Gain;

    /// <summary>The per-sample glides, chosen at 44.1 kHz, at this voice's rate (At44k): the gain's
    /// (0.0015, 15 ms), the delay's (0.002, 11 ms) and the own cursor's pull (1e-5).</summary>
    private (float Rate, float Gain, double Delay, double Pull) _k = (44100f, 0.0015f, 0.002, 1e-5);
    private void Glides()
    {
        if (_k.Rate == SampleRate) return;
        _k = (SampleRate, OpenFPS.Client.AudioEngine.Core.At44k.Step(0.0015f, SampleRate),
              OpenFPS.Client.AudioEngine.Core.At44k.Step(0.002, SampleRate), OpenFPS.Client.AudioEngine.Core.At44k.Step(1e-5, SampleRate));
    }

    public void Render(Span<float> mono)
    {
        // The largest block we have ever been handed. Taking the maximum rather than the current
        // length means a short block (FMOD hands out partial ones) cannot shrink the margin.
        int slack = MinDelayBlocks * mono.Length;
        if (slack > _blockSlack) _blockSlack = slack;
        Glides();

        double floorSamples = Math.Max(MinDelaySeconds * SampleRate, _blockSlack);
        double target = Math.Max(floorSamples, TargetDelaySeconds * SampleRate);
        if (_delay < 0) _delay = target;
        float gTarget = TargetGain;

        if (OwnCursor) { RenderOwnCursor(mono, floorSamples, target, gTarget); return; }
        if (Scattering >= 0f && _diffuser == null) _diffuser = new EchoDiffuser(Scattering, Seed, SampleRate);
        float slew = GainSlew;
        // The source's play position as a continuous clock, not Played itself: Played moves in whole
        // blocks when the source's channel is pitched (EngineVoiceState.ConsumeRate), and an echo read
        // a fixed distance behind it skipped or repeated 23 ms of the car at every extra call.
        double rate = _clock.Begin(Source.Played, Source.ConsumeRate, mono.Length, SampleRate);
        // Slew: up to 12% per sample of drift, which covers the Doppler of a fast pass.
        for (int i = 0; i < mono.Length; i++)
        {
            double diff = target - _delay;
            _delay += Math.Clamp(diff * _k.Delay, -0.12, 0.12);
            _gain += (gTarget - _gain) * slew;
            // Reading back from the source's play position: the source rendered its block before or
            // after this one; the minimum delay covers either order.
            // Clamped to the slack as well as the target, so a delay that is being slewed downward
            // can never cross into the block the source may not have written yet.
            double back = Math.Max(_delay, floorSamples) + mono.Length;
            float y = Source.ReadAt(_clock.Position - back) * _gain;
            _clock.Position += rate;
            mono[i] = _diffuser != null ? _diffuser.Process(y) : y;
        }
    }

    private SourceClock _clock = SourceClock.Unset;

    /// <summary>
    /// The same audio, read at the rate it was synthesized.
    ///
    /// The cursor advances one sample per sample and is nudged — never jumped — back toward its
    /// place behind the source's play position, so nothing about how fast the SOURCE is being
    /// consumed reaches this voice's pitch. It resyncs outright only when it has fallen off the ring
    /// entirely, which means the source stopped or restarted and there is no continuity left to keep.
    /// </summary>
    private void RenderOwnCursor(Span<float> mono, double floorSamples, double target, float gTarget)
    {
        long played = Source.Played;
        // Where the source is, continuously (SourceClock): Played moves in whole blocks when the
        // source's channel is pitched, and a cursor aimed at Played itself was pulled at that saw and
        // clamped against it, a jump at the end of a block whenever the source missed a call. A block
        // further back than the floor, too, so the saw's dips do not reach the ceiling below.
        double rate = _clock.Begin(played, Source.ConsumeRate, mono.Length, SampleRate);
        double where = _clock.Position;
        double back = target + mono.Length;
        // Off the ring, or ahead of what the source has played at all: there is nothing to be
        // continuous with, so start again where we should be.
        if (_cursor < 0 || _cursor > played || played - _cursor > Source.RingLength - 4 * mono.Length)
            _cursor = where - back;

        for (int i = 0; i < mono.Length; i++)
        {
            _gain += (gTarget - _gain) * _k.Gain;
            mono[i] = Source.ReadAt(_cursor) * _gain;
            // One sample per sample, plus an inaudible pull back toward where the cursor belongs.
            double drift = (where - back) - _cursor;
            _cursor += 1.0 + Math.Clamp(drift * _k.Pull, -MaxRateCorrection, MaxRateCorrection);
            where += rate;
        }
        _clock.Position = where;
        // Never let the cursor reach what the source has not played yet.
        double ceiling = played - floorSamples;
        if (_cursor > ceiling) _cursor = ceiling;
    }
}

/// <summary>
/// The front outlet of a machine, as a voice of its own.
///
/// A car is a RIG, not a sound: the exhaust is a couple of metres behind the intake, and at close
/// range that separation is most of how a listener knows which way it is pointing. Heard through one
/// voice the separation is simply lost — the timbre survives, the geometry does not — and the single
/// voice sits between the two ends, biased toward the tailpipe because that is where most of the
/// sound is (VehicleProfile.ExhaustEmitterBias).
///
/// This is the other end. It is not a second engine and not a copy: the same integration writes both
/// taps (see <see cref="EngineVoiceState"/>), and this reads the front one. A car close enough for
/// the two to be told apart gets both; everything else gets the one voice, summed, at exactly the
/// level it always had.
///
/// The cursor rules are a borrowed voice's, for a borrowed voice's reason: it must advance at the
/// rate the audio was SYNTHESIZED, not at the rate some other channel is being consumed, or the
/// exhaust's Doppler arrives in the intake on top of the intake's own. It is nudged, never jumped,
/// back into step — a rate correction is a pitch error, and a jump is a click.
/// </summary>
public sealed class EngineTapState : IGuardedUnit
{
    /// <summary>The non-finite guard's flag and name for this unit (NonFinite).</summary>
    public NonFiniteUnit Guard { get; } = new();

    /// <summary>The mixer callback's mono buffer, made with the voice so the callback never allocates.</summary>
    internal readonly float[] MixScratch = new float[DspCallback.MaxBlock];

    public readonly EngineVoiceState Source;

    /// <summary>Where this voice is heading, 0 or 1. Zero retires it; see <see cref="FadedOut"/>.</summary>
    public volatile float TargetGain = 1f;

    /// <summary>True once a fade-out has finished and the voice can be released.</summary>
    public volatile bool FadedOut;

    private float _gain;

    public EngineTapState(EngineVoiceState source) { Source = source; Ground = new(source.SampleRate); source._frontGround = Ground; }

    /// <summary>A path into the cabin of the vehicle the listener is sitting in (CabinPaths), read from
    /// its own ring: the same cursor, fade and hand-over as the front outlet. No ground: from inside
    /// there is no road to hear.</summary>
    public EngineTapState(EngineVoiceState source, int cabinPath)
    {
        Source = source;
        Ground = new(source.SampleRate);
        CabinPath = cabinPath;
    }

    /// <summary>The cabin path this tap plays (1 and up), or -1 for the machine's front outlet.</summary>
    public readonly int CabinPath = -1;

    /// <summary>Hands what this tap plays back to the voice it came from (slewed there): the front
    /// outlet, or its cabin path.</summary>
    public void HandBack()
    {
        if (CabinPath > 0) Source.SetCabinTapLive(CabinPath, false);
        else Source.SplitVoices = false;
    }

    /// <summary>The ground between the front of the machine and the listener.</summary>
    public readonly OpenFPS.Client.AudioEngine.Acoustics.GroundReflection Ground;

    public void Render(Span<float> mono)
    {
        // Nothing until the engine has something to give. A tap that synthesized on demand would be
        // doing it on the mixer thread, which is the one thing the whole producer design exists to
        // prevent.
        if (!Source.Primed) { mono.Clear(); return; }

        // In step with the voice we are the other half of, on a continuous clock: the source's own
        // rate over ours, leaning slowly on where the source has got to. It used to step up to ten
        // samples once a block toward Played, and Played moves in whole blocks when either channel is
        // pitched (EngineVoiceState.ConsumeRate), so a car going past had its front voice jump at
        // nearly every block: 4-8 discontinuities a second, measured (--quality echo, "front").
        float own = ChannelRate;
        double rate = _clock.Begin(Source.Played, Source.ConsumeRate / (own > 0.05f ? own : 1f), mono.Length, Source.SampleRate);

        float gTarget = TargetGain;
        float step = 1f / (0.06f * MathF.Max(1f, Source.SampleRate));
        for (int i = 0; i < mono.Length; i++)
        {
            _gain += Math.Clamp(gTarget - _gain, -step, step);
            float x = CabinPath > 0 ? Source.ReadCabinAt(CabinPath, _clock.Position) : Ground.Process(Source.ReadFrontAt(_clock.Position));
            mono[i] = x * _gain;
            _clock.Position += rate;
        }

        if (gTarget <= 0f && _gain <= 1e-4f) FadedOut = true;
    }

    /// <summary>This voice's own channel pitch (its Doppler), set by the provider with the pitch.</summary>
    public volatile float ChannelRate = 1f;

    private SourceClock _clock = SourceClock.Unset;
}

/// <summary>
/// Where a live voice's play position IS, continuously, for a reader that has to stay in step with it.
///
/// The voice's <see cref="EngineVoiceState.Played"/> is only right on average: a pitched DSP channel is
/// taken a whole block at a time, more or fewer times per mixer block (EngineVoiceState.ConsumeRate).
/// This clock runs at the rate the reader is told, and leans on Played through an error averaged over
/// half a second, applied as a rate of at most half a per cent (under a tenth of a semitone) — so the
/// block-sized saw in Played never reaches the read, and a real drift is taken out within seconds.
/// It restarts outright only when it has lost the source by more than a few blocks: the source
/// stopped, restarted, or this reader was not called for a while.
/// </summary>
public struct SourceClock
{
    /// <summary>The source position this reader is at, samples.</summary>
    public double Position;
    private double _error;

    public static SourceClock Unset => new() { Position = double.NaN };

    /// <summary>Called once per block before reading; returns the per-sample advance for this block.</summary>
    public double Begin(long played, float rate, int block, float sampleRate)
    {
        if (double.IsNaN(Position) || Math.Abs(played - Position) > 6.0 * Math.Max(256, block))
        {
            Position = played;
            _error = 0;
        }
        double err = played - Position;
        _error += (err - _error) * Math.Min(1.0, block / (0.5 * sampleRate));
        double lean = Math.Clamp(_error / (2.0 * sampleRate), -0.005, 0.005);
        float r = float.IsFinite(rate) && rate > 0.05f && rate < 20f ? rate : 1f;
        return r + lean;
    }
}

/// <summary>An FMOD DSP that is one outlet of a machine. Modelled on <see cref="EchoProcessor"/>.</summary>
public static class TapProcessor
{
    /// <summary>The non-finite guard's flag for a state that is not an IGuardedUnit.</summary>
    private static int _nonFiniteOther;

    private static readonly DSP_READ_CALLBACK _readCallback = ReadCallback;

    public static RESULT CreateDSP(FMOD.System system, EngineTapState state, out FMOD.DSP dsp, out GCHandle handle)
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
    /// NOTHING MAY ESCAPE A DSP CALLBACK.
    ///
    /// This runs on FMOD's mixer thread, called from native code. An exception that reaches the
    /// native frame is not a caught fault, it is a CLR FATAL ERROR: the runtime aborts the process
    /// on the spot, with no managed stack, no log line, and a core that reads
    /// "libfmod -> libcoreclr -> abort".
    ///
    /// So the WHOLE body is inside the guard, not just `state.Render(mono)`. The line most likely to
    /// throw is `GCHandle.FromIntPtr(userData).Target`, which raises InvalidOperationException the
    /// moment the handle it names is no longer allocated. GranularProcessor and SynthProcessor guard
    /// the same way, with the same one-shot log.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        try
        {
            var r = ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
            NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "engine tap", ref _nonFiniteOther);
            return r;
        }
        catch (Exception ex)
        {
            // Once. A DSP that faults faults every block, and a log line per block at 43 blocks a
            // second buries everything else in the file.
            DspFault.Record("TapProcessor", ex);
            unsafe
            {
                if (outchannels == 0) outchannels = 1;
                float* outBuf = (float*)outbuffer;
                for (int i = 0; i < (int)length * outchannels; i++) outBuf[i] = 0f;
            }
            return RESULT.OK;
        }
    }


    private static RESULT ReadCallbackCore(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        if (userData == IntPtr.Zero) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var state = (EngineTapState?)GCHandle.FromIntPtr(userData).Target;
        if (state == null) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        if (outchannels == 0) outchannels = 1;
        int ch = outchannels, n = (int)length;
        if (n > state.MixScratch.Length) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var mono = state.MixScratch.AsSpan(0, n);
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

public static class EchoProcessor
{
    /// <summary>The non-finite guard's flag for a state that is not an IGuardedUnit.</summary>
    private static int _nonFiniteOther;

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

    /// <summary>
    /// NOTHING MAY ESCAPE A DSP CALLBACK.
    ///
    /// This runs on FMOD's mixer thread, called from native code. An exception that reaches the
    /// native frame is not a caught fault, it is a CLR FATAL ERROR: the runtime aborts the process
    /// on the spot, with no managed stack, no log line, and a core that reads
    /// "libfmod -> libcoreclr -> abort".
    ///
    /// So the WHOLE body is inside the guard, not just `state.Render(mono)`. The line most likely to
    /// throw is `GCHandle.FromIntPtr(userData).Target`, which raises InvalidOperationException the
    /// moment the handle it names is no longer allocated. GranularProcessor and SynthProcessor guard
    /// the same way, with the same one-shot log.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        try
        {
            var r = ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
            NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "engine echo", ref _nonFiniteOther);
            return r;
        }
        catch (Exception ex)
        {
            // Once. A DSP that faults faults every block, and a log line per block at 43 blocks a
            // second buries everything else in the file.
            DspFault.Record("EchoProcessor", ex);
            unsafe
            {
                if (outchannels == 0) outchannels = 1;
                float* outBuf = (float*)outbuffer;
                for (int i = 0; i < (int)length * outchannels; i++) outBuf[i] = 0f;
            }
            return RESULT.OK;
        }
    }


    private static RESULT ReadCallbackCore(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        if (userData == IntPtr.Zero) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var state = (EngineEchoState?)GCHandle.FromIntPtr(userData).Target;
        if (state == null) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        if (outchannels == 0) outchannels = 1;
        int ch = outchannels, n = (int)length;
        if (n > state.MixScratch.Length) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var mono = state.MixScratch.AsSpan(0, n);
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
    /// <summary>The non-finite guard's flag for a state that is not an IGuardedUnit.</summary>
    private static int _nonFiniteOther;

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

    /// <summary>
    /// NOTHING MAY ESCAPE A DSP CALLBACK.
    ///
    /// This runs on FMOD's mixer thread, called from native code. An exception that reaches the
    /// native frame is not a caught fault, it is a CLR FATAL ERROR: the runtime aborts the process
    /// on the spot, with no managed stack, no log line, and a core that reads
    /// "libfmod -> libcoreclr -> abort".
    ///
    /// So the WHOLE body is inside the guard, not just `state.Render(mono)`. The line most likely to
    /// throw is `GCHandle.FromIntPtr(userData).Target`, which raises InvalidOperationException the
    /// moment the handle it names is no longer allocated. GranularProcessor and SynthProcessor guard
    /// the same way, with the same one-shot log.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        try
        {
            var r = ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
            NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "engine voice", ref _nonFiniteOther);
            return r;
        }
        catch (Exception ex)
        {
            // Once. A DSP that faults faults every block, and a log line per block at 43 blocks a
            // second buries everything else in the file.
            DspFault.Record("EngineProcessor", ex);
            unsafe
            {
                if (outchannels == 0) outchannels = 1;
                float* outBuf = (float*)outbuffer;
                for (int i = 0; i < (int)length * outchannels; i++) outBuf[i] = 0f;
            }
            return RESULT.OK;
        }
    }


    private static RESULT ReadCallbackCore(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        IntPtr userData = DspCallback.UserData(ref dsp_state);
        if (userData == IntPtr.Zero) { DspCallback.Silence(outbuffer, length, outchannels); return RESULT.OK; }
        var state = (EngineVoiceState?)GCHandle.FromIntPtr(userData).Target;
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
