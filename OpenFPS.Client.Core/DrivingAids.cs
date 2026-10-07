using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using Serilog;

namespace OpenFPS.Client.Core;

/// <summary>
/// What a driver who cannot see the road needs to hear, and nothing else.
///
/// Three sounds and a voice. Each one means one thing, and each is a different kind of sound so
/// that none of them can be mistaken for another:
///
///   * THE GUIDE — a soft high beep placed on the middle of your lane a little way ahead. Steer
///     towards it: when it is straight in front of you, you are in the middle of your lane and
///     pointing down it. It beeps faster the faster you go, so its rate is your speed.
///   * THE CENTRE LINE — a mid-pitched beep from the side the centre line is on, like a parking
///     sensor: silent until the car is within a metre and a half of it, faster as you close, and a
///     steady tone once you are over it — into the oncoming lane.
///   * THE KERB — the same, low and buzzy, from the kerb side.
///   * THE VOICE — the name of the road when you turn onto one, "junction ahead" with its distance
///     and which ways lead off it, "road ends" before a dead end, and "off the road" when the wheels
///     leave the asphalt.
///
/// All of it comes from the carriageway boxes the map is built of: a named box is a road, and an
/// unnamed square one is a junction. When roads become data the same questions will be asked of
/// the road graph instead.
///
/// The first version of this was a noise tick for every painted dash that went by. It was heard as
/// "popping", told nobody anything, and is gone.
/// </summary>
public sealed class DrivingAids
{
    private readonly AudioEngineFacade _audio;

    /// <summary>Something to say. The session hands this to speech.</summary>
    public event Action<string>? Announce;

    /// <summary>What Z says while driving: road, direction, lane, speed.</summary>
    public string? Readout { get; private set; }

    // Voice ids: a block of their own, well clear of every other pool.
    private const int GuideBaseId = -961000, CentreBaseId = -962000, KerbBaseId = -963000, Pool = 6;
    private const int CentreToneId = -964001, KerbToneId = -964002;
    private int _guideIdx, _centreIdx, _kerbIdx;

    private const string GuideSound = "SYNTH/drive_guide", CentreSound = "SYNTH/drive_centre", KerbSound = "SYNTH/drive_kerb";
    private bool _registered;

    /// <summary>How close to a line, metres from the side of the car, before its sensor starts.</summary>
    private const float SensorRangeMetres = 1.5f;

    private const float GuideVolume = 0.28f, SensorVolume = 0.32f, OverToneVolume = 0.16f;

    /// <summary>
    /// Where the listener's head is. Every cue follows the head, but it still needs a real position
    /// in the world: the voice manager drops anything more than one and a half times its range from
    /// the listener, and a cue left at the default position (the middle of the map) was dropped
    /// everywhere more than 120 m from it — which is most of Main Street. None of them ever played.
    /// </summary>
    private Vector3 _ear;
    private double _now, _nextGuide, _nextCentre, _nextKerb, _nextTrace;
    private string _roadName = "";
    private bool _wasOnRoad;
    private double _offRoadSince = -1;
    private int _announcedJunction = int.MinValue;
    private bool _announcedDeadEnd;
    private bool _toldWayBack;

    // ── How far you have turned ──────────────────────────────────────────────────────────────
    //
    // "It's hard to know how far I'm turning, and I overshoot the lane." A driver who can see
    // watches the road swing round the windscreen; nothing here said how far the car had turned
    // until it was pointing somewhere else. So: a soft click for every fifteen degrees the car
    // turns — count them, six is a right angle — and a rising chime when it comes into line with
    // the road, which is the moment to straighten the wheel.
    private const float TurnClickDegrees = 15f, AlignedDegrees = 4f, UnalignedDegrees = 8f;
    private const int TurnBaseId = -965000, AlignId = -966001;
    private const string TurnSound = "SYNTH/drive_turn", AlignSound = "SYNTH/drive_aligned";
    private int _turnIdx, _lastClickSector = int.MinValue;
    private bool _aligned;

    /// <summary>
    /// Lane assist: while you are roughly in line with the road and not steering, the car steers
    /// itself to the middle of your lane — pure pursuit of the same point the guide beep sits on.
    /// Null when it has nothing to say (off the road, in a junction, turning hard, stopped). K
    /// switches it off and on; it starts on.
    /// </summary>
    public float? AssistSteer { get; private set; }
    public bool AssistEnabled { get; set; } = true;
    /// <summary>How far off the road's line assist will still correct, degrees. Beyond it you are
    /// turning on purpose and it keeps its hands off.</summary>
    private const float AssistWithinDegrees = 35f;

    public DrivingAids(AudioEngineFacade audio) => _audio = audio;

    // ── The road ahead (docs/DRIVING_AIDS.md) ────────────────────────────────────────────────
    //
    // From the map's roads as the server sent them (MapRoads): the line ahead through the next
    // junction, the speed limit, the give-way line, a closed level crossing, the end of the road, and
    // how hard the car will have to brake for whichever of them comes first. Null on a map without roads,
    // where the asphalt boxes answer what they can, as before.
    private DrivingCuePlanner? _planner;

    /// <summary>The last plan, for the readout and the tests. Null when there is none.</summary>
    public DrivingCuePlan? Plan { get; private set; }

    /// <summary>The map's roads arrived (or the map changed): plan from them.</summary>
    public void SetRoads(RoadMapData? roads)
    {
        _planner = roads is { IsEmpty: false } ? new DrivingCuePlanner(roads) : null;
        Plan = null;
        _indicator = 0;
    }

    // ── Indicators ───────────────────────────────────────────────────────────────────────────
    //
    // J and L in the driver's seat. Which way you are going to turn is the one thing about the line
    // ahead the road cannot say, so the planner takes it from the indicator: the guide leads into that
    // turn, and the brake cue is for it. The flasher relay ticks in the dash, on and off at 1.5 Hz (SAE
    // J590: 60 to 120 flashes a minute), and it cancels itself once the car has turned through 45 degrees
    // and straightened, the way a steering column's cam throws it off.
    private int _indicator;
    private double _nextFlash;
    private bool _flashOn;
    private float _indicatorFrom;          // heading when it was switched on, radians
    private float _indicatorMost;          // the most the car has turned since, degrees
    private float _steadyFor;              // seconds the heading has held still
    private float _lastHeading = float.NaN;
    private const float FlashSeconds = 1f / 3f;
    private const float CancelAfterDegrees = 45f;
    private const int RelayId = -966010;
    private const string TickSound = "SYNTH/drive_relay_on", TockSound = "SYNTH/drive_relay_off";

