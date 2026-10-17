using System.Globalization;
using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Environment;

namespace OpenFPS.AudioLab.Spikes;

public static partial class RunningWaterSpike
{
    /// <summary>
    /// --running-water banks [width=3] [slope=0.005] [length=300] [made=943] [flows=10,92,943] [sec=20] [out=DIR]
    ///
    /// A wide creek's banks as their own flow (BedSection, docs/RUNNING_WATER.md 15.10): a mapped creek of this bed (its
    /// width, its slope, its length, the flow its bed was made for, as Oak Creek on Albany) at each flow, with its banks
    /// apart (after) and as one strip (before). For each: the water in each lane (depth, speed, flow), what its sites do
    /// (stones stood for, air, lee jets), its octaves at a metre as a whole and lane by lane, and its octaves heard 1.5 m
    /// out from its edge, at 10 m and at 30 m, through the client's cut and the mixer's law (each piece's voice at its
    /// place and gain, ExtendedSources.Place, and 1/d from there): a mono sum, no HRTF, no ground, no air.
    /// </summary>
    private static int Banks(string[] args)
    {
        float width = Arg(args, "width=", 3f), slope = Arg(args, "slope=", 0.005f), length = Arg(args, "length=", 300f);
        float made = Arg(args, "made=", 943f), sec = Arg(args, "sec=", 20f);
        var flows = (args.FirstOrDefault(a => a.StartsWith("flows=", StringComparison.Ordinal))?[6..] ?? "10,92,943")
            .Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);
        var spec = GroundChannels.SpecFor(GroundChannelKind.Creek, width, slope, length, made, new GroundCatchment(new float[GroundHydrology.Surfaces], 600f));
        var strips = BedSection.Strips(spec.ManningN);
        Console.WriteLine($"A creek {width:F1} m wide, {slope:P1} slope, {length:F0} m, its bed made for {made:F0} L/s ({spec.Bed}); stones {spec.Obstacles!.PerMetre:F2} a metre, median drop {spec.Obstacles.MedianDropMetres * 100:F1} cm; banked: {BedSection.Banked(spec)}");
        Console.WriteLine($"  the section (n {spec.ManningN:F3}, a bank {BedSection.BankShare:P0} of the width, vegetation +{BedSection.BankVegetationN:F3}):");
        foreach (var (name, s) in new[] { ("middle", strips.Middle), ("a bank", strips.Bank) })
            Console.WriteLine($"    {name,-7} width {s.WidthShare:P0}, flow {s.FlowShare:P1}, depth x{s.DepthRatio:F2}, speed x{s.SpeedRatio:F2}, stones x{s.SizeRatio:F2}");
        var bed = new StreamBed(spec, 7);
        Console.WriteLine($"  stones a metre: middle {bed.CountBetween(0f, bed.Length, BedSection.Middle) / bed.Length:F2}, each bank {bed.CountBetween(0f, bed.Length, BedSection.LeftBank) / bed.Length:F2}, all {bed.CountBetween(0f, bed.Length) / bed.Length:F2}");
        var path = LinePath.Straight(Vector3.Zero, Vector3.UnitX, length, width, banked: true);
        var flat = LinePath.Straight(Vector3.Zero, Vector3.UnitX, length, width, banked: false);
        float edge = 0.5f * width;
        var ears = new[] { ("1.5 m out from the +z edge", new Vector3(0f, 1.7f, edge + 1.5f)), ("10 m out", new Vector3(0f, 1.7f, edge + 10f)),
                           ("30 m out", new Vector3(0f, 1.7f, edge + 30f)) };
        var centres = NatureSpike.Centres;
        string Row(double[] bands) => string.Join(" ", bands.Select(b => $"{10 * Math.Log10(Math.Max(1e-20, b) / 4e-10),6:F1}"));
        string Diff(double[] a, double[] b) => string.Join(" ", a.Zip(b, (x, y) => $"{10 * Math.Log10(Math.Max(1e-20, x) / Math.Max(1e-20, y)),6:+0.0;-0.0}"));
        string Head = "  octave            " + string.Join(" ", centres.Select(c => c >= 1000 ? $"{c / 1000:F0}k".PadLeft(6) : $"{c:F0}".PadLeft(6)));

