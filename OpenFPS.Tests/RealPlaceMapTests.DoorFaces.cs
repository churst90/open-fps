using System.Numerics;
using OpenFPS.Acoustics;
using OpenFPS.Audio.Acoustics;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

public partial class RealPlaceMapTests
{
    /// <summary>
    /// Where each door of a place hangs in its wall (Resonance's 1.2): how far its leaf stands off the wall's middle,
    /// and for the one furthest off, what a route delivers round the doorway's corners. For the record; the rule it
    /// is heard by is held by ResonanceMaybesGeometryTests.
    /// </summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void Where_each_door_hangs_in_its_wall(string id)
        => DoorFaceSurvey.Say(_o, id, _maps.Get(id).World);
}

/// <summary>Each door's leaf against its wall's middle, and a route round one door's corners.</summary>
internal static class DoorFaceSurvey
{
    private static float Db(float g) => 20f * MathF.Log10(MathF.Max(1e-6f, g));

    /// <summary>Each door's opening and how far its leaf, shut, stands off the wall's middle, metres.</summary>
    public static List<(OpeningRoutes.Opening Opening, float Off)> Offsets(WorldSnapshot world, OpeningRoutes routes)
    {
        var list = new List<(OpeningRoutes.Opening, float)>();
        foreach (var o in routes.Openings)
        {
            if (o.Kind != OpeningRoutes.DoorKind || !world.Entities.TryGetValue(o.Id, out var e)) continue;
            var leaf = EntityAcoustics.LeafOf(e);
            list.Add((o, Vector3.Dot(o.Centre - leaf.DoorwayCentre, o.Normal)));
        }
        return list;
    }

    /// <summary>
    /// The doors that take the outdoor seal by a place with surfaces that is not indoors (a roof, a terrace) rather than
    /// by the outdoors (OpeningRoutes.IsOpenAir, 2026-10-17): until then each took the gaps of a door between two rooms.
    /// Counted by what the open-air place is called, its numbers left out, and by whether the door declares its gaps.
    /// </summary>
    public static void OpenAir(ITestOutputHelper output, string id, WorldSnapshot world, OpeningRoutes routes)
    {
        var map = world.AcousticMap!;
        string Name(int node) => map.Regions.TryGetValue(node, out var r) ? r.FriendlyName : node.ToString();
        string Kind(string name) => new string(name.Where(c => !char.IsDigit(c)).ToArray()).Trim();
        var doors = routes.Openings.Where(o => o.Kind == OpeningRoutes.DoorKind).ToList();
        var changed = doors.Where(o => o.NodeA != OpeningRoutes.Outside && o.NodeB != OpeningRoutes.Outside
                                       && (routes.IsOpenAir(o.NodeA) || routes.IsOpenAir(o.NodeB))).ToList();
        bool Declared(OpeningRoutes.Opening o)
            => world.Entities.TryGetValue(o.Id, out var e) && e.Definition.Portal is { } p && p.GapMetres > 0f && p.UndercutMetres > 0f;
        output.WriteLine($"{id}: {doors.Count} doors; to the outdoors {doors.Count(o => o.NodeA == OpeningRoutes.Outside || o.NodeB == OpeningRoutes.Outside)}, "
                       + $"to a place in the open air {changed.Count} ({changed.Count(Declared)} declaring their own gaps), "
                       + $"between two places indoors {doors.Count - changed.Count - doors.Count(o => o.NodeA == OpeningRoutes.Outside || o.NodeB == OpeningRoutes.Outside)}");
        foreach (var g in changed.GroupBy(o => (Kind(Name(routes.IsOpenAir(o.NodeA) ? o.NodeA : o.NodeB)),
                                               Kind(Name(routes.IsOpenAir(o.NodeA) ? o.NodeB : o.NodeA)), Declared(o)))
                                 .OrderByDescending(g => g.Count()))
        {
            var o = g.First();
            string door = world.Entities.TryGetValue(o.Id, out var e) ? e.Definition.Identity.Name : "";
            output.WriteLine($"  {g.Count()} between '{g.Key.Item2}' and open-air '{g.Key.Item1}'{(g.Key.Item3 ? " (own gaps)" : "")}: "
                           + $"e.g. door {o.Id} '{door}' at ({o.Centre.X:F2}, {o.Centre.Z:F2}, floor {o.Centre.Y - o.HalfHeight:F2}), "
                           + $"its gaps {10f * MathF.Log10(MathF.Max(1e-12f, o.Gaps.Y)):F1} dB mid");
        }
    }

