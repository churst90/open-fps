using System;
using System.Runtime.InteropServices;
using System.Threading;
using FMOD;
using OpenFPS.Client.AudioEngine.Fmod;   // DspCallback.UserData

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// One outdoor reverb bus's traced stage: what the bus's sends carry, played through the place's
/// measured impulse response (TracedReverb) and rendered round the listener's head. It sits just
/// after the bus's SFXREVERB, which in traced mode passes its input through dry; in room mode this
/// stage is bypassed and the SFXREVERB is the tail.
/// </summary>
internal sealed class TracedReverbState
{
    public int FrameSize;
    /// <summary>The traced response's own block (TracedReverb.TracedFrame): the mixer's block is run
    /// through the convolution in pieces of this size.</summary>
    public int SubFrame;
    public IntPtr WorkerContext;          // the tracer's context: the effect must be of the IR's context
    public IntPtr Effect;                 // IPLReflectionEffect (convolution: the whole traced response)
    /// <summary>Play the tail only: the room the listener is in, where the early part is placed
    /// reflections (WorldAudioPlayer.QueueRoomEchoes). Game thread writes.</summary>
    public volatile bool TailOnly;
    /// <summary>The tail: the trace's own late part (LateTailIr), convolved here.</summary>
    public LateTailConvolver? LateConv;
    /// <summary>The directional part's twenty responses against the send (SdmTailIr).</summary>
    public SharedInputConvolver? SdmConv;
    /// <summary>The late part as a field, one independent noise per direction (DiffuseLate). Made by
    /// the game thread when this stage first plays the room you are in; null until then.</summary>
    public volatile DiffuseLateConvolver? DiffuseLateConv;
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
    /// <summary>What the bus's input waits so the room lines up with the voices it answers
    /// (TracedReverbDsp.StagePreDelay). Null: none.</summary>
    public PreDelay? Delay;

    /// <summary>One per ear: the tail the trace hands back is the same in both, and a room is not.</summary>
    public readonly EarDecorrelator Left = new(0), Right = new(1);

    // Diagnostics: what goes in and what comes out, for the /reverb line.
    public volatile float InRms, OutRms;
    /// <summary>Running totals of the energy in (mono) and out (per ear), for the lab.</summary>
    public double InEnergy, OutEnergy, ChannelEnergy;
    public int Channels;
    public volatile int Bailed;

    /// <summary>The listener's own room's late tail as a field round the head; null (Steam Audio
    /// would not make its ears) renders it through the ear decorrelators. See <see cref="DiffuseTail"/>.</summary>
    public DiffuseTail? Diffuse;
    /// <summary>The whole soundfield, interleaved, one block of <see cref="SubFrame"/>.</summary>
    public float[] AmbiScratch = Array.Empty<float>();
}

/// <summary>
/// The late tail of the room you are in, rendered as the diffuse field it is.
///
/// The traced late part is one channel: W, the omnidirectional one. The same signal in both ears is
/// heard inside the head, or straight ahead, and it stays there when the head turns. Splitting it
/// into two ears with different all-pass chains (EarDecorrelator) lowers the correlation but not the
/// place. A real late tail is energy arriving from every direction at once, each direction's share a
/// different signal with the same statistics.
///
/// So the tail goes through twenty velvet-noise branches (DiffuseBranch), one per direction of a
/// dodecahedron tilted off the game's axes, and each branch goes straight through its own head
/// response for that direction (RenderBinaural), turned into the head's frame every block. Each ear
/// then hears twenty independent signals through different responses, which is where a diffuse
/// field's interaural correlation comes from in life, and when the head turns the tail's fine
/// structure turns with it while its level and colour do not. Above <see cref="EarSplitHz"/> each
/// ear's share is made its own as well.
///
/// The low end, below <see cref="SplitHz"/>, goes to both ears as it is: that is what a diffuse
/// field is at those frequencies on a head (interaural correlation near 1, level equal to the
/// field's).
///
/// The directional part of the tail (SdmTailIr) is added from the walls it came off
/// (<see cref="AddDirectional"/>).
///
/// Since 2026-10-03 the late part normally arrives already as twenty independent signals
/// (DiffuseLate) and goes through <see cref="RenderLate"/>: no velvet, no splits. The velvet way
/// above is kept for a trace without directions and for the lab's A/B.
/// </summary>
internal sealed class DiffuseTail
{
    public readonly DiffuseBranch[] Branches = new DiffuseBranch[DiffuseBranch.Count];
    public float[] W = Array.Empty<float>(), Branch = Array.Empty<float>();
    /// <summary>The tail below the split, one block: added to both ears as it is.</summary>
    public float[] Low = Array.Empty<float>();
    public Phonon.IPLAudioBuffer Mono;
    public IntPtr Context;

