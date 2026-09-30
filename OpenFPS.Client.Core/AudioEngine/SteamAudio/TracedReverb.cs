using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The place you are standing in, measured: an impulse response traced through the map's real
/// geometry from the listener's own position, a few times a second, for everything outdoors to be
/// played through (TracedReverbDsp). Steam Audio's "listener-centric reverb".
///
/// Why it exists. Outdoors the reverberation used to be FMOD's room algorithm with its decay set from
/// a ray survey — a statistical ROOM tail, dense and smooth from the first milliseconds. A street is
/// not that: its tail is the flutter between two facades, scatter off windows and cars, and most of
/// the energy leaving through the open sky. Asked "why do we even need reverb at all if that should
/// fall out as a natural consequence of correct physics", the honest answer was that we did not need
/// the ALGORITHM — only something to stand for the thousands of copies of copies nobody can voice one
/// by one. This is that, measured: rays from the listener, bouncing off the scene's own materials
/// (absorption and scattering per band), collected back into a two-second first-order ambisonic IR.
///
/// One simulation for everything, because the tracing is the cost. The approximation is Steam
/// Audio's own: every sound is reverberated as if it were where the listener is. The late field of a
/// place barely depends on where in it the source stands; the early echoes do, and those are voiced
/// separately and exactly (EngineReflections, EarlyReflections' flutter).
/// </summary>
internal sealed class TracedReverb : IDisposable
{
    /// <summary>The one in use, for the provider's reverb buses to read. Null until the scene exists.</summary>
    public static volatile TracedReverb? Current;

    /// <summary>
    /// The block the traced response is convolved in, samples. The convolution answers one block
    /// late, so this IS the room's pre-delay: at the mixer's 1,024 the first reflection of every room,
    /// a hatchback's cabin included, came 20-23 ms after the sound, and a room answered as a separate
    /// space off to one side instead of the one you stand in (the parking garage, a clap in a house:
    /// "reflections centred not around me", 2026-09-28). At 256 it is 5-6 ms. The mixer's block is run
    /// through in pieces of this size (TracedReverbDsp).
    /// </summary>
    public const int TracedFrame = 256;

    public const float DurationSeconds = 2.0f;

    /// <summary>What the simulator produces: HYBRID gives the convolution IR AND the reverb times the
    /// parametric tail is built from (TracedReverbState.TailEffect); CONVOLUTION the IR alone.</summary>
    public static int SimulatedType = Phonon.IPL_REFLECTIONEFFECTTYPE_HYBRID;
    public const int Order = 2;
    public const int Channels = (Order + 1) * (Order + 1);
    /// <summary>Rays and bounces per trace. Sixty-four bounces, not sixteen: in a twelve-metre room
    /// sixteen bounces is a quarter of a second of travel, and the tail stopped dead at half a second
    /// (--traced-reverb) where a hard room rings on.</summary>
    private const int Rays = 8192, Bounces = 64;
    /// <summary>How often the trace is refreshed. Walking is a metre and a half a second; a quarter
    /// second is a third of a metre, and the IR crossfades inside the effect.</summary>
    private const int DefaultRefreshMs = 250;
    private readonly int _refreshMs;

    public IntPtr Context { get; }
    public int SampleRate { get; }
    public int FrameSize { get; }
    public int IrSize => (int)(DurationSeconds * SampleRate);

    private IntPtr _simulator, _source;
    /// <summary>
    /// One source per stage that plays this trace, all at the same point. A Steam Audio IR update is
    /// taken by the FIRST effect that reads it after the trace (measured, --traced-echoes: a second
    /// effect on the same IR stayed silent), and every outdoor bus played the listener's trace through
    /// its own effect — so one bus had the street and the others a stale IR or none, and a car's
    /// reverberation came and went as it crossed from one region's bus to another's: the reflections
    /// "cutting out". Each reader has its own source, and its own copy of every trace.
    /// </summary>
    public const int MaxReaders = 24;
    private readonly IntPtr[] _readers = new IntPtr[MaxReaders];
    private bool _readersDirty;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private Vector3 _listener;
    private bool _haveScene;

    /// <summary>Diagnostics: runs so far and the last run's cost.</summary>
    public int Runs;
    public double LastRunMs;

