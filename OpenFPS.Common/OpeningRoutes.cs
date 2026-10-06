using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Common;

/// <summary>
/// How sound gets from one place to another through the OPENINGS between them: doorways, open faces
/// and the outdoors, as a graph, and the few shortest routes through it.
///
/// Through a wall, a sound is what the wall lets through (<see cref="WallTransmission"/>). Round an
/// obstacle in the open, it is what bends over the one edge in the way (<see cref="Diffraction"/>).
/// Neither reaches a corridor from the street when the way in is a front door, a stairwell and a
/// doorway: that route turns two or more corners, and through rooms rather than round one box. This
/// is that route.
///
/// A ROUTE is a sequence of openings from the source to the listener. Every opening is a rectangle in a
/// wall with what fills it (an open doorway passes everything; a shut leaf passes what its construction
/// lets through, <see cref="WallTransmission"/>), and the route crosses each at the point that makes the
/// whole path shortest. Two things arrive by it, and they are added as energies:
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
/// Only the rooms BETWEEN the two ends are counted so. The source's own room and the listener's own room
/// have reverberant fields too, and those are the reverb's: the source's room bus, heard through its
/// doorway, and the listener's room bus, fed by the voice itself. Counted here as well they would be
/// heard twice, and the second time as a dry voice from the doorway. What arrives at the ear by a route
/// is then its direct sound: the diffracted ray, and what the last room between radiates out of the last
/// opening. Both ends are treated alike, so the route gives the same answer both ways round.
///
/// Nothing here is a constant of a map, a building or a door: what passes is decided by where the
/// openings are, how big they are, what stands in them and what the rooms are made of.
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
    /// the authored width, otherwise zero. The geometry decides the rest (<see cref="Build"/>).
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

    /// <summary>The node every unbounded place belongs to: the global region, and any named patch of
    /// ground with no surfaces (<see cref="RoomAcoustics.IsEnclosure"/> is about a boundary; a street has
    /// none). There is no reverberant field in it to relay anything.</summary>
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
    /// really has its two places on its two sides.
    /// </summary>
    public static OpeningRoutes Build(IReadOnlyList<Solid> solids, AcousticMap? map, IEnumerable<Declared> declared,
                                      Func<Vector3, int>? regionAt = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var arr = new Solid[solids.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = solids[i];
        var grid = new SolidGrid(arr);
        double gridMs = clock.Elapsed.TotalMilliseconds;
        var problems = new List<string>();
        var openings = new List<Opening>();
        var model = new OpeningRoutes(arr, grid, openings, problems);
        double solidsMs = clock.Elapsed.TotalMilliseconds - gridMs;
        model.Fill(map, declared, regionAt, null, clock, gridMs, solidsMs);
        return model;
    }

    // ═══ Built tile by tile (docs/GEOMETRY.md stage 1) ════════════════════════════════════════════
    //
    // The acoustic triangle store already holds the scene a tile at a time, its pieces kept while their
    // boxes stay the same. The graph is made from it in the same way: the boxes are asked of the store's
    // trees instead of a grid built over all of them, each tile's boxes keep their frames and
    // transmissions while its piece does, and an opening is derived again only when a tile its walls
    // could be in has changed. What a route answers is the same as a whole rebuild's (AudioLab
    // --geometry-parity only=routes): the same boxes, met by the same tests.

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
    /// The graph for the scene the acoustic triangle store holds (its tiles' boxes, door leaves where
    /// they stand and marked as leaves), as <see cref="Build(IReadOnlyList{Solid}, AcousticMap?, IEnumerable{Declared}, Func{Vector3, int}?)"/>
    /// makes it from a box list, with what did not change since the last build kept in
    /// <paramref name="cache"/>.
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
        var model = new OpeningRoutes(solids, frames, gains, index, new List<Opening>(), new List<string>());
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
                r = MathF.Max(r, MathF.Max(region.RoomSize.X, MathF.Max(region.RoomSize.Y, region.RoomSize.Z)));
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
                if (map.Regions.TryGetValue(r, out var region)) { h.Add(region.RoomSize); h.Add(RoomAcoustics.OpenFaceCount(region)); }
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
            Span<float> areas = stackalloc float[6];
            foreach (var (id, region) in map.Regions)
            {
                if (id == AcousticConstants.GlobalRegionId || id == map.GlobalEnvironmentId) continue;
                if (region.RoomSize.X <= 0f || RoomAcoustics.OpenFaceCount(region) == 6) continue;
                RoomAcoustics.FaceAreas(region.RoomSize, areas);
                Vector3 a = Vector3.Zero;
                for (int f = 0; f < 6; f++)
                {
                    // An open face is a perfect absorber of its own area: what reaches it leaves.
                    if (RoomAcoustics.FaceIsOpen(region.Materials, f)) { a += new Vector3(areas[f]); continue; }
                    var p = region.Materials != null && f < region.Materials.Length
                        ? AcousticRegistry.GetPropertiesByResonanceIndex(region.Materials[f])
                        : AcousticRegistry.GetProperties("Generic");
                    a += areas[f] * new Vector3(p.AbsorptionLow, p.AbsorptionMid, p.AbsorptionHigh);
                }
                model._nodeOf[id] = id;
                faceAbsorption[id] = a;
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

        for (int i = 0; i < openings.Count; i++)
        {
            var o = openings[i];
            Add(model._byNode, o.NodeA, i);
            Add(model._byNode, o.NodeB, i);
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

    /// <summary>How far past an opening's declared size the geometry is searched for its jambs, floor
    /// and lintel, metres: a doorway's leaf laps its frame by a few centimetres and an authored portal is
    /// placed by hand, so the walls are near but not exactly where the numbers say.</summary>
    private const float FrameSearchMetres = 0.5f;

    /// <summary>How far short of an opening's frame a route may cross it, metres. A leg run exactly along
    /// a jamb's face is a crack the building does not have (Diffraction's EdgeSkin, for the same reason).</summary>
    private const float FrameSkinMetres = 0.02f;

    private Opening? Derive(in Declared d, AcousticMap? map)
    {
        var o = new Opening { Id = d.Id, Kind = d.Kind, RegionA = d.RegionA, RegionB = d.RegionB, Centre = d.Centre };
        bool haveFrame = d.Rotation != default;
        if (haveFrame && d.Kind == FaceKind)
        {
            // A gap measured from the walls round it (FaceOpenings): its rectangle is the geometry's
            // already, to the edge of every box beside it, so there is nothing to search for. Its thin
            // axis is the one through it whatever its proportions: a slot in a thick wall is deeper than
            // it is wide and is still a slot.
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
            // No frame given: the face of the room it belongs to that it sits on says which way it faces.
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
        // Each side is searched outward from the centre, and the first wall met is that side of the
        // frame. Leaves are not walls. A side with nothing within reach keeps its declared extent.
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
        // As deep as the wall it is cut through. A box that runs THROUGH the opening rather than across
        // it — a tunnel's side wall at its open end, a corridor wall beside an open side — is not that
        // wall: its length along the normal is not a depth. Counted as one it was: the tunnel's openings
        // were fifty metres deep, so the check of their sides and the test of what stands in them reached
        // two sections away (2026-10-02).
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
            // Said over anything else found: it is the reason nothing else about it matters.
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
        foreach (int r in new[] { d.RegionA, d.RegionB })
        {
            if (!map.Regions.TryGetValue(r, out var region) || region.RoomSize.X <= 0f) continue;
            if (RoomAcoustics.OpenFaceCount(region) == 6) continue;
            if (!map.RegionPositions.TryGetValue(r, out var c)) continue;
            var q = map.RegionRotations.GetValueOrDefault(r, Quaternion.Identity);
            Vector3 local = Vector3.Transform(d.Centre - c, Quaternion.Inverse(q));
            Vector3 h = region.RoomSize * 0.5f;
            // Distance of the point from each of the six face planes; the nearest is the one it is on.
            Span<float> gap = stackalloc float[3] { MathF.Abs(MathF.Abs(local.X) - h.X), MathF.Abs(MathF.Abs(local.Y) - h.Y), MathF.Abs(MathF.Abs(local.Z) - h.Z) };
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
            BuildChain(c, lTree, sTree, sNode, chain);
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
    /// Whether a chain goes through one opening twice. A route "by the outdoors" between two rooms of
    /// one building can leave by the front door and come straight back in by it: no way round at all,
    /// only the doorway crossed twice, and its crossings line up better than the true route's through
    /// the doorway between the rooms, so it won, and the next room was heard from the front door.
    /// </summary>
    private static bool CrossesTwice(List<(int, int)> chain)
    {
        for (int i = 0; i < chain.Count; i++)
            for (int j = i + 1; j < chain.Count; j++)
                if (chain[i].Item1 == chain[j].Item1) return true;
        return false;
    }

    /// <summary>
    /// How strong a place's reverberant field is at <paramref name="listener"/>, as an amplitude
    /// against standing in it, and where the strongest part of it arrives from.
    ///
    /// A diffuse field of energy density E pushes E c S / 4 watts out of an opening of area S. Out of
    /// the opening it spreads over a half space, so at r metres its pressure squared is S / (8 pi r^2)
    /// of the field's inside: that, per opening, with the opening's own transmission (a shut leaf, a
    /// door ajar). An opening into the listener's own place is heard straight; one into anywhere else
    /// is followed by the routes through openings, as any other sound there would be. Summed over
    /// every opening of the place within <paramref name="range"/>.
    ///
    /// It replaced (aperture / 2) / distance through the single nearest opening joining the two
    /// places, which gave a room two openings away nothing and a lobby none of the street.
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
        // And what comes in builds the listener's own room's field, which they are standing in: the
        // field just outside each of its openings times what that opening lets in, over the room's
        // absorption (the transmission-room equation, E_room = sum E_out S tau / A). Down a corridor
        // two openings from the street this is most of it; the openings' direct radiation is a
        // little of it near each one.
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

    /// <summary>The direct part of <see cref="FieldAt"/>: each opening of the place radiating its field
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
    private void BuildChain(in Candidate c, SideTree lTree, SideTree sTree, int sNode, List<(int, int)> chain)
    {
        // The source's half, from the source outward: its tree's states run from the source, so walking
        // back from the exit gives them exit-first; reversed, source-first.
        int start = chain.Count;
        for (int s = c.SourceState; s >= 0; s = sTree.Prev[s])
        {
            int o = s >> 1;
            chain.Add((o, _openings[o].Other(sTree.From(s))));
        }
        chain.Reverse(start, chain.Count - start);
        // The listener's half, from where it meets the source's side inward: the tree's states run from
        // the listener, so walking back from the meeting state gives them in source-to-listener order.
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

        // Where the route crosses each opening: the shortest path through the rectangles. The length is
        // convex in all the crossings together and each rectangle is convex, so improving one crossing at
        // a time with its neighbours fixed converges on the shortest route.
        for (int pass = 0; pass < 4; pass++)
            for (int k = 1; k <= n; k++)
                x[k] = Crossing(_openings[chain[k - 1].Opening], x[k - 1], x[k + 1]);

        length = 0f;
        for (int k = 0; k <= n; k++) length += Vector3.Distance(x[k], x[k + 1]);
        lastCrossing = x[n];

        // ── The legs: what stands between one crossing and the next ───────────────────────────────
        var legs = scratch.Legs;
        legs.Clear();
        // An opening is as deep as its wall, and a leg runs to the FACE of it that it arrives at, not to
        // the middle of the wall: a car up the street sees a front door almost edge-on, and a line to the
        // door's mid-plane runs through the facade beside it for metres.
        for (int k = 0; k <= n; k++)
        {
            int[] ignoreA = k > 0 ? _openings[chain[k - 1].Opening].Contents : Array.Empty<int>();
            int[] ignoreB = k < n ? _openings[chain[k].Opening].Contents : Array.Empty<int>();
            // Inside the opening's edges, not on them: a crossing hugs the edge it bends round, and that
            // bend is the aperture's to charge (Aperture, below). A leg ending AT the jamb ends in the
            // corner between the jamb and an open leaf hinged on it, with no way round the leaf.
            Vector3 from = k > 0 ? Face(_openings[chain[k - 1].Opening], Inset(_openings[chain[k - 1].Opening], x[k]), x[k + 1]) : x[k];
            Vector3 to = k < n ? Face(_openings[chain[k].Opening], Inset(_openings[chain[k].Opening], x[k + 1]), x[k]) : x[k + 1];
            // Off the face, not on it. A leg ending ON the wall's surface touches the wall beside the
            // doorway, and the clearance check pads every other box by a joint's width: every way round
            // whatever stood in front of the door (its own leaf, swung open) was refused, and the leaf
            // was charged as solid steel. A car down the street from an open front door came in at the
            // shut-door level ("sound struggles through the door only when loud things pass").
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
        // Only when there is a room between: with one opening, the two rooms are the source's and the
        // listener's, and their fields are the reverb's.
        if (n < 2) return geo;
        // Free-field reference: W = 1 at d, so a mean-square pressure p²/ρc is compared with 1/(4πd²).
        float free = 1f / (4f * MathF.PI * d * d);
        var first = _openings[chain[0].Opening];
        float r1 = MathF.Max(0.1f, Vector3.Distance(source, x[1]));
        float cos1 = MathF.Abs(Vector3.Dot(Vector3.Normalize(x[1] - source), first.Normal));
        // Into the first room between: the source's direct sound on the opening's projected area.
        Vector3 power = legs[0] * legs[0] * (cos1 / (4f * MathF.PI * r1 * r1)) * first.Area * first.Tau;
        // Through each room between: its field on the next opening. The outdoors is no room: a route
        // that leaves one building for another carries only its ray across.
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
        // Where the straight line meets the aperture's plane, in its two axes; a line parallel to the
        // plane is placed by its midpoint.
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
        // Unfold about the line (Diffraction.MinimiseOnEdge): the shortest route by it divides the two
        // points' positions along it in the ratio of their distances from it.
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
    /// What a leg loses, kept for the next query whose ends fall in the same cells. A leg can run for
    /// hundreds of metres through a city — from a car up the street to the front door, or across a square
    /// from one building's door to another's — and when it is blocked, the search round each box in its
    /// way costs milliseconds. The answer does not change while both ends stay inside cells a fiftieth of
    /// the leg's length across (a quarter of a metre at a doorway, four metres three hundred away), so it
    /// is asked once per pair of cells. The key does not care which end is which, so a route and its
    /// reverse share it.
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
    /// through (<see cref="WallTransmission"/>), or what bends round the worst of them by a route clear of
    /// everything else (<see cref="BarrierPathDifference"/>), whichever passes more in each band. The boxes
    /// standing in the openings at either end are not charged: the opening's own transmission is.
    /// </summary>
    public Vector3 LegGains(Vector3 a, Vector3 b, int[] ignoreA, int[] ignoreB)
    {
        var cand = Scratch.Get(this).Candidates;
        _grid.Along(a, b, cand);
        Vector3 through = Vector3.One;
        bool blocked = false;
        foreach (int i in cand)
        {
            if (Array.IndexOf(ignoreA, i) >= 0 || Array.IndexOf(ignoreB, i) >= 0) continue;
            if (!SegmentHits(i, a, b)) continue;
            through *= _solidGains[i];
            blocked = true;
        }
        if (!blocked) return Vector3.One;
        float delta = BarrierPathDifference(a, b, out _, out bool verified, ignoreA, ignoreB);
        if (verified && delta >= 0f)
        {
            var (l, m, h) = Diffraction.BandGains(delta, WallTransmission.SoundSpeed);
            float dist = MathF.Max(0.5f, Vector3.Distance(a, b));
            float spread = dist / (dist + delta);
            through = Vector3.Max(through, new Vector3(l, m, h) * spread);
        }
        return through;
    }

    // ═══ Round one obstacle ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// How far out of its way sound had to go to get from the source to the listener, metres, or -1 if
    /// nothing is in the way at all, and the edge the last leg leaves from.
    ///
    /// Barriers do not add up: two screens in a row are not twice one screen, because the second stands in
    /// the first one's shadow. What governs is the single worst detour (the standards' single-worst-screen
    /// rule), searched round each box in the way (<see cref="Diffraction.PathDifferenceAroundBox"/>).
    ///
    /// <paramref name="edgeVerified"/> says a route of that length exists: both legs clear of every OTHER
    /// box, and the shortest such route is the one believed — sound takes the way that exists. Round the
    /// end of one wall and straight into the next is a good answer to "how far past THIS box" and a wrong
    /// one to "which way did it come". A route clear of every box is at least as long as the worst box's
    /// own shortest way round, so nothing shorter can be it.
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
            ref readonly var b = ref _solids[i];
            boxRoutes.Clear();
            if (!Diffraction.PathDifferenceAroundBox(b.Center, b.Size, b.Rotation, source, listener,
                                                     out float dd, out Vector3 p, boxRoutes)) continue;
            foreach (var (rd, rs, re) in boxRoutes) routes.Add((rd, rs, re, i));
            if (dd <= worst) continue;
            worst = dd; edge = p;
        }

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
            // Round a door leaf is through its doorway: believed only clear as it stands, as it always
            // was, never bent on round the rest of the street (ClearedLeg). That found the crack over
            // a shut glass front door and played the street through it at -21 dB.
            int chain = _solids[i].IsLeaf ? 0 : MaxChainedBoxes;
            float extra = ClearedRoute(source, ps, p, listener, i, chain, ignoreA, ignoreB, best - dd, out Vector3 last);
            if (extra < 0f || dd + extra >= best) continue;
            best = dd + extra;
            bestEdge = last;
        }
        // Over the top of everything, as the standards draw it (ISO 9613-2, CNOSSOS-EU): the tight string
        // over every obstacle in the vertical plane through the two points. It exists whenever neither end
        // has something over its head, whatever the search round the sides found or missed.
        if (worst >= 0f)
        {
            float over = OverTheTop(source, listener, ignoreA, ignoreB, out Vector3 overEdge);
            if (over >= 0f && over < best) { best = over; bestEdge = overEdge; }
        }
        if (best == float.MaxValue) return worst;
        edge = bestEdge; edgeVerified = true;
        // Something IS in the way, so the least this can be is grazing it: Maekawa's 5 dB, not the
        // nothing that a path difference of exactly zero reads as (Diffraction.InsertionLossDb). A car
        // whose line just clipped a wall top came out at 0 dB at one position and -5 at the next.
        return MathF.Max(best, GrazingMetres);
    }

    /// <summary>The path difference a route that only grazes what is in its way is given, metres:
    /// small enough to be Maekawa's grazing figure in every band.</summary>
    private const float GrazingMetres = 1e-5f;

    /// <summary>
    /// The path difference of the way over every box between the two points, in the vertical plane through
    /// them: the upper convex hull of the boxes' tops where the plane crosses them, from one point to the
    /// other — the string pulled tight over the profile, which is how road-traffic noise standards (ISO
    /// 9613-2, CNOSSOS-EU) find the path over several screens. Each leg is then checked against the whole
    /// scene: a box it cuts through joins the profile and the string is pulled again. -1 when there is no
    /// way over — an end has a roof over it, so the string can only leave it upward through something.
    /// <paramref name="lastEdge"/> is the top the last leg leaves from for the ear.
    ///
    /// Why it is here: the search round one box at a time (with ClearedLeg's bending) is bounded, and a
    /// bounded search that finds the way round a building for one position of a car and misses it two
    /// metres on hands the car -13 dB and then -76 (through the whole building) a third of a second
    /// apart. Out in the open there is always a way over the roofs, and its level only changes as fast
    /// as the profile does.
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
        // A box is part of the profile if it reaches down to the straight line, or down onto the top of
        // something that does: a building's storeys stacked one on another are one obstacle to go over,
        // and a sign hung over the street that nothing holds up from below is not (the string passes
        // under it unless something else lifts it there, which the leg test finds). Swept bottom-up
        // through one-metre bins along the ground, so it costs one pass over what the plane cuts.
        var crossings = scratch.Crossings;
        crossings.Clear();
        foreach (int i in cand)
        {
            if (ignoreA != null && Array.IndexOf(ignoreA, i) >= 0) continue;
            if (ignoreB != null && Array.IndexOf(ignoreB, i) >= 0) continue;
            if (!Crossing(i, source, listener, run, out float s0, out float s1, out float bottom, out float top)) continue;
            float lineBottom = MathF.Min(Lerp(source.Y, listener.Y, s0 / run), Lerp(source.Y, listener.Y, s1 / run));
            if (top < lineBottom) continue;   // under the line all the way across: never touched
            crossings.Add((i, s0, s1, bottom, top));
        }
        if (crossings.Count == 0) return -1f;
        crossings.Sort((x, y) => x.Bottom.CompareTo(y.Bottom));
        int bins = Math.Clamp((int)MathF.Ceiling(run / ProfileBinMetres), 1, 4096);
        var held = scratch.ProfileBins;
        if (held.Length < bins) held = scratch.ProfileBins = new float[Math.Max(bins, 2 * held.Length)];
        // What holds a box up at each bin: the line itself to begin with.
        for (int k = 0; k < bins; k++)
        {
            float sMid = (k + 0.5f) * run / bins;
            held[k] = Lerp(source.Y, listener.Y, sMid / run);
        }
        var included = scratch.Included;
        included.Clear();
        foreach (var c in crossings)
        {
            int k0 = Math.Clamp((int)(c.S0 / run * bins), 0, bins - 1), k1 = Math.Clamp((int)(c.S1 / run * bins), 0, bins - 1);
            float support = float.MinValue;
            for (int k = k0; k <= k1; k++) support = MathF.Max(support, held[k]);
            if (c.Bottom > support + 2f * RouteJointMetres) continue;
            included.Add(c.Box);
            for (int k = k0; k <= k1; k++) held[k] = MathF.Max(held[k], c.Top);
        }
        if (included.Count == 0) return -1f;
        // Something over an end's own head — a ceiling, a canopy, a balcony — and the string could only
        // leave that end straight up through it. From under a roof the way out is sideways first, which
        // is the search round the sides and the openings' business, not this.
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
                pts.Add((MathF.Max(c.S0, 1e-3f), c.Top + RouteJointMetres, c.Box));
                pts.Add((MathF.Min(c.S1, run - 1e-3f), c.Top + RouteJointMetres, c.Box));
            }
            pts.Sort((x, y) => x.S.CompareTo(y.S));
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
            int blocker = -1;
            for (int k = 0; k + 1 < hull.Count; k++)
            {
                Vector3 a = At(hull[k].S, hull[k].Y), b = At(hull[k + 1].S, hull[k + 1].Y);
                int hit = FirstHit(a, b, hull[k].Box, hull[k + 1].Box, ignoreA, ignoreB);
                if (hit >= 0) { blocker = hit; break; }
                length += Vector3.Distance(a, b);
            }
            if (blocker < 0)
            {
                if (hull.Count > 2) lastEdge = At(hull[^2].S, hull[^2].Y);
                if (explain != null)
                    foreach (var h in hull)
                        if (h.Box >= 0)
                            explain.Append($"          over box {h.Box} {_solids[h.Box].Material} ({_solids[h.Box].Center.X:F1}, {_solids[h.Box].Center.Y:F2}, {_solids[h.Box].Center.Z:F1}) size ({_solids[h.Box].Size.X:F2}, {_solids[h.Box].Size.Y:F2}, {_solids[h.Box].Size.Z:F2}) at {h.S:F1} m, {h.Y:F2} m up\n");
                return MathF.Max(0f, length - Vector3.Distance(source, listener));
            }
            explain?.Append($"          over the top, pass {pass}: cut by box {blocker} {_solids[blocker].Material} ({_solids[blocker].Center.X:F1}, {_solids[blocker].Center.Y:F2}, {_solids[blocker].Center.Z:F1}) size ({_solids[blocker].Size.X:F2}, {_solids[blocker].Size.Y:F2}, {_solids[blocker].Size.Z:F2})\n");
            // Something the string was already over, still in its way: an end is under it.
            if (!included.Add(blocker)) return -1f;
            bool known = false;
            foreach (var c in crossings) if (c.Box == blocker) { known = true; break; }
            if (!known)
            {
                if (!Crossing(blocker, source, listener, run, out float s0, out float s1, out float bottom, out float top)) return -1f;
                if (OverAnEnd(s0, s1, bottom)) return -1f;
                crossings.Add((blocker, s0, s1, bottom, top));
            }
        }
        return -1f;
    }

    /// <summary>How near an end, along the ground, a box's crossing must reach to stand over it, metres.</summary>
    private const float EndColumnMetres = 0.01f;

    /// <summary>Width of the bins the profile is swept through, metres.</summary>
    private const float ProfileBinMetres = 1f;

    /// <summary>Times the string over the top is pulled again over something hung above the line that it
    /// was found to cut.</summary>
    private const int OverTheTopPasses = 4;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Where the vertical plane through the two points crosses box i, as distances along the
    /// ground from the source, and the box's top there. False if it does not.</summary>
    private bool Crossing(int i, Vector3 source, Vector3 listener, float run, out float s0, out float s1, out float bottom, out float top)
    {
        var f = _frames[i];
        s0 = 0f; s1 = run; top = 0f; bottom = 0f;
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
            top = f.Centre.Y + f.Half.Y;
            bottom = f.Centre.Y - f.Half.Y;
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
        top = max.Y;
        bottom = min.Y;
        return true;
    }

    private static bool Slab(float o, float d, float h, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-9f) return o >= -h && o <= h;
        float ta = (-h - o) / d, tb = (h - o) / d;
        if (ta > tb) (ta, tb) = (tb, ta);
        t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
        return t0 <= t1;
    }

    /// <summary>For the lab: the barrier search spelled out — every box on the line with its shortest way
    /// round, and for the routes tried, which other box (if any) each one ran into.</summary>
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
            boxRoutes.Clear();
            if (!Diffraction.PathDifferenceAroundBox(b.Center, b.Size, b.Rotation, source, listener, out float dd, out _, boxRoutes)) continue;
            sb.Append($"        box {i} {b.Material} centre ({b.Center.X:F1}, {b.Center.Y:F2}, {b.Center.Z:F1}) size ({b.Size.X:F2}, {b.Size.Y:F2}, {b.Size.Z:F2}): round it {dd:F3} m\n");
            foreach (var (rd, rs, re) in boxRoutes) routes.Add((rd, rs, re, i));
            worst = MathF.Max(worst, dd);
        }
        routes.RemoveAll(r => r.D < worst - 1e-3f);
        routes.Sort((x, y) => x.D.CompareTo(y.D));
        foreach (var (dd, ps, p, i) in routes.Take(MaxRoutesTried))
        {
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
        return sb.ToString();
    }

    /// <summary>A route clear of the whole scene costs a pass over the boxes near it; past the few
    /// shortest, the rest are ways round walls deeper in.</summary>
    private const int MaxRoutesTried = 6;

    /// <summary>
    /// How much larger every other box is taken to be when a route round one box is checked against it,
    /// metres. An edge flush against its neighbour — a wall's top under the slab it holds up — is not an
    /// edge, and a route along the joint is a crack between two boxes that the building does not have.
    /// </summary>
    private const float RouteJointMetres = 0.05f;

    /// <summary>
    /// How many more boxes a route round one box may bend round on its way, when one of its legs runs into
    /// them. "Pops" heard from the Main Street pavement (2026-10-03): a park's 1.1 m wall with a 0.5 m pier
    /// every few metres along its top, a car 160 m beyond it. Over the wall is a few millimetres of
    /// detour; whether that route was believed depended on whether its leg to the ear crossed the wall
    /// line at a pier or between two, so as the car drove the answer flipped between -7 dB (over the
    /// wall) and -80 (through the wall and everything after it) for a third of a second at a time. A
    /// pier on a wall the sound is already bending over costs it a few millimetres more, not the route.
    /// A box the leg is still blocked by after this many is a building, and its answer is what comes
    /// through it.
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
        if (hit < 0) return 0f;
        var scratch = Scratch.Get(this);
        if (chain <= 0 || scratch.ChainBudget <= 0 || limit <= 0f) return -1f;
        // Round a door leaf is through its doorway, which is the openings' business (Route), with what
        // the leaf passes. Bent round here it was a crack beside a shut glass door at -12 dB.
        if (_solids[hit].IsLeaf) return -1f;
        scratch.ChainBudget--;
        ref readonly var box = ref _solids[hit];
        // One list per depth: the loop below recurses while it walks this one.
        var ways = scratch.Ways[chain];
        ways.Clear();
        if (!Diffraction.PathDifferenceAroundBox(box.Center, box.Size + new Vector3(2f * RouteJointMetres), box.Rotation,
                                                 a, b, out _, out _, ways)) return -1f;
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
        public readonly List<(float D, Vector3 SourceSide, Vector3 Edge, int Box)> BarrierRoutes = new();
        public readonly List<(float D, Vector3 SourceSide, Vector3 Edge)> BoxRoutes = new();
        /// <summary>Boxes the current barrier search may still bend a route round (ClearedLeg).</summary>
        public int ChainBudget;
        public readonly List<(float S, float Y, int Box)> Profile = new(), Hull = new();
        public readonly List<(int Box, float S0, float S1, float Bottom, float Top)> Crossings = new();
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
    private bool SegmentHits(int i, Vector3 a, Vector3 b, float grow = 0f)
    {
        var f = _frames[i];
        Vector3 la = Vector3.Transform(a - f.Centre, f.Inverse), lb = Vector3.Transform(b - f.Centre, f.Inverse);
        Vector3 h = f.Half + new Vector3(grow);
        Vector3 d = lb - la;
        float t0 = 0f, t1 = 1f;
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
