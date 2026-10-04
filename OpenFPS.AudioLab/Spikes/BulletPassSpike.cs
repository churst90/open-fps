using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// A round going past a listener, as the game sends it and the client renders it, written out.
///
///   --bullet-pass [out=DIR]
///
/// Each scene flies the round in still standard air, asks <see cref="BulletFlyby.Sounds"/> what a
/// listener at a point hears (the server's own call), renders each sound from its key as the client
/// does, and lays it down at the moment it arrives: its delay from the shot plus its own distance over
/// the speed of sound, which is what the engine adds. The report is laid down beside it, from the
/// muzzle. Every sound is placed by the game's own law at the shipped compression (Loudness.Place,
/// then the inverse law past its reference distance), and every file is at that one shared gain, the
/// game's full scale. Dry and mono: no HRTF, air absorption, reflections or reverb, which the game
/// adds on the way.
/// </summary>
public static class BulletPassSpike
{
    private const int Sr = TransientSynth.SampleRate;

    public static int Run(string[] args)
    {
        string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..]
                     ?? LabPaths.InRepo("inbox", "bullets-2026-10-04");
        Directory.CreateDirectory(dir);
        float d30 = 30f * MathF.PI / 180f;
        var scenes = new List<(string File, WeaponDefinition W, Vector3 Ear, string What)>
        {
            ("1-m700-200m-downrange-5m-off.wav", WeaponRegistry.M700, new(5f, 0f, 200f), ".308 passing 5 m away, 200 m from the shooter: crack, then the report 0.3 s later"),
            ("2-m700-60m-downrange-1m-off.wav", WeaponRegistry.M700, new(1f, 0f, 60f), ".308 passing a metre away, 60 m from the shooter"),
            ("3-m700-300m-downrange-25m-off.wav", WeaponRegistry.M700, new(25f, 0f, 300f), ".308 passing 25 m away, 300 m out"),
            ("4-ar15-40m-30deg.wav", WeaponRegistry.Ar15, new(40f * MathF.Sin(d30), 0f, 40f * MathF.Cos(d30)), "5.56, the NIJ position: 40 m, 30 degrees off the line"),
            ("5-akm-40m-30deg.wav", WeaponRegistry.Akm, new(40f * MathF.Sin(d30), 0f, 40f * MathF.Cos(d30)), "7.62x39, the NIJ position"),
            ("6-akm-100m-downrange-3m-off.wav", WeaponRegistry.Akm, new(3f, 0f, 100f), "7.62x39 passing 3 m away, 100 m out"),
            ("7-glock-25m-downrange-1m-off.wav", WeaponRegistry.Glock, new(1f, 0f, 25f), "9 mm, Mach 1.09: a small crack"),
            ("8-pistol45-30m-downrange-2m-off.wav", WeaponRegistry.ServicePistol, new(2f, 0f, 30f), ".45, subsonic: the whizz going by 2 m away, then the report"),
            ("9-pistol45-30m-downrange-6m-off.wav", WeaponRegistry.ServicePistol, new(6f, 0f, 30f), ".45 passing 6 m away"),
            ("10-pistol45-15m-downrange-1m-off.wav", WeaponRegistry.ServicePistol, new(1f, 0f, 15f), ".45 passing a metre away, 15 m out"),
        };

