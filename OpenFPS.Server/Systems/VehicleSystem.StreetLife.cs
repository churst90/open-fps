using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Arch.Core;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Systems;

/// <summary>
/// The people driving the traffic.
///
/// A racing line is a very good model of how a car goes round a block and no model at all of the
/// person in it. People honk — mostly a tap, sometimes two, now and then a proper lean — and every so
/// often somebody pulls out and a driver has to stand on the brakes, and then leans on the horn. None
/// of it is on a timetable: each is a random event with a map-wide mean interval
/// (<see cref="StreetLifeData"/>), landing on a vehicle picked at random, so it comes "every now and
/// again, from different vehicles" and never as a jam.
///
/// Nothing here makes a sound. A honk is sent as the vehicle's own horn and a rhythm; a hard stop is
/// a deceleration, and the tyres squeal on the client because the deceleration is past what they
/// grip at.
/// </summary>
public sealed partial class VehicleSystem
{
    /// <summary>How a sound leaves this system: map, source entity, label, the sounds. Set by the
    /// server to its world-audio broadcast; null in tests that only want traffic.</summary>
    public Action<string, int, string, IReadOnlyList<TransientSound>>? Heard { get; set; }

    private readonly Dictionary<string, StreetLifeData> _streetLife = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _streetRng = new(20260923);
    private readonly List<(string Map, DemoVehicle V, double At, float[] Pattern)> _pendingHonks = new();
    private readonly Dictionary<string, double> _streetClocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Map, DemoVehicle V, double At, WeaponDefinition Gun, float Yaw)> _pendingShots = new();

    /// <summary>Below this a car is not going fast enough for braking hard to be an event.</summary>
    private const float HardBrakeMinSpeed = 8f;
    /// <summary>What an emergency stop asks of the tyres: close to all of it. Past the squeal onset
    /// (TyreFriction.SquealOnset, 0.78), short of locking them.</summary>
    private const float HardBrakeGripFraction = 0.92f;
    /// <summary>How likely a driver who just had to do that is to say something about it.</summary>
    private const double HonkAfterHardBrake = 0.55;

    private void UpdateStreetLife(string mapId, World world, float dt)
    {
        if (!_streetLife.TryGetValue(mapId, out var life)) return;
        double clock = _streetClocks[mapId] = _streetClocks.GetValueOrDefault(mapId) + dt;

        if (Chance(life.HornEverySeconds, dt)
            && Pick(mapId, world, v => v.Horn.Length > 0 && v.Park == null) is { } honker)
            Honk(mapId, world, honker, OpenFPS.Common.Honk.Everyday(_streetRng));

        if (Chance(life.HardBrakeEverySeconds, dt)
            && Pick(mapId, world, v => v.Line != null && v.Park == null && v.DwellLeft <= 0f && v.HardBrakeLeft <= 0f
                                       && v.Speed >= HardBrakeMinSpeed) is { } braker)
        {
            // Down to a crawl or a third of the speed, whichever is more, as hard as the tyres allow.
            BrakeHard(braker, MathF.Max(1.5f, braker.Speed * (0.2f + 0.2f * (float)_streetRng.NextDouble())));
            Log.Information("Street: {Name} brakes hard, {From:F0} -> {To:F0} km/h at {Decel:F1} m/s^2.",
                            braker.DisplayName, braker.Speed * 3.6f, braker.HardBrakeTo * 3.6f, braker.HardBrakeDecel);
            if (braker.Horn.Length > 0 && _streetRng.NextDouble() < HonkAfterHardBrake)
                _pendingHonks.Add((mapId, braker, clock + 0.4 + 0.5 * _streetRng.NextDouble(),
                                   OpenFPS.Common.Honk.Startled(_streetRng)));
        }

        MaybePark(mapId, world, life, dt, clock);

        // Somebody on the pavement fires two to four rounds. Picked from the people walking, so
        // it comes from wherever they are — the street you are on, or three blocks over.
        if (Chance(life.GunfireEverySeconds, dt)
            && Pick(mapId, world, v => v.Preset.Equals("walker", StringComparison.OrdinalIgnoreCase), streetOnly: false) is { } shooter)
        {
            var guns = WeaponRegistry.All.ToList();
            var gun = guns[_streetRng.Next(guns.Count)];
            int rounds = 2 + _streetRng.Next(3);
            float yaw = (float)(_streetRng.NextDouble() * Math.PI * 2);
            double at = clock;
            for (int r = 0; r < rounds; r++)
            {
                _pendingShots.Add((mapId, shooter, at, gun, yaw));
                // A self-loader fired as fast as a finger goes, a pump or a pistol a little slower.
                at += 0.14 + 0.4 * _streetRng.NextDouble();
            }
            Log.Information("Street: {Name} fires {Rounds} rounds from a {Gun}.", shooter.DisplayName, rounds, gun.DisplayName);
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

    /// <summary>
    /// One round, the way a player's is sent (CommandHandler.HandleFire): the weapon's own synthesis
    /// at the muzzle, a metre and a half up and half a metre out, at the cartridge's blast level. The
    /// world's reflections and reverb make the place.
    /// </summary>
    private void Shoot(string mapId, World world, DemoVehicle v, WeaponDefinition gun, float yaw)
    {
        if (Heard == null) return;
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var muzzle = world.Get<Transform>(v.Entity).Position + new Vector3(0f, 1.5f, 0f) + forward * 0.5f;
        Heard(mapId, v.Entity.Id, gun.DisplayName, new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Knock,
                Position = muzzle,
                LevelDb = Loudness.MuzzleBlastDb(gun),
                SynthKey = "weapon:" + gun.Id,
                DecaySeconds = 0.6f,
            },
        });
    }

    /// <summary>An emergency stop down to <paramref name="toSpeed"/>, at what the tyres will give.</summary>
    private static void BrakeHard(DemoVehicle v, float toSpeed)
    {
        v.HardBrakeTo = toSpeed;
        v.HardBrakeDecel = HardBrakeGripFraction * v.Grip * 9.81f;
        v.HardBrakeLeft = (v.Speed - v.HardBrakeTo) / v.HardBrakeDecel + 0.6f;
    }

    /// <summary>The same stop, asked for by entity, for tests.</summary>
    internal bool BrakeHard(int entityId, float toSpeed)
    {
        foreach (var v in _vehicles)
            if (v.Entity.Id == entityId) { BrakeHard(v, toSpeed); return true; }
        return false;
    }

    /// <summary>One draw of an event with this mean interval over this step. Zero or less is off.</summary>
    private bool Chance(float everySeconds, float dt)
        => everySeconds > 0f && _streetRng.NextDouble() < dt / everySeconds;

    /// <param name="streetOnly">Only the traffic (the default); false lets the people walking be
    /// chosen, who are not traffic and never honk or park.</param>
    private DemoVehicle? Pick(string mapId, World world, Func<DemoVehicle, bool> eligible, bool streetOnly = true)
    {
        int count = 0;
        DemoVehicle? chosen = null;
        foreach (var v in _vehicles)
        {
            if (v.MapId != mapId || (streetOnly && !v.OnStreet) || !world.IsAlive(v.Entity) || !eligible(v)) continue;
            // Reservoir sampling: every eligible vehicle equally likely, in one pass.
            if (_streetRng.Next(++count) == 0) chosen = v;
        }
        return chosen;
    }

    private void Honk(string mapId, World world, DemoVehicle v, float[] pattern)
    {
        if (Heard == null || v.Horn.Length == 0) return;
        var at = world.Get<Transform>(v.Entity).Position;
        Log.Information("Street: {Name} sounds its horn ({Horn}) for {Seconds:F2} s.",
                        v.DisplayName, v.Horn, OpenFPS.Common.Honk.Duration(pattern));
        Heard(mapId, v.Entity.Id, "horn", new[]
        {
            new TransientSound
            {
                Character = SoundCharacter.Ring,
                Position = at + Vector3.UnitY * 0.6f,
                LevelDb = OpenFPS.Common.Honk.LevelDb(v.Horn),
                DecaySeconds = OpenFPS.Common.Honk.Duration(pattern),
                Noisiness = 0f,
                SynthKey = OpenFPS.Common.Honk.Key(v.Horn, pattern),
            },
        });
    }
}
