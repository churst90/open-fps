using OpenFPS.Common;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using MemoryPack;
using OpenFPS.Common.Networking;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Responsible for low-level UDP networking, message queuing, and reliable delivery.
/// </summary>
public class NetworkService : INetEventListener
{
    private NetManager _netManager = null!;
    private readonly ConcurrentQueue<(NetPeer peer, IMessage message)> _incomingMessages = new();

    /// <summary>
    /// The largest message a client may send. The biggest a client sends in play is a voice packet,
    /// which is one unreliable datagram; LiteNetLib reassembles reliable messages of almost any size,
    /// and anything over this is dropped before it is deserialised.
    /// </summary>
    public const int MaxMessageBytes = 16 * 1024;
    /// <summary>Connections from one address (an IPv6 /64) at once. A household behind one router
    /// fits; a script opening hundreds does not.</summary>
    public const int MaxPeersPerAddress = 4;
    /// <summary>Connections at once, all addresses together.</summary>
    public const int MaxPeers = 200;

    private readonly ConcurrentDictionary<int, DateTime> _connectedAt = new();
    // New connections per address: a burst of 10, then one every 2 seconds.
    private readonly RateLimiter _connectLimiter = new(capacity: 10, refillPerSecond: 0.5);
    // One warning per address a minute, so a flood of refusals cannot flood the log as well.
    private readonly RateLimiter _logLimiter = new(capacity: 1, refillPerSecond: 1.0 / 60);
    
    /// <summary>
    /// Offered each message as it is received, during <see cref="PollEvents"/> on the loop thread; true
    /// means it has been handled and is not queued for the next tick. For voice, which would otherwise wait
    /// up to a whole tick (33 ms) on the server before being relayed, and arrive at every listener that
    /// much later and that much more unevenly.
    /// </summary>
    public Func<NetPeer, IMessage, bool>? HandleNow;

    public Action<NetPeer>? OnConnected;
    public Action<NetPeer, DisconnectInfo>? OnDisconnected;

    public void Start(int port)
    {
        _netManager = new NetManager(this) { AutoRecycle = true };
        _netManager.Start(port);
        Log.Information("NetworkService started on port {Port}", port);
    }

    public void PollEvents() => _netManager?.PollEvents();

    /// <summary>Closes the socket. Safe before Start and safe to call twice.</summary>
    public void Stop()
    {
        if (_netManager == null || !_netManager.IsRunning) return;
        _netManager.DisconnectAll();
        _netManager.Stop();
        Log.Information("NetworkService stopped.");
    }
    public bool TryDequeueMessage(out (NetPeer peer, IMessage message) item) => _incomingMessages.TryDequeue(out item);

    /// <summary>
    /// Sends what is queued now rather than at the library's next update, up to 15 ms away. For voice:
    /// held to the update, frames leave in bunches, and every listener's jitter buffer has to be that much
    /// longer to smooth them out again.
    /// </summary>
    public void Flush() => _netManager.TriggerUpdate();

    /// <summary>Sends one message; returns its size in bytes (0 if it could not be sent).</summary>
    public int SendMessage(NetPeer peer, IMessage message, DeliveryMethod deliveryMethod)
    {
        try
        {
            byte[] data = MemoryPackSerializer.Serialize<IMessage>(message);
            peer.Send(data, deliveryMethod);
            return data.Length;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FAILED to serialize/send message of type {Type} to peer {Id}", message.GetType().Name, peer.Id);
            return 0;
        }
    }

