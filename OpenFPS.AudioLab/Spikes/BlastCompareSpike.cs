using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Plays each weapon's blast three ways — the recording alone, the recording with low-end
/// reinforcement, and pure synthesis — so the question "do we still need the synthesizer?" can be
/// settled by ear instead of by argument.
///
/// It is a fair test only because all three are levelled the same way and played through the same
/// spatial path at the same distance. Comparing a normalised render against an un-normalised one, or
/// a dry one against a reverberated one, would answer a different question.
/// </summary>
public static class BlastCompareSpike
{
    public static int Run(bool live, string? only)
    {
        AcousticRegistry.Initialize();
        string? assets = FindAssets();
        if (assets == null)
        {
            Console.WriteLine("  cannot find OpenFPS.Client/ASSETS/SOUNDS — run the ingest first");
            return 1;
        }

        string outDir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "WEAPONS", "_COMPARE");
        Directory.CreateDirectory(outDir);

        var weapons = WeaponRegistry.All
            .Where(w => only == null || string.Equals(w.Id, only, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (weapons.Count == 0) { Console.WriteLine($"  no weapon '{only}'"); return 1; }

        Console.WriteLine("  Three versions of each blast. Same level, same distance, same path.\n");
        Console.WriteLine($"  {"weapon",-9}{"recorded",10}{"+sub",9}{"synth only",12}   (length, ms)");

        foreach (var w in weapons)
        {
            var profile = WeaponProfile.From(w);
            float[] recorded = LoadTake(assets, w);

            var plain = recorded.Length > 0
                ? WeaponSynth.CompositeBlast(profile, recorded, subLevel: 0f)
                : Array.Empty<float>();
            var withSub = recorded.Length > 0
                ? WeaponSynth.CompositeBlast(profile, recorded)
                : Array.Empty<float>();
            var synth = WeaponSynth.MuzzleBlast(profile);

            Write(outDir, $"{w.Id}_1_recorded.wav", plain);
            Write(outDir, $"{w.Id}_2_recorded_plus_sub.wav", withSub);
            Write(outDir, $"{w.Id}_3_synth_only.wav", synth);

            static string Ms(float[] v) =>
                v.Length == 0 ? "none" : $"{v.Length / (float)WeaponSynth.SampleRate * 1000:F0}";
            Console.WriteLine($"  {w.Id,-9}{Ms(plain),10}{Ms(withSub),9}{Ms(synth),12}");
        }

        Console.WriteLine($"\n  wrote {outDir}");
        if (!live)
        {
            Console.WriteLine("  --blast-compare-live plays them in order, announcing each.");
            return 0;
        }

        return Play(outDir, weapons.Select(w => w.Id).ToList());
    }

    private static int Play(string dir, System.Collections.Generic.List<string> ids)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("  (live playback unavailable)"); return 1; }
        try
        {
            var ear = new Vector3(0f, 1.7f, 0f);
            provider.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);
            for (int i = 0; i < 20; i++) { provider.Update(); Thread.Sleep(8); }

            Console.WriteLine("\n  LIVE. HEADPHONES. Each weapon three times: recorded, recorded+sub, synth.\n");
            int voice = -60000;

            foreach (var id in ids)
            {
                foreach (var (suffix, label) in new[]
                {
                    ("1_recorded", "the recording alone"),
                    ("2_recorded_plus_sub", "recording + low-end reinforcement"),
                    ("3_synth_only", "pure synthesis"),
                })
                {
                    string path = Path.Combine(dir, $"{id}_{suffix}.wav");
                    if (!File.Exists(path)) continue;
                    Console.WriteLine($"    {id,-9} — {label}");
                    provider.PlaySpatialSound(new SpatialEmitter
                    {
                        EntityId = voice--,
                        SoundId = path,
                        Type = EmitterType.WorldLocked,
                        Mode = PlaybackMode.Single,
                        // Twelve metres out: far enough to be a gunshot rather than a click in your
                        // ear, close enough that distance is not doing the shaping.
                        Position = new Vector3(0f, 1.5f, 12f),
                        Volume = 1.0f,
                        Range = 400f,
                        MinDistance = 12f,
                        Pitch = 1.0f,
                        TargetRegionId = AcousticConstants.GlobalRegionId,
                        // Dry. The engine's room would be the same for all three and would only make
                        // them harder to tell apart.
                        EnableReverb = false,
                        IsEvent = true,
                    });
                    var until = DateTime.UtcNow.AddMilliseconds(1100);
                    while (DateTime.UtcNow < until) { provider.Update(); Thread.Sleep(4); }
                }
                Console.WriteLine();
            }
            return 0;
        }
        finally { provider.Dispose(); }
    }

    private static float[] LoadTake(string assets, WeaponDefinition w)
    {
        string dir = Path.Combine(assets, w.FiringFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) return Array.Empty<float>();
        var files = Directory.GetFiles(dir, "*.wav");
        if (files.Length == 0) return Array.Empty<float>();
        Array.Sort(files, StringComparer.Ordinal);
        int i = ((w.FiringTakeIndex % files.Length) + files.Length) % files.Length;
        return WeaponSynth.ReadWav16Mono(File.ReadAllBytes(files[i]));
    }

    private static void Write(string dir, string name, float[] pcm)
    {
        if (pcm.Length == 0) return;
        File.WriteAllBytes(Path.Combine(dir, name), WeaponSynth.ToWav16(pcm));
    }

    private static string? FindAssets()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string c = Path.Combine(d.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS");
            if (Directory.Exists(c)) return c;
        }
        return null;
    }
}
