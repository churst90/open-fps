using System.Numerics;
using System.Text;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Systems;

namespace OpenFPS.Instruments;

/// <summary>The driving, traffic and rail replays: state tracks of what the server's vehicle steppers do.</summary>
public static partial class PhysicsReplays
{
    /// <summary>
    /// One drive. <see cref="At"/> is in player coordinates; a NaN height is the floor there. Script steps
    /// are a count of ticks after the keys held: <c>w</c> throttle, <c>s</c> brake (reverse once stopped),
    /// <c>a</c>/<c>d</c> steer, <c>j</c> the handbrake key, <c>-</c> hands off; <c>wd40</c> is throttle and
    /// right for 40 ticks. The engine is switched on at the first tick and cranks for its second.
    /// </summary>
    public sealed record DriveScript(string Name, string Map, Vector3 At, float Heading, string Preset, string Script, string Note);

    public static readonly DriveScript[] Drives =
    {
        new("city_main_street", "city", new(3f, -120f, 0.05f), 0f, "i4_economy", "-30 w240 -60 s90 -15 s60 -15",
            "up Main Street northbound: crank, eight seconds' throttle, coast, brake to a stop, reverse"),
        new("city_turn", "city", new(3f, -40f, 0.05f), 0f, "i4_economy", "-30 w90 wd30 w45 wa20 -30 s60 -15",
            "away from a standstill, right across the Central Street junction at speed, and a correction left"),
        new("city_wall", "city", new(-3f, -200f, 0.05f), 180f, "v6", "-30 w90 -15 wd60 -30",
            "south on Main Street and steered into the tunnel's west wall: the stop and the impact"),
        new("magnolia_hill", "magnolia_tx", new(-66.2f, 213.2f, float.NaN), 0f, "i4_economy", "-30 w120 -30 s45 -15",
            "up the steepest smooth ground near Magnolia's spawn (7 % on dirt), the pose pitching with it"),
    };

    public const string DriveHeader = "tick\tkeys\tx\ty\tz\theading\tspeed\tsteer\tthrottle\tbrake\tqx\tqy\tqz\tqw\tdemand\tthen per wheel: load_daN angular slip_ratio slip_angle surface demand water";

    /// <summary>One wheel as the wire carries it: load daN, angular speed, slip ratio and angle, surface, demand, water.</summary>
    private static string Wheel(in WheelState w) => $"{w.LoadDaN}\t{w.AngularSpeed}\t{w.SlipRatio}\t{w.SlipAngle}\t{w.Surface}\t{w.Demand}\t{w.Water}";

    private static string Wheels(WheelState[]? wheels) => wheels == null ? "-" : string.Join('\t', wheels.Select(w => Wheel(w)));

