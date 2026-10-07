using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// --siren-route [map=city] [track=downtown] [preset=police_interceptor] [lane=1.8] [at=x,z] [from=metres]
/// [sec=60] [every=10]: a car's path to a fixed listener frame by frame, as it drives its racing line,
/// through the game's AsyncAcousticWorker at the game's cadence (past 50 m, every tenth frame of a ~33 Hz
/// loop), printed as the per-band dB the provider is handed. For sirens fluttering behind buildings.
/// </summary>
public static class SirenRouteSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        MachineRegistry.EnsureLoaded();
        string mapId = Str(args, "map") ?? "city";
        string trackId = Str(args, "track") ?? "downtown";
        string preset = Str(args, "preset") ?? "police_interceptor";
        float lane = Num(args, "lane", 1.8f);
        float seconds = Num(args, "sec", 60f);
        int every = (int)Num(args, "every", 10f);
        float from = Num(args, "from", 0f);
        var atXz = (Str(args, "at") ?? "122.25,237.7").Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var ear = new Vector3(atXz[0], 0.15f + 1.6f, atXz[1]);

        string? mapPath = OpenFPS.AudioLab.LabPaths.Existing(OpenFPS.AudioLab.LabPaths.Server("maps", mapId + ".json"));
        string? prefabDir = OpenFPS.AudioLab.LabPaths.Existing(OpenFPS.AudioLab.LabPaths.Server("prefabs"));
        if (mapPath == null || prefabDir == null) { Console.WriteLine("FAIL: map or prefabs not found above cwd"); return 1; }
        var (world, line) = Load(mapPath, prefabDir, trackId, lane);
        if (line == null) { Console.WriteLine($"FAIL: no track {trackId}"); return 1; }
        Console.WriteLine($"  {world.Entities.Count} solid boxes; track {trackId} {line.Length:F0} m; ear ({ear.X:F1}, {ear.Y:F1}, {ear.Z:F1})");

        var profile = MachineRegistry.VehicleFor(preset);
        using var worker = new AsyncAcousticWorker(new SpatialAcoustics());
        worker.UpdateWorld(world);
        worker.Start();

        const float frame = 1f / 33f;
        float lap = from;
        float t = 0f;
        int n = 0;
        AcousticPathData last = default;
        bool haveLast = false;
        var jumps = new List<float>();
        Console.WriteLine("      t    lap      x      z   dist   occ   low   mid  high  (dB)  apparent-bearing  true-bearing  jump-mid");
        while (t < seconds)
        {
            line.Sample(lap, out Vector3 pos, out float heading, out float v);
            var rot = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
            var src = pos + Vector3.Transform(profile.ExhaustOffset, rot);
            float radius = Math.Clamp(src.Y - pos.Y, AudioEmission.MinOcclusionRadius, AudioEmission.DefaultOcclusionRadius);
            if (n % every == 0)
            {
                worker.EnqueueRequest(new AcousticRequest { EntityId = 1, ListenerPos = ear, SourcePos = src, SourceRadius = radius });
                var until = DateTime.UtcNow.AddSeconds(5);
                List<AcousticPathData>? paths = null;
                while (DateTime.UtcNow < until)
                {
                    if (worker.TryGetResult(1, out paths) && paths.Count > 0 && paths[0].SourcePosition == src) break;
                    Thread.Sleep(1);
                }
                if (paths != null && paths.Count > 0)
                {
                    var p = paths[0];
                    float dMid = Db(p.EqMid);
                    float jump = haveLast ? dMid - Db(last.EqMid) : 0f;
                    if (haveLast) jumps.Add(MathF.Abs(jump));
                    last = p; haveLast = true;
                    float dist = Vector3.Distance(ear, src);
                    Console.WriteLine($"  {t,5:F1} {lap,6:F0} {src.X,6:F1} {src.Z,6:F1} {dist,6:F0}  {p.Occlusion,4:F2} {Db(p.EqLow),5:F0} {dMid,5:F0} {Db(p.EqHigh),5:F0}        {Bearing(ear, p.ApparentPosition),7:F0}         {Bearing(ear, src),7:F0}      {jump,6:F1}");
                }
            }
            lap += v * frame;
            if (lap > line.Length) lap -= line.Length;
            t += frame;
            n++;
        }
        if (jumps.Count > 0)
        {
            var big = jumps.Count(j => j >= 6f);
            Console.WriteLine($"\n  {jumps.Count} answers; mid-band change between consecutive answers: median {jumps.OrderBy(j => j).ElementAt(jumps.Count / 2):F1} dB, " +
                              $"worst {jumps.Max():F1} dB, {big} of them 6 dB or more.");
        }
        return 0;
    }

    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-5f, g));
    private static float Bearing(Vector3 ear, Vector3 p) => MathF.Atan2(p.X - ear.X, p.Z - ear.Z) * 180f / MathF.PI;

    internal static (WorldSnapshot, RaceLine?) Load(string mapPath, string prefabDir, string trackId, float lane)
    {
        var prefabs = new Dictionary<string, (Vector3 Size, string Material, float Leaf, float Studs)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(prefabDir, "*.json"))
        {
            if (Path.GetFileName(file) == "prefab-schema.json") continue;
            using var d = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = d.RootElement;
            if (!r.TryGetProperty("Id", out var idj) || !r.TryGetProperty("ColliderSize", out var cs)) continue;
            bool solid = !r.TryGetProperty("IsSolid", out var sj) || sj.ValueKind != JsonValueKind.False;
            if (!solid) continue;
            if (r.TryGetProperty("SoundId", out var snd) && snd.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(snd.GetString())) continue;
            // How it is built, as PrefabRepository reads it: a door's skins are its leaves.
            float F(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0f;
            bool door = r.TryGetProperty("IsDoor", out var dj) && dj.ValueKind == JsonValueKind.True;
            float leaf = F("LeafMetres") > 0f ? F("LeafMetres") : door ? F("DoorSkinMetres") : 0f;
            prefabs[idj.GetString()!] = (V(cs), r.TryGetProperty("Material", out var mj) ? mj.GetString() ?? "Generic" : "Generic",
                                         leaf, F("StudSpacingMetres"));
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(mapPath));
        var root = doc.RootElement;
        var world = new WorldSnapshot();
        int id = 100;
        foreach (var e in root.GetProperty("Entities").EnumerateArray())
        {
            if (!prefabs.TryGetValue(e.GetProperty("PrefabId").GetString()!, out var p)) continue;
            var pos = V(e.GetProperty("Position"));
            var scale = e.TryGetProperty("Scale", out var sc) ? V(sc) : Vector3.One;
            var rot = Quaternion.Identity;
            if (e.TryGetProperty("Rotation", out var rj))
                rot = new Quaternion(rj.GetProperty("X").GetSingle(), rj.GetProperty("Y").GetSingle(), rj.GetProperty("Z").GetSingle(), rj.GetProperty("W").GetSingle());
            var def = new EntityDefinition
            {
                EntityId = id,
                Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = p.Size * scale, IsSolid = true },
                Material = new MaterialComponent { Material = p.Material },
                Acoustics = new AcousticComponent { LeafMetres = p.Leaf, StudSpacingMetres = p.Studs },
            };
            // A door's two rooms, as the server gives it (PrefabRepository): what makes a leaf a door.
            if (e.TryGetProperty("RegionAId", out var ra) && e.TryGetProperty("RegionBId", out var rb))
                def.Portal = new PortalComponent { RegionAId = ra.GetInt32(), RegionBId = rb.GetInt32() };
            world.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = new Transform { Position = pos, Rotation = rot, Scale = Vector3.One } };
            id++;
        }
        RaceLine? line = null;
        if (trackId == "downtown")
        {
            // The square as traffic drives it now: through its four corner junctions, by the lanes.
            var net = RoadNetwork.FromMapJson(root);
            var tour = net == null ? null : LaneRoutes.Via(net, RoadNetwork.DowntownCorners.Select(id => net.Junctions.First(j => j.Id == id)).ToList());
            if (tour != null) line = new RaceLine(tour.Points, 0f, 62f / 3.6f, 0.6f, 2.92f);
        }
        foreach (var t in root.GetProperty("Tracks").EnumerateArray())
        {
            if (!string.Equals(t.GetProperty("Id").GetString(), trackId, StringComparison.OrdinalIgnoreCase)) continue;
            var wp = t.GetProperty("Waypoints").EnumerateArray().Select(V).ToList();
            float half = MathF.Max(0f, t.GetProperty("WidthMetres").GetSingle() * 0.5f - 1.2f);
            line = new RaceLine(wp, Math.Clamp(lane, -half, half), 62f / 3.6f, 0.6f, 2.92f,
                                t.TryGetProperty("BankingDegrees", out var b) ? b.GetSingle() : 0f);
        }
        return (world, line);
    }

    private static Vector3 V(JsonElement v) => new(
        v.TryGetProperty("X", out var x) ? x.GetSingle() : 0f,
        v.TryGetProperty("Y", out var y) ? y.GetSingle() : 0f,
        v.TryGetProperty("Z", out var z) ? z.GetSingle() : 0f);


    private static string? Str(string[] a, string k) => a.FirstOrDefault(x => x.StartsWith(k + "=", StringComparison.Ordinal))?[(k.Length + 1)..];
    private static float Num(string[] a, string k, float f) => Str(a, k) is { } s && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : f;
}
