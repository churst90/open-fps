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
    /// The shock's positive phase at the source, milliseconds: where its pulse first crosses zero,
    /// which sets the note of the gas bubble (<c>WeaponSynth</c>). 0.20-0.32 ms here; carried 20-40 m
    /// and measured as the NIJ takes were, the renders read 0.26-0.32 ms against 0.2-0.5 in the
    /// recordings (docs/GUNFIRE.md). Calibre shows in the level far more than in this.
    /// </summary>
    public required float ReportPositivePhaseMs { get; init; }
    /// <summary>The turbulent gas behind the shock: its time constant, ms. The first 10 dB of a real
    /// shot goes in 0.75-1.5 ms.</summary>
    public required float ReportBurstDecayMs { get; init; }
    /// <summary>What trails after it, time constant ms: the fall from -20 to -30 dB, 2.5-7 ms.</summary>
    public required float ReportTrailDecayMs { get; init; }
    /// <summary>The turbulent gas's corner, Hz: one pole above it, because the eddies cannot move
    /// faster. The shock front has its own, much higher corner.</summary>
    public required float ReportCornerHz { get; init; }

    /// <summary>
    /// How damped the gas bubble at the muzzle is, as a damping ratio. 1 is critically damped, which
    /// is exactly a Friedlander pulse: a jump, a fall through zero and a negative phase 0.135 of the
    /// peak. The NIJ recordings at 20-40 m have a negative phase 0.4-1.0 of the positive peak, five
    /// times deeper: the gas overshoots ambient and comes back. Below 1 is that rebound.
    /// </summary>
    public float ReportDamping { get; init; } = 1f;

    /// <summary>The gas that leaves after the bubble has rung out, against the burst: a broad mixing
    /// noise from the grown plume, no longer shaped by the bubble. More for a rifle's bigger charge.</summary>
    public float ReportTrailLevel { get; init; } = 0.1f;

    /// <summary>A revolver's cylinder gap: how far ahead of the muzzle blast its own blast comes, ms.
    /// The gas reaches the gap when the bullet leaves the cylinder and the muzzle when it leaves the
    /// barrel, so this is the bullet's time in the barrel. Zero for anything without a gap.</summary>
    public float CylinderGapLeadMs { get; init; }
    /// <summary>The gap blast's peak against the muzzle blast's.</summary>
    public float CylinderGapLevel { get; init; }

    /// <summary>Whether this round outruns sound, and so cracks as it passes.</summary>
    public bool IsSupersonic(float speedOfSound) =>
        MuzzleVelocity > speedOfSound + Ballistics.SubsonicMarginMetresPerSecond;
}