    /// <summary>-1 left, +1 right, 0 off.</summary>
    public int Indicator => _indicator;

    /// <summary>J (-1) or L (+1): that side's indicator on, or off if it already was. What to say.</summary>
    public string ToggleIndicator(int side)
    {
        _indicator = _indicator == side ? 0 : side;
        if (_planner != null) _planner.Indicator = _indicator;
        _indicatorMost = 0f;
        _indicatorFrom = _lastHeading;
        _steadyFor = 0f;
        _nextFlash = 0;
        if (_indicator == 0) { Flash(false); return "Indicator off."; }
        return _indicator < 0 ? "Left indicator." : "Right indicator.";
    }

    // ── How hard to brake ────────────────────────────────────────────────────────────────────
    //
    // A falling note — "come down" — toward whatever the braking is for. Silent while nothing ahead
    // needs more than coasting; then the notes come faster and higher as the braking needed grows
    // toward what the tyres can give (DrivingCueBands): lift, brake, brake hard, and at the limit a
    // run of them so close together they are one sound. On a wet road the tyres give less, so the
    // same corner calls for it sooner.
    private double _nextBrake;
    private int _brakeIdx;
    private const int BrakeBaseId = -967100;
    private static readonly string[] BrakeSounds = { "", "SYNTH/drive_brake_1", "SYNTH/drive_brake_2", "SYNTH/drive_brake_3", "SYNTH/drive_brake_4" };
    private static readonly float[] BrakeHz = { 0f, 330f, 440f, 587f, 784f };
    /// <summary>Seconds between notes at the bottom and top of each band.</summary>
    private static readonly (float Slow, float Fast)[] BrakeEvery = { (1f, 1f), (0.9f, 0.6f), (0.55f, 0.3f), (0.28f, 0.14f), (0.11f, 0.09f) };
    private const float BrakeVolume = 0.3f;

    // ── The speed limit ──────────────────────────────────────────────────────────────────────
    private double _nextOverSpeed;
    private bool _wasOver;
    private const int OverSpeedId = -966011;
    private const string OverSpeedSound = "SYNTH/drive_overspeed";
    /// <summary>Over the limit by this much before it says so, m/s (5 km/h): a speedometer's own error.</summary>
    private const float OverSpeedMargin = 5f / 3.6f;
    private const double OverSpeedRepeat = 10.0;
    private float _announcedLimit = -1f;

    // ── A wheel on a line ────────────────────────────────────────────────────────────────────
    //
    // What a real road does to a car that strays: the tyres run over the raised markers on a centre
    // line, and over the milled rumble strip at the edge. Each is a loop of strikes, played from that
    // side at the front wheel, at a rate that is the speed over the spacing — Botts' dots every 1.2 m,
    // a rumble strip's grooves every 0.3 m. It replaces the steady "over the line" tone.
    private const int CentreRumbleId = -964003, KerbRumbleId = -964004;
    private const string DotsSound = "SYNTH/drive_dots", StripSound = "SYNTH/drive_strip";
    private const float DotSpacing = 1.2f, GrooveSpacing = 0.3f;
    /// <summary>Strikes per second in each loop as it is made: the pitch is the speed's rate over this.</summary>
    private const float DotsLoopHz = 8f, StripLoopHz = 50f;
    private const float RumbleVolume = 0.3f;

    /// <summary>The crossing a driver was last told about, so it is said once.</summary>
    private string _announcedCrossing = "";
    private bool _announcedCrossingClosed;
    private JunctionData? _announcedTurnAt;

