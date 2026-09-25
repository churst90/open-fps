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
public sealed class EngineVoiceState : IRenderedVoice
{
    public readonly VehicleProfile Vehicle;
    public readonly EngineSynth Engine;
    public readonly Driveline Driveline;
    public readonly VirtualDriver Driver;
    private readonly Random _rng;
    private VehicleSynth.TyreVoice _tyre;
    private VehicleSynth.TyreVoice _tyreFront;
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
    /// <summary>Standing at a stop that takes passengers: set the spring brakes, kneel, open the
    /// doors. Anywhere else a stopped vehicle just holds its service brake.</summary>
    public volatile bool ServingStop;
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
    /// <summary>
    /// How much of the front of the car reaches this voice, 0..1 — and it is ONE now, because
    /// there is nothing left for it to represent.
    ///
    /// It was 0.35: a fixed nine decibels taken off the intake on top of everything the intake
    /// model already did. That was the third of these undeclared duplicates to turn up. The block
    /// had one (a 0.4 in this same sum, alongside its declared bay leakage) and the intake had
    /// this. In both cases a real, declared, per-vehicle path existed and a constant was quietly
    /// attenuating on top of it.
    ///
    /// What an intake's route to the street is made of is now all declared and all derived:
    /// IntakeSpec.AirboxLossDb (the box as an expansion chamber, from its own geometry) and
    /// IntakeSpec.Level (what escapes the bay). Keeping a 0.35 in front of those would be saying
    /// the same thing a third time, in a number nobody could look up.
    ///
    /// Still a field rather than a constant because a two-outlet vehicle hands its front to a
    /// SECOND voice when the listener is close enough to tell the ends apart, and that crossfade
    /// runs through here.
    /// </summary>
    public float FrontMix = 1f;
    /// <summary>
    /// How much of the tyre layer reaches the mix.
    ///
    /// The tyre model already works in physical levels — a squeal is scaled from the tyre's own
    /// SquealDb, which for a road tyre is 92 dB against a diesel truck's 104 — so halving it here was
    /// scaling a derived quantity by a taste constant, and it put the squeal sixteen decibels under
    /// the engine instead of twelve. The first person to listen to a truck launching hard heard the
    /// revs and no tyres at all.
    ///
    /// Measured rather than guessed, in the end: rendering the tyre voice on its own put a full
    /// squeal at an RMS of 0.89 where the exhaust runs in pascals and reaches tens, leaving the
    /// squeal twenty-odd decibels under an engine it should be about seven under. Two listening
    /// tests in a row said "hardly noticeable" and "extremely dull", and the dullness was the same
    /// thing: what was audible of it was the low shoulder, because the rest was buried.
    /// </summary>
    public float TyreMix = 1.4f;
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

    /// <summary>The back of the machine: the exhaust, and the body it shakes.</summary>
    private readonly float[] _ring = new float[1 << RingBits];

