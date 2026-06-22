using System;

namespace OpenFPS.Common;

/// <summary>
/// Global tuning parameters for the Acoustic Engine.
/// Moving these here allows for unified balance between client and server.
/// </summary>
public static class AcousticConstants
{
    // --- Architecture & Hierarchy ---
    public const int GlobalRegionId = -1; // The Global Environment world ID
    public const float DefaultVoxelResolution = 0.5f;

    // --- Propagation Settings ---
    public const float PortalPathBias = 0.35f; // Easier to trigger portal paths over direct occlusion
    public const float AperturePenaltyMultiplier = 0.25f; // Less volume loss from small openings
    public const float DetourPenaltyMultiplier = 0.6f; // Around-corner sounds should carry better
    public const float DetourPenaltyCap = 0.4f;
    public const float AirAbsorptionReferenceDist = 200.0f;
    public const float AirAbsorptionMinDist = 15.0f;
    public const float AirAbsorptionMaxMuffle = 0.8f;
    
    // --- Occlusion Settings ---
    public const float OcclusionMaxHighMuffleDb = -40.0f;
    public const float OcclusionMaxMidMuffleDb = -30.0f;
    public const float OcclusionMaxLowMuffleDb = -20.0f;
    public const float TransmissionBleedFactor = 0.15f;
    public const float OcclusionCap = 0.95f;
    
    // --- Reverb & Reflections ---
    public const int MaxReflectionOrder = 3; 
    public const float ReflectionEnergyThreshold = 0.05f; 
    public const float ReflectionMergeDistance = 3.0f; // Merge reflections within 3m of each other
    public const float ReflectionMinSpread = 15.0f; // Minimum degrees of spread for a reflection
    public const float ReflectionMaxSpread = 120.0f; 
    public const float ActiveRegionRadius = 50.0f;
    public const float ReverbFadeSpeed = 0.15f;
    public const float DefaultReverbDecayMs = 500.0f;
    public const float MinReverbDecayMs = 100.0f;
    public const float MaxReverbDecayMs = 10000.0f;
    
    // --- Panning & Volumetric ---
    public const float SpreadGrowthFactor = 5.0f; // Degrees per meter
    public const float VolumetricSpreadMax = 120.0f; // Tighter spread for better directionality
    public const float Min3DDistance = 3.0f; // Sounds stay at 100% volume for 3 meters
    public const float Volumetric3DLevelMin = 0.6f; // More 3D presence even for indirect sound
    public const float ParameterSmoothingTimeConstant = 0.1f;
    
    // --- Shelter & Environment ---
    public const float ShelterRayDistance = 15.0f; // Check up to 15m for a roof
    public const float ShelterFadeSpeed = 4.0f; // Speed at which shelter effects fade in/out
}