    public void Update(WorldSnapshot world, LocalPlayerState state, double now)
    {
        _now = now;
        _ear = state.VisualPosition + new Vector3(0f, state.EyeHeight, 0f);
        if (!state.IsRiding || !state.RidingControls
            || !world.Entities.TryGetValue(state.RidingEntityId, out var car))
        {
            Silence();
            Readout = null;
            // Getting in is a fresh start: say nothing about where the car was parked until it
            // reaches a road, and then say which one.
            _wasOnRoad = false; _roadName = ""; _offRoadSince = -1;
            _announcedJunction = int.MinValue; _announcedDeadEnd = false;
            _lastClickSector = int.MinValue; _aligned = false; AssistSteer = null;
            Plan = null;
            if (_indicator != 0) { _indicator = 0; if (_planner != null) _planner.Indicator = 0; }
            _announcedLimit = -1f; _wasOver = false; _announcedCrossing = ""; _announcedTurnAt = null;
            return;
        }
        EnsureSounds();

        var at = car.Transform.Position;
        var forward = Flat(Vector3.Transform(Vector3.UnitZ, car.Transform.Rotation));
        float speed = car.Velocity.Length();

        float halfWidth = 0.95f;
        VehicleProfile? profile = null;
        if (car.Definition.SoundEmitter.SoundId is { } sid && sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
            && MachineRegistry.Knows(sid[7..]))
        {
            profile = MachineRegistry.VehicleFor(sid[7..]);
            halfWidth = profile.WidthMetres * 0.5f;
        }

        float heading = MathF.Atan2(forward.X, forward.Z);
        Indicators(heading, speed);
        // How fast the car is turning, smoothed over a few updates: with the speed, what the turn it is
        // in already asks of its tyres.
        double since = _now - _lastPlanAt;
        if (!float.IsNaN(_lastHeading) && since > 1e-3 && since < 0.5)
        {
            float rate = MathF.IEEERemainder(heading - _lastHeading, 2f * MathF.PI) / (float)since;
            _yawRate += (rate - _yawRate) * MathF.Min(1f, (float)since / 0.15f);
        }
        _lastPlanAt = _now;
        _lastHeading = heading;
        if (_planner != null && profile != null)
            _planner.MinTurnRadius = MathF.Max(6f, 1.6f * profile.Running.Wheelbase / MathF.Tan(MathF.Max(0.1f, profile.Running.MaxSteerAngleRad)));
        Plan = _planner?.Update(at, forward, speed, AvailableDecel(car, profile, speed),
                                (profile?.LengthMetres ?? 4.4f) * 0.5f, c => CrossingClosed(world, c), _yawRate);
        if (Plan is { Located: false }) Plan = null;
        SpeedLimit(speed);
        BrakeCue(at, speed);
        AheadSpeech();

        TurnClicks(forward);
        AssistSteer = null;

        bool onRoad = TryRoadAt(world, at, forward, out var road, out string name, out bool junction, out int roadId);
        // Looking ahead goes DOWN THE ROAD, not along the bonnet: a car a few degrees off the line
        // would otherwise probe out through the kerb and hear "road ends" on a straight road.
        var ahead = forward;
        if (onRoad && !junction)
        {
            var roadAlong = road.Axes().Along;
            ahead = Vector3.Dot(roadAlong, forward) >= 0f ? roadAlong : -roadAlong;
        }
        Speak(world, at, ahead, new Vector3(ahead.Z, 0f, -ahead.X), speed, onRoad, name, junction, roadId);

        if (!onRoad)
        {
            // Off the asphalt, the guide leads BACK to it: it sits on the nearest road, a couple of
            // metres in from its edge, and the voice says which road and which way. Silence here is
            // how a whole drive was spent crossing open ground at seventy with no idea where any
            // road was.
            StopTones();
            StopRumble();
            string where = "no road nearby";
            if (NearestRoad(world, at, out var point, out string nearName, out float dist))
            {
                Guide(point - at, MathF.Max(speed, 3f));
                where = $"{nearName} {Round(dist)} metres {Bearing(forward, point - at)}";
                if (_offRoadSince >= 0 && _now - _offRoadSince > 0.4 && !_toldWayBack)
                {
                    Say($"The nearest road is {where}. Follow the beep.");
                    _toldWayBack = true;
                }
            }
            Readout = $"Off the road, heading {Compass(forward)}, {Kmh(speed)}. {where}.";
            Trace(at, forward, speed, "off road", 0f, 0, 0f, 0f);
            return;
        }
        _toldWayBack = false;

        if (junction)
        {
            // In the middle of a junction there are no lanes: the guide points the way you are
            // going, and the sensors are quiet until you are on a road again.
            StopTones();
            StopRumble();
            Guide(PlanAim(at) ?? forward * GuideDistance(speed), speed);
            Readout = $"In a junction, heading {Compass(forward)}, {Kmh(speed)}.{AheadWords()}";
            Trace(at, forward, speed, "junction", 0f, 0, 0f, 0f);
            return;
        }

        if (!LaneGuide.Locate(road, at, forward, out var p)) { Silence(); return; }
        var (along, across, _, _) = road.Axes();
        var dirAlong = along * p.Facing;
        var driverRight = across * p.Facing;

        // Which lane you should be in: the one you are in, if it is on your side of the road; the
        // nearest one on your side if you have strayed over the centre.
        float laneWidth = p.Width / p.Lanes;
        float mySide = p.TwoWay ? p.Facing : 0f;             // + is the road's right-hand half
        float target = LaneCentreFor(p, laneWidth, mySide);
        float lookAhead = GuideDistance(speed);
        var aim = dirAlong * lookAhead + driverRight * ((target - p.Across) * p.Facing);
        // The guide on the planned line when there is one: it follows the road's bends and leads into
        // the turn the indicator asks for. Lane assist keeps to the lane (below).
        Guide(PlanAim(at) ?? aim, speed);

        // In line with the road? Signed: positive is pointing to the right of it.
        float offRoadLine = SignedDegrees(dirAlong, forward);
        if (!_aligned && MathF.Abs(offRoadLine) < AlignedDegrees)
        {
            _aligned = true;
            if (DrivingCues.Enabled && DrivingCues.TurnClicks) Play(AlignSound, AlignId, forward * 3f, SensorVolume);
        }
        else if (_aligned && MathF.Abs(offRoadLine) > UnalignedDegrees) _aligned = false;

        // Lane assist: pure pursuit of the aim point. Steering angle atan(2 L sin(alpha) / ld),
        // as a fraction of full lock — the same law a driver's hands follow toward a point ahead.
        if (AssistEnabled && speed > 1.5f && MathF.Abs(offRoadLine) < AssistWithinDegrees)
        {
            // The car's own wheelbase and lock, from its preset's chassis; a car the client cannot
            // name is driven as the default chassis would have it.
            var chassis = car.Definition.SoundEmitter.SoundId is { } s2 && s2.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                          && MachineRegistry.Knows(s2[7..])
                ? MachineRegistry.VehicleFor(s2[7..]).Running
                : null;
            float wheelbase = chassis != null ? MathF.Max(0.5f, chassis.Wheelbase) : DefaultWheelbase;
            float fullLock = chassis?.MaxSteerAngleRad ?? DefaultLockRadians;
            float alpha = SignedDegrees(forward, aim) * MathF.PI / 180f;
            float steer = MathF.Atan(2f * wheelbase * MathF.Sin(alpha) / MathF.Max(1f, aim.Length()));
            AssistSteer = Math.Clamp(steer / fullLock, -1f, 1f);
        }

        var (left, rightSide) = LaneGuide.Sides(p, halfWidth);
        Sensor(left, driverRight, speed, profile);
        Sensor(rightSide, driverRight, speed, profile);

        // Lanes counted from the DRIVER'S left: on a two-way road that is from the centre line out.
        int lanesMySide = p.TwoWay ? p.Lanes / 2 : p.Lanes;
        int laneNumber = Math.Clamp(p.TwoWay
            ? (int)MathF.Floor(MathF.Abs(p.Across) / laneWidth) + 1
            : (int)MathF.Floor((p.Across * p.Facing + p.Width * 0.5f) / laneWidth) + 1, 1, Math.Max(1, lanesMySide));
        bool wrongSide = p.TwoWay && MathF.Sign(p.Across) != MathF.Sign(mySide) && MathF.Abs(p.Across) > 0.3f;
        string lane = wrongSide ? "on the wrong side of the road"
                    : lanesMySide <= 1 ? "in your lane"
                    : laneNumber == lanesMySide ? $"in the right lane of {lanesMySide}"
                    : laneNumber == 1 ? $"in the left lane of {lanesMySide}"
                    : $"in lane {laneNumber} of {lanesMySide} from the left";
        string line = MathF.Abs(offRoadLine) < AlignedDegrees ? "in line with the road"
                    : $"pointing {MathF.Round(MathF.Abs(offRoadLine))} degrees {(offRoadLine > 0 ? "right" : "left")} of the road";
        Readout = $"{_roadName}, heading {Compass(forward)}, {line}, {lane}, {Kmh(speed)}{LimitWords()}.{AheadWords()}";
        Trace(at, forward, speed, _roadName, p.Across, laneNumber, left.Gap, rightSide.Gap);
    }