    /// <summary>
    /// The front of the machine: what it breathes through, and the block behind that.
    ///
    /// A second ring rather than a second engine. The engine is integrated ONCE and its two outlets
    /// are written separately, so a car that earns two voices costs one more copy-out and no more
    /// synthesis — which is the only reason a two-voice car is affordable at all.
    ///
    /// The two taps sum to exactly what the single voice used to be (see <see cref="Consume"/>), so
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
        // The front of the car belongs in this voice only while nothing else is carrying it. The
        // share slews rather than switches: sixty milliseconds, the same as the envelope, so handing
        // the intake over to its own voice is a crossfade and not a step.
        float shareTarget = SplitVoices ? 0f : 1f;
        float shareStep = 1f / (0.06f * SampleRate);
        for (int i = 0; i < take; i++)
        {
            int j = (int)((at + i) & mask);
            _frontShare += Math.Clamp(shareTarget - _frontShare, -shareStep, shareStep);
            mono[i] = Soft(_ring[j] + _front[j] * _frontShare);
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
        for (int i = 0; i < mono.Length; i++)
        {
            int j = (int)((at + i) & mask);
            _frontShare += Math.Clamp(shareTarget - _frontShare, -shareStep, shareStep);
            mono[i] = Soft(_ring[j] + _front[j] * _frontShare);
        }
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
        _windPaAt110 = 20e-6f * MathF.Pow(10f, shell.WindNoiseDbAt110 / 20f);
        if (!string.IsNullOrEmpty(v.AirSystem))
        {
            try
            {
                _air = new AirSystem(ModelLibrary.Air(v.AirSystem), sampleRate, seed + 17);
                // Each valve is where the spec says it is, measured back from the nose; it is heard
                // from whichever outlet of this vehicle is nearer. The compressor is on the engine,
                // and the engine is where the intake is.
                float nose = v.LengthMetres * 0.5f;
                _air.PlaceAtFront(p => NearerFront(v, nose - p.AlongMetres), compressorAtFront: true);
                _chimeAtFront = _air.Ports.TryGetValue("door", out var door)
                    ? NearerFront(v, nose - door.Spec.AlongMetres) : true;
            }
            catch (Exception ex) { Serilog.Log.Warning("Vehicle '{Name}': air system '{Air}' — {Err}", v.Name, v.AirSystem, ex.Message); }
        }
        _bayLeak = Math.Clamp(v.EngineBayLeakage, 0f, 1f);
        _bayIntake = new EchoDiffuser(BayScattering, seed + 71, sampleRate);
        // Drums on anything heavy enough to need them; discs on the rest.
        _squeal = new OpenFPS.Client.AudioEngine.Core.BrakeSqueal(sampleRate, drums: v.MassKg > 5000f, seed: seed + 97);
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
        }
    }

    /// <summary>The cooling fan's contribution, 0..1 — normally one. Writable so an instrument can
    /// mute it and read what it is worth, the same way the bay leak can be.</summary>
    public float FanMix = 1f;

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
        set => Volatile.Write(ref _interior, value ? 1 : 0);
    }
    private int _interior;

    private readonly BodyResonator _cabin;
    private readonly float _panelCorner, _sealLeak, _windPaAt110;
    private float _panelLp, _windLp, _windHp, _windHpIn, _interiorMix;

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
    private double _levelMs;
    private float _levelGain = 1f;
    private const float LevelSeconds = 0.5f, MaxLiftDb = 20f;

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
    private readonly float _fanRatio;
    private float _bayLeak;
    /// <summary>
    /// The intake as it leaves through the bay: the same noise as at the grille, but not the same
    /// waveform. What reaches the bay openings is the airbox and ducting heard off the block, the
    /// bulkheads and the underside of the bonnet — scattered, a few milliseconds of paths, so it is
    /// not the grille's waveform a second time.
    /// </summary>
    private readonly EchoDiffuser _bayIntake;
    /// <summary>The front brakes singing at the end of a stop, on a vehicle whose brakes do. See
    /// BrakeSqueal.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.BrakeSqueal _squeal;
    private float _squealDecel, _squealLastSpeed;
    /// <summary>Whether this vehicle's brakes squeal, and at what. For tests and instruments.</summary>
    internal OpenFPS.Client.AudioEngine.Core.BrakeSqueal Squeal => _squeal;
    /// <summary>The door beeper hangs over the door, so it is heard from the end the door is at.</summary>
    private readonly bool _chimeAtFront = true;

    /// <summary>Is a point this far along the vehicle (its own frame, +Z forward) nearer the front
    /// outlet than the back one?</summary>
    internal static bool NearerFront(VehicleProfile v, float z)
        => MathF.Abs(z - v.IntakeOffsetZ) < MathF.Abs(z - v.ExhaustOffsetZ);
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
        bool inside = Interior;
        // Inside, the listener is AT the machine, and the outside-listener geometry (which tailpipe
        // is nearer, which way the fan blows) has nothing to say about a sound that comes through
        // the floor.
        if (_listenerKnown && !inside)
            Engine.SetListener(new Vector3(Volatile.Read(ref _listenerX), Volatile.Read(ref _listenerY), Volatile.Read(ref _listenerZ)));
        float panelA = 1f - MathF.Exp(-2f * MathF.PI * _panelCorner * dt);
        // The lift for this block, from the level the machine has been running at lately.
        float liftTarget = 1f;
        if (CompensateLevel && _levelMs > 0)
        {
            float nowDb = 10f * MathF.Log10((float)_levelMs / (20e-6f * 20e-6f) + 1e-12f);
            float deficit = Math.Clamp(Vehicle.SourceLevelDb - nowDb, 0f, MaxLiftDb / (1f - Loudness.DynamicRangeCompression));
            liftTarget = MathF.Pow(10f, deficit * (1f - Loudness.DynamicRangeCompression) / 20f);
        }
        float liftStep = MathF.Max(1e-4f, (liftTarget - _levelGain) / MathF.Max(1, count));
        double blockSum = 0;
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
            _tyreChirp *= 0.99985f;
            // Two axles, two tyre noises. They are different tyres on different patches of road, so
            // their noise is INDEPENDENT; one signal written to both ends was the same roar coming
            // from two places a few metres apart, which combs against itself as the car goes by —
            // heard as a car passing "inside out".
            float tyreRear = VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, RoadSlip + _tyreChirp, _rng, ref _tyre);
            float tyreFront = VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, RoadSlip + _tyreChirp, _rng, ref _tyreFront);
            // Tyres are in arbitrary units; place them about 30 dB under a loud exhaust. One axle at
            // each end, at a level that keeps the POWER of the pair what the single coherent signal
            // had (0.6 at each end, summed in phase: 1.2, so 0.6 x root 2 each).
            const float PerAxle = 0.6f * 1.41421356f;
            float rearTyre = tyreRear * PerAxle * TyreMix;
            float frontTyre = tyreFront * PerAxle * TyreMix;
            // The front of the machine: the airbox, which breathes to the outside through the
            // grille, and the tyres at that end.
            //
            // The BLOCK used to be in here as well, at 0.4 — and then again in the bay leak below.
            // Two routes out of the engine for one mechanism, one of them declared per vehicle from
            // the geometry and one of them a constant applied to everything. That second path is
            // what the block actually had: 0.4 x FrontMix = 0.14, a fixed 17 dB of attenuation with
            // nothing behind it. On a car it does not matter, because a car is its exhaust. On a
            // bus, whose block measures SEVEN DECIBELS ABOVE its silenced tailpipe (`--voice-levels
            // parts`), it is most of the machine being thrown away, and that is what "I can hardly
            // hear the engines on those diesels" was.
            //
            // One mechanism, one route: the block gets outside through the bay, and how much of it
            // does is VehicleProfile.EngineBayLeakage, which every vehicle now declares from what is
            // actually around its engine.
            float front = Engine.Intake * FrontMix + frontTyre;
            if (_fan != null)
            {
                // The fan is geared to the crank and has no throttle: it turns at engine speed and
                // its loading is the air it is pushing, which is all it ever pushes. Its speed is
                // set on the slow tick like everything else that does not change per sample.
                if ((i & 63) == 0)
                    _fan.SetSpeed(Engine.Rpm * _fanRatio, 1f,
                                  _listenerKnown
                                      ? new Vector3(Volatile.Read(ref _listenerX), Volatile.Read(ref _listenerY), Volatile.Read(ref _listenerZ))
                                      : Vector3.UnitZ);
                front += _fan.Step() * FanMix;
            }
            float pa = Engine.Exhaust + rearTyre;

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
            // What escapes the engine bay (VehicleProfile.EngineBayLeakage; 0.15 unless declared).
            // It leaves from the BAY, which is where the engine is — the intake slot marks it, nose
            // or mid-ship — so it goes out of the front tap. It used to go out of the back with the
            // tailpipe: on the school bus the block is 97.7 dB against an 83 dB silenced pipe, so an
            // idling bus at a stop, fan slowed, was one sound at its tail — "the front of the bus and
            // the exhaust are in the same place". Once far enough to be one voice, nothing changes.
            float bay = _bayLeak > 0f ? (Engine.Block + 0.5f * _bayIntake.Process(Engine.Intake)) * _bayLeak : 0f;
            front += bay;
            // The air and the door beeper are their own sources at their own levels, each at its own
            // end of the vehicle: the door valve, the kneeling valve and the beeper at the front door,
            // the brake releases at the axles. They all used to come out of the tailpipe.
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
            float squealOut = _squeal.Step(_speedSmooth, _squealDecel);
            float frontExtras = airFront + chimeFront + squealOut;
            float rearExtras = airOut + chimeOut - (airFront + chimeFront);

            // What the ENGINE is radiating, before anything a listener's position does to it: the
            // level the loudness law is applied to. Not the brakes' air or the door beeper, which are
            // their own sources at their own levels and are not what idles.
            // The bay is the engine radiating even though it now leaves by the front: the level the
            // loudness law is applied to is the same machine it always was.
            float engineOnly = pa - rearExtras + bay;
            blockSum += (double)engineOnly * engineOnly;

            // Crossfaded over ~60 ms rather than switched, so getting in or out is not a click.
            _interiorMix += Math.Clamp((inside ? 1f : 0f) - _interiorMix, -envStep, envStep);
            if (_interiorMix > 0f)
            {
                // What arrives at the outside of the cabin: the engine bay just ahead of the
                // firewall, the exhaust along the floor to a tailpipe a couple of metres back, and
                // all four tyres under the floor.
                // All four tyres, at the power the single signal had.
                float atPanels = Engine.Block + Engine.Intake + 0.5f * Engine.Exhaust + (tyreRear + tyreFront) * 0.6f * 0.70710678f * TyreMix;
                _panelLp += (atPanels - _panelLp) * panelA;
                float inCabin = _panelLp + _sealLeak * atPanels;
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
            // Written WITHOUT the soft ceiling, which now belongs to whoever sums the taps back up:
            // a limiter applied to each half separately is not the same limiter, and the single-voice
            // case has to come out bit for bit as it did before the machine had two outlets.
            _levelGain += liftStep;
            // The lift is the ENGINE's: an idling bus's air brake release is exactly as loud as it
            // is, and lifting it with the idle made every bus stop audible across the city.
            // Inside, all of the air and the beeper arrive through the cabin (inCabin, in the back
            // tap); outside, each end carries its own.
            float extras = (1f - _interiorMix) * rearExtras
                         + _interiorMix * (chimeOut + 0.5f * airOut);
            pa = (pa - extras) * _levelGain + extras;
            _ring[(int)(w & mask)] = pa * gain * _envelope;
            _front[(int)(w & mask)] = (front * _levelGain + (1f - _interiorMix) * frontExtras) * gain * _envelope;
            w++;
        }
        Volatile.Write(ref _written, w);
        _levelGain = liftTarget;
        float blockMs = (float)(blockSum / Math.Max(1, count));
        float a = 1f - MathF.Exp(-count / (LevelSeconds * SampleRate));
        _levelMs += (blockMs - _levelMs) * a;
        if (envTarget <= 0f && _envelope <= 1e-4f) FadedOut = true;
    }
}

