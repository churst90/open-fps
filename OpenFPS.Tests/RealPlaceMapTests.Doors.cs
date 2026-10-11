using System.Collections.Concurrent;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;

namespace OpenFPS.Tests;

public partial class RealPlaceMapTests
{
    /// <summary>Every door of a place is rendered as the map arrives (DoorSystem.RenderKeys): a list made of the
    /// city's door sizes left the places' doors silent on their first swing.</summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Every_door_is_rendered_as_the_map_arrives(string id)
    {
        Assert.True(_maps.Get(id).Manager.TryGetMap(id, out var ecs, out _, out _, out _));
        DoorPrewarmTests.EveryDoorIsRenderedAsTheMapArrives(ecs, 1000);
        _o.WriteLine($"{id}: {DoorSystem.RenderKeys(ecs).Length} door renders");
    }

    /// <summary>
    /// Every way into every building and room of a place can be walked to and through, with the server's own
    /// teleport check (MovementSystem.CheckCollision) and ground probe: the floor a step out from each doorway is
    /// clear on both sides and within a step of the threshold, nothing solid stands where a hinged leaf swings, and
    /// every room of a building (but a sliver narrower than a body) is reached on foot from outside it, every door
    /// taken as open. Cody, 2026-10-10, on Bobcat Lane: the kitchen counter stood across the back door, "so I can't
    /// slide it open to get in or out". The rules the generator keeps for it: docs/MAPS_FROM_REAL_PLACES.md.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Every_way_in_can_be_walked_through(string id)
    {
        var p = _maps.Get(id);
        Assert.True(p.Manager.TryGetMap(id, out var ecs, out _, out var grid, out _));
        var rooms = new Dictionary<int, Room>();
        ecs.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (Entity e, ref Transform t, ref RegionComponent r) =>
        {
            if (r.RoomSize.X > 0f && r.IsIndoor) rooms[e.Id] = new Room(e.Id, r.FriendlyName, t.Position, Turn(t.Rotation), r.RoomSize);
        });
        var ways = new List<Way>();
        ecs.Query(new QueryDescription().WithAll<Transform, PortalComponent>(), (Entity e, ref Transform t, ref PortalComponent w) =>
        {
            if (w.RegionAId == w.RegionBId) return;
            if (!rooms.ContainsKey(w.RegionAId) && !rooms.ContainsKey(w.RegionBId)) return;
            string name = ecs.Has<IdentityComponent>(e) ? ecs.Get<IdentityComponent>(e).Name : "";
            float half, tall;
            Vector3 centre;
            Quaternion turn;
            if (ecs.Has<DoorComponent>(e))
            {
                DoorSystem.Doorway(ecs, e, out centre, out turn);
                var size = ecs.Get<ColliderComponent>(e).Size;
                half = size.X * 0.5f; tall = size.Y;
            }
            else
            {
                centre = t.Position; turn = Turn(t.Rotation);
                var size = ecs.Has<ColliderComponent>(e) ? ecs.Get<ColliderComponent>(e).Size : new Vector3(w.ApertureSize, 2f, 0.1f);
                half = size.X * 0.5f; tall = size.Y;
            }
            ways.Add(new Way(e, string.IsNullOrEmpty(name) ? $"opening #{e.Id}" : name, centre, turn, half, centre.Y - tall * 0.5f,
                             w.RegionAId, w.RegionBId, ecs.Has<DoorComponent>(e)));
        });