    // ── What is said ─────────────────────────────────────────────────────────────────────────

    private void Speak(WorldSnapshot world, Vector3 at, Vector3 forward, Vector3 right, float speed,
                       bool onRoad, string name, bool junction, int roadId)
    {
        if (!onRoad)
        {
            if (!_wasOnRoad) return;
            if (_offRoadSince < 0) _offRoadSince = _now;
            if (_wasOnRoad && _now - _offRoadSince > 0.4)
            {
                Say("Off the road.");
                _wasOnRoad = false;
                _roadName = "";
            }
            return;
        }
        _offRoadSince = -1;
        if (!_wasOnRoad && junction) { Say("Back on the road, in a junction."); _wasOnRoad = true; }
        _wasOnRoad = true;

        if (!junction && name != _roadName)
        {
            _roadName = name;
            float limit = Plan?.SpeedLimitMps ?? 0f;
            Say(limit > 0f ? $"{name}, heading {Compass(forward)}, limit {MathF.Round(limit * 3.6f)}." : $"{name}, heading {Compass(forward)}.");
            _announcedLimit = limit;
            _announcedDeadEnd = false;
        }

        // Looking ahead down the road: a junction, or the end of the road. Checked every few metres
        // out to a distance that grows with speed, so there is always three or four seconds' warning.
        if (junction) return;
        float reach = MathF.Max(35f, speed * 4f);
        for (float d = 6f; d <= reach; d += 3f)
        {
            var probe = at + forward * d;
            if (!TryRoadAt(world, probe, forward, out var jRoad, out _, out bool isJunction, out int jId))
            {
                if (!_announcedDeadEnd)
                {
                    Say($"Road ends in {Round(d)} metres.");
                    _announcedDeadEnd = true;
                }
                return;
            }
            if (isJunction && jId != roadId)
            {
                if (jId != _announcedJunction)
                {
                    _announcedJunction = jId;
                    string giveWay = Plan is { GivesWay: true } ? (Plan.StopControl ? ", stop" : ", give way") : "";
                    string turning = TurnWords();
                    if (turning.Length > 0) _announcedTurnAt = Plan?.Junction;
                    Say($"Junction in {Round(d)} metres{giveWay}. {Exits(world, jRoad, forward, right, _roadName)}{turning}");
                }
                return;
            }
        }
    }

    /// <summary>Which ways lead out of a junction, and what they are called, from the way you enter it.</summary>
    private static string Exits(WorldSnapshot world, LaneGuide.Road j, Vector3 forward, Vector3 right, string current)
    {
        float half = MathF.Max(j.Size.X, j.Size.Z) * 0.5f;
        var ways = new List<string>();
        foreach (var (dir, word) in new[] { (-right, "left"), (forward, "straight on"), (right, "right") })
        {
            var beyond = j.Centre + dir * (half + 4f);
            if (TryRoadAt(world, beyond, dir, out _, out string n, out bool alsoJunction, out _) && !alsoJunction)
                ways.Add(string.IsNullOrEmpty(n) || n == current ? word : $"{word} onto {n}");
        }
        return ways.Count == 0 ? "No way through." : $"You can go {string.Join(", ", ways)}.";
    }

    /// <summary>Spoken, and written to the log, so a drive can be read back with what was said in it.</summary>
    private void Say(string text)
    {
        Log.Information("[DRIVE-SAY] {Text}", text);
        Announce?.Invoke(text);
    }

    /// <summary>
    /// The nearest point of road within sixty metres: which road, how far, and where. Junctions
    /// count — they are road — and are named after a road that meets them.
    /// </summary>
    private static bool NearestRoad(WorldSnapshot world, Vector3 at, out Vector3 point, out string name, out float distance)
    {
        point = at; name = ""; distance = float.MaxValue;
        if (world.StaticGrid == null) return false;
        foreach (int eid in world.StaticGrid.GetItemsInRadius(at, 60f))
        {
            if (!world.Entities.TryGetValue(eid, out var e)) continue;
            var def = e.Definition;
            if (!string.Equals(def.Material.Material, "Asphalt", StringComparison.OrdinalIgnoreCase)) continue;
            // The nearest point of the box's footprint, then two metres in from its edge so the beep
            // is ON the road rather than at the kerb.
            var inv = Quaternion.Inverse(e.Transform.Rotation);
            var local = Vector3.Transform(at - e.Transform.Position, inv);
            var half = def.Collider.Size * 0.5f;
            var inset = new Vector3(MathF.Max(0f, half.X - 2f), 0f, MathF.Max(0f, half.Z - 2f));
            var clamped = new Vector3(Math.Clamp(local.X, -inset.X, inset.X), 0f, Math.Clamp(local.Z, -inset.Z, inset.Z));
            var world_ = e.Transform.Position + Vector3.Transform(clamped, e.Transform.Rotation);
            world_.Y = at.Y;
            float d = Vector3.Distance(at, world_);
            if (d >= distance) continue;
            distance = d; point = world_;
            string n = def.Identity.Name ?? "";
            name = IsJunction(def.Collider.Size, n) ? "a junction" : Clean(n);
        }
        return distance < float.MaxValue;
    }

    /// <summary>Where something is from the driver's seat, in words: ahead, ahead left, left, behind...</summary>
    private static string Bearing(Vector3 forward, Vector3 to)
    {
        to.Y = 0f;
        if (to.LengthSquared() < 1e-4f) return "here";
        float angle = MathF.Atan2(Vector3.Cross(forward, Vector3.Normalize(to)).Y, Vector3.Dot(forward, Vector3.Normalize(to))) * 180f / MathF.PI;
        // In this world +X is east and +Z north, so facing north, east (on your right) gives
        // Cross(forward, to).Y = +1. Positive is RIGHT. (Increasing-pitch-looks-down has a sibling.)
        float a = MathF.Abs(angle);
        string side = angle > 0 ? "right" : "left";
        return a < 20f ? "ahead" : a < 70f ? $"ahead to your {side}" : a < 110f ? $"to your {side}" : a < 160f ? $"behind you to the {side}" : "behind you";
    }

    /// <summary>For a car whose preset the client does not know: the wheelbase and lock of the
    /// default chassis (ChassisSpec.Default, on the default profile's axles at 1.25 and -1.35 m).</summary>
    private const float DefaultWheelbase = 2.6f;
    private static readonly float DefaultLockRadians = new ChassisSpec { Axles = Array.Empty<AxleSpec>() }.MaxSteerAngleRad;

