using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FMOD;
using Serilog;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.AudioEngine.Fmod;

internal static class FmodHelpers
{
    public static FMOD.VECTOR ToFmodVec(Vector3 v) => new FMOD.VECTOR { x = v.X, y = v.Y, z = v.Z };
}

/// <summary>Outcome of asking the resource manager for a sound. The distinction between "still loading"
/// and "will never exist" is the whole point: a <see cref="Loading"/> sound must be retried, a
/// <see cref="Missing"/> one must be reported. Collapsing the two is what silently ate the first play of
/// every sound in the game.</summary>
internal enum SoundLoadState
{
    /// <summary>Decoded and playable right now.</summary>
    Ready,
    /// <summary>Load started (or still in flight). Ask again shortly.</summary>
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

    /// <summary>
    /// Resolves a sound, kicking off its NONBLOCKING load on first request.
    ///
    /// Sounds are created with <see cref="MODE.NONBLOCKING"/>, so the very first request for an
    /// un-preloaded asset can only ever answer "loading" — the decode has not finished yet. Reporting
    /// that honestly (rather than handing back a not-yet-ready handle that <c>playSound</c> rejects with
    /// ERR_NOTREADY) is what lets the caller retry instead of dropping the play.
    /// </summary>
    public SoundLoadState TryGetSound(string soundId, out FMOD.Sound sound, bool loop = false)
    {
        sound = default;
        if (string.IsNullOrEmpty(soundId)) return SoundLoadState.Missing;

        string cacheKey = soundId + (loop ? "_L" : "");
        if (_cache.TryGetValue(cacheKey, out sound))
        {
            // NONBLOCKING loads complete asynchronously — verify the sound is ready before use.
            sound.getOpenState(out OPENSTATE openState, out _, out _, out _);
            if (openState == OPENSTATE.READY) return SoundLoadState.Ready;
            sound = default;
            if (openState == OPENSTATE.ERROR)
            {
                ReportMissing(soundId, "FMOD reported OPENSTATE.ERROR after loading");
                return SoundLoadState.Missing;
            }
            return SoundLoadState.Loading;
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
            if (!found)
            {
                ReportMissing(soundId, $"no file at '{path}' (.wav/.ogg/.mp3)");
                return SoundLoadState.Missing;
            }
        }

        MODE mode = MODE.CREATESAMPLE | MODE._3D | MODE._3D_LINEARROLLOFF | MODE.NONBLOCKING;
        if (loop) mode |= MODE.LOOP_NORMAL;

        RESULT res = _system.createSound(path, mode, out sound);
        if (res != RESULT.OK)
        {
            sound = default;
            ReportMissing(soundId, $"createSound failed: {res}");
            return SoundLoadState.Missing;
        }

        _cache[cacheKey] = sound;

        // The load has only just been queued; it is never ready on this call. Say so, so the caller can
        // defer the play rather than lose it.
        sound.getOpenState(out OPENSTATE state, out _, out _, out _);
        if (state == OPENSTATE.READY) return SoundLoadState.Ready;
        sound = default;
        return SoundLoadState.Loading;
    }

    /// <summary>Logs an unresolvable sound once per id — a missing asset is a content bug and must be
    /// visible, but it must not spam the log every frame the emitter is in range.</summary>
    private void ReportMissing(string soundId, string reason)
    {
        if (!_reportedMissing.Add(soundId)) return;
        Log.Warning("Audio asset '{SoundId}' cannot be played: {Reason}. That emitter will be silent.", soundId, reason);
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

        // A live vehicle engine, or a reflection of one
        public FMOD.DSP EngineDsp;
        public System.Runtime.InteropServices.GCHandle EngineHandle;
        public EngineVoiceState? EngineState;
        public EngineEchoState? EchoState;

        /// <summary>When this voice's position was last TRUE, seconds on <see cref="OpenFPS.Common.AudioClock"/>
        /// — the sample time carried by the emitter, not the moment it was handed over.
        /// A voice keeps PLAYING whether or not anything repositions it, so a voice nobody updates is
        /// a sound sitting still in the air while the thing making it drives away.</summary>
        public double LastAttributeAt;

        /// <summary>
        /// The oldest this voice's position has been at the moment it was placed, this report interval.
        ///
        /// The figure that matters, and the one nothing measured. Sampling staleness at the instant of
        /// the five-second report can only see a hold that is still going on when the report fires; a
        /// hold that started and ended between two reports — which is every one of them, at 30 Hz —
        /// was invisible. Kept as a MAXIMUM over the interval and reset when it is read, so a single
        /// bad pass cannot hide behind four good seconds.
        /// </summary>
        public double WorstPositionAge;

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

    // The same voices, indexed by the entity that owns them. Every per-frame audio query used to be a
    // linear scan of the list — IsPlaying, GetSoundPosition, and the lookup at the head of both
    // PlaySpatialSound and UpdateSpatialAttributes — and the audio update runs those once per active
    // voice, so the cost of a frame grew as the SQUARE of the number of things making noise. Silencing one
    // removed entity was worse still: ForgetEntity probes a hundred reflection ids, each a full scan.
    // An entity can own more than one voice (a sound and its reflections), hence a list per id.
    private readonly Dictionary<int, List<ActiveSound>> _activeById = new();
    private readonly object _lock = new();
    private AcousticMap? _acousticMap;
    private Dictionary<int, FMOD.ChannelGroup> _reverbBuses = new();
    private Dictionary<int, FMOD.DSP> _reverbDsps = new();
    private Dictionary<int, float> _reverbVolumes = new();
    // Per-bus Steam Audio voice (HRTF) used to localize a room's reverb to its doorway when the listener
    // is OUTSIDE. Bus-lifetime (no churn); borrowed from the voice pool, returned on bus teardown.
    private readonly Dictionary<int, SaVoice> _reverbSaVoices = new();
    private HashSet<int> _activeRegionIds = new();

    private FMOD.ChannelGroup _reflectionGroup;
    /// <summary>One playing ambisonic ambience bed. Several can be live at once so one region's
    /// ambience can cross-fade into another's rather than cutting.</summary>
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
    private FMOD.DSP _loudnessMeter;
    private MasterTap? _masterTap;
    private EngineRenderPool? _enginePool;
    private readonly List<EngineVoiceState> _engineSnapshot = new();

    private readonly List<(float Distance, EngineVoiceState Voice)> _engineOrder = new();

    /// <summary>
    /// Every live engine, for the render pool, NEAREST FIRST. Copied under the lock so the pool never
    /// walks a list the mixer is changing.
    ///
    /// The order is not cosmetic. When the machine cannot render every ring ahead of the mixer — the
    /// first seconds in a map, on a track with a full field — some voice is going to come up short,
    /// and in _activeSounds order which one it is comes down to when the car happened to be added.
    /// Priming the nearest first means the shortfall lands on the car at the far end of the back
    /// straight, which is a whisper, rather than on the one going past your seat.
    /// </summary>
    private List<EngineVoiceState> SnapshotEngineVoices()
    {
        _engineSnapshot.Clear();
        _engineOrder.Clear();
        lock (_lock)
            foreach (var a in _activeSounds)
                if (a.EngineState != null) _engineOrder.Add((a.EffectiveDistance, a.EngineState));
        _engineOrder.Sort(static (x, y) => x.Distance.CompareTo(y.Distance));
        foreach (var e in _engineOrder) _engineSnapshot.Add(e.Voice);
        return _engineSnapshot;
    }

    /// <summary>
    /// Makeup gain on the master, dB. See the gain-staging note in Initialize.
    ///
    /// Set from measurement, not taste. Metered on the speedway — eight cars, four live engines, a
    /// grandstand answering them — against the loudness meter below:
    ///
    /// |  makeup |     short-term | peak      |                                                    |
    /// |---------|----------------|-----------|----------------------------------------------------|
    /// |    0 dB | -29 to -33 LUFS| -14 dBFS  | correct, and twelve decibels under where it belongs |
    /// |    6 dB | -23 to -27     |  -4.5     | clean, still quiet                                  |
    /// |   10 dB | -19 to -23     |  -3       | on target, limiter as a safety net                  |
    /// |   12 dB | -17 to -21     |  -0.5     | limiter engaging                                    |
    /// |   22 dB | -17 to -21     |  -0.1     | no louder than 12, just squashed                    |
    ///
    /// Ten is the last value that buys loudness rather than compression. Games sit at -18 to -23
    /// LUFS and broadcast at -23, so this puts a busy outdoor scene where it belongs with the brick
    /// wall still doing nothing most of the time.
    ///
    /// Raising it past about twelve is pointless — the table shows 22 dB is no louder than 12 — and
    /// the thing to do when a map sounds quiet is read the "Mix loudness" line before changing
    /// anything. Override with OPENFPS_MASTER_MAKEUP_DB.
    /// </summary>
    public static readonly float MasterMakeupDb =
        float.TryParse(Environment.GetEnvironmentVariable("OPENFPS_MASTER_MAKEUP_DB"), out float mk)
            ? Math.Clamp(mk, 0f, 40f) : 10f;

    private Vector3 _listenerPos = Vector3.Zero;
    private Vector3 _listenerVel = Vector3.Zero;
    private Quaternion _listenerRot = Quaternion.Identity;
    private int _listenerRegionId = -1;
    private float _shelterFactor = 0.0f;

    // Geometry-driven reverb decay (ms) from the Steam Audio reflection sim; 0 = keep the Sabine estimate.
    private float _simReverbDecayMs;

    // Opt-in spatialization tracing (OPENFPS_AUDIO_DEBUG=1): logs each spatial source's
    // listener-relative HRTF direction + listener yaw ~once/sec to diagnose panning.
    private static readonly bool _audioDebug = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_DEBUG") == "1";
    private int _dbgFrame;

    // Steam Audio (phonon) HRTF state — shared context + HRTF.
    private IntPtr _saContext;
    private IntPtr _saHrtf;
    private int _saFrameSize = 1024;
    private bool _steamAudioEnabled;

    // Pre-allocated pool of Steam Audio voices (binaural effect + Phonon buffers + FMOD DSP). Created
    // ONCE at init and reused — never iplBinauralEffectCreate/iplAudioBufferAllocate/free at runtime.
    // Freeing Phonon resources while the FMOD mixer thread is mid-callback on them caused native heap
    // corruption / segfaults under the voice churn from footsteps + wall reflections (reproduced by
    // ProviderOrbit.RunChurn). Pooling removes the runtime alloc/free entirely, so there is nothing
    // to free out from under a live callback.
    private sealed class SaVoice
    {
        public SteamAudioVoiceState State = null!;
        public FMOD.DSP Dsp;
        public System.Runtime.InteropServices.GCHandle Handle;
    }
    private readonly Stack<SaVoice> _saPool = new();
    private readonly List<SaVoice> _saAllVoices = new();
    private const int SaPoolSize = 96;

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
            // ── The garbage collector is in the audio path, whether or not that was intended ───
            //
            // Every custom DSP in this engine is a managed callback entered from FMOD's NATIVE mixer
            // thread — engine voices, echoes, the synth, the granular bank, boundary reflections,
            // Steam Audio's binaural pass, the ambisonic bed, the master tap. A native thread
            // entering managed code while a GC suspension is in progress does not run: it waits for
            // the collection. Workstation GC suspends every managed thread for gen0 and gen1 and for
            // the compacting phases of gen2, and a map load — the entity snapshot, the JSON, the
            // voxel acoustics — grows the heap fast enough to trigger a run of them. The ring
            // buffers being full is no help at all when the consumer is stopped.
            //
            // SustainedLowLatency keeps gen2 in the background and off the foreground path. The
            // short suspensions remain; the hundreds-of-milliseconds one does not. It is set here
            // rather than in either head's Program because it is a property of having managed DSPs
            // at all, and both heads have them. The Mixer load line reports gen2 count and total
            // pause time so this can be checked rather than assumed.
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            if (!FmodCheck(Factory.System_Create(out _system), "System_Create")) return false;

            // Coordinate convention: the listener uses forward = +Z, up = +Y, right = +X
            // (strafe-right is Transform(UnitX, yaw)). That is exactly FMOD's DEFAULT LEFT-handed
            // convention (+X right, +Y up, +Z forward), so NO handedness flag is needed.
            // Empirically: setting INITFLAGS._3D_RIGHTHANDED inverts left/right (a +X source is
            // heard on the LEFT). Verified by ear 2026-06 — leave FMOD in its default left-handed mode.
            FmodCheck(_system.setSoftwareFormat(44100, SPEAKERMODE.STEREO, 0), "setSoftwareFormat");

            // OPENFPS_FMOD_WAV=<path> captures exactly what the mixer produced to a file, instead of
            // (or as well as) the speakers. Worth having permanently: "it crackles" is not something
            // that can be reasoned about from a CPU percentage, and the difference between a clipped
            // waveform, a starved buffer and a stepped voice is obvious in thirty seconds of samples
            // and invisible from the listening chair.
            // OPENFPS_FMOD_WAV replaces the sound card with a file writer: exact, and silent, which
            // suits a rig. OPENFPS_AUDIO_CAPTURE taps the master instead and keeps playing, which
            // suits a person trying to reproduce something they can only find by ear.
            string? wavPath = Environment.GetEnvironmentVariable("OPENFPS_FMOD_WAV");
            IntPtr extra = IntPtr.Zero;
            if (!string.IsNullOrEmpty(wavPath))
            {
                FmodCheck(_system.setOutput(OUTPUTTYPE.WAVWRITER), "setOutput(WAVWRITER)");
                extra = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(wavPath);
                Log.Information("FMOD output is being captured to {Path} (silent: this replaces the sound card)", wavPath);
            }
            FmodCheck(_system.set3DSettings(1.0f, 1.0f, 1.0f), "set3DSettings"); // 1 unit = 1 metre

            // ── How much of a stall the mixer can absorb ──────────────────────────────────────
            //
            // FMOD's default on Linux is four buffers of 1024 samples: 93 ms between the callback
            // running and the sound card wanting the result. That is the whole tolerance for
            // anything that stops the mixer thread — a GC suspension, the scheduler giving its core
            // to a bake — and it was never set. Eight buffers doubles it to about 185 ms for 93 ms
            // more output latency, which on a first-person game is under the threshold where a
            // gunshot stops feeling like it came from the trigger.
            //
            // It is insurance and not a fix: a stall longer than the buffer is still a dropout, and
            // the reason the stalls exist is dealt with elsewhere. But a load spike does not have to
            // be heard just because it happened.
            FmodCheck(_system.setDSPBufferSize(1024, 8), "setDSPBufferSize");

            // ── Real voices, which is not the number passed to init ───────────────────────────
            //
            // BEFORE init, and that is not a style point — FMOD refuses this afterwards with
            // ERR_INITIALIZED, and the first version of this line was after init and did exactly
            // that. The error was logged and nothing else happened, so the limit silently stayed at
            // the default 64 and the symptom arrived two minutes later, which is how long the
            // speedway takes to find reflections for all thirty cars and climb past it.
            //
            // The 512 passed to init is the VIRTUAL channel count. How many are actually MIXED is
            // this, and past it FMOD picks the quietest playing voices and stops rendering them —
            // correctly, silently, and with no error anywhere. On a racetrack the quietest voices
            // are the cars furthest away, so the set being virtualised changes continuously as cars
            // approach and recede: every car crossing in front pushes another one out and is itself
            // pushed out on the way past. For a sampled voice that is inaudible. For a SYNTHESIZED
            // one it is not — the DSP callback simply stops being called and later starts again, and
            // the ring it reads from has moved on, so the voice resumes somewhere else in its own
            // waveform. Heard as extreme pitch jumpiness on every pass-by, arriving suddenly once
            // the voice count crosses the limit and never going away.
            //
            // Thirty cars is thirty engines plus up to sixty reflections; measured at 91 playing
            // with 64 real. 256 leaves room for footsteps, weapons and ambience on top of a full
            // field, and costs nothing until the voices exist.
            FmodCheck(_system.setSoftwareChannels(256), "setSoftwareChannels");

            if (!FmodCheck(_system.init(512, INITFLAGS.NORMAL | INITFLAGS.VOL0_BECOMES_VIRTUAL, extra), "init"))
                return false;

            // Say what was actually granted. A limit that silently stayed at its default is exactly
            // the failure this whole line of investigation was.
            _system.getSoftwareChannels(out int realChannels);
            Log.Information("FMOD software channels: {Real} real voices (512 virtual).", realChannels);


            _system.getVersion(out uint version);
            Log.Information("FMOD initialized: v{Major:X}.{Minor:X2}.{Patch:X2}, left-handed 3D (+X right / +Z fwd), 44.1kHz stereo.",
                (version >> 16) & 0xFFFF, (version >> 8) & 0xFF, version & 0xFF);

            _resources = new FmodResourceManager(_system);
            _granularBank = new GranularBank(_system);
            _system.createChannelGroup("Reflections", out _reflectionGroup);
            _system.getMasterChannelGroup(out var master);
            master.addGroup(_reflectionGroup);

            // Near-field boundary reflections, at the TAIL so every world sound passes through them —
            // a wall reflects the whole room back at you, not one voice.
            //
            // TAIL is where the signal ENTERS: FMOD runs a DSP chain tail -> head -> output, so a
            // unit added at the tail is processed FIRST and one added at the head is processed LAST.
            // The limiter below is therefore added at the HEAD. It used to be added at the tail as
            // well, which put it before these reflections rather than after them — so the one thing
            // it exists to prevent, a reflection pushing the master past the ceiling, was the one
            // case it could not catch.
            _system.getDriverInfo(0, out _, 0, out _, out int driverRate, out _, out _);
            _boundaryState = new BoundaryVoiceState(driverRate > 0 ? driverRate : 44100);
            if (BoundaryProximityProcessor.CreateDSP(_system, _boundaryState, out _boundaryDsp, out _boundaryHandle) == RESULT.OK)
                master.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, _boundaryDsp);
            else
                Log.Warning("Boundary proximity DSP could not be created; walls will not colour the mix.");

