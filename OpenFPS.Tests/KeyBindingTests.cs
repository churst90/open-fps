using OpenFPS.Client.Core.Input;
using OpenFPS.Client.Core.Platform;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// Shift-F5 is a different key from F5, and the brackets still work with shift held.
///
/// Both halves matter. Without the first there is nowhere to put "the players HERE" next to "the
/// players everywhere". Without the second, adding modifiers would silently break every key that
/// reads its own modifier — the chat brackets decide between stepping messages and stepping buffers
/// by asking about shift themselves, and a table that refused to fire them under shift would have
/// taken that away without a word.
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
