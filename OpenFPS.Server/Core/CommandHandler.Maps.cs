using OpenFPS.Common.Networking;
using OpenFPS.Server.Services;

namespace OpenFPS.Server.Core;

/// <summary>
/// Maps of your own: /map new, public, private, invite, uninvite, and /maps. Not gated: making a map
/// and deciding who comes into it is every player's. On a map you own you build, spawn, move yourself
/// and save as staff do anywhere (<see cref="Permissions.OnOwnMap"/>).
/// </summary>
public partial class CommandHandler
{
    private const string MapUsage =
        "Usage: /map on its own says where you are; /map new NAME, /map public, /map private, "
        + "/map invite PLAYER, /map uninvite PLAYER. /maps lists the maps you can go to, /maps mine your own.";

    private void HandleMap(UserSession session, string[] args, Action<IMessage> reply)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        switch (sub)
        {
            case "":
            case "info":
                Say(reply, DescribeMap(session, session.CurrentMapId));
                return;

            case "new":
            case "create":
            {
                if (args.Length < 2) { Say(reply, "Usage: /map new NAME. A name is 2 to 24 lower-case letters, digits, '_' or '-', starting with a letter."); return; }
                string id = args[1].ToLowerInvariant();
                if (!MapTemplates.IsValidId(id))
                { Say(reply, "A map name is 2 to 24 lower-case letters, digits, '_' or '-', starting with a letter."); return; }
                var mine = _maps.OwnedBy(session.Username);
                if (mine.Count >= MapManager.MaxMapsPerOwner)
                { Say(reply, $"You have {mine.Count} maps, the most one person may have: {string.Join(", ", mine)}."); return; }
                int made = _maps.LoadedMapIds.Count(m => _maps.TryGetMapData(m, out var d) && !string.IsNullOrWhiteSpace(d.OwnerId));
                if (made >= MapManager.MaxPlayerMaps)
                { Say(reply, "This server has as many player maps as it can hold."); return; }
                if (!_maps.CreateMap(MapTemplates.Flat(id, session.Username), out string error))
                { Say(reply, $"Could not make the map: {error}."); return; }
                Say(reply, $"Made your map {id}: flat ground a hundred metres square, private, yours to build on. Travelling there.");
                _server.MoveToMap(session, id, reply);
                return;
            }

            case "public":
            case "private":
            {
                string mapId = args.Length > 1 ? args[1] : session.CurrentMapId;
                if (!TryManageMap(session, mapId, reply, out var data)) return;
                bool open = sub == "public";
                if (data.IsPublic == open) { Say(reply, $"{data.Id} is already {sub}."); return; }
                data.IsPublic = open;
                _maps.RecordAccess(data.Id);
                Say(reply, open ? $"{data.Id} is public: anybody may come in."
                                : $"{data.Id} is private: only you and the people you invite may come in.");
                return;
            }

            case "invite":
            case "uninvite":
            {
                if (args.Length < 2) { Say(reply, $"Usage: /map {sub} PLAYER"); return; }
                string mapId = args.Length > 2 ? args[2] : session.CurrentMapId;
                if (!TryManageMap(session, mapId, reply, out var data)) return;
                if (!TryFindUser(args[1], out var username, out _)) { Say(reply, $"There is no player called {args[1]}."); return; }
                bool invite = sub == "invite";
                int at = data.Invited.FindIndex(n => n.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (invite)
                {
                    if (username.Equals(data.OwnerId, StringComparison.OrdinalIgnoreCase)) { Say(reply, $"{data.Id} is {username}'s own map."); return; }
                    if (at >= 0) { Say(reply, $"{username} is already invited to {data.Id}."); return; }
                    data.Invited.Add(username);
                }
                else
                {
                    if (at < 0) { Say(reply, $"{username} is not invited to {data.Id}."); return; }
                    data.Invited.RemoveAt(at);
                }
                _maps.RecordAccess(data.Id);
                if (OnlineSession(username) is { } online && online != session)
                    _server.SendToSession(online, new TextEvent
                    {
                        Text = invite ? $"{session.Username} invited you to the map {data.Id}. /join {data.Id} goes there."
                                      : $"{session.Username} took back your invitation to the map {data.Id}.",
                    });
                Say(reply, invite ? $"{username} may come into {data.Id}." + (data.IsPublic ? " It is public, so anybody may anyway." : "")
                                  : $"{username} is no longer invited to {data.Id}.");
                return;
            }

            default:
                Say(reply, MapUsage);
                return;
        }
    }

    /// <summary>/maps: the maps you can go to. /maps mine: the ones you own, and who may come in.</summary>
    private void HandleMaps(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length > 0 && args[0].Equals("mine", StringComparison.OrdinalIgnoreCase))
        {
            var mine = _maps.OwnedBy(session.Username);
            if (mine.Count == 0) { Say(reply, "You have no maps. /map new NAME makes one."); return; }
            Say(reply, "Your maps: " + string.Join("; ", mine.Select(id =>
            {
                _maps.TryGetMapData(id, out var d);
                string who = d.Invited.Count == 0 ? "" : $", invited: {string.Join(", ", d.Invited)}";
                return $"{id}, {(d.IsPublic ? "public" : "private")}{who}";
            })) + ".");
            return;
        }
        var enterable = _maps.LoadedMapIds.Where(id => DiscoveryService.CanEnter(_maps, id, session))
                                          .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                                          .Select(id =>
                                          {
                                              _maps.TryGetMapData(id, out var d);
                                              string owner = string.IsNullOrWhiteSpace(d.OwnerId) ? ""
                                                  : d.OwnerId.Equals(session.Username, StringComparison.OrdinalIgnoreCase) ? ", yours" : $", {d.OwnerId}'s";
                                              return $"{id}{owner}{(d.IsPublic ? "" : ", private")}";
                                          });
        Say(reply, "Maps you can go to: " + string.Join("; ", enterable) + ". /join MAP goes there.");
    }

    /// <summary>"You are on cody-yard, your map, private. Invited: sean." What /map says on its own.</summary>
    private string DescribeMap(UserSession session, string mapId)
    {
        if (!_maps.TryGetMapData(mapId, out var d)) return $"Map '{mapId}' is not loaded.";
        bool mine = _maps.IsOwner(mapId, session.Username);
        string owner = mine ? "your map" : string.IsNullOrWhiteSpace(d.OwnerId) ? "a map of the server's" : $"{d.OwnerId}'s map";
        string invited = (mine || session.Can(Permissions.MapsAny)) && d.Invited.Count > 0 ? $" Invited: {string.Join(", ", d.Invited)}." : "";
        string build = mine ? " You can build, spawn, move and save here." : "";
        return $"You are on {d.Id}, {owner}, {(d.IsPublic ? "public" : "private")}.{invited}{build}";
    }

    /// <summary>A map whose access this session may change: their own, or any with maps-any.</summary>
    private bool TryManageMap(UserSession session, string mapId, Action<IMessage> reply, out OpenFPS.Server.Repositories.MapData data)
    {
        string? id = _maps.LoadedMapIds.FirstOrDefault(m => m.Equals(mapId, StringComparison.OrdinalIgnoreCase));
        if (id == null || !_maps.TryGetMapData(id, out data!))
        {
            data = null!;
            Say(reply, $"There is no map called {mapId}.");
            return false;
        }
        if (!_maps.IsOwner(id, session.Username) && !session.Can(Permissions.MapsAny))
        {
            Say(reply, $"{id} is not yours.");
            return false;
        }
        return true;
    }
}
