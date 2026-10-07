using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// The world as <see cref="OpeningRoutes"/> wants it: the scene's boxes with the door leaves marked, and
/// every opening the map declares — each door's doorway (where its leaf stands when shut, which the
/// server sends in the portal: <see cref="OpenFPS.Common.Components.PortalComponent.OpeningRotation"/>),
/// each authored portal, and each gap in a structure's faces, as big as the gap.
/// </summary>
public static class OpeningGraph
{
    /// <summary>A door leaf: a solid box that is also a portal. The server gives every door a portal
    /// (PrefabRepository), with both sides the outside when the map names no rooms, so a door is told by
    /// HAVING one, not by its two sides differing. Movers are not in the scene at all.</summary>
    public static bool IsDoorLeaf(EntityDefinition? def)
        => def != null && def.Collider.IsSolid && !def.Moves
           && (def.Portal.RegionAId != 0 || def.Portal.RegionBId != 0);

    /// <summary>Every door leaf the world holds, by id.</summary>
    public static HashSet<int> LeavesOf(WorldSnapshot world)
    {
        var leaves = new HashSet<int>();
        foreach (var snap in world.Entities.Values)
            if (IsDoorLeaf(snap.Definition)) leaves.Add(snap.Id);
        return leaves;
    }

    /// <summary>
    /// The graph for the scene the acoustic triangle store holds (docs/GEOMETRY.md stage 1): the same
    /// openings and places as <see cref="Build(WorldSnapshot, IReadOnlyList{SteamAudioScene.Box}, Func{Vector3, int}?)"/>,
    /// the boxes asked of the store's tiles, and whatever did not change since the last build kept in
    /// <paramref name="cache"/>.
    /// </summary>
    public static OpeningRoutes Build(WorldSnapshot world, OpenFPS.Common.Geometry.TriangleWorld scene, Func<Vector3, int>? regionAt,
                                      OpeningRoutes.TileCache cache)
        => OpeningRoutes.Build(scene, world.AcousticMap, Declared(world, LeavesOf(world)), regionAt, cache);

    /// <summary>The graph for a scene built from <paramref name="boxes"/> (as <see cref="SteamAudioScene.BoxesFromWorld"/>
    /// makes them, so the leaves stand where the scene has them), with the world's places and openings.</summary>
    public static OpeningRoutes Build(WorldSnapshot world, IReadOnlyList<SteamAudioScene.Box> boxes, Func<Vector3, int>? regionAt)
    {
        var leaves = LeavesOf(world);

        var solids = new List<OpeningRoutes.Solid>(boxes.Count);
        foreach (var b in boxes)
            solids.Add(new OpeningRoutes.Solid(b.Center, b.Size, b.Rotation, b.Material, b.Build,
                                               b.EntityId != 0 && leaves.Contains(b.EntityId)));

        return OpeningRoutes.Build(solids, world.AcousticMap, Declared(world, leaves), regionAt);
    }

    private static IEnumerable<OpeningRoutes.Declared> Declared(WorldSnapshot world, HashSet<int> leaves)
    {
        foreach (int id in leaves)
        {
            if (!world.Entities.TryGetValue(id, out var snap)) continue;
            var def = snap.Definition;
            // The doorway, not the leaf: the pose the server recorded for it shut. A definition from
            // before that existed carries the pose it was defined in, which is shut at load.
            Vector3 centre; Quaternion rotation;
            if (def.Portal.OpeningRotation != default) { centre = def.Portal.OpeningCentre; rotation = def.Portal.OpeningRotation; }
            else if (def.Transform.Rotation != default) { centre = def.Transform.Position; rotation = def.Transform.Rotation; }
            else { centre = snap.Transform.Position; rotation = snap.Transform.Rotation; }
            yield return new OpeningRoutes.Declared(id, "door", centre, rotation, def.Collider.Size, 0f,
                                                    def.Portal.RegionAId, def.Portal.RegionBId);
        }

        var map = world.AcousticMap;
        if (map == null) yield break;
        foreach (var (id, (portal, position)) in map.Portals)
        {
            if (leaves.Contains(id)) continue;
            // The gaps in rooms' faces (AcousticVolumeGenerator: open sides, tunnel mouths, doorways with
            // no door) carry their own rectangle; an authored portal is as wide as its author said.
            bool face = id <= OpenFPS.Common.Systems.AcousticVolumeGenerator.FirstFaceOpeningId;
            if (face && map.OpeningFrames.TryGetValue(id, out var frame))
            {
                yield return new OpeningRoutes.Declared(id, OpeningRoutes.FaceKind, frame.Centre, frame.Rotation,
                                                        frame.Size, 0f, portal.RegionAId, portal.RegionBId);
                continue;
            }
            yield return new OpeningRoutes.Declared(id, face ? OpeningRoutes.FaceKind : id > 0 ? "doorway" : "guessed opening",
                                                    position, default, Vector3.Zero, face ? 0f : portal.ApertureSize,
                                                    portal.RegionAId, portal.RegionBId);
        }
    }
}
