using System.Collections.Concurrent;
using System.IO.Compression;
using System.Numerics;
using MemoryPack;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// A mesh asset as it is stored and sent (docs/GEOMETRY.md 4.2, 4.4): its triangles, its surfaces by name with
/// the material each is made of unless a map says otherwise, and whether it is a closed solid or a sheet. On disk
/// as <c>meshes/HASH.mesh</c>, MemoryPack then Brotli; on the wire inside <c>MeshAssetBatch</c>. Fields append only.
/// </summary>
[MemoryPackable]
public sealed partial class MeshAssetData
{
    /// <summary>The asset's hash as sixteen hex digits (<see cref="MeshAsset.Hash"/>): its name everywhere.</summary>
    public string Id { get; set; } = "";
    /// <summary>x, y, z a vertex, metres, about the middle of its bounds.</summary>
    public float[] Positions { get; set; } = Array.Empty<float>();
    public int[] Indices { get; set; } = Array.Empty<int>();
    /// <summary>The surface of each triangle: an index into <see cref="SurfaceNames"/>.</summary>
    public byte[] Surfaces { get; set; } = Array.Empty<byte>();
    public string[] SurfaceNames { get; set; } = Array.Empty<string>();
    /// <summary>The acoustic material of each surface (the import's mapping file), "" for the thing's own.</summary>
    public string[] SurfaceMaterials { get; set; } = Array.Empty<string>();
    /// <summary>A closed solid; otherwise a sheet (a canopy, a fence's mesh), which has no inside.</summary>
    public bool Closed { get; set; }
    /// <summary>Where it came from, for the record: a file name, a licence, an author.</summary>
    public string Source { get; set; } = "";

    /// <summary>The asset as triangles, its hash checked against its id.</summary>
    public MeshAsset ToMesh()
    {
        var v = new Vector3[Positions.Length / 3];
        for (int i = 0; i < v.Length; i++) v[i] = new Vector3(Positions[3 * i], Positions[3 * i + 1], Positions[3 * i + 2]);
        return new MeshAsset(v, Indices, Surfaces, Closed, convex: false);
    }

    public static MeshAssetData From(MeshAsset m, string[] names, string[] materials, string source = "")
    {
        var p = new float[m.Vertices.Length * 3];
        for (int i = 0; i < m.Vertices.Length; i++) { p[3 * i] = m.Vertices[i].X; p[3 * i + 1] = m.Vertices[i].Y; p[3 * i + 2] = m.Vertices[i].Z; }
        return new MeshAssetData
        {
            Id = MeshLibrary.IdOf(m.Hash), Positions = p, Indices = m.Indices, Surfaces = m.TriangleSurface,
            SurfaceNames = names, SurfaceMaterials = materials, Closed = m.Closed, Source = source,
        };
    }

    /// <summary>The bytes of a .mesh file: MemoryPack, then Brotli.</summary>
    public byte[] ToFile()
    {
        var raw = MemoryPackSerializer.Serialize(this);
        using var outStream = new MemoryStream();
        using (var b = new BrotliStream(outStream, CompressionLevel.Optimal, leaveOpen: true)) b.Write(raw);
        return outStream.ToArray();
    }

    public static MeshAssetData FromFile(byte[] bytes)
    {
        using var inStream = new MemoryStream(bytes);
        using var b = new BrotliStream(inStream, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        b.CopyTo(raw);
        return MemoryPackSerializer.Deserialize<MeshAssetData>(raw.ToArray()) ?? throw new InvalidDataException("not a mesh asset");
    }
}

/// <summary>
/// The mesh assets a process knows, by id (docs/GEOMETRY.md 4.2): read from the server's meshes folder at start,
/// received by a client in <c>MeshAssetBatch</c> or read from its cache. Thread-safe. A shape that names a mesh
/// not yet here is made as its box until it arrives (<see cref="Version"/> says when anything arrived).
/// </summary>
public sealed class MeshLibrary
{
    /// <summary>The process's library: the shapes are made by static code, as the catalogue of sounds is.</summary>
    public static MeshLibrary Shared { get; } = new();

    /// <summary>An asset, its triangles, the convex pieces a body meets, and its file's bytes as read (sent as they are).</summary>
    public sealed record Entry(MeshAssetData Data, MeshAsset Mesh, MeshAsset[]? Parts, byte[]? File);

    private readonly ConcurrentDictionary<string, Entry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private int _version;

    /// <summary>Goes up by one each time an asset is added: geometry made before it may have used a box.</summary>
    public int Version => Volatile.Read(ref _version);

    public int Count => _byId.Count;
    public IEnumerable<string> Ids => _byId.Keys;

    public static string IdOf(ulong hash) => hash.ToString("x16");

    public bool TryGet(string? id, out Entry entry)
    {
        entry = null!;
        return !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out entry!);
    }

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>Adds an asset whose triangles hash to its id; returns why not, or null.</summary>
    public string? Add(MeshAssetData data, byte[]? file = null)
    {
        MeshAsset mesh;
        try { mesh = data.ToMesh(); }
        catch (ArgumentException e) { return e.Message; }
        if (!string.Equals(IdOf(mesh.Hash), data.Id, StringComparison.OrdinalIgnoreCase))
            return $"its triangles hash to {IdOf(mesh.Hash)}, not {data.Id}";
        if (_byId.ContainsKey(data.Id)) return null;
        var parts = MeshImporter.PiecesOf(mesh);
        if (_byId.TryAdd(data.Id, new Entry(data, mesh, parts, file))) Interlocked.Increment(ref _version);
        return null;
    }

    /// <summary>Every .mesh file in a folder; returns how many were added and what was wrong with the rest.</summary>
    public (int Added, List<string> Problems) LoadFolder(string dir)
    {
        var problems = new List<string>();
        int added = 0;
        if (!Directory.Exists(dir)) return (0, problems);
        foreach (var f in Directory.GetFiles(dir, "*.mesh").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var bytes = File.ReadAllBytes(f);
                var data = MeshAssetData.FromFile(bytes);
                if (!string.Equals(Path.GetFileNameWithoutExtension(f), data.Id, StringComparison.OrdinalIgnoreCase))
                { problems.Add($"{Path.GetFileName(f)}: holds {data.Id}"); continue; }
                if (Add(data, bytes) is { } why) problems.Add($"{Path.GetFileName(f)}: {why}");
                else added++;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or MemoryPackSerializationException)
            {
                problems.Add($"{Path.GetFileName(f)}: {e.Message}");
            }
        }
        return (added, problems);
    }
}