    /// <summary>
    /// Read the traced IR back after every trace and publish its late part (<see cref="Late"/>) for
    /// the tail of the place you stand in (LateTailIr). The SDK keeps the IR opaque, so an impulse is
    /// pushed through a private convolution on this thread, on a reader of its own
    /// (<see cref="ExtractReader"/>): a trace update goes to the first effect that reads it, and the
    /// mixer's stages must not lose theirs. Set before <see cref="SetScene"/>.
    /// </summary>
    public bool ExtractLate;
    /// <summary>The latest trace's late part, time zero at the direct sound. Null until the first.</summary>
    public volatile LateTailIr? Late;
    /// <summary>The last trace's omnidirectional channel as read back, whole and unwindowed: for the lab.</summary>
    public volatile float[]? LastReadBack;
    /// <summary>What reading it back cost, last time.</summary>
    public double LastExtractMs;
    /// <summary>The reader the extraction uses, never a mixer stage's.</summary>
    public const int ExtractReader = MaxReaders - 1;
    /// <summary>Most partitions a late part may have: the whole trace in blocks of the traced frame.</summary>
    public int MaxLatePartitions => IrSize / FrameSize + 1;
    private IntPtr _extractEffect;
    private Phonon.IPLAudioBuffer _extractIn, _extractOut;
    private float[] _extractMono = Array.Empty<float>(), _extractInter = Array.Empty<float>();

