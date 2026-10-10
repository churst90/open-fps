using System.Numerics;
using System.Runtime.InteropServices;
using OpenFPS.Common;
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
    /// <summary>The horn and siren byte (EntityState.Signals).</summary>
    public byte Signals;
    /// <summary>A copy: the vehicle systems refill one array in place every tick.</summary>
    public WheelState[]? Wheels;
    /// <summary>The tick the state (or the wheels) last differed from the one before it.</summary>
    public long ChangedAt, WheelsChangedAt;
    /// <summary>What this client was last told the thing is sitting in (EntityDefinition.RidingEntityId):
    /// when it differs, the definition goes again.</summary>
    public int Riding = -1;

    /// <summary>The velocity as the server had it at <see cref="SeenAt"/>, unrounded: the rates come from
    /// one tick to the next of the server's own numbers.</summary>
    public Vector3 RawVelocity;
    public long SeenAt = -1;

    // What the client was last sent, as the wire carries it, for a far thing's prediction (DistantMotion).
    /// <summary>The tick the client was last sent this state, or -1.</summary>
    public long ToldAt = -1;
    public Vector3 ToldPosition, ToldVelocity;
    public Quaternion ToldRotation = Quaternion.Identity;
    public DistantMotion.Rates ToldRates;
    public byte ToldDemand, ToldSignals;
    /// <summary>The wheels last sent, or null.</summary>
    public WheelState[]? ToldWheels;
    /// <summary>The last far state went for a change: it goes again next tick, so one lost or late packet
    /// cannot leave the client carrying the thing wrongly for a fifth of a second.</summary>
    public bool RepeatNext;
}

/// <summary>
/// Leaves out of a client's tick the things that have not moved, and sends far moving things less often.
/// On the city about 480 states a tick go to each player, and two thirds are the same as the tick before
/// (parked cars, people waiting at a crossing, a bus at its stop). A state is sent:
/// <list type="bullet">
/// <item>every tick it differs from the last one this client was sent, in anything the wire carries;</item>
/// <item>on the <see cref="RepeatTicks"/> ticks after it stops changing, so that one lost packet (the
/// stream is unreliable) cannot leave a car a few centimetres from where it stopped, or a person still
/// walking;</item>
/// <item>once every <see cref="KeepAliveTicks"/> (a second) while it rests, which heals anything worse;</item>
/// <item>always when the client has just been told what the thing is, and always for the client's own
/// body, which the client reconciles its prediction against every tick;</item>
/// <item>but a thing moving <see cref="DistantMotion.FullRateMetres"/> or more away only when the client's
/// prediction of it would stray, and at least every <see cref="DistantMotion.IntervalTicks"/>
/// (<see cref="DueFar"/>).</item>
/// </list>
/// A resting thing that moves goes that tick; the client holds the last state it had for anything it is
/// not sent, and carries a far moving one (ClientWorldState.UpdateInterpolation). The wheels follow the same
/// rule on their own: a state without wheels means "as before" to the client.
/// </summary>
public static class RestingStates
{
    /// <summary>Ticks after its last change that a state is sent again unchanged.</summary>
    public const int RepeatTicks = 2;

    /// <summary>How often a resting thing is sent anyway: once a second at the 30 Hz tick. Spread over
    /// the second by entity id, so the keep-alives do not all land in the same packet.</summary>
    public const int KeepAliveTicks = 30;

    /// <summary>Whether far moving things go less often (DistantMotion), for every client from now on.
    /// On since the acceptance test passed (DistantUpdatesTests).</summary>
    public static bool DistantLessOften = true;

