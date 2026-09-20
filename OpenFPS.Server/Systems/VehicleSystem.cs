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
public sealed class VehicleSystem
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
        /// <summary>How hard this vehicle CHOOSES to corner, which set its racing line. The tyres are
        /// measured against <see cref="Grip"/>, which may be more.</summary>
        public float CorneringG = 1f;

        /// <summary>Flat-ground grip in g, from the map. Only the LONGITUDINAL half of the demand
        /// needs it; the lateral half comes out of the racing line, which already knows the bank.</summary>
        public float Grip = 1f;
    }

    private enum State { Waiting, Driving, Turning }

    private readonly List<DemoVehicle> _vehicles = new();

    /// <summary>Spawns every vehicle a map declares. Call once after the maps are loaded.</summary>
    public void Spawn(MapManager maps)
    {
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
                var hull = isAircraft
                    ? new Vector3(MathF.Max(8f, air!.CruiseSpeedMps * 0.12f), 6f, MathF.Max(12f, air.CruiseSpeedMps * 0.2f))
                    : isMachine ? new Vector3(0.6f, 1.0f, 0.9f)
                    : isWalker ? new Vector3(0.5f, 1.8f, 0.5f)
                    : new Vector3(1.9f, 1.4f, 4.6f);

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

                var start = vd.RoadStart;
                var heading = MathF.Atan2(vd.RoadEnd.X - vd.RoadStart.X, vd.RoadEnd.Z - vd.RoadStart.Z);
                if (line != null) line.Sample(vd.StartOffsetMetres, out start, out heading, out _);
                string description = isAircraft ? $"{displayKind}, in the air"
                                   : isMachine ? $"{displayKind}, being worked"
                                   : isWalker ? "walking"
                                   : $"{displayKind}, driving the road";
                var e = isWalker
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
                    new ColliderComponent { Shape = ColliderShape.Box, Size = hull, IsSolid = false },
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
                // A racer is already at speed when the world starts; it is a lap in progress, not a
                // standing start, and a standing start would put eight engines on the limiter at
                // once in the same three seconds.
                if (line != null)
                {
                    line.Sample(v.Lap, out _, out _, out float v0);
                    v.Speed = v0;
                    v.Current = State.Driving;
                }
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

    public void Update(string mapId, World world, float dt)
    {
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || !world.IsAlive(v.Entity)) continue;
            ref var t = ref world.Get<Transform>(v.Entity);
            ref var vel = ref world.Get<Velocity>(v.Entity);
            v.Phase += dt;

            if (v.Line != null)
            {
                UpdateRacer(v, ref t, ref vel, dt);
                if (world.Has<VehicleComponent>(v.Entity))
                {
                    ref var rvc = ref world.Get<VehicleComponent>(v.Entity);
                    rvc.Speed = v.Speed;
                }
                continue;
            }

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
                    if (v.Progress >= total - 0.05f && v.Speed < 0.3f)
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
    private static void UpdateRacer(DemoVehicle v, ref Transform t, ref Velocity vel, float dt)
    {
        var line = v.Line!;
        float lookahead = MathF.Max(8f, v.Speed * v.Speed / (2f * v.Brake));
        line.Sample(v.Lap + lookahead, out _, out _, out float ahead);
        line.Sample(v.Lap, out Vector3 here, out float heading, out float now, out float cornerLimit);
        float want = MathF.Min(now, ahead);

        float wasSpeed = v.Speed;
        if (want > v.Speed) v.Speed = MathF.Min(want, v.Speed + v.Accel * dt);
        else v.Speed = MathF.Max(want, v.Speed - v.Brake * dt);

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
        t.Position = here;
        t.Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
        t.IsDirty = true;
        vel.Linear = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading)) * v.Speed;
    }

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
}
