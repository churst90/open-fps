using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Common;

/// <summary>The client's world for one frame or audio tick, shared between systems.</summary>
public class WorldSnapshot
{
    public readonly Dictionary<int, EntitySnapshot> Entities = new();
    public readonly List<EntitySnapshot> DynamicEntities = new();
    public readonly List<int> AudioEntityIds = new();

    /// <summary>
    /// Every entity that declares an acoustic region, so the per-frame check for a moved region (a lift,
    /// a vehicle's interior) does not walk the map: on a city of six thousand entities that was eleven
    /// times the work of the block.
    /// </summary>
    public readonly List<int> RegionEntityIds = new();

    /// <summary>
    /// Every fixed thing that is a beacon without being solid (the ends of stair flights): in neither the
    /// static grid nor <see cref="DynamicEntities"/>, so a list of its own rather than a walk of the map.
    /// </summary>
    public readonly List<int> MarkerEntityIds = new();
    public SpatialGrid<int>? StaticGrid;
    public AcousticMap? AcousticMap;

    /// <summary>
    /// The static solids as a triangle world (docs/GEOMETRY.md stage 1), or null until built (queries then
    /// use <see cref="StaticGrid"/>). Built in the background, so it can lag a build behind: owners changed
    /// since are in <see cref="GeometryStale"/> (not counted) and, if they still exist, in
    /// <see cref="UnindexedStatics"/> with every solid it does not hold, which queries test the old way.
    /// </summary>
    public OpenFPS.Common.Geometry.TriangleWorld? Geometry;
    public IReadOnlySet<int>? GeometryStale;
    public List<int> UnindexedStatics = new();

    /// <summary>
    /// Counts changes to the static geometry under <see cref="AcousticMap"/> without a new map (tiles
    /// arriving and leaving); the acoustic worker rebuilds its Steam Audio scene when it moves
    /// (docs/WORLD_STREAMING.md).
    /// </summary>
    public long GeometryVersion;

    /// <summary>The side of the tiles the map streams in, metres; 0 for a map sent whole. The Steam Audio
    /// scene is kept in sub-scenes of this size (of 250 m on a map sent whole).</summary>
    public float TileMetres;

    /// <summary>The woods the client's tree crowns make, heard as one source each past the hand-over
    /// distance (WoodChorus); null before any crowns are held.</summary>
    public WoodChorus? Woods;

    /// <summary>
    /// When the transforms in this snapshot were sampled, seconds on <see cref="AudioClock"/>. Readers
    /// run more often than the simulation step, and with this can tell a fresh position from one a step old.
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
    /// <summary>The water in a wheel path of an asphalt road, mm (RoadWater): for a vehicle whose wheels
    /// the server does not send. Wheels that are sent carry their own (WheelState.Water).</summary>
    public float RoadWaterMm = 0.0f;
}

/// <summary>One entity in a <see cref="WorldSnapshot"/>.</summary>
public struct EntitySnapshot
{
    public int Id;
    public EntityDefinition Definition;
    public Transform Transform;
    public Vector3 Velocity;

    /// <summary>How hard this vehicle is working its tyres, 0..2 with 1 the limit, as the server worked
    /// it out (<see cref="EntityState.TyreDemand"/>).</summary>
    public float TyreDemand;

    /// <summary>Each wheel as the server last sent it (load, slip, speed, surface), front axle
    /// first; null for anything without wheels.</summary>
    public WheelState[]? Wheels;

    /// <summary>A vehicle's horn and siren switches as the server last sent them
    /// (<see cref="OpenFPS.Common.VehicleSignalBits"/>); zero for anything without.</summary>
    public byte Signals;
}
