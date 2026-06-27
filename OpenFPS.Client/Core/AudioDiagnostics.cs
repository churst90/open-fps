using System;
using System.Numerics;
using System.Threading;
using OpenFPS.Client.AudioEngine.Core;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// Step 1a diagnostic harness. Plays a single mono broadband source and orbits it around a
/// stationary listener so HRTF / 3D panning can be verified BY EAR, completely isolated from
/// the networking, simulation, and acoustics layers.
///
/// This is the "ground truth" test: if a sound cannot be localized correctly here, no amount
/// of reflection/portal/reverb work on top will help. Get this right first.
///
/// Run with:   dotnet run --project OpenFPS.Client -- --audio-test
///        or:  OpenFPS.Client.exe --audio-test
/// </summary>
public static class AudioDiagnostics
{
    /// <summary>
    /// Non-interactive smoke test: initializes the engine, runs the orbit for a fixed duration,
    /// then exits. Lets CI / a headless box confirm FMOD loads and initializes on this platform
    /// without needing a TTY or human ears. Returns 0 on success, 1 if the engine did not init.
    /// </summary>
    public static int RunSmokeTest(double seconds)
    {
        Console.WriteLine($"Smoke test: initializing audio engine, running {seconds:F0}s...");
        var facade = new AudioEngineFacade();
        facade.Initialize();

        if (!facade.IsInitialized)
        {
            Console.WriteLine("RESULT: FAILED — audio engine did not initialize (see log above for the FMOD error).");
            facade.Dispose();
            return 1;
        }

        facade.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
        facade.StartDiagnosticSound();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        double last = 0, angle = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double now = sw.Elapsed.TotalSeconds;
            angle += (Math.PI / 4) * (now - last);
            last = now;
            float a = (float)(angle % (Math.PI * 2));
            facade.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
            facade.SetDiagnosticPosition(new Vector3(MathF.Sin(a) * 3f, 0f, MathF.Cos(a) * 3f));
            Thread.Sleep(40);
        }

        facade.StopDiagnosticSound();
        facade.Dispose();
        Console.WriteLine($"RESULT: PASSED — FMOD initialized and ran for {seconds:F0}s on this platform.");
        return 0;
    }

    public static void RunOrbitTest()
    {
        Console.WriteLine("=== OpenFPS Audio Diagnostic: Orbiting Mono Source ===");
        Console.WriteLine();

        var facade = new AudioEngineFacade();
        facade.Initialize();

        // Listener parked at the origin, facing +Z (FRONT), +Y up, not moving.
        facade.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
        facade.StartDiagnosticSound();

        Console.WriteLine("A repeating broadband click should be orbiting your head at 3 metres.");
        Console.WriteLine("Listener faces +Z (FRONT), +Y is UP, game convention is +X = RIGHT.");
        Console.WriteLine();
        Console.WriteLine("Put on HEADPHONES. Check whether what you HEAR matches the announced label:");
        Console.WriteLine("  HORIZONTAL phase: FRONT -> RIGHT -> BACK -> LEFT");
        Console.WriteLine("  VERTICAL phase:   FRONT -> ABOVE -> BACK -> BELOW");
        Console.WriteLine();
        Console.WriteLine("Interpreting the result:");
        Console.WriteLine("  - LEFT/RIGHT swapped  -> handedness/listener-right vector is inverted.");
        Console.WriteLine("  - FRONT/BACK swapped  -> listener forward vector is inverted.");
        Console.WriteLine("  - No height/front-back distinction at all -> NO real HRTF (expected today;");
        Console.WriteLine("    plain FMOD only pans amplitude). This is the gap a binaural renderer fills.");
        Console.WriteLine();
        Console.WriteLine("Press Q (or Ctrl+C) to quit.");
        Console.WriteLine();

        const float radius = 3f;
        const double angularSpeed = Math.PI / 4; // 45 deg/s -> 8 s per revolution
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double lastTime = 0;
        double angle = 0;
        string lastLabel = "";

        bool running = true;
        var keyThread = new Thread(() =>
        {
            while (running)
            {
                if (Console.ReadKey(true).Key == ConsoleKey.Q) running = false;
            }
        }) { IsBackground = true };
        keyThread.Start();

        while (running)
        {
            double now = sw.Elapsed.TotalSeconds;
            double dt = now - lastTime;
            lastTime = now;
            angle += angularSpeed * dt;

            // Alternate horizontal and vertical orbits every full revolution.
            bool vertical = ((int)(angle / (Math.PI * 2)) % 2) == 1;
            float a = (float)(angle % (Math.PI * 2));

            // angle 0 = +Z (front), pi/2 = +X (right), pi = -Z (back), 3pi/2 = -X (left).
            Vector3 pos = vertical
                ? new Vector3(0f, MathF.Sin(a) * radius, MathF.Cos(a) * radius)
                : new Vector3(MathF.Sin(a) * radius, 0f, MathF.Cos(a) * radius);

            facade.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
            facade.SetDiagnosticPosition(pos);

            string label = Describe(pos);
            if (label != lastLabel)
            {
                Console.WriteLine($"[{(vertical ? "VERTICAL  " : "HORIZONTAL")}] expected: {label,-6} pos=({pos.X,5:F1},{pos.Y,5:F1},{pos.Z,5:F1})");
                lastLabel = label;
            }

            Thread.Sleep(40);
        }

        facade.StopDiagnosticSound();
        facade.Dispose();
        Console.WriteLine("Diagnostic stopped.");
        Log.Information("Audio diagnostic finished.");
    }

    /// <summary>Maps a position to a human-readable direction in the game's coordinate convention.</summary>
    private static string Describe(Vector3 p)
    {
        float ax = MathF.Abs(p.X), ay = MathF.Abs(p.Y), az = MathF.Abs(p.Z);
        if (az >= ax && az >= ay) return p.Z >= 0 ? "FRONT" : "BACK";
        if (ax >= ay) return p.X >= 0 ? "RIGHT" : "LEFT";
        return p.Y >= 0 ? "ABOVE" : "BELOW";
    }
}
