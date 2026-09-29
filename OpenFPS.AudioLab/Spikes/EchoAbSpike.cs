using System;
using System.IO;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// A shot and one wall echo, as the game renders the echo now (the whole of it through the diffuser)
/// and as the physical split would (the specular share a clean copy, the scattered share smeared).
/// `--echo-ab out=DIR`.
/// </summary>
public static class EchoAbSpike
{
    public static int Run(string[] args)
    {
        string dir = Array.Find(args, a => a.StartsWith("out="))?[4..] ?? ".";
        Directory.CreateDirectory(dir);
        const int sr = TransientSynth.SampleRate;
        var materials = new[] { ("metal", 0.10f), ("concrete", 0.10f), ("brick", 0.45f) };
        foreach (var gunId in new[] { "akm", "pistol" })
        {
            if (!WeaponRegistry.TryGet(gunId, out var gun)) continue;
            var shot = WeaponSynth.MuzzleBlast(WeaponProfile.From(gun), 3);
            float peak = 0f; foreach (var v in shot) peak = MathF.Max(peak, MathF.Abs(v));
            foreach (var (mat, s) in materials)
            {
                var wash = WorldAudioPlayer.Diffuse(shot, s, 3);
                var split = new float[wash.Length];
                float clean = MathF.Sqrt(1f - s), rough = MathF.Sqrt(s);
                for (int i = 0; i < split.Length; i++)
                    split[i] = (i < shot.Length ? clean * shot[i] : 0f) + rough * wash[i];
                Write(Path.Combine(dir, $"{gunId}-{mat}-A-wash-as-now.wav"), shot, wash, peak, sr);
                Write(Path.Combine(dir, $"{gunId}-{mat}-B-crack-plus-scatter.wav"), shot, split, peak, sr);
            }
        }
        Console.WriteLine($"  written to {dir}");
        return 0;
    }

    /// <summary>The shot, then the echo 60 ms later (a wall ten metres off) at -8 dB, then a second of quiet.</summary>
    private static void Write(string path, float[] shot, float[] echo, float peak, int sr)
    {
        int at = (int)(0.060f * sr);
        var y = new float[Math.Max(shot.Length, at + echo.Length) + sr];
        float g = 0.9f / MathF.Max(1e-6f, peak), e = MathF.Pow(10f, -8f / 20f);
        for (int i = 0; i < shot.Length; i++) y[i] += shot[i] * g;
        for (int i = 0; i < echo.Length; i++) y[at + i] += echo[i] * g * e;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF".ToCharArray()); w.Write(36 + y.Length * 2); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(y.Length * 2);
        foreach (float x in y) w.Write((short)Math.Clamp(x * 32767f, -32768f, 32767f));
    }
}
