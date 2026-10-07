using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Swings and slides doors, works their closers and sensors, and opens up what you can hear through them.
///
/// Runs BEFORE <see cref="ParentSystem"/> and writes a door in a building into its
/// <see cref="ParentComponent"/>, not its transform: ParentSystem places every part from its local pose
/// each tick and would overwrite a moved transform. A door on its own has no parent and is moved directly.
///
/// The leaf is always solid; opening moves it out of the doorway. What a listener hears is the portal's
/// aperture, which follows the leaf and is re-sent when it has moved enough to matter.
///
/// Closers, sensors and motors come from the <see cref="DoorComponent"/>. Each mechanical event is sent
/// as "door:KIND:EVENT" (<see cref="DoorEvents"/>, docs/DOOR_TYPES_EVENTS.md).
/// </summary>
public sealed class DoorSystem
{
    /// <summary>How much of its full aperture a door must have moved before the client is told again;
    /// every tick would be thirty definitions a second.</summary>
    private const float AnnounceStep = 0.15f;

    /// <summary>The last part of a closer's travel, as a fraction of fully open, run at latch speed:
    /// about the last ten degrees, where a closer's latch valve takes over.</summary>
    public const float LatchZone = 0.12f;

    /// <summary>How far either side of a doorway a person counts as standing in it, metres, when the
    /// leaf slides; a swinging leaf sweeps its own width as well.</summary>
    private const float DoorwayDepthMetres = 0.8f;

    /// <summary>How far past each edge of the leaf a person still counts as in the doorway, metres.</summary>
    private const float DoorwayMarginMetres = 0.4f;

    /// <summary>How far above or below the doorway's middle a person can be and still be at it, metres:
    /// enough for anyone on its own floor, not enough for anyone on the next.</summary>
    private const float SameFloorMetres = 1.8f;

    /// <summary>The aperture each door was last announced at.</summary>
    private readonly Dictionary<int, float> _announced = new();
    private readonly List<Entity> _doors = new();
    private readonly List<(Entity Who, Vector3 At)> _people = new();
    private bool _peopleFound;

    /// <summary>
    /// One tick of every door on a map. <paramref name="announce"/> gets each door whose aperture has
    /// changed enough to re-send (the client's acoustics are built from definitions); <paramref name="heard"/>
    /// gets every mechanical event with its key and sounds, an empty list when it has none.
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

        // A key going into the lock and turning: the leaf does not move until it has.
        bool held = door.KeySeconds > 0f && TurnTheKey(world, entity, ref door, dt, heard);

        if (!held && (door.SensorMetres > 0f
            || ((door.CloseAfterSeconds > 0f || door.Powered) && (door.Target > 0f || door.Openness > 0f))))
            held = Behave(world, entity, ref door, dt);

        float before = door.Openness;
        int travel = door.Target > door.Openness ? 1 : door.Target < door.Openness ? -1 : 0;

        // Nobody is swept aside: the leaf stops at them, and a motor closing on them opens again.
        // Asked of this tick's step, so the leaf goes as far as the person and no further.
        if (!held && travel != 0 && FirstInTheWay(world, entity, door, NextOpenness(door, travel, dt), People(world)) != null)
        {
            if (door.Powered && travel < 0)
            {
                door.Target = 1f;
                door.SelfClosing = false;
                travel = 1;
            }
            else held = true;
        }

        if (!held)
        {
            float seconds = 0f;
            if (travel != 0)
            {
                seconds = TravelSeconds(door, travel);
                door.Openness = NextOpenness(door, travel, dt);
                Place(world, entity, door, parented);
            }
            Events(world, entity, ref door, before, travel, seconds, heard);
        }

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
        foreach (var (_, p) in People(world))
        {
            inDoorway |= InDoorway(p, centre, rotation, halfWidth, door.Slides);
            if (door.SensorMetres > 0f) sensed |= InFront(p, centre, rotation, door.SensorMetres);
            if (inDoorway && (sensed || door.SensorMetres <= 0f)) break;
        }

        if (sensed && door.Target < 1f)
        {
            door.Target = 1f;
            door.SelfClosing = false;
            door.HandId = 0;
            door.OpenedFrom = 0;
        }

        // Closing on somebody. A motor reverses; a closer is held by the person in its way.
        if (door.Target <= 0f && door.Openness > 0f && inDoorway && (door.SelfClosing || door.Powered))
        {
            if (!door.Powered) return true;
            door.Target = 1f;
            door.SelfClosing = false;
        }

