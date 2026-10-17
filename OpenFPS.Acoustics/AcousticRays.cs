using System.Numerics;
using OpenFPS.Geometry;
using OpenFPS.Common;

namespace OpenFPS.Acoustics;

/// <summary>
/// The sound's questions of the world's solids (docs/SOUND_LIBRARY_BOUNDARY.md 7.6): what every wall between two
/// points lets through per band, the room a point is in, and the first surface along a ray. Asked of the triangle
/// world for the static solids, and of the world's boxes for what it does not hold (what moves, and every solid
/// before the triangles are built). The game's own rays (sight, prediction) are the host's.
/// </summary>
public static class AcousticRays
{
    // Queried several times per ray, per source, per frame, from the game thread and the acoustic worker at once:
    // hence per-thread scratch, with no allocation, lock or shared state.
    [ThreadStatic] private static List<AcousticBody>? _bodies;
    [ThreadStatic] private static List<GeometryCrossing>? _crossings;
    [ThreadStatic] private static List<SolidRef>? _inside;
    [ThreadStatic] private static Dictionary<SolidRef, int>? _crossed;
    [ThreadStatic] private static Dictionary<SolidRef, (float In, float Out)>? _spans;
    [ThreadStatic] private static List<Constructions.Crossing>? _crossingList;
    [ThreadStatic] private static List<Constructions.Crossing>? _boxCrossings;

    /// <summary>Whether this world's static solids are answered by its triangle world.</summary>
    public static bool UsesTriangles(IAcousticWorld world) => world.Geometry != null && TriangleGeometry.Enabled;

    /// <summary>
    /// The things a query has to test as boxes, into per-thread scratch valid until this thread asks again: with a
    /// triangle world what it does not hold, without one what is filed round <paramref name="centre"/>.
    /// </summary>
    private static List<AcousticBody> Boxes(IAcousticWorld world, bool triangles, Vector3 centre, float radius, bool staticOnly)
    {
        var list = _bodies ??= new List<AcousticBody>(64);
        if (triangles) world.Unindexed(staticOnly, list);
        else world.Near(centre, radius, staticOnly, list);
        return list;
    }

    /// <summary>Counts what the box path counted: not the stale (rebuilding) and not up to two things.</summary>
    private struct StaticFilter : IGeometryFilter
    {
        public IReadOnlySet<int>? Stale;
        public int A, B;
        public readonly bool Accept(int owner, in Surface surface)
            => owner != A && owner != B && (Stale == null || !Stale.Contains(owner));
    }

    private static StaticFilter Filter(IAcousticWorld world, int a = int.MinValue, int b = int.MinValue)
        => new() { Stale = world.GeometryStale, A = a, B = b };

