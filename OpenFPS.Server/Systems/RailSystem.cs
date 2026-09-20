using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Trains on a map's rail tracks.
///
/// A train is the one physical model that is not one pressure at one point: it is a line of
/// bogies, drives and a body drum spread over the length of the consist (docs/TRAINS.md). So a
/// train here is not one entity but one per sound source, each placed by sampling the track at
/// <c>head − along</c> — which is what makes a 55 m set wrap a 26 m corner correctly, where a
/// straight line behind the head would put the tail through the buildings. The client runs ONE
/// synth for the whole train and gives each entity the source its index names; the SoundId carries
/// that index: "rail:&lt;preset&gt;/&lt;train&gt;/&lt;index&gt;". The order is <see cref="TrainLayout"/>'s,
/// and it is the client's too.
///
/// Speed comes from the track the way a car's does: the curvature at each point sets a limit, the
/// brakes pull it down ahead of the corner, and the head accelerates toward it. Nothing scripts it.
/// </summary>
public sealed class RailSystem
{
    private sealed class Consist
    {
        public required string MapId, Name, Preset;
        public required RaceLine Line;
        public required Entity[] Entities;     // Entity.Null for the signal sources, which are not spawned
        public required float[] Along;
        public float Head, Speed, TopSpeed, Accel, Brake;
    }

    private readonly List<Consist> _trains = new();

    public void Spawn(MapManager maps)
    {
        foreach (var entry in maps.GetAllMaps())
        {
            string mapId = entry.Key;
            var data = entry.Value.data;
            if (data.Trains == null) continue;
            int n = 0;
            foreach (var td in data.Trains)
            {
                TrainProfile profile;
                try { profile = TrainProfile.ByName(td.Preset); }
                catch (Exception ex) { Log.Warning("Map {Map}: train '{Name}' — {Err}", mapId, td.Name, ex.Message); continue; }
                var track = data.Tracks?.Find(t => string.Equals(t.Id, td.Track, StringComparison.OrdinalIgnoreCase));
                if (track == null || track.Waypoints.Count < 3)
                {
                    Log.Warning("Map {Map}: train '{Name}' asks for track '{Track}', which is missing; it will not run.", mapId, td.Name, td.Track);
                    continue;
                }
                float top = MathF.Max(1f, td.TopSpeedKmh) / 3.6f;
                float brake = td.BrakingMps2 > 0 ? td.BrakingMps2 : 1.0f;
                RaceLine line;
                // A tram leans into nothing and slides on nothing: the cornering figure is the
                // lateral acceleration passengers stand up through, about 0.1 g.
                try { line = new RaceLine(track.Waypoints, 0f, top, 0.1f, brake, track.BankingDegrees); }
                catch (Exception ex) { Log.Warning("Map {Map}: track '{Track}' — {Err}", mapId, td.Track, ex.Message); continue; }

                var layout = TrainLayout.Sources(profile);
                string name = td.Name ?? $"{profile.Name} {++n}";
                string trainKey = name.Replace('/', '-').Replace(' ', '_');
                var ents = new Entity[layout.Count];
                var along = new float[layout.Count];
                for (int i = 0; i < layout.Count; i++)
                {
                    var src = layout[i];
                    along[i] = src.AlongMetres;
                    if (src.IsSignal) { ents[i] = Entity.Null; continue; }
                    line.Sample(td.StartOffsetMetres - src.AlongMetres, out var pos, out float heading, out _);
                    pos.Y += src.HeightMetres;
                    string soundId = $"rail:{td.Preset}/{trainKey}/{src.Index}";
                    ents[i] = maps.SpawnEntity(mapId, w => w.Create(
                        EntityType.NPC,
                        new Transform { Position = pos, Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f) },
                        new Velocity { Linear = Vector3.Zero },
                        new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(2.6f, 3.4f, 4.0f), IsSolid = false },
                        new NameComponent { Name = $"{name}: {src.Label}" },
                        new IdentityComponent { Name = name, Description = $"{profile.Name}, on the rails" },
                        new SoundEmitterComponent
                        {
                            IsSynth = true,
                            SoundId = soundId,
                            Mode = PlaybackMode.LoopOne,
                            Volume = 1f,
                            Range = Loudness.AudibleRange(src.LevelDb),
                            MinDistance = 3f,
                        }));
                }
                line.Sample(td.StartOffsetMetres, out _, out _, out float v0);
                _trains.Add(new Consist
                {
                    MapId = mapId, Name = name, Preset = td.Preset, Line = line, Entities = ents, Along = along,
                    Head = td.StartOffsetMetres, Speed = MathF.Min(v0, top), TopSpeed = top,
                    Accel = td.AccelerationMps2 > 0 ? td.AccelerationMps2 : 0.9f, Brake = brake,
                });
                int spawned = 0; foreach (var e in ents) if (e != Entity.Null) spawned++;
                Log.Information("Map {Map}: {Name} ({Profile}) runs '{Track}' — {Length:F0} m loop, {Sources} source(s) over {Consist:F0} m, {Min:F0}-{Max:F0} km/h",
                                mapId, name, profile.Name, td.Track, line.Length, spawned, profile.LengthMetres, line.MinSpeed * 3.6f, line.MaxSpeed * 3.6f);
            }
        }
    }

    public void Update(string mapId, World world, float dt)
    {
        foreach (var tr in _trains)
        {
            if (tr.MapId != mapId) continue;
            float lookahead = MathF.Max(15f, tr.Speed * tr.Speed / (2f * tr.Brake));
            tr.Line.Sample(tr.Head + lookahead, out _, out _, out float ahead);
            tr.Line.Sample(tr.Head, out _, out _, out float now);
            float want = MathF.Min(tr.TopSpeed, MathF.Min(now, ahead));
            if (want > tr.Speed) tr.Speed = MathF.Min(want, tr.Speed + tr.Accel * dt);
            else tr.Speed = MathF.Max(want, tr.Speed - tr.Brake * dt);
            tr.Head += tr.Speed * dt;
            if (tr.Head > tr.Line.Length) tr.Head -= tr.Line.Length;

            for (int i = 0; i < tr.Entities.Length; i++)
            {
                var e = tr.Entities[i];
                if (e == Entity.Null || !world.IsAlive(e)) continue;
                tr.Line.Sample(tr.Head - tr.Along[i], out var pos, out float heading, out _);
                ref var t = ref world.Get<Transform>(e);
                ref var vel = ref world.Get<Velocity>(e);
                float height = t.Position.Y - pos.Y;            // keep the source's own height above the rail
                pos.Y += MathF.Abs(height) < 6f ? height : 0.5f;
                t.Position = pos;
                t.Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
                t.IsDirty = true;
                vel.Linear = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)) * tr.Speed;
            }
        }
    }
}
