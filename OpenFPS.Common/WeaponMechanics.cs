using System;

namespace OpenFPS.Common;

/// <summary>
/// Something a weapon did that a listener could hear. The mechanics produce these; what turns them
/// into sound is not the mechanics' business, which is what lets the server run the same state machine
/// and broadcast the result rather than each client inventing its own.
/// </summary>
public enum WeaponAction
{
    /// <summary>A round left the barrel. The only one that also produces ballistics.</summary>
    Fire,
    /// <summary>The trigger fell on nothing. Quiet, and the single most useful sound in the game to
    /// hear coming from someone else.</summary>
    DryFire,
    /// <summary>The action cycled itself after a shot.</summary>
    Cycle,
    /// <summary>The bolt or slide locked back on an empty magazine.</summary>
    BoltLock,
    /// <summary>The charging handle worked, or a locked-back bolt released.</summary>
    Charge,
    MagOutLoaded,
    MagOutEmpty,
    MagInLoaded,
    MagInEmpty,
    /// <summary>One shell into a tube.</summary>
    LoadShell,
    /// <summary>A pump worked: the empty out, the fresh one in.</summary>
    Pump,
    /// <summary>The selector moved. Says a weapon is about to be used, or has just been made safe.</summary>
    Selector,
    HammerCock,
    HammerDecock,
    /// <summary>A casing hitting the ground, a moment after the shot and a little to one side.</summary>
    Casing,
}

/// <summary>One thing that happened, and when. <paramref name="DelaySeconds"/> is from now.</summary>
public readonly record struct WeaponEvent(WeaponAction Action, float DelaySeconds);

/// <summary>
/// Everything about a weapon that can change while someone is holding it.
///
/// Deliberately a struct of plain fields: the server owns one per player, the client predicts against
/// a copy, and neither is allowed to have state the other cannot see. A weapon whose chamber the
/// client disagrees about is a weapon that dry-fires on one machine and kills on the other.
/// </summary>
public struct WeaponState
{
    public string WeaponId;
    /// <summary>Rounds in the magazine or tube, NOT counting the one in the chamber.</summary>
    public int RoundsInMagazine;
    /// <summary>Whether there is a round ready to fire. A weapon reloaded without charging has a full
    /// magazine and an empty chamber, and pulling the trigger does nothing — which catches people.</summary>
    public bool RoundChambered;
    /// <summary>Loose rounds or magazines the player still has. Inventory will own this; until it does
    /// the server carries it here so the mechanics can be finished and tested.</summary>
    public int ReserveRounds;
    /// <summary>Index into the definition's FireModes.</summary>
    public int FireModeIndex;
    public bool BoltLockedBack;
    /// <summary>Hammer-fired weapons only: whether the first shot is ready.</summary>
    public bool HammerCocked;
    /// <summary>Pump guns: whether the action is closed on a live round. A pump gun that has fired and
    /// not been pumped has a dead trigger, and the player has to notice.</summary>
    public bool ActionClosed;
    /// <summary>World time until which the weapon is doing something else and will not fire.</summary>
    public float BusyUntil;
    /// <summary>World time of the earliest next shot, from the cyclic rate.</summary>
    public float NextShotAt;
    /// <summary>Whether the trigger is currently down. Semi needs a release before it will fire again;
    /// auto does not.</summary>
    public bool TriggerHeld;
    public int BurstRemaining;

    public WeaponState() { WeaponId = ""; ActionClosed = true; }

    public readonly FireMode Mode(WeaponDefinition d) =>
        d.FireModes.Length == 0 ? FireMode.Safe
                                : d.FireModes[Math.Clamp(FireModeIndex, 0, d.FireModes.Length - 1)];

    public readonly int TotalRounds => RoundsInMagazine + (RoundChambered ? 1 : 0);
}

