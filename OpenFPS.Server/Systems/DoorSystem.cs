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
/// Swings and slides doors, works their closers and sensors, and opens up what you can hear through them.
///
/// Runs BEFORE <see cref="ParentSystem"/> and writes the leaf's pose into the door's
/// <see cref="ParentComponent"/> rather than its transform, because a door in a building is a part of
/// that building: ParentSystem places every part from its local pose every tick, so a system that
/// moved the transform directly would have its work overwritten before anybody saw it. A door
/// standing on its own has no parent and is moved directly.
///
/// The leaf is ALWAYS SOLID. What opening does is move it out of the doorway, which is what a door
/// does: a hinged leaf turns about its hinged edge, a sliding one moves along its own width.
///
/// The half that matters to a listener is the aperture. A door is not really a thing you hear, it is
/// a thing that changes what you can hear through it, and the portal machinery already knows how to
/// do that — so all this does is move the number, gradually, as the leaf moves. The client is told
/// when the number has moved enough to be worth hearing about, over the same path that already
/// re-sends an entity whose audio changed.
///
/// What a door does by itself comes from its <see cref="DoorComponent"/>: a closer shuts it once the
/// doorway has been clear for a while, never on anybody standing in it; a sensor opens it for anyone
/// who comes up to it; a motor reverses for anyone in the doorway. Each mechanical event is sent as
/// "door:KIND:EVENT" (<see cref="DoorEvents"/>, docs/DOOR_TYPES_EVENTS.md).
/// </summary>
public sealed class DoorSystem
{
    /// <summary>How much of its full aperture a door must have moved before the client is told again.
    /// Every tick would be thirty definitions a second for a thing that takes one second to open.</summary>
    private const float AnnounceStep = 0.15f;

    /// <summary>The last part of a closer's travel, as a fraction of fully open, that it runs at latch
    /// speed rather than sweep speed: about the last ten degrees of a quarter turn, which is where a
    /// closer's latch valve takes over to carry the bolt over the strike.</summary>
    public const float LatchZone = 0.12f;

    /// <summary>How far either side of a doorway a person counts as standing in it, metres, when the
    /// leaf slides; a swinging leaf sweeps its own width as well.</summary>
    private const float DoorwayDepthMetres = 0.8f;

    /// <summary>How far past each edge of the leaf a person still counts as in the doorway, metres.</summary>
    private const float DoorwayMarginMetres = 0.4f;

    /// <summary>How far above or below the doorway's middle a person can be and still be at it, metres:
    /// enough for anyone on its own floor, not enough for anyone on the next.</summary>
    private const float SameFloorMetres = 1.8f;

    /// <summary>What we last told the client about each door.</summary>
    private readonly Dictionary<int, float> _announced = new();
    private readonly List<Entity> _doors = new();
    private readonly List<Vector3> _people = new();
    private bool _peopleFound;

