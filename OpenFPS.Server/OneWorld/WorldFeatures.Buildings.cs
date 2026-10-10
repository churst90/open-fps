using System.Numerics;
using OpenFPS.Server.Core;
using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// The buildings of a world tile (docs/WORLD_STREAMING.md, "Buildings on the world's tiles"): Overture's footprints
/// built as tools/gen_osm.py builds a place's at medium detail (<see cref="WorldBuildings"/>), each stored whole by the
/// tile its middle is in, and the ground under every building near a tile graded to its floor, so a house across an
/// edge sits level on both sides whichever is made first.
///
/// <para>What gen_osm.py decides from a place's addresses and lots, a world tile decides from the footprint and the
/// roads alone (<see cref="Classify"/>): what a building is by Overture's class, its size and its shape; its name from
/// Overture's (a shop, a church) or "House off Main Street"; its front facing the nearest road; its pad the ground at
/// the middle of its wall nearest the road.</para>
/// </summary>
public sealed partial class WorldFeatures
{
    /// <summary>Where the buildings come from, or null for roads and woods alone.</summary>
    public IBuildingSource? Buildings { get; }

    private readonly IReadOnlyDictionary<string, WorldBuildings.Prefab> _prefabs;

    /// <summary>A building whose floor comes within this of a tile grades its ground, metres: a slab reaches the posts
    /// 3 m round it and the blend back to the survey 6 m further (TerrainBuilder).</summary>
    public const double BuildingNear = 12.0;

    /// <summary>The roads asked for round a tile with buildings, metres: a building near the tile faces the nearest road
    /// within 400 m of its middle (gen_osm.py), and its middle is at most <see cref="MaxBuildingAcross"/> / 2 and
    /// <see cref="BuildingNear"/> beyond the tile.</summary>
    public const double BuildingRoadReach = 650.0;

    /// <summary>The largest building made, metres corner to corner of its bounds: its pad and the ground under it are
    /// then always within <see cref="MaxMargin"/> of every tile it touches. Bigger ones (a mall, a distribution
    /// centre) are left out.</summary>
    public const double MaxBuildingAcross = 230.0;

    /// <summary>The widest window of ground a tile with buildings asks for, metres round it.</summary>
    public const double MaxMargin = 250.0;

    /// <summary>How far round a tile its footprints are asked for, metres: those that matter to it (within
    /// <see cref="BuildingNear"/>) and the houses next to them (<see cref="BesideAHouse"/>).</summary>
    public const double NeighbourReach = 100.0;

    /// <summary>gen_osm.py classify(): a building on a lot whose middle is this near the house's, and smaller, is the
    /// house's garage (under 100 m²) or workshop (under 160 m²), metres.</summary>
    public const double BesideAHouse = 30.0;

    /// <summary>The longest driveway made for a house, metres: a tile works out the drives of the houses this far and
    /// <see cref="BuildingNear"/> round it, so it grades under every drive that reaches it.</summary>
    public const double MaxDrive = 60.0;

    /// <summary>A house with the end of a mapped driveway (OpenStreetMap's) this near it has that one, not one made up.</summary>
    public const double MappedDriveNear = 15.0;

    /// <summary>gen_osm.py: the nearest road a building's pad and name are taken from, and the one its front faces.</summary>
    public const double PadRoadReach = 200.0, FacingRoadReach = 400.0;

    /// <summary>The buildings reaching a tile and the margin round it that matters to it, from the source, whole.</summary>
    public Task<IReadOnlyList<Footprint>> FootprintsAsync(WorldTileKey key, CancellationToken ct)
    {
        if (Buildings == null) return Task.FromResult<IReadOnlyList<Footprint>>(Array.Empty<Footprint>());
        var (s, w, n, e) = Degrees(key, NeighbourReach);
        return Buildings.BuildingsAsync(s, w, n, e, ct);
    }

