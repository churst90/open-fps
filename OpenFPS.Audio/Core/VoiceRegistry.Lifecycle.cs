using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.Core;

/// <summary>How a voice is taken down when its owner lets it go (<see cref="VoiceRegistry.Retire"/>).</summary>
public enum VoiceFade : byte
{
    /// <summary>A running synth: its envelope to silence (the provider's FadeOutEngine), brought back by
    /// ReviveEngine. Cut, a synth's waveform steps.</summary>
    Engine,
    /// <summary>Any voice: its gain to silence over the budget's fade (FadeOutVoice), brought back by
    /// CancelVoiceFade.</summary>
    Voice,
}

/// <summary>
/// The one way a world voice is started, placed, faded and stopped (docs/SPECIAL_CASES_AUDIT.md, P3d). An owner
/// declares its voice, builds its emitter and sounds it (<see cref="Sound"/>, outside the budget) or hands it to the
/// budget (<see cref="Submit"/>); it lets it go with <see cref="Retire"/> (a fade, then a stop) or
/// <see cref="Stop"/>. Whether a voice is started or only placed is the mixer's to decide when it comes to it, never
/// an owner's from a sounding test a frame old; and a voice fading out is an entry until its fade has ended.
/// </summary>
public sealed partial class VoiceRegistry
{
    /// <summary>The mixer voices are started on. Null: a registry that only keeps ids and entries (a test).</summary>
    public AudioEngineFacade? Mixer { get; set; }

    /// <summary>Voices let go and fading, in the order they were let go, and how each fades.</summary>
    private readonly Dictionary<int, VoiceFade> _retiring = new();
    private readonly List<int> _faded = new();

    /// <summary>Sounds a voice outside the budget: starts it if it is not sounding, places it if it is. A voice
    /// that was fading out is brought back first. Its entry is its owner's to declare.</summary>
    public void Sound(in SpatialEmitter e)
    {
        Unretire(e.EntityId);
        Mixer?.Sound(e);
    }

    /// <summary>Hands a voice to the budget (VoiceManager), which starts, ranks and places it. A voice fading
    /// out is brought back first.</summary>
    public void Submit(in SpatialEmitter e)
    {
        Unretire(e.EntityId);
        Mixer?.Submit(e);
    }

    /// <summary>A sustained voice in the budget, renewed every frame: handed over while it is not sounding, its
    /// placing renewed while it is (AudioEngineFacade.Sustain), so the budget never re-applies what it started
    /// with (S11).</summary>
    public void Sustain(in SpatialEmitter e)
    {
        Unretire(e.EntityId);
        Mixer?.Sustain(e);
    }

    /// <summary>A fresh placing for a voice the budget holds, which never starts one (AudioEngineFacade.Refresh).</summary>
    public void Refresh(in SpatialEmitter e) => Mixer?.Refresh(e);

    /// <summary>Stops a voice now (the provider still plays a machine's stop sound) and lets its entry go. A
    /// voice that is no entry and not sounding has nothing to stop.</summary>
    public void Stop(int voiceId)
    {
        bool known;
        lock (_lock) known = _retiring.Remove(voiceId) | _entries.Remove(voiceId);
        if (Mixer != null && (known || Mixer.IsPlaying(voiceId))) Mixer.StopSound(voiceId);
    }

    /// <summary>A hard cut on the calling thread, for a voice that must be gone before the next starts.</summary>
    public void StopNow(int voiceId)
    {
        bool known;
        lock (_lock) known = _retiring.Remove(voiceId) | _entries.Remove(voiceId);
        if (Mixer != null && (known || Mixer.IsPlaying(voiceId))) Mixer.StopSoundImmediate(voiceId);
    }

    /// <summary>
    /// Lets a voice go by fading it (<paramref name="fade"/>) and then stopping it; meanwhile it is an entry and
    /// its id is held. Asked again of a voice already fading, nothing changes. A voice that is not sounding is
    /// let go at once.
    /// </summary>
    public void Retire(int voiceId, VoiceFade fade = VoiceFade.Engine)
    {
        // Nothing sounding to fade: a voice the budget holds is stopped by its engine fade whatever it is
        // doing (the stop takes back a submission waiting for a slot), any other is simply not there.
        if (fade == VoiceFade.Voice && Mixer != null && !Mixer.IsPlaying(voiceId)) return;
        lock (_lock)
        {
            if (_retiring.ContainsKey(voiceId)) return;
            _retiring[voiceId] = fade;
            if (_entries.TryGetValue(voiceId, out var e))
            {
                e.Retiring = true;
                _entries[voiceId] = e;
            }
        }
    }

    /// <summary>Whether a voice was let go and is still fading.</summary>
    public bool IsRetiring(int voiceId) { lock (_lock) return _retiring.ContainsKey(voiceId); }

    /// <summary>Brings back a voice that was fading out: wanted again before its fade ended. True if it was.</summary>
    public bool Unretire(int voiceId)
    {
        VoiceFade fade;
        lock (_lock)
        {
            if (!_retiring.Remove(voiceId, out fade)) return false;
            if (_entries.TryGetValue(voiceId, out var e))
            {
                e.Retiring = false;
                _entries[voiceId] = e;
            }
        }
        if (fade == VoiceFade.Engine) Mixer?.ReviveEngine(voiceId);
        else Mixer?.CancelFade(voiceId);
        return true;
    }

    /// <summary>Once a frame: every voice let go is taken further down its fade, and one that has reached
    /// silence is stopped and is no longer an entry.</summary>
    public void Fades()
    {
        _faded.Clear();
        List<KeyValuePair<int, VoiceFade>> fading;
        lock (_lock) fading = _retiring.ToList();
        foreach (var (id, fade) in fading)
        {
            if (fade == VoiceFade.Voice && Mixer != null && !Mixer.IsPlaying(id))
            {
                // It ended on its own while fading: nothing left to stop.
                lock (_lock) { _retiring.Remove(id); _entries.Remove(id); }
                continue;
            }
            bool silent = Mixer == null || (fade == VoiceFade.Engine ? Mixer.FadeOutEngine(id) : Mixer.FadeOut(id));
            if (silent) _faded.Add(id);
        }
        foreach (int id in _faded) Stop(id);
    }

    /// <summary>How many voices are fading out now.</summary>
    public int RetiringCount { get { lock (_lock) return _retiring.Count; } }
}
