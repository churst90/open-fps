using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
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
/// a moment after they are out of the doorway. It is left for anybody else in the doorway, or any player
/// within reach of it, and never shut on them; a door they found open is left while a player is near it.
/// </summary>
public sealed class DoorManners
{
    /// <summary>What kind of door it is, for what is done with it.</summary>
    public enum Kind { ShutsItself, Outside, Inside }

    /// <summary>What is done with it once they are through.</summary>
    public enum Then { LetGo, Shut, Leave }

    /// <summary>Somebody's way through one door: how they found it, and which way they are going.</summary>
    public sealed class Passage
    {
        public required Entity Door;
        public required Kind Kind;
        public bool FoundOpen;
        /// <summary>Further from the street: an inside door found open is left open.</summary>
        public bool GoingIn;
        public Then Then => Decide(Kind, GoingIn, FoundOpen);
    }

    /// <summary>The rule, by itself.</summary>
    public static Then Decide(Kind kind, bool goingIn, bool foundOpen) => kind switch
    {
        Kind.ShutsItself => Then.LetGo,
        Kind.Outside => Then.Shut,
        _ => goingIn && foundOpen ? Then.Leave : Then.Shut,
    };

    /// <summary>How long after they are clear of the doorway the door is pulled to, seconds: a turn
    /// and a reach back, between these two, different for each person and door.</summary>
    public const float ShortestPause = 0.4f, LongestPause = 0.9f;

    /// <summary>How long they wait to be clear of the doorway before they are taken to be standing in
    /// it, and the door is left, seconds.</summary>
    public const float GiveUpSeconds = 10f;

    /// <summary>How near the doorway a player has to be for a door not to be shut in front of them,
    /// metres: walking up to it, or just through it.</summary>
    public const float PlayerReachMetres = 2.0f;

    /// <summary>
    /// A door they found open is somebody's, left open on purpose, while a player is this near it,
    /// metres: a rider shut Brandt Court's front door on Cody, who had opened it to listen to the street
    /// from the lobby (2026-10-02).
    /// </summary>
    public const float FoundOpenPlayerMetres = 8.0f;

    private sealed class Pending
    {
        public required string MapId;
        public required Passage Passage;
        public required Entity Who;
        public required float Pause;
        public float Clear;
        public float Waited;
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
        if (then != Then.Shut) return;
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
        if (at is { } a && DoorSystem.InDoorway(world, door, a))
        {
            p.Clear = 0f;
            return (p.Waited += dt) >= GiveUpSeconds;
        }
        if ((p.Clear += dt) < p.Pause) return false;

        if (SomebodyElseAt(world, door, p.Who, p.Passage.FoundOpen ? FoundOpenPlayerMetres : PlayerReachMetres) is { } other)
        {
            Log.Debug("Door manners: door {Door} left open for entity {Other}.", door.Id, other.Id);
            return true;
        }
        var hand = whoAlive ? p.Who : (Entity?)null;
        if (DoorSystem.InTheWay(world, door, 0f, hand) != null) return true;
        if (DoorSystem.Set(world, door, false, by: at, who: hand))
            Log.Debug("Door manners: entity {Who} shuts door {Door} behind them.", p.Who.Id, door.Id);
        return true;
    }

    private static bool Alive(World world, Entity who) => who != Entity.Null && world.IsAlive(who);

    private static readonly QueryDescription OnFoot =
        new QueryDescription().WithAll<Transform>().WithAny<PlayerComponent, Pedestrian>().WithNone<OccupantComponent, DeadComponent>();

    /// <summary>Anybody but <paramref name="who"/> in the doorway, or a player within <paramref name="reach"/> of it.</summary>
    private static Entity? SomebodyElseAt(World world, Entity door, Entity who, float reach)
    {
        DoorSystem.Doorway(world, door, out var centre, out _);
        Entity? found = null;
        world.Query(OnFoot, (Entity e, ref Transform t) =>
        {
            if (found != null || e == who) return;
            if (DoorSystem.InDoorway(world, door, t.Position)
                || world.Has<PlayerComponent>(e) && Vector3.Distance(t.Position, centre) < reach)
                found = e;
        });
        return found;
    }

    // ── What kind of door, and which way is in ──────────────────────────────────────────────────

