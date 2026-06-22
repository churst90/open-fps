using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using LiteNetLib;
using Arch.Core;
using Serilog;

namespace OpenFPS.Server.Core;

public class CommandHandler
{
    private readonly NetworkService _network;
    private readonly SessionManager _sessions;
    private readonly MapManager _maps;
    private readonly GameServer _server;

    public CommandHandler(NetworkService network, SessionManager sessions, MapManager maps, GameServer server)
    {
        _network = network;
        _sessions = sessions;
        _maps = maps;
        _server = server;
    }

    public void HandleTextCommand(NetPeer peer, TextCommand cmd)
    {
        if (!_sessions.TryGetSession(peer.Id, out var session)) return;

        string commandName = cmd.Command.TrimStart('/').ToLower(); // Support both /move and move
        bool isElevated = session.Role == UserRole.Dev || session.Role == UserRole.Admin;
        
        switch (commandName)
        {
            case "scan": HandleScan(peer, session); break;
            case "pm": HandlePrivateMessage(peer, session, cmd.Args); break;
            case "move":
            case "tp":
                if (!isElevated) { DenyCommand(peer); return; }
                HandleMove(peer, session, cmd.Args); 
                break;
            case "spawn":
                if (!isElevated) { DenyCommand(peer); return; }
                HandleSpawn(peer, session, cmd.Args); 
                break;
            case "set_sound":
                if (!isElevated) { DenyCommand(peer); return; }
                HandleSetSound(peer, session, cmd.Args); 
                break;
            case "set_audio_mode":
                if (!isElevated) { DenyCommand(peer); return; }
                HandleSetAudioMode(peer, session, cmd.Args); 
                break;
            case "play_folder":
                if (!isElevated) { DenyCommand(peer); return; }
                HandlePlayFolder(peer, session, cmd.Args); 
                break;
            case "start_state":
                if (!isElevated) { DenyCommand(peer); return; }
                HandleStartState(peer, session, cmd.Args); 
                break;
            default:
                _network.SendMessage(peer, new TextEvent { Text = $"Command '{cmd.Command}' not recognized." }, DeliveryMethod.ReliableOrdered);
                break;
        }
    }

    private void DenyCommand(NetPeer peer)
    {
        _network.SendMessage(peer, new TextEvent { Text = "You do not have permission to execute this command." }, DeliveryMethod.ReliableOrdered);
    }

