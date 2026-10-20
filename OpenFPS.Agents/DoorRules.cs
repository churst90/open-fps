using System.Numerics;

namespace OpenFPS.Server.Systems;

/// <summary>
/// What is done with a door somebody goes through, from what the map says about it (Cody, 2026-10-08):
/// <list type="bullet">
/// <item>A door that shuts itself (a closer, a motor) is let go, and its closer shuts it once nobody is
/// in the doorway.</item>
/// <item>An outside door without a closer is shut behind them, however they found it.</item>
/// <item>An inside door is left as they found it going in (further from the street) and shut going out
/// (toward the street).</item>
/// </list>
/// Which way is in, and what the outside is, come from the map's regions and portals, read through
/// <see cref="IPeopleWorld"/>.
/// </summary>
public static class DoorRules
{
    /// <summary>What kind of door it is, for what is done with it.</summary>
    public enum Kind { ShutsItself, Outside, Inside }

    /// <summary>What is done with it once they are through.</summary>
    public enum Then { LetGo, Shut, Leave }

    /// <summary>The rule, by itself.</summary>
    public static Then Decide(Kind kind, bool goingIn, bool foundOpen) => kind switch
    {
        Kind.ShutsItself => Then.LetGo,
        Kind.Outside => Then.Shut,
        _ => goingIn && foundOpen ? Then.Leave : Then.Shut,
    };

    /// <summary>
    /// A door with a closer, a motor or a sensor shuts itself. Otherwise it is an outside door if either
    /// side of it is the outside (its portal's regions, or the places either side of the doorway), and an
    /// inside door if both are indoors. <paramref name="outside"/> is the map's outside region id.
    /// </summary>
    public static Kind KindOf(IPeopleWorld world, int door, int outside)
    {
        world.TryDoor(door, out var d);
        if (d.CloseAfterSeconds > 0f || d.Powered || d.SensorMetres > 0f) return Kind.ShutsItself;
        var regions = Regions(world);
        if (d.RegionAId != d.RegionBId)
            return IsOutside(regions, d.RegionAId, outside) || IsOutside(regions, d.RegionBId, outside) ? Kind.Outside : Kind.Inside;
        var centre = d.Doorway.Centre;
        var through = Vector3.Transform(Vector3.UnitZ, d.Doorway.Rotation);
        return IsOutside(regions, world.RegionAt(centre + through), outside) || IsOutside(regions, world.RegionAt(centre - through), outside)
            ? Kind.Outside : Kind.Inside;
    }

    /// <summary>
    /// Whether going from <paramref name="from"/> to <paramref name="to"/> is going in: to a place more
    /// doorways from the outside than where they were, or, as far from it, a smaller one (a room off a
    /// hall). Neither is going in, so a door they cannot tell about is left as found.
    /// </summary>
    public static bool GoingIn(IPeopleWorld world, Vector3 from, Vector3 to, int outside)
    {
        int a = world.RegionAt(from), b = world.RegionAt(to);
        if (a == b) return true;
        var regions = Regions(world);
        var depth = DepthFromOutside(world, regions, outside);
        int da = depth.GetValueOrDefault(a, int.MaxValue), db = depth.GetValueOrDefault(b, int.MaxValue);
        if (da != db) return db > da;
        float va = regions.TryGetValue(a, out var ra) ? ra.Volume : float.MaxValue;
        float vb = regions.TryGetValue(b, out var rb) ? rb.Volume : float.MaxValue;
        return vb <= va;
    }

    /// <summary>Every region on the map: whether it is indoors, and how big it is (its flood's air where it flooded,
    /// else its box).</summary>
    private static Dictionary<int, (bool Indoor, float Volume)> Regions(IPeopleWorld world)
    {
        var regions = new Dictionary<int, (bool, float)>();
        var list = new List<RegionBody>();
        world.Regions(list);
        foreach (var r in list) regions[r.Id] = (r.Indoor, r.Air);
        return regions;
    }

    /// <summary>The outside: the map's outside region, no region, or a region that is not indoors.</summary>
    private static bool IsOutside(Dictionary<int, (bool Indoor, float Volume)> regions, int regionId, int outside)
        => regionId == outside || regions.TryGetValue(regionId, out var r) && !r.Indoor;

    /// <summary>How many doorways each region is from the outside, through every portal on the map.</summary>
    private static Dictionary<int, int> DepthFromOutside(IPeopleWorld world, Dictionary<int, (bool Indoor, float Volume)> regions, int outside)
    {
        var links = new Dictionary<int, List<int>>();
        void Link(int a, int b)
        {
            if (!links.TryGetValue(a, out var list)) links[a] = list = new List<int>();
            list.Add(b);
        }
        var portals = new List<PortalBody>();
        world.Portals(portals);
        foreach (var p in portals)
        {
            if (p.RegionAId == p.RegionBId) continue;
            Link(p.RegionAId, p.RegionBId);
            Link(p.RegionBId, p.RegionAId);
        }
        var depth = new Dictionary<int, int>();
        var queue = new Queue<int>();
        foreach (int id in links.Keys.Append(outside).Concat(regions.Keys).Distinct())
            if (IsOutside(regions, id, outside)) { depth[id] = 0; queue.Enqueue(id); }
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
