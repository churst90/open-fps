using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// J, K, L and O are your eyes, and so are W, A, S and D (Cody, 2026-10-04): "when I strafe sideways or
/// walk forward, when my line of sight changes, also narrate what changed ... if a vehicle passes in
/// front I should know about it ... if I'm moving sideways and I'm looking for the stairs to go to the
/// next level I need to hear them."
///
/// Three things are said, and only when they CHANGE:
///
/// <b>After you look somewhere</b> — a turn with J or L, a look up or down with K or O — once your
/// head has settled (<see cref="TurnNarration"/>): what is ahead and how far, or which way is open.
/// Looking down at the floor says nothing: the floor is never named, and level is where "open" means
/// something.
///
/// <b>When what is ahead changes for any other reason</b> — you walked, strafed, a door was opened, a
/// car pulled up in front — the new thing and its distance, once it has held for
/// <see cref="SightChanges.StableSeconds"/>. The same thing coming nearer is said again only as it
/// crosses 10, 5 and 2 metres (<see cref="SightChanges.Steps"/>), as the distance alone when nothing
/// else has been said since. Something said in the last <see cref="SightChanges.RecentSeconds"/> is
/// not said again on coming back into view unless it is nearer by a step than when it was said, so
/// strafing along a corridor names each door once and not the wall between every two of them.
///
/// <b>Something crossing in front</b> — a car, a pedestrian, another player, moving faster than a
/// stroll, crossing your line of sight within twenty metres and not behind a wall:
/// "Hatchback passing, left to right, 6 metres" (<see cref="PassingWatch"/>).
///
/// What "ahead" is: the line straight ahead, level, at knee, chest and eye height, plus a cone of
/// <see cref="SightCone.ConeDegrees"/> either side in which only DOORWAYS AND STAIRS are looked for —
/// the places you are walking to find. They win over the thing on the centre line unless that is
/// nearer than they are by more than <see cref="SightCone.LandmarkLeadMetres"/>; so a door in the
/// wall ahead is said rather than the wall round it, and an open doorway is said rather than the far
/// wall seen through it. Nothing else is looked for in the cone: in a corridor it would find the side
/// walls and say them for ever.
///
/// Never a flood: lines from walking and passing are at least <see cref="MinGapSeconds"/> apart, the
/// newest waiting line wins, and it cuts off the stale one — unless something else (a chat line, a
/// stair cue) was said since, which is left to finish.
/// </summary>
public sealed class SightWatch
{
    /// <summary>How often the sight is looked along while nothing is turning: ten times a second.</summary>
    public const double SampleSeconds = 0.1;
    /// <summary>The least time between two lines that come from walking or from something passing.</summary>
    public const double MinGapSeconds = 1.0;
    /// <summary>A move further than this in one tick is a teleport or a spawn, not a walk: what is
    /// ahead after it is taken in silently, like after a reset.</summary>
    public const float JumpMetres = 3f;

    public enum Act { None, Interrupt, Say }

    /// <summary>What to do this tick: nothing, cut the narration off, or say <see cref="Line"/>
    /// (<see cref="Cause"/>: turn, look, move or pass), cutting off what is talking when
    /// <see cref="Interrupt"/>.</summary>
    public readonly record struct Result(Act Act, string? Line = null, string Cause = "", bool Interrupt = true);

    /// <summary>Where the body is and where it faces.</summary>
    public readonly record struct Pose(Vector3 Feet, float Yaw, float Pitch, float EyeHeight);

    private readonly TurnNarration _turn = new();
    private readonly SightChanges _changes = new();
    private readonly PassingWatch _passing = new();
    private readonly SightIndex _index = new();
    private double _nextSampleAt = double.NegativeInfinity;
    private Vector3? _lastFeet;

    private (string Key, string Name, float Distance, bool Step, string Cause)? _pending;
    private double _lastLineAt = double.NegativeInfinity;
    private string? _lastKey;

    /// <summary>The last line said, for telling whether it is still the last thing said.</summary>
    public string? LastLine { get; private set; }