    /// <param name="refreshMs">How often the trace is redone: a quarter second for the listener,
    /// who walks; a second for a room traced from its middle, which does not move.</param>
    public TracedReverb(IntPtr context, int sampleRate = 44100, int frameSize = TracedFrame, int refreshMs = DefaultRefreshMs)
    {
        _refreshMs = refreshMs;
        Context = context; SampleRate = sampleRate; FrameSize = frameSize;
        var s = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS,
            sceneType = Phonon.IPL_SCENETYPE_DEFAULT,
            reflectionType = SimulatedType,
            maxNumOcclusionSamples = 16, maxNumRays = Rays, numDiffuseSamples = 32,
            maxDuration = DurationSeconds, maxOrder = Order, maxNumSources = MaxReaders, numThreads = 2,
            rayBatchSize = 16, numVisSamples = 4, samplingRate = sampleRate, frameSize = frameSize,
        };
        if (Phonon.iplSimulatorCreate(context, ref s, out _simulator) != Phonon.IPL_STATUS_SUCCESS)
            _simulator = IntPtr.Zero;
    }

    public bool IsValid => _simulator != IntPtr.Zero;

    /// <summary>The source whose outputs the effect reads. Mixer thread reads it.</summary>
    public IntPtr Source => _source;

    /// <summary>Points the tracer at a (built) scene, and starts tracing.</summary>
    public void SetScene(SteamAudioScene scene)
    {
        if (!IsValid || !scene.IsBuilt) return;
        lock (_gate)
        {
            Phonon.iplSimulatorSetScene(_simulator, scene.Handle);
            Phonon.iplSimulatorCommit(_simulator);
            if (_source == IntPtr.Zero)
            {
                var ss = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
                if (Phonon.iplSourceCreate(_simulator, ref ss, out _source) == Phonon.IPL_STATUS_SUCCESS)
                {
                    Phonon.iplSourceAdd(_source, _simulator);
                    Phonon.iplSimulatorCommit(_simulator);
                    _readers[0] = _source;
                }
            }
            _haveScene = _source != IntPtr.Zero;
            if (_haveScene && ExtractLate && _readers[ExtractReader] == IntPtr.Zero)
            {
                var es = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
                if (Phonon.iplSourceCreate(_simulator, ref es, out IntPtr xs) == Phonon.IPL_STATUS_SUCCESS)
                {
                    Phonon.iplSourceAdd(xs, _simulator);
                    Phonon.iplSimulatorCommit(_simulator);
                    _readers[ExtractReader] = xs;
                }
            }
        }
        if (_haveScene && _thread == null)
        {
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TracedReverb", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
        Current = this;
    }

    /// <summary>Makes sure reader <paramref name="index"/> has its own source. Game thread; cheap
    /// once it exists. The new source is committed before the next trace.</summary>
    public void EnsureReader(int index)
    {
        if (index <= 0 || index >= MaxReaders || _readers[index] != IntPtr.Zero || !IsValid) return;
        lock (_gate)
        {
            if (_readers[index] != IntPtr.Zero || !_haveScene) return;
            var ss = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS };
            if (Phonon.iplSourceCreate(_simulator, ref ss, out IntPtr src) != Phonon.IPL_STATUS_SUCCESS) return;
            Phonon.iplSourceAdd(src, _simulator);
            _readersDirty = true;
            _readers[index] = src;
        }
    }

    /// <summary>Where the listener is. Game or worker thread.
    ///
    /// Under its OWN lock, never the tracer's: the trace holds that one for the whole run, and in a
    /// big hard hall a run takes hundreds of milliseconds. The game thread calls this every frame,
    /// so it waited out each trace — in the airport terminal every sound stood still for 680 ms at a
    /// time, the game loop ran at 8 Hz, footsteps and claps came late or not at all and the reverb
    /// stepped ("fluttered") (2026-09-29).</summary>
    public void SetListener(Vector3 at) { lock (_listenerGate) _listener = at; }
    private readonly object _listenerGate = new();

    private void Loop()
    {
        while (_running)
        {
            try
            {
                Vector3 at;
                lock (_listenerGate) at = _listener;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    if (!_haveScene) { Thread.Sleep(_refreshMs); continue; }
                    var coord = Coord(at);
                    var shared = new Phonon.IPLSimulationSharedInputs
                    {
                        listener = coord, numRays = Rays, numBounces = Bounces,
                        duration = DurationSeconds, order = Order, irradianceMinDistance = 1.0f,
                    };
                    var inputs = new Phonon.IPLSimulationInputs
                    {
                        flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS,
                        source = coord,
                        reverbScale0 = 1f, reverbScale1 = 1f, reverbScale2 = 1f,
                        hybridReverbTransitionTime = DurationSeconds, hybridReverbOverlapPercent = 0.25f,
                    };
                    if (_readersDirty) { Phonon.iplSimulatorCommit(_simulator); _readersDirty = false; }
                    foreach (var r in _readers)
                        if (r != IntPtr.Zero) Phonon.iplSourceSetInputs(r, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref inputs);
                    Phonon.iplSimulatorSetSharedInputs(_simulator, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref shared);
                    Phonon.iplSimulatorRunReflections(_simulator);
                }
                LastRunMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Runs++;
                if (ExtractLate)
                {
                    long x0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (ReadBack() is { } w) { LastReadBack = w; Late = LateTailIr.Build(w, SampleRate, FrameSize, MaxLatePartitions); }
                    LastExtractMs = (System.Diagnostics.Stopwatch.GetTimestamp() - x0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                }
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Traced reverb: a trace failed."); }
            Thread.Sleep(_refreshMs);
        }
    }

    /// <summary>The latest traced IR, for the effect. Mixer thread; Steam Audio double-buffers it.</summary>
    public bool TryGetParams(out Phonon.IPLReflectionEffectParams p) => TryGetParams(0, out p);

    /// <summary>The latest traced IR as reader <paramref name="reader"/> gets it. Each stage reads its
    /// own; see <see cref="MaxReaders"/>.</summary>
    public bool TryGetParams(int reader, out Phonon.IPLReflectionEffectParams p)
    {
        p = default;
        IntPtr src = reader >= 0 && reader < MaxReaders ? _readers[reader] : IntPtr.Zero;
        if (src == IntPtr.Zero) return false;
        var outs = new Phonon.IPLSimulationOutputs();
        Phonon.iplSourceGetOutputs(src, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS, ref outs);
        p = outs.reflections;
        if (p.ir == IntPtr.Zero) return false;
        p.type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION;
        p.numChannels = Channels;
        p.irSize = IrSize;
        return true;
    }

    /// <summary>
    /// The latest trace's omnidirectional channel, read back through a private convolution: warmed
    /// on silence (an effect crossfades a new IR in over its first block), then one impulse. Tracer
    /// thread only. Null if there is nothing to read yet.
    /// </summary>
    private float[]? ReadBack()
    {
        if (!TryGetParams(ExtractReader, out var prm)) return null;
        int frame = FrameSize, ch = Channels, frames = IrSize / frame;
        if (_extractEffect == IntPtr.Zero)
        {
            var au = new Phonon.IPLAudioSettings { samplingRate = SampleRate, frameSize = frame };
            var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = IrSize, numChannels = ch };
            if (Phonon.iplReflectionEffectCreate(Context, ref au, ref es, out _extractEffect) != Phonon.IPL_STATUS_SUCCESS)
            { _extractEffect = IntPtr.Zero; return null; }
            Phonon.iplAudioBufferAllocate(Context, 1, frame, ref _extractIn);
            Phonon.iplAudioBufferAllocate(Context, ch, frame, ref _extractOut);
            _extractMono = new float[frame];
            _extractInter = new float[frame * ch];
        }
        Phonon.iplReflectionEffectReset(_extractEffect);
        var w = new float[frames * frame];
        for (int b = -4; b < frames; b++)
        {
            Array.Clear(_extractMono);
            if (b == 0) _extractMono[0] = 1f;
            Phonon.iplAudioBufferDeinterleave(Context, _extractMono, ref _extractIn);
            Phonon.iplReflectionEffectApply(_extractEffect, ref prm, ref _extractIn, ref _extractOut, IntPtr.Zero);
            if (b < 0) continue;
            Phonon.iplAudioBufferInterleave(Context, ref _extractOut, _extractInter);
            for (int k = 0; k < frame; k++) w[b * frame + k] = _extractInter[k * ch];
        }
        return w;
    }

    private static Phonon.IPLCoordinateSpace3 Coord(Vector3 origin) => new()
    {
        right = new Phonon.IPLVector3 { x = 1, y = 0, z = 0 },
        up = new Phonon.IPLVector3 { x = 0, y = 1, z = 0 },
        ahead = new Phonon.IPLVector3 { x = 0, y = 0, z = -1 },
        origin = Phonon.World(origin),
    };

    public void Dispose()
    {
        _running = false;
        _thread?.Join(2000);
        if (ReferenceEquals(Current, this)) Current = null;
        lock (_gate)
        {
            for (int i = 1; i < MaxReaders; i++)
                if (_readers[i] != IntPtr.Zero) { Phonon.iplSourceRelease(ref _readers[i]); _readers[i] = IntPtr.Zero; }
            _readers[0] = IntPtr.Zero;
            if (_source != IntPtr.Zero) Phonon.iplSourceRelease(ref _source);
            if (_extractEffect != IntPtr.Zero)
            {
                Phonon.iplReflectionEffectRelease(ref _extractEffect);
                Phonon.iplAudioBufferFree(Context, ref _extractIn);
                Phonon.iplAudioBufferFree(Context, ref _extractOut);
            }
            if (_simulator != IntPtr.Zero) Phonon.iplSimulatorRelease(ref _simulator);
        }
    }
}

