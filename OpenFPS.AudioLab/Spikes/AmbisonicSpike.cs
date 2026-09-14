using System;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Verifies the ambisonics bindings end to end, headless: encode a mono tone into a soundfield from a
/// known direction, decode it binaurally with a given listener orientation, and check which ear it
/// comes out of.
///
/// This is the test that matters for a P/Invoke layer, because every way of getting it wrong is silent.
/// A struct field in the wrong order, a coordinate handedness left unconverted, a rotation applied
/// backwards — none of them return an error. They return audio that is confidently pointing the wrong
/// way, and by ear that is very nearly indistinguishable from working.
///
/// So the assertions are about DIRECTION, not about whether sound came out:
///   1. A source to the listener's left decodes louder in the LEFT ear.
///   2. A source to the right decodes louder in the RIGHT ear.
///   3. Turning the listener 180° swaps which ear a fixed source arrives in. This is the whole reason
///      the beds are ambisonic instead of binaural recordings, so it is the assertion that earns its
///      keep: a head-locked recording would fail it while sounding perfectly fine.
///   4. Facing the source head-on leaves the ears balanced.
/// </summary>
public static class AmbisonicSpike
{
    private const int SampleRate = 44100;
    private const int FrameSize = 1024;
    private const int Order = 1;           // first order: 4 channels, plenty for a diffuse bed

    public static int Run()
    {
        AcousticRegistry.Initialize();

        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS)
        { Console.WriteLine("FAIL: context create failed."); return 1; }

