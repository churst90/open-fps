using System;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Services;

/// <summary>
/// Service responsible for managing map ownership, upload permissions, and global publication.
/// Ensures that users only modify content they have authority over.
/// </summary>
public class MapAuthorityService
{
    /// <summary>
    /// Initializes the map authority service and registers handlers for map management.
    /// </summary>
    public MapAuthorityService(IMessageDispatcher dispatcher)
    {
        dispatcher.RegisterHandler<MapPublishRequest>(HandleMapPublish);
    }

    /// <summary>
    /// Processes a request to publish or update the visibility of a map.
    /// </summary>
    private void HandleMapPublish(int connectionId, MapPublishRequest request, Action<IMessage> reply)
    {
        // TODO: Implement permission check (Does this connectionId own this MapName?)
        string name = AuthService.ForLog(request.MapName);
        Log.Information("Received request to publish map {MapName} as {IsPublic}", name, request.IsPublic ? "Public" : "Private");
        reply(new TextEvent { Text = $"Map '{name}' publish request received and is pending validation." });
    }
}
