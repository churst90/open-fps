using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;

namespace OpenFPS.Server.Core;

/// <summary>
/// /spawn of things that live (a walker, a parked vehicle, a train on a track already laid, an aircraft
/// parked on open ground, a fire, a plain shape) and /give of a vehicle, which is give-premium.
///
/// Each goes through the path the map's own take: a VehicleSystem walker, the vehicle shell a parked car
/// is (CompositeService.Place), a RailSystem consist. Nobody can fly an aircraft yet, so a spawned one is
/// a named body on the ground (CompositeService.ParkAircraft).
///
/// On a player's map each is recorded, so /savemap keeps it. On a shipped map (MapManager.IsShipped) it
/// lasts until a restart: those files are generated and must stay as the generator wrote them.
/// </summary>
public partial class CommandHandler
{
    private const string SpawnUsage =
        "Usage: /spawn walker [NAME], /spawn vehicle PRESET, /spawn train PRESET, /spawn train out [NAME], /spawn fire PRESET, /spawn fire out, or /spawn Box|Cylinder MATERIAL X Y Z.";

    /// <summary>The only jet. No jets are given or spawned.</summary>
    internal const string Jet = "airliner";

    private void HandleSpawn(UserSession session, string[] args, Action<IMessage> reply)
    {
        string kind = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (kind)
        {
            case "walker":
            case "person":
            case "pedestrian":
                SpawnWalker(session, args.Length > 1 ? string.Join(" ", args.Skip(1)) : "", reply);
                return;
            case "vehicle":
            case "car":
            case "aircraft":
            {
                if (args.Length < 2) { Say(reply, $"Usage: /spawn {kind} PRESET. Presets: {VehiclePresetList()}."); return; }
                // Yours on your own map; on anybody else's it is the map's, and anyone may drive it.
                string owner = OwnsHere(session) ? session.Username : "";
                if (!ParkVehicle(session, args[1].ToLowerInvariant(), owner, out string said, out _)) { Say(reply, said); return; }
                Say(reply, said);
                return;
            }
            case "helicopter":
            {
                string owner = OwnsHere(session) ? session.Username : "";
                ParkVehicle(session, "helicopter", owner, out string said, out _);
                Say(reply, said);
                return;
            }
            case "fire":
                SpawnFire(session, args.Length > 1 ? args[1].ToLowerInvariant() : "", reply);
                return;
            case "train":
                if (args.Length < 2) { Say(reply, $"Usage: /spawn train PRESET, or /spawn train out [NAME]. Presets: {string.Join(", ", TrainProfile.Presets.Keys)}."); return; }
                if (args[1].ToLowerInvariant() is "out" or "off" or "remove")
                {
                    TrainOut(session, args.Length > 2 ? string.Join(" ", args.Skip(2)) : null, reply);
                    return;
                }
                SpawnTrain(session, args[1].ToLowerInvariant(), reply);
                return;
            default:
                HandleSpawnShape(session, args, reply);
                return;
        }
    }

