using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase 4c diagnostic: pin down the pathing SH -> WORLD arrival-direction convention (the unresolved
/// detail from the Phase 2 spike). Runs pathing through four scenes whose opening — and therefore the
/// direction the sound must arrive FROM — is a known pure world axis (+z, -z, +x, -x): the listener is
/// always lined up with the doorway so the path is a straight shot through the gap, and the arrival
/// direction equals "listener -> door". Prints the raw order-1 SH for each so the (axis,sign) mapping
/// can be read off, then self-checks that the dominant extracted axis matches the expected one under the
/// deduced mapping. Headless.
/// </summary>
public static class SimPathDirSpike
{
    private delegate void ProgressCallback(float progress, IntPtr userData);
    private static readonly ProgressCallback _progress = (p, u) => { };

    public static int Run()
    {
        var ctxS = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("ctx failed"); return 1; }

        Console.WriteLine("Pathing SH -> world direction calibration (listener lined up with the doorway):");
        Console.WriteLine("  expected = world direction from listener toward the open door (where sound arrives FROM)\n");

        // Each case: wall plane + door, listener lined up with the door, source straight behind it.
        // expected = unit world vector pointing from listener to the door.
        var cases = new (string name, bool wallAlongX, PV listener, PV source, PV expected)[]
        {
            // Wall in the XY plane (normal ±z), door gap in x at x=0:
            ("+z", false, new PV{x=0,y=1.5f,z=-3}, new PV{x=0,y=1.5f,z=3},  new PV{x=0,y=0,z=1}),
            ("-z", false, new PV{x=0,y=1.5f,z=3},  new PV{x=0,y=1.5f,z=-3}, new PV{x=0,y=0,z=-1}),
            // Wall in the ZY plane (normal ±x), door gap in z at z=0:
            ("+x", true,  new PV{x=-3,y=1.5f,z=0}, new PV{x=3,y=1.5f,z=0},  new PV{x=1,y=0,z=0}),
            ("-x", true,  new PV{x=3,y=1.5f,z=0},  new PV{x=-3,y=1.5f,z=0}, new PV{x=-1,y=0,z=0}),
        };

        var rows = new List<(string name, float w, float shY, float shZ, float shX, PV expected)>();
        foreach (var c in cases)
        {
            var sh = RunCase(ctx, c.wallAlongX, c.listener, c.source, out float w);
            // ACN/SN3D order-1: [0]=W, [1]=Y(m=-1), [2]=Z(m=0), [3]=X(m=+1).
            rows.Add((c.name, w, sh[1], sh[2], sh[3], c.expected));
            Console.WriteLine($"  case {c.name}: W={w:F3}  SH(Y,Z,X)=({sh[1]:+0.000;-0.000},{sh[2]:+0.000;-0.000},{sh[3]:+0.000;-0.000})  expected world=({c.expected.x:+0;-0},{c.expected.y:+0;-0},{c.expected.z:+0;-0})");
        }

        // Deduce the mapping: for each case the expected world axis is a single component, so whichever SH
        // dipole component is dominant (and its sign) tells us how SH maps to that world axis. We then
        // verify the SAME (axis,sign) mapping is consistent across all four cases.
        Console.WriteLine();
        bool foundAll = true;
        foreach (var r in rows) if (r.w <= 0.001f) { foundAll = false; Console.WriteLine($"  WARN: case {r.name} found no path (W~0) — geometry/bake issue."); }

        // Discovered convention (regression guard): Steam Audio's order-1 pathing SH maps to a WORLD
        // arrival direction (the direction the sound comes FROM) as
        //     worldDir = normalize( -sh[1], sh[2], -sh[3] )
        // i.e. world X = -ACN(m=-1), world Z = -ACN(m=+1), world Y = ACN(m=0). This is exactly
        // SteamAudioSimulator.PathingWorldDirection(sh). Verify it agrees with every known case.
        int agree = 0;
        foreach (var r in rows)
        {
            var d = SteamAudioSimulator.PathingWorldDirection(r.w, r.shY, r.shZ, r.shX);
            float dot = d.X * r.expected.x + d.Y * r.expected.y + d.Z * r.expected.z;
            Console.WriteLine($"  case {r.name}: mapped worldDir=({d.X:+0.00;-0.00},{d.Y:+0.00;-0.00},{d.Z:+0.00;-0.00})  dot(expected)={dot:+0.00;-0.00}");
            if (dot > 0.7f) agree++;
        }
        Console.WriteLine($"  mapping worldDir = normalize(-sh[1], sh[2], -sh[3]) agrees in {agree}/4 cases.");

        bool ok = foundAll && agree == 4;
        Console.WriteLine(ok
            ? "\nRESULT: PASSED — pathing SH maps consistently to the expected world arrival direction (mapping locked in PathingWorldDirection)."
            : "\nRESULT: FAILED — mapping not consistent; see rows (this spike's job is to surface the convention).");

        Phonon.iplContextRelease(ref ctx);
        return ok ? 0 : 2;
    }

    /// <summary>Builds a floor + a split wall with a 2 m centred doorway, bakes pathing, runs one source,
    /// and returns the order-1 SH (length 4) plus W. wallAlongX = wall lies in the ZY plane (normal ±x);
    /// otherwise it lies in the XY plane (normal ±z).</summary>
    private static float[] RunCase(IntPtr ctx, bool wallAlongX, PV listener, PV source, out float w)
    {
        var sceneS = new Phonon.IPLSceneSettings { type = Phonon.IPL_SCENETYPE_DEFAULT };
        Phonon.iplSceneCreate(ctx, ref sceneS, out IntPtr scene);

        var verts = new List<PV>();
        var tris = new List<Phonon.IPLTriangle>();
        void Quad(PV a, PV b, PV c, PV d)
        {
            int i = verts.Count; verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            tris.Add(new Phonon.IPLTriangle { i0 = i, i1 = i + 1, i2 = i + 2 });
            tris.Add(new Phonon.IPLTriangle { i0 = i, i1 = i + 2, i2 = i + 3 });
        }
        // Floor (normal +y).
        Quad(new PV { x = -8, y = 0, z = -8 }, new PV { x = -8, y = 0, z = 8 }, new PV { x = 8, y = 0, z = 8 }, new PV { x = 8, y = 0, z = -8 });
        if (!wallAlongX)
        {
            // Wall in XY plane at z=0, door gap x[-1,1].
            Quad(new PV { x = -6, y = 0, z = 0 }, new PV { x = -1, y = 0, z = 0 }, new PV { x = -1, y = 4, z = 0 }, new PV { x = -6, y = 4, z = 0 });
            Quad(new PV { x = 1, y = 0, z = 0 }, new PV { x = 6, y = 0, z = 0 }, new PV { x = 6, y = 4, z = 0 }, new PV { x = 1, y = 4, z = 0 });
        }
        else
        {
            // Wall in ZY plane at x=0, door gap z[-1,1].
            Quad(new PV { x = 0, y = 0, z = -6 }, new PV { x = 0, y = 0, z = -1 }, new PV { x = 0, y = 4, z = -1 }, new PV { x = 0, y = 4, z = -6 });
            Quad(new PV { x = 0, y = 0, z = 1 }, new PV { x = 0, y = 0, z = 6 }, new PV { x = 0, y = 4, z = 6 }, new PV { x = 0, y = 4, z = 1 });
        }

        var vArr = verts.ToArray(); var tArr = tris.ToArray();
        var matIdx = new int[tArr.Length];
        var mats = new Phonon.IPLMaterial[] { new() { absLow = 0.1f, absMid = 0.05f, absHigh = 0.03f, scattering = 0.05f, transLow = 0.05f, transMid = 0.02f, transHigh = 0.01f } };
        var hV = GCHandle.Alloc(vArr, GCHandleType.Pinned); var hT = GCHandle.Alloc(tArr, GCHandleType.Pinned);
        var hMI = GCHandle.Alloc(matIdx, GCHandleType.Pinned); var hM = GCHandle.Alloc(mats, GCHandleType.Pinned);
        var meshS = new Phonon.IPLStaticMeshSettings
        {
            numVertices = vArr.Length, numTriangles = tArr.Length, numMaterials = mats.Length,
            vertices = hV.AddrOfPinnedObject(), triangles = hT.AddrOfPinnedObject(),
            materialIndices = hMI.AddrOfPinnedObject(), materials = hM.AddrOfPinnedObject(),
        };
        Phonon.iplStaticMeshCreate(scene, ref meshS, out IntPtr mesh);
        Phonon.iplStaticMeshAdd(mesh, scene);
        Phonon.iplSceneCommit(scene);
        hV.Free(); hT.Free(); hMI.Free(); hM.Free();

        Phonon.iplProbeArrayCreate(ctx, out IntPtr probeArray);
        var genP = new Phonon.IPLProbeGenerationParams { type = Phonon.IPL_PROBEGENERATIONTYPE_UNIFORMFLOOR, spacing = 1.0f, height = 1.5f };
        SetBoxTransform(ref genP.transform, -8, 8, -0.5f, 4f, -8, 8);
        Phonon.iplProbeArrayGenerateProbes(probeArray, scene, ref genP);

        Phonon.iplProbeBatchCreate(ctx, out IntPtr batch);
        Phonon.iplProbeBatchAddProbeArray(batch, probeArray);
        Phonon.iplProbeBatchCommit(batch);

        var identifier = new Phonon.IPLBakedDataIdentifier
        {
            type = Phonon.IPL_BAKEDDATATYPE_PATHING,
            variation = Phonon.IPL_BAKEDDATAVARIATION_DYNAMIC,
            endpointInfluence = new Phonon.IPLSphere { center = new PV { x = 0, y = 0, z = 0 }, radius = 1000f },
        };
        var bakeP = new Phonon.IPLPathBakeParams
        {
            scene = scene, probeBatch = batch, identifier = identifier,
            numSamples = 4, radius = 0.5f, threshold = 0.1f, visRange = 16.0f, pathRange = 100.0f, numThreads = 1,
        };
        Phonon.iplPathBakerBake(ctx, ref bakeP, Marshal.GetFunctionPointerForDelegate(_progress), IntPtr.Zero);

        var simS = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_PATHING, sceneType = Phonon.IPL_SCENETYPE_DEFAULT,
            reflectionType = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = 4096, numDiffuseSamples = 32, maxDuration = 1.0f,
            maxOrder = 1, maxNumSources = 8, numThreads = 1, rayBatchSize = 16, numVisSamples = 4,
            samplingRate = 44100, frameSize = 1024,
        };
        Phonon.iplSimulatorCreate(ctx, ref simS, out IntPtr sim);
        Phonon.iplSimulatorSetScene(sim, scene);
        Phonon.iplSimulatorAddProbeBatch(sim, batch);
        Phonon.iplSimulatorCommit(sim);

        var srcS = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_PATHING };
        Phonon.iplSourceCreate(sim, ref srcS, out IntPtr src);
        Phonon.iplSourceAdd(src, sim);
        Phonon.iplSimulatorCommit(sim);

        var inputs = new Phonon.IPLSimulationInputs
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_PATHING,
            source = Coord(source),
            pathingProbes = batch,
            bakedDataIdentifier = identifier,
            visRadius = 1.0f, visThreshold = 0.1f, visRange = 50.0f, pathingOrder = 1, findAlternatePaths = 1,
        };
        Phonon.iplSourceSetInputs(src, Phonon.IPL_SIMULATIONFLAGS_PATHING, ref inputs);
        var shared = new Phonon.IPLSimulationSharedInputs { listener = Coord(listener), numRays = 4096, numBounces = 1, duration = 1.0f, order = 1, irradianceMinDistance = 1.0f };
        Phonon.iplSimulatorSetSharedInputs(sim, Phonon.IPL_SIMULATIONFLAGS_PATHING, ref shared);
        Phonon.iplSimulatorRunPathing(sim);

        var outputs = default(Phonon.IPLSimulationOutputs);
        Phonon.iplSourceGetOutputs(src, Phonon.IPL_SIMULATIONFLAGS_PATHING, ref outputs);

        var sh = new float[4];
        if (outputs.pathing.shCoeffs != IntPtr.Zero) Marshal.Copy(outputs.pathing.shCoeffs, sh, 0, 4);
        w = sh[0];

        Phonon.iplSourceRelease(ref src);
        Phonon.iplProbeBatchRelease(ref batch);
        Phonon.iplProbeArrayRelease(ref probeArray);
        Phonon.iplSimulatorRelease(ref sim);
        Phonon.iplStaticMeshRelease(ref mesh);
        Phonon.iplSceneRelease(ref scene);
        return sh;
    }

    private static Phonon.IPLCoordinateSpace3 Coord(PV origin) => new()
    {
        right = new PV { x = 1, y = 0, z = 0 },
        up = new PV { x = 0, y = 1, z = 0 },
        ahead = new PV { x = 0, y = 0, z = -1 },
        origin = origin,
    };

    private static unsafe void SetBoxTransform(ref Phonon.IPLMatrix4x4 m, float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        for (int i = 0; i < 16; i++) m.elements[i] = 0;
        m.elements[0] = maxX - minX; m.elements[3] = minX;
        m.elements[5] = maxY - minY; m.elements[7] = minY;
        m.elements[10] = maxZ - minZ; m.elements[11] = minZ;
        m.elements[15] = 1f;
    }
}
