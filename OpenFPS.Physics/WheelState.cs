namespace OpenFPS.Common.Networking;

/// <summary>
/// One wheel on the wire, quantised. An unmanaged struct, copied as its bytes (nine, padded to ten).
/// </summary>
public struct WheelState
{
    /// <summary>Normal load, decanewtons (to 655 kN).</summary>
    public ushort LoadDaN;
    /// <summary>Angular speed, 1/50 rad/s (to 655 rad/s), forward positive.</summary>
    public short AngularSpeed;
    /// <summary>Slip ratio, -1..1 in 1/127ths.</summary>
    public sbyte SlipRatio;
    /// <summary>Slip angle, -0.5..0.5 rad in 1/254ths of a radian.</summary>
    public sbyte SlipAngle;
    /// <summary>The surface under it, an index into <see cref="OpenFPS.Common.RoadSurfaces"/>.</summary>
    public byte Surface;
    /// <summary>Its share of its grip in use, 0..2 with 1 the limit, in steps of 1/127.5 (<see cref="EncodeDemand"/>).</summary>
    public byte Demand;
    // APPEND ONLY BELOW THIS LINE: the struct is copied as its bytes.
    /// <summary>The water under it, mm from the bottom of the road's texture (RoadWater), on a square
    /// root scale: (Water / 40)^2 mm, a hundredth of a millimetre at 4, a millimetre at 40, 40 mm at 255.</summary>
    public byte Water;

    public float LoadNewtons => LoadDaN * 10f;
    public float WaterMm => (Water / 40f) * (Water / 40f);
    public static byte EncodeWater(float mm) => (byte)Math.Clamp((int)MathF.Round(40f * MathF.Sqrt(MathF.Max(0f, float.IsFinite(mm) ? mm : 0f))), 0, 255);
    public float AngularSpeedRadPerSec => AngularSpeed / 50f;
    public float SlipRatioValue => SlipRatio / 127f;
    public float SlipAngleRad => SlipAngle / 254f;
    public float DemandFraction => Demand / 127.5f;

    /// <summary>A share of grip in use, 0..2 with 1 the limit, as a byte: the vehicle's tyre demand is sent the same way.</summary>
    public static byte EncodeDemand(float fraction)
        => (byte)Math.Clamp((int)MathF.Round(fraction * 127.5f), 0, 255);

    public static WheelState Encode(float loadNewtons, float angularSpeed, float slipRatio, float slipAngle, byte surface, float demand,
                                    float waterMm = 0f)
        => new()
        {
            Water = EncodeWater(waterMm),
            LoadDaN = (ushort)Math.Clamp((int)MathF.Round(loadNewtons / 10f), 0, ushort.MaxValue),
            AngularSpeed = (short)Math.Clamp((int)MathF.Round(angularSpeed * 50f), short.MinValue, short.MaxValue),
            SlipRatio = (sbyte)Math.Clamp((int)MathF.Round(slipRatio * 127f), -127, 127),
            SlipAngle = (sbyte)Math.Clamp((int)MathF.Round(slipAngle * 254f), -127, 127),
            Surface = surface,
            Demand = EncodeDemand(demand),
        };
}
