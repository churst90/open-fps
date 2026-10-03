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
    private readonly SolidGrid _grid;
    private readonly List<Opening> _openings;
    private readonly Dictionary<int, List<int>> _byNode = new();
    private readonly Dictionary<int, Vector3> _absorption = new();   // node -> Sabine absorption area per band, m²
    private readonly Dictionary<int, int> _nodeOf = new();          // region id -> node

    public IReadOnlyList<Opening> Openings => _openings;
    public IReadOnlyList<Solid> Solids => _solids;

    /// <summary>The problems found validating the declared openings against the geometry, one line each.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>A room's Sabine absorption area per band (low, mid, high), m², openings included; false
    /// for the outdoors.</summary>
    public bool TryGetAbsorption(int node, out Vector3 area) => _absorption.TryGetValue(node, out area);

    /// <summary>Which graph node a region belongs to.</summary>
    public int NodeOf(int regionId) => _nodeOf.TryGetValue(regionId, out int n) ? n : Outside;

    private OpeningRoutes(Solid[] solids, SolidGrid grid, List<Opening> openings, List<string> problems)
    {
        _solids = solids;
        _grid = grid;
        _openings = openings;
        Problems = problems;
        _solidGains = new Vector3[solids.Length];
        _frames = new Frame[solids.Length];
        for (int i = 0; i < solids.Length; i++)
        {
            _frames[i] = new Frame(solids[i].Center, solids[i].Size * 0.5f, Quaternion.Inverse(Quaternion.Normalize(solids[i].Rotation)));
            var (l, m, h) = WallTransmission.BandGains(solids[i].Material, solids[i].Size, solids[i].Build);
            _solidGains[i] = new Vector3(l, m, h);
        }
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
        var arr = new Solid[solids.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = solids[i];
        var grid = new SolidGrid(arr);
        var problems = new List<string>();
        var openings = new List<Opening>();
        var model = new OpeningRoutes(arr, grid, openings, problems);

        // ── The places ──────────────────────────────────────────────────────────────────────────
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
            var o = model.Derive(d, map);
            if (o == null) continue;
            o.NodeA = model.NodeOf(o.RegionA);
            o.NodeB = model.NodeOf(o.RegionB);
            if (o.NodeA == o.NodeB) continue;              // a doorway between two named bits of street
            if (regionAt != null) model.CheckSides(o, regionAt);
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
        return model;

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
            if (chain.Count == 0) continue;
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
        foreach (var (dd, ps, p, i) in routes)
        {
            if (++tried > MaxRoutesTried) break;
            if (!RouteIsClear(source, ps, p, listener, i, ignoreA, ignoreB)) continue;
            edge = p; edgeVerified = true;
            return dd;
        }
        return worst;
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

    /// <summary>Are the legs of a route round one box — source to its first crossing, along the box, and
    /// from its last crossing to the ear — clear of every OTHER box?</summary>
    private bool RouteIsClear(Vector3 source, Vector3 sourceSide, Vector3 edge, Vector3 listener, int skip,
                              int[]? ignoreA, int[]? ignoreB)
    {
        var cand = Scratch.Get(this).Clear;
        bool Blocked(Vector3 a, Vector3 b)
        {
            _grid.Along(a, b, cand);
            foreach (int i in cand)
            {
                if (i == skip) continue;
                if (ignoreA != null && Array.IndexOf(ignoreA, i) >= 0) continue;
                if (ignoreB != null && Array.IndexOf(ignoreB, i) >= 0) continue;
                if (SegmentHits(i, a, b, RouteJointMetres)) return true;
            }
            return false;
        }
        if (Blocked(edge, listener)) return false;
        if (Blocked(source, sourceSide)) return false;
        if (sourceSide != edge && Blocked(sourceSide, edge)) return false;
        return true;
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

    private readonly record struct Frame(Vector3 Centre, Vector3 Half, Quaternion Inverse);

    /// <summary>
    /// A uniform grid of cubes over the scene, each listing the boxes whose bounds (a hair larger than the
    /// box, so a route checked against slightly enlarged boxes still finds them) overlap it. A segment's
    /// candidates are the boxes in the cells it passes through, walked cell by cell (Amanatides &amp; Woo,
    /// "A Fast Voxel Traversal Algorithm for Ray Tracing", Eurographics 1987). In three dimensions, so a
    /// leg along one storey of a tower is not tested against the six storeys above it.
    /// </summary>
    private sealed class SolidGrid
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
            var stamp = _threadStamp;

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
