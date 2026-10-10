using System.Globalization;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Editing;
using OpenFPS.Common.Networking;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Editor;

/// <summary>A road, path or railway laid (Adding) or taken up, as its data; its pieces are PlaceOps beside it.</summary>
public sealed record RouteOp(string MapId, OverlayRoute Route, bool Adding) : EditOp(MapId)
{
    public override string What => Adding ? $"laid {Route.Name}" : $"took up {Route.Name}";
}

/// <summary>
/// Roads, paths and railways as data (docs/WORLD_EDITOR.md section 18): laid by walking them, a point
/// dropped every metre and the straight stretches joined up when it is finished, or by typing points;
/// with a width and a surface, and for a railway a level (on the ground, raised on pillars, underground
/// in a tunnel of its own), stations, level crossings and a train. What it is made of is ordinary things
/// placed with the editor; what traffic and trains follow is the map's RoadData and TrackData, made from
/// it at load (MapOverlayStore.Lay), so they use it through the code they always have.
/// </summary>
public sealed partial class WorldEditor
{
    /// <summary>A point is dropped every this many metres walked; the straight stretches are joined when it is laid.</summary>
    public const float RouteSampleMetres = 1f;
    /// <summary>How far off a straight line a point may be and still be joined into it, metres.</summary>
    public const float RouteTolerance = 0.5f;
    /// <summary>How often walking a route says how far it has come, metres.</summary>
    public const float RouteSayEvery = 20f;
    /// <summary>The most points one route keeps while it is walked, and its longest, metres.</summary>
    public const int RouteMaxPoints = 5000;
    public const float RouteMaxMetres = 5000f;

    /// <summary>How far a road's top stands over the ground it is laid on, metres: enough to be what you stand on.</summary>
    private const float SurfaceRise = 0.02f;
    /// <summary>A rail's head over the bed it is laid on, metres.</summary>
    private const float RailHead = 0.2f;
    /// <summary>A platform's top over the rail head, metres: a train's floor.</summary>
    private const float PlatformRise = 0.75f;
    private const float PlatformLength = 40f, PlatformWidth = 3f;
    /// <summary>The inside of a tunnel: its height, metres; its width is the line's, at least five.</summary>
    private const float TunnelHeight = 5f, TunnelMinWidth = 5f, Concrete = 0.4f;
    /// <summary>The most metres between a raised line's pillars.</summary>
    private const float PillarSpacing = 20f;

    public static readonly string[] RouteKinds = { OverlayRoute.Road, OverlayRoute.Path, OverlayRoute.Rail };
    public static readonly string[] RouteLevels = { OverlayRoute.OnTheGround, OverlayRoute.Raised, OverlayRoute.Underground };

    private static string KindWord(string kind) => kind == OverlayRoute.Rail ? "railway" : kind;

    private static string? KindOf(string word) => word.ToLowerInvariant() switch
    {
        "road" or "street" => OverlayRoute.Road,
        "path" or "pavement" or "footpath" or "sidewalk" => OverlayRoute.Path,
        "railway" or "rail" or "track" or "line" or "railroad" => OverlayRoute.Rail,
        _ => null,
    };

    private static string? LevelOf(string word) => word.ToLowerInvariant() switch
    {
        "ground" or "surface" or "level" or "flat" => OverlayRoute.OnTheGround,
        "raised" or "elevated" or "pillars" or "viaduct" => OverlayRoute.Raised,
        "underground" or "tunnel" or "subway" => OverlayRoute.Underground,
        _ => null,
    };

    /// <summary>A level as said: "on the ground", "raised 6 metres on pillars", "underground, 8 metres down".</summary>
    private static string SayLevel(OverlayRoute r) => r.Level switch
    {
        OverlayRoute.Raised => $"raised {Metres(r.LevelMetres)} on pillars",
        OverlayRoute.Underground => $"underground, {Metres(r.LevelMetres)} down, in a tunnel of its own",
        _ => "on the ground",
    };

    private static float DefaultWidth(string kind) => kind switch { OverlayRoute.Path => 2f, OverlayRoute.Rail => 4.2f, _ => 7f };
    private static string DefaultSurface(string kind) => kind switch { OverlayRoute.Path => "Concrete", OverlayRoute.Rail => "Gravel", _ => "Asphalt" };
    private static float DefaultSpeed(string kind) => kind == OverlayRoute.Rail ? 60f : 40f;
    private static float DefaultLevelMetres(string level) => level == OverlayRoute.Underground ? 8f : 6f;

    /// <summary>What a route's surface can be: one thin plain floor or road prefab per material the acoustic registry knows.</summary>
    internal List<(string Material, PrefabTemplate Prefab)> RouteSurfaces(UserSession s)
        => Placeable(s)
            .Where(t => Plain(t) && t.ColliderSize!.Value.Y <= 0.25f && t.ColliderSize.Value.X >= 1f && t.ColliderSize.Value.Z >= 1f
                        && (t.Id.Contains("floor", StringComparison.OrdinalIgnoreCase) || t.Id.Contains("road", StringComparison.OrdinalIgnoreCase))
                        && !t.Id.StartsWith("shore_", StringComparison.OrdinalIgnoreCase) && t.Material != "None" && AcousticRegistry.IsKnown(t.Material))
            .GroupBy(t => t.Material)
            .Select(g => (g.Key, g.OrderBy(t => t.Id.Equals($"{g.Key}_floor", StringComparison.OrdinalIgnoreCase) ? 0 : t.Id.Contains("road") ? 1 : 2)
                              .ThenBy(t => t.Id, StringComparer.Ordinal).First()))
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .ToList();

    // ── The map's data, from a route ────────────────────────────────────────────────────────────

