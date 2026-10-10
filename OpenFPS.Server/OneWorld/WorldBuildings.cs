using System.Numerics;
using EntityData = OpenFPS.Server.Repositories.EntityData;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// A building's shell as tools/gen_osm.py builds one at medium detail (its build(), ported; keep the two the same):
/// the footprint covered by up to two rectangles in its own frame, walls round the outside of their union with the
/// front door cut out of the longest wall facing the street, a floor slab and a roof deck over each rectangle, a
/// ceiling in a home, one room in each rectangle, and the front door joining a room to the outdoors. A small shed, and
/// any garage, workshop, barn or outbuilding, is one solid box. Everything is set on the building's pad (the ground at
/// the middle of its wall nearest the road), or, for a solid box, with its foot at the lowest ground under it.
///
/// <para>Ported: the shape of a footprint (Building, min_rect, hull), cover, largest_rect, exterior_runs, wall_run,
/// run_point, run_phi, side_word, room_for_point, door, obox, and build() at detail 1. Not ported: high detail (the
/// rooms of a house, inner and back doors, ridged roofs, plaster inside the walls, outbuildings with rooms), and
/// gen_osm's lots, addresses and classify(), which the world does without (WorldFeatures.Buildings).</para>
/// </summary>
public static class WorldBuildings
{
    // gen_osm.py's constants.
    public const double StoreyH = 2.75, FloorTop = 0.12, Ceil = 2.68, WallT = 0.25, DoorLap = 0.05;
    public const double DoorTop = FloorTop + 2.13;
    public const double BrickShare = 0.55;

    /// <summary>What the port needs of a prefab: its collider's size, its material, and whether it is a door whose
    /// leaf swings (mapgen.py BASE and HINGED).</summary>
    public readonly record struct Prefab(Vector3 Size, string Material, bool Hinged);

