using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common.Components;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The voice budget, held to the edges Stryker found untested (2026-09-24): what is counted as
/// sounding, what is forgotten and when, start and stop sounds, a folder that loops, and the level a
/// voice is ranked on.
/// </summary>
public class VoiceManagerMutationTests
{
    /// <summary>A mixer that remembers everything it was told.</summary>
    private sealed class Mixer : IVoiceSink
    {
        public readonly HashSet<int> Playing = new();
        public readonly List<SpatialEmitter> Started = new();
        public readonly List<int> Stopped = new();
        public readonly List<SpatialEmitter> Placed = new();
        public readonly List<int> FadeCancelled = new();
        public readonly HashSet<int> Fading = new();
        public int FadeFrames = 2;
        private readonly Dictionary<int, int> _fadeAge = new();

        public bool IsPlaying(int entityId) => Playing.Contains(entityId);
        public void PlayPhysicalSoundDirect(SpatialEmitter e) { Playing.Add(e.EntityId); Started.Add(e); }
        public void UpdateSpatialAttributes(SpatialEmitter e) => Placed.Add(e);
        public void StopSoundImmediate(int entityId) { Playing.Remove(entityId); Stopped.Add(entityId); Fading.Remove(entityId); _fadeAge.Remove(entityId); }

        public bool FadeOut(int entityId)
        {
            if (!Playing.Contains(entityId)) return true;
            Fading.Add(entityId);
            int age = _fadeAge.GetValueOrDefault(entityId) + 1;
            _fadeAge[entityId] = age;
            return age >= FadeFrames;
        }

        public void CancelFade(int entityId) { FadeCancelled.Add(entityId); Fading.Remove(entityId); _fadeAge.Remove(entityId); }

        /// <summary>The sound reached its end on its own.</summary>
        public void Ends(int entityId) => Playing.Remove(entityId);

        public IEnumerable<SpatialEmitter> StartsOf(int id) => Started.Where(e => e.EntityId == id);
    }

    private static SpatialEmitter Event(int id, Vector3 at, float volume = 1f) => new()
    {
        EntityId = id, SoundId = "knock", Mode = PlaybackMode.Single, Type = EmitterType.WorldLocked,
        Position = at, Range = 500f, MinDistance = 2f, Volume = volume, IsEvent = true,
    };

    private static SpatialEmitter Engine(int id, Vector3 at, float volume = 1f) => new()
    {
        EntityId = id, SoundId = "engine", Mode = PlaybackMode.LoopOne, Type = EmitterType.WorldLocked,
        Position = at, Range = 500f, MinDistance = 3f, Volume = volume, IsEvent = false,
    };

    private static readonly Vector3 Here = Vector3.Zero;

    // ── Ranking ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a voice is ranked on is what reaches the ear: the path's occlusion takes its share off,
    /// so a source half blocked ranks at half the level of the same source in the open.
    /// </summary>
    [Fact]
    public void OcclusionTakesItsShareOffTheRankedLevel()
    {
        var open = Engine(1, new Vector3(20, 0, 0));
        var half = open; half.Occlusion = 0.5f;
        var all = open; all.Occlusion = 1f;
        float clear = VoiceManager.Audibility(open, 20f, false);
        Assert.True(clear > 0f);
        Assert.Equal(clear * 0.5f, VoiceManager.Audibility(half, 20f, false), 6);
        Assert.Equal(0f, VoiceManager.Audibility(all, 20f, false), 6);
    }

    /// <summary>
    /// A voice already playing is worth about two decibels more than the same voice not playing —
    /// hysteresis, so two sources a hair apart do not trade the last slot every frame.
    /// </summary>
    [Fact]
    public void APlayingVoiceHoldsItsSlotByAboutTwoDecibels()
    {
        var car = Engine(1, new Vector3(20, 0, 0));
        float idle = VoiceManager.Audibility(car, 20f, false);
        float playing = VoiceManager.Audibility(car, 20f, true);
        float db = 20f * MathF.Log10(playing / idle);
        Assert.InRange(db, 1.5f, 2.5f);
    }

    // ── Counting and forgetting ─────────────────────────────────────────────────────────────

