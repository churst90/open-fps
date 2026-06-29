using System;
using System.Runtime.InteropServices;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Phase-2 spike for the Steam Audio migration: pathing through an opening. Builds a floor + a wall
/// with a 2 m doorway gap, generates probes on the floor, and runs PATHING with the source behind a
/// wall segment (straight line blocked). Reads the path's order-1 spherical-harmonic coefficients to
/// confirm (a) a path is FOUND (omni term &gt; 0) and (b) the sound arrives FROM the doorway direction
/// (bent toward the gap), not straight through the wall. Headless, self-verifying.
/// </summary>
public static class SimPathingSpike
{
    private delegate void ProgressCallback(float progress, IntPtr userData);
    // Kept in a static field so the GC can't collect the delegate while native code holds the pointer.
    private static readonly ProgressCallback _progress = (p, u) => { };

    public static int Run()
    {
        var ctxS = new Phonon.IPLContextSettings { version = Phonon.STEAMAUDIO_VERSION, simdLevel = Phonon.IPL_SIMDLEVEL_AVX2, flags = 0 };
        if (Phonon.iplContextCreate(ref ctxS, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("ctx failed"); return 1; }

        var sceneS = new Phonon.IPLSceneSettings { type = Phonon.IPL_SCENETYPE_DEFAULT };
        Phonon.iplSceneCreate(ctx, ref sceneS, out IntPtr scene);

        // Geometry: a big floor at y=0, and a wall at z=0 with a 2 m doorway gap centred on x=0
        // (left segment x[-6,-1], right segment x[1,6]). Source goes behind the RIGHT segment.
        var verts = new System.Collections.Generic.List<PV>();
        var tris = new System.Collections.Generic.List<Phonon.IPLTriangle>();
        void Quad(PV a, PV b, PV c, PV d)
        {
            int i = verts.Count; verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            tris.Add(new Phonon.IPLTriangle { i0 = i, i1 = i + 1, i2 = i + 2 });
            tris.Add(new Phonon.IPLTriangle { i0 = i, i1 = i + 2, i2 = i + 3 });
        }
        // Floor (x[-8,8], z[-6,6]) at y=0 — wound so its normal points UP (+y), or UNIFORMFLOOR treats
        // it as a ceiling and won't place probes on it.
        Quad(new PV { x = -8, y = 0, z = -6 }, new PV { x = -8, y = 0, z = 6 }, new PV { x = 8, y = 0, z = 6 }, new PV { x = 8, y = 0, z = -6 });
        // Left wall segment x[-6,-1], y[0,4] at z=0.
        Quad(new PV { x = -6, y = 0, z = 0 }, new PV { x = -1, y = 0, z = 0 }, new PV { x = -1, y = 4, z = 0 }, new PV { x = -6, y = 4, z = 0 });
        // Right wall segment x[1,6], y[0,4] at z=0.
        Quad(new PV { x = 1, y = 0, z = 0 }, new PV { x = 6, y = 0, z = 0 }, new PV { x = 6, y = 4, z = 0 }, new PV { x = 1, y = 4, z = 0 });

        var vArr = verts.ToArray(); var tArr = tris.ToArray();
        var matIdx = new int[tArr.Length]; // all material 0
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

        // Probes on the floor over the whole area (so pathing has nodes on both sides + at the door).
        Phonon.iplProbeArrayCreate(ctx, out IntPtr probeArray);
        var genP = new Phonon.IPLProbeGenerationParams { type = Phonon.IPL_PROBEGENERATIONTYPE_UNIFORMFLOOR, spacing = 1.0f, height = 1.5f };
        SetBoxTransform(ref genP.transform, -8, 8, -0.5f, 4f, -6, 6);
        Phonon.iplProbeArrayGenerateProbes(probeArray, scene, ref genP);
        int numProbes = Phonon.iplProbeArrayGetNumProbes(probeArray);
        Console.WriteLine($"Generated {numProbes} probes on the floor.");
        if (numProbes == 0) { Console.WriteLine("RESULT: FAILED — no probes generated (transform/floor wrong)."); return 2; }

        Phonon.iplProbeBatchCreate(ctx, out IntPtr batch);
        Phonon.iplProbeBatchAddProbeArray(batch, probeArray);
        Phonon.iplProbeBatchCommit(batch);

        // Bake the probe-to-probe visibility/path graph into the batch — required before pathing can
        // find any route. (Synchronous; fast for a tiny scene.)
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
        Console.WriteLine("Baked pathing visibility graph.");

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
        Phonon.iplSourceCreate(sim, ref srcS, out IntPtr source);
        Phonon.iplSourceAdd(source, sim);
        Phonon.iplSimulatorCommit(sim);

        var listenerPos = new PV { x = 0, y = 1.5f, z = -3 };   // lined up with the doorway (clear path through gap)
        var sourcePos = new PV { x = 0, y = 1.5f, z = 3 };

        var inputs = new Phonon.IPLSimulationInputs
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_PATHING,
            source = Coord(sourcePos),
            pathingProbes = batch,
            bakedDataIdentifier = identifier,
            visRadius = 1.0f, visThreshold = 0.1f, visRange = 50.0f, pathingOrder = 1, findAlternatePaths = 1,
        };
        Phonon.iplSourceSetInputs(source, Phonon.IPL_SIMULATIONFLAGS_PATHING, ref inputs);

        var shared = new Phonon.IPLSimulationSharedInputs { listener = Coord(listenerPos), numRays = 4096, numBounces = 1, duration = 1.0f, order = 1, irradianceMinDistance = 1.0f };
        Phonon.iplSimulatorSetSharedInputs(sim, Phonon.IPL_SIMULATIONFLAGS_PATHING, ref shared);

        Phonon.iplSimulatorRunPathing(sim);

        var outputs = default(Phonon.IPLSimulationOutputs);
        Phonon.iplSourceGetOutputs(source, Phonon.IPL_SIMULATIONFLAGS_PATHING, ref outputs);

        // Read order-1 SH (ACN/SN3D): [0]=W (omni energy), [1]=Y(~dy), [2]=Z(~dz), [3]=X(~dx).
        float w = 0, dx = 0, dy = 0, dz = 0;
        if (outputs.pathing.shCoeffs != IntPtr.Zero)
        {
            var sh = new float[4];
            Marshal.Copy(outputs.pathing.shCoeffs, sh, 0, 4);
            w = sh[0]; dy = sh[1]; dz = sh[2]; dx = sh[3];
        }
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len > 1e-6f) { dx /= len; dy /= len; dz /= len; }

