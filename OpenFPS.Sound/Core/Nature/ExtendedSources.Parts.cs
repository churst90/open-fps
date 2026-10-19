using System.Numerics;

namespace OpenFPS.Sound.Core.Nature;

// Fires and woods on the cut (2026-10-17, docs/FIRE.md 12.13): a fire's burning area cut at the ear as an open water's
// surface is, with its flames over runs of its pieces; a wood's crowns cut by the bearings they lie on.

public static partial class ExtendedSources
{
    /// <summary>The most voices a fire's flames are heard from over its bed (docs/FIRE.md 12.2: three).</summary>
    public const int MostFlames = 3;

    /// <summary>
    /// How many voices carry a fire's roar over <paramref name="bedPieces"/> pieces of its bed: none for one piece (the
    /// whole fire one voice), else one for every three pieces, at most <see cref="MostFlames"/>. The roar is the flames'
    /// turbulence, under 500 Hz, where the two ears hear a fire in front of them nearly alike whatever its width
    /// (docs/FIRE.md 7.2), so a few voices over it carry what the ear can tell; the crackle and every event are the bed's.
    /// </summary>
    public static int FlamesFor(int bedPieces) => bedPieces <= 1 ? 0 : Math.Min(MostFlames, (bedPieces + 2) / 3);

    /// <summary>The most pieces of a fire's bed <paramref name="places"/> voices hold with its flames.</summary>
    public static int BedPiecesFor(int places)
    {
        int n = Math.Clamp(places, 1, MaxPlaces);
        while (n > 1 && n + FlamesFor(n) > places) n--;
        return n;
    }

    /// <summary>The run of pieces, [From, To), flame <paramref name="flame"/> of <paramref name="flames"/> is over.</summary>
    public static (int From, int To) FlameRun(int pieces, int flames, int flame)
        => flames <= 0 ? (0, pieces) : (flame * pieces / flames, (flame + 1) * pieces / flames);

    /// <summary>The flame over piece <paramref name="piece"/>, or -1 for a cut without flames.</summary>
    public static int FlameOfPiece(int pieces, int flames, int piece)
    {
        for (int j = 0; j < flames; j++)
            if (piece < FlameRun(pieces, flames, j).To) return j;
        return flames - 1;
    }

    /// <summary>Points round a round bed's outline: a side under 4 % of its radius short of the circle.</summary>
    public const int FireCircleSides = 24;

    /// <summary>
    /// The outline of the ground a fire burns over (FireSpec.Outline, in its own frame: x across, z along), turned by
    /// <paramref name="turn"/>, moved to <paramref name="at"/> (its bed's middle) and closed: what its burning area is cut over
    /// at the ear.
    /// </summary>
    public static LinePath FireOutline(OpenFPS.Environment.FireShape shape, Vector3 at, Quaternion turn)
    {
        var local = new List<Vector3>();
        switch (shape.Kind)
        {
            case OpenFPS.Environment.FireShapeKind.Circle:
                for (int i = 0; i < FireCircleSides; i++)
                {
                    float a = MathF.Tau * i / FireCircleSides;
                    local.Add(new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * (0.5f * shape.Width));
                }
                break;
            case OpenFPS.Environment.FireShapeKind.Outline when shape.Points.Length >= 3:
                foreach (var p in shape.Points) local.Add(new Vector3(p.X, 0f, p.Y));
                break;
            default:
                float hw = 0.5f * shape.Width, hd = 0.5f * shape.Depth;
                local.Add(new Vector3(-hw, 0f, -hd));
                local.Add(new Vector3(hw, 0f, -hd));
                local.Add(new Vector3(hw, 0f, hd));
                local.Add(new Vector3(-hw, 0f, hd));
                break;
        }
        local.Add(local[0]);
        return LinePath.Of(at, turn, local.ToArray());
    }

    /// <summary>The flames' share of a fire's power, for the measures of where a source is heard from and how wide
    /// (AudioWorld.HeardTogether): its roar and its bed taken as halves. The synth plays each what it makes.</summary>
    public const float HeardFlameShare = 0.5f;

