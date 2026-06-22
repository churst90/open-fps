using System.Numerics;
using System.Collections.Generic;
using OpenFPS.Common.Components;
using System;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: Track the current player's stats and position for immediate TTS feedback.
/// Includes support for visual smoothing during server reconciliation.
/// </summary>
public class LocalPlayerState
{
    // Authoritative logical position (used for physics and networking)
    public Vector3 Position = Vector3.Zero;
    
    // Smoothing offset: Added to Position to get the "Visual" location.
    // This bleeds to zero over time to hide server snaps.
    public Vector3 VisualOffset = Vector3.Zero;

    /// <summary>
    /// Returns the smoothed position used for Audio Listener and UI rendering.
    /// </summary>
    public Vector3 VisualPosition => Position + VisualOffset;

    public Vector3 Velocity = Vector3.Zero;
    public Quaternion Rotation = Quaternion.Identity; 
    public float Yaw;
    public float Pitch;
    public bool IsGrounded { get; set; } = true;
    public int Health { get; set; } = 100;
    public int MaxHealth { get; set; } = 100;
    public string CurrentMaterial { get; set; } = "Generic";
    public string CurrentVariant { get; set; } = "0";
    public string CurrentRegion { get; set; } = "Unknown Area";
    public bool IsIndoor { get; set; }
    public Vector3 RoomSize { get; set; }
    public Vector3 RoomCenter { get; set; }
    public Quaternion RoomRotation { get; set; } = Quaternion.Identity;
    public float ReverbTimeScale { get; set; } = 1.0f;
    public int[] RoomMaterials { get; set; } = new int[6];
    public string CurrentMapId { get; set; } = "default";
    public string CurrentMapChecksum { get; set; } = "";
    public Vector3 MapMin { get; set; } = new Vector3(-50, 0, -50);
    public Vector3 MapMax { get; set; } = new Vector3(50, 10, 50);
    public Vector3 MapSize { get; set; } = new Vector3(100, 100, 100);
    public float MinimumY { get; set; } = -10.0f;
    public float ShelterFactor { get; set; } = 0.0f;
    public List<string> Inventory { get; set; } = new();

    public float Temperature { get; set; } = 20.0f;
    public float Humidity { get; set; } = 0.5f;
    public float AirPressure { get; set; } = 1013.25f;
    public Vector3 WindVelocity { get; set; } = Vector3.Zero;
    public float WindGustiness { get; set; } = 0.0f;
    public float PrecipitationIntensity { get; set; } = 0.0f;

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
        
        float pitchDeg = Pitch * (180.0f / MathF.PI);
        string pitchStr = pitchDeg switch
        {
            > 60.0f => "looking up",
            > 15.0f => "looking slightly up",
            < -60.0f => "looking down",
            < -15.0f => "looking slightly down",
            _ => "looking straight"
        };

        return $"{cardinal}, {pitchStr}";
    }
}
