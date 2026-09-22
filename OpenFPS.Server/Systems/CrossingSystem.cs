using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Level crossings: the bells, and the road holding while a train goes through.
///
/// A crossing is the one place on a map where the railway and the road have to know about each
/// other, and it is worth being careful about WHICH way that knowledge flows. The train does not
/// know there is a crossing; it does not slow down, it does not signal, it simply comes. The ROAD
/// is what changes: a circuit up the line notices the train, the bells start, and the traffic that
/// would have driven across stops instead. So everything here reads the trains and writes to the
/// road, and the rail system is untouched.
///
/// The geometry is DERIVED. A crossing declares a point and nothing else; which rail line runs
/// through it, which roads run through it, and how far round each of those it sits are all worked
/// out from the tracks the map already has. A declared offset is a number that silently rots the
/// first time somebody moves a waypoint, and a crossing whose offset has rotted rings for nothing
/// and stops nobody.
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

    /// <param name="resendDefinition">
    /// Asks the server to send an entity's DEFINITION again. A crossing is the only thing here
    /// whose sound state lives in the definition rather than in its transform, so it is the only
    /// thing that needs this — see where SynthRunning is set.
    /// </param>
    public CrossingSystem(RailSystem rail, Action<int>? resendDefinition = null)
    {
        _rail = rail;
        _resendDefinition = resendDefinition;
    }

    /// <summary>How near the centre counts as "on this track". A crossing is a few metres of road,
    /// not a point, and a waypoint polyline only samples the curve it stands for.</summary>
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

                // The bell, as an entity of its own so it is placed, occluded and reverberated like
                // anything else that makes a noise. It is silent until Update rings it.
                // STATIC, and with no Velocity on it — which is not a tidiness point.
                //
                // "Static" in this server means exactly "no PlayerComponent and no Velocity", and
                // that set is what EntityDefinitionFactory.StaticEntities streams in answer to a
                // map-data request. A bell on a post does not move; giving it a velocity took it
                // out of the bulk map stream and put it in the per-tick dynamic path, which is a
                // different route to the client for no reason. Every other fixed emitter on this
                // map — every air conditioner, every mower — is static, and they all work.
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

                _crossings.Add(c);
                Log.Information("Map {Map}: {Name} at {Pos} — on {Rails} rail line(s), bells from {W:F0} m out; "
                              + "bell entity {Bell} ('{Sound}').",
                                mapId, c.Name, cd.Position, c.OnRail.Count, c.WarningMetres,
                                c.Bell == Entity.Null ? -1 : c.Bell.Id,
                                cd.Bell ?? "crossing_gong");
            }
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
                foreach (float head in _rail.HeadsOn(mapId, track, out float lapLength))
                {
                    // How far the train still has to come. Round the loop, so a train just past
                    // the crossing is a whole lap away and not a metre behind.
                    float toGo = at - head;
                    if (toGo < 0f) toGo += lapLength;
                    if (toGo <= c.WarningMetres) { wants = true; break; }
                    // And the tail: still closed until it is clear on the far side.
                    float past = head - at;
                    if (past < 0f) past += lapLength;
                    if (past <= c.ClearMetres) { wants = true; break; }
                }
                if (wants) break;
            }

            if (wants) { c.Closed = true; c.ClosedFor = 0f; }
            else if (c.Closed)
            {
                c.ClosedFor += dt;
                if (c.ClosedFor >= MinimumClosedSeconds) c.Closed = false;
            }

            // What the crossing is doing, once a second, for one named by a substring:
            //   OPENFPS_TRACE_CROSSING=Southgate ./run-server.sh city
            // A crossing that never closes and one whose bell never reaches the client sound
            // identical from the pavement, and this tells them apart.
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

            if (c.Bell != Entity.Null && world.IsAlive(c.Bell))
            {
                ref var em = ref world.Get<SoundEmitterComponent>(c.Bell);
                if (em.SynthRunning != c.Closed)
                {
                    em.SynthRunning = c.Closed;
                    // The DEFINITION has to go out again, and Transform.IsDirty does not do that.
                    //
                    // A dirty transform re-sends a STATE message — a position and a velocity. The
                    // emitter, with SynthRunning on it, lives in the definition, and definitions
                    // are sent once per entity per client unless something asks for another. So
                    // the bell rang perfectly on the server, the flag flipped every time, and the
                    // client was still holding the definition it was handed at map load, in which
                    // the bell was silent. Nothing failed; the news simply never left the building.
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
}
