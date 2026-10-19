using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Geometry;
using OpenFPS.Acoustics;

namespace OpenFPS.Client.Core;

/// <summary>
/// The client's geometry queries: the acoustics' questions (occlusion, the room a point is in, the first
/// surface along a ray), which the sound library answers over the snapshot as its world input
/// (<see cref="AcousticRays"/>), and the game's own snapshots' view of the sight rays
/// (<see cref="SightRays"/>: the queries over the scene view, unwrapped for the scope, the session and
/// the tests — the aids ask SightRays directly).
/// </summary>
public class SpatialService
{
    public int OwnEntityId { get; set; } = -1;

    /// <summary>Whether this world's static solids are answered by its triangle world.</summary>
    public static bool UsesTriangles(IAcousticWorld world) => SightRays.UsesTriangles(world);

    // ═══ The acoustics' questions, answered by the library (AcousticRays) ═════════════════════════

    /// <summary>How blocked the line between two points is, 0 to 1, and the bleed (the mean of what the
    /// bands let through).</summary>
    public float GetOcclusionFactor(WorldSnapshot world, Vector3 start, Vector3 end, out float bleed, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
        => AcousticRays.OcclusionFactor(world, start, end, out bleed, ignoreEntityId, ignoreEntityId2);

    /// <summary><see cref="GetOcclusionData"/> averaged over five rays in a cross round the end point
    /// (<see cref="AcousticRays.MultiPointOcclusion"/>).</summary>
    public void GetMultiPointOcclusionData(WorldSnapshot world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed, out float eqLow, out float eqMid, out float eqHigh, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
        => AcousticRays.MultiPointOcclusion(world, start, end, out maxBlock, out cumulativeBleed, out eqLow, out eqMid, out eqHigh, ignoreEntityId, ignoreEntityId2);

    /// <summary>What every wall between two points lets through, per band (<see cref="AcousticRays.Occlusion"/>).</summary>
    public void GetOcclusionData(WorldSnapshot world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed, out float eqLow, out float eqMid, out float eqHigh, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
        => AcousticRays.Occlusion(world, start, end, out maxBlock, out cumulativeBleed, out eqLow, out eqMid, out eqHigh, ignoreEntityId, ignoreEntityId2);

    /// <summary>The first solid along each of <paramref name="directions"/> (<see cref="AcousticRays.FirstSurfaces"/>).</summary>
    public void RaycastAll(WorldSnapshot world, Vector3 start, Vector3[] directions, float maxDist,
                           float[] distances, float[] absorptions, string[] materials, bool staticOnly = false,
                           Vector3[]? normals = null, int count = -1)
        => AcousticRays.FirstSurfaces(world, start, directions, maxDist, distances, absorptions, materials, staticOnly, normals, count);

    /// <summary>The acoustic region a position is in (<see cref="AcousticRays.RegionAt"/>).</summary>
    public int GetRegionAt(WorldSnapshot world, Vector3 position) => AcousticRays.RegionAt(world, position);

    /// <summary>The first solid along a ray and its material, this client's own body and
    /// <paramref name="ignoreEntityId"/> not counted (<see cref="AcousticRays.FirstMaterial"/>).</summary>
    public bool RaycastMaterial(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out float distance, out Vector3 normal, out string material, int ignoreEntityId = -1)
        => AcousticRays.FirstMaterial(world, start, dir, maxDist, out distance, out normal, out material, ignoreEntityId, OwnEntityId);

    // ═══ The sight rays, over the view (SightRays), for the game's snapshots ══════════════════════

    /// <summary>The game's own body and snapshots: <see cref="SightRays.CastSight"/> unwrapped. The
    /// scope and the turn narration read it.</summary>
    public bool CastSight(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity,
                          out float hitDistance, Func<EntitySnapshot, bool>? skip = null, bool glassBlocks = false)
    {
        Func<AcousticBody, bool>? bodySkip = skip == null ? null : b => world.Entities.TryGetValue(b.Id, out var e) && skip(e);
        bool found = SightRays.CastSight(world, start, dir, maxDist, out var body, out hitDistance, bodySkip, glassBlocks, OwnEntityId);
        hitEntity = found && world.Entities.TryGetValue(body.Id, out var hit) ? hit : default;
        return found;
    }

    /// <summary>The game's own: <see cref="SightRays.SurfaceFlagsAlong"/>.</summary>
    public SurfaceFlags SurfaceFlagsAlong(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist)
        => SightRays.SurfaceFlagsAlong(world, start, dir, maxDist, OwnEntityId);

    /// <summary>The game's own: <see cref="SightRays.UnderOpenSky"/>.</summary>
    public bool UnderOpenSky(WorldSnapshot world, Vector3 at, float metres) => SightRays.UnderOpenSky(world, at, metres, OwnEntityId);

    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity, out float hitDistance)
        => RaycastSingle(world, start, dir, maxDist, null, out hitEntity, out hitDistance);

    /// <summary>The game's own snapshots: <see cref="SightRays.RaycastSingle"/> unwrapped.</summary>
    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, Func<EntitySnapshot, bool>? accept,
                              out EntitySnapshot hitEntity, out float hitDistance)
    {
        Func<AcousticBody, bool>? bodyAccept = accept == null ? null : b => world.Entities.TryGetValue(b.Id, out var e) && accept(e);
        bool found = SightRays.RaycastSingle(world, start, dir, maxDist, bodyAccept, out var body, out hitDistance);
        hitEntity = found && world.Entities.TryGetValue(body.Id, out var hit) ? hit : default;
        return found;
    }
}

/// <summary>Where the listener stands, for naming it: the game's question of the sound's places.</summary>
public static class Zones
{
    /// <summary>The place a point is in by the boxes alone (a doorway is in none), for naming where the
    /// listener stands, not for sound. A named part of a room (a flight of stairs, a landing) answers
    /// here; it is not a room, so the acoustics' <c>GetRegionAt</c> never does.</summary>
    public static int GetZoneAt(this OpenFPS.Audio.Acoustics.SpatialAcoustics acoustics, WorldSnapshot world, Vector3 position)
        => NamedPlaces.At(world, position) ?? acoustics.GetRoomAt(world, position);
}
