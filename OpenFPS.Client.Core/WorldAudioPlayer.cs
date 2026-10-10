using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// Plays the one-off sounds the world reports (WorldAudioEvent): knocks, rings, hisses, scrapes and
/// the named models (shots, doors, speech, thunder). Render once, play by name: a sound's parameters
/// are its id, so it is synthesised the first time and found in the cache after. Then it is just a
/// sound, through the ordinary emitter path: a rendered latch behind a wall is muffled by the same
/// code that muffles a recorded one. Why the echoes are as they are: docs/CLIENT_NOTES.md, "One-off
/// sounds: why the echoes are as they are".
/// </summary>
public sealed class WorldAudioPlayer
{
    /// <summary>A sound waiting for its moment. A latch precedes its own impact by twenty
    /// milliseconds; a shard lands a second and a half after the pane broke.</summary>
    private readonly struct Pending
    {
        public required TransientSound Sound { get; init; }
        public required string SoundId { get; init; }
        public required int SourceEntityId { get; init; }
        public required double DueAt { get; init; }
        /// <summary>An echo of another. It gets no echoes of its own: reflecting reflections is a
        /// second-order model without the geometry checks that go with one.</summary>
        public bool IsReflection { get; init; }
        /// <summary>The seed the sound was rendered from, so an echo can render its diffused copy.</summary>
        public int Seed { get; init; }
        /// <summary>What the surfaces an echo came off took from its bottom and its top, dB (zero or
        /// less; see ImageSource.SpecularBandLossDb). Zero for anything that is not an echo.</summary>
        public float EchoLowDb { get; init; }
        public float EchoHighDb { get; init; }
        /// <summary>An echo's level against its source, linear, and the source's own level. An echo is
        /// placed AS its source and then scaled by this, never by its own lowered level: see Play.</summary>
        public float CopyGain { get; init; }
        public float SourceLevelDb { get; init; }
        /// <summary>A part of a strike's thunder. It stands for kilometres of lightning channel in one
        /// direction, so it is placed <see cref="SkyOffset"/> from the listener wherever the listener
        /// is, and gets only a sky sound's copies (see <see cref="MaxSkyRoomEchoes"/>).</summary>
        public bool Sky { get; init; }
        public Vector3 SkyOffset { get; init; }
    }

    private readonly AudioEngineFacade _audio;
    private readonly SpatialAcoustics _acoustics;
    private readonly List<Pending> _pending = new();
    private readonly HashSet<string> _registered = new();

    /// <summary>
    /// Ids being synthesised on a worker. Never on the game thread: three hundred people clapping is
    /// 170 ms of render, and the thread holding the clock must not stop for it. The event that found a
    /// new sound waits for it, and is dropped only past <see cref="MaxRenderLateness"/>.
    /// </summary>
    private readonly HashSet<string> _rendering = new();

    /// <summary>Sounds heard for the first time, waiting for the worker to hand back their buffer.</summary>
    private readonly List<Pending> _awaitingRender = new();

    /// <summary>
    /// How late a first hearing may play once its buffer is back, seconds. A stand of three hundred
    /// people clapping takes about 170 ms to render; later than this it is a clap in the wrong place.
    /// </summary>
    internal const double MaxRenderLateness = 0.12;

    /// <summary>
    /// The same for a pane of glass, whose break and landing are always first hearings and take
    /// 30-150 ms to simulate (GlassFracture): at the clap's allowance a window shot out on a slow machine
    /// was silent. A crash a few hundred milliseconds late is still the crash.
    /// </summary>
    internal const double GlassRenderLateness = 0.4;

    private static double LatenessFor(in TransientSound sound)
        => sound.SynthKey != null && (sound.SynthKey.StartsWith(GlassFracture.KeyPrefix, StringComparison.Ordinal)
                                      || sound.SynthKey.StartsWith(StruckThings.KeyPrefix, StringComparison.Ordinal))
            ? GlassRenderLateness : MaxRenderLateness;
    /// <summary>Finished renders with the rate each carries, which is registered as it is: a door
    /// prewarmed before the mixer existed may not be at the mixer's rate.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Id, float[] Pcm, int Rate)> _rendered = new();

    /// <summary>
    /// A door model render's own peak, dB SPL at a metre, by key: what its full scale stands for, and
    /// the level it is placed at. The server's table figure is the model's median, a few decibels off a
    /// 1.4 m leaf or a worn character. Written on the render worker, read when the sound plays.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _fullScaleDb = new(StringComparer.Ordinal);

    // ── Thunder ─────────────────────────────────────────────────────────────────────────────────
    //
    // A strike arrives as one sound keyed by the whole flash (LightningStrike). Thunder.Render works it
    // out on a worker for where this listener stands, as a few parts, one per direction, each carrying
    // what the way did to it; each is placed SkyProxyMetres off in its direction, so the city round
    // the listener still blocks, bends and answers it.

    /// <summary>How far out a part of the thunder is placed, metres: the loudness law's largest
    /// reference distance, so its level at the ear is the peak it was rendered with. Nearer, the
    /// reference clamp would flatten strikes to one level (Loudness.Place).</summary>
    internal const float SkyProxyMetres = Loudness.MaxReferenceDistance;

    /// <summary>
    /// How many of the room's mirrors a part of the thunder gets (strongest first, no washes): a far
    /// source over a street is answered by the facade it shines on. Each copy is a voice for tens of
    /// seconds, so two, not a clap's twelve, and no flutter.
    /// </summary>
    internal const int MaxSkyRoomEchoes = 2;

    private readonly System.Collections.Concurrent.ConcurrentQueue<(LightningStrike Strike, double ReceivedAt, Vector3 Listener, List<Thunder.Part> Parts, long Ms, int Map)> _thunder = new();
    /// <summary>Raised by <see cref="Clear"/>: thunder rendered before it is not played.</summary>
    private int _mapGeneration;
    /// <summary>One-off buffers to let go of once they have played: each strike's thunder is rendered
    /// for one listener at one moment and never asked for again.</summary>
    private readonly List<(string Id, double At)> _releases = new();
    private Vector3 _lastListener;
    private Thunder.Air _air = Thunder.Air.Standard;
    private float _earAboveGround = 1.7f;
    private int _thunderCount;

    /// <summary>
    /// How far a transient carries at most: a bound on cost only, past anything meant to be heard,
    /// since the provider fades the last quarter of a range. At 250 m the speedway's crowd of four
    /// hundred, 118 dB at 219 m, was faded out. The level's audible range decides the rest.
    /// </summary>
    internal const float MaxRange = 3000f;

    /// <summary>The simulator's worker, for a one-shot's first answer. See
    /// <see cref="OpenFPS.Client.AudioEngine.Acoustics.AsyncAcousticWorker.TryGetNearby"/>.</summary>
    public OpenFPS.Client.AudioEngine.Acoustics.AsyncAcousticWorker? Worker { get; set; }

    /// <summary>
    /// The id the simulator files its answer for a sounding entity under (a talker, a one-shot's
    /// source): a question to the worker, never a voice, in a band below every voice band (the horns'
    /// -1,200,000 is the lowest). The per-frame loop skips transient ids, so without this a one-shot
    /// from a quiet place lived on the hand-rolled tracer (<see cref="AsyncAcousticWorker.TryGetNearby"/>).
    /// </summary>
    internal const int SourceProbeBase = -3_000_000;
    internal static int SourceProbeId(int entityId) => SourceProbeBase - entityId;

    /// <summary>Asks the simulator about a sounding entity, at the point the sound leaves it.</summary>
    private void AskAbout(int entityId, Vector3 listener, Vector3 at)
        => Worker?.EnqueueRequest(new AcousticRequest
        {
            EntityId = SourceProbeId(entityId), ListenerPos = listener, SourcePos = at,
            SourceRadius = AudioEmission.MinOcclusionRadius,
        });

    /// <summary>The simulator's answer for a sounding entity, if it has one for about this point,
    /// moved to it.</summary>
    private bool TryAsked(int entityId, Vector3 at, out AcousticPathData path)
    {
        path = default;
        if (Worker == null || !Worker.TryGetResult(SourceProbeId(entityId), out var paths)) return false;
        foreach (var p in paths)
        {
            if (p.IsReflection) continue;
            if (Vector3.Distance(p.SourcePosition, at) > 1f) return false;
            path = p with { ApparentPosition = p.ApparentPosition + (at - p.SourcePosition), SourcePosition = at };
            return true;
        }
        return false;
    }

    /// <param name="audio">The engine the sounds are registered with and submitted to.</param>
    /// <param name="acoustics">Where a sound's path is worked out until the simulator answers.</param>
    /// <param name="prewarm">Render the city's doors and a strike of thunder in the background now
    /// (the game). The emitter-stream replay builds six systems a run and wants neither the load nor
    /// renders landing whenever their threads finish.</param>
    public WorldAudioPlayer(AudioEngineFacade audio, SpatialAcoustics acoustics, bool prewarm = true)
    {
        _audio = audio;
        _acoustics = acoustics;
        if (!prewarm) return;
        PrewarmDoors();
        PrewarmThunder();
    }

    /// <summary>
    /// A door model takes seconds of a core to render, far past <see cref="MaxRenderLateness"/>, so
    /// every render the city's doors ask for is made at start, four at a time, commonest first
    /// (<see cref="PrewarmKeys"/>), and kept on disk (<see cref="DoorRenderCache"/>): a published
    /// client ships them, and a client built here renders each once per build.
    /// </summary>
    private void PrewarmDoors()
    {
        var keys = new System.Collections.Concurrent.ConcurrentQueue<string>();
        foreach (string key in PrewarmKeys())
            if (_rendering.Add($"synth:{key}")) keys.Enqueue(key);

        // Threads of their own, never the pool: a pool thread that finished a render took the next from
        // its own queue first, so every other Task.Run (rain survey, route build, a first-heard sound)
        // waited for the doors. 71 s on twelve cores; a CI login past a 30-minute timeout (2026-10-07).
        for (int t = 0; t < PrewarmThreads; t++)
            new System.Threading.Thread(() =>
            {
                while (keys.TryDequeue(out var key))
                {
                    string id = $"synth:{key}";
                    try { _rendered.Enqueue(AtMixerRate(id, RenderDoorKey(key, _fullScaleDb))); }
                    // An exception here would end the process; a door that will not render is silent.
                    catch (Exception ex) { Serilog.Log.Warning(ex, "[DOOR] prewarm of {Key} failed", key); }
                }
            }) { IsBackground = true, Name = "Door prewarm" }.Start();
    }

