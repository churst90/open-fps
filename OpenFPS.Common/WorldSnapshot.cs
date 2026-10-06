using System.Numerics;
using System.Collections.Generic;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Common;

/// <summary>
/// A flattened, view of the world for a single simulation frame or audio tick.
/// Used to share state between systems without exposing the full ECS world.
/// </summary>
public class WorldSnapshot
{
    public readonly Dictionary<int, EntitySnapshot> Entities = new();
    public readonly List<EntitySnapshot> DynamicEntities = new();
    public readonly List<int> AudioEntityIds = new();

    /// <summary>
    /// Every entity that declares an acoustic REGION, so that looking for one that has moved does not
    /// mean looking at everything.
    ///
    /// The same idea as <see cref="AudioEntityIds"/> and for the same reason. The client checks each
    /// frame whether a region has moved — a lift, a vehicle's interior, anything carrying a room
    /// around with it — and it did that by walking every entity in the world. That is a loop the
    /// size of the MAP running at the frame rate: fine on a block of five hundred boxes, and on a
    /// city of six thousand it is eleven times the work to find the same handful of regions.
    /// </summary>
    public readonly List<int> RegionEntityIds = new();

    /// <summary>
    /// Every fixed thing that is a beacon without being solid: the ends of stair flights.
    ///
    /// The static grid holds only what can be walked into, and a fixed thing is not in
    /// <see cref="DynamicEntities"/>, so a marker standing on a landing was in neither and nothing
    /// that looks for beacons near you could find it. A list of its own, for the same reason as
    /// <see cref="RegionEntityIds"/>: the alternative is walking the whole map every frame.
    /// </summary>
    public readonly List<int> MarkerEntityIds = new();
    public SpatialGrid<int>? StaticGrid;
    public AcousticMap? AcousticMap;

    /// <summary>
    /// The static solid boxes as a triangle world (docs/GEOMETRY.md stage 1), or null until one is built
    /// (every query then answers from <see cref="StaticGrid"/> as before). Built in the background, so it
    /// can lag the definitions by a build: the owners whose solid changed since it was started are in
    /// <see cref="GeometryStale"/> (what it holds of them is not counted) and, where they still exist, in
    /// <see cref="UnindexedStatics"/> with every static solid it does not hold (a shape that is not a box),
    /// which the queries test the old way.
    /// </summary>
    public OpenFPS.Common.Geometry.TriangleWorld? Geometry;
    public IReadOnlySet<int>? GeometryStale;
    public List<int> UnindexedStatics = new();

    /// <summary>
    /// Counts the times the static geometry under <see cref="AcousticMap"/> changed without the map
    /// itself being replaced: tiles of a streamed map arriving and leaving. The acoustic worker rebuilds
    /// its Steam Audio scene in the background when it moves (docs/WORLD_STREAMING.md).
    /// </summary>
    public long GeometryVersion;

    /// <summary>The side of the tiles the map streams in, metres; 0 for a map sent whole. The Steam Audio
    /// scene is kept in sub-scenes of this size (of 250 m on a map sent whole).</summary>
    public float TileMetres;

    /// <summary>
    /// When the transforms in this snapshot were last TRUE, seconds on <see cref="AudioClock"/>.
    ///
    /// Remote entities move on the interpolation clock, which ticks once per simulation step; anything
    /// that reads a snapshot reads it more often than that and would otherwise have no way to tell a
    /// position sampled this instant from one sampled a whole step ago. Carried here rather than asked
    /// for separately so that a consumer holding a snapshot is holding its age with it.
    /// </summary>
    public double PositionsSampledAt;

    // Atmospheric State
    public float Temperature = 20.0f;
    public float Humidity = 0.5f;
    public float AirPressure = 1013.25f;
    public float AirAbsorptionMultiplier = 1.0f;
    public float ShelterFactor = 0.0f;
    public Vector3 WindVelocity = Vector3.Zero;
    public float WindGustiness = 0.0f;
    public float PrecipitationIntensity = 0.0f;
    /// <summary>How hard it is raining, mm/h; zero when dry or when what falls is snow (Rainfall).</summary>
    public float RainRateMmPerHour = 0.0f;
    /// <summary>What is falling and how big (Precipitation): the rate above is its water equivalent.</summary>
    public Precipitation Precipitation = OpenFPS.Common.Precipitation.None;
}

/// <summary>
/// A single-entity view within a WorldSnapshot.
/// </summary>
public struct EntitySnapshot
{
    public int Id;
    public EntityDefinition Definition;
    public Transform Transform;
    public Vector3 Velocity;

    /// <summary>How hard this vehicle is working its tyres, 0..2 with 1 the limit — as the SERVER
    /// worked it out, because a listener cannot tell a banked corner from a flat one.</summary>
    public float TyreDemand;

    /// <summary>Each wheel as the server last sent it (load, slip, speed, surface), front axle
    /// first; null for anything without wheels.</summary>
    public WheelState[]? Wheels;
}
