using OpenFPS.Server.OneWorld;

namespace OpenFPS.Tests;

/// <summary>
/// The world's roots (docs/ENGINE_PACKAGES.md, stage 4): the host names where the maps, prefabs, composites,
/// world.json and world_places.json are, and the tile store is world.json's StorePath from world.json's folder.
/// </summary>
public class WorldRootsTests
{
    [Fact]
    public void AFoldersRootsAreTheServersLayout()
    {
        string folder = Path.Combine(Path.GetTempPath(), "openfps-roots");
        var roots = WorldRoots.In(folder);
        Assert.Equal(Path.Combine(folder, "maps"), roots.Maps);
        Assert.Equal(Path.Combine(folder, "prefabs"), roots.Prefabs);
        Assert.Equal(Path.Combine(folder, "composites"), roots.Composites);
        Assert.Equal(Path.Combine(folder, "world.json"), roots.Settings);
        Assert.Equal(Path.Combine(folder, "world_places.json"), roots.Places);
    }

    /// <summary>A relative StorePath is from world.json's folder, not the working folder; a full one is itself.</summary>
    [Fact]
    public void TheStoreIsFromTheSettingsFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "openfps-roots", "server");
        var roots = WorldRoots.In(folder);
        Assert.Equal(Path.Combine(folder, "world"), roots.Store(new WorldSettings()));
        Assert.Equal(Path.GetFullPath(Path.Combine(folder, "..", "tiles")), roots.Store(new WorldSettings { StorePath = "../tiles" }));
        string full = Path.Combine(Path.GetTempPath(), "elsewhere", "world");
        Assert.Equal(full, roots.Store(new WorldSettings { StorePath = full }));
    }

    /// <summary>The server's own roots are the names it always read, relative to its working folder.</summary>
    [Fact]
    public void TheServersRootsAreItsWorkingFolders()
    {
        var roots = OpenFPS.Server.GameServer.ServerRoots();
        Assert.Equal("prefabs", roots.Prefabs);
        Assert.Equal("world.json", roots.Settings);
        Assert.Equal("world_places.json", roots.Places);
        Assert.EndsWith("maps", roots.Maps);
        Assert.EndsWith("composites", roots.Composites);
        Assert.Equal(Path.GetFullPath("world"), roots.Store(new WorldSettings()));
    }
}
