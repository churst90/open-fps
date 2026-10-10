using System.Globalization;
using System.Numerics;
using System.Text;
using MemoryPack;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;
using OpenFPS.Common.Networking;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Geometry stage 4's import (docs/GEOMETRY.md 4.2 to 4.5, docs/AUTHORING.md): OBJ and glTF 2.0 read by our own
/// small readers, welded, checked closed (or taken as a sheet), turned right way out, kept to the budget, written as
/// .mesh files, sent in MeshAssetBatch and kept in a client's cache; a mesh shape fitted to its box, met by a body
/// as boxes filling it.
/// </summary>
public class GeometryImportTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-import-" + Guid.NewGuid().ToString("N"));

    public GeometryImportTests(ITestOutputHelper o) { _o = o; Directory.CreateDirectory(_dir); }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>An L-shaped block 3 by 2 by 1 with a 1 by 1 bite, as OBJ text: quads, one material for the top.</summary>
    private static string LBlockObj(bool insideOut = false, bool open = false)
    {
        var outline = new (double X, double Z)[] { (0, 0), (3, 0), (3, 1), (1, 1), (1, 2), (0, 2) };
        var sb = new StringBuilder("# an L\n");
        foreach (var (x, z) in outline) sb.Append(Inv($"v {x} 0 {z}\n"));
        foreach (var (x, z) in outline) sb.Append(Inv($"v {x} 1 {z}\n"));
        // The first corner again, as its own vertex: welded back to one.
        sb.Append(Inv($"v {outline[0].X} 0 {outline[0].Z}\n"));
        int n = outline.Length;
        string F(params int[] k) => "f " + string.Join(' ', (insideOut ? k.Reverse() : k).Select(i => (i + 1).ToString(CultureInfo.InvariantCulture))) + "\n";
        sb.Append("usemtl stone\n");
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (open && i == 0) continue;
            sb.Append(F(i == 0 ? 2 * n : i, n + i, n + j, j));
        }
        // The outline counter-clockwise in (x, z) faces down; the top is it reversed.
        sb.Append(F(0, 1, 2, 3, 4, 5));
        sb.Append("usemtl moss\n");
        sb.Append(F(11, 10, 9, 8, 7, 6));
        return sb.ToString();

        static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }

    private static ImportedTriangles Obj(string text) => ObjReader.Read(new StringReader(text));

    [Fact]
    public void An_obj_solid_is_welded_closed_and_named_by_its_materials()
    {
        var read = Obj(LBlockObj());
        Assert.Equal(new[] { "stone", "moss" }, read.SurfaceNames);
        var r = MeshImporter.Prepare(read, materials: new Dictionary<string, string> { ["moss"] = "Grass" });
        Assert.Empty(r.Problems);
        var asset = r.Asset!;
        var mesh = asset.ToMesh();
        var report = MeshCheck.Check(mesh);
        _o.WriteLine(string.Join("; ", r.Notes) + " / " + report);
        Assert.True(report.Closed);
        Assert.Equal(12 + 8, mesh.TriangleCount);   // six sides of two triangles, and each L end in four
        Assert.Equal(new[] { "stone", "moss" }, asset.SurfaceNames);
        Assert.Equal(new[] { "", "Grass" }, asset.SurfaceMaterials);
        // About the middle of its bounds.
        Assert.Equal(Vector3.Zero, (mesh.BoundsMin + mesh.BoundsMax) * 0.5f);
        Assert.Equal(MeshLibrary.IdOf(mesh.Hash), asset.Id);
    }

    [Fact]
    public void Inside_out_is_turned_round_and_open_is_refused_as_a_solid()
    {
        var inside = MeshImporter.Prepare(Obj(LBlockObj(insideOut: true)));
        Assert.Empty(inside.Problems);
        Assert.Contains(inside.Notes, n => n.Contains("inside out"));
        Assert.True(MeshCheck.Check(inside.Asset!.ToMesh()).Closed);

        var open = MeshImporter.Prepare(Obj(LBlockObj(open: true)));
        Assert.Null(open.Asset);
        Assert.Contains(open.Problems, p => p.Contains("not a closed solid"));
        var sheet = MeshImporter.Prepare(Obj(LBlockObj(open: true)), sheet: true);
        Assert.Empty(sheet.Problems);
        Assert.False(sheet.Asset!.Closed);

        var big = MeshImporter.Prepare(Obj(LBlockObj()), budget: 10);
        Assert.Contains(big.Problems, p => p.Contains("budget"));
    }

    [Fact]
    public void A_mesh_file_reads_back_and_its_id_is_checked()
    {
        var asset = MeshImporter.Prepare(Obj(LBlockObj())).Asset!;
        var bytes = asset.ToFile();
        var back = MeshAssetData.FromFile(bytes);
        Assert.Equal(asset.Id, back.Id);
        Assert.Equal(asset.Positions, back.Positions);
        var library = new MeshLibrary();
        Assert.Null(library.Add(back, bytes));
        Assert.True(library.Contains(asset.Id));
        back.Positions[0] += 0.5f;
        Assert.NotNull(new MeshLibrary().Add(back));   // its triangles no longer hash to its name

        File.WriteAllBytes(Path.Combine(_dir, asset.Id + ".mesh"), bytes);
        File.WriteAllBytes(Path.Combine(_dir, "0000000000000000.mesh"), bytes);
        var (added, problems) = new MeshLibrary().LoadFolder(_dir);
        Assert.Equal(1, added);
        Assert.Single(problems);
    }

    /// <summary>A gltf with its buffer in a data: URI, a node moving and scaling the mesh, and a node mirroring a
    /// second copy (its triangles turned back round): both closed, each primitive on its material's surface.</summary>
    [Fact]
    public void A_gltf_is_read_through_its_nodes()
    {
        var (json, _) = BoxGltf(mirror: true);
        var read = GltfReader.Read(Encoding.UTF8.GetBytes(json), _ => throw new FileNotFoundException());
        Assert.Equal(new[] { "brass" }, read.SurfaceNames);
        var r = MeshImporter.Prepare(read);
        Assert.Empty(r.Problems);
        var mesh = r.Asset!.ToMesh();
        Assert.True(MeshCheck.Check(mesh).Closed);
        Assert.Equal(24, mesh.TriangleCount);
        // The two boxes: one 2 m wide at x = 5, one mirrored to x = -5.
        Assert.Equal(12f, mesh.BoundsMax.X - mesh.BoundsMin.X, 3);

        // The same as a .glb.
        var (json2, bin) = BoxGltf(mirror: false, external: true);
        var glb = Glb(json2, bin);
        var fromGlb = MeshImporter.Prepare(GltfReader.Read(glb, _ => throw new FileNotFoundException()));
        Assert.Empty(fromGlb.Problems);
        Assert.Equal(12, fromGlb.Asset!.ToMesh().TriangleCount);
    }

    private static (string Json, byte[] Bin) BoxGltf(bool mirror, bool external = false)
    {
        var p = new List<float>();
        foreach (var c in ShapeLibrary.Box(Vector3.One * 2f).Vertices) { p.Add(c.X); p.Add(c.Y); p.Add(c.Z); }
        var idx = ShapeLibrary.Box(Vector3.One * 2f).Indices.Select(i => (ushort)i).ToArray();
        var bin = new byte[p.Count * 4 + idx.Length * 2];
        Buffer.BlockCopy(p.ToArray(), 0, bin, 0, p.Count * 4);
        Buffer.BlockCopy(idx, 0, bin, p.Count * 4, idx.Length * 2);
        string buffer = external ? $"{{\"byteLength\":{bin.Length}}}"
            : $"{{\"byteLength\":{bin.Length},\"uri\":\"data:application/octet-stream;base64,{Convert.ToBase64String(bin)}\"}}";
        string nodes = mirror
            ? "[{\"mesh\":0,\"translation\":[5,0,0]},{\"mesh\":0,\"translation\":[-5,0,0],\"scale\":[-1,1,1]}]"
            : "[{\"mesh\":0,\"translation\":[5,0,0]}]";
        string roots = mirror ? "[0,1]" : "[0]";
        string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":" + roots + "}],\"nodes\":" + nodes + ","
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1,\"material\":0}]}],"
            + "\"materials\":[{\"name\":\"brass\"}],"
            + $"\"buffers\":[{buffer}],"
            + $"\"bufferViews\":[{{\"buffer\":0,\"byteOffset\":0,\"byteLength\":{p.Count * 4}}},{{\"buffer\":0,\"byteOffset\":{p.Count * 4},\"byteLength\":{idx.Length * 2}}}],"
            + $"\"accessors\":[{{\"bufferView\":0,\"componentType\":5126,\"count\":{p.Count / 3},\"type\":\"VEC3\"}},"
            + $"{{\"bufferView\":1,\"componentType\":5123,\"count\":{idx.Length},\"type\":\"SCALAR\"}}]}}";
        return (json, bin);
    }

    private static byte[] Glb(string json, byte[] bin)
    {
        var j = Encoding.UTF8.GetBytes(json);
        int jl = (j.Length + 3) / 4 * 4, bl = (bin.Length + 3) / 4 * 4;
        var outBytes = new byte[12 + 8 + jl + 8 + bl];
        Encoding.ASCII.GetBytes("glTF").CopyTo(outBytes, 0);
        BitConverter.GetBytes(2).CopyTo(outBytes, 4);
        BitConverter.GetBytes(outBytes.Length).CopyTo(outBytes, 8);
        BitConverter.GetBytes(jl).CopyTo(outBytes, 12);
        BitConverter.GetBytes(0x4E4F534A).CopyTo(outBytes, 16);
        for (int i = 0; i < jl; i++) outBytes[20 + i] = i < j.Length ? j[i] : (byte)' ';
        BitConverter.GetBytes(bl).CopyTo(outBytes, 20 + jl);
        BitConverter.GetBytes(0x004E4942).CopyTo(outBytes, 24 + jl);
        bin.CopyTo(outBytes, 28 + jl);
        return outBytes;
    }

    /// <summary>A mesh shape: fitted to its box, met by a body as boxes filling it (the L's bite is empty), and its
    /// surfaces carry the materials the import gave them.</summary>
    [Fact]
    public void A_mesh_shape_is_fitted_to_its_box_and_met_as_its_pieces()
    {
        var asset = MeshImporter.Prepare(Obj(LBlockObj()), materials: new Dictionary<string, string> { ["moss"] = "Grass" }).Asset!;
        Assert.Null(MeshLibrary.Shared.Add(asset));
        var form = new ShapeSpec { Kind = ShapeKind.Mesh, Mesh = asset.Id };
        var size = new Vector3(6, 2, 4);   // twice the asset each way
        Assert.Null(Shapes.Problem(form, size));
        var made = Shapes.Make(form, size)!;
        Assert.Equal(-size / 2, made.Outer.BoundsMin);
        Assert.Equal(size / 2, made.Outer.BoundsMax);
        Assert.NotNull(made.Parts);
        Assert.All(made.Parts!, p => Assert.True(MeshCheck.Check(p).Closed));

        var surface = EntityGeometry.SurfaceOf("Concrete", size, 0, 0, false, 0, 0, false, false, false, null);
        var world = new TriangleWorldBuilder(250f).Build(new[] { SolidSpec.OfShape(9, new Vector3(0, 1, 0), Quaternion.Identity, size, surface, form) },
                                                         Array.Empty<SolidSpec>());
        var all = new AcceptAll();
        var inside = new List<SolidRef>();
        // The L in the world: x -3..3, z -2..2; the bite is x 0..3 (the asset's 1..3, doubled and centred), z 0..2.
        world.Containing(new Vector3(-2, 1, 1), GeometryLayers.Physical, ref all, inside);
        Assert.NotEmpty(inside);
        inside.Clear();
        world.Containing(new Vector3(2, 1, 1), GeometryLayers.Physical, ref all, inside);
        Assert.Empty(inside);
        Assert.True(world.Closest(new Vector3(-2, 5, 1), -Vector3.UnitY, 10f, GeometryLayers.Physical, RayFaces.Front, ref all, out var top));
        Assert.Equal("Grass", world.SurfaceOf(top).Material);
        Assert.True(world.Closest(new Vector3(-5, 1, -1), Vector3.UnitX, 10f, GeometryLayers.Physical, RayFaces.Front, ref all, out var side));
        Assert.Equal("Concrete", world.SurfaceOf(side).Material);
    }

    /// <summary>The wire: a client asks for what it lacks once, the server answers from its library, the client keeps
    /// what came (in memory and in its cache) and builds again what uses it.</summary>
    [Fact]
    public void Assets_go_over_the_wire_and_into_the_cache()
    {
        var asset = MeshImporter.Prepare(Obj(LBlockObj(insideOut: false)), source: "test L").Asset!;
        var server = new MeshLibrary();
        Assert.Null(server.Add(asset));
        string? folder = MeshAssetFetcher.Folder;
        MeshAssetFetcher.Folder = Path.Combine(_dir, "cache");
        try
        {
            var client = new MeshLibrary();
            var fetcher = new MeshAssetFetcher(client);
            fetcher.Want(asset.Id, 41);
            fetcher.Want(asset.Id, 42);
            fetcher.Want("ffffffffffffffff", 43);
            var request = fetcher.TakeRequest()!;
            Assert.Equal(2, request.Ids.Count);
            Assert.Null(fetcher.TakeRequest());
            IMessage sent = MeshAssetBatch.Answer(request, server);
            var bytes = MemoryPackSerializer.Serialize(sent);
            var back = (MeshAssetBatch)MemoryPackSerializer.Deserialize<IMessage>(bytes)!;
            Assert.Single(back.Missing);
            var users = fetcher.Arrived(back);
            Assert.Equal(new[] { 41, 42 }, users.OrderBy(u => u));
            Assert.True(client.Contains(asset.Id));
            Assert.True(File.Exists(Path.Combine(MeshAssetFetcher.Folder, asset.Id + ".mesh")));

            // A new session reads it from the cache and asks for nothing.
            var later = new MeshAssetFetcher(new MeshLibrary());
            later.Want(asset.Id, 41);
            Assert.Null(later.TakeRequest());
        }
        finally { MeshAssetFetcher.Folder = folder; }
    }
}