    /// <summary>
    /// Which way the late energy leans, game world, length its |I|/E (0 from everywhere, 1 from one
    /// way): set from each source's own trace (LateField). A room's is near zero and the twenty
    /// directions stay equal; in a tunnel or a street it points back toward the cars, and each
    /// direction's share becomes max(0, 1 + 3 cos), renormalised so the tail's energy is unchanged.
    /// Game thread writes; read once a block.
    /// </summary>
    public void SetBias(System.Numerics.Vector3 bias)
    {
        Volatile.Write(ref _biasX, bias.X); Volatile.Write(ref _biasY, bias.Y); Volatile.Write(ref _biasZ, bias.Z);
    }
    private float _biasX, _biasY, _biasZ;
    private readonly float[] _branchGain = System.Linq.Enumerable.Repeat(1f, DiffuseBranch.Count).ToArray();
    private readonly float[] _branchTarget = new float[DiffuseBranch.Count];

    /// <summary>
    /// Where the trace's own remainder arrives from, direction by direction (SdmTailIr.LateShare):
    /// the diffuse part is weighted by it before the sources' lean, so a corridor's late sound runs
    /// along it and a room's stays even. Null: even. Read once a block.
    /// </summary>
    public volatile float[]? LateShares;

    private void UpdateBranchGains(int sub)
    {
        var bias = new System.Numerics.Vector3(Volatile.Read(ref _biasX), Volatile.Read(ref _biasY), Volatile.Read(ref _biasZ));
        var shares = LateShares;
        float sum = 0f;
        for (int b = 0; b < DiffuseBranch.Count; b++)
        {
            float place = shares != null && shares.Length == DiffuseBranch.Count ? shares[b] * DiffuseBranch.Count : 1f;
            float w = place * MathF.Max(0f, 1f + 3f * System.Numerics.Vector3.Dot(bias, Direction(b)));
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
    /// branches stop being distinct signals (their all-passes are 89-431 samples, so about
    /// 100 Hz), not at the ear decorrelator's 300: a step on carpet is nearly all below 300 Hz, and
    /// a tail that is the same in both ears there sits in the head however diffuse the rest is.</summary>
    public const float SplitHz = 120f;
    private readonly float _lpA = 1f - MathF.Exp(-2f * MathF.PI * SplitHz / 44100f);
    private float _a1, _a2, _b1;

    // ── Straight to the ears ─────────────────────────────────────────────────────────────────
    //
    // Not encoded into a second-order soundfield and decoded through the HRTF like every other
    // field. At second order that decode cannot make two ears independent at high frequencies:
    // measured, the tail's interaural coherence was 0.60 at 2 kHz and 0.34 at 4 kHz where a head in
    // a diffuse field gets under about 0.15 (Zaunschirm et al. 2018, the order-limited binaural
    // decode). Coherent ears put a sound in the middle of the head, over everything. So each
    // direction goes through its own binaural effect, the head-related response of exactly that
    // direction, turned into the head's frame every block.
    public readonly IntPtr[] Ears = new IntPtr[DiffuseBranch.Count];
    public Phonon.IPLAudioBuffer EarBuf;
    public IntPtr EarContext, EarHrtf;
    /// <summary>The directions' ears, summed, interleaved L/R, one block: the tail above the split.</summary>
    public float[] Stereo = Array.Empty<float>();
    private float[] _earScratch = Array.Empty<float>();
    /// <summary>A trim on the ears; 1, see CalibrateBinaural.</summary>
    public float BinauralTrim = 1f;
    /// <summary>What the head's responses do to a field from everywhere, measured at creation.</summary>
    public float DiffuseFieldGainDb;
    private float _rx, _ry, _rz, _rw = 1f;

    /// <summary>
    /// Above this each ear's share of the tail is made its own (<see cref="_earL"/>, <see cref="_earR"/>).
    ///
    /// Twenty independent directions leave the two ears about 1/sqrt(20) alike at high frequencies:
    /// measured 0.2-0.3 from 1 to 4 kHz, where two hundred directions through the same head response
    /// read 0.03 (--tail-iacc) — a real diffuse field is simply different at the two ears
    /// up there. Two hundred head responses a block is too dear, so above the frequency where this
    /// head's diffuse field stops being alike at both ears (0.70 at 250 Hz, 0.10 at 500) each ear's
    /// half goes through a velvet filter of its own: independent fine structure, the same energy, so
    /// the level difference that carries the tail's lean survives. Below it the twenty directions keep
    /// the coherence they have, which is already the head's.
    /// </summary>
    public const float EarSplitHz = 400f;
    // A fourth-order Linkwitz-Riley split (two Butterworth sections each side): steep, so the
    // decorrelation above does not leak into the band below, where the ears should stay alike.
    private readonly Biquad _loL1 = Biquad.LowPass(EarSplitHz), _loL2 = Biquad.LowPass(EarSplitHz);
    private readonly Biquad _hiL1 = Biquad.HighPass(EarSplitHz), _hiL2 = Biquad.HighPass(EarSplitHz);
    private readonly Biquad _loR1 = Biquad.LowPass(EarSplitHz), _loR2 = Biquad.LowPass(EarSplitHz);
    private readonly Biquad _hiR1 = Biquad.HighPass(EarSplitHz), _hiR2 = Biquad.HighPass(EarSplitHz);
    private readonly DiffuseBranch _earL, _earR;

    /// <summary>A Butterworth biquad section, direct form I, allocation-free.</summary>
    private sealed class Biquad
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;
        private Biquad(float b0, float b1, float b2, float a1, float a2) { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }
        private static (double c, double al) W(float hz) { double w = 2 * Math.PI * hz / 44100.0; return (Math.Cos(w), Math.Sin(w) / (2 * Math.Sqrt(0.5))); }
        public static Biquad LowPass(float hz) { var (c, al) = W(hz); double a0 = 1 + al; return new((float)((1 - c) / 2 / a0), (float)((1 - c) / a0), (float)((1 - c) / 2 / a0), (float)(-2 * c / a0), (float)((1 - al) / a0)); }
        public static Biquad HighPass(float hz) { var (c, al) = W(hz); double a0 = 1 + al; return new((float)((1 + c) / 2 / a0), (float)(-(1 + c) / a0), (float)((1 + c) / 2 / a0), (float)(-2 * c / a0), (float)((1 - al) / a0)); }
        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
        public void Reset() { _x1 = _x2 = _y1 = _y2 = 0f; }
    }

    /// <summary>The listener's rotation, game world. Game thread writes; read once a block.</summary>
    public void SetListenerRotation(System.Numerics.Quaternion q)
    {
        Volatile.Write(ref _rx, q.X); Volatile.Write(ref _ry, q.Y); Volatile.Write(ref _rz, q.Z); Volatile.Write(ref _rw, q.W);
    }

    private bool CreateEars(IntPtr context, int sub, IntPtr hrtf)
    {
        EarContext = context; EarHrtf = hrtf;
        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = sub };
        var bs = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        for (int i = 0; i < DiffuseBranch.Count; i++)
            if (Phonon.iplBinauralEffectCreate(context, ref au, ref bs, out Ears[i]) != Phonon.IPL_STATUS_SUCCESS) return false;
        Phonon.iplAudioBufferAllocate(context, 2, sub, ref EarBuf);
        Stereo = new float[sub * 2]; _earScratch = new float[sub * 2];
        return true;
    }

