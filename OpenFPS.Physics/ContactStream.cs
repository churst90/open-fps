namespace OpenFPS.Physics;

/// <summary>
/// One map's contacts for one tick, in the order the steps made them (docs/PHYSICS_BOUNDARY.md stage 3): the
/// bodies on foot pressing into things, the vehicles striking them. The host begins a tick, the steps add to
/// it, the host closes it and reads it (a car's impact is heard from here). A pair met last tick and again
/// this one is <see cref="ContactPhase.Continued"/>; a pair met last tick and not this one gets an
/// <see cref="ContactPhase.Ended"/> when the tick closes.
/// </summary>
public sealed class ContactStream
{
    /// <summary>The most a tick keeps: a tick nobody began or closed (a test, a lab rig) must not grow
    /// without end. Past it, contacts are counted in <see cref="Dropped"/> and not kept.</summary>
    public const int MaxEvents = 4096;

    private readonly List<ContactEvent> _events = new();
    private Dictionary<(int, int), ContactEvent> _now = new(), _before = new();

    /// <summary>This tick's contacts so far.</summary>
    public IReadOnlyList<ContactEvent> Events => _events;

    /// <summary>Contacts not kept since the stream was made, for being past <see cref="MaxEvents"/>.</summary>
    public long Dropped { get; private set; }

    /// <summary>A new tick: last tick's pairs are what this one's continue.</summary>
    public void Begin()
    {
        (_before, _now) = (_now, _before);
        _now.Clear();
        _events.Clear();
    }

    /// <summary>A contact made this tick. Its phase is worked out here: whatever the step said is replaced.
    /// The same pair twice in a tick (two inputs of one player) is two events; the second continues the first.</summary>
    public ContactEvent Add(ContactEvent contact)
    {
        var pair = (contact.Body, contact.Other);
        var phase = _now.ContainsKey(pair) || _before.ContainsKey(pair) ? ContactPhase.Continued : ContactPhase.Began;
        var e = contact with { Phase = phase };
        _now[pair] = e;
        if (_events.Count < MaxEvents) _events.Add(e); else Dropped++;
        return e;
    }

    /// <summary>The tick is over: every pair met last tick and not this one ends, where it was last met.</summary>
    public void Close()
    {
        foreach (var (pair, last) in _before)
            if (!_now.ContainsKey(pair))
            {
                if (_events.Count < MaxEvents)
                    _events.Add(last with { Phase = ContactPhase.Ended, NormalImpulse = 0f, ClosingSpeed = 0f, SlipSpeed = 0f });
                else Dropped++;
            }
    }
}