    private static float Length(IReadOnlyList<Vector3> pts, bool closed)
    {
        float l = 0f;
        for (int i = 0; i + 1 < pts.Count; i++) l += Flat(pts[i + 1] - pts[i]);
        if (closed && pts.Count > 2) l += Flat(pts[0] - pts[^1]);
        return l;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>The height a route runs at over its ground: a road's top, a rail's head on its bed, deck or tunnel floor.</summary>
    private static float Rise(OverlayRoute r) => !r.IsRail ? SurfaceRise : r.Level switch
    {
        OverlayRoute.Raised => r.LevelMetres + RailHead,
        OverlayRoute.Underground => -r.LevelMetres + RailHead,
        _ => SurfaceRise + RailHead,
    };

    /// <summary>The line traffic or trains follow: the ground points raised to the running surface.</summary>
    internal static List<Vector3> LineOf(OverlayRoute r)
    {
        float rise = Rise(r);
        return r.Points.Select(p => p with { Y = p.Y + rise }).ToList();
    }

    /// <summary>A road as the map's road network takes it: as many three-metre lanes each way as fit, at its speed.</summary>
    internal static RoadData RoadOf(OverlayRoute r)
    {
        var line = LineOf(r);
        int each = Math.Max(1, (int)(r.WidthMetres / 2f / 3f));
        float lane = r.WidthMetres / 2f / each;
        var lanes = new List<LaneData>();
        for (int k = 0; k < each; k++)
        {
            float off = (k + 0.5f) * lane;
            lanes.Add(new LaneData { OffsetMetres = off, Direction = 1, WidthMetres = lane, SpeedLimitKmh = r.SpeedKmh });
            lanes.Add(new LaneData { OffsetMetres = -off, Direction = -1, WidthMetres = lane, SpeedLimitKmh = r.SpeedKmh });
        }
        return new RoadData
        {
            Id = r.Id, Name = r.Name, Type = r.SpeedKmh >= 50f ? "collector" : "residential", Centreline = line, WidthMetres = r.WidthMetres,
            Lanes = lanes, Surfaces = new List<SurfaceData> { new() { FromMetres = 0f, ToMetres = Length(line, false), Material = r.Surface } },
        };
    }

    internal static TrackData TrackOf(OverlayRoute r) => new()
    {
        Id = r.Id, Waypoints = LineOf(r), WidthMetres = r.WidthMetres, BankingDegrees = 0f,
        Stops = (r.Stations ?? new()).Select(st => new TrackStopData { AtMetres = st.AtMetres, DwellSeconds = 30f, Kind = "platform" }).ToList(),
    };

    internal static TrainData? TrainOf(OverlayRoute r)
        => r.IsRail && r.Train is { Length: > 0 } preset
            ? new TrainData { Name = $"{r.Name} train", Preset = preset, Track = r.Id, TopSpeedKmh = r.SpeedKmh }
            : null;

    internal static IEnumerable<LevelCrossingData> CrossingsOf(OverlayRoute r)
        => (r.Crossings ?? new()).Select(c => new LevelCrossingData { Name = c.Name, Position = c.Position });

    /// <summary>A map's routes laid onto its data as it loads: roads, tracks, crossings and trains, each once.</summary>
    internal static void LayRoutes(MapData map, MapOverlay o)
    {
        if (o.Routes == null) return;
        foreach (var r in o.Routes) AddData(map, r);
    }

    private static void AddData(MapData map, OverlayRoute r)
    {
        if (r.Kind == OverlayRoute.Road && !(map.Roads ?? new()).Any(x => x.Id == r.Id)) (map.Roads ??= new()).Add(RoadOf(r));
        if (!r.IsRail || (map.Tracks ?? new()).Any(x => x.Id == r.Id)) return;
        (map.Tracks ??= new()).Add(TrackOf(r));
        foreach (var c in CrossingsOf(r)) (map.Crossings ??= new()).Add(c);
        if (TrainOf(r) is { } train) (map.Trains ??= new()).Add(train);
    }

    private static void RemoveData(MapData map, OverlayRoute r)
    {
        map.Roads?.RemoveAll(x => x.Id == r.Id && r.Kind == OverlayRoute.Road);
        if (!r.IsRail) return;
        map.Tracks?.RemoveAll(x => x.Id == r.Id);
        foreach (var c in r.Crossings ?? new())
            map.Crossings?.RemoveAll(x => x.Name == c.Name && Vector3.Distance(x.Position, c.Position) < 0.01f);
        map.Trains?.RemoveAll(x => x.Track == r.Id);
    }

    /// <summary>A route laid or taken up, live: the overlay, the map's data, and its train.</summary>
    private void ApplyRoute(string mapId, OverlayRoute r, bool adding)
    {
        var o = Overlays.Get(mapId);
        o.Routes ??= new List<OverlayRoute>();
        o.Routes.RemoveAll(x => x.Id == r.Id);
        if (adding) o.Routes.Add(r);
        if (o.Routes.Count == 0) o.Routes = null;
        Overlays.Save(mapId);
        if (!_maps.TryGetMapData(mapId, out var map)) return;
        var train = TrainOf(r);
        if (train != null) _server.Rail.RemoveNamed(_maps, mapId, train.Name!);
        RemoveData(map, r);
        if (!adding) return;
        AddData(map, r);
        if (train != null && !_server.Rail.SpawnOne(_maps, mapId, train))
            Serilog.Log.Warning("WorldEditor: the train on {Route} on '{Map}' could not be put on its track.", r.Name, mapId);
    }

    private bool ReverseRoute(RouteOp op, bool forward, out string why)
    {
        why = "";
        bool there = Overlays.Get(op.MapId).Routes?.Any(x => x.Id == op.Route.Id) == true;
        bool laying = forward ? op.Adding : !op.Adding;
        if (laying && there) { why = $"{op.Route.Name} is laid already."; return false; }
        if (!laying && !there) { why = $"{op.Route.Name} has been taken up already."; return false; }
        ApplyRoute(op.MapId, op.Route, laying);
        return true;
    }

    // ── Laying one ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Joins the straight stretches of a walked line: a point is kept only where the way turns
    /// more than <see cref="RouteTolerance"/> off the line between its neighbours (Douglas and Peucker).</summary>
    internal static List<Vector3> Simplify(IReadOnlyList<Vector3> pts, float tolerance = RouteTolerance)
    {
        if (pts.Count < 3) return pts.ToList();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float worst = 0f; int at = -1;
            var pa = new Vector2(pts[a].X, pts[a].Z);
            var pb = new Vector2(pts[b].X, pts[b].Z);
            var ab = pb - pa;
            float len2 = ab.LengthSquared();
            for (int i = a + 1; i < b; i++)
            {
                var p = new Vector2(pts[i].X, pts[i].Z);
                float t = len2 > 1e-8f ? Math.Clamp(Vector2.Dot(p - pa, ab) / len2, 0f, 1f) : 0f;
                float d = Vector2.Distance(p, pa + ab * t);
                // A climb counts as a turn: a ramp keeps its top and bottom.
                float y = MathF.Abs(pts[i].Y - (pts[a].Y + (pts[b].Y - pts[a].Y) * t));
                d = MathF.Max(d, y);
                if (d > worst) { worst = d; at = i; }
            }
            if (at < 0 || worst <= tolerance) continue;
            keep[at] = true;
            stack.Push((a, at));
            stack.Push((at, b));
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    /// <summary>The point of a line nearest a place, and how far along it that is, metres.</summary>
    private static (Vector3 Point, float Along, float Away) NearestOn(IReadOnlyList<Vector3> pts, bool closed, Vector3 at)
    {
        var best = (Point: pts[0], Along: 0f, Away: float.MaxValue);
        float along = 0f;
        int n = closed && pts.Count > 2 ? pts.Count : pts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = pts[i], q = pts[(i + 1) % pts.Count];
            var seg = q - p;
            float len = Flat(seg);
            float t = len > 1e-4f ? Math.Clamp(((at.X - p.X) * seg.X + (at.Z - p.Z) * seg.Z) / (len * len), 0f, 1f) : 0f;
            var on = p + seg * t;
            float d = Flat(at - on);
            if (d < best.Away) best = (on, along + t * len, d);
            along += len;
        }
        return best;
    }

    /// <summary>Reads FIELD VALUE pairs onto a route: width, surface, level, height, depth, train, speed, name.</summary>
    private bool TryRouteFields(UserSession s, OverlayRoute r, string[] words, out string error)
    {
        error = "";
        string[] keys = { "width", "surface", "level", "height", "depth", "train", "speed", "name" };
        for (int i = 0; i < words.Length; i++)
        {
            string key = words[i].ToLowerInvariant();
            if (!keys.Contains(key)) { error = $"A route's fields are {string.Join(", ", keys)}; {words[i]} is not one."; return false; }
            if (key == "name")
            {
                int end = i + 1;
                while (end < words.Length && !keys.Contains(words[end].ToLowerInvariant())) end++;
                string name = string.Join(" ", words[(i + 1)..end]).Trim();
                if (name.Length == 0 || name.Length > 60) { error = "A name is 1 to 60 letters."; return false; }
                r.Name = name;
                i = end - 1;
                continue;
            }
            if (i + 1 >= words.Length) { error = $"Say a value after {key}."; return false; }
            string value = words[++i];
            switch (key)
            {
                case "width":
                    if (!TryNumber(value, out float w) || w < 0.5f || w > 60f) { error = "The width is 0.5 to 60 metres."; return false; }
                    r.WidthMetres = w;
                    break;
                case "surface":
                {
                    var surfaces = RouteSurfaces(s);
                    var match = surfaces.FirstOrDefault(m => m.Material.Equals(value, StringComparison.OrdinalIgnoreCase)).Material
                                ?? surfaces.FirstOrDefault(m => m.Material.StartsWith(value, StringComparison.OrdinalIgnoreCase)).Material;
                    if (match == null) { error = $"The surface is one of: {string.Join(", ", surfaces.Select(m => m.Material))}."; return false; }
                    r.Surface = match;
                    break;
                }
                case "level":
                    if (LevelOf(value) is not { } level) { error = "The level is ground, raised or underground."; return false; }
                    if (level != r.Level) r.LevelMetres = level == OverlayRoute.OnTheGround ? 0f : DefaultLevelMetres(level);
                    r.Level = level;
                    break;
                case "height":
                case "depth":
                    if (!TryNumber(value, out float h) || h < 3f || h > 60f) { error = $"The {key} is 3 to 60 metres."; return false; }
                    if (r.Level == OverlayRoute.OnTheGround) r.Level = key == "depth" ? OverlayRoute.Underground : OverlayRoute.Raised;
                    r.LevelMetres = h;
                    break;
                case "train":
                    if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) { r.Train = null; break; }
                    if (!TrainProfile.Presets.ContainsKey(value.ToLowerInvariant()))
                    { error = $"There is no train called {value}. Trains: {string.Join(", ", TrainProfile.Presets.Keys)}, or none."; return false; }
                    r.Train = value.ToLowerInvariant();
                    break;
                case "speed":
                    if (!TryNumber(value, out float v) || v < 5f || v > 300f) { error = "The speed is 5 to 300 km/h."; return false; }
                    r.SpeedKmh = v;
                    break;
            }
        }
        if (!r.IsRail) { r.Level = OverlayRoute.OnTheGround; r.LevelMetres = 0f; r.Train = null; }
        return true;
    }

