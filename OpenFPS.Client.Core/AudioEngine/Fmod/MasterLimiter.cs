using System;
using System.Runtime.InteropServices;
using FMOD;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The master limiter: a look-ahead, true-peak, linked-stereo limiter with a program-dependent release.
///
/// WHY NOT FMOD'S. FMOD's LIMITER has no look-ahead. A shot or a thunder crack reaches full height in a
/// sample or two, and a limiter that only reacts to what it has already passed cannot get its gain down
/// in time, so the leading edge is cut flat at the ceiling: measured on Cody's 16-minute capture, 975
/// flat-topped runs at exactly -2.0 dBFS around gunfire and the storm, and up to 7.9 dB of reduction on
/// a thunder crack 3 km away (docs/AUDIO_QUALITY_2026-10-06.md, finding 6). Flat tops are clipping, and
/// clipping on a crack is crunch. Its channels were also limited independently, so a shot on the left
/// moved the image to the right while it was being held down.
///
/// WHAT THIS DOES, per sample:
/// 1. Makeup (the maximizer's gain, <see cref="FmodAudioProvider.MasterMakeupDb"/>, unchanged).
/// 2. TRUE PEAK, ITU-R BS.1770-4 Annex 2: the signal four times oversampled with the
///    recommendation's own 48-tap interpolator, so the peak between two samples, which is what a
///    sound card's reconstruction filter actually puts out, counts as well as the samples. Both
///    channels feed one detector (LINKED): one gain for the pair keeps the image where it is.
/// 3. LOOK-AHEAD. The audio is delayed by <see cref="LatencySamples"/> (2 ms), and the gain needed
///    for a peak is held for the whole window ahead of it, then smoothed by two running means, so
///    the gain is already down when the peak arrives and has come down along an S-curve over the
///    look-ahead: no sample is cut, and the attack is not a step.
/// 4. RELEASE, which depends on the programme. The gain reduction is held for <see cref="HoldSeconds"/>
///    (longer than half a period of 20 Hz, so it does not ride the waveform of a rumble and distort it),
///    then returns FAST (<see cref="FastSeconds"/>) down to the reduction the material has been
///    needing on average, and SLOWLY (<see cref="SlowSeconds"/>) from there. A single shot leaves
///    almost nothing in the average and recovers in about a tenth of a second; a long thunder roll
///    builds the average up and is let go of slowly, which is what keeps a held-down roll from pumping.
///
/// The gain the hold and the means produce can never be above the gain any peak in the window needs,
/// so the output's true peak stays at the ceiling to within the interpolator's accuracy (a few
/// hundredths of a dB).
///
/// Mixer thread: everything is allocated in the constructor; <see cref="Process"/> does not
/// allocate, lock or throw for any input, and a non-finite sample is taken as silence.
/// </summary>
public sealed class TruePeakLimiter
{
    public const int MaxChannels = 8;

    /// <summary>The ceiling, dB true peak. One under full scale: the headroom a lossy encoder or a sound
    /// card's own resampler needs, and the level BS.1770 / EBU R128 practice sets.</summary>
    public const float DefaultCeilingDb = -1f;

    /// <summary>How far ahead the gain looks, seconds (the attack takes all of it).</summary>
    public const float LookAheadSeconds = 0.002f;
    /// <summary>Gain reduction held after its peak before it starts to come back, seconds.</summary>
    public const float HoldSeconds = 0.025f;
    /// <summary>Release down to the programme's average reduction, time constant, seconds.</summary>
    public const float FastSeconds = 0.040f;
    /// <summary>Release of the average reduction itself, time constant, seconds.</summary>
    public const float SlowSeconds = 0.500f;
    /// <summary>How long the reduction is averaged over to decide how much is sustained, seconds.</summary>
    public const float AverageSeconds = 0.400f;
    /// <summary>A peak within this much of the reduction already held renews the hold, dB: the
    /// interpolator's estimate of a steady tone wobbles by about this between periods.</summary>
    private const float HoldToleranceDb = 0.2f;