    /// <summary>A box round a tile in degrees (south, west, north, east): the tile and <paramref name="reach"/> metres
    /// round it, by its corners and the middles of its north and south edges.</summary>
    private static (double S, double W, double N, double E) Degrees(WorldTileKey key, double reach)
    {
        double w = key.Easting - reach, s = key.Northing - reach;
        double e = key.Easting + WorldTileKey.TileMetres + reach, n = key.Northing + WorldTileKey.TileMetres + reach;
        double lat0 = 90, lat1 = -90, lon0 = 180, lon1 = -180;
        foreach (var (pe, pn) in new[] { (w, s), (e, s), (w, n), (e, n), ((w + e) / 2, s), ((w + e) / 2, n) })
        {
            var (lat, lon) = Utm.ToLatLon(key.Zone, key.North, pe, pn);
            lat0 = Math.Min(lat0, lat); lat1 = Math.Max(lat1, lat);
            lon0 = Math.Min(lon0, lon); lon1 = Math.Max(lon1, lon);
        }
        return (lat0, lon0, lat1, lon1);
    }

    // ═══ What a building is (the world's classify()) ════════════════════════════════════════════════════

    /// <summary>gen_osm.py OSM_HOMES and OSM_SHEDS: OpenStreetMap's building values (Overture's class) for a home and
    /// for a shed.</summary>
    public static readonly HashSet<string> Homes = new() { "house", "detached", "residential", "semidetached_house", "terrace", "bungalow",
                                                           "apartments", "static_caravan", "cabin", "farm" };

    /// <summary>Overture's classes of a place of worship: a named one is a church.</summary>
    private static readonly HashSet<string> Worship = new() { "church", "chapel", "cathedral", "mosque", "temple", "synagogue", "shrine",
                                                              "religious", "monastery" };

    /// <summary>
    /// What a building is, without addresses or lots (gen_osm.py classify() has both): a building with a name of its
    /// own is a place (a church, or premises with an entrance); one Overture calls a home is a house, or a mobile home
    /// if it is a single-wide's shape or a static caravan; a shed under 30 m² or called one is a shed; a garage or
    /// carport is a garage; another class is a building from 60 m² (an outbuilding under); and one with no class, as
    /// most of Microsoft's are, is a house from 45 to 600 m² (a mobile home by its shape), an outbuilding under 45 m²,
    /// and a building over 600 m². One of those houses under 160 m² beside a bigger one (<paramref name="besideAHouse"/>)
    /// is that house's garage (under 100 m²) or workshop instead, as gen_osm.py finds them on a lot. A roof (a canopy
    /// over a forecourt) is not made: it has no walls. Null for one not made.
    /// </summary>
    public static (string Kind, string? PlaceKind)? Classify(Footprint fp, WorldBuildings.Shape s, bool besideAHouse = false)
    {
        string cls = fp.Class ?? "";
        bool narrow = s.Short <= 5.2 && s.Long >= 2.8 * s.Short;
        if (cls == "roof") return null;
        if (fp.Name != null)
        {
            string k = cls.Length > 0 ? cls : "building";
            return (Worship.Contains(k) || k.Contains("worship") ? "church" : "premises", k);
        }
        if (s.Area < 30 || cls == "shed") return ("shed", null);
        if (cls is "garage" or "garages" or "carport") return ("garage", null);
        if (besideAHouse && cls.Length == 0 && s.Area < 160) return (s.Area < 100 ? "garage" : "workshop", null);
        if (Homes.Contains(cls) || (cls.Length == 0 && s.Area >= 45 && s.Area <= 600))
            return ((narrow && !Homes.Contains(cls)) || cls == "static_caravan" ? "mobile_home" : "house", null);
        if (cls.Length > 0) return (s.Area >= 60 ? "building" : "outbuilding", null);
        return (s.Area < 45 ? "outbuilding" : "building", null);
    }

    // ═══ The roads a building is placed by ═════════════════════════════════════════════════════════════

