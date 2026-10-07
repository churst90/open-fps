using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The city's blocks of flats, walked up and down by the real movement engine over the real map:
/// every flight from the ground floor to the roof and back, the roof's parapet, and what the map
/// tells the client about each flight — its two ends as stair markers, said and blipped.
///
/// "We need to also implement stairs ... I need to get up there" (Cody, 2026-10-03). The flights the
/// generator had built could not be climbed: a body's head was in the ceiling slab by the second
/// step, because the slabs were laid whole across the stairwell and every storey's flight stood
/// directly over the one below. There was no flight from the top storey to the roof at all.
/// </summary>
public class StairsTests : IClassFixture<StairsTests.City>
{
    private readonly City _city;
    private readonly ITestOutputHelper _o;

    public StairsTests(City city, ITestOutputHelper o) { _city = city; _o = o; }

    /// <summary>The city loaded once: the server's world for the movement engine, and the map file
    /// for what the generator wrote.</summary>
    public sealed class City
    {
        public readonly World World;
        public readonly SpatialGrid<Entity> Grid;
        public readonly Vector3 Size;
        public readonly MapData Data;
        public readonly List<Marker> Markers = new();
        public readonly List<(string Name, int Id, Vector3 Min, Vector3 Max)> Regions = new();
        /// <summary>The named parts of rooms: each flight and each landing (prefabs/named_place.json).</summary>
        public readonly List<(string Name, int Id, Vector3 Min, Vector3 Max)> Places = new();
        public readonly List<(string Prefab, string Name, Vector3 Min, Vector3 Max)> Boxes = new();
        public readonly List<string> Towers = new();

