namespace OpenFPS.Server.OneWorld;

/// <summary>
/// Where the world is read from and kept, named by the host (docs/ENGINE_PACKAGES.md, section 7): the maps'
/// folder, the prefabs', the composites', the world's settings (world.json) and its places to arrive at
/// (world_places.json). Nothing in OpenFPS.World guesses a folder of its own; the server makes this value.
/// </summary>
public sealed record WorldRoots(string Maps, string Prefabs, string Composites, string Settings, string Places)
{
    /// <summary>The roots in one folder, laid out as the server's is.</summary>
    public static WorldRoots In(string folder) => new(
        Path.Combine(folder, "maps"), Path.Combine(folder, "prefabs"), Path.Combine(folder, "composites"),
        Path.Combine(folder, "world.json"), Path.Combine(folder, "world_places.json"));

    /// <summary>Where the world's tiles are kept: the settings' StorePath, from the folder the settings are in
    /// unless it is a full path.</summary>
    public string Store(WorldSettings settings)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Settings))!, settings.StorePath));
}
