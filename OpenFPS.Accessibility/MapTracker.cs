using System.Numerics;
using OpenFPS.Acoustics;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// The things of one kind on the map, nearest first, stepped through a key at a time (Cody, 2026-10-04:
/// "just , and . should cycle the selected items", categories on the Shift variants like chat).
/// The categories are a fixed ring, an empty one said as "none nearby", so the ring does not change
/// under your fingers. Each thing is said with its direction (<see cref="DirectionWords"/>), distance
/// across the ground and floors up or down. The order is worked out again only on a category change or
/// after moving <see cref="ResortMetres"/>, so stepping does not jump about as you turn. Everything
/// comes from the scene view (stage 9) within <see cref="RangeMetres"/>.
/// </summary>
public sealed class MapTracker
{
    /// <summary>How far things are looked for, metres. A city block and its neighbours: as far as a
    /// beacon or a voice carries, and further than any building on the city is deep.</summary>
    public const float RangeMetres = 100f;

    /// <summary>Walk this far and the next key works the order out again from where you are now.</summary>
    public const float ResortMetres = 3f;

    /// <summary>A storey on the city (tools/gen_city.py STOREY). A thing this far above or below your
    /// feet, give or take half a metre, is on another floor and is said so.</summary>
    public const float StoreyMetres = 3f;

    /// <summary>Two things of the same name nearer than this are one: the two leaves of a
    /// bi-parting door.</summary>
    public const float SameThingMetres = 4f;

    /// <summary>A thing nearer than this across the ground is "right here".</summary>
    public const float HereMetres = 0.5f;

    public static readonly TrackCategory[] Ring = (TrackCategory[])Enum.GetValues(typeof(TrackCategory));

    /// <summary>One thing found: what it is, where it is spoken from, where its floor is, and how far it
    /// is to get to (<see cref="Reach"/>), which is the order things are stepped through in.</summary>
    public readonly record struct Tracked(int Id, string Name, Vector3 At, float FloorY, float Distance);

