using System;
using System.Linq;
using System.Collections.Generic;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;

namespace OpenFPS.Server.Services;

/// <summary>
/// Service responsible for player discovery and information queries.
/// Handles requests for online player lists across different scopes (Server-wide or Map-local).
/// </summary>
public class DiscoveryService
{
    private readonly SessionManager _sessions;

    /// <summary>
    /// Initializes the service and registers message handlers with the dispatcher.
    /// </summary>
    /// <param name="dispatcher">The message dispatcher for routing requests.</param>
    /// <param name="sessions">The session manager for player data lookup.</param>
    public DiscoveryService(IMessageDispatcher dispatcher, SessionManager sessions)
    {
        _sessions = sessions;
        dispatcher.RegisterHandler<PlayerListRequest>(HandlePlayerListRequest);
    }

    /// <summary>
    /// Processes a request for the list of online players.
    /// </summary>
    /// <param name="connectionId">Source connection ID.</param>
    /// <param name="request">The request parameters (Scope).</param>
    /// <param name="reply">Callback to return the list response.</param>
    private void HandlePlayerListRequest(int connectionId, PlayerListRequest request, Action<IMessage> reply)
    {
        if (!_sessions.TryGetSession(connectionId, out var session)) return;

        IEnumerable<UserSession> targets;
        
        // Scope resolution: Should we show everyone, or just people in the requester's current map?
        if (request.Scope == PlayerListScope.Map)
            targets = _sessions.GetSessionsInMap(session.CurrentMapId);
        else
            targets = _sessions.GetAllSessions();

        var playerNames = targets.Select(s => s.Username).ToArray();
        
        reply(new PlayerListResponse { Players = playerNames });
    }
}
