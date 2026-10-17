using System.Numerics;
using OpenFPS.Materials;
using OpenFPS.Geometry;
using OpenFPS.Server.Repositories;
using OpenFPS.Acoustics;

namespace OpenFPS.Tests;

/// <summary>
/// What a building's walls are built of (docs/GEOMETRY.md 12.6, "Timber walls are framed"): every upright sheet of
/// wood, plasterboard or brick a building is made of (the structure and interiors layers, not a door, no shape) must be
/// a framed construction. An outside wall is an assembly whose face is its own material, with sheathing, a stud cavity
/// of glass fibre and plasterboard behind it, or the same without the board and a board of its own against its inner
/// face; its plies run from its -Z face, which faces out of its building (off the floor slab), to its +Z face. A
/// partition is two leaves of plasterboard over studs. A building that is one solid of its footprint (a shed) is an
/// assembly too. Read from materials, builds and geometry, never from what a thing is called; until 2026-10-11 the real
/// places' walls were 25 cm of solid wood or brick.
/// </summary>
internal static class FramedWalls
{
    /// <summary>The thickest an upright piece can be and still be a wall, m.</summary>
    private const float WallMost = 0.4f;
    private const float Cell = 8f;

    public sealed record Result(int Outer, int Unlined, int Boards, int Partitions, int Sheds, List<string> Bad, Dictionary<string, int> Why);

    private sealed record Box(EntityData E, PrefabTemplate T, Vector3 Size, Quaternion Rot, Quaternion Inverse);

    public static Result Check(IEnumerable<EntityData> entities, IReadOnlyDictionary<string, PrefabTemplate> prefabs)
    {
        AcousticRegistry.Initialize();
        var walls = new List<Box>();
        var slabs = new List<Box>();
        var sheds = new List<Box>();
        foreach (var e in entities)
        {
            if (e.Layer is not ("structure" or "interiors") || !prefabs.TryGetValue(e.PrefabId, out var t)) continue;
            if (t.IsDoor == true || t.IsSolid == false || t.ColliderSize is not { } baseSize) continue;
            var props = AcousticRegistry.GetProperties(t.Material);
            var r = e.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(e.Rotation);
            if (Vector3.Transform(Vector3.UnitY, r).Y < 0.999f) continue;
            var box = new Box(e, t, Vector3.Abs(baseSize * e.Scale), r, Quaternion.Inverse(r));
            if (e.Form is { Kind: ShapeKind.Prism } && props.Family == "stone" && box.Size.Y < 0.5f) { slabs.Add(box); continue; }
            bool built = props.Family is "wood" or "building" || t.Material == "Brick";
            if (!built) continue;
            if (e.Form is { Kind: ShapeKind.Prism } && box.Size.Y >= 2f && MathF.Min(box.Size.X, box.Size.Z) >= 1f) { sheds.Add(box); continue; }
            if (e.Form != null || MathF.Min(box.Size.X, box.Size.Z) > WallMost || box.Size.Y < 0.02f) continue;
            walls.Add(box);
        }
        var slabGrid = Grid(slabs);
        var wallGrid = Grid(walls);
        int outer = 0, unlined = 0, boards = 0, partitions = 0, shedCount = 0;
        var bad = new List<string>();
        var why = new Dictionary<string, int>();
        void Fail(Box b, string reason)
        {
            why[reason] = why.GetValueOrDefault(reason) + 1;
            if (bad.Count < 50) bad.Add($"{b.E.Name} ({b.E.PrefabId}, {b.E.EntityId}): {reason}");
        }
        foreach (var s in sheds)
        {
            if (Assemblies.Named(s.T.Assembly) is { } a && Assemblies.HasCavity(a)) shedCount++;
            else Fail(s, "a building of one solid is not framed");
        }
        foreach (var w in walls)
        {
            var t = w.T;
            if (Assemblies.Named(t.Assembly) is { } a)
            {
                var plies = a.Plies.Select(p => AcousticRegistry.GetProperties(p.Material)).ToList();
                bool face = a.Plies[0].Material == t.Material;
                bool sheathing = a.Plies.Any(p => p.Material == "Plywood");
                bool studs = a.Plies.Any(p => p.Material == "GlassWool" && p.StudSpacingMetres > 0f);
                bool lined = a.Plies[^1].Material == "Plaster";
                if (!face || !sheathing || !studs) { Fail(w, $"assembly {a.Name} is not cladding, sheathing and studs"); continue; }
                // A sliver at a corner (2 cm along the wall) is thicker than it is long: its plies still run along Z.
                if (MathF.Abs(w.Size.Z - a.Metres) > 0.002f) { Fail(w, $"{w.Size.Z:F4} m through, its assembly {a.Metres:F4}"); continue; }
                var z = Vector3.Transform(Vector3.UnitZ, w.Rot);
                float half = w.Size.Z / 2;
                if (w.Size.X >= 0.3f && w.Size.Y >= 0.3f)
                {
                    bool outIn = OnSlab(slabGrid, w.E.Position - z * (half + 0.05f));
                    bool inIn = OnSlab(slabGrid, w.E.Position + z * (half + 0.05f));
                    // Over a slab on both sides: a wall where two buildings touch, either way about.
                    if (!inIn) { Fail(w, outIn ? "its cladding faces into its building" : "nothing of its building inside it"); continue; }
                }
                if (lined) outer++;
                else
                {
                    unlined++;
                    if (w.Size.X >= 0.3f && !Near(wallGrid, w).Any(o => IsBoard(o) && Contact(w, o)))
                        Fail(w, "unlined, with no plasterboard against it");
                }
                continue;
            }
            if (t.Material == "Plaster" && (t.LeafMetres ?? 0f) > 0f && (t.StudSpacingMetres ?? 0f) > 0f
                && MathF.Min(w.Size.X, w.Size.Z) >= 2f * t.LeafMetres!.Value + WallTransmission.MinCavityMetres)
            {
                partitions++;
                continue;
            }
            if (IsBoard(w))
            {
                // A 2 cm end kept where a doorway runs into a corner (gen_osm edge_wall) stands against nothing.
                if (w.Size.X <= 0.021f || Near(wallGrid, w).Any(o => Assemblies.Named(o.T.Assembly) is { } oa && oa.Plies[^1].Material != "Plaster" && Contact(w, o))) boards++;
                else Fail(w, "plasterboard against no unlined wall");
                continue;
            }
            Fail(w, $"{t.Material} {MathF.Min(w.Size.X, w.Size.Z):F3} m, not framed");
        }
        return new Result(outer, unlined, boards, partitions, shedCount, bad, why);
    }