    /// <summary>
    /// How far a thing is to get to: across the ground, with each metre up or down counted twice, since a
    /// floor up is a flight of stairs away. Straight-line distance put a roof door 18 m overhead before
    /// the next building's front door 16 m along the pavement.
    /// </summary>
    public static float Reach(Vector3 feet, Vector3 at, float floorY)
        => Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(at.X, at.Z)) + 2f * MathF.Abs(floorY - feet.Y);

    private TrackCategory _category;
    private readonly List<Tracked> _list = new();
    private Vector3 _sortedAt;
    private bool _sorted;
    private int _cursor = -1;

    public MapTracker(TrackCategory category = TrackCategory.Doors) => _category = category;

    public TrackCategory Category => _category;

    /// <summary>The thing last stepped to, if any.</summary>
    public Tracked? Selected => _cursor >= 0 && _cursor < _list.Count ? _list[_cursor] : null;

    /// <summary>"Doors", "Places".</summary>
    public static string NameOf(TrackCategory c) => c.ToString();

    /// <summary>A category by name, as typed: "doors", "door", "people". Null for anything else.</summary>
    public static TrackCategory? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim().ToLowerInvariant();
        foreach (var c in Ring)
        {
            string n = c.ToString().ToLowerInvariant();
            if (t == n || t + "s" == n || (c == TrackCategory.People && t is "person" or "players" or "player")
                || (c == TrackCategory.Stairs && t is "stair") || (c == TrackCategory.Entrances && t is "entrance" or "exits" or "exit"))
                return c;
        }
        return null;
    }

    /// <summary>Forgets the order, so the next key works it out afresh (a new map, a teleport).</summary>
    public void Reset()
    {
        _list.Clear();
        _sorted = false;
        _cursor = -1;
    }

    /// <summary>Puts the tracker on a category without saying anything (from the settings file).</summary>
    public void SetCategory(TrackCategory category)
    {
        _category = category;
        Reset();
    }

    /// <summary>
    /// Shift+comma (-1) and Shift+period (+1): the next category round the ring, and how many of it
    /// there are near you. The next comma or period then says the nearest.
    /// </summary>
    public string CycleCategory(int direction, IAcousticWorld world, Vector3 feet, int selfId)
    {
        int n = Ring.Length;
        _category = Ring[(((int)_category + Math.Sign(direction)) % n + n) % n];
        Rebuild(world, feet, selfId);
        return CountLine();
    }

    /// <summary>"Doors, 12 nearby." or "Vehicles, none nearby."</summary>
    public string CountLine()
        => _list.Count == 0 ? $"{NameOf(_category)}, none nearby." : $"{NameOf(_category)}, {_list.Count} nearby.";

    /// <summary>
    /// Comma (-1) and period (+1): the thing before or after the one you were on, nearest first. The
    /// first key after the order is worked out says the nearest, whichever key it is. At either end
    /// it stays on the last one and says so, as the chat ring says "Top." and "Bottom.".
    /// </summary>
    public string Step(int direction, IAcousticWorld world, Vector3 feet, float yaw, int selfId)
    {
        bool stale = !_sorted || Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(_sortedAt.X, _sortedAt.Z)) > ResortMetres
                     || MathF.Abs(feet.Y - _sortedAt.Y) > StoreyMetres * 0.5f
                     || (Selected is { } was && !world.TryGetBody(was.Id, out _));
        if (stale) Rebuild(world, feet, selfId);
        if (_list.Count == 0) return $"No {NameOf(_category).ToLowerInvariant()} within {RangeMetres:0} metres.";

        string edge = "";
        if (_cursor < 0) _cursor = 0;
        else
        {
            int at = _cursor + Math.Sign(direction);
            if (at < 0) edge = "Nearest. ";
            else if (at >= _list.Count) edge = "Farthest. ";
            _cursor = Math.Clamp(at, 0, _list.Count - 1);
        }
        var t = Live(world, _list[_cursor]);
        return edge + Describe(t, feet, yaw);
    }

    /// <summary>The thing where it is NOW: a person or a car may have moved since the order was made.</summary>
    private Tracked Live(IAcousticWorld world, Tracked t)
    {
        if (_category is TrackCategory.People or TrackCategory.Vehicles && world.TryGetBody(t.Id, out var b))
            return t with { At = b.Position, FloorY = b.Position.Y };
        return t;
    }

    /// <summary>"Brandt Court front entrance, left in front, 14 metres, one floor up."</summary>
    public static string Describe(Tracked t, Vector3 feet, float yaw)
    {
        var flat = new Vector3(t.At.X - feet.X, 0f, t.At.Z - feet.Z);
        float across = flat.Length();
        string where = across < HereMetres ? "right here" : $"{DirectionWords.Relative(yaw, flat)}, {Sightline.SpokenDistance(across)}";
        string floors = Floors(t.FloorY - feet.Y);
        return floors.Length > 0 ? $"{t.Name}, {where}, {floors}." : $"{t.Name}, {where}.";
    }

    /// <summary>"one floor up", "2 floors down", or "" within half a metre short of a storey, so a kerb,
    /// a ramp or a car's roof is not a floor.</summary>
    public static string Floors(float rise)
    {
        if (MathF.Abs(rise) < StoreyMetres - 0.5f) return "";
        int n = Math.Max(1, (int)MathF.Round(MathF.Abs(rise) / StoreyMetres));
        string count = n == 1 ? "one floor" : $"{n} floors";
        return rise > 0 ? $"{count} up" : $"{count} down";
    }

    private void Rebuild(IAcousticWorld world, Vector3 feet, int selfId)
    {
        _list.Clear();
        _list.AddRange(Gather(world, feet, _category, selfId));
        _sortedAt = feet;
        _sorted = true;
        _cursor = -1;
    }

    /// <summary>Everything of a category within <see cref="RangeMetres"/> in a straight line, nearest to
    /// get to first (<see cref="Reach"/>), each once.</summary>
    public static List<Tracked> Gather(IAcousticWorld world, Vector3 feet, TrackCategory category, int selfId)
    {
        var found = new List<Tracked>();
        var bodies = new List<AcousticBody>();
        world.AllBodies(bodies);
        foreach (var e in bodies)
        {
            if (e.Id == selfId) continue;
            if (Match(world, e, category, feet) is { } t && Vector3.Distance(feet, t.At) <= RangeMetres)
                found.Add(t with { Distance = Reach(feet, t.At, t.FloorY) });
        }
        found.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Id.CompareTo(b.Id));

        // The two leaves of a bi-parting door, the same place named twice: the nearer stands for both.
        var result = new List<Tracked>(found.Count);
        foreach (var t in found)
        {
            bool dup = false;
            foreach (var r in result)
                if (r.Name == t.Name && Vector3.Distance(r.At, t.At) < SameThingMetres) { dup = true; break; }
            if (!dup) result.Add(t);
        }
        return result;
    }

    /// <summary>Whether a thing belongs to a category, and if so how it is named and placed.</summary>
    internal static Tracked? Match(IAcousticWorld world, in AcousticBody e, TrackCategory category, Vector3 feet)
    {
        string beacon = e.Beacon ?? "";
        Vector3 at = e.Position;
        switch (category)
        {
            case TrackCategory.Doors:
                if (!Is(beacon, Beacons.Door)) return null;
                return Thing(e, Sightline.NameOf(e), feet);
            case TrackCategory.Entrances:
                if (!IsEntrance(world, e)) return null;
                return Thing(e, EntranceName(world, e), feet);
            case TrackCategory.Stairs:
                // A stair marker stands at handrail height over its landing and is named with its line:
                // "Stairs up, 17 steps, to floor 3".
                if (!Is(beacon, Beacons.Stairs)) return null;
                return new Tracked(e.Id, Sightline.NameOf(e), at, at.Y - StairCues.MarkerHeightMetres, Vector3.Distance(feet, at));
            case TrackCategory.Items:
                // The server takes the item beacon off anything somebody is holding or carrying.
                if (!Is(beacon, Beacons.Item)) return null;
                return Thing(e, Sightline.NameOf(e), feet);
            case TrackCategory.People:
                if (SightKinds.Classify(e) is not (SightKind.Person or SightKind.Player) || Is(beacon, Beacons.Vehicle)) return null;
                var kind = SightKinds.Classify(e)!.Value;
                return new Tracked(e.Id, SightKinds.NameOf(e, kind), at, at.Y, Vector3.Distance(feet, at));
            case TrackCategory.Vehicles:
            {
                // A parked car is a vehicle beacon; one being driven is heard by its engine.
                bool vehicle = Is(beacon, Beacons.Vehicle)
                    || SightKinds.Classify(e) is SightKind.Vehicle or SightKind.Aircraft or SightKind.Train;
                if (!vehicle) return null;
                var k = SightKinds.Classify(e);
                string name = k is { } sk && sk != SightKind.Player && sk != SightKind.Person
                    ? SightKinds.NameOf(e, sk) : Sightline.NameOf(e);
                return Thing(e, name, feet);
            }
            case TrackCategory.Places:
            {
                // A region: a room, a floor of a stairwell, a street.
                string name = e.RegionName ?? "";
                if (string.IsNullOrWhiteSpace(name)) return null;
                // A place is as near as its nearest edge, and you are in it at nought: a room as far as its flood's
                // air reaches, a place (and a room that did not flood) as far as its box.
                Vector3 min, max;
                var cells = world.Rooms?.RoomCells;
                if (cells != null && cells.TryAir(e.Id, out var lo, out var hi, out _))
                {
                    if (cells.TryRoomAt(feet + new Vector3(0f, 1f, 0f), out int inRoom) && inRoom == e.Id)
                        return new Tracked(e.Id, name.Trim(), feet, feet.Y, 0f);
                    float c = OpenFPS.Geometry.RoomFlood.Cell * 0.5f;
                    (min, max) = (lo - new Vector3(c), hi + new Vector3(c));
                }
                else
                {
                    var size = e.Size != Vector3.Zero ? e.Size : e.RoomSize;
                    (min, max) = Bounds(e.Position, e.Rotation, size);
                }
                var near = Vector3.Clamp(feet, min, max);
                return new Tracked(e.Id, name.Trim(), near, near.Y, Vector3.Distance(feet, near));
            }
        }
        return null;
    }

    /// <summary>The axis-aligned box round a box of this size, placed and turned as the body says.</summary>
    private static (Vector3 Min, Vector3 Max) Bounds(Vector3 position, Quaternion rotation, Vector3 size)
    {
        var half = size * 0.5f;
        var r = Matrix4x4.CreateFromQuaternion(rotation);
        var ext = new Vector3(
            MathF.Abs(r.M11) * half.X + MathF.Abs(r.M21) * half.Y + MathF.Abs(r.M31) * half.Z,
            MathF.Abs(r.M12) * half.X + MathF.Abs(r.M22) * half.Y + MathF.Abs(r.M32) * half.Z,
            MathF.Abs(r.M13) * half.X + MathF.Abs(r.M23) * half.Y + MathF.Abs(r.M33) * half.Z);
        return (position - ext, position + ext);
    }

    /// <summary>A thing with a body: spoken from its middle, its floor under its lowest point.</summary>
    private static Tracked Thing(in AcousticBody e, string name, Vector3 feet)
    {
        var at = e.Position;
        float floor = at.Y;
        if (e.Size.Y > 0f) floor = Sightline.WorldBounds(e).Min.Y;
        return new Tracked(e.Id, name, at, floor, Vector3.Distance(feet, at));
    }

    private static bool Is(string beacon, string category) => string.Equals(beacon, category, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A way in or out of a building, from what it joins, not what it is called: an exit beacon, or an opening with
    /// the open air (no region, or one not indoors) on one side and a room on the other. A door is made to be walked
    /// through; an opening with no leaf is one where a person can (<see cref="WalkedThrough"/>), so a car park's open
    /// side over its parapet is not a way in and the hangar's open front is.
    /// </summary>
    internal static bool IsEntrance(IAcousticWorld world, in AcousticBody e)
    {
        string beacon = e.Beacon ?? "";
        if (Is(beacon, Beacons.Exit)) return true;
        int a = e.RegionAId, b = e.RegionBId;
        if (a == b || Outdoors(world, a) == Outdoors(world, b)) return false;
        return Is(beacon, Beacons.Door) || WalkedThrough(world, e);
    }

    /// <summary>How far below an opening's sill the floor may be on either side for a person to walk through it,
    /// metres: a step.</summary>
    public const float SillStepMetres = 0.5f;

    /// <summary>Whether a person can walk through an opening: a floor within a step of its sill on both sides. Its
    /// frame is X across, Y up and Z through.</summary>
    internal static bool WalkedThrough(IAcousticWorld world, in AcousticBody e)
    {
        var size = e.Size;
        if (size.Y <= 0f) return false;
        var turn = e.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(e.Rotation);
        var up = Vector3.Transform(Vector3.UnitY, turn);
        var through = Vector3.Transform(Vector3.UnitZ, turn);
        var sill = e.Position - up * (size.Y * 0.5f);
        foreach (float side in new[] { -1f, 1f })
        {
            var above = sill + through * (side * (size.Z * 0.5f + 0.4f)) + new Vector3(0f, SillStepMetres, 0f);
            if (!SightRays.CastSight(world, above, -Vector3.UnitY, 2f * SillStepMetres, out _, out _, glassBlocks: true)) return false;
        }
        return true;
    }

    /// <summary>An entrance as it is said: a door by its name, and an opening with no leaf by the room it is the way
    /// into (its own name is its prefab's, "Acoustic Portal").</summary>
    internal static string EntranceName(IAcousticWorld world, in AcousticBody e)
    {
        string beacon = e.Beacon ?? "";
        if (Is(beacon, Beacons.Door) || Is(beacon, Beacons.Exit))
            return Sightline.NameOf(e);
        int a = e.RegionAId, b = e.RegionBId;
        int room = Outdoors(world, a) ? b : a;
        return world.TryGetBody(room, out var r) && !string.IsNullOrWhiteSpace(r.RegionName)
            ? $"{r.RegionName.Trim()} entrance" : Sightline.NameOf(e);
    }

    private static bool Outdoors(IAcousticWorld world, int regionId)
        => regionId == AcousticConstants.GlobalRegionId
           || (world.TryGetBody(regionId, out var r) && !r.RegionIndoor);
}
