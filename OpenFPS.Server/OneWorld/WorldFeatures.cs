using System.Numerics;
using OpenFPS.Server.Core;
using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// What stands on a world tile outside the real places, from OpenStreetMap (docs/WORLD_STREAMING.md, Stage 2:
/// the generator): the roads that can be driven, each way in its own width and surface, laid in pieces on the
/// ground as tools/gen_osm.py lays a place's roads, their sidewalks where the way says it has them, a named
/// place over each road and at each junction, and the ground graded under them.
///
/// <para><b>The same answer whichever tile is made first.</b> Everything is decided from the whole way, in the
/// zone's own metres, never from the tile: where a road's pieces start and end, how far each is lengthened at a
/// bend, which junctions are one. A piece belongs to the one tile its middle is in, which alone stores it; every
/// tile it overlaps is graded under it, because each tile works out every piece that reaches into it and its
/// margin. The ground under a road is graded from the posts of the tile and a margin of
/// <see cref="MarginMetres"/> round it, so two tiles grade their shared edge alike.</para>
///
/// <para>Ported from gen_osm.py and kept the same: the classes of road and their widths
/// (<see cref="RoadClass"/>, ROAD_CLASS), the surfaces (SURFACE), way_width, lay_line with road_y, ground_all's
/// pitch of a piece, the zones over the roads, and the junctions. What a place has that this has not yet:
/// verges, the drives, paths, rail, water, buildings, lots, addresses and woods; and no road becomes a road to
/// route traffic on (RoadData) yet.</para>
/// </summary>
public sealed partial class WorldFeatures
{
    /// <summary>How far round a tile its roads are worked out, and its ground asked for, metres: a piece of a
    /// neighbour's road reaches about 35 m in at most (20 m long, lengthened at a bend by up to its width) and
    /// its height reads the ground 10 m round each end.</summary>
    public const double MarginMetres = 50;

    /// <summary>The posts a side of the ground asked for: the tile's and the margin's, 2 m apart.</summary>
    public static readonly int WindowPosts = (int)Math.Round((WorldTileKey.TileMetres + 2 * MarginMetres) / WorldTileService.Spacing) + 1;

    /// <summary>The tile's own posts start this many posts into the window.</summary>
    public static readonly int WindowOffset = (int)Math.Round(MarginMetres / WorldTileService.Spacing);

    // gen_osm.py's constants.
    public const double LaneWidth = 3.4, Verge = 2.5, SidewalkWidth = 1.5;
    public const double SegMax = 20.0, Seam = 0.05, RoadSmooth = 10.0, ZoneHeight = 4.0;
    public const float YRoad = 0.08f, YWalk = 0.12f, YDrive = 0.06f;

    public IOsmSource Osm { get; }
    private readonly IReadOnlyDictionary<string, Vector3> _sizes;

    /// <summary>Whether a tile is a real place's (WorldPlaces): nothing made here reaches into one.</summary>
    public Func<WorldTileKey, bool>? IsPlaced { get; set; }

    public string Name => Osm.Name;

    /// <param name="prefabSizes">Each prefab's collider size: a box's scale is its size over this.</param>
    /// <param name="buildings">Where the buildings come from (Overture), or null for roads and woods alone.</param>
    /// <param name="prefabs">What the buildings' prefabs are (sizes, materials, which doors swing); needed with
    /// <paramref name="buildings"/>.</param>
    public WorldFeatures(IOsmSource osm, IReadOnlyDictionary<string, Vector3> prefabSizes, IBuildingSource? buildings = null,
                         IReadOnlyDictionary<string, WorldBuildings.Prefab>? prefabs = null)
    {
        Osm = osm;
        _sizes = prefabSizes;
        Buildings = buildings;
        _prefabs = prefabs ?? prefabSizes.ToDictionary(kv => kv.Key, kv => new WorldBuildings.Prefab(kv.Value, "None", kv.Key is "door" or "glass_pull_door"),
                                                       StringComparer.OrdinalIgnoreCase);
    }

    // ═══ What a road is (gen_osm.py ROAD_CLASS, SURFACE, way_width) ═════════════════════════════════════

    /// <summary>An OpenStreetMap highway that is a road: our type, its rank, and its width with two lanes.</summary>
    public static readonly IReadOnlyDictionary<string, (string Type, int Rank, double Width)> RoadClass =
        new Dictionary<string, (string, int, double)>
        {
            ["motorway"] = ("arterial", 5, 7.4), ["trunk"] = ("arterial", 5, 7.4), ["primary"] = ("arterial", 4, 7.2),
            ["secondary"] = ("arterial", 4, 7.2), ["tertiary"] = ("collector", 3, 6.7),
            ["motorway_link"] = ("arterial", 4, 5.0), ["trunk_link"] = ("arterial", 4, 5.0), ["primary_link"] = ("arterial", 4, 5.0),
            ["secondary_link"] = ("collector", 3, 5.0), ["tertiary_link"] = ("collector", 3, 5.0),
            ["unclassified"] = ("residential", 2, 6.0), ["residential"] = ("residential", 2, 6.0), ["road"] = ("residential", 2, 6.0),
            ["living_street"] = ("residential", 2, 5.5), ["service"] = ("service", 1, 4.0),
        };

    private static readonly HashSet<string> NotRoads = new() { "driveway", "parking_aisle", "drive-through", "emergency_access" };

