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

namespace OpenFPS.Client.Core.AudioEngine.SteamAudio;

/// <summary>
/// Up a flight of stairs and down again, W held, through the server's own movement on the triangle world
/// (the rounded-head body, the grade), with the client's footfalls (StrideAccumulator) on the way, and
/// every footfall checked: is the foot on a tread (the floor under where the foot goes down is at the
/// foot's height), and is the floor what the flight is made of? (docs/GEOMETRY.md stage 2: footfalls land
/// on real treads, so their cadence, material and height come from the geometry.)
///
///   --stair-walk [scene=shapes|city] [sprint] [trace]
///
/// scene=shapes builds a concrete flight (the concrete_stairs prefab's numbers: 16 risers of 17.5 cm on
/// goings of 28 cm) up to a landing and a wooden flight (wooden_stairs: 15 of 17.3 cm on 26 cm) down the
/// other side, on dirt. scene=city walks the first block of flats' stairwell, flight by flight to its roof
/// and back, by its stair markers.
/// </summary>
public static class StairWalkSpike
{
    private sealed record Footfall(int Tick, Vector3 At, StrideAccumulator.Footfall Fall, float FloorAt, string Material, float Grade);

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string scene = args.FirstOrDefault(a => a.StartsWith("scene="))?[6..] ?? "shapes";
        bool sprint = args.Contains("sprint"), trace = args.Contains("trace");
        string dir = Path.Combine(Path.GetTempPath(), "openfps-stair-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "maps"));
        try
        {
            var prefabs = new PrefabRepository(OpenFPS.AudioLab.LabPaths.Server("prefabs"));
            var maps = new MapManager(new MapRepository(Path.Combine(dir, "maps")), prefabs);
            List<Vector3> route;
            string mapId;
            if (scene == "city")
            {
                File.Copy(OpenFPS.AudioLab.LabPaths.Server("maps", "city.json"), Path.Combine(dir, "maps", "city.json"));
                maps.Initialize();
                mapId = "city";
                route = CityStairwell(maps);
            }
            else
            {
                maps.Initialize();
                mapId = "stairs";
                var map = MapTemplates.Flat(mapId, "lab");
                // Up a concrete flight toward +Z to a landing, and down a wooden one toward +Z again.
                var concrete = new Vector3(1.2f, 2.8f, 4.48f);
                var wood = new Vector3(1.0f, 2.6f, 3.9f);
                map.Entities.Add(new OpenFPS.Server.Repositories.EntityData { EntityId = 2, PrefabId = "concrete_stairs", Name = "concrete flight", Position = new Vector3(0, concrete.Y / 2f, 2f + concrete.Z / 2f) });
                map.Entities.Add(new OpenFPS.Server.Repositories.EntityData { EntityId = 3, PrefabId = "concrete_floor", Name = "landing", Position = new Vector3(0, 2.75f, 2f + concrete.Z + 1f), Scale = new Vector3(0.12f, 1f, 0.2f) });
                // The wooden flight comes down toward +Z: it climbs toward -Z, so it is turned half round, and
                // stands on a 0.2 m plinth so that its top is the landing's height.
                map.Entities.Add(new OpenFPS.Server.Repositories.EntityData { EntityId = 4, PrefabId = "wooden_stairs", Name = "wooden flight", Position = new Vector3(0, 0.2f + wood.Y / 2f, 2f + concrete.Z + 2f + wood.Z / 2f),
                                                               Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI, 0, 0) });
                map.Entities.Add(new OpenFPS.Server.Repositories.EntityData { EntityId = 5, PrefabId = "concrete_floor", Name = "plinth", Position = new Vector3(0, 0.15f, 2f + concrete.Z + 2f + wood.Z / 2f), Scale = new Vector3(0.1f, 2f, 0.39f) });
                if (!maps.CreateMap(map, out string error)) { Console.WriteLine($"FAIL: {error}"); return 1; }
                float end = 2f + concrete.Z + 2f + wood.Z + 2f;
                route = new List<Vector3> { new(0, 0, 0), new(0, 0, end) };
            }
            if (!maps.TryGetMap(mapId, out var world, out _, out var grid, out _) || !maps.TryGetMapData(mapId, out var data))
            { Console.WriteLine("FAIL: no map"); return 1; }
            var geo = grid.Geometry!;
            Console.WriteLine($"{mapId}: {route.Count} points to walk, {(sprint ? "running" : "walking")}");

            var falls = new List<Footfall>();
            var stride = new StrideAccumulator();
            var pos = route[0];
            var vel = Vector3.Zero;
            float dt = PhysicsConstants.FixedDeltaTime;
            int leg = 1, ticks = 0, airborne = 0;
            var solids = new List<SolidRef>();
            var all = new AcceptAll();
            while (leg < route.Count && ticks < 30 * 240)
            {
                var toward = route[leg] - pos; toward.Y = 0;
                if (toward.Length() <= PhysicsConstants.WalkSpeed * dt * 0.6f) { leg++; continue; }
                toward = Vector3.Normalize(toward);
                float ground = PhysicsUtils.GetGroundHeight(world, grid, pos, out _);
                var ctx = new SharedMovementEngine.MovementContext
                {
                    Position = pos, Velocity = vel, InputDirection = toward, DeltaTime = dt, GroundHeight = ground,
                    Gravity = PhysicsConstants.Gravity, JumpForce = PhysicsConstants.JumpPower, Speed = PhysicsConstants.FootSpeed(sprint, float.MaxValue),
                    PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
                    MapMin = data.WalkMin, MapMax = data.WalkMax, Body = BodyShape.Capsule,
                };
                ctx.Grade = SharedMovementEngine.GradeAlong(geo, ref all, pos, toward);
                SharedMovementEngine.GatherSolids(ctx, geo, ref all, solids);
                var obstacles = new SharedMovementEngine.Obstacles(ReadOnlySpan<SharedMovementEngine.Collider>.Empty, geo,
                                                                   System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids));
                var (p, v, grounded) = SharedMovementEngine.Step(ctx, obstacles, out _);
                pos = p; vel = v; ticks++;
                if (!grounded) airborne++;
                var fall = stride.Update(pos, vel, grounded, Quaternion.CreateFromYawPitchRoll(MathF.Atan2(toward.X, toward.Z), 0, 0));
                if (trace) Console.WriteLine($"  tick {ticks}: ({pos.X:F2}, {pos.Y:F3}, {pos.Z:F2}) {(grounded ? "on the ground" : "in the air")} grade {ctx.Grade:F2}");
                if (!fall.Stepped) continue;
                // Where the client puts the foot down (PhysicsUtils.FootOnFloor, as LocalPlayerController and
                // OtherBodies do), and then, independently, the floor under that point and what it is.
                var foot = PhysicsUtils.FootOnFloor(geo, ref all, fall.StepPosition, pos, vel, out _);
                float floor = geo.FloorAt(foot.X, foot.Z, foot.Y + PhysicsConstants.StepHeight, GeometryLayers.Ground, ref all, out var hit);
                string material = floor > -1000f ? geo.SurfaceOf(hit).Material : "-";
                falls.Add(new Footfall(ticks, foot, fall, floor, material, ctx.Grade));
            }

