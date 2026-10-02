using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// One weapon, as numbers: what the audio needs to know, and nothing that is specific to a renderer
/// or a head.
///
/// The velocities are real. That matters more here than in a sighted game, because the muzzle velocity
/// is not a damage stat — it is what decides whether the round cracks as it goes past a listener, how
/// tight that crack is, and how big the gap is between the crack and the report. A player who learns
/// what an AKM sounds like at two hundred metres has learned something true about a 7.62x39 round, and
/// if the numbers were invented that knowledge would not transfer between weapons. See
/// <see cref="Ballistics"/> for what is done with them.
/// </summary>
public sealed record WeaponDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>What the round is, spoken and logged. Real designations, because they are learnable.</summary>
    public required string Cartridge { get; init; }

    // ── Ballistics ──────────────────────────────────────────────────────────────────────────────
    /// <summary>m/s at the muzzle. Decides the crack, the Mach cone and the crack-to-report gap.</summary>
    public required float MuzzleVelocity { get; init; }
    /// <summary>How many projectiles leave the barrel per shot. One for everything but buckshot.</summary>
    public int PelletsPerShot { get; init; } = 1;

    // ── What the blast sounds like ──────────────────────────────────────────────────────────────
    //
    // On the weapon, not in a separate table keyed by name. There was a table, and within a day it had
    // produced both kinds of bug the arrangement invites: the Glock's definition said 375 m/s while the
    // profile it named said 340, so the engine scheduled a crack and rendered silence into it; and
    // five weapons sharing three profiles meant the Glock and the .45 came out of the synthesizer
    // BYTE-IDENTICAL. A 9 mm and a .45 are not the same sound, and in a game played by ear the whole
    // point of having five weapons is that they are five things to recognise.

    /// <summary>
    /// The report, as measured: the positive phase of the blast pulse, milliseconds. 0.35-0.56 ms for
    /// every rifle and pistol in the NIJ recordings at 20-40 m (docs/GUNFIRE.md); calibre shows in the
    /// level far more than in this.
    /// </summary>
    public required float ReportPositivePhaseMs { get; init; }
    /// <summary>The turbulent gas behind the shock: its time constant, ms. The first 10 dB of a real
    /// shot goes in 0.75-1.5 ms.</summary>
    public required float ReportBurstDecayMs { get; init; }
    /// <summary>What trails after it, time constant ms: the fall from -20 to -30 dB, 2.5-7 ms.</summary>
    public required float ReportTrailDecayMs { get; init; }
    /// <summary>Where the report's spectrum starts to fall, Hz: about 6 dB an octave above it, as the
    /// recordings fall 13 dB by 4 kHz and 20 by 8.</summary>
    public required float ReportCornerHz { get; init; }
    /// <summary>When the action is heard after the shot, seconds. Zero for a weapon that does not
    /// cycle itself.</summary>
    public float MechanicalDelaySeconds { get; init; } = 0.045f;
    public float MechanicalLevel { get; init; } = 0.22f;

    /// <summary>Whether this round outruns sound, and so cracks as it passes.</summary>
    public bool IsSupersonic(float speedOfSound) =>
        MuzzleVelocity > speedOfSound + Ballistics.SubsonicMarginMetresPerSecond;
}

/// <summary>
/// The weapons that exist, and the one place that decides so.
///
/// Five, because five is what was recorded.
/// </summary>
public static class WeaponRegistry
{
    private static readonly Dictionary<string, WeaponDefinition> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>7.62x39. Heavy, slow for a rifle, and the loudest thing on this list at low frequency —
    /// which is why it carries so much further through walls than the AR15 does.</summary>
    public static readonly WeaponDefinition Akm = new()
    {
        Id = "akm",
        DisplayName = "AKM",
        Cartridge = "7.62x39mm",
        MuzzleVelocity = 715f,
        // 7.62x39, measured on the WASR-10 (the same pattern), Cadre Forensics, NIJ grant
        // 2016-DN-BX-0183.
        ReportPositivePhaseMs = 0.44f, ReportBurstDecayMs = 0.50f, ReportTrailDecayMs = 1.9f, ReportCornerHz = 3000f,
    };

