using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core.AudioEngine.Fmod;

/// <summary>
/// Walks a real map with the REAL movement engine and the REAL ground probe, and reports every
/// footfall, every landing and every time the ground moved under the listener.
///
///   --walk [map=city] [from=x,z] [to=x,z] [seconds=12] [sprint] [stand=5]
///   --walk map=city from=x,z via=x,z;x,z;... [y=h] [trace]
///
/// via= walks a ROUTE: straight at each point in turn, turning on the spot when it gets there, the
/// way a player holding W and tapping the turn keys does. trace prints every change of ground height
/// rather than the first dozen, so a flight of stairs can be read tread by tread. Doors are taken as
/// open (a door the player has opened is not in the way), and the named place at eye height is
/// printed whenever it changes, which is what the zone announcer is given.
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
        bool trace = args.Contains("trace");
        var route = Route(Str(args, "via"));
        bool routed = route.Count > 0;

        string? mapPath = OpenFPS.AudioLab.LabPaths.Existing(OpenFPS.AudioLab.LabPaths.Server("maps", mapId + ".json"));
        string? prefabDir = mapPath == null ? null
            : Path.Combine(Directory.GetParent(mapPath)!.Parent!.FullName, "prefabs");
        if (mapPath == null || prefabDir == null || !Directory.Exists(prefabDir))
        {
            Console.WriteLine($"  FAIL: no maps/{mapId}.json and prefabs/ above {Directory.GetCurrentDirectory()}");
            return 1;
        }

        var world = World.Create();
        var grid = new SpatialGrid<Entity>(10f);
        var zones = new List<(string Name, Vector3 Min, Vector3 Max)>();
        Vector3 spawn = LoadWorld(mapPath, prefabDir, world, grid, zones, out int boxes, out int doors);
        Console.WriteLine($"\n  {mapId}: {boxes} solid boxes ({doors} doors taken as open), {zones.Count} named boxes, spawn {spawn}");

        float y0 = float.IsNaN(startY) ? spawn.Y : startY;
        Vector3 from = Vec2(Str(args, "from")) is { } f ? new Vector3(f.X, y0, f.Z) : spawn;
        Vector3 to = Vec2(Str(args, "to")) is { } t ? new Vector3(t.X, y0, t.Z) : from + new Vector3(0, 0, 20f);
        if (taps > 0) { seconds = (taps * gap + 60) * PhysicsConstants.FixedDeltaTime; stand = 0f; }
        if (routed)
        {
            to = route[^1];
            if (Str(args, "seconds") == null) seconds = 600f;
            Console.WriteLine($"  walking {from.X:F1},{from.Z:F1} through {route.Count} point(s) to {to.X:F1},{to.Z:F1}, then standing still {stand:F0} s\n");
        }
        else Console.WriteLine($"  walking {from.X:F1},{from.Z:F1} -> {to.X:F1},{to.Z:F1}, then standing still {stand:F0} s\n");

        var dir = to - from; dir.Y = 0;
        if (dir.LengthSquared() > 1e-6f) dir = Vector3.Normalize(dir);
        int leg = 0, airborne = 0;
        var slopes = new int[3];
        string zone = "";

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
            if (routed)
            {
                // Straight at the next point; on reaching it, turn to face the one after. The stand
                // begins on the tick the last one is reached.
                while (leg < route.Count)
                {
                    var toward = route[leg] - pos; toward.Y = 0;
                    if (toward.Length() > PhysicsConstants.WalkSpeed * dt * 0.6f) { dir = Vector3.Normalize(toward); break; }
                    Console.WriteLine($"    t={i * dt,6:F2}s  reached point {leg + 1} at {pos.X:F2},{pos.Y:F2},{pos.Z:F2}");
                    leg++;
                }
                if (leg >= route.Count && standFrom > i) { standFrom = i; ticks = Math.Min(ticks, i + (int)(stand / dt)); }
            }
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
                if (!moving || trace || groundMoves <= 12)
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
                { Position = ct.Position, Size = cc.Size, Rotation = SharedMovementEngine.StandingRotation(cc.Shape, ct.Rotation), Material = mat });
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
            if (fall.Stepped)
            {
                steps++; tapSteps++;
                slopes[(int)fall.Slope]++;
                if (trace) Console.WriteLine($"    t={i * dt,6:F2}s  footstep {fall.Slope.ToString().ToLowerInvariant()} at y {pos.Y:F2}");
            }
            if (taps > 0 && (i % gap) < 4 && i / gap < taps)
                Console.WriteLine($"    tap {i / gap,2} tick {i % gap}: speed {new Vector2(vel.X, vel.Z).Length(),5:F2} m/s grounded {result.IsGrounded,-5} y {pos.Y:F3} ground {ground:F3} {(fall.Stepped ? "STEP" : "")}");
            if (fall.Landed)
            {
                landings++;
                if (!moving) landingsWhileStill++;
                Console.WriteLine($"    t={i * dt,6:F2}s  LANDED at {pos.X:F2},{pos.Y:F2},{pos.Z:F2}"
                                + $"{(moving ? "   (while walking)" : "   <-- WHILE STANDING STILL")}");
            }
            string here = ZoneAt(zones, pos + new Vector3(0, 1.7f, 0));
            if (here != zone)
            {
                Console.WriteLine($"    t={i * dt,6:F2}s  zone '{zone}' -> '{here}' at {pos.X:F2},{pos.Y:F2},{pos.Z:F2}");
                zone = here;
            }
            if (!result.IsGrounded && i > 3) airborne++;
            if (!result.IsGrounded && i > 3)
                Console.WriteLine($"    t={i * dt,6:F2}s  off the ground at y={pos.Y:F3} (ground {ground:F3})"
                                + $"{(moving ? "" : "   <-- WHILE STANDING STILL")}");
        }

        if (taps > 0)
        {
            if (tapIndex >= 0 && tapSteps == 0) silentTaps++;
            Console.WriteLine($"\n  {taps} taps, {gap} ticks apart: {steps} footsteps, {silentTaps} tap(s) with none.");
        }
        Console.WriteLine($"\n  {ticks * dt:F0} s: {steps} footsteps, {landings} landing(s) "
                        + $"({landingsWhileStill} of them standing still), the ground moved {groundMoves} time(s), "
                        + $"{airborne} tick(s) off the ground.");
        Console.WriteLine($"  Footsteps: {slopes[(int)StepSlope.Level]} level, {slopes[(int)StepSlope.Up]} up, {slopes[(int)StepSlope.Down]} down.");
        if (routed)
            Console.WriteLine(leg >= route.Count
                ? $"  Reached all {route.Count} points; ended at {pos.X:F2},{pos.Y:F2},{pos.Z:F2} in '{zone}'."
                : $"  STUCK before point {leg + 1} of {route.Count} ({route[leg].X:F1},{route[leg].Z:F1}); "
                + $"ended at {pos.X:F2},{pos.Y:F2},{pos.Z:F2} in '{zone}'.");
        Console.WriteLine(landingsWhileStill > 0
            ? "  A body standing still landed. That is the bang.\n"
            : "  Nothing landed while standing still.\n");
        return landingsWhileStill > 0 ? 2 : 0;
    }

    private static Vector3 LoadWorld(string mapPath, string prefabDir, World world, SpatialGrid<Entity> grid,
                                     List<(string Name, Vector3 Min, Vector3 Max)> zones, out int boxes, out int doors)
    {
        var prefabs = new Dictionary<string, (Vector3 Size, string Material, bool Solid)>(StringComparer.OrdinalIgnoreCase);
        var doorPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            if (r.TryGetProperty("IsDoor", out var dj) && dj.ValueKind == JsonValueKind.True) doorPrefabs.Add(idj.GetString()!);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(mapPath),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        boxes = 0; doors = 0;
        foreach (var e in root.GetProperty("Entities").EnumerateArray())
        {
            string prefabId = e.GetProperty("PrefabId").GetString()!;
            if (!prefabs.TryGetValue(prefabId, out var p)) continue;
            Vector3 pos = ReadVec(e.GetProperty("Position"));
            Vector3 scale = e.TryGetProperty("Scale", out var sc) ? ReadVec(sc) : Vector3.One;
            Vector3 size = p.Size * scale;
            if (prefabId.Equals("acoustic_region", StringComparison.OrdinalIgnoreCase)
                && e.TryGetProperty("Name", out var nj) && nj.GetString() is { Length: > 0 } zoneName)
                zones.Add((zoneName, pos - size / 2, pos + size / 2));
            if (!p.Solid) continue;
            if (doorPrefabs.Contains(prefabId)) { doors++; continue; }
            // Turned boxes are turned: a door frame or a prop at an angle is not the box it would be
            // square on.
            Quaternion rot = e.TryGetProperty("Rotation", out var rj) ? ReadQuat(rj) : Quaternion.Identity;
            var ent = world.Create(
                new Transform { Position = pos, Rotation = rot },
                new ColliderComponent { Size = size, IsSolid = true, Shape = ColliderShape.Box },
                new MaterialComponent { Material = p.Material });
            grid.AddOverlapping(pos, size, rot, ent, isStatic: true);
            boxes++;
        }
        var s = ReadVec(root.GetProperty("SpawnPoint").GetProperty("Position"));
        return s;
    }

    private static Vector3 ReadVec(JsonElement v) => new(
        v.TryGetProperty("X", out var x) ? x.GetSingle() : 0f,
        v.TryGetProperty("Y", out var y) ? y.GetSingle() : 0f,
        v.TryGetProperty("Z", out var z) ? z.GetSingle() : 0f);

    private static Quaternion ReadQuat(JsonElement v) => new(
        v.TryGetProperty("X", out var x) ? x.GetSingle() : 0f,
        v.TryGetProperty("Y", out var y) ? y.GetSingle() : 0f,
        v.TryGetProperty("Z", out var z) ? z.GetSingle() : 0f,
        v.TryGetProperty("W", out var w) ? w.GetSingle() : 1f);

    /// <summary>The named place holding a point: the smallest box, as SpatialService.GetRegionAt picks.</summary>
    private static string ZoneAt(List<(string Name, Vector3 Min, Vector3 Max)> zones, Vector3 p)
    {
        string best = "";
        float bestVolume = float.MaxValue;
        foreach (var (name, min, max) in zones)
        {
            if (p.X < min.X || p.Y < min.Y || p.Z < min.Z || p.X > max.X || p.Y > max.Y || p.Z > max.Z) continue;
            var d = max - min;
            float v = d.X * d.Y * d.Z;
            if (v < bestVolume) { bestVolume = v; best = name; }
        }
        return best;
    }

    /// <summary>"x,z;x,z;..." as points on the ground.</summary>
    private static List<Vector3> Route(string? s)
    {
        var points = new List<Vector3>();
        if (string.IsNullOrWhiteSpace(s)) return points;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (Vec2(part) is { } v) points.Add(v);
        return points;
    }

    private static Vector3? Vec2(string? s)
    {
        if (s == null) return null;
        var p = s.Split(',');
        return new Vector3(float.Parse(p[0], CultureInfo.InvariantCulture), 0f,
                           float.Parse(p[1], CultureInfo.InvariantCulture));
    }


    private static string? Str(string[] args, string name)
    {
        foreach (var a in args) if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return a[(name.Length + 1)..];
        return null;
    }

    private static float Num(string[] args, string name, float fallback)
        => float.TryParse(Str(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