    /// <summary>Door renders made at once while prewarming.</summary>
    private const int PrewarmThreads = 4;

    /// <summary>Sounds asked for and not yet handed to the mixer: the prewarm, until the first update.</summary>
    internal int RendersOutstanding => _rendering.Count;

    /// <summary>
    /// Every door model render the city's doors and the cars' windows ask for, commonest first: knob
    /// doors (pulled, pushed, closed) at the prefab's width and the city's 1.1 and 1.4 m, push-bar
    /// doors, sliders, the key in a glass front door, knob doors' gentle and hard closes (45 % of
    /// closes), car windows, and the glass doors last, the slowest at 10-40 s each. Without the pushes
    /// (2026-10-05) and the gentle and hard closes, the first doors Cody opened each session were
    /// silent. No lift door: no map has one yet.
    /// </summary>
    public static List<string> PrewarmKeys()
    {
        var keys = new List<string>();
        var widths = new[] { 1.1f, 1.4f, 0.9f };
        foreach (float width in widths)
            for (int v = 0; v < KnobDoor.Variants; v++)
            {
                keys.Add(KnobDoor.Key(false, KnobDoor.Construction.HollowCore, v, 0.9f, KnobDoor.Shut.Normal, width, 2.1f));
                keys.Add(KnobDoor.Key(false, KnobDoor.Construction.HollowCore, v, 0.9f, KnobDoor.Shut.Normal, width, 2.1f, push: true));
                keys.Add(KnobDoor.Key(true, KnobDoor.Construction.HollowCore, v, 0.9f, KnobDoor.Shut.Normal, width, 2.1f));
            }
        // The steel push-bar door at the widths the city builds it (1.1 m) and its prefab's (1.0 m): each
        // character's push, its pull from the lever side, and its closer's latch.
        foreach (float width in new[] { 1.1f, 1.0f })
            for (int v = 0; v < PushBarDoor.Variants; v++)
            {
                keys.Add(PushBarDoor.Key(false, v, 1.4f, width, 2.1f));
                keys.Add(PushBarDoor.Key(false, v, 1.4f, width, 2.1f, pull: true));
                keys.Add(PushBarDoor.Key(true, v, 1.4f, width, 2.1f));
            }
        // The sliding doors at the sizes the city builds them: a patio leaf 1.0 m wide slid in 1.4 s, an
        // automatic leaf 1.15 m wide at its controller's own times.
        foreach (bool closing in new[] { false, true })
            for (int v = 0; v < SlidingDoor.Variants; v++)
            {
                keys.Add(SlidingDoor.Key(SlidingDoor.Kind.Patio, closing, v, 1.4f, 1.0f, 2.1f));
                keys.Add(SlidingDoor.Key(SlidingDoor.Kind.Automatic, closing, v, SlidingDoor.AutomaticSeconds(1.15f, !closing), 1.15f, 2.1f));
            }
        for (int v = 0; v < LockCylinder.Variants; v++)
            keys.Add(LockCylinder.Key(LockCylinder.Host.AluminiumStile, v));
        foreach (var how in new[] { KnobDoor.Shut.Gentle, KnobDoor.Shut.Hard })
            foreach (float width in widths)
                for (int v = 0; v < KnobDoor.Variants; v++)
                    keys.Add(KnobDoor.Key(true, KnobDoor.Construction.HollowCore, v, 0.9f, how, width, 2.1f));
        // A car's windows, each character, every stroke the window command makes from a window at rest:
        // fully down and up, and to and from half way. A window turned round while it is moving starts
        // from a quarter that is rendered when first heard.
        for (int v = 0; v < CarWindow.Variants; v++)
            foreach (var (from, to) in new[] { (0f, 1f), (1f, 0f), (0f, 0.5f), (0.5f, 0f), (0.5f, 1f), (1f, 0.5f) })
                keys.Add(CarWindow.Key(v, from, to));
        // The glass front door (its bar, its key-side pull, its close) at the towers' 1.9 m (the city scales
        // the 1.0 m prefab; prewarmed at 1.0 m, no tower door ever matched) and the prefab's size, and the
        // glass pull door (pulled, pushed, its close) at its prefab's.
        foreach (float width in new[] { 1.9f, 1.0f })
            for (int v = 0; v < GlassDoor.Variants; v++)
            {
                keys.Add(GlassDoor.Key(GlassDoor.Kind.PushBar, false, GlassDoor.Opening.Key, GlassDoor.Glazing.Tempered, v, 1.1f, width, 2.1f));
                keys.Add(GlassDoor.Key(GlassDoor.Kind.PushBar, false, GlassDoor.Opening.Push, GlassDoor.Glazing.Tempered, v, 1.1f, width, 2.1f));
                keys.Add(GlassDoor.Key(GlassDoor.Kind.PushBar, true, GlassDoor.Opening.Pull, GlassDoor.Glazing.Tempered, v, 1.1f, width, 2.1f));
            }
        for (int v = 0; v < GlassDoor.Variants; v++)
        {
            keys.Add(GlassDoor.Key(GlassDoor.Kind.Pull, false, GlassDoor.Opening.Pull, GlassDoor.Glazing.Tempered, v, 1.0f, 1.0f, 2.1f));
            keys.Add(GlassDoor.Key(GlassDoor.Kind.Pull, false, GlassDoor.Opening.Push, GlassDoor.Glazing.Tempered, v, 1.0f, 1.0f, 2.1f));
            keys.Add(GlassDoor.Key(GlassDoor.Kind.Pull, true, GlassDoor.Opening.Pull, GlassDoor.Glazing.Tempered, v, 1.0f, 1.0f, 2.1f));
        }
        return keys;
    }

    /// <summary>A door model's sound, by its key's prefix (through the knob door's renderer a push bar's
    /// key gave sixteen samples of silence). The render's own peak goes into <paramref name="fullScaleDb"/>,
    /// the level the sound is placed at.</summary>
    internal static float[] RenderDoorKey(string key, System.Collections.Concurrent.ConcurrentDictionary<string, float> fullScaleDb)
    {
        // Rendered before, by this build: from disk, in milliseconds (DoorRenderCache).
        if (DoorRenderCache.TryLoad(key, out var kept, out float keptDb))
        {
            if (keptDb > 0f) fullScaleDb[key] = keptDb;
            return kept;
        }
        if (key.StartsWith(CarWindow.KeyPrefix, StringComparison.Ordinal))
        {
            var window = CarWindow.RenderKey(key, TransientSynth.SampleRate);
            DoorRenderCache.Store(key, window, 0f);
            return window;
        }
        float[] pcm = key.StartsWith(GlassFracture.KeyPrefix, StringComparison.Ordinal) ? GlassFracture.RenderKey(key, TransientSynth.SampleRate, out float db)
                    : key.StartsWith(GlassDoor.KeyPrefix, StringComparison.Ordinal) ? GlassDoor.RenderKey(key, TransientSynth.SampleRate, out db)
                    : key.StartsWith(LockCylinder.KeyPrefix, StringComparison.Ordinal) ? LockCylinder.RenderKey(key, TransientSynth.SampleRate, out db)
                    : key.StartsWith(ElevatorDoor.KeyPrefix, StringComparison.Ordinal) ? ElevatorDoor.RenderKey(key, TransientSynth.SampleRate, out db)
                    : key.StartsWith(PushBarDoor.KeyPrefix, StringComparison.Ordinal) ? PushBarDoor.RenderKey(key, TransientSynth.SampleRate, out db)
                    : key.StartsWith(SlidingDoor.KeyPrefix, StringComparison.Ordinal) ? SlidingDoor.RenderKey(key, TransientSynth.SampleRate, out db)
                    : KnobDoor.RenderKey(key, TransientSynth.SampleRate, out db);
        if (db > 0f) fullScaleDb[key] = db;
        DoorRenderCache.Store(key, pcm, db);
        return pcm;
    }

    /// <summary>A key one of the simulated models names (doors, the key in a lock, a lift door, a car window,
    /// breaking glass): rendered by <see cref="RenderDoorKey"/>, one buffer per key.</summary>
    internal static bool IsDoorModelKey(string? key)
        => key != null && (key.StartsWith(KnobDoor.KeyPrefix, StringComparison.Ordinal)
                           || key.StartsWith(PushBarDoor.KeyPrefix, StringComparison.Ordinal)
                           || key.StartsWith(SlidingDoor.KeyPrefix, StringComparison.Ordinal)
                           || key.StartsWith(GlassDoor.KeyPrefix, StringComparison.Ordinal)
                           || key.StartsWith(LockCylinder.KeyPrefix, StringComparison.Ordinal)
                           || key.StartsWith(ElevatorDoor.KeyPrefix, StringComparison.Ordinal)
                           || key.StartsWith(CarWindow.KeyPrefix, StringComparison.Ordinal)
                           || key.StartsWith(GlassFracture.KeyPrefix, StringComparison.Ordinal));

