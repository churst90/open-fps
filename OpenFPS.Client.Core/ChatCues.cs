using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// What a wire message is heard as (UiSounds plays it; the wire's types may not enter the Accessibility
/// layer, so this mapping stays with the game). The channel decides, whoever talks: an admin's map chat is
/// map chat, and the admin cue is for staff's server announcements. A reply to a command (no sender) is
/// only spoken. A presence notice has its own sound or none, never the chat sound: nobody said anything.
/// </summary>
public static class ChatCues
{
    /// <summary>The sound a line of chat is heard with, or none.</summary>
    public static UiCue? For(ChatMessage msg, bool presenceSounds)
    {
        if (msg.Presence != PresenceKind.None)
        {
            if (!presenceSounds) return null;
            return msg.Presence switch
            {
                PresenceKind.LoggedIn => UiCue.PresenceOnline,
                PresenceKind.LoggedOut => UiCue.PresenceLoggedOut,
                PresenceKind.WentOffline => UiCue.PresenceConnectionLost,
                PresenceKind.Away => UiCue.PresenceAway,
                PresenceKind.Back => UiCue.PresenceBack,
                _ => null,
            };
        }
        if (msg.Channel == ChatChannel.Server && msg.Sender.Length == 0) return null;
        return msg.Channel switch
        {
            ChatChannel.Private => UiCue.ChatPrivate,
            ChatChannel.Team => UiCue.ChatTeam,
            ChatChannel.All => UiCue.ChatAll,
            ChatChannel.Server => msg.FromStaff ? UiCue.ChatAdmin : UiCue.ChatServer,
            _ => UiCue.ChatMap,
        };
    }

    /// <summary>Plays the sound for a line of chat, if it has one.</summary>
    public static void Play(UiSounds sounds, ChatMessage msg)
    {
        if (For(msg, sounds.PresenceEnabled) is { } cue) sounds.Play(cue);
    }
}
