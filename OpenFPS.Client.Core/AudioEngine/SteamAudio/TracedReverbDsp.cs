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
/// Since 2026-09-30: twenty directions, not eight (a dodecahedron, tilted off the game's axes), each
/// through a velvet-noise branch and straight through its own head response (RenderBinaural) rather
/// than a second-order soundfield, and each ear made its own above 400 Hz. The notes below on the
/// encode and decode describe the fallback, OPENFPS_TAIL_AMBISONIC=1.
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
    private readonly float[] _branchGain = System.Linq.Enumerable.Repeat(1f, DiffuseBranch.Count).ToArray();
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

    // ── Straight to the ears ─────────────────────────────────────────────────────────────────
    //
    // The eight directions used to be ENCODED into a second-order soundfield and decoded through the
    // HRTF like every other field. At second order that decode cannot make two ears independent at
    // high frequencies: measured, the tail's interaural coherence was 0.60 at 2 kHz and 0.34 at 4 kHz
    // where a head in a diffuse field gets under about 0.15 (Zaunschirm et al. 2018, the order-limited
    // binaural decode). Coherent ears put a sound in the middle of the head, over everything. So each
    // direction goes through its OWN binaural effect, the head-related response of exactly that
    // direction, turned into the head's frame every block; the ears are then eight independent
    // signals through eight different responses, which is what a diffuse field is at a head.
    // OPENFPS_TAIL_AMBISONIC=1 goes back to the encode and decode.
    public static readonly bool Binaural = Environment.GetEnvironmentVariable("OPENFPS_TAIL_AMBISONIC") != "1";
    public readonly IntPtr[] Ears = new IntPtr[DiffuseBranch.Count];
    public Phonon.IPLAudioBuffer EarBuf;
    public IntPtr EarContext, EarHrtf;
    /// <summary>The eight directions' ears, summed, interleaved L/R, one block: the tail above the split.</summary>
    public float[] Stereo = Array.Empty<float>();
    private float[] _earScratch = Array.Empty<float>();
    /// <summary>A trim on the ears; 1, see CalibrateBinaural.</summary>
    public float BinauralTrim = 1f;
    /// <summary>What the head's responses do to a field from everywhere, measured at creation.</summary>
    public float DiffuseFieldGainDb;
    public bool BinauralReady;
    private float _rx, _ry, _rz, _rw = 1f;

    /// <summary>
    /// Above this each ear's share of the tail is made its own (<see cref="_earL"/>, <see cref="_earR"/>).
    ///
    /// Twenty independent directions leave the two ears about 1/sqrt(20) alike at high frequencies:
    /// measured 0.2-0.3 from 1 to 4 kHz, where two hundred directions through the same head response
    /// read 0.03 (--tail-iacc, 2026-09-30) — a real diffuse field is simply different at the two ears
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
    /// The tail's W, one block, through the eight directions' own head responses into
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

    /// <summary>OPENFPS_DIFFUSE_TAIL=0 goes back to the one channel through the ear decorrelators.</summary>
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("OPENFPS_DIFFUSE_TAIL") != "0";

    /// <summary>
    /// The directions the tail arrives from: the twenty vertices of a regular dodecahedron round the
    /// head, in the game's world — as even a spread over the sphere as twenty points get. It was the
    /// eight corners of a cube, and N independent directions summed at two ears leave a coherence of
    /// about 1/sqrt(N) at high frequencies: eight measured 0.4-0.6 above 1.2 kHz where a head in a real
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

    /// <param name="decode">The stage's decoder, with the HRTF it will use, for the level calibration;
    /// it is reset afterwards. Zero skips that calibration.</param>
    /// <param name="orientation">The listener frame the calibration decodes in.</param>
    public DiffuseTail() { (_earL, _earR) = DiffuseBranch.EarPair(101); }

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
        if (Binaural && hrtf != IntPtr.Zero && d.CreateEars(context, subFrame, hrtf))
        {
            d.CalibrateBinaural(subFrame, channels);
            d.BinauralReady = true;
        }
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
        for (int i = 0; i < Ears.Length; i++)
            if (Ears[i] != IntPtr.Zero) Phonon.iplBinauralEffectRelease(ref Ears[i]);
        if (EarBuf.data != IntPtr.Zero) Phonon.iplAudioBufferFree(EarContext, ref EarBuf);
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
                if (s.Diffuse is { Ready: true, BinauralReady: true } dfb)
                {
                    // Straight to the ears (DiffuseTail.RenderBinaural): no soundfield, no decode.
                    dfb.RenderBinaural(s.AmbiScratch, sub, TracedReverb.Channels);
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