/// <summary>
/// The weapons that exist, and the one place that decides so.
///
/// Five measured on their own guns in the NIJ recordings, and a shotgun estimated from the physics.
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
        // 2016-DN-BX-0183. Every report here was fitted with `--gun-fit grid` against its gun's clean
        // takes, 90/130/180 degrees at 20 and 40 m: the AK's octave bands within 1.9 dB rms. The
        // broadest bubble (damping 0.6) and the brightest peak, at 1-2 kHz, as recorded.
        ReportPositivePhaseMs = 0.24f, ReportBurstDecayMs = 0.40f, ReportTrailDecayMs = 3f, ReportCornerHz = 1500f,
        ReportDamping = 0.6f, ReportTrailLevel = 0.15f,
    };

    /// <summary>5.56x45. Faster and much sharper than the AKM: a tighter Mach cone, so the crack is a
    /// whip rather than a bark, and far less low end to get through a wall.</summary>
    public static readonly WeaponDefinition Ar15 = new()
    {
        Id = "ar15",
        DisplayName = "AR-15",
        Cartridge = "5.56x45mm",
        MuzzleVelocity = 940f,
        // 5.56x45, measured on the M16: bands within 1.4 dB rms. More trail than a pistol, which
        // is the bigger charge's gas, and what holds up its 125 Hz band.
        ReportPositivePhaseMs = 0.24f, ReportBurstDecayMs = 0.55f, ReportTrailDecayMs = 3f, ReportCornerHz = 2000f,
        ReportDamping = 0.3f, ReportTrailLevel = 0.2f,
    };

    /// <summary>9x19, and only just supersonic — Mach 1.09. The crack is there but it is small and
    /// broad, nothing like a rifle's, which is exactly how a listener tells the two apart.</summary>
    public static readonly WeaponDefinition Glock = new()
    {
        Id = "glock",
        DisplayName = "Glock 17",
        Cartridge = "9x19mm",
        MuzzleVelocity = 375f,
        // 9x19, measured on the Glock 9: bands within 1.6 dB rms, peak at 1 kHz. The lab's fit had a
        // 0.30 ms positive phase against 0.42 in the game; this renders at 0.31.
        ReportPositivePhaseMs = 0.26f, ReportBurstDecayMs = 0.55f, ReportTrailDecayMs = 4f, ReportCornerHz = 1500f,
        ReportDamping = 0.3f, ReportTrailLevel = 0.05f,
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
        // .45 ACP, measured on the Colt 1911: bands within 1.9 dB rms. Subsonic, so it is the one
        // handgun with no crack.
        ReportPositivePhaseMs = 0.28f, ReportBurstDecayMs = 0.40f, ReportTrailDecayMs = 3f, ReportCornerHz = 4000f,
        ReportDamping = 0.3f, ReportTrailLevel = 0.1f,
    };

    /// <summary>12 gauge buckshot from a pump gun. Nine pellets, marginally supersonic.</summary>
    public static readonly WeaponDefinition Shotgun = new()
    {
        Id = "shotgun",
        DisplayName = "pump shotgun",
        Cartridge = "12 gauge 00 buck",
        MuzzleVelocity = 400f,
        PelletsPerShot = 9,
        // 12 gauge: no recording. The largest bore and charge here, so the longest pulse, the most
        // trailing gas and the darkest report — an estimate from the physics, to be checked against
        // a recording.
        ReportPositivePhaseMs = 0.32f, ReportBurstDecayMs = 0.60f, ReportTrailDecayMs = 4f, ReportCornerHz = 1200f,
        ReportDamping = 0.4f, ReportTrailLevel = 0.25f,
    };

    /// <summary>.357 Magnum from a 6-inch revolver, measured on the NIJ Ruger .357. About 410 m/s
    /// from that barrel: only just supersonic, like the 9 mm. Against the Glock it is slower to die
    /// away (1.4-1.5 ms to -10 dB side-on, against 1.0) and stronger at 2-4 kHz, and it is the one
    /// gun here that blasts twice: once at the cylinder gap, when the bullet jumps from the cylinder
    /// into the barrel, and again at the muzzle. No slide and no ejected case: the empties stay in the
    /// cylinder until it is reloaded.</summary>
    public static readonly WeaponDefinition Revolver357 = new()
    {
        Id = "revolver357",
        DisplayName = ".357 revolver",
        Cartridge = ".357 Magnum",
        MuzzleVelocity = 410f,
        // Bands within 1.3 dB rms of the Ruger. The least damped bubble here (0.2), which is its
        // slower fall: 1.2 ms to -10 dB pooled against the Glock's 0.9 (recorded: 1.1 and 0.8).
        ReportPositivePhaseMs = 0.20f, ReportBurstDecayMs = 0.70f, ReportTrailDecayMs = 3f, ReportCornerHz = 3000f,
        ReportDamping = 0.2f, ReportTrailLevel = 0.1f,
        // The gap leads by the bullet's time in a 6-inch barrel, at a mean speed in the bore of about
        // four fifths of its 410 m/s: 0.152 / 330 = 0.46 ms. Its level, -10 dB, is the lab demo's.
        // No recording isolates it: the fit prefers it (the 2 kHz band lands within 0.1 dB), but the
        // recordings show no separate early zero crossing, and a render's measured positive phase is
        // the gap's (0.16 ms against 0.36 recorded).
        CylinderGapLeadMs = 0.46f, CylinderGapLevel = 0.32f,
    };

    static WeaponRegistry()
    {
        foreach (var w in new[] { Akm, Ar15, Glock, ServicePistol, Shotgun, Revolver357 })
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