/// <summary>
/// One reflection of a live engine: the same signal, read back from the engine's ring buffer at
/// the delay the mirrored path implies, and placed at the mirrored source. The delay is slewed rather
/// than stepped, so as the car moves and the path length changes the echo glides in pitch — which is
/// the Doppler of the reflection, and the reason its emitter carries no velocity of its own.
/// </summary>
/// <summary>
/// The smear a rough surface puts on what it hands back: a short cascade of Schroeder all-passes,
/// flat in level and wandering in phase, spread over a time that grows with the roughness. See
/// <see cref="EngineEchoState.Scattering"/> for why an echo needs one.
/// </summary>
/// <summary>
/// The soft ceiling on a synthesized voice: untouched up to the knee, then bent smoothly towards a
/// ceiling it never reaches.
///
/// It used to be tanh(y) past ±0.8 and y below it, which is not continuous: just under the knee it
/// gave 0.8 and just over it tanh(0.8) = 0.664, so every backfire and overrun pop crossing the knee
/// put a one-sample step of 0.14 into the output — a click, twice per pop. This joins the straight
/// line at the knee with the same value and the same slope.
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
    /// of itself is a comb filter: evenly spaced notches that sweep as either end moves. That is the
    /// phasing ("sirens inside out") and most of the "laser beam", and it is not what a wall does. A
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
    private float GainSlew => Scattering >= 0f ? 1f / (0.18f * SampleRate) : 0.0015f;

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

        if (OwnCursor) { RenderOwnCursor(mono, floorSamples, target, gTarget); return; }
        if (Scattering >= 0f && _diffuser == null) _diffuser = new EchoDiffuser(Scattering, Seed, SampleRate);
        float slew = GainSlew;
        // Slew: up to 12% per sample of drift, which covers the Doppler of a fast pass.
        for (int i = 0; i < mono.Length; i++)
        {
            double diff = target - _delay;
            _delay += Math.Clamp(diff * 0.002, -0.12, 0.12);
            _gain += (gTarget - _gain) * slew;
            // Reading "back" from the source's current write position: the source rendered its
            // block before or after this one; the minimum delay covers either order.
            // Clamped to the slack as well as the target, so a delay that is being slewed downward
            // can never cross into the block the source may not have written yet.
            double back = Math.Max(_delay, floorSamples) + (mono.Length - i);
            float y = Source.ReadBack(back) * _gain;
            mono[i] = _diffuser != null ? _diffuser.Process(y) : y;
        }
    }

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
        double want = played - target;
        // Off the ring, or ahead of what the source has played at all: there is nothing to be
        // continuous with, so start again where we should be.
        if (_cursor < 0 || _cursor > played || played - _cursor > Source.RingLength - 4 * mono.Length)
            _cursor = want;

        for (int i = 0; i < mono.Length; i++)
        {
            _gain += (gTarget - _gain) * 0.0015f;
            mono[i] = Source.ReadAt(_cursor) * _gain;
            // One sample per sample, plus an inaudible pull back toward where the cursor belongs.
            double drift = (played - target) - _cursor;
            _cursor += 1.0 + Math.Clamp(drift * 1e-5, -MaxRateCorrection, MaxRateCorrection);
        }
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
public sealed class EngineTapState
{
    public readonly EngineVoiceState Source;

