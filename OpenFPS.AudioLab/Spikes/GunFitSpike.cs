using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Every weapon's report against the NIJ recordings of its own gun, read by one ruler.
///
///   --gun-fit [nij=DIR] [out=DIR] [tag=after] [wavs] [only=ID] [z= pp= bd= td= corner= trail= gap= lead=] [grid top=N]
///
/// nij= is a folder holding the unzipped Zoom sets (Glock9_1_Zoom, Ruger357_Zoom, ...). For each
/// weapon: the clean takes side-on and behind (90, 130 and 180 degrees at 20 and 40 m) are measured
/// with <see cref="ReportMeasure"/>, and the game's blast is carried across the same open ground to
/// the same distance and measured the same way. With wavs, writes the dry render (tagged) and, once,
/// each real take cut to the same length. The value arguments lay a trial value over every weapon;
/// with grid they take comma lists and every combination is scored (see Grid). nij= defaults to
/// ~/.cache/openfps-nij, where the zips in inbox/weapons/cadreforensics are unpacked.
/// </summary>
public static class GunFitSpike
{
    private const int Sr = 48000;

    /// <summary>Which recording is which weapon. Experiment numbers run in blocks of twenty per
    /// position; each set starts at its own offset in the block.</summary>
    private static readonly (string Id, string[] Sets)[] Guns =
    {
        ("glock", new[] { "Glock9_1_Zoom:8", "Glock9_2_Zoom:9" }),
        ("pistol", new[] { "Colt1911_Zoom:15" }),
        ("ar15", new[] { "M16_Zoom:17" }),
        ("akm", new[] { "WASR_Zoom:18" }),
        ("revolver357", new[] { "Ruger357_Zoom:7" }),
    };

    /// <summary>Block start, letter, angle, metres.</summary>
    private static readonly (int Block, char Letter, int Angle, float Metres)[] Positions =
    {
        (40, 'B', 90, 20f), (40, 'A', 90, 40f), (60, 'B', 130, 20f), (60, 'A', 130, 40f),
        (80, 'A', 180, 20f), (80, 'B', 180, 40f),
    };

    public static int Run(string[] args)
    {
        _args = args;
        string? only = Arg(args, "only");
        string nij = Arg(args, "nij") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "openfps-nij");
        string tag = Arg(args, "tag") ?? "after";
        string outDir = Arg(args, "out") ?? OpenFPS.AudioLab.LabPaths.InRepo("inbox", "gunfire-2026-10-02");
        bool wavs = args.Contains("wavs");
        if (wavs) Directory.CreateDirectory(outDir);

