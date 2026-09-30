using System;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;
using OpenFPS.Client.AudioEngine.Fmod;   // DspCallback.UserData

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// One outdoor reverb bus's traced stage: what the bus's sends carry, played through the place's
/// measured impulse response (TracedReverb) and decoded round the listener's head. It sits just
/// after the bus's SFXREVERB, which in traced mode passes its input through dry; in room mode this
/// stage is bypassed and the SFXREVERB is the tail, as it always was.
/// </summary>
internal sealed class TracedReverbState
{
    public int FrameSize;
    /// <summary>The traced response's own block (TracedReverb.TracedFrame): the mixer's block is run
    /// through the convolution in pieces of this size.</summary>
    public int SubFrame;
    public IntPtr WorkerContext;          // the tracer's context: the effect must be of the IR's context
    public IntPtr Effect;                 // IPLReflectionEffect (convolution: the whole traced response)
    /// <summary>The same trace's late tail alone: Steam Audio's parametric reverb, built from the decay
    /// times the trace measured, with nothing in its first ~50 ms. Played in the room the listener is
    /// in, where the early part is placed reflections (WorldAudioPlayer.QueueRoomEchoes).</summary>
    public IntPtr TailEffect;
    /// <summary>Play the tail only. Game thread writes.</summary>
    public volatile bool TailOnly;
    /// <summary>The tail: the trace's own late part (LateTailIr), convolved here. Null falls back to
    /// the parametric reverb, as does OPENFPS_TAIL_PARAMETRIC=1 for an A/B.</summary>
    public LateTailConvolver? LateConv;
    public static readonly bool ParametricTail = Environment.GetEnvironmentVariable("OPENFPS_TAIL_PARAMETRIC") == "1";
    public float[] LateOut = Array.Empty<float>();
    public IntPtr Decode;                 // IPLAmbisonicsDecodeEffect (provider context)
    public IntPtr Hrtf;
    public Phonon.IPLAudioBuffer Mono, Ambi, Stereo;
    public float[] MonoScratch = Array.Empty<float>(), StereoScratch = Array.Empty<float>();
    public Phonon.IPLCoordinateSpace3 Orientation;
    public IntPtr ProviderContext;
    /// <summary>Which place this bus is played through: the listener's trace for the room they are
    /// in, the room's own for any other. Game thread writes; the mixer reads it once a block.</summary>
    public volatile TracedReverb? Trace;
    /// <summary>This stage's reader in whatever trace it plays (TracedReverb.MaxReaders).</summary>
    public int Reader;
    /// <summary>Output trim, linear. Game thread writes.</summary>
    public volatile float Gain = 1f;

    /// <summary>One per ear: the tail the trace hands back is the same in both, and a room is not.</summary>
    public readonly EarDecorrelator Left = new(0), Right = new(1);

    // Diagnostics: what goes in and what comes out, for the /reverb line.
    public volatile float InRms, OutRms;
    /// <summary>Running totals of the energy in (mono) and out (per ear), for the lab.</summary>
    public double InEnergy, OutEnergy, ChannelEnergy;
    public int Channels;
    public volatile int Bailed;

    /// <summary>The listener's own room's late tail as a field round the head; null renders it
    /// through the ear decorrelators instead. See <see cref="DiffuseTail"/>.</summary>
    public DiffuseTail? Diffuse;
    /// <summary>The whole soundfield, interleaved, one block of <see cref="SubFrame"/>.</summary>
    public float[] AmbiScratch = Array.Empty<float>();
}

