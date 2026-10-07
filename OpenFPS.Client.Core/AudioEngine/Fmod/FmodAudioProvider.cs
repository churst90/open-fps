using System.Numerics;
using FMOD;
using Serilog;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;
using OpenFPS.Client.Core.Platform;

using OpenFPS.Client.AudioEngine.Core.Nature;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// How distance attenuates a voice: FMOD's inverse rolloff, on every 3D channel and bus, the same law
/// as <see cref="OpenFPS.Common.Loudness"/>'s <c>RenderedGain</c>, which every balance is set against.
/// Linear rolloff must not come back: near and far sources sit at nearly one level, and it undoes
/// <c>Widen</c> (a wide source pays its gain down for a reference distance that only an inverse law
/// gives back), so long vehicles were quiet in proportion to their length.
/// </summary>
internal static class Rolloff
{
    public const MODE Mode = MODE._3D_INVERSEROLLOFF;

    /// <summary>Both laws' bits, for clearing the mode before setting 2D.</summary>
    public const MODE Either = MODE._3D_LINEARROLLOFF | MODE._3D_INVERSEROLLOFF;
}

internal static class FmodHelpers
{
    public static FMOD.VECTOR ToFmodVec(Vector3 v) => new FMOD.VECTOR { x = v.X, y = v.Y, z = v.Z };
}

/// <summary>Outcome of asking the resource manager for a sound. A <see cref="Loading"/> sound must be
/// retried and a <see cref="Missing"/> one reported: treating the two alike loses the first play of
/// every sound.</summary>
internal enum SoundLoadState
{
    Ready,
    /// <summary>Load started or still in flight. Ask again shortly.</summary>
    Loading,
    /// <summary>No such asset on disk, or FMOD refused it. Retrying will not help.</summary>
    Missing,
}

internal class FmodResourceManager : IDisposable
{
    private readonly FMOD.System _system;
    private readonly Dictionary<string, FMOD.Sound> _cache = new();
    private readonly HashSet<string> _reportedMissing = new();

    public FmodResourceManager(FMOD.System system) => _system = system;

    /// <summary>Releases a registered synthesised buffer. Stops anything still playing it.</summary>
    public bool ReleasePcm(string soundId)
    {
        if (string.IsNullOrEmpty(soundId) || !_cache.TryGetValue(soundId, out var sound)) return false;
        _cache.Remove(soundId);
        sound.release();
        return true;
    }

    /// <summary><see cref="RegisterPcm"/> from 32-bit float samples, kept as float so a quiet tail is
    /// not cut to the last bit.</summary>
    public bool RegisterPcmFloat(string soundId, float[] pcm, int sampleRate)
    {
        if (string.IsNullOrEmpty(soundId) || pcm.Length == 0) return false;
        if (_cache.ContainsKey(soundId)) return true;
        var bytes = new byte[pcm.Length * 4];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        var info = new CREATESOUNDEXINFO
        {
            cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(),
            length = (uint)bytes.Length,
            numchannels = 1,
            defaultfrequency = sampleRate,
            format = SOUND_FORMAT.PCMFLOAT,
        };
        RESULT res = _system.createSound(bytes,
            MODE.OPENMEMORY | MODE.OPENRAW | MODE._3D | Rolloff.Mode | MODE.LOOP_OFF,
            ref info, out FMOD.Sound sound);
        if (res != RESULT.OK)
        {
            Log.Warning("FmodResourceManager: could not register float sound {Id}: {Result}", soundId, res);
            return false;
        }
        _cache[soundId] = sound;
        _registeredPcm.Add(soundId);
        return true;
    }

    /// <summary>Sounds made in memory (RegisterPcmFloat). One of these asked for as a loop is the same
    /// sound with its channel set to loop: there is no file to load a looping copy from.</summary>
    private readonly HashSet<string> _registeredPcm = new();
    public bool IsRegisteredPcm(string soundId) => _registeredPcm.Contains(soundId);

    /// <summary>
    /// Puts a synthesised buffer into the cache under an id. Every path that plays a sound (placement,
    /// occlusion, reverb, the voice budget) works from an id, so a rendered door latch is heard through
    /// a wall exactly as a recorded one would be.
    /// </summary>
    public bool RegisterPcm(string soundId, byte[] pcm16Mono, int sampleRate)
    {
        if (string.IsNullOrEmpty(soundId) || pcm16Mono.Length == 0) return false;
        if (_cache.ContainsKey(soundId)) return true;

        var info = new CREATESOUNDEXINFO
        {
            cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(),
            length = (uint)pcm16Mono.Length,
            numchannels = 1,
            defaultfrequency = sampleRate,
            format = SOUND_FORMAT.PCM16,
        };

        // OPENRAW: without it FMOD looks for a file header and silently refuses the sound.
        RESULT res = _system.createSound(pcm16Mono,
            MODE.OPENMEMORY | MODE.OPENRAW | MODE._3D | Rolloff.Mode | MODE.LOOP_OFF,
            ref info, out FMOD.Sound sound);
        if (res != RESULT.OK)
        {
            Log.Warning("FmodResourceManager: could not register synthesised sound {Id}: {Result}", soundId, res);
            return false;
        }
        _cache[soundId] = sound;
        return true;
    }

    /// <summary>What FMOD reports about a sound that has not loaded: its open state and how much is
    /// buffered. Diagnostic only.</summary>
    public void DescribeLoad(string soundId, bool loop, out string detail)
    {
        string cacheKey = soundId + (loop ? "_L" : "");
        if (!_cache.TryGetValue(cacheKey, out var s))
        {
            detail = $"(no FMOD sound was ever created for '{cacheKey}')";
            return;
        }
        RESULT r = s.getOpenState(out OPENSTATE state, out uint percent, out bool starving, out bool diskBusy);
        detail = r == RESULT.OK
            ? $"(FMOD openState={state}, {percent}% buffered, starving={starving}, diskBusy={diskBusy})"
            : $"(getOpenState failed: {r} — the sound handle is not usable)";
    }

    /// <summary>
    /// Resolves a sound, starting its nonblocking load on the first request. That first request can
    /// only answer <see cref="SoundLoadState.Loading"/>: a not-yet-ready handle would be refused by
    /// <c>playSound</c> with ERR_NOTREADY, and saying so lets the caller retry instead of dropping the play.
    /// </summary>
    public SoundLoadState TryGetSound(string soundId, out FMOD.Sound sound, bool loop = false)
    {
        sound = default;
        if (string.IsNullOrEmpty(soundId)) return SoundLoadState.Missing;

        string cacheKey = soundId + (loop ? "_L" : "");
        if (loop && _registeredPcm.Contains(soundId)) cacheKey = soundId;
        if (_cache.TryGetValue(cacheKey, out sound))
        {
            sound.getOpenState(out OPENSTATE openState, out _, out _, out _);

            // PLAYING counts as loaded: FMOD reports it for a sound with a channel on it, and taken as
            // "still loading" every further request for a playing loop (a second emitter, a reflection)
            // was parked for ever. A CREATESAMPLE sound is decoded PCM, so PLAYING guarantees what READY does.
            if (openState == OPENSTATE.READY || openState == OPENSTATE.PLAYING) return SoundLoadState.Ready;
            sound = default;
            if (openState == OPENSTATE.ERROR)
            {
                ReportMissing(soundId, "FMOD reported OPENSTATE.ERROR after loading");
                return SoundLoadState.Missing;
            }
            return SoundLoadState.Loading;
        }

        string path = FmodAudioProvider.SoundFilePath(soundId);
        if (path.Length == 0)
        {
            ReportMissing(soundId, "not a path under ASSETS");
            return SoundLoadState.Missing;
        }

        if (!File.Exists(path))
        {
            string[] extensions = { ".wav", ".ogg", ".mp3" };
            bool found = false;
            foreach (var ext in extensions) { if (File.Exists(path + ext)) { path = path + ext; found = true; break; } }
            if (!found)
            {
                ReportMissing(soundId, $"no file at '{path}' (.wav/.ogg/.mp3)");
                return SoundLoadState.Missing;
            }
        }

        MODE mode = MODE.CREATESAMPLE | MODE._3D | Rolloff.Mode | MODE.NONBLOCKING;
        if (loop) mode |= MODE.LOOP_NORMAL;

        RESULT res = _system.createSound(path, mode, out sound);
        if (res != RESULT.OK)
        {
            sound = default;
            ReportMissing(soundId, $"createSound failed: {res}");
            return SoundLoadState.Missing;
        }

        _cache[cacheKey] = sound;

        sound.getOpenState(out OPENSTATE state, out _, out _, out _);
        if (state == OPENSTATE.READY || state == OPENSTATE.PLAYING) return SoundLoadState.Ready;
        sound = default;
        return SoundLoadState.Loading;
    }

    /// <summary>Logs an unresolvable sound once per id, not every frame its emitter is in range.</summary>
    private void ReportMissing(string soundId, string reason)
    {
        if (!_reportedMissing.Add(soundId)) return;
        Log.Warning("Audio asset '{SoundId}' cannot be played: {Reason}. That emitter will be silent.", soundId, reason);
    }

    public void Dispose() { foreach (var s in _cache.Values) s.release(); _cache.Clear(); }
}

public partial class FmodAudioProvider : IAudioProvider
{
    private FMOD.System _system;
    private FmodResourceManager _resources = null!;
    private GranularBank _granularBank = null!;
    private bool _isInitialized = false;

    /// <summary>Voices that played without an HRTF stage because the pool was empty: panned by FMOD,
    /// not placed by Steam Audio.</summary>
    private int _saPoolMisses, _lastSaPoolMisses;
    // Pooled DSPs that could not be detached and were thrown away. Should stay zero; see Detach.
    private int _failedDetaches, _lastFailedDetaches;
    // Pooled DSPs cut loose from the DSP side because their channel was already recycled: the ordinary
    // path for a voice that ended on its own, so non-zero is expected.
    private int _lateDetaches, _lastLateDetaches;

    private readonly System.Collections.Concurrent.ConcurrentStack<FMOD.DSP> _threeEqPool = new();
    private readonly System.Collections.Concurrent.ConcurrentStack<FMOD.DSP> _diffractionPool = new();
    private readonly System.Collections.Concurrent.ConcurrentStack<FMOD.DSP> _sendTapPool = new();

    /// <summary>
    /// A pass-through at the input end of a voice's chain, ahead of the route EQ, the diffraction and
    /// the HRTF. A sound rings its own room whatever stands between it and you, so its own room's
    /// reverb is fed from here; the send into the listener's room stays at the fader, since that room
    /// is rung by what arrives. Fed from the fader, a street sound reached the street's reverb through
    /// the lobby wall and the doorway turned it down a second time.
    /// </summary>
    private FMOD.DSP GetSendTapDsp()
    {
        if (_sendTapPool.TryPop(out var dsp)) { dsp.reset(); return dsp; }
        _system.createDSPByType(DSP_TYPE.MIXER, out dsp);
        return dsp;
    }

    /// <summary>Off the channel, then every connection it still has: a pooled tap that kept a send
    /// would feed a room from the next voice it is given to.</summary>
    private void ReleaseSendTapDsp(ActiveSound active)
    {
        var dsp = active.SendTap;
        if (!dsp.hasHandle()) return;
        active.SendTap = default;
        bool off = Detach(active.Channel, dsp, "send tap");
        dsp.disconnectAll(true, true);
        active.SourceReverbConnection = default;
        active.FadingSourceConnection = default;
        if (off) _sendTapPool.Push(dsp);
    }

