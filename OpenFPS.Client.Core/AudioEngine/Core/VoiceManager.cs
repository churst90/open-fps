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
/// Everything the voice budget needs from the mixer, so the budget can be tested without FMOD.
/// </summary>
public interface IVoiceSink
{
    bool IsPlaying(int entityId);
    void PlayPhysicalSoundDirect(SpatialEmitter emitter);
    void UpdateSpatialAttributes(SpatialEmitter emitter);
    void StopSoundImmediate(int entityId);

    /// <summary>
    /// Takes a continuous voice that lost its slot down to silence over about 80 ms; true once it is
    /// there and may be stopped. Idempotent, called every frame it is out. Cutting it is a click, and
    /// a synthesized engine rebuilt from nothing when it comes back ("vehicles stop close in front of
    /// me").
    /// </summary>
    bool FadeOut(int entityId);

    /// <summary>Cancels a fade because the voice won its slot back. Idempotent; without it a voice fades
    /// to nothing and never returns while everything else about it looks alive.</summary>
    void CancelFade(int entityId);
}

public class VoiceManager
{
    private readonly IVoiceSink _audio;
    private readonly AudioBank _bank;

    /// <summary>
    /// How many voices the budget may spend, set each frame from the mixer's free HRTF slots (96 in
    /// all; see AudioEngineFacade). A voice that gets no slot plays flat, with no binaural position,
    /// and reflections, borrowed cars and outlets spend the same pool outside this manager. It settles:
    /// what is free shrinks as this manager starts voices.
    /// </summary>
    public int MaxVoices { get; set; }

    /// <summary>Voices this manager has sounding.</summary>
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
    /// <summary>Voices fading out of the budget, kept so one that wins its slot back is brought round
    /// rather than restarted.</summary>
    private readonly HashSet<int> _fading = new();
    private readonly Dictionary<int, SpatialEmitter> _activeSubmissions = new(); 
    private readonly List<ScoredCandidate> _scoredCandidates;
    private readonly HashSet<int> _topEntityIds = new();
    private readonly List<int> _keysToRemove = new();
    private readonly List<int> _voiceStatesToRemove = new();
    /// <summary>Voices submitted again since the last pass while a play of them was under way.</summary>
    private readonly HashSet<int> _askedAgain = new();

    public VoiceManager(IVoiceSink audio, AudioBank bank, int maxVoices = 64)
    {
        _audio = audio;
        _bank = bank;
        MaxVoices = maxVoices;
        _scoredCandidates = new List<ScoredCandidate>(maxVoices * 2);
    }

    /// <summary>Submissions waiting to be scored. Diagnostic: growth without bound is a leak, and
    /// one-shots are what leak.</summary>
    public int SubmissionCount => _activeSubmissions.Count;

    public void Submit(SpatialEmitter emitter)
    {
        if (_voiceStates.TryGetValue(emitter.EntityId, out var status))
        {
            status.StopRequested = false;
            // If the play under way turns out to have ended, this is a new one (Process).
            if (status.IsPhysicallyPlaying) _askedAgain.Add(emitter.EntityId);
        }
        _activeSubmissions[emitter.EntityId] = emitter;
    }

    /// <summary>
    /// Asks for a voice to stop: true if this manager owns it, false if not, and then the caller must
    /// stop it. Voices started directly (engine reflections, floor slapback) bypass this manager; when
    /// the request was silently dropped, the speedway ran from 24 live voices to 123 in fifty seconds
    /// with the mixer at 100 %, heard as the whole map crackling.
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
            // Half as far again as the range, so tails are not cut off abruptly.
            if (dist > e.Range * 1.5f) 
            {
                if (e.IsEvent) _keysToRemove.Add(id);
                continue;
            }

