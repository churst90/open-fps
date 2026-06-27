using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: The master geometry engine for the client.
/// Provides high-performance raycasting, region detection, and portal pathfinding.
/// Used by Physics (prediction), Input (interaction), and Audio (occlusion/diffraction).
/// </summary>
public class SpatialService
{
    public int OwnEntityId { get; set; } = -1;

    private IEnumerable<EntitySnapshot> GetEntitiesToTest(WorldSnapshot world, Vector3 center, float radius)
    {
        var ids = world.StaticGrid?.GetItemsInRadius(center, radius);
        if (ids != null && ids.Any())
        {
            return ids.Select(id => world.Entities[id]).Concat(world.DynamicEntities);
        }
        // Fallback to all entities if grid returns nothing or is null
        return world.Entities.Values;
    }

    /// <summary>
    /// Calculates the occlusion factor (0.0 to 1.0) between two points.
    /// Also outputs the 'bleed' factor (how much sound passes through materials).
    /// </summary>
    public float GetOcclusionFactor(WorldSnapshot world, Vector3 start, Vector3 end, out float bleed, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
    {
        GetOcclusionData(world, start, end, out float block, out bleed, out _, out _, out _, ignoreEntityId, ignoreEntityId2);
        return block;
    }

    /// <summary>
    /// Performs a high-fidelity 5-ray cross-pattern sampling between two points.
    /// Used to calculate partial occlusion when sound diffraction is relevant.
    /// Both ignoreEntityId and ignoreEntityId2 are excluded from all rays to ensure symmetric sampling.
    /// </summary>
    public void GetMultiPointOcclusionData(WorldSnapshot world, Vector3 start, Vector3 end, out float maxBlock, out float cumulativeBleed, out float eqLow, out float eqMid, out float eqHigh, int ignoreEntityId = -1, int ignoreEntityId2 = -1)
    {
        Vector3 dir = end - start;
        Vector3 right = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, dir));
        // C6: Adaptive spread — scale offset by distance so nearby sounds use a narrow spread
        // and distant sounds use a wider one, matching the physical projection of the emitter.
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
    /// Performs an exhaustive raycast between two points to calculate cumulative transmission and frequency-specific EQ.
    /// Supports multiple wall layers.
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

        Vector3 rayMin = Vector3.Min(nudgedStart, nudgedEnd);
        Vector3 rayMax = Vector3.Max(nudgedStart, nudgedEnd);

        Vector3 center = (start + end) / 2.0f;
        // Search a wider radius to ensure we catch large static objects like foundations
        var entitiesToTest = GetEntitiesToTest(world, center, (dist / 2.0f) + 10.0f);

