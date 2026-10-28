using System.Numerics;

namespace OpenFPS.Geometry;

public sealed partial class TriangleWorld
{
    /// <summary>
    /// The first thing a shape meets as it is moved by <paramref name="delta"/> from where it is (docs/PHYSICS_BOUNDARY.md
    /// 18.2, step 5). It is conservative: the shape is stopped within <see cref="SweepMath.Slop"/> of the surface it
    /// meets, or sooner, and never passes through a triangle however long the move or thin the triangle. Faces
    /// count from both sides; a shape only touching a surface and moving along it or away does not meet it, and a
    /// shape that begins overlapping one meets it at fraction 0 (<see cref="SweepHit.Embedded"/>).
    /// </summary>
    public bool Sweep<F>(in SweptShape shape, Vector3 delta, GeometryLayers layers, ref F filter, out SweepHit hit)
        where F : IGeometryFilter
    {
        hit = default; hit.Triangle = -1; hit.Fraction = 1f;
        if (_instances.Length == 0) return false;
        if (!float.IsFinite(delta.X + delta.Y + delta.Z)) return false;

        shape.Bounds(out var min0, out var max0);
        shape.Moved(delta).Bounds(out var min1, out var max1);
        var grow = new Vector3(2f * SweepMath.Slop);
        var min = Vector3.Min(min0, min1) - grow;
        var max = Vector3.Max(max0, max1) + grow;

        var best = SweepCandidate.None;
        int bestInstance = -1;
        var tie = Ties.None;
        Span<int> stack = stackalloc int[BvhBuilder.MaxDepth + 2];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly var n = ref _nodes[stack[--sp]];
            if (!BvhBuilder.Overlaps(n, min, max)) continue;
            if (n.Count > 0)
            {
                for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                {
                    int ii = _order[i];
                    ref readonly var inst = ref _instances[ii];
                    if (inst.Piece.IsEmpty) continue;
                    if (inst.Max.X < min.X || inst.Min.X > max.X || inst.Max.Y < min.Y || inst.Min.Y > max.Y || inst.Max.Z < min.Z || inst.Min.Z > max.Z) continue;
                    var before = best;
                    // In the piece's frame, where the precision of a tile's far corner is as good as its near one.
                    inst.Piece.Sweep(shape.InFrame(inst), inst.DirectionToLocal(delta), ref best, ref tie, layers, ref filter, inst.Owner);
                    if (best.Serial != before.Serial)
                        bestInstance = ii;
                }
                continue;
            }
            stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
        }
        if (!best.Found) return false;

        ref readonly var found = ref _instances[bestInstance];
        hit.Fraction = best.Fraction;
        hit.Distance = best.Fraction * delta.Length();
        hit.Point = found.ToWorld(best.Gap.OnTriangle);
        hit.Normal = Vector3.Normalize(found.DirectionToWorld(best.Gap.Normal));
        hit.Solid = new SolidRef(bestInstance, best.Solid);
        hit.Triangle = best.Triangle;
        hit.Owner = best.Owner;
        hit.Embedded = best.Gap.Embedded;
        return true;
    }

    /// <summary>The first thing a shape meets, whatever it is (every layer, every owner).</summary>
    public bool Sweep(in SweptShape shape, Vector3 delta, out SweepHit hit)
    {
        var all = new AcceptAll();
        return Sweep(shape, delta, GeometryLayers.All, ref all, out hit);
    }
}
