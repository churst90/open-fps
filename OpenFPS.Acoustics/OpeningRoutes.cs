using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Geometry;

namespace OpenFPS.Common;

/// <summary>
/// How sound gets from one place to another through the openings between them (doorways, open faces,
/// the outdoors), as a graph, and the few shortest routes through it: the way from the street to a
/// corridor by a front door, a stairwell and a doorway, which neither a wall's transmission
/// (<see cref="WallTransmission"/>) nor one edge's diffraction (<see cref="Diffraction"/>) reaches.
///
/// A route is a sequence of openings from the source to the listener, each a rectangle in a wall with
/// what fills it (a shut leaf passes what its construction lets through), crossed where the whole path is
/// shortest. Two things arrive by it, added as energies:
///
/// THE DIFFRACTED RAY. Each opening is a rectangular aperture, and what it passes of a wave from the
/// previous crossing point to the next is the Fresnel–Kirchhoff result for a rectangle: the product
/// over its two axes of ½[(C(ν₂)−C(ν₁))² + (S(ν₂)−S(ν₁))²], C and S the Fresnel integrals, ν the Fresnel
/// parameter of each edge, ν = ±2√(δ/λ) with δ the extra path by that edge (Born &amp; Wolf, Principles of
/// Optics, §8.7.4; ITU-R P.526-15 §4.1 for ν). It is continuous from the lit zone (a wide doorway in
/// plain view passes everything; a small one already costs its low end, which spreads) through the
/// shadow boundary (−6 dB on one edge) into the shadow, where a corner costs more the higher the
/// frequency. Openings in a row multiply, each with its neighbours as its source and receiver — the
/// Epstein–Peterson construction (Epstein &amp; Peterson, Proc. IRE 41, 1953; ITU-R P.526-15 §4.5.1). The
/// spreading is over the route's length.
///
/// THE FIELD OF THE ROOMS BETWEEN. Sound that comes in by an opening fills the room it enters, and that
/// room's reverberant field is what reaches the next opening — not a ray, which round two corners is
/// nearly nothing. This is the transmission-room equation of building acoustics (ISO 12354-1 §4;
/// Kuttruff, Room Acoustics, 6th ed., §5.1 and §9.6): power W into a room of absorption area A makes a
/// diffuse field whose intensity on any surface is W/A, so an opening of area S with transmission τ passes
/// W·S·τ/A on; a plane wave of intensity I meets an opening with I·S·cosθ; and an opening fed by a diffuse
/// field radiates like a Lambert surface, W·cosθ/(πr²). A room's absorption area is its surveyed surfaces'
/// Σ S·α per band, every open face counted at α = 1 and every opening's leak at its own S·τ.
///
/// Only the rooms between the two ends are counted so. The source's and the listener's own rooms are the
/// reverb's (their room buses); counted here too they would be heard twice, the second time as a dry
/// voice from the doorway. Both ends are treated alike, so a route gives the same answer both ways round.
/// </summary>
public sealed class OpeningRoutes
{
    /// <summary>A box of the scene, as the occlusion model sees it. <paramref name="IsLeaf"/> marks a door
    /// leaf: it belongs in an opening, not in a wall, so it is never taken for a jamb.</summary>
    public readonly record struct Solid(Vector3 Center, Vector3 Size, Quaternion Rotation, string Material,
                                        WallBuild Build = default, bool IsLeaf = false);

    /// <summary>
    /// An opening as the map declares it. <paramref name="Rotation"/> is the doorway's frame when known
    /// (a door's leaf, shut: local X across, Y up, the thin axis through) and the default quaternion when
    /// not; <paramref name="Size"/> the leaf's extent when known, otherwise zero; <paramref name="Aperture"/>
    /// the authored width, otherwise zero. The geometry decides the rest.
    /// </summary>
    public readonly record struct Declared(int Id, string Kind, Vector3 Centre, Quaternion Rotation, Vector3 Size,
                                           float Aperture, int RegionA, int RegionB);

    /// <summary>One opening as the geometry has it.</summary>
    public sealed class Opening
    {
        public int Id;
        public string Kind = "";
        public int RegionA, RegionB;
        /// <summary>The two places it joins as graph nodes: a region id, or <see cref="Outside"/>.</summary>
        public int NodeA, NodeB;
        public Vector3 Centre, Normal, Across, Up;
        public float HalfWidth, HalfHeight, HalfDepth;
        public float Area => 4f * HalfWidth * HalfHeight;
        /// <summary>The energy fraction what stands in it lets through, per band: 1 for a clear doorway,
        /// the leaf's transmission for a shut door, the fraction left uncovered for one ajar.</summary>
        public Vector3 Tau = Vector3.One;
        /// <summary>The scene boxes that stand in it (its leaf). A leg arriving at the opening does not
        /// charge them: <see cref="Tau"/> already has.</summary>
        public int[] Contents = Array.Empty<int>();
        /// <summary>What the geometry disagreed with, or null.</summary>
        public string? Problem;

        public int Other(int node) => node == NodeA ? NodeB : NodeA;

        /// <summary>A copy to fill in again (a tile build's kept opening): every field, the arrays shared.</summary>
        public Opening Copy() => (Opening)MemberwiseClone();
    }

    /// <summary>The node every unbounded place belongs to: the global region and any named patch of
    /// ground with no surfaces. No reverberant field in it relays anything.</summary>
    public const int Outside = int.MinValue;

    /// <summary>How many of the shortest routes are evaluated and summed.</summary>
    public const int RoutesPerQuery = 4;

    /// <summary>Representative frequencies of the mixer's three bands (<see cref="Diffraction.LowBandHz"/>).</summary>
    private static readonly Vector3 BandHz = new(Diffraction.LowBandHz, Diffraction.MidBandHz, Diffraction.HighBandHz);

    private readonly Solid[] _solids;
    private readonly Vector3[] _solidGains;        // amplitude transmission per band
    private readonly Frame[] _frames;
    private readonly ISolidIndex _grid;
    private readonly List<Opening> _openings;
    private readonly Dictionary<int, List<int>> _byNode = new();
    private readonly Dictionary<int, Vector3> _absorption = new();   // node -> Sabine absorption area per band, m²
    private readonly Dictionary<int, int> _nodeOf = new();          // region id -> node
    /// <summary>The door leaves standing in an opening (in its <see cref="Opening.Contents"/>), by box.</summary>
    private bool[] _leafInOpening = Array.Empty<bool>();
    /// <summary>The scene's terrain, which is no box: it buries every edge at or under its surface.</summary>
    private Ground _ground = Ground.None;

    public IReadOnlyList<Opening> Openings => _openings;
    public IReadOnlyList<Solid> Solids => _solids;

    /// <summary>The problems found validating the declared openings against the geometry, one line each.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>What making this graph cost, milliseconds: the index of the boxes, each box's frame and
    /// transmission, deriving the openings from the walls round them, checking their sides, and in all.</summary>
    public readonly record struct BuildCost(double IndexMs, double SolidsMs, double DeriveMs, double SidesMs, double TotalMs);
    public BuildCost BuildTimes { get; private set; }

    /// <summary>A room's Sabine absorption area per band (low, mid, high), m², openings included; false
    /// for the outdoors.</summary>
    public bool TryGetAbsorption(int node, out Vector3 area) => _absorption.TryGetValue(node, out area);

    /// <summary>Which graph node a region belongs to.</summary>
    public int NodeOf(int regionId) => _nodeOf.TryGetValue(regionId, out int n) ? n : Outside;

    private OpeningRoutes(Solid[] solids, ISolidIndex grid, List<Opening> openings, List<string> problems)
    {
        _solids = solids;
        _grid = grid;
        _openings = openings;
        Problems = problems;
        _solidGains = new Vector3[solids.Length];
        _frames = new Frame[solids.Length];
        for (int i = 0; i < solids.Length; i++)
        {
            _frames[i] = FrameOf(solids[i]);
            _solidGains[i] = GainsOf(solids[i]);
        }
    }

    private OpeningRoutes(Solid[] solids, Frame[] frames, Vector3[] gains, ISolidIndex grid, List<Opening> openings, List<string> problems)
    {
        _solids = solids; _frames = frames; _solidGains = gains;
        _grid = grid; _openings = openings; Problems = problems;
    }

    private static Frame FrameOf(in Solid s) => new(s.Center, s.Size * 0.5f, Quaternion.Inverse(Quaternion.Normalize(s.Rotation)));

    private static Vector3 GainsOf(in Solid s)
    {
        var (l, m, h) = WallTransmission.BandGains(s.Material, s.Size, s.Build);
        return new Vector3(l, m, h);
    }

    // ═══ Building the graph ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The graph for one scene: the boxes as they stand (door leaves where they are), the map's places,
    /// and the openings it declares. <paramref name="regionAt"/>, when given, checks that each opening
    /// really has its two places on its two sides. <paramref name="terrains"/> are the scene's tiles of
    /// ground, which are no box.
    /// </summary>
    public static OpeningRoutes Build(IReadOnlyList<Solid> solids, AcousticMap? map, IEnumerable<Declared> declared,
                                      Func<Vector3, int>? regionAt = null, IReadOnlyList<Geometry.SolidSpec>? terrains = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var arr = new Solid[solids.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = solids[i];
        var grid = new SolidGrid(arr);
        double gridMs = clock.Elapsed.TotalMilliseconds;
        var problems = new List<string>();
        var openings = new List<Opening>();
        var model = new OpeningRoutes(arr, grid, openings, problems) { _ground = Ground.Of(terrains) };
        double solidsMs = clock.Elapsed.TotalMilliseconds - gridMs;
        model.Fill(map, declared, regionAt, null, clock, gridMs, solidsMs);
        return model;
    }

    // ═══ Built tile by tile (docs/GEOMETRY.md stage 1) ════════════════════════════════════════════
    //
    // Boxes asked of the triangle store's trees; a tile's boxes keep their frames and transmissions while
    // its piece does, and an opening is derived again only when a tile its walls could be in changed.
    // Routes answer the same as a whole rebuild's (AudioLab --geometry-parity only=routes).

    /// <summary>What a tile-by-tile build keeps from one build to the next. One per map; not shared
    /// between threads (one build at a time, as the worker has it).</summary>
    public sealed class TileCache
    {
        internal readonly Dictionary<Geometry.GeometryPiece, (Solid[] Solids, Frame[] Frames, Vector3[] Gains)> Pieces
            = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<int, CachedOpening> Openings = new();
        /// <summary>Openings derived again by the last build, and kept from the one before.</summary>
        public int Derived { get; internal set; }
        public int Kept { get; internal set; }
    }

    internal sealed class CachedOpening
    {
        public long Declared;
        public Vector3 RegionMin, RegionMax;
        public (TileKey Key, ulong Signature)[] Tiles = Array.Empty<(TileKey, ulong)>();
        public Opening? Template;
        public (Geometry.GeometryPiece Piece, int Solid)[] Contents = Array.Empty<(Geometry.GeometryPiece, int)>();
    }

    /// <summary>
    /// The graph for the scene the acoustic triangle store holds, as
    /// <see cref="Build(IReadOnlyList{Solid}, AcousticMap?, IEnumerable{Declared}, Func{Vector3, int}?)"/>
    /// makes it from a box list, keeping what did not change in <paramref name="cache"/>.
    /// </summary>
    public static OpeningRoutes Build(Geometry.TriangleWorld scene, AcousticMap? map, IEnumerable<Declared> declared,
                                      Func<Vector3, int>? regionAt, TileCache cache)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int count = 0;
        var offsets = new int[scene.InstanceCount];
        for (int i = 0; i < scene.InstanceCount; i++) { offsets[i] = count; count += scene.Instance(i).Piece.SolidCount; }
        var solids = new Solid[count];
        var frames = new Frame[count];
        var gains = new Vector3[count];
        var instanceOf = new Dictionary<Geometry.GeometryPiece, int>(ReferenceEqualityComparer.Instance);
        var live = new HashSet<Geometry.GeometryPiece>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < scene.InstanceCount; i++)
        {
            ref readonly var inst = ref scene.Instance(i);
            var piece = inst.Piece;
            bool placed = !inst.Rotated && inst.Position == piece.Origin;
            if (placed) instanceOf[piece] = i;
            if (!placed || !cache.Pieces.TryGetValue(piece, out var kept))
            {
                kept = (new Solid[piece.SolidCount], new Frame[piece.SolidCount], new Vector3[piece.SolidCount]);
                for (int s = 0; s < piece.SolidCount; s++)
                {
                    var (centre, size, rotation) = scene.BoxOf(new Geometry.SolidRef(i, s));
                    var surface = piece.Surfaces[piece.Solid(s).Surface];
                    var solid = new Solid(centre, size, rotation, surface.Material, surface.Construction.Build,
                                          surface.Is(Geometry.SurfaceFlags.DoorLeaf));
                    kept.Solids[s] = solid;
                    kept.Frames[s] = FrameOf(solid);
                    kept.Gains[s] = GainsOf(solid);
                }
                if (placed) cache.Pieces[piece] = kept;
            }
            if (placed) live.Add(piece);
            Array.Copy(kept.Solids, 0, solids, offsets[i], piece.SolidCount);
            Array.Copy(kept.Frames, 0, frames, offsets[i], piece.SolidCount);
            Array.Copy(kept.Gains, 0, gains, offsets[i], piece.SolidCount);
        }
        foreach (var p in new List<Geometry.GeometryPiece>(cache.Pieces.Keys))
            if (!live.Contains(p)) cache.Pieces.Remove(p);
        double solidsMs = clock.Elapsed.TotalMilliseconds;

