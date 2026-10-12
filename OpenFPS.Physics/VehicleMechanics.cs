namespace OpenFPS.Common;

/// <summary>
/// A vehicle's mechanics: the physics' half of a vehicle preset, under the same id as the sound's half
/// (VehicleProfile, which makes one: <c>VehicleProfile.Mechanics</c>). What holds it on the road and moves it:
/// its running gear with every axle placed, its tyres' mechanics and its mass. The wheel model
/// (<see cref="WheelDynamics"/>) is made from it.
/// </summary>
/// <param name="Running">The running gear, its axles placed (<see cref="ChassisSpec.PlacedOn"/>).</param>
/// <param name="Tyres">The tyres' mechanics.</param>
/// <param name="MassKg">The vehicle's mass.</param>
public sealed record VehicleMechanics(ChassisSpec Running, TyreMechanics Tyres, float MassKg);
