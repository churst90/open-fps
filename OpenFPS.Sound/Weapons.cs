using System.Collections.Generic;

namespace OpenFPS.Common;

/// <summary>
/// One weapon, as numbers. The figures are real: the muzzle velocity decides whether a round cracks
/// past a listener, how tight the crack is and the gap to the report, and a player who learns an AKM
/// by ear has learned something true about a 7.62x39 (see <see cref="Ballistics"/>).
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
    // On the weapon, never in a separate table keyed by name: such a table gave the Glock 340 m/s
    // against its own 375 (a crack scheduled and silence rendered into it), and made the Glock and the
    // .45 byte-identical.

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

    // ── Carrying it and loading it ──────────────────────────────────────────────────────────────
    // Real figures too: a Glock holds seventeen and a pump gun is loaded a shell at a time.

    /// <summary>Which ammunition it takes: a key into the game's Ammunition.</summary>
    public required string AmmoId { get; init; }

    /// <summary>Rounds it holds when full: the magazine, the tube or the cylinder.</summary>
    public required int MagazineCapacity { get; init; }

    /// <summary>How it is loaded, which decides whether a reload is one act or one per round.</summary>
    public required WeaponFeed Feed { get; init; }

    /// <summary>Which hands-on routine reloading it is: what gets pressed, pulled and slammed, and so
    /// what a reload sounds like and how long it takes (<see cref="WeaponHandling"/>).</summary>
    public required WeaponAction Action { get; init; }

    /// <summary>The fastest the action can be worked, rounds a minute. One shot per press of the
    /// trigger key; this only refuses presses faster than the gun could follow.</summary>
    public required float RoundsPerMinute { get; init; }

    /// <summary>Health taken from a person by one hit inside the effective range: per pellet, for
    /// buckshot. A person has 100.</summary>
    public required int Damage { get; init; }

    /// <summary>Out to here a hit does its full damage; beyond it a pistol bullet has slowed and
    /// spread enough to do less (see <see cref="DamageAt"/>). For buckshot this is unused: the
    /// pattern's spread decides how many pellets arrive.</summary>
    public required float EffectiveRangeMetres { get; init; }

    /// <summary>A polymer frame and magazine (a Glock) rather than steel (a 1911): the same routine of
    /// hands, a few decibels softer where plastic meets plastic.</summary>
    public bool PolymerFrame { get; init; }

    /// <summary>A full reload from empty, seconds: the hands' routine, from
    /// <see cref="WeaponHandling"/>, so the time and the sound can never disagree.</summary>
    public float ReloadSeconds => WeaponHandling.ReloadSeconds(this, MagazineCapacity, fromEmpty: true);

    /// <summary>The least time between two shots, seconds.</summary>
    public float SecondsBetweenShots => 60f / MathF.Max(1f, RoundsPerMinute);

    /// <summary>
    /// What one shot does to a person this far away, in health.
    ///
    /// Buckshot spreads about 2.5 cm for every metre it travels (an inch a yard, the figure every
    /// patterning board gives for a cylinder bore), so out to about 18 m all nine pellets land on a
    /// body 45 cm across, and beyond that the share that does falls with the pattern's area. A bullet
    /// does its full damage inside its effective range and falls to half at twice it.
    /// </summary>
    public int DamageAt(float metres)
    {
        if (PelletsPerShot > 1)
        {
            float pattern = MathF.Max(0.01f, 0.025f * metres);
            float share = MathF.Min(1f, (BodyWidthMetres / pattern) * (BodyWidthMetres / pattern));
            return (int)MathF.Round(Damage * PelletsPerShot * share);
        }
        float over = MathF.Max(0f, metres - EffectiveRangeMetres) / MathF.Max(1f, EffectiveRangeMetres);
        return (int)MathF.Round(Damage * MathF.Max(0.5f, 1f - 0.5f * over));
    }

    /// <summary>The width of a person's torso, metres: what a shotgun's pattern has to land on.</summary>
    public const float BodyWidthMetres = 0.45f;

    // ── In flight ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bullet's ballistic coefficient against the G7 standard projectile, lb/in², as its maker
    /// publishes it. It decides how fast the round slows, and so how far it drops and drifts and how long
    /// it takes to get there (<see cref="ExternalBallistics"/>). Zero for a round nobody shoots far
    /// enough for it to matter; such a round is flown with a G7 of 0.1, a pistol bullet's.
    /// </summary>
    public float BallisticCoefficientG7 { get; init; }

    /// <summary>The bullet's diameter, metres: its calibre. With its length it sets how hard its shock
    /// wave cracks past a listener and how long that crack lasts (Whitham's law, see
    /// <see cref="BulletFlyby"/>), and where the turbulence behind a subsonic one whizzes. Zero is a
    /// 9 mm pistol bullet's.</summary>
    public float BulletDiameterMetres { get; init; }

    /// <summary>The bullet's length, metres, nose to base. Zero is a 9 mm pistol bullet's.</summary>
    public float BulletLengthMetres { get; init; }

    /// <summary>The bullet's mass, kg, as its maker lists it in grains (one grain is 64.79891 mg). It is
    /// what the round carries into what it strikes (half m v squared, the energy an impact dumps), and
    /// what a ricochet's deformed slug keeps of it. Zero is a 124 grain 9 mm bullet's.</summary>
    public float BulletMassKg { get; init; }

    /// <summary>The barrel's rifling twist, metres per turn: how fast the bullet spins (its speed over
    /// this), which sets the gyroscopic nutation that modulates a subsonic whizz. Zero is one turn in
    /// 30 calibres.</summary>
    public float RiflingTwistMetres { get; init; }

    /// <summary>The sight this gun comes with, a ScopeRegistry id, or "" for iron sights.
    /// On the weapon for now because the one scoped rifle comes scoped; a scope mounted later on an AKM
    /// or an AR-15 is looked up by the same id, so nothing that reads it has to change.</summary>
    public string ScopeId { get; init; } = "";

    /// <summary>
    /// The fire selector's settings in the order the lever moves through them, or none for a gun that
    /// has no selector or safety lever (a Glock's safety is in its trigger, a double-action revolver's
    /// in its long pull). A two-position safety is { Safe, Semi } and its second setting is spoken
    /// "fire", as it is marked. The game's FireSelector steps through them.
    /// </summary>
    public FireMode[] Selector { get; init; } = Array.Empty<FireMode>();
}

