using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --traced-reverb: the traced outdoor reverb, measured headless. Three places — open ground, a
/// street canyon (two rows of brick buildings 20 m apart on asphalt), a closed concrete room — are
/// traced from the listener's position, an impulse is played through the reflection effect, and the
/// omni (W) channel of what comes back is metered: energy in 50 ms windows, the level of the whole
/// tail against the impulse, how long a trace takes. The street should hand back more than open
/// ground and show its crossing period (2 x 20 m / 343 = 117 ms); the room most of all.
/// </summary>
public static class TracedReverbSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("context failed"); return 1; }
        var q = Quaternion.Identity;
        var ground = new SteamAudioScene.Box(new Vector3(0, -0.25f, 0), new Vector3(600, 0.5f, 600), q, "Asphalt");

        var open = new List<SteamAudioScene.Box> { ground };
        var street = new List<SteamAudioScene.Box> { ground };
        for (int i = 0; i < 8; i++)
        {
            float z = -70f + i * 20f;
            street.Add(new(new Vector3(-15f, 9f, z), new Vector3(10f, 18f, 20f), q, "Brick"));
            street.Add(new(new Vector3(15f, 9f, z), new Vector3(10f, 18f, 20f), q, "Brick"));
        }
        var room = new List<SteamAudioScene.Box>
        {
            ground,
            new(new Vector3(0, 1.5f, -6), new Vector3(12, 3, 0.3f), q, "Concrete"), new(new Vector3(0, 1.5f, 6), new Vector3(12, 3, 0.3f), q, "Concrete"),
            new(new Vector3(-6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, "Concrete"), new(new Vector3(6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, "Concrete"),
            new(new Vector3(0, 3.15f, 0), new Vector3(12, 0.3f, 12), q, "Concrete"),
        };

        List<SteamAudioScene.Box> Room(string walls, string floor) => new()
        {
            new(new Vector3(0, -0.25f, 0), new Vector3(600, 0.5f, 600), q, floor),
            new(new Vector3(0, 1.5f, -6), new Vector3(12, 3, 0.3f), q, walls), new(new Vector3(0, 1.5f, 6), new Vector3(12, 3, 0.3f), q, walls),
            new(new Vector3(-6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, walls), new(new Vector3(6, 1.5f, 0), new Vector3(0.3f, 3, 12), q, walls),
            new(new Vector3(0, 3.15f, 0), new Vector3(12, 0.3f, 12), q, walls),
        };
        List<SteamAudioScene.Box> Cabin(string preset)
        {
            var v = MachineRegistry.VehicleFor(preset);
            var g = VehicleCabin.Measure(v)!.Value;
            var b = new List<SteamAudioScene.Box>();
            foreach (var (prefab, at, size) in VehicleCabin.Shell(v, g))
                b.Add(new(at, size, q, VehicleCabin.MaterialOf(prefab)));
            return b;
        }
        Vector3 Seat(string preset)
        {
            var g = VehicleCabin.Measure(MachineRegistry.VehicleFor(preset))!.Value;
            return new Vector3(0.3f, g.FloorTop + 1.1f, g.Cz);
        }
        var places = new List<(string, List<SteamAudioScene.Box>, Vector3)>
        {
            ("open ground", open, new Vector3(2f, 1.6f, 0f)), ("street, 20 m", street, new Vector3(2f, 1.6f, 0f)),
            ("concrete room", room, new Vector3(2f, 1.6f, 0f)), ("tiled room", Room("Tile", "Tile"), new Vector3(2f, 1.6f, 0f)),
            ("wood, carpet", Room("Wood", "Carpet"), new Vector3(2f, 1.6f, 0f)),
            ("bus cabin", Cabin("school_bus_na"), Seat("school_bus_na")), ("hatchback cabin", Cabin("i4_economy"), Seat("i4_economy")),
        };
        foreach (var (name, boxes, ear) in places)
        {
            using var scene = new SteamAudioScene(ctx);
            scene.Build(boxes);
            using var tr = new TracedReverb(ctx);
            tr.SetScene(scene);
            tr.SetListener(ear);
            var until = DateTime.UtcNow.AddSeconds(20);
            while (tr.Runs < 2 && DateTime.UtcNow < until) Thread.Sleep(50);
            if (!tr.TryGetParams(out var prm)) { Console.WriteLine($"{name}: no IR"); continue; }

            var au = new Phonon.IPLAudioSettings { samplingRate = 44100, frameSize = 1024 };
            var es = new Phonon.IPLReflectionEffectSettings { type = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION, irSize = tr.IrSize, numChannels = TracedReverb.Channels };
            Phonon.iplReflectionEffectCreate(ctx, ref au, ref es, out IntPtr effect);
            var inBuf = new Phonon.IPLAudioBuffer(); var outBuf = new Phonon.IPLAudioBuffer();
            Phonon.iplAudioBufferAllocate(ctx, 1, 1024, ref inBuf);
            Phonon.iplAudioBufferAllocate(ctx, TracedReverb.Channels, 1024, ref outBuf);
            var mono = new float[1024];
            var inter = new float[1024 * TracedReverb.Channels];
            var w = new List<float>();
            for (int b = 0; b < 44100 * 3 / 1024; b++)
            {
                Array.Clear(mono);
                if (b == 0) mono[0] = 1f;
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
                Phonon.iplAudioBufferInterleave(ctx, ref outBuf, inter);
                for (int k = 0; k < 1024; k++) w.Add(inter[k * TracedReverb.Channels]);
            }
            // What one stage costs the mixer: the effect applied to live-ish input, per block.
            var rngc = new Random(1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int b = 0; b < 200; b++)
            {
                for (int k = 0; k < 1024; k++) mono[k] = (float)(rngc.NextDouble() * 2 - 1) * 0.1f;
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
            }
            double blockMs = sw.Elapsed.TotalMilliseconds / 200;
            // ...and with the trace refreshing underneath, as it does in the game: the worst block.
            double worst = 0; int runs0 = tr.Runs;
            for (int b = 0; b < 60; b++)
            {
                Thread.Sleep(23);
                tr.TryGetParams(out prm);
                for (int k = 0; k < 1024; k++) mono[k] = (float)(rngc.NextDouble() * 2 - 1) * 0.1f;
                Phonon.iplAudioBufferDeinterleave(ctx, mono, ref inBuf);
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Phonon.iplReflectionEffectApply(effect, ref prm, ref inBuf, ref outBuf, IntPtr.Zero);
                worst = Math.Max(worst, (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
            Console.WriteLine($"   one stage: {blockMs:F2} ms per block steady, worst {worst:F2} ms with {tr.Runs - runs0} re-traces underneath (deadline 23 ms)");
            double total = 0; foreach (var x in w) total += x * (double)x;
            var windows = new List<string>();
            for (int k = 0; k < 16; k++)
            {
                double e = 0; int a = k * 2205;
                for (int j = a; j < a + 2205 && j < w.Count; j++) e += w[j] * (double)w[j];
                windows.Add($"{10 * Math.Log10(e + 1e-20):F0}");
            }
            // Where the energy has fallen 20 dB from its peak window, as a rough decay.
            Console.WriteLine($"{name,-14} tail {10 * Math.Log10(total + 1e-20),6:F1} dB re impulse, trace {tr.LastRunMs,5:F0} ms");
            Console.WriteLine($"   50 ms windows (dB): {string.Join(" ", windows)}");
            Phonon.iplAudioBufferFree(ctx, ref inBuf); Phonon.iplAudioBufferFree(ctx, ref outBuf);
            Phonon.iplReflectionEffectRelease(ref effect);
        }
        Phonon.iplContextRelease(ref ctx);
        return 0;
    }
}