    /// <summary>
    /// The tail's W, one block, through the directions' own head responses into
    /// <see cref="Stereo"/>; the part below the split into <see cref="Low"/> as before.
    /// </summary>
    public void RenderBinaural(float[] ambiInterleaved, int sub, int channels)
    {
        for (int k = 0; k < sub; k++)
        {
            float x = ambiInterleaved[k * channels];
            _a1 += _lpA * (x - _a1);
            _a2 += _lpA * (_a1 - _a2);
            float h1 = x - _a1;
            _b1 += _lpA * (h1 - _b1);
            Low[k] = _a2;
            W[k] = h1 - _b1;
        }
        Array.Clear(Stereo, 0, sub * 2);
        UpdateBranchGains(sub);
        var rot = new System.Numerics.Quaternion(Volatile.Read(ref _rx), Volatile.Read(ref _ry), Volatile.Read(ref _rz), Volatile.Read(ref _rw));
        var toHead = System.Numerics.Quaternion.Conjugate(rot);
        float baseGain = BinauralTrim / MathF.Sqrt(DiffuseBranch.Count);
        for (int b = 0; b < DiffuseBranch.Count; b++)
        {
            var branch = Branches[b];
            float gain = baseGain * _branchGain[b];
            for (int k = 0; k < sub; k++) Branch[k] = branch.Process(W[k]) * gain;
            Phonon.iplAudioBufferDeinterleave(EarContext, Branch, ref Mono);
            // The branch's world direction in the head's frame, as a voice's is (Steam Audio: -z ahead).
            var local = System.Numerics.Vector3.Transform(Direction(b), toHead);
            var ep = new Phonon.IPLBinauralEffectParams
            {
                direction = new Phonon.IPLVector3 { x = local.X, y = local.Y, z = -local.Z },
                interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = EarHrtf,
            };
            Phonon.iplBinauralEffectApply(Ears[b], ref ep, ref Mono, ref EarBuf);
            Phonon.iplAudioBufferInterleave(EarContext, ref EarBuf, _earScratch);
            for (int i = 0; i < sub * 2; i++) Stereo[i] += _earScratch[i];
        }
        // Each ear its own above the split: low (two one-poles) plus the exact remainder, decorrelated.
        for (int k = 0; k < sub; k++)
        {
            float l = Stereo[k * 2], r = Stereo[k * 2 + 1];
            float lowL = _loL2.Process(_loL1.Process(l)), highL = _hiL2.Process(_hiL1.Process(l));
            float lowR = _loR2.Process(_loR1.Process(r)), highR = _hiR2.Process(_hiR1.Process(r));
            Stereo[k * 2] = lowL + _earL.Process(highL);
            Stereo[k * 2 + 1] = lowR + _earR.Process(highR);
        }
    }

