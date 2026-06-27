using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FMOD;
using Serilog;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.AudioEngine.Fmod;

internal static class FmodHelpers
{
    public static FMOD.VECTOR ToFmodVec(Vector3 v) => new FMOD.VECTOR { x = v.X, y = v.Y, z = v.Z };
}

internal class FmodResourceManager : IDisposable
{
    private readonly FMOD.System _system;
    private readonly Dictionary<string, FMOD.Sound> _cache = new();

    public FmodResourceManager(FMOD.System system) => _system = system;

    public bool TryGetSound(string soundId, out FMOD.Sound sound, bool loop = false)
    {
        if (string.IsNullOrEmpty(soundId)) { sound = default; return false; }
        string cacheKey = soundId + (loop ? "_L" : "");
        if (_cache.TryGetValue(cacheKey, out sound))
        {
            // NONBLOCKING loads complete asynchronously — verify the sound is ready before use.
            sound.getOpenState(out OPENSTATE openState, out _, out _, out _);
            if (openState == OPENSTATE.READY) return true;
            sound = default;
            return false;
        }
        
        string path;
        if (soundId.Contains("ASSETS", StringComparison.OrdinalIgnoreCase)) path = soundId;
        else
        {
            string normId = soundId.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS", "SOUNDS", normId);
        }

        if (!File.Exists(path))
        {
            string[] extensions = { ".wav", ".ogg", ".mp3" };
            bool found = false;
            foreach (var ext in extensions) { if (File.Exists(path + ext)) { path = path + ext; found = true; break; } }
            if (!found) return false;
        }

        MODE mode = MODE.CREATESAMPLE | MODE._3D | MODE._3D_LINEARROLLOFF | MODE.NONBLOCKING;
        if (loop) mode |= MODE.LOOP_NORMAL;
        
        RESULT res = _system.createSound(path, mode, out sound);
        if (res != RESULT.OK) return false;

        _cache[cacheKey] = sound;
        return true;
    }
    public void Dispose() { foreach (var s in _cache.Values) s.release(); _cache.Clear(); }
}

public class FmodAudioProvider : IAudioProvider
{
    private FMOD.System _system;
    private FmodResourceManager _resources = null!;
    private GranularBank _granularBank = null!;
    private bool _isInitialized = false;

    private readonly System.Collections.Concurrent.ConcurrentStack<FMOD.DSP> _threeEqPool = new();
    private readonly System.Collections.Concurrent.ConcurrentStack<FMOD.DSP> _diffractionPool = new();

    private FMOD.DSP GetThreeEqDsp()
    {
        if (_threeEqPool.TryPop(out var dsp)) { dsp.setBypass(false); return dsp; }
        _system.createDSPByType(DSP_TYPE.THREE_EQ, out dsp);
        return dsp;
    }

    private FMOD.DSP GetDiffractionDsp()
    {
        if (_diffractionPool.TryPop(out var dsp)) { dsp.setBypass(false); return dsp; }
        _system.createDSPByType(DSP_TYPE.LOWPASS, out dsp);
        return dsp;
    }

    private void ReleaseThreeEqDsp(FMOD.DSP dsp)
    {
        if (dsp.hasHandle()) { dsp.setBypass(true); _threeEqPool.Push(dsp); }
    }

    private void ReleaseDiffractionDsp(FMOD.DSP dsp)
    {
        if (dsp.hasHandle()) { dsp.setBypass(true); _diffractionPool.Push(dsp); }
    }

    private class PooledGranularDsp { public FMOD.DSP Dsp; public System.Runtime.InteropServices.GCHandle Handle; public GranularVoiceState State; public PooledGranularDsp(FMOD.DSP dsp, System.Runtime.InteropServices.GCHandle h, GranularVoiceState s) { Dsp = dsp; Handle = h; State = s; } }
    private class PooledSynthDsp { public FMOD.DSP Dsp; public System.Runtime.InteropServices.GCHandle Handle; public SynthVoiceState State; public PooledSynthDsp(FMOD.DSP dsp, System.Runtime.InteropServices.GCHandle h, SynthVoiceState s) { Dsp = dsp; Handle = h; State = s; } }
    
    private readonly System.Collections.Concurrent.ConcurrentStack<PooledGranularDsp> _granularPool = new();
    private readonly System.Collections.Concurrent.ConcurrentStack<PooledSynthDsp> _synthPool = new();

