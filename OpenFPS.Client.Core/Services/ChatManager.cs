using System;
using System.Collections.Generic;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Services;

/// <summary>The four rings of chat, in the order Shift+[ and Shift+] walk them.</summary>
public enum ChatBufferType
{
    /// <summary>Everything, as it arrived.</summary>
    All,
    /// <summary>People on your map.</summary>
    Map,
    /// <summary>Every private message you received or sent — reply with /pm name text — and your
    /// team's talk, which is said to you too.</summary>
    Private,
    /// <summary>The server: its answers to your commands, the message of the day, announcements.</summary>
    Server,
}

/// <summary>
/// The player's message history as four rings, navigable by keyboard — the accessible equivalent of
/// a scrollback pane. [ and ] walk the messages in the ring you are in; Shift+[ and Shift+] change
/// ring. Speaks through <see cref="ISpeechOutput"/>, so it is shared by every client head.
///
/// Each message carries its channel; the ring and the wording come from that, never from the
/// sender's name. Nothing from the server is prefixed: its lines have their own sound.
/// </summary>
public class ChatManager
{
    private readonly Dictionary<ChatBufferType, List<string>> _buffers = new();
    private readonly Dictionary<ChatBufferType, int> _cursors = new();
    private ChatBufferType _active = ChatBufferType.All;
    private readonly ISpeechOutput _tts;

    /// <summary>Most lines a ring keeps; the oldest go first.</summary>
    public const int RingSize = 500;

    public ChatBufferType ActiveBuffer => _active;

    /// <summary>Every chat line as it arrives, before it is spoken — for the chat sounds.</summary>
    public event Action<ChatMessage>? Incoming;

    public ChatManager(ISpeechOutput tts)
    {
        _tts = tts;
        foreach (ChatBufferType type in Enum.GetValues(typeof(ChatBufferType)))
        {
            _buffers[type] = new List<string>();
            _cursors[type] = -1;
        }
    }

    public static ChatBufferType BufferFor(ChatChannel channel) => channel switch
    {
        ChatChannel.Private => ChatBufferType.Private,
        // Your team talking is talk addressed to you, as a private message is: filed with them and
        // spoken whichever ring you are in, rather than a fifth ring to walk past.
        ChatChannel.Team => ChatBufferType.Private,
        ChatChannel.Server => ChatBufferType.Server,
        // Said to everyone lives in All only; there is no separate ring for it.
        ChatChannel.All => ChatBufferType.All,
        _ => ChatBufferType.Map,
    };

    /// <summary>How a line reads, spoken or reviewed.</summary>
    public static string Format(ChatMessage msg) => msg.Presence != PresenceKind.None ? msg.Text : msg.Channel switch
    {
        // (A presence notice, above, is whole already: "cody is online", not "cody to all: cody is online".)
        ChatChannel.Private when msg.To.Length > 0 => $"Private to {msg.To}: {msg.Text}",
        ChatChannel.Private => $"Private from {msg.Sender}: {msg.Text}",
        ChatChannel.All => $"{msg.Sender} to all: {msg.Text}",
        ChatChannel.Team => $"{msg.Sender} to team: {msg.Text}",
        // The server's lines carry their own cue; "Server:" in front of each said nothing new.
        ChatChannel.Server => msg.Text,
        _ => msg.Sender.Length == 0 ? msg.Text : $"{msg.Sender}: {msg.Text}",
    };

    /// <summary>
    /// Files a message in its ring and in All, and speaks it if it is addressed to you or is in the
    /// ring you are reading.
    /// </summary>
    public void AddMessage(ChatMessage msg)
    {
        Incoming?.Invoke(msg);
        string line = Format(msg);
        var ring = BufferFor(msg.Channel);
        Add(ChatBufferType.All, line);
        if (ring != ChatBufferType.All) Add(ring, line);

        if (IsAddressedToYou(ring) || _active == ChatBufferType.All || ring == _active)
            _tts.Speak(line, interrupt: false);
    }

    /// <summary>
    /// Private messages and the server's answers are spoken whichever ring you are in. Ambient chat
    /// is not: turning away from it is what a ring is for. The answer to a command you just typed is
    /// always spoken, or a refused command would sound exactly like one that worked.
    /// </summary>
    private static bool IsAddressedToYou(ChatBufferType ring) => ring is ChatBufferType.Private or ChatBufferType.Server;

    private void Add(ChatBufferType ring, string line)
    {
        var list = _buffers[ring];
        list.Add(line);
        if (list.Count > RingSize) list.RemoveAt(0);
        _cursors[ring] = list.Count - 1;
    }

    /// <summary>The server answering you: no name in front of it.</summary>
    public void AddServerMessage(string text)
        => AddMessage(new ChatMessage { Sender = "", Text = text, Channel = ChatChannel.Server });

    /// <summary>Something went wrong for you. Filed with the server's answers, and spoken.</summary>
    public void AddError(string text) => AddServerMessage(text);

    public void CycleBuffer(int direction)
    {
        int count = Enum.GetValues(typeof(ChatBufferType)).Length;
        _active = (ChatBufferType)(((int)_active + direction + count) % count);
        _cursors[_active] = _buffers[_active].Count - 1;
        var list = _buffers[_active];
        _tts.Speak(list.Count == 0 ? $"{_active}, empty." : $"{_active}. {list[^1]}", interrupt: true);
    }

    public void CycleMessage(int direction)
    {
        var list = _buffers[_active];
        if (list.Count == 0)
        {
            _tts.Speak($"{_active} is empty.", interrupt: true);
            return;
        }
        int at = _cursors[_active] + direction;
        bool edge = at < 0 || at >= list.Count;
        at = Math.Clamp(at, 0, list.Count - 1);
        _cursors[_active] = at;
        _tts.Speak(edge ? (direction < 0 ? "Top. " : "Bottom. ") + list[at] : list[at], interrupt: true);
    }
}
