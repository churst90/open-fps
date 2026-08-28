using System;
using System.Collections.Generic;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Services;

public enum ChatBufferType
{
    Global,   // Public chat visible to everyone on the server
    Map,      // Chat from players on the current map
    Private,  // Direct/private messages
    Server,   // Server-generated announcements and TextEvents
    Error     // Error messages from the server or client systems
}

/// <summary>
/// The player's message history, bucketed by kind and navigable by keyboard — the accessible
/// equivalent of a scrollback pane. Speaks through <see cref="ISpeechOutput"/>, so it is shared by
/// both heads; it used to be Windows-only and typed on that head's concrete TTS service, which is
/// why the Linux client had no chat buffers at all.
/// </summary>
public class ChatManager
{
    private readonly Dictionary<ChatBufferType, List<ChatMessage>> _buffers = new();
    private readonly Dictionary<ChatBufferType, int> _bufferCursors = new();
    private ChatBufferType _activeBuffer = ChatBufferType.Global;
    private readonly ISpeechOutput _tts;

    public ChatBufferType ActiveBuffer => _activeBuffer;

    public ChatManager(ISpeechOutput tts)
    {
        _tts = tts;
        foreach (ChatBufferType type in Enum.GetValues(typeof(ChatBufferType)))
        {
            _buffers[type] = new List<ChatMessage>();
            _bufferCursors[type] = -1;
        }
    }

    /// <summary>
    /// Routes an incoming message to the correct buffer and speaks it immediately.
    /// Sender prefixes drive routing:
    ///   "[PM"    → Private
    ///   "[Map]"  → Map
    ///   "[Error" → Error
    ///   "System" → Server
    ///   anything else → Global
    /// </summary>
    public void AddMessage(ChatMessage msg)
    {
        ChatBufferType target = ClassifyMessage(msg);
        _buffers[target].Add(msg);
        _bufferCursors[target] = _buffers[target].Count - 1;

        // Always speak the message immediately.
        // Private and Error messages are spoken regardless of active buffer.
        bool alwaysSpeak = target == ChatBufferType.Private || target == ChatBufferType.Error;
        if (alwaysSpeak || target == _activeBuffer)
        {
            _tts.Speak($"{msg.Sender}: {msg.Text}", interrupt: false);
        }
    }

    /// <summary>
    /// Posts a plain error string directly to the Error buffer and speaks it immediately.
    /// </summary>
    public void AddError(string text)
    {
        AddMessage(new ChatMessage { Sender = "[Error]", Text = text });
    }

    /// <summary>
    /// Posts a server announcement to the Server buffer.
    /// </summary>
    public void AddServerMessage(string text)
    {
        AddMessage(new ChatMessage { Sender = "System", Text = text });
    }

    public void CycleBuffer(int direction)
    {
        int count = Enum.GetValues(typeof(ChatBufferType)).Length;
        int current = (int)_activeBuffer;
        current = (current + direction + count) % count;
        _activeBuffer = (ChatBufferType)current;

        _tts.Speak($"Switched to {_activeBuffer} buffer.", interrupt: true);

        _bufferCursors[_activeBuffer] = _buffers[_activeBuffer].Count - 1;
        ReadCursorMessage();
    }

    public void CycleMessage(int direction)
    {
        var buffer = _buffers[_activeBuffer];
        if (buffer.Count == 0)
        {
            _tts.Speak($"{_activeBuffer} buffer is empty.", interrupt: false);
            return;
        }

        int current = _bufferCursors[_activeBuffer];
        current = Math.Clamp(current + direction, 0, buffer.Count - 1);
        _bufferCursors[_activeBuffer] = current;

        ReadCursorMessage();
    }

    private void ReadCursorMessage()
    {
        var buffer = _buffers[_activeBuffer];
        int idx = _bufferCursors[_activeBuffer];

        if (idx >= 0 && idx < buffer.Count)
        {
            var msg = buffer[idx];
            _tts.Speak($"{msg.Sender}: {msg.Text}", interrupt: false);
        }
        else
        {
            _tts.Speak($"{_activeBuffer} buffer is empty.", interrupt: false);
        }
    }

    private static ChatBufferType ClassifyMessage(ChatMessage msg)
    {
        if (msg.Sender.Contains("[PM", StringComparison.OrdinalIgnoreCase)) return ChatBufferType.Private;
        if (msg.Sender.StartsWith("[Map]", StringComparison.OrdinalIgnoreCase)) return ChatBufferType.Map;
        if (msg.Sender.StartsWith("[Error", StringComparison.OrdinalIgnoreCase)) return ChatBufferType.Error;
        if (msg.Sender.Equals("System", StringComparison.OrdinalIgnoreCase)) return ChatBufferType.Server;
        return ChatBufferType.Global;
    }
}
