using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Sound;

/// <summary>
/// The kinds of part a machine can be assembled from. Strings, not a type hierarchy, so a map author
/// can name a model in data and nothing in C# is recompiled for it.
/// </summary>
public static class MachineModels
{
    /// <summary>The engine itself. <see cref="MachinePart.Profile"/> is an EngineProfile preset key.
    /// It has no offset of its own: an engine is heard through its outlets and its block.</summary>
    public const string Engine = "engine";

    /// <summary>Where the exhaust gas leaves. An emitter slot.</summary>
    public const string Exhaust = "exhaust";

    /// <summary>Where the engine breathes, and where the block radiates from. An emitter slot.</summary>
    public const string Intake = "intake";

    /// <summary>The tyres. <see cref="MachinePart.Profile"/> is a TyreProfile preset key.</summary>
    public const string Tyres = "tyres";

    /// <summary>The shell the engine is bolted into. Profile is a VehicleBody preset key.</summary>
    public const string Body = "body";

    /// <summary>The gearbox. <see cref="MachinePart.Series"/> is its ratios, first gear first.</summary>
    public const string Gearbox = "gearbox";

    /// <summary>Mass, drag, and where the axles are: how it moves, and so what the sounds do.</summary>
    public const string Chassis = "chassis";

    /// <summary>A bogie: a frame with wheelsets in it. Profile is a rail vehicle in the
    /// <see cref="ModelLibrary"/>, which carries the wheel and where the axles sit.</summary>
    public const string Bogie = "bogie";

    /// <summary>What it runs on. Profile is a track in the <see cref="ModelLibrary"/>: the one part
    /// that belongs to the world, here because what a wheel sounds like is half the rail's doing.</summary>
    public const string Track = "track";

    /// <summary>A prime mover that is not a road engine: a diesel-electric's alternator set, an
    /// electric drive, a steam front end. Profile names which.</summary>
    public const string Traction = "traction";

    /// <summary>An air horn. Profile is a horn in the <see cref="ModelLibrary"/>.</summary>
    public const string Horn = "horn";

    /// <summary>A steam whistle. Profile is a whistle in the <see cref="ModelLibrary"/>.</summary>
    public const string Whistle = "whistle";

    /// <summary>A struck bell — a locomotive bell, a tram gong, a crossing gong on its mast.
    /// Profile is a bell in the <see cref="ModelLibrary"/>.</summary>
    public const string Bell = "bell";

    /// <summary>A compressed-air system: reservoir, governor and the ports that let it out. Profile
    /// is an air system in the <see cref="ModelLibrary"/>.</summary>
    public const string Air = "air";
}

/// <summary>
/// One part of a machine: what kind of thing it is, which one, and where it sits. The same shape for
/// a tailpipe, a rotor and a fountain. <see cref="Settings"/> and <see cref="Series"/> carry the
/// numbers not worth a preset; an absent setting means "whatever the profile said", not zero.
/// </summary>
public sealed record MachinePart
{
    public required string Model { get; init; }

    /// <summary>A preset key within this model's registry, or empty to take the base machine's.</summary>
    public string Profile { get; init; } = "";

    /// <summary>What the part is made of, a name in the <see cref="AcousticRegistry"/>; empty for the
    /// profile's own.</summary>
    public string Material { get; init; } = "";

    /// <summary>Where this part sits relative to the machine's origin, in its own frame
    /// (x right, y up, z forward). The emission point, for a part that emits.</summary>
    public Vector3 At { get; init; }

    /// <summary>How big this source is, metres; zero is a point. The mixer can place an extended source
    /// (<see cref="Hearing.HearingSettings.Place(float, float)"/>).</summary>
    // TODO: nothing reads it yet; it should replace ClientAudioSystem's car-sized MathF.Max(reference, 3f), with audibility ranking.
    public float ExtentMetres { get; init; }

    /// <summary>What this part measures at one metre, dB SPL, where that is a property of the part
    /// rather than of the whole machine. Zero means "the machine's own level covers it".</summary>
    public float LevelDb { get; init; }

    /// <summary>Scalars this model needs. Case-insensitive; an absent key means "unchanged".</summary>
    public IReadOnlyDictionary<string, float> Settings { get; init; } = EmptySettings;

    /// <summary>A list of numbers: a gearbox's ratios, a body's panel spans.</summary>
    public IReadOnlyList<float> Series { get; init; } = Array.Empty<float>();

    internal static readonly IReadOnlyDictionary<string, float> EmptySettings =
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    public bool Has(string key) => Settings.ContainsKey(key);
    public float Get(string key, float fallback) => Settings.TryGetValue(key, out float v) ? v : fallback;
    public int Get(string key, int fallback) => Settings.TryGetValue(key, out float v) ? (int)MathF.Round(v) : fallback;
}

/// <summary>
/// A machine as a parts list: data a map author can write (a JSON file next to the maps), where
/// <see cref="VehicleProfile.Presets"/> can only be named.
/// </summary>
public sealed record MachineDefinition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>A machine this one starts from (the same car with a different exhaust), or empty to
    /// build from parts alone.</summary>
    public string Base { get; init; } = "";

    public IReadOnlyList<MachinePart> Parts { get; init; } = Array.Empty<MachinePart>();

    /// <summary>The first part of a model, or null. Machines are small; a scan is cheaper than a map.</summary>
    public MachinePart? Part(string model)
    {
        for (int i = 0; i < Parts.Count; i++)
            if (string.Equals(Parts[i].Model, model, StringComparison.OrdinalIgnoreCase)) return Parts[i];
        return null;
    }
}