    /// <summary>ITU-R BS.1770-4 Annex 2: the four phases of the 48-tap, 4x oversampling interpolator.
    /// Phase k estimates the signal (2k+1)/8 of a sample after x[m-6], from x[m-11..m].</summary>
    private static readonly float[] Phases =
    {
         0.0017089843750f,  0.0109863281250f, -0.0196533203125f,  0.0332031250000f, -0.0594482421875f,  0.1373291015625f,
         0.9721679687500f, -0.1022949218750f,  0.0476074218750f, -0.0266113281250f,  0.0148925781250f, -0.0083007812500f,
        -0.0291748046875f,  0.0292968750000f, -0.0517578125000f,  0.0891113281250f, -0.1665039062500f,  0.4650878906250f,
         0.7797851562500f, -0.2003173828125f,  0.1015625000000f, -0.0582275390625f,  0.0330810546875f, -0.0189208984375f,
        -0.0189208984375f,  0.0330810546875f, -0.0582275390625f,  0.1015625000000f, -0.2003173828125f,  0.7797851562500f,
         0.4650878906250f, -0.1665039062500f,  0.0891113281250f, -0.0517578125000f,  0.0292968750000f, -0.0291748046875f,
        -0.0083007812500f,  0.0148925781250f, -0.0266113281250f,  0.0476074218750f, -0.1022949218750f,  0.9721679687500f,
         0.1373291015625f, -0.0594482421875f,  0.0332031250000f, -0.0196533203125f,  0.0109863281250f,  0.0017089843750f,
    };
    private const int Taps = 12;
    /// <summary>The detector reports on x[m-6] when x[m] arrives.</summary>
    private const int DetectorLag = 6;

    public readonly int SampleRate;
    /// <summary>How far behind its input the output is, samples: the look-ahead plus the detector's lag.</summary>
    public readonly int LatencySamples;
    public double LatencySeconds => (double)LatencySamples / SampleRate;

    private float _ceiling, _makeup;

    // Detector: the last Taps input samples per channel, twice over so a window is contiguous.
    private readonly float[] _hist;        // [ch][2*Taps]
    private int _histPos;

    // Look-ahead: the running minimum of the needed gain over _window samples (a monotonic queue).
    private readonly int _window;
    private readonly float[] _qVal;
    private readonly long[] _qAt;
    private int _qHead, _qCount;
    private long _t;

    // Release.
    private float _envDb, _avgDb;
    private int _hold;
    private readonly int _holdSamples;
    private readonly float _aFast, _aSlow, _aAvg;

    // Smoothing: two running means of _mean samples each.
    private readonly int _mean;
    private readonly float[] _m1, _m2;
    private int _mPos;
    private double _s1, _s2;

    // The audio's delay line, interleaved.
    private readonly float[] _delay;
    private int _delayPos;
    private int _channels;

    /// <summary>Gain reduction now, dB (positive), and the most since the last read.</summary>
    public float ReductionDb { get; private set; }
    private float _maxReductionDb;
    /// <summary>Samples whose input was not finite, taken as silence.</summary>
    public int NonFiniteSamples;

    public TruePeakLimiter(int sampleRate, float ceilingDb = DefaultCeilingDb, float makeupDb = 0f)
    {
        SampleRate = sampleRate > 0 ? sampleRate : 48000;
        _mean = Math.Max(2, (int)MathF.Round(LookAheadSeconds * SampleRate / 2f) + 1);
        _window = 2 * _mean - 1;
        LatencySamples = DetectorLag + 2 * _mean - 2;
        _holdSamples = (int)(HoldSeconds * SampleRate);
        _aFast = MathF.Exp(-1f / (FastSeconds * SampleRate));
        _aSlow = MathF.Exp(-1f / (SlowSeconds * SampleRate));
        _aAvg = 1f - MathF.Exp(-1f / (AverageSeconds * SampleRate));
        _hist = new float[MaxChannels * 2 * Taps];
        _qVal = new float[_window + 1];
        _qAt = new long[_window + 1];
        _m1 = new float[_mean];
        _m2 = new float[_mean];
        _delay = new float[MaxChannels * (LatencySamples + 1)];
        Ceiling = ceilingDb;
        MakeupDb = makeupDb;
        Reset();
    }

    /// <summary>The ceiling, dBTP.</summary>
    public float Ceiling { get => 20f * MathF.Log10(_ceiling); set => _ceiling = MathF.Pow(10f, Math.Clamp(value, -24f, 0f) / 20f); }
    /// <summary>The gain in front of the limiter, dB.</summary>
    public float MakeupDb { get => 20f * MathF.Log10(_makeup); set => _makeup = MathF.Pow(10f, Math.Clamp(value, -24f, 40f) / 20f); }

    /// <summary>Most gain reduction since the last call, dB; resets the count.</summary>
    public float TakeMaxReductionDb() { float m = _maxReductionDb; _maxReductionDb = 0f; return m; }

    /// <summary>Forgets everything: the delay line, the detector, the gain (back to unity).</summary>
    public void Reset()
    {
        Array.Clear(_hist);
        Array.Clear(_delay);
        _histPos = 0; _delayPos = 0;
        _qHead = 0; _qCount = 0; _t = 0;
        _envDb = 0f; _avgDb = 0f; _hold = 0;
        for (int i = 0; i < _mean; i++) { _m1[i] = 1f; _m2[i] = 1f; }
        _s1 = _mean; _s2 = _mean; _mPos = 0;
        ReductionDb = 0f;
    }

