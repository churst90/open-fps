using LiteNetLib;
using MemoryPack;
using OpenFPS.Common.Networking;
using Serilog;
using System.Net;
using System.Net.Sockets;

namespace OpenFPS.Client.Core;

public class ClientNetworkService : INetEventListener
{
    private NetManager _netManager = null!;
    private NetPeer? _serverPeer;
    private string _lastTarget = "";
    /// <summary>The server the live peer was connected to, as asked for ("host port N").</summary>
    private string _peerTarget = "", _pendingTarget = "";
    /// <summary>A peer being left for another server: its disconnect is not news.</summary>
    private NetPeer? _leaving;

    /// <summary>True from the moment a connect is started until the peer connects or the attempt ends.
    /// Without it, a second press of Connect during the handshake queues a second login.</summary>
    private volatile bool _connectPending;

    public event Action<IMessage>? OnMessageReceived;
    public event Action? OnConnected;

    /// <summary>
    /// A connection could not be made. The argument is a finished sentence the head speaks as it is. A
    /// player who cannot see the screen has no other sign of a dead connection, so every failure path
    /// must end in something said.
    /// </summary>
    public event Action<string>? OnConnectionFailed;

    /// <summary>A packet that does not decode: a version mismatch or a damaged packet. The argument is a
    /// speakable sentence.</summary>
    public event Action<string>? OnProtocolError;

    /// <summary>Something the player must hear that is not a failure, such as a second Connect while one
    /// is in flight. Separate from <see cref="OnConnectionFailed"/> so it is not announced as an error.</summary>
    public event Action<string>? OnConnectionNotice;

    /// <summary>A connection that was up has gone. The argument is a speakable reason. A connect that
    /// never succeeded raises <see cref="OnConnectionFailed"/> instead.</summary>
    public event Action<string>? OnConnectionLost;

    public bool IsConnected => _serverPeer != null;

    /// <summary>True while a connect has been started and has neither succeeded nor failed.</summary>
    public bool IsConnecting => _connectPending;

    /// <summary>True once <see cref="Start"/> has opened a socket.</summary>
    public bool IsStarted => _netManager is { IsRunning: true };

    /// <summary>Drops the connection to the server, if there is one. The peer's own disconnect event
    /// follows, as <see cref="OnConnectionLost"/>.</summary>
    public void Disconnect()
    {
        if (_netManager == null) return;
        _connectPending = false;
        _netManager.DisconnectAll();
    }

    public void Start()
    {
        if (IsStarted) return;
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

    /// <summary>
    /// Connects, or re-runs the connected handshake when a peer to that server is already up.
    /// LiteNetLib's <c>NetManager.Connect</c> returns the existing peer then and fires no
    /// <c>OnPeerConnected</c>: a retry after a wrong password sent no second login and the player was
    /// dropped at the menu with nothing said. So <see cref="OnConnected"/> is raised here.
    /// </summary>
    public void Connect(string ip, int port)
    {
        _lastTarget = $"{ip} port {port}";

        var existing = _serverPeer;
        if (existing != null && existing.ConnectionState == ConnectionState.Connected)
        {
            if (_peerTarget == _lastTarget)
            {
                Log.Information("Already connected to {Target}; re-running the connected handshake.", _lastTarget);
                OnConnected?.Invoke();
                return;
            }
            // A different server: leave the old peer first, or the login goes to the server a rejected
            // login left open ("your build is X" from a server never asked; Cody, 2026-10-02).
            Log.Information("Leaving {Old} for {New}.", _peerTarget, _lastTarget);
            _leaving = existing;
            _serverPeer = null;
            existing.Disconnect();
        }

        if (_connectPending)
        {
            Log.Information("Connect to {Target} is already in flight; ignoring the repeat.", _lastTarget);
            OnConnectionNotice?.Invoke($"Still connecting to {_lastTarget}. Please wait.");
            return;
        }

        Log.Information("Connecting to {Target}.", _lastTarget);
        try
        {
            _connectPending = true;
            _pendingTarget = _lastTarget;
            _netManager.Connect(Resolve(ip), port, "OpenFPS_Key");
        }
        catch (Exception ex)
        {
            _connectPending = false;
            Log.Error(ex, "Connect to {Target} could not be started.", _lastTarget);
            OnConnectionFailed?.Invoke($"Could not connect to {_lastTarget}. {ex.Message}");
        }
    }

    /// <summary>
    /// A host name as its IPv4 address when it has one. LiteNetLib prefers IPv6, and codyhurst.com over
    /// IPv6 never answered from a machine whose IPv6 pinged it fine; over IPv4 the login was accepted
    /// (2026-10-03). An address is used as it is.
    /// </summary>
    internal static string Resolve(string host)
    {
        if (IPAddress.TryParse(host, out _)) return host;
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            var v4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
            return (v4 ?? (addresses.Length > 0 ? addresses[0] : null))?.ToString() ?? host;
        }
        catch (SocketException ex)
        {
            Log.Warning("Could not look up {Host}: {Error}", host, ex.Message);
            return host;
        }
    }

    /// <summary>Delivers network events on the calling thread. Does nothing before <see cref="Start"/>.</summary>
    public void Poll() => _netManager?.PollEvents();

    /// <summary>Sends what is queued now rather than at the library's next update, up to 15 ms away: for
    /// voice, whose frames would otherwise leave in bunches. Safe from any thread.</summary>
    public void Flush() => _netManager?.TriggerUpdate();

    /// <summary>Every message as it is sent, connected or not. Tests watch it; nothing else should.</summary>
    internal Action<IMessage>? Sending;

    public void Send(IMessage message, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered)
    {
        Sending?.Invoke(message);
        var peer = _serverPeer;
        if (peer == null)
        {
            // Never drop silently: a key that did nothing must at least leave a line in the log.
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
        _connectPending = false;
        _serverPeer = peer;
        _peerTarget = _pendingTarget;
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

    // Protocol errors can arrive at packet rate: the first is reported at once, then one per interval.
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
        if (ReferenceEquals(peer, _leaving))
        {
            _leaving = null;
            Log.Information("Left {Address}.", peer.Address);
            return;
        }
        if (_serverPeer != null && !ReferenceEquals(peer, _serverPeer)) return;
        bool wasConnected = _serverPeer != null;
        _connectPending = false;
        _serverPeer = null;

        string reason = DescribeDisconnect(info);
        Log.Warning("Disconnected from {Target}: {Reason} (LiteNetLib reason {Raw}, socket {Socket}).",
            _lastTarget.Length > 0 ? _lastTarget : peer.Address.ToString(), reason, info.Reason, info.SocketErrorCode);

        if (wasConnected) OnConnectionLost?.Invoke(reason);
        else OnConnectionFailed?.Invoke($"Could not connect to {_lastTarget}. {reason}");
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
