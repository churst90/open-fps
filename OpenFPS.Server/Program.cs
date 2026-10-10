using System.Diagnostics;
using System.Numerics;
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
    private SessionManager _sessions = new();
    private readonly WorldEnvironmentSystem _environment = new();

    /// <summary>The weather: time, season, sky and wind, for every map (/weather sets it).</summary>
    public WorldEnvironmentSystem WorldEnvironment => _environment;
    /// <summary>When and where the storm flashes. The thunder is each client's own (see EmitStrike).</summary>
    private readonly LightningSystem _lightning = new();
    private MapRepository _mapRepo = null!;
    private MapManager _maps = null!;
    private readonly IUserRepository _userRepo;
    private CommandHandler _commands = null!;
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _dirtyAudioEntities = new();
    private readonly VehicleSystem _vehicles = new();
    private readonly PedestrianSpeech _speech = new();
    private readonly CharacterSystem _characters = new();
    private readonly RailSystem _rail = new();
    private CrossingSystem _crossings = null!;

    private readonly OccupancySystem _occupancy = new();
    private readonly DoorSystem _doors = new();
    private CompositeService _composites = null!;
    private OccupancyService _seats = null!;
    private HandsService _hands = null!;
    /// <summary>Where a player was, their health and their things, kept while they are away. Null in a
    /// test rig until Attach.</summary>
    private PlayerStore? _store;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _commandBuffer = new();

    private readonly MessageDispatcher _dispatcher = new();

    // Who may log in and create accounts, and how often: the limits and the lockout live there. See
    // docs/SERVER_SECURITY.md.
    private readonly AuthService _auth;

    /// <summary>Logins being checked off the tick thread now. Past <see cref="MaxPendingLogins"/> a new one
    /// is told the server is busy, not queued: bcrypt is slow on purpose, and a queue is something an
    /// attacker can fill.</summary>
    private int _pendingLogins;
    public const int MaxPendingLogins = 8;

    /// <summary>A connection that has not logged in by now is closed.</summary>
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(120);
    private FriendRepository _friends = null!;
    /// <summary>Roles administrators made (/role). In memory until Run gives it its file.</summary>
    public RoleRepository Roles { get; private set; } = new RoleRepository(null);
    /// <summary>The teams players have made (teams.json). Null in a test rig that has none; Start()
    /// always makes one. Read at spawn, so a body is born wearing its team.</summary>
    public TeamRepository? Teams { get; set; }
    private MudGateway _mudGateway = null!;
    private readonly ServerStateUpdate _reusableBroadcast = new();
    private readonly ServerStateUpdate _reliableBroadcast = new();

    public GameServer(IUserRepository userRepo)
    {
        _userRepo = userRepo;
        _auth = new AuthService(userRepo);
        // Nothing but a login or a registration is heard from a connection that has not logged in.
        _dispatcher.IsAuthenticated = id => _sessions.TryGetSession(id, out _);
        // Limits follow the account, so reconnecting does not refill them.
        _dispatcher.KeyOf = id => _sessions.TryGetSession(id, out var s) ? s.Username : null;
    }

    /// <summary>The login rules and their state, for the admin's commands.</summary>
    public AuthService Auth => _auth;

    /// <summary>Every message sent to a session, as it is sent. Tests watch it; nothing else should.</summary>
    internal Action<UserSession, IMessage>? Sent;

    /// <summary>Every message the world-state broadcast sends, as it is sent. Tests watch it; with it set,
    /// a session with no socket is broadcast to as well, so the broadcast can be run without one.</summary>
    internal Action<UserSession, IMessage>? Broadcasted;

    /// <summary>One world-state broadcast, as the tick runs it. For tests.</summary>
    internal void BroadcastForTest(long tick) => BroadcastWorldState(tick);

    public void EnqueueCommand(Action action) => _commandBuffer.Enqueue(action);

    /// <summary>Things to do on the tick thread a while from now (a teleporter's charge), by when.</summary>
    private readonly List<(DateTime DueUtc, Action Run)> _later = new();

    /// <summary>Runs <paramref name="action"/> on the tick thread once <paramref name="delay"/> has passed:
    /// at the first command drain after it. A zero delay runs it in the drain that is running now.</summary>
    public void After(TimeSpan delay, Action action)
    {
        lock (_later) _later.Add((DateTime.UtcNow + delay, action));
    }

    /// <summary>The traffic, walkers and parked aircraft: what /spawn and /give add to.</summary>
    public VehicleSystem Vehicles => _vehicles;
    /// <summary>People with names on the maps (Alex), and what the street says.</summary>
    public CharacterSystem Characters => _characters;
    public PedestrianSpeech StreetSpeech => _speech;
    /// <summary>The trains: what /spawn train adds to.</summary>
    public RailSystem Rail => _rail;

    /// <summary>Takes out the delayed actions that are due, in the order they were asked for.</summary>
    private List<Action> TakeDue()
    {
        var due = new List<Action>();
        lock (_later)
        {
            if (_later.Count == 0) return due;
            var now = DateTime.UtcNow;
            for (int i = 0; i < _later.Count; i++)
                if (_later[i].DueUtc <= now) { due.Add(_later[i].Run); _later.RemoveAt(i--); }
        }
        return due;
    }

    /// <summary>The one world (docs/WORLD_STREAMING.md, stage 2): its frames, tiles and places. Null on a
    /// server started without it (a test's).</summary>
    public OpenFPS.Server.OneWorld.WorldMaps? World { get; private set; }

    /// <summary>The world's store and the service that makes its tiles, from the server's world.json.</summary>
    public void StartWorld(OpenFPS.Server.OneWorld.WorldSettings settings, OpenFPS.Server.OneWorld.IElevationSource? survey = null)
    {
        try
        {
            var store = new OpenFPS.Server.OneWorld.WorldStore(settings.StorePath, settings.CapBytes);
            survey ??= settings.Generate ? new OpenFPS.Server.OneWorld.Usgs3Dep() : new OpenFPS.Server.OneWorld.NoNewTiles();
            var service = new OpenFPS.Server.OneWorld.WorldTileService(store, survey, settings.MaxAtOnce);
            // The maps of real places, copied into the world's tiles; tiles copied from an older map go.
            var copied = OpenFPS.Server.OneWorld.WorldPlaces.FromMaps(_maps);
            int stale = copied.DropStale(store);
            if (stale > 0) Log.Information("World: {Count} stored tile(s) of the places will be copied again from their maps.", stale);
            World = new OpenFPS.Server.OneWorld.WorldMaps(_maps, service, () => _sessions.GetAllSessions(),
                                                        OpenFPS.Server.OneWorld.WorldMaps.LoadPlaces(_maps), copied);
            // A frame over a real place has its traffic.
            World.FrameMade += mapId => EnqueueCommand(() => _vehicles?.SpawnMap(_maps, _composites, mapId));
            // The rings round the listed places, made after anything a player wants.
            if (settings.Generate && settings.Prebuild) World.Prebuild();
            Log.Information("World: tiles kept in {Path}, at most {Cap:F1} GB ({Have:F2} GB in {Count} tiles now); {Places} place(s) to arrive at; {Making}.",
                            store.Root, store.CapBytes / 1073741824.0, store.TotalBytes / 1073741824.0, store.Count, World.Places.Count,
                            settings.Generate ? "new tiles made from USGS 3DEP" : "no new tiles made");
            // A cap the disk cannot hold fills the disk before the cap is reached.
            try
            {
                long free = new DriveInfo(store.Root).AvailableFreeSpace;
                if (free + store.TotalBytes < store.CapBytes)
                    Log.Warning("World: the disk under {Path} has {Free:F1} GB free, less than the store's cap of {Cap:F1} GB; "
                                + "set CapGigabytes in world.json below what the disk can spare, or StorePath to a bigger disk.",
                                store.Root, free / 1073741824.0, store.CapBytes / 1073741824.0);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "World: could not be started; the maps work as before.");
            World = null;
        }
    }

    /// <summary>A player into a frame of the world, stood where they arrive.</summary>
    private void ArriveInWorld(UserSession session, string mapId, Vector3 at)
    {
        session.ArriveAt = (mapId, at, true);
        MoveToMap(session, mapId);
    }

    /// <summary>The world's part of a tick, and the commands it queues: for a test without the loop.</summary>
    internal void WorldTickForTest()
    {
        DrainCommandBuffer();
        World?.Update(ArriveInWorld);
        World?.SettleForTest();
        DrainCommandBuffer();
    }

    /// <summary>Gives an unstarted server the maps and sessions a test built, so <see cref="MoveToMap"/>
    /// and spawning run without a socket.</summary>
    public void Attach(MapManager maps, SessionManager sessions, OccupancyService? seats = null, HandsService? hands = null)
    {
        _maps = maps;
        _sessions = sessions;
        _seats = seats!;
        _hands = hands!;
        _store = new PlayerStore(_userRepo, maps, hands ?? new HandsService(maps));
    }

    /// <summary>Runs everything queued for the tick thread, the only place other threads' world changes
    /// (the MUD gateway, commands, despawns) may happen. Returns how many ran.</summary>
    public int DrainCommandBuffer()
    {
        int ran = 0;
        // The queue, then whatever is due (which may queue more), until both are empty: a few rounds
        // at most, so a delayed action that queues a change of map has it done in the same drain.
        for (int round = 0; round < 4; round++)
        {
            while (_commandBuffer.TryDequeue(out var cmd))
            {
                ran++;
                try { cmd(); }
                catch (Exception ex) { Log.Error(ex, "Buffered command threw."); }
            }
            var due = TakeDue();
            if (due.Count == 0) break;
            foreach (var action in due)
            {
                ran++;
                try { action(); }
                catch (Exception ex) { Log.Error(ex, "Delayed command threw."); }
            }
        }
        return ran;
    }

    /// <summary>Caps banked simulation time: the accumulator to keep, and how many ticks were thrown away
    /// (<see cref="RunLoop"/> says why they must be).</summary>
    public static double ClampAccumulatorMs(double accumulatorMs, out int droppedTicks)
    {
        const double maxMs = MaxCatchUpSeconds * 1000.0;
        if (accumulatorMs <= maxMs) { droppedTicks = 0; return accumulatorMs; }
        droppedTicks = (int)((accumulatorMs - maxMs) / TickTimeMs);
        return maxMs;
    }

    /// <summary>What a client could see last broadcast and cannot now: ghosts on its side until it is told,
    /// since everything else about an entity is additive.</summary>
    public static void CollectDeparted(HashSet<int> previouslyVisible, HashSet<int> visibleNow, List<int> into)
    {
        into.Clear();
        foreach (int id in previouslyVisible)
            if (!visibleNow.Contains(id)) into.Add(id);
    }

    private volatile bool _isRunning = true;
    private long _currentTick = 0;

    /// <summary>Asks the loop to finish the tick and shut down. Safe from a signal handler on any thread:
    /// teardown runs on the loop thread, so nothing is disposed under a simulation step.</summary>
    public void Stop() => _isRunning = false;

    public void SyncAudioComponent(int entityId)
    {
        _dirtyAudioEntities.Enqueue(entityId);
    }

    /// <summary>The models the world editor has changed, every version kept (model_versions/). In memory
    /// only until <see cref="Start"/> gives it its folder.</summary>
    public OpenFPS.Server.Editor.ModelStore Models { get; set; } = new(null);

    /// <summary>The entities whose definitions go out again at the next broadcast. For tests.</summary>
    internal IReadOnlyCollection<int> PendingDefinitionResends => _dirtyAudioEntities.ToArray();

    /// <summary>
    /// Puts a player's team on their body and re-sends its definition, which carries the team and is
    /// otherwise sent once: without it a teammate's beacon would keep its old tone until they rejoined.
    /// On the tick thread.
    /// </summary>
    public void RefreshTeam(string username)
    {
        if (Teams == null) return;
        var session = _sessions.GetAllSessions().FirstOrDefault(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (session == null || session.Entity == Entity.Null) return;
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out _) || !world.IsAlive(session.Entity)) return;
        if (!world.Has<PlayerComponent>(session.Entity)) return;
        ref var player = ref world.Get<PlayerComponent>(session.Entity);
        string team = Teams.NameOf(session.Username);
        if (player.Team == team) return;
        player.Team = team;
        SyncAudioComponent(session.Entity.Id);
    }

    /// <summary>
    /// Tells everyone in earshot that something just happened: the one channel for every short sound.
    /// Sent reliably, because nothing resends a one-off event (a dropped door is a door that opened in
    /// silence). Earshot is the map's broadcast radius, sized from its loudest source.
    /// </summary>
    public void EmitWorldAudio(string mapId, int sourceEntityId, string label,
                               IReadOnlyList<TransientSound> sounds)
    {
        if (sounds.Count == 0) return;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return;
        // People in the street hear it too, and some of them say so.
        _speech.Heard(mapId, label, sounds, AudioClock.Now);

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
            float apart = Vector3.Distance(world.Get<Transform>(session.Entity).Position, at);
            if (apart > earshot) continue;
            // A text player is told the words, so only the words they could make out.
            if (session.IsTextClient && label.StartsWith("speech: ", StringComparison.Ordinal)
                && apart > Speech.MadeOutMetres) continue;
            SendToSession(session, message);
        }
    }

    private readonly List<HeldSky> _heldSkies = new();

    /// <summary>One tick of lightning: the server's storm, and the storm of each map that holds its own weather.</summary>
    internal void Lightning(float dt)
    {
        _heldSkies.Clear();
        foreach (var entry in _maps.GetAllMaps())
            if (OpenFPS.Server.Editor.MapSettings.WeatherOf(entry.Value.data.HeldWeather) is WeatherType held)
                _heldSkies.Add(new HeldSky(entry.Key, held, _environment.GetStateForMap(MapAtmosphere.Of(entry.Value.data))));
        _lightning.Update(dt, _environment.CurrentScenario, _environment.GetCurrentState(), _heldSkies, EmitStrike);
    }

    /// <summary>
    /// A lightning flash, told to everyone on the maps under that sky: every map that follows the server's
    /// (<paramref name="mapId"/> null), or the one map that holds the weather it came from. Not through
    /// <see cref="EmitWorldAudio"/>: thunder carries twenty kilometres, far past any broadcast radius. The
    /// storm is drawn round each map's centre; each client renders the thunder where its listener stands,
    /// and a text player is not sent it.
    /// </summary>
    internal void EmitStrike(LightningStrike strike, string? mapId)
    {
        foreach (var entry in _maps.GetAllMaps())
        {
            var data = entry.Value.data;
            bool holds = OpenFPS.Server.Editor.MapSettings.WeatherOf(data.HeldWeather) != null;
            if (mapId == null ? holds : !entry.Key.Equals(mapId, StringComparison.OrdinalIgnoreCase)) continue;
            var world = entry.Value.world;
            var centre = (data.MinBound + data.MaxBound) * 0.5f;
            var placed = strike.Offset(new Vector3(centre.X, 0f, centre.Z));
            WorldAudioEvent? message = null;
            foreach (var session in _sessions.GetSessionsInMap(entry.Key))
            {
                if (session.IsTextClient || session.Entity == Entity.Null || !world.IsAlive(session.Entity)) continue;
                var at = world.Get<Transform>(session.Entity).Position;
                var flat = new Vector2(at.X - placed.Centre.X, at.Z - placed.Centre.Z);
                if (flat.Length() > LightningPhysics.SendRangeMetres) continue;
                message ??= new WorldAudioEvent
                {
                    SourceEntityId = -1,
                    Label = placed.Kind == FlashKind.CloudToGround ? "lightning" : "lightning in the cloud",
                    Sounds = new List<TransientSound> { LightningSystem.SoundFor(placed) },
                    Seed = _audioEventSeed++,
                };
                SendToSession(session, message);
            }
        }
    }

    /// <summary>Travels with every event so two of the same thing do not render bit-identically (twenty
    /// identical rounds read as a recording). One for all clients, so players together hear the same.</summary>
    private int _audioEventSeed = 1;

    // The tick rate lives in PhysicsConstants — the client predicts against the same number.
    private const double TickTimeMs = 1000.0 / PhysicsConstants.TickRate;

    /// <summary>The map players land on, from <c>--map</c>; null leaves it to whichever claims IsDefault.</summary>
    public string? RequestedMapId { get; set; }

    public void Start(int port)
    {
        // Machines an author has written, before anything asks what a vehicle name means: a map's
        // cars, a composite made drivable, and /drivable all resolve through the registry.
        OpenFPS.Common.MachineRegistry.EnsureLoaded();
        OpenFPS.Common.ModelLibrary.EnsureLoaded();
        var prefabRepo = new PrefabRepository("prefabs");
        // The world editor's changed models, over the library and the prefabs, before anything asks what
        // a model is or a map is built from them. Prefabs and groups are kinds of the server's own.
        Models = new OpenFPS.Server.Editor.ModelStore("model_versions");
        Models.Catalog.Add(new OpenFPS.Server.Editor.PrefabKind(prefabRepo));
        Models.Catalog.Add(new OpenFPS.Server.Editor.GroupKind(Models, prefabRepo));
        Models.LoadAll();
        _mapRepo = new MapRepository("maps");
        // Who owns each map, whether it is public and who is invited: beside teams.json, and laid over
        // each map's own file as it loads (MapAccessRepository). The world editor's edits are laid over
        // it too, from maps/overlays (docs/WORLD_EDITOR.md section 7).
        _maps = new MapManager(_mapRepo, prefabRepo)
        {
            RequestedMapId = RequestedMapId,
            Access = new MapAccessRepository("map_access.json"),
            Overlays = new OpenFPS.Server.Editor.MapOverlayStore(_mapRepo.OverlayDirectory),
        };
        _maps.Initialize();
        // Composites BEFORE vehicles and before the earshot pass: a placed building is geometry that
        // the acoustic scene, the spatial grid and the broadcast radius all have to account for.
        _composites = new CompositeService(_maps, prefabRepo, new CompositeRepository("composites"));
        _composites.PlaceRecorded(_maps);
        _vehicles.Spawn(_maps, _composites);
        // Somebody with a name and a day of their own (Alex): made once the map's places are known.
        _characters.Spawn(_maps);
        _speech.CharacterView = _characters.ViewOf;
        _speech.FaceCharacter = _characters.Face;
        _rail.Spawn(_maps);
        _crossings = new CrossingSystem(_rail, SyncAudioComponent);
        _rail.CrossingsOn = _crossings.PositionsOn;
        _rail.Heard = (map, id, label, sounds) => EmitWorldAudio(map, id, label, sounds);
        // After the rail: a crossing derives its geometry from the lines the trains are on, so
        // those lines have to exist first.
        _crossings.Spawn(_maps);
        // And the road has to be able to ask a crossing whether it is closed.
        _vehicles.SetCrossings(_crossings);
        // Horns, and anything else the drivers do, go out on the one channel every short sound uses.
        _vehicles.Heard = (map, id, label, sounds) => EmitWorldAudio(map, id, label, sounds);
        _vehicles.Removed = BroadcastRemoval;
        _vehicles.AudioChanged = SyncAudioComponent;
        // Now that every sound source exists, size each map's broadcast radius from it.
        _maps.RefreshEarshotRanges();
        _seats = new OccupancyService(_maps, EmitWorldAudio);
        _hands = new HandsService(_maps) { Carried = SyncAudioComponent, Removed = BroadcastRemoval };
        _store = new PlayerStore(_userRepo, _maps, _hands);
        StartWorld(OpenFPS.Server.OneWorld.WorldSettings.Load());
        // Beside openfps.db and motd.txt, in the server's working folder. See FriendRepository for
        // why this is a file and not a table.
        _friends = new FriendRepository("friends.json");
        Roles = new RoleRepository("roles.json");
        // Beside it, and a file for the same reason. See TeamRepository.
        Teams = new TeamRepository("teams.json");
        // A pedestrian shot dead is taken off the street (their body stays, as an item), and a minute
        // later somebody else walks their walk.
        var combat = new CombatService(_maps, this, _sessions)
        {
            RetireWalker = _vehicles.RetireWalker,
            ReplaceWalker = _vehicles.ReplaceWalker,
            Possessions = _hands,
            // The admin gun freezes what the traffic moves, and forgets a vehicle before vaporizing it.
            MovedByTraffic = _vehicles.Moves,
            ForgetVehicle = _vehicles.Forget,
        };
        // A flown bullet slows in the map's air and drifts in the server's wind.
        combat.Weather = mapId =>
        {
            var state = _environment.GetStateForMap(_maps.TryGetMapData(mapId, out var m)
                ? MapAtmosphere.Of(m)
                : MapAtmosphere.Default);
            return (state.Temperature, state.AirPressure, state.WindVelocity, state.WindGustiness);
        };
        _commands = new CommandHandler(_sessions, _maps, this, _composites, _seats, _hands, _userRepo, _friends, combat);
        // Vehicles parked with the world editor are kept in its overlays, not the map files.
        _commands.Editor.ParkKeptVehicles();

        // These register their handlers with the dispatcher, which is what keeps them alive.
        _ = new DiscoveryService(_dispatcher, _sessions, _maps, () => World);
        _ = new SocialService(_dispatcher, _sessions, _friends);
        
        RegisterHandlers();

        // The MUD gateway listens on the next port up.
        _mudGateway = new MudGateway(port + 1, _dispatcher);
        _mudGateway.OnDisconnected = HandleMudDisconnected;
        _mudGateway.Start();

        _network.OnConnected = (peer) => Log.Information("Peer connected: {Id}", peer.Id);
        // Voice is relayed the moment it comes in, not at the next tick: same dispatcher, same thread.
        _network.HandleNow = (peer, msg) =>
        {
            if (msg is not VoiceData) return false;
            _dispatcher.Dispatch(peer.Id, msg, reply => _network.SendMessage(peer, reply, DeliveryMethod.ReliableOrdered));
            return true;
        };
        _network.OnDisconnected = HandlePeerDisconnected;
        _network.Start(port);
        RunLoop();
        Shutdown();
    }

    /// <summary>Ordered teardown, on the loop thread after the last tick: store and tell the players, then
    /// close the sockets, then destroy the worlds.</summary>
    private void Shutdown()
    {
        Log.Information("Server shutting down: {Count} session(s) connected.", _sessions.Count);

        // Everybody's place and things into the store before the worlds go. What is still queued runs
        // first: a disconnect waiting in the buffer is a body still to be stored.
        try { DrainCommandBuffer(); }
        catch (Exception ex) { Log.Warning(ex, "Error running the last queued commands."); }
        foreach (var session in _sessions.GetAllSessions())
        {
            try { LeaveWorld(session); }
            catch (Exception ex) { Log.Error(ex, "Could not store {User} at shutdown.", session.Username); }
        }

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

        // When each tile was last visited, for the store's cap after the restart.
        try { World?.Service.Store.SaveIndex(); }
        catch (Exception ex) { Log.Warning(ex, "Error writing the world store's index."); }

        Log.Information("Server stopped cleanly.");
    }

    /// <summary>
    /// Somebody saying something: to their own map, which is what plain typing does, or to everyone
    /// on the server with /all. Private messages and the server's own announcements go elsewhere.
    /// </summary>
    public void Chat(UserSession from, string text, ChatChannel channel)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (from.MutedUntilUtc > DateTime.UtcNow)
        {
            int minutes = (int)Math.Ceiling((from.MutedUntilUtc - DateTime.UtcNow).TotalMinutes);
            SendToSession(from, new TextEvent { Text = $"You are muted for {minutes} more minute{(minutes == 1 ? "" : "s")}." });
            return;
        }
        // To your team: everyone in it who is on, on whatever map they are, and nobody else.
        TeamRepository.Team? team = null;
        if (channel == ChatChannel.Team && (team = Teams?.TeamOf(from.Username)) == null)
        {
            SendToSession(from, new TextEvent { Text = "You are not in a team." });
            return;
        }
        Log.Information("[CHAT {Channel}] {User}: {Text}", channel, from.Username, text);
        var line = ChatFrom(from, text, channel);
        var to = channel switch
        {
            ChatChannel.All => _sessions.GetAllSessions(),
            ChatChannel.Team => _sessions.GetAllSessions().Where(s => team!.Has(s.Username)),
            _ => _sessions.GetSessionsInMap(from.CurrentMapId),
        };
        foreach (var s in to) SendToSession(s, line);
    }

    /// <summary>
    /// A player's line as everybody gets it: their name, their team and their role's title, which each
    /// client puts together ("admin [Mafia] Owner: hello"). A team line leaves the team out: everybody
    /// hearing it is in it.
    /// </summary>
    public ChatMessage ChatFrom(UserSession from, string text, ChatChannel channel) => new()
    {
        Sender = from.Username, Text = text, Channel = channel,
        FromStaff = OpenFPS.Server.Core.Permissions.IsStaff(from.Role),
        Team = channel == ChatChannel.Team ? "" : Teams?.NameOf(from.Username) ?? "",
        Title = OpenFPS.Server.Core.Permissions.ChatTitle(from.Role, from.CustomRole),
    };

    /// <summary>The server speaking to everyone: an announcement, sent as "Server".</summary>
    public void Announce(string text, bool fromStaff)
    {
        var line = new ChatMessage { Sender = "Server", Text = text, Channel = ChatChannel.Server, FromStaff = fromStaff };
        foreach (var s in _sessions.GetAllSessions()) SendToSession(s, line);
    }

    /// <summary>
    /// Tells everyone else on the server that somebody came, went, or is away: a line on the All
    /// channel, marked as a presence notice so each client can play its own sound for it, or not.
    /// Not sent to the person it is about, who knows already and is told in their own words.
    /// </summary>
    internal void AnnouncePresence(UserSession who, PresenceKind kind, string? text = null)
    {
        text ??= PresenceText(who.Username, kind);
        Log.Information("[PRESENCE] {Text}", text);
        var line = new ChatMessage { Sender = who.Username, Text = text, Channel = ChatChannel.All, Presence = kind };
        foreach (var s in _sessions.GetAllSessions())
            if (s != who) SendToSession(s, line);
    }

    /// <summary>The words of a presence notice.</summary>
    public static string PresenceText(string name, PresenceKind kind) => kind switch
    {
        PresenceKind.LoggedIn => $"{name} is online.",
        PresenceKind.LoggedOut => $"{name} logged out.",
        PresenceKind.WentOffline => $"{name} lost connection.",
        PresenceKind.Away => $"{name} is away.",
        PresenceKind.Back => $"{name} is back.",
        _ => name,
    };

    /// <summary>
    /// Says a change between away and here, once. Called after anything that can change it: a
    /// command, /afk, activity after being away. And once a second for the players who have gone
    /// quiet, since doing nothing has no event of its own. On the tick thread.
    /// </summary>
    internal void UpdatePresence(UserSession s, DateTime nowUtc)
    {
        // A session that has gone is not away; it is gone, and was announced as such.
        if (!_sessions.TryGetSession(s.ConnectionId, out var current) || current != s) return;
        bool away = s.IsAway(nowUtc);
        if (away == s.AnnouncedAway) return;
        s.AnnouncedAway = away;
        AnnouncePresence(s, away ? PresenceKind.Away : PresenceKind.Back);
    }

    internal void UpdatePresenceForAll(DateTime nowUtc)
    {
        foreach (var s in _sessions.GetAllSessions()) UpdatePresence(s, nowUtc);
    }

    /// <summary>
    /// The message of the day: motd.txt in the server's working folder, beside maps/ and prefabs/ —
    /// OpenFPS.Server/motd.txt under run-server.sh. Read when it is asked for, so an edit (or
    /// /setmotd) takes effect without a restart. Sent to each player as they arrive in the world,
    /// from "Server", into the server buffer, and spoken.
    /// </summary>
    public static string MotdPath => Path.GetFullPath("motd.txt");

    public static string ReadMotd()
    {
        try { return File.Exists(MotdPath) ? File.ReadAllText(MotdPath).Trim() : ""; }
        catch (IOException) { return ""; }
    }

    public static void WriteMotd(string text) => File.WriteAllText(MotdPath, text.Trim() + Environment.NewLine);

    /// <summary>Sends to a session over whichever transport it has: a MUD session has no UDP peer.</summary>
    public void SendToSession(UserSession session, IMessage message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
    {
        Sent?.Invoke(session, message);
        var peer = _network.GetPeer(session.ConnectionId);
        if (peer != null) { _network.SendMessage(peer, message, delivery); return; }
        _mudGateway?.TrySend(session.ConnectionId, message);
    }

    /// <summary>
    /// The remote address of a connection, whichever transport it is on: what the rate limits count
    /// and what the admin's commands show. A reconnecting attacker cannot cycle it for free.
    /// </summary>
    public string RemoteAddressOf(int connectionId)
    {
        var peer = _network.GetPeer(connectionId);
        if (peer != null) return peer.Address?.ToString() ?? $"peer:{connectionId}";
        return _mudGateway?.GetRemoteAddress(connectionId) ?? $"conn:{connectionId}";
    }

    /// <summary>One-way latency in milliseconds for a UDP session; null for a MUD session.</summary>
    public int? PingOf(int connectionId) => _network.PingOf(connectionId);

    /// <summary>When a connection was opened, by either transport; null if it is not open.</summary>
    public DateTime? ConnectedSince(int connectionId)
        => _network.ConnectedSince(connectionId) ?? _mudGateway?.ConnectedSince(connectionId);

    /// <summary>Open connections, by either transport, that have not logged in.</summary>
    public List<(int Id, string Transport, string Address, DateTime SinceUtc)> PendingConnections()
    {
        var list = new List<(int, string, string, DateTime)>();
        foreach (var (id, since) in _network.Connections())
            if (!HasSession(id)) list.Add((id, "UDP", RemoteAddressOf(id), since));
        if (_mudGateway != null)
            foreach (var (id, since) in _mudGateway.Connections())
                if (!HasSession(id)) list.Add((id, "MUD", RemoteAddressOf(id), since));
        list.Sort((a, b) => a.Item4.CompareTo(b.Item4));
        return list;
    }

    /// <summary>
    /// Marks a session as doing something, which is what /sessions reports as idle time and what
    /// ends being away. Called for what a player does, not for what their client sends on its own.
    /// </summary>
    private void Touch(int connectionId)
    {
        if (!_sessions.TryGetSession(connectionId, out var s)) return;
        s.LastActivityUtc = DateTime.UtcNow;
        s.Away = false;
        // Only queued when there is something to say: this runs for every input frame that moves.
        if (s.AnnouncedAway) EnqueueCommand(() => UpdatePresence(s, DateTime.UtcNow));
    }

    private void RegisterHandlers()
    {
        _dispatcher.RegisterHandler<LoginRequest>(HandleLogin);
        _dispatcher.RegisterHandler<RegisterRequest>(HandleRegister);
        _dispatcher.RegisterHandler<LogoutRequest>((id, req, reply) => LogOut(id));
        _dispatcher.RegisterHandler<MapDataRequest>((id, req, reply) => {
            var peer = _network.GetPeer(id);
            if (peer != null) HandleMapDataRequest(peer, req);
        });
        _dispatcher.RegisterHandler<ClientInputUpdate>((id, req, reply) => {
            if (!_sessions.TryGetSession(id, out var s)) return;
            if (req.SequenceId <= s.LastProcessedSequenceId) return;
            // A client sends input every frame whether or not anyone is at the keys; only input that
            // asks for something counts as the player being there.
            if (req.MoveDirection != Vector3.Zero || req.LookDelta != Vector2.Zero || req.Jump) Touch(id);
            // Bounded: MovementSystem spends real time, not queue depth, so a flood gains nothing.
            if (s.InputQueue.Count >= MaxQueuedInputs)
            {
                s.DroppedInputs++;
                _dispatcher.Limits.Abuse.Note(s.Username, "input", $"input queue full; dropping inputs ({s.DroppedInputs} so far)");
                return;
            }
            s.InputQueue.Enqueue(req);
        });
        _dispatcher.RegisterHandler<TextCommand>((id, req, reply) => {
            // No UDP peer lookup: a MUD session has none, and the guard would drop its every command.
            if (req.Command.TrimStart('/').Equals("ready", StringComparison.OrdinalIgnoreCase)) HandlePlayerReady(id);
            else _commands.HandleTextCommand(id, req, reply);
        });
        // A shot through a scope carries its own aim; see ScopedShot.
        _dispatcher.RegisterHandler<ScopedShot>((id, req, reply) => {
            if (!_sessions.TryGetSession(id, out var shooter)) return;
            Touch(id);
            _commands.Combat.FireScoped(shooter, req, reply);
        });
        _dispatcher.RegisterHandler<ChatMessage>((id, req, reply) => {
            if (!_sessions.TryGetSession(id, out var sess)) return;
            Touch(id);
            Chat(sess, req.Text, req.Channel is ChatChannel.All or ChatChannel.Team ? req.Channel : ChatChannel.Map);
        });
        _dispatcher.RegisterHandler<InventoryRequest>((id, req, reply) => {
            if (!_sessions.TryGetSession(id, out var sess)) return;
            Touch(id);
            EnqueueCommand(() => reply(_hands.List(sess)));
        });
        _dispatcher.RegisterHandler<InteractRequest>((id, req, reply) => {
            var peer = _network.GetPeer(id);
            Touch(id);
            if (peer != null) HandleInteract(peer, req);
        });
        _dispatcher.RegisterHandler<VoiceData>((id, req, reply) => {
            if (!_sessions.TryGetSession(id, out var senderSession)) return;
            // The sender is the session, not the packet's SenderId: listeners place the voice at that
            // entity, and a forged id would put your words in somebody else's mouth.
            if (senderSession.Entity == Entity.Null || req.OpusData.Length > MaxVoiceBytes) return;
            req.SenderId = senderSession.Entity.Id;
            Touch(id);
            // Relayed to everyone else on the same map, at once; each listener places it at the sender.
            int relayed = 0;
            foreach (var s in _sessions.GetAllSessions())
            {
                if (s.ConnectionId == id) continue;
                if (s.CurrentMapId != senderSession.CurrentMapId) continue;
                var peer = _network.GetPeer(s.ConnectionId);
                if (peer != null)
                {
                    _network.SendMessage(peer, req, LiteNetLib.DeliveryMethod.Unreliable);
                    relayed++;
                }
            }
            _network.Flush();
            NoteVoice(senderSession, req.OpusData.Length, relayed);
        });
    }

    /// <summary>Voice traffic per sender, logged every ten seconds while they talk, so "he could not hear
    /// me" can be checked against whether the frames arrived.</summary>
    private readonly Dictionary<string, (int Frames, long Bytes, int RelayedTo, DateTime Since)> _voiceTally = new();

    private void NoteVoice(UserSession sender, int bytes, int relayedTo)
    {
        var now = DateTime.UtcNow;
        var t = _voiceTally.TryGetValue(sender.Username, out var had) ? had : (0, 0L, 0, now);
        t = (t.Item1 + 1, t.Item2 + bytes, relayedTo, t.Item4);
        if ((now - t.Item4).TotalSeconds >= 10)
        {
            double seconds = Math.Max(1, (now - t.Item4).TotalSeconds);
            Log.Information("Voice: {User} sent {Frames} frames ({Kbps:F0} kbit/s) on {Map}, relayed to {Players} player(s).",
                            sender.Username, t.Item1, t.Item2 * 8 / seconds / 1000, sender.CurrentMapId, relayedTo);
            t = (0, 0L, relayedTo, now);
        }
        _voiceTally[sender.Username] = t;
    }

    /// <summary>One voice packet: an Opus frame is at most 1275 bytes, and a packet carries one.</summary>
    public const int MaxVoiceBytes = 4000;

    private void RunLoop()
    {
        var stopwatch = Stopwatch.StartNew();
        double accumulator = 0;

        while (_isRunning)
        {
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            accumulator += elapsed;

            // A GC pause or a suspended host banks any amount of time. Run back to back with no network
            // poll between, the catch-up teleports players and banks more time itself: drop the excess.
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
            DrainCommandBuffer();
            while (_network.TryDequeueMessage(out var item))
            {
                _dispatcher.Dispatch(item.peer.Id, item.message, msg => _network.SendMessage(item.peer, msg, DeliveryMethod.ReliableOrdered));
            }

            float dt = FixedDeltaTime;
            _environment.Update(dt);
            Lightning(dt);
            // Horns whose key has not been reported down for a few ticks are let go.
            VehicleSignals.Update(dt);

            // The world's tiles round its players, every quarter of a second, before the maps are walked:
            // an arrival may make a frame.
            if (World != null && tick % 8 == 0)
            {
                using var _world = PerfProbe.Measure("server.world");
                World.Update(ArriveInWorld);
                if (tick % (TickRate * 30) == 0) World.Service.Store.SaveIndex();
            }
            // Tiles read since, into their frames, a few milliseconds' worth a tick.
            if (World != null)
            {
                using var _pump = PerfProbe.Measure("server.world.pump");
                World.Pump(OpenFPS.Server.OneWorld.WorldMaps.PumpBudget);
            }

            foreach (var entry in _maps.GetAllMaps())
            {
                try
                {
                    var world = entry.Value.world;
                    var grid = entry.Value.grid;
                    var lookup = entry.Value.lookup;

                    var stage = PerfProbe.Measure("server.grid");
                    grid.Clear();
                    world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) => {
                        grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false);
                    });

                    stage.Dispose();
                    stage = PerfProbe.Measure("server.movement");
                    MovementSystem.Update(world, entry.Value.data.WalkMin, entry.Value.data.WalkMax, grid, lookup, _sessions, _maps, dt);
                    stage.Dispose();
                    stage = PerfProbe.Measure("server.traffic");
                    // The water on the roads first: traffic reads it under every wheel.
                    var roadWeather = _environment.GetStateForMap(_maps.TryGetMapData(entry.Key, out var waterMap)
                        ? MapAtmosphere.Of(waterMap)
                        : MapAtmosphere.Default);
                    RoadWaterSystem.Update(entry.Key, roadWeather, _environment.RainRate(roadWeather),
                                           _maps.TryGetRoads(entry.Key, out var waterRoads) ? waterRoads : null, dt);
                    _vehicles.Update(entry.Key, world, dt);
                    // Before the seats carry anybody: Alex gets on and off the bus here.
                    if (_characters.Count > 0)
                    {
                        var day = _environment.GetStateForMap(_maps.TryGetMapData(entry.Key, out var dayMap)
                            ? MapAtmosphere.Of(dayMap)
                            : MapAtmosphere.Default);
                        _characters.Update(entry.Key, world, grid, dt, day, _environment.CurrentScenario);
                    }
                    stage.Dispose();
                    stage = PerfProbe.Measure("server.rail+combat");
                    // Reloads that are due, the dead got up again or taken away.
                    _commands.Combat.Update(entry.Key, world);
                    _rail.Update(entry.Key, world, dt);
                    _crossings.Update(entry.Key, world, dt);

                    stage.Dispose();
                    stage = PerfProbe.Measure("server.speech+crowd");
                    // People in the street saying things to whoever they pass.
                    var weather = _environment.GetStateForMap(_maps.TryGetMapData(entry.Key, out var speechMap)
                        ? MapAtmosphere.Of(speechMap)
                        : MapAtmosphere.Default);
                    _speech.Update(entry.Key, world, AudioClock.Now,
                                   SpeechConditions.From(weather, _environment.CurrentScenario),
                                   (id, label, sound) => EmitWorldAudio(entry.Key, id, label, new[] { sound }));

                    // ...and the people watching them: a source with a place and a size, no loop.
                    CrowdSystem.Update(entry.Key, world, AudioClock.Now, (crowdId, at, spec) =>
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
                                // How far across they are: inside the patch the people fill, the level
                                // does not change as you move.
                                ExtentMetres = Applause.SpreadRadiusMetres(spec.Clappers),
                            },
                        }));
                    // Order matters: driven composites move after the players steering them and before
                    // anything is carried; parts follow the root exactly; occupants keep their own
                    // heads, so they come last.
                    stage.Dispose();
                    stage = PerfProbe.Measure("server.driving+doors+parents");
                    DrivingSystem.Update(world, grid, entry.Value.data.WalkMin, entry.Value.data.WalkMax, dt,
                                         (id, label, sounds) => EmitWorldAudio(entry.Key, id, label, sounds), entry.Key,
                                         _maps.TryGetTiles(entry.Key, out var driveTiles) && driveTiles.Dynamic
                                             ? new DrivingSystem.TileFence(driveTiles.IsReady, driveTiles.TileMetres) : null,
                                         root => TellDriver(world, root, "The road ahead is not built yet. Stopping here until it is."));
                    // The glass in every car's windows, toward wherever it was last sent.
                    WindowSystem.Update(world, dt);
                    // Doors swing before ParentSystem places the parts, or the swing of a door in a
                    // building is overwritten. Re-sending the door's definition carries its aperture to
                    // the client.
                    _doors.Update(world, dt, SyncAudioComponent,
                                  (id, label, sounds) => EmitWorldAudio(entry.Key, id, label, sounds));
                    ParentSystem.Update(world, lookup);
                    // Door leaves where they now are in the triangle world, and any static spawned this
                    // tick taken into it (docs/GEOMETRY.md stage 1).
                    _maps.SyncGeometry(entry.Key);
                    _occupancy.Update(world, lookup);
                    stage.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error updating map {MapId}", entry.Key);
                }
            }

            if (tick % TickRate == 0)  // once per second
            {
                BroadcastEnvironment();
                CloseConnectionsThatNeverLoggedIn();
                UpdatePresenceForAll(DateTime.UtcNow);
            }

            using (PerfProbe.Measure("server.broadcast")) BroadcastWorldState(tick);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fatal error in Update loop for tick {Tick}", tick);
        }
    }

    private void HandleLogin(int connectionId, LoginRequest request, Action<IMessage> reply)
        => _ = Login(connectionId, request, reply);

    /// <summary>
    /// A login, from either transport. The cheap refusals happen here; the password is checked on the
    /// thread pool (bcrypt on the tick thread stalled the world a tenth of a second a login), and the
    /// session is made on the tick thread. The task completes once that last step is queued.
    /// </summary>
    internal Task Login(int connectionId, LoginRequest request, Action<IMessage> reply)
    {
        if (_sessions.TryGetSession(connectionId, out var already))
        {
            // Replacing a live session here left its body in the world with nobody attached.
            reply(new LoginResponse { Success = false, Message = $"You are already logged in as {already.Username}." });
            return Task.CompletedTask;
        }

        string address = RemoteAddressOf(connectionId);
        if (!_auth.Admit(address))
        {
            reply(new LoginResponse { Success = false, Message = AuthService.TooManyAttempts });
            return Task.CompletedTask;
        }

        // A client built from a different OpenFPS.Common misreads every message after this one and
        // spawns into nonsense, so refuse it by name. The MUD gateway speaks text and has no contract.
        var peer = _network.GetPeer(connectionId);
        if (peer != null && request.Build != WireContract.Hash)
        {
            string theirs = request.Build.Length > 0 ? AuthService.ForLog(request.Build) : "an older one";
            Log.Warning("Login REFUSED for user '{User}' on connection {Id}: client build {Client}, server build {Server}.",
                AuthService.ForLog(request.Username), connectionId, theirs, WireContract.Hash);
            reply(new LoginResponse { Success = false, Message =
                $"This client does not match the server. Your build is {theirs}, the server's is {WireContract.Hash}. Get the client built from the same version as the server." });
            return Task.CompletedTask;
        }

        if (Interlocked.Increment(ref _pendingLogins) > MaxPendingLogins)
        {
            Interlocked.Decrement(ref _pendingLogins);
            Log.Warning("Login from {Address} refused: {Max} logins already being checked.", address, MaxPendingLogins);
            reply(new LoginResponse { Success = false, Message = "The server is busy. Try again in a few seconds." });
            return Task.CompletedTask;
        }

        string username = request.Username ?? "", password = request.Password ?? "";
        return Task.Run(() => _auth.Check(address, username, password)).ContinueWith(checking =>
        {
            Interlocked.Decrement(ref _pendingLogins);
            AuthOutcome outcome;
            if (checking.IsCompletedSuccessfully) outcome = checking.Result;
            else
            {
                Log.Error(checking.Exception, "Login check for {Address} failed.", address);
                outcome = new AuthOutcome(false, "The server could not check that login. Try again.");
            }
            EnqueueCommand(() => FinishLogin(connectionId, peer, address, outcome, reply));
        }, TaskScheduler.Default);
    }

    /// <summary>The tick-thread half of a login: the session, the takeover, and the map.</summary>
    private void FinishLogin(int connectionId, NetPeer? peer, string address, AuthOutcome outcome, Action<IMessage> reply)
    {
        // The connection may have gone while its password was being checked; a UDP id may even have
        // been handed to somebody else.
        bool gone = peer != null
            ? !ReferenceEquals(_network.GetPeer(connectionId), peer)
            : _mudGateway != null && !_mudGateway.IsMudConnection(connectionId);
        if (gone) { Log.Information("Login for connection {Id} finished after it closed; dropped.", connectionId); return; }

        if (!outcome.Success)
        {
            reply(new LoginResponse { Success = false, Message = outcome.Message });
            return;
        }
        if (_sessions.TryGetSession(connectionId, out var already))
        {
            // Two logins sent on one connection before either finished.
            reply(new LoginResponse { Success = false, Message = $"You are already logged in as {already.Username}." });
            return;
        }

        var user = outcome.User!;

        // One session per account, the newest winning: LiteNetLib takes seconds to notice a dropped
        // connection, and two bodies with one name confuse every command that finds a player by name.
        bool replacing = false;
        foreach (var old in _sessions.GetAllSessions()
                     .Where(s => s.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            EndSession(old, "Your account has logged in from somewhere else, so this session has been closed.");
            // Now and not when the queue gets to it: the old body's place and things go into the store
            // before this login reads them back out. The queued despawn then finds no body.
            LeaveWorld(old);
            replacing = true;
        }

        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            ConnectionId = connectionId,
            Username = user.Username,
            Role = user.Role,
            Grants = OpenFPS.Server.Core.Permissions.Parse(user.Permissions),
            CustomRole = user.CustomRole is { } custom && Roles.Exists(custom) ? custom : "",
            RolePermissions = Roles.PermissionsOf(user.CustomRole),
            IsTextClient = peer == null,
            // Where a new player lands: the map that claims IsDefault, not the one named "default".
            CurrentMapId = _maps.DefaultMapId,
            RemoteAddress = address,
            LoggedInUtc = now,
            LastActivityUtc = now,
        };
        // Back where they left from: the map now, the place when the body is made (HandlePlayerReady).
        // Read here on the tick thread, not from the record the password check fetched: a body of this
        // account leaving in between has written a newer one.
        if (_store != null)
        {
            session.Saved = _store.Load(user.Username);
            session.CurrentMapId = _store.LandingMap(session, session.Saved, session.CurrentMapId);
        }
        _sessions.AddSession(connectionId, session);

        Log.Information("User {User} authenticated ({Transport}) from {Address}, landing on map '{Map}'.",
                        user.Username, peer == null ? "MUD" : "UDP", address, session.CurrentMapId);
        // Taking over your own session is not arriving: no notice.
        if (!replacing) AnnouncePresence(session, PresenceKind.LoggedIn);

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

        // Left in the world: back to the same spot, through the loading screen, or the landing map if it
        // cannot be built in time.
        if (World != null && session.Saved?.Map is { } left && session.Saved.PlaceOn(left) is { } saved
            && OpenFPS.Server.OneWorld.WorldMaps.WhereSaved(left, new Vector3(saved.X, saved.Y, saved.Z), (float)(World.BaseYOf(left) ?? 0)) is { } spot)
        {
            Log.Information("{User} left in the world at {Frame}; arriving there again.", session.Username, left);
            // Under the height they left at if the frame's base is known; on top of whatever is there if not.
            World.ArriveAt(session, World.NearestPlace(spot.Zone, spot.North, spot.Easting, spot.Northing), spot.Zone, spot.North,
                           spot.Easting, spot.Northing, World.BaseYOf(left) is null ? null : spot.OverSea, LoginToWorldTimeout,
                           text => SendToSession(session, new TextEvent { Text = text }), () => SendManifest(session));
            return;
        }

        SendManifest(session);
    }

    /// <summary>How long a login back into the world waits for the ground where they left before landing them
    /// on the landing map instead.</summary>
    private static readonly TimeSpan LoginToWorldTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Leaving on purpose: the clean-up of a dropped connection, now rather than at the timeout,
    /// so everyone is told they logged out rather than lost the connection.</summary>
    internal void LogOut(int connectionId)
    {
        if (!_sessions.TryRemoveSession(connectionId, out var s)) return;
        Log.Information("Peer {Id} ({User}) logged out.", connectionId, s.Username);
        AnnouncePresence(s, PresenceKind.LoggedOut);
        DespawnSession(s);
        _network.Disconnect(connectionId);
        _mudGateway?.Disconnect(connectionId);
    }

    /// <summary>A moderator's /kick: the session ends as a duplicate login does, with the reason said,
    /// and everyone else is told that they were removed but not why.</summary>
    public void Kick(UserSession session, string reason)
        => EndSession(session, reason, $"{session.Username} was removed from the server.");

    /// <summary>
    /// Ends a session from the server's side: tells the player why, takes their body out as a disconnect
    /// does, and closes the connection (whose own disconnect event then finds no session). Everyone else
    /// hears the notice if there is one; a session replaced by a new login has none.
    /// </summary>
    private void EndSession(UserSession session, string reason, string? notice = null)
    {
        if (!_sessions.TryRemoveSession(session.ConnectionId, out _)) return;
        Log.Information("Session {Id} ({User}) ended by the server: {Reason}", session.ConnectionId, session.Username, reason);
        SendToSession(session, new TextEvent { Text = reason });
        if (notice != null) AnnouncePresence(session, PresenceKind.LoggedOut, notice);
        DespawnSession(session);
        _network.Disconnect(session.ConnectionId);
        _mudGateway?.Disconnect(session.ConnectionId);
    }

    /// <summary>Closes connections, by either transport, open for <see cref="LoginTimeout"/> without
    /// logging in.</summary>
    private void CloseConnectionsThatNeverLoggedIn()
    {
        var now = DateTime.UtcNow;
        foreach (int id in StaleUnauthenticated(_network.Connections(), HasSession, now))
        {
            Log.Information("Closing UDP connection {Id} from {Address}: no login within {Seconds} s.",
                            id, RemoteAddressOf(id), LoginTimeout.TotalSeconds);
            _network.Disconnect(id);
        }
        if (_mudGateway == null) return;
        foreach (int id in StaleUnauthenticated(_mudGateway.Connections(), HasSession, now))
        {
            Log.Information("Closing MUD connection {Id} from {Address}: no login within {Seconds} s.",
                            id, RemoteAddressOf(id), LoginTimeout.TotalSeconds);
            _mudGateway.Disconnect(id, "No login within two minutes. Goodbye.");
        }
    }

    private bool HasSession(int connectionId) => _sessions.TryGetSession(connectionId, out _);

    /// <summary>The connections opened longer than <see cref="LoginTimeout"/> ago that have no session.</summary>
    public static List<int> StaleUnauthenticated(IEnumerable<(int Id, DateTime SinceUtc)> connections,
                                                 Func<int, bool> hasSession, DateTime nowUtc)
        => connections.Where(c => nowUtc - c.SinceUtc > LoginTimeout && !hasSession(c.Id)).Select(c => c.Id).ToList();

    /// <summary>The map a session is on, described: what the client clears its world for and answers with
    /// a MapDataRequest. Internal so a test can start a join without a socket (the <see cref="Sent"/> hook).</summary>
    internal void SendManifest(UserSession session)
    {
        string mapId = session.CurrentMapId;
        if (!_maps.TryGetMap(mapId, out var world, out var mapSize, out var _, out var _))
        {
            Log.Error("Login for {User}: map '{Map}' is not loaded; no manifest sent.", session.Username, mapId);
            SendToSession(session, new TextEvent { Text = $"Map '{mapId}' is not loaded on this server." });
            return;
        }

        // The world editor's changed models first, at this map's pins or else current: every one is sent,
        // so a model pinned on the map just left is put back.
        foreach (var model in Models.UpdatesFor(_maps.Overlays?.Get(mapId).Pins)) SendToSession(session, model);

        int staticCount = 0;
        bool streamed = _maps.TryGetTiles(mapId, out var tiles);
        if (streamed)
        {
            // The count the client waits for: the tiles round where they will stand (worked out again,
            // with its own detail setting, when the data is asked for).
            var probe = new TileInterest { Radii = session.Tiles.Radii };
            staticCount = TileStreamer.Begin(probe, tiles, ArrivalPoint(session, mapId)).Count;
        }
        else world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) => {
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
            MapMax = new Vector3(50, 20, 50),
            TileMetres = streamed ? tiles.TileMetres : 0f,
        };

        if (_maps.TryGetMapData(mapId, out var mapData))
        {
            manifest.VoxelResolution = mapData.VoxelResolution;
            manifest.OcclusionFloor = mapData.OcclusionFloor;
            manifest.SpawnPoint = new Transform { Position = mapData.SpawnPoint.Position, Rotation = mapData.SpawnPoint.Rotation };
            manifest.MinimumY = mapData.MinimumY;
            manifest.OwnerId = mapData.OwnerId ?? "";
            manifest.IsPublic = mapData.IsPublic;
            manifest.MapMin = mapData.MinBound;
            manifest.MapMax = mapData.MaxBound;
            manifest.PlayMin = mapData.WalkMin;
            manifest.PlayMax = mapData.WalkMax;
            manifest.Gravity = mapData.Gravity;
            manifest.AmbienceId = mapData.AmbienceId ?? "";
            manifest.BeaconPolicy = mapData.BeaconPolicy == null ? Array.Empty<string>()
                : mapData.BeaconPolicy.Select(kv => $"{kv.Key}={kv.Value}").ToArray();
            manifest.Temperature = mapData.Temperature;
            manifest.Humidity = mapData.Humidity;
            manifest.AirPressure = mapData.AirPressure;
            manifest.AirAbsorptionMultiplier = mapData.AirAbsorptionMultiplier;
        }
        if (World != null && World.TryGetFrame(mapId, out var frame))
        {
            manifest.IsWorld = true;
            manifest.WorldZone = frame.Origin.Zone;
            manifest.WorldNorth = frame.Origin.North;
            manifest.FrameEasting = frame.Origin.Easting;
            manifest.FrameNorthing = frame.Origin.Northing;
            manifest.FrameBaseY = frame.BaseY;
        }
        else
        {
            Log.Warning("Login for {User}: no authored data for map '{Map}'; sending defaults.", session.Username, mapId);
        }

        session.AwaitingMapData = true;
        SendToSession(session, manifest);
        Log.Information("Manifest sent to {User}. Waiting for data request or ready.", session.Username);
    }

    private void HandleMapDataRequest(NetPeer peer, MapDataRequest request)
    {
        if (!_sessions.TryGetSession(peer.Id, out var session)) return;
        MapDataAsked(session, request);
    }

    /// <summary>A client asking for the map's data, as it does once after each manifest.</summary>
    internal void MapDataAsked(UserSession session, MapDataRequest request)
    {
        // Each asking sends the whole map; a client asks once per manifest.
        if (!session.AwaitingMapData)
        {
            _dispatcher.Limits.Abuse.Note(session.Username, "mapdata", "asked for map data with no manifest outstanding");
            return;
        }
        session.AwaitingMapData = false;
        if (request.FullDetailMetres > 0f || request.FarMetres > 0f)
            session.Tiles.Radii = StreamRadii.Clamp(request.FullDetailMetres, request.FarMetres);
        // On the world the join waits, on the loading screen, for the tiles round where they will stand.
        if (World != null && World.Hold(session, ArrivalPoint(session, session.CurrentMapId),
                                        () => SendMapData(session, request), (done, total, speak) => SayWorldLoading(session, done, total, speak)))
            return;
        SendMapData(session, request);
    }

    /// <summary>Says something to whoever is driving a vehicle, if a player is.</summary>
    private void TellDriver(World world, int rootId, string text)
    {
        var q = new QueryDescription().WithAll<OccupantComponent, PlayerComponent>();
        int connection = -1;
        world.Query(in q, (ref OccupantComponent o, ref PlayerComponent p) =>
        {
            if (o.RootEntityId == rootId && o.Controls) connection = p.ConnectionId;
        });
        if (connection >= 0 && _sessions.TryGetSession(connection, out var session))
            SendToSession(session, new TextEvent { Text = text });
    }

    /// <summary>How far the building of the world round an arriving player has got: on the loading screen,
    /// said aloud when <paramref name="speak"/>; to a text session, only what is said.</summary>
    private void SayWorldLoading(UserSession session, int done, int total, bool speak)
    {
        string text = done >= total ? "The world is built round you." : $"Building the world: {done} of {total} tiles.";
        if (_mudGateway?.IsMudConnection(session.ConnectionId) != true)
            SendToSession(session, new WorldLoading { Done = done, Total = total, Text = text, Speak = speak });
        else if (speak) SendToSession(session, new TextEvent { Text = text });
    }

    /// <summary>
    /// The map's fixed entities, in batches, then MapLoadComplete: the whole map, or on a map streamed in
    /// tiles, the tiles round where the player will arrive (TileStreamer), and a TileStreamUpdate saying
    /// which. Runs on the tick thread (the dispatcher's). Internal so a test can drive a join without a
    /// socket: the messages go through <see cref="SendToSession"/> and its <see cref="Sent"/> hook.
    /// </summary>
    internal void SendMapData(UserSession session, MapDataRequest request)
    {
        // Only the map you are on, or a private map's layout goes to somebody it refuses at the door.
        if (!string.Equals(request.MapName, session.CurrentMapId, StringComparison.OrdinalIgnoreCase)) return;
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out var _, out var _, out var lookup)) return;
        var started = Stopwatch.StartNew();

        // The client cleared its world on the manifest; the record of what it knows starts over with it.
        session.KnownEntities.Clear();
        session.VisibleDynamicEntities.Clear();
        session.SentStates.Clear();
        session.LastStats = null;
        var keepRadii = session.Tiles.Radii;
        session.Tiles.Reset();
        session.Tiles.Radii = keepRadii;

        List<Entity> staticEntities;
        bool streamed = _maps.TryGetTiles(session.CurrentMapId, out var tiles);
        if (streamed)
        {
            if (request.FullDetailMetres > 0f || request.FarMetres > 0f)
                session.Tiles.Radii = StreamRadii.Clamp(request.FullDetailMetres, request.FarMetres);
            var ids = TileStreamer.Begin(session.Tiles, tiles, ArrivalPoint(session, session.CurrentMapId));
            staticEntities = new List<Entity>(ids.Count);
            foreach (int id in ids)
                if ((lookup.TryGetValue(id, out var e) || tiles.TryGetGlobal(id, out e)) && world.IsAlive(e)) staticEntities.Add(e);
        }
        else
        {
            staticEntities = new List<Entity>();
            world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) => {
                if (!world.Has<PlayerComponent>(e) && !world.Has<Velocity>(e)) staticEntities.Add(e);
            });
        }

        Log.Information("Streaming {Count} entities to {User}...", staticEntities.Count, session.Username);

        // In batches: one reliable message per definition made the load wait on round trips.
        long bytes = 0;
        var batch = new EntityDefinitionBatch();
        foreach (var e in staticEntities)
        {
            batch.Definitions.Add(streamed ? TileStreamer.Definition(world, e, tiles, session.Tiles) : CreateDefinition(world, e));
            session.KnownEntities.Add(e.Id);
            if (batch.Definitions.Count < EntityDefinitionBatch.Size) continue;
            bytes += SendCounted(session, EntityDefinitionPack.Pack(batch));
            batch = new EntityDefinitionBatch();
        }
        if (batch.Definitions.Count > 0) bytes += SendCounted(session, EntityDefinitionPack.Pack(batch));

        if (streamed)
        {
            bytes += SendCounted(session, TileStreamer.Snapshot(session.Tiles, tiles, staticEntities.Count));
            session.Tiles.DefinitionsSent = staticEntities.Count;
            session.Tiles.BytesSent = bytes;
            int full = session.Tiles.Levels.Count(kv => kv.Value == TileDetail.Full);
            Log.Information("Join of {Map} for {User}: {Full} full and {Coarse} coarse tile(s) of {All} ({FullM:F0} m and {FarM:F0} m), "
                          + "{Count} of {Total} entities, {KB:F0} KB, {Ms} ms.",
                            session.CurrentMapId, session.Username, full, session.Tiles.Levels.Count - full, tiles.Tiles.Count(),
                            session.Tiles.Radii.FullMetres, session.Tiles.Radii.FarMetres,
                            staticEntities.Count, tiles.TiledCount + tiles.Global.Count, bytes / 1024.0, started.ElapsedMilliseconds);
        }
        else Log.Information("Join of {Map} for {User}: {Count} entities, {KB:F0} KB, {Ms} ms.",
                             session.CurrentMapId, session.Username, staticEntities.Count, bytes / 1024.0, started.ElapsedMilliseconds);
        bytes += SendCounted(session, RoadsFor(session.CurrentMapId));
        SendToSession(session, new MapLoadComplete());
    }

    /// <summary>The map's roads, junctions, level crossings and drivable tracks, for a driver's cues.</summary>
    internal MapRoads RoadsFor(string mapId)
    {
        _maps.TryGetMapData(mapId, out var map);
        // A server built without its systems (the tests' rig) has no crossings: the roads go without them.
        var data = MapRoadsBuilder.Build(map, _crossings?.Rails(mapId) ?? Array.Empty<CrossingRails>());
        return new MapRoads { MapName = mapId, Json = data.IsEmpty ? "" : data.ToJson() };
    }

    /// <summary>
    /// Where a player arriving on a map will stand: the spot a teleport named, else where they left it
    /// if that is still somewhere to stand, else its spawn. The same choice <see cref="HandlePlayerReady"/>
    /// makes, asked earlier so a streamed map can send the tiles round it first.
    /// </summary>
    internal Vector3 ArrivalPoint(UserSession session, string mapId)
    {
        if (session.ArriveAt is { } arrive && arrive.MapId.Equals(mapId, StringComparison.OrdinalIgnoreCase)) return arrive.At;
        if (_store != null) return _store.Arrival(mapId, session.Saved, out _).At.Position;
        return _maps.GetSpawnPoint(mapId).Position;
    }

    /// <summary>A message to a session as <see cref="SendToSession"/> sends it, and its size on the wire
    /// (0 with no socket).</summary>
    private int SendCounted(UserSession session, IMessage message)
    {
        Sent?.Invoke(session, message);
        var peer = _network.GetPeer(session.ConnectionId);
        if (peer != null) return _network.SendMessage(peer, message, DeliveryMethod.ReliableOrdered);
        _mudGateway?.TrySend(session.ConnectionId, message);
        return 0;
    }

    /// <summary>
    /// /detail: how far round this player a streamed map is sent in full and in its coarse layer. Takes
    /// effect at the next tick (TileStreamer). Returns what is now in force, to be said.
    /// </summary>
    public string SetStreamRadii(UserSession session, StreamRadii? asked)
    {
        if (asked is { } r)
        {
            session.Tiles.Radii = StreamRadii.Clamp(r.FullMetres, r.FarMetres);
            session.Tiles.Stale = true;
        }
        var now = session.Tiles.Radii;
        string what = $"World detail: everything to {now.FullMetres:F0} metres, buildings and roads to {now.FarMetres:F0} metres.";
        return _maps.TryGetTiles(session.CurrentMapId, out _) ? what : what + " This map is sent whole, so it applies on maps that stream.";
    }

    internal void HandlePlayerReady(int connectionId)
    {
        if (!_sessions.TryGetSession(connectionId, out var session)) return;
        if (session.Entity != Entity.Null) return; // already in the world
        string mapId = session.CurrentMapId;
        if (!_maps.TryGetMap(mapId, out var world, out var _, out var _, out var _))
        {
            Log.Error("Player {User} sent ready for map '{Map}', which is not loaded.", session.Username, mapId);
            return;
        }

        _commandBuffer.Enqueue(() => {
            // Checked again here, where it counts: two 'ready's in one tick, or a 'ready' that raced a
            // change of map, would otherwise put a second body in the world.
            if (session.Entity != Entity.Null || session.CurrentMapId != mapId) return;
            // Where they were on this map when they last left it, if that is still somewhere to stand;
            // the map's spawn if not, or if they have never been here.
            bool remembered = false;
            var (spawnPoint, spawnYaw) = _store != null
                ? _store.Arrival(mapId, session.Saved, out remembered)
                : (_maps.GetSpawnPoint(mapId), 0f);
            // Sent here by a teleporter (or anything else that names the spot): there, once.
            bool teleported = false;
            if (session.ArriveAt is { } arrive && arrive.MapId.Equals(mapId, StringComparison.OrdinalIgnoreCase))
            {
                spawnPoint.Position = arrive.At;
                teleported = arrive.Teleported;
                remembered = false;
            }
            session.ArriveAt = null;
            while (session.InputQueue.TryDequeue(out _)) { }
            session.GroundProbe.Invalidate();
            session.Entity = world.Create();
            world.Add(session.Entity, new PlayerComponent { 
                    ConnectionId = connectionId, 
                    Username = session.Username, 
                    Role = session.Role,
                    Yaw = spawnYaw,
                    Pitch = 0,
                    // Born wearing its team, so the first definition anybody is sent already says it.
                    Team = Teams?.NameOf(session.Username) ?? "",
                });
            world.Add(session.Entity, EntityType.Player);
            world.Add(session.Entity, spawnPoint);
            world.Add(session.Entity, new Velocity { Linear = Vector3.Zero });
            world.Add(session.Entity, new NameComponent { Name = session.Username });
            world.Add(session.Entity, new InventoryComponent { ItemEntityIds = new List<int>() });
            world.Add(session.Entity, new HealthComponent { Current = 100, Max = 100 });
            // What they are made of, for whatever meets them: a body, not the floor they stand on.
            world.Add(session.Entity, new MaterialComponent { Material = PhysicsConstants.PersonMaterial });
            world.Add(session.Entity, new ColliderComponent { Shape = ColliderShape.Cylinder, Size = new Vector3(PlayerRadius * 2, PlayerHeight, PlayerRadius * 2), IsSolid = true });

            // The one registration path: lookup, spatial index, dirty flag.
            _maps.IndexEntity(mapId, session.Entity);
            // Health as it was, and the things they were carrying, back in their hands and on their back.
            _store?.Arrive(session, mapId, world, session.Entity);
            if (remembered) Log.Information("{User} is back where they left {Map}.", session.Username, mapId);
            if (world.Has<DeadComponent>(session.Entity))
            {
                double left = world.Get<DeadComponent>(session.Entity).DiedAt + CombatService.PlayerRespawnSeconds - AudioClock.Now;
                SendToSession(session, new TextEvent { Text = $"You are dead. You come back in {Math.Max(1, Math.Ceiling(left)):0} seconds." });
            }

            var t = world.Get<Transform>(session.Entity);
            SendToSession(session, new PlayerSpawned { EntityId = session.Entity.Id, SpawnTransform = t });
            if (teleported) EmitWorldAudio(mapId, session.Entity.Id, Teleporter.Arrive, Teleporter.Sound(Teleporter.Arrive, t.Position));
            // Once per session: a change of map spawns you again, and the MOTD is a greeting.
            if (!session.Welcomed && ReadMotd() is { Length: > 0 } motd)
                SendToSession(session, new ChatMessage { Sender = "Server", Text = motd, Channel = ChatChannel.Server });
            session.Welcomed = true;
            Log.Information("Spawned player {User} as Entity {Id}", session.Username, session.Entity.Id);
        });
    }

    /// <summary>
    /// Moves a player to another loaded map, at its spawn or where they last left it. Queued for the tick
    /// thread. The old body leaves its seat, is stored with what it carried (which comes along) and is
    /// destroyed; what the client was sent is forgotten, and the new map goes out as at login. Access is
    /// the caller's to check (<see cref="DiscoveryService.CanEnter"/>).
    /// </summary>
    public void MoveToMap(UserSession session, string mapId, Action<IMessage>? reply = null)
    {
        EnqueueCommand(() => MoveToMapNow(session, mapId, reply));
    }

    private void MoveToMapNow(UserSession session, string mapId, Action<IMessage>? reply)
    {
        void Say(string text)
        {
            var message = new TextEvent { Text = text };
            if (reply != null) reply(message); else SendToSession(session, message);
        }

        if (!_maps.TryGetMap(mapId, out _, out _, out _, out _))
        {
            Say($"There is no map called {mapId} on this server.");
            return;
        }
        string from = session.CurrentMapId;
        if (from.Equals(mapId, StringComparison.OrdinalIgnoreCase) && session.Entity != Entity.Null)
        {
            Say($"You are already on {_maps.DisplayName(mapId)}.");
            return;
        }

        LeaveWorld(session);

        session.CurrentMapId = mapId;
        session.Tiles.Reset();
        session.KnownEntities.Clear();
        session.VisibleDynamicEntities.Clear();
        session.SentStates.Clear();
        session.LastStats = null;
        while (session.InputQueue.TryDequeue(out _)) { }
        session.InputBudget = 0;
        session.GroundProbe.Invalidate();
        session.Build.Reset();

        Log.Information("{User} moved from map '{From}' to '{To}'.", session.Username, from, mapId);

        foreach (var other in _sessions.GetSessionsInMap(from))
            if (other.ConnectionId != session.ConnectionId)
                SendToSession(other, new ChatMessage { Sender = "Server", Text = $"{session.Username} left for {_maps.DisplayName(mapId)}.", Channel = ChatChannel.Server });
        foreach (var other in _sessions.GetSessionsInMap(mapId))
            if (other.ConnectionId != session.ConnectionId)
                SendToSession(other, new ChatMessage { Sender = "Server", Text = $"{session.Username} arrived from {_maps.DisplayName(from)}.", Channel = ChatChannel.Server });

        Say($"Travelling to {_maps.DisplayName(mapId)}.");

        var peer = _network.GetPeer(session.ConnectionId);
        if (peer != null) SendManifest(session);
        // A text session has nothing to load, but on the world it waits for the ground round it all the same.
        else if (World == null || !World.Hold(session, ArrivalPoint(session, mapId), () => HandlePlayerReady(session.ConnectionId),
                                              (done, total, speak) => SayWorldLoading(session, done, total, speak)))
            HandlePlayerReady(session.ConnectionId);
    }

    // One implementation for the broadcast, the streamer and the tests: a divergence would only show as a
    // wrong-sounding room.
    private EntityDefinition CreateDefinition(World world, Entity e) => EntityDefinitionFactory.From(world, e);

    private readonly HashSet<int> _visibleDynamicBuffer = new();
    /// <summary>What a tick's broadcast chooses from, gathered once for the map rather than once for
    /// every player: each collidable entity, whether it moves, and whether it moved this tick.</summary>
    private readonly List<(Entity Entity, bool Dynamic, bool Dirty)> _broadcastCandidates = new();
    /// <summary>The spatial grid's cell (MapManager makes it 10 m): things up to a cell beyond earshot are
    /// sent, as when the broadcast asked the grid.</summary>
    private const float BroadcastCellMetres = 10f;
    private readonly HashSet<int> _dirtyAudioBuffer = new();
    private readonly List<int> _removedBuffer = new();

    /// <summary>One tick of the tile streamer for one player (TileStreamer.Update), with what it sends
    /// counted and each finished set of tiles logged.</summary>
    private void StreamTiles(NetPeer? peer, UserSession session, MapTiles tiles, World world,
                             Dictionary<int, Entity> lookup, Vector3 at)
    {
        TileStreamer.Update(session, tiles, world, lookup, at, message =>
        {
            Broadcasted?.Invoke(session, message);
            int bytes = peer != null ? _network.SendMessage(peer, message, DeliveryMethod.ReliableOrdered) : 0;
            session.Tiles.BytesSent += bytes;
            session.Tiles.BytesSinceUpdate += bytes;
            if (message is not TileStreamUpdate update) return;
            int full = 0, coarse = 0, dropped = 0;
            foreach (var tile in update.Tiles)
                if (tile.Detail == TileDetail.Full) full++; else if (tile.Detail == TileDetail.Coarse) coarse++; else dropped++;
            Log.Information("Tiles for {User} at ({X:F0}, {Z:F0}): {Full} now full, {Coarse} now coarse, {Dropped} dropped; "
                          + "{Defs} definitions, {Removed} removed, {KB:F0} KB. Holding {Held} tile(s).",
                            session.Username, at.X, at.Z, full, coarse, dropped, update.Definitions, update.Removed,
                            session.Tiles.BytesSinceUpdate / 1024.0, session.Tiles.Levels.Count);
            session.Tiles.BytesSinceUpdate = 0;
        });
    }

    /// <summary>
    /// What one tick's broadcast chooses from on a map: every entity with a collider, and every item,
    /// which has none and must still reach the client or its beacon has nothing to sound from. See
    /// docs/WORLD_STREAMING.md, "What the broadcast chooses from".
    /// </summary>
    internal static void GatherBroadcastCandidates(World world, List<(Entity Entity, bool Dynamic, bool Dirty)> into)
    {
        into.Clear();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t) =>
            into.Add((e, Moves(world, e), t.IsDirty)));
        world.Query(new QueryDescription().WithAll<Transform, ItemComponent>().WithNone<ColliderComponent>(), (Entity e, ref Transform t) =>
            into.Add((e, Moves(world, e), t.IsDirty)));
    }

    /// <summary>Sent every tick, unreliably: anything with a velocity, a player, and a carried thing (as a
    /// moved static it went reliably every tick and a lost packet held up everything behind it).</summary>
    private static bool Moves(World world, Entity e)
        => world.Has<Velocity>(e) || world.Has<PlayerComponent>(e) || world.Has<HeldComponent>(e);

    /// <summary>One message of the broadcast, to a session's socket, and to <see cref="Broadcasted"/> when a test watches.</summary>
    private void Deliver(NetPeer? peer, UserSession session, IMessage message, DeliveryMethod delivery)
    {
        Broadcasted?.Invoke(session, message);
        if (peer != null) _network.SendMessage(peer, message, delivery);
    }

    /// <summary>The per-tick states, which go out unreliably and split to fit a packet.</summary>
    private void DeliverStates(NetPeer? peer, UserSession session, ServerStateUpdate update)
    {
        Broadcasted?.Invoke(session, update);
        if (peer != null) _network.SendStateUpdate(peer, update, DeliveryMethod.Unreliable);
    }

    private void BroadcastWorldState(long tick)
    {
        _dirtyAudioBuffer.Clear();
        while (_dirtyAudioEntities.TryDequeue(out int id)) _dirtyAudioBuffer.Add(id);

        foreach (var mapEntry in _maps.GetAllMaps())
        {
            var world = mapEntry.Value.world;
            var grid = mapEntry.Value.grid;
            var sessionsInMap = _sessions.GetSessionsInMap(mapEntry.Key).ToList();
            if (sessionsInMap.Count == 0) goto ClearDirty;

            // One pass over the world for the map, then each player filters it. A grid query per player
            // overran the VPS tick with two players on the city: docs/WORLD_STREAMING.md, "What the
            // broadcast chooses from".
            using (PerfProbe.Measure("server.broadcast.gather"))
                GatherBroadcastCandidates(world, _broadcastCandidates);
            // A map streamed in tiles: each client is sent the tiles near it, and only what stands in them.
            _maps.TryGetTiles(mapEntry.Key, out var tiles);

            foreach (var session in sessionsInMap)
            {
                try
                {
                    if (session.Entity == Entity.Null) continue;
                    if (!world.IsAlive(session.Entity)) continue;
                    using var _sessionPerf = PerfProbe.Measure("server.broadcast.session");
                    var pPos = world.Get<Transform>(session.Entity).Position;
                    float earshot = _maps.GetEarshotRange(mapEntry.Key);
                    var peer = _network.GetPeer(session.ConnectionId);
                    if (peer == null && Broadcasted == null) continue;

                    // Whether the client's position is its own to predict, a seat's, or held still.
                    int riding = world.Has<OccupantComponent>(session.Entity)
                        ? world.Get<OccupantComponent>(session.Entity).RootEntityId : -1;
                    bool driving = riding >= 0 && world.Get<OccupantComponent>(session.Entity).Controls;
                    bool heldStill = world.Has<FrozenComponent>(session.Entity) || world.Has<DeadComponent>(session.Entity);

                    _reusableBroadcast.Tick = tick;
                    _reusableBroadcast.LastProcessedSequenceId = session.LastProcessedSequenceId;
                    _reusableBroadcast.RidingEntityId = riding;
                    _reusableBroadcast.RidingControls = driving;
                    _reusableBroadcast.Held = heldStill;
                    _reusableBroadcast.States.Clear();
                    _reliableBroadcast.Tick = tick;
                    _reliableBroadcast.LastProcessedSequenceId = session.LastProcessedSequenceId;
                    _reliableBroadcast.RidingEntityId = riding;
                    _reliableBroadcast.RidingControls = driving;
                    _reliableBroadcast.Held = heldStill;
                    _reliableBroadcast.States.Clear();
                    _visibleDynamicBuffer.Clear();
                    float reach = earshot + BroadcastCellMetres;

                    if (tiles != null)
                        using (PerfProbe.Measure("server.broadcast.tiles"))
                            StreamTiles(peer, session, tiles, world, mapEntry.Value.lookup, pPos);

                    var scan = PerfProbe.Measure("server.broadcast.scan");
                    foreach (var (e, isDynamic, isDirty) in _broadcastCandidates)
                    {
                        // Unmoved geometry this client already has: most of the city, settled by one lookup.
                        bool known = session.KnownEntities.Contains(e.Id);
                        if (!isDynamic && !isDirty && known && !_dirtyAudioBuffer.Contains(e.Id)) continue;
                        // On a streamed map the map's own geometry is the tile streamer's to send.
                        if (!isDynamic && !known && tiles != null && tiles.IsTiled(e.Id)) continue;
                        // Removed earlier this tick: asking a dead entity anything throws and would skip
                        // this player's whole update.
                        if (!world.IsAlive(e)) continue;
                        ref var t = ref world.Get<Transform>(e);
                        if (MathF.Abs(t.Position.X - pPos.X) > reach || MathF.Abs(t.Position.Z - pPos.Z) > reach) continue;
                        // ...and everything else only while it stands in a tile this client has.
                        if (tiles != null && e != session.Entity && !tiles.IsTiled(e.Id) && !tiles.IsGlobal(e.Id)
                            && !tiles.Holds(t.Position, session.Tiles.Levels)) continue;

                        if (isDynamic) _visibleDynamicBuffer.Add(e.Id);

                        // The definition before the first state, once: KnownEntities keeps it to once.
                        bool isNew = session.KnownEntities.Add(e.Id);
                        bool defined = isNew || _dirtyAudioBuffer.Contains(e.Id);
                        // Into or out of a seat: the definition says which (EntityDefinition.RidingEntityId),
                        // or a body moving at a car's speed sounds as if it is running. Noticed here
                        // rather than at each of the many ways in and out of a seat.
                        int seatedIn = isDynamic && world.Has<OccupantComponent>(e) ? world.Get<OccupantComponent>(e).RootEntityId : -1;
                        if (isDynamic && !defined && session.SentStates.TryGetValue(e.Id, out var told) && told.Riding != seatedIn)
                            defined = true;
                        if (defined)
                            Deliver(peer, session, CreateDefinition(world, e), DeliveryMethod.ReliableOrdered);

                        if (!isDynamic && !t.IsDirty && !isNew) continue;

                        var state = new NetworkEntityState {
                            EntityId = e.Id,
                            Transform = QuantizedTransform.FromTransform(t)
                        };
                        if (world.Has<Velocity>(e)) state.LinearVelocity = world.Get<Velocity>(e).Linear;
                        // How hard it works its tyres, which only the server knows: from a velocity alone
                        // every car on a banked oval reads as sliding. See EntityState.TyreDemand.
                        if (_vehicles.TryGetTyreDemand(e.Id, out float tyreDemand))
                            state.TyreDemand = NetworkEntityState.EncodeTyreDemand(tyreDemand);
                        // ...and a car somebody is driving, which is not traffic.
                        else if (world.Has<DriveComponent>(e))
                            state.TyreDemand = NetworkEntityState.EncodeTyreDemand(world.Get<DriveComponent>(e).TyreDemand);
                        // Each wheel: its load, slip, speed and the surface under it (WheelDynamics).
                        if (_vehicles.TryGetWheels(e.Id, out var wheels) || DrivingSystem.TryGetWheels(e.Id, out wheels))
                            state.Wheels = wheels;
                        // A driven vehicle's horn and siren switches: nothing a listener can observe
                        // says a hand is on the horn.
                        if (isDynamic) state.Signals = VehicleSignals.WireByte(world, e);

                        // A dynamic entity is corrected by the next tick, so unreliable; a moved static is
                        // never resent, so reliable, or a dropped packet leaves a wall where none is. A
                        // dynamic entity at rest goes only as a few repeats and a keep-alive a second
                        // (RestingStates): two thirds of the city.
                        if (isDynamic)
                        {
                            if (RestingStates.ShouldSend(session.SentStates, ref state, tick, force: defined || e == session.Entity))
                                _reusableBroadcast.States.Add(state);
                            else PerfProbe.Count("server.broadcast.resting");
                            session.SentStates[e.Id].Riding = seatedIn;
                        }
                        else _reliableBroadcast.States.Add(state);
                    }

                    scan.Dispose();
                    PerfProbe.Count("server.broadcast.states", _reusableBroadcast.States.Count);
                    PerfProbe.Count("server.broadcast.reliable-states", _reliableBroadcast.States.Count);
                    var send = PerfProbe.Measure("server.broadcast.send");
                    // Static geometry is never evicted: the client's acoustic map is built from all of it.
                    CollectDeparted(session.VisibleDynamicEntities, _visibleDynamicBuffer, _removedBuffer);
                    // A thing just put down has stopped moving, not gone: removing it would blink its beacon
                    // out as it landed.
                    var lookup = mapEntry.Value.lookup;
                    for (int i = _removedBuffer.Count - 1; i >= 0; i--)
                        if (lookup.TryGetValue(_removedBuffer[i], out var put) && world.IsAlive(put)
                            && world.Has<ItemComponent>(put) && !world.Has<HeldComponent>(put))
                        {
                            session.VisibleDynamicEntities.Remove(_removedBuffer[i]);
                            _removedBuffer.RemoveAt(i);
                        }

                    if (_removedBuffer.Count > 0)
                    {
                        foreach (int goneId in _removedBuffer)
                        {
                            session.VisibleDynamicEntities.Remove(goneId);
                            session.KnownEntities.Remove(goneId);
                            session.SentStates.Remove(goneId);
                        }
                        Deliver(peer, session, new EntityRemoved { EntityIds = new List<int>(_removedBuffer) },
                            DeliveryMethod.ReliableOrdered);
                    }

                    foreach (int id in _visibleDynamicBuffer) session.VisibleDynamicEntities.Add(id);

                    if (_reusableBroadcast.States.Count > 0)
                        DeliverStates(peer, session, _reusableBroadcast);
                    if (_reliableBroadcast.States.Count > 0)
                        Deliver(peer, session, _reliableBroadcast, DeliveryMethod.ReliableOrdered);

                    int health = 0, maxHealth = 0;
                    if (world.Has<HealthComponent>(session.Entity))
                    {
                        var h = world.Get<HealthComponent>(session.Entity);
                        health = h.Current; maxHealth = h.Max;
                    }
                    var stats = GetMaterialUnderPlayer(world, grid, pPos);
                    // The gun in your hands, which the client's keys need: Enter fires only a gun,
                    // and R reloads one.
                    var held = CombatService.Held(world, session.Entity, mapEntry.Value.lookup);
                    // Sent only when something in it changes; reliable, so the change cannot be lost.
                    var statsNow = new StatsUpdate {
                        Health = health, MaxHealth = maxHealth,
                        CurrentMaterial = stats.matType, CurrentVariant = stats.variant,
                        HeldWeaponId = held.WeaponId, HeldRounds = held.Rounds, HeldScopeId = held.ScopeId,
                        // A body over the shoulder slows you, and the client predicts the same pace.
                        SpeedLimit = HandsService.SpeedLimit(world, session.Entity, mapEntry.Value.lookup),
                    };
                    if (!SameStats(session.LastStats, statsNow))
                    {
                        Deliver(peer, session, statsNow, DeliveryMethod.ReliableOrdered);
                        session.LastStats = statsNow;
                    }
                    send.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "BroadcastWorldState: Error broadcasting to session {User}. Skipping.", session.Username);
                }
            }

            ClearDirty:
            world.Query(new QueryDescription().WithAll<Transform>(), (ref Transform t) => { t.IsDirty = false; });
        }
    }

    /// <summary>Whether two stats updates say the same thing, field for field.</summary>
    internal static bool SameStats(StatsUpdate? a, StatsUpdate b)
        => a != null && a.Health == b.Health && a.MaxHealth == b.MaxHealth
           && a.CurrentMaterial == b.CurrentMaterial && a.CurrentVariant == b.CurrentVariant
           && a.HeldWeaponId == b.HeldWeaponId && a.HeldRounds == b.HeldRounds && a.HeldScopeId == b.HeldScopeId
           && a.SpeedLimit == b.SpeedLimit;

    private (string matType, string variant) GetMaterialUnderPlayer(World world, SpatialGrid<Entity> grid, Vector3 pos)
    {
        float groundY = PhysicsUtils.GetGroundHeight(world, grid, pos, out string floorMat);
        return (floorMat ?? "Generic", "0");
    }

    /// <summary>A telnet connection closed: how a text player leaves, so it is announced as logging out.</summary>
    internal void HandleMudDisconnected(int connectionId)
    {
        if (!_sessions.TryRemoveSession(connectionId, out var s)) return;
        Log.Information("MUD session {Id} ({User}) ended.", connectionId, s.Username);
        AnnouncePresence(s, PresenceKind.LoggedOut);
        DespawnSession(s);
    }

    private void HandlePeerDisconnected(NetPeer peer, DisconnectInfo info)
        => PeerDisconnected(peer.Id, info.Reason);

    /// <summary>A UDP connection ended without a logout first. Split out so a test can drive it.</summary>
    internal void PeerDisconnected(int connectionId, DisconnectReason reason)
    {
        if (!_sessions.TryRemoveSession(connectionId, out var s)) return;
        Log.Information("Peer {Id} ({User}) disconnected: {Reason}", connectionId, s.Username, reason);
        AnnouncePresence(s, PresenceFor(reason));
        DespawnSession(s);
    }

    /// <summary>What a dropped connection tells everyone: a client that closed its own connection left on
    /// purpose; anything else is a lost connection, and that player is probably on their way back.</summary>
    internal static PresenceKind PresenceFor(DisconnectReason reason)
        => reason is DisconnectReason.RemoteConnectionClose or DisconnectReason.DisconnectPeerCalled
            ? PresenceKind.LoggedOut
            : PresenceKind.WentOffline;

    /// <summary>Takes a disconnected session's body out of the world, on the tick thread. Queued even with no
    /// body yet: a 'ready' queued just before the disconnect spawns one first.</summary>
    internal void DespawnSession(UserSession session)
    {
        _commandBuffer.Enqueue(() => LeaveWorld(session));
    }

    /// <summary>
    /// Takes a session's body off its map: out of any seat, stored with its place, health and things
    /// (PlayerStore), destroyed and announced as gone. On the tick thread. Every way of leaving comes
    /// through here, so a player comes back the same way whichever it was.
    ///
    /// The seat and the things go first, while there is a body: destroyed while carrying, the things kept
    /// a holder that no longer exists and a parent id Arch would reuse. What the store will not keep is
    /// put down where the body stood. Unannounced, every other client keeps the corpse forever.
    /// </summary>
    private void LeaveWorld(UserSession session)
    {
        var body = session.Entity;
        string mapId = session.CurrentMapId;
        // A reload in progress does not follow anybody to another map.
        _commands?.Combat.Forget(session);
        if (body != Entity.Null && _maps.TryGetMap(mapId, out var world, out _, out _, out _))
        {
            if (world.IsAlive(body))
            {
                // Leaving the map is not a request: a moving vehicle or shut doors do not refuse it.
                // Without the seat service (a test rig), unseated where they sit.
                if (world.Has<OccupantComponent>(body) && (_seats == null || !_seats.Exit(session, out _, leavingWorld: true)))
                    CompositeService.Disembark(world, body);
                // Whatever the store will not keep is put down.
                if (_store != null && _maps.TryGetMap(mapId, out _, out _, out _, out var lookup))
                    foreach (int gone in _store.Leave(session, mapId, world, body, lookup))
                        BroadcastEntityRemoved(mapId, gone);
                _hands?.Drop(session, "all", out _);
            }
            session.Entity = Entity.Null;
            _maps.DestroyEntity(mapId, body);
            BroadcastEntityRemoved(mapId, body.Id);
        }
        session.Entity = Entity.Null;
    }

    /// <summary>Tells every session on a map that an entity is gone, and forgets it for them. Anything that
    /// destroys an entity must call it: every other message is additive, and without this the entity
    /// stays on every client, occluding and answering scans.</summary>
    public void BroadcastRemoval(string mapId, int entityId) => BroadcastEntityRemoved(mapId, entityId);

    private void BroadcastEntityRemoved(string mapId, int entityId)
    {
        foreach (var other in _sessions.GetSessionsInMap(mapId))
        {
            other.VisibleDynamicEntities.Remove(entityId);
            other.SentStates.Remove(entityId);
            if (!other.KnownEntities.Remove(entityId)) continue;
            SendToSession(other, new EntityRemoved { EntityIds = new List<int> { entityId } });
        }
    }

    /// <summary>
    /// The interact key: a tap, a door, getting in or out, picking up, shutting a door, in that order.
    /// Queued for the tick thread: reading the Arch world from the network thread raced the simulation.
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

            // A tap you are standing at first: it needs you right beside it (TapReach), so it never takes
            // the key from a door across the room, and a door five metres off would otherwise always win.
            if (ToggleTapInReach(world, position, Say)) return;

            // Then a shut door in reach, from a seat the one beside you: once to open it, again to get
            // in or out.
            var door = ShutDoorInReach(world, position, out Vector3 doorAt, out string doorName);
            if (world.Has<OccupantComponent>(session.Entity))
            {
                if (door != null && OpenDoor(world, door.Value, doorName, position, session.Entity, Say)) return;
                _seats.Exit(session, out string leaving);
                Say(leaving);
                return;
            }
            // Beside a car the client usually points at one of its doors; either names the car.
            int root = -1;
            Vector3 carAt = default;
            if (interact.TargetEntityId.HasValue) root = RootOf(session.CurrentMapId, interact.TargetEntityId.Value, out carAt);
            if (root < 0) root = _seats.NearestEnterable(session.CurrentMapId, position, OccupancyService.BoardingRange, out carAt);

            // A shut door and a car both in reach: the one you face wins, else the nearer (Cody,
            // 2026-10-08: E beside a car opened the apartment door behind him).
            float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;
            if (door != null && (root < 0 || Prefer(position, yaw, doorAt, carAt))
                && OpenDoor(world, door.Value, doorName, position, session.Entity, Say)) return;

            if (root >= 0)
            {
                _seats.Enter(session, root, null, out string entering);
                Say(entering);
                return;
            }

            // Something at your feet, before shutting a door: it is far likelier what you meant (Cody,
            // 2026-10-04: a dropped gun could not be picked up with E).
            if (_hands.TakeWithin(session, PhysicsConstants.PickUpReach, out string took)) { Say(took); return; }

            // Last, an open door in reach is shut: beside a house E opens it and E again shuts it.
            if (CloseDoorInReach(world, position, session.Entity, Say)) return;

            if (interact.TargetEntityId.HasValue
                && _maps.TryGetMap(session.CurrentMapId, out var w, out _, out _, out var lookup)
                && lookup.TryGetValue(interact.TargetEntityId.Value, out var target)
                && w.Has<Transform>(target)
                && Vector3.Distance(position, w.Get<Transform>(target).Position) > PhysicsConstants.InteractionRange)
            {
                Say("Too far away.");
                return;
            }

        });
    }

    /// <summary>The nearest shut door within arm's length that opens by hand. Only shut doors, so once one
    /// is open the key moves on to getting in or out.</summary>
    private static Entity? ShutDoorInReach(World world, Vector3 position, out Vector3 at, out string name)
    {
        Entity? nearest = null;
        float best = PhysicsConstants.InteractionRange;
        Vector3 found = default;
        string named = "door";
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent d) =>
        {
            if (d.Target > 0f) return;                    // already open, or on its way
            if (!DoorSystem.OpensByHand(d)) return;       // it opens for you, or for the lift
            float distance = Vector3.Distance(position, t.Position);
            if (distance > best) return;
            best = distance; nearest = e; found = t.Position;
            named = world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
                  ? world.Get<IdentityComponent>(e).Name : "door";
        });
        at = found; name = named;
        return nearest;
    }

    /// <summary>Opens a door and says so.</summary>
    private static bool OpenDoor(World world, Entity door, string name, Vector3 position, Entity who, Action<string> say)
    {
        if (!DoorSystem.Set(world, door, open: true, by: position, who: who)) return false;
        var opened = world.Get<DoorComponent>(door);
        say(DoorSystem.OpenedPhrase(opened, name) + "."
            + (DoorSystem.InTheWay(world, door, 1f, who) != null ? " Someone is in the way of it." : ""));
        return true;
    }

    /// <summary>How far either side of where you face something counts as in front of you, radians.</summary>
    internal const float FacingHalfAngle = MathF.PI / 3f;

    /// <summary>
    /// Whether <paramref name="a"/> is meant over <paramref name="b"/> by someone at <paramref name="from"/>
    /// facing <paramref name="yaw"/>: the one in front of you, and when both or neither are, the nearer.
    /// Measured across the floor, so a door's height does not count against it.
    /// </summary>
    internal static bool Prefer(Vector3 from, float yaw, Vector3 a, Vector3 b)
    {
        bool aAhead = InFront(from, yaw, a), bAhead = InFront(from, yaw, b);
        if (aAhead != bAhead) return aAhead;
        return Flat(a - from).LengthSquared() <= Flat(b - from).LengthSquared();

        static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);
        static bool InFront(Vector3 from, float yaw, Vector3 to)
        {
            var toward = Flat(to - from);
            if (toward.LengthSquared() < 1e-4f) return true;   // standing on it
            return Vector3.Dot(Vector3.Normalize(toward), Flat(ScopeMath.Forward(yaw, 0f))) >= MathF.Cos(FacingHalfAngle);
        }
    }

    /// <summary>How close you must stand to a tap to turn it, m, measured along the floor: at the sink.</summary>
    internal const float TapReach = 1.3f;

    /// <summary>
    /// Turns the tap you are standing at on, or off (running water with a tap: RunningWaterSpec.Tap). The
    /// tap's flow is its emitter's SynthRunning, which every client reads from the definition, so the
    /// definition goes out again; the basin under it fills and drains on each client from that one flag.
    /// </summary>
    private bool ToggleTapInReach(World world, Vector3 position, Action<string> say)
    {
        Entity? nearest = null;
        float best = TapReach;
        world.Query(new QueryDescription().WithAll<Transform, SoundEmitterComponent>(), (Entity e, ref Transform t, ref SoundEmitterComponent em) =>
        {
            if (!IsTap(em.SoundId)) return;
            float dx = t.Position.X - position.X, dz = t.Position.Z - position.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance > best || MathF.Abs(t.Position.Y - position.Y) > 2.5f) return;
            best = distance; nearest = e;
        });
        if (nearest == null) return false;
        ref var tap = ref world.Get<SoundEmitterComponent>(nearest.Value);
        tap.SynthRunning = !tap.SynthRunning;
        SyncAudioComponent(nearest.Value.Id);
        string name = world.Has<IdentityComponent>(nearest.Value) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(nearest.Value).Name)
            ? world.Get<IdentityComponent>(nearest.Value).Name : "tap";
        say($"You turn the {name.ToLowerInvariant()} {(tap.SynthRunning ? "on" : "off")}.");
        return true;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> TapIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a sound id is running water with a tap a person turns.</summary>
    internal static bool IsTap(string? soundId)
    {
        if (soundId == null || !soundId.StartsWith("flow:", StringComparison.OrdinalIgnoreCase)) return false;
        return TapIds.GetOrAdd(soundId, static id =>
        {
            try { return OpenFPS.Common.RunningWaterSpec.ByName(id[5..]).Tap != null; }
            catch (Exception) { return false; }
        });
    }

    /// <summary>Shuts the nearest open door within arm's length, if there is one, unless somebody is in
    /// the way of the leaf: then says so, and leaves it open.</summary>
    private static bool CloseDoorInReach(World world, Vector3 position, Entity who, Action<string> say)
    {
        Entity? nearest = null;
        float best = PhysicsConstants.InteractionRange;
        string name = "door";
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent d) =>
        {
            if (d.Target <= 0f) return;                   // already shut, or on its way
            if (!DoorSystem.OpensByHand(d)) return;
            // Measured to where the door shuts (the doorway), not to the swung leaf; in the world's frame,
            // since a door in a building keeps its shut pose in the building's.
            DoorSystem.Doorway(world, e, out var doorway, out _);
            float distance = MathF.Min(Vector3.Distance(position, t.Position), Vector3.Distance(position, doorway));
            if (distance > best) return;
            best = distance; nearest = e;
            name = world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
                 ? world.Get<IdentityComponent>(e).Name : "door";
        });
        if (nearest == null) return false;
        if (DoorSystem.InTheWay(world, nearest.Value, 0f, who) is { } blocker)
        {
            say(DoorSystem.InTheWayLine(blocker, who, name));
            return true;
        }
        if (!DoorSystem.Set(world, nearest.Value, open: false, who: who)) return false;
        say($"You {(world.Get<DoorComponent>(nearest.Value).Slides ? "slide" : "shut")} the {name}{(world.Get<DoorComponent>(nearest.Value).Slides ? " shut" : "")}.");
        return true;
    }

    /// <summary>The composite an entity is or is part of, else -1: pointing at a door is pointing at the car,
    /// whose root is in the middle where nobody stands. <paramref name="at"/> is where the part pointed at is.</summary>
    private int RootOf(string mapId, int entityId, out Vector3 at)
    {
        at = default;
        if (!_maps.TryGetMap(mapId, out var world, out _, out _, out var lookup)) return -1;
        if (!lookup.TryGetValue(entityId, out var e) || !world.IsAlive(e)) return -1;
        if (world.Has<Transform>(e)) at = world.Get<Transform>(e).Position;
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
        => _ = Register(connectionId, request, reply);

    /// <summary>
    /// Creating an account: the same limits as a login, the account rules, and a limit of its own on
    /// how many accounts one address may make. Hashed off the tick thread like a login.
    /// </summary>
    internal Task Register(int connectionId, RegisterRequest request, Action<IMessage> reply)
    {
        if (_sessions.TryGetSession(connectionId, out _))
        {
            reply(new RegisterResponse { Success = false, Message = "Log out before creating another account." });
            return Task.CompletedTask;
        }
        string address = RemoteAddressOf(connectionId);
        if (!_auth.Admit(address))
        {
            reply(new RegisterResponse { Success = false, Message = AuthService.TooManyAttempts });
            return Task.CompletedTask;
        }
        if (Interlocked.Increment(ref _pendingLogins) > MaxPendingLogins)
        {
            Interlocked.Decrement(ref _pendingLogins);
            reply(new RegisterResponse { Success = false, Message = "The server is busy. Try again in a few seconds." });
            return Task.CompletedTask;
        }

        string username = request.Username ?? "", password = request.Password ?? "";
        return Task.Run(() => _auth.CheckRegistration(address, username, password)).ContinueWith(checking =>
        {
            Interlocked.Decrement(ref _pendingLogins);
            var outcome = checking.IsCompletedSuccessfully
                ? checking.Result
                : new AuthOutcome(false, "The server could not create that account. Try again.");
            if (checking.IsFaulted) Log.Error(checking.Exception, "Registration check for {Address} failed.", address);
            // A duplicate username must not be told its account was created.
            EnqueueCommand(() => reply(new RegisterResponse { Success = outcome.Success, Message = outcome.Message }));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Sends the world state to every graphical session, built per map: the weather is global, but air
    /// pressure and the air-absorption multiplier are the map's. Leave <c>AirAbsorptionMultiplier</c>
    /// unset and it arrives as 0, and the client's guard turns air absorption off.
    /// </summary>
    public void BroadcastEnvironment()
    {
        var perMap = new Dictionary<string, WorldStateUpdate>();
        // One moment for every map: the wind's eddies are the server's, not a map's.
        double clock = WindField.Now();
        var travel = _environment.WindTravel;

        foreach (var session in _sessions.GetAllSessions())
        {
            if (session.IsTextClient) continue; // nothing to render it with

            if (!perMap.TryGetValue(session.CurrentMapId, out var update))
            {
                var atmosphere = _maps.TryGetMapData(session.CurrentMapId, out var mapData)
                    ? MapAtmosphere.Of(mapData)
                    : MapAtmosphere.Default;

                var state = _environment.GetStateForMap(atmosphere);
                var falling = _environment.PrecipitationFor(state);
                update = new WorldStateUpdate {
                    GameTime = state.GameTime,
                    Temperature = state.Temperature, Humidity = state.Humidity,
                    AirPressure = state.AirPressure, AirAbsorptionMultiplier = state.AirAbsorptionMultiplier,
                    WindVelocity = state.WindVelocity,
                    WindGustiness = state.WindGustiness, PrecipitationIntensity = state.PrecipitationIntensity,
                    WindClock = clock, WindTravelEast = travel.East, WindTravelNorth = travel.North,
                    RainRateMmPerHour = falling.RateMmPerHour,
                    PrecipitationKind = (int)falling.Kind,
                    RainMedianDropMm = falling.MedianDropMm,
                    HailDiameterMm = falling.HailMm,
                    RoadWater = RoadWaterSystem.WaterOf(session.CurrentMapId)?.Save(),
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
        // Serilog first, so startup errors are captured.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/server.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            using var serviceProvider = ConfigureServices().BuildServiceProvider();
            var server = serviceProvider.GetRequiredService<GameServer>();

            // Ctrl-C and SIGTERM ask the loop to finish its tick and tear down in order; cancelled, because
            // the default kills the process with sockets open and SQLite mid-write.
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

            // --port brings a second server up beside a running one. --map picks the landing map; without
            // it the map claiming IsDefault is.
            int port = 33288;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--port" && int.TryParse(args[i + 1], out int p)) port = p;
                if (args[i] == "--map") server.RequestedMapId = args[i + 1];
            }
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
