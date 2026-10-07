using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Geometry;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Geometry stage 2 (docs/GEOMETRY.md section 10) and the decisions taken on stage 1: the ground a map
/// gets when it has none, the turns made unit length at load.
/// </summary>
public class GeometryStage2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "openfps-geo2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MapManager Load(params string[] mapIds)
    {
        string mapDir = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "maps");
        Directory.CreateDirectory(mapDir);
        foreach (var id in mapIds)
            File.Copy(Path.Combine(AppContext.BaseDirectory, "maps", id + ".json"), Path.Combine(mapDir, id + ".json"));
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        return maps;
    }

    private static List<Entity> Grounds(World world, Dictionary<int, Entity> lookup)
        => lookup.Values.Where(e => world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).Name == "Ground").ToList();

    /// <summary>The city lays its own ground over all of its play area: the loader adds nothing under
    /// it, so nothing lies flush under the map's own dirt.</summary>
    [Fact]
    public void AMapWithItsOwnGroundGetsNoFoundation()
    {
        var maps = Load("city");
        Assert.True(maps.TryGetMap("city", out var world, out _, out _, out var lookup));
        var grounds = Grounds(world, lookup);
        Assert.Single(grounds);
        Assert.Equal("Dirt", world.Get<MaterialComponent>(grounds[0]).Material);
        Assert.DoesNotContain(lookup.Values, e => world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).PrefabId == "concrete_floor"
                                                  && world.Get<ColliderComponent>(e).Size.X > 1000f);
    }

    /// <summary>The speedway lays patches (grass, the track) but no ground under all of it: the loader
    /// lays natural ground, dirt, under its bounds, its top at 0.</summary>
    [Fact]
    public void AMapWithoutGroundGetsDirt()
    {
        var maps = Load("speedway");
        Assert.True(maps.TryGetMap("speedway", out var world, out _, out _, out var lookup));
        Assert.True(maps.TryGetMapData("speedway", out var data));
        var ground = Assert.Single(Grounds(world, lookup));
        Assert.Equal("Dirt", world.Get<MaterialComponent>(ground).Material);
        var t = world.Get<Transform>(ground); var c = world.Get<ColliderComponent>(ground);
        Assert.Equal(0f, t.Position.Y + c.Size.Y / 2f, 4);
        Assert.True(c.Size.X >= data.MaxBound.X - data.MinBound.X - 0.01f && c.Size.Z >= data.MaxBound.Z - data.MinBound.Z - 0.01f);
    }

    /// <summary>A new map (/map new) is dirt, and its own: nothing is laid under it.</summary>
    [Fact]
    public void ANewMapIsDirt()
    {
        string mapDir = Path.Combine(_dir, "new", "maps");
        Directory.CreateDirectory(mapDir);
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        Assert.True(maps.CreateMap(MapTemplates.Flat("plot", "tester"), out string error), error);
        Assert.True(maps.TryGetMap("plot", out var world, out _, out _, out var lookup));
        var solids = lookup.Values.Where(e => world.Has<ColliderComponent>(e) && world.Get<ColliderComponent>(e).IsSolid).ToList();
        var ground = Assert.Single(solids);
        Assert.Equal("Dirt", world.Get<MaterialComponent>(ground).Material);
        Assert.Equal("Ground", world.Get<IdentityComponent>(ground).Name);
    }

    // ═══ Shapes ══════════════════════════════════════════════════════════════════════════════════

    private static Surface Concrete(Vector3 size) => EntityGeometry.SurfaceOf("Concrete", size, 0, 0, false, 0, 0, false, false, false, null);

    private static SolidSpec Shaped(int owner, Vector3 at, Vector3 size, ShapeSpec? form, float yaw = 0f)
        => SolidSpec.Of(owner, at, Quaternion.CreateFromYawPitchRoll(yaw, 0, 0), size, Concrete(size), Shapes.Make(form, size));

    private static TriangleWorld WorldOf(params SolidSpec[] solids)
    {
        var all = new List<SolidSpec> { Shaped(1, new Vector3(0, -0.05f, 0), new Vector3(200, 0.1f, 200), null) };
        all.AddRange(solids);
        return new TriangleWorldBuilder(250f).Build(all, Array.Empty<SolidSpec>());
    }

    public static IEnumerable<object[]> Forms() => new[]
    {
        new object[] { new ShapeSpec { Kind = ShapeKind.Wedge }, new Vector3(1.5f, 0.5f, 6f) },
        new object[] { new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }, new Vector3(1.2f, 2.8f, 4.48f) },
        new object[] { new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 10, Landing = 1.2f }, new Vector3(1.0f, 1.75f, 4.0f) },
        new object[] { new ShapeSpec { Kind = ShapeKind.Arch, Thickness = 0.5f }, new Vector3(3f, 3.5f, 0.6f) },
    };

    /// <summary>Every shape is a closed solid: a ray from outside enters and leaves it as often, and a
    /// point is inside one of its convex pieces exactly when a ray from it crosses its surface an odd number
    /// of times.</summary>
    [Theory]
    [MemberData(nameof(Forms))]
    public void AShapeIsClosedAndItsPiecesFillIt(ShapeSpec form, Vector3 size)
    {
        Assert.Null(Shapes.Problem(form, size));
        var world = WorldOf(Shaped(7, new Vector3(3, size.Y / 2f, 4), size, form, yaw: 0.4f));
        var only = new ExceptOwners(1);
        var crossings = new List<GeometryCrossing>();
        var inside = new List<SolidRef>();
        var rng = new Random(11);
        int insideCount = 0;
        for (int i = 0; i < 3000; i++)
        {
            var p = new Vector3(3, size.Y / 2f, 4) + new Vector3((float)(rng.NextDouble() * 2 - 1) * size.X, (float)(rng.NextDouble() * 2 - 1) * size.Y * 0.6f,
                                                                 (float)(rng.NextDouble() * 2 - 1) * size.Z);
            var d = Vector3.Normalize(new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f));
            world.All(p, d, 50f, GeometryLayers.Physical, ref only, crossings);
            int front = crossings.Count(c => c.Front), back = crossings.Count - front;
            inside.Clear();
            world.Containing(p, GeometryLayers.Physical, ref only, inside);
            bool isIn = inside.Count > 0;
            if (isIn) insideCount++;
            // From inside, one more face is left than entered; from outside, as many.
            Assert.Equal(isIn ? 1 : 0, back - front);
        }
        Assert.True(insideCount > 100, $"only {insideCount} of 3000 points fell inside");
    }

    /// <summary>The same numbers make the same triangles, bit for bit, wherever and however often.</summary>
    [Fact]
    public void AShapeIsTheSameEverywhere()
    {
        var size = new Vector3(1.2f, 2.8f, 4.48f);
        var a = Shapes.Make(new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }, size)!;
        var b = Shapes.Make(new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }, size)!;
        Assert.Equal(a.Outer.Hash, b.Outer.Hash);
        Assert.Equal(a.Parts!.Select(m => m.Hash), b.Parts!.Select(m => m.Hash));
        var w1 = WorldOf(Shaped(7, new Vector3(3.3f, 1.4f, 4.1f), size, new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }, 0.7f));
        var w2 = WorldOf(Shaped(7, new Vector3(3.3f, 1.4f, 4.1f), size, new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }, 0.7f));
        Assert.Equal(w1.Instance(w1.InstanceOfTile(new TileKey(0, 0))).Piece.Signature, w2.Instance(w2.InstanceOfTile(new TileKey(0, 0))).Piece.Signature);
    }

    /// <summary>A ramp is ground with a slope; a bank steeper than one can walk is not ground at all, and the
    /// probe goes on down past it to what is under it.</summary>
    [Fact]
    public void ARampIsGroundAndABankTooSteepIsNot()
    {
        var ramp = Shaped(7, new Vector3(0, 0.25f, 3f), new Vector3(1.5f, 0.5f, 6f), new ShapeSpec { Kind = ShapeKind.Wedge });
        var bank = Shaped(8, new Vector3(10, 1f, 3f), new Vector3(2f, 2f, 1f), new ShapeSpec { Kind = ShapeKind.Wedge });   // 63 degrees
        var world = WorldOf(ramp, bank);
        var all = new AcceptAll();
        // Halfway up the ramp: a quarter of a metre, and its normal leans back down the slope.
        float y = world.FloorAt(0f, 3f, 2f, GeometryLayers.Ground, ref all, out var hit);
        Assert.Equal(0.25f, y, 3);
        Assert.True(hit.Normal.Y > 0.99f && hit.Normal.Z < -0.08f, $"normal {hit.Normal}");
        // On the bank: the ground under it, not its face.
        float under = world.FloorAt(10f, 3f, 3f, GeometryLayers.Ground, ref all, out var floor);
        Assert.Equal(0f, under, 4);
        Assert.Equal(1, floor.Owner);
    }

    private sealed record Walked(List<Vector3> Path, Vector3 End, bool Grounded);

    /// <summary>A body walking on a little world as the server walks one: the ground under it, the grade,
    /// the capsule, the solids within reach.</summary>
    private static Walked Walk(TriangleWorld world, Vector3 from, Vector3 direction, int ticks, bool sprint = false,
                               BodyShape body = BodyShape.Capsule)
    {
        var path = new List<Vector3>();
        Vector3 pos = from, vel = Vector3.Zero;
        bool grounded = false;
        var all = new AcceptAll();
        var solids = new List<SolidRef>();
        for (int t = 0; t < ticks; t++)
        {
            float ground = world.Ground(pos, PhysicsConstants.PlayerRadius, PhysicsConstants.StepHeight, GeometryLayers.Ground, ref all, out _, out _);
            var ctx = new SharedMovementEngine.MovementContext
            {
                Position = pos, Velocity = vel, InputDirection = direction, DeltaTime = PhysicsConstants.FixedDeltaTime, GroundHeight = ground,
                Gravity = PhysicsConstants.Gravity, JumpForce = PhysicsConstants.JumpPower, Speed = PhysicsConstants.FootSpeed(sprint, float.MaxValue),
                PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
                MapMin = new Vector3(-100, -100, -100), MapMax = new Vector3(100, 100, 100), Body = body,
            };
            if (body == BodyShape.Capsule) ctx.Grade = SharedMovementEngine.GradeAlong(world, ref all, pos, direction);
            SharedMovementEngine.GatherSolids(ctx, world, ref all, solids);
            var obstacles = new SharedMovementEngine.Obstacles(ReadOnlySpan<SharedMovementEngine.Collider>.Empty, world,
                                                               System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids));
            (pos, vel, grounded) = SharedMovementEngine.Step(ctx, obstacles, out _);
            path.Add(pos);
        }
        return new Walked(path, pos, grounded);
    }

    /// <summary>Up a ramp of one in twelve at a walk: onto the top, a little slower than on the level, and
    /// down it again a little faster.</summary>
    [Fact]
    public void AWalkUpARampIsSlowerAndDownItFaster()
    {
        var world = WorldOf(Shaped(7, new Vector3(0, 0.25f, 3f), new Vector3(1.5f, 0.5f, 6f), new ShapeSpec { Kind = ShapeKind.Wedge }),
                            Shaped(8, new Vector3(0, 0.45f, 8f), new Vector3(1.5f, 0.1f, 4f), null));   // the landing at the top
        var up = Walk(world, new Vector3(0, 0, -1f), Vector3.UnitZ, 70);
        Assert.True(up.End.Z > 6f, $"ended at {up.End}");
        Assert.Equal(0.5f, up.End.Y, 3);
        // On the slope, a tick covers 1 / (1 + 2/12) of a level tick.
        int onSlope = up.Path.FindIndex(p => p.Z > 1.5f);
        float tick = up.Path[onSlope + 1].Z - up.Path[onSlope].Z;
        float level = PhysicsConstants.WalkSpeed * PhysicsConstants.FixedDeltaTime;
        Assert.Equal(level * SharedMovementEngine.GradeSpeed(1f / 12f), tick, 3);
        Assert.True(tick < level);
        var down = Walk(world, new Vector3(0, 0.5f, 6.5f), -Vector3.UnitZ, 70);
        Assert.True(down.End.Z < 0f && down.End.Y == 0f, $"ended at {down.End}");
        int descending = down.Path.FindIndex(p => p.Z < 4.5f);
        Assert.True(down.Path[descending].Z - down.Path[descending + 1].Z > level, "down a gentle ramp is a little faster");
    }

    /// <summary>
    /// Up a flight of real treads with W held, and down again: the body climbs onto each tread in turn
    /// (its feet only ever on a tread's height between steps), reaches the top, and is slower on the
    /// flight than on the level.
    /// </summary>
    [Fact]
    public void AFlightIsClimbedTreadByTreadAndComeDown()
    {
        var size = new Vector3(1.2f, 2.8f, 4.48f);
        var world = WorldOf(Shaped(7, new Vector3(0, 1.4f, 2.24f), size, new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }),
                            Shaped(8, new Vector3(0, 2.75f, 6.5f), new Vector3(1.2f, 0.1f, 4f), null));   // the landing at the top
        var up = Walk(world, new Vector3(0, 0, -1f), Vector3.UnitZ, 90);
        Assert.True(MathF.Abs(up.End.Y - 2.8f) < 1e-3f, $"ended at {up.End}: " + string.Join(" ", up.Path.Select(p => $"({p.Y:F3},{p.Z:F2})")));
        Assert.True(up.End.Z > 4.6f, $"stopped at {up.End}");
        var treads = Enumerable.Range(0, 17).Select(i => i * 0.175f).ToArray();
        foreach (var p in up.Path)
            if (p.Z > 0.3f && p.Z < 4.2f)
                Assert.True(treads.Any(t => MathF.Abs(p.Y - t) < 1e-3f) || treads.Any(t => MathF.Abs(p.Y - (t + PhysicsConstants.StepHeight)) < 1e-3f),
                            $"feet at {p.Y:F3} at z {p.Z:F2}: not on a tread, nor stepping up from one");
        // Slower on the flight: it takes longer than its length at a walk.
        int first = up.Path.FindIndex(p => p.Z > 0.5f), last = up.Path.FindIndex(p => p.Z > 4.0f);
        Assert.True((last - first) * PhysicsConstants.FixedDeltaTime > 3.5f / PhysicsConstants.WalkSpeed * 1.8f,
                    $"{last - first} ticks for 3.5 m of stairs");

        var down = Walk(world, new Vector3(0, 2.8f, 6f), -Vector3.UnitZ, 150);
        Assert.Equal(0f, down.End.Y, 3);
        Assert.True(down.End.Z < -0.5f, $"stopped at {down.End}");
    }

    /// <summary>
    /// Every footfall up a concrete flight and down a wooden one lands on a tread: the floor under the
    /// foot is at the foot's height and is the flight's material, two treads a footfall at the game's walk
    /// (the client's stride, PhysicsUtils.FootOnFloor; AudioLab --stair-walk walks the same with a report).
    /// </summary>
    [Fact]
    public void EachFootfallOnAFlightLandsOnATread()
    {
        var concrete = new Vector3(1.2f, 2.8f, 4.48f);
        var wood = new Vector3(1.0f, 2.6f, 3.9f);
        var woodSurface = EntityGeometry.SurfaceOf("Wood", wood, 0, 0, false, 0, 0, false, false, false, null);
        var world = WorldOf(
            Shaped(7, new Vector3(0, 1.4f, 2f + concrete.Z / 2f), concrete, new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 16 }),
            Shaped(8, new Vector3(0, 2.75f, 2f + concrete.Z + 1f), new Vector3(1.2f, 0.1f, 2f), null),
            SolidSpec.Of(9, new Vector3(0, 0.2f + wood.Y / 2f, 2f + concrete.Z + 2f + wood.Z / 2f), Quaternion.CreateFromYawPitchRoll(MathF.PI, 0, 0),
                         wood, woodSurface, Shapes.Make(new ShapeSpec { Kind = ShapeKind.Stairs, Steps = 15 }, wood)),
            Shaped(10, new Vector3(0, 0.1f, 2f + concrete.Z + 2f + wood.Z / 2f), new Vector3(1.0f, 0.2f, wood.Z), null));
        var walk = Walk(world, new Vector3(0, 0, 0), Vector3.UnitZ, 150);
        var stride = new OpenFPS.Client.Core.StrideAccumulator();
        var all = new AcceptAll();
        Vector3 last = walk.Path[0];
        int onConcrete = 0, onWood = 0;
        foreach (var at in walk.Path)
        {
            var vel = (at - last) / PhysicsConstants.FixedDeltaTime;
            last = at;
            var fall = stride.Update(at, vel, true, Quaternion.Identity);
            if (!fall.Stepped) continue;
            var foot = PhysicsUtils.FootOnFloor(world, ref all, fall.StepPosition, at, vel, out var material);
            float floor = world.FloorAt(foot.X, foot.Z, foot.Y + 0.4f, GeometryLayers.Ground, ref all, out var hit);
            Assert.True(MathF.Abs(foot.Y - floor) < 2e-3f, $"foot at {foot} over a floor at {floor}");
            string under = world.SurfaceOf(hit).Material;
            Assert.Equal(under, material);
            if (foot.Z > 2.3f && foot.Z < 6.3f) { Assert.Equal("Concrete", under); onConcrete++; }
            if (foot.Z > 8.8f && foot.Z < 12.2f) { Assert.Equal("Wood", under); onWood++; }
        }
        // Two treads a footfall: about eight up sixteen treads and down fifteen.
        Assert.InRange(onConcrete, 6, 10);
        Assert.InRange(onWood, 5, 10);
    }

    /// <summary>
    /// A car's four wheel rays: on a ramp of one in twelve it pitches nose up by its slope; with its right
    /// wheels up a 12 cm kerb it rolls right side up; on the level it sits level; and each wheel reads the
    /// surface under it.
    /// </summary>
    [Fact]
    public void FourWheelRaysPitchAndRollACar()
    {
        var kerbSurface = EntityGeometry.SurfaceOf("Concrete", new Vector3(4, 0.12f, 20), 0, 0, false, 0, 0, false, false, false, null);
        var grass = EntityGeometry.SurfaceOf("Grass", new Vector3(20, 0.02f, 20), 0, 0, false, 0, 0, false, false, false, null);
        var world = WorldOf(
            Shaped(7, new Vector3(0, 0.5f, 6f), new Vector3(4f, 1f, 12f), new ShapeSpec { Kind = ShapeKind.Wedge }),   // 1 in 12, rising toward +Z
            new SolidSpec(8, new Vector3(22f, 0.06f, 0f), Quaternion.Identity, new Vector3(4f, 0.12f, 20f), kerbSurface),
            new SolidSpec(9, new Vector3(-30f, -0.01f, 0f), Quaternion.Identity, new Vector3(20f, 0.02f, 20f), grass));
        var all = new AcceptAll();
        float[] along = { 1.3f, 1.3f, -1.3f, -1.3f }, across = { 0.8f, -0.8f, 0.8f, -0.8f };
        (float H, float Pitch, float Roll, WheelContact[] C) Sit(Vector3 middle)
        {
            var at = new Vector3[4];
            for (int i = 0; i < 4; i++) at[i] = middle + new Vector3(across[i], 0, along[i]);
            var c = new WheelContact[4];
            WheelRays.Contacts(world, ref all, at, 0.4f, c);
            var (h, p, r) = WheelRays.Rest(c, along, across);
            return (h, p, r, c);
        }
        var level = Sit(new Vector3(-50f, 0f, -50f));
        Assert.Equal(0f, level.Pitch); Assert.Equal(0f, level.Roll);
        var ramp = Sit(new Vector3(0f, 0.5f, 6f));
        Assert.Equal(MathF.Atan(1f / 12f), ramp.Pitch, 4);
        Assert.Equal(0f, ramp.Roll, 4);
        Assert.Equal(0.5f, ramp.H, 3);
        // Right wheels on the kerb (it starts at x 20), left wheels on the road.
        var kerb = Sit(new Vector3(19.5f, 0f, 0f));
        Assert.Equal(MathF.Atan(0.12f / 1.6f), kerb.Roll, 4);
        Assert.Equal("Concrete", kerb.C[0].Material);
        Assert.Equal("Concrete", kerb.C[1].Material);   // the ground under the left wheels is the world's own
        // Left wheels on the grass patch, right ones on the ground beside it.
        var verge = Sit(new Vector3(-20.5f, 0f, 0f));
        Assert.Equal("Grass", verge.C[1].Material);
        Assert.NotEqual("Grass", verge.C[0].Material);
    }

    /// <summary>A bank too steep to walk is a wall: walked at, it is not climbed.</summary>
    [Fact]
    public void ABankTooSteepToWalkIsNotClimbed()
    {
        var world = WorldOf(Shaped(8, new Vector3(0, 1f, 3f), new Vector3(2f, 2f, 1f), new ShapeSpec { Kind = ShapeKind.Wedge }));
        var walk = Walk(world, new Vector3(0, 0, 0f), Vector3.UnitZ, 60);
        Assert.True(walk.End.Y < 0.45f, $"climbed to {walk.End}");
        Assert.True(walk.End.Z < 3.2f, $"went through to {walk.End}");
    }

    /// <summary>Through an arch's opening, and not through its piers.</summary>
    [Fact]
    public void AnArchIsWalkedThroughItsOpening()
    {
        var world = WorldOf(Shaped(9, new Vector3(0, 1.75f, 2f), new Vector3(3f, 3.5f, 0.6f), new ShapeSpec { Kind = ShapeKind.Arch, Thickness = 0.5f }));
        var through = Walk(world, new Vector3(0, 0, 0), Vector3.UnitZ, 40);
        Assert.True(through.End.Z > 3f, $"stopped at {through.End}");
        var pier = Walk(world, new Vector3(1.2f, 0, 0), Vector3.UnitZ, 40);
        Assert.True(pier.End.Z < 1.71f, $"walked into the pier to {pier.End}");
    }

    /// <summary>Against a box's upright face the capsule is met as the cylinder was: the same way out, the
    /// same distance.</summary>
    [Fact]
    public void TheCapsuleMeetsAWallAsTheCylinderDid()
    {
        var world = WorldOf(Shaped(5, new Vector3(0, 1.5f, 2f), new Vector3(4f, 3f, 0.3f), null, yaw: 0.3f));
        var all = new AcceptAll();
        var near = new List<SolidRef>();
        world.Overlapping(new Vector3(-5, -1, -5), new Vector3(5, 5, 5), GeometryLayers.Movement, ref all, near);
        var wall = near.Single(s => world.OwnerOf(s) == 5);
        var rng = new Random(3);
        var capsule = new SolidContact.Capsule(PhysicsConstants.PlayerRadius, 0.15f, PhysicsConstants.PlayerHeight);
        int compared = 0;
        for (int i = 0; i < 2000; i++)
        {
            var feet = new Vector3((float)(rng.NextDouble() * 4 - 2), 0f, 2f + (float)(rng.NextDouble() * 1.2 - 0.6));
            var cyl = SolidContact.CylinderOverlap(world, wall, feet + new Vector3(0, 0.15f + 0.825f, 0), 0.3f, 1.65f);
            var cap = SolidContact.CapsuleOverlap(world, wall, feet, capsule, airborne: false);
            Assert.Equal(cyl.IsColliding, cap.IsColliding);
            if (!cyl.IsColliding || cyl.Penetration >= 0.3f) continue;   // the axis inside: the capsule is the cylinder there by rule
            compared++;
            Assert.True(Vector3.Distance(cyl.Normal, cap.Normal) < 1e-3f, $"normal {cyl.Normal} vs {cap.Normal}");
            Assert.Equal(cyl.Penetration, cap.Penetration, 3);
        }
        Assert.True(compared > 100);
    }

    /// <summary>Maps write turns in six digits (Magnolia's 0.707082, 0.707131 is not unit length); every
    /// one is unit length once loaded, and it is the same turn. A turn left out is the identity.</summary>
    [Fact]
    public void TurnsAreUnitLengthOnceLoaded()
    {
        string mapDir = Path.Combine(_dir, "turns", "maps");
        Directory.CreateDirectory(mapDir);
        var maps = new MapManager(new MapRepository(mapDir), new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs")));
        maps.Initialize();
        var map = MapTemplates.Flat("turns", "tester");
        var written = new Quaternion(0f, 0.707082f, 0f, 0.707131f);
        Assert.True(MathF.Abs(written.LengthSquared() - 1f) > 1e-7f);
        map.Entities.Add(new OpenFPS.Server.Repositories.EntityData { EntityId = 2, PrefabId = "concrete_wall", Name = "turned wall", Position = new Vector3(5, 1.5f, 5), Rotation = written });
        map.Entities.Add(new OpenFPS.Server.Repositories.EntityData { EntityId = 3, PrefabId = "concrete_wall", Name = "unturned wall", Position = new Vector3(-5, 1.5f, 5), Rotation = default });
        Assert.True(maps.CreateMap(map, out string error), error);
        Assert.True(maps.TryGetMap("turns", out var world, out _, out _, out var lookup));
        Entity Named(string name) => lookup.Values.Single(e => world.Has<IdentityComponent>(e) && world.Get<IdentityComponent>(e).Name == name);
        var q = world.Get<Transform>(Named("turned wall")).Rotation;
        Assert.True(MathF.Abs(q.LengthSquared() - 1f) < 1e-7f, $"length squared {q.LengthSquared()}");
        Assert.True(MathF.Abs(Quaternion.Dot(q, Quaternion.Normalize(written))) > 1f - 1e-6f);
        Assert.Equal(Quaternion.Identity, world.Get<Transform>(Named("unturned wall")).Rotation);
    }
}
