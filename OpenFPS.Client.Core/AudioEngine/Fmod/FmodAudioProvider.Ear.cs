using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using FMOD;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Hearing;
using Serilog;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The ear model on every world voice (docs/EAR_MODEL.md): the law's loudness correction as a gain,
/// and the loudness compensation in the voice's <see cref="EarVoiceState"/>.
///
/// Per voice, from its declared level (SpatialEmitter.EarLevelDb), its distance and path, and its
/// spectrum (measured: a recording or a render from its samples, a live voice from its own output, an
/// engine from what it is making now):
///
///   * the correction: how much more or less the law in loudness units gives it than the unweighted
///     law it was placed by (Loudness.TimbreCorrectionDb). Engines and their outlets get none here:
///     their lift is the law (EngineVoiceState.LiftDb).
///   * the compensation: the level it really has at the ear against the level it plays at, as two
///     ISO 226:2023 loudness levels, and the shelves between them (LoudnessCompensation).
///
/// Worked out ten times a second per voice on the audio update thread; the gain slews at 6 dB a
/// second and the shelves in the mixer at the same rate, so nothing steps.
/// </summary>
public partial class FmodAudioProvider
{
    private readonly ConcurrentStack<(FMOD.DSP Dsp, GCHandle Handle, EarVoiceState State)> _earPool = new();

    /// <summary>How often a voice's ear figures are worked out again, seconds.</summary>
    private const double EarRecomputeSeconds = 0.1;
    /// <summary>How often a live voice's measured spectrum is handed to the registry for ranking and
    /// for other voices of the same sound, seconds.</summary>
    private const double EarPublishSeconds = 5.0;
    private double _earReportAt;

    private sealed class EarVoice
    {
        public FMOD.DSP Dsp;
        public GCHandle Handle;
        public EarVoiceState State = null!;
        public float LevelDb, CopyDb;
        public string Key = "";
        public bool Live;
        public Timbre? Timbre;
        public float CorrectionDb, GainDb;
        public bool Started;
        public double NextAt, PublishAt;
        public float RealDb, PlayedDb, RealPhon, PlayedPhon, LowDb, HighDb;
    }

    /// <summary>Puts an ear stage first in a new voice's chain. Inside the provider's lock, before the
    /// voice is added.</summary>
    private void AttachEar(ActiveSound active, in SpatialEmitter emitter)
    {
        if (emitter.Type == EmitterType.UI || !_isInitialized) return;
        var engine = active.EngineState ?? active.TapState?.Source ?? active.EchoState?.Source;
        if (engine == null && emitter.EarLevelDb <= 0f) return;   // nothing declared: nothing to place

        if (!_earPool.TryPop(out var unit))
        {
            var state = new EarVoiceState();
            if (EarProcessor.CreateDSP(_system, state, out var dsp, out var handle) != RESULT.OK) return;
            unit = (dsp, handle, state);
        }
        _system.getSoftwareFormat(out int rate, out _, out _);
        unit.State.SampleRate = rate;
        unit.State.Reset();
        // Last onto the TAIL: the first thing the signal meets, ahead of the reverb send tap.
        if (active.Channel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, unit.Dsp) != RESULT.OK)
        {
            _earPool.Push(unit);
            return;
        }

