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
}