    /// <summary>OSM surface to the prefab a road of it is laid with.</summary>
    public static readonly IReadOnlyDictionary<string, string> Surface = new Dictionary<string, string>
    {
        ["asphalt"] = "asphalt_road", ["paved"] = "asphalt_road", ["chipseal"] = "asphalt_road",
        ["concrete"] = "concrete_floor", ["concrete:plates"] = "concrete_floor", ["concrete:lanes"] = "concrete_floor",
        ["paving_stones"] = "brick_floor", ["sett"] = "brick_floor", ["bricks"] = "brick_floor",
        ["gravel"] = "gravel_floor", ["fine_gravel"] = "gravel_floor", ["compacted"] = "gravel_floor",
        ["unpaved"] = "gravel_floor", ["pebblestone"] = "gravel_floor",
        ["dirt"] = "dirt_floor", ["ground"] = "dirt_floor", ["earth"] = "dirt_floor", ["mud"] = "dirt_floor", ["sand"] = "dirt_floor",
        ["grass"] = "grass_floor", ["wood"] = "wood_floor",
    };

    public static string SurfaceOf(OsmWay w, string fallback = "asphalt")
        => Surface.TryGetValue(w.Tag("surface", fallback), out var p) ? p : Surface[fallback];

    /// <summary>A way a car drives on (gen_osm.py ways_by_kind["road"]): not a drive, a parking aisle, an area,
    /// or a one-way service way.</summary>
    public static bool IsRoad(OsmWay w)
    {
        string hw = w.Tag("highway");
        return RoadClass.ContainsKey(hw) && !NotRoads.Contains(w.Tag("service")) && w.Tag("area") != "yes"
               && !(hw == "service" && w.Tag("oneway") is "yes" or "-1") && w.Nodes.Length >= 2;
    }

    public static double WidthOf(OsmWay w) => WidthOf(w, w.Tag("highway"));

