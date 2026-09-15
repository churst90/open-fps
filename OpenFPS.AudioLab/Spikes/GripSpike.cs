using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Thread = System.Threading.Thread;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Engine;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// What a tyre sounds like when it is asked for more than it has — and what a turbo does to an
/// engine. Both are properties of the VEHICLE, so both arrive with any vehicle anyone builds.
///
/// The tyre half is one continuum, not three sounds. A tyre rolling is broadband roar plus its tread
/// blocks going past. A tyre at the limit is a resonance: elements of tread grip, deflect, release
/// and snap back, thousands of them slightly out of step, and that is a NOTE — which is why a car at
/// the limit sings rather than merely getting louder, and why the note rises as it is worked harder.
/// Past the limit the contact patch slides continuously, the cycle stops being periodic and the note
/// collapses into the roar of a locked wheel. Chirp, squeal and skid are that one curve sampled at
/// three demands. Nothing triggers them.
/// </summary>
public static class GripSpike
{
    private static readonly Vector3 Ear = new(6.0f, 1.7f, 0f);

    /// <summary>
    /// Three demands on one set of tyres, rendered offline so you can hear the curve itself.
    ///
    /// The same car does a standing start (wheelspin off the line, a chirp at each upshift), a hard
    /// stop (weight forward, the fronts right on the limit and then past it), and a corner taken at
    /// rising lateral load until the tyres let go. The lateral case is fed in directly here because
    /// the bench drives in a straight line; in the game it comes from the car's own motion.
    /// </summary>
    public static int RunTyres(string[] presets)
    {
        AcousticRegistry.Initialize();
        if (presets.Length == 0) presets = new[] { "v8_muscle", "nascar_v8", "diesel_truck" };

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "VEHICLES");
        Directory.CreateDirectory(dir);

        Console.WriteLine("\n  TYRES. The same physics at three demands: a standing start, a lock-up, and a corner.");
        Console.WriteLine("  Grip and the note a tyre squeals at are properties of the TYRE, so they differ per car.\n");