    // ── The late part as a field (DiffuseLate) ─────────────────────────────────────────────────
    //
    // Each direction's own independent late signal (DiffuseLateConvolver), so no velvet branch and
    // no ear velvet: three random spectra multiplied were the ring. Twenty independent directions
    // leave the ears as unlike as the velvet did (--tail-iacc, noise, four headings: 0.11 0.07 0.17
    // 0.13 from 500 Hz to 4 kHz, against 0.25 0.16 0.16 0.12), so nothing more is done to them; an
    // all-pass chain per ear (24 sections from 700 Hz) was tried and changed nothing measurable.
    // And no split at SplitHz either:
    // the directions are distinct signals all the way down, so the head alone makes the low end
    // alike at the two ears, as alike as a head in a diffuse field hears it (0.91 at 125 Hz with this
    // HRTF, --tail-iacc), not identical. Split as the one-channel tail is, the low part (two one-pole
    // low-passes) and the rest (two one-pole high-passes) of the SAME signal met in phase opposition
    // at 120 Hz: a notch, 1.3-2.5 dB out of the 125 Hz octave and its decay shortened.

    /// <summary>One block per direction: the late field's signals, filled by DiffuseLateConvolver.</summary>
    public float[][] LateIn = Array.Empty<float[]>();
    /// <summary>The lab's A/B: the ear velvet on the field as well. Never set in the game.</summary>
    public static bool LateEarVelvet;

    /// <summary>
    /// The late field (<see cref="LateIn"/>) through the directions' own head responses into
    /// <see cref="Stereo"/>, each weighted as the branches are (where the late energy comes from,
    /// and the sources' lean). <see cref="Low"/> is left silent: the low end is in the head responses.
    /// </summary>
    public void RenderLate(int sub)
    {
        Array.Clear(Stereo, 0, sub * 2);
        Array.Clear(Low, 0, sub);
        UpdateBranchGains(sub);
        var rot = new System.Numerics.Quaternion(Volatile.Read(ref _rx), Volatile.Read(ref _ry), Volatile.Read(ref _rz), Volatile.Read(ref _rw));
        var toHead = System.Numerics.Quaternion.Conjugate(rot);
        float baseGain = BinauralTrim / MathF.Sqrt(DiffuseBranch.Count);
        for (int b = 0; b < DiffuseBranch.Count; b++)
        {
            var src = LateIn[b];
            float gain = baseGain * _branchGain[b];
            for (int k = 0; k < sub; k++) Branch[k] = src[k] * gain;
            Phonon.iplAudioBufferDeinterleave(EarContext, Branch, ref Mono);
            var local = System.Numerics.Vector3.Transform(Direction(b), toHead);
            var ep = new Phonon.IPLBinauralEffectParams
            {
                direction = new Phonon.IPLVector3 { x = local.X, y = local.Y, z = -local.Z },
                interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = EarHrtf,
            };
            Phonon.iplBinauralEffectApply(Ears[b], ref ep, ref Mono, ref EarBuf);
            Phonon.iplAudioBufferInterleave(EarContext, ref EarBuf, _earScratch);
            for (int i = 0; i < sub * 2; i++) Stereo[i] += _earScratch[i];
        }
        if (!LateEarVelvet) return;
        for (int k = 0; k < sub; k++)
        {
            float l = Stereo[k * 2], r = Stereo[k * 2 + 1];
            float lowL = _loL2.Process(_loL1.Process(l)), highL = _hiL2.Process(_hiL1.Process(l));
            float lowR = _loR2.Process(_loR1.Process(r)), highR = _hiR2.Process(_hiR1.Process(r));
            Stereo[k * 2] = lowL + _earL.Process(highL);
            Stereo[k * 2 + 1] = lowR + _earR.Process(highR);
        }
    }

