using System.Diagnostics;
using System.Numerics;

namespace OpenFPS.Common.Geometry;

/// <summary>
/// Makes a <see cref="TriangleWorld"/> from solids and keeps making it as they change, building only the
/// tiles whose solids changed (docs/GEOMETRY.md 2.2 and 7, stage 1).
///
/// <para>A static solid belongs to the tile its centre is in (<see cref="TileMetres"/> squares from the
/// origin, as the streaming plan counts them), unless it is wider than a tile (the ground under a whole
/// map), in which case it is in one piece of its own with the world's origin. Each tile's solids are
/// sorted by owner and built in that order, so a server and a client holding the same things in a tile
/// build the same piece, bit for bit.</para>
///
/// <para>A mover (a door leaf) is an instance of a piece made at its own origin; leaves of the same size
/// and build share one. Moving one is a new pose and a refit (<see cref="Move"/>), not a rebuild.</para>
///
/// <para>Not thread-safe: one caller builds. The worlds it hands out are immutable and safe anywhere.</para>
/// </summary>
public sealed class TriangleWorldBuilder
{
    public float TileMetres { get; }

    /// <summary>The key of the piece that holds solids wider than a tile.</summary>
    public static readonly TileKey WideKey = new(int.MinValue, int.MinValue);

    private Dictionary<TileKey, GeometryPiece> _pieces = new();
    private readonly Dictionary<ulong, GeometryPiece> _moverPieces = new();
    private Dictionary<int, int> _moverIndex = new();

    public TriangleWorld Current { get; private set; } = TriangleWorld.Empty;

    /// <summary>Pieces built and kept by the last <see cref="Build"/>, its time, and its tile count.</summary>
    public int LastBuilt { get; private set; }
    public int LastKept { get; private set; }
    public double LastBuildMs { get; private set; }
    public int TileCount => _pieces.Count;

    /// <summary>Whether tiles are built on several threads at once. Each piece is built alone and the
    /// same either way.</summary>
    public bool Parallel { get; set; } = true;

    public TriangleWorldBuilder(float tileMetres)
    {
        TileMetres = tileMetres > 0f ? tileMetres : 250f;
    }

    /// <summary>
    /// The world for these solids: every tile whose solids are not exactly what its piece was built from
    /// is built again, every other piece is kept, tiles with nothing left go.
    /// </summary>
    public TriangleWorld Build(IReadOnlyList<SolidSpec> statics, IReadOnlyList<SolidSpec> movers)
    {
        var clock = Stopwatch.StartNew();
        var byTile = new Dictionary<TileKey, List<SolidSpec>>();
        foreach (var s in statics)
        {
            if (s.Mesh == null && s.Terrain == null && (s.BoxSize.X <= 0f || s.BoxSize.Y <= 0f || s.BoxSize.Z <= 0f)) continue;
            var key = KeyOf(s);
            if (!byTile.TryGetValue(key, out var list)) byTile[key] = list = new List<SolidSpec>();
            list.Add(s);
        }

        var keys = new List<TileKey>(byTile.Keys);
        keys.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
        var pieces = new GeometryPiece[keys.Count];
        var signatures = new ulong[keys.Count];
        var toBuild = new List<int>();
        // Each tile's solids sorted and hashed: on every core when allowed (a server's whole map, every
        // time its static geometry changes), each tile alone, so the answer is the same either way.
        void Sign(int i)
        {
            var list = byTile[keys[i]];
            list.Sort(static (a, b) => a.Owner.CompareTo(b.Owner));
            signatures[i] = Signature(list);
        }
        if (Parallel && keys.Count > 4) System.Threading.Tasks.Parallel.For(0, keys.Count, Sign);
        else for (int i = 0; i < keys.Count; i++) Sign(i);
        for (int i = 0; i < keys.Count; i++)
        {
            if (_pieces.TryGetValue(keys[i], out var had) && had.Signature == signatures[i]) pieces[i] = had;
            else toBuild.Add(i);
        }
        void BuildOne(int i) => pieces[i] = GeometryPiece.Build(keys[i], OriginOf(keys[i]), byTile[keys[i]], signatures[i]);
        if (Parallel && toBuild.Count > 1) System.Threading.Tasks.Parallel.ForEach(toBuild, BuildOne);
        else foreach (int i in toBuild) BuildOne(i);

        var next = new Dictionary<TileKey, GeometryPiece>(keys.Count);
        for (int i = 0; i < keys.Count; i++) next[keys[i]] = pieces[i];
        _pieces = next;

        var instances = new List<GeometryInstance>(keys.Count + movers.Count);
        for (int i = 0; i < keys.Count; i++) instances.Add(new GeometryInstance(pieces[i], pieces[i].Origin, Quaternion.Identity, -1));
        var moverIndex = PlaceMovers(movers, instances);

        Current = new TriangleWorld(instances.ToArray()).WithMoverIndex(moverIndex);
        LastBuilt = toBuild.Count;
        LastKept = keys.Count - toBuild.Count;
        LastBuildMs = clock.Elapsed.TotalMilliseconds;
        return Current;
    }

