using System;
using System.Runtime.InteropServices;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// P/Invoke bindings for the Steam Audio (phonon) SIMULATION API — scene / static mesh / simulator /
/// source / direct-effect — used to replace the hand-rolled occlusion/reflection/portal layer.
/// Struct layouts are transcribed verbatim from phonon.h 4.8.1 (the version this libphonon ships as),
/// so they MUST match the C ABI exactly: a wrong field/order = native memory corruption.
/// Coordinate system is Steam Audio's: +x right, +y up, -z forward (the game uses +z forward — convert
/// directions/orientation at the boundary; occlusion is position-only so the spike ignores it).
/// </summary>
internal static partial class Phonon
{
    // --- enums (as int constants) ---
    public const int IPL_SCENETYPE_DEFAULT = 0;

    public const int IPL_SIMULATIONFLAGS_DIRECT = 1 << 0;
    public const int IPL_SIMULATIONFLAGS_REFLECTIONS = 1 << 1;
    public const int IPL_SIMULATIONFLAGS_PATHING = 1 << 2;

    public const int IPL_DIRECTSIMULATIONFLAGS_DISTANCEATTENUATION = 1 << 0;
    public const int IPL_DIRECTSIMULATIONFLAGS_AIRABSORPTION = 1 << 1;
    public const int IPL_DIRECTSIMULATIONFLAGS_DIRECTIVITY = 1 << 2;
    public const int IPL_DIRECTSIMULATIONFLAGS_OCCLUSION = 1 << 3;
    public const int IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION = 1 << 4;

    public const int IPL_OCCLUSIONTYPE_RAYCAST = 0;
    public const int IPL_OCCLUSIONTYPE_VOLUMETRIC = 1;

    public const int IPL_REFLECTIONEFFECTTYPE_CONVOLUTION = 0;

    // --- geometry / coordinate structs ---
    [StructLayout(LayoutKind.Sequential)]
    public struct IPLCoordinateSpace3 { public IPLVector3 right, up, ahead, origin; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLTriangle { public int i0, i1, i2; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLMaterial { public float absLow, absMid, absHigh, scattering, transLow, transMid, transHigh; }

