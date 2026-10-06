using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --wide-sources cost: what heard-across-their-extent costs, one thread, per second of audio.
///
///   * The synths: a tree, the fire, the fountain and the rain over a roof and a near quarter, one place
///     against several (their streams written separately; the events are the same, only routed).
///   * The binaural stage: Steam Audio's HRTF per voice (and the ground's, which every placed voice has),
///     against the alternative of encoding the same streams into one second-order ambisonic field and
///     decoding it binaurally once (AmbisonicBedDsp's decode).
/// </summary>
public static class WideSourcesCost
{
    private const int Rate = 48000;

    public static int Run()
    {
        AcousticRegistry.Initialize();
        Console.WriteLine("Synthesis, one thread, % of a core per source (10 s of audio, best of 3):");
        Synth("tree, 1 place", 1, n => { var s = new FoliageSynth(FoliageSpec.ParkTree, Rate, 5, n) { Wind = 4.5f, Spread = 1f }; return (s.NextPlaces, () => s.Control(256f / Rate)); });
        Synth("tree, 7 places", 7, n => { var s = new FoliageSynth(FoliageSpec.ParkTree, Rate, 5, n) { Wind = 4.5f, Spread = 1f }; return (s.NextPlaces, () => s.Control(256f / Rate)); });
        Synth("fire, 1 place", 1, n => { var s = new FireSynth(FireSpec.GardenFirePit, Rate, 5, n) { Wind = 1f, Spread = 1f }; return (s.NextPlaces, () => s.Control(256f / Rate)); });
        Synth("fire, 4 places", 4, n => { var s = new FireSynth(FireSpec.GardenFirePit, Rate, 5, n) { Wind = 1f, Spread = 1f }; return (s.NextPlaces, () => s.Control(256f / Rate)); });
        Synth("fountain, 5 taps x 1", 5, n => { var s = new FallingWaterSynth(WaterFeatureSpec.ParkFountain, Rate, 5, 1) { Wind = 2f }; return (s.NextPlaces, () => s.Control(256f / Rate)); });
        Synth("fountain, 5 taps x 4", 20, n =>
        {
            var s = new FallingWaterSynth(WaterFeatureSpec.ParkFountain, Rate, 5, 4) { Wind = 2f };
            for (int t = 0; t < 5; t++) s.SetSpread(t, 1f);
            return (s.NextPlaces, () => s.Control(256f / Rate));
        });

        RainSpike._riding = -1;
        var (shelter, shelterEar, _) = RainSpike.Scenes().First(x => x.Name == "shelter").Make();
        var roof = new OpenFPS.Client.Core.RainSurvey().Run(shelter, shelterEar, -1, -1).Patches[OpenFPS.Client.Core.RainSurvey.OverheadSlot]!;
        var (street, streetEar, _) = RainSpike.Scenes().First(x => x.Name == "street").Make();
        var near = new OpenFPS.Client.Core.RainSurvey().Run(street, streetEar, -1, -1).Patches[2]!;
        Rain("bus shelter roof, 1 voice", roof, 1);
        Rain("bus shelter roof, 4 parts", roof, 4);
        Rain("near street quarter, 1 voice", near, 1);
        Rain("near street quarter, 2 parts", near, 2);

        Console.WriteLine();
        Console.WriteLine("Binaural, one thread, % of a core (10 s at 48 kHz in 1024-sample frames):");
        Binaural();
        return 0;
    }

    private static double Seconds(Action run)
    {
        double best = double.MaxValue;
        for (int rep = 0; rep < 3; rep++)
        {
            var sw = Stopwatch.StartNew();
            run();
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }
        return best;
    }

    private static void Synth(string name, int places, Func<int, (SpanAction Next, Action Control)> make)
    {
        var buf = new float[places];
        double s = Seconds(() =>
        {
            var (next, control) = make(places);
            for (int i = 0; i < Rate * 10; i++)
            {
                if (i % 256 == 0) control();
                next(buf);
            }
        });
        Console.WriteLine($"  {name,-32} {s / 10 * 100,6:F2} %");
    }

    public delegate void SpanAction(Span<float> places);

    private static void Rain(string name, RainPatch patch, int parts)
    {
        var share = parts > 1 ? patch.Share(1f / parts) : patch;
        double s = Seconds(() =>
        {
            for (int k = 0; k < parts; k++)
            {
                var synth = new RainSynth(Rate, 9 + k) { Patch = share, RainRate = Rainfall.ModerateRate };
                for (int i = 0; i < Rate * 10; i++) synth.Next();
            }
        });
        Console.WriteLine($"  {name,-32} {s / 10 * 100,6:F2} %");
    }

    private static void Binaural()
    {
        const int frame = 1024;
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("  no Steam Audio context"); return; }
        var au = new Phonon.IPLAudioSettings { samplingRate = Rate, frameSize = frame };
        var hs = new Phonon.IPLHRTFSettings { type = Phonon.IPL_HRTFTYPE_DEFAULT, volume = 1f, normType = Phonon.IPL_HRTFNORMTYPE_NONE };
        Phonon.iplHRTFCreate(ctx, ref au, ref hs, out IntPtr hrtf);
        var rng = new Random(3);
        var input = new float[frame];
        for (int i = 0; i < frame; i++) input[i] = (float)(rng.NextDouble() * 2 - 1) * 0.1f;
        int frames = Rate * 10 / frame;