    /// <summary>The unit a voice's own-room send hangs off: its tap, or the fader on a voice without one.</summary>
    private static FMOD.DSP SourceSendDsp(ActiveSound active)
    {
        if (active.SendTap.hasHandle()) return active.SendTap;
        active.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var fader);
        return fader;
    }

    /// <summary>What the own-room send must add to stand where a send off the fader stood: the fader's
    /// level, which the tap is ahead of. The route's EQ is the one thing deliberately left out.</summary>
    private static float SourceSendLevel(ActiveSound active)
        => active.SendTap.hasHandle() ? active.LastVolume : 1f;

    private FMOD.DSP GetThreeEqDsp()
    {
        // reset(): the filters keep the previous voice's history, heard as its tail on the new one.
        if (_threeEqPool.TryPop(out var dsp)) { dsp.reset(); dsp.setBypass(false); return dsp; }
        _system.createDSPByType(DSP_TYPE.THREE_EQ, out dsp);
        // The bands every path gain is computed for, set rather than left to FMOD's defaults.
        dsp.setParameterFloat((int)DSP_THREE_EQ.LOWCROSSOVER, OpenFPS.Common.AcousticBands.LowCrossoverHz);
        dsp.setParameterFloat((int)DSP_THREE_EQ.HIGHCROSSOVER, OpenFPS.Common.AcousticBands.HighCrossoverHz);
        return dsp;
    }

    private FMOD.DSP GetDiffractionDsp()
    {
        if (_diffractionPool.TryPop(out var dsp)) { dsp.reset(); dsp.setBypass(false); return dsp; }
        _system.createDSPByType(DSP_TYPE.LOWPASS, out dsp);
        return dsp;
    }

    // Both of these are POOLED, and a pooled DSP is about to be addDSP'd onto a DIFFERENT channel.
    // It has to come off this one first: while it is attached to two, tearing either one down leaves
    // the other holding a connection to something that has gone. Bypassing is not detaching — that
    // was the bug, and setBypass only hid it by making the wrong output silent.
    private void ReleaseThreeEqDsp(FMOD.Channel channel, FMOD.DSP dsp)
    {
        if (!dsp.hasHandle()) return;
        if (!Detach(channel, dsp, "three-band EQ")) return;
        dsp.setBypass(true);
        _threeEqPool.Push(dsp);
    }

    private void ReleaseDiffractionDsp(FMOD.Channel channel, FMOD.DSP dsp)
    {
        if (!dsp.hasHandle()) return;
        if (!Detach(channel, dsp, "diffraction")) return;
        dsp.setBypass(true);
        _diffractionPool.Push(dsp);
    }

    /// <summary>
    /// Takes every room's reverberation unit off its bus and frees both, in that order. FMOD refuses
    /// to release a unit still attached (said only in its logging build: "Failed to release because
    /// unit is still attached", then `connectionsRemaining == 0 failed` at shutdown), and a ChannelGroup
    /// has no <c>stop()</c> that would detach it, so the unit is removed by hand.
    /// </summary>
    private void ReleaseReverbUnits()
    {
        foreach (var kvp in _reverbDsps)
        {
            if (!kvp.Value.hasHandle()) continue;
            if (_reverbBuses.TryGetValue(kvp.Key, out var bus) && bus.hasHandle())
            {
                if (bus.removeDSP(kvp.Value) != RESULT.OK) _failedDetaches++;
            }
            kvp.Value.release();
        }
        foreach (var bus in _reverbBuses.Values) if (bus.hasHandle()) bus.release();
    }

    /// <summary>
    /// Takes a pooled DSP off its channel and says whether it is safe to re-use. A DSP attached to two
    /// channels crashes FMOD when either is torn down (it follows the owner link at +0x78, then +0x10,
    /// and reads address 0x7c). Bypassing does not detach. A DSP that will not detach is dropped, not
    /// pooled: a few hundred bytes lost against the process.
    /// </summary>
    private bool Detach(FMOD.Channel channel, FMOD.DSP dsp, string what)
    {
        if (!dsp.hasHandle()) return false;
        RESULT r = channel.hasHandle() ? channel.removeDSP(dsp) : RESULT.ERR_INVALID_HANDLE;
        if (r == RESULT.OK) return true;

        // The ordinary case: a one-shot that finished had its channel recycled by FMOD before the
        // reaper looked, so the handle is stale. The DSP still sits in that channel's chain, now another
        // sound's; disconnectAll works from the DSP's side and cuts the owner link FMOD crashed on.
        if (dsp.disconnectAll(true, true) == RESULT.OK)
        {
            _lateDetaches++;
            return true;
        }

        _failedDetaches++;
        if (_failedDetaches == 1)
            Log.Warning("A {What} DSP would neither come off its channel ({Result}) nor disconnect "
                      + "itself. It has been dropped rather than pooled; further occurrences are "
                      + "counted, not logged.", what, r);
        return false;
    }

    private class PooledGranularDsp { public FMOD.DSP Dsp; public System.Runtime.InteropServices.GCHandle Handle; public GranularVoiceState State; public PooledGranularDsp(FMOD.DSP dsp, System.Runtime.InteropServices.GCHandle h, GranularVoiceState s) { Dsp = dsp; Handle = h; State = s; } }
    private class PooledSynthDsp { public FMOD.DSP Dsp; public System.Runtime.InteropServices.GCHandle Handle; public SynthVoiceState State; public PooledSynthDsp(FMOD.DSP dsp, System.Runtime.InteropServices.GCHandle h, SynthVoiceState s) { Dsp = dsp; Handle = h; State = s; } }
    
    private readonly System.Collections.Concurrent.ConcurrentStack<PooledGranularDsp> _granularPool = new();
    private readonly System.Collections.Concurrent.ConcurrentStack<PooledSynthDsp> _synthPool = new();

    private PooledGranularDsp? GetGranularDsp(float[] pcm, int ch, int sr)
    {
        if (_granularPool.TryPop(out var pooled))
        {
            pooled.State.Reset();
            pooled.State.PcmData = pcm;
            pooled.State.Channels = ch;
            pooled.State.SampleRate = sr;
            return pooled;
        }
        var state = new GranularVoiceState(pcm, ch, sr);
        if (GranularProcessor.CreateDSP(_system, state, out var dsp, out var handle) == RESULT.OK)
            return new PooledGranularDsp(dsp, handle, state);
        return null;
    }

    private PooledSynthDsp? GetSynthDsp()
    {
        if (_synthPool.TryPop(out var pooled)) { pooled.State.Reset(); return pooled; }
        var state = new SynthVoiceState();
        if (SynthProcessor.CreateDSP(_system, state, out var dsp, out var handle) == RESULT.OK)
        {
            dsp.setChannelFormat(0, 0, SPEAKERMODE.STEREO);
            return new PooledSynthDsp(dsp, handle, state);
        }
        return null;
    }

    private void ReleaseGranularDsp(FMOD.DSP dsp, System.Runtime.InteropServices.GCHandle handle, GranularVoiceState? state)
    {
        // Out of the graph before it is pooled: a still-connected DSP dangles like a released one.
        if (dsp.hasHandle()) dsp.disconnectAll(true, true);
        if (dsp.hasHandle() && state != null) { _granularPool.Push(new PooledGranularDsp(dsp, handle, state)); }
    }

    private void ReleaseSynthDsp(FMOD.DSP dsp, System.Runtime.InteropServices.GCHandle handle, SynthVoiceState? state)
    {
        // Out of the graph before it is pooled: a still-connected DSP dangles like a released one.
        if (dsp.hasHandle()) dsp.disconnectAll(true, true);
        if (dsp.hasHandle() && state != null) { _synthPool.Push(new PooledSynthDsp(dsp, handle, state)); }
    }

    private class ActiveSound
    {
        public int EntityId; 
        public string SoundId = ""; 
        public EmitterType Type;
        public FMOD.Channel Channel; 
        /// <summary>A sound made for this voice alone (a voice-chat packet). Released with the voice,
        /// never before: releasing an FMOD sound stops every channel that is playing it.</summary>
        public FMOD.Sound OwnedSound;
        public FMOD.DSP ThreeEqDsp; 
        public FMOD.DSP DiffractionDsp; 
        
        // Granular state
        public FMOD.DSP GranularDsp;
        public System.Runtime.InteropServices.GCHandle GranularHandle;
        public GranularVoiceState? GranularState;

        // Synth state
        public FMOD.DSP SynthDsp;
        public System.Runtime.InteropServices.GCHandle SynthHandle;
        public SynthVoiceState? SynthState;

        // A live vehicle engine, or a reflection of one
        public FMOD.DSP EngineDsp;
        public System.Runtime.InteropServices.GCHandle EngineHandle;
        public EngineVoiceState? EngineState;
        public EngineEchoState? EchoState;
        /// <summary>One outlet of a machine whose engine belongs to another voice.</summary>
        public EngineTapState? TapState;

        /// <summary>A machine that stands and runs (an air conditioner, a mower). Shares
        /// <see cref="EngineDsp"/> and <see cref="EngineHandle"/> with an engine: a voice is never both,
        /// and there is one release path.</summary>
        public PhysicalVoiceState? MachineState;
        /// <summary>A voice of the player's own room answering their microphone (OwnVoiceTap).</summary>
        public OwnVoiceTap? OwnVoice;

        /// <summary>When this voice's position was last true, seconds on <see cref="OpenFPS.Common.AudioClock"/>:
        /// the emitter's sample time, not when it was handed over. A voice nobody updates keeps playing
        /// where it was while the thing making it drives away.</summary>
        public double LastAttributeAt;

        /// <summary>The oldest this voice's position has been when placed, this report interval. A
        /// maximum, reset when read: sampled at the report, a hold between two reports was invisible.</summary>
        public double WorstPositionAge;

        /// <summary>The budget's gain on this voice and its target (1 holding a slot, 0 having lost
        /// one), slewed in the distance pass so losing or regaining a slot is a fade, for any kind of voice.</summary>
        public float FadeGain = 1f;
        public float FadeTarget = 1f;

        public Vector3 Position; 
        public Vector3 ApparentPosition; 
        public Vector3 CurrentApparentPosition; 
        /// <summary>When <see cref="CurrentApparentPosition"/> was last moved, for its turn rate.</summary>
        public double LastTurnAt;
        public float EffectiveDistance;
        public Vector3 Velocity; 
        public Vector3 Direction;
        public float Range; 
        public float MinDistance;
        public float BaseVolume; 
        public float Pitch;
        
        public float CurrentOcclusion;
        /// <summary>The volume last handed to the channel, before the band EQ. For the census.</summary>
        public float LastVolume;
        /// <summary>The low/mid/high EQ last applied, dB: occlusion, air, shelter, cone. For the census.</summary>
        public (float Low, float Mid, float High) LastEqDb;
        /// <summary>The pop detector's memory (WatchForPops): the level a while ago, when it rose, from where.</summary>
        public float PopBaseDb = float.NaN, PopPeakDb;
        public double PopBaseAt, PopRiseAt = -1, FirstSeenAt = -1, PopLoggedAt = -10;
        public float PopFromOcclusion, PopFromMid;
        public float TargetOcclusion;
        public float CurrentAperture = 1.0f;
        public float TargetAperture = 1.0f;
        public float CurrentBleed;
        public float TargetBleed;
        public float CurrentLow = 1.0f;
        public float TargetLow = 1.0f;
        public float CurrentMid = 1.0f;
        public float TargetMid = 1.0f;
        public float CurrentHigh = 1.0f;
        public float TargetHigh = 1.0f;
        
        /// <summary>What the air takes over the path, dB per band (ISO 9613-1).</summary>
        public float AirLowDb, AirMidDb, AirHighDb;
        public int TargetRegionId = -1; 
        public bool IsReflection; 
        /// <summary>The overloading sound itself: everything else gives way to it, not this.</summary>
        public bool OverloadExempt;
        public bool FollowsListener;
        public bool InsideListenersVehicle;
        public Vector3 ListenerOffset;
        /// <summary>The last batch of wheel strikes handed to this voice's engine: an emitter is applied
        /// over and over until the next one comes, and a batch must be queued once.</summary>
        public OpenFPS.Client.AudioEngine.Core.WheelStrike[]? LastStrikes;
        /// <summary>The direction (Steam Audio's frame) the cabin path's HRTF trim was last worked out for.</summary>
        public Vector3 CabinTrimDir;

        public float ConeInside;
        public float ConeOutside;
        public float ConeOutsideVolume;
        public float ReflectionSpread;
        public int CurrentRegionId = -2;
        public int CurrentSourceRegionId = -2;
        public FMOD.DSPConnection ReverbConnection;
        public FMOD.DSPConnection SourceReverbConnection;
        /// <summary>The pass-through the own-room send is taken from. See GetSendTapDsp.</summary>
        public FMOD.DSP SendTap;
        // The unit each live send feeds, kept beside the connection: a send must fade out of this unit,
        // and looking it up again by region id can find another. See DropSend.
        public FMOD.DSP ReverbBus;
        public FMOD.DSP SourceReverbBus;

        // A send moved to a new room in one frame is two steps on a running signal, on every voice at
        // once, and walking along a wall crosses rooms repeatedly: the reverb clicked. The old
        // connection fades out while the new one fades in, and is dropped once it carries nothing.
        public float ReverbMix;                     // 0..1 of the target mix, ramping in
        public float SourceReverbMix;
        public FMOD.DSPConnection FadingReverbConnection;
        public FMOD.DSP FadingReverbBus;
        public float FadingReverbMix;
        public FMOD.DSPConnection FadingSourceConnection;
        public FMOD.DSP FadingSourceBus;
        public float FadingSourceMix;
        public float RoomGain = 1.0f;

        // Null when Steam Audio is off and FMOD pans the voice.
        public SteamAudioVoiceState? SaState;
        /// <summary>The ear model on this voice (FmodAudioProvider.Ear.cs), or null.</summary>
        public EarVoice? Ear;
        /// <summary>The surface a recorded sound's ground reflection comes off, or null for none.</summary>
        public float? GroundHeight;
        public FMOD.DSP SaDsp;
        /// <summary>This voice's traced echoes, while it is one of the few (UpdateTracedEchoes).</summary>
        public TracedEchoRig? EchoRig;
        /// <summary>How far over to the traced echoes this voice is, 0..1, slewed; and whether it is on
        /// its way back to the ordinary paths.</summary>
        public float EchoWeight;
        public bool EchoLeaving;
        public double EchoSince;
        public System.Runtime.InteropServices.GCHandle SaHandle;
    }

    private readonly List<ActiveSound> _activeSounds = new();

    // The same voices by owning entity (a sound and its reflections, hence a list). The per-frame
    // queries run once per voice, so a scan of the list made a frame cost the square of the voices.
    private readonly Dictionary<int, List<ActiveSound>> _activeById = new();
    private readonly object _lock = new();
    private AcousticMap? _acousticMap;
    private Dictionary<int, FMOD.ChannelGroup> _reverbBuses = new();
    private Dictionary<int, FMOD.DSP> _reverbDsps = new();
    private Dictionary<int, float> _reverbVolumes = new();
    // Per bus, the HRTF voice that places a room's reverb at its doorway when the listener is outside.
    // Borrowed from the voice pool for the bus's life, returned on teardown.
    private readonly Dictionary<int, SaVoice> _reverbSaVoices = new();
    private HashSet<int> _activeRegionIds = new();

    private FMOD.ChannelGroup _reflectionGroup;
    /// <summary>
    /// The player's own voice into their room's reverberation: a group at zero, so the voice itself is
    /// not heard (a late copy of it would be a slap-back) while the channel's sends, which leave from
    /// its own fader, carry it.
    /// </summary>
    private FMOD.ChannelGroup _ownVoiceRoomGroup;

    /// <summary>The physical keys of the player's own voice: the room's reverberation fed from their mouth,
    /// and a copy off one surface round them (ClientAudioSystem.UpdateOwnVoice).</summary>
    public const string OwnVoiceRoomKey = "ownvoice:room", OwnVoiceCopyKey = "ownvoice:copy";

    /// <summary>One playing ambisonic ambience bed. Several can be live, so beds can cross-fade.</summary>
    private sealed class AmbientBed
    {
        public required AmbisonicBedState State;
        public FMOD.DSP Dsp;
        public FMOD.Channel Channel;
        public System.Runtime.InteropServices.GCHandle Handle;
    }

    private readonly Dictionary<string, AmbientBed> _ambientBeds = new();

    // Near-field boundary reflections (see BoundaryProximityProcessor). Held for the provider's life.
    private FMOD.DSP _boundaryDsp;
    private System.Runtime.InteropServices.GCHandle _boundaryHandle;
    private BoundaryVoiceState? _boundaryState;
    private FMOD.DSP _masterLimiter;
    private MasterLimiter? _trueLimiter;
    private FMOD.DSP _loudnessMeter;
    private MasterTap? _masterTap;
    private MasterTap? _preLimiterTap;
    private MasterDither? _dither;

    // The wind at the listener's ears (EarWindVoice). One generator, flat on the master, for the
    // provider's life; silent while nobody is in a world.
    private EarWindState? _earWind;
    private FMOD.DSP _earWindDsp;
    private FMOD.Channel _earWindChannel;
    private System.Runtime.InteropServices.GCHandle _earWindHandle;
    private EngineRenderPool? _enginePool;
    private readonly List<IRenderedVoice> _engineSnapshot = new();

    private readonly List<(float Distance, IRenderedVoice Voice)> _engineOrder = new();

    /// <summary>
    /// Every voice the pool renders ahead, engines and standing machines in one list, nearest first;
    /// copied under the lock so the pool never walks a list the mixer is changing. One list, so a
    /// distant condenser is never rendered ahead of the truck beside you; nearest first, so when the
    /// machine cannot keep every ring ahead (a map's first seconds) the shortfall lands on a whisper.
    /// </summary>
    private List<IRenderedVoice> SnapshotEngineVoices()
    {
        _engineSnapshot.Clear();
        _engineOrder.Clear();
        _snapshotTrains.Clear();
        lock (_lock)
            foreach (var a in _activeSounds)
            {
                if (a.EngineState != null) _engineOrder.Add((a.EffectiveDistance, a.EngineState));
                else if (a.MachineState != null)
                {
                    _engineOrder.Add((a.EffectiveDistance, a.MachineState));
                    // A train's lanes render what its voices read: listed once, just ahead of its nearest voice.
                    if (a.MachineState is TrainSlotState tv && _snapshotTrains.Add(tv.Shared))
                        foreach (var lane in tv.Shared.Lanes) _engineOrder.Add((a.EffectiveDistance - 0.01f, lane));
                }
            }
        _engineOrder.Sort(static (x, y) => x.Distance.CompareTo(y.Distance));
        foreach (var e in _engineOrder) _engineSnapshot.Add(e.Voice);
        return _engineSnapshot;
    }
    private readonly HashSet<TrainVoiceState> _snapshotTrains = new();

    /// <summary>
    /// Makeup gain on the master, dB: how loud the game plays, decided in this one place (see the
    /// gain-staging note in Initialize). It moves no balance: every voice plays where Loudness.Place
    /// puts it for 0 dBFS = 100.8 dB SPL (Loudness.DesignFullScaleDb), a voice at a metre about -38
    /// dBFS RMS; a player who calibrates sets their volume 6 dB lower, 0 dBFS out = 94.8 dB SPL.
    ///
    /// Six: September's level by meter and ear (ten for the speedway at -19 to -23 LUFS short-term,
    /// three back for the ground, two for the street), less the 3.01 dB the binaural stage no longer
    /// loses (SteamAudioDsp). Measured with --quality: four cars on a street -35 LUFS, no limiting;
    /// shots at 1.5 to 30 m -26.6 LUFS, -0.9 dBTP, the limiter taking 2.9 dB on average. Read the "Mix
    /// loudness" line first: once the limiter works, more makeup buys compression, not loudness.
    /// Override with OPENFPS_MASTER_MAKEUP_DB.
    /// </summary>
    public static readonly float MasterMakeupDb =
        float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_MASTER_MAKEUP_DB"), out float mk)
            ? Math.Clamp(mk, 0f, 40f) : 6f;

    /// <summary>A trim on the master for a run, dB, on top of the makeup: OPENFPS_MASTER_DB, 0 when unset.</summary>
    public static readonly float MasterTrimDb =
        float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_MASTER_DB"),
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out float mdb)
            ? Math.Clamp(mdb, -24f, 24f) : 0f;

    private Vector3 _listenerPos = Vector3.Zero;
    private Vector3 _listenerVel = Vector3.Zero;
    private Quaternion _listenerRot = Quaternion.Identity;
    private int _listenerRegionId = -1;
    private float _shelterFactor = 0.0f;

    // Geometry-driven reverb decay (ms) from the Steam Audio reflection sim; 0 = keep the Sabine estimate.
    private float _simReverbDecayMs;

    // OPENFPS_AUDIO_DEBUG=1 logs each source's HRTF direction and the listener's yaw about once a second.
    private static readonly bool _audioDebug = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";
    private int _dbgFrame;

    private IntPtr _saContext;
    private IntPtr _saHrtf;
    /// <summary>The HRTF the traced reverb decodes through, made for its own block
    /// (TracedReverb.TracedFrame): one built for the mixer's 1024 and run at another size comes out 2 dB
    /// hot and is not the same head (measured with --traced-reverb and SA_HRTF_FRAME).</summary>
    private IntPtr _saHrtfTraced;
    private int _saHrtfTracedFrame;
    /// <summary>How long a traced stage's input waits, samples (TracedReverbDsp.StagePreDelay); -1 until measured.</summary>
    private int _tracedPreDelay = -1;
    private int _saFrameSize = 1024;
    private bool _steamAudioEnabled;

    // Steam Audio voices (binaural effect, Phonon buffers, FMOD DSP), made once at init and reused,
    // never created or freed at runtime: freeing Phonon resources while the mixer thread was in a
    // callback on them corrupted the native heap under footstep and reflection churn
    // (ProviderOrbit.RunChurn reproduces it).
    private sealed class SaVoice
    {
        public SteamAudioVoiceState State = null!;
        public FMOD.DSP Dsp;
        public System.Runtime.InteropServices.GCHandle Handle;
    }
    private readonly Stack<SaVoice> _saPool = new();

    /// <summary>
    /// GCHandles of released DSPs, kept until Dispose. FMOD may call a DSP's read callback once more
    /// after release, and the callback first resolves this handle: freed with the voice, it was a
    /// use-after-free on the mixer thread ("libfmod called libcoreclr and libcoreclr aborted", no
    /// exception, no log line). Sixteen bytes per retired voice; freed once the system is closed.
    /// </summary>
    private readonly List<System.Runtime.InteropServices.GCHandle> _retiredHandles = new();
    private readonly List<SaVoice> _saAllVoices = new();
    /// <summary>
    /// How many binaural voices exist; idle ones cost only memory. At 96 the city ran the pool dry
    /// (each region's reverb return holds one, each echo takes one) and the newest voices, the cars
    /// passing close, fell to FMOD's panner with no interaural time difference: "anything that passes
    /// close to me is inverted".
    /// </summary>
    private const int SaPoolSize = 160;

    /// <summary>Binaural voices only a direct sound may take: an echo or reverb return without HRTF is
    /// not heard as wrong, a passing car is.</summary>
    private const int SaDirectReserve = 24;

    /// <summary>What is left of the binaural pool. See IAudioProvider.SpatialVoicesFree.</summary>
    public int SpatialVoicesFree { get { lock (_saPool) return _saPool.Count; } }

    /// <summary>Logs and returns false when an FMOD call fails: a discarded result code is silence with
    /// no explanation.</summary>
    private static bool FmodCheck(RESULT result, string operation)
    {
        if (result != RESULT.OK)
        {
            Log.Error("FMOD {Operation} failed: {Code} — {Message}", operation, result, FMOD.Error.String(result));
            return false;
        }
        return true;
    }

    public bool Initialize()
    {
        try
        {
            // Every custom DSP is a managed callback entered from FMOD's native mixer thread, and that
            // thread waits out any GC suspension; a map load triggers a run of them. SustainedLowLatency
            // keeps gen2 in the background (the hundreds-of-milliseconds pause). Set here because it is a
            // property of having managed DSPs, which both heads do. See docs/AUDIO_LOAD_DROPOUTS.md.
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            if (!FmodCheck(Factory.System_Create(out _system), "System_Create")) return false;

            // The listener's frame (+X right, +Y up, +Z forward) is FMOD's default left-handed one.
            // _3D_RIGHTHANDED puts a +X source on the left (verified by ear 2026-06): leave it unset.
            // The mixer runs at 48 kHz (MixerQuality.RequestedRate; OPENFPS_MIXER_RATE overrides), the
            // devices' and the renders' rate. Read it back (MixerQuality.MixerRate), never a constant.
            FmodCheck(_system.setSoftwareFormat(MixerQuality.RequestedRate, SPEAKERMODE.STEREO, 0), "setSoftwareFormat");

            // OPENFPS_FMOD_WAV=<path> replaces the sound card with a file writer: exact and silent, for
            // a rig. OPENFPS_AUDIO_CAPTURE taps the master and keeps playing, for finding something by ear.
            string? wavPath = Environment.GetEnvironmentVariable("OPENFPS_FMOD_WAV");
            IntPtr extra = IntPtr.Zero;
            if (string.IsNullOrEmpty(wavPath)) MixerQuality.ApplyOutput(_system);
            if (!string.IsNullOrEmpty(wavPath))
            {
                FmodCheck(_system.setOutput(OUTPUTTYPE.WAVWRITER), "setOutput(WAVWRITER)");
                extra = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(wavPath);
                Log.Information("FMOD output is being captured to {Path} (silent: this replaces the sound card)", wavPath);
            }
            FmodCheck(_system.set3DSettings(1.0f, 1.0f, 1.0f), "set3DSettings"); // 1 unit = 1 metre

            // Eight buffers of 1024 (FMOD's Linux default is four, 93 ms): about 185 ms of stall the
            // mixer can absorb, for 93 ms more latency. Insurance, not a fix. docs/AUDIO_LOAD_DROPOUTS.md.
            FmodCheck(_system.setDSPBufferSize(1024, 8), "setDSPBufferSize");

            // Real (mixed) voices, before init: afterwards FMOD refuses with ERR_INITIALIZED and the
            // limit silently stays at 64. Past it FMOD stops rendering the quietest voices, and a
            // synthesised voice resumed later jumps in pitch: every pass-by on the speedway (91 playing
            // with 64 real). 256 leaves room for footsteps, weapons and ambience over a full field.
            FmodCheck(_system.setSoftwareChannels(256), "setSoftwareChannels");

            // VOL0_BECOMES_VIRTUAL must stay unset: virtualising a voice at zero volume (occlusion behind
            // a wall, a fading reflection) stops its stateful DSP chain and resumes it against a signal
            // that moved on. It was the "popping from the reverb as I walk near the walls" (five
            // discontinuities in 46 s of a captured walk). The engine rations voices by audibility
            // itself. See docs/REPEATS_AND_POPS.md.
            // The resampler for every voice not at the mixer's rate, Doppler included. See MixerQuality.
            MixerQuality.ApplyResampler(_system);

            if (!FmodCheck(_system.init(512, INITFLAGS.NORMAL, extra), "init"))
                return false;

            _system.getSoftwareFormat(out int mixRate, out _, out _);
            MixerQuality.MixerRate = mixRate;

            // What was granted: a limit that silently stayed at its default was the speedway's bug.
            _system.getSoftwareChannels(out int realChannels);
            Log.Information("FMOD software channels: {Real} real voices (512 virtual).", realChannels);


            _system.getVersion(out uint version);
            Log.Information("FMOD initialized: v{Major:X}.{Minor:X2}.{Patch:X2}, left-handed 3D (+X right / +Z fwd), mixer {Rate} Hz stereo.",
                (version >> 16) & 0xFFFF, (version >> 8) & 0xFF, version & 0xFF, mixRate);
            // And what the device runs at: a mixer at the device's rate is handed over unresampled.
            if (_system.getDriver(out int driver) == RESULT.OK
                && _system.getDriverInfo(driver, out string driverName, 256, out _, out int deviceRate, out _, out _) == RESULT.OK)
            {
                _system.getOutput(out OUTPUTTYPE outputType);
                Log.Information("Audio output: {Output}, device '{Device}' at {DeviceRate} Hz; the mixer is at {Rate} Hz{Note}.",
                                outputType, driverName, deviceRate, mixRate,
                                deviceRate > 0 && deviceRate != mixRate ? " (the output resamples)" : "");
            }

            _resources = new FmodResourceManager(_system);
            _granularBank = new GranularBank(_system);
            _system.createChannelGroup("Reflections", out _reflectionGroup);
            _system.getMasterChannelGroup(out var master);
            master.addGroup(_reflectionGroup);
            _system.createChannelGroup("Own voice, into the room only", out _ownVoiceRoomGroup);
            master.addGroup(_ownVoiceRoomGroup);
            _ownVoiceRoomGroup.setVolume(0f);
            // Interface sounds have their own group so a world fade can leave them alone.
            _system.createChannelGroup("Interface", out _uiGroup);
            master.addGroup(_uiGroup);

            // How loud the game plays is the makeup's and the balance is the law's; this is a trim for
            // a run. Never per source: nudging presets for loudness undoes what made them agree.
            float masterDb = MasterTrimDb;
            _masterTrim = MathF.Pow(10f, masterDb / 20f);
            if (MathF.Abs(masterDb) > 0.01f)
            {
                master.setVolume(_masterTrim);
                Log.Information("Master trim: {Db:F1} dB (OPENFPS_MASTER_DB).", masterDb);
            }

            // Near-field boundary reflections, at the tail so every world sound passes through them.
            // FMOD runs a chain tail -> head -> output: the tail is processed first, so the limiter goes
            // at the head, after these, where it can catch a reflection pushing past the ceiling.
            // The mixer's rate, not the sound card's: that put every boundary delay 9 % long at 48 kHz.
            _boundaryState = new BoundaryVoiceState(MixerQuality.MixerRate);
            if (BoundaryProximityProcessor.CreateDSP(_system, _boundaryState, out _boundaryDsp, out _boundaryHandle) == RESULT.OK)
                master.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, _boundaryDsp);
            else
                Log.Warning("Boundary proximity DSP could not be created; walls will not colour the mix.");

            // Gain staging. Every source plays at its true level against every other (Loudness.Place,
            // then 1/r), never fiddled per sound: it is how a rifle at 200 m is told from a pistol at 20.
            // So the mix sits low by design (a voice at a metre about -38 dBFS RMS, an outdoor scene
            // another 30 dB down for distance), and the headroom is taken back here, once, for
            // everything: the makeup lifts the whole mix and the brick wall catches what goes over, so
            // more sources change what you hear, never how loud the master is.
            // The brick wall is a look-ahead true-peak limiter (MasterLimiter, 2 ms): FMOD's has no
            // look-ahead and flat-topped every shot's leading edge (975 runs in a 16-minute capture).
            // OPENFPS_LIMITER=fmod puts FMOD's back for an A/B.
            if (!MasterLimiter.UseFmodLimiter
                && (_trueLimiter = MasterLimiter.Create(_system, MixerQuality.MixerRate, MasterMakeupDb)) != null)
            {
                _masterLimiter = _trueLimiter.Dsp;
                Log.Information("Master limiter: look-ahead true peak, ceiling {Ceiling:F1} dBTP, makeup {Makeup:F1} dB, latency {Samples} samples ({Ms:F2} ms).",
                                TruePeakLimiter.DefaultCeilingDb, MasterMakeupDb, _trueLimiter.Core.LatencySamples, _trueLimiter.Core.LatencySeconds * 1000.0);
            }
            else
            {
                _system.createDSPByType(DSP_TYPE.LIMITER, out _masterLimiter);
                _masterLimiter.setParameterFloat(0, 50.0f);            // release time (ms)
                // -2, not -1: without look-ahead a transient got through at +1.6 dBFS with the ceiling at -1.
                _masterLimiter.setParameterFloat(1, -2.0f);            // ceiling (dBFS)
                _masterLimiter.setParameterFloat(2, MasterMakeupDb);   // maximizer gain (dB)
                Log.Information("Master limiter: FMOD's (OPENFPS_LIMITER=fmod), no look-ahead, ceiling -2.0 dBFS.");
            }
            master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, _masterLimiter);

            // Integrated loudness (LUFS) of what leaves the mixer: read it before changing any level.
            // At the head after the limiter; ahead of it, the meter showed the makeup doing nothing.
            if (_system.createDSPByType(DSP_TYPE.LOUDNESS_METER, out _loudnessMeter) == RESULT.OK)
            {
                master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, _loudnessMeter);
                _loudnessMeter.setParameterInt(0, 1);   // state: start metering
            }

            string? capturePath = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE");
            if (!string.IsNullOrEmpty(capturePath))
            {
                _system.getSoftwareFormat(out int capRate, out _, out _);
                _masterTap = MasterTap.Attach(_system, master, capturePath, capRate > 0 ? capRate : MixerQuality.MixerRate,
                                              asFloat: MixerQuality.CaptureFloat);
                if (_masterTap != null) Log.Information("Capturing the mix to {Path} (playback continues).", capturePath);
                else Log.Warning("Could not attach the capture tap; playback is unaffected.");
            }
            // The mix as it reaches the limiter, to compare with what leaves it (the lab's --quality limiter).
            string? prePath = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE_PRE");
            if (!string.IsNullOrEmpty(prePath) && master.getDSPIndex(_masterLimiter, out int limiterAt) == RESULT.OK)
            {
                _system.getSoftwareFormat(out int preRate, out _, out _);
                _preLimiterTap = MasterTap.Attach(_system, master, prePath, preRate > 0 ? preRate : MixerQuality.MixerRate,
                                                  index: limiterAt + 1, asFloat: MixerQuality.CaptureFloat);
            }
            // Last, after the meter and the capture: dither for the sixteen-bit output.
            _dither = MasterDither.Attach(_system, master);
            if (_dither != null) Log.Information("Master dither: triangular, one 16-bit step (OPENFPS_DITHER=0 leaves it out).");

            StartEarWind();

            TryInitSteamAudio();

            // Engines render ahead, off the mixer thread. See EngineRenderPool.
            _enginePool = new EngineRenderPool(SnapshotEngineVoices);

            _isInitialized = true;
            return true;
        }
        catch (Exception ex) { Log.Error(ex, "Failed to initialize FMOD"); return false; }
    }

    /// <summary>
    /// Starts the wind at the ears: a generator played 2D on the master, so it gets no reverb send and
    /// no binaural placement, the way the ambience beds play their finished stereo. It is part of the
    /// listener, not a sound in the world. OPENFPS_EAR_WIND=0 leaves it silent.
    /// </summary>
    private void StartEarWind()
    {
        _system.getSoftwareFormat(out int rate, out _, out _);
        var state = new EarWindState(rate > 0 ? rate : MixerQuality.MixerRate);
        if (Environment.GetEnvironmentVariable("OPENFPS_EAR_WIND") == "0")
        {
            state.Enabled = false;
            Log.Information("Ear wind: OFF (OPENFPS_EAR_WIND=0).");
        }
        if (EarWindProcessor.CreateDSP(_system, state, out var dsp, out var handle) != RESULT.OK)
        {
            Log.Warning("Ear wind: the DSP could not be created; there will be no wind at the ears.");
            return;
        }
        if (_system.playDSP(dsp, default, false, out var channel) != RESULT.OK)
        {
            dsp.release();
            if (handle.IsAllocated) handle.Free();
            Log.Warning("Ear wind: FMOD would not play the DSP; there will be no wind at the ears.");
            return;
        }
        channel.setMode(MODE._2D);
        _earWind = state;
        _earWindDsp = dsp;
        _earWindChannel = channel;
        _earWindHandle = handle;
        AttachEarToWind();
    }

    /// <summary>Where the listener is, for the wind at their ears; null when nobody is in a world.</summary>
    public void SetEarWind(OpenFPS.Common.EarWindListener? listener) => _earWind?.SetListener(listener);

    /// <summary>The wind at the ears on or off, for the lab, which measures one source at a time.</summary>
    public bool EarWindEnabled
    {
        get => _earWind?.Enabled ?? false;
        set { if (_earWind != null) _earWind.Enabled = value; }
    }

    /// <summary>Diagnostics: what the ears were last placed at, dBFS, and what the wind is there.</summary>
    public (float LeftDbfs, float RightDbfs, OpenFPS.Common.EarWindAtEars Ears) EarWindLevels
        => _earWind is { } w ? (w.Synth.RenderedLeftDb, w.Synth.RenderedRightDb, w.Synth.Last) : (-150f, -150f, default);

    /// <summary>Steam Audio's HRTF stage and its voice pool. Without it, FMOD pans.</summary>
    private void TryInitSteamAudio()
    {
        // OPENFPS_HRTF=0 leaves the binaural stage out: a bisect lever for a crash on an FMOD thread
        // (the stage is the largest native surface in the mixer callback), not a setting.
        if (Environment.GetEnvironmentVariable("OPENFPS_HRTF") == "0")
        {
            Log.Warning("OPENFPS_HRTF=0 — the Steam Audio binaural stage is OFF. Spatial cues are FMOD "
                      + "panning only. This is a diagnostic lever; unset it for normal listening.");
            return;
        }
        try
        {
            _system.getDSPBufferSize(out uint block, out int _);
            _saFrameSize = (int)block;
            var cs = Phonon.DefaultContextSettings();
            string simd = Phonon.SimdLevelName(cs.simdLevel);
            if (Phonon.iplContextCreate(ref cs, out _saContext) != Phonon.IPL_STATUS_SUCCESS)
            { Log.Warning("Steam Audio: context create failed (SIMD {Simd}); DEGRADED to FMOD panning — no HRTF binaural.", simd); return; }

            var au = new Phonon.IPLAudioSettings { samplingRate = MixerQuality.MixerRate, frameSize = _saFrameSize };
            var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
            if (Phonon.iplHRTFCreate(_saContext, ref au, ref hs, out _saHrtf) != Phonon.IPL_STATUS_SUCCESS)
            { Log.Warning("Steam Audio: HRTF create failed; DEGRADED to FMOD panning — no HRTF binaural."); Phonon.iplContextRelease(ref _saContext); return; }

            _steamAudioEnabled = true;
            for (int i = 0; i < SaPoolSize; i++)
            {
                if (!CreatePooledVoice(out var v)) break;
                _saAllVoices.Add(v);
                _saPool.Push(v);
            }
            Log.Information("Steam Audio HRTF binaural enabled (SIMD={Simd}, frameSize={Frame}); {Pooled} voices pooled.", simd, _saFrameSize, _saAllVoices.Count);
        }
        catch (DllNotFoundException)
        {
            Log.Warning("Steam Audio: {Lib} not found; DEGRADED to FMOD panning — no HRTF binaural. " +
                        "Spatial cues will be stereo pan only.", NativeAudioLibraries.PhononFileName);
        }
        catch (Exception ex) { Log.Warning(ex, "Steam Audio init failed; DEGRADED to FMOD panning — no HRTF binaural."); }
    }

    /// <summary>The mixer's rate, which the binaural stage's input runs at.</summary>
    private float SaGroundRate()
    {
        _system.getSoftwareFormat(out int rate, out _, out _);
        return rate > 0 ? rate : MixerQuality.MixerRate;
    }

    /// <summary>One pooled voice (effect, Phonon buffers, DSP). Init only.</summary>
    private bool CreatePooledVoice(out SaVoice voice)
    {
        voice = null!;
        var au = new Phonon.IPLAudioSettings { samplingRate = MixerQuality.MixerRate, frameSize = _saFrameSize };
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = _saHrtf };
        if (Phonon.iplBinauralEffectCreate(_saContext, ref au, ref es, out IntPtr effect) != Phonon.IPL_STATUS_SUCCESS) return false;

        var s = new SteamAudioVoiceState
        {
            Context = _saContext, Hrtf = _saHrtf, Effect = effect, FrameSize = _saFrameSize,
            MonoScratch = new float[_saFrameSize], StereoScratch = new float[_saFrameSize * 2],
            Ground = new OpenFPS.Client.AudioEngine.Acoustics.GroundReflection(SaGroundRate())
        };
        Phonon.iplAudioBufferAllocate(_saContext, 1, _saFrameSize, ref s.InBuf);
        Phonon.iplAudioBufferAllocate(_saContext, 2, _saFrameSize, ref s.OutBuf);
        // The ground's own HRTF. Without it the stage still plays, with no ground.
        if (Phonon.iplBinauralEffectCreate(_saContext, ref au, ref es, out IntPtr groundEffect) == Phonon.IPL_STATUS_SUCCESS)
        {
            s.GroundEffect = groundEffect;
            Phonon.iplAudioBufferAllocate(_saContext, 1, _saFrameSize, ref s.GroundInBuf);
            Phonon.iplAudioBufferAllocate(_saContext, 2, _saFrameSize, ref s.GroundOutBuf);
            s.GroundMono = new float[_saFrameSize];
            s.GroundStereo = new float[_saFrameSize * 2];
        }

        if (SteamAudioDsp.CreateDSP(_system, s, out var dsp, out var handle) != RESULT.OK)
        {
            Phonon.iplAudioBufferFree(_saContext, ref s.InBuf);
            Phonon.iplAudioBufferFree(_saContext, ref s.OutBuf);
            Phonon.iplBinauralEffectRelease(ref effect);
            FreeGroundPath(s);
            return false;
        }
        voice = new SaVoice { State = s, Dsp = dsp, Handle = handle };
        return true;
    }

    private static bool HasGround(in SpatialEmitter e) => e.GroundLowGain > 0f || e.GroundHighGain > 0f;

    private void FreeGroundPath(SteamAudioVoiceState s)
    {
        if (s.GroundEffect == IntPtr.Zero) return;
        Phonon.iplAudioBufferFree(_saContext, ref s.GroundInBuf);
        Phonon.iplAudioBufferFree(_saContext, ref s.GroundOutBuf);
        IntPtr g = s.GroundEffect;
        Phonon.iplBinauralEffectRelease(ref g);
        s.GroundEffect = IntPtr.Zero;
    }

    /// <summary>Borrows a voice from the pool, leaving <paramref name="leave"/> in it. False when there
    /// are none: the sound then plays without HRTF.</summary>
    private bool TryCreateSteamAudioVoice(out SteamAudioVoiceState? state, out FMOD.DSP dsp, out System.Runtime.InteropServices.GCHandle handle,
                                          int leave = 0)
    {
        state = null; dsp = default; handle = default;
        SaVoice v;
        lock (_saPool)
        {
            if (_saPool.Count <= leave) return false;
            v = _saPool.Pop();
        }
        // An effect still holding the last sound's overlap-add tail clicks on the first frame. Safe to
        // reset: ReleaseSteamAudioVoice detached the DSP.
        Phonon.iplBinauralEffectReset(v.State.Effect);
        if (v.State.GroundEffect != IntPtr.Zero) Phonon.iplBinauralEffectReset(v.State.GroundEffect);
        v.State.Ground?.Reset();
        v.State.GroundDirX = 0f; v.State.GroundDirY = -1f; v.State.GroundDirZ = 0f;
        v.State.DirX = 0f; v.State.DirY = 0f; v.State.DirZ = -1f;
        // Fully placed: a reverb bus gives its stage back at blend 0, and after a /join half the voices
        // that borrowed one played mono.
        v.State.SpatialBlend = 1f;
        v.State.PreEq = null;
        Array.Clear(v.State.PreEqState);
        v.State.LastRmsL = v.State.LastRmsR = 0f;
        v.State.GuardName = null; v.State.NonFiniteReported = 0; v.State.NonFiniteInputReported = 0;
        state = v.State; dsp = v.Dsp; handle = v.Handle;
        return true;
    }

    private void ReleaseSteamAudioVoice(ActiveSound a)
    {
        if (a.SaState == null) return;
        // removeDSP waits out a callback in flight. Nothing is freed here (freeing raced the mixer and
        // corrupted the heap); Dispose frees it. A voice that will not detach is not pooled again.
        bool reusable = !a.SaDsp.hasHandle() || Detach(a.Channel, a.SaDsp, "binaural");
        if (reusable)
        {
            var v = new SaVoice { State = a.SaState, Dsp = a.SaDsp, Handle = a.SaHandle };
            lock (_saPool) { _saPool.Push(v); }
        }
        a.SaState = null; a.SaDsp = default; a.SaHandle = default;
    }

    // The last five simulated decays, for the median. See SetSimulatedReverbDecay.
    private readonly float[] _simReverbHistory = new float[5];
    private int _simReverbCount;

    // hfDecayRatio and lfDecayRatio are not used, and need not be: the tail is the traced response, whose
    // colour per band comes from the materials it was traced with (docs/CLIENT_NOTES.md, "Traced reverb
    // everywhere"). Measured with --probable-bugs scene=rooms: a carpeted flat and a tiled stairwell.
    /// <summary>
    /// The ray tracer's reverberation time for the listener's surroundings, median-filtered over the
    /// last five readings. The measurement is stochastic (one street canyon gave 1729, 2725 and 2800 ms)
    /// and now and then a run finds nothing and reports zero: a median rejects that dropout, where a mean
    /// would be dragged down by it and a floor could not tell it from a real open field. The enclosure
    /// is the target the listener's room walks toward (AdvanceListenerRoom).
    /// </summary>
    public void SetSimulatedReverbDecay(float decayMs, float enclosure, float hfDecayRatio, float lfDecayRatio)
    {
        _enclosureTarget = enclosure;
        if (_listenerEnclosure < 0f) _listenerEnclosure = enclosure;   // the first measurement is not a move
        for (int i = _simReverbHistory.Length - 1; i > 0; i--)
            _simReverbHistory[i] = _simReverbHistory[i - 1];
        _simReverbHistory[0] = decayMs;
        if (_simReverbCount < _simReverbHistory.Length) _simReverbCount++;

        Span<float> sorted = stackalloc float[_simReverbCount];
        for (int i = 0; i < _simReverbCount; i++) sorted[i] = _simReverbHistory[i];
        sorted.Sort();
        _simReverbDecayMs = sorted[_simReverbCount / 2];
    }

    /// <summary>The median-filtered decay, for the spikes and the profiler.</summary>
    public float SimulatedReverbDecayMs => _simReverbDecayMs;

    /// <summary>How enclosed the listener's surroundings are, 0..1: the value in use, slewed toward
    /// <see cref="_enclosureTarget"/> by <see cref="AdvanceListenerRoom"/>. See OpenFPS.Common.Enclosure.</summary>
    private float _listenerEnclosure = -1f;

    /// <summary>The last measured enclosure, which <see cref="_listenerEnclosure"/> walks toward.</summary>
    private float _enclosureTarget;
    private double _roomAdvancedAt;

    // From the world's air temperature; 20 °C until the weather says otherwise.
    private float _speedOfSound = OpenFPS.Client.AudioEngine.Core.AudioPhysics.SpeedOfSound;
    public void SetAirTemperature(float celsius)
    {
        _speedOfSound = OpenFPS.Client.AudioEngine.Core.AudioPhysics.SpeedOfSoundAt(celsius);
        OpenFPS.Client.AudioEngine.Core.AudioPhysics.CurrentSpeedOfSound = _speedOfSound;
        OpenFPS.Client.AudioEngine.Core.AudioPhysics.CurrentAirCelsius = celsius;
    }

    /// <summary>
    /// Walks the listener's enclosure toward what the rays just measured, with the room's own time
    /// constant. The measurement steps (a car park's mouth: 40 % to 89 % enclosed between two samples)
    /// and applied directly every voice's reverberation jumped 17.5 dB in one two-metre step (measured
    /// with `--enclosure map=city walk=-4,30:-30,30`), a click in and a pop out. A reverberant field
    /// builds and dies over its own decay, so the walk is what a doorway sounds like, not a smoothing
    /// of a bad measurement.
    /// </summary>
    private void AdvanceListenerRoom()
    {
        double now = OpenFPS.Common.AudioClock.Now;
        float dt = _roomAdvancedAt > 0 ? (float)(now - _roomAdvancedAt) : 0f;
        _roomAdvancedAt = now;
        if (dt <= 0f || dt > 0.5f) return;   // a stall is not a walk across a room

        // Floored so a dead room still takes a moment, capped so a cathedral does not lag a listener
        // who has walked out of it.
        float tau = Math.Clamp(_simReverbDecayMs * 0.001f * 0.5f, 0.12f, 0.6f);
        float a = 1f - MathF.Exp(-dt / tau);
        _listenerEnclosure += (_enclosureTarget - _listenerEnclosure) * a;
    }

    private void ApplySimulatedReverb() => AdvanceListenerRoom();

    public void SetAcousticMap(AcousticMap map)
    {
        lock (_lock)
        {
            if (_acousticMap == map) return;
            ClearReverbBuses();
            _acousticMap = map; 
            foreach (var active in _activeSounds) 
            { 
                active.CurrentRegionId = -2; 
                active.CurrentSourceRegionId = -2; 
                active.ReverbConnection = default;
                active.SourceReverbConnection = default;
                active.ReverbBus = default;
                active.SourceReverbBus = default;
                // Their buses are released: forgetting a fading send is the whole of its cleanup.
                active.FadingReverbConnection = default;
                active.FadingSourceConnection = default;
                active.FadingReverbBus = default;
                active.FadingSourceBus = default;
                active.ReverbMix = 0f;
                active.SourceReverbMix = 0f;
            }
        }
    }

    private void ClearReverbBuses()
    {
        ReturnReverbVoices(); // while the buses still exist
        // Each traced stage off its bus while the bus exists: removeDSP waits out a callback in flight,
        // so after it nothing reads the stage's native memory. An attached unit cannot be released.
        foreach (var (regionId, (_, dsp, _)) in _traced)
        {
            if (!dsp.hasHandle()) continue;
            if (_reverbBuses.TryGetValue(regionId, out var bus) && bus.hasHandle()
                && bus.removeDSP(dsp) != RESULT.OK) _failedDetaches++;
            dsp.release();
        }
        ReleaseReverbUnits();
        _reverbDsps.Clear(); _reverbBuses.Clear(); _reverbVolumes.Clear(); _regionDecaySeconds.Clear();
        foreach (var (st, _, handle) in _traced.Values)
        {
            st.Trace = null;
            if (st.Effect != IntPtr.Zero) Phonon.iplReflectionEffectRelease(ref st.Effect);
            if (st.Decode != IntPtr.Zero) Phonon.iplAmbisonicsDecodeEffectRelease(ref st.Decode);
            st.Diffuse?.Release(); st.Diffuse = null;
            if (st.Mono.data != IntPtr.Zero) Phonon.iplAudioBufferFree(st.WorkerContext, ref st.Mono);
            if (st.Ambi.data != IntPtr.Zero) Phonon.iplAudioBufferFree(st.WorkerContext, ref st.Ambi);
            if (st.Stereo.data != IntPtr.Zero) Phonon.iplAudioBufferFree(st.ProviderContext, ref st.Stereo);
            if (handle.IsAllocated) handle.Free();
        }
        _traced.Clear(); _tracedRunning.Clear();
    }

    private void UpdateActiveReverbs(Vector3 listenerPos)
    {
        if (_acousticMap == null) return;
        
        _activeRegionIds.Clear(); var activeIds = _activeRegionIds;
        
        foreach (var kvp in _acousticMap.Regions)
        {
            int regionId = kvp.Key;
            if (regionId == _acousticMap.GlobalEnvironmentId) { activeIds.Add(regionId); continue; }
            
            if (_acousticMap.RegionPositions.TryGetValue(regionId, out var regPos))
            {
                if (Vector3.Distance(listenerPos, regPos) < AcousticConstants.ActiveRegionRadius)
                    activeIds.Add(regionId);
            }
        }
        if (_listenerRegionId != AcousticConstants.GlobalRegionId && _listenerRegionId != -1) activeIds.Add(_listenerRegionId);

        // The listener's neighbours through portals.
        foreach (var pKvp in _acousticMap.Portals.Values)
        {
            if (pKvp.Portal.RegionAId == _listenerRegionId || pKvp.Portal.RegionBId == _listenerRegionId || _listenerRegionId == _acousticMap.GlobalEnvironmentId)
            {
                activeIds.Add(pKvp.Portal.RegionAId);
                activeIds.Add(pKvp.Portal.RegionBId);
            }
        }

        lock (_lock)
        {
            foreach (var active in _activeSounds)
            {
                if (active.TargetRegionId != -2) activeIds.Add(active.TargetRegionId);
            }
        }

        foreach (var id in activeIds)
        {
            if (!_reverbBuses.ContainsKey(id) && CanAffordAnotherReverbBus(id)) CreateReverbBus(id);
        }
    }

    /// <summary>
    /// How many region reverb buses may exist at once; four are ever audible
    /// (<see cref="MaxActiveReverbBuses"/>). Unbounded, one was made for every region any source had
    /// sent to (185 on the city), each a reverb unit in the mix to be silent. Twenty-four never churns
    /// walking down a street with rooms either side.
    /// </summary>
    private const int MaxReverbBuses = 24;

    // Traced reverb, on every bus: the SFXREVERB unit passes its input through dry and only marks
    // where the traced stage (SteamAudio.TracedReverb) is inserted. No parametric room algorithm.
    // See docs/CLIENT_NOTES.md, "Traced reverb everywhere".

    private readonly Dictionary<int, (TracedReverbState State, FMOD.DSP Dsp, System.Runtime.InteropServices.GCHandle Handle)> _traced = new();
    /// <summary>Which traced stages are running (not bypassed) right now.</summary>
    private readonly Dictionary<int, bool> _tracedRunning = new();

    /// <summary>Whether there is a trace to play yet (the scene is built after the map loads).</summary>
    internal static bool TracedActive => TracedReverbSet.Listener != null;

    // Traced echoes: the loudest few sustained sources are traced from where they are (TracedEchoes),
    // because mirror images of a moving source jump from facade to facade. Each rig is a capture at
    // the channel's input end (before the HRTF) and a mix at its output end (after the fader; index 0
    // is the output end, --dsp-order), so the echoes carry their own path, not the direct one's, in
    // the same block as the voice.

    /// <summary>/echoes on | off | a trim in dB. On by default, for far sources only, at
    /// <see cref="TailDb"/>; OPENFPS_ECHOES=off starts with them off.</summary>
    public static volatile bool TracedEchoesOn = !string.Equals(Environment.GetEnvironmentVariable("OPENFPS_ECHOES"), "off", StringComparison.OrdinalIgnoreCase);
    /// <summary>Entities whose echoes are traced this frame, so the game side stops making their mirror
    /// images. Written by the audio update, read by ClientAudioSystem.</summary>
    private static volatile int[] _tracedEchoIds = Array.Empty<int>();
    public static bool HasTracedEchoes(int entityId)
    {
        var ids = _tracedEchoIds;
        foreach (int id in ids) if (id == entityId) return true;
        return false;
    }

    private readonly List<TracedEchoRig> _echoRigs = new();
    private bool _echoRigsTried;
    private readonly List<(ActiveSound A, float Score)> _echoCandidates = new();
    /// <summary>A candidate must render within this much of the loudest voice: under it, its echoes are
    /// under everything else's.</summary>
    private const float EchoWithinDb = 30f;
    /// <summary>A chosen source is held until this far behind its replacement, so two cars at the edge
    /// do not swap every frame.</summary>
    private const float EchoHoldDb = 4f;
    private const int MaxEchoTapsPerTrain = 2;
    /// <summary>
    /// Far sources only: close to, a traced response stacked a second set of strong early reflections
    /// a few milliseconds behind the direct sound, a comb (boxy, flanged). In from this distance, out
    /// again inside the hold.
    /// </summary>
    private const float EchoEnterMetres = 30f, EchoHoldMetres = 22f;
    /// <summary>The hand-over between the ordinary paths and the traced ones.</summary>
    private const float EchoFadeSeconds = 0.6f;
    private long _echoTickAt;
    /// <summary>
    /// Every traced reflection against the direct sound, dB: the rooms' and streets' traced tails and
    /// the far sources' traced echoes (`/tail`, OPENFPS_TAIL_DB; `/reflections` sets this and
    /// <see cref="CopiesDb"/>). Zero is physical to within a couple of decibels where measured
    /// (--clap-room, --traced-reverb). -6 for both, set by ear in a flat, a tunnel and a street; the
    /// trace rings as long at 4 kHz as at 250 Hz (0.79 s, Sabine 0.52), so the top hangs on. See
    /// docs/CLIENT_NOTES.md, "The reflections trim".
    /// </summary>
    public static volatile float TailDb = EnvDb("OPENFPS_TAIL_DB") ?? -6f;
    /// <summary>Every reflection placed as a copy of the source against the direct sound, dB: early,
    /// facade and higher-order echoes, your own steps' echoes, the boundary copies (`/copies`,
    /// OPENFPS_COPIES_DB). The copies carry only the mirror share, with the scattered share as the
    /// wall's wash, and at most four second-order copies. See <see cref="TailDb"/>.</summary>
    public static volatile float CopiesDb = EnvDb("OPENFPS_COPIES_DB") ?? -6f;
    public static float TailTrim => MathF.Pow(10f, TailDb / 20f);
    public static float CopiesTrim => MathF.Pow(10f, CopiesDb / 20f);

    private static float? EnvDb(string name) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out float db) ? Math.Clamp(db, -80f, 6f) : null;

    /// <summary>The cabin's traced response against its physical level, dB: 0 is the traced level.
    /// `/cabin` with a level in dB sets it, for judging a ride by ear; the reflections trim does not touch it.</summary>
    public static volatile float CabinDb = 0f;

    /// <summary>A chosen source keeps its trace at least this long, so it does not flicker passing
    /// behind something.</summary>
    private const float EchoMinHoldSeconds = 3f;
    private double _echoLogAt;

    private void MakeEchoRigs(TracedEchoes echoes)
    {
        _echoRigsTried = true;
        if (_saContext == IntPtr.Zero || _saHrtf == IntPtr.Zero) return;
        // Echoes traced for another rate would play every delay time-scaled.
        if (echoes.SampleRate != MixerQuality.MixerRate)
        {
            Log.Warning("Traced echoes: traced at {Trace} Hz, the mixer is at {Mixer} Hz; no traced echoes.", echoes.SampleRate, MixerQuality.MixerRate);
            return;
        }
        var au = new Phonon.IPLAudioSettings { samplingRate = MixerQuality.MixerRate, frameSize = _saFrameSize };
        for (int k = 0; k < TracedEchoes.MaxSources; k++)
        {
            var es = new Phonon.IPLReflectionEffectSettings
            {
                type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = echoes.IrSize, numChannels = TracedEchoes.Channels,
            };
            if (Phonon.iplReflectionEffectCreate(echoes.Context, ref au, ref es, out IntPtr effect) != Phonon.IPL_STATUS_SUCCESS) break;
            if (Phonon.iplReflectionEffectCreate(echoes.Context, ref au, ref es, out IntPtr effectB) != Phonon.IPL_STATUS_SUCCESS) break;
            var ds = new Phonon.IPLAmbisonicsDecodeEffectSettings { speakerLayout = Phonon.StereoLayout(), hrtf = _saHrtf, maxOrder = TracedEchoes.Order };
            if (Phonon.iplAmbisonicsDecodeEffectCreate(_saContext, ref au, ref ds, out IntPtr decode) != Phonon.IPL_STATUS_SUCCESS) break;
            var rig = new TracedEchoRig
            {
                FrameSize = _saFrameSize, WorkerContext = echoes.Context, ProviderContext = _saContext,
                Effect = effect, EffectB = effectB, Decode = decode, Hrtf = _saHrtf,
                Capture = new float[_saFrameSize], StereoScratch = new float[_saFrameSize * 2],
                AmbiScratchA = new float[_saFrameSize * TracedEchoes.Channels], AmbiScratchB = new float[_saFrameSize * TracedEchoes.Channels],
                Orientation = Phonon.ListenerFrame(_listenerRot), SampleRate = echoes.SampleRate,
            };
            Phonon.iplAudioBufferAllocate(echoes.Context, 1, _saFrameSize, ref rig.Mono);
            Phonon.iplAudioBufferAllocate(echoes.Context, TracedEchoes.Channels, _saFrameSize, ref rig.Ambi);
            Phonon.iplAudioBufferAllocate(echoes.Context, TracedEchoes.Channels, _saFrameSize, ref rig.AmbiB);
            Phonon.iplAudioBufferAllocate(_saContext, 2, _saFrameSize, ref rig.Stereo);
            if (TracedEchoDsp.Create(_system, rig) != RESULT.OK) break;
            _echoRigs.Add(rig);
        }
        Log.Information("Traced echoes: {Count} rigs ready (IR {Ir:F1} s, first order).", _echoRigs.Count, TracedEchoes.DurationSeconds);
    }

    private void AttachEchoRig(ActiveSound a, TracedEchoRig rig, TracedEchoes echoes)
    {
        int slot = echoes.Acquire(a.CurrentApparentPosition);
        if (slot < 0) return;
        a.Channel.getNumDSPs(out int n);
        // The capture at the input end, before the HRTF; the mix at the output end, after the fader.
        if (a.Channel.addDSP(n, rig.CaptureDsp) != RESULT.OK) { echoes.Release(slot); return; }
        if (a.Channel.addDSP(0, rig.MixDsp) != RESULT.OK)
        {
            a.Channel.removeDSP(rig.CaptureDsp);
            echoes.Release(slot);
            return;
        }
        rig.AttachGeneration[0] = System.Threading.Volatile.Read(ref echoes.BankGeneration[0]);
        rig.AttachGeneration[1] = System.Threading.Volatile.Read(ref echoes.BankGeneration[1]);
        a.EchoWeight = 0f;
        a.EchoLeaving = false;
        a.EchoSince = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        rig.NeedsReset = true;
        rig.LastInputGain = -1f;
        rig.Fresh = false;
        rig.Slot = slot;
        a.EchoRig = rig;
    }

    /// <summary>Takes a voice's rig off it (removeDSP waits out a callback in flight) and frees its
    /// slot. The rig itself is never freed.</summary>
    private void DetachEchoRig(ActiveSound a)
    {
        var rig = a.EchoRig;
        if (rig == null) return;
        a.EchoRig = null;
        int slot = rig.Slot;
        rig.Slot = -1;
        if (a.Channel.hasHandle())
        {
            a.Channel.removeDSP(rig.MixDsp);
            a.Channel.removeDSP(rig.CaptureDsp);
        }
        TracedReverbSet.Echoes?.Release(slot);
    }

    // Each source's own late sound (LateField).
    private readonly List<(ActiveSound A, float Level)> _lateCandidates = new();
    private readonly int[] _lateIds = new int[LateField.MaxSources];
    private readonly Vector3[] _lateAt = new Vector3[LateField.MaxSources];
    private readonly LateField.Answer[] _lateAnswers = new LateField.Answer[LateField.MaxSources];
    private readonly Dictionary<int, float> _lateLevel = new();
    /// <summary>The place's own law for sources nobody traced: late energy against a source at the
    /// listener goes as distance^-k, k fitted to the traced ones. NaN when there are too few.</summary>
    private float _lateLawK = float.NaN;
    private Vector3 _tailBias;
    /// <summary>An answer older than this is a place you have left.</summary>
    private const double LateAnswerSeconds = 3.0;

    /// <summary>
    /// Hands the loudest sources in the listener's place to the tracer that measures each one's late
    /// energy and direction, and turns its answers into the place's distance law (for everything it did
    /// not trace) and the way the tail leans.
    /// </summary>
    private void UpdateLateField()
    {
        var lf = TracedReverbSet.LateField;
        if (lf == null || !TracedActive || !_steamAudioEnabled) { _lateLawK = float.NaN; _tailBias = Vector3.Zero; return; }
        _lateCandidates.Clear();
        foreach (var a in _activeSounds)
        {
            if (a.IsReflection || a.FadeTarget <= 0f || a.TargetRegionId != _listenerRegionId) continue;
            if (!a.SourceReverbConnection.hasHandle()) continue;
            // Cabin paths send at the interior voice's send; given slots, they pushed the rain on the
            // roof out of them (+2 dB, the rain falling back on the stand-in law).
            if (a.TapState is { CabinPath: > 0 }) continue;
            float dist = Vector3.Distance(_listenerPos, a.Position);
            _lateCandidates.Add((a, a.BaseVolume * Loudness.RenderedGain(1f, a.MinDistance, a.Range, dist)));
        }
        _lateCandidates.Sort((x, y) => y.Level.CompareTo(x.Level));
        int n = Math.Min(LateField.MaxSources, _lateCandidates.Count);
        _lateLevel.Clear();
        for (int i = 0; i < n; i++)
        {
            _lateIds[i] = _lateCandidates[i].A.EntityId;
            _lateAt[i] = _lateCandidates[i].A.Position;
            _lateLevel[_lateIds[i]] = _lateCandidates[i].Level;
        }
        lf.Want(_listenerPos, _lateIds.AsSpan(0, n), _lateAt.AsSpan(0, n));

        // The law, through the origin in log-log (a source at a metre raises what one at the listener
        // does), fitted to what was traced lately.
        int m = lf.CopyAnswers(_lateAnswers);
        long fresh = DateTime.UtcNow.Ticks - (long)(LateAnswerSeconds * TimeSpan.TicksPerSecond);
        double sxy = 0, sxx = 0, wSum = 0;
        Vector3 lean = Vector3.Zero;
        int used = 0;
        for (int i = 0; i < m; i++)
        {
            var ans = _lateAnswers[i];
            // Not "Ratio <= 0" alone: a NaN passes that, and a NaN here is a NaN send into the bus.
            if (ans.When < fresh || !(ans.Ratio > 0f) || !float.IsFinite(ans.Ratio) || !float.IsFinite(ans.Directivity)
                || !float.IsFinite(ans.Direction.X + ans.Direction.Y + ans.Direction.Z)) continue;
            float d = Vector3.Distance(_listenerPos, ans.At);
            if (d > 1.5f) { double x = Math.Log(d), y = Math.Log(ans.Ratio); sxy += x * y; sxx += x * x; used++; }
            // The lean: each source's late direction, weighted by the late energy it raises here.
            float level = _lateLevel.TryGetValue(ans.Id, out float lv) ? lv : 0f;
            double w = (double)ans.Ratio * level * level;
            lean += (float)w * ans.Directivity * ans.Direction;
            wSum += w;
        }
        _lateLawK = used >= 2 && sxx > 1e-6 ? Math.Clamp((float)(-sxy / sxx), 0f, 4f) : float.NaN;
        var target = wSum > 1e-12 ? lean / (float)wSum : Vector3.Zero;
        _tailBias += (target - _tailBias) * MathF.Min(1f, _attributeDt / 0.5f);
        if (_traced.TryGetValue(_listenerRegionId, out var stage)) stage.State.Diffuse?.SetBias(_tailBias);
        _lateLawKNow = _lateLawK; _tailLeanNow = _tailBias.Length();
    }

    private static volatile float _lateLawKNow = float.NaN, _tailLeanNow;

    private static string LateFieldStatus()
    {
        var lf = TracedReverbSet.LateField;
        if (lf == null) return "No per-source late trace yet.";
        float k = _lateLawKNow;
        string law = float.IsNaN(k) ? "no law fitted yet" : $"the place's tail falls as distance to the -{k:F1}";
        return $"Late field: {lf.Runs} traces, last {lf.LastRunMs:F0} ms; {law}; the tail leans {_tailLeanNow:F2} toward the sources.";
    }

    /// <summary>
    /// How much of the listener's traced tail a source in the same place raises, against its direct
    /// sound as the send carries it: distance × sqrt(its late energy against a source at the listener).
    /// From its own trace, else the place's fitted law, else the stand-in (distance raised to the
    /// enclosure), which made a car 45 m down the tunnel ring nearly as loud as one at 5 m.
    /// </summary>
    private float LateSend(ActiveSound a, float d)
    {
        var lf = TracedReverbSet.LateField;
        if (lf != null && lf.TryGet(a.EntityId, out var ans)
            && ans.When >= DateTime.UtcNow.Ticks - (long)(LateAnswerSeconds * TimeSpan.TicksPerSecond)
            && float.IsFinite(ans.Ratio))
            return d * MathF.Sqrt(Math.Clamp(ans.Ratio, 1e-4f, 4f));
        if (!float.IsNaN(_lateLawK))
            return d * MathF.Pow(MathF.Max(d, 1f), -_lateLawK / 2f);
        return MathF.Min(d, 1f) * MathF.Pow(MathF.Max(d, 1f), Math.Clamp(_listenerEnclosure, 0f, 1f));
    }

    private void UpdateTracedEchoes()
    {
        var echoes = TracedReverbSet.Echoes;
        if (echoes == null || !TracedEchoesOn || !TracedActive || !_steamAudioEnabled)
        {
            foreach (var a in _activeSounds) if (a.EchoRig != null) DetachEchoRig(a);
            if (_tracedEchoIds.Length > 0) _tracedEchoIds = Array.Empty<int>();
            return;
        }
        if (!_echoRigsTried) MakeEchoRigs(echoes);
        if (_echoRigs.Count == 0) return;
        echoes.SetListener(_listenerPos);

        // Who: sustained sources with a binaural voice of their own, by the level they render at.
        _echoCandidates.Clear();
        float loudest = 0f;
        foreach (var a in _activeSounds)
        {
            if (!a.SaDsp.hasHandle() || a.IsReflection || a.EchoState != null || a.TapState != null) continue;
            if (a.EngineState == null && a.MachineState == null) continue;
            if (a.FadeTarget <= 0f || a.LastVolume <= 0f) continue;
            float dist = Vector3.Distance(_listenerPos, a.CurrentApparentPosition);
            if (dist < (a.EchoRig != null && !a.EchoLeaving ? EchoHoldMetres : EchoEnterMetres)) continue;
            // Ranked by what it radiates here, not what gets through: occlusion and the cone swing as a
            // siren passes behind a building, and ranking on them flickered the trace ("reflect, cut
            // out, cut in, cut out").
            float level = a.BaseVolume * Loudness.RenderedGain(1.0f, a.MinDistance, a.Range, dist);
            bool held = a.EchoRig != null && !a.EchoLeaving;
            float score = level * (held ? MathF.Pow(10f, EchoHoldDb / 20f) : 1f);
            loudest = MathF.Max(loudest, level);
            _echoCandidates.Add((a, score));
        }
        _echoCandidates.Sort((x, y) => y.Score.CompareTo(x.Score));
        float floor = loudest * MathF.Pow(10f, -EchoWithinDb / 20f);
        int allowed = _echoRigs.Count;
        var keep = new HashSet<ActiveSound>();
        // A train is a line of sources on one synth; its two loudest carry its echoes, so one train
        // cannot take every rig from the cars and the siren.
        var perTrain = new Dictionary<string, int>();
        foreach (var (a, score) in _echoCandidates)
        {
            if (keep.Count >= allowed || score < floor) break;
            // ...and so is a tree, a fire or a fountain heard from several places.
            string? sharedKey = a.MachineState switch
            {
                TrainSlotState trainVoice => trainVoice.Shared.Key,
                NaturePlaceState place => "place:" + place.Shared.GetHashCode(),
                WaterTapState water => "water:" + water.Shared.Key,
                _ => null,
            };
            if (sharedKey != null)
            {
                perTrain.TryGetValue(sharedKey, out int c);
                if (c >= MaxEchoTapsPerTrain) continue;
                perTrain[sharedKey] = c + 1;
            }
            keep.Add(a);
        }
        double nowS = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        foreach (var a in _activeSounds)
        {
            if (a.EchoRig == null) continue;
            bool wanted = keep.Contains(a);
            // An unwanted source keeps its trace out the minimum hold, unless it has come close, where
            // the trace does harm.
            bool close = Vector3.Distance(_listenerPos, a.CurrentApparentPosition) < EchoHoldMetres;
            if (!wanted && !close && nowS - a.EchoSince < EchoMinHoldSeconds) { keep.Add(a); wanted = true; }
            a.EchoLeaving = !wanted;
        }
        foreach (var a in keep)
        {
            if (a.EchoRig != null) continue;
            TracedEchoRig? free = null;
            foreach (var r in _echoRigs) if (r.Slot < 0) { free = r; break; }
            if (free == null) break;
            AttachEchoRig(a, free, echoes);
        }

        // The same loudness law as the direct sound, so the echoes stand to it as they would in the air.
        long tick = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _echoTickAt == 0 ? 0f : Math.Clamp((tick - _echoTickAt) / (float)System.Diagnostics.Stopwatch.Frequency, 0f, 0.25f);
        _echoTickAt = tick;
        var orient = Phonon.ListenerFrame(_listenerRot);
        var ids = new List<int>();
        List<ActiveSound>? gone = null;
        foreach (var a in _activeSounds)
        {
            var rig = a.EchoRig;
            if (rig == null) continue;
            float step = dt / EchoFadeSeconds;
            a.EchoWeight = Math.Clamp(a.EchoWeight + (a.EchoLeaving ? -step : step), 0f, 1f);
            if (a.EchoLeaving && a.EchoWeight <= 0f) { (gone ??= new()).Add(a); continue; }
            float d = MathF.Max(1f, Vector3.Distance(_listenerPos, a.CurrentApparentPosition));
            float law = Loudness.RenderedGain(1.0f, a.MinDistance, a.Range, d) * d;   // the law over 1/d
            rig.InputGain = a.BaseVolume * a.FadeGain * law * a.EchoWeight * TailTrim;
            rig.Orientation = orient;
            echoes.SetSource(rig.Slot, a.CurrentApparentPosition);
            // The mirror images give way once the trace carries most of it, and come back first.
            if (a.EchoWeight >= 0.5f && !a.EchoLeaving) ids.Add(a.EntityId);
        }
        if (gone != null) foreach (var a in gone) DetachEchoRig(a);
        _tracedEchoIds = ids.ToArray();

        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now >= _echoLogAt)
        {
            _echoLogAt = now + 5.0;
            var parts = new List<string>();
            foreach (var a in _activeSounds)
                if (a.EchoRig is { } r)
                    parts.Add($"{a.SoundId}#{a.EntityId} {20 * Math.Log10(Math.Max(1e-9, a.LastVolume)):F0} dB in {20 * Math.Log10(Math.Max(1e-9, r.InRms)):F0} out {20 * Math.Log10(Math.Max(1e-9, r.OutRms)):F0}");
            if (parts.Count > 0)
                Log.Information("Traced echoes: {N} source(s), trace {Ms:F0} ms: {List}", parts.Count, echoes.LastRunMs, string.Join("; ", parts));
        }
    }

    public static string TracedEchoesStatus()
    {
        var e = TracedReverbSet.Echoes;
        string mode = TracedEchoesOn ? "on" : "off";
        if (e == null) return $"Echoes {mode}. No tracer yet — the scene is still being built.";
        return $"Echoes {mode}, {TailDb:F0} dB against physical (the tail level). {_tracedEchoIds.Length} far source(s) traced from where they are; {e.Runs} traces, the last in {e.LastRunMs:F0} ms.";
    }

    /// <summary>The path the loader opens for a sound id; empty if it would leave ASSETS. Ids come from
    /// the server and a map's owner sets them: a network share would hand Windows' login hash to whoever
    /// runs it.</summary>
    internal static string SoundFilePath(string soundId)
    {
        if (string.IsNullOrEmpty(soundId)) return "";
        string baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        if (soundId.Contains("ASSETS", StringComparison.OrdinalIgnoreCase))
            return Inside(soundId, baseDir, Path.IsPathFullyQualified(soundId) ? soundId : Path.Combine(baseDir, soundId));
        string normId = soundId.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string sounds = Path.Combine(baseDir, "ASSETS", "SOUNDS");
        return Inside(soundId, sounds, Path.Combine(sounds, normId));
    }

    /// <summary>The path, if it is inside the folder and names no network share; empty if not.</summary>
    private static string Inside(string id, string folder, string path)
    {
        if (id.StartsWith(@"\\", StringComparison.Ordinal) || id.StartsWith("//", StringComparison.Ordinal)) return "";
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { return ""; }
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : "";
    }

    /// <summary>A recorded take's correction to its bank's median level (TakeLevels); 1 for anything
    /// synthesized, and for a file that is not one of a bank.</summary>
    private static float TakeGain(in SpatialEmitter emitter)
    {
        if (emitter.IsSynth || emitter.IsGranular || string.IsNullOrEmpty(emitter.SoundId)) return 1f;
        string path = SoundFilePath(emitter.SoundId);
        if (path.Length == 0) return 1f;
        if (!File.Exists(path))
            foreach (var ext in new[] { ".wav", ".ogg", ".mp3" })
                if (File.Exists(path + ext)) { path += ext; break; }
        return TakeLevels.GainFor(path);
    }

    /// <summary>For the /reverb readout: how the tracing is doing and the two trims.</summary>
    public static string TracedReverbStatus()
    {
        if (TracedReverbSet.Listener == null) return "No trace yet — the scene is still being built.";
        var (rooms, runs, ms) = TracedReverbSet.Stats();
        return $"Traced from where you stand and from {rooms} other room(s); {runs} traces so far, the last of yours in {ms:F0} ms. Tail {TailDb:F0} dB, copies {CopiesDb:F0} dB. {LateFieldStatus()}";
    }

    // The ear, overloaded. The loudness law puts everything above about 112 dB at the ear at full scale,
    // so a shot beside you was "just a click", level with one at 30 m. The excess is taken out of
    // everything else instead, as the ear's protective reflex does, by the law that compresses the rest,
    // and comes back over a few hundred milliseconds, longer the harder it was hit; the shot, its echoes
    // and its room are left alone. About 20 dB for a pistol at a metre, 6 at 40 m, none from a jackhammer.

    /// <summary>Most the world gives way by, dB.</summary>
    private const float OverloadMaxDb = 24f;
    private float _overloadDb, _overloadGain = 1f;
    private double _overloadHoldUntil, _overloadTau = 0.3;
    private readonly List<(double At, float Db)> _overloadPending = new();

    /// <summary>How far over the output's ceiling a one-off arrives, in rendered dB (0 if it does not).</summary>
    private float OverloadDb(SpatialEmitter e)
    {
        if (!e.IsEvent || e.IsReflection || e.LevelDb <= 0f || e.Type == EmitterType.UI) return 0f;
        float d = e.EffectiveDistance > 0f ? e.EffectiveDistance : Vector3.Distance(_listenerPos, e.Position);
        float path = (e.EqMid > 0f ? e.EqMid : 1f) * (1f - Math.Clamp(e.Occlusion, 0f, 0.99f));
        return MathF.Min(OverloadMaxDb, Loudness.OverloadDb(e.LevelDb, d, path, e.AirMidDb));
    }

    /// <summary>Starts (or deepens) the world's giving way, <paramref name="afterSeconds"/> from now:
    /// when the sound arrives, not when it was sent.</summary>
    private void Overload(float db, double afterSeconds)
    {
        lock (_overloadPending) _overloadPending.Add((OpenFPS.Common.AudioClock.Now + Math.Max(0.0, afterSeconds), db));
    }

    /// <summary>One step of the overload: arrivals, the hold, the recovery. Audio update.</summary>
    private void StepOverload(double now, float dt)
    {
        lock (_overloadPending)
            for (int i = _overloadPending.Count - 1; i >= 0; i--)
            {
                var (at, db) = _overloadPending[i];
                if (at > now) continue;
                _overloadPending.RemoveAt(i);
                if (db <= _overloadDb) continue;
                _overloadDb = db;
                // Held while the sound is in the ear; back in about a quarter of a second after a
                // distant shot, most of a second after one beside you.
                _overloadHoldUntil = now + 0.05;
                _overloadTau = 0.15 + 0.03 * db;
            }
        if (_overloadDb > 0f && now > _overloadHoldUntil)
            _overloadDb *= (float)Math.Exp(-dt / _overloadTau);
        if (_overloadDb < 0.05f) _overloadDb = 0f;
        _overloadGain = MathF.Pow(10f, -_overloadDb / 20f);
    }

    /// <summary>Adds a traced stage to every bus that lacks one, points each at the place it is heard
    /// as, and runs only the audible ones. Audio update thread.</summary>
    private void UpdateTracedStages()
    {
        var listenerTrace = TracedReverbSet.Listener;
        if (listenerTrace == null || !_steamAudioEnabled || _saContext == IntPtr.Zero) return;
        foreach (var kv in _reverbDsps)
        {
            if (_traced.ContainsKey(kv.Key)) continue;
            if (!_reverbBuses.TryGetValue(kv.Key, out var bus)) continue;
            AddTracedStage(kv.Key, bus, kv.Value, listenerTrace);
        }

        // The room you are in: yours (the cabin's, riding in one). Any other room: its own, from its
        // middle. Open ground elsewhere: yours too, the open air having no middle to trace.
        var cabin = TracedReverbSet.Cabin;
        foreach (var kv in _traced)
        {
            TracedReverb? trace = kv.Key == _listenerRegionId && cabin != null ? cabin : listenerTrace;
            if (kv.Key != _listenerRegionId && IsEnclosure(kv.Key) && _acousticMap != null
                && _acousticMap.RegionPositions.TryGetValue(kv.Key, out var centre)
                && _reverbVolumes.TryGetValue(kv.Key, out float vol) && vol > 0.001f)
                trace = TracedReverbSet.ForRoom(kv.Key, centre) ?? listenerTrace;
            // Open ground heard from indoors: traced from just outside the opening it comes in by.
            // Traced at the listener it was the street with the lobby's echoes in it.
            else if (kv.Key != _listenerRegionId && !IsEnclosure(kv.Key) && IsEnclosure(_listenerRegionId)
                     && _fieldHere.TryGetValue(kv.Key, out var field) && field.Region == _listenerRegionId && field.Gain > 0f
                     && _reverbVolumes.TryGetValue(kv.Key, out float openVol) && openVol > 0.001f)
                trace = TracedReverbSet.ForRoom(kv.Key, field.InField) ?? listenerTrace;
            kv.Value.State.Trace = trace;
            // Your own place plays the late tail alone, its early part being placed copies
            // (WorldAudioPlayer.QueueEarlyEchoes). A cabin keeps its whole response: nothing is placed
            // inside a vehicle.
            bool tailOnly = kv.Key == _listenerRegionId && !ReferenceEquals(trace, cabin);
            // Made here, not on the mixer thread, and only for your own room's stage: it holds half a
            // second per direction.
            if (tailOnly && kv.Value.State.DiffuseLateConv == null && trace != null && kv.Value.State.SubFrame > 0
                && DiffuseLateNoise.Block % kv.Value.State.SubFrame == 0)
                kv.Value.State.DiffuseLateConv = trace.NewDiffuseLateConvolver(kv.Value.State.SubFrame);
            kv.Value.State.TailOnly = tailOnly;
            // A cabin's response is the whole of the room you sit in, not reflections beside a direct
            // sound, so it plays at its traced level: trimmed, a bus ride was muffled, the doors and
            // the street gone and no way to tell the bus was stopping.
            kv.Value.State.Gain = ReferenceEquals(trace, cabin) ? MathF.Pow(10f, CabinDb / 20f) : TailTrim;
        }

        // Only where it can be heard: a stage costs about half a millisecond a block whatever its bus's
        // volume, and twenty-four running took the mixer from 40 % to 77 % ("bit crushed" cars, a train
        // cutting in and out as the budget shed voices). A silent bus's stage is bypassed.
        foreach (var kv in _traced)
        {
            bool audible = _reverbVolumes.TryGetValue(kv.Key, out float v) && v > 0.001f;
            // A stage about to be heard needs its own copy of its trace.
            if (audible) kv.Value.State.Trace?.EnsureReader(kv.Value.State.Reader);
            if (_tracedRunning.TryGetValue(kv.Key, out bool was) && was == audible) continue;
            kv.Value.Dsp.setBypass(!audible);
            _tracedRunning[kv.Key] = audible;
        }
    }

    private void AddTracedStage(int regionId, FMOD.ChannelGroup bus, FMOD.DSP sfx, TracedReverb tr)
    {
        // In pieces of TracedReverb.TracedFrame: the convolution answers a block late, and that is its
        // own block, not the mixer's.
        int sub = tr.FrameSize;
        if (_saFrameSize % sub != 0) { Log.Warning("Traced reverb: mixer block {Block} is not a multiple of {Sub}.", _saFrameSize, sub); return; }
        // A trace at another rate would play every delay time-scaled (8 % between 44.1 and 48 kHz).
        // Only a trace made before the mixer can be; say so rather than play it wrong.
        if (tr.SampleRate != MixerQuality.MixerRate)
        {
            Log.Warning("Traced reverb: the trace runs at {Trace} Hz and the mixer at {Mixer} Hz; region {Id} plays without it.",
                        tr.SampleRate, MixerQuality.MixerRate, regionId);
            return;
        }
        var au = new Phonon.IPLAudioSettings { samplingRate = tr.SampleRate, frameSize = sub };
        var es = new Phonon.IPLReflectionEffectSettings
        {
            type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = tr.IrSize, numChannels = TracedReverb.Channels,
        };
        if (Phonon.iplReflectionEffectCreate(tr.Context, ref au, ref es, out IntPtr effect) != Phonon.IPL_STATUS_SUCCESS)
        { Log.Warning("Traced reverb: Steam Audio would not make a reflection effect for region {Id}.", regionId); return; }
        if (_saHrtfTraced == IntPtr.Zero || _saHrtfTracedFrame != sub)
        {
            if (_saHrtfTraced != IntPtr.Zero) Phonon.iplHRTFRelease(ref _saHrtfTraced);
            var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
            if (Phonon.iplHRTFCreate(_saContext, ref au, ref hs, out _saHrtfTraced) != Phonon.IPL_STATUS_SUCCESS)
            { _saHrtfTraced = IntPtr.Zero; Phonon.iplReflectionEffectRelease(ref effect); return; }
            _saHrtfTracedFrame = sub;
        }
        // The voices' binaural stage (a frame four times as long) delays a sound more than this one, so
        // its input waits the difference and the room answers after the sound, not 4 ms before it
        // (TracedReverbDsp.StagePreDelay). Measured once.
        if (_tracedPreDelay < 0)
        {
            int voice = TracedReverbDsp.BinauralOnset(_saContext, _saHrtf, _saFrameSize);
            int stage = TracedReverbDsp.BinauralOnset(_saContext, _saHrtfTraced, sub);
            _tracedPreDelay = TracedReverbDsp.StagePreDelay(voice, stage);
            Log.Information("Traced reverb: voices render {Voice} samples late, the traced stage {Stage}; its input waits {Wait}.",
                            voice, stage, _tracedPreDelay);
        }
        var ds = new Phonon.IPLAmbisonicsDecodeEffectSettings { speakerLayout = Phonon.StereoLayout(), hrtf = _saHrtfTraced, maxOrder = TracedReverb.Order };
        if (Phonon.iplAmbisonicsDecodeEffectCreate(_saContext, ref au, ref ds, out IntPtr decode) != Phonon.IPL_STATUS_SUCCESS)
        { Phonon.iplReflectionEffectRelease(ref effect); return; }
        var st = new TracedReverbState
        {
            FrameSize = _saFrameSize, SubFrame = sub, SampleRate = tr.SampleRate, WorkerContext = tr.Context, ProviderContext = _saContext,
            Effect = effect, Decode = decode, Hrtf = _saHrtfTraced, Trace = tr,
            MonoScratch = new float[sub], StereoScratch = new float[sub * 2],
            AmbiScratch = new float[sub * TracedReverb.Channels],
            Orientation = Phonon.ListenerFrame(_listenerRot),
            // Its own reader in every trace it plays (TracedReverb.MaxReaders); never the last, which
            // is the tracer's own for reading the late tail back.
            Reader = Math.Min(_traced.Count, TracedReverb.ExtractReader - 1),
            LateConv = new LateTailConvolver(sub, tr.MaxLatePartitions),
            SdmConv = new SharedInputConvolver(sub, SdmTailIr.PartitionsFor(tr.SampleRate, sub), DiffuseBranch.Count),
            LateOut = new float[sub],
            // The room you are in: its late tail as a field round the head, not one channel.
            Diffuse = DiffuseTail.Create(_saContext, sub, TracedReverb.Channels, _saHrtfTraced, tr.SampleRate),
            Delay = _tracedPreDelay > 0 ? new PreDelay(_tracedPreDelay) : null,
            Region = regionId,
        };
        Phonon.iplAudioBufferAllocate(tr.Context, 1, sub, ref st.Mono);
        Phonon.iplAudioBufferAllocate(tr.Context, TracedReverb.Channels, sub, ref st.Ambi);
        Phonon.iplAudioBufferAllocate(_saContext, 2, sub, ref st.Stereo);
        if (TracedReverbDsp.Create(_system, st, out var dsp, out var handle) != RESULT.OK) return;
        // Just downstream of the SFXREVERB: index 0 is the output end, so inserting at the reverb's
        // index puts this after it.
        int at = -1;
        bus.getNumDSPs(out int count);
        for (int i = 0; i < count; i++)
            if (bus.getDSP(i, out var d) == RESULT.OK && d.handle == sfx.handle) { at = i; break; }
        if (at < 0 || bus.addDSP(at, dsp) != RESULT.OK) { dsp.release(); handle.Free(); return; }
        dsp.setBypass(true);                 // UpdateTracedStages runs it once its bus is heard
        _traced[regionId] = (st, dsp, handle);
    }

    /// <summary>
    /// A cap, not a recycler: nothing is torn down while the mixer runs. Releasing silent buses would
    /// destroy FMOD objects mid-mix (see ReleaseSteamAudioVoice) and drain the HRTF pool (everything
    /// mono). Past the cap far rooms are not built and stay dry; walking back builds them, being near.
    /// </summary>
    private bool CanAffordAnotherReverbBus(int regionId)
    {
        if (_reverbBuses.Count < MaxReverbBuses) return true;
        if (regionId == _listenerRegionId) return true;         // the room you are IN, always
        if (_acousticMap != null && regionId == _acousticMap.GlobalEnvironmentId) return true;
        return false;
    }

    /// <summary>Whether a region is a closed boundary, from its own faces (RoomAcoustics): not whether
    /// it is the global region id, which only says whether the map named the place. Naming the infield
    /// does not put a roof over it.</summary>
    private bool IsEnclosure(int regionId)
        => _acousticMap != null && _acousticMap.Regions.TryGetValue(regionId, out var r)
           && RoomAcoustics.IsEnclosure(r);

    private void CreateReverbBus(int regionId)
    {
        if (_acousticMap == null) return;
        if (!_acousticMap.Regions.TryGetValue(regionId, out var region)) return;

        // Checked: FMOD's pools are finite, and using a handle it refused is a null dereference in the
        // native library, no exception and no log line ("it crashes when I walk around").
        string busName = (region.FriendlyName ?? "Unknown") + "_Reverb";
        if (_system.createChannelGroup(busName, out var bus) != RESULT.OK || !bus.hasHandle())
        {
            Log.Warning("Reverb: FMOD would not make a bus for region {Id} ({Name}) — {Count} already exist. "
                      + "That room will be dry.", regionId, region.FriendlyName, _reverbBuses.Count);
            return;
        }
        // 3D, so UpdateReverbBuses can place a room's reverb at its doorway for a listener outside
        // (without a 3D mode those calls do nothing). The level is gated by hand per portal, so FMOD's
        // own rolloff is kept out of the way with a huge max distance.
        bus.setMode(MODE._3D | Rolloff.Mode);
        bus.set3DMinMaxDistance(2.0f, 10000.0f);
        if (_system.createDSPByType(DSP_TYPE.SFXREVERB, out var reverbDsp) != RESULT.OK || !reverbDsp.hasHandle())
        {
            Log.Warning("Reverb: FMOD would not make a reverb unit for region {Id} ({Name}) — {Count} already exist.",
                        regionId, region.FriendlyName, _reverbDsps.Count);
            bus.release();
            return;
        }
        
        // No tail of its own, input passed through dry: the traced stage inserted at its index
        // (AddTracedStage) is the room's answer, and the only thing that leaves the bus.
        reverbDsp.setParameterFloat(11, -80.0f);
        reverbDsp.setParameterFloat(12, 0.0f);

        // At the tail (the input end), so the fader and the binaural stage at the head are downstream.
        // At the head the sends injected past both: a room's reverb at full level, from all
        // directions, from anywhere on the map.
        bus.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, reverbDsp);
        
        float initialVol = (regionId == _listenerRegionId) ? 1.0f : 0.0f;
        bus.setVolume(initialVol);
        _reverbBuses[regionId] = bus;
        _reverbDsps[regionId] = reverbDsp;
        _reverbVolumes[regionId] = initialVol;
        _system.getMasterChannelGroup(out var master);
        master.addGroup(bus);

        // An HRTF voice at the bus's head (its output end, so it places the finished tail): for a
        // listener outside, the room's reverberation comes from its doorway; inside, it passes the
        // stereo through. The bus goes 2D so FMOD does not collapse the binaural pair. Held for the
        // bus's life.
        if (_steamAudioEnabled && TryCreateSteamAudioVoice(out var rvState, out var rvDsp, out var rvHandle, SaDirectReserve))
        {
            rvState!.GuardName = "(a reverb bus's head)";
            bus.getMode(out MODE bm);
            bus.setMode((bm & ~(MODE._3D | Rolloff.Either)) | MODE._2D);
            bus.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, rvDsp);
            // Never bypassed: the stage crossfades between the reverb's own stereo and its placement
            // (SpatialBlend), so there is no switch to click. See SetReverbDirection.
            rvState!.SpatialBlend = 0f;
            _reverbSaVoices[regionId] = new SaVoice { State = rvState!, Dsp = rvDsp, Handle = rvHandle };
        }
    }

    // Trains: one model per consist, heard through a handful of voices (RailVoice.cs). The client asks for
    // voice k of a train as "rail:<preset>/<train>/@k"; the model for <preset>/<train> is made on the
    // first voice or the first plan.
    private readonly Dictionary<string, TrainVoiceState> _trains = new();

    /// <summary>The last signal each train was given, and when, for a synth made after it arrived.</summary>
    private readonly Dictionary<string, (float[] Warning, float Bell, double At)> _trainSignals = new();

    /// <summary>A train sounds its horn (or whistle) and bell: see TrainVoiceState.Signal.</summary>
    public void SignalTrain(string train, float[] warning, float bellSeconds, double secondsAgo)
    {
        lock (_trains)
        {
            _trainSignals[train] = (warning, bellSeconds, OpenFPS.Common.AudioClock.Now - secondsAgo);
            if (_trains.TryGetValue(train, out var t)) t.Signal(warning, bellSeconds, secondsAgo);
        }
        Log.Information("Train '{Train}' sounds: horn {Warning} s, bell {Bell:F1} s", train,
                        string.Join(",", warning.Select(w => w.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))), bellSeconds);
    }

    /// <summary>The model of a train, made on first use. Caller holds the lock on _trains.</summary>
    private TrainVoiceState TrainFor(string preset, string train, int rate)
    {
        string shared = preset + "/" + train;
        if (_trains.TryGetValue(shared, out var t)) return t;
        // The seed from the name's characters, not string.GetHashCode (randomised per process): every
        // client and every run hears the same train.
        int seed = 17;
        foreach (char c in train) seed = unchecked(seed * 31 + c);
        t = new TrainVoiceState(shared, OpenFPS.Common.TrainProfile.ByName(preset), rate, seed & 0x7fff);
        _trains[shared] = t;
        Log.Information("Train '{Train}' ({Profile}): one model, {Sources} source(s) over {Length:F0} m, heard through at most {Voices} voice(s); {Lanes} lane(s) render its engines and signals",
                        shared, t.Profile.Name, t.Layout.Count, t.Profile.LengthMetres,
                        OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.Slots, t.Lanes.Length);
        // Sounding already: the horn came in before any of the train had a voice.
        if (_trainSignals.TryGetValue(shared, out var sig))
        {
            double ago = OpenFPS.Common.AudioClock.Now - sig.At;
            if (ago < OpenFPS.Common.TrainSignal.Duration(sig.Warning, sig.Bell)) t.Signal(sig.Warning, sig.Bell, ago);
        }
        return t;
    }

    /// <summary>See IAudioProvider.PlanTrainSlot.</summary>
    public void PlanTrainSlot(string preset, string train, int slot, TrainSlotPlan plan)
    {
        if (!_isInitialized) return;
        _system.getSoftwareFormat(out int rate, out _, out _);
        lock (_trains)
        {
            try { TrainFor(preset, train, rate).SetPlan(slot, plan); }
            catch (Exception ex) { Log.Warning("Train '{Preset}/{Train}': {Message}", preset, train, ex.Message); }
        }
    }

    private PhysicalVoiceState? RailSlot(string key, int rate)
    {
        if (!TrainVoiceState.ParseSlotKey(key, out string preset, out string train, out int slot)) return null;
        lock (_trains)
        {
            var t = TrainFor(preset, train, rate);
            // Declared at the train's loudest field source; a signal's voice at the signal's own level.
            float level = OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.SlotLevelDb(t.Layout);
            int signal = slot == OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.WarningSlot ? OpenFPS.Common.TrainSignal.WarningSource(t.Layout)
                       : slot == OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.BellSlot ? OpenFPS.Common.TrainSignal.BellSource(t.Layout) : -1;
            if (signal >= 0) level = t.Layout[signal].LevelDb;
            return new TrainSlotState(t, slot, level, rate);
        }
    }

    /// <summary>
    /// A tree's or a fire's voice: its middle (the map's emitter) makes the one synth, with as many
    /// places as the source has (ExtendedSources.Layout); an outer place (SpatialEmitter.PlaceOfEntity)
    /// reads the synth of its source's live voice, or is not made.
    /// </summary>
    private PhysicalVoiceState? NaturePlace(string kind, string preset, in SpatialEmitter emitter, int rate)
    {
        if (emitter.PlaceOfEntity != 0)
        {
            PlacedNatureVoice? shared;
            lock (_lock) { shared = (FindActive(emitter.PlaceOfEntity)?.MachineState as NaturePlaceState)?.Shared; }
            if (shared == null || emitter.Place <= 0 || emitter.Place >= shared.Places) return null;
            return new NaturePlaceState(shared, emitter.Place, rate, emitter.Position);
        }
        int places = ExtendedSources.Layout(emitter.PhysicalKey)?.Length ?? 1;
        PlacedNatureVoice voice = kind == "shore"
            ? ShoreVoice(emitter, rate)
            : kind == "wood" && OpenFPS.Common.WoodChorus.ParseKey(emitter.PhysicalKey, out string woodPreset, out _, out _)
            ? new PlacedNatureVoice(emitter.PhysicalKey, OpenFPS.Common.FoliageSpec.ByName(woodPreset), places, rate,
                                    emitter.EntityId * 43 + 17, emitter.Position)
              { WindPlaces = ExtendedSources.Layout(emitter.PhysicalKey)?[1..], TargetTrees = emitter.Trees }
            : kind == "flow"
            ? new PlacedNatureVoice(emitter.PhysicalKey, OpenFPS.Common.RunningWaterSpec.ByName(preset), rate,
                                    emitter.EntityId * 47 + 19, emitter.Position)
            : kind == "fire"
            ? new PlacedNatureVoice(emitter.PhysicalKey, OpenFPS.Common.FireSpec.ByName(preset), places, rate,
                                    emitter.EntityId * 41 + 13, emitter.Position)
            : new PlacedNatureVoice(emitter.PhysicalKey, OpenFPS.Common.FoliageSpec.ByName(preset), places, rate,
                                    emitter.EntityId * 43 + 17, emitter.Position);
        voice.TargetSpread = emitter.Spread;
        return new NaturePlaceState(voice, 0, rate, emitter.Position);
    }

    /// <summary>Waves at an edge: its preset and its own geometry from the key (ShoreSpec.KeyFor).</summary>
    private static PlacedNatureVoice ShoreVoice(in SpatialEmitter emitter, int rate)
    {
        OpenFPS.Common.ShoreSpec.ParseKey(emitter.PhysicalKey, out string shorePreset, out var geometry);
        var spec = OpenFPS.Common.ShoreSpec.ByName(shorePreset);
        return new PlacedNatureVoice(emitter.PhysicalKey, spec, geometry ?? spec.DefaultGeometry, rate,
                                     emitter.EntityId * 53 + 23, emitter.Position);
    }

    /// <summary>How many voices a rain slot is played as (RainFeeds.PartsFor).</summary>
    private static int RainParts(int slot) => RainFeeds.PartsFor(slot);

    // A map names each tap of a water feature "water:<preset>/<feature>/<tap>"; the one synth for
    // <preset>/<feature> is made on the first tap, at the mixer's rate. See NatureVoices.cs.
    private readonly Dictionary<string, WaterFeatureVoice> _waterFeatures = new();

    private PhysicalVoiceState? Water(string key, int rate, int entityId, System.Numerics.Vector3 position, in SpatialEmitter emitter)
    {
        if (!WaterFeatureVoice.ParseKey(key, out string preset, out string feature, out int tap))
            return new WaterVoiceState(OpenFPS.Common.WaterFeatureSpec.ByName(key[(key.IndexOf(':') + 1)..]),
                                       rate, entityId * 37 + 11, position);
        string shared = preset + "/" + feature;
        if (emitter.PlaceOfEntity != 0)
        {
            // One of the places round a tap: it reads its tap's middle's synth.
            WaterTapState? middle;
            lock (_lock) { middle = FindActive(emitter.PlaceOfEntity)?.MachineState as WaterTapState; }
            if (middle == null || emitter.Place <= 0 || emitter.Place >= middle.Shared.PlacesPerTap) return null;
            return new WaterTapState(middle.Shared, middle.Tap, rate, position, emitter.Place);
        }
        lock (_waterFeatures)
        {
            if (!_waterFeatures.TryGetValue(shared, out var w))
            {
                w = new WaterFeatureVoice(shared, OpenFPS.Common.WaterFeatureSpec.ByName(preset), rate,
                                          (int)((uint)shared.GetHashCode() & 0x7fff),
                                          ExtendedSources.Layout(emitter.PhysicalKey)?.Length ?? 1);
                _waterFeatures[shared] = w;
                Log.Information("Water feature '{Feature}' ({Spec}): one synth, {Taps} tap(s)", shared, w.Spec.Name, w.Water.TapCount);
            }
            if (tap >= w.Water.TapCount) return null;
            w.SetSpread(tap, emitter.Spread);
            return new WaterTapState(w, tap, rate, position);
        }
    }

    /// <summary>The DSP a reverb send must feed: the bus's SFXREVERB unit at its input end, by identity.
    /// <c>getDSP(HEAD)</c> is the binaural output stage, and a send into it bypassed the bus fader and
    /// the reverb.</summary>
    private bool TryGetReverbInput(int regionId, out FMOD.DSP dsp)
        => _reverbDsps.TryGetValue(regionId, out dsp) && dsp.hasHandle();

    /// <summary>
    /// Removes a send outright rather than muting it: a muted one is re-created each time the region
    /// flips back, and FMOD caps inputs per DSP. The unit asked to disconnect must be the unit the
    /// connection feeds: FMOD checks only the source, then unlinks from the owner and decrements the
    /// count on the unit it was told, and the owner later crashed the mixer thread at 0x7c (the city
    /// crash; docs/THE_MIXER_THREAD_CRASH.md, AudioLab --foreign-disconnect). The connection is asked
    /// for its owner and any disagreement is counted for the health line.
    /// </summary>
    private static void DropSend(ref FMOD.DSPConnection conn, FMOD.DSP target, FMOD.DSP source)
    {
        if (!conn.hasHandle()) return;
        if (target.hasHandle() && source.hasHandle())
        {
            if (conn.getOutput(out var owner) == RESULT.OK && owner.hasHandle() && owner.handle != target.handle)
            {
                System.Threading.Interlocked.Increment(ref _sendDropsOnWrongBus);
                target = owner;
            }
            target.disconnectFrom(source, conn);
        }
        conn = default;
    }

    /// <summary>Sends about to be disconnected through a unit that did not own them. Must stay 0: anything
    /// else is the city crash waiting to happen (see DropSend).</summary>
    private static int _sendDropsOnWrongBus;

    /// <summary>Places a room's reverb at its doorway (HRTF) when the listener is outside, or passes its
    /// stereo through when inside. Without Steam Audio for the bus, FMOD's 3D positioning.</summary>
    private void SetReverbDirection(int regionId, FMOD.ChannelGroup bus, Vector3 doorwayPos, bool outside, Vector3 lPos)
    {
        if (_reverbSaVoices.TryGetValue(regionId, out var v) && v.Dsp.hasHandle())
        {
            // Another room's field arrives through its opening (blend 1); your own room's passes its
            // stereo through (blend 0). Only direction and weight change, both smoothed: toggling a
            // bypass at a region's edge re-engaged a stale tail, a whoosh like a door on every crossing
            // between two halves of a hall.
            Vector3 worldDir; float targetBlend;
            if (outside)
            {
                worldDir = doorwayPos - lPos;
                targetBlend = 1f;
            }
            else
            {
                // Your own room is already round the head (placed reflections, DiffuseTail); placed at
                // one point as well it was a second room, the reverb "consolidating" in one direction.
                worldDir = Vector3.Zero;
                targetBlend = 0f;
            }
            float len = worldDir.Length();
            if (len > 1e-4f)
            {
                Vector3 local = Vector3.Transform(worldDir / len, Quaternion.Conjugate(_listenerRot));
                var target = new Vector3(local.X, local.Y, -local.Z);
                var next = Vector3.Lerp(new Vector3(v.State.DirX, v.State.DirY, v.State.DirZ),
                                        target, AcousticConstants.ReverbDirectionSmoothing);
                if (next.LengthSquared() > 1e-6f) next = Vector3.Normalize(next);
                v.State.DirX = next.X; v.State.DirY = next.Y; v.State.DirZ = next.Z;
            }
            else targetBlend = 0f;   // nothing came back from anywhere in particular

            v.State.SpatialBlend = MathHelper.Lerp(v.State.SpatialBlend, targetBlend, AcousticConstants.ReverbBlendSpeed);
        }
        else if (outside)
        {
            bus.set3DLevel(1.0f); bus.set3DSpread(0.0f);
            FMOD.VECTOR fp = FmodHelpers.ToFmodVec(doorwayPos); FMOD.VECTOR fv = new FMOD.VECTOR();
            bus.set3DAttributes(ref fp, ref fv);
        }
        else bus.set3DLevel(0.0f);
    }

    internal int ReverbBusCountForLab => _reverbBuses.Count;
    internal string ReverbBlendsForLab => string.Join(" ", _reverbSaVoices.Values.Select(v => v.State.SpatialBlend.ToString("F2")));

    /// <summary>Lab only: a voice's binaural stage as it last ran, each ear's level and how placed it
    /// is (1 = fully by the HRTF). False when the voice has no stage.</summary>
    internal bool TryGetBinauralLevels(int entityId, out float rmsL, out float rmsR, out float blend)
    {
        rmsL = rmsR = 0f; blend = 0f;
        lock (_lock)
        {
            var s = FindActive(entityId)?.SaState;
            if (s == null) return false;
            rmsL = s.LastRmsL; rmsR = s.LastRmsR; blend = s.SpatialBlend;
            return true;
        }
    }

    /// <summary>Lab only: every send plugged into a region's unit — who feeds it, and at what mix.</summary>
    internal string ListSends(int regionId)
    {
        if (!_reverbDsps.TryGetValue(regionId, out var dsp) || !dsp.hasHandle()) return "no unit";
        dsp.getNumInputs(out int inputs);
        var parts = new List<string>();
        for (int i = 0; i < inputs; i++)
        {
            if (dsp.getInput(i, out var from, out var conn) != RESULT.OK || !conn.hasHandle()) continue;
            conn.getMix(out float m);
            string who = "?";
            foreach (var a in _activeSounds)
            {
                if (!a.Channel.hasHandle()) continue;
                if (a.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var f) == RESULT.OK && f.handle == from.handle)
                { who = $"{a.SoundId}#{a.EntityId}{(conn.handle == a.SourceReverbConnection.handle ? " own" : conn.handle == a.ReverbConnection.handle ? " cross" : conn.handle == a.FadingReverbConnection.handle || conn.handle == a.FadingSourceConnection.handle ? " fading" : " UNTRACKED")}"; break; }
            }
            parts.Add($"{who} {m:F2}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Lab only: the traced stage's last block in and out (rms) and the bus head's blend.</summary>
    internal (double In, double Out, double PerChannel, int Channels, float Blend) TracedMeter(int regionId)
    {
        float blend = _reverbSaVoices.TryGetValue(regionId, out var v) ? v.State.SpatialBlend : -1f;
        return _traced.TryGetValue(regionId, out var t) ? (t.State.InEnergy, t.State.OutEnergy, t.State.ChannelEnergy, t.State.Channels, blend) : (0, 0, 0, 0, blend);
    }

    public string DescribeReverbChain(int regionId)
    {
        if (!_reverbDsps.TryGetValue(regionId, out var dsp) || !dsp.hasHandle()) return $"region {regionId}: no reverb unit";
        if (!_reverbBuses.TryGetValue(regionId, out var bus) || !bus.hasHandle()) return $"region {regionId}: no bus";

        dsp.setMeteringEnabled(true, true);
        float unitIn = 0f, unitOut = 0f;
        if (dsp.getMeteringInfo(out var inInfo, out var outInfo) == RESULT.OK)
        {
            for (int i = 0; i < inInfo.numchannels && i < 32; i++) unitIn = Math.Max(unitIn, inInfo.peaklevel[i]);
            for (int i = 0; i < outInfo.numchannels && i < 32; i++) unitOut = Math.Max(unitOut, outInfo.peaklevel[i]);
        }

        float headOut = 0f; string headName = "none";
        if (_reverbSaVoices.TryGetValue(regionId, out var voice) && voice.Dsp.hasHandle())
        {
            headName = "binaural";
            voice.Dsp.setMeteringEnabled(true, true);
            if (voice.Dsp.getMeteringInfo(IntPtr.Zero, out var hOut) == RESULT.OK)
                for (int i = 0; i < hOut.numchannels && i < 32; i++) headOut = Math.Max(headOut, hOut.peaklevel[i]);
        }

        bus.getVolume(out float vol);
        float tracked = _reverbVolumes.TryGetValue(regionId, out float tv) ? tv : float.NaN;
        // Sends actually plugged in: a computed send never connected reads healthy in every log line.
        dsp.getNumInputs(out int inputs);
        float loudestMix = 0f;
        for (int i = 0; i < inputs; i++)
            if (dsp.getInput(i, out _, out var conn) == RESULT.OK && conn.hasHandle()
                && conn.getMix(out float m) == RESULT.OK) loudestMix = Math.Max(loudestMix, m);
        return $"region {regionId}: {inputs} send(s) in, loudest mix {loudestMix:F3} | "
             + $"unit in {Db(unitIn),6:F1} -> out {Db(unitOut),6:F1} dB | "
             + $"{headName} out {Db(headOut),6:F1} dB | fader {vol:F3} (tracked {tracked:F3})";

        static float Db(float peak) => peak <= 1e-6f ? -120f : 20f * MathF.Log10(peak);
    }

    /// <summary>Detaches every bus's HRTF voice and returns it to the pool, before the buses are
    /// released. Safe to call repeatedly.</summary>
    private void ReturnReverbVoices()
    {
        foreach (var kvp in _reverbSaVoices)
        {
            var v = kvp.Value;
            // Pooled only once off its bus: a stage in two graphs is the crash Detach describes.
            bool off = !v.Dsp.hasHandle()
                    || (_reverbBuses.TryGetValue(kvp.Key, out var b) && b.hasHandle() && b.removeDSP(v.Dsp) == RESULT.OK)
                    || v.Dsp.disconnectAll(true, true) == RESULT.OK;
            if (off) { lock (_saPool) { _saPool.Push(v); } }
            else _failedDetaches++;
        }
        _reverbSaVoices.Clear();
    }

    public void PlaySpatialSound(SpatialEmitter emitter)
    {
        bool loopNative = emitter.Mode == PlaybackMode.LoopOne;
        lock (_lock)
        {
            var active = FindActive(emitter.EntityId);
            if (active != null)
            {
                if (emitter.IsGranular && active.GranularDsp.hasHandle() && active.SoundId == emitter.SoundId)
                {
                    UpdateSpatialAttributes(emitter);
                    return;
                }
                if (emitter.IsSynth && (active.SynthDsp.hasHandle() || active.EngineDsp.hasHandle()))
                {
                    UpdateSpatialAttributes(emitter);
                    return;
                }
                active.Channel.getCurrentSound(out var currentSound);
                if (!emitter.IsGranular && !emitter.IsSynth && currentSound.hasHandle() && active.SoundId == emitter.SoundId) { UpdateSpatialAttributes(emitter); return; }
                StopSound(emitter.EntityId);
            }
        }
        
        FMOD.ChannelGroup targetGroup = emitter.IsReflection ? _reflectionGroup : default;
        FMOD.Channel channel;
        FMOD.DSP granularDsp = default;
        System.Runtime.InteropServices.GCHandle granularHandle = default;
        GranularVoiceState? granularState = null;

        FMOD.DSP synthDsp = default;
        System.Runtime.InteropServices.GCHandle synthHandle = default;
        SynthVoiceState? synthState = null;
        FMOD.DSP engineDsp = default;
        System.Runtime.InteropServices.GCHandle engineHandle = default;
        EngineVoiceState? engineState = null;
        EngineEchoState? echoState = null;
        EngineTapState? tapState = null;
        PhysicalVoiceState? machineState = null;
        OwnVoiceTap? ownVoice = null;

        if (emitter.IsGranular)
        {
            if (!_isInitialized || !_granularBank.TryGetPcmData(emitter.SoundId, out var pcm, out var ch, out var sr)) return;
            var pooled = GetGranularDsp(pcm, ch, sr);
            if (pooled == null) return;
            granularDsp = pooled.Dsp;
            granularHandle = pooled.Handle;
            granularState = pooled.State;
            
            granularState.Position = emitter.GranularPosition;
            granularState.GrainSizeMs = emitter.GranularGrainSizeMs;
            granularState.Density = emitter.GranularDensity;
            granularState.Pitch = emitter.GranularPitch;
            granularState.PositionJitter = emitter.GranularPositionJitter;
            granularState.PitchJitter = emitter.GranularPitchJitter;
            granularState.PositionJitter = emitter.GranularPositionJitter;
            granularState.PitchJitter = emitter.GranularPitchJitter;

            if (_system.playDSP(granularDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                ReleaseGranularDsp(granularDsp, granularHandle, granularState);
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
        }
        else if (emitter.IsSynth && emitter.IntakeOfEntity != 0)
        {
            // The front outlet of a machine that already has a voice, reading that engine's front tap.
            // No engine this frame (it lost its slot), no voice.
            if (!_isInitialized) return;
            EngineVoiceState? src;
            lock (_lock) { src = FindActive(emitter.IntakeOfEntity)?.EngineState; }
            if (src == null) return;
            var tap = new EngineTapState(src);
            if (TapProcessor.CreateDSP(_system, tap, out engineDsp, out engineHandle) != RESULT.OK) return;
            engineDsp.setChannelFormat(0, 0, SPEAKERMODE.MONO);
            if (_system.playDSP(engineDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                engineDsp.release();
                engineHandle.Free();
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
            tapState = tap;
            // The engine's own voice stops carrying the front, slewed (EngineVoiceState.SplitVoices).
            src.SplitVoices = true;
        }
        else if (emitter.IsSynth && emitter.CabinOfEntity != 0)
        {
            // A path into the cabin the listener sits in (CabinPaths), read from the engine's ring.
            if (!_isInitialized) return;
            EngineVoiceState? src;
            lock (_lock) { src = FindActive(emitter.CabinOfEntity)?.EngineState; }
            if (src?.CabinLayout == null || emitter.CabinPath < 1 || emitter.CabinPath >= src.CabinLayout.Count) return;
            var tap = new EngineTapState(src, emitter.CabinPath);
            if (TapProcessor.CreateDSP(_system, tap, out engineDsp, out engineHandle) != RESULT.OK) return;
            engineDsp.setChannelFormat(0, 0, SPEAKERMODE.MONO);
            if (_system.playDSP(engineDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                engineDsp.release();
                engineHandle.Free();
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
            tapState = tap;
            // The engine's own voice stops carrying the path, slewed as the tap fades in.
            src.SetCabinTapLive(emitter.CabinPath, true);
        }
        else if (emitter.IsSynth && emitter.EchoOfEntity != 0)
        {
            if (!_isInitialized) return;
            EngineVoiceState? src;
            lock (_lock) { src = FindActive(emitter.EchoOfEntity)?.EngineState; }
            if (src == null) return;
            // A borrowed voice keeps its own read cursor; a reflection follows the source's, since an
            // echo of a car is that car's sound arriving late, Doppler and all.
            var echo = new EngineEchoState(src)
            {
                TargetDelaySeconds = emitter.EchoDelaySeconds,
                TargetGain = emitter.EchoGain,
                OwnCursor = !emitter.IsReflection,
                Scattering = emitter.IsReflection ? emitter.EchoScattering : -1f,
                Seed = emitter.EntityId,
            };
            if (EchoProcessor.CreateDSP(_system, echo, out engineDsp, out engineHandle) != RESULT.OK) return;
            engineDsp.setChannelFormat(0, 0, SPEAKERMODE.MONO);
            if (_system.playDSP(engineDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                engineDsp.release();
                engineHandle.Free();
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
            echoState = echo;
        }
        else if (emitter.IsSynth && (emitter.PhysicalKey.StartsWith("ownvoice:", StringComparison.Ordinal)
                                     || emitter.PhysicalKey.StartsWith(Talkers.KeyPrefix, StringComparison.Ordinal)
                                     || emitter.PhysicalKey.StartsWith(Talkers.CopyKeyPrefix, StringComparison.Ordinal)))
        {
            // The player's own microphone read back at a delay, their room answering them (the room
            // feed plays into a group at zero, so only its sends are heard); somebody else talking, from
            // what has arrived of their voice (TalkerStream); or a surface answering them, at the copy's delay.
            if (!_isInitialized) return;
            OwnVoiceRing ring = OwnVoiceRing.Shared;
            double maxPull = 0.01;
            bool measures = true;
            if (Talkers.TryParseKey(emitter.PhysicalKey, out int talker))
            {
                if (!Talkers.TryGet(talker, out var stream)) return;
                ring = stream.Ring;
                maxPull = TalkerStream.MaxPull;
            }
            else if (Talkers.TryParseCopyKey(emitter.PhysicalKey, out int answered))
            {
                if (!Talkers.TryGet(answered, out var stream)) return;
                ring = stream.Ring;
                measures = false;
            }
            _system.getSoftwareFormat(out int orate, out _, out _);
            var tap = new OwnVoiceTap(ring, emitter.EchoDelaySeconds, orate, maxPull, measures);
            if (OwnVoiceProcessor.CreateDSP(_system, tap, out engineDsp, out engineHandle) != RESULT.OK) return;
            engineDsp.setChannelFormat(0, 0, SPEAKERMODE.MONO);
            if (emitter.PhysicalKey == OwnVoiceRoomKey) targetGroup = _ownVoiceRoomGroup;
            if (_system.playDSP(engineDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                engineDsp.release();
                engineHandle.Free();
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
            ownVoice = tap;
        }
        else if (emitter.IsSynth && !string.IsNullOrEmpty(emitter.PhysicalKey))
        {
            // A physical model that is not a vehicle (PhysicalVoiceState). The one place a name turns
            // into a model, which is why the emitter carries the prefix rather than a key and a flag.
            if (!_isInitialized) return;
            _system.getSoftwareFormat(out int mrate, out _, out _);
            int colon = emitter.PhysicalKey.IndexOf(':');
            string kind = colon > 0 ? emitter.PhysicalKey[..colon] : "";
            string preset = colon > 0 ? emitter.PhysicalKey[(colon + 1)..] : emitter.PhysicalKey;
            try
            {
                machineState = kind.ToLowerInvariant() switch
                {
                    "machine" => new MachineVoiceState(OpenFPS.Common.SmallMachineSpec.ByName(preset),
                                                       mrate, emitter.EntityId, emitter.EntityId * 31 + 7),
                    "aircraft" => new AircraftVoiceState(OpenFPS.Common.AircraftProfile.ByName(preset),
                                                         mrate, emitter.EntityId * 17 + 3,
                                                         lever: emitter.PowerLever),
                    "rail" => RailSlot(emitter.PhysicalKey, mrate),
                    // Its own voice: 35 dB over the car's exhaust, the two cannot share one full-scale
                    // reference (SirenVoiceState).
                    "siren" => new SirenVoiceState(OpenFPS.Common.SirenSpec.ByName(preset), mrate),
                    // Running is the server's word, not worked out locally (BellVoiceState).
                    "bell" => new BellVoiceState(OpenFPS.Common.ModelLibrary.Bell(preset),
                                                 mrate, emitter.EntityId * 13 + 5),
                    // The arm follows the crossing's closed signal (Running, the server's word).
                    "gate" => new GateVoiceState(OpenFPS.Common.CrossingGateSpec.ByName(preset), mrate, closed: emitter.EngineRunning),
                    // Given their place: they read the wind where they stand (NatureVoiceState).
                    "water" => Water(emitter.PhysicalKey, mrate, emitter.EntityId, emitter.Position, emitter),
                    // Heard from places across it (ExtendedSources), all reading the middle's synth.
                    "fire" or "foliage" or "flow" or "shore" or "wood" => NaturePlace(kind.ToLowerInvariant(), preset, emitter, mrate),
                    // Rain round the listener, fed by the rain survey (RainVoiceState); the roof over
                    // the ear and the near quarters are several parts each (RainFeeds.PartsFor).
                    "rain" => RainFeeds.TryParse(emitter.PhysicalKey, out int rainSlot, out int rainPart)
                                && rainPart < RainParts(rainSlot)
                        ? new RainVoiceState(RainFeeds.Feed[rainSlot], mrate, rainSlot * 53 + 23 + rainPart * 7919, rainPart, RainParts(rainSlot))
                        : null,
                    // The rhythm of the hand on the horn is in the key (Honk).
                    "horn" => OpenFPS.Common.Honk.TryParse(emitter.PhysicalKey, out var hornKey, out var rhythm)
                        ? new HornVoiceState(hornKey, rhythm, mrate, emitter.EntityId * 29 + 1)
                        : null,
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                Log.Warning("Physical voice: '{Key}' for entity {Id} — {Message}",
                            emitter.PhysicalKey, emitter.EntityId, ex.Message);
                return;
            }
            if (machineState == null)
            {
                // An outer place whose source has no voice this frame; asked again next frame.
                if (emitter.PlaceOfEntity != 0) return;
                Log.Warning("Physical voice: '{Key}' names no model this client knows.", emitter.PhysicalKey);
                return;
            }
            machineState.Running = emitter.EngineRunning;
            if (ListenerInMachineFrame(emitter.Position, emitter.Direction, emitter.Velocity, out var mlocal))
                machineState.SetListener(mlocal);
            if (MachineProcessor.CreateDSP(_system, machineState, out engineDsp, out engineHandle) != RESULT.OK) return;
            engineDsp.setChannelFormat(0, 0, SPEAKERMODE.MONO);
            Log.Information("Physical voice started: entity {Id} runs '{Preset}' live at {Rate} Hz",
                            emitter.EntityId, emitter.PhysicalKey, mrate);
            if (_system.playDSP(engineDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                engineDsp.release();
                engineHandle.Free();
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
        }
        else if (emitter.IsSynth && !string.IsNullOrEmpty(emitter.EngineKey))
        {
            if (!_isInitialized) return;
            _system.getSoftwareFormat(out int rate, out _, out _);
            engineState = new EngineVoiceState(OpenFPS.Common.MachineRegistry.VehicleFor(emitter.EngineKey), rate, emitter.EntityId)
            {
                TargetSpeed = emitter.EngineSpeed,
                Running = emitter.EngineRunning,
                ServingStop = emitter.ServingStop,
                WindowsOpen = emitter.WindowsOpen,
                Interior = emitter.Interior,
                CabinEarX = emitter.CabinEarX,
                RoadWaterMm = emitter.RoadWaterMm,
                // The loudness law on what the engine is doing now, not only its declared level.
                CompensateLevel = true,
            };
            // Start the engine at the car's speed, not spinning up from rest in the first 80 ms.
            engineState.PlaceAtSpeed(emitter.EngineSpeed);
            if (emitter.WheelStrikes != null) engineState.QueueStrikes(emitter.WheelStrikes);
            if (ListenerInMachineFrame(emitter.Position, emitter.Direction, emitter.Velocity, out var localListener))
                engineState.SetListener(localListener);
            if (EngineProcessor.CreateDSP(_system, engineState, out engineDsp, out engineHandle) != RESULT.OK) return;
            engineDsp.setChannelFormat(0, 0, SPEAKERMODE.MONO);
            Log.Information("Engine voice started: entity {Id} runs '{Preset}' live at {Rate} Hz", emitter.EntityId, emitter.EngineKey, rate);
            if (_system.playDSP(engineDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                engineDsp.release();
                engineHandle.Free();
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
        }
        else if (emitter.IsSynth)
        {
            if (!_isInitialized) return;
            
            var pooled = GetSynthDsp();
            if (pooled == null) return;
            synthDsp = pooled.Dsp;
            synthHandle = pooled.Handle;
            synthState = pooled.State;
            
            synthState.WaveType = emitter.SynthWave;
            synthState.Frequency = emitter.SynthFrequency;
            synthState.LfoRate = emitter.SynthLfoRate;
            synthState.LfoDepth = emitter.SynthLfoDepth;
            synthState.FilterCutoff = emitter.SynthFilterCutoff;
            synthState.FilterResonance = emitter.SynthFilterResonance;
            synthState.PulseWidth = emitter.SynthPulseWidth;

            if (_system.playDSP(synthDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                ReleaseSynthDsp(synthDsp, synthHandle, synthState);
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
        }
        else
        {
            if (!_isInitialized) return;
            var loadState = _resources.TryGetSound(emitter.SoundId, out FMOD.Sound sound, loopNative);
            if (loadState != SoundLoadState.Ready)
            {
                if (_audioDebug && emitter.Mode == PlaybackMode.LoopOne)
                    Log.Information("[BEACON] e{Id} '{Sound}' not playing yet — {State}", emitter.EntityId, emitter.SoundId, loadState);

                // A load in flight parks the play for retry (DrainDeferredPlays); only a missing asset,
                // already logged by name, is dropped.
                if (loadState == SoundLoadState.Loading) DeferPlay(emitter);
                return;
            }
            RESULT playRes = _system.playSound(sound, targetGroup, true, out channel);
            if (playRes != RESULT.OK)
            {
                // ERR_NOTREADY can still race a load that completed between getOpenState and playSound.
                if (playRes == RESULT.ERR_NOTREADY) DeferPlay(emitter);
                else Log.Warning("playSound failed for '{Sound}' on entity {Id}: {Result}", emitter.SoundId, emitter.EntityId, playRes);
                return;
            }
            channel.setMode(MODE._3D | Rolloff.Mode);
            // A sound made in memory has no looping copy: its channel loops instead.
            if (loopNative && _resources.IsRegisteredPcm(emitter.SoundId))
            {
                channel.setMode(MODE.LOOP_NORMAL);
                channel.setLoopCount(-1);
            }
        }

        if (_audioDebug && emitter.Mode == PlaybackMode.LoopOne)
            Log.Information("[BEACON] e{Id} '{Sound}' PLAYING pos=({X:F0},{Y:F0},{Z:F0}) range={R:F0}",
                emitter.EntityId, emitter.SoundId, emitter.Position.X, emitter.Position.Y, emitter.Position.Z, emitter.Range);

        FMOD.DSP threeEqDsp = default, diffractionDsp = default;
        SteamAudioVoiceState? saState = null;
        FMOD.DSP saDsp = default;
        System.Runtime.InteropServices.GCHandle saHandle = default;
        if (emitter.Type != EmitterType.UI)
        {
            threeEqDsp = GetThreeEqDsp();
            channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, threeEqDsp);

            // No diffraction filter on a reflection: it is already a modelled path, bending it as well
            // counts the geometry twice, and on a track one DSP fewer per echo is room for another car.
            if (!emitter.IsReflection)
            {
                diffractionDsp = GetDiffractionDsp();
                channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, diffractionDsp);
            }
            if (_steamAudioEnabled && TryCreateSteamAudioVoice(out saState, out saDsp, out saHandle,
                                                               emitter.IsReflection ? SaDirectReserve : 0))
            {
                saState!.GuardName = emitter.SoundId;
                channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, saDsp);

                // 2D, keeping the other flags: a 3D channel downmixes the binaural pair to mono, even
                // at set3DLevel(0) (measured L≈R). Distance is applied by hand in ApplyAcousticFilters.
                channel.getMode(out MODE chMode);
                channel.setMode((chMode & ~(MODE._3D | Rolloff.Either)) | MODE._2D);
            }
            else
            {
                // No HRTF voice: FMOD's inverse rolloff is the law the HRTF path applies by hand, so a
                // voice that misses the pool is not louder and nearer (linear left a clap 200 m across a
                // 3 km range at 93 % of full scale). Counted for direct sounds only: an echo refused by
                // the reserve is the policy working.
                if (!emitter.IsReflection) _saPoolMisses++;
                channel.getMode(out MODE fallbackMode);
                channel.setMode((fallbackMode & ~Rolloff.Either) | MODE._3D | MODE._3D_INVERSEROLLOFF);
                channel.set3DLevel(1.0f);
            }
            // Never 3D calls on a channel made 2D: FMOD refuses with ERR_NEEDS3D, 113,228 times in a
            // 25 s run (91 % of its log), each taking FMOD's lock on the game thread against the mixer.
            if (saState == null)
            {
                channel.set3DMinMaxDistance(emitter.MinDistance, emitter.Range);
                if (emitter.ConeInside < 360f)
                {
                    channel.set3DConeSettings(emitter.ConeInside, emitter.ConeOutside, emitter.ConeOutsideVolume);
                    FMOD.VECTOR fdir = FmodHelpers.ToFmodVec(emitter.Direction);
                    channel.set3DConeOrientation(ref fdir);
                }
            }
        }
        else
        {
            // In the head: a cue for the player (the driving aids), panned by its direction and nothing
            // else, no HRTF, distance, room or echo. Head-relative, so it stays put as the head turns.
            channel.getMode(out MODE headMode);
            channel.setMode((headMode & ~Rolloff.Either) | MODE._3D | MODE._3D_HEADRELATIVE);
            channel.set3DMinMaxDistance(1000f, 10000f);
            channel.set3DLevel(1.0f);
            PlaceInHead(channel, emitter.FollowsListener ? emitter.ListenerOffset : emitter.Position - _listenerPos);
        }
        
        channel.setVolume(emitter.Volume);
        channel.setPitch(emitter.IsGranular || emitter.IsSynth ? 1.0f : emitter.Pitch); 

        // A reflection starts at its source's playback position, then waits the path's extra delay.
        // From sample zero it was the announcement again from the top ("I hear like 2 copies, one
        // latent like it is echoing off something way far away").
        if (emitter.IsReflection && emitter.ReflectionOf != 0 && !emitter.IsSynth && !emitter.IsGranular)
        {
            lock (_lock)
            {
                var source = FindActive(emitter.ReflectionOf);
                if (source != null && source.Channel.hasHandle()
                    && string.Equals(source.SoundId, emitter.SoundId, StringComparison.OrdinalIgnoreCase)
                    && source.Channel.getPosition(out uint pcm, TIMEUNIT.PCM) == RESULT.OK)
                {
                    channel.setPosition(pcm, TIMEUNIT.PCM);
                }
            }
        }

        // When the voice begins, on the parent's DSP clock (setDelay and addFadePoint take the parent
        // ChannelGroup's): the channel's own clock put every start and fade point in the past, so no
        // reflection was ever delayed (a comb, a metallic ring) and no one-shot ever had its onset
        // ramp (AudioLab --room-walk clock). Kept for the onset ramp below.
        channel.getDSPClock(out _, out ulong voiceStartClock);   // (dspclock, parentclock)
        if (emitter.DelayMs > 0)
        {
            _system.getSoftwareFormat(out int rate, out _, out _);
            voiceStartClock += (ulong)(rate * (emitter.DelayMs / 1000.0f));
            channel.setDelay(voiceStartClock, 0, false);
        }

        // Louder at the ear than the output can go: the rest of the world gives way (Overload).
        float overDb = OverloadDb(emitter);
        if (overDb > 0f)
        {
            Overload(overDb, emitter.DelayMs / 1000.0);
            Log.Information("[OVERLOAD] {Sound} at {Distance:F0} m: the rest gives way {Db:F1} dB", emitter.SoundId,
                            emitter.EffectiveDistance > 0f ? emitter.EffectiveDistance : Vector3.Distance(_listenerPos, emitter.Position), overDb);
        }

        lock (_lock) { 
            var activeSound = new ActiveSound {
                LastStrikes = emitter.WheelStrikes, 
                EntityId = emitter.EntityId, SoundId = emitter.SoundId, Type = emitter.Type, 
                OverloadExempt = overDb > 0f,
                Channel = channel, ThreeEqDsp = threeEqDsp, DiffractionDsp = diffractionDsp,
                GranularDsp = granularDsp, GranularHandle = granularHandle, GranularState = granularState,
                SynthDsp = synthDsp, SynthHandle = synthHandle, SynthState = synthState,
                EngineDsp = engineDsp, EngineHandle = engineHandle, EngineState = engineState, EchoState = echoState,
                MachineState = machineState,
                OwnVoice = ownVoice,
                TapState = tapState,
                Position = emitter.Position, ApparentPosition = emitter.ApparentPosition,
                LastAttributeAt = emitter.PositionSampledAt > 0 ? emitter.PositionSampledAt : OpenFPS.Common.AudioClock.Now,
                CurrentApparentPosition = (emitter.ApparentPosition != Vector3.Zero) ? emitter.ApparentPosition : emitter.Position, 
                EffectiveDistance = emitter.EffectiveDistance, Velocity = emitter.Velocity, Direction = emitter.Direction, 
                Range = emitter.Range, MinDistance = emitter.MinDistance, BaseVolume = emitter.Volume * TakeGain(emitter), Pitch = emitter.Pitch,
                TargetOcclusion = emitter.Occlusion, 
                CurrentOcclusion = emitter.Occlusion,
                TargetAperture = emitter.ApertureFactor, CurrentAperture = emitter.ApertureFactor,
                TargetBleed = emitter.TransmissionBleed, CurrentBleed = emitter.TransmissionBleed,
                AirLowDb = emitter.AirLowDb, AirMidDb = emitter.AirMidDb, AirHighDb = emitter.AirHighDb,
                TargetLow = emitter.EqLow, CurrentLow = emitter.EqLow,
                TargetMid = emitter.EqMid, CurrentMid = emitter.EqMid,
                TargetHigh = emitter.EqHigh, CurrentHigh = emitter.EqHigh,
                TargetRegionId = emitter.TargetRegionId, IsReflection = emitter.IsReflection,
                FollowsListener = emitter.FollowsListener, ListenerOffset = emitter.ListenerOffset,
                InsideListenersVehicle = emitter.InsideListenersVehicle,
                RoomGain = 1.0f,
                ConeInside = emitter.ConeInside, ConeOutside = emitter.ConeOutside, ConeOutsideVolume = emitter.ConeOutsideVolume,
                ReflectionSpread = emitter.ReflectionSpread,
                SaState = saState, SaDsp = saDsp, SaHandle = saHandle
            };
            if (engineState == null && tapState == null && machineState == null && !emitter.IsReflection)
            {
                saState?.Ground?.Set(emitter.GroundDelaySeconds, emitter.GroundLowGain, emitter.GroundHighGain);
                activeSound.GroundHeight = HasGround(emitter) ? emitter.GroundHeight : null;
            }

            if (_acousticMap != null && !activeSound.IsReflection && emitter.Type != EmitterType.UI) // a reflection does not feed the reverb
            {
                int sourceRegionId = activeSound.TargetRegionId;
                // Not "sourceRegionId != -1": outdoors is region -1, and that test kept every outdoor
                // sound off every bus (a street measuring two seconds sounded dead). TryGetReverbInput
                // is the test wanted.
                // The send tap goes last onto the tail, so it is the first thing the signal meets.
                {
                    var tap = GetSendTapDsp();
                    if (activeSound.Channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, tap) == RESULT.OK) activeSound.SendTap = tap;
                    else _sendTapPool.Push(tap);
                }
                if (TryGetReverbInput(sourceRegionId, out var sourceReverb))
                {
                    sourceReverb.addInput(SourceSendDsp(activeSound), out activeSound.SourceReverbConnection, DSPCONNECTION_TYPE.SEND);
                    activeSound.SourceReverbBus = sourceReverb;
                    activeSound.SourceReverbConnection.setMix(AcousticConstants.ReverbSendMix
                        * (activeSound.SendTap.hasHandle() ? emitter.Volume : 1f));
                    activeSound.SourceReverbMix = 1f;   // a new voice has no running signal to step
                    activeSound.CurrentSourceRegionId = sourceRegionId;
                }

                if (TryGetReverbInput(_listenerRegionId, out var listenerReverb))
                {
                    activeSound.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var channelDsp);
                    listenerReverb.addInput(channelDsp, out activeSound.ReverbConnection, DSPCONNECTION_TYPE.SEND);
                    activeSound.ReverbBus = listenerReverb;
                    activeSound.ReverbConnection.setMix(AcousticConstants.ReverbSendMix * AcousticConstants.ReverbCrossSendScale);
                    activeSound.ReverbMix = 1f;
                    activeSound.CurrentRegionId = _listenerRegionId;
                }
            }

            AttachEar(activeSound, emitter);
            AddActive(activeSound);
        }

        // A 6 ms fade-in on one-shots (an abrupt onset on a recycled HRTF voice clicks) and on
        // reflections (joined mid-file, a step from silence each time a wall starts answering). It
        // begins at the voice's scheduled start; fade points multiply with setVolume.
        if ((emitter.IsEvent || emitter.IsReflection) && channel.hasHandle())
        {
            _system.getSoftwareFormat(out int sr, out _, out _);
            ulong ramp = (ulong)(sr * 0.006f);
            channel.addFadePoint(voiceStartClock, 0.0f);
            channel.addFadePoint(voiceStartClock + ramp, 1.0f);
        }

        channel.setPaused(false);
    }

    public void UpdateSpatialAttributes(SpatialEmitter emitter) 
    { 
        lock (_lock) 
        { 
            var active = FindActive(emitter.EntityId); 
            if (active != null) 
            { 
                // When the position was true, not when it arrived: the voice manager re-applies every
                // voice at 250 Hz, and stamping "now" hid the 30 Hz staircase from dead reckoning. An
                // emitter with no sample time is as fresh as this call.
                active.LastAttributeAt = emitter.PositionSampledAt > 0
                    ? emitter.PositionSampledAt
                    : OpenFPS.Common.AudioClock.Now;
                active.Position = emitter.Position; active.ApparentPosition = emitter.ApparentPosition; 
                active.FollowsListener = emitter.FollowsListener; active.ListenerOffset = emitter.ListenerOffset;
                active.EffectiveDistance = emitter.EffectiveDistance; active.Velocity = emitter.Velocity; 
                active.Direction = emitter.Direction; active.Range = emitter.Range; 
                active.BaseVolume = emitter.Volume * TakeGain(emitter);
                UpdateEarLevel(active, emitter);
                if (emitter.CarriesPath)
                {
                    active.TargetLow = emitter.EqLow; active.TargetMid = emitter.EqMid; active.TargetHigh = emitter.EqHigh;
                    active.AirLowDb = emitter.AirLowDb; active.AirMidDb = emitter.AirMidDb; active.AirHighDb = emitter.AirHighDb;
                    active.TargetRegionId = emitter.TargetRegionId;
                    // Part of the path: a talker can get into your car mid-sentence, or you into theirs.
                    active.InsideListenersVehicle = emitter.InsideListenersVehicle;
                }
                if (emitter.IsGranular && active.GranularState != null)
                {
                    active.GranularState.Position = emitter.GranularPosition;
                    active.GranularState.GrainSizeMs = emitter.GranularGrainSizeMs;
                    active.GranularState.Density = emitter.GranularDensity;
                    active.GranularState.Pitch = emitter.GranularPitch;
                    active.GranularState.PositionJitter = emitter.GranularPositionJitter;
                    active.GranularState.PitchJitter = emitter.GranularPitchJitter;
                }
                else if (emitter.IsSynth && active.OwnVoice != null)
                {
                    // A copy's path changes as the player moves; the tap slews to it.
                    active.OwnVoice.TargetDelay = emitter.EchoDelaySeconds;
                }
                else if (emitter.IsSynth && active.MachineState != null)
                {
                    active.MachineState.Running = emitter.EngineRunning;
                    // The power lever slot, read off the climb angle in ClientAudioSystem for an aircraft.
                    if (active.MachineState is SirenVoiceState sirenv)
                    {
                        // For a siren it carries the mode: a dashboard switch, nothing to slew.
                        sirenv.TargetMode = (int)MathF.Round(emitter.PowerLever);
                    }
                    else if (active.MachineState is AircraftVoiceState airv)
                    {
                        airv.TargetLever = emitter.PowerLever;
                        airv.TargetDescending = emitter.RotorWake;
                        // The runway against a wheel not yet turning: the aeroplane's own speed.
                        airv.TargetGroundSpeed = emitter.Velocity.Length();
                        airv.TargetOnGround = emitter.OnGround;
                    }
                    else if (active.MachineState is MachineVoiceState mach)
                    {
                        mach.TargetGroundSpeed = emitter.Velocity.Length();
                    }
                    else if (active.MachineState is TrainSlotState trainVoice)
                    {
                        // Any of its voices may set it, all reading one train: the lever is the notch
                        // over eight, the wake slot the speed.
                        trainVoice.Shared.TargetSpeed = emitter.RotorWake;
                        trainVoice.Shared.TargetNotch = emitter.PowerLever * 8f;
                    }
                    else if (active.MachineState is NaturePlaceState { Place: 0 } middle)
                    {
                        // The middle carries how much its other places play, and a wood's how many
                        // trees it stands for now (WoodChorus).
                        middle.Shared.TargetSpread = emitter.Spread;
                        if (middle.Shared.WindPlaces != null) middle.Shared.TargetTrees = emitter.Trees;
                    }
                    else if (active.MachineState is WaterTapState { Place: 0 } tapMiddle)
                    {
                        tapMiddle.Shared.SetSpread(tapMiddle.Tap, emitter.Spread);
                    }
                    if (ListenerInMachineFrame(emitter.Position, emitter.Direction, emitter.Velocity, out var mlocal))
                        active.MachineState.SetListener(mlocal);
                }
                else if (emitter.IsSynth && active.EngineState != null)
                {
                    active.EngineState.TargetSpeed = emitter.EngineSpeed;
                    active.EngineState.Interior = emitter.Interior;
                    active.EngineState.CabinEarX = emitter.CabinEarX;
                    active.EngineState.Running = emitter.EngineRunning;
                    active.EngineState.ServingStop = emitter.ServingStop;
                    active.EngineState.WindowsOpen = emitter.WindowsOpen;
                    active.EngineState.RoadSlip = emitter.TyreSlip;
                    active.EngineState.Wheels = emitter.Wheels;
                    if (emitter.WheelStrikes != null && !ReferenceEquals(emitter.WheelStrikes, active.LastStrikes))
                    {
                        active.EngineState.QueueStrikes(emitter.WheelStrikes);
                        active.LastStrikes = emitter.WheelStrikes;
                    }
                    active.EngineState.RoadWaterMm = emitter.RoadWaterMm;
                    if (ListenerInMachineFrame(emitter.Position, emitter.Direction, emitter.Velocity, out var local))
                        active.EngineState.SetListener(local);
                }
                else if (emitter.IsSynth && active.EchoState != null)
                {
                    active.EchoState.TargetDelaySeconds = emitter.EchoDelaySeconds;
                    active.EchoState.TargetGain = emitter.EchoGain;
                }
                else if (emitter.IsSynth && active.SynthState != null)
                {
                    active.SynthState.WaveType = emitter.SynthWave;
                    active.SynthState.Frequency = emitter.SynthFrequency;
                    active.SynthState.LfoRate = emitter.SynthLfoRate;
                    active.SynthState.LfoDepth = emitter.SynthLfoDepth;
                    active.SynthState.FilterCutoff = emitter.SynthFilterCutoff;
                    active.SynthState.FilterResonance = emitter.SynthFilterResonance;
                    active.SynthState.PulseWidth = emitter.SynthPulseWidth;
                }
                else
                {
                    active.Pitch = emitter.Pitch;
                    active.Channel.setPitch(active.Pitch);
                }
                var ground = active.EngineState?.Ground ?? active.TapState?.Ground ?? active.MachineState?.Ground;
                ground?.Set(emitter.GroundDelaySeconds, emitter.GroundLowGain, emitter.GroundHighGain);
                // A recorded sound's ground is in its binaural stage. Never both, or the ground answers twice.
                if (ground == null && !active.IsReflection)
                {
                    active.SaState?.Ground?.Set(emitter.GroundDelaySeconds, emitter.GroundLowGain, emitter.GroundHighGain);
                    active.GroundHeight = HasGround(emitter) ? emitter.GroundHeight : null;
                }
                active.MinDistance = emitter.MinDistance;
                // No 3D calls on a 2D (binaural) voice: run every frame, this was most of the 113,228
                // refused calls (see PlaySpatialSound).
                if (active.SaState == null)
                {
                    active.Channel.set3DMinMaxDistance(active.MinDistance, emitter.Range);
                    if (emitter.ConeInside < 360f)
                    {
                        active.Channel.set3DConeSettings(emitter.ConeInside, emitter.ConeOutside, emitter.ConeOutsideVolume);
                        FMOD.VECTOR fdir = FmodHelpers.ToFmodVec(emitter.Direction);
                        active.Channel.set3DConeOrientation(ref fdir);
                    }
                }
                active.TargetOcclusion = emitter.Occlusion; active.TargetAperture = emitter.ApertureFactor;
                active.TargetBleed = emitter.TransmissionBleed; active.ConeInside = emitter.ConeInside;
                active.ConeOutside = emitter.ConeOutside; active.ConeOutsideVolume = emitter.ConeOutsideVolume;
            } 
        } 
    }

    public void SetAcousticPath(int entityId, AcousticPathData path) 
    { 
        lock (_lock) 
        { 
            if (!_activeById.TryGetValue(entityId, out var voices)) return;
            foreach (var active in voices) 
            {
                // The car you sit in already rendered what gets through its body; a path traced through
                // the same body would take it away twice.
                if (active.EngineState is { Interior: true }) continue;
                active.TargetOcclusion = path.Occlusion;
                active.ApparentPosition = path.ApparentPosition;
                active.EffectiveDistance = path.EffectiveDistance;
                active.TargetAperture = path.ApertureFactor;
                active.TargetBleed = path.TransmissionBleed;
                active.AirLowDb = path.AirLowDb; active.AirMidDb = path.AirMidDb; active.AirHighDb = path.AirHighDb;
                active.TargetRegionId = path.RegionId;
                active.TargetLow = path.EqLow;
                active.TargetMid = path.EqMid;
                active.TargetHigh = path.EqHigh;
                active.RoomGain = path.RoomGain;
            } 
        } 
    }

    // Deferred plays: without them the first play of any sound not preloaded is lost (the decode is in
    // flight and playSound answers ERR_NOTREADY). Parked and retried, it arrives a few ms late instead.
    private sealed class DeferredPlay
    {
        public SpatialEmitter Emitter;
        public long DeadlineTicks;
        public DeferredPlay(SpatialEmitter emitter, long deadlineTicks) { Emitter = emitter; DeadlineTicks = deadlineTicks; }
    }

    // Generous: a cold ogg decode on a slow disk is well inside it, and a late sound beats a missing one.
    private const int DeferredPlayTimeoutMs = 3000;
    private readonly List<DeferredPlay> _deferredPlays = new();
    private readonly object _deferredLock = new();

    /// <summary>Parks an emitter whose sound is still decoding, to be retried by <see cref="Update"/>.
    /// The latest request per entity wins, so a re-triggered emitter never queues twice.</summary>
    private void DeferPlay(SpatialEmitter emitter)
    {
        long deadline = Environment.TickCount64 + DeferredPlayTimeoutMs;
        lock (_deferredLock)
        {
            for (int i = 0; i < _deferredPlays.Count; i++)
            {
                if (_deferredPlays[i].Emitter.EntityId != emitter.EntityId) continue;
                // The original deadline stands, or a broken asset re-asked every frame is never reported.
                _deferredPlays[i].Emitter = emitter;
                return;
            }
            _deferredPlays.Add(new DeferredPlay(emitter, deadline));
        }
    }

    /// <summary>Retries parked plays whose sound has decoded and reports those out of time. Called at
    /// the top of <see cref="Update"/>, outside the active-sound lock: a retry re-enters PlaySpatialSound.</summary>
    private void DrainDeferredPlays()
    {
        List<DeferredPlay>? due = null;
        long now = Environment.TickCount64;

        lock (_deferredLock)
        {
            if (_deferredPlays.Count == 0) return;
            due = new List<DeferredPlay>(_deferredPlays);
            _deferredPlays.Clear();
        }

        foreach (var d in due)
        {
            bool loopNative = d.Emitter.Mode == PlaybackMode.LoopOne;
            var state = _resources.TryGetSound(d.Emitter.SoundId, out _, loopNative);

            if (state == SoundLoadState.Ready)
            {
                PlaySpatialSound(d.Emitter);   // re-enters the normal path now that the decode is done
                continue;
            }

            if (state == SoundLoadState.Missing)
                continue;                       // already logged by name in the resource manager

            if (now >= d.DeadlineTicks)
            {
                // What FMOD says: the open state and the buffered percentage tell a slow decode from a
                // wedged one.
                _resources.DescribeLoad(d.Emitter.SoundId, loopNative, out string detail);
                Log.Warning("Audio asset '{SoundId}' still not decoded after {Timeout} ms; entity {Id} stayed silent. {Detail}",
                    d.Emitter.SoundId, DeferredPlayTimeoutMs, d.Emitter.EntityId, detail);
                continue;
            }

            lock (_deferredLock) { _deferredPlays.Add(d); }   // still loading — keep waiting
        }
    }

    /// <summary>The mixer's DSP load, 0..1+, as of the last sample. Past 1 the callback is late, and
    /// late is torn audio.</summary>
    public float MixerLoad => _mixerLoad;
    private volatile float _mixerLoad;
    /// <summary>The load sample's interval timer, restarted every quarter second: never a timebase for
    /// positions (a restart gives a negative age). Timestamps come from <see cref="OpenFPS.Common.AudioClock"/>.</summary>
    private readonly System.Diagnostics.Stopwatch _loadSampleClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>FMOD's own DSP load, per cent of the mixer's deadline, now. The lab's cost figures.</summary>
    internal float DspCpuPercent() => _isInitialized && _system.getCPUUsage(out var u) == RESULT.OK ? u.dsp : float.NaN;

    /// <summary>
    /// Samples the mixer's load for <see cref="MixerLoad"/> and logs the mixer's health. Clipping (a
    /// wrong level reference) and starving (a missed deadline) are both called "crackling"; the dsp
    /// figure tells them apart: past about 60 % the callback is running out of time. A live engine is
    /// the dearest voice (`--engine-cost`): read this before raising OPENFPS_ENGINE_VOICES.
    /// </summary>
    private void ReportMixerLoad()
    {
        // Sampled far more often than logged: something steers on it.
        if (_loadSampleClock.Elapsed.TotalSeconds >= 0.25)
        {
            _loadSampleClock.Restart();
            if (_system.getCPUUsage(out var now) == RESULT.OK)
            {
                // A slow average: the control loop must not chase a single busy block.
                float f = now.dsp * 0.01f;
                _mixerLoad += (f - _mixerLoad) * 0.35f;
            }
        }

        // Every five seconds, or every second after a starve or a gen2 collection.
        double since = _cpuClock.Elapsed.TotalSeconds;
        bool trouble = EngineVoiceState.GlobalStarves != _lastStarves || GC.CollectionCount(2) != _lastGen2;
        if (since < 5.0 && !(trouble && since >= 1.0)) return;
        _cpuClock.Restart();
        if (_system.getCPUUsage(out var cpu) != RESULT.OK) return;
        int voices = 0;
        lock (_lock) foreach (var a in _activeSounds) if (a.EngineState != null || a.EchoState != null) voices++;

        // A dsp percentage cannot tell a busy mixer from a frozen one, so the line carries deltas of
        // what freezes it: starves (blocks the producers had not rendered), gen2 collections and GC
        // pause (a native mixer thread entering managed code waits the GC out, which no buffer can
        // absorb), and real channels against the limit (past it the quietest voices go silent).
        // The worst any one voice went unplaced, apart from the audio system's figure on purpose: that
        // says the placement pass ran, this that it reached every voice ("some of the cars stop").
        double nowSec = OpenFPS.Common.AudioClock.Now;
        double worstStale = 0; int worstId = 0;
        lock (_lock)
        {
            foreach (var a in _activeSounds)
            {
                if (a.LastAttributeAt <= 0) continue;
                // A voice pinned to the listener waits for no update; counted, every footstep buried the
                // one voice really left behind.
                if (a.FollowsListener) { a.WorstPositionAge = 0; continue; }
                // The worst during the interval, or now: a voice abandoned since the last report has no
                // placement to have recorded it.
                double stale = Math.Max(a.WorstPositionAge, nowSec - a.LastAttributeAt);
                a.WorstPositionAge = 0;
                if (stale > worstStale) { worstStale = stale; worstId = a.EntityId; }
            }
        }

        int noHrtf = 0;
        foreach (var a in _activeSounds) if (a.SaState == null && !a.IsReflection && a.Channel.hasHandle()) noHrtf++;

        int starves = EngineVoiceState.GlobalStarves;
        int gen2 = GC.CollectionCount(2);
        double pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        _system.getChannelsPlaying(out int playing, out int real);
        Log.Information("Mixer load: dsp {Dsp:F1}%, update {Update:F1}%, stream {Stream:F1}% — "
                      + "{Engines} engine/echo voice(s) of {Total} active, {Real}/{Playing} real channel(s), "
                      + "{NoHrtf} direct voice(s) without HRTF ({Misses} new since last, {SaFree} binaural free), {Starve} starve(s), "
                      + "gc {Gen2} gen2 / {Pause:F0} ms paused, {Late} DSP(s) cut loose after their channel went, {Detach} stuck, "
                      + "{WrongBus} send drop(s) on the wrong bus",
                        cpu.dsp, cpu.update, cpu.stream, voices, _activeSounds.Count, real, playing,
                        noHrtf, _saPoolMisses - _lastSaPoolMisses, SpatialVoicesFree,
                        starves - _lastStarves, gen2 - _lastGen2, pauseMs - _lastPauseMs,
                        _lateDetaches - _lastLateDetaches, _failedDetaches - _lastFailedDetaches,
                        _sendDropsOnWrongBus);
        _lastSaPoolMisses = _saPoolMisses; _lastFailedDetaches = _failedDetaches;
        _lastLateDetaches = _lateDetaches;
        _lastStarves = starves; _lastGen2 = gen2; _lastPauseMs = pauseMs;

        // A DSP callback cannot log (DspFault): a faulting unit says so here, on a thread that may block.
        if (DspFault.TryDrain(out int dspFaults, out string? dspFirst))
            Log.Error("A DSP callback faulted {Count} time(s) since the last report; the block(s) were "
                    + "silenced rather than taking the process down. First: {First}", dspFaults, dspFirst);

        // What the room is doing to everything, every report. Measurements only: a constant stated as
        // one is worse than nothing.
        Log.Information("Room: listener in region {Region}; ray-traced RT60 {Sim:F0} ms; enclosure {Enc:P0}; "
                      + "reflections {Refl:F0} dB",
                        _listenerRegionId, _simReverbDecayMs, _listenerEnclosure, TailDb);

        // One simulation step and a margin: older than that, something is holding a source still.
        const double AcceptablePositionAgeSeconds = 0.06;
        if (worstStale > AcceptablePositionAgeSeconds)
            Log.Warning("Voice {Id} was placed at a position {Stale:F0} ms old (worst this interval) — its sound "
                      + "is sitting still while the thing making it moves on. {Voices} engine/echo voice(s) live.",
                        worstId, worstStale * 1000.0, voices);



        // The mix after the makeup and the brick wall; short-term is the last three seconds. -18 to -23
        // LUFS is where a game mix belongs; far under it, the makeup is too low for this content.
        if (!_loudnessMeter.hasHandle()) return;
        if (_loudnessMeter.getParameterData(2, out IntPtr data, out uint _) != RESULT.OK) return;
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<DSP_LOUDNESS_METER_INFO_TYPE>(data);
        if (float.IsFinite(info.shorttermloudness) && info.shorttermloudness > -200f)
            Log.Information("Mix loudness: {Short:F1} LUFS short-term, {Momentary:F1} momentary, "
                          + "{Peak:F1} dBFS peak (makeup {Makeup:F0} dB)",
                            info.shorttermloudness, info.momentaryloudness, info.maxtruepeak, MasterMakeupDb);
        // FMOD's peak is the maximum since metering began (320 of 391 lines on 10-06 read -0.0 dBFS):
        // reset, it is the interval's.
        _loudnessMeter.setParameterInt(0, (int)DSP_LOUDNESS_METER_STATE_TYPE.RESET_MAXPEAK);
        _loudnessMeter.setParameterInt(0, (int)DSP_LOUDNESS_METER_STATE_TYPE.ANALYZING);
        // What the brick wall did in the interval: nothing, most of the time.
        if (_trueLimiter != null)
        {
            float gr = _trueLimiter.Core.TakeMaxReductionDb();
            if (gr >= 0.1f) Log.Information("Master limiter: up to {Gr:F1} dB of gain reduction this interval.", gr);
        }
    }

    private readonly System.Diagnostics.Stopwatch _cpuClock = System.Diagnostics.Stopwatch.StartNew();
    private int _lastStarves, _lastGen2;
    private double _lastPauseMs;

    // How long Update takes and how often it runs: whether a pass-by glides or steps. Doppler is a
    // channel pitch, so the 250 Hz loop's period is every pass-by's resolution (0.65 % steps; 60 Hz is
    // 2.7 %, a staircase), and the period is an outcome of the tick, which walks every voice.
    private double _updateMsSum, _updateMsMax;
    private int _updateCount;
    private readonly System.Diagnostics.Stopwatch _updateTimer = new();

    /// <summary>Mean and worst time one attribute pass took since the last read, and the voices it
    /// walked. Reading resets the window; the caller owns the cadence.</summary>
    public (double MeanMs, double MaxMs, int Calls, int Voices) TakeUpdateCost()
    {
        var r = (_updateCount > 0 ? _updateMsSum / _updateCount : 0.0, _updateMsMax, _updateCount, _activeSounds.Count);
        _updateMsSum = 0; _updateMsMax = 0; _updateCount = 0;
        return r;
    }

    public void Update()
    {
        if (!_isInitialized) return;
        // Any unit whose block was not finite and was zeroed (NonFinite), named here, once each.
        NonFinite.Drain(static (kind, name, region) => Log.Warning("{Line}", NonFinite.Line(kind, name, region)));

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _attributeDt = _attributeTickAt == 0 ? 0.004f
            : Math.Clamp((now - _attributeTickAt) / (float)System.Diagnostics.Stopwatch.Frequency, 0f, 0.25f);
        _attributeTickAt = now;
        StepOverload(OpenFPS.Common.AudioClock.Now, _attributeDt);

        _updateTimer.Restart();
        ReportMixerLoad();

        DrainDeferredPlays();

        try
        {
            Vector3 lPosVec = _listenerPos;
            int listenerRegionId = _listenerRegionId;

            lock (_lock)
            {
                _dbgFrame++;
                UpdateActiveReverbs(lPosVec);
                UpdateTracedStages();
                UpdateTracedEchoes();
                UpdateLateField();
                ApplySimulatedReverb();

                for (int i = _activeSounds.Count - 1; i >= 0; i--)
                {
                    var active = _activeSounds[i];
                    active.Channel.isPlaying(out bool isPlaying);
                    if (!isPlaying) { 
                        ReleaseActiveSoundResources(active);
                        RemoveActiveAt(i); continue; 
                    }
                    
                    if (active.Type == EmitterType.UI)
                    {
                        PlaceInHead(active.Channel, active.FollowsListener ? active.ListenerOffset : active.Position - _listenerPos);
                        continue;
                    }

                    UpdateReverbRouting(active, listenerRegionId);
                    UpdateSpatialPositioning(active, lPosVec);
                    ApplyAcousticFilters(active, lPosVec);
                }

                UpdateReverbBuses(lPosVec, listenerRegionId);
                ReportEar();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in FmodAudioProvider.Update");
        }
        finally
        {
            _system.update();
            double ms = _updateTimer.Elapsed.TotalMilliseconds;
            _updateMsSum += ms; _updateCount++;
            if (ms > _updateMsMax) _updateMsMax = ms;
        }
    }

    /// <summary>
    /// Takes one voice apart: every DSP off the live channel, then the channel stopped, then the
    /// voice's own DSP disconnected and released. The reaper releases on <c>isPlaying == false</c>,
    /// which for a <c>playDSP</c> voice (engine, machine, echo, tap) can read false while the mixer
    /// still holds the DSP: released without the channel stopped, it was the city's SIGSEGV at 0x7c on
    /// FMOD's mixer thread. See docs/THE_MIXER_THREAD_CRASH.md.
    /// </summary>
    private void ReleaseActiveSoundResources(ActiveSound active)
    {
        // Detach, then stop: a stopped channel is recycled at once and removeDSP on it fails, leaving
        // the DSP attached. removeDSP also waits out a callback in flight, so it comes first.
        DetachEchoRig(active);
        ReleaseSteamAudioVoice(active);
        ReleaseThreeEqDsp(active.Channel, active.ThreeEqDsp);
        ReleaseDiffractionDsp(active.Channel, active.DiffractionDsp);
        ReleaseSendTapDsp(active);
        ReleaseEar(active);
        // The owned units too: FMOD refuses to release an attached unit ("Failed to release because
        // unit is still attached", logging build only) and it stays in the graph. Attachment is not a
        // connection, so disconnectAll does not undo it; only removeDSP or stopping the channel does.
        Detach(active.Channel, active.GranularDsp, "granular");
        Detach(active.Channel, active.SynthDsp, "synth");
        Detach(active.Channel, active.EngineDsp, "engine/machine");
        if (active.Channel.hasHandle()) active.Channel.stop();
        if (active.OwnedSound.hasHandle()) { active.OwnedSound.release(); active.OwnedSound = default; }
        ReleaseGranularDsp(active.GranularDsp, active.GranularHandle, active.GranularState);
        ReleaseSynthDsp(active.SynthDsp, active.SynthHandle, active.SynthState);
        if (active.EngineDsp.hasHandle())
        {
            // Not pooled: the next engine is a different car. The GCHandle is not freed: a callback
            // arriving after release resolves it first, and a freed one aborted the runtime on the
            // mixer thread (no exception, signal 11). Userdata cleared so a late callback leaves; the
            // handle parked until Dispose (_retiredHandles).
            active.EngineDsp.setUserData(IntPtr.Zero);
            // Out of the graph before release: a connected DSP released was the null at 0x7c.
            active.EngineDsp.disconnectAll(true, true);
            active.EngineDsp.release();
            if (active.EngineHandle.IsAllocated) _retiredHandles.Add(active.EngineHandle);
            active.EngineDsp = default;
            active.EngineState = null;
            active.MachineState = null;
            active.OwnVoice = null;
            active.EchoState = null;
            // A second outlet gone: its share slews back into the engine's voice, or the car lost a
            // third of its sound for being far enough away to be one voice.
            active.TapState?.HandBack();
            active.TapState = null;
        }
        active.Channel.clearHandle();
    }

    /// <summary>How much of a send's crossfade happens per audio update: a few tens of milliseconds at
    /// the update rate, under anything heard as a change of place.</summary>
    private const float ReverbSendFadeStep = 0.12f;

    private void UpdateReverbRouting(ActiveSound active, int listenerRegionId)
    {
        if (_acousticMap == null) return;
        int sourceRegionId = active.TargetRegionId;

        // The cross-send lets a sound in the next room ring a little here too; a sound already in the
        // listener's room would be sent to the same bus twice.
        int crossRegionId = sourceRegionId == listenerRegionId
            ? AcousticConstants.GlobalRegionId : listenerRegionId;

        bool sourceChanged = sourceRegionId != active.CurrentSourceRegionId || !active.SourceReverbConnection.hasHandle();
        bool listenerChanged = crossRegionId != active.CurrentRegionId || !active.ReverbConnection.hasHandle();

        // A crossfade still running is advanced even when nothing changed this frame.
        bool fading = active.FadingReverbConnection.hasHandle() || active.FadingSourceConnection.hasHandle()
                   || active.ReverbMix < 1f || active.SourceReverbMix < 1f;
        if (!sourceChanged && !listenerChanged && !fading) return;

        active.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var sourceFader);
        var sourceTap = SourceSendDsp(active);

        // The send into the room the sound is in.
        if (sourceChanged)
        {
            // A second change before the first fade finished drops the old one outright.
            DropSend(ref active.FadingSourceConnection, active.FadingSourceBus, sourceTap);
            // It fades out of the unit stored with the connection, never one looked up by region id:
            // the recorded id is a change detector and can differ from the bus (see DropSend).
            if (active.SourceReverbConnection.hasHandle() && active.SourceReverbBus.hasHandle())
            {
                active.FadingSourceConnection = active.SourceReverbConnection;
                active.FadingSourceBus = active.SourceReverbBus;
                active.FadingSourceMix = active.SourceReverbMix;
            }
            active.SourceReverbConnection = default;
            active.SourceReverbBus = default;
            active.SourceReverbMix = 0f;

            if (sourceRegionId != -2 && !active.IsReflection && TryGetReverbInput(sourceRegionId, out var sourceReverb))
            {
                sourceReverb.addInput(sourceTap, out active.SourceReverbConnection, DSPCONNECTION_TYPE.SEND);
                active.SourceReverbBus = sourceReverb;
                active.SourceReverbConnection.setMix(0f);   // in at nothing, then ramped
            }
            active.CurrentSourceRegionId = sourceRegionId;
        }

        // The send into the room the listener is in.
        if (listenerChanged)
        {
            DropSend(ref active.FadingReverbConnection, active.FadingReverbBus, sourceFader);
            // Where the city crashed: CurrentRegionId holds the global id for a source in the
            // listener's own room, which on the city is the outdoor bus, while the send went into the
            // room's unit. The stored handle owns the connection by construction.
            if (active.ReverbConnection.hasHandle() && active.ReverbBus.hasHandle())
            {
                active.FadingReverbConnection = active.ReverbConnection;
                active.FadingReverbBus = active.ReverbBus;
                active.FadingReverbMix = active.ReverbMix;
            }
            active.ReverbConnection = default;
            active.ReverbBus = default;
            active.ReverbMix = 0f;

            if (listenerRegionId != -2 && !active.IsReflection && TryGetReverbInput(listenerRegionId, out var listenerReverb))
            {
                listenerReverb.addInput(sourceFader, out active.ReverbConnection, DSPCONNECTION_TYPE.SEND);
                active.ReverbBus = listenerReverb;
                active.ReverbConnection.setMix(0f);
            }
            active.CurrentRegionId = crossRegionId;
        }

        AdvanceSendFade(active, sourceFader, sourceTap);
    }

    /// <summary>Moves both sends one step along their crossfade and drops a connection that has faded
    /// out. The mixes are fractions of each send's own target level.</summary>
    private void AdvanceSendFade(ActiveSound active, FMOD.DSP sourceFader, FMOD.DSP sourceTap)
    {
        float listenerTarget = AcousticConstants.ReverbSendMix * AcousticConstants.ReverbCrossSendScale;
        float sourceTarget = AcousticConstants.ReverbSendMix;

        // The live sends' fractions only: their mix has one writer, the per-source pass. Two writers
        // lost the ramp.
        if (active.SourceReverbConnection.hasHandle() && active.SourceReverbMix < 1f)
            active.SourceReverbMix = MathF.Min(1f, active.SourceReverbMix + ReverbSendFadeStep);
        if (active.ReverbConnection.hasHandle() && active.ReverbMix < 1f)
            active.ReverbMix = MathF.Min(1f, active.ReverbMix + ReverbSendFadeStep);

        if (active.FadingSourceConnection.hasHandle())
        {
            active.FadingSourceMix -= ReverbSendFadeStep;
            if (active.FadingSourceMix <= 0f) DropSend(ref active.FadingSourceConnection, active.FadingSourceBus, sourceTap);
            else active.FadingSourceConnection.setMix(sourceTarget * active.FadingSourceMix * SourceSendLevel(active));
        }
        if (active.FadingReverbConnection.hasHandle())
        {
            active.FadingReverbMix -= ReverbSendFadeStep;
            if (active.FadingReverbMix <= 0f) DropSend(ref active.FadingReverbConnection, active.FadingReverbBus, sourceFader);
            else active.FadingReverbConnection.setMix(listenerTarget * active.FadingReverbMix);
        }
    }

    /// <summary>
    /// Longest a voice's position may be carried forward on its own velocity, seconds. A position is
    /// legitimately over 50 ms old before a newer one exists (a 33 ms step, re-offered every 22 ms), and
    /// a shorter cap is a freeze. Not to be stretched until a fault goes quiet: a source that stops dead
    /// is flung at most a couple of metres, and anything older is a stall the worst-age line reports.
    /// </summary>
    private const double MaxDeadReckonSeconds = 0.08;

    /// <summary>Degrees a second the direction of a sound heard round an obstacle may turn.</summary>
    private const float BlockedTurnDegPerSecond = 120f;
    /// <summary>...and of one in the clear, which only has to keep up with a car passing close.</summary>
    private const float ClearTurnDegPerSecond = 1500f;

    /// <summary>
    /// Moves where a voice is heard from toward where it should be as a direction and a distance, not
    /// in a straight line. A route round a building can switch sides between two updates; eased in a
    /// line, the heard point swept through the head, swapping ears with 5-10 dB lumps two to five times
    /// a second (siren and horn "flutter"). At a limited turn rate a switch back hardly moves it.
    /// </summary>
    private static Vector3 TurnToward(ActiveSound active, Vector3 listener, Vector3 target, double now)
    {
        float dt = active.LastTurnAt > 0 ? (float)Math.Clamp(now - active.LastTurnAt, 0.0, 0.1) : 0.1f;
        active.LastTurnAt = now;
        Vector3 cur = active.CurrentApparentPosition - listener, tgt = target - listener;
        float dc = cur.Length(), dtg = tgt.Length();
        if (dc < 0.05f || dtg < 0.05f) return Vector3.Lerp(active.CurrentApparentPosition, target, 0.15f);
        Vector3 uc = cur / dc, ut = tgt / dtg;
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(uc, ut), -1f, 1f));
        bool blocked = active.ApparentPosition != Vector3.Zero && active.ApparentPosition != active.Position;
        float maxTurn = (blocked ? BlockedTurnDegPerSecond : ClearTurnDegPerSecond) * (MathF.PI / 180f) * dt;
        Vector3 dir = ut;
        if (angle > maxTurn && angle > 1e-4f)
        {
            // Great-circle step of maxTurn from uc toward ut; straight opposite, round the listener's side.
            Vector3 axis = Vector3.Cross(uc, ut);
            if (axis.LengthSquared() < 1e-8f) axis = MathF.Abs(uc.Y) < 0.9f ? Vector3.Cross(uc, Vector3.UnitY) : Vector3.Cross(uc, Vector3.UnitX);
            dir = Vector3.Transform(uc, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), maxTurn));
        }
        // Distance eased over about 30 ms at the update rate; it does not set the level.
        float d = dc + (dtg - dc) * MathF.Min(1f, dt / 0.03f);
        return listener + Vector3.Normalize(dir) * d;
    }

    /// <summary>
    /// Where a source is now, carried forward on its velocity from where the game thread last said.
    /// Doppler is recomputed at 250 Hz from a position that changes at about 45 Hz, so a close pass
    /// sounded "auto-tuned": the jump goes as v²/d, 0.8 % at 50 m and 280 km/h but 7.6 % at 5 m, a
    /// pitch quantiser. See docs/AUDIO_LOAD_DROPOUTS.md.
    /// </summary>
    private Vector3 DeadReckon(ActiveSound active, Vector3 position, double nowSec)
    {
        if (active.LastAttributeAt <= 0 || active.Velocity == Vector3.Zero) return position;
        double age = nowSec - active.LastAttributeAt;
        if (age <= 0) return position;
        if (age > MaxDeadReckonSeconds) age = MaxDeadReckonSeconds;
        return position + active.Velocity * (float)age;
    }

    /// <summary>
    /// OPENFPS_AUDIO_TRACE=entity id: that voice traced at the full attribute rate, as [ATRACE] CSV
    /// (time, placed position, bearing, distance, Doppler, position age, occlusion). A hold under 33 ms
    /// is invisible to every sampled instrument; here a staircase is eight identical rows and a jump.
    /// </summary>
    private static readonly int _traceEntity =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_AUDIO_TRACE"), out int t) ? t : int.MinValue;

    private void UpdateSpatialPositioning(ActiveSound active, Vector3 lPosVec)
    {
        double now = OpenFPS.Common.AudioClock.Now;
        // A voice pinned to the listener has no position to go stale (see ReportMixerLoad).
        double positionAge = active.FollowsListener || active.LastAttributeAt <= 0
            ? 0 : now - active.LastAttributeAt;
        if (positionAge > active.WorstPositionAge) active.WorstPositionAge = positionAge;

        Vector3 targetPos = (active.ApparentPosition != Vector3.Zero) ? active.ApparentPosition : active.Position;
        // A reflection carries no velocity (EngineEchoState), so this leaves it where it is.
        targetPos = DeadReckon(active, targetPos, now);
        if (active.ApparentPosition != Vector3.Zero && active.ApparentPosition != active.Position)
        {
            float bias = Math.Clamp(active.CurrentBleed / (active.CurrentBleed + active.CurrentAperture + 0.001f), 0f, 1f);
            targetPos = Vector3.Lerp(active.ApparentPosition, active.Position, bias);
        }

        // Part of the listener: under their head this tick, not eased (the ease is for sounds in the world).
        if (active.FollowsListener)
        {
            targetPos = lPosVec + active.ListenerOffset;
            active.Position = targetPos; active.ApparentPosition = targetPos;
            active.CurrentApparentPosition = targetPos;
        }
        else active.CurrentApparentPosition = TurnToward(active, lPosVec, targetPos, now);

        if (active.EntityId == _traceEntity)
        {
            Vector3 rel = active.CurrentApparentPosition - lPosVec;
            float bearing = MathF.Atan2(rel.X, rel.Z) * (180f / MathF.PI);
            float doppler = OpenFPS.Client.AudioEngine.Core.AudioPhysics.DopplerFactor(
                lPosVec, _listenerVel, DeadReckon(active, active.Position, now), active.Velocity, speedOfSound: _speedOfSound);
            Console.WriteLine($"[ATRACE],{now:F4},{active.EntityId},{active.CurrentApparentPosition.X:F3},"
                            + $"{active.CurrentApparentPosition.Y:F3},{active.CurrentApparentPosition.Z:F3},"
                            + $"{bearing:F2},{rel.Length():F3},{doppler:F5},{positionAge * 1000.0:F1},{active.CurrentOcclusion:F3}");
        }

        if (active.SaState != null)
        {
            // The HRTF places it (the channel is 2D; distance is applied in ApplyAcousticFilters).
            // Listener-relative, from the game's +Z forward to Steam Audio's -Z.
            Vector3 local = Vector3.Transform(active.CurrentApparentPosition - lPosVec, Quaternion.Conjugate(_listenerRot));
            float len = local.Length();
            if (len > 1e-4f)
            {
                active.SaState.DirX = local.X / len;
                active.SaState.DirY = local.Y / len;
                active.SaState.DirZ = -local.Z / len;
                // A path into the cabin you sit in: its HRTF colouring taken back to the one voice's.
                if (active.EngineState is { Interior: true, CabinLayout: not null } || active.TapState is { CabinPath: > 0 })
                    CabinTrim(active, new Vector3(active.SaState.DirX, active.SaState.DirY, active.SaState.DirZ));
            }
            // The ground's image: the placed source mirrored in the surface it bounces off.
            if (active.GroundHeight is float gh)
            {
                var at = active.CurrentApparentPosition;
                var image = new Vector3(at.X, 2f * gh - at.Y, at.Z);
                Vector3 gl = Vector3.Transform(image - lPosVec, Quaternion.Conjugate(_listenerRot));
                float glen = gl.Length();
                if (glen > 1e-4f)
                {
                    active.SaState.GroundDirX = gl.X / glen;
                    active.SaState.GroundDirY = gl.Y / glen;
                    active.SaState.GroundDirZ = -gl.Z / glen;
                }
            }

            if (_audioDebug && !active.IsReflection && _dbgFrame % 60 == 0)
            {
                Vector3 fwd = Vector3.Transform(Vector3.UnitZ, _listenerRot);
                float yawDeg = MathF.Atan2(fwd.X, fwd.Z) * 180f / MathF.PI;
                Log.Information("[ADBG] e{Id} {Sound} dist={D:F1} occ={Occ:F2} eqLMH=({EL:F2},{EM:F2},{EH:F2}) dir=({X:F2},{Y:F2},{Z:F2}) listenerYaw={Yaw:F0} L/R={L:F3}/{R:F3}",
                    active.EntityId, active.SoundId, len, active.CurrentOcclusion,
                    active.CurrentLow, active.CurrentMid, active.CurrentHigh,
                    active.SaState.DirX, active.SaState.DirY, active.SaState.DirZ, yawDeg, active.SaState.LastRmsL, active.SaState.LastRmsR);
            }
            return;
        }

        // No Steam Audio: FMOD pans and rolls off the mono point source.
        FMOD.VECTOR fpos = FmodHelpers.ToFmodVec(active.CurrentApparentPosition), fvel = FmodHelpers.ToFmodVec(active.Velocity);
        active.Channel.set3DAttributes(ref fpos, ref fvel);
        active.Channel.set3DMinMaxDistance(active.MinDistance, active.Range);

        float distToSound = Vector3.Distance(lPosVec, active.CurrentApparentPosition);
        bool isIndirect = active.ApparentPosition != Vector3.Zero && active.ApparentPosition != active.Position;

        if (isIndirect)
        {
            // A diffracted path spreads with the aperture.
            float roomFillSpread = Math.Clamp((distToSound * AcousticConstants.SpreadGrowthFactor), 0.0f, AcousticConstants.VolumetricSpreadMax);
            float baseSpread = (active.CurrentAperture * 90.0f) / Math.Max(0.5f, distToSound);
            active.Channel.set3DSpread(Math.Min(baseSpread, roomFillSpread));
        }
        else if (active.IsReflection)
        {
            active.Channel.set3DSpread(active.ReflectionSpread);
        }
        else
        {
            active.Channel.set3DSpread(0.0f);
        }
        active.Channel.set3DLevel(1.0f);
    }

    /// <summary>The EQ a path asks for, dB per band: each linear band gain (the whole of the path's
    /// blocking) as a level, less what the air took. See ApplyAcousticFilters.</summary>
    internal static (float Low, float Mid, float High) PathEqDb(float gainLow, float gainMid, float gainHigh,
                                                               float airLowDb, float airMidDb, float airHighDb)
    {
        static float Db(float gain) => 20f * MathF.Log10(MathF.Max(1e-4f, gain));
        return (Db(gainLow) - airLowDb, Db(gainMid) - airMidDb, Db(gainHigh) - airHighDb);
    }

    // Seconds since the previous attribute pass: the loop's real period, which varies with load.
    private long _attributeTickAt;
    private float _attributeDt;

    private void ApplyAcousticFilters(ActiveSound active, Vector3 lPosVec)
    {
        float dt = _attributeDt;
        float lerpFactor = 1.0f - MathF.Exp(-dt / AcousticConstants.ParameterSmoothingTimeConstant); 
        
        active.CurrentOcclusion = MathHelper.Lerp(active.CurrentOcclusion, active.TargetOcclusion, lerpFactor);
        active.CurrentAperture = MathHelper.Lerp(active.CurrentAperture, active.TargetAperture, lerpFactor);
        active.CurrentBleed = MathHelper.Lerp(active.CurrentBleed, active.TargetBleed, lerpFactor);
        active.CurrentLow = MathHelper.Lerp(active.CurrentLow, active.TargetLow, lerpFactor);
        active.CurrentMid = MathHelper.Lerp(active.CurrentMid, active.TargetMid, lerpFactor);
        active.CurrentHigh = MathHelper.Lerp(active.CurrentHigh, active.TargetHigh, lerpFactor);

        // The path is applied once: the band gains are the whole of what occlusion, transmission and
        // diffraction do, as 20 log10 each in the EQ below, with the air's per-band loss. Applied
        // three times over (a broadband 1 - occlusion, the gains read as dB weights, an extra
        // occlusion cut) a gain of 0.2 came out at -32 dB in the high band.
        float finalVolFactor = 1.0f;

        // Atmospheric sounds (wind, rain) are damped under shelter, keeping 5 % for "interior rain";
        // world sounds are left to occlusion and the geometry.
        if (active.Type == EmitterType.Atmospheric)
        {
            finalVolFactor *= (1.0f - (_shelterFactor * 0.95f));
        }

        float roomGainBonus = MathHelper.Lerp(1.0f, active.RoomGain, 0.5f);

        // A 2D binaural channel gets no rolloff from FMOD, so the law is applied here.
        float distAtten = 1.0f;
        if (active.SaState != null)
        {
            // One law, in Loudness, so a test can check a balance without a sound card. From where the
            // source is, not where it is heard from: round a building the heard point is a direction.
            distAtten = Loudness.RenderedGain(1.0f, active.MinDistance, active.Range,
                                              Vector3.Distance(lPosVec, active.Position));
        }

        // The cone by hand on a 2D binaural channel (a 3D fallback channel uses FMOD's): full inside
        // the inner cone, ConeOutsideVolume past the outer, smooth between.
        float coneAtten = 1.0f;
        float coneOffAxis = 0.0f; // 0 = on-axis, 1 = fully outside the cone (drives the off-axis timbre)
        if (active.SaState != null && active.ConeInside < 360f && active.Direction != Vector3.Zero)
        {
            // Where the horn points is a fact about the horn, not about the route the sound took.
            Vector3 toListener = lPosVec - active.Position;
            if (toListener.LengthSquared() > 1e-6f)
            {
                float cos = Vector3.Dot(Vector3.Normalize(active.Direction), Vector3.Normalize(toListener));
                float offAxisDeg = MathF.Acos(Math.Clamp(cos, -1f, 1f)) * (180f / MathF.PI);
                float innerHalf = active.ConeInside * 0.5f;
                float outerHalf = MathF.Max(active.ConeOutside * 0.5f, innerHalf + 0.01f);
                coneOffAxis = offAxisDeg <= innerHalf ? 0.0f
                    : offAxisDeg >= outerHalf ? 1.0f
                    : (offAxisDeg - innerHalf) / (outerHalf - innerHalf);
                coneAtten = MathHelper.Lerp(1.0f, active.ConeOutsideVolume, coneOffAxis);
            }
        }

        // The budget's fade, here because this pass owns the voice's gain: long enough that no step
        // survives it, short enough that a voice gone does not linger.
        float fadeStep = dt / VoiceFadeSeconds;
        active.FadeGain += Math.Clamp(active.FadeTarget - active.FadeGain, -fadeStep, fadeStep);

        active.LastVolume = active.BaseVolume * finalVolFactor * roomGainBonus * distAtten * coneAtten
                            * active.FadeGain * (active.OverloadExempt || active.IsReflection ? 1f : _overloadGain)
                            * EarGain(active, lPosVec, dt);   // the law in loudness units (FmodAudioProvider.Ear.cs)
        active.Channel.setVolume(active.LastVolume);

        // Doppler by hand on a 2D binaural channel (a 3D fallback voice gets FMOD's), from the real
        // source position carried forward to now (DeadReckon), not the apparent one.
        if (active.SaState != null)
        {
            Vector3 sourceNow = DeadReckon(active, active.Position, OpenFPS.Common.AudioClock.Now);
            float doppler = OpenFPS.Client.AudioEngine.Core.AudioPhysics.DopplerFactor(
                lPosVec, _listenerVel, sourceNow, active.Velocity, speedOfSound: _speedOfSound);
            float basePitch = (active.GranularState != null || active.SynthState != null || active.EngineState != null || active.EchoState != null) ? 1.0f : active.Pitch;

            // An engine reflection reads the source's ring behind its play cursor, so the read rate and
            // the slewing delay already carry its whole Doppler; a channel pitch as well counted the
            // listener's motion twice. The vehicle you sit in and its cabin paths ride with you: no
            // Doppler at all (a pitched channel is called an extra block now and then, and the cabin
            // taps would fall a block out of step with the voice).
            if (active.EngineState is { Interior: true } || active.TapState is { CabinPath: > 0 }) doppler = 1f;
            if (active.EchoState != null && active.IsReflection) active.Channel.setPitch(1.0f);
            else active.Channel.setPitch(basePitch * doppler);
            // What reads this voice in step is told its rate: its play position moves in whole blocks
            // (EngineVoiceState.ConsumeRate).
            if (active.EngineState != null) active.EngineState.ConsumeRate = basePitch * doppler;
            if (active.TapState != null) active.TapState.ChannelRate = basePitch * doppler;
            // The channel's clock against its parent's, once started: puts the vehicle you sit in and
            // its cabin taps on one time line (EngineVoiceState.BlockAt).
            if ((active.EngineState is { CabinLayout: not null, Interior: true } || active.TapState is { CabinPath: > 0 })
                && active.Channel.getDSPClock(out ulong own, out ulong parent) == RESULT.OK && own > 0)
            {
                long offset = (long)parent - (long)own;
                if (active.EngineState != null) active.EngineState.ChannelClockOffset = offset;
                else active.TapState!.ChannelClockOffset = offset;
            }
        }

        if (active.ThreeEqDsp.hasHandle())
        {
            var (lowDb, midDb, highDb) = PathEqDb(active.CurrentLow, active.CurrentMid, active.CurrentHigh,
                                                  active.AirLowDb, active.AirMidDb, active.AirHighDb);

            // Sitting in a car, everything outside it comes through the glass and the doors: not the
            // car's own engine (its voice rendered the body already), not the cues on your head.
            if (!active.FollowsListener && !active.InsideListenersVehicle && active.EngineState is not { Interior: true })
            {
                lowDb += _enclosureLowDb; midDb += _enclosureMidDb; highDb += _enclosureHighDb;
            }

            // Off-axis a projecting source (a megaphone) loses its highs, then its mids: dull to the
            // sides, not only quieter.
            if (coneOffAxis > 0f)
            {
                highDb -= coneOffAxis * 36.0f;
                midDb -= coneOffAxis * 14.0f;
            }

            active.ThreeEqDsp.setParameterFloat(0, Math.Clamp(lowDb, -80.0f, 10.0f));
            active.ThreeEqDsp.setParameterFloat(1, Math.Clamp(midDb, -80.0f, 10.0f));
            active.ThreeEqDsp.setParameterFloat(2, Math.Clamp(highDb, -80.0f, 10.0f));
            active.LastEqDb = (Math.Clamp(lowDb, -80f, 10f), Math.Clamp(midDb, -80f, 10f), Math.Clamp(highDb, -80f, 10f));
        }

        WatchForPops(active, lPosVec);

        float sourceDist = Vector3.Distance(lPosVec, active.Position);
        // A room rings with what the source radiates, not what its cone lets through to you.
        float radiated = 1f / MathF.Max(coneAtten, 0.05f);
        // The sends carry the source to its room's traced stage at unity: the trace is the room's
        // answer to a source at a metre. In the listener's own room the send is scaled by the distance
        // the direct sound has already fallen over (LateSend); through a doorway the other room's stage
        // is fed as it is. A copy sends nothing, being the room answering already, and nothing is sent
        // until a stage exists.
        float d = MathF.Max(0.1f, sourceDist);
        bool here = active.TargetRegionId == _listenerRegionId;
        float atDistance = here ? LateSend(active, d)
                                : MathF.Min(d, 1f) * MathF.Pow(MathF.Max(d, 1f), Math.Clamp(_listenerEnclosure, 0f, 1f));
        // The cabin paths feed your room at the interior voice's own send, all alike: each traced for
        // itself, they came out a decibel apart, and the cabin's boom with them.
        if (here && active.EngineState is { Interior: true, CabinLayout: not null } inside) inside.CabinRoomSend = atDistance;
        else if (here && active.TapState is { CabinPath: > 0 } cabinTap && cabinTap.Source.CabinRoomSend is float shared && shared >= 0f)
            atDistance = shared;
        float ownMix = active.IsReflection || !_traced.ContainsKey(active.TargetRegionId) ? 0f : (here ? atDistance : 1f);
        float crossMix = active.IsReflection || !_traced.ContainsKey(_listenerRegionId) ? 0f : 1f;
        if (active.SourceReverbConnection.hasHandle() && active.ReverbConnection.hasHandle()
            && active.SourceReverbBus.handle == active.ReverbBus.handle)
            crossMix = 0f;
        // A source traced from where it is carries its whole reverberation in its own IR.
        if (active.EchoRig != null) { ownMix *= 1f - active.EchoWeight; crossMix *= 1f - active.EchoWeight; }
        if (active.SourceReverbConnection.hasHandle())
            active.SourceReverbConnection.setMix(ownMix * radiated * active.SourceReverbMix * SourceSendLevel(active));
        if (active.ReverbConnection.hasHandle())
            active.ReverbConnection.setMix(crossMix * radiated * active.ReverbMix);

        if (active.DiffractionDsp.hasHandle())
        {
            if (active.CurrentAperture > 0.98f) active.DiffractionDsp.setBypass(true);
            else
            {
                active.DiffractionDsp.setBypass(false);
                float diffractionCutoff = Math.Max(400.0f, 22000.0f * MathF.Pow(active.CurrentAperture, 2.0f));
                active.DiffractionDsp.setParameterFloat(0, diffractionCutoff);
            }
        }
    }

    /// <summary>How many region reverb buses may be audible at once; the rest fade to silence.</summary>
    private const int MaxActiveReverbBuses = 4;

    /// <summary>Each room's reverberation time as last measured from inside it, seconds.</summary>
    private readonly Dictionary<int, float> _regionDecaySeconds = new();
    /// <summary>A room never measured from inside decays as a middling room does.</summary>
    private const float DefaultRoomDecaySeconds = 1.5f;
    /// <summary>How fast a room's bus comes up as you enter it or its doorway opens to you.</summary>
    private const float ReverbRiseSeconds = 0.1f;

    private void UpdateReverbBuses(Vector3 lPosVec, int listenerRegionId)
    {
        if (_acousticMap == null) return;
        
        // The listener's room first, then the nearest; every other bus fades out.
        var candidates = _activeRegionIds
            .Select(id => new { 
                Id = id, 
                Dist = (id == _acousticMap.GlobalEnvironmentId) ? 0 : Vector3.Distance(lPosVec, _acousticMap.RegionPositions.GetValueOrDefault(id, Vector3.Zero)),
                Priority = (id == listenerRegionId) ? 0 : 1
            })
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Dist)
            .Take(MaxActiveReverbBuses)
            .ToList();

        HashSet<int> candidateIds = candidates.Select(c => c.Id).ToHashSet();

        foreach (var kvp in _reverbBuses)
        {
            int regionId = kvp.Key;
            var bus = kvp.Value;
            float targetVol = 0.0f;

            if (candidateIds.Contains(regionId))
            {
                if (regionId == listenerRegionId)
                {
                    targetVol = 1.0f;
                    SetReverbDirection(regionId, bus, default, outside: false, lPosVec);
                }
                else if (_routesSource?.Invoke() is { } routes
                         && routes.NodeOf(regionId) != routes.NodeOf(listenerRegionId))
                {
                    if (FieldHere(routes, regionId, lPosVec, listenerRegionId, out float gain, out Vector3 via))
                    {
                        targetVol = gain;
                        SetReverbDirection(regionId, bus, via, outside: true, lPosVec);
                    }
                }
                else
                {
                    // Leakage through portals, for a map with no openings yet and for two outdoor places,
                    // which share the one unbounded node.
                    var portals = _acousticMap.Portals.Values.Where(p =>
                        (p.Portal.RegionAId == regionId && p.Portal.RegionBId == listenerRegionId) ||
                        (p.Portal.RegionAId == listenerRegionId && p.Portal.RegionBId == regionId) ||
                        (!IsEnclosure(listenerRegionId) && (p.Portal.RegionAId == regionId || p.Portal.RegionBId == regionId))
                    );

                    var nearest = portals.OrderBy(p => Vector3.Distance(lPosVec, p.Position)).FirstOrDefault();
                    if (nearest.Portal.ApertureSize > 0)
                    {
                        float dist = Vector3.Distance(lPosVec, nearest.Position);
                        targetVol = Math.Clamp((nearest.Portal.ApertureSize / 2.0f) / Math.Max(1.0f, dist), 0.0f, 1.0f);
                        SetReverbDirection(regionId, bus, nearest.Position, outside: true, lPosVec);
                    }
                }
            }

            // In seconds, not per pass (0.15 a pass was 25 ms, cutting a room's ring off as you left).
            // A falling bus follows that room's own decay (-60 dB over the RT60 measured while you were
            // in it); a rising one takes a tenth of a second.
            if (regionId == listenerRegionId && _simReverbDecayMs > 0f)
                _regionDecaySeconds[regionId] = _simReverbDecayMs / 1000f;
            float current = _reverbVolumes[regionId];
            float tau = targetVol >= current
                ? ReverbRiseSeconds
                : _regionDecaySeconds.GetValueOrDefault(regionId, DefaultRoomDecaySeconds) / 6.91f;
            float dtBus = _attributeDt > 0f ? _attributeDt : 0.004f;
            _reverbVolumes[regionId] = current + (targetVol - current) * (1f - MathF.Exp(-dtBus / MathF.Max(0.01f, tau)));
            bus.setVolume(_reverbVolumes[regionId]);

            if (_audioDebug && _dbgFrame % 60 == 0)
            {
                string name = _acousticMap.Regions.TryGetValue(regionId, out var rg) ? rg.FriendlyName : "?";
                float rdist = (regionId == _acousticMap.GlobalEnvironmentId) ? 0 : Vector3.Distance(lPosVec, _acousticMap.RegionPositions.GetValueOrDefault(regionId, Vector3.Zero));
                string binaural = "n/a";
                if (_reverbSaVoices.TryGetValue(regionId, out var rv) && rv.Dsp.hasHandle())
                    binaural = $"blend={rv.State.SpatialBlend:F2} dir=({rv.State.DirX:F2},{rv.State.DirY:F2},{rv.State.DirZ:F2})";
                Log.Information("[REVERB] listenerRegion={LR} bus={Rid}({Name}) {Pos} dist={D:F1} target={T:F2} vol={V:F2} reverbMode={B}",
                    listenerRegionId, regionId, name, regionId == listenerRegionId ? "INSIDE" : "outside", rdist, targetVol, _reverbVolumes[regionId], binaural);
            }
        }
    }

    // The cabin's paths, equalised for their directions (CabinPaths, HrtfBands).

    /// <summary>The ears' mean power per band in each direction asked about, by direction to a degree
    /// or so, and in the one interior voice's direction.</summary>
    private readonly Dictionary<(int, int, int), float[]?> _hrtfBands = new();
    private float[]? _hrtfOnePlace;

    /// <summary>The ears' mean power per band from <paramref name="dir"/>, measured once a direction. A
    /// measurement is a millisecond or two of the game thread and getting into a car asks for nine, so
    /// at most one every <see cref="HrtfMeasureSeconds"/>; false when this one must wait its turn.</summary>
    private bool HrtfBandsAt(Vector3 dir, out float[]? db)
    {
        var key = ((int)MathF.Round(dir.X * 50f), (int)MathF.Round(dir.Y * 50f), (int)MathF.Round(dir.Z * 50f));
        if (_hrtfBands.TryGetValue(key, out db)) return true;
        double now = OpenFPS.Common.AudioClock.Now;
        if (now - _hrtfMeasuredAt < HrtfMeasureSeconds) return false;
        _hrtfMeasuredAt = now;
        db = HrtfBands.Measure(_saContext, _saHrtf, MixerQuality.MixerRate, _saFrameSize, dir);
        if (_hrtfBands.Count > 512) _hrtfBands.Clear();
        _hrtfBands[key] = db;
        return true;
    }

    private double _hrtfMeasuredAt = double.NegativeInfinity;
    private const double HrtfMeasureSeconds = 0.01;

    /// <summary>
    /// The equaliser that takes a cabin path's HRTF colouring back to the one interior voice's (a little
    /// ahead and below, CabinPaths.OnePlace), on the path's binaural stage; redone when the path's
    /// direction moves more than two degrees.
    /// </summary>
    private void CabinTrim(ActiveSound active, Vector3 dir)
    {
        if (!_steamAudioEnabled || _saHrtf == IntPtr.Zero) return;
        if (active.CabinTrimDir != Vector3.Zero && Vector3.Dot(dir, active.CabinTrimDir) > 0.9994f) return;   // 2 degrees
        var one = OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.OnePlace;
        if (_hrtfOnePlace == null && !HrtfBandsAt(Vector3.Normalize(new Vector3(one.X, one.Y, -one.Z)), out _hrtfOnePlace)) return;
        if (!HrtfBandsAt(dir, out var here)) return;   // its turn comes next update
        active.CabinTrimDir = dir;
        if (_hrtfOnePlace == null || here == null) return;
        Span<float> trim = stackalloc float[here.Length];
        for (int k = 0; k < trim.Length; k++) trim[k] = Math.Clamp(_hrtfOnePlace[k] - here[k], -12f, 12f);
        // On the binaural stage, after the room's send: on the path itself it took the correction off
        // the send too, and the cabin's boom fell 1-2 dB.
        if (active.SaState != null) active.SaState.PreEq = OpenFPS.Client.AudioEngine.Core.Engine.BandEq.Design(trim, MixerQuality.MixerRate);
    }

    /// <summary>
    /// The listener in the frame a machine's parts are placed in (x across, y up, z forward, origin at
    /// the emitter). Forward is the emitter's Direction (an engine's heading), else the way it moves;
    /// false for a machine standing still with no heading.
    /// </summary>
    private bool ListenerInMachineFrame(Vector3 position, Vector3 direction, Vector3 velocity, out Vector3 local)
    {
        Vector3 fwd = direction;
        if (fwd.LengthSquared() < 1e-6f) fwd = velocity;
        fwd.Y = 0f;
        if (fwd.LengthSquared() < 1e-4f) { local = default; return false; }
        fwd = Vector3.Normalize(fwd);
        // +x is up × forward, the axis Vector3.Transform(slot, rotation) puts a part's x on.
        Vector3 right = Vector3.Cross(Vector3.UnitY, fwd);
        Vector3 d = _listenerPos - position;
        local = new Vector3(Vector3.Dot(d, right), d.Y, Vector3.Dot(d, fwd));
        return true;
    }

    /// <summary>The routes through openings, from whoever builds them (the acoustic worker).</summary>
    private Func<OpenFPS.Common.OpeningRoutes?>? _routesSource;
    public Func<OpenFPS.Common.OpeningRoutes?>? RoutesSource { set => _routesSource = value; }

    /// <summary>Beyond this an opening's share of a room's field is under -45 dB and not asked about.</summary>
    private const float FieldLeakRange = 60f;
    private readonly Dictionary<int, (double At, Vector3 Where, int Region, float Gain, Vector3 Via, Vector3 InField)> _fieldHere = new();

    /// <summary>Another room's reverberant field where the listener stands (OpeningRoutes.FieldAt),
    /// asked at most every quarter second or half metre.</summary>
    private bool FieldHere(OpenFPS.Common.OpeningRoutes routes, int regionId, Vector3 listener, int listenerRegion,
                           out float gain, out Vector3 via)
    {
        double now = OpenFPS.Common.AudioClock.Now;
        if (_fieldHere.TryGetValue(regionId, out var c) && c.Region == listenerRegion
            && now - c.At < 0.25 && Vector3.DistanceSquared(c.Where, listener) < 0.25f)
        {
            gain = c.Gain; via = c.Via;
            return gain > 0f;
        }
        gain = routes.FieldAt(regionId, listener, listenerRegion, FieldLeakRange, out via, out var inField);
        _fieldHere[regionId] = (now, listener, listenerRegion, gain, via, inField);
        return gain > 0f;
    }

    /// <summary>
    /// "Now and then I hear sounds like a siren pop through for an instant when I'm deep inside a
    /// building" (Cody, 2026-10-02). A sustained voice that jumps 15 dB or more within 150 ms and
    /// falls back within 400 ms is logged as [POP] with what its path said before and during, so the
    /// next one names its own cause. Not a fix: an instrument that runs in every session.
    /// </summary>
    private void WatchForPops(ActiveSound a, Vector3 listener)
    {
        if (a.Type == EmitterType.UI || a.IsReflection) return;
        double now = OpenFPS.Common.AudioClock.Now;
        if (a.FirstSeenAt < 0) a.FirstSeenAt = now;
        float level = 20f * MathF.Log10(MathF.Max(1e-6f, a.LastVolume)) + a.LastEqDb.Mid;
        if (float.IsNaN(a.PopBaseDb) || now - a.PopBaseAt > 0.15 || level < a.PopBaseDb)
        {
            if (a.PopRiseAt < 0) { a.PopBaseDb = level; a.PopBaseAt = now; a.PopFromOcclusion = a.CurrentOcclusion; a.PopFromMid = a.LastEqDb.Mid; }
        }
        if (a.PopRiseAt < 0)
        {
            if (now - a.FirstSeenAt > 0.5 && level > a.PopBaseDb + 15f && now - a.PopBaseAt <= 0.15)
            { a.PopRiseAt = now; a.PopPeakDb = level; }
            return;
        }
        a.PopPeakDb = MathF.Max(a.PopPeakDb, level);
        if (now - a.PopRiseAt > 0.4) { a.PopRiseAt = -1; a.PopBaseDb = level; a.PopBaseAt = now; return; }   // it stayed: a real change
        if (level < a.PopPeakDb - 12f)
        {
            if (now - a.PopLoggedAt > 2.0)
            {
                a.PopLoggedAt = now;
                Log.Information("[POP] {Sound} e{Id} at {Dist:F0} m: {From:F0} -> {Peak:F0} dB and back in {Ms:F0} ms; "
                              + "occlusion {Oc0:F2} -> {Oc1:F2}, mid EQ {M0:F0} -> {M1:F0} dB; listener region {Lr}, source region {Sr}",
                                a.SoundId, a.EntityId, Vector3.Distance(listener, a.Position), a.PopBaseDb, a.PopPeakDb,
                                (now - a.PopRiseAt) * 1000, a.PopFromOcclusion, a.CurrentOcclusion, a.PopFromMid, a.LastEqDb.Mid,
                                _listenerRegionId, a.TargetRegionId);
            }
            a.PopRiseAt = -1; a.PopBaseDb = level; a.PopBaseAt = now;
        }
    }

    /// <summary>
    /// Puts a head-relative voice at <paramref name="offset"/> (world axes, from the listener) in the
    /// listener's own frame. FMOD's listener space is right, up, forward, built from the same forward
    /// and up given to set3DListenerAttributes, so right is up x forward in its vectors.
    /// </summary>
    private void PlaceInHead(FMOD.Channel channel, Vector3 offset)
    {
        Vector3 fwd = Vector3.Transform(Vector3.UnitZ, _listenerRot), up = Vector3.Transform(Vector3.UnitY, _listenerRot);
        Vector3 right = Vector3.Cross(up, fwd);
        if (offset.LengthSquared() < 1e-6f) offset = fwd;
        var local = new FMOD.VECTOR { x = Vector3.Dot(offset, right), y = Vector3.Dot(offset, up), z = Vector3.Dot(offset, fwd) };
        var still = new FMOD.VECTOR();
        channel.set3DAttributes(ref local, ref still);
    }

    public void UpdateListener(Vector3 position, Quaternion rotation, Vector3 velocity, int regionId)
    {
        if (!_isInitialized) return;
        _listenerPos = position; _listenerRot = rotation; _listenerVel = velocity; _listenerRegionId = regionId;

        // Every ambisonic bed is rotated by this on the mixer thread: the field stays fixed in the
        // world while the listener turns, which is why the beds are ambisonic.
        if (_ambientBeds.Count > 0)
        {
            var frame = Phonon.ListenerFrame(rotation);
            foreach (var bed in _ambientBeds.Values) bed.State.Orientation = frame;
        }
        if (_traced.Count > 0)
        {
            var frame = Phonon.ListenerFrame(rotation);
            foreach (var t in _traced.Values) { t.State.Orientation = frame; t.State.Diffuse?.SetListenerRotation(rotation); }
        }
        FMOD.VECTOR fpos = FmodHelpers.ToFmodVec(position), fvel = FmodHelpers.ToFmodVec(velocity);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        Vector3 up = Vector3.Transform(Vector3.UnitY, rotation);
        FMOD.VECTOR ffwd = FmodHelpers.ToFmodVec(forward), fup = FmodHelpers.ToFmodVec(up);
        _system.set3DListenerAttributes(0, ref fpos, ref fvel, ref ffwd, ref fup);
    }

    public void UpdateShelter(float shelterFactor) => _shelterFactor = shelterFactor;

    // No blanket muffle on outdoor sounds inside an enclosure (retired 2026-09-30: it ignored doors,
    // "I open the door but I don't hear the outside world flow inside", and counted the walls twice).
    // Door leaves and walls are in the geometry; only rain and wind keep the sky-ray shelter.

    /// <summary>The listener's vehicle body against everything outside it, dB per band.</summary>
    private float _enclosureLowDb, _enclosureMidDb, _enclosureHighDb;
    public void SetListenerEnclosure(float lowDb, float midDb, float highDb)
    {
        _enclosureLowDb = lowDb; _enclosureMidDb = midDb; _enclosureHighDb = highDb;
    }

    /// <summary>
    /// Hands the mixer the space round the listener's head: one probe per direction, each its own
    /// reflection with its own delay, damping and place between the ears.
    /// </summary>
    public void UpdateBoundaries(ReadOnlySpan<BoundaryProbe> probes)
    {
        var s = _boundaryState;
        if (!_isInitialized || s == null) return;

        int count = Math.Min(probes.Length, BoundaryVoiceState.MaxTaps);
        Span<BoundaryTap> taps = stackalloc BoundaryTap[BoundaryVoiceState.MaxTaps];
        Span<bool> live = stackalloc bool[BoundaryVoiceState.MaxTaps];
        float total = 0f;

        for (int i = 0; i < count; i++)
        {
            live[i] = BoundaryModel.TryBuildTap(probes[i], _speedOfSound, s.SampleRate, out taps[i]);
            if (live[i]) total += Math.Max(Math.Abs(taps[i].GainL), Math.Abs(taps[i].GainR));
        }

        // A tight hard space puts a surface in every direction, and six summed swamp the direct sound:
        // the set is scaled back together, keeping the ratios between surfaces, which are the cue.
        float trim = total > AcousticConstants.MaxBoundaryReflectionSum
            ? AcousticConstants.MaxBoundaryReflectionSum / total
            : 1f;
        // Copies of the whole mix, on top of QueueEarlyEchoes' walls (in flat 01F, a -9 dB copy of
        // every sound off the ceiling 6 ms late): the copies trim governs them too.
        trim *= CopiesTrim;

        for (int i = 0; i < BoundaryVoiceState.MaxTaps; i++)
        {
            if (i < count && live[i])
            {
                s.TargetDelayL[i] = taps[i].DelayLSeconds;
                s.TargetDelayR[i] = taps[i].DelayRSeconds;
                s.TargetGainL[i] = taps[i].GainL * trim;
                s.TargetGainR[i] = taps[i].GainR * trim;
                s.LowpassAlpha[i] = taps[i].LowpassAlpha;
            }
            else
            {
                // Silenced, not detached: the mixer glides it out.
                s.TargetGainL[i] = 0f;
                s.TargetGainR[i] = 0f;
            }
        }
    }

    /// <summary>
    /// Starts an ambisonic ambience bed, or re-aims a playing one at a new level. Only a full-sphere
    /// recording (4, 9 or 16 channels) is taken: anything else would play as a soundfield pointing
    /// anywhere, with nothing to say so.
    /// </summary>
    /// <param name="soundId">Path under ASSETS/SOUNDS.</param>
    /// <param name="layout">The file's channel layout, AmbiX unless known otherwise; see
    /// <see cref="AmbisonicFormat"/> for why guessing wrong is silently awful.</param>
    /// <param name="volume">The level the bed glides to.</param>
    /// <param name="loop">Whether it loops.</param>
    public bool PlayAmbientBed(string soundId, AmbisonicLayout layout, float volume, bool loop = true)
    {
        if (!_isInitialized) return false;

        if (_ambientBeds.TryGetValue(soundId, out var existing))
        {
            existing.State.TargetVolume = volume;
            return true;
        }

        if (!_steamAudioEnabled || _saContext == IntPtr.Zero || _saHrtf == IntPtr.Zero)
        {
            Log.Warning("Ambience bed '{Id}' not started: Steam Audio is unavailable, so a soundfield " +
                        "cannot be decoded. The world will be quieter, not wrong.", soundId);
            return false;
        }

        if (!_granularBank.TryGetPcmData(soundId, out var pcm, out int channels, out int sampleRate))
        {
            Log.Warning("Ambience bed '{Id}' not found or could not be decoded.", soundId);
            return false;
        }

        int order = AmbisonicFormat.OrderForChannels(channels);
        if (order < 1)
        {
            Log.Warning("Ambience bed '{Id}' has {Channels} channel(s), which is not a full-sphere " +
                        "ambisonic layout (4, 9 or 16). Refusing to decode it as a soundfield.",
                        soundId, channels);
            return false;
        }

        // Converted once, on the bed's own copy, not per block on the mixer thread.
        var converted = new float[pcm.Length];
        Array.Copy(pcm, converted, pcm.Length);
        AmbisonicFormat.ConvertToN3d(converted, channels, layout);

        var state = new AmbisonicBedState
        {
            Pcm = converted,
            Channels = channels,
            Order = order,
            SourceSampleRate = sampleRate > 0 ? sampleRate : MixerQuality.MixerRate,
            FrameSize = _saFrameSize,
            Loop = loop,
            Context = _saContext,
            Hrtf = _saHrtf,
            Scratch = new float[_saFrameSize * channels],
            StereoScratch = new float[_saFrameSize * 2],
            TargetVolume = volume,
            CurrentVolume = 0f, // fade in from silence; a bed that starts at full level thumps
            Orientation = Phonon.ListenerFrame(_listenerRot)
        };

        var au = new Phonon.IPLAudioSettings { samplingRate = MixerQuality.MixerRate, frameSize = _saFrameSize };
        var settings = new Phonon.IPLAmbisonicsDecodeEffectSettings
        {
            speakerLayout = Phonon.StereoLayout(),
            hrtf = _saHrtf,
            maxOrder = order
        };
        if (Phonon.iplAmbisonicsDecodeEffectCreate(_saContext, ref au, ref settings, out state.Effect) != Phonon.IPL_STATUS_SUCCESS)
        {
            Log.Warning("Ambience bed '{Id}': Steam Audio refused to create an order-{Order} decode effect.", soundId, order);
            return false;
        }

        Phonon.iplAudioBufferAllocate(_saContext, channels, _saFrameSize, ref state.InBuf);
        Phonon.iplAudioBufferAllocate(_saContext, 2, _saFrameSize, ref state.OutBuf);

        if (AmbisonicBedDsp.CreateDSP(_system, state, out var dsp, out var handle) != RESULT.OK)
        {
            ReleaseBedResources(state, handle);
            Log.Warning("Ambience bed '{Id}': the FMOD DSP could not be created.", soundId);
            return false;
        }

        if (_system.playDSP(dsp, default, false, out var channel) != RESULT.OK)
        {
            dsp.release();
            ReleaseBedResources(state, handle);
            Log.Warning("Ambience bed '{Id}': FMOD would not play the DSP.", soundId);
            return false;
        }

        // 2D: the bed is already a finished binaural pair and FMOD must not pan it again.
        channel.setMode(MODE._2D);
        _ambientBeds[soundId] = new AmbientBed { State = state, Dsp = dsp, Channel = channel, Handle = handle };
        Log.Information("Ambience bed '{Id}' playing: order {Order}, {Channels}ch, {Rate} Hz, {Layout}.",
            soundId, order, channels, sampleRate, layout);
        return true;
    }

    /// <summary>Stops a bed and frees it, at once: there is no fade here.</summary>
    public void StopAmbientBed(string soundId)
    {
        if (!_ambientBeds.TryGetValue(soundId, out var bed)) return;
        _ambientBeds.Remove(soundId);

        if (bed.Channel.hasHandle()) bed.Channel.stop();
        if (bed.Dsp.hasHandle()) bed.Dsp.release();
        ReleaseBedResources(bed.State, bed.Handle);
    }

    private void ReleaseBedResources(AmbisonicBedState state, System.Runtime.InteropServices.GCHandle handle)
    {
        if (state.Effect != IntPtr.Zero) Phonon.iplAmbisonicsDecodeEffectRelease(ref state.Effect);
        if (state.InBuf.data != IntPtr.Zero) Phonon.iplAudioBufferFree(_saContext, ref state.InBuf);
        if (state.OutBuf.data != IntPtr.Zero) Phonon.iplAudioBufferFree(_saContext, ref state.OutBuf);
        if (handle.IsAllocated) handle.Free();
    }

    // Every change to _activeSounds goes through these, so the index and the list cannot drift apart.
    // All assume _lock is held.
    private void AddActive(ActiveSound sound)
    {
        _activeSounds.Add(sound);
        if (!_activeById.TryGetValue(sound.EntityId, out var voices))
            _activeById[sound.EntityId] = voices = new List<ActiveSound>(1);
        voices.Add(sound);
    }

    private void DeindexActive(ActiveSound sound)
    {
        if (!_activeById.TryGetValue(sound.EntityId, out var voices)) return;
        voices.Remove(sound);
        if (voices.Count == 0) _activeById.Remove(sound.EntityId);
    }

    private void RemoveActiveAt(int index)
    {
        var sound = _activeSounds[index];
        _activeSounds.RemoveAt(index);
        DeindexActive(sound);
    }

    /// <summary>The voice an entity's per-frame update drives, or null.</summary>
    private ActiveSound? FindActive(int entityId) =>
        _activeById.TryGetValue(entityId, out var voices) && voices.Count > 0 ? voices[0] : null;

    /// <summary>Restarts the loudness meter's integration, to measure per condition rather than per
    /// session.</summary>
    public void ResetLoudnessMeter()
    {
        if (!_loudnessMeter.hasHandle()) return;
        _loudnessMeter.setParameterInt(0, 0);
        _loudnessMeter.setParameterInt(0, 1);
    }

    /// <summary>Whether an engine voice's vehicle has its doors open.</summary>
    public bool EngineDoorsOpen(int entityId)
    {
        lock (_lock) return FindActive(entityId)?.EngineState?.DoorsOpen ?? false;
    }

    public bool TryGetEngineTelemetry(int entityId, out float toldSpeed, out float ownSpeed, out float rpm, out int gear)
    {
        toldSpeed = ownSpeed = rpm = 0f; gear = 0;
        EngineVoiceState? st;
        lock (_lock) st = FindActive(entityId)?.EngineState;
        if (st == null) return false;
        toldSpeed = st.TargetSpeed;
        ownSpeed = st.Driveline.Speed;
        rpm = st.Engine.Rpm;
        gear = st.Driveline.Gear;
        return true;
    }

    public string EngineVoiceDetail(int entityId)
    {
        lock (_lock)
        {
            var a = FindActive(entityId);
            if (a?.EngineState is not { } st) return "";
            static float Db(float g) => 20f * MathF.Log10(MathF.Max(g, 1e-9f));
            a.Channel.getVolume(out float volume);
            a.Channel.getAudibility(out float audibility);
            a.Channel.isVirtual(out bool isVirtual);
            a.Channel.getPaused(out bool paused);
            return $"voice out {st.LastOutputDb:F1} dBFS, envelope {st.EnvelopeNow:F2}, lift {Db(st.LiftNow):F1} dB, "
                 + $"{(st.Running ? "running" : "off")}{(st.Interior ? ", interior" : "")}, channel volume {Db(volume):F1} dB, "
                 + $"audibility {Db(audibility):F1} dB{(isVirtual ? ", VIRTUAL" : "")}{(paused ? ", PAUSED" : "")}";
        }
    }

    /// <summary>Brings an engine or machine voice back to full after a fade-out was started. Idempotent.</summary>
    public void ReviveEngine(int entityId)
    {
        lock (_lock)
        {
            var active = FindActive(entityId);
            // A standing machine fades and revives by the same rules: one question, one answer.
            active?.EngineState?.Revive();
            active?.MachineState?.Revive();
        }
    }

    /// <summary>How long the budget's fade takes, seconds. See ActiveSound.FadeGain.</summary>
    private const float VoiceFadeSeconds = 0.08f;

    public bool FadeOutVoice(int entityId)
    {
        lock (_lock)
        {
            var active = FindActive(entityId);
            if (active == null) return true;
            active.FadeTarget = 0f;
            return active.FadeGain <= 0.01f;
        }
    }

    public void CancelVoiceFade(int entityId)
    {
        lock (_lock)
        {
            var active = FindActive(entityId);
            if (active != null) active.FadeTarget = 1f;
        }
    }

    /// <summary>
    /// Asks a live engine, machine or tap voice to fade out; true once it is silent and safe to stop,
    /// or when there is no such voice. A synthesised voice has no zero-crossing to stop on, so stopping
    /// one without this is a step in the waveform.
    /// </summary>
    public bool FadeOutEngine(int entityId)
    {
        lock (_lock)
        {
            var active = FindActive(entityId);
            var tap = active?.TapState;
            if (tap != null)
            {
                tap.TargetGain = 0f;
                // Handed back as the fade starts, so the voice that stays gains the share over the
                // same sixty milliseconds this one loses it.
                tap.HandBack();
                return tap.FadedOut;
            }
            var mach = active?.MachineState;
            if (mach != null)
            {
                mach.TargetEnvelope = 0f;
                return mach.FadedOut;
            }
            var st = active?.EngineState;
            if (st == null) return true;
            st.TargetEnvelope = 0f;
            return st.FadedOut;
        }
    }

    public void StopSound(int entityId) { 
        lock (_lock) { 
            if (!_activeById.TryGetValue(entityId, out var voices)) return;
            foreach (var sound in voices) {
                sound.Channel.stop();
                ReleaseActiveSoundResources(sound);
                _activeSounds.Remove(sound);
            }
            _activeById.Remove(entityId);
        } 
    }
    
    public bool IsPlaying(int entityId) { lock (_lock) return _activeById.ContainsKey(entityId); }
    public IEnumerable<int> GetActiveSpatialSoundIds() { lock(_lock) return new List<int>(_activeById.Keys); }
    public Vector3 GetSoundPosition(int entityId) { lock(_lock) return FindActive(entityId)?.Position ?? Vector3.Zero; }

    public bool TryDecode(string soundId, out float[] pcm, out int channels, out int sampleRate)
    {
        pcm = Array.Empty<float>(); channels = 0; sampleRate = 0;
        return _isInitialized && _granularBank.TryDecode(soundId, out pcm, out channels, out sampleRate);
    }

    public void Preload(string soundId)
    {
        if (!_isInitialized) return;

        _granularBank.TryGetPcmData(soundId, out _, out _, out _);

        // The nonblocking decode starts here, long before anything asks to hear it: why deferred plays are rare.
        _resources.TryGetSound(soundId, out _, false);
    }

    /// <summary>Registers a synthesised buffer under an id, for an ordinary emitter to play with the full
    /// acoustic treatment. False if the audio engine is not up.</summary>
    public bool RegisterSynthesisedSound(string soundId, byte[] pcm16Mono, int sampleRate)
        => _isInitialized && _resources.RegisterPcm(soundId, pcm16Mono, sampleRate);

    public bool ReleaseSynthesisedSound(string soundId)
        => _isInitialized && _resources.ReleasePcm(soundId);

    public bool RegisterSynthesisedSoundFloat(string soundId, float[] pcm, int sampleRate)
        => _isInitialized && _resources.RegisterPcmFloat(soundId, pcm, sampleRate);

    /// <summary>Interface sounds, made once and kept until shutdown: releasing an FMOD sound stops every
    /// channel playing it.</summary>
    private readonly Dictionary<string, FMOD.Sound> _uiSounds = new();

    /// <summary>The group interface sounds play in, under the master.</summary>
    private FMOD.ChannelGroup _uiGroup;

    /// <summary>The master's own level (OPENFPS_MASTER_DB), which a world fade scales.</summary>
    private float _masterTrim = 1f;

    /// <summary>The quietest a fade goes, 60 dB down. Never zero: the interface group is lifted by
    /// the inverse, and that has to stay finite.</summary>
    public const float WorldFadeFloor = 1e-3f;

    /// <summary>Fades everything heard in the world, not the interface: the fade is the master's level,
    /// and the interface group is lifted by the inverse.</summary>
    public void SetWorldFade(float gain)
    {
        if (!_isInitialized) return;
        float g = Math.Clamp(gain, WorldFadeFloor, 1f);
        _system.getMasterChannelGroup(out var master);
        master.setVolume(_masterTrim * g);
        if (_uiGroup.hasHandle()) _uiGroup.setVolume(1f / g);
    }

    /// <summary>The voices reaching the listener loudest, by the volume last applied and the strongest
    /// band the EQ lets through. Diagnostic: "why can I still hear that".</summary>
    public IReadOnlyList<VoiceLevel> LoudestVoices(int count)
    {
        var all = new List<VoiceLevel>();
        lock (_lock)
        {
            foreach (var a in _activeSounds)
            {
                if (!a.Channel.hasHandle() || a.LastVolume <= 0f) continue;
                float dist = Vector3.Distance(_listenerPos, a.CurrentApparentPosition);
                // FMOD applies the law to a voice it pans; LastVolume holds it only for binaural ones.
                float law = a.SaState != null ? 1f : Loudness.RenderedGain(1f, a.MinDistance, a.Range, dist);
                float db = 20f * MathF.Log10(MathF.Max(1e-9f, a.LastVolume * law));
                var eq = a.LastEqDb;
                all.Add(new VoiceLevel(a.EntityId, a.SoundId, Vector3.Distance(_listenerPos, a.Position),
                                       db + MathF.Max(eq.Low, MathF.Max(eq.Mid, eq.High)),
                                       a.CurrentOcclusion, db + eq.Low, db + eq.Mid, db + eq.High,
                                       a.IsReflection, Vector3.Distance(a.Position, a.CurrentApparentPosition) > 1f));
            }
        }
        all.Sort(static (x, y) => y.Db.CompareTo(x.Db));
        return all.Count > count ? all.GetRange(0, count) : all;
    }

    public IReadOnlyList<string> OutputDevices()
    {
        var names = new List<string>();
        if (!_isInitialized || _system.getNumDrivers(out int n) != RESULT.OK) return names;
        for (int i = 0; i < n; i++)
            if (_system.getDriverInfo(i, out string name, 256, out _, out _, out _, out _) == RESULT.OK) names.Add(name);
        return names;
    }

    public IReadOnlyList<string> InputDevices()
    {
        var names = new List<string>();
        if (!_isInitialized || _system.getRecordNumDrivers(out int n, out _) != RESULT.OK) return names;
        for (int i = 0; i < n; i++)
            if (_system.getRecordDriverInfo(i, out string name, 256, out _, out _, out _, out _, out _) == RESULT.OK) names.Add(name);
        return names;
    }

    // The microphone, for the Linux head's voice chat: FMOD records too, and Settings lists its device
    // names. One second of looping buffer, read as the record cursor moves.

    private readonly object _recLock = new();
    private FMOD.Sound _recSound;
    private int _recDriver = -1;
    private int _recChannels;
    private uint _recFrames;
    private uint _recLast;

    public bool HasRecordingDevice
        => _isInitialized && _system.getRecordNumDrivers(out _, out int connected) == RESULT.OK && connected > 0;

    public bool StartRecording(string deviceName, out int sampleRate)
    {
        sampleRate = 0;
        if (!_isInitialized) return false;
        lock (_recLock)
        {
            StopRecordingLocked();
            if (_system.getRecordNumDrivers(out int n, out _) != RESULT.OK) return false;
            int chosen = -1, fallback = -1, rate = 0, channels = 0;
            for (int i = 0; i < n; i++)
            {
                if (_system.getRecordDriverInfo(i, out string name, 256, out _, out int r, out _, out int ch, out var state) != RESULT.OK) continue;
                if ((state & DRIVER_STATE.CONNECTED) == 0) continue;
                bool wanted = deviceName.Length > 0 ? name == deviceName : (state & DRIVER_STATE.DEFAULT) != 0;
                if (wanted) { chosen = i; rate = r; channels = ch; break; }
                if (fallback < 0) { fallback = i; rate = r; channels = ch; }
            }
            if (chosen < 0)
            {
                if (fallback < 0) return false;
                if (deviceName.Length > 0) Log.Warning("The chosen microphone, {Name}, is not connected; using another.", deviceName);
                chosen = fallback;
                _system.getRecordDriverInfo(chosen, out _, 256, out _, out rate, out _, out channels, out _);
            }
            if (rate <= 0) rate = 48000;
            if (channels <= 0) channels = 1;

            var info = new CREATESOUNDEXINFO
            {
                cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(),
                numchannels = channels,
                defaultfrequency = rate,
                format = SOUND_FORMAT.PCM16,
                length = (uint)(rate * channels * sizeof(short)),
            };
            if (_system.createSound(IntPtr.Zero, MODE.LOOP_NORMAL | MODE.OPENUSER, ref info, out _recSound) != RESULT.OK) return false;
            var res = _system.recordStart(chosen, _recSound, true);
            if (res != RESULT.OK)
            {
                Log.Warning("The microphone would not start: {R}", res);
                _recSound.release();
                _recSound = default;
                return false;
            }
            _recDriver = chosen;
            _recChannels = channels;
            _recFrames = (uint)rate;
            _recLast = 0;
            sampleRate = rate;
            Log.Information("Microphone recording from driver {Driver}: {Rate} Hz, {Ch} channel(s).", chosen, rate, channels);
            return true;
        }
    }

    public int ReadRecording(List<float> mono)
    {
        lock (_recLock)
        {
            if (_recDriver < 0 || !_recSound.hasHandle()) return 0;
            if (_system.getRecordPosition(_recDriver, out uint pos) != RESULT.OK || pos == _recLast) return 0;
            uint frames = (pos + _recFrames - _recLast) % _recFrames;
            uint bpf = (uint)(_recChannels * sizeof(short));
            if (_recSound.@lock(_recLast * bpf, frames * bpf, out IntPtr p1, out IntPtr p2, out uint l1, out uint l2) != RESULT.OK) return 0;
            int before = mono.Count;
            Append(p1, l1);
            Append(p2, l2);
            _recSound.unlock(p1, p2, l1, l2);
            _recLast = pos;
            return mono.Count - before;

            void Append(IntPtr p, uint bytes)
            {
                if (p == IntPtr.Zero || bytes == 0) return;
                int count = (int)(bytes / sizeof(short));
                var buf = new short[count];
                System.Runtime.InteropServices.Marshal.Copy(p, buf, 0, count);
                for (int i = 0; i + _recChannels <= count; i += _recChannels)
                {
                    float sum = 0f;
                    for (int c = 0; c < _recChannels; c++) sum += buf[i + c];
                    mono.Add(sum / (_recChannels * 32768f));
                }
            }
        }
    }

    public void StopRecording()
    {
        lock (_recLock) StopRecordingLocked();
    }

    private void StopRecordingLocked()
    {
        if (_recDriver >= 0 && _isInitialized) _system.recordStop(_recDriver);
        if (_recSound.hasHandle()) _recSound.release();
        _recSound = default;
        _recDriver = -1;
    }

    public bool SetOutputDevice(string name)
    {
        if (!_isInitialized) return false;
        int index = 0;
        if (!string.IsNullOrEmpty(name))
        {
            var all = OutputDevices();
            index = -1;
            for (int i = 0; i < all.Count; i++) if (all[i] == name) { index = i; break; }
            if (index < 0) return false;
        }
        var r = _system.setDriver(index);
        if (r != RESULT.OK) { Log.Warning("Could not switch output to '{Name}': {R}", name, r); return false; }
        Log.Information("Audio output: {Name}", string.IsNullOrEmpty(name) ? "system default" : name);
        return true;
    }

    public void PlayUiSound(string id, Func<float[]> render, int sampleRate, float volume)
    {
        if (!_isInitialized) return;
        lock (_uiSounds)
        {
        if (!_uiSounds.TryGetValue(id, out var sound))
        {
            // In float: an interface sound's fade is its quiet end, and sixteen truncated bits are steps there.
            var pcm = FloatBytes(render());
            var info = new CREATESOUNDEXINFO
            {
                cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(),
                length = (uint)pcm.Length,
                numchannels = 1,
                defaultfrequency = sampleRate,
                format = SOUND_FORMAT.PCMFLOAT,
            };
            if (_system.createSound(pcm, MODE.OPENMEMORY | MODE.OPENRAW | MODE.CREATESAMPLE | MODE.LOOP_OFF | MODE._2D,
                                    ref info, out sound) != RESULT.OK) return;
            _uiSounds[id] = sound;
        }
        if (_system.playSound(sound, _uiGroup, true, out FMOD.Channel channel) != RESULT.OK) return;
        channel.setVolume(Math.Clamp(volume, 0f, 1f));
        channel.setPaused(false);
        }
    }

    /// <summary>A float buffer as the bytes FMOD reads for PCMFLOAT.</summary>
    private static byte[] FloatBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>The interface loops playing now, by slot: the channel and the loop it is playing.</summary>
    private readonly Dictionary<string, (FMOD.Channel Channel, string Id)> _uiLoops = new();

    /// <summary>An interface loop's fade in or out, seconds: no click, and the guidance tone still
    /// answers the aim at once.</summary>
    private const float UiLoopFadeSeconds = 0.02f;

    public void SetUiLoop(string slot, string id, Func<float[]> render, int sampleRate, float volume, float pitch)
    {
        if (!_isInitialized) return;
        lock (_uiSounds)
        {
            volume = Math.Clamp(volume, 0f, 1f);
            pitch = Math.Clamp(pitch, 0.25f, 4f);
            if (_uiLoops.TryGetValue(slot, out var current) && current.Id == id
                && current.Channel.isPlaying(out bool playing) == RESULT.OK && playing)
            {
                current.Channel.setVolume(volume);
                current.Channel.setPitch(pitch);
                return;
            }
            if (_uiLoops.Remove(slot, out var old)) FadeOutAndStop(old.Channel);

            string key = "loop:" + id;
            if (!_uiSounds.TryGetValue(key, out var sound))
            {
                var pcm = FloatBytes(render());
                var info = new CREATESOUNDEXINFO
                {
                    cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(),
                    length = (uint)pcm.Length,
                    numchannels = 1,
                    defaultfrequency = sampleRate,
                    format = SOUND_FORMAT.PCMFLOAT,
                };
                if (_system.createSound(pcm, MODE.OPENMEMORY | MODE.OPENRAW | MODE.CREATESAMPLE | MODE.LOOP_NORMAL | MODE._2D,
                                        ref info, out sound) != RESULT.OK) return;
                _uiSounds[key] = sound;
            }
            if (_system.playSound(sound, _uiGroup, true, out FMOD.Channel channel) != RESULT.OK) return;
            channel.setMode(MODE.LOOP_NORMAL);
            channel.setLoopCount(-1);
            channel.setVolume(volume);
            channel.setPitch(pitch);
            // On the parent's clock: the channel's own is not running yet (see PlaySpatialSound).
            channel.getDSPClock(out _, out ulong start);
            _system.getSoftwareFormat(out int rate, out _, out _);
            channel.addFadePoint(start, 0f);
            channel.addFadePoint(start + (ulong)(rate * UiLoopFadeSeconds), 1f);
            channel.setPaused(false);
            _uiLoops[slot] = (channel, id);
        }
    }

    public void StopUiLoop(string slot)
    {
        if (!_isInitialized) return;
        lock (_uiSounds)
        {
            if (_uiLoops.Remove(slot, out var old)) FadeOutAndStop(old.Channel);
        }
    }

    /// <summary>A loop faded to nothing and stopped once it is there, rather than cut mid-cycle.</summary>
    private void FadeOutAndStop(FMOD.Channel channel)
    {
        if (!channel.hasHandle()) return;
        if (channel.getDSPClock(out _, out ulong now) != RESULT.OK) return;
        _system.getSoftwareFormat(out int rate, out _, out _);
        ulong end = now + (ulong)(rate * UiLoopFadeSeconds);
        channel.addFadePoint(now, 1f);
        channel.addFadePoint(end, 0f);
        channel.setDelay(0, end, true);
    }

    // One isolated mono source for checking HRTF and panning by ear, bypassing the voice manager and
    // the acoustics. Driven by AudioDiagnostics (`--audio-test`).
    private FMOD.Channel _diagChannel;
    private FMOD.Sound _diagSound;
    private byte[]? _diagPcm;

    public void StartDiagnosticSound()
    {
        if (!_isInitialized) return;
        StopDiagnosticSound();

        // A one-second loop of broadband noise bursts: transients localise far better than tones.
        int sampleRate = MixerQuality.MixerRate;
        int numSamples = sampleRate;
        _diagPcm = new byte[numSamples * 2];
        int periodSamples = sampleRate / 8; // 8 bursts per second
        uint seed = 22695477;
        for (int i = 0; i < numSamples; i++)
        {
            int posInPeriod = i % periodSamples;
            float env = posInPeriod < periodSamples * 0.32f ? 1.0f : 0.0f; // ~40ms on, ~85ms off
            seed = seed * 1103515245 + 12345; // LCG white noise
            float noise = ((seed >> 16) & 0x7FFF) / 16384.0f - 1.0f;
            short s = (short)(noise * env * 0.5f * short.MaxValue);
            _diagPcm[i * 2] = (byte)(s & 0xFF);
            _diagPcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        var info = new CREATESOUNDEXINFO
        {
            cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(),
            length = (uint)_diagPcm.Length,
            numchannels = 1,
            defaultfrequency = sampleRate,
            format = SOUND_FORMAT.PCM16
        };

        // OPENRAW: headerless PCM in memory.
        if (!FmodCheck(_system.createSound(_diagPcm, MODE.OPENMEMORY | MODE.OPENRAW | MODE._3D | Rolloff.Mode | MODE.LOOP_NORMAL, ref info, out _diagSound), "createSound(diagnostic)"))
            return;
        if (!FmodCheck(_system.playSound(_diagSound, default, true, out _diagChannel), "playSound(diagnostic)"))
            return;

        _diagChannel.setMode(MODE._3D | Rolloff.Mode | MODE.LOOP_NORMAL);
        _diagChannel.set3DLevel(1.0f);
        _diagChannel.set3DMinMaxDistance(1.0f, 100.0f);
        _diagChannel.setVolume(1.0f);
        var origin = new FMOD.VECTOR { x = 0, y = 0, z = 3 };
        var zero = new FMOD.VECTOR();
        _diagChannel.set3DAttributes(ref origin, ref zero);
        _diagChannel.setPaused(false);
        Log.Information("Diagnostic mono source started (broadband burst loop).");
    }

    public void SetDiagnosticPosition(Vector3 position)
    {
        if (!_diagChannel.hasHandle()) return;
        var fpos = FmodHelpers.ToFmodVec(position);
        var fvel = new FMOD.VECTOR();
        _diagChannel.set3DAttributes(ref fpos, ref fvel);
    }

    public void StopDiagnosticSound()
    {
        if (_diagChannel.hasHandle()) { _diagChannel.stop(); _diagChannel = default; }
        if (_diagSound.hasHandle()) { _diagSound.release(); _diagSound = default; }
        _diagPcm = null;
    }

    public void Dispose() {
        StopRecording();
        lock (_lock) {
            foreach (var active in _activeSounds) {
                ReleaseActiveSoundResources(active);
            }
            _activeSounds.Clear();
            _activeById.Clear();
            ReturnReverbVoices();
            ReleaseReverbUnits();
            foreach (var id in new List<string>(_ambientBeds.Keys)) StopAmbientBed(id);
            if (_earWindChannel.hasHandle()) _earWindChannel.stop();
            if (_earWindDsp.hasHandle()) _earWindDsp.release();
            // The master's units come off before they are freed: FMOD refuses to release an attached unit.
            _system.getMasterChannelGroup(out var masterOut);
            if (masterOut.hasHandle())
            {
                if (_boundaryDsp.hasHandle()) masterOut.removeDSP(_boundaryDsp);
                if (_loudnessMeter.hasHandle()) masterOut.removeDSP(_loudnessMeter);
                if (_masterLimiter.hasHandle()) masterOut.removeDSP(_masterLimiter);
            }
            if (_boundaryDsp.hasHandle()) _boundaryDsp.release();
            _enginePool?.Dispose(); _enginePool = null;
            _masterTap?.Dispose(); _masterTap = null;
            _preLimiterTap?.Dispose(); _preLimiterTap = null;
            _dither?.Dispose();
            if (_loudnessMeter.hasHandle()) _loudnessMeter.release();
            if (_masterLimiter.hasHandle()) _masterLimiter.release();
        } 
        StopDiagnosticSound();

        // The FMOD side, then close, then what the callbacks read: a released DSP can be mid-callback
        // until the mixer syncs, and native memory freed under it is a crash no guard catches.
        // close() stops the mixer thread.
        if (_steamAudioEnabled)
            foreach (var v in _saAllVoices)
                if (v.Dsp.hasHandle()) v.Dsp.release();
        _resources?.Dispose();
        _granularBank?.Dispose();
        if (_isInitialized) _system.close();   // FMOD requires close() before release()
        if (_earWindHandle.IsAllocated) _earWindHandle.Free();
        _dither?.FreeHandle(); _dither = null;
        _trueLimiter?.FreeHandle(); _trueLimiter = null;

        if (_steamAudioEnabled)
        {
            // The ONLY place Phonon voice resources are freed.
            foreach (var v in _saAllVoices)
            {
                if (v.Handle.IsAllocated) v.Handle.Free();
                Phonon.iplAudioBufferFree(_saContext, ref v.State.InBuf);
                Phonon.iplAudioBufferFree(_saContext, ref v.State.OutBuf);
                IntPtr eff = v.State.Effect;
                Phonon.iplBinauralEffectRelease(ref eff);
                FreeGroundPath(v.State);
            }
            _saAllVoices.Clear();
            _saPool.Clear();
            Phonon.iplHRTFRelease(ref _saHrtf);
            if (_saHrtfTraced != IntPtr.Zero) Phonon.iplHRTFRelease(ref _saHrtfTraced);
            Phonon.iplContextRelease(ref _saContext);
            _steamAudioEnabled = false;
        }

        // Every voice handle retired during the session, and the boundary unit's: the mixer is
        // stopped, so no callback can resolve one.
        if (_boundaryHandle.IsAllocated) _boundaryHandle.Free();
        foreach (var h in _retiredHandles) if (h.IsAllocated) h.Free();
        _retiredHandles.Clear();
        if (_isInitialized) _system.release();
    }
}

internal static class MathHelper
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}