    // --- creation settings ---
    [StructLayout(LayoutKind.Sequential)]
    public struct IPLSceneSettings
    {
        public int type;                // IPLSceneType
        public IntPtr closestHitCallback, anyHitCallback, batchedClosestHitCallback, batchedAnyHitCallback;
        public IntPtr userData;
        public IntPtr embreeDevice, radeonRaysDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLStaticMeshSettings
    {
        public int numVertices, numTriangles, numMaterials;
        public IntPtr vertices;         // IPLVector3*
        public IntPtr triangles;        // IPLTriangle*
        public IntPtr materialIndices;  // IPLint32*
        public IntPtr materials;        // IPLMaterial*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLSimulationSettings
    {
        public int flags;               // IPLSimulationFlags
        public int sceneType;           // IPLSceneType
        public int reflectionType;      // IPLReflectionEffectType
        public int maxNumOcclusionSamples;
        public int maxNumRays;
        public int numDiffuseSamples;
        public float maxDuration;
        public int maxOrder;
        public int maxNumSources;
        public int numThreads;
        public int rayBatchSize;
        public int numVisSamples;
        public int samplingRate;
        public int frameSize;
        public IntPtr openCLDevice, radeonRaysDevice, tanDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLSourceSettings { public int flags; }

    // --- per-frame input model structs ---
    [StructLayout(LayoutKind.Sequential)]
    public struct IPLDistanceAttenuationModel { public int type; public float minDistance; public IntPtr callback, userData; public int dirty; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAirAbsorptionModel { public int type; public float c0, c1, c2; public IntPtr callback, userData; public int dirty; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLDirectivity { public float dipoleWeight, dipolePower; public IntPtr callback, userData; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLSphere { public IPLVector3 center; public float radius; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLBakedDataIdentifier { public int type, variation; public IPLSphere endpointInfluence; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLSimulationInputs
    {
        public int flags;               // IPLSimulationFlags
        public int directFlags;         // IPLDirectSimulationFlags
        public IPLCoordinateSpace3 source;
        public IPLDistanceAttenuationModel distanceAttenuationModel;
        public IPLAirAbsorptionModel airAbsorptionModel;
        public IPLDirectivity directivity;
        public int occlusionType;       // IPLOcclusionType
        public float occlusionRadius;
        public int numOcclusionSamples;
        public float reverbScale0, reverbScale1, reverbScale2;
        public float hybridReverbTransitionTime, hybridReverbOverlapPercent;
        public int baked;
        public IPLBakedDataIdentifier bakedDataIdentifier;
        public IntPtr pathingProbes;
        public float visRadius, visThreshold, visRange;
        public int pathingOrder;
        public int enableValidation, findAlternatePaths;
        public int numTransmissionRays;
        public IntPtr deviationModel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLSimulationSharedInputs
    {
        public IPLCoordinateSpace3 listener;
        public int numRays, numBounces;
        public float duration;
        public int order;
        public float irradianceMinDistance;
        public IntPtr pathingVisCallback, pathingUserData;
    }

    // --- direct-effect output (the result we read) ---
    [StructLayout(LayoutKind.Sequential)]
    public struct IPLDirectEffectParams
    {
        public int flags;               // IPLDirectEffectFlags
        public int transmissionType;    // IPLTransmissionType
        public float distanceAttenuation;
        public float airAbsorption0, airAbsorption1, airAbsorption2;
        public float directivity;
        public float occlusion;
        public float transmission0, transmission1, transmission2;
    }

    /// <summary>
    /// Output struct for iplSourceGetOutputs. We only read <c>direct</c>; the reflections + pathing
    /// params that follow it in the C struct are reserved here as opaque bytes (sized generously) so
    /// the native call can write its full struct without overrunning ours.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IPLSimulationOutputs
    {
        public IPLDirectEffectParams direct;
        // ~256 bytes of reserve covering IPLReflectionEffectParams (~72) + IPLPathEffectParams (~96).
        public IntPtr r0, r1, r2, r3, r4, r5, r6, r7, r8, r9, r10, r11, r12, r13, r14, r15;
        public IntPtr p0, p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15;
    }

    // --- functions ---
    [DllImport(Lib, CallingConvention = CC)] public static extern int iplSceneCreate(IntPtr context, ref IPLSceneSettings settings, out IntPtr scene);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSceneRelease(ref IntPtr scene);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSceneCommit(IntPtr scene);

    [DllImport(Lib, CallingConvention = CC)] public static extern int iplStaticMeshCreate(IntPtr scene, ref IPLStaticMeshSettings settings, out IntPtr staticMesh);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplStaticMeshRelease(ref IntPtr staticMesh);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplStaticMeshAdd(IntPtr staticMesh, IntPtr scene);

    [DllImport(Lib, CallingConvention = CC)] public static extern int iplSimulatorCreate(IntPtr context, ref IPLSimulationSettings settings, out IntPtr simulator);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSimulatorRelease(ref IntPtr simulator);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSimulatorSetScene(IntPtr simulator, IntPtr scene);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSimulatorSetSharedInputs(IntPtr simulator, int flags, ref IPLSimulationSharedInputs sharedInputs);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSimulatorCommit(IntPtr simulator);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSimulatorRunDirect(IntPtr simulator);

    [DllImport(Lib, CallingConvention = CC)] public static extern int iplSourceCreate(IntPtr simulator, ref IPLSourceSettings settings, out IntPtr source);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSourceRelease(ref IntPtr source);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSourceAdd(IntPtr source, IntPtr simulator);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSourceSetInputs(IntPtr source, int flags, ref IPLSimulationInputs inputs);
    [DllImport(Lib, CallingConvention = CC)] public static extern void iplSourceGetOutputs(IntPtr source, int flags, ref IPLSimulationOutputs outputs);
}
