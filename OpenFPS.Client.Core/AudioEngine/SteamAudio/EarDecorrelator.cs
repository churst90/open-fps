using System;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// One direction's share of a diffuse field: the late tail through a sparse random filter of its own
/// (velvet noise), so that the branches fed the same tail are different signals with the same
/// spectrum and envelope, as the field arriving from each direction of a real room is.
///
/// It was a chain of three short all-passes, and the chains' first delays were neighbouring primes —
/// 79, 83, 89 samples. Mostly each branch was the same input a few samples later, so the branches
/// correlated with each other at small lags, and a head measures its two ears' correlation over lags
/// of up to a millisecond: the tail's interaural coherence stayed 0.35-0.55 above 1.2 kHz with eight
/// directions and with twenty (2026-09-30), where a head in a real diffuse field gets far less.
///
/// Velvet noise is independent in fine structure by construction: thirty-two taps at random places
/// across 30 ms, random signs, a gently falling weight, energy one. Two branches share nothing but
/// chance, so they are decorrelated at every lag and every frequency the tail has. The 30 ms smear is
/// nothing on a tail that begins 50 ms after the sound and is already noise.
/// </summary>
internal sealed class DiffuseBranch
{
    public const int Count = 20;
    private readonly int Taps;
    private readonly int[] _pos;
    private readonly float[] _gain;
    private readonly float[] _line;
    private int _at;

    /// <param name="taps">How many taps; <paramref name="span"/> the samples they spread over (30 ms,
    /// 32 taps for a direction's share). More taps closer together leave two filters fed alike signals
    /// less alike: the ear decorrelation uses 128 over 40 ms.</param>
    public DiffuseBranch(int index, int taps = 32, int span = 1323)
    {
        Taps = taps;
        _pos = new int[taps]; _gain = new float[taps]; _line = new float[span + 1];
        int Span = span;
        var rng = new Random(7919 * (index + 1) + 13);
        double energy = 0;
        for (int k = 0; k < Taps; k++)
        {
            // One tap in each of Taps equal slots, somewhere in it: spread, never two together.
            int slot = Span / Taps;
            _pos[k] = k * slot + rng.Next(slot);
            float sign = rng.Next(2) == 0 ? -1f : 1f;
            float weight = MathF.Exp(-_pos[k] / (0.02f * 44100f));
            _gain[k] = sign * weight;
            energy += weight * (double)weight;
        }
        float norm = (float)(1.0 / Math.Sqrt(energy));
        for (int k = 0; k < Taps; k++) _gain[k] *= norm;
    }

    /// <summary>
    /// A pair of ear filters: two independent velvet sequences, each tap anywhere in its own slot.
    ///
    /// Not interleaved. Forcing the ears' taps into alternate 2.7 ms slots, each tap in the first
    /// 0.7 ms of its slot, made each ear's filter a near-regular pulse train 5.4 ms apart: a comb, a
    /// pitch near 180 Hz, measured as the left ear repeating itself at 5.5-5.7 ms (0.32) on a click
    /// (2026-09-30) and heard as "a metallic reverb, not natural". Taps free across their slots do not
    /// ring; the ears are a little less unlike for it.
    /// </summary>
    public static (DiffuseBranch Left, DiffuseBranch Right) EarPair(int seed, int span = 1764, int taps = 96)
        => (new DiffuseBranch(seed, taps, span), new DiffuseBranch(seed + 977, taps, span));

    private DiffuseBranch(int taps, int span)
    {
        Taps = taps; _pos = new int[taps]; _gain = new float[taps]; _line = new float[span + 1];
    }

    public float Process(float x)
    {
        _line[_at] = x;
        float y = 0f;
        int n = _line.Length;
        for (int k = 0; k < Taps; k++)
        {
            int i = _at - _pos[k]; if (i < 0) i += n;
            y += _gain[k] * _line[i];
        }
        _at = _at + 1 == n ? 0 : _at + 1;
        return y;
    }

    public void Reset() { Array.Clear(_line); _at = 0; }
}

