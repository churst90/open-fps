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

public class VoiceManager
{
    private readonly AudioEngineFacade _audio;
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

    public VoiceManager(AudioEngineFacade audio, AudioBank bank, int maxVoices = 64)
    {
        _audio = audio;
        _bank = bank;
        _maxVoices = maxVoices;
        _scoredCandidates = new List<ScoredCandidate>(maxVoices * 2);
    }

    public void Submit(SpatialEmitter emitter)
    {
        if (_voiceStates.TryGetValue(emitter.EntityId, out var status))
        {
            status.StopRequested = false; // Reset if re-submitted
        }
        _activeSubmissions[emitter.EntityId] = emitter;
    }

    public void RequestStop(int entityId)
    {
        if (_voiceStates.TryGetValue(entityId, out var status))
        {
            status.StopRequested = true;
        }
        else
        {
            _activeSubmissions.Remove(entityId);
        }
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
                
                if (!_activeSubmissions.ContainsKey(id))
                {
                    _voiceStatesToRemove.Add(id);
                }
            }
        }
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