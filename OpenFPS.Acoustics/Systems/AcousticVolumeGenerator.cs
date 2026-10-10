using System.Numerics;
using System;
using System.Linq;

namespace OpenFPS.Common.Systems;

/// <summary>Builds an AcousticMap from the world's rooms, openings and solids, voxelising only the regions'
/// own volumes. The host lists them (open-fps: EntityAcoustics, from the entities, in entity order); the order
/// decides which of two equal rooms holds their overlap and which ids openings found in faces take.</summary>
public static class AcousticVolumeGenerator
{
    /// <param name="autoDiscoverPortals">Give every boundary the map did not describe a portal at the face's
    /// centre. Off by default: a guessed portal puts a room's reverb and its doorway direction on the wrong
    /// wall, worse than none (walls still transmit and occlude). Undescribed boundaries are always reported.</param>
    /// <param name="report">Write what was found and what is missing to the console: a streamed map rebuilds
    /// this as tiles arrive and says it once, at the first build.</param>
    /// <param name="floods">The rooms' floods (RoomFlood): a flooded room's voxels are where its flood is, not its
    /// box, and the map carries them for "which room is this in".</param>
    public static AcousticMap GenerateRegions(IEnumerable<PlacedRoom> rooms, IEnumerable<PlacedOpening> openings, IEnumerable<FaceOpenings.Box> solids,
                                              Vector3 mapSize, Vector3 minBound, float voxelResolution = 0.5f, float occlusionFloor = 0.05f, bool autoDiscoverPortals = false, bool report = true,
                                              OpenFPS.Common.Geometry.RoomCells? floods = null)
    {
        var console = report ? Console.Out : System.IO.TextWriter.Null;
        var acousticMap = new AcousticMap(mapSize, minBound, voxelResolution);
        acousticMap.OcclusionFloor = occlusionFloor;
        
        var grid = acousticMap.VoxelGrid;
        Vector3 offset = minBound;

        // Everything not in a region is "Outside": no flood fill of the outdoor void.
        int outsideRegionId = AcousticConstants.GlobalRegionId;
        var globalDef = rooms.Where(r => r.Id == outsideRegionId && r.Room.RoomSize.X > 0).Cast<PlacedRoom?>().FirstOrDefault();
        
        if (globalDef is { } global)
        {
            acousticMap.Regions[outsideRegionId] = global.Room;
            acousticMap.RegionPositions[outsideRegionId] = global.Position;
        }
        else
        {
            acousticMap.Regions[outsideRegionId] = new Room {
                FriendlyName = "Outside",
                IsIndoor = false,
                RoomSize = mapSize,
                ReverbTimeScale = 0.0f, // Default to dry unless a GlobalEnvironment entity exists
                // Six open faces: six real materials made the outdoors a sealed map-sized room to anything
                // that asked its boundary (RoomAcoustics).
                Materials = new int[6]
            };
            acousticMap.RegionPositions[outsideRegionId] = Vector3.Zero;
        }
        acousticMap.GlobalEnvironmentId = outsideRegionId;

        // Largest first, so where two overlap the smaller holds the overlap (a room inside a hall), the
        // rule SpatialService.GetRegionAt applies; in entity order the map's last listed won.
        foreach (var def in rooms.Where(d => d.Room.RoomSize.X > 0)
                                 .OrderByDescending(d => d.Room.RoomSize.X * d.Room.RoomSize.Y * d.Room.RoomSize.Z))
        {
            if (def.Room.RoomSize.X > 0)
            {
                int regionId = def.Id;
                acousticMap.Regions[regionId] = def.Room;
                acousticMap.RegionPositions[regionId] = def.Position;
                acousticMap.RegionRotations[regionId] = def.Rotation;

                // Indoors with no surfaces is as dry as the field outside, and nothing else would say so.
                if (def.Room.IsIndoor && RoomAcoustics.OpenFaceCount(def.Room) == 6)
                    console.WriteLine(
                        $"[WARNING] AcousticVolumeGenerator: region '{def.Room.FriendlyName}' (entity {regionId}) " +
                        "is marked indoors but declares no RoomMaterials, so it has no surfaces and cannot reverberate.");

                if (floods == null || !floods.Flooded.Contains(regionId))
                    grid.SetRegionOBB(def.Position, def.Room.RoomSize, def.Rotation, regionId);
            }
        }
        if (floods != null)
        {
            acousticMap.RoomCells = floods;
            MarkFloods(grid, floods, minBound, voxelResolution, acousticMap.Regions);
        }

        // Regions are optional, but a map with none answers "where am I" with "Outside" everywhere: the
        // speedway went weeks like that with nothing noticing.
        if (acousticMap.Regions.Count <= 1)
            console.WriteLine(
                "[WARNING] AcousticVolumeGenerator: this map has no named regions, so every place in it " +
                "answers to 'Outside'. Nothing else will report this.");

        // Authored portals first: placed by hand at the real doorway, before anything is found or guessed.
        int authoredCount = 0;
        foreach (var def in openings)
        {
            if (def.Opening.ApertureSize <= 0) continue;

            int rA = def.Opening.RegionAId;
            int rB = def.Opening.RegionBId;

            // Unlinked (the prefab default): take the first two regions found just past the opening.
            if (rA == rB)
            {
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, def.Rotation);
                Vector3[] searchDirs = { forward, -forward, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY };

                var foundRegions = new List<int>();
                foreach (var dir in searchDirs)
                {
                    int r = grid.GetRegionAt(def.Position + dir * 0.5f);
                    if (!foundRegions.Contains(r)) foundRegions.Add(r);
                    if (foundRegions.Count >= 2) break;
                }

                if (foundRegions.Count >= 2) { rA = foundRegions[0]; rB = foundRegions[1]; }
                else if (foundRegions.Count == 1) { rA = foundRegions[0]; rB = AcousticConstants.GlobalRegionId; }

                console.WriteLine(rA != rB
                    ? $"[AcousticMap] Portal {def.Id} had no region link; probed the voxel grid and joined region {rA} to {rB}."
                    : $"[AcousticMap] WARNING: portal {def.Id} at {def.Position} has no region link and probing found no rooms — it will be ignored. Set RegionAId/RegionBId on the map entity.");
            }

            if (rA == rB) continue;

            var portalComp = def.Opening;
            portalComp.RegionAId = rA;
            portalComp.RegionBId = rB;
            acousticMap.Portals[def.Id] = (portalComp, def.Position);
            authoredCount++;
        }

