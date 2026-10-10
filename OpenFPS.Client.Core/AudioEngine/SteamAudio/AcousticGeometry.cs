using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The acoustic scene as a triangle world (docs/GEOMETRY.md stage 1): the boxes of
/// SteamAudioScene.BoxesFromWorld, tiled, with open ground flagged on their surfaces. One store for
/// the Steam Audio tile sub-scenes (TileSceneSet) and Enclosure's survey.
///
/// <para>Incremental: a hash of each tile's boxes says which tiles changed, and open ground (which
/// reads what stands over a slab) is decided again for those and their neighbours only. Not
/// thread-safe: one Update at a time.</para>
/// </summary>
public sealed class AcousticGeometry
{
    private readonly TriangleWorldBuilder _builder;
    public float TileMetres => _builder.TileMetres;

    public TriangleWorld World => _builder.Current;
    public TriangleWorldBuilder Builder => _builder;

    /// <summary>The ground's height under a point as of the last <see cref="Update"/>.</summary>
    public GroundHeights Ground { get; private set; } = GroundHeights.Flat;

    /// <summary>Each tile's boxes as last seen: their order-free hash, and the solids made of them.</summary>
    private Dictionary<TileKey, (long Raw, List<SolidSpec> Solids)> _tiles = new();

    /// <summary>How long the last <see cref="Update"/> spent deciding what is open ground, milliseconds,
    /// and how many tiles it decided it for.</summary>
    public double LastFlagsMs { get; private set; }
    public int LastDirtyTiles { get; private set; }

    /// <summary>Tiles are built on one thread after the first build: the store runs on a niced
    /// background thread, and the thread pool it would borrow is the game's.</summary>
    public AcousticGeometry(float tileMetres) => _builder = new TriangleWorldBuilder(tileMetres) { Parallel = false };

    /// <summary>The cell of the grid of what covers the ground, metres (as TileSceneSet had it).</summary>
    private const float CoverCell = 16f;

    /// <summary>A one-off world from a box list (the parity harness, tests).</summary>
    public static TriangleWorld FromBoxes(IReadOnlyList<SteamAudioScene.Box> boxes, float tileMetres, ISet<int>? leaves = null)
        => new AcousticGeometry(tileMetres).Update(boxes, leaves);

    /// <summary>Brings the world up to <paramref name="boxes"/>, building only the tiles that changed.
    /// <paramref name="leaves"/> are the door leaves among them, by entity id, flagged
    /// SurfaceFlags.DoorLeaf for the routes through openings.</summary>
    public TriangleWorld Update(IReadOnlyList<SteamAudioScene.Box> boxes, ISet<int>? leaves = null,
                                IReadOnlyList<SolidSpec>? terrains = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool first = _tiles.Count == 0;
        terrains ??= Array.Empty<SolidSpec>();
        var ground = terrains.Count > 0 ? new GroundHeights(terrains) : null;
        Ground = ground ?? GroundHeights.Flat;
        var terrainOf = new Dictionary<TileKey, SolidSpec>();
        foreach (var t in terrains) if (t.Terrain != null) terrainOf.TryAdd(_builder.KeyOf(t), t);

        // Which tile each box is in (the builder's own rule: a box wider than a tile is in the wide piece).
        var byTile = new Dictionary<TileKey, List<SteamAudioScene.Box>>();
        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            var key = _builder.KeyOf(new SolidSpec(b.EntityId, b.Center, b.Rotation, b.Size, default));
            if (!byTile.TryGetValue(key, out var list)) byTile[key] = list = new List<SteamAudioScene.Box>();
            list.Add(b);
        }
        foreach (var k in terrainOf.Keys) if (!byTile.ContainsKey(k)) byTile[k] = new List<SteamAudioScene.Box>();
        var raw = new Dictionary<TileKey, long>(byTile.Count);
        foreach (var (k, list) in byTile)
        {
            long r = 17;
            foreach (var b in list) r += Hash(b) + (leaves != null && leaves.Contains(b.EntityId) ? 7919 : 0);   // order-free
            // The ground under a tile decides which of its slabs lie on it, so a new ground changes them all.
            if (terrainOf.TryGetValue(k, out var ts)) r += (long)ts.Terrain!.Hash * 31 + ts.Owner;
            raw[k] = r;
        }

        // Changed, and the neighbours whose open ground a change can alter; the wide piece whenever
        // anything changed (the ground under the whole map is open wherever nothing covers it).
        var changed = new HashSet<TileKey>();
        foreach (var (k, r) in raw)
            if (!_tiles.TryGetValue(k, out var had) || had.Raw != r) changed.Add(k);
        var removed = new List<TileKey>();
        foreach (var k in _tiles.Keys)
            if (!raw.ContainsKey(k)) { changed.Add(k); removed.Add(k); }
        var dirty = new HashSet<TileKey>();
        foreach (var k in changed)
        {
            if (k == TriangleWorldBuilder.WideKey) { dirty.Add(k); continue; }
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    dirty.Add(new TileKey(k.X + dx, k.Z + dz));
        }
        if (changed.Count > 0) dirty.Add(TriangleWorldBuilder.WideKey);
        dirty.IntersectWith(byTile.Keys);

