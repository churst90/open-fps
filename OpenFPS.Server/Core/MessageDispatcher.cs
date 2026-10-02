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
        else
        {
            Log.Warning("No handler registered for message type {Type}", type.Name);
        }
    }

    private static bool IsFinite(System.Numerics.Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
