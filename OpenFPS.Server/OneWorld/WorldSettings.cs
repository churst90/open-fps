using System.Text.Json;
using Serilog;

namespace OpenFPS.Server.OneWorld;

/// <summary>
/// The world's settings, from world.json in the server's folder (beside openfps.db and motd.txt). Every
/// field may be left out, and so may the file:
/// <code>
/// { "StorePath": "world", "CapGigabytes": 20, "Generate": true, "MaxAtOnce": 2, "Prebuild": true }
/// </code>
/// StorePath is where the tiles are kept (relative to the server's folder, or a full path); CapGigabytes the
/// most the store holds before it drops the tiles visited least recently (WorldStore); Generate false serves
/// only tiles already made; MaxAtOnce how many tiles are made at once; Prebuild false leaves the tiles round
/// world_places.json's places to be made when first wanted instead of at start.
/// </summary>
public sealed class WorldSettings
{
    public string StorePath { get; set; } = "world";
    public double CapGigabytes { get; set; } = 20;
    public bool Generate { get; set; } = true;
    public int MaxAtOnce { get; set; } = 2;
    public bool Prebuild { get; set; } = true;

    public long CapBytes => (long)(Math.Max(0.01, CapGigabytes) * 1024 * 1024 * 1024);

    public static string FilePath => Path.GetFullPath("world.json");

    /// <summary>world.json, or the defaults when there is none or it does not read.</summary>
    public static WorldSettings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<WorldSettings>(File.ReadAllText(path),
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
                                                       AllowTrailingCommas = true }) ?? new WorldSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warning(ex, "World: {Path} could not be read; using the defaults.", path);
        }
        return new WorldSettings();
    }
}
