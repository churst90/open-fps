using System.Numerics;
using OpenFPS.Acoustics;
using OpenFPS.Geometry;

namespace OpenFPS.Client.Core;

/// <summary>
/// Parts of a room with names of their own: a flight of stairs, a landing (prefabs/named_place.json;
/// Cody, 2026-10-04: "the stairs themselves need a zone, then the landings"). Only a name, never a room
/// for sound: a stairwell cut into regions a flight long would be reverberated as 1.6 m boxes, with the
/// rest of the shaft heard through walls that are not there (docs/AUTHORING.md, "Named places"). Not
/// solid, so the host keeps them with the markers; the aid reads them off the scene view
/// (<see cref="IAcousticWorld.Markers"/>), as the library's aids read a view (stage 9).
/// </summary>
public static class NamedPlaces
{
    /// <summary>The prefab a named place is placed from. The server's scan and place lookup use the
    /// same id (CommandHandler).</summary>
    public const string PrefabId = "named_place";

    /// <summary>Whether a thing is a named place: placed from the prefab, fixed, not solid and named
    /// (the values as the host's marker test has them).</summary>
    public static bool Is(string? prefabId, bool moves, bool solid, Vector3 size, string? name)
        => string.Equals(prefabId, PrefabId, StringComparison.Ordinal)
           && !moves && !solid && size.X > 0f
           && !string.IsNullOrWhiteSpace(name);

    /// <summary>Whether a body is a named place.</summary>
    public static bool Is(in AcousticBody body)
        => Is(body.Prefab, body.Moves, body.Solid, body.Size, body.Name);

    /// <summary>The smallest named place holding a point, or null for none.</summary>
    public static int? At(IAcousticWorld world, Vector3 point)
    {
        var markers = new List<AcousticBody>();
        world.Markers(markers);
        int? best = null;
        float bestVolume = float.MaxValue;
        foreach (var body in markers)
        {
            if (!Is(body)) continue;
            var size = body.Size;
            if (!GeometryUtils.IsPointInOBB(point, body.Position, size, body.Rotation)) continue;
            float volume = size.X * size.Y * size.Z;
            if (volume < bestVolume || (volume == bestVolume && body.Id < best)) { bestVolume = volume; best = body.Id; }
        }
        return best;
    }

    /// <summary>A named place's name, or null if the id is not one.</summary>
    public static string? NameOf(IAcousticWorld world, int id)
        => world.TryGetBody(id, out var body) && Is(body) ? body.Name!.Trim() : null;
}
