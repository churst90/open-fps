using System.Numerics;

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

    /// <summary>Whether the room was measured from its flood (docs/GEOMETRY.md 2.7 item 4), not its box.</summary>
    public static bool IsMeasured(in Room room) => room.Volume > 0f && room.FaceAreas is { Length: 6 } && room.FaceAbsorption is { Length: 6 };

    /// <summary>The room's volume, m³: its flood's air where it flooded, else its box.</summary>
    public static float Volume(in Room room)
        => IsMeasured(room) ? room.Volume : room.RoomSize.X * room.RoomSize.Y * room.RoomSize.Z;

    /// <summary>The areas of the room's six faces: its flood's where it flooded, else its box's.</summary>
    public static void FaceAreas(in Room room, Span<float> areas)
    {
        if (IsMeasured(room)) { for (int f = 0; f < 6; f++) areas[f] = room.FaceAreas![f]; return; }
        FaceAreas(room.RoomSize, areas);
    }

    /// <summary>The room's largest size, m: its flood's bounds where it flooded (or its box, if larger), else its box.</summary>
    public static float Reach(in Room room)
    {
        var size = IsMeasured(room) ? Vector3.Max(room.Extent, room.RoomSize) : room.RoomSize;
        return MathF.Max(size.X, MathF.Max(size.Y, size.Z));
    }

    /// <summary>The room's Sabine absorption area, low, mid and high, m²: what lines its flood where it flooded,
    /// else each box face by its material. An open face absorbs all of its area: what reaches it leaves.</summary>
    public static Vector3 AbsorptionArea(in Room room)
    {
        Vector3 a = Vector3.Zero;
        if (IsMeasured(room))
        {
            for (int f = 0; f < 6; f++) { var w = room.FaceAbsorption![f]; a += new Vector3(w.X, w.Y, w.Z); }
            return a;
        }
        Span<float> areas = stackalloc float[6];
        FaceAreas(room.RoomSize, areas);
        for (int f = 0; f < 6; f++)
        {
            if (FaceIsOpen(room.Materials, f)) { a += new Vector3(areas[f]); continue; }
            var p = room.Materials != null && f < room.Materials.Length
                ? AcousticRegistry.GetPropertiesByResonanceIndex(room.Materials[f])
                : AcousticRegistry.GetProperties("Generic");
            a += areas[f] * new Vector3(p.AbsorptionLow, p.AbsorptionMid, p.AbsorptionHigh);
        }
        return a;
    }

    /// <summary>A face is open when at least this share of it is open sky: then it is no surface.</summary>
    private const float OpenShare = 0.5f;

    /// <summary>
    /// The room as its flood measured it: its volume, each face's area, absorption and main material (the one lining
    /// most of it), and its air's bounds. A face is "None" where its box's face was (an open side, a portal's end) or
    /// where half or more of it is open sky; another room's air absorbs nothing here, since the routes count what
    /// leaves by an opening at its own S·tau. The box's material stands on a face nothing lines and nothing opens.
    /// </summary>
    public static Room Measured(in Room room, OpenFPS.Geometry.RoomFlood.Lining lining)
    {
        var measured = room;
        var areas = new float[6];
        var absorption = new Vector4[6];
        var materials = new int[6];
        var generic = AcousticRegistry.GetProperties("Generic");
        for (int f = 0; f < 6; f++)
        {
            float lined = 0f, best = 0f;
            string main = "";
            Vector4 a = Vector4.Zero;
            foreach (var (material, area) in lining.Surfaces[f])
            {
                lined += area;
                var p = string.IsNullOrEmpty(material) ? generic : AcousticRegistry.GetProperties(material);
                a += area * new Vector4(p.AbsorptionLow, p.AbsorptionMid, p.AbsorptionHigh, p.Absorption);
                if (area > best && !string.IsNullOrEmpty(material)) { best = area; main = material; }
            }
            float open = lining.Open[f];
            areas[f] = lined + open + lining.Meets[f];
            absorption[f] = a + new Vector4(open);
            if (FaceIsOpen(room.Materials, f) || (open > 0f && open >= OpenShare * (open + lined))) materials[f] = OpenFaceMaterial;
            else if (main.Length > 0 && AcousticRegistry.TryGetResonanceIndex(main, out int index)) materials[f] = index;
            else if (lined > 0f) materials[f] = generic.ResonanceIndex;
            else materials[f] = room.Materials != null && f < room.Materials.Length ? room.Materials[f] : generic.ResonanceIndex;
        }
        measured.Volume = MathF.Max(lining.Volume, 1e-3f);
        measured.FaceAreas = areas;
        measured.FaceAbsorption = absorption;
        measured.Materials = materials;
        measured.Extent = lining.Max - lining.Min;
        return measured;
    }

    /// <summary>Whether this face is an opening: an explicit "None", as <see cref="Room"/> and
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
    public static int OpenFaceCount(in Room region)
    {
        int open = 0;
        for (int f = 0; f < 6; f++) if (FaceIsOpen(region.Materials, f)) open++;
        return open;
    }

    /// <summary>The fraction of this region's boundary that is a surface, by area: a 220 by 210 m infield's
    /// open sky is nearly half of it, a 30 by 6 m end wall a rounding error.</summary>
    public static float Enclosure(in Room region)
    {
        Span<float> areas = stackalloc float[6];
        FaceAreas(region, areas);
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
    public static bool IsEnclosure(in Room region)
        => region.RoomSize.X > 0f && OpenFaceCount(region) == 0;

    /// <summary>
    /// Sabine's reverberation time for the region, ms, or zero where there is no enclosure to estimate
    /// (the ray tracer's then). A closed boundary that absorbs nothing rings as long as the clamp allows.
    /// A flooded room's from its measure.
    /// </summary>
    public static float DecayMs(in Room region)
    {
        if (!IsEnclosure(region)) return 0f;
        if (region.ReverbTimeScale <= 0f) return 0f;

        float volume = MathF.Max(0.01f, Volume(region));
        float absorption = 0f;
        if (IsMeasured(region)) for (int f = 0; f < 6; f++) absorption += region.FaceAbsorption![f].W;
        else
        {
            Span<float> areas = stackalloc float[6];
            FaceAreas(region.RoomSize, areas);
            for (int f = 0; f < 6; f++) absorption += areas[f] * FaceAbsorption(region.Materials, f);
        }

        float decayMs = absorption > 0.01f
            ? Math.Clamp(0.161f * volume / absorption * 1000f,
                         AcousticConstants.MinReverbDecayMs, AcousticConstants.MaxReverbDecayMs)
            : AcousticConstants.MaxReverbDecayMs;

        return decayMs * region.ReverbTimeScale;
    }
}
