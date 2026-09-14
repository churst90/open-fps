using System;

namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>How a multi-channel ambisonic file lays out its channels and scales them.</summary>
public enum AmbisonicLayout
{
    /// <summary>ACN channel order, SN3D normalization. What "AmbiX" means, what almost every modern
    /// ambisonic recording and every ambisonic microphone's output uses, and the sane default.</summary>
    AmbiX,

    /// <summary>ACN channel order, N3D (fully orthonormal) normalization. Steam Audio's own native
    /// format, so this needs no conversion at all.</summary>
    N3D,

    /// <summary>Furse-Malham: WXYZ channel order with W attenuated by 1/√2. What "B-format" usually
    /// meant before AmbiX existed, so older free recordings are often this — and feeding FuMa to a
    /// decoder expecting AmbiX does not fail, it just puts everything in the wrong place.</summary>
    FuMa
}

/// <summary>
/// Converts a block of ambisonic audio into the format Steam Audio's decode effect actually wants.
///
/// Steam Audio's native format is N3D — ACN channel order, orthonormal spherical harmonics — and the
/// decode effect assumes it. Recordings in the wild are almost never N3D: they are AmbiX (ACN/SN3D) or,
/// if they are older, FuMa. Neither mismatch produces an error. AmbiX fed to an N3D decoder renders with
/// the directional components 1.7x too quiet, which reads as a vague, over-wide, badly localized field.
/// FuMa fed to an ACN decoder swaps the axes outright: front becomes up.
///
/// So this is not a nicety. It is the difference between a soundfield that points the right way and one
/// that sounds broken in a way that is very hard to diagnose by ear.
/// </summary>
public static class AmbisonicFormat
{
    /// <summary>Channels in a full-sphere ambisonic signal of this order: (order + 1)².
    /// First order is 4 (W, Y, Z, X in ACN order), second is 9, third is 16.</summary>
    public static int ChannelsForOrder(int order) => order < 0 ? 0 : (order + 1) * (order + 1);

    /// <summary>The highest order a file with this many channels can carry, or −1 if the count is not a
    /// full-sphere ambisonic layout at all (2 channels is stereo, not ambisonics).</summary>
    public static int OrderForChannels(int channels)
    {
        if (channels < 1) return -1;
        int order = (int)Math.Round(Math.Sqrt(channels)) - 1;
        return ChannelsForOrder(order) == channels ? order : -1;
    }

    /// <summary>The ACN index's order: channel 0 is order 0, channels 1–3 are order 1, 4–8 order 2.</summary>
    public static int OrderOfAcnChannel(int acn) => acn < 0 ? 0 : (int)Math.Floor(Math.Sqrt(acn));

    /// <summary>SN3D → N3D gain for one ACN channel: √(2n + 1) for order n.</summary>
    public static float Sn3dToN3dGain(int acn) => MathF.Sqrt(2f * OrderOfAcnChannel(acn) + 1f);

    /// <summary>
    /// Rewrites one interleaved block in place into N3D/ACN.
    ///
    /// Interleaved rather than planar because that is how a decoded audio file arrives and how FMOD
    /// hands buffers around; the caller de-interleaves once, afterwards, into Steam Audio's planar
    /// buffer. Allocates nothing.
    /// </summary>
    /// <param name="samples">Interleaved frames of <paramref name="channels"/> channels.</param>
    public static void ConvertToN3d(Span<float> samples, int channels, AmbisonicLayout layout)
    {
        if (channels <= 0 || samples.Length < channels) return;
        int frames = samples.Length / channels;

        switch (layout)
        {
            case AmbisonicLayout.N3D:
                return; // already what the decoder wants

            case AmbisonicLayout.AmbiX:
                // Channel order already matches; only the normalization differs.
                for (int c = 0; c < channels; c++)
                {
                    float gain = Sn3dToN3dGain(c);
                    if (gain == 1f) continue;
                    for (int f = 0; f < frames; f++) samples[f * channels + c] *= gain;
                }
                return;

            case AmbisonicLayout.FuMa:
                // FuMa is first order only in any file worth worrying about, and it differs in BOTH
                // ways: the channels are W X Y Z where ACN wants W Y Z X, and W is recorded 1/√2 down.
                if (channels < 4) return;
                float w = MathF.Sqrt(2f);
                float dir = Sn3dToN3dGain(1); // the three first-order channels share one gain
                for (int f = 0; f < frames; f++)
                {
                    int b = f * channels;
                    float fw = samples[b + 0], fx = samples[b + 1], fy = samples[b + 2], fz = samples[b + 3];
                    samples[b + 0] = fw * w;    // W  -> ACN 0
                    samples[b + 1] = fy * dir;  // Y  -> ACN 1
                    samples[b + 2] = fz * dir;  // Z  -> ACN 2
                    samples[b + 3] = fx * dir;  // X  -> ACN 3
                }
                return;
        }
    }

    /// <summary>
    /// Guesses the layout of a file from its name, so an author can drop a download straight in.
    /// A filename is a hint and nothing more — the bed spec can always say outright — but the naming
    /// conventions in the wild are consistent enough to be worth reading.
    /// </summary>
    public static AmbisonicLayout GuessLayout(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return AmbisonicLayout.AmbiX;
        string n = fileName.ToLowerInvariant();

        if (n.Contains("fuma") || n.Contains("_fma") || n.Contains("-fma")) return AmbisonicLayout.FuMa;

        // ORDER MATTERS: "sn3d" contains "n3d". Checking for N3D first reads every file labelled
        // SN3D — which is to say most AmbiX files, since SN3D is what AmbiX normalization is called —
        // as N3D, and then skips the conversion they needed.
        if (n.Contains("sn3d") || n.Contains("ambix") || n.Contains("acn")) return AmbisonicLayout.AmbiX;
        if (n.Contains("n3d")) return AmbisonicLayout.N3D;

        return AmbisonicLayout.AmbiX; // foa, b-format, or nothing at all: assume the common case
    }
}
