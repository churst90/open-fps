using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenFPS.Common;
using PV = OpenFPS.Client.Core.AudioEngine.SteamAudio.Phonon.IPLVector3;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Throwaway measurements for docs/GEOMETRY.md: what the world costs as triangles.
///
///   --geometry [map=magnolia_tx] [terrain=5] [sa=1]
///
/// Loads a real-place map's solid boxes as the Steam Audio scene takes them (12 triangles a box),
/// optionally adds a terrain heightfield from the place's elevation.json at the given spacing, then:
///   1. builds a binned-SAH BVH over the triangles (flattened arrays) and times it;
///   2. times closest-hit rays, any-hit rays, downward ground probes and capsule overlap queries;
///   3. times today's Enclosure.Look against the same rays through the BVH;
///   4. builds Steam Audio scenes (default and Embree, one static mesh and one instanced sub-scene per
///      250 m tile) and times the build, an incremental tile swap, a moving body and a reflection trace.
/// Engine code is not touched; everything here is local to the spike.
/// </summary>
public static class GeometrySpike
{
    // ───────────────────────────── the triangle world ─────────────────────────────

    private sealed class Tris
    {
        public readonly List<Vector3> V = new();
        public readonly List<(int A, int B, int C)> T = new();
        public readonly List<int> Mat = new();
        public readonly List<string> MatNames = new();
        private readonly Dictionary<string, int> _mi = new(StringComparer.OrdinalIgnoreCase);
        public int MatIndex(string n) { if (!_mi.TryGetValue(n, out int i)) { i = MatNames.Count; MatNames.Add(n); _mi[n] = i; } return i; }
        public int Count => T.Count;
    }

    private static readonly Vector3[] Corner =
    {
        new(-1,-1,-1), new(1,-1,-1), new(1,1,-1), new(-1,1,-1),
        new(-1,-1, 1), new(1,-1, 1), new(1,1, 1), new(-1,1, 1),
    };
    private static readonly int[] Face =
    {
        0,1,2, 0,2,3, 4,6,5, 4,7,6, 0,3,7, 0,7,4, 1,5,6, 1,6,2, 0,4,5, 0,5,1, 3,2,6, 3,6,7,
    };

    private static void AddBox(Tris w, Vector3 c, Vector3 size, Quaternion r, int mat)
    {
        int b = w.V.Count;
        var h = size * 0.5f;
        for (int i = 0; i < 8; i++) w.V.Add(c + Vector3.Transform(Corner[i] * h, r));
        for (int f = 0; f < Face.Length; f += 3) { w.T.Add((b + Face[f], b + Face[f + 1], b + Face[f + 2])); w.Mat.Add(mat); }
    }

    private static Vector3 V(JsonElement v) => new(
        v.TryGetProperty("X", out var x) ? x.GetSingle() : 0f,
        v.TryGetProperty("Y", out var y) ? y.GetSingle() : 0f,
        v.TryGetProperty("Z", out var z) ? z.GetSingle() : 0f);

