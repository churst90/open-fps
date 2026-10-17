using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Geometry;
using OpenFPS.Materials;
using OpenFPS.Client.Core;

namespace OpenFPS.AudioLab.Spikes;

/// <summary>
/// --walk-pair map=magnolia_tx at=E,N,H [heading=DEG] [key=w|a|s|d] [ticks=90] [taps=N gap=10] [sprint] [full] [from=E,N,H] [open=ID,...] [show=all]
/// --walk-pair map=... room=ID[,ID...]  the standing spot in each room (StandingSpots.InRoom; the map file's ids), as /tp takes it
/// --walk-pair map=... ears=x,y,z[;x,y,z...]  the standing spot nearest each ear the lab measured at (engine coordinates)
///
/// One walk taken twice from the same spot: by the server's MovementSystem.Update with a real session and
/// queued inputs, and by the client's ClientPhysicsSystem.Predict over the client's world as it arrives
/// (the tiles a streamed client holds there, or every definition with full). at= is in player coordinates
/// (east, north, height), heading 0 north and 90 east. Every tick where the two bodies part by more than a
/// centimetre is printed with the ground each side reads and what the client was held off; the server's
/// answer is what the client is pulled back to, so a walk the server refuses is a walk that goes nowhere.
/// </summary>
public static class WalkPairSpike
{
    private static string? Arg(string[] args, string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
    private static float Num(string[] args, string name, float fallback)
        => Arg(args, name) is { } s ? float.Parse(s, CultureInfo.InvariantCulture) : fallback;
    private static string V(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

    public static int Run(string[] args)
    {
        AcousticRegistry.Initialize();
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        string mapId = Arg(args, "map") ?? "magnolia_tx";
        if (!Load(mapId, out var maps)) return 1;
        maps.TryGetMap(mapId, out var ecs, out _, out var grid, out var lookup);
        maps.TryGetMapData(mapId, out var data);

        if (Arg(args, "room") is { } roomArg)
        {
            var authored = maps.AuthoredEntities(mapId);
            foreach (var part in roomArg.Split(','))
            {
                int id = int.Parse(part, CultureInfo.InvariantCulture);
                if (!authored.TryGetValue(id, out var room)) { Console.WriteLine($"  #{id}: no such entity in the map file"); continue; }
                string name = ecs.Has<RegionComponent>(room) ? ecs.Get<RegionComponent>(room).FriendlyName : "";
                if (StandingSpots.InRoom(ecs, grid, room) is { } spot)
                    Console.WriteLine($"  #{id} {name}: /tp {StandingSpots.Typed(spot)}   (engine {V(spot)})");
                else Console.WriteLine($"  #{id} {name}: no standing spot");
            }
            return 0;
        }

        if (Arg(args, "ears") is { } earsArg)
        {
            // An ear the lab measured at (engine coordinates, 1.6 m over its floor): the nearest spot a body
            // stands on within a 4 m square round it, on that floor.
            foreach (var part in earsArg.Split(';'))
            {
                var f = part.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var floor = new Vector3(f[0], f[1] - 1.6f, f[2]);
                var spot = StandingSpots.In(ecs, grid, floor + new Vector3(0, 1.2f, 0), Quaternion.Identity, new Vector3(4f, 2.4f, 4f));
                Console.WriteLine($"  ear {V(new Vector3(f[0], f[1], f[2]))}: {(spot is { } s ? $"/tp {StandingSpots.Typed(s)}   (engine {V(s)})" : "no standing spot")}");
            }
            return 0;
        }

        var start = Arg(args, "at") is { } atArg ? Parse(atArg) : maps.GetSpawnPoint(mapId).Position;
        float yaw = Num(args, "heading", 0f) * MathF.PI / 180f;
        int ticks = (int)Num(args, "ticks", 90f);
        int taps = (int)Num(args, "taps", 0f), gap = (int)Num(args, "gap", 10f);
        if (taps > 0) ticks = taps * gap + 30;
        bool sprint = args.Contains("sprint"), showAll = Arg(args, "show") == "all";
        // key=w|a|s|d: which movement key is held (or tapped), as GatherInput sends it.
        var key = (Arg(args, "key") ?? "w") switch { "a" => new Vector3(-1, 0, 0), "d" => new Vector3(1, 0, 0), "s" => new Vector3(0, 0, -1), _ => new Vector3(0, 0, 1) };

        // open=ID,...: those doors (the map file's ids) swung or slid open first, on both sides.
        if (Arg(args, "open") is { } openArg)
        {
            var authored = maps.AuthoredEntities(mapId);
            var doors = new DoorSystem();
            foreach (var part in openArg.Split(','))
                if (authored.TryGetValue(int.Parse(part, CultureInfo.InvariantCulture), out var door) && ecs.Has<DoorComponent>(door))
                    Console.WriteLine($"  door #{part} {(DoorSystem.Set(ecs, door, open: true, by: start) ? "opens" : "does not open")}");
            for (int i = 0; i < 90; i++) doors.Update(ecs, PhysicsConstants.FixedDeltaTime, _ => { });
        }

        if (MovementSystem.CheckCollision(ecs, grid, start, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight))
            Console.WriteLine($"  the start {V(start)} is solid to the server's teleport check");

        // The client's world: what a streamed client holds standing there, or everything; with from=, what it
        // holds after streaming there from another spot (a /tp across the map: coarse tiles come up to full).
        List<EntityDefinition> defs;
        WorldSnapshot snap;
        if (Arg(args, "from") is { } fromArg && maps.TryGetTiles(mapId, out var moved))
        {
            snap = Streamed(ecs, lookup, moved, data.TileMetres, Parse(fromArg), start, out int count);
            Console.WriteLine($"  client streamed from {fromArg}: {count:N0} definitions");
        }
        else
        {
        if (!args.Contains("full") && data.TileMetres > 0 && maps.TryGetTiles(mapId, out var tiles))
        {
            var interest = new TileInterest { Radii = StreamRadii.Default };
            defs = new List<EntityDefinition>();
            foreach (int id in TileStreamer.Begin(interest, tiles, start))
                if (lookup.TryGetValue(id, out var e) || tiles.TryGetGlobal(id, out e)) defs.Add(TileStreamer.Definition(ecs, e, tiles, interest));
        }
        else defs = EntityDefinitionFactory.StaticDefinitions(ecs);
        (_, snap) = GeometryParitySpike.ClientSnapshots(defs, data.TileMetres);
        Console.WriteLine($"  client holds {defs.Count:N0} definitions; triangle world {(snap.Geometry?.TriangleCount ?? 0):N0} triangles");
        }

        // The ground each side holds under the start: which tiles of it, and at what spacing.
        foreach (var e in snap.Entities.Values)
            if (e.Definition.Terrain is { } g && MathF.Abs(e.Transform.Position.X - start.X) < g.Posts * g.Spacing * 0.5f + 1f
                && MathF.Abs(e.Transform.Position.Z - start.Z) < g.Posts * g.Spacing * 0.5f + 1f)
                Console.WriteLine($"  client ground #{e.Id} at {V(e.Transform.Position)}: {g.Posts} posts {g.Spacing:F2} m apart{(g.IsCoarse ? ", coarse" : "")}");
        ecs.Query(new QueryDescription().WithAll<Transform, TerrainTileComponent>(), (Entity e, ref Transform t, ref TerrainTileComponent g) =>
        {
            if (MathF.Abs(t.Position.X - start.X) < g.Posts * g.Spacing * 0.5f + 1f && MathF.Abs(t.Position.Z - start.Z) < g.Posts * g.Spacing * 0.5f + 1f)
                Console.WriteLine($"  server ground #{e.Id} at {V(t.Position)}: {g.Posts} posts {g.Spacing:F2} m apart");
        });

        // The server's body and session, as a login makes them.
        const int conn = 1;
        var sessions = new SessionManager();
        var session = new UserSession { ConnectionId = conn, Username = "walker", CurrentMapId = mapId };
        sessions.AddSession(conn, session);
        var body = ecs.Create(
            new PlayerComponent { Username = "walker", ConnectionId = conn, Yaw = yaw },
            new Transform { Position = start, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0), Scale = Vector3.One },
            new Velocity(), new MaterialComponent(),
            new ColliderComponent { Shape = ColliderShape.Box, Size = PhysicsConstants.PlayerSize, IsSolid = false });
        var state = new LocalPlayerState { Position = start, Yaw = yaw, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0) };
        var physics = new ClientPhysicsSystem(state, new SpatialService()) { OwnEntityId = body.Id, MapMin = data.WalkMin, MapMax = data.WalkMax };

        float dt = PhysicsConstants.FixedDeltaTime;
        int parted = 0;
        float worst = 0f;
        for (int tick = 1; tick <= ticks; tick++)
        {
            bool down = taps > 0 ? tick <= taps * gap && (tick - 1) % gap == 0 : tick > 15;
            var input = new ClientInputUpdate
            {
                SequenceId = tick, DeltaTime = dt, Sprint = sprint,
                MoveDirection = down ? key : Vector3.Zero,
            };
            session.InputQueue.Enqueue(input);
            MovementSystem.Update(ecs, data.WalkMin, data.WalkMax, grid, lookup, sessions, maps, dt);
            var contact = physics.Predict(input, snap, dt);
            var server = ecs.Get<Transform>(body).Position;
            var client = state.Position;
            float apart = Vector3.Distance(server, client);
            worst = MathF.Max(worst, apart);
            if (apart > 0.01f) parted++;
            if (showAll || apart > 0.01f || contact != null)
            {
                float gs = PhysicsUtils.GetGroundHeight(ecs, grid, server, out string ms);
                float gc = PhysicsUtils.GetGroundHeight(snap, client, body.Id, out string mc);
                string held = contact is { } c ? $" client held off #{c.EntityId} {Named(ecs, lookup, c.EntityId)} normal {V(c.Normal)}" : "";
                Console.WriteLine($"  {tick,4} {(down ? "W" : " ")} server {V(server)} ground {gs:F3} {ms}; client {V(client)} ground {gc:F3} {mc}; apart {apart:F3}{held}");
            }
        }
        var end = ecs.Get<Transform>(body).Position;
        Console.WriteLine($"\n  server walked {Vector3.Distance(Flat(start), Flat(end)):F2} m to {PlayerCoordinates.Format(end)}; "
                        + $"client {Vector3.Distance(Flat(start), Flat(state.Position)):F2} m to {PlayerCoordinates.Format(state.Position)}; "
                        + $"{parted} tick(s) more than 1 cm apart, at most {worst:F3} m");
        return 0;

        static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    }

