namespace OpenFPS.Client.Core;

/// <summary>
/// When to say what is in front of you as you turn (Cody, 2026-10-04): once the heading has held for
/// <see cref="SettleSeconds"/> after a turn of your own, so a run of taps is one line about where you
/// ended up. A line still being spoken when you turn again is about a heading you have left, and is
/// cut off (<see cref="Step.Interrupt"/>). Only your own turns count: a correction, a vehicle carrying
/// you round, a spawn or a teleport is not narrated.
/// </summary>
public sealed class TurnNarration
{
    /// <summary>How long the heading must hold after the last turn input before it is said.</summary>
    public const double SettleSeconds = 0.25;
    /// <summary>How long a narration is taken to be still talking: a fixed lead plus a time per
    /// character, which is about what a screen reader at a brisk rate takes.</summary>
    public const double SpeakingLeadSeconds = 0.4;
    public const double SpeakingSecondsPerChar = 0.045;
    /// <summary>The same line again within this long is not said again — unless the first was cut off.</summary>
    public const double RepeatSeconds = 3.0;

    public enum Step { None, Interrupt, Narrate }

    private bool _pending;
    private double _lastTurnAt = double.NegativeInfinity;
    private double _spokenAt = double.NegativeInfinity;
    private string? _spoken;
    private bool _cut;

    /// <summary>How long a line takes to say, roughly. See <see cref="SpeakingLeadSeconds"/>.</summary>
    public static double SpeakingSeconds(string line) => SpeakingLeadSeconds + SpeakingSecondsPerChar * line.Length;

    /// <summary>
    /// One simulation tick. <paramref name="turned"/>: whether the player's own input turned them on
    /// this tick. <paramref name="narrationStillCurrent"/>: whether the last thing said is still the
    /// last narration (nothing else has been spoken since), so cutting it off cuts nothing else.
    /// </summary>
    public Step Update(double now, bool turned, bool narrationStillCurrent = true)
    {
        if (turned)
        {
            bool starting = !_pending;
            _pending = true;
            _lastTurnAt = now;
            if (starting && narrationStillCurrent && _spoken != null && now - _spokenAt < SpeakingSeconds(_spoken))
            {
                // Cut off: whatever comes next is said, even if it is the same line again.
                _cut = true;
                return Step.Interrupt;
            }
            return Step.None;
        }
        if (_pending && now - _lastTurnAt >= SettleSeconds)
        {
            _pending = false;
            return Step.Narrate;
        }
        return Step.None;
    }

    /// <summary>Whether to say <paramref name="line"/> now: not if it is the same line said moments
    /// ago. Records it when it is to be said.</summary>
    public bool Accept(string line, double now)
    {
        if (line == _spoken && !_cut && now - _spokenAt < RepeatSeconds) return false;
        _spoken = line;
        _spokenAt = now;
        _cut = false;
        return true;
    }

    /// <summary>True from a turn until the narration after it: the head is still moving or settling.</summary>
    public bool Pending => _pending;

    /// <summary>Forget any turn in progress (a spawn, a teleport, getting into a seat).</summary>
    public void Reset()
    {
        _pending = false;
    }
}
