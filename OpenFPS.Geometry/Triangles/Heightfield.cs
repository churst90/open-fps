using System.Numerics;
using System.Runtime.CompilerServices;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// A terrain tile (docs/GEOMETRY.md 2.3): heights on a square grid of posts, a material per cell, in its
/// own frame (post (0, 0) at x = z = 0; heights are world heights). Each cell is two triangles split along
/// the diagonal from post (i, j) to (i + 1, j + 1). Under each triangle is a prism down to
/// <see cref="FloorY"/>: the solid a body meets and a point can be inside, so the ground is something you
/// stand on and cannot walk into, and a ray through a hill enters and leaves it.
///
/// <para>Immutable, and built from the same centimetres on the server and every client, so both hold the
/// same floats. Queries never use a tree: a point's height is a lookup, a ray walks the cells it crosses.</para>
///
/// <para>A skirted tile (<see cref="Skirted"/>) also shows the outer sides of its edge prisms to rays: a
/// strip from each edge down to the floor, after its surface triangles in the numbering.</para>
/// </summary>
public sealed class Heightfield
{
    /// <summary>How far the ground goes down under its lowest post, metres: deep enough that nothing
    /// walks or falls out of the bottom of it.</summary>
    public const float Depth = 20f;

    public int Posts { get; }
    public int CellsPerSide => Posts - 1;
    public float Spacing { get; }
    /// <summary>Post to post across the whole tile, metres.</summary>
    public float Size { get; }
    /// <summary>World heights, row by row: post (i, j) is at [j * Posts + i], i along X, j along Z.</summary>
    public float[] Heights { get; }
    /// <summary>Each cell's material, as an index into <see cref="Materials"/>: [j * CellsPerSide + i].</summary>
    public byte[] Cells { get; }
    public string[] Materials { get; }
    public float MinY { get; }
    public float MaxY { get; }
    public float FloorY => MinY - Depth;
    /// <summary>A hash of the exact bits it was made from.</summary>
    public ulong Hash { get; }
    /// <summary>
    /// Whether its four edges hang a skirt down to <see cref="FloorY"/>: ground sent at the far ring's
    /// spacing, whose edge lies between a finer neighbour's posts (docs/WORLD_STREAMING.md, Coarse ground).
    /// The skirt closes the crack between the two edges; ground at full spacing never has one.
    /// </summary>
    public bool Skirted { get; }
    /// <summary>Two triangles a cell, and two for each cell's side on the tile's edge when skirted.</summary>
    public int TriangleCount => SurfaceTriangleCount + (Skirted ? 8 * CellsPerSide : 0);
    public int SurfaceTriangleCount => 2 * CellsPerSide * CellsPerSide;

    // Each cell's lowest and highest corner, for the walk along a ray.
    private readonly float[] _cellMin, _cellMax;

    public Heightfield(int posts, float spacing, float[] heights, byte[]? cells, string[]? materials, bool skirted = false)
    {
        if (posts < 2) throw new ArgumentOutOfRangeException(nameof(posts), "a terrain tile needs at least two posts a side");
        if (!(spacing > 0f)) throw new ArgumentOutOfRangeException(nameof(spacing));
        if (heights.Length != posts * posts) throw new ArgumentException("one height per post", nameof(heights));
        int c = posts - 1;
        cells ??= new byte[c * c];
        if (cells.Length != c * c) throw new ArgumentException("one material per cell", nameof(cells));
        materials = materials is { Length: > 0 } ? materials : new[] { "Dirt" };
        foreach (byte m in cells)
            if (m >= materials.Length) throw new ArgumentException("a cell names a material that is not in the list", nameof(cells));
        Posts = posts; Spacing = spacing; Size = c * spacing;
        Heights = heights; Cells = cells; Materials = materials;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (float h in heights)
        {
            if (!float.IsFinite(h)) throw new ArgumentException("a height is not a number", nameof(heights));
            lo = MathF.Min(lo, h); hi = MathF.Max(hi, h);
        }
        MinY = lo; MaxY = hi;
        Skirted = skirted;
        _cellMin = new float[c * c]; _cellMax = new float[c * c];
        for (int j = 0; j < c; j++)
            for (int i = 0; i < c; i++)
            {
                float a = heights[j * posts + i], b = heights[j * posts + i + 1];
                float d = heights[(j + 1) * posts + i], e = heights[(j + 1) * posts + i + 1];
                bool edge = skirted && (i == 0 || j == 0 || i == c - 1 || j == c - 1);
                // An edge cell's skirt reaches the floor, so the walk must visit it for a ray that low.
                _cellMin[j * c + i] = edge ? FloorY : MathF.Min(MathF.Min(a, b), MathF.Min(d, e));
                _cellMax[j * c + i] = MathF.Max(MathF.Max(a, b), MathF.Max(d, e));
            }
        Hash = ContentHash(posts, spacing, heights, cells, materials, skirted);
    }

