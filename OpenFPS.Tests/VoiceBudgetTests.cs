using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common.Components;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Who gets a voice, and what happens to whoever does not.
///
/// This is the one part of the audio engine with no acoustics in it — it is bookkeeping — and it had
/// no tests at all, because it used to need FMOD and a sound card to construct. What went wrong in it
/// was invisible from everywhere else: a ONE-SHOT that lost the budget was kept in the queue and
/// re-scored for ever, and fired whenever the listener moved somewhere that made its score win. A clap
/// from a minute ago, played from a position computed for a place you have walked away from — heard on
/// the speedway as reflections piling up in a spot where nothing was happening.
/// </summary>
public class VoiceBudgetTests
{
    private readonly ITestOutputHelper _o;
    public VoiceBudgetTests(ITestOutputHelper o) => _o = o;

    /// <summary>A mixer that remembers what it was told, and nothing else.</summary>
    private sealed class FakeMixer : IVoiceSink
    {
        public readonly HashSet<int> Playing = new();
        public readonly List<int> Started = new();
        public readonly List<int> Stopped = new();

        public bool IsPlaying(int entityId) => Playing.Contains(entityId);
        public void PlayPhysicalSoundDirect(SpatialEmitter e) { Playing.Add(e.EntityId); Started.Add(e.EntityId); }
        public void UpdateSpatialAttributes(SpatialEmitter e) { }
        public void StopSoundImmediate(int entityId) { Playing.Remove(entityId); Stopped.Add(entityId); }

        /// <summary>The sound reached its end on its own, as a one-shot does.</summary>
        public void FinishedOnItsOwn(int entityId) => Playing.Remove(entityId);
    }

    private static SpatialEmitter Event(int id, Vector3 at, float priority = 2f) => new()
    {
        EntityId = id,
        SoundId = "knock",
        Mode = PlaybackMode.Single,
        Type = EmitterType.WorldLocked,
        Position = at,
        Range = 500f,
        MinDistance = 2f,
        Volume = 1f,
        Priority = (int)priority,
        IsEvent = true,
    };

    private static SpatialEmitter Engine(int id, Vector3 at, float priority = 5f) => new()
    {
        EntityId = id,
        SoundId = "engine",
        Mode = PlaybackMode.LoopOne,
        Type = EmitterType.WorldLocked,
        Position = at,
        Range = 500f,
        MinDistance = 3f,
        Volume = 1f,
        Priority = (int)priority,
        IsEvent = false,
    };

    /// <summary>
    /// A one-shot that loses the budget is forgotten, not stored.
    ///
    /// The queue must not grow, and — this is the part that was audible — the loser must never be
    /// played later. Its moment has gone.
    /// </summary>
    [Fact]
    public void AnEventThatDoesNotWinAVoiceIsForgottenRatherThanQueued()
    {
        var mixer = new FakeMixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 2);
        var listener = Vector3.Zero;

        // Two near events fill the budget; twenty far ones cannot have it.
        for (int frame = 0; frame < 5; frame++)
        {
            voices.Submit(Event(1, new Vector3(2, 0, 0)));
            voices.Submit(Event(2, new Vector3(3, 0, 0)));
            for (int i = 0; i < 20; i++) voices.Submit(Event(100 + i, new Vector3(0, 0, 200 + i)));
            voices.Process(listener);
        }

        _o.WriteLine($"after five frames of 22 events each: {voices.SubmissionCount} submissions still queued, " +
                     $"{mixer.Started.Count} started");
        Assert.True(voices.SubmissionCount <= 2,
            $"{voices.SubmissionCount} submissions are still queued; a one-shot that lost the budget is being kept.");