    public static void Say(ITestOutputHelper output, string id, WorldSnapshot world)
    {
        var acoustics = new SpatialAcoustics();
        var routes = acoustics.RoutesFor(world)!;
        var offs = Offsets(world, routes);
        int mid = offs.Count(x => MathF.Abs(x.Off) < 0.01f), near = offs.Count(x => MathF.Abs(x.Off) is >= 0.01f and < 0.05f);
        output.WriteLine($"{id}: {offs.Count} doors; leaf within 1 cm of its wall's middle {mid}, 1 to 5 cm off {near}, 5 cm or more {offs.Count - mid - near}");
        // Which way round each opening's places are named (OpeningRoutes.FieldAt took the first along the normal until
        // 2026-10-17).
        int along = routes.Openings.Count(o => o.ASide > 0), against = routes.Openings.Count(o => o.ASide < 0);
        output.WriteLine($"{id}: {routes.Openings.Count} openings; the first-named place along the normal {along}, against it {against}, "
                       + $"sides not as named {routes.Openings.Count - along - against}"
                       + $" (doors {routes.Openings.Count(o => o.Kind == OpeningRoutes.DoorKind && o.ASide < 0)} against)");
        OpenAir(output, id, world, routes);
        foreach (var (o, off) in offs.OrderByDescending(x => MathF.Abs(x.Off)).Take(5))
            output.WriteLine($"  door {o.Id} at ({o.Centre.X:F2}, {o.Centre.Z:F2}, floor {o.Centre.Y - o.HalfHeight:F2}): leaf {off:F3} m off the middle of a wall {2 * o.HalfDepth:F2} m deep");

        // The door furthest off its wall's middle, heard round its corners: a source two metres out in one room and a
        // metre and more to one side, the ear just past the other face and further to the same side.
        foreach (var (o, _) in offs.OrderByDescending(x => MathF.Abs(x.Off)).Take(3))
        {
            var leafAt = EntityAcoustics.LeafOf(world.Entities[o.Id]).DoorwayCentre;
            foreach (float s in new[] { 1f, -1f })
            foreach (float u in new[] { 1f, -1f })
            {
                Vector3 up = o.Up * 0.45f;
                Vector3 src = leafAt - o.Normal * (s * 2.3f) + o.Across * (u * (o.HalfWidth + 1f)) + up;
                Vector3 lis = leafAt + o.Normal * (s * 0.65f) + o.Across * (u * (o.HalfWidth + 1.5f)) + up;
                int rs = acoustics.GetRegionAt(world, src), rl = acoustics.GetRegionAt(world, lis);
                int ns = routes.NodeOf(rs), nl = routes.NodeOf(rl);
                bool across = (ns == o.NodeA && nl == o.NodeB) || (ns == o.NodeB && nl == o.NodeA);
                if (!across) continue;
                if (!routes.Route(src, rs, lis, rl, out var a)) continue;
                output.WriteLine($"  door {o.Id}: ({src.X:F2}, {src.Z:F2}, {src.Y:F2}) to ({lis.X:F2}, {lis.Z:F2}, {lis.Y:F2}) "
                               + $"[x east, y north, height]: {Db(a.Low):F1}/{Db(a.Mid):F1}/{Db(a.High):F1} dB by {a.Routes} routes");
            }
        }
    }
}