    private PooledGranularDsp? GetGranularDsp(float[] pcm, int ch, int sr)
    {
        if (_granularPool.TryPop(out var pooled))
        {
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
        if (_synthPool.TryPop(out var pooled)) return pooled;
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
        if (dsp.hasHandle() && state != null) { _granularPool.Push(new PooledGranularDsp(dsp, handle, state)); }
    }

    private void ReleaseSynthDsp(FMOD.DSP dsp, System.Runtime.InteropServices.GCHandle handle, SynthVoiceState? state)
    {
        if (dsp.hasHandle() && state != null) { _synthPool.Push(new PooledSynthDsp(dsp, handle, state)); }
    }

    private class ActiveSound
    {
        public int EntityId; 
        public string SoundId = ""; 
        public EmitterType Type;
        public FMOD.Channel Channel; 
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

        public Vector3 Position; 
        public Vector3 ApparentPosition; 
        public Vector3 CurrentApparentPosition; 
        public float EffectiveDistance;
        public Vector3 Velocity; 
        public Vector3 Direction;
        public float Range; 
        public float MinDistance;
        public float BaseVolume; 
        public float Pitch;
        
        public float CurrentOcclusion;
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
        
        public float AirAbsorption;
        public int TargetRegionId = -1; 
        public bool IsReflection; 
        public float ConeInside;
        public float ConeOutside;
        public float ConeOutsideVolume;
        public float ReflectionSpread;
        public int CurrentRegionId = -2;
        public int CurrentSourceRegionId = -2;
        public FMOD.DSPConnection ReverbConnection;
        public FMOD.DSPConnection SourceReverbConnection;
        public float RoomGain = 1.0f;

        // Steam Audio per-voice binaural effect (null when SA disabled / falling back to FMOD pan).
        public SteamAudioVoiceState? SaState;
        public FMOD.DSP SaDsp;
        public System.Runtime.InteropServices.GCHandle SaHandle;
    }

    private readonly List<ActiveSound> _activeSounds = new();
    private readonly object _lock = new();
    private AcousticMap? _acousticMap;
    private Dictionary<int, FMOD.ChannelGroup> _reverbBuses = new();
    private Dictionary<int, FMOD.DSP> _reverbDsps = new();
    private Dictionary<int, float> _reverbVolumes = new();
    private HashSet<int> _activeRegionIds = new();

    private FMOD.ChannelGroup _reflectionGroup;
    private FMOD.DSP _masterCombFilter;

    private Vector3 _listenerPos = Vector3.Zero;
    private Vector3 _listenerVel = Vector3.Zero;
    private Quaternion _listenerRot = Quaternion.Identity;
    private int _listenerRegionId = -1;
    private float _shelterFactor = 0.0f;

    // Steam Audio (phonon) HRTF state — shared context + HRTF; per-voice effects live on ActiveSound.
    private IntPtr _saContext;
    private IntPtr _saHrtf;
    private int _saFrameSize = 1024;
    private bool _steamAudioEnabled;

    /// <summary>
    /// Logs and returns false when an FMOD call fails. FMOD result codes were previously
    /// discarded everywhere, so failures (missing DLL, bad format, etc.) produced silence
    /// with no explanation. Route significant calls through this to make failures visible.
    /// </summary>
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
            if (!FmodCheck(Factory.System_Create(out _system), "System_Create")) return false;

            // Coordinate convention: the listener uses forward = +Z, up = +Y, right = +X
            // (strafe-right is Transform(UnitX, yaw)). That is exactly FMOD's DEFAULT LEFT-handed
            // convention (+X right, +Y up, +Z forward), so NO handedness flag is needed.
            // Empirically: setting INITFLAGS._3D_RIGHTHANDED inverts left/right (a +X source is
            // heard on the LEFT). Verified by ear 2026-06 — leave FMOD in its default left-handed mode.
            FmodCheck(_system.setSoftwareFormat(44100, SPEAKERMODE.STEREO, 0), "setSoftwareFormat");
            FmodCheck(_system.set3DSettings(1.0f, 1.0f, 1.0f), "set3DSettings"); // 1 unit = 1 metre

            if (!FmodCheck(_system.init(512, INITFLAGS.NORMAL | INITFLAGS.VOL0_BECOMES_VIRTUAL, IntPtr.Zero), "init"))
                return false;

            _system.getVersion(out uint version);
            Log.Information("FMOD initialized: v{Major:X}.{Minor:X2}.{Patch:X2}, left-handed 3D (+X right / +Z fwd), 44.1kHz stereo.",
                (version >> 16) & 0xFFFF, (version >> 8) & 0xFF, version & 0xFF);

            _resources = new FmodResourceManager(_system);
            _granularBank = new GranularBank(_system);
            _system.createChannelGroup("Reflections", out _reflectionGroup);
            _system.getMasterChannelGroup(out var master);
            master.addGroup(_reflectionGroup);

            _system.createDSPByType(DSP_TYPE.ECHO, out _masterCombFilter);
            _masterCombFilter.setParameterFloat(0, 10.0f); 
            _masterCombFilter.setParameterFloat(1, 30.0f); 
            _masterCombFilter.setParameterFloat(2, 0.0f); 
            _masterCombFilter.setParameterFloat(3, -80.0f); 
            master.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, _masterCombFilter);
            _masterCombFilter.setBypass(true);

            TryInitSteamAudio();