/// <summary>
/// Makes one ear's copy of a room's tail its own.
///
/// The traced reverb is rebuilt from an energy field round the listener, and in a diffuse room the
/// energy comes equally from every way — so every directional channel cancels and what is left is the
/// omnidirectional one. Decoded round the head, that is the SAME signal in both ears: the room in the
/// middle of the head, mono, with the floor-and-ceiling flutter of a low room sitting on top of it.
/// Measured from a capture in 64 Alder Street (2026-09-29): 0.8-0.99 interaural correlation in the tail
/// where a real room is 0.1-0.5; the traced IR's first-order channels 18-26 dB under the omni one where
/// a diffuse field puts them 5 dB under ("fluttering centrally... I can't sense the actual room").
///
/// A real tail differs at the two ears because every reflection reaches each by its own path. This
/// gives each ear its own chain of all-passes — mutually prime delays, a different set per ear — which
/// changes the phase of every component and not its level, so the spectrum and the decay are the
/// trace's and the two ears no longer carry one signal. Mixer-thread object, allocated once.
/// </summary>
internal sealed class EarDecorrelator
{
    private readonly float[][] _lines;
    private readonly int[] _at;
    private const float G = 0.5f;

    /// <param name="ear">0 left, 1 right: which set of delays.</param>
    public EarDecorrelator(int ear)
    {
        // Samples at 44.1 kHz, 0.16 to 2.2 ms. The first set ran to 13 ms at a feedback of 0.6, and
        // six of those in a row are a reverberator: a click came out as 100 ms of build-up peaking
        // 20-45 ms late, a hall laid over every room ("like I'm in a stadium", "ears cupped"). These
        // hand 90 % of a click back inside 9 ms and still take the ears to 0.2-0.3 above 1 kHz.
        //
        // And the SAME total in each ear. A chain of all-passes delays a signal, on average, by the sum
        // of its delays; the right set summed to 292 samples against the left's 260, so the tail
        // reached the left ear 0.7 ms first on every sound — as large as the head ever makes that
        // difference — and the ear put the whole room on the left whichever way you faced ("reverb
        // stays static, sounds like to my left", 2026-09-29). Both sets now sum to 260.
        int[] delays = ear == 0 ? new[] { 7, 19, 31, 47, 67, 89 } : new[] { 13, 17, 41, 43, 71, 75 };
        _lines = new float[delays.Length][];
        _at = new int[delays.Length];
        for (int k = 0; k < delays.Length; k++) _lines[k] = new float[delays[k]];
    }

    // Below a few hundred hertz the two ears of a real diffuse field hear nearly the same thing — the
    // wavelength is longer than the head — so the bottom is left shared and only the rest is pulled
    // apart. Pulled apart all the way down, a room's bass goes wide and hollow in headphones.
    private const float SplitHz = 300f;
    private readonly float _lpA = 1f - MathF.Exp(-2f * MathF.PI * SplitHz / 44100f);
    private float _a1, _a2, _b1;

    public float Process(float x)
    {
        // A second-order Linkwitz-Riley split from first-order sections: the bottom is LP x LP, the
        // rest HP x HP, and LP^2 - HP^2 is an all-pass — the two halves add back to the level they
        // came from, with the bottom 25 dB clear of the scattered half by 100 Hz. (Subtracting a
        // steep low-pass from the input is not a high-pass: it left three quarters of 100 Hz in the
        // half that was being pulled apart.)
        _a1 += _lpA * (x - _a1);                 // low, once
        _a2 += _lpA * (_a1 - _a2);               // low, twice
        float h1 = x - _a1;                       // high, once (a one-pole's complement is exact)
        _b1 += _lpA * (h1 - _b1);
        float h2 = h1 - _b1;                      // high, twice
        return _a2 - Scatter(h2);
    }

    private float Scatter(float x)
    {
        for (int k = 0; k < _lines.Length; k++)
        {
            var line = _lines[k];
            int at = _at[k];
            float delayed = line[at];
            float y = -G * x + delayed;
            line[at] = x + G * y;
            _at[k] = at + 1 == line.Length ? 0 : at + 1;
            x = y;
        }
        return x;
    }
}
