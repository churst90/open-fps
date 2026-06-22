using System.Numerics;

namespace OpenFPS.Common;

public static class PhysicsConstants
{
    public static readonly Vector3 PlayerSize = new(0.6f, 1.8f, 0.6f);
    public const float PlayerRadius = 0.3f;
    public const float PlayerHeight = 1.8f;
    public const float WalkSpeed = 4.5f;
    public const float JumpPower = 5.0f;
    public const float Gravity = 15.0f;
    public const float StepHeight = 0.4f;
    public const float RotationSpeed = 1.5f; // SHARED: Radians per second at full stick/key

    // --- Simulation Bounds & Environment ---
    public const float MapMinimumY = -10.0f;
    public const float DefaultGroundCheckLimit = -900.0f;
    public const float InteractionRange = 5.0f;
    public const float EarshotRange = 200.0f;
    public const float CollisionSearchRadius = 5.0f;

    // --- Simulation Timing ---
    public const int TickRate = 20; // 20 ticks per second (Standard for networking)
    public const float FixedDeltaTime = 1.0f / TickRate; // 0.05s
}
