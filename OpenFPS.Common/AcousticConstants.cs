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

    /// <summary>Level of the map's outdoor ambience bed in the open air.</summary>
    public const float OutdoorAmbienceLevel = 0.55f;

    /// <summary>How much of the outdoor bed a fully sheltered listener loses. Not all of it: a room
    /// with a door in it is still connected to outside, and a building that silences the world
    /// completely is a building that feels like a loading screen.</summary>
    public const float ShelteredAmbienceDuck = 0.75f;

    /// <summary>Level of a region's own ambience bed while the listener is inside it.</summary>
    public const float RegionAmbienceLevel = 0.5f;

    public const float DefaultReverbDecayMs = 500.0f;
    public const float MinReverbDecayMs = 100.0f;
    public const float MaxReverbDecayMs = 10000.0f;

    // ── Outdoor reverberation, from geometry ────────────────────────────────────────────────────
    //
    // "Outdoors is dry" is true in a field and false in a street. A concrete canyon between two rows
    // of tall buildings has a measurable reverberation time — that slapback off a facade a hundred
    // metres away is the single most useful thing a blind player can hear in a city, because it tells
    // them the street has sides and roughly where they are. What is NOT true is the Sabine estimate
    // for "the outdoors", which takes the whole map as one room, returns an enormous number, and
    // washes the entire world in undirected reverb; that estimate is why the outdoor bus is muted.
    //
    // Steam Audio's ray-traced RT60 does not have that problem. It is computed from the geometry that
    // is actually around the listener, so an open field returns nearly nothing and a street canyon
    // returns a real decay. These two numbers are the gate: below the first, outdoors stays silent
    // exactly as it does today; between them the bus opens in proportion to what the rays found.
    /// <summary>Simulated RT60 below which outdoors is treated as open air and stays dry.</summary>
    public const float OutdoorDryDecayMs = 260.0f;
    /// <summary>Simulated RT60 at which the outdoor reverb bus reaches full wet level.</summary>
    public const float OutdoorFullWetDecayMs = 1400.0f;
    /// <summary>
    /// Loudest the outdoor bus may get, dB.
    ///
    /// Much lower than it was, because its job changed. Before there were discrete reflections this
    /// wash was the ONLY thing representing the buildings, so it had to be loud enough to be noticed —
    /// and a loud undirected two-second decay on every gunshot is precisely "one big echoey room".
    /// Now the facades answer individually, with their own directions and delays, and this is only the
    /// diffuse tail behind them: the part that has bounced too many times to have a direction left.
    /// It should be felt rather than heard.
    /// </summary>
    public const float OutdoorMaxWetDb = -16.0f;

    /// <summary>
    /// Longest reverberation time the outdoors is allowed, milliseconds.
    ///
    /// The ray tracer measured 1.7 to 2.8 seconds for a concrete street canyon, and taken literally
    /// that is not wrong — concrete absorbs almost nothing and a canyon traps sound between two
    /// parallel faces. But a two-second decay is a cathedral, and applying one to an outdoor space
    /// makes every shot in the open sound like it was fired indoors. Real streets measure nearer a
    /// second, because the sky is an infinite absorber and the tracer's rays do not all find it.
    /// </summary>
    public const float OutdoorMaxDecayMs = 1100.0f;
    /// <summary>How fast the outdoor wet level moves toward its target, per audio update. Stepping it
    /// in one frame is a step change in the signal, which is a click — the same fault that the region
    /// bus's binaural bypass had when crossing a threshold.</summary>
    public const float OutdoorWetBlendSpeed = 0.06f;
    
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