            // ── Gain staging, and where the headroom is paid back ────────────────────────────────
            //
            // Every source in this game is rendered at its TRUE level relative to every other one:
            // Loudness.Place turns a source's dB SPL at a metre into a gain and a reference distance,
            // and the engine then attenuates literally with 1/r. That is the part that must not be
            // fiddled with per sound, because it is the whole reason a listener can tell a rifle at
            // two hundred metres from a pistol at twenty.
            //
            // The cost of getting that right is that the mix sits LOW. A source has to leave room for
            // its own peaks (a synthesized engine's are 16 dB over its mean), and an outdoor scene
            // spends another 30 dB on distance before anything reaches the ear. Nothing is clipping
            // and nothing is wrong — there is simply a lot of unused range above the music.
            //
            // That range is taken back HERE, once, for everything — not by making individual sounds
            // louder, which would destroy the relative levels the game is built on, and not by
            // guessing per map, which cannot work when nobody knows what sources a map will carry.
            // The maximizer raises the whole mix into the top of the range and the brick wall catches
            // whatever that pushes over, so adding a hundred more sound sources changes what you hear
            // and never how loud the master is.
            _system.createDSPByType(DSP_TYPE.LIMITER, out _masterLimiter);
            _masterLimiter.setParameterFloat(0, 50.0f);            // release time (ms)
            _masterLimiter.setParameterFloat(1, -1.0f);            // ceiling (dBFS)
            _masterLimiter.setParameterFloat(2, MasterMakeupDb);   // maximizer gain (dB)
            master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, _masterLimiter);

            // And the meter that says whether the number above is right. Integrated loudness on the
            // master, in LUFS — the only honest answer to "is the mix too quiet", and the thing to
            // read before changing any level anywhere. Added at the HEAD *after* the limiter, so it
            // ends up nearer the output and measures what actually leaves the mixer; metering ahead
            // of the limiter reports the makeup gain as having done nothing, which is how the
            // ordering above came to be noticed.
            if (_system.createDSPByType(DSP_TYPE.LOUDNESS_METER, out _loudnessMeter) == RESULT.OK)
            {
                master.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, _loudnessMeter);
                _loudnessMeter.setParameterInt(0, 1);   // state: start metering
            }

            string? capturePath = Environment.GetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE");
            if (!string.IsNullOrEmpty(capturePath))
            {
                _system.getSoftwareFormat(out int capRate, out _, out _);
                _masterTap = MasterTap.Attach(_system, master, capturePath, capRate > 0 ? capRate : 44100);
                if (_masterTap != null) Log.Information("Capturing the mix to {Path} (playback continues).", capturePath);
                else Log.Warning("Could not attach the capture tap; playback is unaffected.");
            }

            TryInitSteamAudio();

            // Engines render ahead, off the mixer thread. See EngineRenderPool.
            _enginePool = new EngineRenderPool(SnapshotEngineVoices);

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
            var cs = Phonon.DefaultContextSettings();
            string simd = Phonon.SimdLevelName(cs.simdLevel);
            if (Phonon.iplContextCreate(ref cs, out _saContext) != Phonon.IPL_STATUS_SUCCESS)
            { Log.Warning("Steam Audio: context create failed (SIMD {Simd}); DEGRADED to FMOD panning — no HRTF binaural.", simd); return; }

            var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = _saFrameSize };
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

    /// <summary>Allocates one pooled voice (effect + Phonon buffers + DSP). Called only at init.</summary>
    private bool CreatePooledVoice(out SaVoice voice)
    {
        voice = null!;
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

        if (SteamAudioDsp.CreateDSP(_system, s, out var dsp, out var handle) != RESULT.OK)
        {
            Phonon.iplAudioBufferFree(_saContext, ref s.InBuf);
            Phonon.iplAudioBufferFree(_saContext, ref s.OutBuf);
            Phonon.iplBinauralEffectRelease(ref effect);
            return false;
        }
        voice = new SaVoice { State = s, Dsp = dsp, Handle = handle };
        return true;
    }

    /// <summary>Borrows a voice from the pool (no allocation). Returns false when the pool is empty —
    /// that sound then plays without HRTF rather than crashing.</summary>
    private bool TryCreateSteamAudioVoice(out SteamAudioVoiceState? state, out FMOD.DSP dsp, out System.Runtime.InteropServices.GCHandle handle)
    {
        state = null; dsp = default; handle = default;
        SaVoice v;
        lock (_saPool)
        {
            if (_saPool.Count == 0) return false;
            v = _saPool.Pop();
        }
        // Reset transient state. Crucially, clear the binaural effect's internal overlap-add buffers:
        // pooled voices are reused rapidly (footsteps), and reusing an effect that still holds the tail of
        // the previous sound produces an audible click/pop on the first frame. The DSP is detached from any
        // channel at this point (ReleaseSteamAudioVoice removed it), so resetting here is safe.
        Phonon.iplBinauralEffectReset(v.State.Effect);
        v.State.DirX = 0f; v.State.DirY = 0f; v.State.DirZ = -1f;
        v.State.LastRms = v.State.LastRmsL = v.State.LastRmsR = 0f;
        v.State.ProducedAudio = false;
        state = v.State; dsp = v.Dsp; handle = v.Handle;
        return true;
    }

    private void ReleaseSteamAudioVoice(ActiveSound a)
    {
        if (a.SaState == null) return;
        // Detach the DSP from its channel so the mixer stops calling it (removeDSP blocks until any
        // in-flight callback completes), then return the voice to the pool for reuse. We never free
        // the Phonon effect/buffers or the GCHandle here — that is exactly what raced the mixer
        // callback and corrupted the heap. The resources live until Dispose.
        if (a.SaDsp.hasHandle() && a.Channel.hasHandle()) a.Channel.removeDSP(a.SaDsp);
        var v = new SaVoice { State = a.SaState, Dsp = a.SaDsp, Handle = a.SaHandle };
        lock (_saPool) { _saPool.Push(v); }
        a.SaState = null; a.SaDsp = default; a.SaHandle = default;
    }

    // A short history of simulated decays, median-filtered. See SetSimulatedReverbDecay.
    private readonly float[] _simReverbHistory = new float[5];
    private int _simReverbCount;

    /// <summary>
    /// The geometry-derived reverberation time for the listener's surroundings, from the ray tracer.
    ///
    /// Median-filtered over the last five readings, and that is not smoothing for its own sake. The
    /// measurement is stochastic — successive runs over the SAME street canyon returned 1729, 2725 and
    /// 2800 ms — and every so often a run finds nothing at all and reports zero. Used raw, the good
    /// readings make the reverb time wander audibly, and a single empty one collapses the street to
    /// open air for a frame, which is a far worse artefact than being slightly wrong about the decay.
    ///
    /// A median rejects the dropout outright (one bad sample in five cannot move it) while tracking a
    /// genuine change — walking out of the canyon into a field moves three readings and the median
    /// follows. A mean would let every dropout drag the value down, and a floor-check could not tell
    /// a failed trace from an actual open field, because both report the same number.
    /// </summary>
    public void SetSimulatedReverbDecay(float decayMs)
    {
        for (int i = _simReverbHistory.Length - 1; i > 0; i--)
            _simReverbHistory[i] = _simReverbHistory[i - 1];
        _simReverbHistory[0] = decayMs;
        if (_simReverbCount < _simReverbHistory.Length) _simReverbCount++;

        Span<float> sorted = stackalloc float[_simReverbCount];
        for (int i = 0; i < _simReverbCount; i++) sorted[i] = _simReverbHistory[i];
        sorted.Sort();
        _simReverbDecayMs = sorted[_simReverbCount / 2];
    }

    /// <summary>The median-filtered decay currently driving the reverb, for the spikes and profiler.</summary>
    public float SimulatedReverbDecayMs => _simReverbDecayMs;

    // Speed of sound, m/s, derived from the world's air temperature. Defaults to the 20 °C value so a
    // provider that is never told the weather behaves exactly as it did before.
    private float _speedOfSound = OpenFPS.Client.AudioEngine.Core.AudioPhysics.SpeedOfSound;
    public void SetAirTemperature(float celsius) =>
        _speedOfSound = OpenFPS.Client.AudioEngine.Core.AudioPhysics.SpeedOfSoundAt(celsius);

    /// <summary>Current outdoor wet level in dB, moved toward its target rather than snapped to it.</summary>
    private float _outdoorWetDb = -80f;

    /// <summary>Overrides the listener-region reverb DSP decay with the simulated RT60-derived value when
    /// available, replacing the Sabine estimate for the room the listener is in. No-op when 0 (sim off).
    ///
    /// Outdoors is included, and used not to be. The outdoor bus is built muted because the Sabine
    /// estimate for it treats the entire map as one room and produces an omnipresent wash — but that
    /// is an argument against THAT ESTIMATE, not against outdoor reverberation, and the early return on
    /// the global region id was quietly turning down the one model that gets it right. A ray-traced
    /// RT60 is computed from the geometry actually standing around the listener: nearly nothing in an
    /// open field, and a real decay in a street with tall buildings down both sides. So outdoors the
    /// simulated decay sets the time AND opens the wet level in proportion to it, which leaves open
    /// ground exactly as dry as it is today and gives a concrete canyon the slapback it should have.
    /// </summary>
    private void ApplySimulatedReverb(int listenerRegionId)
    {
        if (_simReverbDecayMs <= 0f) return;
        if (!_reverbDsps.TryGetValue(listenerRegionId, out var dsp) || !dsp.hasHandle()) return;

        float ms = Math.Clamp(_simReverbDecayMs, AcousticConstants.MinReverbDecayMs, AcousticConstants.MaxReverbDecayMs);
        dsp.setParameterFloat(0, ms);

        if (_acousticMap == null || listenerRegionId != _acousticMap.GlobalEnvironmentId) return;

        // Outdoors is capped. The sky is an absorber that a ray tracer working from box colliders
        // cannot see, so its canyon RT60 comes back long — and an uncapped two-second decay outdoors
        // is what makes a street sound like a nave.
        if (ms > AcousticConstants.OutdoorMaxDecayMs)
        {
            ms = AcousticConstants.OutdoorMaxDecayMs;
            dsp.setParameterFloat(0, ms);
        }

        float span = AcousticConstants.OutdoorFullWetDecayMs - AcousticConstants.OutdoorDryDecayMs;
        float openness = span > 1f
            ? Math.Clamp((ms - AcousticConstants.OutdoorDryDecayMs) / span, 0f, 1f)
            : 0f;
        // -80 dB is the mute the bus is built with, so an open field lands back on exactly the value
        // it has today and nothing about open ground changes.
        float target = openness <= 0.001f
            ? -80f
            : MathHelper.Lerp(-40f, AcousticConstants.OutdoorMaxWetDb, openness);

        _outdoorWetDb += (target - _outdoorWetDb) * AcousticConstants.OutdoorWetBlendSpeed;
        if (MathF.Abs(target - _outdoorWetDb) < 0.05f) _outdoorWetDb = target;
        dsp.setParameterFloat(11, _outdoorWetDb);
    }

    /// <summary>What the outdoor reverb bus is currently doing, for the spikes and the profiler. A
    /// value of -80 means open air.</summary>
    public float OutdoorReverbWetDb => _outdoorWetDb;

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
        ReturnReverbVoices(); // detach HRTF voices while the buses still exist, return them to the pool
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
        // Make the reverb bus positionable in 3D so UpdateReverbBuses can place a room's reverb AT its
        // doorway when the listener is outside (set3DLevel 1 + set3DAttributes(portal)). Without a 3D
        // mode those calls are no-ops and the reverb is heard omnidirectionally everywhere it is sent.
        // We gate the LEVEL manually (per-portal aperture/distance), so keep FMOD's own distance
        // rolloff out of the way with a huge max distance. When the listener is inside the room we set
        // 3D level back to 0 so the reverb fills the space non-directionally.
        bus.setMode(MODE._3D | MODE._3D_LINEARROLLOFF);
        bus.set3DMinMaxDistance(2.0f, 10000.0f);
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
        
        // The global/outdoor "region" spans the whole map, so its Sabine decay is enormous and it is
        // always at full volume — that omnipresent wash is what reads as "reverb all around, even far
        // from any room". Open air should be effectively dry, so mute the outdoor reverb; rooms keep
        // their own (gated + doorway-positioned) reverb.
        bool outdoors = regionId == _acousticMap.GlobalEnvironmentId;
        if (region.ReverbTimeScale > 0.01f && !outdoors) {
            reverbDsp.setParameterFloat(11, 0.0f); // Wet level normal
        } else {
            reverbDsp.setParameterFloat(11, -80.0f); // Mute (outdoors dry / no global wash)
        }
        // A region bus is an AUX SEND, not an insert: the only thing that should leave it is the
        // reverberant field. Dry at 0 dB meant every send was ALSO an undirected copy of the source —
        // a second, position-less image of the siren mixed in on top of its own HRTF voice.
        reverbDsp.setParameterFloat(12, -80.0f); // Dry muted (send bus)

        // ORDER MATTERS, and it is the opposite of what it reads like. FMOD's chain runs TAIL (input) ->
        // HEAD (output), so the reverb must go at the TAIL for the fader — and the binaural stage added
        // at the HEAD below — to sit DOWNSTREAM of it. With the reverb at the HEAD (where it used to be)
        // the sends injected past both: the per-portal volume gating did nothing and the doorway HRTF
        // localized silence, which is exactly why a room's reverb was heard at full level, from all
        // directions, from anywhere on the map.
        bus.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, reverbDsp);
        
        float initialVol = (regionId == _listenerRegionId) ? 1.0f : 0.0f;
        bus.setVolume(initialVol);
        _reverbBuses[regionId] = bus;
        _reverbDsps[regionId] = reverbDsp;
        _reverbVolumes[regionId] = initialVol;
        _system.getMasterChannelGroup(out var master);
        master.addGroup(bus);

        // Steam Audio directional reverb: route this room's reverb output through an HRTF voice so that,
        // when the listener is OUTSIDE, the reverberation localizes to the doorway (like the direct sound)
        // instead of washing from all sides. The voice's binaural DSP sits at the bus HEAD — the OUTPUT
        // end, so it binauralizes the finished reverb tail rather than the dry input — and is bypassed
        // while inside the room (reverb then fills the space as 2D stereo). The bus is switched to 2D so
        // FMOD doesn't also collapse the binaural pair. Voice is held for the bus lifetime.
        if (_steamAudioEnabled && TryCreateSteamAudioVoice(out var rvState, out var rvDsp, out var rvHandle))
        {
            bus.getMode(out MODE bm);
            bus.setMode((bm & ~(MODE._3D | MODE._3D_LINEARROLLOFF)) | MODE._2D);
            bus.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, rvDsp);
            rvDsp.setBypass(true); // start omnidirectional; UpdateReverbBuses enables it when outside
            _reverbSaVoices[regionId] = new SaVoice { State = rvState!, Dsp = rvDsp, Handle = rvHandle };
        }
    }

    /// <summary>The DSP a source's reverb SEND must feed: the region bus's own SFXREVERB unit, which sits
    /// at the TAIL (input end) of the bus chain. Addressing it by identity rather than by
    /// <c>getDSP(HEAD)</c> is the point — the head is the binaural output stage, and sending into it
    /// bypassed both the bus fader (the per-portal distance/aperture gating) and the reverb itself.</summary>
    private bool TryGetReverbInput(int regionId, out FMOD.DSP dsp)
        => _reverbDsps.TryGetValue(regionId, out dsp) && dsp.hasHandle();

    /// <summary>Removes a send connection outright instead of leaving it muted. A muted-but-attached
    /// connection is re-created every time the region flips back, and FMOD caps inputs per DSP.</summary>
    private static void DropSend(ref FMOD.DSPConnection conn, FMOD.DSP target, FMOD.DSP source)
    {
        if (!conn.hasHandle()) return;
        if (target.hasHandle() && source.hasHandle()) target.disconnectFrom(source, conn);
        conn = default;
    }

    /// <summary>Localizes a room's reverb to its doorway (HRTF) when the listener is outside, or makes it
    /// fill the room (binaural bypassed) when inside. Falls back to FMOD 3D positioning if Steam Audio is
    /// unavailable for this bus.</summary>
    private void SetReverbDirection(int regionId, FMOD.ChannelGroup bus, Vector3 doorwayPos, bool outside, Vector3 lPos)
    {
        if (_reverbSaVoices.TryGetValue(regionId, out var v) && v.Dsp.hasHandle())
        {
            // Crossing a threshold used to flip this stage's bypass in a single frame, which is a step
            // change in the signal and CLICKS — once per crossing at best, and repeatedly while a player
            // stood in a doorway. The switch is a RAMP now: spatialBlend goes 1 (arriving through the
            // opening) to 0 (filling the room the listener is in) over a couple of hundred milliseconds,
            // and the direction is smoothed with it so a change of nearest portal does not snap either.
            if (outside)
            {
                v.Dsp.setBypass(false); // re-engage before the ramp climbs, never during it
                Vector3 local = Vector3.Transform(doorwayPos - lPos, Quaternion.Conjugate(_listenerRot));
                float len = local.Length();
                if (len > 1e-4f)
                {
                    var target = new Vector3(local.X / len, local.Y / len, -local.Z / len);
                    var next = Vector3.Lerp(new Vector3(v.State.DirX, v.State.DirY, v.State.DirZ),
                                            target, AcousticConstants.ReverbDirectionSmoothing);
                    if (next.LengthSquared() > 1e-6f) next = Vector3.Normalize(next);
                    v.State.DirX = next.X; v.State.DirY = next.Y; v.State.DirZ = next.Z;
                }
            }

            float blend = MathHelper.Lerp(v.State.SpatialBlend, outside ? 1f : 0f, AcousticConstants.ReverbBlendSpeed);
            v.State.SpatialBlend = blend;

            // Bypass only at the bottom of the ramp, where the stage is contributing no direction at
            // all. That restores the reverb's true stereo width inside a room, and the only thing that
            // changes at the switch is decorrelation — not level, so there is no edge to hear.
            if (!outside && blend < 0.02f) v.Dsp.setBypass(true);
        }
        else if (outside)
        {
            bus.set3DLevel(1.0f); bus.set3DSpread(0.0f);
            FMOD.VECTOR fp = FmodHelpers.ToFmodVec(doorwayPos); FMOD.VECTOR fv = new FMOD.VECTOR();
            bus.set3DAttributes(ref fp, ref fv);
        }
        else bus.set3DLevel(0.0f);
    }

    /// <summary>Diagnostics only (OpenFPS.AudioLab's `--reverb-route`): the live state of one region's
    /// reverb bus. There is no way to see a DSP graph from outside FMOD, and the graph is exactly what
    /// was wrong — the sends were injected at the bus HEAD, downstream of both the fader and the
    /// binaural stage, so the gating and the doorway localization were connected to nothing.</summary>
    /// <param name="doorwayBlend">How localized to the doorway the bus currently is: 1 outside, 0 when
    /// it fills the listener's own room, and anything between while the crossing ramp runs.</param>
    /// <param name="volume">The gated bus level — what the per-portal aperture/distance rule produced.</param>
    /// <param name="rmsL">Left-ear energy of the last block the HRTF stage produced.</param>
    /// <param name="rmsR">Right-ear energy of the last block the HRTF stage produced.</param>
    internal bool TryGetReverbDiagnostics(int regionId, out float doorwayBlend, out float volume,
                                          out float rmsL, out float rmsR)
    {
        doorwayBlend = 0f; volume = 0f; rmsL = 0f; rmsR = 0f;
        if (!_reverbVolumes.TryGetValue(regionId, out volume)) return false;
        if (_reverbSaVoices.TryGetValue(regionId, out var v) && v.Dsp.hasHandle())
        {
            v.Dsp.getBypass(out bool bypassed);
            doorwayBlend = bypassed ? 0f : v.State.SpatialBlend;
            // A bypassed stage is not running, so its last block is stale. Report nothing rather than
            // the numbers it produced the last time the listener was somewhere else.
            rmsL = bypassed ? 0f : v.State.LastRmsL;
            rmsR = bypassed ? 0f : v.State.LastRmsR;
        }
        return true;
    }

    /// <summary>
    /// Peak level actually flowing through a region's reverb DSP, metered by FMOD itself.
    ///
    /// <see cref="TryGetReverbDiagnostics"/> cannot answer this question and it is important to know
    /// why: it reads the bus's binaural stage, which is BYPASSED whenever the listener is inside the
    /// region — and outdoors the listener is always inside region -1. So it reports zero for the
    /// outdoor bus whether that bus is carrying a firefight or nothing at all, which is exactly the
    /// kind of measurement that makes you certain of something false. This meters the reverb DSP's own
    /// input and output and is true regardless of what is bypassed downstream.
    /// </summary>
    internal bool TryMeterReverbBus(int regionId, out float inputPeak, out float outputPeak)
    {
        inputPeak = 0f; outputPeak = 0f;
        if (!_reverbDsps.TryGetValue(regionId, out var dsp) || !dsp.hasHandle()) return false;
        dsp.setMeteringEnabled(true, true);
        if (dsp.getMeteringInfo(out var inInfo, out var outInfo) != RESULT.OK) return false;
        for (int i = 0; i < inInfo.numchannels && i < 32; i++)
            inputPeak = Math.Max(inputPeak, inInfo.peaklevel[i]);
        for (int i = 0; i < outInfo.numchannels && i < 32; i++)
            outputPeak = Math.Max(outputPeak, outInfo.peaklevel[i]);
        return true;
    }

    /// <summary>Detaches and returns all per-bus reverb HRTF voices to the pool (before the buses are
    /// released). Safe to call repeatedly.</summary>
    private void ReturnReverbVoices()
    {
        foreach (var kvp in _reverbSaVoices)
        {
            var v = kvp.Value;
            if (v.Dsp.hasHandle() && _reverbBuses.TryGetValue(kvp.Key, out var b) && b.hasHandle()) b.removeDSP(v.Dsp);
            lock (_saPool) { _saPool.Push(v); }
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
        else if (emitter.IsSynth && emitter.EchoOfEntity != 0)
        {
            if (!_isInitialized) return;
            EngineVoiceState? src;
            lock (_lock) { src = FindActive(emitter.EchoOfEntity)?.EngineState; }
            if (src == null) return;
            var echo = new EngineEchoState(src) { TargetDelaySeconds = emitter.EchoDelaySeconds, TargetGain = emitter.EchoGain };
            if (EchoProcessor.CreateDSP(_system, echo, out engineDsp, out engineHandle) != RESULT.OK) return;
            engineDsp.setChannelFormat(0, 0, SPEAKERMODE.MONO);
            if (_system.playDSP(engineDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                engineDsp.release();
                engineHandle.Free();
                return;
            }
            channel.setMode(MODE._3D | MODE._3D_LINEARROLLOFF);
            echoState = echo;
        }
        else if (emitter.IsSynth && !string.IsNullOrEmpty(emitter.EngineKey))
        {
            if (!_isInitialized) return;
            _system.getSoftwareFormat(out int rate, out _, out _);
            engineState = new EngineVoiceState(OpenFPS.Common.VehicleProfile.ByName(emitter.EngineKey), rate, emitter.EntityId)
            {
                TargetSpeed = emitter.EngineSpeed,
                Running = emitter.EngineRunning,
            };
            // The car is already doing this speed; start the engine in that state rather than
            // spinning it up from rest inside the first eighty milliseconds.
            engineState.PlaceAtSpeed(emitter.EngineSpeed);
            if (EngineProcessor.CreateDSP(_system, engineState, out engineDsp, out engineHandle) != RESULT.OK) return;
            engineDsp.setChannelFormat(0, 0, SPEAKERMODE.MONO);
            Log.Information("Engine voice started: entity {Id} runs '{Preset}' live at {Rate} Hz", emitter.EntityId, emitter.EngineKey, rate);
            if (_system.playDSP(engineDsp, targetGroup, true, out channel) != RESULT.OK)
            {
                engineDsp.release();
                engineHandle.Free();
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
            if (!_isInitialized) return;
            var loadState = _resources.TryGetSound(emitter.SoundId, out FMOD.Sound sound, loopNative);
            if (loadState != SoundLoadState.Ready)
            {
                if (_audioDebug && emitter.Mode == PlaybackMode.LoopOne)
                    Log.Information("[BEACON] e{Id} '{Sound}' not playing yet — {State}", emitter.EntityId, emitter.SoundId, loadState);

                // A NONBLOCKING load that has not finished is NOT a reason to lose the play: park the
                // emitter and retry it on subsequent Update() ticks (see DrainDeferredPlays). Only a
                // genuinely Missing asset is dropped, and that has already been logged once by name.
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
            channel.setMode(MODE._3D | MODE._3D_LINEARROLLOFF);
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

            // A reflection does not get a diffraction filter.
            //
            // It is already a modelled path — the image-source pass decided which surface it came off
            // and how far it travelled — so bending it round an obstacle as well counts the geometry
            // twice. It is also the cheapest voice in the mix to generate and one of the dearest to
            // place, and on a track where every car has a reflection against every wall, one fewer
            // DSP per echo is the difference between the mixer having room for another car and not.
            if (!emitter.IsReflection)
            {
                diffractionDsp = GetDiffractionDsp();
                channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, diffractionDsp);
            }
            if (_steamAudioEnabled && TryCreateSteamAudioVoice(out saState, out saDsp, out saHandle))
            {
                // Steam Audio binaural sits last in the chain (after occlusion EQ + diffraction),
                // turning the filtered mono into an HRTF stereo pair.
                channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, saDsp);

                // CRITICAL: a 3D channel treats the signal as a mono point source and downmixes the
                // DSP's binaural stereo back to mono on the way to the master bus — set3DLevel(0) does
                // NOT prevent this (verified by SteamAudioLiveTest.RunStereoCheck: 3D collapses L≈R,
                // switching the channel to 2D restores full L/R separation). Swap the 3D flags for 2D
                // while preserving loop/other flags. Distance falloff is applied manually below in
                // ApplyAcousticFilters (distAtten), so we lose nothing by leaving FMOD's 3D path.
                channel.getMode(out MODE chMode);
                channel.setMode((chMode & ~(MODE._3D | MODE._3D_LINEARROLLOFF)) | MODE._2D);
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
                EngineDsp = engineDsp, EngineHandle = engineHandle, EngineState = engineState, EchoState = echoState,
                Position = emitter.Position, ApparentPosition = emitter.ApparentPosition,
                LastAttributeAt = emitter.PositionSampledAt > 0 ? emitter.PositionSampledAt : OpenFPS.Common.AudioClock.Now,
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
                // NOT "sourceRegionId != -1". Outdoors IS region -1, so that test — written to mean
                // "this source has no region" — excluded every sound in the open world from every
                // reverb bus. The outdoor bus could be built, unmuted and correctly timed, and still
                // receive nothing: a street that measured a two-second reverberation time and sounded
                // completely dead. TryGetReverbInput already returns false for a region with no bus,
                // which is the test that was actually wanted.
                if (TryGetReverbInput(sourceRegionId, out var sourceReverb))
                {
                    activeSound.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var channelDsp);
                    sourceReverb.addInput(channelDsp, out activeSound.SourceReverbConnection, DSPCONNECTION_TYPE.SEND);
                    activeSound.SourceReverbConnection.setMix(AcousticConstants.ReverbSendMix);
                    activeSound.CurrentSourceRegionId = sourceRegionId;
                }

                if (TryGetReverbInput(_listenerRegionId, out var listenerReverb))
                {
                    activeSound.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var channelDsp);
                    listenerReverb.addInput(channelDsp, out activeSound.ReverbConnection, DSPCONNECTION_TYPE.SEND);
                    activeSound.ReverbConnection.setMix(AcousticConstants.ReverbSendMix * AcousticConstants.ReverbCrossSendScale);
                    activeSound.CurrentRegionId = _listenerRegionId;
                }
            }

            AddActive(activeSound);
        }

        // Short fade-in on one-shot event voices (footsteps, impacts). These recycle pooled HRTF voices
        // rapidly; an abrupt onset (or a hard cut when the pool reuses a still-playing voice) clicks. A
        // ~6 ms ramp from silence removes the onset pop. Fade points multiply with setVolume, so the
        // per-frame distance/cone volume still applies on top.
        if (emitter.IsEvent && channel.hasHandle())
        {
            channel.getDSPClock(out ulong startClock, out _);
            _system.getSoftwareFormat(out int sr, out _, out _);
            ulong ramp = (ulong)(sr * 0.006f);
            channel.addFadePoint(startClock, 0.0f);
            channel.addFadePoint(startClock + ramp, 1.0f);
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
                // WHEN THE POSITION WAS TRUE, not when it turned up.
                //
                // An emitter that knows its own sample time keeps it, however many times the same
                // emitter is re-applied — and it is re-applied constantly: the voice manager hands
                // every playing voice back to this method on each of its 250 Hz ticks. Stamping "now"
                // here made that refresh reset the age of a position that had not changed, so dead
                // reckoning never saw an age over one tick and the 30 Hz staircase it exists to smooth
                // came through untouched. An emitter with no sample time (an event, a UI sound, a
                // reflection placed this instant) is as fresh as this call, which is the old behaviour.
                active.LastAttributeAt = emitter.PositionSampledAt > 0
                    ? emitter.PositionSampledAt
                    : OpenFPS.Common.AudioClock.Now;
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
                else if (emitter.IsSynth && active.EngineState != null)
                {
                    active.EngineState.TargetSpeed = emitter.EngineSpeed;
                    active.EngineState.Running = emitter.EngineRunning;
                    active.EngineState.RoadSlip = emitter.TyreSlip;
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
            // Once per active voice per audio frame, so the scan this replaces was the squared term in the
            // frame's cost all by itself. Every voice the entity owns still gets the path.
            if (!_activeById.TryGetValue(entityId, out var voices)) return;
            foreach (var active in voices) 
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

    // --- Deferred plays: NONBLOCKING loads that were not ready when the emitter asked to be heard ------
    // Without this, the FIRST play of any un-preloaded sound is silently lost — createSound returns
    // immediately, the decode is still in flight, and playSound answers ERR_NOTREADY. Parking the emitter
    // and retrying costs nothing and makes the sound arrive a few milliseconds late instead of never.
    private sealed class DeferredPlay
    {
        public SpatialEmitter Emitter;
        public long DeadlineTicks;
        public DeferredPlay(SpatialEmitter emitter, long deadlineTicks) { Emitter = emitter; DeadlineTicks = deadlineTicks; }
    }

    // How long a queued play waits for its decode before we give up and say so. Generous: a cold ogg
    // decode on a slow disk is still well inside this, and a one-off late sound beats a missing one.
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
                // Keep the ORIGINAL deadline: re-requesting a still-loading sound every frame must not
                // extend the wait forever, or a genuinely broken asset would never be reported.
                _deferredPlays[i].Emitter = emitter;
                return;
            }
            _deferredPlays.Add(new DeferredPlay(emitter, deadline));
        }
    }

    /// <summary>Retries parked plays whose sound has since finished decoding, and reports the ones that
    /// ran out of time. Called at the top of <see cref="Update"/>, OUTSIDE the active-sound lock, because
    /// a successful retry re-enters PlaySpatialSound and mutates the active list.</summary>
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
                Log.Warning("Audio asset '{SoundId}' still not decoded after {Timeout} ms; entity {Id} stayed silent.",
                    d.Emitter.SoundId, DeferredPlayTimeoutMs, d.Emitter.EntityId);
                continue;
            }

            lock (_deferredLock) { _deferredPlays.Add(d); }   // still loading — keep waiting
        }
    }

    /// <summary>
    /// Reports the mixer's own CPU load every few seconds.
    ///
    /// Worth having permanently, because the two ways this engine breaks sound alike and are fixed
    /// in opposite directions. A voice whose level reference is wrong CLIPS — a continuous rasp that
    /// gets worse the louder the source is. A mixer that misses its deadline STARVES — the same
    /// audio, torn and stuttering. Both get described as "crackling and breaking up", and the dsp
    /// figure below tells them apart in one line: past about 60% the callback is running out of
    /// time; under it, whatever is wrong is not the budget.
    ///
    /// A live engine is by far the most expensive voice in the mix (`--engine-cost`), so this is the
    /// number to look at before raising OPENFPS_ENGINE_VOICES.
    /// </summary>
    /// <summary>
    /// The mixer's DSP load, 0..1+, as of the last sample. 1 means the callback is using its whole
    /// deadline; past that it is late, and late is not slow, it is torn audio.
    /// </summary>
    public float MixerLoad => _mixerLoad;
    private volatile float _mixerLoad;
    /// <summary>
    /// Interval timer for the load SAMPLE, and nothing else.
    ///
    /// It is restarted every quarter second, which is fine for an interval timer and catastrophic for
    /// a timebase — and it used to be both. Every voice's position stamp and the dead reckoning that
    /// reads it were measured on this, so a position stamped at 0.24 s was compared against a "now" of
    /// 0.01 s the moment this restarted. The age came out negative, the reckoning returned the position
    /// unchanged, and the fix for a close pass stepping in pitch was inert for most of every quarter
    /// second. Timestamps now come from <see cref="OpenFPS.Common.AudioClock"/>, which nothing restarts.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _loadSampleClock = System.Diagnostics.Stopwatch.StartNew();

    private void ReportMixerLoad()
    {
        // Sampled far more often than it is logged, because something has to steer on it.
        if (_loadSampleClock.Elapsed.TotalSeconds >= 0.25)
        {
            _loadSampleClock.Restart();
            if (_system.getCPUUsage(out var now) == RESULT.OK)
            {
                // A slow average: the control loop above this must not chase a single busy block.
                float f = now.dsp * 0.01f;
                _mixerLoad += (f - _mixerLoad) * 0.35f;
            }
        }

        // Five seconds when nothing is wrong, one second when something is — a starve or a gen2
        // collection in the last second is exactly when a five-second average stops being useful.
        double since = _cpuClock.Elapsed.TotalSeconds;
        bool trouble = EngineVoiceState.GlobalStarves != _lastStarves || GC.CollectionCount(2) != _lastGen2;
        if (since < 5.0 && !(trouble && since >= 1.0)) return;
        _cpuClock.Restart();
        if (_system.getCPUUsage(out var cpu) != RESULT.OK) return;
        int voices = 0;
        lock (_lock) foreach (var a in _activeSounds) if (a.EngineState != null || a.EchoState != null) voices++;

        // ── What a dropout actually needs you to know ────────────────────────────────────────
        //
        // A dsp percentage cannot tell the difference between a mixer that is working hard and a
        // mixer that was FROZEN — and every remaining suspect freezes it rather than loads it. So
        // the line carries the three things that distinguish them, as deltas over the last five
        // seconds, because a running total tells you nothing about now:
        //
        //   starves   blocks the engine producers had not rendered in time. Producer-side.
        //   gc        gen2 collections and the total time every managed thread spent suspended.
        //             A managed DSP callback is entered from FMOD's native mixer thread, and a
        //             native thread entering managed code during a GC suspension WAITS for the GC.
        //             The ring being full does not help; the consumer is the thing that stopped.
        //             If this jumps when the audio cuts, that is the cause, and it is the one
        //             suspect that no amount of buffer depth can absorb.
        //   real      FMOD's REAL channels against its limit. 512 at init is the VIRTUAL count;
        //             the software limit is 64 by default, and past it the quietest voices go
        //             silent — which on a track with engines, echoes, wall reflections, samples
        //             and ambience is reached without anything appearing to be wrong.
        // ── The worst any ONE voice went unrepositioned ──────────────────────────────────────
        //
        // Separate from the audio system's own figure ON PURPOSE. That one measures whether the
        // placement pass ran at all; this one measures whether it reached every voice. They come
        // apart exactly where "some of the cars stop, not all of them" lives: a pass that runs on
        // time but skips a voice leaves that one car's engine hanging in the air while the rest of
        // the field carries on, and a global timer cannot see it.
        double nowSec = OpenFPS.Common.AudioClock.Now;
        double worstStale = 0; int worstId = 0;
        lock (_lock)
        {
            foreach (var a in _activeSounds)
            {
                if (a.LastAttributeAt <= 0) continue;
                // The worst it reached DURING the interval, and the worst it is right now — a voice
                // that has been abandoned since the last report has no placement to have recorded it.
                double stale = Math.Max(a.WorstPositionAge, nowSec - a.LastAttributeAt);
                a.WorstPositionAge = 0;
                if (stale > worstStale) { worstStale = stale; worstId = a.EntityId; }
            }
        }

        int starves = EngineVoiceState.GlobalStarves;
        int gen2 = GC.CollectionCount(2);
        double pauseMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        _system.getChannelsPlaying(out int playing, out int real);
        Log.Information("Mixer load: dsp {Dsp:F1}%, update {Update:F1}%, stream {Stream:F1}% — "
                      + "{Engines} engine/echo voice(s) of {Total} active, {Real}/{Playing} real channel(s), "
                      + "{Starve} starve(s), gc {Gen2} gen2 / {Pause:F0} ms paused",
                        cpu.dsp, cpu.update, cpu.stream, voices, _activeSounds.Count, real, playing,
                        starves - _lastStarves, gen2 - _lastGen2, pauseMs - _lastPauseMs);
        _lastStarves = starves; _lastGen2 = gen2; _lastPauseMs = pauseMs;

        // One simulation step plus a comfortable margin. Below that a voice is being placed at a
        // position from the last step, which is exactly what the interpolation clock delivers and what
        // dead reckoning carries forward; above it, something is holding a source still.
        const double AcceptablePositionAgeSeconds = 0.06;
        if (worstStale > AcceptablePositionAgeSeconds)
            Log.Warning("Voice {Id} was placed at a position {Stale:F0} ms old (worst this interval) — its sound "
                      + "is sitting still while the thing making it moves on. {Voices} engine/echo voice(s) live.",
                        worstId, worstStale * 1000.0, voices);



        // What the mix actually measures, after the makeup gain and the brick wall. Momentary is
        // "right now", short-term is the last three seconds; -18 to -23 LUFS is where a game mix
        // belongs, and a long way under that means the master makeup is too low for this content.
        if (!_loudnessMeter.hasHandle()) return;
        if (_loudnessMeter.getParameterData(2, out IntPtr data, out uint _) != RESULT.OK) return;
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<DSP_LOUDNESS_METER_INFO_TYPE>(data);
        if (float.IsFinite(info.shorttermloudness) && info.shorttermloudness > -200f)
            Log.Information("Mix loudness: {Short:F1} LUFS short-term, {Momentary:F1} momentary, "
                          + "{Peak:F1} dBFS peak (makeup {Makeup:F0} dB)",
                            info.shorttermloudness, info.momentaryloudness, info.maxtruepeak, MasterMakeupDb);
    }

    private readonly System.Diagnostics.Stopwatch _cpuClock = System.Diagnostics.Stopwatch.StartNew();
    private int _lastStarves, _lastGen2;
    private double _lastPauseMs;

    /// <summary>
    /// How long this method is taking, and how often it is being called — the two numbers that say
    /// whether a pass-by glides or steps.
    ///
    /// The audio thread runs at 250 Hz on purpose (see AudioEngineFacade): Doppler is applied as a
    /// channel pitch, a channel pitch changes the instant it is set, so the loop period IS the
    /// resolution of every pass-by. At 250 Hz a car going past steps by 0.65 %, under the threshold
    /// where a pitch change is heard as a step; at 60 Hz it steps by 2.7 % and is heard as a
    /// staircase. But the loop's period is `max(1 ms, 4 ms - however long the tick took)`, so the
    /// rate is not a setting, it is an OUTCOME — and the tick walks every active voice. Going from
    /// eight cars to thirty multiplies that walk by three or four, and nothing anywhere said so.
    /// </summary>
    private double _updateMsSum, _updateMsMax;
    private int _updateCount;
    private readonly System.Diagnostics.Stopwatch _updateTimer = new();

    /// <summary>Mean and worst time one attribute pass took since this was last read, and how many
    /// voices it walked. Reading it resets the window. The caller owns the cadence, so the caller is
    /// the one that can say whether it is fast enough.</summary>
    public (double MeanMs, double MaxMs, int Calls, int Voices) TakeUpdateCost()
    {
        var r = (_updateCount > 0 ? _updateMsSum / _updateCount : 0.0, _updateMsMax, _updateCount, _activeSounds.Count);
        _updateMsSum = 0; _updateMsMax = 0; _updateCount = 0;
        return r;
    }

    public void Update()
    {
        if (!_isInitialized) return;

        _updateTimer.Restart();
        ReportMixerLoad();

        // Before anything else: retry plays that were waiting on a NONBLOCKING decode.
        DrainDeferredPlays();

        try
        {
            Vector3 lPosVec = _listenerPos;
            int listenerRegionId = _listenerRegionId;

            lock (_lock)
            {
                _dbgFrame++;
                UpdateActiveReverbs(lPosVec);
                ApplySimulatedReverb(listenerRegionId);

                for (int i = _activeSounds.Count - 1; i >= 0; i--)
                {
                    var active = _activeSounds[i];
                    active.Channel.isPlaying(out bool isPlaying);
                    if (!isPlaying) { 
                        ReleaseActiveSoundResources(active);
                        RemoveActiveAt(i); continue; 
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
            double ms = _updateTimer.Elapsed.TotalMilliseconds;
            _updateMsSum += ms; _updateCount++;
            if (ms > _updateMsMax) _updateMsMax = ms;
        }
    }

    private void ReleaseActiveSoundResources(ActiveSound active)
    {
        ReleaseSteamAudioVoice(active);
        ReleaseThreeEqDsp(active.ThreeEqDsp);
        ReleaseDiffractionDsp(active.DiffractionDsp);
        ReleaseGranularDsp(active.GranularDsp, active.GranularHandle, active.GranularState);
        ReleaseSynthDsp(active.SynthDsp, active.SynthHandle, active.SynthState);
        if (active.EngineDsp.hasHandle())
        {
            // Not pooled: an engine is a whole vehicle's worth of state, and the next one is a
            // different car.
            active.EngineDsp.release();
            if (active.EngineHandle.IsAllocated) active.EngineHandle.Free();
            active.EngineDsp = default;
            active.EngineState = null;
            active.EchoState = null;
        }
    }

    private void UpdateReverbRouting(ActiveSound active, int listenerRegionId)
    {
        if (_acousticMap == null) return;
        int sourceRegionId = active.TargetRegionId;
        
        bool sourceChanged = sourceRegionId != active.CurrentSourceRegionId || !active.SourceReverbConnection.hasHandle();
        bool listenerChanged = listenerRegionId != active.CurrentRegionId || !active.ReverbConnection.hasHandle();
        if (!sourceChanged && !listenerChanged) return; // the steady state, which is nearly every frame

        active.Channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER, out var sourceFader);

        // Update Source Reverb Send (the room the sound is in)
        if (sourceChanged)
        {
            if (TryGetReverbInput(active.CurrentSourceRegionId, out var oldReverb))
                DropSend(ref active.SourceReverbConnection, oldReverb, sourceFader);
            active.SourceReverbConnection = default;

            if (sourceRegionId != -2 && !active.IsReflection && TryGetReverbInput(sourceRegionId, out var sourceReverb))
            {
                sourceReverb.addInput(sourceFader, out active.SourceReverbConnection, DSPCONNECTION_TYPE.SEND);
                active.SourceReverbConnection.setMix(AcousticConstants.ReverbSendMix);
            }
            active.CurrentSourceRegionId = sourceRegionId;
        }

        // Update Listener Reverb Send (the room the listener is in)
        if (listenerChanged)
        {
            if (TryGetReverbInput(active.CurrentRegionId, out var oldListenerReverb))
                DropSend(ref active.ReverbConnection, oldListenerReverb, sourceFader);
            active.ReverbConnection = default;

            if (listenerRegionId != -2 && !active.IsReflection && TryGetReverbInput(listenerRegionId, out var listenerReverb))
            {
                listenerReverb.addInput(sourceFader, out active.ReverbConnection, DSPCONNECTION_TYPE.SEND);
                active.ReverbConnection.setMix(AcousticConstants.ReverbSendMix * AcousticConstants.ReverbCrossSendScale);
            }
            active.CurrentRegionId = listenerRegionId;
        }
    }

    /// <summary>
    /// Longest a voice's position may be carried forward on its own velocity, seconds.
    ///
    /// Sized from how the position is actually SAMPLED, now that the stamp says so honestly. A remote
    /// entity is interpolated once per simulation step (33 ms) and the audio update re-offers it about
    /// every 22 ms, so a position legitimately reaches the far side of fifty milliseconds old before
    /// a newer one exists anywhere in the process. A cap under that is not caution, it is a hold: the
    /// reckoning stops, the source freezes for the remainder of the step, and the freeze is exactly
    /// the artefact this mechanism exists to remove.
    ///
    /// This is NOT permission to stretch the cap until a fault goes quiet. It is short enough that a
    /// source which stops dead — a car that crashes, a stream that drops — cannot be flung more than a
    /// couple of metres before the truth arrives, and anything older than this is a stall, which the
    /// worst-position-age instrument reports rather than papers over.
    /// </summary>
    private const double MaxDeadReckonSeconds = 0.08;

    /// <summary>
    /// Where a source IS NOW, rather than where the game thread last said it was.
    ///
    /// This is the fix for the "auto-tune" on a close pass, and the reason the 250 Hz attribute loop
    /// was not already delivering what it promised. Doppler is computed from the unit vector between
    /// listener and source, and that vector was being recomputed 250 times a second out of a position
    /// that only changes when the game thread resubmits the emitter — measured at 22 ms, so about
    /// 45 Hz. The loop was writing the same pitch five times over and then jumping.
    ///
    /// The size of the jump goes as v^2/d, so it is entirely a NEAR-FIELD problem, which is exactly
    /// how it was reported: "it only happens to ones that are close to me, the further away they are
    /// they pass just fine". A car at fifty metres and 280 km/h swings its radial speed by 2.7 m/s
    /// between updates — 0.8 %, a glide. The same car at FIVE metres swings it by 27 m/s — 7.6 %,
    /// well over a semitone, forty-five times a second. That is not a Doppler shift, it is a pitch
    /// quantiser, and it sounds like one.
    ///
    /// Carrying the source forward on its own velocity makes the vector vary continuously at the
    /// rate this loop actually runs at, which is what the loop was for.
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
    /// One voice, traced at the full attribute rate. Set OPENFPS_AUDIO_TRACE to an entity id.
    ///
    /// The instrument that was missing when a close pass "stopped for a second and then carried on".
    /// Everything that existed averaged or sampled: the census every five seconds, [ADBG] one frame in
    /// sixty. A hold that starts and ends inside 33 milliseconds is invisible to all of it, and a hold
    /// that size is precisely what a 30 Hz position on a 250 Hz loop produces.
    ///
    /// What comes out is a CSV of one pass — time, placed position, bearing, distance, pitch, and the
    /// age of the position each of those was computed from. A staircase is visible in it at a glance:
    /// eight identical rows and a jump, thirty times a second. A glide is not.
    /// </summary>
    private static readonly int _traceEntity =
        int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_AUDIO_TRACE"), out int t) ? t : int.MinValue;

    private void UpdateSpatialPositioning(ActiveSound active, Vector3 lPosVec)
    {
        double now = OpenFPS.Common.AudioClock.Now;
        double positionAge = active.LastAttributeAt > 0 ? now - active.LastAttributeAt : 0;
        if (positionAge > active.WorstPositionAge) active.WorstPositionAge = positionAge;

        Vector3 targetPos = (active.ApparentPosition != Vector3.Zero) ? active.ApparentPosition : active.Position;
        // Placed where it is now, not where it was last told. A reflection carries no velocity of its
        // own (deliberately — see EngineEchoState), so this is a no-op for one.
        targetPos = DeadReckon(active, targetPos, now);
        if (active.ApparentPosition != Vector3.Zero && active.ApparentPosition != active.Position)
        {
            float bias = Math.Clamp(active.CurrentBleed / (active.CurrentBleed + active.CurrentAperture + 0.001f), 0f, 1f);
            targetPos = Vector3.Lerp(active.ApparentPosition, active.Position, bias);
        }

        active.CurrentApparentPosition = Vector3.Lerp(active.CurrentApparentPosition, targetPos, 0.15f);

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
            // Steam Audio path: the channel is 2D (see PlaySpatialSound) so FMOD does not collapse the
            // binaural stereo. The HRTF owns directional panning and distance is applied manually in
            // ApplyAcousticFilters — so we skip FMOD's 3D positioning calls entirely (they would be
            // no-ops on a 2D channel). Feed the listener-relative direction, converting from the game
            // frame (+Z forward) to Steam Audio's (-Z forward).
            Vector3 local = Vector3.Transform(active.CurrentApparentPosition - lPosVec, Quaternion.Conjugate(_listenerRot));
            float len = local.Length();
            if (len > 1e-4f)
            {
                active.SaState.DirX = local.X / len;
                active.SaState.DirY = local.Y / len;
                active.SaState.DirZ = -local.Z / len;
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

        // FMOD native 3D fallback (Steam Audio unavailable): position the mono point source and let
        // FMOD pan + roll off by distance.
        FMOD.VECTOR fpos = FmodHelpers.ToFmodVec(active.CurrentApparentPosition), fvel = FmodHelpers.ToFmodVec(active.Velocity);
        active.Channel.set3DAttributes(ref fpos, ref fvel);
        active.Channel.set3DMinMaxDistance(active.MinDistance, active.Range);

        float distToSound = Vector3.Distance(lPosVec, active.CurrentApparentPosition);
        bool isIndirect = active.ApparentPosition != Vector3.Zero && active.ApparentPosition != active.Position;

        if (isIndirect)
        {
            // Indirect paths (diffraction) fill the room based on aperture size.
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

        // Distance attenuation: when Steam Audio drives panning (set3DLevel 0), FMOD does NOT apply its
        // 3D rolloff, so we apply it ourselves. Use a NATURAL inverse-distance rolloff (≈1/dist) rather
        // than a gentle linear ramp, so a source is loud/present right up close and falls off quickly as
        // you move away — then a smooth fade over the last stretch brings it fully to zero at Range.
        float distAtten = 1.0f;
        if (active.SaState != null)
        {
            float dist = Vector3.Distance(lPosVec, active.CurrentApparentPosition);
            float min = MathF.Max(0.1f, active.MinDistance);
            float span = MathF.Max(0.01f, active.Range - min);
            float inv = Math.Clamp(min / MathF.Max(dist, min), 0.0f, 1.0f);          // 1/dist beyond MinDistance
            float edgeFade = Math.Clamp((active.Range - dist) / (0.25f * span), 0.0f, 1.0f); // last 25% → 0 at Range
            distAtten = inv * edgeFade;
        }

        // Directional cone: Steam Audio channels are 2D, so FMOD's set3DConeSettings no longer fires.
        // Reproduce it manually from the angle between the source's facing (Direction) and the
        // source->listener vector: full volume inside the inner cone, ConeOutsideVolume beyond the
        // outer cone, smooth between. (Native-3D fallback channels still use FMOD's own cone.)
        float coneAtten = 1.0f;
        float coneOffAxis = 0.0f; // 0 = on-axis, 1 = fully outside the cone (drives the off-axis timbre)
        if (active.SaState != null && active.ConeInside < 360f && active.Direction != Vector3.Zero)
        {
            Vector3 toListener = lPosVec - active.CurrentApparentPosition;
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

        active.Channel.setVolume(active.BaseVolume * finalVolFactor * roomGainBonus * distAtten * coneAtten);

        // Doppler: Steam Audio voices play on a 2D channel, so FMOD's own Doppler is bypassed — apply it
        // manually to the channel pitch from the real (not apparent) source/listener motion. Native-3D
        // fallback voices already get FMOD Doppler, so skip them here.
        if (active.SaState != null)
        {
            // The real (not apparent) source position, carried forward to NOW. See DeadReckon: this
            // is what turns a 45 Hz pitch staircase back into the glide the 250 Hz loop exists for.
            Vector3 sourceNow = DeadReckon(active, active.Position, OpenFPS.Common.AudioClock.Now);
            float doppler = OpenFPS.Client.AudioEngine.Core.AudioPhysics.DopplerFactor(
                lPosVec, _listenerVel, sourceNow, active.Velocity, speedOfSound: _speedOfSound);
            float basePitch = (active.GranularState != null || active.SynthState != null || active.EngineState != null || active.EchoState != null) ? 1.0f : active.Pitch;

            // ── A reflection already has its Doppler, and must not be given it twice ────────
            //
            // A reflection voice is a read of the source engine's ring buffer at the extra delay its
            // longer path implies. The position it reads from is the source's PLAY cursor, which is
            // the emission time whose direct sound is arriving right now — so the read rate already
            // carries the direct leg's Doppler, and the delay slewing on top of it adds the rest of
            // the mirrored path's. That sum is the reflection's whole Doppler, correctly.
            //
            // Applying a channel pitch as well counted the listener's own motion a second time: real
            // while it lasted, wrong in size, and worse the faster the listener moved. What a
            // reflection must not inherit is a Doppler that is not its own; what it must not be given
            // is one it already has.
            if (active.EchoState != null && active.IsReflection) active.Channel.setPitch(1.0f);
            else active.Channel.setPitch(basePitch * doppler);
        }

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

            // Directional-source timbre: off-axis, a projecting source (e.g. a megaphone) loses its highs
            // first, then mids — so to the sides it sounds DULL, not just quieter. Combined with the cone
            // volume attenuation above, this gives it a real "beamed" character (bright/present in front).
            if (coneOffAxis > 0f)
            {
                highDb -= coneOffAxis * 36.0f;
                midDb -= coneOffAxis * 14.0f;
            }

            active.ThreeEqDsp.setParameterFloat(0, Math.Clamp(lowDb, -80.0f, 10.0f));
            active.ThreeEqDsp.setParameterFloat(1, Math.Clamp(midDb, -80.0f, 10.0f));
            active.ThreeEqDsp.setParameterFloat(2, Math.Clamp(highDb, -80.0f, 10.0f));
        }

        // Reverb send: a modest, roughly CONSTANT contribution per source. It must NOT grow with distance
        // — the old `0.25 + sqrt(dist)*0.65` made distant sounds drown in reverb and the room "follow" the
        // listener ("the further back I get, the more it sounds like I'm in the room"). The dry path already
        // rolls off with distance (distAtten / FMOD rolloff), so a fixed wet send naturally reads as a
        // wetter ratio when far — without piling on absolute reverb everywhere.
        float baseReverbMix = active.IsReflection
            ? AcousticConstants.ReverbSendMix * Math.Max(0.5f, active.RoomGain) // reflections excite the bus by remaining energy
            : AcousticConstants.ReverbSendMix;

        // The source's OWN room gets the primary send. The listener's room gets only a small cross-send,
        // so a sound in an adjacent room doesn't smear reverb from many directions at once.
        if (active.SourceReverbConnection.hasHandle()) active.SourceReverbConnection.setMix(baseReverbMix);
        if (active.ReverbConnection.hasHandle()) active.ReverbConnection.setMix(baseReverbMix * AcousticConstants.ReverbCrossSendScale);

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
            .Take(MaxActiveReverbBuses)
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
                    SetReverbDirection(regionId, bus, default, outside: false, lPosVec); // fill the room
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
                        // Localize the room's reverb to the doorway (HRTF), so from outside the reverb
                        // arrives from the opening — not spread around the listener.
                        SetReverbDirection(regionId, bus, nearest.Position, outside: true, lPosVec);
                    }
                }
            }

            _reverbVolumes[regionId] = MathHelper.Lerp(_reverbVolumes[regionId], targetVol, AcousticConstants.ReverbFadeSpeed);
            bus.setVolume(_reverbVolumes[regionId]);

            if (_audioDebug && _dbgFrame % 60 == 0)
            {
                string name = _acousticMap.Regions.TryGetValue(regionId, out var rg) ? rg.FriendlyName : "?";
                float rdist = (regionId == _acousticMap.GlobalEnvironmentId) ? 0 : Vector3.Distance(lPosVec, _acousticMap.RegionPositions.GetValueOrDefault(regionId, Vector3.Zero));
                string binaural = "n/a";
                if (_reverbSaVoices.TryGetValue(regionId, out var rv) && rv.Dsp.hasHandle())
                { rv.Dsp.getBypass(out bool byp); binaural = byp ? "OMNI(inside)" : "DOORWAY"; }
                Log.Information("[REVERB] listenerRegion={LR} bus={Rid}({Name}) {Pos} dist={D:F1} target={T:F2} vol={V:F2} reverbMode={B}",
                    listenerRegionId, regionId, name, regionId == listenerRegionId ? "INSIDE" : "outside", rdist, targetVol, _reverbVolumes[regionId], binaural);
            }
        }
    }

    public void UpdateListener(Vector3 position, Quaternion rotation, Vector3 velocity, int regionId)
    {
        if (!_isInitialized) return;
        _listenerPos = position; _listenerRot = rotation; _listenerVel = velocity; _listenerRegionId = regionId;

        // Every ambisonic bed is rotated by this on the mixer thread. It is the whole reason the beds
        // are ambisonic rather than binaural recordings: the field stays fixed in the world and the
        // listener's frame of reference turns underneath it.
        if (_ambientBeds.Count > 0)
        {
            var frame = Phonon.ListenerFrame(rotation);
            foreach (var bed in _ambientBeds.Values) bed.State.Orientation = frame;
        }
        FMOD.VECTOR fpos = FmodHelpers.ToFmodVec(position), fvel = FmodHelpers.ToFmodVec(velocity);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        Vector3 up = Vector3.Transform(Vector3.UnitY, rotation);
        FMOD.VECTOR ffwd = FmodHelpers.ToFmodVec(forward), fup = FmodHelpers.ToFmodVec(up);
        _system.set3DListenerAttributes(0, ref fpos, ref fvel, ref ffwd, ref fup);
    }

    public void UpdateShelter(float shelterFactor) => _shelterFactor = shelterFactor;

    /// <summary>
    /// Hands the mixer what the space immediately around the listener's head looks like: one probe per
    /// direction, each with the distance to whatever is there and what it is made of.
    ///
    /// This replaced a single "distance to the nearest wall" scalar, which could only ever produce one
    /// undifferentiated colouration — a ceiling a metre up and a wall thirty centimetres to the left
    /// sounded the same, and turning your head changed nothing. Each probe now becomes its own
    /// reflection, with its own delay, its own damping and its own place between the ears.
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

        // A tight hard-walled space can put a surface in every direction at once. Each reflection is
        // individually right, but six of them summed onto the master would swamp the direct sound and
        // ride the limiter. Scale the set back together rather than clipping it — the RATIOS between
        // the surfaces are the cue, and they survive.
        float trim = total > AcousticConstants.MaxBoundaryReflectionSum
            ? AcousticConstants.MaxBoundaryReflectionSum / total
            : 1f;

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
                // Silence it rather than detaching it: the mixer glides the gain down, so a surface
                // leaving probe range fades out instead of being cut off.
                s.TargetGainL[i] = 0f;
                s.TargetGainR[i] = 0f;
            }
        }
    }

    /// <summary>Diagnostics (AudioLab): the loudest boundary reflection currently being rendered.</summary>
    internal float BoundaryReflectionLevel => _boundaryState?.LoudestGain ?? 0f;

    /// <summary>
    /// Starts an ambisonic ambience bed, or re-aims an already-playing one at a new level.
    ///
    /// The file must be a full-sphere ambisonic recording — 4 channels for first order, 9 for second,
    /// 16 for third. A stereo file is not ambisonics and is refused rather than played as if it were,
    /// because the failure would otherwise be a soundfield pointing in an arbitrary direction with
    /// nothing to say so.
    /// </summary>
    /// <param name="soundId">Path under ASSETS/SOUNDS, as everywhere else.</param>
    /// <param name="layout">The file's channel layout. AmbiX unless you know otherwise; see
    /// <see cref="AmbisonicFormat"/> for why guessing wrong is silently awful.</param>
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

        // Convert ONCE, on the copy this bed will play, rather than per block on the mixer thread.
        var converted = new float[pcm.Length];
        Array.Copy(pcm, converted, pcm.Length);
        AmbisonicFormat.ConvertToN3d(converted, channels, layout);

        var state = new AmbisonicBedState
        {
            Pcm = converted,
            Channels = channels,
            Order = order,
            SourceSampleRate = sampleRate > 0 ? sampleRate : 44100,
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

        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = _saFrameSize };
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

    /// <summary>Fades a bed out and frees it. Fading rather than cutting, because an ambience that
    /// stops dead is the one thing more noticeable than one that never started.</summary>
    public void StopAmbientBed(string soundId)
    {
        if (!_ambientBeds.TryGetValue(soundId, out var bed)) return;
        _ambientBeds.Remove(soundId);

        if (bed.Channel.hasHandle()) bed.Channel.stop();
        if (bed.Dsp.hasHandle()) bed.Dsp.release();
        ReleaseBedResources(bed.State, bed.Handle);
    }

    /// <summary>Sets the level a bed glides toward. Two beds and two levels is a cross-fade.</summary>
    public void SetAmbientBedVolume(string soundId, float volume)
    {
        if (_ambientBeds.TryGetValue(soundId, out var bed)) bed.State.TargetVolume = volume;
    }

    /// <summary>Diagnostics (AudioLab): the ear levels a bed most recently decoded to.</summary>
    internal bool TryGetAmbientBedLevels(string soundId, out float rmsL, out float rmsR)
    {
        rmsL = 0f; rmsR = 0f;
        if (!_ambientBeds.TryGetValue(soundId, out var bed)) return false;
        rmsL = bed.State.LastRmsL;
        rmsR = bed.State.LastRmsR;
        return true;
    }

    /// <summary>Diagnostics (AudioLab): why a bed is silent, which is otherwise unknowable from outside
    /// the mixer thread.</summary>
    internal string DescribeAmbientBed(string soundId)
    {
        if (!_ambientBeds.TryGetValue(soundId, out var bed)) return "no such bed";
        var s = bed.State;
        string bail = s.Bailed switch
        {
            0 => "none",
            1 => $"BLOCK LENGTH {s.LastBlockLength} != effect frame size {s.FrameSize}",
            2 => "decode effect is null",
            _ => "no PCM"
        };
        return $"callbacks={s.CallbackCount} inputRms={s.InputRms:F5} block={s.LastBlockLength} outCh={s.LastOutChannels} " +
               $"frames={(s.Channels > 0 ? s.Pcm.Length / s.Channels : 0)} pos={s.Position:F0} " +
               $"produced={s.ProducedAudio} bail={bail}";
    }

    private void ReleaseBedResources(AmbisonicBedState state, System.Runtime.InteropServices.GCHandle handle)
    {
        if (state.Effect != IntPtr.Zero) Phonon.iplAmbisonicsDecodeEffectRelease(ref state.Effect);
        if (state.InBuf.data != IntPtr.Zero) Phonon.iplAudioBufferFree(_saContext, ref state.InBuf);
        if (state.OutBuf.data != IntPtr.Zero) Phonon.iplAudioBufferFree(_saContext, ref state.OutBuf);
        if (handle.IsAllocated) handle.Free();
    }

    // --- Active-voice bookkeeping. Every mutation of _activeSounds goes through these so the index and the
    // list cannot drift apart. All of them assume _lock is already held.
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

    /// <summary>The voice an entity's per-frame update should drive, or null. O(1).</summary>
    private ActiveSound? FindActive(int entityId) =>
        _activeById.TryGetValue(entityId, out var voices) && voices.Count > 0 ? voices[0] : null;

    /// <summary>
    /// Asks a live engine voice to fade out, and reports whether it has finished.
    ///
    /// Returns true when the voice is silent and safe to stop — or when there is no such voice, so a
    /// caller can treat "gone" and "never existed" the same way. A synthesized engine has no
    /// zero-crossing to be stopped on, so stopping one without this is a step in the waveform.
    /// </summary>
    /// <summary>Restarts the loudness meter's integration, so a measurement can be taken per
    /// condition rather than averaged across a whole session.</summary>
    public void ResetLoudnessMeter()
    {
        if (!_loudnessMeter.hasHandle()) return;
        _loudnessMeter.setParameterInt(0, 0);
        _loudnessMeter.setParameterInt(0, 1);
    }

    /// <summary>Brings a live engine voice back to full after a fade-out was started. Idempotent.</summary>
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

    public void ReviveEngine(int entityId)
    {
        lock (_lock) { FindActive(entityId)?.EngineState?.Revive(); }
    }

    public bool FadeOutEngine(int entityId)
    {
        lock (_lock)
        {
            var active = FindActive(entityId);
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
    public float GetPlaybackProgress(int entityId) { 
        lock (_lock) { 
            var sound = FindActive(entityId); 
            if (sound == null) return 0f; 
            sound.Channel.getCurrentSound(out var fmodSound); 
            if (!fmodSound.hasHandle()) return 0f; 
            fmodSound.getLength(out uint len, TIMEUNIT.MS); 
            sound.Channel.getPosition(out uint pos, TIMEUNIT.MS); 
            return (len == 0) ? 0f : (float)pos / len; 
        } 
    }
    public IEnumerable<int> GetActiveSpatialSoundIds() { lock(_lock) return new List<int>(_activeById.Keys); }
    public Vector3 GetSoundPosition(int entityId) { lock(_lock) return FindActive(entityId)?.Position ?? Vector3.Zero; }

    public void Preload(string soundId)
    {
        if (!_isInitialized) return;

        // Try preloading as a granular sound first
        _granularBank.TryGetPcmData(soundId, out _, out _, out _);

        // Also preload as a standard FMOD sound. This is exactly what makes the deferred-play path rare:
        // the NONBLOCKING decode is started here, long before anything asks to hear it.
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

        // Route voice through the Steam Audio binaural DSP too, so remote players localize like
        // every other spatial sound (otherwise voice would fall back to FMOD's flat panner).
        // NOTE: voice is one-shot-per-packet, so this creates/releases a binaural effect per packet.
        // Functionally correct; a future optimization is to pool SA voices or use a persistent
        // per-speaker channel instead of one-shot packets.
        SteamAudioVoiceState? vState = null;
        FMOD.DSP vDsp = default;
        System.Runtime.InteropServices.GCHandle vHandle = default;
        if (_steamAudioEnabled && TryCreateSteamAudioVoice(out vState, out vDsp, out vHandle))
        {
            ch.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, vDsp);
            ch.set3DLevel(0.0f);
        }
        ch.setPaused(false);

        // Release the sound object once it finishes — FMOD holds its own copy of the PCM.
        // We queue a callback via the active sound list so it is released in the Update loop.
        lock (_lock)
        {
            AddActive(new ActiveSound
            {
                EntityId = -(senderId + 90000), // negative offset to avoid collision
                SoundId = "__voice__",
                Type = EmitterType.WorldLocked,
                Channel = ch,
                Position = position,
                ApparentPosition = position,
                CurrentApparentPosition = position,
                BaseVolume = 1.0f,
                MinDistance = 1.0f,
                Range = 30.0f,
                IsReflection = false,
                TargetRegionId = -1,
                SaState = vState, SaDsp = vDsp, SaHandle = vHandle
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
            _activeById.Clear();
            ReturnReverbVoices();
            foreach (var dsp in _reverbDsps.Values) dsp.release();
            foreach (var bus in _reverbBuses.Values) bus.release();
            foreach (var id in new List<string>(_ambientBeds.Keys)) StopAmbientBed(id);
            if (_boundaryDsp.hasHandle()) _boundaryDsp.release();
            if (_boundaryHandle.IsAllocated) _boundaryHandle.Free();
            _enginePool?.Dispose(); _enginePool = null;
            _masterTap?.Dispose(); _masterTap = null;
            if (_loudnessMeter.hasHandle()) _loudnessMeter.release();
            if (_masterLimiter.hasHandle()) _masterLimiter.release();
        } 
        StopDiagnosticSound();
        if (_steamAudioEnabled)
        {
            // Free the whole voice pool (active sounds were returned to it above). Release each DSP
            // first (detaches it from the mixer), then its Phonon effect/buffers, then the shared
            // context. This is the ONLY place Phonon voice resources are freed.
            foreach (var v in _saAllVoices)
            {
                if (v.Dsp.hasHandle()) v.Dsp.release();
                if (v.Handle.IsAllocated) v.Handle.Free();
                Phonon.iplAudioBufferFree(_saContext, ref v.State.InBuf);
                Phonon.iplAudioBufferFree(_saContext, ref v.State.OutBuf);
                IntPtr eff = v.State.Effect;
                Phonon.iplBinauralEffectRelease(ref eff);
            }
            _saAllVoices.Clear();
            _saPool.Clear();
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
