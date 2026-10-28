using System.Numerics;
using OpenFPS.Geometry;

namespace OpenFPS.Physics;

/// <summary>
/// A geometry piece (a tile's solids and ground, or a door leaf) made ready for a <see cref="BodyWorld"/>
/// (docs/PHYSICS_BOUNDARY.md 18.7): its triangles that a body meets, one welded mesh shape for each material, built
/// ahead on any thread by <see cref="PieceBodies.Prepare"/>. Friction and restitution belong to a body, so a piece with
/// several materials becomes several bodies. A piece that was never handed to a <see cref="PieceBodies"/> is freed
/// by <see cref="Dispose"/>.
/// </summary>
public sealed class PreparedPiece : IDisposable
{
    internal readonly record struct Part(string Material, BuiltMesh Mesh);

    internal readonly List<Part> Parts = new();

    public TileKey Key { get; }
    /// <summary>Where the piece's frame starts in the world: the pose its bodies are at when it is a tile.</summary>
    public Vector3 Origin { get; }
    public ulong Signature { get; }
    /// <summary>Triangles handed to the shapes, after the ones no body meets were left out.</summary>
    public int Triangles { get; internal set; }
    /// <summary>Triangles the piece holds in all.</summary>
    public int PieceTriangles { get; }
    /// <summary>Vertices the shapes hold, after welding.</summary>
    public int Vertices { get; internal set; }
    /// <summary>How long it took to make, ms.</summary>
    public double BuildMs { get; internal set; }

    public int PartCount => Parts.Count;

    /// <summary>True once a <see cref="PieceBodies"/> took the shapes; a piece goes to one tile or build, once.</summary>
    public bool Taken { get; internal set; }

    internal PreparedPiece(GeometryPiece piece)
    {
        Key = piece.Key; Origin = piece.Origin; Signature = piece.Signature; PieceTriangles = piece.TriangleCount;
    }

    public void Dispose()
    {
        foreach (var p in Parts) p.Mesh.Dispose();
        Parts.Clear();
    }
}

/// <summary>
/// Jolt's copy of the world's solid things, kept in step with ours (docs/PHYSICS_BOUNDARY.md 18.2 step 3 and 18.7): each
/// tile's triangles as static mesh bodies, each door leaf as kinematic mesh bodies, so that Jolt's ground is the BVH's.
///
/// <para>Heavy work is done ahead. <see cref="Prepare"/> (any thread) welds a piece's triangles into mesh shapes, and
/// Jolt builds each shape's tree there. What is left for the thread that owns the world is to register the shapes and
/// add the bodies, which <see cref="Pump"/> does a few at a time, calling <c>Optimize</c> once a tile is whole so the
/// first step after it does not build the broad phase (the 18 to 72 ms spikes of 18.4).</para>
///
/// <para>A tile is its geometry piece, found again by its key and told changed by its signature. The host either calls
/// <see cref="AddTile"/>, <see cref="DropTile"/>, <see cref="SetLeaf"/>, <see cref="MoveLeaf"/> and <see cref="DropLeaf"/>
/// as streaming and doors do them, or hands each new <see cref="TriangleWorld"/> to <see cref="Follow"/>, which works
/// out the difference. One thread owns it, the one that steps the world.</para>
/// </summary>
public sealed class PieceBodies : IDisposable
{
    /// <summary>The layers a body in the world meets: what a walking body collides with and what can be stood on.</summary>
    public const GeometryLayers Layers = GeometryLayers.Movement | GeometryLayers.Ground;

    /// <summary>Two vertices closer than this are one: our triangles meet at corners computed along different edges, which
    /// differ by a float's rounding, and Jolt finds an edge shared only by a shared vertex (else a ball rolling over a
    /// flat floor catches on the seams). Larger than the rounding at ten kilometres, far smaller than anything heard.</summary>
    public const float WeldMetres = 0.002f;

    private sealed class Record
    {
        public TileKey Key;
        public int Owner = -1;                 // -1 for a tile
        public ulong Signature;
        public PreparedPiece? Waiting;         // until its shapes are registered
        public int[] Shapes = Array.Empty<int>();
        public string[] Materials = Array.Empty<string>();
        public int NextBody;
        public readonly List<int> Bodies = new();
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public bool Whole;
        public bool Moving;                    // last given a pose different from the one before
    }

    private readonly BodyWorld _world;
    private readonly Dictionary<TileKey, Record> _tiles = new();
    private readonly Dictionary<int, Record> _leaves = new();
    private readonly List<Record> _queue = new();
    // A leaf's shapes are shared by every leaf of its build; they live as long as the feeder (a handful of pieces).
    private readonly Dictionary<ulong, (int[] Shapes, string[] Materials)> _leafShapes = new();
    private bool _disposed;