    /// <summary>
    /// One tick of every door on a map.
    ///
    /// <paramref name="announce"/> is handed the id of any door whose opening has changed enough to
    /// matter, so the caller can re-send its definition; the acoustic map on the other end is built
    /// from definitions, and an aperture nobody was told about is a door that opens silently and
    /// changes nothing. <paramref name="heard"/> is handed every mechanical event with its key and its
    /// sounds; an event with no sound yet comes with an empty list.
    /// </summary>
    public void Update(World world, float dt, Action<int> announce,
                       Action<int, string, IReadOnlyList<TransientSound>>? heard = null)
    {
        _doors.Clear();
        _peopleFound = false;
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e) => _doors.Add(e));

        foreach (var door in _doors)
        {
            try { Step(world, door, dt, announce, heard); }
            catch (Exception ex) { Log.Error(ex, "DoorSystem: door {Id} failed to move.", door.Id); }
        }
    }

    private void Step(World world, Entity entity, float dt, Action<int> announce,
                      Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        ref var door = ref world.Get<DoorComponent>(entity);
        bool parented = world.Has<ParentComponent>(entity);

        if (!door.Captured) Capture(world, entity, ref door, parented);

        // What it does by itself: only a door with a sensor, a closer or a motor, and only while it
        // could do something.
        bool held = false;
        if (door.SensorMetres > 0f
            || ((door.CloseAfterSeconds > 0f || door.Powered) && (door.Target > 0f || door.Openness > 0f)))
            held = Behave(world, entity, ref door, dt);

        float before = door.Openness;
        int travel = door.Target > door.Openness ? 1 : door.Target < door.Openness ? -1 : 0;
        if (!held)
        {
            float seconds = 0f;
            if (travel != 0)
            {
                seconds = TravelSeconds(door, travel);
                float step = seconds > 0f ? dt / seconds : 1f;
                door.Openness = travel > 0
                    ? MathF.Min(door.Target, door.Openness + step)
                    : MathF.Max(door.Target, door.Openness - step);
                Place(world, entity, door, parented);
            }
            Events(world, entity, ref door, before, travel, seconds, heard);
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
    /// The sensor, the closer and the motor's safety reversal. True when somebody is holding the leaf
    /// where it is, so it does not move this tick.
    /// </summary>
    private bool Behave(World world, Entity entity, ref DoorComponent door, float dt)
    {
        Doorway(world, entity, out var centre, out var rotation);
        float halfWidth = HalfWidth(world, entity, door);
        bool inDoorway = false, sensed = false;
        foreach (var p in People(world))
        {
            inDoorway |= InDoorway(p, centre, rotation, halfWidth, door.Slides);
            if (door.SensorMetres > 0f) sensed |= InFront(p, centre, rotation, door.SensorMetres);
            if (inDoorway && (sensed || door.SensorMetres <= 0f)) break;
        }

        // Somebody coming up to it: open, from wherever it was.
        if (sensed && door.Target < 1f)
        {
            door.Target = 1f;
            door.SelfClosing = false;
        }

        // Closing on somebody. A motor reverses; a closer is held by the person in its way.
        if (door.Target <= 0f && door.Openness > 0f && inDoorway && (door.SelfClosing || door.Powered))
        {
            if (!door.Powered) return true;
            door.Target = 1f;
            door.SelfClosing = false;
        }

        // Fully open and nobody there for long enough: it closes itself.
        if (door.CloseAfterSeconds > 0f && door.Target >= 1f)
        {
            if (inDoorway || sensed || door.Openness < 1f) door.ClearSeconds = 0f;
            else if ((door.ClearSeconds += dt) >= door.CloseAfterSeconds)
            {
                door.Target = 0f;
                door.SelfClosing = true;
                door.ClearSeconds = 0f;
            }
        }
        return false;
    }

    /// <summary>How long a whole travel takes at the speed it is going now, seconds.</summary>
    private static float TravelSeconds(in DoorComponent door, int travel)
    {
        if (travel > 0 || door.CloseSeconds <= 0f || !(door.SelfClosing || door.Powered)) return door.SwingSeconds;
        // A closer sweeps slowly and then speeds up for the latch; a motor has no latch to carry.
        if (!door.Slides && !door.Powered && door.Openness <= LatchZone) return door.SwingSeconds;
        return door.CloseSeconds;
    }

    // ── The mechanical events ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whatever the leaf just did, as the events its hardware makes. Fired on a change of direction
    /// and on arriving, never while it is travelling: what lasts as long as the travel (the hinges, the
    /// rollers) belongs to the event that starts it.
    /// </summary>
    private static void Events(World world, Entity entity, ref DoorComponent door, float before, int travel,
                               float seconds, Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        int now = door.Target > door.Openness ? 1 : door.Target < door.Openness ? -1 : 0;
        var kind = (DoorKind)door.Kind;
        var none = Array.Empty<TransientSound>();

        if (travel > 0 && (door.Travel != 1 || before <= 0f))
        {
            if (before <= 0f) OpenStart(world, entity, ref door, kind, heard);
            else if (door.Powered) Emit(heard, entity, kind, DoorEvents.Reopen, none);
            else Emit(heard, entity, kind, door.Slides ? DoorEvents.Rollers : DoorEvents.Swing, none);
        }
        else if (travel < 0 && door.Travel != -1)
        {
            if (door.Powered) { Emit(heard, entity, kind, DoorEvents.MotorStart, none); Emit(heard, entity, kind, DoorEvents.Rollers, none); }
            else if (door.Slides) Emit(heard, entity, kind, DoorEvents.Rollers, none);
            else Emit(heard, entity, kind, door.SelfClosing ? DoorEvents.Closer : DoorEvents.Swing, none);
        }

        if (travel != 0 && now == 0)
        {
            if (door.Openness <= 0f)
            {
                float width = HalfWidth(world, entity, door) * 2f;
                float edge = door.Slides ? DoorAcoustics.EdgeSpeed(width, 1f, seconds)
                                         : DoorAcoustics.EdgeSpeed(width, door.SwingRadians, seconds);
                Emit(heard, entity, kind, door.Powered ? DoorEvents.Shut : DoorEvents.Latch,
                     heard == null ? none
                     // The knob door's close is one simulation of a hand shutting it, sent as it arrives.
                     : kind == DoorKind.Hinged ? KnobDoorSound(world, entity, door, closing: true)
                     // The push-bar door's close is the closer bringing it into its latch.
                     : kind == DoorKind.PushBar ? PushBarSound(world, entity, door, closing: true)
                     : Closing(world, entity, door, edge));
                door.SelfClosing = false;
            }
            else if (door.Openness >= 1f && door.Slides)
                Emit(heard, entity, kind, DoorEvents.Stop, none);
        }
        door.Travel = now;
    }

    /// <summary>The events of a shut door starting to open, by what is on it.</summary>
    private static void OpenStart(World world, Entity entity, ref DoorComponent door, DoorKind kind,
                                  Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        var none = Array.Empty<TransientSound>();
        if (door.Powered)
        {
            Emit(heard, entity, kind, DoorEvents.MotorStart, none);
            Emit(heard, entity, kind, DoorEvents.Rollers, none);
            return;
        }
        // Each kind's first sound is its own mechanism letting go of the frame; until each has been
        // synthesised from its recordings, all of them are the latch-and-leaf model every door had.
        if (heard != null)
        {
            var opening = Opening(world, entity, door);
            switch (kind)
            {
                case DoorKind.PushBar:
                    Emit(heard, entity, kind, DoorEvents.Bar, PushBarSound(world, entity, door, closing: false));
                    break;
                case DoorKind.GlassPushBar when door.KeyTurned:
                    Emit(heard, entity, kind, DoorEvents.Key, none);
                    Emit(heard, entity, kind, DoorEvents.LatchRetract, opening);
                    break;
                case DoorKind.GlassPushBar:
                    Emit(heard, entity, kind, DoorEvents.Bar, opening);
                    break;
                case DoorKind.GlassPull:
                    Emit(heard, entity, kind, DoorEvents.Pull, opening);
                    break;
                case DoorKind.Hinged:
                    Emit(heard, entity, kind, DoorEvents.LatchRetract, KnobDoorSound(world, entity, door, closing: false));
                    break;
                default:
                    Emit(heard, entity, kind, DoorEvents.LatchRetract, opening);
                    break;
            }
            Emit(heard, entity, kind, door.Slides ? DoorEvents.Rollers : DoorEvents.Swing, none);
        }
        door.KeyTurned = false;
    }

    private static void Emit(Action<int, string, IReadOnlyList<TransientSound>>? heard, Entity entity,
                             DoorKind kind, string ev, IReadOnlyList<TransientSound> sounds)
        => heard?.Invoke(entity.Id, DoorEvents.Of(kind, ev), sounds);

    // ── Where things are ────────────────────────────────────────────────────────────────────────

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
        // Where the hole is: the leaf's world pose now, shut, before anything has moved it. The client
        // hears through the doorway, and the leaf's own transform stops saying where that is the moment
        // it opens.
        if (world.Has<PortalComponent>(entity))
        {
            var shut = world.Get<Transform>(entity);
            ref var portal = ref world.Get<PortalComponent>(entity);
            portal.OpeningCentre = shut.Position;
            portal.OpeningRotation = shut.Rotation;
        }
        if (door.SwingSeconds <= 0f) door.SwingSeconds = 0.9f;
        if (door.SwingRadians == 0f) door.SwingRadians = MathF.PI / 2f;
        if (door.HingeSide == 0f) door.HingeSide = 1f;
        door.Captured = true;
    }

    /// <summary>
    /// Puts the leaf where its current openness says it should be.
    ///
    /// A hinged leaf turns about its hinged EDGE, not its middle — swinging about the centre would
    /// sweep the leaf through the doorway in both directions and leave half of it still in the way at
    /// ninety degrees. A sliding leaf moves along its own width, toward <see cref="DoorComponent.HingeSide"/>,
    /// by as much as it is wide: fully open, it stands clear beside the doorway, in the wall's pocket
    /// or on its own track past the next panel.
    /// </summary>
    private static void Place(World world, Entity entity, DoorComponent door, bool parented)
    {
        float halfWidth = HalfWidth(world, entity, door);

        float shut = door.ShutYaw;
        Vector3 position;
        Quaternion rotation;
        if (door.Slides)
        {
            var along = Vector3.Transform(new Vector3(door.Openness * 2f * halfWidth * door.HingeSide, 0f, 0f),
                                          Quaternion.CreateFromYawPitchRoll(shut, 0f, 0f));
            position = door.ShutPosition + along;
            rotation = Quaternion.CreateFromYawPitchRoll(shut, 0f, 0f);
        }
        else
        {
            float swung = shut + door.Openness * door.SwingRadians * -door.HingeSide;

            // Where the hinged edge is: half a leaf along the shut leaf's own width, on the hinge side.
            var alongShut = Vector3.Transform(new Vector3(halfWidth * door.HingeSide, 0f, 0f),
                                              Quaternion.CreateFromYawPitchRoll(shut, 0f, 0f));
            var hinge = door.ShutPosition + alongShut;

            // ...and where the centre of the leaf ends up once it has turned about that edge.
            var alongSwung = Vector3.Transform(new Vector3(halfWidth * door.HingeSide, 0f, 0f),
                                               Quaternion.CreateFromYawPitchRoll(swung, 0f, 0f));
            position = hinge - alongSwung;
            rotation = Quaternion.CreateFromYawPitchRoll(swung, 0f, 0f);
        }

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

    private static float HalfWidth(World world, Entity entity, in DoorComponent door)
        => world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size.X * 0.5f
            : door.Aperture * 0.5f;

    /// <summary>
    /// Where the doorway is in the world, and which way it faces: the leaf's shut pose, wherever the
    /// building it belongs to now stands. Local X runs across the doorway, local Z through it.
    /// </summary>
    public static void Doorway(World world, Entity entity, out Vector3 centre, out Quaternion rotation)
    {
        var t = world.Get<Transform>(entity);
        var door = world.Get<DoorComponent>(entity);
        if (!door.Captured) { centre = t.Position; rotation = t.Rotation; return; }
        var shut = Quaternion.CreateFromYawPitchRoll(door.ShutYaw, 0f, 0f);
        if (world.Has<ParentComponent>(entity))
        {
            // The building's pose, from the leaf's world pose and its pose in the building.
            var p = world.Get<ParentComponent>(entity);
            var building = Quaternion.Normalize(t.Rotation * Quaternion.Inverse(p.LocalRotation));
            var origin = t.Position - Vector3.Transform(p.LocalPosition, building);
            centre = origin + Vector3.Transform(door.ShutPosition, building);
            rotation = building * shut;
        }
        else
        {
            centre = door.ShutPosition;
            rotation = shut;
        }
    }

    /// <summary>Standing in the doorway: across it within the leaf and a margin, through it within
    /// arm's length (or the leaf's own width, for a leaf that swings), and on its floor.</summary>
    internal static bool InDoorway(Vector3 p, Vector3 centre, Quaternion rotation, float halfWidth, bool slides)
    {
        var d = p - centre;
        if (MathF.Abs(d.Y) > SameFloorMetres) return false;
        float across = MathF.Abs(Vector3.Dot(d, Vector3.Transform(Vector3.UnitX, rotation)));
        float through = MathF.Abs(Vector3.Dot(d, Vector3.Transform(Vector3.UnitZ, rotation)));
        float depth = slides ? DoorwayDepthMetres : MathF.Max(DoorwayDepthMetres, 2f * halfWidth);
        return across <= halfWidth + DoorwayMarginMetres && through <= depth;
    }

    /// <summary>In front of it, either side, within its sensor's reach — through the doorway and
    /// along the wall alike, as a door sensor's field is.</summary>
    internal static bool InFront(Vector3 p, Vector3 centre, Quaternion rotation, float reach)
    {
        var d = p - centre;
        if (MathF.Abs(d.Y) > SameFloorMetres) return false;
        float across = MathF.Abs(Vector3.Dot(d, Vector3.Transform(Vector3.UnitX, rotation)));
        float through = MathF.Abs(Vector3.Dot(d, Vector3.Transform(Vector3.UnitZ, rotation)));
        return across <= reach && through <= reach;
    }

    /// <summary>Everybody on foot: players not sitting in anything, and the people in the street.</summary>
    private List<Vector3> People(World world)
    {
        if (_peopleFound) return _people;
        _people.Clear();
        world.Query(new QueryDescription().WithAll<Transform>().WithAny<PlayerComponent, Pedestrian>().WithNone<OccupantComponent>(),
                    (ref Transform t) => _people.Add(t.Position));
        _peopleFound = true;
        return _people;
    }

    // ── What it sounds like ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the leaf and its latch sound like as it leaves the frame and as it meets it.
    ///
    /// Everything the model needs is already on the leaf: what it is made of, how big it is, how
    /// thick, and how fast its latch edge was travelling. So a door somebody builds out of a material
    /// somebody else invented is audible the first time it shuts, with nobody having recorded anything.
    /// </summary>
    private static IReadOnlyList<TransientSound> Opening(World world, Entity entity, in DoorComponent door)
        => Sounds(world, entity, door, opening: true, edgeSpeed: 0f);

    private static IReadOnlyList<TransientSound> Closing(World world, Entity entity, in DoorComponent door, float edgeSpeed)
        => Sounds(world, entity, door, opening: false, edgeSpeed);

    private static List<TransientSound> Sounds(World world, Entity entity, in DoorComponent door, bool opening, float edgeSpeed)
    {
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
        float ringThickness = size.Z;

        // ...unless it is not solid. A steel door is two skins of sheet folded over a core — 1.2 mm
        // of steel either side of forty-odd millimetres of honeycomb — and reckoned as a solid slab it
        // weighed 2.4 tonnes and slammed seventeen decibels too hard. The prefab says so the way any
        // door does, with its skin thickness. The skins weigh what they weigh; and bonded to a
        // core they bend as a sandwich, stiff for their mass, so the note is that of a solid plate
        // with the same stiffness-to-mass ratio: t = sqrt(6 rho s (d - s)^2 / m).
        if (door.SkinMetres > 0f)
        {
            float skin = MathF.Min(door.SkinMetres, size.Z * 0.5f);
            float area = size.X * size.Y;
            float rho = MathF.Max(100f, material.DensityKgM3);
            float perArea = 2f * skin * rho + MathF.Max(0f, size.Z - 2f * skin) * HollowCoreKgM3;
            massKg = MathF.Max(2f, area * perArea);
            ringThickness = MathF.Sqrt(6f * rho * skin * (size.Z - skin) * (size.Z - skin) / perArea);
        }

        // A seal is a property of what the thing is FOR: anything that keeps weather or noise out has
        // one, and the material is the best evidence available. Metal and glass doors are sealed;
        // a wooden one in a shed is not.
        bool hasSeal = material.DensityKgM3 > 2000f;

        var sounds = opening
            // Hinges silent by default. A creak is a FAULT — a dry pin in a dry knuckle — and most
            // doors do not have one; rendering three quarters of a second of stick-slip on every
            // door made every door sound like a haunted house, which a listener heard as an
            // unexplained hiss either side of the thud. A gate or a cellar door can ask for it.
            ? DoorAcoustics.Opening(material, latchEdge, hinge, size.X, size.Y, ringThickness, massKg,
                                    door.SwingSeconds, hingeDryness: 0f, hasSeal)
            : DoorAcoustics.Closing(material, latchEdge, transform.Position, size.X, size.Y, ringThickness, massKg,
                                    edgeSpeed, hasSeal);

        var transients = new List<TransientSound>(sounds.Count);
        foreach (var sound in sounds) transients.Add(sound.ToTransient());
        return transients;
    }

    /// <summary>
    /// The knob door as a physical model (<see cref="KnobDoor"/>): the server names it, the client
    /// renders it. The door's id picks its character, so each door keeps its own hinges.
    /// </summary>
    private static readonly Random _shutDice = new();

    /// <summary>The push-bar door as a physical model (<see cref="PushBarDoor"/>); its id picks its
    /// character (silencers, bar stops, how its closer is set).</summary>
    private static IReadOnlyList<TransientSound> PushBarSound(World world, Entity entity, in DoorComponent door, bool closing)
    {
        var size = world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size
            : new Vector3(1.0f, 2.1f, 0.08f);
        var transform = world.Get<Transform>(entity);
        var latchEdge = transform.Position
                      + Vector3.Transform(new Vector3(size.X * 0.5f * -door.HingeSide, 0f, 0f), transform.Rotation);
        int variant = entity.Id;
        return new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock, Position = latchEdge, Hz = 300f, Noisiness = 1f,
                LevelDb = closing ? PushBarDoor.CloseLevelDb(variant) : PushBarDoor.OpenLevelDb(variant),
                DecaySeconds = 1.8f,
                SynthKey = PushBarDoor.Key(closing, variant, door.SwingSeconds, size.X, size.Y),
            },
        };
    }

    private static IReadOnlyList<TransientSound> KnobDoorSound(World world, Entity entity, in DoorComponent door, bool closing)
    {
        var size = world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size
            : new Vector3(0.9f, 2.1f, 0.05f);
        var transform = world.Get<Transform>(entity);
        var latchEdge = transform.Position
                      + Vector3.Transform(new Vector3(size.X * 0.5f * -door.HingeSide, 0f, 0f), transform.Rotation);
        // Wooden knob doors are interior doors, and interior doors are hollow-core.
        // People shut a door differently each time: mostly guided in, sometimes eased, sometimes pushed.
        var how = !closing ? KnobDoor.Shut.Normal : _shutDice.NextDouble() switch
        {
            < 0.25 => KnobDoor.Shut.Gentle,
            < 0.80 => KnobDoor.Shut.Normal,
            _ => KnobDoor.Shut.Hard,
        };
        string key = KnobDoor.Key(closing, KnobDoor.Construction.HollowCore, entity.Id, door.SwingSeconds, how, size.X, size.Y);
        return new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock, Position = latchEdge, Hz = 500f, Noisiness = 1f,
                LevelDb = closing ? KnobDoor.CloseLevelDb(how) : KnobDoor.OpenLevelDb,
                DecaySeconds = closing ? 1.6f : door.SwingSeconds + 0.85f,
                SynthKey = key,
            },
        };
    }

    /// <summary>What fills a hollow door between its skins, kg/m^3: kraft honeycomb or mineral core,
    /// with the edge channels and the lock reinforcement averaged in.</summary>
    private const float HollowCoreKgM3 = 150f;

    // ── Asking a door to move ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks a door to open or shut. Returns false if it is already going that way.
    ///
    /// <paramref name="by"/> is where whoever is opening it is standing, when there is somebody: from
    /// a door's keyed side, opening it means turning a key, which is a sound of its own. Every player
    /// and every resident has the key; a lock that keeps somebody out needs a key to be an item, which
    /// does not exist yet (docs/DOOR_TYPES_EVENTS.md).
    /// </summary>
    public static bool Set(World world, Entity entity, bool open, Vector3? by = null)
    {
        if (!world.Has<DoorComponent>(entity)) return false;
        bool keyed = false;
        if (open && by.HasValue && world.Get<DoorComponent>(entity).KeyedSide != 0f)
        {
            Doorway(world, entity, out var centre, out var rotation);
            float side = Vector3.Dot(by.Value - centre, Vector3.Transform(Vector3.UnitZ, rotation));
            keyed = side * world.Get<DoorComponent>(entity).KeyedSide > 0f;
        }
        ref var door = ref world.Get<DoorComponent>(entity);
        float target = open ? 1f : 0f;
        if (door.Target == target) return false;
        door.Target = target;
        door.SelfClosing = false;
        door.ClearSeconds = 0f;
        if (open && door.Openness <= 0f) door.KeyTurned = keyed;
        return true;
    }

    /// <summary>Whether a person opens and shuts it with their hand. A motor's doors open for you or
    /// for the lift, never by hand.</summary>
    public static bool OpensByHand(in DoorComponent door) => !door.Powered;

    /// <summary>"swings" or "slides", for saying what a door just did.</summary>
    public static string Verb(in DoorComponent door) => door.Slides ? "slides" : "swings";

    /// <summary>Open far enough to walk through without touching it.</summary>
    public static bool OpenEnough(World world, Entity entity)
        => !world.IsAlive(entity) || !world.Has<DoorComponent>(entity) || world.Get<DoorComponent>(entity).Openness >= 0.9f;

    /// <summary>Forgets a door that no longer exists, so the announcement memory does not grow forever.</summary>
    public void Forget(int entityId) => _announced.Remove(entityId);
}
