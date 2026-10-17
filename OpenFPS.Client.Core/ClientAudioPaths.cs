using OpenFPS.Audio.Core;

namespace OpenFPS.Client.Core;

/// <summary>Where the client's audio reads and keeps its data (AudioDataPaths), worked out as it always was.</summary>
public static class ClientAudioPaths
{
    /// <summary>
    /// The sound bank and the shipped door renders beside the program, and the player's own door renders under
    /// their local application data (OpenFPS/rendercache/&lt;this build's door models&gt;). OPENFPS_RENDER_CACHE
    /// names another folder for those, or "off" keeps none.
    /// </summary>
    public static AudioDataPaths ForPlayer()
        => AudioDataPaths.Beside(AppContext.BaseDirectory) with { RenderCache = PlayersRenderCache() };

    private static string? PlayersRenderCache()
    {
        string? env = System.Environment.GetEnvironmentVariable("OPENFPS_RENDER_CACHE");
        if (env == "off") return null;
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) return null;
        return Path.Combine(root, "OpenFPS", "rendercache", DoorRenderCache.Name);
    }
}
