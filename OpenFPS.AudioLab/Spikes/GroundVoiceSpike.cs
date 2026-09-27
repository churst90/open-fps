using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// A person talking on a pavement, through the game's own HRTF, three ways: no ground; the ground's
/// answer summed into the voice's own direction (what was built first on 2026-09-27, heard as heavy
/// flanging); and the ground's answer from its image below the pavement, through an HRTF of its own.
///
///   --ground-voice [line.ogg] [--at=3] [--angle=30] [--out=DIR]
///
/// The geometry and the surface are the game's (ClientAudioSystem.ApplyGround): asphalt, a mouth at
/// Speech.MouthHeight, an ear at 1.7 m.
/// </summary>
public static class GroundVoiceSpike
{
    const int Rate = 48000, Frame = 1024;

    public static int Run(string[] args)
    {
        string line = Array.Find(args, a => a.EndsWith(".ogg") || a.EndsWith(".wav"))
                      ?? Path.Combine(Repo(), "OpenFPS.Client/ASSETS/SOUNDS/VOICES/tim/greet_hey_how_s_it_going.ogg");
        float at = Arg(args, "--at=", 3f), angle = Arg(args, "--angle=", 30f);
        string outDir = Array.Find(args, a => a.StartsWith("--out="))?[6..]
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "openfps-listen", "ground-voice");
        Directory.CreateDirectory(outDir);

        float[] dry = Decode(line);
        double lufs = Speech.LoudnessLufs(dry);
        float norm = (float)Math.Pow(10.0, (Speech.BufferLoudnessLufs - lufs) / 20.0);
        for (int i = 0; i < dry.Length; i++) dry[i] *= norm;

        // Geometry: the listener at the origin facing -z (Steam Audio's forward), ear 1.7 m up.
        float rad = angle * MathF.PI / 180f;
        var ear = new Vector3(0f, 1.7f, 0f);
        var mouth = new Vector3(at * MathF.Sin(rad), Speech.MouthHeight, -at * MathF.Cos(rad));
        var image = new Vector3(mouth.X, -mouth.Y, mouth.Z);
        float direct = Vector3.Distance(mouth, ear), mirrored = Vector3.Distance(image, ear);
        var m = AcousticRegistry.GetProperties("Asphalt");
        float spread = direct / mirrored;
        float rho = 1f / (0.5f * (1f / mouth.Y + 1f / ear.Y));
        float low = MathF.Sqrt(1f - 0.5f * (m.AbsorptionLow + m.AbsorptionMid)) * spread * ClientAudioSystem.Coherence(250f, direct, rho);
        float high = MathF.Sqrt(1f - m.AbsorptionHigh) * Math.Clamp(0.94f - 0.0055f * direct, 0.45f, 1f)
                     * spread * ClientAudioSystem.Coherence(2500f, direct, rho);
        float delay = (mirrored - direct) / AudioPhysics.SpeedOfSound;
        Console.WriteLine($"talker {at:F1} m at {angle:F0} deg: ground {delay * 1000f:F2} ms late, low {low:F2}, high {high:F2}");

        var dirDirect = Dir(mouth - ear);
        var dirImage = Dir(image - ear);
        Console.WriteLine($"direct from {Elevation(mouth - ear):F0} deg, ground from {Elevation(image - ear):F0} deg");

        // The reflected part alone, exactly as the stage makes it.
        var g = new GroundReflection(Rate);
        g.Set(delay, low, high);
        var reflected = new float[dry.Length];
        for (int i = 0; i < dry.Length; i++) reflected[i] = g.Process(dry[i]) - dry[i];
        var summed = new float[dry.Length];
        for (int i = 0; i < dry.Length; i++) summed[i] = dry[i] + reflected[i];

        var none = Binaural(dry, dirDirect);
        var oneDirection = Binaural(summed, dirDirect);
        var fromBelow = Binaural(dry, dirDirect);
        var groundPart = Binaural(reflected, dirImage);
        for (int i = 0; i < fromBelow.Length; i++) fromBelow[i] += groundPart[i];

        string stem = Path.GetFileNameWithoutExtension(line);
        Write(Path.Combine(outDir, $"{stem}_1_no_ground.wav"), none);
        Write(Path.Combine(outDir, $"{stem}_2_ground_same_direction.wav"), oneDirection);
        Write(Path.Combine(outDir, $"{stem}_3_ground_from_below.wav"), fromBelow);
        Console.WriteLine($"wrote {outDir}/{stem}_[1-3]*.wav");
        return 0;
    }

    static float Arg(string[] args, string key, float fallback)
    {
        string? a = Array.Find(args, x => x.StartsWith(key));
        return a != null && float.TryParse(a[key.Length..], System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }

    static Phonon.IPLVector3 Dir(Vector3 v)
    {
        v = Vector3.Normalize(v);
        return new Phonon.IPLVector3 { x = v.X, y = v.Y, z = v.Z };
    }

    static float Elevation(Vector3 v) => MathF.Asin(Vector3.Normalize(v).Y) * 180f / MathF.PI;

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client"))) dir = dir.Parent;
        return dir?.FullName ?? "/home/cody/external-rescue/Github/open-fps";
    }

    static float[] Decode(string path)
    {
        string raw = Path.GetTempFileName();
        Process.Start(new ProcessStartInfo("sox", $"\"{path}\" -t f32 -r {Rate} -c 1 \"{raw}\"") { UseShellExecute = false })!.WaitForExit();
        byte[] b = File.ReadAllBytes(raw);
        File.Delete(raw);
        var x = new float[b.Length / 4 + Rate / 2];                 // half a second of tail room
        Buffer.BlockCopy(b, 0, x, 0, b.Length / 4 * 4);
        return x;
    }

    static float[] Binaural(float[] mono, Phonon.IPLVector3 dir)
    {
        var cs = Phonon.DefaultContextSettings();
        Phonon.iplContextCreate(ref cs, out IntPtr ctx);
        var au = new Phonon.IPLAudioSettings { samplingRate = Rate, frameSize = Frame };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out IntPtr eff);
        var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(ctx, 1, Frame, ref inBuf);
        Phonon.iplAudioBufferAllocate(ctx, 2, Frame, ref outBuf);
        var m = new float[Frame]; var st = new float[Frame * 2];
        var y = new float[mono.Length * 2];
        for (int f = 0; f + Frame <= mono.Length; f += Frame)
        {
            Array.Copy(mono, f, m, 0, Frame);
            Phonon.iplAudioBufferDeinterleave(ctx, m, ref inBuf);
            var prm = new Phonon.IPLBinauralEffectParams { direction = dir, interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = hrtf, peakDelays = IntPtr.Zero };
            Phonon.iplBinauralEffectApply(eff, ref prm, ref inBuf, ref outBuf);
            Phonon.iplAudioBufferInterleave(ctx, ref outBuf, st);
            Array.Copy(st, 0, y, f * 2, Frame * 2);
        }
        Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref outBuf);
        Phonon.iplBinauralEffectRelease(ref eff); Phonon.iplHRTFRelease(ref hrtf); Phonon.iplContextRelease(ref ctx);
        return y;
    }

    static void Write(string path, float[] stereo)
    {
        string raw = Path.GetTempFileName();
        var b = new byte[stereo.Length * 4];
        Buffer.BlockCopy(stereo, 0, b, 0, b.Length);
        File.WriteAllBytes(raw, b);
        Process.Start(new ProcessStartInfo("sox", $"-t f32 -r {Rate} -c 2 \"{raw}\" -b 16 \"{path}\" gain -n -3") { UseShellExecute = false })!.WaitForExit();
        File.Delete(raw);
    }
}