    /// <summary>A tile from whole centimetres over <paramref name="baseY"/>, as the wire and the map carry
    /// it: every machine turns the same numbers into the same floats.</summary>
    public static Heightfield FromCentimetres(int posts, float spacing, float baseY, short[] centimetres, byte[]? cells, string[]? materials,
                                             bool skirted = false)
    {
        if (centimetres.Length != posts * posts) throw new ArgumentException("one height per post", nameof(centimetres));
        var h = new float[centimetres.Length];
        for (int k = 0; k < h.Length; k++) h[k] = baseY + centimetres[k] * 0.01f;
        return new Heightfield(posts, spacing, h, cells, materials, skirted);
    }

    /// <summary>Heights as whole centimetres over <paramref name="baseY"/>, rounded: what
    /// <see cref="FromCentimetres"/> reads back. Throws when a height is more than 327 m from the base.</summary>
    public static short[] ToCentimetres(float[] heights, float baseY)
    {
        var cm = new short[heights.Length];
        for (int k = 0; k < cm.Length; k++)
        {
            double v = Math.Round((heights[k] - (double)baseY) * 100.0);
            if (v < short.MinValue || v > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(heights), "a height is too far from the base for centimetres");
            cm[k] = (short)v;
        }
        return cm;
    }

    // ═══ Where things are ═════════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float HeightOfPost(int i, int j) => Heights[j * Posts + i];

    /// <summary>The cell (x, z) is over, and where in it (0 to 1 each way). False outside the tile. The
    /// far edges belong to the last cells.</summary>
    public bool CellAt(float x, float z, out int i, out int j, out float fx, out float fz)
    {
        i = j = 0; fx = fz = 0f;
        if (!(x >= 0f && z >= 0f && x <= Size && z <= Size)) return false;
        int c = CellsPerSide;
        i = Math.Min((int)(x / Spacing), c - 1);
        j = Math.Min((int)(z / Spacing), c - 1);
        fx = (x - i * Spacing) / Spacing;
        fz = (z - j * Spacing) / Spacing;
        return true;
    }

    /// <summary>Which of a cell's two triangles a point at (fx, fz) in it is over: 0 below the diagonal
    /// (fx at least fz), 1 above it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HalfOf(float fx, float fz) => fx >= fz ? 0 : 1;

    /// <summary>The triangle's number: two a cell, row by row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int TriangleOf(int i, int j, int half) => 2 * (j * CellsPerSide + i) + half;

    /// <summary>The ground's height at (x, z), on the plane of the triangle under it; NaN outside.</summary>
    public float HeightAt(float x, float z)
    {
        if (!CellAt(x, z, out int i, out int j, out _, out _)) return float.NaN;
        var tr = Triangle(TriangleOf(i, j, HalfOf((x - i * Spacing) / Spacing, (z - j * Spacing) / Spacing)));
        return HeightOnTriangle(tr, x, z);
    }

    /// <summary>The height of a triangle's plane at (x, z).</summary>
    public static float HeightOnTriangle(in GeometryTriangle tr, float x, float z)
    {
        var n = tr.Normal;
        return tr.V0.Y - (n.X * (x - tr.V0.X) + n.Z * (z - tr.V0.Z)) / n.Y;
    }

    /// <summary>The material of the cell under (x, z), or null outside.</summary>
    public string? MaterialAt(float x, float z)
        => CellAt(x, z, out int i, out int j, out _, out _) ? Materials[Cells[j * CellsPerSide + i]] : null;

    /// <summary>The cell a triangle belongs to: a skirt's is the edge cell over it.</summary>
    public int CellOfTriangle(int k) => PrismOf(k) >> 1;

    /// <summary>The prism (the surface triangle) a triangle is a face of: itself, or for a skirt the edge
    /// prism whose outer side it is.</summary>
    public int PrismOf(int k)
    {
        int surface = SurfaceTriangleCount;
        if (k < surface) return k;
        SkirtOf(k, out int i, out int j, out int half, out _);
        return TriangleOf(i, j, half);
    }