            _isInitialized = true;
            return true;
        }
        catch (Exception ex) { Log.Error(ex, "Failed to initialize FMOD"); return false; }
    }

    // --- Steam Audio (phonon) HRTF binaural spatialization. Falls back to FMOD panning if unavailable. ---

    private void TryInitSteamAudio()
    {
        try
        {
            _system.getDSPBufferSize(out uint block, out int _);
            _saFrameSize = (int)block;
            var cs = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
            if (Phonon.iplContextCreate(ref cs, out _saContext) != Phonon.IPL_STATUS_SUCCESS)
            { Log.Warning("Steam Audio: context create failed; using FMOD panning."); return; }

            var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = _saFrameSize };
            var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
            if (Phonon.iplHRTFCreate(_saContext, ref au, ref hs, out _saHrtf) != Phonon.IPL_STATUS_SUCCESS)
            { Log.Warning("Steam Audio: HRTF create failed; using FMOD panning."); Phonon.iplContextRelease(ref _saContext); return; }

            _steamAudioEnabled = true;
            Log.Information("Steam Audio HRTF binaural enabled (frameSize={Frame}).", _saFrameSize);
        }
        catch (DllNotFoundException) { Log.Warning("Steam Audio: libphonon not found; using FMOD panning."); }
        catch (Exception ex) { Log.Warning(ex, "Steam Audio init failed; using FMOD panning."); }
    }

    private bool TryCreateSteamAudioVoice(out SteamAudioVoiceState? state, out FMOD.DSP dsp, out System.Runtime.InteropServices.GCHandle handle)
    {
        state = null; dsp = default; handle = default;
        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = _saFrameSize };
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = _saHrtf };
        if (Phonon.iplBinauralEffectCreate(_saContext, ref au, ref es, out IntPtr effect) != Phonon.IPL_STATUS_SUCCESS) return false;

        var s = new SteamAudioVoiceState
        {
            Context = _saContext, Hrtf = _saHrtf, Effect = effect, FrameSize = _saFrameSize,
            MonoScratch = new float[_saFrameSize], StereoScratch = new float[_saFrameSize * 2]
        };
        Phonon.iplAudioBufferAllocate(_saContext, 1, _saFrameSize, ref s.InBuf);
        Phonon.iplAudioBufferAllocate(_saContext, 2, _saFrameSize, ref s.OutBuf);

        if (SteamAudioDsp.CreateDSP(_system, s, out dsp, out handle) != RESULT.OK)
        {
            Phonon.iplAudioBufferFree(_saContext, ref s.InBuf);
            Phonon.iplAudioBufferFree(_saContext, ref s.OutBuf);
            Phonon.iplBinauralEffectRelease(ref effect);
            return false;
        }
        state = s;
        return true;
    }

    private void ReleaseSteamAudioVoice(ActiveSound a)
    {
        if (a.SaState == null) return;
        if (a.SaDsp.hasHandle()) { a.SaDsp.release(); a.SaDsp = default; }
        if (a.SaHandle.IsAllocated) a.SaHandle.Free();
        Phonon.iplAudioBufferFree(_saContext, ref a.SaState.InBuf);
        Phonon.iplAudioBufferFree(_saContext, ref a.SaState.OutBuf);
        IntPtr eff = a.SaState.Effect;
        Phonon.iplBinauralEffectRelease(ref eff);
        a.SaState = null;
    }

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
            }
        }
    }

    private void ClearReverbBuses()
    {
        foreach (var dsp in _reverbDsps.Values) dsp.release();
        foreach (var bus in _reverbBuses.Values) bus.release();
        _reverbDsps.Clear(); _reverbBuses.Clear(); _reverbVolumes.Clear();
    }

    private void UpdateActiveReverbs(Vector3 listenerPos)
    {
        if (_acousticMap == null) return;
        
        _activeRegionIds.Clear(); var activeIds = _activeRegionIds;
        
        foreach (var kvp in _acousticMap.Regions)
        {
            int regionId = kvp.Key;
            // Global Environment is always active
            if (regionId == _acousticMap.GlobalEnvironmentId) { activeIds.Add(regionId); continue; }
            
            if (_acousticMap.RegionPositions.TryGetValue(regionId, out var regPos))
            {
                if (Vector3.Distance(listenerPos, regPos) < AcousticConstants.ActiveRegionRadius)
                    activeIds.Add(regionId);
            }
        }
        if (_listenerRegionId != AcousticConstants.GlobalRegionId && _listenerRegionId != -1) activeIds.Add(_listenerRegionId);

        // Add adjacent regions through portals
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
            if (!_reverbBuses.ContainsKey(id)) CreateReverbBus(id);
        }
    }

    private void CreateReverbBus(int regionId)
    {
        if (_acousticMap == null) return;
        if (!_acousticMap.Regions.TryGetValue(regionId, out var region)) return;

        string busName = (region.FriendlyName ?? "Unknown") + "_Reverb";
        _system.createChannelGroup(busName, out var bus);
        _system.createDSPByType(DSP_TYPE.SFXREVERB, out var reverbDsp);
        
        float decayMs = AcousticConstants.DefaultReverbDecayMs;
        float volume = Math.Max(0.01f, region.RoomSize.X * region.RoomSize.Y * region.RoomSize.Z);
        
        // --- Sabin's Multi-Material Calculation ---
        // Materials index: 0=Floor, 1=Ceiling, 2=North, 3=South, 4=East, 5=West
        float areaFloorCeil = region.RoomSize.X * region.RoomSize.Z;
        float areaNS = region.RoomSize.X * region.RoomSize.Y;
        float areaEW = region.RoomSize.Z * region.RoomSize.Y;
        
        float totalAbsorption = 0;
        
        // Helper to get absorption safely
        float GetAbs(int idx) {
            if (region.Materials == null || idx >= region.Materials.Length) 
                return AcousticRegistry.GetProperties("Generic").Absorption;
            return AcousticRegistry.GetPropertiesByResonanceIndex(region.Materials[idx]).Absorption;
        }

        totalAbsorption += areaFloorCeil * GetAbs(0); // Floor
        totalAbsorption += areaFloorCeil * GetAbs(1); // Ceiling
        totalAbsorption += areaNS * GetAbs(2);        // North
        totalAbsorption += areaNS * GetAbs(3);        // South
        totalAbsorption += areaEW * GetAbs(4);        // East
        totalAbsorption += areaEW * GetAbs(5);        // West

        totalAbsorption = Math.Max(0.01f, totalAbsorption);

        if (totalAbsorption > 0.01f) 
            decayMs = Math.Clamp(0.161f * volume / totalAbsorption * 1000.0f, AcousticConstants.MinReverbDecayMs, AcousticConstants.MaxReverbDecayMs);
        
        decayMs *= Math.Max(0.0f, region.ReverbTimeScale);
        
        reverbDsp.setParameterFloat(0, Math.Max(AcousticConstants.MinReverbDecayMs, decayMs)); 
        reverbDsp.setParameterFloat(1, 0.1f); 
        
        if (region.ReverbTimeScale > 0.01f) {
            reverbDsp.setParameterFloat(11, 0.0f); // Wet level normal
        } else {
            reverbDsp.setParameterFloat(11, -80.0f); // Mute Reverb
        }
        reverbDsp.setParameterFloat(12, 0.0f); // Dry level normal
        
        bus.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, reverbDsp);
        
        float initialVol = (regionId == _listenerRegionId) ? 1.0f : 0.0f;
        bus.setVolume(initialVol); 
        _reverbBuses[regionId] = bus; 
        _reverbDsps[regionId] = reverbDsp; 
        _reverbVolumes[regionId] = initialVol;
        _system.getMasterChannelGroup(out var master);
        master.addGroup(bus);
    }

    public void PlaySpatialSound(SpatialEmitter emitter)
    {
        bool loopNative = emitter.Mode == PlaybackMode.LoopOne;
        lock (_lock)
        {
            var active = _activeSounds.FirstOrDefault(s => s.EntityId == emitter.EntityId);
            if (active != null)
            {
                if (emitter.IsGranular && active.GranularDsp.hasHandle() && active.SoundId == emitter.SoundId)
                {
                    UpdateSpatialAttributes(emitter);
                    return;
                }
                if (emitter.IsSynth && active.SynthDsp.hasHandle())
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
            channel.setMode(MODE._3D | MODE._3D_LINEARROLLOFF);
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
            channel.setMode(MODE._3D | MODE._3D_LINEARROLLOFF);
        }
        else
        {
            if (!_isInitialized || !_resources.TryGetSound(emitter.SoundId, out FMOD.Sound sound, loopNative)) return;
            if (_system.playSound(sound, targetGroup, true, out channel) != RESULT.OK) return;
            channel.setMode(MODE._3D | MODE._3D_LINEARROLLOFF);
        }

        FMOD.DSP threeEqDsp = default, diffractionDsp = default;
        SteamAudioVoiceState? saState = null;
        FMOD.DSP saDsp = default;
        System.Runtime.InteropServices.GCHandle saHandle = default;
        if (emitter.Type != EmitterType.UI)
        {
            threeEqDsp = GetThreeEqDsp();
            channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, threeEqDsp);
            diffractionDsp = GetDiffractionDsp();
            channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, diffractionDsp);
            if (_steamAudioEnabled && TryCreateSteamAudioVoice(out saState, out saDsp, out saHandle))
            {
                // Steam Audio binaural sits last in the chain (after occlusion EQ + diffraction),
                // turning the filtered mono into an HRTF stereo pair. FMOD's own panner is muted.
                channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, saDsp);
                channel.set3DLevel(0.0f);
            }
            else
            {
                channel.set3DLevel(1.0f);
            }
            channel.set3DMinMaxDistance(emitter.MinDistance, emitter.Range);
            if (emitter.ConeInside < 360f)
            {
                channel.set3DConeSettings(emitter.ConeInside, emitter.ConeOutside, emitter.ConeOutsideVolume);
                FMOD.VECTOR fdir = FmodHelpers.ToFmodVec(emitter.Direction);
                channel.set3DConeOrientation(ref fdir);
            }
        }
        else { channel.set3DLevel(0.0f); }
        
        channel.setVolume(emitter.Volume);
        channel.setPitch(emitter.IsGranular || emitter.IsSynth ? 1.0f : emitter.Pitch); 

        if (emitter.DelayMs > 0)
        {
            _system.getSoftwareFormat(out int rate, out _, out _);
            channel.getDSPClock(out ulong dspclock, out _);
            channel.setDelay(dspclock + (ulong)(rate * (emitter.DelayMs / 1000.0f)), 0, false);
        }
        
        lock (_lock) { 
            var activeSound = new ActiveSound { 
                EntityId = emitter.EntityId, SoundId = emitter.SoundId, Type = emitter.Type, 
                Channel = channel, ThreeEqDsp = threeEqDsp, DiffractionDsp = diffractionDsp,
                GranularDsp = granularDsp, GranularHandle = granularHandle, GranularState = granularState,
                SynthDsp = synthDsp, SynthHandle = synthHandle, SynthState = synthState,
                Position = emitter.Position, ApparentPosition = emitter.ApparentPosition, 
                CurrentApparentPosition = (emitter.ApparentPosition != Vector3.Zero) ? emitter.ApparentPosition : emitter.Position, 
                EffectiveDistance = emitter.EffectiveDistance, Velocity = emitter.Velocity, Direction = emitter.Direction, 
                Range = emitter.Range, MinDistance = emitter.MinDistance, BaseVolume = emitter.Volume, Pitch = emitter.Pitch,
                TargetOcclusion = emitter.Occlusion, 
                CurrentOcclusion = emitter.Occlusion,
                TargetAperture = emitter.ApertureFactor, CurrentAperture = emitter.ApertureFactor,
                TargetBleed = emitter.TransmissionBleed, CurrentBleed = emitter.TransmissionBleed,
                AirAbsorption = 0.0f, 
                TargetLow = emitter.EqLow, CurrentLow = emitter.EqLow,
                TargetMid = emitter.EqMid, CurrentMid = emitter.EqMid,
                TargetHigh = emitter.EqHigh, CurrentHigh = emitter.EqHigh,
                TargetRegionId = emitter.TargetRegionId, IsReflection = emitter.IsReflection,
                RoomGain = 1.0f,
                ConeInside = emitter.ConeInside, ConeOutside = emitter.ConeOutside, ConeOutsideVolume = emitter.ConeOutsideVolume,
                ReflectionSpread = emitter.ReflectionSpread,
                SaState = saState, SaDsp = saDsp, SaHandle = saHandle
            };

            if (_acousticMap != null && !activeSound.IsReflection) // Reflections should not feed back into reverb
            {
                int sourceRegionId = activeSound.TargetRegionId;
                if (sourceRegionId != -1 && _reverbBuses.TryGetValue(sourceRegionId, out var sourceBus))
                {
                    activeSound.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var channelDsp);
                    sourceBus.getDSP(CHANNELCONTROL_DSP_INDEX.HEAD, out var busDsp);
                    busDsp.addInput(channelDsp, out activeSound.SourceReverbConnection, DSPCONNECTION_TYPE.SEND);
                    activeSound.SourceReverbConnection.setMix(0.1f);
                    activeSound.CurrentSourceRegionId = sourceRegionId;
                }

                if (_listenerRegionId != -1 && _reverbBuses.TryGetValue(_listenerRegionId, out var listenerBus))
                {
                    activeSound.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var channelDsp);
                    listenerBus.getDSP(CHANNELCONTROL_DSP_INDEX.HEAD, out var busDsp);
                    busDsp.addInput(channelDsp, out activeSound.ReverbConnection, DSPCONNECTION_TYPE.SEND);
                    activeSound.ReverbConnection.setMix(0.1f);
                    activeSound.CurrentRegionId = _listenerRegionId;
                }
            }

            _activeSounds.Add(activeSound); 
        }
        channel.setPaused(false);
    }

    public void UpdateSpatialAttributes(SpatialEmitter emitter) 
    { 
        lock (_lock) 
        { 
            var active = _activeSounds.FirstOrDefault(s => s.EntityId == emitter.EntityId); 
            if (active != null) 
            { 
                active.Position = emitter.Position; active.ApparentPosition = emitter.ApparentPosition; 
                active.EffectiveDistance = emitter.EffectiveDistance; active.Velocity = emitter.Velocity; 
                active.Direction = emitter.Direction; active.Range = emitter.Range; 
                active.BaseVolume = emitter.Volume; 
                if (emitter.IsGranular && active.GranularState != null)
                {
                    active.GranularState.Position = emitter.GranularPosition;
                    active.GranularState.GrainSizeMs = emitter.GranularGrainSizeMs;
                    active.GranularState.Density = emitter.GranularDensity;
                    active.GranularState.Pitch = emitter.GranularPitch;
                    active.GranularState.PositionJitter = emitter.GranularPositionJitter;
                    active.GranularState.PitchJitter = emitter.GranularPitchJitter;
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
                active.MinDistance = emitter.MinDistance;
                active.Channel.set3DMinMaxDistance(active.MinDistance, emitter.Range);
                if (emitter.ConeInside < 360f)
                {
                    active.Channel.set3DConeSettings(emitter.ConeInside, emitter.ConeOutside, emitter.ConeOutsideVolume);
                    FMOD.VECTOR fdir = FmodHelpers.ToFmodVec(emitter.Direction);
                    active.Channel.set3DConeOrientation(ref fdir);
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
            foreach (var active in _activeSounds) 
            {
                if (active.EntityId == entityId) 
                { 
                    active.TargetOcclusion = path.Occlusion; 
                    active.ApparentPosition = path.ApparentPosition; 
                    active.EffectiveDistance = path.EffectiveDistance; 
                    active.TargetAperture = path.ApertureFactor;
                    active.TargetBleed = path.TransmissionBleed; 
                    active.AirAbsorption = path.AirAbsorption;
                    active.TargetRegionId = path.RegionId; 
                    active.TargetLow = path.EqLow;
                    active.TargetMid = path.EqMid; 
                    active.TargetHigh = path.EqHigh;
                    active.RoomGain = path.RoomGain;
                } 
            } 
        } 
    }

    public void Update()
    {
        if (!_isInitialized) return;
        
        try
        {
            Vector3 lPosVec = _listenerPos;
            int listenerRegionId = _listenerRegionId;

            lock (_lock)
            {
                UpdateActiveReverbs(lPosVec);

                for (int i = _activeSounds.Count - 1; i >= 0; i--)
                {
                    var active = _activeSounds[i];
                    active.Channel.isPlaying(out bool isPlaying);
                    if (!isPlaying) { 
                        ReleaseActiveSoundResources(active);
                        _activeSounds.RemoveAt(i); continue; 
                    }
                    
                    if (active.Type == EmitterType.UI) continue;

                    UpdateReverbRouting(active, listenerRegionId);
                    UpdateSpatialPositioning(active, lPosVec);
                    ApplyAcousticFilters(active, lPosVec);
                }

                UpdateReverbBuses(lPosVec, listenerRegionId);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in FmodAudioProvider.Update");
        }
        finally
        {
            _system.update();
        }
    }

    private void ReleaseActiveSoundResources(ActiveSound active)
    {
        ReleaseSteamAudioVoice(active);
        ReleaseThreeEqDsp(active.ThreeEqDsp);
        ReleaseDiffractionDsp(active.DiffractionDsp);
        ReleaseGranularDsp(active.GranularDsp, active.GranularHandle, active.GranularState);
        ReleaseSynthDsp(active.SynthDsp, active.SynthHandle, active.SynthState);
    }

    private void UpdateReverbRouting(ActiveSound active, int listenerRegionId)
    {
        if (_acousticMap == null) return;
        int sourceRegionId = active.TargetRegionId;
        
        // Update Source Reverb Send (the room the sound is in)
        if (sourceRegionId != active.CurrentSourceRegionId || !active.SourceReverbConnection.hasHandle())
        {
            if (active.SourceReverbConnection.hasHandle()) active.SourceReverbConnection.setMix(0.0f);
            if (sourceRegionId != -2 && _reverbBuses.TryGetValue(sourceRegionId, out var sourceBus)) 
            { 
                if (!active.IsReflection)
                {
                    active.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var channelDsp);
                    sourceBus.getDSP(CHANNELCONTROL_DSP_INDEX.HEAD, out var busDsp);
                    busDsp.addInput(channelDsp, out active.SourceReverbConnection, DSPCONNECTION_TYPE.SEND); 
                    active.SourceReverbConnection.setMix(0.1f);
                }
            }
            active.CurrentSourceRegionId = sourceRegionId;
        }

        // Update Listener Reverb Send (the room the listener is in)
        if (listenerRegionId != active.CurrentRegionId || !active.ReverbConnection.hasHandle())
        {
            if (active.ReverbConnection.hasHandle()) active.ReverbConnection.setMix(0.0f);
            if (listenerRegionId != -2 && _reverbBuses.TryGetValue(listenerRegionId, out var listenerBus))
            {
                if (!active.IsReflection)
                {
                    active.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var channelDsp);
                    listenerBus.getDSP(CHANNELCONTROL_DSP_INDEX.HEAD, out var busDsp);
                    busDsp.addInput(channelDsp, out active.ReverbConnection, DSPCONNECTION_TYPE.SEND);
                    active.ReverbConnection.setMix(0.1f);
                }
            }
            active.CurrentRegionId = listenerRegionId;
        }
    }

    private void UpdateSpatialPositioning(ActiveSound active, Vector3 lPosVec)
    {
        Vector3 targetPos = (active.ApparentPosition != Vector3.Zero) ? active.ApparentPosition : active.Position;
        if (active.ApparentPosition != Vector3.Zero && active.ApparentPosition != active.Position)
        {
            float bias = Math.Clamp(active.CurrentBleed / (active.CurrentBleed + active.CurrentAperture + 0.001f), 0f, 1f);
            targetPos = Vector3.Lerp(active.ApparentPosition, active.Position, bias);
        }

        active.CurrentApparentPosition = Vector3.Lerp(active.CurrentApparentPosition, targetPos, 0.15f);
        FMOD.VECTOR fpos = FmodHelpers.ToFmodVec(active.CurrentApparentPosition), fvel = FmodHelpers.ToFmodVec(active.Velocity);
        active.Channel.set3DAttributes(ref fpos, ref fvel);
        active.Channel.set3DMinMaxDistance(active.MinDistance, active.Range);

        float distToSound = Vector3.Distance(lPosVec, active.CurrentApparentPosition);
        bool isIndirect = active.ApparentPosition != Vector3.Zero && active.ApparentPosition != active.Position;
        
        if (isIndirect)
        {
            // --- PHASE 3: Precision Panning ---
            // Indirect paths (diffraction) fill the room based on aperture size
            float roomFillSpread = Math.Clamp((distToSound * AcousticConstants.SpreadGrowthFactor), 0.0f, AcousticConstants.VolumetricSpreadMax);
            float baseSpread = (active.CurrentAperture * 90.0f) / Math.Max(0.5f, distToSound);
            active.Channel.set3DSpread(Math.Min(baseSpread, roomFillSpread));
            active.Channel.set3DLevel(1.0f); 
        }
        else if (active.IsReflection)
        {
            // --- PHASE 3: Scattering-Based Spread ---
            // High-order reflections on smooth surfaces should be pinpoints. 
            // Only use high spread for rough materials.
            active.Channel.set3DSpread(active.ReflectionSpread);
            active.Channel.set3DLevel(1.0f);
        }
        else
        {
            active.Channel.set3DSpread(0.0f);
            active.Channel.set3DLevel(1.0f);
        }

        if (active.SaState != null)
        {
            // Steam Audio owns directional panning (override the set3DLevel above); FMOD still
            // applies distance attenuation via set3DMinMaxDistance. Feed the listener-relative
            // direction, converted from the game frame (+Z fwd) to Steam Audio's (-Z fwd).
            active.Channel.set3DLevel(0.0f);
            Vector3 local = Vector3.Transform(active.CurrentApparentPosition - lPosVec, Quaternion.Conjugate(_listenerRot));
            float len = local.Length();
            if (len > 1e-4f)
            {
                active.SaState.DirX = local.X / len;
                active.SaState.DirY = local.Y / len;
                active.SaState.DirZ = -local.Z / len;
            }
        }
    }

    private void ApplyAcousticFilters(ActiveSound active, Vector3 lPosVec)
    {
        float dt = 0.016f; 
        float lerpFactor = 1.0f - MathF.Exp(-dt / AcousticConstants.ParameterSmoothingTimeConstant); 
        
        active.CurrentOcclusion = MathHelper.Lerp(active.CurrentOcclusion, active.TargetOcclusion, lerpFactor);
        active.CurrentAperture = MathHelper.Lerp(active.CurrentAperture, active.TargetAperture, lerpFactor);
        active.CurrentBleed = MathHelper.Lerp(active.CurrentBleed, active.TargetBleed, lerpFactor);
        active.CurrentLow = MathHelper.Lerp(active.CurrentLow, active.TargetLow, lerpFactor);
        active.CurrentMid = MathHelper.Lerp(active.CurrentMid, active.TargetMid, lerpFactor);
        active.CurrentHigh = MathHelper.Lerp(active.CurrentHigh, active.TargetHigh, lerpFactor);

        float totalMuffle = Math.Clamp(active.CurrentOcclusion + active.AirAbsorption, 0.0f, 0.80f);
        
        // Fix: Volume should be a combination of the 'dry' direct path and the 'bleed' through walls.
        // We use a weighted model where bleed is significantly quieter than direct sound.
        float dryVol = Math.Max(0.0f, 1.0f - active.CurrentOcclusion);
        float bleedVol = active.CurrentBleed * AcousticConstants.TransmissionBleedFactor;
        float finalVolFactor = Math.Max(dryVol, bleedVol * 0.5f); 
        
        // --- Shelter Damping ---
        // Atmospheric sounds (Wind, Rain) are aggressively dampened when sheltered.
        // Physical world sounds (Megaphones, NPCs) are NOT double-dampened here as they rely on Occlusion/Diffraction.
        if (active.Type == EmitterType.Atmospheric)
        {
            finalVolFactor *= (1.0f - (_shelterFactor * 0.95f)); // Keep 5% for "interior rain" sense
        }
        else if (_shelterFactor > 0.01f && active.TargetRegionId != _listenerRegionId)
        {
            // If we are sheltered and the sound is from another region, add a subtle extra damping
            // to simulate the building's structural insulation.
            finalVolFactor *= (1.0f - (_shelterFactor * 0.2f));
        }

        float roomGainBonus = MathHelper.Lerp(1.0f, active.RoomGain, 0.5f);

        // Distance attenuation: when Steam Audio drives panning (set3DLevel 0), FMOD does NOT apply
        // its 3D rolloff, so we apply linear distance falloff ourselves (verified by RunDistanceCheck).
        float distAtten = 1.0f;
        if (active.SaState != null)
        {
            float dist = Vector3.Distance(lPosVec, active.CurrentApparentPosition);
            float span = MathF.Max(0.01f, active.Range - active.MinDistance);
            distAtten = Math.Clamp(1.0f - (dist - active.MinDistance) / span, 0.0f, 1.0f);
        }

        active.Channel.setVolume(active.BaseVolume * finalVolFactor * roomGainBonus * distAtten);
        
        if (active.ThreeEqDsp.hasHandle()) 
        {
            Vector3 toSound = Vector3.Normalize(active.CurrentApparentPosition - lPosVec);
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, _listenerRot);
            Vector3 right = Vector3.Transform(Vector3.UnitX, _listenerRot);

            float lowDb = MathHelper.Lerp(AcousticConstants.OcclusionMaxLowMuffleDb, 0.0f, active.CurrentLow);
            float midDb = MathHelper.Lerp(AcousticConstants.OcclusionMaxMidMuffleDb, 0.0f, active.CurrentMid);
            float highDb = MathHelper.Lerp(AcousticConstants.OcclusionMaxHighMuffleDb, 0.0f, active.CurrentHigh);

            // Extra muffle for environmental sounds when sheltered
            if (active.TargetRegionId == AcousticConstants.GlobalRegionId)
            {
                highDb -= (_shelterFactor * 40.0f);
                midDb -= (_shelterFactor * 20.0f);
            }

            highDb -= (totalMuffle * 40.0f); midDb -= (totalMuffle * 20.0f);
            lowDb += (active.CurrentBleed * 10.0f);

            active.ThreeEqDsp.setParameterFloat(0, Math.Clamp(lowDb, -80.0f, 10.0f));
            active.ThreeEqDsp.setParameterFloat(1, Math.Clamp(midDb, -80.0f, 10.0f));
            active.ThreeEqDsp.setParameterFloat(2, Math.Clamp(highDb, -80.0f, 10.0f));
        }

        float distFactor = Math.Clamp(active.EffectiveDistance / 50.0f, 0.0f, 1.0f);
        float baseReverbMix = 0.25f + (MathF.Sqrt(distFactor) * 0.65f);
        
        // --- Reverb-Reflection Coupling ---
        // For reflections, the 'RoomGain' property actually carries the 'Remaining Energy' 
        // after all bounces. We use this to excite the reverb bus.
        if (active.IsReflection)
        {
            baseReverbMix *= Math.Max(0.5f, active.RoomGain);
        }

        if (active.SourceReverbConnection.hasHandle()) active.SourceReverbConnection.setMix(baseReverbMix * 0.8f);
        if (active.ReverbConnection.hasHandle()) active.ReverbConnection.setMix(baseReverbMix * 0.4f);

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

    private const int MaxReverbSlots = 4;
    private readonly Dictionary<int, int> _regionToSlot = new();
    private readonly int[] _slotToRegion = new int[MaxReverbSlots];
    private readonly FMOD.ChannelGroup[] _reverbSlots = new FMOD.ChannelGroup[MaxReverbSlots];
    private readonly FMOD.DSP[] _reverbSlotDsps = new FMOD.DSP[MaxReverbSlots];
    private readonly float[] _slotVolumes = new float[MaxReverbSlots];

    private void UpdateReverbBuses(Vector3 lPosVec, int listenerRegionId)
    {
        if (_acousticMap == null) return;
        
        // 1. Identify candidate regions for reverb (Top N nearest)
        var candidates = _activeRegionIds
            .Select(id => new { 
                Id = id, 
                Dist = (id == _acousticMap.GlobalEnvironmentId) ? 0 : Vector3.Distance(lPosVec, _acousticMap.RegionPositions.GetValueOrDefault(id, Vector3.Zero)),
                Priority = (id == listenerRegionId) ? 0 : 1
            })
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.Dist)
            .Take(MaxReverbSlots)
            .ToList();

        // 2. Manage bus volumes directly instead of slots for now to fix the handles
        // We will fade out any bus that isn't a candidate
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
                    bus.set3DLevel(0.0f);
                }
                else
                {
                    // Leakage through portals
                    var portals = _acousticMap.Portals.Values.Where(p => 
                        (p.Portal.RegionAId == regionId && p.Portal.RegionBId == listenerRegionId) || 
                        (p.Portal.RegionAId == listenerRegionId && p.Portal.RegionBId == regionId) ||
                        (listenerRegionId == _acousticMap.GlobalEnvironmentId && (p.Portal.RegionAId == regionId || p.Portal.RegionBId == regionId))
                    );
                    
                    var nearest = portals.OrderBy(p => Vector3.Distance(lPosVec, p.Position)).FirstOrDefault();
                    if (nearest.Portal.ApertureSize > 0)
                    {
                        float dist = Vector3.Distance(lPosVec, nearest.Position);
                        targetVol = Math.Clamp((nearest.Portal.ApertureSize / 2.0f) / Math.Max(1.0f, dist), 0.0f, 1.0f);
                        
                        bus.set3DLevel(1.0f);
                        FMOD.VECTOR fpos = FmodHelpers.ToFmodVec(nearest.Position);
                        FMOD.VECTOR fvel = new FMOD.VECTOR { x = 0, y = 0, z = 0 };
                        bus.set3DAttributes(ref fpos, ref fvel);
                    }
                }
            }

            _reverbVolumes[regionId] = MathHelper.Lerp(_reverbVolumes[regionId], targetVol, AcousticConstants.ReverbFadeSpeed);
            bus.setVolume(_reverbVolumes[regionId]);
        }
    }

    private int FindBestSlotToLease(HashSet<int> preferred)
    {
        for (int i = 0; i < MaxReverbSlots; i++) if (_slotToRegion[i] == -2) return i;
        // Priority-based eviction (oldest/quietest)
        return 0; 
    }

    private void LeaseSlot(int slotIdx, int regionId)
    {
        if (_reverbSlots[slotIdx].hasHandle()) {
            ConfigureReverbDsp(_reverbSlotDsps[slotIdx], regionId);
            _slotToRegion[slotIdx] = regionId;
            _regionToSlot[regionId] = slotIdx;
            _slotVolumes[slotIdx] = 0.0f; // Start silent for fade-in
        }
    }

    private void EvictSlot(int slotIdx)
    {
        int regId = _slotToRegion[slotIdx];
        if (regId != -2) {
            _regionToSlot.Remove(regId);
            _slotToRegion[slotIdx] = -2;
        }
    }

    private void ConfigureReverbDsp(FMOD.DSP dsp, int regionId)
    {
        if (_acousticMap == null) return;
        
        if (regionId != -1)
        {
            var region = _acousticMap.Regions[regionId];
            float decayMs = AcousticConstants.DefaultReverbDecayMs;
            var matProps = (region.Materials != null && region.Materials.Length > 0) ? AcousticRegistry.GetPropertiesByResonanceIndex(region.Materials[0]) : AcousticRegistry.GetProperties("Generic");
            float volume = region.RoomSize.X * region.RoomSize.Y * region.RoomSize.Z;
            float surfaceArea = 2 * (region.RoomSize.X * region.RoomSize.Y + region.RoomSize.Y * region.RoomSize.Z + region.RoomSize.X * region.RoomSize.Z);
            float totalAbs = surfaceArea * matProps.Absorption;
            if (totalAbs > 0.01f) decayMs = Math.Clamp(0.161f * volume / totalAbs * 1000.0f, AcousticConstants.MinReverbDecayMs, AcousticConstants.MaxReverbDecayMs);
            decayMs *= Math.Max(0.1f, region.ReverbTimeScale);
            
            dsp.setBypass(false);
            dsp.setParameterFloat(0, decayMs);
            dsp.setParameterFloat(11, 0.0f); // Wet 0dB
            dsp.setParameterFloat(12, 0.0f); // Dry 0dB
        }
        else 
        { 
            // Fix: Truly bypass the DSP for the Outside region
            dsp.setBypass(true);
        }
    }

    private void UpdateSlotAcoustics(int slotIdx, int regionId, Vector3 lPosVec, int listenerRegionId)
    {
        float targetVol = (regionId == listenerRegionId) ? 1.0f : 0.0f;
        if (regionId != listenerRegionId)
        {
            var portals = _acousticMap!.Portals.Values.Where(p => (p.Portal.RegionAId == regionId && p.Portal.RegionBId == listenerRegionId) || (p.Portal.RegionAId == listenerRegionId && p.Portal.RegionBId == regionId));
            var nearest = portals.OrderBy(p => Vector3.Distance(lPosVec, p.Position)).FirstOrDefault();
            if (nearest.Portal.ApertureSize > 0) targetVol = Math.Clamp((nearest.Portal.ApertureSize / 2.0f) / Math.Max(1.0f, Vector3.Distance(lPosVec, nearest.Position)), 0.0f, 1.0f);
        }

        _slotVolumes[slotIdx] = MathHelper.Lerp(_slotVolumes[slotIdx], targetVol, AcousticConstants.ReverbFadeSpeed);
        _reverbSlots[slotIdx].setVolume(_slotVolumes[slotIdx]);

        if (regionId == listenerRegionId) _reverbSlots[slotIdx].set3DLevel(0.0f);
        else {
            _reverbSlots[slotIdx].set3DLevel(1.0f);
            // Position at portal for leakage
        }
    }


    public void UpdateListener(Vector3 position, Quaternion rotation, Vector3 velocity, int regionId)
    {
        if (!_isInitialized) return;
        _listenerPos = position; _listenerRot = rotation; _listenerVel = velocity; _listenerRegionId = regionId;
        FMOD.VECTOR fpos = FmodHelpers.ToFmodVec(position), fvel = FmodHelpers.ToFmodVec(velocity);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        Vector3 up = Vector3.Transform(Vector3.UnitY, rotation);
        FMOD.VECTOR ffwd = FmodHelpers.ToFmodVec(forward), fup = FmodHelpers.ToFmodVec(up);
        _system.set3DListenerAttributes(0, ref fpos, ref fvel, ref ffwd, ref fup);
    }

    public void UpdateShelter(float shelterFactor) => _shelterFactor = shelterFactor;

    private float _currentProximityMix = 0.0f;

    public void UpdateProximity(float distance)
    {
        if (!_isInitialized || !_masterCombFilter.hasHandle()) return;
        
        float targetMix = distance >= 1.5f ? 0.0f : 1.0f - (distance / 1.5f);
        _currentProximityMix = MathHelper.Lerp(_currentProximityMix, targetMix, 0.15f); // Faster response

        // --- PHASE 2: DSP Smoothing ---
        // Instead of hard-toggling bypass (which causes pops), we always leave the DSP 
        // active but fade the Wet Level to silence (-80dB).
        float wetLevel = MathHelper.Lerp(-80.0f, -4.0f, _currentProximityMix); 
        
        if (_currentProximityMix > 0.001f)
        {
            _masterCombFilter.setBypass(false);
            // Pressure simulation: very short delay (comb filter)
            float delayMs = MathHelper.Lerp(0.1f, 1.2f, distance / 1.5f);
            _masterCombFilter.setParameterFloat(0, delayMs);
            _masterCombFilter.setParameterFloat(1, 45.0f); // 45% feedback for metallic resonance
            _masterCombFilter.setParameterFloat(3, wetLevel);
        }
        else
        {
            _masterCombFilter.setParameterFloat(3, -80.0f);
            _masterCombFilter.setBypass(true);
        }
    }

    public void StopSound(int entityId) { 
        lock (_lock) { 
            for (int i = _activeSounds.Count - 1; i >= 0; i--) {
                if (_activeSounds[i].EntityId == entityId) { 
                    _activeSounds[i].Channel.stop(); 
                    ReleaseActiveSoundResources(_activeSounds[i]);
                    _activeSounds.RemoveAt(i); 
                } 
            } 
        } 
    }
    
    public bool IsPlaying(int entityId) { lock (_lock) return _activeSounds.Any(s => s.EntityId == entityId); }
    public float GetPlaybackProgress(int entityId) { 
        lock (_lock) { 
            var sound = _activeSounds.FirstOrDefault(s => s.EntityId == entityId); 
            if (sound == null) return 0f; 
            sound.Channel.getCurrentSound(out var fmodSound); 
            if (!fmodSound.hasHandle()) return 0f; 
            fmodSound.getLength(out uint len, TIMEUNIT.MS); 
            sound.Channel.getPosition(out uint pos, TIMEUNIT.MS); 
            return (len == 0) ? 0f : (float)pos / len; 
        } 
    }
    public IEnumerable<int> GetActiveSpatialSoundIds() { lock(_lock) return _activeSounds.Select(s => s.EntityId).Distinct().ToList(); }
    public Vector3 GetSoundPosition(int entityId) { lock(_lock) return _activeSounds.FirstOrDefault(s => s.EntityId == entityId)?.Position ?? Vector3.Zero; }

    public void Preload(string soundId)
    {
        if (!_isInitialized) return;

        // Try preloading as a granular sound first
        _granularBank.TryGetPcmData(soundId, out _, out _, out _);

        // Also preload as a standard FMOD sound
        _resources.TryGetSound(soundId, out _, false);
    }

    /// <summary>
    /// Plays a decoded 16-bit mono 48kHz PCM voice packet as a one-shot 3D sound.
    /// Copies the data into FMOD's internal buffers (MODE.OPENMEMORY) so no pinning is needed.
    /// </summary>
    public void PlayVoice(int senderId, Vector3 position, byte[] pcmData)
    {
        if (!_isInitialized || pcmData.Length == 0) return;

        var info = new CREATESOUNDEXINFO
        {
            cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(),
            length = (uint)pcmData.Length,
            numchannels = 1,
            defaultfrequency = 48000,
            format = SOUND_FORMAT.PCM16
        };

        // OPENRAW is required for headerless PCM in memory; without it FMOD tries to parse a file
        // header and createSound fails (voice was silently dropped).
        RESULT res = _system.createSound(pcmData, MODE.OPENMEMORY | MODE.OPENRAW | MODE._3D | MODE._3D_LINEARROLLOFF | MODE.LOOP_OFF, ref info, out FMOD.Sound sound);
        if (res != RESULT.OK) return;

        _system.playSound(sound, default, true, out FMOD.Channel ch);
        ch.setMode(MODE._3D | MODE._3D_LINEARROLLOFF);
        ch.set3DMinMaxDistance(1.0f, 30.0f);

        var fpos = FmodHelpers.ToFmodVec(position);
        var fvel = new FMOD.VECTOR();
        ch.set3DAttributes(ref fpos, ref fvel);
        ch.setVolume(1.0f);
        ch.setPaused(false);

        // Release the sound object once it finishes — FMOD holds its own copy of the PCM.
        // We queue a callback via the active sound list so it is released in the Update loop.
        lock (_lock)
        {
            _activeSounds.Add(new ActiveSound
            {
                EntityId = -(senderId + 90000), // negative offset to avoid collision
                SoundId = "__voice__",
                Type = EmitterType.WorldLocked,
                Channel = ch,
                Position = position,
                ApparentPosition = position,
                BaseVolume = 1.0f,
                Range = 30.0f,
                IsReflection = false,
                TargetRegionId = -1
            });
        }

        // The sound handle is managed by FMOD; it auto-releases when playback ends.
        sound.release();
    }

    /// <summary>
    /// Plays a short synthesized sine tone as a non-spatial UI sound.
    /// Generates PCM data directly rather than going through the full synth DSP pipeline.
    /// </summary>
    public void PlayUiBeep(float frequencyHz, float durationMs)
    {
        if (!_isInitialized) return;

        int sampleRate = 44100;
        int numSamples = (int)(sampleRate * durationMs / 1000.0f);
        var pcm = new byte[numSamples * 2]; // 16-bit

        for (int i = 0; i < numSamples; i++)
        {
            float t = (float)i / sampleRate;
            // Sine wave with a fast fade-out envelope to avoid clicks
            float envelope = Math.Clamp(1.0f - (float)i / numSamples * 2.0f, 0f, 1f);
            float sample = MathF.Sin(2.0f * MathF.PI * frequencyHz * t) * envelope * 0.6f;
            short s = (short)(sample * short.MaxValue);
            pcm[i * 2]     = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        var info = new CREATESOUNDEXINFO
        {
            cbsize = System.Runtime.InteropServices.Marshal.SizeOf<CREATESOUNDEXINFO>(),
            length = (uint)pcm.Length,
            numchannels = 1,
            defaultfrequency = sampleRate,
            format = SOUND_FORMAT.PCM16
        };

        RESULT res = _system.createSound(pcm, MODE.OPENMEMORY | MODE.OPENRAW | MODE.LOOP_OFF, ref info, out FMOD.Sound sound);
        if (res != RESULT.OK) return;

        _system.playSound(sound, default, false, out _);
        sound.release();
    }

    // --- Step 1a diagnostic: a single isolated mono source for verifying HRTF / panning. ---
    // Deliberately bypasses the VoiceManager and the whole acoustics layer so we test ONLY
    // the renderer + listener path. Driven by AudioDiagnostics (`--audio-test`).
    private FMOD.Channel _diagChannel;
    private FMOD.Sound _diagSound;
    private byte[]? _diagPcm;

    public void StartDiagnosticSound()
    {
        if (!_isInitialized) return;
        StopDiagnosticSound();

        // 1 second mono 44.1kHz loop of short broadband noise bursts. Broadband transients
        // localize far better than pure tones, so HRTF / panning cues are unmistakable.
        const int sampleRate = 44100;
        int numSamples = sampleRate;
        _diagPcm = new byte[numSamples * 2];
        const int periodSamples = sampleRate / 8; // 8 bursts per second
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

        // OPENRAW is required for headerless PCM in memory; OPENMEMORY alone would make FMOD
        // try to parse a (non-existent) file header. (The existing voice/beep paths omit OPENRAW
        // and are likely silently broken — out of scope here, flagged for the cleanup pass.)
        if (!FmodCheck(_system.createSound(_diagPcm, MODE.OPENMEMORY | MODE.OPENRAW | MODE._3D | MODE._3D_LINEARROLLOFF | MODE.LOOP_NORMAL, ref info, out _diagSound), "createSound(diagnostic)"))
            return;
        if (!FmodCheck(_system.playSound(_diagSound, default, true, out _diagChannel), "playSound(diagnostic)"))
            return;

        _diagChannel.setMode(MODE._3D | MODE._3D_LINEARROLLOFF | MODE.LOOP_NORMAL);
        _diagChannel.set3DLevel(1.0f); // fully spatialized, no 2D blend
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
        lock (_lock) {
            foreach (var active in _activeSounds) {
                ReleaseActiveSoundResources(active);
            }
            _activeSounds.Clear(); 
            foreach (var dsp in _reverbDsps.Values) dsp.release();
            foreach (var bus in _reverbBuses.Values) bus.release();
            if (_masterCombFilter.hasHandle()) _masterCombFilter.release();
        } 
        StopDiagnosticSound();
        if (_steamAudioEnabled)
        {
            // Per-voice effects were released above via ReleaseActiveSoundResources; now the shared context.
            Phonon.iplHRTFRelease(ref _saHrtf);
            Phonon.iplContextRelease(ref _saContext);
            _steamAudioEnabled = false;
        }
        _resources?.Dispose();
        _granularBank?.Dispose();
        if (_isInitialized) {
            // FMOD requires close() BEFORE release(); the previous order made close() a
            // use-after-free on an already-freed system handle.
            _system.close();
            _system.release();
        }
    }
}

internal static class MathHelper
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}
