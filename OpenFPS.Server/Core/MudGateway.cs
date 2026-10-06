using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenFPS.Common.Networking;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Gateway for text-based MUD (Multi-User Dungeon) connections.
/// Listens for TCP connections and translates plain-text commands into structured game messages.
/// This allows accessibility-focused clients (like screen readers via Telnet) to interact with the 3D world.
/// </summary>
public class MudGateway
{
    private readonly TcpListener _listener;
    private readonly IMessageDispatcher _dispatcher;
    private bool _isRunning;
    private int _nextConnectionId = 10000; // Offset to avoid ID collision with LiteNetLib's internal peer IDs
    
    private readonly ConcurrentDictionary<int, MudConnection> _connections = new();

    /// <summary>The longest line read. Anything longer is thrown away up to its newline.</summary>
    public const int MaxLineLength = 512;
    /// <summary>Connections from one address (an IPv6 /64) at once.</summary>
    public const int MaxConnectionsPerAddress = 4;
    /// <summary>Connections at once, all addresses together.</summary>
    public const int MaxConnections = 64;
    /// <summary>
    /// Lines waiting to be written to one connection. A client that stops reading fills this, and is
    /// then closed: the writes used to be synchronous on the tick thread, so one telnet window left
    /// unread would have stopped the whole server once its socket buffer filled.
    /// </summary>
    public const int MaxQueuedLines = 256;

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Raised (on the connection's own task thread) when a MUD client goes away. The server uses it to
    /// end the session and remove the player's body — a telnet player can now spawn one, so without this
    /// every dropped connection would leave a corpse standing in the world.
    /// </summary>
    public Action<int>? OnDisconnected;

    /// <summary>
    /// Internal container for a MUD client's connection state.
    /// </summary>
    private class MudConnection
    {
        public int Id;
        public TcpClient Client = null!;
        public StreamReader Reader = null!;
        public StreamWriter Writer = null!;
        public int CommandsThisSecond;
        public long LastSecondTimestamp;
        public string Address = "";
        public DateTime ConnectedUtc = DateTime.UtcNow;
        /// <summary>
        /// Everything written to this connection goes through here, to one writer task. Replies arrive
        /// from the tick thread, the thread pool and the reader task alike; one writer means no two
        /// lines interleave and no caller ever waits on the network.
        /// </summary>
        public readonly Channel<string> Outbox = Channel.CreateBounded<string>(
            new BoundedChannelOptions(MaxQueuedLines) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public int Closed;
    }

    /// <summary>
    /// Creates a new MUD gateway.
    /// </summary>
    /// <param name="port">The TCP port to listen on.</param>
    /// <param name="dispatcher">The dispatcher to route incoming commands to game logic.</param>
    public MudGateway(int port, IMessageDispatcher dispatcher)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Starts the asynchronous acceptance loop for new MUD connections.
    /// </summary>
    public void Start()
    {
        try
        {
            _isRunning = true;
            _listener.Start();
            Task.Run(AcceptLoop);
            Log.Information("MUD Gateway listening on TCP port {Port}", ((IPEndPoint)_listener.LocalEndpoint).Port);
        }
        catch (SocketException ex)
        {
            _isRunning = false;
            Log.Warning(ex, "MUD Gateway failed to start (port may already be in use). MUD interface disabled.");
        }
    }

    /// <summary>
    /// Main loop for accepting incoming TCP connections. 
    /// Dispatches a background Task for each client to prevent blocking.
    /// </summary>
    private async Task AcceptLoop()
    {
        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                string address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
                string key = RateLimiter.AddressKey(address);
                int fromThere = _connections.Values.Count(c => RateLimiter.AddressKey(c.Address) == key);
                string? refusal = _connections.Count >= MaxConnections ? "The server is full. Try again later."
                                : fromThere >= MaxConnectionsPerAddress ? "Too many connections from your address."
                                : null;
                if (refusal != null)
                {
                    Log.Warning("MUD: refused a connection from {Address}: {Reason}", address, refusal);
                    _ = RefuseAsync(client, refusal);
                    continue;
                }

                int id = Interlocked.Increment(ref _nextConnectionId);
                var stream = client.GetStream();
                var conn = new MudConnection
                {
                    Id = id,
                    Client = client,
                    Reader = new StreamReader(stream, Encoding.UTF8),
                    // No byte-order mark: a telnet client prints it, and a screen reader reads it out.
                    Writer = new StreamWriter(stream, NoBom) { AutoFlush = true },
                    LastSecondTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Address = address,
                };
                _connections[id] = conn;
                Log.Information("MUD connection {Id} opened from {Address}.", id, address);

                _ = Task.Run(() => WriteLoop(conn));
                Enqueue(conn, "Welcome to OpenFPS MUD. Type 'login [user] [pass]' to log in. Then 'who' lists players and 'friends' your friends.");

                _ = Task.Run(() => HandleConnection(conn));
            }
            catch (Exception ex)
            {
                if (_isRunning) Log.Warning(ex, "MUD Gateway accept error.");
            }
        }
    }