    // Skirt triangle k: side s (0 west, x = 0; 1 east; 2 south, z = 0; 3 north) of the edge cell at q along
    // it, its first or second triangle; the cell, the surface triangle that side belongs to, and which of
    // that triangle's three sides it is (0 a-b, 1 b-c, 2 c-a, corners as Triangle gives them).
    private void SkirtOf(int k, out int i, out int j, out int half, out int side)
    {
        int c = CellsPerSide, m = (k - SurfaceTriangleCount) >> 1, s = m / c, q = m % c;
        switch (s)
        {
            case 0: i = 0; j = q; half = 1; side = 0; break;          // (i, j) to (i, j+1)
            case 1: i = c - 1; j = q; half = 0; side = 1; break;      // (i+1, j+1) to (i+1, j)
            case 2: i = q; j = 0; half = 0; side = 2; break;          // (i+1, j) to (i, j)
            default: i = q; j = c - 1; half = 1; side = 1; break;     // (i, j+1) to (i+1, j+1)
        }
    }

    /// <summary>The skirt triangles under an edge cell's sides on the tile's edge (none unless skirted, two
    /// a side), into <paramref name="into"/>; how many.</summary>
    public int SkirtsOf(int i, int j, Span<int> into)
    {
        if (!Skirted) return 0;
        int c = CellsPerSide, n = 0, b = SurfaceTriangleCount;
        void Side(int s, int q, Span<int> to) { to[n++] = b + 2 * (s * c + q); to[n++] = b + 2 * (s * c + q) + 1; }
        if (i == 0) Side(0, j, into);
        if (i == c - 1) Side(1, j, into);
        if (j == 0) Side(2, i, into);
        if (j == c - 1) Side(3, i, into);
        return n;
    }

