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

/// <summary>A place on a vehicle's route to pull in to: beside a door somebody could be going to.</summary>
internal sealed class ParkingSpot
{
    /// <summary>Metres round the vehicle's own line.</summary>
    public float At;
    /// <summary>How far toward the kerb it pulls over, metres.</summary>
    public float Shift;
    public Entity Door;
    /// <summary>Just outside the door, on the street side, and just inside it.</summary>
    public Vector3 Outside, Inside;
}

internal enum ParkPhase { Approach, Parked, PullOut }

internal sealed class ParkState
{
    public required ParkingSpot Spot;
    public ParkPhase Phase = ParkPhase.Approach;
    /// <summary>Seconds since it stopped at the kerb.</summary>
    public float Clock;
    /// <summary>How long the car stands empty, seconds.</summary>
    public float Away;
    /// <summary>The person, while they are out of the car and not yet indoors.</summary>
    public Entity Driver = Entity.Null;
    public Vector3[] Route = Array.Empty<Vector3>();
    public int Leg;
    /// <summary>Which of the things along the way have happened.</summary>
    public int Step;
    public bool Chirp;
}

/// <summary>
/// Somebody parking, going in somewhere, and coming back.
///
/// Every sound in it is one the world already makes for its own reasons: the engine running down
/// because it has been switched off, a car door that is the same steel-skin-and-seal door a player's
/// car has, footsteps because a body is walking, a building's door because it was opened. What is new
/// is only the order a person does them in. Where it can happen is found from the map, not placed:
/// a stretch of road with a street-level door beside it and nothing solid in between.
/// </summary>
public sealed partial class VehicleSystem
{
    private MapManager? _maps;

    /// <summary>An entity has gone and everyone must be told. Set by the server.</summary>
    public Action<string, int>? Removed { get; set; }
    /// <summary>An entity's sound emitter changed (an engine switched off or on). Set by the server.</summary>
    public Action<int>? AudioChanged { get; set; }

    private readonly HashSet<string> _spotsFound = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _doorsInUse = new();

    /// <summary>Walking pace, m/s.</summary>
    private const float WalkSpeed = 1.35f;
    /// <summary>How far to the side a door may be from the lane and still be one you would park for.</summary>
    private const float MinDoorOffset = 3.5f, MaxDoorOffset = 16f;

