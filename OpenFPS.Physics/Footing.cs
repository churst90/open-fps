using System.Numerics;
using OpenFPS.Common.Geometry;
using static OpenFPS.Common.BodyConstants;

namespace OpenFPS.Common;

/// <summary>
/// Where the floor is under a body on foot and where its foot goes down, asked of a triangle world
/// (docs/GEOMETRY.md 3.3, 3.8). The server and a client ask the same question of their own world through
/// <c>PhysicsUtils</c>, each with a filter that says which solids are theirs to count.
/// </summary>
public static class Footing
{
    /// <summary>How near the body's height a floor is to be the one it stands on, metres.</summary>
    public const float FootOnFloorTolerance = 0.002f;

    /// <summary>
    /// The floor under a body at <paramref name="pos"/>: five points across its footprint, the highest top no
    /// higher than a step above the feet. Below -1000 where there is none, and <paramref name="solid"/> says
    /// which solid it is.
    /// </summary>
    public static float Ground<F>(TriangleWorld geo, ref F filter, Vector3 pos, out SolidRef solid)
        where F : IGeometryFilter
        => geo.Ground(pos, PlayerRadius, StepHeight, GeometryLayers.Ground, ref filter, out solid, out _);

    /// <summary>
    /// Where a foot goes down, and on what (docs/GEOMETRY.md 3.8). The body stands at the highest floor
    /// under its footprint, so on a flight a foot under its middle was in the air over the lower tread.
    /// The foot goes on the floor the body stands on: under the foot, or a body's radius ahead along
    /// <paramref name="way"/>. Nothing at the body's height (a vehicle's floor) leaves the foot where it
    /// was and <paramref name="material"/> null.
    /// </summary>
    public static Vector3 FootOnFloor<F>(TriangleWorld geo, ref F filter, Vector3 foot, Vector3 feet, Vector3 way, out string? material)
        where F : IGeometryFilter
    {
        material = null;
        float top = feet.Y + StepHeight;
        var ahead = new Vector3(way.X, 0f, way.Z);
        ahead = ahead.LengthSquared() > 1e-8f ? Vector3.Normalize(ahead) * PlayerRadius : Vector3.Zero;
        float under = geo.FloorAt(foot.X, foot.Z, top, GeometryLayers.Ground, ref filter, out var underHit);
        // On the floor the body stands on, under the foot...
        if (under > -1000f && MathF.Abs(under - feet.Y) <= FootOnFloorTolerance) return On(geo, foot, under, underHit, out material);
        // ...or a stride ahead, the tread a body climbing a flight is stepping up onto...
        if (ahead != Vector3.Zero)
        {
            var at = foot + ahead;
            float y = geo.FloorAt(at.X, at.Z, top, GeometryLayers.Ground, ref filter, out var hit);
            if (y > -1000f && MathF.Abs(y - feet.Y) <= FootOnFloorTolerance) return On(geo, at, y, hit, out material);
        }
        // ...or whatever is under the foot within a step: going down, the body is held on the tread it is
        // leaving while the foot is already on the one below.
        if (under > -1000f && MathF.Abs(under - feet.Y) <= StepHeight + 0.01f) return On(geo, foot, under, underHit, out material);
        return foot;

        static Vector3 On(TriangleWorld geo, Vector3 at, float y, in GeometryHit hit, out string? material)
        {
            var raw = geo.SurfaceOf(hit).Material;
            material = string.IsNullOrEmpty(raw) ? "Generic" : raw;
            return new Vector3(at.X, y, at.Z);
        }
    }
}
