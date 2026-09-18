using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Rail;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// A train going past somebody standing by the line.
///
///   --train [preset ...] [speed=m/s] [offset=m] [sec=s] [notch=0..8] [cars=N] [horn=0|1] [stems]
///
/// Rendered the honest way, the same as the aircraft flyover: every source is integrated in the
/// TRAIN's time and every sample is deposited at the moment it ARRIVES — emission time plus the path
/// over the speed of sound — with the inverse-distance gain and the air's absorption for that path.
/// Doppler is not applied; it happens. And because a train is sixty sources spread over a couple of
/// hundred metres rather than one source at a place, three things come out that no point-source
/// rendering can give you: the level RISES TO A PLATEAU instead of a peak, because a line source
/// falls off three decibels a doubling and not six; the clatter SWEEPS along the train past you as
/// each bogie's bangs arrive from a different place; and the far end of a long train is DULLER than
/// the near end, because the air has had four hundred metres to take the top off it.
/// </summary>
public static class TrainSpike
{
    private const int Sr = VehicleSynth.SampleRate;

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        float speed = Arg(args, "speed", -1f), offset = Arg(args, "offset", 12f);
        float seconds = Arg(args, "sec", -1f), notch = Arg(args, "notch", 7f);
        int cars = (int)Arg(args, "cars", -1f);
        bool horn = Arg(args, "horn", 1f) > 0.5f;
        bool stems = args.Contains("stems");
        bool binaural = args.Contains("binaural");

