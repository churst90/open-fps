using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>
/// The client's geometry queries: rays, occlusion and region lookup, for prediction, sight and the
/// acoustics.
/// </summary>
public class SpatialService
{
    public int OwnEntityId { get; set; } = -1;

    // Queried several times per ray, per source, per frame, from the game thread and the acoustic worker
    // at once: hence per-thread scratch, with no allocation, lock or shared state.
    [ThreadStatic] private static List<EntitySnapshot>? _candidates;
    [ThreadStatic] private static List<int>? _candidateIds;
    [ThreadStatic] private static HashSet<int>? _candidateSeen;

    /// <summary>
    /// The entities a query at <paramref name="center"/> could possibly hit, each once (a wall spanning
    /// cells is filed in each of them).
    ///
    /// The returned list is per-thread scratch, valid until this thread calls back in: no caller may nest
    /// a second query inside a walk of the first.
    /// </summary>
    private List<EntitySnapshot> GetEntitiesToTest(WorldSnapshot world, Vector3 center, float radius, bool staticOnly = false)
    {
        var candidates = _candidates ??= new List<EntitySnapshot>(64);
        candidates.Clear();

        if (world.StaticGrid != null)
        {
            var ids = _candidateIds ??= new List<int>(64);
            var seen = _candidateSeen ??= new HashSet<int>();
            world.StaticGrid.CollectInRadius(center, radius, ids, seen);

            for (int i = 0; i < ids.Count; i++)
                if (world.Entities.TryGetValue(ids[i], out var snap)) candidates.Add(snap);

            // Dynamic entities are not in the static grid, so they are always in play.
            if (!staticOnly)
                for (int i = 0; i < world.DynamicEntities.Count; i++)
                    if (seen.Add(world.DynamicEntities[i].Id)) candidates.Add(world.DynamicEntities[i]);

            // An empty answer from the grid is an answer (the middle of a road). Falling through to every
            // entity in the world froze a city of six thousand boxes, outdoors, where a listener spends
            // their time.
            return candidates;
        }

        // No grid yet (until the first RebuildGrid, seconds after a map arrives): only then is the whole
        // world the honest answer.
        foreach (var snap in world.Entities.Values) candidates.Add(snap);
        return candidates;
    }

    // ═══ The triangle world (docs/GEOMETRY.md stage 1) ═══════════════════════════════════════════
    //
    // With a triangle world the static solids are asked of it; only what it does not hold (what moves,
    // and the Unindexed statics) goes through the per-entity box tests. AudioLab --geometry-parity lists
    // where the two paths differ.

    [ThreadStatic] private static List<EntitySnapshot>? _others;
    [ThreadStatic] private static List<OpenFPS.Common.Geometry.GeometryCrossing>? _crossings;
    [ThreadStatic] private static List<OpenFPS.Common.Geometry.SolidRef>? _inside;
    [ThreadStatic] private static Dictionary<OpenFPS.Common.Geometry.SolidRef, int>? _crossed;

    /// <summary>Whether this snapshot's static solids are answered by its triangle world.</summary>
    public static bool UsesTriangles(WorldSnapshot world) => world.Geometry != null && OpenFPS.Common.Geometry.TriangleGeometry.Enabled;

    /// <summary>What the triangle world does not answer for: what moves (unless <paramref name="staticOnly"/>)
    /// and the statics it does not hold.</summary>
    private static List<EntitySnapshot> Others(WorldSnapshot world, bool staticOnly)
    {
        var list = _others ??= new List<EntitySnapshot>(32);
        list.Clear();
        foreach (int id in world.UnindexedStatics)
            if (world.Entities.TryGetValue(id, out var s)) list.Add(s);
        if (!staticOnly) list.AddRange(world.DynamicEntities);
        return list;
    }

    /// <summary>Counts what the box path counted: not the stale (rebuilding) and not up to two entities.</summary>
    private struct StaticFilter : OpenFPS.Common.Geometry.IGeometryFilter
    {
        public IReadOnlySet<int>? Stale;
        public int A, B;
        public readonly bool Accept(int owner, in OpenFPS.Common.Geometry.Surface surface)
            => owner != A && owner != B && (Stale == null || !Stale.Contains(owner));
    }