    private static async Task RefuseAsync(TcpClient client, string reason)
    {
        try
        {
            using (client)
            {
                var writer = new StreamWriter(client.GetStream(), NoBom) { AutoFlush = true };
                var write = writer.WriteLineAsync(reason);
                await Task.WhenAny(write, Task.Delay(1000));
            }
        }
        catch { /* gone already */ }
    }

    /// <summary>The one writer for a connection: drains its outbox, then closes it.</summary>
    private static async Task WriteLoop(MudConnection conn)
    {
        try
        {
            await foreach (var line in conn.Outbox.Reader.ReadAllAsync())
                await conn.Writer.WriteLineAsync(line);
        }
        catch
        {
            // The client went away mid-write; the reader notices too.
        }
        finally
        {
            Close(conn);
        }
    }

    /// <summary>
    /// Queues a line for a connection. A connection whose queue is full is not reading, and is closed
    /// rather than waited for.
    /// </summary>
    private static void Enqueue(MudConnection conn, string text)
    {
        if (conn.Outbox.Writer.TryWrite(text)) return;
        if (Volatile.Read(ref conn.Closed) == 0)
            Log.Warning("MUD connection {Id} ({Address}) is not reading what it is sent; closing it.", conn.Id, conn.Address);
        Close(conn);
    }

    private static void Close(MudConnection conn)
    {
        if (Interlocked.Exchange(ref conn.Closed, 1) == 1) return;
        conn.Outbox.Writer.TryComplete();
        try { conn.Client.Close(); } catch { }
    }

    /// <summary>
    /// Per-connection loop that reads lines of text and dispatches them as IMessage objects.
    /// </summary>
    private async Task HandleConnection(MudConnection conn)
    {
        var lines = new BoundedLineReader(conn.Reader);
        try
        {
            while (conn.Client.Connected)
            {
                var (line, tooLong) = await lines.ReadLineAsync(MaxLineLength);
                if (line == null) break;

                // Robustness: Message length check. Bounded while reading, not after: ReadLine would
                // have held a gigabyte with no newline in memory before anything could look at it.
                if (tooLong)
                {
                    Enqueue(conn, $"Error: Command too long (max {MaxLineLength} chars).");
                    continue;
                }

                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Robustness: Rate Limiting (max 5 cmds / sec)
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (now > conn.LastSecondTimestamp)
                {
                    conn.LastSecondTimestamp = now;
                    conn.CommandsThisSecond = 0;
                }

                if (++conn.CommandsThisSecond > 5)
                {
                    Enqueue(conn, "Error: Rate limit exceeded (max 5 commands per second).");
                    continue;
                }

                try
                {
                    IMessage? msg = ParseCommand(line);
                    if (msg != null)
                    {
                        // Dispatch to Game Services and provide the SendReply method as the response target
                        _dispatcher.Dispatch(conn.Id, msg, reply => SendReply(conn, reply));
                    }
                    else
                    {
                        Enqueue(conn, "Unknown command.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error processing MUD command '{Line}' for connection {Id}", Redact(line), conn.Id);
                    Enqueue(conn, "Internal error processing command.");
                }
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref conn.Closed) == 0)
                Log.Error(ex, "MUD Connection {Id} experienced a fatal error.", conn.Id);
        }
        finally
        {
            _connections.TryRemove(conn.Id, out _);
            Close(conn);
            Log.Information("MUD Connection {Id} closed.", conn.Id);
            try { OnDisconnected?.Invoke(conn.Id); }
            catch (Exception ex) { Log.Warning(ex, "MUD disconnect handler failed for connection {Id}.", conn.Id); }
        }
    }

