using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The Steam Audio geometry as a tile owns it (docs/WORLD_STREAMING.md, docs/GEOMETRY.md 2.4): each tile
/// is a sub-scene built once and kept while its boxes stay the same, and the scenes the simulators trace
/// are top scenes of instances of them. Needs Embree: Steam Audio's default tracer walks instances one
/// by one and traces them twenty times slower (docs/GEOMETRY.md 6.4); without Embree the worker builds
/// whole scenes as before.
///
/// <para>Per tile, two sub-scenes: its open ground (thin slabs at ground level with the sky over them)
/// and everything else. The full scene instances both; the listener's scene, which must not hear the
/// ground under its own feet (SteamAudioScene.WithoutOpenGround), instances only the second. A door leaf
/// stays in its tile's sub-scene, so a door that swings rebuilds that one tile.</para>
///
/// <para>Two pairs of top scenes, used in turn. While the simulators trace one pair, the other is idle:
/// a change (<see cref="Update"/>, then <see cref="Assemble"/>) adds the instances of the tiles that came
/// or were rebuilt to the idle pair, removes those of the tiles that went, commits it, and it is handed to
/// the simulators as a rebuilt scene always was (AsyncAcousticWorker). A scene is never committed while
/// anything traces it, which Steam Audio forbids, and nothing is assembled from scratch. Every instance
/// is made when its sub-scene is new, before anything traces it: making an instance of a sub-scene that
/// is being traced waits for the trace, measured at up to two thirds of a second.</para>
///
/// <para>Not thread-safe: one Update and Assemble at a time, which the worker's one background build at
/// a time gives it. The caller must only Assemble once the pair it last handed over is the only one in
/// use (AsyncAcousticWorker waits for TracedReverbSet.Reconfiguring to clear).</para>
/// </summary>
internal sealed class TileSceneSet : IDisposable
{
    private readonly IntPtr _context;
    public float TileMetres { get; }

    /// <summary>One version of one tile's geometry: its sub-scenes, and their instances in each pair.</summary>
    private sealed class Piece
    {
        public TileKey Key;
        public long Signature, Raw;
        public IntPtr Ground, Rest;
        public List<SteamAudioScene.Box> Boxes = new(), RestBoxes = new();
        public readonly IntPtr[] GroundIn = new IntPtr[2], RestIn = new IntPtr[2], ListenerIn = new IntPtr[2];
        public readonly bool[] InPair = new bool[2];
    }

    private readonly Dictionary<TileKey, Piece> _current = new();
    /// <summary>Pieces replaced or gone, still in a pair; let go once out of both.</summary>
    private readonly List<Piece> _dead = new();
    private readonly IntPtr[] _full = new IntPtr[2], _listener = new IntPtr[2];
    private readonly SteamAudioScene?[] _fullScene = new SteamAudioScene?[2], _listenerScene = new SteamAudioScene?[2];
    private int _active = -1;

    /// <summary>Sub-scenes built by the last <see cref="Update"/>, and in all; tiles held.</summary>
    public int LastBuilt { get; private set; }
    public int TotalBuilt { get; private set; }
    public int TileCount => _current.Count;
    public double LastUpdateMs { get; private set; }
    public double LastAssembleMs { get; private set; }
    /// <summary>True when Embree made both pairs of top scenes.</summary>
    public bool IsValid => _full[0] != IntPtr.Zero && _full[1] != IntPtr.Zero && _listener[0] != IntPtr.Zero && _listener[1] != IntPtr.Zero;

    /// <summary>The cell of the grid of what covers the ground, metres.</summary>
    private const float CoverCell = 16f;

    public TileSceneSet(IntPtr context, float tileMetres)
    {
        _context = context;
        TileMetres = tileMetres > 0f ? tileMetres : 250f;
        for (int b = 0; b < 2; b++)
        {
            _full[b] = SteamAudioScene.CreateScene(context);
            _listener[b] = SteamAudioScene.CreateScene(context);
            _fullScene[b] = SteamAudioScene.Borrowed(context, _full[b]);
            _listenerScene[b] = SteamAudioScene.Borrowed(context, _listener[b]);
        }
    }

    /// <summary>
    /// Brings the tiles up to <paramref name="boxes"/>: a tile whose boxes changed gets new sub-scenes
    /// (and their instances, made now while nothing traces them), a tile with no boxes left goes. Nothing
    /// changes in either pair of top scenes until <see cref="Assemble"/>.
    /// </summary>
    public void Update(IReadOnlyList<SteamAudioScene.Box> boxes)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var byTile = new Dictionary<TileKey, List<SteamAudioScene.Box>>();
        foreach (var b in boxes)
        {
            if (b.Size.X <= 0 || b.Size.Y <= 0 || b.Size.Z <= 0) continue;
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
            if (!_current.TryGetValue(k, out var p) || p.Raw != r) changed.Add(k);
        foreach (var k in _current.Keys)
            if (!raw.ContainsKey(k)) changed.Add(k);
        var dirty = new HashSet<TileKey>();
        foreach (var k in changed)
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    dirty.Add(new TileKey(k.X + dx, k.Z + dz));

        // What stands over the ground, filed in a grid, so a tile's slabs are not each tested against
        // every box round them: anything whose underside is a metre and a half up or more.
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
            foreach (var b in list)
            {
                bool open = SteamAudioScene.IsOpenGround(b, SteamAudioScene.WorldExtents(b), CoveredAt);
                (open ? ground : rest).Add(b);
                signature += Hash(b) * (open ? 31 : 1);
            }
            if (_current.TryGetValue(k, out var had) && had.Signature == signature) { had.Raw = raw[k]; continue; }
            if (had != null) _dead.Add(had);
            _current[k] = NewPiece(k, signature, raw[k], ground, rest, list);
            built++;
        }
        foreach (var k in new List<TileKey>(_current.Keys))
            if (!byTile.ContainsKey(k)) { _dead.Add(_current[k]); _current.Remove(k); }

