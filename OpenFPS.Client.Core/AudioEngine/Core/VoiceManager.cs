using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.AudioEngine.Core;

public class ScoredCandidate
{
    public int EntityId;
    public float Score;
}

/// <summary>
/// Everything the budget needs from the mixer, and nothing else.
///
/// It exists so the budget can be TESTED. Deciding which sounds get a voice is the one part of this
/// engine with no acoustics in it at all — it is bookkeeping — and it was the part with no tests,
/// because <see cref="AudioEngineFacade"/> needs FMOD and a sound card. What went wrong there was a
/// one-shot that lost the budget being kept in the queue for ever and fired minutes later from a
/// position computed for somewhere the listener no longer was, and nothing could have caught it.
/// </summary>
public interface IVoiceSink
{
    bool IsPlaying(int entityId);
    void PlayPhysicalSoundDirect(SpatialEmitter emitter);
    void UpdateSpatialAttributes(SpatialEmitter emitter);
    void StopSoundImmediate(int entityId);

    /// <summary>
    /// Takes a voice down to silence over about eighty milliseconds, and says when it is there.
    ///
    /// The difference between a continuous source and a one-shot losing its slot. A one-shot has
    /// missed its moment and is dropped; a car that runs out of budget IS STILL THERE, still making a
    /// noise, and cutting it mid-waveform is a click — for a synthesized engine it is worse than a
    /// click, because the voice is rebuilt from nothing when it comes back: a fresh ring, priming
    /// silence and an envelope fade. That is what "vehicles stop close in front of me" is made of.
    ///
    /// Idempotent: called every frame the voice is out of the budget, and returns true once the fade
    /// has finished and the caller may stop it.
    /// </summary>
    bool FadeOut(int entityId);

    /// <summary>Cancels a fade, because the voice won its slot back. Cheap and idempotent — it is the
    /// other half of <see cref="FadeOut"/>, and its absence is a voice that fades to nothing and never
    /// comes back while everything else about it looks alive.</summary>
    void CancelFade(int entityId);
}

public class VoiceManager
{
    private readonly IVoiceSink _audio;
    private readonly AudioBank _bank;

    /// <summary>
    /// How many voices the budget may spend — the REAL resource, not a constant.
    ///
    /// Set each frame from what the mixer actually has left (see AudioEngineFacade): every
    /// spatialised voice needs an HRTF slot, there are ninety-six of them, and a voice that cannot
    /// get one does not fail — it plays FLAT, with no binaural position at all, which on a map you
    /// navigate by ear is worse than not playing it. A fixed 256 could not see that coming; it only
    /// counted its own voices, and the reflections, borrowed cars and outlets that bypass this
    /// manager were spending the same pool behind its back.
    ///
    /// It settles rather than hunting: what is free shrinks as this manager starts voices, so the
    /// budget stops rising exactly where the pool runs out.
    /// </summary>
    public int MaxVoices { get; set; }

    /// <summary>How many voices this manager currently has sounding. With what the mixer says is
    /// free, this is what the budget above is worth.</summary>
    public int PlayingCount
    {
        get
        {
            int n = 0;
            foreach (var kv in _voiceStates) if (kv.Value.IsPhysicallyPlaying) n++;
            return n;
        }
    }
    
    private enum InternalState { Idle, Starting, Playing, Stopping, Finished }

    private class VoiceStatus
    {
        public SpatialEmitter Emitter;
        public InternalState State = InternalState.Idle;
        public string CurrentResolvedPath = "";
        public bool IsPhysicallyPlaying = false;
        public bool StopRequested = false;
    }

    private readonly Dictionary<int, VoiceStatus> _voiceStates = new();
    /// <summary>Voices that lost the budget and are on their way out. Kept so a fade can finish, and
    /// so one that wins its slot back can be brought round rather than restarted.</summary>
    private readonly HashSet<int> _fading = new();
    private readonly Dictionary<int, SpatialEmitter> _activeSubmissions = new(); 
    private readonly List<ScoredCandidate> _scoredCandidates;
    private readonly HashSet<int> _topEntityIds = new();
    private readonly List<int> _keysToRemove = new();
    private readonly List<int> _voiceStatesToRemove = new();

    public VoiceManager(IVoiceSink audio, AudioBank bank, int maxVoices = 64)
    {
        _audio = audio;
        _bank = bank;
        MaxVoices = maxVoices;
        _scoredCandidates = new List<ScoredCandidate>(maxVoices * 2);
    }

