using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// One flat face of a mesh: its connected triangles that lie in one plane and are one surface, merged
/// (docs/GEOMETRY.md 3.4). A box has six; a wedge's slope is one; a stair has a tread and a riser a step; a
/// twelve-sided column has twelve narrow sides, each too small to mirror much and so a scatterer. In the mesh's
/// own frame.
/// </summary>
/// <param name="Normal">Out of the solid, unit length.</param>
/// <param name="Centre">The middle of its area.</param>
/// <param name="RectCentre">The middle of the rectangle round it in its plane.</param>
/// <param name="HalfU">Along the facet's longest edge, half the rectangle's extent that way.</param>
/// <param name="HalfV">Across it in its plane, half the rectangle's extent that way.</param>
/// <param name="TriStart">Its first triangle in <see cref="FacetSet.Triangles"/>.</param>
public readonly record struct Facet(Vector3 Normal, Vector3 Centre, float Area, Vector3 RectCentre, Vector3 HalfU, Vector3 HalfV,
                                    byte Slot, int TriStart, int TriCount)
{
    /// <summary>How much of the rectangle round it the facet fills: 1 for a rectangle, a half for a right triangle.</summary>
    public float Fill => Area / MathF.Max(1e-12f, 4f * HalfU.Length() * HalfV.Length());
}

/// <summary>A mesh's facets and the triangles of each, in the order of their first triangle.</summary>
public sealed class FacetSet
{
    public Facet[] Items { get; }
    /// <summary>Triangle indices of the mesh, facet by facet.</summary>
    public int[] Triangles { get; }
    private readonly MeshAsset _mesh;

    private FacetSet(MeshAsset mesh, Facet[] items, int[] triangles) { _mesh = mesh; Items = items; Triangles = triangles; }

    /// <summary>Whether a point in the facet's plane (in the mesh's frame) lies on it, within <paramref name="slack"/> metres.</summary>
    public bool Contains(int facet, Vector3 p, float slack = 1e-4f)
    {
        var f = Items[facet];
        var v = _mesh.Vertices; var idx = _mesh.Indices;
        for (int i = 0; i < f.TriCount; i++)
        {
            int t = Triangles[f.TriStart + i];
            Vector3 a = v[idx[3 * t]], b = v[idx[3 * t + 1]], c = v[idx[3 * t + 2]];
            if (Side(a, b, p, f.Normal) >= -slack && Side(b, c, p, f.Normal) >= -slack && Side(c, a, p, f.Normal) >= -slack) return true;
        }
        return false;

        static float Side(Vector3 a, Vector3 b, Vector3 p, Vector3 n)
        {
            var e = b - a;
            float len = e.Length();
            return len < 1e-12f ? 0f : Vector3.Dot(Vector3.Cross(e, p - a), n) / len;
        }
    }

