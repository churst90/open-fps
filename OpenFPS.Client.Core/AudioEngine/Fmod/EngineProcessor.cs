using System.Numerics;
using System.Runtime.InteropServices;
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
    /// Every wheel's squeal, in the server's wheel order (<see cref="WheelDynamics"/>), each from its
    /// own demand, slip and load, out through the tap at its end weighted by its distance against that
    /// tap's: the loaded outside front of a corner sings first, from the front, on its own side.
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
    /// How hard the road is working the tyres, a fraction of their grip, set by the game from the car's
    /// motion (only the game sees the corner). The voice adds what the game cannot see: the tenth of a
    /// second of slip a gear change puts through the driven wheels.
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
    /// The listener in the machine's frame (x across, y up, z forward, origin at the exhaust part), so
    /// each tailpipe radiates from its own place (<see cref="ExhaustNetwork.SetListener"/>).
    /// </summary>
    public void SetListener(Vector3 machineFrame)
    {
        Volatile.Write(ref _listenerX, machineFrame.X);
        Volatile.Write(ref _listenerY, machineFrame.Y);
        Volatile.Write(ref _listenerZ, machineFrame.Z);
        _listenerKnown = true;
    }

    /// <summary>
    /// True when the front outlet has a voice of its own and this one is the back alone, set by the game
    /// when the car's ends can be told apart (Localisation.Resolvable). The front slews across over about
    /// sixty milliseconds: a running waveform has no zero-crossing to switch at.
    /// </summary>
    public volatile bool SplitVoices;

    /// <summary>
    /// How much of the engine to integrate (EngineDetail), from how loud this voice is against the loudest
    /// engine heard (FmodAudioProvider.ChooseEngineDetail). Game thread writes; the engine hands over
    /// between the two without a step. From inside a vehicle it is always Full.
    /// </summary>
    public volatile EngineDetail Detail;

    /// <summary>
    /// Where the voice's envelope is heading, 0 or 1. Game thread writes. An engine is never cut: there
    /// is no zero-crossing to stop at, and cars passing in and out of the voice budget clicked every few
    /// seconds ("slight popping as they drive around").
    /// </summary>
    public volatile float TargetEnvelope = 1f;

    /// <summary>True once a fade-out has finished and the voice can be released.</summary>
    public volatile bool FadedOut;

    /// <summary>
    /// Brings a fading voice back to full; the other half of FadeOutEngine. A voice that wins its slot
    /// back mid-fade is taken off the retiring list, but without this its envelope still heads for zero
    /// and the car is silent for the rest of its life.
    /// </summary>
    public void Revive()
    {
        TargetEnvelope = 1f;
        FadedOut = false;
        Volatile.Write(ref _silentFrom, -1);
    }

    /// <summary>Where in the ring the fade-out reached silence, or -1: the voice is released once the
    /// mixer has played that far, not when it was rendered up to 0.7 s earlier, which cut it at full level.</summary>
    private long _silentFrom = -1;

    private float _envelope;
    /// <summary>
    /// The pressure at a metre that maps to full scale, pascals, set from the vehicle. A fixed 40 Pa
    /// (126 dB) suits a road car, but an unsilenced V10 peaks at 149 dB, thirteen times over, and came
    /// out of the soft ceiling as a square wave.
    /// </summary>
    public float PascalsAtFullScale = 40f;
    /// <summary>
    /// How much of the front of the car reaches this voice: 1 for a whole voice, since the intake's
    /// route is already declared (IntakeSpec.AirboxLossDb, IntakeSpec.Level) and anything less would say
    /// it twice. A field because a two-outlet vehicle crossfades its front to a second voice through it.
    /// </summary>
    public float FrontMix = 1f;
    /// <summary>
    /// How much of the tyre layer reaches the mix. Measured, not taste: the tyre voice alone puts a full
    /// squeal (scaled from the tyre's SquealDb) at 0.89 RMS while the exhaust runs in pascals and reaches
    /// tens, which left the squeal twenty-odd decibels under an engine it should be about seven under.
    /// </summary>
    public float TyreMix = DefaultTyreMix;
    private const float DefaultTyreMix = 1.4f;
    /// <summary>One axle at each end, at a level that keeps the POWER of the pair what the single
    /// coherent signal had (0.6 at each end, summed in phase: 1.2, so 0.6 x root 2 each). The squeal
    /// is still quoted against this; the rolling noise is anchored through it.</summary>
    private const float PerAxle = 0.6f * 1.41421356f;
    public float SampleRate = MixerQuality.MixerRate;

    private float _speedSmooth;

    // A render worker writes ahead into the rings and the FMOD callback only copies out of them
    // (EngineRenderPool). Echoes and borrowed voices read back behind the play position, not the
    // write position, so they do not depend on which DSP the mixer calls first.
    private const int RingBits = 17;                       // about three seconds at 44.1 kHz

    /// <summary>The back of the machine: the exhaust, and the body it shakes.</summary>
    private readonly float[] _ring = new float[1 << RingBits];

    /// <summary>
    /// The front of the machine: its intake and the block behind it. A second ring, not a second engine:
    /// a two-voice car costs one more copy-out and no more synthesis. The two taps sum to exactly what one
    /// voice plays (<see cref="Consume"/>), or the car would jump in level whenever the mixer changed its
    /// mind about how many voices to spend on it.
    /// </summary>
    private readonly float[] _front = new float[1 << RingBits];
    private long _written;                                 // producer writes, consumer only reads
    private long _played;                                  // consumer writes, producer only reads

    /// <summary>
    /// One producer at a time, claimed without waiting: the pool may hand one voice to two workers for
    /// an instant (its array is republished, not mutated), and two integrating one engine would corrupt
    /// it. The second has nothing to wait for, so it leaves.
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
    /// How fast the mixer takes this voice against its own rate: the channel's pitch, its Doppler. The
    /// provider sets it. <see cref="Played"/> moves in whole blocks, not at this rate: FMOD resamples a
    /// pitched DSP by calling it more or less often, 1024 samples each time (a car closing at 60 km/h was
    /// called 4.9 % more often), so a reader a fixed distance behind Played jumps 23 ms about twice a
    /// second. Readers that must keep step run a continuous clock at this rate (<see cref="SourceClock"/>).
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
    /// How far ahead of the mixer this voice's producer tries to stay, seconds. Per voice: a global
    /// figure grown when any voice starved let one new car (empty only because it is new) push all
    /// thirty others to the ceiling within a second of a map load. A voice grows its own lead from
    /// its own starves, and only after it has played for one lead. Long enough to absorb a scheduling
    /// hiccup, short enough that a throttle change is not heard late.
    /// </summary>
    private volatile float _leadSeconds = MinLeadSeconds;
    public const float MinLeadSeconds = 0.25f;
    /// <summary>The deepest the buffer will go when the machine cannot keep up.</summary>
    public const float MaxLeadSeconds = 0.7f;

    /// <summary>
    /// One sample at an absolute position in this voice's stream, linearly interpolated. Never read
    /// relative to the play position: it moves at this voice's own Doppler, and a borrowed voice reading
    /// back from it and applying its own heard two. A reader with its own cursor gets the audio at the
    /// rate it was synthesized.
    /// </summary>
    public float ReadAt(double position)
    {
        long i0 = (long)Math.Floor(position);
        float f = (float)(position - i0);
        int mask = _ring.Length - 1;
        int j0 = (int)(i0 & mask), j1 = (int)((i0 + 1) & mask);
        // Both taps, and every cabin path: a borrowed voice or an echo is the whole car heard from
        // somewhere else.
        float a = _ring[j0] + _front[j0], b = _ring[j1] + _front[j1];
        if (Volatile.Read(ref _cabinRings) is { } rings)
            foreach (var r in rings)
            {
                int m = r.Length - 1;
                a += r[(int)(i0 & m)]; b += r[(int)((i0 + 1) & m)];
            }
        return Soft(a + (b - a) * f);
    }

    /// <summary>The front tap alone at an absolute position, what an intake voice reads (cursor rules as
    /// in <see cref="EngineTapState"/>).</summary>
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

    /// <summary>How many samples the ring holds: the whole past a borrowed voice may read.</summary>
    public int RingLength => _ring.Length;

    /// <summary>
    /// True once the producer has filled the ring far enough for the mixer to take from it. Until then
    /// the voice gives silence, never synthesizes on demand: thirty cars come into earshot at a map load
    /// with empty rings, and thirty engines integrating inside one mixer callback is the overload this
    /// design exists to avoid. The wait is a moment at the start of a voice that is fading in anyway.
    /// </summary>
    public bool Primed => _primed;
    private volatile bool _primed;

    /// <summary>
    /// How long a new engine runs with its output thrown away, seconds. PlaceAtSpeed sets the crank
    /// turning but the waveguides start empty, and an exhaust pressurising from silence is a click (thirty
    /// at a map load, a second of mess). A tenth of a second is several cycles and more than the longest
    /// pipe's round trip, rendered on the worker before the voice is audible.
    /// </summary>
    private const float WarmupSeconds = 0.1f;
    private bool _warmed;

    /// <summary>
    /// Renders ahead until the producer is one lead in front of the mixer. Worker thread. Takes no lock
    /// the mixer could want: a lock held for a whole top-up (around 175 ms at load-time speed) and taken
    /// by a short mixer callback would freeze the mixer past FMOD's 93 ms buffer, and a deeper buffer
    /// would only make the top-up longer.
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
                // Thrown away: the play position moves up to meet it.
                Volatile.Write(ref _played, Volatile.Read(ref _written));
            }

            // A starved block moved the play position past what was written (a voice keeps wall
            // clock); resume from there. Still the producer moving _written: one writer.
            long played = Volatile.Read(ref _played);
            if (played > Volatile.Read(ref _written))
            {
                Volatile.Write(ref _written, played);
                // Starved while fading: nothing of it is left to play.
                if (TargetEnvelope <= 0f) FadedOut = true;
            }

            int room = _ring.Length - 8;
            int want = Math.Min((int)(_leadSeconds * SampleRate), room);
            while (Volatile.Read(ref _written) - Volatile.Read(ref _played) < want)
                Synthesize(Math.Min(512, want));

            if (!_primed && Volatile.Read(ref _written) - Volatile.Read(ref _played) >= want / 2) _primed = true;

            // Give the lead back slowly while it is not needed: a deep buffer makes the throttle late.
            float lead = _leadSeconds;
            if (lead > MinLeadSeconds) _leadSeconds = MathF.Max(MinLeadSeconds, lead - 0.0005f);
        }
        finally { Volatile.Write(ref _producing, 0); }
    }

    /// <summary>
    /// The mixer's block, out of what the producer has rendered. Mixer thread: nothing here may block or
    /// synthesize; a callback that waits on a producer, or integrates an engine itself, is a hole in
    /// every voice. A short ring gives what is there, ramps the rest to silence and counts a starve.
    ///
    /// A starved block is a gap, never a delay: the play position advances by the whole block and the
    /// producer resumes from there. Keeping your place plays the voice slow (900 of 1024 samples is 88 %
    /// speed, a tone and a half flat: the whole field winding down together) and drifts it behind its
    /// car, up to a lead (fifty metres at 250 km/h).
    /// </summary>
    public void Consume(Span<float> mono)
    {
        long at = Volatile.Read(ref _played);
        // Negative after a starve (the play position is ahead of the write) until the producer resyncs.
        long avail = _primed ? Volatile.Read(ref _written) - at : 0;
        int take = (int)Math.Clamp(avail, 0, mono.Length);
        int mask = _ring.Length - 1;
        // The front belongs here only while no other voice carries it; handed over in a 60 ms crossfade.
        float shareTarget = SplitVoices ? 0f : 1f;
        float shareStep = 1f / (0.06f * SampleRate);
        // The cabin's paths whose taps are not playing (yet, or at all) are carried here.
        var cabin = Volatile.Read(ref _cabinRings);
        for (int i = 0; i < take; i++)
        {
            int j = (int)((at + i) & mask);
            _frontShare += Math.Clamp(shareTarget - _frontShare, -shareStep, shareStep);
            float carried = cabin != null ? CarriedCabin(cabin, at + i, shareStep) : 0f;
            // The ground after the ceiling: with the road's up-to-6 dB bass lift inside it, every pulse
            // of a loud V8 hit the knee ("really bad over sampling ... the v8 muscle car"). The mixer
            // is floating point; the lift has room there.
            mono[i] = Ground.Process(Soft(_ring[j] + _front[j] * _frontShare + carried));
        }
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

        // Short: ramp out of the last sample; a step is a click.
        int ramp = Math.Min(mono.Length - take, 64);
        for (int i = 0; i < ramp; i++) mono[take + i] = _lastOut * (1f - (i + 1) / (float)ramp);
        mono[(take + ramp)..].Clear();
        _lastOut = 0f;

        if (!_primed) return;
        _starves++;
        Interlocked.Increment(ref GlobalStarves);
        // Grow the lead, but not for a voice younger than one lead: its producer is still catching up.
        float lead = _leadSeconds;
        if (_consumedSincePrimed > lead * SampleRate)
            _leadSeconds = MathF.Min(MaxLeadSeconds, lead * 1.35f);
    }

    /// <summary>Renders a block synchronously, filling what the ring lacks. Offline only (the lab and the
    /// tests), never from a mixer callback.</summary>
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
            // The ground after the ceiling, as in Consume.
            mono[i] = Ground.Process(Soft(_ring[j] + _front[j] * _frontShare));
        }
        Volatile.Write(ref _played, at + mono.Length);
        long silent = Volatile.Read(ref _silentFrom);
        if (silent >= 0 && at + mono.Length >= silent) FadedOut = true;
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
            // The voice is its level a metre from each source; the windows are about half the vehicle's
            // length from the bay, the pipe and the wheels together, falling as one over the distance.
            _windowFromSources = 1f / MathF.Max(1f, v.LengthMetres * 0.5f);
        }
        if (!string.IsNullOrEmpty(v.AirSystem))
        {
            try
            {
                _air = new AirSystem(ModelLibrary.Air(v.AirSystem), sampleRate, seed + 17);
                // Each valve is heard from whichever outlet is nearer its place (measured back from the
                // nose); the compressor is on the engine.
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
        // The wheels as the server's model lays them out, so order, loads and positions match the wire.
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
        _strikes = new OpenFPS.Client.AudioEngine.Core.WheelStrikes(body.Wheels.Select(w => w.Radius).ToArray(), sampleRate);
        _strikeNow = new float[nw];
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
            // It blows forward through the radiator and out of the grille: the vehicle's axis, and
            // the front tap.
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
        // end (the steered ones at the front), divided by the mix gain both tyre taps get, which the
        // squeal was set against.
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
    /// This block's drive for every wheel's squeal from the wheels as sent: the demand, the speed the
    /// rubber drags over the road (u sqrt(kappa^2 + tan^2 alpha), so a locked wheel slides at road
    /// speed) and the load over the static load; and each wheel's gain against its tap, the listener's
    /// distance from the tap over the distance from the wheel (each held to half a metre). False without
    /// this vehicle's count of wheels: then the axle voices squeal from the overall demand.
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
    /// The slip each axle's tyre voice plays: the overall demand, shared in the proportion the axles'
    /// worst wheels carry it. Without wheels both play the overall figure.
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

    /// <summary>The cooling fan's share, normally 1; an instrument mutes it to read what it is worth.</summary>
    public float FanMix = 1f;

    /// <summary>The coolant and the fan clutch, on a vehicle whose fan has one.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.Engine.CoolingSystem? _cooling;
    /// <summary>For instruments: the cooling system, or null.</summary>
    public OpenFPS.Client.AudioEngine.Core.Engine.CoolingSystem? Cooling => _cooling;
    private readonly float _rollingFrontPa, _rollingRearPa;
    /// <summary>How many wheels the front axle has, and each end's rolling radius (its tread tone).</summary>
    private readonly int _frontWheels;
    private readonly float _frontRadius, _rearRadius;

    // ── Sitting in it ───────────────────────────────────────────────────────────────────────────
    //
    // The same machine heard from the seat: nothing new is synthesised, only the path changes, and it
    // is three mechanisms declared on the vehicle. The panels by their mass: transmission falls 6 dB an
    // octave above rho*c / (pi*m), about 20 Hz for a 0.8 mm steel door, and a first-order low-pass at
    // that corner is the mass law. The seals, massless, letting everything through a little
    // (VehicleBody.SealLeak). The cabin, a box of air whose axial modes boom, here at full weight
    // (outside at CabinLeak). Plus the wind over the body, lost outside, its power going as the sixth
    // power of speed.

    /// <summary>Whether the listener is sitting in this vehicle. Game thread sets; read per block.</summary>
    public bool Interior
    {
        get => Volatile.Read(ref _interior) != 0;
        set
        {
            // The cabin's rings before the flag: the producer reads the flag, then the rings.
            if (value && _cabinLayout != null && Volatile.Read(ref _cabinRings) == null) EnsureCabin();
            Volatile.Write(ref _interior, value ? 1 : 0);
        }
    }
    private int _interior;

    // ── The cabin from where each path comes in (CabinPaths) ──────────────────────────────────
    //
    // Inside, the model is split into paths: the bulkhead (this voice's own ring), the exhaust under
    // the floor, each wheel at its corner, the wind at each A-pillar, a bus's door. Every path but the
    // first has a ring a tap voice reads from where it comes in (EngineTapState). Until its tap plays,
    // this voice carries it, handed over in the front outlet's 60 ms crossfade.

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
    /// <summary>The front and rear axles' tread tone phases, for the cabin's tread path.</summary>
    private double _treadFront, _treadRear;
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

    /// <summary>The interior voice's send into the room you sit in (the provider's, game thread), which
    /// its cabin taps send at too; negative until it has one.</summary>
    public volatile float CabinRoomSend = -1f;

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

    // Which samples of this voice went out at which time on the parent's clock: a seqlock of two copies
    // of the time round the position, written by the mixer, read by the cabin taps.
    private long _blockAtA = long.MinValue, _blockFrom, _blockAtB = long.MinValue;

    /// <summary>For the lab: replace the cabin with a click in this voice and its negative in the first
    /// path, to measure how the taps line up with the voice.</summary>
    internal static bool LabAlignProbe;

    /// <summary>
    /// Where this voice's channel's own clock sits on its parent's (parent minus own), once the channel
    /// has started; long.MinValue until then. Set from the game thread (Channel.getDSPClock).
    /// </summary>
    public long ChannelClockOffset { get => Volatile.Read(ref _channelClockOffset); set => Volatile.Write(ref _channelClockOffset, value); }
    private long _channelClockOffset = long.MinValue;
    public bool ChannelClockKnown => ChannelClockOffset != long.MinValue;

    /// <summary>The mixer took the block starting at <paramref name="from"/> in a block whose channel
    /// clock was <paramref name="clock"/>. Mixer thread.</summary>
    internal void NoteBlock(ulong clock, long from)
    {
        long offset = ChannelClockOffset;
        if (offset == long.MinValue) return;
        long at = (long)clock + offset;
        Volatile.Write(ref _blockAtA, at);
        Volatile.Write(ref _blockFrom, from);
        Volatile.Write(ref _blockAtB, at);
    }

    /// <summary>
    /// Where in this voice's stream is the sample that leaves at <paramref name="parentTime"/> on the
    /// parent's clock, so a cabin tap plays in step with this voice whichever FMOD calls first. The paths
    /// are one pressure field and must line up sample for sample: a tap on its own clock sat 239 to 1 024
    /// samples away (AudioLab --cabin probe=align), and at idle the intake's suction and the exhaust's
    /// pressure summed instead of cancelling, +6.5 dB measured. False until both clocks are known.
    /// </summary>
    internal bool BlockAt(long parentTime, out long position)
    {
        long b = Volatile.Read(ref _blockAtB);
        long from = Volatile.Read(ref _blockFrom);
        long a = Volatile.Read(ref _blockAtA);
        position = 0;
        if (a != b || a == long.MinValue) return false;
        long delta = parentTime - a;
        if (delta < -2 * DspCallback.MaxBlock || delta > 2 * DspCallback.MaxBlock) return false;
        position = from + delta;
        return true;
    }

    /// <summary>The cabin paths this voice still carries at sample <paramref name="at"/> (absolute),
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
    /// <summary>Where the starter's path through the mounts and the floor loses its top, Hz: from the
    /// seat a starter is a muffled whirr, not the buzz it is at the bellhousing. Writable so an
    /// instrument can bracket it.</summary>
    public float StarterPathCornerHz = 150f;
    private float _panelLp, _windLp, _windHp, _windHpIn, _interiorMix;

    // ── The windows down ──────────────────────────────────────────────────────────────────────────
    //
    // A window down is a massless hole in the cabin's wall (all four are a tenth of a hatchback's wall):
    // the street comes in at every frequency at the hole's share of the wall, as power, and the wind as
    // it is outside the glass. The cabin becomes a Helmholtz resonator; the shear layer over an opening
    // sheds vortices at about 0.45 U / L, and where that meets the cabin's note it locks in and throbs.
    // One window open, it does at motorway speed; all open, the note is out of reach and it only roars.
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
    // Loudness.Place compresses a source's declared level toward the ceiling, and an engine's declared
    // level is full load: idling 25 dB under it, uncompressed, a hatchback three metres away rendered
    // eleven decibels under a window air conditioner it is physically nine over ("I start the car, get
    // out, and it doesn't sound like it's running"). So the law is applied to the deficit too, over half
    // a second. Live voices only (the provider sets it); offline renders measure true pascals.
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
    /// The unweighted law applied to the deficit, held to 0..20 dB: what runs with the ear model off
    /// (/ear off, OPENFPS_EAR_MODEL=0).
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
    /// it is doing now, level and tone (docs/EAR_MODEL.md): an idling engine is mostly bass the ear
    /// barely hears. No cap (the 20 dB bounded only the unweighted law). Below the threshold of hearing
    /// it returns NaN and the caller holds the lift where it was.
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
    // Nothing on the wire says "the brakes came off": a release ends a deceleration, a park brake
    // follows standing still, a bus's doors open once stopped and shut before it moves. So the events
    // are read off the speed history the voice already keeps, on the render thread.

    /// <summary>Whether the doors are standing open, which the beeper runs on. Exposed so a test can
    /// tell "the beeper is inaudible" from "the doors never opened".</summary>
    public bool DoorsOpen => _doorsOpen;

    /// <summary>Whether this voice built a door beeper at all.</summary>
    public bool HasDoorChime => _chime != null;

    /// <summary>The largest beeper sample this voice has produced, pascals. Diagnostic.</summary>
    public float PeakChimePa { get; private set; }

    private readonly AirSystem? _air;
    // The door beeper: a piezo behind a grille over the doorway, sounding while the doors are open.
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
    /// The air round the vehicle, °C, for its cooling system (whether the air conditioning is on, and so
    /// whether the fan runs at a standstill). NaN (the default) takes the world's
    /// (AudioPhysics.CurrentAirCelsius); a test sets it outright.
    /// </summary>
    public float AmbientCelsius = float.NaN;
    private float _bayLeak;
    /// <summary>
    /// The intake as it leaves through the bay: scattered off the block, the bulkheads and the bonnet,
    /// a few milliseconds of paths, so not the grille's waveform a second time.
    /// </summary>
    private readonly EchoDiffuser _bayIntake;
    private readonly bool _engineAtRear;
    /// <summary>Which way the tailpipe throws its sound and what the body does to it, for where the
    /// listener is. See ExhaustRadiation.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.Engine.ExhaustRadiation _radiation;
    /// <summary>Where the bay's noise leaves the vehicle, grille and open floor, and what the body does
    /// to it on the way to the listener. See BayRadiation.</summary>
    private readonly OpenFPS.Client.AudioEngine.Core.Engine.BayRadiation _bayRadiation;
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
    /// What escapes the engine bay, 0..1: the vehicle's <see cref="VehicleProfile.EngineBayLeakage"/>,
    /// writable so an instrument can render with and without it and read the contribution, not the
    /// constant (as `--engine-orders jet=0` does for the gas path).
    /// </summary>
    public float BayLeakage { get => _bayLeak; set => _bayLeak = Math.Clamp(value, 0f, 1f); }
    private float _lastSpeedForAir, _accelForAir;
    private float _brakedSeconds, _stoppedSeconds, _peakBrake;
    private bool _parked, _doorsOpen, _holding;
    /// <summary>m/s²: a foot on the pedal. Lifting off at city speed is a few tenths; at 0.45 the
    /// brakes hissed slowing for every corner on the racing line.</summary>
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
            // Only at a stop that takes passengers: parking, kneeling and opening the doors at every
            // junction made each give-way on the city end in a long blow of air.
            if (!_parked && ServingStop && _stoppedSeconds > ParkAfterSeconds)
            {
                _parked = true;
                if (_holding) ReleaseService();             // foot off the pedal as the springs take it
                _air.Vent("parking");                       // spring brakes: the chambers dump
                // It kneels: the kerb-side bags dump and the body drops a hundred millimetres, the
                // long low hiss that says "bus at a stop".
                if (_air.Ports.ContainsKey("kneel")) _air.Vent("kneel");
                if (_air.Ports.ContainsKey("door")) { _air.Vent("door"); _doorsOpen = true; }
            }
        }
        else
        {
            if (_parked)
            {
                // Moving off: doors shut, then the park brake off, a shorter hiss (only the control
                // line vents while the springs are pushed back).
                if (_doorsOpen) { _air.Vent("door", 0.6f); _doorsOpen = false; }
                _air.Vent("parking", 0.35f);
                _parked = false;
            }
            else if (_holding) ReleaseService();            // off the brake and away
            _stoppedSeconds = 0f;
        }
    }

    /// <summary>
    /// The service chambers exhausting through the quick-release valve, at the pressure the braking put
    /// in them: a gentle stop is a quarter of full service and a quiet "pssht". Vented by how long the
    /// pedal was down, a slow three-second stop dumped the full fourteen litres.
    /// </summary>
    private void ReleaseService()
    {
        _air!.Vent("service_release", Math.Clamp(_peakBrake / FullServiceDecel, 0.1f, 1f));
        _peakBrake = 0f;
        _holding = false;
    }

    /// <summary>One sample of the door beeper, sounding while the doors are open.</summary>
    private float StepChime()
    {
        var c = _chime!;
        float dts = 1f / SampleRate;
        _chimeCycle += c.RateHz * dts;
        if (_chimeCycle >= 1.0) _chimeCycle -= 1.0;
        bool on = _doorsOpen && _chimeCycle < c.Duty;
        // A piezo takes a few milliseconds to start and stop; an instant edge is a click.
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

    /// <summary>
    /// Starts the voice as a car already doing this speed: the driveline teleported, in the gear the
    /// speed implies, the crank spun to match. From a dead engine the driver floored it to catch up, a
    /// whole spin-up in the eighty milliseconds of the speed filter, every time a car entered the budget.
    /// </summary>
    public void PlaceAtSpeed(float metresPerSecond)
    {
        _speedSmooth = MathF.Max(0f, metresPerSecond);
        Driveline.Teleport(_speedSmooth);
        Driver.TargetSpeed = _speedSmooth;

        // The highest gear between the downshift and upshift points: without the lower bound a truck
        // placed at 40 km/h went in eighth at 600 rpm and changed down twice as its voice started.
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

    /// <summary>Each wheel struck by a step in the road (a rail): see WheelStrikes. Game thread.</summary>
    public void QueueStrikes(IReadOnlyList<OpenFPS.Client.AudioEngine.Core.WheelStrike> strikes) => _strikes.Queue(strikes);

    private readonly OpenFPS.Client.AudioEngine.Core.WheelStrikes _strikes;
    /// <summary>This sample's strike on each wheel, pascals at a metre, for the cabin's paths.</summary>
    private readonly float[] _strikeNow;
    private bool _strikesLeftOver;

    /// <summary>Integrates <paramref name="count"/> samples of engine into the rings. Producer thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Synthesize(int count)
    {
        float dt = 1f / SampleRate;
        _strikes.Drain(_written, Volatile.Read(ref _played), OpenFPS.Common.AudioClock.Now, SampleRate, ConsumeRate);
        long strikeBase = _written;
        float gain = 1f / MathF.Max(1f, PascalsAtFullScale);
        float target = TargetSpeed;
        Driver.Running = Running;
        Engine.Detail = Interior ? EngineDetail.Full : Detail;
        // About 60 ms either way: no step survives it, and a car arriving is still a car arriving.
        float envStep = 1f / (0.06f * SampleRate);
        float envTarget = TargetEnvelope;
        int mask = _ring.Length - 1;
        long w = _written;
        bool inside = Interior;
        var cabinRings = _cabinLayout != null ? Volatile.Read(ref _cabinRings) : null;
        // Inside, the sound comes through the floor: the outside geometry (which pipe is nearer, which
        // way the fan blows) does not apply.
        if (_listenerKnown && !inside)
        {
            var heard = new Vector3(Volatile.Read(ref _listenerX), Volatile.Read(ref _listenerY), Volatile.Read(ref _listenerZ));
            Engine.SetListener(heard);
            // Relative to where this voice is placed: the true tailpipe once the ends are split, the
            // compromise point before (VehicleProfile.ExhaustOffset).
            var placedAt = SplitVoices ? Vehicle.ExhaustSlot : Vehicle.ExhaustOffset;
            _radiation.Aim(heard + placedAt);
            _bayRadiation.Aim(heard + placedAt);
        }
        else { _radiation.Aim(null); _bayRadiation.Aim(null); }
        float panelA = 1f - MathF.Exp(-2f * MathF.PI * _panelCorner * dt);
        float starterA = 1f - MathF.Exp(-2f * MathF.PI * StarterPathCornerHz * dt);
        float liftTarget = 1f;
        if (CompensateLevel && _levelMs > 0)
        {
            float nowDb = 10f * MathF.Log10((float)_levelMs / (20e-6f * 20e-6f) + 1e-12f);
            // Placed by its declared level, the voice is lifted to where the law places what it is
            // running at, level and spectrum (LiftDb). Worked out only when the spectrum is re-measured
            // or the level moves: the level is a half-second average anyway.
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
        // Linear, no floor on the step: a floor made a falling lift rise through the block and snap down
        // at its end, a step every block while an engine revved.
        float liftStep = (liftTarget - _levelGain) / MathF.Max(1, count);
        var wheels = Wheels;
        bool perWheel = WheelsDrive(wheels, inside);
        // Each wheel's own water when the wheels are sent, the road's wheel-path figure when not.
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
            // The shift chirp, which only this voice can see: a big ratio step with the throttle open
            // puts the driven wheels briefly out of step with the road.
            if (Driveline.Gear != _tyreGear && _tyreGear >= 1 && Driveline.Gear >= 1)
            {
                float from = Driveline.Ratio(_tyreGear), to = Driveline.Ratio(Driveline.Gear);
                if (from > 0f && to > 0f)
                    _tyreChirp = MathF.Max(_tyreChirp, TyreFriction.ShiftChirp(from / to, Engine.Throttle));
            }
            _tyreGear = Driveline.Gear;
            _tyreChirp *= _chirpDecay;
            // Two axles, two independent tyre noises: one signal at both ends combs against itself as
            // the car goes by and is heard as a car passing inside out.
            float tyreRear, tyreFront;
            float frontSlip = 0f, rearSlip = 0f;
            if (perWheel)
            {
                // Each wheel squeals for itself; the axle voices roll and carry their wheels' squeal.
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
            // A wheel over a rail: its strike out through its end's tap, with its own distance gain.
            if (_strikes.Busy)
            {
                _strikes.Advance(strikeBase + i);
                float sf = 0f, sr = 0f;
                for (int k = 0; k < _strikeNow.Length; k++)
                {
                    float x = _strikes.Out(k);
                    _strikeNow[k] = x;
                    if (_wheelFront[k]) sf += x * _wheelGain[k]; else sr += x * _wheelGain[k];
                }
                frontTyre += sf * (TyreMix / DefaultTyreMix);
                rearTyre += sr * (TyreMix / DefaultTyreMix);
                _strikesLeftOver = true;
            }
            else if (_strikesLeftOver) { Array.Clear(_strikeNow); _strikesLeftOver = false; }
            // The water: already pascals at a metre, each end's wheels at that end's tap.
            _wet.Step();
            rearTyre += _wet.Rear * wetMix;
            frontTyre += _wet.Front * wetMix;
            // The front: its tyres, the fan, and what the bay lets out. The block has one route out,
            // the bay (VehicleProfile.EngineBayLeakage), never a second constant one: a bus's block is
            // seven decibels over its silenced tailpipe (`--voice-levels parts`). The intake leaves the
            // same way: a snorkel draws under the same bonnet, and pass-by separations find the block,
            // not the intake, dominating a car's front (ISMA 2014); straight out of the front it was
            // 10-18 dB too much on a V8 at full throttle. A bike, with no bay, lets all of it out.
            float front = frontTyre;
            float fanOut = 0f;
            if (_fan != null)
            {
                // At engine speed through its clutch (CoolingSystem), or its relay's speed for an
                // electric fan; set on the slow tick.
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
            // An engine in the back (VehicleProfile.EngineAtRear) cools and leaks out beside the pipe.
            if (_engineAtRear) pa += fanOut;

            // The body rings as well as the pipe radiating, so it is added, and it is the one linear,
            // time-invariant part (the gas path's resonances move 75 % with exhaust temperature). Driven
            // by the exhaust, as the offline VehicleSynth render drives it: a body that coloured one
            // renderer and not the other is a change measured as working and heard as nothing.
            pa += _body.Process(Engine.Exhaust) * BodyMix;
            // What escapes the bay leaves where the engine is, out of the front tap for a front
            // engine: on the school bus the block is 97.7 dB against an 83 dB silenced pipe, and out of
            // the back an idling bus was one sound at its tail. It leaves by the grille and the open
            // floor, shaded by the body (BayRadiation); `bayLevel` is before shading, for the loudness law.
            float bayLevel = _bayLeak > 0f ? (Engine.Block - Engine.StarterOut + FrontMix * _bayIntake.Process(Engine.Intake)) * _bayLeak : 0f;
            float bay = _bayRadiation.Process(bayLevel);
            // The starter hangs under the car on the bellhousing, in no enclosure: through the bay
            // leak it was 16 dB down and a big V8's start could not be heard from the kerb.
            float starterOut = Engine.StarterOut * MathF.Max(_bayLeak, StarterUnderbody);
            bay += starterOut;
            bayLevel += starterOut;
            if (_engineAtRear) pa += bay; else front += bay;
            // The air valves and the door beeper, each at its own end: the door and kneeling valves and
            // the beeper at the front door, the brake releases at the axles; none from the tailpipe.
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

            // What the machine radiates, which the loudness law is applied to: the bay and both axles'
            // tyres (the larger part at a cruise; only the rear is in `pa`), not the air or the beeper,
            // which do not idle, and without the pipe's directivity: a car facing away is not running
            // quietly, and the idle lift must not turn it back up.
            float engineOnly = pa - rearExtras + (_engineAtRear ? -fanOut : bayLevel) + frontTyre + (Engine.Exhaust - exhaustOut);
            blockSum += (double)engineOnly * engineOnly;
            if (CompensateLevel)
            {
                _bandScratch[_bandFill++] = engineOnly;
                if (_bandFill == _bandScratch.Length) FlushBands();
            }
            tyreSum += (double)(rearTyre * rearTyre + frontTyre * frontTyre);

            // Crossfaded over ~60 ms, so getting in or out is not a click.
            _interiorMix += Math.Clamp((inside ? 1f : 0f) - _interiorMix, -envStep, envStep);
            // What the cabin carries that the lift leaves alone (the air and the beeper), in this voice.
            float insideExtras = chimeOut + 0.5f * airOut;
            float doorUnlifted = 0f;
            bool split = false;
            if (_interiorMix > 0f && cabinRings != null)
            {
                // The model below, path by path (CabinPaths), each from where it comes in. Linear, so
                // the paths sum to the one signal, except that the tyres are a noise per wheel and the
                // wind a noise per side, at the same powers.
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
                    // Its roar and its squeal: the tread tone is played once, below (CabinPaths.Kind.Tread).
                    float corner = perWheel
                        ? VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, 0f, _rng, ref _cornerTyre[q], _cornerPa[q], _cornerRadius[q], _wheelSq[q], SampleRate,
                                            toneScale: 0f)
                        : VehicleSynth.Tyre(Vehicle.Tyres, Driveline.Speed, (_wheelFront[q] ? frontSlip : rearSlip) + _tyreChirp, _rng,
                                            ref _cornerTyre[q], _cornerPa[q], _cornerRadius[q], sampleRate: SampleRate,
                                            squealScale: 1f / MathF.Sqrt(lay.GroupWheels[q]), toneScale: 0f);
                    // A rail under this wheel comes in through its arch with the rest of it.
                    if (q < _strikeNow.Length) corner += _strikeNow[q] / (PerAxle * DefaultTyreMix);
                    int pq = lay.PathOfWheel[q];
                    float src = corner * TyreToPanels * TyreMix;
                    _pathLp[pq] += (src - _pathLp[pq]) * panelA;
                    _pathNow[pq] += _pathLp[pq] + _sealLeak * src + _wet.CabinWheel(q) * wetMix;
                }
                if (lay.PathOfWheel.Length > 0) _pathNow[lay.PathOfWheel[0]] += _wet.CabinTail * wetMix;
                // The tread tone of both ends, as the axles make it, from under the floor.
                {
                    float tone = (VehicleSynth.TreadTone(Vehicle.Tyres, Driveline.Speed, _rollingFrontPa, _frontRadius, ref _treadFront, SampleRate)
                                + VehicleSynth.TreadTone(Vehicle.Tyres, Driveline.Speed, _rollingRearPa, _rearRadius, ref _treadRear, SampleRate))
                                * TyreToPanels * TyreMix;
                    int tp = lay.Tread;
                    _pathLp[tp] += (tone - _pathLp[tp]) * panelA;
                    _pathNow[tp] += _pathLp[tp] + _sealLeak * tone;
                }
                // The starter through the mounts and the floor of the footwell.
                _starterLp1 += (Engine.StarterOut * _starterPath - _starterLp1) * starterA;
                _starterLp2 += (_starterLp1 - _starterLp2) * starterA;
                _pathNow[0] += _starterLp2;
                // The cabin's modes, driven by everything through the body; its boom is low enough
                // that where it is played from hardly matters.
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
                    // The throb is the whole cabin's air at once, the same at both ears.
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
                    // At the outside of the cabin: the bay ahead of the firewall, the exhaust along the
                    // floor, and all four tyres at the power the single signal had.
                    float atPanels = Engine.Block + Engine.Intake + 0.5f * Engine.Exhaust + (tyreRear + tyreFront) * 0.6f * 0.70710678f * TyreMix;
                    _panelLp += (atPanels - _panelLp) * panelA;
                    float inCabin = _panelLp + _sealLeak * atPanels;
                    // The starter through the mounts and the floor (VehicleBody.StarterPathLossDb): two
                    // poles at the path's corner, the top gone through rubber and a damped floor.
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

                    // Followed over about a tenth of a second, so a glass moving is a glide.
                    _windowsNow += (Math.Clamp(WindowsOpen, 0f, 1f) - _windowsNow) * MathF.Min(1f, dt * 10f);
                    if (_windowsNow > 0.001f && _windowShareFull > 0f)
                    {
                        float share = _windowsNow * _windowShareFull;
                        float hole = MathF.Sqrt(share);                // pressure through the opening
                        // The outside straight in, as heard at the windows.
                        inCabin += (pa - rearExtras + bay) * hole * _windowFromSources;
                        // The wind outside the glass, broader than through it (_windLp is about 0.6 RMS).
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
                                // A resonator at the cabin's note, damped more by each window venting it.
                                float w0 = 2f * MathF.PI * _cabinHelmholtzHz * dt;
                                float r = MathF.Exp(-w0 / (2f * (6f / MathF.Max(1, _windowCount))));
                                float y = n * (1f - r) + 2f * r * MathF.Cos(w0) * _buffetY1 - r * r * _buffetY2;
                                _buffetY2 = _buffetY1; _buffetY1 = y;
                                float q = 0.5f * 1.2f * speed * speed;
                                inCabin += y * locked * BuffetShare * q * _windowsNow;
                            }
                        }
                    }

                    // Inside with you, not through the body: the beeper over the doorway and the door
                    // engines venting into the step well (half the air system; the brakes are the rest).
                    inCabin += chimeOut + 0.5f * airOut;
                    // An open doorway, 2.4 m^2 of a hundred-odd m^2 of wall: -16 dB of the outside, unfiltered.
                    if (_doorsOpen) inCabin += (pa - rearExtras + bay) * DoorwayLeak;
                    float k = _interiorMix;
                    pa = pa * (1f - k) + inCabin * k;
                    front *= 1f - k;
            }

            _envelope += Math.Clamp(envTarget - _envelope, -envStep, envStep);
            // Written without the soft ceiling, which belongs to whoever sums the taps: a limiter on
            // each half is not the same limiter, and one voice must equal the two taps summed.
            _levelGain += liftStep;
            // The lift is the engine's alone: lifting an idling bus's air brake release with it made
            // every bus stop audible across the city. Inside, the air and the beeper arrive in the
            // back tap through the cabin; outside, each end carries its own.
            float extras = (1f - _interiorMix) * rearExtras
                         + _interiorMix * insideExtras;
            pa = (pa - extras) * _levelGain + extras;
            float outSample = pa * gain * _envelope;
            // The lab's alignment probe: a click every tenth of a second here and inverted in the first
            // cabin path; in line, they cancel.
            if (LabAlignProbe && split) outSample = w % 4800 == 0 ? 0.25f : 0f;
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
                    if (LabAlignProbe && split) v = p == 1 && w % 4800 == 0 ? -0.25f : 0f;
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
        // The tyres' share of the pressure, for the ground reflection (GroundReflection.NearGroundShare),
        // over about half a second.
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
        if (envTarget <= 0f && _envelope <= 1e-4f)
        {
            // Released once the mixer has played this far (Consume), not now.
            if (Volatile.Read(ref _silentFrom) < 0) Volatile.Write(ref _silentFrom, _written);
        }
        else if (Volatile.Read(ref _silentFrom) >= 0) Volatile.Write(ref _silentFrom, -1);
    }
}