    /// <summary>A drive taken through DrivingSystem.Update, as the server's tick runs it for a seated driver.</summary>
    public static string Drive(LoadedMap map, DriveScript script)
    {
        var ecs = map.World;
        var grid = map.Grid;
        var start = StartOf(map, script.At);
        float yaw = script.Heading * MathF.PI / 180f;
        var profile = MachineRegistry.VehicleFor(script.Preset);
        var car = ecs.Create(
            new Transform { Position = start, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0), Scale = Vector3.One },
            new DriveComponent { Preset = script.Preset, Heading = yaw },
            new Velocity(), new MaterialComponent { Material = "Metal" },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(profile.WidthMetres, profile.HeightMetres, profile.LengthMetres), IsSolid = true });
        map.Maps.IndexEntity(map.Id, car);
        var driver = ecs.Create(new Transform { Position = start, Rotation = Quaternion.Identity, Scale = Vector3.One },
                                new OccupantComponent { RootEntityId = car.Id, SeatIndex = 0, Controls = true });
        var sb = new StringBuilder();
        sb.Append("# ").Append(script.Name).Append(": ").Append(script.Note).Append('\n');
        sb.Append("# map ").Append(script.Map).Append(", ").Append(script.Preset).Append(", start ").Append(F(start.X, 3)).Append(' ')
          .Append(F(start.Y, 3)).Append(' ').Append(F(start.Z, 3)).Append(", heading ").Append(F(script.Heading, 0))
          .Append(", script ").Append(script.Script).Append('\n');
        sb.Append(DriveHeader).Append('\n');
        try
        {
            DrivingSystem.SetIgnition(ecs, car, true, null);
            float dt = PhysicsConstants.FixedDeltaTime;
            int tick = 0;
            foreach (var token in script.Script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int digits = token.Length;
                while (digits > 0 && char.IsDigit(token[digits - 1])) digits--;
                string keys = token[..digits];
                int n = int.Parse(token[digits..], Inv);
                var move = Vector3.Zero;
                foreach (char k in keys)
                    move += k switch { 'w' => Vector3.UnitZ, 's' => -Vector3.UnitZ, 'a' => -Vector3.UnitX, 'd' => Vector3.UnitX, _ => Vector3.Zero };
                for (int i = 0; i < n; i++)
                {
                    tick++;
                    grid.Clear();
                    ecs.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                        (Entity e, ref Transform t, ref ColliderComponent c) => grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));
                    DrivingSystem.ApplyControls(ecs, car, new ClientInputUpdate { SequenceId = tick, DeltaTime = dt, MoveDirection = move, Jump = keys.Contains('j') });
                    DrivingSystem.Update(ecs, grid, map.Data.WalkMin, map.Data.WalkMax, dt);
                    var t = ecs.Get<Transform>(car);
                    var d = ecs.Get<DriveComponent>(car);
                    DrivingSystem.TryGetWheels(ecs, car, out var wheels);
                    sb.Append(tick).Append('\t').Append(keys).Append('\t')
                      .Append(F(t.Position.X, 4)).Append('\t').Append(F(t.Position.Y, 4)).Append('\t').Append(F(t.Position.Z, 4)).Append('\t')
                      .Append(F(d.Heading, 5)).Append('\t').Append(F(d.Speed, 4)).Append('\t').Append(F(d.Steer, 4)).Append('\t')
                      .Append(F(d.Throttle, 3)).Append('\t').Append(F(d.Brake, 3)).Append('\t')
                      .Append(F(t.Rotation.X, 5)).Append('\t').Append(F(t.Rotation.Y, 5)).Append('\t').Append(F(t.Rotation.Z, 5)).Append('\t')
                      .Append(F(t.Rotation.W, 5)).Append('\t').Append(F(d.TyreDemand, 4)).Append('\t').Append(Wheels(wheels)).Append('\n');
                }
            }
        }
        finally
        {
            ecs.Destroy(driver);
            ecs.Destroy(car);
            grid.Clear();
            DrivingSystem.Update(ecs, grid, map.Data.WalkMin, map.Data.WalkMax, PhysicsConstants.FixedDeltaTime);   // forgets the car
        }
        return sb.ToString();
    }

    public const string MoverHeader = "second\tid\tname\tx\ty\tz\tyaw\tspeed\tthen per wheel: load_daN angular slip_ratio slip_angle surface demand water";

    /// <summary>
    /// The city's traffic for <paramref name="seconds"/> on a freshly loaded map, through VehicleSystem as
    /// the server's tick runs it; every moving thing sampled once a second.
    /// </summary>
    public static string Traffic(LoadedMap map, double seconds = 60)
    {
        var vehicles = new VehicleSystem();
        vehicles.Spawn(map.Maps);
        return Movers(map, seconds, dt => vehicles.Update(map.Id, map.World, dt), id => vehicles.TryGetWheels(map.Id, id, out var w) ? w : null);
    }

    /// <summary>The city's trains alone, through RailSystem, sampled the same way.</summary>
    public static string Rail(LoadedMap map, double seconds = 90)
    {
        var rail = new RailSystem();
        rail.Spawn(map.Maps);
        return Movers(map, seconds, dt => rail.Update(map.Id, map.World, dt), _ => null);
    }

    private static string Movers(LoadedMap map, double seconds, Action<float> step, Func<int, WheelState[]?> wheels)
    {
        var ecs = map.World;
        float dt = PhysicsConstants.FixedDeltaTime;
        var rows = new List<(int Second, int Id, string Line)>();
        var first = new Dictionary<int, Vector3>();
        var moved = new HashSet<int>();
        int ticks = (int)Math.Round(seconds * PhysicsConstants.TickRate);
        for (int tick = 1; tick <= ticks; tick++)
        {
            step(dt);
            if (tick % PhysicsConstants.TickRate != 0) continue;
            int second = tick / PhysicsConstants.TickRate;
            ecs.Query(new QueryDescription().WithAll<Transform, Velocity>().WithNone<PlayerComponent>(), (Entity e, ref Transform t, ref Velocity v) =>
            {
                if (!first.TryAdd(e.Id, t.Position) && Vector3.DistanceSquared(first[e.Id], t.Position) > 1e-6f) moved.Add(e.Id);
                string name = ecs.Has<NameComponent>(e) ? ecs.Get<NameComponent>(e).Name : ecs.Has<IdentityComponent>(e) ? ecs.Get<IdentityComponent>(e).Name : "";
                var f = Vector3.Transform(Vector3.UnitZ, t.Rotation);
                float yaw = MathF.Atan2(f.X, f.Z);
                rows.Add((second, e.Id, $"{second}\t{e.Id}\t{name}\t{F(t.Position.X, 3)}\t{F(t.Position.Y, 3)}\t{F(t.Position.Z, 3)}\t{F(yaw, 4)}\t{F(v.Linear.Length(), 3)}\t{Wheels(wheels(e.Id))}"));
            });
        }
        var sb = new StringBuilder(MoverHeader).Append('\n');
        foreach (var r in rows.Where(r => moved.Contains(r.Id)).OrderBy(r => r.Second).ThenBy(r => r.Id)) sb.Append(r.Line).Append('\n');
        return sb.ToString();
    }
}
