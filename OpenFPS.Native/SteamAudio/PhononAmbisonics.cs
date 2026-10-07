using System.Runtime.InteropServices;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Ambisonics bindings for the Steam Audio (phonon) C API: encode, and the decode effect that turns a
/// soundfield into a pair of ears, rotated by the listener's orientation first so the world stays put
/// while the player turns. Steam Audio is -z forward; see <see cref="World"/>.
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

    // IPLCoordinateSpace3 is declared in PhononSim.cs. Its field order (right, up, ahead, origin) must
    // match phonon.h: wrong, and the soundfield rotates the wrong way with nothing to tell you so.

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

    /// <summary>Encodes a mono signal into a soundfield from a direction. The decode path is tested with
    /// fields from this encoder, so the test checks the round trip, not a guess at the spherical-harmonic
    /// convention.</summary>
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
    /// A point or direction of the game's world in Steam Audio's: z the other way (the game is +z
    /// forward, Steam Audio -z). Every world coordinate handed to Steam Audio goes through here, or a
    /// traced response is decoded facing backwards while occlusion and pathing still look right
    /// (changes.md, 2026-09-29).
    /// </summary>
    /// <remarks>SA_MIRROR=0 hands the world over unflipped, for the lab's <c>--sa-frame</c> check
    /// and nothing else.</remarks>
    public static IPLVector3 World(System.Numerics.Vector3 v) => new IPLVector3 { x = v.X, y = v.Y, z = MirrorZ ? -v.Z : v.Z };
    /// <summary>The z component of a Steam Audio world direction, in the game's world.</summary>
    public static float WorldZ(float saZ) => MirrorZ ? -saZ : saZ;
    internal static readonly bool MirrorZ = Environment.GetEnvironmentVariable("SA_MIRROR") != "0";

    /// <summary>
    /// The listener's own axes as world-space directions, z flipped: what Steam Audio asks for to keep
    /// the soundfield fixed in the world while the listener turns.
    /// </summary>
    public static IPLCoordinateSpace3 ListenerFrame(System.Numerics.Quaternion listenerRotation)
    {
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
