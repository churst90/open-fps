using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Buffers;
using LiteNetLib;
using Serilog;
using Arch.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using NetworkEntityState = OpenFPS.Common.Networking.EntityState;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using OpenFPS.Server.Services;
using static OpenFPS.Common.PhysicsConstants;

namespace OpenFPS.Server;

public class GameServer
{
    private readonly NetworkService _network = new();
    private readonly SessionManager _sessions = new();
    private readonly WorldEnvironmentSystem _environment = new();
    private MapRepository _mapRepo = null!;
    private MapManager _maps = null!;
    private readonly IUserRepository _userRepo;
    private CommandHandler _commands = null!;
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _dirtyAudioEntities = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _commandBuffer = new();

    private readonly MessageDispatcher _dispatcher = new();
    private DiscoveryService _discovery = null!;
    private SocialService _social = null!;
    private MapAuthorityService _mapAuthority = null!;
    private MudGateway _mudGateway = null!;
    private readonly ServerStateUpdate _reusableBroadcast = new();

    public GameServer(IUserRepository userRepo)
    {
        _userRepo = userRepo;
    }

    public void EnqueueCommand(Action action) => _commandBuffer.Enqueue(action);

    private bool _isRunning = true;
    private long _currentTick = 0;

    public void SyncAudioComponent(int entityId)
    {
        _dirtyAudioEntities.Enqueue(entityId);
    }

    // The tick rate lives in PhysicsConstants — the client predicts against the same number.
    private const double TickTimeMs = 1000.0 / PhysicsConstants.TickRate;
    private const float EarshotRange = 200.0f; 

    public void Start(int port)
    {
        var prefabRepo = new PrefabRepository("prefabs");
        _mapRepo = new MapRepository("maps");
        _maps = new MapManager(_mapRepo, prefabRepo);
        _maps.Initialize();
        _commands = new CommandHandler(_network, _sessions, _maps, this);
        
        // Initialize new Service Architecture
        _discovery = new DiscoveryService(_dispatcher, _sessions);
        _social = new SocialService(_dispatcher);
        _mapAuthority = new MapAuthorityService(_dispatcher);
        
        RegisterHandlers();

        // Start MUD Gateway on port + 1 (e.g. 33289)
        _mudGateway = new MudGateway(port + 1, _dispatcher);
        _mudGateway.Start();

        _network.OnConnected = (peer) => Log.Information("Peer connected: {Id}", peer.Id);
        _network.OnDisconnected = HandlePeerDisconnected;
        _network.Start(port);
        RunLoop();
    }

    private void RegisterHandlers()
    {
        _dispatcher.RegisterHandler<LoginRequest>((id, req, reply) => {
            var peer = _network.GetPeer(id);
            if (peer != null) { HandleLogin(peer, req); } 
            else { HandleMudLogin(id, req, reply); }
        });
        _dispatcher.RegisterHandler<RegisterRequest>((id, req, reply) => {
            var peer = _network.GetPeer(id);
            if (peer != null) HandleRegister(peer, req);
        });
        _dispatcher.RegisterHandler<MapDataRequest>((id, req, reply) => {
            var peer = _network.GetPeer(id);
            if (peer != null) HandleMapDataRequest(peer, req);
        });
        _dispatcher.RegisterHandler<ClientInputUpdate>((id, req, reply) => {
            if (!_sessions.TryGetSession(id, out var s)) return;
            if (req.SequenceId <= s.LastProcessedSequenceId) return;
            // Bound the backlog: a client that floods inputs cannot grow the queue without limit,
            // and gains nothing by trying — MovementSystem spends real time, not queue depth.
            if (s.InputQueue.Count >= MaxQueuedInputs)
            {
                s.DroppedInputs++;
                if (s.DroppedInputs % 120 == 1)
                    Log.Warning("Input queue full for {User}; dropping inputs ({Count} so far).", s.Username, s.DroppedInputs);
                return;
            }
            s.InputQueue.Enqueue(req);
        });
        _dispatcher.RegisterHandler<TextCommand>((id, req, reply) => {
            var peer = _network.GetPeer(id);
            if (peer != null)
            {
                if (req.Command == "ready") HandlePlayerReady(peer);
                else HandleTextCommand(peer, req);
            }
        });
        _dispatcher.RegisterHandler<ChatMessage>((id, req, reply) => {
            if (_sessions.TryGetSession(id, out var sess)) 
            {
                Log.Information("[CHAT] {User}: {Text}", sess.Username, req.Text);
                var broadcast = new ChatMessage { Sender = sess.Username, Text = req.Text };
                foreach (var s in _sessions.GetAllSessions())
                {
                    var p = _network.GetPeer(s.ConnectionId);
                    if (p != null) _network.SendMessage(p, broadcast, DeliveryMethod.ReliableOrdered);
                }
            }
        });
        _dispatcher.RegisterHandler<InteractRequest>((id, req, reply) => {
            var peer = _network.GetPeer(id);
            if (peer != null) HandleInteract(peer, req);
        });
        _dispatcher.RegisterHandler<VoiceData>((id, req, reply) => {
            if (!_sessions.TryGetSession(id, out var senderSession)) return;
            // Relay to all players on the same map within earshot
            foreach (var s in _sessions.GetAllSessions())
            {
                if (s.ConnectionId == id) continue; // don't echo back to sender
                if (s.CurrentMapId != senderSession.CurrentMapId) continue;
                var peer = _network.GetPeer(s.ConnectionId);
                if (peer != null)
                    _network.SendMessage(peer, req, LiteNetLib.DeliveryMethod.Unreliable);
            }
        });
    }

