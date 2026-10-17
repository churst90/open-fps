using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Acoustics;
using OpenFPS.Sound;

namespace OpenFPS.Client.AudioEngine.Acoustics;

/// <summary>
/// A vehicle's cabin as a wall: what it takes off a sound crossing it, either way, by the same three
/// paths (<see cref="CarWindow.CabinLossDb"/>): the glass by its mass, the seals, and whatever is open.
/// What moves is not in the acoustic scene, so without this someone talking in a closed car was heard
/// as though they sat in the open.
/// </summary>
public sealed class CabinWalls
{
    /// <summary>Each vehicle's windows as this client has them, and when: the glass follows the host's
    /// target at the motor's pace (<see cref="CarWindow.Glide"/>).</summary>
    private readonly Dictionary<int, (float Open, double At)> _windows = new();

    /// <summary>
    /// 0 shut to 1 fully down. A vehicle seen for the first time has its windows where they were going: a
    /// car that drove up with them down did not just roll them down.
    /// </summary>
    public float WindowsOpen(AcousticBody car, double now) => WindowsOpen(car.Id, car.WindowsOpen, now);

    /// <summary>The same, for vehicle <paramref name="id"/> whose windows are going to <paramref name="target"/>.</summary>
    public float WindowsOpen(int id, float target, double now)
    {
        target = Math.Clamp(target, 0f, 1f);
        if (!_windows.TryGetValue(id, out var w))
        {
            _windows[id] = (target, now);
            return target;
        }
        float dt = (float)Math.Max(0.0, now - w.At);
        float open = CarWindow.Glide(w.Open, target, dt);
        _windows[id] = (open, now);
        return open;
    }

    /// <summary>Forgets vehicles that are gone.</summary>
    public void Forget(Func<int, bool> stillHere)
    {
        List<int>? gone = null;
        foreach (int id in _windows.Keys) if (!stillHere(id)) (gone ??= new()).Add(id);
        if (gone != null) foreach (int id in gone) _windows.Remove(id);
    }

    /// <summary>The vehicle profile a thing is voiced as, or null for anything that is not a vehicle.</summary>
    public static VehicleProfile? Vehicle(AcousticBody body) => Vehicle(body.SoundId);

    /// <summary>The vehicle profile a sound id names, or null.</summary>
    public static VehicleProfile? Vehicle(string? soundId) => ModelKindTable.Vehicle(soundId);

    /// <summary>Whether a vehicle has a closed cabin at all: a motorcycle and a formula car do not.</summary>
    public static bool HasCabin(VehicleProfile v)
        => v.Body is { CabinLengthM: > 0f } && VehicleCabin.Measure(v) is not null;

    /// <summary>
    /// Whether <paramref name="point"/> is inside the cabin now; a few centimetres of grace all round,
    /// since a seated person's feet are on its floor.
    /// </summary>
    public static bool Contains(AcousticBody car, VehicleProfile v, Vector3 point, float grace = 0.05f)
    {
        if (VehicleCabin.Measure(v) is not { } g) return false;
        var local = Vector3.Transform(point - car.Position, Quaternion.Inverse(car.Rotation));
        return MathF.Abs(local.X) <= g.Wc * 0.5f + grace
            && local.Y >= g.FloorTop - grace && local.Y <= g.RoofUnder + grace
            && MathF.Abs(local.Z - g.Cz) <= g.Lc * 0.5f + grace;
    }

    /// <summary>The vehicle whose cabin <paramref name="point"/> is in, if any: a handful of box tests over
    /// the moving things voiced as vehicles.</summary>
    public static bool TryFind(IAcousticWorld world, Vector3 point, out AcousticBody car, out VehicleProfile profile)
    {
        for (int i = 0, n = world.MovingCount; i < n; i++)
        {
            var body = world.MovingAt(i);
            if (Vehicle(body) is not { } v || !HasCabin(v)) continue;
            if (!Contains(body, v, point)) continue;
            car = body; profile = v;
            return true;
        }
        car = default; profile = null!;
        return false;
    }

    /// <summary>
    /// dB (negative) at the mixer's three bands, with the side windows <paramref name="windowsOpen"/> of
    /// the way down and <paramref name="otherOpenShare"/> of the wall open some other way (a bus's doorway).
    /// </summary>
    public static (float Low, float Mid, float High) LossDb(VehicleProfile v, float windowsOpen, float otherOpenShare = 0f)
    {
        if (v.Body is not { CabinLengthM: > 0f } body) return (0f, 0f, 0f);
        return CarWindow.CabinLossDb(body, CarWindow.OpenShare(v, windowsOpen) + otherOpenShare);
    }

    public static float Gain(float db) => MathF.Pow(10f, db / 20f);
}
