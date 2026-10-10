using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace OpenFPS.Common.Networking;

/// <summary>
/// The per-tick entity states as they go over the wire: packed by hand to about half MemoryPack's size
/// (about fifty bytes a walker at full width, five hundred walkers a tick on the city), with nothing a
/// listener can hear thrown away.
///
/// Per state, in order:
/// <list type="bullet">
/// <item>the entity id, as a variable-length integer (two bytes for any id under 16,384);</item>
/// <item>one byte of flags: how the velocity is carried, and whether a tyre demand and wheels follow;</item>
/// <item>the position, three 32-bit millimetre counts;</item>
/// <item>the rotation as "smallest three", six bytes (below);</item>
/// <item>the velocity: nothing for none, three 16-bit millimetres a second below 32.7 m/s in every
/// axis, else three floats. A millimetre a second is a Doppler shift of three parts in a million, and
/// differentiated (a train's notch, a tyre's demand) a step of 0.03 m/s², under every threshold;</item>
/// <item>the tyre demand byte, when it is not zero;</item>
/// <item>the horn and siren byte (EntityState.Signals), when it is not zero;</item>
/// <item>how a far thing is changing (EntityState.SpeedRate and the turn), four 16-bit numbers, when any
/// is not zero (<see cref="DistantMotion"/>);</item>
/// <item>the wheels, a count and <see cref="WheelBytes"/> each, when they are sent.</item>
/// </list>
///
/// No wheels sent does not mean no wheels: the client keeps the last, and the server leaves them out
/// while they are the same.
/// </summary>
public static class StatePacking
{
    /// <summary>The fastest any axis of a velocity can be and still go as 16-bit millimetres a second.</summary>
    public const float MillimetreRange = short.MaxValue / 1000f;

    private const byte VelocityNone = 0, VelocityMillimetres = 1, VelocityFloats = 2, VelocityMask = 3;
    private const byte HasTyreDemand = 4, HasWheels = 8, HasSignals = 16, HasRates = 32;

    /// <summary>One wheel on the wire, as its bytes: the struct's own size, which grows when a field is
    /// appended to it (WheelState.Water made it ten).</summary>
    private static readonly int WheelBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<WheelState>();

    /// <summary>The largest one state can pack to: id, flags, position, rotation, float velocity,
    /// demand, signals, rates and a count. Wheels are on top of this.</summary>
    private const int MaxFixedBytes = 5 + 1 + 12 + 6 + 12 + 1 + 1 + 8 + 1;

    /// <summary>
    /// The velocity the client will be given, so the server compares what it sends with what it last
    /// sent as the client would. Rounded to the millimetre a second below <see cref="MillimetreRange"/>.
    /// </summary>
    public static Vector3 WireVelocity(Vector3 v)
    {
        switch (VelocityForm(v, out short x, out short y, out short z))
        {
            case VelocityNone: return Vector3.Zero;
            case VelocityMillimetres: return new Vector3(x / 1000f, y / 1000f, z / 1000f);
            default: return v;
        }
    }

