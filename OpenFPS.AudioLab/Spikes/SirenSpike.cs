using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Core.Signals;
using OpenFPS.Client.AudioEngine.Fmod;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// The siren, measured and written out.
///
///   --siren [preset ...] [sec=12]
///
/// Writes one WAV per mode per preset and reports what each one measures: the level against the
/// spec figure, the band the horn leaves, and the sweep rate actually achieved. The last of those
/// matters because a sweep is the whole identity of a mode — a "yelp" at the wrong rate is not a
/// slightly-off yelp, it is a different siren — and it can be counted rather than judged.
/// </summary>
public static class SirenSpike
{
    private const int Sr = 44100;

    public static int Run(string[] args)
    {
        var presets = args.Where(a => SirenSpec.Presets.ContainsKey(a)).ToList();
        if (presets.Count == 0) presets = SirenSpec.Presets.Keys.ToList();
        float seconds = Arg(args, "sec", 20f);   // long enough for four wails
        if (args.Contains("drive")) return Drive(args, seconds);
        if (args.Contains("voice")) return ThroughTheVoice(args, seconds);

        // Which vehicles actually carry one, asked through the SAME registry the client asks —
        // because a profile declaring a siren and the registry handing one back are two different
        // facts, and the client only ever sees the second.
        Console.WriteLine("\n  Vehicles carrying a siren head, via MachineRegistry:");
        int carried = 0;
        foreach (var id in MachineRegistry.Ids)
        {
            var prof = MachineRegistry.VehicleFor(id);
            if (prof.Siren is { } head)
            {
                var sp = SirenSpec.ByName(head);
                Console.WriteLine($"    {id,-20} {head,-10} {sp.SourceLevelDb:F0} dB at 1 m "
                                + $"against the vehicle's own {prof.SourceLevelDb:F0} "
                                + $"(+{sp.SourceLevelDb - prof.SourceLevelDb:F0})");
                carried++;
            }
        }
        if (carried == 0) Console.WriteLine("    NONE — nothing on the map can sound one.");

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "SIRENS");
        Directory.CreateDirectory(dir);
        Console.WriteLine("\n  Police sirens, on the horn's axis at one metre.\n");

