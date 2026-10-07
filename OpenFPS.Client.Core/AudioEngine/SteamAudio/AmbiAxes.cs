using System.Numerics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Which world direction each of Steam Audio's first-order ambisonic channels stands for, and with what
/// sign, found by encoding a sound from each axis through its own encoder and reading the channels
/// back. The intensity of a field is then sum(W x channel_c x axis_c) over the three channels, in
/// Steam Audio's world; nothing about channel order or handedness is assumed.
/// </summary>
internal static class AmbiAxes
{
    /// <summary>The axes of channels 1, 2 and 3, scaled so a sound straight down an axis reads as
    /// |I|/E = 1. Zero vectors if the encoder cannot be made.</summary>
    public static (Vector3 C1, Vector3 C2, Vector3 C3) Calibrate(IntPtr context, int sampleRate)
    {
        const int order = 1, channels = 4, frame = 64;
        var au = new Phonon.IPLAudioSettings { samplingRate = sampleRate, frameSize = frame };
        var es = new Phonon.IPLAmbisonicsEncodeEffectSettings { maxOrder = order };
        if (Phonon.iplAmbisonicsEncodeEffectCreate(context, ref au, ref es, out IntPtr enc) != Phonon.IPL_STATUS_SUCCESS)
            return default;
        var inB = new Phonon.IPLAudioBuffer(); var outB = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(context, 1, frame, ref inB);
        Phonon.iplAudioBufferAllocate(context, channels, frame, ref outB);
        var ones = new float[frame]; Array.Fill(ones, 1f);
        var inter = new float[frame * channels];
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var gains = new float[3, 3];
        for (int a = 0; a < 3; a++)
        {
            var dir = new Phonon.IPLVector3 { x = axes[a].X, y = axes[a].Y, z = axes[a].Z };
            var ep = new Phonon.IPLAmbisonicsEncodeEffectParams { direction = dir, order = order };
            for (int rep = 0; rep < 3; rep++)
            {
                Phonon.iplAudioBufferDeinterleave(context, ones, ref inB);
                Phonon.iplAmbisonicsEncodeEffectApply(enc, ref ep, ref inB, ref outB);
            }
            Phonon.iplAudioBufferInterleave(context, ref outB, inter);
            float w = inter[(frame - 1) * channels];
            for (int c = 1; c < 4; c++) gains[a, c - 1] = w != 0 ? inter[(frame - 1) * channels + c] / w : 0f;
        }
        Vector3 Axis(int c)
        {
            var v = new Vector3(gains[0, c], gains[1, c], gains[2, c]);
            float l2 = v.LengthSquared();
            return l2 > 1e-9f ? v / l2 : Vector3.Zero;
        }
        var result = (Axis(0), Axis(1), Axis(2));
        Phonon.iplAmbisonicsEncodeEffectRelease(ref enc);
        Phonon.iplAudioBufferFree(context, ref inB); Phonon.iplAudioBufferFree(context, ref outB);
        return result;
    }

    /// <summary>A direction in Steam Audio's world, in the game's (Phonon.World mirrors z).</summary>
    public static Vector3 ToGame(Vector3 sa) => new(sa.X, sa.Y, Phonon.WorldZ(sa.Z));
}