    /// <summary>"a railway, Loop line: 6 points, 240 metres round, 1 station, raised 6 metres on pillars, light_rail train".</summary>
    private static string SayRoute(OverlayRoute r)
    {
        float length = Length(r.Points, r.IsRail);
        var parts = new List<string>
        {
            Plural(r.Points.Count, "point"),
            $"{Metres(MathF.Round(length))} {(r.IsRail ? "round" : "long")}",
            $"{FieldDescriptor.Format(r.WidthMetres)} metres wide, {r.Surface.ToLowerInvariant()}",
        };
        if (r.IsRail)
        {
            parts.Add(SayLevel(r));
            if (r.Stations is { Count: > 0 } st) parts.Add(Plural(st.Count, "station"));
            if (r.Crossings is { Count: > 0 } cr) parts.Add(Plural(cr.Count, "level crossing"));
            parts.Add(r.Train is { } t ? $"a {t.Replace('_', ' ')} train at up to {FieldDescriptor.Format(r.SpeedKmh)} km/h" : "no train");
        }
        else parts.Add($"{FieldDescriptor.Format(r.SpeedKmh)} km/h");
        return $"{Article(KindWord(r.Kind))}, {r.Name}: {string.Join(", ", parts)}";
    }

    /// <summary>/edit route ...: laying a road, path or railway, and the ones laid.</summary>
    private void RouteCommand(UserSession s, string[] args, Action<IMessage> reply)
    {
        if (args.Length > 0 && args[^1].Equals("dialog", StringComparison.OrdinalIgnoreCase)) args = args[..^1];
        var hand = HandOf(s);
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
        switch (verb)
        {
            case "":
                Say(reply, hand.Route is { } inHand ? $"Laying {SayRoute(inHand)}. /edit route finish lays it; /edit route cancel drops it."
                                               : "Nothing is being laid. /edit route start road, path or railway, and walk it; /edit routes lists those laid.");
                return;
            case "start":
            case "new":
                StartRoute(s, rest, atFeet: verb == "start", reply);
                return;
            case "point": RoutePoint(s, reply); return;
            case "points": RoutePoints(s, string.Join(" ", rest), reply); return;
            case "back":
            {
                if (hand.Route is not { } r) { Say(reply, NotLaying); return; }
                if (r.Points.Count == 0) { Say(reply, "There are no points to take back."); return; }
                r.Points.RemoveAt(r.Points.Count - 1);
                hand.RouteLast = r.Points.Count > 0 ? r.Points[^1] : (Vector3?)null;
                Say(reply, $"The last point is taken back: {Plural(r.Points.Count, "point")}, {Metres(MathF.Round(Length(r.Points, false)))}.");
                Refresh(s, reply);
                return;
            }
            case "set":
            {
                if (hand.Route is not { } r) { Say(reply, NotLaying); return; }
                if (!TryRouteFields(s, r, rest, out string error)) { Say(reply, error); return; }
                Say(reply, $"Laying {SayRoute(r)}.");
                Refresh(s, reply);
                return;
            }
            case "station":
            case "crossing":
                RouteMark(s, verb == "station", string.Join(" ", rest).Trim(), reply);
                return;
            case "finish":
            case "lay":
                FinishRoute(s, rest, reply);
                return;
            case "cancel":
                if (hand.Route == null) { Say(reply, NotLaying); return; }
                Say(reply, $"{hand.Route.Name} is not laid; its points are dropped.");
                hand.Route = null;
                Refresh(s, reply);
                return;
            case "remove":
                RemoveRoute(s, string.Join(" ", rest).Trim(), reply);
                return;
            case "goto":
                GoToRoute(s, string.Join(" ", rest).Trim(), reply);
                return;
            default:
                Say(reply, "Say /edit route start road|path|railway [FIELD VALUE ...], /edit route new ... (no point where you stand), /edit route point, "
                         + "/edit route points EAST NORTH; EAST NORTH ..., /edit route back, /edit route set FIELD VALUE, /edit route station [NAME], "
                         + "/edit route crossing [NAME], /edit route finish, /edit route cancel, /edit route remove NAME, /edit route goto NAME, /edit routes.");
                return;
        }
    }