    /// <summary>Where this voice is heading, 0 or 1. Zero retires it; see <see cref="FadedOut"/>.</summary>
    public volatile float TargetGain = 1f;

    /// <summary>True once a fade-out has finished and the voice can be released.</summary>
    public volatile bool FadedOut;

    private float _gain;
    private double _cursor = -1;

    public EngineTapState(EngineVoiceState source) { Source = source; }

    public void Render(Span<float> mono)
    {
        // Nothing until the engine has something to give. A tap that synthesized on demand would be
        // doing it on the mixer thread, which is the one thing the whole producer design exists to
        // prevent.
        if (!Source.Primed) { mono.Clear(); return; }

        long played = Source.Played;
        // Start in step with the voice we are the other half of, and resync outright only when there
        // is no continuity left to keep — the source stopped, or we have fallen off the ring.
        if (_cursor < 0 || Math.Abs(played - _cursor) > Source.RingLength - 4 * mono.Length)
            _cursor = played;

        float gTarget = TargetGain;
        float step = 1f / (0.06f * MathF.Max(1f, Source.SampleRate));
        for (int i = 0; i < mono.Length; i++)
        {
            _gain += Math.Clamp(gTarget - _gain, -step, step);
            mono[i] = Source.ReadFrontAt(_cursor + i) * _gain;
        }

        // Wall clock, plus an inaudible pull back toward where the other half of this machine has
        // got to. A hundredth of the block is about a sixth of a semitone, applied only while the
        // two are out of step; what it is correcting is the difference between two channels' pitch,
        // which for the two ends of one car is very nearly nothing.
        double drift = played - _cursor;
        double maxNudge = Math.Max(1.0, mono.Length * EngineEchoState.MaxRateCorrection);
        _cursor += mono.Length + Math.Clamp(drift, -maxNudge, maxNudge);

        if (gTarget <= 0f && _gain <= 1e-4f) FadedOut = true;
    }
}