    private void HandleMudLogin(int connId, LoginRequest req, Action<IMessage> reply)
    {
        if (!_userRepo.VerifyPassword(req.Username, req.Password))
        {
            reply(new LoginResponse { Success = false, Message = "Invalid Credentials" });
            return;
        }
        var user = _userRepo.GetUser(req.Username)!;
        _sessions.AddSession(connId, new UserSession { ConnectionId = connId, Username = user.Username, Role = user.Role });
        reply(new LoginResponse { Success = true, Message = "Authenticated", Username = user.Username });
    }

    private void RunLoop()
    {
        var stopwatch = Stopwatch.StartNew();
        double accumulator = 0;
        while (_isRunning)
        {
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            accumulator += elapsed;
            _network.PollEvents();
            while (accumulator >= TickTimeMs) { Update(_currentTick++); accumulator -= TickTimeMs; }
            Thread.Sleep(1);
        }
    }

    private void Update(long tick)
    {
        try
        {
            // 1. Process Commands & Network Messages
            while (_commandBuffer.TryDequeue(out var cmd)) cmd();
            while (_network.TryDequeueMessage(out var item))
            {
                _dispatcher.Dispatch(item.peer.Id, item.message, msg => _network.SendMessage(item.peer, msg, DeliveryMethod.ReliableOrdered));
            }

            // 2. Update Environment
            float dt = FixedDeltaTime;
            _environment.Update(dt);

            foreach (var entry in _maps.GetAllMaps())
            {
                try
                {
                    var world = entry.Value.world;
                    var grid = entry.Value.grid;
                    var lookup = entry.Value.lookup;

                    // 3. Refresh Spatial Grid (Dynamic items)
                    grid.Clear();
                    world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) => {
                        grid.AddOverlapping(t.Position, c.Size, e, false);
                    });

                    // 4. Update Simulation (Movement/AI)
                    MovementSystem.Update(world, entry.Value.data.MinBound, entry.Value.data.MaxBound, grid, _sessions, _maps, dt);
                    AISystem.Update(world, lookup, dt);
                    ParentSystem.Update(world, lookup);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error updating map {MapId}", entry.Key);
                }
            }

            if (tick % TickRate == 0) BroadcastEnvironment(); // once per second