        foreach (var entitySnap in entitiesToTest)
        {
            if (entitySnap.Id == ignoreEntityId) continue;
            if (entitySnap.Id == ignoreEntityId2) continue;
            var def = entitySnap.Definition;
            
            if (def.Collider.Size.X > 0 && def.Collider.IsSolid)
            {
                var transform = entitySnap.Transform;
                bool intersected = false;
                float thickness = 0.2f;
                Vector3 hitPoint = transform.Position; // updated in Box branch

                if (def.Collider.Shape == ColliderShape.Box)
                {
                    if (GeometryUtils.RayIntersectsOBB(nudgedStart, rayDir, transform.Position, def.Collider.Size, transform.Rotation, out float entry) &&
                        GeometryUtils.RayIntersectsOBB(nudgedEnd, -rayDir, transform.Position, def.Collider.Size, transform.Rotation, out float exitFromEnd))
                    {
                        float exitFromStart = dist - exitFromEnd;
                        if (exitFromStart > entry)
                        {
                            intersected = true;
                            hitPoint = nudgedStart + rayDir * entry;
                            if (def.Acoustics.IsHollow)
                            {
                                float shell = def.Acoustics.ShellThickness > 0 ? def.Acoustics.ShellThickness : 0.2f;
                                thickness = Math.Min(exitFromStart - entry, shell * 2.0f);
                            }
                            else
                            {
                                thickness = Math.Max(0.1f, exitFromStart - entry);
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
                        if (clampedEx > en) { thickness = Math.Max(0.1f, clampedEx - en); hitPoint = nudgedStart + rayDir * en; }
                        else intersected = false;
                    }
                }

                // C4: Portal-aware occlusion.
                // If the ray's hit point lies within a portal aperture, the opening is unobstructed — skip this wall.
                if (intersected && world.AcousticMap != null)
                {
                    foreach (var kvp in world.AcousticMap.Portals.Values)
                    {
                        if (Vector3.Distance(hitPoint, kvp.Position) < kvp.Portal.ApertureSize)
                        {
                            intersected = false;
                            break;
                        }
                    }
                }

                if (intersected)
                {
                    float thicknessFactor = MathF.Log10(thickness + 1.0f) * 2.5f;

                    float transLow = MathF.Pow(Math.Max(0.01f, def.Acoustics.TransmissionLow), thicknessFactor);
                    float transMid = MathF.Pow(Math.Max(0.005f, def.Acoustics.TransmissionMid), thicknessFactor);
                    float transHigh = MathF.Pow(Math.Max(0.001f, def.Acoustics.TransmissionHigh), thicknessFactor);

                    // Multiplicative: Total transmission is the product of all layers
                    cumulativeBleed *= (transLow + transMid + transHigh) / 3.0f;
                    eqLow *= transLow;
                    eqMid *= transMid;
                    eqHigh *= transHigh;
                }
            }
        }

        // Final Occlusion is 1.0 - cumulative transmission
        maxBlock = Math.Clamp(1.0f - cumulativeBleed, 0.0f, 1.0f);

        // Clamp final EQ to safe ranges
        eqLow = Math.Clamp(eqLow, 0.05f, 1.0f);
        eqMid = Math.Clamp(eqMid, 0.01f, 1.0f);
        eqHigh = Math.Clamp(eqHigh, 0.001f, 1.0f);

        // Apply the Occlusion Floor from the map metadata to prevent total silence
        float floor = world.AcousticMap?.OcclusionFloor ?? 0.05f;
        maxBlock = Math.Min(maxBlock, 1.0f - floor);
        cumulativeBleed = Math.Max(cumulativeBleed, floor);
    }

    /// <summary>
    /// Performs a multi-raycast in given directions. Used for echolocation.
    /// </summary>
    public void RaycastAll(WorldSnapshot world, Vector3 start, Vector3[] directions, float maxDist, out float[] distances, out float[] absorptions, out string[] materials)
    {
        distances = new float[directions.Length];
        absorptions = new float[directions.Length];
        materials = new string[directions.Length];

        for (int i = 0; i < directions.Length; i++)
        {
            distances[i] = maxDist;
            absorptions[i] = 1.0f;
            materials[i] = "Generic";
        }

        var entitiesToTest = GetEntitiesToTest(world, start, maxDist);

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

    /// <summary>
    /// Searches for nearby large reflective surfaces (buildings) to calculate echo delays.
    /// This implementation is geometric: it finds the nearest faces of the closest buildings.
    /// </summary>
    public void GetReflectionData(WorldSnapshot world, Vector3 position, float maxDist, out List<(Vector3 normal, float distance, float absorption, float scattering, int buildingId)> reflections)
    {
        reflections = new();

        var entitiesToTest = GetEntitiesToTest(world, position, maxDist + 10.0f);

        // Find the nearest solid colliders
        var candidates = entitiesToTest
            .Where(e => e.Definition.Collider.Size.X > 0 && e.Definition.Collider.IsSolid && e.Definition.Type == EntityType.StaticObject)
            .Select(e => new { Id = e.Id, Pos = e.Transform.Position, Rot = e.Transform.Rotation, Size = e.Definition.Collider.Size, Shape = e.Definition.Collider.Shape, Dist = Vector3.Distance(position, e.Transform.Position), Def = e.Definition })
            .Where(c => c.Dist < maxDist + (c.Size.Length() / 2.0f))
            .OrderBy(c => c.Dist)
            .Take(8);

        foreach (var c in candidates)
        {
            Matrix4x4 worldToLocal = Matrix4x4.CreateTranslation(-c.Pos) * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(c.Rot));
            Vector3 localPos = Vector3.Transform(position, worldToLocal);
            
            float minDist = float.MaxValue;
            Vector3 worldNormal = Vector3.Zero;
            float shapeScattering = 0.0f;
            bool valid = false;

            if (c.Shape == ColliderShape.Box)
            {
                Vector3 halfSize = c.Size / 2.0f;
                Vector3 localNormal = Vector3.Zero;
                float faceArea = 0;

                float dXp = Math.Abs(localPos.X - halfSize.X); if (dXp < minDist) { minDist = dXp; localNormal = Vector3.UnitX; faceArea = c.Size.Y * c.Size.Z; }
                float dXn = Math.Abs(localPos.X + halfSize.X); if (dXn < minDist) { minDist = dXn; localNormal = -Vector3.UnitX; faceArea = c.Size.Y * c.Size.Z; }
                float dYp = Math.Abs(localPos.Y - halfSize.Y); if (dYp < minDist) { minDist = dYp; localNormal = Vector3.UnitY; faceArea = c.Size.X * c.Size.Z; }
                float dYn = Math.Abs(localPos.Y + halfSize.Y); if (dYn < minDist) { minDist = dYn; localNormal = -Vector3.UnitY; faceArea = c.Size.X * c.Size.Z; }
                float dZp = Math.Abs(localPos.Z - halfSize.Z); if (dZp < minDist) { minDist = dZp; localNormal = Vector3.UnitZ; faceArea = c.Size.X * c.Size.Y; }
                float dZn = Math.Abs(localPos.Z + halfSize.Z); if (dZn < minDist) { minDist = dZn; localNormal = -Vector3.UnitZ; faceArea = c.Size.X * c.Size.Y; }

                if (minDist < maxDist && faceArea > 2.0f)
                {
                    bool isInside = Math.Abs(localPos.X) < halfSize.X && Math.Abs(localPos.Y) < halfSize.Y && Math.Abs(localPos.Z) < halfSize.Z;
                    Vector3 finalLocalNormal = isInside ? -localNormal : localNormal;
                    worldNormal = Vector3.TransformNormal(finalLocalNormal, Matrix4x4.CreateFromQuaternion(c.Rot));
                    shapeScattering = 0.0f; // Flat wall = low scattering
                    valid = true;
                }
            }
            else if (c.Shape == ColliderShape.Sphere)
            {
                float radius = c.Size.X / 2f;
                minDist = Math.Max(0, c.Dist - radius);
                if (minDist < maxDist)
                {
                    Vector3 dir = position - c.Pos;
                    if (dir.LengthSquared() > 0.0001f) worldNormal = Vector3.Normalize(dir);
                    else worldNormal = Vector3.UnitY;
                    shapeScattering = 0.6f; // Curved = high scattering
                    valid = true;
                }
            }
            else if (c.Shape == ColliderShape.Cylinder || c.Shape == ColliderShape.Cone)
            {
                float radius = c.Size.X / 2f;
                float height = c.Size.Y;
                
                Vector3 localDir = new Vector3(localPos.X, 0, localPos.Z);
                float distXZ = localDir.Length();
                if (distXZ > 0.001f)
                {
                    Vector3 localNormal = localDir / distXZ;
                    
                    if (c.Shape == ColliderShape.Cone)
                    {
                        float angle = MathF.Atan2(radius, height);
                        localNormal.Y = MathF.Sin(angle);
                        localNormal = Vector3.Normalize(localNormal);
                    }
                    
                    worldNormal = Vector3.TransformNormal(localNormal, Matrix4x4.CreateFromQuaternion(c.Rot));
                    
                    // Simple distance approximation for cylinder/cone reflection point
                    minDist = Math.Max(0, c.Dist - radius);
                    
                    if (minDist < maxDist)
                    {
                        shapeScattering = 0.5f; // Curved
                        valid = true;
                    }
                }
            }

            if (valid)
            {
                Vector3 dirToSurface = -worldNormal;
                
                if (Vector3.Dot(dirToSurface, worldNormal) > 0)
                {
                    valid = false;
                }
                else if (RaycastSingle(world, position, dirToSurface, minDist - 0.1f, out var hitEntity, out float hitDist))
                {
                    if (hitEntity.Id != c.Id) valid = false;
                }
            }

            if (valid)
            {
                string matName = string.IsNullOrEmpty(c.Def.Material.Material) ? "Generic" : c.Def.Material.Material;
                var props = AcousticRegistry.GetProperties(matName);
                
                float absorption = c.Def.Acoustics.Absorption > 0 ? c.Def.Acoustics.Absorption : props.Absorption;
                float finalScattering = Math.Clamp(shapeScattering + props.Scattering, 0f, 1f);
                
                reflections.Add((worldNormal, minDist, absorption, finalScattering, c.Id));
            }
        }
    }

    /// <summary>
    /// Identifies which acoustic region a position resides in.
    /// Prefers high-precision OBB volumes, falls back to voxel grid.
    /// </summary>
    public int GetRegionAt(WorldSnapshot world, Vector3 position)
    {
        if (world.AcousticMap == null) return AcousticConstants.GlobalRegionId;

        // 1. High Precision: Check explicit region volumes
        foreach (var regId in world.AcousticMap.Regions.Keys)
        {
            if (regId == AcousticConstants.GlobalRegionId) continue; 
            
            // Try to get the region's size and transform
            Vector3 size = Vector3.Zero;
            Vector3 pos = Vector3.Zero;
            Quaternion rot = Quaternion.Identity;

            if (world.Entities.TryGetValue(regId, out var snap))
            {
                pos = snap.Transform.Position;
                rot = snap.Transform.Rotation;
                // Prefer explicit collider if it exists, otherwise use Region RoomSize
                size = (snap.Definition.Collider.Size.X > 0) 
                    ? snap.Definition.Collider.Size 
                    : snap.Definition.Region.RoomSize;
            }
            else if (world.AcousticMap.RegionPositions.TryGetValue(regId, out var regPos))
            {
                pos = regPos;
                rot = world.AcousticMap.RegionRotations.GetValueOrDefault(regId, Quaternion.Identity);
                size = world.AcousticMap.Regions[regId].RoomSize;
            }

            if (size.X > 0 && GeometryUtils.IsPointInOBB(position, pos, size, rot))
                return regId;
        }

        // 2. Low Precision Fallback: Voxel grid
        return world.AcousticMap.VoxelGrid.GetRegionAt(position);
    }

    public bool RaycastMaterial(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out float distance, out Vector3 normal, out string material, int ignoreEntityId = -1)
    {
        distance = maxDist;
        normal = -dir;
        material = "Generic";
        bool hit = false;

        var entitiesToTest = GetEntitiesToTest(world, start, maxDist);

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

    public bool RaycastSingle(WorldSnapshot world, Vector3 start, Vector3 dir, float maxDist, out EntitySnapshot hitEntity, out float hitDistance)
    {
        hitDistance = maxDist;
        hitEntity = default;
        bool found = false;

        Vector3 center = start + (dir * (maxDist / 2.0f));
        var entitiesToTest = GetEntitiesToTest(world, center, (maxDist / 2.0f) + 1.0f);

        foreach (var entitySnap in entitiesToTest)
        {
            var def = entitySnap.Definition;
            if (def.Collider.Size.X <= 0) continue;
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

    private bool RayIntersectsAABB(Vector3 start, Vector3 dir, Vector3 boxPos, Vector3 boxSize, out float distance)
    {
        Vector3 min = boxPos - (boxSize / 2.0f);
        Vector3 max = boxPos + (boxSize / 2.0f);
        float tmin = -float.MaxValue, tmax = float.MaxValue;
        distance = 0;
        if (Math.Abs(dir.X) > 0.000001f) {
            float t1 = (min.X - start.X) / dir.X, t2 = (max.X - start.X) / dir.X;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.X < min.X || start.X > max.X) return false;
        if (Math.Abs(dir.Y) > 0.000001f) {
            float t1 = (min.Y - start.Y) / dir.Y, t2 = (max.Y - start.Y) / dir.Y;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.Y < min.Y || start.Y > max.Y) return false;
        if (Math.Abs(dir.Z) > 0.000001f) {
            float t1 = (min.Z - start.Z) / dir.Z, t2 = (max.Z - start.Z) / dir.Z;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
        } else if (start.Z < min.Z || start.Z > max.Z) return false;
        if (tmax >= tmin && tmax > 0) { distance = tmin > 0 ? tmin : 0; return true; }
        return false;
    }

    public void InvalidateCache() { }
}