    /// <summary>Noise through the whole binaural path, each ear's energy against the noise's: the trim
    /// that makes the tail at the ears as strong as the tail that went in.</summary>
    private void CalibrateBinaural(int sub, int channels)
    {
        var inter = new float[sub * channels];
        var rng = new Random(17);
        double eIn = 0, eEar = 0;
        int blocks = Math.Max(12, 44100 / sub);
        for (int b = 0; b < blocks; b++)
        {
            Array.Clear(inter);
            for (int k = 0; k < sub; k++) inter[k * channels] = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
            RenderBinaural(inter, sub, channels);
            if (b < 4) continue;
            for (int k = 0; k < sub; k++)
            {
                eIn += inter[k * channels] * (double)inter[k * channels];
                double l = Stereo[k * 2] + Low[k], r = Stereo[k * 2 + 1] + Low[k];
                eEar += (l * l + r * r) / 2;
            }
        }
        // Measured, and NOT applied. It is the head's diffuse-field gain — what the pinna does to sound
        // from everywhere, a few decibels above 2 kHz — and the direct sounds keep theirs, so the tail
        // keeps its: only the 1/sqrt(N) split between directions. Trimming it away (white noise, so the
        // top end ruled) put the tail 2.5 dB under the voices it belongs to.
        if (eEar > 0 && eIn > 0) DiffuseFieldGainDb = (float)(10 * Math.Log10(eEar / eIn));
        BinauralTrim = 1f;
        foreach (var e in Ears) if (e != IntPtr.Zero) Phonon.iplBinauralEffectReset(e);
        foreach (var br in Branches) br.Reset();
        _earL.Reset(); _earR.Reset();
        foreach (var bq in new[] { _loL1, _loL2, _hiL1, _hiL2, _loR1, _loR2, _hiR1, _hiR2 }) bq.Reset();
        _a1 = _a2 = _b1 = 0f;
    }

    // ── The directional part (SdmTailIr) ──────────────────────────────────────────────────────
    //
    // Each direction's own response, through that direction's own head response, turned with the head
    // every block — and NOT through the ear decorrelation, which would scramble the very timing
    // between the ears that says where it comes from. Its own twenty binaural effects: a binaural
    // effect carries state, and the diffuse branches use theirs.
    public readonly IntPtr[] SdmEars = new IntPtr[DiffuseBranch.Count];
    public float[][] SdmOut = Array.Empty<float[]>();
    public bool SdmReady;

