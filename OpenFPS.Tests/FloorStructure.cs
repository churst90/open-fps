using System.Numerics;
using OpenFPS.Materials;
using OpenFPS.Geometry;
using OpenFPS.Server.OneWorld;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Tests;

/// <summary>
/// What a room's floor is built of (docs/GEOMETRY.md 12.6, "A floor is a construction"): at points over the room's floor,
/// the solids stacked under what a body stands on, each less than a void's depth under the one before, must hold a
/// structure: a slab (airtight, stone or metal, 8 cm or more) or boards (airtight wood, 18 mm or more) over a void of
/// 10 cm or more, the ground included. A finish on its own fails: until
/// 2026-10-11 every home at medium detail stood on 27 cm of carpet or wood laid on the ground. Read from materials and
/// geometry only, never from what a thing is called.
/// </summary>
internal static class FloorStructure
{
    public const float SlabLeast = 0.08f, BoardLeast = 0.018f, VoidLeast = 0.10f;
    /// <summary>Faces this close are in contact, m: a map's numbers round to a millimetre.</summary>
    private const float Contact = 0.003f;
    /// <summary>How far over a room's underside a floor's top may be, m (a finish's top is 2 cm over it).</summary>
    public const float FloorReach = 0.10f;
    private const float Cell = 8f;

    private sealed record Solid(Vector3 Centre, Quaternion Inverse, Vector3 Half, float[]? Outline, MaterialProperties Props, string Prefab)
    {
        public float Top => Centre.Y + Half.Y;
        public float Bottom => Centre.Y - Half.Y;
        public float Thick => 2 * Half.Y;

