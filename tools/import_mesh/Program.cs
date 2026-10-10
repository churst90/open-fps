using System.Text.Json;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;

// The mesh importer (docs/AUTHORING.md): a glTF 2.0 (.gltf, .glb) or OBJ file to a mesh asset.
//
//   ImportMesh FILE [--sheet] [--scale S] [--material SURFACE=MATERIAL]... [--materials MAP.json]
//                   [--out DIR] [--source TEXT] [--budget N] [--check]
//
// Prints what it found and the asset's id; writes DIR/ID.mesh (OpenFPS.Server/meshes by default) unless
// --check. Exit 0 written (or fit to write), 1 refused, 2 not understood.
var files = new List<string>();
bool sheet = false, check = false;
float scale = 1f;
int budget = MeshImporter.Budget;
string outDir = Path.Combine("OpenFPS.Server", "meshes"), source = "";
var materials = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    try
    {
        switch (a)
        {
            case "--sheet": sheet = true; break;
            case "--check": check = true; break;
            case "--scale": scale = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--budget": budget = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
            case "--out": outDir = Next(); break;
            case "--source": source = Next(); break;
            case "--material":
                var kv = Next().Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException("--material takes SURFACE=MATERIAL");
                materials[kv[0]] = kv[1];
                break;
            case "--materials":
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Next())) ?? new()) materials[k] = v;
                break;
            default:
                if (a.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"no option {a}");
                files.Add(a);
                break;
        }
    }
    catch (Exception e) when (e is ArgumentException or FormatException or IOException or JsonException)
    {
        Console.Error.WriteLine(e.Message);
        return 2;
    }
}
if (files.Count != 1)
{
    Console.Error.WriteLine("ImportMesh FILE [--sheet] [--scale S] [--material SURFACE=MATERIAL]... [--materials MAP.json] [--out DIR] [--source TEXT] [--budget N] [--check]");
    return 2;
}

string file = files[0];
ImportedTriangles read;
try
{
    read = Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".obj" => ObjReader.Read(new StreamReader(file)),
        ".gltf" or ".glb" => GltfReader.ReadFile(file),
        _ => throw new InvalidDataException("not a .gltf, .glb or .obj file"),
    };
}
catch (Exception e) when (e is IOException or InvalidDataException or JsonException or FormatException or KeyNotFoundException)
{
    Console.Error.WriteLine($"{file}: {e.Message}");
    return 1;
}

AcousticRegistry.EnsureInitialized();
var problems = new List<string>();
foreach (var (surface, mat) in materials)
{
    if (!read.SurfaceNames.Contains(surface)) problems.Add($"no surface called {surface} (it has {string.Join(", ", read.SurfaceNames)})");
    else if (!AcousticRegistry.IsKnown(mat)) problems.Add($"{surface}: no material called {mat}");
}
var result = MeshImporter.Prepare(read, sheet, scale, materials, budget, string.IsNullOrEmpty(source) ? Path.GetFileName(file) : source);
problems.AddRange(result.Problems);
foreach (var n in result.Notes) Console.WriteLine(n);
Console.WriteLine(result.Report);
if (problems.Count > 0 || result.Asset == null)
{
    foreach (var p in problems) Console.Error.WriteLine($"refused: {p}");
    return 1;
}
foreach (var (name, mat) in result.Asset.SurfaceNames.Zip(result.Asset.SurfaceMaterials))
    Console.WriteLine($"surface {name}: {(mat.Length > 0 ? mat : "the thing's own material")}");
Console.WriteLine($"id {result.Asset.Id}");
if (check) return 0;
Directory.CreateDirectory(outDir);
string path = Path.Combine(outDir, result.Asset.Id + ".mesh");
File.WriteAllBytes(path, result.Asset.ToFile());
Console.WriteLine($"wrote {path} ({new FileInfo(path).Length} bytes)");
return 0;