        LastBuilt = built;
        TotalBuilt += built;
        LastUpdateMs = clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The idle pair of top scenes brought up to the tiles and committed, as the scenes to hand over: the
    /// full scene and the listener's. Only call once the pair handed over last time is the one in use.
    /// </summary>
    public (SteamAudioScene Full, SteamAudioScene Listener) Assemble()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int b = _active < 0 ? 0 : 1 - _active;
        for (int i = _dead.Count - 1; i >= 0; i--)
        {
            var p = _dead[i];
            if (p.InPair[b])
            {
                Remove(p.GroundIn[b], _full[b]); Remove(p.RestIn[b], _full[b]); Remove(p.ListenerIn[b], _listener[b]);
                p.InPair[b] = false;
            }
            if (!p.InPair[0] && !p.InPair[1]) { Release(p); _dead.RemoveAt(i); }
        }
        var all = new List<SteamAudioScene.Box>();
        var listenerBoxes = new List<SteamAudioScene.Box>();
        foreach (var p in _current.Values)
        {
            if (!p.InPair[b])
            {
                Add(p.GroundIn[b], _full[b]); Add(p.RestIn[b], _full[b]); Add(p.ListenerIn[b], _listener[b]);
                p.InPair[b] = true;
            }
            all.AddRange(p.Boxes);
            listenerBoxes.AddRange(p.RestBoxes);
        }
        Phonon.iplSceneCommit(_full[b]);
        Phonon.iplSceneCommit(_listener[b]);
        _fullScene[b]!.SetGeometry(all);
        _listenerScene[b]!.SetGeometry(listenerBoxes);
        _active = b;
        LastAssembleMs = clock.Elapsed.TotalMilliseconds;
        return (_fullScene[b]!, _listenerScene[b]!);
    }

    private Piece NewPiece(TileKey k, long signature, long raw, List<SteamAudioScene.Box> ground, List<SteamAudioScene.Box> rest,
                           List<SteamAudioScene.Box> all)
    {
        var p = new Piece { Key = k, Signature = signature, Raw = raw, Boxes = all, RestBoxes = rest,
                            Ground = SubScene(ground), Rest = SubScene(rest) };
        var identity = Matrix(Vector3.Zero, Quaternion.Identity);
        for (int b = 0; b < 2; b++)
        {
            p.GroundIn[b] = Instance(_full[b], p.Ground, identity);
            p.RestIn[b] = Instance(_full[b], p.Rest, identity);
            p.ListenerIn[b] = Instance(_listener[b], p.Rest, identity);
        }
        return p;
    }

    private static IntPtr Instance(IntPtr top, IntPtr sub, Phonon.IPLMatrix4x4 transform)
    {
        if (top == IntPtr.Zero || sub == IntPtr.Zero) return IntPtr.Zero;
        var settings = new Phonon.IPLInstancedMeshSettings { subScene = sub, transform = transform };
        return Phonon.iplInstancedMeshCreate(top, ref settings, out IntPtr instance) == Phonon.IPL_STATUS_SUCCESS ? instance : IntPtr.Zero;
    }

    private static void Add(IntPtr instance, IntPtr top) { if (instance != IntPtr.Zero) Phonon.iplInstancedMeshAdd(instance, top); }
    private static void Remove(IntPtr instance, IntPtr top) { if (instance != IntPtr.Zero) Phonon.iplInstancedMeshRemove(instance, top); }

    private static void Release(Piece p)
    {
        for (int b = 0; b < 2; b++)
        {
            Free(ref p.GroundIn[b]); Free(ref p.RestIn[b]); Free(ref p.ListenerIn[b]);
        }
        if (p.Ground != IntPtr.Zero) Phonon.iplSceneRelease(ref p.Ground);
        if (p.Rest != IntPtr.Zero) Phonon.iplSceneRelease(ref p.Rest);
        static void Free(ref IntPtr instance) { if (instance != IntPtr.Zero) Phonon.iplInstancedMeshRelease(ref instance); }
    }

    /// <summary>
    /// A pose in Steam Audio's frame, row-major with the translation in the last column. Steam Audio's z
    /// runs the other way from the game's (Phonon.World), so with F the mirror in z the transform is
    /// F·R·F for the turn and F·c for the place; a sub-scene's vertices are already mirrored. Every tile
    /// sits at the identity today; a moving thing (geometry stage 1) will not.
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

    /// <summary>Lets go of everything. Only once nothing traces either pair.</summary>
    public void Dispose()
    {
        foreach (var p in _current.Values) Release(p);
        foreach (var p in _dead) Release(p);
        _current.Clear(); _dead.Clear();
        for (int b = 0; b < 2; b++)
        {
            if (_full[b] != IntPtr.Zero) Phonon.iplSceneRelease(ref _full[b]);
            if (_listener[b] != IntPtr.Zero) Phonon.iplSceneRelease(ref _listener[b]);
        }
    }
}