        bool sample = engine == null && !active.SynthDsp.hasHandle() && !active.GranularDsp.hasHandle()
                      && active.MachineState == null && active.OwnVoice == null && !active.EngineDsp.hasHandle();
        var ear = new EarVoice
        {
            Dsp = unit.Dsp, Handle = unit.Handle, State = unit.State,
            LevelDb = emitter.EarLevelDb, CopyDb = emitter.EarCopyDb,
            Key = !string.IsNullOrEmpty(emitter.PhysicalKey) ? emitter.PhysicalKey : emitter.SoundId ?? "",
            Live = engine == null && !sample,
        };
        if (ear.Live)
        {
            unit.State.Tap ??= new LiveBands(rate);
            unit.State.Tap.Reset();
            unit.State.Tapping = true;
        }
        else if (sample) RequestTimbre(ear.Key);
        ear.Timbre = engine == null ? EarTimbres.Find(ear.Key) : null;
        active.Ear = ear;
        RecomputeEar(active, ear, _listenerPos, OpenFPS.Common.AudioClock.Now);
    }

    /// <summary>Takes a voice's ear stage off its channel and back to the pool. Before the channel stops.</summary>
    private void ReleaseEar(ActiveSound active)
    {
        var ear = active.Ear;
        if (ear == null) return;
        active.Ear = null;
        ear.State.Tapping = false;
        // Not pooled if it could not be detached; and its handle is never freed while the mixer runs.
        if (!Detach(active.Channel, ear.Dsp, "ear stage")) return;
        _earPool.Push((ear.Dsp, ear.Handle, ear.State));
    }

    /// <summary>A re-submission's declared level, for a source whose level is what moves.</summary>
    private static void UpdateEarLevel(ActiveSound active, in SpatialEmitter emitter)
    {
        if (active.Ear == null || emitter.EarLevelDb <= 0f) return;
        active.Ear.LevelDb = emitter.EarLevelDb;
        active.Ear.CopyDb = emitter.EarCopyDb;
    }

    /// <summary>
    /// The voice's loudness correction as a linear gain, slewed; and, when due, its figures worked out
    /// again. Called from ApplyAcousticFilters, which owns the voice's gain.
    /// </summary>
    private float EarGain(ActiveSound active, Vector3 listener, float dt)
    {
        var ear = active.Ear;
        if (ear == null) return 1f;
        double now = OpenFPS.Common.AudioClock.Now;
        if (now >= ear.NextAt) RecomputeEar(active, ear, listener, now);
        float step = EarVoiceState.SlewDbPerSecond * MathF.Max(dt, 0f);
        ear.GainDb += Math.Clamp(ear.CorrectionDb - ear.GainDb, -step, step);
        return MathF.Pow(10f, ear.GainDb / 20f);
    }

    private void RecomputeEar(ActiveSound active, EarVoice ear, Vector3 listener, double now)
    {
        ear.NextAt = now + EarRecomputeSeconds;
        bool on = EarModel.Enabled;
        var engine = active.EngineState ?? active.TapState?.Source ?? active.EchoState?.Source;

        float levelDb, placedDb, correction;
        Timbre? timbre;
        if (engine != null)
        {
            // The engine's lift has already put it where the law puts what it is doing: no gain here.
            levelDb = engine.RunningLevelDb;
            timbre = engine.RunningTimbre;
            correction = 0f;
            if (levelDb <= 0f) { Settle(ear, 0f, 0f, 0f); return; }
            placedDb = Loudness.PlacedDb(levelDb, timbre);
        }
        else
        {
            if (ear.Live && ear.State.Tap != null && ear.State.Tap.Update(0.5f, 2f))
            {
                ear.Timbre = Timbre.FromBandPowers(ear.State.Tap.BandPowers, ear.Key, live: true);
                if (ear.Timbre != null && now >= ear.PublishAt && ear.Key.Length > 0)
                {
                    EarTimbres.Set(ear.Key, Timbre.FromBandPowers(ear.State.Tap.BandPowers, ear.Key)!);
                    ear.PublishAt = now + EarPublishSeconds;
                }
            }
            else if (!ear.Live && (ear.Timbre == null || !EarTimbres.Has(ear.Key)))
                ear.Timbre = EarTimbres.Find(ear.Key) ?? ear.Timbre;
            timbre = ear.Timbre;
            levelDb = ear.LevelDb;
            correction = on ? Loudness.TimbreCorrectionDb(levelDb, timbre) : 0f;
            placedDb = Loudness.PlacedDb(levelDb, timbre);
        }

        float distance = Vector3.Distance(listener, active.Position);
        float pathDb = 20f * MathF.Log10(MathF.Max(1e-4f, active.CurrentMid)) + active.AirMidDb;
        // Real and played levels in the voice's own convention (Loudness: a recording declares its full
        // scale, a physical voice its RMS). A recording not measured yet is taken as a speech line.
        bool physical = engine != null || ear.Live;
        float realOffset = timbre?.RealOffsetDb ?? (physical ? 0f : Loudness.ReferenceRmsDbfs);
        float digitalRms = timbre?.DigitalRmsDb ?? (physical ? Loudness.PhysicalRmsDbfs : Loudness.ReferenceRmsDbfs);
        float real = EarModel.RealAtEarDb(levelDb + realOffset + ear.CopyDb, distance, pathDb);
        float played = EarModel.PlayedAtEarDb(placedDb + ear.CopyDb, digitalRms, active.MinDistance, distance, pathDb);
        var t = timbre ?? Timbre.Speech;
        float realPhon = t.Phons(real), playedPhon = t.Phons(played);
        var (low, high) = on ? LoudnessCompensation.Shelves(realPhon, playedPhon) : (0f, 0f);

        ear.RealDb = real; ear.PlayedDb = played; ear.RealPhon = realPhon; ear.PlayedPhon = playedPhon;
        Settle(ear, correction, low, high);
    }

    private static void Settle(EarVoice ear, float correctionDb, float lowDb, float highDb)
    {
        ear.CorrectionDb = correctionDb;
        ear.LowDb = lowDb; ear.HighDb = highDb;
        if (!ear.Started)
        {
            // A new voice starts where it belongs, not on its way there.
            ear.GainDb = correctionDb;
            ear.State.Snap(lowDb, highDb);
            ear.Started = true;
        }
        else ear.State.SetTarget(lowDb, highDb);
    }

    /// <summary>
    /// Has a recording or a rendered buffer measured, once: its samples copied out of FMOD here, the
    /// analysis on the thread pool. Until it is done the voice uses its folder's measurement, if any.
    /// </summary>
    private void RequestTimbre(string soundId)
    {
        if (string.IsNullOrEmpty(soundId) || EarTimbres.Requested(soundId)) return;
        if (_resources.TryGetSound(soundId, out FMOD.Sound sound) != SoundLoadState.Ready) return;
        try
        {
            if (sound.getFormat(out _, out SOUND_FORMAT format, out int channels, out int bits) != RESULT.OK || channels <= 0) return;
            sound.getDefaults(out float frequency, out _);
            if (sound.getLength(out uint bytes, TIMEUNIT.PCMBYTES) != RESULT.OK || bytes == 0) return;
            int bytesPerSample = bits / 8;
            if (format is not (SOUND_FORMAT.PCM16 or SOUND_FORMAT.PCMFLOAT or SOUND_FORMAT.PCM8 or SOUND_FORMAT.PCM24 or SOUND_FORMAT.PCM32)
                || bytesPerSample <= 0) return;
            int rate = (int)MathF.Round(frequency);
            if (rate <= 0) return;
            // Up to ten seconds of it.
            uint want = (uint)Math.Min(bytes, (long)rate * 10 * channels * bytesPerSample);
            if (sound.@lock(0, want, out IntPtr p1, out IntPtr p2, out uint l1, out uint l2) != RESULT.OK) return;
            float[] mono;
            try
            {
                int frames = (int)(l1 / (uint)(channels * bytesPerSample));
                mono = new float[frames];
                unsafe
                {
                    byte* b = (byte*)p1;
                    for (int i = 0; i < frames; i++)
                    {
                        float s = 0f;
                        for (int c = 0; c < channels; c++)
                        {
                            byte* at = b + (i * channels + c) * bytesPerSample;
                            s += format switch
                            {
                                SOUND_FORMAT.PCMFLOAT => *(float*)at,
                                SOUND_FORMAT.PCM16 => *(short*)at / 32768f,
                                SOUND_FORMAT.PCM8 => (*at - 128) / 128f,
                                SOUND_FORMAT.PCM24 => ((at[0] | (at[1] << 8) | ((sbyte)at[2] << 16))) / 8388608f,
                                _ => *(int*)at / 2147483648f,
                            };
                        }
                        mono[i] = s / channels;
                    }
                }
            }
            finally { sound.unlock(p1, p2, l1, l2); }
            EarTimbres.MeasureAsync(soundId, mono, rate);
        }
        catch (Exception ex)
        {
            Log.Debug("Ear model: {Sound} not measured: {Message}", soundId, ex.Message);
        }
    }

    // ── The wind at the ears ──────────────────────────────────────────────────────────────────
    //
    // Not a world voice: it is made at the ears and placed by the same law straight from its declared
    // ear level (EarWind.PlacedDb). It gets the same two things as every other voice, from its own
    // measured spectrum: the law's correction (a gain on its channel) and the compensation (the
    // shelves). Its real level at the ear is its declared level.
    private EarVoiceState? _windEar;
    private FMOD.DSP _windEarDsp;
    private GCHandle _windEarHandle;
    private Timbre? _windTimbre;
    private float _windGainDb, _windCorrectionDb;
    private double _windEarAt;
    private bool _windEarStarted;

    /// <summary>The wind's ear stage, for the log and the lab: its correction, dB.</summary>
    public float EarWindCorrectionDb => _windGainDb;

    private void AttachEarToWind()
    {
        if (_earWind == null || !_earWindChannel.hasHandle()) return;
        var state = new EarVoiceState();
        if (EarProcessor.CreateDSP(_system, state, out var dsp, out var handle) != RESULT.OK) return;
        _system.getSoftwareFormat(out int rate, out _, out _);
        state.SampleRate = rate;
        state.Reset();
        state.Tap = new LiveBands(rate);
        state.Tapping = true;
        if (_earWindChannel.addDSP(CHANNELCONTROL_DSP_INDEX.TAIL, dsp) != RESULT.OK) return;   // kept, never freed
        _windEar = state;
        _windEarDsp = dsp;
        _windEarHandle = handle;
    }

    private void UpdateEarWind(float dt)
    {
        if (_windEar == null || _earWind == null) return;
        double now = OpenFPS.Common.AudioClock.Now;
        if (now >= _windEarAt)
        {
            _windEarAt = now + EarRecomputeSeconds;
            if (_windEar.Tap != null && _windEar.Tap.Update(0.5f, 2f))
                _windTimbre = Timbre.FromBandPowers(_windEar.Tap.BandPowers, "ear wind", live: true);
            var ears = _earWind.Synth.Last;
            float declared = ears.DeclaredDb;
            bool on = EarModel.Enabled;
            if (declared > 0f && _windTimbre != null)
            {
                _windCorrectionDb = on ? Loudness.TimbreCorrectionDb(declared, _windTimbre) : 0f;
                // At the ear there is no distance: what the law places is the level itself.
                float real = MathF.Max(ears.LeftDb, ears.RightDb);
                float played = EarModel.PlaybackFullScaleDb + Loudness.PhysicalRmsDbfs
                             + (declared - Loudness.RenderCeilingDb) * Loudness.DynamicRangeCompression
                             + _windCorrectionDb + (real - declared);
                var (low, high) = on ? LoudnessCompensation.Shelves(_windTimbre.Phons(real), _windTimbre.Phons(played)) : (0f, 0f);
                if (!_windEarStarted) { _windEar.Snap(low, high); _windGainDb = _windCorrectionDb; _windEarStarted = true; }
                else _windEar.SetTarget(low, high);
            }
            else { _windCorrectionDb = 0f; _windEar.SetTarget(0f, 0f); }
        }
        float step = EarVoiceState.SlewDbPerSecond * MathF.Max(dt, 0f);
        _windGainDb += Math.Clamp(_windCorrectionDb - _windGainDb, -step, step);
        _earWindChannel.setVolume(MathF.Pow(10f, _windGainDb / 20f));
    }

    /// <summary>
    /// One line every ten seconds: the listening level and the loudest few voices as the ear model
    /// sees them. "[EAR]" in the log.
    /// </summary>
    private void ReportEar()
    {
        UpdateEarWind(_attributeDt);
        double now = OpenFPS.Common.AudioClock.Now;
        if (now < _earReportAt) return;
        _earReportAt = now + 10.0;
        var top = new System.Collections.Generic.List<(float Played, string Line)>();
        foreach (var a in _activeSounds)
        {
            var e = a.Ear;
            if (e == null || !e.Started) continue;
            string name = a.EngineState != null ? "engine:" + a.EngineState.Vehicle.EngineKey : e.Key;
            if (name.Length > 28) name = "..." + name[^25..];
            top.Add((e.PlayedDb, $"{name} {e.RealDb:F0}->{e.PlayedDb:F0} dB ({e.RealPhon:F0}->{e.PlayedPhon:F0} phon) gain {e.GainDb:+0.0;-0.0} shelves {e.LowDb:+0.0;-0.0}/{e.HighDb:+0.0;-0.0}"));
        }
        if (top.Count == 0 && _windGainDb == 0f && _windCorrectionDb == 0f) return;
        top.Sort((x, y) => y.Played.CompareTo(x.Played));
        Log.Information("[EAR] {State}, listening level {Listening:F0} dB, ear wind gain {Wind:+0.0;-0.0}; {Voices}",
            EarModel.Enabled ? "on" : "OFF", EarModel.ListeningLevelDb, _windGainDb,
            string.Join("; ", top.GetRange(0, Math.Min(5, top.Count)).ConvertAll(t => t.Line)));
    }
}
