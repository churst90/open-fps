using System.Numerics;
using OpenFPS.Acoustics;
using OpenFPS.Audio.SteamAudio;

namespace OpenFPS.Audio.Acoustics;

/// <summary>
/// The world as <see cref="OpeningRoutes"/> wants it: the scene's boxes with the door leaves marked, and
/// every opening the map declares — each door's doorway (where its leaf stands when shut, which the
/// host gives with the leaf: <see cref="DoorLeaf.DoorwayCentre"/>), each authored portal, and each gap in a
/// structure's faces, as big as the gap.
/// </summary>
public static class OpeningGraph
{
    /// <summary>Every door leaf the world holds, by id.</summary>
    public static HashSet<int> LeavesOf(IAcousticWorld world)
    {
        var all = new List<DoorLeaf>();
        world.DoorLeaves(all);
        var leaves = new HashSet<int>();
        foreach (var leaf in all) leaves.Add(leaf.Id);
        return leaves;
    }

    /// <summary>
    /// The graph for the scene the acoustic triangle store holds (docs/GEOMETRY.md stage 1): the same
    /// openings and places as <see cref="Build(IAcousticWorld, IReadOnlyList{SteamAudioScene.Box}, Func{Vector3, int}?, IReadOnlyList{OpenFPS.Geometry.SolidSpec}?)"/>,
    /// the boxes asked of the store's tiles, and whatever did not change since the last build kept in
    /// <paramref name="cache"/>.
    /// </summary>
    public static OpeningRoutes Build(IAcousticWorld world, OpenFPS.Geometry.TriangleWorld scene, Func<Vector3, int>? regionAt,
                                      OpeningRoutes.TileCache cache)
    {
        var leaves = new List<DoorLeaf>();
        world.DoorLeaves(leaves);
        return OpeningRoutes.Build(scene, world.Rooms, Declared(world.Rooms, leaves), regionAt, cache);
    }

    /// <summary>The graph for a scene built from <paramref name="boxes"/> (as <see cref="SteamAudioScene.BoxesFromWorld"/>
    /// makes them, so the leaves stand where the scene has them), with the world's places and openings, and
    /// its tiles of ground (<paramref name="terrains"/>, IAcousticWorld.TerrainSolids), which are no box.</summary>
    public static OpeningRoutes Build(IAcousticWorld world, IReadOnlyList<SteamAudioScene.Box> boxes, Func<Vector3, int>? regionAt,
                                      IReadOnlyList<OpenFPS.Geometry.SolidSpec>? terrains)
    {
        var leafList = new List<DoorLeaf>();
        world.DoorLeaves(leafList);
        var leaves = new HashSet<int>();
        foreach (var leaf in leafList) leaves.Add(leaf.Id);

        var solids = new List<OpeningRoutes.Solid>(boxes.Count);
        foreach (var b in boxes)
            solids.Add(new OpeningRoutes.Solid(b.Center, b.Size, b.Rotation, b.Material, b.Build,
                                               b.EntityId != 0 && leaves.Contains(b.EntityId)));

        return OpeningRoutes.Build(solids, world.Rooms, Declared(world.Rooms, leafList), regionAt, terrains);
    }

    private static IEnumerable<OpeningRoutes.Declared> Declared(AcousticMap? map, List<DoorLeaf> leafList)
    {
        var leaves = new HashSet<int>();
        foreach (var leaf in leafList)
        {
            // The doorway, not the leaf: the pose the host recorded for it shut.
            if (!leaves.Add(leaf.Id)) continue;
            yield return new OpeningRoutes.Declared(leaf.Id, "door", leaf.DoorwayCentre, leaf.DoorwayRotation, leaf.Size, 0f,
                                                    leaf.RoomA, leaf.RoomB);
        }

        if (map == null) yield break;
        foreach (var (id, (portal, position)) in map.Portals)
        {
            if (leaves.Contains(id)) continue;
            // The gaps in rooms' faces (AcousticVolumeGenerator: open sides, tunnel mouths, doorways with
            // no door) carry their own rectangle; an authored portal is as wide as its author said.
            bool face = id <= OpenFPS.Acoustics.Systems.AcousticVolumeGenerator.FirstFaceOpeningId;
            // An authored opening with no leaf carries the frame its rooms' floods met (AcousticVolumeGenerator.ApplyFloodOpenings).
            if (map.OpeningFrames.TryGetValue(id, out var frame))
            {
                yield return new OpeningRoutes.Declared(id, face ? OpeningRoutes.FaceKind : id > 0 ? "doorway" : "guessed opening",
                                                        frame.Centre, frame.Rotation, frame.Size, 0f, portal.RegionAId, portal.RegionBId);
                continue;
            }
            yield return new OpeningRoutes.Declared(id, face ? OpeningRoutes.FaceKind : id > 0 ? "doorway" : "guessed opening",
                                                    position, default, Vector3.Zero, face ? 0f : portal.ApertureSize,
                                                    portal.RegionAId, portal.RegionBId);
        }
    }
}
