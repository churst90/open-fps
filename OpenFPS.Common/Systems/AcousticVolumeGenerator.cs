using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using System.Collections.Generic;
using System;
using System.Linq;

namespace OpenFPS.Common.Systems;

/// <summary>
/// Responsibility: Generates an AcousticMap from a list of world entities.
/// Optimized Strategy: "Region-First" - only voxelizes explicit room volumes to save CPU/Memory.
/// </summary>
public static class AcousticVolumeGenerator
{
    /// <param name="autoDiscoverPortals">
    /// When true, any region boundary the map did not describe gets a synthesized portal at the centre of
    /// that face. This is OFF by default: the placement is a guess, and a guessed portal puts a room's
    /// reverb and its doorway-localized arrival direction on the wrong wall — which sounds worse than an
    /// unopened boundary, because walls still transmit and occlude correctly on their own. Undescribed
    /// boundaries are always reported either way, with the exact portal the map is missing.
    /// </param>
    /// <param name="report">
    /// Whether to write what was found, and what the map is missing, to the console. A streamed map
    /// rebuilds this every time tiles arrive, and says it once, at the first build.
    /// </param>
    public static AcousticMap GenerateRegions(IEnumerable<EntityDefinition> entities, Vector3 mapSize, Vector3 minBound, float voxelResolution = 0.5f, float occlusionFloor = 0.05f, bool autoDiscoverPortals = false, bool report = true)
    {
        var console = report ? Console.Out : System.IO.TextWriter.Null;
        // 1. Initialize the map with the full map bounds
        var acousticMap = new AcousticMap(mapSize, minBound, voxelResolution);
        acousticMap.OcclusionFloor = occlusionFloor;
        
        var grid = acousticMap.VoxelGrid;
        Vector3 offset = minBound;

        // 2. Pre-fill the grid with "Outside" (GlobalEnvironmentId)
        // This avoids the expensive flood-fill of the outdoor void.
        int outsideRegionId = AcousticConstants.GlobalRegionId;
        var globalDef = entities.FirstOrDefault(e => e.EntityId == outsideRegionId && e.Region.RoomSize.X > 0);
        
        if (globalDef != null)
        {
            acousticMap.Regions[outsideRegionId] = globalDef.Region;
            acousticMap.RegionPositions[outsideRegionId] = globalDef.Transform.Position;
        }
        else
        {
            acousticMap.Regions[outsideRegionId] = new RegionComponent {
                FriendlyName = "Outside",
                IsIndoor = false,
                RoomSize = mapSize,
                ReverbTimeScale = 0.0f, // Default to dry unless a GlobalEnvironment entity exists
                // Six open faces, because that is what outdoors IS: the map-sized box has no surfaces
                // on it and nothing that leaves through one comes back. It used to be given six real
                // materials, which made the whole outdoors read as a sealed map-sized room to anything
                // that asked its boundary a question — and the only reason that never sounded like one
                // was a separate test for this region's id. See RoomAcoustics.
                Materials = new int[6]
            };
            acousticMap.RegionPositions[outsideRegionId] = Vector3.Zero;
        }
        acousticMap.GlobalEnvironmentId = outsideRegionId;

        // 3. Process Explicit Regions
        // We only voxelize the space INSIDE your defined rooms. Largest first, so where two overlap
        // the smaller one is written last and holds the overlap: a named spot inside a bigger zone, a
        // room inside a hall. The same rule SpatialService.GetRegionAt applies to the boxes themselves;
        // in entity order it was whichever the map happened to list last.
        foreach (var def in entities.Where(d => d.Region.RoomSize.X > 0)
                                    .OrderByDescending(d => d.Region.RoomSize.X * d.Region.RoomSize.Y * d.Region.RoomSize.Z))
        {
            if (def.Region.RoomSize.X > 0)
            {
                int regionId = def.EntityId;
                acousticMap.Regions[regionId] = def.Region;
                acousticMap.RegionPositions[regionId] = def.Transform.Position;
                acousticMap.RegionRotations[regionId] = def.Transform.Rotation;

                // A region that says it is indoors and names no surfaces is a contradiction, and the
                // consequence of it is silence: no closed boundary means no reverberation estimate, so
                // the room the map thinks it placed would be as dry as the field outside it and nothing
                // would say so. Exactly the shape of fault that let the speedway go without regions for
                // weeks — "not configured" and "working" look the same from everywhere else.
                if (def.Region.IsIndoor && RoomAcoustics.OpenFaceCount(def.Region) == 6)
                    console.WriteLine(
                        $"[WARNING] AcousticVolumeGenerator: region '{def.Region.FriendlyName}' (entity {regionId}) " +
                        "is marked indoors but declares no RoomMaterials, so it has no surfaces and cannot reverberate.");

                // Voxelize using the high-performance hierarchical OBB method
                grid.SetRegionOBB(def.Transform.Position, def.Region.RoomSize, def.Transform.Rotation, regionId);
            }
        }

        // A map that names nowhere is not broken, and it is not finished either.
        //
        // Regions are OPTIONAL: since a region's acoustics come from its own boundary rather than from
        // its existence, naming a place costs nothing and changes nothing, and a map with none of them
        // is simply one where everywhere is "Outside". That is fine for a test rig and useless for a
        // player who cannot see, which is the fault the speedway had for weeks without anything
        // noticing — a two-kilometre loop that answered "where am I" once and then never again.
        if (acousticMap.Regions.Count <= 1)
            console.WriteLine(
                "[WARNING] AcousticVolumeGenerator: this map has no named regions, so every place in it " +
                "answers to 'Outside'. Nothing else will report this.");

        // 4. AUTHORED PORTALS — the primary source of truth.
        // A portal entity carries the two regions it joins and the size of the opening. These are placed
        // by hand at the actual doorway, so they must be processed BEFORE any automatic guessing.
        int authoredCount = 0;
        foreach (var def in entities)
        {
            if (def.Portal.ApertureSize <= 0) continue;

            int rA = def.Portal.RegionAId;
            int rB = def.Portal.RegionBId;

            // Unlinked portal (both ends identical — typically the prefab default, or an author who
            // dropped a portal in a doorway without naming the rooms): probe the voxel grid outward
            // from the opening and take the first two distinct regions found.
            if (rA == rB)
            {
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, def.Transform.Rotation);
                Vector3[] searchDirs = { forward, -forward, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY };

                var foundRegions = new List<int>();
                foreach (var dir in searchDirs)
                {
                    // One sample just past the opening on each side. The octree answers GlobalRegionId
                    // for anything outside a room, so a single sample is enough and is deterministic.
                    int r = grid.GetRegionAt(def.Transform.Position + dir * 0.5f);
                    if (!foundRegions.Contains(r)) foundRegions.Add(r);
                    if (foundRegions.Count >= 2) break;
                }

                if (foundRegions.Count >= 2) { rA = foundRegions[0]; rB = foundRegions[1]; }
                else if (foundRegions.Count == 1) { rA = foundRegions[0]; rB = AcousticConstants.GlobalRegionId; }

                console.WriteLine(rA != rB
                    ? $"[AcousticMap] Portal {def.EntityId} had no region link; probed the voxel grid and joined region {rA} to {rB}."
                    : $"[AcousticMap] WARNING: portal {def.EntityId} at {def.Transform.Position} has no region link and probing found no rooms — it will be ignored. Set RegionAId/RegionBId on the map entity.");
            }

            if (rA == rB) continue;

            var portalComp = def.Portal;
            portalComp.RegionAId = rA;
            portalComp.RegionBId = rB;
            acousticMap.Portals[def.EntityId] = (portalComp, def.Transform.Position);
            authoredCount++;
        }

