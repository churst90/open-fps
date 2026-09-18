using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Aircraft;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Aircraft, heard from the ground.
///
///   --aircraft [preset ...] [alt=m] [speed=m/s] [offset=m] [sec=s] [lever=0..1] [descend=0..1]
///
/// Renders each aircraft flying a straight line past a listener standing on grass, and writes one
/// WAV per preset. The flyover is done the honest way: the machine is integrated in ITS time, and
/// each sample is deposited at the moment it ARRIVES — emission time plus the path over the speed of
/// sound — with the inverse-distance gain and the air's absorption for that path. Doppler is not
/// applied; it happens, because the path is shortening. A second arrival off the ground, a little
/// later and a little weaker, is what gives a flyover its slow comb.
/// </summary>
public static class AircraftSpike
{
    private const int Sr = VehicleSynth.SampleRate;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        float alt = Arg(args, "alt", -1f), speed = Arg(args, "speed", -1f), offset = Arg(args, "offset", 60f);
        float seconds = Arg(args, "sec", 18f), lever = Arg(args, "lever", -1f), descend = Arg(args, "descend", -1f);
        var presets = args.Where(a => AircraftProfile.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0) presets = AircraftProfile.Presets.Keys.ToList();

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "AIRCRAFT");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  Aircraft, flying past a listener on the ground.\n");

        foreach (var key in presets)
        {
            var p = AircraftProfile.ByName(key);
            // A sensible pass for each kind, unless told otherwise.
            float h = alt > 0 ? alt : p.Power switch
            {
                AircraftPower.Turbofan => 400f, AircraftPower.Turboprop => 300f,
                AircraftPower.Turboshaft => 90f, _ => 150f,
            };
            float v = speed > 0 ? speed : p.Power switch
            {
                AircraftPower.Turbofan => 95f, AircraftPower.Turboprop => 110f,
                AircraftPower.Turboshaft => 45f, _ => 50f,
            };
            float lv = lever >= 0 ? lever : (p.Power == AircraftPower.Turboshaft ? 0.8f : 1f);
            float ds = descend >= 0 ? descend : (p.Power == AircraftPower.Turboshaft ? 0.7f : 0f);

            var synth = new AircraftSynth(p, Sr, 5) { Lever = lv, Descending = ds };
            Console.WriteLine($"  {key}");
            foreach (var line in synth.Describe()) Console.WriteLine($"    {line}");
            Console.WriteLine($"    pass: {h:F0} m up, {offset:F0} m to the side, {v:F0} m/s ({v * 3.6f:F0} km/h), {seconds:F0} s");

            var (wav, peakDb, closestDb) = Flyover(synth, h, offset, v, seconds);
            string path = Path.Combine(dir, $"aircraft_{key}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(wav));
            Console.WriteLine($"    at the ear: {closestDb:F0} dB at the closest point, peak {peakDb:F0} dB; wrote {path}\n");
        }
        return 0;
    }

    /// <summary>
    /// The aircraft flies along +x at a height, offset to one side, passing abeam the listener
    /// half way through. Returns the received signal normalised for playback, plus the level at the
    /// ear when it was closest and the peak.
    /// </summary>
    private static (float[] Wav, float PeakDb, float ClosestDb) Flyover(AircraftSynth synth, float alt, float offset, float speed, float seconds)
    {
        const float c = 343f;
        int n = (int)(seconds * Sr);
        var ear = new Vector3(0f, 1.6f, 0f);
        var earImage = new Vector3(0f, -1.6f, 0f);            // the ground's mirror of the ear
        var outBuf = new float[n + Sr * 4];
        float half = seconds * 0.5f;

        // Warm the machine up out of earshot first: a piston engine has to crank and settle, a
        // turbine has to spool.
        int warm = (int)(3f * Sr);
        synth.SetListener(new Vector3(0f, -alt, -speed * half));
        for (int i = 0; i < warm; i++) synth.Step();

        float lp1 = 0f, lp2 = 0f, glp1 = 0f, glp2 = 0f;
        double closestP2 = 0; int closeCount = 0;
        float rMin = float.MaxValue;
        for (int k = 0; k < n; k++)
        {
            float t = k / (float)Sr;
            var pos = new Vector3((t - half) * speed, alt, offset);
            if ((k & 63) == 0)
            {
                // The listener in the aircraft's frame: forward is +x here, up +y, starboard -z.
                Vector3 d = ear - pos;
                synth.SetListener(new Vector3(-d.Z, d.Y, d.X));
            }
            synth.Step();
            float s = synth.Total;

            // Direct path.
            float r = Vector3.Distance(pos, ear);
            rMin = MathF.Min(rMin, r);
            float fc = AirCorner(r);
            float a = 1f - MathF.Exp(-2f * MathF.PI * fc / Sr);
            lp1 += a * (s - lp1); lp2 += a * (lp1 - lp2);
            float direct = lp2 / MathF.Max(1f, r);
            Deposit(outBuf, (t + r / c) * Sr, direct);
            if (MathF.Abs(t - half) < 0.5f) { closestP2 += direct * direct; closeCount++; }

            // Off the ground: grass keeps about 70 % of the pressure at grazing incidence and
            // rather less of the top.
            float rg = Vector3.Distance(pos, earImage);
            float fg = AirCorner(rg) * 0.6f;
            float ag = 1f - MathF.Exp(-2f * MathF.PI * fg / Sr);
            glp1 += ag * (s - glp1); glp2 += ag * (glp1 - glp2);
            Deposit(outBuf, (t + rg / c) * Sr, 0.7f * glp2 / MathF.Max(1f, rg));
        }

        float peak = 0f;
        foreach (var x in outBuf) peak = MathF.Max(peak, MathF.Abs(x));
        float peakDb = 20f * MathF.Log10(MathF.Max(1e-9f, peak) / 2e-5f);
        float closestDb = 10f * MathF.Log10((float)Math.Max(1e-20, closestP2 / Math.Max(1, closeCount)) / (2e-5f * 2e-5f));
        var wav = new float[n + Sr * 2];
        float g = peak > 1e-9f ? 0.89f / peak : 0f;
        for (int i = 0; i < wav.Length; i++) wav[i] = outBuf[i] * g;
        return (wav, peakDb, closestDb);
    }

    /// <summary>Where the air has taken 3 dB off, for a path of this length: about 4 kHz at a
    /// hundred metres, 2 kHz at three hundred, 1 kHz at a kilometre (20 C, 50 % humidity).</summary>
    private static float AirCorner(float r)
        => Math.Clamp(4000f * MathF.Pow(100f / MathF.Max(1f, r), 0.59f), 300f, 18000f);

    /// <summary>Adds a sample at a fractional position, split linearly across the two slots.</summary>
    private static void Deposit(float[] buf, float at, float v)
    {
        int i = (int)at;
        float f = at - i;
        if (i < 0 || i + 1 >= buf.Length) return;
        buf[i] += v * (1f - f);
        buf[i + 1] += v * f;
    }

    private static float Arg(string[] args, string key, float fallback)
    {
        foreach (var a in args)
        {
            int eq = a.IndexOf('=');
            if (eq > 0 && a[..eq] == key && float.TryParse(a[(eq + 1)..], out float v)) return v;
        }
        return fallback;
    }
}
