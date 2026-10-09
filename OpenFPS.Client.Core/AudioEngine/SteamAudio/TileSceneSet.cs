using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Geometry;

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// The Steam Audio geometry as a tile owns it (docs/WORLD_STREAMING.md, docs/GEOMETRY.md 2.4): each tile
/// is a sub-scene kept while its solids stay the same, and the simulators trace top scenes of instances
/// of them. Needs Embree: the default tracer traces instances twenty times slower (docs/GEOMETRY.md 6.4),
/// and without Embree the worker builds whole scenes.
///
/// <para>The triangles come from the acoustic store (<see cref="AcousticGeometry"/>), which the enclosure
/// survey also casts against, so the two never disagree. Per tile two sub-scenes, open ground and the
/// rest; the listener's scene instances only the rest (SteamAudioScene.WithoutOpenGround). A door leaf
/// stays in its tile, so a swinging door rebuilds one tile.</para>
///
/// <para>Two pairs of top scenes, used in turn: a change (<see cref="Update"/>, <see cref="Assemble"/>)
/// goes into the idle pair, which is committed and handed over; Steam Audio forbids committing a scene
/// being traced. Instances are made when their sub-scene is new: making one of a sub-scene being traced
/// waits for the trace (up to two thirds of a second).</para>
///
/// <para>Steam Audio 4.8's Embree scenes (read from its source, 2026-10-06; docs/WORLD_STREAMING.md):</para>
/// <list type="bullet">
/// <item>An instance is in its top scene's Embree scene, enabled, from the moment it is made, and Embree
/// will not build a scene holding an enabled geometry never committed, silently (the first door that
/// swung handed over a pair that traced empty). So each instance is disabled at once (added and taken
/// out again) and enabled only by being added for a commit.</item>
/// <item>Releasing an instance never detaches its Embree geometry, so the next instance given its id is
/// silently missing (the third swing of a door lost its tile). So instances live, disabled, as long as
/// their top scene; a pair holding more than <see cref="RecycleShare"/> of replaced geometry is made
/// afresh when next idle.</item>
/// </list>
///
/// <para>Not thread-safe: one Update and Assemble at a time. Assemble only once the pair last handed over
/// is the only one in use (AsyncAcousticWorker waits for TracedReverbSet.Reconfiguring to clear).</para>
/// </summary>
internal sealed class TileSceneSet : IDisposable
{
    private readonly IntPtr _context;
    public float TileMetres { get; }

    /// <summary>The acoustic triangle store the sub-scenes are made from.</summary>
    public AcousticGeometry Store { get; }

    /// <summary>The store's world as the last <see cref="Update"/> left it.</summary>
    public TriangleWorld Geometry => Store.World;

    /// <summary>
    /// How much replaced geometry a pair may hold, as a share of the triangles it traces, before it is
    /// made afresh. Held tiles cost only memory; making a pair afresh can wait on the other pair's traces.
    /// 0 makes it afresh whenever it holds anything replaced (the lab and the tests).
    /// </summary>
    internal static double RecycleShare { get; set; } = 1.0;

    /// <summary>One pair of top scenes: the full scene and the listener's.</summary>
    private sealed class Pair
    {
        public IntPtr Full, Listener;
        public SteamAudioScene FullScene = null!, ListenerScene = null!;
    }

    /// <summary>One version of one tile's geometry: its sub-scenes, and their instances in each pair.</summary>
    private sealed class Piece
    {
        public ulong Signature;
        public int Triangles;
        public Vector3 Origin;
        public IntPtr Ground, Rest;
        public List<SteamAudioScene.Box> Boxes = new(), RestBoxes = new();
        public readonly IntPtr[] GroundIn = new IntPtr[2], RestIn = new IntPtr[2], ListenerIn = new IntPtr[2];
        /// <summary>Added to that pair (enabled, and in its list for the next commit).</summary>
        public readonly bool[] InPair = new bool[2];
        public bool Holds(int b) => GroundIn[b] != IntPtr.Zero || RestIn[b] != IntPtr.Zero || ListenerIn[b] != IntPtr.Zero;
    }

    private readonly Dictionary<TileKey, Piece> _current = new();
    /// <summary>Pieces replaced or gone whose instances a pair still holds; let go once neither does.</summary>
    private readonly List<Piece> _dead = new();
    private readonly Pair[] _pairs = new Pair[2];
    private int _active = -1;

    /// <summary>Sub-scenes built by the last <see cref="Update"/>, and in all; tiles held.</summary>
    public int LastBuilt { get; private set; }
    public int TotalBuilt { get; private set; }
    public int TileCount => _current.Count;
    public double LastUpdateMs { get; private set; }
    public double LastAssembleMs { get; private set; }
    /// <summary>Of <see cref="LastUpdateMs"/>, building the triangle store's changed tiles.</summary>
    public double LastStoreMs { get; private set; }
    /// <summary>Pairs made afresh so far, and how long the last took, milliseconds (part of an Assemble).</summary>
    public int Recycles { get; private set; }
    public double LastRecycleMs { get; private set; }
    /// <summary>Assembles so far: the number of pairs handed over.</summary>
    public int Assembles { get; private set; }
    /// <summary>The pair the last <see cref="Assemble"/> handed over, -1 before the first.</summary>
    public int ActivePair => _active;
    /// <summary>True when Embree made both pairs of top scenes.</summary>
    public bool IsValid => _pairs[0].Full != IntPtr.Zero && _pairs[1].Full != IntPtr.Zero
                           && _pairs[0].Listener != IntPtr.Zero && _pairs[1].Listener != IntPtr.Zero;

