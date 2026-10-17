using OpenFPS.Materials;

namespace OpenFPS.Acoustics;

/// <summary>Tuning for the acoustics, shared so the client and the server agree.</summary>
public static class AcousticConstants
{
    /// <summary>The range and reference distance of a sound that declares neither, one pair for the server's
    /// component and the client's emitter (audit S24, Cody 2026-10-12): a declared level's audible range wins.</summary>
    public const float DefaultRange = 50f, DefaultMinDistance = 3f;

    // --- Architecture & Hierarchy ---
    public const int GlobalRegionId = -1; // The Global Environment world ID
    public const float DefaultVoxelResolution = 0.5f;
    /// <summary>The speed of sound in air at 20 °C, m/s: the reference, for a law's default and for what must
    /// not follow the weather (<see cref="Air.ReferenceSpeedOfSound"/>). The world's air now is its audio
    /// world's <c>Atmosphere.SpeedOfSound</c>.</summary>
    public const float SpeedOfSound = Air.ReferenceSpeedOfSound;
    /// <summary>A map's occlusion floor when its file gives none: one value for the map file, the manifest,
    /// the client and the generator (they had 0.2 and 0.05). Carried and not read (see
    /// <see cref="AcousticMap.OcclusionFloor"/>).</summary>
    public const float DefaultOcclusionFloor = 0.2f;

    // --- Occlusion Settings ---
    public const float OcclusionCap = 0.95f;

    // --- Reverb & Reflections ---
    //
    // An image source gives one arrival per surface, so there is no reflection order or merge distance;
    // what a surface returns is the surface's business (EarlyReflections).
    public const float ActiveRegionRadius = 50.0f;
    /// <summary>How much of a source is sent into its own room's reverb bus: the one knob for how wet a
    /// room is. Constant with distance: a send that grows with range makes the room follow the
    /// listener.</summary>
    public const float ReverbSendMix = 0.35f;

    /// <summary>The cross-send into the listener's room, as a fraction of <see cref="ReverbSendMix"/>.
    /// Small: a sound in the next room should ring there and arrive through the doorway.</summary>
    public const float ReverbCrossSendScale = 0.25f;

    /// <summary>Per audio update (60 Hz), how far a region bus's HRTF stage moves between localised at
    /// its doorway and filling the room. About 0.2 s end to end; a hard switch clicks.</summary>
    public const float ReverbBlendSpeed = 0.08f;

    /// <summary>Per audio update, how far a region bus's doorway direction moves toward the nearest
    /// portal, so a change of nearest portal does not snap the reverb across the head.</summary>
    public const float ReverbDirectionSmoothing = 0.12f;

    /// <summary>Ceiling on the sum of the near-field boundary reflections' gains. A corner or a stairwell
    /// puts a surface in every direction at once; above this the set is trimmed in proportion, keeping
    /// the balance between surfaces, which is the cue.</summary>
    public const float MaxBoundaryReflectionSum = 1.2f;

    public const float DefaultReverbDecayMs = 500.0f;
    public const float MinReverbDecayMs = 100.0f;
    public const float MaxReverbDecayMs = 10000.0f;

    /// <summary>FMOD's EARLYLATEMIX, the blend of late reverb to early reflections: 0 means early
    /// reflections only and no tail at all, in every room (AudioLab --tailcheck). Named after the
    /// parameter it writes because the trap is the name. The unit's own early reflections are not
    /// wanted: the image-source pass measures the real ones (docs/COMMON_NOTES.md, Reverberation).</summary>
    public const float ReverbLateToEarlyMixPercent = 100.0f;

    // --- Panning & Volumetric ---
    public const float SpreadGrowthFactor = 5.0f; // Degrees per meter
    public const float VolumetricSpreadMax = 120.0f; // Tighter spread for better directionality
    public const float ParameterSmoothingTimeConstant = 0.1f;

    // --- Shelter & Environment ---
    public const float ShelterRayDistance = 15.0f; // Check up to 15m for a roof
    public const float ShelterFadeSpeed = 4.0f; // Speed at which shelter effects fade in/out
}