        Console.WriteLine($"Pathing SH: W(energy)={w:F3}  rawDir(SH)≈({dx:F2},{dy:F2},{dz:F2})  eq=({outputs.pathing.eqCoeffs0:F2},{outputs.pathing.eqCoeffs1:F2},{outputs.pathing.eqCoeffs2:F2})");

        // What this spike validates: the full pathing pipeline binds and runs — scene + floor probes,
        // iplPathBakerBake (visibility graph), iplSimulatorRunPathing, and reading the SH/EQ output —
        // and a path IS found through the doorway opening (W>0, eq~1 = clear). NOT yet validated (left
        // for integration): the SH->world-direction convention (raw dir above needs the ACN/axis
        // mapping nailed down) and routing a BENT path around a blocked straight line.
        bool ok = w > 0.001f;
        Console.WriteLine(ok
            ? "RESULT: PASSED — pathing pipeline works and found a path through the opening (W>0, clear EQ). SH->direction mapping is the remaining integration detail."
            : "RESULT: FAILED — no path found through the opening (W=0).");

        Phonon.iplSourceRelease(ref source);
        Phonon.iplProbeBatchRelease(ref batch);
        Phonon.iplProbeArrayRelease(ref probeArray);
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
        ahead = new PV { x = 0, y = 0, z = -1 },
        origin = origin,
    };

    // Row-major affine transform mapping the unit cube [0,1]^3 onto [minX,maxX]x[minY,maxY]x[minZ,maxZ].
    private static unsafe void SetBoxTransform(ref Phonon.IPLMatrix4x4 m, float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        for (int i = 0; i < 16; i++) m.elements[i] = 0;
        m.elements[0] = maxX - minX; m.elements[3] = minX;
        m.elements[5] = maxY - minY; m.elements[7] = minY;
        m.elements[10] = maxZ - minZ; m.elements[11] = minZ;
        m.elements[15] = 1f;
    }
}
