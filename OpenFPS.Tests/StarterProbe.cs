using System;
using System.Collections.Generic;
using System.IO;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Key-on renders as the game plays them (the level lift on): crank speed, bands while cranking and
/// at idle, from the kerb and from the seat at three corners of the starter's path. Set
/// STARTER_WAV_DIR to write the renders, each car's files at one gain.
/// </summary>
public class StarterProbe
{
    private readonly ITestOutputHelper _out;
    public StarterProbe(ITestOutputHelper o) => _out = o;
    const int Rate = 44100, Block = 441;

    public static string Split = "";
    static double LoadSum, RpmSum; static int LoadN;
    static (List<float> Wav, double CrankBand, double IdleBand, double CrankAll, float Rpm) Render(VehicleProfile v, bool inside, float corner = 150f, float starter = 1f)
    {
        var voice = new EngineVoiceState(v, Rate, 3) { TargetSpeed = 0f, Running = false, Interior = inside, CompensateLevel = true, StarterPathCornerHz = corner };
        voice.Engine.StarterMix = starter;
        voice.PlaceAtSpeed(0f); voice.Revive();
        var buf = new float[Block];
        for (int b = 0; b < 300; b++) voice.Render(buf);
        voice.Running = true;
        var wav = new List<float>();
        float hpA = MathF.Exp(-2f * MathF.PI * 300f / Rate), lpA = 1f - MathF.Exp(-2f * MathF.PI * 2000f / Rate);
        float hp = 0, hpIn = 0, lp = 0, rpm = 0;
        double cb = 0, ib = 0, ca = 0; int nc = 0, ni = 0;
        for (int b = 0; b < 400; b++)
        {
            voice.Render(buf);
            wav.AddRange(buf);
            bool cr = voice.Engine.Starter && !voice.Engine.Firing;
            foreach (float x in buf)
            {
                hp = hpA * (hp + x - hpIn); hpIn = x; lp += (hp - lp) * lpA;
                if (cr) { cb += lp * (double)lp; ca += x * (double)x; nc++; }
                if (b >= 300) { ib += lp * (double)lp; ni++; }
            }
            if (cr) { rpm = voice.Engine.Rpm; LoadSum += voice.Engine.StarterLoadNow; RpmSum += voice.Engine.Rpm; LoadN++; }
        }
        double Db(double e, int n) => 10 * Math.Log10(e / Math.Max(1, n) + 1e-30);
        // Bands of the whole render: crank 0..0.6 s and idle 3..4 s.
        string Bands(int from, int to)
        {
            float a80 = 1f - MathF.Exp(-2f * MathF.PI * 80f / Rate), a300 = 1f - MathF.Exp(-2f * MathF.PI * 300f / Rate);
            float l80 = 0, l300 = 0; double s0 = 0, s1 = 0, s2 = 0;
            for (int i = 0; i < to; i++)
            {
                float x = wav[i]; l80 += (x - l80) * a80; l300 += (x - l300) * a300;
                if (i < from) continue;
                s0 += l80 * (double)l80; s1 += (l300 - l80) * (double)(l300 - l80); s2 += (x - l300) * (double)(x - l300);
            }
            int n = to - from;
            return $"<80 {Db(s0, n),6:F1} 80-300 {Db(s1, n),6:F1} >300 {Db(s2, n),6:F1}";
        }
        Split = $"crank {Bands(0, (int)(0.6f * Rate))} | idle {Bands(3 * Rate, 4 * Rate - 1)}";
        return (wav, Db(cb, nc), Db(ib, ni), Db(ca, nc), rpm);
    }

    static void Write(string path, List<float> wav, float gain)
    {
        using var w = new BinaryWriter(File.Create(path));
        int n = wav.Count;
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        foreach (float x in wav) w.Write((short)Math.Clamp(x * gain * 32767f, -32767f, 32767f));
    }

    [Fact]
    public void Probe()
    {
        string dir = Environment.GetEnvironmentVariable("STARTER_WAV_DIR") ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);
        foreach (var key in new[] { "i4_economy", "i4_midsize", "v8_muscle", "pickup_v8", "police_interceptor", "transit_bus" })
        {
            var v = VehicleProfile.ByName(key);
            LoadSum = RpmSum = 0; LoadN = 0;
            var kerb = Render(v, false);
            _out.WriteLine($"{key,-20} cranking mean load {LoadSum / Math.Max(1, LoadN):F2}, mean {RpmSum / Math.Max(1, LoadN):F0} rpm (declared {v.Engine.CrankingRpm})");
            float peak = 1e-9f; foreach (float x in kerb.Wav) peak = MathF.Max(peak, MathF.Abs(x));
            float gain = 0.5f / peak;
            var probeVoice = new EngineVoiceState(v, Rate, 3);
            double fs = 20 * Math.Log10(probeVoice.PascalsAtFullScale / 20e-6);
            _out.WriteLine($"{key,-20} full scale {fs:F1} dB SPL, declared {v.SourceLevelDb:F1}, {v.Engine.DisplacementLitres:F1} L, exhaust {v.Engine.Exhaust?.GetType().Name}");
            _out.WriteLine($"{key,-20} kerb        crank all {kerb.CrankAll,6:F1} band {kerb.CrankBand,6:F1}  idle band {kerb.IdleBand,6:F1}   {Split}");
            if (dir.Length > 0) Write(Path.Combine(dir, $"{key}-kerb.wav"), kerb.Wav, gain);
            if (dir.Length > 0)
            {
                Write(Path.Combine(dir, $"{key}-kerb-nostarter.wav"), Render(v, false, 150f, 0f).Wav, gain);
                Write(Path.Combine(dir, $"{key}-seat-150hz-nostarter.wav"), Render(v, true, 150f, 0f).Wav, gain);
            }
            foreach (float corner in new[] { 150f, 300f, 600f })
            {
                var seat = Render(v, true, corner);
                _out.WriteLine($"{key,-20} seat {corner,4:F0} Hz crank all {seat.CrankAll,6:F1} band {seat.CrankBand,6:F1}  idle band {seat.IdleBand,6:F1}   {Split}");
                if (dir.Length > 0) Write(Path.Combine(dir, $"{key}-seat-{corner:F0}hz.wav"), seat.Wav, gain);
            }
        }
    }
}
