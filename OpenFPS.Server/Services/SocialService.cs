using System;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;

namespace OpenFPS.Server.Services;

/// <summary>
/// Service responsible for social interactions, including friends lists, 
/// contact management, and block lists.
/// </summary>
public class SocialService
{
    /// <summary>
    /// Initializes the social service and registers handlers for friend-related messages.
    /// </summary>
    public SocialService(IMessageDispatcher dispatcher)
    {
        dispatcher.RegisterHandler<FriendListRequest>(HandleFriendListRequest);
    }

    /// <summary>
    /// Handles requests for the user's friend list.
    /// Currently uses placeholder data while the persistence layer for contacts is finalized.
    /// </summary>
    private void HandleFriendListRequest(int connectionId, FriendListRequest request, Action<IMessage> reply)
    {
        // TODO: Integrate with UserRepository to load real contacts from JSON
        reply(new FriendListResponse { Friends = new[] { "SystemAdmin", "TestFriend" } });
    }
}
