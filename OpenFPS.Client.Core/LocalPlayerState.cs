using System.Numerics;

namespace OpenFPS.Client.Core;

/// <summary>The local player's position, look, stats and surroundings, as the client knows them now.</summary>
public class LocalPlayerState
{
    /// <summary>The predicted position, for physics and the network.</summary>
    public Vector3 Position = Vector3.Zero;

    /// <summary>Added to <see cref="Position"/> for where the listener is; bleeds to zero to hide a
    /// server correction.</summary>
    public Vector3 VisualOffset = Vector3.Zero;

    /// <summary>The smoothed position: the listener's.</summary>
    public Vector3 VisualPosition => Position + VisualOffset;

    public Vector3 Velocity = Vector3.Zero;
    public Quaternion Rotation = Quaternion.Identity; 
    public float Yaw;
    public float Pitch;
    public bool IsGrounded { get; set; } = true;

    /// <summary>
    /// The composite this player rides in, or -1 on foot. While set, the position is not predicted (it
    /// is a seat's, see PredictionReconciler.Riding) and there are no footsteps.
    /// </summary>
    public int RidingEntityId { get; set; } = -1;

    public bool IsRiding => RidingEntityId >= 0;
    /// <summary>The seat ridden in is the one that drives.</summary>
    public bool RidingControls { get; set; }

    /// <summary>
    /// How high the ears are above the body's position, metres. A seat's position is its floor, and a
    /// hatchback's roof is 1.15 m above it: at the standing 1.7 a driver's ears were over the roof, out
    /// of the cabin's room. Seated, they are about a metre up.
    /// </summary>
    public float EyeHeight => IsRiding ? 1.0f : 1.7f;
    public int Health { get; set; } = 100;
    /// <summary>The gun in your hands (a weapon id), or empty: what decides whether Enter fires and R
    /// reloads. From the server's StatsUpdate.</summary>
    public string HeldWeaponId { get; set; } = "";
    /// <summary>The scope on it (a ScopeRegistry id), or empty: what numpad star raises.</summary>
    public string HeldScopeId { get; set; } = "";
    /// <summary>The fastest you may move on foot, m/s, or 0 for none: set while you carry a body. From the
    /// server's StatsUpdate.</summary>
    public float SpeedLimit { get; set; }
    public string CurrentMaterial { get; set; } = "Generic";
    public string CurrentVariant { get; set; } = "0";
    public const string UnknownArea = "Unknown Area";
    public string CurrentRegion { get; set; } = UnknownArea;

    /// <summary>The acoustic region the listener is in, or negative for none. A crossing is a change of
    /// id, not of name: two rooms can share a name, and the outdoor name flips between "Outside" and
    /// "Under Shelter" on a continuous shelter value.</summary>
    public int CurrentRegionId { get; set; } = int.MinValue;

    /// <summary>The room the listener is in: <see cref="CurrentRegionId"/> without the named parts of
    /// rooms (a flight, a landing; see NamedPlaces). The stair cues count a new room, not a new name.</summary>
    public int CurrentRoomId { get; set; } = int.MinValue;
    public bool IsIndoor { get; set; }
    public Vector3 MapMin { get; set; } = new Vector3(-50, 0, -50);
    public Vector3 MapMax { get; set; } = new Vector3(50, 10, 50);
    public float ShelterFactor { get; set; } = 0.0f;

    public float Temperature { get; set; } = 20.0f;

    public float PrecipitationIntensity { get; set; } = 0.0f;

    /// <summary>The way you face, as one of eight points: "North West".</summary>
    public string GetCardinal()
    {
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), Rotation);
        float angle = MathF.Atan2(forward.X, forward.Z) * (180.0f / MathF.PI);
        if (angle < 0) angle += 360.0f;
        return angle switch
        {
            < 22.5f or >= 337.5f => "North",
            < 67.5f => "North East",
            < 112.5f => "East",
            < 157.5f => "South East",
            < 202.5f => "South",
            < 247.5f => "South West",
            < 292.5f => "West",
            _ => "North West"
        };
    }

    public string GetCompassDirection()
    {
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), Rotation);

        float angle = MathF.Atan2(forward.X, forward.Z) * (180.0f / MathF.PI);
        if (angle < 0) angle += 360.0f;

        string cardinal = angle switch
        {
            < 22.5f or >= 337.5f => "North",
            < 67.5f => "North East",
            < 112.5f => "East",
            < 157.5f => "South East",
            < 202.5f => "South",
            < 247.5f => "South West",
            < 292.5f => "West",
            _ => "North West"
        };
        
        // Increasing pitch looks down: CreateFromYawPitchRoll's pitch turns forward (+Z) toward -Y.
        // This readout had it backwards once ("pressing f says looking down" while looking up), and
        // the K and O keys fell into the same trap.
        float pitchDeg = Pitch * (180.0f / MathF.PI);
        string pitchStr = pitchDeg switch
        {
            > 60.0f => "looking down",
            > 15.0f => "looking slightly down",
            < -60.0f => "looking up",
            < -15.0f => "looking slightly up",
            _ => "looking straight"
        };

        return $"{cardinal}, {pitchStr}";
    }
}
