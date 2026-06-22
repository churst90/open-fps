using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
                int id = Interlocked.Increment(ref _nextConnectionId);
                var stream = client.GetStream();
                var conn = new MudConnection
                {
                    Id = id,
                    Client = client,
                    Reader = new StreamReader(stream, Encoding.UTF8),
                    Writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true },
                    LastSecondTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                _connections[id] = conn;
                
                await conn.Writer.WriteLineAsync("Welcome to OpenFPS MUD. Type 'who' to list players, 'friends' to list friends, or 'login [user] [pass]'.");
                
                _ = Task.Run(() => HandleConnection(conn));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MUD Gateway accept error.");
            }
        }
    }

    /// <summary>
    /// Per-connection loop that reads lines of text and dispatches them as IMessage objects.
    /// </summary>
    private async Task HandleConnection(MudConnection conn)
    {
        try
        {
            while (conn.Client.Connected)
            {
                var line = await conn.Reader.ReadLineAsync();
                if (line == null) break;

                // Robustness: Message length check
                if (line.Length > 512)
                {
                    await conn.Writer.WriteLineAsync("Error: Command too long (max 512 chars).");
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
                    await conn.Writer.WriteLineAsync("Error: Rate limit exceeded (max 5 commands per second).");
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
                        await conn.Writer.WriteLineAsync("Unknown command.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error processing MUD command '{Line}' for connection {Id}", line, conn.Id);
                    await conn.Writer.WriteLineAsync("Internal error processing command.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MUD Connection {Id} experienced a fatal error.", conn.Id);
        }
        finally
        {
            _connections.TryRemove(conn.Id, out _);
            conn.Client.Close();
            Log.Information("MUD Connection {Id} closed.", conn.Id);
        }
    }

    /// <summary>
    /// Sends a response message back to the MUD client, formatting it as plain text.
    /// </summary>
    private void SendReply(MudConnection conn, IMessage reply)
    {
        try
        {
            string text = FormatReply(reply);
            if (!string.IsNullOrEmpty(text))
            {
                conn.Writer.WriteLine(text);
            }
        }
        catch
        {
            // Fail silently for disconnected clients
        }
    }

    /// <summary>
    /// Translates a structured game message (IMessage) into a human-readable string for the MUD player.
    /// </summary>
    private string FormatReply(IMessage reply)
    {
        return reply switch
        {
            PlayerListResponse p => "Players online: " + (p.Players.Length > 0 ? string.Join(", ", p.Players) : "None"),
            FriendListResponse f => "Friends: " + (f.Friends.Length > 0 ? string.Join(", ", f.Friends) : "None"),
            LoginResponse l => l.Success ? "Login successful." : "Login failed: " + l.Message,
            TextEvent t => t.Text,
            ChatMessage c => $"[{c.Sender}]: {c.Text}",
            _ => "" // Movement and world state updates are not converted to text for performance/verbosity reasons.
        };
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
            "friends" => new FriendListRequest(),
            "login" when parts.Length >= 3 => new LoginRequest { Username = parts[1], Password = parts[2] },
            _ => new TextCommand { Command = cmd, Args = parts.Length > 1 ? parts[1..] : Array.Empty<string>() }
        };
    }
}
