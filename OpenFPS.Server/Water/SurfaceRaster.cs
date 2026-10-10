using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Water;

/// <summary>
/// What covers each 2 m cell of a map's ground, as the rain sees it (GroundSurface): the top of the highest
/// fixed solid thing whose footprint covers the cell's middle (a roof, a road, a drive, a lawn), else the woods
/// where a canopy volume is over it, else the ground's own material. A cell nothing covers is
/// <see cref="Uncovered"/>, for the caller to take from the ground.
/// </summary>
public static class SurfaceRaster
{
    public const byte Uncovered = 255;

    /// <summary>
    /// The surfaces of <paramref name="nx"/> by <paramref name="nz"/> cells of <paramref name="cell"/> metres from
    /// (<paramref name="x0"/>, <paramref name="z0"/>) in the world's own metres, row by row from the south-west.
    /// </summary>
    public static byte[] Of(World world, float x0, float z0, int nx, int nz, float cell)
    {
        var top = new float[nx * nz];
        var surface = new byte[nx * nz];
        Array.Fill(top, float.NegativeInfinity);
        Array.Fill(surface, Uncovered);
        var woods = new bool[nx * nz];
        var q = new QueryDescription().WithAll<Transform, ColliderComponent>();
        world.Query(in q, (Entity e, ref Transform t, ref ColliderComponent c) =>
        {
            if (c.Shape != ColliderShape.Box || c.Form != null) return;
            if (world.Has<Velocity>(e) || world.Has<PlayerComponent>(e) || world.Has<RegionComponent>(e) || world.Has<PortalComponent>(e)) return;
            if (world.Has<EntityType>(e) && world.Get<EntityType>(e) != EntityType.StaticObject) return;
            string? material = world.Has<MaterialComponent>(e) ? world.Get<MaterialComponent>(e).Material : null;
            bool canopy = !c.IsSolid && string.Equals(material, "Foliage", StringComparison.OrdinalIgnoreCase);
            if (!c.IsSolid && !canopy) return;
            var s = c.Size;
            if (s.X <= 0f || s.Z <= 0f) return;
            var r = t.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(t.Rotation);
            // The footprint: the box's horizontal axes as they lie (a tilted roof's slope still covers its eaves).
            var ax = Vector3.Transform(new Vector3(s.X * 0.5f, 0f, 0f), r);
            var az = Vector3.Transform(new Vector3(0f, 0f, s.Z * 0.5f), r);
            var ay = Vector3.Transform(new Vector3(0f, s.Y * 0.5f, 0f), r);
            float boxTop = t.Position.Y + MathF.Abs(ax.Y) + MathF.Abs(ay.Y) + MathF.Abs(az.Y);
            var u = new Vector2(ax.X, ax.Z);
            var w = new Vector2(az.X, az.Z);
            float hx = MathF.Abs(u.X) + MathF.Abs(w.X) + 1e-3f, hz = MathF.Abs(u.Y) + MathF.Abs(w.Y) + 1e-3f;
            int i0 = Math.Max(0, (int)MathF.Floor((t.Position.X - hx - x0) / cell));
            int i1 = Math.Min(nx - 1, (int)MathF.Floor((t.Position.X + hx - x0) / cell));
            int j0 = Math.Max(0, (int)MathF.Floor((t.Position.Z - hz - z0) / cell));
            int j1 = Math.Min(nz - 1, (int)MathF.Floor((t.Position.Z + hz - z0) / cell));
            if (i0 > i1 || j0 > j1) return;
            float uu = u.LengthSquared(), ww = w.LengthSquared();
            if (uu < 1e-9f || ww < 1e-9f) return;
            byte made = (byte)GroundHydrology.OfMaterial(material, road: c.IsSolid && s.Y <= 0.6f);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var p = new Vector2(x0 + (i + 0.5f) * cell - t.Position.X, z0 + (j + 0.5f) * cell - t.Position.Z);
                    // Inside the parallelogram the two half-axes span (a rectangle unless the box is tilted on both).
                    float a = Vector2.Dot(p, u) / uu, b = Vector2.Dot(p, w) / ww;
                    if (MathF.Abs(a) > 1f || MathF.Abs(b) > 1f) continue;
                    int k = j * nx + i;
                    if (canopy) { woods[k] = true; continue; }
                    if (boxTop <= top[k]) continue;
                    top[k] = boxTop;
                    surface[k] = made;
                }
        });
        for (int k = 0; k < surface.Length; k++)
            if (surface[k] == Uncovered && woods[k]) surface[k] = (byte)GroundSurface.Woods;
        return surface;
    }

    /// <summary>A cell of ground nothing covers: its own material (the land cover on the world's tiles; dirt on
    /// a place's map, which is open ground).</summary>
    public static byte OfGround(string? material) => (byte)GroundHydrology.OfMaterial(material);

    /// <summary>ESA WorldCover's class to a surface (LandCoverMaterials gives its material for sound): trees and
    /// mangroves the woods; grassland, shrub, crops, bare ground and moss open ground (TR-55's "pasture, fair"
    /// stands for them all); built-up sealed; open water and herbaceous wetland water.</summary>
    public static byte OfLandCover(byte cls) => cls switch
    {
        10 => (byte)GroundSurface.Woods,
        50 => (byte)GroundSurface.Impervious,
        80 or 90 or 95 => (byte)GroundSurface.Water,
        _ => (byte)GroundSurface.Open,
    };
}
