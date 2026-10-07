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
        public required Entity[] Entities;     // one per source, TrainLayout's order
        public required float[] Along;
        /// <summary>Each source's own height above the rail, metres.</summary>
        public required float[] Heights;
        public float Head, Speed, TopSpeed, Accel, Brake;
        /// <summary>Head to tail, metres: the sum of the vehicles' lengths.</summary>
        public float LengthMetres;

        /// <summary>Platforms on this route, in order round it. Same description a bus stop uses:
        /// a train halting at a platform and a bus halting at a kerb are the same fact about a
        /// route, and the sounds are each vehicle's own.</summary>
        public (float At, float Dwell, string Kind)[] Stops = System.Array.Empty<(float, float, string)>();
        public int NextStop;
        public float DwellLeft;
        /// <summary>Metres run since the train last stood at a stop. A stop it has only just left is
        /// not one to stop at again: on a line with one stop the next stop after it is itself.</summary>
        public float SinceStop = float.PositiveInfinity;

        /// <summary>The train's name in its sources' SoundIds ("rail:&lt;preset&gt;/&lt;key&gt;/&lt;i&gt;").</summary>
        public string Key = "";
        /// <summary>Which source sounds its warning (the horn, or a steam engine's whistle) and which is
        /// its bell, TrainLayout indices; -1 for none.</summary>
        public int Warning = -1, Bell = -1;
        /// <summary>The warning's level at a metre, for who is in earshot of it.</summary>
        public float WarningDb;
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

    public void Spawn(MapManager maps) => Spawn(maps, null, null);

    /// <summary>The trains running on a map.</summary>
    public int CountOn(string mapId) => _trains.Count(t => t.MapId.Equals(mapId, StringComparison.OrdinalIgnoreCase));

    /// <summary>One more train on a map that is already running (/spawn train), on a track it names.
    /// False when it could not be made; the reason is logged.</summary>
    public bool SpawnOne(MapManager maps, string mapId, TrainData td)
    {
        int before = _trains.Count;
        Spawn(maps, mapId, new[] { td });
        return _trains.Count > before;
    }

    /// <summary>Every map's trains, or only <paramref name="only"/> on <paramref name="onlyMap"/>.</summary>
    private void Spawn(MapManager maps, string? onlyMap, IReadOnlyList<TrainData>? only)
    {
        foreach (var entry in maps.GetAllMaps())
        {
            string mapId = entry.Key;
            var data = entry.Value.data;
            if (onlyMap != null && !mapId.Equals(onlyMap, StringComparison.OrdinalIgnoreCase)) continue;
            var list = only ?? data.Trains;
            if (list == null) continue;
            int n = 0;
            foreach (var td in list)
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
                var heights = new float[layout.Count];
                for (int i = 0; i < layout.Count; i++)
                {
                    var src = layout[i];
                    along[i] = src.AlongMetres;
                    heights[i] = src.HeightMetres;
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
                var consist = new Consist
                {
                    MapId = mapId, Name = name, Preset = td.Preset, Line = line, Entities = ents, Along = along, Heights = heights,
                    Stops = (data.Tracks?.Find(x => string.Equals(x.Id, td.Track, StringComparison.OrdinalIgnoreCase))?.Stops ?? new())
                        .Where(sp => string.IsNullOrEmpty(sp.ForPreset)
                                  || td.Preset.Contains(sp.ForPreset!, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(sp => sp.AtMetres)
                        .Select(sp => (At: sp.AtMetres, Dwell: sp.DwellSeconds, Kind: sp.Kind ?? "platform"))
                        .ToArray(),
                    Track = td.Track,
                    Key = trainKey,
                    Warning = TrainSignal.WarningSource(layout),
                    Bell = TrainSignal.BellSource(layout),
                    WarningDb = TrainSignal.WarningSource(layout) is int w and >= 0 ? layout[w].LevelDb
                              : TrainSignal.BellSource(layout) is int b and >= 0 ? layout[b].LevelDb : 0f,
                    Head = td.StartOffsetMetres, Speed = MathF.Min(v0, top), TopSpeed = top,
                    LengthMetres = profile.LengthMetres,
                    Accel = td.AccelerationMps2 > 0 ? td.AccelerationMps2 : 0.9f, Brake = brake,
                };
                consist.NextStop = FirstStopAhead(consist.Stops, consist.Head);
                _trains.Add(consist);
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
                {
                    tr.NextStop = (tr.NextStop + 1) % tr.Stops.Length;
                    tr.SinceStop = 0f;
                }
                continue;
            }

            float lookahead = MathF.Max(15f, tr.Speed * tr.Speed / (2f * tr.Brake));
            // The slowest of the whole stretch ahead, not its far end: see RaceLine.SlowestWithin.
            float want = MathF.Min(tr.TopSpeed, tr.Line.SlowestWithin(tr.Head, lookahead));

            // Coming up on a platform. A train's braking rate is a tenth of a car's and its
            // approach is correspondingly long — which is most of why a train arriving sounds
            // like an event rather than like a vehicle turning up.
            // Until it has run clear of the stop it just left, which on a line with one stop is the
            // next stop too: standing within a metre and a half of it at zero speed, it would start
            // dwelling again and never leave.
            if (tr.Stops.Length > 0 && tr.SinceStop > StopClearMetres)
            {
                float d = tr.Stops[tr.NextStop].At - tr.Head;
                if (d < -1f) d += tr.Line.Length;
                d = MathF.Max(0f, d);
                want = MathF.Min(want, MathF.Sqrt(MathF.Max(0f, 2f * tr.Brake * d)));
                if (d <= StopReachMetres && tr.Speed < 1.5f)
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
            tr.SinceStop += tr.Speed * dt;
            if (tr.Head > tr.Line.Length) tr.Head -= tr.Line.Length;

            // Placed first, so a signal is sent from where its horn is this tick.
            PlaceConsist(tr, world);
            SoundForCrossings(tr, world);
        }
    }

    /// <summary>How close to a stop a train has to have come to stand at it, metres.</summary>
    private const float StopReachMetres = 1.5f;
    /// <summary>How far a train runs from a stop before that stop can stop it again: past the
    /// metre and a half it may have stood short, and the metre a stop still counts as ahead.</summary>
    private const float StopClearMetres = 3f;

    /// <summary>
    /// The first stop at or ahead of a place round the line: where a train put there stops first.
    /// Index 0 for every train ran one placed past the first platform a lap round to it, through
    /// every platform on the way.
    /// </summary>
    private static int FirstStopAhead((float At, float Dwell, string Kind)[] stops, float head)
    {
        // A stop up to a metre behind still counts as here, the same allowance Update gives.
        for (int i = 0; i < stops.Length; i++)
            if (stops[i].At >= head - 1f) return i;
        return 0;
    }

    /// <summary>
    /// Long, long, short, long for every level crossing ahead, begun eighteen seconds out and held
    /// until the train is on it — the pattern every North American train sounds, and the reason a
    /// listener hears the train before the bells have told them anything — with the bell rung until
    /// the crossing is reached. Worked out here because this is where the train's speed and the
    /// distance to the crossing are both known; sounded on the train's own horn (or whistle) and bell,
    /// which the client's synth for the train plays from where they are on it (TrainSignal).
    /// </summary>
    private void SoundForCrossings(Consist tr, World world)
    {
        if ((tr.Warning < 0 && tr.Bell < 0) || Heard == null || CrossingsOn == null || tr.Speed < 2f) return;
        foreach (float at in CrossingsOn(tr.MapId, tr.Track))
        {
            float toGo = at - tr.Head;
            if (toGo < 0f) toGo += tr.Line.Length;
            if (toGo > tr.Line.Length * 0.5f) { tr.Sounded.Remove(at); continue; }   // behind us now
            float eta = toGo / tr.Speed;
            if (eta > HornLeadSeconds || tr.Sounded.Contains(at)) continue;
            tr.Sounded.Add(at);
            var source = tr.Entities[tr.Warning >= 0 ? tr.Warning : tr.Bell];
            if (!world.IsAlive(source)) continue;
            // The first three blasts and their gaps take ten seconds; the last is held to arrival.
            var (warning, bell) = TrainSignal.ForCrossing(eta);
            if (tr.Warning < 0) warning = Array.Empty<float>();
            if (tr.Bell < 0) bell = 0f;
            Log.Information("Rail: {Name} sounds for the crossing {ToGo:F0} m ahead ({Eta:F0} s).", tr.Name, toGo, eta);
            Heard(tr.MapId, source.Id, "horn", new[]
            {
                new TransientSound
                {
                    Character = SoundCharacter.Ring,
                    Position = world.Get<Transform>(source).Position,
                    LevelDb = tr.WarningDb,
                    DecaySeconds = TrainSignal.Duration(warning, bell),
                    SynthKey = TrainSignal.Key(tr.Preset, tr.Key, warning, bell),
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
            // The source's own height above the rail, wherever the rail goes. This used to be read
            // back off the transform as "the old height less the rail's height here" and added to
            // the rail's height, which is the old height again: on a slope the source stayed where
            // it had been until it was six metres out.
            pos.Y += tr.Heights[i];
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
    /// lap is. See <see cref="TrainsOn"/> for the back of each train as well.
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

    /// <summary>
    /// Each train on a line: how far round its leading end is, and how long it is, metres. Both,
    /// because a crossing starts ringing for the front of a train and stops ringing for the back of
    /// it, and those are a train's length apart.
    /// </summary>
    public List<(float Head, float Length)> TrainsOn(string mapId, string track, out float lapLength)
    {
        lapLength = 1f;
        var trains = new List<(float, float)>();
        foreach (var tr in _trains)
        {
            if (tr.MapId != mapId || !string.Equals(tr.Track, track, StringComparison.OrdinalIgnoreCase)) continue;
            lapLength = tr.Line.Length;
            trains.Add((tr.Head, tr.LengthMetres));
        }
        return trains;
    }
}