        public City()
        {
            AcousticRegistry.Initialize();
            var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
            var maps = new MapManager(new MapRepository(Path.Combine(AppContext.BaseDirectory, "maps")), prefabs);
            maps.Initialize();
            Assert.True(maps.TryGetMap("city", out World world, out Vector3 mapSize, out var grid, out _));
            Assert.True(maps.TryGetMapData("city", out var data));
            World = world;
            Grid = grid;
            Size = mapSize;
            Data = data;

            var sizes = new Dictionary<string, Vector3>();
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "prefabs"), "*.json"))
            {
                using var p = JsonDocument.Parse(File.ReadAllText(file));
                if (p.RootElement.TryGetProperty("Id", out var id) && p.RootElement.TryGetProperty("ColliderSize", out var cs))
                    sizes[id.GetString()!] = Vec(cs);
            }
            var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "city.json")));
            foreach (var e in doc.RootElement.GetProperty("Entities").EnumerateArray())
            {
                string prefab = e.GetProperty("PrefabId").GetString()!;
                string name = e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                var at = Vec(e.GetProperty("Position"));
                if (prefab == "stair_marker")
                {
                    var rot = e.TryGetProperty("Rotation", out var r)
                        ? new Quaternion(r.GetProperty("X").GetSingle(), r.GetProperty("Y").GetSingle(), r.GetProperty("Z").GetSingle(), r.GetProperty("W").GetSingle())
                        : Quaternion.Identity;
                    var f = Vector3.Transform(Vector3.UnitZ, rot);
                    Markers.Add(new Marker(name, at, Vector3.Normalize(new Vector3(f.X, 0, f.Z))));
                    continue;
                }
                if (!sizes.TryGetValue(prefab, out var size)) continue;
                var scale = e.TryGetProperty("Scale", out var s) ? Vec(s) : Vector3.One;
                var half = size * scale / 2;
                if (prefab == NamedPlaces.PrefabId)
                {
                    Places.Add((name, e.GetProperty("EntityId").GetInt32(), at - half, at + half));
                    continue;
                }
                if (prefab == "acoustic_region")
                {
                    Regions.Add((name, e.GetProperty("EntityId").GetInt32(), at - half, at + half));
                    if (name.EndsWith(" roof access")) Towers.Add(name[..^" roof access".Length]);
                }
                else Boxes.Add((prefab, name, at - half, at + half));
            }
        }

        private static Vector3 Vec(JsonElement v) => new(v.GetProperty("X").GetSingle(), v.GetProperty("Y").GetSingle(), v.GetProperty("Z").GetSingle());

        /// <summary>The name you are told at a point, as the client picks it: the smallest named part
        /// of a room holding it, or else the smallest region.</summary>
        public string ZoneAt(Vector3 p) => Smallest(Places, p) ?? RoomAt(p);

        /// <summary>The room a point is in for sound: the smallest region, named parts left out.</summary>
        public string RoomAt(Vector3 p) => Smallest(Regions, p) ?? "";

        private static string? Smallest(List<(string Name, int Id, Vector3 Min, Vector3 Max)> boxes, Vector3 p) => boxes
            .Where(r => p.X >= r.Min.X && p.Y >= r.Min.Y && p.Z >= r.Min.Z && p.X <= r.Max.X && p.Y <= r.Max.Y && p.Z <= r.Max.Z)
            .OrderBy(r => (r.Max.X - r.Min.X) * (r.Max.Y - r.Min.Y) * (r.Max.Z - r.Min.Z))
            .Select(r => r.Name).FirstOrDefault();

        /// <summary>A tower's stair markers: the ones in its stairwell, under its roof access.</summary>
        public List<Marker> MarkersOf(string tower)
        {
            var access = Regions.First(r => r.Name == tower + " roof access");
            var c = (access.Min + access.Max) / 2;
            return Markers.Where(m => Vector2.Distance(new(m.At.X, m.At.Z), new(c.X, c.Z)) < 12f).ToList();
        }

        /// <summary>
        /// A tower's flights in order from the ground floor, by following the markers: from the foot of
        /// one flight to the top facing back down it, then across to the foot of the next on that floor.
        /// </summary>
        public List<(Marker Foot, Marker Top)> Flights(string tower)
        {
            var ms = MarkersOf(tower);
            var flights = new List<(Marker, Marker)>();
            var foot = ms.Where(m => m.Name.StartsWith("Stairs up")).OrderBy(m => m.At.Y).First();
            while (true)
            {
                var top = ms.Where(m => m.Name.StartsWith("Stairs down") && Vector3.Dot(m.Along, foot.Along) < -0.9f
                                        && m.At.Y > foot.At.Y + 2f && m.At.Y < foot.At.Y + 4f)
                            .OrderBy(m => Vector2.Distance(new(m.At.X, m.At.Z), new(foot.At.X, foot.At.Z))).First();
                flights.Add((foot, top));
                if (foot.Name.EndsWith("to the roof")) return flights;
                foot = ms.Where(m => m.Name.StartsWith("Stairs up") && MathF.Abs(m.At.Y - top.At.Y) < 0.1f)
                         .OrderBy(m => Vector2.Distance(new(m.At.X, m.At.Z), new(top.At.X, top.At.Z))).First();
                Assert.True(flights.Count < 20, "no roof flight");
            }
        }
    }

    public readonly record struct Marker(string Name, Vector3 At, Vector3 Along)
    {
        public Vector3 Floor => At - new Vector3(0, StairCues.MarkerHeightMetres, 0);
    }

    private sealed record Walked(bool Reached, int Airborne, int Landings, Vector3 End, int Up, int Down, int Level);

    /// <summary>
    /// The server's own tick, as MovementSystem runs it: the ground probe, the solid things within
    /// reach, one step of the movement engine, straight at each point in turn as W does.
    /// </summary>
    private Walked Walk(Vector3 from, IReadOnlyList<Vector3> route, float seconds = 60f)
    {
        var world = _city.World;
        var grid = _city.Grid;
        float dt = PhysicsConstants.FixedDeltaTime;
        var pos = from;
        var vel = Vector3.Zero;
        var stride = new StrideAccumulator();
        var near = new List<Entity>();
        var seen = new HashSet<Entity>();
        var cols = new List<SharedMovementEngine.Collider>();
        int leg = 0, airborne = 0, landings = 0, up = 0, down = 0, level = 0, still = 0;
        // ...and half a second standing at the end: the ground under the last tick's position is
        // probed on the tick after it, as the server does.
        for (int i = 0; i < seconds / dt && still < 15; i++)
        {
            var toward = Vector3.Zero;
            if (leg < route.Count)
            {
                toward = route[leg] - pos; toward.Y = 0;
                if (toward.Length() <= PhysicsConstants.WalkSpeed * dt * 0.6f) { leg++; i--; continue; }
                toward = Vector3.Normalize(toward);
            }
            else still++;
            float ground = PhysicsUtils.GetGroundHeight(world, grid, pos, out _);
            near.Clear(); seen.Clear(); cols.Clear();
            grid.CollectInRadius(pos, PhysicsConstants.CollisionSearchRadius, near, seen);
            foreach (var e in near)
            {
                if (!world.IsAlive(e) || !world.Has<ColliderComponent>(e) || !world.Has<Transform>(e)) continue;
                var c = world.Get<ColliderComponent>(e);
                if (!c.IsSolid) continue;
                var t = world.Get<Transform>(e);
                cols.Add(new SharedMovementEngine.Collider { Position = t.Position, Size = c.Size, Rotation = t.Rotation, Material = "Concrete" });
            }
            var r = SharedMovementEngine.Step(new SharedMovementEngine.MovementContext
            {
                Position = pos, Velocity = vel, InputDirection = toward, DeltaTime = dt,
                GroundHeight = ground, Gravity = PhysicsConstants.Gravity, JumpForce = PhysicsConstants.JumpPower,
                Speed = PhysicsConstants.WalkSpeed, PlayerRadius = PhysicsConstants.PlayerRadius,
                PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
                MapMin = new Vector3(-2000, -100, -2000), MapMax = new Vector3(2000, 200, 2000),
            }, CollectionsMarshal.AsSpan(cols));
            pos = r.NewPosition; vel = r.NewVelocity;
            if (!r.IsGrounded) airborne++;
            var fall = stride.Update(pos, vel, r.IsGrounded, Quaternion.Identity);
            if (fall.Landed) landings++;
            if (fall.Stepped)
            {
                if (fall.Slope == StepSlope.Up) up++;
                else if (fall.Slope == StepSlope.Down) down++;
                else level++;
            }
        }
        return new Walked(leg >= route.Count, airborne, landings, pos, up, down, level);
    }

    /// <summary>Opens the doors on a tower's roof, as a player does with E: the walk is about the stairs.</summary>
    private void OpenRoofDoor(Marker roofTop)
    {
        var doors = new List<Entity>();
        _city.World.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t) =>
        {
            if (MathF.Abs(t.Position.Y - roofTop.Floor.Y) < 2f
                && Vector2.Distance(new(t.Position.X, t.Position.Z), new(roofTop.At.X, roofTop.At.Z)) < 4f) doors.Add(e);
        });
        Assert.Single(doors);
        Assert.True(DoorSystem.Set(_city.World, doors[0], open: true));
        var system = new DoorSystem();
        for (int i = 0; i < 90; i++) system.Update(_city.World, 1f / 30f, _ => { });
    }

    // ── The map ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every block of flats has a way onto its roof: a flight from the top storey, a housing
    /// over it with a steel door out, and the roof a named place in the open air.</summary>
    [Fact]
    public void EveryBlockOfFlatsHasAFlightToItsRoofAndADoorOut()
    {
        Assert.Equal(5, _city.Towers.Count);
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "city.json")));
        var steelDoors = doc.RootElement.GetProperty("Entities").EnumerateArray()
            .Where(e => e.GetProperty("PrefabId").GetString() == "steel_door").ToList();
        foreach (var tower in _city.Towers)
        {
            var flights = _city.Flights(tower);
            int storeys = flights.Count;
            _o.WriteLine($"{tower}: {storeys} flights, roof at {flights[^1].Top.Floor.Y:F2} m");
            Assert.EndsWith("to the roof", flights[^1].Foot.Name);
            Assert.Equal($"Stairs down, {flights[^1].Foot.Name.Split(' ')[2]} steps, to floor {storeys - 1}", flights[^1].Top.Name);
            Assert.Equal(storeys * 2, _city.MarkersOf(tower).Count);

            int access = _city.Regions.Single(r => r.Name == tower + " roof access").Id;
            var roofIds = _city.Regions.Where(r => r.Name == tower + " roof").Select(r => r.Id).ToHashSet();
            Assert.NotEmpty(roofIds);
            Assert.Contains(steelDoors, d => d.GetProperty("RegionAId").GetInt32() == access
                                             && roofIds.Contains(d.GetProperty("RegionBId").GetInt32()));
        }
    }

    /// <summary>
    /// The roof is the open air and its stair housing a room. The housing is measured from its walls
    /// (regions-measure-themselves); the roof is said to be outdoors by the map, because a strip of it
    /// between the parapet and the housing measured as a room.
    /// </summary>
    [Fact]
    public void TheRoofIsOutdoorsAndItsHousingIsARoom()
    {
        var defs = EntityDefinitionFactory.StaticDefinitions(_city.World);
        var map = OpenFPS.Common.Systems.AcousticVolumeGenerator.GenerateRegions(defs, _city.Size, _city.Data.MinBound,
                                                                                  _city.Data.VoxelResolution, _city.Data.OcclusionFloor);
        foreach (var tower in _city.Towers)
        {
            var roof = map.Regions.Values.Where(r => r.FriendlyName == tower + " roof").ToList();
            var access = map.Regions.Values.Where(r => r.FriendlyName == tower + " roof access").ToList();
            Assert.NotEmpty(roof);
            Assert.Single(access);
            Assert.All(roof, r => Assert.False(r.IsIndoor, $"{tower} roof measured as indoors"));
            Assert.True(access[0].IsIndoor, $"{tower} roof access measured as outdoors");
        }
    }

    /// <summary>
    /// Each end of every flight says where it goes, and it stands where it says: the foot of the flight
    /// up to floor N is in the stairwell of the floor below it, the top of it in floor N's, and the top
    /// of the flight to the roof in the roof's stair housing. Up at the foot, down at the top.
    /// </summary>
    [Fact]
    public void EveryMarkerNamesTheFloorItLeadsTo()
    {
        foreach (var tower in _city.Towers)
        {
            var flights = _city.Flights(tower);
            for (int s = 0; s < flights.Count; s++)
            {
                var (foot, top) = flights[s];
                string to = s + 1 == flights.Count ? "the roof" : $"floor {s + 1}";
                Assert.Matches($"^Stairs up, \\d+ steps, to {to}$", foot.Name);
                Assert.Matches($"^Stairs down, \\d+ steps, to floor {s}$", top.Name);
                Assert.Equal($"{tower} landing, floor {s}", _city.ZoneAt(foot.Floor + new Vector3(0, 1.7f, 0)));
                Assert.Equal(s + 1 == flights.Count ? $"{tower} roof access" : $"{tower} landing, floor {s + 1}",
                             _city.ZoneAt(top.Floor + new Vector3(0, 1.7f, 0)));
                // ...and for sound, still the stairwell: a landing is a name, not a room.
                Assert.Equal($"{tower} stairwell, floor {s}", _city.RoomAt(foot.Floor + new Vector3(0, 1.7f, 0)));
                // A foot faces up its flight and the top faces down it, along the same line.
                Assert.True(Vector3.Dot(foot.Along, top.Along) < -0.99f);
                var run = top.At - foot.At; run.Y = 0;
                Assert.True(Vector3.Dot(Vector3.Normalize(run), foot.Along) > 0.99f, $"{foot.Name} does not face its top");
            }
        }
    }

    /// <summary>
    /// A parapet a metre and ten high stands on all four edges of every roof: from anywhere along an
    /// edge, a little inside it, something solid fills knee to waist height.
    /// </summary>
    [Fact]
    public void EveryRoofHasAParapetAllRound()
    {
        foreach (var tower in _city.Towers)
        {
            var slab = _city.Boxes.Where(b => b.Name == tower + " roof").ToList();
            Assert.NotEmpty(slab);
            var min = slab.Aggregate(new Vector3(float.MaxValue), (m, b) => Vector3.Min(m, b.Min));
            var max = slab.Aggregate(new Vector3(float.MinValue), (m, b) => Vector3.Max(m, b.Max));
            float roof = max.Y;
            bool Solid(float x, float z) => _city.Boxes.Any(b => b.Prefab != "acoustic_region"
                && x >= b.Min.X && x <= b.Max.X && z >= b.Min.Z && z <= b.Max.Z
                && b.Min.Y <= roof + 0.2f && b.Max.Y >= roof + 1.0f);
            for (float x = min.X + 0.5f; x < max.X - 0.5f; x += 0.5f)
            {
                Assert.True(Solid(x, min.Z + 0.1f), $"{tower}: no parapet at {x:F1},{min.Z:F1}");
                Assert.True(Solid(x, max.Z - 0.1f), $"{tower}: no parapet at {x:F1},{max.Z:F1}");
            }
            for (float z = min.Z + 0.5f; z < max.Z - 0.5f; z += 0.5f)
            {
                Assert.True(Solid(min.X + 0.1f, z), $"{tower}: no parapet at {min.X:F1},{z:F1}");
                Assert.True(Solid(max.X - 0.1f, z), $"{tower}: no parapet at {max.X:F1},{z:F1}");
            }
        }
    }

    // ── The movement engine on them ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Up every flight of every block of flats from the ground floor to the roof, out through the door
    /// onto it, and all the way back down: never off the ground for a tick, never landing, every step
    /// of the climb a step up and every step of the descent a step down or on the level.
    /// </summary>
    [Fact]
    public void EveryFlightIsClimbedAndDescendedWithoutLeavingTheGround()
    {
        foreach (var tower in _city.Towers)
        {
            var flights = _city.Flights(tower);
            var roofTop = flights[^1].Top;
            OpenRoofDoor(roofTop);
            var onRoof = roofTop.At - roofTop.Along * 5f;

            var upRoute = new List<Vector3>();
            foreach (var (foot, top) in flights) { upRoute.Add(foot.At); upRoute.Add(top.At); }
            upRoute.Add(onRoof);
            var start = flights[0].Foot.Floor;
            var up = Walk(start, upRoute);
            _o.WriteLine($"{tower} up: {up}");
            Assert.True(up.Reached, $"{tower}: stuck at {up.End} going up");
            Assert.Equal(0, up.Airborne);
            Assert.Equal(0, up.Landings);
            Assert.Equal(roofTop.Floor.Y, up.End.Y, 2);
            Assert.Equal($"{tower} roof", _city.ZoneAt(up.End + new Vector3(0, 1.7f, 0)));
            Assert.True(up.Up >= flights.Count, $"{up.Up} steps up on {flights.Count} flights");
            Assert.Equal(0, up.Down);

            var downRoute = new List<Vector3>(upRoute);
            downRoute.Reverse();
            downRoute.RemoveAt(0);
            downRoute.Add(start);
            var down = Walk(up.End, downRoute);
            _o.WriteLine($"{tower} down: {down}");
            Assert.True(down.Reached, $"{tower}: stuck at {down.End} going down");
            Assert.Equal(0, down.Airborne);
            Assert.Equal(0, down.Landings);
            Assert.Equal(start.Y, down.End.Y, 2);
            Assert.True(down.Down >= flights.Count, $"{down.Down} steps down on {flights.Count} flights");
            Assert.Equal(0, down.Up);
        }
    }

    /// <summary>Walking at the edge of a roof in any direction leaves you on the roof: the parapet
    /// stops you, on the ground, at roof height.</summary>
    [Fact]
    public void TheParapetStopsYouWalkingOffTheRoof()
    {
        foreach (var tower in _city.Towers)
        {
            var roofTop = _city.Flights(tower)[^1].Top;
            var slab = _city.Boxes.Where(b => b.Name == tower + " roof").ToList();
            var min = slab.Aggregate(new Vector3(float.MaxValue), (m, b) => Vector3.Min(m, b.Min));
            var max = slab.Aggregate(new Vector3(float.MinValue), (m, b) => Vector3.Max(m, b.Max));
            var centre = (min + max) / 2;
            // Out on the open roof, clear of the housing and the plant in the middle.
            var across = new Vector3(centre.X - roofTop.At.X, 0, 0);
            var from = new Vector3(roofTop.At.X + MathF.Sign(across.X) * 5f, roofTop.Floor.Y, roofTop.At.Z - roofTop.Along.Z * 6f);
            foreach (var dir in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
            {
                var w = Walk(from, new[] { from + dir * 200f }, seconds: 15f);
                Assert.False(w.Reached);
                Assert.Equal(0, w.Airborne);
                Assert.Equal(roofTop.Floor.Y, w.End.Y, 2);
                Assert.InRange(w.End.X, min.X, max.X);
                Assert.InRange(w.End.Z, min.Z, max.Z);
            }
        }
    }

    // ── What the client makes of them ────────────────────────────────────────────────────────────

    private static EntitySnapshot MarkerSnap(int id, string name, Vector3 at, Vector3 along) => new()
    {
        Id = id,
        Definition = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Collider = new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.4f), IsSolid = false },
            Identity = new IdentityComponent { Name = name, BeaconCategory = Beacons.Stairs },
        },
        Transform = new Transform
        {
            Position = at, Scale = Vector3.One,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(along.X, along.Z)),
        },
    };

    private static WorldSnapshot Markers(params EntitySnapshot[] markers)
    {
        var w = new WorldSnapshot();
        foreach (var m in markers)
        {
            w.Entities[m.Id] = m;
            w.MarkerEntityIds.Add(m.Id);
        }
        return w;
    }

    /// <summary>The city's flight: seventeen 3/17 m risers on 28 cm treads, its end risers half a metre
    /// in from the markers.</summary>
    private const int Risers = 17;
    private const float Going = 0.28f, Rise = 3f / Risers, MarkerBack = 0.5f, Run = Risers * Going;

    /// <summary>Where the feet stand on a flight whose foot marker is at along 0 on floor 0, rising
    /// along +along: on the floor, on a tread, or on the floor at the top.</summary>
    private static float TreadHeight(float along)
    {
        if (along <= MarkerBack) return 0f;
        int tread = Math.Min(Risers, (int)MathF.Floor((along - MarkerBack) / Going) + 1);
        return tread * Rise;
    }

    /// <summary>One flight: foot on the floor at y 0 facing +z, top three metres up facing back down
    /// it, as the generator places them.</summary>
    private static WorldSnapshot OneFlight() => Markers(
        MarkerSnap(1, "Stairs up, 17 steps, to floor 3", new Vector3(0, 1f, 0), Vector3.UnitZ),
        MarkerSnap(2, "Stairs down, 17 steps, to floor 2", new Vector3(0, 4f, Run + 2 * MarkerBack), -Vector3.UnitZ));

    /// <summary>
    /// A dog-leg of two flights, as the generator builds a stairwell: up the first along +z in the lane
    /// at x 0, turn, and up the second back along -z in the lane at x -2.4, across the well. On the
    /// landing between them the top of the first and the foot of the second stand side by side,
    /// facing the same way.
    /// </summary>
    private static WorldSnapshot DogLeg()
    {
        float far = Run + 2 * MarkerBack;
        return Markers(
            MarkerSnap(1, "Stairs up, 17 steps, to floor 1", new Vector3(0, 1f, 0), Vector3.UnitZ),
            MarkerSnap(2, "Stairs down, 17 steps, to floor 0", new Vector3(0, 4f, far), -Vector3.UnitZ),
            MarkerSnap(3, "Stairs up, 17 steps, to the roof", new Vector3(-2.4f, 4f, far), -Vector3.UnitZ),
            MarkerSnap(4, "Stairs down, 17 steps, to floor 1", new Vector3(-2.4f, 7f, 0), Vector3.UnitZ));
    }

    private static Quaternion Facing(Vector3 along) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(along.X, along.Z));

    /// <summary>
    /// Reaching the foot facing up says it once, and staying on that landing says nothing more:
    /// standing, turning, stepping back, stepping aside, walking away across the floor and coming back.
    /// Leaving for another zone and coming back is a new arrival; where there are no zones, so is
    /// going right away across the floor.
    /// </summary>
    [Fact]
    public void AFlightIsSaidOnceForAsLongAsYouStayOnItsLanding()
    {
        const string line = "Stairs up, 17 steps, to floor 3";
        const int stairwell = 7, corridor = 8;
        var world = OneFlight();
        var cues = new StairCues();
        var up = Facing(Vector3.UnitZ);

        Assert.Null(cues.Update(world, new Vector3(0, 0, -3f), up, stairwell));        // too far
        Assert.Equal(line, cues.Update(world, new Vector3(0, 0, -0.6f), up, stairwell));
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.4f), up, stairwell));      // still there
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.4f), Facing(-Vector3.UnitZ), stairwell));
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.4f), up, stairwell));      // turned back
        Assert.Null(cues.Update(world, new Vector3(0, 0, -2.5f), up, stairwell));      // stepped back...
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.6f), up, stairwell));      // ...and forward again
        Assert.Null(cues.Update(world, new Vector3(0.7f, 0, -0.5f), up, stairwell));   // aside
        Assert.Null(cues.Update(world, new Vector3(6f, 0, -4f), up, stairwell));       // across the landing...
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.6f), up, stairwell));      // ...and back

        // Out through the door into the corridor, and back in: a new arrival.
        Assert.Null(cues.Update(world, new Vector3(0, 0, -7f), up, corridor));
        Assert.Equal(line, cues.Update(world, new Vector3(0, 0, -0.6f), up, stairwell));

        // With no zones, nine metres away and back is still the landing; eleven is not.
        var open = new StairCues();
        Assert.Equal(line, open.Update(world, new Vector3(0, 0, -0.6f), up));
        Assert.Null(open.Update(world, new Vector3(0, 0, -9f), up));
        Assert.Null(open.Update(world, new Vector3(0, 0, -0.6f), up));
        Assert.Null(open.Update(world, new Vector3(0, 0, -11f), up));
        Assert.Equal(line, open.Update(world, new Vector3(0, 0, -0.6f), up));
    }

    /// <summary>Facing away from a flight, or along the landing past it, is not facing up it.</summary>
    [Fact]
    public void FacingAwayFromAFlightSaysNothing()
    {
        var world = OneFlight();
        var cues = new StairCues();
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.5f), Facing(-Vector3.UnitZ)));
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.5f), Facing(Vector3.UnitX)));
        // ...and turning to face up it, still there, says it.
        Assert.Equal("Stairs up, 17 steps, to floor 3", cues.Update(world, new Vector3(0, 0, -0.5f), Facing(Vector3.UnitZ)));
    }

    /// <summary>
    /// Stepping off a flight says nothing about that flight, at either end, whichever way you face:
    /// up it and turning round at the top, or down it walking backwards — facing up it, as Cody often
    /// walks — and arriving at the foot. The feet are on the flight between the two ends and the two
    /// floors, and only there. Somebody arriving at the top who did not come up it is told.
    /// </summary>
    [Fact]
    public void ArrivingOffAFlightSaysNothingAboutIt()
    {
        var world = OneFlight();
        var cues = new StairCues();
        var up = Facing(Vector3.UnitZ);
        var down = Facing(-Vector3.UnitZ);
        float top = Run + 2 * MarkerBack;

        Assert.Equal("Stairs up, 17 steps, to floor 3", cues.Update(world, new Vector3(0, 0, -0.4f), up));
        Assert.False(cues.OnFlight);
        for (float z = MarkerBack + 0.05f; z < top - MarkerBack; z += 0.14f)
        {
            float y = TreadHeight(z);
            Assert.Null(cues.Update(world, new Vector3(0, y, z), up));
            Assert.Equal(y > 0.1f && y < 2.9f, cues.OnFlight);
        }
        Assert.Null(cues.Update(world, new Vector3(0, 3f, top - 0.3f), up));          // the top, facing on
        Assert.False(cues.OnFlight);
        Assert.Null(cues.Update(world, new Vector3(0, 3f, top), down));               // turned round: you came up it
        Assert.Null(cues.Update(world, new Vector3(0.5f, 3f, top + 0.3f), down));

        // Back down it backwards, still facing up it, and off at the foot.
        for (float z = top - MarkerBack - 0.05f; z > MarkerBack; z -= 0.14f)
            Assert.Null(cues.Update(world, new Vector3(0, TreadHeight(z), z), up));
        Assert.Null(cues.Update(world, new Vector3(0, 0, 0.2f), up));
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.3f), up));
        Assert.False(cues.OnFlight);

        // Somebody who walked onto the top landing from the floor there is told where it goes.
        Assert.Equal("Stairs down, 17 steps, to floor 2", new StairCues().Update(world, new Vector3(0, 3f, top + 0.2f), down));
    }

    /// <summary>
    /// The cue speaks for the stairs it has just told you about, and their zones are said only when it
    /// did not. Up to a flight facing it: the cue is said and stepping onto the flight is a flight it
    /// announced, so the flight's name is not said after it; nor is a landing entered a moment before
    /// the cue or while it was being said. Off the top and later back down it backwards, with no cue,
    /// the flight's name is news. A room is not a named part of one, and is always said (the zone
    /// announcer asks this only of a flight or a landing).
    /// </summary>
    [Fact]
    public void TheCueSpeaksForAFlightAndItsLandingAndTheZoneForTheRest()
    {
        var world = OneFlight();
        var cues = new StairCues();
        var up = Facing(Vector3.UnitZ);
        float top = Run + 2 * MarkerBack;

        Assert.Equal("Stairs up, 17 steps, to floor 3", cues.Update(world, new Vector3(0, 0, -0.4f), up));
        Assert.False(cues.FlightAnnounced);
        cues.Update(world, new Vector3(0, TreadHeight(1.0f), 1.0f), up);
        Assert.True(cues.OnFlight);
        Assert.True(cues.FlightAnnounced);                                             // its cue was said
        Assert.True(StairCues.CoversZone(cues.FlightAnnounced, double.NegativeInfinity, 100.0));
        cues.Update(world, new Vector3(0, 3f, top), up);                               // off at the top
        Assert.False(cues.FlightAnnounced);

        // Back down it walking backwards, facing up it: no cue, so the flight's name is said.
        Assert.Null(cues.Update(world, new Vector3(0, TreadHeight(top - 1.0f), top - 1.0f), up));
        Assert.True(cues.OnFlight);
        Assert.False(cues.FlightAnnounced);
        Assert.False(StairCues.CoversZone(cues.FlightAnnounced, double.NegativeInfinity, 100.0));

        // A landing: entered at 100 s. A cue at 99.5 s or after covers it; one at 98 s is another visit.
        Assert.True(StairCues.CoversZone(false, 99.5, 100.0));
        Assert.True(StairCues.CoversZone(false, 100.3, 100.0));
        Assert.False(StairCues.CoversZone(false, 98.0, 100.0));
    }

    /// <summary>
    /// Where two flights meet, each is said once however you shuffle between them: up the first, turn,
    /// and the flight you came up is not news but the next one is, once, however many times you step
    /// across the well and back. Up the second and back down it — you have been on another floor — and
    /// the first one's top is news again, once.
    /// </summary>
    [Fact]
    public void ALandingWhereTwoFlightsMeetSaysEachOnceHoweverYouShuffle()
    {
        const int place = 5;
        var world = DogLeg();
        var cues = new StairCues();
        var plusZ = Facing(Vector3.UnitZ);
        var minusZ = Facing(-Vector3.UnitZ);
        float far = Run + 2 * MarkerBack;
        var said = new List<string>();
        void Step(Vector3 feet, Quaternion facing)
        {
            if (cues.Update(world, feet, facing, place) is { } line) said.Add(line);
        }
        void Shuffle(float y, int times)
        {
            for (int i = 0; i < times; i++)
            {
                for (float x = 0.3f; x >= -2.7f; x -= 0.1f) Step(new Vector3(x, y, far), minusZ);
                for (float x = -2.7f; x <= 0.3f; x += 0.1f) Step(new Vector3(x, y, far - 0.3f), minusZ);
            }
        }

        Step(new Vector3(0, 0, -0.6f), plusZ);
        for (float z = MarkerBack + 0.05f; z < far - MarkerBack; z += 0.14f) Step(new Vector3(0, TreadHeight(z), z), plusZ);
        Step(new Vector3(0, 3f, far - 0.2f), plusZ);
        Step(new Vector3(0, 3f, far), minusZ);                                         // turned round at the top
        Shuffle(3f, 6);
        Assert.Equal(new[] { "Stairs up, 17 steps, to floor 1", "Stairs up, 17 steps, to the roof" }, said);

        // Up the second flight, which climbs along -z from far, and back down it walking backwards.
        said.Clear();
        for (float z = far - MarkerBack - 0.05f; z > MarkerBack; z -= 0.14f)
            Step(new Vector3(-2.4f, 3f + TreadHeight(far - z), z), minusZ);
        Step(new Vector3(-2.4f, 6f, 0.2f), minusZ);
        for (float z = MarkerBack + 0.05f; z < far - MarkerBack; z += 0.14f)
            Step(new Vector3(-2.4f, 3f + TreadHeight(far - z), z), minusZ);
        Shuffle(3f, 6);
        Assert.Equal(new[] { "Stairs down, 17 steps, to floor 0" }, said);
    }

    /// <summary>
    /// Cody's walk on Marlow Tower's floor 2 landing (2026-10-04, 04:53:27 to 04:54:02), replayed on
    /// the stairwell as it was then: two markers 1.8 m apart facing the same way, and him walking up
    /// to the flight, seven metres back across the landing and back, five metres along it and back,
    /// and across between the two lanes. The old cues said "Stairs up, 10 steps, to floor 3" four
    /// times and "Stairs down" once in those thirty-five seconds; each is said once.
    /// </summary>
    [Fact]
    public void CodysWalkOnTheLandingSaysEachFlightOnce()
    {
        var world = Markers(
            MarkerSnap(1, "Stairs up, 10 steps, to floor 3", new Vector3(-12.45f, 7.28f, -106.45f), -Vector3.UnitZ),
            MarkerSnap(2, "Stairs down, 10 steps, to floor 2", new Vector3(-12.45f, 10.28f, -110.45f), Vector3.UnitZ),
            MarkerSnap(3, "Stairs down, 10 steps, to floor 1", new Vector3(-10.65f, 7.28f, -106.45f), -Vector3.UnitZ),
            MarkerSnap(4, "Stairs up, 10 steps, to floor 2", new Vector3(-10.65f, 4.28f, -110.45f), Vector3.UnitZ));
        // Where his feet went down, from the client log's footsteps, in order.
        var path = new (float X, float Z)[]
        {
            (-10.15f, -102.32f), (-11.5f, -102.32f), (-11.65f, -102.47f), (-11.95f, -103.97f), (-11.65f, -105.17f),
            (-11.8f, -105.77f), (-11.95f, -106.37f), (-11.65f, -106.4f), (-11.95f, -105.35f), (-11.65f, -104.3f),
            (-11.95f, -103.25f), (-11.65f, -102.2f), (-11.95f, -101.15f), (-11.65f, -100.1f), (-11.8f, -99.3f),
            (-11.95f, -99.5f), (-11.65f, -100.1f), (-11.95f, -100.7f), (-11.65f, -101.45f), (-11.95f, -102.2f),
            (-11.65f, -102.95f), (-11.95f, -104f), (-11.65f, -104.9f), (-11.95f, -105.8f), (-11.8f, -106.55f),
            (-12.85f, -106.55f), (-13.45f, -106.55f), (-14.65f, -106.55f), (-15.4f, -106.55f), (-16.6f, -106.55f),
            (-17.5f, -106.5f), (-17.2f, -106.55f), (-16f, -106.55f), (-14.8f, -106.55f), (-13.6f, -106.55f),
            (-13.3f, -106.55f), (-12.25f, -106.55f), (-11.65f, -106.55f), (-11.5f, -106.55f), (-10.9f, -106.55f),
            (-11.5f, -106.55f), (-11.95f, -106.55f), (-12.4f, -106.55f), (-13.3f, -106.55f),
        };
        var cues = new StairCues();
        var facing = Facing(-Vector3.UnitZ);
        var said = new List<string>();
        for (int i = 1; i < path.Length; i++)
        {
            var a = new Vector2(path[i - 1].X, path[i - 1].Z);
            var b = new Vector2(path[i].X, path[i].Z);
            int steps = Math.Max(1, (int)(Vector2.Distance(a, b) / 0.05f));
            for (int k = 1; k <= steps; k++)
            {
                var p = Vector2.Lerp(a, b, k / (float)steps);
                if (cues.Update(world, new Vector3(p.X, 6.28f, p.Y), facing, 338) is { } line) said.Add(line);
            }
        }
        _o.WriteLine(string.Join(" / ", said));
        Assert.Equal(1, said.Count(l => l.StartsWith("Stairs up")));
        Assert.Equal(1, said.Count(l => l.StartsWith("Stairs down")));
    }

    /// <summary>A marker the client is sent lands in the snapshot's marker list, which is where the
    /// beacons and the stair cues look; a solid thing or one that is not a beacon does not.</summary>
    [Fact]
    public void AStairMarkerIsAMarkerAndAWallIsNot()
    {
        var marker = MarkerSnap(7, "Stairs up, 10 steps, to floor 1", Vector3.Zero, Vector3.UnitZ).Definition;
        Assert.True(ClientWorldState.IsMarker(marker));
        var wall = MarkerSnap(8, "", Vector3.Zero, Vector3.UnitZ).Definition;
        wall.Identity.BeaconCategory = "";
        Assert.False(ClientWorldState.IsMarker(wall));
        var solid = MarkerSnap(9, "x", Vector3.Zero, Vector3.UnitZ).Definition;
        solid.Collider.IsSolid = true;
        Assert.False(ClientWorldState.IsMarker(solid));
        // A named place is kept with them, for the zone lookup; it is not a beacon or a stair end.
        var place = MarkerSnap(10, "Marlow Tower landing, floor 2", Vector3.Zero, Vector3.UnitZ).Definition;
        place.Identity.BeaconCategory = "";
        place.Identity.PrefabId = NamedPlaces.PrefabId;
        place.Collider.Size = new Vector3(4f, 2.5f, 2f);
        Assert.True(NamedPlaces.Is(place));
        Assert.True(ClientWorldState.IsMarker(place));
    }

    /// <summary>
    /// The stairs beacon blips once a floor, from where you step onto the stairs to go up from it, and
    /// on the roof from the top of the flight down: "how will you find the levels in between" with a
    /// beacon only at the bottom and the top (Cody, 2026-10-04). On the landing where two flights meet
    /// only the one going up blips, and only your own floor's: the floors above and below are open to
    /// the stairwell and a few metres off. /beacons stairs off and a map that forbids it both silence it.
    /// </summary>
    [Fact]
    public void TheStairsBeaconBlipsFromYourFloorsWayUp()
    {
        var world = DogLeg();
        Assert.Equal(new[] { 1, 3, 4 }, StairCues.FloorBeacons(world).OrderBy(i => i));
        var wayUp = world.Entities[3].Transform.Position;                             // foot of the flight to the roof
        var ear = new Vector3(-1.2f, 4.6f, Run + 2 * MarkerBack);                    // on the landing between

        List<SpatialEmitter> Blips(BeaconPreferences prefs, IEnumerable<string>? policy = null)
        {
            var mixer = new EmitterRecordingProvider();
            var audio = new AudioEngineFacade(mixer);
            audio.InitializeForTest();
            var aids = new BeaconAids(audio, prefs);
            aids.SetMapPolicy(policy);
            for (int i = 0; i < 40; i++)
            {
                audio.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
                aids.Update(world, ear, 10.0 + i * 0.1);
                for (int k = 0; k < 3; k++) audio.PumpForTest();
            }
            return mixer.Played.Where(e => e.SoundId == "SYNTH/beacon_stairs_steps").ToList();
        }

        var blips = Blips(BeaconPreferences.InMemory());
        Assert.NotEmpty(blips);
        Assert.All(blips, e => Assert.True(Vector3.Distance(e.Position, wayUp) < 1e-3f, $"a stairs blip from {e.Position}"));

        var off = BeaconPreferences.InMemory();
        off.Set(Beacons.Stairs, false);
        Assert.Empty(Blips(off));
        Assert.Empty(Blips(BeaconPreferences.InMemory(), new[] { "stairs=forbidden" }));
    }

    /// <summary>Every tower has one stairs beacon a floor: the foot of each floor's flight up, the ground
    /// floor's included, and the top of the flight onto the roof. Never the top of a flight on a floor
    /// that has a flight up beside it.</summary>
    [Fact]
    public void EveryFloorOfEveryTowerHasOneStairsBeacon()
    {
        foreach (var tower in _city.Towers)
        {
            var flights = _city.Flights(tower);
            var ms = _city.MarkersOf(tower);
            var world = Markers(ms.Select((m, i) => MarkerSnap(i + 1, m.Name, m.At, m.Along)).ToArray());
            var beacons = StairCues.FloorBeacons(world).Select(id => world.Entities[id]).ToList();
            _o.WriteLine($"{tower}: {string.Join(" / ", beacons.OrderBy(b => b.Transform.Position.Y).Select(b => b.Definition.Identity.Name))}");
            Assert.Equal(flights.Count + 1, beacons.Count);
            Assert.Equal(flights.Count + 1, beacons.Select(b => MathF.Round(b.Transform.Position.Y, 1)).Distinct().Count());
            foreach (var (foot, _) in flights)
                Assert.Contains(beacons, b => Vector3.Distance(b.Transform.Position, foot.At) < 1e-3f);
            Assert.Contains(beacons, b => Vector3.Distance(b.Transform.Position, flights[^1].Top.At) < 1e-3f);
        }
    }

    /// <summary>
    /// Standing in the doorway from the corridor into the stairwell, on every floor of every tower,
    /// the stairs beacon is heard, and from that floor's way up only: on every other floor that is at
    /// the far end of the shaft, nearly twelve metres off, and the floors above and below are nearer.
    /// </summary>
    [Fact]
    public void FromEveryStairwellDoorYouHearThatFloorsWayUp()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "city.json")));
        var portals = doc.RootElement.GetProperty("Entities").EnumerateArray()
            .Where(e => e.GetProperty("PrefabId").GetString() == "portal")
            .Select(e => (A: e.GetProperty("RegionAId").GetInt32(), At: new Vector3(e.GetProperty("Position").GetProperty("X").GetSingle(),
                          e.GetProperty("Position").GetProperty("Y").GetSingle(), e.GetProperty("Position").GetProperty("Z").GetSingle())))
            .ToList();
        foreach (var tower in _city.Towers)
        {
            var flights = _city.Flights(tower);
            var ms = _city.MarkersOf(tower);
            var world = Markers(ms.Select((m, i) => MarkerSnap(i + 1, m.Name, m.At, m.Along)).ToArray());
            for (int s = 0; s < flights.Count; s++)
            {
                int stairwell = _city.Regions.Single(r => r.Name == $"{tower} stairwell, floor {s}").Id;
                var door = portals.Single(p => p.A == stairwell).At;
                var ear = door with { Y = flights[s].Foot.Floor.Y + 1.6f };
                var wayUp = flights[s].Foot.At;

                var mixer = new EmitterRecordingProvider();
                var audio = new AudioEngineFacade(mixer);
                audio.InitializeForTest();
                var aids = new BeaconAids(audio, BeaconPreferences.InMemory());
                for (int i = 0; i < 40; i++)
                {
                    audio.UpdateListener(ear, Quaternion.Identity, Vector3.Zero, -1);
                    aids.Update(world, ear, 10.0 + i * 0.1);
                    for (int k = 0; k < 3; k++) audio.PumpForTest();
                }
                var blips = mixer.Played.Where(e => e.SoundId == "SYNTH/beacon_stairs_steps").ToList();
                Assert.True(blips.Count > 0, $"{tower} floor {s}: no stairs beacon from the door, {Vector3.Distance(ear, wayUp):F1} m from the way up");
                Assert.All(blips, e => Assert.True(Vector3.Distance(e.Position, wayUp) < 1e-3f, $"{tower} floor {s}: a blip from {e.Position}"));
            }
        }
    }

    /// <summary>
    /// Each flight is a named place of its own, from its first riser to its last, and the floor at each
    /// end of it a landing: "the stairs themselves need a zone, then the landings need a zone" (Cody,
    /// 2026-10-04). Up every flight of every tower at eye height, over the treads as they are built,
    /// the name is the landing, then the flight, then the next landing — and the room you hear is the
    /// stairwell all the way, as it was: a flight and a landing are names, not rooms.
    /// </summary>
    [Fact]
    public void EveryFlightAndEveryLandingIsAZone()
    {
        foreach (var tower in _city.Towers)
        {
            var flights = _city.Flights(tower);
            for (int s = 0; s < flights.Count; s++)
            {
                var (foot, top) = flights[s];
                bool roof = s + 1 == flights.Count;
                string flight = $"{tower} stairs, floor {s} to {(roof ? "the roof" : $"{s + 1}")}";
                string above = roof ? $"{tower} roof access" : $"{tower} landing, floor {s + 1}";
                var treads = _city.Boxes.Where(b => b.Name == flight).ToList();
                Assert.NotEmpty(treads);
                var rooms = new[] { $"{tower} stairwell, floor {s}", roof ? $"{tower} roof access" : $"{tower} stairwell, floor {s + 1}" };

                float length = Vector3.Dot(top.At - foot.At, foot.Along);
                var heard = new List<string>();
                for (float a = 0f; a <= length + 1e-3f; a += 0.05f)
                {
                    var feet = foot.Floor + foot.Along * a;
                    // On the tread under you, or on the floor at either end.
                    var under = treads.Where(b => feet.X >= b.Min.X && feet.X <= b.Max.X && feet.Z >= b.Min.Z && feet.Z <= b.Max.Z).ToList();
                    feet.Y = under.Count > 0 ? under.Max(b => b.Max.Y) : a > length / 2 ? top.Floor.Y : foot.Floor.Y;
                    var eye = feet + new Vector3(0, 1.7f, 0);
                    string zone = _city.ZoneAt(eye);
                    if (heard.Count == 0 || heard[^1] != zone) heard.Add(zone);
                    Assert.Contains(_city.RoomAt(eye), rooms);
                }
                _o.WriteLine($"{tower} flight {s}: {string.Join(" / ", heard)}");
                Assert.Equal(new[] { $"{tower} landing, floor {s}", flight, above }, heard);
            }
        }
    }

    /// <summary>
    /// "Are you sure when it says 19 steps that it's actually 19 actual steps, how is this
    /// measured?" (Cody, 2026-10-04.) Counted from the map: the boxes each flight is built of, one box
    /// a step. Every box's top is one riser over the one before it, the first one riser over the floor
    /// you start from and the last level with the floor you arrive on — so the count is the number of
    /// times you step up, the last of them onto the landing, and it is what both ends of the flight say.
    /// </summary>
    [Fact]
    public void EveryFlightSaysHowManyStepsItIsBuiltOf()
    {
        foreach (var tower in _city.Towers)
        {
            var flights = _city.Flights(tower);
            for (int s = 0; s < flights.Count; s++)
            {
                var (foot, top) = flights[s];
                string flight = $"{tower} stairs, floor {s} to {(s + 1 == flights.Count ? "the roof" : $"{s + 1}")}";
                var boxes = _city.Boxes.Where(b => b.Name == flight).OrderBy(b => b.Max.Y).ToList();
                var tops = boxes.Select(b => MathF.Round(b.Max.Y, 3)).Distinct().ToList();
                _o.WriteLine($"{flight}: {boxes.Count} boxes, {tops.Count} heights, from {foot.Floor.Y:F2} to {top.Floor.Y:F2}; "
                             + $"'{foot.Name}', '{top.Name}'");

                Assert.Equal(boxes.Count, tops.Count);                                  // one box a step
                Assert.Equal(boxes.Count, Steps(foot));
                Assert.Equal(boxes.Count, Steps(top));
                float last = foot.Floor.Y;
                foreach (float y in tops)
                {
                    Assert.InRange(y - last, 0.102f, 0.178f);                            // each a riser
                    last = y;
                }
                Assert.Equal(top.Floor.Y, last, 2);                                      // the last onto the landing
                // ...in a line from the foot to the top, a tread apart.
                var along = boxes.Select(b => Vector3.Dot((b.Min + b.Max) / 2 - foot.At, foot.Along)).ToList();
                for (int k = 1; k < along.Count; k++) Assert.Equal(Going, along[k] - along[k - 1], 2);
            }
        }
    }

    private static int Steps(Marker m) => int.Parse(m.Name.Split(' ')[2].TrimEnd(','));

    /// <summary>
    /// Every flight in every tower is built to the figures a real stair is (International Building
    /// Code 2021, 1011): risers no more than 178 mm, treads at least 279 mm, at least 1.12 m clear
    /// between its guards, and an open well between it and the flight beside it — "the stairs also
    /// seem kind of short ... way too narrow and close to each other" (Cody, 2026-10-04).
    /// </summary>
    [Fact]
    public void EveryFlightIsBuiltToCode()
    {
        bool Solid(Vector3 p) => _city.Boxes.Any(b => b.Prefab != "acoustic_region"
            && p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y && p.Z >= b.Min.Z && p.Z <= b.Max.Z);
        foreach (var tower in _city.Towers)
        {
            var flights = _city.Flights(tower);
            for (int s = 0; s < flights.Count; s++)
            {
                var (foot, top) = flights[s];
                int n = int.Parse(foot.Name.Split(' ')[2].TrimEnd(','));
                var line = top.At - foot.At; line.Y = 0;
                float length = line.Length();
                var side = new Vector3(-foot.Along.Z, 0, foot.Along.X);
                // The treads: the boxes standing on the lane's centre line between the two floors.
                var treads = _city.Boxes.Where(b => b.Prefab == "concrete_floor"
                        && b.Max.Y > foot.Floor.Y + 0.05f && b.Max.Y < top.Floor.Y + 0.05f)
                    .Select(b => (Box: b, Along: Vector3.Dot((b.Min + b.Max) / 2 - foot.At, foot.Along),
                                  Aside: Vector3.Dot((b.Min + b.Max) / 2 - foot.At, side)))
                    .Where(t => t.Along > 0 && t.Along < length && MathF.Abs(t.Aside) < 0.1f)
                    .OrderBy(t => t.Along).ToList();
                Assert.Equal(n, treads.Count);
                float lastTop = foot.Floor.Y;
                foreach (var (box, _, _) in treads)
                {
                    float depth = MathF.Abs(Vector3.Dot(box.Max - box.Min, foot.Along));
                    Assert.True(depth >= 0.279f, $"{foot.Name} in {tower}: a {depth:F3} m tread");
                    Assert.InRange(box.Max.Y - lastTop, 0.102f, 0.178f);
                    lastTop = box.Max.Y;
                }
                Assert.Equal(top.Floor.Y, lastTop, 2);

                // Clear width, half way up, at waist height over the tread: to the first solid thing
                // either side of the centre line.
                var mid = treads[n / 2];
                var at = foot.At + foot.Along * mid.Along;
                at.Y = mid.Box.Max.Y + 0.5f;
                float Clear(float dir)
                {
                    for (float d = 0.02f; d < 3f; d += 0.02f)
                        if (Solid(at + side * dir * d)) return d;
                    return 3f;
                }
                float width = Clear(+1) + Clear(-1);
                Assert.True(width >= 1.118f, $"{foot.Name} in {tower}: {width:F2} m between its sides");

                // The flight beside it, across the well: a guard, then open air, then the other lane.
                if (s + 1 < flights.Count)
                {
                    var next = flights[s + 1].Foot;
                    float apart = MathF.Abs(Vector3.Dot(next.At - top.At, side));
                    Assert.True(apart >= 2.0f, $"{tower}: flights {s} and {s + 1} are {apart:F2} m apart");
                    float toward = MathF.Sign(Vector3.Dot(next.At - top.At, side));
                    float guard = Clear(toward);
                    Assert.True(guard < 0.9f, $"{tower} flight {s}: nothing beside it on the well side at {guard:F2} m");
                    for (float d = guard + 0.15f; d < guard + 0.5f; d += 0.05f)
                        Assert.False(Solid(at + side * toward * d), $"{tower} flight {s}: the well is filled {d:F2} m out");
                }
            }
        }
    }

    // ── Footsteps on them ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A body walking up a flight puts its feet down as steps up, coming down as steps down, and on the
    /// level as neither; and on the flight each footfall lands on the update the body arrives on a tread.
    /// </summary>
    [Fact]
    public void StepsOnAFlightAreUpOrDownAndLandOnTreads()
    {
        const float rise = Rise, going = Going;
        var speed = PhysicsConstants.WalkSpeed;
        float dt = PhysicsConstants.FixedDeltaTime;

        (List<StepSlope> Slopes, int OffTread) Walk(int direction)
        {
            var stride = new StrideAccumulator();
            var slopes = new List<StepSlope>();
            int offTread = 0;
            float lastY = float.NaN;
            for (float z = -1f; z < Run + 2f; z += speed * dt)
            {
                float along = direction > 0 ? z : Run + 1f - z;
                int tread = Math.Clamp((int)MathF.Floor(along / going) + 1, 0, Risers);
                float y = tread * rise;
                var f = stride.Update(new Vector3(0, y, z), new Vector3(0, 0, speed), true, Quaternion.Identity);
                if (f.Stepped)
                {
                    slopes.Add(f.Slope);
                    if (along > 0.4f && along < Run - 0.4f && y == lastY) offTread++;
                }
                lastY = y;
            }
            return (slopes, offTread);
        }

        var (upSlopes, upOff) = Walk(+1);
        var (downSlopes, downOff) = Walk(-1);
        Assert.Contains(StepSlope.Up, upSlopes);
        Assert.DoesNotContain(StepSlope.Down, upSlopes);
        Assert.Contains(StepSlope.Down, downSlopes);
        Assert.DoesNotContain(StepSlope.Up, downSlopes);
        Assert.Equal(0, upOff);
        Assert.Equal(0, downOff);

        var flat = new StrideAccumulator();
        for (float z = 0; z < 6f; z += speed * dt)
        {
            var f = flat.Update(new Vector3(0, 0.12f * MathF.Floor(z / 3f), z), new Vector3(0, 0, speed), true, Quaternion.Identity);
            if (f.Stepped) Assert.Equal(StepSlope.Level, f.Slope);   // a kerb is walked over, not climbed
        }
    }

    /// <summary>
    /// The movement engine takes a step up by lifting the body its whole StepHeight, 40 cm, and the
    /// ground probe settles it onto the tread an update later. A footfall on the lifted update must
    /// not count from there: the landing at the top of a flight of 17.6 cm risers is 22 cm under it,
    /// and the last step up was heard as a heel drop. Every phase of footfall against the lifts: up a
    /// flight is never a step down, and the level beyond it is never a step down either.
    /// </summary>
    [Fact]
    public void TheEnginesLiftOntoATreadIsNotAStepDown()
    {
        var speed = PhysicsConstants.WalkSpeed;
        float dt = PhysicsConstants.FixedDeltaTime;
        for (float phase = 0f; phase < 1.5f; phase += 0.05f)
        {
            var stride = new StrideAccumulator();
            int lastTread = 0;
            float settled = 0f;
            for (float z = -1f - phase; z < Run + 3f; z += speed * dt)
            {
                int tread = Math.Clamp((int)MathF.Floor(z / Going) + 1, 0, Risers);
                // Arriving on a higher tread: lifted 40 cm over the one you were on, for one update.
                float y = tread > lastTread ? settled + PhysicsConstants.StepHeight : tread * Rise;
                settled = tread * Rise;
                lastTread = tread;
                var f = stride.Update(new Vector3(0, y, z), new Vector3(0, 0, speed), true, Quaternion.Identity);
                Assert.False(f.Stepped && f.Slope == StepSlope.Down, $"a step down at {z:F2} m, phase {phase:F2}");
            }
        }
    }

    /// <summary>
    /// Going up is a lighter, higher toe-first step and going down a heavier, lower heel drop, every
    /// time, whatever the take's own wander of a decibel and three per cent.
    /// </summary>
    [Fact]
    public void AStepUpIsLighterAndAStepDownHeavier()
    {
        Assert.True(StrideAccumulator.SlopeDb(StepSlope.Up) < -2f);
        Assert.True(StrideAccumulator.SlopeDb(StepSlope.Down) > 2f);
        Assert.Equal(0f, StrideAccumulator.SlopeDb(StepSlope.Level));
        Assert.True(StrideAccumulator.SlopePitch(StepSlope.Up) > 1f && StrideAccumulator.SlopePitch(StepSlope.Down) < 1f);

        var h = new ClientAudioHarness(Sounds());
        h.StandAt(Vector3.Zero);
        h.Tick();
        float Volume(StepSlope slope, out float pitch)
        {
            int before = h.Mixer.Started.Count;
            h.Audio.OnOwnFootstep(new Vector3(0f, 0f, 0.2f), "Concrete", "0", slope);
            h.Tick(3);
            var step = h.Mixer.Started.Skip(before).First(e => e.FollowsListener && e.EntityId <= -100 && e.EntityId > -200);
            pitch = step.Pitch;
            // The take has played out: its voice is free for the next step to start again.
            h.Mixer.Live.Clear();
            return step.Volume;
        }
        // Each take wanders a decibel either way, so the levels are compared on the average of twenty;
        // the difference is the source's, through the mix's own loudness law (Loudness.Place), which
        // narrows every difference in level between quiet sounds the same way.
        double upDb = 0, levelDb = 0, downDb = 0;
        const int n = 20;
        for (int i = 0; i < n; i++)
        {
            upDb += 20 * Math.Log10(Volume(StepSlope.Up, out float upPitch));
            downDb += 20 * Math.Log10(Volume(StepSlope.Down, out float downPitch));
            levelDb += 20 * Math.Log10(Volume(StepSlope.Level, out _));
            Assert.True(upPitch > downPitch, $"up pitch {upPitch}, down {downPitch}");
        }
        upDb /= n; levelDb /= n; downDb /= n;
        float Rendered(StepSlope s) => 20f * MathF.Log10(Loudness.Place(Loudness.FootstepDb + StrideAccumulator.SlopeDb(s)).Gain
                                                         / Loudness.Place(Loudness.FootstepDb).Gain);
        _o.WriteLine($"up {upDb - levelDb:F2} dB, down {downDb - levelDb:F2} dB against level "
                   + $"(expected {Rendered(StepSlope.Up):F2}, {Rendered(StepSlope.Down):F2})");
        Assert.Equal(Rendered(StepSlope.Up), upDb - levelDb, 0.6);
        Assert.Equal(Rendered(StepSlope.Down), downDb - levelDb, 0.6);
        Assert.True(levelDb - upDb > 1.0 && downDb - levelDb > 1.0);
    }

    private static string Sounds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS"))) dir = dir.Parent;
        return dir != null ? Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS")
                           : "/home/cody/external-rescue/Github/open-fps/OpenFPS.Client/ASSETS/SOUNDS";
    }
}
