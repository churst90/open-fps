using System.Numerics;
using System.Text.Json;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// One part of a composite, in the composite's own frame. The same shape as <see cref="EntityData"/>
/// on purpose, so a composite holds anything a map can and places it by the same path.
/// </summary>
public class CompositePart
{
    public string PrefabId { get; set; } = string.Empty;
    /// <summary>Position relative to the composite's origin, in its own frame.</summary>
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;
    public string[]? RoomMaterials { get; set; }
    public bool? IsIndoor { get; set; }
    public float? ApertureSize { get; set; }
}

/// <summary>One seat, in the composite's own frame: the saved form of <see cref="Seat"/>. Seats travel
/// in the template, so every instance has the same ones.</summary>
public class SeatDefinition
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Where the occupant's feet go, relative to the composite's origin.</summary>
    public Vector3 Position { get; set; }
    /// <summary>Which way the seat faces within the composite, degrees: people read these files.</summary>
    public float YawDegrees { get; set; }
    /// <summary>Whether sitting here drives it.</summary>
    public bool Controls { get; set; }
}

/// <summary>A saved composite: what it is called, whether it is fixed down, and what it is made of.</summary>
public class CompositeTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    /// <summary>Whether an instance is fixed to the world. A house is; a caravan is not.</summary>
    public bool Anchored { get; set; } = true;
    public List<CompositePart> Parts { get; set; } = new();

    /// <summary>Where people can sit in it. Empty for a thing nobody gets inside, like a barricade.</summary>
    public List<SeatDefinition> Seats { get; set; } = new();

    /// <summary>The vehicle this drives as (a <see cref="OpenFPS.Common.MachineRegistry"/> id, the same
    /// ones the map's traffic uses), or empty for something that does not drive.</summary>
    public string VehiclePreset { get; set; } = string.Empty;
}

/// <summary>Where a composite was put on a map: what, where and which way round. What makes a built
/// house outlast a restart.</summary>
public class CompositePlacement
{
    public string TemplateId { get; set; } = string.Empty;
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    /// <summary>Overrides the template's own setting when present. A caravan parked and bolted down.</summary>
    public bool? Anchored { get; set; }
    /// <summary>Who owns it, if anyone. Empty is public property.</summary>
    public string Owner { get; set; } = string.Empty;
}

/// <summary>The composites there are to place, a folder of JSON files read and written the way
/// <see cref="PrefabRepository"/> reads prefabs.</summary>
public class CompositeRepository
{
    private readonly string _directory;
    private readonly Dictionary<string, CompositeTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rejected = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, CompositeTemplate> All => _templates;
    public IReadOnlyDictionary<string, string> Rejected => _rejected;

    public CompositeRepository(string directory)
    {
        // Found from the repo root or from the server's own folder.
        if (!Directory.Exists(directory) && Directory.Exists(Path.Combine("OpenFPS.Server", directory)))
            _directory = Path.GetFullPath(Path.Combine("OpenFPS.Server", directory));
        else
            _directory = Path.GetFullPath(directory);
        Load();
    }

    public bool TryGet(string id, out CompositeTemplate template) => _templates.TryGetValue(id, out template!);

    public void Load()
    {
        _templates.Clear();
        _rejected.Clear();
        if (!Directory.Exists(_directory))
        {
            Log.Information("CompositeRepository: no composites directory at {Dir}; nothing to place yet.", _directory);
            return;
        }

        foreach (string file in Directory.GetFiles(_directory, "*.json"))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("composite-schema", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var t = JsonSerializer.Deserialize<CompositeTemplate>(File.ReadAllText(file), MapRepository.JsonOptions);
                if (t == null || string.IsNullOrWhiteSpace(t.Id)) { _rejected[name] = "no Id"; continue; }
                if (t.Parts.Count == 0) { _rejected[t.Id] = "no Parts"; continue; }
                if (t.Parts.Exists(p => string.IsNullOrWhiteSpace(p.PrefabId)))
                { _rejected[t.Id] = "a part names no prefab"; continue; }
                if (!string.IsNullOrWhiteSpace(t.VehiclePreset)
                    && !OpenFPS.Common.MachineRegistry.Knows(t.VehiclePreset))
                { _rejected[t.Id] = $"unknown vehicle preset '{t.VehiclePreset}'"; continue; }
                // Checked here as well as at /drivable: a file can be edited by hand.
                if (!string.IsNullOrWhiteSpace(t.VehiclePreset) && !t.Seats.Exists(s => s.Controls))
                { _rejected[t.Id] = "it drives but has no seat that drives"; continue; }
                if (t.Seats.Exists(s => string.IsNullOrWhiteSpace(s.Name)))
                { _rejected[t.Id] = "a seat has no name"; continue; }
                _templates[t.Id] = t;
            }
            catch (Exception ex) { _rejected[name] = ex.Message; }
        }

        if (_rejected.Count > 0)
            Log.Error("CompositeRepository: {Loaded} composite(s) loaded, {Rejected} REJECTED ({Ids}).",
                      _templates.Count, _rejected.Count, string.Join(", ", _rejected.Keys));
        else
            Log.Information("CompositeRepository: {Loaded} composite(s) loaded.", _templates.Count);
    }

    /// <summary>Writes a composite to disk and makes it placeable at once.</summary>
    public void Save(CompositeTemplate template)
    {
        if (!OpenFPS.Server.Core.SafeText.IsFileName(template.Id))
            throw new ArgumentException($"'{template.Id}' is not a design name: letters, digits, _ and - only.");
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"{template.Id}.json");
        var options = new JsonSerializerOptions(MapRepository.JsonOptions) { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(template, options));
        _templates[template.Id] = template;
        Log.Information("CompositeRepository: saved '{Id}' ({Parts} part(s)) to {Path}.", template.Id, template.Parts.Count, path);
    }
}
