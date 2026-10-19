using System.Numerics;
using OpenFPS.Geometry;

namespace OpenFPS.Acoustics;

/// <summary>
/// The world as the sound reads it (docs/SOUND_LIBRARY_BOUNDARY.md 7.3): the solids as triangles, the rooms and
/// openings, the air, and every thing with a box. The host fills it (open-fps: <c>WorldSnapshot</c>, from its
/// entities); the library reads nothing else of the host's world.
/// </summary>
/// <remarks>
/// One view answers one request, and it never changes once handed out: the acoustic worker runs on its own
/// thread and holds the view it was given while the game builds the next. A view that followed the live world
/// would answer one question half from before a door moved and half from after.
/// </remarks>
public interface IAcousticWorld
{
    /// <summary>The static solids as triangles, door leaves where they stand; null until built (every solid is
    /// then a box).</summary>
    TriangleWorld? Geometry { get; }

    /// <summary>Owners whose place in <see cref="Geometry"/> is being rebuilt, which it must not count; null when none.</summary>
    IReadOnlySet<int>? GeometryStale { get; }

    /// <summary>Counts changes to the static solids under the same <see cref="Rooms"/> (tiles arriving and leaving).</summary>
    long GeometryVersion { get; }

    /// <summary>The side of the tiles the map streams in, metres; 0 for a map sent whole.</summary>
    float TileMetres { get; }

    /// <summary>The rooms and openings; null before a map has any.</summary>
    AcousticMap? Rooms { get; }

    /// <summary>The air the paths cross.</summary>
    AirState Air { get; }

    /// <summary>How many things the world holds: a change says one arrived or left.</summary>
    int BodyCount { get; }

    /// <summary>A thing by its id.</summary>
    bool TryGetBody(int id, out AcousticBody body);

    /// <summary>Every thing, in the host's order. Cleared first.</summary>
    void AllBodies(List<AcousticBody> into);

    /// <summary>How many things move (or are not fixed objects): what the static solids leave out.</summary>
    int MovingCount { get; }

    /// <summary>The <paramref name="index"/>th moving thing, in the host's order.</summary>
    AcousticBody MovingAt(int index);

    /// <summary>
    /// What a ray round <paramref name="centre"/> could meet, each once: the fixed solids filed within
    /// <paramref name="radius"/>, then (unless <paramref name="staticOnly"/>) everything that moves. False when
    /// the host has no index of its fixed solids yet: then it is everything it holds. Cleared first.
    /// </summary>
    bool Near(Vector3 centre, float radius, bool staticOnly, List<AcousticBody> into);

    /// <summary>What <see cref="Geometry"/> does not answer for: the fixed solids it does not hold, then
    /// (unless <paramref name="staticOnly"/>) everything that moves. Cleared first.</summary>
    void Unindexed(bool staticOnly, List<AcousticBody> into);

    /// <summary>Every door leaf (<see cref="AcousticBody.DoorLeaf"/>), in the host's order, with the doorway
    /// it shuts. Cleared first.</summary>
    void DoorLeaves(List<DoorLeaf> into);

    /// <summary>Every marker: a fixed thing nothing meets that a look near the player wants (a stair end, a
    /// named place), in the host's order. Cleared first.</summary>
    void Markers(List<AcousticBody> into);

    /// <summary>The tiles of ground held, as solids, by owner: what the acoustic scene lays its ground from.</summary>
    List<SolidSpec> TerrainSolids();
}

/// <summary>The air as the host knows it: °C, relative humidity 0..1, millibars, and how much of the standard
/// absorption it takes (1 is ISO 9613-1's).</summary>
public readonly record struct AirState(float Celsius, float Humidity, float PressureMillibars, float AbsorptionMultiplier)
{
    /// <summary>Twenty degrees, half humidity, a standard atmosphere.</summary>
    public static readonly AirState Standard = new(20f, 0.5f, 1013.25f, 1f);
}

/// <summary>What a body is, as its host's types name them (open-fps: EntityType; None for an entity with no
/// definition). The aids that name people read it.</summary>
public enum BodyKind : byte { None = 0, Player, Npc, Beacon, StaticObject, Item, Projectile, Trigger }