        // Now walk over to where the far ones were. Nothing from before may speak.
        int startedBefore = mixer.Started.Count;
        for (int frame = 0; frame < 5; frame++) voices.Process(new Vector3(0, 0, 205));
        var late = mixer.Started.Skip(startedBefore).ToList();
        _o.WriteLine($"after walking to where they were: {late.Count} of them fired late");
        Assert.Empty(late);
    }

    /// <summary>
    /// ...but a CONTINUOUS source that loses the budget keeps its place and comes back.
    ///
    /// The distinction is the whole point: a car that is out-scored is still there, still making a
    /// noise, and gets a voice again when one frees. Dropping those would be cars falling silent as
    /// they went round the back.
    /// </summary>
    [Fact]
    public void AnEngineThatDoesNotWinAVoiceKeepsItsPlaceAndComesBack()
    {
        var mixer = new FakeMixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);

        voices.Submit(Engine(1, new Vector3(5, 0, 0)));
        voices.Submit(Engine(2, new Vector3(300, 0, 0)));
        voices.Process(Vector3.Zero);

        Assert.Contains(1, mixer.Playing);
        Assert.DoesNotContain(2, mixer.Playing);
        Assert.Equal(2, voices.SubmissionCount);   // the far car is still known about

        // The near car goes away; the far one should get the voice without being re-submitted.
        mixer.FinishedOnItsOwn(1);
        voices.RequestStop(1);
        voices.Submit(Engine(2, new Vector3(300, 0, 0)));
        voices.Process(Vector3.Zero);
        voices.Submit(Engine(2, new Vector3(300, 0, 0)));
        voices.Process(Vector3.Zero);

        _o.WriteLine($"started: {string.Join(", ", mixer.Started)}");
        Assert.Contains(2, mixer.Started);
    }

    /// <summary>
    /// The three bands of negative voice ids must not overlap.
    ///
    /// They did. A transient's id was a hash of the sound modulo a million — −1,000 to −1,001,000 —
    /// straight across the engine-echo band at −600,000 and the borrowed-voice band at −700,000, so a
    /// footstep could take over a car's reflection voice or a distant car's voice. Heard as the
    /// reflection of a bike that had long since gone past, and as cars going quiet for no reason.
    /// </summary>
    [Fact]
    public void TheVoiceIdBandsDoNotOverlap()
    {
        int transientHighest = WorldAudioPlayer.TransientVoiceId(0);
        int transientLowest = WorldAudioPlayer.TransientVoiceId(WorldAudioPlayer.TransientVoiceSpan - 1);
        _o.WriteLine($"transients   {transientLowest} .. {transientHighest}");
        _o.WriteLine($"engine echoes {EngineReflections.EchoVoiceIdBase} and below");
        _o.WriteLine($"borrowed      {ClientAudioSystem.DistantVoiceBase} and below");

        Assert.True(transientHighest < 0, "a transient id must be negative so it cannot collide with an entity.");
        Assert.True(transientLowest > EngineReflections.EchoVoiceIdBase,
            "the transient band reaches into the engine-echo band.");
        Assert.True(transientLowest > ClientAudioSystem.DistantVoiceBase,
            "the transient band reaches into the borrowed-voice band.");
        Assert.False(EngineReflections.IsEchoVoice(transientLowest),
            "a transient is being classified as an engine echo.");

        // And consecutive transients are different voices, which is what stops a sound and its own
        // reflection replacing each other in the submission queue.
        var seen = new HashSet<int>();
        for (int i = 1; i <= 1000; i++) Assert.True(seen.Add(WorldAudioPlayer.TransientVoiceId(i)));
    }

    /// <summary>
    /// A transient's range must not fade it out before it stops being audible.
    ///
    /// The mixer fades a voice to nothing over the last quarter of its range, so a range shorter than
    /// the level can carry is not a saving, it is a silence — at 250 m the fade began at 187, and the
    /// grandstand is 219 m from the spawn.
    /// </summary>
    [Fact]
    public void ALoudTransientIsNotFadedOutInsideTheMapItIsOn()
    {
        float level = OpenFPS.Common.Applause.LevelDb(300, 0.7f);
        float range = MathF.Min(WorldAudioPlayer.MaxRange, OpenFPS.Common.Loudness.AudibleRange(level));
        float reference = OpenFPS.Common.Loudness.Place(level, OpenFPS.Common.Applause.SpreadRadiusMetres(300)).ReferenceDistance;
        float fadeStarts = range - 0.25f * (range - reference);
        _o.WriteLine($"a 300-person crowd at {level:F1} dB: range {range:F0} m, fade starts at {fadeStarts:F0} m");
        Assert.True(fadeStarts > 500f,
            $"a crowd this loud starts fading at {fadeStarts:F0} m, which is inside a racetrack.");
    }
}
