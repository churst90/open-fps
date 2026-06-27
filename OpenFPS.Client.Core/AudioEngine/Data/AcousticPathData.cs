using System.Numerics;

namespace OpenFPS.Client.AudioEngine.Data;

public struct AcousticPathData
{
    public float Occlusion;
    public Vector3 ApparentPosition;
    public float EffectiveDistance;
    public float MaterialAbsorption;
    public float RoomGain;
    public float ApertureFactor;
    public float TransmissionBleed; 
    public float AirAbsorption; 
    public int RegionId; 

    public float EqLow;
    public float EqMid;
    public float EqHigh;

    public bool IsReflection;
    public int ReflectionId;
    public float ReflectionDelayMs;
    public int ReflectionIndex;
    public float Scattering;
    public float Spread; // (0-360) Volumetric width of the sound

    public AcousticPathData(float occlusion, Vector3 apparentPos, float effectiveDist, float materialAbsorption = 0.0f, float aperture = 1.0f, float bleed = 0.1f, float airAbs = 0.0f, int regionId = -1, float eqL = 1.0f, float eqM = 1.0f, float eqH = 1.0f)
    {
        Occlusion = occlusion;
        ApparentPosition = apparentPos;
        EffectiveDistance = effectiveDist;
        MaterialAbsorption = materialAbsorption;
        ApertureFactor = aperture;
        TransmissionBleed = bleed;
        AirAbsorption = airAbs;
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
