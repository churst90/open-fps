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

    /// <summary>
    /// The id the simulator files its answer for a sounding entity under: somebody talking, or the
    /// thing a one-shot came from. Never a voice — only a question to the worker — and in a band of its
    /// own below every voice band (the horns' at -1,200,000 is the lowest).
    ///
    /// A transient voice is never asked about by the per-frame loop (ClientAudioSystem skips ids below
    /// -5000), so a voice and a one-shot from somewhere nothing else was sounding fell back to the
    /// hand-rolled tracer for the whole of its life. Asked about by its source instead, a talker is
    /// carried by the simulator from the line's first tick on, and so is the next sound from the
    /// same place (<see cref="AsyncAcousticWorker.TryGetNearby"/>).
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

    public WorldAudioPlayer(AudioEngineFacade audio, SpatialAcoustics acoustics)
    {
        _audio = audio;
        _acoustics = acoustics;
        PrewarmDoors();
    }

    /// <summary>
    /// A door model is a simulation that takes seconds of a core to render, far longer than a first
    /// hearing waits (<see cref="MaxRenderLateness"/>), so the first door anyone opened would be silent.
    /// Each knob door character's opening and normal close is rendered at start, in the background, four
    /// at a time and openings first, for the prefab's width and the two the city scales it to (1.1 and
    /// 1.4 m); then the push bar's, and the patio and automatic doors' at the city's sizes. A gentle or
    /// hard close, or a door of another size, renders when first heard.
    /// </summary>
    private void PrewarmDoors()
    {
        var keys = new List<string>();
        foreach (bool closing in new[] { false, true })
            foreach (float width in new[] { 1.1f, 1.4f, 0.9f })
                for (int v = 0; v < KnobDoor.Variants; v++)
                    keys.Add(KnobDoor.Key(closing, KnobDoor.Construction.HollowCore, v, 0.9f, KnobDoor.Shut.Normal, width, 2.1f));
        // The steel push-bar door at its prefab's size: each character's push and its closer's latch.
        foreach (bool closing in new[] { false, true })
            for (int v = 0; v < PushBarDoor.Variants; v++)
                keys.Add(PushBarDoor.Key(closing, v, 1.4f, 1.0f, 2.1f));
        // The sliding doors at the sizes the city builds them: a patio leaf 1.0 m wide slid in 1.4 s, an
        // automatic leaf 1.15 m wide at its controller's own times.
        foreach (bool closing in new[] { false, true })
            for (int v = 0; v < SlidingDoor.Variants; v++)
            {
                keys.Add(SlidingDoor.Key(SlidingDoor.Kind.Patio, closing, v, 1.4f, 1.0f, 2.1f));
                keys.Add(SlidingDoor.Key(SlidingDoor.Kind.Automatic, closing, v, SlidingDoor.AutomaticSeconds(1.15f, !closing), 1.15f, 2.1f));
            }
        // A car's windows, each character, every stroke the window command makes from a window at rest:
        // fully down and up, and to and from half way. A window turned round while it is moving starts
        // from a quarter that is rendered when first heard.
        for (int v = 0; v < CarWindow.Variants; v++)
            foreach (var (from, to) in new[] { (0f, 1f), (1f, 0f), (0f, 0.5f), (0.5f, 0f), (0.5f, 1f), (1f, 0.5f) })
                keys.Add(CarWindow.Key(v, from, to));
        var gate = new System.Threading.SemaphoreSlim(4);
        foreach (string key in keys)
        {
            string id = $"synth:{key}";
            if (!_rendering.Add(id)) continue;
            System.Threading.Tasks.Task.Run(async () =>
            {
                await gate.WaitAsync();
                try { _rendered.Enqueue((id, RenderDoorKey(key))); }
                finally { gate.Release(); }
            });
        }
    }

    /// <summary>A door model's sound by its key's prefix. (Every key went through the knob door's renderer,
    /// which does not know a push bar's key and gave back sixteen samples of silence: the prewarmed push-bar
    /// doors were silent.)</summary>
    private static float[] RenderDoorKey(string key)
        => key.StartsWith(PushBarDoor.KeyPrefix, StringComparison.Ordinal) ? PushBarDoor.RenderKey(key, TransientSynth.SampleRate)
         : key.StartsWith(SlidingDoor.KeyPrefix, StringComparison.Ordinal) ? SlidingDoor.RenderKey(key, TransientSynth.SampleRate)
         : key.StartsWith(CarWindow.KeyPrefix, StringComparison.Ordinal) ? CarWindow.RenderKey(key, TransientSynth.SampleRate)
         : KnobDoor.RenderKey(key, TransientSynth.SampleRate);

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
                // back in time — see MaxRenderLateness. Dropping every first hearing would silence
                // whatever is RARE: each sound has four seed variants and each is a first hearing
                // once, so a door material used only a handful of times would never be heard at all.
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
    /// <summary>The listener's own entity, and where the client has its body now (feet, facing): a
    /// sound made on your own body is placed on you, not where the server last had you.</summary>
    public int SelfId { get; set; } = -1;
    public Func<(Vector3 Feet, Quaternion Rotation)>? Self { get; set; }

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
        // of the list this loop has already walked past, and left for the next update they would go
        // out a whole frame late. Each is delayed by its own path from the moment it is submitted, so
        // a reflection due 6-25 ms after a clap would arrive 45-65 ms after it, a cluster of slaps of
        // its own. Played in the same pass, every copy starts from the same moment as its source.
        for (int pass = 0; pass < 3; pass++)
        {
        bool playedAny = false;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            if (now < item.DueAt) continue;
            playedAny = true;
            _pending.RemoveAt(i);
            if (!item.IsReflection && item.Sound.OnBody && BodyNow(world, item.SourceEntityId, out var feet, out var facing))
            {
                var onBody = item.Sound;
                onBody.Position = feet + Vector3.Transform(onBody.BodyOffset, facing);
                item = item with { Sound = onBody };
            }

            if (_trace)
                Serilog.Log.Information("[WAUDIO] play {Id}{Echo} {Db:F0} dB at {Dist:F1} m, {Late:F2}s after it was due "
                                      + "({Q} still queued)",
                    item.SoundId, item.IsReflection ? " (echo)" : "", item.Sound.LevelDb,
                    Vector3.Distance(listenerPosition, item.Sound.Position), now - item.DueAt, _pending.Count);

            // A person talking gets no echo copies. Each copy is the whole line again from a fixed
            // mirror point, while the speaker walks on: heard as a room passing you rather than a
            // person. The street's answer to a voice comes from the reverb, which follows the listener.
            bool spoken = Speech.TryParseKey(item.Sound.SynthKey, out _);
            // Made inside a vehicle's cabin (a window's motor, in its door): it goes where the vehicle
            // goes, and anyone outside hears it through the cabin's walls. Nor is it copied off the walls
            // round about: the cabin is its room, and the street's walls are on the far side of the glass.
            // Not a driver's yell, which is said out of the window they have wound down to say it.
            bool inCabin = InCabin(world, item.SourceEntityId, item.Sound.Position, out var cabinCar, out var cabinVehicle)
                           && !item.IsReflection && !spoken;
            if (!item.IsReflection && !spoken && !inCabin)
            {
                QueueEarlyEchoes(item, world, listenerPosition);
                QueueReflections(item, reflections, listenerPosition, now);
                QueueHigherOrderEchoes(item, world, listenerPosition);
            }

            var path = _acoustics.CalculateAcousticPath(world, item.SourceEntityId,
                                                        listenerPosition, item.Sound.Position);
            // An echo is placed at its mirror image, which is BEHIND the wall it came off, and the ray
            // from the listener to that point goes through that very wall: traced like a direct sound,
            // every echo would come out 40-60 dB down, even of a shot in plain view. Both legs of an echo's route were already checked clear when it
            // was found (ImageSource, EarlyReflections), so, as for the engines' echoes
            // (EngineReflections.ApplyPath), it keeps the air over its own path and nothing else.
            if (item.IsReflection)
                path = path with
                {
                    Occlusion = 0f, EqMid = 1f,
                    EqLow = MathF.Pow(10f, item.EchoLowDb / 20f), EqHigh = MathF.Pow(10f, item.EchoHighDb / 20f),
                    ApertureFactor = 1f, TransmissionBleed = 0f, ApparentPosition = item.Sound.Position,
                };
            // Start where the simulator will put it, not where the hand-rolled tracer guesses: its
            // answer for this sound's source, or for the nearest source it heard a moment ago, moved
            // to this one.
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
            // Placed at its own SIZE if it has one. A grandstand full of people is eight metres
            // across, and inside that the level is flat; beyond it, it falls away exactly as a point
            // source of the same power would, which is what the gain compensation in there is for.
            // And ask about where it came from, so the next sound from there — the rest of this line,
            // the next step, the next shot — starts on the simulator's answer.
            if (!item.IsReflection && item.SourceEntityId >= 0)
                AskAbout(item.SourceEntityId, listenerPosition, item.Sound.Position);
            var placed = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres);
            // A COPY keeps its source's placement. The placement compresses level differences between
            // sounds (Loudness.DynamicRangeCompression, 0.45 shipped) — right between a rifle and a
            // footstep, wrong between a sound and its own reflection: placed on its own, an echo
            // handed in 14 dB down would come out 6 dB down, 4-8 dB too loud against what it is a
            // copy of. Placed as the source and
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
                // Every direct sound sends to the reverb, indoors and out; a reflection never does (the
                // provider skips IsReflection voices). An echo is already the place answering, and
                // sent to the tail as well it would be counted twice.
                // An EVENT: it belongs to a moment. If the budget has no room for it now there is no
                // playing it later — see VoiceManager.Process, which drops one that did not win a slot
                // rather than keeping it queued to fire from a stale position minutes afterwards.
                IsEvent = true,
                LevelDb = item.IsReflection ? 0f : item.Sound.LevelDb,
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
            if (!inCabin && HearsTheGround(item, spoken)) Ground?.Invoke(ref emitter, world);
            _audio.Submit(emitter);

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
    /// 10.1051/aacus/2024002: below 800 Hz it is stronger still). Rendered, it flanges, summed into
    /// the voice's direction and again from its own direction below. A real
    /// talker on a pavement does not sound like that, so something the ear uses is missing: the
    /// torso's shadow on sound from below, the talker's own vertical radiation, or the small
    /// movements that keep a comb from standing still. Until one is measured, a voice has none.
    /// </summary>
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
        public double Until;
        public double StartedAt;
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
            if (now >= f.Until || !world.Entities.TryGetValue(f.SourceEntityId, out var speaker)
                || (now - f.StartedAt > StartGraceSeconds && !_audio.IsPlaying(f.Emitter.EntityId)))
            {
                _following.RemoveAt(i);
                continue;
            }
            var at = f.InCabin ? speaker.Transform.Position + Vector3.Transform(f.Offset, speaker.Transform.Rotation)
                               : speaker.Transform.Position + f.Offset;
            // The simulator's answer for the speaker, as for any other source; the hand-rolled tracer
            // only until it has one.
            AskAbout(f.SourceEntityId, listenerPosition, at);
            if (!TryAsked(f.SourceEntityId, at, out var path))
                path = _acoustics.CalculateAcousticPath(world, f.SourceEntityId, listenerPosition, at);
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
            if (f.InCabin)
            {
                // Where the vehicle is now decides whether you are inside it with the sound: you may have
                // got in, or out, while the window was moving.
                e.InsideListenersVehicle = f.SourceEntityId == ListenerVehicleId;
                if (OpenFPS.Client.AudioEngine.Acoustics.CabinWalls.Vehicle(speaker) is { } vehicle)
                    ThroughCabin(ref e, speaker, vehicle, now);
            }
            else Facing(ref e, speaker.Transform.Rotation, listenerPosition);
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
        EarlyReflections.Find(item.Sound.Position, listenerPosition, solids, _higher, AudioPhysics.CurrentSpeedOfSound,
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
    // the middle of the head and does not move when the head turns (interaural correlation 0.85-0.95
    // in a capture). What places a real room round you is its first few reflections, each off one
    // wall, each from that wall's direction.
    //
    // So in a room, a one-off sound's early reflections are voices of their own, mirrored through
    // the walls round it (EarlyReflections, to third order), each placed at its image through the
    // HRTF, and the room's traced stage plays only the late tail (TracedReverbDsp, LateTailIr).
    // The floor under the source is left out: the voice already carries its own ground reflection.

    /// <summary>How long the placed reflections run before the tail takes over, seconds. The traced
    /// tail fades in from 50 to 100 ms after the sound (LateTailIr); the two overlap a little.</summary>
    internal const float RoomEchoWindowSeconds = 0.08f;

    /// <summary>At most this many placed reflections per sound: the first order of a box room is six
    /// and the loudest second orders follow.</summary>
    internal const int MaxRoomEchoes = 12;

    /// <summary>
    /// How many SECOND-order copies a sound's room gets as clean copies, beyond its first order. The
    /// rest of the copies of copies are the room's tail, which the trace already is. Up to twelve
    /// clean copies of one dry clap were twelve separate clicks to the ear, which hears copies of an
    /// impulse as echoes from a few milliseconds on, where real reflections fuse.
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

    private void QueueEarlyEchoes(in Pending item, WorldSnapshot world, Vector3 listenerPosition)
    {
        var solids = _acoustics.ReflectionSolids(world);
        if (solids.Count == 0) return;
        Vector3 src = item.Sound.Position;
        EarlyReflections.Find(src, listenerPosition, solids, _room, AudioPhysics.CurrentSpeedOfSound,
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
            // The scattered share of a first-order wall, as the wall's wash rather than a copy: the
            // sound smeared by that surface's roughness, from the same place, carrying what the
            // mirror does not. A wall that scatters little sends back almost all of it as the crack.
            if (e.WashGain >= ImageSource.MinGain) QueueWash(item, a, e.WashGain);
            float gain = e.MirrorGain;
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
        // Loudest first. Find hands its arrivals back in surface order, and with more inside the
        // window than there are voices (twenty-two in flat 01F, twelve voices) the first twelve BY
        // SURFACE were taken, and which walls answered depended on their order in the map.
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
                                            AudioPhysics.CurrentSpeedOfSound, found, DiffuseTaps);
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
        // A knob door: the door simulated, its character named in the key.
        if (sound.SynthKey != null && sound.SynthKey.StartsWith(KnobDoor.KeyPrefix, StringComparison.Ordinal))
            return KnobDoor.RenderKey(sound.SynthKey, TransientSynth.SampleRate);
        if (sound.SynthKey != null && sound.SynthKey.StartsWith(PushBarDoor.KeyPrefix, StringComparison.Ordinal))
            return PushBarDoor.RenderKey(sound.SynthKey, TransientSynth.SampleRate);
        if (sound.SynthKey != null && sound.SynthKey.StartsWith(SlidingDoor.KeyPrefix, StringComparison.Ordinal))
            return SlidingDoor.RenderKey(sound.SynthKey, TransientSynth.SampleRate);
        // A car's power window: its motor, worm and glass simulated through the stroke the key names.
        if (sound.SynthKey != null && sound.SynthKey.StartsWith(CarWindow.KeyPrefix, StringComparison.Ordinal))
            return CarWindow.RenderKey(sound.SynthKey, TransientSynth.SampleRate);

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
    /// the wall. Only those taps, the scattered share, are smeared. Through the diffuser, a shot off
    /// a steel panel or concrete — the shortest all-pass delays, a fraction of a millisecond — comes
    /// back as a ringing, processed copy; it should be a crack that comes from the wall.
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
        // A knob door's key already names its door; four seeds of it would be four identical renders.
        if (sound.SynthKey != null && (sound.SynthKey.StartsWith(KnobDoor.KeyPrefix, StringComparison.Ordinal)
                                       || sound.SynthKey.StartsWith(PushBarDoor.KeyPrefix, StringComparison.Ordinal)
                                       || sound.SynthKey.StartsWith(SlidingDoor.KeyPrefix, StringComparison.Ordinal)
                                       || sound.SynthKey.StartsWith(CarWindow.KeyPrefix, StringComparison.Ordinal)))
            return $"synth:{sound.SynthKey}";
        if (!string.IsNullOrEmpty(sound.SynthKey)) return $"synth:{sound.SynthKey}:{seed & 3}";
        return $"synth:{sound.Character}:{hz}:{level}:{decay}:{noise}:{seed & 3}";
    }
}
