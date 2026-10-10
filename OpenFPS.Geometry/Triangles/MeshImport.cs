using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace OpenFPS.Common.Geometry;

/// <summary>Triangles as a file gave them: positions, three indices and a surface a triangle, and the surfaces' names.</summary>
public sealed class ImportedTriangles
{
    public List<Vector3> Positions { get; } = new();
    /// <summary>a, b, c, surface: a triangle each four.</summary>
    public List<int> Triangles { get; } = new();
    public List<string> SurfaceNames { get; } = new();
    /// <summary>What the reader could not take (a strip, a compressed buffer, a point cloud) and left out.</summary>
    public List<string> Skipped { get; } = new();

    public int Surface(string name)
    {
        int i = SurfaceNames.IndexOf(name);
        if (i >= 0) return i;
        SurfaceNames.Add(name);
        return SurfaceNames.Count - 1;
    }
}

/// <summary>
/// Wavefront OBJ (docs/GEOMETRY.md 4.3): vertices, faces (a polygon cut into triangles), and each face on the
/// surface its <c>usemtl</c> names, or its group when the file names no materials. Normals and texture
/// coordinates are read past. A small parser of our own: the format is a few lines of text.
/// </summary>
public static class ObjReader
{
    public static ImportedTriangles Read(TextReader text)
    {
        var m = new ImportedTriangles();
        string? material = null, group = null;
        bool anyMaterial = false;
        var faces = new List<(int[] Corners, string? Material, string? Group)>();
        string? line;
        int lineNo = 0;
        while ((line = text.ReadLine()) != null)
        {
            lineNo++;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            switch (parts[0])
            {
                case "v":
                    if (parts.Length < 4) throw new InvalidDataException($"line {lineNo}: a vertex needs x, y and z");
                    m.Positions.Add(new Vector3(F(parts[1], lineNo), F(parts[2], lineNo), F(parts[3], lineNo)));
                    break;
                case "f":
                    if (parts.Length < 4) throw new InvalidDataException($"line {lineNo}: a face needs three corners");
                    var corners = new int[parts.Length - 1];
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var s = parts[i];
                        int slash = s.IndexOf('/');
                        int k = int.Parse(slash >= 0 ? s[..slash] : s, CultureInfo.InvariantCulture);
                        corners[i - 1] = k > 0 ? k - 1 : m.Positions.Count + k;
                        if (corners[i - 1] < 0 || corners[i - 1] >= m.Positions.Count)
                            throw new InvalidDataException($"line {lineNo}: corner {s} names no vertex");
                    }
                    faces.Add((corners, material, group));
                    break;
                case "usemtl": material = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : null; anyMaterial = true; break;
                case "g" or "o": group = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : null; break;
            }
        }
        foreach (var (corners, mat, grp) in faces)
        {
            int surface = m.Surface((anyMaterial ? mat : grp) ?? "body");
            MeshImporter.AddPolygon(m, corners, surface);
        }
        return m;

        static float F(string s, int line)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f
               : throw new InvalidDataException($"line {line}: {s} is not a number");
    }
}