    /// <summary>As the pointer form, for managed buffers (the lab and the tests): <paramref name="input"/>
    /// and <paramref name="output"/> interleaved, the same length.</summary>
    public unsafe void Process(ReadOnlySpan<float> input, Span<float> output, int channels)
    {
        if (channels <= 0 || output.Length < input.Length) return;
        fixed (float* x = input, y = output) Process(x, y, input.Length / channels, channels);
    }

    /// <summary>
    /// Limits <paramref name="frames"/> interleaved frames of <paramref name="channels"/> channels from
    /// <paramref name="input"/> into <paramref name="output"/> (which may be the same buffer). A layout
    /// wider than <see cref="MaxChannels"/> is not this game's: such a block is passed through unlimited.
    /// </summary>
    public unsafe void Process(float* input, float* output, int frames, int channels)
    {
        if (channels <= 0 || frames <= 0) return;
        if (channels > MaxChannels)
        {
            if (input != output) Buffer.MemoryCopy(input, output, (long)frames * channels * 4, (long)frames * channels * 4);
            return;
        }
        if (channels != _channels) { _channels = channels; Reset(); }
        int stride = 2 * Taps, line = LatencySamples + 1;
        fixed (float* hist = _hist, phases = Phases, delay = _delay)
        {
            for (int n = 0; n < frames; n++)
            {
                // ── 1, 2: makeup, and the true peak of the pair ────────────────────────────────
                int hp = _histPos;
                float peak = 0f;
                for (int c = 0; c < channels; c++)
                {
                    float x = input[n * channels + c];
                    if (!float.IsFinite(x)) { x = 0f; NonFiniteSamples++; }
                    x *= _makeup;
                    float* h = hist + c * stride;
                    h[hp] = x; h[hp + Taps] = x;                  // h[hp+1 .. hp+Taps] is x[m-11..m]
                    float* w = h + hp + 1;
                    float a = MathF.Abs(w[Taps - 1 - DetectorLag]), b = MathF.Abs(w[Taps - DetectorLag]);
                    float p = MathF.Max(a, b);
                    for (int k = 0; k < 4; k++)
                    {
                        float* f = phases + k * Taps;
                        float y = 0f;
                        for (int j = 0; j < Taps; j++) y += f[j] * w[Taps - 1 - j];
                        p = MathF.Max(p, MathF.Abs(y));
                    }
                    peak = MathF.Max(peak, p);
                    // The audio into the delay line, made up.
                    delay[_delayPos * MaxChannels + c] = x;
                }
                _histPos = hp + 1 == Taps ? 0 : hp + 1;

                // ── 3: the gain this peak needs, held over the look-ahead window ────────────────
                float need = peak > _ceiling ? _ceiling / peak : 1f;
                while (_qCount > 0 && _qVal[(_qHead + _qCount - 1) % _qVal.Length] >= need) _qCount--;
                int tail = (_qHead + _qCount) % _qVal.Length;
                _qVal[tail] = need; _qAt[tail] = _t; _qCount++;
                while (_t - _qAt[_qHead] >= _window) { _qHead = (_qHead + 1) % _qVal.Length; _qCount--; }
                float held = _qVal[_qHead];
                _t++;

                // ── 4: release ────────────────────────────────────────────────────────────────
                // Two parts. _avgDb is what the material has been needing: it builds up toward the
                // reduction while there is one to hold, and once released it decays SLOWLY. _envDb is
                // the reduction applied: never less than what is needed now, and released FAST, but
                // only as far as _avgDb. A shot builds little and is let go of at once; a roll builds
                // a lot and is let go of slowly.
                float t = held < 1f ? -20f * MathF.Log10(held) : 0f;
                if (t >= _envDb - HoldToleranceDb && t > 0f)
                {
                    if (t > _envDb) _envDb = t;
                    _hold = _holdSamples;
                }
                if (_hold > 0)
                {
                    _hold--;
                    _avgDb += (_envDb - _avgDb) * _aAvg;
                }
                else if (_envDb > 0f)
                {
                    _avgDb = MathF.Max(t, _avgDb * _aSlow);
                    float floor = MathF.Max(t, _avgDb);
                    _envDb = floor + (_envDb - floor) * _aFast;
                    if (_envDb < 1e-4f) { _envDb = 0f; _avgDb = 0f; }
                }
                float g = _envDb > 0f ? MathF.Exp(_envDb * -0.11512925f) : 1f;   // 10^(-dB/20)

                // ── the two running means: the attack's S-curve ────────────────────────────────
                int mp = _mPos;
                _s1 += g - _m1[mp]; _m1[mp] = g;
                float g1 = (float)(_s1 / _mean);
                _s2 += g1 - _m2[mp]; _m2[mp] = g1;
                float gain = (float)(_s2 / _mean);
                _mPos = mp + 1 == _mean ? 0 : mp + 1;
                if (gain > 1f) gain = 1f;

                // ── out: the delayed audio at the gain for it ─────────────────────────────────
                int read = _delayPos + 1 == line ? 0 : _delayPos + 1;   // the oldest: LatencySamples ago
                for (int c = 0; c < channels; c++)
                    output[n * channels + c] = delay[read * MaxChannels + c] * gain;
                _delayPos = read;

                float red = gain < 1f ? -20f * MathF.Log10(gain) : 0f;
                ReductionDb = red;
                if (red > _maxReductionDb) _maxReductionDb = red;
            }
        }
        // The running sums are doubles fed floats; re-add them now and then so no drift can build up.
        if ((_t & 0xFFFF) < (uint)frames) { _s1 = 0; _s2 = 0; for (int i = 0; i < _mean; i++) { _s1 += _m1[i]; _s2 += _m2[i]; } }
    }
}

