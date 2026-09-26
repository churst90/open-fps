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

        if (_pending.Count == 0) return;

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            if (now < item.DueAt) continue;
            _pending.RemoveAt(i);

            if (_trace)
                Serilog.Log.Information("[WAUDIO] play {Id}{Echo} {Db:F0} dB at {Dist:F1} m, {Late:F2}s after it was due "
                                      + "({Q} still queued)",
                    item.SoundId, item.IsReflection ? " (echo)" : "", item.Sound.LevelDb,
                    Vector3.Distance(listenerPosition, item.Sound.Position), now - item.DueAt, _pending.Count);

            if (!item.IsReflection)
            {
                _enclosedNow = ListenerEnclosed(world, listenerPosition);
                QueueReflections(item, reflections, listenerPosition, now);
                QueueHigherOrderEchoes(item, world, listenerPosition);
            }

            var path = _acoustics.CalculateAcousticPath(world, item.SourceEntityId,
                                                        listenerPosition, item.Sound.Position);
            // Placed at its own SIZE if it has one. A grandstand full of people is eight metres
            // across, and inside that the level is flat; beyond it, it falls away exactly as a point
            // source of the same power would, which is what the gain compensation in there is for.
            var placed = Loudness.Place(item.Sound.LevelDb, item.Sound.ExtentMetres);

            _audio.Submit(new SpatialEmitter
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
                EnableReverb = !(IsImpulse(item.Sound) && !ListenerEnclosed(world, listenerPosition))
                               || OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedActive,
                // An EVENT: it belongs to a moment. If the budget has no room for it now there is no
                // playing it later — see VoiceManager.Process, which drops one that did not win a slot
                // rather than keeping it queued to fire from a stale position minutes afterwards.
                IsEvent = true,
                InsideListenersVehicle = item.SourceEntityId >= 0 && item.SourceEntityId == ListenerVehicleId,
            });
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
    private bool _enclosedNow;

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
        float direct = MathF.Max(1f, Vector3.Distance(item.Sound.Position, listenerPosition));
        int added = 0;
        foreach (var a in _higher)
        {
            if (a.Order < 2 || !EarlyReflections.IsSeparateEvent(a)) continue;
            // Loud enough against the sound it is a copy of to be heard as a second event at all.
            if (a.GainMid < ImageSource.EchoAudibleRatio) continue;
            // Placed at the image, which is the path length away: undo the spreading the engine will
            // apply there, as QueueReflections does, so it is not applied twice.
            float gain = Math.Clamp(a.GainMid * a.PathLength / direct, 0f, 1f);
            if (gain < ImageSource.MinGain) continue;
            var echo = item.Sound;
            echo.Position = a.ImagePosition;
            echo.LevelDb = item.Sound.LevelDb + 20f * MathF.Log10(gain);
            QueueEcho(new Pending
            {
                Sound = echo,
                SoundId = DiffusedId(item, a.Scattering),
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

    private void QueueReflections(in Pending item, EngineReflections? reflections,
                                  Vector3 listenerPosition, double now)
    {
        if (reflections == null || reflections.SurfaceCount == 0) return;
        // Traced, indoors: the room's traced response carries these; outdoors a short sound's own
        // echoes stay, because a shot up the street is not where you stand.
        if (OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.TracedActive && _enclosedNow) return;

        Span<Reflection> found = stackalloc Reflection[MaxEchoes];
        // Sampled across a scattering face as well as mirrored through it.
        // Through the echo system's own search, which only mirrors through faces near the path and
        // tests both legs for something standing in the way. Doing it straight out of ImageSource
        // skipped the obstruction test, and an echo that cannot be blocked is the one thing left when
        // the direct sound is — which is what "I only hear the reflections of the clapping" was.
        int n = reflections.FindReflections(item.Sound.Position, listenerPosition,
                                            AudioPhysics.SpeedOfSound, found, DiffuseTaps);
        if (n == 0) return;

        float directDist = MathF.Max(1f, Vector3.Distance(item.Sound.Position, listenerPosition));
        for (int i = 0; i < n; i++)
        {
            var r = found[i];
            // Loud enough, against the sound it is a copy of, to be heard as a second event at all.
            if (r.Gain < ImageSource.EchoAudibleRatio) continue;

            float gain = Math.Clamp(r.Gain * r.PathLength / directDist, 0f, 1f);
            if (gain < ImageSource.MinGain) continue;

            var echo = item.Sound;
            echo.Position = r.ApparentPosition;
            echo.LevelDb = item.Sound.LevelDb + 20f * MathF.Log10(gain);
            QueueEcho(new Pending
            {
                Sound = echo,
                SoundId = DiffusedId(item, r.Scattering),
                Seed = item.Seed,
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
    private static float[] RenderOne(TransientSound sound, int seed)
    {
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

        return TransientSynth.Render(sound, seed);
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
    public void Clear() { _pending.Clear(); _awaitingRender.Clear(); }

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
        if (!string.IsNullOrEmpty(sound.SynthKey)) return $"synth:{sound.SynthKey}:{seed & 3}";
        return $"synth:{sound.Character}:{hz}:{level}:{decay}:{noise}:{seed & 3}";
    }
}
