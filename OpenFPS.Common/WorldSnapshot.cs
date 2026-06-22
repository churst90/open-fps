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
    public SpatialGrid<int>? StaticGrid;
    public AcousticMap? AcousticMap;

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
}
