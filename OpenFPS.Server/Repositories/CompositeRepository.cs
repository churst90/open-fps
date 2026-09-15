using System.Numerics;
using System.Text.Json;
using OpenFPS.Common.Components;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// One part of a composite, in the composite's OWN frame.
///
/// Deliberately the same shape as <see cref="EntityData"/>, which is what a map entry already is.
/// That is not laziness: it means a composite can contain anything a map can contain — a wall with
/// authored room materials, a portal with an aperture, a sound emitter with a slot offset — and none
/// of it needs a second code path to be placed. A house saved from a map and a house placed into one
/// are the same list of entries read in two directions.
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

/// <summary>
/// One seat, in the composite's own frame. The saved form of <see cref="Seat"/>.
///
/// Seats are part of what a thing IS, so they travel in the template rather than being re-authored
/// on every instance: place a bus twice and both have the same seats, the same as both have the same
/// walls.
/// </summary>
public class SeatDefinition
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Where the occupant's feet go, relative to the composite's origin.</summary>
    public Vector3 Position { get; set; }
    /// <summary>Which way the seat faces within the composite, DEGREES — this is a file a person may
    /// end up reading, and radians in a file are a small cruelty.</summary>
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

    /// <summary>
    /// The vehicle profile this drives as, or empty for something that does not drive.
    ///
    /// A key into <see cref="OpenFPS.Common.VehicleProfile.Presets"/> — the same profiles the map's own
    /// traffic uses, so a composite somebody built out of walls and a hatchback the map spawned are
    /// the same kind of thing to the engine, the tyres and the client that has to make them audible.
    /// </summary>
    public string VehiclePreset { get; set; } = string.Empty;
}

/// <summary>
/// Where a composite was PUT. A map carries a list of these the way it carries a list of vehicles:
/// what to place, where, and which way round.
///
/// This is what makes a house permanent. Without it, building one is a thing that happens until the
/// server is restarted, which is not a house, it is a rehearsal.
/// </summary>
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

/// <summary>
/// The composites available to place, loaded from JSON on disk and saved back the same way.
///
/// Mirrors <see cref="PrefabRepository"/> on purpose — same directory-of-files shape, same reader
/// options, same "rejected" reporting — because a composite IS a prefab, just one made of more than
/// one entity. Anyone who can author the one can author the other.
/// </summary>
public class CompositeRepository
{
    private readonly string _directory;
    private readonly Dictionary<string, CompositeTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rejected = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, CompositeTemplate> All => _templates;
    public IReadOnlyDictionary<string, string> Rejected => _rejected;

    public CompositeRepository(string directory)
    {
        // Same path discovery as the other repositories: run from the repo root or from the server's
        // own directory and either finds its data.
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
                    && !OpenFPS.Common.VehicleProfile.Presets.ContainsKey(t.VehiclePreset))
                { _rejected[t.Id] = $"unknown vehicle preset '{t.VehiclePreset}'"; continue; }
                // A vehicle nobody can drive is a shed with an engine in it. Caught here as well as
                // at /drivable, because a file on disk can be edited by hand and this is the door.
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

    /// <summary>Writes a composite to disk and makes it immediately placeable. This is the moment a
    /// thing somebody built out of parts becomes a thing anybody can place again.</summary>
    public void Save(CompositeTemplate template)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"{template.Id}.json");
        var options = new JsonSerializerOptions(MapRepository.JsonOptions) { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(template, options));
        _templates[template.Id] = template;
        Log.Information("CompositeRepository: saved '{Id}' ({Parts} part(s)) to {Path}.", template.Id, template.Parts.Count, path);
    }
}
