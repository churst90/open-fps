using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.Core;

namespace OpenFPS.Tests;

/// <summary>
/// The ground of the real places (docs/GEOMETRY.md 5.1, step T2 of docs/WORLD_STREAMING.md "Stage 2 with
/// terrain"): the survey's heights laid as terrain, graded to what rests on it, and everything set on it.
/// </summary>
public partial class RealPlaceMapTests
{
    /// <summary>Counts only the tiles of ground.</summary>
    private readonly struct OnlyOwners : IGeometryFilter
    {
        private readonly HashSet<int> _owners;
        public OnlyOwners(HashSet<int> owners) => _owners = owners;
        public bool Accept(int owner, in Surface surface) => _owners.Contains(owner);
    }

    private static HashSet<int> TerrainIds(World ecs)
    {
        var ids = new HashSet<int>();
        ecs.Query(new QueryDescription().WithAll<TerrainTileComponent>(), (Entity e) => ids.Add(e.Id));
        return ids;
    }

    private static TriangleWorld ServerWorld(Place p)
    {
        Assert.True(p.Manager.TryGetGeometry(p.Data.Id, out var geometry));
        return geometry.World;
    }

    /// <summary>The survey as tools/places/ID/elevation.json has it, on its own grid of latitude and
    /// longitude, and the map's local east-north frame (gen_osm.py's P and geo_of) to find a point on it.</summary>
    private sealed class Survey
    {
        private readonly double _lon0, _lat0, _dlon, _dlat, _base, _unit;
        private readonly int _cols, _rows;
        private readonly short[] _v;
        private readonly double _olat, _olon;
        private readonly (double X, double Y, double Z) _o;
        private readonly double _sla, _cla, _slo, _clo;

        public Survey(string place, double originLat, double originLon)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tools", "places", place, "elevation.json")));
            var r = doc.RootElement;
            _lon0 = r.GetProperty("lon0").GetDouble(); _lat0 = r.GetProperty("lat0").GetDouble();
            _dlon = r.GetProperty("dlon").GetDouble(); _dlat = r.GetProperty("dlat").GetDouble();
            _cols = r.GetProperty("cols").GetInt32(); _rows = r.GetProperty("rows").GetInt32();
            _base = r.GetProperty("base").GetDouble(); _unit = r.GetProperty("unit").GetDouble();
            var bytes = Convert.FromBase64String(r.GetProperty("data").GetString()!);
            _v = new short[bytes.Length / 2];
            for (int k = 0; k < _v.Length; k++) _v[k] = (short)(bytes[2 * k] | (bytes[2 * k + 1] << 8));
            _olat = originLat; _olon = originLon;
            _o = Ecef(originLat, originLon);
            _sla = Math.Sin(originLat * Math.PI / 180); _cla = Math.Cos(originLat * Math.PI / 180);
            _slo = Math.Sin(originLon * Math.PI / 180); _clo = Math.Cos(originLon * Math.PI / 180);
        }

        private static (double, double, double) Ecef(double lat, double lon)
        {
            const double a = 6378137.0, f = 1 / 298.257223563;
            double e2 = f * (2 - f), la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
            double n = a / Math.Sqrt(1 - e2 * Math.Sin(la) * Math.Sin(la));
            return (n * Math.Cos(la) * Math.Cos(lo), n * Math.Cos(la) * Math.Sin(lo), n * (1 - e2) * Math.Sin(la));
        }

        private (double X, double Z) P(double lat, double lon)
        {
            var (x, y, z) = Ecef(lat, lon);
            double dx = x - _o.X, dy = y - _o.Y, dz = z - _o.Z;
            return (-_slo * dx + _clo * dy, -_sla * _clo * dx - _sla * _slo * dy + _cla * dz);
        }

        private (double Lat, double Lon) Geo(double x, double z)
        {
            double lat = _olat + z / 110_900.0, lon = _olon + x / (111_320.0 * Math.Cos(_olat * Math.PI / 180));
            for (int k = 0; k < 2; k++)
            {
                var (px, pz) = P(lat, lon);
                lat += (z - pz) / 110_900.0;
                lon += (x - px) / (111_320.0 * Math.Cos(_olat * Math.PI / 180));
            }
            return (lat, lon);
        }

