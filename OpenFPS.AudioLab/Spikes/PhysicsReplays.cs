using System.Globalization;
using System.Numerics;
using System.Text;
using Arch.Core;
using OpenFPS.Client.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Geometry;
using OpenFPS.Materials;

namespace OpenFPS.Instruments;

/// <summary>
/// The physics guards of docs/PHYSICS_BOUNDARY.md stage 0, shared by AudioLab (--physics-replay,
/// --physics-parity) and OpenFPS.Tests (MovementReplayTests, DrivingReplayTests, RailReplayTests,
/// BallisticsTableTests, RicochetTableTests), which store what these produce and compare a run with it.
///
/// <para>A walk is --walk-pair's: one script of keys from one spot, taken by the server's
/// MovementSystem.Update with a real session and by the client's ClientPhysicsSystem.Predict over the
/// client's world as a streamed client holds it. Beside the two bodies it measures the three parity risks
/// the survey read from the code (section 3.7): the two sides' floors, the ground memo's answers against a
/// fresh probe, and the two sides' gathers. Stage 3 closed all three (one view of the world both fill,
/// docs/PHYSICS_BOUNDARY.md 17); the measures stay, to say so on every walk.</para>
/// </summary>
public static partial class PhysicsReplays
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── Maps ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One map as the server loads it, with what the walks opened on it.</summary>
    public sealed class LoadedMap
    {
        public required string Id;
        public required MapManager Maps;
        public required World World;
        public required SpatialGrid<Entity> Grid;
        public required Dictionary<int, Entity> Lookup;
        public required MapData Data;
    }

    /// <summary>Where the maps are: the shipped maps folder (city.json) and the places folder
    /// (magnolia_tx.json and its .elevation).</summary>
    public readonly record struct MapFolders(string Maps, string Places, string Prefabs);

    /// <summary>Doors each map's walks go through, by the map file's ids, opened once when it loads so
    /// every walk meets the same world whichever runs first.</summary>
    private static readonly Dictionary<string, (int Door, Vector3 From)[]> DoorsOpened = new()
    {
        // Brandt Court's glass front door from the street, and the stairwell's steel door from the lobby.
        ["city"] = new[] { (4129, new Vector3(-5f, 0f, 157f)), (4089, new Vector3(-15f, 0f, 157f)) },
    };

    /// <summary>A map loaded alone from a copy of its file, as the server loads it; its doors opened.</summary>
    public static LoadedMap Load(string mapId, MapFolders folders)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-physreplay-" + Guid.NewGuid().ToString("N"));
        try
        {
            string place = Path.Combine(folders.Places, mapId + ".json");
            bool isPlace = File.Exists(place);
            Directory.CreateDirectory(Path.Combine(dir, "maps", "places"));
            if (isPlace)
            {
                File.Copy(place, Path.Combine(dir, "maps", "places", mapId + ".json"));
                string ground = Path.ChangeExtension(place, ".elevation");
                if (File.Exists(ground)) File.Copy(ground, Path.Combine(dir, "maps", "places", mapId + ".elevation"));
            }
            else File.Copy(Path.Combine(folders.Maps, mapId + ".json"), Path.Combine(dir, "maps", mapId + ".json"));
            AcousticRegistry.Initialize();
            var maps = new MapManager(new MapRepository(Path.Combine(dir, "maps")), new PrefabRepository(folders.Prefabs));
            maps.Initialize();
            if (!maps.TryGetMap(mapId, out var world, out _, out var grid, out var lookup) || !maps.TryGetMapData(mapId, out var data))
                throw new InvalidOperationException($"no map {mapId}");
            var map = new LoadedMap { Id = mapId, Maps = maps, World = world, Grid = grid, Lookup = lookup, Data = data };
            if (DoorsOpened.TryGetValue(mapId, out var doors))
            {
                var authored = maps.AuthoredEntities(mapId);
                foreach (var (id, from) in doors)
                    if (!authored.TryGetValue(id, out var door) || !world.Has<DoorComponent>(door) || !DoorSystem.Set(world, door, open: true, by: from))
                        throw new InvalidOperationException($"door #{id} on {mapId} did not open");
                var system = new DoorSystem();
                for (int i = 0; i < 90; i++) system.Update(world, PhysicsConstants.FixedDeltaTime, _ => { });
            }
            return map;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ── Scripts ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A solid box that moves (a parked car, a bus standing at a stop), put down for one walk: filed in the
    /// server's grid as moving things are each tick, and sent to the client as a definition.
    /// </summary>
    public readonly record struct Prop(string Name, Vector3 Centre, Vector3 Size, string Material);

    /// <summary>
    /// One walk. <see cref="At"/> is in player coordinates (east, north, height) as /tp takes them, heading 0
    /// north and 90 east. The script is space-separated steps, each a count of ticks:
    /// <c>-N</c> no key; <c>wN</c> (a, s, d) a key held, upper case sprinting; <c>wjN</c> the key held and a
    /// jump on the first tick; <c>jN</c> a jump on the first tick and no key; <c>twN</c> N taps of the key, one
    /// tick down and nine up (one tap is 15 cm).
    /// </summary>
    public sealed record WalkScript(string Name, string Map, Vector3 At, float Heading, string Script, string Note)
    {
        public Prop[] Props { get; init; } = Array.Empty<Prop>();
        /// <summary>The largest gap between server and client this walk is known to have today, mm, and why
        /// (a parity risk of section 3.7, recorded and not fixed in stage 0). Zero: held to 1 mm.</summary>
        public float KnownGapMm { get; init; }
        public string KnownWhy { get; init; } = "";
    }

    /// <summary>The stored walks: held keys and taps, a sprint, a kerb, stairs both ways, a doorway and a
    /// lobby, a jump, a fall off a roof, a parked car under the feet and a bus beside them on the city; the
    /// ground of a real place, outdoors and in a house, on Magnolia.</summary>
    public static readonly WalkScript[] Walks =
    {
        new("city_street_held", "city", new(60f, 122f, 0.12f), 90f, "-15 w90 -15", "W held east along the plaza from the spawn"),
        new("city_street_taps", "city", new(60f, 122f, 0.12f), 90f, "-15 tw12 -10", "twelve taps of W east from the spawn"),
        new("city_street_sprint", "city", new(60f, 122f, 0.12f), 270f, "-15 W60 -15", "a sprint west along the plaza"),
        new("city_strafe_taps", "city", new(60f, 122f, 0.12f), 0f, "-15 ta6 td6 ts6 -10", "taps of A, D and S"),
        new("city_kerb", "city", new(60f, 122f, 0.12f), 0f, "-15 w30 -10 s30 -10", "off the 7 cm kerb onto Foundry Street and back up it"),
        new("city_jump", "city", new(60f, 122f, 0.12f), 90f, "-15 w15 wj30 -10 j40", "a running jump, then one standing"),
        new("city_doorway_lobby", "city", new(-5f, 157.08f, 0.1f), 270f, "-15 w100 -10",
            "in through Brandt Court's front door, across the lobby, through the stairwell door"),
        new("city_stairs_up", "city", new(-13.05f, 156.4f, 0.1f), 180f, "-15 w80 -10", "up Brandt Court's first flight, nineteen steps"),
        new("city_stairs_down", "city", new(-13.05f, 149.4f, 3.4f), 0f, "-15 w60 -10", "down the same flight from its top"),
        new("city_roof_fall", "city", new(-31.4f, 191.884f, 18.25f), 270f, "-15 w60 -60",
            "off Brandt Court's west side at roof height: eighteen metres down (RoofEdgeFallTests' spot)"),
        // Until physics stage 3 the client's ground probe counted nothing that moves, and this walk parted the two
        // sides by 39 mm on every tick on the roof (KnownGapMm 40): risk 1, closed by the one view both fill.
        new("city_parked_car", "city", new(70f, 127f, 1.6f), 0f, "-15 w30 -30", "standing on a parked car and walking off its end")
        {
            Props = new[] { new Prop("parked car", new Vector3(70f, 0.8f, 128f), new Vector3(1.8f, 1.5f, 4.6f), "Metal") },
        },
        new("city_bus_side", "city", new(70f, 127.2f, 0.05f), 0f, "-15 w30 d30 -10", "into the side of a bus at a stop, then along it")
        {
            Props = new[] { new Prop("bus", new Vector3(76f, 1.65f, 131f), new Vector3(12f, 3.2f, 2.5f), "Metal") },
        },
        new("magnolia_spawn_held", "magnolia_tx", new(float.NaN, float.NaN, float.NaN), 0f, "-15 w120 -15", "W held north from the spawn over the ground"),
        new("magnolia_spawn_taps", "magnolia_tx", new(float.NaN, float.NaN, float.NaN), 90f, "-15 tw12 -10", "twelve taps east from the spawn"),
        new("magnolia_house", "magnolia_tx", new(-301.4f, -268.7f, 5.2f), 0f, "-15 w60 -10 s60 -10", "about the house at 19617 Turtle Dove Lane"),
        new("magnolia_barn", "magnolia_tx", new(-88.8f, -253.0f, 3.9f), 90f, "-15 w90 -10 a60 -10", "about the barn at 19415 Turtle Dove Lane"),
        new("magnolia_slope_up", "magnolia_tx", new(-66.2f, 218.2f, float.NaN), 0f, "-15 w140 -10",
            "up the steepest smooth ground near the spawn: 2.1 m in 30 m (--physics-replay hills)"),
        new("magnolia_slope_down", "magnolia_tx", new(-66.2f, 248.2f, float.NaN), 180f, "-15 tw10 -10 W60 -10", "down it, in taps and then running"),
        new("city_bus_roof", "city", new(73f, 131f, 3.3f), 90f, "-15 tw4 -10 w75 -30",
            "standing on a bus's roof at a stop, four taps along it, then off its east end: three metres down")
        {
            Props = new[] { new Prop("bus", new Vector3(76f, 1.65f, 131f), new Vector3(12f, 3.2f, 2.5f), "Metal") },
        },
        new("city_two_cars", "city", new(68.6f, 125.5f, 0.05f), 20f, "-15 w60 -10",
            "pressed along the sides of two parked cars nose to tail, across the joint between them")
        {
            Props = new[]
            {
                new Prop("parked car, front", new Vector3(70f, 0.8f, 128f), new Vector3(1.8f, 1.5f, 4.6f), "Metal"),
                new Prop("parked car, behind", new Vector3(70f, 0.8f, 132.7f), new Vector3(1.8f, 1.5f, 4.6f), "Metal"),
            },
        },
    };

    /// <summary>Where a walk starts, in engine coordinates: the spawn when none is given, and the floor
    /// under the point when no height is.</summary>
    public static Vector3 StartOf(LoadedMap map, Vector3 at)
    {
        if (float.IsNaN(at.X)) return map.Maps.GetSpawnPoint(map.Id).Position;
        if (!float.IsNaN(at.Z)) return PlayerCoordinates.ToWorld(at.X, at.Y, at.Z);
        var any = new OpenFPS.Geometry.AcceptAll();
        float floor = map.Grid.Geometry!.FloorAt(at.X, at.Y, 200f, OpenFPS.Geometry.GeometryLayers.Ground, ref any, out _);
        return new Vector3(at.X, floor + 0.05f, at.Y);
    }

    /// <summary>One tick's input from a script, and its label in the track.</summary>
    public readonly record struct Step(Vector3 Move, bool Sprint, bool Jump, string Keys);

    private static Vector3 KeyVector(char k) => char.ToLowerInvariant(k) switch
    {
        'a' => new Vector3(-1, 0, 0), 'd' => new Vector3(1, 0, 0), 's' => new Vector3(0, 0, -1), 'w' => new Vector3(0, 0, 1),
        _ => throw new FormatException($"no key {k}"),
    };

    /// <summary>A script's ticks, in order.</summary>
    public static List<Step> Expand(string script)
    {
        var steps = new List<Step>();
        foreach (var token in script.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int digits = token.Length;
            while (digits > 0 && char.IsDigit(token[digits - 1])) digits--;
            string head = token[..digits];
            int n = int.Parse(token[digits..], Inv);
            if (head == "-") { for (int i = 0; i < n; i++) steps.Add(new Step(Vector3.Zero, false, false, "-")); }
            else if (head == "j") { for (int i = 0; i < n; i++) steps.Add(new Step(Vector3.Zero, false, i == 0, i == 0 ? "j" : "-")); }
            else if (head.Length == 2 && head[0] == 't')
            {
                for (int tap = 0; tap < n; tap++)
                    for (int i = 0; i < 10; i++)
                        steps.Add(i == 0 ? new Step(KeyVector(head[1]), false, false, head[1].ToString()) : new Step(Vector3.Zero, false, false, "-"));
            }
            else if (head.Length is 1 or 2 && (head.Length == 1 || head[1] == 'j'))
            {
                bool sprint = char.IsUpper(head[0]);
                for (int i = 0; i < n; i++)
                {
                    bool jump = head.Length == 2 && i == 0;
                    steps.Add(new Step(KeyVector(head[0]), sprint, jump, head[0] + (jump ? "j" : "")));
                }
            }
            else throw new FormatException($"walk script step '{token}'");
        }
        return steps;
    }

    // ── A walk ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One tick of a walk: the server's body after it, and the client's.</summary>
    public readonly record struct WalkTick(int Tick, string Keys, Vector3 Server, Vector3 ServerVelocity, bool Grounded, Vector3 Client)
    {
        public float GapMetres => Vector3.Distance(Server, Client);
    }

    /// <summary>A walk taken, with the three parity risks measured on it.</summary>
    public sealed class WalkRun
    {
        public required WalkScript Script;
        public Vector3 Start;
        public readonly List<WalkTick> Ticks = new();
        public float WorstGapMetres;
        public int GapTicks;
        /// <summary>Risk 1: ticks where the server's fresh ground probe and the client's, asked at the same
        /// point, differ by over 1 mm; of those, the ones where the moving things made the difference.</summary>
        public int GroundDiffers, GroundFromMovers;
        public float WorstGroundDifference;
        /// <summary>Risk 2: ticks where the memo handed back a floor more than 1 mm from a fresh probe, the
        /// worst, the memo's hits (the triangle world's floor remembered for the same point), and how far the
        /// server's body went from where it goes with no memo.</summary>
        public int MemoStale, MemoHits;
        public float WorstMemoError, MemoTrackEffect;
        /// <summary>Risk 3: ticks where the two sides gathered different boxes (or in another order), and
        /// ticks where that changed the step by over 1 mm, the worst.</summary>
        public int GatherDiffers, GatherMoves;
        public float WorstGatherEffect;
        /// <summary>Ticks where this rig's copy of the server's step disagreed with MovementSystem: must be 0,
        /// or the risk measures describe some other step.</summary>
        public int ReproductionMisses;
        /// <summary>The two gathers' entities the first time they differed, for the lab.</summary>
        public string? FirstGatherDifference;

        public float Metres => Ticks.Count == 0 ? 0f
            : Vector2.Distance(new Vector2(Start.X, Start.Z), new Vector2(Ticks[^1].Server.X, Ticks[^1].Server.Z));
    }

    /// <summary>Takes a walk on a loaded map. The map is left as it was found.</summary>
    public static WalkRun Walk(LoadedMap map, WalkScript script)
    {
        var run = WalkOnce(map, script, memo: true, measure: true);
        var bare = WalkOnce(map, script, memo: false, measure: false);
        for (int i = 0; i < run.Ticks.Count && i < bare.Ticks.Count; i++)
            run.MemoTrackEffect = MathF.Max(run.MemoTrackEffect, Vector3.Distance(run.Ticks[i].Server, bare.Ticks[i].Server));
        return run;
    }

    private static WalkRun WalkOnce(LoadedMap map, WalkScript script, bool memo, bool measure)
    {
        var ecs = map.World;
        var grid = map.Grid;
        var data = map.Data;
        var start = StartOf(map, script.At);
        float yaw = script.Heading * MathF.PI / 180f;
        var run = new WalkRun { Script = script, Start = start };

        // The props, as moving solids on both sides.
        var props = new List<Entity>();
        foreach (var p in script.Props)
        {
            var e = ecs.Create(
                new Transform { Position = p.Centre, Rotation = Quaternion.Identity, Scale = Vector3.One },
                new Velocity(), new MaterialComponent { Material = p.Material },
                new IdentityComponent { Name = p.Name, PrefabId = "prop" },
                new ColliderComponent { Shape = ColliderShape.Box, Size = p.Size, IsSolid = true });
            map.Maps.IndexEntity(map.Id, e);
            props.Add(e);
        }

        // The body and its session, as a login makes them (Program.cs, the join).
        const int conn = 1;
        var sessions = new SessionManager();
        var session = new UserSession { ConnectionId = conn, Username = "walker", CurrentMapId = map.Id };
        sessions.AddSession(conn, session);
        var body = ecs.Create(
            new PlayerComponent { Username = "walker", ConnectionId = conn, Yaw = yaw },
            EntityType.Player,
            new Transform { Position = start, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0), Scale = Vector3.One },
            new Velocity(), new NameComponent { Name = "walker" },
            new MaterialComponent { Material = PhysicsConstants.PersonMaterial },
            new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(PhysicsConstants.PlayerRadius * 2, PhysicsConstants.PlayerHeight, PhysicsConstants.PlayerRadius * 2), IsSolid = true });
        map.Maps.IndexEntity(map.Id, body);

        try
        {
            var snap = ClientWorld(map, start, props);
            var state = new LocalPlayerState { Position = start, Yaw = yaw, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0) };
            var physics = new ClientPhysicsSystem(state, new SpatialService()) { OwnEntityId = body.Id, MapMin = data.WalkMin, MapMax = data.WalkMax };

            float dt = PhysicsConstants.FixedDeltaTime;
            var steps = Expand(script.Script);
            for (int tick = 1; tick <= steps.Count; tick++)
            {
                var step = steps[tick - 1];
                var input = new ClientInputUpdate { SequenceId = tick, DeltaTime = dt, Sprint = step.Sprint, Jump = step.Jump, MoveDirection = step.Move };
                session.InputQueue.Enqueue(input);

                // The moving things filed again, as the server's tick does before it moves anybody.
                grid.Clear();
                ecs.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(),
                    (Entity e, ref Transform t, ref ColliderComponent c) => grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false));

                // With no memo, the floor is asked afresh every tick.
                if (!memo) session.GroundProbe.Invalidate();
                var before = ecs.Get<Transform>(body).Position;
                var velBefore = ecs.Get<Velocity>(body).Linear;
                long hitsBefore = session.GroundProbe.Hits;

                Expected? mine = measure ? Measure(map, session, body, before, velBefore, step, snap, run) : null;

                MovementSystem.Update(ecs, data.WalkMin, data.WalkMax, grid, map.Lookup, sessions, map.Maps, dt);
                if (session.GroundProbe.Hits > hitsBefore) run.MemoHits++;

                // Each step from the server's state, as the reconciler replays from it: the gap is what one
                // step on each side makes of the same body, so a parting is seen on the tick it happens.
                state.Position = before;
                state.Velocity = velBefore;
                physics.Predict(input, snap, dt);
                var server = ecs.Get<Transform>(body).Position;
                var row = new WalkTick(tick, step.Keys, server, ecs.Get<Velocity>(body).Linear, ecs.Get<PlayerComponent>(body).IsGrounded, state.Position);
                run.Ticks.Add(row);
                if (mine is { } m && Vector3.Distance(m.Position, server) > 1e-6f) run.ReproductionMisses++;
                float gap = row.GapMetres;
                run.WorstGapMetres = MathF.Max(run.WorstGapMetres, gap);
                if (gap > 0.001f) run.GapTicks++;
            }
        }
        finally
        {
            ecs.Destroy(body);
            foreach (var p in props) ecs.Destroy(p);
            grid.Clear();
        }
        return run;
    }

    private readonly record struct Expected(Vector3 Position);

    /// <summary>
    /// The three risks at one tick, from the body where it stands before the tick, and this rig's own copy
    /// of the server's step (MovementSystem's ground, gather and context) to check against the real one.
    /// </summary>
    private static Expected Measure(LoadedMap map, UserSession session, Entity body, Vector3 at, Vector3 velocity, Step step,
                                    WorldSnapshot snap, WalkRun run)
    {
        var ecs = map.World;
        var grid = map.Grid;

        // Risk 1: the ground each side reads at the same point, and the server's without what moves.
        float serverGround = PhysicsUtils.GetGroundHeight(ecs, grid, at, out _);
        float clientGround = PhysicsUtils.GetGroundHeight(snap, at, body.Id, out _);
        var movers = new List<Entity>();
        grid.CollectDynamicInRadius(at, 50f, movers, new HashSet<Entity>());
        movers.RemoveAll(e => e == body);
        float withoutMovers = PhysicsUtils.GetGroundHeight(ecs, grid, at, movers, out _);
        float differ = MathF.Abs(serverGround - clientGround);
        if (differ > 0.001f)
        {
            run.GroundDiffers++;
            if (MathF.Abs(serverGround - withoutMovers) > 0.001f) run.GroundFromMovers++;
            run.WorstGroundDifference = MathF.Max(run.WorstGroundDifference, differ);
        }

        // Risk 2: what the memo will hand MovementSystem this tick, against a fresh probe.
        var copy = session.GroundProbe;
        float remembered = PhysicsUtils.GetGroundHeight(ecs, grid, at, body.Id, ref copy, out _);
        float stale = MathF.Abs(remembered - serverGround);
        if (stale > 0.001f) run.MemoStale++;
        run.WorstMemoError = MathF.Max(run.WorstMemoError, stale);

        // Risk 3: the boxes each side gathers, and the step each set gives from the server's state.
        var ctx = ServerContext(map, body, at, velocity, step, remembered, out var geometry, out var solids);
        var serverBoxes = ServerGather(map, body, at);
        var clientBoxes = ClientGather(snap, at, body.Id);
        var obstacles = new SharedMovementEngine.Obstacles(serverBoxes.Boxes.ToArray(), geometry, solids.ToArray());
        var result = SharedMovementEngine.Step(ctx, obstacles, out _);
        if (!serverBoxes.Ids.SequenceEqual(clientBoxes.Ids))
        {
            run.GatherDiffers++;
            run.FirstGatherDifference ??= $"server [{string.Join(' ', serverBoxes.Ids)}], client [{string.Join(' ', clientBoxes.Ids)}]";
            var other = SharedMovementEngine.Step(ctx, new SharedMovementEngine.Obstacles(clientBoxes.Boxes.ToArray(), geometry, solids.ToArray()), out _);
            float moved = Vector3.Distance(result.NewPosition, other.NewPosition);
            if (moved > 0.001f) run.GatherMoves++;
            run.WorstGatherEffect = MathF.Max(run.WorstGatherEffect, moved);
        }
        return new Expected(result.NewPosition);
    }

    /// <summary>MovementSystem's context for one input, and what OnFoot.Step adds to it on the triangle world
    /// (the capsule, the grade, the solids gathered).</summary>
    public static SharedMovementEngine.MovementContext ServerContext(LoadedMap map, Entity body, Vector3 at, Vector3 velocity, Step step,
                                                                     float ground, out OpenFPS.Geometry.TriangleWorld? geometry,
                                                                     out List<OpenFPS.Geometry.SolidRef> solids)
    {
        var ecs = map.World;
        ref var player = ref ecs.Get<PlayerComponent>(body);
        float gravity = PhysicsConstants.Gravity;
        ecs.Query(new QueryDescription().WithAll<ZoneComponent>(), (ref ZoneComponent zone) => gravity = zone.Gravity);
        var move = step.Move;
        if (move.LengthSquared() > 1f) move = Vector3.Normalize(move);
        var inputDir = move == Vector3.Zero ? Vector3.Zero : Vector3.Transform(move, Quaternion.CreateFromYawPitchRoll(player.Yaw, 0, 0));
        var ctx = new SharedMovementEngine.MovementContext
        {
            Position = at, Velocity = velocity, InputDirection = inputDir, DeltaTime = PhysicsConstants.FixedDeltaTime,
            GroundHeight = ground, Gravity = gravity, JumpForce = PhysicsConstants.JumpPower,
            Speed = PhysicsConstants.FootSpeed(step.Sprint, HandsService.SpeedLimit(ecs, body, map.Lookup)),
            PlayerRadius = PhysicsConstants.PlayerRadius, PlayerHeight = PhysicsConstants.PlayerHeight, StepHeight = PhysicsConstants.StepHeight,
            IsJumpRequested = step.Jump, MapMin = map.Data.WalkMin, MapMax = map.Data.WalkMax,
        };
        solids = new List<OpenFPS.Geometry.SolidRef>();
        var view = ServerPhysicsView.Of(ecs, map.Grid);
        geometry = view.Triangles;
        if (geometry != null)
        {
            var self = new BodyFilter(view, body.Id);
            ctx.Body = OpenFPS.Geometry.BodyShape.Capsule;
            ctx.Grade = SharedMovementEngine.GradeAlong(geometry, ref self, at, inputDir);
            SharedMovementEngine.GatherSolids(ctx, geometry, ref self, solids);
        }
        return ctx;
    }

    /// <summary>The boxes a step is given, with their entities in the order given.</summary>
    public sealed record Gathered(List<int> Ids, List<SharedMovementEngine.Collider> Boxes);

    /// <summary>The server's gather (OnFoot.Gather over the server's view): the grid's moving half and the
    /// statics the triangles do not hold, by id.</summary>
    public static Gathered ServerGather(LoadedMap map, Entity body, Vector3 at)
        => Gather(ServerPhysicsView.Of(map.World, map.Grid), body.Id, at);

    /// <summary>A client's gather (OnFoot.Gather over its snapshot's view): the statics the triangles do not
    /// hold and every moving solid near, by id. Before stage 3 it was a gather of its own, in the snapshot's
    /// order and by another rule of near.</summary>
    public static Gathered ClientGather(WorldSnapshot snap, Vector3 at, int own)
        => Gather(SnapshotPhysicsView.Of(snap), own, at);

    private static Gathered Gather(IPhysicsWorld world, int own, Vector3 at)
    {
        var bodies = new List<PhysicsBody>();
        OnFoot.Gather(world, own, at, bodies);
        return new Gathered(bodies.Select(b => b.Id).ToList(), bodies.Select(b => b.Collider).ToList());
    }

    /// <summary>
    /// The client's world standing at <paramref name="at"/>: the tiles a streamed client holds there (or every
    /// static on a map without tiles), and the moving things it has been told of.
    /// </summary>
    public static WorldSnapshot ClientWorld(LoadedMap map, Vector3 at, IEnumerable<Entity> movers, bool full = false)
    {
        var ecs = map.World;
        var defs = new List<EntityDefinition>();
        if (!full && map.Data.TileMetres > 0 && map.Maps.TryGetTiles(map.Id, out var tiles))
        {
            var interest = new TileInterest { Radii = StreamRadii.Default };
            foreach (int id in TileStreamer.Begin(interest, tiles, at))
                if (map.Lookup.TryGetValue(id, out var e) || tiles.TryGetGlobal(id, out e)) defs.Add(TileStreamer.Definition(ecs, e, tiles, interest));
        }
        else defs = EntityDefinitionFactory.StaticDefinitions(ecs);
        foreach (var m in movers) defs.Add(EntityDefinitionFactory.From(ecs, m));
        var state = new ClientWorldState();
        state.Geometry.Runner = work => work();
        state.Clear(new Vector3(4000f, 400f, 4000f));
        state.ConfigureAcoustics(Vector3.Zero, 0.5f, 0.2f, map.Data.TileMetres);
        foreach (var def in defs) state.RegisterDefinition(def);
        var built = state.GetSnapshot();
        var fresh = state.GetSnapshot();
        return fresh.Geometry != null ? fresh : built;
    }

    // ── Stored form ──────────────────────────────────────────────────────────────────────────────────

    public const string WalkHeader = "tick\tkeys\tx\ty\tz\tvx\tvy\tvz\tgrounded\tgap_mm";

    /// <summary>A walk's track as it is stored: the server's body each tick (engine x east, y up, z north,
    /// metres) and the gap to the client's.</summary>
    public static string Track(WalkRun run)
    {
        var sb = new StringBuilder();
        var s = run.Script;
        sb.Append("# ").Append(s.Name).Append(": ").Append(s.Note).Append('\n');
        sb.Append("# map ").Append(s.Map).Append(", start ").Append(F(run.Start.X, 3)).Append(' ').Append(F(run.Start.Y, 3)).Append(' ')
          .Append(F(run.Start.Z, 3)).Append(", heading ").Append(F(s.Heading, 0)).Append(", script ").Append(s.Script).Append('\n');
        sb.Append(WalkHeader).Append('\n');
        foreach (var t in run.Ticks)
            sb.Append(t.Tick).Append('\t').Append(t.Keys).Append('\t')
              .Append(F(t.Server.X, 5)).Append('\t').Append(F(t.Server.Y, 5)).Append('\t').Append(F(t.Server.Z, 5)).Append('\t')
              .Append(F(t.ServerVelocity.X, 4)).Append('\t').Append(F(t.ServerVelocity.Y, 4)).Append('\t').Append(F(t.ServerVelocity.Z, 4)).Append('\t')
              .Append(t.Grounded ? 1 : 0).Append('\t').Append(F(t.GapMetres * 1000f, 2)).Append('\n');
        return sb.ToString();
    }

    public const string WalkSummaryHeader =
        "walk\tmap\tticks\tmetres\tworst_gap_mm\tgap_ticks\tground_differs\tground_from_movers\tworst_ground_mm\tmemo_hits\tmemo_stale\tworst_memo_mm\tmemo_track_effect_mm\tgather_differs\tgather_moves\tworst_gather_mm";

    public static string SummaryLine(WalkRun r) => string.Join('\t', r.Script.Name, r.Script.Map, r.Ticks.Count.ToString(Inv), F(r.Metres, 2),
        F(r.WorstGapMetres * 1000f, 2), r.GapTicks.ToString(Inv), r.GroundDiffers.ToString(Inv), r.GroundFromMovers.ToString(Inv),
        F(r.WorstGroundDifference * 1000f, 1), r.MemoHits.ToString(Inv), r.MemoStale.ToString(Inv), F(r.WorstMemoError * 1000f, 1),
        F(r.MemoTrackEffect * 1000f, 1), r.GatherDiffers.ToString(Inv), r.GatherMoves.ToString(Inv), F(r.WorstGatherEffect * 1000f, 1));

    internal static string F(float v, int places) => v.ToString("F" + places.ToString(Inv), Inv);
    internal static string F(double v, int places) => v.ToString("F" + places.ToString(Inv), Inv);

    /// <summary>A stored row read back: its columns, numbers where they parse.</summary>
    public static string[] Columns(string line) => line.Split('\t');

    /// <summary>
    /// Compares a run's text with what is stored, line by line: the same words, and every number within
    /// <paramref name="tolerance"/> of the stored one (the maths library rounds differently on other
    /// machines). Null when they agree; else the first line that does not, both ways.
    /// </summary>
    /// <remarks>An <paramref name="angle"/> column is radians, compared round the circle: a heading due south
    /// is pi on one machine and -pi on another (GitHub's runner, 2026-10-11).</remarks>
    public static string? Compare(string stored, string now, Func<int, double> tolerance, Func<int, bool>? angle = null)
    {
        var a = stored.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var b = now.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length) return $"line {i + 1} is new: {b[i]}";
            if (i >= b.Length) return $"line {i + 1} is gone: {a[i]}";
            if (a[i] == b[i]) continue;
            var ca = a[i].Split('\t');
            var cb = b[i].Split('\t');
            bool same = ca.Length == cb.Length && !a[i].StartsWith('#');
            for (int c = 0; same && c < ca.Length; c++)
            {
                if (ca[c] == cb[c]) continue;
                same = double.TryParse(ca[c], NumberStyles.Float, Inv, out double x) && double.TryParse(cb[c], NumberStyles.Float, Inv, out double y)
                       && (angle != null && angle(c) ? Math.Abs(Math.IEEERemainder(x - y, 2 * Math.PI)) : Math.Abs(x - y)) <= tolerance(c);
            }
            if (!same) return $"line {i + 1}:\n  stored {a[i]}\n  now    {b[i]}";
        }
        return null;
    }
}
