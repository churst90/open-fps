using System.Numerics;
using OpenFPS.Geometry;
using OpenFPS.Physics;
using OpenFPS.Sound;

namespace OpenFPS.Server.Systems;

/// <summary>
/// A seat on a vehicle, as the people's laws read it: what it is called, where the occupant's feet go in
/// the vehicle's frame, which way it faces and whether it drives.
/// </summary>
public readonly record struct SeatBody(string Name, Vector3 LocalPosition, float LocalYaw, bool Controls);

/// <summary>
/// One person on foot: who they are, where and how they move, and whether a vehicle carries them.
/// <see cref="RootId"/> and <see cref="SeatIndex"/> are -1 when nobody is carrying them.
/// </summary>
public readonly record struct PersonBody(
    int Id, string Voice, string Pair, string Character, Vector3 Position, Quaternion Facing,
    Vector3 Velocity, float Speed, bool Dead, bool Frozen, bool OnFoot, int RootId, int SeatIndex);

/// <summary>A player, as a hearer: where they are, and whether a seat has them.</summary>
public readonly record struct PlayerBody(int Id, Vector3 Position, bool InVehicle);

/// <summary>
/// A vehicle as the people's laws read it: where it is and how it moves, whether its doors are open at a
/// stop, and its seats.
/// </summary>
public readonly record struct VehicleView(
    int Id, Vector3 Position, Quaternion Rotation, Vector3 Velocity, float Speed, bool ServingStop,
    string Type, SeatBody[] Seats);

/// <summary>A doorway: the leaf's shut pose, half its width across, and whether it slides.</summary>
public readonly record struct DoorwayView(Vector3 Centre, Quaternion Rotation, float HalfWidth, bool Slides);

/// <summary>
/// A door as the laws read it: its doorway and name, which side a key is needed on, the leaf's size, the
/// kind of leaf, how far open it is and where it is going, what closes or senses it, the regions either
/// side, whose hand is moving it, and whether it has died.
/// </summary>
public readonly record struct DoorBody(
    int Id, DoorwayView Doorway, string Name, float KeyedSide, Vector3 LeafSize, DoorKind Kind,
    float Openness, float Target, bool Powered, float SensorMetres, float CloseAfterSeconds,
    int RegionAId, int RegionBId, int Hand, bool Dead);

/// <summary>A region: whether it is indoors, where its box is, its friendly name and the air it holds.</summary>
public readonly record struct RegionBody(int Id, bool Indoor, Vector3 Centre, Vector3 Size, string Name, float Air);

/// <summary>A portal, as the door rule's region graph reads it: the two regions it joins.</summary>
public readonly record struct PortalBody(int Id, int RegionAId, int RegionBId);

/// <summary>
/// A named box a law may read: a pavement, a shelter part, a wall or a door. <see cref="Moves"/> is whether
/// a velocity carries it; <see cref="IsDoor"/> whether it is a door leaf.
/// </summary>
public readonly record struct NamedBox(
    int Id, string Name, Vector3 Centre, Quaternion Rotation, Vector3 Size, ColliderShape Shape,
    bool Solid, bool Moves, bool IsDoor);

/// <summary>
/// The people, doors and places the people's laws read, and the physics they stand in
/// (docs/ENGINE_PACKAGES.md, stage 10). The server fills one over a map's world and grid; a test may fill
/// one over a world alone, for the people/door/place half. Lists are cleared and filled in entity-id order,
/// so every host gets the same order; the lookups answer about one entity by id. Nothing here writes.
/// </summary>
public interface IPeopleWorld : IPhysicsWorld
{
    void People(List<PersonBody> into);
    void Players(List<PlayerBody> into);
    void Vehicles(List<VehicleView> into);
    void Doors(List<DoorBody> into);
    void Regions(List<RegionBody> into);
    void Portals(List<PortalBody> into);
    void NamedBoxes(List<NamedBox> into);

    bool TryPerson(int id, out PersonBody person);
    bool TryDoor(int id, out DoorBody door);

    /// <summary>Where the doorway is and how wide it is; false for no such door.</summary>
    bool Doorway(int doorId, out DoorwayView doorway);

    /// <summary>Whether somebody standing at <paramref name="at"/> is in this door's doorway.</summary>
    bool InDoorway(int doorId, Vector3 at);

    /// <summary>Whether the leaf is open far enough to walk through.</summary>
    bool OpenEnough(int doorId);

    /// <summary>Whether a hand opens it, rather than a motor.</summary>
    bool OpensByHand(int doorId);

    /// <summary>The region holding a point: a room's flood, then the smallest region box, then the outside.
    /// The host passes its outside region id to the laws that need it.</summary>
    int RegionAt(Vector3 at);

    /// <summary>The name of the place a point is in, as the client names places; null for none.</summary>
    string? PlaceAt(Vector3 at);

    /// <summary>The ground under a point, metres. Without a grid (a test world) the point's own height.</summary>
    float Ground(Vector3 at);
}
