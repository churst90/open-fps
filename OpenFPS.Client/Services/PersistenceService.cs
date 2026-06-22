using System.Text.Json;

namespace OpenFPS.Client.Services;

public class GameSettings
{
    public float MusicVolume { get; set; } = 0.5f;
    public float SfxVolume { get; set; } = 1.0f;
    public float VoiceVolume { get; set; } = 1.0f;
    public string TtsLanguage { get; set; } = "en-US";
}

public class SavedServer
{
    public string Name { get; set; } = "My Server";
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 33288;
    public string SavedUsername { get; set; } = string.Empty;
}

/// <summary>
/// Responsibility: Manage persistent game settings and saved server lists.
/// </summary>
public class PersistenceService
{
    private readonly string _settingsPath = "settings.json";
    private readonly string _serversPath = "servers.json";

    public GameSettings Settings { get; private set; } = new();
    public List<SavedServer> SavedServers { get; private set; } = new();

    public void Load()
    {
        if (File.Exists(_settingsPath))
            Settings = JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(_settingsPath)) ?? new();
        
        if (File.Exists(_serversPath))
            SavedServers = JsonSerializer.Deserialize<List<SavedServer>>(File.ReadAllText(_serversPath)) ?? new();
    }

    public void Save()
    {
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(_serversPath, JsonSerializer.Serialize(SavedServers, new JsonSerializerOptions { WriteIndented = true }));
    }
}