    /// <summary>
    /// The static solids' share of <see cref="Occlusion"/>: every solid the nudged segment passes through (or
    /// starts or ends in), each charged its WallTransmission once, or once per wall of a hollow shell it actually
    /// crosses.
    /// </summary>
    private static void StaticOcclusion(IAcousticWorld world, Vector3 nudgedStart, Vector3 nudgedEnd, Vector3 rayDir,
                                        StaticFilter filter, ref float eqLow, ref float eqMid, ref float eqHigh)
    {
        var geo = world.Geometry!;
        float len = Vector3.Dot(nudgedEnd - nudgedStart, rayDir);
        if (len <= 0f) return;
        var crossings = _crossings ??= new List<GeometryCrossing>(16);
        var inside = _inside ??= new List<SolidRef>(4);
        var crossed = _crossed ??= new Dictionary<SolidRef, int>();
        crossed.Clear();
        geo.All(nudgedStart, rayDir, len, GeometryLayers.Physical, ref filter, crossings);
        foreach (var c in crossings) crossed.TryAdd(c.Solid, 0);
        inside.Clear();
        geo.Containing(nudgedStart, GeometryLayers.Physical, ref filter, inside);
        foreach (var s in inside) crossed.TryAdd(s, 0);
        if (crossed.Count == 0) return;
        // Where the segment goes into and comes out of each: solids in contact, crossed one straight after
        // another, are one construction (Constructions.Through).
        var spans = _spans ??= new Dictionary<SolidRef, (float In, float Out)>();
        spans.Clear();
        foreach (var s in inside) spans[s] = (0f, -1f);
        foreach (var c in crossings)
        {
            var (tin, tout) = spans.TryGetValue(c.Solid, out var had) ? had : (len, -1f);
            if (c.Front) tin = MathF.Min(tin, c.T); else tout = MathF.Max(tout, c.T);
            spans[c.Solid] = (tin, tout);
        }
        var list = _crossingList ??= new List<Constructions.Crossing>(8);
        list.Clear();
        foreach (var solid in crossed.Keys)
        {
            ref readonly var surface = ref geo.SurfaceOf(solid);
            var panel = surface.Construction.PanelSize;
            int layers = 1;
            bool hollow = surface.Is(SurfaceFlags.Hollow);
            if (hollow)
            {
                // A hollow shell is walls of its shell thickness round an empty inside: one wall in and
                // one out, or one if either end is inside it.
                float shell = surface.Construction.ShellThickness > 0 ? surface.Construction.ShellThickness : 0.2f;
                var s = panel;
                float a = MathF.Max(s.X, MathF.Max(s.Y, s.Z)), b = s.X + s.Y + s.Z - a - MathF.Min(s.X, MathF.Min(s.Y, s.Z));
                panel = new Vector3(shell, a, b);
                bool startIn = Contains(geo, solid, nudgedStart), endIn = Contains(geo, solid, nudgedEnd);
                layers = (startIn ? 0 : 1) + (endIn ? 0 : 1);
            }
            var (tin, tout) = spans.TryGetValue(solid, out var span) ? span : (0f, -1f);
            if (tout < 0f) tout = len;   // it ends inside
            var normal = Vector3.Zero;
            float run = 0f;
            if (!hollow)
            {
                var (centre, size, rotation) = geo.BoxOf(solid);
                // Gone into by its edge, a box pays what the line runs through of it (Constructions).
                if (size.X > 0f && size.Y > 0f && size.Z > 0f) run = Constructions.RunInside(nudgedStart, nudgedEnd, centre, size, rotation);
                if (!surface.Is(SurfaceFlags.DoorLeaf))
                {
                    int owner = geo.OwnerOf(solid);
                    bool hung = world.TryGetBody(owner, out var e) && e.DoorLeaf;
                    if (!hung && size.X > 0f && Constructions.IsSheet(size)) normal = Constructions.Normal(size, rotation);
                }
            }
            list.Add(Constructions.Of(tin, tout, AcousticRegistry.GetProperties(surface.Material), panel, surface.Construction.Build, layers, normal, run));
        }
        var g = Constructions.Through(list, rayDir);
        eqLow *= g.X; eqMid *= g.Y; eqHigh *= g.Z;
    }

    private static bool Contains(TriangleWorld geo, SolidRef solid, Vector3 p)
    {
        var inside = _inside ??= new List<SolidRef>(4);
        inside.Clear();
        var all = new AcceptAll();
        geo.Containing(p, GeometryLayers.Physical, ref all, inside);
        return inside.Contains(solid);
    }

    /// <summary>How blocked the line between two points is, 0 to 1, and the bleed (the mean of what the bands let
    /// through).</summary>
    public static float OcclusionFactor(IAcousticWorld world, Vector3 start, Vector3 end, out float bleed, int ignoreA = -1, int ignoreB = -1)
    {
        Occlusion(world, start, end, out float block, out bleed, out _, out _, out _, ignoreA, ignoreB);
        return block;
    }

