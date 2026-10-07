using System.Numerics;

namespace OpenFPS.Client.AudioEngine.Data;

/// <summary>What the path from a source to the listener does to it, as the acoustic worker answered.</summary>
public struct AcousticPathData
{
    public float Occlusion;
    /// <summary>Where the source was when this was computed: a pooled voice id reused elsewhere must not
    /// inherit the previous occupant's occlusion and apparent position (ClientAudioSystem).</summary>
    public Vector3 SourcePosition;
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
    // TODO: ReflectionId and Scattering are written (AsyncAcousticWorker, SpatialAcoustics) and read nowhere.
    public int ReflectionId;
    public float ReflectionDelayMs;
    public int ReflectionIndex;
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
        ReflectionIndex = 0;
        Scattering = 0;
        Spread = 0;
    }
}
