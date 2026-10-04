using System;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>
/// What is in front of you, and what is merely the ground. One answer for the three things that ask:
/// P (look ahead), the narration as you turn, and the bump when you walk into something.
///
/// THE GROUND IS NEVER NAMED. Z says where you stand; hearing "Concrete Floor" or "Brandt Court roof"
/// whenever you look down, turn, or brush a kerb tells you nothing Z did not, and it buries the one
/// name that matters. Ground is decided from GEOMETRY, not from names, because names are an author's
/// and the generator's to change: a box is ground when it is a slab (no taller than it is wide either
/// way) and either its top is within a step of your feet — the floor, a kerb, the road, the roof you
/// are on — or it is thin and its top is below your eyes, which is a floor or a tread you are looking
/// down at. A wall is taller than it is thick; a car, a sofa and a parapet stand above a step.
/// </summary>
public static class Sightline
{
    /// <summary>How far ahead the turn narration looks.</summary>
    public const float NarrationRange = 20f;

    /// <summary>A slab this thin or thinner, with its top below your eyes, is a floor or a tread.</summary>
    public const float FloorThickness = 0.35f;

    /// <summary>What a ray stopped at, and how far away.</summary>
    public readonly record struct Sighting(EntitySnapshot Entity, float Distance)
    {
        public string Name => NameOf(Entity);
    }

    /// <summary>The axis-aligned box round an entity's collider, in the world.</summary>
    public static (Vector3 Min, Vector3 Max) WorldBounds(in EntitySnapshot e)
    {
        var half = e.Definition.Collider.Size * 0.5f;
        var r = Matrix4x4.CreateFromQuaternion(e.Transform.Rotation);
        var ext = new Vector3(
            MathF.Abs(r.M11) * half.X + MathF.Abs(r.M21) * half.Y + MathF.Abs(r.M31) * half.Z,
            MathF.Abs(r.M12) * half.X + MathF.Abs(r.M22) * half.Y + MathF.Abs(r.M32) * half.Z,
            MathF.Abs(r.M13) * half.X + MathF.Abs(r.M23) * half.Y + MathF.Abs(r.M33) * half.Z);
        var c = e.Transform.Position;
        return (c - ext, c + ext);
    }

    /// <summary>
    /// Whether this is ground to a body whose feet are at <paramref name="feetY"/> and eyes at
    /// <paramref name="eyeY"/>: something you could stand on, at or below your feet, or a floor you
    /// are looking down at. See the class summary.
    /// </summary>
    public static bool IsGround(in EntitySnapshot e, float feetY, float eyeY)
    {
        var (min, max) = WorldBounds(e);
        float height = max.Y - min.Y;
        float span = MathF.Min(max.X - min.X, max.Z - min.Z);
        if (height > span) return false;                       // taller than it is wide: a wall, a post
        if (max.Y <= feetY + PhysicsConstants.StepHeight + 0.05f) return true;   // underfoot, a kerb, a step
        return height <= FloorThickness && max.Y < eyeY;       // a floor or tread you look down at
    }

    /// <summary>A top this deep or deeper, both ways, is one a foot can stand on: a tread is 28 cm.</summary>
    public const float StandableDepth = 0.25f;
    /// <summary>A ray passing this close under a standable top has met a step's nose, not a wall.</summary>
    public const float NoseMetres = 0.25f;

    /// <summary>
    /// Whether a ray that met this box at <paramref name="hitY"/> met GROUND: the box is ground
    /// (<see cref="IsGround"/>), or the ray only clipped the nose of a top you could stand on — a
    /// stair tread a few steps up, which a level look at knee, chest or eye height meets just under
    /// its top. A flight of stairs is built of columns, each as tall as its tread is high, and those
    /// are walls to <see cref="IsGround"/>; this is what keeps them from being named as you turn.
    /// A wall is too thin on top to stand on, so a ray grazing its top is still a ray meeting a wall.
    /// </summary>
    public static bool IsGroundAt(in EntitySnapshot e, float hitY, float feetY, float eyeY)
    {
        if (IsGround(e, feetY, eyeY)) return true;
        var (min, max) = WorldBounds(e);
        bool standable = MathF.Min(max.X - min.X, max.Z - min.Z) >= StandableDepth;
        return standable && max.Y - hitY <= NoseMetres && max.Y <= eyeY + NoseMetres;
    }

    /// <summary>What a thing is called: its name, and only if it has none, what it is made of.</summary>
    public static string NameOf(in EntitySnapshot e)
    {
        string name = e.Definition.Identity.Name;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        string material = e.Definition.Material.Material;
        if (!string.IsNullOrEmpty(material) && material is not ("Generic" or "None"))
            return $"something {material.ToLowerInvariant()}";
        return "something";
    }

    /// <summary>Whether a ray should stop at this at all: things you can walk into, and things the
    /// server says are worth naming (items, people, beacons). Region volumes, portals and your own body
    /// are looked straight through.</summary>
    public static bool Stops(in EntitySnapshot e, int ownEntityId)
        => e.Id != ownEntityId && (e.Definition.Collider.IsSolid || e.Definition.Identity.Announce);

    /// <summary>
    /// The first thing along each ray that is not the ground, the nearest of them; null when the rays
    /// meet nothing within <paramref name="range"/>, or meet the ground first — a floor you are looking
    /// at hides whatever is under it, and is not itself an answer.
    /// </summary>
    public static Sighting? Ahead(SpatialService spatial, WorldSnapshot world, ReadOnlySpan<Vector3> origins, Vector3 dir,
                                  float range, float feetY, float eyeY, int ownEntityId)
    {
        if (dir.LengthSquared() < 1e-8f) return null;
        dir = Vector3.Normalize(dir);
        Sighting? best = null;
        foreach (var origin in origins)
        {
            if (!spatial.RaycastSingle(world, origin, dir, range, e => Stops(e, ownEntityId), out var hit, out float dist)) continue;
            if (IsGroundAt(hit, origin.Y + dir.Y * dist, feetY, eyeY)) continue;
            if (best == null || dist < best.Value.Distance) best = new Sighting(hit, dist);
        }
        return best;
    }

    /// <summary>
    /// What the turn narration looks along: level, the way you face, at your knees, chest and eyes —
    /// so a car, a bench or a low wall is found as well as a wall at head height.
    /// </summary>
    public static Sighting? AheadLevel(SpatialService spatial, WorldSnapshot world, Vector3 feet, float yaw, float eyeHeight, int ownEntityId)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f));
        Span<Vector3> origins = stackalloc Vector3[]
        {
            feet + new Vector3(0, 0.5f, 0),
            feet + new Vector3(0, 1.1f, 0),
            feet + new Vector3(0, eyeHeight, 0),
        };
        return Ahead(spatial, world, origins, forward, NarrationRange, feet.Y, feet.Y + eyeHeight, ownEntityId);
    }

    /// <summary>"8 metres", "1 metre", "0.6 metres": whole metres past one, tenths under it.</summary>
    public static string SpokenDistance(float metres)
    {
        if (metres < 0.95f) return $"{MathF.Max(0.1f, metres):0.0} metres";
        int whole = (int)MathF.Round(metres);
        return whole == 1 ? "1 metre" : $"{whole} metres";
    }

    /// <summary>The line the turn narration says: the thing and how far, or which way is open.</summary>
    public static string NarrationLine(Sighting? seen, string cardinal)
        => seen is { } s ? $"{s.Name}, {SpokenDistance(s.Distance)}" : $"Open, {cardinal}";
}