/// <summary>
/// The late tail of the room you are in, rendered as the diffuse field it is.
///
/// Steam Audio's parametric reverb, which plays that tail (TracedReverbState.TailEffect), writes
/// one channel: W, the omnidirectional one. Decoded, one channel is the same signal in both ears,
/// and a sound that is the same in both ears is heard inside the head, or straight ahead — and it
/// stays there when the head turns, because there is nothing in it to turn. That was the room
/// "in front of me no matter where I turn" (2026-09-29), after the placed early reflections had
/// been put round the head and measured moving with it. Splitting the one channel into two ears
/// with different all-pass chains (EarDecorrelator) lowered the correlation but not the place: the
/// two ears' signals were still not anything from anywhere.
///
/// A real late tail is energy arriving from every direction at once, each direction's share a
/// different signal with the same statistics. That is what is made here: the tail through eight
/// short all-pass chains (DiffuseBranch), each encoded into the soundfield from one of eight fixed
/// directions in the WORLD — the corners of a cube round the listener — and the field decoded
/// through the HRTF in the listener's frame like every other soundfield. Each ear then hears eight
/// directions through eight head-related responses, which is where a diffuse field's interaural
/// correlation comes from in life (about 1 below 200 Hz, falling through 0.5 near 500 Hz to near 0
/// above 2 kHz), and when the head turns each branch moves to a different response: the tail's fine
/// structure turns with the head while its level and colour do not, as a room's does.
///
/// Eight directions, equal weight: the trace measures the flat's late field within a decibel or
/// two of isotropic (its first-order channels 20 dB under W past 50 ms, --traced-reverb), and the
/// parametric tail carries no direction to weight by.
///
/// Two calibrations, both measured rather than assumed (--sa-encode):
/// - Steam Audio's encoder writes W at 1/sqrt(4 pi) of its input, not 1. The scale is measured at
///   creation by running noise through one encoder and reading W back (<see cref="WScale"/>), and
///   each branch is then scaled by 1/sqrt(8), so eight uncorrelated branches carry the tail's energy.
/// - Its decoder sums its virtual loudspeakers' head responses, and for a signal that is the SAME in
///   all of them — a one-channel W field, or the eight branches below the frequency their all-passes
///   can tell apart — that sum is coherent: +10 dB below 300 Hz against unity for a diffuse field.
///   The old one-channel tail had that ten decibels of bass on it, which is the "boom" of the room.
///   So the tail's low end, below 300 Hz, does not go through the decoder at all: it is added to both
///   ears as it is, which is what a diffuse field is at those frequencies on a head (interaural
///   correlation near 1, level equal to the field's). Only the part above goes round the eight
///   directions. The split is the EarDecorrelator's, two one-pole stages and their exact complement.
/// </summary>
internal sealed class DiffuseTail
{
    public readonly IntPtr[] Encoders = new IntPtr[DiffuseBranch.Count];
    public readonly Phonon.IPLVector3[] Directions = new Phonon.IPLVector3[DiffuseBranch.Count];
    public readonly DiffuseBranch[] Branches = new DiffuseBranch[DiffuseBranch.Count];
    public float[] W = Array.Empty<float>(), Branch = Array.Empty<float>(), Field = Array.Empty<float>(), Sum = Array.Empty<float>();
    /// <summary>The tail below the split, one block: added to both ears past the decoder.</summary>
    public float[] Low = Array.Empty<float>();
    public Phonon.IPLAudioBuffer Mono, Encoded;
    public IntPtr Context;
    public bool Ready;
    /// <summary>What one encoder's W is, against its input: measured at creation.</summary>
    public float WScale = 1f;
    /// <summary>What the decoder makes of the eight-direction field, per ear, against the tail that
    /// went in: measured at creation, and taken back out, so an ear gets the tail's energy — which
    /// is what a diffuse field is at an ear, give or take the pinna's few decibels above 2 kHz.</summary>
    public float DecodeTrim = 1f;
    public float Gain => WScale * DecodeTrim / MathF.Sqrt(DiffuseBranch.Count);

    /// <summary>
    /// Which way the late energy leans, game world, length its |I|/E (0 from everywhere, 1 from one
    /// way): set from each source's own trace (LateField). A room's is near zero and the eight
    /// directions stay equal; in a tunnel or a street it points back toward the cars, and each
    /// direction's share becomes max(0, 1 + 3 cos), renormalised so the tail's energy is unchanged.
    /// Game thread writes; read once a block.
    /// </summary>
    public void SetBias(System.Numerics.Vector3 bias)
    {
        Volatile.Write(ref _biasX, bias.X); Volatile.Write(ref _biasY, bias.Y); Volatile.Write(ref _biasZ, bias.Z);
    }
    private float _biasX, _biasY, _biasZ;
    private readonly float[] _branchGain = { 1, 1, 1, 1, 1, 1, 1, 1 };
    private readonly float[] _branchTarget = new float[DiffuseBranch.Count];