    /// <summary>
    /// One simulation tick. <paramref name="looking"/>: your own input turned or tilted your head this
    /// tick, or a look key is still down. <paramref name="cardinal"/>: the way you face, in words, for
    /// "Open, North". <paramref name="ourLineIsLast"/>: nothing has been said since this said
    /// <see cref="LastLine"/>, so cutting it off cuts nothing else.
    /// </summary>
    public Result Update(WorldSnapshot world, Pose pose, int ownEntityId, double now,
                         bool looking, string cardinal, bool ourLineIsLast)
    {
        if (_lastFeet is { } was && Vector3.DistanceSquared(was, pose.Feet) > JumpMetres * JumpMetres) Reset();
        _lastFeet = pose.Feet;
        _index.Refresh(world, now);
        bool level = SightCone.IsLevel(pose.Pitch);

        switch (_turn.Update(now, looking, ourLineIsLast))
        {
            case TurnNarration.Step.Interrupt:
                _pending = null;
                _passing.Forget();
                return new Result(Act.Interrupt);
            case TurnNarration.Step.Narrate:
            {
                _passing.Forget();
                _nextSampleAt = now + SampleSeconds;
                var seen = SightCone.Look(_index, world, pose.Feet, pose.Yaw, pose.Pitch, pose.EyeHeight, ownEntityId);
                // Looking down at the floor, or up at the sky: nothing, and nothing to say.
                if (seen is null && !level) { _changes.Forget(); return default; }
                string key = KeyOf(seen);
                float d = seen?.Distance ?? 0f;
                _changes.Adopt(key, d);
                string line = Sightline.NarrationLine(seen, cardinal);
                if (!_turn.Accept(line, now)) return default;
                return Emit(now, key, line, d, level ? "turn" : "look", interrupt: true, track: true);
            }
        }
        if (_turn.Pending) { _passing.Forget(); return default; }

        if (now >= _nextSampleAt)
        {
            _nextSampleAt = now + SampleSeconds;
            var seen = SightCone.Look(_index, world, pose.Feet, pose.Yaw, pose.Pitch, pose.EyeHeight, ownEntityId);
            if (seen is not null || level)
            {
                string key = KeyOf(seen);
                float d = seen?.Distance ?? 0f;
                if (_changes.Observe(now, key, d) is { } change)
                    _pending = (key, seen?.Name ?? "", d, change == SightChanges.Change.Nearer, "move");
            }
            var eye = pose.Feet + new Vector3(0, pose.EyeHeight, 0);
            if (_passing.Update(_index, world, eye, pose.Feet.Y, pose.Yaw, ownEntityId, now) is { } passing)
                _pending = ("", passing, 0f, false, "pass");
        }

        if (_pending is { } p && now - _lastLineAt >= MinGapSeconds)
        {
            _pending = null;
            string line;
            if (p.Cause == "pass") line = p.Name;
            else if (p.Key.Length == 0) line = $"Open, {cardinal}";
            else if (p.Step && ourLineIsLast && _lastKey == p.Key) line = Sightline.SpokenDistance(p.Distance);
            else line = $"{p.Name}, {Sightline.SpokenDistance(p.Distance)}";
            _turn.Accept(line, now);
            return Emit(now, p.Key, line, p.Distance, p.Cause, interrupt: ourLineIsLast, track: p.Cause != "pass");
        }
        return default;
    }

    private Result Emit(double now, string key, string line, float distance, string cause, bool interrupt, bool track)
    {
        _lastLineAt = now;
        LastLine = line;
        _lastKey = track ? key : null;
        if (track) _changes.Remember(now, key, distance);
        return new Result(Act.Say, line, cause, interrupt);
    }

    /// <summary>What a sighting is the same as: its name, so two panels of one wall are one wall; ""
    /// for open.</summary>
    private static string KeyOf(Sightline.Sighting? seen) => seen?.Name ?? "";

    /// <summary>Forget what was ahead and any turn in progress: a spawn, a teleport, a seat, a menu,
    /// the narration switched. What is ahead afterwards is taken in silently.</summary>
    public void Reset()
    {
        _turn.Reset();
        _changes.Forget();
        _passing.Forget();
        _pending = null;
    }
}

