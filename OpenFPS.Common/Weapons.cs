using System;
using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>How the trigger behaves when it is held down.</summary>
public enum FireMode
{
    /// <summary>The trigger does nothing. A selector click and then silence is information too.</summary>
    Safe,
    /// <summary>One round per pull.</summary>
    Semi,
    /// <summary>A fixed count per pull, then the trigger must be released.</summary>
    Burst,
    /// <summary>Rounds until the trigger is released or the magazine is empty.</summary>
    Auto,
}

/// <summary>Where the next round comes from, which is most of what a reload sounds like.</summary>
public enum FeedSystem
{
    /// <summary>A box magazine: out, in, and a bolt to release if it locked back. Two or three sounds.</summary>
    DetachableBox,
    /// <summary>A tube, loaded one shell at a time. The reload is as long as the player lets it be,
    /// and every shell is a separate sound — which is why a player can count what someone else has.</summary>
    TubeMagazine,
}

/// <summary>What happens between one shot and the next without the player doing anything.</summary>
public enum ActionType
{
    /// <summary>The gun cycles itself. The action sound follows the shot on its own.</summary>
    SelfLoading,
    /// <summary>The player works a pump between shots. Until they do, the trigger is dead.</summary>
    Pump,
}

/// <summary>
/// One weapon, as numbers. Everything the mechanics and the audio need to know, and nothing that is
/// specific to a renderer or a head.
///
/// The velocities are real. That matters more here than in a sighted game, because the muzzle velocity
/// is not a damage stat — it is what decides whether the round cracks as it goes past a listener, how
/// tight that crack is, and how big the gap is between the crack and the report. A player who learns
/// what an AKM sounds like at two hundred metres has learned something true about a 7.62x39 round, and
/// if the numbers were invented that knowledge would not transfer between weapons. See
/// <see cref="Ballistics"/> for what is done with them.
///
/// <see cref="SoundFolder"/> is the root under ASSETS/SOUNDS for this weapon's handling sounds, laid
/// out by action name as the ingest writes it (CHARGE, TRIGGER, MAG_IN_LOADED, PUMP, ...). Each is a
/// folder, not a file, so the bank picks a variant per use and a reload does not repeat verbatim.
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
    /// <summary>Full cone angle of the spread, degrees. Zero for a rifled bore.</summary>
    public float SpreadDegrees { get; init; } = 0f;
    /// <summary>Damage per projectile at the muzzle, before range falloff.</summary>
    public required int DamagePerPellet { get; init; }
    /// <summary>Metres past which damage has fallen to half. Buckshot falls off fast; a rifle does not.</summary>
    public required float DamageHalfDistance { get; init; }
    /// <summary>Metres beyond which the round is not modelled at all.</summary>
    public required float MaxRange { get; init; }

    // ── Mechanism ───────────────────────────────────────────────────────────────────────────────
    public required FeedSystem Feed { get; init; }
    public required ActionType Action { get; init; }
    /// <summary>Rounds the magazine or tube holds, not counting one in the chamber.</summary>
    public required int MagazineCapacity { get; init; }
    /// <summary>Cyclic rate. Caps <see cref="FireMode.Auto"/> and how fast Semi can be pulled.</summary>
    public required int RoundsPerMinute { get; init; }
    /// <summary>Which selector positions exist, in the order the selector passes through them.</summary>
    public required FireMode[] FireModes { get; init; }
    public int BurstCount { get; init; } = 3;
    /// <summary>Whether the bolt or slide locks back on the last round. It is the loudest possible
    /// signal that someone is empty, and a weapon that has it is a weapon you can count.</summary>
    public bool LocksBackWhenEmpty { get; init; } = true;
    /// <summary>Whether the hammer must be cocked for the first shot from a decocked state.</summary>
    public bool HammerFired { get; init; } = false;

    // ── Handling times, seconds ─────────────────────────────────────────────────────────────────
    public required float MagOutSeconds { get; init; }
    public required float MagInSeconds { get; init; }
    /// <summary>Working the charging handle or releasing a locked-back bolt.</summary>
    public required float ChargeSeconds { get; init; }
    /// <summary>One shell into a tube. Irrelevant to a box-fed weapon.</summary>
    public float ShellLoadSeconds { get; init; } = 0.55f;
    /// <summary>Working a pump. Irrelevant to a self-loader.</summary>
    public float PumpSeconds { get; init; } = 0.55f;
    public float SelectorSeconds { get; init; } = 0.25f;

    // ── Sound ───────────────────────────────────────────────────────────────────────────────────
    /// <summary>Root under ASSETS/SOUNDS holding this weapon's handling folders.</summary>
    public required string SoundFolder { get; init; }
    /// <summary>Folder of muzzle-blast transients. Several weapons share one until takes of their own
    /// turn up — named here so that the sharing is visible rather than buried in a copied file.</summary>
    public required string FiringFolder { get; init; }
    /// <summary>Which magazine variant folder to draw from, for a weapon recorded with more than one.
    /// Empty when the action folder holds the takes directly.</summary>
    public string MagazineVariant { get; init; } = "";
    // ── What the blast sounds like ──────────────────────────────────────────────────────────────
    //
    // On the weapon, not in a separate table keyed by name. There was a table, and within a day it had
    // produced both kinds of bug the arrangement invites: the Glock's definition said 375 m/s while the
    // profile it named said 340, so the engine scheduled a crack and rendered silence into it; and
    // five weapons sharing three profiles meant the Glock and the .45 came out of the synthesizer
    // BYTE-IDENTICAL. A 9 mm and a .45 are not the same sound, and in a game played by ear the whole
    // point of having five weapons is that they are five things to recognise.

    /// <summary>Centre of the body resonance, Hz. Most of what reads as calibre: a 12 gauge sits near
    /// 200 and a 9 mm near 470.</summary>
    public required float BlastLowHz { get; init; }
    /// <summary>How long the body rings, seconds.</summary>
    public required float BlastDecaySeconds { get; init; }
    /// <summary>The chest-punch fundamental, Hz. The part that survives distance and walls.</summary>
    public required float ThumpHz { get; init; }
    public required float ThumpDecaySeconds { get; init; }
    /// <summary>Low-pass on the blast, Hz. Higher is sharper and more cracking.</summary>
    public required float BrightnessHz { get; init; }
    /// <summary>When the action is heard after the shot, seconds. Zero for a weapon that does not
    /// cycle itself.</summary>
    public float MechanicalDelaySeconds { get; init; } = 0.045f;
    public float MechanicalLevel { get; init; } = 0.22f;

    /// <summary>
    /// How far the blast's brightness falls as it decays, as a fraction of <see cref="BrightnessHz"/>.
    ///
    /// The sweep from bright to dark is what makes a blast sound like a blast rather than a burst of
    /// noise, but how FAR it falls is per-cartridge: a 5.56 keeps its top end much longer than a 12
    /// gauge does. A fixed one-tenth for everything made the AR-15 dive to 850 Hz in eighteen
    /// milliseconds, which is a big dull gun, not a sharp small one.
    /// </summary>
    public float BrightnessFloorFraction { get; init; } = 0.10f;

    /// <summary>
    /// How much of the recorded transient to mix under this weapon's blast, 0 to 1.
    ///
    /// One where the take is genuinely of this weapon. LOW where it is borrowed: the drop has three
    /// "semiauto rifle" takes and no 5.56 at all, and their spectral centroids sit within a thousand
    /// hertz of each other — they are one gun recorded one way. Mixed in at full strength they pull
    /// the AR-15 towards exactly the sound the AKM already has, and the two stop being distinguishable
    /// no matter how far apart their synthesis parameters are. Better to let the synthesis carry a
    /// weapon we have no recording of than to dress it in another weapon's.
    /// </summary>
    public float RecordedBlend { get; init; } = 1.0f;

    /// <summary>Which take in <see cref="FiringFolder"/> to bake into the blast. Fixed rather than
    /// random so a weapon sounds the same every session and two weapons sharing a folder cannot end
    /// up on the same take.</summary>
    public int FiringTakeIndex { get; init; } = 0;

    /// <summary>
    /// How fast a person can actually pull this trigger, rounds per minute.
    ///
    /// <see cref="RoundsPerMinute"/> is the CYCLIC rate — what the mechanism can do — and using it for
    /// semi-automatic fire is wrong by a factor of three. An AR-15 cycles at 800 rpm, but nobody pulls
    /// a trigger thirteen times a second; four or five is a fast shooter. Firing semi at the cyclic
    /// rate put four shots inside three hundred milliseconds, which does not read as four shots at all
    /// — it reads as a single buzzing artefact.
    /// </summary>
    public int SemiAutoRoundsPerMinute { get; init; } = 280;

    /// <summary>Seconds between one shot and the next at the cyclic rate.</summary>
    public float ShotInterval => RoundsPerMinute > 0 ? 60f / RoundsPerMinute : 0.1f;

    /// <summary>Seconds between shots in a given mode: the mechanism's limit under automatic fire, the
    /// finger's under everything else.</summary>
    public float IntervalFor(FireMode mode)
    {
        if (mode == FireMode.Auto || mode == FireMode.Burst) return ShotInterval;
        int rpm = Math.Min(RoundsPerMinute, Math.Max(1, SemiAutoRoundsPerMinute));
        return 60f / rpm;
    }

    /// <summary>Whether this round outruns sound, and so cracks as it passes.</summary>
    public bool IsSupersonic(float speedOfSound) =>
        MuzzleVelocity > speedOfSound + Ballistics.SubsonicMarginMetresPerSecond;

    /// <summary>Folder for one handling action, honouring the magazine variant where there is one.</summary>
    public string SoundFor(string action)
    {
        string root = $"{SoundFolder}/{action}";
        return action.StartsWith("MAG_", StringComparison.Ordinal) && MagazineVariant.Length > 0
            ? $"{root}/{MagazineVariant}"
            : root;
    }
}

