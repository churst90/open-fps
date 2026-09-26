using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.Core;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Walks a real map with the REAL movement engine and the REAL ground probe, and reports every
/// footfall, every landing and every time the ground moved under the listener.
///
///   --walk [map=city] [from=x,z] [to=x,z] [seconds=12] [sprint] [stand=5]
///
/// Written for one report that no instrument could answer: *"walk a few steps, stop, and for like
/// 10 seconds, periodic bangs."* A landing is a much heavier sound than a footstep and fires at most
/// twice a second (StrideAccumulator.MinSecondsBetweenLandings), so "periodic bangs" is exactly what
/// a body being repeatedly lifted and dropped would sound like — and whether that happens is a
/// question about SharedMovementEngine and PhysicsUtils, not about audio at all.
///
/// So this runs those two, tick for tick, over the map's own boxes: nothing here is a model of the
/// movement, it IS the movement. If a landing fires on flat ground, or the ground height under a
/// standing body changes, it is printed with the tick it happened on.
/// </summary>
public static class WalkSpike
{
    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        string mapId = Str(args, "map") ?? "city";
        float seconds = Num(args, "seconds", 12f);
        float stand = Num(args, "stand", 5f);
        bool sprint = args.Contains("sprint");
        // taps=N: the key pressed for one tick and let go, N times, gap= ticks apart — the way Cody
        // walks as often as he holds a key. y= starts the body at that height (an upper floor).
        int taps = (int)Num(args, "taps", 0f);
        int gap = (int)Num(args, "gap", 10f);
        float startY = Num(args, "y", float.NaN);

        string? mapPath = FindUp(Path.Combine("OpenFPS.Server", "maps", mapId + ".json"));
        string? prefabDir = mapPath == null ? null
            : Path.Combine(Directory.GetParent(mapPath)!.Parent!.FullName, "prefabs");
        if (mapPath == null || prefabDir == null || !Directory.Exists(prefabDir))
        {
            Console.WriteLine($"  FAIL: no maps/{mapId}.json and prefabs/ above {Directory.GetCurrentDirectory()}");
            return 1;
        }

        var world = World.Create();
        var grid = new SpatialGrid<Entity>(10f);
        Vector3 spawn = LoadWorld(mapPath, prefabDir, world, grid, out int boxes);
        Console.WriteLine($"\n  {mapId}: {boxes} solid boxes, spawn {spawn}");

        float y0 = float.IsNaN(startY) ? spawn.Y : startY;
        Vector3 from = Vec2(Str(args, "from")) is { } f ? new Vector3(f.X, y0, f.Z) : spawn;
        Vector3 to = Vec2(Str(args, "to")) is { } t ? new Vector3(t.X, y0, t.Z) : from + new Vector3(0, 0, 20f);
        if (taps > 0) { seconds = (taps * gap + 60) * PhysicsConstants.FixedDeltaTime; stand = 0f; }
        Console.WriteLine($"  walking {from.X:F1},{from.Z:F1} -> {to.X:F1},{to.Z:F1}, then standing still {stand:F0} s\n");

        var dir = to - from; dir.Y = 0;
        if (dir.LengthSquared() > 1e-6f) dir = Vector3.Normalize(dir);

        var stride = new StrideAccumulator();
        var pos = from;
        var vel = Vector3.Zero;
        float dt = PhysicsConstants.FixedDeltaTime;
        int ticks = (int)(seconds / dt), standFrom = (int)((seconds - stand) / dt);
        int steps = 0, landings = 0, landingsWhileStill = 0, groundMoves = 0;
        float lastGround = float.NaN;
        var colliders = new List<SharedMovementEngine.Collider>();