/// <summary>
/// Whether what is ahead has changed, and whether that is worth a line. See <see cref="SightWatch"/>.
/// Keyed by the NAME of what is seen, so the next panel of the same wall is the same wall.
/// </summary>
public sealed class SightChanges
{
    /// <summary>A new thing ahead must stay ahead this long before it is said: a glance past a post
    /// between two strides is not a change.</summary>
    public const double StableSeconds = 0.2;
    /// <summary>...and nothing ahead, this long. Open is said less readily than a thing: walking along a
    /// row of parked cars, the gaps between them are not news.</summary>
    public const double OpenStableSeconds = 0.6;
    /// <summary>The same thing coming back into view within this long is not said again, unless nearer
    /// by a step.</summary>
    public const double RecentSeconds = 8.0;

    /// <summary>
    /// The distances at which the same thing, coming nearer, is said again: 10, 5 and 2 metres.
    ///
    /// Ten is where a room's far wall or a door down a corridor stops being "over there"; five is two
    /// or three strides, time to line up on it; two is one stride and an arm, about to arrive. At the
    /// game's walk of 4.5 m/s that is a line every second or two on the way in, and nothing between —
    /// the whole metre-by-metre count was the flood to avoid. A step is said once on the way in, and
    /// again only after backing off a quarter past it (<see cref="RearmFactor"/>), so tapping back and
    /// forth across five metres says it once.
    /// </summary>
    public static readonly float[] Steps = { 10f, 5f, 2f };
    public const float RearmFactor = 1.25f;

    public enum Change { New, Nearer }

    private bool _known;
    private string _shown = "";
    private int _band;
    private string? _candidate;
    private double _candidateSince;
    private readonly Dictionary<string, (double At, int Band)> _recent = new();

    /// <summary>How many of the <see cref="Steps"/> a distance is inside.</summary>
    public static int BandOf(float metres)
    {
        int b = 0;
        foreach (float s in Steps) if (metres < s) b++;
        return b;
    }

    /// <summary>Take this as what is ahead, without saying it.</summary>
    public void Adopt(string key, float distance)
    {
        _known = true;
        _shown = key;
        _band = key.Length == 0 ? 0 : BandOf(distance);
        _candidate = null;
    }

    /// <summary>This was said.</summary>
    public void Remember(double now, string key, float distance)
    {
        if (_recent.Count > 64)
        {
            var stale = new List<string>();
            foreach (var (k, v) in _recent) if (now - v.At > RecentSeconds) stale.Add(k);
            foreach (var k in stale) _recent.Remove(k);
        }
        _recent[key] = (now, key.Length == 0 ? 0 : BandOf(distance));
    }

    /// <summary>Forget what is ahead: the next look is taken in silently.</summary>
    public void Forget()
    {
        _known = false;
        _candidate = null;
    }

    /// <summary>One look ahead: what it met (<paramref name="key"/>, "" for nothing) and how far.
    /// Returns what changed, if it is to be said.</summary>
    public Change? Observe(double now, string key, float distance)
    {
        if (!_known) { Adopt(key, distance); return null; }
        bool open = key.Length == 0;
        if (key == _shown)
        {
            _candidate = null;
            if (open) return null;
            int b = BandOf(distance);
            if (b > _band) { _band = b; return Change.Nearer; }
            if (_band > 0 && distance > Steps[_band - 1] * RearmFactor) _band = b;
            return null;
        }
        if (key != _candidate) { _candidate = key; _candidateSince = now; return null; }
        if (now - _candidateSince < (open ? OpenStableSeconds : StableSeconds) - 1e-6) return null;
        _candidate = null;
        int band = open ? 0 : BandOf(distance);
        _shown = key;
        _band = band;
        if (_recent.TryGetValue(key, out var r) && now - r.At < RecentSeconds && band <= r.Band) return null;
        return Change.New;
    }
}

/// <summary>
/// Things crossing in front of you: "Hatchback passing, left to right, 6 metres".
///
/// A person or vehicle on the move (<see cref="Sightline.IsOnTheMove"/>), on your level, within
/// <see cref="Sightline.NarrationRange"/> ahead, whose bearing goes from one side of straight ahead to
/// the other between two looks while it is within <see cref="SideMetres"/> of the line — and which you
/// could see, with no wall between. Said once: a thing of the same name is not said passing again for
/// <see cref="SameNameSeconds"/>, which also makes a train one line rather than one per carriage.
/// Your own turning is not something passing, so the bearings are forgotten whenever you turn.
/// </summary>
public sealed class PassingWatch
{
    /// <summary>How near the line ahead a crossing must be on both looks, either side: a car at city
    /// speed covers a metre and a half between two looks a tenth of a second apart.</summary>
    public const float SideMetres = 4f;
    public const double SameNameSeconds = 10.0;
    /// <summary>...and a thing of the same KIND for this long: on a pavement facing the road, every
    /// pedestrian walking by crosses in front, and one "Pedestrian passing" in four seconds is the
    /// news; a different kind — the bus behind them — is still said.</summary>
    public const double SameKindSeconds = 4.0;