        if (door.CloseAfterSeconds > 0f && door.Target >= 1f)
        {
            if (inDoorway || sensed || door.Openness < 1f) door.ClearSeconds = 0f;
            else if ((door.ClearSeconds += dt) >= door.CloseAfterSeconds)
            {
                door.Target = 0f;
                door.SelfClosing = true;
                door.ClearSeconds = 0f;
                door.HandId = 0;
            }
        }
        return false;
    }

    /// <summary>Where the leaf will be after this tick's step toward its target.</summary>
    private static float NextOpenness(in DoorComponent door, int travel, float dt)
    {
        float seconds = TravelSeconds(door, travel);
        float step = seconds > 0f ? dt / seconds : 1f;
        return travel > 0 ? MathF.Min(door.Target, door.Openness + step)
                          : MathF.Max(door.Target, door.Openness - step);
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
            else Emit(heard, entity, kind, door.Slides ? DoorEvents.Rollers : HandEvent(door), none);
        }
        else if (travel < 0 && door.Travel != -1)
        {
            // A sliding door's close is one simulation of its whole run, so it goes as the run starts;
            // sent on arrival, its roll played on after the leaf had shut.
            if (door.Powered)
            {
                Emit(heard, entity, kind, DoorEvents.MotorStart,
                     heard == null ? none
                     : kind == DoorKind.AutoSliding ? SlidingSound(world, entity, door, closing: true)
                     : kind == DoorKind.Elevator ? ElevatorSound(world, entity, door, closing: true) : none);
                Emit(heard, entity, kind, DoorEvents.Rollers, none);
            }
            else if (door.Slides)
                Emit(heard, entity, kind, DoorEvents.Rollers,
                     kind == DoorKind.PatioSliding && heard != null ? SlidingSound(world, entity, door, closing: true) : none);
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
                     : kind == DoorKind.Hinged ? KnobDoorSound(world, entity, door, closing: true)
                     : kind == DoorKind.PushBar ? PushBarSound(world, entity, door, closing: true)
                     : kind is DoorKind.GlassPushBar or DoorKind.GlassPull ? GlassDoorSound(world, entity, door, closing: true)
                     // A sliding door's or a lift's arrival is already in the run it sent as it set off.
                     : kind is DoorKind.PatioSliding or DoorKind.AutoSliding or DoorKind.Elevator ? none
                     : Closing(world, entity, door, edge));
                door.SelfClosing = false;
            }
            else if (door.Openness >= 1f && door.Slides)
                Emit(heard, entity, kind, DoorEvents.Stop, none);
            // Arrived: whoever moved it has let go.
            door.HandId = 0;
        }
        door.Travel = now;
    }

    /// <summary>A hand starting a hinged leaf moving open: "push" from its push side, "pull" from the
    /// other, "swing" when nobody knows which (a hinged door opened by a sensor).</summary>
    private static string HandEvent(in DoorComponent door)
        => door.OpenedFrom > 0 ? DoorEvents.Push : door.OpenedFrom < 0 ? DoorEvents.Pull : DoorEvents.Swing;

    /// <summary>Kinds with a push bar, which is on the push side only.</summary>
    public static bool HasBar(DoorKind kind) => kind is DoorKind.PushBar or DoorKind.GlassPushBar;

    /// <summary>The events of a shut door starting to open, by what is on it.</summary>
    private static void OpenStart(World world, Entity entity, ref DoorComponent door, DoorKind kind,
                                  Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        var none = Array.Empty<TransientSound>();
        if (door.Powered)
        {
            Emit(heard, entity, kind, DoorEvents.MotorStart,
                 heard == null ? none
                 : kind == DoorKind.AutoSliding ? SlidingSound(world, entity, door, closing: false)
                 : kind == DoorKind.Elevator ? ElevatorSound(world, entity, door, closing: false) : none);
            Emit(heard, entity, kind, DoorEvents.Rollers, none);
            return;
        }
        // First the mechanism that lets go of the frame, which depends on the side (a push bar is on the
        // push side only; from the other, the pull trim draws the same latch), then the hand. From a keyed
        // side the key has already drawn the latch (TurnTheKey), except on a knob door, whose knob still turns.
        if (heard != null)
        {
            bool pulled = door.OpenedFrom < 0;
            string hand = HandEvent(door);
            if (door.Slides)
            {
                Emit(heard, entity, kind, DoorEvents.LatchRetract,
                     kind == DoorKind.PatioSliding ? SlidingSound(world, entity, door, closing: false) : Opening(world, entity, door));
                Emit(heard, entity, kind, DoorEvents.Rollers, none);
            }
            else if (door.KeyTurned && kind != DoorKind.Hinged)
                // A glass front door is pulled on its handle with the key still held (GlassDoor).
                Emit(heard, entity, kind, hand,
                     kind == DoorKind.GlassPushBar ? GlassDoorSound(world, entity, door, closing: false, GlassDoor.Opening.Key) : none);
            else
            {
                switch (kind)
                {
                    case DoorKind.Hinged:
                        Emit(heard, entity, kind, DoorEvents.LatchRetract, KnobDoorSound(world, entity, door, closing: false, pulled));
                        break;
                    case DoorKind.PushBar:
                        Emit(heard, entity, kind, pulled ? DoorEvents.LatchRetract : DoorEvents.Bar,
                             PushBarSound(world, entity, door, closing: false, pulled));
                        break;
                    case DoorKind.GlassPushBar:
                        Emit(heard, entity, kind, pulled ? DoorEvents.LatchRetract : DoorEvents.Bar,
                             GlassDoorSound(world, entity, door, closing: false, pulled ? GlassDoor.Opening.Pull : GlassDoor.Opening.Push));
                        break;
                    case DoorKind.GlassPull:
                        // No latch: a shop's door is held by its closer, and the hand is the first sound.
                        Emit(heard, entity, kind, hand,
                             GlassDoorSound(world, entity, door, closing: false, door.OpenedFrom > 0 ? GlassDoor.Opening.Push : GlassDoor.Opening.Pull));
                        hand = "";
                        break;
                    default:
                        Emit(heard, entity, kind, DoorEvents.LatchRetract, Opening(world, entity, door));
                        break;
                }
                if (hand.Length > 0) Emit(heard, entity, kind, hand, none);
            }
        }
        door.KeyTurned = false;
    }

    // ── The key ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>How long the key takes from going in to the leaf starting to move, seconds.</summary>
    public const float KeySequenceSeconds = 1.0f;

    /// <summary>When, after it goes in, the key turns, seconds.</summary>
    public const float KeyTurnSeconds = 0.45f;

    /// <summary>When, after it goes in, the turned key has drawn the latch back, seconds.</summary>
    public const float UnlockSeconds = 0.7f;

    /// <summary>
    /// One tick of a key in a lock on the keyed side of a shut door: in, turned, the latch drawn, each
    /// sent as its own event. True while the key is still being worked, so the leaf waits.
    /// </summary>
    private static bool TurnTheKey(World world, Entity entity, ref DoorComponent door, float dt,
                                   Action<int, string, IReadOnlyList<TransientSound>>? heard)
    {
        var kind = (DoorKind)door.Kind;
        var none = Array.Empty<TransientSound>();
        float was = KeySequenceSeconds - door.KeySeconds;
        door.KeySeconds = MathF.Max(0f, door.KeySeconds - dt);
        float now = KeySequenceSeconds - door.KeySeconds;
        bool done = door.KeySeconds <= 0f;
        bool At(float t) => was <= t && (t < now || done);

        // The key's whole working is one LockCylinder sound sent at key-insert and paced to these
        // constants; the turn and the unlock are its moments, silent here.
        if (At(0f)) Emit(heard, entity, kind, DoorEvents.KeyInsert, heard == null || door.Slides ? none : KeySound(world, entity, door));
        if (At(KeyTurnSeconds)) Emit(heard, entity, kind, DoorEvents.KeyTurn, none);
        if (At(UnlockSeconds)) Emit(heard, entity, kind, DoorEvents.Unlock, none);
        return !done;
    }

    private static void Emit(Action<int, string, IReadOnlyList<TransientSound>>? heard, Entity entity,
                             DoorKind kind, string ev, IReadOnlyList<TransientSound> sounds)
        => heard?.Invoke(entity.Id, DoorEvents.Of(kind, ev), sounds);

    // ── Where things are ────────────────────────────────────────────────────────────────────────

    /// <summary>A door made again shut where its doorway is, given the openness it had: its shut pose
    /// taken there, and its leaf put where the openness says (WorldEditor.Remake).</summary>
    internal static void Settle(World world, Entity entity)
    {
        ref var door = ref world.Get<DoorComponent>(entity);
        bool parented = world.Has<ParentComponent>(entity);
        if (!door.Captured) Capture(world, entity, ref door, parented);
        Place(world, entity, door, parented);
    }

    /// <summary>
    /// Records where "shut" is, the first time a door is looked at: wherever it actually stands, so a
    /// door from a prefab, the build cursor or a template is shut where it was put.
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

        // The opening is the leaf's own width.
        if (door.Aperture <= 0f && world.Has<ColliderComponent>(entity))
        {
            var size = world.Get<ColliderComponent>(entity).Size;
            door.Aperture = MathF.Max(0.1f, MathF.Max(size.X, size.Z));
        }
        // The doorway is the leaf's pose now, shut: once it opens, its transform no longer says where
        // the hole is.
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
        if (door.PushSide == 0f) door.PushSide = 1f;
        // An automatic leaf runs at its controller's speeds, so it moves as long as its sound says it does.
        if ((DoorKind)door.Kind == DoorKind.AutoSliding)
        {
            float width = HalfWidth(world, entity, door) * 2f;
            door.SwingSeconds = SlidingDoor.AutomaticSeconds(width, opening: true);
            door.CloseSeconds = SlidingDoor.AutomaticSeconds(width, opening: false);
        }
        door.Captured = true;
    }

    /// <summary>
    /// Puts the leaf where its openness says. A hinged leaf turns about its hinged edge (about its
    /// middle, half of it would still block the doorway at ninety degrees); a sliding leaf moves its own
    /// width toward <see cref="DoorComponent.HingeSide"/>.
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
            float swung = shut + door.Openness * door.SwingRadians * SwingSign(door);

            var alongShut = Vector3.Transform(new Vector3(halfWidth * door.HingeSide, 0f, 0f),
                                              Quaternion.CreateFromYawPitchRoll(shut, 0f, 0f));
            var hinge = door.ShutPosition + alongShut;

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
        // So the triangle world places the leaf again before its next answer.
        OpenFPS.Common.MoverPoses.Moved();
    }

    /// <summary>
    /// Which way the leaf turns as it opens, as the sign of its yaw: hinged on the right (+1) and
    /// pushed from its +Z face it turns clockwise seen from above, which sends it toward -Z; pushed
    /// from the other face, the other way, toward +Z.
    /// </summary>
    private static float SwingSign(in DoorComponent door)
        => -door.HingeSide * (door.PushSide < 0f ? -1f : 1f);

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
    private List<(Entity Who, Vector3 At)> People(World world)
    {
        if (_peopleFound) return _people;
        PeopleInto(world, _people);
        _peopleFound = true;
        return _people;
    }

    private static readonly QueryDescription OnFoot =
        new QueryDescription().WithAll<Transform>().WithAny<PlayerComponent, Pedestrian>().WithNone<OccupantComponent, DeadComponent>();

    private static void PeopleInto(World world, List<(Entity Who, Vector3 At)> people)
    {
        people.Clear();
        world.Query(OnFoot, (Entity e, ref Transform t) => people.Add((e, t.Position)));
    }

    // ── Nobody is swept aside ───────────────────────────────────────────────────────────────────

    /// <summary>How much room a person takes up round where they stand, metres: a player's own radius.</summary>
    public const float BodyRadiusMetres = PhysicsConstants.PlayerRadius;

    /// <summary>
    /// The first person in the way of the leaf going from where it is to <paramref name="to"/> (0 shut,
    /// 1 open), or null. <paramref name="hand"/>, whoever moves it by hand, walks with it: never in the
    /// way of a leaf they open, and in the way of one they shut only when standing in its doorway.
    /// </summary>
    public static Entity? InTheWay(World world, Entity entity, float to, Entity? hand = null)
    {
        if (!world.IsAlive(entity) || !world.Has<DoorComponent>(entity)) return null;
        var door = world.Get<DoorComponent>(entity);
        if (hand is { } h && h != Entity.Null) door.HandId = h.Id + 1;
        else door.HandId = 0;
        var people = new List<(Entity, Vector3)>();
        PeopleInto(world, people);
        return FirstInTheWay(world, entity, door, to, people);
    }

    private static Entity? FirstInTheWay(World world, Entity entity, in DoorComponent door, float to,
                                         List<(Entity Who, Vector3 At)> people)
    {
        float from = door.Openness;
        if (to == from || people.Count == 0) return null;
        foreach (var (who, at) in people)
        {
            bool theHand = door.HandId != 0 && who.Id == door.HandId - 1;
            if (theHand)
            {
                if (to < from && Sweeps(world, entity, door, from, MathF.Min(to, door.Target), at, onlyTheDoorway: true)) return who;
                continue;
            }
            if (Sweeps(world, entity, door, from, to, at)) return who;
        }
        return null;
    }

    /// <summary>
    /// Whether somebody at <paramref name="p"/> is within a body's radius of the ground the leaf sweeps
    /// from <paramref name="from"/> to <paramref name="to"/>, on its floor. A hinged leaf sweeps a slice of
    /// disc about its hinge, and only what is ahead of it counts (somebody behind an open leaf is not in
    /// the way of it shutting); a sliding leaf sweeps the strip its leading edge runs along.
    /// <paramref name="onlyTheDoorway"/> asks only about where the leaf ends up.
    /// </summary>
    internal static bool Sweeps(World world, Entity entity, in DoorComponent door, float from, float to, Vector3 p,
                                bool onlyTheDoorway = false)
    {
        Doorway(world, entity, out var centre, out var rotation);
        var d = p - centre;
        if (MathF.Abs(d.Y) > SameFloorMetres) return false;
        var local = Vector3.Transform(d, Quaternion.Inverse(rotation));
        float halfWidth = HalfWidth(world, entity, door);
        float halfThick = world.Has<ColliderComponent>(entity) ? world.Get<ColliderComponent>(entity).Size.Z * 0.5f : 0.03f;
        float reach = BodyRadiusMetres + halfThick;
        // No leaf reaches further from its doorway's middle than three half-widths and a body.
        float far = 3f * halfWidth + reach;
        if (d.X * d.X + d.Z * d.Z > far * far) return false;

        if (door.Slides)
        {
            if (MathF.Abs(local.Z) > reach) return false;
            float c0 = from * 2f * halfWidth * door.HingeSide, c1 = to * 2f * halfWidth * door.HingeSide;
            float lead = MathF.Sign(c1 - c0);
            if (lead == 0f) return false;
            float e0 = c0 + lead * halfWidth, e1 = c1 + lead * halfWidth;
            float lo = MathF.Min(e0, e1) - BodyRadiusMetres, hi = MathF.Max(e0, e1) + BodyRadiusMetres;
            if (onlyTheDoorway) { lo = MathF.Max(lo, -halfWidth); hi = MathF.Min(hi, halfWidth); }
            return local.X >= lo && local.X <= hi;
        }

        // Across the shut leaf from its hinge (u), and out on the side it swings toward (v).
        float width = 2f * halfWidth;
        float u = (local.X - halfWidth * door.HingeSide) * -door.HingeSide;
        float v = local.Z * (door.PushSide < 0f ? 1f : -1f);
        float a0 = from * door.SwingRadians, a1 = to * door.SwingRadians;
        if (SegmentDistance(u, v, a1, width) <= reach) return true;
        if (onlyTheDoorway) return false;
        float r = MathF.Sqrt(u * u + v * v);
        if (r > width + BodyRadiusMetres) return false;
        float phi = MathF.Atan2(v, u);
        return phi >= MathF.Min(a0, a1) && phi <= MathF.Max(a0, a1);
    }

    /// <summary>How far (u, v) is from the leaf lying at <paramref name="angle"/> from its hinge.</summary>
    private static float SegmentDistance(float u, float v, float angle, float length)
    {
        float cx = MathF.Cos(angle), cy = MathF.Sin(angle);
        float along = Math.Clamp(u * cx + v * cy, 0f, length);
        float dx = u - along * cx, dy = v - along * cy;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ── Where a door's sound comes from ─────────────────────────────────────────────────────────

    /// <summary>
    /// How far in from the leaf's free edge its handle and latch are, metres: a lockset's backset, 60
    /// or 70 mm. It also keeps door sounds out of the wall: a leaf laps each jamb by 50 mm, and a sound
    /// on the leaf's edge was inside the brick.
    /// </summary>
    public const float HandleBacksetMetres = 0.07f;

    /// <summary>
    /// How far off the leaf, beyond its half-thickness, a door's sound is placed on the listener's side:
    /// clear of a 30 cm wall's reveal and of the occlusion probe's 10 cm sphere, so it is not heard
    /// through the leaf and the jamb.
    /// </summary>
    public const float FaceStandoffMetres = 0.25f;

    /// <summary>Where the leaf is, centre and turn, at an opening between 0 and 1: what Place puts it at,
    /// in the world.</summary>
    private static (Vector3 Centre, Quaternion Rotation) LeafAt(World world, Entity entity, in DoorComponent door, float openness)
    {
        Doorway(world, entity, out var shutCentre, out var shutRotation);
        float halfWidth = HalfWidth(world, entity, door);
        if (door.Slides)
            return (shutCentre + Vector3.Transform(new Vector3(openness * 2f * halfWidth * door.HingeSide, 0f, 0f), shutRotation),
                    shutRotation);
        var turn = Quaternion.CreateFromYawPitchRoll(openness * door.SwingRadians * SwingSign(door), 0f, 0f);
        var hinge = shutCentre + Vector3.Transform(new Vector3(halfWidth * door.HingeSide, 0f, 0f), shutRotation);
        var rotation = Quaternion.Normalize(turn * shutRotation);
        return (hinge - Vector3.Transform(new Vector3(halfWidth * door.HingeSide, 0f, 0f), rotation), rotation);
    }

    /// <summary>The handle, at an opening: the backset in from the edge away from the hinge (for a slider,
    /// the edge that trails, which stays in the doorway all the way across).</summary>
    private static Vector3 HandleAt(World world, Entity entity, in DoorComponent door, float openness)
    {
        var (centre, rotation) = LeafAt(world, entity, door, openness);
        float halfWidth = HalfWidth(world, entity, door);
        float across = MathF.Max(0f, halfWidth - HandleBacksetMetres) * -door.HingeSide;
        return centre + Vector3.Transform(new Vector3(across, 0f, 0f), rotation);
    }

    /// <summary>The leaf's normal, as long as its sound stands off it (see TransientSound.FaceNormal).</summary>
    private static Vector3 FaceAt(World world, Entity entity, in DoorComponent door, float openness)
    {
        var (_, rotation) = LeafAt(world, entity, door, openness);
        float half = world.Has<ColliderComponent>(entity) ? world.Get<ColliderComponent>(entity).Size.Z * 0.5f : 0.03f;
        return Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation)) * (half + FaceStandoffMetres);
    }

    /// <summary>
    /// A door's sounds placed in its doorway: inside the opening by the handle's backset, in the plane of
    /// the SHUT leaf, and given the leaf's faces to be heard from. Every door sound goes out through here.
    /// The shut leaf, because the leaf has already moved a tick when its opening is announced, and a
    /// slider's handle ends its run behind the far jamb, in the wall.
    /// </summary>
    private static IReadOnlyList<TransientSound> OnTheLeaf(World world, Entity entity, in DoorComponent door,
                                                           IReadOnlyList<TransientSound> sounds)
    {
        if (sounds.Count == 0) return sounds;
        var (centre, rotation) = LeafAt(world, entity, door, 0f);
        var inverse = Quaternion.Inverse(rotation);
        float reach = MathF.Max(0f, HalfWidth(world, entity, door) - HandleBacksetMetres);
        var face = FaceAt(world, entity, door, 0f);
        Vector3 InTheDoorway(Vector3 p)
        {
            var local = Vector3.Transform(p - centre, inverse);
            local.X = Math.Clamp(local.X, -reach, reach);
            local.Z = 0f;
            return centre + Vector3.Transform(local, rotation);
        }
        var placed = new TransientSound[sounds.Count];
        for (int i = 0; i < sounds.Count; i++)
        {
            var s = sounds[i];
            s.Position = InTheDoorway(s.Position);
            if (s.MoveSeconds > 0f) s.MovesTo = InTheDoorway(s.MovesTo);
            s.FaceNormal = face;
            placed[i] = s;
        }
        return placed;
    }

    // ── What it sounds like ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The leaf and its latch leaving the frame and meeting it, from the leaf's material, size,
    /// thickness and latch-edge speed: the generic door model, for kinds without one of their own.
    /// </summary>
    private static IReadOnlyList<TransientSound> Opening(World world, Entity entity, in DoorComponent door)
        => OnTheLeaf(world, entity, door, Sounds(world, entity, door, opening: true, edgeSpeed: 0f));

    private static IReadOnlyList<TransientSound> Closing(World world, Entity entity, in DoorComponent door, float edgeSpeed)
        => OnTheLeaf(world, entity, door, Sounds(world, entity, door, opening: false, edgeSpeed));

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

        float massKg = MathF.Max(2f, size.X * size.Y * size.Z * MathF.Max(100f, material.DensityKgM3));
        float ringThickness = size.Z;

        // A hollow door (1.2 mm steel skins over a honeycomb core) reckoned as a solid slab weighed
        // 2.4 t and slammed 17 dB too hard. Bonded skins bend as a sandwich, so the note is a solid
        // plate's of the same stiffness-to-mass ratio: t = sqrt(6 rho s (d - s)^2 / m).
        if (door.SkinMetres > 0f)
        {
            float skin = MathF.Min(door.SkinMetres, size.Z * 0.5f);
            float area = size.X * size.Y;
            float rho = MathF.Max(100f, material.DensityKgM3);
            float perArea = 2f * skin * rho + MathF.Max(0f, size.Z - 2f * skin) * HollowCoreKgM3;
            massKg = MathF.Max(2f, area * perArea);
            ringThickness = MathF.Sqrt(6f * rho * skin * (size.Z - skin) * (size.Z - skin) / perArea);
        }

        // The material is the only evidence of a seal: metal and glass doors are sealed, wooden ones not.
        bool hasSeal = material.DensityKgM3 > 2000f;

        var sounds = opening
            // Hinges silent: a creak is a fault, and stick-slip on every door was heard as an
            // unexplained hiss either side of the thud. A gate or a cellar door can ask for it.
            ? DoorAcoustics.Opening(material, latchEdge, hinge, size.X, size.Y, ringThickness, massKg,
                                    door.SwingSeconds, hingeDryness: 0f, hasSeal)
            : DoorAcoustics.Closing(material, latchEdge, transform.Position, size.X, size.Y, ringThickness, massKg,
                                    edgeSpeed, hasSeal);

        var transients = new List<TransientSound>(sounds.Count);
        foreach (var sound in sounds) transients.Add(sound.ToTransient());
        return transients;
    }

    private static readonly Random _shutDice = new();

    /// <summary>The push-bar door as a physical model (<see cref="PushBarDoor"/>); its id picks its
    /// character (silencers, bar stops, how its closer is set). <paramref name="pulled"/> is an opening
    /// from the side without the bar: the outside trim's lever drawing the latch and pulling the leaf.</summary>
    private static IReadOnlyList<TransientSound> PushBarSound(World world, Entity entity, in DoorComponent door, bool closing,
                                                              bool pulled = false)
    {
        var size = world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size
            : new Vector3(1.0f, 2.1f, 0.08f);
        int variant = entity.Id;
        return OnTheLeaf(world, entity, door, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock, Position = HandleAt(world, entity, door, 0f), Hz = 300f, Noisiness = 1f,
                LevelDb = closing ? PushBarDoor.CloseLevelDb(variant) : PushBarDoor.OpenLevelDb(variant),
                DecaySeconds = 1.8f,
                SynthKey = PushBarDoor.Key(closing, variant, door.SwingSeconds, size.X, size.Y, pull: pulled),
            },
        });
    }

    /// <summary>The patio and automatic doors as physical models (<see cref="SlidingDoor"/>): each sound is
    /// the whole run, sent as it starts. The door's id picks its character.</summary>
    private static IReadOnlyList<TransientSound> SlidingSound(World world, Entity entity, in DoorComponent door, bool closing)
    {
        var kind = (DoorKind)door.Kind == DoorKind.AutoSliding ? SlidingDoor.Kind.Automatic : SlidingDoor.Kind.Patio;
        var size = world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size
            : new Vector3(kind == SlidingDoor.Kind.Patio ? 0.9f : 1.0f, 2.1f, 0.02f);
        float travel = closing && door.CloseSeconds > 0f ? door.CloseSeconds : door.SwingSeconds;
        int variant = entity.Id;
        // The run is one sound, so it comes from the handle travelling with the leaf.
        float to = closing ? 0f : 1f;
        return OnTheLeaf(world, entity, door, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock, Position = HandleAt(world, entity, door, door.Openness), Hz = 300f, Noisiness = 1f,
                LevelDb = closing ? SlidingDoor.CloseLevelDb(kind, variant) : SlidingDoor.OpenLevelDb(kind, variant),
                DecaySeconds = SlidingDoor.Seconds(kind, closing, travel),
                SynthKey = SlidingDoor.Key(kind, closing, variant, travel, size.X, size.Y),
                MovesTo = HandleAt(world, entity, door, to),
                MoveSeconds = travel * MathF.Abs(to - door.Openness),
            },
        });
    }

    /// <summary>The glass front and pull doors as physical models (<see cref="GlassDoor"/>). How it was opened
    /// (pushed, pulled, pulled with the key held) is the caller's; the door's id picks its character.</summary>
    private static IReadOnlyList<TransientSound> GlassDoorSound(World world, Entity entity, in DoorComponent door, bool closing,
                                                                GlassDoor.Opening how = GlassDoor.Opening.Pull, float delaySeconds = 0f)
    {
        var size = world.Has<ColliderComponent>(entity) ? world.Get<ColliderComponent>(entity).Size : new Vector3(1.0f, 2.1f, 0.012f);
        var kind = (DoorKind)door.Kind == DoorKind.GlassPushBar ? GlassDoor.Kind.PushBar : GlassDoor.Kind.Pull;
        int variant = entity.Id;
        return OnTheLeaf(world, entity, door, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock, Position = HandleAt(world, entity, door, 0f), Hz = 300f, Noisiness = 1f,
                LevelDb = closing ? GlassDoor.CloseLevelDb(kind, variant) : GlassDoor.OpenLevelDb(kind, how),
                DecaySeconds = closing ? 1.2f : door.SwingSeconds + 0.8f,
                DelaySeconds = delaySeconds,
                SynthKey = GlassDoor.Key(kind, closing, how, GlassDoor.Glazing.Tempered, variant, door.SwingSeconds, size.X, size.Y),
            },
        });
    }

    /// <summary>A key unlocking a door from its keyed side (<see cref="LockCylinder"/>), at the lock.</summary>
    private static IReadOnlyList<TransientSound> KeySound(World world, Entity entity, in DoorComponent door)
    {
        var host = (DoorKind)door.Kind switch
        {
            DoorKind.GlassPushBar or DoorKind.GlassPull => LockCylinder.Host.AluminiumStile,
            DoorKind.PushBar => LockCylinder.Host.SteelDoor,
            _ => LockCylinder.Host.WoodDoor,
        };
        return OnTheLeaf(world, entity, door, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock, Position = HandleAt(world, entity, door, 0f), Hz = 2000f, Noisiness = 1f,
                LevelDb = LockCylinder.LevelDb(host), DecaySeconds = LockCylinder.UnlockSeconds + 0.5f,
                SynthKey = LockCylinder.Key(host, entity.Id),
            },
        });
    }

    /// <summary>A lift's landing door leaf as a physical model (<see cref="ElevatorDoor"/>): the whole run, sent
    /// as it starts. Of a bi-parting pair, the leaf with HingeSide 1 carries the operator's motor.</summary>
    private static IReadOnlyList<TransientSound> ElevatorSound(World world, Entity entity, in DoorComponent door, bool closing)
    {
        var size = world.Has<ColliderComponent>(entity) ? world.Get<ColliderComponent>(entity).Size : new Vector3(0.55f, 2.1f, 0.04f);
        float travel = closing && door.CloseSeconds > 0f ? door.CloseSeconds : door.SwingSeconds;
        float to = closing ? 0f : 1f;
        return OnTheLeaf(world, entity, door, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock, Position = HandleAt(world, entity, door, door.Openness), Hz = 300f, Noisiness = 1f,
                LevelDb = closing ? ElevatorDoor.CloseLevelDb(entity.Id) : ElevatorDoor.OpenLevelDb(entity.Id),
                DecaySeconds = ElevatorDoor.Seconds(closing, travel),
                SynthKey = ElevatorDoor.Key(closing, entity.Id, travel, size.X, size.Y, door.HingeSide > 0f),
                MovesTo = HandleAt(world, entity, door, to),
                MoveSeconds = travel * MathF.Abs(to - door.Openness),
            },
        });
    }

    /// <summary>The knob door as a physical model (<see cref="KnobDoor"/>); its id picks its character, so
    /// each door keeps its own hinges. <paramref name="pulled"/>: opened from the pull side (the approved
    /// "grip, turn, pull"); a side nobody knows is the pull too.</summary>
    private static IReadOnlyList<TransientSound> KnobDoorSound(World world, Entity entity, in DoorComponent door, bool closing,
                                                               bool pulled = false)
    {
        var size = world.Has<ColliderComponent>(entity)
            ? world.Get<ColliderComponent>(entity).Size
            : new Vector3(0.9f, 2.1f, 0.05f);
        // Knob doors are interior doors, which are hollow-core. People shut one differently each time.
        var how = !closing ? KnobDoor.Shut.Normal : _shutDice.NextDouble() switch
        {
            < 0.25 => KnobDoor.Shut.Gentle,
            < 0.80 => KnobDoor.Shut.Normal,
            _ => KnobDoor.Shut.Hard,
        };
        bool push = !closing && !pulled && door.OpenedFrom > 0;
        string key = KnobDoor.Key(closing, KnobDoor.Construction.HollowCore, entity.Id, door.SwingSeconds, how, size.X, size.Y, push);
        return OnTheLeaf(world, entity, door, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock, Position = HandleAt(world, entity, door, 0f), Hz = 500f, Noisiness = 1f,
                LevelDb = closing ? KnobDoor.CloseLevelDb(how) : KnobDoor.OpenLevelDb,
                DecaySeconds = closing ? 1.6f : door.SwingSeconds + 0.85f,
                SynthKey = key,
            },
        });
    }

    /// <summary>What fills a hollow door between its skins, kg/m^3: kraft honeycomb or mineral core,
    /// with the edge channels and the lock reinforcement averaged in.</summary>
    private const float HollowCoreKgM3 = 150f;

    // ── Asking a door to move ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks a door to open or shut. False if it is already going that way, or if shutting it would sweep
    /// somebody (<see cref="InTheWay"/> says who).
    ///
    /// <paramref name="by"/> (else <paramref name="who"/>'s position) decides how a hinged door opens:
    /// pushed from <see cref="DoorComponent.PushSide"/>, by its bar if it has one, pulled from the other;
    /// an unknown side is the push side. From <see cref="DoorComponent.KeyedSide"/> a shut door is locked
    /// and the key turns first; everybody has the key until keys are items (docs/DOOR_TYPES_EVENTS.md).
    /// </summary>
    public static bool Set(World world, Entity entity, bool open, Vector3? by = null, Entity? who = null)
    {
        if (!world.Has<DoorComponent>(entity)) return false;
        if (who is { } w && (w == Entity.Null || !world.IsAlive(w))) who = null;
        if (!by.HasValue && who is { } w2 && world.Has<Transform>(w2)) by = world.Get<Transform>(w2).Position;

        var state = world.Get<DoorComponent>(entity);
        float target = open ? 1f : 0f;
        if (state.Target == target) return false;
        if (!open && state.Openness > 0f && InTheWay(world, entity, 0f, who) != null) return false;

        int side = 1;
        bool keyed = false;
        if (by.HasValue)
        {
            Doorway(world, entity, out var centre, out var rotation);
            float through = Vector3.Dot(by.Value - centre, Vector3.Transform(Vector3.UnitZ, rotation));
            side = through * (state.PushSide < 0f ? -1f : 1f) < 0f ? -1 : 1;
            keyed = state.KeyedSide != 0f && through * state.KeyedSide > 0f;
        }

        ref var door = ref world.Get<DoorComponent>(entity);
        door.Target = target;
        door.SelfClosing = false;
        door.ClearSeconds = 0f;
        door.HandId = who is { } hand && OpensByHand(door) ? hand.Id + 1 : 0;
        door.KeySeconds = 0f;
        if (open)
        {
            door.OpenedFrom = door.Slides || door.Powered ? 0 : side;
            if (door.Openness <= 0f)
            {
                door.KeyTurned = keyed;
                if (keyed) door.KeySeconds = KeySequenceSeconds;
            }
        }
        else door.KeyTurned = false;
        return true;
    }

    /// <summary>
    /// What a person opening a door hears said, from how it was opened (after <see cref="Set"/>), with
    /// no full stop: "You unlock the front entrance with your key and pull it open", "You push the bar
    /// and the stair door swings open", "You pull the flat 2A door open", "The patio door slides open".
    /// </summary>
    public static string OpenedPhrase(in DoorComponent door, string name)
    {
        if (door.Slides || door.OpenedFrom == 0) return $"The {name} {Verb(door)} open";
        string hand = door.OpenedFrom < 0 ? "pull" : "push";
        if (door.KeyTurned) return $"You unlock the {name} with your key and {hand} it open";
        if (door.OpenedFrom > 0 && HasBar((DoorKind)door.Kind)) return $"You push the bar and the {name} swings open";
        return $"You {hand} the {name} open";
    }

    /// <summary>What a person trying to shut a door is told when somebody is in the way of it.</summary>
    public static string InTheWayLine(Entity blocker, Entity? who, string name)
        => who is { } w && blocker == w ? $"You are in the way of the {name}. Step out of the doorway first."
                                        : $"Someone is in the way of the {name}.";

    /// <summary>Whether a person moves it by hand: never a motor's door.</summary>
    public static bool OpensByHand(in DoorComponent door) => !door.Powered;

    /// <summary>"swings" or "slides", for saying what a door just did.</summary>
    public static string Verb(in DoorComponent door) => door.Slides ? "slides" : "swings";

    /// <summary>Open far enough to walk through without touching it.</summary>
    public static bool OpenEnough(World world, Entity entity)
        => !world.IsAlive(entity) || !world.Has<DoorComponent>(entity) || world.Get<DoorComponent>(entity).Openness >= 0.9f;

    /// <summary>Forgets a door that no longer exists.</summary>
    public void Forget(int entityId) => _announced.Remove(entityId);
}
