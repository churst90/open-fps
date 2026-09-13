using System;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The near-field boundary effect through the REAL provider: a steady source, and a wall that closes in
/// on the listener and retreats again.
///
/// What you are listening for is not "loudness near a wall". It is the comb: the wall returns a copy of
/// everything delayed by 2d/c, and direct-plus-delayed cancels at odd multiples of c/4d. Close in, the
/// notches are high and wide apart and the sound goes hollow and boxy; back off and the pattern slides
/// down and thins out into the reverb. The spike prints where the first notch should be at each step, so
/// what you hear can be checked against what the geometry says.
///
/// Headless it is a crash-and-level check. With --boundary-live it is a listening test: the wall passes
/// from your right, to in front, to your left, so lateralization can be judged too.
/// </summary>
public static class BoundarySpike
{
    public static int Run(bool interactive)
    {
        AcousticRegistry.Initialize();

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }

        try
        {
            provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);

            // Broadband, continuous, and dead centre — a comb filter is only audible on something that
            // has energy at the frequencies it notches out.
            var source = new SpatialEmitter
            {
                EntityId = 1,
                Type = EmitterType.WorldLocked,
                IsSynth = true,
                SynthWave = SynthWaveType.Noise,
                SynthFrequency = 200f,
                SynthFilterCutoff = 1.0f,
                Volume = 0.5f,
                Position = new Vector3(0, 0, 4),
                Range = 100f,
                MinDistance = 4f
            };
            provider.PlaySpatialSound(source);

            if (interactive)
                Console.WriteLine("LIVE. HEADPHONES. A concrete wall closes in from the RIGHT, swings to the FRONT,\n" +
                                  "then to the LEFT, and retreats. Listen for the hollow colouration rising in pitch\n" +
                                  "as it nears, and for which ear it sits in.");

            bool ok = true;
            ok &= Sweep(provider, source, Vector3.UnitX, "from the right", interactive);
            ok &= Sweep(provider, source, Vector3.UnitZ, "from the front", interactive);
            ok &= Sweep(provider, source, -Vector3.UnitX, "from the left", interactive);

            // Open air: nothing within range, so the effect must retire completely.
            var far = new[] { new BoundaryProbe(Vector3.UnitX, BoundaryModel.MaxDistance + 1f, "Concrete") };
            for (int i = 0; i < 120; i++) { provider.UpdateBoundaries(far); provider.Update(); Thread.Sleep(5); }
            float idle = provider.BoundaryReflectionLevel;
            Console.WriteLine($"  open air                        reflection level = {idle:F4}");
            ok &= Check("with nothing nearby the effect retires completely", idle < 0.001f);

            Console.WriteLine(ok
                ? "RESULT: PASS — a nearby surface colours the mix, and the colour tracks the geometry."
                : "RESULT: FAIL — see the unmet conditions above.");
            return ok ? 0 : 1;
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static bool Sweep(FmodAudioProvider provider, SpatialEmitter source, Vector3 headDirection,
                              string label, bool interactive)
    {
        Console.WriteLine($"  --- wall {label} ---");
        float nearLevel = 0f, farLevel = 0f;

        float[] distances = { 2.5f, 1.8f, 1.2f, 0.8f, 0.5f, 0.3f, 0.5f, 0.8f, 1.2f, 1.8f, 2.5f };
        var probes = new BoundaryProbe[1];

        for (int d = 0; d < distances.Length; d++)
        {
            float distance = distances[d];
            probes[0] = new BoundaryProbe(headDirection, distance, "Concrete");

            // Hold each distance long enough for the mixer's glide to settle and, live, to be heard.
            int holds = interactive ? 90 : 30;
            for (int i = 0; i < holds; i++)
            {
                provider.UpdateBoundaries(probes);
                provider.UpdateSpatialAttributes(source);
                provider.Update();
                Thread.Sleep(interactive ? 8 : 4);
            }

            float level = provider.BoundaryReflectionLevel;
            Console.WriteLine($"      {distance,4:F1} m  reflection={level:F3}  " +
                              $"round trip={2f * distance / AudioPhysics.SpeedOfSound * 1000f,5:F1} ms  " +
                              $"first notch={BoundaryModel.FirstNotchHz(distance),6:F0} Hz");

            if (Math.Abs(distance - 0.3f) < 0.01f) nearLevel = level;
            if (Math.Abs(distance - 2.5f) < 0.01f) farLevel = level;
        }

        return Check($"the wall {label} gets louder as it closes in", nearLevel > farLevel * 2f);
    }

    private static bool Check(string what, bool held)
    {
        Console.WriteLine($"  [{(held ? "PASS" : "FAIL")}] {what}");
        return held;
    }
}