    private bool CreateSdmEars(int sub)
    {
        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = sub };
        var bs = new Phonon.IPLBinauralEffectSettings { hrtf = EarHrtf };
        for (int i = 0; i < DiffuseBranch.Count; i++)
            if (Phonon.iplBinauralEffectCreate(EarContext, ref au, ref bs, out SdmEars[i]) != Phonon.IPL_STATUS_SUCCESS) return false;
        SdmOut = new float[DiffuseBranch.Count][];
        for (int i = 0; i < SdmOut.Length; i++) SdmOut[i] = new float[sub];
        return true;
    }

    /// <summary>Adds the directional part (<see cref="SdmOut"/>, one block per direction) into
    /// <see cref="Stereo"/>, each from its own direction in the head's frame.</summary>
    public void AddDirectional(int sub)
    {
        var rot = new System.Numerics.Quaternion(Volatile.Read(ref _rx), Volatile.Read(ref _ry), Volatile.Read(ref _rz), Volatile.Read(ref _rw));
        var toHead = System.Numerics.Quaternion.Conjugate(rot);
        for (int b = 0; b < DiffuseBranch.Count; b++)
        {
            var src = SdmOut[b];
            bool any = false;
            for (int k = 0; k < sub && !any; k++) any = src[k] != 0f;
            if (!any) continue;
            Phonon.iplAudioBufferDeinterleave(EarContext, src, ref Mono);
            var local = System.Numerics.Vector3.Transform(Direction(b), toHead);
            var ep = new Phonon.IPLBinauralEffectParams
            {
                direction = new Phonon.IPLVector3 { x = local.X, y = local.Y, z = -local.Z },
                interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = EarHrtf,
            };
            Phonon.iplBinauralEffectApply(SdmEars[b], ref ep, ref Mono, ref EarBuf);
            Phonon.iplAudioBufferInterleave(EarContext, ref EarBuf, _earScratch);
            for (int i = 0; i < sub * 2; i++) Stereo[i] += _earScratch[i];
        }
    }

    /// <summary>
    /// The directions the tail arrives from: the twenty vertices of a regular dodecahedron round the
    /// head, in the game's world — as even a spread over the sphere as twenty points get. Not fewer:
    /// N independent directions summed at two ears leave a coherence of about 1/sqrt(N) at high
    /// frequencies, and eight (a cube's corners) measured 0.4-0.6 above 1.2 kHz where a head in a real
    /// diffuse field is far lower.
    /// </summary>
    public static System.Numerics.Vector3 Direction(int i) => Dodecahedron[i % Dodecahedron.Length];

    private static readonly System.Numerics.Vector3[] Dodecahedron = MakeDodecahedron();

    private static System.Numerics.Vector3[] MakeDodecahedron()
    {
        float phi = (1f + MathF.Sqrt(5f)) / 2f, inv = 1f / phi;
        var v = new System.Collections.Generic.List<System.Numerics.Vector3>();
        for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2) v.Add(new(x, y, z));
        for (int a = -1; a <= 1; a += 2) for (int b = -1; b <= 1; b += 2)
        {
            v.Add(new(0, a * inv, b * phi));
            v.Add(new(a * inv, b * phi, 0));
            v.Add(new(a * phi, 0, b * inv));
        }
        // Tilted off every axis the game lines a head up with. As generated, four vertices lie exactly in
        // the plane between the ears when you face north, and your turns snap to 45-degree steps, so the
        // head sat on that alignment often: sound from the median plane is the same at both ears, and a
        // fifth of the tail arriving from it held the ears' coherence up. 17 degrees about the vertical
        // and 11 about east-west line nothing up with anything.
        var tilt = System.Numerics.Quaternion.CreateFromYawPitchRoll(17f * MathF.PI / 180f, 11f * MathF.PI / 180f, 0f);
        for (int i = 0; i < v.Count; i++) v[i] = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Transform(v[i], tilt));
        return v.ToArray();
    }

    public DiffuseTail() { (_earL, _earR) = DiffuseBranch.EarPair(101); }

    /// <summary>The tail's renderer, or null when Steam Audio will not make its ears.</summary>
    public static DiffuseTail? Create(IntPtr context, int subFrame, int channels, IntPtr hrtf)
    {
        if (hrtf == IntPtr.Zero) return null;
        var d = new DiffuseTail { Context = context };
        for (int i = 0; i < DiffuseBranch.Count; i++) d.Branches[i] = new DiffuseBranch(i);
        d.W = new float[subFrame]; d.Branch = new float[subFrame]; d.Low = new float[subFrame];
        d.LateIn = new float[DiffuseBranch.Count][];
        for (int i = 0; i < DiffuseBranch.Count; i++) d.LateIn[i] = new float[subFrame];
        Phonon.iplAudioBufferAllocate(context, 1, subFrame, ref d.Mono);
        if (!d.CreateEars(context, subFrame, hrtf)) { d.Release(); return null; }
        d.CalibrateBinaural(subFrame, channels);
        d.SdmReady = d.CreateSdmEars(subFrame);
        return d;
    }

    public void Release()
    {
        for (int i = 0; i < Ears.Length; i++)
            if (Ears[i] != IntPtr.Zero) Phonon.iplBinauralEffectRelease(ref Ears[i]);
        for (int i = 0; i < SdmEars.Length; i++)
            if (SdmEars[i] != IntPtr.Zero) Phonon.iplBinauralEffectRelease(ref SdmEars[i]);
        if (EarBuf.data != IntPtr.Zero) Phonon.iplAudioBufferFree(EarContext, ref EarBuf);
        if (Mono.data != IntPtr.Zero) Phonon.iplAudioBufferFree(Context, ref Mono);
    }
}