        var au = new Phonon.IPLAudioSettings { samplingRate = SampleRate, frameSize = FrameSize };
        var hs = new Phonon.IPLHRTFSettings
        {
            type = Phonon.IPL_HRTFTYPE_DEFAULT,
            volume = 1f,
            normType = Phonon.IPL_HRTFNORMTYPE_NONE
        };
        if (Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf) != Phonon.IPL_STATUS_SUCCESS)
        { Console.WriteLine("FAIL: HRTF create failed."); Phonon.iplContextRelease(ref ctx); return 1; }

        try
        {
            Console.WriteLine($"  Steam Audio SIMD: {Phonon.SimdLevelName(cs.simdLevel)}, order {Order} " +
                              $"({AmbisonicFormat.ChannelsForOrder(Order)} channels), frame {FrameSize}.");

            // Facing +Z in game terms: the identity rotation.
            var facingForward = Quaternion.Identity;
            var facingBackward = Quaternion.CreateFromYawPitchRoll(MathF.PI, 0f, 0f);
            var facingLeft = Quaternion.CreateFromYawPitchRoll(MathF.PI / 2f, 0f, 0f);

            // Steam Audio's frame: +x right, +y up, -z forward. A source off to the left is -x.
            var toTheLeft = new Phonon.IPLVector3 { x = -1f, y = 0f, z = 0f };
            var toTheRight = new Phonon.IPLVector3 { x = 1f, y = 0f, z = 0f };

            var leftFacingForward = Render(ctx, hrtf, toTheLeft, facingForward);
            var rightFacingForward = Render(ctx, hrtf, toTheRight, facingForward);
            var leftFacingBackward = Render(ctx, hrtf, toTheLeft, facingBackward);
            var leftFacingLeft = Render(ctx, hrtf, toTheLeft, facingLeft);

            Report("source left,  facing forward ", leftFacingForward);
            Report("source right, facing forward ", rightFacingForward);
            Report("source left,  facing backward", leftFacingBackward);
            Report("source left,  facing it      ", leftFacingLeft);

            bool ok = true;
            ok &= Check("the decode produced audio at all", leftFacingForward.Energy > 1e-6f);
            ok &= Check("a source on the left decodes into the left ear",
                        leftFacingForward.L > leftFacingForward.R * 1.15f);
            ok &= Check("a source on the right decodes into the right ear",
                        rightFacingForward.R > rightFacingForward.L * 1.15f);

            // The one that separates a rotatable soundfield from a baked binaural recording.
            ok &= Check("turning around swaps which ear a FIXED source arrives in",
                        leftFacingBackward.R > leftFacingBackward.L * 1.15f);
            ok &= Check("turning around does not change how loud it is",
                        Ratio(leftFacingBackward.Energy, leftFacingForward.Energy) > 0.6f);

            // Facing the source: it is dead ahead, so neither ear should lead.
            ok &= Check("facing a source head-on leaves the ears balanced",
                        Ratio(leftFacingLeft.L, leftFacingLeft.R) > 0.7f);

            Console.WriteLine(ok
                ? "RESULT: PASS — the soundfield rotates with the listener and decodes to the right ear."
                : "RESULT: FAIL — see the unmet conditions above.");
            return ok ? 0 : 1;
        }
        finally
        {
            Phonon.iplHRTFRelease(ref hrtf);
            Phonon.iplContextRelease(ref ctx);
        }
    }

    private readonly record struct Ears(float L, float R)
    {
        public float Energy => L + R;
    }

    private static float Ratio(float a, float b)
    {
        float hi = MathF.Max(MathF.Abs(a), MathF.Abs(b));
        float lo = MathF.Min(MathF.Abs(a), MathF.Abs(b));
        return hi <= 1e-9f ? 1f : lo / hi;
    }

    /// <summary>Encodes a tone from <paramref name="sourceDirection"/> into a soundfield, then decodes
    /// it for a listener with the given game-space rotation. Returns the two ears' RMS.</summary>
    private static Ears Render(IntPtr ctx, IntPtr hrtf, Phonon.IPLVector3 sourceDirection, Quaternion listenerRotation)
    {
        int channels = AmbisonicFormat.ChannelsForOrder(Order);
        var au = new Phonon.IPLAudioSettings { samplingRate = SampleRate, frameSize = FrameSize };

        var encSettings = new Phonon.IPLAmbisonicsEncodeEffectSettings { maxOrder = Order };
        Phonon.iplAmbisonicsEncodeEffectCreate(ctx, ref au, ref encSettings, out IntPtr encoder);

        var decSettings = new Phonon.IPLAmbisonicsDecodeEffectSettings
        {
            speakerLayout = Phonon.StereoLayout(),
            hrtf = hrtf,
            maxOrder = Order
        };
        Phonon.iplAmbisonicsDecodeEffectCreate(ctx, ref au, ref decSettings, out IntPtr decoder);

        Phonon.IPLAudioBuffer mono = default, field = default, ears = default;
        Phonon.iplAudioBufferAllocate(ctx, 1, FrameSize, ref mono);
        Phonon.iplAudioBufferAllocate(ctx, channels, FrameSize, ref field);
        Phonon.iplAudioBufferAllocate(ctx, 2, FrameSize, ref ears);

        var tone = new float[FrameSize];
        var stereo = new float[FrameSize * 2];

        double sumL = 0, sumR = 0;
        int blocks = 8; // let the HRTF's overlap-add settle before measuring
        int measured = 0;

        try
        {
            for (int b = 0; b < blocks; b++)
            {
                // Broadband-ish: a 440 Hz tone plus a little noise, so the HRTF has something to work
                // with across the spectrum rather than one frequency it might notch.
                var rnd = new Random(1000 + b);
                for (int i = 0; i < FrameSize; i++)
                {
                    double t = (b * FrameSize + i) / (double)SampleRate;
                    tone[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * t)
                            + 0.2f * (float)(rnd.NextDouble() * 2 - 1);
                }
                Phonon.iplAudioBufferDeinterleave(ctx, tone, ref mono);

                var encParams = new Phonon.IPLAmbisonicsEncodeEffectParams
                {
                    direction = sourceDirection,
                    order = Order
                };
                Phonon.iplAmbisonicsEncodeEffectApply(encoder, ref encParams, ref mono, ref field);

                var decParams = new Phonon.IPLAmbisonicsDecodeEffectParams
                {
                    order = Order,
                    hrtf = hrtf,
                    orientation = Phonon.ListenerFrame(listenerRotation),
                    binaural = Phonon.IPL_TRUE
                };
                Phonon.iplAmbisonicsDecodeEffectApply(decoder, ref decParams, ref field, ref ears);
                Phonon.iplAudioBufferInterleave(ctx, ref ears, stereo);

                if (b < 2) continue; // discard the settling blocks
                for (int i = 0; i < FrameSize; i++)
                {
                    sumL += stereo[i * 2] * (double)stereo[i * 2];
                    sumR += stereo[i * 2 + 1] * (double)stereo[i * 2 + 1];
                }
                measured += FrameSize;
            }
        }
        finally
        {
            Phonon.iplAudioBufferFree(ctx, ref mono);
            Phonon.iplAudioBufferFree(ctx, ref field);
            Phonon.iplAudioBufferFree(ctx, ref ears);
            Phonon.iplAmbisonicsEncodeEffectRelease(ref encoder);
            Phonon.iplAmbisonicsDecodeEffectRelease(ref decoder);
        }

        if (measured == 0) return new Ears(0f, 0f);
        return new Ears((float)Math.Sqrt(sumL / measured), (float)Math.Sqrt(sumR / measured));
    }

    private static void Report(string what, Ears e) =>
        Console.WriteLine($"  {what}  L/R = {e.L:F5} / {e.R:F5}   balance = {(e.L > e.R ? "LEFT" : "right")}");

    private static bool Check(string what, bool held)
    {
        Console.WriteLine($"  [{(held ? "PASS" : "FAIL")}] {what}");
        return held;
    }
}