    /// <summary>How many submissions are waiting to be scored. Diagnostic — a number that grows
    /// without bound is a leak, and one-shots are what leak.</summary>
    public int SubmissionCount => _activeSubmissions.Count;

    public void Submit(SpatialEmitter emitter)
    {
        if (_voiceStates.TryGetValue(emitter.EntityId, out var status))
        {
            status.StopRequested = false; // Reset if re-submitted
        }
        _activeSubmissions[emitter.EntityId] = emitter;
    }

    /// <summary>
    /// Asks for a voice to stop. Returns TRUE if this manager owns it and will deal with it, FALSE
    /// if it has never heard of it.
    ///
    /// The return value matters, and its absence was a leak. Voices started through
    /// <c>PlayPhysicalSoundDirect</c> — every engine reflection, every floor slapback — deliberately
    /// bypass this manager, so they are not in <c>_voiceStates</c> and the else branch below quietly
    /// dropped the request on the floor. Nothing ever stopped them. On the speedway, where a
    /// reflection voice is created for each car against each wall, that ran from 24 live voices to
    /// 123 in fifty seconds with the mixer pegged at 100% — heard as the whole map crackling and
    /// stuttering. The caller now knows to stop those itself.
    /// </summary>
    public bool RequestStop(int entityId)
    {
        if (_voiceStates.TryGetValue(entityId, out var status))
        {
            status.StopRequested = true;
            return true;
        }
        _activeSubmissions.Remove(entityId);
        return false;
    }

