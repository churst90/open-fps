using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;

namespace OpenFPS.Server.Services;

/// <summary>Who is online and which maps there are: the player list and the map chooser.</summary>
public class DiscoveryService
{
    private readonly SessionManager _sessions;
    private readonly MapManager _maps;

    public DiscoveryService(IMessageDispatcher dispatcher, SessionManager sessions, MapManager maps)
    {
        _sessions = sessions;
        _maps = maps;
        dispatcher.RegisterHandler<PlayerListRequest>(HandlePlayerListRequest);
        dispatcher.RegisterHandler<MapListRequest>(HandleMapListRequest);
    }

    private void HandlePlayerListRequest(int connectionId, PlayerListRequest request, Action<IMessage> reply)
    {
        if (!_sessions.TryGetSession(connectionId, out var session)) return;

        IEnumerable<UserSession> targets;

        if (request.Scope == PlayerListScope.Map)
            targets = _sessions.GetSessionsInMap(session.CurrentMapId);
        else
            targets = _sessions.GetAllSessions();

        // Your own map first: where somebody is, is the reason for asking.
        var ordered = targets
            .OrderBy(s => s.CurrentMapId != session.CurrentMapId)
            .ThenBy(s => s.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        reply(new PlayerListResponse
        {
            Players = ordered.Select(s => Describe(s, session)).ToArray(),
            // The same people, in the same order, as names a menu can act on.
            Usernames = ordered.Select(s => s.Username).ToArray(),
        });
    }

    private static string Describe(UserSession player, UserSession asker)
    {
        if (player.ConnectionId == asker.ConnectionId) return $"{player.Username} (you), on {player.CurrentMapId}";
        if (player.CurrentMapId == asker.CurrentMapId) return $"{player.Username}, here on {player.CurrentMapId}";
        return $"{player.Username}, on {player.CurrentMapId}";
    }

    /// <summary>
    /// Whether a session may walk into a loaded map: it is public, or it is theirs, or they were
    /// invited (/map invite), or they may join private maps. The one rule for the chooser, /join and a
    /// teleport, so a map is never offered and then refused.
    /// </summary>
    public static bool CanEnter(MapManager maps, string mapId, UserSession session)
    {
        if (session.Can(OpenFPS.Server.Core.Permissions.JoinPrivate)) return true;
        if (!maps.TryGetMapData(mapId, out var data)) return true;
        if (data.IsPublic) return true;
        if ((data.OwnerId ?? "").Equals(session.Username, StringComparison.OrdinalIgnoreCase)) return true;
        return data.Invited.Any(n => n.Equals(session.Username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The loaded maps and how many are on each. Only loaded ones: a map file the server has
    /// not loaded is nowhere a player can go.</summary>
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
            if (request.Scope == MapListScope.Server && !CanEnter(_maps, id, session)) continue;

            maps.Add(new MapSummary
            {
                Id = id,
                Name = data?.Name ?? "",
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
