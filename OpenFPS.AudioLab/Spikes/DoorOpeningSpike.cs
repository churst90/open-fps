using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Doors opening and shutting at a metre, before and after the leaf answers its latch.
///
///   --door-opening [out=DIR]
///
/// Each file is the door opened, 1.2 s, then shut. "before" is the opening without the leaf's
/// answer to the bolt and without its ring, which is what opening was until the leaf's mass and
/// material were used; "after" is as it is now. A door's two files share one gain, so before
/// against after is real; door against door is not.
/// </summary>
public static class DoorOpeningSpike
{
    private const int Sr = TransientSynth.SampleRate;

    private sealed record Leaf(string Name, string Material, float Width, float Height, float RingThickness,
                               float MassKg, bool Seal);

    public static int Run(string[] args)
    {
        string dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4)
                     ?? "/home/cody/external-rescue/Github/open-fps/inbox/doors-2026-09-24";
        Directory.CreateDirectory(dir);
        // The hollow steel door as DoorSystem reckons one: 1.2 mm skins over a 45 mm core.
        const float skin = 0.0012f, depth = 0.045f, rho = 7850f;
        float perArea = 2f * skin * rho + (depth - 2f * skin) * 150f;
        var leaves = new[]
        {
            new Leaf("wood_door", "Wood", 0.9f, 2.1f, 0.04f, 0.9f * 2.1f * 0.04f * 650f, false),
            new Leaf("steel_door", "Metal", 0.9f, 2.1f, MathF.Sqrt(6f * rho * skin * (depth - skin) * (depth - skin) / perArea), 0.9f * 2.1f * perArea, true),
            new Leaf("car_door", "Metal", 1.0f, 1.1f, 0.0008f, 22f, true),
        };
        foreach (var leaf in leaves)
        {
            var m = AcousticRegistry.GetProperties(leaf.Material);
            var opening = DoorAcoustics.Opening(m, Vector3.Zero, Vector3.One, leaf.Width, leaf.Height, leaf.RingThickness,
                                                leaf.MassKg, 0.9f, 0f, leaf.Seal);
            var before = opening.Where(s => s.Kind != DoorSoundKind.Panel && !(s.Kind == DoorSoundKind.Latch && s.Hz < 1000f)).ToList();
            var closing = DoorAcoustics.Closing(m, Vector3.Zero, Vector3.Zero, leaf.Width, leaf.Height, leaf.RingThickness,
                                                leaf.MassKg, DoorAcoustics.EdgeSpeed(leaf.Width, MathF.PI / 2f, 0.9f), leaf.Seal);
            Console.WriteLine($"  {leaf.Name}: {leaf.MassKg:F0} kg");
            foreach (var s in opening)
                Console.WriteLine($"    {(before.Contains(s) ? "   " : "new")} {s.Kind,-6} {s.Character,-6} {s.LevelDb,5:F1} dB {s.Hz,6:F0} Hz {s.DecaySeconds * 1000f,5:F0} ms");
            var a = Mix(before, closing, 1);
            var b = Mix(opening, closing, 1);
            float peak = a.Concat(b).Select(MathF.Abs).Max();
            Write(Path.Combine(dir, $"{leaf.Name}_before.wav"), a, 0.89f / peak);
            Write(Path.Combine(dir, $"{leaf.Name}_after.wav"), b, 0.89f / peak);
            Console.WriteLine($"    opening in 0-150 ms: before {Db(a, 0.3f, 0.45f):F1} dB, after {Db(b, 0.3f, 0.45f):F1} dB re the same gain; below 200 Hz: "
                              + $"{LowShare(a, 0.3f, 0.45f) * 100f:F0}% -> {LowShare(b, 0.3f, 0.45f) * 100f:F0}%\n");
        }
        return 0;
    }

    /// <summary>Opening at 0.3 s and shutting 1.2 s later, in pascals at a metre.</summary>
    private static float[] Mix(List<DoorSound> opening, List<DoorSound> closing, int seed)
    {
        var buf = new float[(int)(3.5f * Sr)];
        void Add(IEnumerable<DoorSound> sounds, float at)
        {
            foreach (var s in sounds)
            {
                var pcm = TransientSynth.Render(s.ToTransient(), seed++);
                float g = 20e-6f * MathF.Pow(10f, s.LevelDb / 20f);
                int start = (int)((at + s.DelaySeconds) * Sr);
                for (int i = 0; i < pcm.Length && start + i < buf.Length; i++) buf[start + i] += pcm[i] * g;
            }
        }
        Add(opening, 0.3f);
        Add(closing, 1.5f);
        return buf;
    }

    private static float Db(float[] x, float from, float to)
    {
        int a = (int)(from * Sr), b = (int)(to * Sr);
        double e = 0; for (int i = a; i < b; i++) e += x[i] * (double)x[i];
        return 10f * MathF.Log10((float)(e / (b - a)) / (20e-6f * 20e-6f) + 1e-12f);
    }

    private static float LowShare(float[] x, float from, float to)
    {
        int a = (int)(from * Sr), b = (int)(to * Sr);
        float alpha = 1f - MathF.Exp(-2f * MathF.PI * 200f / Sr), lp = 0f;
        double low = 0, all = 0;
        for (int i = a; i < b; i++) { lp += alpha * (x[i] - lp); low += lp * (double)lp; all += x[i] * (double)x[i]; }
        return (float)(low / Math.Max(1e-30, all));
    }

    private static void Write(string path, float[] x, float gain)
    {
        var w = new float[x.Length];
        for (int i = 0; i < w.Length; i++) w[i] = x[i] * gain;
        File.WriteAllBytes(path, WeaponSynth.ToWav16(w, Sr));
        Console.WriteLine($"    wrote {path}");
    }
}