    public static Dictionary<string, Prefab> PrefabsFrom(IReadOnlyDictionary<string, Repositories.PrefabTemplate> templates)
    {
        var d = new Dictionary<string, Prefab>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, t) in templates)
        {
            bool slides = t.Slides ?? t.DoorKind is "auto-slide" or "patio-slide" or "elevator";
            d[id] = new Prefab(t.ColliderSize ?? Vector3.One, t.Material, t.IsDoor == true && !slides);
        }
        return d;
    }

    // ═══ A footprint's shape (gen_osm.py Building) ═══════════════════════════════════════════════════════

    /// <summary>A footprint in the ground plane: its ring (closed), area, middle (the mean of its corners), the
    /// smallest rectangle round it (the angle of its long side, its long and short sides), how much of that
    /// rectangle it fills, and its bounds.</summary>
    public sealed class Shape
    {
        public List<(double X, double Z)> Ring { get; }
        public double Area, Cx, Cz, Angle, Long, Short, RectFill, X0, X1, Z0, Z1;

        public Shape(List<(double X, double Z)> ring)
        {
            Ring = ring;
            double a = 0;
            for (int i = 0; i < ring.Count - 1; i++) a += ring[i].X * ring[i + 1].Z - ring[i + 1].X * ring[i].Z;
            Area = Math.Abs(a) / 2;
            double sx = 0, sz = 0;
            for (int i = 0; i < ring.Count - 1; i++) { sx += ring[i].X; sz += ring[i].Z; }
            Cx = sx / (ring.Count - 1);
            Cz = sz / (ring.Count - 1);
            (Angle, _, _, Long, Short) = MinRect(ring.Take(ring.Count - 1).ToList());
            RectFill = Area / Math.Max(1e-6, Long * Short);
            X0 = ring.Min(p => p.X); X1 = ring.Max(p => p.X);
            Z0 = ring.Min(p => p.Z); Z1 = ring.Max(p => p.Z);
        }
    }

    /// <summary>gen_osm.py hull: Andrew's monotone chain over the points, each once, in order.</summary>
    public static List<(double X, double Z)> Hull(IEnumerable<(double X, double Z)> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Z).ToList();
        if (pts.Count < 3) return pts;
        static double Cross((double X, double Z) o, (double X, double Z) a, (double X, double Z) b)
            => (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
        var lo = new List<(double, double)>();
        foreach (var p in pts)
        {
            while (lo.Count >= 2 && Cross(lo[^2], lo[^1], p) <= 0) lo.RemoveAt(lo.Count - 1);
            lo.Add(p);
        }
        var hi = new List<(double, double)>();
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (hi.Count >= 2 && Cross(hi[^2], hi[^1], p) <= 0) hi.RemoveAt(hi.Count - 1);
            hi.Add(p);
        }
        lo.RemoveAt(lo.Count - 1);
        hi.RemoveAt(hi.Count - 1);
        lo.AddRange(hi);
        return lo;
    }

    /// <summary>gen_osm.py min_rect: the smallest rectangle round some points, (angle of its long side folded into a
    /// half turn, centre x, centre z, long, short).</summary>
    public static (double A, double Cx, double Cz, double Long, double Short) MinRect(List<(double X, double Z)> points)
    {
        var h = Hull(points);
        (double Area, double A, double U0, double U1, double V0, double V1)? best = null;
        for (int i = 0; i < h.Count; i++)
        {
            var (ax, az) = h[i];
            var (bx, bz) = h[(i + 1) % h.Count];
            double ang = Math.Atan2(bz - az, bx - ax), c = Math.Cos(ang), s = Math.Sin(ang);
            double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
            foreach (var (x, z) in h)
            {
                double u = x * c + z * s, v = -x * s + z * c;
                umin = Math.Min(umin, u); umax = Math.Max(umax, u); vmin = Math.Min(vmin, v); vmax = Math.Max(vmax, v);
            }
            double area = (umax - umin) * (vmax - vmin);
            if (best == null || area < best.Value.Area - 1e-9) best = (area, ang, umin, umax, vmin, vmax);
        }
        var (_, a, u0, u1, v0, v1) = best!.Value;
        if (v1 - v0 > u1 - u0) a += Math.PI / 2;
        a = WorldFeatures.NormHalf(a);
        double cc = Math.Cos(a), ss = Math.Sin(a);
        double us0 = double.MaxValue, us1 = double.MinValue, vs0 = double.MaxValue, vs1 = double.MinValue;
        foreach (var (x, z) in h)
        {
            double u = x * cc + z * ss, v = -x * ss + z * cc;
            us0 = Math.Min(us0, u); us1 = Math.Max(us1, u); vs0 = Math.Min(vs0, v); vs1 = Math.Max(vs1, v);
        }
        double cu = (us0 + us1) / 2, cv = (vs0 + vs1) / 2;
        return (a, cu * cc - cv * ss, cu * ss + cv * cc, us1 - us0, vs1 - vs0);
    }

    // ═══ Geometry in the ground plane ═══════════════════════════════════════════════════════════════════

    /// <summary>gen_osm.py Frame: u along <see cref="A"/> (radians from +x toward +z), v a quarter turn on.</summary>
    public readonly struct Frame
    {
        public readonly double Ox, Oz, A, C, S;
        public Frame(double ox, double oz, double a) { Ox = ox; Oz = oz; A = a; C = Math.Cos(a); S = Math.Sin(a); }
        public (double X, double Z) W(double u, double v) => (Ox + u * C - v * S, Oz + u * S + v * C);
        public (double U, double V) L(double x, double z) { double dx = x - Ox, dz = z - Oz; return (dx * C + dz * S, -dx * S + dz * C); }
    }

    /// <summary>gen_osm.py pip: whether a point is inside a ring (even-odd).</summary>
    public static bool Pip(double x, double z, IReadOnlyList<(double X, double Z)> ring)
    {
        bool inside = false;
        int n = ring.Count, j = n - 1;
        for (int i = 0; i < n; i++)
        {
            var (xi, zi) = ring[i];
            var (xj, zj) = ring[j];
            if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            j = i;
        }
        return inside;
    }

    /// <summary>gen_osm.py extents: a ring's bounds in a frame, (u0, u1, v0, v1).</summary>
    public static (double U0, double U1, double V0, double V1) Extents(Frame f, IReadOnlyList<(double X, double Z)> ring)
    {
        double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
        foreach (var (x, z) in ring)
        {
            var (u, v) = f.L(x, z);
            u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
        }
        return (u0, u1, v0, v1);
    }

    /// <summary>gen_osm.py largest_rect over mask[j][i]: (area, i0, i1, j0, j1).</summary>
    public static (int Area, int I0, int I1, int J0, int J1) LargestRect(bool[][] mask, int nu, int nv)
    {
        var heights = new int[nu];
        var best = (0, 0, 0, 0, 0);
        var stack = new Stack<(int Start, int H)>();
        for (int j = 0; j < nv; j++)
        {
            for (int i = 0; i < nu; i++) heights[i] = mask[j][i] ? heights[i] + 1 : 0;
            stack.Clear();
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

    /// <summary>The grid a footprint is cut into: its first corner, a cell's size, and the cells a side.</summary>
    public readonly record struct Grid(double U0, double V0, double Cu, double Cv, int Nu, int Nv);

    /// <summary>gen_osm.py cover: rectangles (i0, i1, j0, j1) of a grid in a frame at angle <paramref name="a"/>
    /// through the origin covering a polygon, the biggest first, until what is left is a sliver.</summary>
    public static (List<(int I0, int I1, int J0, int J1)> Rects, Grid Grid) Cover(IReadOnlyList<(double X, double Z)> ring, double a, double cell,
                                                                                  int maxRects, double minArea, double stopFrac = 0.06)
    {
        var f = new Frame(0, 0, a);
        var loc = ring.Select(p => f.L(p.X, p.Z)).Select(p => (X: p.U, Z: p.V)).ToList();
        double u0 = loc.Min(p => p.X), u1 = loc.Max(p => p.X), v0 = loc.Min(p => p.Z), v1 = loc.Max(p => p.Z);
        int nu = Math.Max(1, (int)Math.Round((u1 - u0) / cell, MidpointRounding.ToEven));
        int nv = Math.Max(1, (int)Math.Round((v1 - v0) / cell, MidpointRounding.ToEven));
        double cu = (u1 - u0) / nu, cv = (v1 - v0) / nv;
        var mask = new bool[nv][];
        int total = 0;
        for (int j = 0; j < nv; j++)
        {
            mask[j] = new bool[nu];
            for (int i = 0; i < nu; i++)
                if (mask[j][i] = Pip(u0 + (i + 0.5) * cu, v0 + (j + 0.5) * cv, loc)) total++;
        }
        var grid = new Grid(u0, v0, cu, cv, nu, nv);
        var rects = new List<(int, int, int, int)>();
        if (total == 0) return (rects, grid);
        int left = total;
        while (rects.Count < maxRects)
        {
            var (area, i0, i1, j0, j1) = LargestRect(mask, nu, nv);
            if (area == 0 || area * cu * cv < minArea) break;
            rects.Add((i0, i1, j0, j1));
            for (int j = j0; j < j1; j++) for (int i = i0; i < i1; i++) mask[j][i] = false;
            left -= area;
            if (left < stopFrac * total) break;
        }
        return (rects, grid);
    }

    /// <summary>A wall run: along v = Line (Axis 'v') or u = Line (Axis 'u'), from S0 to S1, its outside toward +1 or -1.</summary>
    public readonly record struct Run(char Axis, double Line, double S0, double S1, int Out);

    /// <summary>gen_osm.py exterior_runs: the outside of a union of grid rectangles, as wall runs.</summary>
    public static List<Run> ExteriorRuns(List<(int I0, int I1, int J0, int J1)> rects, Grid g)
    {
        var mask = new bool[g.Nv, g.Nu];
        foreach (var (i0, i1, j0, j1) in rects)
            for (int j = j0; j < j1; j++) for (int i = i0; i < i1; i++) mask[j, i] = true;
        bool M(int i, int j) => i >= 0 && i < g.Nu && j >= 0 && j < g.Nv && mask[j, i];
        var runs = new List<Run>();
        for (int j = 0; j <= g.Nv; j++)
        {
            int i = 0;
            while (i < g.Nu)
            {
                bool a = M(i, j - 1), b = M(i, j);
                if (a == b) { i++; continue; }
                int o = a ? 1 : -1, k = i;
                while (k < g.Nu && M(k, j - 1) == a && M(k, j) == b) k++;
                runs.Add(new Run('v', g.V0 + j * g.Cv, g.U0 + i * g.Cu, g.U0 + k * g.Cu, o));
                i = k;
            }
        }
        for (int i = 0; i <= g.Nu; i++)
        {
            int j = 0;
            while (j < g.Nv)
            {
                bool a = M(i - 1, j), b = M(i, j);
                if (a == b) { j++; continue; }
                int o = a ? 1 : -1, k = j;
                while (k < g.Nv && M(i - 1, k) == a && M(i, k) == b) k++;
                runs.Add(new Run('u', g.U0 + i * g.Cu, g.V0 + j * g.Cv, g.V0 + k * g.Cv, o));
                j = k;
            }
        }
        return runs;
    }

    /// <summary>gen_osm.py run_point: a point on a run's line, <paramref name="depth"/> outward of it.</summary>
    public static (double X, double Z) RunPoint(Frame f, Run r, double s, double depth = 0)
        => r.Axis == 'v' ? f.W(s, r.Line + r.Out * depth) : f.W(r.Line + r.Out * depth, s);

    public static double RunPhi(Frame f, Run r) => r.Axis == 'v' ? f.A : f.A + Math.PI / 2;

    /// <summary>gen_osm.py side_word: which way a run's outside faces, as a compass word.</summary>
    public static string SideWord(Frame f, Run r)
    {
        var (nx, nz) = r.Axis == 'v' ? f.W(0, r.Out) : f.W(r.Out, 0);
        nx -= f.Ox; nz -= f.Oz;
        double ang = PyMod(Math.Atan2(nz, nx) * 180.0 / Math.PI, 360);
        string[] words = { "east", "northeast", "north", "northwest", "west", "southwest", "south", "southeast" };
        return words[(int)Math.Floor(PyMod(ang + 22.5, 360) / 45)];
    }

    private static double PyMod(double a, double b) => a - b * Math.Floor(a / b);

    // ═══ A building ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What a building is to be (gen_osm.py's decisions before build()): what kind (house, mobile_home,
    /// premises, church, building, garage, workshop, barn, outbuilding, shed), its name, the label its walls and floors
    /// are seeded from, the place it is (premises: its category), where the street is, and its pad (null for a solid
    /// box, which is set into the ground instead).</summary>
    public sealed class Plan
    {
        public required Shape Shape { get; init; }
        public required string Kind { get; init; }
        public required string Name { get; init; }
        public required string Seed { get; init; }
        public string? PlaceKind { get; init; }
        public double? Height { get; init; }
        public (double X, double Z) Street { get; init; }
        public double? Pad { get; init; }
        /// <summary>What the room is called after the name (gen_osm: ", house" for a home).</summary>
        public string? RoomSuffix { get; init; }
    }

    /// <summary>Whether a kind is one solid box at medium detail (gen_osm.py build(): a shed always, an outbuilding
    /// below high detail).</summary>
    public static bool IsSolidBox(string kind) => kind is "shed" or "garage" or "workshop" or "barn" or "outbuilding";

    /// <summary>
    /// gen_osm.py build() at medium detail: the entities of one building, positions (x and z) in the plan's frame
    /// moved by <paramref name="shift"/> and rounded as the maps write them, heights over the pad (or the ground under
    /// a solid box). Each gets the next id from <paramref name="nextId"/>; a door's room is the id of the room it
    /// opens from. With <paramref name="measured"/>, each room carries what it is made of (floor, ceiling, walls) and
    /// that it is indoors, as a map's load measures it, so a world tile needs no survey.
    /// </summary>
    public static List<EntityData> Build(Plan p, IReadOnlyDictionary<string, Prefab> prefabs, Func<double, double, double> ground,
                                         (double X, double Z) shift, ref int nextId, bool measured = false)
    {
        var bd = p.Shape;
        var outList = new List<EntityData>();
        string kind = p.Kind, name = p.Name, label = p.Seed;
        double lift = p.Pad ?? 0;
        int id = nextId;

        Vector3 Base(string prefab) => prefabs.TryGetValue(prefab, out var f) && f.Size.X > 0 && f.Size.Y > 0 && f.Size.Z > 0 ? f.Size : Vector3.One;

        EntityData Obox(string prefab, Frame f, double u0, double u1, double v0, double v1, double y0, double y1, string? nm, string layer, double? foot = null)
        {
            var b = Base(prefab);
            var (cx, cz) = f.W((u0 + u1) / 2, (v0 + v1) / 2);
            double a = WorldFeatures.NormHalf(f.A);
            var e = new EntityData
            {
                EntityId = ++id,
                PrefabId = prefab,
                Position = new Vector3(R4(cx + shift.X), R4(R4((y0 + y1) / 2) + (foot ?? lift)), R4(cz + shift.Z)),
                Rotation = Math.Abs(a) > 1e-9 ? WorldFeatures.Yaw(-a) : Quaternion.Identity,
                Scale = new Vector3(R4((u1 - u0) / b.X), R4((y1 - y0) / b.Y), R4((v1 - v0) / b.Z)),
                Name = nm,
                Layer = layer,
            };
            outList.Add(e);
            return e;
        }

        // Small sheds, and every outbuilding below high detail, are one solid box: you walk into the shed, you hear "shed".
        if (IsSolidBox(kind))
        {
            var f = new Frame(bd.Cx, bd.Cz, bd.Angle);
            var e = Extents(f, bd.Ring);
            double hgt = Math.Max(2.2, Math.Min(p.Height ?? 2.5, 6.0));
            // gen_osm.py ground_all, anything else solid: its foot at the lowest ground under it.
            double lo = Samples(f, e.U0, e.U1, e.V0, e.V1, ground).Min();
            Obox(bd.Area < 30 ? "siding_wall" : "metal_wall", f, e.U0, e.U1, e.V0, e.V1, 0.0, hgt, name, "structure", lo);
            nextId = id;
            return outList;
        }

        // The footprint, in rectangles.
        double cell = bd.Area < 1500 ? 0.5 : 1.0;
        List<(int I0, int I1, int J0, int J1)> rects;
        Grid grid;
        if (bd.RectFill >= 0.9)
        {
            var e = Extents(new Frame(0, 0, bd.Angle), bd.Ring);
            rects = new() { (0, 1, 0, 1) };
            grid = new Grid(e.U0, e.V0, e.U1 - e.U0, e.V1 - e.V0, 1, 1);
        }
        else
        {
            (rects, grid) = Cover(bd.Ring, bd.Angle, cell, 2, Math.Max(9.0, 0.08 * bd.Area));
            if (rects.Count == 0) return outList;
        }
        var F = new Frame(0, 0, bd.Angle);
        var R = rects.Select(r => (U0: grid.U0 + r.I0 * grid.Cu, U1: grid.U0 + r.I1 * grid.Cu, V0: grid.V0 + r.J0 * grid.Cv, V1: grid.V0 + r.J1 * grid.Cv)).ToList();
        var runs = ExteriorRuns(rects, grid);

        bool two = (p.Height ?? 0) >= 6.8 && bd.Area >= 110 && kind is "house" or "premises" or "church";
        double wallH = kind is "house" or "mobile_home" ? StoreyH * (two ? 2 : 1) + (two ? 0.1 : 0.0) : Math.Max(3.0, Math.Min((p.Height ?? 4.5) * 0.85, 9.0));
        string mat = kind switch
        {
            "house" or "building" => H01(label, "walls") < BrickShare ? "brick" : "siding",
            "mobile_home" => "siding",
            "church" => "brick",
            "premises" => bd.Area > 250 ? "metal" : "brick",
            _ => "metal",
        };
        string wallPrefab = mat switch { "brick" => "brick_wall", "siding" => "siding_wall", "metal" => "metal_wall", _ => "concrete_wall" };
        string roofPrefab = mat == "metal" ? "metal_wall" : "shingle_roof";

        var toStreet = (X: p.Street.X - bd.Cx, Z: p.Street.Z - bd.Cz);
        double Facing(Run r)
        {
            var n = r.Axis == 'v' ? F.W(0, r.Out) : F.W(r.Out, 0);
            double l = Math.Sqrt(toStreet.X * toStreet.X + toStreet.Z * toStreet.Z);
            if (l == 0) l = 1.0;
            return (n.X * toStreet.X + n.Z * toStreet.Z) / l;
        }

        // Slab, roof.
        bool homes = kind is "house" or "mobile_home";
        string floorPrefab = "concrete_floor";
        if (homes) floorPrefab = H01(label, "floor") < 0.5 ? "wood_floor" : "carpet_floor";
        foreach (var (u0, u1, v0, v1) in R)
            Obox(floorPrefab, F, u0, u1, v0, v1, -0.15, floorPrefab != "concrete_floor" ? FloorTop : 0.10, $"{name} floor", "structure");
        foreach (var (u0, u1, v0, v1) in R)
            Obox(roofPrefab, F, u0, u1, v0, v1, wallH, wallH + (roofPrefab == "shingle_roof" ? 0.2 : 0.06), $"{name} roof", "structure");
        if (homes)
            foreach (var (u0, u1, v0, v1) in R)
                Obox("plaster_wall", F, u0 + WallT, u1 - WallT, v0 + WallT, v1 - WallT, Ceil, Ceil + 0.06, $"{name} ceiling", "structure");

        // One room per rectangle, all called the same; the gaps between them are openings.
        string suffix = p.RoomSuffix ?? "";
        double top = homes ? Ceil : wallH - 0.05;
        string[]? roomMaterials = measured
            ? new[] { Material(floorPrefab), Material(homes ? "plaster_wall" : roofPrefab), Material(wallPrefab), Material(wallPrefab), Material(wallPrefab), Material(wallPrefab) }
            : null;
        var ids = new List<(int Id, (double U0, double U1, double V0, double V1) Rect)>();
        foreach (var r in R)
        {
            var room = Obox("acoustic_region", F, r.U0 + WallT, r.U1 - WallT, r.V0 + WallT, r.V1 - WallT, FloorTop - 0.02, top, $"{name}{suffix}", "rooms");
            if (measured) { room.RoomMaterials = roomMaterials; room.IsIndoor = true; }
            ids.Add((room.EntityId, r));
        }

        // The front door: the longest stretch of wall facing the street, in its middle.
        int k = Enumerable.Range(0, runs.Count)
                          .OrderBy(i => (Facing(runs[i]) > 0.5 ? -1.0 : 0.0) * (runs[i].S1 - runs[i].S0))
                          .ThenBy(i => -Facing(runs[i])).First();
        var run = runs[k];
        double s = (run.S0 + run.S1) / 2;
        double width;
        string? doorPrefab;
        string doorName;
        if (kind == "barn") { width = Math.Min(3.0, run.S1 - run.S0 - 1.0); doorPrefab = null; doorName = $"{name} doorway"; }
        else if (kind == "premises" && p.PlaceKind != null && p.PlaceKind is not ("fire_station" or "storage_facility" or "rv_park" or "self_storage"))
        { width = 1.0; doorPrefab = "glass_pull_door"; doorName = $"{name} entrance"; }
        else if (kind == "church") { width = 1.0; doorPrefab = "door"; doorName = $"{name} front door"; }
        else if (homes) { width = 0.95; doorPrefab = "door"; doorName = $"{name} front door"; }
        else { width = 0.95; doorPrefab = "door"; doorName = $"{name} door"; }
        width = Math.Max(0.8, Math.Min(width, run.S1 - run.S0 - 0.6));
        var (roomId, roomXz) = RoomForPoint(ids, F, run, s);
        var cut = (C0: s - width / 2, C1: s + width / 2, Top: DoorTop);

        for (int i = 0; i < runs.Count; i++)
            WallRun(F, runs[i], wallPrefab, wallH, $"{name} {SideWord(F, runs[i])} wall", i == k ? new[] { cut } : Array.Empty<(double, double, double)>());

        if (doorPrefab != null)
        {
            var (x, z) = RunPoint(F, run, s, -WallT / 2);
            Door(doorPrefab, x, z, FloorTop, RunPhi(F, run), roomId, -1, width, roomXz, doorName);
        }
        nextId = id;
        return outList;

        // gen_osm.py wall_run: a wall along a run, inside its line, with openings cut out of it and a lintel over each.
        void WallRun(Frame f, Run r, string prefab, double wtop, string nm, (double C0, double C1, double Top)[] cuts)
        {
            var pieces = new List<(double A0, double A1, double Y0, double Y1)>();
            double at = r.S0;
            foreach (var (c0, c1, ctop) in cuts.OrderBy(c => c.C0).ThenBy(c => c.C1).ThenBy(c => c.Top))
            {
                if (c0 > at) pieces.Add((at, c0, 0.0, wtop));
                if (ctop < wtop) pieces.Add((c0, c1, ctop, wtop));
                at = c1;
            }
            if (at < r.S1) pieces.Add((at, r.S1, 0.0, wtop));
            foreach (var (a0, a1, y0, y1) in pieces)
            {
                if (a1 - a0 < 0.02) continue;
                var (w0, w1) = r.Out > 0 ? (r.Line - WallT, r.Line) : (r.Line, r.Line + WallT);
                if (r.Axis == 'v') Obox(prefab, f, a0, a1, w0, w1, y0, y1, nm, "structure");
                else Obox(prefab, f, w0, w1, a0, a1, y0, y1, nm, "structure");
            }
        }

        // gen_osm.py door: a leaf in a doorway whose wall runs along phi, its +Z face (the outside) away from its room,
        // made to cover the opening and lap each jamb.
        void Door(string prefab, double x, double z, double y0, double phi, int a, int b, double opening, (double X, double Z) room, string nm)
        {
            var bs = Base(prefab);
            if (prefabs.TryGetValue(prefab, out var pf) && pf.Hinged)
            {
                double nx = -Math.Sin(phi), nz = Math.Cos(phi);
                if ((room.X - x) * nx + (room.Z - z) * nz > 0) phi += Math.PI;
            }
            outList.Add(new EntityData
            {
                EntityId = ++id,
                PrefabId = prefab,
                Position = new Vector3(R4(x + shift.X), R4(R4(y0 + bs.Y / 2) + lift), R4(z + shift.Z)),
                Rotation = WorldFeatures.Yaw(-phi),
                RegionAId = a,
                RegionBId = b,
                Scale = new Vector3(R4(Math.Round((opening + 2 * DoorLap) / bs.X, 4)), 1f, 1f),
                Name = nm,
                Layer = "structure",
            });
        }

        string Material(string prefab) => prefabs.TryGetValue(prefab, out var pf) && !string.IsNullOrEmpty(pf.Material) && pf.Material != "None" ? pf.Material : "Generic";
    }

    /// <summary>gen_osm.py room_for_point: the room a point just inside a run is in, and its middle.</summary>
    private static (int Id, (double X, double Z) Middle) RoomForPoint(List<(int Id, (double U0, double U1, double V0, double V1) Rect)> ids, Frame f, Run r, double s)
    {
        var (x, z) = RunPoint(f, r, s, -WallT - 0.3);
        foreach (var (rid, (u0, u1, v0, v1)) in ids)
        {
            var (u, v) = f.L(x, z);
            if (u0 <= u && u <= u1 && v0 <= v && v <= v1) return (rid, f.W((u0 + u1) / 2, (v0 + v1) / 2));
        }
        var first = ids[0];
        return (first.Id, f.W((first.Rect.U0 + first.Rect.U1) / 2, (first.Rect.V0 + first.Rect.V1) / 2));
    }

    /// <summary>gen_osm.py _samples: the ground under a box's footprint, at its corners, its middle, and a grid at
    /// most 20 m apart.</summary>
    public static IEnumerable<double> Samples(Frame f, double u0, double u1, double v0, double v1, Func<double, double, double> ground, double every = 20.0)
    {
        int nu = Math.Max(1, Math.Min(24, (int)Math.Ceiling((u1 - u0) / every)));
        int nv = Math.Max(1, Math.Min(24, (int)Math.Ceiling((v1 - v0) / every)));
        for (int i = 0; i <= nu; i++)
            for (int j = 0; j <= nv; j++)
            {
                var (x, z) = f.W(u0 + (u1 - u0) * i / nu, v0 + (v1 - v0) * j / nv);
                yield return ground(x, z);
            }
        var (mx, mz) = f.W((u0 + u1) / 2, (v0 + v1) / 2);
        yield return ground(mx, mz);
    }

    /// <summary>gen_osm.py h01: a number in [0, 1) from what a thing is (crc32 of its keys joined by "|"): the same
    /// house is the same house every run.</summary>
    public static double H01(params object[] keys)
    {
        string s = string.Join("|", keys.Select(k => k switch
        {
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        }));
        return Crc32(System.Text.Encoding.UTF8.GetBytes(s)) / 4294967296.0;
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>zlib.crc32.</summary>
    public static uint Crc32(byte[] bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static float R4(double v) => (float)Math.Round(v, 4);
}
