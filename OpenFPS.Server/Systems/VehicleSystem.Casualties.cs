using System;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Somebody walking who has been shot dead: they stop where they fell (the update skips them while
/// they carry a <see cref="DeadComponent"/>), and when the body is taken away somebody else comes
/// walking along the same way, so a street does not empty one shot at a time.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>
    /// Takes a dead walker's body away and puts somebody new on the same walk: as far round a loop
    /// as it is possible to be from where the body lay, or at the start of an out-and-back walk, so
    /// nobody gets up where somebody fell. Returns the new walker, or <see cref="Entity.Null"/> if
    /// the body was not one of the map's walkers (it is still taken away).
    /// </summary>
    public Entity ReplaceWalker(string mapId, World world, Entity dead)
    {
        var maps = _maps;
        if (maps == null) return Entity.Null;
        _byEntity.TryGetValue(dead.Id, out var v);

        // What the person was: the same kind of person, with the same voice, comes along instead.
        var name = world.Has<NameComponent>(dead) ? world.Get<NameComponent>(dead) : new NameComponent { Name = "someone walking" };
        var identity = world.Has<IdentityComponent>(dead) ? world.Get<IdentityComponent>(dead) : new IdentityComponent { Name = name.Name };
        var collider = world.Has<ColliderComponent>(dead) ? world.Get<ColliderComponent>(dead)
                     : new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(0.5f, 1.8f, 0.5f), IsSolid = false };
        var person = world.Has<Pedestrian>(dead) ? world.Get<Pedestrian>(dead) : new Pedestrian { Voice = "", Pair = "" };

        int goneId = dead.Id;
        _byEntity.Remove(goneId);
        maps.DestroyEntity(mapId, dead);
        Removed?.Invoke(mapId, goneId);
        if (v == null || v.MapId != mapId) return Entity.Null;

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
            collider, name, identity, person,
            new HealthComponent { Current = 100, Max = 100 }));
        if (e == Entity.Null) return Entity.Null;
        v.Entity = e;
        _byEntity[e.Id] = v;
        Log.Information("Map {Map}: {Name} ({Old}) was taken away; somebody new ({New}) walks the same way.",
                        mapId, v.DisplayName, goneId, e.Id);
        return e;
    }
}
