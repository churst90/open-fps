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
/// The cabin is a closed box on purpose. A composite that encloses space grows a ROOM
/// (<see cref="CompositeService.RefreshRoom"/>), which is what makes the inside of a car sound like
/// the inside of a car — a small, soft, close space — without anything here saying so. A bus's cabin
/// is long and hard, a hatchback's short and carpeted, and the difference is the materials below.
/// </summary>
public static class VehicleShell
{
    public const string Prefix = "vehicle:";

    /// <summary>The prefabs the shell is made of. Plain boxes, scaled: see <see cref="Box"/>.</summary>
    private const string Glass = "glass_wall", Steel = "metal_wall", Carpet = "carpet_floor";

    /// <summary>How thick a panel is. Enough for the collision to find it, not a structural claim.</summary>
    private const float Skin = 0.05f;

    /// <summary>Front-to-back room one row of seats takes, metres.</summary>
    private const float RowPitch = 0.85f;

    /// <summary>
    /// Where the waist of the body is, as a fraction of the cabin's height: steel below, glass above.
    /// A car's window line is a little under halfway up the cabin; a bus's is about the same.
    /// </summary>
    private const float BeltFraction = 0.45f;

    /// <summary>Whether an id names a generated vehicle shell, and which profile.</summary>
    public static bool TryParse(string id, out string preset)
    {
        preset = "";
        if (!id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        preset = id[Prefix.Length..];
        return MachineRegistry.Knows(preset);
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

        float L = v.LengthMetres, W = v.WidthMetres, H = v.HeightMetres;
        bool hasCabin = body.CabinLengthM > 0f && body.CabinWidthM > 0f && body.CabinHeightM > 0f;

        if (!hasCabin)
        {
            // A motorcycle, a formula car: nothing to be inside. One solid body at saddle height and a
            // seat on top of it — you are sitting ON this, out in the air, and that is the point.
            float saddle = MathF.Min(H, 0.9f);
            parts.Add(Box(Steel, new Vector3(0f, saddle * 0.5f + 0.1f, 0f), new Vector3(W, saddle - 0.2f, L), prefabSize));
            seats.Add(new SeatDefinition { Name = "rider", Position = Vector3.Zero, Controls = true });
            return Template(preset, v, parts, seats);
        }

        // The cabin inside the body. Never longer or wider than the body that holds it — a school bus
        // declares an eleven-metre cabin in a ten-point-nine-metre body, because the cabin was sized
        // for its acoustics and the body for its bumpers.
        float Lc = MathF.Min(body.CabinLengthM, L - 0.4f);
        float Wc = MathF.Min(body.CabinWidthM, W - 2f * Skin);
        float Hc = MathF.Min(body.CabinHeightM, H - 0.2f);
        // The floor is wherever the roof leaves room for the cabin: a quarter of a metre up in a car,
        // over a metre in a bus, which is why you climb steps to get into one.
        float floorTop = MathF.Max(0.2f, H - Hc - Skin);
        float roofUnder = floorTop + Hc;
        float belt = floorTop + BeltFraction * Hc;

        // Most of what is not cabin is in front of it: the engine is at the front of nearly everything
        // on this map, and the boot is the shorter end.
        float spare = L - Lc;
        float frontLen = spare * 0.6f, rearLen = spare - frontLen;
        float cz = L * 0.5f - frontLen - Lc * 0.5f;              // the cabin's centre, along the body
        float front = cz + Lc * 0.5f, back = cz - Lc * 0.5f;

        // Whether the inside is soft. Seats, carpet and a headliner in a car; a bus or a van is
        // hard plastic and steel, which is exactly what CabinAbsorption already says.
        string lining = body.CabinAbsorption >= 0.25f ? Carpet : Steel;

        // Underneath, from just off the road to the floor. Not open space: a player must not walk
        // under a bus, and a bus's floor is over a metre up.
        float chassisBottom = 0.15f;
        if (floorTop - Skin - chassisBottom > 0.02f)
            parts.Add(Box(Steel, new Vector3(0f, (chassisBottom + floorTop - Skin) * 0.5f, cz),
                          new Vector3(Wc, floorTop - Skin - chassisBottom, Lc), prefabSize));
        parts.Add(Box(lining, new Vector3(0f, floorTop - Skin * 0.5f, cz), new Vector3(Wc, Skin, Lc), prefabSize));
        parts.Add(Box(lining, new Vector3(0f, roofUnder + Skin * 0.5f, cz), new Vector3(Wc + 2f * Skin, Skin, Lc), prefabSize));

        // The sides: steel doors to the waist, glass to the roof.
        foreach (float side in new[] { -1f, 1f })
        {
            float x = side * (Wc * 0.5f + Skin * 0.5f);
            parts.Add(Box(Steel, new Vector3(x, (floorTop + belt) * 0.5f, cz), new Vector3(Skin, belt - floorTop, Lc), prefabSize));
            parts.Add(Box(Glass, new Vector3(x, (belt + roofUnder) * 0.5f, cz), new Vector3(Skin, roofUnder - belt, Lc), prefabSize));
        }

        // Windscreen and back window, full height: the bulkhead below the screen is the bonnet's job.
        parts.Add(Box(Glass, new Vector3(0f, (floorTop + roofUnder) * 0.5f, front + Skin * 0.5f),
                      new Vector3(Wc + 2f * Skin, Hc, Skin), prefabSize));
        parts.Add(Box(Glass, new Vector3(0f, (floorTop + roofUnder) * 0.5f, back - Skin * 0.5f),
                      new Vector3(Wc + 2f * Skin, Hc, Skin), prefabSize));

        // The bonnet and the boot, solid to the waist. They are the ends you walk into.
        if (frontLen > 0.2f)
            parts.Add(Box(Steel, new Vector3(0f, (chassisBottom + belt) * 0.5f, front + Skin + (frontLen - Skin) * 0.5f),
                          new Vector3(W, belt - chassisBottom, frontLen - Skin), prefabSize));
        if (rearLen > 0.2f)
            parts.Add(Box(Steel, new Vector3(0f, (chassisBottom + belt) * 0.5f, back - Skin - (rearLen - Skin) * 0.5f),
                          new Vector3(W, belt - chassisBottom, rearLen - Skin), prefabSize));

        // Seats, in rows from the front: the driver on the left, as on every vehicle on this map.
        int rows = Math.Max(1, (int)((Lc - 0.3f) / RowPitch));
        float firstRow = front - 0.75f;
        float offset = Wc * 0.25f;
        for (int r = 0; r < rows; r++)
        {
            float z = firstRow - r * RowPitch;
            if (r == 0)
            {
                seats.Add(new SeatDefinition { Name = "driver", Position = new Vector3(-offset, floorTop, z), Controls = true });
                seats.Add(new SeatDefinition { Name = "front passenger", Position = new Vector3(offset, floorTop, z) });
            }
            else
            {
                string row = rows == 2 ? "rear" : $"row {r + 1}";
                seats.Add(new SeatDefinition { Name = $"{row} left", Position = new Vector3(-offset, floorTop, z) });
                seats.Add(new SeatDefinition { Name = $"{row} right", Position = new Vector3(offset, floorTop, z) });
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