            int onTread = 0, off = 0;
            Console.WriteLine($"{falls.Count} footfalls in {ticks} ticks ({ticks * dt:F1} s), {airborne} ticks in the air:");
            Footfall? last = null;
            foreach (var f in falls)
            {
                float gap = MathF.Abs(f.At.Y - f.FloorAt);
                bool ok = gap < 0.002f;
                if (ok) onTread++; else off++;
                float rise = last == null ? 0f : f.At.Y - last.At.Y;
                float run = last == null ? 0f : new Vector2(f.At.X - last.At.X, f.At.Z - last.At.Z).Length();
                double seconds = last == null ? 0 : (f.Tick - last.Tick) * dt;
                Console.WriteLine($"  {f.Tick * dt,5:F2} s  at ({f.At.X:F2}, {f.At.Z:F2})  foot {f.At.Y:F3} m, floor {f.FloorAt:F3} m {f.Material,-8} "
                                  + $"{f.Fall.Slope,-5} {(ok ? "on the tread" : $"OFF BY {gap * 1000:F0} mm")}"
                                  + (last == null ? "" : $"  (+{seconds:F2} s, {run:F2} m, {rise:+0.000;-0.000} m)") + (f.Grade != 0f ? $"  grade {f.Grade:F2}" : ""));
                last = f;
            }
            Console.WriteLine(off == 0 ? $"PASS: every one of {onTread} footfalls is on a floor at the foot's height" : $"FAIL: {off} of {falls.Count} footfalls are not on a floor at the foot's height");
            return off == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>The first block of flats' stairwell, by its markers: from the foot of each flight to its top
    /// and across to the next, to the top storey, then back down the same way.</summary>
    private static List<Vector3> CityStairwell(MapManager maps)
    {
        maps.TryGetMap("city", out var world, out _, out _, out _);
        var markers = new List<(string Name, Vector3 At, Vector3 Along)>();
        world.Query(new QueryDescription().WithAll<IdentityComponent, Transform>(), (ref IdentityComponent id, ref Transform t) =>
        {
            if (id.PrefabId != "stair_marker") return;
            var f = Vector3.Transform(Vector3.UnitZ, t.Rotation);
            markers.Add((id.Name, t.Position, Vector3.Normalize(new Vector3(f.X, 0, f.Z))));
        });
        var foot = markers.Where(m => m.Name.StartsWith("Stairs up")).OrderBy(m => m.At.Y).ThenBy(m => m.At.X).ThenBy(m => m.At.Z).First();
        var up = new List<Vector3> { foot.At - new Vector3(0, StairCues.MarkerHeightMetres, 0) };
        for (int i = 0; i < 12; i++)
        {
            var top = markers.Where(m => m.Name.StartsWith("Stairs down") && Vector3.Dot(m.Along, foot.Along) < -0.9f && m.At.Y > foot.At.Y + 2f && m.At.Y < foot.At.Y + 4f)
                             .OrderBy(m => Vector2.Distance(new(m.At.X, m.At.Z), new(foot.At.X, foot.At.Z))).FirstOrDefault();
            if (top.Name == null) break;
            up.Add(foot.At); up.Add(top.At);
            var next = markers.Where(m => m.Name.StartsWith("Stairs up") && MathF.Abs(m.At.Y - top.At.Y) < 0.1f && !m.Name.EndsWith("to the roof"))
                              .OrderBy(m => Vector2.Distance(new(m.At.X, m.At.Z), new(top.At.X, top.At.Z))).FirstOrDefault();
            if (next.Name == null || Vector2.Distance(new(next.At.X, next.At.Z), new(top.At.X, top.At.Z)) > 8f) break;
            foot = next;
        }
        var route = new List<Vector3>(up);
        for (int i = up.Count - 2; i >= 0; i--) route.Add(up[i]);
        return route.Select(p => p with { Y = 0f }).ToList();
    }
}
