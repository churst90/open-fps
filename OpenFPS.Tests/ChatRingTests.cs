using System;
using System.Collections.Generic;
using System.Linq;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using OpenFPS.Client.Services;
using OpenFPS.Common.Networking;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>The four chat rings, what each line says, and the interface sounds.</summary>
public class ChatRingTests
{
    private sealed class Speech : ISpeechOutput
    {
        public readonly List<string> Spoken = new();
        public bool Initialize() => true;
        public void Speak(string text, bool interrupt = true) => Spoken.Add(text);
        public void Interrupt() { }
        public string BackendName => "test";
        public void Dispose() { }
    }

    [Fact]
    public void NothingIsLabelledSystemAndOnlyTheServerIsServer()
    {
        var tts = new Speech();
        var chat = new ChatManager(tts);
        chat.AddServerMessage("Moved to 40, 0, 120");
        chat.AddMessage(new ChatMessage { Sender = "Server", Text = "Welcome", Channel = ChatChannel.Server });
        chat.AddMessage(new ChatMessage { Sender = "sean01", Text = "hi", Channel = ChatChannel.Map });
        chat.AddMessage(new ChatMessage { Sender = "sean01", Text = "psst", Channel = ChatChannel.Private });
        chat.AddMessage(new ChatMessage { Sender = "me", Text = "hello", Channel = ChatChannel.Private, To = "sean01" });
        chat.AddMessage(new ChatMessage { Sender = "ann", Text = "everyone!", Channel = ChatChannel.All });

        Assert.DoesNotContain(tts.Spoken, s => s.Contains("System"));
        Assert.Contains("Moved to 40, 0, 120", tts.Spoken);
        Assert.Contains("Server: Welcome", tts.Spoken);
        Assert.Contains("sean01: hi", tts.Spoken);
        Assert.Contains("Private from sean01: psst", tts.Spoken);
        Assert.Contains("Private to sean01: hello", tts.Spoken);
        Assert.Contains("ann to all: everyone!", tts.Spoken);
    }

    [Fact]
    public void ShiftBracketsWalkAllMapPrivateServer()
    {
        var tts = new Speech();
        var chat = new ChatManager(tts);
        var seen = new List<ChatBufferType> { chat.ActiveBuffer };
        for (int i = 0; i < 4; i++) { chat.CycleBuffer(1); seen.Add(chat.ActiveBuffer); }
        Assert.Equal(new[] { ChatBufferType.All, ChatBufferType.Map, ChatBufferType.Private, ChatBufferType.Server, ChatBufferType.All }, seen);
    }

    [Fact]
    public void ThePrivateRingIsEveryPrivateMessageAndNothingElse()
    {
        var tts = new Speech();
        var chat = new ChatManager(tts);
        chat.AddMessage(new ChatMessage { Sender = "a", Text = "one", Channel = ChatChannel.Private });
        chat.AddMessage(new ChatMessage { Sender = "b", Text = "map talk", Channel = ChatChannel.Map });
        chat.AddMessage(new ChatMessage { Sender = "c", Text = "two", Channel = ChatChannel.Private });
        chat.CycleBuffer(1); chat.CycleBuffer(1);   // Private
        Assert.Equal(ChatBufferType.Private, chat.ActiveBuffer);
        tts.Spoken.Clear();
        chat.CycleMessage(-1);
        chat.CycleMessage(-1);
        Assert.Equal(new[] { "Private from a: one", "Top. Private from a: one" }, tts.Spoken);
    }

    [Fact]
    public void EveryInterfaceSoundIsShortAndDistinct()
    {
        var shapes = new HashSet<int>();
        foreach (UiCue cue in Enum.GetValues(typeof(UiCue)))
        {
            var w = UiSounds.Render(cue);
            float peak = w.Max(MathF.Abs);
            Assert.InRange(peak, 0.2f, 0.95f);
            Assert.True(w.Length < UiSounds.SampleRate * 1.3, $"{cue} is too long");
            shapes.Add(w.Length);
        }
        Assert.True(shapes.Count >= 6, "the cues are not distinguishable by shape");
    }
}
