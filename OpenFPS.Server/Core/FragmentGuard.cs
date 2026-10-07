using System.Net;
using LiteNetLib.Layers;

namespace OpenFPS.Server.Core;

public sealed class FragmentGuard : PacketLayerBase
{
    public const int MaxFragments = 64;
    public const int MaxOpenGroups = 8;

    public FragmentGuard() : base(0) { }

    public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length) { }

    public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length) { }

    public void Forget(IPEndPoint endPoint) { }
}
