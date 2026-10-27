using System.Numerics;
using System.Reflection;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// An instrument, not a test (2026-10-27): which of the faults Resonance found in its copy of the traffic
/// (inbox/2026-10-18-resonance-host-needs, "Traffic built in Resonance's copy") open-fps's own traffic
/// has. It runs the real server traffic headless on a real map and counts events; a probe that finds a
/// fault reports it and passes. Runs only with OPENFPS_TRAFFIC_PROBE=1 (a few minutes of simulated
/// traffic on several seeds takes longer than a minute); OPENFPS_TRAFFIC_PROBE_SECONDS sets the length
/// of each run (default 300 s). A seed k other than 0 changes every route's seed by 101 k and every
/// start by 17 k metres, so the tours and the placing differ; seed 0 is the map as it ships.
/// Reads the vehicles by reflection: DemoVehicle is private to VehicleSystem.
/// </summary>
public class TrafficFaultProbe
{
    private const float Dt = 1f / 30f;
    private readonly ITestOutputHelper _o;
    public TrafficFaultProbe(ITestOutputHelper o) => _o = o;

    private static bool Enabled => System.Environment.GetEnvironmentVariable("OPENFPS_TRAFFIC_PROBE") == "1";
    private static float Seconds => float.TryParse(System.Environment.GetEnvironmentVariable("OPENFPS_TRAFFIC_PROBE_SECONDS"), out var s) ? s : 300f;

    [Fact]
    [Trait("Category", "Probe")]
    public void Probe_city_traffic_faults()
    {
        if (!Enabled) return;
        var total = new Tally("city");
        foreach (int seed in new[] { 0, 1, 2, 3 }) Run("city", "city.json", "maps", seed, Seconds, total);
        total.Report(_o);
    }

    [Fact]
    [Trait("Category", "Probe")]
    public void Probe_magnolia_traffic_faults()
    {
        if (!Enabled) return;
        var total = new Tally("magnolia_tx");
        foreach (int seed in new[] { 0, 1 }) Run("magnolia_tx", "magnolia_tx.json", "places", seed, Seconds, total);
        total.Report(_o);
    }

    [Fact]
    [Trait("Category", "Probe")]
    public void Probe_albany_traffic_faults()
    {
        if (!Enabled) return;
        var total = new Tally("albany_or");
        foreach (int seed in new[] { 0, 1 }) Run("albany_or", "albany_or.json", "places", seed, Seconds, total);
        total.Report(_o);
    }

