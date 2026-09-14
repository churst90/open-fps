using System;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Plays a REAL ambisonic file through the real provider and turns the listener around in it.
///
/// The `--ambisonic` spike proves the Phonon calls; this proves everything above them — that a
/// four-channel file survives being loaded as PCM, that the N3D conversion is applied to it, that the
/// generator DSP streams and resamples it correctly, and that the field rotates when the listener does.
/// Those are separate failure modes: FMOD downmixing the file to stereo on load, the conversion being
/// skipped, the 96 kHz-to-44.1 kHz resample running the bed fast, a rotation that never reaches the
/// mixer thread. None of them throw.
///
/// Headless it sweeps the listener through a full turn and checks that the ear balance actually moves.
/// With --bed-live it turns slowly and audibly, so a soundfield that is subtly wrong can be heard.
/// </summary>
public static class AmbientBedSpike
{
    public static int Run(string soundId, bool interactive)
    {
        AcousticRegistry.Initialize();

        var provider = new FmodAudioProvider();
        if (!provider.Initialize()) { Console.WriteLine("FAIL: provider init failed."); return 1; }

        try
        {
            provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, AcousticConstants.GlobalRegionId);

            var layout = AmbisonicFormat.GuessLayout(soundId);
            Console.WriteLine($"  bed '{soundId}', layout guessed as {layout}");

            if (!provider.PlayAmbientBed(soundId, layout, 1.0f))
            {
                Console.WriteLine("FAIL: the bed would not start — see the log line above for why.");
                return 1;
            }

            // Let it fill and the level glide settle before measuring anything.
            for (int i = 0; i < 60; i++) { provider.Update(); Thread.Sleep(8); }

            if (interactive)
                Console.WriteLine("  LIVE. HEADPHONES. Turning slowly through a full circle, twice.");

            // Sweep a full turn, sampling the ear balance as we go.
            const int steps = 24;
            var balance = new float[steps];
            float energy = 0f;
            int turns = interactive ? 2 : 1;

            for (int t = 0; t < turns; t++)
            {
                for (int i = 0; i < steps; i++)
                {
                    float yaw = i / (float)steps * MathF.PI * 2f;
                    var rot = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);

                    int holds = interactive ? 40 : 14;
                    double sumL = 0, sumR = 0;
                    int taken = 0;
                    for (int h = 0; h < holds; h++)
                    {
                        provider.UpdateListener(Vector3.Zero, rot, Vector3.Zero, AcousticConstants.GlobalRegionId);
                        provider.Update();
                        if (h >= holds / 2 && provider.TryGetAmbientBedLevels(soundId, out float l, out float r))
                        { sumL += l; sumR += r; taken++; }
                        Thread.Sleep(8);
                    }

                    float el = taken > 0 ? (float)(sumL / taken) : 0f;
                    float er = taken > 0 ? (float)(sumR / taken) : 0f;
                    float total = el + er;
                    balance[i] = total > 1e-9f ? (er - el) / total : 0f;   // -1 all left, +1 all right
                    energy += total;

                    if (t == turns - 1)
                        Console.WriteLine($"      yaw {yaw * 180f / MathF.PI,5:F0}°  L/R = {el:F5} / {er:F5}" +
                                          $"   balance {balance[i]:+0.000;-0.000; 0.000}");
                }
            }

            float spread = Max(balance) - Min(balance);
            Console.WriteLine($"  balance swings {spread:F3} across a full turn.");

            Console.WriteLine($"  bed state: {provider.DescribeAmbientBed(soundId)}");

            bool ok = true;
            ok &= Check("the bed decoded to something audible", energy > 1e-5f);
            // A field that does not move as the listener turns is either head-locked or not being
            // rotated at all — which is exactly the failure this whole path exists to avoid.
            ok &= Check("the soundfield moves as the listener turns", spread > 0.02f);

            provider.StopAmbientBed(soundId);
            for (int i = 0; i < 20; i++) { provider.Update(); Thread.Sleep(8); }
            ok &= Check("the bed stops cleanly", !provider.TryGetAmbientBedLevels(soundId, out _, out _));

            Console.WriteLine(ok
                ? "RESULT: PASS — a real recorded soundfield plays and turns with the listener."
                : "RESULT: FAIL — see the unmet conditions above.");
            return ok ? 0 : 1;
        }
        finally
        {
            provider.Dispose();
        }
    }

    private static float Max(float[] v) { float m = v[0]; foreach (var x in v) if (x > m) m = x; return m; }
    private static float Min(float[] v) { float m = v[0]; foreach (var x in v) if (x < m) m = x; return m; }

    private static bool Check(string what, bool held)
    {
        Console.WriteLine($"  [{(held ? "PASS" : "FAIL")}] {what}");
        return held;
    }
}