        Console.WriteLine($"\n  Reports against the NIJ recordings ({tag}). Bands 125..16k dB re loudest; ms to -10/-20/-30.");
        Console.WriteLine("  rms = band error 125 Hz-8 kHz, dB.\n");
        var summary = new List<string>();
        foreach (var (id, sets) in Guns.Append(("shotgun", Array.Empty<string>())))
        {
            if (only != null && only != id) continue;
            var weapon = WeaponRegistry.Get(id);
            var real = new List<(int Angle, float Metres, ReportMeasurement M, float[] Take)>();
            foreach (var set in sets)
            {
                var parts = set.Split(':');
                int offset = int.Parse(parts[1]);
                foreach (var (block, letter, angle, metres) in Positions)
                {
                    string ex = $"ZM_{block + offset:000}{letter}_S0";
                    string dir = Path.Combine(nij, parts[0]);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, ex + "*.wav").OrderBy(f => f))
                    {
                        var (pcm, rate, clipped) = ReadStereo(f);
                        if (clipped || rate != 96000) continue;
                        real.Add((angle, metres, ReportMeasure.Measure(pcm, rate), pcm));
                    }
                }
            }

            Console.WriteLine($"  {id}  ({real.Count} clean takes)");
            if (args.Contains("grid") && weapon != null && real.Count > 0) { Grid(weapon, real); continue; }
            float[]? pooledReal = null, pooledSyn = null;
            var realDn = new List<float[]>(); var synDn = new List<float[]>();
            foreach (var (_, _, angle, metres) in Positions)
            {
                var at = real.Where(r => r.Angle == angle && r.Metres == metres).ToList();
                if (at.Count == 0 && weapon == null) continue;
                string line = $"    {angle,3}deg {metres,2}m";
                float[]? rb = null;
                if (at.Count > 0)
                {
                    rb = Mean(at.Select(r => r.M.BandsDb));
                    var dn = new[] { at.Average(r => r.M.Down10Ms), at.Average(r => r.M.Down20Ms), at.Average(r => r.M.Down30Ms), at.Average(r => r.M.PositivePhaseMs) };
                    realDn.Add(dn);
                    pooledReal = pooledReal == null ? rb : pooledReal.Zip(rb, (a, b) => a + b).ToArray();
                    line += $"  real {Fmt(rb)} | {dn[0]:F2}/{dn[1]:F2}/{dn[2]:F2} +{dn[3]:F2} neg {at.Average(r => r.M.NegativeRatio):F2} (n={at.Count})";
                }
                if (weapon != null)
                {
                    var ms = Enumerable.Range(1, 4).Select(s => ReportMeasure.Measure(
                        ReportMeasure.Propagate(Lead(WeaponSynth.MuzzleBlast(Prof(weapon), s)), Sr, metres), Sr)).ToList();
                    var sb = Mean(ms.Select(m => m.BandsDb));
                    var dn = new[] { ms.Average(m => m.Down10Ms), ms.Average(m => m.Down20Ms), ms.Average(m => m.Down30Ms), ms.Average(m => m.PositivePhaseMs) };
                    if (rb != null) { synDn.Add(dn); pooledSyn = pooledSyn == null ? sb : pooledSyn.Zip(sb, (a, b) => a + b).ToArray(); }
                    line += $"\n                synth {Fmt(sb)} | {dn[0]:F2}/{dn[1]:F2}/{dn[2]:F2} +{dn[3]:F2} neg {ms.Average(m => m.NegativeRatio):F2}";
                    if (rb != null) line += $"  rms {Rms(sb, rb):F1}";
                }
                Console.WriteLine(line);
            }
            if (pooledReal != null && pooledSyn != null)
            {
                int k = realDn.Count;
                var pr = Rel(pooledReal.Select(v => v / k).ToArray());
                var ps = Rel(pooledSyn.Select(v => v / k).ToArray());
                var rd = Enumerable.Range(0, 4).Select(i => realDn.Average(d => d[i])).ToArray();
                var sd = Enumerable.Range(0, 4).Select(i => synDn.Average(d => d[i])).ToArray();
                string s = $"  {id,-12} pooled real  {Fmt(pr)} | {rd[0]:F2}/{rd[1]:F2}/{rd[2]:F2} +{rd[3]:F2}\n" +
                           $"  {"",-12} pooled synth {Fmt(ps)} | {sd[0]:F2}/{sd[1]:F2}/{sd[2]:F2} +{sd[3]:F2}  rms {Rms(ps, pr):F1}";
                summary.Add(s);
            }

            if (weapon != null)
            {
                var dry = WeaponSynth.MuzzleBlast(Prof(weapon), 1);
                float peak = dry.Max(MathF.Abs);
                Console.WriteLine($"    dry buffer: {dry.Length * 1000f / Sr:F1} ms, peak {20 * MathF.Log10(peak):F1} dBFS, energy {ReportMeasure.EnergyDb(dry, Sr):F1} dB");
            }
            if (wavs)
            {
                if (weapon != null)
                {
                    // Three shots a second apart, at the level the buffer carries into the game.
                    var three = new List<float>();
                    for (int s = 1; s <= 3; s++)
                    {
                        three.AddRange(new float[Sr / 4]);
                        var b = WeaponSynth.MuzzleBlast(Prof(weapon), s);
                        three.AddRange(b);
                        three.AddRange(new float[Sr - b.Length]);
                    }
                    WriteScaled(Path.Combine(outDir, $"{id}_{tag}_dry.wav"), three.ToArray(), 0.25f);
                }
                var reference = real.FirstOrDefault(r => r.Angle == 90 && r.Metres == 20f);
                if (reference.Take != null)
                {
                    var cut = Cut(Decimate(reference.Take), 0.040f);
                    var three = new List<float>();
                    for (int s = 0; s < 3; s++) { three.AddRange(new float[Sr / 4]); three.AddRange(cut); three.AddRange(new float[Sr - cut.Length]); }
                    float pk = three.Max(MathF.Abs);
                    WriteScaled(Path.Combine(outDir, $"{id}_nij_90deg_20m_cut40ms.wav"), three.ToArray(), 0.89f / pk);
                }
            }
        }
        Console.WriteLine("\n  Pooled over the clean positions:");
        foreach (var s in summary) Console.WriteLine(s);

        // The energy a shot carries against a clap and a knock, each as the engine is handed it.
        var clap = Applause.RenderClap(Sr, 1);
        var knock = DoorKnock.Render(1, Sr, 1);
        Console.WriteLine($"\n  clap:  peak {20 * MathF.Log10(clap.Max(MathF.Abs)):F1} dBFS, energy {ReportMeasure.EnergyDb(clap, Sr):F1} dB");
        Console.WriteLine($"  knock: peak {20 * MathF.Log10(knock.Max(MathF.Abs)):F1} dBFS, energy {ReportMeasure.EnergyDb(knock, Sr):F1} dB");
        return 0;
    }

    private static string[] _args = Array.Empty<string>();

    /// <summary>
    /// grid: every combination of comma-separated z= pp= corner= bd= td= trail= gap=, scored as the
    /// lab scored its fits (band error 125 Hz-8 kHz plus 1.5 x the decay-time error), best first.
    /// Positions whose real takes carry the range's echo inside the window (-10 dB later than 3 ms)
    /// are left out of the decay target.
    /// </summary>
    private static void Grid(WeaponDefinition weapon, List<(int Angle, float Metres, ReportMeasurement M, float[] Take)> real)
    {
        float[] L(string name, float dflt) => Arg(_args, name) is { } v
            ? v.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : new[] { dflt };
        var basis = WeaponProfile.From(weapon);
        var groups = real.GroupBy(r => (r.Angle, r.Metres)).ToList();
        var rb = Rel(Mean(groups.Select(g => Mean(g.Select(r => r.M.BandsDb)))));
        var clean = groups.Where(g => g.Average(r => r.M.Down10Ms) < 3f).ToList();
        var rd = new[] { clean.Average(g => g.Average(r => r.M.Down10Ms)), clean.Average(g => g.Average(r => r.M.Down20Ms)), clean.Average(g => g.Average(r => r.M.Down30Ms)) };
        Console.WriteLine($"  target {Fmt(rb)} | {rd[0]:F2}/{rd[1]:F2}/{rd[2]:F2} +{clean.Average(g => g.Average(r => r.M.PositivePhaseMs)):F2} neg {clean.Average(g => g.Average(r => r.M.NegativeRatio)):F2}");
        var results = new List<(float Cost, string Text)>();
        foreach (float z in L("z", basis.Damping))
        foreach (float pp in L("pp", basis.PositivePhaseMs))
        foreach (float corner in L("corner", basis.CornerHz))
        foreach (float bd in L("bd", basis.BurstDecayMs))
        foreach (float td in L("td", basis.TrailDecayMs))
        foreach (float trail in L("trail", basis.TrailLevel))
        foreach (float gap in L("gap", basis.GapLevel))
        {
            var p = basis with { Damping = z, PositivePhaseMs = pp, CornerHz = corner, BurstDecayMs = bd, TrailDecayMs = td, TrailLevel = trail, GapLevel = gap };
            var ms = groups.Select(g => g.Key.Metres).Distinct().ToDictionary(m => m, m => Enumerable.Range(1, 4)
                .Select(s => ReportMeasure.Measure(ReportMeasure.Propagate(Lead(WeaponSynth.MuzzleBlast(p, s)), Sr, m), Sr)).ToList());
            var sb = Rel(Mean(groups.Select(g => Mean(ms[g.Key.Metres].Select(m => m.BandsDb)))));
            var all = clean.SelectMany(g => ms[g.Key.Metres]).ToList();
            var sd = new[] { all.Average(m => m.Down10Ms), all.Average(m => m.Down20Ms), all.Average(m => m.Down30Ms) };
            float cost = Rms(sb, rb) + 1.5f * MathF.Sqrt(Enumerable.Range(0, 3).Average(i => (sd[i] - rd[i]) * (sd[i] - rd[i])));
            results.Add((cost, $"  cost {cost:F2} z {z} pp {pp} corner {corner} bd {bd} td {td} trail {trail} gap {gap}\n      {Fmt(sb)} | {sd[0]:F2}/{sd[1]:F2}/{sd[2]:F2} +{all.Average(m => m.PositivePhaseMs):F2} neg {all.Average(m => m.NegativeRatio):F2} band rms {Rms(sb, rb):F1}"));
        }
        foreach (var r in results.OrderBy(r => r.Cost).Take(int.TryParse(Arg(_args, "top"), out int top) ? top : 6))
            Console.WriteLine(r.Text);
    }

    /// <summary>The weapon's profile, with any of z= pp= bd= td= corner= gap= lead= from the command
    /// line laid over it, to try a value before it goes into the registry.</summary>
    private static WeaponProfile Prof(WeaponDefinition w)
    {
        var p = WeaponProfile.From(w);
        float? F(string name) => Arg(_args, name) is { } v ? float.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        return p with
        {
            Damping = F("z") ?? p.Damping,
            PositivePhaseMs = F("pp") ?? p.PositivePhaseMs,
            BurstDecayMs = F("bd") ?? p.BurstDecayMs,
            TrailDecayMs = F("td") ?? p.TrailDecayMs,
            CornerHz = F("corner") ?? p.CornerHz,
            GapLevel = F("gap") ?? p.GapLevel,
            TrailLevel = F("trail") ?? p.TrailLevel,
            GapLeadMs = F("lead") ?? p.GapLeadMs,
        };
    }

    /// <summary>A short silence before the source, so the measurement can find the onset.</summary>
    private static float[] Lead(float[] x) => new float[Sr / 100].Concat(x).Concat(new float[Sr / 20]).ToArray();

    private static float[] Mean(IEnumerable<float[]> xs)
    {
        var l = xs.ToList();
        return Enumerable.Range(0, l[0].Length).Select(i => l.Average(x => x[i])).ToArray();
    }

    private static float[] Rel(float[] b) { float m = b.Max(); return b.Select(v => v - m).ToArray(); }

    private static float Rms(float[] a, float[] b) =>
        MathF.Sqrt(Enumerable.Range(0, 7).Average(i => (a[i] - b[i]) * (a[i] - b[i])));

    private static string Fmt(float[] b) => string.Join(" ", b.Select(v => $"{v,5:F1}"));

    private static (float[] Pcm, int Rate, bool Clipped) ReadStereo(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int rate = BitConverter.ToInt32(bytes, 24);
        bool clipped = false;
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data")
            {
                int frames = Math.Min(size, bytes.Length - pos - 8) / 4;
                var x = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    short l = BitConverter.ToInt16(bytes, pos + 8 + i * 4), r = BitConverter.ToInt16(bytes, pos + 10 + i * 4);
                    if (Math.Abs((int)l) >= 32735 || Math.Abs((int)r) >= 32735) clipped = true;
                    x[i] = (l + r) / 2f / 32768f;
                }
                return (x, rate, clipped);
            }
            pos += 8 + size + (size & 1);
        }
        return (Array.Empty<float>(), rate, true);
    }

    /// <summary>96 kHz to 48: a windowed-sinc low-pass at 22 kHz, then every other sample.</summary>
    private static float[] Decimate(float[] x)
    {
        const int half = 32;
        var h = new float[2 * half + 1];
        for (int i = -half; i <= half; i++)
        {
            double fc = 22000.0 / 96000.0, v = i == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * i) / (Math.PI * i);
            h[i + half] = (float)(v * (0.54 + 0.46 * Math.Cos(Math.PI * i / half)));
        }
        var y = new float[x.Length / 2];
        for (int j = 0; j < y.Length; j++)
        {
            double acc = 0;
            for (int i = -half; i <= half; i++) { int at = 2 * j - i; if (at >= 0 && at < x.Length) acc += h[i + half] * x[at]; }
            y[j] = (float)acc;
        }
        return y;
    }

    /// <summary>From 2 ms before the onset, this long, the last 8 ms faded: the range's echo cut away.</summary>
    private static float[] Cut(float[] x, float seconds)
    {
        float pk = x.Max(MathF.Abs);
        int o = 0; while (MathF.Abs(x[o]) <= 0.1f * pk) o++;
        int a = Math.Max(0, o - Sr / 500), n = Math.Min((int)(seconds * Sr), x.Length - a);
        var y = x.AsSpan(a, n).ToArray();
        int fade = Sr * 8 / 1000;
        for (int i = 0; i < fade; i++) y[n - 1 - i] *= i / (float)fade;
        return y;
    }

    private static void WriteScaled(string path, float[] x, float gain)
        => File.WriteAllBytes(path, WeaponSynth.ToWav16(x.Select(v => v * gain).ToArray(), Sr));

    private static string? Arg(string[] args, string name)
        => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?.Substring(name.Length + 1);
}