/// <summary>Where a fire selector or safety lever can sit.</summary>
public enum FireMode
{
    /// <summary>The trigger is blocked: pressing it does nothing.</summary>
    Safe,
    /// <summary>One round for each press of the trigger.</summary>
    Semi,
    /// <summary>Rounds at the action's cyclic rate for as long as the trigger is held.</summary>
    Auto,
}

/// <summary>How a weapon is fed.</summary>
public enum WeaponFeed
{
    /// <summary>A detachable box: out with the old one, in with the new, all at once.</summary>
    Magazine,
    /// <summary>A tube under the barrel, loaded a shell at a time through the loading port.</summary>
    Tube,
    /// <summary>A revolver's cylinder: swung out, emptied, and filled from a speedloader.</summary>
    Cylinder,
    /// <summary>A box inside the stock, filled from the top through the open action a round at a time.</summary>
    InternalBox,
}

/// <summary>The routine of hands that reloads a weapon, which is what its reload sounds like.</summary>
public enum WeaponAction
{
    /// <summary>Kalashnikov: paddle release, the magazine rocked out and rocked in, and the charging
    /// handle on the right pulled and let go when the gun was empty.</summary>
    Kalashnikov,
    /// <summary>AR-15: a button drops the magazine free, a straight push seats the new one, and a slap
    /// on the bolt catch sends the bolt home when it was locked back empty.</summary>
    Stoner,
    /// <summary>Self-loading pistol: magazine button, the new one palmed in, the slide released.</summary>
    Pistol,
    /// <summary>Revolver: the cylinder swung out, the empties pushed out by the ejector rod, a
    /// speedloader, and the cylinder closed.</summary>
    Revolver,
    /// <summary>Pump gun: shells thumbed into the tube one at a time, and the action racked when the
    /// chamber was empty.</summary>
    Pump,
    /// <summary>Bolt action: the bolt lifted and drawn back, rounds pressed down into the internal
    /// magazine through the open action one at a time, and the bolt run forward and turned down. Worked
    /// by hand between every shot as well.</summary>
    BoltAction,
}