    private void UpdateBranchGains(int sub)
    {
        var bias = new System.Numerics.Vector3(Volatile.Read(ref _biasX), Volatile.Read(ref _biasY), Volatile.Read(ref _biasZ));
        float sum = 0f;
        for (int b = 0; b < DiffuseBranch.Count; b++)
        {
            float w = MathF.Max(0f, 1f + 3f * System.Numerics.Vector3.Dot(bias, Direction(b)));
            _branchTarget[b] = w; sum += w;
        }
        float norm = sum > 1e-6f ? DiffuseBranch.Count / sum : 1f;
        // A block is 6 ms; moving an eighth of the way each block is a 50 ms glide, no zipper.
        for (int b = 0; b < DiffuseBranch.Count; b++)
        {
            float target = sum > 1e-6f ? MathF.Sqrt(_branchTarget[b] * norm) : 1f;
            _branchGain[b] += (target - _branchGain[b]) * 0.125f;
        }
    }

    /// <summary>Below this the tail goes straight to both ears; see the class note. Set where the
    /// eight branches stop being distinct signals (their all-passes are 89-431 samples, so about
    /// 100 Hz), not at the ear decorrelator's 300: a step on carpet is nearly all below 300 Hz, and
    /// a tail that is the same in both ears there sits in the head however diffuse the rest is.</summary>
    public const float SplitHz = 120f;
    private readonly float _lpA = 1f - MathF.Exp(-2f * MathF.PI * SplitHz / 44100f);
    private float _a1, _a2, _b1;

    /// <summary>OPENFPS_DIFFUSE_TAIL=0 goes back to the one channel through the ear decorrelators.</summary>
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("OPENFPS_DIFFUSE_TAIL") != "0";

    /// <summary>The eight directions: the corners of a cube round the head, in the game's world,
    /// handed to Steam Audio in its own (Phonon.World).</summary>
    public static System.Numerics.Vector3 Direction(int i)
    {
        float s = 1f / MathF.Sqrt(3f);
        return new System.Numerics.Vector3((i & 1) == 0 ? -s : s, (i & 2) == 0 ? -s : s, (i & 4) == 0 ? -s : s);
    }

