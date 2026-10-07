using System;
using System.Numerics;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// What an HRTF does to the level at the ears, band by band, for one direction: the two ears' mean
/// power through Steam Audio's binaural effect, per third of an octave of
/// <see cref="OpenFPS.Client.AudioEngine.Core.Engine.BandEq.Centres"/>.
///
/// For the cabin's paths (CabinPaths): the interior model works out the pressure at the ear, and the one
/// interior voice was played from a little ahead and below. Played from where each path comes in, the
/// HRTF's own colouring of each direction would change the level at the ears by up to seven decibels in
/// an octave (more from the side at 4 kHz, more from below at 63 Hz in Steam Audio's set, measured with
/// AudioLab --cabin hrtf), which is the HRTF, not the cabin. Each path is equalised by the difference
/// between its direction and the one voice's, so the two ears hear the model's level and the path's own
/// interaural differences.
/// </summary>
internal static class HrtfBands
{
    private const int Blocks = 4, PointsPerBand = 16;

    /// <summary>The ears' mean power per band, dB, through <paramref name="hrtf"/> from
    /// <paramref name="direction"/> (Steam Audio's frame: x right, y up, z back). Null if Steam Audio
    /// would not make an effect. Allocates; game thread.</summary>
    public static float[]? Measure(IntPtr context, IntPtr hrtf, int rate, int frame, Vector3 direction)
    {
        if (context == IntPtr.Zero || hrtf == IntPtr.Zero || frame <= 0) return null;
        var au = new Phonon.IPLAudioSettings { samplingRate = rate, frameSize = frame };
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        if (Phonon.iplBinauralEffectCreate(context, ref au, ref es, out IntPtr effect) != Phonon.IPL_STATUS_SUCCESS) return null;
        var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(context, 1, frame, ref inBuf);
        Phonon.iplAudioBufferAllocate(context, 2, frame, ref outBuf);
        var mono = new float[frame];
        var stereo = new float[frame * 2];
        var left = new float[frame * Blocks];
        var right = new float[frame * Blocks];
        var d = Phonon.SafeDirection(direction);
        try
        {
            for (int b = 0; b < Blocks; b++)
            {
                Array.Clear(mono);
                if (b == 0) mono[0] = 1f;
                Phonon.iplAudioBufferDeinterleave(context, mono, ref inBuf);
                var prm = new Phonon.IPLBinauralEffectParams
                {
                    direction = new Phonon.IPLVector3 { x = d.X, y = d.Y, z = d.Z },
                    interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR,
                    spatialBlend = 1f, hrtf = hrtf, peakDelays = IntPtr.Zero,
                };
                Phonon.iplBinauralEffectApply(effect, ref prm, ref inBuf, ref outBuf);
                Phonon.iplAudioBufferInterleave(context, ref outBuf, stereo);
                for (int i = 0; i < frame; i++) { left[b * frame + i] = stereo[2 * i]; right[b * frame + i] = stereo[2 * i + 1]; }
            }
        }
        finally
        {
            Phonon.iplAudioBufferFree(context, ref inBuf);
            Phonon.iplAudioBufferFree(context, ref outBuf);
            Phonon.iplBinauralEffectRelease(ref effect);
        }

        var centres = OpenFPS.Client.AudioEngine.Core.Engine.BandEq.Centres;
        double half = OpenFPS.Client.AudioEngine.Core.Engine.BandEq.HalfWidth;
        var db = new float[centres.Length];
        for (int k = 0; k < centres.Length; k++)
        {
            double lo = centres[k] / half, hi = Math.Min(centres[k] * half, 0.49 * rate), sum = 0;
            for (int j = 0; j < PointsPerBand; j++)
            {
                double f = lo + (hi - lo) * (j + 0.5) / PointsPerBand;
                sum += 0.5 * (Power(left, f, rate) + Power(right, f, rate));
            }
            db[k] = (float)(10 * Math.Log10(sum / PointsPerBand + 1e-30));
        }
        return db;
    }

    /// <summary>|H(f)|² of a response, by Goertzel.</summary>
    private static double Power(float[] h, double f, int rate)
    {
        double w = 2 * Math.PI * f / rate, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int n = 0; n < h.Length; n++)
        {
            double s = h[n] + c * s1 - s2;
            s2 = s1; s1 = s;
        }
        return s1 * s1 + s2 * s2 - c * s1 * s2;
    }
}