    /// <summary>The box path's predicates over EntitySnapshots, asked of a triangle's owner.</summary>
    private struct PredicateFilter : OpenFPS.Common.Geometry.IGeometryFilter
    {
        public StaticFilter Base;
        public WorldSnapshot World;
        public Func<EntitySnapshot, bool>? Accepts;
        public Func<EntitySnapshot, bool>? Skips;
        public bool GlassBlocks;
        public bool SkipGlass;
        public readonly bool Accept(int owner, in OpenFPS.Common.Geometry.Surface surface)
        {
            if (!Base.Accept(owner, surface)) return false;
            if (SkipGlass && !GlassBlocks && surface.Is(OpenFPS.Common.Geometry.SurfaceFlags.Glass)) return false;
            if (Accepts == null && Skips == null) return true;
            if (!World.Entities.TryGetValue(owner, out var e)) return false;
            if (Accepts != null && !Accepts(e)) return false;
            if (Skips != null && Skips(e)) return false;
            return true;
        }
    }

    private StaticFilter Filter(WorldSnapshot world, int a = int.MinValue, int b = int.MinValue)
        => new() { Stale = world.GeometryStale, A = a, B = b };

    /// <summary>
    /// The static solids' share of <see cref="GetOcclusionData"/>: every solid the nudged segment passes
    /// through (or starts or ends in), each charged its WallTransmission once, or once per wall of a hollow
    /// shell it actually crosses.
    /// </summary>
    private static void StaticOcclusion(WorldSnapshot world, Vector3 nudgedStart, Vector3 nudgedEnd, Vector3 rayDir,
                                        StaticFilter filter, ref float eqLow, ref float eqMid, ref float eqHigh)
    {
        var geo = world.Geometry!;
        float len = Vector3.Dot(nudgedEnd - nudgedStart, rayDir);
        if (len <= 0f) return;
        var crossings = _crossings ??= new List<OpenFPS.Common.Geometry.GeometryCrossing>(16);
        var inside = _inside ??= new List<OpenFPS.Common.Geometry.SolidRef>(4);
        var crossed = _crossed ??= new Dictionary<OpenFPS.Common.Geometry.SolidRef, int>();
        crossed.Clear();
        geo.All(nudgedStart, rayDir, len, OpenFPS.Common.Geometry.GeometryLayers.Physical, ref filter, crossings);
        foreach (var c in crossings) crossed.TryAdd(c.Solid, 0);
        inside.Clear();
        geo.Containing(nudgedStart, OpenFPS.Common.Geometry.GeometryLayers.Physical, ref filter, inside);
        foreach (var s in inside) crossed.TryAdd(s, 0);
        if (crossed.Count == 0) return;
        foreach (var solid in crossed.Keys)
        {
            ref readonly var surface = ref geo.SurfaceOf(solid);
            var panel = surface.Construction.PanelSize;
            int layers = 1;
            if (surface.Is(OpenFPS.Common.Geometry.SurfaceFlags.Hollow))
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
            var (gl, gm, gh) = WallTransmission.BandGains(surface.Material, panel, surface.Construction.Build);
            for (int k = 0; k < layers; k++) { eqLow *= gl; eqMid *= gm; eqHigh *= gh; }
        }
    }

    private static bool Contains(OpenFPS.Common.Geometry.TriangleWorld geo, OpenFPS.Common.Geometry.SolidRef solid, Vector3 p)
    {
        var inside = _inside ??= new List<OpenFPS.Common.Geometry.SolidRef>(4);
        inside.Clear();
        var all = new OpenFPS.Common.Geometry.AcceptAll();
        geo.Containing(p, OpenFPS.Common.Geometry.GeometryLayers.Physical, ref all, inside);
        return inside.Contains(solid);
    }

