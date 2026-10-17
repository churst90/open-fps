using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Core.Nature;

/// <summary>
/// A source that is metres across, heard as metres across: a tree's crown, a fire's bed.
///
/// One synth at one point gave both ears the same noise, interaural coherence 1.00 in every band
/// (docs/AUDIO_QUALITY_2026-10-06.md item 8), heard as narrow, inside the head and harsh (Blauert
/// 1997, "Spatial Hearing", 3.3; Hidaka, Beranek and Okano 1995 on IACC and width). So the synth
/// renders into several places, each its own voice at its own point: every event goes to exactly one
/// place and every continuous part has its own noise at each. They are independent streams, not
/// copies (copies of one signal from two points comb), and their powers add to the source's at any
/// split.
///
/// Place 0 is the middle (the map's emitter). How much goes to the others is the spread, from the
/// angle they subtend: none under <see cref="PointDegrees"/> (one voice, one cost), all over
/// <see cref="FullDegrees"/>, slewed at <see cref="SlewPerSecond"/> so walking up to a tree widens it
/// with no step in level. Merged, the outer places' voices are let go.
///
/// Each place is as loud as what it holds, by the mixer's law from where it is (2026-10-16, Cody: "a sound
/// source should have its sound be the entire area of the sound source"). A line of running water is cut,
/// at the listener, into pieces by the angle each covers (<see cref="Cut"/>).
///
/// Whether a world spreads its sources at all, by which rule, and the lab's two levers are that world's
/// (<see cref="WideSources"/>: OPENFPS_WIDE_SOURCES=0 plays every one from one point, as before 2026-10-06;
/// OPENFPS_EXTENT_RULE=middle the old rule, for an A/B). This holds the laws they share.
/// </summary>
public static class ExtendedSources
{

    /// <summary>How the places of a source share its power.</summary>
    public enum PlaceRule
    {
        /// <summary>Before 2026-10-16: a fixed layout, every place paid down so that together they are as
        /// loud as the source heard from its middle (<see cref="Balance"/>). Beside a chain of 20 m creek
        /// voices the level rose and fell 10.4 dB every 20 m (docs/RUNNING_WATER.md 15).</summary>
        Middle,
        /// <summary>Each place as loud as what it holds, by the mixer's law from where it is, and a line cut
        /// by angle into pieces (<see cref="Cut"/>).</summary>
        Pieces,
    }

    /// <summary>Under this half-angle the places are one point to the ear, degrees: the localisation
    /// blur straight ahead is about 1-4° (Blauert 1997, table 2.1), so two sources this far apart are
    /// not told apart, and a source this narrow is not heard as wide.</summary>
    public const float PointDegrees = 2.5f;

    /// <summary>From this half-angle the source is played wholly from its places, degrees.</summary>
    public const float FullDegrees = 6f;

    /// <summary>How fast the spread moves, per second: a full swing in about 1.4 s.</summary>
    public const float SlewPerSecond = 0.7f;

    /// <summary>How many places a tree's crown is heard from round its middle: one per bough
    /// (FoliageSynth.Boughs). Measured (AudioLab --wide-sources, 2026-10-06) against three.</summary>
    public static int TreePlaces = int.TryParse(Environment.GetEnvironmentVariable("OPENFPS_WIDE_TREE_PLACES"), out int t) && t >= 1
        ? Math.Min(t, FoliageSynth.Boughs) : FoliageSynth.Boughs;

    /// <summary>How many places a fire pit's bed is heard from round its flames; a bigger fire says its own
    /// (FireSpec.Places, FireSynth.Layout).</summary>
    public const int FirePlaces = 8;

    /// <summary>How many places each tap of a water feature is heard from round its middle.</summary>
    public const int WaterTapPlaces = 3;

    /// <summary>The most places a source is heard from, its middle included: the voice ids the client gives a
    /// source (ClientAudioSystem.PlaceVoiceId). A layout with more is heard from its middle alone.</summary>
    public const int MaxPlaces = 12;

