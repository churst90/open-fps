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

    public void Update(WorldSnapshot world, LocalPlayerState state, double now)
    {
        _now = now;
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
            return;
        }
        EnsureSounds();

        var at = car.Transform.Position;
        var forward = Flat(Vector3.Transform(Vector3.UnitZ, car.Transform.Rotation));
        float speed = car.Velocity.Length();

        float halfWidth = 0.95f;
        if (car.Definition.SoundEmitter.SoundId is { } sid && sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
            && MachineRegistry.Knows(sid[7..]))
            halfWidth = MachineRegistry.VehicleFor(sid[7..]).WidthMetres * 0.5f;

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
            Guide(forward * GuideDistance(speed), speed);
            Readout = $"In a junction, heading {Compass(forward)}, {Kmh(speed)}.";
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
        Guide(aim, speed);

        // In line with the road? Signed: positive is pointing to the right of it.
        float offRoadLine = SignedDegrees(dirAlong, forward);
        if (!_aligned && MathF.Abs(offRoadLine) < AlignedDegrees)
        {
            _aligned = true;
            Play(AlignSound, AlignId, forward * 3f, SensorVolume);
        }
        else if (_aligned && MathF.Abs(offRoadLine) > UnalignedDegrees) _aligned = false;

        // Lane assist: pure pursuit of the aim point. Steering angle atan(2 L sin(alpha) / ld),
        // as a fraction of full lock — the same law a driver's hands follow toward a point ahead.
        if (AssistEnabled && speed > 1.5f && MathF.Abs(offRoadLine) < AssistWithinDegrees)
        {
            float wheelbase = 2.6f;
            if (car.Definition.SoundEmitter.SoundId is { } s2 && s2.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)
                && MachineRegistry.Knows(s2[7..]))
            {
                var prof = MachineRegistry.VehicleFor(s2[7..]);
                wheelbase = MathF.Max(1.2f, prof.FrontAxleZ - prof.RearAxleZ);
            }
            float alpha = SignedDegrees(forward, aim) * MathF.PI / 180f;
            float steer = MathF.Atan(2f * wheelbase * MathF.Sin(alpha) / MathF.Max(1f, aim.Length()));
            AssistSteer = Math.Clamp(steer / FullLockRadians, -1f, 1f);
        }

        var (left, rightSide) = LaneGuide.Sides(p, halfWidth);
        Sensor(left, driverRight);
        Sensor(rightSide, driverRight);

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
        Readout = $"{_roadName}, heading {Compass(forward)}, {line}, {lane}, {Kmh(speed)}.";
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
            Say($"{name}, heading {Compass(forward)}.");
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
                    Say($"Junction in {Round(d)} metres. {Exits(world, jRoad, forward, right, _roadName)}");
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

    /// <summary>Full lock at the wheels, radians — the server's DrivingSystem.MaxSteerAngle.</summary>
    private const float FullLockRadians = 0.61f;

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
        if (_lastClickSector != int.MinValue && sector != _lastClickSector)
            Play(TurnSound, TurnBaseId - (_turnIdx++ % Pool), forward * 2f, SensorVolume * 0.7f);
        _lastClickSector = sector;
    }

    // ── What is heard ────────────────────────────────────────────────────────────────────────

    private static float GuideDistance(float speed) => Math.Clamp(8f + speed * 0.8f, 8f, 30f);

    private void Guide(Vector3 offset, float speed)
    {
        if (_now < _nextGuide) return;
        // A beep every 0.6 s crawling, every 0.2 s at motorway speed: the rate is the speed.
        _nextGuide = _now + Math.Clamp(6f / MathF.Max(speed, 0.1f), 0.2f, 0.6f);
        Play(GuideSound, GuideBaseId - (_guideIdx++ % Pool), offset, GuideVolume);
    }

    private void Sensor((float Gap, LaneGuide.Line Kind, float Offset) side, Vector3 driverRight)
    {
        if (side.Kind == LaneGuide.Line.Lane || side.Gap == float.MaxValue) return;
        bool centre = side.Kind == LaneGuide.Line.Centre;
        int toneId = centre ? CentreToneId : KerbToneId;
        var offset = driverRight * side.Offset;

        if (side.Gap <= 0f)
        {
            // Over it: a steady tone, which no beep can be mistaken for.
            Tone(toneId, offset, centre ? 660f : 220f, centre ? SynthWaveType.Sine : SynthWaveType.Triangle);
            return;
        }
        if (_audio.IsPlaying(toneId)) _audio.StopSound(toneId);
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
            Volume = volume,
            MinDistance = 40f,          // no distance attenuation: where it is, not how far
            Range = 80f,
            Essential = true,
            IsEvent = true,
            Type = EmitterType.WorldLocked,
            EnableReverb = false,
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
            Volume = OverToneVolume,
            MinDistance = 40f,
            Range = 80f,
            Essential = true,
            Type = EmitterType.WorldLocked,
            EnableReverb = false,
        };
        if (_audio.IsPlaying(id)) _audio.UpdateSpatialAttributes(e);
        else _audio.PlayPhysicalSoundDirect(e);
    }

    private void StopTones()
    {
        if (_audio.IsPlaying(CentreToneId)) _audio.StopSound(CentreToneId);
        if (_audio.IsPlaying(KerbToneId)) _audio.StopSound(KerbToneId);
    }

    private void Silence() => StopTones();

    // ── The sounds themselves ────────────────────────────────────────────────────────────────

    private void EnsureSounds()
    {
        if (_registered) return;
        int rate = TransientSynth.SampleRate;
        _registered =
            _audio.RegisterSynthesisedSound(GuideSound, TransientSynth.ToPcm16(Beep(rate, 1175f, 0.07f, 0f)), rate)
            & _audio.RegisterSynthesisedSound(CentreSound, TransientSynth.ToPcm16(Beep(rate, 660f, 0.06f, 0.3f)), rate)
            & _audio.RegisterSynthesisedSound(KerbSound, TransientSynth.ToPcm16(Beep(rate, 220f, 0.08f, 0.6f)), rate)
            & _audio.RegisterSynthesisedSound(TurnSound, TransientSynth.ToPcm16(Beep(rate, 1800f, 0.018f, 0f)), rate)
            & _audio.RegisterSynthesisedSound(AlignSound, TransientSynth.ToPcm16(Chime(rate)), rate);
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

    /// <summary>A junction is a square of road nobody named: the box where two carriageways meet.</summary>
    private static bool IsJunction(Vector3 size, string name)
        => (string.IsNullOrWhiteSpace(name) || name == "Road")
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