    /// <summary>
    /// /spawn fire PRESET: a fire lit now in front of you, for testing (docs/FIRE.md): a campfire, a
    /// bonfire, a car, a house, a stand of trees or a crown fire's front, far enough ahead that you are not
    /// standing in it. It grows from the moment it is lit, burns, dies down and smoulders, as its preset's
    /// life says; every client hears it at the same point of its life (FireSpec.KeyFor). Not kept by
    /// /savemap. /spawn fire out puts out the nearest one lit this way within 400 m.
    /// </summary>
    private void SpawnFire(UserSession session, string preset, Action<IMessage> reply)
    {
        string list = string.Join(", ", FireSpec.Presets.Keys);
        if (preset.Length == 0) { Say(reply, $"Usage: /spawn fire PRESET, or /spawn fire out. Fires: {list}."); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var feet)) return;
        if (preset is "out" or "off")
        {
            Entity? nearest = null;
            float best = 400f;
            world.Query(new QueryDescription().WithAll<Transform, SoundEmitterComponent>(), (Entity e, ref Transform t, ref SoundEmitterComponent em) =>
            {
                if (!em.SoundId.StartsWith("fire:", StringComparison.OrdinalIgnoreCase) || !em.SoundId.Contains("/lit=", StringComparison.Ordinal)) return;
                float d = Vector3.Distance(feet, t.Position);
                if (d < best) { best = d; nearest = e; }
            });
            if (nearest is not { } fire) { Say(reply, "There is no fire lit with /spawn fire within 400 metres."); return; }
            _maps.DestroyEntity(session.CurrentMapId, fire);
            Say(reply, $"The fire {best:F0} metres away is out.");
            return;
        }
        if (!FireSpec.Presets.ContainsKey(preset)) { Say(reply, $"There is no fire called {preset}. Fires: {list}."); return; }
        var spec = FireSpec.ByName(preset);
        float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        // Its near edge a few metres ahead (a crown fire's front a hundred), its width across your way.
        float ahead = 0.5f * spec.AreaDepth + (spec.Fuel == FireFuel.Crown ? 100f : 3f);
        var at = feet + forward * ahead + new Vector3(0f, MathF.Max(0.4f, MathF.Min(spec.FlameHeightMetres * 0.5f, MathF.Max(0.4f, spec.FuelHeightMetres))), 0f);
        string key = FireSpec.KeyFor(preset, WindField.Now());
        var e = _maps.SpawnEntity(session.CurrentMapId, w => w.Create(
            new Transform { Position = at, Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f), IsDirty = true },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(spec.AreaWidth, MathF.Max(0.5f, spec.FlameHeightMetres), spec.AreaDepth), IsSolid = false },
            new IdentityComponent { Name = "Fire", Description = spec.Name + ", lit with /spawn fire." },
            new SoundEmitterComponent { IsSynth = true, SoundId = key, Mode = PlaybackMode.LoopOne, Volume = 1f, Range = 3000f, MinDistance = 1f },
            EntityType.StaticObject));
        if (e == Entity.Null) { Say(reply, "The fire could not be lit: the map is not loaded."); return; }
        _server.SyncAudioComponent(e.Id);
        Say(reply, $"{spec.Name}, lit {ahead:F0} metres ahead of you. It grows over {spec.GrowthSeconds / 60f:F0} minutes. /spawn fire out puts it out; /savemap does not keep it.");
    }

    private static string VehiclePresetList()
        => string.Join(", ", MachineRegistry.Ids.Concat(AircraftProfile.Presets.Keys.Where(k => k != Jet)));

    /// <summary>
    /// /spawn walker [NAME]: somebody walking back and forth on a clear line from in front of you, at a
    /// walking pace, like the map's own pedestrians.
    /// </summary>
    private void SpawnWalker(UserSession session, string name, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out var grid, out var feet)) return;
        float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var right = new Vector3(forward.Z, 0f, -forward.X);
        Vector3 a = default, b = default;
        float length = 0f;
        foreach (var dir in new[] { forward, right, -right, -forward })
        {
            var start = feet + dir * 1.5f;
            float clear = 0f;
            for (float l = 1f; l <= 15f; l += 1f)
            {
                var p = start + dir * l;
                if (MovementSystem.CheckCollision(world, grid, p, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight)) break;
                float ground = PhysicsUtils.GetGroundHeight(world, grid, p + new Vector3(0f, 1f, 0f), out _);
                if (ground < -500f || MathF.Abs(ground - feet.Y) > 0.6f) break;
                clear = l;
            }
            if (clear >= 4f) { a = start; b = start + dir * clear; length = clear; break; }
        }
        if (length == 0f) { Say(reply, "There is no clear stretch of four metres beside you for somebody to walk."); return; }

        string who = string.IsNullOrWhiteSpace(name) ? "someone walking" : name.Trim();
        var vd = new VehicleData { Name = who, Preset = "walker", RoadStart = a, RoadEnd = b, SpeedsKmh = new[] { 5f }, WaitSeconds = 3f };
        var e = _server.Vehicles.SpawnOne(_maps, _composites, session.CurrentMapId, vd);
        if (e == Entity.Null) { Say(reply, "Nobody could be made to walk here."); return; }
        bool record = !_maps.IsShipped(session.CurrentMapId);
        if (record && _maps.TryGetMapData(session.CurrentMapId, out var data)) (data.Vehicles ??= new List<VehicleData>()).Add(vd);
        Say(reply, $"{who} walks back and forth here now, over {length:F0} metres. {Lasting(record, "them")}");
    }

    /// <summary>/spawn train PRESET: a train on the nearest track a train already runs, at the point of
    /// it nearest you. A train goes only where there is a railway.</summary>
    private void SpawnTrain(UserSession session, string preset, Action<IMessage> reply)
    {
        if (!TrainProfile.Presets.ContainsKey(preset)) { Say(reply, $"There is no train called {preset}. Trains: {string.Join(", ", TrainProfile.Presets.Keys)}."); return; }
        if (!TryGetBody(session, reply, out _, out _, out var feet)) return;
        if (!_maps.TryGetMapData(session.CurrentMapId, out var data)) return;
        var railIds = (data.Trains ?? new List<TrainData>()).Select(t => t.Track).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rails = (data.Tracks ?? new List<TrackData>()).Where(t => railIds.Contains(t.Id) && t.Waypoints.Count >= 3).ToList();
        if (rails.Count == 0) { Say(reply, "There is no railway on this map. A train can only go on a track that is already there."); return; }

        TrackData? best = null; float bestDistance = float.MaxValue, bestAlong = 0f;
        foreach (var track in rails)
        {
            float along = 0f;
            var pts = track.Waypoints;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 p = pts[i], q = pts[(i + 1) % pts.Count];
                var seg = q - p;
                float len = seg.Length();
                float t = len > 1e-4f ? Math.Clamp(Vector3.Dot(feet - p, seg) / (len * len), 0f, 1f) : 0f;
                float d = Vector3.Distance(feet, p + seg * t);
                if (d < bestDistance) { bestDistance = d; best = track; bestAlong = along + t * len; }
                along += len;
            }
        }
        var like = data.Trains!.First(t => t.Track.Equals(best!.Id, StringComparison.OrdinalIgnoreCase));
        var profile = TrainProfile.ByName(preset);
        var td = new TrainData
        {
            Name = $"{profile.Name} {_server.Rail.CountOn(session.CurrentMapId) + 1}",
            Preset = preset, Track = best!.Id, TopSpeedKmh = like.TopSpeedKmh,
            AccelerationMps2 = like.AccelerationMps2, BrakingMps2 = like.BrakingMps2,
            StartOffsetMetres = bestAlong,
        };
        if (!_server.Rail.SpawnOne(_maps, session.CurrentMapId, td)) { Say(reply, "The train could not be put on the track."); return; }
        bool record = !_maps.IsShipped(session.CurrentMapId);
        if (record) data.Trains.Add(td);
        Say(reply, $"A {profile.Name} is on the {best.Id} track, {bestDistance:F0} metres from you. {Lasting(record, "it")}");
    }

    /// <summary>
    /// /spawn train out [NAME]: takes off a train put on with /spawn train, the one named or else the
    /// nearest. The map's own trains stay. On a player's map it is also struck from what /savemap keeps.
    /// </summary>
    private void TrainOut(UserSession session, string? name, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out _, out _, out var feet)) return;
        string? gone = _server.Rail.RemoveSpawned(_maps, session.CurrentMapId, feet, name);
        if (gone == null)
        {
            Say(reply, string.IsNullOrWhiteSpace(name)
                ? "There is no train put on with /spawn train on this map."
                : $"There is no train put on with /spawn train called {name.Trim()} on this map.");
            return;
        }
        if (!_maps.IsShipped(session.CurrentMapId) && _maps.TryGetMapData(session.CurrentMapId, out var data))
            data.Trains?.RemoveAll(t => string.Equals(t.Name, gone, StringComparison.OrdinalIgnoreCase));
        Say(reply, $"{gone} is off the track.");
    }

    /// <summary>How long something spawned lasts: kept by /savemap on a player's map, until a restart
    /// on a shipped one.</summary>
    private static string Lasting(bool recorded, string pronoun)
        => recorded ? $"/savemap keeps {pronoun}." : "This is one of the server's own maps, so it lasts until the server restarts.";

    /// <summary>
    /// /give [NAME] vehicle PRESET: a vehicle parked beside them, theirs. Premium. True when the words
    /// named a vehicle (whether or not it could be given), false when /give should carry on with items.
    /// </summary>
    private bool TryGiveVehicle(UserSession session, string[] args, Action<IMessage> reply)
    {
        static bool IsWord(string a) => a.Equals("vehicle", StringComparison.OrdinalIgnoreCase)
                                     || a.Equals("car", StringComparison.OrdinalIgnoreCase)
                                     || a.Equals("aircraft", StringComparison.OrdinalIgnoreCase);
        int k = args.Length > 0 && IsWord(args[0]) ? 0 : args.Length > 1 && IsWord(args[1]) ? 1 : -1;
        if (k < 0) return false;
        var receiver = session;
        if (k == 1)
        {
            if (OnlineSession(args[0]) is not { } named) { Say(reply, $"{args[0]} is not online."); return true; }
            receiver = named;
        }
        if (!session.Can(Permissions.GivePremium)) { Say(reply, $"Vehicles are premium; giving one needs {Permissions.GivePremium}."); return true; }
        if (args.Length <= k + 1) { Say(reply, $"Usage: /give [NAME] vehicle PRESET. Presets: {VehiclePresetList()}."); return true; }
        string preset = args[k + 1].ToLowerInvariant();
        if (!ParkVehicle(receiver, preset, receiver.Username, out string said, out string kind)) { Say(reply, said); return true; }
        SendGiveEvent(receiver, "vehicle:" + preset);
        if (receiver != session)
        {
            _server.SendToSession(receiver, new TextEvent { Text = $"{session.Username} gave you {kind}. {said}" });
            Say(reply, $"You gave {receiver.Username} {kind}.");
        }
        else Say(reply, said);
        return true;
    }

    /// <summary>
    /// A vehicle of a preset parked on clear ground beside somebody, facing the way they face, owned by
    /// <paramref name="owner"/> ("" for the map's). <paramref name="said"/> is what to tell them, or why
    /// not; <paramref name="kind"/> is "a muscle car", "a helicopter".
    /// </summary>
    private bool ParkVehicle(UserSession beside, string preset, string owner, out string said, out string kind)
    {
        said = kind = "";
        if (preset == Jet) { said = "No jets: the airliner cannot be given or spawned."; return false; }
        bool aircraft = AircraftProfile.Presets.ContainsKey(preset);
        if (!aircraft && !(MachineRegistry.Knows(preset) && VehicleShell.TryParse(VehicleShell.Prefix + preset, out _)))
        { said = $"There is no vehicle called {preset}. Presets: {VehiclePresetList()}."; return false; }
        if (!_maps.TryGetMap(beside.CurrentMapId, out var world, out _, out var grid, out _)
            || beside.Entity == Entity.Null || !world.IsAlive(beside.Entity))
        { said = $"{beside.Username} is not in the world just now."; return false; }
        var feet = world.Get<Transform>(beside.Entity).Position;
        float yaw = world.Has<PlayerComponent>(beside.Entity) ? world.Get<PlayerComponent>(beside.Entity).Yaw : 0f;
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
        string mapId = beside.CurrentMapId;
        string yours = owner.Length == 0 ? "" : owner.Equals(beside.Username, StringComparison.OrdinalIgnoreCase) ? ", yours" : $", {owner}'s";

        if (_composites == null) { said = "Vehicles are not available on this server."; return false; }
        bool record = !_maps.IsShipped(mapId);

        if (aircraft)
        {
            var air = AircraftProfile.ByName(preset);
            kind = $"a {CompositeService.AircraftWord(preset)}";
            var footprint = new Vector3(air.WingspanMetres, 3f, air.LengthMetres);
            if (ClearGroundBeside(world, grid, feet, yaw, footprint, openSky: true) is not { } at)
            { said = $"There is no open ground beside {beside.Username} big enough for {kind}."; return false; }
            if (_composites.ParkAircraft(mapId, preset, at, rotation, owner, record) == Entity.Null)
            { said = "It could not be made: the map is not loaded."; return false; }
            said = $"{Capital(kind)} is parked beside you on open ground{yours}. Nobody can fly one yet: aircraft only fly their circuits for now.";
            return true;
        }

        var profile = MachineRegistry.VehicleFor(preset);
        kind = $"a {profile.Name}";
        var size = new Vector3(profile.WidthMetres, MathF.Max(1.2f, profile.HeightMetres), profile.LengthMetres);
        if (ClearGroundBeside(world, grid, feet, yaw, size, openSky: false) is not { } spot)
        { said = $"There is no clear ground beside {beside.Username} big enough for {kind}."; return false; }
        int root = _composites.Place(mapId, VehicleShell.Prefix + preset, spot, rotation, owner, out _, out string error, record);
        if (root < 0) { said = $"It could not be parked: {error}."; return false; }
        _server.SyncAudioComponent(root);
        said = $"{Capital(kind)} is parked beside you{yours}. Get in with /enter.";
        return true;
    }

    /// <summary>
    /// Ground clear for a body of <paramref name="size"/> (width, height, length) beside somebody: to
    /// their right first, then left, ahead, behind, a little further out each round. Level with their
    /// feet, nothing solid anywhere on the footprint, and with <paramref name="openSky"/> nothing over it
    /// for twelve metres either.
    /// </summary>
    internal static Vector3? ClearGroundBeside(World world, SpatialGrid<Entity> grid, Vector3 feet, float yaw, Vector3 size, bool openSky)
    {
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var right = new Vector3(forward.Z, 0f, -forward.X);
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
        float height = openSky ? 12f : MathF.Max(size.Y, PhysicsConstants.PlayerHeight);
        for (int ring = 0; ring < 3; ring++)
        {
            foreach (var (dir, half) in new[] { (right, size.X), (-right, size.X), (forward, size.Z), (-forward, size.Z) })
            {
                var centre = feet + dir * (half * 0.5f + 1.5f + ring * 3f);
                float ground = PhysicsUtils.GetGroundHeight(world, grid, centre + new Vector3(0f, 1f, 0f), out _);
                if (ground < -500f || MathF.Abs(ground - feet.Y) > 1f) continue;
                centre.Y = ground;
                if (FootprintClear(world, grid, centre, rotation, size, height)) return centre;
            }
        }
        return null;
    }

    private static bool FootprintClear(World world, SpatialGrid<Entity> grid, Vector3 centre, Quaternion rotation, Vector3 size, float height)
    {
        const float Step = 1.0f, Radius = 0.6f;
        int nx = Math.Max(1, (int)MathF.Ceiling(size.X / Step)), nz = Math.Max(1, (int)MathF.Ceiling(size.Z / Step));
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
            {
                var local = new Vector3((i / (float)nx - 0.5f) * size.X, 0f, (j / (float)nz - 0.5f) * size.Z);
                var p = centre + Vector3.Transform(local, rotation);
                if (MovementSystem.CheckCollision(world, grid, p, Radius, height)) return false;
            }
        return true;
    }

    /// <summary>/spawn Box|Cylinder MATERIAL X Y Z: a plain solid three metres in front of you.</summary>
    private void HandleSpawnShape(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 5)
        {
            Say(reply, SpawnUsage);
            return;
        }

        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        if (!Enum.TryParse<ColliderShape>(args[0], true, out var shape)) shape = ColliderShape.Box;
        string material = args[1];
        if (!float.TryParse(args[2], out float sx) || !float.TryParse(args[3], out float sy) || !float.TryParse(args[4], out float sz))
        {
            Say(reply, "Invalid sizes.");
            return;
        }

        var rot = world.Get<Transform>(session.Entity).Rotation;
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), rot);
        Vector3 spawnPos = playerPos + (forward * 3.0f);

        // Through SpawnEntity: a bare world.Create leaves the object out of the map lookup and the
        // spatial grid, so nothing could see it, hear it or walk into it.
        var e = _maps.SpawnEntity(session.CurrentMapId, w => w.Create(
            new Transform { Position = spawnPos, Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = shape, Size = new Vector3(sx, sy, sz), IsSolid = true },
            new MaterialComponent { Material = material, Variant = "0" },
            new IdentityComponent { Name = $"Custom {shape}" },
            EntityType.StaticObject
        ));

        if (e == Entity.Null) { Say(reply, "Spawn failed: the map is not loaded."); return; }

        Say(reply, $"Spawned {material} {shape} (entity {e.Id}) at {PlayerCoordinates.Format(spawnPos)}");
    }
}
