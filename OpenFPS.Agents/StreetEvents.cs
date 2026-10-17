using System.Numerics;
using OpenFPS.Models;
using OpenFPS.Sound;

namespace OpenFPS.Server.Systems;

/// <summary>
/// Street life's laws: honks, hard stops and gunfire, each a random event with a map-wide mean interval
/// (StreetLifeData) landing on somebody picked at random. A honk is sent as the vehicle's horn and a rhythm; a
/// hard stop is only a deceleration, and the client's tyres squeal. Every draw is from the generator the host
/// hands in, in the order the host has always drawn them.
/// </summary>
public static class StreetEvents
{
    /// <summary>Below this a car is not going fast enough for braking hard to be an event.</summary>
    public const float HardBrakeMinSpeed = 8f;
    /// <summary>What an emergency stop asks of the tyres: close to all of it. Past the squeal onset
    /// (TyreFriction.SquealOnset, 0.78), short of locking them.</summary>
    public const float HardBrakeGripFraction = 0.92f;
    /// <summary>How likely a driver who just had to do that is to say something about it.</summary>
    public const double HonkAfterHardBrake = 0.55;

    /// <summary>One draw of an event with this mean interval over this step. Zero or less is off.</summary>
    public static bool Chance(Random rng, float everySeconds, float dt)
        => everySeconds > 0f && rng.NextDouble() < dt / everySeconds;

    /// <summary>One of <paramref name="eligible"/>, every one equally likely, in one pass (reservoir
    /// sampling): a draw for each, so the draws are as many as the eligible.</summary>
    public static T? Pick<T>(Random rng, IEnumerable<T> eligible) where T : class
    {
        int count = 0;
        T? chosen = null;
        foreach (var x in eligible)
            if (rng.Next(++count) == 0) chosen = x;
        return chosen;
    }

    /// <summary>The hardest a driver brakes, m/s²: an emergency stop at what the tyres will give.</summary>
    /// <param name="grip">Flat-ground grip, g.</param>
    /// <param name="gravity">The map's, m/s².</param>
    public static float HardestBrake(float grip, float gravity) => HardBrakeGripFraction * grip * gravity;

    /// <summary>What a hard stop brakes down to: 20 to 40 per cent of the speed, or 1.5 m/s if that is more.</summary>
    public static float HardStopTo(Random rng, float speed) => MathF.Max(1.5f, speed * (0.2f + 0.2f * (float)rng.NextDouble()));

    /// <summary>An emergency stop from <paramref name="speed"/> down to <paramref name="toSpeed"/>: how hard, and
    /// for how long, with six tenths of a second standing on the brakes after.</summary>
    public static (float Decel, float Seconds) HardStop(float speed, float toSpeed, float grip, float gravity)
    {
        float decel = HardestBrake(grip, gravity);
        return (decel, (speed - toSpeed) / decel + 0.6f);
    }

    /// <summary>Somebody walking fires two to four rounds from a gun picked at random, in one direction, as
    /// fast as a finger goes (a self-loader) or a little slower (a pump or a pistol): the gun, the direction
    /// and when each round goes, from <paramref name="from"/>, seconds.</summary>
    public static (WeaponDefinition Gun, float Yaw, double[] At) Gunfire(Random rng, IReadOnlyList<WeaponDefinition> guns, double from)
    {
        var gun = guns[rng.Next(guns.Count)];
        int rounds = 2 + rng.Next(3);
        float yaw = (float)(rng.NextDouble() * Math.PI * 2);
        var at = new double[rounds];
        double t = from;
        for (int r = 0; r < rounds; r++)
        {
            at[r] = t;
            t += 0.14 + 0.4 * rng.NextDouble();
        }
        return (gun, yaw, at);
    }

    /// <summary>
    /// One round, as a player's is sent (CombatService.Fire): the weapon's own synthesis at the muzzle, 1.5 m
    /// up and 0.5 m out from where the one firing stands, at the cartridge's blast level.
    /// </summary>
    public static TransientSound Shot(Vector3 standing, WeaponDefinition gun, float yaw)
    {
        var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var muzzle = standing + new Vector3(0f, 1.5f, 0f) + forward * 0.5f;
        return new TransientSound
        {
            Character = SoundCharacter.Knock,
            Position = muzzle,
            LevelDb = Loudness.MuzzleBlastDb(gun),
            SynthKey = "weapon:" + gun.Id,
            DecaySeconds = 0.6f,
        };
    }

    /// <summary>A horn sounded in a rhythm, from 0.6 m above where the vehicle is.</summary>
    public static TransientSound Horn(Vector3 at, string horn, float[] pattern)
        => new()
        {
            Character = SoundCharacter.Ring,
            Position = at + Vector3.UnitY * 0.6f,
            LevelDb = Honk.LevelDb(horn, ModelLibrary.Presets),
            DecaySeconds = Honk.Duration(pattern),
            Noisiness = 0f,
            SynthKey = Honk.Key(horn, pattern),
        };
}