    /// <summary>gen_osm.py way_width: a way's width as if it were a <paramref name="hw"/>.</summary>
    public static double WidthOf(OsmWay w, string hw)
    {
        if (w.Tags.TryGetValue("width", out var ws))
        {
            var m = System.Text.RegularExpressions.Regex.Match(ws, @"^[0-9.]+");
            if (m.Success && double.TryParse(m.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
                return Math.Max(2.5, v);
        }
        double b = RoadClass[hw].Width;
        int lanes = int.TryParse(w.Tag("lanes", "0"), out int l) ? l : 0;
        if (lanes > 2) return Math.Round(lanes * LaneWidth + 0.6, 2);
        if (lanes == 1 || (w.Tag("oneway") == "yes" && hw == "service")) return Math.Max(3.5, Math.Min(b, 4.0));
        return b;
    }

    /// <summary>What a road is called: its name, or what kind of road it is.</summary>
    /// <summary>A driveway, parking aisle or private service way (gen_osm.py ways_by_kind["drive"]): a surface, not a
    /// road to route on.</summary>
    public static bool IsDrive(OsmWay w)
    {
        string hw = w.Tag("highway");
        if (w.Nodes.Length < 2 || IsRoad(w)) return false;
        return hw == "service" || (RoadClass.ContainsKey(hw) && NotRoads.Contains(w.Tag("service")));
    }

    public static string NameOf(OsmWay w)
    {
        if (w.Tag("name") is { Length: > 0 } n) return n;
        if (w.Tag("access") is "private" or "no") return "Private road";
        return w.Tag("highway") == "service" ? "Service road" : "Unnamed road";
    }

    // ═══ Asking ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The roads reaching into a tile and its margin, from the source, whole.</summary>
    public Task<IReadOnlyList<OsmWay>> WaysAsync(WorldTileKey key, CancellationToken ct)
    {
        // With buildings, far enough for the road a building near the tile faces (BuildingRoadReach).
        double reach = Buildings != null ? BuildingRoadReach : MarginMetres + 50;
        double w = key.Easting - reach, s = key.Northing - reach;
        double e = key.Easting + WorldTileKey.TileMetres + reach, n = key.Northing + WorldTileKey.TileMetres + reach;
        double lat0 = 90, lat1 = -90, lon0 = 180, lon1 = -180;
        foreach (var (pe, pn) in new[] { (w, s), (e, s), (w, n), (e, n), ((w + e) / 2, s), ((w + e) / 2, n) })
        {
            var (lat, lon) = Utm.ToLatLon(key.Zone, key.North, pe, pn);
            lat0 = Math.Min(lat0, lat); lat1 = Math.Max(lat1, lat);
            lon0 = Math.Min(lon0, lon); lon1 = Math.Max(lon1, lon);
        }
        return Osm.HighwaysAsync(lat0, lon0, lat1, lon1, ct);
    }

    // ═══ Laying ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A tile laid: what stands on it (in its own metres, y over the sea), its ground graded to the
    /// roads (<see cref="WorldTileService.Posts"/> a side, over the sea), and how many roads reach into it.</summary>
    public sealed record Laid(List<EntityData> Entities, float[] Heights, int Roads, int Pieces, int Trees = 0, int Buildings = 0,
                              string? BuildingSources = null);

    /// <summary>The posts a side of a window reaching <paramref name="margin"/> metres round a tile.</summary>
    public static int PostsFor(double margin) => (int)Math.Round((WorldTileKey.TileMetres + 2 * margin) / WorldTileService.Spacing) + 1;

    /// <summary>The ground of the window, posts every 2 m from its south-west corner, over the sea.</summary>
    private sealed class Ground(double west, double south, float[] h, int n)
    {
        private readonly int _n = n;
        private const double S = WorldTileService.Spacing;

        /// <summary>gen_osm.py ground(): bilinear between posts, held at the edge.</summary>
        public double At(double e, double n)
        {
            double fx = (e - west) / S, fz = (n - south) / S;
            int i = Math.Clamp((int)Math.Floor(fx), 0, _n - 2), j = Math.Clamp((int)Math.Floor(fz), 0, _n - 2);
            double tx = Math.Clamp(fx - i, 0, 1), tz = Math.Clamp(fz - j, 0, 1);
            int k = j * _n + i;
            double h00 = h[k], h10 = h[k + 1], h01 = h[k + _n], h11 = h[k + _n + 1];
            return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz;
        }

        /// <summary>gen_osm.py road_y(): the ground averaged over a 20 m square.</summary>
        public double Road(double e, double n)
        {
            double sum = 0;
            for (int a = -2; a <= 2; a++)
                for (int b = -2; b <= 2; b++)
                    sum += At(e + a * 0.5 * RoadSmooth, n + b * 0.5 * RoadSmooth);
            return sum / 25;
        }
    }

    /// <summary>A box along a run of a line, before it is placed: gen_osm.py seg_box's arguments.</summary>
    private readonly record struct Piece(string Prefab, double Ax, double Az, double Bx, double Bz, double Width,
                                         float Y0, float Y1, double Ext0, double Ext1, double Hp, double Hq, string Name, string Layer);

    /// <summary>
    /// Lays a tile from the ways that reach it, the ground of its window (<see cref="PostsFor"/> a side from
    /// <paramref name="margin"/> metres south-west of its corner, over the sea; NaN where nothing was surveyed;
    /// <see cref="WindowPosts"/> a side for the usual <see cref="MarginMetres"/>), its cells' land cover classes
    /// (null: no woods), and the buildings near it (null: none; <see cref="MarginFor"/> says how wide a window they need).
    /// </summary>
    public Laid Lay(WorldTileKey key, IReadOnlyList<OsmWay> ways, float[] window, byte[]? classes = null,
                    IReadOnlyList<Footprint>? footprints = null, double margin = MarginMetres)
    {
        int windowPosts = PostsFor(margin);
        if (window.Length != windowPosts * windowPosts) throw new ArgumentException($"a window of {window.Length} posts for a margin of {margin} m");
        double west = key.Easting - margin, south = key.Northing - margin;
        double winEast = west + (windowPosts - 1) * WorldTileService.Spacing, winNorth = south + (windowPosts - 1) * WorldTileService.Spacing;
        var raw = window.Select(v => float.IsFinite(v) ? v : 0f).ToArray();
        var ground = new Ground(west, south, raw, windowPosts);

        var entities = new List<EntityData>();
        var slabs = new List<TerrainBuilder.Slab>();
        var strips = new List<(double Ax, double Az, double Bx, double Bz, double Half)>();
        int roads = 0, pieces = 0;

        // Every road way that comes near, in the zone's metres, whole.
        var lines = new List<Line>();
        foreach (var w in ways)
        {
            if (!IsRoad(w)) continue;
            var nodes = new (double E, double N)[w.Nodes.Length];
            var pts = new List<(double, double)>(w.Nodes.Length);
            double e0 = double.MaxValue, e1 = double.MinValue, n0 = double.MaxValue, n1 = double.MinValue;
            for (int k = 0; k < w.Nodes.Length; k++)
            {
                var (pe, pn) = Utm.FromLatLon(w.Lat[k], w.Lon[k], key.Zone, key.North);
                nodes[k] = (pe, pn);
                // No point twice: a zero-length segment has no direction.
                if (pts.Count > 0 && Math.Abs(pe - pts[^1].Item1) + Math.Abs(pn - pts[^1].Item2) < 1e-6) continue;
                pts.Add((pe, pn));
                e0 = Math.Min(e0, pe); e1 = Math.Max(e1, pe); n0 = Math.Min(n0, pn); n1 = Math.Max(n1, pn);
            }
            if (pts.Count < 2) continue;
            double reach = WidthOf(w) + 2 * SidewalkWidth + 20;
            if (e1 < west - reach || e0 > winEast + reach || n1 < south - reach || n0 > winNorth + reach) continue;
            lines.Add(new Line(w, pts, nodes));
        }

        bool Touches(double x0, double x1, double z0, double z1)
            => x1 >= west && x0 <= winEast && z1 >= south && z0 <= winNorth;

        bool InPlace(double x0, double x1, double z0, double z1)
        {
            if (IsPlaced == null) return false;
            for (int tx = (int)Math.Floor(x0 / WorldTileKey.TileMetres); tx <= (int)Math.Floor(x1 / WorldTileKey.TileMetres); tx++)
                for (int tz = (int)Math.Floor(z0 / WorldTileKey.TileMetres); tz <= (int)Math.Floor(z1 / WorldTileKey.TileMetres); tz++)
                    if (IsPlaced(new WorldTileKey(key.Zone, key.North, tx, tz))) return true;
            return false;
        }

        bool Mine(double e, double n) => WorldTileKey.Of(key.Zone, key.North, e, n) == key;

        void Place(Piece p)
        {
            var box = PieceBox(p);
            var (x0, x1, z0, z1) = box.Bounds;
            if (!Touches(x0, x1, z0, z1) || InPlace(x0, x1, z0, z1)) return;
            strips.Add((p.Ax, p.Az, p.Bx, p.Bz, p.Width / 2));
            var size = Size(p.Prefab);
            if (TerrainBuilder.SlabOf(new Vector3((float)(box.Cx - west), box.Cy, (float)(box.Cz - south)), box.Turn, box.Extent) is { } slab)
                slabs.Add(slab);
            if (!Mine(box.Cx, box.Cz)) return;
            entities.Add(new EntityData
            {
                PrefabId = p.Prefab,
                Position = new Vector3(R4(box.Cx - key.Easting), R4(box.Cy), R4(box.Cz - key.Northing)),
                Rotation = box.Turn,
                Scale = new Vector3(R4(box.Extent.X / size.X), R4(box.Extent.Y / size.Y), R4(box.Extent.Z / size.Z)),
                Name = p.Name,
                Layer = p.Layer,
            });
            pieces++;
        }

        foreach (var (w, pts, _) in lines)
        {
            double width = WidthOf(w);
            string name = NameOf(w), prefab = SurfaceOf(w);
            int before = pieces;
            LayLine(pts, width, prefab, YRoad, name, "roads", ground.Road, Place);
            // A sidewalk the road's own tags say it has, out from the kerb past a planting strip, level with the
            // road beside it.
            var sides = new List<int>();
            switch (w.Tag("sidewalk")) { case "both": sides.AddRange(new[] { 1, -1 }); break; case "left": sides.Add(1); break; case "right": sides.Add(-1); break; }
            if (w.Tag("sidewalk:left") == "yes" && !sides.Contains(1)) sides.Add(1);
            if (w.Tag("sidewalk:right") == "yes" && !sides.Contains(-1)) sides.Add(-1);
            foreach (int side in sides)
            {
                double off = side * (width / 2 + 1.0 + SidewalkWidth / 2);
                LayLine(OffsetLine(pts, off), SidewalkWidth, "concrete_floor", YWalk, name + " sidewalk", "paths",
                        (e, n) => { var f = Project(pts, e, n).Foot; return ground.Road(f.E, f.N); }, Place);
            }
            if (pieces > before) roads++;
        }

        // Driveways, parking aisles and private service ways (gen_osm.py DRIVES), on the ground under them.
        foreach (var w in ways)
        {
            if (!IsDrive(w)) continue;
            var pts = PointsOf(w, key);
            if (pts.Count < 2) continue;
            double reach = 30;
            if (pts.Max(p => p.E) < west - reach || pts.Min(p => p.E) > winEast + reach || pts.Max(p => p.N) < south - reach || pts.Min(p => p.N) > winNorth + reach) continue;
            bool driveway = w.Tag("service") == "driveway";
            LayLine(pts, driveway ? 3.6 : WidthOf(w, "service"), SurfaceOf(w, driveway ? "concrete" : "asphalt"), YDrive,
                    w.Tag("service") == "parking_aisle" ? "Parking aisle" : "Driveway", "drives", ground.At, Place, tol: 0.6);
        }

        // The zones over the roads: walking along one says nothing, stepping onto it from elsewhere says its name.
        foreach (var (w, pts, _) in lines)
        {
            var sp = Simplify(pts, 2.5);
            double half = WidthOf(w) / 2 + Verge;
            for (int i = 0; i < sp.Count - 1; i++)
            {
                var (ax, az) = sp[i];
                var (bx, bz) = sp[i + 1];
                double L = Dist(sp[i], sp[i + 1]);
                if (L < 0.5) continue;
                double e0 = i > 0 ? half : 0, e1 = i < sp.Count - 2 ? half : 0;
                Zone(NameOf(w), ax, az, Math.Atan2(bz - az, bx - ax), -e0, L + e1, -half, half);
            }
        }

        foreach (var j in Junctions(lines))
        {
            double r = j.Radius + 1.0;
            Zone(j.Name + " junction", j.E, j.N, 0, -r, r, -r, r);
        }

        void Zone(string name, double ox, double oz, double a, double u0, double u1, double v0, double v1)
        {
            var f = new Frame(ox, oz, a);
            var (cx, cz) = f.W((u0 + u1) / 2, (v0 + v1) / 2);
            if (!Mine(cx, cz)) return;
            var (x0, x1, z0, z1) = f.Bounds(u0, u1, v0, v1);
            if (InPlace(x0, x1, z0, z1)) return;
            // gen_osm.py ground_all, for a box that is not solid: from the lowest ground under it to the highest.
            int nu = Math.Max(1, Math.Min(24, (int)Math.Ceiling((u1 - u0) / 20.0))), nv = Math.Max(1, Math.Min(24, (int)Math.Ceiling((v1 - v0) / 20.0)));
            double lo = double.MaxValue, hi = double.MinValue;
            void Sample(double u, double v) { var (e, n) = f.W(u, v); double g = ground.At(e, n); lo = Math.Min(lo, g); hi = Math.Max(hi, g); }
            for (int i = 0; i <= nu; i++) for (int k = 0; k <= nv; k++) Sample(u0 + (u1 - u0) * i / nu, v0 + (v1 - v0) * k / nv);
            Sample((u0 + u1) / 2, (v0 + v1) / 2);
            var size = Size("named_place");
            double y0 = lo, y1 = hi + ZoneHeight;
            entities.Add(new EntityData
            {
                PrefabId = "named_place",
                Position = new Vector3(R4(cx - key.Easting), R4((y0 + y1) / 2), R4(cz - key.Northing)),
                Rotation = Yaw(-NormHalf(a)),
                Scale = new Vector3(R4((u1 - u0) / size.X), R4((y1 - y0) / size.Y), R4((v1 - v0) / size.Z)),
                Name = name,
                Layer = "zones",
            });
        }

        // The buildings near the tile: those whose middle is in it stored whole, every one near graded under.
        var boxes = new List<(WorldBuildings.Frame F, double U0, double U1, double V0, double V1)>();
        var (built, sources) = footprints is { Count: > 0 }
            ? LayBuildings(key, footprints, ways, ground, west, south, InPlace, entities, slabs, boxes, Place)
            : (0, null);

        int trees = classes == null ? 0 : Woods(key, classes, ground, strips, boxes, entities);

        // The tile's ground: the window graded to every road and floor reaching it, then the tile cut out of it.
        var graded = TerrainBuilder.Grade(raw, windowPosts, windowPosts, 0f, 0f, WorldTileService.Spacing, slabs);
        int posts = WorldTileService.Posts, o = (int)Math.Round(margin / WorldTileService.Spacing);
        var heights = new float[posts * posts];
        for (int j = 0; j < posts; j++)
            Array.Copy(graded, (o + j) * windowPosts + o, heights, j * posts, posts);
        return new Laid(entities, heights, roads, pieces, trees, built, sources);
    }

    // ═══ The woods (gen_osm.py "The woods", a tile at a time) ══════════════════════════════════════════

    /// <summary>The woods' 10 m cells, a tile's side of them, and how high the canopy is.</summary>
    public const int WoodCell = 10, WoodCells = 25;
    public const double CanopyLow = 5.0, CanopyHigh = 18.0;

    /// <summary>One trunk on average in a square this wide, metres (gen_osm.py's medium detail).</summary>
    public const double TrunkSpacing = 40.0;

    /// <summary>One crown, the wind in the trees, for a block of woods this many cells a side.</summary>
    public const int CrownCells = 8;

    /// <summary>
    /// The woods of a tile, from its own land cover: where WorldCover has tree cover every 10 m and no road is
    /// within 2 m, the canopy as volumes of foliage (rectangles of 10 m cells, at least 800 m², at most 300 m a
    /// side; the scatterer that takes the top off sound through a wood), a trunk you can walk into one in every
    /// 40 m square on average (where it is, and whether there is one, seeded from the cell's place on the
    /// world's grid), and the wind in the trees from one crown in every 80 m block of woods. Everything is within
    /// the tile, from the tile's own cells, so no neighbour can disagree. How many things were made.
    /// </summary>
    private int Woods(WorldTileKey key, byte[] classes, Ground ground, List<(double Ax, double Az, double Bx, double Bz, double Half)> strips,
                      List<(WorldBuildings.Frame F, double U0, double U1, double V0, double V1)> boxes, List<EntityData> into)
    {
        int n = WorldTileService.Posts - 1, per = WoodCell / (int)WorldTileService.Spacing;
        var wooded = new bool[WoodCells, WoodCells];
        var free = new bool[WoodCells, WoodCells];
        bool Clear(double e, double nn, double pad)
        {
            foreach (var (ax, az, bx, bz, half) in strips)
            {
                if (Math.Min(ax, bx) - half - pad > e || Math.Max(ax, bx) + half + pad < e
                    || Math.Min(az, bz) - half - pad > nn || Math.Max(az, bz) + half + pad < nn) continue;
                if (SegPoint((e, nn), (ax, az), (bx, bz)).D < half + pad) return false;
            }
            // gen_osm.py is_clear's boxes: nothing in a building, or within the pad of one.
            foreach (var (f, u0, u1, v0, v1) in boxes)
            {
                var (u, v) = f.L(e, nn);
                if (u0 - pad < u && u < u1 + pad && v0 - pad < v && v < v1 + pad) return false;
            }
            return true;
        }
        int any = 0;
        for (int j = 0; j < WoodCells; j++)
            for (int i = 0; i < WoodCells; i++)
            {
                // The 2 m cell whose middle is the 10 m cell's middle: WorldCover's pixel there.
                byte c = classes[(j * per + per / 2) * n + i * per + per / 2];
                wooded[i, j] = c is 10 or 95;
                free[i, j] = wooded[i, j] && Clear(key.Easting + (i + 0.5) * WoodCell, key.Northing + (j + 0.5) * WoodCell, 2.0);
                if (wooded[i, j]) any++;
            }
        if (any == 0) return 0;
        int made = 0;

        // Canopy.
        var mask = (bool[,])free.Clone();
        while (true)
        {
            var (area, i0, i1, j0, j1) = LargestRect(mask, WoodCells, WoodCells);
            if (area < 8) break;
            i1 = Math.Min(i1, i0 + 30); j1 = Math.Min(j1, j0 + 30);
            double e0 = key.Easting + i0 * WoodCell, e1 = key.Easting + i1 * WoodCell;
            double n0 = key.Northing + j0 * WoodCell, n1 = key.Northing + j1 * WoodCell;
            var (lo, hi) = LowHigh(ground, e0, e1, n0, n1);
            double y0 = lo + CanopyLow, y1 = hi + CanopyHigh;
            into.Add(Thing("foliage_hedge", (e0 + e1) / 2 - key.Easting, (y0 + y1) / 2, (n0 + n1) / 2 - key.Northing,
                           new Vector3((float)(e1 - e0), (float)(y1 - y0), (float)(n1 - n0)), "Woods", "trees"));
            made++;
            for (int j = j0; j < j1; j++) for (int i = i0; i < i1; i++) mask[i, j] = false;
        }

        // Trunks.
        double chance = WoodCell * WoodCell / (TrunkSpacing * TrunkSpacing);
        long gx0 = (long)Math.Round(key.Easting / WoodCell), gz0 = (long)Math.Round(key.Northing / WoodCell);
        for (int j = 0; j < WoodCells; j++)
            for (int i = 0; i < WoodCells; i++)
            {
                if (!wooded[i, j]) continue;
                long gx = gx0 + i, gz = gz0 + j;
                if (Seeded("trunk", key, gx, gz) >= chance) continue;
                double e = (gx + Seeded("tx", key, gx, gz)) * WoodCell, nn = (gz + Seeded("tz", key, gx, gz)) * WoodCell;
                if (!Clear(e, nn, 1.5)) continue;
                const double d = 0.5;
                var (lo, _) = LowHigh(ground, e - d / 2, e + d / 2, nn - d / 2, nn + d / 2);
                double h = CanopyLow + 1.0;
                into.Add(Thing("wood_floor", e - key.Easting, lo + h / 2, nn - key.Northing, new Vector3((float)d, (float)h, (float)d), "Tree", "trees"));
                made++;
            }

        // The wind in the trees.
        for (int bj = 0; bj + CrownCells <= WoodCells; bj += CrownCells)
            for (int bi = 0; bi + CrownCells <= WoodCells; bi += CrownCells)
            {
                var cells = new List<(int I, int J)>();
                for (int j = bj; j < bj + CrownCells; j++) for (int i = bi; i < bi + CrownCells; i++) if (free[i, j]) cells.Add((i, j));
                if (cells.Count < CrownCells * CrownCells / 2) continue;
                var (ci, cj) = cells[(int)(Seeded("crown", key, gx0 + bi, gz0 + bj) * cells.Count)];
                double e = key.Easting + (ci + 0.5) * WoodCell, nn = key.Northing + (cj + 0.5) * WoodCell;
                into.Add(new EntityData
                {
                    PrefabId = "tree_crown",
                    Position = new Vector3(R4(e - key.Easting), R4(ground.At(e, nn) + (CanopyLow + CanopyHigh) / 2), R4(nn - key.Northing)),
                    Name = "Trees", Layer = "trees",
                });
                made++;
            }
        return made;

        EntityData Thing(string prefab, double x, double y, double z, Vector3 extent, string name, string layer)
        {
            var size = Size(prefab);
            return new EntityData
            {
                PrefabId = prefab, Position = new Vector3(R4(x), R4(y), R4(z)),
                Scale = new Vector3(R4(extent.X / size.X), R4(extent.Y / size.Y), R4(extent.Z / size.Z)), Name = name, Layer = layer,
            };
        }
    }

    /// <summary>gen_osm.py _samples: the lowest and highest ground under a box, at its corners, its middle, and a
    /// grid at most 20 m apart.</summary>
    private static (double Lo, double Hi) LowHigh(Ground ground, double e0, double e1, double n0, double n1)
    {
        int nu = Math.Max(1, Math.Min(24, (int)Math.Ceiling((e1 - e0) / 20.0))), nv = Math.Max(1, Math.Min(24, (int)Math.Ceiling((n1 - n0) / 20.0)));
        double lo = double.MaxValue, hi = double.MinValue;
        void Sample(double e, double n) { double g = ground.At(e, n); lo = Math.Min(lo, g); hi = Math.Max(hi, g); }
        for (int i = 0; i <= nu; i++) for (int k = 0; k <= nv; k++) Sample(e0 + (e1 - e0) * i / nu, n0 + (n1 - n0) * k / nv);
        Sample((e0 + e1) / 2, (n0 + n1) / 2);
        return (lo, hi);
    }

    /// <summary>A number in [0, 1) from what a thing is and where on the world's grid (gen_osm.py h01's job):
    /// the same tree is the same tree every time, whichever tile is made first.</summary>
    public static double Seeded(string what, WorldTileKey key, long gx, long gz)
    {
        ulong h = 1469598103934665603UL;
        foreach (char c in what) { h ^= c; h *= 1099511628211UL; }
        foreach (long v in new[] { key.Zone * (key.North ? 1L : -1L), gx, gz })
        {
            h ^= (ulong)v; h *= 1099511628211UL;
            h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 32;
        }
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>gen_osm.py largest_rect: the largest all-true rectangle, (area, i0, i1, j0, j1).</summary>
    private static (int Area, int I0, int I1, int J0, int J1) LargestRect(bool[,] mask, int nu, int nv)
    {
        var heights = new int[nu];
        var best = (0, 0, 0, 0, 0);
        for (int j = 0; j < nv; j++)
        {
            for (int i = 0; i < nu; i++) heights[i] = mask[i, j] ? heights[i] + 1 : 0;
            var stack = new Stack<(int Start, int H)>();
            for (int i = 0; i <= nu; i++)
            {
                int hgt = i < nu ? heights[i] : 0, start = i;
                while (stack.Count > 0 && stack.Peek().H >= hgt)
                {
                    var (si, sh) = stack.Pop();
                    int area = sh * (i - si);
                    if (area > best.Item1) best = (area, si, i, j - sh + 1, j + 1);
                    start = si;
                }
                stack.Push((start, hgt));
            }
        }
        return best;
    }

    private Vector3 Size(string prefab) => _sizes.TryGetValue(prefab, out var s) && s.X > 0 && s.Y > 0 && s.Z > 0 ? s : Vector3.One;

    private static float R4(double v) => (float)Math.Round(v, 4);

    // ═══ gen_osm.py lay_line and seg_box ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Boxes along a line, each lengthened at a bend by as much as the bend opens on its outside, each pitched
    /// along its run to the heights <paramref name="height"/> gives its ends and level across; a straight run in
    /// pieces of at most <see cref="SegMax"/>, overlapping by <see cref="Seam"/> at the joins.
    /// </summary>
    private static void LayLine(List<(double E, double N)> line, double width, string prefab, float y1, string name, string layer,
                                Func<double, double, double> height, Action<Piece> lay, double tol = 0.25)
    {
        var pts = Simplify(line, tol);
        var hts = pts.Select(p => height(p.E, p.N)).ToList();
        var dirs = new double[pts.Count - 1];
        for (int i = 0; i < dirs.Length; i++) dirs[i] = Math.Atan2(pts[i + 1].N - pts[i].N, pts[i + 1].E - pts[i].E);
        double Ext(int k)
        {
            if (k < 0 || k >= dirs.Length - 1) return 0;
            double turn = Math.Abs(PyMod(dirs[k + 1] - dirs[k] + Math.PI, 2 * Math.PI) - Math.PI);
            return Math.Min(width, width / 2 * Math.Tan(Math.Min(turn, 2.6) / 2) + 0.05);
        }
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double e0 = Ext(i - 1), e1 = Ext(i);
            var (ax, az) = pts[i];
            var (bx, bz) = pts[i + 1];
            int n = Math.Max(1, (int)Math.Ceiling(Dist(pts[i], pts[i + 1]) / SegMax));
            for (int k = 0; k < n; k++)
            {
                double px = k == 0 ? ax : ax + (bx - ax) * k / n, pz = k == 0 ? az : az + (bz - az) * k / n;
                double qx = k == n - 1 ? bx : ax + (bx - ax) * (k + 1) / n, qz = k == n - 1 ? bz : az + (bz - az) * (k + 1) / n;
                double hp = k == 0 ? hts[i] : height(px, pz), hq = k == n - 1 ? hts[i + 1] : height(qx, qz);
                double f0 = k == 0 ? e0 : Seam, f1 = k == n - 1 ? e1 : Seam;
                if (Math.Sqrt((qx - px) * (qx - px) + (qz - pz) * (qz - pz)) < 0.05) continue;
                lay(new Piece(prefab, px, pz, qx, qz, width, 0f, y1, f0, f1, hp, hq, name, layer));
            }
        }
    }

    /// <summary>A piece as the box it is: its middle (zone metres, y over the sea), its turn, and its extent.</summary>
    private readonly record struct Box(double Cx, float Cy, double Cz, Quaternion Turn, Vector3 Extent, (double, double, double, double) Bounds);

    /// <summary>gen_osm.py seg_box, then ground_all's pitch for a piece of a line: along its run from the height
    /// at its start to the height at its end, level across.</summary>
    private static Box PieceBox(Piece p)
    {
        double L = Math.Sqrt((p.Bx - p.Ax) * (p.Bx - p.Ax) + (p.Bz - p.Az) * (p.Bz - p.Az));
        var f = new Frame(p.Ax, p.Az, Math.Atan2(p.Bz - p.Az, p.Bx - p.Ax));
        double u0 = -p.Ext0, u1 = L + p.Ext1, v0 = -p.Width / 2, v1 = p.Width / 2;
        var (cx, cz) = f.W((u0 + u1) / 2, (v0 + v1) / 2);
        double g = (p.Hq - p.Hp) / L, uc = (u0 + u1) / 2;
        double cy = p.Hp + g * uc + (p.Y0 + p.Y1) / 2.0;
        return new Box(cx, (float)Math.Round(cy, 4), cz, Turn(f.A, g, 0), new Vector3((float)(u1 - u0), p.Y1 - p.Y0, (float)(v1 - v0)),
                       f.Bounds(u0, u1, v0, v1));
    }

    /// <summary>A way's nodes in the zone's metres, no point twice.</summary>
    private static List<(double E, double N)> PointsOf(OsmWay w, WorldTileKey key)
    {
        var pts = new List<(double E, double N)>(w.Nodes.Length);
        for (int k = 0; k < w.Nodes.Length; k++)
        {
            var (pe, pn) = Utm.FromLatLon(w.Lat[k], w.Lon[k], key.Zone, key.North);
            if (pts.Count > 0 && Math.Abs(pe - pts[^1].E) + Math.Abs(pn - pts[^1].N) < 1e-6) continue;
            pts.Add((pe, pn));
        }
        return pts;
    }

    // ═══ Junctions (gen_osm.py make_junctions) ═════════════════════════════════════════════════════════

    /// <summary>A road way in the zone's metres: its line without repeated points, and every node where it is.</summary>
    private sealed record Line(OsmWay Way, List<(double E, double N)> Pts, (double E, double N)[] Nodes);

    private sealed record Junction(double E, double N, string Name, double Radius, int Ways);

    /// <summary>
    /// Where roads meet: a node two or more road ways share, except where two ways of one road (the same name and
    /// class) only run on from one to the other, and except a hairpin where two ways both end heading the same way.
    /// Two within 4 m are one (the one more roads meet at, or the first from the south-west). Decided from every way
    /// near the tile, so a junction by the edge is the same junction from either side.
    /// </summary>
    private static List<Junction> Junctions(List<Line> lines)
    {
        var at = new Dictionary<long, List<(int Line, int Index)>>();
        for (int li = 0; li < lines.Count; li++)
        {
            var w = lines[li].Way;
            var seen = new HashSet<long>();
            for (int k = 0; k < w.Nodes.Length; k++)
            {
                if (!seen.Add(w.Nodes[k])) continue;
                if (!at.TryGetValue(w.Nodes[k], out var list)) at[w.Nodes[k]] = list = new List<(int, int)>();
                list.Add((li, k));
            }
        }
        var found = new List<(double E, double N, long Node, Junction J)>();
        foreach (var (node, list) in at)
        {
            if (list.Count < 2) continue;
            var ways = list.Select(l => lines[l.Line].Way).ToList();
            bool End(OsmWay w, int k) => k == 0 || k == w.Nodes.Length - 1;
            if (list.Count == 2 && End(ways[0], list[0].Index) && End(ways[1], list[1].Index))
            {
                // One road running on, or a hairpin.
                string Key(OsmWay w) => w.Tag("name") is { Length: > 0 } n ? n + "|" + RoadClass[w.Tag("highway")].Type : "#" + w.Id;
                if (Key(ways[0]) == Key(ways[1])) continue;
                var h0 = Heading(lines[list[0].Line], list[0].Index);
                var h1 = Heading(lines[list[1].Line], list[1].Index);
                if (h0.E * h1.E + h0.N * h1.N > 0.7) continue;
            }
            var (e, n) = lines[list[0].Line].Nodes[list[0].Index];
            var names = ways.Select(NameOf).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            double r = ways.Max(WidthOf) / 2 + 1.0;
            found.Add((e, n, node, new Junction(e, n, string.Join(" and ", names), r, ways.Count)));
        }
        // Twins: in order from the south-west, the one more roads meet at kept.
        var keep = new List<Junction>();
        foreach (var (e, n, _, j) in found.OrderBy(f => Math.Round(f.E, 3)).ThenBy(f => Math.Round(f.N, 3)).ThenBy(f => f.Node))
        {
            int twin = keep.FindIndex(k => Math.Sqrt((k.E - e) * (k.E - e) + (k.N - n) * (k.N - n)) < 4.0);
            if (twin < 0) keep.Add(j);
            else if (j.Ways > keep[twin].Ways) keep[twin] = j;
        }
        return keep;

        // The way's direction away from the node at one of its ends.
        static (double E, double N) Heading(Line line, int k)
        {
            var a = line.Nodes[k];
            var b = line.Nodes[k == 0 ? 1 : k - 1];
            double L = Math.Max(1e-9, Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N)));
            return ((b.E - a.E) / L, (b.N - a.N) / L);
        }
    }

