using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --nature: water, fire and the wind in leaves, rendered from their models and measured.
///
///   --nature levels [preset ...] [sec=30] [wind=4.5]   each model's level at a metre (Leq, LAeq, the
///                                                      loudest second), its octave bands, its texture
///                                                      statistics and what one voice costs a core.
///                                                      SourceLevelDb is read from this.
///   --nature render out=DIR [sec=30] [wind=]           one WAV per preset at a metre, dry, mono,
///                                                      one shared gain (−20 dBFS is 94 dB SPL), so
///                                                      files compare by level as well as by ear.
///   --nature live [sec=10] [dist=3]                   each model through the REAL FMOD voice path —
///                                                      the provider making a WaterVoiceState etc. from
///                                                      its "water:" key, its DSP, the mixer — one at a
///                                                      time at a distance, beside an air conditioner of
///                                                      known level placed the same way; the master is
///                                                      captured and each source's level read back.
///                                                      The differences must be what the loudness law
///                                                      says they should be.
///   --nature compare=FILE.wav [...]                    the same statistics for a recording, to set
///                                                      beside the model's. Recordings are a yardstick
///                                                      and are never played in the game.
///
/// The texture statistics are the ones the footstep rounds lacked: per octave, how PEAKY the band is
/// (kurtosis — a dense wash is 3, separate clicks are tens), how much its envelope moves (the
/// standard deviation of its 10 ms envelope over the mean), and how many separate transients a second
/// stand 12 dB clear of the local level. Octave levels alone passed renders the ear failed.
/// </summary>
public static class NatureSpike
{
    private const int Rate = 48000;
    private static readonly float[] Centres = { 31.5f, 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    /// <summary>−20 dBFS rms in the file is 94 dB SPL: a pascal is 0.1 of full scale.</summary>
    private const float PascalsToFull = 0.1f;

    /// <summary>parts=impact,drop,lump,plunge (water) or roar,crackle,steam,settle (fire): render
    /// only those parts.</summary>
    private static string[]? Parts;

    /// <summary>steady=M: hold the wind at M m/s instead of reading the field. turb=T: the field's
    /// turbulence intensity for this run.</summary>
    private static float? Steady;

    public static int Run(string[] args)
    {
        Steady = args.Any(a => a.StartsWith("steady=", StringComparison.Ordinal)) ? Arg(args, "steady=", 4f) : null;
        Parts = args.FirstOrDefault(a => a.StartsWith("parts=", StringComparison.Ordinal))?.Substring(6).Split(',');
        float sec = Arg(args, "sec=", 30f);
        float wind = Arg(args, "wind=", WindField.MeanSpeed);
        WindField.MeanSpeed = wind;
        if (args.Any(a => a.StartsWith("turb=", StringComparison.Ordinal))) WindField.Turbulence = Arg(args, "turb=", WindField.Turbulence);
        AcousticRegistry.Initialize();

        if (args.Contains("live")) return Live(Arg(args, "sec=", 10f), Arg(args, "dist=", 3f));

        var compares = args.Where(a => a.StartsWith("compare=", StringComparison.Ordinal)).Select(a => a[8..]).ToList();
        if (compares.Count > 0)
        {
            foreach (var path in compares)
            {
                var (pcm, sr) = ReadWav(path);
                Report(Path.GetFileName(path), pcm, sr, calibrated: false);
            }
            return 0;
        }

        var wanted = args.Where(a => !a.StartsWith("--") && !a.Contains('=') && a != "levels" && a != "render").ToList();
        var presets = new List<string>();
        foreach (var k in WaterFeatureSpec.Presets.Keys) presets.Add("water:" + k);
        foreach (var k in FireSpec.Presets.Keys) presets.Add("fire:" + k);
        foreach (var k in FoliageSpec.Presets.Keys) presets.Add("foliage:" + k);
        if (wanted.Count > 0) presets = presets.Where(p => wanted.Any(w => p.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();

        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?.Substring(4);
        if (dir != null) Directory.CreateDirectory(dir);

        foreach (var key in presets)
        {
            var sw = Stopwatch.StartNew();
            var (pa, declared, census) = Render(key, sec, seed: 7);
            double cost = sw.Elapsed.TotalSeconds / sec;
            Console.WriteLine();
            Console.WriteLine($"== {key}: declared {declared:F1} dB at 1 m, render costs {cost * 100:F1} % of a core");
            if (census != null) Console.WriteLine(census);
            Report(key, pa, Rate, calibrated: true);
            if (dir != null)
            {
                string path = Path.Combine(dir, key.Replace(':', '_') + ".wav");
                WriteWav(path, pa.Select(p => p * PascalsToFull).ToArray());
                Console.WriteLine($"  wrote {path}");
            }
        }
        return 0;
    }

    /// <summary>
    /// Each nature voice, and an air conditioner for reference, through the real provider: placed at
    /// the game's gain for its declared level (Loudness.Place times the voice's headroom gain, as
    /// ClientAudioSystem does), the master captured, the level read back. What the law predicts for
    /// two sources at the same distance is the difference of their PlacedDb.
    /// </summary>
    private static int Live(float sec, float dist)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-nature-live");
        Directory.CreateDirectory(dir);
        var keys = new[] { "machine:ac_window", "water:park_fountain", "fire:fire_pit", "foliage:park_tree" };
        var results = new List<(string Key, double Db, double Predicted)>();
        foreach (var key in keys)
        {
            string cap = Path.Combine(dir, key.Replace(':', '_') + ".wav");
            Environment.SetEnvironmentVariable("OPENFPS_AUDIO_CAPTURE", cap);
            var provider = new OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider();
            if (!provider.Initialize()) { Console.WriteLine("provider init failed"); return 1; }
            provider.UpdateListener(Vector3.Zero, Quaternion.Identity, Vector3.Zero, -1);
            var (level, extent, headroom) = Declared(key);
            var (gain, reference) = Loudness.Place(level, extent);
            var emitter = new OpenFPS.Client.AudioEngine.Data.SpatialEmitter
            {
                EntityId = 777001,
                Type = OpenFPS.Client.AudioEngine.Data.EmitterType.EntityAttached,
                IsSynth = true,
                PhysicalKey = key,
                EngineRunning = true,
                Position = new Vector3(0f, 1.6f, dist),
                Direction = Vector3.UnitZ,
                Volume = gain * OpenFPS.Client.AudioEngine.Fmod.PhysicalVoiceState.HeadroomGain(headroom),
                MinDistance = reference,
                ExtentMetres = extent,
                Range = MathF.Max(60f, Loudness.AudibleRange(level)),
                TargetRegionId = -1,
            };
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < sec)
            {
                provider.PlaySpatialSound(emitter);
                provider.Update();
                System.Threading.Thread.Sleep(16);
            }
            provider.Dispose();
            var (pcm, sr) = ReadWav(cap);
            // The first two seconds are the voice priming and fading in.
            int from = Math.Min(pcm.Length, 2 * sr);
            double e = 0;
            int finite = 0;
            for (int i = from; i < pcm.Length; i++) { if (float.IsFinite(pcm[i])) { e += pcm[i] * (double)pcm[i]; finite++; } }
            double db = 10 * Math.Log10(Math.Max(1e-15, e / Math.Max(1, finite)));
            // Flat inside the reference distance, 1/r past it; the voice's own rms sits the shared
            // headroom under full scale whatever its own headroom, which the channel gain gives back.
            double predicted = 20 * Math.Log10(gain * reference / Math.Max(reference, dist));
            results.Add((key, db, predicted));
            Console.WriteLine($"  {key}: declared {level:F1} dB, extent {extent:F1} m, headroom {headroom:F0} dB; captured {db:F1} dBFS over {(pcm.Length - from) / (double)sr:F1} s");
        }
        var basis = results[0];
        Console.WriteLine();
        Console.WriteLine($"  Against {basis.Key}, at {dist:F0} m (rms difference, captured vs the law; the law counts level and distance, not timbre):");
        foreach (var r in results.Skip(1))
            Console.WriteLine($"    {r.Key}: captured {r.Db - basis.Db:+0.0;-0.0} dB, law {r.Predicted - basis.Predicted:+0.0;-0.0} dB");
        return 0;
    }

    private static (float Level, float Extent, float Headroom) Declared(string key)
    {
        string kind = key[..key.IndexOf(':')], preset = key[(key.IndexOf(':') + 1)..];
        return kind switch
        {
            "machine" => (SmallMachineSpec.ByName(preset).SourceLevelDb, SmallMachineSpec.ByName(preset).ExtentMetres, VehicleProfile.PeakHeadroomDb),
            "water" => (WaterFeatureSpec.ByName(preset).SourceLevelDb, WaterFeatureSpec.ByName(preset).ExtentMetres, WaterFeatureSpec.ByName(preset).PeakHeadroomDb),
            "fire" => (FireSpec.ByName(preset).SourceLevelDb, FireSpec.ByName(preset).ExtentMetres, FireSpec.ByName(preset).PeakHeadroomDb),
            "foliage" => (FoliageSpec.ByName(preset).SourceLevelDb, FoliageSpec.ByName(preset).ExtentMetres, FoliageSpec.ByName(preset).PeakHeadroomDb),
            _ => throw new ArgumentException(key),
        };
    }

    /// <summary>A preset rendered at a metre, pascals, with the wind driven from the field at a fixed
    /// place on a clock that starts at zero.</summary>
    public static (float[] Pascals, float DeclaredDb, string? Census) Render(string key, float sec, int seed)
    {
        int n = (int)(sec * Rate);
        var pa = new float[n];
        int block = 256;
        string kind = key[..key.IndexOf(':')], preset = key[(key.IndexOf(':') + 1)..];
        float x = 0f, z = 0f;
        switch (kind)
        {
            case "water":
            {
                var spec = WaterFeatureSpec.ByName(preset);
                var s = new FallingWaterSynth(spec, Rate, seed);
                if (Parts != null)
                {
                    s.ImpactPart = Parts.Contains("impact") ? 1f : 0f;
                    s.DropBubblePart = Parts.Contains("drop") ? 1f : 0f;
                    s.LumpBubblePart = Parts.Contains("lump") ? 1f : 0f;
                    s.PlungePart = Parts.Contains("plunge") ? 1f : 0f;
                }
                for (int i = 0; i < n; i += block)
                {
                    s.Wind = Steady ?? WindField.SpeedAt(x, spec.WindHeightMetres, z, i / (double)Rate);
                    s.Control(block / (float)Rate);
                    for (int k = i; k < Math.Min(n, i + block); k++) pa[k] = s.Next();
                }
                string census = string.Join("\n", s.Census().Select(c =>
                    $"  {c.Name}: {c.Drops:F0} drops/s, {c.Lumps:F0} lumps/s, {c.Bubbles:F0} plunge bubbles/s"));
                return (pa, spec.SourceLevelDb, census);
            }
            case "fire":
            {
                var spec = FireSpec.ByName(preset);
                var s = new FireSynth(spec, Rate, seed);
                if (Parts != null)
                {
                    s.RoarPart = Parts.Contains("roar") ? 1f : 0f;
                    s.CracklePart = Parts.Contains("crackle") ? 1f : 0f;
                    s.SteamPart = Parts.Contains("steam") ? 1f : 0f;
                    s.SettlePart = Parts.Contains("settle") ? 1f : 0f;
                }
                for (int i = 0; i < n; i += block)
                {
                    s.Wind = Steady ?? WindField.SpeedAt(x, spec.FlameHeightMetres, z, i / (double)Rate);
                    s.Control(block / (float)Rate);
                    for (int k = i; k < Math.Min(n, i + block); k++) pa[k] = s.Next();
                }
                return (pa, spec.SourceLevelDb, $"  crackles {s.CrackleRate:F1}/s before clustering");
            }
            case "foliage":
            {
                var spec = FoliageSpec.ByName(preset);
                var s = new FoliageSynth(spec, Rate, seed);
                if (Parts != null)
                {
                    s.LeafPart = Parts.Contains("leaf") ? 1f : 0f;
                    s.ShedPart = Parts.Contains("shed") ? 1f : 0f;
                }
                for (int i = 0; i < n; i += block)
                {
                    if (Steady is float still) s.Wind = still;
                    else s.ReadWind(x, z, i / (double)Rate);
                    s.Control(block / (float)Rate);
                    for (int k = i; k < Math.Min(n, i + block); k++) pa[k] = s.Next();
                }
                float mean = WindField.MeanAt(spec.CrownHeightMetres);
                return (pa, spec.SourceLevelDb,
                        $"  wind at the crown {mean:F1} m/s mean; {s.StrikesPerSecond(mean):F0} leaf strikes/s at the mean, {s.StrikesPerSecond(mean * 1.6f):F0} in a gust");
            }
        }
        throw new ArgumentException(key);
    }

    /// <summary>The measurements, for a render (calibrated, Pa) or a recording (relative only).</summary>
    public static void Report(string name, float[] x, int sr, bool calibrated)
    {
        // Level: Leq over the whole, and the loudest second.
        double sum = 0;
        foreach (float v in x) sum += (double)v * v;
        double rms = Math.Sqrt(sum / Math.Max(1, x.Length));
        double loudest = 0;
        for (int s = 0; s + sr <= x.Length; s += sr / 2)
        {
            double e = 0;
            for (int i = s; i < s + sr; i++) e += (double)x[i] * x[i];
            loudest = Math.Max(loudest, Math.Sqrt(e / sr));
        }
        var (bands, aWeighted) = Octaves(x, sr);
        // The room the voice needs: its 10 ms peaks' 99.9th percentile over its Leq.
        var peaks10 = new List<float>();
        for (int s = 0; s + sr / 100 <= x.Length; s += sr / 100)
        {
            float pk = 0f;
            for (int i = s; i < s + sr / 100; i++) pk = MathF.Max(pk, MathF.Abs(x[i]));
            peaks10.Add(pk);
        }
        peaks10.Sort();
        double p999 = peaks10.Count > 0 ? peaks10[Math.Min(peaks10.Count - 1, (int)(0.999 * peaks10.Count))] : 0;
        Console.WriteLine($"  headroom needed: 99.9th percentile 10 ms peak is {20 * Math.Log10(Math.Max(1e-12, p999) / Math.Max(1e-12, rms)):F1} dB over the Leq");
        double total = bands.Sum();
        string unit(double p) => calibrated ? $"{20 * Math.Log10(Math.Max(1e-9, p) / 20e-6):F1} dB" : $"{20 * Math.Log10(Math.Max(1e-12, p)):F1} dBFS";
        Console.WriteLine($"  {name}: Leq {unit(rms)}, LAeq {unit(Math.Sqrt(aWeighted))}{(calibrated ? "A" : "(A)")}, loudest second {unit(loudest)}, crest {20 * Math.Log10(x.Max(v => Math.Abs(v)) / Math.Max(1e-12, rms)):F1} dB");
        Console.WriteLine("  octave  " + string.Join(" ", Centres.Select(c => c >= 1000 ? $"{c / 1000:F0}k".PadLeft(6) : $"{c:F0}".PadLeft(6))));
        Console.WriteLine("  shape   " + string.Join(" ", bands.Select(b => $"{10 * Math.Log10(Math.Max(1e-15, b / total)),6:F1}")));

        // Texture: per octave, kurtosis of the band signal, envelope fluctuation, transient count.
        var kurt = new double[Centres.Length];
        var fluct = new double[Centres.Length];
        var trans = new double[Centres.Length];
        for (int b = 0; b < Centres.Length; b++)
        {
            float lo = Centres[b] / MathF.Sqrt(2f), hi = MathF.Min(Centres[b] * MathF.Sqrt(2f), 0.45f * sr);
            if (lo >= hi) continue;
            var band = BandPass(x, sr, lo, hi);
            double m2 = 0, m4 = 0;
            foreach (float v in band) { double q = v * v; m2 += q; m4 += q * q; }
            m2 /= band.Length; m4 /= band.Length;
            kurt[b] = m2 > 0 ? m4 / (m2 * m2) : 0;
            // 10 ms rms envelope.
            int w = sr / 100;
            var env = new List<double>();
            for (int s = 0; s + w <= band.Length; s += w)
            {
                double e = 0;
                for (int i = s; i < s + w; i++) e += (double)band[i] * band[i];
                env.Add(Math.Sqrt(e / w));
            }
            double em = env.Average();
            double es = Math.Sqrt(env.Select(e => (e - em) * (e - em)).Average());
            fluct[b] = em > 0 ? es / em : 0;
            // Transients: a 1 ms peak 12 dB over the median of the 200 ms round it.
            int pw = Math.Max(1, sr / 1000);
            var peaks = new List<double>();
            for (int s = 0; s + pw <= band.Length; s += pw)
            {
                double p = 0;
                for (int i = s; i < s + pw; i++) p = Math.Max(p, Math.Abs(band[i]));
                peaks.Add(p);
            }
            int count = 0;
            int halfWin = 100;
            for (int i = 0; i < peaks.Count; i++)
            {
                if (i > 0 && peaks[i - 1] >= peaks[i]) continue;
                if (i + 1 < peaks.Count && peaks[i + 1] > peaks[i]) continue;
                int a = Math.Max(0, i - halfWin), z = Math.Min(peaks.Count, i + halfWin);
                var round = peaks.GetRange(a, z - a);
                round.Sort();
                double median = round[round.Count / 2];
                if (peaks[i] > 3.98 * median) count++;
            }
            trans[b] = count / (x.Length / (double)sr);
        }
        Console.WriteLine("  kurtosis" + string.Join(" ", kurt.Select(k => $"{k,6:F1}")));
        Console.WriteLine("  env sd  " + string.Join(" ", fluct.Select(k => $"{k,6:F2}")));
        Console.WriteLine("  clicks/s" + string.Join(" ", trans.Select(k => $"{k,6:F1}")));

        // Slow modulation: the 0.5-8 Hz spectrum of the broadband envelope, its strongest rate.
        int hop = sr / 50;
        var benv = new List<double>();
        for (int s = 0; s + hop <= x.Length; s += hop)
        {
            double e = 0;
            for (int i = s; i < s + hop; i++) e += (double)x[i] * x[i];
            benv.Add(Math.Sqrt(e / hop));
        }
        if (benv.Count > 256)
        {
            double mean = benv.Average();
            double best = 0, bestHz = 0;
            for (double hz = 0.3; hz <= 8; hz *= 1.06)
            {
                double re = 0, im = 0;
                for (int i = 0; i < benv.Count; i++)
                {
                    double ph = 2 * Math.PI * hz * i / 50.0;
                    re += (benv[i] - mean) * Math.Cos(ph);
                    im += (benv[i] - mean) * Math.Sin(ph);
                }
                double mag = Math.Sqrt(re * re + im * im) / benv.Count / Math.Max(1e-12, mean);
                if (mag > best) { best = mag; bestHz = hz; }
            }
            double sd = Math.Sqrt(benv.Select(e => (e - mean) * (e - mean)).Average()) / Math.Max(1e-12, mean);
            Console.WriteLine($"  envelope: 20 ms sd/mean {sd:F2}, strongest slow rate {bestHz:F2} Hz ({best:F3})");
        }
    }

    /// <summary>Octave-band energies (Welch, Hann, 8192) and the A-weighted total, mean square.</summary>
    private static (double[] Bands, double AWeighted) Octaves(float[] x, int sr)
    {
        const int n = 8192;
        var bands = new double[Centres.Length];
        double a = 0;
        int frames = 0;
        var win = new double[n];
        double wsum = 0;
        for (int i = 0; i < n; i++) { win[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n); wsum += win[i] * win[i]; }
        var buf = new Complex[n];
        for (int s = 0; s + n <= x.Length; s += n / 2)
        {
            for (int i = 0; i < n; i++) buf[i] = new Complex(x[s + i] * win[i], 0);
            Spectrum.Fft(buf);
            for (int k = 1; k < n / 2; k++)
            {
                double f = k * (double)sr / n;
                double p = 2 * buf[k].Magnitude * buf[k].Magnitude / (wsum * n);
                for (int b = 0; b < Centres.Length; b++)
                    if (f >= Centres[b] / Math.Sqrt(2) && f < Centres[b] * Math.Sqrt(2)) { bands[b] += p; break; }
                a += p * AWeight(f);
            }
            frames++;
        }
        if (frames == 0) return (bands, 0);
        for (int b = 0; b < bands.Length; b++) bands[b] /= frames;
        return (bands, a / frames);
    }

    private static double AWeight(double f)
    {
        double f2 = f * f;
        double ra = 12194.0 * 12194.0 * f2 * f2 /
                    ((f2 + 20.6 * 20.6) * Math.Sqrt((f2 + 107.7 * 107.7) * (f2 + 737.9 * 737.9)) * (f2 + 12194.0 * 12194.0));
        double db = 20 * Math.Log10(ra) + 2.0;
        return Math.Pow(10, db / 10);
    }

    /// <summary>A fourth-order band-pass: two RBJ high-passes then two low-passes.</summary>
    internal static float[] BandPass(float[] x, int sr, float lo, float hi)
    {
        var y = (float[])x.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            Biquad(y, sr, lo, highPass: true);
            Biquad(y, sr, hi, highPass: false);
        }
        return y;
    }