/// <summary>
/// Every traced place in use: the listener's own (traced from where they stand, following them) and
/// each other room that can be heard (traced from its own middle, so a sound through a doorway rings
/// with the room it is in, not the one you are in). Built on the acoustic worker's context and scene;
/// asked for by the provider's reverb buses.
/// </summary>
internal static class TracedReverbSet
{
    private static readonly object Gate = new();
    private static IntPtr _context;
    private static SteamAudioScene? _scene;
    private static TracedReverb? _listener;
    private static TracedEchoes? _echoes;
    private static LateField? _late;
    private static readonly Dictionary<int, (TracedReverb Trace, Vector3 At)> Rooms = new();
    /// <summary>At most this many rooms traced besides the listener's; the mixer only ever hears four.</summary>
    private const int MaxRooms = 6;

    /// <summary>The worker, once its scene is built (and again after every rebuild).</summary>
    /// <param name="listenerScene">The scene the listener's own trace uses: the same geometry without
    /// its open ground (SteamAudioScene.WithoutOpenGround). Null uses <paramref name="scene"/>.</param>
    public static void Configure(IntPtr context, SteamAudioScene scene, SteamAudioScene? listenerScene = null)
    {
        lock (Gate)
        {
            _context = context; _scene = scene;
            _listener ??= new TracedReverb(context) { ExtractLate = true };
            if (_listener.IsValid) _listener.SetScene(listenerScene ?? scene);
            // The few sources traced from where they are (TracedEchoes), on the scene WITHOUT its open
            // ground, as the listener's trace is. Every voice already carries its own ground bounce
            // (GroundReflection); traced over the ground as well, a car at 30 m had that bounce twice,
            // the second at about the direct level and under a millisecond late — a comb that took
            // twenty decibels of trim to hide (the first "-24", 2026-09-26; found 2026-09-29).
            _echoes ??= new TracedEchoes(context);
            if (_echoes.IsValid) _echoes.SetScene(listenerScene ?? scene);
            foreach (var r in Rooms.Values) r.Trace.SetScene(scene);
            // Each source's own late energy and its direction, on the listener's scene (LateField).
            _late ??= new LateField(context);
            if (_late.IsValid) _late.SetScene(listenerScene ?? scene);
        }
    }

    public static void SetListener(Vector3 at) { lock (Gate) _listener?.SetListener(at); }

    /// <summary>The per-source tracer, or null before the scene exists.</summary>
    public static TracedEchoes? Echoes { get { lock (Gate) return _echoes is { IsValid: true } e ? e : null; } }

    /// <summary>Each source's late energy and direction, traced from where it is. Null before the scene.</summary>
    public static LateField? LateField { get { lock (Gate) return _late is { IsValid: true } l ? l : null; } }