    private static byte VelocityForm(Vector3 v, out short x, out short y, out short z)
    {
        x = y = z = 0;
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) return VelocityFloats;
        float mx = MathF.Round(v.X * 1000f), my = MathF.Round(v.Y * 1000f), mz = MathF.Round(v.Z * 1000f);
        if (mx == 0f && my == 0f && mz == 0f) return VelocityNone;
        if (MathF.Abs(mx) > short.MaxValue || MathF.Abs(my) > short.MaxValue || MathF.Abs(mz) > short.MaxValue)
            return VelocityFloats;
        x = (short)mx; y = (short)my; z = (short)mz;
        return VelocityMillimetres;
    }

    /// <summary>Packs states[from .. from+count), writing where each one starts into
    /// <paramref name="offsets"/> when given (one entry per state, then the end).</summary>
    public static byte[] Pack(List<EntityState> states, int from, int count, List<int>? offsets = null)
    {
        int bound = 0;
        for (int i = from; i < from + count; i++)
            bound += MaxFixedBytes + (states[i].Wheels?.Length ?? 0) * WheelBytes;
        var buffer = new byte[bound];
        int at = 0;
        offsets?.Clear();
        for (int i = from; i < from + count; i++)
        {
            offsets?.Add(at);
            at += Write(buffer.AsSpan(at), states[i]);
        }
        offsets?.Add(at);
        return at == buffer.Length ? buffer : buffer.AsSpan(0, at).ToArray();
    }

    /// <summary>Writes one state; returns its length.</summary>
    public static int Write(Span<byte> into, in EntityState s)
    {
        int at = WriteVarUInt(into, (uint)s.EntityId);
        int flagsAt = at++;
        byte flags = VelocityForm(s.LinearVelocity, out short vx, out short vy, out short vz);

        var t = s.Transform;
        BinaryPrimitives.WriteInt32LittleEndian(into[at..], t.X); at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into[at..], t.Y); at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into[at..], t.Z); at += 4;
        ulong rot = PackRotation(t.QX, t.QY, t.QZ, t.QW);
        for (int b = 0; b < 6; b++) into[at++] = (byte)(rot >> (8 * b));

        switch (flags)
        {
            case VelocityMillimetres:
                BinaryPrimitives.WriteInt16LittleEndian(into[at..], vx); at += 2;
                BinaryPrimitives.WriteInt16LittleEndian(into[at..], vy); at += 2;
                BinaryPrimitives.WriteInt16LittleEndian(into[at..], vz); at += 2;
                break;
            case VelocityFloats:
                BinaryPrimitives.WriteSingleLittleEndian(into[at..], s.LinearVelocity.X); at += 4;
                BinaryPrimitives.WriteSingleLittleEndian(into[at..], s.LinearVelocity.Y); at += 4;
                BinaryPrimitives.WriteSingleLittleEndian(into[at..], s.LinearVelocity.Z); at += 4;
                break;
        }
        if (s.TyreDemand != 0) { flags |= HasTyreDemand; into[at++] = s.TyreDemand; }
        if (s.Signals != 0) { flags |= HasSignals; into[at++] = s.Signals; }
        if (s.SpeedRate != 0 || s.TurnX != 0 || s.TurnY != 0 || s.TurnZ != 0)
        {
            flags |= HasRates;
            BinaryPrimitives.WriteInt16LittleEndian(into[at..], s.SpeedRate); at += 2;
            BinaryPrimitives.WriteInt16LittleEndian(into[at..], s.TurnX); at += 2;
            BinaryPrimitives.WriteInt16LittleEndian(into[at..], s.TurnY); at += 2;
            BinaryPrimitives.WriteInt16LittleEndian(into[at..], s.TurnZ); at += 2;
        }
        if (s.Wheels != null)
        {
            flags |= HasWheels;
            int n = Math.Min(s.Wheels.Length, byte.MaxValue);
            into[at++] = (byte)n;
            var bytes = MemoryMarshal.AsBytes(s.Wheels.AsSpan(0, n));
            bytes.CopyTo(into[at..]);
            at += bytes.Length;
        }
        into[flagsAt] = flags;
        return at;
    }

    /// <summary>Unpacks every state in <paramref name="data"/> onto the end of <paramref name="into"/>.</summary>
    public static void Unpack(ReadOnlySpan<byte> data, List<EntityState> into)
    {
        int at = 0;
        while (at < data.Length)
        {
            var s = new EntityState { EntityId = (int)ReadVarUInt(data, ref at) };
            byte flags = data[at++];
            var t = new QuantizedTransform
            {
                X = BinaryPrimitives.ReadInt32LittleEndian(data[at..]),
                Y = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]),
                Z = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 8)..]),
            };
            at += 12;
            ulong rot = 0;
            for (int b = 0; b < 6; b++) rot |= (ulong)data[at++] << (8 * b);
            UnpackRotation(rot, out t.QX, out t.QY, out t.QZ, out t.QW);
            s.Transform = t;

            switch (flags & VelocityMask)
            {
                case VelocityMillimetres:
                    s.LinearVelocity = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(data[at..]) / 1000f,
                                                   BinaryPrimitives.ReadInt16LittleEndian(data[(at + 2)..]) / 1000f,
                                                   BinaryPrimitives.ReadInt16LittleEndian(data[(at + 4)..]) / 1000f);
                    at += 6;
                    break;
                case VelocityFloats:
                    s.LinearVelocity = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(data[at..]),
                                                   BinaryPrimitives.ReadSingleLittleEndian(data[(at + 4)..]),
                                                   BinaryPrimitives.ReadSingleLittleEndian(data[(at + 8)..]));
                    at += 12;
                    break;
            }
            if ((flags & HasTyreDemand) != 0) s.TyreDemand = data[at++];
            if ((flags & HasSignals) != 0) s.Signals = data[at++];
            if ((flags & HasRates) != 0)
            {
                s.SpeedRate = BinaryPrimitives.ReadInt16LittleEndian(data[at..]);
                s.TurnX = BinaryPrimitives.ReadInt16LittleEndian(data[(at + 2)..]);
                s.TurnY = BinaryPrimitives.ReadInt16LittleEndian(data[(at + 4)..]);
                s.TurnZ = BinaryPrimitives.ReadInt16LittleEndian(data[(at + 6)..]);
                at += 8;
            }
            if ((flags & HasWheels) != 0)
            {
                int n = data[at++];
                var wheels = new WheelState[n];
                data.Slice(at, n * WheelBytes).CopyTo(MemoryMarshal.AsBytes(wheels.AsSpan()));
                at += n * WheelBytes;
                s.Wheels = wheels;
            }
            into.Add(s);
        }
    }

    // ── Rotation: smallest three ──────────────────────────────────────────────────────────────────
    //
    // The largest component is worked out from the other three, which are no bigger than 1/√2: two
    // bits say which was dropped and 15 bits each carry the rest, a step of 4.3e-5, under a hundredth
    // of a degree. The 48th bit keeps the sign, so the client gets back the q it was sent, not -q.

    private const float Root2 = 1.41421356f;
    private const float StepsPerUnit = 16383f;

    /// <summary>The 48 bits that carry a rotation given as the four 16-bit components.</summary>
    public static ulong PackRotation(short qx, short qy, short qz, short qw)
    {
        Span<float> q = stackalloc float[] { qx, qy, qz, qw };
        float norm = MathF.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        if (norm < 1f) { q[0] = q[1] = q[2] = 0f; q[3] = 1f; norm = 1f; }
        int largest = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(q[i]) > MathF.Abs(q[largest])) largest = i;
        float sign = q[largest] < 0f ? -1f : 1f;
        ulong bits = (ulong)largest << 45 | (sign < 0f ? 1UL << 47 : 0UL);
        int shift = 30;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            float c = q[i] * sign / norm;
            int v = (int)MathF.Round((c * Root2 + 1f) * StepsPerUnit);
            bits |= (ulong)Math.Clamp(v, 0, 2 * (int)StepsPerUnit) << shift;
            shift -= 15;
        }
        return bits;
    }

    /// <summary>The four 16-bit components back from <see cref="PackRotation"/>.</summary>
    public static void UnpackRotation(ulong bits, out short qx, out short qy, out short qz, out short qw)
    {
        int largest = (int)(bits >> 45) & 3;
        Span<float> q = stackalloc float[4];
        int shift = 30;
        float sum = 0f;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            float c = ((int)((bits >> shift) & 0x7FFF) / StepsPerUnit - 1f) / Root2;
            q[i] = c;
            sum += c * c;
            shift -= 15;
        }
        q[largest] = MathF.Sqrt(MathF.Max(0f, 1f - sum));
        if ((bits >> 47 & 1) != 0) for (int i = 0; i < 4; i++) q[i] = -q[i];
        qx = ToShort(q[0]); qy = ToShort(q[1]); qz = ToShort(q[2]); qw = ToShort(q[3]);
    }

    private static short ToShort(float c) => (short)Math.Clamp((int)MathF.Round(c * 32767f), -32767, 32767);

    private static int WriteVarUInt(Span<byte> into, uint v)
    {
        int n = 0;
        while (v >= 0x80) { into[n++] = (byte)(v | 0x80); v >>= 7; }
        into[n++] = (byte)v;
        return n;
    }

    private static uint ReadVarUInt(ReadOnlySpan<byte> data, ref int at)
    {
        uint v = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = data[at++];
            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
        }
        return v;
    }
}
