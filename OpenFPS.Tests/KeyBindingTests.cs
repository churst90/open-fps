using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Tests;

/// <summary>
/// Shift-F5 is a different key from F5 ("the players here" beside "the players everywhere"), and the
/// brackets still fire with shift held: they read shift themselves to step buffers rather than messages.
/// </summary>
public class KeyBindingTests
{
    [Fact]
    public void AModifiedBindingWinsOverTheSameKeyAlone()
    {
        var map = new InputCommandMapper();
        string fired = "";
        map.Bind(InputContext.Gameplay, GameKey.F5, () => fired = "plain");
        map.Bind(InputContext.Gameplay, GameKey.F5, KeyModifiers.Shift, () => fired = "shifted");

        map.Execute(InputContext.Gameplay, GameKey.F5, KeyModifiers.None);
        Assert.Equal("plain", fired);

        map.Execute(InputContext.Gameplay, GameKey.F5, KeyModifiers.Shift);
        Assert.Equal("shifted", fired);
    }

    [Fact]
    public void AKeyWithNoModifiedBindingFiresWhateverIsHeld()
    {
        var map = new InputCommandMapper();
        int fired = 0;
        map.Bind(InputContext.Gameplay, GameKey.BracketLeft, () => fired++);

        map.Execute(InputContext.Gameplay, GameKey.BracketLeft, KeyModifiers.None);
        map.Execute(InputContext.Gameplay, GameKey.BracketLeft, KeyModifiers.Shift);
        map.Execute(InputContext.Gameplay, GameKey.BracketLeft, KeyModifiers.Control | KeyModifiers.Alt);

        Assert.Equal(3, fired);
    }

    [Fact]
    public void ContextBeatsGlobalAndGlobalIsTheFallback()
    {
        var map = new InputCommandMapper();
        string fired = "";
        map.Bind(GameKey.F6, () => fired = "global");
        map.Execute(InputContext.Gameplay, GameKey.F6);
        Assert.Equal("global", fired);

        map.Bind(InputContext.Gameplay, GameKey.F6, () => fired = "gameplay");
        map.Execute(InputContext.Gameplay, GameKey.F6);
        Assert.Equal("gameplay", fired);
    }

    [Fact]
    public void AnUnboundKeyRunsNothing()
    {
        var map = new InputCommandMapper();
        Assert.False(map.Execute(InputContext.Gameplay, GameKey.F12, KeyModifiers.Shift));
    }
}