    /// <summary>
    /// The world with some tiles' solids replaced (<paramref name="changed"/>: each tile's full list, in
    /// any order), some tiles gone, and every other tile's piece kept as it is without being looked at:
    /// for a caller that knows which tiles changed (the acoustic store). A changed tile whose sorted
    /// solids hash to its piece's signature is kept too. Movers are as <paramref name="movers"/> says.
    /// </summary>
    public TriangleWorld Rebuild(IReadOnlyDictionary<TileKey, List<SolidSpec>> changed, IEnumerable<TileKey> removed,
                                 IReadOnlyList<SolidSpec> movers)
    {
        var clock = Stopwatch.StartNew();
        var next = new Dictionary<TileKey, GeometryPiece>(_pieces);
        foreach (var k in removed) next.Remove(k);
        var toBuild = new List<(TileKey Key, List<SolidSpec> Solids, ulong Signature)>();
        foreach (var (key, list) in changed)
        {
            if (list.Count == 0) { next.Remove(key); continue; }
            list.Sort(static (a, b) => a.Owner.CompareTo(b.Owner));
            ulong sig = Signature(list);
            if (next.TryGetValue(key, out var had) && had.Signature == sig) continue;
            toBuild.Add((key, list, sig));
        }
        var built = new GeometryPiece[toBuild.Count];
        void BuildOne(int i) => built[i] = GeometryPiece.Build(toBuild[i].Key, OriginOf(toBuild[i].Key), toBuild[i].Solids, toBuild[i].Signature);
        if (Parallel && toBuild.Count > 1) System.Threading.Tasks.Parallel.For(0, toBuild.Count, BuildOne);
        else for (int i = 0; i < toBuild.Count; i++) BuildOne(i);
        for (int i = 0; i < toBuild.Count; i++) next[toBuild[i].Key] = built[i];
        _pieces = next;

        var keys = new List<TileKey>(next.Keys);
        keys.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
        var instances = new List<GeometryInstance>(keys.Count + movers.Count);
        foreach (var k in keys) instances.Add(new GeometryInstance(next[k], next[k].Origin, Quaternion.Identity, -1));
        var moverIndex = PlaceMovers(movers, instances);
        Current = new TriangleWorld(instances.ToArray()).WithMoverIndex(moverIndex);
        LastBuilt = toBuild.Count;
        LastKept = keys.Count - toBuild.Count;
        LastBuildMs = clock.Elapsed.TotalMilliseconds;
        return Current;
    }

    private Dictionary<int, int> PlaceMovers(IReadOnlyList<SolidSpec> movers, List<GeometryInstance> instances)
    {
        var sortedMovers = new List<SolidSpec>(movers);
        sortedMovers.Sort(static (a, b) => a.Owner.CompareTo(b.Owner));
        var moverIndex = new Dictionary<int, int>(sortedMovers.Count);
        foreach (var m in sortedMovers)
        {
            if (m.Mesh == null && (m.BoxSize.X <= 0f || m.BoxSize.Y <= 0f || m.BoxSize.Z <= 0f)) continue;
            var piece = MoverPiece(m);
            moverIndex[m.Owner] = instances.Count;
            instances.Add(new GeometryInstance(piece, m.Position, m.Rotation, m.Owner));
        }
        _moverIndex = moverIndex;
        return moverIndex;
    }

    /// <summary>
    /// The current world with movers in new poses; those whose pose is what it was are left alone, and
    /// an owner that is not a mover is ignored. Returns the same world when nothing moved.
    /// </summary>
    public TriangleWorld Move(IReadOnlyList<(int Owner, Vector3 Position, Quaternion Rotation)> poses)
    {
        if (poses.Count == 0 || _moverIndex.Count == 0) return Current;
        Span<(int, Vector3, Quaternion)> moved = poses.Count <= 256 ? stackalloc (int, Vector3, Quaternion)[poses.Count] : new (int, Vector3, Quaternion)[poses.Count];
        int n = 0;
        foreach (var (owner, p, r) in poses)
        {
            if (!_moverIndex.TryGetValue(owner, out int i)) continue;
            ref readonly var inst = ref Current.Instance(i);
            var rot = r.LengthSquared() < 1e-6f ? Quaternion.Identity : r;
            if (inst.Position == p && inst.Rotation == rot) continue;
            moved[n++] = (i, p, rot);
        }
        if (n == 0) return Current;
        Current = Current.WithMoved(moved[..n]);
        return Current;
    }