    /// <summary>5.56x45. Faster and much sharper than the AKM: a tighter Mach cone, so the crack is a
    /// whip rather than a bark, and far less low end to get through a wall.</summary>
    public static readonly WeaponDefinition Ar15 = new()
    {
        Id = "ar15",
        DisplayName = "AR-15",
        Cartridge = "5.56x45mm",
        MuzzleVelocity = 940f,
        // 5.56x45, measured on the M16.
        ReportPositivePhaseMs = 0.40f, ReportBurstDecayMs = 0.45f, ReportTrailDecayMs = 1.8f, ReportCornerHz = 3200f,
    };

    /// <summary>9x19, and only just supersonic — Mach 1.09. The crack is there but it is small and
    /// broad, nothing like a rifle's, which is exactly how a listener tells the two apart.</summary>
    public static readonly WeaponDefinition Glock = new()
    {
        Id = "glock",
        DisplayName = "Glock 17",
        Cartridge = "9x19mm",
        MuzzleVelocity = 375f,
        // 9x19, measured on the Glock 9: no brighter than a rifle at 20 m, and no shorter.
        ReportPositivePhaseMs = 0.42f, ReportBurstDecayMs = 0.40f, ReportTrailDecayMs = 1.5f, ReportCornerHz = 3000f,
    };

    /// <summary>.45 ACP from a hammer-fired service pistol: heavy, slow and SUBSONIC. It makes no crack
    /// at all — only the report — so its range cannot be read off the gap the way a rifle's can. That
    /// is a real property of the cartridge and it is worth having a weapon that demonstrates it.</summary>
    public static readonly WeaponDefinition ServicePistol = new()
    {
        Id = "pistol",
        DisplayName = "service pistol",
        Cartridge = ".45 ACP",
        MuzzleVelocity = 253f,
        // .45 ACP, measured on the Colt 1911. Subsonic, so it is the one handgun with no crack.
        ReportPositivePhaseMs = 0.38f, ReportBurstDecayMs = 0.45f, ReportTrailDecayMs = 1.6f, ReportCornerHz = 2800f,
    };

    /// <summary>12 gauge buckshot from a pump gun. Nine pellets, marginally supersonic.</summary>
    public static readonly WeaponDefinition Shotgun = new()
    {
        Id = "shotgun",
        DisplayName = "pump shotgun",
        Cartridge = "12 gauge 00 buck",
        MuzzleVelocity = 400f,
        PelletsPerShot = 9,
        // 12 gauge: no recording. The largest bore and charge here, so the longest pulse and the
        // darkest report — an estimate from the physics, to be checked against a recording.
        ReportPositivePhaseMs = 0.60f, ReportBurstDecayMs = 0.60f, ReportTrailDecayMs = 2.2f, ReportCornerHz = 2400f,
        MechanicalDelaySeconds = 0f,
        MechanicalLevel = 0f,
    };

    static WeaponRegistry()
    {
        foreach (var w in new[] { Akm, Ar15, Glock, ServicePistol, Shotgun })
            _byId[w.Id] = w;
    }

    public static IReadOnlyCollection<WeaponDefinition> All => _byId.Values;

    public static bool TryGet(string id, out WeaponDefinition weapon) =>
        _byId.TryGetValue(id ?? "", out weapon!);

    /// <summary>The weapon by id, or null. Callers that cannot proceed without one should say which id
    /// they were given — a weapon that silently becomes a different weapon is a bug you hear once, in
    /// the middle of a firefight, and cannot reproduce.</summary>
    public static WeaponDefinition? Get(string id) =>
        _byId.TryGetValue(id ?? "", out var w) ? w : null;
}
