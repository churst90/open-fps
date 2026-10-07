using OpenFPS.Common.Networking;

namespace OpenFPS.Server.Core;

/// <summary>Routes a message from either gateway to the handler registered for its type.</summary>
public interface IMessageDispatcher
{
    /// <param name="handler">Called with the connection id, the message and a reply callback.</param>
    void RegisterHandler<T>(Action<int, T, Action<IMessage>> handler) where T : IMessage;

    /// <param name="replyAction">How a handler answers the sender.</param>
    void Dispatch(int connectionId, IMessage message, Action<IMessage> replyAction);
}
