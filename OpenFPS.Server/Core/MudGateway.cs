using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using OpenFPS.Common.Networking;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>The text gateway: telnet connections whose lines become the same messages a game client sends.</summary>
public class MudGateway
{
    private readonly TcpListener _listener;
    private readonly IMessageDispatcher _dispatcher;
    private bool _isRunning;
    private int _nextConnectionId = 10000; // above LiteNetLib's peer ids, so the two never collide
    
    private readonly ConcurrentDictionary<int, MudConnection> _connections = new();

    /// <summary>The longest line read. Anything longer is thrown away up to its newline.</summary>
    public const int MaxLineLength = 512;
    /// <summary>Connections from one address (an IPv6 /64) at once.</summary>
    public const int MaxConnectionsPerAddress = 4;
    /// <summary>Connections at once, all addresses together.</summary>
    public const int MaxConnections = 64;
    /// <summary>
    /// Lines waiting to be written to one connection. A client that stops reading fills this and is
    /// closed: writing on the tick thread, one unread telnet window would stop the whole server.
    /// </summary>
    public const int MaxQueuedLines = 256;

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Raised on the connection's own task thread when a MUD client goes away, so the server ends the
    /// session and removes the body a telnet player may have.
    /// </summary>
    public Action<int>? OnDisconnected;

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
        /// Everything written to this connection, for its one writer task. Replies come from the tick
        /// thread, the pool and the reader alike; one writer means no interleaving and no caller waits.
        /// </summary>
        public readonly Channel<string> Outbox = Channel.CreateBounded<string>(
            new BoundedChannelOptions(MaxQueuedLines) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public int Closed;
    }

    public MudGateway(int port, IMessageDispatcher dispatcher)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _dispatcher = dispatcher;
    }

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

    /// <summary>Accepts connections, each served by a task of its own.</summary>
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

    /// <summary>Reads a connection's lines and dispatches them, until it closes.</summary>
    private async Task HandleConnection(MudConnection conn)
    {
        var lines = new BoundedLineReader(conn.Reader);
        try
        {
            while (conn.Client.Connected)
            {
                var (line, tooLong) = await lines.ReadLineAsync(MaxLineLength);
                if (line == null) break;

                // Bounded while reading: ReadLine would hold a gigabyte with no newline before anything looked.
                if (tooLong)
                {
                    Enqueue(conn, $"Error: Command too long (max {MaxLineLength} chars).");
                    continue;
                }

                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // At most 5 commands a second.
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
        if (parts.Length >= 3 && (parts[0].Equals("login", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("register", StringComparison.OrdinalIgnoreCase)))
            return $"{parts[0].ToLowerInvariant()} {AuthService.ForLog(parts[1])} [password]";
        return AuthService.ForLog(line);
    }

    /// <summary>A reply to the MUD client, as plain text.</summary>
    private static void SendReply(MudConnection conn, IMessage reply)
    {
        string text = FormatReply(reply);
        if (string.IsNullOrEmpty(text)) return;
        Enqueue(conn, text);
    }

    /// <summary>
    /// A message to a MUD connection outside a request and reply: chat, private messages, world events.
    /// False if the id is not a live MUD connection.
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
        // The writer finishes what is queued and closes; a client not reading gets five seconds.
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

    /// <summary>A game message as a line for the MUD player; empty for what a text player is not told.</summary>
    private static string FormatReply(IMessage reply)
    {
        return reply switch
        {
            PlayerListResponse p => "Players online: " + (p.Players.Length > 0 ? string.Join(", ", p.Players) : "None"),
            FriendListResponse f => "Friends: " + (f.Friends.Length > 0
                ? string.Join(", ", f.Friends.Select((name, i) => i < f.Online.Length && f.Online[i] ? $"{name} (online)" : name))
                : "None"),
            MapListResponse m => FormatMaps(m),
            // A refresh is a game client's menu catching up with a change; a text player has the result.
            EditorMenu { Refresh: true } => "",
            EditorMenu e => FormatEditorMenu(e),
            LoginResponse l => l.Success ? "Login successful." : "Login failed: " + l.Message,
            RegisterResponse r => r.Success ? "Registration successful." : "Registration failed: " + r.Message,
            PlayerSpawned => "You are now in the world. Try 'scan'.",
            TextEvent t => t.Text,
            // The one world sound a text player can be told word for word.
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
            _ => ""
        };
    }

    /// <summary>
    /// A world editor menu as numbered lines, each with what to type for it: the same menu the game
    /// client shows, so a text player can do everything the menu does.
    /// </summary>
    public static string FormatEditorMenu(EditorMenu menu)
    {
        var lines = new List<string> { $"{menu.Title}:" };
        for (int i = 0; i < menu.Items.Length; i++)
        {
            var item = menu.Items[i];
            string how = item.Kind switch
            {
                EditorItemKind.Menu => $" (type: edit menu {item.Command})",
                EditorItemKind.Action => $" (type: {item.Command})",
                EditorItemKind.Input => $" (type: {item.Command.TrimStart('/').TrimEnd()} and a value)",
                _ => "",
            };
            lines.Add($"  {i + 1}. {item.Label}{how}");
        }
        if (menu.Items.Length == 0) lines.Add("  Nothing here.");
        return string.Join("\n", lines);
    }

    private static string FormatMaps(MapListResponse response)
    {
        string what = response.Scope == MapListScope.Mine ? "Your maps" : "Maps on this server";
        if (response.Maps.Length == 0) return $"{what}: none.";

        var lines = new List<string>();
        // The world's places first, as /join takes them, then the maps.
        var places = response.Maps.Where(m => m.IsWorldPlace).ToList();
        if (places.Count > 0)
        {
            lines.Add("The world:");
            foreach (var p in places)
                lines.Add($"  {p.Name} (/join {p.Id}){(p.PlayerCount == 0 ? "" : p.PlayerCount == 1 ? ": 1 player" : $": {p.PlayerCount} players")}{(p.IsCurrent ? ", near where you are" : "")}.");
        }
        lines.Add($"{what}:");
        foreach (var map in response.Maps.Where(m => !m.IsWorldPlace))
        {
            string people = map.PlayerCount == 1 ? "1 player" : $"{map.PlayerCount} players";
            string named = string.IsNullOrWhiteSpace(map.Name) || map.Name == map.Id ? map.Id : $"{map.Name} ({map.Id})";
            lines.Add($"  {named}: {people}{(map.IsPublic ? "" : ", private")}{(map.IsCurrent ? ", where you are" : "")}.");
        }
        return string.Join("\n", lines);
    }

    /// <summary>A typed line as a message: a few words of its own, and everything else a text command.</summary>
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
            "say" when parts.Length >= 2 => new ChatMessage { Text = string.Join(' ', parts[1..]) },
            _ => new TextCommand { Command = cmd, Args = parts.Length > 1 ? parts[1..] : Array.Empty<string>() }
        };
    }
}