    /// <summary>
    /// Triangle <paramref name="k"/> of the surface, wound so its normal points up (out of the ground).
    /// Half 0 is (i, j), (i+1, j+1), (i+1, j); half 1 is (i, j), (i, j+1), (i+1, j+1). A skirt's triangle
    /// is the outer side of its edge prism, as <see cref="Prism"/> makes it, wound outward.
    /// </summary>
    public GeometryTriangle Triangle(int k)
    {
        if (k >= SurfaceTriangleCount) return SkirtTriangle(k);
        int cell = k >> 1, c = CellsPerSide;
        int i = cell % c, j = cell / c;
        var p00 = Post(i, j);
        var p11 = Post(i + 1, j + 1);
        Vector3 a = p00, b, d;
        if ((k & 1) == 0) { b = p11; d = Post(i + 1, j); }
        else { b = Post(i, j + 1); d = p11; }
        return new GeometryTriangle { V0 = a, E1 = b - a, E2 = d - a, Solid = k };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 Post(int i, int j) => new(i * Spacing, Heights[j * Posts + i], j * Spacing);

    private GeometryTriangle SkirtTriangle(int k)
    {
        SkirtOf(k, out int i, out int j, out int half, out int side);
        var t = Triangle(TriangleOf(i, j, half));
        Vector3 a = t.V0, b = t.V0 + t.E1, c = t.V0 + t.E2;
        // The side from p to q, as the prism's: (p, p0, q0) then (p, q0, q).
        Vector3 p = side == 0 ? a : side == 1 ? b : c, q = side == 0 ? b : side == 1 ? c : a;
        var p0 = new Vector3(p.X, FloorY, p.Z); var q0 = new Vector3(q.X, FloorY, q.Z);
        return (k & 1) == 0 ? new GeometryTriangle { V0 = p, E1 = p0 - p, E2 = q0 - p, Solid = k }
                            : new GeometryTriangle { V0 = p, E1 = q0 - p, E2 = q - p, Solid = k };
    }

    /// <summary>The bounds of the prism under triangle <paramref name="k"/>.</summary>
    public (Vector3 Min, Vector3 Max) PrismBounds(int k)
    {
        var t = Triangle(k);
        Vector3 b = t.V0 + t.E1, d = t.V0 + t.E2;
        var lo = Vector3.Min(t.V0, Vector3.Min(b, d));
        var hi = Vector3.Max(t.V0, Vector3.Max(b, d));
        return (new Vector3(lo.X, FloorY, lo.Z), hi);
    }

    /// <summary>How many triangles a prism has: its top, its bottom and two for each of its three sides.</summary>
    public const int PrismTriangleCount = 8;
    /// <summary>How many face planes a prism has.</summary>
    public const int PrismPlaneCount = 5;

    /// <summary>The prism under triangle <paramref name="k"/> as eight outward triangles (three corners each)
    /// and five face planes (n, d: n·p - d &lt; 0 inside), shifted by <paramref name="shift"/>.</summary>
    public void Prism(int k, Vector3 shift, Span<Vector3> corners, Span<Vector4> planes)
    {
        var t = Triangle(k);
        Vector3 a = t.V0 + shift, b = t.V0 + t.E1 + shift, c = t.V0 + t.E2 + shift;
        float floor = FloorY + shift.Y;
        var a0 = new Vector3(a.X, floor, a.Z); var b0 = new Vector3(b.X, floor, b.Z); var c0 = new Vector3(c.X, floor, c.Z);
        int n = 0;
        void Tri(Vector3 p, Vector3 q, Vector3 r, Span<Vector3> into) { into[n++] = p; into[n++] = q; into[n++] = r; }
        Tri(a, b, c, corners);                         // the top, up
        Tri(a0, c0, b0, corners);                      // the bottom, down
        Tri(a, a0, b0, corners); Tri(a, b0, b, corners);   // side a-b
        Tri(b, b0, c0, corners); Tri(b, c0, c, corners);   // side b-c
        Tri(c, c0, a0, corners); Tri(c, a0, a, corners);   // side c-a
        int p = 0;
        for (int f = 0; f < 4; f++)
        {
            // The top, the bottom and the first triangle of each side give the five planes.
            int o = f == 0 ? 0 : f == 1 ? 3 : 6 + 6 * (f - 2);
            Vector3 u = corners[o], v = corners[o + 1], w = corners[o + 2];
            var nn = Vector3.Normalize(Vector3.Cross(v - u, w - u));
            planes[p++] = new Vector4(nn, Vector3.Dot(nn, u));
        }
        {
            Vector3 u = corners[18], v = corners[19], w = corners[20];
            var nn = Vector3.Normalize(Vector3.Cross(v - u, w - u));
            planes[p++] = new Vector4(nn, Vector3.Dot(nn, u));
        }
    }

    /// <summary>Whether a point is inside the ground (under the surface, over the floor) by more than
    /// <paramref name="slack"/>, and in which triangle's prism.</summary>
    public bool Inside(Vector3 p, float slack, out int k)
    {
        k = -1;
        if (!CellAt(p.X, p.Z, out int i, out int j, out float fx, out float fz)) return false;
        if (p.Y <= FloorY + slack) return false;
        k = TriangleOf(i, j, HalfOf(fx, fz));
        var tr = Triangle(k);
        // Under the triangle's plane by more than the slack, measured square to it.
        var n = Vector3.Normalize(tr.Normal);
        if (Vector3.Dot(n, p - tr.V0) > -slack) { k = -1; return false; }
        return true;
    }

    /// <summary>Every triangle whose prism's bounds can overlap [min, max], both halves of each cell.</summary>
    public void Overlapping(Vector3 min, Vector3 max, List<int> into)
    {
        if (max.Y < FloorY || min.Y > MaxY) return;
        if (max.X < 0f || max.Z < 0f || min.X > Size || min.Z > Size) return;
        int c = CellsPerSide;
        int i0 = Math.Clamp((int)MathF.Floor(min.X / Spacing), 0, c - 1), i1 = Math.Clamp((int)MathF.Floor(max.X / Spacing), 0, c - 1);
        int j0 = Math.Clamp((int)MathF.Floor(min.Z / Spacing), 0, c - 1), j1 = Math.Clamp((int)MathF.Floor(max.Z / Spacing), 0, c - 1);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                if (_cellMax[j * c + i] < min.Y) continue;
                into.Add(TriangleOf(i, j, 0));
                into.Add(TriangleOf(i, j, 1));
            }
    }

    // ═══ Rays ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The cells under a ray from <paramref name="o"/> along <paramref name="d"/> (the tile's frame) between
    /// <paramref name="tMin"/> and <paramref name="tMax"/>, nearest first: only those whose corners the
    /// ray's height across the cell can reach.
    /// </summary>
    public CellWalk Walk(Vector3 o, Vector3 d, float tMin, float tMax) => new(this, o, d, tMin, tMax);

    /// <summary>A walk over the cells under a ray (<see cref="Walk"/>): Amanatides and Woo's grid walk.</summary>
    public struct CellWalk
    {
        private readonly Heightfield _f;
        private readonly Vector3 _o, _d;
        private readonly float _t1, _dI, _dJ;
        private readonly int _stepI, _stepJ;
        private float _nextI, _nextJ, _enter;
        private int _i, _j, _left;
        private bool _done;