    /// <summary>
    /// Where place <paramref name="place"/> of an area's cut is played from now and the gain its voice takes over the source's
    /// own: a piece of a surface or of a fire's bed by the law over it from where the ear is now (PlaceArea), a run of a
    /// fire's pieces in its flames the same <see cref="AreaPieces.Rise"/> above, a piece of a wood by its crowns
    /// (<see cref="PlacePoints"/>).
    /// </summary>
    public static PathPlace PlaceOfArea(LineCut cut, Vector3 ear, int place, float reference)
    {
        var area = cut.Area!;
        if (area.Discrete) return PlacePoints(cut, ear, cut.PieceOfPlace(Math.Min(place, cut.Count - 1)), reference);
        if (place < cut.Count) return PlaceArea(cut, ear, cut.PieceOfPlace(place), reference);
        var (from, to) = FlameRun(cut.Count, area.Flames, Math.Clamp(place - cut.Count, 0, Math.Max(0, area.Flames - 1)));
        bool inside = Inside(cut.Path!, new Vector2(ear.X, ear.Z));
        return AreaPiece(cut.Path!, ear, area.Facing + cut.Edges[from], area.Facing + cut.Edges[to], reference,
                         area.Surface + area.Rise, inside, null, null).Place;
    }

    // ── A wood's crowns ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bearings a set of points covers at the ear (radians from +x toward +z), only those with a share: the whole
    /// circle less its widest gap between two of them, from the gap's far side round. Its middle is the direction the cut's
    /// angles are measured from. Nothing (a span of zero) when none has a share.
    /// </summary>
    private static (double From, double To, double Facing) PointsSpan(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> weights, Vector3 ear)
    {
        var bearings = new List<double>(points.Length);
        for (int i = 0; i < points.Length; i++)
            if (weights[i] > 0f) bearings.Add(Math.Atan2(points[i].Z - ear.Z, points[i].X - ear.X));
        if (bearings.Count == 0) return (0, 0, 0);
        bearings.Sort();
        double gap = bearings[0] + 2 * Math.PI - bearings[^1];
        double start = bearings[0];
        for (int i = 1; i < bearings.Count; i++)
        {
            double g = bearings[i] - bearings[i - 1];
            if (g > gap) { gap = g; start = bearings[i]; }
        }
        double span = 2 * Math.PI - gap;
        double facing = Math.IEEERemainder(start + 0.5 * span, 2 * Math.PI);
        return (-0.5 * span, 0.5 * span, facing);
    }

    /// <summary>How much of the ear's view a wood's crowns cover, degrees (those with a share).</summary>
    public static float PointsDegrees(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> weights, Vector3 ear)
    {
        var (from, to, _) = PointsSpan(points, weights, ear);
        return (float)((to - from) * 180.0 / Math.PI);
    }

    /// <summary>How many pieces a wood's crowns want from here, by the angle they cover (<see cref="WantedPieces(LineView, int)"/>).</summary>
    public static int WantedPointPieces(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> weights, Vector3 ear, int previous)
        => WantedPieces(PointsDegrees(points, weights, ear), previous);

    /// <summary>
    /// A wood's crowns (<paramref name="points"/>, each with its share of the wood in <paramref name="weights"/>) cut at the
    /// ear into <paramref name="pieces"/> pieces of equal angle across the bearings they cover, as a line and a surface are
    /// cut, a run of bearings with no crown in it given to the piece before it (a piece with nothing in it would be a voice
    /// with nothing to play). Each piece is placed by the mixer's law over its crowns (<see cref="PlacePoints"/>), and holds
    /// their share of the wood (<see cref="AreaPieces.PieceArea"/>). The samples are the crowns, piece by piece.
    /// </summary>
    public static LineCut CutPoints(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> weights, Vector3 ear, int pieces, float reference)
    {
        int n = Math.Clamp(pieces, 1, MaxPlaces);
        var (from, to, facing) = PointsSpan(points, weights, ear);
        var even = new float[n + 1];
        for (int i = 0; i <= n; i++) even[i] = (float)(from + (to - from) * i / n);
        // Which piece of the even cut each crown with a share is in.
        var probe = new AreaPieces
        {
            Places = Array.Empty<PathPlace>(), PieceArea = Array.Empty<float>(), SampleStart = Array.Empty<int>(),
            Samples = Array.Empty<Vector2>(), SampleArea = Array.Empty<float>(), Ear = ear, Facing = (float)facing,
        };
        var count = new int[n];
        var pieceOf = new int[points.Length];
        for (int k = 0; k < points.Length; k++)
        {
            pieceOf[k] = -1;
            if (!(weights[k] > 0f)) continue;
            pieceOf[k] = n == 1 ? 0 : probe.PieceOf(new Vector2(points[k].X, points[k].Z), even);
            count[pieceOf[k]]++;
        }
        // Empty runs to the piece before them (the first piece's to the first that has any).
        var edges = new List<float> { even[0] };
        var renumber = new int[n];
        int kept = -1;
        for (int i = 0; i < n; i++)
        {
            if (count[i] > 0)
            {
                if (kept >= 0) edges.Add(even[i]);
                kept++;
            }
            renumber[i] = Math.Max(0, kept);
        }
        edges.Add(even[n]);
        int m = edges.Count - 1;
        var starts = new int[m + 1];
        var samples = new List<Vector2>();
        var share = new List<float>();
        var height = new List<float>();
        var areas = new float[m];
        for (int i = 0; i < m; i++)
        {
            starts[i] = samples.Count;
            for (int k = 0; k < points.Length; k++)
            {
                if (pieceOf[k] < 0 || renumber[pieceOf[k]] != i) continue;
                samples.Add(new Vector2(points[k].X, points[k].Z));
                share.Add(weights[k]);
                height.Add(points[k].Y);
                areas[i] += weights[k];
            }
        }
        starts[m] = samples.Count;
        float nearest = float.MaxValue;
        for (int k = 0; k < points.Length; k++)
            if (weights[k] > 0f) nearest = MathF.Min(nearest, Vector3.Distance(points[k], ear));
        var cut = new LineCut(edges.ToArray())
        {
            Area = new AreaPieces
            {
                Places = new PathPlace[m], PieceArea = areas, SampleStart = starts, Samples = samples.ToArray(), SampleArea = share.ToArray(),
                SampleHeight = height.ToArray(), Discrete = true, Ear = ear, Facing = (float)facing,
                // Again when the ear has moved a tenth of the way to the nearest crown: each crown's share of the wood moves as
                // the ear does (WoodChorus.Weigh), and so does which piece it is in.
                RecutMetres = nearest < float.MaxValue ? Math.Clamp(0.1f * nearest, 0.5f, 5f) : 5f,
                Surface = height.Count > 0 ? height.Average() : ear.Y,
            },
        };
        for (int i = 0; i < m; i++) cut.Area.Places[i] = PlacePoints(cut, ear, i, reference);
        return cut;
    }

