using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Level crossings: the bells, the gates, and the road holding while a train goes through. Everything
/// here reads the trains and writes to the road; the trains know nothing of crossings. A crossing
/// declares only a point: its rails, roads and offsets are derived from the map's tracks, because a
/// declared offset rots the first time a waypoint moves.
/// </summary>
public sealed class CrossingSystem
{
    private sealed class Crossing
    {
        public required string MapId, Name;
        public required Vector3 Position;
        public required float WarningMetres, ClearMetres;
        /// <summary>Where this crossing sits along each rail line that runs through it, metres.</summary>
        public readonly List<(string Track, float At)> OnRail = new();
        public Entity Bell = Entity.Null;
        /// <summary>A gate on each road approach (see SpawnGates).</summary>
        public readonly List<Entity> Gates = new();
        public bool Closed;
        /// <summary>How long it has been closed, so a crossing cannot chatter shut and open.</summary>
        public float ClosedFor;
        public float TraceAt;
    }

    private readonly List<Crossing> _crossings = new();
    private readonly RailSystem _rail;

    /// <summary>A crossing does not reopen the instant the last axle clears: the gates lift, and
    /// until they are up the road is still held.</summary>
    private const float MinimumClosedSeconds = 6f;

    private readonly Action<int>? _resendDefinition;

    /// <param name="resendDefinition">Asks the server to send an entity's definition again: the bell's
    /// and gates' state (SynthRunning) lives there, not in the transform.</param>
    public CrossingSystem(RailSystem rail, Action<int>? resendDefinition = null)
    {
        _rail = rail;
        _resendDefinition = resendDefinition;
    }

    /// <summary>How near the centre counts as "on this track": a crossing is a few metres of road, and
    /// a waypoint polyline only samples its curve.</summary>
    private const float OnTrackMetres = 14f;

