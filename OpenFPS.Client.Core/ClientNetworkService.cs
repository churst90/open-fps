using LiteNetLib;
using LiteNetLib.Utils;
using MemoryPack;
using OpenFPS.Common.Networking;
using System.Net;
using System.Net.Sockets;
using System;

namespace OpenFPS.Client.Core;

public class ClientNetworkService : INetEventListener
{
    private NetManager _netManager = null!;
    private NetPeer? _serverPeer;
    
    public event Action<IMessage>? OnMessageReceived;
    public event Action? OnConnected;

    public void Start()
    {
        _netManager = new NetManager(this) { AutoRecycle = true };
        _netManager.Start();
    }

    public void Connect(string ip, int port)
    {
        _netManager.Connect(ip, port, "OpenFPS_Key");
    }

    public void Poll() => _netManager.PollEvents();

    public void Send(IMessage message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
    {
        if (_serverPeer == null) return;
        
        byte[] data = MemoryPackSerializer.Serialize<IMessage>(message);
        _serverPeer.Send(data, delivery);
    }

    public void OnPeerConnected(NetPeer peer)
    {
        _serverPeer = peer;
        OnConnected?.Invoke();
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        try {
            var bytes = reader.GetRemainingBytes();
            var msg = MemoryPackSerializer.Deserialize<IMessage>(bytes);
            if (msg != null) OnMessageReceived?.Invoke(msg);
        }
        catch (Exception)
        {
        }
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info) { _serverPeer = null; }
    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnConnectionRequest(ConnectionRequest request) { }
}