    /// <summary>A road way in the zone's metres, whole: its line, its name, and the road it is part of (gen_osm.py
    /// road_key: the same name and class, an unnamed way only itself).</summary>
    private sealed record RoadLine(OsmWay Way, List<(double E, double N)> Pts, string Name, string Key, double Width);

    /// <summary>gen_osm.py's roads (ROAD_GEOM): every road way near, at least 4 m long.</summary>
    private static List<RoadLine> RoadLines(WorldTileKey key, IReadOnlyList<OsmWay> ways)
    {
        var lines = new List<RoadLine>();
        foreach (var w in ways)
        {
            if (!IsRoad(w)) continue;
            var pts = new List<(double E, double N)>(w.Nodes.Length);
            for (int k = 0; k < w.Nodes.Length; k++)
            {
                var (pe, pn) = Utm.FromLatLon(w.Lat[k], w.Lon[k], key.Zone, key.North);
                if (pts.Count > 0 && Math.Abs(pe - pts[^1].E) + Math.Abs(pn - pts[^1].N) < 1e-6) continue;
                pts.Add((pe, pn));
            }
            double len = 0;
            for (int k = 0; k < pts.Count - 1; k++) len += Dist(pts[k], pts[k + 1]);
            if (pts.Count < 2 || len < 4.0) continue;
            string name = w.Tag("name");
            lines.Add(new RoadLine(w, pts, NameOf(w), name + "|" + RoadClass[w.Tag("highway")].Type + "|" + (name.Length > 0 ? name : w.Id.ToString()), WidthOf(w)));
        }
        return lines;
    }

    /// <summary>The ways of the road a way is part of: joined end to end where exactly two ways meet at a node nothing
    /// else passes through and both are the same road (gen_osm.py build_roads' chains).</summary>
    private static List<RoadLine> Chain(RoadLine start, List<RoadLine> lines, Dictionary<long, int> uses, Dictionary<long, List<RoadLine>> ends)
    {
        var chain = new List<RoadLine> { start };
        var seen = new HashSet<RoadLine> { start };
        for (int i = 0; i < chain.Count; i++)
        {
            var w = chain[i].Way;
            foreach (long node in new[] { w.Nodes[0], w.Nodes[^1] })
            {
                if (!uses.TryGetValue(node, out int u) || u != 2 || !ends.TryGetValue(node, out var at) || at.Count != 2) continue;
                foreach (var other in at)
                    if (other.Key == start.Key && seen.Add(other)) chain.Add(other);
            }
        }
        return chain;
    }

    /// <summary>gen_osm.py nearest_road and project: the nearest road within <paramref name="reach"/> of a point, and
    /// the nearest point of that road (its whole chain) to the point.</summary>
    private static (List<RoadLine> Road, double D, (double E, double N) Foot)? NearestRoad(double e, double n, double reach, List<RoadLine> lines,
                                                                                         Dictionary<long, int> uses, Dictionary<long, List<RoadLine>> ends)
    {
        RoadLine? best = null;
        double bestD = double.MaxValue;
        foreach (var l in lines)
        {
            var (d, _) = Project(l.Pts, e, n);
            if (d <= reach && d < bestD) { bestD = d; best = l; }
        }
        if (best == null) return null;
        var chain = Chain(best, lines, uses, ends);
        double dd = double.MaxValue;
        (double, double) foot = default;
        foreach (var l in chain)
        {
            var (d, f) = Project(l.Pts, e, n);
            if (d < dd) { dd = d; foot = f; }
        }
        return (chain, dd, foot);
    }