    private readonly Dictionary<int, (float Lateral, long Look)> _bearings = new();
    private readonly Dictionary<string, double> _saidAt = new();
    private readonly List<int> _scratch = new();
    private long _look;

    public void Forget() => _bearings.Clear();

    /// <summary>One look. Returns the line to say, or null.</summary>
    public string? Update(SightIndex index, WorldSnapshot world, Vector3 eye, float feetY, float yaw, int ownEntityId, double now)
    {
        _look++;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        float range = Sightline.NarrationRange;
        string? best = null;
        float bestDistance = float.MaxValue;
        string bestKey = "", bestKind = "";
        bool prepared = false;

        var movers = world.DynamicEntities;
        for (int i = 0; i < movers.Count; i++)
        {
            var e = movers[i];
            if (e.Id == ownEntityId || !Sightline.IsOnTheMove(e)) continue;
            var p = e.Transform.Position;
            if (p.Y < feetY - 1.5f || p.Y > feetY + 4f) { _bearings.Remove(e.Id); continue; }
            float dx = p.X - eye.X, dz = p.Z - eye.Z;
            float ahead = dx * forward.X + dz * forward.Z;
            float lateral = dx * right.X + dz * right.Z;
            if (ahead < 0.5f || ahead > range || MathF.Abs(lateral) > range) { _bearings.Remove(e.Id); continue; }
            bool had = _bearings.TryGetValue(e.Id, out var prev);
            _bearings[e.Id] = (lateral, _look);
            if (!had || prev.Look != _look - 1) continue;
            if ((prev.Lateral < 0f) == (lateral < 0f)) continue;
            if (MathF.Abs(prev.Lateral) > SideMetres || MathF.Abs(lateral) > SideMetres) continue;

            string name = e.Definition.Identity.Name ?? "";
            string key = name.Length > 0 ? name : "#" + e.Id;
            if (_saidAt.TryGetValue(key, out double at) && now - at < SameNameSeconds) continue;
            string kind = Sightline.KindOf(e);
            if (_saidAt.TryGetValue("kind:" + kind, out double kindAt) && now - kindAt < SameKindSeconds) continue;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance >= bestDistance) continue;
            if (!prepared) { index.Prepare(world, eye); prepared = true; }
            if (!InView(index, world, eye, feetY, e, ownEntityId)) continue;
            string way = prev.Lateral < 0f ? "left to right" : "right to left";
            best = $"{kind} passing, {way}, {Sightline.SpokenDistance(distance)}";
            bestDistance = distance;
            bestKey = key;
            bestKind = kind;
        }

        // Things that left the world or stopped moving keep no bearing.
        if (_look % 50 == 0)
        {
            _scratch.Clear();
            foreach (var (id, b) in _bearings) if (b.Look < _look - 1) _scratch.Add(id);
            foreach (int id in _scratch) _bearings.Remove(id);
            if (_saidAt.Count > 32)
            {
                var stale = new List<string>();
                foreach (var (k, t) in _saidAt) if (now - t > SameNameSeconds) stale.Add(k);
                foreach (var k in stale) _saidAt.Remove(k);
            }
        }

