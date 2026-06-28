using System.Numerics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Client.Core;
using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Client.AudioEngine.Data;

namespace OpenFPS.Client.Services;

/// <summary>
/// Responsibility: Maps high-level gameplay events (Steps, Impacts, UI) 
/// to specific physical WAV files and categories in the Audio Bank.
/// </summary>
public class SoundMappingService
{
    private readonly AudioEngineFacade _audioEngine;
    private readonly LocalPlayerState _state;
    private readonly AudioBank _bank = new();
    private bool _initialized = false;

    public SoundMappingService(AudioEngineFacade engine, LocalPlayerState state)
    {
        _audioEngine = engine;
        _state = state;
    }

    /// <summary>
    /// Backward-compat shim. The Windows head still passes its <c>TolkService</c> as the middle
    /// argument; this service never used it (the handle was always dead), so the overload simply
    /// ignores it. Lets the Windows client keep compiling unchanged while the GTK head — and any
    /// future caller — uses the two-argument form. Remove once the Windows head migrates.
    /// </summary>
    public SoundMappingService(AudioEngineFacade engine, object? legacySpeech, LocalPlayerState state)
        : this(engine, state) { }

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

    /// <summary>
    /// Triggers a physical sound event (Footstep or Landing).
    /// </summary>
    public void PlayPhysicalInteraction(int entityId, string action, string material, Vector3 position, string style = "")
    {
        if (!_initialized) Initialize();

        string actionDir = action.ToUpper() switch {
            "FOOTSTEP" => "FOOTSTEPS",
            "LAND" => "LANDING",
            _ => action.ToUpper()
        };
        
        string soundPath = GetImpactSoundId(material, actionDir == "LANDING" ? 10f : 0f);
        if (string.IsNullOrEmpty(soundPath)) return;

        float vol = (actionDir == "FOOTSTEPS") ? 1.2f : 1.8f;
        
        // Submit the spatial emitter with default explicit audible EQ
        _audioEngine.Submit(new SpatialEmitter {
            EntityId = entityId,
            SoundId = soundPath,
            Position = position,
            ApparentPosition = position, 
            EffectiveDistance = Vector3.Distance(_state.Position, position), 
            Occlusion = 0.0f, 
            Volume = vol,
            Range = 50.0f, 
            Pitch = 1.0f,
            Type = EmitterType.WorldLocked,
            EnableReverb = true,
            IsEvent = true, 
            IsReflection = false,
            Priority = 2,
            EqLow = 1.0f,
            EqMid = 1.0f,
            EqHigh = 1.0f
        });
    }

    public string ResolvePath(string id)
    {
        if (!_initialized) Initialize();
        if (_bank.HasCategory(id)) return _bank.GetRandomSoundPath(id) ?? id;
        return id;
    }

    public void PlayUiSound(string soundId)
    {
        _audioEngine.Submit(new SpatialEmitter {
            EntityId = -1,
            SoundId = soundId,
            Type = EmitterType.UI,
            Volume = 1.0f,
            Pitch = 1.0f,
            Occlusion = 0.0f,
            IsReflection = false,
            EnableReverb = false,
            EqLow = 1.0f,
            EqMid = 1.0f,
            EqHigh = 1.0f
        });
    }

    private static readonly Random _rand = new Random();

    /// <summary>
    /// Plays a geometric reflection bounce sound.
    /// This is used for echolocation off nearby surfaces.
    /// </summary>
    public void PlayReflection(string material, Vector3 hitPos, float distance, float absorption, float delayMs = 0, int regionId = -1)
    {
        if (!_initialized) Initialize();

        // Reflections are quieter and muffled by distance/absorption
        float vol = Math.Max(0.0f, 1.0f - (distance / 12.0f)) * Math.Clamp(1.0f - absorption, 0.1f, 1.0f) * 0.35f;
        if (vol <= 0.01f) return;

        string soundPath = GetImpactSoundId(material, 0f);
        if (string.IsNullOrEmpty(soundPath)) return;

        // Use a unique pool of negative IDs for reflections to prevent collisions
        int reflectId = -2000 - _rand.Next(1000);
        
        var matProps = AcousticRegistry.GetProperties(material);

        _audioEngine.PlayPhysicalSoundDirect(new SpatialEmitter {
            EntityId = reflectId,
            SoundId = soundPath,
            Position = hitPos,
            ApparentPosition = hitPos,
            EffectiveDistance = distance, 
            Volume = vol,
            Pitch = 1.0f,
            Occlusion = 0.0f, 
            IsReflection = true, 
            DelayMs = delayMs, 
            Range = 15.0f,
            Type = EmitterType.WorldLocked,
            EnableReverb = true, 
            TargetRegionId = regionId,
            Priority = 0,
            IsEvent = true,
            EqLow = 1.0f - matProps.AbsorptionLow,
            EqMid = 1.0f - matProps.AbsorptionMid,
            EqHigh = 1.0f - matProps.AbsorptionHigh
        });
    }
}