    public TileKey KeyOf(in SolidSpec s)
    {
        // A terrain tile is its tile's ground, whatever its size: keyed by its middle.
        if (s.Terrain != null) return TileKey.Of(s.Position, TileMetres);
        var half = HalfExtents(s);
        if (2f * half.X > TileMetres || 2f * half.Z > TileMetres) return WideKey;
        return TileKey.Of(s.Position, TileMetres);
    }

    public Vector3 OriginOf(TileKey key) => key == WideKey ? Vector3.Zero : new Vector3(key.X * TileMetres, 0f, key.Z * TileMetres);

    private static Vector3 HalfExtents(in SolidSpec s)
    {
        if (s.Mesh != null)
            return Vector3.Max(Vector3.Abs(s.Mesh.BoundsMin), Vector3.Abs(s.Mesh.BoundsMax)) * 1.7320508f;
        var h = s.BoxSize * 0.5f;
        var r = s.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : s.Rotation;
        var x = Vector3.Abs(Vector3.Transform(new Vector3(h.X, 0, 0), r));
        var y = Vector3.Abs(Vector3.Transform(new Vector3(0, h.Y, 0), r));
        var z = Vector3.Abs(Vector3.Transform(new Vector3(0, 0, h.Z), r));
        return x + y + z;
    }

    private GeometryPiece MoverPiece(in SolidSpec m)
    {
        ulong key = SpecHash(14695981039346656037UL, m with { Owner = -1, Position = Vector3.Zero, Rotation = Quaternion.Identity });
        if (_moverPieces.TryGetValue(key, out var piece)) return piece;
        piece = GeometryPiece.Build(WideKey, Vector3.Zero, new[] { m with { Owner = -1, Position = Vector3.Zero, Rotation = Quaternion.Identity } }, key);
        _moverPieces[key] = piece;
        return piece;
    }

    /// <summary>A hash of a sorted list of solids: the same list, the same bits, the same number.</summary>
    public static ulong Signature(IReadOnlyList<SolidSpec> solids)
    {
        ulong h = 14695981039346656037UL;
        foreach (var s in solids) h = SpecHash(h, s);
        return h;
    }

    private static ulong SpecHash(ulong h, in SolidSpec s)
    {
        // FNV-1a a word at a time: the same bits in, the same number out, four times as fast as by bytes.
        void Mix(uint v) { h ^= v; h *= 1099511628211UL; h ^= h >> 29; }
        void F(float f) => Mix(BitConverter.SingleToUInt32Bits(f));
        void V(Vector3 v) { F(v.X); F(v.Y); F(v.Z); }
        void S(string? str) { if (str == null) { Mix(0xFFFFFFFFu); return; } Mix((uint)str.Length); foreach (char c in str) Mix(c); }
        Mix((uint)s.Owner);
        V(s.Position); F(s.Rotation.X); F(s.Rotation.Y); F(s.Rotation.Z); F(s.Rotation.W); V(s.BoxSize);
        void Sf(in Surface sf)
        {
            S(sf.Material); V(sf.Construction.PanelSize); F(sf.Construction.Build.LeafMetres); F(sf.Construction.Build.StudSpacingMetres);
            F(sf.Construction.ShellThickness); Mix((uint)sf.Layers); Mix((uint)sf.Flags); F(sf.Absorption);
        }
        Sf(s.Surface);
        if (s.Slots != null) { Mix((uint)s.Slots.Length); foreach (var slot in s.Slots) Sf(slot); }
        if (s.Mesh != null) { Mix((uint)(s.Mesh.Hash >> 32)); Mix((uint)s.Mesh.Hash); }
        if (s.Terrain != null) { Mix((uint)(s.Terrain.Hash >> 32)); Mix((uint)s.Terrain.Hash); }
        if (s.Parts != null) foreach (var part in s.Parts) { Mix((uint)(part.Hash >> 32)); Mix((uint)part.Hash); }
        return h;
    }
}
