using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Core;

/// <summary>
/// Working out whether a composite is a ROOM, and what kind.
///
/// Nobody should have to author the inside of a building they just built. If you put four walls, a
/// floor and a roof around yourself, you are indoors — that is a fact about the geometry, not a
/// property somebody remembered to tick. So the question "is this a room" is asked of the parts
/// themselves, and answered the same way for a shed, a cathedral and the cab of a lorry.
///
/// Three things have to be true, and each of them rules out a thing that is not a room:
///
///   IT HAS TO BE BIG ENOUGH TO BE INSIDE. A fence is a wall with more wall next to it; whatever its
///   footprint, one of its dimensions is a wall's thickness, and you cannot be inside that.
///
///   IT HAS TO BE MOSTLY EMPTY. A stack of crates the size of a garage is not a garage. If the parts
///   fill their own bounding box there is nowhere in it to stand.
///
///   MOST OF IT HAS TO BE COVERED. A room is a room because of what is between you and the sky. Four
///   faces out of six is a roofless courtyard, which is generous and deliberately so — the acoustics
///   of standing in a walled yard genuinely are closer to a room than to a field.
///
/// What the room is MADE of comes from the parts too: each of the six faces takes the material of
/// whichever part covers most of it. A glass-sided office is bright, a carpeted one is not, and a
/// car with metal panels rings — because of what they are built from, not because anyone said so.
/// </summary>
public static class CompositeAcoustics
{
    /// <summary>The smallest a room can be in any direction, metres. Under this you are not inside
    /// it, you are next to it.</summary>
    public const float MinimumRoomDimension = 1.0f;

    /// <summary>How much of its own bounding box a composite may be made of and still have an inside.</summary>
    public const float MaxSolidFraction = 0.6f;

    /// <summary>How much of a face has to be covered for that face to count as a wall.</summary>
    public const float FaceCoverage = 0.35f;

    /// <summary>How many of the six faces have to be walls. Four is a roofless yard.</summary>
    public const int MinimumCoveredFaces = 4;

    /// <summary>The face order the whole codebase uses: floor, ceiling, north, south, east, west.</summary>
    private static readonly (int Axis, float Side)[] Faces =
    {
        (1, -1f),   // Floor
        (1, +1f),   // Ceiling
        (2, +1f),   // North
        (2, -1f),   // South
        (0, +1f),   // East
        (0, -1f),   // West
    };

    /// <summary>
    /// The room a set of parts makes, if they make one.
    ///
    /// <paramref name="centre"/> comes back in the composite's own frame, because a composite's origin
    /// is where it meets the GROUND and a room's centre is half its height above that. Putting the
    /// room volume at the origin would place its ceiling at your knees.
    /// </summary>
    public static bool Derive(World world, List<Entity> parts, string name,
                              out RegionComponent room, out Vector3 centre)
    {
        room = default;
        var size = Bounds(world, parts, out centre);
        if (parts.Count == 0) return false;

        // 1. Big enough to be inside.
        if (MathF.Min(size.X, MathF.Min(size.Y, size.Z)) < MinimumRoomDimension) return false;

        // 2. Mostly empty.
        float boxVolume = size.X * size.Y * size.Z;
        float solid = 0f;
        foreach (var part in parts)
        {
            if (!world.Has<ColliderComponent>(part)) continue;
            var s = world.Get<ColliderComponent>(part).Size;
            solid += MathF.Abs(s.X * s.Y * s.Z);
        }
        if (boxVolume <= 0f || solid / boxVolume > MaxSolidFraction) return false;

        // 3. Mostly covered — and, on the way, what each face is made of.
        var materials = new int[6];
        int walls = 0;
        for (int f = 0; f < Faces.Length; f++)
        {
            float coverage = Face(world, parts, size, centre, Faces[f].Axis, Faces[f].Side, out string material);
            if (coverage >= FaceCoverage) walls++;
            materials[f] = AcousticRegistry.TryGetResonanceIndex(material, out int index)
                ? index
                : AcousticRegistry.TryGetResonanceIndex("Generic", out int fallback) ? fallback : 0;
        }
        if (walls < MinimumCoveredFaces) return false;

        room = new RegionComponent
        {
            FriendlyName = string.IsNullOrWhiteSpace(name) ? "Inside" : name,
            IsIndoor = true,
            Environment = AcousticEnvironmentType.Atmospheric,
            RoomSize = size,
            ReverbTimeScale = 1.0f,
            Materials = materials,
            AmbienceId = "",
        };
        return true;
    }