    public void Spawn(MapManager maps)
    {
        foreach (var entry in maps.GetAllMaps())
        {
            string mapId = entry.Key;
            var data = entry.Value.data;
            if (data.Crossings == null || data.Crossings.Count == 0) continue;

            foreach (var cd in data.Crossings)
            {
                var c = new Crossing
                {
                    MapId = mapId,
                    Name = cd.Name ?? "level crossing",
                    Position = cd.Position,
                    WarningMetres = cd.WarningMetres,
                    ClearMetres = MathF.Max(5f, cd.ClearMetres),
                };

                // Which rail lines run through it, and how far round each.
                foreach (var (track, line) in _rail.Lines(mapId))
                {
                    if (!NearestOn(line, cd.Position, out float at, out float dist)) continue;
                    if (dist > OnTrackMetres) continue;
                    c.OnRail.Add((track, at));
                    if (c.WarningMetres <= 0f)
                        // Timed, not placed: the bells are wired to ring for a fixed number of
                        // seconds before the train arrives, so the distance follows the line speed.
                        c.WarningMetres = MathF.Max(60f, line.MaxSpeed * MathF.Max(5f, cd.WarningSeconds));
                }

                if (c.OnRail.Count == 0)
                {
                    Log.Warning("Map {Map}: crossing '{Name}' at {Pos} is not within {R} m of any rail track — "
                              + "it will never ring.", mapId, c.Name, cd.Position, OnTrackMetres);
                    continue;
                }

                // The bell, an entity of its own, silent until Update rings it. No Velocity: static
                // means "no PlayerComponent and no Velocity" (EntityDefinitionFactory.StaticEntities),
                // and a velocity moved it from the map stream to the per-tick dynamic path.
                c.Bell = maps.SpawnEntity(mapId, w => w.Create(
                    EntityType.StaticObject,
                    new Transform { Position = cd.Position + new Vector3(0f, 2.6f, 0f), Rotation = Quaternion.Identity },
                    new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.4f, 0.4f, 0.4f), IsSolid = false },
                    new NameComponent { Name = c.Name + " bell" },
                    new IdentityComponent { Name = c.Name, Description = "a level crossing" },
                    new SoundEmitterComponent
                    {
                        IsSynth = true,
                        SoundId = "bell:" + (cd.Bell ?? "crossing_gong"),
                        Mode = PlaybackMode.LoopOne,
                        Volume = 1f,
                        Range = Loudness.AudibleRange(86f),
                        MinDistance = 3f,
                    }));

                SpawnGates(maps, mapId, c);
                _crossings.Add(c);
                Log.Information("Map {Map}: {Name} at {Pos} — on {Rails} rail line(s), bells from {W:F0} m out; "
                              + "bell entity {Bell} ('{Sound}'), {Gates} gate(s).",
                                mapId, c.Name, cd.Position, c.OnRail.Count, c.WarningMetres,
                                c.Bell == Entity.Null ? -1 : c.Bell.Id,
                                cd.Bell ?? "crossing_gong", c.Gates.Count);
            }
        }
    }

    /// <summary>How far from the nearest rail a gate stands, along the road, metres: 12 feet, the usual
    /// clearance from the rail to the gate mast, inside the 15-foot stop line.</summary>
    public const float GateFromRail = 3.7f;

    /// <summary>
    /// A gate on each road approach, derived from the roads through the crossing: on the right of the
    /// traffic coming toward the rails, its mast just off the carriageway, GateFromRail short of the
    /// nearest rail. Its SynthRunning is the crossing's closed state; the client moves the arm (GateArm).
    /// </summary>
    private void SpawnGates(MapManager maps, string mapId, Crossing c)
    {
        if (!maps.TryGetRoads(mapId, out var net)) return;
        // The rails' direction at the crossing, from the rail line.
        var (track, at) = c.OnRail[0];
        Vector3 along = Vector3.UnitX;
        foreach (var (t, line) in _rail.Lines(mapId))
            if (string.Equals(t, track, StringComparison.OrdinalIgnoreCase))
            {
                line.Sample(at, out _, out float h, out _);
                along = new Vector3(MathF.Sin(h), 0f, MathF.Cos(h));
                break;
            }
        var railsAcross = new Vector3(along.Z, 0f, -along.X);
        foreach (var road in net.Roads)
        {
            if (road.Centreline.Count < 2) continue;
            var (s, off) = RoadNetwork.Project(road.Centreline, c.Position);
            if (off > road.WidthMetres * 0.5f) continue;
            var dir = RoadNetwork.PointAt(road.Centreline, s + 1f) - RoadNetwork.PointAt(road.Centreline, MathF.Max(0f, s - 1f));
            dir.Y = 0f;
            if (dir.LengthSquared() < 1e-6f) continue;
            dir = Vector3.Normalize(dir);
            // Along the road, the rails are a rail-centre's half apart over the sine of the angle between.
            float sin = MathF.Max(0.3f, MathF.Abs(Vector3.Dot(dir, railsAcross)));
            float back = (CrossingRails.StandardRailCentres * 0.5f + GateFromRail) / sin;
            var centre = RoadNetwork.PointAt(road.Centreline, s);
            foreach (float sign in new[] { 1f, -1f })
            {
                // Traffic coming toward the rails along `approach`: the gate is behind the rails from
                // it, on its right.
                var approach = dir * sign;
                var right = new Vector3(approach.Z, 0f, -approach.X);
                var post = centre - approach * back + right * (road.WidthMetres * 0.5f + 0.6f);
                post.Y = c.Position.Y;
                var gate = maps.SpawnEntity(mapId, w => w.Create(
                    EntityType.StaticObject,
                    new Transform { Position = post + new Vector3(0f, 0.6f, 0f), Rotation = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(-approach.X, -approach.Z), 0f, 0f) },
                    new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.2f, 0.5f), IsSolid = false },
                    new NameComponent { Name = $"{c.Name} gate" },
                    new IdentityComponent { Name = $"{c.Name} gate", Description = "a level crossing gate" },
                    new SoundEmitterComponent
                    {
                        IsSynth = true,
                        SoundId = "gate:" + CrossingGateSpec.Standard.Name,
                        Mode = PlaybackMode.LoopOne,
                        Volume = 1f,
                        Range = Loudness.AudibleRange(CrossingGateSpec.Standard.ClunkDb),
                        MinDistance = 1f,
                        SynthRunning = false,
                    }));
                if (gate != Entity.Null) c.Gates.Add(gate);
            }
        }
    }

    /// <summary>The gates' posts on a map, for tests and the lab.</summary>
    public IEnumerable<(string Crossing, Vector3 At)> GatePosts(string mapId, World world)
    {
        foreach (var c in _crossings)
        {
            if (c.MapId != mapId) continue;
            foreach (var g in c.Gates)
                if (world.IsAlive(g)) yield return (c.Name, world.Get<Transform>(g).Position);
        }
    }

    /// <summary>Where this map's crossings are round a rail track, metres.</summary>
    public IEnumerable<float> PositionsOn(string mapId, string track)
    {
        foreach (var c in _crossings)
        {
            if (c.MapId != mapId) continue;
            foreach (var (t, at) in c.OnRail)
                if (string.Equals(t, track, StringComparison.OrdinalIgnoreCase)) yield return at;
        }
    }

    /// <summary>
    /// Whether the road at this point is being held. Asked by VehicleSystem for a stop of kind
    /// "crossing", which is the whole of how traffic learns about trains.
    /// </summary>
    public bool IsClosedAt(string mapId, Vector3 where, float withinMetres = OnTrackMetres)
    {
        foreach (var c in _crossings)
        {
            if (c.MapId != mapId) continue;
            if (Vector3.DistanceSquared(c.Position, where) <= withinMetres * withinMetres)
                return c.Closed;
        }
        return false;
    }

    public void Update(string mapId, World world, float dt)
    {
        foreach (var c in _crossings)
        {
            if (c.MapId != mapId) continue;

            bool wants = false;
            foreach (var (track, at) in c.OnRail)
            {
                foreach (var (head, length) in _rail.TrainsOn(mapId, track, out float lapLength))
                {
                    // Round the loop: a train just past is a whole lap away, not a metre behind.
                    float toGo = at - head;
                    if (toGo < 0f) toGo += lapLength;
                    if (toGo <= c.WarningMetres) { wants = true; break; }
                    // Closed until the tail is clear: measuring the head reopened the road with 20 m
                    // of a 55 m tram still on it.
                    float past = head - at;
                    if (past < 0f) past += lapLength;
                    if (past <= length + c.ClearMetres) { wants = true; break; }
                }
                if (wants) break;
            }

            if (wants) { c.Closed = true; c.ClosedFor = 0f; }
            else if (c.Closed)
            {
                c.ClosedFor += dt;
                if (c.ClosedFor >= MinimumClosedSeconds) c.Closed = false;
            }

            // Once a second, for a crossing named by a substring: OPENFPS_TRACE_CROSSING=Southgate.
            // Tells a crossing that never closes from a bell that never reaches the client.
            if (Environment.GetEnvironmentVariable("OPENFPS_TRACE_CROSSING") is { } tr
                && c.Name.Contains(tr, StringComparison.OrdinalIgnoreCase))
            {
                c.TraceAt += dt;
                if (c.TraceAt >= 1f)
                {
                    c.TraceAt = 0f;
                    var (t0, a0) = c.OnRail[0];
                    var heads = _rail.HeadsOn(mapId, t0, out float lap);
                    string near = heads.Count == 0 ? "no trains"
                        : string.Join(", ", heads.Select(h => { float d = a0 - h; if (d < 0) d += lap; return $"{d:F0} m"; }));
                    Log.Information("CROSSING {Name}: {State}, train(s) {Near} away (rings at {W:F0})",
                                    c.Name, c.Closed ? "CLOSED" : "open", near, c.WarningMetres);
                }
            }

            foreach (var gate in c.Gates)
            {
                if (!world.IsAlive(gate)) continue;
                ref var gem = ref world.Get<SoundEmitterComponent>(gate);
                if (gem.SynthRunning == c.Closed) continue;
                gem.SynthRunning = c.Closed;
                _resendDefinition?.Invoke(gate.Id);
            }
            if (c.Bell != Entity.Null && world.IsAlive(c.Bell))
            {
                ref var em = ref world.Get<SoundEmitterComponent>(c.Bell);
                if (em.SynthRunning != c.Closed)
                {
                    em.SynthRunning = c.Closed;
                    // The definition must go out again: a dirty transform sends only position and
                    // velocity, and without this the bell rang on the server and nowhere else.
                    _resendDefinition?.Invoke(c.Bell.Id);
                }
            }
        }
    }

    /// <summary>Nearest point on a closed line to a position: how far round it, and how far away.</summary>
    private static bool NearestOn(RaceLine line, Vector3 p, out float at, out float dist)
    {
        at = 0f; dist = float.MaxValue;
        for (float s = 0f; s < line.Length; s += 2f)
        {
            line.Sample(s, out var q, out _, out _);
            float d = Vector3.Distance(new Vector3(q.X, 0f, q.Z), new Vector3(p.X, 0f, p.Z));
            if (d < dist) { dist = d; at = s; }
        }
        return dist < float.MaxValue;
    }

    public int Count => _crossings.Count;

    /// <summary>
    /// This map's crossings as the rails lie across the road: the middle of the track at each, from the
    /// rail line rather than the declared point, and the direction the rails run there.
    /// </summary>
    public IEnumerable<CrossingRails> Rails(string mapId)
    {
        foreach (var c in _crossings)
        {
            if (c.MapId != mapId) continue;
            var (track, at) = c.OnRail[0];
            foreach (var (t, line) in _rail.Lines(mapId))
            {
                if (!string.Equals(t, track, StringComparison.OrdinalIgnoreCase)) continue;
                line.Sample(at, out var centre, out float heading, out _);
                // Nearest the declared point to a few centimetres: the line is sampled every 2 m.
                for (float step = 1f; step > 0.02f; step *= 0.5f)
                {
                    foreach (float d in new[] { at - step, at + step })
                    {
                        line.Sample(d, out var q, out float h, out _);
                        if (Flat(q - c.Position) < Flat(centre - c.Position)) { centre = q; heading = h; at = d; }
                    }
                }
                yield return new CrossingRails
                {
                    Name = c.Name,
                    Centre = centre,
                    // A line's heading runs from +z toward +x: (sin h, cos h) on the ground.
                    Along = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)),
                };
                break;
            }
        }
    }

    private static float Flat(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);
}