    private const string NotLaying = "Nothing is being laid. /edit route start road, path or railway begins one.";

    private void StartRoute(UserSession s, string[] args, bool atFeet, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (hand.Route is { } laying) { Say(reply, $"You are laying {laying.Name} already: /edit route finish lays it, /edit route cancel drops it."); return; }
        if (args.Length == 0 || KindOf(args[0]) is not { } kind) { Say(reply, "Say /edit route start road, path or railway, then any of width, surface, level, height, depth, train, speed and name."); return; }
        if (!TryBody(s, reply, out _, out var feet, out _)) return;
        var o = Overlays.Get(s.CurrentMapId);
        int n = (o.Routes?.Count(x => x.Kind == kind) ?? 0) + 1;
        var r = new OverlayRoute
        {
            Kind = kind, Name = $"{Capital(KindWord(kind))} {n}", WidthMetres = DefaultWidth(kind), Surface = DefaultSurface(kind),
            SpeedKmh = DefaultSpeed(kind),
        };
        if (!RouteSurfaces(s).Any(m => m.Material == r.Surface)) r.Surface = RouteSurfaces(s).FirstOrDefault().Material ?? r.Surface;
        if (!TryRouteFields(s, r, args[1..], out string error)) { Say(reply, error); return; }
        if (atFeet) r.Points.Add(feet);
        hand.Route = r;
        hand.RouteLast = atFeet ? feet : null;
        hand.RouteSaid = 0f;
        Say(reply, atFeet
            ? $"Laying {Article(KindWord(kind))}, {r.Name}. The first point is where you stand. Close the editor and walk its way: a point is dropped every metre, "
              + $"and every {Metres(RouteSayEvery)} you are told how far it has come. /edit route finish lays it; the straight stretches are joined up."
            : $"Laying {Article(KindWord(kind))}, {r.Name}, from points typed: /edit route points EAST NORTH; EAST NORTH ...");
        Refresh(s, reply);
    }

    /// <summary>A point added to the route in hand, if it is far enough from the last; false when the route is full.</summary>
    private static bool AddPoint(Hand hand, Vector3 p)
    {
        var r = hand.Route!;
        if (r.Points.Count >= RouteMaxPoints || Length(r.Points, false) >= RouteMaxMetres) return false;
        r.Points.Add(p);
        hand.RouteLast = p;
        return true;
    }

    private void RoutePoint(UserSession s, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (hand.Route is not { } r) { Say(reply, NotLaying); return; }
        if (!TryBody(s, reply, out _, out var feet, out _)) return;
        if (!AddPoint(hand, feet)) { Say(reply, $"A route is at most {RouteMaxPoints} points and {FieldDescriptor.Format(RouteMaxMetres / 1000f)} kilometres."); return; }
        Say(reply, $"Point {r.Points.Count}, {Metres(MathF.Round(Length(r.Points, false)))} from the start.");
        Refresh(s, reply);
    }

