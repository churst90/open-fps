using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The Steam Audio geometry as a tile owns it: each tile of the map has its own sub-scenes, built once
/// and kept while the tile's boxes stay the same, and the scenes the simulators trace are assembled from
/// instances of them (docs/WORLD_STREAMING.md, docs/GEOMETRY.md 2.4).
///
/// Per tile, two sub-scenes: its open ground (thin slabs at ground level with the sky over them) and
/// everything else. The full scene instances both; the listener's scene, which must not hear the ground
/// under its own feet (SteamAudioScene.WithoutOpenGround), instances only the second. Shared, so the
/// geometry is held once.
///
/// Each door leaf is a sub-scene of its own, the leaf's box in its own frame, instanced at the leaf's
/// pose: a door that swings changes an instance's transform, and no geometry is rebuilt. Leaves of the
/// same size and make share one.
///
/// Changing tiles or doors never rebuilds the whole scene: <see cref="Update"/> builds the sub-scenes of
/// the tiles whose boxes changed and lets go of those that went, and <see cref="Assemble"/> makes two new
/// top scenes of instances, which costs a fraction of a millisecond with Embree. The new scenes are
/// handed to the simulators as a rebuilt scene always was (AsyncAcousticWorker), so no scene is changed
/// while anything traces it. Needs Embree: Steam Audio's default tracer walks instances one by one and
/// takes twenty times as long to trace them (docs/GEOMETRY.md 6.4); without it the worker builds whole
/// scenes as before.
///
/// Not thread-safe: one Update and Assemble at a time, which the worker's one background build at a
/// time gives it.
/// </summary>
internal sealed class TileSceneSet : IDisposable
{
    private readonly IntPtr _context;
    public float TileMetres { get; }

    private sealed class Tile
    {
        public long Signature;
        /// <summary>The boxes alone, without which are open ground: what says the tile itself changed.</summary>
        public long Raw;
        public IntPtr Ground, Rest;            // sub-scenes; zero when that half is empty
        public int Boxes;
        public List<SteamAudioScene.Box> RestBoxes = new();
    }

    private readonly Dictionary<TileKey, Tile> _tiles = new();
    /// <summary>A door leaf's sub-scene by what it is (size, material, build).</summary>
    private readonly Dictionary<string, IntPtr> _leaves = new();
    private readonly List<(SteamAudioScene.Box Box, IntPtr Sub)> _doors = new();
    private readonly List<SteamAudioScene.Box> _all = new();

    /// <summary>Sub-scenes built by the last <see cref="Update"/>, and in all; tiles held.</summary>
    public int LastBuilt { get; private set; }
    public int TotalBuilt { get; private set; }
    public int TileCount => _tiles.Count;
    public double LastUpdateMs { get; private set; }
    public double LastAssembleMs { get; private set; }
    public double LastInstanceMs { get; private set; }

    /// <summary>
    /// Door leaves as instances of their own, each at its pose (OPENFPS_TILE_DOORS=1). Off: measured on
    /// Magnolia with about 500 leaves in reach, assembling the top scenes took 70 ms to 2.2 s instead of
    /// 5 to 60 ms, so a leaf stays in its tile's sub-scene and a door that swings rebuilds that one tile.
    /// Left for geometry stage 1, which makes moving things instances by design.
    /// </summary>
    internal static bool InstanceDoors = Environment.GetEnvironmentVariable("OPENFPS_TILE_DOORS") == "1";

    /// <summary>The cell of the grid of what covers the ground, metres.</summary>
    private const float CoverCell = 16f;

    public TileSceneSet(IntPtr context, float tileMetres)
    {
        _context = context;
        TileMetres = tileMetres > 0f ? tileMetres : 250f;
    }

    /// <summary>
    /// Brings the sub-scenes up to <paramref name="boxes"/>: the tiles whose boxes changed are built
    /// again, tiles with no boxes are let go. Boxes whose entity is in <paramref name="doorLeaves"/> are
    /// door leaves, instanced at their pose rather than built into their tile.
    /// </summary>
    public void Update(IReadOnlyList<SteamAudioScene.Box> boxes, ISet<int> doorLeaves)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        RetireOld();
        _all.Clear();
        _all.AddRange(boxes);