        if (best != null) { _saidAt[bestKey] = now; _saidAt["kind:" + bestKind] = now; }
        return best;
    }

    /// <summary>Whether a wall stands between your eyes and the thing: something fixed and solid, not
    /// the ground, met more than a metre short of its middle.</summary>
    private static bool InView(SightIndex index, WorldSnapshot world, Vector3 eye, float feetY, in EntitySnapshot e, int ownEntityId)
    {
        var p = e.Transform.Position;
        var target = new Vector3(p.X, Math.Clamp(p.Y, feetY + 0.5f, eye.Y), p.Z);
        var dir = target - eye;
        float length = dir.Length();
        if (length < 0.5f) return true;
        dir /= length;
        int id = e.Id;
        if (!index.Grid.Cast(world, eye, dir, length, s => s.Id != id && Sightline.Stops(s, ownEntityId) && !Sightline.IsOnTheMove(s),
                             out var hit, out float d))
            return true;
        if (d >= length - 1f) return true;
        return Sightline.IsGroundAt(hit, eye.Y + dir.Y * d, feetY, eye.Y);
    }
}

/// <summary>
/// What the sight looks through, kept so a look need not walk the whole world: the map's fixed solid
/// things in a fine grid (<see cref="SightGrid"/>), and its doorways and stair ends — every door
/// leaf's doorway (where the leaf stands shut, so an open door is still its doorway) and every stair
/// marker.
/// </summary>
public sealed class SightIndex
{
    /// <summary>A doorway or stair end, flattened: its middle, the way across it, half its width, the
    /// floor it stands on and the top of the opening.</summary>
    public readonly record struct Mark(int Id, string Name, Vector3 Centre, Vector3 Across, float Half, float Bottom, float Top, bool Stairs);

    /// <summary>How wide a stair end is taken to be, either side of its marker: a flight is 1.1 to
    /// 1.2 metres wide.</summary>
    public const float StairHalfWidth = 0.5f;
    /// <summary>How long the list is kept after the world's count of things changes, before it is
    /// rebuilt: doors and stairs are the map's and do not come and go.</summary>
    public const double RefreshSeconds = 2.0;

    private Mark[] _marks = Array.Empty<Mark>();
    private int _builtCount = -1;
    private double _builtAt = double.NegativeInfinity;

    public IReadOnlyList<Mark> Marks => _marks;

    /// <summary>The fixed solid things, for casting sight rays.</summary>
    public SightGrid Grid { get; } = new();

    /// <summary>Gathers what the casts of one look from <paramref name="eye"/> test besides the grid.</summary>
    public void Prepare(WorldSnapshot world, Vector3 eye) => Grid.Prepare(world, eye, Sightline.NarrationRange + 1f, _marks);

    public void Refresh(WorldSnapshot world, double now)
    {
        Grid.Refresh(world, now);
        if (world.Entities.Count == _builtCount) return;
        if (_builtCount >= 0 && now - _builtAt < RefreshSeconds) return;
        _builtCount = world.Entities.Count;
        _builtAt = now;
        var marks = new List<Mark>();
        foreach (var snap in world.Entities.Values)
        {
            var def = snap.Definition;
            if (!OpeningGraph.IsDoorLeaf(def)) continue;
            Vector3 centre; Quaternion rotation;
            if (def.Portal.OpeningRotation != default) { centre = def.Portal.OpeningCentre; rotation = def.Portal.OpeningRotation; }
            else if (def.Transform.Rotation != default) { centre = def.Transform.Position; rotation = def.Transform.Rotation; }
            else { centre = snap.Transform.Position; rotation = snap.Transform.Rotation; }
            var across = Vector3.Transform(Vector3.UnitX, rotation);
            across.Y = 0f;
            if (across.LengthSquared() < 1e-6f) continue;
            across = Vector3.Normalize(across);
            var size = def.Collider.Size;
            marks.Add(new Mark(snap.Id, Sightline.NameOf(snap), centre, across, size.X * 0.5f,
                               centre.Y - size.Y * 0.5f, centre.Y + size.Y * 0.5f, false));
        }
        foreach (int id in world.MarkerEntityIds)
        {
            if (!world.Entities.TryGetValue(id, out var snap)) continue;
            if (!string.Equals(snap.Definition.Identity.BeaconCategory, Beacons.Stairs, StringComparison.OrdinalIgnoreCase)) continue;
            var at = snap.Transform.Position;
            float floor = at.Y - StairCues.MarkerHeightMetres;
            marks.Add(new Mark(id, Sightline.NameOf(snap), at, Vector3.Zero, StairHalfWidth, floor, floor + 2.2f, true));
        }
        _marks = marks.ToArray();
    }
}