    /// <summary>/edit route points E N [U]; E N [U]; ...: points typed in player coordinates; a point without
    /// a height is on the ground there, found from your own level.</summary>
    private void RoutePoints(UserSession s, string typed, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (hand.Route is not { } r) { Say(reply, NotLaying); return; }
        if (!TryBody(s, reply, out var world, out var feet, out _)) return;
        if (!_maps.TryGetMap(s.CurrentMapId, out _, out _, out var grid, out _)) return;
        var added = new List<Vector3>();
        foreach (var part in typed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var nums = part.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var v = new float[3];
            if (nums.Length is < 2 or > 3 || Enumerable.Range(0, nums.Length).Any(i => !TryNumber(nums[i], out v[i])))
            { Say(reply, $"Each point is east and north in metres, and its height if you like, apart by semicolons: 0 0; 50 0; 50 40. {part} is not one."); return; }
            var p = PlayerCoordinates.ToWorld(v[0], v[1], nums.Length == 3 ? v[2] : feet.Y);
            if (!InReach(p)) { Say(reply, $"Not added: {TooFar}"); return; }
            if (nums.Length == 2)
            {
                float ground = PhysicsUtils.GetGroundHeight(world, grid, p with { Y = feet.Y + 1f }, out _);
                p.Y = ground < -500f ? feet.Y : ground;
            }
            added.Add(p);
        }
        if (added.Count == 0) { Say(reply, "Say the points: east and north in metres, apart by semicolons: 0 0; 50 0; 50 40."); return; }
        foreach (var p in added)
            if (!AddPoint(hand, p)) { Say(reply, $"A route is at most {RouteMaxPoints} points and {FieldDescriptor.Format(RouteMaxMetres / 1000f)} kilometres."); return; }
        Say(reply, $"{Plural(added.Count, "point")} added: {Plural(r.Points.Count, "point")}, {Metres(MathF.Round(Length(r.Points, r.IsRail)))} {(r.IsRail ? "round" : "long")}.");
        Refresh(s, reply);
    }

    /// <summary>/edit route station|crossing [NAME]: a station or a level crossing on the railway in hand, at
    /// the point of it nearest you.</summary>
    private void RouteMark(UserSession s, bool station, string name, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (hand.Route is not { } r) { Say(reply, NotLaying); return; }
        if (!r.IsRail) { Say(reply, "Stations and level crossings are on a railway."); return; }
        if (r.Points.Count < 2) { Say(reply, "Lay at least two points of the line first."); return; }
        if (!TryBody(s, reply, out _, out var feet, out _)) return;
        var (point, along, away) = NearestOn(r.Points, closed: r.Points.Count > 2, feet);
        if (station)
        {
            r.Stations ??= new List<OverlayStation>();
            string n = name.Length > 0 ? name : $"Station {r.Stations.Count + 1}";
            r.Stations.Add(new OverlayStation { Name = n, AtMetres = along });
            Say(reply, $"{n}: trains stop here, {Metres(MathF.Round(along))} round the line{(away > 1f ? $", {Metres(MathF.Round(away))} from you" : "")}.");
        }
        else
        {
            r.Crossings ??= new List<OverlayCrossing>();
            string n = name.Length > 0 ? name : $"Level crossing {r.Crossings.Count + 1}";
            r.Crossings.Add(new OverlayCrossing { Name = n, Position = point });
            Say(reply, $"{n}: a level crossing, {Metres(MathF.Round(along))} round the line{(away > 1f ? $", {Metres(MathF.Round(away))} from you" : "")}. Its bells and gates come with the next load of the map.");
        }
        Refresh(s, reply);
    }

    /// <summary>The editor's walking: a point dropped every metre each editor laying a route walks, and how far
    /// it has come said now and then. Called a few times a second from the server's tick.</summary>
    public void Tick()
    {
        foreach (var (name, hand) in _hands)
        {
            if (hand.Route == null || hand.RouteLast is not { } last) continue;
            var s = _sessions.GetAllSessions().FirstOrDefault(x => x.Username.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s == null || !s.CurrentMapId.Equals(hand.MapId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryBody(s, _ => { }, out _, out var feet, out _)) continue;
            if (Flat(feet - last) < RouteSampleMetres * 0.99f) continue;
            if (!AddPoint(hand, feet)) continue;
            float length = Length(hand.Route.Points, false);
            if (length - hand.RouteSaid >= RouteSayEvery)
            {
                hand.RouteSaid = MathF.Floor(length / RouteSayEvery) * RouteSayEvery;
                _server.SendToSession(s, new TextEvent { Text = $"{Metres(hand.RouteSaid)}." });
            }
        }
    }

    /// <summary>One piece of a route: a prefab placed for it, sized and turned, kept as an addition.</summary>
    private bool Piece(UserSession s, OverlayRoute r, PrefabTemplate t, Vector3 at, float yaw, Vector3 size, string name, List<EditOp> ops, out string why)
    {
        var pose = new Pose(at, Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f), ScaleFor(t, size));
        if (!PlaceOne(s.CurrentMapId, t, pose, name, null, out var thing, out why)) return false;
        ops.Add(new PlaceOp(s.CurrentMapId, thing, "laid", name));
        r.Pieces.Add(thing.Id);
        return true;
    }

