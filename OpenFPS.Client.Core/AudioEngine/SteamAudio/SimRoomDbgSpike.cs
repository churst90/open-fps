using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase 4 debug: reproduce the in-game wood-room occlusion using the ACTUAL default-map geometry
/// (foundation + room-B walls/floor + a material zone), with the listener at eye height (~2.75) and the
/// beacon at y=1.5 — to find why the live game pins occlusion at the cap even inside the room.
/// </summary>
public static class SimRoomDbgSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();
        var q = Quaternion.Identity;

        // Real default-map boxes around room B (wood), sizes = prefab ColliderSize * map Scale.
        var boxes = new System.Collections.Generic.List<SteamAudioScene.Box>
        {
            new(new Vector3(0, -0.05f, 0),  new Vector3(100, 0.1f, 100), q, "Concrete"), // auto foundation
            new(new Vector3(0, 0, 10),      new Vector3(40, 0.1f, 40),  q, "Concrete"),  // material zone 100
            new(new Vector3(7, 0.05f, 15),  new Vector3(10, 0.1f, 10),  q, "Wood"),      // room B floor 305
            new(new Vector3(7, 2, 20),      new Vector3(10, 4, 0.5f),   q, "Concrete"),  // north 300
            new(new Vector3(12, 2, 15),     new Vector3(0.5f, 4, 10),   q, "Concrete"),  // east 301
            new(new Vector3(2, 2, 15),      new Vector3(0.5f, 4, 10),   q, "Concrete"),  // west 302
            new(new Vector3(4, 2, 10),      new Vector3(4, 4, 0.5f),    q, "Concrete"),  // south-left 303
            new(new Vector3(10, 2, 10),     new Vector3(4, 4, 0.5f),    q, "Concrete"),  // south-right 304 -> door x6..8
        };

        var ctxS = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("ctx failed"); return 1; }

        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        // Match the LIVE worker: pathing enabled (this also generates floor probes + bakes a path graph).
        using var sim = new SteamAudioSimulator(ctx, maxSources: 4, enablePathing: true);
        sim.SetScene(scene);
        Console.WriteLine($"PathingReady={sim.PathingReady}");
        IntPtr src = sim.AcquireSource();

        var source = new Vector3(7, 1.5f, 15); // megaphone inside room B

        float Occ(Vector3 listener)
        {
            sim.SetSourceInputs(src, source);
            sim.SetListener(listener);
            sim.Run();
            return 1f - sim.GetResult(src).Visibility; // fraction blocked
        }

        Console.WriteLine("Beacon at (7,1.5,15) inside wood room B. Occlusion (0=clear,1=blocked):");
        (string label, Vector3 pos)[] cases =
        {
            ("inside, next to beacon, eye y=2.75", new Vector3(7, 2.75f, 13)),
            ("inside, next to beacon, y=1.5     ", new Vector3(7, 1.5f, 13)),
            ("inside far corner,      eye y=2.75", new Vector3(4, 2.75f, 18)),
            ("in doorway (x7,z9.5),   eye y=2.75", new Vector3(7, 2.75f, 9.5f)),
            ("outside behind S wall,  eye y=2.75", new Vector3(10, 2.75f, 8)),
            ("outside behind E wall,  eye y=2.75", new Vector3(15, 2.75f, 15)),
        };
        foreach (var c in cases)
            Console.WriteLine($"  occ={Occ(c.pos):F2}   {c.label}  dist={Vector3.Distance(c.pos, source):F1}");

        Console.WriteLine("\nExpected: inside ~0, doorway ~0 (clear through door), behind walls high.");
        Phonon.iplContextRelease(ref ctx);
        return 0;
    }
}