    public TileSceneSet(IntPtr context, float tileMetres)
    {
        _context = context;
        TileMetres = tileMetres > 0f ? tileMetres : 250f;
        Store = new AcousticGeometry(TileMetres);
        for (int b = 0; b < 2; b++) _pairs[b] = NewPair();
    }

    private Pair NewPair()
    {
        var p = new Pair { Full = SteamAudioScene.CreateScene(_context), Listener = SteamAudioScene.CreateScene(_context) };
        p.FullScene = SteamAudioScene.Borrowed(_context, p.Full);
        p.ListenerScene = SteamAudioScene.Borrowed(_context, p.Listener);
        return p;
    }

    /// <summary>
    /// Brings the tiles up to <paramref name="boxes"/>: the store builds the tiles whose solids changed
    /// (their open ground included), a tile whose piece is new gets new sub-scenes (and their instances,
    /// made now while nothing traces them, and disabled), a tile with nothing left goes. Nothing either
    /// pair traces changes until <see cref="Assemble"/>.
    /// </summary>
    public void Update(IReadOnlyList<SteamAudioScene.Box> boxes, ISet<int>? leaves = null,
                       IReadOnlyList<OpenFPS.Common.Geometry.SolidSpec>? terrains = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var world = Store.Update(boxes, leaves, terrains);
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
        if (ShouldRecycle(b)) Recycle(b);
        var pair = _pairs[b];
        // The tiles that went: out of this pair (disabled), their instances kept while the pair lives.
        foreach (var p in _dead)
            if (p.InPair[b])
            {
                Remove(p.GroundIn[b], pair.Full); Remove(p.RestIn[b], pair.Full); Remove(p.ListenerIn[b], pair.Listener);
                p.InPair[b] = false;
            }
        var all = new List<SteamAudioScene.Box>();
        var listenerBoxes = new List<SteamAudioScene.Box>();
        foreach (var p in _current.Values)
        {
            if (!p.InPair[b])
            {
                Add(p.GroundIn[b], pair.Full); Add(p.RestIn[b], pair.Full); Add(p.ListenerIn[b], pair.Listener);
                p.InPair[b] = true;
            }
            all.AddRange(p.Boxes);
            listenerBoxes.AddRange(p.RestBoxes);
        }
        Phonon.iplSceneCommit(pair.Full);
        Phonon.iplSceneCommit(pair.Listener);
        pair.FullScene.SetGeometry(all);
        pair.ListenerScene.SetGeometry(listenerBoxes);
        _active = b;
        Assembles++;
        LastAssembleMs = clock.Elapsed.TotalMilliseconds;
        return (pair.FullScene, pair.ListenerScene);
    }

    /// <summary>True when pair <paramref name="b"/> holds more replaced geometry than it may.</summary>
    private bool ShouldRecycle(int b)
    {
        long held = 0, live = 0;
        foreach (var p in _dead) if (p.Holds(b)) held += p.Triangles;
        if (held == 0) return false;
        foreach (var p in _current.Values) live += p.Triangles;
        return held > RecycleShare * live;
    }

    /// <summary>
    /// Idle pair <paramref name="b"/> made afresh: everything taken out and committed, the instances
    /// released while their top scenes still live (released after, an instance keeps its Embree geometry
    /// and the sub-scene for good), the old top scenes released, and new ones made with an instance of
    /// every tile in use. Replaced tiles neither pair holds any more are let go.
    /// </summary>
    private void Recycle(int b)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var old = _pairs[b];
        foreach (var p in Pieces())
            if (p.InPair[b])
            {
                Remove(p.GroundIn[b], old.Full); Remove(p.RestIn[b], old.Full); Remove(p.ListenerIn[b], old.Listener);
                p.InPair[b] = false;
            }
        Phonon.iplSceneCommit(old.Full);
        Phonon.iplSceneCommit(old.Listener);
        foreach (var p in Pieces()) FreeInstances(p, b);
        ReleasePair(old);