        /// <summary>The survey's height over the sea at a point of the map.</summary>
        public double At(double x, double z)
        {
            var (lat, lon) = Geo(x, z);
            double fx = Math.Clamp((lon - _lon0) / _dlon, 0, _cols - 1.0), fy = Math.Clamp((_lat0 - lat) / _dlat, 0, _rows - 1.0);
            int i = Math.Min((int)fx, _cols - 2), j = Math.Min((int)fy, _rows - 2);
            double tx = fx - i, ty = fy - j;
            double H(int k) => _base + _v[k] * _unit;
            int q = j * _cols + i;
            return (H(q) * (1 - tx) + H(q + 1) * tx) * (1 - ty) + (H(q + _cols) * (1 - tx) + H(q + _cols + 1) * tx) * ty;
        }
    }

    /// <summary>
    /// Away from what the ground is graded to, the terrain is the survey: within 0.25 m at 99 points in a
    /// hundred and 0.6 m at every one, over 3,000 points of the place (2 m triangles over a 5 m resampling of
    /// the survey's own 5 m grid; the difference is the resampling on the creek banks).
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void The_ground_follows_the_survey(string id)
    {
        var p = _maps.Get(id);
        Assert.NotNull(p.Data.Elevation);
        var world = ServerWorld(p);
        var survey = new Survey(id, p.Data.GeoOrigin!.Lat, p.Data.GeoOrigin.Lon);
        var only = new OnlyOwners(TerrainIds(p.Ecs));
        var slabs = TerrainBuilder.Slabs(p.Ecs);
        // The slabs filed by 50 m squares, so each point asks only those near it.
        var near = new Dictionary<(int, int), List<TerrainBuilder.Slab>>();
        foreach (var s in slabs)
        {
            var (lo, hi) = s.Bounds;
            for (int a = (int)MathF.Floor(lo.X / 50f) - 1; a <= (int)MathF.Floor(hi.X / 50f) + 1; a++)
                for (int b = (int)MathF.Floor(lo.Y / 50f) - 1; b <= (int)MathF.Floor(hi.Y / 50f) + 1; b++)
                {
                    if (!near.TryGetValue((a, b), out var l)) near[(a, b)] = l = new List<TerrainBuilder.Slab>();
                    l.Add(s);
                }
        }
        var rng = new Random(9);
        var errors = new List<double>();
        var min = p.Data.WalkMin; var max = p.Data.WalkMax;
        int tries = 0;
        while (errors.Count < 3000 && tries++ < 100000)
        {
            float x = min.X + (float)rng.NextDouble() * (max.X - min.X), z = min.Z + (float)rng.NextDouble() * (max.Z - min.Z);
            if (near.TryGetValue(((int)MathF.Floor(x / 50f), (int)MathF.Floor(z / 50f)), out var l)
                && l.Any(s => s.DistanceFrom(x, z) < TerrainBuilder.SkirtMetres + 2f * TerrainTiles.Spacing + 1f)) continue;
            float y = world.FloorAt(x, z, 1000f, GeometryLayers.Ground, ref only, out _);
            Assert.True(y > -1000f, $"no ground at ({x}, {z})");
            double expected = survey.At(x, z) + p.Data.Elevation!.SeaLevelY;
            errors.Add(Math.Abs(y - expected));
        }
        errors.Sort();
        double p99 = errors[(int)(errors.Count * 0.99)], worst = errors[^1], median = errors[errors.Count / 2];
        _o.WriteLine($"{id}: {errors.Count} points off the graded ground; terrain against the survey: median {median:F3} m, 99th {p99:F3} m, worst {worst:F3} m");
        Assert.True(errors.Count >= 2000, $"only {errors.Count} points away from grading");
        Assert.True(p99 <= 0.25, $"99th percentile {p99:F3} m");
        Assert.True(worst <= 0.6, $"worst {worst:F3} m");
    }

    /// <summary>
    /// The ground never rises through what lies on it: along every road, every 5 m, the floor under the
    /// centreline is the road (its own slab, not the terrain), and at the middle of every lawn and drive
    /// the floor is the lawn or the drive. A tie or a hill poking through is what put footsteps on dirt in
    /// the middle of a road.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void The_ground_never_rises_through_what_lies_on_it(string id)
    {
        var p = _maps.Get(id);
        var world = ServerWorld(p);
        var terrain = TerrainIds(p.Ecs);
        var all = new AcceptAll();
        int checkedRoads = 0;
        var bad = new List<string>();
        foreach (var r in p.Data.Roads ?? new())
        {
            // Off the road's two ends, where its last slab stops at the centreline's last point.
            var line = r.Centreline;
            float total = 0f;
            for (int k = 0; k + 1 < line.Count; k++) total += Vector3.Distance(line[k], line[k + 1]);
            for (float s = 2f; s < total - 2f; s += 5f)
            {
                var at = RoadNetwork.PointAt(line, s);
                world.FloorAt(at.X, at.Z, at.Y + 1f, GeometryLayers.Ground, ref all, out var hit);
                checkedRoads++;
                if (hit.Triangle >= 0 && terrain.Contains(hit.Owner)) bad.Add($"{r.Name} at ({at.X:F2}, {at.Z:F2})");
            }
        }
        int checkedSlabs = 0;
        foreach (var e in p.Data.Entities)
        {
            if (e.Layer is not ("yards" or "drives")) continue;
            world.FloorAt(e.Position.X, e.Position.Z, e.Position.Y + 1f, GeometryLayers.Ground, ref all, out var hit);
            checkedSlabs++;
            if (hit.Triangle < 0 || terrain.Contains(hit.Owner)) bad.Add($"{e.Name ?? e.PrefabId} ({e.Layer}) at ({e.Position.X:F1}, {e.Position.Z:F1})");
        }
        foreach (var b in bad.Take(15)) _o.WriteLine(b);
        _o.WriteLine($"{id}: {checkedRoads} points of road and {checkedSlabs} lawns and drives, {bad.Count} with the ground on top");
        Assert.Empty(bad);
    }

    /// <summary>
    /// A walk from the spawn: out of the drive toward the street, along it and back, at the game's walk,
    /// through the server's own movement and ground. On the ground every tick, no step of the feet more than
    /// a kerb's height, and the spawn itself on the ground.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void A_walk_from_the_spawn_stays_on_the_ground(string id)
    {
        var p = _maps.Get(id);
        var world = ServerWorld(p);
        var all = new AcceptAll();
        var spawn = p.Data.SpawnPoint.Position;
        // The server drops a body in from a metre over the ground under the spawn (MapManager.VerifySpawnPoint).
        float under = world.Ground(spawn - new Vector3(0f, 1f, 0f), PhysicsConstants.PlayerRadius, PhysicsConstants.StepHeight, GeometryLayers.Ground, ref all, out _, out _);
        Assert.True(MathF.Abs(spawn.Y - 1f - under) < 0.05f, $"spawn at {spawn.Y}, ground {under}");
        var facing = Vector3.Transform(Vector3.UnitZ, p.Data.SpawnPoint.Rotation);
        var side = new Vector3(facing.Z, 0f, -facing.X);
        Vector3 pos = new(spawn.X, under, spawn.Z), vel = Vector3.Zero;
        var solids = new List<SolidRef>();
        int airborne = 0, worstAir = 0, ticks = 0;
        float worstStep = 0f;
        foreach (var (dir, n) in new[] { (facing, 150), (side, 300), (-side, 300) })
            for (int t = 0; t < n; t++, ticks++)
            {
                float ground = world.Ground(pos, PhysicsConstants.PlayerRadius, PhysicsConstants.StepHeight, GeometryLayers.Ground, ref all, out _, out _);
                var ctx = new SharedMovementEngine.MovementContext
                {
                    Position = pos, Velocity = vel, InputDirection = dir, DeltaTime = PhysicsConstants.FixedDeltaTime, GroundHeight = ground,
                    Gravity = PhysicsConstants.Gravity, JumpForce = PhysicsConstants.JumpPower, Speed = PhysicsConstants.FootSpeed(false, float.MaxValue),
                    PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
                    MapMin = p.Data.WalkMin, MapMax = p.Data.WalkMax, Body = BodyShape.Capsule,
                };
                ctx.Grade = SharedMovementEngine.GradeAlong(world, ref all, pos, dir);
                SharedMovementEngine.GatherSolids(ctx, world, ref all, solids);
                var obstacles = new SharedMovementEngine.Obstacles(ReadOnlySpan<SharedMovementEngine.Collider>.Empty, world,
                                                                   System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids));
                var before = pos;
                (pos, vel, bool grounded) = SharedMovementEngine.Step(ctx, obstacles, out _);
                worstStep = MathF.Max(worstStep, MathF.Abs(pos.Y - before.Y));
                airborne = grounded ? 0 : airborne + 1;
                worstAir = Math.Max(worstAir, airborne);
            }
        _o.WriteLine($"{id}: {ticks} ticks from the spawn, ended at ({pos.X:F1}, {pos.Z:F1}, {pos.Y:F2}); longest in the air {worstAir} ticks, biggest step {worstStep:F3} m");
        Assert.True(worstAir <= 2, $"in the air for {worstAir} ticks");
        Assert.True(worstStep <= PhysicsConstants.StepHeight, $"a step of {worstStep} m");
    }

    /// <summary>There is ground everywhere a body can go: every 25 m over the play area, the probe finds it.</summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void There_is_ground_everywhere(string id)
    {
        var p = _maps.Get(id);
        var world = ServerWorld(p);
        var all = new AcceptAll();
        var min = p.Data.WalkMin; var max = p.Data.WalkMax;
        int points = 0, holes = 0;
        for (float x = min.X + 1f; x < max.X; x += 25f)
            for (float z = min.Z + 1f; z < max.Z; z += 25f)
            {
                points++;
                if (world.FloorAt(x, z, 1000f, GeometryLayers.Ground, ref all, out _) <= -1000f) holes++;
            }
        _o.WriteLine($"{id}: {points} points, {holes} without ground; {TerrainIds(p.Ecs).Count} tiles of ground");
        Assert.Equal(0, holes);
    }
}
