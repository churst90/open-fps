using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Client.Core.Session;

/// <summary>
/// Trying a server until it answers or the time runs out, and what to say on the way: one line when the
/// first try goes unanswered, then "still trying" no more often than the announce interval, never the
/// same line twice. It only decides; the session connects and speaks. Times are passed in, so a test
/// needs no clock.
/// </summary>
public sealed class ConnectRetry
{
    public const string NotAnsweringYet = "The server is not answering yet; still trying. Escape to cancel.";

    public enum Next { Wait, Try, GiveUp }

    private readonly TimeSpan _for, _interval, _announceEvery;
    private DateTime _lastTry, _lastSaid;
    private string? _lastLine;

    public DateTime Started { get; }

    /// <summary>True once a try has gone unanswered, or from the start after a drop: the next try is
    /// the session's to start.</summary>
    public bool Retrying { get; private set; }

    /// <param name="retrying">True after a drop, where the drop itself was the announcement and the
    /// first try waits one interval.</param>
    public ConnectRetry(DateTime now, TimeSpan retryFor, TimeSpan interval, TimeSpan announceEvery, bool retrying = false)
    {
        Started = _lastTry = _lastSaid = now;
        _for = retryFor;
        _interval = interval;
        _announceEvery = announceEvery;
        Retrying = retrying;
    }

    /// <summary>A try went unanswered. Returns the line to say now, or null.</summary>
    public string? NotAnswered(DateTime now)
    {
        if (Retrying) return null;
        Retrying = true;
        return Say(now, NotAnsweringYet);
    }

    /// <summary>
    /// What to do now. <paramref name="attemptOut"/> is a try in flight: nothing new starts over it.
    /// When the time is up it is given up, in flight or not: the session drops it.
    /// </summary>
    public Next Tick(DateTime now, bool attemptOut, out string? say)
    {
        say = null;
        if (!Retrying) return Next.Wait;
        if (now - Started >= _for) return Next.GiveUp;
        if (now - _lastSaid >= _announceEvery)
            say = Say(now, $"Still trying, {(int)Math.Round((now - Started).TotalSeconds)} seconds.");
        if (attemptOut || now - _lastTry < _interval) return Next.Wait;
        _lastTry = now;
        return Next.Try;
    }

    private string? Say(DateTime now, string line)
    {
        if (line == _lastLine) return null;
        _lastLine = line;
        _lastSaid = now;
        return line;
    }
}

/// <summary>What a key does in the window that holds focus while a connect is under way.</summary>
public enum HoldKey
{
    /// <summary>Left alone, for the screen reader and the window manager.</summary>
    Pass,
    /// <summary>Taken and dropped, so nothing behind the window moves.</summary>
    Hold,
    /// <summary>Stops the connect.</summary>
    Cancel,
}

/// <summary>The keys of the connecting window, the same in both heads.</summary>
public static class ConnectHold
{
    /// <summary>
    /// Escape cancels. Every key the game knows is held, the menu's arrows, Tab, Enter and Space among
    /// them. The modifiers pass (Control is how a screen reader stops talking), and so does anything
    /// the game does not map: Insert, Caps Lock and the keypad with Num Lock off are the reader's.
    /// </summary>
    public static HoldKey Decide(GameKey key) => key switch
    {
        GameKey.Escape => HoldKey.Cancel,
        GameKey.None or GameKey.ShiftLeft or GameKey.ShiftRight => HoldKey.Pass,
        _ when ClientGameSession.ScreenReaderKeys.Contains(key) => HoldKey.Pass,
        _ => HoldKey.Hold,
    };
}