        // The buildings: rooms joined by their ways through.
        var parent = rooms.Keys.ToDictionary(k => k, k => k);
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var w in ways)
            if (rooms.ContainsKey(w.A) && rooms.ContainsKey(w.B)) parent[Find(w.A)] = Find(w.B);
        var buildings = rooms.Keys.GroupBy(Find).Select(g => g.Select(k => rooms[k]).ToList()).ToList();

        var problems = new ConcurrentBag<string>();
        int checkedWays = 0;
        Parallel.ForEach(buildings, new ParallelOptions { MaxDegreeOfParallelism = 8 }, building =>
        {
            var mine = building.Select(r => r.Id).ToHashSet();
            var own = ways.Where(w => mine.Contains(w.A) || mine.Contains(w.B)).ToList();
            var outside = own.Where(w => !mine.Contains(w.A) || !mine.Contains(w.B)).ToList();
            if (outside.Count == 0)
            {
                // A leftover sliver of a footprint narrower than a body, joined to nothing, is no room anyone enters.
                if (building.Any(r => MathF.Min(r.Size.X, r.Size.Z) >= 2f * PhysicsConstants.PlayerRadius + 0.05f))
                    problems.Add($"{building[0].Name} (#{building[0].Id}): no way in");
                return;
            }
            float floor = building.Min(r => r.Bottom);

            foreach (var w in own)
            {
                // A way through a wall: a door, or a doorway or an arch with a head over it. An opening as high as
                // the room (where two of a footprint's rectangles or a leftover piece meet, with no wall between)
                // is air, walked across wherever the walk finds it.
                if (!w.Door && (w.Centre.Y - w.Threshold) * 2f > DoorwayHighest) continue;
                Interlocked.Increment(ref checkedWays);
                var across = Offsets(w);
                foreach (float side in new[] { 1f, -1f })
                {
                    string? trouble = null;
                    foreach (float s in across)
                    {
                        var step = w.Centre + w.Across * s + w.Normal * (side * Approach);
                        var feet = Standing(ecs, grid, step, w.Threshold, w.Door ? w.Entity : null);
                        if (feet is { } f && MathF.Abs(f.Y - w.Threshold) <= PhysicsConstants.StepHeight - 0.02f) { trouble = null; break; }
                        trouble ??= feet is { } g
                            ? $"{w.Name}: a step of {w.Threshold - g.Y:F2} m between the threshold and the ground {Approach} m out {(InRoom(building, step) ? "inside" : "outside")} ({PlayerCoordinates.Format(step)})"
                            : $"{w.Name}: no floor clear to stand on {Approach} m out {(InRoom(building, step) ? "inside" : "outside")} "
                              + $"({PlayerCoordinates.Format(step)}; {Why(ecs, grid, step, w.Threshold, w.Door ? w.Entity : null)})";
                    }
                    if (trouble != null) problems.Add(trouble);
                }
                if (w.Door && ecs.Get<DoorComponent>(w.Entity) is { Slides: false } leaf)
                    foreach (float open in new[] { 0.33f, 0.67f, 1f })
                    {
                        var (c, r) = DoorSystem.LeafAt(ecs, w.Entity, leaf, open);
                        foreach (float along in new[] { 0.2f, 0.4f })
                        {
                            // The free half of the leaf, at hip and chest height: clear of its own jamb and lintel.
                            var at = c + Vector3.Transform(new Vector3(-leaf.HingeSide * along * w.Half * 2f, 0f, 0f), r);
                            at.Y = w.Threshold + 0.3f;
                            if (MovementSystem.CheckCollision(ecs, grid, at, 0.04f, 1.2f, w.Entity))
                            {
                                problems.Add($"{w.Name}: something solid where the leaf swings, {open * 100:F0} % open ({PlayerCoordinates.Format(at)})");
                                goto swung;
                            }
                        }
                    }
                swung:;
            }

            // On foot from the front door, every door open: from outside each way in, since the yard between them is
            // walked round (a side door is reached from the front door outside, past the walk's edge).
            var start = outside.FirstOrDefault(w => w.Name.EndsWith("front door", StringComparison.Ordinal)) ?? outside[0];
            var from = new List<Vector3>();
            foreach (var w in outside)
            {
                float outward = InRoom(building, w.Centre + w.Normal * Approach) ? -1f : 1f;
                if (Standing(ecs, grid, w.Centre + w.Normal * (outward * Approach), w.Threshold, w.Door ? w.Entity : null) is { } f) from.Add(f);
            }
            if (from.Count == 0) return;   // said above
            var unlinked = new ConcurrentBag<string>();
            var reached = Walk(ecs, grid, building, own, from, floor, unlinked);
            // A sliver of air narrower than a body (a footprint's leftover beside a wall) is a room no one stands in.
            foreach (var room in building.Where(r => MathF.Min(r.Size.X, r.Size.Z) >= 2f * PhysicsConstants.PlayerRadius + 0.05f))
                if (!reached.Any(c => room.Holds(c, 0.15f) && MathF.Abs(c.Y - room.Bottom) < 0.6f))
                    problems.Add($"{room.Name} (#{room.Id}): not reached on foot from {start.Name}; not walked through: {string.Join("; ", unlinked)}; its ways: "
                               + string.Join(" | ", own.Where(w => w.A == room.Id || w.B == room.Id).Select(w => $"{w.Name} at {PlayerCoordinates.Format(w.Centre)} "
                                   + string.Join(", ", new[] { 1f, -1f }.Select(side =>
                                       Standing(ecs, grid, w.Centre + w.Normal * (side * Approach), w.Threshold, w.Door ? w.Entity : null) is { } f
                                           ? $"{(side > 0 ? "+" : "-")} stands {f.Y - w.Threshold:F2}"
                                           : $"{(side > 0 ? "+" : "-")} {Why(ecs, grid, w.Centre + w.Normal * (side * Approach), w.Threshold, w.Door ? w.Entity : null)}")))));
        });

        var list = problems.OrderBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var s in list.Take(60)) _o.WriteLine(s);
        _o.WriteLine($"{id}: {buildings.Count} buildings, {checkedWays} doors and doorways, {list.Count} problems");
        Assert.Empty(list);
    }

    /// <summary>How far out from a doorway's middle a body stands to go through it, metres: off a 25 cm wall with
    /// a body's radius to spare.</summary>
    private const float Approach = 0.6f;

    /// <summary>The highest an opening with no leaf stands and is a doorway, metres: gen_osm's DOOR_TOP over the floor is
    /// 2.13; an opening between rooms with no wall reaches the ceiling, 2.7.</summary>
    private const float DoorwayHighest = 2.4f;

    /// <summary>The spacing of the walk's grid over a building, metres.</summary>
    private const float WalkCell = 0.25f;

    private sealed record Room(int Id, string Name, Vector3 Centre, Quaternion Turn, Vector3 Size)
    {
        public float Bottom => Centre.Y - Size.Y * 0.5f;
        public bool Holds(Vector3 p, float margin)
        {
            var local = Vector3.Transform(p - Centre, Quaternion.Inverse(Turn));
            return MathF.Abs(local.X) <= Size.X * 0.5f + margin && MathF.Abs(local.Z) <= Size.Z * 0.5f + margin;
        }
    }

    private sealed record Way(Entity Entity, string Name, Vector3 Centre, Quaternion Turn, float Half, float Threshold, int A, int B, bool Door)
    {
        public Vector3 Normal => Vector3.Transform(Vector3.UnitZ, Turn);
        public Vector3 Across => Vector3.Transform(Vector3.UnitX, Turn);
    }

    /// <summary>Where across a way a body goes through it: a door through its middle; an opening with no leaf anywhere
    /// along it, from the middle out a quarter-metre at a time, a body's radius clear of each end.</summary>
    private static List<float> Offsets(Way w)
    {
        var across = new List<float> { 0f };
        if (!w.Door)
            for (float s = 0.25f; s <= w.Half - PhysicsConstants.PlayerRadius + 1e-3f; s += 0.25f) { across.Add(s); across.Add(-s); }
        return across;
    }

    private static Quaternion Turn(Quaternion q) => q.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(q);

    private static bool InRoom(List<Room> building, Vector3 p) => building.Any(r => r.Holds(p, 0f));

    /// <summary>Where a body stands at a point on the floor near <paramref name="level"/>, or null when the server
    /// would find it solid there (the door's own leaf aside: it opens).</summary>
    private static Vector3? Standing(World ecs, SpatialGrid<Entity> grid, Vector3 at, float level, Entity? door)
    {
        float ground = PhysicsUtils.GetGroundHeight(ecs, grid, new Vector3(at.X, level + 0.3f, at.Z), out _);
        if (ground < level - 1.5f) return null;
        var feet = new Vector3(at.X, ground + 0.01f, at.Z);
        return MovementSystem.CheckCollision(ecs, grid, feet, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight, door)
            ? null : feet;
    }

    /// <summary>Why a body cannot stand there: no floor near, or what it would stand in (as the box path finds it).</summary>
    private static string Why(World ecs, SpatialGrid<Entity> grid, Vector3 at, float level, Entity? door)
    {
        float ground = PhysicsUtils.GetGroundHeight(ecs, grid, new Vector3(at.X, level + 0.3f, at.Z), out string material);
        if (ground < level - 1.5f) return $"no floor within 1.5 m below the threshold {level:F2}";
        var centre = new Vector3(at.X, ground + 0.01f + 0.4f + (PhysicsConstants.PlayerHeight - 0.4f) * 0.5f, at.Z);
        var names = new List<string>();
        foreach (var e in grid.GetItemsInRadius(at, 4f))
        {
            if (door is { } d && e.Id == d.Id || !ecs.Has<ColliderComponent>(e)) continue;
            var c = ecs.Get<ColliderComponent>(e);
            if (!c.IsSolid) continue;
            var t = ecs.Get<Transform>(e);
            var local = Vector3.Transform(centre - t.Position, Quaternion.Inverse(SharedMovementEngine.StandingRotation(c.Shape, t.Rotation)));
            if (GeometryUtils.AABBIntersectsCylinder(-c.Size / 2f, c.Size / 2f, local, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight - 0.4f))
                names.Add(ecs.Has<IdentityComponent>(e) ? ecs.Get<IdentityComponent>(e).Name is { Length: > 0 } n ? n : ecs.Get<IdentityComponent>(e).PrefabId : $"#{e.Id}");
        }
        return $"floor {ground:F2} ({material}) for a threshold at {level:F2}; in {(names.Count > 0 ? string.Join(", ", names.Distinct()) : "something the box path does not see")}";
    }

    /// <summary>
    /// Every point of a grid over a building and a margin round it that a body reaches on foot from
    /// <paramref name="from"/>: a step to each neighbour no higher than a stair's, onto a spot the teleport check
    /// finds clear, a door in the way taken as open.
    /// </summary>
    private static List<Vector3> Walk(World ecs, SpatialGrid<Entity> grid, List<Room> building, List<Way> ways, List<Vector3> from, float floor,
                                      ConcurrentBag<string>? unlinked = null)
    {
        var lo = new Vector2(float.MaxValue); var hi = new Vector2(float.MinValue);
        foreach (var r in building)
            foreach (var (sx, sz) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var c = r.Centre + Vector3.Transform(new Vector3(sx * r.Size.X * 0.5f, 0f, sz * r.Size.Z * 0.5f), r.Turn);
                lo = Vector2.Min(lo, new Vector2(c.X, c.Z)); hi = Vector2.Max(hi, new Vector2(c.X, c.Z));
            }
        // The ways in too: a room's box can stop short of its walls, and a door's step outside is past them.
        foreach (var w in ways)
        {
            lo = Vector2.Min(lo, new Vector2(w.Centre.X, w.Centre.Z));
            hi = Vector2.Max(hi, new Vector2(w.Centre.X, w.Centre.Z));
        }
        lo -= new Vector2(2f); hi += new Vector2(2f);
        int nx = (int)MathF.Ceiling((hi.X - lo.X) / WalkCell) + 1, nz = (int)MathF.Ceiling((hi.Y - lo.Y) / WalkCell) + 1;
        var feet = new Vector3?[nx, nz];
        var tried = new bool[nx, nz];
        var doors = ways.Where(w => w.Door).ToList();

        Vector3? Spot(int i, int k)
        {
            if (tried[i, k]) return feet[i, k];
            tried[i, k] = true;
            var at = new Vector3(lo.X + i * WalkCell, 0f, lo.Y + k * WalkCell);
            // A door within reach of the spot is taken as open: the nearest, which is the one a body there walks through.
            Entity? leaf = null;
            float best = float.MaxValue;
            foreach (var d in doors)
            {
                float dist = Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(d.Centre.X, d.Centre.Z));
                if (dist < d.Half + 0.6f && dist < best) { best = dist; leaf = d.Entity; }
            }
            return feet[i, k] = Standing(ecs, grid, at, floor, leaf);
        }

        // The standable grid point nearest a spot, within a cell and a half of it.
        (int, int)? Near(Vector3 p)
        {
            (int, int)? best = null;
            float bestD = float.MaxValue;
            int ci = (int)MathF.Round((p.X - lo.X) / WalkCell), ck = (int)MathF.Round((p.Z - lo.Y) / WalkCell);
            for (int i = ci - 1; i <= ci + 1; i++)
                for (int k = ck - 1; k <= ck + 1; k++)
                {
                    if (i < 0 || k < 0 || i >= nx || k >= nz || Spot(i, k) is not { } s || MathF.Abs(s.Y - p.Y) > PhysicsConstants.StepHeight) continue;
                    float d = Vector2.Distance(new Vector2(s.X, s.Z), new Vector2(p.X, p.Z));
                    if (d < bestD) { bestD = d; best = (i, k); }
                }
            return best;
        }

        // A doorway is narrower than a grid at any turn can thread, so each way through is walked as a body walks
        // it, straight across from a step out on one side to a step out on the other.
        var through = new Dictionary<(int, int), List<(int, int)>>();
        foreach (var w in ways)
        foreach (float s in Offsets(w))
        {
            var leaf = w.Door ? w.Entity : (Entity?)null;
            var mid0 = w.Centre + w.Across * s;
            if (Standing(ecs, grid, mid0 + w.Normal * Approach, w.Threshold, leaf) is not { } a
                || Standing(ecs, grid, mid0 - w.Normal * Approach, w.Threshold, leaf) is not { } b) continue;
            bool clear = true;
            Vector3 stuck = default;
            for (float t = 0.1f; t < 2f * Approach && clear; t += 0.1f)
            {
                stuck = Vector3.Lerp(a, b, t / (2f * Approach));
                clear = Standing(ecs, grid, stuck, w.Threshold, leaf) is { } mid
                        && MathF.Abs(mid.Y - a.Y) <= PhysicsConstants.StepHeight + MathF.Abs(b.Y - a.Y);
            }
            if (!clear) { unlinked?.Add($"{w.Name} blocked crossing at {PlayerCoordinates.Format(stuck)} ({Why(ecs, grid, stuck, w.Threshold, leaf)})"); continue; }
            if (Near(a) is not { } ca || Near(b) is not { } cb) { unlinked?.Add($"{w.Name}: no grid point by a step out"); continue; }
            (through.TryGetValue(ca, out var la) ? la : through[ca] = new()).Add(cb);
            (through.TryGetValue(cb, out var lb) ? lb : through[cb] = new()).Add(ca);
        }

        var queue = new Queue<(int, int)>();
        var seen = new bool[nx, nz];
        var reached = new List<Vector3>();
        foreach (var seed in from)
            if (Near(seed) is { } first && !seen[first.Item1, first.Item2]) { queue.Enqueue(first); seen[first.Item1, first.Item2] = true; }
        while (queue.Count > 0)
        {
            var (i, k) = queue.Dequeue();
            var here = feet[i, k]!.Value;
            reached.Add(here);
            foreach (var (di, dk) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int a = i + di, b = k + dk;
                if (a < 0 || b < 0 || a >= nx || b >= nz || seen[a, b]) continue;
                if (Spot(a, b) is not { } there || MathF.Abs(there.Y - here.Y) > PhysicsConstants.StepHeight) continue;
                seen[a, b] = true;
                queue.Enqueue((a, b));
            }
            if (through.TryGetValue((i, k), out var across))
                foreach (var (a, b) in across)
                    if (!seen[a, b]) { seen[a, b] = true; queue.Enqueue((a, b)); }
        }
        return reached;
    }
}
