namespace OpenFPS.Common;

// What water on the road does to a tyre's grip: a law of the contact between tyre and road, so it is the
// physics' (docs/PHYSICS_BOUNDARY.md 4.2). The water itself, how it gathers, runs off and dries, and what it
// sounds like, is the sound's (RoadWater, RoadWaterLaw), which reads the surfaces' table here.

/// <summary>A surface's water-holding: its texture, its roughness to sheet flow, how much grip it keeps wet.</summary>
/// <param name="TextureDepthMm">Sand-patch mean texture depth, mm: the water the voids hold before a film forms.</param>
/// <param name="ManningN">Manning's n for sheet flow across it (HEC-22 Table 3-2: smooth asphalt 0.011, concrete 0.012).</param>
/// <param name="WetGripRatio">Peak friction wet over dry, at a town speed (Wong, Theory of Ground Vehicles, Table 1.3).</param>
/// <param name="SoakMm">Water it soaks up below the surface, mm, before anything stands on it (an earth road;
/// zero for a sealed surface).</param>
/// <param name="Drains">Loose stone: water drains straight through and nothing stands on it.</param>
/// <param name="Frozen">Snow and ice: the water model does not apply.</param>
public readonly record struct RoadTexture(float TextureDepthMm, float ManningN, float WetGripRatio, float SoakMm = 0f,
                                          bool Drains = false, bool Frozen = false);

/// <summary>The laws of a tyre on a wet road, as pure functions: nothing here keeps state.</summary>
public static class RoadGripLaw
{
    private const float MmPerInch = 25.4f;

    // Index by RoadSurfaces: unknown, asphalt, concrete, gravel, dirt, snow, ice.
    // Texture depths: dense asphalt concrete 0.5-1.0 mm sand patch, brushed or tined concrete 0.6-1.2
    // (PIARC Technical Committee on Surface Characteristics, 1987; Sandberg and Ejsmont 2002, ch. 9).
    // Wet over dry peak friction: Wong's Table 1.3 — asphalt and concrete dry 0.8-0.9, asphalt wet
    // 0.5-0.7, concrete wet 0.8, earth road dry 0.68, wet 0.55 — each ratio over the dry figure. Gravel
    // has no wet figure there; it drains, and keeps its grip.
    private static readonly RoadTexture[] Table =
    {
        new(0.7f, 0.011f, 0.6f / 0.85f),                       // 0: unknown, as asphalt
        new(0.7f, 0.011f, 0.6f / 0.85f),                       // 1: asphalt
        new(0.8f, 0.012f, 0.8f / 0.85f),                       // 2: concrete
        new(3f, 0.02f, 1f, Drains: true),                      // 3: gravel
        new(1.5f, 0.02f, 0.55f / 0.68f, SoakMm: 4f),           // 4: dirt
        new(0f, 0.02f, 1f, Frozen: true),                      // 5: snow
        new(0f, 0.02f, 1f, Frozen: true),                      // 6: ice
    };

    public static int SurfaceCount => Table.Length;

    public static RoadTexture TextureOf(byte surface) => surface < Table.Length ? Table[surface] : Table[0];

    /// <summary>What a surface holds before any water stands on it, mm: its texture and what it soaks up.</summary>
    public static float HoldsMm(byte surface)
    {
        var t = TextureOf(surface);
        return t.Frozen ? 0f : t.TextureDepthMm + t.SoakMm;
    }

