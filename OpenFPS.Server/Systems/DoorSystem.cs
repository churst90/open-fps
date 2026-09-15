using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Swings doors, and opens up what you can hear through them.
///
/// Runs BEFORE <see cref="ParentSystem"/> and writes the swing into the door's
/// <see cref="ParentComponent"/> rather than its transform, because a door in a building is a part of
/// that building: ParentSystem places every part from its local pose every tick, so a system that
/// moved the transform directly would have its work overwritten before anybody saw it. A door
/// standing on its own has no parent and is moved directly.
///
/// The leaf is ALWAYS SOLID. What opening does is move it out of the doorway, which is what a door
/// does. Making it non-solid instead would produce a door you can walk through while it is shut and
/// standing in front of you, and one whose open leaf is not in the way of anything.
///
/// The half that matters to a listener is the aperture. A door is not really a thing you hear, it is
/// a thing that changes what you can hear through it, and the portal machinery already knows how to
/// do that — so all this does is move the number, gradually, as the leaf swings. The client is told
/// when the number has moved enough to be worth hearing about, over the same path that already
/// re-sends an entity whose audio changed.
/// </summary>
public sealed class DoorSystem
{
    /// <summary>How much of its full aperture a door must have moved before the client is told again.
    /// Every tick would be thirty definitions a second for a thing that takes one second to open.</summary>
    private const float AnnounceStep = 0.15f;

    /// <summary>What we last told the client about each door.</summary>
    private readonly Dictionary<int, float> _announced = new();
    private readonly List<Entity> _doors = new();