/// <summary>
/// The weapons that exist, and the one place that decides so.
///
/// Five, because five is what was recorded. A weapon with no handling sounds of its own would be a
/// weapon the player cannot hear themselves operating, which in this game is not a weapon at all.
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
        DamagePerPellet = 43,
        DamageHalfDistance = 300f,
        MaxRange = 800f,
        Feed = FeedSystem.DetachableBox,
        Action = ActionType.SelfLoading,
        MagazineCapacity = 30,
        RoundsPerMinute = 600,
        FireModes = new[] { FireMode.Safe, FireMode.Auto, FireMode.Semi },
        MagOutSeconds = 0.75f,
        MagInSeconds = 0.85f,
        ChargeSeconds = 0.55f,
        SoundFolder = "WEAPONS/AKM",
        // A WASR-10 — the same 7.62x39 pattern — recorded at 96 kHz, three metres behind the muzzle,
        // unclipped and unlimited. Cadre Forensics, NIJ grant 2016-DN-BX-0183.
        FiringFolder = "WEAPONS/_FIRING/AKM/3m",
        MagazineVariant = "polymer",
        FiringTakeIndex = 0,
        // 7.62x39: a big, low, barking blast. Less top end than the AR-15 and much more weight, which
        // is why it is the one you still hear clearly through a wall two rooms away.
        BlastLowHz = 280f,
        BlastDecaySeconds = 0.030f,
        ThumpHz = 68f,
        ThumpDecaySeconds = 0.17f,
        BrightnessHz = 6200f,
        BrightnessFloorFraction = 0.09f,
    };

    /// <summary>5.56x45. Faster and much sharper than the AKM: a tighter Mach cone, so the crack is a
    /// whip rather than a bark, and far less low end to get through a wall.</summary>
    public static readonly WeaponDefinition Ar15 = new()
    {
        Id = "ar15",
        DisplayName = "AR-15",
        Cartridge = "5.56x45mm",
        MuzzleVelocity = 940f,
        DamagePerPellet = 38,
        DamageHalfDistance = 400f,
        MaxRange = 900f,
        Feed = FeedSystem.DetachableBox,
        Action = ActionType.SelfLoading,
        MagazineCapacity = 30,
        RoundsPerMinute = 800,
        FireModes = new[] { FireMode.Safe, FireMode.Semi },
        MagOutSeconds = 0.7f,
        MagInSeconds = 0.8f,
        ChargeSeconds = 0.5f,
        SoundFolder = "WEAPONS/AR15",
        // An M16 — 5.56x45 at last, rather than a borrowed 7.62 take.
        FiringFolder = "WEAPONS/_FIRING/AR15/3m",
        // 5.56x45: sharper, brighter, less body. A crack rather than a bark.
        BlastLowHz = 430f,
        BlastDecaySeconds = 0.019f,
        ThumpHz = 95f,
        ThumpDecaySeconds = 0.09f,
        BrightnessHz = 11000f,
        // A 5.56 barely darkens at all — it stays a sharp crack the whole way down. At the default
        // tenth this dived to 850 Hz inside twenty milliseconds and came out sounding like a bigger,
        // duller weapon than the AKM, which is backwards.
        BrightnessFloorFraction = 0.30f,
        FiringTakeIndex = 0,
        // Full strength now. The 0.30 blend existed because the only rifle take in the drop was a
        // 7.62 and mixing it in at full level dragged the AR-15 towards the AKM's character. With a
        // real 5.56 recording there is nothing to hedge against.
        RecordedBlend = 1.0f,
    };

    /// <summary>9x19, and only just supersonic — Mach 1.09. The crack is there but it is small and
    /// broad, nothing like a rifle's, which is exactly how a listener tells the two apart.</summary>
    public static readonly WeaponDefinition Glock = new()
    {
        Id = "glock",
        DisplayName = "Glock 17",
        Cartridge = "9x19mm",
        MuzzleVelocity = 375f,
        DamagePerPellet = 26,
        DamageHalfDistance = 60f,
        MaxRange = 200f,
        Feed = FeedSystem.DetachableBox,
        Action = ActionType.SelfLoading,
        MagazineCapacity = 17,
        RoundsPerMinute = 450,       // trigger-limited; the mechanism would go far faster
        FireModes = new[] { FireMode.Semi },   // no selector at all, which is its own tell
        MagOutSeconds = 0.55f,
        MagInSeconds = 0.6f,
        ChargeSeconds = 0.4f,
        SoundFolder = "WEAPONS/Glock",
        FiringFolder = "WEAPONS/_FIRING/Glock/3m",
        // 9x19: small, high, snappy. The least body of anything here.
        BlastLowHz = 470f,
        BrightnessFloorFraction = 0.22f,
        BlastDecaySeconds = 0.018f,
        ThumpHz = 105f,
        ThumpDecaySeconds = 0.085f,
        BrightnessHz = 6000f,
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
        DamagePerPellet = 34,
        DamageHalfDistance = 45f,
        MaxRange = 150f,
        Feed = FeedSystem.DetachableBox,
        Action = ActionType.SelfLoading,
        MagazineCapacity = 8,
        RoundsPerMinute = 400,
        FireModes = new[] { FireMode.Semi },
        HammerFired = true,
        MagOutSeconds = 0.6f,
        MagInSeconds = 0.65f,
        ChargeSeconds = 0.45f,
        SoundFolder = "WEAPONS/pistol",
        // A Colt 1911 — hammer-fired .45 ACP, which is exactly what this weapon is.
        FiringFolder = "WEAPONS/_FIRING/pistol/3m",
        // .45 ACP: slow, heavy, dull. Noticeably deeper and darker than the 9 mm, and subsonic, so it
        // is the one handgun with no crack to go with it — two independent tells for the same weapon.
        BlastLowHz = 360f,
        BlastDecaySeconds = 0.023f,
        ThumpHz = 82f,
        ThumpDecaySeconds = 0.115f,
        BrightnessHz = 4200f,
    };

    /// <summary>12 gauge buckshot from a pump gun. Nine pellets, marginally supersonic, and a reload
    /// that is one shell at a time — so an attentive listener can hear exactly how many someone has
    /// put back in, and how far they got before deciding they had enough.</summary>
    public static readonly WeaponDefinition Shotgun = new()
    {
        Id = "shotgun",
        DisplayName = "pump shotgun",
        Cartridge = "12 gauge 00 buck",
        MuzzleVelocity = 400f,
        PelletsPerShot = 9,
        SpreadDegrees = 3.5f,
        DamagePerPellet = 13,
        DamageHalfDistance = 18f,
        MaxRange = 60f,
        Feed = FeedSystem.TubeMagazine,
        Action = ActionType.Pump,
        MagazineCapacity = 5,
        RoundsPerMinute = 75,
        SemiAutoRoundsPerMinute = 75,     // the pump is the limit, not the finger        // as fast as the pump allows
        FireModes = new[] { FireMode.Safe, FireMode.Semi },
        LocksBackWhenEmpty = false,  // a pump gun does not lock back; you find out by pulling
        MagOutSeconds = 0f,
        MagInSeconds = 0f,
        ChargeSeconds = 0.5f,
        ShellLoadSeconds = 0.85f,
        PumpSeconds = 0.6f,
        SoundFolder = "WEAPONS/Shotgun",
        // The Cadre dataset has no shotgun in it. The only thing on hand is twelve milliseconds of a
        // single-shot RIFLE take, and twelve milliseconds of the wrong weapon is worse than a
        // synthesized one of the right weapon — so this is the one gun still fired entirely from the
        // synthesizer, by choice. A real 12 gauge take is the open item.
        FiringFolder = "WEAPONS/_FIRING/rifle_single",
        RecordedBlend = 0f,
        // 12 gauge: the biggest body and the lowest thump on the list, and no self-cycling action.
        BlastLowHz = 205f,
        BrightnessFloorFraction = 0.06f,
        BlastDecaySeconds = 0.038f,
        ThumpHz = 55f,
        ThumpDecaySeconds = 0.22f,
        BrightnessHz = 4800f,
        MechanicalDelaySeconds = 0f,
        MechanicalLevel = 0f,
    };

    /// <summary>Casings hitting the ground, shared by everything. Where it lands and how long after the
    /// shot are per-weapon; what it sounds like is not.</summary>
    public const string CasingFolder = "WEAPONS/_CASINGS";

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
