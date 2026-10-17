using System.Numerics;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Common;

/// <summary>The client's world for one frame or audio tick, shared between systems. Never changed once built,
/// which makes it the sound's world input as it is (<see cref="IAcousticWorld"/>): one immutable view per request.</summary>
public class WorldSnapshot : IAcousticWorld
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

    /// <summary>The tiles of ground held, as solids (docs/GEOMETRY.md 2.3), by owner: what the acoustic
    /// scene lays its ground from. Read from the definitions, so it never lags them.</summary>
    public List<OpenFPS.Common.Geometry.SolidSpec> TerrainSolids()
    {
        var list = new List<OpenFPS.Common.Geometry.SolidSpec>();
        foreach (var snap in Entities.Values)
        {
            var def = snap.Definition;
            if (def == null || def.Collider.Shape != ColliderShape.Terrain || def.Moves) continue;
            var spec = EntityGeometry.SpecOf(def, snap.Transform, GeometryRole.Static);
            if (spec.Terrain != null) list.Add(spec);
        }
        list.Sort(static (a, b) => a.Owner.CompareTo(b.Owner));
        return list;
    }

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
    /// When the transforms in this snapshot were sampled, seconds on <see cref="IAudioClock"/>. Readers
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

    /// <summary>What is coming down: <see cref="Precipitation"/>, or rain at <see cref="RainRateMmPerHour"/> from a
    /// server that sends only the rate.</summary>
    public Precipitation Falling => Precipitation.Falling ? Precipitation
                                  : new Precipitation(PrecipitationKind.Rain, RainRateMmPerHour);

    // ── The world input (docs/SOUND_LIBRARY_BOUNDARY.md 7.3) ─────────────────────────────────────
    //
    // Each answer is read off the snapshot as it is asked, so the view is exactly as immutable as the
    // snapshot: nothing here is cached against a later change to it.

    OpenFPS.Common.Geometry.TriangleWorld? IAcousticWorld.Geometry => Geometry;
    IReadOnlySet<int>? IAcousticWorld.GeometryStale => GeometryStale;
    long IAcousticWorld.GeometryVersion => GeometryVersion;
    float IAcousticWorld.TileMetres => TileMetres;
    AcousticMap? IAcousticWorld.Rooms => AcousticMap;
    AirState IAcousticWorld.Air => new(Temperature, Humidity, AirPressure, AirAbsorptionMultiplier);
    int IAcousticWorld.BodyCount => Entities.Count;

    bool IAcousticWorld.TryGetBody(int id, out AcousticBody body)
    {
        if (Entities.TryGetValue(id, out var snap)) { body = EntityAcoustics.BodyOf(snap); return true; }
        body = default;
        return false;
    }

    void IAcousticWorld.AllBodies(List<AcousticBody> into)
    {
        into.Clear();
        foreach (var snap in Entities.Values) into.Add(EntityAcoustics.BodyOf(snap));
    }

    int IAcousticWorld.MovingCount => DynamicEntities.Count;
    AcousticBody IAcousticWorld.MovingAt(int index) => EntityAcoustics.BodyOf(DynamicEntities[index]);

    [ThreadStatic] private static List<EntitySnapshot>? _near;

    bool IAcousticWorld.Near(Vector3 centre, float radius, bool staticOnly, List<AcousticBody> into)
    {
        var near = _near ??= new List<EntitySnapshot>(64);
        bool indexed = EntitiesNear(centre, radius, staticOnly, near);
        into.Clear();
        foreach (var snap in near) into.Add(EntityAcoustics.BodyOf(snap));
        return indexed;
    }

    void IAcousticWorld.Unindexed(bool staticOnly, List<AcousticBody> into)
    {
        into.Clear();
        foreach (int id in UnindexedStatics)
            if (Entities.TryGetValue(id, out var s)) into.Add(EntityAcoustics.BodyOf(s));
        if (!staticOnly) foreach (var s in DynamicEntities) into.Add(EntityAcoustics.BodyOf(s));
    }

    void IAcousticWorld.DoorLeaves(List<DoorLeaf> into)
    {
        into.Clear();
        foreach (var snap in Entities.Values)
            if (EntityAcoustics.IsDoorLeaf(snap.Definition)) into.Add(EntityAcoustics.LeafOf(snap));
    }

    [ThreadStatic] private static List<int>? _nearIds;
    [ThreadStatic] private static HashSet<int>? _nearSeen;

    /// <summary>
    /// The entities a query at <paramref name="centre"/> could possibly hit, each once (a wall spanning cells is
    /// filed in each of them): the fixed solids the static grid files round it, then everything that moves unless
    /// <paramref name="staticOnly"/>. With no grid yet (until the first rebuild, seconds after a map arrives) only
    /// the whole world is the honest answer, and false says so. Cleared first.
    /// </summary>
    public bool EntitiesNear(Vector3 centre, float radius, bool staticOnly, List<EntitySnapshot> into)
    {
        into.Clear();
        if (StaticGrid != null)
        {
            var ids = _nearIds ??= new List<int>(64);
            var seen = _nearSeen ??= new HashSet<int>();
            StaticGrid.CollectInRadius(centre, radius, ids, seen);

            for (int i = 0; i < ids.Count; i++)
                if (Entities.TryGetValue(ids[i], out var snap)) into.Add(snap);

            // Dynamic entities are not in the static grid, so they are always in play.
            if (!staticOnly)
                for (int i = 0; i < DynamicEntities.Count; i++)
                    if (seen.Add(DynamicEntities[i].Id)) into.Add(DynamicEntities[i]);

            // An empty answer from the grid is an answer (the middle of a road). Falling through to every
            // entity in the world froze a city of six thousand boxes, outdoors, where a listener spends
            // their time.
            return true;
        }

        foreach (var snap in Entities.Values) into.Add(snap);
        return false;
    }
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

    /// <summary>How hard a train or an aeroplane is working as the server last sent it, 0..1
    /// (EntityState.Lever); zero for anything else.</summary>
    public float Lever;

    /// <summary>Whether a body on foot stands on something, as the server's step left it
    /// (EntityState.Grounding); Unknown for anything no step stands.</summary>
    public Grounding Grounding;

    /// <summary>An entity is a body to the sound (<see cref="EntityAcoustics.BodyOf"/>).</summary>
    public static implicit operator AcousticBody(EntitySnapshot snap) => EntityAcoustics.BodyOf(snap);
}