        int tapSteps = 0, tapIndex = -1, silentTaps = 0;
        for (int i = 0; i < ticks; i++)
        {
            bool moving = i < standFrom;
            Vector3 input = moving ? dir : Vector3.Zero;
            if (taps > 0)
            {
                bool press = i % gap == 0 && i / gap < taps;
                input = press ? dir : Vector3.Zero;
                moving = true;
                if (press)
                {
                    if (tapIndex >= 0 && tapSteps == 0) silentTaps++;
                    tapIndex = i / gap; tapSteps = 0;
                }
            }

            float ground = PhysicsUtils.GetGroundHeight(world, grid, pos, out string material);
            if (!float.IsNaN(lastGround) && MathF.Abs(ground - lastGround) > 0.001f)
            {
                groundMoves++;
                if (!moving || groundMoves <= 12)
                    Console.WriteLine($"    t={i * dt,6:F2}s  ground {lastGround,7:F3} -> {ground,7:F3} "
                                    + $"({material}) at {pos.X:F2},{pos.Z:F2}{(moving ? "" : "   <-- WHILE STANDING STILL")}");
            }
            lastGround = ground;

            colliders.Clear();
            var near = new List<Entity>();
            var seen = new HashSet<Entity>();
            grid.CollectInRadius(pos, PhysicsConstants.CollisionSearchRadius, near, seen);
            foreach (var e in near)
            {
                if (!world.Has<Transform>(e) || !world.Has<ColliderComponent>(e)) continue;
                var ct = world.Get<Transform>(e); var cc = world.Get<ColliderComponent>(e);
                if (!cc.IsSolid) continue;
                string mat = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material : "Generic";
                colliders.Add(new SharedMovementEngine.Collider
                { Position = ct.Position, Size = cc.Size, Rotation = ct.Rotation, Material = mat });
            }

            var ctx = new SharedMovementEngine.MovementContext
            {
                Position = pos, Velocity = vel, InputDirection = input, DeltaTime = dt,
                GroundHeight = ground, Gravity = PhysicsConstants.Gravity, JumpForce = PhysicsConstants.JumpPower,
                Speed = sprint ? PhysicsConstants.SprintSpeed : PhysicsConstants.WalkSpeed,
                PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight,
                StepHeight = PhysicsConstants.StepHeight, IsJumpRequested = false,
                MapMin = new Vector3(-500, -100, -500), MapMax = new Vector3(500, 100, 500),
            };
            var result = SharedMovementEngine.Step(ctx, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(colliders));
            pos = result.NewPosition; vel = result.NewVelocity;

            var fall = stride.Update(pos, vel, result.IsGrounded, Quaternion.Identity);
            if (fall.Stepped) { steps++; tapSteps++; }
            if (taps > 0 && (i % gap) < 4 && i / gap < taps)
                Console.WriteLine($"    tap {i / gap,2} tick {i % gap}: speed {new Vector2(vel.X, vel.Z).Length(),5:F2} m/s grounded {result.IsGrounded,-5} y {pos.Y:F3} ground {ground:F3} {(fall.Stepped ? "STEP" : "")}");
            if (fall.Landed)
            {
                landings++;
                if (!moving) landingsWhileStill++;
                Console.WriteLine($"    t={i * dt,6:F2}s  LANDED at {pos.X:F2},{pos.Y:F2},{pos.Z:F2}"
                                + $"{(moving ? "   (while walking)" : "   <-- WHILE STANDING STILL")}");
            }
            if (!result.IsGrounded && i > 3)
                Console.WriteLine($"    t={i * dt,6:F2}s  off the ground at y={pos.Y:F3} (ground {ground:F3})"
                                + $"{(moving ? "" : "   <-- WHILE STANDING STILL")}");
        }

        if (taps > 0)
        {
            if (tapIndex >= 0 && tapSteps == 0) silentTaps++;
            Console.WriteLine($"\n  {taps} taps, {gap} ticks apart: {steps} footsteps, {silentTaps} tap(s) with none.");
        }
        Console.WriteLine($"\n  {seconds:F0} s: {steps} footsteps, {landings} landing(s) "
                        + $"({landingsWhileStill} of them standing still), the ground moved {groundMoves} time(s).");
        Console.WriteLine(landingsWhileStill > 0
            ? "  A body standing still landed. That is the bang.\n"
            : "  Nothing landed while standing still.\n");
        return landingsWhileStill > 0 ? 2 : 0;
    }

    private static Vector3 LoadWorld(string mapPath, string prefabDir, World world, SpatialGrid<Entity> grid, out int boxes)
    {
        var prefabs = new Dictionary<string, (Vector3 Size, string Material, bool Solid)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(prefabDir, "*.json"))
        {
            if (Path.GetFileName(file) == "prefab-schema.json") continue;
            using var d = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var r = d.RootElement;
            if (!r.TryGetProperty("Id", out var idj) || !r.TryGetProperty("ColliderSize", out var cs)) continue;
            bool solid = !r.TryGetProperty("IsSolid", out var sj) || sj.ValueKind != JsonValueKind.False;
            prefabs[idj.GetString()!] = (ReadVec(cs),
                r.TryGetProperty("Material", out var mj) ? mj.GetString() ?? "Generic" : "Generic", solid);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(mapPath),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        boxes = 0;
        foreach (var e in root.GetProperty("Entities").EnumerateArray())
        {
            if (!prefabs.TryGetValue(e.GetProperty("PrefabId").GetString()!, out var p) || !p.Solid) continue;
            Vector3 pos = ReadVec(e.GetProperty("Position"));
            Vector3 scale = e.TryGetProperty("Scale", out var sc) ? ReadVec(sc) : Vector3.One;
            Vector3 size = p.Size * scale;
            var ent = world.Create(
                new Transform { Position = pos, Rotation = Quaternion.Identity },
                new ColliderComponent { Size = size, IsSolid = true, Shape = ColliderShape.Box },
                new MaterialComponent { Material = p.Material });
            grid.AddOverlapping(pos, size, ent, isStatic: true);
            boxes++;
        }
        var s = ReadVec(root.GetProperty("SpawnPoint").GetProperty("Position"));
        return s;
    }

    private static Vector3 ReadVec(JsonElement v) => new(
        v.TryGetProperty("X", out var x) ? x.GetSingle() : 0f,
        v.TryGetProperty("Y", out var y) ? y.GetSingle() : 0f,
        v.TryGetProperty("Z", out var z) ? z.GetSingle() : 0f);

    private static Vector3? Vec2(string? s)
    {
        if (s == null) return null;
        var p = s.Split(',');
        return new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), 0f,
                           float.Parse(p[1], CultureInfo.InvariantCulture));
    }

    private static string? FindUp(string relative)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string c = Path.Combine(dir.FullName, relative);
            if (File.Exists(c) || Directory.Exists(c)) return c;
        }
        return null;
    }

    private static string? Str(string[] args, string name)
    {
        foreach (var a in args) if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return a[(name.Length + 1)..];
        return null;
    }

    private static float Num(string[] args, string name, float fallback)
        => float.TryParse(Str(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