    /// <param name="decode">The stage's decoder, with the HRTF it will use, for the level calibration;
    /// it is reset afterwards. Zero skips that calibration.</param>
    /// <param name="orientation">The listener frame the calibration decodes in.</param>
    public static DiffuseTail? Create(IntPtr context, int subFrame, int order, int channels,
                                      IntPtr decode = default, Phonon.IPLCoordinateSpace3 orientation = default, IntPtr hrtf = default)
    {
        var d = new DiffuseTail { Context = context };
        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = subFrame };
        var es = new Phonon.IPLAmbisonicsEncodeEffectSettings { maxOrder = order };
        for (int i = 0; i < DiffuseBranch.Count; i++)
        {
            if (Phonon.iplAmbisonicsEncodeEffectCreate(context, ref au, ref es, out d.Encoders[i]) != Phonon.IPL_STATUS_SUCCESS)
            { d.Release(); return null; }
            d.Directions[i] = Phonon.World(Direction(i));
            d.Branches[i] = new DiffuseBranch(i);
        }
        d.W = new float[subFrame]; d.Branch = new float[subFrame]; d.Low = new float[subFrame];
        d.Field = new float[subFrame * channels]; d.Sum = new float[subFrame * channels];
        Phonon.iplAudioBufferAllocate(context, 1, subFrame, ref d.Mono);
        Phonon.iplAudioBufferAllocate(context, channels, subFrame, ref d.Encoded);
        d.Calibrate(subFrame, channels, order);
        if (decode != IntPtr.Zero) d.CalibrateDecode(subFrame, channels, order, decode, orientation, hrtf);
        d.Ready = true;
        return d;
    }

    /// <summary>Noise through the whole path — split, branches, encoders, the decoder — and each
    /// ear's energy against the noise. The first blocks are skipped for the HRTF's latency and the
    /// branches' fill; everything is reset afterwards.</summary>
    private void CalibrateDecode(int sub, int channels, int order, IntPtr decode, Phonon.IPLCoordinateSpace3 orientation, IntPtr hrtf)
    {
        var field = new Phonon.IPLAudioBuffer(); Phonon.iplAudioBufferAllocate(Context, channels, sub, ref field);
        var ears = new Phonon.IPLAudioBuffer(); Phonon.iplAudioBufferAllocate(Context, 2, sub, ref ears);
        var inter = new float[sub * channels]; var st = new float[sub * 2];
        var dp = new Phonon.IPLAmbisonicsDecodeEffectParams { order = order, hrtf = hrtf, orientation = orientation, binaural = Phonon.IPL_TRUE };
        var rng = new Random(13);
        double eIn = 0, eEar = 0;
        int blocks = Math.Max(12, 44100 / sub);           // about a second
        for (int b = 0; b < blocks; b++)
        {
            Array.Clear(inter);
            for (int k = 0; k < sub; k++) inter[k * channels] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
            Render(inter, sub, channels, order);
            Phonon.iplAudioBufferDeinterleave(Context, Sum, ref field);
            Phonon.iplAmbisonicsDecodeEffectApply(decode, ref dp, ref field, ref ears);
            Phonon.iplAudioBufferInterleave(Context, ref ears, st);
            if (b < 4) continue;
            for (int k = 0; k < sub; k++)
            {
                eIn += inter[k * channels] * (double)inter[k * channels];
                double l = st[k * 2] + Low[k], r = st[k * 2 + 1] + Low[k];
                eEar += (l * l + r * r) / 2;
            }
        }
        // The low end went round the decoder at unity; only the field's share is trimmed, so solve
        // for the trim that brings the whole to unity: ear = low + trim² × (ear − low) ⇒ measured
        // once more with the low alone would be exact; the low is a small share of white noise
        // (about 1/70 of it below 300 Hz), so the whole is trimmed and the low end is left as it is.
        if (eEar > 0 && eIn > 0) DecodeTrim = (float)Math.Sqrt(eIn / eEar);
        Phonon.iplAmbisonicsDecodeEffectReset(decode);
        foreach (var e in Encoders) if (e != IntPtr.Zero) Phonon.iplAmbisonicsEncodeEffectReset(e);
        foreach (var br in Branches) br.Reset();
        _a1 = _a2 = _b1 = 0f;
        Phonon.iplAudioBufferFree(Context, ref field);
        Phonon.iplAudioBufferFree(Context, ref ears);
    }

    /// <summary>Noise through one encoder: what comes back in W, against what went in. The first
    /// block is skipped in case the encoder ramps its gains in.</summary>
    private void Calibrate(int sub, int channels, int order)
    {
        var rng = new Random(11);
        double eIn = 0, eW = 0;
        var ep = new Phonon.IPLAmbisonicsEncodeEffectParams { direction = Directions[0], order = order };
        for (int b = 0; b < 4; b++)
        {
            for (int k = 0; k < sub; k++) Branch[k] = (float)(rng.NextDouble() * 2 - 1);
            Phonon.iplAudioBufferDeinterleave(Context, Branch, ref Mono);
            Phonon.iplAmbisonicsEncodeEffectApply(Encoders[0], ref ep, ref Mono, ref Encoded);
            Phonon.iplAudioBufferInterleave(Context, ref Encoded, Field);
            if (b == 0) continue;
            for (int k = 0; k < sub; k++) { eIn += Branch[k] * (double)Branch[k]; eW += Field[k * channels] * (double)Field[k * channels]; }
        }
        Phonon.iplAmbisonicsEncodeEffectReset(Encoders[0]);
        WScale = eW > 0 ? (float)Math.Sqrt(eIn / eW) : 1f;
    }

    /// <summary>The field's W, one block, into the field of eight directions, interleaved into
    /// <see cref="Sum"/>.</summary>
    public void Render(float[] ambiInterleaved, int sub, int channels, int order)
    {
        for (int k = 0; k < sub; k++)
        {
            float x = ambiInterleaved[k * channels];
            _a1 += _lpA * (x - _a1);                 // low, once
            _a2 += _lpA * (_a1 - _a2);               // low, twice
            float h1 = x - _a1;                       // high, once (a one-pole's complement is exact)
            _b1 += _lpA * (h1 - _b1);
            Low[k] = _a2;
            W[k] = h1 - _b1;                          // high, twice
        }
        Array.Clear(Sum, 0, sub * channels);
        UpdateBranchGains(sub);
        for (int b = 0; b < DiffuseBranch.Count; b++)
        {
            var branch = Branches[b];
            float gain = Gain * _branchGain[b];
            for (int k = 0; k < sub; k++) Branch[k] = branch.Process(W[k]) * gain;
            Phonon.iplAudioBufferDeinterleave(Context, Branch, ref Mono);
            var ep = new Phonon.IPLAmbisonicsEncodeEffectParams { direction = Directions[b], order = order };
            Phonon.iplAmbisonicsEncodeEffectApply(Encoders[b], ref ep, ref Mono, ref Encoded);
            Phonon.iplAudioBufferInterleave(Context, ref Encoded, Field);
            for (int i = 0; i < sub * channels; i++) Sum[i] += Field[i];
        }
    }

    public void Release()
    {
        Ready = false;
        for (int i = 0; i < Encoders.Length; i++)
            if (Encoders[i] != IntPtr.Zero) Phonon.iplAmbisonicsEncodeEffectRelease(ref Encoders[i]);
        if (Mono.data != IntPtr.Zero) Phonon.iplAudioBufferFree(Context, ref Mono);
        if (Encoded.data != IntPtr.Zero) Phonon.iplAudioBufferFree(Context, ref Encoded);
    }
}