            bool playing = _audio.IsPlaying(id);
            float score = Audibility(e, dist, playing);
            // An inaudible one-shot is dropped. A continuous voice already playing is held at the floor
            // and left to the budget: dropped there, a car at 300 m passing behind buildings was rebuilt
            // 45 times in nine minutes, heard as distant traffic stuttering.
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
                // A one-shot asked for again as its last play ended is a new play. Finishing the old play
                // must not drop the new submission: a repeating announcement was silent for a whole
                // interval when its last line ended in the frame the next came round.
                if (_askedAgain.Contains(id) && emitter.Mode == PlaybackMode.Single && !status.StopRequested)
                {
                    _voiceStates[id] = status = new VoiceStatus { Emitter = emitter };
                    UpdateStateLogic(status);
                    continue;
                }
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
                // A voice that holds a slot is audible: cancel any fade it was in.
                if (_fading.Remove(scored.EntityId)) _audio.CancelFade(scored.EntityId);
                _audio.UpdateSpatialAttributes(status.Emitter);
            }
            else if (!string.IsNullOrEmpty(status.CurrentResolvedPath) && (status.State == InternalState.Playing || status.State == InternalState.Starting || status.State == InternalState.Stopping))
            {
                var playEmitter = status.Emitter;
                playEmitter.SoundId = status.CurrentResolvedPath;
                // A stop sound does not loop.
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
                    // A one-shot's moment has gone, so it is dropped; anything else is still there and
                    // fades, let go only once silent (see IVoiceSink.FadeOut).
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
                        // Still fading, and still placed: a fade from a frozen position slides off to
                        // one side.
                        _fading.Add(id);
                        _audio.UpdateSpatialAttributes(status.Emitter);
                        continue;
                    }
                }

                // An event that did not win a slot has missed its moment. Left queued, it was re-scored
                // for ever and fired whenever the listener moved somewhere its score won: reflections
                // piling up where nothing happened, a crowd from the wrong side of the track.
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
        _askedAgain.Clear();
    }

    private void UpdateStateLogic(VoiceStatus status)
    {
        bool isCurrentlyPlayingInFmod = _audio.IsPlaying(status.Emitter.EntityId);
        
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
    /// What this voice will deliver to the ear, the one quantity the budget ranks on: the mixer's own
    /// distance law (<see cref="OpenFPS.Common.Loudness.RenderedGain"/>) times what the path lets
    /// through, as loudness. Two departures: <see cref="SpatialEmitter.Essential"/> pins speech, your
    /// own steps and warnings above the physics, which on a racetrack would bury them; and a playing
    /// voice gets 2 dB of hysteresis, so two near-equal sources do not trade the last slot every frame.
    /// </summary>
    internal static float Audibility(in SpatialEmitter e, float distance, bool playing)
    {
        float level = OpenFPS.Common.Loudness.RenderedGain(e.Volume, e.MinDistance, e.Range, distance);
        level *= 1f - Math.Clamp(e.Occlusion, 0f, 1f);
        // Ranked by loudness at the ear from the measured spectrum (docs/EAR_MODEL.md, Ranking), not by
        // the law's gain: the law plays what the ear hears less of louder (a 25 Hz rumble about 21 dB
        // up), and ranking on that gain put the rumble at the top.
        if (e.EarLevelDb > 0f)
        {
            string key = string.IsNullOrEmpty(e.PhysicalKey) ? e.SoundId : e.PhysicalKey;
            float db = EarTimbres.CorrectionDb(key, e.EarLevelDb);
            if (db != 0f) level *= MathF.Pow(10f, db / 20f);
            level = OpenFPS.Common.Loudness.HeardGain(level, EarTimbres.Find(key), IsPhysical(e));
        }
        if (playing) level *= PlayingHysteresis;
        // Pinned, not weighted: essential voices rank above all others, and among themselves by level.
        return e.Essential ? level + EssentialPin : level;
    }

    /// <summary>Whether a voice is physical (declared level is its RMS) rather than a recording (declared
    /// level is full scale), read from what will build its DSP. Consulted until its spectrum is measured.</summary>
    private static bool IsPhysical(in SpatialEmitter e)
        => e.IsSynth || e.IsGranular || !string.IsNullOrEmpty(e.PhysicalKey) || !string.IsNullOrEmpty(e.EngineKey);

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