    /// <summary>
    /// A piece of a wood placed by the mixer's law over its crowns from where the ear is now: at the centroid of what each
    /// crown brings (its share times the law at its distance, Loudness.RenderedGain squared without its edge fade), with the
    /// gain that makes the law there the share-weighted mean over its crowns. So the piece is as loud as its crowns each at
    /// its own distance, and the pieces together as the wood's crowns all are, however it is cut.
    /// </summary>
    public static PathPlace PlacePoints(LineCut cut, Vector3 ear, int piece, float reference)
    {
        var area = cut.Area!;
        double r = MathF.Max(0.1f, reference);
        double law = 0, weight = 0, cx = 0, cy = 0, cz = 0;
        for (int k = area.SampleStart[piece]; k < area.SampleStart[piece + 1]; k++)
        {
            var at = new Vector3(area.Samples[k].X, area.SampleHeight?[k] ?? area.Surface, area.Samples[k].Y);
            double d2 = Vector3.DistanceSquared(at, ear);
            double l = d2 <= r * r ? 1.0 : r * r / d2;
            double w = area.SampleArea[k];
            law += w * l;
            weight += w;
            cx += w * l * at.X; cy += w * l * at.Y; cz += w * l * at.Z;
        }
        if (!(law > 1e-30) || !(weight > 0))
        {
            double mid = area.Facing + 0.5 * (cut.Edges[piece] + cut.Edges[piece + 1]);
            return new PathPlace(new Vector3(ear.X + (float)Math.Cos(mid), area.Surface, ear.Z + (float)Math.Sin(mid)), 0f, 0f);
        }
        var c = new Vector3((float)(cx / law), (float)(cy / law), (float)(cz / law));
        double there = Math.Min(1.0, r / Math.Max(1e-6, Vector3.Distance(c, ear)));
        return new PathPlace(c, 0f, (float)(Math.Sqrt(law / weight) / there));
    }

    /// <summary>The mean of the mixer's law over a wood's crowns from the ear, each by its share (<see cref="MeanLawPower(LinePath,
    /// Vector3, float)"/> for a line).</summary>
    public static float PointsMeanLawPower(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> weights, Vector3 ear, float reference)
    {
        double r = MathF.Max(0.1f, reference), law = 0, weight = 0;
        for (int k = 0; k < points.Length; k++)
        {
            if (!(weights[k] > 0f)) continue;
            double d2 = Vector3.DistanceSquared(points[k], ear);
            law += weights[k] * (d2 <= r * r ? 1.0 : r * r / d2);
            weight += weights[k];
        }
        return weight > 0 ? (float)(law / weight) : 0f;
    }
}