/// <summary>
/// glTF 2.0 (docs/GEOMETRY.md 4.3), as .gltf with its buffers (files beside it or data: URIs) or as .glb: every
/// mesh of the default scene placed by its nodes' transforms, triangles only, each primitive on the surface its
/// material names. Normals, texture coordinates and textures are what a renderer needs and are left for one. A
/// small reader of our own (no dependency): sparse accessors, Draco and quantised positions are refused by name.
/// </summary>
public static class GltfReader
{
    public static ImportedTriangles ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        return Read(bytes, uri => File.ReadAllBytes(Path.Combine(dir, Uri.UnescapeDataString(uri))));
    }

    /// <summary>A .gltf's JSON or a whole .glb; <paramref name="resolve"/> reads a buffer that is a file.</summary>
    public static ImportedTriangles Read(byte[] bytes, Func<string, byte[]> resolve)
    {
        byte[]? glbBin = null;
        string json;
        if (bytes.Length >= 12 && bytes[0] == (byte)'g' && bytes[1] == (byte)'l' && bytes[2] == (byte)'T' && bytes[3] == (byte)'F')
        {
            int at = 12;
            json = "";
            while (at + 8 <= bytes.Length)
            {
                int len = BitConverter.ToInt32(bytes, at), type = BitConverter.ToInt32(bytes, at + 4);
                if (at + 8 + len > bytes.Length) throw new InvalidDataException("the .glb's chunks run past its end");
                if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(bytes, at + 8, len);
                else if (type == 0x004E4942) glbBin = bytes.AsSpan(at + 8, len).ToArray();
                at += 8 + len;
            }
        }
        else json = Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("extensionsRequired", out var req))
            foreach (var ext in req.EnumerateArray())
                throw new InvalidDataException($"it needs {ext.GetString()}, which the importer does not read");

        var buffers = new List<byte[]>();
        if (root.TryGetProperty("buffers", out var bufs))
            foreach (var b in bufs.EnumerateArray())
            {
                if (!b.TryGetProperty("uri", out var uri)) { buffers.Add(glbBin ?? throw new InvalidDataException("a buffer with no data")); continue; }
                var u = uri.GetString()!;
                if (u.StartsWith("data:", StringComparison.Ordinal))
                {
                    int comma = u.IndexOf(',');
                    buffers.Add(Convert.FromBase64String(u[(comma + 1)..]));
                }
                else buffers.Add(resolve(u));
            }
        var m = new ImportedTriangles();
        var materials = root.TryGetProperty("materials", out var mats) ? mats.EnumerateArray().ToList() : new List<JsonElement>();
        var meshes = root.TryGetProperty("meshes", out var ms) ? ms.EnumerateArray().ToList() : new List<JsonElement>();
        var nodes = root.TryGetProperty("nodes", out var ns) ? ns.EnumerateArray().ToList() : new List<JsonElement>();

        void Node(int index, Matrix4x4 parent, int depth)
        {
            if (depth > 64) throw new InvalidDataException("the nodes nest more than 64 deep");
            var n = nodes[index];
            var world = Local(n) * parent;
            if (n.TryGetProperty("mesh", out var meshIx)) Mesh(meshes[meshIx.GetInt32()], world);
            if (n.TryGetProperty("children", out var kids))
                foreach (var k in kids.EnumerateArray()) Node(k.GetInt32(), world, depth + 1);
        }
        void Mesh(JsonElement mesh, Matrix4x4 world)
        {
            bool mirrored = world.GetDeterminant() < 0;
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                int mode = prim.TryGetProperty("mode", out var md) ? md.GetInt32() : 4;
                if (mode != 4) { m.Skipped.Add($"a primitive of mode {mode} (only triangles are read)"); continue; }
                var attrs = prim.GetProperty("attributes");
                if (!attrs.TryGetProperty("POSITION", out var posIx)) { m.Skipped.Add("a primitive with no positions"); continue; }
                var pos = Accessor(root, buffers, posIx.GetInt32(), out int pcomp, out string ptype);
                if (ptype != "VEC3" || pcomp != 5126) throw new InvalidDataException("positions that are not three floats (quantised meshes are not read)");
                int baseIndex = m.Positions.Count;
                for (int i = 0; i < pos.Length / 3; i++)
                    m.Positions.Add(Vector3.Transform(new Vector3((float)pos[3 * i], (float)pos[3 * i + 1], (float)pos[3 * i + 2]), world));
                string name = "body";
                if (prim.TryGetProperty("material", out var matIx))
                {
                    int mi = matIx.GetInt32();
                    name = mi < materials.Count && materials[mi].TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } s ? s : $"material {mi}";
                }
                int surface = m.Surface(name);
                double[] idx;
                if (prim.TryGetProperty("indices", out var ixIx)) idx = Accessor(root, buffers, ixIx.GetInt32(), out _, out _);
                else { idx = new double[pos.Length / 3]; for (int i = 0; i < idx.Length; i++) idx[i] = i; }
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = baseIndex + (int)idx[t], b = baseIndex + (int)idx[t + 1], c = baseIndex + (int)idx[t + 2];
                    if (mirrored) (b, c) = (c, b);
                    m.Triangles.Add(a); m.Triangles.Add(b); m.Triangles.Add(c); m.Triangles.Add(surface);
                }
            }
        }

        if (root.TryGetProperty("scenes", out var scenes))
        {
            int sceneIx = root.TryGetProperty("scene", out var sc) ? sc.GetInt32() : 0;
            var scene = scenes.EnumerateArray().ElementAt(sceneIx);
            if (scene.TryGetProperty("nodes", out var roots))
                foreach (var r in roots.EnumerateArray()) Node(r.GetInt32(), Matrix4x4.Identity, 0);
        }
        else for (int i = 0; i < meshes.Count; i++) Mesh(meshes[i], Matrix4x4.Identity);
        return m;
    }

    private static Matrix4x4 Local(JsonElement n)
    {
        if (n.TryGetProperty("matrix", out var mx))
        {
            var a = mx.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            // Column-major in glTF; System.Numerics multiplies row vectors, so the same numbers read row by row.
            return new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
        }
        var t = n.TryGetProperty("translation", out var tr) ? V3(tr) : Vector3.Zero;
        var s = n.TryGetProperty("scale", out var scl) ? V3(scl) : Vector3.One;
        var r = Quaternion.Identity;
        if (n.TryGetProperty("rotation", out var rot))
        {
            var q = rot.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            r = new Quaternion(q[0], q[1], q[2], q[3]);
        }
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);

        static Vector3 V3(JsonElement e) { var a = e.EnumerateArray().Select(x => x.GetSingle()).ToArray(); return new Vector3(a[0], a[1], a[2]); }
    }

    /// <summary>An accessor's numbers, as doubles, its components in order.</summary>
    private static double[] Accessor(JsonElement root, List<byte[]> buffers, int index, out int componentType, out string type)
    {
        var acc = root.GetProperty("accessors").EnumerateArray().ElementAt(index);
        if (acc.TryGetProperty("sparse", out _)) throw new InvalidDataException("a sparse accessor (not read)");
        componentType = acc.GetProperty("componentType").GetInt32();
        type = acc.GetProperty("type").GetString()!;
        int count = acc.GetProperty("count").GetInt32();
        int comps = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => throw new InvalidDataException($"an accessor of {type}") };
        int size = componentType switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException($"component type {componentType}") };
        var result = new double[count * comps];
        if (!acc.TryGetProperty("bufferView", out var bvIx)) return result;
        var bv = root.GetProperty("bufferViews").EnumerateArray().ElementAt(bvIx.GetInt32());
        var buf = buffers[bv.GetProperty("buffer").GetInt32()];
        int start = (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        int stride = bv.TryGetProperty("byteStride", out var st) ? st.GetInt32() : comps * size;
        for (int i = 0; i < count; i++)
            for (int c = 0; c < comps; c++)
            {
                int at = start + i * stride + c * size;
                if (at + size > buf.Length) throw new InvalidDataException("an accessor runs past its buffer");
                result[i * comps + c] = componentType switch
                {
                    5126 => BitConverter.ToSingle(buf, at),
                    5125 => BitConverter.ToUInt32(buf, at),
                    5123 => BitConverter.ToUInt16(buf, at),
                    5122 => BitConverter.ToInt16(buf, at),
                    5121 => buf[at],
                    _ => (sbyte)buf[at],
                };
            }
        return result;
    }
}

