using System.Numerics;

namespace OpenFPS.Client.AudioEngine.Data;

/// <summary>What the path from a source to the listener does to it, as the acoustic worker answered.</summary>
public struct AcousticPathData
{
    public float Occlusion;
    /// <summary>Where the source was when this was computed: a pooled voice id reused elsewhere must not
    /// inherit the previous occupant's occlusion and apparent position (ClientAudioSystem).</summary>
    public Vector3 SourcePosition;
    /// <summary>Where the source was when this was traced. The request's position (<see cref="SourcePosition"/>)
    /// for a fresh answer; older for one held over a tick the pool had no room in.</summary>
    public Vector3 SimulatedFrom;
    /// <summary>Where the ear was when this was traced.</summary>
    public Vector3 ListenerPosition;
    /// <summary>What produced it.</summary>
    public PathProvenance Provenance;
    public Vector3 ApparentPosition;
    public float EffectiveDistance;
    public float MaterialAbsorption;
    public float RoomGain;
    public float ApertureFactor;
    public float TransmissionBleed; 
    /// <summary>What the air took over this path, dB (positive), per band — ISO 9613-1 at the band
    /// centres the diffraction model uses. See AudioPhysics.AirLossDb.</summary>
    public float AirLowDb, AirMidDb, AirHighDb;
    public int RegionId; 

    public float EqLow;
    public float EqMid;
    public float EqHigh;

    public bool IsReflection;
    /// <summary>The surface a reflection came off (EarlyReflections.SurfaceId): what keeps a wall on one
    /// voice (ClientAudioSystem.AssignReflectionSlots).</summary>
    public int ReflectionId;
    public float ReflectionDelayMs;
    /// <summary>How rough the surface is, 0..1: the copy's width (<see cref="Spread"/>), and the share of a
    /// recording's copy that is smeared (SpatialEmitter.EchoMirrorShare).</summary>
    public float Scattering;
    public float Spread; // degrees, 0-360: the sound's width

    public AcousticPathData(float occlusion, Vector3 apparentPos, float effectiveDist, float materialAbsorption = 0.0f, float aperture = 1.0f, float bleed = 0.1f, int regionId = -1, float eqL = 1.0f, float eqM = 1.0f, float eqH = 1.0f)
    {
        Occlusion = occlusion;
        ApparentPosition = apparentPos;
        EffectiveDistance = effectiveDist;
        MaterialAbsorption = materialAbsorption;
        ApertureFactor = aperture;
        TransmissionBleed = bleed;
        RegionId = regionId;
        
        EqLow = eqL;
        EqMid = eqM;
        EqHigh = eqH;

        IsReflection = false;
        ReflectionId = 0;
        ReflectionDelayMs = 0;
        Scattering = 0;
        Spread = 0;
    }

    /// <summary>
    /// Whether this answer is about a source at <paramref name="source"/> heard at <paramref name="ear"/>:
    /// traced within a metre of where the source is, three of where the ear is, plus how far each can have
    /// moved while the answer was on its way (<see cref="MaxAgeSeconds"/>). A pooled id's last occupant,
    /// a source asked about only now and then, or an ear that has jumped, is somewhere else's answer.
    /// </summary>
    public readonly bool Answers(Vector3 source, float sourceSpeed, Vector3 ear, float earSpeed)
    {
        float s = SourceSlackMetres + sourceSpeed * MaxAgeSeconds, e = EarSlackMetres + earSpeed * MaxAgeSeconds;
        return Vector3.DistanceSquared(SimulatedFrom, source) <= s * s && Vector3.DistanceSquared(ListenerPosition, ear) <= e * e;
    }

    /// <summary>About a stride: a request answered a frame late is still the voice's own, a step taken
    /// elsewhere in a room is not.</summary>
    public const float SourceSlackMetres = 1.0f;
    /// <summary>Half a second of running, where a teleport or a minute's walk is tens of metres.</summary>
    public const float EarSlackMetres = 3.0f;
    /// <summary>
    /// How old an answer may be: the ask cadence (every tenth frame past 50 m), the worker's tick and a
    /// second held over a full pool (AsyncAcousticWorker.HeldSimPathMs). On the city the worker's answers
    /// trail a car by 380 to 470 ms at the median and up to 1.5 s on a busy machine (--pop-hunt, 2026-10-11).
    /// </summary>
    public const float MaxAgeSeconds = 1.5f;
}

/// <summary>What produced an acoustic answer.</summary>
public enum PathProvenance : byte
{
    /// <summary>None recorded: made by hand, or before the worker stamped it.</summary>
    Unknown,
    /// <summary>The simulator, from inputs staged the same tick.</summary>
    Simulated,
    /// <summary>The simulator's last answer, moved with the source, for a tick the pool had no room.</summary>
    Held,
    /// <summary>The hand-rolled tracer (no simulator, or never answered by it).</summary>
    HandRolled,
}