    /// <summary>
    /// <see cref="Occlusion"/> averaged over five rays in a cross round the end point, for partial occlusion.
    /// Every output is the mean of the five, maxBlock included. Both ignored ids are left out of every ray.
    /// </summary>
    public static void MultiPointOcclusion(IAcousticWorld world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed,
                                           out float eqLow, out float eqMid, out float eqHigh, int ignoreA = -1, int ignoreB = -1)
    {
        Vector3 dir = end - start;
        if (dir.LengthSquared() < 0.01f)
        {
            Occlusion(world, start, end, out maxBlock, out cumulativeBleed, out eqLow, out eqMid, out eqHigh, ignoreA, ignoreB);
            return;
        }
        // Straight up or down the cross product with world up is zero, and normalised NaN: four rays went
        // nowhere and a floor slab passed a sound from the room above at -2 dB. Any horizontal axis will do.
        Vector3 side = Vector3.Cross(dir, Vector3.UnitY);
        if (side.LengthSquared() < 1e-8f * MathF.Max(1e-8f, dir.LengthSquared())) side = Vector3.Cross(dir, Vector3.UnitX);
        Vector3 right = Vector3.Normalize(side);
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, dir));
        // The spread grows with distance (10 degrees, 0.15 to 0.8 m), as the emitter's projection does.
        float sampleOffset = Math.Clamp(Vector3.Distance(start, end) * MathF.Tan(10f * MathF.PI / 180f), 0.15f, 0.8f);

        // Each point round the source is in the source's own air: pulled back to the first solid on the way out
        // to it. Left where the cross put it, a source 0.3 m over a slab had a point under it, in the room below,
        // and one ray in five clear made any floor or roof -14 dB at every band (rain on Marlow Tower's roof, a
        // footstep in the flat above, 2026-10-10).
        Vector3[] sourcePoints = {
            end, // Center
            InItsAir(world, end, up * sampleOffset, ignoreA, ignoreB),
            InItsAir(world, end, -up * sampleOffset, ignoreA, ignoreB),
            InItsAir(world, end, right * sampleOffset, ignoreA, ignoreB),
            InItsAir(world, end, -right * sampleOffset, ignoreA, ignoreB)
        };

        float sumBlock = 0, sumBleed = 0, sumLow = 0, sumMid = 0, sumHigh = 0;

        foreach (var p in sourcePoints)
        {
            Occlusion(world, start, p, out float b, out float bl, out float el, out float em, out float eh, ignoreA, ignoreB);
            sumBlock += b;
            sumBleed += bl;
            sumLow += el;
            sumMid += em;
            sumHigh += eh;
        }

        maxBlock = sumBlock / 5.0f;
        cumulativeBleed = sumBleed / 5.0f;
        eqLow = sumLow / 5.0f;
        eqMid = sumMid / 5.0f;
        eqHigh = sumHigh / 5.0f;
    }

    /// <summary>How far a source's extent goes this way: <paramref name="offset"/> from <paramref name="at"/>,
    /// or up to 5 cm short of the first solid on the way. Neither ignored id stops it.</summary>
    private static Vector3 InItsAir(IAcousticWorld world, Vector3 at, Vector3 offset, int ignoreA, int ignoreB)
    {
        float len = offset.Length();
        if (len < 1e-4f) return at;
        Vector3 dir = offset / len;
        float clear = len;
        bool triangles = UsesTriangles(world);
        if (triangles)
        {
            var filter = Filter(world, ignoreA, ignoreB);
            if (world.Geometry!.Enter(at, dir, len, GeometryLayers.Physical, ref filter, out var hit) && hit.T < clear)
                clear = hit.T;
        }
        var others = Boxes(world, triangles, at, len + 1f, staticOnly: false);
        foreach (var s in others)
        {
            if (s.Id == ignoreA || s.Id == ignoreB) continue;
            if (!s.Solid || s.Shape != ColliderShape.Box || s.Size.X <= 0) continue;
            if (GeometryUtils.RayIntersectsOBB(at, dir, s.Position, s.Size, s.Rotation, out float t) && t < clear)
                clear = t;
        }
        return clear >= len ? at + offset : at + dir * MathF.Max(0f, clear - 0.05f);
    }

    /// <summary>
    /// What every wall between two points lets through, per band (walls in a row multiply), and the broadband
    /// block and bleed read off the bands.
    /// </summary>
    public static void Occlusion(IAcousticWorld world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed,
                                 out float eqLow, out float eqMid, out float eqHigh, int ignoreA = -1, int ignoreB = -1)
    {
        maxBlock = 0;
        cumulativeBleed = 1.0f;
        eqLow = 1.0f;
        eqMid = 1.0f;
        eqHigh = 1.0f;

        Vector3 dir = end - start;
        float dist = dir.Length();
        if (dist < 0.1f) return;

        Vector3 rayDir = Vector3.Normalize(dir);
        Vector3 nudgedStart = start + (rayDir * 0.05f);
        Vector3 nudgedEnd = end - (rayDir * 0.05f);

        Vector3 center = (start + end) / 2.0f;
        bool triangles = UsesTriangles(world);
        if (triangles)
            StaticOcclusion(world, nudgedStart, nudgedEnd, rayDir, Filter(world, ignoreA, ignoreB),
                            ref eqLow, ref eqMid, ref eqHigh);
        // 10 m wider than the segment, to catch large static objects such as foundations.
        var bodies = Boxes(world, triangles, center, (dist / 2.0f) + 10.0f, staticOnly: false);

        var crossed = _boxCrossings ??= new List<Constructions.Crossing>(8);
        crossed.Clear();
        float span = dist - 0.1f;
        foreach (var body in bodies)
        {
            if (body.Id == ignoreA) continue;
            if (body.Id == ignoreB) continue;

            if (body.Size.X > 0 && body.Solid)
            {
                bool intersected = false;
                // The panel the sound goes through (its smallest dimension is its thickness, the other
                // two its face) and how many times it goes through it.
                Vector3 panel = body.Size;
                int layers = 1;
                float tIn = 0f, tOut = span;

                if (body.Shape == ColliderShape.Box)
                {
                    if (GeometryUtils.RayIntersectsOBB(nudgedStart, rayDir, body.Position, body.Size, body.Rotation, out float entry) &&
                        GeometryUtils.RayIntersectsOBB(nudgedEnd, -rayDir, body.Position, body.Size, body.Rotation, out float exitFromEnd))
                    {
                        float exitFromStart = dist - exitFromEnd;
                        if (exitFromStart > entry)
                        {
                            intersected = true;
                            tIn = entry; tOut = span - exitFromEnd;
                            if (body.Hollow)
                            {
                                // A hollow shell is walls of its shell thickness round an empty inside:
                                // one wall in and one out, or one if either end is inside it.
                                float shell = body.ShellThickness > 0 ? body.ShellThickness : 0.2f;
                                var s = body.Size;
                                float a = MathF.Max(s.X, MathF.Max(s.Y, s.Z)), b = s.X + s.Y + s.Z - a - MathF.Min(s.X, MathF.Min(s.Y, s.Z));
                                panel = new Vector3(shell, a, b);
                                layers = (entry > 0f ? 1 : 0) + (exitFromEnd > 0f ? 1 : 0);
                            }
                        }
                    }
                }
                else
                {
                    float en = 0, ex = 0;
                    intersected = body.Shape switch
                    {
                        ColliderShape.Sphere => GeometryUtils.RayIntersectsSphere(nudgedStart, rayDir, body.Position, body.Size.X / 2.0f, out en, out ex),
                        ColliderShape.Cylinder => GeometryUtils.RayIntersectsCylinder(nudgedStart, rayDir, body.Position, body.Size.X / 2.0f, body.Size.Y, out en, out ex),
                        ColliderShape.Cone => GeometryUtils.RayIntersectsCone(nudgedStart, rayDir, body.Position, body.Size.X / 2.0f, body.Size.Y, out en, out ex),
                        _ => false
                    };
                    if (intersected)
                    {
                        float clampedEx = Math.Min(ex, dist);
                        // A round thing is as thick as the chord the sound crosses it by.
                        if (clampedEx > en) { panel = new Vector3(clampedEx - en, body.Size.X, body.Size.Y); tIn = en; tOut = clampedEx; }
                        else intersected = false;
                    }
                }

                // A wall is charged wherever the ray meets it, however near a doorway: what comes in by the
                // doorway is a route (OpeningRoutes). Skipping walls near apertures passed every ray that
                // grazed a facade by a door at full level, and five rays averaged to flat 20/40/60/80 %.
                if (intersected)
                {
                    // The Steam Audio scene's panel model (WallTransmission), so the fallback and the
                    // simulator answer with one model. The prefab's own Transmission figures are not read.
                    // A fixed sheet can be a layer of a construction (Constructions); what moves, hangs in a
                    // doorway, is hollow or is round never is.
                    bool layer = body.Shape == ColliderShape.Box && !body.Hollow && !body.Moves
                                 && !body.DoorLeaf && Constructions.IsSheet(body.Size);
                    // Gone into by its edge, a box pays what the line runs through of it (Constructions).
                    float run = body.Shape == ColliderShape.Box && !body.Hollow
                        ? Constructions.RunInside(nudgedStart, nudgedEnd, body.Position, body.Size, body.Rotation) : 0f;
                    crossed.Add(Constructions.Of(tIn, tOut, AcousticRegistry.GetProperties(body.Material), panel,
                                                 body.Build, layers,
                                                 layer ? Constructions.Normal(body.Size, body.Rotation) : Vector3.Zero, run));
                }
            }
        }
        var through = Constructions.Through(crossed, rayDir);
        eqLow *= through.X; eqMid *= through.Y; eqHigh *= through.Z;

        // No floor lets sound through whatever the walls are. Blocked is what the loudest band lost;
        // bleed is the bands' mean.
        cumulativeBleed = (eqLow + eqMid + eqHigh) / 3f;
        maxBlock = Math.Clamp(1f - MathF.Max(eqLow, MathF.Max(eqMid, eqHigh)), 0f, 1f);
    }

    /// <summary>
    /// The first solid along each of <paramref name="directions"/>, into buffers the caller owns (it runs every
    /// audio frame): distance, absorption and material, or maxDist, 1 and "Generic" for a miss.
    /// <paramref name="staticOnly"/> leaves out what moves: a ray from inside a car would find the car.
    /// <paramref name="normals"/>, when given, takes each hit surface's normal (zero for a miss, or for a shape that
    /// has none worked out); <paramref name="count"/> casts only the first that many.
    /// </summary>
    public static void FirstSurfaces(IAcousticWorld world, Vector3 start, Vector3[] directions, float maxDist,
                                     float[] distances, float[] absorptions, string[] materials, bool staticOnly = false,
                                     Vector3[]? normals = null, int count = -1)
    {
        int rays = count < 0 ? directions.Length : Math.Min(count, directions.Length);
        for (int i = 0; i < rays; i++)
        {
            distances[i] = maxDist;
            absorptions[i] = 1.0f;
            materials[i] = "Generic";
            if (normals != null) normals[i] = Vector3.Zero;
        }

        bool triangles = UsesTriangles(world);
        if (triangles)
        {
            var geo = world.Geometry!;
            var filter = Filter(world);
            for (int i = 0; i < rays; i++)
            {
                if (!geo.Enter(start, directions[i], maxDist, GeometryLayers.Physical, ref filter, out var hit)) continue;
                if (!(hit.T < distances[i])) continue;
                ref readonly var surface = ref geo.SurfaceOf(hit);
                distances[i] = hit.T;
                if (normals != null) normals[i] = hit.Normal;
                absorptions[i] = surface.Absorption > 0 ? surface.Absorption : AcousticRegistry.GetProperties(surface.Material).Absorption;
                materials[i] = string.IsNullOrEmpty(surface.Material) ? "Generic" : surface.Material;
            }
        }
        var bodies = Boxes(world, triangles, start, maxDist, staticOnly);

        foreach (var body in bodies)
        {
            if (body.Size.X <= 0 || !body.Solid) continue;

            var registryProps = AcousticRegistry.GetProperties(body.Material);
            float absorption = (body.Absorption > 0) ? body.Absorption : registryProps.Absorption;
            string materialName = string.IsNullOrEmpty(body.Material) ? "Generic" : body.Material;
            // Nothing of it within reach: on the triangle path the list is the whole map's traffic.
            if (Vector3.Distance(start, body.Position) - body.Size.Length() > maxDist) continue;

            for (int i = 0; i < rays; i++)
            {
                bool intersected = false;
                float dist = maxDist;

                if (body.Shape == ColliderShape.Box)
                {
                    intersected = GeometryUtils.RayIntersectsOBB(start, directions[i], body.Position, body.Size, body.Rotation, out dist);
                }
                else
                {
                    intersected = body.Shape switch
                    {
                        ColliderShape.Sphere => GeometryUtils.LineIntersectsSphere(start, start + directions[i] * maxDist, body.Position, body.Size.X / 2.0f),
                        ColliderShape.Cylinder or ColliderShape.Cone => GeometryUtils.RayIntersectsCylinder(start, directions[i], body.Position, body.Size.X / 2.0f, body.Size.Y, out dist),
                        _ => false
                    };
                    if (intersected && body.Shape == ColliderShape.Sphere) dist = Vector3.Distance(start, body.Position) - (body.Size.X / 2f);
                }

                if (intersected && dist >= 0 && dist < distances[i])
                {
                    distances[i] = dist;
                    absorptions[i] = absorption;
                    materials[i] = materialName;
                    if (normals != null)
                        normals[i] = body.Shape == ColliderShape.Box
                            && RayIntersectsOBBWithNormal(start, directions[i], body.Position, body.Size, body.Rotation, out _, out var n)
                            ? n : Vector3.Zero;
                }
            }
        }
    }

    /// <summary>The acoustic region a position is in: the room whose flood holds it, else by the boxes of the regions
    /// that did not flood, else the voxel grid. The server asks the same (MapManager.RegionAt).</summary>
    public static int RegionAt(IAcousticWorld world, Vector3 position)
    {
        var map = world.Rooms;
        if (map == null) return AcousticConstants.GlobalRegionId;

        // A room is where its walls put it (docs/GEOMETRY.md 12.6): its box no longer holds a point its flood
        // does not, and a box drawn past its walls no longer takes in the yard.
        var floods = map.RoomCells;
        if (floods != null && floods.TryRoomAt(position, out int flooded) && map.Regions.ContainsKey(flooded))
            return flooded;

        // Where boxes overlap the smallest holding the point wins (a room inside a hall), not the first
        // in the map's list, which made the room depend on authoring order (Cody, 2026-09-28).
        int best = int.MinValue;
        float bestVolume = float.MaxValue;
        foreach (var regId in map.Regions.Keys)
        {
            if (regId == AcousticConstants.GlobalRegionId) continue;
            if (floods != null && floods.Flooded.Contains(regId)) continue;

            Vector3 size = Vector3.Zero;
            Vector3 pos = Vector3.Zero;
            Quaternion rot = Quaternion.Identity;

            if (world.TryGetBody(regId, out var body))
            {
                pos = body.Position;
                rot = body.Rotation;
                size = (body.Size.X > 0) ? body.Size : body.RoomSize;
            }
            else if (map.RegionPositions.TryGetValue(regId, out var regPos)
                     && map.Regions.TryGetValue(regId, out var reg))
            {
                // Both asked: a streamed map swaps its tables one after another, and for a moment one can
                // have a room the other has not.
                pos = regPos;
                rot = map.RegionRotations.GetValueOrDefault(regId, Quaternion.Identity);
                size = reg.RoomSize;
            }

            if (size.X > 0 && GeometryUtils.IsPointInOBB(position, pos, size, rot))
            {
                float volume = size.X * size.Y * size.Z;
                if (volume < bestVolume) { bestVolume = volume; best = regId; }
            }
        }
        if (best != int.MinValue) return best;

        // The voxel grid, only for a region with no box: a region whose box did not hold the point is not
        // where it is, whatever the grid says. Its half-metre voxels carry a room past its walls, and
        // standing against a house put you acoustically inside it (Cody, 64 Alder Street, 2026-09-28).
        int coarse = map.VoxelGrid.GetRegionAt(position);
        if (coarse != AcousticConstants.GlobalRegionId && HasBox(world, map, coarse))
            return AcousticConstants.GlobalRegionId;
        return coarse;
    }

    /// <summary>Whether a region has an explicit volume that <see cref="RegionAt"/> can test.</summary>
    private static bool HasBox(IAcousticWorld world, AcousticMap map, int regId)
    {
        if (world.TryGetBody(regId, out var body))
            return body.Size.X > 0 || body.RoomSize.X > 0;
        return map.RegionPositions.ContainsKey(regId)
            && map.Regions.TryGetValue(regId, out var reg) && reg.RoomSize.X > 0;
    }

    /// <summary>
    /// The first solid along a ray out to <paramref name="maxDist"/>, neither ignored id counted: how far, its
    /// normal and its material (the material under a ray). False, and "Generic" facing back, for a miss.
    /// </summary>
    public static bool FirstMaterial(IAcousticWorld world, Vector3 start, Vector3 dir, float maxDist, out float distance, out Vector3 normal,
                                     out string material, int ignoreA = -1, int ignoreB = -1)
    {
        distance = maxDist;
        normal = -dir;
        material = "Generic";
        bool hit = false;

        bool triangles = UsesTriangles(world);
        if (triangles)
        {
            var geo = world.Geometry!;
            var filter = Filter(world, ignoreA, ignoreB);
            if (geo.Enter(start, dir, maxDist, GeometryLayers.Physical, ref filter, out var h) && h.T < distance)
            {
                distance = h.T;
                normal = h.Normal;
                material = geo.SurfaceOf(h).Material;
                hit = true;
            }
        }
        var bodies = Boxes(world, triangles, start, maxDist, staticOnly: false);

        foreach (var body in bodies)
        {
            if (body.Id == ignoreA || body.Id == ignoreB) continue;
            if (!body.Solid) continue;

            float d = maxDist;
            bool intersected = false;
            Vector3 n = -dir;

            if (body.Shape == ColliderShape.Box)
            {
                intersected = RayIntersectsOBBWithNormal(start, dir, body.Position, body.Size, body.Rotation, out d, out n);
            }
            else if (body.Shape == ColliderShape.Sphere)
            {
                intersected = GeometryUtils.LineIntersectsSphere(start, start + dir * maxDist, body.Position, body.Size.X / 2.0f);
                if (intersected)
                {
                    d = Vector3.Distance(start, body.Position) - (body.Size.X / 2f);
                    Vector3 hitPos = start + dir * d;
                    n = Vector3.Normalize(hitPos - body.Position);
                }
            }

            if (intersected && d >= 0 && d < distance)
            {
                distance = d;
                normal = n;
                material = body.Material;
                hit = true;
            }
        }

        return hit;
    }

    private static bool RayIntersectsOBBWithNormal(Vector3 start, Vector3 dir, Vector3 boxPos, Vector3 boxSize, Quaternion boxRot, out float distance, out Vector3 normal)
    {
        Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-boxPos) * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(boxRot));
        Vector3 localStart = Vector3.Transform(start, worldToLocal);
        Vector3 localDir = Vector3.TransformNormal(dir, worldToLocal);

        bool hit = RayIntersectsAABBWithNormal(localStart, localDir, Vector3.Zero, boxSize, out distance, out Vector3 localNormal);
        if (hit)
        {
            normal = Vector3.Normalize(Vector3.TransformNormal(localNormal, Matrix4x4.CreateFromQuaternion(boxRot)));
        }
        else
        {
            normal = -dir;
        }
        return hit;
    }

    private static bool RayIntersectsAABBWithNormal(Vector3 start, Vector3 dir, Vector3 boxPos, Vector3 boxSize, out float distance, out Vector3 normal)
    {
        Vector3 min = boxPos - (boxSize / 2.0f);
        Vector3 max = boxPos + (boxSize / 2.0f);
        distance = 0;
        normal = Vector3.Zero;

        float tmin = -float.MaxValue, tmax = float.MaxValue;
        int hitAxis = -1;
        float hitSign = 1.0f;

        if (Math.Abs(dir.X) > 0.000001f) {
            float t1 = (min.X - start.X) / dir.X, t2 = (max.X - start.X) / dir.X;
            if (t1 < t2) { if (t1 > tmin) { tmin = t1; hitAxis = 0; hitSign = -1.0f; } tmax = Math.Min(tmax, t2); }
            else { if (t2 > tmin) { tmin = t2; hitAxis = 0; hitSign = 1.0f; } tmax = Math.Min(tmax, t1); }
        } else if (start.X < min.X || start.X > max.X) return false;

        if (Math.Abs(dir.Y) > 0.000001f) {
            float t1 = (min.Y - start.Y) / dir.Y, t2 = (max.Y - start.Y) / dir.Y;
            if (t1 < t2) { if (t1 > tmin) { tmin = t1; hitAxis = 1; hitSign = -1.0f; } tmax = Math.Min(tmax, t2); }
            else { if (t2 > tmin) { tmin = t2; hitAxis = 1; hitSign = 1.0f; } tmax = Math.Min(tmax, t1); }
        } else if (start.Y < min.Y || start.Y > max.Y) return false;

        if (Math.Abs(dir.Z) > 0.000001f) {
            float t1 = (min.Z - start.Z) / dir.Z, t2 = (max.Z - start.Z) / dir.Z;
            if (t1 < t2) { if (t1 > tmin) { tmin = t1; hitAxis = 2; hitSign = -1.0f; } tmax = Math.Min(tmax, t2); }
            else { if (t2 > tmin) { tmin = t2; hitAxis = 2; hitSign = 1.0f; } tmax = Math.Min(tmax, t1); }
        } else if (start.Z < min.Z || start.Z > max.Z) return false;

        if (tmax >= tmin && tmax > 0)
        {
            distance = tmin > 0 ? tmin : 0;
            if (hitAxis == 0) normal = new Vector3(hitSign, 0, 0);
            else if (hitAxis == 1) normal = new Vector3(0, hitSign, 0);
            else if (hitAxis == 2) normal = new Vector3(0, 0, hitSign);
            return true;
        }
        return false;
    }
}
