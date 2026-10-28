using System.Runtime.CompilerServices;

namespace OpenFPS.Physics;

/// <summary>
/// What a vehicle can do to slow itself (docs/PHYSICS_BOUNDARY.md 16.8): the car's capability, not how hard
/// a driver chooses to brake (traffic's and the trains' service brakes are the drivers', in Agents and the
/// maps) and not where a sound starts to listen. Written once; the server's car, the world's edge and the
/// driving aids read it.
/// </summary>
public static class Brakes
{
    /// <summary>The most <paramref name="tyres"/> can slow a car, or turn it, in any direction, m/s², under
    /// <paramref name="gravity"/>, times what the road's water leaves of the grip
    /// (<see cref="RoadGripLaw.GripFactor"/>, 1 on a dry road).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Capacity(TyreMechanics tyres, float gravity, float gripFactor = 1f)
        => MathF.Max(0.1f, tyres.PeakGripG) * gravity * gripFactor;
}
