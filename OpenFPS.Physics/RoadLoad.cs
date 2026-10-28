using System.Runtime.CompilerServices;

namespace OpenFPS.Physics;

/// <summary>
/// What the air and the road take from a vehicle as it rolls, newtons against its motion
/// (docs/PHYSICS_BOUNDARY.md 16.8): drag ½ρ·CdA·v² and rolling Crr·m·g. Written once; the server's car, the
/// sound's car and its driver read it with their own air and gravity. Where a slow car stops being pushed back
/// is each integrator's own, sized to its step, so the law has no standstill cut.
/// </summary>
public static class RoadLoad
{
    /// <summary>Aerodynamic drag, N, of a body of drag area <paramref name="dragArea"/> (Cd·A, m²) at
    /// <paramref name="speed"/> m/s through air of <paramref name="airDensity"/> kg/m³.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Drag(float airDensity, float dragArea, float speed)
        => 0.5f * airDensity * dragArea * speed * speed;

    /// <summary>Rolling resistance, N, of <paramref name="massKg"/> on tyres of coefficient
    /// <paramref name="rollingResistance"/> (Crr) under <paramref name="gravity"/> m/s².</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Rolling(float rollingResistance, float massKg, float gravity)
        => rollingResistance * massKg * gravity;
}
