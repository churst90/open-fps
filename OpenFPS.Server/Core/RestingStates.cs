using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenFPS.Common.Networking;

namespace OpenFPS.Server.Core;

/// <summary>
/// What one client was last sent about one moving thing, so the same thing is not sent again.
/// </summary>
public sealed class SentState
{
    public QuantizedTransform Transform;
    /// <summary>As the wire carries it (<see cref="StatePacking.WireVelocity"/>).</summary>
    public Vector3 Velocity;
    public byte TyreDemand;
    /// <summary>A copy: the vehicle systems refill one array in place every tick.</summary>
    public WheelState[]? Wheels;
    /// <summary>The tick the state (or the wheels) last differed from the one before it.</summary>
    public long ChangedAt, WheelsChangedAt;
}

/// <summary>
/// Leaves out of a client's tick the things that have not moved.
///
/// Everything that can move was sent to everyone every tick, moving or not. On the city that is about
/// 480 states a tick per player, and two thirds of them are the same as the tick before: parked cars,
/// people waiting at a crossing, a bus at its stop, the panels of every parked car. Sending a state that
/// has not changed tells the client nothing it does not already hold, so it is now sent:
/// <list type="bullet">
/// <item>every tick it differs from the last one this client was sent, in anything the wire carries;</item>
/// <item>on the <see cref="RepeatTicks"/> ticks after it stops changing, so that losing one packet (the
/// stream is unreliable) cannot leave a car parked a few centimetres from where it stopped, or a person
/// still walking. The client ends up holding the exact resting state three times over;</item>
/// <item>once every <see cref="KeepAliveTicks"/> (a second) while it rests, which heals anything worse;</item>
/// <item>always when the client has just been told what the thing is, and always for the client's own
/// body, which the client reconciles its prediction against every tick.</item>
/// </list>
/// The moment a resting thing moves it differs, so it goes that tick: nothing waits for a keep-alive to
/// start moving. The client holds the last state it had for anything it is not sent
/// (ClientWorldState.UpdateInterpolation).
///
/// The wheels follow the same rule on their own: a car moving at a steady speed has the same wheels
/// tick after tick, and a state without wheels means "as before" to the client.
/// </summary>
public static class RestingStates
{
    /// <summary>Ticks after its last change that a state is sent again unchanged.</summary>
    public const int RepeatTicks = 2;

    /// <summary>How often a resting thing is sent anyway: once a second at the 30 Hz tick. Spread over
    /// the second by entity id, so the keep-alives do not all land in the same packet.</summary>
    public const int KeepAliveTicks = 30;

    /// <summary>
    /// Whether to send <paramref name="state"/> to the client whose record is <paramref name="sent"/>,
    /// and records it if so. Clears <see cref="EntityState.Wheels"/> when the wheels need not go.
    /// <paramref name="force"/> sends it whatever it holds: a definition just went, or it is the
    /// client's own body.
    /// </summary>
    public static bool ShouldSend(Dictionary<int, SentState> sent, ref EntityState state, long tick, bool force)
    {
        int id = state.EntityId;
        var velocity = StatePacking.WireVelocity(state.LinearVelocity);
        bool fresh = !sent.TryGetValue(id, out var last);
        if (fresh) sent[id] = last = new SentState();

        if (fresh || !Same(last!.Transform, state.Transform) || last.Velocity != velocity || last.TyreDemand != state.TyreDemand)
        {
            last!.Transform = state.Transform;
            last.Velocity = velocity;
            last.TyreDemand = state.TyreDemand;
            last.ChangedAt = tick;
        }
        if (state.Wheels != null && (last.Wheels == null || !MemoryMarshal.AsBytes(last.Wheels.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(state.Wheels.AsSpan()))))
        {
            last.Wheels = (WheelState[])state.Wheels.Clone();
            last.WheelsChangedAt = tick;
        }

        bool keepAlive = (tick + id) % KeepAliveTicks == 0;
        bool wheels = state.Wheels != null && (fresh || force || keepAlive || tick - last.WheelsChangedAt <= RepeatTicks);
        if (!wheels) state.Wheels = null;
        return fresh || force || keepAlive || wheels || tick - last.ChangedAt <= RepeatTicks;
    }

    /// <summary>Field by field: the struct's own Equals boxes, five hundred times a tick a player.</summary>
    private static bool Same(in QuantizedTransform a, in QuantizedTransform b)
        => a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.QX == b.QX && a.QY == b.QY && a.QZ == b.QZ && a.QW == b.QW;
}