        var fresh = _pairs[b] = NewPair();
        foreach (var p in _current.Values) MakeInstances(p, b, fresh, disable: false);
        for (int i = _dead.Count - 1; i >= 0; i--)
            if (!_dead[i].Holds(0) && !_dead[i].Holds(1)) { ReleaseSubScenes(_dead[i]); _dead.RemoveAt(i); }
        Recycles++;
        LastRecycleMs = clock.Elapsed.TotalMilliseconds;
    }

    private IEnumerable<Piece> Pieces()
    {
        foreach (var p in _current.Values) yield return p;
        foreach (var p in _dead) yield return p;
    }

    private Piece NewPiece(GeometryPiece piece)
    {
        var p = new Piece { Signature = piece.Signature, Triangles = piece.TriangleCount,
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
        p.Origin = piece.Origin;
        // Made into both pairs now, while nothing traces the sub-scenes, and disabled until added: one of
        // the pairs is being traced, and the other may never take this piece (it is replaced first).
        for (int b = 0; b < 2; b++) MakeInstances(p, b, _pairs[b], disable: true);
        return p;
    }

    /// <summary>
    /// The piece's instances in a pair. Embree has each in the top scene from here on, enabled; unless it
    /// is about to be added for a commit, it is disabled at once (added and taken out again: Steam Audio's
    /// only way to disable one), or the pair's next commit fails on it.
    /// </summary>
    private static void MakeInstances(Piece p, int b, Pair pair, bool disable)
    {
        var at = Matrix(p.Origin, Quaternion.Identity);
        p.GroundIn[b] = Instance(pair.Full, p.Ground, at);
        p.RestIn[b] = Instance(pair.Full, p.Rest, at);
        p.ListenerIn[b] = Instance(pair.Listener, p.Rest, at);
        if (!disable) return;
        Add(p.GroundIn[b], pair.Full); Remove(p.GroundIn[b], pair.Full);
        Add(p.RestIn[b], pair.Full); Remove(p.RestIn[b], pair.Full);
        Add(p.ListenerIn[b], pair.Listener); Remove(p.ListenerIn[b], pair.Listener);
    }

    private static IntPtr Instance(IntPtr top, IntPtr sub, Phonon.IPLMatrix4x4 transform)
    {
        if (top == IntPtr.Zero || sub == IntPtr.Zero) return IntPtr.Zero;
        var settings = new Phonon.IPLInstancedMeshSettings { subScene = sub, transform = transform };
        return Phonon.iplInstancedMeshCreate(top, ref settings, out IntPtr instance) == Phonon.IPL_STATUS_SUCCESS ? instance : IntPtr.Zero;
    }

    private static void Add(IntPtr instance, IntPtr top) { if (instance != IntPtr.Zero) Phonon.iplInstancedMeshAdd(instance, top); }
    private static void Remove(IntPtr instance, IntPtr top) { if (instance != IntPtr.Zero) Phonon.iplInstancedMeshRemove(instance, top); }

    private static void FreeInstances(Piece p, int b)
    {
        Free(ref p.GroundIn[b]); Free(ref p.RestIn[b]); Free(ref p.ListenerIn[b]);
        static void Free(ref IntPtr instance) { if (instance != IntPtr.Zero) Phonon.iplInstancedMeshRelease(ref instance); instance = IntPtr.Zero; }
    }

    private static void ReleaseSubScenes(Piece p)
    {
        if (p.Ground != IntPtr.Zero) Phonon.iplSceneRelease(ref p.Ground);
        if (p.Rest != IntPtr.Zero) Phonon.iplSceneRelease(ref p.Rest);
        p.Ground = p.Rest = IntPtr.Zero;
    }

    /// <summary>A pair's top scenes released. Their instances must already be. The wrappers handed out
    /// for them stop being built, so a holder that kept one gets nothing rather than a freed handle.</summary>
    private static void ReleasePair(Pair pair)
    {
        pair.FullScene.Revoke();
        pair.ListenerScene.Revoke();
        if (pair.Full != IntPtr.Zero) Phonon.iplSceneRelease(ref pair.Full);
        if (pair.Listener != IntPtr.Zero) Phonon.iplSceneRelease(ref pair.Listener);
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

    /// <summary>Which pair is in use and what each holds. Diagnostic.</summary>
    public string Describe()
    {
        int h0 = 0, h1 = 0;
        foreach (var p in _dead) { if (p.Holds(0)) h0++; if (p.Holds(1)) h1++; }
        return $"pair {_active} in use after {Assembles} assembl{(Assembles == 1 ? "y" : "ies")}; {_current.Count} tiles; "
             + $"replaced tiles held {h0}/{h1}; {Recycles} pair(s) made afresh" + (Recycles > 0 ? $" (last {LastRecycleMs:F1} ms)" : "");
    }

    /// <summary>Lets go of everything. Only once nothing traces either pair.</summary>
    public void Dispose()
    {
        for (int b = 0; b < 2; b++)
        {
            var pair = _pairs[b];
            foreach (var p in Pieces())
                if (p.InPair[b])
                {
                    Remove(p.GroundIn[b], pair.Full); Remove(p.RestIn[b], pair.Full); Remove(p.ListenerIn[b], pair.Listener);
                    p.InPair[b] = false;
                }
            if (pair.Full != IntPtr.Zero) Phonon.iplSceneCommit(pair.Full);
            if (pair.Listener != IntPtr.Zero) Phonon.iplSceneCommit(pair.Listener);
            foreach (var p in Pieces()) FreeInstances(p, b);
            ReleasePair(pair);
        }
        foreach (var p in Pieces()) ReleaseSubScenes(p);
        _current.Clear(); _dead.Clear();
    }
}