/// <summary>The weapons that exist. Five reports are measured on their own guns in the NIJ recordings;
/// the shotgun's and the M700's are estimated from the physics.</summary>
public static class WeaponRegistry
{
    /// <summary>One grain, kg: what bullet weights are published in.</summary>
    public const float Grain = 64.79891e-6f;

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
        // The standard 30-round steel magazine; 600 rounds a minute cyclic.
        AmmoId = "7.62x39", MagazineCapacity = 30, Feed = WeaponFeed.Magazine, Action = WeaponAction.Kalashnikov,
        RoundsPerMinute = 600f, Damage = 45, EffectiveRangeMetres = 300f,
        // The 123 grain steel-cored ball: G1 about 0.28, about 0.14 against the G7 shape.
        BallisticCoefficientG7 = 0.14f,
        // The M43 bullet: 7.92 mm across, 26.5 mm long.
        BulletDiameterMetres = 0.0079f, BulletLengthMetres = 0.0265f,
        // 123 grain; the AKM's 1 in 240 mm (9.45 in) twist.
        BulletMassKg = 123f * Grain, RiflingTwistMetres = 0.240f,
        // The lever on the right of the receiver: up is safe (and blocks the charging handle), the
        // middle detent automatic (AB), the bottom single shots (OD).
        Selector = new[] { FireMode.Safe, FireMode.Auto, FireMode.Semi },
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
        // The 30-round STANAG magazine. Semi-automatic, so the rate is how fast the action cycles.
        AmmoId = "5.56", MagazineCapacity = 30, Feed = WeaponFeed.Magazine, Action = WeaponAction.Stoner,
        RoundsPerMinute = 750f, Damage = 35, EffectiveRangeMetres = 300f,
        // M193, 55 grain: G1 0.243, about 0.12 against G7.
        BallisticCoefficientG7 = 0.12f,
        // M193: 5.70 mm across, 18.9 mm long.
        BulletDiameterMetres = 0.0057f, BulletLengthMetres = 0.0189f,
        // 55 grain; the original AR-15's 1 in 12 in twist, the one M193 was made for.
        BulletMassKg = 55f * Grain, RiflingTwistMetres = 0.3048f,
        // The M16-pattern selector: safe, semi, auto. A civilian AR-15's stops at semi; the report here
        // was measured on an M16, and this is its lever.
        Selector = new[] { FireMode.Safe, FireMode.Semi, FireMode.Auto },
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
        // Seventeen in the standard magazine: the number in its name.
        AmmoId = "9mm", MagazineCapacity = 17, Feed = WeaponFeed.Magazine, Action = WeaponAction.Pistol,
        RoundsPerMinute = 600f, Damage = 25, EffectiveRangeMetres = 50f, PolymerFrame = true,
        // 124 grain full metal jacket: 9.01 mm across, 15.6 mm long.
        BulletDiameterMetres = 0.0090f, BulletLengthMetres = 0.0156f,
        // 124 grain; the Glock 17's 1 in 250 mm (9.84 in).
        BulletMassKg = 124f * Grain, RiflingTwistMetres = 0.250f,
    };

    /// <summary>.45 ACP from a hammer-fired service pistol: heavy, slow and subsonic. No crack, only the
    /// report, so its range cannot be read off the gap the way a rifle's can.</summary>
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
        // A 1911's single-stack magazine holds seven.
        AmmoId = ".45", MagazineCapacity = 7, Feed = WeaponFeed.Magazine, Action = WeaponAction.Pistol,
        RoundsPerMinute = 500f, Damage = 30, EffectiveRangeMetres = 50f,
        // 230 grain ball: 11.5 mm across, 17 mm long.
        BulletDiameterMetres = 0.0115f, BulletLengthMetres = 0.017f,
        // 230 grain; the 1911's 1 in 16 in.
        BulletMassKg = 230f * Grain, RiflingTwistMetres = 0.4064f,
        // A 1911 has a thumb safety: up is safe, down is fire.
        Selector = new[] { FireMode.Safe, FireMode.Semi },
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
        // A six-shell tube, loaded one at a time. A pump is worked by hand between shots, about 0.85 s
        // for somebody practised: 70 a minute.
        AmmoId = "12 gauge", MagazineCapacity = 6, Feed = WeaponFeed.Tube, Action = WeaponAction.Pump,
        RoundsPerMinute = 70f, Damage = 12, EffectiveRangeMetres = 40f,
        // One 00 buck pellet, a lead ball 8.4 mm across: the charge is flown as one, along its centre.
        BulletDiameterMetres = 0.0084f, BulletLengthMetres = 0.0084f,
        // One 00 pellet, 53.8 grain of lead, from a smooth bore.
        BulletMassKg = 53.8f * Grain,
        // A pump gun's cross-bolt safety: safe or fire.
        Selector = new[] { FireMode.Safe, FireMode.Semi },
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
        // Six in the cylinder. Double action, so the long trigger pull is the limit: about 0.2 s.
        AmmoId = ".357", MagazineCapacity = 6, Feed = WeaponFeed.Cylinder, Action = WeaponAction.Revolver,
        RoundsPerMinute = 300f, Damage = 40, EffectiveRangeMetres = 50f,
        // 158 grain jacketed soft point: 9.07 mm across, 17.5 mm long.
        BulletDiameterMetres = 0.0091f, BulletLengthMetres = 0.0175f,
        // 158 grain; a Ruger GP100's 1 in 18.75 in.
        BulletMassKg = 158f * Grain, RiflingTwistMetres = 0.476f,
    };

    /// <summary>
    /// .308 Winchester from a 24-inch bolt-action rifle of the Remington 700 pattern, with a 4-12x
    /// scope. The load is Federal Gold Medal Match: a 168 grain Sierra MatchKing at 2650 ft/s (808 m/s),
    /// G7 0.224, the round long-range tables are written for, so what a player learns about where it
    /// lands at 600 m is true of the real one. No recording of the report: a longer pulse and more
    /// trailing gas than the 7.62x39 for about twice the charge, still short of a 12 gauge's.
    /// </summary>
    public static readonly WeaponDefinition M700 = new()
    {
        Id = "m700",
        DisplayName = "M700",
        Cartridge = ".308 Winchester",
        MuzzleVelocity = 808f,
        ReportPositivePhaseMs = 0.28f, ReportBurstDecayMs = 0.50f, ReportTrailDecayMs = 3.5f, ReportCornerHz = 1300f,
        ReportDamping = 0.5f, ReportTrailLevel = 0.2f,
        // Five in the internal box, loaded through the top. The bolt is worked by hand between shots,
        // lift, back, forward, down, about a second and a half with the eye kept on the scope.
        AmmoId = ".308", MagazineCapacity = 5, Feed = WeaponFeed.InternalBox, Action = WeaponAction.BoltAction,
        RoundsPerMinute = 40f, Damage = 80, EffectiveRangeMetres = 800f,
        BallisticCoefficientG7 = 0.224f,
        ScopeId = "scope_4_12",
        // The 168 grain MatchKing: 7.82 mm across, 30.9 mm (1.215 in) long.
        BulletDiameterMetres = 0.0078f, BulletLengthMetres = 0.0309f,
        // 168 grain; a 1 in 12 in .308 barrel.
        BulletMassKg = 168f * Grain, RiflingTwistMetres = 0.3048f,
        // The Model 700's two-position safety beside the bolt: safe or fire.
        Selector = new[] { FireMode.Safe, FireMode.Semi },
    };

    static WeaponRegistry()
    {
        foreach (var w in new[] { Akm, Ar15, Glock, ServicePistol, Shotgun, Revolver357, M700 })
            _byId[w.Id] = w;
    }

    public static IReadOnlyCollection<WeaponDefinition> All => _byId.Values;

    public static bool TryGet(string id, out WeaponDefinition weapon) =>
        _byId.TryGetValue(id ?? "", out weapon!);

    /// <summary>The weapon by id, or null. A caller that cannot go on without one should say which id it
    /// was given: a weapon that silently becomes another is a bug heard once and never reproduced.</summary>
    public static WeaponDefinition? Get(string id) =>
        _byId.TryGetValue(id ?? "", out var w) ? w : null;
}
