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
    public SpatialGrid<int>? StaticGrid;
    public AcousticMap? AcousticMap;

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
}