/// <summary>
/// A thing in the world with a box, as the sound meets it: a wall, a door leaf, a tree's leaves, a car. The
/// host fills it from what it holds (open-fps: an entity's collider, material, acoustics and emitter).
/// </summary>
public readonly struct AcousticBody
{
    /// <summary>The host's id for it (open-fps: the entity id).</summary>
    public int Id { get; init; }
    public Vector3 Position { get; init; }
    public Quaternion Rotation { get; init; }
    public Vector3 Velocity { get; init; }

    /// <summary>The collider's shape and full size; <see cref="Solid"/> false for what nothing meets (leaves, a
    /// marker, a room's box).</summary>
    public ColliderShape Shape { get; init; }
    public Vector3 Size { get; init; }
    public bool Solid { get; init; }
    /// <summary>The shape built from triangles, where the box is only its bounds; null for a box.</summary>
    public ShapeSpec? Form { get; init; }

    /// <summary>A fixed object of the map (not a person, a vehicle or an item), whether or not it moves.</summary>
    public bool Fixed { get; init; }
    /// <summary>It moves (a lift, a carried thing): never in a scene built once.</summary>
    public bool Moves { get; init; }
    /// <summary>A door leaf: a fixed solid that is also a doorway between two places. It hangs in its frame and
    /// is never a layer of the wall round it.</summary>
    public bool DoorLeaf { get; init; }

    /// <summary>The material's name as the host gave it (empty, or null in a default: the readers say what that is).</summary>
    public string Material { get; init; }
    /// <summary>An absorption the author gave it, 0 for the material's own.</summary>
    public float Absorption { get; init; }
    /// <summary>Walls of <see cref="ShellThickness"/> round an empty inside.</summary>
    public bool Hollow { get; init; }
    public float ShellThickness { get; init; }
    /// <summary>How the wall is built (two leaves over a cavity, a multi-ply assembly), solid by default.</summary>
    public WallBuild Build { get; init; }

    /// <summary>A room's box, for a thing that declares a room and has no collider of its own size.</summary>
    public Vector3 RoomSize { get; init; }

    /// <summary>What it is voiced as (a vehicle's preset key, a tree's crown), or null or empty: none.</summary>
    public string? SoundId { get; init; }
    /// <summary>A vehicle's side windows as the host last sent them, 0 shut to 1 down.</summary>
    public float WindowsOpen { get; init; }

    /// <summary>What it is called (open-fps: <c>Identity.Name</c>), or null or empty: unnamed.</summary>
    public string? Name { get; init; }
    /// <summary>The prefab it was placed from (open-fps: <c>Identity.PrefabId</c>), or null or empty: built by
    /// hand.</summary>
    public string? Prefab { get; init; }
    /// <summary>Its beacon category (open-fps: <c>Identity.BeaconCategory</c>, "door", "item", "stairs"...),
    /// or null or empty: it is not one.</summary>
    public string? Beacon { get; init; }

    /// <summary>What it is (open-fps: the entity's type), for naming somebody as somebody.</summary>
    public BodyKind Kind { get; init; }
    /// <summary>A person with a name of their own (open-fps: <c>Identity.Named</c>).</summary>
    public bool Named { get; init; }
    /// <summary>Whether a look should name it even though it is not solid (open-fps:
    /// <c>Identity.Announce</c>: items, people and beacons as the prefab sets it).</summary>
    public bool Announce { get; init; }
}

/// <summary>A door leaf where it stands now, and the doorway it shuts: its pose when shut, its size, the two
/// places it joins (both may be the outside), and the gaps round it shut, metres, at its jambs and head
/// (<paramref name="GapMetres"/>) and under it (<paramref name="UndercutMetres"/>); zero takes the default for where it
/// hangs (<see cref="DoorGaps.For"/>).</summary>
public readonly record struct DoorLeaf(int Id, Vector3 Position, Quaternion Rotation, Vector3 Size,
                                      Vector3 DoorwayCentre, Quaternion DoorwayRotation, int RoomA, int RoomB,
                                      float GapMetres = 0f, float UndercutMetres = 0f);
