using System;
using System.Runtime.InteropServices;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase-1 spike for the Steam Audio simulation migration. Builds a scene with a single wall quad, a
/// simulator, and one source, then runs DIRECT simulation and reads the occlusion factor as the source
/// moves BEHIND the wall vs BESIDE it. No ears needed: PASS = occluded behind, clear beside. Also a
/// smoke test of the new sim P/Invoke struct layouts (a wrong layout would crash or return garbage).
/// </summary>
public static class SimOcclusionSpike
{
    public static int Run()
    {
        // 1. Context.
        var ctxS = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("iplContextCreate failed"); return 1; }

        // 2. Scene (built-in ray tracer).
        var sceneS = new Phonon.IPLSceneSettings { type = Phonon.IPL_SCENETYPE_DEFAULT };
        if (Phonon.iplSceneCreate(ctx, ref sceneS, out IntPtr scene) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("iplSceneCreate failed"); return 1; }

        // 3. One wall quad in the x-y plane at z=0, spanning x[-5,5] y[0,5]. Solid concrete-ish material.
        var verts = new PV[]
        {
            new() { x = -5, y = 0, z = 0 }, new() { x = 5, y = 0, z = 0 },
            new() { x = 5,  y = 5, z = 0 }, new() { x = -5, y = 5, z = 0 },
        };
        var tris = new Phonon.IPLTriangle[] { new() { i0 = 0, i1 = 1, i2 = 2 }, new() { i0 = 0, i1 = 2, i2 = 3 } };
        var matIdx = new int[] { 0, 0 };
        var mats = new Phonon.IPLMaterial[] { new() { absLow = 0.1f, absMid = 0.05f, absHigh = 0.03f, scattering = 0.05f, transLow = 0.05f, transMid = 0.02f, transHigh = 0.01f } };

        var hV = GCHandle.Alloc(verts, GCHandleType.Pinned);
        var hT = GCHandle.Alloc(tris, GCHandleType.Pinned);
        var hMI = GCHandle.Alloc(matIdx, GCHandleType.Pinned);
        var hM = GCHandle.Alloc(mats, GCHandleType.Pinned);

        var meshS = new Phonon.IPLStaticMeshSettings
        {
            numVertices = verts.Length, numTriangles = tris.Length, numMaterials = mats.Length,
            vertices = hV.AddrOfPinnedObject(), triangles = hT.AddrOfPinnedObject(),
            materialIndices = hMI.AddrOfPinnedObject(), materials = hM.AddrOfPinnedObject(),
        };
        if (Phonon.iplStaticMeshCreate(scene, ref meshS, out IntPtr mesh) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("iplStaticMeshCreate failed"); return 1; }
        Phonon.iplStaticMeshAdd(mesh, scene);
        Phonon.iplSceneCommit(scene);
        hV.Free(); hT.Free(); hMI.Free(); hM.Free();

        // 4. Simulator (DIRECT only).
        var simS = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_DIRECT,
            sceneType = Phonon.IPL_SCENETYPE_DEFAULT,
            reflectionType = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = 4096, numDiffuseSamples = 32,
            maxDuration = 1.0f, maxOrder = 1, maxNumSources = 8, numThreads = 1,
            rayBatchSize = 16, numVisSamples = 4, samplingRate = 44100, frameSize = 1024,
        };
        if (Phonon.iplSimulatorCreate(ctx, ref simS, out IntPtr sim) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("iplSimulatorCreate failed"); return 1; }
        Phonon.iplSimulatorSetScene(sim, scene);
        Phonon.iplSimulatorCommit(sim);

        // 5. One source.
        var srcS = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_DIRECT };
        if (Phonon.iplSourceCreate(sim, ref srcS, out IntPtr source) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("iplSourceCreate failed"); return 1; }
        Phonon.iplSourceAdd(source, sim);
        Phonon.iplSimulatorCommit(sim);

        var listenerPos = new PV { x = 0, y = 1.5f, z = -3 };

        float Occlusion(PV sourcePos)
        {
            var inputs = new Phonon.IPLSimulationInputs
            {
                flags = Phonon.IPL_SIMULATIONFLAGS_DIRECT,
                directFlags = Phonon.IPL_DIRECTSIMULATIONFLAGS_OCCLUSION | Phonon.IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION,
                source = Coord(sourcePos),
                occlusionType = Phonon.IPL_OCCLUSIONTYPE_VOLUMETRIC,
                occlusionRadius = 0.5f,
                numOcclusionSamples = 16,
                numTransmissionRays = 1,
            };
            Phonon.iplSourceSetInputs(source, Phonon.IPL_SIMULATIONFLAGS_DIRECT, ref inputs);

            var shared = new Phonon.IPLSimulationSharedInputs { listener = Coord(listenerPos), numRays = 4096, numBounces = 1, duration = 1.0f, order = 1, irradianceMinDistance = 1.0f };
            Phonon.iplSimulatorSetSharedInputs(sim, Phonon.IPL_SIMULATIONFLAGS_DIRECT, ref shared);

            Phonon.iplSimulatorRunDirect(sim);

            var outputs = default(Phonon.IPLSimulationOutputs);
            Phonon.iplSourceGetOutputs(source, Phonon.IPL_SIMULATIONFLAGS_DIRECT, ref outputs);
            return outputs.direct.occlusion;
        }

        // Steam Audio's `occlusion` is a VISIBILITY/gain factor (1 = clear/full, 0 = fully blocked) —
        // the direct effect multiplies the signal by it.
        float behind = Occlusion(new PV { x = 0, y = 1.5f, z = 3 });   // wall between listener and source
        float beside = Occlusion(new PV { x = 8, y = 1.5f, z = -3 });  // clear, alongside the listener

        Console.WriteLine($"Visibility BEHIND wall (z=+3): {behind:F2}   BESIDE wall (x=8): {beside:F2}   (1=clear, 0=blocked)");
        bool ok = behind < 0.2f && beside > 0.5f;
        Console.WriteLine(ok
            ? "RESULT: PASSED — Steam Audio simulation blocks behind the wall (vis~0) and is clear beside it (vis~1); struct layouts valid."
            : "RESULT: FAILED — occlusion did not behave as expected (check struct layouts / scene).");

        Phonon.iplSourceRelease(ref source);
        Phonon.iplSimulatorRelease(ref sim);
        Phonon.iplStaticMeshRelease(ref mesh);
        Phonon.iplSceneRelease(ref scene);
        Phonon.iplContextRelease(ref ctx);
        return ok ? 0 : 2;
    }

    private static Phonon.IPLCoordinateSpace3 Coord(PV origin) => new()
    {
        right = new PV { x = 1, y = 0, z = 0 },
        up = new PV { x = 0, y = 1, z = 0 },
        ahead = new PV { x = 0, y = 0, z = -1 }, // Steam Audio forward
        origin = origin,
    };
}
