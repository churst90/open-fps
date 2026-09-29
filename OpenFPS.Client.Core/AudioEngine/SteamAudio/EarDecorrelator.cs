using System;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// One direction's share of a diffuse field: the late tail through a short all-pass chain of its
/// own, so that eight of these fed the same tail are eight different signals with the same
/// spectrum and envelope, as the field arriving from eight directions of a real room is.
///
/// The delays are long — each chain sums to 14-18 ms — where the ear decorrelator's are kept under
/// 2 ms. Two chains are different signals only at frequencies their phase responses have wound
/// apart at, and with delays under 100 samples that is above a kilohertz: below it the eight
/// branches were still one signal, summed coherently in the field and again in the decoder, and
/// the tail came out with ten decibels of extra bass (--sa-encode, 2026-09-29). Delays a hundred
/// samples and more apart tell the branches apart from about 300 Hz, where the low end is taken out
/// of their hands anyway (DiffuseTail.SplitHz). The smearing that made the short delays necessary
/// elsewhere does not apply: only the late tail comes through here, and it begins 50 ms after the
/// sound, as a noise already a second long.
/// </summary>
internal sealed class DiffuseBranch
{
    private static readonly int[][] Delays =
    {
        new[] { 89, 181, 419 }, new[] { 131, 263, 311 }, new[] { 97, 331, 227 }, new[] { 149, 197, 397 },
        new[] { 113, 307, 251 }, new[] { 173, 241, 353 }, new[] { 103, 277, 373 }, new[] { 157, 211, 431 },
    };
    public const int Count = 8;
    private const float G = 0.5f;
    private readonly float[][] _lines;
    private readonly int[] _at;

    public DiffuseBranch(int index)
    {
        var d = Delays[index % Delays.Length];
        _lines = new float[d.Length][];
        _at = new int[d.Length];
        for (int k = 0; k < d.Length; k++) _lines[k] = new float[d[k]];
    }

    public float Process(float x)
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

    public void Reset() { foreach (var l in _lines) Array.Clear(l); Array.Clear(_at); }
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