        foreach (float q in flows)
        {
            Console.WriteLine();
            Console.WriteLine($"== {q:F1} L/s");
            var after = new RunningWaterSynth(spec, Rate, 7, ExtendedSources.MaxPlaces) { Flow = q };
            for (int i = 0; i < 40; i++) after.Control(0.1f);
            foreach (int lane in new[] { BedSection.Middle, BedSection.LeftBank })
            {
                var (y, v, f) = after.LaneWater(lane);
                Console.WriteLine($"  {(lane == 0 ? "middle" : "a bank"),-7} {y * 100,5:F1} cm deep, {v:F2} m/s, {f:F1} L/s");
            }
            var sites = after.LaneSites;
            foreach (var (name, pick) in new (string, Func<int, bool>)[] { ("middle", l => l == BedSection.Middle), ("banks", l => l != BedSection.Middle) })
            {
                var ss = sites.Where(s => pick(s.Lane)).ToList();
                if (ss.Count == 0) { Console.WriteLine($"  {name}: no sites"); continue; }
                var breaking = ss.Where(s => s.Air > 0f).ToList();
                Console.WriteLine($"  {name,-7} {ss.Count} sites for {ss.Sum(s => s.Weight):F0} stones; air {ss.Sum(s => s.Air) * 1e6:F0} mL/s; lee jets {ss.Average(s => s.Jet):F2} m/s, Froude {ss.Average(s => s.Froude):F1}; drops {ss.Average(s => s.Drop) * 100:F1} cm; {breaking.Count} breaking");
            }
            // At a metre, the whole line as one point: after (all, the middle alone, the banks alone) and before.
            double[] AtAMetre(bool banks, float middle, float bank, string name)
            {
                var s = new RunningWaterSynth(spec, Rate, 7, ExtendedSources.MaxPlaces, banksApart: banks) { Flow = q, MiddlePart = middle, BankPart = bank };
                var pa = RenderPlaces(s, sec, null);
                if (dir != null) WriteFloatWav(Path.Combine(dir, $"banks_{q:F0}_{name}.wav"), pa.Select(p => p * PascalsToFull).ToArray(), 1);
                return NatureSpike.Octaves(pa, Rate).Bands;
            }
            var all = AtAMetre(true, 1f, 1f, "whole");
            var mid = AtAMetre(true, 1f, 0f, "middle");
            var banksOnly = AtAMetre(true, 0f, 1f, "banks");
            var before = AtAMetre(false, 1f, 1f, "before");
            Console.WriteLine($"  at a metre, the whole {length:F0} m as one point, dB:");
            Console.WriteLine(Head);
            Console.WriteLine($"  before (one strip) {Row(before)}");
            Console.WriteLine($"  after              {Row(all)}");
            Console.WriteLine($"    its middle       {Row(mid)}");
            Console.WriteLine($"    its banks        {Row(banksOnly)}");
            Console.WriteLine($"  after - before     {Diff(all, before)}");
            Console.WriteLine($"  banks - middle     {Diff(banksOnly, mid)}");
            foreach (var (where, ear) in ears)
            {
                var b0 = Heard(spec, q, flat, ear, banks: false, sec, out _);
                var b1 = Heard(spec, q, path, ear, banks: true, sec, out var cut);
                var banksHeard = Heard(spec, q, path, ear, banks: true, sec, out _, middle: 0f);
                var midHeard = Heard(spec, q, path, ear, banks: true, sec, out _, bank: 0f);
                path.Nearest(ear, out float along);
                int piece = cut.PieceAt(along);
                Console.WriteLine($"  heard {where} ({cut.Count} pieces; beside you the near bank x{cut.LaneGain(piece, BedSection.LeftBank):F2}, the middle x{cut.LaneGain(piece, BedSection.Middle):F2}, the far bank x{cut.LaneGain(piece, BedSection.RightBank):F2} in amplitude):");
                Console.WriteLine(Head);
                Console.WriteLine($"  before             {Row(b0)}");
                Console.WriteLine($"  after              {Row(b1)}");
                Console.WriteLine($"    its middle       {Row(midHeard)}");
                Console.WriteLine($"    its banks        {Row(banksHeard)}");
                Console.WriteLine($"  after - before     {Diff(b1, b0)}");
                Console.WriteLine($"  total: before {10 * Math.Log10(b0.Sum() / 4e-10):F1} dB, after {10 * Math.Log10(b1.Sum() / 4e-10):F1} dB; the banks {10 * Math.Log10(banksHeard.Sum() / Math.Max(1e-20, midHeard.Sum())):+0.0;-0.0} dB on the middle");
            }
        }
        return 0;
    }

    /// <summary>A line's places rendered and summed as the mixer would sum its voices from <paramref name="ear"/>: each
    /// place at its piece's centroid with its piece's gain (ExtendedSources.Place) and the law from there, mono. Octave
    /// powers, Pa².</summary>
    private static double[] Heard(RunningWaterSpec spec, float q, LinePath path, Vector3 ear, bool banks, float sec, out ExtendedSources.LineCut cut,
                                  float middle = 1f, float bank = 1f)
    {
        cut = ExtendedSources.Cut(path, ear, ExtendedSources.WantedPieces(path, ear, 0), 1f);
        var factor = new float[cut.Count];
        for (int k = 0; k < cut.Count; k++)
        {
            int i = cut.PieceOfPlace(k);
            var p = ExtendedSources.Place(path, ear, cut.Edges[i], cut.Edges[i + 1], 1f);
            factor[k] = p.Gain * MathF.Min(1f, 1f / MathF.Max(1e-3f, Vector3.Distance(p.At, ear)));
        }
        var s = new RunningWaterSynth(spec, Rate, 7, ExtendedSources.MaxPlaces, banksApart: banks) { Flow = q, Cut = cut, MiddlePart = middle, BankPart = bank };
        var pa = RenderPlaces(s, sec, factor);
        return NatureSpike.Octaves(pa, Rate).Bands;
    }

    /// <summary>A synth's places summed, each times its factor (all one without), after two seconds to settle; Pa.</summary>
    private static float[] RenderPlaces(RunningWaterSynth s, float sec, float[]? factor)
    {
        var buf = new float[s.Places];
        for (int i = 0; i < Rate * 2; i++) { if (i % 256 == 0) s.Control(256f / Rate); s.NextPlaces(buf); }
        int n = (int)(sec * Rate);
        var pa = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (i % 256 == 0) s.Control(256f / Rate);
            s.NextPlaces(buf);
            float y = 0f;
            for (int k = 0; k < buf.Length; k++) y += buf[k] * (factor == null ? 1f : k < factor.Length ? factor[k] : 0f);
            pa[i] = y;
        }
        return pa;
    }
}
