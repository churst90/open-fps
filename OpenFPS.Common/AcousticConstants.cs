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
    //
    // MaxReflectionOrder and ReflectionMergeDistance used to live here and are gone with the generator
    // that needed them. A recursive ray solve produced a scatter of near-duplicate reflections and then
    // merged whatever landed within three metres to hide it; an image source produces exactly one
    // arrival per surface, so there is nothing to merge and no order to cap. What a surface returns and
    // how wide it reads are properties of the surface — see OpenFPS.Common.EarlyReflections.
    public const float ReflectionEnergyThreshold = 0.05f; 
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
    // Steam Audio's ray-traced RT60 does not have that problem for the TIME. It does for the LEVEL, and
    // two constants used to live here that tried to read one off the other — a decay below which
    // outdoors stayed dry, and a decay at which the bus reached full wet. They are gone, because the
    // premise under them is false: the estimator fits a curve to whatever energy its rays bring home
    // and cannot report that there was hardly any, so a roofless yard fits a LONGER tail than the same
    // walls with a roof on (1.00 s against 0.60 s, AudioLab --sim-reverbfield). No threshold can
    // separate places that sit on the same side of it. How loud the tail is comes from how enclosed
    // the place is, measured directly — see OpenFPS.Common.Enclosure.
    /// <summary>
    /// Loudest the reverb bus may get, dB — full wet, which is where a sealed hard room belongs.
    ///
    /// It used to be -16, from a time when this wash was the only thing representing a room and had to
    /// be kept out of the way of everything else. Both halves of that have changed: the surfaces answer
    /// individually now (EarlyReflections), so this is only the diffuse remainder behind them, and the
    /// LEVEL of that remainder is measured rather than chosen — it is the fraction of emitted energy
    /// that comes back, which for open ground is one percent and for a sealed hard box is nearly all of
    /// it. Holding the top of that scale 16 dB down put a hard-walled courtyard at -35 dB, which is
    /// audible in a meter and not in the ear.
    /// </summary>
    public const float OutdoorMaxWetDb = 0.0f;

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

    /// <summary>
    /// How enclosed a place may be and still have <see cref="OutdoorMaxDecayMs"/> applied to it.
    ///
    /// The cap above was written against a ray tracer whose rays "do not all find the sky", and that
    /// is no longer the estimator. Enclosure.Look treats a direction that hits nothing as a perfect
    /// absorber, so the sky is IN the measurement: a street on the city map reads 525 ms and a
    /// pavement 627, with no cap involved at all. Nothing genuinely outdoors comes near 1,100.
    ///
    /// What the cap had started doing instead was silencing the places that are supposed to ring. It
    /// is applied to any region with no Sabine estimate, and a roofed tunnel has none — so a tunnel
    /// measuring three seconds was served 1.1, and was reported as "tunnel sounds dry, but shouldn't
    /// it sound echoy like reverby wet?". A car park's upper deck measured 4.5 s, a tiled stairwell 2.
    ///
    /// So the cap now asks whether the place is actually open. Below this it is outdoors and the cap
    /// is the safety net it was meant to be; above it, the rays found walls and a roof, and what they
    /// measured is what a listener should get.
    /// </summary>
    public const float OutdoorEnclosureCeiling = 0.45f;

    /// <summary>
    /// The reverberation unit's own wet level, dB — a constant, because the room is carried by the
    /// two things that ARE the room.
    ///
    /// How much reverberant field a source raises is the send's business (the room equation, with the
    /// distance and the absorption in it). How long it rings is the decay's. What is left for the
    /// unit is the difference between FMOD's internal scaling and unity, which is a property of the
    /// DSP and not of the place — so it is one number and does not move.
    ///
    /// It used to be driven by a loop that metered the unit and held its gain at unity, and that loop
    /// was cancelling the rooms: a reverberation unit accumulates energy in proportion to its decay,
    /// so a long tail measures a higher output and was trimmed back down by exactly as much as it was
    /// live. Six decibels for a corridor against thirteen for a seven-second hall. See the note in
    /// FmodAudioProvider.ApplySimulatedReverb for the measurements.
    ///
    /// Minus six is where that loop settled for a mid-sized room, which is the one place it was
    /// giving the right answer.
    /// </summary>
    public const float ReverbUnitWetDb = -6.0f;

    // ── What the reverb unit is for ─────────────────────────────────────────────────────────────
    //
    // The unit's own synthetic early reflections are OFF. Early reflections are a fact about the
    // geometry — which wall, how far, what it is made of — and the image-source pass measures them
    // per source; a reverb unit's are a fixed pattern of copies stamped onto every transient a tenth
    // of a millisecond after it, whatever the room. Measured on a footstep in the wood room: with
    // them the step peaked 9 dB louder than dry and sat on the master limiter's ceiling on every step;
    // heard as "pop pop pop, like four or five copies of reflections piling up on every step, and the
    // footsteps are loud". The unit renders the diffuse tail only, starting after the mean free path
    // has been crossed a couple of times, which is when reflections become too dense to have a
    // direction (see FmodAudioProvider.ApplySimulatedReverb).
    public const float ReverbEarlyReflectionsPercent = 0.0f;
    public const float ReverbLateDelayMeanFreePaths = 2.0f;
    public const float ReverbLateDelayMaxMs = 100.0f;   // the unit's own ceiling for the parameter
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