    /// <summary>The angle from one direction to another on the ground, degrees; positive is to the right.</summary>
    private static float SignedDegrees(Vector3 from, Vector3 to)
    {
        from.Y = 0f; to.Y = 0f;
        if (from.LengthSquared() < 1e-6f || to.LengthSquared() < 1e-6f) return 0f;
        from = Vector3.Normalize(from); to = Vector3.Normalize(to);
        return MathF.Atan2(Vector3.Cross(from, to).Y, Vector3.Dot(from, to)) * 180f / MathF.PI;
    }

    private void TurnClicks(Vector3 forward)
    {
        float yaw = MathF.Atan2(forward.X, forward.Z) * 180f / MathF.PI;
        int sector = (int)MathF.Floor(yaw / TurnClickDegrees);
        if (_lastClickSector != int.MinValue && sector != _lastClickSector && DrivingCues.Enabled && DrivingCues.TurnClicks)
            Play(TurnSound, TurnBaseId - (_turnIdx++ % Pool), forward * 2f, SensorVolume * 0.7f);
        _lastClickSector = sector;
    }

    // ── What is heard ────────────────────────────────────────────────────────────────────────

    private static float GuideDistance(float speed) => Math.Clamp(8f + speed * 0.8f, 8f, 30f);

    private void Guide(Vector3 offset, float speed)
    {
        if (!DrivingCues.Enabled || !DrivingCues.Guide) return;
        if (_now < _nextGuide) return;
        // A beep every 0.6 s crawling, every 0.2 s at motorway speed: the rate is the speed.
        _nextGuide = _now + Math.Clamp(6f / MathF.Max(speed, 0.1f), 0.2f, 0.6f);
        Play(GuideSound, GuideBaseId - (_guideIdx++ % Pool), offset, GuideVolume);
    }

    private void Sensor((float Gap, LaneGuide.Line Kind, float Offset) side, Vector3 driverRight, float speed, VehicleProfile? profile)
    {
        if (side.Kind == LaneGuide.Line.Lane || side.Gap == float.MaxValue) return;
        bool centre = side.Kind == LaneGuide.Line.Centre;
        int toneId = centre ? CentreToneId : KerbToneId;
        int rumbleId = centre ? CentreRumbleId : KerbRumbleId;
        var offset = driverRight * side.Offset;
        if (!DrivingCues.Enabled || !DrivingCues.LineSensors)
        {
            if (_audio.IsPlaying(toneId)) _audio.StopSound(toneId);
            if (_audio.IsPlaying(rumbleId)) _audio.StopSound(rumbleId);
            return;
        }

        if (side.Gap <= 0f)
        {
            // Over it: the wheels on the markers, from that side at the front wheel — or, standing
            // still where there is nothing to roll over, the steady tone no beep can be mistaken for.
            float rate = speed / (centre ? DotSpacing : GrooveSpacing);
            if (speed > 0.5f)
            {
                if (_audio.IsPlaying(toneId)) _audio.StopSound(toneId);
                // The front wheel on that side: a front overhang of about 0.9 m behind the nose.
                float frontAxle = MathF.Max(0.5f, (profile?.LengthMetres ?? 4.4f) * 0.5f - 0.9f);
                var wheelSide = driverRight * (MathF.Sign(side.Offset) * (profile?.WidthMetres ?? 1.8f) * 0.5f);
                Rumble(rumbleId, centre ? DotsSound : StripSound, wheelSide + ForwardOf(driverRight) * frontAxle,
                       rate / (centre ? DotsLoopHz : StripLoopHz));
            }
            else
            {
                if (_audio.IsPlaying(rumbleId)) _audio.StopSound(rumbleId);
                Tone(toneId, offset, centre ? 660f : 220f, centre ? SynthWaveType.Sine : SynthWaveType.Triangle);
            }
            return;
        }
        if (_audio.IsPlaying(toneId)) _audio.StopSound(toneId);
        if (_audio.IsPlaying(rumbleId)) _audio.StopSound(rumbleId);
        if (side.Gap > SensorRangeMetres) return;

        ref double next = ref centre ? ref _nextCentre : ref _nextKerb;
        if (_now < next) return;
        float closeness = 1f - side.Gap / SensorRangeMetres;             // 0 far .. 1 touching
        next = _now + 0.6 - 0.5 * closeness;                             // 0.6 s down to 0.1 s
        if (centre) Play(CentreSound, CentreBaseId - (_centreIdx++ % Pool), offset, SensorVolume);
        else Play(KerbSound, KerbBaseId - (_kerbIdx++ % Pool), offset, SensorVolume);
    }

    private void Play(string sound, int id, Vector3 offset, float volume)
    {
        if (!_registered) return;
        _audio.Submit(new SpatialEmitter
        {
            EntityId = id,
            SoundId = sound,
            Mode = OpenFPS.Common.Components.PlaybackMode.Single,
            FollowsListener = true,
            ListenerOffset = offset,
            Position = _ear + offset,
            Volume = volume,
            MinDistance = 40f,          // no distance attenuation: where it is, not how far
            Range = 80f,
            Essential = true,
            IsEvent = true,
            Type = EmitterType.UI,           // in the head: dry, panned, not out on the road
        });
    }

    private void Tone(int id, Vector3 offset, float hz, SynthWaveType wave)
    {
        var e = new SpatialEmitter
        {
            EntityId = id,
            SoundId = "SYNTH",
            IsSynth = true,
            SynthWave = wave,
            SynthFrequency = hz,
            SynthFilterCutoff = 1f,
            FollowsListener = true,
            ListenerOffset = offset,
            Position = _ear + offset,
            Volume = OverToneVolume,
            MinDistance = 40f,
            Range = 80f,
            Essential = true,
            Type = EmitterType.UI,           // in the head: dry, panned, not out on the road
        };
        if (_audio.IsPlaying(id)) _audio.UpdateSpatialAttributes(e);
        else _audio.PlayPhysicalSoundDirect(e);
    }

    private void StopTones()
    {
        if (_audio.IsPlaying(CentreToneId)) _audio.StopSound(CentreToneId);
        if (_audio.IsPlaying(KerbToneId)) _audio.StopSound(KerbToneId);
    }

    private void Silence()
    {
        StopTones();
        StopRumble();
    }

