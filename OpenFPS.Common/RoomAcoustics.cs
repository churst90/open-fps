using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Common;

/// <summary>
/// What a region's boundary does to the sound inside it, and whether there is one. A region is a named
/// volume, not a room: a face of material "None" is no surface at all, sound leaves through it, and a
/// region with one gets no Sabine estimate (V/A assumes a closed diffuse field); its reverberation is the
/// ray tracer's. Keying open-air behaviour on the global region id made every named place indoors
/// (docs/COMMON_NOTES.md, A region is not a room).
/// </summary>
public static class RoomAcoustics
{
    /// <summary>The resonance index of the material "None" (absorption 0, transmission 1): nothing here.</summary>
    public const int OpenFaceMaterial = 0;

    /// <summary>The face order the whole codebase uses: floor, ceiling, north, south, east, west.</summary>
    public static readonly string[] FaceNames = { "floor", "ceiling", "north", "south", "east", "west" };

    /// <summary>The areas of a box's six faces, in that order.</summary>
    public static void FaceAreas(Vector3 size, Span<float> areas)
    {
        float floorCeil = size.X * size.Z, northSouth = size.X * size.Y, eastWest = size.Z * size.Y;
        areas[0] = floorCeil; areas[1] = floorCeil;
        areas[2] = northSouth; areas[3] = northSouth;
        areas[4] = eastWest; areas[5] = eastWest;
    }

    /// <summary>Whether this face is an opening: an explicit "None", as <see cref="RegionComponent"/> and
    /// the prefab loader give a region that declares no surfaces. A face the map never set is Generic.</summary>
    public static bool FaceIsOpen(int[]? materials, int face)
        => materials != null && face < materials.Length && materials[face] == OpenFaceMaterial;

    /// <summary>The absorption coefficient of one face, or Generic where the map never said.</summary>
    public static float FaceAbsorption(int[]? materials, int face)
    {
        if (materials == null || face >= materials.Length)
            return AcousticRegistry.GetProperties("Generic").Absorption;
        return AcousticRegistry.GetPropertiesByResonanceIndex(materials[face]).Absorption;
    }

    /// <summary>How many of the six faces are not there.</summary>
    public static int OpenFaceCount(in RegionComponent region)
    {
        int open = 0;
        for (int f = 0; f < 6; f++) if (FaceIsOpen(region.Materials, f)) open++;
        return open;
    }

    /// <summary>The fraction of this region's boundary that is a surface, by area: a 220 by 210 m infield's
    /// open sky is nearly half of it, a 30 by 6 m end wall a rounding error.</summary>
    public static float Enclosure(in RegionComponent region)
    {
        Span<float> areas = stackalloc float[6];
        FaceAreas(region.RoomSize, areas);
        float total = 0f, closed = 0f;
        for (int f = 0; f < 6; f++)
        {
            total += areas[f];
            if (!FaceIsOpen(region.Materials, f)) closed += areas[f];
        }
        return total <= 0f ? 0f : closed / total;
    }

    /// <summary>Whether this region is a closed room: one open face disqualifies it. A doorway is not an
    /// open face but a portal, a leak between two closed rooms.</summary>
    public static bool IsEnclosure(in RegionComponent region)
        => region.RoomSize.X > 0f && OpenFaceCount(region) == 0;

    /// <summary>
    /// Sabine's reverberation time for the region, ms, or zero where there is no enclosure to estimate
    /// (the ray tracer's then). A closed boundary that absorbs nothing rings as long as the clamp allows.
    /// </summary>
    public static float DecayMs(in RegionComponent region)
    {
        if (!IsEnclosure(region)) return 0f;
        if (region.ReverbTimeScale <= 0f) return 0f;

        Span<float> areas = stackalloc float[6];
        FaceAreas(region.RoomSize, areas);

        float volume = MathF.Max(0.01f, region.RoomSize.X * region.RoomSize.Y * region.RoomSize.Z);
        float absorption = 0f;
        for (int f = 0; f < 6; f++) absorption += areas[f] * FaceAbsorption(region.Materials, f);

        float decayMs = absorption > 0.01f
            ? Math.Clamp(0.161f * volume / absorption * 1000f,
                         AcousticConstants.MinReverbDecayMs, AcousticConstants.MaxReverbDecayMs)
            : AcousticConstants.MaxReverbDecayMs;

        return decayMs * region.ReverbTimeScale;
    }
}