        // Boxes by the tile their centre is in; door leaves apart.
        var byTile = new Dictionary<TileKey, List<SteamAudioScene.Box>>();
        _doors.Clear();
        var leafBoxes = new List<SteamAudioScene.Box>();
        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            if (InstanceDoors && b.EntityId != 0 && doorLeaves.Contains(b.EntityId)) { leafBoxes.Add(b); continue; }
            var k = TileKey.Of(b.Center, TileMetres);
            if (!byTile.TryGetValue(k, out var list)) byTile[k] = list = new List<SteamAudioScene.Box>();
            list.Add(b);
        }

        // Which tiles changed: their boxes, or (for which of them are open ground) a neighbour's.
        var raw = new Dictionary<TileKey, long>();
        foreach (var (k, list) in byTile)
        {
            long r = 17;
            foreach (var b in list) r += Hash(b);   // order-free: boxes come from a dictionary of entities
            raw[k] = r;
        }
        var changed = new HashSet<TileKey>();
        foreach (var (k, r) in raw)
            if (!_tiles.TryGetValue(k, out var t) || t.Raw != r) changed.Add(k);
        foreach (var k in _tiles.Keys)
            if (!raw.ContainsKey(k)) changed.Add(k);
        var dirty = new HashSet<TileKey>();
        foreach (var k in changed)
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    dirty.Add(new TileKey(k.X + dx, k.Z + dz));

        // Which are open ground: whether anything stands over a slab is asked of a grid of what could
        // (anything whose underside is a metre and a half up or more: roofs, ceilings, canopies), so a
        // tile's slabs are not each tested against every box round them.
        var cover = new Dictionary<(int, int), List<(Vector3 Min, Vector3 Max)>>();
        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
            var (lo, hi) = SteamAudioScene.WorldExtents(b);
            if (lo.Y < SteamAudioScene.LowestCover) continue;
            for (int cx = (int)MathF.Floor(lo.X / CoverCell); cx <= (int)MathF.Floor(hi.X / CoverCell); cx++)
                for (int cz = (int)MathF.Floor(lo.Z / CoverCell); cz <= (int)MathF.Floor(hi.Z / CoverCell); cz++)
                {
                    if (!cover.TryGetValue((cx, cz), out var l)) cover[(cx, cz)] = l = new List<(Vector3, Vector3)>();
                    l.Add((lo, hi));
                }
        }
        bool CoveredAt(float x, float z, float lowest)
        {
            if (!cover.TryGetValue(((int)MathF.Floor(x / CoverCell), (int)MathF.Floor(z / CoverCell)), out var l)) return false;
            foreach (var (lo, hi) in l)
                if (lo.Y >= lowest && x >= lo.X && x <= hi.X && z >= lo.Z && z <= hi.Z) return true;
            return false;
        }
        int built = 0;
        foreach (var (k, list) in byTile)
        {
            if (!dirty.Contains(k)) continue;
            var ground = new List<SteamAudioScene.Box>();
            var rest = new List<SteamAudioScene.Box>();
            long signature = 17;
            for (int i = 0; i < list.Count; i++)
            {
                bool open = SteamAudioScene.IsOpenGround(list[i], SteamAudioScene.WorldExtents(list[i]), CoveredAt);
                (open ? ground : rest).Add(list[i]);
                signature += Hash(list[i]) * (open ? 31 : 1);
            }
            if (_tiles.TryGetValue(k, out var had) && had.Signature == signature) { had.Raw = raw[k]; continue; }
            if (had != null) { Retire(had.Ground); Retire(had.Rest); }
            _tiles[k] = new Tile { Signature = signature, Raw = raw[k], Ground = SubScene(ground), Rest = SubScene(rest), Boxes = list.Count, RestBoxes = rest };
            built++;
        }
        foreach (var k in new List<TileKey>(_tiles.Keys))
            if (!byTile.ContainsKey(k)) { Retire(_tiles[k].Ground); Retire(_tiles[k].Rest); _tiles.Remove(k); }

        foreach (var b in leafBoxes)
        {
            string key = $"{b.Size.X:F3}|{b.Size.Y:F3}|{b.Size.Z:F3}|{b.Material}|{b.Build}";
            if (!_leaves.TryGetValue(key, out var sub))
            {
                // The leaf at the origin, unturned: its pose is the instance's.
                sub = SubScene(new[] { b with { Center = Vector3.Zero, Rotation = Quaternion.Identity } });
                _leaves[key] = sub;
                built++;
            }
            if (sub != IntPtr.Zero) _doors.Add((b, sub));
        }
        LastBuilt = built;
        TotalBuilt += built;
        LastUpdateMs = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>The full scene and the listener's, as new top scenes of instances, committed.</summary>
    public (SteamAudioScene Full, SteamAudioScene Listener) Assemble()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        IntPtr full = SteamAudioScene.CreateScene(_context), listener = SteamAudioScene.CreateScene(_context);
        var fullInst = new List<IntPtr>();
        var listenerInst = new List<IntPtr>();
        var fullSubs = new HashSet<IntPtr>();
        var listenerSubs = new HashSet<IntPtr>();
        var listenerBoxes = new List<SteamAudioScene.Box>();
        var identity = Matrix(Vector3.Zero, Quaternion.Identity);
        foreach (var t in _tiles.Values)
        {
            if (t.Ground != IntPtr.Zero) { Instance(full, t.Ground, identity, fullInst); fullSubs.Add(t.Ground); }
            if (t.Rest != IntPtr.Zero)
            {
                Instance(full, t.Rest, identity, fullInst); fullSubs.Add(t.Rest);
                Instance(listener, t.Rest, identity, listenerInst); listenerSubs.Add(t.Rest);
            }
            listenerBoxes.AddRange(t.RestBoxes);
        }
        foreach (var (b, sub) in _doors)
        {
            var m = Matrix(b.Center, b.Rotation);
            Instance(full, sub, m, fullInst); fullSubs.Add(sub);
            Instance(listener, sub, m, listenerInst); listenerSubs.Add(sub);
            listenerBoxes.Add(b);
        }
        LastInstanceMs = clock.Elapsed.TotalMilliseconds;
        Phonon.iplSceneCommit(full);
        Phonon.iplSceneCommit(listener);
        LastAssembleMs = clock.Elapsed.TotalMilliseconds;
        return (SteamAudioScene.Assembled(_context, full, fullInst, fullSubs, _all),
                SteamAudioScene.Assembled(_context, listener, listenerInst, listenerSubs, listenerBoxes));
    }

    private static void Instance(IntPtr top, IntPtr sub, Phonon.IPLMatrix4x4 transform, List<IntPtr> into)
    {
        var settings = new Phonon.IPLInstancedMeshSettings { subScene = sub, transform = transform };
        if (Phonon.iplInstancedMeshCreate(top, ref settings, out IntPtr instance) != Phonon.IPL_STATUS_SUCCESS) return;
        Phonon.iplInstancedMeshAdd(instance, top);
        into.Add(instance);
    }

    /// <summary>
    /// A pose in Steam Audio's frame, row-major with the translation in the last column. Steam Audio's z
    /// runs the other way from the game's (Phonon.World), so with F the mirror in z the transform is
    /// F·R·F for the turn and F·c for the place; the sub-scene's vertices are already mirrored.
    /// </summary>
    internal static unsafe Phonon.IPLMatrix4x4 Matrix(Vector3 at, Quaternion rotation)
    {
        if (rotation.LengthSquared() < 1e-6f) rotation = Quaternion.Identity;
        var cols = new[] { Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation), Vector3.Transform(Vector3.UnitZ, rotation) };
        float[] s = { 1f, 1f, -1f };
        var m = new Phonon.IPLMatrix4x4();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                float r = i switch { 0 => cols[j].X, 1 => cols[j].Y, _ => cols[j].Z };
                m.elements[i * 4 + j] = s[i] * s[j] * r;
            }
        m.elements[3] = at.X; m.elements[7] = at.Y; m.elements[11] = -at.Z;
        m.elements[15] = 1f;
        return m;
    }

    private IntPtr SubScene(IReadOnlyList<SteamAudioScene.Box> boxes)
    {
        if (boxes.Count == 0) return IntPtr.Zero;
        IntPtr sub = SteamAudioScene.CreateScene(_context);
        if (sub == IntPtr.Zero) return IntPtr.Zero;
        IntPtr mesh = SteamAudioScene.AddMesh(_context, sub, boxes, out _, out _);
        Phonon.iplSceneCommit(sub);
        // The scene holds the mesh; the handle is not needed past the build.
        if (mesh != IntPtr.Zero) Phonon.iplStaticMeshRelease(ref mesh);
        return sub;
    }

    private static long Hash(in SteamAudioScene.Box b)
    {
        var h = new HashCode();
        h.Add(MathF.Round(b.Center.X * 100f)); h.Add(MathF.Round(b.Center.Y * 100f)); h.Add(MathF.Round(b.Center.Z * 100f));
        h.Add(MathF.Round(b.Size.X * 100f)); h.Add(MathF.Round(b.Size.Y * 100f)); h.Add(MathF.Round(b.Size.Z * 100f));
        h.Add(MathF.Round(b.Rotation.X * 1000f)); h.Add(MathF.Round(b.Rotation.Y * 1000f)); h.Add(MathF.Round(b.Rotation.Z * 1000f)); h.Add(MathF.Round(b.Rotation.W * 1000f));
        h.Add(b.Material); h.Add(b.Build); h.Add(b.EntityId);
        return h.ToHashCode();
    }

    /// <summary>Lets go of this set's hold on a sub-scene. A scene assembled with it holds its own
    /// (SteamAudioScene.Assembled retains each one), so it lives as long as anything traces it.</summary>
    private void Retire(IntPtr sub) { if (sub != IntPtr.Zero) Phonon.iplSceneRelease(ref sub); }

    private void RetireOld(bool all = false) { }

    public void Dispose()
    {
        foreach (var t in _tiles.Values) { Retire(t.Ground); Retire(t.Rest); }
        foreach (var s in _leaves.Values) Retire(s);
        _tiles.Clear(); _leaves.Clear(); _doors.Clear();
        RetireOld(all: true);
    }
}