    private void StopRumble()
    {
        if (_audio.IsPlaying(CentreRumbleId)) _audio.StopSound(CentreRumbleId);
        if (_audio.IsPlaying(KerbRumbleId)) _audio.StopSound(KerbRumbleId);
    }

    /// <summary>The way a car faces, from the direction to its right.</summary>
    private static Vector3 ForwardOf(Vector3 right) => new(-right.Z, 0f, right.X);

    /// <summary>A loop of strikes from one side, at the rate the speed rolls them.</summary>
    private void Rumble(int id, string sound, Vector3 offset, float pitch)
    {
        if (!_registered) return;
        var e = new SpatialEmitter
        {
            EntityId = id,
            SoundId = sound,
            Mode = OpenFPS.Common.Components.PlaybackMode.LoopOne,
            FollowsListener = true,
            ListenerOffset = offset,
            Position = _ear + offset,
            Volume = RumbleVolume,
            Pitch = Math.Clamp(pitch, 0.1f, 4f),
            MinDistance = 40f,
            Range = 80f,
            Essential = true,
            Type = EmitterType.UI,
        };
        if (_audio.IsPlaying(id)) _audio.UpdateSpatialAttributes(e);
        else _audio.Submit(e);
    }

    // ── The road ahead ───────────────────────────────────────────────────────────────────────

    /// <summary>Where the guide sits on the planned line, from the listener's car; null without a plan.</summary>
    private Vector3? PlanAim(Vector3 at)
    {
        if (Plan is not { Located: true } plan || plan.Path.Count < 2) return null;
        var d = plan.GuidePoint - at;
        d.Y = 0f;
        return d.LengthSquared() < 1f ? null : d;
    }

    /// <summary>
    /// What the tyres can give on this road now, m/s²: the car's peak grip, less what the water under
    /// its wheels takes (RoadWaterLaw.GripFactor), the worst wheel. Without wheels on the wire, dry.
    /// </summary>
    private static float AvailableDecel(EntitySnapshot car, VehicleProfile? profile, float speed)
    {
        float grip = (profile?.Tyres.PeakGripG ?? 0.95f) * WheelDynamics.G;
        float factor = 1f;
        if (car.Wheels is { Length: > 0 } wheels && profile != null)
            foreach (var w in wheels)
                factor = MathF.Min(factor, RoadWaterLaw.GripFactor(w.Surface, w.WaterMm, speed,
                                                                     profile.Tyres.InflationKPa, profile.Tyres.TreadDepthMm));
        return grip * factor;
    }

    /// <summary>Whether a crossing's bells are ringing: its bell is an emitter above the crossing whose
    /// SynthRunning the server sets while it is closed (CrossingSystem).</summary>
    private static bool CrossingClosed(WorldSnapshot world, CrossingRails c)
    {
        if (world.StaticGrid == null) return false;
        foreach (int id in world.StaticGrid.GetItemsInRadius(c.Centre, 12f))
        {
            if (!world.Entities.TryGetValue(id, out var e)) continue;
            var em = e.Definition.SoundEmitter;
            if (em.SoundId != null && em.SoundId.StartsWith("bell:", StringComparison.OrdinalIgnoreCase)
                && Vector3.DistanceSquared(new Vector3(e.Transform.Position.X, 0f, e.Transform.Position.Z),
                                           new Vector3(c.Centre.X, 0f, c.Centre.Z)) < 100f)
                return em.SynthRunning;
        }
        return false;
    }

    /// <summary>The flasher, and the indicator throwing itself off after the turn.</summary>
    private void Indicators(float heading, float speed)
    {
        if (_indicator == 0) return;
        if (float.IsNaN(_indicatorFrom)) _indicatorFrom = heading;
        float turned = MathF.IEEERemainder(heading - _indicatorFrom, 2f * MathF.PI) * 180f / MathF.PI;
        _indicatorMost = MathF.Max(_indicatorMost, MathF.Abs(turned));
        float dt = (float)Math.Clamp(_now - _lastIndicatorAt, 0.0, 0.25);
        _lastIndicatorAt = _now;
        if (!float.IsNaN(_lastHeading) && dt > 0f)
        {
            float rate = MathF.Abs(MathF.IEEERemainder(heading - _lastHeading, 2f * MathF.PI)) * 180f / MathF.PI / dt;
            _steadyFor = rate < 6f ? _steadyFor + dt : 0f;
        }
        // Through the turn and straight again: the column throws it off.
        if (_indicatorMost >= CancelAfterDegrees && _steadyFor > 0.6f && speed > 0.5f)
        {
            _indicator = 0;
            if (_planner != null) _planner.Indicator = 0;
            Flash(false);
            Log.Information("[DRIVE] indicator cancelled after {Deg:F0} degrees", _indicatorMost);
            return;
        }
        if (_now < _nextFlash) return;
        _nextFlash = _now + FlashSeconds;
        _flashOn = !_flashOn;
        Flash(_flashOn);
    }
    private double _lastIndicatorAt;
    private double _lastPlanAt;
    private float _yawRate;

    /// <summary>The relay closing (tick) or opening (tock), in the dash a little ahead.</summary>
    private void Flash(bool on)
    {
        if (!_registered) return;
        if (!on) _flashOn = false;
        float h = float.IsNaN(_lastHeading) ? 0f : _lastHeading;
        var ahead = Vector3.Transform(new Vector3(0f, -0.35f, 0.6f), Quaternion.CreateFromYawPitchRoll(h, 0f, 0f));
        Play(on ? TickSound : TockSound, RelayId, ahead, 0.22f);
    }

    /// <summary>The limit when you join a road with a different one, and the two notes when over it.</summary>
    private void SpeedLimit(float speed)
    {
        if (Plan is not { SpeedLimitMps: > 0f } plan) { _wasOver = false; return; }
        if (_announcedLimit >= 0f && MathF.Abs(plan.SpeedLimitMps - _announcedLimit) > 0.5f && !plan.InJunction)
        {
            Say($"Limit {MathF.Round(plan.SpeedLimitMps * 3.6f)}.");
            _announcedLimit = plan.SpeedLimitMps;
        }
        bool over = speed > plan.SpeedLimitMps + OverSpeedMargin;
        if (over && (!_wasOver || _now >= _nextOverSpeed))
        {
            _nextOverSpeed = _now + OverSpeedRepeat;
            if (DrivingCues.Enabled && DrivingCues.SpeedWarning)
                Play(OverSpeedSound, OverSpeedId, Vector3.Transform(new Vector3(0f, -0.2f, 0.7f), Quaternion.CreateFromYawPitchRoll(_lastHeading, 0f, 0f)), 0.2f);
            if (!_wasOver) Log.Information("[DRIVE] over the limit: {Kmh:F0} in a {Limit:F0}", speed * 3.6f, plan.SpeedLimitMps * 3.6f);
        }
        _wasOver = over;
    }