    /// <summary>How blocked the line between two points is, 0 to 1, and the bleed (the mean of what the
    /// bands let through).</summary>
    public float GetOcclusionFactor(WorldSnapshot world, Vector3 start, Vector3 end, out float bleed, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
    {
        GetOcclusionData(world, start, end, out float block, out bleed, out _, out _, out _, ignoreEntityId, ignoreEntityId2);
        return block;
    }

    /// <summary>
    /// <see cref="GetOcclusionData"/> averaged over five rays in a cross round the end point, for partial
    /// occlusion. Every output is the mean of the five, maxBlock included. Both ignored ids are left out
    /// of every ray.
    /// </summary>
    public void GetMultiPointOcclusionData(WorldSnapshot world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed, out float eqLow, out float eqMid, out float eqHigh, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
    {
        Vector3 dir = end - start;
        if (dir.LengthSquared() < 0.01f)
        {
            GetOcclusionData(world, start, end, out maxBlock, out cumulativeBleed, out eqLow, out eqMid, out eqHigh, ignoreEntityId, ignoreEntityId2);
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

        Vector3[] sourcePoints = {
            end, // Center
            end + (up * sampleOffset), 
            end - (up * sampleOffset),
            end + (right * sampleOffset),
            end - (right * sampleOffset)
        };

        float sumBlock = 0, sumBleed = 0, sumLow = 0, sumMid = 0, sumHigh = 0;

        foreach (var p in sourcePoints)
        {
            GetOcclusionData(world, start, p, out float b, out float bl, out float el, out float em, out float eh, ignoreEntityId, ignoreEntityId2);
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

    /// <summary>
    /// What every wall between two points lets through, per band (walls in a row multiply), and the
    /// broadband block and bleed read off the bands.
    /// </summary>
    public void GetOcclusionData(WorldSnapshot world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed, out float eqLow, out float eqMid, out float eqHigh, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
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
        // 10 m wider than the segment, to catch large static objects such as foundations.
        List<EntitySnapshot> entitiesToTest;
        if (UsesTriangles(world))
        {
            StaticOcclusion(world, nudgedStart, nudgedEnd, rayDir, Filter(world, ignoreEntityId, ignoreEntityId2),
                            ref eqLow, ref eqMid, ref eqHigh);
            entitiesToTest = Others(world, staticOnly: false);
        }
        else entitiesToTest = GetEntitiesToTest(world, center, (dist / 2.0f) + 10.0f);

        foreach (var entitySnap in entitiesToTest)
        {
            if (entitySnap.Id == ignoreEntityId) continue;
            if (entitySnap.Id == ignoreEntityId2) continue;
            var def = entitySnap.Definition;
            
            if (def.Collider.Size.X > 0 && def.Collider.IsSolid)
            {
                var transform = entitySnap.Transform;
                bool intersected = false;
                // The panel the sound goes through (its smallest dimension is its thickness, the other
                // two its face) and how many times it goes through it.
                Vector3 panel = def.Collider.Size;
                int layers = 1;

                if (def.Collider.Shape == ColliderShape.Box)
                {
                    if (GeometryUtils.RayIntersectsOBB(nudgedStart, rayDir, transform.Position, def.Collider.Size, transform.Rotation, out float entry) &&
                        GeometryUtils.RayIntersectsOBB(nudgedEnd, -rayDir, transform.Position, def.Collider.Size, transform.Rotation, out float exitFromEnd))
                    {
                        float exitFromStart = dist - exitFromEnd;
                        if (exitFromStart > entry)
                        {
                            intersected = true;
                            if (def.Acoustics.IsHollow)
                            {
                                // A hollow shell is walls of its shell thickness round an empty inside:
                                // one wall in and one out, or one if either end is inside it.
                                float shell = def.Acoustics.ShellThickness > 0 ? def.Acoustics.ShellThickness : 0.2f;
                                var s = def.Collider.Size;
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
                    intersected = def.Collider.Shape switch
                    {
                        ColliderShape.Sphere => GeometryUtils.RayIntersectsSphere(nudgedStart, rayDir, transform.Position, def.Collider.Size.X / 2.0f, out en, out ex),
                        ColliderShape.Cylinder => GeometryUtils.RayIntersectsCylinder(nudgedStart, rayDir, transform.Position, def.Collider.Size.X / 2.0f, def.Collider.Size.Y, out en, out ex),
                        ColliderShape.Cone => GeometryUtils.RayIntersectsCone(nudgedStart, rayDir, transform.Position, def.Collider.Size.X / 2.0f, def.Collider.Size.Y, out en, out ex),
                        _ => false
                    };
                    if (intersected)
                    {
                        float clampedEx = Math.Min(ex, dist);
                        // A round thing is as thick as the chord the sound crosses it by.
                        if (clampedEx > en) { panel = new Vector3(clampedEx - en, def.Collider.Size.X, def.Collider.Size.Y); }
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
                    var (gl, gm, gh) = WallTransmission.BandGains(def.Material.Material, panel,
                                                                  new WallBuild(def.Acoustics.LeafMetres, def.Acoustics.StudSpacingMetres));
                    for (int k = 0; k < layers; k++) { eqLow *= gl; eqMid *= gm; eqHigh *= gh; }
                }
            }
        }

        // No floor lets sound through whatever the walls are. Blocked is what the loudest band lost;
        // bleed is the bands' mean.
        cumulativeBleed = (eqLow + eqMid + eqHigh) / 3f;
        maxBlock = Math.Clamp(1f - MathF.Max(eqLow, MathF.Max(eqMid, eqHigh)), 0f, 1f);
    }

    /// <summary>
    /// The first solid along each of <paramref name="directions"/>, into buffers the caller owns (it runs
    /// every audio frame): distance, absorption and material, or maxDist, 1 and "Generic" for a miss.
    /// <paramref name="staticOnly"/> leaves out what moves: a ray from inside a car would find the car.
    /// </summary>
    public void RaycastAll(WorldSnapshot world, Vector3 start, Vector3[] directions, float maxDist,
                           float[] distances, float[] absorptions, string[] materials, bool staticOnly = false)
    {
        for (int i = 0; i < directions.Length; i++)
        {
            distances[i] = maxDist;
            absorptions[i] = 1.0f;
            materials[i] = "Generic";
        }

        List<EntitySnapshot> entitiesToTest;
        if (UsesTriangles(world))
        {
            var geo = world.Geometry!;
            var filter = Filter(world);
            for (int i = 0; i < directions.Length; i++)
            {
                if (!geo.Enter(start, directions[i], maxDist, OpenFPS.Common.Geometry.GeometryLayers.Physical, ref filter, out var hit)) continue;
                if (!(hit.T < distances[i])) continue;
                ref readonly var surface = ref geo.SurfaceOf(hit);
                distances[i] = hit.T;
                absorptions[i] = surface.Absorption > 0 ? surface.Absorption : AcousticRegistry.GetProperties(surface.Material).Absorption;
                materials[i] = string.IsNullOrEmpty(surface.Material) ? "Generic" : surface.Material;
            }
            entitiesToTest = Others(world, staticOnly);
        }
        else entitiesToTest = GetEntitiesToTest(world, start, maxDist, staticOnly);

        foreach (var entitySnap in entitiesToTest)
        {
            var def = entitySnap.Definition;
            if (def.Collider.Size.X <= 0 || !def.Collider.IsSolid) continue;

            var transform = entitySnap.Transform;
            var registryProps = AcousticRegistry.GetProperties(def.Material.Material);
            float absorption = (def.Acoustics.Absorption > 0) ? def.Acoustics.Absorption : registryProps.Absorption;
            string materialName = string.IsNullOrEmpty(def.Material.Material) ? "Generic" : def.Material.Material;

            for (int i = 0; i < directions.Length; i++)
            {
                bool intersected = false;
                float dist = maxDist;

                if (def.Collider.Shape == ColliderShape.Box)
                {
                    intersected = GeometryUtils.RayIntersectsOBB(start, directions[i], transform.Position, def.Collider.Size, transform.Rotation, out dist);
                }
                else
                {
                    intersected = def.Collider.Shape switch
                    {
                        ColliderShape.Sphere => GeometryUtils.LineIntersectsSphere(start, start + directions[i] * maxDist, transform.Position, def.Collider.Size.X / 2.0f),
                        ColliderShape.Cylinder or ColliderShape.Cone => GeometryUtils.RayIntersectsCylinder(start, directions[i], transform.Position, def.Collider.Size.X / 2.0f, def.Collider.Size.Y, out dist),
                        _ => false
                    };
                    if (intersected && def.Collider.Shape == ColliderShape.Sphere) dist = Vector3.Distance(start, transform.Position) - (def.Collider.Size.X / 2f);
                }

                if (intersected && dist >= 0 && dist < distances[i])
                {
                    distances[i] = dist;
                    absorptions[i] = absorption;
                    materials[i] = materialName;
                }
            }
        }
    }

    /// <summary>The acoustic region a position is in: by the regions' boxes, else the voxel grid.</summary>
    public int GetRegionAt(WorldSnapshot world, Vector3 position)
    {
        if (world.AcousticMap == null) return AcousticConstants.GlobalRegionId;

        // Where boxes overlap the smallest holding the point wins (a room inside a hall), not the first
        // in the map's list, which made the room depend on authoring order (Cody, 2026-09-28).
        int best = int.MinValue;
        float bestVolume = float.MaxValue;
        foreach (var regId in world.AcousticMap.Regions.Keys)
        {
            if (regId == AcousticConstants.GlobalRegionId) continue; 
            
            Vector3 size = Vector3.Zero;
            Vector3 pos = Vector3.Zero;
            Quaternion rot = Quaternion.Identity;

            if (world.Entities.TryGetValue(regId, out var snap))
            {
                pos = snap.Transform.Position;
                rot = snap.Transform.Rotation;
                size = (snap.Definition.Collider.Size.X > 0) 
                    ? snap.Definition.Collider.Size 
                    : snap.Definition.Region.RoomSize;
            }
            else if (world.AcousticMap.RegionPositions.TryGetValue(regId, out var regPos)
                     && world.AcousticMap.Regions.TryGetValue(regId, out var reg))
            {
                // Both asked: a streamed map swaps its tables one after another, and for a moment one can
                // have a room the other has not.
                pos = regPos;
                rot = world.AcousticMap.RegionRotations.GetValueOrDefault(regId, Quaternion.Identity);
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
        int coarse = world.AcousticMap.VoxelGrid.GetRegionAt(position);
        if (coarse != AcousticConstants.GlobalRegionId && HasBox(world, coarse))
            return AcousticConstants.GlobalRegionId;
        return coarse;
    }

    /// <summary>Whether a region has an explicit volume that <see cref="GetRegionAt"/> can test.</summary>
    private static bool HasBox(WorldSnapshot world, int regId)
    {
        if (world.Entities.TryGetValue(regId, out var snap))
            return snap.Definition.Collider.Size.X > 0 || snap.Definition.Region.RoomSize.X > 0;
        return world.AcousticMap!.RegionPositions.ContainsKey(regId)
            && world.AcousticMap.Regions.TryGetValue(regId, out var reg) && reg.RoomSize.X > 0;
    }

    public bool RaycastMaterial(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out float distance, out Vector3 normal, out string material, int ignoreEntityId = -1)
    {
        distance = maxDist;
        normal = -dir;
        material = "Generic";
        bool hit = false;

        List<EntitySnapshot> entitiesToTest;
        if (UsesTriangles(world))
        {
            var geo = world.Geometry!;
            var filter = Filter(world, ignoreEntityId, OwnEntityId);
            if (geo.Enter(start, dir, maxDist, OpenFPS.Common.Geometry.GeometryLayers.Physical, ref filter, out var h) && h.T < distance)
            {
                distance = h.T;
                normal = h.Normal;
                material = geo.SurfaceOf(h).Material;
                hit = true;
            }
            entitiesToTest = Others(world, staticOnly: false);
        }
        else entitiesToTest = GetEntitiesToTest(world, start, maxDist);

        foreach (var entitySnap in entitiesToTest)
        {
            if (entitySnap.Id == ignoreEntityId || entitySnap.Id == OwnEntityId) continue;
            var def = entitySnap.Definition;
            if (!def.Collider.IsSolid) continue;

            float d = maxDist;
            bool intersected = false;
            Vector3 n = -dir;

            if (def.Collider.Shape == ColliderShape.Box)
            {
                intersected = RayIntersectsOBBWithNormal(start, dir, entitySnap.Transform.Position, def.Collider.Size, entitySnap.Transform.Rotation, out d, out n);
            }
            else if (def.Collider.Shape == ColliderShape.Sphere)
            {
                intersected = GeometryUtils.LineIntersectsSphere(start, start + dir * maxDist, entitySnap.Transform.Position, def.Collider.Size.X / 2.0f);
                if (intersected)
                {
                    d = Vector3.Distance(start, entitySnap.Transform.Position) - (def.Collider.Size.X / 2f);
                    Vector3 hitPos = start + dir * d;
                    n = Vector3.Normalize(hitPos - entitySnap.Transform.Position);
                }
            }

            if (intersected && d >= 0 && d < distance)
            {
                distance = d;
                normal = n;
                material = def.Material.Material;
                hit = true;
            }
        }

        return hit;
    }

    private bool RayIntersectsOBBWithNormal(Vector3 start, Vector3 dir, Vector3 boxPos, Vector3 boxSize, Quaternion boxRot, out float distance, out Vector3 normal)
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

    private bool RayIntersectsAABBWithNormal(Vector3 start, Vector3 dir, Vector3 boxPos, Vector3 boxSize, out float distance, out Vector3 normal)
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

    /// <summary>
    /// The first solid thing along a line of sight, out to <paramref name="maxDist"/>: what a scope sees,
    /// or what stops it seeing further.
    ///
    /// Without triangles the static grid is walked in 40 m stretches, stopping at the first with a hit:
    /// asking for half a 600 m ray round its middle is the whole city. Players are not solid to sight
    /// here (the scope finds them itself), and glass is seen through unless <paramref name="glassBlocks"/>.
    /// </summary>
    public bool CastSight(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity,
                          out float hitDistance, Func<EntitySnapshot, bool>? skip = null, bool glassBlocks = false)
    {
        hitEntity = default;
        hitDistance = maxDist;
        bool found = false;
        dir = Vector3.Normalize(dir);

        bool Test(in EntitySnapshot e, ref float best)
        {
            if (e.Id == OwnEntityId) return false;
            var def = e.Definition;
            if (def.Collider.Size.X <= 0f || !def.Collider.IsSolid) return false;
            if (def.Type is EntityType.Player) return false;
            if (!glassBlocks && string.Equals(def.Material.Material, "Glass", StringComparison.OrdinalIgnoreCase)) return false;
            if (skip != null && skip(e)) return false;
            float d;
            bool hit;
            var t = e.Transform;
            switch (def.Collider.Shape)
            {
                case ColliderShape.Box:
                    hit = GeometryUtils.RayIntersectsOBB(start, dir, t.Position, def.Collider.Size, t.Rotation, out d);
                    break;
                case ColliderShape.Sphere:
                    hit = GeometryUtils.RayIntersectsSphere(start, dir, t.Position, def.Collider.Size.X * 0.5f, out d, out _);
                    break;
                case ColliderShape.Cylinder:
                case ColliderShape.Cone:
                    hit = GeometryUtils.RayIntersectsCylinder(start, dir, t.Position, def.Collider.Size.X * 0.5f, def.Collider.Size.Y, out d);
                    break;
                default:
                    return false;
            }
            if (!hit || d < 0f || d >= best) return false;
            best = d;
            return true;
        }

        if (UsesTriangles(world))
        {
            var geo = world.Geometry!;
            var filter = new PredicateFilter
            {
                Base = Filter(world, OwnEntityId), World = world, Skips = skip, GlassBlocks = glassBlocks, SkipGlass = true,
            };
            if (geo.Enter(start, dir, maxDist, OpenFPS.Common.Geometry.GeometryLayers.Sight, ref filter, out var h)
                && h.T < hitDistance && world.Entities.TryGetValue(h.Owner, out var e))
            {
                hitDistance = h.T; hitEntity = e; found = true;
            }
            var others = Others(world, staticOnly: true);
            for (int i = 0; i < others.Count; i++)
                if (Test(others[i], ref hitDistance)) { hitEntity = others[i]; found = true; }
        }
        else
        {
            const float Stretch = 40f;
            for (float from = 0f; from < maxDist && from < hitDistance; from += Stretch)
            {
                float to = MathF.Min(maxDist, from + Stretch);
                var candidates = GetEntitiesToTest(world, start + dir * (0.5f * (from + to)), 0.5f * (to - from) + 2f, staticOnly: true);
                for (int i = 0; i < candidates.Count; i++)
                    if (Test(candidates[i], ref hitDistance)) { hitEntity = candidates[i]; found = true; }
            }
        }
        for (int i = 0; i < world.DynamicEntities.Count; i++)
        {
            var e = world.DynamicEntities[i];
            if (Test(e, ref hitDistance)) { hitEntity = e; found = true; }
        }
        return found;
    }

    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity, out float hitDistance)
        => RaycastSingle(world, start, dir, maxDist, null, out hitEntity, out hitDistance);

    /// <summary>The nearest thing along a ray that <paramref name="accept"/> admits; everything else is
    /// looked straight through.</summary>
    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, Func<EntitySnapshot, bool>? accept,
                              out EntitySnapshot hitEntity, out float hitDistance)
    {
        hitDistance = maxDist;
        hitEntity = default;
        bool found = false;

        Vector3 center = start + (dir * (maxDist / 2.0f));
        List<EntitySnapshot> entitiesToTest;
        if (UsesTriangles(world))
        {
            var geo = world.Geometry!;
            var filter = new PredicateFilter { Base = Filter(world), World = world, Accepts = accept };
            if (geo.Enter(start, dir, maxDist, OpenFPS.Common.Geometry.GeometryLayers.Physical, ref filter, out var h)
                && h.T < hitDistance && world.Entities.TryGetValue(h.Owner, out var e))
            {
                hitDistance = h.T; hitEntity = e; found = true;
            }
            entitiesToTest = Others(world, staticOnly: false);
        }
        else entitiesToTest = GetEntitiesToTest(world, center, (maxDist / 2.0f) + 1.0f);

        foreach (var entitySnap in entitiesToTest)
        {
            var def = entitySnap.Definition;
            if (def.Collider.Size.X <= 0) continue;
            if (accept != null && !accept(entitySnap)) continue;
            var transform = entitySnap.Transform;
            bool intersected = false;
            float dist = maxDist;

            if (def.Collider.Shape == ColliderShape.Box)
            {
                intersected = GeometryUtils.RayIntersectsOBB(start, dir, transform.Position, def.Collider.Size, transform.Rotation, out dist);
            }
            else
            {
                intersected = def.Collider.Shape switch
                {
                    ColliderShape.Sphere => GeometryUtils.LineIntersectsSphere(start, start + dir * maxDist, transform.Position, def.Collider.Size.X / 2.0f),
                    ColliderShape.Cylinder or ColliderShape.Cone => GeometryUtils.RayIntersectsCylinder(start, dir, transform.Position, def.Collider.Size.X / 2.0f, def.Collider.Size.Y, out dist),
                    _ => false
                };
                if (intersected && def.Collider.Shape == ColliderShape.Sphere) dist = Vector3.Distance(start, transform.Position) - (def.Collider.Size.X / 2f);
            }

            if (intersected && dist >= 0 && dist < hitDistance)
            {
                hitDistance = dist;
                hitEntity = entitySnap;
                found = true;
            }
        }
        return found;
    }
}