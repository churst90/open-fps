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
    /// <summary>How much of a source is sent into its OWN room's reverb bus. The bus is a pure send
    /// (its dry path is muted) and its level is gated per-portal by the bus fader, so this is the one
    /// knob for "how wet is a room". Deliberately constant with distance — a send that grows with range
    /// makes the room follow the listener.</summary>
    public const float ReverbSendMix = 0.35f;

    /// <summary>The cross-send into the room the LISTENER is standing in, as a fraction of
    /// <see cref="ReverbSendMix"/>. Small on purpose: a sound in the next room should reverberate in
    /// THAT room and arrive through the doorway, not smear the listener's own room from all sides.</summary>
    public const float ReverbCrossSendScale = 0.25f;

    /// <summary>Per audio update (60 Hz), how far a region bus's HRTF stage moves toward being fully
    /// localized to its doorway or fully filling the room. ~0.2 s end to end; a hard switch clicks.</summary>
    public const float ReverbBlendSpeed = 0.08f;

    /// <summary>Per audio update, how far a region bus's apparent doorway direction moves toward the
    /// current nearest portal. Stops a change of nearest portal from snapping the reverb across the head.</summary>
    public const float ReverbDirectionSmoothing = 0.12f;

    /// <summary>Ceiling on the SUM of the near-field boundary reflections' gains. A corner, a narrow
    /// corridor or a stairwell can put a surface in every probed direction at once; each reflection is
    /// individually correct but six of them together would swamp the direct sound. Above this the whole
    /// set is trimmed proportionally, so the balance between the surfaces — which is the actual cue —
    /// is kept while the total stays sane.</summary>
    public const float MaxBoundaryReflectionSum = 1.2f;

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