    /// <summary>The listener's own trace, or null before the scene exists.</summary>
    public static TracedReverb? Listener { get { lock (Gate) return _listener is { IsValid: true } l && l.Source != IntPtr.Zero ? l : null; } }

    /// <summary>A room's trace from a point in it, made on first asking. Null before the scene exists
    /// or past the cap (then the room is played through the listener's trace).</summary>
    public static TracedReverb? ForRoom(int regionId, Vector3 at)
    {
        lock (Gate)
        {
            if (_scene == null || _context == IntPtr.Zero) return null;
            if (Rooms.TryGetValue(regionId, out var r))
            {
                if (Vector3.DistanceSquared(r.At, at) > 1f) { r.Trace.SetListener(at); Rooms[regionId] = (r.Trace, at); }
                return r.Trace;
            }
            if (Rooms.Count >= MaxRooms) return null;
            var t = new TracedReverb(_context, refreshMs: 1000);
            if (!t.IsValid) { t.Dispose(); return null; }
            t.SetScene(_scene);
            t.SetListener(at);
            Rooms[regionId] = (t, at);
            return t;
        }
    }

    // ── The vehicle you are riding in ────────────────────────────────────────────────────────
    //
    // "That means even inside vehicles like buses too, use reflections, not what we've been doing."
    // A vehicle moves, so it is not in the map's traced scene — traced from a bus seat, the world
    // scene answers with the street outside. But from inside, the cabin does not move relative to
    // you: it is traced as a scene of its own, in the vehicle's frame, from the same geometry the
    // server builds the shell from (VehicleCabin) — its floor, its steel below the waist and glass
    // above, its length.
    private static string? _cabinPreset;
    private static SteamAudioScene? _cabinScene;
    private static TracedReverb? _cabin;
    private static bool _riding;

    /// <summary>The cabin's trace while riding in something that has a cabin, else null.</summary>
    public static TracedReverb? Cabin { get { lock (Gate) return _riding && _cabin is { IsValid: true } c && c.Source != IntPtr.Zero ? c : null; } }

    /// <summary>Riding in <paramref name="preset"/> (null: on foot), with the ear at
    /// <paramref name="local"/> in the vehicle's own frame.</summary>
    public static void RideIn(string? preset, Vector3 local)
    {
        lock (Gate)
        {
            if (preset == null || _context == IntPtr.Zero || !MachineRegistry.Knows(preset)) { _riding = false; return; }
            if (!string.Equals(preset, _cabinPreset, StringComparison.OrdinalIgnoreCase))
            {
                _cabin?.Dispose(); _cabin = null;
                _cabinScene?.Dispose(); _cabinScene = null;
                _cabinPreset = preset;
                var v = MachineRegistry.VehicleFor(preset);
                if (VehicleCabin.Measure(v) is { } g)
                {
                    var boxes = new List<SteamAudioScene.Box>();
                    foreach (var (prefab, at, size) in VehicleCabin.Shell(v, g))
                        boxes.Add(new SteamAudioScene.Box(at, size, Quaternion.Identity, VehicleCabin.MaterialOf(prefab)));
                    _cabinScene = new SteamAudioScene(_context);
                    _cabinScene.Build(boxes);
                    if (_cabinScene.IsBuilt)
                    {
                        _cabin = new TracedReverb(_context, refreshMs: 1000);
                        if (_cabin.IsValid) _cabin.SetScene(_cabinScene); else { _cabin.Dispose(); _cabin = null; }
                    }
                }
            }
            _cabin?.SetListener(local);
            _riding = _cabin != null;
        }
    }

    /// <summary>Everything traced so far, for the /reverb readout.</summary>
    public static (int Rooms, int Runs, double LastMs) Stats()
    {
        lock (Gate)
        {
            int runs = _listener?.Runs ?? 0;
            foreach (var r in Rooms.Values) runs += r.Trace.Runs;
            return (Rooms.Count, runs, _listener?.LastRunMs ?? 0);
        }
    }

    public static void Dispose()
    {
        lock (Gate)
        {
            _listener?.Dispose(); _listener = null;
            _echoes?.Dispose(); _echoes = null;
            _late?.Dispose(); _late = null;
            _cabin?.Dispose(); _cabin = null; _cabinScene?.Dispose(); _cabinScene = null; _cabinPreset = null; _riding = false;
            foreach (var r in Rooms.Values) r.Trace.Dispose();
            Rooms.Clear();
            _scene = null; _context = IntPtr.Zero;
        }
    }
}