    /// <summary>A door model's sound as it plays: placed at its own render's full scale, where the client has
    /// rendered it, instead of the table figure the server sent for the key.</summary>
    internal static TransientSound AtOwnLevel(TransientSound sound, System.Collections.Concurrent.ConcurrentDictionary<string, float> fullScaleDb)
    {
        if (sound.SynthKey != null && fullScaleDb.TryGetValue(sound.SynthKey, out float db)) sound.LevelDb = db;
        return sound;
    }

    /// <summary>Sounds waiting to be heard, for the log.</summary>
    public int Pending_Count => _pending.Count;

    /// <summary>
    /// Traces every one-off sound, received and played, under OPENFPS_AUDIO_DEBUG=1 (what
    /// `run-gtk-client.sh capture` sets): the place to answer "what was that bang, and when".
    /// </summary>
    private static readonly bool _trace = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";

    /// <summary>
    /// A horn sounded on a vehicle (the vehicle, which horn, the rhythm), handed to whoever voices
    /// vehicles: it plays on the vehicle for as long as it is held. See <see cref="Honk"/>.
    /// </summary>
    public Action<int, string, float[]>? HornReceived { get; set; }

    /// <summary>
    /// A train's horn or whistle and bell (which train, "preset/train"; the rhythm; how long the bell
    /// rings), handed on: the train's synth plays it on its own outlets. See <see cref="TrainSignal"/>.
    /// </summary>
    public Action<string, float[], float>? TrainSignalReceived { get; set; }

    /// <summary>Everything the world reports, before it is played: the birds go quiet at a bang.</summary>
    public Action<WorldAudioEvent>? Received { get; set; }

    /// <summary>Takes an event off the wire: renders anything new, and queues every sound for its moment.</summary>

    public void Receive(WorldAudioEvent message, double now)
    {
        if (message.Sounds == null) return;
        Received?.Invoke(message);
        if (HornReceived != null && message.Sounds.Count == 1
            && Honk.TryParse(message.Sounds[0].SynthKey, out string horn, out float[] rhythm))
        {
            HornReceived(message.SourceEntityId, horn, rhythm);
            return;
        }
        if (TrainSignalReceived != null && message.Sounds.Count == 1
            && TrainSignal.TryParse(message.Sounds[0].SynthKey, out string train, out float[] warning, out float bell))
        {
            TrainSignalReceived(train, warning, bell);
            return;
        }
        if (_trace)
        {
            foreach (var s in message.Sounds)
                Serilog.Log.Information("[WAUDIO] recv '{Label}' from e{Src}: {Ch} {Hz:F0} Hz {Db:F0} dB "
                                      + "decay {Decay:F2}s delay {Delay:F2}s at {Pos} (queue {Q})",
                    message.Label, message.SourceEntityId, s.Character, s.Hz, s.LevelDb,
                    s.DecaySeconds, s.DelaySeconds, s.Position, _pending.Count);
        }
        foreach (var sound in message.Sounds)
        {
            if (LightningStrike.TryParseKey(sound.SynthKey, out var strike))
            {
                HearThunder(strike, now);
                continue;
            }
            string id = IdFor(sound, message.Seed);
            if (!_registered.Contains(id))
            {
                // Not heard before: synthesised on a worker (see _rendering).
                if (_rendering.Add(id))
                {
                    var toRender = sound;
                    int seed = message.Seed;
                    System.Threading.Tasks.Task.Run(() => _rendered.Enqueue(AtMixerRate(id, RenderOne(toRender, seed))));
                }
                // It waits for its buffer: dropping every first hearing would silence whatever is rare
                // (four seed variants, each a first hearing once).
                _awaitingRender.Add(new Pending
                {
                    Sound = sound,
                    SoundId = id,
                    SourceEntityId = message.SourceEntityId,
                    DueAt = now + Math.Max(0f, sound.DelaySeconds),
                    Seed = message.Seed,
                });
                continue;
            }
            _pending.Add(new Pending
            {
                Sound = sound,
                SoundId = id,
                SourceEntityId = message.SourceEntityId,
                DueAt = now + Math.Max(0f, sound.DelaySeconds),
                Seed = message.Seed,
            });
        }
    }