    /// <summary>
    /// The speed at which a tyre aquaplanes on a film <paramref name="filmMm"/> deep above the texture,
    /// km/h: Gallaway et al.'s (1979) empirical equation for treaded car tyres,
    ///   V = SD^0.04 P^0.3 (TD + 1)^0.06 A,  A = max(10.409 / WD^0.06 + 3.507, (28.952 / WD^0.06 − 7.817) TXD^0.14)
    /// (mph, spin-down 10 %, P psi, TD in 32nds of an inch, WD and TXD in inches). For a 220 kPa car tyre
    /// on a 2 mm film it gives about 89 km/h, beside NASA's 6.34 sqrt(p kPa) = 94 km/h (10.35 sqrt(p psi) mph) for a fully flooded
    /// smooth tyre (Horne and Dreher 1963, NASA TN D-2056); it depends on the film only weakly.
    /// </summary>
    public static float AquaplaningKmh(float filmMm, float textureMm, float inflationKPa, float treadMm)
    {
        float wd = MathF.Max(0.01f, filmMm) / MmPerInch;
        float txd = MathF.Max(0.05f, textureMm) / MmPerInch;
        float p = MathF.Max(50f, inflationKPa) * 0.1450377f;
        float td = MathF.Max(0f, treadMm) / MmPerInch * 32f;
        float w6 = MathF.Pow(wd, 0.06f);
        float a = MathF.Max(10.409f / w6 + 3.507f, (28.952f / w6 - 7.817f) * MathF.Pow(txd, 0.14f));
        float mph = MathF.Pow(10f, 0.04f) * MathF.Pow(p, 0.3f) * MathF.Pow(td + 1f, 0.06f) * a;
        return mph * 1.609344f;
    }

    /// <summary>The PIARC speed constant of wet friction, km/h: Sp = 14.2 + 89.7 MPD (Wambold et al.
    /// 1995, the International Friction Index), MPD from the sand-patch depth by ASTM E1845 (MTD = 0.2 +
    /// 0.8 MPD).</summary>
    public static float FrictionSpeedConstantKmh(float textureMm) => 14.2f + 89.7f * MathF.Max(0.05f, (textureMm - 0.2f) / 0.8f);

    /// <summary>The share of a tyre's speed it slips at when it is making its most force: the peak of
    /// the longitudinal curve, about 12 % for a car tyre (Pacejka 2006, fig. 1.4).</summary>
    public const float PeakSlip = 0.12f;

    /// <summary>The speed at which Wong's wet figures are taken as read, km/h: a town speed.</summary>
    public const float WetGripReferenceKmh = 50f;

    /// <summary>The share of a tyre's tread depth its grooves can carry water away in: a summer tyre's
    /// groove void ratio, about 0.3.</summary>
    public const float GrooveVoidRatio = 0.3f;

    /// <summary>
    /// What a tyre keeps of its dry grip with this much water under it at this speed, 0..1:
    ///
    ///   * damp, the texture filling: from dry toward Wong's wet ratio as the voids fill;
    ///   * wet friction falls with the slip speed through the PIARC exponential, exp((S_ref − S) / Sp),
    ///     the slip speed at peak force being PeakSlip of the road speed, normalised at 50 km/h;
    ///   * a film standing above the texture lifts the tyre: the water's dynamic pressure grows as V²
    ///     and carries the whole load at the aquaplaning speed, so the share of the contact patch lifted
    ///     is (V / Vp)², of which the grooves swallow a film up to their own volume: the lift is
    ///     scaled by h / (h + void ratio × tread depth). A slick has nothing to swallow it with.
    /// </summary>
    public static float GripFactor(byte surface, float waterMm, float speedMps, float inflationKPa, float treadMm)
    {
        var t = TextureOf(surface);
        if (t.Frozen || t.Drains || !(waterMm > 0f)) return 1f;
        float holds = t.TextureDepthMm + t.SoakMm;
        float damp = Math.Clamp(waterMm / MathF.Max(0.05f, holds), 0f, 1f);
        float kmh = MathF.Abs(speedMps) * 3.6f;
        float sp = FrictionSpeedConstantKmh(t.TextureDepthMm);
        float speedTerm = MathF.Min(1f / t.WetGripRatio, MathF.Exp(PeakSlip * (WetGripReferenceKmh - kmh) / sp));
        float wet = t.WetGripRatio * speedTerm;
        float ratio = 1f - damp * (1f - wet);
        float film = MathF.Max(0f, waterMm - holds);
        if (film > 0f && kmh > 1f)
        {
            float vp = AquaplaningKmh(film, t.TextureDepthMm, inflationKPa, treadMm);
            float lift = MathF.Min(1f, (kmh / vp) * (kmh / vp)) * film / (film + GrooveVoidRatio * MathF.Max(0f, treadMm));
            ratio *= MathF.Max(0.05f, 1f - lift);
        }
        return Math.Clamp(ratio, 0.05f, 1f);
    }
}