        internal CellWalk(Heightfield f, Vector3 o, Vector3 d, float tMin, float tMax)
        {
            _f = f; _o = o; _d = d;
            _t1 = tMax; _dI = _dJ = _nextI = _nextJ = _enter = 0f; _stepI = _stepJ = 1; _i = _j = 0; _left = 0;
            _done = true;
            if (!(tMax >= tMin)) return;
            // Clipped to the tile's box: x and z within it, y from the floor to the highest post.
            float t0 = tMin, t1 = tMax;
            if (!Clip(o.X, d.X, 0f, f.Size, ref t0, ref t1)) return;
            if (!Clip(o.Z, d.Z, 0f, f.Size, ref t0, ref t1)) return;
            if (!Clip(o.Y, d.Y, f.FloorY, f.MaxY, ref t0, ref t1)) return;
            int c = f.CellsPerSide;
            var start = o + d * t0;
            _i = Math.Clamp((int)MathF.Floor(start.X / f.Spacing), 0, c - 1);
            _j = Math.Clamp((int)MathF.Floor(start.Z / f.Spacing), 0, c - 1);
            _stepI = d.X > 0f ? 1 : -1; _stepJ = d.Z > 0f ? 1 : -1;
            _nextI = MathF.Abs(d.X) < 1e-12f ? float.MaxValue : ((d.X > 0f ? (_i + 1) * f.Spacing : _i * f.Spacing) - o.X) / d.X;
            _nextJ = MathF.Abs(d.Z) < 1e-12f ? float.MaxValue : ((d.Z > 0f ? (_j + 1) * f.Spacing : _j * f.Spacing) - o.Z) / d.Z;
            _dI = MathF.Abs(d.X) < 1e-12f ? float.MaxValue : f.Spacing / MathF.Abs(d.X);
            _dJ = MathF.Abs(d.Z) < 1e-12f ? float.MaxValue : f.Spacing / MathF.Abs(d.Z);
            _enter = t0; _t1 = t1;
            _left = 4 * c + 8;
            _done = false;
        }

        /// <summary>The next cell the ray can meet the ground in, and the distance at which the ray leaves it.</summary>
        public bool Next(out int i, out int j, out float leave)
        {
            const float Eps = 1e-3f;
            int c = _f.CellsPerSide;
            while (!_done && _left-- > 0)
            {
                i = _i; j = _j;
                leave = MathF.Min(MathF.Min(_nextI, _nextJ), _t1);
                float y0 = _o.Y + _d.Y * _enter, y1 = _o.Y + _d.Y * leave;
                int idx = j * c + i;
                bool meets = MathF.Max(y0, y1) >= _f._cellMin[idx] - Eps && MathF.Min(y0, y1) <= _f._cellMax[idx] + Eps;
                if (leave >= _t1) _done = true;
                else
                {
                    if (_nextI < _nextJ) { _i += _stepI; _enter = _nextI; _nextI += _dI; }
                    else { _j += _stepJ; _enter = _nextJ; _nextJ += _dJ; }
                    if (_i < 0 || _j < 0 || _i >= c || _j >= c) _done = true;
                }
                if (meets) return true;
            }
            i = j = 0; leave = 0f;
            return false;
        }
    }

    private static bool Clip(float o, float d, float lo, float hi, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-12f) return o >= lo - 1e-4f && o <= hi + 1e-4f;
        float a = (lo - o) / d, b = (hi - o) / d;
        if (a > b) (a, b) = (b, a);
        if (a > t0) t0 = a;
        if (b < t1) t1 = b;
        return t0 <= t1 + 1e-5f;
    }

    /// <summary>FNV-1a over the exact bits it is made of.</summary>
    public static ulong ContentHash(int posts, float spacing, float[] heights, byte[] cells, string[] materials, bool skirted = false)
    {
        ulong h = 14695981039346656037UL;
        void Mix(uint v) { h ^= v; h *= 1099511628211UL; h ^= h >> 29; }
        Mix((uint)posts); Mix(BitConverter.SingleToUInt32Bits(spacing));
        foreach (float f in heights) Mix(BitConverter.SingleToUInt32Bits(f));
        foreach (byte b in cells) Mix(b);
        foreach (var m in materials) { Mix((uint)m.Length); foreach (char ch in m) Mix(ch); }
        if (skirted) Mix(0x5c1u);
        return h;
    }
}
