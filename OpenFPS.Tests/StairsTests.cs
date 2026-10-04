using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using Xunit;
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
                if (prefab == "acoustic_region")
                {
                    Regions.Add((name, e.GetProperty("EntityId").GetInt32(), at - half, at + half));
                    if (name.EndsWith(" roof access")) Towers.Add(name[..^" roof access".Length]);
                }
                else Boxes.Add((prefab, name, at - half, at + half));
            }
        }

        private static Vector3 Vec(JsonElement v) => new(v.GetProperty("X").GetSingle(), v.GetProperty("Y").GetSingle(), v.GetProperty("Z").GetSingle());

        /// <summary>The named place holding a point: the smallest box, as the client picks.</summary>
        public string ZoneAt(Vector3 p) => Regions
            .Where(r => p.X >= r.Min.X && p.Y >= r.Min.Y && p.Z >= r.Min.Z && p.X <= r.Max.X && p.Y <= r.Max.Y && p.Z <= r.Max.Z)
            .OrderBy(r => (r.Max.X - r.Min.X) * (r.Max.Y - r.Min.Y) * (r.Max.Z - r.Min.Z))
            .Select(r => r.Name).FirstOrDefault() ?? "";

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
    /// The roof measures as the open air and its stair housing as a room, from the walls round them
    /// (regions-measure-themselves): the parapet is a metre high and does not make a roof a courtyard.
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
                Assert.Equal($"{tower} stairwell, floor {s}", _city.ZoneAt(foot.Floor + new Vector3(0, 1.7f, 0)));
                Assert.Equal(s + 1 == flights.Count ? $"{tower} roof access" : $"{tower} stairwell, floor {s + 1}",
                             _city.ZoneAt(top.Floor + new Vector3(0, 1.7f, 0)));
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

    /// <summary>One flight: foot on the floor at y 0 facing +z, top three metres up and 4 m along
    /// facing back down it, as the generator places them.</summary>
    private static WorldSnapshot OneFlight()
    {
        var w = new WorldSnapshot();
        foreach (var m in new[]
        {
            MarkerSnap(1, "Stairs up, 10 steps, to floor 3", new Vector3(0, 1f, 0), Vector3.UnitZ),
            MarkerSnap(2, "Stairs down, 10 steps, to floor 2", new Vector3(0, 4f, 4f), -Vector3.UnitZ),
        })
        {
            w.Entities[m.Id] = m;
            w.MarkerEntityIds.Add(m.Id);
        }
        return w;
    }

    private static Quaternion Facing(Vector3 along) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(along.X, along.Z));

    /// <summary>Reaching the foot facing up says it once; standing, turning and shuffling there says
    /// nothing more; walking away and coming back says it again.</summary>
    [Fact]
    public void TheFootOfAFlightIsSaidOncePerApproach()
    {
        var world = OneFlight();
        var cues = new StairCues();
        var up = Facing(Vector3.UnitZ);

        Assert.Null(cues.Update(world, new Vector3(0, 0, -3f), up));                 // too far
        Assert.Equal("Stairs up, 10 steps, to floor 3", cues.Update(world, new Vector3(0, 0, -0.8f), up));
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.6f), up));               // still there
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.6f), Facing(-Vector3.UnitZ)));
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.6f), up));               // turned back: same visit
        Assert.Null(cues.Update(world, new Vector3(0, 0, -1.3f), up));               // past reach, short of leaving
        Assert.Null(cues.Update(world, new Vector3(0, 0, -0.9f), up));
        Assert.Null(cues.Update(world, new Vector3(0, 0, -2.0f), up));               // left
        Assert.Equal("Stairs up, 10 steps, to floor 3", cues.Update(world, new Vector3(0, 0, -0.9f), up));
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
        Assert.Equal("Stairs up, 10 steps, to floor 3", cues.Update(world, new Vector3(0, 0, -0.5f), Facing(Vector3.UnitZ)));
    }

    /// <summary>
    /// Climbing: nothing is said on the treads, nothing on arriving at the top facing up the way you
    /// came, and turning round there to go back down says the top's line, with the floor below it.
    /// The feet are on the flight between the two ends and the two floors, and only there.
    /// </summary>
    [Fact]
    public void ClimbingAFlightSaysTheTopOnlyWhenYouTurnToGoDown()
    {
        var world = OneFlight();
        var cues = new StairCues();
        var up = Facing(Vector3.UnitZ);
        Assert.Equal("Stairs up, 10 steps, to floor 3", cues.Update(world, new Vector3(0, 0, -0.4f), up));
        Assert.False(cues.OnFlight);
        for (float z = 0.1f; z < 3.9f; z += 0.15f)
        {
            float y = MathF.Min(3f, MathF.Floor((z + 0.3f) / 0.32f) * 0.3f);
            Assert.Null(cues.Update(world, new Vector3(0, y, z), up));
            Assert.Equal(y > 0.1f && y < 2.9f, cues.OnFlight);
        }
        Assert.Null(cues.Update(world, new Vector3(0, 3f, 4.3f), up));               // the top, facing on
        Assert.False(cues.OnFlight);
        Assert.Equal("Stairs down, 10 steps, to floor 2", cues.Update(world, new Vector3(0, 3f, 4.3f), Facing(-Vector3.UnitZ)));
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
    }

    /// <summary>
    /// The stairs beacon is wired: the two ends of a flight blip, nothing between them does, it is the
    /// nearest two that blip, and /beacons stairs off and a map that forbids it both silence it.
    /// </summary>
    [Fact]
    public void TheStairsBeaconBlipsFromTheEndsOfFlightsOnly()
    {
        var world = OneFlight();
        // A third flight end further off: only the nearest two are heard.
        var far = MarkerSnap(3, "Stairs up, 10 steps, to floor 4", new Vector3(0, 1f, 8f), Vector3.UnitZ);
        world.Entities[far.Id] = far;
        world.MarkerEntityIds.Add(far.Id);
        var ear = new Vector3(0, 1.6f, -1f);

        List<SpatialEmitter> Run(BeaconPreferences prefs, IEnumerable<string>? policy = null)
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

        var blips = Run(BeaconPreferences.InMemory());
        Assert.NotEmpty(blips);
        Assert.All(blips, e => Assert.True(Vector3.Distance(e.Position, new Vector3(0, 1f, 0)) < 1e-3f
                                           || Vector3.Distance(e.Position, new Vector3(0, 4f, 4f)) < 1e-3f,
                                           $"a stairs blip from {e.Position}"));
        Assert.Contains(blips, e => e.Position.Y < 2f);
        Assert.Contains(blips, e => e.Position.Y > 2f);

        var off = BeaconPreferences.InMemory();
        off.Set(Beacons.Stairs, false);
        Assert.Empty(Run(off));
        Assert.Empty(Run(BeaconPreferences.InMemory(), new[] { "stairs=forbidden" }));
    }

    // ── Footsteps on them ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A body walking up a flight puts its feet down as steps up, coming down as steps down, and on the
    /// level as neither; and on the flight each footfall lands on the update the body arrives on a tread.
    /// </summary>
    [Fact]
    public void StepsOnAFlightAreUpOrDownAndLandOnTreads()
    {
        const float rise = 0.3f, going = 0.32f;
        var speed = PhysicsConstants.WalkSpeed;
        float dt = PhysicsConstants.FixedDeltaTime;

        (List<StepSlope> Slopes, int OffTread) Walk(int direction)
        {
            var stride = new StrideAccumulator();
            var slopes = new List<StepSlope>();
            int offTread = 0;
            float lastY = float.NaN;
            for (float z = -1f; z < 5f; z += speed * dt)
            {
                float along = direction > 0 ? z : 4f - z;
                int tread = Math.Clamp((int)MathF.Floor(along / going) + 1, 0, 10);
                float y = tread * rise;
                var f = stride.Update(new Vector3(0, y, z), new Vector3(0, 0, speed), true, Quaternion.Identity);
                if (f.Stepped)
                {
                    slopes.Add(f.Slope);
                    if (along > 0.4f && along < 3.0f && y == lastY) offTread++;
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
