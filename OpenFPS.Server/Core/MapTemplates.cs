using System.Numerics;
using System.Text.RegularExpressions;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Core;

/// <summary>
/// What a new map starts as (/map new). One template for now: flat dirt a hundred metres square,
/// open sky, nothing on it, the spawn in the middle. A player builds the rest.
/// </summary>
public static class MapTemplates
{
    /// <summary>A map id a player may choose: 2 to 24 lower-case letters, digits, '_' or '-', starting with
    /// a letter. It is also the file name, so nothing that could leave the folder.</summary>
    public static bool IsValidId(string id) => Regex.IsMatch(id, "^[a-z][a-z0-9_-]{1,23}$");

    public static MapData Flat(string id, string owner) => new()
    {
        Id = id,
        Description = $"{owner}'s map.",
        Size = new Vector3(100, 40, 100),
        MinBound = new Vector3(-50, 0, -50),
        MaxBound = new Vector3(50, 40, 50),
        SpawnPoint = new Transform { Position = new Vector3(0, 1, 0), Rotation = Quaternion.Identity },
        VoxelResolution = 0.5f,
        OcclusionFloor = 0.15f,
        OwnerId = owner,
        IsPublic = false,
        Entities = new List<EntityData>
        {
            // The ground: natural ground, dirt, ten metres scaled ten times. Concrete, grass and asphalt
            // are laid on it, as a world is built (Cody, 2026-10-06).
            new() { EntityId = 1, PrefabId = "dirt_floor", Name = "Ground", Position = Vector3.Zero, Scale = new Vector3(10, 1, 10) },
        },
    };
}