/// <summary>
/// The rules a weapon obeys, as a pure function of its state. No timers, no coroutines, no audio: give
/// it a state and a world time and it tells you what changed and what was heard.
///
/// Stateless and deterministic for the same reason <see cref="SharedMovementEngine"/> is: the server
/// is authoritative, the client predicts, and the two have to agree exactly or the player hears a shot
/// that did not happen. Every method takes the current time rather than reading a clock, so a test can
/// fire a thirty-round magazine in a microsecond and check the spacing.
///
/// Events are written into a caller-owned span and the count returned, so a burst does not allocate.
/// Eight is enough for anything here: the largest single call is a shot, which produces at most a
/// fire, a cycle, a casing and a bolt lock.
/// </summary>
public static class WeaponMechanics
{
    public const int MaxEventsPerCall = 8;

    /// <summary>A fresh weapon, loaded and ready, selector on the first firing position it has.</summary>
    public static WeaponState Fresh(WeaponDefinition d, int spareMagazines = 3)
    {
        int firing = Array.FindIndex(d.FireModes, m => m != FireMode.Safe);
        return new WeaponState
        {
            WeaponId = d.Id,
            RoundsInMagazine = d.MagazineCapacity,
            RoundChambered = true,
            ReserveRounds = d.MagazineCapacity * spareMagazines,
            FireModeIndex = firing < 0 ? 0 : firing,
            HammerCocked = d.HammerFired,
            ActionClosed = true,
        };
    }

    /// <summary>
    /// The trigger goes down. Returns how many events were written.
    ///
    /// One pull, one call: automatic fire is <see cref="Hold"/> called on subsequent ticks, not a
    /// loop in here, because the trigger can be released between any two rounds and the state has to
    /// be right at every point in between.
    /// </summary>
    public static int PullTrigger(ref WeaponState s, WeaponDefinition d, float now, Span<WeaponEvent> events)
    {
        bool wasHeld = s.TriggerHeld;
        s.TriggerHeld = true;

        if (s.Mode(d) == FireMode.Safe)
            return 0;                       // A safe weapon makes no sound at all, not even a click.

        if (now < s.BusyUntil)
            return 0;                       // Mid-reload. The trigger is simply not connected.

        // Semi and burst need the trigger to have come back up. Holding it down on a semi-auto after
        // the first shot is the classic way to discover you are not firing.
        var mode = s.Mode(d);
        if (wasHeld && mode != FireMode.Auto)
            return 0;

        if (mode == FireMode.Burst && s.BurstRemaining <= 0)
            s.BurstRemaining = d.BurstCount;

        return Discharge(ref s, d, now, events);
    }

    /// <summary>The trigger is still down a tick later. Only automatic and burst do anything.</summary>
    public static int Hold(ref WeaponState s, WeaponDefinition d, float now, Span<WeaponEvent> events)
    {
        if (!s.TriggerHeld || now < s.BusyUntil || now < s.NextShotAt) return 0;
        var mode = s.Mode(d);
        if (mode == FireMode.Auto) return Discharge(ref s, d, now, events);
        if (mode == FireMode.Burst && s.BurstRemaining > 0) return Discharge(ref s, d, now, events);
        return 0;
    }

    public static void ReleaseTrigger(ref WeaponState s)
    {
        s.TriggerHeld = false;
        s.BurstRemaining = 0;
    }

