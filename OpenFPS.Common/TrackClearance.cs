using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// Does the route the map says things drive on actually fit?
///
/// The server already validates prefabs at load, and it validated everything about the speedway
/// except the one thing that made it sound broken: a hundred and twenty metres of the front straight
/// ran THROUGH the grandstand deck. Every car on that stretch was inside a solid box, the acoustic
/// model — quite correctly — rendered a source inside a building as a source inside a building, and
/// the report that came back was "the cars vanish and I only hear their reflections".
///
/// Nothing in the audio engine could have been right about that, and no amount of tuning it would
/// have helped. It is a map fault, and a map fault should be caught when the map is loaded, in terms
/// the person who built it can act on, rather than heard four minutes into a session as a mystery.
///
/// Deliberately general: a track is a list of points with a width, an obstacle is a solid box, and a
/// vehicle is something with a width. Nothing here knows about ovals, grandstands or this map.
/// </summary>
public static class TrackClearance
{
    /// <summary>Half the width of the widest thing expected to use the route, metres. A road vehicle
    /// is about 1.8 m across, so this is the margin a track must keep clear beyond its own edge.</summary>
    public const float DefaultVehicleHalfWidth = 0.9f;

    /// <summary>How often along the route to look, metres. Fine enough to catch a single wall segment,
    /// coarse enough that a two-kilometre lap is a few hundred tests.</summary>
    public const float SampleSpacing = 4.0f;

    /// <summary>The band of heights a vehicle's body occupies above the surface, metres. The check is
    /// made across it, not at a point: testing at the surface would collide with the road itself, and
    /// testing at one height would miss a gantry at head height or a kerb at axle height.</summary>
    public const float BodyBottom = 0.4f;
    public const float BodyTop = 1.6f;

    /// <summary>One place the route is not clear: where, how far off the centreline, and what it hit.</summary>
    public readonly record struct Obstruction(Vector3 Point, float LateralOffset, Vector3 ObstacleCentre, Vector3 ObstacleSize);

    /// <summary>A solid box in the world, as the check sees it.</summary>
    public readonly record struct Solid(Vector3 Centre, Vector3 Size, Quaternion Rotation);

    /// <summary>
    /// Walks the closed route and reports every place a vehicle of the given width could not pass.
    ///
    /// The lane band is tested, not just the centreline, because a route is a SURFACE: a car on the
    /// outside line is a car's width nearer whatever is beside the track, and "the middle is clear" is
    /// not the property that matters to it.
    /// </summary>
    public static List<Obstruction> Check(IReadOnlyList<Vector3> waypoints, float widthMetres,
                                          IReadOnlyList<Solid> solids,
                                          float vehicleHalfWidth = DefaultVehicleHalfWidth)
    {
        var found = new List<Obstruction>();
        if (waypoints == null || waypoints.Count < 3 || solids == null || solids.Count == 0) return found;

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
                // The centreline and both edges of the usable band. Three is enough to catch anything
                // wide enough to be a building and cheap enough to run on every load.
                for (int lane = -1; lane <= 1; lane++)
                {
                    float offset = lane * halfLane;
                    Vector3 side = centre + lateral * offset;
                    bool hit = false;
                    for (int b = 0; b < solids.Count && !hit; b++)
                    {
                        var solid = solids[b];
                        // Grown SIDEWAYS only, by the vehicle's own width, so "close enough to clip
                        // it" fails as well as "inside it" — a route that passes a wall by ten
                        // centimetres is not a route, whatever a point test says. Never grown
                        // vertically: the road a vehicle drives on is a solid box directly beneath it,
                        // and a vertical margin would report every metre of every track as obstructed.
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