    /// <summary>
    /// Fault 1 and 8 made as hard as they can be: two vehicles on one tour of the city, the follower spawned at
    /// its full lane speed this many metres behind a lead of 20 km/h, with nothing else on the map. Counts
    /// footprint overlaps and the least gap, nose to tail, over the first 60 s.
    /// </summary>
    [Fact]
    [Trait("Category", "Probe")]
    public void Probe_spawned_at_speed_behind_a_slower_one()
    {
        if (!Enabled) return;
        foreach (float gap in new[] { 4f, 8f, 12f, 20f, 40f })
        {
            var (maps, world, vehicles, folder) = Load("city", "city.json", "maps", 0, data =>
            {
                var template = data.Vehicles!.First(v => v.Route != null && v.Preset == "i4_compact");
                VehicleData Copy(string name, float start, float kmh) => new()
                {
                    Name = name, Preset = template.Preset, TopSpeedKmh = kmh, CorneringG = template.CorneringG, GripG = template.GripG,
                    AccelerationMps2 = template.AccelerationMps2, BrakingMps2 = template.BrakingMps2, StartOffsetMetres = start,
                    Route = new RouteData { StartRoad = template.Route!.StartRoad, Direction = template.Route.Direction, Seed = template.Route.Seed, WanderMetres = template.Route.WanderMetres },
                };
                data.Vehicles = new List<VehicleData> { Copy("Lead", 4.6f + gap, 20f), Copy("Follower", 0f, 52f) };
            });
            try
            {
                float least = float.MaxValue, maxDecel = 0f, prev = float.NaN;
                int overlapTicks = 0;
                for (int tick = 0; tick <= 60 * 30; tick++)
                {
                    if (tick > 0) vehicles.Update("city", world, Dt);
                    var d = vehicles.DriversForTest("city", world).ToDictionary(x => x.Name);
                    var (l, f) = (d["Lead"], d["Follower"]);
                    float centres = Vector3.Distance(l.Position, f.Position);
                    least = MathF.Min(least, centres - 0.5f * (l.Length + f.Length));
                    if (BoxesOverlap(l.Position, l.Heading, l.Length, f.Position, f.Heading, f.Length)) overlapTicks++;
                    if (tick > 0) maxDecel = MathF.Max(maxDecel, (prev - f.Speed) / Dt);
                    prev = f.Speed;
                    if (tick == 0) _o.WriteLine($"gap {gap:F0} m: at tick 0 the lead goes {l.Speed:F1} m/s, the follower {f.Speed:F1} m/s");
                }
                _o.WriteLine($"gap {gap:F0} m: least nose-to-tail gap {least:F1} m; overlapping ticks {overlapTicks}; follower's hardest braking {maxDecel:F1} m/s2");
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }
    }

    /// <summary>The roads' own shape, which says what kinds of fault can arise at all.</summary>
    [Fact]
    [Trait("Category", "Probe")]
    public void Probe_what_the_road_network_has()
    {
        if (!Enabled) return;
        foreach (var (id, file, dir) in new[] { ("city", "city.json", "maps"), ("magnolia_tx", "magnolia_tx.json", "places"), ("albany_or", "albany_or.json", "places") })
        {
            var (maps, _, _, folder) = Load(id, file, dir, 0);
            try
            {
                Assert.True(maps.TryGetRoads(id, out var net));
                int junctions = 0, mergeJunctions = 0, mergePairs = 0, deadEnds = net.DeadEnds;
                foreach (var j in net.Junctions)
                {
                    junctions++;
                    var arriving = net.Segments.Where(s => s.To == j).ToList();
                    bool any = false;
                    for (int a = 0; a < arriving.Count; a++)
                        for (int b = a + 1; b < arriving.Count; b++)
                        {
                            var (p, q) = (arriving[a], arriving[b]);
                            if (p.Road != q.Road || Math.Sign(p.Lane.Direction) != Math.Sign(q.Lane.Direction)) continue;
                            if (p.Next.Any(n => q.Next.Any(m => m.Next == n.Next))) { mergePairs++; any = true; }
                        }
                    if (any) mergeJunctions++;
                }
                float maxLimit = net.Segments.Max(s => s.Lane.SpeedLimitKmh);
                _o.WriteLine($"{id}: {net.Roads.Count} roads, {net.Segments.Count} lane segments, {junctions} junctions, {deadEnds} dead ends; "
                           + $"{mergeJunctions} junctions where two lanes of one approach lead to the same lane out ({mergePairs} pairs); "
                           + $"fastest lane {maxLimit:F0} km/h; look ahead {JunctionRules.LookMetres:F0} m = {JunctionRules.LookMetres / (maxLimit / 3.6f):F1} s at that speed");
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }
    }

    // ── Loading ───────────────────────────────────────────────────────────────────────────────

    private static (MapManager Maps, World World, VehicleSystem Vehicles, string Folder) Load(string id, string file, string dir, int seed, Action<MapData>? change = null)
    {
        string folder = Path.Combine(Path.GetTempPath(), "openfps-trafficprobe-" + Guid.NewGuid().ToString("N"));
        string places = Path.Combine(folder, "places");
        Directory.CreateDirectory(places);
        string from = Path.Combine(AppContext.BaseDirectory, dir, file);
        File.Copy(from, dir == "places" ? Path.Combine(places, file) : Path.Combine(folder, file));
        if (dir == "places") RealPlaceMapTests.CopyGround(id, places);
        var prefabs = new PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new MapManager(new MapRepository(folder), prefabs);
        maps.Initialize();
        Assert.True(maps.TryGetMapData(id, out var data));
        if (seed != 0)
            foreach (var v in data.Vehicles ?? new())
                if (v.Route != null) { v.Route.Seed += 101 * seed; v.StartOffsetMetres += 17f * seed; }
        change?.Invoke(data);
        var vehicles = new VehicleSystem();
        vehicles.SpawnMap(maps, null, id);
        Assert.True(maps.TryGetMap(id, out World world, out _, out _, out _));
        return (maps, world, vehicles, folder);
    }

    // ── What is counted ───────────────────────────────────────────────────────────────────────

    private sealed class Tally
    {
        public readonly string Map;
        public Tally(string map) => Map = map;
        public double VehicleSeconds;
        public int Overlaps, OverlapsAtPlacing, OverlapsInJunction, OverlapsOnLane, SpawnOverlapsTick0, SpawnFrontOverLine, SpawnFrontOverGiveWay;
        public int HoldEpisodes, HoldOverLine, HoldOverLine2m, HoldLong, HoldLongOverLine, HoldOnPriority, HoldPriorityBySideBySide, HoldSideBySide;
        public int BreakerEpisodes, BreakerEpisodesBelowCritical;
        public int Releases, ReleasesByBreaker, ReHoldsAfterGap, ReHoldsAfterBreaker, BreakerBelowCritical, StopOverLineAfterRelease;
        public int HardDecelTicks, VeryHardDecelTicks;
        public float MaxDecel;
        public readonly List<string> Notes = new();
        public readonly SortedDictionary<string, int> HoldByPreset = new(), HoldOverByPreset = new();

        public void Report(ITestOutputHelper o)
        {
            o.WriteLine($"=== {Map}: all seeds, {VehicleSeconds / 60:F0} vehicle-minutes on routes ===");
            o.WriteLine($"[1,8] body overlaps (episodes, any heading): {Overlaps}; in the first 20 s: {OverlapsAtPlacing}; at a junction: {OverlapsInJunction}; on a lane: {OverlapsOnLane}; at tick 0: {SpawnOverlapsTick0}");
            o.WriteLine($"[8] placed at tick 0 with the front over a lane's end: {SpawnFrontOverLine}, of which the lane ends at a give-way line: {SpawnFrontOverGiveWay}");
            o.WriteLine($"[1] ticks decelerating harder than 1.5x own braking: {HardDecelTicks}; harder than 10 m/s2: {VeryHardDecelTicks}; max {MaxDecel:F1} m/s2");
            o.WriteLine($"[2] hold-at-line episodes: {HoldEpisodes}; on a priority road: {HoldOnPriority}; merges (same approach, another lane, same lane out, within 6 m of the line): {HoldSideBySide}; of those on a priority road: {HoldPriorityBySideBySide}");
            o.WriteLine($"[3,5] stood still held with the front over the lane's end by > 0.3 m: {HoldOverLine} of {HoldEpisodes} (> 2 m: {HoldOverLine2m}); vehicles 8 m or longer: {HoldLongOverLine} of {HoldLong}");
            o.WriteLine("    by preset (over/held): " + string.Join(", ", HoldByPreset.Select(kv => $"{kv.Key} {HoldOverByPreset.GetValueOrDefault(kv.Key)}/{kv.Value}")));
            o.WriteLine($"[3,4] released from a hold: {Releases} (by the deadlock breaker {ReleasesByBreaker} ticks, in {BreakerEpisodes} episodes); held again before the junction: after a gap {ReHoldsAfterGap}, after the breaker {ReHoldsAfterBreaker} ticks; "
                      + $"breaker episodes begun with traffic closer than the critical gap: {BreakerEpisodesBelowCritical} of {BreakerEpisodes} (ticks {BreakerBelowCritical}); stood again with front over the line after a release: {StopOverLineAfterRelease}");
            foreach (var n in Notes.Take(150)) o.WriteLine("  " + n);
        }
    }

    private sealed class Snap
    {
        public object V = null!;
        public string Name = "", Preset = "";
        public Vector3 Pos;
        public float Heading, Length, Speed, Brake, Lap;
        public int Leg;
        public float Along, SegLen, FrontOver;   // FrontOver: metres the front is past the lane's end (negative: short of it)
        public bool InJunction, Holding;
        public RoadNetwork.LaneSegment Seg = null!, NextSeg = null!;
        public JunctionData? To;
        public float WaitedSeconds;
        public string? WaitingAt;
    }

    private sealed class Memory
    {
        public bool WasHolding, Stood, Released, ReleasedByBreaker;
        public string? ReleaseJunction;
        public float PrevSpeed;
        public int ReHolds;
        public float LastBreakerAt = -100f, LastStoodAt = -100f;
        public bool EpisodeOver, EpisodeOver2, EpisodeLongOver;
    }

    /// <summary>Two vehicles' footprints (1.8 m wide, their lengths long, centred on their positions) overlap by more than 5 cm.</summary>
    private static bool BoxesOverlap(Vector3 pa, float ha, float la, Vector3 pb, float hb, float lb)
    {
        const float Width = 1.8f;
        Span<Vector2> axes = stackalloc Vector2[4]
        {
            new(MathF.Sin(ha), MathF.Cos(ha)), new(MathF.Cos(ha), -MathF.Sin(ha)),
            new(MathF.Sin(hb), MathF.Cos(hb)), new(MathF.Cos(hb), -MathF.Sin(hb)),
        };
        var d = new Vector2(pb.X - pa.X, pb.Z - pa.Z);
        foreach (var ax in axes)
        {
            float ra = 0.5f * la * MathF.Abs(Vector2.Dot(ax, axes[0])) + 0.5f * Width * MathF.Abs(Vector2.Dot(ax, axes[1]));
            float rb = 0.5f * lb * MathF.Abs(Vector2.Dot(ax, axes[2])) + 0.5f * Width * MathF.Abs(Vector2.Dot(ax, axes[3]));
            if (MathF.Abs(Vector2.Dot(d, ax)) >= ra + rb - 0.05f) return false;
        }
        return true;
    }

    // ── The run ───────────────────────────────────────────────────────────────────────────────

    private void Run(string id, string file, string dir, int seed, float seconds, Tally t)
    {
        var (maps, world, vehicles, folder) = Load(id, file, dir, seed);
        try
        {
            Assert.True(maps.TryGetMapData(id, out var data));
            var life = data.StreetLife ?? new StreetLifeData();
            var dv = typeof(VehicleSystem).GetNestedType("DemoVehicle", BindingFlags.NonPublic | BindingFlags.Public)!;
            var list = (System.Collections.IList)typeof(VehicleSystem).GetField("_vehicles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vehicles)!;
            FieldInfo F(string n) => dv.GetField(n)!;
            var fRoute = F("Route"); var fLine = F("Line"); var fLap = F("Lap"); var fScale = F("RouteScale"); var fName = F("DisplayName");
            var fPreset = F("Preset"); var fBrake = F("Brake"); var fHold = F("Holding"); var fWait = F("WaitedSeconds"); var fWaitAt = F("WaitingAt");
            var fLen = F("LengthMetres"); var fSpeed = F("Speed"); var fMap = F("MapId"); var fPark = F("Park"); var fGone = F("Gone");
            Assert.True(maps.TryGetRoads(id, out var net));

            var memory = new Dictionary<string, Memory>();
            var overlapping = new HashSet<(string, string)>();
            var snaps = new List<Snap>();
            int nRoute = 0;
            string tag = $"{id} seed {seed}";

            for (int tick = 0; tick <= seconds * 30; tick++)
            {
                if (tick > 0) vehicles.Update(id, world, Dt);
                float now = tick * Dt;
                var drivers = vehicles.DriversForTest(id, world).ToDictionary(d => d.Name, d => d);
                snaps.Clear();
                foreach (object v in list)
                {
                    if ((string)fMap.GetValue(v)! != id || fRoute.GetValue(v) is not LaneRoute route || fLine.GetValue(v) is not RaceLine line) continue;
                    if ((bool)fGone.GetValue(v)!) continue;
                    var park = fPark.GetValue(v);
                    if (park != null && park.GetType().GetProperty("Phase")?.GetValue(park)?.ToString() == "Parked") continue;
                    string name = (string)fName.GetValue(v)!;
                    if (!drivers.TryGetValue(name, out var d)) continue;
                    float lap = (float)fLap.GetValue(v)!;
                    var (leg, along) = RouteRules.WhereOnLane(route, line, lap, (float)fScale.GetValue(v)!);
                    bool inJ = false;
                    if (along < 0f)
                    {
                        if (RouteRules.ShortOfTheLine(route, line, lap, ref leg, ref along)) { }
                        else inJ = true;
                    }
                    var seg = route.Legs[leg].Segment;
                    float length = (float)fLen.GetValue(v)!;
                    // In a junction the lane just left is the one whose end it has passed.
                    var snap = new Snap
                    {
                        V = v, Name = name, Preset = (string)fPreset.GetValue(v)!, Pos = d.Position, Heading = d.Heading, Length = length,
                        Speed = d.Speed, Brake = (float)fBrake.GetValue(v)!, Lap = lap, Leg = leg, Along = along, SegLen = seg.LengthMetres,
                        FrontOver = inJ ? float.NaN : along + 0.5f * length - seg.LengthMetres, InJunction = inJ,
                        Holding = (bool)fHold.GetValue(v)!, Seg = seg, NextSeg = route.Legs[(leg + 1) % route.Legs.Count].Segment, To = seg.To, WaitedSeconds = (float)fWait.GetValue(v)!, WaitingAt = (string?)fWaitAt.GetValue(v),
                    };
                    snaps.Add(snap);
                }
                nRoute = snaps.Count;
                t.VehicleSeconds += nRoute * Dt;

                // Placing: tick 0, before anything has moved.
                if (tick == 0)
                    foreach (var s in snaps)
                        if (!s.InJunction && s.FrontOver > 0f)
                        {
                            t.SpawnFrontOverLine++;
                            if (s.To != null && s.To.GivesWay(s.Seg.Road))
                            {
                                t.SpawnFrontOverGiveWay++;
                                t.Notes.Add($"{tag}: placed at tick 0, {s.Name} ({s.Preset}, {s.Length:F1} m) front {s.FrontOver:F1} m over the give-way line of {s.To.Id} on {s.Seg.Road.Id} at x {s.Pos.X:F0} east, y {s.Pos.Z:F0} north");
                            }
                        }

                // Bodies overlapping.
                for (int a = 0; a < snaps.Count; a++)
                    for (int b = a + 1; b < snaps.Count; b++)
                    {
                        var (p, q) = (snaps[a], snaps[b]);
                        var dd = q.Pos - p.Pos;
                        if (dd.X * dd.X + dd.Z * dd.Z > 100f) continue;
                        bool hit = BoxesOverlap(p.Pos, p.Heading, p.Length, q.Pos, q.Heading, q.Length);
                        var key = (p.Name, q.Name);
                        if (!hit) { overlapping.Remove(key); continue; }
                        if (!overlapping.Add(key)) continue;
                        t.Overlaps++;
                        if (tick == 0) t.SpawnOverlapsTick0++;
                        if (now <= 20f) t.OverlapsAtPlacing++;
                        bool atJ = p.InJunction || q.InJunction || (p.To != null && p.SegLen - p.Along < 12f) || (q.To != null && q.SegLen - q.Along < 12f);
                        if (atJ) t.OverlapsInJunction++; else t.OverlapsOnLane++;
                        float turn = MathF.Abs(MathF.IEEERemainder(p.Heading - q.Heading, 2 * MathF.PI));
                        t.Notes.Add($"{tag}: OVERLAP t {now:F1} s {p.Name} ({p.Preset}, {p.Speed:F1} m/s) and {q.Name} ({q.Preset}, {q.Speed:F1} m/s) "
                                  + $"centres {Vector3.Distance(p.Pos, q.Pos):F1} m apart at x {p.Pos.X:F0} east, y {p.Pos.Z:F0} north, heading apart {turn * 57.3f:F0} deg, {(atJ ? "at a junction" : "on a lane")}, "
                                  + $"{p.Seg.Road.Id}/{q.Seg.Road.Id}");
                    }

                // Per vehicle: deceleration, holds and releases.
                foreach (var s in snaps)
                {
                    if (!memory.TryGetValue(s.Name, out var m)) memory[s.Name] = m = new Memory { PrevSpeed = s.Speed };
                    float decel = (m.PrevSpeed - s.Speed) / Dt;
                    m.PrevSpeed = s.Speed;
                    if (tick > 0)
                    {
                        if (decel > 1.5f * s.Brake) t.HardDecelTicks++;
                        if (decel > 10f) { t.VeryHardDecelTicks++; if (t.VeryHardDecelTicks <= 5) t.Notes.Add($"{tag}: {s.Name} ({s.Preset}) braked {decel:F0} m/s2 at t {now:F1} s at x {s.Pos.X:F0} east, y {s.Pos.Z:F0} north"); }
                        t.MaxDecel = MathF.Max(t.MaxDecel, decel);
                    }

                    bool stoodHeld = s.Holding && s.Speed < 0.3f && !s.InJunction;
                    // A new stand-still at a line.
                    bool newStand = stoodHeld && now - m.LastStoodAt > 2f;
                    if (stoodHeld) m.LastStoodAt = now;
                    if (newStand) { m.EpisodeOver = m.EpisodeOver2 = m.EpisodeLongOver = false; }
                    if (stoodHeld && !m.EpisodeOver && s.FrontOver > 0.3f && !newStand)
                    {
                        m.EpisodeOver = true;
                        t.HoldOverLine++;
                        t.HoldOverByPreset[s.Preset] = t.HoldOverByPreset.GetValueOrDefault(s.Preset) + 1;
                        if (s.Length >= 8f) t.HoldLongOverLine++;
                        t.Notes.Add($"{tag}: HELD OVER LINE t {now:F1} s {s.Name} ({s.Preset}, {s.Length:F1} m) front {s.FrontOver:F1} m over the end of {s.Seg.Road.Id} at {s.To?.Id} (x {s.Pos.X:F0} east, y {s.Pos.Z:F0} north), waited {s.WaitedSeconds:F1} s");
                    }
                    if (stoodHeld && !m.EpisodeOver2 && s.FrontOver > 2f) { m.EpisodeOver2 = true; t.HoldOverLine2m++; }
                    if (newStand)
                    {
                        m.Stood = true;
                        t.HoldEpisodes++;
                        t.HoldByPreset[s.Preset] = t.HoldByPreset.GetValueOrDefault(s.Preset) + 1;
                        bool priority = s.To != null && !s.To.GivesWay(s.Seg.Road);
                        if (priority) t.HoldOnPriority++;
                        bool longVehicle = s.Length >= 8f;
                        if (longVehicle) t.HoldLong++;
                        // A merge: another vehicle of the same approach, in another lane, going on into the same lane out,
                        // about as far from the line (within 6 m).
                        bool beside = snaps.Any(o => o != s && o.Seg != s.Seg && o.Seg.To == s.To && o.Seg.Road == s.Seg.Road
                                                     && Math.Sign(o.Seg.Lane.Direction) == Math.Sign(s.Seg.Lane.Direction) && !o.InJunction
                                                     && o.NextSeg == s.NextSeg
                                                     && MathF.Abs(o.SegLen - o.Along - (s.SegLen - s.Along)) < 6f);
                        if (beside) { t.HoldSideBySide++; if (priority) t.HoldPriorityBySideBySide++; }
                        if (s.FrontOver > 0.3f)
                        {
                            m.EpisodeOver = true;
                            t.HoldOverLine++;
                            if (s.FrontOver > 2f) { m.EpisodeOver2 = true; t.HoldOverLine2m++; }
                            if (longVehicle) t.HoldLongOverLine++;
                            t.HoldOverByPreset[s.Preset] = t.HoldOverByPreset.GetValueOrDefault(s.Preset) + 1;
                            t.Notes.Add($"{tag}: HELD OVER LINE t {now:F1} s {s.Name} ({s.Preset}, {s.Length:F1} m) front {s.FrontOver:F1} m over the end of {s.Seg.Road.Id} at {s.To?.Id} (x {s.Pos.X:F0} east, y {s.Pos.Z:F0} north), waited {s.WaitedSeconds:F1} s");
                        }
                        else if (priority && t.Notes.Count < 400)
                            t.Notes.Add($"{tag}: held on a PRIORITY road t {now:F1} s {s.Name} ({s.Preset}) {s.Seg.Road.Id} at {s.To?.Id}, front {s.FrontOver:F1} m, same approach beside: {beside}");
                    }

                    // A release: held last tick, not now.
                    if (m.WasHolding && !s.Holding && !s.InJunction)
                    {
                        t.Releases++;
                        m.Released = true;
                        m.ReHolds = 0;
                        m.ReleaseJunction = s.To?.Id;
                        bool breaker = s.WaitingAt != null && s.WaitedSeconds > life.GiveWayPatienceSeconds;
                        m.ReleasedByBreaker = breaker;
                        if (breaker)
                        {
                            t.ReleasesByBreaker++;
                            bool newEpisode = now - m.LastBreakerAt > 3f;
                            m.LastBreakerAt = now;
                            // The nearest arrival at the same junction on another lane, seconds.
                            float soonest = float.MaxValue; string who = "";
                            foreach (var o in snaps)
                            {
                                if (o == s || o.InJunction || o.To != s.To || o.Seg == s.Seg || o.Speed < 0.5f) continue;
                                float eta = (o.SegLen - o.Along) / o.Speed;
                                if (eta < soonest) { soonest = eta; who = o.Name; }
                            }
                            float critical = life.CriticalGapStraightSeconds;
                            if (soonest < critical) t.BreakerBelowCritical++;
                            if (newEpisode) { t.BreakerEpisodes++; if (soonest < critical) t.BreakerEpisodesBelowCritical++; }
                            if (newEpisode) t.Notes.Add($"{tag}: BREAKER t {now:F1} s let go {s.Name} ({s.Preset}) at {s.To?.Id}, waited {s.WaitedSeconds:F1} s, nearest arrival {(soonest == float.MaxValue ? "none" : $"{soonest:F1} s ({who})")} against a critical gap of {critical:F1} s, "
                                      + $"at x {s.Pos.X:F0} east, y {s.Pos.Z:F0} north");
                        }
                    }
                    // Held again after a release, before it is into the junction.
                    if (m.Released)
                    {
                        if (s.InJunction || s.To?.Id != m.ReleaseJunction) m.Released = false;
                        else if (!m.WasHolding && s.Holding)
                        {
                            m.ReHolds++;
                            if (m.ReleasedByBreaker) t.ReHoldsAfterBreaker++; else t.ReHoldsAfterGap++;
                            bool quiet = m.ReleasedByBreaker && now - m.LastBreakerAt < 1f;
                            if (s.FrontOver > 0.3f)
                            {
                                t.StopOverLineAfterRelease++;
                                t.Notes.Add($"{tag}: HELD AGAIN OVER LINE t {now:F1} s {s.Name} ({s.Preset}) front {s.FrontOver:F1} m over the end at {s.To?.Id}, speed {s.Speed:F1} m/s, x {s.Pos.X:F0} east, y {s.Pos.Z:F0} north");
                            }
                            else if (!quiet && t.Notes.Count < 400)
                                t.Notes.Add($"{tag}: held again t {now:F1} s {s.Name} ({s.Preset}) at {s.To?.Id}, front {s.FrontOver:F1} m from the end, {s.Speed:F1} m/s, after {(m.ReleasedByBreaker ? "the breaker" : "a gap")}");
                        }
                    }
                    m.WasHolding = s.Holding;
                    if (!stoodHeld) m.Stood = false;
                }
            }
            _o.WriteLine($"{tag}: {nRoute} vehicles on routes, {seconds:F0} s run");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
}
