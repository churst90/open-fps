using System;
using OpenFPS.Common.Networking;

namespace OpenFPS.Server.Core;

/// <summary>
/// Defines a contract for dispatching messages to their registered handlers.
/// This acts as a protocol-agnostic mediator between the network layer and game logic.
/// </summary>
public interface IMessageDispatcher
{
    /// <summary>
    /// Registers a handler for a specific message type.
    /// </summary>
    /// <typeparam name="T">The type of message to handle.</typeparam>
    /// <param name="handler">A callback invoked when a message of type T is received.</param>
    void RegisterHandler<T>(Action<int, T, Action<IMessage>> handler) where T : IMessage;

    /// <summary>
    /// Routes an incoming message to the appropriate registered handler.
    /// </summary>
    /// <param name="connectionId">The unique ID of the source connection.</param>
    /// <param name="message">The message object to process.</param>
    /// <param name="replyAction">A callback that allow handlers to send a response back to the sender.</param>
    void Dispatch(int connectionId, IMessage message, Action<IMessage> replyAction);
}
