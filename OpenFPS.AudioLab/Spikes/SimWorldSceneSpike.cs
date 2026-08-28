using System;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase 4b spike: prove the live integration path that the acoustic worker uses — extract scene geometry
/// from a <see cref="WorldSnapshot"/> of SOLID BOX entities via <see cref="SteamAudioScene.BoxesFromWorld"/>,
/// build the scene, and run <see cref="SteamAudioSimulator"/> against it. This is the same code the
/// AsyncAcousticWorker runs, minus the hand-rolled reflection pass (which needs a full AcousticMap), so it
/// validates the WorldSnapshot -> Box -> IPLScene extraction and per-source occlusion in one headless run.
///
/// Scene = the demo wood-room as world entities; a source inside is BLOCKED to a listener behind the east
/// wall and CLEAR to a listener in the doorway. Headless.
/// </summary>
public static class SimWorldSceneSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();

        var world = BuildWoodRoomWorld();
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        Console.WriteLine($"Extracted {boxes.Count} solid box colliders from the WorldSnapshot (expected 5).");
        if (boxes.Count != 5) { Console.WriteLine("RESULT: FAILED — wrong collider count from WorldSnapshot."); return 2; }

        var ctxS = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("ctx failed"); return 1; }

        using var scene = new SteamAudioScene(ctx);
        scene.Build(boxes);
        if (!scene.IsBuilt) { Console.WriteLine("scene build failed"); Phonon.iplContextRelease(ref ctx); return 1; }

        using var sim = new SteamAudioSimulator(ctx, maxSources: 8);
        if (!sim.IsValid) { Console.WriteLine("simulator create failed"); Phonon.iplContextRelease(ref ctx); return 1; }
        sim.SetScene(scene);

        IntPtr src = sim.AcquireSource();
        if (src == IntPtr.Zero) { Console.WriteLine("acquire failed"); Phonon.iplContextRelease(ref ctx); return 1; }

        var srcPos = new Vector3(7, 1.5f, 15); // inside the room

        float Visibility(Vector3 listener)
        {
            sim.SetSourceInputs(src, srcPos);
            sim.SetListener(listener);
            sim.Run();
            return sim.GetResult(src).Visibility;
        }

        float behindWall = Visibility(new Vector3(17, 1.5f, 15));
        float throughDoor = Visibility(new Vector3(7, 1.5f, 6));
        Console.WriteLine($"Visibility behind east wall: {behindWall:F2}   through open door: {throughDoor:F2}   (1=clear, 0=blocked)");

        bool ok = behindWall < 0.3f && throughDoor > 0.5f;
        Console.WriteLine(ok
            ? "RESULT: PASSED — WorldSnapshot colliders -> scene -> simulator: blocked behind the wall, clear through the doorway."
            : "RESULT: FAILED — review WorldSnapshot scene extraction / occlusion.");

        Phonon.iplContextRelease(ref ctx);
        return ok ? 0 : 2;
    }

    /// <summary>Builds a minimal WorldSnapshot whose solid box entities form the wood room + doorway.</summary>
    private static WorldSnapshot BuildWoodRoomWorld()
    {
        var world = new WorldSnapshot();
        int id = 1;
        void AddBox(Vector3 center, Vector3 size, string material)
        {
            var def = new EntityDefinition
            {
                EntityId = id,
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = size, IsSolid = true },
                Material = new MaterialComponent { Material = material },
            };
            world.Entities[id] = new EntitySnapshot
            {
                Id = id,
                Definition = def,
                Transform = new Transform { Position = center, Rotation = Quaternion.Identity, Scale = Vector3.One },
                Velocity = Vector3.Zero,
            };
            id++;
        }

        AddBox(new Vector3(7, 2, 20),  new Vector3(10, 4, 0.5f), "Concrete"); // north
        AddBox(new Vector3(12, 2, 15), new Vector3(0.5f, 4, 10), "Concrete"); // east
        AddBox(new Vector3(2, 2, 15),  new Vector3(0.5f, 4, 10), "Concrete"); // west
        AddBox(new Vector3(4, 2, 10),  new Vector3(4, 4, 0.5f), "Concrete");  // south-left  (x2..6)
        AddBox(new Vector3(10, 2, 10), new Vector3(4, 4, 0.5f), "Concrete");  // south-right (x8..12) -> door x6..8
        return world;
    }
}