    public PieceBodies(BodyWorld world) => _world = world ?? throw new ArgumentNullException(nameof(world));

    /// <summary>Bodies yet to be added to the world.</summary>
    public int PendingBodies
    {
        get
        {
            int n = 0;
            foreach (var r in _queue) n += (r.Waiting?.PartCount ?? r.Shapes.Length) - r.NextBody;
            return n;
        }
    }

    public int TileCount => _tiles.Count;
    public int LeafCount => _leaves.Count;

    /// <summary>The tile's bodies in the world, and whether every one is in (and the broad phase rebuilt).</summary>
    public IReadOnlyList<int> TileBodies(TileKey key) => _tiles.TryGetValue(key, out var r) ? r.Bodies : Array.Empty<int>();
    public bool TileIsWhole(TileKey key) => _tiles.TryGetValue(key, out var r) && r.Whole;
    public bool HasTile(TileKey key) => _tiles.ContainsKey(key);
    public ulong? TileSignature(TileKey key) => _tiles.TryGetValue(key, out var r) ? r.Signature : null;
    public IReadOnlyList<int> LeafBodies(int owner) => _leaves.TryGetValue(owner, out var r) ? r.Bodies : Array.Empty<int>();
    public bool HasLeaf(int owner) => _leaves.ContainsKey(owner);