    /// <summary>The map's solid boxes, as SteamAudioScene.BoxesFromWorld takes them (solid, no emitter).</summary>
    private static (Tris W, List<Enclosure.Solid> Solids, Vector3 Spawn, Vector3 Min, Vector3 Size, double Lat0, double Lon0)
        LoadBoxes(string mapPath, string prefabDir)
    {
        var prefabs = new Dictionary<string, (Vector3 Size, string Material)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(prefabDir, "*.json"))
        {
            if (Path.GetFileName(file) == "prefab-schema.json") continue;
            using var d = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = d.RootElement;
            if (!r.TryGetProperty("Id", out var idj) || !r.TryGetProperty("ColliderSize", out var cs)) continue;
            if (r.TryGetProperty("IsSolid", out var sj) && sj.ValueKind == JsonValueKind.False) continue;
            if (r.TryGetProperty("SoundId", out var snd) && snd.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(snd.GetString())) continue;
            prefabs[idj.GetString()!] = (V(cs), r.TryGetProperty("Material", out var mj) ? mj.GetString() ?? "Generic" : "Generic");
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(mapPath));
        var root = doc.RootElement;
        var w = new Tris();
        var solids = new List<Enclosure.Solid>();
        foreach (var e in root.GetProperty("Entities").EnumerateArray())
        {
            if (!prefabs.TryGetValue(e.GetProperty("PrefabId").GetString()!, out var p)) continue;
            var pos = V(e.GetProperty("Position"));
            var scale = e.TryGetProperty("Scale", out var sc) ? V(sc) : Vector3.One;
            var rot = Quaternion.Identity;
            if (e.TryGetProperty("Rotation", out var rj))
                rot = new Quaternion(rj.GetProperty("X").GetSingle(), rj.GetProperty("Y").GetSingle(), rj.GetProperty("Z").GetSingle(), rj.GetProperty("W").GetSingle());
            var size = p.Size * scale;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0) continue;
            AddBox(w, pos, size, rot, w.MatIndex(p.Material));
            solids.Add(new Enclosure.Solid(pos, size, rot, p.Material));
        }
        var spawn = V(root.GetProperty("SpawnPoint").GetProperty("Position"));
        var min = V(root.GetProperty("MinBound"));
        var sizeMap = V(root.GetProperty("Size"));
        double lat0 = 0, lon0 = 0;
        if (root.TryGetProperty("GeoOrigin", out var go)) { lat0 = go.GetProperty("Lat").GetDouble(); lon0 = go.GetProperty("Lon").GetDouble(); }
        return (w, solids, spawn, min, sizeMap, lat0, lon0);
    }

    /// <summary>A heightfield from elevation.json (bilinear between posts), relative to the origin's height.</summary>
    private static Func<float, float, float> LoadElevation(string path, double lat0, double lon0)
    {
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        var r = d.RootElement;
        double west = r.GetProperty("west").GetDouble(), north = r.GetProperty("north").GetDouble();
        double dlon = r.GetProperty("dlon").GetDouble(), dlat = r.GetProperty("dlat").GetDouble();
        var rows = r.GetProperty("rows").EnumerateArray().Select(row => row.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetSingle() : float.NaN).ToArray()).ToArray();
        int nr = rows.Length, nc = rows[0].Length;
        double kx = 111320.0 * Math.Cos(lat0 * Math.PI / 180.0), kz = 110574.0;
        float At(int i, int j) { i = Math.Clamp(i, 0, nr - 1); j = Math.Clamp(j, 0, nc - 1); float v = rows[i][j]; return float.IsNaN(v) ? 60f : v; }
        float Raw(float x, float z)
        {
            double lon = lon0 + x / kx, lat = lat0 + z / kz;
            double fj = (lon - west) / dlon, fi = (north - lat) / dlat;
            int i = (int)Math.Floor(fi), j = (int)Math.Floor(fj);
            float ti = (float)(fi - i), tj = (float)(fj - j);
            float a = At(i, j) * (1 - tj) + At(i, j + 1) * tj, b = At(i + 1, j) * (1 - tj) + At(i + 1, j + 1) * tj;
            return a * (1 - ti) + b * ti;
        }
        float h0 = Raw(0, 0);
        return (x, z) => Raw(x, z) - h0;
    }

    private static void AddTerrain(Tris w, Vector3 min, Vector3 size, float g, Func<float, float, float> h, int mat)
    {
        int nx = (int)MathF.Ceiling(size.X / g), nz = (int)MathF.Ceiling(size.Z / g);
        int b = w.V.Count;
        for (int iz = 0; iz <= nz; iz++)
            for (int ix = 0; ix <= nx; ix++)
            {
                float x = min.X + ix * g, z = min.Z + iz * g;
                w.V.Add(new Vector3(x, h(x, z), z));
            }
        for (int iz = 0; iz < nz; iz++)
            for (int ix = 0; ix < nx; ix++)
            {
                int a = b + iz * (nx + 1) + ix, c = a + 1, d = a + nx + 1, e = d + 1;
                w.T.Add((a, d, c)); w.Mat.Add(mat);
                w.T.Add((c, d, e)); w.Mat.Add(mat);
            }
    }

    // ───────────────────────────── the BVH ─────────────────────────────

    /// <summary>A binary BVH over triangles: binned SAH, flattened nodes (32 bytes), leaves of up to
    /// four triangles stored in traversal order as (v0, e1, e2, material).</summary>
    private sealed class Bvh
    {
        public struct Node { public Vector3 Min; public int LeftFirst; public Vector3 Max; public int Count; }
        public struct Tri { public Vector3 V0, E1, E2; public int Mat; }

        public Node[] Nodes = Array.Empty<Node>();
        public int NodeCount;
        public Tri[] T = Array.Empty<Tri>();

        public long Bytes => (long)NodeCount * 32 + (long)T.Length * 40;

        private const int Bins = 12, MaxLeaf = 4;

        public static Bvh Build(Tris w)
        {
            int n = w.Count;
            var bmin = new Vector3[n]; var bmax = new Vector3[n]; var cen = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var (a, b, c) = w.T[i];
                Vector3 p = w.V[a], q = w.V[b], r = w.V[c];
                bmin[i] = Vector3.Min(p, Vector3.Min(q, r)); bmax[i] = Vector3.Max(p, Vector3.Max(q, r));
                cen[i] = (bmin[i] + bmax[i]) * 0.5f;
            }
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            var bvh = new Bvh { Nodes = new Node[Math.Max(1, 2 * n)] };
            bvh.NodeCount = 1;
            bvh.Nodes[0] = new Node { LeftFirst = 0, Count = n };
            Bounds(ref bvh.Nodes[0], idx, bmin, bmax);
            var stack = new Stack<int>();
            stack.Push(0);
            Span<int> binCount = stackalloc int[Bins];
            var binMin = new Vector3[Bins]; var binMax = new Vector3[Bins];
            var leftArea = new float[Bins - 1]; var leftCount = new int[Bins - 1];
            while (stack.Count > 0)
            {
                int ni = stack.Pop();
                ref Node node = ref bvh.Nodes[ni];
                int first = node.LeftFirst, count = node.Count;
                if (count <= MaxLeaf) continue;
                // centroid bounds
                Vector3 cmin = new(float.MaxValue), cmax = new(float.MinValue);
                for (int i = first; i < first + count; i++) { cmin = Vector3.Min(cmin, cen[idx[i]]); cmax = Vector3.Max(cmax, cen[idx[i]]); }
                float bestCost = float.MaxValue; int bestAxis = -1, bestSplit = -1;
                for (int axis = 0; axis < 3; axis++)
                {
                    float lo = Get(cmin, axis), hi = Get(cmax, axis);
                    if (hi - lo < 1e-6f) continue;
                    float scale = Bins / (hi - lo);
                    binCount.Clear();
                    for (int k = 0; k < Bins; k++) { binMin[k] = new Vector3(float.MaxValue); binMax[k] = new Vector3(float.MinValue); }
                    for (int i = first; i < first + count; i++)
                    {
                        int t = idx[i];
                        int k = Math.Min(Bins - 1, (int)((Get(cen[t], axis) - lo) * scale));
                        binCount[k]++; binMin[k] = Vector3.Min(binMin[k], bmin[t]); binMax[k] = Vector3.Max(binMax[k], bmax[t]);
                    }
                    Vector3 lmin = new(float.MaxValue), lmax = new(float.MinValue); int lc = 0;
                    for (int k = 0; k < Bins - 1; k++)
                    {
                        lc += binCount[k]; lmin = Vector3.Min(lmin, binMin[k]); lmax = Vector3.Max(lmax, binMax[k]);
                        leftCount[k] = lc; leftArea[k] = lc > 0 ? Area(lmin, lmax) : 0f;
                    }
                    Vector3 rmin = new(float.MaxValue), rmax = new(float.MinValue); int rc = 0;
                    for (int k = Bins - 1; k > 0; k--)
                    {
                        rc += binCount[k]; rmin = Vector3.Min(rmin, binMin[k]); rmax = Vector3.Max(rmax, binMax[k]);
                        float cost = leftArea[k - 1] * leftCount[k - 1] + (rc > 0 ? Area(rmin, rmax) * rc : 0f);
                        if (leftCount[k - 1] > 0 && rc > 0 && cost < bestCost) { bestCost = cost; bestAxis = axis; bestSplit = k; }
                    }
                }
                float leafCost = Area(node.Min, node.Max) * count;
                if (bestAxis < 0 || (bestCost >= leafCost && count <= 16))
                {
                    if (bestAxis < 0 && count > MaxLeaf) { /* all centroids equal: median split */ bestAxis = 0; }
                    else continue;
                }
                int mid;
                if (bestSplit < 0)
                    mid = first + count / 2;
                else
                {
                    float lo = Get(cmin, bestAxis), scale = Bins / (Get(cmax, bestAxis) - lo);
                    int i = first, j = first + count - 1;
                    while (i <= j)
                    {
                        int k = Math.Min(Bins - 1, (int)((Get(cen[idx[i]], bestAxis) - lo) * scale));
                        if (k < bestSplit) i++; else { (idx[i], idx[j]) = (idx[j], idx[i]); j--; }
                    }
                    mid = i;
                    if (mid == first || mid == first + count) mid = first + count / 2;
                }
                int l = bvh.NodeCount++, r = bvh.NodeCount++;
                bvh.Nodes[l] = new Node { LeftFirst = first, Count = mid - first };
                bvh.Nodes[r] = new Node { LeftFirst = mid, Count = first + count - mid };
                Bounds(ref bvh.Nodes[l], idx, bmin, bmax);
                Bounds(ref bvh.Nodes[r], idx, bmin, bmax);
                node = ref bvh.Nodes[ni];
                node.LeftFirst = l; node.Count = 0;
                stack.Push(r); stack.Push(l);
            }
            bvh.T = new Tri[n];
            for (int i = 0; i < n; i++)
            {
                var (a, b, c) = w.T[idx[i]];
                bvh.T[i] = new Tri { V0 = w.V[a], E1 = w.V[b] - w.V[a], E2 = w.V[c] - w.V[a], Mat = w.Mat[idx[i]] };
            }
            return bvh;
        }

        private static float Get(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
        private static float Area(Vector3 mn, Vector3 mx) { var e = mx - mn; return e.X * e.Y + e.Y * e.Z + e.Z * e.X; }
        private static void Bounds(ref Node n, int[] idx, Vector3[] bmin, Vector3[] bmax)
        {
            Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
            for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++) { mn = Vector3.Min(mn, bmin[idx[i]]); mx = Vector3.Max(mx, bmax[idx[i]]); }
            n.Min = mn; n.Max = mx;
        }

        private static float Slab(in Node n, Vector3 o, Vector3 inv, float tmax)
        {
            var t1 = (n.Min - o) * inv; var t2 = (n.Max - o) * inv;
            var lo = Vector3.Min(t1, t2); var hi = Vector3.Max(t1, t2);
            float tn = MathF.Max(MathF.Max(lo.X, lo.Y), MathF.Max(lo.Z, 0f));
            float tf = MathF.Min(MathF.Min(hi.X, hi.Y), MathF.Min(hi.Z, tmax));
            return tn <= tf ? tn : float.MaxValue;
        }

        private static bool HitTri(in Tri t, Vector3 o, Vector3 d, float tmax, out float dist)
        {
            dist = 0;
            var p = Vector3.Cross(d, t.E2);
            float det = Vector3.Dot(t.E1, p);
            if (MathF.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            var s = o - t.V0;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, t.E1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            dist = Vector3.Dot(t.E2, q) * inv;
            return dist > 1e-4f && dist < tmax;
        }

        public bool Closest(Vector3 o, Vector3 d, float tmax, out float dist, out int tri)
        {
            dist = tmax; tri = -1;
            var inv = new Vector3(1f / (MathF.Abs(d.X) < 1e-12f ? 1e-12f : d.X), 1f / (MathF.Abs(d.Y) < 1e-12f ? 1e-12f : d.Y), 1f / (MathF.Abs(d.Z) < 1e-12f ? 1e-12f : d.Z));
            Span<int> stack = stackalloc int[64];
            int sp = 0;
            if (Slab(Nodes[0], o, inv, dist) == float.MaxValue) return false;
            stack[sp++] = 0;
            while (sp > 0)
            {
                ref readonly Node n = ref Nodes[stack[--sp]];
                if (n.Count > 0)
                {
                    for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                        if (HitTri(T[i], o, d, dist, out float t)) { dist = t; tri = i; }
                    continue;
                }
                int a = n.LeftFirst, b = a + 1;
                float ta = Slab(Nodes[a], o, inv, dist), tb = Slab(Nodes[b], o, inv, dist);
                if (ta > tb) { (a, b) = (b, a); (ta, tb) = (tb, ta); }
                if (tb != float.MaxValue) stack[sp++] = b;
                if (ta != float.MaxValue) stack[sp++] = a;
            }
            return tri >= 0;
        }

        public bool Any(Vector3 o, Vector3 d, float tmax)
        {
            var inv = new Vector3(1f / (MathF.Abs(d.X) < 1e-12f ? 1e-12f : d.X), 1f / (MathF.Abs(d.Y) < 1e-12f ? 1e-12f : d.Y), 1f / (MathF.Abs(d.Z) < 1e-12f ? 1e-12f : d.Z));
            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                ref readonly Node n = ref Nodes[stack[--sp]];
                if (Slab(n, o, inv, tmax) == float.MaxValue) continue;
                if (n.Count > 0)
                {
                    for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                        if (HitTri(T[i], o, d, tmax, out _)) return true;
                    continue;
                }
                stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
            }
            return false;
        }

        /// <summary>Triangles within a capsule (segment a-b, radius r): the AABB walk, then the exact
        /// point-triangle distance at the capsule's ends and middle. Returns how many touch.</summary>
        public int Capsule(Vector3 a, Vector3 b, float r, out int candidates)
        {
            var mn = Vector3.Min(a, b) - new Vector3(r); var mx = Vector3.Max(a, b) + new Vector3(r);
            Span<int> stack = stackalloc int[64];
            int sp = 0, touch = 0; candidates = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                ref readonly Node n = ref Nodes[stack[--sp]];
                if (n.Max.X < mn.X || n.Min.X > mx.X || n.Max.Y < mn.Y || n.Min.Y > mx.Y || n.Max.Z < mn.Z || n.Min.Z > mx.Z) continue;
                if (n.Count > 0)
                {
                    for (int i = n.LeftFirst; i < n.LeftFirst + n.Count; i++)
                    {
                        candidates++;
                        ref readonly Tri t = ref T[i];
                        float best = float.MaxValue;
                        for (int s = 0; s <= 2; s++)
                        {
                            var p = Vector3.Lerp(a, b, s * 0.5f);
                            best = MathF.Min(best, Vector3.DistanceSquared(p, ClosestOnTri(p, t.V0, t.V0 + t.E1, t.V0 + t.E2)));
                        }
                        if (best <= r * r) touch++;
                    }
                    continue;
                }
                stack[sp++] = n.LeftFirst + 1; stack[sp++] = n.LeftFirst;
            }
            return touch;
        }

        // Ericson, Real-Time Collision Detection, 5.1.5
        private static Vector3 ClosestOnTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + d1 / (d1 - d3) * ab;
            Vector3 cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + d2 / (d2 - d6) * ac;
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (d4 - d3) / ((d4 - d3) + (d5 - d6)) * (c - b);
            float den = 1f / (va + vb + vc);
            return a + ab * (vb * den) + ac * (vc * den);
        }
    }

    // ───────────────────────────── Steam Audio extras the engine does not bind ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct IPLMatrix4x4 { public fixed float e[16]; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IPLInstancedMeshSettings { public IntPtr subScene; public IPLMatrix4x4 transform; }

    [DllImport("phonon", CallingConvention = CallingConvention.Cdecl)] private static extern int iplEmbreeDeviceCreate(IntPtr context, ref byte settings, out IntPtr device);
    [DllImport("phonon", CallingConvention = CallingConvention.Cdecl)] private static extern int iplInstancedMeshCreate(IntPtr scene, ref IPLInstancedMeshSettings settings, out IntPtr mesh);
    [DllImport("phonon", CallingConvention = CallingConvention.Cdecl)] private static extern void iplStaticMeshRemove(IntPtr mesh, IntPtr scene);
    [DllImport("phonon", CallingConvention = CallingConvention.Cdecl)] private static extern void iplInstancedMeshAdd(IntPtr mesh, IntPtr scene);
    [DllImport("phonon", CallingConvention = CallingConvention.Cdecl)] private static extern void iplInstancedMeshRemove(IntPtr mesh, IntPtr scene);
    [DllImport("phonon", CallingConvention = CallingConvention.Cdecl)] private static extern void iplInstancedMeshUpdateTransform(IntPtr mesh, IntPtr scene, IPLMatrix4x4 transform);

    private static unsafe IPLMatrix4x4 Translate(Vector3 t)
    {
        var m = new IPLMatrix4x4();
        m.e[0] = 1; m.e[5] = 1; m.e[10] = 1; m.e[15] = 1;
        m.e[3] = t.X; m.e[7] = t.Y; m.e[11] = -t.Z;   // row-major, translation in the last column; z mirrored as Phonon.World
        return m;
    }

    private static IntPtr NewScene(IntPtr ctx, int type, IntPtr embree)
    {
        var s = new Phonon.IPLSceneSettings { type = type, embreeDevice = embree };
        return Phonon.iplSceneCreate(ctx, ref s, out IntPtr scene) == Phonon.IPL_STATUS_SUCCESS ? scene : IntPtr.Zero;
    }

    private static IntPtr AddMesh(IntPtr scene, Tris w, IReadOnlyList<int>? subset)
    {
        var verts = new List<PV>(); var tris = new List<Phonon.IPLTriangle>(); var mi = new List<int>();
        var remap = new Dictionary<int, int>();
        IEnumerable<int> which = subset ?? Enumerable.Range(0, w.Count);
        foreach (int t in which)
        {
            var (a, b, c) = w.T[t];
            int Map(int v) { if (!remap.TryGetValue(v, out int k)) { k = verts.Count; verts.Add(Phonon.World(w.V[v])); remap[v] = k; } return k; }
            tris.Add(new Phonon.IPLTriangle { i0 = Map(a), i1 = Map(b), i2 = Map(c) });
            mi.Add(w.Mat[t]);
        }
        var mats = w.MatNames.Select(n =>
        {
            var p = AcousticRegistry.GetProperties(n);
            return new Phonon.IPLMaterial { absLow = p.AbsorptionLow, absMid = p.AbsorptionMid, absHigh = p.AbsorptionHigh, scattering = p.Scattering, transLow = 0.01f, transMid = 0.01f, transHigh = 0.01f };
        }).ToArray();
        var vA = verts.ToArray(); var tA = tris.ToArray(); var mA = mi.ToArray();
        var hV = GCHandle.Alloc(vA, GCHandleType.Pinned); var hT = GCHandle.Alloc(tA, GCHandleType.Pinned);
        var hI = GCHandle.Alloc(mA, GCHandleType.Pinned); var hM = GCHandle.Alloc(mats, GCHandleType.Pinned);
        try
        {
            var ms = new Phonon.IPLStaticMeshSettings
            {
                numVertices = vA.Length, numTriangles = tA.Length, numMaterials = mats.Length,
                vertices = hV.AddrOfPinnedObject(), triangles = hT.AddrOfPinnedObject(), materialIndices = hI.AddrOfPinnedObject(), materials = hM.AddrOfPinnedObject(),
            };
            if (Phonon.iplStaticMeshCreate(scene, ref ms, out IntPtr mesh) != Phonon.IPL_STATUS_SUCCESS) return IntPtr.Zero;
            Phonon.iplStaticMeshAdd(mesh, scene);
            return mesh;
        }
        finally { hV.Free(); hT.Free(); hI.Free(); hM.Free(); }
    }

    private static Phonon.IPLCoordinateSpace3 Coord(Vector3 origin) => new()
    {
        right = new PV { x = 1, y = 0, z = 0 }, up = new PV { x = 0, y = 1, z = 0 }, ahead = new PV { x = 0, y = 0, z = -1 },
        origin = Phonon.World(origin),
    };

    /// <summary>One reflection trace (LateField's numbers: 4096 rays, 48 bounces, one thread) and one
    /// direct pass with occlusion and transmission for 32 sources, against a scene. Milliseconds.</summary>
    private static (double Refl, double Direct) TraceCost(IntPtr ctx, IntPtr scene, int type, Vector3 listener, int runs = 3)
    {
        const int sources = 32;
        var s = new Phonon.IPLSimulationSettings
        {
            flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS | Phonon.IPL_SIMULATIONFLAGS_DIRECT, sceneType = type,
            reflectionType = Phonon.IPL_REFLECTIONEFFECTTYPE_CONVOLUTION,
            maxNumOcclusionSamples = 16, maxNumRays = 4096, numDiffuseSamples = 32, maxDuration = 1.0f, maxOrder = 1,
            maxNumSources = sources, numThreads = 1, rayBatchSize = 16, numVisSamples = 4, samplingRate = 48000, frameSize = 1024,
        };
        if (Phonon.iplSimulatorCreate(ctx, ref s, out IntPtr sim) != Phonon.IPL_STATUS_SUCCESS) return (-1, -1);
        Phonon.iplSimulatorSetScene(sim, scene);
        var src = new IntPtr[sources];
        var rng = new Random(7);
        for (int i = 0; i < sources; i++)
        {
            var ss = new Phonon.IPLSourceSettings { flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS | Phonon.IPL_SIMULATIONFLAGS_DIRECT };
            Phonon.iplSourceCreate(sim, ref ss, out src[i]);
            Phonon.iplSourceAdd(src[i], sim);
        }
        Phonon.iplSimulatorCommit(sim);
        for (int i = 0; i < sources; i++)
        {
            var at = listener + new Vector3((float)(rng.NextDouble() * 160 - 80), 1.0f, (float)(rng.NextDouble() * 160 - 80));
            var inputs = new Phonon.IPLSimulationInputs
            {
                flags = Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS | Phonon.IPL_SIMULATIONFLAGS_DIRECT,
                directFlags = Phonon.IPL_DIRECTSIMULATIONFLAGS_OCCLUSION | Phonon.IPL_DIRECTSIMULATIONFLAGS_TRANSMISSION,
                source = Coord(i == 0 ? listener : at), occlusionType = Phonon.IPL_OCCLUSIONTYPE_RAYCAST, numOcclusionSamples = 16,
                reverbScale0 = 1f, reverbScale1 = 1f, reverbScale2 = 1f, hybridReverbTransitionTime = 1f, hybridReverbOverlapPercent = 0.25f,
                numTransmissionRays = 4,
            };
            Phonon.iplSourceSetInputs(src[i], Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS | Phonon.IPL_SIMULATIONFLAGS_DIRECT, ref inputs);
        }
        var shared = new Phonon.IPLSimulationSharedInputs { listener = Coord(listener), numRays = 4096, numBounces = 48, duration = 1.0f, order = 1, irradianceMinDistance = 1.0f };
        Phonon.iplSimulatorSetSharedInputs(sim, Phonon.IPL_SIMULATIONFLAGS_REFLECTIONS | Phonon.IPL_SIMULATIONFLAGS_DIRECT, ref shared);
        // Only the reference source traces reflections (as LateField's slot 0); the others are direct only.
        double refl = double.MaxValue, direct = double.MaxValue;
        for (int r = 0; r < runs; r++)
        {
            var sw = Stopwatch.StartNew();
            Phonon.iplSimulatorRunDirect(sim);
            direct = Math.Min(direct, sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            Phonon.iplSimulatorRunReflections(sim);
            refl = Math.Min(refl, sw.Elapsed.TotalMilliseconds);
        }
        for (int i = 0; i < sources; i++) Phonon.iplSourceRelease(ref src[i]);
        Phonon.iplSimulatorRelease(ref sim);
        return (refl, direct);
    }

    private static int BvhLook(Bvh bvh, Vector3 p)
    {
        const int R = 192;
        int rays = 0, misses = 0;
        Span<float> dist = stackalloc float[R];
        Span<int> tri = stackalloc int[R];
        for (int k = 0; k < R; k++)
        {
            rays++;
            if (!bvh.Closest(p, Enclosure.SphereDirection(k, R), 120f, out dist[k], out tri[k])) { tri[k] = -1; misses++; }
        }
        if (misses / (float)R < 0.35f)
            for (int k = 0; k < R; k++)
            {
                if (tri[k] < 0 || dist[k] < 3f) continue;
                var mid = p + Enclosure.SphereDirection(k, R) * dist[k] * 0.5f;
                for (int j = 0; j < 14; j++) { rays++; bvh.Any(mid, Enclosure.SphereDirection(j, 14), 120f); }
            }
        for (int k = 0; k < R; k++)
        {
            if (tri[k] < 0) continue;
            var d = Enclosure.SphereDirection(k, R);
            var n = Vector3.Normalize(Vector3.Cross(bvh.T[tri[k]].E1, bvh.T[tri[k]].E2));
            if (Vector3.Dot(n, d) > 0) n = -n;
            var on = Vector3.Reflect(d, n);
            rays++;
            bvh.Closest(p + d * dist[k] + on * 0.01f, on, 120f, out _, out _);
        }
        return rays;
    }

    // ───────────────────────────── the run ─────────────────────────────

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string mapId = args.FirstOrDefault(a => a.StartsWith("map="))?[4..] ?? "magnolia_tx";
        float terrain = float.Parse(args.FirstOrDefault(a => a.StartsWith("terrain="))?[8..] ?? "0", CultureInfo.InvariantCulture);
        bool sa = (args.FirstOrDefault(a => a.StartsWith("sa="))?[3..] ?? "1") != "0";
        string mapPath = OpenFPS.AudioLab.LabPaths.Server("maps", "places", mapId + ".json");
        if (!File.Exists(mapPath)) mapPath = OpenFPS.AudioLab.LabPaths.Server("maps", mapId + ".json");
        var sw = Stopwatch.StartNew();
        var (w, solids, spawn, min, size, lat0, lon0) = LoadBoxes(mapPath, OpenFPS.AudioLab.LabPaths.Server("prefabs"));
        int boxTris = w.Count;
        Console.WriteLine($"{mapId}: {solids.Count} solid boxes, {boxTris:N0} triangles ({sw.ElapsedMilliseconds} ms to load)");
        if (terrain > 0)
        {
            var elev = OpenFPS.AudioLab.LabPaths.InRepo("tools", "places", mapId, "elevation.json");
            var h = File.Exists(elev) ? LoadElevation(elev, lat0, lon0) : (_, _) => 0f;
            AddTerrain(w, min, size, terrain, h, w.MatIndex("Dirt"));
            Console.WriteLine($"  + terrain at {terrain} m: {w.Count - boxTris:N0} triangles, {w.Count:N0} in all");
        }

        // 1. BVH build
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        Bvh bvh = null!;
        double build = double.MaxValue;
        for (int r = 0; r < 3; r++) { sw.Restart(); bvh = Bvh.Build(w); build = Math.Min(build, sw.Elapsed.TotalMilliseconds); }
        Console.WriteLine($"  BVH: {bvh.NodeCount:N0} nodes, {bvh.Bytes / 1e6:F1} MB (nodes + triangles), build {build:F0} ms on one thread");

        // 2. queries, from 400 listener points within 300 m of the spawn at ear height
        var rng = new Random(1);
        var pts = Enumerable.Range(0, 400).Select(_ => spawn + new Vector3((float)(rng.NextDouble() * 600 - 300), 1.6f, (float)(rng.NextDouble() * 600 - 300))).ToArray();
        // ground height under each point: start well above
        for (int i = 0; i < pts.Length; i++)
            if (bvh.Closest(pts[i] + new Vector3(0, 60, 0), -Vector3.UnitY, 200f, out float gd, out _)) pts[i].Y = pts[i].Y + 60 - gd;
        int rays = 0, hits = 0;
        sw.Restart();
        foreach (var p in pts)
            for (int k = 0; k < 192; k++) { rays++; if (bvh.Closest(p, Enclosure.SphereDirection(k, 192), 60f, out _, out _)) hits++; }
        double tShort = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  closest hit, 192-ray spheres to 60 m: {rays / tShort * 1000 / 1e6:F2} M rays/s one thread ({hits * 100 / rays} % hit), {tShort / pts.Length:F3} ms a sphere");
        rays = 0; sw.Restart();
        foreach (var p in pts)
            for (int k = 0; k < 16; k++) { var d = Enclosure.SphereDirection(k * 12 + 5, 192); d.Y *= 0.1f; rays++; bvh.Closest(p, Vector3.Normalize(d), 600f, out _, out _); }
        double tLong = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  closest hit, near-level sight rays to 600 m: {rays / tLong * 1000 / 1e6:F2} M rays/s");
        rays = 0; sw.Restart();
        for (int i = 0; i < pts.Length; i++)
            for (int j = 0; j < 16; j++) { rays++; bvh.Any(pts[i], Vector3.Normalize(pts[(i + j + 1) % pts.Length] - pts[i]), Vector3.Distance(pts[i], pts[(i + j + 1) % pts.Length])); }
        double tAny = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  any hit, point to point (occlusion), up to 850 m: {rays / tAny * 1000 / 1e6:F2} M rays/s");
        rays = 0; sw.Restart();
        for (int rep = 0; rep < 25; rep++)
            foreach (var p in pts) { rays++; bvh.Closest(p + new Vector3(0, 0.5f, 0), -Vector3.UnitY, 50f, out _, out _); }
        double tDown = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  ground height (one downward ray): {tDown / rays * 1000:F2} us each");
        int caps = 0, cand = 0, touch = 0; sw.Restart();
        for (int rep = 0; rep < 25; rep++)
            foreach (var p in pts) { caps++; touch += bvh.Capsule(p - new Vector3(0, 1.2f, 0), p + new Vector3(0, 0.1f, 0), 0.3f, out int c); cand += c; }
        double tCap = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  capsule overlap (r 0.3 m, 1.8 m tall): {tCap / caps * 1000:F2} us each, {cand / (double)caps:F1} candidate triangles, {touch / (double)caps:F1} touching");
        // all cores
        long par = 0; sw.Restart();
        Parallel.For(0, pts.Length * 20, ii => { int i = ii % pts.Length; int c = 0; for (int k = 0; k < 192; k++) if (bvh.Closest(pts[i], Enclosure.SphereDirection(k, 192), 60f, out _, out _)) c++; System.Threading.Interlocked.Add(ref par, 192); });
        double tPar = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  the same spheres on all {Environment.ProcessorCount} threads: {par / tPar * 1000 / 1e6:F1} M rays/s");

        // 3. today's Enclosure.Look against the same survey through the BVH: 192 rays, a bounce for each
        // hit, and (when the listener is not already outside) 14 openness rays halfway along each hit
        // of 3 m or more, uncached here where Enclosure caches them on a 2 x 0.5 x 2 m grid.
        if (terrain == 0)
        {
            var some = pts.Take(60).ToArray();
            Enclosure.Look(some[0], solids); // warm
            sw.Restart();
            foreach (var p in some) Enclosure.Look(p, solids);
            double tLook = sw.Elapsed.TotalMilliseconds / some.Length;
            long bvhRays = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                bvhRays = 0;
                sw.Restart();
                foreach (var p in some) bvhRays += BvhLook(bvh, p);
            }
            double tBvhLook = sw.Elapsed.TotalMilliseconds / some.Length;
            Console.WriteLine($"  Enclosure.Look today (boxes, Nearby filter, slab tests): {tLook:F2} ms a survey; the same survey through the BVH: {tBvhLook:F3} ms ({bvhRays / some.Length} rays a survey)");
        }

        Console.WriteLine($"  peak working set so far: {Process.GetCurrentProcess().PeakWorkingSet64 / 1e6:F0} MB");
        if (!sa) return 0;

        // 4. Steam Audio
        var cs = Phonon.DefaultContextSettings();
        if (Phonon.iplContextCreate(ref cs, out IntPtr ctx) != Phonon.IPL_STATUS_SUCCESS) { Console.WriteLine("  no Steam Audio context"); return 1; }
        byte reserved = 0;
        IntPtr embree = iplEmbreeDeviceCreate(ctx, ref reserved, out IntPtr ed) == Phonon.IPL_STATUS_SUCCESS ? ed : IntPtr.Zero;
        foreach (var (name, type, dev) in new[] { ("default", Phonon.IPL_SCENETYPE_DEFAULT, IntPtr.Zero), ("Embree", 1, embree) })
        {
            if (type == 1 && embree == IntPtr.Zero) { Console.WriteLine("  Embree: no device"); continue; }
            sw.Restart();
            var scene = NewScene(ctx, type, dev);
            var mesh = AddMesh(scene, w, null);
            double tMesh = sw.Elapsed.TotalMilliseconds;
            Phonon.iplSceneCommit(scene);
            double tAll = sw.Elapsed.TotalMilliseconds;
            var (refl, direct) = TraceCost(ctx, scene, type, spawn + new Vector3(0, 1.6f, 0));
            Console.WriteLine($"  Steam Audio {name}, one static mesh: create {tMesh:F0} ms + commit {tAll - tMesh:F0} ms; reflection trace (4096 rays x 48 bounces) {refl:F0} ms, direct for 32 sources {direct:F2} ms");
            Phonon.iplStaticMeshRelease(ref mesh);
            Phonon.iplSceneRelease(ref scene);
        }

        // per tile (WORLD_STREAMING stage 3): one static mesh per 250 m tile in one scene, or one
        // sub-scene per tile instanced into a top scene; default ray tracer and Embree.
        {
            const float T = 250f;
            var byTile = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < w.Count; t++)
            {
                var (a, b, c) = w.T[t];
                var cen = (w.V[a] + w.V[b] + w.V[c]) / 3f;
                var key = ((int)MathF.Floor(cen.X / T), (int)MathF.Floor(cen.Z / T));
                if (!byTile.TryGetValue(key, out var l)) byTile[key] = l = new List<int>();
                l.Add(t);
            }
            var lists = byTile.Values.ToList();
            foreach (var (name, type, dev) in new[] { ("default", Phonon.IPL_SCENETYPE_DEFAULT, IntPtr.Zero), ("Embree", 1, embree) })
            {
                if (type == 1 && embree == IntPtr.Zero) continue;
                // (a) a static mesh per tile, all in one scene
                {
                    var scene = NewScene(ctx, type, dev);
                    var per = new List<double>(); var meshes = new List<IntPtr>();
                    sw.Restart();
                    foreach (var list in lists) { var t0 = sw.Elapsed.TotalMilliseconds; meshes.Add(AddMesh(scene, w, list)); per.Add(sw.Elapsed.TotalMilliseconds - t0); }
                    double tMeshes = sw.Elapsed.TotalMilliseconds;
                    Phonon.iplSceneCommit(scene);
                    double tCommit = sw.Elapsed.TotalMilliseconds - tMeshes;
                    per.Sort();
                    // swap the biggest tile out and back
                    int big = Enumerable.Range(0, lists.Count).OrderByDescending(i => lists[i].Count).First();
                    sw.Restart();
                    iplStaticMeshRemove(meshes[big], scene); Phonon.iplSceneCommit(scene);
                    double tOut = sw.Elapsed.TotalMilliseconds; sw.Restart();
                    var again = AddMesh(scene, w, lists[big]); Phonon.iplSceneCommit(scene);
                    double tIn = sw.Elapsed.TotalMilliseconds;
                    var (refl, direct) = TraceCost(ctx, scene, type, spawn + new Vector3(0, 1.6f, 0));
                    Console.WriteLine($"  Steam Audio {name}, {lists.Count} static meshes (one a tile) in one scene: meshes {tMeshes:F0} ms (median {per[per.Count / 2]:F1}, max {per[^1]:F0}), commit {tCommit:F0} ms; " +
                                      $"biggest tile ({lists[big].Count:N0} tris) out {tOut:F0} ms, back in {tIn:F0} ms; trace {refl:F0} ms, direct {direct:F2} ms");
                    Phonon.iplSceneRelease(ref scene);
                }
                // (b) a sub-scene per tile, instanced into a top scene
                {
                    var top = NewScene(ctx, type, dev);
                    var per = new List<double>(); var inst = new List<IntPtr>();
                    sw.Restart();
                    foreach (var list in lists)
                    {
                        var t0 = sw.Elapsed.TotalMilliseconds;
                        var sub = NewScene(ctx, type, dev);
                        AddMesh(sub, w, list);
                        Phonon.iplSceneCommit(sub);
                        var st = new IPLInstancedMeshSettings { subScene = sub, transform = Translate(Vector3.Zero) };
                        iplInstancedMeshCreate(top, ref st, out IntPtr im);
                        iplInstancedMeshAdd(im, top);
                        per.Add(sw.Elapsed.TotalMilliseconds - t0);
                        inst.Add(im);
                    }
                    double tTiles = sw.Elapsed.TotalMilliseconds;
                    Phonon.iplSceneCommit(top);
                    double tTop = sw.Elapsed.TotalMilliseconds - tTiles;
                    per.Sort();
                    sw.Restart();
                    iplInstancedMeshRemove(inst[0], top); Phonon.iplSceneCommit(top);
                    iplInstancedMeshAdd(inst[0], top); Phonon.iplSceneCommit(top);
                    double tSwap = sw.Elapsed.TotalMilliseconds / 2;
                    var body = new Tris();
                    for (int i = 0; i < 84; i++) AddBox(body, new Vector3(i % 7 * 0.6f, 0.7f, i / 7 * 0.4f), new Vector3(0.5f, 0.3f, 0.3f), Quaternion.Identity, body.MatIndex("Metal"));
                    var bsub = NewScene(ctx, type, dev);
                    AddMesh(bsub, body, null); Phonon.iplSceneCommit(bsub);
                    var bst = new IPLInstancedMeshSettings { subScene = bsub, transform = Translate(spawn) };
                    iplInstancedMeshCreate(top, ref bst, out IntPtr bim); iplInstancedMeshAdd(bim, top); Phonon.iplSceneCommit(top);
                    sw.Restart();
                    for (int f = 0; f < 100; f++) { iplInstancedMeshUpdateTransform(bim, top, Translate(spawn + new Vector3(f * 0.3f, 0, 0))); Phonon.iplSceneCommit(top); }
                    double tMove = sw.Elapsed.TotalMilliseconds / 100;
                    var (refl, direct) = TraceCost(ctx, top, type, spawn + new Vector3(0, 1.6f, 0));
                    Console.WriteLine($"  Steam Audio {name}, {lists.Count} instanced tile sub-scenes: build median {per[per.Count / 2]:F1} ms, max {per[^1]:F0} ms, all {tTiles:F0} ms; top commit {tTop:F1} ms; " +
                                      $"tile swap {tSwap:F2} ms; moving {body.Count}-triangle body {tMove:F3} ms a frame; trace {refl:F0} ms, direct {direct:F2} ms");
                }
            }
        }
        Console.WriteLine($"  peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1e6:F0} MB");
        return 0;
    }
}