    /// <summary>A tree's crown: one place per bough, at the middle of the boughs it stands for.</summary>
    internal static Vector3[]? TreePlacesOf(string key, IPresets presets)
    {
        var spec = presets.Foliage(key[8..]);
        int n = TreePlaces;
        var places = new Vector3[1 + n];
        for (int j = 0; j < n; j++)
        {
            Vector3 sum = Vector3.Zero;
            int count = 0;
            for (int b = 0; b < FoliageSynth.Boughs; b++)
                if (FoliageSynth.PlaceOfBough(b, n) == j) { sum += FoliageSynth.BoughOffset(spec, b); count++; }
            places[1 + j] = count > 0 ? sum / count : Vector3.Zero;
        }
        return places;
    }

    /// <summary>One tap of a water feature ("water:&lt;preset&gt;/&lt;feature&gt;/&lt;tap&gt;"): round the place its
    /// water lands, at half its extent. The whole feature at one point ("water:&lt;preset&gt;") is not spread.</summary>
    internal static Vector3[]? WaterTapPlacesOf(string key, IPresets presets)
    {
        var parts = key[6..].Split('/');
        if (parts.Length != 3 || !int.TryParse(parts[2], out int tap)) return null;
        var spec = presets.Water(parts[0]);
        if (tap < 0 || tap >= spec.Taps.Length) return null;
        float r = 0.5f * spec.Taps[tap].ExtentMetres;
        var places = new Vector3[1 + WaterTapPlaces];
        for (int j = 0; j < WaterTapPlaces; j++)
        {
            float a = MathF.Tau * (j + 0.25f) / WaterTapPlaces;
            places[1 + j] = new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
        }
        return places;
    }

    /// <summary>Running water: round where it lands (RunningWaterSynth.Layout), or along its length. A world
    /// that cuts a line by angle instead asks <see cref="WideSources.LineLength"/> first.</summary>
    internal static Vector3[]? FlowPlacesOf(string key, IPresets presets)
    {
        var spec = presets.Flow(key[5..]);
        if (spec.Places <= 1) return null;
        return RunningWaterSynth.Layout(spec);
    }

    /// <summary>Waves at an edge: along it, and on its break line (ShoreSynth.Layout), its length the map's.</summary>
    internal static Vector3[]? ShorePlacesOf(string key, IPresets presets)
    {
        // A whole outline is cut along itself at the ear (WideSources.IsOutline), not laid out.
        if (ShoreSpec.TryOutline(key, out _, out _)) return null;
        ShoreSpec.ParseKey(key, out string preset, out var geometry);
        var spec = presets.Shore(preset);
        if (spec.TotalPlaces <= 1) return null;
        return ShoreSynth.Layout(spec, geometry?.LengthMetres ?? spec.LengthMetres);
    }

    /// <summary>A wood heard as one (WoodChorus): a place a bough's worth of it, six round the wood at
    /// two-thirds of its half-widths, where its trees' wind is read as well (FoliageSynth.ReadWindAt).</summary>
    internal static Vector3[]? WoodPlacesOf(string key)
    {
        if (!WoodChorus.ParseKey(key, out _, out float rx, out float rz)) return null;
        int n = FoliageSynth.Boughs;
        var places = new Vector3[1 + n];
        for (int j = 0; j < n; j++)
        {
            float a = MathF.Tau * (j + 0.5f) / n;
            places[1 + j] = new Vector3(MathF.Cos(a) * rx, 0f, MathF.Sin(a) * rz) * (2f / 3f);
        }
        return places;
    }

    /// <summary>A fire's bed, by its own layout (FireSynth.Layout).</summary>
    internal static Vector3[]? FirePlacesOf(string key, IPresets presets)
    {
        var places = FireSynth.Layout(presets.Fire(key[5..]));
        return places.Length <= 1 ? null : places;
    }

    /// <summary>How far the outer places reach from the middle, m.</summary>
    public static float Reach(ReadOnlySpan<Vector3> layout)
    {
        float r = 0f;
        for (int i = 1; i < layout.Length; i++) r = MathF.Max(r, layout[i].Length());
        return r;
    }

