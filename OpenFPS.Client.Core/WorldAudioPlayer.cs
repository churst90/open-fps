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
    }

    private readonly AudioEngineFacade _audio;
    private readonly SpatialAcoustics _acoustics;
    private readonly List<Pending> _pending = new();
    private readonly HashSet<string> _registered = new();

    /// <summary>How far a transient carries at most. The per-sound level decides the rest; this only
    /// stops a very loud one being computed against the whole map.</summary>
    private const float MaxRange = 250f;

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
    public void Receive(WorldAudioEvent message, double now)
    {
        if (message.Sounds == null) return;
        foreach (var sound in message.Sounds)
        {
            string id = IdFor(sound, message.Seed);
            if (_registered.Add(id))
            {
                var pcm = RenderOne(sound, message.Seed);
                if (!_audio.RegisterSynthesisedSound(id, TransientSynth.ToPcm16(pcm), TransientSynth.SampleRate))
                {
                    _registered.Remove(id);      // the engine is not up yet; try again next time
                    continue;
                }
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
    public void Update(WorldSnapshot world, Vector3 listenerPosition, double now)
    {
        if (_pending.Count == 0) return;

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            if (now < item.DueAt) continue;
            _pending.RemoveAt(i);

            var path = _acoustics.CalculateAcousticPath(world, item.SourceEntityId,
                                                        listenerPosition, item.Sound.Position);
            var placed = Loudness.Place(item.Sound.LevelDb);

            _audio.Submit(new SpatialEmitter
            {
                // Negative ids so a transient can never collide with an entity's own voice — one door
                // shutting must not stop the engine of the car it belongs to.
                EntityId = -Math.Abs(item.SoundId.GetHashCode() % 1_000_000) - 1000,
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
                Priority = 2,
                EnableReverb = true,
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