        var air = Air.Standard;
        float c = air.SpeedOfSound;
        var muzzle = new Vector3(0f, 0f, 0.5f);
        var readme = new List<string>
        {
            "# Bullets going by, 2026-10-04",
            "",
            "Rendered by `OpenFPS.AudioLab --bullet-pass`. Each file is one shot heard by somebody standing off",
            "the line of fire: the crack of a supersonic round's shock wave (an N-wave, Whitham's law) or the",
            "whizz of a subsonic one's wake, then the report from the muzzle. All at ONE shared gain: the game's",
            "own full scale, each sound placed by the game's loudness law at the shipped compression and spread",
            "by distance. Dry and mono: the game adds direction, air, reflections and reverb.",
            "",
            "Shooter at the origin firing level along +Z; the listener's ear at the height of the line.",
            "",
            "| file | what | sounds (key, declared dB at 1 m, arrives) | peak dBFS |",
            "|---|---|---|---|",
        };
        Console.WriteLine($"Writing to {dir}\n");
        foreach (var (file, w, ear, what) in scenes)
        {
            var path = Flown(w, air);
            var sounds = BulletFlyby.Sounds(path, ear, w, air);
            var laid = new List<(float At, float[] Pcm, float Gain)>();
            var parts = new List<string>();
            foreach (var s in sounds)
            {
                float r = Vector3.Distance(ear, s.Position);
                float at = s.DelaySeconds + r / c;
                float[] pcm = BulletFlyby.TryParseCrack(s.SynthKey, out float T) ? BulletFlyby.RenderCrack(T, Sr)
                            : BulletFlyby.TryParseWhizz(s.SynthKey, out var wz) ? BulletFlyby.RenderWhizz(wz, Sr, 1)
                            : Array.Empty<float>();
                laid.Add((at, pcm, Placed(s.LevelDb, r)));
                parts.Add($"{s.SynthKey} {s.LevelDb:F0} dB +{at * 1000f:F1} ms");
            }
            float reportR = Vector3.Distance(ear, muzzle);
            laid.Add((reportR / c, WeaponSynth.MuzzleBlast(WeaponProfile.From(w), 1), Placed(Loudness.MuzzleBlastDb(w), reportR)));
            parts.Add($"report {Loudness.MuzzleBlastDb(w):F0} dB +{reportR / c * 1000f:F1} ms");

            float end = laid.Max(l => l.At + l.Pcm.Length / (float)Sr) + 0.3f;
            var mix = new float[(int)(end * Sr)];
            foreach (var (at, pcm, gain) in laid)
            {
                int o = (int)MathF.Round((at + 0.2f) * Sr);
                for (int i = 0; i < pcm.Length && o + i < mix.Length; i++) mix[o + i] += pcm[i] * gain;
            }
            WriteWav(Path.Combine(dir, file), mix);
            float peak = mix.Max(MathF.Abs);
            Console.WriteLine($"  {file,-40} {20f * MathF.Log10(MathF.Max(1e-9f, peak)),6:F1} dBFS  {string.Join("; ", parts)}");
            readme.Add($"| {file} | {what} | {string.Join("; ", parts)} | {20f * MathF.Log10(MathF.Max(1e-9f, peak)):F1} |");
        }
        readme.Add("");
        readme.Add("Each file starts 0.2 s before the shot. Arrival times are from the trigger.");
        File.WriteAllLines(Path.Combine(dir, "README.md"), readme);
        return 0;
    }

    /// <summary>The game's gain for a sound of this declared level heard this far off: its placement,
    /// then the engine's inverse law beyond the reference distance.</summary>
    private static float Placed(float levelDb, float distance)
    {
        var (gain, reference) = Loudness.Place(levelDb);
        return gain * MathF.Min(1f, reference / MathF.Max(0.01f, distance));
    }

    private static List<FlightSample> Flown(WeaponDefinition w, Air air)
    {
        var s = new BulletState { Position = new Vector3(0f, 0f, 0.5f), Velocity = Vector3.UnitZ * w.MuzzleVelocity };
        var path = new List<FlightSample> { new(0f, s.Position, s.Velocity) };
        float bc = ExternalBallistics.CoefficientOf(w);
        while (s.Seconds < 3f)
        {
            ExternalBallistics.Advance(ref s, 0.01f, bc, air, Vector3.Zero);
            path.Add(new FlightSample(s.Seconds, s.Position, s.Velocity));
        }
        return path;
    }

    private static void WriteWav(string path, float[] samples)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int bytes = samples.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Sr); w.Write(Sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(bytes);
        foreach (float v in samples) w.Write((short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue));
    }
}