    /// <summary>
    /// How much of one face is covered, and by what.
    ///
    /// A part belongs to a face if its OUTER edge is near that face — near meaning within a quarter of
    /// the box's depth, or half a metre, whichever is more forgiving. That tolerance is what lets a
    /// wall that is a little inboard of the corner still count as that wall, and it is why a pillar in
    /// the middle of a room covers nothing.
    /// </summary>
    private static float Face(World world, List<Entity> parts, Vector3 size, Vector3 centre,
                              int axis, float side, out string material)
    {
        material = "Generic";
        int a = axis, b = (axis + 1) % 3, c = (axis + 2) % 3;
        float faceArea = Component(size, b) * Component(size, c);
        if (faceArea <= 0f) return 0f;

        float plane = Component(centre, a) + side * Component(size, a) * 0.5f;
        float depth = MathF.Max(0.5f, Component(size, a) * 0.25f);

        float covered = 0f, best = 0f;
        foreach (var part in parts)
        {
            if (!world.Has<ColliderComponent>(part) || !world.Has<ParentComponent>(part)) continue;
            var parent = world.Get<ParentComponent>(part);
            var half = AxisAlignedHalfExtents(world.Get<ColliderComponent>(part).Size * 0.5f, parent.LocalRotation);

            float outer = Component(parent.LocalPosition, a) + side * Component(half, a);
            if (MathF.Abs(plane - outer) > depth) continue;

            float area = 4f * Component(half, b) * Component(half, c);
            covered += area;
            if (area > best)
            {
                best = area;
                material = world.Has<MaterialComponent>(part)
                    ? world.Get<MaterialComponent>(part).Material ?? "Generic"
                    : "Generic";
            }
        }
        return MathF.Min(1f, covered / faceArea);
    }

    /// <summary>
    /// The bounding box of a set of parts in their composite's own frame, and its centre.
    ///
    /// Rotated parts are measured by the box that CONTAINS them, not by their own dimensions: a wall
    /// turned ninety degrees is two metres of wall across, not half a metre, and measuring it the
    /// naive way makes every building that has corners come out the wrong shape.
    /// </summary>
    public static Vector3 Bounds(World world, List<Entity> parts, out Vector3 centre)
    {
        centre = Vector3.Zero;
        if (parts.Count == 0) return Vector3.Zero;

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var part in parts)
        {
            if (!world.Has<ParentComponent>(part)) continue;
            var parent = world.Get<ParentComponent>(part);
            var half = world.Has<ColliderComponent>(part)
                ? AxisAlignedHalfExtents(world.Get<ColliderComponent>(part).Size * 0.5f, parent.LocalRotation)
                : Vector3.Zero;
            min = Vector3.Min(min, parent.LocalPosition - half);
            max = Vector3.Max(max, parent.LocalPosition + half);
        }
        if (min.X > max.X) return Vector3.Zero;

        centre = (min + max) * 0.5f;
        return Vector3.Max(max - min, new Vector3(0.01f));
    }

    /// <summary>Half-extents of the axis-aligned box that contains a rotated one.</summary>
    public static Vector3 AxisAlignedHalfExtents(Vector3 half, Quaternion rotation)
    {
        var x = Vector3.Transform(new Vector3(half.X, 0f, 0f), rotation);
        var y = Vector3.Transform(new Vector3(0f, half.Y, 0f), rotation);
        var z = Vector3.Transform(new Vector3(0f, 0f, half.Z), rotation);
        return new Vector3(
            MathF.Abs(x.X) + MathF.Abs(y.X) + MathF.Abs(z.X),
            MathF.Abs(x.Y) + MathF.Abs(y.Y) + MathF.Abs(z.Y),
            MathF.Abs(x.Z) + MathF.Abs(y.Z) + MathF.Abs(z.Z));
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
}
