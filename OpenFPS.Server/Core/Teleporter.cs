using System.Numerics;
using OpenFPS.Common;

namespace OpenFPS.Server.Core;

/// <summary>
/// The teleporter: a handheld device (prefabs/teleporter.json, a premium item) that /tp needs. As long
/// as you have one, it takes you to a place, a player or a point, as often as you like (Cody,
/// 2026-10-05: unlimited uses; obtaining one is the hard part).
///
/// What it does is three sound EVENTS and a move, in this order: it charges where you stand for
/// <see cref="ChargeSeconds"/>, then you go — heard leaving where you were and arriving where you land.
/// The server only says that each happened and where; the client renders them (the event names are
/// agreed with it, and are the SynthKey and label of each). Docs/PLAN_2026-10-05.md section 2 has the
/// physics they stand for: a capacitor charging, about 75 litres of air rushing in where a body was,
/// and the same volume pushed out where it appears.
///
/// Nothing cancels a charge: moving or being hurt during it does not stop the teleport.
/// </summary>
public static class Teleporter
{
    /// <summary>The item's prefab id.</summary>
    public const string PrefabId = "teleporter";

    public const string Charge = "teleporter:charge";
    public const string Leave = "teleporter:leave";
    public const string Arrive = "teleporter:arrive";

    /// <summary>How long it charges before it takes you, seconds. Tests set it to zero.</summary>
    public static float ChargeSeconds { get; set; } = 2f;

    /// <summary>
    /// One event's sound: at <paramref name="at"/> (a body's feet), at chest height, its level the
    /// starting guess for the client's model. The charge rides on the body that carries the device;
    /// the leaving and arriving are where the air moves, and stay there.
    /// </summary>
    public static TransientSound[] Sound(string evt, Vector3 at) => new[]
    {
        new TransientSound
        {
            Character = evt == Charge ? SoundCharacter.Ring : SoundCharacter.Knock,
            Position = at + new Vector3(0f, 1.1f, 0f),
            OnBody = evt == Charge,
            BodyOffset = evt == Charge ? new Vector3(0.25f, 1.1f, 0.2f) : Vector3.Zero,
            LevelDb = evt == Charge ? 70f : 95f,
            Hz = evt == Charge ? 4000f : evt == Leave ? 60f : 120f,
            DecaySeconds = evt == Charge ? ChargeSeconds : 0.3f,
            Noisiness = evt == Charge ? 0.2f : 0.9f,
            SynthKey = evt,
        },
    };
}