    // ═══ Geometry in the ground plane (gen_osm.py) ════════════════════════════════════════════════════

    /// <summary>gen_osm.py Frame: u along <see cref="A"/> (radians from +x toward +z), v a quarter turn on.</summary>
    private readonly record struct Frame(double Ox, double Oz, double A)
    {
        public (double E, double N) W(double u, double v)
            => (Ox + u * Math.Cos(A) - v * Math.Sin(A), Oz + u * Math.Sin(A) + v * Math.Cos(A));

        public (double X0, double X1, double Z0, double Z1) Bounds(double u0, double u1, double v0, double v1)
        {
            var c = new[] { W(u0, v0), W(u1, v0), W(u0, v1), W(u1, v1) };
            return (c.Min(p => p.E), c.Max(p => p.E), c.Min(p => p.N), c.Max(p => p.N));
        }
    }

    private static double Dist((double E, double N) a, (double E, double N) b)
        => Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));

    /// <summary>Python's modulo: the sign of the divisor.</summary>
    private static double PyMod(double a, double b) => a - b * Math.Floor(a / b);

    /// <summary>gen_osm.py norm_half: an angle folded into (-pi/2, pi/2].</summary>
    public static double NormHalf(double a)
    {
        while (a > Math.PI / 2 + 1e-12) a -= Math.PI;
        while (a <= -Math.PI / 2 + 1e-12) a += Math.PI;
        return a;
    }

    /// <summary>mapgen.py yaw: a turn about the vertical, rounded as the maps write it.</summary>
    public static Quaternion Yaw(double r)
        => Math.Abs(r) < 1e-9 ? Quaternion.Identity : new Quaternion(0f, (float)Math.Round(Math.Sin(r / 2), 6), 0f, (float)Math.Round(Math.Cos(r / 2), 6));

    /// <summary>gen_osm.py _turn: the frame's turn about the vertical, then the box tilted so its +X rises
    /// <paramref name="pitchU"/> a metre and its +Z <paramref name="pitchV"/>.</summary>
    public static Quaternion Turn(double a, double pitchU, double pitchV)
    {
        double n = NormHalf(a);
        if (Math.Abs(Math.IEEERemainder(n - a, 2 * Math.PI)) > 1.0) { pitchU = -pitchU; pitchV = -pitchV; }
        (double X, double Y, double Z, double W) q = Math.Abs(n) > 1e-9 ? (0, Math.Sin(-n / 2), 0, Math.Cos(-n / 2)) : (0, 0, 0, 1);
        double al = Math.Atan(pitchU), be = -Math.Atan(pitchV);
        q = Mul(q, (0, 0, Math.Sin(al / 2), Math.Cos(al / 2)));
        q = Mul(q, (Math.Sin(be / 2), 0, 0, Math.Cos(be / 2)));
        return new Quaternion((float)Math.Round(q.X, 6), (float)Math.Round(q.Y, 6), (float)Math.Round(q.Z, 6), (float)Math.Round(q.W, 6));

        static (double, double, double, double) Mul((double X, double Y, double Z, double W) a, (double X, double Y, double Z, double W) b)
            => (a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y, a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W, a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    }

    /// <summary>gen_osm.py simplify: Douglas-Peucker, keeping the ends.</summary>
    public static List<(double E, double N)> Simplify(List<(double E, double N)> pts, double tol)
    {
        if (pts.Count < 3) return new List<(double, double)>(pts);
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double best = -1; int bi = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = SegPoint(pts[i], pts[a], pts[b]).D;
                if (d > best) { best = d; bi = i; }
            }
            if (best > tol) { keep[bi] = true; stack.Push((a, bi)); stack.Push((bi, b)); }
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    private static (double D, double T) SegPoint((double E, double N) p, (double E, double N) a, (double E, double N) b)
    {
        double dx = b.E - a.E, dz = b.N - a.N, l2 = dx * dx + dz * dz;
        double t = l2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.E - a.E) * dx + (p.N - a.N) * dz) / l2));
        double qx = a.E + t * dx, qz = a.N + t * dz;
        return (Math.Sqrt((p.E - qx) * (p.E - qx) + (p.N - qz) * (p.N - qz)), t);
    }

    /// <summary>gen_osm.py project: the nearest point of a line.</summary>
    private static (double D, (double E, double N) Foot) Project(List<(double E, double N)> pts, double e, double n)
    {
        double best = double.MaxValue;
        (double, double) foot = pts[0];
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (Dist(pts[i], pts[i + 1]) < 1e-9) continue;
            var (d, t) = SegPoint((e, n), pts[i], pts[i + 1]);
            if (d < best) { best = d; foot = (pts[i].E + t * (pts[i + 1].E - pts[i].E), pts[i].N + t * (pts[i + 1].N - pts[i].N)); }
        }
        return (best, foot);
    }

    /// <summary>gen_osm.py offset_line: a line moved sideways, positive to its left.</summary>
    private static List<(double E, double N)> OffsetLine(List<(double E, double N)> pts, double left)
    {
        var o = new List<(double, double)>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[Math.Max(0, i - 1)];
            var b = pts[Math.Min(pts.Count - 1, i + 1)];
            double dx = b.E - a.E, dz = b.N - a.N, l = Math.Sqrt(dx * dx + dz * dz);
            if (l == 0) l = 1;
            o.Add((pts[i].E - dz / l * left, pts[i].N + dx / l * left));
        }
        return o;
    }
}