    // ═══ Preparing, on any thread ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The piece's triangles that bodies meet (<see cref="Layers"/>), welded and grouped by material, as mesh shapes. Safe
    /// on any thread, touches no world, and is the slow part: the work that must never be done where sounds are placed.
    /// Triangles are kept in the piece's frame (a tile's corner, a leaf's own origin), so a float holds them to
    /// sub-millimetres anywhere in the world.
    /// </summary>
    public static PreparedPiece Prepare(GeometryPiece piece)
    {
        ArgumentNullException.ThrowIfNull(piece);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var prepared = new PreparedPiece(piece);
        var groups = new Dictionary<string, Group>();
        var order = new List<Group>();
        int total = piece.TriangleCount;
        for (int t = 0; t < total; t++)
        {
            ref readonly var surface = ref piece.SurfaceOfTriangle(t);
            if ((surface.Layers & Layers) == 0) continue;
            var tr = piece.TriangleAt(t);
            if (!groups.TryGetValue(surface.Material ?? "", out var g))
            {
                g = new Group(surface.Material ?? "");
                groups[g.Material] = g;
                order.Add(g);
            }
            g.Add(tr.V0, tr.V0 + tr.E1, tr.V0 + tr.E2);
        }
        try
        {
            foreach (var g in order)
            {
                if (g.Indices.Count == 0) continue;
                var mesh = new MeshAsset(g.Vertices.ToArray(), g.Indices.ToArray(), new byte[g.Indices.Count / 3], closed: false, convex: false);
                prepared.Parts.Add(new PreparedPiece.Part(g.Material, BuiltMesh.Build(mesh)));
                prepared.Triangles += mesh.TriangleCount;
                prepared.Vertices += mesh.Vertices.Length;
            }
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
        prepared.BuildMs = clock.Elapsed.TotalMilliseconds;
        return prepared;
    }

    /// <summary>One material's triangles with their corners welded.</summary>
    private sealed class Group
    {
        public readonly string Material;
        public readonly List<Vector3> Vertices = new();
        public readonly List<int> Indices = new();
        private readonly Dictionary<(long, long, long), int> _cells = new();

        public Group(string material) => Material = material;

        private static long Cell(float v) => (long)MathF.Floor(v / WeldMetres);

        private int Weld(Vector3 v)
        {
            long cx = Cell(v.X), cy = Cell(v.Y), cz = Cell(v.Z);
            if (_cells.TryGetValue((cx, cy, cz), out int hit) && Near(Vertices[hit], v)) return hit;
            // A corner that rounding put on the far side of a cell's edge from its twin is in a neighbouring cell.
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if ((dx != 0 || dy != 0 || dz != 0) && _cells.TryGetValue((cx + dx, cy + dy, cz + dz), out hit) && Near(Vertices[hit], v))
                            return hit;
            int id = Vertices.Count;
            Vertices.Add(v);
            _cells[(cx, cy, cz)] = id;
            return id;
        }

        private static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) <= WeldMetres * WeldMetres;

        public void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            int ia = Weld(a), ib = Weld(b), ic = Weld(c);
            // A triangle whose corners were welded together has no area to stand on.
            if (ia == ib || ib == ic || ia == ic) return;
            Indices.Add(ia); Indices.Add(ib); Indices.Add(ic);
        }
    }

    // ═══ Tiles ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Queues a tile's prepared piece to go into the world (a tile already held under the key goes first), taking the
    /// shapes over. Its bodies come with <see cref="Pump"/>.
    /// </summary>
    public void AddTile(PreparedPiece piece)
    {
        Check();
        ArgumentNullException.ThrowIfNull(piece);
        if (piece.Taken) throw new InvalidOperationException("this prepared piece was already given to a world; prepare it again");
        piece.Taken = true;
        DropTile(piece.Key);
        var r = new Record { Key = piece.Key, Signature = piece.Signature, Position = piece.Origin, Waiting = piece };
        _tiles[piece.Key] = r;
        _queue.Add(r);
    }

    /// <summary>Takes a tile's bodies out of the world (or out of the queue if they have not gone in) and frees its
    /// shapes. False if there was no such tile.</summary>
    public bool DropTile(TileKey key)
    {
        Check();
        if (!_tiles.Remove(key, out var r)) return false;
        Release(r, freeShapes: true);
        return true;
    }

    private void Release(Record r, bool freeShapes)
    {
        _queue.Remove(r);
        foreach (int b in r.Bodies) _world.Remove(b);
        r.Bodies.Clear();
        r.Waiting?.Dispose();
        r.Waiting = null;
        if (freeShapes) foreach (int s in r.Shapes) _world.RemoveShape(s);
        r.Shapes = Array.Empty<int>();
    }

    // ═══ Door leaves ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A moving thing's pose: its bodies are added at it (kinematic, queued like a tile's) or, if the owner is already
    /// here, moved to it. <paramref name="piece"/> may be null once any leaf of the same build has been given; it is not
    /// freed here (the shapes of a leaf are shared and kept), so one prepared piece serves every owner of its build.
    /// </summary>
    public void SetLeaf(int owner, PreparedPiece? piece, Vector3 position, Quaternion rotation, float seconds = BodyWorld.StepSeconds * BodyWorld.StepsPerTick)
    {
        Check();
        if (rotation.LengthSquared() < 1e-6f) rotation = Quaternion.Identity;
        if (_leaves.TryGetValue(owner, out var r))
        {
            if (piece != null && piece.Signature != r.Signature) { DropLeaf(owner); }
            else
            {
                // A kinematic body keeps the speed it was last given, so a leaf that has stopped is told to stay: once.
                if (r.Position == position && r.Rotation == rotation && !r.Moving) return;
                r.Moving = r.Position != position || r.Rotation != rotation;
                r.Position = position; r.Rotation = rotation;
                foreach (int b in r.Bodies) _world.MoveKinematic(b, position, rotation, seconds);
                return;
            }
        }
        if (piece == null) throw new ArgumentNullException(nameof(piece), "a leaf of a build not seen before needs its prepared piece");
        if (!_leafShapes.ContainsKey(piece.Signature))
        {
            if (piece.Taken) throw new InvalidOperationException("this prepared piece was already given to a world; prepare it again");
            piece.Taken = true;
            var shapes = new int[piece.Parts.Count];
            var materials = new string[shapes.Length];
            for (int i = 0; i < shapes.Length; i++) { shapes[i] = _world.AddMesh(piece.Parts[i].Mesh); materials[i] = piece.Parts[i].Material; }
            piece.Parts.Clear();
            _leafShapes[piece.Signature] = (shapes, materials);
        }
        NewLeaf(owner, piece.Signature, piece.Key, position, rotation);
    }

    private void NewLeaf(int owner, ulong signature, TileKey key, Vector3 position, Quaternion rotation)
    {
        var (s, m) = _leafShapes[signature];
        var r = new Record { Key = key, Owner = owner, Signature = signature, Shapes = s, Materials = m, Position = position, Rotation = rotation };
        _leaves[owner] = r;
        _queue.Add(r);
    }

    /// <summary>Moves an owner's bodies to a pose; false if there are none.</summary>
    public bool MoveLeaf(int owner, Vector3 position, Quaternion rotation, float seconds = BodyWorld.StepSeconds * BodyWorld.StepsPerTick)
    {
        if (!_leaves.ContainsKey(owner)) return false;
        SetLeaf(owner, null, position, rotation, seconds);
        return true;
    }

    public bool DropLeaf(int owner)
    {
        Check();
        if (!_leaves.Remove(owner, out var r)) return false;
        Release(r, freeShapes: false);
        return true;
    }

    // ═══ Putting bodies in ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds up to <paramref name="maxBodies"/> of the queued bodies, oldest first, registering a tile's shapes as it
    /// comes to them, and rebuilds the broad phase once when a tile's last body is in. Returns how many were added.
    /// Called once a tick with a small budget, so a tile arrives over a few ticks and no one tick pays for it.
    /// </summary>
    public int Pump(int maxBodies)
    {
        Check();
        int added = 0;
        while (_queue.Count > 0 && added < maxBodies)
        {
            var r = _queue[0];
            if (r.Waiting != null)
            {
                var w = r.Waiting;
                var shapes = new int[w.Parts.Count];
                var materials = new string[shapes.Length];
                for (int i = 0; i < shapes.Length; i++) { shapes[i] = _world.AddMesh(w.Parts[i].Mesh); materials[i] = w.Parts[i].Material; }
                w.Parts.Clear();
                r.Shapes = shapes; r.Materials = materials; r.Waiting = null;
            }
            bool leaf = r.Owner >= 0;
            while (r.NextBody < r.Shapes.Length && added < maxBodies)
            {
                int i = r.NextBody++;
                r.Bodies.Add(_world.Add(r.Shapes[i], leaf ? BodyMotion.Kinematic : BodyMotion.Static, r.Position, r.Rotation, r.Materials[i]));
                added++;
            }
            if (r.NextBody < r.Shapes.Length) break;
            _queue.RemoveAt(0);
            r.Whole = true;
            if (!leaf && r.Bodies.Count > 0) _world.Optimize();
        }
        return added;
    }

    // ═══ Following the geometry ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Brings the world's bodies to <paramref name="world"/>: a tile no longer there, or there with another signature, is
    /// dropped; a new one is queued if <paramref name="prepared"/> has its piece ready (else its piece goes in
    /// <paramref name="missing"/> for the host to <see cref="Prepare"/> on a worker, and the next call picks it up);
    /// leaves are added, moved to their instances' poses, or dropped. Nothing heavy happens here. Returns how many
    /// pieces were missing.
    /// </summary>
    public int Follow(TriangleWorld world, Func<GeometryPiece, PreparedPiece?> prepared, List<GeometryPiece>? missing = null,
                      float seconds = BodyWorld.StepSeconds * BodyWorld.StepsPerTick)
    {
        Check();
        var tilesNow = new HashSet<TileKey>();
        var leavesNow = new HashSet<int>();
        int lacking = 0;
        foreach (ref readonly var inst in world.Instances)
        {
            var piece = inst.Piece;
            if (piece.IsEmpty) continue;
            if (inst.Owner < 0)
            {
                tilesNow.Add(piece.Key);
                if (_tiles.TryGetValue(piece.Key, out var held))
                {
                    if (held.Signature == piece.Signature) continue;
                    DropTile(piece.Key);
                }
                var p = prepared(piece);
                if (p == null) { lacking++; missing?.Add(piece); continue; }
                AddTile(p);
            }
            else
            {
                leavesNow.Add(inst.Owner);
                bool known = _leafShapes.ContainsKey(piece.Signature);
                if (_leaves.TryGetValue(inst.Owner, out var had) && had.Signature != piece.Signature) DropLeaf(inst.Owner);
                if (known)
                {
                    if (_leaves.ContainsKey(inst.Owner)) MoveLeaf(inst.Owner, inst.Position, inst.Rotation, seconds);
                    else NewLeaf(inst.Owner, piece.Signature, piece.Key, inst.Position, inst.Rotation);
                    continue;
                }
                var p = prepared(piece);
                if (p == null) { lacking++; missing?.Add(piece); continue; }
                SetLeaf(inst.Owner, p, inst.Position, inst.Rotation, seconds);
            }
        }
        List<TileKey>? goneTiles = null;
        foreach (var k in _tiles.Keys) if (!tilesNow.Contains(k)) (goneTiles ??= new()).Add(k);
        if (goneTiles != null) foreach (var k in goneTiles) DropTile(k);
        List<int>? goneLeaves = null;
        foreach (int o in _leaves.Keys) if (!leavesNow.Contains(o)) (goneLeaves ??= new()).Add(o);
        if (goneLeaves != null) foreach (int o in goneLeaves) DropLeaf(o);
        return lacking;
    }

    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Takes every body out and frees what was waiting. The world's own Dispose frees the shapes it holds.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        foreach (var r in _tiles.Values) { r.Waiting?.Dispose(); r.Waiting = null; }
        _tiles.Clear();
        _leaves.Clear();
        _queue.Clear();
        _disposed = true;
    }
}
