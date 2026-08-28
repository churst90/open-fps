using LiteNetLib;
using LiteNetLib.Utils;
using MemoryPack;
using OpenFPS.Common.Networking;
using Serilog;
using System.Net;
using System.Net.Sockets;
using System;

namespace OpenFPS.Client.Core;

public class ClientNetworkService : INetEventListener
{
    private NetManager _netManager = null!;
    private NetPeer? _serverPeer;
    private string _lastTarget = "";

    public event Action<IMessage>? OnMessageReceived;
    public event Action? OnConnected;

    /// <summary>
    /// Raised when the connection could not be established, or was lost after it was. The argument is a
    /// finished, speakable sentence — heads pass it straight to the screen reader.
    ///
    /// A blind player has no window title, no greyed-out button and no spinner to look at: if a connection
    /// dies silently the game simply stops responding with no explanation. Every one of these paths must
    /// end in something said out loud.
    /// </summary>
    public event Action<string>? OnConnectionFailed;

    /// <summary>Raised when a packet arrives that cannot be decoded as a message — a protocol mismatch
    /// between client and server, or a corrupt packet. Argument is a speakable sentence.</summary>
    public event Action<string>? OnProtocolError;

    /// <summary>True while a server peer is connected.</summary>
    public bool IsConnected => _serverPeer != null;

    public void Start()
    {
        _netManager = new NetManager(this) { AutoRecycle = true };
        if (!_netManager.Start())
        {
            const string msg = "Could not open a network socket. Another program may be using the port.";
            Log.Error("NetManager.Start() failed — no UDP socket. The client cannot connect.");
            OnConnectionFailed?.Invoke(msg);
            return;
        }
        Log.Information("Client network started on local port {Port}.", _netManager.LocalPort);
    }

    public void Connect(string ip, int port)
    {
        _lastTarget = $"{ip} port {port}";
        Log.Information("Connecting to {Target}.", _lastTarget);
        try
        {
            _netManager.Connect(ip, port, "OpenFPS_Key");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Connect to {Target} could not be started.", _lastTarget);
            OnConnectionFailed?.Invoke($"Could not connect to {_lastTarget}. {ex.Message}");
        }
    }

    public void Poll() => _netManager.PollEvents();

    public void Send(IMessage message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
    {
        var peer = _serverPeer;
        if (peer == null)
        {
            // Dropping a message because there is no connection is exactly the kind of silent failure this
            // step exists to remove: the player pressed a key and nothing happened, with no trace anywhere.
            Log.Warning("Dropped outgoing {Type}: not connected to a server.", message.GetType().Name);
            return;
        }

        try
        {
            byte[] data = MemoryPackSerializer.Serialize<IMessage>(message);
            peer.Send(data, delivery);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to serialize/send {Type} to the server.", message.GetType().Name);
            OnProtocolError?.Invoke($"Failed to send a {message.GetType().Name} to the server.");
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        _serverPeer = peer;
        Log.Information("Connected to server {EndPoint}.", peer.Address);
        OnConnected?.Invoke();
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        try
        {
            var bytes = reader.GetRemainingBytes();
            var msg = MemoryPackSerializer.Deserialize<IMessage>(bytes);
            if (msg != null) { OnMessageReceived?.Invoke(msg); return; }

            // A null decode is a well-formed packet carrying a message this build does not know about.
            ReportProtocolError("The server sent a message this client does not understand. " +
                                "The client and server versions may not match.",
                                "Deserialized to null ({Bytes} bytes) — unknown message type.", bytes.Length);
        }
        catch (Exception ex)
        {
            ReportProtocolError("A damaged or incompatible packet arrived from the server.",
                                "Malformed packet discarded: {Error}", ex.Message);
        }
    }

    // Protocol errors can arrive at packet rate, so speak/log the FIRST one immediately and then at most
    // one per interval — loud enough to notice, quiet enough to stay usable.
    private long _lastProtocolReportTicks;
    private int _protocolErrorCount;
    private const long ProtocolReportIntervalMs = 5000;

    private void ReportProtocolError(string spoken, string logTemplate, params object[] logArgs)
    {
        _protocolErrorCount++;
        long now = Environment.TickCount64;
        if (_protocolErrorCount > 1 && now - _lastProtocolReportTicks < ProtocolReportIntervalMs) return;
        _lastProtocolReportTicks = now;

        Log.Warning("Protocol error #{Count}: " + logTemplate, PrependCount(_protocolErrorCount, logArgs));
        OnProtocolError?.Invoke(spoken);
    }

    private static object[] PrependCount(int count, object[] rest)
    {
        var all = new object[rest.Length + 1];
        all[0] = count;
        Array.Copy(rest, 0, all, 1, rest.Length);
        return all;
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        bool wasConnected = _serverPeer != null;
        _serverPeer = null;

        string reason = DescribeDisconnect(info);
        Log.Warning("Disconnected from {Target}: {Reason} (LiteNetLib reason {Raw}, socket {Socket}).",
            _lastTarget.Length > 0 ? _lastTarget : peer.Address.ToString(), reason, info.Reason, info.SocketErrorCode);

        OnConnectionFailed?.Invoke(wasConnected
            ? $"Disconnected from the server. {reason}"
            : $"Could not connect to {_lastTarget}. {reason}");
    }

    /// <summary>Turns LiteNetLib's disconnect enum into something worth hearing.</summary>
    private static string DescribeDisconnect(DisconnectInfo info) => info.Reason switch
    {
        DisconnectReason.ConnectionFailed => "The server did not answer. Check the address and that the server is running.",
        DisconnectReason.Timeout => "The connection timed out.",
        DisconnectReason.HostUnreachable => "The host could not be reached.",
        DisconnectReason.NetworkUnreachable => "The network is unreachable.",
        DisconnectReason.RemoteConnectionClose => "The server closed the connection.",
        DisconnectReason.DisconnectPeerCalled => "You disconnected.",
        DisconnectReason.ConnectionRejected => "The server rejected the connection.",
        DisconnectReason.InvalidProtocol => "The server is running an incompatible protocol version.",
        DisconnectReason.UnknownHost => "That host name could not be resolved.",
        DisconnectReason.Reconnect => "The server saw a new connection from this machine.",
        DisconnectReason.PeerToPeerConnection => "Peer to peer connection ended.",
        _ => $"Reason: {info.Reason}.",
    };

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        Log.Error("Socket error {Error} talking to {EndPoint}.", socketError, endPoint);
        OnConnectionFailed?.Invoke($"Network error: {socketError}.");
    }

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnConnectionRequest(ConnectionRequest request) { }
}
