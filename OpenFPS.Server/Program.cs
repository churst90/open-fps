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

    /// <summary>
    /// Logins being checked off the tick thread right now. Past <see cref="MaxPendingLogins"/> a new
    /// one is told the server is busy rather than queued: bcrypt is slow on purpose, and a queue
    /// that grows with the number of addresses asking is a queue an attacker can fill.
    /// </summary>
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

    /// <summary>
    /// Gives an UNSTARTED server the maps and sessions a test built, so the paths that need the
    /// world — <see cref="MoveToMap"/>, spawning — can run without a socket. Start() builds its own.
    /// </summary>
    public void Attach(MapManager maps, SessionManager sessions, OccupancyService? seats = null, HandsService? hands = null)
    {
        _maps = maps;
        _sessions = sessions;
        _seats = seats!;
        _hands = hands!;
        _store = new PlayerStore(_userRepo, maps, hands ?? new HandsService(maps));
    }

    /// <summary>
    /// Runs everything queued for the tick thread. This is the only place world mutations from other
    /// threads — the MUD gateway's TCP tasks, command handlers, despawns — are allowed to happen.
    /// Returns how many ran.
    /// </summary>
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

    /// <summary>The models the world editor has changed, every version kept (model_versions/). In memory
    /// only until <see cref="Start"/> gives it its folder.</summary>
    public OpenFPS.Server.Editor.ModelStore Models { get; set; } = new(null);

    /// <summary>The entities whose definitions go out again at the next broadcast. For tests.</summary>
    internal IReadOnlyCollection<int> PendingDefinitionResends => _dirtyAudioEntities.ToArray();

    /// <summary>
    /// Puts a player's team on their body, if they are in the world, and has everybody who can see them
    /// told again. A client hears a teammate's beacon in its own tone by comparing the team on that
    /// body with the team on its own, and both travel in the entity definition, which is sent once —
    /// so a change of team that did not re-send it would go unheard until the player left and came back.
    /// Runs on the tick thread.
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

    /// <summary>
    /// A lightning flash, told to everyone on every map, wherever they are on it.
    ///
    /// Not through <see cref="EmitWorldAudio"/>: its earshot is the map's broadcast radius, a few
    /// hundred metres to three kilometres, and thunder is heard from twenty. The storm is drawn round
    /// each map's centre, so every map has the same storm in its own frame. Each client works out
    /// the thunder for where its listener stands (Thunder.Render); a text player has nothing to render
    /// it with and is not sent it.
    /// </summary>
    private void EmitStrike(LightningStrike strike)
    {
        foreach (var entry in _maps.GetAllMaps())
        {
            var data = entry.Value.data;
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

        // These register their handlers with the dispatcher, which is what keeps them alive.
        _ = new DiscoveryService(_dispatcher, _sessions, _maps);
        _ = new SocialService(_dispatcher, _sessions, _friends);
        
        RegisterHandlers();

        // Start MUD Gateway on port + 1 (e.g. 33289)
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

    /// <summary>
    /// Ordered teardown, on the loop thread after the last tick: tell the players first, then close the
    /// sockets, then destroy the worlds. Previously Ctrl-C tore all three down at once, mid-tick.
    /// </summary>
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

        Log.Information("Server stopped cleanly.");
    }

    /// <summary>
    /// Sends to a session over whichever transport it actually has. A MUD session has no UDP peer, so a
    /// reply sent only by UDP would vanish for telnet players — including chat aimed at them.
    /// </summary>
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
        var line = new ChatMessage
        {
            Sender = from.Username, Text = text, Channel = channel,
            FromStaff = from.Role is UserRole.Admin or UserRole.Dev or UserRole.Moderator,
        };
        var to = channel switch
        {
            ChatChannel.All => _sessions.GetAllSessions(),
            ChatChannel.Team => _sessions.GetAllSessions().Where(s => team!.Has(s.Username)),
            _ => _sessions.GetSessionsInMap(from.CurrentMapId),
        };
        foreach (var s in to) SendToSession(s, line);
    }

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
            // Bound the backlog: a client that floods inputs cannot grow the queue without limit,
            // and gains nothing by trying — MovementSystem spends real time, not queue depth.
            if (s.InputQueue.Count >= MaxQueuedInputs)
            {
                s.DroppedInputs++;
                _dispatcher.Limits.Abuse.Note(s.Username, "input", $"input queue full; dropping inputs ({s.DroppedInputs} so far)");
                return;
            }
            s.InputQueue.Enqueue(req);
        });
        _dispatcher.RegisterHandler<TextCommand>((id, req, reply) => {
            // No UDP peer lookup here: a MUD session has none, so such a guard would drop every MUD
            // command silently. Commands run on the tick thread whatever the transport, so a telnet
            // client is just another session.
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
            // The sender is who sent it, not who the packet says sent it: the client fills SenderId
            // with its own entity id and the listeners place the voice at that entity, so a forged id
            // would put your words in somebody else's mouth.
            if (senderSession.Entity == Entity.Null || req.OpusData.Length > MaxVoiceBytes) return;
            req.SenderId = senderSession.Entity.Id;
            Touch(id);
            // Relayed to everyone else on the same map, at once; each listener places it at the sender.
            int relayed = 0;
            foreach (var s in _sessions.GetAllSessions())
            {
                if (s.ConnectionId == id) continue; // don't echo back to sender
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

    /// <summary>
    /// Voice traffic per sender, logged every ten seconds while they talk: "Voice: sean sent 500 frames
    /// (24 kbit/s) on city, relayed to 1 player". Without it, "he could not hear me" had nothing on the
    /// server to say whether the frames ever arrived (2026-10-04).
    /// </summary>
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
            _lightning.Update(dt, _environment.CurrentScenario, _environment.GetCurrentState(), EmitStrike);
            // Horns whose key has not been reported down for a few ticks are let go.
            VehicleSignals.Update(dt);

            foreach (var entry in _maps.GetAllMaps())
            {
                try
                {
                    var world = entry.Value.world;
                    var grid = entry.Value.grid;
                    var lookup = entry.Value.lookup;

                    // 3. Refresh Spatial Grid (Dynamic items)
                    var stage = PerfProbe.Measure("server.grid");
                    grid.Clear();
                    world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithAny<Velocity, PlayerComponent>(), (Entity e, ref Transform t, ref ColliderComponent c) => {
                        grid.AddOverlapping(t.Position, c.Size, t.Rotation, e, false);
                    });

                    stage.Dispose();
                    // 4. Update Simulation (Movement)
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

                    // ...and the people watching them. Only a source with a place and a size: no
                    // loop, no bed, and nothing in it that knows what a car is.
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
                    stage.Dispose();
                    stage = PerfProbe.Measure("server.driving+doors+parents");
                    DrivingSystem.Update(world, grid, entry.Value.data.WalkMin, entry.Value.data.WalkMax, dt,
                                         (id, label, sounds) => EmitWorldAudio(entry.Key, id, label, sounds), entry.Key);
                    // The glass in every car's windows, toward wherever it was last sent.
                    WindowSystem.Update(world, dt);
                    // Doors swing BEFORE the parts are placed: a door in a building is one of its
                    // parts, and ParentSystem writes every part's world transform from its local one
                    // each tick, so a swing applied after it would be overwritten before anyone saw
                    // it. The announcement re-sends the door's definition, which is how the aperture
                    // reaches the client's acoustic map.
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

            // 5. Broadcast World State
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
    /// A login, from either transport. The cheap refusals happen here and now; the password is checked
    /// on the thread pool, and the session is made back on the tick thread through the command buffer.
    /// The returned task completes once that last step has been queued, which is what a test waits on.
    ///
    /// bcrypt used to run inline on the tick thread, so every login stalled the whole world for a
    /// tenth of a second — and a few hundred addresses each spending their allowed burst could stall it
    /// for minutes.
    /// </summary>
    internal Task Login(int connectionId, LoginRequest request, Action<IMessage> reply)
    {
        if (_sessions.TryGetSession(connectionId, out var already))
        {
            // A second login on a live session used to replace it, and the first body stayed in the
            // world with nobody attached to it.
            reply(new LoginResponse { Success = false, Message = $"You are already logged in as {already.Username}." });
            return Task.CompletedTask;
        }

        string address = RemoteAddressOf(connectionId);
        if (!_auth.Admit(address))
        {
            reply(new LoginResponse { Success = false, Message = AuthService.TooManyAttempts });
            return Task.CompletedTask;
        }

        // A network client built from a different OpenFPS.Common reads every message after this one
        // wrongly, and nothing downstream can say so: it spawns into nonsense. Refuse it here, by name.
        // The MUD gateway speaks text, not MemoryPack, so it has no contract to match.
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

        // One session per account, and the newest wins. A dropped connection takes LiteNetLib several
        // seconds to notice, and the player reconnecting in that time must not be told they are
        // already here; and two bodies with one name make every command that finds a player by name
        // pick one of them.
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
        // Back where they left from: the map now, the place on it when the body is made (HandlePlayerReady).
        // Read here, on the tick thread, and not from the record the password check fetched: a body of
        // this account leaving in between (the takeover above, or a lost connection queued before this)
        // has written a newer one.
        if (_store != null)
        {
            session.Saved = _store.Load(user.Username);
            session.CurrentMapId = _store.LandingMap(session, session.Saved, session.CurrentMapId);
        }
        _sessions.AddSession(connectionId, session);

        Log.Information("User {User} authenticated ({Transport}) from {Address}, landing on map '{Map}'.",
                        user.Username, peer == null ? "MUD" : "UDP", address, session.CurrentMapId);
        // Somebody taking over their own session from another machine has not arrived: they were
        // here all along, and saying they left and came back would be two notices about nothing.
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

        SendManifest(session);
    }

    /// <summary>
    /// Leaving on purpose: the same clean-up as a dropped connection, now rather than at the timeout.
    /// The session goes here rather than when the transport notices, so that everyone is told it
    /// logged out and not that its connection was lost.
    /// </summary>
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
    /// Ends a session from the server's side: tells the player why, takes their body out of the world
    /// the way a disconnect does, and closes the connection. The transport's own disconnect event then
    /// finds no session and does nothing more. Everyone else hears the notice if there is one; a
    /// session replaced by a new login of the same account has none, since nobody has gone.
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

    /// <summary>
    /// Closes connections, by either transport, that have been open for <see cref="LoginTimeout"/>
    /// without logging in. A connection costs memory and a slot whether or not it ever says anything.
    /// </summary>
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

        // The models changed in the world editor, before anything on the map is heard with them.
        // At the versions this map uses: its pins, else the current ones. Every changed model is sent, so
        // one pinned on the map just left is put back to what this map uses.
        foreach (var model in Models.UpdatesFor(_maps.Overlays?.Get(mapId).Pins)) SendToSession(session, model);

        int staticCount = 0;
        bool streamed = _maps.TryGetTiles(mapId, out var tiles);
        if (streamed)
        {
            // What the join will send: the tiles round where they will stand. Worked out again when the
            // data is asked for, with the client's own detail setting; this is the count it waits for.
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

        // Straight from the loaded map rather than re-reading every map file from disk per login.
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
        SendMapData(session, request);
    }

    /// <summary>
    /// The map's fixed entities, in batches, then MapLoadComplete: the whole map, or on a map streamed in
    /// tiles, the tiles round where the player will arrive (TileStreamer), and a TileStreamUpdate saying
    /// which. Runs on the tick thread (the dispatcher's). Internal so a test can drive a join without a
    /// socket: the messages go through <see cref="SendToSession"/> and its <see cref="Sent"/> hook.
    /// </summary>
    internal void SendMapData(UserSession session, MapDataRequest request)
    {
        // Only the map you are on. Any loaded map could be asked for by name, and a private one would
        // have streamed its whole layout to somebody it refuses at the door.
        if (!string.Equals(request.MapName, session.CurrentMapId, StringComparison.OrdinalIgnoreCase)) return;
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out var _, out var _, out var lookup)) return;
        var started = Stopwatch.StartNew();

        // The client clears its world on the manifest, so the server's record of what it knows starts
        // over here too — otherwise a re-request would leave the two disagreeing about a set the removal
        // logic is driven from.
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
            batch.Definitions.Add(CreateDefinition(world, e));
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
    /// Takes a player off the map they are on and puts them on another loaded one, at its spawn point,
    /// or where they were on it when they last left it if that is still somewhere to stand.
    ///
    /// Runs on the tick thread. The old body is got out of any seat, stored with what it was carrying
    /// (which comes with it to the new map, and the place it left is kept for coming back to this one),
    /// destroyed and announced as gone; everything the server remembers having sent the client is forgotten, so the new map's entities all go
    /// out fresh. Then the new map is sent exactly as login sends one: a graphical client gets a
    /// MapManifest and goes through map data, MapLoadComplete and 'ready' again; a text client has
    /// no geometry to load and is spawned straight away. Access is checked by the caller
    /// (see <see cref="DiscoveryService.CanEnter"/>).
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
        else HandlePlayerReady(session.ConnectionId); // a text session has nothing to load
    }

    // Definition building lives in EntityDefinitionFactory so the streaming path and the map/acoustics
    // tests share one implementation — the client builds its acoustic map (regions AND portals) from
    // these, so any divergence would only surface as a wrong-sounding room.
    private EntityDefinition CreateDefinition(World world, Entity e) => EntityDefinitionFactory.From(world, e);

    private readonly HashSet<int> _visibleDynamicBuffer = new();
    /// <summary>What a tick's broadcast chooses from, gathered once for the map rather than once for
    /// every player: each collidable entity, whether it moves, and whether it moved this tick.</summary>
    private readonly List<(Entity Entity, bool Dynamic, bool Dirty)> _broadcastCandidates = new();
    /// <summary>The spatial grid's cell (MapManager makes it 10 m). The grid answered a radius with
    /// every cell that touched it, so a thing up to a cell beyond earshot was sent; this keeps that.</summary>
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
    /// What one tick's broadcast chooses from on a map: every entity that has a collider, and every item.
    ///
    /// The items because they have no collider — you walk over a gun on the floor, and a shot does not
    /// stop on it — and a broadcast that chose only from the collidable never once mentioned one. An item
    /// /give made, or one picked up and put down, never reached a client at all: the client did not know
    /// the gun was on the floor, so its item beacon had nothing to sound from (Cody, 2026-10-04: "when I
    /// drop items I still don't hear them"). An item is the one thing that changes hands after the map is
    /// streamed, so it is the one colliderless thing the broadcast has to carry.
    /// </summary>
    internal static void GatherBroadcastCandidates(World world, List<(Entity Entity, bool Dynamic, bool Dirty)> into)
    {
        into.Clear();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>(), (Entity e, ref Transform t) =>
            into.Add((e, Moves(world, e), t.IsDirty)));
        world.Query(new QueryDescription().WithAll<Transform, ItemComponent>().WithNone<ColliderComponent>(), (Entity e, ref Transform t) =>
            into.Add((e, Moves(world, e), t.IsDirty)));
    }

    /// <summary>
    /// Sent every tick, unreliably, like a body: anything with a velocity, a player, and a thing somebody
    /// is carrying. A carried gun moves whenever its holder does; as a static that moved it went out on
    /// the reliable channel every tick anyone walked with one, and a lost packet then held up every chat
    /// line and definition behind it.
    /// </summary>
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

            // Every player used to ask the spatial grid for everything within earshot, and on the city
            // earshot is the whole map: 86,000 grid entries a player a tick (a wall is filed in every
            // cell it crosses) to find 7,500 entities, 16 ms each on a desktop. Two players overran the
            // 33 ms tick on the VPS, the loop fell behind every few ticks, and everything anyone heard
            // trailed what it belonged to. The grid holds exactly the collidable entities, so one pass
            // over the world finds the same set once, with no repeats; each player then filters it.
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

                    // What the client needs to know about itself that is not in its transform: whether
                    // its position is its own to predict, a seat's to decide, or held still.
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
                        // Geometry that stays put, has not moved, and this client already has (the whole
                        // map was streamed to it on arrival) has nothing to say: most of the city, every
                        // tick, settled by one lookup.
                        bool known = session.KnownEntities.Contains(e.Id);
                        if (!isDynamic && !isDirty && known && !_dirtyAudioBuffer.Contains(e.Id)) continue;
                        // On a streamed map the map's own geometry is the tile streamer's to send, with
                        // its tile: not here, one wall at a time, because it is in earshot.
                        if (!isDynamic && !known && tiles != null && tiles.IsTiled(e.Id)) continue;
                        // Anything taken, killed or removed earlier in this tick: asking a dead entity
                        // what it has throws, which used to skip this player's whole update.
                        if (!world.IsAlive(e)) continue;
                        ref var t = ref world.Get<Transform>(e);
                        if (MathF.Abs(t.Position.X - pPos.X) > reach || MathF.Abs(t.Position.Z - pPos.Z) > reach) continue;
                        // ...and everything else only while it stands in a tile this client has: a car
                        // that drives out of them goes as anything leaving earshot does.
                        if (tiles != null && e != session.Entity && !tiles.IsTiled(e.Id) && !tiles.IsGlobal(e.Id)
                            && !tiles.Holds(t.Position, session.Tiles.Levels)) continue;

                        if (isDynamic) _visibleDynamicBuffer.Add(e.Id);

                        // A state message names an entity the client may never have heard of — every
                        // remote player, and anything spawned at runtime. Send the definition first, and
                        // only once: KnownEntities is what makes it once rather than every tick.
                        bool isNew = session.KnownEntities.Add(e.Id);
                        bool defined = isNew || _dirtyAudioBuffer.Contains(e.Id);
                        // Somebody getting into or out of a seat: the definition says which, and it is
                        // the only thing that tells a client a body moving at a car's speed is not
                        // running (EntityDefinition.RidingEntityId). Noticed here rather than at each
                        // way in and out of a seat, of which there are many.
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
                        // How hard it is working its tyres. Sent because only the server can know it:
                        // a banked corner and a flat one look identical in a velocity, and a listener
                        // dividing lateral acceleration by flat grip reads every car on a banked oval
                        // as sliding. See EntityState.TyreDemand.
                        if (_vehicles.TryGetTyreDemand(e.Id, out float tyreDemand))
                            state.TyreDemand = NetworkEntityState.EncodeTyreDemand(tyreDemand);
                        // ...and a car somebody is driving, which is not traffic and was never asked:
                        // its tyres never squealed, however hard it was thrown into a corner.
                        else if (world.Has<DriveComponent>(e))
                            state.TyreDemand = NetworkEntityState.EncodeTyreDemand(world.Get<DriveComponent>(e).TyreDemand);
                        // Each wheel: its load, slip, speed and the surface under it (WheelDynamics).
                        if (_vehicles.TryGetWheels(e.Id, out var wheels) || DrivingSystem.TryGetWheels(e.Id, out wheels))
                            state.Wheels = wheels;
                        // A driven vehicle's horn and siren switches: nothing a listener can observe
                        // says a hand is on the horn.
                        if (isDynamic) state.Signals = VehicleSignals.WireByte(world, e);

                        // A dynamic entity is corrected by the next tick's packet, so losing one costs
                        // nothing. A static entity that moved is a one-off event that nothing will ever
                        // resend — on the unreliable channel a single dropped packet leaves that client
                        // colliding with a wall that is no longer there. Reliable delivery IS the
                        // acknowledgement; there is no separate ack to wait for.
                        //
                        // A dynamic entity that has not moved is not sent at all, past a few repeats and
                        // a keep-alive a second (RestingStates): two thirds of the city, every tick.
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
                    // Anything dynamic this client could see and now cannot is a ghost on their side.
                    // Static geometry is never evicted: the client's acoustic map is built from the whole
                    // streamed map, so dropping a distant wall would change how the world sounds.
                    CollectDeparted(session.VisibleDynamicEntities, _visibleDynamicBuffer, _removedBuffer);
                    // A thing that was being carried and has just been put down has stopped moving, not gone:
                    // it stays known as the fixed thing it now is, rather than being taken off the client
                    // and sent again a tick later, which would blink its beacon out as it landed.
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
                    // Only when something in it has changed. It went every tick, reliably, to say the
                    // same health and the same floor thirty times a second; it is reliable, so the one
                    // that says something new cannot be lost.
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
            // Cleanup dirty flags after broadcast
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

    /// <summary>
    /// A telnet connection closed. Closing it is how a text player leaves, since there is no logout
    /// for them to send first, so it is announced as logging out.
    /// </summary>
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

    /// <summary>
    /// What a dropped connection tells everyone. A client that closed its own connection (the window
    /// shut without going through the menu) left on purpose. Anything else is a lost connection, which
    /// is worth telling apart, because that player is probably on their way back.
    /// </summary>
    internal static PresenceKind PresenceFor(DisconnectReason reason)
        => reason is DisconnectReason.RemoteConnectionClose or DisconnectReason.DisconnectPeerCalled
            ? PresenceKind.LoggedOut
            : PresenceKind.WentOffline;

    /// <summary>
    /// Takes a disconnected session's body out of the world, on the tick thread.
    ///
    /// Queued even when the session has no body yet: a 'ready' queued just before the disconnect
    /// spawns one when the buffer drains, and this runs after it and takes it away again.
    /// </summary>
    internal void DespawnSession(UserSession session)
    {
        _commandBuffer.Enqueue(() => LeaveWorld(session));
    }

    /// <summary>
    /// Takes a session's body off the map it is on: out of any seat, stored (where it stood, its
    /// health, and its things, which leave the world with it: PlayerStore), destroyed, and announced
    /// as gone. Runs on the tick thread. Changing map, logging out, a lost connection, a kick and a
    /// shutdown all come through here, so a player who leaves any way comes back the same way.
    ///
    /// The seat and the things go first, while the body is still there to be got out and to drop
    /// from. Anything the store would not keep (no prefab to make it from again, or a store that
    /// cannot keep things) is put down where the body stood. Destroyed while carrying, the things kept
    /// a HeldComponent naming a dead holder, so nobody could pick them up again, and a ParentComponent
    /// naming an id Arch would reuse.
    /// Without the announcement every other client keeps the corpse forever: it still occupies
    /// space, still answers scans, and still plays whatever sound it carried.
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
                // Out of the seat first, standing beside the vehicle wherever it is: leaving the map is
                // not a request, so a moving vehicle or shut doors do not refuse it. Without the seat
                // service (a test rig), unseated where they sit.
                if (world.Has<OccupantComponent>(body) && (_seats == null || !_seats.Exit(session, out _, leavingWorld: true)))
                    CompositeService.Disembark(world, body);
                // Where they stand now, their health and their things into the store, and the things out
                // of the world with it. Whatever the store would not keep is put down, as everything was.
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
            other.SentStates.Remove(entityId);
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
            // A tap you are standing at, first: it needs you right beside it (TapReach), so it never takes
            // the key from a door across the room, and a door five metres off would otherwise always win.
            if (ToggleTapInReach(world, position, Say)) return;

            if (OpenDoorInReach(world, position, session.Entity, Say)) return;

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

            // Something lying at your feet: pick it up. Before shutting a door, because an item within
            // two metres is far likelier to be what you meant (Cody, 2026-10-04: a dropped gun could not
            // be picked up with E, only with G).
            if (_hands.TakeWithin(session, PhysicsConstants.PickUpReach, out string took)) { Say(took); return; }

            // Nothing to get into, and an open door within reach: shut it. So beside a house, E opens
            // the door and E again shuts it, the way a handle does; beside a car the sequence is still
            // door, then in. Shutting is the loud half of a door, and it was only reachable by typing.
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

    /// <summary>
    /// Opens the shut door within arm's length, if there is one. Says so, and says nothing otherwise.
    ///
    /// Only SHUT doors count, which is what turns the interact key into a SEQUENCE rather than a
    /// toggle that fights you: once the door is open, the key moves on to meaning "get in" or "get
    /// out". Somebody who wants it shut again says so.
    /// </summary>
    private static bool OpenDoorInReach(World world, Vector3 position, Entity who, Action<string> say)
    {
        Entity? nearest = null;
        float best = PhysicsConstants.InteractionRange;
        string name = "door";
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent d) =>
        {
            if (d.Target > 0f) return;                    // already open, or on its way
            if (!DoorSystem.OpensByHand(d)) return;       // it opens for you, or for the lift
            float distance = Vector3.Distance(position, t.Position);
            if (distance > best) return;
            best = distance; nearest = e;
            name = world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
                 ? world.Get<IdentityComponent>(e).Name : "door";
        });

        if (nearest == null) return false;
        if (!DoorSystem.Set(world, nearest.Value, open: true, by: position, who: who)) return false;
        var opened = world.Get<DoorComponent>(nearest.Value);
        say(DoorSystem.OpenedPhrase(opened, name) + "."
            + (DoorSystem.InTheWay(world, nearest.Value, 1f, who) != null ? " Someone is in the way of it." : ""));
        return true;
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
            // Measured to where the door SHUTS, not where the leaf has swung to: that is the doorway,
            // which is what somebody standing in front of it is next to. In the world's frame: a door
            // in a building keeps its shut pose in the building's.
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
            // The reply follows what the store did: a duplicate username must not be told its account
            // was created.
            EnqueueCommand(() => reply(new RegisterResponse { Success = outcome.Success, Message = outcome.Message }));
        }, TaskScheduler.Default);
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

            // --port lets a second server be brought up beside a running one, to smoke-test a change
            // without taking someone's session down.
            // --map picks the landing map, where every player arrives at login. Without it the map
            // claiming IsDefault is the landing map; players reach the others with /join.
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