    /// <summary>A client's world as the server streams it: the tiles round <paramref name="from"/> at the join,
    /// then everything the streamer sends and removes as the player stands at <paramref name="at"/>.</summary>
    private static WorldSnapshot Streamed(World ecs, Dictionary<int, Entity> lookup, MapTiles tiles, float tileMetres,
                                          Vector3 from, Vector3 at, out int count)
    {
        var state = new ClientWorldState();
        state.Geometry.Runner = work => work();
        state.Clear(new Vector3(4000f, 400f, 4000f));
        state.ConfigureAcoustics(Vector3.Zero, 0.5f, 0.2f, tileMetres);
        var session = new UserSession { ConnectionId = 99, Username = "streamed" };
        session.Tiles.Radii = StreamRadii.Default;
        foreach (int id in TileStreamer.Begin(session.Tiles, tiles, from))
            if (lookup.TryGetValue(id, out var e) || tiles.TryGetGlobal(id, out e))
            {
                state.RegisterDefinition(TileStreamer.Definition(ecs, e, tiles, session.Tiles));
                session.KnownEntities.Add(id);
            }
        state.GetSnapshot();
        for (int i = 0; i < 100000; i++)
        {
            TileStreamer.Update(session, tiles, ecs, lookup, at, message =>
            {
                if (message is EntityDefinitionPack pack && pack.Unpack() is { } unpacked) foreach (var d in unpacked.Definitions) state.RegisterDefinition(d);
                else if (message is EntityDefinitionBatch batch) foreach (var d in batch.Definitions) state.RegisterDefinition(d);
                else if (message is EntityDefinition one) state.RegisterDefinition(one);
                else if (message is EntityRemoved gone) state.RemoveEntities(gone.EntityIds);
            });
            if (i > 0 && session.Tiles.Pending.Count == 0) break;
        }
        count = session.KnownEntities.Count;
        state.GetSnapshot();
        return state.GetSnapshot();
    }