        // 4b. OPENINGS FROM THE GEOMETRY — every side of a room that is not closed in.
        // The walls round a room are real boxes, and wherever they leave a gap in one of its faces there
        // is an opening, exactly where the gap is and exactly its size: a tunnel's open ends, a building's
        // open side, a doorway with no door in it, a window with no glass. A door is the same opening with
        // a leaf in it, and its leaf stands shut in the gap here, so it is the door's (an authored portal)
        // and not found twice. See FaceOpenings.
        //
        // One opening per GAP, not one per pair of rooms. A lobby with a front door and an open side onto
        // the same street has both, and the street is heard through each from where each is (2026-10-02).
        // Until then only the first opening between two places was kept, so a building with a door to the
        // outdoors lost its open side.
        //
        // Only for STRUCTURES: a region with at least one wall. A named stretch of street is never
        // surveyed, so all six of its faces read open, and coupling every patch of open ground to its
        // neighbours is not what an opening is. Never the floor: that is the ground.
        int openingCount = AddFaceOpenings(acousticMap, entities);
        if (openingCount > 0)
            console.WriteLine($"[AcousticMap] {openingCount} opening(s) from the gaps in rooms' faces (open sides, tunnel mouths, doorways with no door).");

        // 5. UNDESCRIBED BOUNDARY AUDIT (and, only on request, synthesis).
        // Report every region boundary the map did not describe. Optionally fill it with a guessed portal
        // at the centre of the face — see the autoDiscoverPortals remarks: the guess is almost never where
        // the real doorway is, and a portal on the wrong wall is exactly what makes a room's reverb arrive
        // from the wrong direction. Reporting is unconditional; synthesizing is not.
        var linkedPairs = new HashSet<(int, int)>();
        foreach (var p in acousticMap.Portals.Values)
            linkedPairs.Add(PairKey(p.Portal.RegionAId, p.Portal.RegionBId));

