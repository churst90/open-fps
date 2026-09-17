using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Runtime.InteropServices;
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
    private readonly VehicleSystem _vehicles = new();
    private readonly OccupancySystem _occupancy = new();
    private readonly DoorSystem _doors = new();
    private CompositeService _composites = null!;
    private OccupancyService _seats = null!;
    private HandsService _hands = null!;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _commandBuffer = new();

    private readonly MessageDispatcher _dispatcher = new();

    // Login and registration both verify a bcrypt hash on the tick thread. Six attempts in a burst, then
    // one every five seconds, per remote address — generous for a person mistyping a password, useless
    // for guessing one.
    private readonly RateLimiter _authLimiter = new(capacity: 6, refillPerSecond: 0.2);
    private DiscoveryService _discovery = null!;
    private SocialService _social = null!;
    private MapAuthorityService _mapAuthority = null!;
    private MudGateway _mudGateway = null!;
    private readonly ServerStateUpdate _reusableBroadcast = new();
    private readonly ServerStateUpdate _reliableBroadcast = new();

    public GameServer(IUserRepository userRepo)
    {
        _userRepo = userRepo;
    }

    public void EnqueueCommand(Action action) => _commandBuffer.Enqueue(action);

    /// <summary>
    /// Runs everything queued for the tick thread. This is the only place world mutations from other
    /// threads — the MUD gateway's TCP tasks, command handlers, despawns — are allowed to happen.
    /// Returns how many ran.
    /// </summary>
    public int DrainCommandBuffer()
    {
        int ran = 0;
        while (_commandBuffer.TryDequeue(out var cmd))
        {
            ran++;
            try { cmd(); }
            catch (Exception ex) { Log.Error(ex, "Buffered command threw."); }
        }
        return ran;
    }

    /// <summary>
    /// Caps banked simulation time. Returns the accumulator to keep, and reports how many ticks' worth
    /// were thrown away — see <see cref="RunLoop"/> for why they must be.
    /// </summary>
    public static double ClampAccumulatorMs(double accumulatorMs, out int droppedTicks)
    {
        const double maxMs = MaxCatchUpSeconds * 1000.0;
        if (accumulatorMs <= maxMs) { droppedTicks = 0; return accumulatorMs; }
        droppedTicks = (int)((accumulatorMs - maxMs) / TickTimeMs);
        return maxMs;
    }

    /// <summary>
    /// The area-of-interest diff: what a client could see last broadcast and cannot see now. Those ids
    /// are ghosts on its side until it is told, because everything else about an entity is additive.
    /// </summary>
    public static void CollectDeparted(HashSet<int> previouslyVisible, HashSet<int> visibleNow, List<int> into)
    {
        into.Clear();
        foreach (int id in previouslyVisible)
            if (!visibleNow.Contains(id)) into.Add(id);
    }

    private volatile bool _isRunning = true;
    private long _currentTick = 0;

    /// <summary>
    /// Asks the loop to finish the current tick and shut down. Safe to call from a signal handler on any
    /// thread; teardown itself happens on the loop thread once it has left the tick, so nothing is
    /// disposed underneath a simulation step.
    /// </summary>
    public void Stop() => _isRunning = false;

    public void SyncAudioComponent(int entityId)
    {
        _dirtyAudioEntities.Enqueue(entityId);
    }

    /// <summary>
    /// Tells everyone who could hear it that something just happened.
    ///
    /// The one channel for every short sound the world makes. Until this existed the server's entire
    /// vocabulary for sound was "this entity carries a looping emitter", which is why glass breakage,
    /// gunfire and collisions are all written, tested and completely silent: there was no way to say
    /// "that just happened", only "that is always happening".
    ///
    /// Sent RELIABLY, because a transient is a one-off event that nothing will ever resend. A dropped
    /// state packet costs nothing — the next tick corrects it — and a dropped door is a door that
    /// opened in silence, which the player then walks into.
    ///
    /// Earshot is the map's own broadcast radius, which is sized from how far the loudest thing on it
    /// actually carries, so nothing needs a per-event range.
    /// </summary>
    public void EmitWorldAudio(string mapId, int sourceEntityId, string label,
                               IReadOnlyList<TransientSound> sounds)
    {
        if (sounds.Count == 0) return;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return;

        // Where it happened, for the earshot test: the loudest of its own sounds.
        Vector3 at = sounds[0].Position;
        float loudest = float.MinValue;
        foreach (var sound in sounds)
            if (sound.LevelDb > loudest) { loudest = sound.LevelDb; at = sound.Position; }

        float earshot = _maps.GetEarshotRange(mapId);
        var message = new WorldAudioEvent
        {
            SourceEntityId = sourceEntityId,
            Label = label,
            Sounds = new List<TransientSound>(sounds),
            Seed = _audioEventSeed++,
        };

        foreach (var session in _sessions.GetSessionsInMap(mapId))
        {
            if (session.Entity == Entity.Null || !world.IsAlive(session.Entity)) continue;
            if (Vector3.Distance(world.Get<Transform>(session.Entity).Position, at) > earshot) continue;
            SendToSession(session, message);
        }
    }

    /// <summary>
    /// Travels with every event so two of the same thing do not render bit-identically.
    ///
    /// Twenty rounds from one rifle that are the same twenty samples read as a recording, which is
    /// the one thing this engine exists not to sound like. It is shared rather than per-client so
    /// that two players standing together hear the same variation of the same event.
    /// </summary>
    private int _audioEventSeed = 1;

    // The tick rate lives in PhysicsConstants — the client predicts against the same number.
    private const double TickTimeMs = 1000.0 / PhysicsConstants.TickRate;
    /// <summary>Fallback only — the real radius is per map, from MapManager.GetEarshotRange.</summary>
    private const float EarshotRange = 200.0f; 

    public void Start(int port)
    {
        var prefabRepo = new PrefabRepository("prefabs");
        _mapRepo = new MapRepository("maps");
        _maps = new MapManager(_mapRepo, prefabRepo);
        _maps.Initialize();
        // Composites BEFORE vehicles and before the earshot pass: a placed building is geometry that
        // the acoustic scene, the spatial grid and the broadcast radius all have to account for.
        _composites = new CompositeService(_maps, prefabRepo, new CompositeRepository("composites"));
        _composites.PlaceRecorded(_maps);
        _vehicles.Spawn(_maps);
        // Now that every sound source exists, size each map's broadcast radius from it.
        _maps.RefreshEarshotRanges();
        _seats = new OccupancyService(_maps);
        _hands = new HandsService(_maps);
        _commands = new CommandHandler(_sessions, _maps, this, _composites, _seats, _hands);
        
        // Initialize new Service Architecture
        _discovery = new DiscoveryService(_dispatcher, _sessions, _maps);
        _social = new SocialService(_dispatcher);
        _mapAuthority = new MapAuthorityService(_dispatcher);
        
        RegisterHandlers();

        // Start MUD Gateway on port + 1 (e.g. 33289)
        _mudGateway = new MudGateway(port + 1, _dispatcher);
        _mudGateway.OnDisconnected = HandleMudDisconnected;
        _mudGateway.Start();

        _network.OnConnected = (peer) => Log.Information("Peer connected: {Id}", peer.Id);
        _network.OnDisconnected = HandlePeerDisconnected;
        _network.Start(port);
        RunLoop();
        Shutdown();
    }

    /// <summary>
    /// Ordered teardown, on the loop thread after the last tick: tell the players first, then close the
    /// sockets, then destroy the worlds. Previously Ctrl-C tore all three down at once, mid-tick.
    /// </summary>
    private void Shutdown()
    {
        Log.Information("Server shutting down: {Count} session(s) connected.", _sessions.Count);

        foreach (var session in _sessions.GetAllSessions())
        {
            try { SendToSession(session, new TextEvent { Text = "Server is shutting down." }); }
            catch (Exception ex) { Log.Debug(ex, "Shutdown notice failed for {User}.", session.Username); }
        }

        try { _mudGateway?.Stop(); }
        catch (Exception ex) { Log.Warning(ex, "Error stopping the MUD gateway."); }

        try { _network.Stop(); }
        catch (Exception ex) { Log.Warning(ex, "Error stopping the network service."); }

        try { _maps?.Shutdown(); }
        catch (Exception ex) { Log.Warning(ex, "Error tearing down map worlds."); }

        Log.Information("Server stopped cleanly.");
    }

    /// <summary>
    /// Sends to a session over whichever transport it actually has. A MUD session has no UDP peer, which
    /// is why every gameplay reply used to vanish for telnet players — including chat aimed at them.
    /// </summary>
    public void SendToSession(UserSession session, IMessage message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
    {
        var peer = _network.GetPeer(session.ConnectionId);
        if (peer != null) { _network.SendMessage(peer, message, delivery); return; }
        _mudGateway?.TrySend(session.ConnectionId, message);
    }

    /// <summary>Rate-limit key: the remote address, which a reconnecting attacker cannot cycle for free.</summary>
    private string RateKeyFor(int connectionId)
    {
        var peer = _network.GetPeer(connectionId);
        if (peer != null) return peer.Address?.ToString() ?? $"peer:{connectionId}";
        return _mudGateway?.GetRemoteAddress(connectionId) ?? $"conn:{connectionId}";
    }

    private void RegisterHandlers()
    {
        _dispatcher.RegisterHandler<LoginRequest>(HandleLogin);
        _dispatcher.RegisterHandler<RegisterRequest>(HandleRegister);
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
            // No peer lookup here any more: that guard was the only thing keeping MUD commands out, and
            // it dropped every one of them silently. Commands now run on the tick thread whatever the
            // transport, so a telnet client is just another session.
            if (req.Command.TrimStart('/').Equals("ready", StringComparison.OrdinalIgnoreCase)) HandlePlayerReady(id);
            else _commands.HandleTextCommand(id, req, reply);
        });
        _dispatcher.RegisterHandler<ChatMessage>((id, req, reply) => {
            if (_sessions.TryGetSession(id, out var sess)) 
            {
                Log.Information("[CHAT] {User}: {Text}", sess.Username, req.Text);
                var broadcast = new ChatMessage { Sender = sess.Username, Text = req.Text };
                foreach (var s in _sessions.GetAllSessions()) SendToSession(s, broadcast);
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

    private void RunLoop()
    {
        var stopwatch = Stopwatch.StartNew();
        double accumulator = 0;

        while (_isRunning)
        {
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            accumulator += elapsed;

            // A GC pause, a debugger break or a suspended host hands us an arbitrarily large elapsed
            // time. Unclamped, the loop then runs every banked tick back to back with no network poll
            // between them: players teleport, inputs arrive for ticks already simulated, and the catch-up
            // itself takes long enough to bank more time. Drop the excess and say so — the simulation
            // loses a few ticks of wall clock, which is the honest outcome, instead of fast-forwarding.
            accumulator = ClampAccumulatorMs(accumulator, out int droppedTicks);
            if (droppedTicks > 0)
                Log.Warning("Server loop fell behind; dropped {Dropped} catch-up tick(s).", droppedTicks);

            _network.PollEvents();
            while (accumulator >= TickTimeMs) { Update(_currentTick++); accumulator -= TickTimeMs; }

            // Costs nothing unless OPENFPS_PROFILE=1; see PerfProbe.
            PerfProbe.ReportIfDue(TimeSpan.FromSeconds(30), line => Log.Information("{Perf}", line));
            Thread.Sleep(1);
        }
    }

    private void Update(long tick)
    {
        using var _perf = PerfProbe.Measure("server.tick");
        try
        {
            // 1. Process Commands & Network Messages
            DrainCommandBuffer();
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
                    MovementSystem.Update(world, entry.Value.data.MinBound, entry.Value.data.MaxBound, grid, lookup, _sessions, _maps, dt);
                    AISystem.Update(world, lookup, dt);
                    _vehicles.Update(entry.Key, world, dt);

                    // ...and the people watching them. Only a source with a place and a size: no
                    // loop, no bed, and nothing in it that knows what a car is.
                    CrowdSystem.Update(entry.Key, world, lookup, AudioClock.Now, (crowdId, at, spec) =>
                        EmitWorldAudio(entry.Key, crowdId, "crowd", new[]
                        {
                            new TransientSound
                            {
                                Character = SoundCharacter.Knock,
                                Position = at,
                                LevelDb = Applause.LevelDb(spec.Clappers, spec.Intensity),
                                SynthKey = Applause.Key(spec),
                                DecaySeconds = spec.Seconds,
                                Noisiness = 1f,
                                // How far across they are. A stand is not a firework: inside the patch
                                // the people fill, moving does not change the level, and the falling
                                // off only starts once the whole crowd is in front of you.
                                ExtentMetres = Applause.SpreadRadiusMetres(spec.Clappers),
                            },
                        }));
                    // Driven composites move AFTER the players who are steering them have had their
                    // say, and BEFORE anything is carried: the order here is the whole contract.
                    // Parts are bolted to the root and follow it exactly; occupants are carried by it
                    // but keep their own heads, so they come last of all.
                    DrivingSystem.Update(world, grid, entry.Value.data.MinBound, entry.Value.data.MaxBound, dt,
                                         (id, label, sounds) => EmitWorldAudio(entry.Key, id, label, sounds));
                    // Doors swing BEFORE the parts are placed: a door in a building is one of its
                    // parts, and ParentSystem writes every part's world transform from its local one
                    // each tick, so a swing applied after it would be overwritten before anyone saw
                    // it. The announcement re-sends the door's definition, which is how the aperture
                    // reaches the client's acoustic map.
                    _doors.Update(world, dt, SyncAudioComponent,
                                  (id, label, sounds) => EmitWorldAudio(entry.Key, id, label, sounds));
                    ParentSystem.Update(world, lookup);
                    _occupancy.Update(world, lookup);
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

    private void HandleLogin(int connectionId, LoginRequest request, Action<IMessage> reply)
    {
        // One path for both transports. The MUD gateway used to have its own copy of this, which is how
        // it ended up with neither the rate limit nor the spawn.
        if (!_authLimiter.TryConsume(RateKeyFor(connectionId)))
        {
            Log.Warning("Login rate limit hit for connection {Id} (user '{User}').", connectionId, request.Username);
            reply(new LoginResponse { Success = false, Message = "Too many attempts. Wait a few seconds and try again." });
            return;
        }

        if (!_userRepo.VerifyPassword(request.Username, request.Password))
        {
            // A rejected login left no trace at all, which made "the client said nothing" impossible to
            // tell apart from "the client never asked". Log the attempt (never the password).
            Log.Warning("Login REJECTED for user '{User}' on connection {Id}: invalid credentials.",
                request.Username, connectionId);
            reply(new LoginResponse { Success = false, Message = "Invalid Credentials" });
            return;
        }

        var user = _userRepo.GetUser(request.Username)!;
        var peer = _network.GetPeer(connectionId);
        var session = new UserSession
        {
            ConnectionId = connectionId,
            Username = user.Username,
            Role = user.Role,
            IsTextClient = peer == null,
            // Where a new player lands: the map that claims IsDefault, not the one named "default".
            CurrentMapId = _maps.DefaultMapId,
        };
        _sessions.AddSession(connectionId, session);

        Log.Information("User {User} authenticated ({Transport}), landing on map '{Map}'.",
                        user.Username, peer == null ? "MUD" : "UDP", session.CurrentMapId);

        reply(new LoginResponse
        {
            Success = true,
            Message = "Authenticated",
            Username = user.Username,
            Role = user.Role
        });

        // A text client has no geometry to load — it goes straight to 'ready'.
        if (peer == null)
        {
            reply(new TextEvent { Text = "Type 'ready' to enter the world, then 'scan' to look around." });
            return;
        }

        SendManifest(peer, session);
    }

    private void SendManifest(NetPeer peer, UserSession session)
    {
        string mapId = session.CurrentMapId;
        if (!_maps.TryGetMap(mapId, out var world, out var mapSize, out var _, out var _))
        {
            Log.Error("Login for {User}: map '{Map}' is not loaded; no manifest sent.", session.Username, mapId);
            _network.SendMessage(peer, new TextEvent { Text = $"Map '{mapId}' is not loaded on this server." }, DeliveryMethod.ReliableOrdered);
            return;
        }

        int staticCount = 0;
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) => {
            if (!world.Has<PlayerComponent>(e) && !world.Has<Velocity>(e)) staticCount++;
        });

        var manifest = new MapManifest
        {
            MapName = mapId,
            Checksum = _maps.GetMapChecksum(mapId),
            WorldSize = mapSize,
            ExpectedEntityCount = staticCount,
            SpawnPoint = new Transform { Position = new Vector3(0, 5, 0) },
            MapMin = new Vector3(-50, 0, -50),
            MapMax = new Vector3(50, 20, 50)
        };

        // Straight from the loaded map rather than re-reading every map file from disk per login.
        if (_maps.TryGetMapData(mapId, out var mapData))
        {
            manifest.VoxelResolution = mapData.VoxelResolution;
            manifest.OcclusionFloor = mapData.OcclusionFloor;
            manifest.SpawnPoint = new Transform { Position = mapData.SpawnPoint.Position, Rotation = mapData.SpawnPoint.Rotation };
            manifest.MinimumY = mapData.MinimumY;
            manifest.MapMin = mapData.MinBound;
            manifest.MapMax = mapData.MaxBound;
            manifest.Gravity = mapData.Gravity;
            manifest.AmbienceId = mapData.AmbienceId ?? "";
            manifest.Temperature = mapData.Temperature;
            manifest.Humidity = mapData.Humidity;
            manifest.AirPressure = mapData.AirPressure;
            manifest.AirAbsorptionMultiplier = mapData.AirAbsorptionMultiplier;
        }
        else
        {
            Log.Warning("Login for {User}: no authored data for map '{Map}'; sending defaults.", session.Username, mapId);
        }

        _network.SendMessage(peer, manifest, DeliveryMethod.ReliableOrdered);
        Log.Information("Manifest sent to {User}. Waiting for data request or ready.", session.Username);
    }

    private void HandleMapDataRequest(NetPeer peer, MapDataRequest request)
    {
        if (!_maps.TryGetMap(request.MapName, out var world, out var _, out var _, out var _)) return;
        if (!_sessions.TryGetSession(peer.Id, out var session)) return;

        var staticEntities = new List<Entity>();
        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) => {
            if (!world.Has<PlayerComponent>(e) && !world.Has<Velocity>(e)) staticEntities.Add(e);
        });

        Log.Information("Streaming {Count} entities to {Peer}...", staticEntities.Count, peer.Id);

        // The client clears its world on the manifest, so the server's record of what it knows starts
        // over here too — otherwise a re-request would leave the two disagreeing about a set the removal
        // logic is driven from.
        session.KnownEntities.Clear();
        session.VisibleDynamicEntities.Clear();

        foreach (var e in staticEntities)
        {
            _network.SendMessage(peer, CreateDefinition(world, e), DeliveryMethod.ReliableOrdered);
            session.KnownEntities.Add(e.Id);
        }

        _network.SendMessage(peer, new MapLoadComplete(), DeliveryMethod.ReliableOrdered);
    }

    private void HandlePlayerReady(int connectionId)
    {
        if (!_sessions.TryGetSession(connectionId, out var session)) return;
        if (session.Entity != Entity.Null) return; // already in the world
        string mapId = session.CurrentMapId;
        if (!_maps.TryGetMap(mapId, out var world, out var _, out var _, out var _))
        {
            Log.Error("Player {User} sent ready for map '{Map}', which is not loaded.", session.Username, mapId);
            return;
        }

        Transform spawnPoint = _maps.GetSpawnPoint(mapId);

        _commandBuffer.Enqueue(() => {
            session.Entity = world.Create();
            world.Add(session.Entity, new PlayerComponent { 
                    ConnectionId = connectionId, 
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

            // The one registration path: lookup, spatial index, dirty flag.
            _maps.IndexEntity(mapId, session.Entity);

            var t = world.Get<Transform>(session.Entity);
            SendToSession(session, new PlayerSpawned { EntityId = session.Entity.Id, SpawnTransform = t });
            Log.Information("Spawned player {User} as Entity {Id}", session.Username, session.Entity.Id);
        });
    }

    // Definition building lives in EntityDefinitionFactory so the streaming path and the map/acoustics
    // tests share one implementation — the client builds its acoustic map (regions AND portals) from
    // these, so any divergence would only surface as a wrong-sounding room.
    private EntityDefinition CreateDefinition(World world, Entity e) => EntityDefinitionFactory.From(world, e);

    private readonly HashSet<int> _visibleBuffer = new();
    private readonly HashSet<int> _visibleDynamicBuffer = new();
    private readonly HashSet<int> _dirtyAudioBuffer = new();
    private readonly List<int> _removedBuffer = new();

    private void BroadcastWorldState(long tick)
    {
        _dirtyAudioBuffer.Clear();
        while (_dirtyAudioEntities.TryDequeue(out int id)) _dirtyAudioBuffer.Add(id);

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
                    if (!world.IsAlive(session.Entity)) continue;
                    var pPos = world.Get<Transform>(session.Entity).Position;
                    float earshot = _maps.GetEarshotRange(mapEntry.Key);
                    var peer = _network.GetPeer(session.ConnectionId);
                    if (peer == null) continue;

                    // What the client needs to know about itself that is not in its transform: whether
                    // its position is its own to predict, or a seat's to decide.
                    int riding = world.Has<OccupantComponent>(session.Entity)
                        ? world.Get<OccupantComponent>(session.Entity).RootEntityId : -1;

                    _reusableBroadcast.Tick = tick;
                    _reusableBroadcast.LastProcessedSequenceId = session.LastProcessedSequenceId;
                    _reusableBroadcast.RidingEntityId = riding;
                    _reusableBroadcast.States.Clear();
                    _reliableBroadcast.Tick = tick;
                    _reliableBroadcast.LastProcessedSequenceId = session.LastProcessedSequenceId;
                    _reliableBroadcast.RidingEntityId = riding;
                    _reliableBroadcast.States.Clear();
                    _visibleBuffer.Clear();
                    _visibleDynamicBuffer.Clear();

                    foreach (var e in grid.GetItemsInRadius(pPos, earshot))
                    {
                        if (!_visibleBuffer.Add(e.Id)) continue;
                        if (!world.Has<Transform>(e)) continue;
                        ref var t = ref world.Get<Transform>(e);

                        bool isDynamic = world.Has<Velocity>(e) || world.Has<PlayerComponent>(e);
                        if (isDynamic) _visibleDynamicBuffer.Add(e.Id);

                        // A state message names an entity the client may never have heard of — every
                        // remote player, and anything spawned at runtime. Send the definition first, and
                        // only once: KnownEntities is what makes it once rather than every tick.
                        bool isNew = session.KnownEntities.Add(e.Id);
                        if (isNew || _dirtyAudioBuffer.Contains(e.Id))
                            _network.SendMessage(peer, CreateDefinition(world, e), DeliveryMethod.ReliableOrdered);

                        if (!isDynamic && !t.IsDirty && !isNew) continue;

                        var state = new NetworkEntityState {
                            EntityId = e.Id,
                            Transform = QuantizedTransform.FromTransform(t)
                        };
                        if (world.Has<Velocity>(e)) state.LinearVelocity = world.Get<Velocity>(e).Linear;
                        // How hard it is working its tyres. Sent because only the server can know it:
                        // a banked corner and a flat one look identical in a velocity, and a listener
                        // dividing lateral acceleration by flat grip reads every car on a banked oval
                        // as sliding. See EntityState.TyreDemand.
                        if (_vehicles.TryGetTyreDemand(e.Id, out float tyreDemand))
                            state.TyreDemand = NetworkEntityState.EncodeTyreDemand(tyreDemand);
                        if (world.Has<BeaconComponent>(e))
                        {
                            var beacon = world.Get<BeaconComponent>(e);
                            state.ExtraData = new BeaconData { Frequency = beacon.Frequency, Interval = beacon.Interval };
                        }

                        // A dynamic entity is corrected by the next tick's packet, so losing one costs
                        // nothing. A static entity that moved is a one-off event that nothing will ever
                        // resend — on the unreliable channel a single dropped packet leaves that client
                        // colliding with a wall that is no longer there. Reliable delivery IS the
                        // acknowledgement; there is no separate ack to wait for.
                        if (isDynamic) _reusableBroadcast.States.Add(state);
                        else _reliableBroadcast.States.Add(state);
                    }

                    // Anything dynamic this client could see and now cannot is a ghost on their side.
                    // Static geometry is never evicted: the client's acoustic map is built from the whole
                    // streamed map, so dropping a distant wall would change how the world sounds.
                    CollectDeparted(session.VisibleDynamicEntities, _visibleDynamicBuffer, _removedBuffer);

                    if (_removedBuffer.Count > 0)
                    {
                        foreach (int goneId in _removedBuffer)
                        {
                            session.VisibleDynamicEntities.Remove(goneId);
                            session.KnownEntities.Remove(goneId);
                        }
                        _network.SendMessage(peer, new EntityRemoved { EntityIds = new List<int>(_removedBuffer) },
                            DeliveryMethod.ReliableOrdered);
                    }

                    foreach (int id in _visibleDynamicBuffer) session.VisibleDynamicEntities.Add(id);

                    if (_reusableBroadcast.States.Count > 0)
                        _network.SendStateUpdate(peer, _reusableBroadcast, DeliveryMethod.Unreliable);
                    if (_reliableBroadcast.States.Count > 0)
                        _network.SendMessage(peer, _reliableBroadcast, DeliveryMethod.ReliableOrdered);

                    int health = 0, maxHealth = 0;
                    if (world.Has<HealthComponent>(session.Entity))
                    {
                        var h = world.Get<HealthComponent>(session.Entity);
                        health = h.Current; maxHealth = h.Max;
                    }
                    var stats = GetMaterialUnderPlayer(world, grid, pPos);
                    _network.SendMessage(peer, new StatsUpdate {
                        Health = health, MaxHealth = maxHealth,
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
    }

    private (string matType, string variant) GetMaterialUnderPlayer(World world, SpatialGrid<Entity> grid, Vector3 pos)
    {
        float groundY = PhysicsUtils.GetGroundHeight(world, grid, pos, out string floorMat);
        return (floorMat ?? "Generic", "0");
    }

    private void HandleMudDisconnected(int connectionId)
    {
        if (!_sessions.TryRemoveSession(connectionId, out var s)) return;
        Log.Information("MUD session {Id} ({User}) ended.", connectionId, s.Username);
        DespawnSession(s);
    }

    private void HandlePeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_sessions.TryRemoveSession(peer.Id, out var s)) return;
        Log.Information("Peer {Id} ({User}) disconnected: {Reason}", peer.Id, s.Username, info.Reason);
        DespawnSession(s);
    }

    /// <summary>
    /// Removes a session's body from the world and tells everyone who could see it. Without the second
    /// half every other client keeps the corpse forever: it still occupies space, still answers scans,
    /// and still plays whatever sound it carried.
    /// </summary>
    private void DespawnSession(UserSession session)
    {
        if (session.Entity == Entity.Null) return;
        var entity = session.Entity;
        string mapId = session.CurrentMapId;
        session.Entity = Entity.Null;

        _commandBuffer.Enqueue(() => {
            _maps.DestroyEntity(mapId, entity);
            BroadcastEntityRemoved(mapId, entity.Id);
        });
    }

    /// <summary>
    /// Tells every session on a map that an entity is gone, and forgets it on their behalf.
    ///
    /// Public because /undo destroys things too, and an entity destroyed without this stays on every
    /// client forever: still in their acoustic map, still occluding, still answering a scan. Every
    /// other message about an entity is additive, so this is the only thing that can take one back.
    /// </summary>
    public void BroadcastRemoval(string mapId, int entityId) => BroadcastEntityRemoved(mapId, entityId);

    private void BroadcastEntityRemoved(string mapId, int entityId)
    {
        foreach (var other in _sessions.GetSessionsInMap(mapId))
        {
            other.VisibleDynamicEntities.Remove(entityId);
            if (!other.KnownEntities.Remove(entityId)) continue;
            SendToSession(other, new EntityRemoved { EntityIds = new List<int> { entityId } });
        }
    }

    /// <summary>
    /// The interact key, which is mostly a door handle.
    ///
    /// Getting into and out of things is what "interact" means almost every time anyone presses it
    /// near a composite, so it is what the key does: press it beside a car and you are in it, press
    /// it again and you are out. That the same key does both is not a shortcut — from inside the
    /// thing, the only interaction there is IS getting out.
    ///
    /// Runs on the tick thread through the command buffer, like every other world-touching handler.
    /// Reading the Arch world from the network thread raced the simulation.
    /// </summary>
    private void HandleInteract(NetPeer peer, InteractRequest interact)
    {
        if (!_sessions.TryGetSession(peer.Id, out var session)) return;

        EnqueueCommand(() =>
        {
            void Say(string text) =>
                _network.SendMessage(peer, new TextEvent { Text = text }, DeliveryMethod.ReliableOrdered);

            if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out _)
                || session.Entity == Entity.Null || !world.IsAlive(session.Entity))
            {
                Say("You are not in the world yet.");
                return;
            }

            var position = world.Get<Transform>(session.Entity).Position;

            // A door in reach comes first, and from a seat that means the door beside YOU. The
            // sequence a person expects falls straight out of it: press it once beside a car and the
            // door opens, press it again and you are in; sitting in one, press it once and your door
            // opens, again and you are out. Climbing in through a shut door would be the alternative,
            // and it is not one.
            if (OpenDoorInReach(world, position, Say)) return;

            if (world.Has<OccupantComponent>(session.Entity))
            {
                _seats.Exit(session, out string leaving);
                Say(leaving);
                return;
            }
            // The client points at the nearest entity it knows about, which beside a car is usually
            // one of its doors rather than the car. Either names the thing.
            int root = -1;
            if (interact.TargetEntityId.HasValue) root = RootOf(session.CurrentMapId, interact.TargetEntityId.Value);
            if (root < 0) root = _seats.NearestEnterable(session.CurrentMapId, position, OccupancyService.BoardingRange);

            if (root >= 0)
            {
                _seats.Enter(session, root, null, out string entering);
                Say(entering);
                return;
            }

            if (interact.TargetEntityId.HasValue
                && _maps.TryGetMap(session.CurrentMapId, out var w, out _, out _, out var lookup)
                && lookup.TryGetValue(interact.TargetEntityId.Value, out var target)
                && w.Has<Transform>(target)
                && Vector3.Distance(position, w.Get<Transform>(target).Position) > PhysicsConstants.InteractionRange)
            {
                Say("Interaction rejected: Target too far away.");
                return;
            }

            Say($"Interaction '{interact.Action}' received.");
        });
    }

    /// <summary>
    /// Opens the shut door within arm's length, if there is one. Says so, and says nothing otherwise.
    ///
    /// Only SHUT doors count, which is what turns the interact key into a SEQUENCE rather than a
    /// toggle that fights you: once the door is open, the key moves on to meaning "get in" or "get
    /// out". Somebody who wants it shut again says so.
    /// </summary>
    private static bool OpenDoorInReach(World world, Vector3 position, Action<string> say)
    {
        Entity? nearest = null;
        float best = PhysicsConstants.InteractionRange;
        string name = "door";
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent d) =>
        {
            if (d.Target > 0f) return;                    // already open, or on its way
            float distance = Vector3.Distance(position, t.Position);
            if (distance > best) return;
            best = distance; nearest = e;
            name = world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
                 ? world.Get<IdentityComponent>(e).Name : "door";
        });

        if (nearest == null) return false;
        if (!DoorSystem.Set(world, nearest.Value, open: true)) return false;
        say($"The {name} swings open.");
        return true;
    }

    /// <summary>
    /// The composite an entity belongs to, if it is one or is part of one, else -1.
    ///
    /// Pointing at a door is pointing at the car. Nothing a player can pick out by proximity is
    /// reliably the root — the root is an origin on the ground in the middle of the thing, which is
    /// exactly where nobody is standing.
    /// </summary>
    private int RootOf(string mapId, int entityId)
    {
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return -1;
        if (!lookup.TryGetValue(entityId, out var e) || !world.IsAlive(e)) return -1;
        if (world.Has<OccupancyComponent>(e)) return e.Id;
        if (world.Has<ParentComponent>(e))
        {
            int parentId = world.Get<ParentComponent>(e).ParentEntityId;
            if (lookup.TryGetValue(parentId, out var parent)
                && world.IsAlive(parent) && world.Has<OccupancyComponent>(parent)) return parentId;
        }
        return -1;
    }

    private void HandleRegister(int connectionId, RegisterRequest request, Action<IMessage> reply)
    {
        if (!_authLimiter.TryConsume(RateKeyFor(connectionId)))
        {
            Log.Warning("Register rate limit hit for connection {Id}.", connectionId);
            reply(new RegisterResponse { Success = false, Message = "Too many attempts. Wait a few seconds and try again." });
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            reply(new RegisterResponse { Success = false, Message = "A username is required." });
            return;
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            reply(new RegisterResponse { Success = false, Message = "A password is required." });
            return;
        }

        // The reply used to be an unconditional Success = true, so a duplicate username told the player
        // their account was created and then refused every login with it.
        bool created = _userRepo.AddUser(request.Username, request.Password, UserRole.Player);
        reply(created
            ? new RegisterResponse { Success = true, Message = "Registration Successful." }
            : new RegisterResponse { Success = false, Message = "That username is already taken." });
    }

    /// <summary>
    /// Sends the world state to every graphical session, built PER MAP.
    ///
    /// The weather is global, but two of the fields in the message are not: air pressure is altitude
    /// and the air-absorption multiplier is authored tuning, and both belong to the map the player is
    /// standing on. Sending one message to everybody meant a player on a mountain map heard sea-level
    /// air — and, worse, <c>AirAbsorptionMultiplier</c> was never assigned at all, so it arrived as 0
    /// and the client's `distance / max(0.1, multiplier)` guard silently multiplied the absorption
    /// distance by ten, switching air absorption off for the whole game.
    /// </summary>
    private void BroadcastEnvironment()
    {
        string season = _environment.GetSeason();
        var perMap = new Dictionary<string, WorldStateUpdate>();

        foreach (var session in _sessions.GetAllSessions())
        {
            if (session.IsTextClient) continue; // nothing to render it with

            if (!perMap.TryGetValue(session.CurrentMapId, out var update))
            {
                var atmosphere = _maps.TryGetMapData(session.CurrentMapId, out var mapData)
                    ? new MapAtmosphere(mapData.Temperature, mapData.Humidity, mapData.AirPressure, mapData.AirAbsorptionMultiplier)
                    : MapAtmosphere.Default;

                var state = _environment.GetStateForMap(atmosphere);
                update = new WorldStateUpdate {
                    GameTime = state.GameTime, Season = season,
                    Temperature = state.Temperature, Humidity = state.Humidity,
                    AirPressure = state.AirPressure, AirAbsorptionMultiplier = state.AirAbsorptionMultiplier,
                    WindVelocity = state.WindVelocity,
                    WindGustiness = state.WindGustiness, PrecipitationIntensity = state.PrecipitationIntensity
                };
                perMap[session.CurrentMapId] = update;
            }

            SendToSession(session, update);
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
            var server = serviceProvider.GetRequiredService<GameServer>();

            // Ctrl-C and SIGTERM both ask the loop to finish its tick and tear down in order. Cancelling
            // the signal is the point: the default action kills the process where it stands, with sockets
            // open, ECS worlds live and SQLite mid-write.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Log.Information("Ctrl-C received; shutting down.");
                server.Stop();
            };
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
            {
                ctx.Cancel = true;
                Log.Information("SIGTERM received; shutting down.");
                server.Stop();
            });

            // --port lets a second server be brought up beside a running one, which is the only way
            // to smoke-test a map change without taking someone's session down.
            int port = 33288;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--port" && int.TryParse(args[i + 1], out int p)) port = p;
            server.Start(port);
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
