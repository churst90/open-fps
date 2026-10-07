using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Services;

/// <summary>The friends list, with who of them is on. Adding and removing are /friend in
/// <see cref="CommandHandler"/>.</summary>
public class SocialService
{
    private readonly SessionManager _sessions;
    private readonly FriendRepository _friends;

    public SocialService(IMessageDispatcher dispatcher, SessionManager sessions, FriendRepository friends)
    {
        _sessions = sessions;
        _friends = friends;
        dispatcher.RegisterHandler<FriendListRequest>(HandleFriendListRequest);
    }

    private void HandleFriendListRequest(int connectionId, FriendListRequest request, Action<IMessage> reply)
    {
        if (!_sessions.TryGetSession(connectionId, out var session)) return;
        reply(BuildFriendList(session.Username, _friends, _sessions));
    }

    /// <summary>A user's friends, with an Online flag for each, parallel and in the order they were added.</summary>
    public static FriendListResponse BuildFriendList(string username, FriendRepository friends, SessionManager sessions)
    {
        var names = friends.GetFriends(username);
        var online = sessions.GetAllSessions().Select(s => s.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new FriendListResponse
        {
            Friends = names,
            Online = names.Select(online.Contains).ToArray(),
        };
    }
}