    private static void Biquad(float[] y, int sr, float f, bool highPass)
    {
        double w = 2 * Math.PI * f / sr, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * 0.7071);
        double b0 = highPass ? (1 + c) / 2 : (1 - c) / 2, b1 = highPass ? -(1 + c) : 1 - c, b2 = b0;
        double a0 = 1 + alpha, a1 = -2 * c, a2 = 1 - alpha;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double xi = y[i];
            double yi = (b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = xi; y2 = y1; y1 = yi;
            y[i] = (float)yi;
        }
    }

    private static float Arg(string[] args, string prefix, float fallback)
        => float.TryParse(args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length),
                          System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    /// <summary>Reads a 16-bit PCM WAV, mixed to mono.</summary>
    public static (float[] Pcm, int Rate) ReadWav(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadBytes(12);
        int channels = 1, rate = 44100, bits = 16;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            string id = new string(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32(); r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                if (size > 16) r.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new InvalidDataException($"{path}: {bits}-bit, want 16");
                int frames = size / (2 * channels);
                var pcm = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float s = 0;
                    for (int c = 0; c < channels; c++) s += r.ReadInt16() / 32768f;
                    pcm[i] = s / channels;
                }
                return (pcm, rate);
            }
            else r.ReadBytes(size);
        }
        throw new InvalidDataException($"{path}: no data chunk");
    }

    private static void WriteWav(string path, float[] pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (float v in pcm) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }
}
