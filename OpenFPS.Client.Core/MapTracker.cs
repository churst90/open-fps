using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;

namespace OpenFPS.Client.Core;

/// <summary>What comma and period step through: one kind of thing at a time.</summary>
public enum TrackCategory { Doors, Entrances, Stairs, Items, People, Vehicles, Places }

/// <summary>
/// The things of one kind on the map, nearest first, stepped through a key at a time.
///
/// "We should have a way of tracking different things on a map, like just doors, or just items ...
/// shift variants change categories of things you can view just like the chat implementation. then
/// just , and . should cycle the selected items" (Cody, 2026-10-04). So it is the chat ring's shape:
/// Shift+comma and Shift+period go round the categories, which are a fixed ring — an empty one is
/// still there, said as "none nearby", so the ring does not change under your fingers as you walk —
/// and comma and period step back and forth through the things in the one you are on.
///
/// Each thing is said as its name, which way it is from the way you face (the eight words the server
/// uses, <see cref="DirectionWords"/>), how far across the ground, and how many floors up or down
/// if it is on another one. The order is nearest first, worked out when you change category or press
/// a key having moved <see cref="ResortMetres"/> since it was last worked out; between those it stays
/// put, so stepping on goes on to the next one rather than jumping about as you turn.
///
/// Everything comes from the world the client already holds, within <see cref="RangeMetres"/>.
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
    /// How far a thing is to get to: across the ground, and each metre up or down counted twice, since
    /// a floor up is a flight of stairs away and not three metres. Straight-line distance put a roof
    /// door eighteen metres overhead before the next building's front door sixteen metres along the
    /// pavement; this puts what is on your own floor first.
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
    public string CycleCategory(int direction, WorldSnapshot world, Vector3 feet, int selfId)
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
    public string Step(int direction, WorldSnapshot world, Vector3 feet, float yaw, int selfId)
    {
        bool stale = !_sorted || Vector2.Distance(new Vector2(feet.X, feet.Z), new Vector2(_sortedAt.X, _sortedAt.Z)) > ResortMetres
                     || MathF.Abs(feet.Y - _sortedAt.Y) > StoreyMetres * 0.5f
                     || (Selected is { } was && !world.Entities.ContainsKey(was.Id));
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
    private Tracked Live(WorldSnapshot world, Tracked t)
    {
        if (_category is TrackCategory.People or TrackCategory.Vehicles && world.Entities.TryGetValue(t.Id, out var e))
            return t with { At = e.Transform.Position, FloorY = e.Transform.Position.Y };
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

    /// <summary>"one floor up", "2 floors down", or "" on your own floor (within half a storey,
    /// so a kerb, a ramp or a car's roof is not a floor).</summary>
    public static string Floors(float rise)
    {
        if (MathF.Abs(rise) < StoreyMetres - 0.5f) return "";
        int n = Math.Max(1, (int)MathF.Round(MathF.Abs(rise) / StoreyMetres));
        string count = n == 1 ? "one floor" : $"{n} floors";
        return rise > 0 ? $"{count} up" : $"{count} down";
    }

    private void Rebuild(WorldSnapshot world, Vector3 feet, int selfId)
    {
        _list.Clear();
        _list.AddRange(Gather(world, feet, _category, selfId));
        _sortedAt = feet;
        _sorted = true;
        _cursor = -1;
    }

    /// <summary>Everything of a category within <see cref="RangeMetres"/> in a straight line, nearest to
    /// get to first (<see cref="Reach"/>), each once.</summary>
    public static List<Tracked> Gather(WorldSnapshot world, Vector3 feet, TrackCategory category, int selfId)
    {
        var found = new List<Tracked>();
        void Consider(in EntitySnapshot e)
        {
            if (e.Id == selfId) return;
            if (Match(world, e, category, feet) is { } t && Vector3.Distance(feet, t.At) <= RangeMetres)
                found.Add(t with { Distance = Reach(feet, t.At, t.FloorY) });
        }
        // A place is a region, and the world keeps a list of those; everything else is looked at once.
        if (category == TrackCategory.Places)
        {
            foreach (int id in world.RegionEntityIds)
                if (world.Entities.TryGetValue(id, out var r)) Consider(r);
        }
        else
            foreach (var e in world.Entities.Values) Consider(e);
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
    internal static Tracked? Match(WorldSnapshot world, in EntitySnapshot e, TrackCategory category, Vector3 feet)
    {
        var def = e.Definition;
        string beacon = def.Identity.BeaconCategory ?? "";
        Vector3 at = e.Transform.Position;
        switch (category)
        {
            case TrackCategory.Doors:
                if (!Is(beacon, Beacons.Door)) return null;
                return Thing(e, Sightline.NameOf(e), feet);
            case TrackCategory.Entrances:
                if (!IsEntrance(world, e)) return null;
                return Thing(e, Sightline.NameOf(e), feet);
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
                if (ScopeView.Classify(e) is not (SightKind.Person or SightKind.Player) || Is(beacon, Beacons.Vehicle)) return null;
                var kind = ScopeView.Classify(e)!.Value;
                return new Tracked(e.Id, ScopeView.NameOf(e, kind), at, at.Y, Vector3.Distance(feet, at));
            case TrackCategory.Vehicles:
            {
                // A parked car is a vehicle beacon; one being driven is heard by its engine.
                bool vehicle = Is(beacon, Beacons.Vehicle)
                    || ScopeView.Classify(e) is SightKind.Vehicle or SightKind.Aircraft or SightKind.Train;
                if (!vehicle) return null;
                var k = ScopeView.Classify(e);
                string name = k is { } sk && sk != SightKind.Player && sk != SightKind.Person
                    ? ScopeView.NameOf(e, sk) : Sightline.NameOf(e);
                return Thing(e, name, feet);
            }
            case TrackCategory.Places:
            {
                // A region: a room, a floor of a stairwell, a street. Gather only asks of regions.
                string name = def.Region.FriendlyName ?? "";
                if (string.IsNullOrWhiteSpace(name)) return null;
                // A place is as near as its nearest edge, and you are in it at nought.
                var size = def.Collider.Size != Vector3.Zero ? def.Collider.Size : def.Region.RoomSize;
                var (min, max) = Bounds(e.Transform, size);
                var near = Vector3.Clamp(feet, min, max);
                return new Tracked(e.Id, name.Trim(), near, near.Y, Vector3.Distance(feet, near));
            }
        }
        return null;
    }

    /// <summary>The axis-aligned box round a box of this size, placed and turned as the transform says.</summary>
    private static (Vector3 Min, Vector3 Max) Bounds(in Transform t, Vector3 size)
    {
        var half = size * 0.5f;
        var r = Matrix4x4.CreateFromQuaternion(t.Rotation);
        var ext = new Vector3(
            MathF.Abs(r.M11) * half.X + MathF.Abs(r.M21) * half.Y + MathF.Abs(r.M31) * half.Z,
            MathF.Abs(r.M12) * half.X + MathF.Abs(r.M22) * half.Y + MathF.Abs(r.M32) * half.Z,
            MathF.Abs(r.M13) * half.X + MathF.Abs(r.M23) * half.Y + MathF.Abs(r.M33) * half.Z);
        return (t.Position - ext, t.Position + ext);
    }

    /// <summary>A thing with a body: spoken from its middle, its floor under its lowest point.</summary>
    private static Tracked Thing(in EntitySnapshot e, string name, Vector3 feet)
    {
        var at = e.Transform.Position;
        float floor = at.Y;
        var size = e.Definition.Collider.Size;
        if (size.Y > 0f) floor = Sightline.WorldBounds(e).Min.Y;
        return new Tracked(e.Id, name, at, floor, Vector3.Distance(feet, at));
    }

    private static bool Is(string beacon, string category) => string.Equals(beacon, category, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A way in or out of a building: a door between inside and outside — the open air on one side of
    /// its doorway (no region, or a region that is not indoors) and a room on the other — or an exit
    /// beacon, or anything its map calls an entrance. Worked out from the doorway, not from names,
    /// except where a map has said so in words.
    /// </summary>
    internal static bool IsEntrance(WorldSnapshot world, in EntitySnapshot e)
    {
        var def = e.Definition;
        string beacon = def.Identity.BeaconCategory ?? "";
        if (Is(beacon, Beacons.Exit)) return true;
        string name = def.Identity.Name ?? "";
        if (name.Contains("entrance", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Is(beacon, Beacons.Door)) return false;
        int a = def.Portal.RegionAId, b = def.Portal.RegionBId;
        if (a == b) return false;
        return Outdoors(world, a) != Outdoors(world, b);
    }

    private static bool Outdoors(WorldSnapshot world, int regionId)
        => regionId == AcousticConstants.GlobalRegionId
           || (world.Entities.TryGetValue(regionId, out var r) && !r.Definition.Region.IsIndoor);
}