        // Openings from the geometry: every gap the walls leave in a room's faces, where it is and its size
        // (a tunnel mouth, an open side, a doorway with no door). A door's leaf stands shut in its gap, so
        // it is not found twice. One per gap, not per pair of rooms: a lobby with a front door and an open
        // side onto one street is heard through both (2026-10-02). Only for regions with a wall, never the
        // floor (FaceOpenings).
        int openingCount = AddFaceOpenings(acousticMap, solids);
        if (openingCount > 0)
            console.WriteLine($"[AcousticMap] {openingCount} opening(s) from the gaps in rooms' faces (open sides, tunnel mouths, doorways with no door).");

        // Report every boundary the map did not describe; guess a portal only when asked
        // (autoDiscoverPortals).
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

            // The room's own face normals, each probed just past its own half-extent: the largest for
            // all six put the probe metres past a thin room's short faces.
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
                    new Opening { RegionAId = r1, RegionBId = r2, ApertureSize = 2.0f },
                    edgePos);
                discoveredCount++;
            }
        }

        console.WriteLine($"[AcousticMap] {acousticMap.Regions.Count - 1} region(s), {authoredCount} authored portal(s), {discoveredCount} guessed.");

        return acousticMap;
    }

    /// <summary>Each voxel whose middle a room's flood holds, to that room: over the boxes of the places that did not
    /// flood (a yard, a street), as a room inside a hall held the hall's voxels.</summary>
    private static void MarkFloods(SparseAcousticOctree grid, OpenFPS.Common.Geometry.RoomCells floods, Vector3 min, float res,
                                   Dictionary<int, Room> regions)
    {
        const float cell = OpenFPS.Common.Geometry.RoomFlood.Cell;
        var o = floods.Origin;
        // The cell holding a voxel's middle, along one axis.
        int MiddleCell(int v, float minA, float originA) => (int)MathF.Floor(((v + 0.5f) * res + minA - originA) / cell);
        int VoxelOf(int c, float minA, float originA) => (int)MathF.Floor(((c + 0.5f) * cell + originA - minA) / res);
        floods.ForEachSpan((room, y, z, x0, x1) =>
        {
            if (!regions.ContainsKey(room)) return;
            int vy = VoxelOf(y, min.Y, o.Y), vz = VoxelOf(z, min.Z, o.Z);
            if (MiddleCell(vy, min.Y, o.Y) != y || MiddleCell(vz, min.Z, o.Z) != z) return;
            for (int vx = VoxelOf(x0, min.X, o.X); vx <= VoxelOf(x1, min.X, o.X); vx++)
            {
                int cx = MiddleCell(vx, min.X, o.X);
                if (cx < x0 || cx > x1) continue;
                grid.SetRegionAt(min + new Vector3(vx + 0.5f, vy + 0.5f, vz + 0.5f) * res, room);
            }
        });
    }

    /// <summary>The first id the openings found in faces take; each next one is one lower. OpeningGraph
    /// tells them from authored portals by it.</summary>
    public const int FirstFaceOpeningId = -2000;

    /// <summary>
    /// Puts on the map an opening for every gap in the faces of its structures (or of only
    /// <paramref name="rooms"/>, replacing what those rooms had), from <paramref name="boxes"/>: the scene's
    /// fixed solid boxes, each door's leaf standing shut in its doorway (open-fps: EntityAcoustics.Solids).
    /// Returns how many it added. The aperture is the side of a square of the gap's area (pressure through an
    /// opening goes as the root of its area); the gap's own rectangle is in <see cref="AcousticMap.OpeningFrames"/>.
    /// </summary>
    public static int AddFaceOpenings(AcousticMap map, IEnumerable<FaceOpenings.Box> boxes, IReadOnlyCollection<int>? rooms = null)
    {
        float res = map.VoxelResolution;

        var solids = new List<(FaceOpenings.Box Box, Vector3 Min, Vector3 Max)>();
        foreach (var box in boxes)
        {
            var half = FaceOpenings.AxisAlignedHalfExtents(box.Size * 0.5f, box.Rotation);
            solids.Add((box, box.Centre - half, box.Centre + half));
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
        var portals = new Dictionary<int, (Opening Portal, Vector3 Position)>(map.Portals);
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
        // Filed by where they stand (BoxColumns): each room asks only its neighbours, in the same order
        // as a scan of all.
        var solidColumns = new BoxColumns();
        for (int i = 0; i < solids.Count; i++) solidColumns.Add(i, solids[i].Min, solids[i].Max);
        var placeColumns = new BoxColumns();
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
                portals[id] = (new Opening { RegionAId = room.Id, RegionBId = gap.Beyond, ApertureSize = MathF.Sqrt(gap.Area) }, gap.Centre);
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

    /// <summary>Updates one region that has moved or changed.</summary>
    public static void UpdateRegion(AcousticMap map, int regionId, Vector3 pos, Quaternion rot, Room reg)
    {
        var grid = map.VoxelGrid;

        grid.SetRegionOBB(pos, reg.RoomSize, rot, regionId);

        map.RegionPositions[regionId] = pos;
        map.RegionRotations[regionId] = rot;
        map.Regions[regionId] = reg;
    }
}
