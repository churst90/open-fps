using System;
using System.Runtime.InteropServices;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Ambisonics bindings for the Steam Audio (phonon) C API — the decode effect, which is what turns a
/// recorded soundfield into a pair of ears.
///
/// This is the half of Steam Audio that makes an ambience bed work in a game at all. A binaural
/// RECORDING is head-locked: the spatial image is baked to the orientation of the head that recorded
/// it, so the bird on your left stays on your left after you turn around. A first-order ambisonic
/// recording instead describes sound arriving from every direction as a field, and
/// <c>iplAmbisonicsDecodeEffectApply</c> rotates that field by the listener's orientation BEFORE
/// decoding it to binaural — so the world stays put while the player turns in it.
///
/// See <see cref="Phonon"/> for the coordinate convention (+x right, +y up, -z FORWARD; the game uses
/// +z forward, so every direction handed to this API is converted on the way in).
/// </summary>
internal static partial class Phonon
{
    // IPLSpeakerLayoutType
    public const int IPL_SPEAKERLAYOUTTYPE_MONO = 0;
    public const int IPL_SPEAKERLAYOUTTYPE_STEREO = 1;
    public const int IPL_SPEAKERLAYOUTTYPE_QUADRAPHONIC = 2;
    public const int IPL_SPEAKERLAYOUTTYPE_SURROUND_5_1 = 3;
    public const int IPL_SPEAKERLAYOUTTYPE_SURROUND_7_1 = 4;
    public const int IPL_SPEAKERLAYOUTTYPE_CUSTOM = 5;

    // IPLbool
    public const int IPL_FALSE = 0;
    public const int IPL_TRUE = 1;

    // IPLAudioEffectState
    public const int IPL_AUDIOEFFECTSTATE_TAILREMAINING = 0;
    public const int IPL_AUDIOEFFECTSTATE_TAILCOMPLETE = 1;

    // IPLCoordinateSpace3 (right, up, ahead, origin) is already declared in PhononSim.cs and its field
    // order matches phonon.h. That order is load-bearing: get it wrong and the soundfield rotates the
    // wrong way with nothing to tell you so.

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLSpeakerLayout
    {
        public int type;          // IPLSpeakerLayoutType
        public int numSpeakers;
        public IntPtr speakers;   // IPLVector3* (null for the standard layouts)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAmbisonicsDecodeEffectSettings
    {
        public IPLSpeakerLayout speakerLayout;
        public IntPtr hrtf;       // IPLHRTF
        public int maxOrder;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAmbisonicsDecodeEffectParams
    {
        public int order;
        public IntPtr hrtf;                     // IPLHRTF
        public IPLCoordinateSpace3 orientation; // the LISTENER's frame, relative to the soundfield
        public int binaural;                    // IPLbool
    }

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplAmbisonicsDecodeEffectCreate(
        IntPtr context, ref IPLAudioSettings audioSettings,
        ref IPLAmbisonicsDecodeEffectSettings effectSettings, out IntPtr effect);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplAmbisonicsDecodeEffectApply(
        IntPtr effect, ref IPLAmbisonicsDecodeEffectParams effectParams,
        ref IPLAudioBuffer inBuf, ref IPLAudioBuffer outBuf);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplAmbisonicsDecodeEffectReset(IntPtr effect);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplAmbisonicsDecodeEffectRelease(ref IntPtr effect);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplAmbisonicsDecodeEffectGetTailSize(IntPtr effect);

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAmbisonicsEncodeEffectSettings { public int maxOrder; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAmbisonicsEncodeEffectParams
    {
        public IPLVector3 direction;  // source direction, in the LISTENER's frame
        public int order;
    }

    /// <summary>Encodes a mono signal into a soundfield from a given direction. Not needed to PLAY a
    /// recorded bed — but it is how a mono point source joins an ambisonic bus later, and it is how the
    /// decode path is verified: generating the test field with Steam Audio's own encoder tests the
    /// round trip rather than testing a guess at its spherical-harmonic convention.</summary>
    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplAmbisonicsEncodeEffectCreate(
        IntPtr context, ref IPLAudioSettings audioSettings,
        ref IPLAmbisonicsEncodeEffectSettings effectSettings, out IntPtr effect);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int iplAmbisonicsEncodeEffectApply(
        IntPtr effect, ref IPLAmbisonicsEncodeEffectParams effectParams,
        ref IPLAudioBuffer inBuf, ref IPLAudioBuffer outBuf);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplAmbisonicsEncodeEffectReset(IntPtr effect);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern void iplAmbisonicsEncodeEffectRelease(ref IntPtr effect);

    /// <summary>A stereo (headphone) output layout — the only one this game has any use for.</summary>
    public static IPLSpeakerLayout StereoLayout() => new()
    {
        type = IPL_SPEAKERLAYOUTTYPE_STEREO,
        numSpeakers = 2,
        speakers = IntPtr.Zero
    };

    /// <summary>
    /// The listener's frame of reference in the SOUNDFIELD's coordinates, built from the game's
    /// listener rotation.
    ///
    /// Steam Audio's documentation is explicit about which way round this goes: to keep the soundfield
    /// fixed in world space while the listener looks around, you pass the listener's own axes as world
    /// -space direction vectors. The effect works out the rotation from that.
    /// </summary>
    public static IPLCoordinateSpace3 ListenerFrame(System.Numerics.Quaternion listenerRotation)
    {
        // Game frame: +x right, +y up, +z forward. Steam Audio: +x right, +y up, -z forward.
        // Flipping the z component of each axis is the whole of the conversion.
        var right = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, listenerRotation);
        var up = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, listenerRotation);
        var ahead = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, listenerRotation);

        return new IPLCoordinateSpace3
        {
            right = new IPLVector3 { x = right.X, y = right.Y, z = -right.Z },
            up = new IPLVector3 { x = up.X, y = up.Y, z = -up.Z },
            ahead = new IPLVector3 { x = ahead.X, y = ahead.Y, z = -ahead.Z },
            origin = new IPLVector3 { x = 0f, y = 0f, z = 0f }
        };
    }
}
