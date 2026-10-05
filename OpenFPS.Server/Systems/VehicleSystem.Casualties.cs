using System;
using System.Collections.Generic;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Server.Core;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Somebody walking who has been shot dead. Their body is left lying as an item (Bodies), and the
/// person is taken off the street at once (<see cref="RetireWalker"/>). A minute on, somebody else comes
/// walking the same way (<see cref="ReplaceWalker"/>), from as far round their walk as it is possible to
/// be from where the body lies, so a street does not empty one shot at a time and nobody gets up where
/// somebody fell.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>A walker taken off the street, with what the next one on their walk is to be.</summary>
    private sealed record Retired(DemoVehicle V, NameComponent Name, IdentityComponent Identity,
                                  ColliderComponent Collider, Pedestrian Person);

    /// <summary>Walkers taken off the street and not yet replaced, by the dead one's entity id.</summary>
    private readonly Dictionary<int, Retired> _retired = new();

    /// <summary>
    /// Takes a dead person off the street: out of the world, and every client told. True when they were
    /// one of the map's walkers, and somebody is to walk the same way later (<see cref="ReplaceWalker"/>
    /// with this id). A driver out of a parked car is taken off too, and false: their car waits at the
    /// kerb, and after <see cref="CombatService.WalkerRespawnSeconds"/> somebody comes out of the door
    /// they were going to and drives it away. Anybody else is left for the caller, and false.
    /// </summary>
    public bool RetireWalker(string mapId, World world, Entity dead)
    {
        var maps = _maps;
        if (maps == null) return false;

        foreach (var car in _vehicles)
            if (car.MapId == mapId && car.Park is { } pk && pk.Driver == dead)
            {
                DriverKilled(car, pk);
                Take(maps, mapId, dead);
                return false;
            }

        if (!_byEntity.TryGetValue(dead.Id, out var v) || v.MapId != mapId) return false;
        var name = world.Has<NameComponent>(dead) ? world.Get<NameComponent>(dead) : new NameComponent { Name = "someone walking" };
        var identity = world.Has<IdentityComponent>(dead) ? world.Get<IdentityComponent>(dead) : new IdentityComponent { Name = name.Name };
        var collider = world.Has<ColliderComponent>(dead) ? world.Get<ColliderComponent>(dead)
                     : new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false };
        var person = world.Has<Pedestrian>(dead) ? world.Get<Pedestrian>(dead) : new Pedestrian { Voice = "", Pair = "" };
        _retired[dead.Id] = new Retired(v, name, identity, collider, person);
        _byEntity.Remove(dead.Id);
        // Nobody is on a crossing or waiting at its kerb any more: traffic is not held for them.
        v.ClearedFor = null;
        v.WaitingFor = null;
        v.OnCarriageway = false;
        v.KerbWait = 0f;
        v.Speed = 0f;
        v.Gone = true;
        Take(maps, mapId, dead);
        Log.Information("Map {Map}: {Name} ({Id}) was killed; somebody walks the same way in a while.",
                        mapId, v.DisplayName, dead.Id);
        return true;
    }

    private void Take(MapManager maps, string mapId, Entity dead)
    {
        int id = dead.Id;
        maps.DestroyEntity(mapId, dead);
        Removed?.Invoke(mapId, id);
    }

    /// <summary>
    /// Puts somebody new on a retired walker's walk: as far round a loop as it is possible to be from
    /// where the body lies, or at the start of an out-and-back walk. Returns the new walker, or
    /// <see cref="Entity.Null"/> if that id was not a retired walker.
    /// </summary>
    public Entity ReplaceWalker(string mapId, World world, int deadId)
    {
        var maps = _maps;
        if (maps == null || !_retired.Remove(deadId, out var r)) return Entity.Null;
        var v = r.V;
        if (v.MapId != mapId) return Entity.Null;

        Vector3 start;
        float heading;
        if (v.Line != null)
        {
            v.Lap = (v.Lap + v.Line.Length * 0.5f) % v.Line.Length;
            v.Line.Sample(v.Lap, out start, out heading, out _);
            v.Speed = 0f;
        }
        else
        {
            start = v.From;
            heading = MathF.Atan2(v.To.X - v.From.X, v.To.Z - v.From.Z);
            v.Current = State.Waiting;
            v.Phase = 0f;
            v.Progress = 0f;
            v.Speed = 0f;
        }
        v.Heading = heading;

        var e = maps.SpawnEntity(mapId, w => w.Create(
            EntityType.NPC,
            new Transform { Position = start, Rotation = Quaternion.CreateFromYawPitchRoll(heading, 0f, 0f) },
            new Velocity { Linear = Vector3.Zero },
            r.Collider, r.Name, r.Identity, r.Person,
            new HealthComponent { Current = 100, Max = 100 }));
        if (e == Entity.Null) return Entity.Null;
        v.Entity = e;
        v.Gone = false;
        _byEntity[e.Id] = v;
        Log.Information("Map {Map}: somebody new ({New}) walks the way {Name} ({Old}) walked.",
                        mapId, e.Id, v.DisplayName, deadId);
        return e;
    }

    /// <summary>
    /// A parked car's driver killed on the pavement. They are no longer the car's to walk or to take
    /// indoors (the body is nobody's now), and the car waits at the kerb: as if they had gone in, somebody
    /// comes back out of that door after a minute at least, and drives it away.
    /// </summary>
    private static void DriverKilled(DemoVehicle car, ParkState pk)
    {
        pk.Driver = Entity.Null;
        pk.Route = Array.Empty<Vector3>();
        pk.Leg = 0;
        pk.Chirp = false;
        pk.Away = MathF.Max(pk.Away, (float)CombatService.WalkerRespawnSeconds);
        pk.Clock = 0f;
        pk.Step = 7;
        Log.Information("Street: the driver of {Name} was killed; somebody comes back for it in {Away:F0} s.",
                        car.DisplayName, pk.Away);
    }
}

public sealed partial class VehicleSystem
{
    /// <summary>Tests: every car standing at the kerb or about to, with how far through its visit it is
    /// and the person it brought, if they are out of it.</summary>
    internal IEnumerable<(Entity Car, string Phase, int Step, Entity Driver, float Away)> ParkedForTest(string mapId)
    {
        foreach (var v in _vehicles)
            if (v.MapId == mapId && v.Park is { } pk)
                yield return (v.Entity, pk.Phase.ToString(), pk.Step, pk.Driver, pk.Away);
    }

    /// <summary>Tests: whether a walker's walk has nobody on it, waiting for somebody new.</summary>
    internal bool IsRetiredForTest(int deadId) => _retired.ContainsKey(deadId);
}