/// <summary>
/// The soft ceiling on a synthesized voice: untouched up to the knee, then bent toward a ceiling it
/// never reaches, joining the line at the knee with the same value and slope. A plain tanh past the knee
/// is discontinuous there (0.8 under, 0.664 over): a click twice per backfire. The ceiling is 2 dB over
/// full scale so the peakiest pulse in the fleet (a 450 single's blowdown, 1.6 dB over at its 99.9th
/// percentile) is rounded, not squared; a unit sample is still the declared level plus
/// <see cref="OpenFPS.Common.VehicleProfile.PeakHeadroomDb"/>, and the floating-point mix ends in the
/// master limiter.
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
        // Mutually prime-ish bases, ms, stretched by the roughness. Four stages leave no regular
        // structure in the phase; more would sound like a room, the reverb's job. The delays are also
        // time the echo arrives late by, so a mirror gets almost none: under two milliseconds for
        // polished metal and glass (s ~ 0.05), a dozen for brick, twenty for a crowd.
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
/// One reflection of a live engine, or a borrowed voice: the engine's ring read back at the mirrored
/// path's delay and placed at the mirrored source. The delay slews, so the echo glides in pitch as the
/// path changes: that is its Doppler, and why its emitter carries no velocity of its own.
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
    /// Floor on the echo's delay, in mixer blocks, not time. FMOD calls the source's and the echo's DSPs
    /// in an order that flips whenever the graph changes (several times a second on a racetrack), and a
    /// flip reads a whole block early or late: a click. Two blocks absorbs it either way; a fixed 26 ms is
    /// two blocks at 512 samples but only 1.1 at the 1024 FMOD chooses.
    /// </summary>
    public const int MinDelayBlocks = 2;

    /// <summary>The floor as a time, the fallback before a block has been seen.</summary>
    public const float MinDelaySeconds = 0.026f;

    private int _blockSlack;

    /// <summary>
    /// Keeps its own read cursor instead of following the source's play position: set for a borrowed
    /// voice (a distant car voiced from a near car's ring), which is a different car with its own
    /// Doppler; following the source gave it two, and a car at the redline sounded like it was
    /// cruising. False for a reflection, which is the source arriving late and keeps the source's
    /// Doppler (so its channel pitch is pinned to 1).
    /// </summary>
    public bool OwnCursor;

    /// <summary>Where this voice has read up to, absolute, when it keeps its own cursor.</summary>
    private double _cursor = -1;

    /// <summary>
    /// The hardest the own cursor is pulled back toward its place, a fraction of the rate. A rate error
    /// is a pitch error: a hundredth is about a sixth of a semitone, on a voice too far away to tell
    /// apart. It corrects the drift from the source car's Doppler, which averages out over a lap.
    /// </summary>
    public const double MaxRateCorrection = 0.01;

    public EngineEchoState(EngineVoiceState source) { Source = source; SampleRate = source.SampleRate; }

    /// <summary>
    /// How rough the surface was, 0..1, or below zero for a borrowed voice (not an echo). Set once,
    /// before the first block. An echo is smeared through an <see cref="EchoDiffuser"/>: an exact
    /// delayed copy combs against the source and sounds inside out, where a real wall returns the same
    /// sound but not the same waveform. Level and arrival time are untouched. See docs/CLIENT_NOTES.md,
    /// "Why an echo is smeared".
    /// </summary>
    public float Scattering = -1f;
    /// <summary>Seeds the smear's delays, so each wall's is its own.</summary>
    public int Seed;

    private EchoDiffuser? _diffuser;

    /// <summary>
    /// How fast the level follows its target, per sample: a reflection swells and dies over a few
    /// hundred milliseconds as the lit patch of wall slides along; a borrowed voice, a car, follows fast.
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
        // The largest block ever handed in: a short (partial) block must not shrink the margin.
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
            // Held to the floor as well, so a delay slewing down never reads into a block the source
            // may not have written yet.
            double back = Math.Max(_delay, floorSamples) + mono.Length;
            float y = Source.ReadAt(_clock.Position - back) * _gain;
            _clock.Position += rate;
            mono[i] = _diffuser != null ? _diffuser.Process(y) : y;
        }
    }

    private SourceClock _clock = SourceClock.Unset;

    /// <summary>
    /// The audio at the rate it was synthesized: the cursor advances a sample per sample and is nudged,
    /// never jumped, toward its place behind the source, so the source's consumption never reaches this
    /// pitch. It resyncs outright only after falling off the ring (the source stopped or restarted).
    /// </summary>
    private void RenderOwnCursor(Span<float> mono, double floorSamples, double target, float gTarget)
    {
        long played = Source.Played;
        // Aimed at a continuous clock (SourceClock), not Played, which moves in whole blocks: aimed at
        // Played the cursor jumped whenever the source missed a call. A block further back than the
        // floor, so the saw's dips do not reach the ceiling below.
        double rate = _clock.Begin(played, Source.ConsumeRate, mono.Length, SampleRate);
        double where = _clock.Position;
        double back = target + mono.Length;
        // Off the ring, or ahead of what the source has played: nothing to be continuous with.
        if (_cursor < 0 || _cursor > played || played - _cursor > Source.RingLength - 4 * mono.Length)
            _cursor = where - back;

        for (int i = 0; i < mono.Length; i++)
        {
            _gain += (gTarget - _gain) * _k.Gain;
            mono[i] = Source.ReadAt(_cursor) * _gain;
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
/// The front outlet of a machine as a voice of its own, or one of its cabin paths. At close range the
/// couple of metres between intake and exhaust is most of how a listener knows which way a car points;
/// through one voice, placed toward the tailpipe (VehicleProfile.ExhaustEmitterBias), it is lost. Not a
/// second engine: the same integration writes both taps (<see cref="EngineVoiceState"/>) and this reads
/// the front one; summed, they are exactly the one voice. It advances at the rate the audio was
/// synthesized, or the exhaust's Doppler lands on the intake's own, and is nudged, never jumped, into
/// step: a rate correction is a pitch error, a jump a click.
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

    /// <summary>Cabin path blocks read in line with the engine's voice by the mixer clock, and those read
    /// on the tap's own clock because they could not be lined up. For the instruments.</summary>
    internal static long AlignedBlocks, UnalignedBlocks;

    /// <summary>Where this tap's channel's own clock sits on its parent's (EngineVoiceState.ChannelClockOffset).</summary>
    public long ChannelClockOffset { get => Volatile.Read(ref _channelClockOffset); set => Volatile.Write(ref _channelClockOffset, value); }
    private long _channelClockOffset = long.MinValue;

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

    public void Render(Span<float> mono) => Render(mono, long.MinValue);

    /// <param name="mono">The block to fill.</param>
    /// <param name="parentTime">Where this block starts on the parent's clock (this channel's clock plus
    /// <see cref="ChannelClockOffset"/>), or long.MinValue if not known.</param>
    public void Render(Span<float> mono, long parentTime)
    {
        // Silence until primed: synthesizing on demand would be on the mixer thread.
        if (!Source.Primed) { mono.Clear(); return; }

        // A cabin path: exactly the samples the engine's voice plays at this moment. Rider and vehicle
        // move together, so neither channel is pitched.
        if (CabinPath > 0 && parentTime != long.MinValue && Source.BlockAt(parentTime, out long from))
        {
            Interlocked.Increment(ref AlignedBlocks);
            float gStep = 1f / (0.06f * MathF.Max(1f, Source.SampleRate));
            float gTo = TargetGain;
            for (int i = 0; i < mono.Length; i++)
            {
                _gain += Math.Clamp(gTo - _gain, -gStep, gStep);
                mono[i] = Source.ReadCabinAt(CabinPath, from + i) * _gain;
            }
            _clock.Position = from + mono.Length;
            if (gTo <= 0f && _gain <= 1e-4f) FadedOut = true;
            return;
        }

        if (CabinPath > 0) Interlocked.Increment(ref UnalignedBlocks);
        // In step with the source on a continuous clock (the source's rate over ours, leaning slowly on
        // Played). Stepping toward Played, which moves in whole blocks, made a passing car's front
        // voice jump 4-8 times a second, measured (--quality echo, "front").
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
/// Where a live voice's play position is, continuously, for a reader that must stay in step with it.
/// <see cref="EngineVoiceState.Played"/> is right only on average (a pitched channel is taken a whole
/// block at a time). This runs at the rate it is told and leans on Played through an error averaged over
/// half a second, at most half a per cent (under a tenth of a semitone), so the block saw never reaches
/// the read and real drift goes in seconds. It restarts only when it has lost the source by a few blocks.
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
    /// Nothing may escape a DSP callback: an exception reaching FMOD's native frame is a CLR fatal error,
    /// the process aborted with no managed stack and no log line (core: "libfmod -> libcoreclr -> abort").
    /// So the whole body is inside the guard; `GCHandle.FromIntPtr(userData).Target` throws the moment
    /// its handle is freed.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        long profiled = MixerProfile.Start();
        try
        {
            try
            {
                var r = ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
                NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "engine tap", ref _nonFiniteOther);
                return r;
            }
            catch (Exception ex)
            {
                // Logged once: a faulting DSP faults every block, 43 a second.
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
        finally { MixerProfile.Stop(MixerProfile.Kind.EngineTap, profiled); }
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
        long parentTime = long.MinValue;
        if (state.CabinPath > 0 && state.ChannelClockOffset is long offset && offset != long.MinValue
            && DspCallback.Clock(ref dsp_state, out ulong clock))
            parentTime = (long)clock + offset;
        try { state.Render(mono, parentTime); } catch { mono.Clear(); }
        unsafe
        {
            float* outBuf = (float*)outbuffer;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < ch; c++) outBuf[i * ch + c] = mono[i];
        }
        return RESULT.OK;
    }
}

/// <summary>The FMOD DSP of an engine's echo or borrowed voice (<see cref="EngineEchoState"/>).</summary>
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
    /// Nothing may escape a DSP callback: an exception reaching FMOD's native frame is a CLR fatal error,
    /// the process aborted with no managed stack and no log line (core: "libfmod -> libcoreclr -> abort").
    /// So the whole body is inside the guard; `GCHandle.FromIntPtr(userData).Target` throws the moment
    /// its handle is freed.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        long profiled = MixerProfile.Start();
        try
        {
            try
            {
                var r = ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
                NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "engine echo", ref _nonFiniteOther);
                return r;
            }
            catch (Exception ex)
            {
                // Logged once: a faulting DSP faults every block, 43 a second.
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
        finally { MixerProfile.Stop(MixerProfile.Kind.EngineEcho, profiled); }
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
/// The FMOD DSP of a vehicle's voice: it copies out of the engine's ring, which the render pool keeps
/// ahead (EngineRenderPool), and notes which samples went out when for the cabin taps.
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
    /// Nothing may escape a DSP callback: an exception reaching FMOD's native frame is a CLR fatal error,
    /// the process aborted with no managed stack and no log line (core: "libfmod -> libcoreclr -> abort").
    /// So the whole body is inside the guard; `GCHandle.FromIntPtr(userData).Target` throws the moment
    /// its handle is freed.
    /// </summary>
    private static RESULT ReadCallback(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        long profiled = MixerProfile.Start();
        try
        {
            try
            {
                var r = ReadCallbackCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels);
                NonFinite.After(ref dsp_state, outbuffer, length, inchannels, outchannels, "engine voice", ref _nonFiniteOther);
                return r;
            }
            catch (Exception ex)
            {
                // Logged once: a faulting DSP faults every block, 43 a second.
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
        finally { MixerProfile.Stop(MixerProfile.Kind.Engine, profiled); }
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
        // Which samples went out when, for the cabin's taps (EngineVoiceState.BlockAt).
        long from = state.Played;
        try { state.Consume(mono); }
        catch { mono.Clear(); }
        if (state.CabinLayout != null && state.ChannelClockKnown && DspCallback.Clock(ref dsp_state, out ulong clock))
            state.NoteBlock(clock, from);

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