        public bool Covers(float x, float z)
        {
            var l = Vector3.Transform(new Vector3(x, Centre.Y, z) - Centre, Inverse);
            if (MathF.Abs(l.X) > Half.X + 1e-3f || MathF.Abs(l.Z) > Half.Z + 1e-3f) return false;
            if (Outline is not { Length: >= 6 } o) return true;
            bool inside = false;
            for (int i = 0, j = o.Length / 2 - 1; i < o.Length / 2; j = i++)
            {
                float xi = o[2 * i], zi = o[2 * i + 1], xj = o[2 * j], zj = o[2 * j + 1];
                if ((zi > l.Z) != (zj > l.Z) && l.X < (xj - xi) * (l.Z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>Rooms looked at, points looked at, and each room with a point whose floor has no structure (its name,
    /// where, and the stack top to bottom), with how many rooms fail by each stack.</summary>
    public sealed record Result(int Rooms, int Points, List<string> Bare, Dictionary<string, int> ByStack);

    /// <summary>Every room (an acoustic region in the rooms layer, or each <paramref name="room"/> says is one) of
    /// <paramref name="entities"/>, nine points over its floor each. <paramref name="ground"/>: the ground's height at a
    /// point, or null where there is none to meet. <paramref name="floorReach"/>: how far over a room's underside its floor's
    /// top may be (<see cref="FloorReach"/>; more where a map's rooms run down through the slab under them).</summary>
    public static Result Check(IEnumerable<EntityData> entities, IReadOnlyDictionary<string, WorldBuildings.Prefab> prefabs,
                               Func<float, float, float?> ground, Func<EntityData, bool>? room = null, float floorReach = FloorReach)
    {
        room ??= x => x.Layer == "rooms";
        AcousticRegistry.Initialize();
        var list = entities.ToList();
        var solids = new List<Solid>();
        var grid = new Dictionary<(int, int), List<int>>();
        foreach (var e in list)
        {
            if (e.PrefabId is "acoustic_region" or "portal" || !prefabs.TryGetValue(e.PrefabId, out var pf) || pf.Hinged) continue;
            if (string.IsNullOrEmpty(pf.Material) || pf.Material == "None") continue;
            if (e.Form != null && e.Form.Kind != ShapeKind.Prism) continue;
            var r = e.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(e.Rotation);
            if (Vector3.Transform(Vector3.UnitY, r).Y < 0.999f) continue;
            var half = pf.Size * e.Scale * 0.5f;
            var s = new Solid(e.Position, Quaternion.Inverse(r), half, e.Form?.Outline, AcousticRegistry.GetProperties(pf.Material), e.PrefabId);
            int k = solids.Count;
            solids.Add(s);
            float reach = MathF.Sqrt(half.X * half.X + half.Z * half.Z);
            for (int i = (int)MathF.Floor((e.Position.X - reach) / Cell); i <= (int)MathF.Floor((e.Position.X + reach) / Cell); i++)
                for (int j = (int)MathF.Floor((e.Position.Z - reach) / Cell); j <= (int)MathF.Floor((e.Position.Z + reach) / Cell); j++)
                {
                    if (!grid.TryGetValue((i, j), out var cell)) grid[(i, j)] = cell = new List<int>();
                    cell.Add(k);
                }
        }

        int rooms = 0, points = 0;
        var bare = new List<string>();
        var byStack = new Dictionary<string, int>();
        var under = new List<Solid>();
        foreach (var r in list.Where(x => x.PrefabId == "acoustic_region" && room(x)))
        {
            rooms++;
            var half = (prefabs.TryGetValue(r.PrefabId, out var rp) ? rp.Size : Vector3.One) * r.Scale * 0.5f;
            var rot = r.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(r.Rotation);
            float floor = r.Position.Y - half.Y;
            string? worst = null;
            foreach (float fu in new[] { -1f, 0f, 1f })
                foreach (float fv in new[] { -1f, 0f, 1f })
                {
                    float au = fu * MathF.Max(0f, half.X - MathF.Min(0.3f, half.X / 2));
                    float av = fv * MathF.Max(0f, half.Z - MathF.Min(0.3f, half.Z / 2));
                    var at = r.Position + Vector3.Transform(new Vector3(au, 0f, av), rot);
                    points++;
                    under.Clear();
                    if (grid.TryGetValue(((int)MathF.Floor(at.X / Cell), (int)MathF.Floor(at.Z / Cell)), out var cell))
                        foreach (int k in cell)
                            if (solids[k].Top <= floor + floorReach && solids[k].Covers(at.X, at.Z)) under.Add(solids[k]);
                    string? why = Why(under, ground(at.X, at.Z));
                    if (why != null) worst ??= why;
                }
            if (worst == null) continue;
            byStack[worst] = byStack.GetValueOrDefault(worst) + 1;
            if (bare.Count < 40) bare.Add($"{r.Name} ({r.Position.X:F1}, {r.Position.Z:F1}): {worst}");
        }
        return new Result(rooms, points, bare, byStack);
    }

    /// <summary>Null if the floor at a point has a structure; else the stack under it, top to bottom.</summary>
    private static string? Why(List<Solid> under, float? ground)
    {
        if (under.Count == 0) return "nothing";
        under.Sort((a, b) => b.Top.CompareTo(a.Top));
        // What a body stands on, then everything under it down to the first void.
        var stack = new List<Solid> { under[0] };
        float bottom = under[0].Bottom;
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var s in under)
                if (!stack.Contains(s) && s.Top > bottom - VoidLeast && s.Bottom < bottom - 1e-4f)
                {
                    stack.Add(s);
                    bottom = s.Bottom;
                    grew = true;
                }
        }
        foreach (var s in stack)
        {
            if (s.Props.Porous) continue;
            if (s.Props.Family is "stone" or "metal" && s.Thick >= SlabLeast - 1e-4f) return null;
            if (s.Props.Family == "wood" && s.Thick >= BoardLeast - 1e-4f)
            {
                bool clear = (ground is not float g || g <= s.Bottom - VoidLeast)
                             && !under.Any(o => o != s && o.Top <= s.Bottom + Contact && o.Top > s.Bottom - VoidLeast);
                if (clear) return null;
            }
        }
        string onGround = ground is float gg && gg > bottom - VoidLeast ? " on the ground" : "";
        return string.Join(" / ", stack.Select(s => $"{s.Prefab} {s.Thick:F2} m")) + onGround;
    }
}
