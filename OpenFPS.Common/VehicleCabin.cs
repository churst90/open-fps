using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenFPS.Common;

/// <summary>
/// A vehicle's body and cabin as boxes, in its own frame (x right, y up from the road, z forward from
/// the middle of the body), from nothing but its profile. The server builds the shell you sit in from
/// this (VehicleShell); the client traces the same cabin for its reverberation, so the inside of a
/// bus sounds like the bus you are sitting in — its size, its steel and glass — and nothing is said
/// twice.
/// </summary>
public static class VehicleCabin
{
    /// <summary>The prefabs the shell is made of, and the acoustic material each one is.</summary>
    public const string Glass = "glass_wall", Steel = "metal_wall", Carpet = "carpet_floor";

    public static string MaterialOf(string prefab) => prefab switch
    {
        Glass => "Glass", Steel => "Metal", Carpet => "Carpet", _ => "Generic",
    };

    /// <summary>How thick a panel is. Enough for the collision to find it, not a structural claim.</summary>
    public const float Skin = 0.05f;

    /// <summary>Where the waist of the body is, as a fraction of the cabin's height: steel below,
    /// glass above.</summary>
    public const float BeltFraction = 0.45f;

    /// <summary>Where the underside of the body is, metres off the road.</summary>
    public const float ChassisBottom = 0.15f;

    public readonly record struct Geometry(float L, float W, float H, float Lc, float Wc, float Hc,
                                           float FloorTop, float Cz, float FrontLen, float RearLen)
    {
        public float Front => Cz + Lc * 0.5f;
        public float Back => Cz - Lc * 0.5f;
        public float RoofUnder => FloorTop + Hc;
        public float Belt => FloorTop + BeltFraction * Hc;
    }

    public static Geometry? Measure(VehicleProfile v)
    {
        var body = v.Body ?? VehicleBody.Saloon;
        if (body.CabinLengthM <= 0f || body.CabinWidthM <= 0f || body.CabinHeightM <= 0f) return null;
        float L = v.LengthMetres, W = v.WidthMetres, H = v.HeightMetres;

        // The cabin inside the body. Never longer or wider than the body that holds it — a school bus
        // declares an eleven-metre cabin in a ten-point-nine-metre body, because the cabin was sized
        // for its acoustics and the body for its bumpers.
        float Lc = MathF.Min(body.CabinLengthM, L - 0.4f);
        float Wc = MathF.Min(body.CabinWidthM, W - 2f * Skin);
        float Hc = MathF.Min(body.CabinHeightM, H - 0.2f);
        // The floor is wherever the roof leaves room for the cabin: a quarter of a metre up in a car,
        // over a metre in a bus, which is why you climb steps to get into one.
        float floorTop = MathF.Max(0.2f, H - Hc - Skin);
        // Most of what is not cabin is in front of it: the engine is at the front of nearly everything
        // on this map, and the boot is the shorter end.
        float spare = L - Lc;
        float frontLen = spare * 0.6f, rearLen = spare - frontLen;
        float cz = L * 0.5f - frontLen - Lc * 0.5f;
        return new Geometry(L, W, H, Lc, Wc, Hc, floorTop, cz, frontLen, rearLen);
    }

    /// <summary>The shell's panels: which prefab, where its centre is, and its size.</summary>
    public static List<(string Prefab, Vector3 At, Vector3 Size)> Shell(VehicleProfile v, Geometry g)
    {
        var body = v.Body ?? VehicleBody.Saloon;
        var parts = new List<(string, Vector3, Vector3)>();
        // Whether the inside is soft. Seats, carpet and a headliner in a car; a bus or a van is
        // hard plastic and steel, which is exactly what CabinAbsorption already says.
        string lining = body.CabinAbsorption >= 0.25f ? Carpet : Steel;

        // Underneath, from just off the road to the floor. Not open space: a player must not walk
        // under a bus, and a bus's floor is over a metre up.
        if (g.FloorTop - Skin - ChassisBottom > 0.02f)
            parts.Add((Steel, new Vector3(0f, (ChassisBottom + g.FloorTop - Skin) * 0.5f, g.Cz),
                       new Vector3(g.Wc, g.FloorTop - Skin - ChassisBottom, g.Lc)));
        parts.Add((lining, new Vector3(0f, g.FloorTop - Skin * 0.5f, g.Cz), new Vector3(g.Wc, Skin, g.Lc)));
        parts.Add((lining, new Vector3(0f, g.RoofUnder + Skin * 0.5f, g.Cz), new Vector3(g.Wc + 2f * Skin, Skin, g.Lc)));

        // The sides: steel doors to the waist, glass to the roof.
        foreach (float side in new[] { -1f, 1f })
        {
            float x = side * (g.Wc * 0.5f + Skin * 0.5f);
            parts.Add((Steel, new Vector3(x, (g.FloorTop + g.Belt) * 0.5f, g.Cz), new Vector3(Skin, g.Belt - g.FloorTop, g.Lc)));
            parts.Add((Glass, new Vector3(x, (g.Belt + g.RoofUnder) * 0.5f, g.Cz), new Vector3(Skin, g.RoofUnder - g.Belt, g.Lc)));
        }

        // Windscreen and back window, full height: the bulkhead below the screen is the bonnet's job.
        parts.Add((Glass, new Vector3(0f, (g.FloorTop + g.RoofUnder) * 0.5f, g.Front + Skin * 0.5f), new Vector3(g.Wc + 2f * Skin, g.Hc, Skin)));
        parts.Add((Glass, new Vector3(0f, (g.FloorTop + g.RoofUnder) * 0.5f, g.Back - Skin * 0.5f), new Vector3(g.Wc + 2f * Skin, g.Hc, Skin)));

        // The bonnet and the boot, solid to the waist. They are the ends you walk into.
        if (g.FrontLen > 0.2f)
            parts.Add((Steel, new Vector3(0f, (ChassisBottom + g.Belt) * 0.5f, g.Front + Skin + (g.FrontLen - Skin) * 0.5f),
                       new Vector3(g.W, g.Belt - ChassisBottom, g.FrontLen - Skin)));
        if (g.RearLen > 0.2f)
            parts.Add((Steel, new Vector3(0f, (ChassisBottom + g.Belt) * 0.5f, g.Back - Skin - (g.RearLen - Skin) * 0.5f),
                       new Vector3(g.W, g.Belt - ChassisBottom, g.RearLen - Skin)));
        return parts;
    }
}