    /// <summary>
    /// A door with a closer, a motor or a sensor shuts itself. Otherwise it is an outside door if either
    /// side of it is the outside (its portal's regions, or the places either side of the doorway), and an
    /// inside door if both are indoors.
    /// </summary>
    public static Kind KindOf(World world, Entity door)
    {
        var d = world.Get<DoorComponent>(door);
        if (d.CloseAfterSeconds > 0f || d.Powered || d.SensorMetres > 0f) return Kind.ShutsItself;
        var regions = Regions(world);
        if (world.Has<PortalComponent>(door))
        {
            var portal = world.Get<PortalComponent>(door);
            if (portal.RegionAId != portal.RegionBId)
                return IsOutside(regions, portal.RegionAId) || IsOutside(regions, portal.RegionBId) ? Kind.Outside : Kind.Inside;
        }
        DoorSystem.Doorway(world, door, out var centre, out var rotation);
        var through = Vector3.Transform(Vector3.UnitZ, rotation);
        return IsOutside(regions, RegionAt(world, centre + through)) || IsOutside(regions, RegionAt(world, centre - through))
            ? Kind.Outside : Kind.Inside;
    }

    /// <summary>
    /// Whether going from <paramref name="from"/> to <paramref name="to"/> is going in: to a place more
    /// doorways from the outside than where they were, or, as far from it, a smaller one (a room off a
    /// hall). Neither is going in, so a door they cannot tell about is left as found.
    /// </summary>
    public static bool GoingIn(World world, Vector3 from, Vector3 to)
    {
        int a = RegionAt(world, from), b = RegionAt(world, to);
        if (a == b) return true;
        var regions = Regions(world);
        var depth = DepthFromOutside(world, regions);
        int da = depth.GetValueOrDefault(a, int.MaxValue), db = depth.GetValueOrDefault(b, int.MaxValue);
        if (da != db) return db > da;
        float va = regions.TryGetValue(a, out var ra) ? ra.Volume : float.MaxValue;
        float vb = regions.TryGetValue(b, out var rb) ? rb.Volume : float.MaxValue;
        return vb <= va;
    }

    /// <summary>Every region on the map: whether it is indoors, and how big it is.</summary>
    private static Dictionary<int, (bool Indoor, float Volume)> Regions(World world)
    {
        var regions = new Dictionary<int, (bool, float)>();
        world.Query(new QueryDescription().WithAll<RegionComponent>(), (Entity e, ref RegionComponent r) =>
        {
            var s = r.RoomSize;
            regions[e.Id] = (r.IsIndoor, s.X > 0f && s.Y > 0f && s.Z > 0f ? s.X * s.Y * s.Z : float.MaxValue);
        });
        return regions;
    }

    /// <summary>The outside: no region, or a region that is not indoors.</summary>
    private static bool IsOutside(Dictionary<int, (bool Indoor, float Volume)> regions, int regionId)
        => regionId == AcousticConstants.GlobalRegionId || regions.TryGetValue(regionId, out var r) && !r.Indoor;

    /// <summary>The smallest region whose box holds a point, or the outside.</summary>
    public static int RegionAt(World world, Vector3 p)
    {
        int best = AcousticConstants.GlobalRegionId;
        float volume = float.MaxValue;
        world.Query(new QueryDescription().WithAll<Transform, RegionComponent>(), (Entity e, ref Transform t, ref RegionComponent r) =>
        {
            var size = r.RoomSize;
            if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) return;
            var local = Vector3.Transform(p - t.Position, Quaternion.Inverse(t.Rotation));
            if (MathF.Abs(local.X) > size.X * 0.5f || MathF.Abs(local.Y) > size.Y * 0.5f || MathF.Abs(local.Z) > size.Z * 0.5f) return;
            float v = size.X * size.Y * size.Z;
            if (v < volume) { volume = v; best = e.Id; }
        });
        return best;
    }

    /// <summary>How many doorways each region is from the outside, through every portal on the map.</summary>
    private static Dictionary<int, int> DepthFromOutside(World world, Dictionary<int, (bool Indoor, float Volume)> regions)
    {
        var links = new Dictionary<int, List<int>>();
        void Link(int a, int b)
        {
            if (!links.TryGetValue(a, out var list)) links[a] = list = new List<int>();
            list.Add(b);
        }
        world.Query(new QueryDescription().WithAll<PortalComponent>(), (ref PortalComponent p) =>
        {
            if (p.RegionAId == p.RegionBId) return;
            Link(p.RegionAId, p.RegionBId);
            Link(p.RegionBId, p.RegionAId);
        });
        var depth = new Dictionary<int, int>();
        var queue = new Queue<int>();
        foreach (int id in links.Keys.Append(AcousticConstants.GlobalRegionId).Concat(regions.Keys).Distinct())
            if (IsOutside(regions, id)) { depth[id] = 0; queue.Enqueue(id); }
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            if (!links.TryGetValue(at, out var next)) continue;
            foreach (int n in next)
                if (!depth.ContainsKey(n)) { depth[n] = depth[at] + 1; queue.Enqueue(n); }
        }
        return depth;
    }
}
