using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace OpenFPS.Client.AudioEngine.Core;

public class AudioBank
{
    private readonly Dictionary<string, List<string>> _sounds = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized = false;

    public void Initialize(string basePath)
    {
        if (_initialized) return;
        if (!Directory.Exists(basePath)) return;

        string fullBasePath = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = Directory.GetFiles(fullBasePath, "*.*", SearchOption.AllDirectories)
                             .Where(f => f.EndsWith(".wav") || f.EndsWith(".ogg") || f.EndsWith(".mp3"));

        foreach (var file in files)
        {
            string fullPath = Path.GetFullPath(file);
            string relPath = Path.GetRelativePath(fullBasePath, fullPath);
            string key = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";
            string engineId = relPath.Replace('\\', '/').Replace(Path.GetExtension(relPath), "");

            if (!_sounds.ContainsKey(key)) _sounds[key] = new List<string>();
            _sounds[key].Add(engineId);
            
            if (!_sounds.ContainsKey(engineId)) _sounds[engineId] = new List<string> { engineId };
        }
        _initialized = true;
    }

    public string GetRandomSoundPath(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        string normKey = key.Replace('\\', '/').Trim('/');
        if (_sounds.TryGetValue(normKey, out var list) && list.Count > 0)
            return list[Random.Shared.Next(list.Count)];
        return "";
    }

    public bool HasCategory(string key) 
    {
        if (string.IsNullOrEmpty(key)) return false;
        return _sounds.ContainsKey(key.Replace('\\', '/').Trim('/'));
    }

    public IEnumerable<string> GetAllSoundIds()
    {
        // Return all unique sound paths that point to actual files
        return _sounds.Values.SelectMany(x => x).Distinct();
    }
}