    /// <summary>Everything that happens when the sear releases, whether or not a round goes off.</summary>
    private static int Discharge(ref WeaponState s, WeaponDefinition d, float now, Span<WeaponEvent> events)
    {
        int n = 0;

        // A pump gun that has not been pumped since its last shot has a dead trigger, and so does a
        // hammer-fired pistol whose hammer is down. Neither makes the click of an empty chamber —
        // nothing moves at all — so neither is a DryFire the way an empty magazine is.
        if (d.Action == ActionType.Pump && !s.ActionClosed) return 0;
        if (d.HammerFired && !s.HammerCocked) return 0;

        if (!s.RoundChambered)
        {
            // The distinctive, quiet, extremely informative click.
            events[n++] = new WeaponEvent(WeaponAction.DryFire, 0f);
            s.NextShotAt = now + d.IntervalFor(s.Mode(d));
            s.BurstRemaining = 0;
            return n;
        }

        events[n++] = new WeaponEvent(WeaponAction.Fire, 0f);
        s.RoundChambered = false;
        s.NextShotAt = now + d.IntervalFor(s.Mode(d));
        if (s.BurstRemaining > 0) s.BurstRemaining--;
        if (d.HammerFired) s.HammerCocked = false;

        if (d.Action == ActionType.SelfLoading)
        {
            // The gun works itself: the empty case out, the next round in — or the bolt back if there
            // was no next round. The action sound is a few tens of milliseconds behind the blast, and
            // at conversational range that separation is audible.
            if (s.RoundsInMagazine > 0)
            {
                s.RoundsInMagazine--;
                s.RoundChambered = true;
                if (d.HammerFired) s.HammerCocked = true;   // the slide re-cocks it
                events[n++] = new WeaponEvent(WeaponAction.Cycle, 0.045f);
            }
            else if (d.LocksBackWhenEmpty)
            {
                s.BoltLockedBack = true;
                events[n++] = new WeaponEvent(WeaponAction.BoltLock, 0.045f);
            }
            // A casing leaves a self-loader and lands about a third of a second later. On a pump gun
            // it leaves when the player works the pump, not now.
            events[n++] = new WeaponEvent(WeaponAction.Casing, 0.33f);
        }
        else
        {
            // A pump gun holds the empty case until the player works the action.
            s.ActionClosed = false;
        }

        return n;
    }

    /// <summary>Work the pump. Ejects the empty, chambers the next, and re-connects the trigger.</summary>
    public static int Pump(ref WeaponState s, WeaponDefinition d, float now, Span<WeaponEvent> events)
    {
        if (d.Action != ActionType.Pump || now < s.BusyUntil) return 0;
        int n = 0;
        events[n++] = new WeaponEvent(WeaponAction.Pump, 0f);
        bool ejected = !s.ActionClosed;
        if (s.RoundsInMagazine > 0 && !s.RoundChambered)
        {
            s.RoundsInMagazine--;
            s.RoundChambered = true;
        }
        s.ActionClosed = true;
        s.BusyUntil = now + d.PumpSeconds;
        if (ejected) events[n++] = new WeaponEvent(WeaponAction.Casing, d.PumpSeconds * 0.6f + 0.28f);
        return n;
    }

    /// <summary>
    /// Reload. For a box-fed weapon this is the whole sequence at once — magazine out, magazine in,
    /// and the bolt released if it had locked back. For a tube gun it is ONE shell; the caller repeats
    /// it, which is what makes a shotgun reload interruptible and countable by ear.
    /// </summary>
    public static int Reload(ref WeaponState s, WeaponDefinition d, float now, Span<WeaponEvent> events)
    {
        if (now < s.BusyUntil || s.ReserveRounds <= 0) return 0;
        int n = 0;

        if (d.Feed == FeedSystem.TubeMagazine)
        {
            if (s.RoundsInMagazine >= d.MagazineCapacity) return 0;
            s.RoundsInMagazine++;
            s.ReserveRounds--;
            s.BusyUntil = now + d.ShellLoadSeconds;
            events[n++] = new WeaponEvent(WeaponAction.LoadShell, 0f);
            return n;
        }

        if (s.RoundsInMagazine >= d.MagazineCapacity && s.RoundChambered) return 0;

        // The rounds left in a part-used magazine go with it. A player who reloads at twenty-nine
        // rounds has thrown twenty-nine rounds away, and the weight of that decision is the point.
        bool hadRounds = s.RoundsInMagazine > 0;
        events[n++] = new WeaponEvent(hadRounds ? WeaponAction.MagOutLoaded : WeaponAction.MagOutEmpty, 0f);

        int take = Math.Min(d.MagazineCapacity, s.ReserveRounds);
        s.ReserveRounds -= take;
        s.RoundsInMagazine = take;
        events[n++] = new WeaponEvent(take > 0 ? WeaponAction.MagInLoaded : WeaponAction.MagInEmpty,
                                      d.MagOutSeconds);

        float busy = d.MagOutSeconds + d.MagInSeconds;
        if (s.BoltLockedBack)
        {
            // The bolt was back, so releasing it chambers a round — and it is loud, which is the
            // sound of someone finishing a reload and being dangerous again.
            s.BoltLockedBack = false;
            if (s.RoundsInMagazine > 0) { s.RoundsInMagazine--; s.RoundChambered = true; }
            if (d.HammerFired) s.HammerCocked = true;
            events[n++] = new WeaponEvent(WeaponAction.Charge, busy);
            busy += d.ChargeSeconds;
        }
        s.BusyUntil = now + busy;
        return n;
    }