        int virtualPortalId = -1000;
        int discoveredCount = 0;

        foreach (var r1 in acousticMap.Regions.Keys)
        {
            if (r1 == acousticMap.GlobalEnvironmentId) continue;
            if (!acousticMap.RegionPositions.TryGetValue(r1, out var pos1)) continue;
            var size1 = acousticMap.Regions[r1].RoomSize;

            // D: Use rotation-aware face normals so buildings rotated away from cardinal axes
            // have their actual wall faces probed, not just the world-axis directions.
            // Each face is probed just beyond ITS OWN half-extent — using the largest dimension for all
            // six faces (as this did previously) puts the probe metres past a thin room's short faces.
            Quaternion regionRot = acousticMap.RegionRotations.GetValueOrDefault(r1, Quaternion.Identity);
            (Vector3 dir, float halfExtent)[] faces =
            {
                (Vector3.Normalize(Vector3.Transform(Vector3.UnitX, regionRot)),  size1.X / 2f),
                (Vector3.Normalize(Vector3.Transform(-Vector3.UnitX, regionRot)), size1.X / 2f),
                (Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, regionRot)),  size1.Z / 2f),
                (Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, regionRot)), size1.Z / 2f),
                (Vector3.UnitY,  size1.Y / 2f),   // Vertical faces are never rotated horizontally
                (-Vector3.UnitY, size1.Y / 2f)
            };

            foreach (var (dir, halfExtent) in faces)
            {
                Vector3 edgePos = pos1 + (dir * (halfExtent + 0.5f));
                int r2 = grid.GetRegionAt(edgePos);
                if (r2 == r1 || r2 == 0) continue; // not a boundary

                var key = PairKey(r1, r2);
                if (!linkedPairs.Add(key)) continue; // already described (authored or discovered)

                string nameA = acousticMap.Regions.TryGetValue(r1, out var ra) ? ra.FriendlyName : r1.ToString();
                string nameB = r2 == acousticMap.GlobalEnvironmentId ? "Outside"
                    : acousticMap.Regions.TryGetValue(r2, out var rb) ? rb.FriendlyName : r2.ToString();

                if (!autoDiscoverPortals)
                {
                    console.WriteLine($"[AcousticMap] NOTE: '{nameA}' ({r1}) borders '{nameB}' ({r2}) with no portal authored, " +
                                      $"so the two are not acoustically coupled. If there is a real opening there, add a portal " +
                                      $"entity at the doorway with RegionAId={r1}, RegionBId={r2}.");
                    continue;
                }

                console.WriteLine($"[AcousticMap] WARNING: no portal authored between '{nameA}' ({r1}) and '{nameB}' ({r2}). " +
                                  $"Guessing one at the centre of a face, {edgePos} — reverb and occlusion will arrive from the wrong direction. " +
                                  $"Add a portal entity at the real doorway with RegionAId={r1}, RegionBId={r2}.");

                acousticMap.Portals[virtualPortalId--] = (
                    new PortalComponent { RegionAId = r1, RegionBId = r2, ApertureSize = 2.0f },
                    edgePos);
                discoveredCount++;
            }
        }

        console.WriteLine($"[AcousticMap] {acousticMap.Regions.Count - 1} region(s), {authoredCount} authored portal(s), {discoveredCount} guessed.");

        return acousticMap;
    }

    /// <summary>The first id the openings found in faces take; each next one is one lower. OpeningGraph
    /// tells them from authored portals by it.</summary>
    public const int FirstFaceOpeningId = -2000;

    /// <summary>
    /// Puts on the map an opening for every gap in the faces of its structures (or of only
    /// <paramref name="rooms"/>, replacing what those rooms had), from the solid boxes in
    /// <paramref name="entities"/>. Returns how many it added.
    ///
    /// The aperture is the width of a square of the gap's area: the bus's leak goes as aperture over
    /// distance, and the pressure through an opening goes as the square root of its area. The gap's own
    /// rectangle is in <see cref="AcousticMap.OpeningFrames"/>.
    /// </summary>
    public static int AddFaceOpenings(AcousticMap map, IEnumerable<EntityDefinition> entities, IReadOnlyCollection<int>? rooms = null)
    {
        float res = map.VoxelResolution;

        // The solid boxes, as the scene has them, with each door's leaf standing shut in its doorway.
        var solids = new List<(FaceOpenings.Box Box, Vector3 Min, Vector3 Max)>();
        foreach (var def in entities)
        {
            if (def == null || !def.Collider.IsSolid || def.Collider.Shape != ColliderShape.Box) continue;
            if (def.Region.RoomSize.X > 0f || def.Moves) continue;
            if (!string.IsNullOrEmpty(def.SoundEmitter.SoundId)) continue;
            var size = def.Collider.Size;
            if (size.X <= 0f || size.Y <= 0f || size.Z <= 0f) continue;
            bool leaf = def.Portal.RegionAId != 0 || def.Portal.RegionBId != 0;
            Vector3 at = def.Transform.Position;
            Quaternion rot = def.Transform.Rotation == default ? Quaternion.Identity : def.Transform.Rotation;
            if (leaf && def.Portal.OpeningRotation != default) { at = def.Portal.OpeningCentre; rot = def.Portal.OpeningRotation; }
            var half = FaceOpenings.AxisAlignedHalfExtents(size * 0.5f, rot);
            solids.Add((new FaceOpenings.Box(at, size, rot), at - half, at + half));
        }

        var places = new List<(FaceOpenings.Place Place, Vector3 Min, Vector3 Max)>();
        foreach (var (id, region) in map.Regions)
        {
            if (id == AcousticConstants.GlobalRegionId || id == map.GlobalEnvironmentId || region.RoomSize.X <= 0f) continue;
            if (!map.RegionPositions.TryGetValue(id, out var c)) continue;
            var q = map.RegionRotations.GetValueOrDefault(id, Quaternion.Identity);
            if (q == default) q = Quaternion.Identity;
            var half = FaceOpenings.AxisAlignedHalfExtents(region.RoomSize * 0.5f, q);
            places.Add((new FaceOpenings.Place(id, c, region.RoomSize, q), c - half, c + half));
        }

        // What is already there: the authored portals, and the openings of rooms not being redone.
        var frames = new Dictionary<int, OpeningFrame>(map.OpeningFrames);
        var portals = new Dictionary<int, (PortalComponent Portal, Vector3 Position)>(map.Portals);
        if (rooms != null)
            foreach (var (id, frame) in map.OpeningFrames)
                if (rooms.Contains(frame.Room)) { frames.Remove(id); portals.Remove(id); }
        int nextId = FirstFaceOpeningId;
        foreach (int id in portals.Keys) if (id <= FirstFaceOpeningId) nextId = Math.Min(nextId, id - 1);

        var known = new Dictionary<(int, int), List<(Vector3 Centre, OpeningFrame? Frame)>>();
        foreach (var (id, (portal, position)) in portals)
        {
            OpeningFrame? frame = frames.TryGetValue(id, out var fr) ? fr : null;
            Vector3 centre = frame?.Centre ?? (portal.OpeningRotation != default ? portal.OpeningCentre : position);
            Known(known, portal.RegionAId, portal.RegionBId).Add((centre, frame));
        }

        int added = 0;
        var nearSolids = new List<FaceOpenings.Box>();
        var nearPlaces = new List<FaceOpenings.Place>();
        // The walls and the places filed by where they stand, so each room asks about its neighbours
        // and not the whole map (BoxColumns): the same ones, in the same order, as a scan of all.
        var solidColumns = new BoxColumns(10f);
        for (int i = 0; i < solids.Count; i++) solidColumns.Add(i, solids[i].Min, solids[i].Max);
        var placeColumns = new BoxColumns(10f);
        for (int i = 0; i < places.Count; i++) placeColumns.Add(i, places[i].Min, places[i].Max);
        var candidates = new List<int>();
        foreach (var (room, rMin, rMax) in places)
        {
            if (rooms != null && !rooms.Contains(room.Id)) continue;
            var region = map.Regions[room.Id];
            if (RoomAcoustics.OpenFaceCount(region) == 6) continue;

            float pad = FaceOpenings.WallReachMetres + 0.01f;
            nearSolids.Clear();
            solidColumns.Collect(rMin - new Vector3(pad), rMax + new Vector3(pad), candidates);
            foreach (int i in candidates)
            {
                var (box, min, max) = solids[i];
                if (Overlaps(min, max, rMin - new Vector3(pad), rMax + new Vector3(pad))) nearSolids.Add(box);
            }
            // Beyond a face as far as the thickest wall near it and two voxels more.
            float wall = 0f;
            foreach (var b in nearSolids) wall = MathF.Max(wall, MathF.Min(b.Size.X, MathF.Min(b.Size.Y, b.Size.Z)));
            float out_ = MathF.Min(wall, 10f) + FaceOpenings.WallReachMetres + 2f * res + 0.01f;
            nearPlaces.Clear();
            placeColumns.Collect(rMin - new Vector3(out_), rMax + new Vector3(out_), candidates);
            foreach (int i in candidates)
            {
                var (place, min, max) = places[i];
                if (Overlaps(min, max, rMin - new Vector3(out_), rMax + new Vector3(out_))) nearPlaces.Add(place);
            }

            foreach (var gap in FaceOpenings.Find(room, region.Materials, nearSolids, nearPlaces, res, map.GlobalEnvironmentId))
            {
                var frame = new OpeningFrame(room.Id, gap.Centre, gap.Rotation, new Vector3(gap.Width, gap.Height, gap.Depth));
                var list = Known(known, room.Id, gap.Beyond);
                bool seen = false;
                foreach (var (centre, other) in list)
                    if (Inside(centre, frame, res) || (other is { } o && Inside(frame.Centre, o, res))) { seen = true; break; }
                if (seen) continue;
                list.Add((frame.Centre, frame));
                int id = nextId--;
                portals[id] = (new PortalComponent { RegionAId = room.Id, RegionBId = gap.Beyond, ApertureSize = MathF.Sqrt(gap.Area) }, gap.Centre);
                frames[id] = frame;
                added++;
            }
        }
        map.OpeningFrames = frames;
        map.Portals = portals;
        return added;

        static List<(Vector3, OpeningFrame?)> Known(Dictionary<(int, int), List<(Vector3, OpeningFrame?)>> known, int a, int b)
        {
            var key = PairKey(a, b);
            if (!known.TryGetValue(key, out var l)) known[key] = l = new List<(Vector3, OpeningFrame?)>();
            return l;
        }
        static bool Overlaps(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax)
            => aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y && aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    }

    /// <summary>Whether a point lies in an opening's rectangle — within a voxel of it in its plane, and
    /// within the wall's depth and two voxels of it through — so the same gap found from its other side,
    /// or a doorway authored in it, is one opening.</summary>
    private static bool Inside(Vector3 point, in OpeningFrame frame, float res)
    {
        var local = Vector3.Transform(point - frame.Centre, Quaternion.Inverse(frame.Rotation));
        return MathF.Abs(local.X) <= frame.Size.X * 0.5f + res
            && MathF.Abs(local.Y) <= frame.Size.Y * 0.5f + res
            && MathF.Abs(local.Z) <= frame.Size.Z * 0.5f + 2f * res;
    }

    /// <summary>Order-independent key for a region boundary, so A→B and B→A are the same pair.</summary>
    private static (int, int) PairKey(int a, int b) => a <= b ? (a, b) : (b, a);

    /// <summary>
    /// Performs a delta update for a single region entity that has moved or changed.
    /// </summary>
    public static void UpdateRegion(AcousticMap map, int regionId, Vector3 pos, Quaternion rot, RegionComponent reg)
    {
        var grid = map.VoxelGrid;
        
        // 1. Re-voxelize at NEW position/rotation (Octree handles overwriting)
        grid.SetRegionOBB(pos, reg.RoomSize, rot, regionId);
        
        // 2. Update metadata
        map.RegionPositions[regionId] = pos;
        map.RegionRotations[regionId] = rot;
        map.Regions[regionId] = reg;
    }
}
