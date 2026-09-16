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
    private readonly MapManager _maps;

    /// <summary>
    /// Initializes the service and registers message handlers with the dispatcher.
    /// </summary>
    /// <param name="dispatcher">The message dispatcher for routing requests.</param>
    /// <param name="sessions">The session manager for player data lookup.</param>
    public DiscoveryService(IMessageDispatcher dispatcher, SessionManager sessions, MapManager maps)
    {
        _sessions = sessions;
        _maps = maps;
        dispatcher.RegisterHandler<PlayerListRequest>(HandlePlayerListRequest);
        dispatcher.RegisterHandler<MapListRequest>(HandleMapListRequest);
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

        // A name on its own is not a status. Where somebody is, is the whole reason for asking:
        // a list of eight names tells you nothing, and "four of them are on the map you are on"
        // tells you where the game is.
        var playerNames = targets
            .OrderBy(s => s.CurrentMapId != session.CurrentMapId)
            .ThenBy(s => s.Username, StringComparer.OrdinalIgnoreCase)
            .Select(s => Describe(s, session))
            .ToArray();

        reply(new PlayerListResponse { Players = playerNames });
    }

    private static string Describe(UserSession player, UserSession asker)
    {
        if (player.ConnectionId == asker.ConnectionId) return $"{player.Username} (you), on {player.CurrentMapId}";
        if (player.CurrentMapId == asker.CurrentMapId) return $"{player.Username}, here on {player.CurrentMapId}";
        return $"{player.Username}, on {player.CurrentMapId}";
    }

    /// <summary>
    /// The maps this server has, and who is on them.
    ///
    /// Answered from the LOADED maps rather than from the map directory, because a map file the
    /// server has not loaded is not somewhere a player can go, and a chooser that offers places you
    /// cannot reach is worse than no chooser.
    /// </summary>
    private void HandleMapListRequest(int connectionId, MapListRequest request, Action<IMessage> reply)
    {
        if (!_sessions.TryGetSession(connectionId, out var session)) return;

        var population = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sessions.GetAllSessions())
            population[s.CurrentMapId] = population.GetValueOrDefault(s.CurrentMapId) + 1;

        var maps = new List<MapSummary>();
        foreach (string id in _maps.LoadedMapIds)
        {
            string owner = _maps.TryGetMapData(id, out var data) ? data.OwnerId ?? "" : "";
            bool isPublic = !_maps.TryGetMapData(id, out var d2) || d2.IsPublic;
            bool mine = owner.Equals(session.Username, StringComparison.OrdinalIgnoreCase);

            if (request.Scope == MapListScope.Mine && !mine) continue;
            if (request.Scope == MapListScope.Server && !isPublic && !mine) continue;

            maps.Add(new MapSummary
            {
                Id = id,
                OwnerId = owner,
                IsPublic = isPublic,
                PlayerCount = population.GetValueOrDefault(id),
                IsCurrent = id.Equals(session.CurrentMapId, StringComparison.OrdinalIgnoreCase),
            });
        }

        maps.Sort((a, b) => b.PlayerCount != a.PlayerCount
            ? b.PlayerCount.CompareTo(a.PlayerCount)
            : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));

        reply(new MapListResponse { Scope = request.Scope, Maps = maps.ToArray() });
    }
}
