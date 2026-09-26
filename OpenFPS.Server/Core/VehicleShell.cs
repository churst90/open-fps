using System;
using System.Collections.Generic;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Core;

/// <summary>
/// A vehicle you can get into, built from what its profile already says it is.
///
/// A map names "vehicle:i4_economy" as a composite and gets a hatchback: a floor, a roof, doors below
/// the waist and glass above it, a windscreen and a back window, a bonnet and a boot, and seats. Every
/// number comes from the profile — the body's outside size (<see cref="VehicleProfile.LengthMetres"/>
/// and its two siblings) and the cabin's inside size (<see cref="VehicleBody.CabinLengthM"/>), which
/// were declared for the sound and the collision long before anyone could sit in one. So there is no
/// file per car to rot out of step with the car: change the cabin and the shell you sit in changes.
///
/// The cabin is a closed box on purpose. It is the vehicle's ROOM (<see cref="TryCabin"/>, used by
/// <see cref="CompositeService.RefreshRoom"/>), which is what makes the inside of a car sound like the
/// inside of a car — a small, soft, close space — without anything here saying so. A bus's cabin is
/// long and hard, a hatchback's short and carpeted, and the difference is the materials below.
/// </summary>
public static class VehicleShell
{
    public const string Prefix = "vehicle:";

    /// <summary>The prefabs the shell is made of — see <see cref="VehicleCabin"/>, where the geometry lives
    /// now, shared with the client that traces the same cabin for its sound.</summary>
    private const string Steel = VehicleCabin.Steel;

    /// <summary>Front-to-back room one row of seats takes, metres.</summary>
    private const float RowPitch = 0.85f;

    /// <summary>Whether an id names a generated vehicle shell, and which profile.</summary>
    public static bool TryParse(string id, out string preset)
    {
        preset = "";
        if (!id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        preset = id[Prefix.Length..];
        return MachineRegistry.Knows(preset);
    }

    private static VehicleCabin.Geometry? Measure(VehicleProfile v) => VehicleCabin.Measure(v);

    /// <summary>The cabin as a box in the vehicle's own frame — what its room is.</summary>
    public static bool TryCabin(string preset, out Vector3 centre, out Vector3 size)
    {
        centre = size = Vector3.Zero;
        if (!MachineRegistry.Knows(preset) || Measure(MachineRegistry.VehicleFor(preset)) is not { } g) return false;
        centre = new Vector3(0f, g.FloorTop + g.Hc * 0.5f, g.Cz);
        size = new Vector3(g.Wc, g.Hc, g.Lc);
        return true;
    }

    /// <summary>
    /// The template for a profile. <paramref name="prefabSize"/> answers how big each prefab is
    /// before scaling, so a part can be scaled to exactly the box it should be.
    /// </summary>
    public static CompositeTemplate Build(string preset, Func<string, Vector3> prefabSize)
    {
        var v = MachineRegistry.VehicleFor(preset);
        var body = v.Body ?? VehicleBody.Saloon;
        var parts = new List<CompositePart>();
        var seats = new List<SeatDefinition>();

        if (Measure(v) is not { } g)
        {
            // A motorcycle, a formula car: nothing to be inside. One solid body at saddle height and a
            // seat on top of it — you are sitting ON this, out in the air, and that is the point.
            float saddle = MathF.Min(v.HeightMetres, 0.9f);
            parts.Add(Box(Steel, new Vector3(0f, saddle * 0.5f + 0.1f, 0f),
                          new Vector3(v.WidthMetres, saddle - 0.2f, v.LengthMetres), prefabSize));
            seats.Add(new SeatDefinition { Name = "rider", Position = Vector3.Zero, Controls = true });
            return Template(preset, v, parts, seats);
        }

        foreach (var (prefab, at, size) in VehicleCabin.Shell(v, g))
            parts.Add(Box(prefab, at, size, prefabSize));

        // Seats, in rows from the front: the driver on the left, as on every vehicle on this map.
        int rows = Math.Max(1, (int)((g.Lc - 0.3f) / RowPitch));
        float firstRow = g.Front - 0.75f;
        float offset = g.Wc * 0.25f;
        for (int r = 0; r < rows; r++)
        {
            float z = firstRow - r * RowPitch;
            if (r == 0)
            {
                seats.Add(new SeatDefinition { Name = "driver", Position = new Vector3(-offset, g.FloorTop, z), Controls = true });
                seats.Add(new SeatDefinition { Name = "front passenger", Position = new Vector3(offset, g.FloorTop, z) });
            }
            else
            {
                string row = rows == 2 ? "rear" : $"row {r + 1}";
                seats.Add(new SeatDefinition { Name = $"{row} left", Position = new Vector3(-offset, g.FloorTop, z) });
                seats.Add(new SeatDefinition { Name = $"{row} right", Position = new Vector3(offset, g.FloorTop, z) });
            }
        }
        return Template(preset, v, parts, seats);
    }

    private static CompositeTemplate Template(string preset, VehicleProfile v, List<CompositePart> parts, List<SeatDefinition> seats)
        => new()
        {
            Id = Prefix + preset,
            Name = v.Name,
            Description = $"A {v.Name}. Press E beside it to get in.",
            Anchored = false,
            Parts = parts,
            Seats = seats,
            VehiclePreset = preset,
        };

    /// <summary>A prefab scaled to be exactly <paramref name="size"/>, centred at <paramref name="at"/>.</summary>
    private static CompositePart Box(string prefab, Vector3 at, Vector3 size, Func<string, Vector3> prefabSize)
    {
        var unit = prefabSize(prefab);
        return new CompositePart
        {
            PrefabId = prefab,
            Position = at,
            Scale = new Vector3(size.X / MathF.Max(1e-4f, unit.X),
                                size.Y / MathF.Max(1e-4f, unit.Y),
                                size.Z / MathF.Max(1e-4f, unit.Z)),
        };
    }
}
