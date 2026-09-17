using System;
using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Common;

/// <summary>
/// What a region's boundary does to the sound inside it — and whether there is a boundary at all.
///
/// A REGION IS A NAMED VOLUME, NOT A ROOM. The distinction did not exist until a map needed both:
/// the speedway names Front straight, Turns one and two, Infield and Grandstand so that a blind
/// player standing on two kilometres of identical asphalt knows where they are, and the moment it
/// did, the whole map started sounding like the inside of a building. Every open-air behaviour in
/// the engine — the muted reverb bus, the ray-traced outdoor decay and its wet gate, the outdoor air
/// absorption, the absence of a small-room gain — was keyed on "the listener is in the GLOBAL region
/// id", which is to say on the map NOT HAVING NAMED the place. Name the place and you were indoors.
///
/// So the question each of those sites should have been asking is answered here instead, from the
/// boundary itself: a face whose material is "None" is not a soft surface, it is NO SURFACE. Nothing
/// is there. Sound that reaches it leaves and does not come back.
///
/// That one reading fixes the estimate as well. Sabine reads absorption zero as a PERFECT MIRROR, so
/// six open faces came out as a sealed box of infinite reverberation — and because the total then
/// fell under the "did anything absorb?" guard, the code quietly substituted a 500 ms default room.
/// An unbounded 277,000 m³ infield was handed a 500 ms room and then the ray tracer wrote a longer
/// decay over the top of it at full wet.
///
/// The estimate has a precondition and it is not a taste: Sabine's V/A describes a DIFFUSE FIELD in a
/// CLOSED enclosure, where the energy keeps coming back until the surfaces have eaten it. Open one
/// face and there is no such field — the energy leaves once and is gone. So an unclosed region gets
/// no statistical estimate at all; what reverberation it does have (a grandstand at your back, a
/// wall down one side, a street with facades on both sides) comes from the ray tracer, which can see
/// the geometry that is actually standing there and which has been computing exactly that all along.
/// </summary>
public static class RoomAcoustics
{
    /// <summary>The resonance index of the material called "None". Absorption 0, transmission 1 — the
    /// registry's way of saying there is nothing here, which is what an unset face resolves to.</summary>
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

    /// <summary>
    /// Is this face an opening?
    ///
    /// A null or short Materials array means the map never said, and "never said" has always meant
    /// Generic here — so it keeps meaning that. It is an explicit "None" that means open, which is
    /// what <see cref="OpenFPS.Common.Components.RegionComponent"/> and the prefab loader both
    /// produce for a region that declares no surfaces.
    /// </summary>
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

    /// <summary>
    /// The fraction of this region's boundary that is a surface, by AREA.
    ///
    /// Area-weighted because a face is not a vote: a 220 x 210 m infield with an open sky has lost
    /// nearly half its boundary to that one face and a 30 x 6 m end wall is worth a rounding error.
    /// Used where a thing is a matter of degree — how much the air absorption behaves like a room's.
    /// </summary>
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

    /// <summary>
    /// Is this region a closed boundary — a room rather than a named patch of ground?
    ///
    /// One open face is enough to disqualify it, because the diffuse field the whole statistical
    /// model rests on needs the energy to come BACK, and through an opening it does not. A doorway is
    /// not an open face: an opening small enough to be a doorway is a portal, and a portal is modelled
    /// as a leak between two closed rooms rather than as a missing wall.
    /// </summary>
    public static bool IsEnclosure(in RegionComponent region)
        => region.RoomSize.X > 0f && OpenFaceCount(region) == 0;

    /// <summary>
    /// Sabine's reverberation time for the region, in milliseconds, or ZERO where the model does not
    /// apply.
    ///
    /// Zero has one meaning and it is not "quiet by default": there is no enclosure here, so there is
    /// no reverberant field to estimate, and whatever the geometry outside does about it is the ray
    /// tracer's to say. The old zero-absorption fallback to a 500 ms default is gone — a closed
    /// boundary that absorbs nothing is a hall of mirrors and rings for as long as the clamp allows,
    /// which is the honest answer and also one somebody will hear and come and ask about.
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
