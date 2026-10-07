namespace OpenFPS.Client.AudioEngine.Core;

/// <summary>How a multi-channel ambisonic file lays out its channels and scales them.</summary>
public enum AmbisonicLayout
{
    /// <summary>ACN order, SN3D normalization: almost every modern recording and microphone, and the
    /// default.</summary>
    AmbiX,

    /// <summary>ACN order, N3D (orthonormal): Steam Audio's own format, no conversion.</summary>
    N3D,

    /// <summary>Furse-Malham: WXYZ order with W 1/√2 down, the old "B-format" of many free recordings.</summary>
    FuMa
}

/// <summary>
/// Converts ambisonic audio to N3D/ACN, which Steam Audio's decode effect assumes. Neither mismatch is an
/// error: AmbiX decoded as N3D has its directional components 1.7x too quiet (a vague, over-wide field),
/// and FuMa decoded as ACN swaps the axes (front becomes up).
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

    /// <summary>Rewrites one interleaved block in place into N3D/ACN. Allocates nothing.</summary>
    /// <param name="samples">Interleaved frames of <paramref name="channels"/> channels.</param>
    /// <param name="channels">Channels per frame.</param>
    /// <param name="layout">The layout the block is in.</param>
    public static void ConvertToN3d(Span<float> samples, int channels, AmbisonicLayout layout)
    {
        if (channels <= 0 || samples.Length < channels) return;
        int frames = samples.Length / channels;

        switch (layout)
        {
            case AmbisonicLayout.N3D:
                return;

            case AmbisonicLayout.AmbiX:
                for (int c = 0; c < channels; c++)
                {
                    float gain = Sn3dToN3dGain(c);
                    if (gain == 1f) continue;
                    for (int f = 0; f < frames; f++) samples[f * channels + c] *= gain;
                }
                return;

            case AmbisonicLayout.FuMa:
                // First order only: W X Y Z where ACN wants W Y Z X, and W 1/√2 down.
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

    /// <summary>Guesses a file's layout from its name; the bed spec can always say outright.</summary>
    public static AmbisonicLayout GuessLayout(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return AmbisonicLayout.AmbiX;
        string n = fileName.ToLowerInvariant();

        if (n.Contains("fuma") || n.Contains("_fma") || n.Contains("-fma")) return AmbisonicLayout.FuMa;

        // Order matters: "sn3d" contains "n3d", and most AmbiX files are labelled SN3D.
        if (n.Contains("sn3d") || n.Contains("ambix") || n.Contains("acn")) return AmbisonicLayout.AmbiX;
        if (n.Contains("n3d")) return AmbisonicLayout.N3D;

        return AmbisonicLayout.AmbiX; // foa, b-format, or nothing at all: assume the common case
    }
}
