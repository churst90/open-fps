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
/// Drives the map's vehicles along their roads.
///
/// A vehicle is an ordinary dynamic entity — a transform, a velocity, a collider so the spatial grid
/// carries it into every client's earshot, and a sound emitter whose id names an engine preset. The
/// client hears that id and runs the engine live, following the speed this system reports, so all
/// the server decides is where the car is and how fast it is going. Which is as it should be: the
/// server knows nothing about exhausts.
///
/// A vehicle either SHUTTLES a straight road or LAPS a circuit.
///
/// Shuttling is the demonstration case: a straight line between two points, out and back, at each
/// speed in a list. The car sits at one end idling, pulls away, holds the speed, brakes to a stop at
/// the other end, waits, turns round, and comes back at the next speed.
///
/// Lapping is a race. The car follows a <see cref="MapData.Tracks"/> centreline offset onto its own
/// line, and its speed at every point of the lap is decided by the CURVATURE there: the fastest it
/// can go through a bend of radius R at g lateral g is sqrt(g * 9.81 * R), and no faster. A backward
/// pass round the loop then pulls each limit down to whatever the brakes can still shed before the
/// next slower point, which is how a real racing line is computed and why the car starts slowing
/// before the corner rather than at it. That profile is the entire sound of a lap — an engine
/// pulling all the way down the straight, a lift and a settle through the turn, and back on it at
/// the exit — and none of it is scripted; it falls out of the shape of the track and the grip of
/// the car. Give two cars different grip and top speed and they lap at different rates, catch each
/// other and pass, with no notion of racing anywhere in the code.
/// </summary>
public sealed partial class VehicleSystem
{
    private sealed class DemoVehicle
    {
        public required Entity Entity;
        public required string MapId;
        public required Vector3 A, B;
        public required float[] SpeedsMetresPerSecond;
        public required float Accel, Brake, TurnSeconds, WaitSeconds;
        public int Pass;
        public float Speed;
        public float Phase;                    // seconds in the current state
        public Vector3 From, To;
        public float Heading;                  // radians, the way the nose points
        public float HeadingFrom, HeadingTo;
        public State Current = State.Waiting;
        public float Progress;                 // metres along From -> To

        // Racing. Null for a shuttling vehicle.
        public RaceLine? Line;
        public float Lap;                      // metres round the circuit
        public int Laps;
        public string DisplayName = "";

        /// <summary>How hard the tyres are working, 0..2, 1 being the limit. Worked out HERE because
        /// this is the only place that knows the corner: the racing line's speed limit already has
        /// the banking in it, so the fraction of it being used is the fraction of the grip being
        /// used, and no listener can arrive at that from a velocity alone.</summary>
        public float TyreDemand;

        /// <summary>Where this vehicle stops on its route, in order round the lap. Empty for
        /// anything that does not — a car does not use a bus stop.</summary>
        public (float At, float Dwell, string Kind)[] Stops = System.Array.Empty<(float, float, string)>();
        /// <summary>Which stop it is heading for, an index into <see cref="Stops"/>.</summary>
        public int NextStop;
        /// <summary>Seconds left standing. Above zero means it is AT a stop, not driving to one.</summary>
        public float DwellLeft;
        /// <summary>The stop it has just left, and how far round it was when it left — so it does
        /// not immediately see the stop it is standing on and serve it again for ever.</summary>
        public int LeftStop = -1;
        public float LeftAtLap;
        /// <summary>How hard this vehicle CHOOSES to corner, which set its racing line. The tyres are
        /// measured against <see cref="Grip"/>, which may be more.</summary>
        public float CorneringG = 1f;

        /// <summary>Flat-ground grip in g, from the map. Only the LONGITUDINAL half of the demand
        /// needs it; the lateral half comes out of the racing line, which already knows the bank.</summary>
        public float Grip = 1f;

        /// <summary>The vehicle preset, and the horn that comes with it ("" for none).</summary>
        public string Preset = "";
        public string Horn = "";
        /// <summary>A road vehicle on a street: something with a driver who can honk, brake hard or
        /// park. Not an aircraft, a mower, a walker or a racing car.</summary>
        public bool OnStreet;

        /// <summary>Standing on the brakes: seconds left, the speed being braked to, and how hard.</summary>
        public float HardBrakeLeft;
        public float HardBrakeTo;
        public float HardBrakeDecel;

        /// <summary>A kerb this vehicle can pull in to, near a door somebody could be going to.</summary>
        public List<ParkingSpot> Spots = new();
        /// <summary>What it is doing about parking, or null when it is simply driving.</summary>
        public ParkState? Park;
        /// <summary>How far toward the kerb it is sitting off its lane, metres.</summary>
        public float KerbShift;
    }

    private enum State { Waiting, Driving, Turning }

    private readonly List<DemoVehicle> _vehicles = new();