    /// <summary>The distance from a road's line to a footprint: 0 where they cross or the line is inside it.</summary>
    private static double LineToRing(List<(double E, double N)> line, List<(double X, double Z)> ring)
    {
        double best = double.MaxValue;
        foreach (var (x, z) in ring) best = Math.Min(best, Project(line, x, z).D);
        for (int i = 0; i < line.Count; i++)
        {
            if (WorldBuildings.Pip(line[i].E, line[i].N, ring)) return 0;
            for (int k = 0; k < ring.Count - 1; k++)
            {
                best = Math.Min(best, SegPoint(line[i], (ring[k].X, ring[k].Z), (ring[k + 1].X, ring[k + 1].Z)).D);
                if (i < line.Count - 1 && Crosses(line[i], line[i + 1], ring[k], ring[k + 1])) return 0;
            }
        }
        return best;

        static bool Crosses((double X, double Z) a, (double X, double Z) b, (double X, double Z) c, (double X, double Z) d)
        {
            double C(( double X, double Z) o, (double X, double Z) p, (double X, double Z) q) => (p.X - o.X) * (q.Z - o.Z) - (p.Z - o.Z) * (q.X - o.X);
            double d1 = C(a, b, c), d2 = C(a, b, d), d3 = C(c, d, a), d4 = C(c, d, b);
            return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
        }
    }

    // ═══ Siting ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A building as a tile sees it: its footprint, shape and plan in the metres of the tile its middle is in
    /// (<see cref="Owner"/>), whether this tile stores it, and the points whose ground it is set on.</summary>
    private sealed class Sited
    {
        public required Footprint Fp;
        public required WorldBuildings.Shape Shape;
        public required WorldTileKey Owner;
        public required string Kind;
        public string? PlaceKind;
        public required string Name, Seed;
        public string? Suffix;
        public (double X, double Z) Street;
        /// <summary>Where its pad's height is read (the owner's metres), or null for a solid box.</summary>
        public (double X, double Z)? PadAt;
        /// <summary>Whether it comes within <see cref="BuildingNear"/> of the tile (its floor grades the tile's ground);
        /// one further out is sited for its driveway alone.</summary>
        public bool Near;
        /// <summary>Its driveway, made up as gen_osm.py makes one for a lot with none mapped (the zone's metres), or null.</summary>
        public List<(double E, double N)>? Drive;
    }