/// <summary>What the eyes meet: the centre line, and doorways and stairs in a cone round it. See
/// <see cref="SightWatch"/>.</summary>
public static class SightCone
{
    /// <summary>
    /// How far either side of straight ahead a doorway or a stair is looked for: twelve degrees. A
    /// doorway in the wall beside a corridor, a metre to the side, comes into it about four and a half
    /// metres ahead — two or three strides' warning that it is coming up; a stair end two metres to
    /// the side, at nine. Much wider and every door down both walls of a corridor is ahead at once.
    /// </summary>
    public const float ConeDegrees = 12f;
    /// <summary>A doorway or stair wins over the thing on the centre line unless that thing is nearer
    /// than it by more than this: the wall a door is set in is about as near as the door.</summary>
    public const float LandmarkLeadMetres = 1f;
    /// <summary>A head tilted less than this is looking level.</summary>
    public const float LevelDegrees = 15f;
    /// <summary>How far above or below your feet a stair end's floor may be and still be on your
    /// floor: a few treads.</summary>
    public const float StairLevelMetres = 0.6f;
    /// <summary>A landmark nearer ahead than this is the one you are in, not one ahead of you.</summary>
    public const float MinAheadMetres = 0.3f;

    private static readonly float TanCone = MathF.Tan(ConeDegrees * MathF.PI / 180f);

    public static bool IsLevel(float pitch) => MathF.Abs(pitch) < LevelDegrees * MathF.PI / 180f;

