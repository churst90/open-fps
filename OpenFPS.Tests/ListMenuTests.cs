using System.Collections.Generic;
using OpenFPS.Client.Core;
using OpenFPS.Client.Core.Platform;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>The in-game lists behind F5, F6 and F8: arrows, Enter into a submenu, Escape back out.</summary>
public class ListMenuTests
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
    public void ArrowToAPlayerOpenTheirMenuAndChooseAnAction()
    {
        var tts = new Speech();
        var menus = new MenuStack(tts, null);
        string? did = null;
        ListMenu Person(string name) => new(name, new List<MenuItem>
        {
            new("Private message", () => did = $"pm {name}"),
            new("Where is", () => did = $"where {name}"),
        });
        menus.Show(new ListMenu("Players", new List<MenuItem>
        {
            new("ann, here on city", Opens: () => Person("ann")),
            new("sean01, on rooms", Opens: () => Person("sean01")),
        }));
        Assert.Contains("Players, 2 items. ann, here on city", tts.Spoken);

        menus.HandleKey(GameKey.Down);
        Assert.Equal("sean01, on rooms", tts.Spoken[^1]);
        menus.HandleKey(GameKey.Down);                       // the end: stays, says it again
        Assert.Equal("sean01, on rooms", tts.Spoken[^1]);
        menus.HandleKey(GameKey.Enter);                      // into sean01's menu
        Assert.Contains("sean01, 2 items. Private message", tts.Spoken);
        menus.HandleKey(GameKey.W);                          // w jumps to "Where is"
        menus.HandleKey(GameKey.Enter);
        Assert.Equal("where sean01", did);
        Assert.False(menus.IsOpen, "an action closes the lists");
    }

    [Fact]
    public void EscapeGoesBackALevelThenOut()
    {
        var tts = new Speech();
        var menus = new MenuStack(tts, null);
        menus.Show(new ListMenu("Maps", new List<MenuItem>
        {
            new("city", Opens: () => new ListMenu("city", new List<MenuItem> { new("Go") })),
        }));
        menus.HandleKey(GameKey.Enter);
        Assert.Equal("city", menus.Current!.Title);
        menus.HandleKey(GameKey.Escape);
        Assert.Equal("Maps", menus.Current!.Title);
        menus.HandleKey(GameKey.Escape);
        Assert.False(menus.IsOpen);
        Assert.Equal("Closed.", tts.Spoken[^1]);
    }
}