internal static class TracedReverbDsp
{
    private static readonly FMOD.DSP_READ_CALLBACK _read = Read;

    public static RESULT Create(FMOD.System system, TracedReverbState state, out FMOD.DSP dsp, out GCHandle handle)
    {
        var desc = new FMOD.DSP_DESCRIPTION
        {
            pluginsdkversion = FMOD.VERSION.number,
            numinputbuffers = 1,
            numoutputbuffers = 1,
            read = _read,
        };
        RESULT res = system.createDSP(ref desc, out dsp);
        if (res == RESULT.OK)
        {
            handle = GCHandle.Alloc(state);
            dsp.setUserData(GCHandle.ToIntPtr(handle));
            dsp.setChannelFormat(0, 0, SPEAKERMODE.STEREO);
        }
        else handle = default;
        return res;
    }

    private static RESULT Read(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        // A callback on FMOD's mixer thread must never throw: the process aborts.
        try { return ReadCore(ref dsp_state, inbuffer, outbuffer, length, inchannels, ref outchannels); }
        catch
        {
            unsafe
            {
                int ch = outchannels > 0 ? outchannels : 2;
                if (outbuffer != IntPtr.Zero) new Span<float>((void*)outbuffer, (int)length * ch).Clear();
            }
            return RESULT.OK;
        }
    }

