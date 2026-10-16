namespace OpenFPS.Client.Core;

/// <summary>What an interface sound means. Each has its own, so a menu, a chat and the world arriving
/// can be told apart by ear before a word is spoken.</summary>
public enum UiCue
{
    MenuMove, MenuSelect, MenuBack, MenuEdge,
    EnterWorld,
    ChatMap, ChatAll, ChatPrivate, ChatServer, ChatAdmin,
    VoiceOn, VoiceOff,
    Reconnecting,
    PresenceOnline, PresenceLoggedOut, PresenceConnectionLost, PresenceAway, PresenceBack,
    /// <summary>Your team talking: /t, or /team chat.</summary>
    ChatTeam,
    // Your shot landed on somebody; your shot killed them.
    Hit, Kill,
    /// <summary>Walking into a tile of the world that is not built yet: stopped, and nothing struck.</summary>
    UnbuiltEdge,
}