/// <summary>
/// Readied triangles for the world (docs/GEOMETRY.md 4.3, 4.5): corners in the same place welded, triangles with
/// no area and repeats dropped, a solid checked closed and turned right way out, the budget kept, the whole set
/// about the middle of its bounds. A mesh that is not closed is refused as a solid: it can only be a sheet.
/// </summary>
public static class MeshImporter
{
    /// <summary>The most triangles an imported mesh may have (Cody, 2026-10-06: 50,000).</summary>
    public const int Budget = 50_000;

    /// <summary>Corners nearer than this are one, metres.</summary>
    public const float WeldMetres = 1e-5f;

    public sealed record Result(MeshAssetData? Asset, MeshReport Report, List<string> Problems, List<string> Notes);

    /// <summary>One face of a file, cut into triangles: a triangle as it is, a polygon by ears in its own plane.</summary>
    public static void AddPolygon(ImportedTriangles m, int[] corners, int surface)
    {
        if (corners.Length == 3) { m.Triangles.AddRange(new[] { corners[0], corners[1], corners[2], surface }); return; }
        // The polygon's plane by Newell's method, then the corners in it.
        var n = Vector3.Zero;
        for (int i = 0; i < corners.Length; i++)
        {
            var a = m.Positions[corners[i]]; var b = m.Positions[corners[(i + 1) % corners.Length]];
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        if (n.LengthSquared() < 1e-20f) return;
        n = Vector3.Normalize(n);
        var u = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, n));
        var v = Vector3.Cross(n, u);
        var flat = corners.Select(k => new Point2(Vector3.Dot(m.Positions[k], u), Vector3.Dot(m.Positions[k], v))).ToList();
        foreach (var t in Chunk3(Polygons.Triangulate(flat)))
            m.Triangles.AddRange(new[] { corners[t.A], corners[t.B], corners[t.C], surface });