    private static bool IsBoard(Box b) => b.T.Material == "Plaster" && b.T.Assembly == null && (b.T.LeafMetres ?? 0f) == 0f && b.Size.Z <= 0.016f;

    /// <summary>Whether two upright boxes stand face to face across their Z (a wall and its board), sharing some of
    /// their face; a lintel over a door is too short to be a sheet (Constructions.IsSheet) but has its board all the same.</summary>
    private static bool Contact(Box a, Box b)
    {
        var za = Vector3.Transform(Vector3.UnitZ, a.Rot);
        if (MathF.Abs(Vector3.Dot(za, Vector3.Transform(Vector3.UnitZ, b.Rot))) < 0.9999f) return false;
        var d = Vector3.Transform(b.E.Position - a.E.Position, a.Inverse);
        return MathF.Abs(MathF.Abs(d.Z) - (a.Size.Z + b.Size.Z) / 2) < 0.003f
               && MathF.Abs(d.X) < (a.Size.X + b.Size.X) / 2 - 0.01f && MathF.Abs(d.Y) < (a.Size.Y + b.Size.Y) / 2 - 0.01f;
    }

    private static Dictionary<(int, int), List<Box>> Grid(List<Box> boxes)
    {
        var grid = new Dictionary<(int, int), List<Box>>();
        foreach (var b in boxes)
        {
            float reach = MathF.Sqrt(b.Size.X * b.Size.X + b.Size.Z * b.Size.Z) / 2;
            for (int i = (int)MathF.Floor((b.E.Position.X - reach) / Cell); i <= (int)MathF.Floor((b.E.Position.X + reach) / Cell); i++)
                for (int j = (int)MathF.Floor((b.E.Position.Z - reach) / Cell); j <= (int)MathF.Floor((b.E.Position.Z + reach) / Cell); j++)
                {
                    if (!grid.TryGetValue((i, j), out var l)) grid[(i, j)] = l = new List<Box>();
                    l.Add(b);
                }
        }
        return grid;
    }

    private static IEnumerable<Box> Near(Dictionary<(int, int), List<Box>> grid, Box b)
        => grid.TryGetValue(((int)MathF.Floor(b.E.Position.X / Cell), (int)MathF.Floor(b.E.Position.Z / Cell)), out var l) ? l.Where(o => o != b) : Enumerable.Empty<Box>();

    /// <summary>Whether a point is over a floor slab (a stone prism), across its outline.</summary>
    private static bool OnSlab(Dictionary<(int, int), List<Box>> grid, Vector3 p)
    {
        if (!grid.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell)), out var l)) return false;
        foreach (var s in l)
        {
            var q = Vector3.Transform(p - s.E.Position, s.Inverse);
            if (MathF.Abs(q.X) > s.Size.X / 2 + 1e-3f || MathF.Abs(q.Z) > s.Size.Z / 2 + 1e-3f) continue;
            if (s.E.Form?.Outline is not { Length: >= 6 } o) return true;
            bool inside = false;
            for (int i = 0, j = o.Length / 2 - 1; i < o.Length / 2; j = i++)
            {
                float xi = o[2 * i], zi = o[2 * i + 1], xj = o[2 * j], zj = o[2 * j + 1];
                if ((zi > q.Z) != (zj > q.Z) && q.X < (xj - xi) * (q.Z - zi) / (zj - zi) + xi) inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }
}
