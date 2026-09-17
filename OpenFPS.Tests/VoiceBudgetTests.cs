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

        public readonly HashSet<int> Fading = new();
        /// <summary>How many frames a fade takes here. The real one is a slew in the mixer; what the
        /// budget needs to be right about is that it takes MORE THAN ONE, and that a voice can win
        /// its slot back in the middle of one.</summary>
        public int FadeFrames = 2;
        private readonly Dictionary<int, int> _fadeAge = new();

        public bool IsPlaying(int entityId) => Playing.Contains(entityId);
        public void PlayPhysicalSoundDirect(SpatialEmitter e) { Playing.Add(e.EntityId); Started.Add(e.EntityId); }
        public void UpdateSpatialAttributes(SpatialEmitter e) { }
        public void StopSoundImmediate(int entityId) { Playing.Remove(entityId); Stopped.Add(entityId); Fading.Remove(entityId); _fadeAge.Remove(entityId); }

        public bool FadeOut(int entityId)
        {
            if (!Playing.Contains(entityId)) return true;
            Fading.Add(entityId);
            int age = _fadeAge.GetValueOrDefault(entityId) + 1;
            _fadeAge[entityId] = age;
            return age >= FadeFrames;
        }

        public void CancelFade(int entityId) { Fading.Remove(entityId); _fadeAge.Remove(entityId); }

        /// <summary>The sound reached its end on its own, as a one-shot does.</summary>
        public void FinishedOnItsOwn(int entityId) => Playing.Remove(entityId);
    }

    private static SpatialEmitter Event(int id, Vector3 at, float volume = 1f) => new()
    {
        EntityId = id,
        SoundId = "knock",
        Mode = PlaybackMode.Single,
        Type = EmitterType.WorldLocked,
        Position = at,
        Range = 500f,
        MinDistance = 2f,
        Volume = volume,
        IsEvent = true,
    };

    private static SpatialEmitter Engine(int id, Vector3 at, float volume = 1f) => new()
    {
        EntityId = id,
        SoundId = "engine",
        Mode = PlaybackMode.LoopOne,
        Type = EmitterType.WorldLocked,
        Position = at,
        Range = 500f,
        MinDistance = 3f,
        Volume = volume,
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
    /// A hundred quiet, distant sources cannot take the voice off one near car.
    ///
    /// This is the fault the old ranking had, stated as a test. Voices were scored on an AUTHORED
    /// priority — engines 1, transients 2 — squared, over distance: so a clap two hundred metres away
    /// outranked a car at five metres by four to one, and the car did not fade, it was stopped and
    /// rebuilt the next frame. Now everything is ranked on the level it will actually deliver, and a
    /// hundred things nobody can hear add up to nothing.
    /// </summary>
    [Fact]
    public void AHundredQuietDistantSourcesCannotDisplaceOneNearCar()
    {
        var mixer = new FakeMixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 8);

        for (int frame = 0; frame < 3; frame++)
        {
            voices.Submit(Engine(1, new Vector3(5, 0, 0)));
            for (int i = 0; i < 100; i++)
                voices.Submit(Event(200 + i, new Vector3(0, 0, 200 + i * 0.5f)));
            voices.Process(Vector3.Zero);
        }

        _o.WriteLine($"the near car is {(mixer.Playing.Contains(1) ? "playing" : "SILENT")}; "
                   + $"{mixer.Playing.Count} voices sounding of a budget of 8");
        Assert.Contains(1, mixer.Playing);
        Assert.DoesNotContain(1, mixer.Stopped);
    }

    /// <summary>
    /// ...and the reverse, which is the half that makes it physics rather than a rule about cars.
    ///
    /// One near, loud transient — a clap at three metres — outranks a distant car, because at that
    /// moment it is the louder thing. Nothing here knows which is which.
    /// </summary>
    [Fact]
    public void ANearTransientOutranksADistantCar()
    {
        var mixer = new FakeMixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);

        voices.Submit(Engine(1, new Vector3(0, 0, 300)));
        voices.Submit(Event(2, new Vector3(3, 0, 0)));
        voices.Process(Vector3.Zero);

        _o.WriteLine($"playing: {string.Join(", ", mixer.Playing)}");
        Assert.Contains(2, mixer.Playing);
        Assert.DoesNotContain(1, mixer.Playing);
    }

    /// <summary>
    /// A continuous source that loses its slot FADES; a one-shot is cut.
    ///
    /// The difference is what a sound IS, not how loud it is. A car that runs out of budget is still
    /// there — cutting it mid-waveform is a click, and for a synthesized engine it is a rebuild from
    /// silence. A one-shot has already missed its moment, and fading something that is over buys
    /// nothing and holds a voice.
    /// </summary>
    [Fact]
    public void AContinuousSourceFadesOutWhereAOneShotIsCut()
    {
        var mixer = new FakeMixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);

        // The car holds the only slot, then a much nearer event takes it.
        voices.Submit(Engine(1, new Vector3(6, 0, 0)));
        voices.Process(Vector3.Zero);
        Assert.Contains(1, mixer.Playing);

        voices.Submit(Engine(1, new Vector3(6, 0, 0)));
        voices.Submit(Event(2, new Vector3(1, 0, 0)));
        voices.Process(Vector3.Zero);

        _o.WriteLine($"after losing the slot: fading={string.Join(",", mixer.Fading)} stopped={string.Join(",", mixer.Stopped)}");
        Assert.Contains(1, mixer.Fading);
        Assert.DoesNotContain(1, mixer.Stopped);      // not cut — going down gently

        // ...and once the fade has run, it is let go.
        for (int frame = 0; frame < 4; frame++)
        {
            voices.Submit(Engine(1, new Vector3(6, 0, 0)));
            voices.Submit(Event(2, new Vector3(1, 0, 0)));
            voices.Process(Vector3.Zero);
        }
        Assert.Contains(1, mixer.Stopped);
    }

    /// <summary>
    /// A source that wins its slot back MID-FADE is brought round rather than left to die.
    ///
    /// The exact fault that made cars fall silent for the rest of their lives once before: a voice
    /// was taken off the retiring list with its envelope still heading for zero and nothing anywhere
    /// to turn it round. It kept its engine, kept its position, kept being updated every frame, and
    /// was inaudible.
    /// </summary>
    [Fact]
    public void ASourceThatWinsItsSlotBackMidFadeIsBroughtRound()
    {
        var mixer = new FakeMixer { FadeFrames = 6 };
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);

        voices.Submit(Engine(1, new Vector3(6, 0, 0)));
        voices.Process(Vector3.Zero);

        voices.Submit(Engine(1, new Vector3(6, 0, 0)));
        voices.Submit(Event(2, new Vector3(1, 0, 0)));
        voices.Process(Vector3.Zero);
        Assert.Contains(1, mixer.Fading);

        // The event ends; the car is the loudest thing again. Two frames, because a one-shot that
        // finished is scored once more before it is noticed to be over — it frees its slot on the
        // frame after it ends, which is a frame of grace out of a fade that is several frames long.
        mixer.FinishedOnItsOwn(2);
        voices.Submit(Engine(1, new Vector3(6, 0, 0)));
        voices.Process(Vector3.Zero);
        voices.Submit(Engine(1, new Vector3(6, 0, 0)));
        voices.Process(Vector3.Zero);

        _o.WriteLine($"fading={string.Join(",", mixer.Fading)} playing={string.Join(",", mixer.Playing)}");
        Assert.DoesNotContain(1, mixer.Fading);
        Assert.Contains(1, mixer.Playing);
        Assert.DoesNotContain(1, mixer.Stopped);
    }

    /// <summary>
    /// An essential voice is not outbid by the physics.
    ///
    /// The one deliberate departure: your own footsteps, speech and a warning tone are what a player
    /// NEEDS, and on a loud map the arithmetic would rightly bury every one of them. A short explicit
    /// list, pinned — not a knob on every object.
    /// </summary>
    [Fact]
    public void AnEssentialVoiceIsNotOutbidByTheLoudestThingOnTheMap()
    {
        var mixer = new FakeMixer();
        var voices = new VoiceManager(mixer, new AudioBank(), maxVoices: 1);

        var footstep = Event(-100, new Vector3(0, 0, 1f));
        footstep.Essential = true;
        footstep.Volume = 0.05f;                       // quiet, and right next to you
        voices.Submit(footstep);
        voices.Submit(Engine(1, new Vector3(4, 0, 0)));  // a car at four metres, far louder
        voices.Process(Vector3.Zero);

        _o.WriteLine($"playing: {string.Join(", ", mixer.Playing)}");
        Assert.Contains(-100, mixer.Playing);
    }

    /// <summary>
    /// Giving a source a SIZE does not make it louder — it flattens the near field and leaves the far
    /// field exactly where it was.
    ///
    /// The half that was wrong in the engine: a vehicle's reference distance was widened to three
    /// metres and nothing was paid back for it, which is not an extended source, it is a louder one.
    /// Worth up to eight decibels on a quiet vehicle.
    /// </summary>
    [Fact]
    public void ExtentFlattensTheNearFieldAndLeavesTheFarFieldAlone()
    {
        const float level = 94f;                        // a small hatchback
        var point = OpenFPS.Common.Loudness.Place(level);
        var sized = OpenFPS.Common.Loudness.Place(level, 3.2f);

        float Far(float d, (float Gain, float ReferenceDistance) p)
            => OpenFPS.Common.Loudness.RenderedGain(p.Gain, p.ReferenceDistance, 500f, d);

        _o.WriteLine($"point: gain {point.Gain:F3} ref {point.ReferenceDistance:F2} m; "
                   + $"3.2 m across: gain {sized.Gain:F3} ref {sized.ReferenceDistance:F2} m");
        // Far away, identical to within a rounding error: that is what makes the point model usable.
        foreach (float d in new[] { 20f, 50f, 130f })
            Assert.Equal(Far(d, point), Far(d, sized), 4);

        // Close in, the sized one is quieter, because you cannot get to the middle of it.
        Assert.True(Far(2f, sized) < Far(2f, point));

        // And the old fudge — widen the reference, pay nothing back — was louder everywhere.
        float fudged = OpenFPS.Common.Loudness.RenderedGain(point.Gain, MathF.Max(point.ReferenceDistance, 3f), 500f, 20f);
        float honest = Far(20f, sized);
        _o.WriteLine($"at 20 m: honest {20 * MathF.Log10(honest):F1} dBFS, old fudge {20 * MathF.Log10(fudged):F1} dBFS");
        Assert.True(fudged > honest * 1.5f, "the old fudge was not worth several decibels, so this test proves nothing.");
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
