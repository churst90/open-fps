using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Geometry;
using OpenFPS.Audio.SteamAudio;
using OpenFPS.Materials;
using OpenFPS.Physics;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --ground-pieces [map=city|magnolia_tx|albany_or] [detail=medium]: the low pieces the listener's trace
/// still holds. A piece is low when it is a slab (at most half a metre thick), its top can be walked
/// (no steeper than the body walks) and it lies within a metre of the ground under it. Every such
/// piece the filter keeps either has a roof over it, or is ground a person stands on that the listener's
/// own feet would bounce a sound off 9-15 ms late (a small room). Counts and top-face area, the kept ones
/// with and without cover, by width. A place map is read at its spawn (TileScenesSpike's way).
/// </summary>
public static class GroundPiecesSpike
{
    public static int Run(string[] args)
    {
        string Arg(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..] ?? fallback;
        string mapId = Arg("map", "city");
        AcousticRegistry.Initialize();
        WorldSnapshot world;
        if (mapId == "city" || mapId == "speedway")
        {
            string root = OpenFPS.AudioLab.LabPaths.Server() + Path.DirectorySeparatorChar;
            world = OpenFPS.AudioLab.Spikes.SirenRouteSpike.Load(root + "maps/" + mapId + ".json", root + "prefabs", "none", 0f).Item1;
        }
        else
        {
            var radii = StreamRadii.Named(Arg("detail", "medium")) ?? StreamRadii.Default;
            string dir = Path.Combine(Path.GetTempPath(), "openfps-ground-pieces-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "maps", "places"));
            File.Copy(OpenFPS.AudioLab.LabPaths.Server("maps", "places", mapId + ".json"), Path.Combine(dir, "maps", "places", mapId + ".json"));
            if (File.Exists(OpenFPS.AudioLab.LabPaths.Server("maps", "places", mapId + ".elevation")))
                File.Copy(OpenFPS.AudioLab.LabPaths.Server("maps", "places", mapId + ".elevation"), Path.Combine(dir, "maps", "places", mapId + ".elevation"));
            var maps = new MapManager(new MapRepository(Path.Combine(dir, "maps")), new PrefabRepository(OpenFPS.AudioLab.LabPaths.Server("prefabs")));
            maps.Initialize();
            Directory.Delete(dir, true);
            if (!maps.TryGetMap(mapId, out var ecs, out _, out _, out var lookup) || !maps.TryGetTiles(mapId, out var tiles))
            { Console.WriteLine($"FAIL: no streamed map {mapId}"); return 1; }
            var spawn = maps.GetSpawnPoint(mapId).Position;
            var interest = new TileInterest { Radii = radii };
            var snap = new WorldSnapshot();
            foreach (int id in TileStreamer.Begin(interest, tiles, spawn))
            {
                if (!lookup.TryGetValue(id, out var e) && !tiles.TryGetGlobal(id, out e)) continue;
                var def = EntityDefinitionFactory.From(ecs, e);
                snap.Entities[id] = new EntitySnapshot { Id = id, Definition = def, Transform = def.Transform };
            }
            world = snap;
        }
        var boxes = SteamAudioScene.BoxesFromWorld(world);
        var ground = new GroundHeights(world.TerrainSolids());
        var kept = SteamAudioScene.WithoutOpenGround(boxes, ground);
        Console.WriteLine($"{mapId}: {boxes.Count} boxes, {kept.Count} kept in the listener's trace, {boxes.Count - kept.Count} left out as open ground");

        // What stands over a piece: anything at least 1.5 m above its top whose footprint holds its middle.
        const float cell = 16f;
        var grid = new Dictionary<(int, int), List<(Vector3 Min, Vector3 Max)>>();
        foreach (var b in boxes)
        {
            var (lo, hi) = SteamAudioScene.WorldExtents(b);
            for (int cx = (int)MathF.Floor(lo.X / cell); cx <= (int)MathF.Floor(hi.X / cell); cx++)
                for (int cz = (int)MathF.Floor(lo.Z / cell); cz <= (int)MathF.Floor(hi.Z / cell); cz++)
                {
                    if (!grid.TryGetValue((cx, cz), out var l)) grid[(cx, cz)] = l = new();
                    l.Add((lo, hi));
                }
        }
        bool Covered(Vector3 c, float top)
        {
            if (!grid.TryGetValue(((int)MathF.Floor(c.X / cell), (int)MathF.Floor(c.Z / cell)), out var l)) return false;
            foreach (var (lo, hi) in l)
                if (lo.Y >= top + 1.5f && c.X >= lo.X && c.X <= hi.X && c.Z >= lo.Z && c.Z <= hi.Z) return true;
            return false;
        }

        var rows = new Dictionary<string, (int N, double Area)>();
        var examples = new List<string>();
        void Add(string k, double a) { rows.TryGetValue(k, out var r); rows[k] = (r.N + 1, r.Area + a); }
        foreach (var b in kept)
        {
            if (b.Size.Y > 0.5f) continue;
            var up = Vector3.Transform(Vector3.UnitY, b.Rotation);
            if (up.Y < SolidContact.WalkableCos) continue;
            var (lo, hi) = SteamAudioScene.WorldExtents(b);
            float gy = ground.At((lo.X + hi.X) * 0.5f, (lo.Z + hi.Z) * 0.5f);
            if (hi.Y > gy + 1.0f) continue;
            double area = b.Size.X * (double)b.Size.Z;
            string cover = Covered(b.Center, hi.Y) ? "under cover" : "OPEN to the sky";
            string kind = up.Y < 0.99f ? "sloped" : MathF.Min(b.Size.X, b.Size.Z) < 1f ? "narrow" : "wide";
            string high = hi.Y - gy > BodyConstants.StepHeight ? "above a step" : "within a step";
            Add($"{cover,-15} {kind,-6} {high}", area);
            if (cover[0] == 'O' && examples.Count < 12 && (kind != "wide" || high[0] == 'w'))
                examples.Add($"      {b.Material,-10} at {b.Center.X:F1},{b.Center.Z:F1} top {hi.Y - gy:F2} m over ground, {b.Size.X:F2} x {b.Size.Y:F2} x {b.Size.Z:F2}, up.y {up.Y:F3}");
        }
        int n = 0; double a = 0;
        foreach (var (k, (cnt, area)) in rows.OrderBy(r => r.Key))
        {
            Console.WriteLine($"  {k}: {cnt,6} pieces {area,10:F0} m2");
            if (k.StartsWith("OPEN")) { n += cnt; a += area; }
        }
        Console.WriteLine($"  open-to-the-sky low pieces kept: {n} pieces, {a:F0} m2");
        foreach (var e in examples) Console.WriteLine(e);

        // Every piece the rule takes out of the trace that ebeb7c5b's rule (broad, level, within a metre,
        // open to the sky) kept, grouped by material and size, with how high it lies over the ground.
        var removed = new Dictionary<string, (int N, double Area, float MinTop, float MaxTop, float MinUnder, float MaxUnder)>();
        int removedN = 0;
        for (int i = 0, k = 0; i < boxes.Count; i++)
        {
            if (k < kept.Count && kept[k].Equals(boxes[i])) { k++; continue; }
            var b = boxes[i];
            var (lo, hi) = SteamAudioScene.WorldExtents(b);
            float gy = ground.At((lo.X + hi.X) * 0.5f, (lo.Z + hi.Z) * 0.5f);
            if (OldOpenGround(b, lo, hi, gy, Covered)) continue;
            removedN++;
            string key = $"{b.Material,-10} {b.Size.X,6:F2} x {b.Size.Y:F2} x {b.Size.Z:F2}{(MathF.Abs(Vector3.Transform(Vector3.UnitY, b.Rotation).Y) < 0.99f ? " sloped" : "")}";
            float top = hi.Y - gy, under = lo.Y - gy;
            removed.TryGetValue(key, out var r);
            removed[key] = r.N == 0 ? (1, b.Size.X * (double)b.Size.Z, top, top, under, under)
                : (r.N + 1, r.Area + b.Size.X * (double)b.Size.Z, MathF.Min(r.MinTop, top), MathF.Max(r.MaxTop, top), MathF.Min(r.MinUnder, under), MathF.Max(r.MaxUnder, under));
        }
        Console.WriteLine($"  removed from the trace by this rule that ebeb7c5b's kept: {removedN} pieces");
        foreach (var (k, v) in removed.OrderByDescending(r => r.Value.N).ThenBy(r => r.Key))
            Console.WriteLine($"    {v.N,5} x {k}  {v.Area,8:F0} m2  top {v.MinTop:F2}..{v.MaxTop:F2}  underside {v.MinUnder:F2}..{v.MaxUnder:F2} m over the ground");
        return 0;
    }

    /// <summary>ebeb7c5b's rule: a broad level slab within a metre of the ground, open to the sky over it.</summary>
    private static bool OldOpenGround(SteamAudioScene.Box b, Vector3 lo, Vector3 hi, float groundY, Func<Vector3, float, bool> covered)
    {
        if (b.Size.Y > 0.5f || b.Size.X < 1f || b.Size.Z < 1f) return false;
        if (MathF.Abs(Vector3.Transform(Vector3.UnitY, b.Rotation).Y) < 0.99f) return false;
        if (hi.Y > groundY + 1.0f) return false;
        var c = (lo + hi) * 0.5f; var q = (hi - lo) * 0.25f;
        int open = 0;
        foreach (var p in new[] { c, c + new Vector3(-q.X, 0, -q.Z), c + new Vector3(q.X, 0, -q.Z), c + new Vector3(-q.X, 0, q.Z), c + new Vector3(q.X, 0, q.Z) })
            if (!covered(p, hi.Y)) open++;
        return open >= 3;
    }
}
