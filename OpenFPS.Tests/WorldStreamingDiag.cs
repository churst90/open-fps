using System.Numerics;
using MemoryPack;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

public class WorldStreamingDiag
{
    private readonly ITestOutputHelper _o;
    public WorldStreamingDiag(ITestOutputHelper o) => _o = o;

    [Fact]
    public void Diag()
    {
        foreach (var id in new[] { "magnolia_tx", "albany_or" })
        {
            string dir = Path.Combine(Path.GetTempPath(), "openfps-stream-" + Guid.NewGuid().ToString("N"));
            string maps = Path.Combine(dir, "maps");
            Directory.CreateDirectory(Path.Combine(maps, "places"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "places", id + ".json"), Path.Combine(maps, "places", id + ".json"));
            AcousticRegistry.Initialize();
            var manager = new MapManager(new MapRepository(maps), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
            manager.Initialize();
            manager.TryGetMap(id, out var world, out _, out _, out var lookup);
            manager.TryGetTiles(id, out var tiles);
            manager.TryGetMapData(id, out var data);
            _o.WriteLine($"{id}: file {data.Entities.Count}, tiled {tiles.TiledCount}, global {tiles.Global.Count}, lookup {lookup.Count}");
            var spawn = manager.GetSpawnPoint(id).Position;
            foreach (var radii in new[] { StreamRadii.Low, StreamRadii.Medium, StreamRadii.High, new StreamRadii(300, 300) })
            {
                var interest = new TileInterest { Radii = radii };
                var ids = TileStreamer.Begin(interest, tiles, spawn);
                long bytes = 0; int full = 0, coarse = 0;
                var byPrefab = new Dictionary<string, (int n, long b)>();
                foreach (int i in ids)
                {
                    if (!lookup.TryGetValue(i, out var e)) continue;
                    var def = EntityDefinitionFactory.From(world, e);
                    int b = MemoryPackSerializer.Serialize(def).Length;
                    bytes += b;
                    if (tiles.TryGet(i, out var m)) { if (m.Needs == TileDetail.Full) full++; else coarse++; }
                    string k = def.Identity.Name.Split(',')[^1].Trim().Split(' ')[^1];
                    var (n, bb) = byPrefab.GetValueOrDefault(k);
                    byPrefab[k] = (n + 1, bb + b);
                }
                _o.WriteLine($"  {radii}: {ids.Count} ids ({full} full-need, {coarse} coarse-need), {interest.Levels.Count(kv => kv.Value == TileDetail.Full)} full tiles, {interest.Levels.Count} tiles, {bytes / 1024} KB, {bytes / Math.Max(1, ids.Count)} B each");
                if (radii == StreamRadii.Medium)
                    foreach (var kv in byPrefab.OrderByDescending(kv => kv.Value.b).Take(12))
                        _o.WriteLine($"     {kv.Key}: {kv.Value.n} x {kv.Value.b / kv.Value.n} B = {kv.Value.b / 1024} KB");
            }
            {
                var interest = new TileInterest();
                var ids = TileStreamer.Begin(interest, tiles, spawn);
                var defs = ids.Where(lookup.ContainsKey).Select(i => EntityDefinitionFactory.From(world, lookup[i])).ToList();
                foreach (var level in new[] { 1, 4 })
                {
                    long raw = 0, packed = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var chunk in defs.Chunk(256))
                    {
                        var b = MemoryPackSerializer.Serialize<OpenFPS.Common.Networking.IMessage>(new OpenFPS.Common.Networking.EntityDefinitionBatch { Definitions = chunk.ToList() });
                        raw += b.Length;
                        using var ms = new MemoryStream();
                        using (var br = new System.IO.Compression.BrotliStream(ms, (System.IO.Compression.CompressionLevel)0)) { }
                        var enc = new System.IO.Compression.BrotliEncoder(level, 22);
                        var dst = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(b.Length)];
                        enc.Compress(b, dst, out _, out int written, true);
                        enc.Dispose();
                        packed += written;
                    }
                    _o.WriteLine($"  brotli q{level}: {raw / 1024} KB -> {packed / 1024} KB in {sw.ElapsedMilliseconds} ms");
                }
                {
                    long raw = 0, packed = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
                    foreach (var chunk in defs.Chunk(256))
                    {
                        var b = MemoryPackSerializer.Serialize<OpenFPS.Common.Networking.IMessage>(new OpenFPS.Common.Networking.EntityDefinitionBatch { Definitions = chunk.ToList() });
                        raw += b.Length;
                        using var ms = new MemoryStream();
                        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(b);
                        packed += ms.Length;
                    }
                    _o.WriteLine($"  zlib fastest: {raw / 1024} KB -> {packed / 1024} KB in {sw.ElapsedMilliseconds} ms");
                }
            }
            {
                var interest = new TileInterest();
                var ids = TileStreamer.Begin(interest, tiles, spawn);
                var defs = ids.Where(lookup.ContainsKey).Select(i => EntityDefinitionFactory.From(world, lookup[i])).ToList();
                var client = new OpenFPS.Client.Core.ClientWorldState();
                client.Clear(data.Size);
                foreach (var d in defs) client.RegisterDefinition(d);
                client.GetSnapshot();
                for (int k = 0; k < 3; k++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    client.SyncState(Array.Empty<OpenFPS.Common.Networking.EntityState>());
                    client.GetSnapshot();
                    double copy = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    client.RegisterDefinition(defs[5]);
                    client.GetSnapshot();
                    double withGrid = sw.Elapsed.TotalMilliseconds;
                    _o.WriteLine($"  snapshot of {defs.Count}: copy {copy:F1} ms, with grid rebuild {withGrid:F1} ms");
                }
            }
            var one = EntityDefinitionFactory.From(world, lookup[tiles.Members(TileKey.Of(spawn, 250f))[5]]);
            _o.WriteLine("  sample: " + System.Text.Json.JsonSerializer.Serialize(one, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }));
        }
    }
}
