using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// A vehicle's cabin as a wall between two places: what it takes off a sound that crosses it, either way.
///
/// The same three paths whichever side the listener is on (<see cref="CarWindow.CabinLossDb"/>): the
/// glass by its mass, the seals, and whatever is open. Sitting in a car, the street comes in through
/// them; standing in the street, a person talking in a car comes out through them. Rolled down, the
/// windows are a hole in the side, and both come through it.
///
/// What moves is not in the acoustic scene, so a car's glass was never between anybody and anything:
/// someone talking in a closed car was heard as though they sat in the open. This puts the cabin back
/// as what it is, for the sources that are inside one.
/// </summary>
public sealed class CabinWalls
{
    /// <summary>How far down each vehicle's windows are as this client has them, and when that was worked
    /// out: the glass follows the server's target at the motor's pace (<see cref="CarWindow.Glide"/>).</summary>
    private readonly Dictionary<int, (float Open, double At)> _windows = new();

    /// <summary>
    /// How far down a vehicle's windows are now, 0 shut to 1 fully down. The first time a vehicle is seen
    /// its windows are wherever they were going: a car that drove up with its windows down did not just
    /// roll them down.
    /// </summary>
    public float WindowsOpen(EntitySnapshot car, double now)
    {
        float target = Math.Clamp(car.Definition?.SoundEmitter.WindowsOpen ?? 0f, 0f, 1f);
        if (!_windows.TryGetValue(car.Id, out var w))
        {
            _windows[car.Id] = (target, now);
            return target;
        }
        float dt = (float)Math.Max(0.0, now - w.At);
        float open = CarWindow.Glide(w.Open, target, dt);
        _windows[car.Id] = (open, now);
        return open;
    }

    /// <summary>Forgets vehicles that are gone, so the table does not grow with every car ever seen.</summary>
    public void Forget(Func<int, bool> stillHere)
    {
        List<int>? gone = null;
        foreach (int id in _windows.Keys) if (!stillHere(id)) (gone ??= new()).Add(id);
        if (gone != null) foreach (int id in gone) _windows.Remove(id);
    }

    /// <summary>The vehicle profile an entity is voiced as, or null for anything that is not a vehicle.</summary>
    public static VehicleProfile? Vehicle(EntitySnapshot snap)
    {
        string? sid = snap.Definition?.SoundEmitter.SoundId;
        if (sid == null || !sid.StartsWith("engine:", StringComparison.OrdinalIgnoreCase)) return null;
        string preset = sid[7..];
        return MachineRegistry.Knows(preset) ? MachineRegistry.VehicleFor(preset) : null;
    }

    /// <summary>Whether a vehicle has a closed cabin at all: a motorcycle and a formula car do not.</summary>
    public static bool HasCabin(VehicleProfile v)
        => v.Body is { CabinLengthM: > 0f } && VehicleCabin.Measure(v) is not null;

    /// <summary>
    /// Whether <paramref name="point"/> is inside this vehicle's cabin, as it stands now. A seated person's
    /// feet are on its floor, so the box is given a few centimetres of grace all round.
    /// </summary>
    public static bool Contains(EntitySnapshot car, VehicleProfile v, Vector3 point, float grace = 0.05f)
    {
        if (VehicleCabin.Measure(v) is not { } g) return false;
        var local = Vector3.Transform(point - car.Transform.Position, Quaternion.Inverse(car.Transform.Rotation));
        return MathF.Abs(local.X) <= g.Wc * 0.5f + grace
            && local.Y >= g.FloorTop - grace && local.Y <= g.RoofUnder + grace
            && MathF.Abs(local.Z - g.Cz) <= g.Lc * 0.5f + grace;
    }

    /// <summary>The vehicle whose cabin <paramref name="point"/> is in, if any. Only moving things are
    /// asked, and only those voiced as vehicles: a handful of box tests.</summary>
    public static bool TryFind(WorldSnapshot world, Vector3 point, out EntitySnapshot car, out VehicleProfile profile)
    {
        foreach (var snap in world.DynamicEntities)
        {
            if (Vehicle(snap) is not { } v || !HasCabin(v)) continue;
            if (!Contains(snap, v, point)) continue;
            car = snap; profile = v;
            return true;
        }
        car = default; profile = null!;
        return false;
    }

    /// <summary>
    /// What a vehicle's cabin takes off a sound crossing it, dB (negative) at the mixer's three bands, with
    /// its side windows <paramref name="windowsOpen"/> of the way down and <paramref name="otherOpenShare"/>
    /// of its wall open some other way (a bus's doorway).
    /// </summary>
    public static (float Low, float Mid, float High) LossDb(VehicleProfile v, float windowsOpen, float otherOpenShare = 0f)
    {
        if (v.Body is not { CabinLengthM: > 0f } body) return (0f, 0f, 0f);
        return CarWindow.CabinLossDb(body, CarWindow.OpenShare(v, windowsOpen) + otherOpenShare);
    }

    public static float Gain(float db) => MathF.Pow(10f, db / 20f);
}