    /// <summary>
    /// What a route is made of, segment by segment: a road's or path's surface; a railway's bed on the
    /// ground, its deck and pillars raised, or its tunnel (floor, walls and roof) underground; a platform at
    /// each station not underground. The surface is needed and nothing is laid without it; a pillar or a
    /// platform where somebody stands is left out and said.
    /// </summary>
    private bool LayPieces(UserSession s, OverlayRoute r, List<EditOp> ops, List<string> notes, out string why)
    {
        why = "";
        var surfaces = RouteSurfaces(s);
        var surface = surfaces.FirstOrDefault(m => m.Material == r.Surface).Prefab;
        var concrete = surfaces.FirstOrDefault(m => m.Material == "Concrete").Prefab ?? surface;
        var wall = BuildMaterials(s, "wall").FirstOrDefault(m => m.Material == "Concrete").Prefab;
        var pillar = _maps.Prefabs.TryGetValue("pillar_round", out var pr) ? pr : wall;
        if (surface == null || concrete == null) { why = $"there is no {r.Surface.ToLowerInvariant()} floor to lay it with."; return false; }
        if (r.Level == OverlayRoute.Underground && wall == null) { why = "there is no concrete wall to make a tunnel of."; return false; }

        var pts = r.Points;
        int segments = r.IsRail ? pts.Count : pts.Count - 1;
        int skippedPillars = 0;
        for (int i = 0; i < segments; i++)
        {
            Vector3 a = pts[i], b = pts[(i + 1) % pts.Count];
            float len = Flat(b - a);
            if (len < 0.05f) continue;
            float yaw = MathF.Atan2(b.X - a.X, b.Z - a.Z);
            var mid = (a + b) * 0.5f;
            var side = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));   // to the right of the way
            float overlap = len + r.WidthMetres * 0.5f;                      // the joints overlap, so a bend has no gap
            float thick = surface.ColliderSize!.Value.Y;
            string label = r.Kind switch { OverlayRoute.Path => $"{r.Name}, pavement", OverlayRoute.Road => r.Name, _ => r.Name };
            switch (r.IsRail ? r.Level : "")
            {
                case "":
                    if (!Piece(s, r, surface, mid with { Y = mid.Y + SurfaceRise - thick * 0.5f }, yaw, new Vector3(r.WidthMetres, thick, overlap), label, ops, out why)) return false;
                    break;
                case OverlayRoute.OnTheGround:
                    if (!Piece(s, r, surface, mid with { Y = mid.Y + SurfaceRise - thick * 0.5f }, yaw, new Vector3(r.WidthMetres, thick, overlap), $"{label}, track bed", ops, out why)) return false;
                    break;
                case OverlayRoute.Raised:
                {
                    float deck = 0.5f, top = mid.Y + r.LevelMetres;
                    if (!Piece(s, r, concrete, mid with { Y = top - deck * 0.5f }, yaw, new Vector3(r.WidthMetres, deck, overlap), $"{label}, deck", ops, out why)) return false;
                    int count = Math.Max(1, (int)MathF.Ceiling(len / PillarSpacing));
                    for (int k = 0; k < count; k++)
                    {
                        var at = a + (b - a) * ((k + 0.5f) / count);
                        float h = r.LevelMetres - deck;
                        if (!Piece(s, r, pillar!, at with { Y = at.Y + h * 0.5f }, yaw, new Vector3(0.8f, h, 0.8f), $"{label}, pillar", ops, out _)) skippedPillars++;
                    }
                    break;
                }
                case OverlayRoute.Underground:
                {
                    float inside = MathF.Max(r.WidthMetres, TunnelMinWidth);
                    float floorTop = mid.Y - r.LevelMetres;
                    float across = inside + 2f * Concrete;
                    if (!Piece(s, r, concrete, mid with { Y = floorTop - Concrete * 0.5f }, yaw, new Vector3(across, Concrete, overlap), $"{label}, tunnel floor", ops, out why)) return false;
                    if (!Piece(s, r, concrete, mid with { Y = floorTop + TunnelHeight + Concrete * 0.5f }, yaw, new Vector3(across, Concrete, overlap), $"{label}, tunnel roof", ops, out why)) return false;
                    foreach (float sgn in new[] { -1f, 1f })
                    {
                        var at = mid + side * sgn * (inside * 0.5f + Concrete * 0.5f);
                        if (!Piece(s, r, wall!, at with { Y = floorTop + TunnelHeight * 0.5f }, yaw, new Vector3(Concrete, TunnelHeight, len), $"{label}, tunnel wall", ops, out why)) return false;
                    }
                    break;
                }
            }
        }
        if (skippedPillars > 0) notes.Add($"{Plural(skippedPillars, "pillar")} where somebody stands left out");

        // A platform beside the line at each station, on its right, a train's floor high.
        int skippedPlatforms = 0;
        foreach (var st in r.IsRail ? r.Stations ?? new() : new())
        {
            if (r.Level == OverlayRoute.Underground) { notes.Add($"{st.Name} is underground, so it has a stop but no platform or stairs yet"); continue; }
            var line = LineOf(r);
            if (!PointAlong(line, st.AtMetres, out var at, out float yaw)) continue;
            var side = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
            float top = at.Y + PlatformRise;
            float bottom = r.Level == OverlayRoute.Raised ? top - 0.5f : at.Y - Rise(r);
            var centre = at + side * (r.WidthMetres * 0.5f + PlatformWidth * 0.5f + 0.3f);
            centre.Y = (top + bottom) * 0.5f;
            if (!Piece(s, r, concrete, centre, yaw, new Vector3(PlatformWidth, top - bottom, PlatformLength), $"{st.Name}, platform", ops, out _)) skippedPlatforms++;
        }
        if (skippedPlatforms > 0) notes.Add($"{Plural(skippedPlatforms, "platform")} where somebody stands left out");
        return true;
    }

    /// <summary>Where on a loop a distance round it is, and which way the line runs there.</summary>
    private static bool PointAlong(IReadOnlyList<Vector3> pts, float metres, out Vector3 at, out float yaw)
    {
        at = default; yaw = 0f;
        if (pts.Count < 2) return false;
        float total = Length(pts, closed: pts.Count > 2);
        if (total <= 0f) return false;
        float left = ((metres % total) + total) % total;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 p = pts[i], q = pts[(i + 1) % pts.Count];
            float len = Flat(q - p);
            if (left <= len || i == pts.Count - 1)
            {
                float t = len > 1e-4f ? Math.Clamp(left / len, 0f, 1f) : 0f;
                at = p + (q - p) * t;
                yaw = MathF.Atan2(q.X - p.X, q.Z - p.Z);
                return true;
            }
            left -= len;
        }
        return false;
    }

    /// <summary>A route id for the map's data from its name, made unique on the map.</summary>
    private string RouteId(string mapId, OverlayRoute r)
    {
        string slug = new string(r.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (slug.Length == 0) slug = r.Kind;
        string id = $"editor_{slug}";
        _maps.TryGetMapData(mapId, out var map);
        bool Taken(string x) => (Overlays.Get(mapId).Routes?.Any(y => y.Id == x) ?? false)
                                || (map?.Roads?.Any(y => y.Id == x) ?? false) || (map?.Tracks?.Any(y => y.Id == x) ?? false);
        for (int n = 2; Taken(id); n++) id = $"editor_{slug}_{n}";
        return id;
    }

    /// <summary>/edit route finish [FIELD VALUE ...]: the route in hand laid, its pieces and its data, as one undo.</summary>
    private void FinishRoute(UserSession s, string[] args, Action<IMessage> reply)
    {
        var hand = HandOf(s);
        if (hand.Route is not { } r) { Say(reply, NotLaying); return; }
        if (!TryRouteFields(s, r, args, out string error)) { Say(reply, error); return; }
        var pts = Simplify(r.Points);
        // A loop's last point on its first is one point.
        if (r.IsRail && pts.Count > 3 && Flat(pts[^1] - pts[0]) < 1f) pts.RemoveAt(pts.Count - 1);
        int need = r.IsRail ? 3 : 2;
        if (pts.Count < need)
        {
            Say(reply, r.IsRail ? "A railway is a loop of at least three points that are not in a line: walk it round, or type its corners."
                                : "A road or path needs at least two points: walk it, or type its ends.");
            return;
        }
        if (r.IsRail)
        {
            try { _ = new RaceLine(pts, 0f, MathF.Max(1f, r.SpeedKmh) / 3.6f, 0.1f, 1f); }
            catch (Exception ex) { Say(reply, $"A train could not run that loop: {ex.Message}"); return; }
        }
        string mapId = s.CurrentMapId;
        var laid = new OverlayRoute
        {
            Id = RouteId(mapId, r), Kind = r.Kind, Name = r.Name, Points = pts, WidthMetres = r.WidthMetres, Surface = r.Surface,
            Level = r.Level, LevelMetres = r.LevelMetres, SpeedKmh = r.SpeedKmh, Train = r.Train, LaidBy = s.Username, LaidAt = DateTime.UtcNow,
            // Where a station is round the line, measured again on the line as laid.
            Stations = r.Stations?.Select(st => new OverlayStation
            {
                Name = st.Name, AtMetres = PointAlong(r.Points, st.AtMetres, out var at, out _) ? NearestOn(pts, true, at).Along : st.AtMetres,
            }).ToList(),
            Crossings = r.Crossings?.Select(c => new OverlayCrossing { Name = c.Name, Position = c.Position with { Y = c.Position.Y + Rise(r) } }).ToList(),
        };
        var ops = new List<EditOp>();
        var notes = new List<string>();
        int segments = laid.IsRail ? pts.Count : pts.Count - 1;
        int estimate = segments * (laid.Level == OverlayRoute.Underground ? 4 : laid.Level == OverlayRoute.Raised ? 3 : 1) + (laid.Stations?.Count ?? 0);
        if (Full(s, estimate, out string full)) { Say(reply, full); return; }
        if (!LayPieces(s, laid, ops, notes, out string why))
        {
            for (int i = ops.Count - 1; i >= 0; i--) Reverse(s, ops[i], forward: false, out _, nested: true);
            Say(reply, $"Not laid: {why}");
            return;
        }
        var routeOp = new RouteOp(mapId, laid, Adding: true);
        ApplyRoute(mapId, laid, adding: true);
        ops.Add(routeOp);
        Push(s, new BatchOp(mapId, ops, $"laid {laid.Name}"));
        hand.Route = null;
        string use = laid.Kind switch
        {
            OverlayRoute.Road => " Traffic can follow it from the next time the map loads; it is not joined to other roads.",
            OverlayRoute.Path => " People walking and characters use it as a pavement.",
            _ => laid.Train != null ? $" {laid.Name} train runs it now." : " No train runs it: set a train, or lay it again with one.",
        };
        Say(reply, $"Laid {SayRoute(laid)}.{use}" + (notes.Count > 0 ? $" {Capital(string.Join("; ", notes))}." : "") + " One undo takes it up.");
        Notify(s, $"{s.Username} laid {laid.Name}.");
        Serilog.Log.Information("WorldEditor: {User} laid {Kind} {Name} ({Id}) on '{Map}': {Points} points, {Pieces} pieces.",
                                s.Username, laid.Kind, laid.Name, laid.Id, mapId, laid.Points.Count, laid.Pieces.Count);
        Refresh(s, reply);
    }

    // ── Those laid ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A route laid on this map, by its id or its name (whole, or the start of it).</summary>
    private OverlayRoute? RouteNamed(string mapId, string words)
    {
        var all = Overlays.Get(mapId).Routes ?? new List<OverlayRoute>();
        return all.FirstOrDefault(r => r.Id.Equals(words, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(r => r.Name.Equals(words, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(r => r.Name.StartsWith(words, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A laid route as a row: "Loop line, a railway, 240 metres round, ..., 30 metres north east, laid by cody".</summary>
    private string RouteRow(OverlayRoute r, Vector3 feet)
    {
        var (point, _, _) = NearestOn(r.Points, r.IsRail, feet);
        string when = r.LaidBy == null ? "" : $", laid by {r.LaidBy}{(r.LaidAt is { } t ? $", {t.ToLocalTime():d MMMM HH:mm}" : "")}";
        return $"{SayRoute(r)}, nearest {PointWhere(point, feet)}{when}";
    }

    private void SayRoutes(UserSession s, Action<IMessage> reply)
    {
        var all = Overlays.Get(s.CurrentMapId).Routes ?? new List<OverlayRoute>();
        if (all.Count == 0) { Say(reply, "No roads, paths or railways have been laid on this map with the editor. /edit route start road, path or railway begins one."); return; }
        if (!s.IsTextClient) { SendMenu(s, "routes", reply, refresh: false); return; }
        if (!TryBody(s, reply, out _, out var feet, out _)) return;
        Say(reply, "Laid on this map: " + string.Join("; ", all.Select(r => $"{r.Id}: {RouteRow(r, feet)}")) + ". /edit route remove NAME takes one up.");
    }

    private void RemoveRoute(UserSession s, string which, Action<IMessage> reply)
    {
        string mapId = s.CurrentMapId;
        if (which.Length == 0 || RouteNamed(mapId, which) is not { } r) { Say(reply, $"Nothing called {which} has been laid here. /edit routes lists them."); return; }
        if (!TryBody(s, reply, out var world, out _, out _)) return;
        var authored = _maps.AuthoredEntities(mapId);
        var ops = new List<EditOp> { new RouteOp(mapId, r, Adding: false) };
        foreach (int id in r.Pieces)
            if (authored.TryGetValue(id, out var e) && world.IsAlive(e) && Overlays.Get(mapId).AdditionFor(id) != null)
                ops.Add(new DeleteOp(mapId, Take(mapId, world, e, id), NameOf(world, e)));
        var batch = new BatchOp(mapId, ops, $"took up {r.Name}");
        if (!Reverse(s, batch, forward: true, out string why)) { Say(reply, $"Not taken up: {why}"); return; }
        Push(s, batch);
        Say(reply, $"Took up {r.Name} and {Plural(ops.Count - 1, "piece")} of it. Undo lays it again.");
        Notify(s, $"{s.Username} took up {r.Name}.");
        Refresh(s, reply);
    }

    private void GoToRoute(UserSession s, string which, Action<IMessage> reply)
    {
        if (!MayGo(s)) { Say(reply, "Going to a thing needs the move permission here, as /move does: your own map, or a developer's."); return; }
        string mapId = s.CurrentMapId;
        if (which.Length == 0 || RouteNamed(mapId, which) is not { } r) { Say(reply, $"Nothing called {which} has been laid here. /edit routes lists them."); return; }
        if (!TryBody(s, reply, out var world, out var feet, out _)) return;
        if (!_maps.TryGetMap(mapId, out _, out _, out var grid, out _)) return;
        var (point, _, _) = NearestOn(LineOf(r), r.IsRail, feet);
        var half = new Vector3(r.WidthMetres * 0.5f, 0.5f, r.WidthMetres * 0.5f);
        if (SpotBeside(world, grid, point - half, point + half, feet) is not { } spot) { Say(reply, $"There is no room to stand beside {r.Name}."); return; }
        float face = MathF.Atan2(point.X - spot.X, point.Z - spot.Z);
        if (world.Has<OccupantComponent>(s.Entity)) CompositeService.Disembark(world, s.Entity);
        ref var tr = ref world.Get<Transform>(s.Entity);
        tr.Position = spot;
        tr.Rotation = Quaternion.CreateFromYawPitchRoll(face, 0f, 0f);
        tr.IsDirty = true;
        if (world.Has<PlayerComponent>(s.Entity)) world.Get<PlayerComponent>(s.Entity).Yaw = face;
        _server.SendToSession(s, new PlayerSpawned { EntityId = s.Entity.Id, SpawnTransform = tr });
        Say(reply, $"You are beside {r.Name}, facing it.");
        Refresh(s, reply);
    }

    // ── Menus and the dialog ────────────────────────────────────────────────────────────────────

    private EditorMenu RoutesMenu(UserSession s)
    {
        var items = new List<EditorMenuItem>();
        var hand = HandOf(s);
        if (hand.Route is { } r)
        {
            items.Add(Info($"Laying {SayRoute(r)}"));
            items.Add(Act("Drop a point where you stand", "edit route point"));
            items.Add(Typed("Add points, typed", "/edit route points ", "points: east and north in metres, apart by semicolons", "Such as 0 0; 50 0; 50 40."));
            if (r.IsRail)
            {
                items.Add(Act("Add a station where you stand", "edit route station"));
                items.Add(Act("Add a level crossing where you stand", "edit route crossing"));
            }
            items.Add(Act("Take back the last point", "edit route back"));
            items.Add(Act("Lay it", "edit route finish", stay: false));
            items.Add(Act("Cancel: lay nothing", "edit route cancel"));
        }
        else
            foreach (var kind in RouteKinds)
            {
                items.Add(Act($"Start {Article(KindWord(kind))} here, then walk it", $"edit route start {kind}", stay: false));
                items.Add(Typed($"Start {Article(KindWord(kind))}, typed: its fields", $"/edit route start {kind} ", "fields: width, surface, level, height, depth, train, speed, name",
                                "Such as width 7 surface asphalt name High Street. Leave it empty for the usual."));
            }
        if (TryBody(s, _ => { }, out _, out var feet, out _))
            foreach (var laid in Overlays.Get(s.CurrentMapId).Routes ?? new List<OverlayRoute>())
                items.Add(Opens(RouteRow(laid, feet), $"route:{laid.Id}"));
        return Menu("Roads, paths and railways", items);
    }

    private EditorMenu? RouteMenu(UserSession s, string id)
    {
        if (RouteNamed(s.CurrentMapId, id) is not { } r || !TryBody(s, _ => { }, out _, out var feet, out _)) return null;
        var items = new List<EditorMenuItem> { Info(RouteRow(r, feet)), Act("Take it up", $"edit route remove {r.Id}", stay: false) };
        if (MayGo(s)) items.Add(Act("Go to it", $"edit route goto {r.Id}", stay: false));
        return Menu(r.Name, items);
    }

    /// <summary>The Place tab's route form: what is being laid (a line, or none), the surfaces, levels and trains to choose from.</summary>
    private IEnumerable<EditorMenuItem> DialogRoutes(UserSession s)
    {
        var hand = HandOf(s);
        yield return Line("place.routeinfo", hand.Route is { } r ? $"Laying {SayRoute(r)}" : "Nothing is being laid",
                          hand.Route?.Kind ?? "", hand.Route == null ? "" : $"{hand.Route.Points.Count}");
        foreach (var (material, prefab) in RouteSurfaces(s))
            yield return Line("place.routesurface", $"{material}, {prefab.Name}", material);
        foreach (var key in TrainProfile.Presets.Keys.OrderBy(k => k, StringComparer.Ordinal))
            yield return Line("place.routetrain", key.Replace('_', ' '), key);
    }

    /// <summary>The World tab's list of what was laid.</summary>
    private IEnumerable<EditorMenuItem> DialogLaid(UserSession s)
    {
        if (!TryBody(s, _ => { }, out _, out var feet, out _)) yield break;
        foreach (var r in Overlays.Get(s.CurrentMapId).Routes ?? new List<OverlayRoute>())
            yield return Line("world.route", RouteRow(r, feet), r.Id, prompt: r.Name);
    }
}
