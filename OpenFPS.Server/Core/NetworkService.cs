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
