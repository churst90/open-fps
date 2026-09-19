using System.Collections.Generic;
using System.IO;
using System;
using OpenFPS.Client.Core;
using OpenFPS.Client.AudioEngine.Core;

namespace OpenFPS.Client.Services;

/// <summary>
/// Resolves a gameplay event (a footstep on a material, a named sound category) to a concrete file in
/// the audio bank. It answers "which file", never "play it" — the emitters are built by
/// <see cref="ClientAudioSystem"/>, which is the one place that knows the acoustics of the shot.
///
/// It once also submitted emitters of its own (<c>PlayPhysicalInteraction</c>, <c>PlayUiSound</c>,
/// <c>PlayReflection</c>); nothing had called them since the audio system took that job over, and they
/// duplicated its emitter construction with drifted values, so they are gone.
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

    public void Initialize()
    {
        if (_initialized) return;
        string basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS", "SOUNDS");
        _bank.Initialize(basePath);
        _initialized = true;
    }

    /// <summary>
    /// The folder a material's footsteps come from when it has none of its own. The key is a
    /// material in the <see cref="AcousticRegistry"/>; the value is a folder under FOOTSTEPS/.
    ///
    /// Deliberately a short, explicit list rather than a guess: a material that belongs here is one
    /// somebody looked at and said "a footfall on that is a footfall on this". Anything not named
    /// keeps its own name and takes the ordinary fallback chain below.
    /// </summary>
    private static readonly Dictionary<string, string> RecordedStandIn = new(StringComparer.OrdinalIgnoreCase)
    {
        // A road is a pavement is a path, to a shoe. What differs between them is what they do to
        // sound arriving from elsewhere, not what a heel does on them.
        ["Asphalt"] = "Pavement",
        // Brick paving underfoot is a hard fired surface with joints in it, which is what Stone is.
        ["Brick"] = "Stone",
        // Walking into a hedge is walking into leaves.
        ["Foliage"] = "Leaves",
        // A crowd is a floor with people standing on it; you are walking on whatever they are.
        ["Audience"] = "Concrete",
    };

    private static string NearestRecorded(string material)
        => RecordedStandIn.TryGetValue(material, out var folder) ? folder : material;

    /// <summary>
    /// Resolves a material-specific sound path for physical interactions.
    /// </summary>
    public string GetImpactSoundId(string material, float force)
    {
        if (!_initialized) Initialize();
        
        // Map high-level action to folder structure
        string action = (force > 5.0f) ? "LANDING" : "FOOTSTEPS"; 
        
        // Normalize material name
        string materialName = string.IsNullOrEmpty(material) || material == "None" || material == "Generic" ? "Generic" : material;
        
        // Ensure proper capitalization for folder naming convention (e.g. "marble" -> "Marble")
        if (char.IsLower(materialName[0])) 
            materialName = char.ToUpper(materialName[0]) + materialName.Substring(1);

        // ── A material with no recordings of its own borrows the nearest one that has them ──────
        //
        // The acoustic table and the sample bank are two different collections and they do not have
        // to agree. A material earns its place in the table by being a different SURFACE — asphalt
        // absorbs three or four times what concrete does, brick scatters four times as much as a
        // poured wall — and none of that obliges anybody to have gone out and recorded somebody
        // walking on it. Without this, the city's road resolved to Generic, so the first thing a
        // player heard walking off the spawn point was the fallback.
        //
        // What it is NOT is a way to make two materials sound alike: the reflections, the occlusion
        // and the reverb still come from the real material. This is only about which recording plays
        // when there is no recording of its own, and every one of these is a surface a person would
        // struggle to tell from its stand-in by footfall alone.
        materialName = NearestRecorded(materialName);

        // Weather-based material overrides (Only if not sheltered)
        if (_state.ShelterFactor < 0.5f)
        {
            if (_state.Temperature < 2.0f && _state.PrecipitationIntensity > 0.2f) materialName = "Snow";
            else if (_state.PrecipitationIntensity > 0.3f && _state.Temperature >= 2.0f)
            {
                if (string.Equals(materialName, "Concrete", StringComparison.OrdinalIgnoreCase)) materialName = "Wet_Concrete";
                else if (string.Equals(materialName, "Grass", StringComparison.OrdinalIgnoreCase)) materialName = "Mud";
            }
        }

        string relativePath = "";

        // 1. Try Material + Variant subfolder
        string variantFolder = materialName + (_state.CurrentVariant ?? "0");
        string key = $"{action}/{materialName}/{variantFolder}";
        relativePath = _bank.GetRandomSoundPath(key);
        
        if (string.IsNullOrEmpty(relativePath))
        {
            // 2. Try just the Material folder
            key = $"{action}/{materialName}";
            relativePath = _bank.GetRandomSoundPath(key);
        }

        if (string.IsNullOrEmpty(relativePath) && materialName != "Generic")
        {
            // 3. Fallback to Generic Variant
            key = $"{action}/Generic/Generic0";
            relativePath = _bank.GetRandomSoundPath(key);
            
            if (string.IsNullOrEmpty(relativePath))
            {
                // 4. Fallback to Generic folder
                key = $"{action}/Generic";
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