    public void Process(Vector3 listenerPos)
    {
        _scoredCandidates.Clear();
        _topEntityIds.Clear();
        _keysToRemove.Clear();
        _voiceStatesToRemove.Clear();

        foreach (var kvp in _activeSubmissions)
        {
            var id = kvp.Key;
            var e = kvp.Value;

            float dist = Vector3.Distance(listenerPos, e.Position);
            // Higher range for scoring so we don't cut off tails abruptly
            if (dist > e.Range * 1.5f) 
            {
                if (e.IsEvent) _keysToRemove.Add(id);
                continue;
            }

            bool playing = _audio.IsPlaying(id);
            float score = Audibility(e, dist, playing);
            // Below this nobody can hear it — not because of what it is, but because of where it is
            // and how loud it is. A one-shot that cannot be heard has missed nothing by being dropped.
            //
            // A continuous voice that is ALREADY PLAYING is not dropped here. Its level goes under
            // the floor and back every time a distant car passes behind a building, and stopping it
            // there meant rebuilding the engine from nothing each time it came back out — at 300 m,
            // one car was rebuilt 45 times in nine minutes, heard as distant traffic stuttering. It
            // is inaudible down there anyway; it stays at the floor and still competes for a slot,
            // so the budget, not the occlusion, decides when it goes.
            if (playing && !e.IsEvent && score < OpenFPS.Common.Loudness.SilenceGain)
                score = OpenFPS.Common.Loudness.SilenceGain;
            if (score < OpenFPS.Common.Loudness.SilenceGain && !e.Essential)
            {
                if (e.IsEvent) _keysToRemove.Add(id);
                continue;
            }
            _scoredCandidates.Add(new ScoredCandidate { EntityId = id, Score = score });
        }

        foreach (var id in _keysToRemove) _activeSubmissions.Remove(id);
        _keysToRemove.Clear();

        _scoredCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        int activeCount = Math.Min(_scoredCandidates.Count, Math.Max(1, MaxVoices));

        foreach (var kvp in _activeSubmissions)
        {
            var id = kvp.Key;
            var emitter = kvp.Value;

            if (!_voiceStates.TryGetValue(id, out var status))
            {
                status = new VoiceStatus { Emitter = emitter };
                _voiceStates[id] = status;
            }
            
            status.Emitter = emitter; 
            UpdateStateLogic(status);

            if (status.State == InternalState.Finished)
            {
                _keysToRemove.Add(id);
                _voiceStatesToRemove.Add(id);
                continue;
            }
        }

        foreach (var id in _keysToRemove) _activeSubmissions.Remove(id);
        foreach (var id in _voiceStatesToRemove) _voiceStates.Remove(id);

        for (int i = 0; i < activeCount; i++)
        {
            var scored = _scoredCandidates[i];
            _topEntityIds.Add(scored.EntityId);

            if (!_voiceStates.TryGetValue(scored.EntityId, out var status)) continue;
            
            bool isActuallyPlayingInFmod = _audio.IsPlaying(scored.EntityId);

            if (isActuallyPlayingInFmod && status.IsPhysicallyPlaying)
            {
                // Whatever happened to it before, a voice that holds a slot is audible. The other
                // half of the fade above, and the reason a source that dips out of the budget for a
                // moment comes back instead of dying quietly with everything else about it alive.
                if (_fading.Remove(scored.EntityId)) _audio.CancelFade(scored.EntityId);
                _audio.UpdateSpatialAttributes(status.Emitter);
            }
            else if (!string.IsNullOrEmpty(status.CurrentResolvedPath) && (status.State == InternalState.Playing || status.State == InternalState.Starting || status.State == InternalState.Stopping))
            {
                var playEmitter = status.Emitter;
                playEmitter.SoundId = status.CurrentResolvedPath;
                // If in stopping state, don't loop the stop sound
                if (status.State == InternalState.Stopping) playEmitter.Mode = PlaybackMode.Single;
                
                _audio.PlayPhysicalSoundDirect(playEmitter);
                status.IsPhysicallyPlaying = true;
            }
        }

        _voiceStatesToRemove.Clear();
        _keysToRemove.Clear();
        foreach (var kvp in _voiceStates)
        {
            var id = kvp.Key;
            var status = kvp.Value;
            if (!_topEntityIds.Contains(id))
            {
                if (status.IsPhysicallyPlaying)
                {
                    // A ONE-SHOT is dropped: its moment has gone, and a moment cannot be resumed.
                    // ANYTHING ELSE IS STILL THERE — a car does not stop existing because the budget
                    // ran out — so it fades, and is let go only once it is silent. Cutting it instead
                    // is a click on anything with a waveform running, and on a synthesized engine it
                    // is a rebuild: ring, priming silence and envelope, every time it comes back.
                    if (status.Emitter.IsEvent)
                    {
                        _audio.StopSoundImmediate(id);
                        status.IsPhysicallyPlaying = false;
                    }
                    else if (_audio.FadeOut(id))
                    {
                        _audio.StopSoundImmediate(id);
                        status.IsPhysicallyPlaying = false;
                        _fading.Remove(id);
                    }
                    else
                    {
                        // Still going down. Keep the voice and its bookkeeping alive, and keep
                        // placing it: a fading car is still moving, and a fade from a frozen
                        // position is a sound sliding off to one side as it goes.
                        _fading.Add(id);
                        _audio.UpdateSpatialAttributes(status.Emitter);
                        continue;
                    }
                }

                // AN EVENT THAT DID NOT WIN A SLOT HAS MISSED ITS MOMENT.
                //
                // A submission stays here until it has been observed to start and then stop, which is
                // right for a car — it is still there, still making a noise, and will get a voice back
                // when one frees up. A one-shot is not: a clap that lost the budget this frame cannot
                // be played later, because "later" is a different moment and the sound belongs to this
                // one. Left in, it sat in the queue being re-scored for ever and FIRED whenever the
                // listener moved somewhere that made its score win — heard as reflections piling up in
                // a place where nothing was happening, and as a crowd arriving from the wrong side of
                // the track, because a mirrored position means nothing once you have walked away from
                // where it was computed.
                //
                // It was invisible until transients got one voice id each. Sharing ids by sound meant
                // the next footstep overwrote the last one's stale entry, so the queue stayed small by
                // accident and the fault looked like a feature.
                if (!status.IsPhysicallyPlaying && status.Emitter.IsEvent)
                {
                    _keysToRemove.Add(id);
                    _voiceStatesToRemove.Add(id);
                    continue;
                }

                if (!_activeSubmissions.ContainsKey(id))
                {
                    _voiceStatesToRemove.Add(id);
                }
            }
        }
        foreach (var id in _keysToRemove) _activeSubmissions.Remove(id);
        foreach (var id in _voiceStatesToRemove) _voiceStates.Remove(id);
    }