    /// <summary>
    /// What is ahead. Level: the centre line (<see cref="Sightline.AheadLevel"/>, looking through
    /// things on the move) or a doorway or stair in the cone, whichever wins (see
    /// <see cref="LandmarkLeadMetres"/>). Tilted up or down: only what the eye line meets — a ceiling,
    /// a wall, never the floor.
    /// </summary>
    public static Sightline.Sighting? Look(SightIndex index, WorldSnapshot world, Vector3 feet,
                                           float yaw, float pitch, float eyeHeight, int ownEntityId)
    {
        var eyeAt = feet + new Vector3(0, eyeHeight, 0);
        index.Prepare(world, eyeAt);
        if (!IsLevel(pitch))
        {
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0f));
            ReadOnlySpan<Vector3> eye = stackalloc Vector3[] { eyeAt };
            return Centre(index, world, eye, forward, feet.Y, eyeAt.Y, ownEntityId);
        }
        // Level, the way you face, at your knees, chest and eyes (as Sightline.AheadLevel).
        Vector3 level = new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        ReadOnlySpan<Vector3> origins = stackalloc Vector3[]
        {
            feet + new Vector3(0, 0.5f, 0),
            feet + new Vector3(0, 1.1f, 0),
            eyeAt,
        };
        var centre = Centre(index, world, origins, level, feet.Y, eyeAt.Y, ownEntityId);
        var mark = Landmark(index, world, feet, yaw, eyeHeight, ownEntityId);
        if (mark is { } m && (centre is not { } c || m.Distance <= c.Distance + LandmarkLeadMetres)) return m;
        return centre;
    }

    /// <summary><see cref="Sightline.Ahead"/> through the grid, looking through things on the move.</summary>
    private static Sightline.Sighting? Centre(SightIndex index, WorldSnapshot world, ReadOnlySpan<Vector3> origins, Vector3 dir,
                                              float feetY, float eyeY, int ownEntityId)
    {
        dir = Vector3.Normalize(dir);
        Func<EntitySnapshot, bool> stops = e => Sightline.Stops(e, ownEntityId) && !Sightline.IsOnTheMove(e);
        Sightline.Sighting? best = null;
        foreach (var origin in origins)
        {
            if (!index.Grid.Cast(world, origin, dir, Sightline.NarrationRange, stops, out var hit, out float dist)) continue;
            if (Sightline.IsGroundAt(hit, origin.Y + dir.Y * dist, feetY, eyeY)) continue;
            if (best == null || dist < best.Value.Distance) best = new Sightline.Sighting(hit, dist);
        }
        return best;
    }

    /// <summary>The nearest doorway or stair end in the cone, on your floor and in plain view.</summary>
    public static Sightline.Sighting? Landmark(SightIndex index, WorldSnapshot world, Vector3 feet,
                                               float yaw, float eyeHeight, int ownEntityId)
    {
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
        var eye = feet + new Vector3(0, eyeHeight, 0);
        float range = Sightline.NarrationRange;
        Sightline.Sighting? best = null;
        var marks = index.Marks;
        for (int i = 0; i < marks.Count; i++)
        {
            var m = marks[i];
            float cx = m.Centre.X - eye.X, cz = m.Centre.Z - eye.Z;
            float reach = range + m.Half;
            if (cx * cx + cz * cz > reach * reach) continue;
            if (m.Stairs ? MathF.Abs(m.Bottom - feet.Y) > StairLevelMetres
                         : m.Bottom > feet.Y + 0.6f || m.Top < feet.Y + 1.0f) continue;
            if (best is { } b && MathF.Sqrt(cx * cx + cz * cz) - m.Half > b.Distance) continue;

            // The opening as a segment across it, a little in from each jamb for a door; a stair end
            // square to the look.
            var across = m.Stairs ? right : m.Across;
            float half = m.Stairs ? m.Half : m.Half * 0.8f;
            float f0 = cx * forward.X + cz * forward.Z, l0 = cx * right.X + cz * right.Z;
            float fa = half * (across.X * forward.X + across.Z * forward.Z);
            float la = half * (across.X * right.X + across.Z * right.Z);
            // The part of it inside the cone and ahead: t in [-1, 1] along it.
            float lo = -1f, hi = 1f;
            Restrict(ref lo, ref hi, -fa, MinAheadMetres - f0);                  // f(t) >= min
            Restrict(ref lo, ref hi, fa, f0 - range);                            // f(t) <= range
            Restrict(ref lo, ref hi, la - TanCone * fa, l0 - TanCone * f0);      // l(t) <= k f(t)
            Restrict(ref lo, ref hi, -la - TanCone * fa, -l0 - TanCone * f0);    // -l(t) <= k f(t)
            if (lo > hi) continue;
            float t = MathF.Abs(la) > 1e-6f ? Math.Clamp(-l0 / la, lo, hi) : Math.Clamp(0f, lo, hi);
            float px = m.Centre.X + across.X * half * t, pz = m.Centre.Z + across.Z * half * t;
            float dx = px - eye.X, dz = pz - eye.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (best is { } b2 && distance >= b2.Distance) continue;

            float aimY = m.Stairs ? m.Bottom + eyeHeight : Math.Clamp(eye.Y, m.Bottom + 0.3f, m.Top - 0.2f);
            if (!InView(index, world, eye, new Vector3(px, aimY, pz), m, feet.Y, ownEntityId)) continue;
            if (!world.Entities.TryGetValue(m.Id, out var snap)) continue;
            best = new Sightline.Sighting(snap, distance);
        }
        return best;
    }

    /// <summary>Narrows [lo, hi] to where a t + b &lt;= 0.</summary>
    private static void Restrict(ref float lo, ref float hi, float a, float b)
    {
        if (MathF.Abs(a) < 1e-6f) { if (b > 0f) { lo = 1f; hi = -1f; } return; }
        float t0 = -b / a;
        if (a > 0f) hi = MathF.Min(hi, t0); else lo = MathF.Max(lo, t0);
    }

    /// <summary>Whether the eye sees <paramref name="target"/>: nothing fixed and solid in the way
    /// but the door itself (or its other leaf), or the nose of a step.</summary>
    private static bool InView(SightIndex index, WorldSnapshot world, Vector3 eye, Vector3 target, SightIndex.Mark m,
                               float feetY, int ownEntityId)
    {
        var dir = target - eye;
        float length = dir.Length();
        if (length < 0.05f) return true;
        dir /= length;
        int id = m.Id;
        string name = m.Name;
        bool stairs = m.Stairs;
        if (!index.Grid.Cast(world, eye, dir, length,
                s => s.Id != id && Sightline.Stops(s, ownEntityId) && !Sightline.IsOnTheMove(s)
                     && (stairs || !OpeningGraph.IsDoorLeaf(s.Definition) || Sightline.NameOf(s) != name),
                out var hit, out float d))
            return true;
        if (d >= length - 0.25f) return true;
        return Sightline.IsGroundAt(hit, eye.Y + dir.Y * d, feetY, eye.Y);
    }
}
