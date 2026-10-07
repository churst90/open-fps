using System.Diagnostics;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// A person talking on a pavement, through the game's own HRTF, three ways: no ground; the ground's
/// answer summed into the voice's own direction (what was built first on 2026-09-27, heard as heavy
/// flanging); and the ground's answer from its image below the pavement, through an HRTF of its own.
///
///   --ground-voice [line.ogg] [--at=3] [--angle=30] [--out=DIR] [--ladder]
///
/// --ladder renders the reflection at its physical level and 6, 12 and 20 dB below it, and at the
/// physical level with the talker and listener moving as standing people do. Every file has the same
/// gain, so they compare by level too.
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
                      ?? OpenFPS.AudioLab.LabPaths.Sounds("VOICES", "tim", "greet_hey_how_s_it_going.ogg");
        float at = Arg(args, "--at=", 3f), angle = Arg(args, "--angle=", 30f);
        string outDir = Array.Find(args, a => a.StartsWith("--out="))?[6..]
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "openfps-listen", "ground-voice");
        Directory.CreateDirectory(outDir);

        float[] dry = Decode(line);
        double lufs = Speech.LoudnessLufs(dry);
        float norm = (float)Math.Pow(10.0, (Speech.BufferLoudnessLufs - lufs) / 20.0);
        for (int i = 0; i < dry.Length; i++) dry[i] *= norm;
        string stem = Path.GetFileNameWithoutExtension(line);

        if (args.Contains("--ladder"))
        {
            // The same line at the physical level and turned down, still and moving, all from below.
            int k = 1;
            foreach (float db in new[] { 0f, -6f, -12f, -20f })
                Write(Path.Combine(outDir, $"{stem}_ladder_{k++}_{(db == 0f ? "physical" : $"{-db:F0}dB_down")}.wav"),
                      Render(dry, at, angle, db, moving: false, fromBelow: true));
            Write(Path.Combine(outDir, $"{stem}_ladder_{k++}_physical_moving.wav"),
                  Render(dry, at, angle, 0f, moving: true, fromBelow: true));
            Write(Path.Combine(outDir, $"{stem}_ladder_0_no_ground.wav"),
                  Render(dry, at, angle, float.NegativeInfinity, moving: false, fromBelow: true));
            Console.WriteLine($"wrote {outDir}/{stem}_ladder_*.wav");
            return 0;
        }

        Write(Path.Combine(outDir, $"{stem}_1_no_ground.wav"), Render(dry, at, angle, float.NegativeInfinity, false, true));
        Write(Path.Combine(outDir, $"{stem}_2_ground_same_direction.wav"), Render(dry, at, angle, 0f, false, fromBelow: false));
        Write(Path.Combine(outDir, $"{stem}_3_ground_from_below.wav"), Render(dry, at, angle, 0f, false, fromBelow: true));
        Console.WriteLine($"wrote {outDir}/{stem}_[1-3]*.wav");
        return 0;
    }

    /// <summary>
    /// The line through the game's HRTF with its ground reflection, block by block. <paramref name="db"/>
    /// scales the reflection against its physical level (negative infinity: none). Moving: a standing
    /// talker and listener are never still — postural sway of about a centimetre at a few tenths of a
    /// hertz, and the head movements that go with speaking (Munhall et al. 2004), a centimetre or two
    /// at around a hertz — so both mouth and ear wander by that much, independently.
    /// </summary>
    static float[] Render(float[] dry, float at, float angleDeg, float db, bool moving, bool fromBelow)
    {
        float scale = float.IsNegativeInfinity(db) ? 0f : MathF.Pow(10f, db / 20f);
        float rad = angleDeg * MathF.PI / 180f;
        var m = AcousticRegistry.GetProperties("Asphalt");
        var g = new GroundReflection(Rate);
        var cs = Phonon.DefaultContextSettings();
        Phonon.iplContextCreate(ref cs, out IntPtr ctx);
        var au = new Phonon.IPLAudioSettings { samplingRate = Rate, frameSize = Frame };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
        Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out IntPtr effD);
        Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out IntPtr effG);
        var inD = new Phonon.IPLAudioBuffer(); var outD = new Phonon.IPLAudioBuffer();
        var inG = new Phonon.IPLAudioBuffer(); var outG = new Phonon.IPLAudioBuffer();
        Phonon.iplAudioBufferAllocate(ctx, 1, Frame, ref inD); Phonon.iplAudioBufferAllocate(ctx, 2, Frame, ref outD);
        Phonon.iplAudioBufferAllocate(ctx, 1, Frame, ref inG); Phonon.iplAudioBufferAllocate(ctx, 2, Frame, ref outG);
        var md = new float[Frame]; var mg = new float[Frame]; var sd = new float[Frame * 2]; var sg = new float[Frame * 2];
        var y = new float[dry.Length * 2];
        var rng = new Random(7);
        double p1 = rng.NextDouble() * 6.28, p2 = rng.NextDouble() * 6.28, p3 = rng.NextDouble() * 6.28, p4 = rng.NextDouble() * 6.28;
        bool first = true;
        for (int f = 0; f + Frame <= dry.Length; f += Frame)
        {
            double t = f / (double)Rate;
            float mouthDy = 0f, mouthDx = 0f, earDy = 0f;
            if (moving)
            {
                mouthDy = (float)(0.010 * Math.Sin(2 * Math.PI * 0.25 * t + p1) + 0.012 * Math.Sin(2 * Math.PI * 1.1 * t + p2));
                mouthDx = (float)(0.015 * Math.Sin(2 * Math.PI * 0.2 * t + p3));
                earDy = (float)(0.010 * Math.Sin(2 * Math.PI * 0.3 * t + p4) + 0.008 * Math.Sin(2 * Math.PI * 0.9 * t + p1));
            }
            var ear = new Vector3(0f, 1.7f + earDy, 0f);
            var mouth = new Vector3(at * MathF.Sin(rad) + mouthDx, Speech.MouthHeight + mouthDy, -at * MathF.Cos(rad));
            var image = new Vector3(mouth.X, -mouth.Y, mouth.Z);
            float direct = Vector3.Distance(mouth, ear), mirrored = Vector3.Distance(image, ear);
            float spread = direct / mirrored;
            float rho = 1f / (0.5f * (1f / mouth.Y + 1f / ear.Y));
            float low = MathF.Sqrt(1f - 0.5f * (m.AbsorptionLow + m.AbsorptionMid)) * spread * ClientAudioSystem.Coherence(250f, direct, rho);
            float high = MathF.Sqrt(1f - m.AbsorptionHigh) * Math.Clamp(0.94f - 0.0055f * direct, 0.45f, 1f)
                         * spread * ClientAudioSystem.Coherence(2500f, direct, rho);
            g.Set((mirrored - direct) / AudioPhysics.SpeedOfSound, low * scale, high * scale);
            if (first)
            {
                Console.WriteLine($"talker {at:F1} m at {angleDeg:F0} deg: ground {(mirrored - direct) / AudioPhysics.SpeedOfSound * 1000f:F2} ms late, " +
                                  $"low {low:F2}, high {high:F2} ({20 * Math.Log10(low):F1} / {20 * Math.Log10(high):F1} dB), scaled {db:F0} dB");
                first = false;
            }

            Array.Copy(dry, f, md, 0, Frame);
            for (int i = 0; i < Frame; i++) mg[i] = g.Process(md[i]) - md[i];
            if (!fromBelow) { for (int i = 0; i < Frame; i++) md[i] += mg[i]; Array.Clear(mg); }

            Apply(ctx, effD, hrtf, md, Dir(mouth - ear), ref inD, ref outD, sd);
            Apply(ctx, effG, hrtf, mg, Dir(image - ear), ref inG, ref outG, sg);
            for (int i = 0; i < Frame * 2; i++) y[f * 2 + i] = sd[i] + sg[i];
        }
        Phonon.iplAudioBufferFree(ctx, ref inD); Phonon.iplAudioBufferFree(ctx, ref outD);
        Phonon.iplAudioBufferFree(ctx, ref inG); Phonon.iplAudioBufferFree(ctx, ref outG);
        Phonon.iplBinauralEffectRelease(ref effD); Phonon.iplBinauralEffectRelease(ref effG);
        Phonon.iplHRTFRelease(ref hrtf); Phonon.iplContextRelease(ref ctx);
        return y;
    }

    static void Apply(IntPtr ctx, IntPtr eff, IntPtr hrtf, float[] mono, Phonon.IPLVector3 dir,
                      ref Phonon.IPLAudioBuffer inBuf, ref Phonon.IPLAudioBuffer outBuf, float[] stereo)
    {
        Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
        var prm = new Phonon.IPLBinauralEffectParams { direction = dir, interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = hrtf, peakDelays = IntPtr.Zero };
        Phonon.iplBinauralEffectApply(eff, ref prm, ref inBuf, ref outBuf);
        Phonon.iplAudioBufferInterleave(ctx, ref outBuf, stereo);
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

    static void Write(string path, float[] stereo)
    {
        string raw = Path.GetTempFileName();
        var b = new byte[stereo.Length * 4];
        Buffer.BlockCopy(stereo, 0, b, 0, b.Length);
        File.WriteAllBytes(raw, b);
        Process.Start(new ProcessStartInfo("sox", $"-t f32 -r {Rate} -c 2 \"{raw}\" -b 16 \"{path}\" gain -3") { UseShellExecute = false })!.WaitForExit();
        File.Delete(raw);
    }
}