    /// <summary>Spawns every vehicle a map declares. Call once after the maps are loaded.</summary>
    /// <param name="shells">
    /// Builds the body of a vehicle people can ride in. Without it (the tests that only want traffic)
    /// every vehicle is a bare moving box, as it always was.
    /// </param>
    public void Spawn(MapManager maps, CompositeService? shells = null)
    {
        _maps = maps;
        foreach (var entry in maps.GetAllMaps())
        {
            string mapId = entry.Key;
            var data = entry.Value.data;
            if (data.Vehicles == null) continue;
            foreach (var vd in data.Vehicles)
            {
                // ── A vehicle, or an aircraft ────────────────────────────────────────────────────
                //
                // The mover does not care which. A shuttle is a straight line between two points in
                // THREE dimensions and always was — RoadStart and RoadEnd carry a Y — so an airliner
                // crossing the map at six hundred metres and a truck going through the tunnel are
                // the same object to this system, and an approach is a shuttle that ends lower than
                // it starts. What differs is only how loud the thing is, how big it is, and which
                // library the client should look the preset up in.
                //
                // That last one is the whole of the coupling: the client reads the SoundId's prefix
                // and runs the right model. Deciding it HERE, from which library actually has the
                // preset, means a map names "airliner" and gets an airliner without anything else
                // on either side having to be told about aircraft.
                bool isAircraft = AircraftProfile.Presets.ContainsKey(vd.Preset);
                // ── ...or a small machine, or a person ──────────────────────────────────────────
                //
                // A push mower moves because somebody is pushing it, at a walking pace, up and down
                // a garden; and a person moves because they are walking. Both are the same object to
                // this system as a truck: a thing on a line between two points at a speed. What
                // differs is the voice — a small machine's is "machine:<preset>" (see the client's
                // physical voice path), and a walker has none at all, because the client hears a
                // body with legs by its footsteps, which it makes from the body's own movement.
                bool isMachine = !isAircraft && SmallMachineSpec.Presets.ContainsKey(vd.Preset);
                bool isWalker = string.Equals(vd.Preset, "walker", StringComparison.OrdinalIgnoreCase);
                if (!isAircraft && !isMachine && !isWalker && !MachineRegistry.Knows(vd.Preset))
                {
                    Log.Warning("Map {Map}: vehicle preset '{Preset}' is not known; known: {Known}",
                                mapId, vd.Preset, string.Join(", ", MachineRegistry.Ids));
                    continue;
                }
                var air = isAircraft ? AircraftProfile.ByName(vd.Preset) : null;
                var machine = isMachine ? SmallMachineSpec.ByName(vd.Preset) : null;
                var profile = isAircraft || isMachine || isWalker ? null : MachineRegistry.VehicleFor(vd.Preset);
                string displayKind = isAircraft ? air!.Name : isMachine ? machine!.Name : isWalker ? "someone walking" : profile!.Engine.Name;
                float sourceLevelDb = isAircraft ? air!.SourceLevelDb : isMachine ? machine!.SourceLevelDb : isWalker ? 0f : profile!.SourceLevelDb;
                string prefix = isAircraft ? "aircraft:" : isMachine ? "machine:" : "engine:";
                // An airliner is sixty metres of aeroplane; a car is four and a half of car. The
                // collider is what carries it into a client's earshot through the spatial grid, so
                // an aeroplane sized like a hatchback is one that appears late.
                // An aeroplane's size is its WINGSPAN and its LENGTH, which it now declares. It used
                // to be guessed from cruise speed — a fast aeroplane was a big one — which made a
                // turboprop wider than an airliner is long and had nothing to do with either.
                var hull = isAircraft
                    ? new Vector3(air!.WingspanMetres, 6f, air.LengthMetres)
                    : isMachine ? new Vector3(0.6f, 1.0f, 0.9f)
                    : isWalker ? new Vector3(0.5f, 1.8f, 0.5f)
                    : new Vector3(profile!.WidthMetres, profile.HeightMetres, profile.LengthMetres);
                // SOLID, so you cannot walk through it. A car, a bus, a mower: all of them. Not an
                // aeroplane, whose box is wingspan by length and would wall off the empty air under a
                // wing, and not a pedestrian, who steps round you rather than shoving you along the
                // pavement. A car that drives into you pushes you out of its way — the movement
                // solver lifts a player out of whatever they are inside — and that is all for now:
                // being hit hurting is its own piece of work.
                bool solid = !isAircraft && !isWalker;

                // A vehicle that names a track laps it; one that does not shuttles its road.
                RaceLine? line = null;
                float accel = vd.AccelerationMps2 > 0 ? vd.AccelerationMps2 : 3.2f;
                float brake = vd.BrakingMps2 > 0 ? vd.BrakingMps2 : 5.5f;
                if (!string.IsNullOrEmpty(vd.Track))
                {
                    var track = data.Tracks?.Find(t => string.Equals(t.Id, vd.Track, StringComparison.OrdinalIgnoreCase));
                    if (track == null || track.Waypoints.Count < 3)
                    {
                        Log.Warning("Map {Map}: vehicle '{Name}' asks for track '{Track}', which is missing or has fewer than three waypoints; it will not be spawned.",
                                    mapId, vd.Name ?? displayKind, vd.Track);
                        continue;
                    }
                    float topSpeed = (vd.TopSpeedKmh > 0 ? vd.TopSpeedKmh : 200f) / 3.6f;
                    float grip = vd.CorneringG > 0 ? vd.CorneringG : 1.0f;
                    // A car may not pull further off the centreline than the surface it is on.
                    float half = MathF.Max(0f, track.WidthMetres * 0.5f - 1.2f);
                    float lane = Math.Clamp(vd.LaneOffsetMetres, -half, half);
                    try { line = new RaceLine(track.Waypoints, lane, topSpeed, grip, brake, track.BankingDegrees); }
                    catch (Exception ex)
                    {
                        Log.Warning("Map {Map}: track '{Track}' could not be turned into a racing line: {Error}", mapId, vd.Track, ex.Message);
                        continue;
                    }
                }

                // No track and no road: a shuttle from a point to the same point, whose direction is
                // a zero vector normalised — NaN, written into its position and sent to every client.
                if (line == null && Vector3.DistanceSquared(vd.RoadStart, vd.RoadEnd) < 0.01f)
                {
                    Log.Warning("Map {Map}: vehicle '{Name}' has neither a track nor a road (RoadStart and RoadEnd are the same point); it will not be spawned.",
                                mapId, vd.Name ?? displayKind);
                    continue;
                }

                var start = vd.RoadStart;
                var heading = MathF.Atan2(vd.RoadEnd.X - vd.RoadStart.X, vd.RoadEnd.Z - vd.RoadStart.Z);
                if (line != null) line.Sample(vd.StartOffsetMetres, out start, out heading, out _);
                string description = isAircraft ? $"{displayKind}, in the air"
                                   : isMachine ? $"{displayKind}, being worked"
                                   : isWalker ? "walking"
                                   : $"{displayKind}, driving the road";
                // ── A bus you can get on ────────────────────────────────────────────────────────
                //
                // A vehicle that stops at a BUS STOP is one that takes passengers, and that is the
                // whole test — nothing on the map says "boardable". It gets a real body with seats,
                // the same shell a parked car has, and a person waiting at the stop gets on when it
                // stops. Everything else about it is the traffic it always was.
                Entity e = Entity.Null;
                if (shells != null && profile != null && line != null && TakesPassengers(data, vd)
                    && maps.TryGetMap(mapId, out var shellWorld, out _, out _, out _))
                {
                    e = shells.InstantiateForTraffic(mapId, vd.Preset, start, Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f));
                    if (e != Entity.Null)
                    {
                        SetOrAdd(shellWorld, e, EntityType.NPC);
                        SetOrAdd(shellWorld, e, new ColliderComponent { Shape = ColliderShape.Box, Size = hull, IsSolid = solid });
                        SetOrAdd(shellWorld, e, new NameComponent { Name = vd.Name ?? displayKind });
                        SetOrAdd(shellWorld, e, new IdentityComponent { Name = vd.Name ?? displayKind, Description = description, Announce = true });
                        SetOrAdd(shellWorld, e, new VehicleComponent { VehicleType = vd.Preset, MaxSeats = shellWorld.Get<OccupancyComponent>(e).Seats.Count });
                        SetOrAdd(shellWorld, e, new SoundEmitterComponent
                        {
                            IsSynth = true,
                            SoundId = prefix + vd.Preset,
                            Mode = PlaybackMode.LoopOne,
                            Volume = 1f,
                            Range = Loudness.AudibleRange(sourceLevelDb),
                            MinDistance = 3f,
                        });
                        Log.Information("Map {Map}: {Name} takes passengers — {Seats} seat(s).",
                                        mapId, vd.Name ?? displayKind, shellWorld.Get<OccupancyComponent>(e).Seats.Count);
                    }
                }
                if (e == Entity.Null)
                e = isWalker
                    ? maps.SpawnEntity(mapId, w => w.Create(
                    EntityType.NPC,
                    new Transform { Position = start, Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f) },
                    new Velocity { Linear = Vector3.Zero },
                    new ColliderComponent { Shape = ColliderShape.Box, Size = hull, IsSolid = false },
                    new NameComponent { Name = vd.Name ?? displayKind },
                    new IdentityComponent { Name = vd.Name ?? displayKind, Description = description }))
                    : maps.SpawnEntity(mapId, w => w.Create(
                    EntityType.NPC,
                    new Transform { Position = start, Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f) },
                    new Velocity { Linear = Vector3.Zero },
                    new ColliderComponent { Shape = ColliderShape.Box, Size = hull, IsSolid = solid },
                    new NameComponent { Name = vd.Name ?? displayKind },
                    new IdentityComponent { Name = vd.Name ?? displayKind, Description = description },
                    new VehicleComponent { VehicleType = vd.Preset, MaxSeats = isAircraft || isMachine ? 0 : 2 },
                    new SoundEmitterComponent
                    {
                        // The client recognises the prefix and runs the engine itself.
                        IsSynth = true,
                        SoundId = prefix + vd.Preset,
                        Mode = PlaybackMode.LoopOne,
                        Volume = 1f,
                        // How far this car actually carries, from how loud it is — the same
                        // calculation the client uses to place it. A flat 220 m was the number the
                        // server then sized its broadcast radius from, so on a one-mile oval the
                        // cars stopped existing for the client round the back of the track.
                        Range = Loudness.AudibleRange(sourceLevelDb),
                        MinDistance = 3f,
                    }));
                if (e == Entity.Null) continue;
                var speeds = vd.SpeedsKmh is { Length: > 0 } ? vd.SpeedsKmh : new[] { 30f, 60f, 90f };
                string display = vd.Name ?? displayKind;
                var v = new DemoVehicle
                {
                    Entity = e, MapId = mapId, A = vd.RoadStart, B = vd.RoadEnd,
                    SpeedsMetresPerSecond = Array.ConvertAll(speeds, k => k / 3.6f),
                    Accel = accel,
                    Brake = brake,
                    TurnSeconds = 2.5f,
                    WaitSeconds = vd.WaitSeconds > 0 ? vd.WaitSeconds : 4f,
                    From = vd.RoadStart, To = vd.RoadEnd, Heading = heading,
                    Phase = -MathF.Max(0f, vd.StartDelaySeconds),
                    Line = line,
                    Lap = vd.StartOffsetMetres,
                    DisplayName = display,
                    // The friction circle, which is NOT the cornering number unless the map says so.
                    Grip = vd.GripG > 0 ? vd.GripG : (vd.CorneringG > 0 ? vd.CorneringG : 1.0f),
                    CorneringG = vd.CorneringG > 0 ? vd.CorneringG : 1.0f,
                };

