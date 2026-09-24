using System;
using System.Linq;
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
        public required string MapId, Name, Preset, Track;
        public required RaceLine Line;
        public required Entity[] Entities;     // Entity.Null for the signal sources, which are not spawned
        public required float[] Along;
        public float Head, Speed, TopSpeed, Accel, Brake;

        /// <summary>Platforms on this route, in order round it. Same description a bus stop uses:
        /// a train halting at a platform and a bus halting at a kerb are the same fact about a
        /// route, and the sounds are each vehicle's own.</summary>
        public (float At, float Dwell, string Kind)[] Stops = System.Array.Empty<(float, float, string)>();
        public int NextStop;
        public float DwellLeft;

        /// <summary>The horn on the leading unit, as "air:&lt;preset&gt;", or "" for a train with none.</summary>
        public string Horn = "";
        /// <summary>Crossings this train has already sounded for on its way in, by where they are
        /// round the line; forgotten once it is past them.</summary>
        public readonly HashSet<float> Sounded = new();
    }

    /// <summary>How a sound leaves this system. Set by the server; null in tests.</summary>
    public Action<string, int, string, IReadOnlyList<TransientSound>>? Heard { get; set; }

    /// <summary>Where the level crossings are round a track, metres. Set by the server from the
    /// crossing system, which already works that out for its bells.</summary>
    public Func<string, string, IEnumerable<float>>? CrossingsOn { get; set; }

    /// <summary>
    /// How long before a crossing the horn starts. US rule (49 CFR 222.21): at least fifteen and no
    /// more than twenty seconds before the lead reaches it, and it goes on until it does.
    /// </summary>
    private const float HornLeadSeconds = 18f;

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
                    Stops = (data.Tracks?.Find(x => string.Equals(x.Id, td.Track, StringComparison.OrdinalIgnoreCase))?.Stops ?? new())
                        .Where(sp => string.IsNullOrEmpty(sp.ForPreset)
                                  || td.Preset.Contains(sp.ForPreset!, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(sp => sp.AtMetres)
                        .Select(sp => (At: sp.AtMetres, Dwell: sp.DwellSeconds, Kind: sp.Kind ?? "platform"))
                        .ToArray(),
                    Track = td.Track,
                    Horn = profile.Consist.Select(c => c.Vehicle.Traction?.HornKey).FirstOrDefault(h => h != null) is { } hk
                        ? "air:" + hk : "",
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
            // Standing at a platform. Held at a dead stop, because everything a stopped train
            // makes — the brakes blowing off, the compressor catching up, the doors — is read off
            // its speed being zero and staying there.
            if (tr.DwellLeft > 0f)
            {
                tr.DwellLeft -= dt;
                tr.Speed = 0f;
                PlaceConsist(tr, world);
                if (tr.DwellLeft <= 0f && tr.Stops.Length > 0)
                    tr.NextStop = (tr.NextStop + 1) % tr.Stops.Length;
                continue;
            }

            float lookahead = MathF.Max(15f, tr.Speed * tr.Speed / (2f * tr.Brake));
            tr.Line.Sample(tr.Head + lookahead, out _, out _, out float ahead);
            tr.Line.Sample(tr.Head, out _, out _, out float now);
            float want = MathF.Min(tr.TopSpeed, MathF.Min(now, ahead));

            // Coming up on a platform. A train's braking rate is a tenth of a car's and its
            // approach is correspondingly long — which is most of why a train arriving sounds
            // like an event rather than like a vehicle turning up.
            if (tr.Stops.Length > 0)
            {
                float d = tr.Stops[tr.NextStop].At - tr.Head;
                if (d < -1f) d += tr.Line.Length;
                d = MathF.Max(0f, d);
                want = MathF.Min(want, MathF.Sqrt(MathF.Max(0f, 2f * tr.Brake * d)));
                if (d <= 1.5f && tr.Speed < 1.5f)
                {
                    tr.DwellLeft = MathF.Max(1f, tr.Stops[tr.NextStop].Dwell);
                    tr.Speed = 0f;
                    PlaceConsist(tr, world);
                    continue;
                }
            }
            if (want > tr.Speed) tr.Speed = MathF.Min(want, tr.Speed + tr.Accel * dt);
            else tr.Speed = MathF.Max(want, tr.Speed - tr.Brake * dt);
            tr.Head += tr.Speed * dt;
            if (tr.Head > tr.Line.Length) tr.Head -= tr.Line.Length;

            SoundForCrossings(tr, world);
            PlaceConsist(tr, world);
        }
    }

    /// <summary>
    /// Long, long, short, long for every level crossing ahead, begun eighteen seconds out and held
    /// until the train is on it — the pattern every North American train sounds, and the reason a
    /// listener hears the train before the bells have told them anything. Worked out here because
    /// this is where the train's speed and the distance to the crossing are both known.
    /// </summary>
    private void SoundForCrossings(Consist tr, World world)
    {
        if (tr.Horn.Length == 0 || Heard == null || CrossingsOn == null || tr.Speed < 2f) return;
        foreach (float at in CrossingsOn(tr.MapId, tr.Track))
        {
            float toGo = at - tr.Head;
            if (toGo < 0f) toGo += tr.Line.Length;
            if (toGo > tr.Line.Length * 0.5f) { tr.Sounded.Remove(at); continue; }   // behind us now
            float eta = toGo / tr.Speed;
            if (eta > HornLeadSeconds || tr.Sounded.Contains(at)) continue;
            tr.Sounded.Add(at);
            var lead = Array.Find(tr.Entities, e => e != Entity.Null);
            if (lead == Entity.Null || !world.IsAlive(lead)) continue;
            // The first three blasts and their gaps take ten seconds; the last is held to arrival.
            var pattern = Honk.Crossing(eta - 10f);
            Log.Information("Rail: {Name} sounds for the crossing {ToGo:F0} m ahead ({Eta:F0} s).", tr.Name, toGo, eta);
            Heard(tr.MapId, lead.Id, "horn", new[]
            {
                new TransientSound
                {
                    Character = SoundCharacter.Ring,
                    Position = world.Get<Transform>(lead).Position + Vector3.UnitY * 4.5f,
                    LevelDb = Honk.LevelDb(tr.Horn),
                    DecaySeconds = Honk.Duration(pattern),
                    SynthKey = Honk.Key(tr.Horn, pattern),
                },
            });
        }
    }

    /// <summary>
    /// Puts every source of a consist where its own place in the train says it is. Factored out
    /// because a train standing at a platform still has to be PLACED — it is not moving, but its
    /// bogies, its compressor and its brakes are all still somewhere, and a stopped train that
    /// stopped being positioned would stop being audible.
    /// </summary>
    private static void PlaceConsist(Consist tr, World world)
    {
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

    /// <summary>
    /// The distinct rail lines on a map, by track id — so anything that needs to know where a
    /// railway RUNS can ask the system that owns it rather than re-reading the map and building a
    /// second copy of the same geometry. Two copies of a track is two things to get out of step.
    /// </summary>
    public IEnumerable<(string Track, RaceLine Line)> Lines(string mapId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tr in _trains)
        {
            if (tr.MapId != mapId || !seen.Add(tr.Track)) continue;
            yield return (tr.Track, tr.Line);
        }
    }

    /// <summary>
    /// How far round a given line each train's leading end currently is, metres, and how long the
    /// lap is. The HEAD, because a crossing starts ringing for the front of a train and stops
    /// ringing for the back of it, and those are different points.
    /// </summary>
    public List<float> HeadsOn(string mapId, string track, out float lapLength)
    {
        lapLength = 1f;
        var heads = new List<float>();
        foreach (var tr in _trains)
        {
            if (tr.MapId != mapId || !string.Equals(tr.Track, track, StringComparison.OrdinalIgnoreCase)) continue;
            lapLength = tr.Line.Length;
            heads.Add(tr.Head);
        }
        return heads;
    }
}