    /// <summary>
    /// One tick of every door on a map.
    ///
    /// <paramref name="announce"/> is handed the id of any door whose opening has changed enough to
    /// matter, so the caller can re-send its definition; the acoustic map on the other end is built
    /// from definitions, and an aperture nobody was told about is a door that opens silently and
    /// changes nothing.
    /// </summary>
    public void Update(World world, float dt, Action<int> announce,
                       Action<int, string, IReadOnlyList<TransientSound>>? heard = null)
    {
        _doors.Clear();
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e) => _doors.Add(e));

        foreach (var door in _doors)
        {
            try { Step(world, door, dt, announce, heard); }
            catch (Exception ex) { Log.Error(ex, "DoorSystem: door {Id} failed to swing.", door.Id); }
        }
    }

    private void Step(World world, Entity entity, float dt, Action<int> announce,
                      Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        ref var door = ref world.Get<DoorComponent>(entity);
        bool parented = world.Has<ParentComponent>(entity);

        if (!door.Captured) Capture(world, entity, ref door, parented);

        if (door.Openness != door.Target)
        {
            float step = door.SwingSeconds > 0f ? dt / door.SwingSeconds : 1f;
            bool wasShut = door.Openness <= 0f;
            door.Openness = door.Target > door.Openness
                ? MathF.Min(door.Target, door.Openness + step)
                : MathF.Max(door.Target, door.Openness - step);
            Place(world, entity, door, parented);

            // The two moments a door makes a noise: the instant it starts to open, and the instant it
            // arrives shut. Not while it is travelling — that is the hinges, and they are part of the
            // opening event because they last as long as the swing does.
            if (wasShut && door.Openness > 0f) Heard(world, entity, door, opening: true, heard);
            else if (door.Openness <= 0f && door.Target <= 0f) Heard(world, entity, door, opening: false, heard);
        }

        // The opening, which is the half anyone listening cares about.
        if (world.Has<PortalComponent>(entity))
        {
            ref var portal = ref world.Get<PortalComponent>(entity);
            float aperture = door.Openness * door.Aperture;
            portal.ApertureSize = aperture;

            if (!_announced.TryGetValue(entity.Id, out float last)
                || MathF.Abs(aperture - last) >= AnnounceStep * MathF.Max(0.01f, door.Aperture)
                || (aperture <= 0f && last > 0f)
                || (door.Openness >= 1f && last < door.Aperture))
            {
                _announced[entity.Id] = aperture;
                announce(entity.Id);
            }
        }
    }

    /// <summary>
    /// Records where "shut" is, the first time a door is looked at.
    ///
    /// Taken from wherever the door actually is rather than from anything authored, so a door placed
    /// by a prefab, dropped by a build cursor or rebuilt from a saved template is shut where it was
    /// put — and a template that carries a door does not need to carry a second copy of its pose that
    /// could disagree with the first.
    /// </summary>
    private static void Capture(World world, Entity entity, ref DoorComponent door, bool parented)
    {
        if (parented)
        {
            var parent = world.Get<ParentComponent>(entity);
            door.ShutPosition = parent.LocalPosition;
            MathHelper.ToYawPitch(parent.LocalRotation, out float yaw, out _);
            door.ShutYaw = yaw;
        }
        else
        {
            var t = world.Get<Transform>(entity);
            door.ShutPosition = t.Position;
            MathHelper.ToYawPitch(t.Rotation, out float yaw, out _);
            door.ShutYaw = yaw;
        }

        // A door makes a hole exactly its own size, so the opening is the leaf's own width — no
        // authored number to disagree with the thing it describes.
        if (door.Aperture <= 0f && world.Has<ColliderComponent>(entity))
        {
            var size = world.Get<ColliderComponent>(entity).Size;
            door.Aperture = MathF.Max(0.1f, MathF.Max(size.X, size.Z));
        }
        if (door.SwingSeconds <= 0f) door.SwingSeconds = 0.9f;
        if (door.SwingRadians == 0f) door.SwingRadians = MathF.PI / 2f;
        if (door.HingeSide == 0f) door.HingeSide = 1f;
        door.Captured = true;
    }

    /// <summary>
    /// Puts the leaf where its current openness says it should be: rotated about its hinged edge.
    ///
    /// The hinge is an EDGE, not the middle, which is the whole geometry of a door — swinging about
    /// the centre would sweep the leaf through the doorway in both directions and leave half of it
    /// still in the way at ninety degrees.
    /// </summary>
    private static void Place(World world, Entity entity, DoorComponent door, bool parented)
    {
        float halfWidth = world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size.X * 0.5f
            : door.Aperture * 0.5f;

        float shut = door.ShutYaw;
        float swung = shut + door.Openness * door.SwingRadians * -door.HingeSide;

        // Where the hinged edge is: half a leaf along the shut leaf's own width, on the hinge side.
        var alongShut = Vector3.Transform(new Vector3(halfWidth * door.HingeSide, 0f, 0f),
                                          Quaternion.CreateFromYawPitchRoll(shut, 0f, 0f));
        var hinge = door.ShutPosition + alongShut;

        // ...and where the centre of the leaf ends up once it has turned about that edge.
        var alongSwung = Vector3.Transform(new Vector3(halfWidth * door.HingeSide, 0f, 0f),
                                           Quaternion.CreateFromYawPitchRoll(swung, 0f, 0f));
        var position = hinge - alongSwung;
        var rotation = Quaternion.CreateFromYawPitchRoll(swung, 0f, 0f);

        if (parented)
        {
            ref var parent = ref world.Get<ParentComponent>(entity);
            parent.LocalPosition = position;
            parent.LocalRotation = rotation;
        }
        else
        {
            ref var t = ref world.Get<Transform>(entity);
            t.Position = position;
            t.Rotation = rotation;
            t.IsDirty = true;
        }
    }

    /// <summary>
    /// Works out what the door just sounded like and hands it over.
    ///
    /// Everything the model needs is already on the leaf: what it is made of, how big it is, how
    /// thick, and — from its own swing time and width — how fast its latch edge was travelling. So a
    /// door somebody builds out of a material somebody else invented is audible the first time it
    /// shuts, with nobody having recorded anything.
    /// </summary>
    private static void Heard(World world, Entity entity, DoorComponent door, bool opening,
                              Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        if (heard == null) return;

        var size = world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size
            : new Vector3(0.9f, 2.1f, 0.05f);
        var material = AcousticRegistry.GetProperties(
            world.Has<MaterialComponent>(entity) ? world.Get<MaterialComponent>(entity).Material ?? "Wood" : "Wood");

        var transform = world.Get<Transform>(entity);
        float halfWidth = size.X * 0.5f;
        var acrossLeaf = Vector3.Transform(new Vector3(halfWidth * -door.HingeSide, 0f, 0f), transform.Rotation);
        var latchEdge = transform.Position + acrossLeaf;
        var hinge = transform.Position - acrossLeaf;

        // Mass from the leaf's own volume and the density of what it is made of. A steel door is
        // heavy because steel is heavy, not because somebody typed a number.
        float massKg = MathF.Max(2f, size.X * size.Y * size.Z * MathF.Max(100f, material.DensityKgM3));

        // A seal is a property of what the thing is FOR: anything that keeps weather or noise out has
        // one, and the material is the best evidence available. Metal and glass doors are sealed;
        // a wooden one in a shed is not.
        bool hasSeal = material.DensityKgM3 > 2000f;

        var sounds = opening
            // Hinges silent by default. A creak is a FAULT — a dry pin in a dry knuckle — and most
            // doors do not have one; rendering three quarters of a second of stick-slip on every
            // door made every door sound like a haunted house, which a listener heard as an
            // unexplained hiss either side of the thud. A gate or a cellar door can ask for it.
            ? DoorAcoustics.Opening(material, latchEdge, hinge, size.X, size.Y, size.Z,
                                    door.SwingSeconds, hingeDryness: 0f, hasSeal)
            : DoorAcoustics.Closing(material, latchEdge, transform.Position, size.X, size.Y, size.Z, massKg,
                                    DoorAcoustics.EdgeSpeed(size.X, door.SwingRadians, door.SwingSeconds), hasSeal);

        var transients = new List<TransientSound>(sounds.Count);
        foreach (var sound in sounds) transients.Add(sound.ToTransient());

        string name = world.Has<IdentityComponent>(entity) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(entity).Name)
            ? world.Get<IdentityComponent>(entity).Name : "door";
        heard(entity.Id, name, transients);
    }

    /// <summary>Asks a door to open or shut. Returns false if it is already going that way.</summary>
    public static bool Set(World world, Entity entity, bool open)
    {
        if (!world.Has<DoorComponent>(entity)) return false;
        ref var door = ref world.Get<DoorComponent>(entity);
        float target = open ? 1f : 0f;
        if (door.Target == target) return false;
        door.Target = target;
        return true;
    }

    /// <summary>Forgets a door that no longer exists, so the announcement memory does not grow forever.</summary>
    public void Forget(int entityId) => _announced.Remove(entityId);
}
