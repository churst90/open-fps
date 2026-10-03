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

        if (travel > 0 && (door.Travel != 1 || before <= 0f))
        {
            if (before <= 0f) OpenStart(world, entity, ref door, kind, heard);
            else if (door.Powered)
                // Turned back for somebody: the motor starts again and the leaves run open from where they are.
                Emit(heard, entity, kind, DoorEvents.Reopen,
                     Mech(heard, world, entity, door, kind, DoorEvents.Reopen, true, (1f - before) * door.SwingSeconds));
            else if (door.Slides)
                Emit(heard, entity, kind, DoorEvents.Rollers,
                     Mech(heard, world, entity, door, kind, DoorEvents.Rollers, true, (1f - before) * door.SwingSeconds));
            else Emit(heard, entity, kind, DoorEvents.Swing, Mech(heard, world, entity, door, kind, DoorEvents.Swing));
        }
        else if (travel < 0 && door.Travel != -1)
        {
            float closing = ClosingSeconds(door, before);
            if (door.Powered)
            {
                Emit(heard, entity, kind, DoorEvents.MotorStart, Mech(heard, world, entity, door, kind, DoorEvents.MotorStart, false));
                Emit(heard, entity, kind, DoorEvents.Rollers, Mech(heard, world, entity, door, kind, DoorEvents.Rollers, false, closing));
            }
            else if (door.Slides)
                Emit(heard, entity, kind, DoorEvents.Rollers, Mech(heard, world, entity, door, kind, DoorEvents.Rollers, false, closing));
            else if (door.SelfClosing)
                // The closer's brush seal wipes the frame over the last of the sweep, ending at the shut.
                Emit(heard, entity, kind, DoorEvents.Closer,
                     Mech(heard, world, entity, door, kind, DoorEvents.Closer, false, 0f,
                          MathF.Max(0f, closing - DoorMechanisms.SealSweepSeconds)));
            else Emit(heard, entity, kind, DoorEvents.Swing, Mech(heard, world, entity, door, kind, DoorEvents.Swing, false));
        }

        if (travel != 0 && now == 0)
        {
            if (door.Openness <= 0f)
            {
                string ev = door.Powered ? DoorEvents.Shut : DoorEvents.Latch;
                Emit(heard, entity, kind, ev, Mech(heard, world, entity, door, kind, ev, false));
                door.SelfClosing = false;
            }
            else if (door.Openness >= 1f && door.Slides)
                Emit(heard, entity, kind, DoorEvents.Stop, Mech(heard, world, entity, door, kind, DoorEvents.Stop));
        }
        door.Travel = now;
    }

    /// <summary>How long the leaf will take to shut from <paramref name="openness"/>, seconds, at the
    /// speeds it shuts at: a motor's, a closer's sweep and then its latch speed, or a hand's.</summary>
    internal static float ClosingSeconds(in DoorComponent door, float openness)
    {
        bool self = door.SelfClosing || door.Powered;
        if (!self || door.CloseSeconds <= 0f) return openness * door.SwingSeconds;
        if (door.Slides || door.Powered) return openness * door.CloseSeconds;
        return MathF.Max(0f, openness - LatchZone) * door.CloseSeconds + MathF.Min(openness, LatchZone) * door.SwingSeconds;
    }

    /// <summary>The events of a shut door starting to open, by what is on it.</summary>
    private static void OpenStart(World world, Entity entity, ref DoorComponent door, DoorKind kind,
                                  Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        if (door.Powered)
        {
            Emit(heard, entity, kind, DoorEvents.MotorStart, Mech(heard, world, entity, door, kind, DoorEvents.MotorStart, true));
            Emit(heard, entity, kind, DoorEvents.Rollers, Mech(heard, world, entity, door, kind, DoorEvents.Rollers, true, door.SwingSeconds));
            return;
        }
        // Each kind's first sound is its own mechanism letting go of the frame.
        switch (kind)
        {
            case DoorKind.PushBar:
                Emit(heard, entity, kind, DoorEvents.Bar, Mech(heard, world, entity, door, kind, DoorEvents.Bar));
                break;
            case DoorKind.GlassPushBar when door.KeyTurned:
                Emit(heard, entity, kind, DoorEvents.Key, Mech(heard, world, entity, door, kind, DoorEvents.Key));
                // The latch comes back once the key has turned.
                Emit(heard, entity, kind, DoorEvents.LatchRetract,
                     Mech(heard, world, entity, door, kind, DoorEvents.LatchRetract, true, 0f, DoorMechanisms.KeySeconds));
                break;
            case DoorKind.GlassPushBar:
                Emit(heard, entity, kind, DoorEvents.Bar, Mech(heard, world, entity, door, kind, DoorEvents.Bar));
                break;
            case DoorKind.GlassPull:
                Emit(heard, entity, kind, DoorEvents.Pull, Mech(heard, world, entity, door, kind, DoorEvents.Pull));
                break;
            default:
                Emit(heard, entity, kind, DoorEvents.LatchRetract, Mech(heard, world, entity, door, kind, DoorEvents.LatchRetract));
                break;
        }
        if (door.Slides)
            Emit(heard, entity, kind, DoorEvents.Rollers, Mech(heard, world, entity, door, kind, DoorEvents.Rollers, true, door.SwingSeconds));
        else Emit(heard, entity, kind, DoorEvents.Swing, Mech(heard, world, entity, door, kind, DoorEvents.Swing));
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
    /// One event's sound: its model (<see cref="DoorMechanisms"/>), named by what the leaf is made of,
    /// its size and how it is built, and placed where it happens at its measured level against the
    /// door's main hit. Empty for an event that makes no sound of its own, and when nobody is
    /// listening.
    ///
    /// Everything the model needs is already on the leaf, so a door somebody builds out of a material
    /// somebody else invented is audible the first time it shuts, with nobody having recorded anything.
    /// </summary>
    private static IReadOnlyList<TransientSound> Mech(Action<int, string, IReadOnlyList<TransientSound>>? heard,
                                                      World world, Entity entity, in DoorComponent door, DoorKind kind,
                                                      string ev, bool opening = true, float seconds = 0f, float delay = 0f)
    {
        float rel = DoorMechanisms.RelativeDb(kind, ev, opening);
        if (heard == null || float.IsNaN(rel)) return Array.Empty<TransientSound>();
        var spec = Spec(world, entity, door, kind) with { Event = ev, Seconds = seconds, Opening = opening };

        var transform = world.Get<Transform>(entity);
        var acrossLeaf = Vector3.Transform(new Vector3(spec.Width * 0.5f * -door.HingeSide, 0f, 0f), transform.Rotation);
        // A hinged door's hardware is at its latch edge; a sliding leaf is heard from itself.
        var at = door.Slides ? transform.Position : transform.Position + acrossLeaf;

        bool sustained = DoorMechanisms.IsMotionEvent(ev) || ev == DoorEvents.Closer;
        return new[]
        {
            new TransientSound
            {
                Character = sustained ? SoundCharacter.Hiss : SoundCharacter.Knock,
                DelaySeconds = delay,
                Position = at,
                LevelDb = MainHitDb(spec, door) + rel,
                Hz = 1000f,
                DecaySeconds = sustained ? MathF.Max(seconds, DoorMechanisms.SealSweepSeconds) : 0.9f,
                Noisiness = sustained ? 1f : 0.3f,
                SynthKey = DoorMechanisms.Key(spec),
            },
        };
    }

    /// <summary>The leaf, as the sound model needs it: material, size, how it is built.</summary>
    public static DoorMechanisms.Spec Spec(World world, Entity entity, in DoorComponent door, DoorKind kind)
    {
        var size = world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size
            : new Vector3(0.9f, 2.1f, 0.05f);
        string material = world.Has<MaterialComponent>(entity) ? world.Get<MaterialComponent>(entity).Material ?? "Wood" : "Wood";
        // An insulated pane's thickness is the leaf's acoustic leaf; a hollow door's skin is its skin.
        float pane = door.SkinMetres <= 0f && world.Has<AcousticComponent>(entity) ? world.Get<AcousticComponent>(entity).LeafMetres : 0f;
        return new DoorMechanisms.Spec(kind, "", material, size.X, size.Y, size.Z, door.SkinMetres, pane);
    }

    /// <summary>
    /// The door's main hit at a metre: the leaf's mass arriving at the speed it shuts at. A closer
    /// brings a swinging leaf home at latch speed, which is its hand speed; a motor brakes first.
    /// </summary>
    private static float MainHitDb(in DoorMechanisms.Spec spec, in DoorComponent door)
    {
        float seconds = door.Powered && door.CloseSeconds > 0f ? door.CloseSeconds : door.SwingSeconds;
        float average = door.Slides ? spec.Width / MathF.Max(0.1f, seconds)
                                    : DoorAcoustics.EdgeSpeed(spec.Width, door.SwingRadians, seconds);
        float arrival = DoorMechanisms.ArrivalSpeed(average, door.Slides, door.Powered);
        return DoorMechanisms.MainHitDb(DoorMechanisms.LeafMassKg(spec), arrival, hinged: !door.Slides);
    }

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
