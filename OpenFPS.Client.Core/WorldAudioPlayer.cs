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
    /// The event that discovers a new sound is dropped rather than delayed, because a one-shot belongs
    /// to a moment and a rendered-too-late clap is a clap in the wrong place. Every one after it — the
    /// same crowd a second later, the next door of the same kind — finds the buffer waiting.
    /// </summary>
    private readonly HashSet<string> _rendering = new();
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

    public void Receive(WorldAudioEvent message, double now)
    {
        if (message.Sounds == null) return;
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
                continue;
            }
            _pending.Add(new Pending
            {
                Sound = sound,
                SoundId = id,
                SourceEntityId = message.SourceEntityId,
                DueAt = now + Math.Max(0f, sound.DelaySeconds),
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

            if (!item.IsReflection) QueueReflections(item, reflections, listenerPosition, now);

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
                EnableReverb = true,
                // An EVENT: it belongs to a moment. If the budget has no room for it now there is no
                // playing it later — see VoiceManager.Process, which drops one that did not win a slot
                // rather than keeping it queued to fire from a stale position minutes afterwards.
                IsEvent = true,
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
            _pending.Add(new Pending
            {
                Sound = echo,
                SoundId = item.SoundId,
                SourceEntityId = item.SourceEntityId,
                // LATER THAN THE SOUND IT IS A COPY OF — by exactly the extra distance it travelled.
                //
                // This was `item.DueAt`, so every echo of every world event arrived on the same sample
                // as the direct sound. Reflection.DelaySeconds says of itself "seconds later than the
                // direct sound: this is the whole point", and it was being thrown away. What that
                // produces is not an echo: it is the direct sound with three or four copies of itself
                // summed onto its own transient — louder, smeared, and arriving as ONE bang. Near a
                // building, where there are surfaces to find, that is every world sound.
                DueAt = item.DueAt + Math.Max(0f, r.DelaySeconds),
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

    /// <summary>Forgets everything queued. Called on a map change, where the positions mean nothing
    /// any more and the things that made them are gone.</summary>
    public void Clear() => _pending.Clear();

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