    /// <summary>
    /// Sends a world-state update, SPLIT into as many packets as it takes to fit.
    ///
    /// LiteNetLib does not fragment unreliable packets: past the peer's single-packet size it throws
    /// `TooBigPacketException` and the send does not happen. The broadcast loop catches, logs and
    /// moves on, so the entire tick's world state is simply LOST — every entity, for every client.
    ///
    /// Eight cars fitted in 1023 bytes and thirty do not, which is how raising the field turned this
    /// from dormant into constant: 47 dropped ticks in three minutes. A dropped tick freezes every
    /// entity until one gets through, and because the packet is only sometimes over the line — it
    /// depends how many cars are in range of that player — it is intermittent, and it lands on
    /// whichever cars happened to be moving. Heard exactly as reported: some of the cars, not all of
    /// them, stopping for about a second in front of you and then carrying on.
    ///
    /// The states go packed (<see cref="StatePacking"/>), and a tick that does not fit one packet is cut
    /// at state boundaries into packets filled as far as the peer allows. It used to be halved until each
    /// half fitted, which left packets anywhere between half full and full: more packets than the bytes
    /// needed, each with its own header. Every piece keeps the same Tick, so the client reassembles them
    /// into one snapshot.
    /// </summary>
    public void SendStateUpdate(NetPeer peer, ServerStateUpdate update, DeliveryMethod deliveryMethod)
    {
        try
        {
            int max = peer.GetMaxSinglePacketSize(deliveryMethod);
            foreach (var piece in Pieces(update, max))
            {
                byte[] data = MemoryPackSerializer.Serialize<IMessage>(piece);
                PerfProbe.Count("net.state-bytes", data.Length);
                PerfProbe.Count("net.state-packets");
                peer.Send(data, deliveryMethod);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FAILED to send a world state update of {Count} entities to peer {Id}", update.States.Count, peer.Id);
        }
    }

    private static readonly List<EntityState> NoStates = new();

    /// <summary>
    /// A tick's update as the packets it goes in, each no bigger than <paramref name="maxBytes"/>
    /// serialised. Public for the tests.
    /// </summary>
    public static List<ServerStateUpdate> Pieces(ServerStateUpdate update, int maxBytes)
    {
        var offsets = new List<int>();
        var packed = StatePacking.Pack(update.States, 0, update.States.Count, offsets);
        // What a piece costs besides its states: the message's own fields, and room for the length of
        // the packed array to grow by a few bytes.
        int budget = maxBytes - MemoryPackSerializer.Serialize<IMessage>(Piece(update, Array.Empty<byte>())).Length - 8;
        var pieces = new List<ServerStateUpdate>();
        int first = 0, count = update.States.Count;
        while (first < count)
        {
            int last = first + 1;
            while (last < count && offsets[last + 1] - offsets[first] <= budget) last++;
            if (offsets[last] - offsets[first] > budget)
            {
                // Only ever one state on its own: one entity that will not fit is not a splitting
                // problem. Say so rather than loop for ever.
                Log.Warning("A single entity state is larger than the packet limit ({Max} bytes); dropped.", maxBytes);
                first = last;
                continue;
            }
            pieces.Add(Piece(update, packed.AsSpan(offsets[first], offsets[last] - offsets[first]).ToArray()));
            first = last;
        }
        // A tick with no states still says what the client is riding in and which input it has had.
        if (count == 0) pieces.Add(Piece(update, Array.Empty<byte>()));
        return pieces;
    }

    /// <summary>
    /// One packet of a tick: everything the client is told about ITSELF, and some of the states,
    /// packed.
    ///
    /// Each piece is a whole update, not just the states. The pieces used to be rebuilt from three
    /// fields, so RidingEntityId arrived as its default of -1 in every split update: on a map big enough
    /// to split every tick, which the city is, a player in a driving seat was told every tick that they
    /// were standing in the road. They heard their own footsteps, their own car from outside, no cabin
    /// and no lane lines, and the client walked their ears away from the seat.
    /// </summary>
    public static ServerStateUpdate Piece(ServerStateUpdate whole, byte[] packed) => new()
    {
        Tick = whole.Tick,
        LastProcessedSequenceId = whole.LastProcessedSequenceId,
        States = NoStates,
        RidingEntityId = whole.RidingEntityId,
        RidingControls = whole.RidingControls,
        Packed = packed,
    };

    public NetPeer? GetPeer(int id) => _netManager?.GetPeerById(id);

    /// <summary>Closes a connection from this end. The usual disconnect event follows.</summary>
    public void Disconnect(int id)
    {
        if (GetPeer(id) is { } peer) _netManager.DisconnectPeer(peer);
    }

    /// <summary>One-way latency in milliseconds, or null if there is no such connection.</summary>
    public int? PingOf(int id) => GetPeer(id)?.Ping;

    /// <summary>When a connection was accepted, or null if it is not open.</summary>
    public DateTime? ConnectedSince(int id) => _connectedAt.TryGetValue(id, out var at) ? at : null;

    /// <summary>Every open connection and when it was accepted.</summary>
    public IEnumerable<(int Id, DateTime SinceUtc)> Connections() => _connectedAt.Select(kv => (kv.Key, kv.Value));

    public void OnPeerConnected(NetPeer peer)
    {
        _connectedAt[peer.Id] = DateTime.UtcNow;
        OnConnected?.Invoke(peer);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        _connectedAt.TryRemove(peer.Id, out _);
        OnDisconnected?.Invoke(peer, info);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        if (reader.AvailableBytes > MaxMessageBytes)
        {
            if (_logLimiter.TryConsume("big:" + RateLimiter.AddressKey(peer.Address)))
                Log.Warning("Dropped a {Bytes}-byte message from peer {Id} ({EndPoint}): over the {Max}-byte limit.",
                    reader.AvailableBytes, peer.Id, peer.Address, MaxMessageBytes);
            return;
        }
        try
        {
            var msg = MemoryPackSerializer.Deserialize<IMessage>(reader.GetRemainingBytes());
            if (msg != null && HandleNow?.Invoke(peer, msg) != true) _incomingMessages.Enqueue((peer, msg));
        }
        catch (Exception ex)
        {
            if (_logLimiter.TryConsume("bad:" + RateLimiter.AddressKey(peer.Address)))
                Log.Warning("Malformed packet from peer {Id} ({EndPoint}) discarded: {Error}",
                    peer.Id, peer.Address, ex.Message);
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) => Log.Error("Network Error {Error} on {EndPoint}", socketError, endPoint);
    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    /// <summary>
    /// Accepts a connection unless its address is connecting too often, already has
    /// <see cref="MaxPeersPerAddress"/> open, or the server is full. Every connection is a peer
    /// LiteNetLib keeps state for, logged in or not.
    /// </summary>
    public void OnConnectionRequest(ConnectionRequest request)
    {
        string key = RateLimiter.AddressKey(request.RemoteEndPoint.Address);
        int fromThere = 0;
        foreach (var peer in _netManager.ConnectedPeerList)
            if (RateLimiter.AddressKey(peer.Address) == key) fromThere++;

        string? refusal = _connectLimiter.TryConsume(key)
            ? Refusal(_netManager.ConnectedPeersCount, fromThere)
            : "connecting too often";
        if (refusal == null) { request.Accept(); return; }

        request.Reject();
        if (_logLimiter.TryConsume("refused:" + key))
            Log.Warning("Refused a connection from {Address}: {Reason}.", request.RemoteEndPoint, refusal);
    }

    /// <summary>Why a new connection is refused given how many are open, or null to accept it.</summary>
    public static string? Refusal(int connectedNow, int fromThisAddress)
    {
        if (connectedNow >= MaxPeers) return $"the server has {MaxPeers} connections";
        if (fromThisAddress >= MaxPeersPerAddress) return $"{MaxPeersPerAddress} connections are already open from there";
        return null;
    }
}