    private void UpdateStateLogic(VoiceStatus status)
    {
        bool isCurrentlyPlayingInFmod = _audio.IsPlaying(status.Emitter.EntityId);
        
        // Handle stop requests for machines (Car shutdown etc)
        if (status.StopRequested && status.State != InternalState.Stopping && status.State != InternalState.Finished)
        {
            if (!string.IsNullOrEmpty(status.Emitter.StopSoundId))
            {
                status.State = InternalState.Stopping;
                status.CurrentResolvedPath = ResolvePath(status.Emitter.StopSoundId);
                if (status.IsPhysicallyPlaying) _audio.StopSoundImmediate(status.Emitter.EntityId);
                status.IsPhysicallyPlaying = false;
                return;
            }
            else
            {
                status.State = InternalState.Finished;
                if (status.IsPhysicallyPlaying) _audio.StopSoundImmediate(status.Emitter.EntityId);
                return;
            }
        }

        if (status.IsPhysicallyPlaying && !isCurrentlyPlayingInFmod)
        {
            status.IsPhysicallyPlaying = false;
            
            if (status.State == InternalState.Starting)
            {
                status.State = InternalState.Playing;
                status.CurrentResolvedPath = ResolvePath(status.Emitter.SoundId);
                return;
            }
            else if (status.State == InternalState.Stopping)
            {
                status.State = InternalState.Finished;
                status.CurrentResolvedPath = "";
                return;
            }
            else if (status.State == InternalState.Playing)
            {
                if (status.Emitter.Mode == PlaybackMode.Single)
                {
                    status.State = InternalState.Finished;
                    status.CurrentResolvedPath = "";
                    return;
                }
                else if (status.Emitter.Mode == PlaybackMode.LoopFolder || status.Emitter.Mode == PlaybackMode.Sequential)
                {
                    status.State = InternalState.Playing; 
                    status.CurrentResolvedPath = ResolvePath(status.Emitter.SoundId);
                    return;
                }
            }
        }

        if (status.State == InternalState.Idle)
        {
            if (!string.IsNullOrEmpty(status.Emitter.StartSoundId))
            {
                status.CurrentResolvedPath = ResolvePath(status.Emitter.StartSoundId);
                status.State = InternalState.Starting;
            }
            else
            {
                status.CurrentResolvedPath = ResolvePath(status.Emitter.SoundId);
                status.State = InternalState.Playing;
            }
        }
    }

    /// <summary>
    /// What this voice will actually deliver to the ear — the one quantity everything is ranked on.
    ///
    /// <see cref="OpenFPS.Common.Loudness.RenderedGain"/> is the mixer's own distance law, so this is
    /// not an estimate of the balance, it IS the balance: the same gain, reference distance and range
    /// the voice will be played at, times what the path lets through. A bird, a bus, a fountain and a
    /// jet are compared in identical units, and nothing in here knows what any of them is.
    ///
    /// The two departures from pure physics are deliberate and small:
    ///
    /// <see cref="SpatialEmitter.Essential"/> pins a voice above the arithmetic — speech, your own
    /// footsteps, a warning tone — because those are what a player NEEDS rather than what is loudest,
    /// and on a racetrack the physics would rightly bury every one of them.
    ///
    /// A voice that is already playing is worth a little more than one that is not, which is
    /// HYSTERESIS and not importance: two sources within a hair of each other would otherwise trade
    /// the last slot every frame, and a voice swapping in and out at frame rate is a far worse noise
    /// than either of them being missing. Two decibels is enough to settle it and small enough that
    /// it cannot hold a slot against anything actually louder.
    /// </summary>
    internal static float Audibility(in SpatialEmitter e, float distance, bool playing)
    {
        float level = OpenFPS.Common.Loudness.RenderedGain(e.Volume, e.MinDistance, e.Range, distance);
        level *= 1f - Math.Clamp(e.Occlusion, 0f, 1f);
        if (playing) level *= PlayingHysteresis;
        // Pinned, not weighted: an essential voice ranks above every voice that is merely loud, and
        // among themselves they still rank on what can be heard.
        return e.Essential ? level + EssentialPin : level;
    }

    /// <summary>About two decibels. See <see cref="Audibility"/>.</summary>
    private const float PlayingHysteresis = 1.26f;

    /// <summary>Above any gain the physics can produce, so an essential voice cannot be outbid.</summary>
    private const float EssentialPin = 1000f;

    private string ResolvePath(string pathOrCategory)
    {
        if (string.IsNullOrEmpty(pathOrCategory)) return "";
        if (_bank.HasCategory(pathOrCategory)) return _bank.GetRandomSoundPath(pathOrCategory);
        return pathOrCategory;
    }
}