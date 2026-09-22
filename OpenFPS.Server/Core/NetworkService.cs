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

    public void SendMessage(NetPeer peer, IMessage message, DeliveryMethod deliveryMethod)
    {
        try
        {
            byte[] data = MemoryPackSerializer.Serialize<IMessage>(message);
            peer.Send(data, deliveryMethod);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FAILED to serialize/send message of type {Type} to peer {Id}", message.GetType().Name, peer.Id);
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
    /// Split by halving rather than by a size constant per entity: the serializer decides how big a
    /// state is, the peer decides how big a packet may be, and neither of those is ours to predict.
    /// Every chunk keeps the same Tick, so the client reassembles them into one snapshot.
    /// </summary>
    public void SendStateUpdate(NetPeer peer, ServerStateUpdate update, DeliveryMethod deliveryMethod)
    {
        try
        {
            byte[] data = MemoryPackSerializer.Serialize<IMessage>(update);
            if (data.Length <= peer.GetMaxSinglePacketSize(deliveryMethod))
            {
                peer.Send(data, deliveryMethod);
                return;
            }
            if (update.States.Count <= 1)
            {
                // One entity that will not fit is not a splitting problem; say so rather than
                // recursing for ever.
                Log.Warning("A single entity state is larger than the peer's packet limit ({Max} bytes); dropped.",
                            peer.GetMaxSinglePacketSize(deliveryMethod));
                return;
            }
            // Each half is a whole update, with everything the client is told about ITSELF — not just
            // the entity states. The halves used to be rebuilt from three fields, so RidingEntityId
            // arrived as its default of -1 in every split update: on a map big enough to split every
            // tick, which the city is, a player in a driving seat was told every tick that they were
            // standing in the road. They heard their own footsteps, their own car from outside, no
            // cabin and no lane lines, and the client walked their ears away from the seat.
            int half = update.States.Count / 2;
            SendStateUpdate(peer, Half(update, 0, half), deliveryMethod);
            SendStateUpdate(peer, Half(update, half, update.States.Count - half), deliveryMethod);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FAILED to send a world state update of {Count} entities to peer {Id}", update.States.Count, peer.Id);
        }
    }

    public static ServerStateUpdate Half(ServerStateUpdate whole, int from, int count) => new()
    {
        Tick = whole.Tick,
        LastProcessedSequenceId = whole.LastProcessedSequenceId,
        States = whole.States.GetRange(from, count),
        RidingEntityId = whole.RidingEntityId,
        RidingControls = whole.RidingControls,
    };

    public void BroadcastToMap(IEnumerable<NetPeer?> peers, IMessage message)
    {
        try
        {
            byte[] data = MemoryPackSerializer.Serialize<IMessage>(message);
            foreach (var peer in peers) peer?.Send(data, DeliveryMethod.Unreliable);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FAILED to broadcast message of type {Type}", message.GetType().Name);
        }
    }

    public NetPeer? GetPeer(int id) => _netManager?.GetPeerById(id);

    public void OnPeerConnected(NetPeer peer) => OnConnected?.Invoke(peer);
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info) => OnDisconnected?.Invoke(peer, info);
    
    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        try
        {
            var msg = MemoryPackSerializer.Deserialize<IMessage>(reader.GetRemainingBytes());
            if (msg != null) _incomingMessages.Enqueue((peer, msg));
        }
        catch (Exception ex)
        {
            Log.Warning("Malformed packet from peer {Id} ({EndPoint}) discarded: {Error}",
                peer.Id, peer.Address, ex.Message);
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) => Log.Error("Network Error {Error} on {EndPoint}", socketError, endPoint);
    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnConnectionRequest(ConnectionRequest request) => request.Accept();
}
