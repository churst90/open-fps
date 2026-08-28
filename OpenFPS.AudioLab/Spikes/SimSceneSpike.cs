using System;
using System.Numerics;
using OpenFPS.Common;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase-3 test: build an <see cref="SteamAudioScene"/> from the demo wood-room's box colliders (the same
/// walls + doorway gap as maps/default.json) and verify occlusion against it — a source inside the room
/// is CLEAR to a listener lined up with the open door, and BLOCKED to a listener behind a side wall.
/// Proves the box-collider -> triangle-mesh -> IPLScene path produces correct geometry. Headless.
/// </summary>
public static class SimSceneSpike
{
    public static int Run()
    {
        AcousticRegistry.Initialize();

        var ctxS = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("ctx failed"); return 1; }

        // Wood room (concrete walls), interior x[2,12] z[10,20], door gap x[6,8] in the south wall (z=10).
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
        if (!scene.IsBuilt) { Console.WriteLine("scene build failed"); return 1; }

        var simS = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_DIRECT, sceneType = Phonon.IPL_SCENETYPE_DEFAULT,
            reflectionType = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = 4096, numDiffuseSamples = 32, maxDuration = 1.0f,
            maxOrder = 1, maxNumSources = 8, numThreads = 1, rayBatchSize = 16, numVisSamples = 4,
            samplingRate = 44100, frameSize = 1024,
        };
        Phonon.iplSimulatorCreate(ctx, ref simS, out IntPtr sim);
        Phonon.iplSimulatorSetScene(sim, scene.Handle);
        Phonon.iplSimulatorCommit(sim);

        var srcS = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_DIRECT };
        Phonon.iplSourceCreate(sim, ref srcS, out IntPtr source);
        Phonon.iplSourceAdd(source, sim);
        Phonon.iplSimulatorCommit(sim);

        var megaphone = new PV { x = 7, y = 1.5f, z = 15 }; // inside the room

        float Visibility(PV listener)
        {
            var inputs = new Phonon.IPLSimulationInputs
            {
                flags = Phonon.IPL_SIMULATIONFLAGS_DIRECT,
                directFlags = Phonon.IPL_DIRECTSIMULATIONFLAGS_OCCLUSION | Phonon.IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION,
                source = Coord(megaphone),
                occlusionType = Phonon.IPL_OCCLUSIONTYPE_VOLUMETRIC, occlusionRadius = 0.5f, numOcclusionSamples = 16,
                numTransmissionRays = 1,
            };
            Phonon.iplSourceSetInputs(source, Phonon.IPL_SIMULATIONFLAGS_DIRECT, ref inputs);
            var shared = new Phonon.IPLSimulationSharedInputs { listener = Coord(listener), numRays = 4096, numBounces = 1, duration = 1.0f, order = 1, irradianceMinDistance = 1.0f };
            Phonon.iplSimulatorSetSharedInputs(sim, Phonon.IPL_SIMULATIONFLAGS_DIRECT, ref shared);
            Phonon.iplSimulatorRunDirect(sim);
            var outputs = default(Phonon.IPLSimulationOutputs);
            Phonon.iplSourceGetOutputs(source, Phonon.IPL_SIMULATIONFLAGS_DIRECT, ref outputs);
            return outputs.direct.occlusion;
        }

        float throughDoor = Visibility(new PV { x = 7, y = 1.5f, z = 6 });   // lined up with the open doorway
        float behindWall = Visibility(new PV { x = 17, y = 1.5f, z = 15 });  // east of the room, east wall between

        Console.WriteLine($"Visibility through open door (x=7,z=6): {throughDoor:F2}   behind east wall (x=17,z=15): {behindWall:F2}   (1=clear, 0=blocked)");
        bool ok = throughDoor > 0.5f && behindWall < 0.3f;
        Console.WriteLine(ok
            ? "RESULT: PASSED — scene built from box colliders: clear through the doorway, blocked behind a wall."
            : "RESULT: FAILED — review geometry/occlusion (door should be clear, wall should block).");

        Phonon.iplSourceRelease(ref source);
        Phonon.iplSimulatorRelease(ref sim);
        Phonon.iplContextRelease(ref ctx);
        return ok ? 0 : 2;
    }

    private static Phonon.IPLCoordinateSpace3 Coord(PV origin) => new()
    {
        right = new PV { x = 1, y = 0, z = 0 }, up = new PV { x = 0, y = 1, z = 0 },
        ahead = new PV { x = 0, y = 0, z = -1 }, origin = origin,
    };
}
