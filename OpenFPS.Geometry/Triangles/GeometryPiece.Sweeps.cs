using System.Numerics;

namespace OpenFPS.Geometry;

/// <summary>The best touch found so far in one sweep, in the frame of the piece being asked (see <see cref="TriangleWorld.Sweep"/>).</summary>
internal struct SweepCandidate
{
    public float Fraction;
    public ShapeGap Gap;
    public int Triangle;
    public int Owner;
    public int Solid;
    public int Serial;
    public bool Found => Triangle >= 0;

    public static SweepCandidate None => new() { Fraction = 1f, Triangle = -1 };
}

public sealed partial class GeometryPiece
{
    /// <summary>Two touches this near in fraction are the same moment; the rest of the rule decides between them.</summary>
    private const float SameMoment = 1e-5f;

    /// <summary>
    /// The first triangle of this piece a shape meets as it moves by <paramref name="delta"/>, kept in
    /// <paramref name="best"/> if it is earlier than what is there. Both are in the piece's frame. At the same moment, a
    /// shape that began overlapping is held by the deepest, otherwise the smaller solid, then the lower owner (Ties).
    /// </summary>
    internal void Sweep<F>(in SweptShape shape, Vector3 delta, ref SweepCandidate best, ref Ties tie, GeometryLayers layers,
                           ref F filter, int ownerOverride) where F : IGeometryFilter
    {
        shape.Bounds(out var min0, out var max0);
        var moved = shape.Moved(delta);
        moved.Bounds(out var min1, out var max1);
        var grow = new Vector3(2f * SweepMath.Slop);
        var min = Vector3.Min(min0, min1) - grow;
        var max = Vector3.Max(max0, max1) + grow;

        if (Terrain != null) SweepTerrain(shape, delta, min, max, ref best, ref tie, layers, ref filter, ownerOverride);
        if (Tris.Length == 0) return;

        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref TriNodes[stack[--sp]];
            if (!BvhBuilder.Overlaps(n, min, max)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    ref readonly var tr = ref Tris[i];
                    ref readonly var surface = ref Surfaces[TriSurface[i]];
                    if ((surface.Layers & layers) == 0) continue;
                    ref readonly var rec = ref Solids[tr.Solid];
                    int owner = ownerOverride >= 0 ? ownerOverride : rec.Owner;
                    if (!filter.Accept(owner, surface)) continue;
                    Try(shape, delta, tr, Vector3.Zero, min, max, i, tr.Solid, rec.Footprint, owner, ref best, ref tie);
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
    }

    private void SweepTerrain<F>(in SweptShape shape, Vector3 delta, Vector3 min, Vector3 max, ref SweepCandidate best, ref Ties tie,
                                 GeometryLayers layers, ref F filter, int ownerOverride) where F : IGeometryFilter
    {
        var field = Terrain!;
        var cells = Scratch.Ints;
        cells.Clear();
        field.Overlapping(min - TerrainOffset, max - TerrainOffset, cells);
        if (cells.Count == 0) return;
        int owner = ownerOverride >= 0 ? ownerOverride : TerrainOwner;
        float footprint = field.Size * field.Size;
        // The field is asked in its own frame, which is the piece's less its offset.
        var inField = shape.Moved(-TerrainOffset);
        Span<int> skirts = stackalloc int[4];
        int count = cells.Count;
        for (int q = 0; q < count; q++)
        {
            int k = cells[q];
            TryTerrain(inField, delta, field, k, min, max, footprint, owner, layers, ref filter, ref best, ref tie);
            if ((k & 1) != 0 || !field.Skirted) continue;
            int cell = k >> 1;
            int c = field.CellsPerSide;
            int n = field.SkirtsOf(cell % c, cell / c, skirts);
            for (int s = 0; s < n; s++) TryTerrain(inField, delta, field, skirts[s], min, max, footprint, owner, layers, ref filter, ref best, ref tie);
        }
    }

    private void TryTerrain<F>(in SweptShape inField, Vector3 delta, Heightfield field, int k, Vector3 min, Vector3 max, float footprint,
                               int owner, GeometryLayers layers, ref F filter, ref SweepCandidate best, ref Ties tie) where F : IGeometryFilter
    {
        ref readonly var surface = ref Surfaces[TerrainSurfaceOf(k)];
        if ((surface.Layers & layers) == 0) return;
        if (!filter.Accept(owner, surface)) return;
        Try(inField, delta, field.Triangle(k), TerrainOffset, min, max, Tris.Length + k, Solids.Length + field.PrismOf(k), footprint, owner,
            ref best, ref tie);
    }

    /// <summary>One triangle against the shape; <paramref name="offset"/> is where the shape's frame lies from the piece's.</summary>
    private static void Try(in SweptShape shape, Vector3 delta, in GeometryTriangle tr, Vector3 offset, Vector3 min, Vector3 max,
                            int triangle, int solid, float footprint, int owner, ref SweepCandidate best, ref Ties tie)
    {
        var a = tr.V0; var b = tr.V0 + tr.E1; var c = tr.V0 + tr.E2;
        var lo = Vector3.Min(a, Vector3.Min(b, c)) + offset; var hi = Vector3.Max(a, Vector3.Max(b, c)) + offset;
        if (hi.X < min.X || lo.X > max.X || hi.Y < min.Y || lo.Y > max.Y || hi.Z < min.Z || lo.Z > max.Z) return;

        float limit = MathF.Min(1f, best.Fraction + SameMoment);
        if (!SweepMath.FirstTouch(shape, delta, a, b, c, limit, out float fraction, out var gap)) return;
        if (best.Found && fraction > best.Fraction + SameMoment) return;
        if (best.Found && MathF.Abs(fraction - best.Fraction) <= SameMoment)
        {
            if (fraction <= SameMoment)
            {
                // Both began overlapping: the deepest holds the shape, a hair of depth is the same depth.
                if (gap.Distance > best.Gap.Distance - SameMoment && !(MathF.Abs(gap.Distance - best.Gap.Distance) <= SameMoment && tie.Beats(footprint, owner)))
                    return;
            }
            else if (!tie.Beats(footprint, owner)) return;
        }
        gap.OnTriangle += offset;
        best = new SweepCandidate { Fraction = fraction, Gap = gap, Triangle = triangle, Owner = owner, Solid = solid, Serial = best.Serial + 1 };
        tie = new Ties(footprint, owner);
    }
}