    /// <summary>A line fit for the log: a login keeps its name and loses its password.</summary>
    public static string Redact(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("login", StringComparison.OrdinalIgnoreCase))
            return $"login {AuthService.ForLog(parts[1])} [password]";
        return AuthService.ForLog(line);
    }

    /// <summary>
    /// Sends a response message back to the MUD client, formatting it as plain text.
    /// </summary>
    private static void SendReply(MudConnection conn, IMessage reply)
    {
        string text = FormatReply(reply);
        if (string.IsNullOrEmpty(text)) return;
        Enqueue(conn, text);
    }

    /// <summary>
    /// Pushes a message to a MUD connection outside a request/reply exchange — chat, private messages and
    /// world events that the UDP clients receive by peer. Without this a MUD player can talk but never
    /// hears anyone answer. Returns false if the id is not a live MUD connection.
    /// </summary>
    public bool TrySend(int connectionId, IMessage message)
    {
        if (!_connections.TryGetValue(connectionId, out var conn)) return false;
        SendReply(conn, message);
        return true;
    }

    /// <summary>True if the id belongs to a live MUD connection rather than a UDP peer.</summary>
    public bool IsMudConnection(int connectionId) => _connections.ContainsKey(connectionId);

    /// <summary>The remote address of a MUD connection, for rate limiting. Null if the id is not ours.</summary>
    public string? GetRemoteAddress(int connectionId)
        => _connections.TryGetValue(connectionId, out var conn) ? conn.Address : null;

    /// <summary>When a MUD connection was opened, or null if the id is not ours.</summary>
    public DateTime? ConnectedSince(int connectionId)
        => _connections.TryGetValue(connectionId, out var conn) ? conn.ConnectedUtc : null;

    /// <summary>Every open MUD connection and when it was opened.</summary>
    public IEnumerable<(int Id, DateTime SinceUtc)> Connections()
        => _connections.Values.Select(c => (c.Id, c.ConnectedUtc));

    /// <summary>
    /// Closes a connection from this end, after writing <paramref name="finalLine"/> and whatever was
    /// already queued. The usual disconnect follows when its reader notices.
    /// </summary>
    public void Disconnect(int connectionId, string? finalLine = null)
    {
        if (!_connections.TryGetValue(connectionId, out var conn)) return;
        if (finalLine != null) conn.Outbox.Writer.TryWrite(finalLine);
        // Completing the outbox lets the writer finish what is queued and then close the socket; a
        // client that is not reading gets five seconds of that before it is closed anyway.
        conn.Outbox.Writer.TryComplete();
        _ = Task.Delay(5000).ContinueWith(_ => Close(conn), TaskScheduler.Default);
    }

    /// <summary>Stops accepting connections and closes the open ones. Idempotent.</summary>
    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;

        try { _listener.Stop(); }
        catch (Exception ex) { Log.Warning(ex, "MUD Gateway: error stopping the listener."); }

        foreach (var kv in _connections)
        {
            kv.Value.Outbox.Writer.TryWrite("Server shutting down. Goodbye.");
            kv.Value.Outbox.Writer.TryComplete();
        }
        _connections.Clear();
        Log.Information("MUD Gateway stopped.");
    }

    /// <summary>
    /// Translates a structured game message (IMessage) into a human-readable string for the MUD player.
    /// </summary>
    private static string FormatReply(IMessage reply)
    {
        return reply switch
        {
            PlayerListResponse p => "Players online: " + (p.Players.Length > 0 ? string.Join(", ", p.Players) : "None"),
            FriendListResponse f => "Friends: " + (f.Friends.Length > 0
                ? string.Join(", ", f.Friends.Select((name, i) => i < f.Online.Length && f.Online[i] ? $"{name} (online)" : name))
                : "None"),
            MapListResponse m => FormatMaps(m),
            LoginResponse l => l.Success ? "Login successful." : "Login failed: " + l.Message,
            RegisterResponse r => r.Success ? "Registration successful." : "Registration failed: " + r.Message,
            PlayerSpawned => "You are now in the world. Try 'scan'.",
            TextEvent t => t.Text,
            // Somebody in the street saying something: the one world sound a text player can be told
            // word for word.
            WorldAudioEvent w when w.Label.StartsWith("speech: ", StringComparison.Ordinal)
                => $"Someone nearby says: \"{w.Label["speech: ".Length..]}\"",
            // Somebody came or went: the notice is the whole line, with no sender in front of it.
            ChatMessage { Presence: not PresenceKind.None } p => p.Text,
            ChatMessage c => c.Channel switch
            {
                ChatChannel.Private when c.To.Length > 0 => $"[to {c.To}]: {c.Text}",
                ChatChannel.Private => $"[from {c.Sender}]: {c.Text}",
                ChatChannel.All => $"[{c.Sender}, to all]: {c.Text}",
                ChatChannel.Team => $"[{c.Sender}, to team]: {c.Text}",
                _ => $"[{c.Sender}]: {c.Text}",
            },
            _ => "" // Movement and world state updates are not converted to text for performance/verbosity reasons.
        };
    }

    private static string FormatMaps(MapListResponse response)
    {
        string what = response.Scope == MapListScope.Mine ? "Your maps" : "Maps on this server";
        if (response.Maps.Length == 0) return $"{what}: none.";

        var lines = new List<string> { $"{what}:" };
        foreach (var map in response.Maps)
        {
            string people = map.PlayerCount == 1 ? "1 player" : $"{map.PlayerCount} players";
            string named = string.IsNullOrWhiteSpace(map.Name) || map.Name == map.Id ? map.Id : $"{map.Name} ({map.Id})";
            lines.Add($"  {named}: {people}{(map.IsPublic ? "" : ", private")}{(map.IsCurrent ? ", where you are" : "")}.");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Basic text parser that maps natural language commands to internal Message types.
    /// </summary>
    private IMessage? ParseCommand(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var cmd = parts[0].ToLowerInvariant();
        return cmd switch
        {
            "who" => new PlayerListRequest { Scope = PlayerListScope.Server },
            "who_map" => new PlayerListRequest { Scope = PlayerListScope.Map },
            "maps" => new MapListRequest { Scope = MapListScope.Server },
            "mymaps" => new MapListRequest { Scope = MapListScope.Mine },
            "friends" => new FriendListRequest(),
            "login" when parts.Length >= 3 => new LoginRequest { Username = parts[1], Password = parts[2] },
            // Chat now reaches MUD players; this is the other half — a way for them to answer.
            "say" when parts.Length >= 2 => new ChatMessage { Text = string.Join(' ', parts[1..]) },
            _ => new TextCommand { Command = cmd, Args = parts.Length > 1 ? parts[1..] : Array.Empty<string>() }
        };
    }
}
