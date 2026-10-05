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
    public static float ChargeSeconds { get; set; } = TeleporterSounds.ChargeSeconds;

    /// <summary>
    /// One event's sound, built by the client's model (TeleporterSounds): the charge rides on the body
    /// that carries the device; the leaving and arriving are where the air moves, and stay there.
    /// </summary>
    public static TransientSound[] Sound(string evt, Vector3 at)
        => new[] { TeleporterSounds.Sound(evt[TeleporterSounds.Prefix.Length..], at) };
}
