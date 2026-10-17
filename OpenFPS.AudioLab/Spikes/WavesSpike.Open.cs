using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Environment;
using OpenFPS.Physics;

namespace OpenFPS.AudioLab.Spikes;

public static partial class WavesSpike
{
    /// <summary>
    /// --waves open [map=albany_or|magnolia_tx|round] [winds=0,4,8,12] [from=250] [turbulence=0.25] [sec=20] [out=DIR] [level]
    ///
    /// Whitecaps over open water (ShoreSpec.OpenPond, ShoreSynth's open water; docs/RUNNING_WATER.md 15.11): the biggest
    /// pond on a real place's map (its shore's outline, read from the map file) or a round hectare, at each wind from
    /// <c>from=</c>, gusting as the world's wind field does. For each wind: the sea over the water (its pieces' fetch,
    /// height, the area breaking, breaks a second, the air they fold under), and its octaves heard from its downwind shore
    /// 2 m back from the edge and from a boat at its middle, through the cut (ExtendedSources.CutArea: each piece's voice
    /// at its place and gain, then 1/d from there), a mono sum with no HRTF, ground or air; and how many more crests break
    /// in the gusts than in the lulls. <c>level</c>: the round hectare at 8 m/s heard as one point at a metre, for the
    /// preset's SourceLevelDb and PeakHeadroomDb.
    /// </summary>
    private static int Open(string[] args)
    {
        string map = args.FirstOrDefault(a => a.StartsWith("map=", StringComparison.Ordinal))?[4..] ?? "albany_or";
        var winds = (args.FirstOrDefault(a => a.StartsWith("winds=", StringComparison.Ordinal))?[6..] ?? "0,4,8,12")
            .Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        float from = Arg(args, "from=", WindWeather.PrevailingFromDegrees), sec = Arg(args, "sec=", 20f), turbulence = Arg(args, "turbulence=", 0.25f);
        string? dir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..];
        if (dir != null) Directory.CreateDirectory(dir);

        if (args.Contains("level"))
        {
            // The preset's own reference: a round pond of its reference area at its reference wind, at a metre as one point.
            var spec0 = ShoreSpec.OpenPond;
            var round = Round(MathF.Sqrt(spec0.AreaSquareMetres / MathF.PI));
            var far = new Vector3(0f, 1.7f, 5000f);
            var pa = OpenRender(spec0, round, far, spec0.ReferenceWind, from, turbulence, sec, whole: true, out _, out _);
            NatureSpike.Report($"open_pond at {spec0.ReferenceWind:F0} m/s over {spec0.AreaSquareMetres:F0} m²", pa, Rate, calibrated: true);
            if (dir != null) WriteFloatWav(Path.Combine(dir, "open_pond_level.wav"), pa.Select(p => p * PascalsToFull).ToArray(), 1);
            return 0;
        }