    /// <summary>
    /// The buildings that matter to a tile: every one whose bounds come within <see cref="BuildingNear"/> of it, made
    /// (not too small, too big, a roof, on a road or reaching into a real place), sited and planned. Decided from the
    /// building and the roads near it alone, so every tile that sees a building sees the same one.
    /// </summary>
    private List<Sited> Site(WorldTileKey key, IReadOnlyList<Footprint> footprints, IReadOnlyList<OsmWay> ways, Func<double, double, double, double, bool> inPlace,
                             double reach = BuildingNear)
    {
        var lines = RoadLines(key, ways);
        // The ends of every mapped driveway near: a house with one beside it is given no made-up one.
        var driveEnds = new List<(double E, double N)>();
        foreach (var w in ways)
            if (IsDrive(w))
                foreach (int k in new[] { 0, w.Nodes.Length - 1 })
                    driveEnds.Add(Utm.FromLatLon(w.Lat[k], w.Lon[k], key.Zone, key.North));
        var uses = new Dictionary<long, int>();
        var ends = new Dictionary<long, List<RoadLine>>();
        foreach (var l in lines)
        {
            foreach (long node in l.Way.Nodes) uses[node] = uses.GetValueOrDefault(node) + 1;
            foreach (long node in new[] { l.Way.Nodes[0], l.Way.Nodes[^1] })
            {
                if (!ends.TryGetValue(node, out var list)) ends[node] = list = new List<RoadLine>();
                list.Add(l);
            }
        }
        double t0x = key.Easting - reach, t1x = key.Easting + WorldTileKey.TileMetres + reach;
        double t0z = key.Northing - reach, t1z = key.Northing + WorldTileKey.TileMetres + reach;

        // Every footprint in the zone's metres, and the ones that would be houses by their own looks: a smaller one
        // beside a bigger one is its garage or workshop.
        var rings = new List<(Footprint Fp, List<(double X, double Z)> Ring, double Mx, double Mz, double Area, bool House)>();
        foreach (var fp in footprints)
        {
            var ring = new List<(double X, double Z)>(fp.Lat.Length + 1);
            for (int k = 0; k < fp.Lat.Length; k++) ring.Add(Utm.FromLatLon(fp.Lat[k], fp.Lon[k], key.Zone, key.North));
            if (ring.Count < 4) continue;
            if (ring[0] != ring[^1]) ring.Add(ring[0]);
            double a = 0, sx = 0, sz = 0;
            for (int k = 0; k < ring.Count - 1; k++)
            {
                a += ring[k].X * ring[k + 1].Z - ring[k + 1].X * ring[k].Z;
                sx += ring[k].X; sz += ring[k].Z;
            }
            a = Math.Abs(a) / 2;
            string cls = fp.Class ?? "";
            rings.Add((fp, ring, sx / (ring.Count - 1), sz / (ring.Count - 1), a,
                       fp.Name == null && (cls.Length == 0 || Homes.Contains(cls)) && a >= 45 && a <= 600));
        }
        bool Beside(int i)
        {
            var me = rings[i];
            if (!me.House || me.Area >= 160) return false;
            for (int j = 0; j < rings.Count; j++)
            {
                var o = rings[j];
                if (j == i || !o.House) continue;
                if (o.Area < me.Area || (o.Area == me.Area && string.CompareOrdinal(o.Fp.Id, me.Fp.Id) > 0)) continue;
                if ((o.Mx - me.Mx) * (o.Mx - me.Mx) + (o.Mz - me.Mz) * (o.Mz - me.Mz) < BesideAHouse * BesideAHouse) return true;
            }
            return false;
        }

        var sited = new List<Sited>();
        for (int ri = 0; ri < rings.Count; ri++)
        {
            var (fp, ring, _, _, _, _) = rings[ri];
            double x0 = ring.Min(p => p.X), x1 = ring.Max(p => p.X), z0 = ring.Min(p => p.Z), z1 = ring.Max(p => p.Z);
            if (x1 < t0x || x0 > t1x || z1 < t0z || z0 > t1z) continue;
            if (Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) > MaxBuildingAcross) continue;
            if (inPlace(x0, x1, z0, z1)) continue;
            // The building's own metres: those of the tile its middle is in, the same from every tile.
            double mx = 0, mz = 0;
            for (int k = 0; k < ring.Count - 1; k++) { mx += ring[k].X; mz += ring[k].Z; }
            var owner = WorldTileKey.Of(key.Zone, key.North, mx / (ring.Count - 1), mz / (ring.Count - 1));
            var local = ring.Select(p => (p.X - owner.Easting, p.Z - owner.Northing)).ToList();
            var shape = new WorldBuildings.Shape(local);
            if (shape.Area < 6.0) continue;
            var classed = Classify(fp, shape, Beside(ri));
            if (classed == null) continue;
            var (kind, placeKind) = classed.Value;
            // A building standing on a road is a mistake in somebody's data, or a canopy over a forecourt: the road
            // is kept and the building is not made.
            bool onRoad = false;
            foreach (var l in lines)
            {
                double half = l.Width / 2;
                if (l.Pts.Max(p => p.E) < x0 - half || l.Pts.Min(p => p.E) > x1 + half || l.Pts.Max(p => p.N) < z0 - half || l.Pts.Min(p => p.N) > z1 + half) continue;
                if (LineToRing(l.Pts, ring) < half) { onRoad = true; break; }
            }
            if (onRoad) continue;

            double cx = shape.Cx + owner.Easting, cz = shape.Cz + owner.Northing;
            string word = char.ToUpperInvariant(kind[0]) + kind[1..].Replace('_', ' ');
            // The nearest road within 400 m is the one within 200 m too, when it is that near.
            var facing = NearestRoad(cx, cz, FacingRoadReach, lines, uses, ends);
            var near = facing is { } fr && fr.D <= PadRoadReach ? facing : null;
            string label = fp.Name ?? (near is { } nr ? $"{word} off {nr.Road[0].Name}" : word);
            var street = facing is { } f ? (f.Foot.E - owner.Easting, f.Foot.N - owner.Northing) : (shape.Cx, shape.Cz - 10);
            (double, double)? padAt = null;
            if (!WorldBuildings.IsSolidBox(kind))
            {
                // gen_osm.py pad_of: the ground at the middle of its wall nearest the road.
                padAt = (shape.Cx, shape.Cz);
                if (near is { } pr)
                {
                    double bestD = double.MaxValue;
                    for (int i = 0; i < ring.Count - 1; i++)
                    {
                        double ex = (ring[i].X + ring[i + 1].X) / 2, ez = (ring[i].Z + ring[i + 1].Z) / 2;
                        double d = pr.Road.Min(l => Project(l.Pts, ex, ez).D);
                        if (d < bestD) { bestD = d; padAt = (ex - owner.Easting, ez - owner.Northing); }
                    }
                }
            }
            bool homes = kind is "house" or "mobile_home";
            string seed = fp.Name != null ? label : fp.Id;
            bool close = x1 >= key.Easting - BuildingNear && x0 <= key.Easting + WorldTileKey.TileMetres + BuildingNear
                        && z1 >= key.Northing - BuildingNear && z0 <= key.Northing + WorldTileKey.TileMetres + BuildingNear;
            List<(double E, double N)>? drive = null;
            if (homes && facing is { } fd && !driveEnds.Any(p => WorldBuildings.Pip(p.E, p.N, ring) || Project(ring.Select(q => (q.X, q.Z)).ToList(), p.E, p.N).D < MappedDriveNear))
                drive = DriveFor(ring, shape.Angle, (cx, cz), fd.Foot, fd.Road[0].Width, seed);
            sited.Add(new Sited
            {
                Fp = fp, Shape = shape, Owner = owner, Kind = kind, PlaceKind = placeKind,
                Name = label, Seed = seed,
                Suffix = homes && fp.Name != null ? ", house" : null,
                Street = street, PadAt = padAt, Near = close, Drive = drive,
            });
        }
        // In gen_osm.py's order: by the middle, west to east then south to north.
        return sited.OrderBy(b => Math.Round(b.Shape.Cx + b.Owner.Easting, 2)).ThenBy(b => Math.Round(b.Shape.Cz + b.Owner.Northing, 2))
                    .ThenBy(b => b.Fp.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// gen_osm.py's driveway for a lot with none mapped, without the lot: from the edge of the road the house faces
    /// straight in to its front wall, 2.4 m in from the end of the house h01 picks, in a frame square to the road
    /// (or to the house, where one of its sides is within 35 degrees of that); null where the house is at the road or
    /// further than <see cref="MaxDrive"/> from it.
    /// </summary>
    private static List<(double E, double N)>? DriveFor(List<(double X, double Z)> ring, double houseAngle, (double X, double Z) middle,
                                                         (double E, double N) foot, double roadWidth, string seed)
    {
        double nx = middle.X - foot.E, nz = middle.Z - foot.N, l = Math.Sqrt(nx * nx + nz * nz);
        if (l < 1e-6) return null;
        nx /= l; nz /= l;
        double ang = Math.Atan2(nz, nx) - Math.PI / 2;
        double bestDot = double.MinValue, best = ang;
        for (int k = 0; k < 4; k++)
        {
            double cand = houseAngle + k * Math.PI / 2, dot = -Math.Sin(cand) * nx + Math.Cos(cand) * nz;
            if (dot > bestDot) { bestDot = dot; best = cand; }
        }
        if (bestDot > Math.Cos(35 * Math.PI / 180)) ang = best;
        var f = new WorldBuildings.Frame(foot.E, foot.N, ang);
        double front = roadWidth / 2 + Verge;
        var h = WorldBuildings.Extents(f, ring);
        if (h.V0 < front + 1) front = Math.Min(front, h.V0 - 1);
        double du = WorldBuildings.H01(seed, "drive") < 0.5 ? h.U0 + 2.4 : h.U1 - 2.4;
        double v0 = front - Verge, v1 = h.V0 - 0.2;
        if (v1 - v0 < 2.0 || v1 - v0 > MaxDrive) return null;
        return new List<(double E, double N)> { f.W(du, v0), f.W(du, v1) };
    }

    /// <summary>
    /// How far round a tile its ground must be asked for, metres, for the buildings near it: every pad it is graded
    /// to, and the ground under every solid box it stores, read from posts that every tile asks the same survey for.
    /// <see cref="MarginMetres"/> at least, in tens of metres, at most <see cref="MaxMargin"/>.
    /// </summary>
    public double MarginFor(WorldTileKey key, IReadOnlyList<Footprint> footprints, IReadOnlyList<OsmWay> ways)
    {
        if (footprints.Count == 0) return MarginMetres;
        double over = 0;
        void Need(double e, double n)
        {
            over = Math.Max(over, Math.Max(Math.Max(key.Easting - e, e - key.Easting - WorldTileKey.TileMetres),
                                           Math.Max(key.Northing - n, n - key.Northing - WorldTileKey.TileMetres)));
        }
        foreach (var b in Site(key, footprints, ways, (_, _, _, _) => false))
        {
            if (!b.Near) continue;
            if (b.PadAt is { } p) Need(p.X + b.Owner.Easting, p.Z + b.Owner.Northing);
            else if (b.Owner == key)
            {
                var f = new WorldBuildings.Frame(b.Shape.Cx, b.Shape.Cz, b.Shape.Angle);
                var (u0, u1, v0, v1) = WorldBuildings.Extents(f, b.Shape.Ring);
                foreach (var (u, v) in new[] { (u0, v0), (u1, v0), (u0, v1), (u1, v1) })
                {
                    var (x, z) = f.W(u, v);
                    Need(x + b.Owner.Easting, z + b.Owner.Northing);
                }
            }
        }
        // Two posts more: a height between posts reads the posts round it.
        return Math.Clamp(Math.Ceiling((over + 2 * WorldTileService.Spacing) / 10) * 10, MarginMetres, MaxMargin);
    }

    /// <summary>
    /// Lays the buildings near a tile: those whose middle is in it as entities (ids from 1, in the tile's own metres,
    /// heights over the sea), and the floors of every one near it as slabs for the ground (in the window's metres) and
    /// boxes the woods keep clear of. How many it stores, and the attribution of their sources.
    /// </summary>
    private (int Built, string? Sources) LayBuildings(WorldTileKey key, IReadOnlyList<Footprint> footprints, IReadOnlyList<OsmWay> ways, Ground ground,
                                                     double west, double south, Func<double, double, double, double, bool> inPlace,
                                                     List<EntityData> entities, List<TerrainBuilder.Slab> slabs,
                                                     List<(WorldBuildings.Frame F, double U0, double U1, double V0, double V1)> boxes, Action<Piece> place)
    {
        // The made-up driveways of every house near enough for one to reach the tile, laid as a road's pieces are.
        foreach (var b in Site(key, footprints, ways, inPlace, BuildingNear + MaxDrive))
            if (b.Drive != null)
                LayLine(b.Drive, 3.6, "concrete_floor", YDrive, $"{b.Name} driveway", "drives", ground.At, place);
        int built = 0;
        var mine = new List<Footprint>();
        foreach (var b in BuildEach(key, footprints, ways, ground, inPlace))
        {
            if (b.Owner == key)
            {
                entities.AddRange(b.Entities);
                mine.Add(b.Fp);
                built++;
            }
            boxes.Add(b.Box);
            // The floors it rests on: the ground is graded to them.
            if (b.Pad != null)
                foreach (var e in b.Entities.Where(x => x.Name != null && x.Name.EndsWith(" floor", StringComparison.Ordinal) && x.Layer == "structure"))
                {
                    var size = _prefabs.TryGetValue(e.PrefabId, out var pf) ? pf.Size : Vector3.One;
                    var centre = new Vector3((float)(e.Position.X + key.Easting - west), e.Position.Y, (float)(e.Position.Z + key.Northing - south));
                    if (TerrainBuilder.SlabOf(centre, e.Rotation, size * e.Scale) is { } slab) slabs.Add(slab);
                }
        }
        return (built, built > 0 ? Footprint.Attribution(Buildings?.Name ?? "Buildings", mine) : null);
    }

    /// <summary>A building as made for a tile: what it was made from, the tile it belongs to, what it is, its pad (null
    /// for a solid box), where the street it faces is (the zone's metres), its entities (in the tile's metres; ids from 1
    /// where the tile stores it), and the box the woods keep clear of.</summary>
    internal sealed record MadeBuilding(Footprint Fp, WorldTileKey Owner, string Kind, string Name, double? Pad, (double X, double Z) Street,
                                        List<EntityData> Entities, (WorldBuildings.Frame F, double U0, double U1, double V0, double V1) Box);

    private IEnumerable<MadeBuilding> BuildEach(WorldTileKey key, IReadOnlyList<Footprint> footprints, IReadOnlyList<OsmWay> ways, Ground ground,
                                               Func<double, double, double, double, bool> inPlace)
    {
        int nextId = 0;
        foreach (var b in Site(key, footprints, ways, inPlace))
        {
            if (!b.Near) continue;
            var o = b.Owner;
            double G(double x, double z) => ground.At(x + o.Easting, z + o.Northing);
            double? pad = b.PadAt is { } p ? G(p.X, p.Z) : null;
            var plan = new WorldBuildings.Plan
            {
                Shape = b.Shape, Kind = b.Kind, Name = b.Name, Seed = b.Seed, PlaceKind = b.PlaceKind, Height = b.Fp.Height,
                Street = b.Street, Pad = pad, RoomSuffix = b.Suffix,
            };
            bool owned = o == key;
            int ids = owned ? nextId : 0;
            var made = WorldBuildings.Build(plan, _prefabs, G, (o.Easting - key.Easting, o.Northing - key.Northing), ref ids, measured: true);
            if (owned) nextId = ids;
            var frame = new WorldBuildings.Frame(b.Shape.Cx + o.Easting, b.Shape.Cz + o.Northing, b.Shape.Angle);
            var (u0, u1, v0, v1) = WorldBuildings.Extents(new WorldBuildings.Frame(b.Shape.Cx, b.Shape.Cz, b.Shape.Angle), b.Shape.Ring);
            yield return new MadeBuilding(b.Fp, o, b.Kind, b.Name, pad, (b.Street.X + o.Easting, b.Street.Z + o.Northing), made, (frame, u0, u1, v0, v1));
        }
    }

    /// <summary>The buildings a tile would make from these footprints, roads and window (<see cref="PostsFor"/> a side
    /// <paramref name="margin"/> metres round it), each with what was decided for it: for the tests that hold the port
    /// to gen_osm.py.</summary>
    internal List<MadeBuilding> BuildingsOf(WorldTileKey key, IReadOnlyList<Footprint> footprints, IReadOnlyList<OsmWay> ways, float[] window, double margin)
    {
        var ground = new Ground(key.Easting - margin, key.Northing - margin, window.Select(v => float.IsFinite(v) ? v : 0f).ToArray(), PostsFor(margin));
        return BuildEach(key, footprints, ways, ground, (_, _, _, _) => false).ToList();
    }
}