        static IEnumerable<(int A, int B, int C)> Chunk3(List<int> l) { for (int i = 0; i + 2 < l.Count; i += 3) yield return (l[i], l[i + 1], l[i + 2]); }
    }

    /// <summary>
    /// The triangles readied as an asset: <paramref name="sheet"/> takes an open mesh as a sheet, otherwise it
    /// must be closed. <paramref name="scale"/> turns the file's units to metres; <paramref name="materials"/>
    /// gives a surface's material by its name (a missing one is the thing's own).
    /// </summary>
    public static Result Prepare(ImportedTriangles input, bool sheet = false, float scale = 1f,
                                 IReadOnlyDictionary<string, string>? materials = null, int budget = Budget, string source = "")
    {
        var problems = new List<string>(); var notes = new List<string>(input.Skipped);
        // Weld: corners on the same 10 µm are one.
        var key = new Dictionary<(long, long, long), int>();
        var verts = new List<Vector3>();
        var remap = new int[input.Positions.Count];
        for (int i = 0; i < input.Positions.Count; i++)
        {
            var p = input.Positions[i] * scale;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) { problems.Add($"vertex {i} is not a number"); remap[i] = 0; continue; }
            var k = ((long)MathF.Round(p.X / WeldMetres), (long)MathF.Round(p.Y / WeldMetres), (long)MathF.Round(p.Z / WeldMetres));
            if (!key.TryGetValue(k, out int at)) { at = verts.Count; verts.Add(p); key[k] = at; }
            remap[i] = at;
        }
        var tris = new List<int>(); var surf = new List<byte>();
        var seen = new HashSet<(int, int, int)>();
        int dropped = 0, repeats = 0;
        var usedSurfaces = new List<int>();
        for (int t = 0; t + 3 < input.Triangles.Count + 1; t += 4)
        {
            int a = remap[input.Triangles[t]], b = remap[input.Triangles[t + 1]], c = remap[input.Triangles[t + 2]];
            if (a == b || b == c || a == c || Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).LengthSquared() < 4e-16f) { dropped++; continue; }
            var sorted = Sort3(a, b, c);
            if (!seen.Add(sorted)) { repeats++; continue; }
            int s = input.Triangles[t + 3];
            int slot = usedSurfaces.IndexOf(s);
            if (slot < 0) { usedSurfaces.Add(s); slot = usedSurfaces.Count - 1; }
            tris.Add(a); tris.Add(b); tris.Add(c); surf.Add((byte)Math.Min(255, slot));
        }
        if (dropped > 0) notes.Add($"{dropped} triangles with no area left out");
        if (repeats > 0) notes.Add($"{repeats} repeated triangles left out");
        if (usedSurfaces.Count > 256) problems.Add($"{usedSurfaces.Count} surfaces: a mesh has at most 256");
        if (tris.Count == 0) problems.Add("no triangles");
        if (tris.Count / 3 > budget) problems.Add($"{tris.Count / 3} triangles, over the budget of {budget}");

        var report = MeshCheck.Check(verts, tris);
        bool closed = report.OpenEdges == 0 && report.CrowdedEdges == 0 && tris.Count > 0;
        if (closed && report.Volume < 0)
        {
            for (int t = 0; t < tris.Count; t += 3) (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
            report = MeshCheck.Check(verts, tris);
            notes.Add("it was inside out (its triangles faced in): turned right way out");
        }
        if (!sheet && !report.Closed)
            problems.Add($"not a closed solid ({report.OpenEdges} open edges, {report.CrowdedEdges} edges shared by more than two triangles): "
                         + "close it, or import it as a sheet");
        if (report.Slivers > 0) notes.Add($"{report.Slivers} slivers under {MeshCheck.SliverMetres * 1000:0} mm");
        if (report.SmallestFeature < 0.01f && report.Triangles > 0) notes.Add($"its smallest feature is {report.SmallestFeature * 1000:0.#} mm, under a centimetre");
        if (problems.Count > 0) return new Result(null, report, problems, notes);

        // About the middle of its bounds: the box it fills is centred on the thing.
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (int i in tris) { lo = Vector3.Min(lo, verts[i]); hi = Vector3.Max(hi, verts[i]); }
        var mid = (lo + hi) * 0.5f;
        // Only the corners the triangles use, in the order they first use them.
        var order = new Dictionary<int, int>();
        var finalV = new List<Vector3>();
        var finalI = new int[tris.Count];
        for (int i = 0; i < tris.Count; i++)
        {
            if (!order.TryGetValue(tris[i], out int at)) { at = finalV.Count; finalV.Add(verts[tris[i]] - mid); order[tris[i]] = at; }
            finalI[i] = at;
        }
        var mesh = new MeshAsset(finalV.ToArray(), finalI, surf.ToArray(), closed: !sheet, convex: false);
        var names = usedSurfaces.Select(s => s < input.SurfaceNames.Count ? input.SurfaceNames[s] : $"surface {s}").ToArray();
        var mats = names.Select(n => materials != null && materials.TryGetValue(n, out var mat) ? mat : "").ToArray();
        notes.Add($"{mesh.TriangleCount} triangles, {mesh.Vertices.Length} corners, {(sheet ? "a sheet" : "closed")}, "
                  + $"{hi.X - lo.X:0.###} by {hi.Y - lo.Y:0.###} by {hi.Z - lo.Z:0.###} m; surfaces: {string.Join(", ", names)}");
        return new Result(MeshAssetData.From(mesh, names, mats, source), MeshCheck.Check(mesh), problems, notes);

        static (int, int, int) Sort3(int a, int b, int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
            return (a, b, c);
        }
    }

    /// <summary>The most convex pieces an imported mesh is met as.</summary>
    public const int MaxPieces = 512;

    /// <summary>
    /// The convex pieces a body meets an imported mesh as, in its own frame: a solid as boxes filling it (its
    /// inside found along rows of a grid of at most 32 cells on its longest side, merged into the fewest boxes
    /// greedily), a sheet as each triangle 4 cm thick. Null for a sheet of more than <see cref="MaxPieces"/>
    /// triangles, which a body walks through.
    /// </summary>
    public static MeshAsset[]? PiecesOf(MeshAsset m)
    {
        if (!m.Closed)
        {
            if (m.TriangleCount > MaxPieces) return null;
            var slabs = new List<MeshAsset>();
            for (int t = 0; t < m.TriangleCount; t++)
            {
                Vector3 a = m.Vertices[m.Indices[3 * t]], b = m.Vertices[m.Indices[3 * t + 1]], c = m.Vertices[m.Indices[3 * t + 2]];
                var n = Vector3.Normalize(Vector3.Cross(b - a, c - a)) * 0.02f;
                var bd = new Shapes.Builder();
                bd.Tri(a + n, b + n, c + n);
                bd.Tri(a - n, c - n, b - n);
                bd.Quad(a - n, b - n, b + n, a + n);
                bd.Quad(b - n, c - n, c + n, b + n);
                bd.Quad(c - n, a - n, a + n, c + n);
                slabs.Add(bd.Make(convex: true));
            }
            return slabs.ToArray();
        }
        var size = m.BoundsMax - m.BoundsMin;
        for (int cells = 32; cells >= 4; cells /= 2)
        {
            float cell = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / cells;
            if (!(cell > 0)) return null;
            int nx = Math.Max(1, (int)MathF.Ceiling(size.X / cell)), ny = Math.Max(1, (int)MathF.Ceiling(size.Y / cell)), nz = Math.Max(1, (int)MathF.Ceiling(size.Z / cell));
            var inside = new bool[nx, ny, nz];
            var hits = new List<float>();
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                {
                    // A row along X through the cells' middles, a hair off them so it misses the edges of a grid of faces.
                    float y = m.BoundsMin.Y + (j + 0.5f) * cell + 1.7e-4f * cell, z = m.BoundsMin.Z + (k + 0.5f) * cell + 2.3e-4f * cell;
                    hits.Clear();
                    for (int t = 0; t < m.TriangleCount; t++)
                    {
                        Vector3 a = m.Vertices[m.Indices[3 * t]], b = m.Vertices[m.Indices[3 * t + 1]], c = m.Vertices[m.Indices[3 * t + 2]];
                        if (RowCrosses(a, b, c, y, z, out float x)) hits.Add(x);
                    }
                    hits.Sort();
                    for (int h = 0; h + 1 < hits.Count; h += 2)
                        for (int i = 0; i < nx; i++)
                        {
                            float x = m.BoundsMin.X + (i + 0.5f) * cell;
                            if (x >= hits[h] && x <= hits[h + 1]) inside[i, j, k] = true;
                        }
                }
            var boxes = new List<MeshAsset>();
            var used = new bool[nx, ny, nz];
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        if (!inside[i, j, k] || used[i, j, k]) continue;
                        int i1 = i; while (i1 + 1 < nx && inside[i1 + 1, j, k] && !used[i1 + 1, j, k]) i1++;
                        int j1 = j; while (j1 + 1 < ny && Row(inside, used, i, i1, j1 + 1, k)) j1++;
                        int k1 = k; while (k1 + 1 < nz && Slab(inside, used, i, i1, j, j1, k1 + 1)) k1++;
                        for (int c = k; c <= k1; c++) for (int b = j; b <= j1; b++) for (int a = i; a <= i1; a++) used[a, b, c] = true;
                        var lo = m.BoundsMin + new Vector3(i, j, k) * cell;
                        var hi = Vector3.Min(m.BoundsMax, m.BoundsMin + new Vector3(i1 + 1, j1 + 1, k1 + 1) * cell);
                        boxes.Add(Shapes.BoxPiece(lo, hi));
                    }
            if (boxes.Count <= MaxPieces) return boxes.Count > 0 ? boxes.ToArray() : null;
        }
        return null;

        static bool Row(bool[,,] inside, bool[,,] used, int i0, int i1, int j, int k)
        {
            for (int i = i0; i <= i1; i++) if (!inside[i, j, k] || used[i, j, k]) return false;
            return true;
        }
        static bool Slab(bool[,,] inside, bool[,,] used, int i0, int i1, int j0, int j1, int k)
        {
            for (int j = j0; j <= j1; j++) if (!Row(inside, used, i0, i1, j, k)) return false;
            return true;
        }
    }

    /// <summary>Whether the line along X at (y, z) crosses a triangle, and where.</summary>
    private static bool RowCrosses(Vector3 a, Vector3 b, Vector3 c, float y, float z, out float x)
    {
        x = 0f;
        float d = (b.Y - a.Y) * (c.Z - a.Z) - (c.Y - a.Y) * (b.Z - a.Z);
        if (MathF.Abs(d) < 1e-12f) return false;
        float u = ((y - a.Y) * (c.Z - a.Z) - (c.Y - a.Y) * (z - a.Z)) / d;
        float v = ((b.Y - a.Y) * (z - a.Z) - (y - a.Y) * (b.Z - a.Z)) / d;
        if (u < 0f || v < 0f || u + v > 1f) return false;
        x = a.X + u * (b.X - a.X) + v * (c.X - a.X);
        return true;
    }
}