    /// <summary>Work the charging handle by hand. Chambers a round, and throws away a chambered one —
    /// which is what makes doing it needlessly a small, audible, expensive mistake.</summary>
    public static int Charge(ref WeaponState s, WeaponDefinition d, float now, Span<WeaponEvent> events)
    {
        if (now < s.BusyUntil) return 0;
        int n = 0;
        events[n++] = new WeaponEvent(WeaponAction.Charge, 0f);
        bool ejected = s.RoundChambered;
        s.RoundChambered = false;
        s.BoltLockedBack = false;
        if (s.RoundsInMagazine > 0)
        {
            s.RoundsInMagazine--;
            s.RoundChambered = true;
        }
        if (d.HammerFired) s.HammerCocked = true;
        if (d.Action == ActionType.Pump) s.ActionClosed = true;
        s.BusyUntil = now + d.ChargeSeconds;
        if (ejected) events[n++] = new WeaponEvent(WeaponAction.Casing, d.ChargeSeconds + 0.28f);
        return n;
    }

    /// <summary>Move the selector one position on. Returns 0 for a weapon that has no selector.</summary>
    public static int CycleFireMode(ref WeaponState s, WeaponDefinition d, float now, Span<WeaponEvent> events)
    {
        if (d.FireModes.Length < 2 || now < s.BusyUntil) return 0;
        s.FireModeIndex = (s.FireModeIndex + 1) % d.FireModes.Length;
        s.BurstRemaining = 0;
        s.BusyUntil = now + d.SelectorSeconds;
        events[0] = new WeaponEvent(WeaponAction.Selector, 0f);
        return 1;
    }

    /// <summary>Cock or drop the hammer by hand, on a weapon that has one to cock.</summary>
    public static int ToggleHammer(ref WeaponState s, WeaponDefinition d, float now, Span<WeaponEvent> events)
    {
        if (!d.HammerFired || now < s.BusyUntil) return 0;
        s.HammerCocked = !s.HammerCocked;
        s.BusyUntil = now + 0.3f;
        events[0] = new WeaponEvent(s.HammerCocked ? WeaponAction.HammerCock : WeaponAction.HammerDecock, 0f);
        return 1;
    }

    /// <summary>The folder a heard action resolves to, for a given weapon. One place, so the server
    /// and both heads cannot drift on what a reload sounds like.</summary>
    public static string SoundFolderFor(WeaponDefinition d, WeaponAction action) => action switch
    {
        WeaponAction.Fire => d.FiringFolder,
        WeaponAction.DryFire => d.SoundFor("TRIGGER"),
        WeaponAction.Cycle => d.SoundFor(d.Action == ActionType.Pump ? "PUMP" : "BOLT_CLOSE"),
        WeaponAction.BoltLock => d.SoundFor("BOLT_OPEN"),
        WeaponAction.Charge => d.SoundFor("CHARGE"),
        WeaponAction.MagOutLoaded => d.SoundFor("MAG_OUT_LOADED"),
        WeaponAction.MagOutEmpty => d.SoundFor("MAG_OUT_EMPTY"),
        WeaponAction.MagInLoaded => d.SoundFor("MAG_IN_LOADED"),
        WeaponAction.MagInEmpty => d.SoundFor("MAG_IN_EMPTY"),
        WeaponAction.LoadShell => d.SoundFor("LOAD_SHELL"),
        WeaponAction.Pump => d.SoundFor("PUMP"),
        WeaponAction.Selector => d.SoundFor("SAFETY"),
        WeaponAction.HammerCock => d.SoundFor("HAMMER_COCK"),
        WeaponAction.HammerDecock => d.SoundFor("HAMMER_DECOCK"),
        WeaponAction.Casing => WeaponRegistry.CasingFolder,
        _ => "",
    };
}
