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
