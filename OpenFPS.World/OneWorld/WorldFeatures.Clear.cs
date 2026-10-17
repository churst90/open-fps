namespace OpenFPS.Server.OneWorld;

/// <summary>
/// Nothing laid along a line goes into a building (docs/GEOMETRY.md 12.6, "A road stops at a wall"): a road's
/// surfaces, a sidewalk, a drive or a path stops short of every footprint it would enter, the cut that keeps the most
/// of it. gen_osm.py's ring_in_box, poly_area and cut_off_rings, the same arithmetic.
/// </summary>
public sealed partial class WorldFeatures
{
    /// <summary>How far a surface laid along a line stops short of a building's wall, metres.</summary>
    public const double ClearGap = 0.02;

    /// <summary>The shortest or narrowest piece of one worth keeping, metres.</summary>
    public const double ClearLeast = 0.3;

    /// <summary>The part of a ring (points (u, v) in a box's frame, not closed) inside the box, by clipping it to each
    /// side in turn; its corners bound it as tightly as the part itself.</summary>
    internal static List<(double U, double V)> RingInBox(List<(double U, double V)> ring, double u0, double u1, double v0, double v1)
    {
        var poly = ring;
        foreach (var (k, s, side) in new[] { (0, 1.0, u0), (0, -1.0, u1), (1, 1.0, v0), (1, -1.0, v1) })
        {
            var outp = new List<(double U, double V)>(poly.Count + 4);
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % poly.Count];
                double dp = s * ((k == 0 ? p.U : p.V) - side), dq = s * ((k == 0 ? q.U : q.V) - side);
                if (dp >= 0) outp.Add(p);
                if ((dp >= 0) != (dq >= 0))
                {
                    double t = dp / (dp - dq);
                    outp.Add((p.U + (q.U - p.U) * t, p.V + (q.V - p.V) * t));
                }
            }
            poly = outp;
            if (poly.Count == 0) break;
        }
        return poly;
    }

    /// <summary>A piece cut back off every building it would enter (<see cref="CutOffRings"/>, the buildings in order
    /// of their bounds so every tile cuts alike): none, itself, or the pieces left.</summary>
    private static List<Piece> ClearOf(Piece p, List<Standing> standing)
    {
        double L = Math.Sqrt((p.Bx - p.Ax) * (p.Bx - p.Ax) + (p.Bz - p.Az) * (p.Bz - p.Az));
        var f = new WorldBuildings.Frame(p.Ax, p.Az, Math.Atan2(p.Bz - p.Az, p.Bx - p.Ax));
        double u0 = -p.Ext0, u1 = L + p.Ext1;
        var (x0, x1, z0, z1) = Bounds(f, u0, u1, p.V0, p.V1);
        var near = standing.Where(s => s.X1 >= x0 && s.X0 <= x1 && s.Z1 >= z0 && s.Z0 <= z1)
                           .OrderBy(s => s.X0).ThenBy(s => s.Z0).ThenBy(s => s.X1).ThenBy(s => s.Z1).ToList();
        if (near.Count == 0) return new List<Piece> { p };
        var cut = CutOffRings(u0, u1, p.V0, p.V1, near.Select(s => s.Ring.Select(q => f.L(q.X, q.Z)).ToList()));
        if (cut.Count == 1 && cut[0] == (u0, u1, p.V0, p.V1)) return new List<Piece> { p };
        return cut.Select(c => p with { Ext0 = -c.U0, Ext1 = c.U1 - L, V0 = c.V0, V1 = c.V1 }).ToList();
    }

    internal static double PolyArea(List<(double U, double V)> poly)
    {
        int n = poly.Count;
        if (n < 3) return 0;
        double a = 0;
        for (int i = 0; i < n; i++) a += poly[i].U * poly[(i + 1) % n].V - poly[(i + 1) % n].U * poly[i].V;
        return Math.Abs(a) / 2;
    }

    /// <summary>
    /// A box along a line (u along it, v across) cut back off every footprint it enters (rings in the box's frame):
    /// for each in turn, the cut that keeps the most of it, shorter from either end or with a gap where it passes
    /// through (in u), or narrower from either side (in v). The pieces left, each clear of every ring.
    /// </summary>
    internal static List<(double U0, double U1, double V0, double V1)> CutOffRings(double u0, double u1, double v0, double v1,
                                                                                   IEnumerable<List<(double U, double V)>> rings)
    {
        var pieces = new List<(double U0, double U1, double V0, double V1)> { (u0, u1, v0, v1) };
        foreach (var ring in rings)
        {
            var next = new List<(double U0, double U1, double V0, double V1)>();
            foreach (var (a0, a1, b0, b1) in pieces)
            {
                var part = RingInBox(ring, a0, a1, b0, b1);
                if (PolyArea(part) <= 1e-4) { next.Add((a0, a1, b0, b1)); continue; }
                double pu0 = part.Min(p => p.U), pu1 = part.Max(p => p.U), pv0 = part.Min(p => p.V), pv1 = part.Max(p => p.V);
                var cuts = new List<List<(double U0, double U1, double V0, double V1)>>
                {
                    new[] { (a0, pu0 - ClearGap, b0, b1), (pu1 + ClearGap, a1, b0, b1) }.Where(c => c.Item2 - c.Item1 >= ClearLeast).ToList(),
                };
                if (pv0 - ClearGap - b0 >= ClearLeast) cuts.Add(new() { (a0, a1, b0, pv0 - ClearGap) });
                if (b1 - pv1 - ClearGap >= ClearLeast) cuts.Add(new() { (a0, a1, pv1 + ClearGap, b1) });
                // The first that keeps the most, as Python's max().
                var best = cuts[0];
                double kept = Kept(best);
                for (int c = 1; c < cuts.Count; c++)
                    if (Kept(cuts[c]) > kept) { best = cuts[c]; kept = Kept(best); }
                next.AddRange(best);
            }
            pieces = next;
        }
        return pieces;

        static double Kept(List<(double U0, double U1, double V0, double V1)> cut) => cut.Sum(c => (c.U1 - c.U0) * (c.V1 - c.V0));
    }
}
