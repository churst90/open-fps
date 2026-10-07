using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Whether a map's drive route fits, checked at load in terms its author can act on. A hundred and
/// twenty metres of the speedway's front straight ran through the grandstand deck, and the cars on it,
/// rightly rendered inside a solid box, were heard as "the cars vanish and I only hear their
/// reflections".
/// </summary>
public static class TrackClearance
{
    /// <summary>Half the width of the widest thing expected to use the route, metres. A road vehicle
    /// is about 1.8 m across, so this is the margin a track must keep clear beyond its own edge.</summary>
    public const float DefaultVehicleHalfWidth = 0.9f;

    /// <summary>How often along the route to look, metres. Fine enough to catch a single wall segment,
    /// coarse enough that a two-kilometre lap is a few hundred tests.</summary>
    public const float SampleSpacing = 4.0f;

    /// <summary>The band of heights a vehicle's body occupies, metres: at the surface the test meets
    /// the road itself, and at one height it misses a gantry or a kerb.</summary>
    public const float BodyBottom = 0.4f;
    public const float BodyTop = 1.6f;

    /// <summary>One place the route is not clear: where, how far off the centreline, and what it hit.</summary>
    public readonly record struct Obstruction(Vector3 Point, float LateralOffset, Vector3 ObstacleCentre, Vector3 ObstacleSize);

    /// <summary>A solid box in the world, as the check sees it.</summary>
    public readonly record struct Solid(Vector3 Centre, Vector3 Size, Quaternion Rotation);

    /// <summary>Every place on the closed route a vehicle of the given width could not pass, across the
    /// lane band and not only the centreline: a car on the outside line is a car's width nearer the side.</summary>
    public static List<Obstruction> Check(IReadOnlyList<Vector3> waypoints, float widthMetres,
                                          IReadOnlyList<Solid> solids,
                                          float vehicleHalfWidth = DefaultVehicleHalfWidth)
    {
        var found = new List<Obstruction>();
        if (waypoints == null || waypoints.Count < 3 || solids == null || solids.Count == 0) return found;

        // The grown solids in a tree over their bounds (docs/GEOMETRY.md stage 1): each point asks the few
        // whose bounds hold it, in list order.
        var bmin = new Vector3[solids.Count]; var bmax = new Vector3[solids.Count];
        for (int b = 0; b < solids.Count; b++)
        {
            var solid = solids[b];
            var grownSize = new Vector3(solid.Size.X + vehicleHalfWidth * 2f, solid.Size.Y, solid.Size.Z + vehicleHalfWidth * 2f);
            var r = solid.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : solid.Rotation;
            var h = grownSize * 0.5f;
            var e = Vector3.Abs(Vector3.Transform(new Vector3(h.X, 0, 0), r)) + Vector3.Abs(Vector3.Transform(new Vector3(0, h.Y, 0), r))
                    + Vector3.Abs(Vector3.Transform(new Vector3(0, 0, h.Z), r));
            // A little over: the point test reads the turn as it is, and the tree must never leave one out.
            e = e * 1.001f + new Vector3(0.01f);
            bmin[b] = solid.Centre - e; bmax[b] = solid.Centre + e;
        }
        var nodes = Geometry.BvhBuilder.Build(bmin, bmax, solids.Count, 4, out var order);
        var candidates = new List<int>();
        Span<int> stack = stackalloc int[Geometry.BvhBuilder.MaxDepth + 2];

        // Widest a vehicle may sit from the centreline and still be on the road.
        float halfLane = MathF.Max(0f, widthMetres * 0.5f - vehicleHalfWidth);

        int n = waypoints.Count;
        for (int i = 0; i < n; i++)
        {
            Vector3 here = waypoints[i];
            Vector3 next = waypoints[(i + 1) % n];
            Vector3 along = next - here;
            float span = along.Length();
            if (span < 1e-3f) continue;
            along /= span;

            // Sideways, in the ground plane: the direction a lane offset moves a vehicle.
            var lateral = new Vector3(-along.Z, 0f, along.X);
            if (lateral.LengthSquared() < 1e-6f) continue;
            lateral = Vector3.Normalize(lateral);

            int steps = Math.Max(1, (int)MathF.Ceiling(span / SampleSpacing));
            for (int s = 0; s < steps; s++)
            {
                Vector3 centre = here + along * (span * s / steps);
                // The centreline and both edges of the band: enough to catch a building.
                for (int lane = -1; lane <= 1; lane++)
                {
                    float offset = lane * halfLane;
                    Vector3 side = centre + lateral * offset;
                    Vector3 lo = side + new Vector3(0f, BodyBottom, 0f), hi = side + new Vector3(0f, BodyTop + 1e-3f, 0f);
                    candidates.Clear();
                    int sp = 0;
                    stack[sp++] = 0;
                    while (sp > 0)
                    {
                        ref readonly var node = ref nodes[stack[--sp]];
                        if (node.Max.X < lo.X || node.Min.X > lo.X || node.Max.Z < lo.Z || node.Min.Z > lo.Z || node.Max.Y < lo.Y || node.Min.Y > hi.Y) continue;
                        if (node.Count > 0) { for (int k = node.LeftFirst; k < node.LeftFirst + node.Count; k++) candidates.Add(order[k]); continue; }
                        stack[sp++] = node.LeftFirst + 1; stack[sp++] = node.LeftFirst;
                    }
                    candidates.Sort();
                    bool hit = false;
                    foreach (int b in candidates)
                    {
                        if (hit) break;
                        var solid = solids[b];
                        // Grown sideways by the vehicle's width, so clipping fails as well as inside.
                        // Never vertically: the road is a solid box beneath, and every metre would fail.
                        var grown = new Vector3(solid.Size.X + vehicleHalfWidth * 2f,
                                                solid.Size.Y,
                                                solid.Size.Z + vehicleHalfWidth * 2f);
                        for (float y = BodyBottom; y <= BodyTop + 1e-3f; y += 0.4f)
                        {
                            Vector3 p = side + new Vector3(0f, y, 0f);
                            if (!GeometryUtils.IsPointInOBB(p, solid.Centre, grown, solid.Rotation)) continue;
                            found.Add(new Obstruction(p, offset, solid.Centre, solid.Size));
                            hit = true;
                            break;
                        }
                    }
                }
            }
        }
        return found;
    }
}