                // Where this vehicle stops on its route. A stop names who uses it — a bus stop is
                // for buses and a car does not pull into one — so the same track carries the stops
                // for everything that runs it and each vehicle takes the ones that are its own.
                if (line != null)
                {
                    var track = data.Tracks?.Find(tr => string.Equals(tr.Id, vd.Track, StringComparison.OrdinalIgnoreCase));
                    if (track is { Stops.Count: > 0 })
                    {
                        var mine = track.Stops
                            .Where(sp => string.IsNullOrEmpty(sp.ForPreset)
                                      || vd.Preset.Contains(sp.ForPreset!, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(sp => sp.AtMetres)
                            .Select(sp => (At: sp.AtMetres, Dwell: sp.DwellSeconds, Kind: sp.Kind ?? "stop"))
                            .ToArray();
                        v.Stops = mine;
                        // Start heading for the first stop that is actually ahead of it, or it will
                        // drive most of a lap backwards to reach one it has already passed.
                        for (int si = 0; si < mine.Length; si++)
                            if (mine[si].At >= v.Lap) { v.NextStop = si; break; }
                        if (mine.Length > 0)
                            Log.Information("Map {Map}: {Name} stops at {Count} place(s) round '{Track}'.",
                                            mapId, display, mine.Length, vd.Track);
                    }
                }
                // A racer is already at speed when the world starts; it is a lap in progress, not a
                // standing start, and a standing start would put eight engines on the limiter at
                // once in the same three seconds.
                if (line != null)
                {
                    line.Sample(v.Lap, out _, out _, out float v0);
                    v.Speed = v0;
                    v.Current = State.Driving;
                }
                v.Preset = vd.Preset;
                v.Horn = profile != null ? VehicleProfile.HornFor(profile) : "";
                v.OnStreet = data.StreetLife != null && profile != null && !isAircraft && !isMachine && !isWalker;
                if (data.StreetLife != null) _streetLife[mapId] = data.StreetLife;
                _vehicles.Add(v);
                if (line != null)
                    Log.Information("Map {Map}: {Name} (entity {Id}) laps '{Track}' — {Length:F0} m lap, {Min:F0}-{Max:F0} km/h, starting {At:F0} m round",
                                    mapId, display, e.Id, vd.Track, line.Length, line.MinSpeed * 3.6f, line.MaxSpeed * 3.6f, vd.StartOffsetMetres);
                else
                    Log.Information("Map {Map}: spawned {Name} (entity {Id}) on the road {A} -> {B}, passes at {Speeds} km/h",
                                    mapId, display, e.Id, vd.RoadStart, vd.RoadEnd, string.Join("/", speeds));
            }
        }
    }

    /// <summary>Whether a vehicle stops at a bus stop on its route, which is what taking passengers is.</summary>
    private static bool TakesPassengers(MapData data, VehicleData vd)
    {
        var track = data.Tracks?.Find(tr => string.Equals(tr.Id, vd.Track, StringComparison.OrdinalIgnoreCase));
        return track?.Stops != null && track.Stops.Any(sp =>
            string.Equals(sp.Kind, "bus_stop", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(sp.ForPreset) || vd.Preset.Contains(sp.ForPreset!, StringComparison.OrdinalIgnoreCase)));
    }

    private static void SetOrAdd<T>(World world, Entity e, T component)
    {
        if (world.Has<T>(e)) world.Set(e, component); else world.Add(e, component);
    }

    public void Update(string mapId, World world, float dt)
    {
        UpdateStreetLife(mapId, world, dt);
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || !world.IsAlive(v.Entity)) continue;
            ref var t = ref world.Get<Transform>(v.Entity);
            ref var vel = ref world.Get<Velocity>(v.Entity);
            v.Phase += dt;

            if (v.Line != null)
            {
                if (!HoldParked(v, world, ref t, ref vel, dt)) UpdateRacer(v, ref t, ref vel, dt);
                // The same trace a shuttle gets — racers need it more, because a vehicle on a lap
                // that fails to stop looks identical to one that has no stops declared.
                if (Environment.GetEnvironmentVariable("OPENFPS_TRACE_SHUTTLE") is { } rtrace
                    && v.DisplayName.Contains(rtrace, StringComparison.OrdinalIgnoreCase)
                    && (int)(v.Phase * 2) != (int)((v.Phase - dt) * 2))
                    Log.Information("RACER {Name}: lap {Lap:F0}/{Len:F0} m speed {Speed:F1} "
                                  + "next stop {Next} at {At:F0} m, dwell {Dwell:F1}",
                                    v.DisplayName, v.Lap, v.Line.Length, v.Speed,
                                    v.Stops.Length == 0 ? -1 : v.NextStop,
                                    v.Stops.Length == 0 ? -1f : v.Stops[v.NextStop].At, v.DwellLeft);
                if (world.Has<VehicleComponent>(v.Entity))
                {
                    ref var rvc = ref world.Get<VehicleComponent>(v.Entity);
                    rvc.Speed = v.Speed;
                }
                continue;
            }

            // Where a shuttle actually is, twice a second, for one vehicle named by a substring:
            //
            //   OPENFPS_TRACE_SHUTTLE="Mower, garden 2" ./run-server.sh city
            //
            // "The lawnmowers don't move" is a claim about the server that cannot be settled from
            // the client, where a slow machine on a short line sounds the same standing still as
            // crawling. Costs nothing unless the variable is set, and it settled that one in three
            // minutes: the mowers traverse their sixteen metres at 1.1 m/s, turn, wait and come
            // back, exactly as declared. What was missing was audible MOVEMENT, not movement.
            if (Environment.GetEnvironmentVariable("OPENFPS_TRACE_SHUTTLE") is { } trace
                && v.DisplayName.Contains(trace, StringComparison.OrdinalIgnoreCase)
                && (int)(v.Phase * 2) != (int)((v.Phase - dt) * 2))
                Log.Information("SHUTTLE {Name}: {State} pos {Pos} speed {Speed:F2} progress {Prog:F1}",
                                v.DisplayName, v.Current, t.Position, v.Speed, v.Progress);

            switch (v.Current)
            {
                case State.Waiting:
                    v.Speed = 0f;
                    vel.Linear = Vector3.Zero;
                    if (v.Phase >= v.WaitSeconds)
                    {
                        v.Current = State.Driving;
                        v.Phase = 0f;
                        v.Progress = 0f;
                    }
                    break;

                case State.Driving:
                {
                    float total = Vector3.Distance(v.From, v.To);
                    float target = v.SpeedsMetresPerSecond[v.Pass % v.SpeedsMetresPerSecond.Length];
                    float remaining = MathF.Max(0f, total - v.Progress);
                    // The speed the brakes allow with this much road left.
                    float allowed = MathF.Sqrt(MathF.Max(0f, 2f * v.Brake * remaining));
                    float want = MathF.Min(target, allowed);
                    if (want > v.Speed) v.Speed = MathF.Min(want, v.Speed + v.Accel * dt);
                    else v.Speed = MathF.Max(want, v.Speed - v.Brake * dt);
                    v.Progress += v.Speed * dt;
                    var dir = Vector3.Normalize(v.To - v.From);
                    t.Position = v.From + dir * MathF.Min(v.Progress, total);
                    vel.Linear = dir * v.Speed;
                    // Checked AFTER this tick's braking, so it waits for the brake to reach zero:
                    // with no road left the target is nought and the speed gets there on its own.
                    if (v.Progress >= total - 0.05f && v.Speed <= 1e-3f)
                    {
                        v.Speed = 0f;
                        vel.Linear = Vector3.Zero;
                        v.Current = State.Turning;
                        v.Phase = 0f;
                        v.HeadingFrom = v.Heading;
                        v.HeadingTo = v.Heading + MathF.PI;
                        (v.From, v.To) = (v.To, v.From);
                        v.Pass++;
                    }
                    break;
                }

                case State.Turning:
                {
                    float f = Math.Clamp(v.Phase / v.TurnSeconds, 0f, 1f);
                    float s = f * f * (3f - 2f * f);
                    v.Heading = v.HeadingFrom + (v.HeadingTo - v.HeadingFrom) * s;
                    vel.Linear = Vector3.Zero;
                    if (f >= 1f)
                    {
                        v.Heading = MathF.IEEERemainder(v.HeadingTo, 2f * MathF.PI);
                        v.Current = State.Waiting;
                        v.Phase = 0f;
                    }
                    break;
                }
            }
            t.Rotation = Quaternion.CreateFromYawPitchRoll(v.Heading, 0f, 0f);
            t.IsDirty = true;
            if (world.Has<VehicleComponent>(v.Entity))
            {
                ref var vc = ref world.Get<VehicleComponent>(v.Entity);
                vc.Speed = v.Speed;
            }
        }
    }

    /// <summary>
    /// One tick of a car on a circuit: chase the speed the line allows here, and move that far.
    ///
    /// The target is read a BRAKING DISTANCE AHEAD rather than underfoot, because a driver does not
    /// discover a corner on arriving at it. Looking ahead by v^2/2a — exactly the distance this car
    /// needs to shed the speed — is what makes it start lifting at the right place, and it is why a
    /// formula car (which stops in half the distance) stays on the throttle noticeably longer into
    /// the same turn than the stock car beside it.
    /// </summary>
    private void UpdateRacer(DemoVehicle v, ref Transform t, ref Velocity vel, float dt)
    {
        var line = v.Line!;

        // ── Standing at a stop ─────────────────────────────────────────────────────────────────
        //
        // Held STILL, not crawling. Everything a halted vehicle makes is an event read off its own
        // speed going to zero and staying there — the spring brakes after two and a half seconds,
        // the doors, the kneel — and a bus that never quite stops never makes any of it. That is
        // why a city full of buses had no air in it: they were all on racing lines, and a racing
        // line never stops.
        if (v.DwellLeft > 0f)
        {
            // Held at a crossing: it is the CROSSING that lets you go, not a clock.
            //
            // Both halves matter. Counting a declared dwell down at a crossing sends the vehicle
            // over the rails when the timer expires no matter where the train is — traced doing
            // exactly that, pulling away with the train eighty-seven metres out — and releasing
            // only on a timer means it also sits there after the train has long gone. So while the
            // crossing is closed the dwell is topped up, and the instant it opens it is dropped.
            if (v.Stops.Length > 0
                && string.Equals(v.Stops[v.NextStop].Kind, "crossing", StringComparison.OrdinalIgnoreCase)
                && _crossings != null)
            {
                line.Sample(v.Stops[v.NextStop].At, out var gate, out _, out _);
                v.DwellLeft = _crossings.IsClosedAt(v.MapId, gate) ? 1f : 0f;
            }
            v.DwellLeft -= dt;
            v.Speed = 0f;
            vel.Linear = Vector3.Zero;
            line.Sample(v.Lap, out Vector3 at, out float hdg, out _);
            t.Position = at;
            t.Rotation = Quaternion.CreateFromYawPitchRoll(hdg, 0f, 0f);
            t.IsDirty = true;
            if (v.DwellLeft <= 0f && v.Stops.Length > 0)
            {
                v.LeftStop = v.NextStop;
                v.LeftAtLap = v.Lap;
            }
            return;
        }

        float lookahead = MathF.Max(8f, v.Speed * v.Speed / (2f * v.Brake));
        line.Sample(v.Lap + lookahead, out _, out _, out float ahead);
        line.Sample(v.Lap, out Vector3 here, out float heading, out float now, out float cornerLimit);
        float want = MathF.Min(now, ahead);

        // ── Coming up on one ───────────────────────────────────────────────────────────────────
        //
        // The same braking rule the shuttle uses: the fastest it may be going with this much road
        // left and this much brake, v = sqrt(2 a s). So it slows the way a vehicle slows rather
        // than arriving and then stopping, and the deceleration is real — which is what the air
        // system reads to decide the service brakes have been used.
        // ── Standing on the brakes ──────────────────────────────────────────────────────────────
        //
        // Somebody pulled out, or stepped off the kerb. The driver wants to be doing a lot less, now,
        // and brakes at close to what the tyres will give — which is what makes them squeal: the
        // demand below is worked out from the deceleration actually applied, and nothing here asks
        // for a noise.
        float brake = v.Brake;
        if (v.HardBrakeLeft > 0f)
        {
            v.HardBrakeLeft -= dt;
            want = MathF.Min(want, v.HardBrakeTo);
            brake = MathF.Max(v.Brake, v.HardBrakeDecel);
        }

        // ── Pulling in to park ─────────────────────────────────────────────────────────────────
        //
        // The same v = sqrt(2 a s) as a bus stop, to a kerb beside a door; and over the last few car
        // lengths it eases across toward the kerb, so it stops out of the lane rather than in it.
        if (v.Park is { Phase: ParkPhase.Approach } pk)
        {
            float toPark = pk.Spot.At - v.Lap;
            if (toPark < -1f) toPark += line.Length;
            want = MathF.Min(want, MathF.Sqrt(MathF.Max(0f, 2f * brake * MathF.Max(0f, toPark))));
            v.KerbShift = pk.Spot.Shift * Math.Clamp(1f - (toPark - 2f) / 22f, 0f, 1f);
            if (toPark <= 0.6f && CanHalt(v.Speed, brake, dt))
            {
                v.Speed = 0f;
                vel.Linear = Vector3.Zero;
                pk.Phase = ParkPhase.Parked;
                pk.Clock = 0f;
                return;
            }
        }
        else if (v.Park is { Phase: ParkPhase.PullOut } po)
        {
            float gone = v.Lap - po.Spot.At;
            if (gone < 0f) gone += line.Length;
            v.KerbShift = po.Spot.Shift * Math.Clamp(1f - gone / 18f, 0f, 1f);
            if (gone > 18f) { v.KerbShift = 0f; v.Park = null; }
        }

        float toStop = DistanceToNextStop(v, line);
        if (toStop < float.MaxValue)
        {
            want = MathF.Min(want, MathF.Sqrt(MathF.Max(0f, 2f * v.Brake * toStop)));
            if (toStop <= 0.6f && CanHalt(v.Speed, v.Brake, dt))
            {
                v.DwellLeft = MathF.Max(0.5f, v.Stops[v.NextStop].Dwell);
                v.Speed = 0f;
                vel.Linear = Vector3.Zero;
                return;
            }
        }

        float wasSpeed = v.Speed;
        if (want > v.Speed) v.Speed = MathF.Min(want, v.Speed + v.Accel * dt);
        else v.Speed = MathF.Max(want, v.Speed - brake * dt);

        // What the tyres are being asked for, as a fraction of what they have.
        //
        // LATERALLY it is (v / vlimit)^2, and that is not an approximation: the line's limit speed is
        // the one where lateral acceleration equals the available grip, a = v^2/R either way, so the
        // ratio of accelerations is the square of the ratio of speeds. Crucially the line's limit
        // ALREADY has the banking in it — that was fixed when the cars were found to be lifting four
        // semitones a lap — so a car tracking its line comes out at 1.0 rather than at 1.59.
        // LONGITUDINALLY it is whatever acceleration or braking is actually being applied against the
        // same grip. The two combine in quadrature, because a tyre has one contact patch and cornering
        // and braking come out of the same friction circle.
        // Against the CORNERING limit, not the speed limit. The speed limit is the top speed on a
        // straight and whatever the braking pass allows into a turn, so measuring against it reported
        // a car flat out down the back straight as being at the limit of its grip — which is how
        // "they're all screeching" survived the first attempt at this.
        // AGAINST THE GRIP, not against the line's own limit.
        //
        // The line's limit is sqrt(CorneringG * 9.81 * R), so (v / cornerLimit)^2 is the fraction of
        // the CORNERING number being used — and a vehicle tracking its line is at 1.0 of that by
        // construction, in every corner, for ever. That is right for a racing line and wrong for a
        // bus, and it is why every vehicle on a city map screeched through every junction.
        //
        // What the tyre is actually being asked for is the fraction of its GRIP. The radius drops
        // out: R = cornerLimit^2 / (CorneringG * g), so the grip-limited speed at the same corner is
        // cornerLimit * sqrt(Grip / CorneringG), and the fraction of grip used is therefore
        // (v / cornerLimit)^2 * (CorneringG / Grip). With GripG unset the two are equal, the ratio is
        // one, and the speedway is unchanged to the bit.
        float cornerShare = v.Grip > 0.01f ? Math.Clamp(v.CorneringG / v.Grip, 0f, 1f) : 1f;
        float latFraction = float.IsInfinity(cornerLimit) || cornerLimit < 0.5f
            ? 0f
            : (v.Speed / cornerLimit) * (v.Speed / cornerLimit) * cornerShare;
        float longFraction = v.Grip > 0.01f ? MathF.Abs(v.Speed - wasSpeed) / MathF.Max(1e-4f, dt) / (v.Grip * 9.81f) : 0f;
        v.TyreDemand = MathF.Min(2f, MathF.Sqrt(latFraction * latFraction + longFraction * longFraction));

        float before = v.Lap;
        v.Lap += v.Speed * dt;
        if (v.Lap >= line.Length) { v.Lap -= line.Length; v.Laps++; }
        else if (before > v.Lap) v.Laps++;

        line.Sample(v.Lap, out here, out heading, out _);
        t.Position = here + (v.KerbShift != 0f ? RightOf(heading) * v.KerbShift : Vector3.Zero);
        t.Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
        t.IsDirty = true;
        vel.Linear = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)) * v.Speed;
    }

    /// <summary>
    /// Whether a vehicle this slow can be brought to rest in one tick on its own brake. It used to be
    /// released at under 1.2 m/s (a shuttle, 0.3) and set to zero on the spot: 36 m/s^2 on a
    /// 3 m/s^2 bus, a lurch at the end of every stop, and the engine was handed that as its target.
    /// The approach curve, v = sqrt(2 a s), already brings the speed down to this as it arrives.
    /// </summary>
    private static bool CanHalt(float speed, float brake, float dt) => speed <= brake * dt + 1e-3f;

    /// <summary>
    /// Road left to the next stop this vehicle must actually make, metres, or MaxValue if there is
    /// none ahead. Sets <see cref="DemoVehicle.NextStop"/> to whichever that is.
    ///
    /// It SCANS, every tick, rather than walking a stored index forward. The first version held an
    /// index and only advanced it on arrival, which works for one stop and fails for two: a vehicle
    /// locked on to a crossing three hundred metres ahead drove straight over the one under its
    /// wheels, because that one was not the stop it was thinking about. Traced exactly that way —
    /// "next stop 0 at 48 m" held for a whole lap while the van crossed the rails at 328 m at
    /// thirteen metres a second with the bells going.
    ///
    /// An OPEN crossing is not a stop at all and is skipped here, which is what makes traffic flow
    /// over it and queue at it without either being a special case further down.
    /// </summary>
    private float DistanceToNextStop(DemoVehicle v, RaceLine line)
    {
        if (v.Stops.Length == 0) return float.MaxValue;
        float best = float.MaxValue;
        int bestIdx = -1;
        for (int i = 0; i < v.Stops.Length; i++)
        {
            float d = v.Stops[i].At - v.Lap;
            if (d < -1f) d += line.Length;              // it is round the other side
            if (d >= best) continue;

            // The one it has just served, until it is properly clear of it. Without this a
            // vehicle standing on a stop sees a stop nought metres ahead and serves it again,
            // for ever.
            if (i == v.LeftStop)
            {
                float since = v.Lap - v.LeftAtLap;
                if (since < 0f) since += line.Length;
                if (since < 25f) continue;
            }

            if (string.Equals(v.Stops[i].Kind, "crossing", StringComparison.OrdinalIgnoreCase))
            {
                line.Sample(v.Stops[i].At, out var gate, out _, out _);
                if (_crossings == null || !_crossings.IsClosedAt(v.MapId, gate)) continue;
            }

            best = d; bestIdx = i;
        }
        if (bestIdx < 0) return float.MaxValue;
        v.NextStop = bestIdx;
        return MathF.Max(0f, best);
    }

    private CrossingSystem? _crossings;

    /// <summary>
    /// Hands the road the crossings, so a stop of kind "crossing" can ask whether it is closed.
    ///
    /// One direction only, and deliberately: the road reads the crossing, the crossing reads the
    /// trains, and the trains read nothing. A train does not slow for a level crossing and does not
    /// need to know one is there.
    /// </summary>
    public void SetCrossings(CrossingSystem crossings) => _crossings = crossings;

    /// <summary>How hard a vehicle is working its tyres, 0..2 with 1 the limit. False for anything
    /// that is not one of ours.</summary>
    public bool TryGetTyreDemand(int entityId, out float demand)
    {
        foreach (var v in _vehicles)
            if (v.Entity.Id == entityId) { demand = v.TyreDemand; return true; }
        demand = 0f;
        return false;
    }

    public int Count => _vehicles.Count;

    /// <summary>What a vehicle is doing right now, for tests: the physics is otherwise only visible
    /// through where it puts the entity.</summary>
    internal readonly record struct Inspection(float Speed, float Lap, int Laps, float DwellLeft, float KerbShift,
                                               float TyreDemand, float LapLength, int NextStop, float Brake, float Accel,
                                               bool OnStreet, string Horn, float Wait,
                                               int Spots, string Park, int ParkStep, float SpotAt, float SpotShift);

    internal bool TryInspect(int entityId, out Inspection state)
    {
        foreach (var v in _vehicles)
            if (v.Entity.Id == entityId)
            {
                state = new Inspection(v.Speed, v.Lap, v.Laps, v.DwellLeft, v.KerbShift, v.TyreDemand,
                                       v.Line?.Length ?? 0f, v.NextStop, v.Brake, v.Accel, v.OnStreet, v.Horn, v.WaitSeconds,
                                       v.Spots.Count, v.Park?.Phase.ToString() ?? "", v.Park?.Step ?? -1,
                                       v.Park?.Spot.At ?? -1f, v.Park?.Spot.Shift ?? 0f);
                return true;
            }
        state = default;
        return false;
    }
}