    /// <summary>The spread a source of this reach wants at this distance from its middle, 0 to 1 (a world's
    /// own, which the lab can force, is <see cref="WideSources.SpreadFor"/>).</summary>
    public static float SpreadFor(float reachMetres, float distanceMetres)
    {
        if (reachMetres <= 0f) return 0f;
        float half = MathF.Atan2(reachMetres, MathF.Max(0.01f, distanceMetres)) * (180f / MathF.PI);
        float x = Math.Clamp((half - PointDegrees) / (FullDegrees - PointDegrees), 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>Moves a spread toward its target at the slew rate.</summary>
    public static float Slew(float now, float target, float dt)
    {
        float step = SlewPerSecond * MathF.Max(0f, dt);
        return now + Math.Clamp(target - now, -step, step);
    }

    /// <summary>
    /// Each place's share of the source's power at a spread: the middle keeps 1 - s·n/(n+1), each of
    /// the n others s/(n+1). At full spread every place has an equal share; merged, the middle has it
    /// all. They always sum to one.
    /// </summary>
    public static void Shares(float spread, Span<float> shares)
    {
        int n = shares.Length - 1;
        if (n <= 0) { if (shares.Length == 1) shares[0] = 1f; return; }
        float s = Math.Clamp(spread, 0f, 1f);
        float each = s / (n + 1);
        shares[0] = 1f - each * n;
        for (int i = 1; i <= n; i++) shares[i] = each;
    }

    /// <summary>
    /// The one gain for every place's voice so that together they are as loud as the source played from
    /// its middle. The mixer's law (Loudness.RenderedGain, per voice) makes the near places louder, which
    /// is right for where the sound comes from but not for how much of it there is: the declared level
    /// and extent already say that. One for a merged source, and inside the extent, where the law is flat.
    /// </summary>
    public static float Balance(Vector3 listener, Vector3 middle, ReadOnlySpan<Vector3> places, ReadOnlySpan<float> shares,
                                float referenceMetres, float rangeMetres)
    {
        float centre = Loudness.RenderedGain(1f, referenceMetres, rangeMetres, Vector3.Distance(listener, middle));
        double sum = 0;
        for (int i = 0; i < places.Length && i < shares.Length; i++)
        {
            float g = Loudness.RenderedGain(1f, referenceMetres, rangeMetres, Vector3.Distance(listener, places[i]));
            sum += shares[i] * (double)g * g;
        }
        if (sum <= 1e-20 || centre <= 0f) return 1f;
        return (float)(centre / Math.Sqrt(sum));
    }

    // ── A line cut into pieces by the angle each covers at the ear (2026-10-16) ───────────────────

    /// <summary>
    /// The length of a source heard as a line cut into pieces, m: running water laid along its own x (a creek,
    /// a ditch, a rivulet, a gutter). Zero for anything else. Whether a world cuts its lines at all is its own
    /// (<see cref="WideSources.LineLength"/>, which caches this). Its model is <paramref name="presets"/>' (the
    /// catalogue's).
    /// </summary>
    internal static float LineLengthOf(string physicalKey, IPresets presets)
    {
        if (!physicalKey.StartsWith("flow:", StringComparison.OrdinalIgnoreCase)) return 0f;
        try
        {
            var spec = presets.Flow(physicalKey[5..]);
            return spec.Layout == FlowLayout.Line && spec.Places > 1 ? spec.LengthMetres : 0f;
        }
        catch (Exception) { return 0f; }
    }

    /// <summary>
    /// A straight line source as the ear sees it: where along it the ear's foot falls (m from its middle,
    /// along its own x), how far off the line the ear is (m, height included, never under 5 cm) and half its
    /// length. Distances along it from the foot are u; from its middle, along.
    /// </summary>
    public readonly record struct LineView(float Foot, float Off, float HalfLength)
    {
        public float From => -HalfLength - Foot;
        public float To => HalfLength - Foot;
        /// <summary>The direction of a point u along from the foot, radians from the perpendicular.</summary>
        public float AngleOf(float u) => MathF.Atan2(u, Off);
        /// <summary>The angle the whole line covers at the ear, degrees.</summary>
        public float SubtendedDegrees => (AngleOf(To) - AngleOf(From)) * (180f / MathF.PI);
    }

    /// <summary>The line through <paramref name="middle"/> along <paramref name="axis"/> (a unit vector), as
    /// seen from <paramref name="ear"/>.</summary>
    public static LineView View(Vector3 middle, Vector3 axis, float length, Vector3 ear)
    {
        var d = ear - middle;
        float foot = Vector3.Dot(d, axis);
        float off = (d - foot * axis).Length();
        return new LineView(foot, MathF.Max(0.05f, off), 0.5f * MathF.Max(0f, length));
    }

    /// <summary>
    /// How many pieces a line wants from here: enough that none covers more than TrainVoicing's split angle, and
    /// no more than leave every two neighbours together over its merge angle, as TrainVoicing merges a pair;
    /// holding <paramref name="previous"/> while it fits between the two (the ear places a source to a few
    /// degrees, and the band between them keeps a tap of a key from cutting it back and forth); never more than
    /// <see cref="MaxPlaces"/>.
    /// </summary>
    public static int WantedPieces(LineView view, int previous) => WantedPieces(view.SubtendedDegrees, previous);

    /// <summary>The same for a line covering <paramref name="covered"/> degrees of the ear's view.</summary>
    private static int WantedPieces(float covered, int previous)
    {
        int fewest = Math.Max(1, (int)MathF.Ceiling(covered / OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.SplitAboveDegrees - 1e-4f));
        int most = Math.Max(fewest, (int)MathF.Floor(2f * covered / OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.MergeBelowDegrees));
        int n = previous <= 0 ? fewest : Math.Clamp(previous, fewest, most);
        return Math.Min(n, MaxPlaces);
    }

    /// <summary>
    /// A line cut into pieces, each heard as one voice: the edges along it (m from its middle, ascending, the
    /// first and last its ends) and which piece holds its middle, which the source's own voice plays. The
    /// others are places 1 up, in order along it. Shared by the client, which places the voices, and the synth,
    /// which writes each event into the piece it happens in.
    /// </summary>
    public sealed class LineCut
    {
        public readonly float[] Edges;
        public readonly int Middle;

        public LineCut(float[] edges)
        {
            Edges = edges;
            int m = 0;
            // As PieceAt finds it: a piece holds its lower edge, not its upper.
            while (m < edges.Length - 2 && edges[m + 1] <= 0f) m++;
            Middle = m;
        }

        public int Count => Edges.Length - 1;
        public float Length => Edges[^1] - Edges[0];

        /// <summary>The path it was cut along, when it was cut along one (<see cref="Cut(LinePath, Vector3, int)"/>,
        /// <see cref="Whole(LinePath)"/>): what a shore's synth reads where its water lies from, piece by piece.</summary>
        public LinePath? Path { get; init; }

        /// <summary>The place (the voice: 0 the source's own) that plays piece <paramref name="piece"/>.</summary>
        public int PlaceOfPiece(int piece) => piece == Middle ? 0 : piece < Middle ? piece + 1 : piece;

        /// <summary>The piece place <paramref name="place"/> plays.</summary>
        public int PieceOfPlace(int place) => place == 0 ? Middle : place <= Middle ? place - 1 : place;

        /// <summary>The piece a point <paramref name="along"/> m from the middle is in; the ends' own beyond them.</summary>
        public int PieceAt(float along)
        {
            int lo = 0, hi = Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (along < Edges[mid + 1]) hi = mid; else lo = mid + 1;
            }
            return lo;
        }

        /// <summary>The place that hears a point <paramref name="along"/> m from the middle.</summary>
        public int PlaceAt(float along) => PlaceOfPiece(PieceAt(along));

        /// <summary>The share of the line's length place <paramref name="place"/> plays: of a line doing the same
        /// all along, its share of the power.</summary>
        public float ShareOfPlace(int place)
        {
            if (place < 0 || place >= Count || Length <= 0f) return 0f;
            int i = PieceOfPlace(place);
            return (Edges[i + 1] - Edges[i]) / Length;
        }

        /// <summary>The shortest piece, m: the one at the ear.</summary>
        public float Shortest
        {
            get
            {
                float s = float.MaxValue;
                for (int i = 0; i < Count; i++) s = MathF.Min(s, Edges[i + 1] - Edges[i]);
                return s;
            }
        }
    }

    /// <summary>The whole line as one piece.</summary>
    public static LineCut Whole(float length) => new(new[] { -0.5f * length, 0.5f * length });

    /// <summary>A whole path as one piece.</summary>
    public static LineCut Whole(LinePath path) => new(new[] { -path.HalfLength, path.HalfLength }) { Path = path };

    /// <summary>
    /// The line cut into <paramref name="pieces"/> pieces of equal angle at the ear: small beside it, long far
    /// along it. When fewer are given than it wants (the place budget), each covers more; the far end is never
    /// dropped.
    /// </summary>
    public static LineCut Cut(LineView view, int pieces)
    {
        int n = Math.Clamp(pieces, 1, MaxPlaces);
        var edges = new float[n + 1];
        float a0 = view.AngleOf(view.From), a1 = view.AngleOf(view.To);
        edges[0] = -view.HalfLength;
        edges[n] = view.HalfLength;
        for (int i = 1; i < n; i++)
            edges[i] = Math.Clamp(view.Foot + view.Off * MathF.Tan(a0 + (a1 - a0) * i / n), -view.HalfLength, view.HalfLength);
        return new LineCut(edges);
    }

    /// <summary>Where a piece is played from (m along the line from its middle) and the gain its voice takes over
    /// the source's own, so that played from there it is as loud as everything along it.</summary>
    public readonly record struct PiecePlace(float Along, float Gain);

    /// <summary>
    /// A piece of a line from <paramref name="from"/> to <paramref name="to"/> (m along it from its middle), as
    /// the mixer's law (Loudness.RenderedGain: flat inside <paramref name="reference"/>, then 1/d) would play
    /// every point of it from where it is: placed where its sound arrives from (the centroid of what each part
    /// of it brings to the ear) and given the gain that makes the law at that one point equal the law over the
    /// whole piece. Its voice carries its own events, so with this gain it is as loud as what it holds, and the
    /// pieces together are the whole line heard from every point of it, flat as you walk beside it however it
    /// is cut. Closed forms, no sampling: under a microsecond a piece. The edge fade at the end of the range is
    /// left out, being nearly the same over a piece.
    /// </summary>
    public static PiecePlace Place(LineView view, float from, float to, float reference)
    {
        double h = view.Off, r = MathF.Max(0.1f, reference);
        double u0 = from - view.Foot, u1 = to - view.Foot;
        if (!(u1 - u0 > 1e-4)) return new PiecePlace(0.5f * (from + to), 1f);
        double i0 = LawIntegral(u1, h, r) - LawIntegral(u0, h, r);
        double i1 = LawMoment(u1, h, r) - LawMoment(u0, h, r);
        double uc = i0 > 1e-30 ? Math.Clamp(i1 / i0, u0, u1) : 0.5 * (u0 + u1);
        double mean = i0 / (u1 - u0);
        double at = Math.Min(1.0, r / Math.Sqrt(h * h + uc * uc));
        return new PiecePlace((float)(view.Foot + uc), (float)(Math.Sqrt(Math.Max(0.0, mean)) / at));
    }

    /// <summary>The mean of the law's power over the whole line (Loudness.RenderedGain squared, without its edge
    /// fade): how loud the line is here, against one point of it at the same reference.</summary>
    public static float MeanLawPower(LineView view, float reference)
    {
        double h = view.Off, r = MathF.Max(0.1f, reference);
        double u0 = view.From, u1 = view.To;
        if (!(u1 - u0 > 1e-4)) return Math.Min(1f, (float)(r * r / (h * h)));
        return (float)((LawIntegral(u1, h, r) - LawIntegral(u0, h, r)) / (u1 - u0));
    }

    // ── A line along a path of its own (2026-10-17) ────────────────────────────────────────────────

    /// <summary>How much of the ear's view a path covers, degrees: each segment's angle at the ear, added along it.</summary>
    public static float SubtendedDegrees(LinePath path, Vector3 ear)
    {
        double sum = 0;
        for (int k = 0; k < path.Segments; k++) sum += SegmentAngle(path, k, ear);
        return (float)(sum * 180.0 / Math.PI);
    }

    /// <summary>The angle segment <paramref name="k"/> covers at the ear, radians, as a straight line's (View).</summary>
    private static double SegmentAngle(LinePath path, int k, Vector3 ear)
    {
        var (_, _, foot, off) = path.ViewOf(k, ear);
        double len = path.StartOf(k + 1) - path.StartOf(k);
        return Math.Atan2(len - foot, off) - Math.Atan2(-foot, off);
    }

    /// <summary>How many pieces a path wants from here (<see cref="WantedPieces(LineView, int)"/>, by the angle it covers).</summary>
    public static int WantedPieces(LinePath path, Vector3 ear, int previous) => WantedPieces(SubtendedDegrees(path, ear), previous);

    /// <summary>
    /// A path cut into <paramref name="pieces"/> pieces of equal angle at the ear, as <see cref="Cut(LineView, int)"/>
    /// cuts a straight line: the angle each segment covers added along it, and each edge found in its segment by the
    /// straight line's own law. Small beside the ear, long far along it or where it runs away from the ear.
    /// </summary>
    public static LineCut Cut(LinePath path, Vector3 ear, int pieces)
    {
        int n = Math.Clamp(pieces, 1, MaxPlaces);
        float half = path.HalfLength;
        var edges = new float[n + 1];
        edges[0] = -half;
        edges[n] = half;
        if (n == 1) return new LineCut(edges) { Path = path };
        int segs = path.Segments;
        Span<double> angle = segs <= 512 ? stackalloc double[segs] : new double[segs];
        double total = 0;
        for (int k = 0; k < segs; k++) total += angle[k] = SegmentAngle(path, k, ear);
        int seg = 0;
        double before = 0;
        for (int i = 1; i < n; i++)
        {
            double target = total * i / n;
            while (seg < segs - 1 && before + angle[seg] < target) { before += angle[seg]; seg++; }
            var (_, _, foot, off) = path.ViewOf(seg, ear);
            double a = Math.Clamp(Math.Atan2(-foot, off) + (target - before), -0.5 * Math.PI + 1e-6, 0.5 * Math.PI - 1e-6);
            double len = path.StartOf(seg + 1) - path.StartOf(seg);
            double t = Math.Clamp(foot + off * Math.Tan(a), 0.0, len);
            edges[i] = Math.Clamp((float)(path.StartOf(seg) + t) - half, edges[i - 1], half);
        }
        return new LineCut(edges) { Path = path };
    }

    /// <summary>Where a piece of a path is played from (in the world, and m along the path from its middle) and the
    /// gain its voice takes over the source's own.</summary>
    public readonly record struct PathPlace(Vector3 At, float Along, float Gain);

    /// <summary>
    /// A piece of a path from <paramref name="from"/> to <paramref name="to"/> (m along it from its middle) placed by
    /// the mixer's law as <see cref="Place(LineView, float, float, float)"/> places a piece of a straight line, segment by
    /// segment: at the centroid of what each part of it brings to the ear, with the gain that makes the law there equal
    /// the law's mean over the whole piece. A wide bed (<see cref="LinePath.WideBedMetres"/>) is three lines across it,
    /// each a third of it, so its near side brings more than its far. Closed forms, a few a segment.
    /// </summary>
    public static PathPlace Place(LinePath path, Vector3 ear, float from, float to, float reference)
    {
        double r = MathF.Max(0.1f, reference);
        float half = path.HalfLength;
        double s0 = from + half, s1 = to + half;
        if (!(s1 - s0 > 1e-4)) return new PathPlace(path.PointAt(0.5f * (from + to)), 0.5f * (from + to), 1f);
        bool wide = path.Width >= LinePath.WideBedMetres;
        int lines = wide ? 3 : 1;
        double power = 0, along = 0, cx = 0, cy = 0, cz = 0;
        for (int k = 0; k < path.Segments; k++)
        {
            double a = path.StartOf(k), b = path.StartOf(k + 1);
            if (b <= s0 || a >= s1) continue;
            double t0 = Math.Max(s0, a) - a, t1 = Math.Min(s1, b) - a;
            var across = wide ? path.AcrossOf(k) : Vector3.Zero;
            for (int j = 0; j < lines; j++)
            {
                var shift = across * ((j - 1) * path.Width / 3f);
                var (start, way, foot, off) = path.ViewOf(k, ear - shift);
                double u0 = t0 - foot, u1 = t1 - foot;
                double i0 = LawIntegral(u1, off, r) - LawIntegral(u0, off, r);
                if (!(i0 > 0)) continue;
                double uc = Math.Clamp((LawMoment(u1, off, r) - LawMoment(u0, off, r)) / i0, u0, u1);
                var at = start + shift + way * (float)(foot + uc);
                double w = i0 / lines;
                power += w;
                along += w * (a + foot + uc);
                cx += w * at.X; cy += w * at.Y; cz += w * at.Z;
            }
        }
        if (!(power > 1e-30)) return new PathPlace(path.PointAt(0.5f * (from + to)), 0.5f * (from + to), 1f);
        var c = new Vector3((float)(cx / power), (float)(cy / power), (float)(cz / power));
        double mean = power / (s1 - s0);
        double law = Math.Min(1.0, r / Math.Max(1e-6, Vector3.Distance(c, ear)));
        return new PathPlace(c, (float)(along / power) - half, (float)(Math.Sqrt(Math.Max(0.0, mean)) / law));
    }

    /// <summary>The mean of the law's power over a whole path (<see cref="MeanLawPower(LineView, float)"/>), a wide bed
    /// taken across itself as <see cref="Place(LinePath, Vector3, float, float, float)"/> takes it.</summary>
    public static float MeanLawPower(LinePath path, Vector3 ear, float reference)
    {
        double r = MathF.Max(0.1f, reference);
        bool wide = path.Width >= LinePath.WideBedMetres;
        int lines = wide ? 3 : 1;
        double power = 0;
        for (int k = 0; k < path.Segments; k++)
        {
            double len = path.StartOf(k + 1) - path.StartOf(k);
            var across = wide ? path.AcrossOf(k) : Vector3.Zero;
            for (int j = 0; j < lines; j++)
            {
                var (_, _, foot, off) = path.ViewOf(k, ear - across * ((j - 1) * path.Width / 3f));
                power += (LawIntegral(len - foot, off, r) - LawIntegral(-foot, off, r)) / lines;
            }
        }
        return path.Length > 1e-4f ? (float)(power / path.Length) : 0f;
    }

    /// <summary>∫₀ᵘ min(1, r²/(h² + s²)) ds: the law's power along a line h off it, flat inside r.</summary>
    private static double LawIntegral(double u, double h, double r)
    {
        double c = r > h ? Math.Sqrt(r * r - h * h) : 0.0;
        double a = Math.Abs(u);
        double v = a <= c ? a : c + r * r / h * (Math.Atan(a / h) - Math.Atan(c / h));
        return u < 0 ? -v : v;
    }

    /// <summary>∫₀ᵘ s min(1, r²/(h² + s²)) ds, for the centroid.</summary>
    private static double LawMoment(double u, double h, double r)
    {
        double c = r > h ? Math.Sqrt(r * r - h * h) : 0.0;
        double a = Math.Abs(u);
        return a <= c ? 0.5 * a * a : 0.5 * c * c + 0.5 * r * r * Math.Log((h * h + a * a) / (h * h + c * c));
    }

    /// <summary>
    /// How many outer places a source is given from what is left of the place budget: a fixed layout all of
    /// them or none (a crown with a bough missing is lopsided), a line as many as are left (it is cut coarser).
    /// </summary>
    public static int Grant(int outerWanted, bool cutsCoarser, ref int left)
    {
        if (outerWanted <= 0 || left <= 0) return 0;
        int given = outerWanted <= left ? outerWanted : cutsCoarser ? left : 0;
        left -= given;
        return given;
    }
}
