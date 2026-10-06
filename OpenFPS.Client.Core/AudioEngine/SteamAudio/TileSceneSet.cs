using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The Steam Audio geometry as a tile owns it (docs/WORLD_STREAMING.md, docs/GEOMETRY.md 2.4): each tile
/// is a sub-scene built once and kept while its solids stay the same, and the scenes the simulators trace
/// are top scenes of instances of them. Needs Embree: Steam Audio's default tracer walks instances one
/// by one and traces them twenty times slower (docs/GEOMETRY.md 6.4); without Embree the worker builds
/// whole scenes as before.
///
/// <para>The triangles come from the acoustic triangle store (<see cref="AcousticGeometry"/>, geometry
/// stage 1): a tile's piece is its sub-scenes' source, in the tile's own frame, and the instance places
/// it at the tile's corner. The same store is what the enclosure survey casts its rays against, so the
/// two can never disagree about what stands where. A box wider than a tile (the ground under a whole map)
/// is a piece of its own at the world's origin.</para>
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

    /// <summary>The acoustic triangle store the sub-scenes are made from.</summary>
    public AcousticGeometry Store { get; }

    /// <summary>The store's world as the last <see cref="Update"/> left it.</summary>
    public TriangleWorld Geometry => Store.World;

    /// <summary>One version of one tile's geometry: its sub-scenes, and their instances in each pair.</summary>
    private sealed class Piece
    {
        public TileKey Key;
        public ulong Signature;
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
    /// <summary>Of <see cref="LastUpdateMs"/>, building the triangle store's changed tiles.</summary>
    public double LastStoreMs { get; private set; }
    /// <summary>True when Embree made both pairs of top scenes.</summary>
    public bool IsValid => _full[0] != IntPtr.Zero && _full[1] != IntPtr.Zero && _listener[0] != IntPtr.Zero && _listener[1] != IntPtr.Zero;

    public TileSceneSet(IntPtr context, float tileMetres)
    {
        _context = context;
        TileMetres = tileMetres > 0f ? tileMetres : 250f;
        Store = new AcousticGeometry(TileMetres);
        for (int b = 0; b < 2; b++)
        {
            _full[b] = SteamAudioScene.CreateScene(context);
            _listener[b] = SteamAudioScene.CreateScene(context);
            _fullScene[b] = SteamAudioScene.Borrowed(context, _full[b]);
            _listenerScene[b] = SteamAudioScene.Borrowed(context, _listener[b]);
        }
    }

    /// <summary>
    /// Brings the tiles up to <paramref name="boxes"/>: the store builds the tiles whose solids changed
    /// (their open ground included), a tile whose piece is new gets new sub-scenes (and their instances,
    /// made now while nothing traces them), a tile with nothing left goes. Nothing changes in either pair
    /// of top scenes until <see cref="Assemble"/>.
    /// </summary>
    public void Update(IReadOnlyList<SteamAudioScene.Box> boxes, ISet<int>? leaves = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var world = Store.Update(boxes, leaves);
        LastStoreMs = clock.Elapsed.TotalMilliseconds;

        int built = 0;
        var seen = new HashSet<TileKey>();
        foreach (var inst in world.Instances)
        {
            var piece = inst.Piece;
            seen.Add(piece.Key);
            if (_current.TryGetValue(piece.Key, out var had) && had.Signature == piece.Signature) continue;
            if (had != null) _dead.Add(had);
            _current[piece.Key] = NewPiece(piece);
            built++;
        }
        foreach (var k in new List<TileKey>(_current.Keys))
            if (!seen.Contains(k)) { _dead.Add(_current[k]); _current.Remove(k); }

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

    private Piece NewPiece(GeometryPiece piece)
    {
        var p = new Piece { Key = piece.Key, Signature = piece.Signature,
                            Ground = SubScene(piece, openGround: true), Rest = SubScene(piece, openGround: false) };
        // The boxes the scene holds, for the reflection search and the bounds (SteamAudioScene.SetGeometry).
        for (int s = 0; s < piece.SolidCount; s++)
        {
            ref readonly var rec = ref piece.Solid(s);
            var surface = piece.Surfaces[rec.Surface];
            var box = new SteamAudioScene.Box(rec.PlacedAt, rec.BoxSize, rec.BoxRotation, surface.Material,
                                              surface.Construction.Build, rec.Owner);
            p.Boxes.Add(box);
            if (!surface.Is(SurfaceFlags.OpenGround)) p.RestBoxes.Add(box);
        }
        var at = Matrix(piece.Origin, Quaternion.Identity);
        for (int b = 0; b < 2; b++)
        {
            p.GroundIn[b] = Instance(_full[b], p.Ground, at);
            p.RestIn[b] = Instance(_full[b], p.Rest, at);
            p.ListenerIn[b] = Instance(_listener[b], p.Rest, at);
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
    /// F·R·F for the turn and F·c for the place; a sub-scene's vertices are already mirrored. A tile sits
    /// at its corner with no turn.
    /// </summary>
    internal static unsafe Phonon.IPLMatrix4x4 Matrix(Vector3 at, Quaternion rotation)
    {
        if (rotation.LengthSquared() < 1e-6f) rotation = Quaternion.Identity;
        var cols = new[] { Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation), Vector3.Transform(Vector3.UnitZ, rotation) };
        float mz = Phonon.MirrorZ ? -1f : 1f;
        float[] s = { 1f, 1f, mz };
        var m = new Phonon.IPLMatrix4x4();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                float r = i switch { 0 => cols[j].X, 1 => cols[j].Y, _ => cols[j].Z };
                m.elements[i * 4 + j] = s[i] * s[j] * r;
            }
        var w = Phonon.World(at);
        m.elements[3] = w.x; m.elements[7] = w.y; m.elements[11] = w.z;
        m.elements[15] = 1f;
        return m;
    }

    private IntPtr SubScene(GeometryPiece piece, bool openGround)
    {
        IntPtr sub = SteamAudioScene.CreateScene(_context);
        if (sub == IntPtr.Zero) return IntPtr.Zero;
        IntPtr mesh = SteamAudioScene.AddPieceMesh(sub, piece, openGround);
        if (mesh == IntPtr.Zero) { Phonon.iplSceneRelease(ref sub); return IntPtr.Zero; }
        Phonon.iplSceneCommit(sub);
        // The scene holds the mesh; the handle is not needed past the build.
        Phonon.iplStaticMeshRelease(ref mesh);
        return sub;
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
