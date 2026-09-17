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
}

public class VoiceManager
{
    private readonly IVoiceSink _audio;
    private readonly AudioBank _bank;
    private readonly int _maxVoices;
    
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
    private readonly Dictionary<int, SpatialEmitter> _activeSubmissions = new(); 
    private readonly List<ScoredCandidate> _scoredCandidates;
    private readonly HashSet<int> _topEntityIds = new();
    private readonly List<int> _keysToRemove = new();
    private readonly List<int> _voiceStatesToRemove = new();

    public VoiceManager(IVoiceSink audio, AudioBank bank, int maxVoices = 64)
    {
        _audio = audio;
        _bank = bank;
        _maxVoices = maxVoices;
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

            float playingBoost = _audio.IsPlaying(id) ? 1.5f : 1.0f;
            float score = ((e.Priority * e.Priority) / (1.0f + dist)) * playingBoost;
            _scoredCandidates.Add(new ScoredCandidate { EntityId = id, Score = score });
        }

        foreach (var id in _keysToRemove) _activeSubmissions.Remove(id);
        _keysToRemove.Clear();

        _scoredCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        int activeCount = Math.Min(_scoredCandidates.Count, _maxVoices);

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
                    _audio.StopSoundImmediate(id);
                    status.IsPhysicallyPlaying = false;
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

    private string ResolvePath(string pathOrCategory)
    {
        if (string.IsNullOrEmpty(pathOrCategory)) return "";
        if (_bank.HasCategory(pathOrCategory)) return _bank.GetRandomSoundPath(pathOrCategory);
        return pathOrCategory;
    }
}