    private static Vector3 RightOf(float heading)
        => Vector3.Transform(Vector3.UnitX, Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f));

    // ── Finding the places ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every street-level door each car's route passes within a few metres of, on the kerb side,
    /// with a clear walk between them. Done once, a couple of seconds in, because a building's door is
    /// one of its parts and has no world position until the parts have been placed.
    /// </summary>
    private void FindSpots(string mapId, World world)
    {
        if (!_spotsFound.Add(mapId)) return;
        var doors = new List<(Entity E, Vector3 P, Vector3 N, Vector3 Size)>();
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent, ColliderComponent>(),
            (Entity e, ref Transform t, ref DoorComponent d, ref ColliderComponent c) =>
                doors.Add((e, t.Position, Vector3.Transform(Vector3.UnitZ, t.Rotation), c.Size)));
        var solids = new List<(Vector3 P, Quaternion R, Vector3 Half)>();
        world.Query(new QueryDescription().WithAll<Transform, ColliderComponent>().WithNone<Velocity, DoorComponent>(),
            (Entity e, ref Transform t, ref ColliderComponent c) =>
            {
                if (c.IsSolid && c.Shape == ColliderShape.Box && c.Size.X > 0 && c.Size.Y > 0 && c.Size.Z > 0)
                    solids.Add((t.Position, t.Rotation, c.Size * 0.5f));
            });

        int total = 0;
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || !v.OnStreet || v.Line == null || v.Horn.StartsWith("air:")) continue;
            var line = v.Line;
            var samples = new List<(float At, Vector3 P, float H)>();
            for (float at = 0f; at < line.Length; at += 2f)
            {
                line.Sample(at, out var p, out float h, out _);
                samples.Add((at, p, h));
            }
            foreach (var d in doors)
            {
                if (_doorsInUse.Contains(d.E.Id)) continue;
                var best = samples.MinBy(sm => Vector2.DistanceSquared(new(sm.P.X, sm.P.Z), new(d.P.X, d.P.Z)));
                if (MathF.Abs(d.P.Y - best.P.Y) > 2.2f) continue;       // an upstairs door
                var right = RightOf(best.H);
                var toDoor = d.P - best.P; toDoor.Y = 0f;
                float side = Vector3.Dot(toDoor, right);
                float along = MathF.Abs(Vector3.Dot(toDoor, Vector3.Normalize(new Vector3(right.Z, 0f, -right.X))));
                if (side < MinDoorOffset || side > MaxDoorOffset || along > 3f) continue;   // kerb side, level with it
                var n = d.N; n.Y = 0f;
                if (n.LengthSquared() < 1e-4f) continue;
                n = Vector3.Normalize(n);
                if (Vector3.Dot(n, -toDoor) < 0f) n = -n;                                  // the street side
                float floor = d.P.Y - d.Size.Y * 0.5f;
                var outside = d.P + n * 0.9f; outside.Y = floor;
                var inside = d.P - n * 1.4f; inside.Y = floor;
                var kerb = best.P + right * 2.4f;
                if (Blocked(solids, kerb + Vector3.UnitY, outside + Vector3.UnitY)) continue;
                if (Blocked(solids, d.P - n * 0.35f + Vector3.UnitY * 0.0f, inside + Vector3.UnitY)) continue;
                v.Spots.Add(new ParkingSpot { At = best.At, Shift = 2.0f, Door = d.E, Outside = outside, Inside = inside });
            }
            total += v.Spots.Count;
        }
        Log.Information("Map {Map}: {Count} place(s) to pull in beside a door, across the traffic.", mapId, total);
    }

    /// <summary>Whether a straight walk from a to b runs into anything solid.</summary>
    private static bool Blocked(List<(Vector3 P, Quaternion R, Vector3 Half)> solids, Vector3 a, Vector3 b)
    {
        var lo = Vector3.Min(a, b); var hi = Vector3.Max(a, b);
        foreach (var (p, r, half) in solids)
        {
            float reach = half.Length();
            if (p.X + reach < lo.X || p.X - reach > hi.X || p.Z + reach < lo.Z || p.Z - reach > hi.Z
                || p.Y + reach < lo.Y || p.Y - reach > hi.Y) continue;
            var inv = Quaternion.Inverse(r);
            var la = Vector3.Transform(a - p, inv);
            var lb = Vector3.Transform(b - p, inv);
            if (SegmentHitsBox(la, lb, half)) return true;
        }
        return false;
    }

    private static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 half)
    {
        var d = b - a;
        float t0 = 0f, t1 = 1f;
        for (int i = 0; i < 3; i++)
        {
            float ai = i == 0 ? a.X : i == 1 ? a.Y : a.Z;
            float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float hi = i == 0 ? half.X : i == 1 ? half.Y : half.Z;
            if (MathF.Abs(di) < 1e-6f) { if (ai < -hi || ai > hi) return false; continue; }
            float ta = (-hi - ai) / di, tb = (hi - ai) / di;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return true;
    }

    // ── Deciding to park ───────────────────────────────────────────────────────────────────────

    private void MaybePark(string mapId, World world, StreetLifeData life, float dt, double clock)
    {
        if (clock > 2.0) FindSpots(mapId, world);
        if (!_spotsFound.Contains(mapId) || !Chance(life.ParkEverySeconds, dt)) return;
        var v = Pick(mapId, world, c => c.Spots.Count > 0 && c.Park == null && c.HardBrakeLeft <= 0f
                                        && c.DwellLeft <= 0f && c.Speed > 3f);
        if (v == null) return;
        // The first free spot far enough ahead to pull in to without standing on the brakes.
        float needs = v.Speed * v.Speed / (2f * v.Brake) + 15f;
        ParkingSpot? chosen = null; float nearest = float.MaxValue;
        foreach (var sp in v.Spots)
        {
            if (_doorsInUse.Contains(sp.Door.Id) || !world.IsAlive(sp.Door)) continue;
            float ahead = sp.At - v.Lap;
            if (ahead < 0f) ahead += v.Line!.Length;
            if (ahead < needs || ahead > 400f || ahead >= nearest) continue;
            nearest = ahead; chosen = sp;
        }
        if (chosen == null) return;
        _doorsInUse.Add(chosen.Door.Id);
        v.Park = new ParkState
        {
            Spot = chosen,
            Away = 60f + 180f * (float)_streetRng.NextDouble(),
            Chirp = _streetRng.NextDouble() < 0.4,
        };
        Log.Information("Street: {Name} will pull in {Ahead:F0} m ahead, by the door of entity {Door}, for {Away:F0} s.",
                        v.DisplayName, nearest, chosen.Door.Id, v.Park.Away);
    }

    // ── Standing at the kerb ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A parked car and the person it brought, moved on one tick. True while it is holding the car
    /// still, in which case the racer does not run.
    /// </summary>
    private bool HoldParked(DemoVehicle v, World world, ref Transform t, ref Velocity vel, float dt)
    {
        if (v.Park is not { Phase: ParkPhase.Parked } pk) return false;
        var line = v.Line!;
        line.Sample(v.Lap, out var here, out float heading, out _);
        t.Position = here + RightOf(heading) * v.KerbShift;
        t.Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f);
        t.IsDirty = true;
        v.Speed = 0f;
        vel.Linear = Vector3.Zero;
        v.TyreDemand = 0f;
        pk.Clock += dt;

        var forward = Vector3.Transform(Vector3.UnitZ, t.Rotation);
        var right = RightOf(heading);
        // The driver's door is on the street side.
        var driverDoor = t.Position - right * 0.95f + forward * 0.2f + Vector3.UnitY * 0.55f;
        var standBy = t.Position - right * 1.5f + forward * 0.2f;
        var kerb = t.Position - forward * 3.2f + right * 2.4f;
        var roadBehind = t.Position - forward * 3.2f - right * 0.4f;

        // What happens, in order: a list of (at what point, what). Step counts through it.
        switch (pk.Step)
        {
            case 0 when pk.Clock >= 1.2f:                      // key off
                SetRunning(world, v, false); pk.Step++; break;
            case 1 when pk.Clock >= 2.2f:                      // door, out, door
                Say(v, "car door", OccupancyService.CarDoorSounds(driverDoor, forward, 1.4f));
                pk.Step++; break;
            case 2 when pk.Clock >= 3.0f:                      // standing beside it
                pk.Driver = SpawnPerson(v, standBy, heading);
                pk.Route = new[] { roadBehind, kerb, pk.Spot.Outside };
                pk.Leg = 0; pk.Step++; break;
            case 3:                                            // round the back of it to the door
                if (pk.Chirp && pk.Clock >= 5.0f) { pk.Chirp = false; ChirpLock(world, v); }
                if (Walk(world, pk, dt)) { DoorSystem.Set(world, pk.Spot.Door, true); pk.Clock = 0f; pk.Step++; }
                break;
            case 4 when pk.Clock >= 1.3f:                      // through it
                pk.Route = new[] { pk.Spot.Inside }; pk.Leg = 0; pk.Step++; break;
            case 5:
                if (Walk(world, pk, dt)) { pk.Clock = 0f; pk.Step++; }
                break;
            case 6 when pk.Clock >= 1.0f:                      // shut it behind them, and they are in
                DoorSystem.Set(world, pk.Spot.Door, false);
                RemovePerson(v, pk);
                pk.Clock = 0f; pk.Step++; break;
            case 7 when pk.Clock >= pk.Away:                   // and back out
                DoorSystem.Set(world, pk.Spot.Door, true);
                pk.Clock = 0f; pk.Step++; break;
            case 8 when pk.Clock >= 1.3f:
                pk.Driver = SpawnPerson(v, pk.Spot.Inside, heading);
                pk.Route = new[] { pk.Spot.Outside }; pk.Leg = 0; pk.Step++; break;
            case 9:
                if (Walk(world, pk, dt)) { pk.Clock = 0f; pk.Step++; }
                break;
            case 10 when pk.Clock >= 0.8f:
                DoorSystem.Set(world, pk.Spot.Door, false);
                pk.Route = new[] { kerb, roadBehind, standBy }; pk.Leg = 0; pk.Step++; break;
            case 11:
                if (Walk(world, pk, dt))
                {
                    Say(v, "car door", OccupancyService.CarDoorSounds(driverDoor, forward, 1.2f));
                    pk.Clock = 0f; pk.Step++;
                }
                break;
            case 12 when pk.Clock >= 0.9f:                     // in
                RemovePerson(v, pk); pk.Step++; break;
            case 13 when pk.Clock >= 3.0f:                     // key on
                SetRunning(world, v, true); pk.Step++; break;
            case 14 when pk.Clock >= 6.5f:                     // mirror, signal, away
                pk.Phase = ParkPhase.PullOut;
                _doorsInUse.Remove(pk.Spot.Door.Id);
                Log.Information("Street: {Name} pulls out.", v.DisplayName);
                break;
        }
        return pk.Phase == ParkPhase.Parked;
    }

    /// <summary>Moves the person one tick along their route. True once they reach the end of it.</summary>
    private static bool Walk(World world, ParkState pk, float dt)
    {
        if (pk.Driver == Entity.Null || !world.IsAlive(pk.Driver)) return true;
        ref var t = ref world.Get<Transform>(pk.Driver);
        ref var vel = ref world.Get<Velocity>(pk.Driver);
        while (pk.Leg < pk.Route.Length)
        {
            var target = pk.Route[pk.Leg];
            var to = target - t.Position; to.Y = 0f;
            float dist = to.Length();
            if (dist < 0.15f) { pk.Leg++; continue; }
            var dir = to / dist;
            float step = MathF.Min(dist, WalkSpeed * dt);
            t.Position += dir * step;
            t.Position = new Vector3(t.Position.X, target.Y, t.Position.Z);
            t.Rotation = Quaternion.CreateFromYawPitchRoll(MathF.Atan2(dir.X, dir.Z), 0f, 0f);
            t.IsDirty = true;
            vel.Linear = dir * WalkSpeed;
            return false;
        }
        vel.Linear = Vector3.Zero;
        return true;
    }

    private Entity SpawnPerson(DemoVehicle v, Vector3 at, float heading)
    {
        if (_maps == null) return Entity.Null;
        // The same body a walker on the map has: no sound of its own, heard by its feet.
        return _maps.SpawnEntity(v.MapId, w => w.Create(
            EntityType.NPC,
            new Transform { Position = at, Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f) },
            new Velocity { Linear = Vector3.Zero },
            new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.75f, 0.35f), IsSolid = false },
            new NameComponent { Name = "driver of " + v.DisplayName },
            new IdentityComponent { Name = "someone", Description = "on foot, from a parked car" }));
    }

    private void RemovePerson(DemoVehicle v, ParkState pk)
    {
        if (pk.Driver == Entity.Null || _maps == null) return;
        int id = pk.Driver.Id;
        _maps.DestroyEntity(v.MapId, pk.Driver);
        Removed?.Invoke(v.MapId, id);
        pk.Driver = Entity.Null;
    }

    private void SetRunning(World world, DemoVehicle v, bool running)
    {
        if (!world.Has<SoundEmitterComponent>(v.Entity)) return;
        ref var em = ref world.Get<SoundEmitterComponent>(v.Entity);
        if (em.SynthRunning == running) return;
        em.SynthRunning = running;
        AudioChanged?.Invoke(v.Entity.Id);
    }

    private void Say(DemoVehicle v, string label, IReadOnlyList<TransientSound> sounds)
        => Heard?.Invoke(v.MapId, v.Entity.Id, label, sounds);

    /// <summary>A lot of cars answer the lock button with a touch of the horn.</summary>
    private void ChirpLock(World world, DemoVehicle v)
    {
        if (v.Horn.Length == 0) return;
        Honk(v.MapId, world, v, new[] { 0.045f });
    }
}
