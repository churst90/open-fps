using System;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Drives the REAL FmodAudioProvider end-to-end: plays a synth-noise spatial emitter and orbits
/// its world position around the listener. This exercises the full integrated path — FMOD channel
/// + occlusion/EQ DSPs + the Steam Audio binaural DSP wired into the provider — not a parallel
/// harness. interactive=true runs until Q; false runs `seconds` headless for a crash/init check.
/// </summary>
public static class ProviderOrbit
{
    public static int Run(bool interactive, double seconds = 3.0)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);

        var emitter = new SpatialEmitter
        {
            EntityId = 1,
            Type = EmitterType.WorldLocked,
            IsSynth = true,
            SynthWave = SynthWaveType.Noise,
            SynthFrequency = 200f,
            SynthFilterCutoff = 1.0f,
            SynthFilterResonance = 0.0f,
            Volume = 0.6f,
            Position = new Vector3(0, 0, 3),
            Range = 100f,
            MinDistance = 3f
        };
        provider.PlaySpatialSound(emitter);

        bool running = true;
        if (interactive)
        {
            Console.WriteLine("LIVE orbit through FmodAudioProvider + Steam Audio. HEADPHONES. Q to quit.");
            Console.WriteLine("0-8s horizontal (front->right->back->left), 8-16s vertical (front->up->back->down), looping.");
            var kt = new Thread(() => { while (running) if (Console.ReadKey(true).Key == ConsoleKey.Q) running = false; }) { IsBackground = true };
            kt.Start();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (running)
        {
            double t = sw.Elapsed.TotalSeconds;
            if (!interactive && t >= seconds) break;

            double tc = t % 16.0;
            Vector3 pos;
            if (tc < 8.0) { double th = 2 * Math.PI * (tc / 4.0); pos = new Vector3((float)Math.Sin(th) * 3f, 0f, (float)Math.Cos(th) * 3f); }
            else { double ph = 2 * Math.PI * ((tc - 8.0) / 4.0); pos = new Vector3(0f, (float)Math.Sin(ph) * 3f, (float)Math.Cos(ph) * 3f); }

            emitter.Position = pos;
            provider.UpdateSpatialAttributes(emitter);
            provider.Update();
            Thread.Sleep(20);
        }

        provider.StopSound(1);
        provider.Dispose();
        if (!interactive) Console.WriteLine("RESULT: provider orbit ran to completion (see log for 'Steam Audio HRTF binaural enabled').");
        return 0;
    }

    /// <summary>
    /// Stress test: rapidly create and stop many transient spatial voices (footsteps + reflections)
    /// while the FMOD mixer thread runs the Steam Audio DSP callbacks — reproducing the native crash
    /// seen in-game when moving near walls (voice create/release churn racing the mixer callback).
    /// Headless: survives `seconds` and prints a count, or crashes (segfault / heap corruption).
    /// </summary>
    public static int RunChurn(double seconds = 12.0)
    {
        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
        provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);

        int id = 100000;
        int created = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rnd = new Random(12345);

        SpatialEmitter MakeVoice(int eid, bool reflection) => new SpatialEmitter
        {
            EntityId = eid,
            Type = EmitterType.WorldLocked,
            IsSynth = true, SynthWave = SynthWaveType.Noise, SynthFrequency = 180f + rnd.Next(400),
            SynthFilterCutoff = 1.0f, Volume = 0.25f,
            Position = new Vector3(rnd.Next(-6, 6), rnd.Next(-2, 2), 1 + rnd.Next(8)),
            Range = 40f, MinDistance = 1f, IsEvent = true, IsReflection = reflection,
        };

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            // Burst of new voices (some flagged as reflections, like the in-game wall bounces).
            for (int k = 0; k < 10; k++) { provider.PlaySpatialSound(MakeVoice(++id, k % 2 == 0)); created++; }
            provider.Update();
            Thread.Sleep(4);
            // Stop a batch of older voices to force release churn against the live mixer callbacks.
            for (int k = 0; k < 10; k++) provider.StopSound(id - 20 - k);
            provider.Update();
            Thread.Sleep(4);
            if (created % 200 == 0) Console.WriteLine($"  churned {created} voices ({sw.Elapsed.TotalSeconds:F1}s)...");
        }

        provider.Dispose();
        Console.WriteLine($"RESULT: SURVIVED — churned {created} transient Steam Audio voices in {seconds:F0}s without crashing.");
        return 0;
    }
}