/// <summary>A fixed delay, a sample at a time. Allocation-free after construction; never throws.</summary>
internal sealed class PreDelay
{
    private readonly float[] _line;
    private int _w;
    public readonly int Samples;

    public PreDelay(int samples) { Samples = Math.Max(0, samples); _line = new float[Samples + 1]; }

    public float Process(float x)
    {
        if (Samples == 0) return x;
        _line[_w] = x;
        _w = _w + 1 == _line.Length ? 0 : _w + 1;
        return _line[_w];       // written Samples samples ago
    }
}

internal static class TracedReverbDsp
{
    private static readonly FMOD.DSP_READ_CALLBACK _read = Read;

    // ── Lining the room up with the voices ─────────────────────────────────────────────────────
    //
    // Steam Audio's binaural effect delays what it renders by an amount that depends on its frame:
    // an impulse straight ahead comes out 289 samples later at the voices' 1,024 and 97 at the traced
    // stage's 256 (--early-tail hrtf; the same within 15 samples for every direction tried). A voice
    // and its send leave the channel together (the send tap is the first thing the signal meets), so
    // the room came out 192 samples, 4.4 ms, BEFORE the sound it answers: measured with a click 30 ms
    // into the traced response, it landed 25.5 ms after the dry click (--clap-room probe=30). While
    // the response started at 50 ms nobody could hear that. Started at the first reflection, a room
    // whose nearest surface answers in 6 ms would have answered in 1.5. So the stage's input waits the
    // difference.