    private static unsafe RESULT ReadCore(ref DSP_STATE dsp_state, IntPtr inbuffer, IntPtr outbuffer, uint length, int inchannels, ref int outchannels)
    {
        if (outchannels == 0) outchannels = 2;
        int outCh = outchannels, n = (int)length;
        float* o = (float*)outbuffer;
        float* i = (float*)inbuffer;

        IntPtr userData = DspCallback.UserData(ref dsp_state);
        TracedReverb? reverb = userData != IntPtr.Zero && GCHandle.FromIntPtr(userData).Target is TracedReverbState own ? own.Trace : null;
        if (userData == IntPtr.Zero || GCHandle.FromIntPtr(userData).Target is not TracedReverbState s
            || reverb == null || n != s.FrameSize || s.Effect == IntPtr.Zero
            || !reverb.TryGetParams(s.Reader, out var prm))
        {
            if (userData != IntPtr.Zero && GCHandle.FromIntPtr(userData).Target is TracedReverbState b) b.Bailed++;
            for (int k = 0; k < n * outCh; k++) o[k] = 0f;
            return RESULT.OK;
        }

        // The bus's sends, down to one channel (the tracer's source is a point at the listener), run
        // through the convolution a SubFrame at a time: it answers one of ITS blocks late, and that
        // block is the room's pre-delay (TracedReverb.TracedFrame).
        var mono = s.MonoScratch;
        var st = s.StereoScratch;
        int sub = s.SubFrame > 0 ? s.SubFrame : n;
        int inCh = Math.Max(1, inchannels);
        float g = s.Gain;
        double inSum = 0, outSum = 0, chSum = 0;
        var dp = new Phonon.IPLAmbisonicsDecodeEffectParams
        {
            order = TracedReverb.Order, hrtf = s.Hrtf, orientation = s.Orientation, binaural = Phonon.IPL_TRUE,
        };
        for (int at = 0; at + sub <= n; at += sub)
        {
            for (int k = 0; k < sub; k++)
            {
                float v = 0f;
                for (int c = 0; c < inCh; c++) { float x = i[(at + k) * inCh + c]; v += x; chSum += x * (double)x; }
                v /= inCh;
                mono[k] = v;
                inSum += v * (double)v;
            }
            Phonon.iplAudioBufferDeinterleave(s.WorkerContext, mono, ref s.Mono);
            bool diffuse = false;
            if (s.TailOnly && !TracedReverbState.ParametricTail && s.LateConv is { } lc)
            {
                // The traced late part itself, convolved: its level, its envelope and its decay are
                // the room's. Silent until the first trace has been read back, a fraction of a second.
                lc.SetIr(reverb.Late);
                if (s.LateOut.Length < sub) { for (int k = 0; k < n * outCh; k++) o[k] = 0f; return RESULT.OK; }
                lc.Process(mono.AsSpan(0, sub), s.LateOut.AsSpan(0, sub));
                Array.Clear(s.AmbiScratch, 0, sub * TracedReverb.Channels);
                for (int k = 0; k < sub; k++) s.AmbiScratch[k * TracedReverb.Channels] = s.LateOut[k];
                if (s.Diffuse is { Ready: true } dfl)
                {
                    dfl.Render(s.AmbiScratch, sub, TracedReverb.Channels, TracedReverb.Order);
                    Phonon.iplAudioBufferDeinterleave(s.WorkerContext, dfl.Sum, ref s.Ambi);
                    diffuse = true;
                }
                else Phonon.iplAudioBufferDeinterleave(s.WorkerContext, s.AmbiScratch, ref s.Ambi);
            }
            else if (s.TailOnly && s.TailEffect != IntPtr.Zero)
            {
                var tail = prm;
                tail.type = Phonon.IPL_REFLECTIONEFFECTTYPE_PARAMETRIC;
                Phonon.iplReflectionEffectApply(s.TailEffect, ref tail, ref s.Mono, ref s.Ambi, IntPtr.Zero);
                // The one channel it wrote, made into a field from every direction: see DiffuseTail.
                if (s.Diffuse is { Ready: true } df && s.AmbiScratch.Length >= sub * TracedReverb.Channels)
                {
                    Phonon.iplAudioBufferInterleave(s.WorkerContext, ref s.Ambi, s.AmbiScratch);
                    df.Render(s.AmbiScratch, sub, TracedReverb.Channels, TracedReverb.Order);
                    Phonon.iplAudioBufferDeinterleave(s.WorkerContext, df.Sum, ref s.Ambi);
                    diffuse = true;
                }
            }
            else Phonon.iplReflectionEffectApply(s.Effect, ref prm, ref s.Mono, ref s.Ambi, IntPtr.Zero);
            Phonon.iplAmbisonicsDecodeEffectApply(s.Decode, ref dp, ref s.Ambi, ref s.Stereo);
            Phonon.iplAudioBufferInterleave(s.ProviderContext, ref s.Stereo, st);
            for (int k = 0; k < sub; k++)
            {
                // A field decoded from eight directions is already two different ears; its low end
                // did not go through the decoder and joins both ears here (DiffuseTail).
                float l = diffuse ? (st[k * 2] + s.Diffuse!.Low[k]) * g : s.Left.Process(st[k * 2]) * g;
                float r = diffuse ? (st[k * 2 + 1] + s.Diffuse!.Low[k]) * g : s.Right.Process(st[k * 2 + 1]) * g;
                outSum += l * (double)l + r * (double)r;
                int ok = (at + k) * outCh;
                o[ok] = l;
                if (outCh > 1) o[ok + 1] = r;
                for (int c = 2; c < outCh; c++) o[ok + c] = 0f;
            }
        }
        s.InEnergy += inSum; s.OutEnergy += outSum / 2; s.ChannelEnergy += chSum / inCh; s.Channels = inCh;
        s.InRms = (float)Math.Sqrt(inSum / n);
        s.OutRms = (float)Math.Sqrt(outSum / (2 * n));
        return RESULT.OK;
    }
}
