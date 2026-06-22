using System.Collections.Concurrent;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Server.Core;

/// <summary>
/// Responsible for tracking all active player sessions and their mapping to ECS entities.
/// </summary>
public class SessionManager
{
    private readonly ConcurrentDictionary<int, UserSession> _sessions = new();

    public void AddSession(int connectionId, UserSession session) => _sessions[connectionId] = session;
    
    public bool TryGetSession(int connectionId, out UserSession session) => _sessions.TryGetValue(connectionId, out session!);
    
    public bool TryRemoveSession(int connectionId, out UserSession session) => _sessions.TryRemove(connectionId, out session!);

    public IEnumerable<UserSession> GetAllSessions() => _sessions.Values;

    public IEnumerable<UserSession> GetSessionsInMap(string mapId) => _sessions.Values.Where(s => s.CurrentMapId == mapId);

    public bool IsEmpty => _sessions.IsEmpty;
    public int Count => _sessions.Count;
}