/// <summary>
/// The <see cref="TruePeakLimiter"/> as a unit on FMOD's master group, where FMOD's LIMITER was: at the
/// head of the chain, ahead of the loudness meter, the captures and the dither. OPENFPS_LIMITER=fmod
/// puts FMOD's own back, for an A/B.
/// </summary>
public sealed class MasterLimiter : IDisposable
{
    public readonly TruePeakLimiter Core;
    private static DSP_READ_CALLBACK? _callback;
    private FMOD.DSP _dsp;
    private GCHandle _handle;
    private int _faultReported;

    /// <summary>OPENFPS_LIMITER=fmod: FMOD's limiter (no look-ahead, sample peak, -2 dBFS) instead.</summary>
    public static bool UseFmodLimiter => string.Equals(Environment.GetEnvironmentVariable("OPENFPS_LIMITER")?.Trim(), "fmod", StringComparison.OrdinalIgnoreCase);

    private MasterLimiter(int rate, float makeupDb) { Core = new TruePeakLimiter(rate, TruePeakLimiter.DefaultCeilingDb, makeupDb); }

    public FMOD.DSP Dsp => _dsp;

    /// <summary>Makes the unit (not yet on any group). Null if FMOD would not make it.</summary>
    public static MasterLimiter? Create(FMOD.System system, int rate, float makeupDb)
    {
        try
        {
            var l = new MasterLimiter(rate, makeupDb);
            _callback ??= ReadCallback;
            var desc = new DSP_DESCRIPTION
            {
                pluginsdkversion = VERSION.number,
                numinputbuffers = 1,
                numoutputbuffers = 1,
                read = _callback,
            };
            if (system.createDSP(ref desc, out l._dsp) != RESULT.OK) return null;
            l._handle = GCHandle.Alloc(l);
            l._dsp.setUserData(GCHandle.ToIntPtr(l._handle));
            return l;
        }
        catch { return null; }
    }

    private static RESULT ReadCallback(ref DSP_STATE state, IntPtr inbuffer, IntPtr outbuffer,
                                       uint length, int inchannels, ref int outchannels)
    {
        try
        {
            if (outchannels == 0) outchannels = inchannels;
            IntPtr user = DspCallback.UserData(ref state);
            var self = user != IntPtr.Zero ? GCHandle.FromIntPtr(user).Target as MasterLimiter : null;
            if (self == null || inbuffer == IntPtr.Zero || outbuffer == IntPtr.Zero || inchannels != outchannels)
            {
                DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels);
                return RESULT.OK;
            }
            unsafe { self.Core.Process((float*)inbuffer, (float*)outbuffer, (int)length, inchannels); }
            if (self.Core.NonFiniteSamples > 0)
            {
                self.Core.NonFiniteSamples = 0;
                NonFinite.Report(ref self._faultReported, "MasterLimiter input");
            }
            return RESULT.OK;
        }
        catch (Exception ex)
        {
            DspCallback.PassThrough(inbuffer, outbuffer, length, inchannels, outchannels > 0 ? outchannels : inchannels);
            DspFault.Record("MasterLimiter", ex);
            return RESULT.OK;
        }
    }

    public void Release()
    {
        try { if (_dsp.hasHandle()) _dsp.release(); } catch { }
    }

    /// <summary>Frees the callback's handle. Only once no callback can be in flight (System.close).</summary>
    public void FreeHandle()
    {
        if (_handle.IsAllocated) _handle.Free();
    }

    public void Dispose() => Release();
}