            // 5. Broadcast World State
            BroadcastWorldState(tick);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fatal error in Update loop for tick {Tick}", tick);
        }
    }

    private void HandleLogin(NetPeer peer, LoginRequest request)
    {
        try
        {
            if (!_userRepo.VerifyPassword(request.Username, request.Password))
            {
                _network.SendMessage(peer, new LoginResponse { Success = false, Message = "Invalid Credentials" }, DeliveryMethod.ReliableOrdered);
                return;
            }

            var user = _userRepo.GetUser(request.Username)!;
            var session = new UserSession { ConnectionId = peer.Id, Username = user.Username, Role = user.Role };
            _sessions.AddSession(peer.Id, session);

            Log.Information("User {User} authenticated. Sending Manifest.", user.Username);

            _network.SendMessage(peer, new LoginResponse { 
                Success = true, 
                Message = "Authenticated",
                Username = user.Username,
                Role = user.Role
            }, DeliveryMethod.ReliableOrdered);

            if (_maps.TryGetMap("default", out var world, out var mapSize, out var _, out var _))
            {
                var staticEntities = new List<Entity>();
                world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) => {
                    if (!world.Has<PlayerComponent>(e) && !world.Has<Velocity>(e)) staticEntities.Add(e);
                });

                string checksum = _maps.GetMapChecksum("default"); 
                
                float voxelRes = 0.5f;
                float occFloor = 0.2f;
                float minimumY = -10.0f;
                Transform spawnPoint = new Transform { Position = new Vector3(0, 5, 0) };
                Vector3 mapMin = new Vector3(-50, 0, -50);
                Vector3 mapMax = new Vector3(50, 20, 50);
                float gravity = 15.0f;
                float temperature = 20.0f;
                float humidity = 0.5f;
                float pressure = 1.0f;
                float absorbMult = 1.0f;

                var allMaps = _mapRepo.LoadAll();
                var currentMapData = allMaps.FirstOrDefault(m => m.Id == "default");
                if (currentMapData != null)
                {
                    voxelRes = currentMapData.VoxelResolution;
                    occFloor = currentMapData.OcclusionFloor;
                    spawnPoint = new Transform { Position = currentMapData.SpawnPoint.Position, Rotation = currentMapData.SpawnPoint.Rotation };
                    minimumY = currentMapData.MinimumY;
                    mapMin = currentMapData.MinBound;
                    mapMax = currentMapData.MaxBound;
                    gravity = currentMapData.Gravity;
                    temperature = currentMapData.Temperature;
                    humidity = currentMapData.Humidity;
                    pressure = currentMapData.AirPressure;
                    absorbMult = currentMapData.AirAbsorptionMultiplier;
                }

                _network.SendMessage(peer, new MapManifest {
                    MapName = "default",
                    Checksum = checksum,
                    WorldSize = mapSize,
                    MapMin = mapMin,
                    MapMax = mapMax,
                    ExpectedEntityCount = staticEntities.Count,
                    VoxelResolution = voxelRes,
                    OcclusionFloor = occFloor,
                    SpawnPoint = spawnPoint,
                    MinimumY = minimumY,
                    Gravity = gravity,
                    Temperature = temperature,
                    Humidity = humidity,
                    AirPressure = pressure,
                    AirAbsorptionMultiplier = absorbMult
                }, DeliveryMethod.ReliableOrdered);
                Log.Information("Manifest sent to {User}. Waiting for data request or ready.", user.Username);
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "CRASH in HandleLogin for user {User}", request.Username);
            throw;
        }
    }

    private void HandleMapDataRequest(NetPeer peer, MapDataRequest request)
    {
        if (!_maps.TryGetMap(request.MapName, out var world, out var _, out var _, out var _)) return;

        var staticEntities = new List<Entity>();
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) => {
            if (!world.Has<PlayerComponent>(e) && !world.Has<Velocity>(e)) staticEntities.Add(e);
        });

        Log.Information("Streaming {Count} entities to {Peer}...", staticEntities.Count, peer.Id);

        foreach (var e in staticEntities)
        {
            _network.SendMessage(peer, CreateDefinition(world, e), DeliveryMethod.ReliableOrdered);
        }

        _network.SendMessage(peer, new MapLoadComplete(), DeliveryMethod.ReliableOrdered);
    }

    private void HandlePlayerReady(NetPeer peer)
    {
        if (!_sessions.TryGetSession(peer.Id, out var session)) return;
        if (!_maps.TryGetMap("default", out var world, out var _, out var _, out var lookup)) return;

        Transform spawnPoint = _maps.GetSpawnPoint("default");

        _commandBuffer.Enqueue(() => {
            session.Entity = world.Create();
            world.Add(session.Entity, new PlayerComponent { 
                    ConnectionId = peer.Id, 
                    Username = session.Username, 
                    Role = session.Role,
                    Yaw = 0,
                    Pitch = 0
                });
            world.Add(session.Entity, EntityType.Player);
            world.Add(session.Entity, spawnPoint);
            world.Add(session.Entity, new Velocity { Linear = Vector3.Zero });
            world.Add(session.Entity, new NameComponent { Name = session.Username });
            world.Add(session.Entity, new InventoryComponent { ItemEntityIds = new List<int>() });
            world.Add(session.Entity, new HealthComponent { Current = 100, Max = 100 });
            world.Add(session.Entity, new MaterialComponent { Material = "Generic" });
            world.Add(session.Entity, new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(PlayerRadius * 2, PlayerHeight, PlayerRadius * 2), IsSolid = true });

            _maps.RegisterEntity("default", session.Entity);
            ref var t = ref world.Get<Transform>(session.Entity);
            t.IsDirty = true;
            
            _network.SendMessage(peer, new PlayerSpawned { EntityId = session.Entity.Id, SpawnTransform = t }, DeliveryMethod.ReliableOrdered);
            Log.Information("Spawned player {User} as Entity {Id}", session.Username, session.Entity.Id);
        });
    }

    // Definition building lives in EntityDefinitionFactory so the streaming path and the map/acoustics
    // tests share one implementation — the client builds its acoustic map (regions AND portals) from
    // these, so any divergence would only surface as a wrong-sounding room.
    private EntityDefinition CreateDefinition(World world, Entity e) => EntityDefinitionFactory.From(world, e);

    private readonly HashSet<int> _addedEntitiesBuffer = new();

    private void BroadcastWorldState(long tick)
    {
        int dirtyCount = _dirtyAudioEntities.Count;
        int[] dirtyRented = ArrayPool<int>.Shared.Rent(Math.Max(dirtyCount, 1));
        int i = 0;
        while (_dirtyAudioEntities.TryDequeue(out int id)) { dirtyRented[i++] = id; }
        dirtyCount = i;

        foreach (var mapEntry in _maps.GetAllMaps())
        {
            var world = mapEntry.Value.world;
            var grid = mapEntry.Value.grid;
            var sessionsInMap = _sessions.GetSessionsInMap(mapEntry.Key);
            
            foreach (var session in sessionsInMap)
            {
                try
                {
                    if (session.Entity == Entity.Null) continue;
                    var pPos = world.Get<Transform>(session.Entity).Position;
                    var peer = _network.GetPeer(session.ConnectionId);
                    if (peer == null) continue;

                    _reusableBroadcast.Tick = tick;
                    _reusableBroadcast.LastProcessedSequenceId = session.LastProcessedSequenceId;
                    _reusableBroadcast.States.Clear();
                    _addedEntitiesBuffer.Clear();

                    foreach (var e in grid.GetItemsInRadius(pPos, EarshotRange))
                    {
                        if (!_addedEntitiesBuffer.Add(e.Id)) continue;
                        if (!world.Has<Transform>(e)) continue;
                        ref var t = ref world.Get<Transform>(e);

                        bool isDynamic = world.Has<Velocity>(e) || world.Has<PlayerComponent>(e);
                        if (isDynamic || t.IsDirty)
                        {
                            bool isDirtyAudio = false;
                            for (int j = 0; j < dirtyCount; j++) if (dirtyRented[j] == e.Id) { isDirtyAudio = true; break; }

                            if (isDirtyAudio)
                                _network.SendMessage(peer, CreateDefinition(world, e), DeliveryMethod.ReliableOrdered);

                            var state = new NetworkEntityState {
                                EntityId = e.Id,
                                Transform = QuantizedTransform.FromTransform(t)
                            };
                            if (world.Has<Velocity>(e)) state.LinearVelocity = world.Get<Velocity>(e).Linear;
                            if (world.Has<BeaconComponent>(e))
                            {
                                var beacon = world.Get<BeaconComponent>(e);
                                state.ExtraData = new BeaconData { Frequency = beacon.Frequency, Interval = beacon.Interval };
                            }
                            _reusableBroadcast.States.Add(state);
                        }
                    }

                    _network.SendMessage(peer, _reusableBroadcast, DeliveryMethod.Unreliable);

                    var health = world.Get<HealthComponent>(session.Entity);
                    var stats = GetMaterialUnderPlayer(world, grid, pPos);
                    _network.SendMessage(peer, new StatsUpdate {
                        Health = health.Current, MaxHealth = health.Max,
                        CurrentMaterial = stats.matType, CurrentVariant = stats.variant
                    }, DeliveryMethod.ReliableOrdered);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "BroadcastWorldState: Error broadcasting to session {User}. Skipping.", session.Username);
                }
            }

            // Cleanup dirty flags after broadcast
            world.Query(new QueryDescription().WithAll<Transform>(), (ref Transform t) => { t.IsDirty = false; });
        }

        ArrayPool<int>.Shared.Return(dirtyRented);
    }

    private (string matType, string variant) GetMaterialUnderPlayer(World world, SpatialGrid<Entity> grid, Vector3 pos)
    {
        float groundY = PhysicsUtils.GetGroundHeight(world, grid, pos, out string floorMat);
        return (floorMat ?? "Generic", "0");
    }

    private void HandlePeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (_sessions.TryRemoveSession(peer.Id, out var s))
        {
            if (s.Entity != Entity.Null && _maps.TryGetMap(s.CurrentMapId, out var world, out var _, out var _, out var _)) 
            {
                _maps.UnregisterEntity(s.CurrentMapId, s.Entity.Id);
                world.Destroy(s.Entity);
            }
        }
    }

    private void HandleInteract(NetPeer peer, InteractRequest interact)
    {
        if (!_sessions.TryGetSession(peer.Id, out var session)) return;

        if (interact.TargetEntityId.HasValue)
        {
            if (_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out var lookup))
            {
                if (lookup.TryGetValue(interact.TargetEntityId.Value, out var targetEntity))
                {
                    var pPos = world.Get<Transform>(session.Entity).Position;
                    var tPos = world.Get<Transform>(targetEntity).Position;
                    if (Vector3.Distance(pPos, tPos) > 5.0f)
                    {
                        _network.SendMessage(peer, new TextEvent { Text = "Interaction rejected: Target too far away (> 5.0m)." }, DeliveryMethod.ReliableOrdered);
                        return;
                    }
                }
            }
        }

        _network.SendMessage(peer, new TextEvent { Text = $"Interaction '{interact.Action}' received." }, DeliveryMethod.ReliableOrdered);
    }

    private void HandleRegister(NetPeer peer, RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username)) return;
        _userRepo.AddUser(request.Username, request.Password, UserRole.Player);
        _network.SendMessage(peer, new RegisterResponse { Success = true, Message = "Registration Successful." }, DeliveryMethod.ReliableOrdered);
    }

    private void HandleTextCommand(NetPeer peer, TextCommand cmd) => _commands.HandleTextCommand(peer, cmd);

    private void BroadcastEnvironment()
    {
        var state = _environment.GetCurrentState();
        var update = new WorldStateUpdate {
            GameTime = state.GameTime, Season = _environment.GetSeason(),
            Temperature = state.Temperature, Humidity = state.Humidity, 
            AirPressure = state.AirPressure, WindVelocity = state.WindVelocity,
            WindGustiness = state.WindGustiness, PrecipitationIntensity = state.PrecipitationIntensity
        };
        foreach(var session in _sessions.GetAllSessions())
        {
            var peer = _network.GetPeer(session.ConnectionId);
            if (peer != null) _network.SendMessage(peer, update, DeliveryMethod.ReliableOrdered);
        }
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        // Bootstrap Serilog first so startup errors are captured.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/server.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            using var serviceProvider = ConfigureServices().BuildServiceProvider();
            serviceProvider.GetRequiredService<GameServer>().Start(33288);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL ERROR] Server failed to start: {ex}");
            Log.Fatal(ex, "Server failed to start.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static IServiceCollection ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddDbContext<AppDbContext>(opts =>
            opts.UseSqlite("Data Source=openfps.db"),
            ServiceLifetime.Transient);

        services.AddSingleton<IUserRepository>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext>>();
            return new SqliteUserRepository(opts);
        });

        services.AddSingleton<GameServer>();
        return services;
    }
}
