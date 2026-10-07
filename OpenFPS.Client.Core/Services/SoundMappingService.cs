using OpenFPS.Client.Core;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Services;

/// <summary>
/// Which file in the audio bank a footstep on a material, or a named sound category, plays. It never
/// plays anything: <see cref="ClientAudioSystem"/> builds the emitters.
/// </summary>
public class SoundMappingService
{
    private readonly LocalPlayerState _state;
    private readonly AudioBank _bank = new();
    private bool _initialized = false;

    public SoundMappingService(LocalPlayerState state)
    {
        _state = state;
    }

    /// <param name="basePath">The sound folder; the program's own ASSETS/SOUNDS when not given. A test
    /// names the repository's.</param>
    public void Initialize(string? basePath = null)
    {
        if (_initialized) return;
        _bank.Initialize(basePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS", "SOUNDS"));
        _initialized = true;
    }

    /// <summary>
    /// The folder under FOOTSTEPS/ a material in <see cref="OpenFPS.Common.AcousticRegistry"/> borrows
    /// when it has no recordings of its own. A short list somebody chose by ear, not a guess; anything
    /// else keeps its own name and the fallback chain.
    /// </summary>
    private static readonly Dictionary<string, string> RecordedStandIn = new(StringComparer.OrdinalIgnoreCase)
    {
        // A road is a pavement is a path, to a shoe. What differs between them is what they do to
        // sound arriving from elsewhere, not what a heel does on them.
        ["Asphalt"] = "Cement",
        // Brick paving underfoot is a hard fired surface with joints in it, which is what cement is.
        ["Brick"] = "Cement",
        // Walking into a hedge is walking into leaves.
        ["Foliage"] = "Leaves",
        // A crowd is a floor with people standing on it; you are walking on whatever they are.
        ["Audience"] = "Concrete",
        // Polished stone is a hard tile with less grout in it.
        ["Marble"] = "Tile",
        // Rain on a hard floor: no wet recording exists, so it is the dry one rather than silence.
        // This one is reached from the weather substitution below, not from a map material.
        ["Wet_Concrete"] = "Concrete",
        // The end of the chain, for every material with no bank and no stand-in (glass, plaster,
        // plastic). Unlisted, it fell to a folder that no longer exists: a silent footstep. Twelve
        // recorded banks serve twenty-two materials.
        ["Generic"] = "Concrete",
    };

    private static string NearestRecorded(string material)
        => RecordedStandIn.TryGetValue(material, out var folder) ? folder : material;

    /// <summary>A footstep's file (a landing's above 5 of force) on a material, the weather and the
    /// player's variant taken into account.</summary>
    public string GetImpactSoundId(string material, float force)
    {
        if (!_initialized) Initialize();
        
        string action = (force > 5.0f) ? "LANDING" : "FOOTSTEPS"; 
        
        string materialName = string.IsNullOrEmpty(material) || material == "None" || material == "Generic" ? "Generic" : material;
        
        // The folders are capitalised: "marble" is Marble.
        if (char.IsLower(materialName[0])) 
            materialName = char.ToUpper(materialName[0]) + materialName.Substring(1);

        // The acoustic table and the sample bank need not agree: a material is in the table for what it
        // does to sound (asphalt absorbs three or four times what concrete does), not because it was
        // recorded underfoot. Only the recording is borrowed; reflections, occlusion and reverb still
        // come from the real material.
        materialName = NearestRecorded(materialName);

        // Weather changes the surface only where it falls.
        if (_state.ShelterFactor < 0.5f)
        {
            if (_state.Temperature < 2.0f && _state.PrecipitationIntensity > 0.2f) materialName = "Snow";
            else if (_state.PrecipitationIntensity > 0.3f && _state.Temperature >= 2.0f)
            {
                if (string.Equals(materialName, "Concrete", StringComparison.OrdinalIgnoreCase)) materialName = "Wet_Concrete";
                else if (string.Equals(materialName, "Dirt", StringComparison.OrdinalIgnoreCase)) materialName = "Mud";
            }
            // The weather can name a surface with no recording (Wet_Concrete): same table. Grass is
            // already Dirt by now, which is why the rain test is on Dirt.
            materialName = NearestRecorded(materialName);
        }

        // The material's variant folder, then the material, then the end of the chain.
        string variantFolder = materialName + (_state.CurrentVariant ?? "0");
        string key = $"{action}/{materialName}/{variantFolder}";
        string relativePath = _bank.GetRandomSoundPath(key);
        
        if (string.IsNullOrEmpty(relativePath))
        {
            key = $"{action}/{materialName}";
            relativePath = _bank.GetRandomSoundPath(key);
        }

        // Through the same table: "Generic" itself has no files.
        string generic = NearestRecorded("Generic");
        if (string.IsNullOrEmpty(relativePath) && !string.Equals(materialName, generic, StringComparison.OrdinalIgnoreCase))
        {
            key = $"{action}/{generic}/{generic}0";
            relativePath = _bank.GetRandomSoundPath(key);

            if (string.IsNullOrEmpty(relativePath))
            {
                key = $"{action}/{generic}";
                relativePath = _bank.GetRandomSoundPath(key);
            }
        }
        
        return relativePath ?? "";
    }

    public string ResolvePath(string id)
    {
        if (!_initialized) Initialize();
        if (_bank.HasCategory(id)) return _bank.GetRandomSoundPath(id) ?? id;
        return id;
    }

}
