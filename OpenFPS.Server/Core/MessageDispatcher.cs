using System;
using System.Collections.Generic;
using OpenFPS.Common.Networking;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// A centralized dispatcher that maps message types to their respective handling logic.
/// Implements the Mediator pattern to decouple network gateways from game services.
/// </summary>
public class MessageDispatcher : IMessageDispatcher
{
    private readonly Dictionary<Type, Action<int, IMessage, Action<IMessage>>> _handlers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _warnedUnhandled = new();
    private long _refusedBeforeLogin;

    /// <summary>
    /// Whether a connection has logged in. When set, a connection that has not hears nothing but a
    /// login or a registration; everything else is refused here, before any handler sees it, so no
    /// handler has to remember to check.
    /// </summary>
    public Func<int, bool>? IsAuthenticated { get; set; }

    /// <summary>How often each account may do each thing (docs/SERVER_SECURITY.md). Tests give it a clock.</summary>
    public MessageLimits Limits { get; set; } = new();

    /// <summary>What a connection's limits are counted under: its account once logged in, so that
    /// reconnecting does not refill them. Null, or a null answer, counts by connection.</summary>
    public Func<int, string?>? KeyOf { get; set; }

    /// <summary>
    /// Every message a client sends. Anything else arriving from the network is one of the server's own
    /// messages sent back at it, and is dropped before it is read (NetworkService.IsClientMessage).
    /// </summary>
    public static readonly IReadOnlyList<Type> SentByClients = new[]
    {
        typeof(ClientInputUpdate), typeof(ChatMessage), typeof(LoginRequest), typeof(TextCommand),
        typeof(InteractRequest), typeof(VoiceData), typeof(RegisterRequest), typeof(LogoutRequest),
        typeof(MapDataRequest), typeof(PlayerListRequest), typeof(FriendListRequest), typeof(MapListRequest),
        typeof(ScopedShot), typeof(InventoryRequest),
    };

    /// <summary>What a connection may send before it has logged in.</summary>
    public static readonly IReadOnlySet<Type> AllowedBeforeLogin = new HashSet<Type>
    {
        typeof(LoginRequest), typeof(RegisterRequest),
    };

    /// <summary>
    /// Refused messages that a person asked for, and so are answered. The rest — input, voice, the
    /// server's own message types sent back at it — are dropped without a word.
    /// </summary>
    private static readonly HashSet<Type> AnsweredWhenRefused = new()
    {
        typeof(TextCommand), typeof(ChatMessage), typeof(PlayerListRequest), typeof(FriendListRequest),
        typeof(MapListRequest),
    };

    /// <summary>Messages refused because their connection had not logged in.</summary>
    public long RefusedBeforeLogin => System.Threading.Interlocked.Read(ref _refusedBeforeLogin);

    /// <summary>
    /// Registers a handler for a specific message type. 
    /// Internally wraps the handler to allow type-safe dispatching.
    /// </summary>
    public void RegisterHandler<T>(Action<int, T, Action<IMessage>> handler) where T : IMessage
    {
        _handlers[typeof(T)] = (connectionId, msg, reply) => handler(connectionId, (T)msg, reply);
    }

    /// <summary>
    /// Dispatches the message to a registered handler. 
    /// If no handler is found, a warning is logged.
    /// </summary>
    public void Dispatch(int connectionId, IMessage message, Action<IMessage> replyAction)
    {
        // Phase 2: Sanity Gates & Anti-Cheat
        if (message is ClientInputUpdate input)
        {
            // Finite first. NaN is not greater than one, so the length check alone let it through to
            // the player's position; an infinite direction normalised to NaN; and Math.Clamp passes
            // NaN, which a NaN look would have put into the yaw for good. Not a number is no input.
            if (!IsFinite(input.MoveDirection)) input.MoveDirection = System.Numerics.Vector3.Zero;
            if (!float.IsFinite(input.LookDelta.X) || !float.IsFinite(input.LookDelta.Y))
                input.LookDelta = System.Numerics.Vector2.Zero;
            if (float.IsNaN(input.DeltaTime)) input.DeltaTime = 0.001f;

            if (input.MoveDirection.Length() > 1.0f)
            {
                input.MoveDirection = System.Numerics.Vector3.Normalize(input.MoveDirection);
            }
            input.DeltaTime = Math.Clamp(input.DeltaTime, 0.001f, 0.1f);
        }

        var type = message.GetType();
        if (IsAuthenticated != null && !AllowedBeforeLogin.Contains(type) && !IsAuthenticated(connectionId))
        {
            System.Threading.Interlocked.Increment(ref _refusedBeforeLogin);
            if (AnsweredWhenRefused.Contains(type))
                replyAction(new TextEvent { Text = "You are not logged in. Type login, your name and your password." });
            return;
        }

        if (_handlers.TryGetValue(type, out var handler))
        {
            try
            {
                handler(connectionId, message, replyAction);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling message of type {Type} for connection {ConnectionId}", type.Name, connectionId);
            }
        }
        else if (_warnedUnhandled.TryAdd(type, true))
        {
            // Once per type: a client sending the server's own messages back at it would otherwise
            // write a line to the log for every one.
            Log.Warning("No handler registered for message type {Type}", type.Name);
        }
    }

    private static bool IsFinite(System.Numerics.Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
