using System.Numerics;
using Arch.Core;
using OpenFPS.Acoustics;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// What somebody the server walks (Alex, a driver going indoors from a parked car) does with a door they
/// go through (Cody, 2026-10-08):
/// <list type="bullet">
/// <item>A door that shuts itself (a closer, a motor) is let go, and its closer shuts it once nobody is
/// in the doorway.</item>
/// <item>An outside door without a closer is shut behind them, however they found it.</item>
/// <item>An inside door is left as they found it going in (further from the street) and shut going out
/// (toward the street).</item>
/// </list>
/// They shut it by hand, as a player does (<see cref="DoorSystem.Set"/>, so it makes the same sounds),
/// a moment after the doorway is clear. Who is about does not change the rule (Cody, 2026-10-10: "the
/// rule of what the npc does should take precedence, regardless of a player is around or not"). Only a
/// body stops it: a leaf cannot swing through anybody, so it waits while anyone is in the doorway or in
/// the leaf's way, and shuts once they have moved.
/// </summary>
public sealed class DoorManners
{
    /// <summary>Somebody's way through one door: how they found it, and which way they are going.</summary>
    public sealed class Passage
    {
        public required Entity Door;
        public required DoorRules.Kind Kind;
        public bool FoundOpen;
        /// <summary>Further from the street: an inside door found open is left open.</summary>
        public bool GoingIn;
        public DoorRules.Then Then => DoorRules.Decide(Kind, GoingIn, FoundOpen);
    }

    /// <summary>How long after the doorway is clear the door is pulled to, seconds: a turn and a reach
    /// back, between these two, different for each person and door.</summary>
    public const float ShortestPause = 0.4f, LongestPause = 0.9f;

    private sealed class Pending
    {
        public required string MapId;
        public required Passage Passage;
        public required Entity Who;
        public required float Pause;
        public float Clear;
    }

    private readonly List<Pending> _pending = new();

    /// <summary>Doors waiting to be pulled to behind somebody, on every map.</summary>
    public int Waiting => _pending.Count;

    /// <summary>
    /// Somebody at <paramref name="from"/> reaching a door on their way to <paramref name="to"/>: how it
    /// is is remembered, and with <paramref name="open"/> it is opened if it is shut and opens by hand
    /// (a door with a sensor opens for them by itself). <paramref name="who"/> may be nobody yet: a
    /// person who appears once it is open. Null for no door.
    /// </summary>
    public Passage? Reach(World world, Entity door, Vector3 from, Vector3 to, Entity who, bool open = true)
    {
        if (door == Entity.Null || !world.IsAlive(door) || !world.Has<DoorComponent>(door)) return null;
        var d = world.Get<DoorComponent>(door);
        var passage = new Passage
        {
            Door = door,
            Kind = KindOf(world, door),
            FoundOpen = d.Target > 0.5f,
            GoingIn = GoingIn(world, from, to),
        };
        if (open && !passage.FoundOpen && DoorSystem.OpensByHand(d))
            DoorSystem.Set(world, door, true, by: from, who: Alive(world, who) ? who : null);
        return passage;
    }

    /// <summary>They are through: the door is shut behind them if the rule says so, once they are out
    /// of the doorway (<see cref="Update"/>).</summary>
    public void Through(string mapId, World world, Passage? passage, Entity who)
    {
        if (passage == null || !world.IsAlive(passage.Door)) return;
        var then = passage.Then;
        Log.Debug("Door manners: entity {Who} through door {Door} ({Kind}, found {Found}, going {Way}): {Then}.",
                  who.Id, passage.Door.Id, passage.Kind, passage.FoundOpen ? "open" : "shut", passage.GoingIn ? "in" : "out", then);
        if (then != DoorRules.Then.Shut) return;
        _pending.RemoveAll(p => p.MapId == mapId && p.Passage.Door == passage.Door);
        // Each person and door its own pause, the same every time.
        uint h = (uint)HashCode.Combine(passage.Door.Id, who.Id);
        float pause = ShortestPause + (LongestPause - ShortestPause) * (h % 1000) / 999f;
        _pending.Add(new Pending { MapId = mapId, Passage = passage, Who = who, Pause = pause });
    }

    /// <summary>One tick: each door waiting to be shut on this map, shut once its person is clear of the
    /// doorway and the pause is over, or left.</summary>
    public void Update(string mapId, World world, float dt)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.MapId != mapId) continue;
            if (Settle(world, p, dt)) _pending.RemoveAt(i);
        }
    }

    /// <summary>True once the door is shut or left.</summary>
    private static bool Settle(World world, Pending p, float dt)
    {
        var door = p.Passage.Door;
        if (!world.IsAlive(door) || !world.Has<DoorComponent>(door)) return true;
        var d = world.Get<DoorComponent>(door);
        // Shut, or going shut, already; or somebody else has hold of it.
        if (d.Target <= 0f) return true;
        bool whoAlive = Alive(world, p.Who);
        if (d.HandId != 0 && (!whoAlive || d.HandId != p.Who.Id + 1)) return true;
        // Killed on the way: nobody shuts it.
        if (p.Who != Entity.Null && world.IsAlive(p.Who) && world.Has<DeadComponent>(p.Who)) return true;

        Vector3? at = whoAlive && world.Has<Transform>(p.Who) ? world.Get<Transform>(p.Who).Position : null;
        var hand = whoAlive ? p.Who : (Entity?)null;
        // A body in the doorway or in the leaf's way, theirs or anybody's: the leaf cannot go through it.
        if (AnybodyInTheDoorway(world, door) || DoorSystem.InTheWay(world, door, 0f, hand) != null)
        {
            p.Clear = 0f;
            return false;
        }
        if ((p.Clear += dt) < p.Pause) return false;
        if (DoorSystem.Set(world, door, false, by: at, who: hand))
            Log.Debug("Door manners: entity {Who} shuts door {Door} behind them.", p.Who.Id, door.Id);
        return true;
    }

    private static bool Alive(World world, Entity who) => who != Entity.Null && world.IsAlive(who);

    private static readonly QueryDescription OnFoot =
        new QueryDescription().WithAll<Transform>().WithAny<PlayerComponent, Pedestrian>().WithNone<OccupantComponent, DeadComponent>();

    /// <summary>Anybody on foot, player or not, in the doorway.</summary>
    private static bool AnybodyInTheDoorway(World world, Entity door)
    {
        bool found = false;
        world.Query(OnFoot, (Entity e, ref Transform t) =>
        {
            if (!found && DoorSystem.InDoorway(world, door, t.Position)) found = true;
        });
        return found;
    }

    // ── What kind of door, and which way is in ──────────────────────────────────────────────────

    /// <summary>A view over the map, for the law: the wrappers need no grid.</summary>
    private static IPeopleWorld ViewOf(World world) => ServerPeopleView.Of(world, null);

    /// <summary>What kind of door it is, through the law (DoorRules.KindOf).</summary>
    public static DoorRules.Kind KindOf(World world, Entity door)
        => DoorRules.KindOf(ViewOf(world), door.Id, AcousticConstants.GlobalRegionId);

    /// <summary>Whether this way is going in, through the law (DoorRules.GoingIn).</summary>
    public static bool GoingIn(World world, Vector3 from, Vector3 to)
        => DoorRules.GoingIn(ViewOf(world), from, to, AcousticConstants.GlobalRegionId);
}
