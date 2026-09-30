using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// Plays the short sounds the world reports, and this is the only thing in the client that knows how.
///
/// Before this the server's entire vocabulary for sound was "this entity carries a looping emitter",
/// which is why glass breakage, gunfire and collisions are all written, tested and completely silent:
/// there was no way to say "that just happened", only "that is always happening". This is that way,
/// and it is deliberately generic — it knows about knocks, rings, hisses and scrapes, and nothing at
/// all about doors. A ball that bounces needs no code here.
///
/// Two ideas do the work.
///
/// RENDER ONCE, PLAY BY NAME. A sound's parameters ARE its identity: two doors of the same material
/// and size shutting at the same speed are the same buffer, so the id is a hash of the parameters and
/// the buffer is synthesised the first time and found in the cache ever after. A busy corridor of
/// identical doors costs one render.
///
/// THEN IT IS JUST A SOUND. Once registered under an id it goes through the ordinary emitter path,
/// which already does placement, distance, occlusion through walls, portals, reverb and the region —
/// none of which needed changing and none of which knows that nobody recorded it. That is the whole
/// reason for bridging at the sound-id layer rather than playing buffers directly: a rendered latch
/// heard through a wall is muffled by the same code that muffles a recorded one.
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
        /// <summary>This one is already an echo of another. It gets no echoes of its own — a
        /// first-order model that reflects its own reflections is a second-order model with none of
        /// the geometry checks that go with one.</summary>
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
    }

    private readonly AudioEngineFacade _audio;
    private readonly SpatialAcoustics _acoustics;
    private readonly List<Pending> _pending = new();
    private readonly HashSet<string> _registered = new();

    /// <summary>
    /// Buffers being synthesised on a worker, and the ones that have come back.
    ///
    /// A NEW sound used to be rendered on the spot, on the thread that took it off the wire — which is
    /// the game thread. A crowd of three hundred people clapping for three and a half seconds is a
    /// thousand claps a second to synthesise, measured at 170 ms, and the world stops for every one of
    /// them. The same reasoning that moved engine synthesis off the mixer callback applies here: the
    /// thread that must not be blocked is whichever one is holding the clock.
    ///
    /// The event that discovers a new sound WAITS for it, and is dropped only if the render comes back
    /// too late to belong to its moment (see MaxRenderLateness): a rendered-too-late clap is a clap in
    /// the wrong place, but a door latch that took four milliseconds to make is not late at all.
    /// </summary>
    private readonly HashSet<string> _rendering = new();

    /// <summary>Sounds heard for the first time, waiting for the worker to hand back their buffer.</summary>
    private readonly List<Pending> _awaitingRender = new();

    /// <summary>
    /// How late a first hearing may play once its buffer is back, seconds.
    ///
    /// A door's parts render in a few milliseconds, so they are never late; a stand of three hundred
    /// people clapping takes about 170 ms and is. Past this the sound is a clap in the wrong place and
    /// is dropped, which is what the drop was always for — but only for the ones that are actually late.
    /// </summary>
    internal const double MaxRenderLateness = 0.12;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Id, float[] Pcm)> _rendered = new();

    /// <summary>
    /// How far a transient carries at most.
    ///
    /// It is a bound on cost and nothing else, so it has to sit past anything anyone is meant to hear
    /// — the provider fades a voice to nothing over the last quarter of its range, so a range that
    /// cuts in early is not a saving, it is a silence. At 250 m it cut in at 187, and the grandstand
    /// on the speedway is 219 m from where you land: a crowd of four hundred, at a hundred and
    /// eighteen decibels, was being faded out for being far away by a number that had nothing to do
    /// with whether it could be heard. The level's own audible range decides that — and the SERVER
    /// already decides what is worth sending at all, per map, from what is actually in it.
    /// </summary>
    internal const float MaxRange = 3000f;

    /// <summary>The simulator's worker, for a one-shot's first answer. See
    /// <see cref="OpenFPS.Client.AudioEngine.Acoustics.AsyncAcousticWorker.TryGetNearby"/>.</summary>
    public OpenFPS.Client.AudioEngine.Acoustics.AsyncAcousticWorker? Worker { get; set; }

    public WorldAudioPlayer(AudioEngineFacade audio, SpatialAcoustics acoustics)
    {
        _audio = audio;
        _acoustics = acoustics;
    }

    /// <summary>How many are waiting to be heard. Diagnostics.</summary>
    public int Pending_Count => _pending.Count;

    /// <summary>
    /// Takes an event off the wire: renders anything new, and queues every sound for its moment.
    /// </summary>
    /// <summary>
    /// Tracing for OPENFPS_AUDIO_DEBUG=1, which is what `run-gtk-client.sh capture` turns on.
    ///
    /// Every short sound the world makes goes through here, and until this existed there was no way
    /// to answer "what was that bang" except by reading a synth key out of a spatialiser trace and
    /// working backwards. A report of "periodic bangs for ten seconds after I stop walking" is a
    /// question about WHAT and WHEN, and both are known right here.
    /// </summary>
    private static readonly bool _trace = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";

    /// <summary>
    /// A horn being sounded on a vehicle: the vehicle, which horn, and the rhythm. Not rendered here
    /// — a horn is played ON its vehicle for as long as it is held, and moves with it — so it is
    /// handed to whoever voices vehicles. See <see cref="Honk"/>.
    /// </summary>
    public Action<int, string, float[]>? HornReceived { get; set; }

    /// <summary>Everything the world reports, before it is played — for whatever else is listening
    /// (the birds, who go quiet at a bang).</summary>
    public Action<WorldAudioEvent>? Received { get; set; }

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
            string id = IdFor(sound, message.Seed);
            if (!_registered.Contains(id))
            {
                // Not heard before: synthesise it on a worker and let this one go. See _rendering.
                if (_rendering.Add(id))
                {
                    var toRender = sound;
                    int seed = message.Seed;
                    System.Threading.Tasks.Task.Run(() => _rendered.Enqueue((id, RenderOne(toRender, seed))));
                }
                // ...but it is not simply let go. It waits for its own buffer and plays if that comes
                // back in time — see MaxRenderLateness. Dropping every first hearing silenced whatever
                // is RARE: each sound has four seed variants and each is a first hearing once, so a
                // door material used a handful of times a session was never heard at all. The city's
                // seven steel doors were exactly that — "it just says the steel door swings open".
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
    /// Submits everything whose moment has come, through the ordinary acoustic path.
    ///
    /// The source entity is ignored when the path is worked out, because a door must not be occluded
    /// by itself — the leaf is solid and sits exactly where its own latch is, so without this every
    /// door would be heard through a door.
    /// </summary>
    /// <summary>The vehicle the listener is sitting in, or -1. Its own sounds are not heard through its glass.</summary>
    public int ListenerVehicleId { get; set; } = -1;

    public void Update(WorldSnapshot world, Vector3 listenerPosition, double now,
                       EngineReflections? reflections = null)
    {
        // Anything a worker finished is registered here, on the thread that owns the engine.
        while (_rendered.TryDequeue(out var done))
        {
            if (_audio.RegisterSynthesisedSound(done.Id, TransientSynth.ToPcm16(done.Pcm), TransientSynth.SampleRate))
            {
                _registered.Add(done.Id);
                _rendering.Remove(done.Id);
            }
            else
            {
                // The engine is not up yet. Forget it was ever asked for, so the next event asks again.
                _rendering.Remove(done.Id);
            }
        }

        // Sounds that were waiting on their first render: in time, they join the queue as if they
        // had always been there; too late, they belong to a moment that has gone and are dropped.
        for (int i = _awaitingRender.Count - 1; i >= 0; i--)
        {
            var item = _awaitingRender[i];
            bool ready = _registered.Contains(item.SoundId);
            bool late = now > item.DueAt + MaxRenderLateness;
            if (!ready && !late && _rendering.Contains(item.SoundId)) continue;
            _awaitingRender.RemoveAt(i);
            if (ready && !late) _pending.Add(item);
        }

        FollowSpeakers(world, listenerPosition, now);
        if (_pending.Count == 0) return;

        // More than one pass: a sound's early reflections are queued WHILE it is played, at the end
        // of the list this loop has already walked past, and left for the next update they went out
        // a whole frame late. Each is delayed by its own path from the moment it is submitted, so a
        // reflection due 6-25 ms after a clap arrived 45-65 ms after it: a cluster of slaps of its own
        // ("the clapping breaks up", every one of 289 echoes in the log 0.02-0.05 s late, 2026-09-29).
        // Played in the same pass, every copy starts from the same moment as its source.
        for (int pass = 0; pass < 3; pass++)
        {
        bool playedAny = false;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            if (now < item.DueAt) continue;
            playedAny = true;
            _pending.RemoveAt(i);

            if (_trace)
                Serilog.Log.Information("[WAUDIO] play {Id}{Echo} {Db:F0} dB at {Dist:F1} m, {Late:F2}s after it was due "
                                      + "({Q} still queued)",
                    item.SoundId, item.IsReflection ? " (echo)" : "", item.Sound.LevelDb,
                    Vector3.Distance(listenerPosition, item.Sound.Position), now - item.DueAt, _pending.Count);

            // A person talking gets no echo copies. Each copy is the whole line again from a fixed
            // mirror point, while the speaker walks on: heard as a room passing you rather than a
            // person. The street's answer to a voice comes from the reverb, which follows the listener.
            bool spoken = Speech.TryParseKey(item.Sound.SynthKey, out _);
            if (!item.IsReflection && !spoken)
            {
                QueueEarlyEchoes(item, world, listenerPosition);
                QueueReflections(item, reflections, listenerPosition, now);
                QueueHigherOrderEchoes(item, world, listenerPosition);
            }

            var path = _acoustics.CalculateAcousticPath(world, item.SourceEntityId,
                                                        listenerPosition, item.Sound.Position);
            // An echo is placed at its mirror image, which is BEHIND the wall it came off, and the ray
            // from the listener to that point goes through that very wall: every echo came out 40-60 dB
            // down, even of a shot in plain view (traced 2026-09-28, "reflections for gunshots don't
            // appear all that loud"). Both legs of an echo's route were already checked clear when it
            // was found (ImageSource, EarlyReflections), so, as for the engines' echoes
            // (EngineReflections.ApplyPath), it keeps the air over its own path and nothing else.
            if (item.IsReflection)
                path = path with
                {
                    Occlusion = 0f, EqMid = 1f,
                    EqLow = MathF.Pow(10f, item.EchoLowDb / 20f), EqHigh = MathF.Pow(10f, item.EchoHighDb / 20f),
                    ApertureFactor = 1f, TransmissionBleed = 0f, ApparentPosition = item.Sound.Position,
                };
            // Start where the simulator will put it, not where the hand-rolled tracer guesses: the
            // simulator's answer for the nearest source it heard a moment ago, moved to this one.
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
            // Placed at its own SIZE if it has one. A grandstand full of people is eight metres
            // across, and inside that the level is flat; beyond it, it falls away exactly as a point
            // source of the same power would, which is what the gain compensation in there is for.
            var placed = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres);
            // A COPY keeps its source's placement. The placement compresses level differences between
            // sounds (Loudness.DynamicRangeCompression, 0.45 shipped) — right between a rifle and a
            // footstep, wrong between a sound and its own reflection: an echo handed in 14 dB down
            // came out 6 dB down, every reflection in the game 4-8 dB too loud against what it is a
            // copy of ("all the reflections are piling up", 2026-09-29). Placed as the source and
            // scaled by what the surface and the longer path actually kept, it is exactly that much
            // under it; distance is the engine's literal 1/r either way.
            if (item.IsReflection && item.CopyGain > 0f)
            {
                var source = Loudness.Place(item.SourceLevelDb, item.Sound.ExtentMetres);
                placed = (source.Gain * item.CopyGain, source.ReferenceDistance);
            }

            var emitter = new SpatialEmitter
            {
                // A voice of its own, every time.
                //
                // This used to be a hash of the SOUND, which is a different thing entirely: the id is
                // what the VoiceManager keys a submission by, so two sounds sharing one replace each
                // other. Two identical footsteps are the same BUFFER — that is what the id-by-
                // parameters cache is for — but they are not the same EVENT, and a sound and its own
                // reflection never are. Submitted together under one id, the last one written won,
                // which is why a grandstand full of people could be heard only as its echo.
                // Still negative, so a transient can never collide with an entity's own voice.
                EntityId = NextVoiceId(),
                SoundId = item.SoundId,
                // Single: it happens once and stops. Nothing here ever loops — a latch that looped
                // would be a fire alarm.
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
                // Gain and reference distance are decided TOGETHER — that is the whole point of
                // Loudness.Place, and taking the gain while hardcoding the reference throws half of
                // it away. A quiet source wants a short reference so it is still itself close to;
                // a gunshot wants a long one so it is still full scale across a street.
                Volume = placed.Gain,
                Range = MathF.Min(MaxRange, Loudness.AudibleRange(item.Sound.LevelDb)),
                MinDistance = placed.ReferenceDistance,
                Pitch = 1.0f,
                Type = EmitterType.WorldLocked,
                // Nothing is ranked by WHAT IT IS any more. A reflection gives way first because it
                // IS quieter — its Volume already carries what the surface kept and how far the
                // mirrored path ran — and the budget ranks on the level a voice will deliver.
                // Outdoors, an impulse's tail is the geometry's: the facades hand it back a crossing
                // at a time (QueueHigherOrderEchoes) and the sky takes the rest. The reverb unit is a
                // ROOM's tail — dense and smooth from the first milliseconds — and a shot sent to it
                // between two buildings sounded fired in a hall ("gunshots sound odd with the
                // reverb"). Indoors the copies are too dense to hear apart and the reverb is right;
                // a sustained sound's late field is the reverb's everywhere.
                //
                // That was the ROOM algorithm. Traced, the tail is the street's own response —
                // the facades handing the shot back again and again, the sky taking the rest — and
                // it is what a real shot between buildings rolls on with: "I don't hear many echos".
                // So in traced mode an outdoor impulse goes to it; in room mode it still does not.
                // An echo is already the street answering; sent to the tail as well, it would be
                // counted twice.
                EnableReverb = !item.IsReflection,
                // An EVENT: it belongs to a moment. If the budget has no room for it now there is no
                // playing it later — see VoiceManager.Process, which drops one that did not win a slot
                // rather than keeping it queued to fire from a stale position minutes afterwards.
                IsEvent = true,
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
            if (HearsTheGround(item, spoken)) Ground?.Invoke(ref emitter, world);
            _audio.Submit(emitter);

            // Somebody talking while they walk carries their voice with them. A two-second line left
            // where it started is three metres behind the footsteps by the end of it.
            if (follow && world.Entities.TryGetValue(item.SourceEntityId, out var speaker))
                _following.Add(new Following
                {
                    Emitter = emitter,
                    SourceEntityId = item.SourceEntityId,
                    Offset = item.Sound.Position - speaker.Transform.Position,
                    // Stop a little before the line ends: a submission after the voice has finished
                    // would start it again.
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

    /// <summary>
    /// Whether a sound gets a ground reflection of its own. Not an echo copy, which is already a path
    /// off a surface; and not a source with a size, whose parts are at every height and distance at
    /// once, so their bounces arrive spread out and add up to no comb at all.
    ///
    /// Impulses only, for now: a shot, a door, a knock, whose bounce lands inside the attack and is heard
    /// as part of it. On anything that lasts it is a comb that stands still.
    ///
    /// Not speech. A voice three metres off on asphalt has a bounce 4 ms late at two thirds
    /// of its pressure, and that is what the physics says (Acta Acustica 2024, doi
    /// 10.1051/aacus/2024002: below 800 Hz it is stronger still). Rendered, it flanged, summed into
    /// the voice's direction and again from its own direction below (heard 2026-09-27, both). A real
    /// talker on a pavement does not sound like that, so something the ear uses is missing: the
    /// torso's shadow on sound from below, the talker's own vertical radiation, or the small
    /// movements that keep a comb from standing still. Until one is measured, a voice has none.
    /// </summary>
    private static bool HearsTheGround(in Pending item, bool spoken)
        => !spoken && IsImpulse(item.Sound) && !item.IsReflection && item.Sound.ExtentMetres <= 1f;

    /// <summary>Folds which way the speaker faces into the path's band gains.</summary>
    private static void Facing(ref SpatialEmitter e, Quaternion rotation, Vector3 listenerPosition)
    {
        var (low, mid, high) = Speech.Directivity(Vector3.Transform(Vector3.UnitZ, rotation), listenerPosition - e.Position);
        e.EqLow *= low; e.EqMid *= mid; e.EqHigh *= high;
    }

    /// <summary>A line being said by a body that is moving, and where its mouth is on that body.</summary>
    private struct Following
    {
        public SpatialEmitter Emitter;
        public int SourceEntityId;
        public Vector3 Offset;
        public double Until;
        public double StartedAt;
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
            if (now >= f.Until || !world.Entities.TryGetValue(f.SourceEntityId, out var speaker)
                || (now - f.StartedAt > StartGraceSeconds && !_audio.IsPlaying(f.Emitter.EntityId)))
            {
                _following.RemoveAt(i);
                continue;
            }
            var at = speaker.Transform.Position + f.Offset;
            var path = _acoustics.CalculateAcousticPath(world, f.SourceEntityId, listenerPosition, at);
            var e = f.Emitter;
            e.Position = at;
            e.Velocity = speaker.Velocity;
            e.ApparentPosition = path.ApparentPosition;
            e.EffectiveDistance = path.EffectiveDistance;
            e.Occlusion = path.Occlusion;
            e.EqLow = path.EqLow; e.EqMid = path.EqMid; e.EqHigh = path.EqHigh;
            e.AirLowDb = path.AirLowDb; e.AirMidDb = path.AirMidDb; e.AirHighDb = path.AirHighDb;
            e.ApertureFactor = path.ApertureFactor;
            e.TransmissionBleed = path.TransmissionBleed;
            e.TargetRegionId = path.RegionId;
            Facing(ref e, speaker.Transform.Rotation, listenerPosition);
            _audio.Submit(e);
        }
    }

    /// <summary>
    /// How many arrivals a transient's reflections may cost.
    ///
    /// TWO, and the number is a voice budget rather than an acoustic one. Every arrival is a voice, a
    /// Steam Audio binaural slot out of a pool of ninety-six, and an FMOD channel — and a stand full of
    /// people is eleven blocks reacting, each of them a sound in its own right. Four was enough to put
    /// fifty voices in the air for one round of applause and squeeze the cars, which are the thing a
    /// player navigates by. The strongest two of whatever the search found, mirror or scattered.
    /// </summary>
    private const int MaxEchoes = 2;

    /// <summary>
    /// How many points across a scattering surface also radiate its share.
    ///
    /// Two, because two decorrelated arrivals a few tens of milliseconds apart is the difference
    /// between a copy and a wash, and every one after that costs a voice for less and less. A ONE-SHOT
    /// needs this and a continuous source does not: an engine's scattered energy is already in the
    /// ray-traced reverb that drives the outdoor wet level, and nothing anywhere accounts for a clap's.
    /// </summary>
    private const int DiffuseTaps = 2;

    /// <summary>
    /// Transient voices live in their own band of negative ids, one per event.
    ///
    /// The band matters as much as the uniqueness. The id used to be a hash of the sound's parameters
    /// modulo a million, which spans -1,000 to -1,001,000 — straight across the engine echo band at
    /// -600,000 and the borrowed-voice band at -700,000. A door or a footstep whose hash landed there
    /// took over a car's reflection or a distant car's voice, which is heard as a car going quiet, or
    /// as a reflection of a bike that has long since gone past standing still and repeating.
    /// This band sits above both and below any entity id, which are positive.
    /// </summary>
    internal const int TransientVoiceBase = -100_000;
    internal const int TransientVoiceSpan = 400_000;
    private int _nextVoice;

    /// <summary>The id of the nth transient voice. Static so the bands can be checked without a mixer.</summary>
    internal static int TransientVoiceId(int counter) => TransientVoiceBase - (counter % TransientVoiceSpan);

    private int NextVoiceId() => TransientVoiceId(++_nextVoice);

    /// <summary>
    /// The same sound again, later, off a wall.
    ///
    /// A reflection of a one-shot IS the one-shot, delayed, quieter and arriving from somewhere else —
    /// which is exactly what this queue already does, so it needs no voices of its own and no new
    /// model: the mirrored source is queued as another pending sound. A cheer off the back of a
    /// grandstand is most of what makes a stand sound occupied rather than like a loudspeaker hung in
    /// the air, and the same machinery gives a gunshot the slapback off the building opposite.
    ///
    /// THE DELAY IS NOT ADDED HERE. The mirrored source is further away and the facade already delays
    /// every submission by its own distance over the speed of sound, so queueing it late as well
    /// counted the extra path twice — heard as a knock arriving a second after a footstep instead of
    /// a tenth of one, which is the difference between a room and a canyon.
    ///
    /// The level is the direct sound's, times what the surface kept, times the extra spreading UNDONE
    /// — because the echo is placed at the mirrored position and the engine applies that spreading
    /// itself. Leaving it in applies it twice, which is a wall that answers a near source and goes
    /// silent for a far one.
    /// </summary>
    /// <summary>How many copies-of-copies one event may add. A clap between two facades comes back as a
    /// short train; past three the train is the tail.</summary>
    /// <summary>How many copies of copies a one-off sound gets. Three kept only the first crossings
    /// of a street; the flutter that follows a shot down it is a couple of dozen (EarlyReflections
    /// .FindFlutter), and each is an event of its own, short-lived.</summary>
    private const int MaxHigherOrderEchoes = EarlyReflections.MaxFlutterArrivals;
    private readonly List<EarlyReflections.Arrival> _higher = new();

    /// <summary>
    /// The second and third bounces of a ONE-OFF sound, out in the open: the clap handed back and
    /// forth between two facades, each crossing a street's width later than the last.
    ///
    /// Only here, and only outdoors. A one-off sound's echo is an event — it happens once and is gone —
    /// which is exactly what a separate voice can render. A sustained sound's echo is not (see
    /// AsyncAcousticWorker.AddEarlyReflections), and in a room the copies of copies are the dense
    /// tail, which the reverb already is. First order stays with QueueReflections.
    /// </summary>
    /// <summary>A short impact — a shot, a slam, a clap — rather than a sustained sound.</summary>
    private static bool IsImpulse(in TransientSound s) => s.Character == SoundCharacter.Knock && s.DecaySeconds <= 1f;

    /// <summary>Is the listener in a room, where copies of copies are dense and the reverb is the tail?</summary>
    private bool ListenerEnclosed(WorldSnapshot world, Vector3 listenerPosition)
        => world.AcousticMap != null
           && world.AcousticMap.Regions.TryGetValue(_acoustics.GetRegionAt(world, listenerPosition), out var room)
           && RoomAcoustics.IsEnclosure(room);

    private void QueueHigherOrderEchoes(in Pending item, WorldSnapshot world, Vector3 listenerPosition)
    {
        if (ListenerEnclosed(world, listenerPosition)) return;

        var solids = _acoustics.ReflectionSolids(world);
        if (solids.Count == 0) return;
        EarlyReflections.Find(item.Sound.Position, listenerPosition, solids, _higher, AudioPhysics.SpeedOfSound,
                              maxOrder: EarlyReflections.MaxOrder, separateFirst: true,
                              // The long roll down a street is for an IMPULSE: a shot, a slam, a clap.
                              // Two dozen overlapping copies of a two-second horn are a cloud, not a
                              // flutter — a sustained sound's copies of copies are the field.
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
                // A chain of mirrors: each crossing is the crack again, a street's width later, and a
                // little duller for every surface it has come off.
                SoundId = item.SoundId,
                EchoLowDb = SpecularLoss(a.Scattering, a.Order).LowDb,
                EchoHighDb = SpecularLoss(a.Scattering, a.Order).HighDb,
                SourceEntityId = item.SourceEntityId,
                // Not delayed here: see QueueReflections — the facade delays every submission by its
                // own distance, and the image is the whole path length away.
                DueAt = item.DueAt,
                IsReflection = true,
                Seed = item.Seed,
            });
            if (++added >= MaxHigherOrderEchoes) break;
        }
    }

    // ── The room you are in: its first answers, from where they come ─────────────────────────
    //
    // The traced response of a room is built round the listener's head from an energy field, and
    // what it hands back is almost all omnidirectional: in Marlow flat 01F its left-right, up-down
    // and front-back channels sit twenty decibels under the omni one. A room made of that is heard in
    // the middle of the head and does not move when the head turns ("the room sounds narrow... I
    // turn my head and nothing seems to move or change", 2026-09-29, interaural correlation 0.85-0.95
    // in the capture). What places a real room round you is its first few reflections, each off one
    // wall, each from that wall's direction.
    //
    // So in a room, a one-off sound's early reflections are voices of their own, mirrored through
    // the walls round it (EarlyReflections, to third order), each placed at its image through the
    // HRTF, and the room's traced stage plays only the late tail (TracedReverbDsp, parametric).
    // The floor under the source is left out: the voice already carries its own ground reflection.

    /// <summary>How long the placed reflections run before the tail takes over, seconds. Steam Audio's
    /// parametric tail comes in about 50 ms after the sound; the two overlap a little.</summary>
    internal const float RoomEchoWindowSeconds = 0.08f;

    /// <summary>At most this many placed reflections per sound: the first order of a box room is six
    /// and the loudest second orders follow.</summary>
    internal const int MaxRoomEchoes = 12;

    /// <summary>
    /// How many SECOND-order copies a sound's room gets as clean copies, beyond its first order. The
    /// rest of the copies of copies are the room's tail, which the trace already is. Up to twelve
    /// clean copies of one dry clap were twelve separate clicks to the ear, which hears copies of an
    /// impulse as echoes from a few milliseconds on, where real reflections fuse; that, not their
    /// level, is what -24 was paying for (2026-09-29).
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

    private void QueueEarlyEchoes(in Pending item, WorldSnapshot world, Vector3 listenerPosition)
    {
        var solids = _acoustics.ReflectionSolids(world);
        if (solids.Count == 0) return;
        Vector3 src = item.Sound.Position;
        EarlyReflections.Find(src, listenerPosition, solids, _room, AudioPhysics.SpeedOfSound,
                              maxOrder: 2, keep: MaxRoomEchoes * 2,
                              maxExtraPathMetres: RoomEchoWindowSeconds * AudioPhysics.SpeedOfSound);
        float direct = Vector3.Distance(src, listenerPosition);
        float reference = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres).ReferenceDistance;
        // Loudest first. Find hands its arrivals back in surface order, and with more inside the
        // window than there are voices (twenty-two in flat 01F, twelve voices) the first twelve BY
        // SURFACE were taken, and which walls answered depended on their order in the map.
        _room.Sort(static (a, b) => b.GainMid.CompareTo(a.GainMid));
        int added = 0, secondOrder = 0;
        foreach (var a in _room)
        {
            if (a.ExtraDelaySeconds > RoomEchoWindowSeconds) continue;
            // The ground under the source: already inside the voice (GroundReflection).
            if (a.Order == 1 && a.HitPoint.Y < MathF.Min(src.Y, listenerPosition.Y) - 0.2f) continue;
            if (a.Order >= 2 && ++secondOrder > MaxSecondOrderCopies) continue;
            float returned = EarlyReflections.PlacedCopyGain(a.GainMid, a.PathLength, direct, reference)
                           * OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.CopiesTrim;   // /copies
            // The scattered share of a first-order wall, as the wall's wash rather than a copy: the
            // sound smeared by that surface's roughness, from the same place, carrying what the
            // mirror does not. A wall that scatters little sends back almost all of it as the crack.
            float s = Math.Clamp(a.Scattering, 0f, 1f);
            if (a.Order == 1 && s > 0.05f && returned * MathF.Sqrt(s) >= ImageSource.MinGain)
                QueueWash(item, a, returned * MathF.Sqrt(s));
            float gain = returned * MirrorShare(s, a.Order);
            if (gain < ImageSource.MinGain) continue;
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
                DueAt = item.DueAt,                 // the facade delays it by its own path; see QueueReflections
                IsReflection = true,
                Seed = item.Seed,
            });
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

    private void QueueReflections(in Pending item, EngineReflections? reflections,
                                  Vector3 listenerPosition, double now)
    {
        if (reflections == null || reflections.SurfaceCount == 0) return;

        Span<Reflection> found = stackalloc Reflection[MaxEchoes];
        // Sampled across a scattering face as well as mirrored through it.
        // Through the echo system's own search, which only mirrors through faces near the path and
        // tests both legs for something standing in the way. Doing it straight out of ImageSource
        // skipped the obstruction test, and an echo that cannot be blocked is the one thing left when
        // the direct sound is — which is what "I only hear the reflections of the clapping" was.
        int n = reflections.FindReflections(item.Sound.Position, listenerPosition,
                                            AudioPhysics.SpeedOfSound, found, DiffuseTaps);
        if (n == 0) return;

        float directDist = Vector3.Distance(item.Sound.Position, listenerPosition);
        float reference = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres).ReferenceDistance;
        for (int i = 0; i < n; i++)
        {
            var r = found[i];
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
                // ON TIME, because it is already late. The facade delays every submission by its own
                // distance over the speed of sound, and an echo is submitted at its mirrored position —
                // the whole path length away — so it arrives exactly its extra path behind the direct
                // sound with nothing added here. This used to add r.DelaySeconds as well, from when no
                // delay in the engine was honoured at all (FMOD read the wrong clock; see the provider's
                // setDelay). Once that was fixed both applied, and every echo of every world sound came
                // twice as late as the wall it came off: a facade's slapback at 180 ms instead of 90.
                DueAt = item.DueAt,
                IsReflection = true,
            });
        }
    }

    /// <summary>
    /// Renders one sound, by whichever model knows how.
    ///
    /// Nearly everything is one of the four generic characters. A few things name a richer model
    /// instead — a gunshot is a blast wave, a body resonance, a brightness sweep and the action
    /// working, and flattening that to one knock would throw away a model that exists and is better.
    /// The routing is by prefix, exactly as engine emitters already route "engine:v8_sports".
    /// </summary>
    private float[] RenderOne(TransientSound sound, int seed)
    {
        // A person saying something: a recording, not a model. Decoded here, off the game thread, and
        // then it is a world sound like any other.
        if (Speech.TryParseKey(sound.SynthKey, out string line))
            return SpokenLine(line);
        if (!string.IsNullOrEmpty(sound.SynthKey)
            && sound.SynthKey.StartsWith("weapon:", StringComparison.OrdinalIgnoreCase))
        {
            string id = sound.SynthKey["weapon:".Length..];
            if (WeaponRegistry.TryGet(id, out var weapon))
                return WeaponSynth.MuzzleBlast(WeaponProfile.From(weapon), seed);
        }
        // A crowd, which is many impacts rather than one. Named for the same reason a gunshot is:
        // the four characters describe one event and a thousand people clapping is not one event.
        if (Applause.TryParseKey(sound.SynthKey, out var crowd))
            return Applause.Render(crowd, TransientSynth.SampleRate, seed);
        if (sound.SynthKey == Applause.ClapKey)
            return Applause.RenderClap(TransientSynth.SampleRate, seed);
        if (DoorKnock.TryParseKey(sound.SynthKey, out int knocks))
            return DoorKnock.Render(knocks, TransientSynth.SampleRate, seed);
        // A car door: a mechanism fitted to a recording, which one knock and one ring could not be.
        if (CarDoor.TryParseKey(sound.SynthKey, out bool closing))
            return CarDoor.Render(closing, TransientSynth.SampleRate, seed);

        return TransientSynth.Render(sound, seed);
    }

    /// <summary>
    /// A recorded line at the mixer's rate and at the level the server placed it from.
    ///
    /// The server sends a level on the basis that every line is equally loud, so each is brought to
    /// <see cref="Speech.BufferLoudnessLufs"/> here: a take that came out quieter or hotter is not a
    /// person talking quieter or louder. A line that is missing plays nothing and says so once.
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

    /// <summary>Linear interpolation. The shipped lines are already at the mixer's rate; this is for
    /// a file that is not, so it plays at the right pitch rather than not at all.</summary>
    internal static float[] Resample(float[] pcm, int from, int to)
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

    /// <summary>
    /// The id of this sound as a surface of this roughness hands it back: not a copy, a WASH. Every
    /// echo of a one-off sound used to be the sound itself, placed at the mirror point — a clean
    /// second gunshot off a brick wall, which is not what a wall does. A rough surface returns the
    /// sound from a patch of itself with every part a little later than the next, so the echo is
    /// the sound smeared through the same diffuser the engine echoes use (EchoDiffuser: about a
    /// millisecond for glass and polished steel, a dozen for brick). Five steps of roughness, each
    /// rendered once per sound and seed and kept.
    /// </summary>
    /// <summary>
    /// What an echo of a one-off sound plays. The MIRROR share is the sound itself, arriving from the
    /// wall: a shot off a facade is a crack, not a smear. The surface's roughness is already paid for
    /// in the geometry — the mirror carries (1 - scattering) of what the face returns and the rest is
    /// the diffuse taps spread across the face (ImageSource), which is what gives the echo the size of
    /// the wall. Only those taps, the scattered share, are smeared. Every echo used to go through the
    /// diffuser, so a shot off a steel panel or concrete — the shortest all-pass delays, a fraction of
    /// a millisecond — came back as a ringing, "processed sounding" copy (Cody, 2026-09-28: "it should
    /// be a crack, but a crack that comes from the wall, not smeared").
    /// </summary>
    internal static (float LowDb, float HighDb) SpecularLoss(float scattering, int bounces)
        => ImageSource.SpecularBandLossDb(scattering, bounces);

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
            System.Threading.Tasks.Task.Run(() => _rendered.Enqueue((id, Diffuse(RenderOne(sound, seed), step / 4f, seed))));
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

    /// <summary>Forgets everything queued. Called on a map change, where the positions mean nothing
    /// any more and the things that made them are gone.</summary>
    public void Clear() { _pending.Clear(); _awaitingRender.Clear(); _following.Clear(); }

    /// <summary>
    /// A sound's parameters ARE its identity.
    ///
    /// Two doors of the same material and size shutting at the same speed genuinely are the same
    /// sound, so they should be the same buffer — otherwise a corridor of identical doors renders one
    /// each. Quantised before hashing, because two values a thousandth of a decibel apart are not two
    /// different sounds and should not be two different buffers.
    /// </summary>
    private static string IdFor(TransientSound sound, int seed)
    {
        int hz = (int)MathF.Round(sound.Hz);
        int level = (int)MathF.Round(sound.LevelDb);
        int decay = (int)MathF.Round(sound.DecaySeconds * 100f);
        int noise = (int)MathF.Round(sound.Noisiness * 20f);
        // The seed is coarse on purpose: a handful of variations of each sound, not one per event.
        // A named model is its own identity — two shots from one rifle are one buffer.
        // A recording is one take: four seeds of it would be four identical buffers.
        if (Speech.TryParseKey(sound.SynthKey, out _)) return $"synth:{sound.SynthKey}";
        if (!string.IsNullOrEmpty(sound.SynthKey)) return $"synth:{sound.SynthKey}:{seed & 3}";
        return $"synth:{sound.Character}:{hz}:{level}:{decay}:{noise}:{seed & 3}";
    }
}