    private static string Named(World ecs, Dictionary<int, Entity> lookup, int id)
        => lookup.TryGetValue(id, out var e) && ecs.Has<IdentityComponent>(e) ? $"'{ecs.Get<IdentityComponent>(e).Name}' ({ecs.Get<IdentityComponent>(e).PrefabId})" : "";

    private static Vector3 Parse(string s)
    {
        var f = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return PlayerCoordinates.ToWorld(f[0], f[1], f[2]);
    }

    /// <summary>A map as the server loads it, from a copy of its files.</summary>
    internal static bool Load(string mapId, out MapManager maps)
    {
        string dir = Path.Combine(Path.GetTempPath(), "openfps-walk-pair-" + Guid.NewGuid().ToString("N"));
        string src = OpenFPS.AudioLab.LabPaths.Server("maps", "places", mapId + ".json");
        bool place = File.Exists(src);
        if (!place) src = OpenFPS.AudioLab.LabPaths.Server("maps", mapId + ".json");
        Directory.CreateDirectory(Path.Combine(dir, "maps", "places"));
        File.Copy(src, place ? Path.Combine(dir, "maps", "places", mapId + ".json") : Path.Combine(dir, "maps", mapId + ".json"));
        if (place && File.Exists(Path.ChangeExtension(src, ".elevation")))
            File.Copy(Path.ChangeExtension(src, ".elevation"), Path.Combine(dir, "maps", "places", mapId + ".elevation"));
        maps = new MapManager(new MapRepository(Path.Combine(dir, "maps")), new PrefabRepository(OpenFPS.AudioLab.LabPaths.Server("prefabs")));
        maps.Initialize();
        Directory.Delete(dir, true);
        if (maps.TryGetMap(mapId, out _, out _, out _, out _)) return true;
        Console.WriteLine($"FAIL: no map {mapId}");
        return false;
    }
}