/// <summary>An FMOD DSP that is one outlet of a machine. Modelled on <see cref="EchoProcessor"/>.</summary>
public static class TapProcessor
{
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

    [ThreadStatic] private static float[]? _scratch;

    /// <summary>
    /// NOTHING MAY ESCAPE A DSP CALLBACK.
    ///
    /// This runs on FMOD's mixer thread, called from native code. An exception that reaches the
    /// native frame is not a caught fault, it is a CLR FATAL ERROR: the runtime aborts the process
    /// on the spot, with no managed stack, no log line, and a core that reads
    /// "libfmod -> libcoreclr -> abort". That is precisely the crash the city kept producing, and
    /// the only reason it was ever reachable is that the guard below stopped one line short.
    ///
    /// The old body wrapped only `state.Render(mono)`. Everything before it was bare — and the line
    /// that actually throws is `GCHandle.FromIntPtr(userData).Target`, which raises
    /// InvalidOperationException the moment the handle it names is no longer allocated. So the one
    /// statement most likely to fail was the one statement outside the net.
    ///
    /// GranularProcessor and SynthProcessor have always done it this way, one-shot log and all.
    /// These four had not.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        try { return ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels); }
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
        if (userData == IntPtr.Zero) return RESULT.OK;
        var state = (EngineTapState?)GCHandle.FromIntPtr(userData).Target;
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

