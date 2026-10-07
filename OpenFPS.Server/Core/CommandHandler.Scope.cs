using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Core;

/// <summary>
/// Permission by scope, and the ceiling on granting. See <see cref="Permissions"/>: a permission held
/// is a permission on any map, and the building verbs are also everybody's on a map they own.
/// </summary>
public partial class CommandHandler
{
    /// <summary>Whether this session owns the map it is on.</summary>
    private bool OwnsHere(UserSession session) => _maps.IsOwner(session.CurrentMapId, session.Username);

    /// <summary>Whether this session may use a permission where it is: held (any map), or one everybody
    /// has on their own map and this is theirs.</summary>
    private bool MayHere(UserSession session, string permission)
        => session.Can(permission) || (Permissions.OnOwnMap(permission) && OwnsHere(session))
           || (Permissions.ForMapEditors(permission) && _maps.IsEditor(session.CurrentMapId, session.Username));

    /// <summary>The world editor, made on first use (docs/WORLD_EDITOR.md).</summary>
    public OpenFPS.Server.Editor.WorldEditor Editor => _editor ??= new OpenFPS.Server.Editor.WorldEditor(_maps, _server, _sessions);
    private OpenFPS.Server.Editor.WorldEditor? _editor;

    /// <summary>
    /// Why somebody without grant-any may not grant or revoke this, or null if they may. A developer
    /// grants to players only, never to anybody whose custom role can do more than the developer can,
    /// and only permissions the developer holds (Cody, 2026-10-05: "can never grant anyone else
    /// anything more than what they can do").
    /// </summary>
    private string? GrantCeiling(UserSession granter, UserData target, string permission)
    {
        if (target.Role != UserRole.Player)
            return $"You can only change a player's permissions; {target.Username} is {Article(target.Role)} {RoleWord(target.Role)}.";
        if (target.CustomRole is { Length: > 0 } custom && _server.Roles.Exists(custom)
            && _server.Roles.PermissionsOf(custom).Any(p => !granter.Can(p)))
            return $"{target.Username}'s role can do things you cannot, so only an administrator can change their permissions.";
        if (!granter.Can(permission))
            return $"You do not have {permission} yourself, so you cannot grant or revoke it.";
        return null;
    }

    /// <summary>
    /// The hand-over, heard by whoever was given something: an event named for what it was,
    /// "give:PREFAB" ("give:teleporter"), or "give:vehicle:PRESET" for a vehicle. The client makes the
    /// item's own sound from the name; the server only says it happened, on the receiver's body.
    /// </summary>
    private void SendGiveEvent(UserSession receiver, string what)
    {
        if (!_maps.TryGetMap(receiver.CurrentMapId, out var world, out _, out _, out _)
            || receiver.Entity == Entity.Null || !world.IsAlive(receiver.Entity)) return;
        var at = world.Get<Transform>(receiver.Entity).Position;
        string key = HandOverSounds.Key(what);
        _server.SendToSession(receiver, new WorldAudioEvent
        {
            SourceEntityId = receiver.Entity.Id,
            Label = key,
            Seed = Random.Shared.Next(),
            Sounds = new List<TransientSound> { HandOverSounds.Sound(what, at) },
        });
    }
}