        var presets = args.Where(a => ModelLibrary.Knows(ModelLibrary.Kinds.Train, a)).ToList();
        if (presets.Count == 0) presets = ModelLibrary.Ids(ModelLibrary.Kinds.Train).ToList();

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "TRAINS");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  Trains, from the lineside.\n");

        foreach (var key in presets)
        {
            var p = ModelLibrary.Train(key);
            // Only rewrite a consist that HAS a long tail of like vehicles: "cars=18" means
            // eighteen wagons behind the locomotives, not eighteen trams.
            if (cars > 0 && p.Consist[^1].Count > 4)
            {
                var consist = p.Consist.ToArray();
                consist[^1] = consist[^1] with { Count = cars };
                p = p with { Consist = consist };
            }
            float v = speed > 0 ? speed : p.TypicalSpeedMps;
            // Long enough for the whole train plus the approach and the going away.
            float sec = seconds > 0 ? seconds : Math.Clamp(p.LengthMetres / v + 22f, 18f, 150f);

            var train = new TrainSynth(p, Sr, 41) { Speed = v, Notch = notch };
            Console.WriteLine($"  {key}");
            foreach (var line in train.Describe(v)) Console.WriteLine($"    {line}");
            Console.WriteLine($"    pass: {v:F0} m/s ({v * 3.6f:F0} km/h), listener {offset:F0} m from the track, {sec:F0} s");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = binaural ? PassByBinaural(train, v, offset, sec, horn, dir, key) : PassBy(train, v, offset, sec, horn, stems);
            Console.WriteLine($"    at the ear: {r.ClosestDb:F0} dB as it passes, peak {r.PeakDb:F0} dB   ({sw.Elapsed.TotalSeconds:F0} s to render)");
            // Averaged over windows spread across the whole pass, NOT the first four thousand
            // samples — which on a pass-by are the train still being a mile away.
            var offsets = Enumerable.Range(1, 40).Select(i => (int)(r.Mix.Length * i / 42f)).ToArray();
            Console.WriteLine("    bands: " + string.Join("  ", Spectrum.AverageBandsDb(r.Mix, Sr, offsets).Select((d, i) => $"{Spectrum.BandEdges[i]:F0}:{d:F0}")));
            foreach (var (name, pcm) in r.Stems)
            {
                float sp = 0f; foreach (var x in pcm) sp = MathF.Max(sp, MathF.Abs(x));
                Console.WriteLine($"      {name,-9} peak {20f * MathF.Log10(MathF.Max(1e-9f, sp) / 2e-5f):F0} dB   "
                    + string.Join(" ", Spectrum.AverageBandsDb(pcm, Sr, offsets).Select((d, i) => $"{Spectrum.BandEdges[i]:F0}:{d:F0}")));
            }

            if (!binaural) Write(dir, $"train_{key}", r.Mix);
            if (stems)
                foreach (var (name, pcm) in r.Stems)
                    Write(dir, $"train_{key}_{name}", pcm, r.Peak);
            Console.WriteLine();
        }
        return 0;
    }

    private static void Write(string dir, string name, float[] pcm, float peakOverride = 0f)
    {
        float peak = peakOverride;
        if (peak <= 0f) foreach (var x in pcm) peak = MathF.Max(peak, MathF.Abs(x));
        var wav = new float[pcm.Length];
        float g = peak > 1e-12f ? 0.89f / peak : 0f;
        for (int i = 0; i < wav.Length; i++) wav[i] = pcm[i] * g;
        File.WriteAllBytes(Path.Combine(dir, name + ".wav"), VehicleSynth.ToWav16(wav));
        Console.WriteLine($"    wrote {Path.Combine(dir, name + ".wav")}");
    }

    private sealed class Result
    {
        public float[] Mix = Array.Empty<float>();
        public List<(string Name, float[] Pcm)> Stems = new();
        public float PeakDb, ClosestDb, Peak;
    }

    /// <summary>Which layer a source belongs to, for the stems. A quiet layer buried under a loud
    /// one is indistinguishable from a missing one, which is the whole reason for rendering them
    /// apart.</summary>
    private static int LayerOf(string label)
    {
        if (label.Contains("horn") || label.Contains("whistle") || label.Contains("bell")) return 2;
        if (label.Contains("stack") || label.Contains("fans") || label.Contains("traction") || label.Contains("chimney")) return 1;
        return 0;
    }
    private static readonly string[] LayerNames = { "rolling", "traction", "signals" };

    private static Result PassBy(TrainSynth train, float speed, float offset, float seconds, bool horn, bool stems)
    {
        const float c = 343f;
        int n = (int)(seconds * Sr);
        var ear = new Vector3(0f, 1.6f, offset);
        var earImage = new Vector3(0f, -1.6f, offset);
        int tail = Sr * 3;
        var mix = new float[n + tail];
        var layers = new float[3][];
        for (int i = 0; i < 3; i++) layers[i] = stems ? new float[n + tail] : mix;

        var sources = train.Sources;
        int ns = sources.Count;
        var layerOf = new int[ns];
        var alongs = new float[ns];
        var heights = new float[ns];
        var extents = new float[ns];
        // Per-source filter state for the air and for the ground bounce.
        var d1 = new float[ns]; var d2 = new float[ns]; var g1 = new float[ns]; var g2 = new float[ns];
        for (int i = 0; i < ns; i++)
        {
            layerOf[i] = LayerOf(sources[i].Label);
            alongs[i] = sources[i].AlongMetres;
            heights[i] = sources[i].HeightMetres;
            extents[i] = MathF.Max(0.5f, sources[i].ExtentMetres);
        }

        // Start with the head of the train far enough back that it arrives when it should: it is
        // abeam the listener half way through.
        float half = seconds * 0.5f;
        train.Place(-speed * half);

        // The grade-crossing signal: two long, one short, and one long held through the crossing.
        // A quarter mile out at this speed is where the rule says to start.
        float hornStart = half - 400f / MathF.Max(1f, speed);
        var phrase = new (float At, float For)[]
        {
            (hornStart, 2.2f), (hornStart + 2.9f, 2.2f), (hornStart + 5.8f, 0.7f), (hornStart + 7.2f, 4.5f),
        };
        train.BellRinging = true;

        for (int k = 0; k < n; k++)
        {
            float t = k / (float)Sr;
            if (horn)
            {
                bool on = false;
                foreach (var (at, dur) in phrase) if (t >= at && t < at + dur) { on = true; break; }
                train.HornBlowing = on;
                train.WhistleBlowing = on;
            }
            if ((k & 63) == 0)
            {
                // Where the ear is in the train's frame, for the horn's directivity.
                float headX = (float)train.HeadMetres;
                train.SetListener(new Vector3(0f - headX, ear.Y - 4.8f, offset));
            }
            train.Step();
            float headNow = (float)train.HeadMetres;

            for (int i = 0; i < ns; i++)
            {
                float s = sources[i].Out;
                if (s == 0f) continue;
                float x = headNow - alongs[i];
                float dx = x - ear.X, dy = heights[i] - ear.Y, dz = 0f - ear.Z;
                float r = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                // A source has a size: inside its own extent the inverse distance stops.
                float rEff = MathF.Max(extents[i], r);
                float fc = AirCorner(r);
                float a = 1f - MathF.Exp(-2f * MathF.PI * fc / Sr);
                d1[i] += a * (s - d1[i]); d2[i] += a * (d1[i] - d2[i]);
                float direct = d2[i] / rEff;
                Deposit(layers[layerOf[i]], (t + r / c) * Sr, direct);
                if (stems) Deposit(mix, (t + r / c) * Sr, direct);

                // Off the ground beside the track: ballast and grass keep about half of it and
                // rather less of the top.
                float dy2 = heights[i] - earImage.Y;
                float rg = MathF.Sqrt(dx * dx + dy2 * dy2 + dz * dz);
                float ag = 1f - MathF.Exp(-2f * MathF.PI * (AirCorner(rg) * 0.55f) / Sr);
                g1[i] += ag * (s - g1[i]); g2[i] += ag * (g1[i] - g2[i]);
                float bounce = 0.5f * g2[i] / MathF.Max(extents[i], rg);
                Deposit(layers[layerOf[i]], (t + rg / c) * Sr, bounce);
                if (stems) Deposit(mix, (t + rg / c) * Sr, bounce);


            }
        }

        // The level at the ear as it passes: the RMS of what actually ARRIVED, over a second
        // either side of the moment the middle of the train is abeam. Summing each source's own
        // contribution and averaging them (which is what this did first) measures the mean of the
        // sources rather than the sound of the train, and reads about twenty decibels low.
        int from = Math.Max(0, (int)((half + train.Profile.LengthMetres * 0.5f / speed - 1f) * Sr));
        int to = Math.Min(mix.Length, from + 2 * Sr);
        double closestP2 = 0; int closeCount = 0;
        for (int i = from; i < to; i++) { closestP2 += mix[i] * (double)mix[i]; closeCount++; }

        float peak = 0f;
        foreach (var x in mix) peak = MathF.Max(peak, MathF.Abs(x));
        var res = new Result
        {
            Mix = mix,
            Peak = peak,
            PeakDb = 20f * MathF.Log10(MathF.Max(1e-9f, peak) / 2e-5f),
            ClosestDb = 10f * MathF.Log10((float)Math.Max(1e-20, closestP2 / Math.Max(1, closeCount)) / (2e-5f * 2e-5f)),
        };
        if (stems) for (int i = 0; i < 3; i++) res.Stems.Add((LayerNames[i], layers[i]));
        return res;
    }

    /// <summary>
    /// The same pass-by, heard by two ears instead of one. The listener stands facing the track, so
    /// the train comes from the left and goes to the right — and the sweep is not a pan: each ear
    /// gets its own arrival time, so the interaural delay slides continuously through the pass the
    /// way it does in life.
    /// </summary>
    private static Result PassByBinaural(TrainSynth train, float speed, float offset, float seconds,
                                         bool horn, string dir, string key)
    {
        int n = (int)(seconds * Sr);
        var ear = new Vector3(0f, 1.6f, 0f);
        var (earL, earR) = CrossingSpike.HeadShadowEars(ear);
        var chL = new float[n + Sr * 3];
        var chR = new float[n + Sr * 3];
        var st = train.Sources.Select(_ => new CrossingSpike.EarState(Sr)).ToArray();

        float half = seconds * 0.5f;
        train.Place(-speed * half);
        train.BellRinging = true;
        float hornStart = half - 400f / MathF.Max(1f, speed);
        var phrase = new (float At, float For)[]
        {
            (hornStart, 2.2f), (hornStart + 2.9f, 2.2f), (hornStart + 5.8f, 0.7f), (hornStart + 7.2f, 4.5f),
        };

        for (int k = 0; k < n; k++)
        {
            float t = k / (float)Sr;
            if (horn)
            {
                bool on = false;
                foreach (var (at, dur) in phrase) if (t >= at && t < at + dur) { on = true; break; }
                train.HornBlowing = on;
                train.WhistleBlowing = on;
            }
            if ((k & 63) == 0)
                train.SetListener(new Vector3(0f - (float)train.HeadMetres, ear.Y - 4.8f, offset));
            train.Step();
            float head = (float)train.HeadMetres;
            for (int i = 0; i < train.Sources.Count; i++)
            {
                var src = train.Sources[i];
                float s = src.Out;
                if (s == 0f) continue;
                var at = new Vector3(head - src.AlongMetres, src.HeightMetres, offset);
                CrossingSpike.DepositBinaural(chL, chR, t, s, at, ear, earL, earR,
                                              MathF.Max(0.5f, src.ExtentMetres), st[i], Sr);
            }
        }

        float peak = 0f;
        for (int i = 0; i < chL.Length; i++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(chL[i]), MathF.Abs(chR[i])));
        int from = Math.Max(0, (int)((half + train.Profile.LengthMetres * 0.5f / speed - 1f) * Sr));
        int to = Math.Min(chL.Length, from + 2 * Sr);
        double p2 = 0; int pn = 0;
        for (int i = from; i < to; i++) { p2 += 0.5 * (chL[i] * (double)chL[i] + chR[i] * (double)chR[i]); pn++; }

        float g = peak > 1e-12f ? 0.89f / peak : 0f;
        var wl = new float[chL.Length]; var wr = new float[chR.Length];
        for (int i = 0; i < wl.Length; i++) { wl[i] = chL[i] * g; wr[i] = chR[i] * g; }
        string path = Path.Combine(dir, $"train_{key}_binaural.wav");
        File.WriteAllBytes(path, CrossingSpike.ToWav16Stereo(wl, wr, Sr));
        Console.WriteLine($"    wrote {path}  (two ears, own arrival time each)");

        return new Result
        {
            Mix = chL,
            Peak = peak,
            PeakDb = 20f * MathF.Log10(MathF.Max(1e-9f, peak) / 2e-5f),
            ClosestDb = 10f * MathF.Log10((float)Math.Max(1e-20, p2 / Math.Max(1, pn)) / (2e-5f * 2e-5f)),
        };
    }

    private static float AirCorner(float r)
        => Math.Clamp(4000f * MathF.Pow(100f / MathF.Max(1f, r), 0.59f), 300f, 18000f);

    private static void Deposit(float[] buf, float at, float v)
    {
        int i = (int)at;
        if (i < 0 || i + 1 >= buf.Length) return;
        float f = at - i;
        buf[i] += v * (1f - f);
        buf[i + 1] += v * f;
    }

    public static float Arg(string[] args, string key, float fallback)
    {
        foreach (var a in args)
        {
            int eq = a.IndexOf('=');
            if (eq > 0 && a[..eq] == key && float.TryParse(a[(eq + 1)..], out float v)) return v;
        }
        return fallback;
    }
}