    private void HandleSetAudioMode(NetPeer peer, UserSession session, string[] args)
    {
        if (args.Length < 1) return;
        if (!Enum.TryParse<PlaybackMode>(args[0], true, out var mode)) return;

        Entity? nearest = FindNearestObject(session);
        if (nearest == null) return;

        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out _)) return;
        
        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.Mode = mode;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        _network.SendMessage(peer, new TextEvent { Text = $"Set audio mode of nearest object to {mode}." }, DeliveryMethod.ReliableOrdered);
    }

    private void HandlePlayFolder(NetPeer peer, UserSession session, string[] args)
    {
        if (args.Length < 1) return;

        Entity? nearest = FindNearestObject(session);
        if (nearest == null) return;

        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out _)) return;
        
        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.SoundId = args[0];
        emitter.Mode = PlaybackMode.LoopFolder;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        _network.SendMessage(peer, new TextEvent { Text = $"Nearest object is now playing folder: {args[0]}." }, DeliveryMethod.ReliableOrdered);
    }

    private void HandleStartState(NetPeer peer, UserSession session, string[] args)
    {
        if (args.Length < 2) return;

        Entity? nearest = FindNearestObject(session);
        if (nearest == null) return;

        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out _)) return;
        
        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.StartSoundId = args[0];
        emitter.SoundId = args[1];
        emitter.Mode = PlaybackMode.StateMachine;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        _network.SendMessage(peer, new TextEvent { Text = $"Started state machine on nearest object: {args[0]} -> {args[1]}." }, DeliveryMethod.ReliableOrdered);
    }

    private void SetOrAdd<T>(World world, Entity e, T component) where T : struct
    {
        if (world.Has<T>(e)) world.Set(e, component);
        else world.Add(e, component);
    }

    private Entity? FindNearestObject(UserSession session)
    {
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out var _, out var _, out var _)) return null;
        var playerPos = world.Get<Transform>(session.Entity).Position;
        
        Entity? nearest = null;
        float minDist = 5.0f;
        world.Query(new QueryDescription().WithAll<Transform, IdentityComponent>(), (Entity e, ref Transform t) => {
            if (e == session.Entity) return;
            float d = Vector3.Distance(playerPos, t.Position);
            if (d < minDist) { minDist = d; nearest = e; }
        });
        return nearest;
    }

    private void HandleSpawn(NetPeer peer, UserSession session, string[] args)
    {
        if (args.Length < 5)
        {
            _network.SendMessage(peer, new TextEvent { Text = "Usage: /spawn [Box|Cylinder] [Material] [sizeX] [sizeY] [sizeZ]" }, DeliveryMethod.ReliableOrdered);
            return;
        }

        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out var _, out var _, out var _)) return;
        var playerPos = world.Get<Transform>(session.Entity).Position;
        
        if (!Enum.TryParse<ColliderShape>(args[0], true, out var shape)) shape = ColliderShape.Box;
        string material = args[1];
        if (!float.TryParse(args[2], out float sx) || !float.TryParse(args[3], out float sy) || !float.TryParse(args[4], out float sz))
        {
            _network.SendMessage(peer, new TextEvent { Text = "Invalid sizes." }, DeliveryMethod.ReliableOrdered);
            return;
        }

        var rot = world.Get<Transform>(session.Entity).Rotation;
        Vector3 forward = Vector3.Transform(new Vector3(0,0,1), rot);
        Vector3 spawnPos = playerPos + (forward * 3.0f);
        
        var e = world.Create(
            new Transform { Position = spawnPos, Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = shape, Size = new Vector3(sx, sy, sz), IsSolid = true },
            new MaterialComponent { Material = material, Variant = "0" },
            new IdentityComponent { Name = $"Custom {shape}" },
            EntityType.StaticObject
        );

        _network.SendMessage(peer, new TextEvent { Text = $"Spawned {material} {shape} at {spawnPos.X:F1}, {spawnPos.Y:F1}, {spawnPos.Z:F1}" }, DeliveryMethod.ReliableOrdered);
    }

    private void HandleSetSound(NetPeer peer, UserSession session, string[] args)
    {
        if (args.Length < 2)
        {
            _network.SendMessage(peer, new TextEvent { Text = "Usage: /set_sound [SoundId] [Volume]" }, DeliveryMethod.ReliableOrdered);
            return;
        }

        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out var _, out var _, out var _)) return;
        var playerPos = world.Get<Transform>(session.Entity).Position;
        
        Entity? nearest = null;
        float minDist = 5.0f;
        world.Query(new QueryDescription().WithAll<Transform, IdentityComponent>(), (Entity e, ref Transform t) => {
            if (e == session.Entity) return;
            float d = Vector3.Distance(playerPos, t.Position);
            if (d < minDist) { minDist = d; nearest = e; }
        });

        if (nearest == null)
        {
            _network.SendMessage(peer, new TextEvent { Text = "No object nearby to attach sound." }, DeliveryMethod.ReliableOrdered);
            return;
        }

        float vol = float.Parse(args[1]);
        world.Add(nearest.Value, new SoundEmitterComponent { SoundId = args[0], Volume = vol, Range = 50.0f });
        world.Add(nearest.Value, EntityType.Beacon); // Tag it so client pulses it

        _network.SendMessage(peer, new TextEvent { Text = $"Attached sound {args[0]} to nearest object." }, DeliveryMethod.ReliableOrdered);
    }

    private void HandleScan(NetPeer peer, UserSession session)
    {
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out var _, out var _, out var _)) return;

        var playerPos = world.Get<Transform>(session.Entity).Position;
        float scanRadius = 20.0f;

        var results = new List<(string Name, float Distance, Vector3 Direction)>();

        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (e.Id == session.Entity.Id) return;

            float dist = Vector3.Distance(playerPos, t.Position);
            if (dist <= scanRadius)
            {
                string name = "Unknown Object";
                if (world.Has<IdentityComponent>(e)) name = world.Get<IdentityComponent>(e).Name;
                else if (world.Has<PlayerComponent>(e)) name = world.Get<PlayerComponent>(e).Username;
                else if (world.Has<BeaconComponent>(e)) name = "Beacon";

                results.Add((name, dist, Vector3.Normalize(t.Position - playerPos)));
            }
        });

        if (results.Count == 0)
        {
            _network.SendMessage(peer, new TextEvent { Text = "No objects detected nearby." }, DeliveryMethod.ReliableOrdered);
            return;
        }

        var sorted = results.OrderBy(r => r.Distance).Take(5);
        foreach (var item in sorted)
        {
            ref var playerTransform = ref world.Get<Transform>(session.Entity);
            string direction = GetRelativeDirection(playerTransform.Rotation, item.Direction);
            _network.SendMessage(peer, new TextEvent { Text = $"{item.Name} at {direction}, {item.Distance:F1} meters." }, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleMove(NetPeer peer, UserSession session, string[] args)
    {
        if (args.Length < 3)
        {
            _network.SendMessage(peer, new TextEvent { Text = "Usage: /move x y z" }, DeliveryMethod.ReliableOrdered);
            return;
        }

        if (float.TryParse(args[0], out float x) && float.TryParse(args[1], out float y) && float.TryParse(args[2], out float z))
        {
            if (!_maps.TryGetMap(session.CurrentMapId, out var world, out var mapSize, out var grid, out var _)) return;

            Vector3 targetPos = new Vector3(x, y, z);

            // COLLISION AWARE TELEPORT (Cylinder-based)
            if (OpenFPS.Server.Systems.MovementSystem.CheckCollision(world, grid, targetPos, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight))
            {
                _network.SendMessage(peer, new TextEvent { Text = "Cannot move there: Area is solid." }, DeliveryMethod.ReliableOrdered);
                return;
            }

            ref var t = ref world.Get<Transform>(session.Entity);
            t.Position = targetPos;
            t.IsDirty = true;
            
            // Force client reset
            _network.SendMessage(peer, new PlayerSpawned { EntityId = session.Entity.Id, SpawnTransform = t }, DeliveryMethod.ReliableOrdered);
            
            _network.SendMessage(peer, new TextEvent { Text = $"Moved to {targetPos.X:F1}, {targetPos.Y:F1}, {targetPos.Z:F1}" }, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandlePrivateMessage(NetPeer peer, UserSession session, string[] args)
    {
        if (args.Length < 2)
        {
            _network.SendMessage(peer, new TextEvent { Text = "Usage: /pm [username] [message]" }, DeliveryMethod.ReliableOrdered);
            return;
        }

        string targetUsername = args[0];
        string message = string.Join(" ", args.Skip(1));

        var targetSession = _sessions.GetAllSessions().FirstOrDefault(s => s.Username.Equals(targetUsername, StringComparison.OrdinalIgnoreCase));
        if (targetSession == null)
        {
            _network.SendMessage(peer, new TextEvent { Text = $"User '{targetUsername}' not found." }, DeliveryMethod.ReliableOrdered);
            return;
        }

        var targetPeer = _network.GetPeer(targetSession.ConnectionId);
        if (targetPeer != null)
        {
            var pm = new ChatMessage { Sender = $"[PM from {session.Username}]", Text = message };
            _network.SendMessage(targetPeer, pm, DeliveryMethod.ReliableOrdered);
            _network.SendMessage(peer, new ChatMessage { Sender = $"[PM to {targetUsername}]", Text = message }, DeliveryMethod.ReliableOrdered);
        }
    }

    private string GetRelativeDirection(Quaternion rotation, Vector3 targetDir)
    {
        Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(rotation));
        Vector3 localDir = Vector3.Transform(targetDir, rotMat);
        float angle = MathF.Atan2(localDir.X, localDir.Z) * (180.0f / MathF.PI);
        if (angle < 0) angle += 360.0f;
        return angle switch
        {
            < 22.5f or >= 337.5f => "12 o'clock", < 67.5f => "2 o'clock", < 112.5f => "3 o'clock",
            < 157.5f => "5 o'clock", < 202.5f => "6 o'clock", < 247.5f => "8 o'clock",
            < 292.5f => "9 o'clock", _ => "11 o'clock"
        };
    }
}