    /// <summary>
    /// Whether to send <paramref name="state"/> to the client whose record is <paramref name="sent"/>,
    /// and records it if so. Clears <see cref="EntityState.Wheels"/> when the wheels need not go.
    /// <paramref name="force"/> sends it whatever it holds: a definition just went, or it is the
    /// client's own body. <paramref name="distance"/> is how far the thing is from the client's body, or zero
    /// for anything that must go every tick (what they ride, drive or carry). From
    /// <see cref="DistantMotion.RatesMetres"/> out a moving state carries its rates.
    /// </summary>
    public static bool ShouldSend(Dictionary<int, SentState> sent, ref EntityState state, long tick, bool force, float distance = 0f)
    {
        int id = state.EntityId;
        var velocity = StatePacking.WireVelocity(state.LinearVelocity);
        bool fresh = !sent.TryGetValue(id, out var last);
        if (fresh) sent[id] = last = new SentState();

        // How it is changing, from the last tick to this one, as the server has it.
        var rates = last!.SeenAt == tick - 1
            ? DistantMotion.RatesOf(last.RawVelocity, state.LinearVelocity, PhysicsConstants.FixedDeltaTime)
            : DistantMotion.Rates.None;
        last.RawVelocity = state.LinearVelocity;
        last.SeenAt = tick;
        if (velocity != Vector3.Zero && distance >= DistantMotion.RatesMetres) rates.WriteTo(ref state);

        if (fresh || !Same(last.Transform, state.Transform) || last.Velocity != velocity || last.TyreDemand != state.TyreDemand
            || last.Signals != state.Signals)
        {
            last.Transform = state.Transform;
            last.Velocity = velocity;
            last.TyreDemand = state.TyreDemand;
            last.Signals = state.Signals;
            last.ChangedAt = tick;
        }
        if (state.Wheels != null && (last.Wheels == null || !SameWheels(last.Wheels, state.Wheels)))
        {
            last.Wheels = (WheelState[])state.Wheels.Clone();
            last.WheelsChangedAt = tick;
        }

        bool keepAlive = (tick + id) % KeepAliveTicks == 0;
        bool send, wheels;
        if (!fresh && !force && !keepAlive && distance >= DistantMotion.FullRateMetres && velocity != Vector3.Zero)
        {
            send = DueFar(last, state, velocity, tick, distance, out bool changed);
            last.RepeatNext = changed;
            // With a far state the wheels go if they are not what the client has.
            wheels = send && state.Wheels != null && (last.ToldWheels == null || !SameWheels(last.ToldWheels, state.Wheels));
        }
        else
        {
            wheels = state.Wheels != null && (fresh || force || keepAlive || tick - last.WheelsChangedAt <= RepeatTicks);
            send = fresh || force || keepAlive || wheels || tick - last.ChangedAt <= RepeatTicks;
            last.RepeatNext = false;
        }
        if (!wheels) state.Wheels = null;
        if (send) Told(last, state, velocity, tick);
        return send;
    }

    /// <summary>
    /// Whether a far moving thing is due: <see cref="DistantMotion.IntervalTicks"/> since it last went, the
    /// tick after one that went for a change, or a change: its horn, its tyres audibly (the demand, or the
    /// surface or water under a wheel), or the client's prediction from what it was last sent has strayed
    /// (<see cref="DistantMotion.Strayed"/>). <paramref name="changed"/> says it went for a change, and so
    /// goes again next tick: the stream is unreliable, and a change lost or overtaken would otherwise be
    /// carried wrongly until the next state, where everything sent every tick is bridged by the tick after.
    /// </summary>
    private static bool DueFar(SentState last, in EntityState state, Vector3 velocity, long tick, float distance, out bool changed)
    {
        changed = false;
        if (last.ToldAt < 0) return true;
        var predicted = DistantMotion.Predict(last.ToldPosition, last.ToldVelocity, last.ToldRotation, last.ToldRates,
                                              (tick - last.ToldAt) * PhysicsConstants.FixedDeltaTime);
        var now = state.Transform.ToTransform();
        changed = state.Signals != last.ToldSignals
               || MathF.Abs(state.TyreDemandFraction - last.ToldDemand / 127.5f) > DistantMotion.DemandTolerance
               || state.Wheels != null && WheelsStrayed(last.ToldWheels, state.Wheels)
               || DistantMotion.Strayed(predicted, now.Position, velocity, now.Rotation, distance);
        return changed || last.RepeatNext || tick - last.ToldAt >= DistantMotion.IntervalTicks;
    }

    /// <summary>A wheel on another surface or in other water, or working its tyre by more than the tolerance.</summary>
    private static bool WheelsStrayed(WheelState[]? told, WheelState[] now)
    {
        if (told == null || told.Length != now.Length) return true;
        for (int i = 0; i < now.Length; i++)
            if (told[i].Surface != now[i].Surface || told[i].Water != now[i].Water
                || MathF.Abs(told[i].DemandFraction - now[i].DemandFraction) > DistantMotion.DemandTolerance)
                return true;
        return false;
    }

    private static bool SameWheels(WheelState[] a, WheelState[] b)
        => MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

    /// <summary>Records what the client has now been sent, as the client will read it.</summary>
    private static void Told(SentState last, in EntityState state, Vector3 velocity, long tick)
    {
        var now = state.Transform.ToTransform();
        last.ToldAt = tick;
        last.ToldPosition = now.Position;
        last.ToldVelocity = velocity;
        last.ToldRotation = now.Rotation;
        last.ToldRates = DistantMotion.Rates.Of(state);
        last.ToldDemand = state.TyreDemand;
        last.ToldSignals = state.Signals;
        if (state.Wheels != null) last.ToldWheels = (WheelState[])state.Wheels.Clone();
    }

    /// <summary>Field by field: the struct's own Equals boxes, five hundred times a tick a player.</summary>
    private static bool Same(in QuantizedTransform a, in QuantizedTransform b)
        => a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.QX == b.QX && a.QY == b.QY && a.QZ == b.QZ && a.QW == b.QW;
}
