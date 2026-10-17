using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Street life: honks, hard stops, parking, alarms and gunfire, each a random event with a map-wide
/// mean interval (<see cref="StreetLifeData"/>) landing on a vehicle picked at random. The events' laws
/// are StreetEvents'; this steps them over the map's vehicles and sends what is heard.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>How a sound leaves this system: map, source entity, label, the sounds. The server's
    /// world-audio broadcast; null in tests.</summary>
    public Action<string, int, string, IReadOnlyList<TransientSound>>? Heard { get; set; }

    private readonly Dictionary<string, StreetLifeData> _streetLife = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _streetRng = new(20260923);
    private readonly List<(string Map, DemoVehicle V, double At, float[] Pattern)> _pendingHonks = new();
    private readonly Dictionary<string, double> _streetClocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Map, DemoVehicle V, double At, WeaponDefinition Gun, float Yaw)> _pendingShots = new();

    private void UpdateStreetLife(string mapId, World world, float dt)
    {
        if (!_streetLife.TryGetValue(mapId, out var life)) return;
        double clock = _streetClocks[mapId] = _streetClocks.GetValueOrDefault(mapId) + dt;

        if (Chance(life.HornEverySeconds, dt)
            && Pick(mapId, world, v => v.Horn.Length > 0 && v.Park == null) is { } honker)
        {
            var pattern = OpenFPS.Common.Honk.Everyday(_streetRng);
            Honk(mapId, world, honker, pattern);
            if (_streetRng.NextDouble() < YellAfterHonk)
                Yell(mapId, world, honker, StreetLines.Impatient, OpenFPS.Common.Honk.Duration(pattern) + 0.2f, clock);
        }

        if (Chance(life.HardBrakeEverySeconds, dt)
            && Pick(mapId, world, v => v.Line != null && v.Park == null && v.DwellLeft <= 0f && v.HardBrakeLeft <= 0f
                                       && v.Speed >= StreetEvents.HardBrakeMinSpeed) is { } braker)
        {
            BrakeHard(braker, StreetEvents.HardStopTo(_streetRng, braker.Speed));
            Log.Information("Street: {Name} brakes hard, {From:F0} -> {To:F0} km/h at {Decel:F1} m/s^2.",
                            braker.DisplayName, braker.Speed * 3.6f, braker.HardBrakeTo * 3.6f, braker.HardBrakeDecel);
            if (braker.Horn.Length > 0 && _streetRng.NextDouble() < StreetEvents.HonkAfterHardBrake)
                _pendingHonks.Add((mapId, braker, clock + 0.4 + 0.5 * _streetRng.NextDouble(),
                                   OpenFPS.Common.Honk.Startled(_streetRng)));
            if (_streetRng.NextDouble() < YellAfterHardBrake)
                Yell(mapId, world, braker, StreetLines.Startled, 1.0f + 0.6f * (float)_streetRng.NextDouble(), clock);
        }

        MaybePark(mapId, world, life, dt, clock);
        UpdateDrivers(mapId, world, clock);

        if (Chance(life.AlarmEverySeconds, dt)
            && Pick(mapId, world, v => v.Horn.StartsWith("electric:") && v.Park is { Phase: ParkPhase.Parked, Step: 7 }) is { } alarmed)
        {
            var pattern = AlarmPattern(_streetRng);
            Log.Information("Street: {Name}'s alarm goes off for {Seconds:F0} s.", alarmed.DisplayName, OpenFPS.Common.Honk.Duration(pattern));
            Honk(mapId, world, alarmed, pattern);
        }

        // Somebody walking fires two to four rounds.
        if (Chance(life.GunfireEverySeconds, dt)
            && Pick(mapId, world, v => v.Preset.Equals("walker", StringComparison.OrdinalIgnoreCase), streetOnly: false) is { } shooter)
        {
            var (gun, yaw, rounds) = StreetEvents.Gunfire(_streetRng, WeaponRegistry.All.ToList(), clock);
            foreach (double at in rounds) _pendingShots.Add((mapId, shooter, at, gun, yaw));
            Log.Information("Street: {Name} fires {Rounds} rounds from a {Gun}.", shooter.DisplayName, rounds.Length, gun.DisplayName);
        }
        for (int i = _pendingShots.Count - 1; i >= 0; i--)
        {
            var p = _pendingShots[i];
            if (p.Map != mapId || clock < p.At) continue;
            _pendingShots.RemoveAt(i);
            if (world.IsAlive(p.V.Entity)) Shoot(mapId, world, p.V, p.Gun, p.Yaw);
        }

        for (int i = _pendingHonks.Count - 1; i >= 0; i--)
        {
            var p = _pendingHonks[i];
            if (p.Map != mapId || clock < p.At) continue;
            _pendingHonks.RemoveAt(i);
            if (world.IsAlive(p.V.Entity)) Honk(mapId, world, p.V, p.Pattern);
        }
    }

    /// <summary>One round (StreetEvents.Shot), from where the one firing stands.</summary>
    private void Shoot(string mapId, World world, DemoVehicle v, WeaponDefinition gun, float yaw)
    {
        if (Heard == null) return;
        Heard(mapId, v.Entity.Id, gun.DisplayName, new[] { StreetEvents.Shot(world.Get<Transform>(v.Entity).Position, gun, yaw) });
    }

    /// <summary>An emergency stop down to <paramref name="toSpeed"/>, at what the tyres will give.</summary>
    private static void BrakeHard(DemoVehicle v, float toSpeed)
    {
        v.HardBrakeTo = toSpeed;
        (v.HardBrakeDecel, v.HardBrakeLeft) = StreetEvents.HardStop(v.Speed, toSpeed, v.Grip, v.Gravity);
    }

    /// <summary>The same stop, asked for by map and entity, for tests.</summary>
    internal bool BrakeHard(string mapId, int entityId, float toSpeed)
    {
        foreach (var v in _vehicles)
            if (v.MapId == mapId && v.Entity.Id == entityId) { BrakeHard(v, toSpeed); return true; }
        return false;
    }

    /// <summary>One draw of an event with this mean interval over this step (StreetEvents.Chance).</summary>
    private bool Chance(float everySeconds, float dt) => StreetEvents.Chance(_streetRng, everySeconds, dt);

    /// <summary>One of the map's vehicles, picked at random (StreetEvents.Pick).</summary>
    /// <param name="streetOnly">Only the traffic (the default); false lets the people walking be
    /// chosen, who are not traffic and never honk or park.</param>
    private DemoVehicle? Pick(string mapId, World world, Func<DemoVehicle, bool> eligible, bool streetOnly = true)
        => StreetEvents.Pick(_streetRng, _vehicles.Where(v => v.MapId == mapId && (!streetOnly || v.OnStreet)
                                                            && world.IsAlive(v.Entity) && eligible(v)));

    private void Honk(string mapId, World world, DemoVehicle v, float[] pattern)
    {
        if (Heard == null || v.Horn.Length == 0) return;
        var at = world.Get<Transform>(v.Entity).Position;
        Log.Information("Street: {Name} sounds its horn ({Horn}) for {Seconds:F2} s.",
                        v.DisplayName, v.Horn, OpenFPS.Common.Honk.Duration(pattern));
        Heard(mapId, v.Entity.Id, "horn", new[] { StreetEvents.Horn(at, v.Horn, pattern) });
    }
}
