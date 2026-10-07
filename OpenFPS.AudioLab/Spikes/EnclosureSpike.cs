using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// What the room round a listener measures, at a place on a real map.
///
///   --enclosure [map=city] [at=x,y,z] [walk=x0,z0:x1,z1] [step=2] [head=1.6] [dist=1.6]
///
/// For "the reverb clicks in when I step into an area and pops when I step out": the shipped map's
/// solid boxes built as the acoustic worker builds them, and the survey at a point or a step at a time
/// along a walk, which shows a number jumping. The last column is the reverb send a source at
/// <c>dist</c> metres gets (Enclosure.ReverberantToDirectPower), as your own footsteps do: over 100 %
/// the room answers louder than the sound, and a figure that swings as you walk is heard as a pop.
/// </summary>
public static class EnclosureSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string mapId = Str(args, "map") ?? "city";
        float head = Num(args, "head", 1.6f);
        float dist = Num(args, "dist", 1.6f);
        float step = Num(args, "step", 2f);

        string? mapPath = OpenFPS.AudioLab.LabPaths.Existing(OpenFPS.AudioLab.LabPaths.Server("maps", mapId + ".json"))
                       ?? OpenFPS.AudioLab.LabPaths.Existing(Path.Combine(AppContext.BaseDirectory, "maps", mapId + ".json"));
        // The prefabs beside the map, always: an empty `prefabs/` at the repo root once built a world of
        // no solid boxes, every place reading as open field.
        string? prefabDir = mapPath == null ? null
            : Path.Combine(Directory.GetParent(mapPath)!.Parent!.FullName, "prefabs");
        if (prefabDir != null && !Directory.Exists(prefabDir)) prefabDir = null;
        if (mapPath == null || prefabDir == null)
        {
            Console.WriteLine($"  FAIL: could not find maps/{mapId}.json and prefabs/ from {Directory.GetCurrentDirectory()}");
            return 1;
        }

        var solids = LoadSolids(mapPath, prefabDir, out Vector3 spawn);
        Console.WriteLine($"\n  {mapId}: {solids.Count} solid boxes from {prefabDir}, spawn {spawn}\n");
        if (solids.Count == 0)
        {
            Console.WriteLine("  FAIL: no geometry. Every reading below would say 'open field'.");
            return 1;
        }

        var points = new List<(string Label, Vector3 At)>();
        string? walk = Str(args, "walk");
        if (walk != null)
        {
            var ends = walk.Split(':');
            var a = ends[0].Split(','); var b = ends[1].Split(',');
            var from = new Vector3(F(a[0]), head, F(a[1]));
            var to = new Vector3(F(b[0]), head, F(b[1]));
            float len = Vector3.Distance(from, to);
            int n = Math.Max(1, (int)(len / MathF.Max(0.1f, step)));
            for (int i = 0; i <= n; i++)
            {
                var at = Vector3.Lerp(from, to, i / (float)n);
                points.Add(($"{at.X,7:F1} {at.Z,7:F1}", at));
            }
        }
        else
        {
            string? at = Str(args, "at");
            if (at != null)
            {
                var p = at.Split(',');
                points.Add(("given", new Vector3(F(p[0]), p.Length > 2 ? F(p[1]) : head, F(p[p.Length - 1]))));
            }
            else points.Add(("spawn", spawn + new Vector3(0, head, 0)));
        }

        // The decay's colour as well as its length: a tail losing its top four times faster than its
        // middle is muffled however long it is.
        Console.WriteLine("       where          enclosure   open    mfp   surface   decay lo/mid/hi ms   absorption lo/mid/hi   send at " + dist.ToString("F1") + " m");
        float lastSend = -1f;
        foreach (var (label, at) in points)
        {
            var survey = Enclosure.Look(at, solids);
            var (low, mid, high) = Enclosure.DecaySeconds(survey);
            float ratio = Enclosure.ReverberantToDirectPower(survey.Enclosure, survey.MeanFreePathMetres,
                                                            survey.SurfaceAreaSquareMetres, dist);
            float send = MathF.Sqrt(ratio);
            // A jump between one step and the next is the artefact, whatever the level is.
            string jump = lastSend >= 0f && lastSend > 0.01f
                ? $"   {20f * MathF.Log10(MathF.Max(send, 1e-4f) / MathF.Max(lastSend, 1e-4f)),+6:F1} dB from the last step"
                : "";
            Console.WriteLine($"  {label,-16} {survey.Enclosure,8:P0} {survey.OpenFraction,6:P0} "
                            + $"{survey.MeanFreePathMetres,6:F2} {survey.SurfaceAreaSquareMetres,8:F0} "
                            + $"{low * 1000f,6:F0}/{mid * 1000f:F0}/{high * 1000f:F0}   "
                            + $"{survey.AbsorptionLow,5:F3}/{survey.AbsorptionMid:F3}/{survey.AbsorptionHigh:F3}   "
                            + $"{send,8:P0}{jump}");
            lastSend = send;
        }
        Console.WriteLine();
        return 0;
    }

    /// <summary>The map's solid boxes, built the way AsyncAcousticWorker builds them: a prefab that is
    /// not solid is not geometry, and the collider is the prefab's size times the entity's scale.</summary>
    private static List<Enclosure.Solid> LoadSolids(string mapPath, string prefabDir, out Vector3 spawn)
    {
        var prefabs = new Dictionary<string, (Vector3 Size, string Material)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(prefabDir, "*.json"))
        {
            if (Path.GetFileName(file) == "prefab-schema.json") continue;
            using var d = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = d.RootElement;
            if (!r.TryGetProperty("Id", out var idj) || !r.TryGetProperty("ColliderSize", out var cs)) continue;
            bool solid = !r.TryGetProperty("IsSolid", out var sj) || sj.ValueKind != JsonValueKind.False;
            // A region or a portal is not a surface; neither is anything that makes a noise.
            if (!solid) continue;
            prefabs[idj.GetString()!] = (ReadVec(cs),
                r.TryGetProperty("Material", out var mj) ? mj.GetString() ?? "Generic" : "Generic");
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(mapPath),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        spawn = ReadVec(root.GetProperty("SpawnPoint").GetProperty("Position"));

        var solids = new List<Enclosure.Solid>();
        foreach (var e in root.GetProperty("Entities").EnumerateArray())
        {
            string id = e.GetProperty("PrefabId").GetString()!;
            if (!prefabs.TryGetValue(id, out var p)) continue;
            Vector3 pos = ReadVec(e.GetProperty("Position"));
            Vector3 scale = e.TryGetProperty("Scale", out var sc) ? ReadVec(sc) : Vector3.One;
            solids.Add(new Enclosure.Solid(pos, p.Size * scale, Quaternion.Identity, p.Material));
        }
        return solids;
    }

    private static Vector3 ReadVec(JsonElement v) => new(
        v.TryGetProperty("X", out var x) ? x.GetSingle() : 0f,
        v.TryGetProperty("Y", out var y) ? y.GetSingle() : 0f,
        v.TryGetProperty("Z", out var z) ? z.GetSingle() : 0f);

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);


    private static string? Str(string[] args, string name)
    {
        foreach (var a in args)
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return a[(name.Length + 1)..];
        return null;
    }

    private static float Num(string[] args, string name, float fallback)
        => float.TryParse(Str(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