    /// <summary>
    /// NOTHING MAY ESCAPE A DSP CALLBACK.
    ///
    /// This runs on FMOD's mixer thread, called from native code. An exception that reaches the
    /// native frame is not a caught fault, it is a CLR FATAL ERROR: the runtime aborts the process
    /// on the spot, with no managed stack, no log line, and a core that reads
    /// "libfmod -> libcoreclr -> abort". That is precisely the crash the city kept producing, and
    /// the only reason it was ever reachable is that the guard below stopped one line short.
    ///
    /// The old body wrapped only `state.Render(mono)`. Everything before it was bare — and the line
    /// that actually throws is `GCHandle.FromIntPtr(userData).Target`, which raises
    /// InvalidOperationException the moment the handle it names is no longer allocated. So the one
    /// statement most likely to fail was the one statement outside the net.
    ///
    /// GranularProcessor and SynthProcessor have always done it this way, one-shot log and all.
    /// These four had not.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        try { return ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels); }
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

    /// <summary>
    /// NOTHING MAY ESCAPE A DSP CALLBACK.
    ///
    /// This runs on FMOD's mixer thread, called from native code. An exception that reaches the
    /// native frame is not a caught fault, it is a CLR FATAL ERROR: the runtime aborts the process
    /// on the spot, with no managed stack, no log line, and a core that reads
    /// "libfmod -> libcoreclr -> abort". That is precisely the crash the city kept producing, and
    /// the only reason it was ever reachable is that the guard below stopped one line short.
    ///
    /// The old body wrapped only `state.Render(mono)`. Everything before it was bare — and the line
    /// that actually throws is `GCHandle.FromIntPtr(userData).Target`, which raises
    /// InvalidOperationException the moment the handle it names is no longer allocated. So the one
    /// statement most likely to fail was the one statement outside the net.
    ///
    /// GranularProcessor and SynthProcessor have always done it this way, one-shot log and all.
    /// These four had not.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        try { return ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels); }
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