        var rebuilt = new Dictionary<TileKey, List<SolidSpec>>();
        if (dirty.Count > 0)
        {
            var coveredAt = CoverOf(boxes, ground?.Lowest ?? 0f);
            foreach (var k in dirty)
            {
                var specs = new List<SolidSpec>(byTile[k].Count + 1);
                foreach (var b in byTile[k])
                    specs.Add(SpecOf(b, SteamAudioScene.IsOpenGround(b, SteamAudioScene.WorldExtents(b), coveredAt, ground),
                                     leaves != null && b.EntityId != 0 && leaves.Contains(b.EntityId)));
                if (terrainOf.TryGetValue(k, out var ts)) specs.Add(ts with { Surface = SceneTerrain.Surface });
                rebuilt[k] = specs;
            }
        }
        var next = new Dictionary<TileKey, (long, List<SolidSpec>)>(raw.Count);
        foreach (var (k, r) in raw)
            next[k] = (r, rebuilt.TryGetValue(k, out var specs) ? specs : _tiles[k].Solids);
        _tiles = next;
        LastDirtyTiles = dirty.Count;
        LastFlagsMs = clock.Elapsed.TotalMilliseconds;

        // Copies: the builder sorts what it is given, and the lists are kept here. A whole map at load is
        // built on every core; a tile or two after that on this thread alone.
        var toBuild = new Dictionary<TileKey, List<SolidSpec>>(rebuilt.Count);
        foreach (var (k, specs) in rebuilt) toBuild[k] = new List<SolidSpec>(specs);
        _builder.Parallel = first && toBuild.Count > 8;
        return _builder.Rebuild(toBuild, removed, Array.Empty<SolidSpec>());
    }

    /// <summary>Whether anything whose underside is at least <c>lowest</c> stands over (x, z): what stands
    /// over the ground, filed in a grid so a slab is not tested against every box round it.</summary>
    private static Func<float, float, float, bool> CoverOf(IReadOnlyList<SteamAudioScene.Box> boxes, float lowestGround)
    {
        var cover = new Dictionary<(int, int), List<(Vector3 Min, Vector3 Max)>>();
        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            var (lo, hi) = SteamAudioScene.WorldExtents(b);
            if (lo.Y < lowestGround + SteamAudioScene.LowestCover) continue;
            for (int cx = (int)MathF.Floor(lo.X / CoverCell); cx <= (int)MathF.Floor(hi.X / CoverCell); cx++)
                for (int cz = (int)MathF.Floor(lo.Z / CoverCell); cz <= (int)MathF.Floor(hi.Z / CoverCell); cz++)
                {
                    if (!cover.TryGetValue((cx, cz), out var l)) cover[(cx, cz)] = l = new List<(Vector3, Vector3)>();
                    l.Add((lo, hi));
                }
        }
        return (x, z, lowest) =>
        {
            if (!cover.TryGetValue(((int)MathF.Floor(x / CoverCell), (int)MathF.Floor(z / CoverCell)), out var l)) return false;
            foreach (var (lo, hi) in l)
                if (lo.Y >= lowest && x >= lo.X && x <= hi.X && z >= lo.Z && z <= hi.Z) return true;
            return false;
        };
    }

    /// <summary>A scene box as a solid of the acoustic layer.</summary>
    public static SolidSpec SpecOf(in SteamAudioScene.Box b, bool openGround, bool leaf = false)
    {
        var flags = (openGround ? SurfaceFlags.OpenGround : SurfaceFlags.None) | (leaf ? SurfaceFlags.DoorLeaf : SurfaceFlags.None);
        var surface = new Surface(b.Material ?? "", new Construction(b.Size, b.Build), GeometryLayers.Acoustics, flags);
        return SolidSpec.OfShape(b.EntityId, b.Center, b.Rotation, b.Size, surface, b.Form);
    }

    private static long Hash(in SteamAudioScene.Box b)
    {
        var h = new HashCode();
        h.Add(b.Center); h.Add(b.Size); h.Add(b.Rotation);
        h.Add(b.Material); h.Add(b.Build); h.Add(b.EntityId); h.Add(b.Form);
        // A mesh is its box until its asset arrives: the tile is built again then.
        if (b.Form is { Kind: ShapeKind.Mesh } m) h.Add(MeshLibrary.Shared.Contains(m.Mesh ?? ""));
        return h.ToHashCode();
    }
}