    /// <summary>
    /// A lightning flash: its thunder worked out on a worker for where the listener is now, and queued
    /// for its moment when it comes back (<see cref="QueueThunder"/>). The nearest thunder is heard a
    /// third of a second after the flash at 100 m, and the render takes about half that.
    /// </summary>
    private void HearThunder(LightningStrike strike, double now)
    {
        var listener = _lastListener;
        var air = _air;
        int map = _mapGeneration;
        var options = new Thunder.Options { EarAboveGround = _earAboveGround };
        System.Threading.Tasks.Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var parts = Thunder.Render(strike, listener, air, options);
                // At the mixer's rate, band-limited, here on the worker: from 24 kHz FMOD's resampler
                // left the rumble's images across the top octave. About 4 ms a second of thunder.
                int mix = MixerQuality.MixerRate;
                foreach (var part in parts)
                    if (part.SampleRate != mix)
                    {
                        part.Pressure = MixerQuality.Resample(part.Pressure, part.SampleRate, mix);
                        part.SampleRate = mix;
                    }
                _thunder.Enqueue((strike, now, listener, parts, sw.ElapsedMilliseconds, map));
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[THUNDER] could not render strike {Key}", strike.Key());
            }
        });
    }

    /// <summary>
    /// One far strike rendered at start and thrown away, so the first real one does not wait on the
    /// JIT: its first run took twice as long, and the nearest thunder is due a third of a second after
    /// the flash.
    /// </summary>
    private static void PrewarmThunder()
        => System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var strike = new LightningStrike(1, FlashKind.CloudToGround, new Vector3(3000f, 4000f, 0f), new Vector3(3000f, 0f, 0f), 2e5f, 2);
                Thunder.Render(strike, new Vector3(0f, 1.7f, 0f), Thunder.Air.Standard, new Thunder.Options { Threads = 1 });
            }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[THUNDER] prewarm failed"); }
        });

    /// <summary>Registers each part of a rendered strike and queues it, placed in its direction.</summary>
    private void QueueThunder(LightningStrike strike, double receivedAt, Vector3 listener, List<Thunder.Part> parts, long ms, double now)
    {
        int n = ++_thunderCount;
        var flat = new Vector2(strike.Centre.X - listener.X, strike.Centre.Z - listener.Z);
        Serilog.Log.Information("[THUNDER] {Kind} {Km:F1} km away, bearing {Bearing:F0}; {Parts} part(s), loudest {Db:F0} dB SPL peak, "
                              + "first heard {First:F1} s after the flash; rendered in {Ms} ms, queued {Late:F2} s after it arrived",
            strike.Kind == FlashKind.CloudToGround ? "ground flash" : "cloud flash", flat.Length() / 1000f,
            (MathF.Atan2(flat.X, flat.Y) * 180f / MathF.PI + 360f) % 360f, parts.Count,
            parts.Count > 0 ? parts[0].PeakDb : 0f, parts.Count > 0 ? parts.Min(p => p.StartSeconds) : 0f, ms, now - receivedAt);
        for (int k = 0; k < parts.Count; k++)
        {
            var part = parts[k];
            if (part.PeakPa <= 0f || part.Pressure.Length == 0) continue;
            // Full scale is the part's own peak: the level it is declared at.
            var pcm = new float[part.Pressure.Length];
            float g = 1f / part.PeakPa;
            for (int i = 0; i < pcm.Length; i++) pcm[i] = part.Pressure[i] * g;
            string id = $"synth:thunder:{strike.Seed}:{n}:{k}";
            // In float: a minute of rumble 40-60 dB under the crack, in sixteen bits, ends as the last
            // bit stepping on and off (round 1: "crackly and breaks up").
            if (!_audio.RegisterSynthesisedSoundFloat(id, pcm, part.SampleRate)) continue;
            _registered.Add(id);
            var offset = part.Direction * SkyProxyMetres;
            _pending.Add(new Pending
            {
                Sound = new TransientSound
                {
                    Character = SoundCharacter.Knock,
                    Position = listener + offset,
                    // Its peak at the ear, carried out to where it is placed.
                    LevelDb = SkyLevelDb(part.PeakDb),
                    DecaySeconds = part.Seconds,
                    Noisiness = 1f,
                },
                SoundId = id,
                SourceEntityId = -1,
                DueAt = receivedAt + part.StartSeconds,
                Seed = strike.Seed,
                Sky = true,
                SkyOffset = offset,
            });
            _releases.Add((id, receivedAt + part.StartSeconds + part.Seconds + 5.0));
        }
    }

    /// <summary>The level a part of the thunder is declared at, dB SPL at a metre: its peak at the ear
    /// carried out to <see cref="SkyProxyMetres"/>, where it is placed.</summary>
    internal static float SkyLevelDb(float peakDbAtEar) => peakDbAtEar + 20f * MathF.Log10(SkyProxyMetres);

    /// <summary>The vehicle the listener is sitting in, or -1. Its own sounds are not heard through its glass.</summary>
    public int ListenerVehicleId { get; set; } = -1;
    /// <summary>The listener's own entity, and where the client has its body now (feet, facing): a
    /// sound made on your own body is placed on you, not where the server last had you.</summary>
    public int SelfId { get; set; } = -1;
    public Func<(Vector3 Feet, Quaternion Rotation)>? Self { get; set; }

    /// <summary>
    /// Submits everything whose moment has come, through the ordinary acoustic path. The path ignores
    /// the source entity: a door's leaf sits where its own latch is, and every door was heard through a door.
    /// </summary>
    public void Update(WorldSnapshot world, Vector3 listenerPosition, double now,
                       EngineReflections? reflections = null)
    {
        // Workers' renders are registered here, on the thread that owns the engine.
        while (_rendered.TryDequeue(out var done))
        {
            // In float, at the mixer's rate: sixteen bits truncated every quiet end to its last bit and
            // clipped renders over full scale, and FMOD's resampler imaged the top octave into the band.
            if (_audio.RegisterSynthesisedSoundFloat(done.Id, done.Pcm, done.Rate))
            {
                _registered.Add(done.Id);
                _rendering.Remove(done.Id);
            }
            else
            {
                // The engine is not up yet: the next event asks again.
                _rendering.Remove(done.Id);
            }
        }

        // The listener and the air, for the next strike's thunder; and any thunder that has come back.
        _lastListener = listenerPosition;
        _air = new Thunder.Air(world.Temperature, world.Humidity, world.AirPressure, world.WindVelocity, world.WindGustiness);
        if (Self != null)
        {
            float ear = listenerPosition.Y - Self().Feet.Y;
            if (ear > 0.3f && ear < 3f) _earAboveGround = ear;
        }
        while (_thunder.TryDequeue(out var t))
            if (t.Map == _mapGeneration) QueueThunder(t.Strike, t.ReceivedAt, t.Listener, t.Parts, t.Ms, now);
        for (int i = _releases.Count - 1; i >= 0; i--)
        {
            if (now < _releases[i].At) continue;
            _audio.ReleaseSynthesisedSound(_releases[i].Id);
            _registered.Remove(_releases[i].Id);
            _releases.RemoveAt(i);
        }

        // First renders back in time join the queue; too late, they are dropped.
        for (int i = _awaitingRender.Count - 1; i >= 0; i--)
        {
            var item = _awaitingRender[i];
            bool ready = _registered.Contains(item.SoundId);
            bool late = now > item.DueAt + LatenessFor(item.Sound);
            if (!ready && !late && _rendering.Contains(item.SoundId)) continue;
            _awaitingRender.RemoveAt(i);
            if (ready && !late) _pending.Add(item);
        }

        FollowSpeakers(world, listenerPosition, now);
        if (_pending.Count == 0) return;

        // Several passes: a sound's reflections are queued while it plays, behind this loop, and a
        // frame later a reflection due 6-25 ms after a clap arrived at 45-65 ms, a cluster of slaps. In
        // the same update every copy starts from its source's moment.
        for (int pass = 0; pass < 3; pass++)
        {
        bool playedAny = false;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            if (now < item.DueAt) continue;
            playedAny = true;
            _pending.RemoveAt(i);
            // A door model plays at its own render's peak; a copy keeps the level its source gave it.
            if (!item.IsReflection) item = item with { Sound = AtOwnLevel(item.Sound, _fullScaleDb) };
            // A part of the thunder is out in its direction from wherever the listener is now.
            if (item.Sky)
            {
                var sky = item.Sound;
                sky.Position = listenerPosition + item.SkyOffset;
                item = item with { Sound = sky };
            }
            if (!item.IsReflection && item.Sound.OnBody && BodyNow(world, item.SourceEntityId, out var feet, out var facing))
            {
                var onBody = item.Sound;
                onBody.Position = feet + Vector3.Transform(onBody.BodyOffset, facing);
                item = item with { Sound = onBody };
            }
            // A door's sound comes off the leaf's face on this listener's side: on the leaf it was inside
            // the leaf and the jamb, and heard through both.
            var movingFrom = item.Sound.Position;
            if (!item.IsReflection && item.Sound.FaceNormal != Vector3.Zero)
            {
                var faced = item.Sound;
                faced.Position = TransientSound.FacingListener(faced.Position, faced.FaceNormal, listenerPosition);
                item = item with { Sound = faced };
            }

            if (_trace)
                Serilog.Log.Information("[WAUDIO] play {Id}{Echo} {Db:F0} dB at {Dist:F1} m, {Late:F2}s after it was due "
                                      + "({Q} still queued)",
                    item.SoundId, item.IsReflection ? " (echo)" : "", item.Sound.LevelDb,
                    Vector3.Distance(listenerPosition, item.Sound.Position), now - item.DueAt, _pending.Count);

            // Speech gets no echo copies: each was the whole line from a fixed mirror while the speaker
            // walked on, heard as a room passing you. The reverb answers a voice.
            bool spoken = Speech.TryParseKey(item.Sound.SynthKey, out _);
            // Made inside a cabin (a window's motor): it goes with the vehicle, is heard outside through
            // the cabin's walls, and gets no copies off the street's walls. Not a driver's yell, said
            // out of the window.
            bool inCabin = InCabin(world, item.SourceEntityId, item.Sound.Position, out var cabinCar, out var cabinVehicle)
                           && !item.IsReflection && !spoken;
            if (item.Sky)
            {
                QueueEarlyEchoes(item, world, listenerPosition, MaxSkyRoomEchoes, washes: false);
                QueueReflections(item, reflections, listenerPosition, now, diffuse: false);
            }
            else if (!item.IsReflection && !spoken && !inCabin)
            {
                QueueEarlyEchoes(item, world, listenerPosition);
                QueueReflections(item, reflections, listenerPosition, now);
                QueueHigherOrderEchoes(item, world, listenerPosition);
            }

            var path = _acoustics.CalculateAcousticPath(world, item.SourceEntityId,
                                                        listenerPosition, item.Sound.Position);
            // An echo's image is behind its wall, so traced like a direct sound it came out 40-60 dB down.
            // Both legs were checked clear when it was found; like the engines' echoes
            // (EngineReflections.ApplyPath) it keeps only its surfaces' and the air's loss.
            if (item.IsReflection)
                path = path with
                {
                    Occlusion = 0f, EqMid = 1f,
                    EqLow = MathF.Pow(10f, item.EchoLowDb / 20f), EqHigh = MathF.Pow(10f, item.EchoHighDb / 20f),
                    ApertureFactor = 1f, TransmissionBleed = 0f, ApparentPosition = item.Sound.Position,
                };
            // Start on the simulator's answer for the source, or for the nearest source it heard a
            // moment ago, moved here; the hand-rolled tracer only without one.
            else if (item.SourceEntityId >= 0 && TryAsked(item.SourceEntityId, item.Sound.Position, out var asked))
            {
                path = path with
                {
                    Occlusion = asked.Occlusion, EqLow = asked.EqLow, EqMid = asked.EqMid, EqHigh = asked.EqHigh,
                    TransmissionBleed = asked.TransmissionBleed, ApertureFactor = asked.ApertureFactor,
                    ApparentPosition = asked.ApparentPosition, EffectiveDistance = asked.EffectiveDistance,
                };
            }
            else if (Worker != null && Worker.TryGetNearby(listenerPosition, item.Sound.Position, out var near))
            {
                Vector3 moved = item.Sound.Position - near.SourcePosition;
                float nearDist = MathF.Max(0.1f, Vector3.Distance(listenerPosition, near.SourcePosition));
                float dist = Vector3.Distance(listenerPosition, item.Sound.Position);
                path = path with
                {
                    Occlusion = near.Occlusion, EqLow = near.EqLow, EqMid = near.EqMid, EqHigh = near.EqHigh,
                    TransmissionBleed = near.TransmissionBleed, ApertureFactor = near.ApertureFactor,
                    ApparentPosition = near.ApparentPosition + moved,
                    EffectiveDistance = near.EffectiveDistance * dist / nearDist,
                };
            }
            // Ask about the source, so the next sound from there starts on the simulator's answer.
            // Placed at its own size if it has one: inside a grandstand's eight metres the level is flat,
            // beyond it it falls as a point source of the same power would.
            if (!item.IsReflection && item.SourceEntityId >= 0)
                AskAbout(item.SourceEntityId, listenerPosition, item.Sound.Position);
            var placed = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres);
            // A copy keeps its source's placement, scaled by what it kept: placed on its own, the law's
            // compression (0.45 shipped) brought an echo 14 dB down to 6 dB down, 4-8 dB too loud.
            if (item.IsReflection && item.CopyGain > 0f)
            {
                var source = Loudness.Place(item.SourceLevelDb, item.Sound.ExtentMetres);
                placed = (source.Gain * item.CopyGain, source.ReferenceDistance);
            }

            var emitter = new SpatialEmitter
            {
                // A voice of its own every time: the VoiceManager keys a submission by id, and a sound and
                // its reflection under one id replaced each other (a grandstand heard only as its echo).
                EntityId = NextVoiceId(),
                SoundId = item.SoundId,
                // Nothing here loops: a looping latch would be a fire alarm.
                Mode = OpenFPS.Common.Components.PlaybackMode.Single,
                Position = item.Sound.Position,
                ApparentPosition = path.ApparentPosition,
                EffectiveDistance = path.EffectiveDistance,
                Occlusion = path.Occlusion,
                EqLow = path.EqLow, EqMid = path.EqMid, EqHigh = path.EqHigh,
                AirLowDb = path.AirLowDb, AirMidDb = path.AirMidDb, AirHighDb = path.AirHighDb,
                ApertureFactor = path.ApertureFactor,
                TransmissionBleed = path.TransmissionBleed,
                TargetRegionId = path.RegionId,
                // Gain and reference distance come together from Loudness.Place: a quiet source wants a
                // short reference, a gunshot a long one.
                Volume = placed.Gain,
                Range = MathF.Min(MaxRange, Loudness.AudibleRange(item.Sound.LevelDb)),
                MinDistance = placed.ReferenceDistance,
                Pitch = 1.0f,
                Type = EmitterType.WorldLocked,
                // The budget ranks by delivered level, so a reflection gives way because it is quieter.
                // A reflection never sends to the reverb (the provider skips them): it is already the
                // place answering. An event: with no room in the budget now it is dropped, never played
                // later from a stale position (VoiceManager.Process).
                IsEvent = true,
                LevelDb = item.IsReflection ? 0f : item.Sound.LevelDb,
                // For the ear model: the level it was placed by, and a copy's place under its source.
                EarLevelDb = item.IsReflection && item.CopyGain > 0f ? item.SourceLevelDb : item.Sound.LevelDb,
                EarCopyDb = item.IsReflection && item.CopyGain > 0f ? 20f * MathF.Log10(item.CopyGain) : 0f,
                InsideListenersVehicle = item.SourceEntityId >= 0 && item.SourceEntityId == ListenerVehicleId,
            };
            // Somebody talking faces a way: duller and quieter behind them.
            bool follow = spoken && !item.IsReflection && item.SourceEntityId >= 0
                          && world.Entities.TryGetValue(item.SourceEntityId, out _);
            if (follow)
            {
                var speakerNow = world.Entities[item.SourceEntityId];
                emitter.CarriesPath = true;
                Facing(ref emitter, speakerNow.Transform.Rotation, listenerPosition);
            }
            if (inCabin)
            {
                emitter.CarriesPath = true;
                ThroughCabin(ref emitter, cabinCar, cabinVehicle, now);
            }
            // Thunder carries its own ground reflection, worked out from where each bit of channel is.
            if (!inCabin && !item.Sky && HearsTheGround(item, spoken)) Ground?.Invoke(ref emitter, world);
            _audio.Submit(emitter);

            // A part of the thunder stays in its direction while the listener walks on under it.
            if (item.Sky)
                _following.Add(new Following
                {
                    Emitter = emitter,
                    SourceEntityId = -1,
                    Sky = true,
                    Offset = item.SkyOffset,
                    Until = now + Math.Max(0f, item.Sound.DecaySeconds - 0.1f),
                    StartedAt = now,
                });

            // A sound in a cabin rides with it, turning as it turns: a window's motor is in its door.
            if (inCabin)
                _following.Add(new Following
                {
                    Emitter = emitter,
                    SourceEntityId = item.SourceEntityId,
                    Offset = Vector3.Transform(item.Sound.Position - cabinCar.Transform.Position,
                                               Quaternion.Inverse(cabinCar.Transform.Rotation)),
                    InCabin = true,
                    Until = now + Math.Max(0f, item.Sound.DecaySeconds - 0.1f),
                    StartedAt = now,
                });

            // A sliding door's run comes from its handle, which crosses the doorway while the run plays.
            if (!item.IsReflection && item.Sound.MoveSeconds > 0f && !follow && !inCabin)
                _following.Add(new Following
                {
                    Emitter = emitter,
                    SourceEntityId = item.SourceEntityId,
                    Moving = true,
                    From = movingFrom,
                    To = item.Sound.MovesTo,
                    MoveSeconds = item.Sound.MoveSeconds,
                    Face = item.Sound.FaceNormal,
                    Until = now + Math.Max(0f, item.Sound.DecaySeconds - 0.1f),
                    StartedAt = now,
                });

            // A talker carries the voice with them: a two-second line left where it started is three
            // metres behind by its end. A line placed on the body (a mouth, a driver's window) stays
            // there, turning with them; taken from where the server had the speaker, the offset pointed
            // back along their way and a driver's yell trailed the car (Sean, 2026-10-04).
            if (follow && world.Entities.TryGetValue(item.SourceEntityId, out var speaker))
                _following.Add(new Following
                {
                    Emitter = emitter,
                    SourceEntityId = item.SourceEntityId,
                    BodyFrame = item.Sound.OnBody,
                    Offset = item.Sound.OnBody ? item.Sound.BodyOffset : item.Sound.Position - speaker.Transform.Position,
                    // A submission after the voice has finished would start it again.
                    Until = now + Math.Max(0f, item.Sound.DecaySeconds - 0.1f),
                    StartedAt = now,
                });
        }
        if (!playedAny) break;
        bool dueLeft = false;
        foreach (var p in _pending) if (now >= p.DueAt) { dueLeft = true; break; }
        if (!dueLeft) break;
        }
    }

    /// <summary>The ground between a sound and the listener, worked out on the game thread (see
    /// ClientAudioSystem.ApplyRecordedGround).</summary>
    public delegate void GroundHandler(ref SpatialEmitter e, WorldSnapshot world);

    /// <summary>Sets a recorded sound's ground reflection. Null leaves every sound without one.</summary>
    public GroundHandler? Ground;

    /// <summary>A rotation's turn about the vertical alone: which way a body faces, not how it leans.</summary>
    private static Quaternion Yaw(Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        forward.Y = 0f;
        return forward.LengthSquared() > 1e-6f
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(forward.X, forward.Z))
            : Quaternion.Identity;
    }

    /// <summary>Where a body is now and which way it faces (yaw only): yours from the client's own
    /// prediction, anyone else's from the world as you hear it.</summary>
    private bool BodyNow(WorldSnapshot world, int entityId, out Vector3 feet, out Quaternion facing)
    {
        feet = default; facing = Quaternion.Identity;
        Quaternion rotation;
        if (entityId == SelfId && Self != null) (feet, rotation) = Self();
        else if (world.Entities.TryGetValue(entityId, out var e)) { feet = e.Transform.Position; rotation = e.Transform.Rotation; }
        else return false;
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        forward.Y = 0f;
        if (forward.LengthSquared() > 1e-6f)
            facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(forward.X, forward.Z));
        return true;
    }

    /// <summary>
    /// Whether a sound gets a ground reflection of its own: impulses only (a shot, a door, a knock), whose
    /// bounce lands inside the attack; on anything that lasts it is a comb that stands still. Not an echo
    /// copy, not a source with a size (its bounces are spread out), and not speech: a voice 3 m off on
    /// asphalt has a bounce 4 ms late at two thirds of its pressure (Acta Acustica 2024,
    /// doi 10.1051/aacus/2024002), and rendered it flanges. Something the ear uses is missing; until it
    /// is measured a voice has none. See docs/CLIENT_NOTES.md, "Speech has no ground reflection".
    /// </summary>
    private static bool HearsTheGround(in Pending item, bool spoken)
        => !spoken && IsImpulse(item.Sound) && !item.IsReflection && item.Sound.ExtentMetres <= 1f;

    /// <summary>Folds which way the speaker faces into the path's band gains.</summary>
    private static void Facing(ref SpatialEmitter e, Quaternion rotation, Vector3 listenerPosition)
    {
        var (low, mid, high) = Speech.Directivity(Vector3.Transform(Vector3.UnitZ, rotation), listenerPosition - e.Position);
        e.EqLow *= low; e.EqMid *= mid; e.EqHigh *= high;
    }

    /// <summary>A line being said by a body that is moving, and where its mouth is on that body; or a
    /// sound made inside a vehicle's cabin, and where in the vehicle's own frame.</summary>
    private struct Following
    {
        public SpatialEmitter Emitter;
        public int SourceEntityId;
        /// <summary>From the body, in the world's frame; for a sound in a cabin, in the vehicle's.</summary>
        public Vector3 Offset;
        public bool InCabin;
        /// <summary>The offset is in the body's own frame, turning only as it turns (yaw): a voice at a
        /// mouth, a driver's at the window.</summary>
        public bool BodyFrame;
        public double Until;
        public double StartedAt;
        /// <summary>Not on a body at all: a sound travelling from <see cref="From"/> to <see cref="To"/>
        /// over <see cref="MoveSeconds"/>, off a panel facing <see cref="Face"/> (a sliding door's run).</summary>
        public bool Moving;
        /// <summary>A part of the thunder: at <see cref="Offset"/> from the listener, wherever the listener is.</summary>
        public bool Sky;
        public Vector3 From, To, Face;
        public float MoveSeconds;
    }

    /// <summary>Every vehicle's windows as this client has them. Without it a cabin's windows are taken
    /// to be wherever the server last sent them.</summary>
    public OpenFPS.Client.AudioEngine.Acoustics.CabinWalls? Cabins { get; set; }

    /// <summary>Whether a sound was made inside the cabin of the vehicle that made it.</summary>
    private static bool InCabin(WorldSnapshot world, int sourceId, Vector3 at, out EntitySnapshot car, out VehicleProfile vehicle)
    {
        car = default; vehicle = null!;
        if (sourceId < 0 || !world.Entities.TryGetValue(sourceId, out car)) return false;
        if (OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Vehicle(car) is not { } v
            || !OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.HasCabin(v)) return false;
        vehicle = v;
        return OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Contains(car, v, at);
    }

    /// <summary>A sound inside a cabin, heard from outside it: through the glass, the seals and whatever
    /// windows are down. From inside the same vehicle, nothing is in the way.</summary>
    private void ThroughCabin(ref SpatialEmitter e, EntitySnapshot car, VehicleProfile vehicle, double now)
    {
        if (car.Id == ListenerVehicleId) { e.InsideListenersVehicle = true; return; }
        float open = Cabins?.WindowsOpen(car, now) ?? (car.Definition?.SoundEmitter.WindowsOpen ?? 0f);
        var (low, mid, high) = OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.LossDb(vehicle, open);
        e.EqLow *= OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Gain(low);
        e.EqMid *= OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Gain(mid);
        e.EqHigh *= OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Gain(high);
    }

    /// <summary>How long a voice has to start before following it stops. A line that lost the voice
    /// budget must not be submitted again: that would start it late, from the top.</summary>
    private const double StartGraceSeconds = 0.5;

    private readonly List<Following> _following = new();

    /// <summary>Moves every voice that is still talking to where its speaker is now.</summary>
    private void FollowSpeakers(WorldSnapshot world, Vector3 listenerPosition, double now)
    {
        for (int i = _following.Count - 1; i >= 0; i--)
        {
            var f = _following[i];
            EntitySnapshot speaker = default;
            if (now >= f.Until || (!f.Moving && !f.Sky && !world.Entities.TryGetValue(f.SourceEntityId, out speaker))
                || (now - f.StartedAt > StartGraceSeconds && !_audio.IsPlaying(f.Emitter.EntityId)))
            {
                _following.RemoveAt(i);
                continue;
            }
            var at = f.Sky ? listenerPosition + f.Offset
                   : f.Moving ? TransientSound.FacingListener(
                                    TransientSound.Along(f.From, f.To, f.MoveSeconds, (float)(now - f.StartedAt)), f.Face, listenerPosition)
                   : f.InCabin ? speaker.Transform.Position + Vector3.Transform(f.Offset, speaker.Transform.Rotation)
                   : f.BodyFrame ? speaker.Transform.Position + Vector3.Transform(f.Offset, Yaw(speaker.Transform.Rotation))
                   : speaker.Transform.Position + f.Offset;
            // The simulator's answer for the speaker, as for any other source; the hand-rolled tracer
            // only until it has one.
            if (f.SourceEntityId >= 0) AskAbout(f.SourceEntityId, listenerPosition, at);
            if (f.SourceEntityId < 0 || !TryAsked(f.SourceEntityId, at, out var path))
                path = _acoustics.CalculateAcousticPath(world, f.SourceEntityId, listenerPosition, at);
            var e = f.Emitter;
            e.Position = at;
            e.Velocity = f.Sky ? Vector3.Zero
                : f.Moving
                ? (now - f.StartedAt < f.MoveSeconds ? (f.To - f.From) / MathF.Max(0.01f, f.MoveSeconds) : Vector3.Zero)
                : speaker.Velocity;
            e.ApparentPosition = path.ApparentPosition;
            e.EffectiveDistance = path.EffectiveDistance;
            e.Occlusion = path.Occlusion;
            e.EqLow = path.EqLow; e.EqMid = path.EqMid; e.EqHigh = path.EqHigh;
            e.AirLowDb = path.AirLowDb; e.AirMidDb = path.AirMidDb; e.AirHighDb = path.AirHighDb;
            e.ApertureFactor = path.ApertureFactor;
            e.TransmissionBleed = path.TransmissionBleed;
            e.TargetRegionId = path.RegionId;
            if (f.InCabin)
            {
                // Where the vehicle is now decides whether you are inside it with the sound: you may have
                // got in, or out, while the window was moving.
                e.InsideListenersVehicle = f.SourceEntityId == ListenerVehicleId;
                if (OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Vehicle(speaker) is { } vehicle)
                    ThroughCabin(ref e, speaker, vehicle, now);
            }
            else if (!f.Moving && !f.Sky) Facing(ref e, speaker.Transform.Rotation, listenerPosition);
            _audio.Submit(e);
        }
    }

    /// <summary>
    /// How many of a transient's far reflections may play, the strongest: a voice budget, not an
    /// acoustic one. Each is a voice, a binaural slot of ninety-six and an FMOD channel, and at four one
    /// round of applause from eleven blocks of stand put fifty voices up and squeezed the cars out.
    /// </summary>
    private const int MaxEchoes = 2;

    /// <summary>
    /// Points across a scattering surface that also radiate its share: two decorrelated arrivals are the
    /// difference between a copy and a wash. A continuous source needs none, its scatter being in the
    /// traced reverb; nothing else accounts for a clap's.
    /// </summary>
    private const int DiffuseTaps = 2;

    /// <summary>
    /// Transient voices' own band of negative ids, one per event, above the engine echo band
    /// (-600,000) and the borrowed-voice band (-700,000): a hashed id that landed in those took over a
    /// car's reflection or a distant car's voice.
    /// </summary>
    internal const int TransientVoiceBase = -100_000;
    internal const int TransientVoiceSpan = 400_000;
    private int _nextVoice;

    /// <summary>The id of the nth transient voice. Static so the bands can be checked without a mixer.</summary>
    internal static int TransientVoiceId(int counter) => TransientVoiceBase - (counter % TransientVoiceSpan);

    private int NextVoiceId() => TransientVoiceId(++_nextVoice);

    /// <summary>How many copies of copies a one-off sound gets. Three kept only the first crossings
    /// of a street; the flutter that follows a shot down it is a couple of dozen (EarlyReflections
    /// .FindFlutter), and each is an event of its own, short-lived.</summary>
    private const int MaxHigherOrderEchoes = EarlyReflections.MaxFlutterArrivals;
    private readonly List<EarlyReflections.Arrival> _higher = new();

    /// <summary>A short impact (a shot, a slam, a clap) rather than a sustained sound.</summary>
    private static bool IsImpulse(in TransientSound s) => s.Character == SoundCharacter.Knock && s.DecaySeconds <= 1f;

    /// <summary>Is the listener in a room, where copies of copies are dense and the reverb is the tail?</summary>
    private bool ListenerEnclosed(WorldSnapshot world, Vector3 listenerPosition)
        => world.AcousticMap != null
           && world.AcousticMap.Regions.TryGetValue(_acoustics.GetRegionAt(world, listenerPosition), out var room)
           && RoomAcoustics.IsEnclosure(room);

    /// <summary>
    /// The second and later bounces of a one-off sound, outdoors only: the clap handed between two
    /// facades, a street's width later each time. An event, which a voice of its own can render; a
    /// sustained sound's copies are not, and in a room the copies of copies are the reverb's tail.
    /// </summary>
    private void QueueHigherOrderEchoes(in Pending item, WorldSnapshot world, Vector3 listenerPosition)
    {
        if (ListenerEnclosed(world, listenerPosition)) return;

        _acoustics.FindReflections(world, item.Sound.Position, listenerPosition, _higher, AudioPhysics.CurrentSpeedOfSound,
                              maxOrder: EarlyReflections.MaxOrder, separateFirst: true,
                              // The roll down a street is for an impulse: two dozen copies of a horn
                              // are a cloud, not a flutter.
                              flutter: IsImpulse(item.Sound));
        float direct = Vector3.Distance(item.Sound.Position, listenerPosition);
        float reference = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres).ReferenceDistance;
        int added = 0;
        foreach (var a in _higher)
        {
            if (a.Order < 2 || !EarlyReflections.IsSeparateEvent(a)) continue;
            if (a.ExtraDelaySeconds <= RoomEchoWindowSeconds) continue;   // placed by QueueEarlyEchoes
            // Loud enough against the sound it is a copy of to be heard as a second event at all.
            if (a.GainMid < ImageSource.EchoAudibleRatio) continue;
            // Placed at the image, which is the path length away: undo what the engine will do there
            // against what it does to the direct sound, as QueueReflections does.
            float gain = EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, direct, reference);
            if (gain < ImageSource.MinGain) continue;
            gain *= OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesTrim;   // /copies
            var echo = item.Sound;
            echo.Position = a.ImagePosition;
            echo.LevelDb = item.Sound.LevelDb + 20f * MathF.Log10(gain);
            QueueEcho(new Pending
            {
                Sound = echo,
                CopyGain = gain, SourceLevelDb = item.Sound.LevelDb,
                // Each crossing is the crack again, a little duller for every surface.
                SoundId = item.SoundId,
                EchoLowDb = SpecularLoss(a.Scattering, a.Order).LowDb,
                EchoHighDb = SpecularLoss(a.Scattering, a.Order).HighDb,
                SourceEntityId = item.SourceEntityId,
                // Not delayed here (see QueueReflections).
                DueAt = item.DueAt,
                IsReflection = true,
                Seed = item.Seed,
            });
            if (++added >= MaxHigherOrderEchoes) break;
        }
    }

    // ── The room you are in: its first answers, from where they come ─────────────────────────
    //
    // The traced room response is almost all omnidirectional (Marlow flat 01F: the directional
    // channels 20 dB under the omni), heard in the middle of the head (interaural correlation
    // 0.85-0.95 in a capture). What places a room round you is its first reflections, so a one-off
    // sound's early reflections are voices of their own at their images, through the HRTF, and the
    // traced stage plays only the late tail (TracedReverbDsp, LateTailIr). The floor under the source is
    // left out: the voice carries its own ground reflection.

    /// <summary>How long the placed reflections run before the tail takes over, seconds. The traced
    /// tail fades in from 50 to 100 ms after the sound (LateTailIr); the two overlap a little.</summary>
    internal const float RoomEchoWindowSeconds = 0.08f;

    /// <summary>At most this many placed reflections per sound: the first order of a box room is six
    /// and the loudest second orders follow.</summary>
    internal const int MaxRoomEchoes = 12;

    /// <summary>
    /// Second-order clean copies per sound, beyond its first order; the rest are the traced tail. Twelve
    /// clean copies of a dry clap were twelve separate clicks: the ear hears copies of an impulse as
    /// echoes from a few milliseconds on, where real reflections fuse.
    /// </summary>
    internal const int MaxSecondOrderCopies = 4;

    /// <summary>
    /// What a surface returns as a clean copy, of what it returns at all: the mirror share,
    /// sqrt(1 - s) of the pressure per bounce. The scattered sqrt(s) is not a copy; it is the wash
    /// (<see cref="EchoId"/>), and for a first-order wall it is placed beside the mirror.
    /// </summary>
    internal static float MirrorShare(float scattering, int order)
        => MathF.Pow(MathF.Sqrt(1f - Math.Clamp(scattering, 0f, 1f)), Math.Max(1, order));

    private readonly List<EarlyReflections.Arrival> _room = new();
    private readonly List<RoomEcho> _roomPlan = new();

    private void QueueEarlyEchoes(in Pending item, WorldSnapshot world, Vector3 listenerPosition,
                                  int maxMirrors = int.MaxValue, bool washes = true)
    {
        int mirrors = 0;
        Vector3 src = item.Sound.Position;
        _acoustics.FindReflections(world, src, listenerPosition, _room, AudioPhysics.CurrentSpeedOfSound,
                              maxOrder: 2, keep: MaxRoomEchoes * 2,
                              maxExtraPathMetres: RoomEchoWindowSeconds * AudioPhysics.CurrentSpeedOfSound);
        float direct = Vector3.Distance(src, listenerPosition);
        float reference = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres).ReferenceDistance;
        PlanRoomEchoes(_room, src, listenerPosition, direct, reference,
                       OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesTrim, audible: true, _roomPlan);   // /copies
        foreach (var e in _roomPlan)
        {
            if (e.InVoice) continue;
            var a = e.Arrival;
            // A first-order wall's scattered share, as its wash: the sound smeared by its roughness.
            if (washes && e.WashGain >= ImageSource.MinGain) QueueWash(item, a, e.WashGain);
            float gain = e.MirrorGain;
            if (gain < ImageSource.MinGain) continue;
            if (++mirrors > maxMirrors) break;
            var echo = item.Sound;
            echo.Position = a.ImagePosition;
            echo.LevelDb = item.Sound.LevelDb + 20f * MathF.Log10(gain);
            var loss = SpecularLoss(a.Scattering, a.Order);
            // What the surfaces took from each band, against the middle: carpet keeps the bass and
            // eats the top, plaster the other way round.
            float lowDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainLow) / MathF.Max(1e-4f, a.GainMid));
            float highDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainHigh) / MathF.Max(1e-4f, a.GainMid));
            QueueEcho(new Pending
            {
                Sound = echo,
                CopyGain = gain, SourceLevelDb = item.Sound.LevelDb,
                SoundId = item.SoundId,
                EchoLowDb = loss.LowDb + lowDb,
                EchoHighDb = loss.HighDb + highDb,
                SourceEntityId = item.SourceEntityId,
                DueAt = item.DueAt,                 // not delayed here (see QueueReflections)
                IsReflection = true,
                Seed = item.Seed,
            });
        }
    }

    /// <summary>
    /// One placed reflection of a one-off sound in a room: the arrival, and what it returns with the
    /// trim (EarlyReflections.PlacedCopyGain times /copies). <paramref name="InVoice"/>: the floor
    /// under the source, which the voice carries itself (GroundReflection) and nothing places.
    /// </summary>
    internal readonly record struct RoomEcho(EarlyReflections.Arrival Arrival, float Returned, bool InVoice)
    {
        public float Scattering => Math.Clamp(Arrival.Scattering, 0f, 1f);
        /// <summary>The clean copy: the mirror share of what the surfaces return.</summary>
        public float MirrorGain => Returned * MirrorShare(Scattering, Arrival.Order);
        /// <summary>A first-order wall's scattered share, placed beside the copy as its wash: zero for
        /// a wall that scatters next to nothing, and for higher orders (their scatter is the tail).</summary>
        public float WashGain => Arrival.Order == 1 && Scattering > 0.05f ? Returned * MathF.Sqrt(Scattering) : 0f;
    }

    /// <summary>
    /// Which of <paramref name="found"/> (a search from <paramref name="src"/> to <paramref name="listener"/>
    /// out to <see cref="RoomEchoWindowSeconds"/>) a room places, loudest first: every first-order
    /// surface in the window, at most <see cref="MaxSecondOrderCopies"/> second orders, at most
    /// <see cref="MaxRoomEchoes"/> copies in all, and the floor under the source marked as the voice's.
    ///
    /// One rule for the copies a sound gets (QueueEarlyEchoes, <paramref name="audible"/>: a copy under
    /// ImageSource.MinGain is not placed and does not count against the cap) and for the early energy
    /// the listener's trace leaves to them (EarlyCopies, not audible: the trace stands for a sound at
    /// the listener, and every surface in the window counts). <paramref name="found"/> is sorted.
    /// </summary>
    internal static void PlanRoomEchoes(List<EarlyReflections.Arrival> found, Vector3 src, Vector3 listener,
                                        float direct, float reference, float trim, bool audible, List<RoomEcho> into)
    {
        into.Clear();
        // Loudest first: in surface order, with more in the window than voices (twenty-two in flat
        // 01F, twelve voices), which walls answered depended on their order in the map.
        found.Sort(static (a, b) => b.GainMid.CompareTo(a.GainMid));
        int added = 0, secondOrder = 0;
        foreach (var a in found)
        {
            if (a.ExtraDelaySeconds > RoomEchoWindowSeconds) continue;
            // The ground under the source: already inside the voice (GroundReflection).
            if (a.Order == 1 && a.HitPoint.Y < MathF.Min(src.Y, listener.Y) - 0.2f)
            {
                into.Add(new RoomEcho(a, 0f, InVoice: true));
                continue;
            }
            if (a.Order >= 2 && ++secondOrder > MaxSecondOrderCopies) continue;
            float returned = audible ? EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, direct, reference) * trim : trim;
            var echo = new RoomEcho(a, returned, InVoice: false);
            into.Add(echo);
            // A copy too quiet to place is not one of the twelve (its wash may still play).
            if (audible && echo.MirrorGain < ImageSource.MinGain) continue;
            if (++added >= MaxRoomEchoes) break;
        }
    }

    /// <summary>A first-order wall's scattered share: the wash of the sound from where the wall
    /// answers, coloured by what the wall absorbs but not by the mirror's loss.</summary>
    private void QueueWash(in Pending item, in EarlyReflections.Arrival a, float gain)
    {
        var wash = item.Sound;
        wash.Position = a.ImagePosition;
        wash.LevelDb = item.Sound.LevelDb + 20f * MathF.Log10(gain);
        float lowDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainLow) / MathF.Max(1e-4f, a.GainMid));
        float highDb = 20f * MathF.Log10(MathF.Max(1e-4f, a.GainHigh) / MathF.Max(1e-4f, a.GainMid));
        QueueEcho(new Pending
        {
            Sound = wash,
            CopyGain = gain, SourceLevelDb = item.Sound.LevelDb,
            SoundId = EchoId(item, scattered: true, a.Scattering),
            EchoLowDb = lowDb,
            EchoHighDb = highDb,
            SourceEntityId = item.SourceEntityId,
            DueAt = item.DueAt,
            IsReflection = true,
            Seed = item.Seed,
        });
    }

    /// <summary>
    /// The same sound again off a wall further out (a facade up the street): queued as another pending
    /// sound at its mirror image, at the direct level times what the surface kept with the extra
    /// spreading undone, since the engine spreads it from the image itself (twice, a wall answered a near
    /// source and went silent for a far one).
    /// </summary>
    private void QueueReflections(in Pending item, EngineReflections? reflections,
                                  Vector3 listenerPosition, double now, bool diffuse = true)
    {
        if (reflections == null || reflections.SurfaceCount == 0) return;

        Span<Reflection> found = stackalloc Reflection[MaxEchoes];
        // Through the echo search, which tests both legs for obstruction: an echo that cannot be blocked
        // is all that is left when the direct sound is ("I only hear the reflections of the clapping").
        int n = reflections.FindReflections(item.Sound.Position, listenerPosition,
                                            AudioPhysics.CurrentSpeedOfSound, found, diffuse ? DiffuseTaps : 0);
        if (n == 0) return;

        float directDist = Vector3.Distance(item.Sound.Position, listenerPosition);
        float reference = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres).ReferenceDistance;
        for (int i = 0; i < n; i++)
        {
            var r = found[i];
            // A scattered tap is a wash, rendered from the sound's own model, which thunder has not.
            if (!diffuse && r.IsDiffuse) continue;
            // Inside the window the surface is already placed by QueueEarlyEchoes; this is the facade
            // up the street, a separate event.
            if (r.DelaySeconds <= RoomEchoWindowSeconds) continue;
            // Loud enough, against the sound it is a copy of, to be heard as a second event at all.
            if (r.Gain < ImageSource.EchoAudibleRatio) continue;

            float gain = EarlyReflections.PlacedCopyGain(r.Gain, r.PathLength, directDist, reference);
            if (gain < ImageSource.MinGain) continue;
            gain *= OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesTrim;   // /copies

            var echo = item.Sound;
            echo.Position = r.ApparentPosition;
            echo.LevelDb = item.Sound.LevelDb + 20f * MathF.Log10(gain);
            QueueEcho(new Pending
            {
                Sound = echo,
                CopyGain = gain, SourceLevelDb = item.Sound.LevelDb,
                SoundId = EchoId(item, r.IsDiffuse, r.Scattering),
                Seed = item.Seed,
                // The mirror copy loses its top to the surface's roughness; the scattered taps are
                // that roughness already, and the wash is their colour.
                EchoLowDb = r.IsDiffuse ? 0f : SpecularLoss(r.Scattering, 1).LowDb,
                EchoHighDb = r.IsDiffuse ? 0f : SpecularLoss(r.Scattering, 1).HighDb,
                SourceEntityId = item.SourceEntityId,
                // On time: the facade delays every submission by its own distance, and the image is the
                // whole path away. Adding the delay here too made a facade's slapback 180 ms, not 90.
                DueAt = item.DueAt,
                IsReflection = true,
            });
        }
    }

    /// <summary>
    /// Renders one sound: a named model by its key's prefix (a gunshot, a door, a crowd, speech), and
    /// anything else as one of the generic characters (TransientSynth).
    /// </summary>
    private float[] RenderOne(TransientSound sound, int seed)
    {
        // A recording, decoded here off the game thread.
        if (Speech.TryParseKey(sound.SynthKey, out string line))
            return SpokenLine(line);
        if (!string.IsNullOrEmpty(sound.SynthKey)
            && sound.SynthKey.StartsWith("weapon:", StringComparison.OrdinalIgnoreCase))
        {
            string id = sound.SynthKey["weapon:".Length..];
            if (WeaponRegistry.TryGet(id, out var weapon))
                return WeaponSynth.MuzzleBlast(WeaponProfile.From(weapon), seed);
        }
        // The admin gun, the teleporter and things handed over: designed sounds, each from its key.
        if (AdminGunSynth.TryRender(sound.SynthKey, seed, out var designed))
            return designed;
        // A bullet going by: the N-wave of its shock, or a piece of a subsonic one's whizz, each
        // worked from its key (BulletFlyby). Both are full scale at the level the server declared.
        if (BulletFlyby.TryParseCrack(sound.SynthKey, out float crackSeconds))
            return BulletFlyby.RenderCrack(crackSeconds, TransientSynth.SampleRate);
        if (BulletFlyby.TryParseWhizz(sound.SynthKey, out var whizz))
            return BulletFlyby.RenderWhizz(whizz, TransientSynth.SampleRate, seed & 3);
        // A round striking something, what it throws off landing, and a ricochet's tumbling slug
        // whining away: each worked from its key (BulletImpact, Ricochet), full scale at the level the
        // server declared from the same key.
        if (BulletImpact.TryParseHit(sound.SynthKey, out var strike))
            return BulletImpact.RenderHit(strike, TransientSynth.SampleRate, seed & 3);
        if (BulletImpact.TryParseDebris(sound.SynthKey, out var debris))
            return BulletImpact.RenderDebris(debris, TransientSynth.SampleRate, seed & 3);
        if (Ricochet.TryParseWhine(sound.SynthKey, out var whine))
            return Ricochet.RenderWhine(whine, TransientSynth.SampleRate, seed & 3);
        // A gun worked by hand: a reload's routine, or a trigger on an empty chamber.
        if (WeaponHandling.TryParseKey(sound.SynthKey, out var handling))
            return WeaponHandling.Render(handling, TransientSynth.SampleRate, seed);
        // A crowd: many impacts, not one event.
        if (Applause.TryParseKey(sound.SynthKey, out var crowd))
            return Applause.Render(crowd, TransientSynth.SampleRate, seed);
        if (sound.SynthKey == Applause.ClapKey)
            return Applause.RenderClap(TransientSynth.SampleRate, seed);
        if (DoorKnock.TryParseKey(sound.SynthKey, out int knocks))
            return DoorKnock.Render(knocks, TransientSynth.SampleRate, seed);
        // A car door: a mechanism fitted to a recording, which one knock and one ring could not be.
        if (CarDoor.TryParseKey(sound.SynthKey, out bool closing))
            return CarDoor.Render(closing, TransientSynth.SampleRate, seed);
        // The simulated models (doors, the key in a lock, a lift door, a car window, breaking glass).
        if (IsDoorModelKey(sound.SynthKey))
            return RenderDoorKey(sound.SynthKey!, _fullScaleDb);
        // A struck thing (a bump, a knock or a tap on anything): its own modes, from its key (StruckThings),
        // placed at its own render's level.
        if (StruckThings.TryParseKey(sound.SynthKey, out var struck))
        {
            var pcm = StruckThings.Render(struck, TransientSynth.SampleRate, out float struckDb);
            _fullScaleDb[sound.SynthKey!] = struckDb;
            return pcm;
        }

        return TransientSynth.Render(sound, seed);
    }

    /// <summary>
    /// A recorded line, brought to <see cref="Speech.BufferLoudnessLufs"/>: the server's level assumes
    /// every line equally loud, and a quieter take is not a quieter person. A missing line is silent
    /// and logged once.
    /// </summary>
    private float[] SpokenLine(string soundId)
    {
        if (!_audio.TryDecodeMono(soundId, out var pcm, out int rate) || pcm.Length == 0)
        {
            if (_missingLines.Add(soundId))
                Serilog.Log.Warning("[SPEECH] no recording for {Line}; it is silent", soundId);
            return new float[16];
        }
        if (rate != TransientSynth.SampleRate) pcm = Resample(pcm, rate, TransientSynth.SampleRate);
        double lufs = Speech.LoudnessLufs(pcm);
        if (lufs > -90.0)
        {
            float gain = (float)Math.Pow(10.0, (Speech.BufferLoudnessLufs - lufs) / 20.0);
            for (int i = 0; i < pcm.Length; i++) pcm[i] = Math.Clamp(pcm[i] * gain, -1f, 1f);
        }
        return pcm;
    }

    private readonly HashSet<string> _missingLines = new();

    /// <summary>A rendered one-shot (at <see cref="TransientSynth.SampleRate"/>) brought to the mixer's
    /// rate, band-limited, on the worker that rendered it (MixerQuality.Resample).</summary>
    private static (string Id, float[] Pcm, int Rate) AtMixerRate(string id, float[] pcm)
    {
        int rate = MixerQuality.MixerRate;
        return (id, MixerQuality.Resample(pcm, TransientSynth.SampleRate, rate), rate);
    }

    /// <summary>A line at another rate brought to the render rate, band-limited. The shipped lines are
    /// already at 48 kHz; this is for a file that is not, so it plays at the right pitch, and without the
    /// images linear interpolation left above the old Nyquist.</summary>
    internal static float[] Resample(float[] pcm, int from, int to) => MixerQuality.Resample(pcm, from, to);

    /// <summary>Linear interpolation (the AudioLab's thunder spike).</summary>
    internal static float[] ResampleLinear(float[] pcm, int from, int to)
    {
        int n = (int)((long)pcm.Length * to / from);
        var y = new float[Math.Max(1, n)];
        double step = (double)from / to;
        for (int i = 0; i < y.Length; i++)
        {
            double x = i * step;
            int k = (int)x;
            float f = (float)(x - k);
            float a = pcm[Math.Min(k, pcm.Length - 1)], b = pcm[Math.Min(k + 1, pcm.Length - 1)];
            y[i] = a + (b - a) * f;
        }
        return y;
    }

    /// <summary>What a mirror copy loses at the bottom and the top to the surfaces' roughness, dB.</summary>
    internal static (float LowDb, float HighDb) SpecularLoss(float scattering, int bounces)
        => ImageSource.SpecularBandLossDb(scattering, bounces);

    /// <summary>
    /// What an echo plays. The mirror share is the sound itself, a crack from the wall: smeared, a shot
    /// off steel or concrete came back a ringing, processed copy. Only the scattered share, the taps
    /// across the face (ImageSource), is a wash: the sound through the engines' EchoDiffuser (about a
    /// millisecond for glass and polished steel, a dozen for brick), five steps of roughness, each
    /// rendered once per sound and seed.
    /// </summary>
    private string EchoId(in Pending item, bool scattered, float scattering)
        => scattered ? DiffusedId(item, scattering) : item.SoundId;

    private string DiffusedId(in Pending item, float scattering)
    {
        int step = (int)MathF.Round(Math.Clamp(scattering, 0f, 1f) * 4f);
        string id = $"{item.SoundId}~wash{step}";
        if (!_registered.Contains(id) && _rendering.Add(id))
        {
            var sound = item.Sound;
            int seed = item.Seed;
            System.Threading.Tasks.Task.Run(() => _rendered.Enqueue(AtMixerRate(id, Diffuse(RenderOne(sound, seed), step / 4f, seed))));
        }
        return id;
    }

    /// <summary>An echo joins the queue if its washed copy exists, or waits for it like a first hearing.</summary>
    private void QueueEcho(Pending echo)
    {
        if (_registered.Contains(echo.SoundId)) _pending.Add(echo);
        else _awaitingRender.Add(echo);
    }

    /// <summary>The sound through the diffuser, with room after it for the smear to ring out.</summary>
    internal static float[] Diffuse(float[] pcm, float scattering, int seed)
    {
        var d = new OpenFPS.Client.AudioEngine.Fmod.EchoDiffuser(scattering, seed, TransientSynth.SampleRate);
        int tail = (int)(0.004f * (1f + 24f * scattering) * TransientSynth.SampleRate);
        var y = new float[pcm.Length + tail];
        for (int i = 0; i < y.Length; i++) y[i] = d.Process(i < pcm.Length ? pcm[i] : 0f);
        return y;
    }

    /// <summary>Forgets everything queued, and thunder still rendering, and stops the voices it was
    /// following: for a map change, where the positions mean nothing any more.</summary>
    public void Clear()
    {
        // A followed voice left playing stays where it last was: thunder for tens of seconds, a line
        // finishing in the next map from wherever its speaker stood in the last.
        foreach (var f in _following) _audio.StopSound(f.Emitter.EntityId);
        _pending.Clear(); _awaitingRender.Clear(); _following.Clear(); _mapGeneration++;
    }

    /// <summary>Sounds still to play: queued, waiting for a render, or followed.</summary>
    internal int Outstanding => _pending.Count + _awaitingRender.Count + _following.Count;

    /// <summary>Raised by each <see cref="Clear"/>.</summary>
    internal int MapGeneration => _mapGeneration;

    /// <summary>
    /// A sound's parameters are its id, so a corridor of identical doors is one buffer. Quantised: a
    /// thousandth of a decibel apart is not two sounds.
    /// </summary>
    private static string IdFor(TransientSound sound, int seed)
    {
        int hz = (int)MathF.Round(sound.Hz);
        int level = (int)MathF.Round(sound.LevelDb);
        int decay = (int)MathF.Round(sound.DecaySeconds * 100f);
        int noise = (int)MathF.Round(sound.Noisiness * 20f);
        // Four seeds: a handful of variations of each sound, not one per event. A recording, an N-wave
        // and a door model's key are their own identity.
        if (Speech.TryParseKey(sound.SynthKey, out _)) return $"synth:{sound.SynthKey}";
        if (BulletFlyby.TryParseCrack(sound.SynthKey, out _)) return $"synth:{sound.SynthKey}";
        if (IsDoorModelKey(sound.SynthKey))
            return $"synth:{sound.SynthKey}";
        // A strike's key carries its own variant.
        if (sound.SynthKey != null && sound.SynthKey.StartsWith(StruckThings.KeyPrefix, StringComparison.Ordinal))
            return $"synth:{sound.SynthKey}";
        if (!string.IsNullOrEmpty(sound.SynthKey)) return $"synth:{sound.SynthKey}:{seed & 3}";
        return $"synth:{sound.Character}:{hz}:{level}:{decay}:{noise}:{seed & 3}";
    }
}
