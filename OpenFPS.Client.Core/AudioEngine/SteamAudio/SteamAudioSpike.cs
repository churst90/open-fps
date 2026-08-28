using System;
using System.IO;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Spike: render a mono broadband source orbiting the listener through Steam Audio's HRTF
/// binaural effect, written to a stereo WAV. Proves real HRTF (above/below + front/back),
/// isolated from FMOD and the rest of the engine. Play the WAV on headphones.
///
/// Segment 1 (0-8s): HORIZONTAL circle  front -> right -> back -> left.
/// Segment 2 (8-16s): VERTICAL circle    front -> above -> back -> below.
/// </summary>
public static class SteamAudioSpike
{
    public static int RenderOrbitWav(string outputPath)
    {
        const int sampleRate = 44100;
        const int frameSize = 1024;
        const double segmentSeconds = 8.0;
        const double revolutionSeconds = 4.0; // two revolutions per segment

        var ctxSettings = Phonon.DefaultContextSettings();
        int err = Phonon.iplContextCreate(ref ctxSettings, out IntPtr context);
        if (err != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine($"iplContextCreate failed: {err}"); return 1; }

        var audio = new Phonon.IPLAudioSettings { samplingRate = sampleRate, frameSize = frameSize };
        var hrtfSettings = new Phonon.IPLHRTFSettings
        {
            type = Phonon.IPL_HRTFTYPE_DEFAULT,
            volume = 1.0f,
            normType = Phonon.IPL_HRTFNORMTYPE_NONE
        };
        err = Phonon.iplHRTFCreate(context, ref audio, ref hrtfSettings, out IntPtr hrtf);
        if (err != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine($"iplHRTFCreate failed: {err}"); return 1; }

        var effSettings = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        err = Phonon.iplBinauralEffectCreate(context, ref audio, ref effSettings, out IntPtr effect);
        if (err != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine($"iplBinauralEffectCreate failed: {err}"); return 1; }

        var inBuf = new Phonon.IPLAudioBuffer();
        var outBuf = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(context, 1, frameSize, ref inBuf);
        Phonon.iplAudioBufferAllocate(context, 2, frameSize, ref outBuf);

        var monoFrame = new float[frameSize];
        var stereoFrame = new float[frameSize * 2];

        double totalSeconds = segmentSeconds * 2;
        int totalFrames = (int)(totalSeconds * sampleRate / frameSize);
        using var pcm = new MemoryStream(totalFrames * frameSize * 4);

        const int burstPeriod = sampleRate / 6; // 6 broadband bursts/sec
        uint seed = 2463534242;
        long sampleIndex = 0;
        double rmsL = 0, rmsR = 0;
        long count = 0;

        for (int f = 0; f < totalFrames; f++)
        {
            for (int i = 0; i < frameSize; i++)
            {
                long gi = sampleIndex + i;
                int posInPeriod = (int)(gi % burstPeriod);
                float env = posInPeriod < burstPeriod * 0.30 ? 1f : 0f;
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; // xorshift32
                float noise = (int)seed / (float)int.MaxValue;
                monoFrame[i] = noise * env * 0.5f;
            }
            sampleIndex += frameSize;

            Phonon.iplAudioBufferDeinterleave(context, monoFrame, ref inBuf);

            double t = (double)f * frameSize / sampleRate;
            Phonon.IPLVector3 dir;
            if (t < segmentSeconds)
            {
                double th = 2 * Math.PI * (t / revolutionSeconds);
                // Steam Audio: -z forward, +x right. front->right->back->left.
                dir = new Phonon.IPLVector3 { x = (float)Math.Sin(th), y = 0f, z = (float)(-Math.Cos(th)) };
            }
            else
            {
                double ph = 2 * Math.PI * ((t - segmentSeconds) / revolutionSeconds);
                // Vertical median-plane circle: front->up->back->down.
                dir = new Phonon.IPLVector3 { x = 0f, y = (float)Math.Sin(ph), z = (float)(-Math.Cos(ph)) };
            }

            var prm = new Phonon.IPLBinauralEffectParams
            {
                direction = dir,
                interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR,
                spatialBlend = 1.0f,
                hrtf = hrtf,
                peakDelays = IntPtr.Zero
            };
            Phonon.iplBinauralEffectApply(effect, ref prm, ref inBuf, ref outBuf);
            Phonon.iplAudioBufferInterleave(context, ref outBuf, stereoFrame);

            for (int i = 0; i < frameSize; i++)
            {
                float l = Math.Clamp(stereoFrame[i * 2], -1f, 1f);
                float r = Math.Clamp(stereoFrame[i * 2 + 1], -1f, 1f);
                rmsL += l * l; rmsR += r * r; count++;
                short sl = (short)(l * short.MaxValue);
                short sr = (short)(r * short.MaxValue);
                pcm.WriteByte((byte)(sl & 0xFF)); pcm.WriteByte((byte)((sl >> 8) & 0xFF));
                pcm.WriteByte((byte)(sr & 0xFF)); pcm.WriteByte((byte)((sr >> 8) & 0xFF));
            }
        }

        Phonon.iplAudioBufferFree(context, ref inBuf);
        Phonon.iplAudioBufferFree(context, ref outBuf);
        Phonon.iplBinauralEffectRelease(ref effect);
        Phonon.iplHRTFRelease(ref hrtf);
        Phonon.iplContextRelease(ref context);

        byte[] data = pcm.ToArray();
        WriteWav(outputPath, data, sampleRate, 2);

        Console.WriteLine($"Rendered {totalSeconds:F0}s, {data.Length / 1024} KB. " +
                          $"RMS L={Math.Sqrt(rmsL / count):F4} R={Math.Sqrt(rmsR / count):F4} " +
                          $"(L!=R confirms binaural processing).");
        return 0;
    }

    private static void WriteWav(string path, byte[] pcm16, int sampleRate, int channels)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        int byteRate = sampleRate * channels * 2;
        int blockAlign = channels * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + pcm16.Length); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(byteRate); w.Write((short)blockAlign); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(pcm16.Length); w.Write(pcm16);
    }
}