    /// <summary>PlayingCount is the number of voices this manager has sounding — no more, no less —
    /// and falls when one is cut.</summary>
    [Fact]
    public void PlayingCountIsWhatIsSounding()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 2);
        Assert.Equal(0, voices.PlayingCount);

        voices.Submit(Engine(1, new Vector3(5, 0, 0)));
        voices.Submit(Engine(2, new Vector3(6, 0, 0)));
        voices.Submit(Engine(3, new Vector3(300, 0, 0)));
        voices.Process(Here);
        Assert.Equal(2, voices.PlayingCount);
        Assert.Equal(2, mixer.Playing.Count);
    }

    /// <summary>
    /// RequestStop says whether this manager owned the voice: yes for one it was given, no for one it
    /// never heard of — and a submission not yet scored is simply withdrawn.
    /// </summary>
    [Fact]
    public void RequestStopSaysWhetherTheVoiceWasOwned()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        voices.Submit(Engine(1, new Vector3(5, 0, 0)));
        voices.Process(Here);
        Assert.True(voices.RequestStop(1));
        Assert.False(voices.RequestStop(42));

        // Submitted, never processed: withdrawn, and it never plays.
        voices.Submit(Engine(7, new Vector3(5, 0, 0)));
        Assert.False(voices.RequestStop(7));
        voices.Process(Here);
        Assert.Empty(mixer.StartsOf(7));
    }

    /// <summary>
    /// An event nobody can hear — out past half again its range, or inside it but too quiet — is
    /// dropped on the spot and never plays, however free the budget. So is an ESSENTIAL one out past
    /// its range: essential ranks a voice, it does not make a sound carry.
    /// </summary>
    [Fact]
    public void AnInaudibleEventIsDroppedEvenWithVoicesFree()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 8);

        var gone = Event(1, new Vector3(800, 0, 0));                       // past 1.5 x 500 m
        var faint = Event(2, new Vector3(510, 0, 0));                      // inside 1.5 x range, silent
        var farEssential = Event(3, new Vector3(800, 0, 0)); farEssential.Essential = true;
        voices.Submit(gone); voices.Submit(faint); voices.Submit(farEssential);
        voices.Process(Here);

        Assert.Empty(mixer.Started);
        Assert.Equal(0, voices.SubmissionCount);
    }

    /// <summary>
    /// A continuous source out of earshot keeps its place in the queue but is not started: a car
    /// round the far side of the map is still a car, and gets a voice when it comes back.
    /// </summary>
    [Fact]
    public void AnInaudibleEngineKeepsItsPlaceButIsNotStarted()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 8);
        voices.Submit(Engine(1, new Vector3(520, 0, 0)));   // past its range: rendered level zero
        voices.Submit(Engine(2, new Vector3(800, 0, 0)));   // past 1.5 x its range
        var essential = Engine(3, new Vector3(0, 0, 800)); essential.Essential = true;   // essential ranks, it does not carry
        voices.Submit(essential);
        voices.Process(Here);
        Assert.Empty(mixer.Started);
        Assert.Equal(3, voices.SubmissionCount);
    }

    /// <summary>
    /// An essential voice right at the edge of the scoring range is still played: the edge is
    /// half again the range, inclusive.
    /// </summary>
    [Fact]
    public void AnEssentialVoiceAtTheScoringEdgeIsKept()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        var e = Event(1, new Vector3(15, 0, 0)); e.Range = 10f; e.Essential = true;
        voices.Submit(e);
        voices.Process(Here);
        Assert.Single(mixer.StartsOf(1));
    }

    /// <summary>
    /// An event that was forgotten one frame can be submitted again the next under the same id and
    /// play. Nothing left over from the frame that dropped it may drop it again.
    /// </summary>
    [Fact]
    public void AForgottenEventCanBeSubmittedAgain()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);

        // Frame one: 5 loses to a nearer event and is forgotten.
        voices.Submit(Event(1, new Vector3(1, 0, 0)));
        voices.Submit(Event(5, new Vector3(9, 0, 0)));
        voices.Process(Here);
        Assert.Empty(mixer.StartsOf(5));

        // Frame two: there is room for both, and 5 comes again. It plays on this frame.
        voices.MaxVoices = 4;
        voices.Submit(Event(5, new Vector3(2, 0, 0)));
        voices.Process(Here);
        Assert.Single(mixer.StartsOf(5));
    }

    /// <summary>
    /// A car that has been stopped and let go can be started again: the next submission under its
    /// id is a new voice, not the ghost of the old one.
    /// </summary>
    [Fact]
    public void ACarStoppedAndLetGoCanStartAgain()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        voices.Submit(Engine(1, new Vector3(5, 0, 0)));
        voices.Process(Here);
        Assert.Single(mixer.StartsOf(1));

        // No stop sound: it is cut, and forgotten.
        voices.RequestStop(1);
        voices.Process(Here);
        Assert.Contains(1, mixer.Stopped);
        Assert.Equal(0, voices.SubmissionCount);

        voices.Submit(Engine(1, new Vector3(5, 0, 0)));
        voices.Process(Here);
        Assert.Equal(2, mixer.StartsOf(1).Count());
    }

    /// <summary>
    /// A voice with no sound is never started, whatever state it is in.
    /// </summary>
    [Fact]
    public void AVoiceWithNoSoundIsNeverStarted()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        var silent = Engine(1, new Vector3(5, 0, 0)); silent.SoundId = "";
        var none = Engine(2, new Vector3(5, 0, 0)); none.SoundId = null!;
        for (int i = 0; i < 3; i++) { voices.Submit(silent); voices.Submit(none); voices.Process(Here); }
        Assert.Empty(mixer.Started);
    }

    // ── Placing, cutting and fading ─────────────────────────────────────────────────────────

    /// <summary>
    /// A voice that holds its slot is re-placed every frame, and so is one on its way out: a car
    /// still moves while it fades.
    /// </summary>
    [Fact]
    public void PlayingAndFadingVoicesAreStillPlaced()
    {
        var mixer = new Mixer { FadeFrames = 5 };
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);
        voices.Submit(Engine(1, new Vector3(6, 0, 0)));
        voices.Process(Here);

        voices.Submit(Engine(1, new Vector3(6, 0, 1)));
        voices.Process(Here);
        Assert.Contains(mixer.Placed, e => e.EntityId == 1 && e.Position.Z == 1f);

        // A nearer event takes the slot: the car fades, and is still placed where it now is.
        voices.Submit(Engine(1, new Vector3(6, 0, 2)));
        voices.Submit(Event(2, new Vector3(1, 0, 0)));
        voices.Process(Here);
        Assert.Contains(1, mixer.Fading);
        Assert.Contains(mixer.Placed, e => e.EntityId == 1 && e.Position.Z == 2f);
    }

    /// <summary>
    /// A one-shot that loses its slot while sounding is cut at once and forgotten; it is not faded,
    /// not counted as sounding, and not kept in the queue.
    /// </summary>
    [Fact]
    public void ASoundingOneShotThatLosesItsSlotIsCutAndForgotten()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);
        voices.Submit(Event(1, new Vector3(6, 0, 0)));
        voices.Process(Here);
        Assert.Contains(1, mixer.Playing);

        voices.Submit(Event(2, new Vector3(1, 0, 0)));
        voices.Process(Here);
        Assert.Contains(1, mixer.Stopped);
        Assert.DoesNotContain(1, mixer.Fading);
        Assert.Equal(1, voices.PlayingCount);
        Assert.Equal(1, voices.SubmissionCount);
    }

    /// <summary>
    /// A car that has faded right out is no longer counted as sounding, and when it wins a slot back
    /// it is started afresh — there is no fade left to cancel.
    /// </summary>
    [Fact]
    public void ACarThatHasFadedOutIsNotCountedAndStartsAfresh()
    {
        var mixer = new Mixer { FadeFrames = 2 };
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);
        voices.Submit(Engine(1, new Vector3(6, 0, 0)));
        voices.Process(Here);

        for (int i = 0; i < 2; i++)
        {
            voices.Submit(Engine(1, new Vector3(6, 0, 0)));
            voices.Submit(Engine(2, new Vector3(1, 0, 0)));
            voices.Process(Here);
        }
        Assert.Contains(1, mixer.Stopped);
        Assert.Equal(1, voices.PlayingCount);

        // The nearer car goes; the first is the loudest thing again.
        voices.RequestStop(2);
        mixer.Ends(2);
        for (int i = 0; i < 3; i++) { voices.Submit(Engine(1, new Vector3(6, 0, 0))); voices.Process(Here); }
        Assert.Equal(2, mixer.StartsOf(1).Count());
        Assert.DoesNotContain(1, mixer.FadeCancelled);
    }

    // ── Start and stop sounds, folders ──────────────────────────────────────────────────────

    private static SpatialEmitter Car(int id, string? start = null, string? stop = null)
    {
        var e = Engine(id, new Vector3(5, 0, 0));
        e.StartSoundId = start!;
        e.StopSoundId = stop!;
        return e;
    }

    /// <summary>
    /// A car with a start sound plays it first, once, and then its running sound on a loop when the
    /// start has finished.
    /// </summary>
    [Fact]
    public void AStartSoundPlaysFirstThenTheRunningSound()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        voices.Submit(Car(1, start: "ignition"));
        voices.Process(Here);
        Assert.Equal("ignition", mixer.StartsOf(1).Single().SoundId);

        mixer.Ends(1);
        voices.Submit(Car(1, start: "ignition"));
        voices.Process(Here);
        var second = mixer.StartsOf(1).Last();
        Assert.Equal("engine", second.SoundId);
        Assert.Equal(PlaybackMode.LoopOne, second.Mode);
    }

    /// <summary>An empty start sound is no start sound: the running sound plays straight away.</summary>
    [Fact]
    public void AnEmptyStartSoundIsNoStartSound()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        voices.Submit(Car(1, start: ""));
        voices.Process(Here);
        Assert.Equal("engine", mixer.StartsOf(1).Single().SoundId);
    }

    /// <summary>
    /// Told to stop, a car with a stop sound cuts its running sound and plays the stop sound ONCE —
    /// never looped — and when that has finished it is forgotten.
    /// </summary>
    [Fact]
    public void AStopSoundPlaysOnceAndThenTheCarIsForgotten()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        voices.Submit(Car(1, stop: "shutdown"));
        voices.Process(Here);

        voices.RequestStop(1);
        voices.Process(Here);
        Assert.Contains(1, mixer.Stopped);
        var stop = mixer.StartsOf(1).Last();
        Assert.Equal("shutdown", stop.SoundId);
        Assert.Equal(PlaybackMode.Single, stop.Mode);

        mixer.Ends(1);
        voices.Process(Here);
        voices.Process(Here);
        Assert.Equal(0, voices.SubmissionCount);
        Assert.Equal(2, mixer.StartsOf(1).Count());
    }

    /// <summary>An empty stop sound is no stop sound: the car is cut and forgotten at once.</summary>
    [Fact]
    public void AnEmptyStopSoundIsNoStopSound()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        voices.Submit(Car(1, stop: ""));
        voices.Process(Here);
        voices.RequestStop(1);
        voices.Process(Here);
        Assert.Contains(1, mixer.Stopped);
        Assert.Equal(0, voices.SubmissionCount);
        Assert.Single(mixer.StartsOf(1));
    }

    /// <summary>
    /// A car told to stop that loses its slot while its stop sound is going does not come back as a
    /// running engine: if it plays again, it is the stop sound it plays.
    /// </summary>
    [Fact]
    public void ACarStoppingThatLosesItsSlotDoesNotRunAgain()
    {
        var mixer = new Mixer { FadeFrames = 1 };
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);
        voices.Submit(Car(1, stop: "shutdown"));
        voices.Process(Here);
        voices.RequestStop(1);
        voices.Process(Here);
        Assert.Equal("shutdown", mixer.StartsOf(1).Last().SoundId);

        // A much nearer event takes the only slot, and the stop sound fades out.
        voices.Submit(Event(2, new Vector3(1, 0, 0)));
        voices.Process(Here);
        mixer.Ends(2);
        for (int i = 0; i < 3; i++) voices.Process(Here);

        var after = mixer.StartsOf(1).Skip(1).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, e => Assert.Equal("shutdown", e.SoundId));
    }

    /// <summary>
    /// A folder that loops, or plays in sequence, picks its next file from the folder each time one
    /// ends — not the same file for ever.
    /// </summary>
    [Theory]
    [InlineData(PlaybackMode.LoopFolder)]
    [InlineData(PlaybackMode.Sequential)]
    public void AFolderPicksAFreshFileEachTimeOneEnds(PlaybackMode mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "openfps-voice-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "RAIN"));
            for (int i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(root, "RAIN", $"drop_{i}.wav"), Array.Empty<byte>());
            var bank = new AudioBank();
            bank.Initialize(root);

            var mixer = new Mixer();
            var voices = new VoiceManager(mixer, bank);
            var rain = Engine(1, new Vector3(5, 0, 0)); rain.SoundId = "RAIN"; rain.Mode = mode;
            for (int i = 0; i < 40; i++)
            {
                voices.Submit(rain);
                voices.Process(Here);
                mixer.Ends(1);
            }
            var files = mixer.StartsOf(1).Select(e => e.SoundId).ToList();
            Assert.True(files.Count >= 20, $"only {files.Count} plays");
            Assert.All(files, f => Assert.StartsWith("RAIN/drop_", f));
            Assert.True(files.Distinct().Count() >= 3, $"the folder played only {string.Join(", ", files.Distinct())}");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>A one-shot that has played to its end is forgotten, and not played again.</summary>
    [Fact]
    public void AOneShotThatHasEndedIsForgotten()
    {
        var mixer = new Mixer();
        var voices = new VoiceManager(mixer, new AudioBank());
        var e = Event(1, new Vector3(3, 0, 0));
        e.IsEvent = false;   // a single-shot sound that is not flagged as an event: its mode decides
        voices.Submit(e);
        voices.Process(Here);
        mixer.Ends(1);
        for (int i = 0; i < 3; i++) voices.Process(Here);
        Assert.Single(mixer.StartsOf(1));
        Assert.Equal(0, voices.SubmissionCount);
    }
}
