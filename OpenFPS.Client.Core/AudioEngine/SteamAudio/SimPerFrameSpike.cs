using System;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase 4a spike: exercise <see cref="SteamAudioSimulator"/> in the exact runtime shape the acoustic
/// worker thread will use — build a scene once, acquire pooled sources, then run the simulator ONCE per
/// "tick" with two sources batched together while the listener moves, reading per-source occlusion each
/// frame. Also proves source-pool acquire/release/reuse. Headless; no ears needed.
///
/// Scene = the demo wood-room (same boxes as <see cref="SimSceneSpike"/>): a source inside the room is
/// BLOCKED to a listener behind the east wall and becomes CLEAR as the listener walks into the doorway,
/// while a second source out in the open stays clear throughout.
/// </summary>
public static class SimPerFrameSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();

        var ctxS = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("ctx failed"); return 1; }

        var q = Quaternion.Identity;
        var boxes = new[]
        {
            new SteamAudioScene.Box(new Vector3(7, 2, 20),  new Vector3(10, 4, 0.5f), q, "Concrete"), // north
            new SteamAudioScene.Box(new Vector3(12, 2, 15), new Vector3(0.5f, 4, 10), q, "Concrete"), // east
            new SteamAudioScene.Box(new Vector3(2, 2, 15),  new Vector3(0.5f, 4, 10), q, "Concrete"), // west
            new SteamAudioScene.Box(new Vector3(4, 2, 10),  new Vector3(4, 4, 0.5f), q, "Concrete"),  // south-left (x2..6)
            new SteamAudioScene.Box(new Vector3(10, 2, 10), new Vector3(4, 4, 0.5f), q, "Concrete"),  // south-right (x8..12) -> door x6..8
        };

        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        if (!scene.IsBuilt) { Console.WriteLine("scene build failed"); Phonon.iplContextRelease(ref ctx); return 1; }

        using var sim = new SteamAudioSimulator(ctx, maxSources: 8);
        if (!sim.IsValid) { Console.WriteLine("simulator create failed"); Phonon.iplContextRelease(ref ctx); return 1; }
        sim.SetScene(scene);
        Console.WriteLine($"Pool: capacity={sim.Capacity} available={sim.Available}");

        var roomSrcPos = new Vector3(7, 1.5f, 15);   // megaphone inside the room
        // Control source out in the open, SOUTH-EAST of the building (same side as the whole listener
        // path) so its line of sight to every listener position stays clear of the walls (x>12.25).
        var openSrcPos = new Vector3(30, 1.5f, -10);

        IntPtr roomSrc = sim.AcquireSource();
        IntPtr openSrc = sim.AcquireSource();
        if (roomSrc == IntPtr.Zero || openSrc == IntPtr.Zero) { Console.WriteLine("acquire failed"); Phonon.iplContextRelease(ref ctx); return 1; }
        Console.WriteLine($"Acquired 2 sources; available={sim.Available} (expected {sim.Capacity - 2})");

        // Listener walks from behind the east wall toward (and into) the open doorway over several ticks.
        var path = new[]
        {
            new Vector3(17, 1.5f, 15), // behind east wall  -> room source blocked
            new Vector3(14, 1.5f, 12),
            new Vector3(10, 1.5f, 8),
            new Vector3(7,  1.5f, 6),  // lined up with the doorway -> room source clear
        };

        bool allOk = true;
        bool openAlwaysClear = true;
        SteamAudioSimulator.DirectResult firstRoom = default, lastRoom = default;
        for (int t = 0; t < path.Length; t++)
        {
            // One "tick": stage every active source, set the listener, run ONCE, then read each source.
            sim.SetSourceInputs(roomSrc, roomSrcPos);
            sim.SetSourceInputs(openSrc, openSrcPos);
            sim.SetListener(path[t]);
            sim.Run();
            var room = sim.GetResult(roomSrc);
            var open = sim.GetResult(openSrc);
            if (t == 0) firstRoom = room;
            lastRoom = room;

            Console.WriteLine($"  tick {t} listener={path[t]}  roomVis={room.Visibility:F2}  openVis={open.Visibility:F2}");

            // The open source has no occluder on its path to any listener position -> always clear.
            if (open.Visibility < 0.5f) { allOk = false; openAlwaysClear = false; Console.WriteLine("    FAIL: open source should stay clear (batched run leaked occlusion?)"); }
        }

        bool blockedAtStart = firstRoom.Visibility < 0.3f;
        bool clearAtDoor = lastRoom.Visibility > 0.5f;
        if (!blockedAtStart) { allOk = false; Console.WriteLine("    FAIL: room source should be BLOCKED behind the east wall."); }
        if (!clearAtDoor) { allOk = false; Console.WriteLine("    FAIL: room source should be CLEAR through the doorway."); }

        // Pool reuse: release the open source, re-acquire, confirm a handle comes back and still simulates.
        sim.ReleaseSource(openSrc);
        int availAfterRelease = sim.Available;
        IntPtr reacquired = sim.AcquireSource();
        bool poolReuse = reacquired != IntPtr.Zero && availAfterRelease == sim.Capacity - 1;
        if (poolReuse)
        {
            sim.SetSourceInputs(reacquired, openSrcPos);
            sim.SetListener(path[^1]);
            sim.Run();
            var reuse = sim.GetResult(reacquired);
            poolReuse = reuse.Visibility > 0.5f;
            Console.WriteLine($"  pool reuse: reacquired source vis={reuse.Visibility:F2} (available was {availAfterRelease})");
        }
        if (!poolReuse) { allOk = false; Console.WriteLine("    FAIL: pool release/reacquire did not return a working source."); }

        Console.WriteLine($"RESULT: blockedBehindWall={blockedAtStart} clearThroughDoor={clearAtDoor} openAlwaysClear={openAlwaysClear} poolReuse={poolReuse}");
        Console.WriteLine(allOk
            ? "RESULT: PASSED — per-frame batched simulation + source pool work: blocked->clear as the listener reaches the doorway, batched sources independent, pool reused."
            : "RESULT: FAILED — review per-frame simulation / pooling (see FAIL lines above).");

        Phonon.iplContextRelease(ref ctx);
        return allOk ? 0 : 2;
    }
}