    /// <summary>Where an impulse straight ahead first comes out of a binaural effect of this HRTF and
    /// frame, samples: the first sample at a tenth of the peak. -1 if Steam Audio will not make one.</summary>
    public static int BinauralOnset(IntPtr context, IntPtr hrtf, int frame)
    {
        if (context == IntPtr.Zero || hrtf == IntPtr.Zero || frame <= 0) return -1;
        var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = frame };
        var bs = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        if (Phonon.iplBinauralEffectCreate(context, ref au, ref bs, out IntPtr fx) != Phonon.IPL_STATUS_SUCCESS) return -1;
        var inB = new Phonon.IPLAudioBuffer(); var outB = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(context, 1, frame, ref inB);
        Phonon.iplAudioBufferAllocate(context, 2, frame, ref outB);
        var mono = new float[frame]; var st = new float[2 * frame];
        var y = new float[3 * frame];
        for (int b = 0; b < 4; b++)
        {
            Array.Clear(mono);
            if (b == 1) mono[0] = 1f;               // the first block warms the effect
            Phonon.iplAudioBufferDeinterleave(context, mono, ref inB);
            var p = new Phonon.IPLBinauralEffectParams
            {
                direction = new Phonon.IPLVector3 { x = 0, y = 0, z = -1 }, interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR,
                spatialBlend = 1f, hrtf = hrtf,
            };
            Phonon.iplBinauralEffectApply(fx, ref p, ref inB, ref outB);
            Phonon.iplAudioBufferInterleave(context, ref outB, st);
            if (b == 0) continue;
            for (int k = 0; k < frame; k++) y[(b - 1) * frame + k] = MathF.Abs(st[2 * k]) + MathF.Abs(st[2 * k + 1]);
        }
        Phonon.iplBinauralEffectRelease(ref fx);
        Phonon.iplAudioBufferFree(context, ref inB); Phonon.iplAudioBufferFree(context, ref outB);
        float pk = 0f; foreach (float v in y) pk = MathF.Max(pk, v);
        if (pk <= 0f) return -1;
        for (int i = 0; i < y.Length; i++) if (y[i] >= 0.1f * pk) return i;
        return -1;
    }

    /// <summary>How long the traced stage's input waits, samples: what the voices' binaural rendering
    /// delays a sound by, less what the stage's own does. Never negative; zero if either is unknown.</summary>
    public static int StagePreDelay(int voiceOnset, int stageOnset)
        => LabNoPreDelay || voiceOnset < 0 || stageOnset < 0 ? 0 : Math.Max(0, voiceOnset - stageOnset);

    /// <summary>The lab's A/B: no wait, as before 2026-10-03. Never set in the game.</summary>
    public static bool LabNoPreDelay;

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
                if (s.Delay != null) v = s.Delay.Process(v);
                mono[k] = v;
                inSum += v * (double)v;
            }
            Phonon.iplAudioBufferDeinterleave(s.WorkerContext, mono, ref s.Mono);
            if (s.TailOnly && s.Diffuse is { } dff && s.DiffuseLateConv is { } lfc && reverb.DiffuseLate is { } field)
            {
                // The late part as a field: each direction its own noise (DiffuseLate), straight to
                // the ears through its own head response, and the directional part as below.
                lfc.Set(field);
                lfc.Process(mono.AsSpan(0, sub), dff.LateIn);
                dff.LateShares = reverb.LateSdm?.LateShare;
                dff.RenderLate(sub);
                if (dff.SdmReady && s.SdmConv is { } sc2)
                {
                    sc2.Set(reverb.LateSdm);
                    sc2.Process(mono.AsSpan(0, sub), dff.SdmOut);
                    dff.AddDirectional(sub);
                }
                for (int k = 0; k < sub; k++)
                {
                    float l = (dff.Stereo[k * 2] + dff.Low[k]) * g, r = (dff.Stereo[k * 2 + 1] + dff.Low[k]) * g;
                    outSum += l * (double)l + r * (double)r;
                    int ok = (at + k) * outCh;
                    o[ok] = l;
                    if (outCh > 1) o[ok + 1] = r;
                    for (int c = 2; c < outCh; c++) o[ok + c] = 0f;
                }
                continue;
            }
            if (s.TailOnly && s.LateConv is { } lc)
            {
                // The traced late part itself, convolved: its level, its envelope and its decay are
                // the room's. Silent until the first trace has been read back, a fraction of a second.
                lc.SetIr(reverb.Late);
                if (s.LateOut.Length < sub) { for (int k = 0; k < n * outCh; k++) o[k] = 0f; return RESULT.OK; }
                lc.Process(mono.AsSpan(0, sub), s.LateOut.AsSpan(0, sub));
                Array.Clear(s.AmbiScratch, 0, sub * TracedReverb.Channels);
                for (int k = 0; k < sub; k++) s.AmbiScratch[k * TracedReverb.Channels] = s.LateOut[k];
                if (s.Diffuse is { } dfb)
                {
                    // Straight to the ears (DiffuseTail.RenderBinaural): no soundfield, no decode.
                    dfb.RenderBinaural(s.AmbiScratch, sub, TracedReverb.Channels);
                    // And the directional part from the walls it came off (SdmTailIr).
                    if (dfb.SdmReady && s.SdmConv is { } sc)
                    {
                        dfb.LateShares = reverb.LateSdm?.LateShare;
                        sc.Set(reverb.LateSdm);
                        sc.Process(mono.AsSpan(0, sub), dfb.SdmOut);
                        dfb.AddDirectional(sub);
                    }
                    for (int k = 0; k < sub; k++)
                    {
                        float l = (dfb.Stereo[k * 2] + dfb.Low[k]) * g, r = (dfb.Stereo[k * 2 + 1] + dfb.Low[k]) * g;
                        outSum += l * (double)l + r * (double)r;
                        int ok = (at + k) * outCh;
                        o[ok] = l;
                        if (outCh > 1) o[ok + 1] = r;
                        for (int c = 2; c < outCh; c++) o[ok + c] = 0f;
                    }
                    continue;
                }
                Phonon.iplAudioBufferDeinterleave(s.WorkerContext, s.AmbiScratch, ref s.Ambi);
            }
            else Phonon.iplReflectionEffectApply(s.Effect, ref prm, ref s.Mono, ref s.Ambi, IntPtr.Zero);
            Phonon.iplAmbisonicsDecodeEffectApply(s.Decode, ref dp, ref s.Ambi, ref s.Stereo);
            Phonon.iplAudioBufferInterleave(s.ProviderContext, ref s.Stereo, st);
            for (int k = 0; k < sub; k++)
            {
                float l = s.Left.Process(st[k * 2]) * g;
                float r = s.Right.Process(st[k * 2 + 1]) * g;
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
