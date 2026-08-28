using System;
using System.Runtime.InteropServices;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Minimal P/Invoke bindings for the Steam Audio (phonon) C API — just the binaural-rendering
/// path needed to prove HRTF works (context, HRTF, binaural effect, audio buffers).
/// Resolves libphonon.so on Linux / phonon.dll on Windows. Coordinate system: right-handed,
/// +x right, +y up, -z FORWARD (note: the game uses +z forward, so convert when passing directions).
/// </summary>
internal static partial class Phonon
{
    private const string Lib = "phonon";
    private const CallingConvention CC = CallingConvention.Cdecl;

    // Must match the loaded library; context creation fails otherwise. 4.8.1 -> 0x00040801.
    public const uint STEAMAUDIO_VERSION = (4u << 16) | (8u << 8) | 1u;

    // IPLerror
    public const int IPL_STATUS_SUCCESS = 0;

    // IPLSIMDLevel (phonon.h). NEON aliases SSE2 (both are 4-wide), exactly as the header declares it.
    public const int IPL_SIMDLEVEL_SSE2 = 0;
    public const int IPL_SIMDLEVEL_SSE4 = 1;
    public const int IPL_SIMDLEVEL_AVX = 2;
    public const int IPL_SIMDLEVEL_AVX2 = 3;
    public const int IPL_SIMDLEVEL_AVX512 = 4;
    public const int IPL_SIMDLEVEL_NEON = IPL_SIMDLEVEL_SSE2;
    // IPLHRTFType
    public const int IPL_HRTFTYPE_DEFAULT = 0;
    // IPLHRTFNormType
    public const int IPL_HRTFNORMTYPE_NONE = 0;
    // IPLHRTFInterpolation
    public const int IPL_HRTFINTERPOLATION_NEAREST = 0;
    public const int IPL_HRTFINTERPOLATION_BILINEAR = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLVector3 { public float x, y, z; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLContextSettings
    {
        public uint version;
        public IntPtr logCallback;       // IPLLogFunction  (null)
        public IntPtr allocateCallback;  // IPLAllocateFunction (null)
        public IntPtr freeCallback;      // IPLFreeFunction (null)
        public int simdLevel;            // IPLSIMDLevel
        public int flags;                // IPLContextFlags
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAudioSettings { public int samplingRate; public int frameSize; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLHRTFSettings
    {
        public int type;             // IPLHRTFType
        public IntPtr sofaFileName;  // const char* (null)
        public IntPtr sofaData;      // const uint8* (null)
        public int sofaDataSize;
        public float volume;
        public int normType;         // IPLHRTFNormType
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLBinauralEffectSettings { public IntPtr hrtf; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLBinauralEffectParams
    {
        public IPLVector3 direction;
        public int interpolation;    // IPLHRTFInterpolation
        public float spatialBlend;
        public IntPtr hrtf;
        public IntPtr peakDelays;    // float* (null)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAudioBuffer
    {
        public int numChannels;
        public int numSamples;
        public IntPtr data;          // float**
    }

    // --- SIMD capability -----------------------------------------------------------------------------
    // Steam Audio does NOT probe the CPU: whatever level you hand iplContextCreate is the level it will
    // emit code for. Asking for AVX2 on a machine without it is an illegal-instruction crash inside
    // libphonon, not a graceful failure — so ask the CPU what it actually has. Cached: CPU features
    // cannot change while the process runs.
    private static int _simdLevel = -1;

    /// <summary>The highest IPLSIMDLevel this CPU actually supports.</summary>
    public static int DetectSimdLevel()
    {
        if (_simdLevel >= 0) return _simdLevel;

        int level;
        if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) level = IPL_SIMDLEVEL_AVX512;
        else if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) level = IPL_SIMDLEVEL_AVX2;
        else if (System.Runtime.Intrinsics.X86.Avx.IsSupported) level = IPL_SIMDLEVEL_AVX;
        else if (System.Runtime.Intrinsics.X86.Sse42.IsSupported) level = IPL_SIMDLEVEL_SSE4;
        else level = IPL_SIMDLEVEL_SSE2;  // also the value for ARM NEON

        _simdLevel = level;
        return level;
    }

    /// <summary>Human-readable name for an IPLSIMDLevel, for the log line that records what we asked for.</summary>
    public static string SimdLevelName(int level) => level switch
    {
        IPL_SIMDLEVEL_AVX512 => "AVX-512",
        IPL_SIMDLEVEL_AVX2 => "AVX2",
        IPL_SIMDLEVEL_AVX => "AVX",
        IPL_SIMDLEVEL_SSE4 => "SSE4.2",
        _ => System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported ? "NEON" : "SSE2",
    };

    /// <summary>Context settings with the SIMD level set from the running CPU rather than assumed.</summary>
    public static IPLContextSettings DefaultContextSettings() => new()
    {
        version = STEAMAUDIO_VERSION,
        simdLevel = DetectSimdLevel(),
        flags = 0,
    };

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplContextCreate(ref IPLContextSettings settings, out IntPtr context);
    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplContextRelease(ref IntPtr context);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplHRTFCreate(IntPtr context, ref IPLAudioSettings audioSettings, ref IPLHRTFSettings hrtfSettings, out IntPtr hrtf);
    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplHRTFRelease(ref IntPtr hrtf);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplBinauralEffectCreate(IntPtr context, ref IPLAudioSettings audioSettings, ref IPLBinauralEffectSettings effectSettings, out IntPtr effect);
    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplBinauralEffectApply(IntPtr effect, ref IPLBinauralEffectParams effectParams, ref IPLAudioBuffer inBuf, ref IPLAudioBuffer outBuf);
    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplBinauralEffectReset(IntPtr effect);
    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplBinauralEffectRelease(ref IntPtr effect);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplAudioBufferAllocate(IntPtr context, int numChannels, int numSamples, ref IPLAudioBuffer audioBuffer);
    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplAudioBufferFree(IntPtr context, ref IPLAudioBuffer audioBuffer);
    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplAudioBufferInterleave(IntPtr context, ref IPLAudioBuffer src, float[] dst);
    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplAudioBufferDeinterleave(IntPtr context, float[] src, ref IPLAudioBuffer dst);
}