        var index = new TileIndex(scene, offsets);
        var model = new OpeningRoutes(solids, frames, gains, index, new List<Opening>(), new List<string>()) { _ground = Ground.Of(scene) };
        var seen = new HashSet<int>();
        int derived = 0, keptCount = 0;
        Opening? Derive(Declared d)
        {
            seen.Add(d.Id);
            long key = DeclaredHash(d, map);
            if (cache.Openings.TryGetValue(d.Id, out var c) && c.Declared == key && c.Template != null
                && SameTiles(scene, c.RegionMin, c.RegionMax, c.Tiles))
            {
                var contents = new int[c.Contents.Length];
                bool ok = true;
                for (int k = 0; k < contents.Length && ok; k++)
                {
                    if (instanceOf.TryGetValue(c.Contents[k].Piece, out int ii)) contents[k] = offsets[ii] + c.Contents[k].Solid;
                    else ok = false;
                }
                if (ok)
                {
                    keptCount++;
                    var o = c.Template.Copy();
                    o.Contents = contents;
                    return o;
                }
            }
            derived++;
            var fresh = model.Derive(d, map);
            if (fresh == null) { cache.Openings.Remove(d.Id); return null; }
            // What it was derived from: every tile whose boxes the derivation could have met.
            float reach = MathF.Max(MathF.Max(fresh.HalfWidth, fresh.HalfHeight), fresh.HalfDepth + 0.05f);
            reach = MathF.Max(reach, MathF.Max(d.Size.X, MathF.Max(d.Size.Y, MathF.Max(d.Size.Z, d.Aperture))));
            reach += FrameSearchMetres + 1f + FaceReach(d, map);
            var lo = Vector3.Min(fresh.Centre, d.Centre) - new Vector3(reach);
            var hi = Vector3.Max(fresh.Centre, d.Centre) + new Vector3(reach);
            var stable = new (Geometry.GeometryPiece, int)[fresh.Contents.Length];
            for (int k = 0; k < stable.Length; k++)
            {
                int ii = InstanceAt(offsets, fresh.Contents[k]);
                stable[k] = (scene.Instance(ii).Piece, fresh.Contents[k] - offsets[ii]);
            }
            cache.Openings[d.Id] = new CachedOpening
            {
                Declared = key, RegionMin = lo, RegionMax = hi, Tiles = TilesIn(scene, lo, hi),
                Template = fresh.Copy(), Contents = stable,
            };
            return fresh;
        }
        model.Fill(map, declared, regionAt, Derive, clock, 0, solidsMs);
        foreach (int id in new List<int>(cache.Openings.Keys))
            if (!seen.Contains(id)) cache.Openings.Remove(id);
        cache.Derived = derived;
        cache.Kept = keptCount;
        return model;
    }

    /// <summary>How far round a doorway with no frame of its own its room's face reaches (FaceOfRoom).</summary>
    private static float FaceReach(in Declared d, AcousticMap? map)
    {
        if (d.Rotation != default || map == null) return 0f;
        float r = 0f;
        foreach (int id in new[] { d.RegionA, d.RegionB })
            if (map.Regions.TryGetValue(id, out var region))
                r = MathF.Max(r, RoomAcoustics.Reach(region));
        return r;
    }

    /// <summary>Everything an opening's derivation reads apart from the boxes: its declaration, and for
    /// one with no frame of its own the places it sits on the face of.</summary>
    private static long DeclaredHash(in Declared d, AcousticMap? map)
    {
        var h = new HashCode();
        h.Add(d.Id); h.Add(d.Kind); h.Add(d.Centre); h.Add(d.Rotation); h.Add(d.Size); h.Add(d.Aperture); h.Add(d.RegionA); h.Add(d.RegionB);
        if (d.Rotation == default && map != null)
            foreach (int r in new[] { d.RegionA, d.RegionB })
            {
                if (map.Regions.TryGetValue(r, out var region))
                {
                    h.Add(region.RoomSize); h.Add(RoomAcoustics.OpenFaceCount(region));
                    h.Add(RoomAcoustics.Volume(region)); h.Add(region.Extent);
                }
                if (map.RegionPositions.TryGetValue(r, out var c)) h.Add(c);
                if (map.RegionRotations.TryGetValue(r, out var q)) h.Add(q);
            }
        return h.ToHashCode();
    }

    /// <summary>The tiles (their keys and signatures) whose pieces stand within a box.</summary>
    private static (TileKey, ulong)[] TilesIn(Geometry.TriangleWorld scene, Vector3 lo, Vector3 hi)
    {
        var list = new List<(TileKey, ulong)>();
        foreach (var inst in scene.Instances)
            if (inst.Max.X >= lo.X && inst.Min.X <= hi.X && inst.Max.Y >= lo.Y && inst.Min.Y <= hi.Y && inst.Max.Z >= lo.Z && inst.Min.Z <= hi.Z)
                list.Add((inst.Piece.Key, inst.Piece.Signature));
        list.Sort((a, b) => a.Item1.X != b.Item1.X ? a.Item1.X.CompareTo(b.Item1.X) : a.Item1.Z.CompareTo(b.Item1.Z));
        return list.ToArray();
    }

    private static bool SameTiles(Geometry.TriangleWorld scene, Vector3 lo, Vector3 hi, (TileKey, ulong)[] had)
    {
        var now = TilesIn(scene, lo, hi);
        if (now.Length != had.Length) return false;
        for (int i = 0; i < now.Length; i++) if (now[i] != had[i]) return false;
        return true;
    }

    private static int InstanceAt(int[] offsets, int index)
    {
        int i = Array.BinarySearch(offsets, index);
        if (i >= 0)
        {
            while (i + 1 < offsets.Length && offsets[i + 1] == index) i++;   // pieces with no solids share an offset
            return i;
        }
        return ~i - 1;
    }

    /// <summary>The boxes near a segment, asked of the acoustic store's trees (geometry stage 1).</summary>
    private sealed class TileIndex : ISolidIndex
    {
        private readonly Geometry.TriangleWorld _scene;
        private readonly int[] _offsets;
        [ThreadStatic] private static List<Geometry.SolidRef>? _refs;

        public TileIndex(Geometry.TriangleWorld scene, int[] offsets) { _scene = scene; _offsets = offsets; }

        public void Along(Vector3 a, Vector3 b, List<int> into)
        {
            var refs = _refs ??= new List<Geometry.SolidRef>(64);
            refs.Clear(); into.Clear();
            var all = new Geometry.AcceptAll();
            _scene.Along(a, b, IndexMargin, Geometry.GeometryLayers.All, ref all, refs);
            Gather(refs, into);
        }

        public void Column(Vector3 a, Vector3 b, float fromY, List<int> into)
        {
            var refs = _refs ??= new List<Geometry.SolidRef>(64);
            refs.Clear(); into.Clear();
            var all = new Geometry.AcceptAll();
            _scene.Column(a, b, fromY, IndexMargin, Geometry.GeometryLayers.All, ref all, refs);
            Gather(refs, into);
        }

        // In index order: the order the boxes are listed in, as a scan of all of them would meet them.
        private void Gather(List<Geometry.SolidRef> refs, List<int> into)
        {
            foreach (var r in refs) into.Add(_offsets[r.Instance] + r.Solid);
            into.Sort();
        }
    }

    /// <summary>For the parity harness only: the box grid hands its boxes back in reverse walk order, to
    /// measure how far the answers depend on the order boxes are met in. Never set in the game.</summary>
    public static bool ReverseGridOrderForParity;

    /// <summary>For the lab (--barrier-sweep) and the tests: the barrier model as it was before 2026-10-11, the
    /// ways round clear of everything and none through anything. Per thread, so a test that sets it moves no
    /// other test. Never set in the game.</summary>
    [ThreadStatic] public static bool OnlyClearWaysRoundForLab;

    /// <summary>For the parity harness only: told every box the over-the-top search crosses.</summary>
    public static Action<string>? DebugOverTheTop;

    /// <summary>How much bigger than a box its bounds are taken to be when finding the boxes near a
    /// segment, metres: a route is checked against boxes grown by its joint, less than this.</summary>
    private const float IndexMargin = 0.1f;

    /// <summary>What finds the boxes a segment, or the vertical plane over one, could meet.</summary>
    private interface ISolidIndex
    {
        void Along(Vector3 a, Vector3 b, List<int> into);
        void Column(Vector3 a, Vector3 b, float fromY, List<int> into);
    }

    /// <summary>
    /// The places, the openings and the graph between them, for a model whose boxes are in place.
    /// <paramref name="derive"/>, when given, stands in for <see cref="Derive"/> (the tile build's cache).
    /// </summary>
    private void Fill(AcousticMap? map, IEnumerable<Declared> declared, Func<Vector3, int>? regionAt,
                      Func<Declared, Opening?>? derive, System.Diagnostics.Stopwatch clock, double gridMs, double solidsMs)
    {
        var model = this;
        var openings = _openings;
        var problems = (List<string>)Problems;
        double deriveMs = 0, sidesMs = 0;
        var faceAbsorption = new Dictionary<int, Vector3>();
        if (map != null)
        {
            foreach (var (id, region) in map.Regions)
            {
                if (id == AcousticConstants.GlobalRegionId || id == map.GlobalEnvironmentId) continue;
                if (region.RoomSize.X <= 0f || RoomAcoustics.OpenFaceCount(region) == 6) continue;
                model._nodeOf[id] = id;
                // Its flood's lining where it flooded, else its box's faces; an open face absorbs all its area.
                faceAbsorption[id] = RoomAcoustics.AbsorptionArea(region);
            }
        }

        // ── The openings ────────────────────────────────────────────────────────────────────────
        foreach (var d in declared)
        {
            double t0 = clock.Elapsed.TotalMilliseconds;
            var o = derive != null ? derive(d) : model.Derive(d, map);
            deriveMs += clock.Elapsed.TotalMilliseconds - t0;
            if (o == null) continue;
            o.NodeA = model.NodeOf(o.RegionA);
            o.NodeB = model.NodeOf(o.RegionB);
            if (o.NodeA == o.NodeB) continue;              // a doorway between two named bits of street
            t0 = clock.Elapsed.TotalMilliseconds;
            if (regionAt != null) model.CheckSides(o, regionAt);
            sidesMs += clock.Elapsed.TotalMilliseconds - t0;
            if (o.Problem != null) problems.Add($"{o.Kind} {o.Id} at ({o.Centre.X:F1}, {o.Centre.Y:F1}, {o.Centre.Z:F1}): {o.Problem}");
            openings.Add(o);
        }

        model._leafInOpening = new bool[model._solids.Length];
        for (int i = 0; i < openings.Count; i++)
        {
            var o = openings[i];
            Add(model._byNode, o.NodeA, i);
            Add(model._byNode, o.NodeB, i);
            foreach (int c in o.Contents)
                if (c >= 0 && c < model._solids.Length && model._solids[c].IsLeaf) model._leafInOpening[c] = true;
        }

        // A room's absorption counts what leaves by its openings, at each opening's own S·τ.
        foreach (var (node, a) in faceAbsorption)
        {
            Vector3 total = a;
            if (model._byNode.TryGetValue(node, out var list))
                foreach (int i in list) total += openings[i].Area * openings[i].Tau;
            model._absorption[node] = Vector3.Max(total, new Vector3(1e-3f));
        }
        model.BuildTimes = new BuildCost(gridMs, solidsMs, deriveMs, sidesMs, clock.Elapsed.TotalMilliseconds);

        static void Add(Dictionary<int, List<int>> into, int node, int i)
        {
            if (!into.TryGetValue(node, out var l)) into[node] = l = new List<int>();
            l.Add(i);
        }
    }

    /// <summary>What an opening in a room's face is called (<see cref="Declared.Kind"/>): an open side, a
    /// tunnel mouth, a doorway with no door in it.</summary>
    public const string FaceKind = "open face";

    /// <summary>What a door's doorway is called (<see cref="Declared.Kind"/>): its leaf's frame is searched round.</summary>
    public const string DoorKind = "door";

    /// <summary>How far past an opening's declared size its jambs, floor and lintel are searched for,
    /// metres: a leaf laps its frame and an authored portal is placed by hand.</summary>
    private const float FrameSearchMetres = 0.5f;

    /// <summary>How far short of an opening's frame a route may cross it, metres. A leg run exactly along
    /// a jamb's face is a crack the building does not have (Diffraction's EdgeSkin, for the same reason).</summary>
    private const float FrameSkinMetres = 0.02f;

    private Opening? Derive(in Declared d, AcousticMap? map)
    {
        var o = new Opening { Id = d.Id, Kind = d.Kind, RegionA = d.RegionA, RegionB = d.RegionB, Centre = d.Centre };
        bool haveFrame = d.Rotation != default;
        if (haveFrame && d.Kind != DoorKind)
        {
            // A gap measured from its walls (FaceOpenings) or from the air that met it (FloodOpenings): nothing to search for. Z is through it whatever
            // its proportions: a slot in a thick wall is deeper than it is wide.
            var fq = Quaternion.Normalize(d.Rotation);
            o.Across = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, fq));
            o.Up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, fq));
            o.Normal = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, fq));
            o.HalfWidth = MathF.Max(0.05f, d.Size.X * 0.5f);
            o.HalfHeight = MathF.Max(0.05f, d.Size.Y * 0.5f);
            o.HalfDepth = MathF.Max(0.01f, d.Size.Z * 0.5f);
            MeasureContents(o);
            if (o.Tau.Y < 0.5f && o.Contents.Length > 0) o.Problem = "a wall stands in it: it is not an opening";
            return o;
        }
        float halfW, halfH, searchW, searchH;
        if (haveFrame)
        {
            // A leaf, shut: its thin axis runs through the doorway, the other horizontal one across it.
            var q = Quaternion.Normalize(d.Rotation);
            Vector3 lx = Vector3.Transform(Vector3.UnitX, q), lz = Vector3.Transform(Vector3.UnitZ, q);
            Vector3 s = d.Size;
            bool thinZ = s.Z <= s.X;
            o.Normal = Vector3.Normalize(thinZ ? lz : lx);
            o.Across = Vector3.Normalize(thinZ ? lx : lz);
            o.Up = Vector3.UnitY;
            halfW = (thinZ ? s.X : s.Z) * 0.5f;
            halfH = s.Y * 0.5f;
            o.HalfDepth = MathF.Max(0.01f, (thinZ ? s.Z : s.X) * 0.5f);
            searchW = halfW + FrameSearchMetres;
            searchH = halfH + FrameSearchMetres;
        }
        else
        {
            // No frame: the room face it sits on says which way it faces.
            if (!FaceOfRoom(map, d, out o.Normal, out float faceHalfW, out float faceHalfH, out o.Up))
            {
                o.Normal = Vector3.UnitX; o.Up = Vector3.UnitY;
                faceHalfW = faceHalfH = MathF.Max(0.5f, d.Aperture * 0.5f);
                o.Problem = "neither of its places is a box it sits on the face of; facing assumed";
            }
            o.Across = Vector3.Normalize(Vector3.Cross(o.Up, o.Normal));
            float declaredHalf = d.Aperture > 0f ? d.Aperture * 0.5f : faceHalfW;
            halfW = MathF.Min(declaredHalf, faceHalfW);
            halfH = faceHalfH;
            searchW = MathF.Min(faceHalfW, halfW + FrameSearchMetres);
            searchH = faceHalfH;
            o.HalfDepth = 0.05f;
        }

        // ── The frame, from the walls round it ──────────────────────────────────────────────────
        // Each side is the first wall (not leaf) met outward from the centre, or the declared extent.
        float right = Reach(o.Centre, o.Across, searchW, out int jambR);
        float left = Reach(o.Centre, -o.Across, searchW, out int jambL);
        float top = Reach(o.Centre, o.Up, searchH, out int lintel);
        float bottom = Reach(o.Centre, -o.Up, searchH, out _);
        if (jambR < 0) right = halfW;
        if (jambL < 0) left = halfW;
        if (lintel < 0) top = halfH;
        if (bottom >= searchH) bottom = halfH;
        o.Centre += o.Across * (right - left) * 0.5f + o.Up * (top - bottom) * 0.5f;
        o.HalfWidth = MathF.Max(0.05f, (right + left) * 0.5f);
        o.HalfHeight = MathF.Max(0.05f, (top + bottom) * 0.5f);
        // As deep as the wall it is cut through, not a box running through it (a tunnel's side wall):
        // counted, the tunnel's openings were fifty metres deep and their checks reached two sections
        // away (2026-10-02).
        foreach (int j in new[] { jambR, jambL, lintel })
        {
            if (j < 0) continue;
            float through = HalfExtentAlong(_solids[j], o.Normal);
            if (through > MathF.Min(HalfExtentAlong(_solids[j], o.Across), HalfExtentAlong(_solids[j], o.Up))) continue;
            o.HalfDepth = MathF.Max(o.HalfDepth, through);
        }
        if (jambR < 0 && jambL < 0 && lintel < 0 && d.Kind != FaceKind)
            o.Problem ??= "no wall found round it: an opening in nothing";

        MeasureContents(o);
        if (o.Tau.Y < 0.5f && o.Contents.Length > 0)
        {
            bool onlyLeaves = true;
            foreach (int i in o.Contents) onlyLeaves &= _solids[i].IsLeaf;
            // Over anything else found: nothing else about it matters.
            if (!onlyLeaves) o.Problem = "a wall stands in it: it is not an opening";
        }
        return o;
    }

    /// <summary>The distance from a point to the first non-leaf box along a direction, up to a limit.</summary>
    private float Reach(Vector3 from, Vector3 dir, float limit, out int hit)
    {
        hit = -1;
        float best = limit;
        var cand = Scratch.Get(this).Candidates;
        _grid.Along(from, from + dir * limit, cand);
        foreach (int i in cand)
        {
            ref readonly var s = ref _solids[i];
            if (s.IsLeaf) continue;
            if (GeometryUtils.IsPointInOBB(from, s.Center, s.Size, s.Rotation)) continue;   // the floor it stands in
            if (GeometryUtils.RayHitsOBB(from, dir, best, s.Center, s.Size, s.Rotation, out float t, out _) && t < best)
            {
                best = t; hit = i;
            }
        }
        return best;
    }

    private static float HalfExtentAlong(in Solid s, Vector3 n)
    {
        Vector3 h = s.Size * 0.5f;
        return MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitX, s.Rotation), n)) * h.X
             + MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitY, s.Rotation), n)) * h.Y
             + MathF.Abs(Vector3.Dot(Vector3.Transform(Vector3.UnitZ, s.Rotation), n)) * h.Z;
    }

    /// <summary>
    /// What stands in the opening and what it lets through: straight through the wall's depth at a grid
    /// of points over the aperture, each the product of the boxes it crosses, the energies averaged. A
    /// shut leaf covers every point; one swung aside covers none; one ajar, the part it still covers.
    /// </summary>
    private void MeasureContents(Opening o)
    {
        var found = new HashSet<int>();
        Vector3 sum = Vector3.Zero;
        int n = 0;
        float depth = o.HalfDepth + 0.05f;
        var cand = Scratch.Get(this).Candidates;
        for (int a = -1; a <= 1; a++)
        for (int b = -1; b <= 1; b++)
        {
            Vector3 p = o.Centre + o.Across * (a * 0.7f * o.HalfWidth) + o.Up * (b * 0.7f * o.HalfHeight);
            Vector3 p0 = p - o.Normal * depth, p1 = p + o.Normal * depth;
            _grid.Along(p0, p1, cand);
            Vector3 tau = Vector3.One;
            foreach (int i in cand)
            {
                if (!SegmentHits(i, p0, p1)) continue;
                found.Add(i);
                tau *= _solidGains[i] * _solidGains[i];
            }
            sum += tau; n++;
        }
        o.Tau = sum / n;
        o.Contents = new int[found.Count];
        found.CopyTo(o.Contents);
    }

    /// <summary>For an opening with no frame of its own: the face of one of its places it lies on.</summary>
    private static bool FaceOfRoom(AcousticMap? map, in Declared d, out Vector3 normal, out float halfW, out float halfH, out Vector3 up)
    {
        normal = Vector3.UnitX; up = Vector3.UnitY; halfW = halfH = 0f;
        if (map == null) return false;
        float bestGap = float.MaxValue;
        bool found = false;
        Span<float> gap = stackalloc float[3];
        foreach (int r in new[] { d.RegionA, d.RegionB })
        {
            if (!map.Regions.TryGetValue(r, out var region) || region.RoomSize.X <= 0f) continue;
            if (RoomAcoustics.OpenFaceCount(region) == 6) continue;
            if (!map.RegionPositions.TryGetValue(r, out var c)) continue;
            var q = map.RegionRotations.GetValueOrDefault(r, Quaternion.Identity);
            Vector3 local = Vector3.Transform(d.Centre - c, Quaternion.Inverse(q));
            Vector3 h = region.RoomSize * 0.5f;
            // Distance of the point from each of the six face planes; the nearest is the one it is on.
            gap[0] = MathF.Abs(MathF.Abs(local.X) - h.X);
            gap[1] = MathF.Abs(MathF.Abs(local.Y) - h.Y);
            gap[2] = MathF.Abs(MathF.Abs(local.Z) - h.Z);
            for (int ax = 0; ax < 3; ax++)
            {
                if (gap[ax] >= bestGap) continue;
                bestGap = gap[ax];
                found = true;
                float sign = (ax == 0 ? local.X : ax == 1 ? local.Y : local.Z) >= 0f ? 1f : -1f;
                Vector3 axis = ax == 0 ? Vector3.UnitX : ax == 1 ? Vector3.UnitY : Vector3.UnitZ;
                normal = Vector3.Normalize(Vector3.Transform(axis * sign, q));
                if (ax == 1)
                {
                    // A floor or ceiling opening: "up" is the region's Z, across its X.
                    up = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q));
                    halfW = h.X; halfH = h.Z;
                }
                else
                {
                    up = Vector3.UnitY;
                    halfW = ax == 0 ? h.Z : h.X;
                    halfH = h.Y;
                }
            }
        }
        return found;
    }

    /// <summary>The places either side of an opening should be the two it joins.</summary>
    private void CheckSides(Opening o, Func<Vector3, int> regionAt)
    {
        float step = o.HalfDepth + 0.3f;
        int a = NodeOf(regionAt(o.Centre + o.Normal * step));
        int b = NodeOf(regionAt(o.Centre - o.Normal * step));
        bool ok = (a == o.NodeA && b == o.NodeB) || (a == o.NodeB && b == o.NodeA);
        if (!ok) o.Problem ??= $"its sides are in {Name(a)} and {Name(b)}, not {Name(o.NodeA)} and {Name(o.NodeB)}";
        static string Name(int n) => n == Outside ? "the outdoors" : $"region {n}";
    }

    // ═══ Asking for a route ═══════════════════════════════════════════════════════════════════════

    /// <summary>What the routes deliver, as amplitude gains per band relative to the free field at the
    /// straight-line distance, and where the strongest of them arrives from.</summary>
    public readonly record struct Answer(float Low, float Mid, float High, Vector3 Apparent, float Length,
                                         int Routes, string Via)
    {
        public float Energy => Low * Low + Mid * Mid + High * High;
    }

    /// <summary>
    /// The routes between two points in two places. False when they are in the same place (no opening
    /// lies between them: the straight line and the barrier search answer that) or when no route joins
    /// them.
    /// </summary>
    public bool Route(Vector3 source, int sourceRegion, Vector3 listener, int listenerRegion, out Answer answer)
    {
        answer = default;
        int sNode = NodeOf(sourceRegion), lNode = NodeOf(listenerRegion);
        if (sNode == lNode) return false;
        var scratch = Scratch.Get(this);

        var lTree = Tree(listener, lNode, scratch.ListenerTree);
        var sTree = Tree(source, sNode, scratch.SourceTree);

        // ── Candidates, shortest first ────────────────────────────────────────────────────────────
        var cand = scratch.Routes;
        cand.Clear();
        // Indoors all the way: an opening the listener's tree reaches that leads into the source's place.
        if (lNode != Outside)
            foreach (var (state, dist) in lTree.Reached)
            {
                int o = state >> 1;
                int far = _openings[o].Other(lTree.From(state));
                if (far != sNode) continue;
                cand.Add(new Candidate(dist + Vector3.Distance(_openings[o].Centre, source), state, -1));
            }
        // By the outdoors: out of the listener's building, across, and into the source's.
        {
            var lOut = Exits(lTree, lNode, listener, scratch.ListenerExits);
            var sOut = Exits(sTree, sNode, source, scratch.SourceExits);
            foreach (var (ls, ld, lp) in lOut)
            foreach (var (ss, sd, sp) in sOut)
            {
                if (ls < 0 && ss < 0) continue;          // both outdoors: not a route through openings
                cand.Add(new Candidate(ld + Vector3.Distance(lp, sp) + sd, ls, ss));
            }
        }
        if (cand.Count == 0) return false;
        cand.Sort((x, y) => x.Cost.CompareTo(y.Cost));

        // ── Each of the few best, evaluated ───────────────────────────────────────────────────────
        float d = MathF.Max(0.1f, Vector3.Distance(source, listener));
        Vector3 total = Vector3.Zero;
        float bestEnergy = -1f;
        Vector3 apparent = listener;
        float bestLength = 0f;
        string via = "";
        int used = 0;
        var seen = scratch.SeenRoutes;
        seen.Clear();
        var chain = scratch.Chain;
        foreach (var c in cand)
        {
            if (used >= RoutesPerQuery) break;
            chain.Clear();
            BuildChain(c, lTree, sTree, chain);
            if (chain.Count == 0 || CrossesTwice(chain)) continue;
            long key = 17;
            foreach (var (o, _) in chain) key = key * 1_000_003 + o;
            if (!seen.Add(key)) continue;
            used++;
            Vector3 e = Evaluate(source, listener, d, chain, scratch, out Vector3 lastCrossing, out float length);
            total += e;
            float sumE = e.X + e.Y + e.Z;
            if (sumE > bestEnergy)
            {
                bestEnergy = sumE; apparent = lastCrossing; bestLength = length;
                via = DescribeChain(chain);
            }
        }
        if (used == 0) return false;
        total = Vector3.Min(total, Vector3.One);       // a route never delivers more than the open field
        answer = new Answer(MathF.Sqrt(total.X), MathF.Sqrt(total.Y), MathF.Sqrt(total.Z), apparent, bestLength, used, via);
        return true;
    }

    /// <summary>
    /// Whether a chain goes through one opening twice: "by the outdoors" between two rooms of one building
    /// it can leave by the front door and come straight back in, and its crossings line up better than
    /// the true route's, so the next room was heard from the front door.
    /// </summary>
    private static bool CrossesTwice(List<(int, int)> chain)
    {
        for (int i = 0; i < chain.Count; i++)
            for (int j = i + 1; j < chain.Count; j++)
                if (chain[i].Item1 == chain[j].Item1) return true;
        return false;
    }

    /// <summary>
    /// How strong a place's reverberant field is at <paramref name="listener"/>, as an amplitude against
    /// standing in it, and where the strongest part arrives from. A diffuse field of energy density E
    /// pushes E c S / 4 watts out of an opening of area S, which over a half space at r metres is
    /// S / (8 pi r^2) of the field's pressure squared, times the opening's transmission: heard straight
    /// into the listener's place, by the routes into anywhere else, summed over every opening within
    /// <paramref name="range"/>. Through only the nearest opening, a room two openings away got nothing
    /// and a lobby none of the street.
    /// </summary>
    public float FieldAt(int regionId, Vector3 listener, int listenerRegion, float range, out Vector3 via)
        => FieldAt(regionId, listener, listenerRegion, range, out via, out _);

    /// <summary><see cref="FieldAt(int, Vector3, int, float, out Vector3)"/>, and a point in the place
    /// itself, two metres out from the opening most of it leaves by: where its field should be
    /// heard (traced) from, for a listener who is not in it.</summary>
    public float FieldAt(int regionId, Vector3 listener, int listenerRegion, float range, out Vector3 via, out Vector3 inField)
    {
        int node = NodeOf(regionId), lNode = NodeOf(listenerRegion);
        via = listener; inField = listener;
        if (node == lNode) return 1f;
        double energy = FieldEnergy(node, listener, listenerRegion, lNode, range, ref via, ref inField);
        // What comes in builds the listener's own room's field: E_room = sum E_out S tau / A, the
        // transmission-room equation. Down a corridor two openings from the street this is most of it.
        if (lNode != Outside && TryGetAbsorption(lNode, out var absorption) && absorption.Y > 0f)
        {
            double into = 0;
            foreach (var o in _openings)
            {
                bool onA = o.NodeA == lNode, onB = o.NodeB == lNode;
                if (onA == onB) continue;
                float sTau = o.Area * o.Tau.Y;
                if (sTau <= 0f || Vector3.Distance(listener, o.Centre) > range) continue;
                int outerNode = onA ? o.NodeB : o.NodeA;
                double outside;
                if (outerNode == node) outside = 1.0;
                else
                {
                    Vector3 point = o.Centre + (onA ? -o.Normal : o.Normal) * (o.HalfDepth + 0.3f);
                    Vector3 ignored = point, ignoredToo = point;
                    outside = Math.Min(1.0, FieldEnergy(node, point, onA ? o.RegionB : o.RegionA, outerNode, range, ref ignored, ref ignoredToo));
                }
                into += outside * sTau / absorption.Y;
            }
            energy += Math.Min(1.0, into);
        }
        return MathF.Min(1f, MathF.Sqrt((float)energy));
    }

    /// <summary>The direct part of <see cref="FieldAt(int, Vector3, int, float, out Vector3)"/>: each opening of the place radiating its field
    /// at <paramref name="listener"/>, straight or by the routes.</summary>
    private double FieldEnergy(int node, Vector3 listener, int listenerRegion, int lNode, float range,
                               ref Vector3 via, ref Vector3 inField)
    {
        double energy = 0; float best = -1f;
        foreach (var o in _openings)
        {
            bool onA = o.NodeA == node, onB = o.NodeB == node;
            if (onA == onB) continue;
            float sTau = o.Area * o.Tau.Y;
            if (sTau <= 0f) continue;
            float toOpening = Vector3.Distance(listener, o.Centre);
            if (toOpening > range) continue;
            float part; Vector3 from;
            if ((onA ? o.NodeB : o.NodeA) == lNode)
            {
                float r = MathF.Max(1f, toOpening);
                part = sTau / (8f * MathF.PI * r * r);
                from = o.Centre;
            }
            else
            {
                // Out of the opening into the place beyond it (its A side is along +Normal), then on.
                Vector3 start = o.Centre + (onA ? -o.Normal : o.Normal) * (o.HalfDepth + 0.3f);
                int beyond = onA ? o.RegionB : o.RegionA;
                if (!Route(start, beyond, listener, listenerRegion, out var answer)) continue;
                float r = MathF.Max(1f, Vector3.Distance(start, listener));
                part = sTau / (8f * MathF.PI) * answer.Mid * answer.Mid / (r * r);
                from = answer.Apparent;
            }
            energy += part;
            if (part > best)
            {
                best = part; via = from;
                inField = o.Centre + (onA ? o.Normal : -o.Normal) * (o.HalfDepth + 2f);
            }
        }
        return energy;
    }

    /// <summary>
    /// What reaches the ear when the straight way (through the walls, or over one edge) and the way by the
    /// openings compete: per band, the one that delivers more; and whether the openings deliver more over
    /// all, which decides where the sound is heard from. One rule, used by every voice.
    /// </summary>
    public static Vector3 Better(Vector3 direct, in Answer route, out bool routeWins)
    {
        var r = new Vector3(route.Low, route.Mid, route.High);
        routeWins = route.Energy > Vector3.Dot(direct, direct);
        return Vector3.Max(direct, r);
    }

    private string DescribeChain(List<(int Opening, int IntoNode)> chain)
    {
        var parts = new List<string>(chain.Count);
        foreach (var (o, _) in chain) parts.Add($"{_openings[o].Kind} {_openings[o].Id}");
        return string.Join(" > ", parts);
    }

    private readonly record struct Candidate(float Cost, int ListenerState, int SourceState);

    /// <summary>
    /// The shortest ways out from a point in a place, through openings, as far as the outdoors and no
    /// further: Dijkstra over (opening, side) states, the cost metres of straight line between crossing
    /// centres. The outdoors is never crossed inside a tree — a route that goes out and back in is joined
    /// by <see cref="Route"/> with the two ends' own trees, which keeps each tree to one building.
    /// </summary>
    private SideTree Tree(Vector3 at, int node, SideTree tree)
    {
        tree.Reset(_openings.Count);
        if (node == Outside || !_byNode.TryGetValue(node, out var start)) return tree;
        var pq = tree.Queue;
        foreach (int o in start)
        {
            int state = State(o, node);
            float dd = Vector3.Distance(at, _openings[o].Centre);
            if (dd < tree.Dist[state]) { tree.Dist[state] = dd; tree.Prev[state] = -1; pq.Enqueue(state, dd); }
        }
        while (pq.TryDequeue(out int s, out float ds))
        {
            if (ds > tree.Dist[s]) continue;
            tree.Reached.Add((s, ds));
            int o = s >> 1;
            int far = _openings[o].Other(tree.From(s));
            if (far == Outside || !_byNode.TryGetValue(far, out var next)) continue;
            foreach (int p in next)
            {
                if (p == o) continue;
                int ns = State(p, far);
                float nd = ds + Vector3.Distance(_openings[o].Centre, _openings[p].Centre);
                if (nd < tree.Dist[ns]) { tree.Dist[ns] = nd; tree.Prev[ns] = s; pq.Enqueue(ns, nd); }
            }
        }
        return tree;
    }

    /// <summary>A state is an opening and the side it is entered from: (index &lt;&lt; 1) | (entered from B).</summary>
    private int State(int opening, int fromNode) => (opening << 1) | (fromNode == _openings[opening].NodeA ? 0 : 1);

    /// <summary>The ways a tree reaches the outdoors: (state, metres so far, where it comes out), the
    /// shortest few; or the point itself when it is outdoors already (state -1).</summary>
    private List<(int State, float Dist, Vector3 At)> Exits(SideTree tree, int node, Vector3 at, List<(int, float, Vector3)> into)
    {
        into.Clear();
        if (node == Outside) { into.Add((-1, 0f, at)); return into; }
        foreach (var (s, ds) in tree.Reached)
        {
            int o = s >> 1;
            if (_openings[o].Other(tree.From(s)) != Outside) continue;
            into.Add((s, ds, _openings[o].Centre));
            if (into.Count >= RoutesPerQuery * 2) break;    // Reached is in order of distance
        }
        return into;
    }

    /// <summary>The route as (opening, the node it leads into) from the source's side to the listener's.</summary>
    private void BuildChain(in Candidate c, SideTree lTree, SideTree sTree, List<(int, int)> chain)
    {
        // The source's half: walking back from the exit gives it exit-first, so reversed.
        int start = chain.Count;
        for (int s = c.SourceState; s >= 0; s = sTree.Prev[s])
        {
            int o = s >> 1;
            chain.Add((o, _openings[o].Other(sTree.From(s))));
        }
        chain.Reverse(start, chain.Count - start);
        // The listener's half: walking back from the meeting state is already source to listener.
        for (int s = c.ListenerState; s >= 0; s = lTree.Prev[s])
        {
            int o = s >> 1;
            chain.Add((o, lTree.From(s)));
        }
    }

    // ── What one route delivers ───────────────────────────────────────────────────────────────

    private Vector3 Evaluate(Vector3 source, Vector3 listener, float d, List<(int Opening, int IntoNode)> chain,
                             Scratch scratch, out Vector3 lastCrossing, out float length)
    {
        int n = chain.Count;
        var x = scratch.Points;
        x.Clear();
        x.Add(source);
        foreach (var (o, _) in chain) x.Add(_openings[o].Centre);
        x.Add(listener);

        // The shortest path through the rectangles: convex in all the crossings together, so improving one
        // at a time with its neighbours fixed converges.
        for (int pass = 0; pass < 4; pass++)
            for (int k = 1; k <= n; k++)
                x[k] = Crossing(_openings[chain[k - 1].Opening], x[k - 1], x[k + 1]);

        length = 0f;
        for (int k = 0; k <= n; k++) length += Vector3.Distance(x[k], x[k + 1]);
        lastCrossing = x[n];

        // ── The legs: what stands between one crossing and the next ───────────────────────────────
        var legs = scratch.Legs;
        legs.Clear();
        // A leg runs to the face of the opening it arrives at, not the wall's middle: a car up the street
        // sees a front door almost edge-on, and a line to the mid-plane runs through the facade for metres.
        for (int k = 0; k <= n; k++)
        {
            int[] ignoreA = k > 0 ? _openings[chain[k - 1].Opening].Contents : Array.Empty<int>();
            int[] ignoreB = k < n ? _openings[chain[k].Opening].Contents : Array.Empty<int>();
            // Inside the edges, not on them: the bend is the aperture's to charge, and a leg ending at the
            // jamb ends in the corner with an open leaf hinged on it, with no way round the leaf.
            Vector3 from = k > 0 ? Face(_openings[chain[k - 1].Opening], Inset(_openings[chain[k - 1].Opening], x[k]), x[k + 1]) : x[k];
            Vector3 to = k < n ? Face(_openings[chain[k].Opening], Inset(_openings[chain[k].Opening], x[k + 1]), x[k]) : x[k + 1];
            // Off the face: a leg ending on it touched the wall, the padded clearance refused every way
            // round the open leaf, and the leaf was charged as solid steel ("sound struggles through the
            // door only when loud things pass").
            Vector3 along = to - from;
            float span = along.Length();
            if (span > 4f * FaceClearance)
            {
                along /= span;
                if (k > 0) from += along * FaceClearance;
                if (k < n) to -= along * FaceClearance;
            }
            legs.Add(Leg(k > 0 ? chain[k - 1].Opening : -1, from, k < n ? chain[k].Opening : -1, to, ignoreA, ignoreB));
        }

        // ── The diffracted ray ────────────────────────────────────────────────────────────────────
        float spread = d / MathF.Max(d, length);
        Vector3 geo = new Vector3(spread * spread);
        for (int k = 0; k <= n; k++) geo *= legs[k] * legs[k];
        for (int k = 1; k <= n; k++)
        {
            var o = _openings[chain[k - 1].Opening];
            geo *= o.Tau * Aperture(o, x[k - 1], x[k + 1]);
        }

        // ── The field of the rooms between, relayed opening to opening ────────────────────────────
        // Only with a room between: with one opening the two rooms' fields are the reverb's.
        if (n < 2) return geo;
        // Free-field reference: W = 1 at d, so a mean-square pressure p²/ρc is compared with 1/(4πd²).
        float free = 1f / (4f * MathF.PI * d * d);
        var first = _openings[chain[0].Opening];
        float r1 = MathF.Max(0.1f, Vector3.Distance(source, x[1]));
        float cos1 = MathF.Abs(Vector3.Dot(Vector3.Normalize(x[1] - source), first.Normal));
        // Into the first room between: the source's direct sound on the opening's projected area.
        Vector3 power = legs[0] * legs[0] * (cos1 / (4f * MathF.PI * r1 * r1)) * first.Area * first.Tau;
        // Through each room between: its field on the next opening. Across the outdoors only the ray.
        for (int k = 1; k < n; k++)
        {
            if (!TryGetAbsorption(chain[k - 1].IntoNode, out var ak)) return geo;
            var next = _openings[chain[k].Opening];
            power = power / ak * next.Area * next.Tau;
        }
        // And out of the last opening, as a Lambert surface, straight at the listener.
        var last = _openings[chain[n - 1].Opening];
        float rn = MathF.Max(0.1f, Vector3.Distance(x[n], listener));
        float cosn = MathF.Abs(Vector3.Dot(Vector3.Normalize(listener - x[n]), last.Normal));
        Vector3 atEar = power * (cosn / (MathF.PI * rn * rn)) * (legs[n] * legs[n]);

        return geo + atEar / free;
    }

    /// <summary>Where a route crossing an opening at <paramref name="crossing"/> leaves its wall on the side
    /// facing <paramref name="toward"/>; the crossing itself when that point is inside the wall.</summary>
    private static Vector3 Face(Opening o, Vector3 crossing, Vector3 toward)
    {
        float side = Vector3.Dot(toward - crossing, o.Normal);
        float depth = o.HalfDepth + FrameSkinMetres;
        if (MathF.Abs(side) <= depth) return crossing;
        return crossing + o.Normal * (side > 0f ? depth : -depth);
    }

    /// <summary>The point of an opening's rectangle the route from <paramref name="prev"/> to
    /// <paramref name="next"/> is shortest through: where the straight line meets it if it does, otherwise
    /// the best point on its rim.</summary>
    private static Vector3 Crossing(Opening o, Vector3 prev, Vector3 next)
    {
        float hw = MathF.Max(0f, o.HalfWidth - FrameSkinMetres), hh = MathF.Max(0f, o.HalfHeight - FrameSkinMetres);
        Vector3 dir = next - prev;
        float den = Vector3.Dot(dir, o.Normal);
        if (MathF.Abs(den) > 1e-6f)
        {
            float t = Vector3.Dot(o.Centre - prev, o.Normal) / den;
            if (t >= 0f && t <= 1f)
            {
                Vector3 p = prev + dir * t - o.Centre;
                float u = Vector3.Dot(p, o.Across), v = Vector3.Dot(p, o.Up);
                if (MathF.Abs(u) <= hw && MathF.Abs(v) <= hh) return o.Centre + o.Across * u + o.Up * v;
            }
        }
        // On the rim: the best of the four edges, each solved exactly (Diffraction.MinimiseOnEdge).
        Vector3 best = o.Centre;
        float bestLen = float.MaxValue;
        Span<Vector3> corner = stackalloc Vector3[4]
        {
            o.Centre - o.Across * hw - o.Up * hh, o.Centre + o.Across * hw - o.Up * hh,
            o.Centre + o.Across * hw + o.Up * hh, o.Centre - o.Across * hw + o.Up * hh,
        };
        for (int e = 0; e < 4; e++)
        {
            Vector3 a = corner[e], b = corner[(e + 1) & 3];
            float s = Diffraction.MinimiseOnEdge(a, b, prev, next);
            Vector3 p = Vector3.Lerp(a, b, s);
            float len = Vector3.Distance(prev, p) + Vector3.Distance(p, next);
            if (len < bestLen) { bestLen = len; best = p; }
        }
        return best;
    }

    /// <summary>
    /// What a rectangular aperture passes of a wave from one point to another, energy per band: the
    /// Fresnel–Kirchhoff result, separable in the aperture's two axes (Born &amp; Wolf §8.7.4). Each edge's
    /// Fresnel parameter ν = 2√(δ/λ) is taken from the extra path by the nearest point of that edge's line,
    /// signed by which side of the straight line the edge lies on, so a wide opening in plain view passes
    /// everything, a source at its rim loses 6 dB, and one round its corner loses more at every octave up.
    /// </summary>
    public static Vector3 Aperture(Opening o, Vector3 prev, Vector3 next)
    {
        float straight = Vector3.Distance(prev, next);
        // Where the straight line meets the plane (a parallel line by its midpoint).
        Vector3 dir = next - prev;
        float den = Vector3.Dot(dir, o.Normal);
        Vector3 at = MathF.Abs(den) > 1e-6f
            ? prev + dir * Math.Clamp(Vector3.Dot(o.Centre - prev, o.Normal) / den, 0f, 1f)
            : (prev + next) * 0.5f;
        float u = Vector3.Dot(at - o.Centre, o.Across), v = Vector3.Dot(at - o.Centre, o.Up);

        Vector3 result = Vector3.One;
        // Across: the two vertical edges, at -hw and +hw; up: the floor and lintel at -hh and +hh.
        for (int axis = 0; axis < 2; axis++)
        {
            Vector3 along = axis == 0 ? o.Up : o.Across;
            Vector3 off = axis == 0 ? o.Across : o.Up;
            float half = axis == 0 ? o.HalfWidth : o.HalfHeight;
            float line = axis == 0 ? u : v;
            float dLo = EdgeDetour(o.Centre - off * half, along, prev, next, straight);
            float dHi = EdgeDetour(o.Centre + off * half, along, prev, next, straight);
            float sLo = (-half - line) >= 0f ? 1f : -1f;    // the edge's side of the straight line
            float sHi = (half - line) >= 0f ? 1f : -1f;
            for (int b = 0; b < 3; b++)
            {
                float lambda = WallTransmission.SoundSpeed / (b == 0 ? BandHz.X : b == 1 ? BandHz.Y : BandHz.Z);
                float nuLo = sLo * 2f * MathF.Sqrt(dLo / lambda);
                float nuHi = sHi * 2f * MathF.Sqrt(dHi / lambda);
                Fresnel(nuLo, out float c1, out float s1);
                Fresnel(nuHi, out float c2, out float s2);
                float f = 0.5f * ((c2 - c1) * (c2 - c1) + (s2 - s1) * (s2 - s1));
                if (b == 0) result.X *= f; else if (b == 1) result.Y *= f; else result.Z *= f;
            }
        }
        return Vector3.Min(result, Vector3.One);
    }

    /// <summary>The extra path from one point to another by the nearest point of an infinite edge line.</summary>
    private static float EdgeDetour(Vector3 point, Vector3 along, Vector3 from, Vector3 to, float straight)
    {
        // Unfolded about the line, as Diffraction.MinimiseOnEdge.
        float sF = Vector3.Dot(from - point, along), sT = Vector3.Dot(to - point, along);
        float rF = Vector3.Distance(from, point + along * sF), rT = Vector3.Distance(to, point + along * sT);
        float s = rF + rT > 1e-9f ? sF + (sT - sF) * rF / (rF + rT) : 0.5f * (sF + sT);
        Vector3 p = point + along * s;
        return MathF.Max(0f, Vector3.Distance(from, p) + Vector3.Distance(p, to) - straight);
    }

    /// <summary>
    /// The Fresnel integrals C(x) and S(x) (normalised, C(∞) = S(∞) = ½), by the auxiliary functions of
    /// Abramowitz &amp; Stegun 7.3.32–33, absolute error under 2·10⁻³: C = ½ + f sin(πx²/2) − g cos(πx²/2),
    /// S = ½ − f cos(πx²/2) − g sin(πx²/2) for x ≥ 0; both odd in x.
    /// </summary>
    public static void Fresnel(float x, out float c, out float s)
    {
        float ax = MathF.Abs(x);
        float f = (1f + 0.926f * ax) / (2f + 1.792f * ax + 3.104f * ax * ax);
        float g = 1f / (2f + 4.142f * ax + 3.492f * ax * ax + 6.670f * ax * ax * ax);
        float arg = 0.5f * MathF.PI * ax * ax;
        float sn = MathF.Sin(arg), cs = MathF.Cos(arg);
        c = 0.5f + f * sn - g * cs;
        s = 0.5f - f * cs - g * sn;
        if (x < 0f) { c = -c; s = -s; }
    }

    // ═══ Legs: what stands between two points ════════════════════════════════════════════════════

    /// <summary>
    /// What a leg loses, kept per pair of end cells a fiftieth of its length across (a quarter of a metre
    /// at a doorway, four metres 300 m away): a blocked leg hundreds of metres long costs milliseconds to
    /// search round. Either way round, so a route and its reverse share it.
    /// </summary>
    private Vector3 Leg(int openingA, Vector3 a, int openingB, Vector3 b, int[] ignoreA, int[] ignoreB)
    {
        float len = Vector3.Distance(a, b);
        int level = Math.Clamp((int)MathF.Round(MathF.Log2(MathF.Max(1f, 0.02f * len / LegCellMetres))), 0, 4);
        float cell = LegCellMetres * (1 << level);
        var ka = (openingA, (int)MathF.Floor(a.X / cell), (int)MathF.Floor(a.Y / cell), (int)MathF.Floor(a.Z / cell));
        var kb = (openingB, (int)MathF.Floor(b.X / cell), (int)MathF.Floor(b.Y / cell), (int)MathF.Floor(b.Z / cell));
        var key = ka.CompareTo(kb) <= 0 ? (level, ka, kb) : (level, kb, ka);
        if (_legCache.TryGetValue(key, out var held)) return held;
        var gains = LegGains(a, b, ignoreA, ignoreB);
        if (_legCache.Count > LegCacheLimit) _legCache.Clear();
        _legCache[key] = gains;
        return gains;
    }

    /// <summary>A point in an opening's plane held <see cref="EdgeClearance"/> inside its edges.</summary>
    private static Vector3 Inset(Opening o, Vector3 p)
    {
        Vector3 d = p - o.Centre;
        float n = Vector3.Dot(d, o.Normal), u = Vector3.Dot(d, o.Across), v = Vector3.Dot(d, o.Up);
        float hu = MathF.Max(0f, o.HalfWidth - EdgeClearance), hv = MathF.Max(0f, o.HalfHeight - EdgeClearance);
        return o.Centre + o.Normal * n + o.Across * Math.Clamp(u, -hu, hu) + o.Up * Math.Clamp(v, -hv, hv);
    }

    /// <summary>How far inside an opening's edges a leg is taken to end, metres.</summary>
    private const float EdgeClearance = 0.15f;

    /// <summary>How far off an opening's face a leg is taken to end, metres: past the joint padding of
    /// <see cref="RouteJointMetres"/>, so the wall beside the doorway does not count as in the way.</summary>
    private const float FaceClearance = 0.1f;

    /// <summary>The finest cell a leg is kept for, metres; coarser by powers of two with its length.</summary>
    private const float LegCellMetres = 0.25f;
    private const int LegCacheLimit = 50_000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, (int, int, int, int), (int, int, int, int)), Vector3> _legCache = new();

    /// <summary>
    /// What a straight leg loses to the boxes it crosses, amplitude per band: what their constructions let
    /// through (<see cref="WallTransmission"/>), or what bends round them (<see cref="BarrierGains"/>),
    /// whichever passes more in each band. The boxes standing in the openings at either end are not
    /// charged: the opening's own transmission is. A leg through the earth (<see cref="ThroughTheGround"/>)
    /// passes nothing that way, and what comes is what bends over the ground's profile or round the boxes.
    /// </summary>
    public Vector3 LegGains(Vector3 a, Vector3 b, int[] ignoreA, int[] ignoreB)
    {
        var scratch = Scratch.Get(this);
        var cand = scratch.Candidates;
        _grid.Along(a, b, cand);
        var crossed = scratch.Layers;
        crossed.Clear();
        float len = Vector3.Distance(a, b);
        foreach (int i in cand)
        {
            if (Array.IndexOf(ignoreA, i) >= 0 || Array.IndexOf(ignoreB, i) >= 0) continue;
            if (!SegmentSpan(i, a, b, 0f, out float t0, out float t1)) continue;
            crossed.Add(CrossingOf(i, a, b, t0, t1, len));
        }
        bool earth = ThroughTheGround(a, b);
        if (crossed.Count == 0 && !earth) return Vector3.One;
        Vector3 through = earth ? Vector3.Zero : Constructions.Through(crossed, len > 1e-6f ? (b - a) / len : Vector3.UnitX);
        var round = BarrierGains(a, b, WallTransmission.SoundSpeed, out _, out _, out _, out bool found, ignoreA, ignoreB);
        return found ? Vector3.Max(through, round) : through;
    }

    /// <summary>Box i crossed by the line from <paramref name="a"/> to <paramref name="b"/>, from <paramref name="t0"/>
    /// to <paramref name="t1"/> of its length <paramref name="len"/>: solids in contact crossed one after another are
    /// one construction, and one gone into by its edge pays what the line runs through of it (Constructions).</summary>
    private Constructions.Crossing CrossingOf(int i, Vector3 a, Vector3 b, float t0, float t1, float len)
    {
        ref readonly var s = ref _solids[i];
        bool layer = !s.IsLeaf && Constructions.IsSheet(s.Size);
        var f = _frames[i];
        float run = Constructions.RunInsideLocal(Vector3.Transform(a - f.Centre, f.Inverse), Vector3.Transform(b - f.Centre, f.Inverse), s.Size);
        return Constructions.Of(t0 * len, t1 * len, AcousticRegistry.GetProperties(s.Material), s.Size, s.Build, 1,
                                layer ? Constructions.Normal(s.Size, s.Rotation) : Vector3.Zero, _solidGains[i], run);
    }

    /// <summary>
    /// What charging the straight line's boxes by what it runs through of them does to it, per band, against
    /// each box's panel figure (Constructions.InPlaneCorrection): 1 unless the line goes into some solid by
    /// its edge. The worker multiplies Steam Audio's transmission by it, whose faces each carry a panel's figure.
    /// </summary>
    public Vector3 InPlaneCorrection(Vector3 source, Vector3 listener)
    {
        var scratch = Scratch.Get(this);
        var cand = scratch.Candidates;
        _grid.Along(source, listener, cand);
        var crossed = scratch.Layers;
        crossed.Clear();
        float len = Vector3.Distance(source, listener);
        if (len < 1e-6f) return Vector3.One;
        bool any = false;
        foreach (int i in cand)
        {
            if (!SegmentSpan(i, source, listener, 0f, out float t0, out float t1)) continue;
            var c = CrossingOf(i, source, listener, t0, t1, len);
            any |= c.Run > 0f;
            crossed.Add(c);
        }
        return any ? Constructions.InPlaneCorrection(crossed, (listener - source) / len) : Vector3.One;
    }

    /// <summary>
    /// What the worker multiplies Steam Audio's transmission by, per band: nothing through the earth (the
    /// straight line under the terrain, <see cref="ThroughTheGround"/>), and otherwise the run rule's
    /// correction (<see cref="InPlaneCorrection"/>). Steam Audio's ground is its surface's faces, each carrying
    /// its land cover's figure, and the porous covers' figures do not depend on thickness: a line through a
    /// grassy hill 4 m high, its ends 100 m apart, passed -8/-4/-2 dB (bare earth -10/-8/-6), where over
    /// its crest is -8/-14/-22 (OpeningRoutesTerrainTests). What comes over the hill is the barrier
    /// model's way over the ground's profile (<see cref="BarrierGains"/>), as for every other asker.
    /// </summary>
    public Vector3 SimulatorCorrection(Vector3 source, Vector3 listener)
        => ThroughTheGround(source, listener) ? Vector3.Zero : InPlaneCorrection(source, listener);

    /// <summary>
    /// For an asker that charges the straight line through boxes only (the hand-rolled tracer): when the line
    /// goes through the earth, which passes nothing, what comes over everything in the vertical plane, the
    /// ground's profile and the boxes on it (<see cref="OverTheTop"/>), by the barrier model's law, and the top
    /// it is heard from (<paramref name="edge"/>); zero, <paramref name="found"/> false, with no way over.
    /// False when the line stays out of the ground, and the asker's own figure stands.
    /// </summary>
    public bool OverTheGround(Vector3 source, Vector3 listener, float speedOfSound, out Vector3 gains, out Vector3 edge, out bool found)
    {
        gains = Vector3.Zero; edge = listener; found = false;
        if (!ThroughTheGround(source, listener)) return false;
        float over = OverTheTop(source, listener, null, null, out edge);
        if (over < 0f) return true;
        found = true;
        gains = Round(MathF.Max(over, GrazingMetres), MathF.Max(0.5f, Vector3.Distance(source, listener)), speedOfSound);
        return true;
    }

    /// <summary>The boxes the straight line passes, one a line: each one's panel figure, and how far the line
    /// runs inside one it goes into by its edge and what that run lets through. For the lab.</summary>
    public string ExplainLine(Vector3 source, Vector3 listener)
    {
        var sb = new System.Text.StringBuilder();
        var cand = new List<int>();
        _grid.Along(source, listener, cand);
        float len = Vector3.Distance(source, listener);
        foreach (int i in cand)
        {
            if (!SegmentSpan(i, source, listener, 0f, out float t0, out float t1)) continue;
            var c = CrossingOf(i, source, listener, t0, t1, len);
            ref readonly var s = ref _solids[i];
            sb.Append($"        line box {i} {s.Material} ({s.Center.X:F2}, {s.Center.Y:F2}, {s.Center.Z:F2}) size ({s.Size.X:F2}, {s.Size.Y:F2}, {s.Size.Z:F2}), "
                    + $"in {t0 * len:F2} out {t1 * len:F2} m: panel {Db3(c.AsPanel)}"
                    + (c.Run > 0f ? $", gone in by its edge, {c.Run:F2} m inside {Db3(c.Gains)}" : "") + "\n");
        }
        if (GroundCounts && _ground.Profile(source, listener, RouteJointMetres, new List<(float, float)>(), out float most, out float at))
            sb.Append($"        line through the earth: the ground {most:F2} m over it at {at:F1} m along; the ground under the source "
                    + $"{_ground.Describe(source.X, source.Z)}, under the ear {_ground.Describe(listener.X, listener.Z)}\n");
        return sb.ToString();
    }

    // ═══ Round one thing and through another ══════════════════════════════════════════════════════

    /// <summary>
    /// What gets past the boxes in the way by bending round them, amplitude per band against the free field
    /// at the straight distance, the longer way's spreading included; <paramref name="found"/> false (and
    /// zero) when there is no way round. Per band the better of two kinds of path:
    /// <list type="bullet">
    /// <item>round everything, clear of the whole scene (<see cref="BarrierPathDifference"/>, whose
    /// <paramref name="pathDifference"/> and <paramref name="verified"/> come back as it gives them);</item>
    /// <item>round one box on the line, its legs bending round what stands against the bend and paying what
    /// they pass through (<see cref="LegsThrough"/>): Maekawa's loss for the whole detour times what the boxes
    /// the legs cross let through (<see cref="WallTransmission"/>). A ray through a sequence of edges and walls
    /// keeps the product of what each does to it (Funkhouser et al., JASA 115, 2004, p. 747). Never more
    /// than that detour's diffraction times the straight line through every box the way does not go round:
    /// a wall the way passes through costs it no less than it costs the line. Round an edge in the air first;
    /// then round an edge buried in another box, that box paid once (LegsThrough, Passage): what a front
    /// lets through radiates over its whole face, so the brick just over the edge of a slab built into it is
    /// heard past that edge, as the field through a panel is diffracted at the panel's edge.</item>
    /// </list>
    /// No way bends at or under the terrain (<see cref="WaysRound"/>), which is no box, and no leg goes
    /// through it (<see cref="ThroughTheGround"/>); a line through it is gone over (<see cref="OverTheTop"/>).
    /// Without the second, a 3 cm way over a stair's concrete guard whose leg ran on through the brick front
    /// was thrown out, and the guard was charged as a solid wall: a car on Dock Street jumped 22 to 31 dB
    /// as its line came out of the guard's shadow (Marlow Tower, 2026-10-11, --pop-hunt).
    /// <paramref name="edge"/> is where the best of them, by energy, leaves for the ear.
    /// </summary>
    public Vector3 BarrierGains(Vector3 source, Vector3 listener, float speedOfSound, out float pathDifference,
                                out bool verified, out Vector3 edge, out bool found, int[]? ignoreA = null, int[]? ignoreB = null)
        => BarrierGains(source, listener, speedOfSound, out pathDifference, out verified, out edge, out found, ignoreA, ignoreB, null);

    private Vector3 BarrierGains(Vector3 source, Vector3 listener, float speedOfSound, out float pathDifference,
                                 out bool verified, out Vector3 edge, out bool found, int[]? ignoreA, int[]? ignoreB,
                                 System.Text.StringBuilder? explain)
    {
        pathDifference = BarrierPathDifference(source, listener, out edge, out verified, ignoreA, ignoreB);
        float dist = MathF.Max(0.5f, Vector3.Distance(source, listener));
        Vector3 best = Vector3.Zero;
        float bestEnergy = 0f;
        found = false;
        if (verified && pathDifference >= 0f)
        {
            best = Round(pathDifference, dist, speedOfSound);
            bestEnergy = Vector3.Dot(best, best);
            found = true;
        }
        var scratch = Scratch.Get(this);
        var all = scratch.AllBarrierRoutes;
        if (all.Count == 0 || OnlyClearWaysRoundForLab) return best;

        // The straight line's crossings, to pay the rest of the line without the boxes a way goes round.
        var line = scratch.LineCrossings;
        line.Clear();
        float len = Vector3.Distance(source, listener);
        Vector3 dir = len > 1e-6f ? (listener - source) / len : Vector3.UnitX;
        var cand = scratch.Candidates;
        _grid.Along(source, listener, cand);
        foreach (int i in cand)
        {
            if (ignoreA != null && Array.IndexOf(ignoreA, i) >= 0) continue;
            if (ignoreB != null && Array.IndexOf(ignoreB, i) >= 0) continue;
            if (!SegmentSpan(i, source, listener, 0f, out float t0, out float t1)) continue;
            line.Add((i, CrossingOf(i, source, listener, t0, t1, len)));
        }

        // Most promising first: the detour's diffraction times the rest of the line without that box, each
        // box by its own figure (an order, not a bound; the layered figure costs a panel model per box).
        var tc = scratch.ThroughCandidates;
        tc.Clear();
        var paid = scratch.Paid;
        int restFor = -1;
        Vector3 rest = Vector3.One;
        foreach (var (d, ps, p, box) in all)
        {
            // Round a leaf standing in its doorway is through the doorway (BarrierPathDifference).
            if (_solids[box].IsLeaf && box < _leafInOpening.Length && _leafInOpening[box]) continue;
            var round = Round(MathF.Max(d, GrazingMetres), dist, speedOfSound);
            if (round.X <= best.X && round.Y <= best.Y && round.Z <= best.Z) continue;
            if (box != restFor)
            {
                rest = Vector3.One;
                foreach (var (i, c) in line) if (i != box) rest *= c.Gains;
                restFor = box;
            }
            var hope = round * rest;
            tc.Add((Vector3.Dot(hope, hope), d, ps, p, box));
        }
        if (tc.Count > 1) tc.Sort((x, y) => y.Hope.CompareTo(x.Hope));
        Vector3 bestEdge = edge;
        bool any = found;
        var burial = scratch.Burials;
        burial.Clear();
        for (int k = 0; k < tc.Count; k++) burial.Add(Burial.Unknown);
        Burial BurialOf(int k)
        {
            if (burial[k] == Burial.Unknown)
            {
                var (_, _, ps, p, box) = tc[k];
                burial[k] = EdgeBurial(p, ps, box, ignoreA, ignoreB, null, null);
            }
            return burial[k];
        }

        // One way round each box: the first, most promising, whose edge is in the air, or on its own
        // construction's edge (Buried). A box whose first few are all buried is a wall's.
        var perBox = scratch.PerBox;
        perBox.Clear();
        int tried = 0;
        scratch.ChainBudget = ThroughBendsPerSearch;
        for (int k = 0; k < tc.Count; k++)
        {
            int box = tc[k].Box;
            int looked = perBox.TryGetValue(box, out int had) ? had : 0;
            if (looked < 0 || looked >= BuriedLooksPerBox) continue;
            perBox[box] = looked + 1;
            if (BurialOf(k) != Burial.Air) continue;
            perBox[box] = -1;
            if (++tried > MaxThroughTried) break;
            TryWay(k, buried: false);
        }

        // Then round an edge buried in another box, the most promising of each box that comes before any
        // way of it in the air: the slab's edge in the brick of the front it is built into. Its legs pay
        // that box once. A search of its own, so it only ever adds to the ways in the air.
        if (!OldBurialForLab)
        {
            perBox.Clear();
            tried = 0;
            // Their legs go straight: a third of the cost, and 6 pairs in 4,000 on the city up to 10 dB under
            // what bending them round what stands by the bend gives.
            scratch.ChainBudget = 0;
            for (int k = 0; k < tc.Count; k++)
            {
                int box = tc[k].Box;
                int looked = perBox.TryGetValue(box, out int had) ? had : 0;
                if (looked < 0 || looked >= BuriedLooksPerBox) continue;
                perBox[box] = looked + 1;
                // A box's ways come most promising first: once one cannot beat what was found, none after it can.
                var most = Round(MathF.Max(tc[k].D, GrazingMetres), dist, speedOfSound);
                if (most.X <= best.X && most.Y <= best.Y && most.Z <= best.Z) { perBox[box] = -1; continue; }
                var kind = BurialOf(k);
                if (kind == Burial.Air) { perBox[box] = -1; continue; }
                perBox[box] = -1;
                if (tried >= MaxThroughTried) break;
                if (TryWay(k, buried: true)) tried++;
            }
        }
        edge = bestEdge;
        found = any;
        return best;

        // The way tc[k], round its box, its legs paid (LegsThrough), competing per band with what was found.
        // Whether its legs were traced.
        bool TryWay(int k, bool buried)
        {
            var (_, d, ps, p, box) = tc[k];
            float dd = MathF.Max(d, GrazingMetres);
            var round = Round(dd, dist, speedOfSound);
            if (round.X <= best.X && round.Y <= best.Y && round.Z <= best.Z) return false;
            // What the edge is buried in, and the layers of its own construction that end with it there.
            var inBoxes = scratch.EdgeBoxes;
            var ownLayers = scratch.EdgeLayers;
            inBoxes.Clear(); ownLayers.Clear();
            EdgeBurial(p, ps, box, ignoreA, ignoreB, inBoxes, ownLayers);
            if (buried)
            {
                // Its legs pay at least the panels of what the edge is buried in: no better than found, not traced.
                var bound = round;
                foreach (int b in inBoxes) bound *= _solidGains[b];
                if (bound.X <= best.X && bound.Y <= best.Y && bound.Z <= best.Z) return false;
            }
            paid.Clear();
            var legs = LegsThrough(source, ps, p, listener, box, dd, dist, speedOfSound, ignoreA, ignoreB,
                                   buried ? inBoxes : null, out float extra, out bool blocked);
            if (blocked)
            {
                explain?.Append($"        round box {box} {_solids[box].Material}: its edge buried in {string.Join(", ", inBoxes.Select(b => $"{b} {_solids[b].Material}"))}, "
                              + "which the sound could reach there only through the box itself: no way\n");
                return false;
            }
            var detour = Round(dd + extra, dist, speedOfSound);
            // Every box on the line the way does not go round, it passes through: never paying less for
            // them than the line does.
            var line_ = LineThrough(line, -1, paid, dir);
            var g = detour * Vector3.Min(legs, line_);
            best = Vector3.Max(best, g);
            any = true;
            float energy = Vector3.Dot(g, g);
            if (energy > bestEnergy) { bestEnergy = energy; bestEdge = p; }
            if (explain != null)
            {
                explain.Append($"        round box {box} {_solids[box].Material} ({_solids[box].Center.X:F1}, {_solids[box].Center.Y:F2}, {_solids[box].Center.Z:F1}) "
                             + $"size ({_solids[box].Size.X:F2}, {_solids[box].Size.Y:F2}, {_solids[box].Size.Z:F2}) {d:F3} m, {dd + extra:F3} m in all ({Db3(detour)}); "
                             + (ownLayers.Count > 0 ? $"its construction's {string.Join(", ", ownLayers.Select(b => $"{b} {_solids[b].Material}"))} ending with it; " : "")
                             + (buried ? $"its edge buried in {string.Join(", ", inBoxes.Select(b => $"{b} {_solids[b].Material}"))}, paid once; " : "")
                             + $"its legs pay {string.Join(", ", paid.Select(b => $"{b} {_solids[b].Material}"))} ({Db3(legs)}), the line those of them it crosses {Db3(line_)}: {Db3(g)}\n");
            }
            return true;
        }
    }

    /// <summary>What the straight line keeps through the boxes it crosses: all but <paramref name="except"/>,
    /// or only those in <paramref name="only"/> when given.</summary>
    private Vector3 LineThrough(List<(int Box, Constructions.Crossing Crossing)> line, int except, List<int>? only, Vector3 dir)
    {
        var without = Scratch.Get(this).Without;
        without.Clear();
        foreach (var (i, c) in line)
            if (i != except && (only == null || only.Contains(i))) without.Add(c);
        return Constructions.Through(without, dir);
    }

    /// <summary>What an edge is buried in: nothing but its own construction (in the air), or another box.</summary>
    private enum Burial : byte { Unknown, Air, Boxes }

    /// <summary>A way's bends (<paramref name="p"/>, and <paramref name="ps"/> where it crosses a face) on the
    /// edges of box <paramref name="owner"/>: buried in the boxes added to <paramref name="boxes"/>, and on
    /// the edge of the layers of its own construction added to <paramref name="layers"/> (<see cref="Buried"/>).</summary>
    private Burial EdgeBurial(Vector3 p, Vector3 ps, int owner, int[]? ignoreA, int[]? ignoreB, List<int>? boxes, List<int>? layers)
    {
        bool inBox = Buried(p, owner, ignoreA, ignoreB, boxes, layers);
        if (ps != p && (boxes != null || !inBox)) inBox |= Buried(ps, owner, ignoreA, ignoreB, boxes, layers);
        return inBox ? Burial.Boxes : Burial.Air;
    }

    /// <summary>
    /// Whether a point on an edge of box <paramref name="owner"/> is buried in another box, every box grown
    /// by the joint: a guard's foot in the floor it stands on, a slab's edge in the brick of the front built
    /// on it. A layer of the owner's own construction (sheets in contact, <see cref="LayeredFaces.InContact"/>)
    /// whose own edge runs there too does not bury it: the two end together, and the point is on the edge of
    /// the construction, in the air. A 2 cm finish laid on a slab to its outline buried every top edge of the
    /// slab, and the slab every edge of the finish, so no way went round the floor's edge but under the
    /// slab, into the earth. Such a layer is added to <paramref name="layers"/> (for the lab), every box that
    /// buries the point to <paramref name="boxes"/>. Before 2026-10-11 (<see cref="OldBurialForLab"/>) any
    /// other box buried it.
    /// </summary>
    private bool Buried(Vector3 p, int owner, int[]? ignoreA, int[]? ignoreB, List<int>? boxes, List<int>? layers)
    {
        var cand = Scratch.Get(this).Clear;
        var e = new Vector3(RouteJointMetres);
        _grid.Along(p - e, p + e, cand);
        bool buried = false;
        foreach (int i in cand)
        {
            if (i == owner) continue;
            if (ignoreA != null && Array.IndexOf(ignoreA, i) >= 0) continue;
            if (ignoreB != null && Array.IndexOf(ignoreB, i) >= 0) continue;
            ref readonly var s = ref _solids[i];
            if (!GeometryUtils.IsPointInOBB(p, s.Center, s.Size + new Vector3(2f * RouteJointMetres), s.Rotation)) continue;
            if (!OldBurialForLab && EndsWith(i, owner, p))
            {
                if (layers != null && !layers.Contains(i)) layers.Add(i);
                continue;
            }
            buried = true;
            if (boxes == null) return true;
            if (!boxes.Contains(i)) boxes.Add(i);
        }
        return buried;
    }

    /// <summary>Whether box <paramref name="i"/> is a layer of <paramref name="owner"/>'s construction whose own
    /// edge runs through <paramref name="p"/> (within the joint of two of its sides): the two end together.</summary>
    private bool EndsWith(int i, int owner, Vector3 p)
    {
        ref readonly var s = ref _solids[i];
        ref readonly var o = ref _solids[owner];
        if (s.IsLeaf || o.IsLeaf) return false;
        if (!LayeredFaces.InContact(o.Center, o.Size, o.Rotation, s.Center, s.Size, s.Rotation)) return false;
        var f = _frames[i];
        var local = Vector3.Abs(Vector3.Transform(p - f.Centre, f.Inverse));
        var inner = f.Half - new Vector3(RouteJointMetres);
        int sides = (local.X >= inner.X ? 1 : 0) + (local.Y >= inner.Y ? 1 : 0) + (local.Z >= inner.Z ? 1 : 0);
        return sides >= 2;
    }

    /// <summary>
    /// The barrier model as it was before 2026-10-11, for the lab (--barrier-sweep buried) and the tests: every
    /// other box within the joint buries an edge, the terrain buries none and is in no segment's way, and no
    /// way goes round an edge buried in another box. Per thread. Never set in the game.
    /// </summary>
    [ThreadStatic] public static bool OldBurialForLab;

    /// <summary>For the lab (--barrier-sweep terrain) and the tests: the ground no obstacle to a straight
    /// segment, as before 2026-10-11 (docs/GEOMETRY.md 12.6, "The ground in the way"): the legs, the line and
    /// the string over the top paid boxes only, and a box within 2.6 degrees of level was taken as level by
    /// the string over the top (Crossing). Bends are still not taken in the earth. Per thread. Never set in
    /// the game; <see cref="OldBurialForLab"/> implies the first.</summary>
    [ThreadStatic] public static bool GroundNoObstacleForLab;

    /// <summary>Whether the scene has ground that stands in the way of a straight segment.</summary>
    private bool GroundCounts => !_ground.IsEmpty && !OldBurialForLab && !GroundNoObstacleForLab;

    /// <summary>
    /// Whether the straight segment from <paramref name="a"/> to <paramref name="b"/> goes through the earth:
    /// the terrain stands over it, in the vertical plane through them, by more than the joint somewhere
    /// (Ground.Profile). The ground passes nothing (its impedance is so far above air's that outdoor
    /// propagation treats it as a boundary, never one sound crosses), so such a segment is blocked, and what
    /// gets past is what bends over the ground's profile (<see cref="OverTheTop"/>) or round the boxes with
    /// every leg in the air. Within the joint is not in the way, so an end standing on the ground is not
    /// blocked by the ground it stands on.
    /// </summary>
    public bool ThroughTheGround(Vector3 a, Vector3 b) => GroundCounts && _ground.Profile(a, b, RouteJointMetres, null, out _, out _);

    /// <summary>The terrain's height at (x, z) as the routes have it, the highest tile's; NaN with none there (for the lab).</summary>
    public float GroundAt(float x, float z) => _ground.HeightAt(x, z);

    /// <summary>Whether a point is at or under the terrain's surface (within the joint): the ground is no box,
    /// and an edge there is in the earth, not the air (Buried, WaysRound).</summary>
    private bool InGround(Vector3 p) => !OldBurialForLab && _ground.Buries(p, RouteJointMetres);

    /// <summary>
    /// Every way round box <paramref name="i"/>, grown by <paramref name="grow"/> on every side, from
    /// <paramref name="a"/> to <paramref name="b"/> (Diffraction.PathDifferenceAroundBox), into
    /// <paramref name="ways"/>, less those that bend at or under the terrain (<see cref="InGround"/>), and the
    /// shortest left's path difference and the crossing its last leg leaves from. False when none is left.
    /// One rule for every search: a slab's underside graded into a hill, a wall's foot on the ground.
    /// </summary>
    private bool WaysRound(int i, Vector3 a, Vector3 b, List<(float D, Vector3 SourceSide, Vector3 Edge)> ways,
                           out float shortest, out Vector3 last, float grow = 0f)
    {
        ref readonly var s = ref _solids[i];
        ways.Clear();
        if (!Diffraction.PathDifferenceAroundBox(s.Center, s.Size + new Vector3(2f * grow), s.Rotation, a, b,
                                                 out shortest, out last, ways)) return false;
        if (_ground.IsEmpty || OldBurialForLab) return true;
        int kept = 0;
        float best = float.MaxValue;
        for (int k = 0; k < ways.Count; k++)
        {
            var w = ways[k];
            if (InGround(w.Edge) || (w.SourceSide != w.Edge && InGround(w.SourceSide))) continue;
            ways[kept++] = w;
            if (w.D < best) { best = w.D; last = w.Edge; }
        }
        ways.RemoveRange(kept, ways.Count - kept);
        if (kept == 0) return false;
        shortest = best;
        return true;
    }

    private static string Db3(Vector3 g) => $"{DbOf(g.X):F0}/{DbOf(g.Y):F0}/{DbOf(g.Z):F0}";
    private static float DbOf(float g) => 20f * MathF.Log10(MathF.Max(1e-5f, g));

    /// <summary>Maekawa's loss for a detour (Diffraction.BandGains) and the spreading over the longer way.</summary>
    private static Vector3 Round(float pathDifference, float dist, float speedOfSound)
    {
        var (l, m, h) = Diffraction.BandGains(pathDifference, speedOfSound);
        return new Vector3(l, m, h) * (dist / (dist + pathDifference));
    }

    /// <summary>
    /// What the legs of a way round box <paramref name="skip"/> pass through, per band, and how much further
    /// they bend (<paramref name="extra"/>, metres): from the bend to the ear, across the box, and from the
    /// bend back to the source (<see cref="PaidLeg"/>). Each leg starts at the bend, so what stands against
    /// the edge (a stair's guard in two pieces) is gone round and what stands further off is paid. Each box
    /// the bend is buried in (<paramref name="buriedIn"/>) is paid once, by the leg that runs furthest inside
    /// it, and never bent round: it is what the way goes through. One no leg touches (the edge within the
    /// joint of it) is paid as its panel all the same: going round the edge goes through it.
    /// </summary>
    private Vector3 LegsThrough(Vector3 source, Vector3 sourceSide, Vector3 edge, Vector3 listener, int skip, float d,
                                float dist, float speedOfSound, int[]? ignoreA, int[]? ignoreB,
                                List<int>? buriedIn, out float extra, out bool blocked)
    {
        var scratch = Scratch.Get(this);
        var notOn = scratch.NotOnLeg;
        for (int l = 0; l < 3; l++) notOn[l].Clear();
        var through = buriedIn is { Count: > 0 } ? buriedIn : null;
        var alone = Vector3.One;
        extra = 0f;
        blocked = false;
        if (through != null)
        {
            foreach (int box in through)
            {
                // Legs 0 (bend to ear), 1 (across the box) and 2 (back to the source): the one deepest in it.
                float c0 = Inside(box, edge, listener), c1 = sourceSide != edge ? Inside(box, edge, sourceSide) : -1f, c2 = Inside(box, sourceSide, source);
                int payer = c0 >= c1 && c0 >= c2 ? 0 : c1 >= c2 ? 1 : 2;
                for (int l = 0; l < 3; l++) if (l != payer) notOn[l].Add(box);
                // Where the paying leg leaves its bend into the box, which way the sound comes from or goes
                // on to in the air there, and so which face of the box it passes (Passage).
                if (payer == 1) continue;
                Vector3 at = payer == 0 ? edge : sourceSide;
                Vector3 air = payer == 0 ? (sourceSide != edge ? sourceSide : source) : (sourceSide != edge ? edge : listener);
                if (Vector3.DistanceSquared(air, at) < 1e-10f) continue;
                switch (Passage(box, skip, at, Vector3.Normalize(air - at)))
                {
                    case PassageKind.ThroughOwner:
                        blocked = true;
                        return Vector3.Zero;
                    case PassageKind.ByEdge:
                        // Gone into the box by its edge: what the leg runs through of it, never less than its panel.
                        float run = payer == 0 ? c0 : c2;
                        ref readonly var s = ref _solids[box];
                        WallTransmission.Faces(s.Size, out float thickness, out _, out _);
                        if (run <= thickness) break;
                        var c = Constructions.Of(0f, run, AcousticRegistry.GetProperties(s.Material), s.Size, s.Build, 1, Vector3.Zero, _solidGains[box], run);
                        notOn[payer].Add(box);
                        alone *= Vector3.Min(c.Gains, _solidGains[box]);
                        scratch.Paid.Add(box);
                        break;
                }
            }
        }
        float x3 = PaidLeg(edge, listener, skip, -1, MaxChainedBoxes, d, dist, speedOfSound, ignoreA, ignoreB, notOn[0], through, out var t3);
        float x2 = 0f;
        var t2 = Vector3.One;
        if (sourceSide != edge) x2 = PaidLeg(edge, sourceSide, skip, skip, MaxChainedBoxes, d + x3, dist, speedOfSound, ignoreA, ignoreB, notOn[1], through, out t2);
        float x1 = PaidLeg(sourceSide, source, skip, -1, MaxChainedBoxes, d + x3 + x2, dist, speedOfSound, ignoreA, ignoreB, notOn[2], through, out var t1);
        extra = x1 + x2 + x3;
        if (through != null)
            foreach (int box in through)
                if (!scratch.Paid.Contains(box)) { alone *= _solidGains[box]; scratch.Paid.Add(box); }
        return t1 * t2 * t3 * alone;
    }

    private enum PassageKind { Undecided, ByFace, ByEdge, ThroughOwner }

    /// <summary>
    /// Which face of box <paramref name="box"/> the sound passes at a bend <paramref name="at"/> on the edge of
    /// box <paramref name="owner"/> buried in it, the air it comes from or goes on into lying in direction
    /// <paramref name="toAir"/>. Sound goes from the air into a solid, or out of it, only by a face open to the
    /// air: where the bend is at a corner of the box, a face against the owner (a slab's underside on the
    /// wall it bears on) passes nothing but what the owner lets through, and the way goes round the owner. By
    /// one of the box's broad faces it crosses the box as a panel (the brick of a front over the edge of the
    /// slab built into it); by a narrow one it has gone into the box by its edge (the 15 cm edge of a slab
    /// in a front, from the street) and pays the run (Constructions, "A solid gone into by its edge"); by
    /// none, only through the owner, it is no way.
    /// </summary>
    private PassageKind Passage(int box, int owner, Vector3 at, Vector3 toAir)
    {
        ref readonly var s = ref _solids[box];
        var f = _frames[box];
        if (!GeometryUtils.IsPointInOBB(at, s.Center, s.Size + new Vector3(2f * RouteJointMetres), s.Rotation)) return PassageKind.Undecided;
        var la = Vector3.Transform(at - f.Centre, f.Inverse);
        var q = Vector3.Transform(at + toAir * PassageStepMetres - f.Centre, f.Inverse);
        // The faces of the box the air beside the bend is outside of.
        Span<int> faces = stackalloc int[3];
        int n = 0;
        for (int k = 0; k < 3; k++)
        {
            float v = Comp(q, k), h = Comp(f.Half, k);
            if (MathF.Abs(v) > h) faces[n++] = v > 0f ? k + 1 : -(k + 1);
        }
        if (n == 0) return PassageKind.Undecided;
        // The bend on the box's surface, and the box's own frame back to the world.
        var corner = Vector3.Clamp(la, -f.Half, f.Half);
        var toWorld = Quaternion.Inverse(f.Inverse);
        ref readonly var o = ref _solids[owner];
        int thick = Constructions.ThicknessAxis(s.Size);
        bool byFace = false, byEdge = false;
        for (int a = 0; a < n; a++)
        {
            bool covered = false;
            // At a corner, a little way along this face from the edge: is the owner against it?
            for (int b = 0; b < n && !covered; b++)
            {
                if (b == a) continue;
                var r = corner + Normal(faces[a]) * (2f * Constructions.ContactMetres) - Normal(faces[b]) * RouteJointMetres;
                covered = GeometryUtils.IsPointInOBB(f.Centre + Vector3.Transform(r, toWorld), o.Center,
                                                     o.Size + new Vector3(2f * Constructions.ContactMetres), o.Rotation);
            }
            if (covered) continue;
            if (Math.Abs(faces[a]) - 1 == thick) byFace = true; else byEdge = true;
        }
        return byFace ? PassageKind.ByFace : byEdge ? PassageKind.ByEdge : PassageKind.ThroughOwner;

        static Vector3 Normal(int face)
        {
            float sign = face > 0 ? 1f : -1f;
            int k = Math.Abs(face) - 1;
            return k == 0 ? new Vector3(sign, 0, 0) : k == 1 ? new Vector3(0, sign, 0) : new Vector3(0, 0, sign);
        }
    }

    /// <summary>How far into the air beside a bend the face it passes is judged from, metres.</summary>
    private const float PassageStepMetres = 0.01f;

    private static float Comp(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>How far the segment runs inside box i, metres (0 for one that only touches it), or -1 if it
    /// misses it.</summary>
    private float Inside(int i, Vector3 a, Vector3 b)
        => SegmentSpan(i, a, b, 0f, out float t0, out float t1) ? (t1 - t0) * Vector3.Distance(a, b) : -1f;

    /// <summary>
    /// One straight leg from <paramref name="a"/> to <paramref name="b"/>, the better of paying everything it
    /// crosses (one construction where solids touch, <see cref="Constructions.Through"/>) and bending round the
    /// first thing it meets from <paramref name="a"/> and doing the same with the legs of that bend, up to
    /// <paramref name="chain"/> more boxes; better by the energy the whole way delivers, its detour so far
    /// <paramref name="detour"/>. Returns the extra metres, <paramref name="t"/> what is paid. A box counts if
    /// the leg touches it, faces included, so a leg run along the joint of a wall top and the slab on it pays
    /// both; not grown by the joint, as the clear search's tests are: grown, a leg crossing a front within
    /// 5 cm of where its slab meets its brick paid both, 30 dB more than either. <paramref name="ownerA"/>
    /// and <paramref name="ownerB"/> are the boxes the ends are on the edge of; <paramref name="notOn"/> are
    /// not paid on this leg, and <paramref name="through"/> are never bent round (LegsThrough).
    /// </summary>
    private float PaidLeg(Vector3 a, Vector3 b, int ownerA, int ownerB, int chain, float detour, float dist, float speedOfSound,
                          int[]? ignoreA, int[]? ignoreB, List<int>? notOn, List<int>? through, out Vector3 t)
    {
        t = Vector3.One;
        float len = Vector3.Distance(a, b);
        if (len < 1e-6f) return 0f;
        var scratch = Scratch.Get(this);
        var cand = scratch.Clear;
        _grid.Along(a, b, cand);
        var layers = scratch.LegLayers;
        layers.Clear();
        // What each way pays is added to Scratch.Paid; only the chosen way's stays.
        var paid = scratch.Paid;
        int start = paid.Count;
        int nearest = -1;
        float nearestT = float.MaxValue;
        foreach (int i in cand)
        {
            if (i == ownerA || i == ownerB) continue;
            if (ignoreA != null && Array.IndexOf(ignoreA, i) >= 0) continue;
            if (ignoreB != null && Array.IndexOf(ignoreB, i) >= 0) continue;
            if (notOn != null && notOn.Contains(i)) continue;
            if (!SegmentSpan(i, a, b, 0f, out float t0, out float t1)) continue;
            layers.Add(CrossingOf(i, a, b, t0, t1, len));
            paid.Add(i);
            if (through != null && through.Contains(i)) continue;
            if (t0 < nearestT) { nearestT = t0; nearest = i; }
        }
        // Through the earth the leg passes nothing; bent round a box it may yet clear the ground.
        bool earth = ThroughTheGround(a, b);
        if (layers.Count == 0)
        {
            if (earth) t = Vector3.Zero;
            return 0f;
        }
        t = earth ? Vector3.Zero : Constructions.Through(layers, (b - a) / len);
        if (nearest < 0 || chain <= 0 || scratch.ChainBudget <= 0 || _solids[nearest].IsLeaf || nearestT * len > BendReachMetres) return 0f;

        // Round the first thing in the way, its own legs paid or bent in turn.
        scratch.ChainBudget--;
        float bestX = 0f;
        var r0 = Round(detour, dist, speedOfSound) * t;
        float bestScore = Vector3.Dot(r0, r0);
        var ways = scratch.Ways[chain];
        if (!WaysRound(nearest, a, b, ways, out _, out _, RouteJointMetres)) return 0f;
        ways.Sort((x, y) => x.D.CompareTo(y.D));
        var kept = scratch.PaidKept[chain];
        kept.Clear();
        for (int k = start; k < paid.Count; k++) kept.Add(paid[k]);
        int tried = 0;
        foreach (var (d, ps, pe) in ways)
        {
            if (++tried > ChainRoutesPerBox) break;
            paid.RemoveRange(start, paid.Count - start);
            float x1 = PaidLeg(a, ps, ownerA, nearest, chain - 1, detour + d, dist, speedOfSound, ignoreA, ignoreB, notOn, through, out var t1);
            float x2 = 0f;
            var t2 = Vector3.One;
            if (ps != pe) x2 = PaidLeg(ps, pe, nearest, nearest, chain - 1, detour + d + x1, dist, speedOfSound, ignoreA, ignoreB, notOn, through, out t2);
            float x3 = PaidLeg(pe, b, nearest, ownerB, chain - 1, detour + d + x1 + x2, dist, speedOfSound, ignoreA, ignoreB, notOn, through, out var t3);
            float x = d + x1 + x2 + x3;
            var tt = t1 * t2 * t3;
            var g = Round(detour + x, dist, speedOfSound) * tt;
            float score = Vector3.Dot(g, g);
            if (score <= bestScore) continue;
            bestScore = score; bestX = x; t = tt;
            kept.Clear();
            for (int k = start; k < paid.Count; k++) kept.Add(paid[k]);
        }
        paid.RemoveRange(start, paid.Count - start);
        paid.AddRange(kept);
        return bestX;
    }

    /// <summary>Ways round one box and through the rest whose legs are traced, most promising first.</summary>
    private const int MaxThroughTried = 4;

    /// <summary>Ways round each box looked at for one whose edge is in the air.</summary>
    private const int BuriedLooksPerBox = 12;

    /// <summary>Boxes the legs of all those ways may bend round in all, a bound on cost.</summary>
    private const int ThroughBendsPerSearch = 4;

    /// <summary>How near the start of a leg, metres, what it runs into must stand to be gone round rather than
    /// paid: the other pieces of the guard or post the way bends over. Further off, the way round everything
    /// clear (BarrierPathDifference) is the search that bends.</summary>
    private const float BendReachMetres = 1f;

    // ═══ Round one obstacle ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// How far out of its way sound had to go from the source to the listener, metres, or -1 if nothing is
    /// in the way, and the edge the last leg leaves from. The single worst detour governs (the standards'
    /// single-worst-screen rule: a second screen stands in the first's shadow), searched round each box
    /// (Diffraction.PathDifferenceAroundBox). <paramref name="edgeVerified"/> says a route of that length
    /// exists clear of every other box; the shortest such is believed, and none can be shorter than the
    /// worst box's own way round. The ground is in the way too where the line goes through the earth, and
    /// is gone over, never round (<see cref="OverTheTop"/>); no leg of a way round a box may go through it.
    /// </summary>
    public float BarrierPathDifference(Vector3 source, Vector3 listener, out Vector3 edge, out bool edgeVerified,
                                       int[]? ignoreA = null, int[]? ignoreB = null)
    {
        var scratch = Scratch.Get(this);
        var cand = scratch.Candidates;
        _grid.Along(source, listener, cand);
        float worst = -1f;
        edge = listener;
        var routes = scratch.BarrierRoutes;
        routes.Clear();
        var boxRoutes = scratch.BoxRoutes;
        foreach (int i in cand)
        {
            if (ignoreA != null && Array.IndexOf(ignoreA, i) >= 0) continue;
            if (ignoreB != null && Array.IndexOf(ignoreB, i) >= 0) continue;
            if (!WaysRound(i, source, listener, boxRoutes, out float dd, out Vector3 p)) continue;
            foreach (var (rd, rs, re) in boxRoutes) routes.Add((rd, rs, re, i));
            if (dd <= worst) continue;
            worst = dd; edge = p;
        }

        // Every way round every box, for BarrierGains' ways round one box and through the rest.
        var all = scratch.AllBarrierRoutes;
        all.Clear();
        all.AddRange(routes);
        routes.RemoveAll(r => r.D < worst - 1e-3f);
        edgeVerified = false;
        if (routes.Count > 1) routes.Sort((x, y) => x.D.CompareTo(y.D));
        int tried = 0;
        float best = float.MaxValue;
        Vector3 bestEdge = edge;
        scratch.ChainBudget = ChainBendsPerSearch;
        foreach (var (dd, ps, p, i) in routes)
        {
            // Sorted, and bending round more boxes only ever adds: nothing after this can beat it.
            if (dd >= best) break;
            if (++tried > MaxRoutesTried) break;
            // Round a leaf standing in its doorway is through the doorway, which the opening's own
            // transmission (Route) already charges: a leaf 5 cm short of its lintel, as the city's glass
            // front doors are, let a PA on the street into Selby House's lobby at -7/-12/-20 dB over the
            // top edge, against the leaf's -18/-28/-45 (2026-10-09, --path-probe explain).
            if (_solids[i].IsLeaf && i < _leafInOpening.Length && _leafInOpening[i]) continue;
            // Round a leaf swung aside is believed only clear as it stands, never bent on round the street
            // (ClearedLeg), which found the crack over a shut glass door at -21 dB.
            int chain = _solids[i].IsLeaf ? 0 : MaxChainedBoxes;
            float extra = ClearedRoute(source, ps, p, listener, i, chain, ignoreA, ignoreB, best - dd, out Vector3 last);
            if (extra < 0f || dd + extra >= best) continue;
            best = dd + extra;
            bestEdge = last;
        }
        // Over the top of everything (ISO 9613-2, CNOSSOS-EU), whenever neither end has something over
        // its head, whatever the search round the sides found; and over the ground's profile when the line
        // goes through the earth, with or without a box on it.
        if (worst >= 0f || ThroughTheGround(source, listener))
        {
            float over = OverTheTop(source, listener, ignoreA, ignoreB, out Vector3 overEdge);
            if (over >= 0f && over < best) { best = over; bestEdge = overEdge; }
        }
        if (best == float.MaxValue) return worst;
        edge = bestEdge; edgeVerified = true;
        // Something is in the way, so at least grazing (Maekawa's 5 dB): a path difference of zero reads
        // as nothing, and a car whose line clipped a wall top came out 0 dB at one position, -5 the next.
        return MathF.Max(best, GrazingMetres);
    }

    /// <summary>The path difference a route that only grazes what is in its way is given, metres:
    /// small enough to be Maekawa's grazing figure in every band.</summary>
    private const float GrazingMetres = 1e-5f;

    /// <summary>
    /// The path difference of the way over every box between the two points in their vertical plane: the
    /// string pulled tight over the profile (upper convex hull), as road-traffic standards (ISO 9613-2,
    /// CNOSSOS-EU) go over several screens. A box a leg cuts through joins the profile and the string is
    /// pulled again. -1 when an end has a roof over it. <paramref name="lastEdge"/> is the top the last leg
    /// leaves from. The bounded search round the sides found a way round a building at one car position and
    /// missed it two metres on: -13 dB, then -76, a third of a second apart; the way over changes only as
    /// fast as the profile.
    /// <para>The ground is in the profile where it stands over the line (Ground.Profile, every corner of the
    /// heightfield along the plane), and holds up what stands on it: the string goes over a brow, a creek's
    /// bank or a cutting's side as over a screen, and over several ridges as over several screens (ISO 9613-2
    /// 7.4, the path difference over the outermost edges; CNOSSOS-EU, the convex hull of the profile).</para>
    /// </summary>
    private float OverTheTop(Vector3 source, Vector3 listener, int[]? ignoreA, int[]? ignoreB, out Vector3 lastEdge)
        => OverTheTop(source, listener, ignoreA, ignoreB, out lastEdge, null);

    private float OverTheTop(Vector3 source, Vector3 listener, int[]? ignoreA, int[]? ignoreB, out Vector3 lastEdge,
                             System.Text.StringBuilder? explain)
    {
        lastEdge = listener;
        var flat = new Vector2(listener.X - source.X, listener.Z - source.Z);
        float run = flat.Length();
        if (run < 0.1f) return -1f;
        var scratch = Scratch.Get(this);
        var cand = scratch.Clear;
        _grid.Column(source, listener, MathF.Min(source.Y, listener.Y) - 1f, cand);

        // ── The profile: what stands up from the ground in the plane ─────────────────────────
        //
        // A box is in the profile if it reaches down to the line or onto the top of one that does
        // (stacked storeys are one obstacle; a sign hung over the street is not). Swept bottom-up through
        // one-metre bins along the ground.
        var crossings = scratch.Crossings;
        crossings.Clear();
        foreach (int i in cand)
        {
            if (ignoreA != null && Array.IndexOf(ignoreA, i) >= 0) continue;
            if (ignoreB != null && Array.IndexOf(ignoreB, i) >= 0) continue;
            if (!Crossing(i, source, listener, run, out float s0, out float s1, out float bottom, out float top, out float topIn, out float topOut)) continue;
            float lineBottom = MathF.Min(Lerp(source.Y, listener.Y, s0 / run), Lerp(source.Y, listener.Y, s1 / run));
            if (top < lineBottom) continue;   // under the line all the way across: never touched
            crossings.Add((i, s0, s1, bottom, top, topIn, topOut));
        }
        // The ground's profile where it stands over the line (Ground.Profile), and the string over it alone:
        // only the corners of that string can be corners of the string over everything.
        var groundTops = scratch.GroundTops;
        groundTops.Clear();
        if (GroundCounts)
        {
            var profile = scratch.GroundProfile;
            profile.Clear();
            _ground.Profile(source, listener, RouteJointMetres, profile, out _, out _);
            if (profile.Count > 0)
            {
                // Off the ends' own columns, and a joint over the ground, as the boxes' tops are: the string
                // starts and ends at the ends, and on the surface it ran along the roads and lawns laid on the
                // ground and was cut by them.
                for (int k = 0; k < profile.Count; k++)
                    profile[k] = (Math.Clamp(profile[k].S, 1e-3f, run - 1e-3f), profile[k].H + RouteJointMetres);
                profile.Add((0f, source.Y));
                profile.Add((run, listener.Y));
                profile.Sort((x, y) => x.S != y.S ? x.S.CompareTo(y.S) : x.H.CompareTo(y.H));
                UpperHull(profile, groundTops);
                // Its two ends are the source and the ear, added with the boxes' tops below.
                groundTops.RemoveAt(groundTops.Count - 1);
                groundTops.RemoveAt(0);
            }
        }
        if (crossings.Count == 0 && groundTops.Count == 0) return -1f;
        crossings.Sort((x, y) => x.Bottom.CompareTo(y.Bottom));
        int bins = Math.Clamp((int)MathF.Ceiling(run / ProfileBinMetres), 1, 4096);
        var held = scratch.ProfileBins;
        if (held.Length < bins) held = scratch.ProfileBins = new float[Math.Max(bins, 2 * held.Length)];
        // What holds a box up at each bin: the line itself to begin with, or the ground where it stands
        // over the line (a house on the brow of the hill in the way).
        for (int k = 0; k < bins; k++)
        {
            float sMid = (k + 0.5f) * run / bins;
            held[k] = Lerp(source.Y, listener.Y, sMid / run);
            if (groundTops.Count > 0)
            {
                var q = Vector3.Lerp(source, listener, sMid / run);
                float g = _ground.HeightAt(q.X, q.Z);
                if (g > held[k]) held[k] = g;
            }
        }
        var included = scratch.Included;
        included.Clear();
        // Until nothing more is held up: two roofs side by side hold each other up, and one sweep made the
        // result depend on the index's order (the geometry parity harness, 2026-10-06).
        for (bool more = true; more;)
        {
            more = false;
            foreach (var c in crossings)
            {
                if (included.Contains(c.Box)) continue;
                int k0 = Math.Clamp((int)(c.S0 / run * bins), 0, bins - 1), k1 = Math.Clamp((int)(c.S1 / run * bins), 0, bins - 1);
                float support = float.MinValue;
                for (int k = k0; k <= k1; k++) support = MathF.Max(support, held[k]);
                if (c.Bottom > support + 2f * RouteJointMetres) continue;
                included.Add(c.Box);
                for (int k = k0; k <= k1; k++) held[k] = MathF.Max(held[k], c.Top);
                more = true;
            }
        }
        if (DebugOverTheTop != null)
            foreach (var c in crossings)
                DebugOverTheTop($"crossing {_solids[c.Box].Center} size {_solids[c.Box].Size} s {c.S0:F3}-{c.S1:F3} y {c.Bottom:F3}-{c.Top:F3} included {included.Contains(c.Box)}");
        if (included.Count == 0 && groundTops.Count == 0) return -1f;
        // Something over an end's head (a ceiling, a canopy): the way out is sideways first, the side
        // search's and the openings' business.
        bool OverAnEnd(float s0, float s1, float bottom)
            => (s0 <= EndColumnMetres && bottom > source.Y) || (s1 >= run - EndColumnMetres && bottom > listener.Y);
        foreach (var c in crossings)
            if (included.Contains(c.Box) && OverAnEnd(c.S0, c.S1, c.Bottom)) return -1f;

        var pts = scratch.Profile;
        var hull = scratch.Hull;
        Vector3 At(float s, float y) { var q = Vector3.Lerp(source, listener, s / run); q.Y = y; return q; }
        for (int pass = 0; pass < OverTheTopPasses; pass++)
        {
            pts.Clear();
            pts.Add((0f, source.Y, -1));
            pts.Add((run, listener.Y, -1));
            foreach (var c in crossings)
            {
                if (!included.Contains(c.Box)) continue;
                // A box over an end's own column puts its top straight above that end: the string can
                // only leave it upward through the box, which the leg test below finds.
                pts.Add((MathF.Max(c.S0, 1e-3f), c.TopIn + RouteJointMetres, c.Box));
                pts.Add((MathF.Min(c.S1, run - 1e-3f), c.TopOut + RouteJointMetres, c.Box));
            }
            foreach (var (gs, gh) in groundTops) pts.Add((gs, gh, GroundTop));
            // Then by height too: two at one distance otherwise came in the index's order.
            pts.Sort((x, y) => x.S != y.S ? x.S.CompareTo(y.S) : x.Y.CompareTo(y.Y));
            // Upper hull, left to right (Andrew's monotone chain).
            hull.Clear();
            foreach (var p in pts)
            {
                while (hull.Count >= 2)
                {
                    var o = hull[^2]; var a = hull[^1];
                    float cross = (a.S - o.S) * (p.Y - o.Y) - (a.Y - o.Y) * (p.S - o.S);
                    if (cross >= 0f) hull.RemoveAt(hull.Count - 1); else break;
                }
                hull.Add(p);
            }
            float length = 0f;
            int blocker = -1, cutAt = -1;
            for (int k = 0; k + 1 < hull.Count; k++)
            {
                Vector3 a = At(hull[k].S, hull[k].Y), b = At(hull[k + 1].S, hull[k + 1].Y);
                int hit = FirstHit(a, b, hull[k].Box, hull[k + 1].Box, ignoreA, ignoreB);
                if (hit >= 0) { blocker = hit; cutAt = k; break; }
                length += Vector3.Distance(a, b);
            }
            if (blocker < 0)
            {
                if (hull.Count > 2) lastEdge = At(hull[^2].S, hull[^2].Y);
                if (explain != null)
                    foreach (var h in hull)
                        if (h.Box == GroundTop)
                            explain.Append($"          over the ground at {h.S:F1} m, {h.Y:F2} m up\n");
                        else if (h.Box >= 0)
                            explain.Append($"          over box {h.Box} {_solids[h.Box].Material} ({_solids[h.Box].Center.X:F1}, {_solids[h.Box].Center.Y:F2}, {_solids[h.Box].Center.Z:F1}) size ({_solids[h.Box].Size.X:F2}, {_solids[h.Box].Size.Y:F2}, {_solids[h.Box].Size.Z:F2}) at {h.S:F1} m, {h.Y:F2} m up\n");
                return MathF.Max(0f, length - Vector3.Distance(source, listener));
            }
            explain?.Append($"          over the top, pass {pass}: cut by box {blocker} {_solids[blocker].Material} ({_solids[blocker].Center.X:F1}, {_solids[blocker].Center.Y:F2}, {_solids[blocker].Center.Z:F1}) size ({_solids[blocker].Size.X:F2}, {_solids[blocker].Size.Y:F2}, {_solids[blocker].Size.Z:F2}) turned {_solids[blocker].Rotation}, "
                          + $"from {hull[cutAt].S:F2} m, {hull[cutAt].Y:F3} up ({hull[cutAt].Box}) to {hull[cutAt + 1].S:F2} m, {hull[cutAt + 1].Y:F3} up ({hull[cutAt + 1].Box})\n");
            // Something the string was already over, still in its way: an end is under it.
            if (!included.Add(blocker)) return -1f;
            bool known = false;
            foreach (var c in crossings) if (c.Box == blocker) { known = true; break; }
            if (!known)
            {
                if (!Crossing(blocker, source, listener, run, out float s0, out float s1, out float bottom, out float top, out float topIn, out float topOut)) return -1f;
                if (OverAnEnd(s0, s1, bottom)) return -1f;
                crossings.Add((blocker, s0, s1, bottom, top, topIn, topOut));
            }
        }
        return -1f;
    }

    /// <summary>What a corner of the string over the top that is the ground's, not a box's, is marked by.</summary>
    private const int GroundTop = -2;

    /// <summary>The upper convex hull of points sorted by distance along (then height), left to right
    /// (Andrew's monotone chain): the string pulled tight over them.</summary>
    private static void UpperHull(List<(float S, float H)> sorted, List<(float S, float H)> hull)
    {
        hull.Clear();
        foreach (var p in sorted)
        {
            while (hull.Count >= 2)
            {
                var o = hull[^2]; var a = hull[^1];
                if ((a.S - o.S) * (p.H - o.H) - (a.H - o.H) * (p.S - o.S) >= 0f) hull.RemoveAt(hull.Count - 1); else break;
            }
            hull.Add(p);
        }
    }

    /// <summary>How near an end, along the ground, a box's crossing must reach to stand over it, metres.</summary>
    private const float EndColumnMetres = 0.01f;

    /// <summary>Width of the bins the profile is swept through, metres.</summary>
    private const float ProfileBinMetres = 1f;

    /// <summary>
    /// Times the string over the top is pulled again over something hung above the line that it was found
    /// to cut. Each pass adds a box or gives up, so this only bounds a pathological scene. It was 4: a
    /// multi-storey car park is a parapet and a slab a storey, and from the Main Street pavement a car on
    /// Wharf Avenue 200 m off had the way over it for a metre, none for the next eight, -25 then -90 dB,
    /// 56 pops a minute (2026-10-11, --pop-hunt).
    /// </summary>
    private const int OverTheTopPasses = 256;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Where the vertical plane through the two points crosses box i, as distances along the
    /// ground from the source, the box's top where the plane goes in and out (<paramref name="topIn"/>,
    /// <paramref name="topOut"/>), and its highest top and lowest bottom between. False if it does not.</summary>
    private bool Crossing(int i, Vector3 source, Vector3 listener, float run, out float s0, out float s1, out float bottom, out float top,
                          out float topIn, out float topOut)
    {
        var f = _frames[i];
        s0 = 0f; s1 = run; top = 0f; bottom = 0f; topIn = topOut = 0f;
        // Grown by the joint, as every leg test is: a string that passes within it of a box is in it.
        var half = f.Half + new Vector3(RouteJointMetres);
        var rot = Quaternion.Inverse(f.Inverse);
        Vector3 up = Vector3.Transform(Vector3.UnitY, rot);
        if (MathF.Abs(up.Y) > 0.999f)
        {
            // Turned about the vertical only: its footprint is its own X and Z.
            Vector3 la = Vector3.Transform(new Vector3(source.X, f.Centre.Y, source.Z) - f.Centre, f.Inverse);
            Vector3 lb = Vector3.Transform(new Vector3(listener.X, f.Centre.Y, listener.Z) - f.Centre, f.Inverse);
            float t0 = 0f, t1 = 1f;
            if (!Slab(la.X, lb.X - la.X, half.X, ref t0, ref t1) || !Slab(la.Z, lb.Z - la.Z, half.Z, ref t0, ref t1)) return false;
            s0 = t0 * run; s1 = t1 * run;
            if (GroundNoObstacleForLab || up.Y == 1f)
            {
                top = topIn = topOut = f.Centre.Y + f.Half.Y;
                bottom = f.Centre.Y - f.Half.Y;
                return true;
            }
            // Nearly level is not level: its top and bottom faces' planes where the plane goes in and out, the
            // top grown by the joint as the leg test grows it (a joint along its tilted up, a little more than
            // a joint straight up). A road segment 16 m long pitched 1.1 degrees up a slope has one end 0.15 m
            // over its middle, and the string laid level over the middle went through that end and was given up.
            float j = RouteJointMetres;
            topIn = FaceAt(f, up, Vector3.Lerp(source, listener, s0 / run), f.Half.Y + j) - j + 1e-4f;
            topOut = FaceAt(f, up, Vector3.Lerp(source, listener, s1 / run), f.Half.Y + j) - j + 1e-4f;
            top = MathF.Max(topIn, topOut);
            bottom = MathF.Min(FaceAt(f, up, Vector3.Lerp(source, listener, s0 / run), -f.Half.Y),
                               FaceAt(f, up, Vector3.Lerp(source, listener, s1 / run), -f.Half.Y));
            return true;
        }
        // Tilted: its world bounds, which is more than it covers and so never lets the string through it.
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var corner = f.Centre + Vector3.Transform(new Vector3((c & 1) == 0 ? -f.Half.X : f.Half.X,
                                                                  (c & 2) == 0 ? -f.Half.Y : f.Half.Y,
                                                                  (c & 4) == 0 ? -f.Half.Z : f.Half.Z), rot);
            min = Vector3.Min(min, corner); max = Vector3.Max(max, corner);
        }
        float u0 = 0f, u1 = 1f;
        if (!Slab(source.X - (min.X + max.X) * 0.5f, listener.X - source.X, (max.X - min.X) * 0.5f + RouteJointMetres, ref u0, ref u1)
            || !Slab(source.Z - (min.Z + max.Z) * 0.5f, listener.Z - source.Z, (max.Z - min.Z) * 0.5f + RouteJointMetres, ref u0, ref u1)) return false;
        s0 = u0 * run; s1 = u1 * run;
        top = topIn = topOut = max.Y;
        bottom = min.Y;
        return true;
    }

    /// <summary>The height over (q.X, q.Z) of the plane of a box's face <paramref name="h"/> along its own up
    /// from its centre (its top at h = half its height).</summary>
    private static float FaceAt(in Frame f, Vector3 up, Vector3 q, float h)
        => f.Centre.Y + (h - up.X * (q.X - f.Centre.X) - up.Z * (q.Z - f.Centre.Z)) / up.Y;

    private static bool Slab(float o, float d, float h, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-9f) return o >= -h && o <= h;
        float ta = (-h - o) / d, tb = (h - o) / d;
        if (ta > tb) (ta, tb) = (tb, ta);
        t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
        return t0 <= t1;
    }

    /// <summary>The boxes a straight leg runs through, with what each lets through (the parity harness's
    /// look at a leg).</summary>
    public List<(Solid Box, Vector3 Gains)> LegBoxes(Vector3 a, Vector3 b)
    {
        var into = new List<int>();
        _grid.Along(a, b, into);
        var list = new List<(Solid, Vector3)>();
        foreach (int i in into) if (SegmentHits(i, a, b)) list.Add((_solids[i], _solidGains[i]));
        return list;
    }

    /// <summary>The boxes the index hands back for the vertical plane between two points (the parity
    /// harness's look at what over-the-top is given).</summary>
    public List<Solid> ColumnCandidates(Vector3 a, Vector3 b, float fromY)
    {
        var into = new List<int>();
        _grid.Column(a, b, fromY, into);
        var list = new List<Solid>();
        foreach (int i in into) list.Add(_solids[i]);
        return list;
    }

    /// <summary>For the lab: the barrier search spelled out, every box on the line with its shortest way
    /// round, and for the routes tried, which other box (if any) each ran into.</summary>
    public string ExplainBarrier(Vector3 source, Vector3 listener)
    {
        var sb = new System.Text.StringBuilder();
        var cand = new List<int>();
        _grid.Along(source, listener, cand);
        var routes = new List<(float D, Vector3 S, Vector3 E, int Box)>();
        var boxRoutes = new List<(float D, Vector3 SourceSide, Vector3 Edge)>();
        float worst = -1f;
        foreach (int i in cand)
        {
            ref readonly var b = ref _solids[i];
            if (!WaysRound(i, source, listener, boxRoutes, out float dd, out _)) continue;
            sb.Append($"        box {i} {b.Material} centre ({b.Center.X:F1}, {b.Center.Y:F2}, {b.Center.Z:F1}) size ({b.Size.X:F2}, {b.Size.Y:F2}, {b.Size.Z:F2}): round it {dd:F3} m\n");
            foreach (var (rd, rs, re) in boxRoutes) routes.Add((rd, rs, re, i));
            worst = MathF.Max(worst, dd);
        }
        routes.RemoveAll(r => r.D < worst - 1e-3f);
        routes.Sort((x, y) => x.D.CompareTo(y.D));
        foreach (var (dd, ps, p, i) in routes.Take(MaxRoutesTried))
        {
            if (_solids[i].IsLeaf && i < _leafInOpening.Length && _leafInOpening[i])
            {
                sb.Append($"        route round {i}, {dd:F3} m via ({p.X:F2}, {p.Y:F2}, {p.Z:F2}): a leaf standing in its doorway, the doorway's to charge\n");
                continue;
            }
            string Hit(Vector3 a, Vector3 b2)
            {
                var c = new List<int>();
                _grid.Along(a, b2, c);
                foreach (int j in c)
                    if (j != i && SegmentHits(j, a, b2, RouteJointMetres))
                        return $"box {j} {_solids[j].Material} ({_solids[j].Center.X:F1}, {_solids[j].Center.Y:F2}, {_solids[j].Center.Z:F1}) size ({_solids[j].Size.X:F2}, {_solids[j].Size.Y:F2}, {_solids[j].Size.Z:F2})";
                return "clear";
            }
            Scratch.Get(this).ChainBudget = ChainBendsPerSearch;
            float extra = ClearedRoute(source, ps, p, listener, i, MaxChainedBoxes, null, null, float.MaxValue, out _);
            sb.Append($"        route round {i}, {dd:F3} m via ({p.X:F2}, {p.Y:F2}, {p.Z:F2}): to ear {Hit(p, listener)}; from source {Hit(source, ps)}"
                    + (ps != p ? $"; across {Hit(ps, p)}" : "") + $"; bent round the rest: {(extra < 0f ? "no way" : $"+{extra:F3} m")}\n");
        }
        float over = OverTheTop(source, listener, null, null, out var oe, sb);
        sb.Append($"        over the top: {(over < 0f ? "no way" : $"{over:F3} m, last top ({oe.X:F1}, {oe.Y:F2}, {oe.Z:F1})")}\n");
        var gains = BarrierGains(source, listener, WallTransmission.SoundSpeed, out _, out _, out var ge, out bool found, null, null, sb);
        sb.Append($"        what bends round, both kinds: {(found ? $"{Db3(gains)} dB, from ({ge.X:F1}, {ge.Y:F2}, {ge.Z:F1})" : "no way")}\n");
        return sb.ToString();
    }

    /// <summary>A route clear of the whole scene costs a pass over the boxes near it; past the few
    /// shortest, the rest are ways round walls deeper in.</summary>
    private const int MaxRoutesTried = 6;

    /// <summary>How much larger every other box is taken to be when a route round one box is checked
    /// against it, metres: a route along the joint of a wall top and its slab is a crack the building does
    /// not have.</summary>
    private const float RouteJointMetres = 0.05f;

    /// <summary>
    /// How many more boxes a route round one box may bend round when its legs run into them. "Pops" from
    /// the Main Street pavement (2026-10-03): a park's 1.1 m wall with a 0.5 m pier every few metres, a car
    /// 160 m beyond; as the leg to the ear crossed at a pier or between two, the car flipped between -7 dB
    /// and -80 for a third of a second at a time. Still blocked after this many, it is a building.
    /// </summary>
    internal const int MaxChainedBoxes = 2;

    /// <summary>Boxes one barrier search may bend its routes round in all, whichever routes they are on: a
    /// bound on cost, spent first on the shortest routes.</summary>
    private const int ChainBendsPerSearch = 6;

    /// <summary>Ways round each box in the way that are tried, shortest first.</summary>
    private const int ChainRoutesPerBox = 2;

    /// <summary>
    /// The extra length a route round box <paramref name="skip"/> needs to be clear of everything else —
    /// its three legs, source to its first crossing, across the box, and from its last crossing to the
    /// ear, each bent round whatever OTHER box it runs into — or -1 if no such route exists within
    /// <paramref name="chain"/> more boxes. 0 is a route clear as it stands. <paramref name="lastEdge"/>
    /// is where its last leg leaves for the ear.
    /// </summary>
    private float ClearedRoute(Vector3 source, Vector3 sourceSide, Vector3 edge, Vector3 listener, int skip,
                               int chain, int[]? ignoreA, int[]? ignoreB, float limit, out Vector3 lastEdge)
    {
        lastEdge = edge;
        float toEar = ClearedLeg(edge, listener, skip, -1, chain, ignoreA, ignoreB, limit, out var bend);
        if (toEar < 0f) return -1f;
        if (bend is { } b) lastEdge = b;
        float fromSource = ClearedLeg(source, sourceSide, -1, skip, chain, ignoreA, ignoreB, limit - toEar, out _);
        if (fromSource < 0f) return -1f;
        float across = 0f;
        if (sourceSide != edge)
        {
            across = ClearedLeg(sourceSide, edge, skip, skip, chain, ignoreA, ignoreB, limit - toEar - fromSource, out _);
            if (across < 0f) return -1f;
        }
        return toEar + fromSource + across;
    }

    /// <summary>
    /// The extra length the straight leg from <paramref name="a"/> to <paramref name="b"/> needs to get
    /// past every box in its way — 0 when nothing is, -1 when it cannot within <paramref name="chain"/>
    /// boxes or <paramref name="limit"/> metres. <paramref name="ownerA"/> and <paramref name="ownerB"/>
    /// are the boxes the ends sit on the edge of, which the leg is allowed to touch. Each box in the way is
    /// gone round by its own shortest ways (Diffraction.PathDifferenceAroundBox), grown by the joint, so a
    /// route cannot slip through the crack between two boxes that meet.
    /// </summary>
    private float ClearedLeg(Vector3 a, Vector3 b, int ownerA, int ownerB, int chain,
                             int[]? ignoreA, int[]? ignoreB, float limit, out Vector3? lastBend)
    {
        lastBend = null;
        int hit = FirstHit(a, b, ownerA, ownerB, ignoreA, ignoreB);
        // Clear of every box but through the earth: no way here; over the ground is the string's (OverTheTop).
        if (hit < 0) return ThroughTheGround(a, b) ? -1f : 0f;
        var scratch = Scratch.Get(this);
        if (chain <= 0 || scratch.ChainBudget <= 0 || limit <= 0f) return -1f;
        // Round a door leaf is through its doorway, the openings' business (Route): bent round here it
        // was a crack beside a shut glass door at -12 dB.
        if (_solids[hit].IsLeaf) return -1f;
        scratch.ChainBudget--;
        // One list per depth: the loop below recurses while it walks this one.
        var ways = scratch.Ways[chain];
        if (!WaysRound(hit, a, b, ways, out _, out _, RouteJointMetres)) return -1f;
        ways.Sort((x, y) => x.D.CompareTo(y.D));
        float best = -1f;
        int tried = 0;
        foreach (var (d, ps, pe) in ways)
        {
            if (best >= 0f && d >= best) break;
            if (d >= limit) break;
            if (++tried > ChainRoutesPerBox) break;
            float cap = (best >= 0f ? MathF.Min(best, limit) : limit) - d;
            float x1 = ClearedLeg(a, ps, ownerA, hit, chain - 1, ignoreA, ignoreB, cap, out _);
            if (x1 < 0f) continue;
            float x2 = ps != pe ? ClearedLeg(ps, pe, hit, hit, chain - 1, ignoreA, ignoreB, cap - x1, out _) : 0f;
            if (x2 < 0f) continue;
            float x3 = ClearedLeg(pe, b, hit, ownerB, chain - 1, ignoreA, ignoreB, cap - x1 - x2, out var bend);
            if (x3 < 0f) continue;
            float total = d + x1 + x2 + x3;
            if (best >= 0f && total >= best) continue;
            best = total;
            lastBend = bend ?? pe;
        }
        return best;
    }

    /// <summary>The first box, other than the two the ends belong to, that the segment runs into (grown by
    /// the joint), or -1.</summary>
    private int FirstHit(Vector3 a, Vector3 b, int ownerA, int ownerB, int[]? ignoreA, int[]? ignoreB)
    {
        var cand = Scratch.Get(this).Clear;
        _grid.Along(a, b, cand);
        int first = -1;
        float nearest = float.MaxValue;
        foreach (int i in cand)
        {
            if (i == ownerA || i == ownerB) continue;
            if (ignoreA != null && Array.IndexOf(ignoreA, i) >= 0) continue;
            if (ignoreB != null && Array.IndexOf(ignoreB, i) >= 0) continue;
            if (!SegmentHits(i, a, b, RouteJointMetres)) continue;
            float at = Vector3.DistanceSquared(a, _solids[i].Center);
            if (at < nearest) { nearest = at; first = i; }
        }
        return first;
    }

    // ═══ Scratch, one per thread ═════════════════════════════════════════════════════════════════

    private sealed class SideTree
    {
        public float[] Dist = Array.Empty<float>();
        public int[] Prev = Array.Empty<int>();
        public readonly List<(int State, float Dist)> Reached = new();
        public readonly PriorityQueue<int, float> Queue = new();
        private OpeningRoutes? _owner;

        public void Reset(int openings)
        {
            int states = openings * 2;
            if (Dist.Length < states) { Dist = new float[states]; Prev = new int[states]; }
            Array.Fill(Dist, float.MaxValue, 0, states);
            Reached.Clear();
            Queue.Clear();
        }

        public void Bind(OpeningRoutes owner) => _owner = owner;

        /// <summary>The node a state's opening is entered from.</summary>
        public int From(int state)
        {
            var o = _owner!._openings[state >> 1];
            return (state & 1) == 0 ? o.NodeA : o.NodeB;
        }
    }

    private sealed class Scratch
    {
        [ThreadStatic] private static Scratch? _current;
        private OpeningRoutes? _for;

        public readonly List<int> Candidates = new();
        public readonly List<int> Clear = new();
        public readonly SideTree ListenerTree = new(), SourceTree = new();
        public readonly List<Candidate> Routes = new();
        public readonly List<(int, float, Vector3)> ListenerExits = new(), SourceExits = new();
        public readonly HashSet<long> SeenRoutes = new();
        public readonly List<(int, int)> Chain = new();
        public readonly List<Vector3> Points = new();
        public readonly List<Vector3> Legs = new();
        public readonly List<(float D, Vector3 SourceSide, Vector3 Edge, int Box)> BarrierRoutes = new(), AllBarrierRoutes = new();
        public readonly List<(float Hope, float D, Vector3 SourceSide, Vector3 Edge, int Box)> ThroughCandidates = new();
        public readonly List<(int Box, Constructions.Crossing Crossing)> LineCrossings = new();
        public readonly List<Constructions.Crossing> Without = new();
        /// <summary>The boxes the way being weighed passes through (BarrierGains, PaidLeg).</summary>
        public readonly List<int> Paid = new();
        public readonly List<int>[] PaidKept =
            System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, MaxChainedBoxes + 1), _ => new List<int>()));
        public readonly Dictionary<int, int> PerBox = new();
        /// <summary>What each way round in ThroughCandidates is buried in, worked out when first asked.</summary>
        public readonly List<Burial> Burials = new();
        /// <summary>The way being weighed: the boxes its edge is buried in, and its construction's layers ending there.</summary>
        public readonly List<int> EdgeBoxes = new(), EdgeLayers = new();
        /// <summary>What each of a way's three legs does not pay (LegsThrough).</summary>
        public readonly List<int>[] NotOnLeg = { new(), new(), new() };
        public readonly List<(float D, Vector3 SourceSide, Vector3 Edge)> BoxRoutes = new();
        /// <summary>Boxes the current barrier search may still bend a route round (ClearedLeg).</summary>
        public int ChainBudget;
        public readonly List<(float S, float Y, int Box)> Profile = new(), Hull = new();
        /// <summary>The ground's profile over the line, and the corners of the string over it (OverTheTop).</summary>
        public readonly List<(float S, float H)> GroundProfile = new(), GroundTops = new();
        public readonly List<(int Box, float S0, float S1, float Bottom, float Top, float TopIn, float TopOut)> Crossings = new();
        public readonly List<Constructions.Crossing> Layers = new(), LegLayers = new();
        public readonly HashSet<int> Included = new();
        public float[] ProfileBins = new float[256];
        public readonly List<(float D, Vector3 SourceSide, Vector3 Edge)>[] Ways =
            System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(System.Linq.Enumerable.Range(0, MaxChainedBoxes + 1), _ => new List<(float D, Vector3 SourceSide, Vector3 Edge)>()));

        public static Scratch Get(OpeningRoutes owner)
        {
            var s = _current ??= new Scratch();
            if (!ReferenceEquals(s._for, owner))
            {
                s._for = owner;
                s.ListenerTree.Bind(owner);
                s.SourceTree.Bind(owner);
            }
            return s;
        }
    }

    // ═══ The boxes, by where they are ══════════════════════════════════════════════════════════════

    /// <summary>Does the segment from a to b pass through box i, made <paramref name="grow"/> metres
    /// larger on every side? A slab test in the box's own frame (Kay &amp; Kajiya 1986).</summary>
    private bool SegmentHits(int i, Vector3 a, Vector3 b, float grow = 0f) => SegmentSpan(i, a, b, grow, out _, out _);

    /// <summary>Whether the segment a-b passes through box i (grown by <paramref name="grow"/>), and where
    /// it goes in and out, as fractions of the way from a to b.</summary>
    private bool SegmentSpan(int i, Vector3 a, Vector3 b, float grow, out float t0, out float t1)
    {
        t0 = 0f; t1 = 1f;
        var f = _frames[i];
        Vector3 la = Vector3.Transform(a - f.Centre, f.Inverse), lb = Vector3.Transform(b - f.Centre, f.Inverse);
        Vector3 h = f.Half + new Vector3(grow);
        Vector3 d = lb - la;
        for (int ax = 0; ax < 3; ax++)
        {
            float o = ax == 0 ? la.X : ax == 1 ? la.Y : la.Z;
            float dd = ax == 0 ? d.X : ax == 1 ? d.Y : d.Z;
            float hh = ax == 0 ? h.X : ax == 1 ? h.Y : h.Z;
            if (MathF.Abs(dd) < 1e-9f)
            {
                if (o < -hh || o > hh) return false;
                continue;
            }
            float inv = 1f / dd;
            float ta = (-hh - o) * inv, tb = (hh - o) * inv;
            if (ta > tb) (ta, tb) = (tb, ta);
            if (ta > t0) t0 = ta;
            if (tb < t1) t1 = tb;
            if (t0 > t1) return false;
        }
        return true;
    }

    internal readonly record struct Frame(Vector3 Centre, Vector3 Half, Quaternion Inverse);

    // ═══ The ground ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The terrain under a point (docs/GEOMETRY.md 2.3), from the scene's tiles of ground: the highest tile
    /// over it. The terrain is no box, so the searches round boxes see it only here.
    /// </summary>
    private sealed class Ground
    {
        public static readonly Ground None = new(new List<(Geometry.Heightfield, Vector3)>());

        /// <summary>The cells a tile is filed under, metres.</summary>
        private const float Cell = 64f;
        private readonly List<(Geometry.Heightfield Field, Vector3 Corner)> _tiles;
        private readonly Dictionary<(int, int), List<int>> _cells = new();
        /// <summary>Each tile's blocks of <see cref="BlockCells"/> cells a side: the highest post of each, row by row.</summary>
        private readonly float[][] _blockTop;

        public bool IsEmpty => _tiles.Count == 0;

        private Ground(List<(Geometry.Heightfield Field, Vector3 Corner)> tiles)
        {
            _tiles = tiles;
            _blockTop = new float[tiles.Count][];
            for (int t = 0; t < tiles.Count; t++)
            {
                var f = tiles[t].Field;
                int cells = f.CellsPerSide, nb = (cells + BlockCells - 1) / BlockCells;
                var top = new float[nb * nb];
                for (int bj = 0; bj < nb; bj++)
                    for (int bi = 0; bi < nb; bi++)
                    {
                        float h = float.MinValue;
                        for (int j = bj * BlockCells; j <= Math.Min((bj + 1) * BlockCells, cells); j++)
                            for (int i = bi * BlockCells; i <= Math.Min((bi + 1) * BlockCells, cells); i++)
                                h = MathF.Max(h, f.HeightOfPost(i, j));
                        top[bj * nb + bi] = h;
                    }
                _blockTop[t] = top;
            }
            for (int t = 0; t < tiles.Count; t++)
            {
                var (f, c) = tiles[t];
                for (int x = Key(c.X); x <= Key(c.X + f.Size); x++)
                    for (int z = Key(c.Z); z <= Key(c.Z + f.Size); z++)
                    {
                        if (!_cells.TryGetValue((x, z), out var list)) _cells[(x, z)] = list = new List<int>(1);
                        list.Add(t);
                    }
            }
        }

        private static int Key(float v) => (int)MathF.Floor(v / Cell);

        /// <summary>The tiles held as solids (WorldSnapshot.TerrainSolids): heights in the world, post (0, 0) at the corner.</summary>
        public static Ground Of(IReadOnlyList<Geometry.SolidSpec>? terrains)
        {
            if (terrains == null || terrains.Count == 0) return None;
            var tiles = new List<(Geometry.Heightfield, Vector3)>();
            foreach (var t in terrains) if (t.Terrain != null) tiles.Add((t.Terrain, t.TerrainCorner));
            return tiles.Count == 0 ? None : new Ground(tiles);
        }

        /// <summary>The tiles a triangle store holds, each where its piece is placed.</summary>
        public static Ground Of(Geometry.TriangleWorld scene)
        {
            List<(Geometry.Heightfield, Vector3)>? tiles = null;
            for (int i = 0; i < scene.InstanceCount; i++)
            {
                ref readonly var inst = ref scene.Instance(i);
                if (inst.Piece.Terrain is not { } field || inst.Rotated) continue;
                (tiles ??= new()).Add((field, inst.ToWorld(inst.Piece.TerrainOffset)));
            }
            return tiles == null ? None : new Ground(tiles);
        }

        /// <summary>Whether <paramref name="p"/> is at or under the ground's surface, within <paramref name="slack"/>.</summary>
        public bool Buries(Vector3 p, float slack)
        {
            if (_tiles.Count == 0 || !_cells.TryGetValue((Key(p.X), Key(p.Z)), out var list)) return false;
            foreach (int t in list)
            {
                var (f, c) = _tiles[t];
                float h = f.HeightAt(p.X - c.X, p.Z - c.Z);
                if (!float.IsNaN(h) && p.Y <= h + c.Y + slack) return true;
            }
            return false;
        }

        /// <summary>The ground's height at (x, z), the highest tile's; NaN where there is none.</summary>
        public float HeightAt(float x, float z)
        {
            float best = float.NaN;
            if (_tiles.Count == 0 || !_cells.TryGetValue((Key(x), Key(z)), out var list)) return best;
            foreach (int t in list)
            {
                var (f, c) = _tiles[t];
                float h = f.HeightAt(x - c.X, z - c.Z) + c.Y;
                if (!float.IsNaN(h) && !(h <= best)) best = h;
            }
            return best;
        }

        /// <summary>Every tile's height at (x, z), for the lab.</summary>
        public string Describe(float x, float z)
        {
            if (!_cells.TryGetValue((Key(x), Key(z)), out var list)) return "none";
            var parts = new List<string>();
            foreach (int t in list)
            {
                var (f, c) = _tiles[t];
                float h = f.HeightAt(x - c.X, z - c.Z);
                if (!float.IsNaN(h)) parts.Add($"{h + c.Y:F2} (tile {t}, {f.Spacing:F1} m posts)");
            }
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        /// <summary>Cells a side of the blocks whose highest post lets <see cref="Profile"/> pass over them unwalked.</summary>
        private const int BlockCells = 8;

        [ThreadStatic] private static List<int>? _alongScratch;
        [ThreadStatic] private static List<float>? _spanScratch, _cornerScratch;

        /// <summary>
        /// The ground under the straight line from <paramref name="a"/> to <paramref name="b"/>, in the vertical
        /// plane through them: true when it stands over the line by more than <paramref name="slack"/> somewhere
        /// between them (the line goes through the earth). Every corner of the ground's profile that stands over
        /// the line, or within <paramref name="slack"/> under it, is added to <paramref name="into"/>, as (metres
        /// along the ground from a, height). Exact for
        /// the heightfield: over a tile the ground is flat within each triangle, so along a straight line it is
        /// straight between the places the line crosses a cell's side or its diagonal, and those are its
        /// corners. A block of cells whose highest post is under the line is passed over. <paramref name="most"/>
        /// is the most the ground stands over the line, metres, <paramref name="mostAt"/> where (for the lab).
        /// Without <paramref name="into"/> it stops at the first corner that blocks.
        /// </summary>
        public bool Profile(Vector3 a, Vector3 b, float slack, List<(float S, float H)>? into, out float most, out float mostAt)
        {
            most = float.MinValue; mostAt = 0f;
            if (_tiles.Count == 0) return false;
            float du = b.X - a.X, dw = b.Z - a.Z, dy = b.Y - a.Y;
            float run = MathF.Sqrt(du * du + dw * dw);
            var tiles = _alongScratch ??= new List<int>();
            TilesAlong(a, b, tiles);
            var spans = _spanScratch ??= new List<float>();
            var corners = _cornerScratch ??= new List<float>();
            bool blocked = false;
            foreach (int ti in tiles)
            {
                var (f, c) = _tiles[ti];
                float au = a.X - c.X, aw = a.Z - c.Z;
                float t0 = 0f, t1 = 1f;
                if (!Clip(au, du, f.Size, ref t0, ref t1) || !Clip(aw, dw, f.Size, ref t0, ref t1)) continue;
                if (f.MaxY + c.Y <= MathF.Min(a.Y + dy * t0, a.Y + dy * t1)) continue;
                float block = BlockCells * f.Spacing;
                int nb = (f.CellsPerSide + BlockCells - 1) / BlockCells;
                var tops = _blockTop[ti];
                spans.Clear();
                spans.Add(t0);
                Crossings(au, du, block, t0, t1, spans);
                Crossings(aw, dw, block, t0, t1, spans);
                spans.Add(t1);
                spans.Sort();
                for (int k = 0; k + 1 < spans.Count; k++)
                {
                    float ta = spans[k], tb = spans[k + 1];
                    float tm = 0.5f * (ta + tb);
                    int bi = Math.Clamp((int)MathF.Floor((au + du * tm) / block), 0, nb - 1);
                    int bj = Math.Clamp((int)MathF.Floor((aw + dw * tm) / block), 0, nb - 1);
                    if (tops[bj * nb + bi] + c.Y <= MathF.Min(a.Y + dy * ta, a.Y + dy * tb)) continue;
                    corners.Clear();
                    corners.Add(ta);
                    corners.Add(tb);
                    Crossings(au, du, f.Spacing, ta, tb, corners);
                    Crossings(aw, dw, f.Spacing, ta, tb, corners);
                    Crossings(au - aw, du - dw, f.Spacing, ta, tb, corners);
                    foreach (float t in corners)
                    {
                        float h = f.HeightAt(Math.Clamp(au + du * t, 0f, f.Size), Math.Clamp(aw + dw * t, 0f, f.Size));
                        if (float.IsNaN(h)) continue;
                        h += c.Y;
                        float over = h - (a.Y + dy * t);
                        if (over > most) { most = over; mostAt = t * run; }
                        if (over > slack)
                        {
                            blocked = true;
                            if (into == null) return true;
                        }
                        if (over + slack > 0f) into?.Add((t * run, h));
                    }
                }
            }
            return blocked;
        }

        /// <summary>The fractions of the way along where o + d t passes a multiple of <paramref name="step"/>,
        /// strictly between <paramref name="t0"/> and <paramref name="t1"/>.</summary>
        private static void Crossings(float o, float d, float step, float t0, float t1, List<float> into)
        {
            if (MathF.Abs(d) < 1e-9f) return;
            float v0 = o + d * t0, v1 = o + d * t1;
            int k0 = (int)MathF.Ceiling(MathF.Min(v0, v1) / step), k1 = (int)MathF.Floor(MathF.Max(v0, v1) / step);
            for (int k = k0; k <= k1; k++)
            {
                float t = (k * step - o) / d;
                if (t > t0 && t < t1) into.Add(t);
            }
        }

        /// <summary>Clips o + d t, t in [t0, t1], to [0, size].</summary>
        private static bool Clip(float o, float d, float size, ref float t0, ref float t1)
        {
            if (MathF.Abs(d) < 1e-9f) return o >= 0f && o <= size;
            float ta = -o / d, tb = (size - o) / d;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            return t0 <= t1;
        }

        /// <summary>The tiles filed under the cells the line from a to b passes over, each once (Amanatides
        /// and Woo's walk, on the ground).</summary>
        private void TilesAlong(Vector3 a, Vector3 b, List<int> into)
        {
            into.Clear();
            int x = Key(a.X), z = Key(a.Z), xe = Key(b.X), ze = Key(b.Z);
            float dx = b.X - a.X, dz = b.Z - a.Z;
            int sx = Math.Sign(dx), sz = Math.Sign(dz);
            float tdx = sx != 0 ? Cell / MathF.Abs(dx) : float.MaxValue, tdz = sz != 0 ? Cell / MathF.Abs(dz) : float.MaxValue;
            float tx = sx > 0 ? ((x + 1) * Cell - a.X) / dx : sx < 0 ? (x * Cell - a.X) / dx : float.MaxValue;
            float tz = sz > 0 ? ((z + 1) * Cell - a.Z) / dz : sz < 0 ? (z * Cell - a.Z) / dz : float.MaxValue;
            for (int guard = Math.Abs(xe - x) + Math.Abs(ze - z) + 2; guard >= 0; guard--)
            {
                if (_cells.TryGetValue((x, z), out var list))
                    foreach (int t in list)
                        if (!into.Contains(t)) into.Add(t);
                if (x == xe && z == ze) break;
                if (tx < tz) { x += sx; tx += tdx; } else { z += sz; tz += tdz; }
            }
        }
    }

    /// <summary>
    /// A uniform grid of cubes over the scene, each listing the boxes whose bounds (a hair larger than the
    /// box, so a route checked against slightly enlarged boxes still finds them) overlap it. A segment's
    /// candidates are the boxes in the cells it passes through, walked cell by cell (Amanatides &amp; Woo,
    /// "A Fast Voxel Traversal Algorithm for Ray Tracing", Eurographics 1987). In three dimensions, so a
    /// leg along one storey of a tower is not tested against the six storeys above it.
    /// </summary>
    private sealed class SolidGrid : ISolidIndex
    {
        private const float Cell = 4f;
        private const float Margin = 0.1f;
        private readonly Vector3 _origin;
        private readonly int _nx, _ny, _nz;
        private readonly int[][] _cells;
        private readonly int _count;
        [ThreadStatic] private static int[]? _threadStamp;
        [ThreadStatic] private static int _threadMark;
        [ThreadStatic] private static SolidGrid? _threadStampFor;

        public SolidGrid(Solid[] solids)
        {
            _count = solids.Length;
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            var bounds = new (Vector3 Lo, Vector3 Hi)[solids.Length];
            for (int i = 0; i < solids.Length; i++)
            {
                var s = solids[i];
                Vector3 h = s.Size * 0.5f;
                Vector3 ax = Vector3.Transform(Vector3.UnitX, s.Rotation), ay = Vector3.Transform(Vector3.UnitY, s.Rotation), az = Vector3.Transform(Vector3.UnitZ, s.Rotation);
                Vector3 e = Vector3.Abs(ax) * h.X + Vector3.Abs(ay) * h.Y + Vector3.Abs(az) * h.Z + new Vector3(Margin);
                bounds[i] = (s.Center - e, s.Center + e);
                lo = Vector3.Min(lo, bounds[i].Lo); hi = Vector3.Max(hi, bounds[i].Hi);
            }
            if (solids.Length == 0) { lo = hi = Vector3.Zero; }
            _origin = lo;
            _nx = Math.Max(1, (int)MathF.Ceiling((hi.X - lo.X) / Cell));
            _ny = Math.Max(1, (int)MathF.Ceiling((hi.Y - lo.Y) / Cell));
            _nz = Math.Max(1, (int)MathF.Ceiling((hi.Z - lo.Z) / Cell));
            var lists = new List<int>?[_nx * _ny * _nz];
            for (int i = 0; i < bounds.Length; i++)
            {
                int x0 = Idx(bounds[i].Lo.X, _origin.X, _nx), x1 = Idx(bounds[i].Hi.X, _origin.X, _nx);
                int y0 = Idx(bounds[i].Lo.Y, _origin.Y, _ny), y1 = Idx(bounds[i].Hi.Y, _origin.Y, _ny);
                int z0 = Idx(bounds[i].Lo.Z, _origin.Z, _nz), z1 = Idx(bounds[i].Hi.Z, _origin.Z, _nz);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                            (lists[(z * _ny + y) * _nx + x] ??= new List<int>()).Add(i);
            }
            _cells = new int[lists.Length][];
            for (int c = 0; c < lists.Length; c++) _cells[c] = lists[c]?.ToArray() ?? Array.Empty<int>();
        }

        private static int Idx(float v, float origin, int n) => Math.Clamp((int)MathF.Floor((v - origin) / Cell), 0, n - 1);

        /// <summary>Every box whose cell the segment passes through, each once.</summary>
        public void Along(Vector3 a, Vector3 b, List<int> into)
        {
            into.Clear();
            if (_count == 0) return;
            if (!ReferenceEquals(_threadStampFor, this) || _threadStamp == null || _threadStamp.Length < _count)
            {
                _threadStamp = new int[_count];
                _threadMark = 0;
                _threadStampFor = this;
            }
            int mark = ++_threadMark;
            if (mark == int.MaxValue) { Array.Clear(_threadStamp); _threadMark = mark = 1; }
            Walk(a, b, _threadStamp, mark, into);
            if (ReverseGridOrderForParity) into.Reverse();
        }

        /// <summary>Every box in the cells over the ground between the two points, from the height
        /// <paramref name="fromY"/> up to the top of the grid, each once: what the vertical plane through
        /// them can cut.</summary>
        public void Column(Vector3 a, Vector3 b, float fromY, List<int> into)
        {
            into.Clear();
            if (_count == 0) return;
            if (!ReferenceEquals(_threadStampFor, this) || _threadStamp == null || _threadStamp.Length < _count)
            {
                _threadStamp = new int[_count];
                _threadMark = 0;
                _threadStampFor = this;
            }
            int mark = ++_threadMark;
            if (mark == int.MaxValue) { Array.Clear(_threadStamp); _threadMark = mark = 1; }
            for (int cy = Idx(fromY, _origin.Y, _ny); cy < _ny; cy++)
            {
                float y = _origin.Y + (cy + 0.5f) * Cell;
                Walk(new Vector3(a.X, y, a.Z), new Vector3(b.X, y, b.Z), _threadStamp, mark, into);
            }
            if (ReverseGridOrderForParity) into.Reverse();
        }

        private void Walk(Vector3 a, Vector3 b, int[] stamp, int mark, List<int> into)
        {
            // Clip the segment to the grid's box, so the walk starts and ends inside it.
            Vector3 lo = _origin, hi = _origin + new Vector3(_nx, _ny, _nz) * Cell;
            Vector3 d = b - a;
            float t0 = 0f, t1 = 1f;
            for (int ax = 0; ax < 3; ax++)
            {
                float o = ax == 0 ? a.X : ax == 1 ? a.Y : a.Z, dd = ax == 0 ? d.X : ax == 1 ? d.Y : d.Z;
                float l = ax == 0 ? lo.X : ax == 1 ? lo.Y : lo.Z, h = ax == 0 ? hi.X : ax == 1 ? hi.Y : hi.Z;
                if (MathF.Abs(dd) < 1e-9f) { if (o < l || o > h) return; continue; }
                float ta = (l - o) / dd, tb = (h - o) / dd;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
                if (t0 > t1) return;
            }
            Vector3 p = a + d * t0;

            int cx = Idx(p.X, _origin.X, _nx), cy = Idx(p.Y, _origin.Y, _ny), cz = Idx(p.Z, _origin.Z, _nz);
            int sx = d.X > 0 ? 1 : d.X < 0 ? -1 : 0, sy = d.Y > 0 ? 1 : d.Y < 0 ? -1 : 0, sz = d.Z > 0 ? 1 : d.Z < 0 ? -1 : 0;
            float Next(float pos, float origin, int cell, int step, float dir)
                => step == 0 ? float.MaxValue : (origin + (cell + (step > 0 ? 1 : 0)) * Cell - pos) / dir;
            // In units of the whole segment: t runs from t0 to t1.
            float tx = sx == 0 ? float.MaxValue : t0 + Next(p.X, _origin.X, cx, sx, d.X);
            float ty = sy == 0 ? float.MaxValue : t0 + Next(p.Y, _origin.Y, cy, sy, d.Y);
            float tz = sz == 0 ? float.MaxValue : t0 + Next(p.Z, _origin.Z, cz, sz, d.Z);
            float dx = sx == 0 ? float.MaxValue : Cell / MathF.Abs(d.X);
            float dy = sy == 0 ? float.MaxValue : Cell / MathF.Abs(d.Y);
            float dz = sz == 0 ? float.MaxValue : Cell / MathF.Abs(d.Z);
            int guard = _nx + _ny + _nz + 8;
            while (true)
            {
                foreach (int i in _cells[(cz * _ny + cy) * _nx + cx])
                {
                    if (stamp[i] == mark) continue;
                    stamp[i] = mark;
                    into.Add(i);
                }
                if (--guard < 0) break;
                if (tx <= ty && tx <= tz)
                {
                    if (tx > t1) break;
                    cx += sx; tx += dx;
                    if (cx < 0 || cx >= _nx) break;
                }
                else if (ty <= tz)
                {
                    if (ty > t1) break;
                    cy += sy; ty += dy;
                    if (cy < 0 || cy >= _ny) break;
                }
                else
                {
                    if (tz > t1) break;
                    cz += sz; tz += dz;
                    if (cz < 0 || cz >= _nz) break;
                }
            }
        }
    }
}