        foreach (string key in presets)
        {
            var v = VehicleProfile.ByName(key);
            var t = v.Tyres;
            Console.WriteLine($"  {v.Name}");
            Console.WriteLine($"    {t.PeakGripG:F2} g of grip, squeals at {t.SquealHz:F0} Hz (Q {t.SquealQ:F0}), "
                            + $"{t.TreadBlocks} tread block(s), {t.ReferenceDb:F0} dB rolling / {t.SquealDb:F0} dB sliding");

            // The demand curve, printed. This is the whole model and it is worth seeing as numbers
            // before hearing it: nothing below 0.78 makes a sound, and nothing above 1.45 is a note.
            Console.Write("    demand ");
            for (float d = 0.6f; d <= 1.6f; d += 0.1f)
                Console.Write($"{d:F1}:{TyreFriction.SquealAmount(d):F2}/{TyreFriction.SkidAmount(d):F2}  ");
            Console.WriteLine("   (squeal/skid)");

            var render = VehicleSynth.Render(v, StandingStart(v), seed: 7);
            string path = Path.Combine(dir, $"grip_{key}_launch.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(Mix(render)));
            Console.WriteLine($"    launch + upshifts -> {Path.GetFileName(path)}  ({render.Seconds:F1} s)");

            // A corner: the tyres are handed a rising lateral load with the car at a steady speed.
            var corner = Corner(v);
            path = Path.Combine(dir, $"grip_{key}_corner.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(corner));
            Console.WriteLine($"    corner to the limit and past it -> {Path.GetFileName(path)}");
            Console.WriteLine();
        }
        Console.WriteLine($"  Written to {dir}");
        return 0;
    }

    /// <summary>A standing start: the launch itself spins the driven wheels, and every upshift puts a
    /// ratio step through them. Both come out of the driveline, neither is scripted.</summary>
    private static List<DriveOrder> StandingStart(VehicleProfile v) => new()
    {
        new DriveOrder { Action = DriverAction.Idling, Seconds = 2.0f },
        new DriveOrder { Action = DriverAction.Accelerating, Seconds = 9.0f, TargetSpeed = 45f },
        new DriveOrder { Action = DriverAction.Braking, Seconds = 4.5f, TargetSpeed = 0f },
    };

    /// <summary>
    /// A steady-speed corner whose radius tightens until the tyres give up.
    ///
    /// Rendered directly from the tyre model rather than through the driveline, because a bench with
    /// no steering cannot generate lateral load — but the number fed in is the same one the game
    /// computes from a car's actual motion, so what you hear here is what a corner sounds like there.
    /// </summary>
    private static float[] Corner(VehicleProfile v)
    {
        const int sr = VehicleSynth.SampleRate;
        int n = sr * 9;
        var buf = new float[n];
        var rng = new Random(3);
        var voice = default(VehicleSynth.TyreVoice);
        // Fast enough that THIS car's tyres run out at the tightest radius. Chosen from the grip
        // rather than fixed, because a stock car on slicks simply does not let go at the speed a road
        // car does — and a demo that fixes the speed only demonstrates the cars that are slow.
        float capacity = v.Tyres.PeakGripG * TyreFriction.G;
        float speed = MathF.Sqrt(1.55f * capacity * 34f);
        for (int i = 0; i < n; i++)
        {
            // The radius closes from something easy to something impossible, so the lateral
            // acceleration — v squared over r — climbs past what the tyres have.
            float u = i / (float)n;
            float radius = OpenFPS.Common.MathHelper.Lerp(260f, 34f, u);
            float aLat = speed * speed / radius;
            buf[i] = VehicleSynth.Tyre(v.Tyres, speed, TyreFriction.Demand(0f, aLat, v.Tyres.PeakGripG), rng, ref voice);
            if (i % sr == 0)
                Console.WriteLine($"      {i / sr,2:F0}s  {speed * 3.6f,3:F0} km/h  r {radius,5:F0} m   {aLat / TyreFriction.G,4:F2} g of {v.Tyres.PeakGripG:F2}"
                                + $"   demand {aLat / capacity:F2}");
        }
        Console.WriteLine($"       9s  {speed * 3.6f,3:F0} km/h  r    34 m   {speed * speed / 34f / TyreFriction.G,4:F2} g of {v.Tyres.PeakGripG:F2}"
                        + $"   demand {speed * speed / 34f / capacity:F2}   (sliding)");
        float peak = 0f;
        foreach (float s in buf) peak = MathF.Max(peak, MathF.Abs(s));
        if (peak > 1e-6f) for (int i = 0; i < n; i++) buf[i] *= 0.9f / peak;
        return buf;
    }

    private static float[] Mix(VehicleRender r)
    {
        var buf = new float[r.Exhaust.Length];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = MathF.Tanh(r.Exhaust[i] + r.Intake[i] * 0.4f + r.Tyres[i] * 1.6f);
        return buf;
    }

    /// <summary>
    /// The same 2.0 four with and without a turbocharger, then the truck that has always had one.
    ///
    /// Almost nothing about the turbo version is described separately. Compression comes down because
    /// you cannot run eleven-to-one on boost; the cam loses overlap because a boosted engine does not
    /// want reversion diluting a pressurised charge, and less overlap is a CLEANER idle; the turbine
    /// is a muffler, so the exhaust goes flat and woofly and the interesting noise moves to the intake
    /// side; and the torque arrives at three thousand instead of five and a half, so it is geared
    /// longer and spends a pull in fewer, taller gears. All of that is consequence, not styling.
    /// </summary>
    public static int RunTurbo(string[] presets)
    {
        AcousticRegistry.Initialize();
        if (presets.Length == 0) presets = new[] { "i4_sport", "i4_turbo", "diesel_truck" };

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "VEHICLES");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  TURBO. Idle, a pull through the gears, then off the throttle.\n");

        foreach (string key in presets)
        {
            var v = VehicleProfile.ByName(key);
            var e = v.Engine;
            Console.WriteLine($"  {v.Name} — {e.Induction}"
                            + (e.Induction == Induction.NaturallyAspirated ? "" : $", {e.BoostBar:F2} bar")
                            + $", peak torque {e.PeakTorqueNm:F0} Nm at {e.PeakTorqueRpm:F0}, redline {e.RedlineRpm:F0}");
            if (e.Mechanical.TurboWhistleLevel > 0f)
                Console.WriteLine($"    whistle {e.Mechanical.TurboWhistleLevel:F2}, shaft lag {e.Mechanical.TurboLagSeconds:F2} s "
                                + "— the spool is what you are listening for on the way up, and the hiss dying away on the lift.");

            var orders = new List<DriveOrder>
            {
                new DriveOrder { Action = DriverAction.Idling, Seconds = 2.5f },
                new DriveOrder { Action = DriverAction.Accelerating, Seconds = 11f, TargetSpeed = 50f },
                new DriveOrder { Action = DriverAction.Coasting, Seconds = 3.5f },
            };
            var render = VehicleSynth.Render(v, orders, seed: 5);
            string path = Path.Combine(dir, $"turbo_{key}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(Mix(render)));
            foreach (string line in render.Log) if (line.Contains("into ")) Console.WriteLine("    " + line);
            Console.WriteLine($"    -> {Path.GetFileName(path)}  ({render.Seconds:F1} s, exhaust {render.ExhaustDb:F0} dB at 1 m)\n");
        }
        Console.WriteLine($"  Written to {dir}");
        return 0;
    }
}