    /// <summary>The brake cue: toward what the braking is for, faster and higher as it nears the grip.</summary>
    private void BrakeCue(Vector3 at, float speed)
    {
        if (Plan is not { } plan || !DrivingCues.Enabled || !DrivingCues.BrakeCue || speed < 1f) return;
        float ratio = plan.BrakeRatio;
        int band = DrivingCueBands.Of(ratio);
        if (band == 0 || _now < _nextBrake) return;
        float lo = band switch { 1 => DrivingCueBands.Lift, 2 => DrivingCueBands.Brake, 3 => DrivingCueBands.Hard, _ => DrivingCueBands.Limit };
        float hi = band switch { 1 => DrivingCueBands.Brake, 2 => DrivingCueBands.Hard, 3 => DrivingCueBands.Limit, _ => 1.2f };
        float f = Math.Clamp((ratio - lo) / (hi - lo), 0f, 1f);
        var (slow, fast) = BrakeEvery[band];
        _nextBrake = _now + slow + (fast - slow) * f;
        // Toward the hazard, at arm's length: where, not how far.
        var toward = plan.HazardPoint - at;
        toward.Y = 0f;
        var dir = toward.LengthSquared() > 1f ? Vector3.Normalize(toward) : Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(_lastHeading, 0f, 0f));
        Play(BrakeSounds[band], BrakeBaseId - (_brakeIdx++ % Pool), dir * 2.5f, BrakeVolume);
        TracePlan(plan, speed);
    }

    private double _nextPlanTrace;
    private void TracePlan(DrivingCuePlan plan, float speed)
    {
        if (_now < _nextPlanTrace) return;
        _nextPlanTrace = _now + 1.0;
        Log.Information("[DRIVE-CUE] {Kmh:F0} km/h, brake {Ratio:F2} ({Band}) for {Hazard} at {D:F0} m to {To:F0} km/h, need {Need:F1} of {Have:F1} m/s2",
                        speed * 3.6f, plan.BrakeRatio, DrivingCueBands.Of(plan.BrakeRatio), plan.Hazard, plan.HazardDistance,
                        plan.HazardSpeed * 3.6f, plan.NeededDecel, plan.AvailableDecel);
    }

    /// <summary>A level crossing on the line ahead: said once, and again if it closes as you come.</summary>
    private void AheadSpeech()
    {
        if (Plan is not { } plan) return;
        if (plan.Crossing is { } c && plan.CrossingDistance < MathF.Max(40f, plan.Path.Count * 0.8f))
        {
            if (c.Name != _announcedCrossing || (plan.CrossingClosed && !_announcedCrossingClosed))
            {
                Say(plan.CrossingClosed
                    ? $"Level crossing in {Round(plan.CrossingDistance)} metres, closed. Stop before it."
                    : $"Level crossing in {Round(plan.CrossingDistance)} metres.");
                _announcedCrossing = c.Name;
                _announcedCrossingClosed = plan.CrossingClosed;
            }
        }
        else if (plan.Crossing == null && _announcedCrossing.Length > 0 && !plan.InJunction)
        {
            _announcedCrossing = "";
            _announcedCrossingClosed = false;
        }
        // The indicator set after the junction was announced: say which way it now goes.
        if (_indicator != 0 && plan.Junction != null && !ReferenceEquals(plan.Junction, _announcedTurnAt)
            && plan.NextTurn is Turn.Left or Turn.Right && plan.JunctionDistance < 120f)
        {
            _announcedTurnAt = plan.Junction;
            Say($"Turning {(plan.NextTurn == Turn.Left ? "left" : "right")} onto {plan.NextRoad}.");
        }
    }

    private string LimitWords()
        => Plan is { SpeedLimitMps: > 0f } p ? $", limit {MathF.Round(p.SpeedLimitMps * 3.6f)}" : "";

    /// <summary>What the line ahead holds, for Z: the next junction and the way through it, a crossing.</summary>
    private string AheadWords()
    {
        if (Plan is not { } p) return "";
        var parts = new List<string>();
        if (p.Junction != null && p.JunctionDistance < 250f)
        {
            string way = p.NextTurn switch { Turn.Left => $", turning left onto {p.NextRoad}", Turn.Right => $", turning right onto {p.NextRoad}", _ => "" };
            parts.Add($"Junction in {Round(p.JunctionDistance)} metres{(p.GivesWay ? ", give way" : "")}{way}");
        }
        if (p.Crossing != null)
            parts.Add($"Level crossing in {Round(p.CrossingDistance)} metres{(p.CrossingClosed ? ", closed" : "")}");
        if (_indicator != 0) parts.Add(_indicator < 0 ? "Left indicator on" : "Right indicator on");
        return parts.Count == 0 ? "" : " " + string.Join(". ", parts) + ".";
    }

    private string TurnWords()
        => _indicator != 0 && Plan?.NextTurn is Turn.Left or Turn.Right
            ? $" Turning {(Plan!.NextTurn == Turn.Left ? "left" : "right")} onto {Plan.NextRoad}."
            : "";

    // ── The sounds themselves ────────────────────────────────────────────────────────────────

    private void EnsureSounds()
    {
        if (_registered) return;
        int rate = TransientSynth.SampleRate;
        _registered =
            _audio.RegisterSynthesisedSoundFloat(GuideSound, Beep(rate, 1175f, 0.07f, 0f), rate)
            & _audio.RegisterSynthesisedSoundFloat(CentreSound, Beep(rate, 660f, 0.06f, 0.3f), rate)
            & _audio.RegisterSynthesisedSoundFloat(KerbSound, Beep(rate, 220f, 0.08f, 0.6f), rate)
            & _audio.RegisterSynthesisedSoundFloat(TurnSound, Beep(rate, 1800f, 0.018f, 0f), rate)
            & _audio.RegisterSynthesisedSoundFloat(AlignSound, Chime(rate), rate)
            & _audio.RegisterSynthesisedSoundFloat(TickSound, DrivingCueSounds.Relay(rate, closing: true), rate)
            & _audio.RegisterSynthesisedSoundFloat(TockSound, DrivingCueSounds.Relay(rate, closing: false), rate)
            & _audio.RegisterSynthesisedSoundFloat(OverSpeedSound, DrivingCueSounds.OverSpeed(rate), rate)
            & _audio.RegisterSynthesisedSoundFloat(DotsSound, DrivingCueSounds.Dots(rate, DotsLoopHz), rate)
            & _audio.RegisterSynthesisedSoundFloat(StripSound, DrivingCueSounds.Strip(rate, StripLoopHz), rate);
        for (int b = 1; b < BrakeSounds.Length; b++)
            _registered &= _audio.RegisterSynthesisedSoundFloat(BrakeSounds[b], DrivingCueSounds.Brake(rate, BrakeHz[b], b), rate);
    }

    /// <summary>A beep: a tone with soft edges so it does not click, and some odd harmonics to make it
    /// buzzier — none for the guide, a little for the centre line, a lot for the kerb.</summary>
    internal static float[] Beep(int rate, float hz, float seconds, float buzz)
    {
        int n = (int)(rate * seconds);
        var buf = new float[n];
        int edge = Math.Max(1, (int)(rate * 0.006f));
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, w = MathF.Tau * hz * t;
            float s = MathF.Sin(w) + buzz * (MathF.Sin(3f * w) / 3f + MathF.Sin(5f * w) / 5f);
            float env = MathF.Min(1f, MathF.Min(i, n - 1 - i) / (float)edge);
            buf[i] = 0.6f * s * env / (1f + buzz * 0.5f);
        }
        return buf;
    }

    /// <summary>Two notes going up, a fifth apart: "there".</summary>
    private static float[] Chime(int rate)
    {
        var a = Beep(rate, 880f, 0.07f, 0f);
        var b = Beep(rate, 1320f, 0.09f, 0f);
        var both = new float[a.Length + b.Length];
        a.CopyTo(both, 0);
        b.CopyTo(both, a.Length);
        return both;
    }

    // ── Finding the road ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The asphalt at a point: the road, its name, and whether it is a junction. Where a carriageway
    /// and a junction overlap, the junction wins — it is the part of the road with no lanes — and
    /// where two carriageways overlap, the one running the way you are pointing.
    /// </summary>
    private static bool TryRoadAt(WorldSnapshot world, Vector3 at, Vector3 forward, out LaneGuide.Road road,
                                  out string name, out bool junction, out int id)
    {
        road = default; name = ""; junction = false; id = int.MinValue;
        if (world.StaticGrid == null) return false;
        float bestScore = -1f;
        foreach (int eid in world.StaticGrid.GetItemsInRadius(at, 30f))
        {
            if (!world.Entities.TryGetValue(eid, out var e)) continue;
            var def = e.Definition;
            if (!string.Equals(def.Material.Material, "Asphalt", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = new LaneGuide.Road(e.Transform.Position, def.Collider.Size, e.Transform.Rotation);
            if (!LaneGuide.Locate(candidate, at, forward, out _)) continue;
            string n = def.Identity.Name ?? "";
            bool isJunction = IsJunction(def.Collider.Size, n);
            float score = (isJunction ? 2f : 0f)
                        + MathF.Abs(Vector3.Dot(candidate.Axes().Along, forward));
            if (score > bestScore)
            {
                bestScore = score; road = candidate; id = eid; junction = isJunction;
                name = isJunction ? "" : Clean(n);
            }
        }
        return id != int.MinValue;
    }

    /// <summary>A junction is a square of road named as one ("Central Street and Main Street junction"),
    /// or that nobody named at all: the box where two carriageways meet.</summary>
    private static bool IsJunction(Vector3 size, string name)
        => (string.IsNullOrWhiteSpace(name) || name == "Road" || name.EndsWith(" junction", StringComparison.OrdinalIgnoreCase))
        && MathF.Abs(size.X - size.Z) < 0.2f * MathF.Max(size.X, size.Z);

    private static string Clean(string n)
    {
        if (string.IsNullOrWhiteSpace(n) || n == "Road") return "the road";
        return n.EndsWith(" carriageway", StringComparison.OrdinalIgnoreCase) ? n[..^" carriageway".Length] : n;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The middle of the lane to aim for, across the road.</summary>
    private static float LaneCentreFor(LaneGuide.Position p, float laneWidth, float mySide)
    {
        if (!p.TwoWay)
        {
            float from = -p.Width * 0.5f;
            int idx = (int)Math.Clamp(MathF.Floor((p.Across - from) / laneWidth), 0, p.Lanes - 1);
            return from + (idx + 0.5f) * laneWidth;
        }
        int perSide = p.Lanes / 2;
        float onMySide = p.Across * mySide;                                  // + = on my side
        int lane = (int)Math.Clamp(MathF.Floor(MathF.Max(0f, onMySide) / laneWidth), 0, perSide - 1);
        return mySide * (lane + 0.5f) * laneWidth;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.Y = 0f;
        return v.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(v);
    }

    private static string Compass(Vector3 f)
    {
        float deg = MathF.Atan2(f.X, f.Z) * 180f / MathF.PI;
        if (deg < 0) deg += 360f;
        string[] names = { "north", "north east", "east", "south east", "south", "south west", "west", "north west" };
        return names[(int)MathF.Round(deg / 45f) % 8];
    }

    private static string Kmh(float speed) => speed < 0.5f ? "stopped" : $"{MathF.Round(speed * 3.6f)} kilometres an hour";

    private static int Round(float metres) => (int)(MathF.Round(metres / 5f) * 5f);

    /// <summary>Once a second, what the driver was doing — so a drive can be read back afterwards.</summary>
    private void Trace(Vector3 at, Vector3 forward, float speed, string road, float across, int lane, float gapLeft, float gapRight)
    {
        if (_now < _nextTrace) return;
        _nextTrace = _now + 1.0;
        Log.Information("[DRIVE] at ({X:F1}, {Z:F1}) heading {H} {Kmh:F0} km/h on {Road} across {A:F1} lane {L} gaps L {GL:F1} R {GR:F1}",
                        at.X, at.Z, Compass(forward), speed * 3.6f, road, across, lane,
                        gapLeft == float.MaxValue ? -99f : gapLeft, gapRight == float.MaxValue ? -99f : gapRight);
    }
}