        var outline = map == "round" ? Round(56.4f) : BiggestPond(map);
        if (outline == null) { Console.WriteLine($"FAIL: no pond on {map}"); return 1; }
        var path = new LinePath(outline);
        float area = ShoreSpec.AreaOf(outline);
        var spec = ShoreSpec.OpenPond.AcrossArea(area);
        // The downwind shore: the outline's point furthest downwind, 2 m back from the edge; and a boat at its middle.
        float rad = from * MathF.PI / 180f;
        var downwind = new Vector2(-MathF.Sin(rad), -MathF.Cos(rad));
        var edge = outline.MaxBy(p => p.X * downwind.X + p.Z * downwind.Y);
        var shore = edge + new Vector3(downwind.X, 0f, downwind.Y) * 2f;
        shore.Y = outline.Average(p => p.Y) + 1.7f;
        // The boat: the point of the water furthest from any edge (a 2 m grid over it).
        var middle = Centroid(outline);
        float bestEdge = -1f;
        float x0 = outline.Min(p => p.X), x1 = outline.Max(p => p.X), z0 = outline.Min(p => p.Z), z1 = outline.Max(p => p.Z);
        for (float x = x0; x <= x1; x += 2f)
            for (float z = z0; z <= z1; z += 2f)
            {
                if (!ExtendedSources.Inside(path, new Vector2(x, z))) continue;
                var q = new Vector3(x, outline[0].Y, z);
                float d = Vector3.Distance(path.Nearest(q, out _) with { Y = 0f }, q with { Y = 0f });
                if (d > bestEdge) { bestEdge = d; middle = q; }
            }
        middle.Y = shore.Y;
        bool afloat = ExtendedSources.Inside(path, new Vector2(middle.X, middle.Z));
        Console.WriteLine($"Open water: {(map == "round" ? "a round hectare" : $"the biggest pond on {map}")}, {area:F0} m², {path.Length:F0} m round, the surface at {outline.Average(p => p.Y):F2} m; " +
                          $"declared {spec.SourceLevelDb:F1} dB at a metre at {spec.ReferenceWind:F0} m/s; wind from {from:F0}°, turbulence {turbulence:F2}");
        Console.WriteLine($"  the shore: /tp {OpenFPS.Common.PlayerCoordinates.Format(shore with { Y = shore.Y - 1.7f })}; the boat: /tp {OpenFPS.Common.PlayerCoordinates.Format(middle with { Y = middle.Y - 1.7f })} ({(afloat ? "on the water" : "NOT on the water")})");
        string head = "  octave             " + string.Join(" ", NatureSpike.Centres.Select(c => c >= 1000 ? $"{c / 1000:F0}k".PadLeft(6) : $"{c:F0}".PadLeft(6)));
        foreach (float u in winds)
        {
            Console.WriteLine();
            Console.WriteLine($"== {u:F0} m/s from {from:F0}°" + (spec.CalmAt(u) ? $": calm for open water (under {WindWaves.WhitecapOnsetWind:F0} m/s), no voice" : ""));
            if (spec.CalmAt(u)) continue;
            Console.WriteLine(head);
            foreach (var (name, ear) in new[] { ("the shore", shore), ("the boat", middle) })
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var pa = OpenRender(spec, outline, ear, u, from, turbulence, sec, whole: false, out var synth, out var gusts);
                double cost = clock.Elapsed.TotalSeconds / (sec + 2f);
                var bands = NatureSpike.Octaves(pa, Rate).Bands;
                double total = bands.Sum();
                Console.WriteLine($"  {name,-17} {string.Join(" ", bands.Select(b => $"{10 * Math.Log10(Math.Max(1e-20, b) / 4e-10),6:F1}"))}   total {10 * Math.Log10(Math.Max(1e-20, total) / 4e-10):F1} dB; {cost * 100:F1} % of a core");
                var cols = synth.OpenColumns;
                float breaking = cols.Sum(c => c.Breaking);
                Console.WriteLine($"    {cols.Count(c => c.Rate > 0f)} of {cols.Count} pieces breaking; {cols.Sum(c => c.Rate):F1} breaks a second over {breaking:F0} m² ({breaking / area:P0} of it); " +
                                  $"Hs up to {cols.Max(c => c.Hs) * 100:F1} cm; mean fetch {cols.Where(c => c.Fetch > 0).Select(c => c.Fetch).DefaultIfEmpty(0f).Average():F0} m; air {cols.Sum(c => c.Air) * 1e6:F1} mL/s");
                if (gusts.Gust > 0 && gusts.Lull > 0)
                    Console.WriteLine($"    a piece's air while a gust crosses it x{gusts.Gust:F2} its mean, in a lull x{gusts.Lull:F2} ({10 * Math.Log10(gusts.Gust / gusts.Lull):+0.0;-0.0} dB)");
                if (dir != null) WriteFloatWav(Path.Combine(dir, $"open_{map}_{u:F0}_{(name == "the shore" ? "shore" : "boat")}.wav"), pa.Select(p => p * PascalsToFull).ToArray(), 1);
            }
            // The shore's own lapping at the shore spot, for scale: the pond's bank as one outline (ShoreSpec.PondBank).
            var lap = LapRender(outline, shore, u, from, sec);
            var lapBands = NatureSpike.Octaves(lap, Rate).Bands;
            Console.WriteLine($"  {"its bank lapping",-17} {string.Join(" ", lapBands.Select(b => $"{10 * Math.Log10(Math.Max(1e-20, b) / 4e-10),6:F1}"))}   total {10 * Math.Log10(Math.Max(1e-20, lapBands.Sum()) / 4e-10):F1} dB");
        }
        if (args.Contains("ramp"))
        {
            // The wind from calm to 12 m/s and back over the run, broadcast once a second as the server does, heard from the
            // shore spot; against a steady 12 m/s.
            float run = MathF.Max(30f, Arg(args, "run=", 60f));
            Console.WriteLine();
            Console.WriteLine($"== the wind from calm to 12 m/s and back over {run:F0} s, at the shore spot");
            var ramp = OpenRender(spec, outline, shore, 0f, from, turbulence, run, whole: false, out _, out _, t => 12f * (1f - MathF.Abs(2f * t / run - 1f)));
            var steady = OpenRender(spec, outline, shore, 12f, from, turbulence, 20f, whole: false, out _, out _);
            BandSteps.Print("through the ramp", BandSteps.Measure(ramp, null, Rate));
            BandSteps.Print("a steady 12 m/s", BandSteps.Measure(steady, null, Rate));
            // Its level in 100 ms windows: the largest change from one to the next, and where it first sounds.
            var env = Enumerable.Range(0, ramp.Length / (Rate / 10)).Select(w => 10 * Math.Log10(Math.Max(1e-20, ramp.Skip(w * Rate / 10).Take(Rate / 10).Average(x => (double)x * x)) / 4e-10)).ToList();
            var steps = env.Zip(env.Skip(1), (a, b) => (Step: b - a, Level: b)).Where(s => s.Level > 0).ToList();
            int first = env.FindIndex(v => v > 0);
            Console.WriteLine($"  level every 100 ms: first over 0 dB at {first / 10.0:F1} s ({12f * (1f - MathF.Abs(2f * first / 10f / run - 1f)):F1} m/s); the largest rise between neighbours {(steps.Count > 0 ? steps.Max(s => s.Step) : 0):F1} dB, fall {(steps.Count > 0 ? steps.Min(s => s.Step) : 0):F1} dB");
            if (dir != null) WriteFloatWav(Path.Combine(dir, $"open_{map}_ramp.wav"), ramp.Select(p => p * PascalsToFull).ToArray(), 1);
        }
        return 0;
    }

    /// <summary>A pond's bank lapping (pond_bank along the whole outline) heard from <paramref name="ear"/>: cut as a line, each
    /// place at its piece's gain and the law from its place, mono.</summary>
    private static float[] LapRender(Vector3[] outline, Vector3 ear, float wind, float from, float sec)
    {
        var path = new LinePath(outline);
        var cut = ExtendedSources.Cut(path, ear, ExtendedSources.WantedPieces(path, ear, 0), 1f);
        var spec = ShoreSpec.PondBank.AlongOutline(path.Length);
        var synth = new ShoreSynth(spec, Rate, 13, null, ExtendedSources.MaxPlaces) { Cut = cut, WindSpeed = wind, WindFromDegrees = from };
        var factor = new float[cut.Count];
        for (int k = 0; k < cut.Count; k++)
        {
            int i = cut.PieceOfPlace(k);
            var p = ExtendedSources.Place(path, ear, cut.Edges[i], cut.Edges[i + 1], 1f);
            factor[k] = p.Gain * MathF.Min(1f, 1f / MathF.Max(1e-3f, Vector3.Distance(p.At, ear)));
        }
        var buf = new float[synth.Places];
        int settle = 2 * Rate, n = (int)(sec * Rate);
        var pa = new float[n];
        for (int i = 0; i < settle + n; i++)
        {
            if (i % 256 == 0) synth.Control(256f / Rate);
            synth.NextPlaces(buf);
            if (i < settle) continue;
            float y = 0f;
            for (int k = 0; k < factor.Length; k++) y += buf[k] * factor[k];
            pa[i - settle] = y;
        }
        return pa;
    }

    /// <summary>The surface heard from <paramref name="ear"/>: its synth cut there as the client cuts it, each place summed at
    /// its piece's gain and the law from its place (or every place as one point, <paramref name="whole"/>); Pa, mono. Also
    /// the breaks a second while the wind over the water's middle gusts (over its mean plus half its spread) and lulls.</summary>
    private static float[] OpenRender(ShoreSpec spec, Vector3[] outline, Vector3 ear, float wind, float from, float turbulence, float sec, bool whole,
                                      out ShoreSynth synth, out (double Gust, double Lull) gusts, Func<float, float>? windAt = null)
    {
        var path = new LinePath(outline);
        var cut = ExtendedSources.CutArea(path, ear, whole ? 1 : ExtendedSources.WantedAreaPieces(path, ear, 0), 1f);
        var factor = new float[cut.Count];
        for (int k = 0; k < cut.Count; k++)
        {
            var p = cut.Area!.Places[cut.PieceOfPlace(k)];
            factor[k] = whole ? 1f : p.Gain * MathF.Min(1f, 1f / MathF.Max(1e-3f, Vector3.Distance(p.At, ear)));
        }
        var weather = WindWeather.Steady(wind, from, turbulence);
        synth = new ShoreSynth(spec, Rate, 11, null, ExtendedSources.MaxPlaces) { Cut = cut, WindSpeed = wind, WindFromDegrees = from, Gusts = weather };
        var buf = new float[synth.Places];
        double now = 1000.0;
        int settle = 2 * Rate, n = (int)(sec * Rate);
        var pa = new float[n];
        // Each piece's wind and the air its crests fold under, every 0.1 s.
        var rows = new List<(int Piece, float Wind, float Air)>();
        for (int i = 0; i < settle + n; i++)
        {
            if (i % 256 == 0)
            {
                // A wind that changes arrives once a second, ramped to, as the server's broadcasts do.
                if (windAt != null && i >= settle && (i - settle) % Rate < 256)
                {
                    float u = windAt((i - settle) / (float)Rate);
                    weather = weather.Following(WindAir.FromCompass(u, from, turbulence), now);
                    synth.Gusts = weather;
                }
                if (windAt != null) synth.WindSpeed = weather.At(now).Speed;
                synth.Now = now;
                synth.Control(256f / Rate);
                now += 256.0 / Rate;
                if (i >= settle && i % 4864 == 0)
                {
                    var cols = synth.OpenColumns;
                    for (int c = 0; c < cols.Count; c++) if (cols[c].Air > 0f) rows.Add((c, cols[c].Wind, cols[c].Air));
                }
            }
            synth.NextPlaces(buf);
            if (i < settle) continue;
            float y = 0f;
            for (int k = 0; k < factor.Length && k < buf.Length; k++) y += buf[k] * factor[k];
            pa[i - settle] = y;
        }
        // Piece by piece: its air while the wind over it gusts (half a spread over its mean) and while it lulls, against
        // its own mean air; pooled.
        double up = 0, down = 0;
        int ups = 0, downs = 0;
        foreach (var g in rows.GroupBy(r => r.Piece))
        {
            double mw = g.Average(r => r.Wind), sw = Math.Sqrt(g.Average(r => (r.Wind - mw) * (r.Wind - mw))), ma = g.Average(r => r.Air);
            if (!(sw > 0) || !(ma > 0)) continue;
            foreach (var r in g)
            {
                if (r.Wind > mw + 0.5 * sw) { up += r.Air / ma; ups++; }
                else if (r.Wind < mw - 0.5 * sw) { down += r.Air / ma; downs++; }
            }
        }
        gusts = (ups > 0 ? up / ups : 0, downs > 0 ? down / downs : 0);
        return pa;
    }

    /// <summary>A round pond's outline, anticlockwise, on the ground at zero.</summary>
    private static Vector3[] Round(float radius)
    {
        const int N = 64;
        return Enumerable.Range(0, N + 1).Select(i => new Vector3(radius * MathF.Cos(MathF.Tau * i / N), 0f, radius * MathF.Sin(MathF.Tau * i / N))).ToArray();
    }

    private static Vector3 Centroid(Vector3[] outline)
    {
        double a = 0, cx = 0, cz = 0;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
        {
            double cross = (double)outline[j].X * outline[i].Z - (double)outline[i].X * outline[j].Z;
            a += cross;
            cx += (outline[j].X + outline[i].X) * cross;
            cz += (outline[j].Z + outline[i].Z) * cross;
        }
        return Math.Abs(a) < 1e-9 ? outline[0] : new Vector3((float)(cx / (3 * a)), outline.Average(p => p.Y), (float)(cz / (3 * a)));
    }

    /// <summary>The biggest pond's outline on a real place's map, in the world: its shore's (or its open water's) line
    /// placed by its position, read from the map file.</summary>
    private static Vector3[]? BiggestPond(string map)
    {
        string file = Path.Combine("OpenFPS.Server", "maps", "places", map + ".json");
        if (!File.Exists(file)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        Vector3[]? best = null;
        float bestArea = 0f;
        foreach (var e in doc.RootElement.GetProperty("Entities").EnumerateArray())
        {
            string prefab = e.TryGetProperty("PrefabId", out var p) ? p.GetString() ?? "" : "";
            if (!(prefab.StartsWith("shore_pond", StringComparison.Ordinal) || prefab.StartsWith("shore_reed", StringComparison.Ordinal)) || !e.TryGetProperty("Line", out var line)) continue;
            var pos = e.GetProperty("Position");
            var at = new Vector3(pos.GetProperty("X").GetSingle(), pos.GetProperty("Y").GetSingle(), pos.GetProperty("Z").GetSingle());
            var pts = line.EnumerateArray().Select(q => at + new Vector3(q.GetProperty("X").GetSingle(), q.GetProperty("Y").GetSingle(), q.GetProperty("Z").GetSingle())).ToArray();
            float area = ShoreSpec.AreaOf(pts);
            if (area > bestArea) { bestArea = area; best = pts; }
        }
        return best;
    }
}