        foreach (var key in presets)
        {
            var spec = SirenSpec.ByName(key);
            Console.WriteLine($"  {key}");
            foreach (var line in new ElectronicSiren(spec, Sr).Describe()) Console.WriteLine($"    {line}");

            var modes = key == "two_tone"
                ? new[] { SirenMode.HiLo }
                : new[] { SirenMode.Wail, SirenMode.Yelp, SirenMode.Phaser, SirenMode.HiLo };
            // How much of what comes out is NOT a harmonic of what went in.
            //
            // This is the number that decides whether a sweep sounds like a sweep. Anything the
            // oscillator makes above Nyquist folds back as a partial at an unrelated frequency,
            // and during a sweep those aliases slide DOWN while the real harmonics slide up — heard
            // as roughness on a slow wail and as stepping on a fast one. Held at a fixed frequency
            // the aliases are still there and still off-harmonic, so they can be counted without
            // the sweep confusing the measurement.
            Console.WriteLine($"\n      off-harmonic energy held at {spec.SweepHighHz:F0} Hz: {AliasFloor(spec):F2} %");
            Console.WriteLine();
            Console.WriteLine("      mode      level dB   peak dB    low Hz   high Hz   sweeps/min   band 0.5-4k");
            foreach (var mode in modes)
            {
                var (wav, db, peak, lo, hi, sweeps, band) = Render(spec, mode, seconds);
                string path = Path.Combine(dir, $"siren_{key}_{mode.ToString().ToLowerInvariant()}.wav");
                File.WriteAllBytes(path, VehicleSynth.ToWav16(wav));
                Console.WriteLine($"      {mode,-8}  {db,9:F1} {peak,9:F1}  {lo,8:F0}  {hi,8:F0}   {sweeps,10:F0}   {band,10:F0} %");
            }
            Console.WriteLine($"    wrote {modes.Length} WAV(s) to {dir}\n");
        }
        return 0;
    }


    /// <summary>
    /// The share of the output that is not at a harmonic of the oscillator, per cent, with the
    /// sweep held still at the top of its range. Alias products and nothing else live there.
    /// </summary>
    private static float AliasFloor(SirenSpec spec)
    {
        // Hi-lo holds a fixed note, so it is the mode that can be held: put its two tones at the
        // top of the sweep range and the oscillator runs at a constant frequency.
        var held = spec with { HiLoLowHz = spec.SweepHighHz, HiLoRatio = 1f, HiLoHoldSeconds = 600f };
        var siren = new ElectronicSiren(held, Sr) { Mode = SirenMode.HiLo };
        for (int i = 0; i < Sr / 2; i++) siren.Step();

        int n = Sr;                                   // a one-second window: 1 Hz bins
        var x = new float[n];
        for (int i = 0; i < n; i++) { siren.Step(); x[i] = siren.Output; }

        double total = 0;
        foreach (float v in x) total += (double)v * v;
        if (total <= 0) return 0f;

        // Everything within a few hertz of a harmonic is signal; the rest is not.
        double harmonic = 0;
        for (int k = 1; k * spec.SweepHighHz < Sr * 0.48f; k++)
        {
            float f = k * spec.SweepHighHz;
            for (int off = -3; off <= 3; off++) harmonic += BinEnergy(x, f + off);
        }
        return (float)(100.0 * Math.Max(0.0, 1.0 - harmonic / total));
    }

    /// <summary>Energy in one Goertzel bin.</summary>
    private static double BinEnergy(float[] x, float hz)
    {
        double w = 2 * Math.PI * hz / Sr, cw = 2 * Math.Cos(w);
        double g1 = 0, g2 = 0;
        foreach (float v in x) { double g0 = v + cw * g1 - g2; g2 = g1; g1 = g0; }
        double mag = Math.Sqrt(g1 * g1 + g2 * g2 - cw * g1 * g2) * 2.0 / x.Length;
        return mag * mag / 2.0 * x.Length;
    }

    private static (float[] Wav, float Db, float PeakDb, float LoHz, float HiHz, float SweepsPerMin, float BandPercent)
        Render(SirenSpec spec, SirenMode mode, float seconds)
    {
        var siren = new ElectronicSiren(spec, Sr) { Mode = mode };
        int n = (int)(seconds * Sr);
        var pa = new float[n];
        float lo = float.MaxValue, hi = 0f, peak = 0f;
        double sum = 0;
        // Count sweeps by counting the turning points of the oscillator's own frequency, which is
        // the honest way: it measures what came out, not what the spec asked for.
        int turns = 0; float prev = 0f; int dir = 0;

        for (int i = 0; i < Sr / 4; i++) siren.Step();          // let the gate come up
        for (int i = 0; i < n; i++)
        {
            siren.Step();
            float x = siren.Output;
            pa[i] = x;
            sum += (double)x * x;
            float a = MathF.Abs(x);
            if (a > peak) peak = a;
            float f = siren.Hz;
            if (f < lo) lo = f;
            if (f > hi) hi = f;
            // A five-second wail moves its oscillator less than a hundredth of a hertz per sample,
            // so a hundredth-hertz deadband sees a flat line and counts no sweeps at all — which is
            // what the first run of this reported for the apparatus wail. The deadband only has to
            // reject arithmetic noise.
            int d = f > prev + 1e-5f ? 1 : f < prev - 1e-5f ? -1 : dir;
            if (dir != 0 && d != 0 && d != dir) turns++;
            dir = d; prev = f;
        }
        float rms = MathF.Sqrt((float)(sum / n));
        float db = 20f * MathF.Log10(MathF.Max(1e-9f, rms) / 20e-6f);
        float peakDb = 20f * MathF.Log10(MathF.Max(1e-9f, peak) / 20e-6f);
        // Two turning points per sweep.
        float sweepsPerMin = turns / 2f / seconds * 60f;
        float band = BandShare(pa, 500f, 4000f);
        // Peak-normalised for listening; the levels above are the real ones.
        var wav = new float[n];
        float g = peak > 1e-9f ? 0.89f / peak : 0f;
        for (int i = 0; i < n; i++) wav[i] = pa[i] * g;
        return (wav, db, peakDb, lo, hi, sweepsPerMin, band);
    }

    /// <summary>What share of the energy is inside a band, per cent. A siren that is not almost all
    /// between 500 Hz and 4 kHz is not going to be heard over a bus, whatever it measures.</summary>
    private static float BandShare(float[] x, float lo, float hi)
    {
        double all = 0, inside = 0;
        // Two second-order sections rather than an FFT: the question is a ratio, not a spectrum.
        float aLo = 1f - MathF.Exp(-2f * MathF.PI * lo / Sr);
        float aHi = 1f - MathF.Exp(-2f * MathF.PI * hi / Sr);
        float l1 = 0, l2 = 0, h1 = 0, h2 = 0;
        foreach (float s in x)
        {
            l1 += aLo * (s - l1); l2 += aLo * (l1 - l2);       // below lo
            h1 += aHi * (s - h1); h2 += aHi * (h1 - h2);       // below hi
            float inBand = h2 - l2;
            all += (double)s * s;
            inside += (double)inBand * inBand;
        }
        return all <= 0 ? 0f : (float)(100.0 * inside / all);
    }


    /// <summary>
    /// The siren as the MAP drives it — `--siren drive [map=city] [track=downtown_cw] [sec=]`.
    ///
    /// The gap this closes is the one that let a fault through: the bench renders a head held in
    /// one mode and it sounded right, while on the map the same head was being switched between
    /// modes by a controller reading a racing line, and it sounded wrong. A bench that cannot
    /// reproduce how a thing is DRIVEN can only ever exonerate it.
    ///
    /// So this runs the real <see cref="SirenController"/> against the map's real racing line, at
    /// the rate the audio system sees positions, and renders the head it commands. Prints where
    /// every change happened, and writes a WAV of the lap.
    /// </summary>
    private static int Drive(string[] args, float seconds)
    {
        string trackId = Str(args, "track") ?? "downtown_cw";
        var line = CityLine(Str(args, "map") ?? "city", trackId);
        if (line == null) { Console.WriteLine($"  FAIL: no track '{trackId}' on that map."); return 1; }

        var spec = SirenSpec.Patrol100W;
        var siren = new ElectronicSiren(spec, Sr);
        var control = new SirenController();
        const float tick = 1f / 30f;                  // the audio system's position rate

        Console.WriteLine($"\n  The patrol head, driven by '{trackId}' ({line.Length:F0} m).\n");
        int n = (int)(seconds * Sr);
        var pa = new float[n];
        float travelled = 0f, sinceTick = 0f;
        var last = SirenMode.Off;
        int changes = 0;

        for (int i = 0; i < n; i++)
        {
            sinceTick += 1f / Sr;
            if (sinceTick >= tick)
            {
                line.Sample(travelled, out _, out _, out float v);
                travelled += v * sinceTick;
                var mode = control.Update(v, sinceTick);
                if (mode != last)
                {
                    Console.WriteLine($"    {i / (float)Sr,6:F1} s  {travelled,6:F0} m  {v * 3.6f,5:F0} km/h   -> {mode}");
                    last = mode; changes++;
                }
                siren.Mode = mode;
                sinceTick = 0f;
            }
            siren.Step();
            pa[i] = siren.Output;
        }

        float peak = 0f;
        foreach (float x in pa) peak = MathF.Max(peak, MathF.Abs(x));
        var wav = new float[n];
        float g = peak > 1e-9f ? 0.89f / peak : 0f;
        for (int i = 0; i < n; i++) wav[i] = pa[i] * g;
        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "SIRENS");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"siren_driven_{trackId}.wav");
        File.WriteAllBytes(path, VehicleSynth.ToWav16(wav));
        Console.WriteLine($"\n    {changes} change(s) in {seconds:F0} s; wrote {path}\n");
        return 0;
    }

    private static RaceLine? CityLine(string mapId, string trackId)
    {
        var d = new DirectoryInfo(Environment.CurrentDirectory);
        string? path = null;
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string c = Path.Combine(d.FullName, "OpenFPS.Server", "maps", mapId + ".json");
            if (File.Exists(c)) { path = c; break; }
        }
        if (path == null) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("Tracks", out var tracks)) return null;
        foreach (var t in tracks.EnumerateArray())
        {
            if (!string.Equals(t.GetProperty("Id").GetString(), trackId, StringComparison.OrdinalIgnoreCase)) continue;
            var wp = new List<System.Numerics.Vector3>();
            foreach (var w in t.GetProperty("Waypoints").EnumerateArray())
                wp.Add(new System.Numerics.Vector3(w.GetProperty("X").GetSingle(),
                                                   w.GetProperty("Y").GetSingle(),
                                                   w.GetProperty("Z").GetSingle()));
            float bank = t.TryGetProperty("BankingDegrees", out var b) ? b.GetSingle() : 0f;
            return wp.Count >= 3 ? new RaceLine(wp, 0f, 62f / 3.6f, 0.85f, 2.92f, bank) : null;
        }
        return null;
    }

    private static string? Str(string[] a, string k)
    {
        string? v = a.FirstOrDefault(x => x.StartsWith(k + "=", StringComparison.Ordinal));
        return v?[(k.Length + 1)..];
    }


    /// <summary>
    /// The siren rendered through the object the GAME uses — `--siren voice`.
    ///
    /// Every other mode here drives <see cref="ElectronicSiren"/> directly, and that has been
    /// enough to exonerate a siren that was wrong on the map twice: once because the mode was
    /// being switched by a controller reading a racing line, and once because the whole server was
    /// a day old. Neither was visible from a bench that instantiated the synthesiser itself.
    ///
    /// So this goes through <see cref="SirenVoiceState"/> — the real voice, with the real
    /// full-scale reference derived from the spec's own level, the real per-block Control() tick,
    /// the real ring buffer and the real soft ceiling — and reports what comes out of it. If this
    /// and `--siren` disagree, the fault is in the voice and not in the model.
    /// </summary>
    private static int ThroughTheVoice(string[] args, float seconds)
    {
        var key = args.FirstOrDefault(a => SirenSpec.Presets.ContainsKey(a)) ?? "patrol";
        var spec = SirenSpec.ByName(key);
        var voice = new SirenVoiceState(spec, Sr) { Running = true };

        Console.WriteLine($"\n  '{key}' through SirenVoiceState — the object the game plays.\n");
        Console.WriteLine($"    spec: {spec.SweepLowHz:F0}-{spec.SweepHighHz:F0} Hz, duty {spec.Duty:F2}, "
                        + $"mouth {spec.HornMouthMetres * 1000f:F0} mm -> cutoff {spec.FlareCutoffHz:F0} Hz, "
                        + $"{spec.ReferenceDbAt3m:F0} dB at 10 ft");
        Console.WriteLine("      mode        level dB    3rd/2nd    5th/4th   (odd over even: a square is positive)");

        string dir = Path.Combine(AppContext.BaseDirectory, "ASSETS", "SOUNDS", "SIRENS");
        Directory.CreateDirectory(dir);

        foreach (var mode in new[] { SirenMode.Wail, SirenMode.Yelp, SirenMode.Phaser })
        {
            voice.TargetMode = (int)mode;
            int n = (int)(seconds * Sr);
            var buf = new float[1024];
            // Let the voice prime and the gate come up.
            for (int i = 0; i < Sr / 1024; i++) { voice.Produce(); voice.Consume(buf); }

            var outp = new float[n];
            int at = 0;
            while (at < n)
            {
                voice.Produce();
                voice.Consume(buf);
                int take = Math.Min(buf.Length, n - at);
                Array.Copy(buf, 0, outp, at, take);
                at += take;
            }

            // Back to pascals: the voice divides by its own full-scale reference.
            float scale = voice.PascalsAtFullScale;
            double sum = 0; float peak = 0f;
            foreach (float x in outp) { sum += (double)x * x; peak = MathF.Max(peak, MathF.Abs(x)); }
            float db = 20f * MathF.Log10(MathF.Max(1e-9f, MathF.Sqrt((float)(sum / n)) * scale) / 20e-6f);

            // The square test, on the voice's own output, held at mid-sweep by using a long window.
            var hold = spec with { HiLoLowHz = 900f, HiLoRatio = 1f, HiLoHoldSeconds = 600f };
            var probe = new SirenVoiceState(hold, Sr) { Running = true, TargetMode = (int)SirenMode.HiLo };
            var pb = new float[1024];
            for (int i = 0; i < Sr / 1024; i++) { probe.Produce(); probe.Consume(pb); }
            var px = new float[Sr];
            int pat = 0;
            while (pat < px.Length)
            {
                probe.Produce(); probe.Consume(pb);
                int take = Math.Min(pb.Length, px.Length - pat);
                Array.Copy(pb, 0, px, pat, take); pat += take;
            }
            double h2 = BinEnergy(px, 1800f), h3 = BinEnergy(px, 2700f);
            double h4 = BinEnergy(px, 3600f), h5 = BinEnergy(px, 4500f);
            double lo = 10 * Math.Log10(Math.Max(1e-20, h3) / Math.Max(1e-20, h2));
            double hi = 10 * Math.Log10(Math.Max(1e-20, h5) / Math.Max(1e-20, h4));

            float g = peak > 1e-9f ? 0.89f / peak : 0f;
            for (int i = 0; i < n; i++) outp[i] *= g;
            string path = Path.Combine(dir, $"voice_{key}_{mode.ToString().ToLowerInvariant()}.wav");
            File.WriteAllBytes(path, VehicleSynth.ToWav16(outp));
            Console.WriteLine($"      {mode,-10} {db,9:F1}  {lo,9:F1}  {hi,9:F1}");
        }
        Console.WriteLine($"\n    wrote voice_{key}_*.wav to {dir}\n");
        return 0;
    }

    private static float Arg(string[] args, string key, float fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal));
        return a != null && float.TryParse(a[(key.Length + 1)..], out float v) ? v : fallback;
    }
}
