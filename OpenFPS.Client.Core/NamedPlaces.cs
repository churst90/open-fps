using System;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// Parts of a room with names of their own: a flight of stairs, a landing (prefabs/named_place.json).
///
/// "The stairs themselves need a zone, then the landings need a zone" (Cody, 2026-10-04). A region
/// would not do: a region is a name and a room for sound at once, and a stairwell cut into a region
/// a flight would have been cut into rooms a flight long — its reverberation measured from a box
/// 1.6 m wide, and sound from the rest of the shaft reaching the stairs through walls that are not
/// there. A named place is only a name. Where you stand in one, it is what you are told you are in;
/// the room you hear is the region round it, as before.
///
/// Each is a fixed box that is not solid, so the client keeps them with the markers
/// (<see cref="WorldSnapshot.MarkerEntityIds"/>), which is a short list rather than the whole map.
/// </summary>
public static class NamedPlaces
{
    /// <summary>The prefab a named place is placed from. The server's scan and place lookup use the
    /// same id (CommandHandler).</summary>
    public const string PrefabId = "named_place";

    /// <summary>Whether an entity is a named place: a fixed box that is not solid and has a name.</summary>
    public static bool Is(EntityDefinition def)
        => string.Equals(def.Identity.PrefabId, PrefabId, StringComparison.Ordinal)
           && !def.Moves && !def.Collider.IsSolid && def.Collider.Size.X > 0f
           && !string.IsNullOrWhiteSpace(def.Identity.Name);

    /// <summary>The smallest named place holding a point, or null for none.</summary>
    public static int? At(WorldSnapshot world, Vector3 point)
    {
        int? best = null;
        float bestVolume = float.MaxValue;
        foreach (int id in world.MarkerEntityIds)
        {
            if (!world.Entities.TryGetValue(id, out var e) || !Is(e.Definition)) continue;
            var size = e.Definition.Collider.Size;
            if (!GeometryUtils.IsPointInOBB(point, e.Transform.Position, size, e.Transform.Rotation)) continue;
            float volume = size.X * size.Y * size.Z;
            if (volume < bestVolume) { bestVolume = volume; best = id; }
        }
        return best;
    }

    /// <summary>A named place's name, or null if the id is not one.</summary>
    public static string? NameOf(WorldSnapshot world, int id)
        => world.Entities.TryGetValue(id, out var e) && Is(e.Definition) ? e.Definition.Identity.Name.Trim() : null;
}
