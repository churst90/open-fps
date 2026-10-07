using System.Net;
using LiteNetLib.Layers;

namespace OpenFPS.Server.Core;

/// <summary>
/// Drops fragmented packets LiteNetLib would hold without limit, before it sees them.
/// </summary>
// LiteNetLib 1.2.0 (NetPeer.AddReliablePacket) allocates NetPacket[FragmentsTotal] for each new fragment id
// and keeps it until the message completes or the peer goes: one 1.4 KB packet claiming 65,535 parts held
// 512 KB, with 65,536 ids per peer, for anybody connected, before login. Header layout from NetPacket.cs.
public sealed class FragmentGuard : PacketLayerBase
{
    /// <summary>The most parts one message may come in: 16 KB (the largest message read) at the smallest MTU is 33.</summary>
    public const int MaxFragments = 64;
    /// <summary>Messages one sender may have half arrived at once.</summary>
    public const int MaxOpenGroups = 8;
    /// <summary>Senders tracked at once; past it, a new sender's fragments are dropped until one is forgotten.</summary>
    public const int MaxSenders = 4096;
    private const long ForgetAfterMs = 10 * 60_000;

    private const byte Channeled = 1, Merged = 12;
    private const int ChanneledHeader = 4, FragmentHeader = 6;

    private sealed class Sender
    {
        public readonly Dictionary<ushort, (ushort Total, ulong Parts)> Open = new();
        public long SeenMs;
    }

    private readonly Dictionary<IPEndPoint, Sender> _senders = new();
    private readonly Func<long> _nowMs;

    public FragmentGuard(Func<long>? nowMs = null) : base(0) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
    {
        if (length < 1) return;
        byte property = (byte)(data[0] & 0x1F);
        if (property != Merged && (property != Channeled || (data[0] & 0x80) == 0)) return;
        lock (_senders)
            if (!Allowed(endPoint, data, 0, length, depth: 0)) length = 0;
    }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length) { }

    /// <summary>Forgets a sender's half-arrived messages: its connection has closed.</summary>
    public void Forget(IPEndPoint? endPoint)
    {
        if (endPoint == null) return;
        lock (_senders) _senders.Remove(endPoint);
    }

    private bool Allowed(IPEndPoint from, byte[] data, int start, int length, int depth)
    {
        byte property = (byte)(data[start] & 0x1F);
        if (property == Merged)
        {
            if (depth > 0) return false;
            int pos = start + 1, end = start + length;
            while (pos < end)
            {
                if (end - pos < 2) return false;
                int size = BitConverter.ToUInt16(data, pos);
                pos += 2;
                // LiteNetLib checks a part against the buffer, not the datagram: a part longer than what
                // arrived would be read from an earlier packet's bytes.
                if (size == 0 || size > end - pos) return false;
                if (!Allowed(from, data, pos, size, depth + 1)) return false;
                pos += size;
            }
            return true;
        }
        if (property != Channeled || (data[start] & 0x80) == 0) return true;
        if (length < ChanneledHeader + FragmentHeader) return false;
        ushort id = BitConverter.ToUInt16(data, start + 4);
        ushort part = BitConverter.ToUInt16(data, start + 6);
        ushort total = BitConverter.ToUInt16(data, start + 8);
        if (total == 0 || total > MaxFragments || part >= total) return false;
        return Track(from, id, part, total);
    }

    private bool Track(IPEndPoint from, ushort id, ushort part, ushort total)
    {
        long now = _nowMs();
        if (!_senders.TryGetValue(from, out var sender))
        {
            if (_senders.Count >= MaxSenders) Prune(now);
            if (_senders.Count >= MaxSenders) return false;
            _senders[from] = sender = new Sender();
        }
        sender.SeenMs = now;
        if (!sender.Open.TryGetValue(id, out var group))
        {
            if (sender.Open.Count >= MaxOpenGroups) return false;
            group = (total, 0UL);
        }
        else if (group.Total != total) return false;
        group.Parts |= 1UL << part;
        // Every part seen: LiteNetLib has put the message together and let go of it. A resent part only
        // finishes early, after which its group is counted as new again.
        if (group.Parts == (total == 64 ? ulong.MaxValue : (1UL << total) - 1)) sender.Open.Remove(id);
        else sender.Open[id] = group;
        return true;
    }

    private void Prune(long now)
    {
        var stale = new List<IPEndPoint>();
        foreach (var (ep, s) in _senders) if (now - s.SeenMs > ForgetAfterMs) stale.Add(ep);
        foreach (var ep in stale) _senders.Remove(ep);
    }
}