    /// <summary>The facets of a mesh: triangles sharing an edge, of one surface, whose planes agree within a
    /// millionth of a turn and a tenth of a millimetre, are one.</summary>
    public static FacetSet Of(MeshAsset m)
    {
        int n = m.TriangleCount;
        var v = m.Vertices; var idx = m.Indices;
        var normal = new Vector3[n]; var offset = new float[n]; var area = new float[n];
        for (int t = 0; t < n; t++)
        {
            Vector3 a = v[idx[3 * t]], b = v[idx[3 * t + 1]], c = v[idx[3 * t + 2]];
            var cr = Vector3.Cross(b - a, c - a);
            float len = cr.Length();
            area[t] = len * 0.5f;
            normal[t] = len > 0f ? cr / len : Vector3.UnitY;
            offset[t] = Vector3.Dot(normal[t], a);
        }
        var parent = new int[n];
        for (int t = 0; t < n; t++) parent[t] = t;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var edgeOwner = new Dictionary<(int, int), int>();
        for (int t = 0; t < n; t++)
            for (int e = 0; e < 3; e++)
            {
                int u = idx[3 * t + e], w = idx[3 * t + (e + 1) % 3];
                var key = u < w ? (u, w) : (w, u);
                if (!edgeOwner.TryGetValue(key, out int other)) { edgeOwner[key] = t; continue; }
                if (m.TriangleSurface[t] != m.TriangleSurface[other]) continue;
                if (Vector3.Dot(normal[t], normal[other]) < 1f - 1e-6f || MathF.Abs(offset[t] - offset[other]) > 1e-4f) continue;
                int ra = Find(t), rb = Find(other);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        var groups = new SortedDictionary<int, List<int>>();
        for (int t = 0; t < n; t++)
        {
            int r = Find(t);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<int>();
            g.Add(t);
        }
        var items = new List<Facet>(groups.Count);
        var tris = new List<int>(n);
        foreach (var (_, g) in groups)
        {
            // The plane of its biggest triangle; its longest outside edge says which way the rectangle runs.
            int big = g[0];
            foreach (int t in g) if (area[t] > area[big]) big = t;
            var nrm = normal[big];
            var inGroup = new Dictionary<(int, int), int>();
            foreach (int t in g)
                for (int e = 0; e < 3; e++)
                {
                    int ea = idx[3 * t + e], eb = idx[3 * t + (e + 1) % 3];
                    var key = ea < eb ? (ea, eb) : (eb, ea);
                    inGroup[key] = inGroup.GetValueOrDefault(key) + 1;
                }
            float total = 0f; var centre = Vector3.Zero; var along = Vector3.Zero; float longest = -1f;
            foreach (int t in g)
            {
                Vector3 a = v[idx[3 * t]], b = v[idx[3 * t + 1]], c = v[idx[3 * t + 2]];
                total += area[t];
                centre += (a + b + c) * (area[t] / 3f);
                for (int e = 0; e < 3; e++)
                {
                    int ea = idx[3 * t + e], eb = idx[3 * t + (e + 1) % 3];
                    if (inGroup[ea < eb ? (ea, eb) : (eb, ea)] != 1) continue;
                    float l = (v[eb] - v[ea]).LengthSquared();
                    if (l > longest) { longest = l; along = v[eb] - v[ea]; }
                }
            }
            centre = total > 0f ? centre / total : v[idx[3 * g[0]]];
            var u = along - nrm * Vector3.Dot(along, nrm);
            u = u.LengthSquared() > 1e-20f ? Vector3.Normalize(u) : Vector3.Normalize(Vector3.Cross(nrm, MathF.Abs(nrm.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var w = Vector3.Cross(nrm, u);
            float u0 = float.MaxValue, u1 = float.MinValue, w0 = float.MaxValue, w1 = float.MinValue;
            foreach (int t in g)
                for (int k = 0; k < 3; k++)
                {
                    var p = v[idx[3 * t + k]] - centre;
                    float pu = Vector3.Dot(p, u), pw = Vector3.Dot(p, w);
                    u0 = MathF.Min(u0, pu); u1 = MathF.Max(u1, pu); w0 = MathF.Min(w0, pw); w1 = MathF.Max(w1, pw);
                }
            var rect = centre + u * ((u0 + u1) * 0.5f) + w * ((w0 + w1) * 0.5f);
            items.Add(new Facet(nrm, centre, total, rect, u * ((u1 - u0) * 0.5f), w * ((w1 - w0) * 0.5f),
                                m.TriangleSurface[g[0]], tris.Count, g.Count));
            tris.AddRange(g);
        }
        return new FacetSet(m, items.ToArray(), tris.ToArray());
    }
}

/// <summary>What <see cref="MeshCheck.Check"/> found (docs/GEOMETRY.md 4.5).</summary>
/// <param name="OpenEdges">Edges with one triangle on them (a hole in the surface).</param>
/// <param name="CrowdedEdges">Edges with more than two triangles, or two going the same way.</param>
/// <param name="Volume">Signed: positive when the triangles face out of what they enclose.</param>
/// <param name="SmallestFeature">The shortest height of any triangle, metres: under a centimetre is a sliver.</param>
public readonly record struct MeshReport(int Triangles, int OpenEdges, int CrowdedEdges, int Degenerate, int Slivers,
                                         double Volume, double Area, float SmallestFeature)
{
    /// <summary>A solid: every edge shared by two triangles going opposite ways, and the triangles facing out.</summary>
    public bool Closed => OpenEdges == 0 && CrowdedEdges == 0 && Volume > 0;

    public override string ToString()
        => $"{Triangles} triangles, {(Closed ? "closed" : $"not closed ({OpenEdges} open edges, {CrowdedEdges} crowded, volume {Volume:0.###})")}"
           + (Degenerate > 0 ? $", {Degenerate} with no area" : "") + (Slivers > 0 ? $", {Slivers} slivers" : "")
           + $", smallest feature {SmallestFeature * 100:0.##} cm";
}

/// <summary>Whether a mesh is a solid fit for the world: closed, facing out, no triangles without area and no
/// slivers (docs/GEOMETRY.md 4.5).</summary>
public static class MeshCheck
{
    /// <summary>A triangle under this tall is a sliver, metres.</summary>
    public const float SliverMetres = 0.001f;

    public static MeshReport Check(MeshAsset m) => Check(m.Vertices, m.Indices);

    public static MeshReport Check(IReadOnlyList<Vector3> v, IReadOnlyList<int> idx)
    {
        int n = idx.Count / 3, degenerate = 0, slivers = 0;
        double volume = 0, area = 0;
        float smallest = float.MaxValue;
        var directed = new Dictionary<(int, int), int>();
        for (int t = 0; t < n; t++)
        {
            int ia = idx[3 * t], ib = idx[3 * t + 1], ic = idx[3 * t + 2];
            Vector3 a = v[ia], b = v[ib], c = v[ic];
            var cr = Vector3.Cross(b - a, c - a);
            float twice = cr.Length();
            area += twice * 0.5;
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
            float longest = MathF.Max((b - a).Length(), MathF.Max((c - b).Length(), (a - c).Length()));
            float height = longest > 0f ? twice / longest : 0f;
            if (twice < 2e-8f) degenerate++;
            else if (height < SliverMetres) slivers++;
            smallest = MathF.Min(smallest, height);
            foreach (var e in new[] { (ia, ib), (ib, ic), (ic, ia) })
                directed[e] = directed.TryGetValue(e, out int k) ? k + 1 : 1;
        }
        int open = 0, crowded = 0;
        foreach (var ((u, w), count) in directed)
        {
            int back = directed.TryGetValue((w, u), out int k) ? k : 0;
            if (count > 1 || back > 1) crowded++;
            else if (back == 0) open++;
        }
        return new MeshReport(n, open, crowded, degenerate, slivers, volume, area, n == 0 ? 0f : smallest);
    }
}