        foreach (int voices in new[] { 1, 7, 20 })
        {
            var es = new Phonon.IPLBinauralEffectSettings { hrtf = hrtf };
            var effects = new IntPtr[voices * 2];
            for (int v = 0; v < effects.Length; v++) Phonon.iplBinauralEffectCreate(ctx, ref au, ref es, out effects[v]);
            var inB = new Phonon.IPLAudioBuffer(); var outB = new Phonon.IPLAudioBuffer();
            Phonon.iplAudioBufferAllocate(ctx, 1, frame, ref inB);
            Phonon.iplAudioBufferAllocate(ctx, 2, frame, ref outB);
            Phonon.iplAudioBufferDeinterleave(ctx, input, ref inB);
            double s = Seconds(() =>
            {
                for (int f = 0; f < frames; f++)
                    for (int v = 0; v < effects.Length; v++)
                    {
                        float a = 0.3f * v + f * 0.01f;
                        var prm = new Phonon.IPLBinauralEffectParams
                        {
                            direction = new Phonon.IPLVector3 { x = MathF.Sin(a), y = 0.2f, z = -MathF.Cos(a) },
                            interpolation = Phonon.IPL_HRTFINTERPOLATION_BILINEAR, spatialBlend = 1f, hrtf = hrtf, peakDelays = IntPtr.Zero,
                        };
                        Phonon.iplBinauralEffectApply(effects[v], ref prm, ref inB, ref outB);
                    }
            });
            Console.WriteLine($"  {voices,2} voice(s), HRTF and the ground's HRTF each     {s / 10 * 100,6:F2} %");
            foreach (var e in effects) { var x = e; Phonon.iplBinauralEffectRelease(ref x); }
            Phonon.iplAudioBufferFree(ctx, ref inB);
            Phonon.iplAudioBufferFree(ctx, ref outB);
        }

        foreach (int order in new[] { 1, 2 })
        {
            int ch = (order + 1) * (order + 1);
            var ens = new Phonon.IPLAmbisonicsEncodeEffectSettings { maxOrder = order };
            var ds = new Phonon.IPLAmbisonicsDecodeEffectSettings { speakerLayout = Phonon.StereoLayout(), hrtf = hrtf, maxOrder = order };
            Phonon.iplAmbisonicsDecodeEffectCreate(ctx, ref au, ref ds, out IntPtr decode);
            foreach (int voices in new[] { 7, 20 })
            {
                var encs = new IntPtr[voices];
                for (int v = 0; v < voices; v++) Phonon.iplAmbisonicsEncodeEffectCreate(ctx, ref au, ref ens, out encs[v]);
                var inB = new Phonon.IPLAudioBuffer(); var amb = new Phonon.IPLAudioBuffer(); var sum = new Phonon.IPLAudioBuffer(); var outB = new Phonon.IPLAudioBuffer();
                Phonon.iplAudioBufferAllocate(ctx, 1, frame, ref inB);
                Phonon.iplAudioBufferAllocate(ctx, ch, frame, ref amb);
                Phonon.iplAudioBufferAllocate(ctx, ch, frame, ref sum);
                Phonon.iplAudioBufferAllocate(ctx, 2, frame, ref outB);
                Phonon.iplAudioBufferDeinterleave(ctx, input, ref inB);
                var one = new float[frame * ch];
                var all = new float[frame * ch];
                double s = Seconds(() =>
                {
                    for (int f = 0; f < frames; f++)
                    {
                        Array.Clear(all);
                        for (int v = 0; v < voices; v++)
                        {
                            float a = 0.3f * v + f * 0.01f;
                            var ep = new Phonon.IPLAmbisonicsEncodeEffectParams
                            {
                                direction = new Phonon.IPLVector3 { x = MathF.Sin(a), y = 0.2f, z = -MathF.Cos(a) }, order = order,
                            };
                            Phonon.iplAmbisonicsEncodeEffectApply(encs[v], ref ep, ref inB, ref amb);
                            Phonon.iplAudioBufferInterleave(ctx, ref amb, one);
                            for (int i = 0; i < all.Length; i++) all[i] += one[i];
                        }
                        Phonon.iplAudioBufferDeinterleave(ctx, all, ref sum);
                        var dp = new Phonon.IPLAmbisonicsDecodeEffectParams
                        {
                            order = order, hrtf = hrtf, orientation = Phonon.ListenerFrame(Quaternion.Identity), binaural = Phonon.IPL_TRUE,
                        };
                        Phonon.iplAmbisonicsDecodeEffectApply(decode, ref dp, ref sum, ref outB);
                    }
                });
                Console.WriteLine($"  {voices,2} streams into order {order} ({ch} ch), one binaural decode   {s / 10 * 100,6:F2} %");
                foreach (var e in encs) { var x = e; Phonon.iplAmbisonicsEncodeEffectRelease(ref x); }
                Phonon.iplAudioBufferFree(ctx, ref inB); Phonon.iplAudioBufferFree(ctx, ref amb);
                Phonon.iplAudioBufferFree(ctx, ref sum); Phonon.iplAudioBufferFree(ctx, ref outB);
            }
        }
    }
}
