using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase 4c end-to-end spike: drive pathing through the real <see cref="SteamAudioSimulator"/> API the
/// acoustic worker uses — build the wood-room scene (walls + a floor so UNIFORMFLOOR can place probes),
/// let the simulator generate probes from the scene bounds and bake the path graph, then per "tick" run
/// the combined direct+pathing stages and read the WORLD arrival direction. A listener lined up with the
/// open doorway should hear the source arriving from the doorway/room side (+z here), confirming the full
/// chain: scene -> probes -> bake -> RunPathing -> SH -> world direction. (The SH->direction convention
/// itself is pinned independently by SimPathDirSpike.) Headless.
/// </summary>
public static class SimPathFrameSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();

        var q = Quaternion.Identity;
        var boxes = new[]
        {
            // Floor under and south of the room (top face at y=0) so pathing probes have a surface.
            new SteamAudioScene.Box(new Vector3(7, -0.25f, 13), new Vector3(30, 0.5f, 30), q, "Wood"),
            new SteamAudioScene.Box(new Vector3(7, 2, 20),  new Vector3(10, 4, 0.5f), q, "Concrete"), // north
            new SteamAudioScene.Box(new Vector3(12, 2, 15), new Vector3(0.5f, 4, 10), q, "Concrete"), // east
            new SteamAudioScene.Box(new Vector3(2, 2, 15),  new Vector3(0.5f, 4, 10), q, "Concrete"), // west
            new SteamAudioScene.Box(new Vector3(4, 2, 10),  new Vector3(4, 4, 0.5f), q, "Concrete"),  // south-left (x2..6)
            new SteamAudioScene.Box(new Vector3(10, 2, 10), new Vector3(4, 4, 0.5f), q, "Concrete"),  // south-right (x8..12) -> door x6..8
        };

        var ctxS = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("ctx failed"); return 1; }

        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        if (!scene.IsBuilt) { Console.WriteLine("scene build failed"); Phonon.iplContextRelease(ref ctx); return 1; }
        Console.WriteLine($"Scene bounds: min={scene.BoundsMin} max={scene.BoundsMax}");

        using var sim = new SteamAudioSimulator(ctx, maxSources: 8, enablePathing: true);
        if (!sim.IsValid) { Console.WriteLine("simulator create failed"); Phonon.iplContextRelease(ref ctx); return 1; }
        sim.SetScene(scene); // generates probes + bakes the path graph
        Console.WriteLine($"PathingReady={sim.PathingReady}");
        if (!sim.PathingReady) { Console.WriteLine("RESULT: FAILED — no probes/bake (floor missing or no probes generated)."); Phonon.iplContextRelease(ref ctx); return 2; }

        IntPtr src = sim.AcquireSource();
        if (src == IntPtr.Zero) { Console.WriteLine("acquire failed"); Phonon.iplContextRelease(ref ctx); return 1; }

        var sourcePos = new Vector3(7, 1.5f, 15); // inside the room
        var doorPos = new Vector3(7, 1.5f, 10);   // the opening
        var listener = new Vector3(7, 1.5f, 6);   // outside, lined up with the doorway

        // One tick: stage source + listener, run direct+pathing, read both.
        sim.SetSourceInputs(src, sourcePos);
        sim.SetListener(listener);
        sim.Run();
        var direct = sim.GetResult(src);
        var path = sim.GetPathing(src);

        Vector3 dirToDoor = Vector3.Normalize(doorPos - listener);
        float towardDoor = path.Found ? Vector3.Dot(path.WorldDirection, dirToDoor) : 0f;
        Vector3 apparent = path.Found ? listener + path.WorldDirection * Vector3.Distance(listener, sourcePos) : sourcePos;

        Console.WriteLine($"  direct visibility={direct.Visibility:F2}");
        Console.WriteLine($"  pathing found={path.Found} energy={path.Energy:F3} arrivalDir=({path.WorldDirection.X:F2},{path.WorldDirection.Y:F2},{path.WorldDirection.Z:F2})");
        Console.WriteLine($"  dot(arrival, toward-door)={towardDoor:F2}   synthesized apparentPos=({apparent.X:F1},{apparent.Y:F1},{apparent.Z:F1})");

        // The doorway is straight ahead at +z; sound should arrive from the door/room side, not behind.
        bool ok = path.Found && path.WorldDirection.Z > 0.5f && towardDoor > 0.5f;
        Console.WriteLine(ok
            ? "RESULT: PASSED — pathing via the simulator API found a route and the sound arrives from the doorway (+z), so the HRTF apparent-position can be driven from it."
            : "RESULT: FAILED — pathing did not yield a doorway-ward arrival direction (review probes/bake/mapping).");

        Phonon.iplContextRelease(ref ctx);
        return ok ? 0 : 2;
    }
}
