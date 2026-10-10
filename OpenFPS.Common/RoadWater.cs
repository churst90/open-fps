using System.Numerics;

namespace OpenFPS.Common;

// Water on the road: how much is under each wheel and how much grip is left. Worked out on the server
// and sent to every client (WorldStateUpdate.RoadWater, WheelState.Water), so the grip the server drives
// with and the hiss a client hears come from the same millimetres. Three stores, each with its published
// law (docs/WET_ROADS.md): the texture's voids (sand-patch MTD, ASTM E965), filled by rain and emptied
// only by evaporation, the damp road that stays noisy for an hour; the sheet running to the edge while it
// rains (Gallaway et al. 1979, FHWA-RD-79-31, eq. 16); the gutter (Izzard, HEC-22 eq. 4-2) and the
// puddles along the kerbs (PuddleField). Drying is Penman's (1948) equation.

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

/// <summary>The constants of the road's drainage, as data (defaults from the design manuals).</summary>
public sealed record RoadDrainageSpec
{
    /// <summary>Cross-fall of the carriageway from the crown to the kerb: AASHTO Green Book 1.5-2 %.</summary>
    public float CrossSlope { get; init; } = 0.02f;
    /// <summary>Longitudinal grade along the gutter: a town street, 1 %.</summary>
    public float LongitudinalSlope { get; init; } = 0.01f;
    /// <summary>Manning's n of an asphalt gutter (HEC-22 Table 4-1).</summary>
    public float GutterManningN { get; init; } = 0.016f;
    /// <summary>Metres of kerb between two inlets: 30-90 m in HEC-22 practice.</summary>
    public float InletSpacingMetres { get; init; } = 50f;
    /// <summary>The rational method's run-off coefficient of a paved street (0.7-0.95).</summary>
    public float RunoffCoefficient { get; init; } = 0.9f;
    /// <summary>The time constant the gutter's flow follows the rain with, s: a street's few minutes.</summary>
    public float GutterSeconds { get; init; } = 240f;
    /// <summary>Puddles: how many square metres of road drain into a square metre of puddle.</summary>
    public float PuddleCatchmentRatio { get; init; } = 8f;
    /// <summary>The depth a puddle's fill is counted in, mm: a puddle holding this is full.</summary>
    public float PuddleReferenceMm { get; init; } = 20f;
    /// <summary>Seepage out of a puddle through the cracks it sits in, mm/h.</summary>
    public float PuddleSeepMmPerHour { get; init; } = 0.3f;
    /// <summary>Latitude for the sun's height, degrees. Maps do not carry one yet.</summary>
    public float LatitudeDegrees { get; init; } = 45f;

    public static readonly RoadDrainageSpec Default = new();
}

/// <summary>The laws, as pure functions: nothing here keeps state.</summary>
public static class RoadWaterLaw
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
    /// Gallaway's water film depth above the texture, mm, <paramref name="drainMetres"/> down the
    /// cross-fall: WD = 0.00338 TXD^0.11 L^0.43 I^0.59 / S^0.42 − TXD in inches, feet and in/h (FHWA-RD-79-31,
    /// eq. 16, fitted to 335 measurements). Zero when the rain is too light for a film.
    /// </summary>
    public static float SheetDepthMm(float rainMmPerHour, float drainMetres, float textureMm, float crossSlope)
    {
        if (!(rainMmPerHour > 0f) || !(drainMetres > 0f)) return 0f;
        // The SI form: 0.00338 x 25.4 x 25.4^-0.11 x 0.3048^-0.43 x 25.4^-0.59 = 0.01485.
        float txd = MathF.Max(0.01f, textureMm);
        float s = MathF.Max(0.002f, crossSlope);
        float wd = 0.01485f * MathF.Pow(txd, 0.11f) * MathF.Pow(drainMetres, 0.43f) * MathF.Pow(rainMmPerHour, 0.59f) / MathF.Pow(s, 0.42f) - txd;
        return MathF.Max(0f, wd);
    }

    /// <summary>
    /// The kinematic-wave time to equilibrium of sheet flow, s: t_e = (n L / sqrt S)^0.6 / i^0.4 with
    /// i in m/s (Woolhiser and Liggett 1967; HEC-22 eq. 3-4). A minute or two; the sheet drains over the
    /// same, read off the Runoff.Rungs ladder at this time constant.
    /// </summary>
    public static float EquilibriumSeconds(float drainMetres, float rainMmPerHour, float manningN, float crossSlope)
    {
        float i = MathF.Max(0.5f, rainMmPerHour) / 3.6e6f;
        float a = manningN * MathF.Max(0.3f, drainMetres) / MathF.Sqrt(MathF.Max(0.002f, crossSlope));
        return MathF.Pow(a, 0.6f) / MathF.Pow(i, 0.4f);
    }

    /// <summary>
    /// How far the flow in a kerb gutter spreads into the road, metres: Izzard's equation in HEC-22's SI
    /// form, Q = (0.376 / n) Sx^1.67 SL^0.5 T^2.67, Q in m³/s.
    /// </summary>
    public static float GutterSpreadMetres(float flowM3PerSecond, RoadDrainageSpec d)
    {
        if (!(flowM3PerSecond > 0f)) return 0f;
        float k = 0.376f / d.GutterManningN * MathF.Pow(d.CrossSlope, 1.67f) * MathF.Sqrt(d.LongitudinalSlope);
        return MathF.Pow(flowM3PerSecond / k, 1f / 2.67f);
    }

    /// <summary>Saturation vapour pressure over water, hPa (Tetens).</summary>
    public static float SaturationHpa(float celsius) => 6.108f * MathF.Exp(17.27f * celsius / (celsius + 237.3f));

    /// <summary>
    /// Evaporation from a wet surface, mm/h: Penman's combination equation, E = (Δ Rn / λ + γ f(u) (es − ea))
    /// / (Δ + γ), f(u) = 0.26 (1 + 0.54 u2) mm/day/hPa (Shuttleworth 1993, Handbook of Hydrology eq. 4.2.27,
    /// with the 1948 wind function), the wind at two metres. Where the longwave loss makes the net
    /// radiation negative, evaporation stops rather than going to dew.
    /// </summary>
    public static float EvaporationMmPerHour(float airCelsius, float relativeHumidity, float windMps, float netRadiationWm2)
    {
        float es = SaturationHpa(airCelsius);
        float ea = es * Math.Clamp(relativeHumidity, 0f, 1f);
        float delta = 4098f * es / ((airCelsius + 237.3f) * (airCelsius + 237.3f));   // hPa/K
        const float Gamma = 0.66f;                                                     // hPa/K at sea level
        float wind = 0.26f / 24f * (1f + 0.54f * MathF.Max(0f, windMps));             // mm/h per hPa
        float radiative = netRadiationWm2 * 3600f / 2.45e6f;                           // mm/h
        float e = (delta * radiative + Gamma * wind * (es - ea)) / (delta + Gamma);
        return MathF.Max(0f, e);
    }

    /// <summary>
    /// The net radiation a road takes in, W/m²: the sun's height from the hour and the day, the clear-sky
    /// beam cut by cloud as Kasten and Czeplak (1980) found (1 − 0.75 c^3.4), an asphalt albedo of 0.1,
    /// and a longwave loss of 90 W/m² under a clear sky falling to a sixth of that under full cloud.
    /// </summary>
    public static float NetRadiationWm2(float hour, int dayOfYear, float cloud, float latitudeDegrees)
    {
        float lat = latitudeDegrees * MathF.PI / 180f;
        float decl = 23.44f * MathF.PI / 180f * MathF.Sin(2f * MathF.PI * (284 + dayOfYear) / 365f);
        float hourAngle = (hour - 12f) * 15f * MathF.PI / 180f;
        float sinElev = MathF.Sin(lat) * MathF.Sin(decl) + MathF.Cos(lat) * MathF.Cos(decl) * MathF.Cos(hourAngle);
        float c = Math.Clamp(cloud, 0f, 1f);
        float shortwave = sinElev > 0f ? 1000f * sinElev * (1f - 0.75f * MathF.Pow(c, 3.4f)) * 0.9f : 0f;
        float longwave = 90f * (1f - 0.83f * c);
        return shortwave - longwave;
    }

    /// <summary>How cloudy it is, 0..1: overcast while anything falls, else from the humidity, clear at
    /// 50 % and overcast at 95 %. A stand-in until the weather has a cloud cover of its own.</summary>
    public static float CloudFrom(float precipitationIntensity, float relativeHumidity)
        => MathF.Max(Math.Clamp(precipitationIntensity * 4f, 0f, 1f), Math.Clamp((relativeHumidity - 0.5f) / 0.45f, 0f, 1f));

    // ── Grip ────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// How much of a dry tyre's stick-slip squeal survives on a wet surface, 0..1. The squeal is the
    /// tread's stick-snap, strongest on clean, dry surfaces (Tan Li, NOISE-CON 2019); water in the
    /// contact lubricates it and the snap goes. Taken as falling with the texture's fill to a fifth when
    /// the voids are full, and with any film above that to nothing by a millimetre. An assumption, made
    /// to be judged by ear: no measurement of squeal against water depth was found.
    /// </summary>
    public static float SquealFactor(byte surface, float waterMm)
    {
        var t = TextureOf(surface);
        if (t.Frozen || !(waterMm > 0f)) return 1f;
        float holds = MathF.Max(0.05f, t.TextureDepthMm + t.SoakMm);
        float damp = Math.Clamp(waterMm / holds, 0f, 1f);
        float film = MathF.Max(0f, waterMm - holds);
        return (1f - 0.8f * damp) * MathF.Max(0f, 1f - film);
    }
}

/// <summary>
/// The water on one map's roads: the three stores of the top of this file, advanced by the server
/// every tick and sent to the clients whole (<see cref="Save"/>, <see cref="Load"/>).
/// </summary>
public sealed class RoadWater
{
    public RoadDrainageSpec Drainage { get; }

    /// <summary>The rain through each rung of <see cref="Runoff.Rungs"/>, mm/h.</summary>
    private readonly float[] _held = new float[Runoff.Rungs.Length];
    /// <summary>The water in each surface's texture, mm (by RoadSurfaces index).</summary>
    private readonly float[] _texture = new float[RoadWaterLaw.SurfaceCount];
    /// <summary>The water in a reference puddle, mm (0 .. Drainage.PuddleReferenceMm).</summary>
    private float _puddle;
    private float _rain, _evaporation;
    private bool _settled;

    public RoadWater(RoadDrainageSpec? drainage = null) => Drainage = drainage ?? RoadDrainageSpec.Default;

    /// <summary>The rain landing now, mm/h.</summary>
    public float RainMmPerHour => _rain;
    /// <summary>What the air is taking off a wet surface now, mm/h.</summary>
    public float EvaporationMmPerHour => _evaporation;
    /// <summary>How full the puddles are, 0..1.</summary>
    public float PuddleFill => Math.Clamp(_puddle / Drainage.PuddleReferenceMm, 0f, 1f);
    /// <summary>The water in a surface's texture, mm.</summary>
    public float TextureMm(byte surface) => surface < _texture.Length ? _texture[surface] : _texture[0];

    /// <summary>
    /// Advances the stores by <paramref name="dt"/> seconds. The first call settles them as if the
    /// weather had been like this for hours: a player who logs in during a downpour finds the road
    /// running with water, and one who logs in on a dry day finds it dry.
    /// </summary>
    public void Step(float rainMmPerHour, float evaporationMmPerHour, float dt)
    {
        float i = float.IsFinite(rainMmPerHour) ? MathF.Max(0f, rainMmPerHour) : 0f;
        float e = float.IsFinite(evaporationMmPerHour) ? MathF.Max(0f, evaporationMmPerHour) : 0f;
        _rain = i; _evaporation = e;
        if (!_settled) { Settle(i, e); return; }
        if (!(dt > 0f)) return;
        dt = MathF.Min(dt, 600f);
        for (int k = 0; k < _held.Length; k++)
            _held[k] += (i - _held[k]) * (1f - MathF.Exp(-dt / Runoff.Rungs[k]));
        for (byte s = 0; s < _texture.Length; s++)
        {
            float holds = RoadWaterLaw.HoldsMm(s);
            _texture[s] = Math.Clamp(_texture[s] + (i - e) * dt / 3600f, 0f, holds);
        }
        float inflow = Drainage.PuddleCatchmentRatio * Drainage.RunoffCoefficient * Through(Drainage.GutterSeconds);
        float outflow = e + Drainage.PuddleSeepMmPerHour;
        _puddle = Math.Clamp(_puddle + (inflow + i - outflow) * dt / 3600f, 0f, Drainage.PuddleReferenceMm);
    }

    /// <summary>Every store at the steady state of this weather.</summary>
    public void Settle(float rainMmPerHour, float evaporationMmPerHour)
    {
        float i = MathF.Max(0f, rainMmPerHour);
        _rain = i; _evaporation = evaporationMmPerHour;
        for (int k = 0; k < _held.Length; k++) _held[k] = i;
        for (byte s = 0; s < _texture.Length; s++) _texture[s] = i > evaporationMmPerHour ? RoadWaterLaw.HoldsMm(s) : 0f;
        float inflow = Drainage.PuddleCatchmentRatio * Drainage.RunoffCoefficient * i + i;
        _puddle = inflow > evaporationMmPerHour + Drainage.PuddleSeepMmPerHour ? Drainage.PuddleReferenceMm : 0f;
        _settled = true;
    }

    /// <summary>The rain through a catchment of time constant <paramref name="seconds"/>, mm/h (as
    /// Runoff.Through, on this map's own ladder).</summary>
    public float Through(float seconds)
    {
        var rungs = Runoff.Rungs;
        if (!(seconds > rungs[0])) return _held[0];
        for (int k = 1; k < rungs.Length; k++)
        {
            if (seconds > rungs[k]) continue;
            float t = MathF.Log(seconds / rungs[k - 1]) / MathF.Log(rungs[k] / rungs[k - 1]);
            return _held[k - 1] + (_held[k] - _held[k - 1]) * t;
        }
        return _held[^1];
    }

    /// <summary>
    /// The water under a wheel, mm from the bottom of the texture: the texture's own, a sheet running
    /// <paramref name="fromCrownMetres"/> down the cross-fall, the gutter's flow if the wheel is inside
    /// its spread (<paramref name="fromKerbMetres"/> from the kerb face; infinity for no kerb), and a
    /// puddle's depth there (<paramref name="puddleMm"/>, from PuddleField).
    /// </summary>
    public float WaterMm(byte surface, float fromCrownMetres, float fromKerbMetres, float halfWidthMetres, float puddleMm = 0f)
    {
        var t = RoadWaterLaw.TextureOf(surface);
        if (t.Frozen) return 0f;
        float held = TextureMm(surface);
        float holds = RoadWaterLaw.HoldsMm(surface);
        if (t.Drains) return held;
        float water = held;
        // The sheet stands only on a texture that is full.
        float full = holds > 0f ? Math.Clamp((held / holds - 0.9f) / 0.1f, 0f, 1f) : 1f;
        // Nothing running anywhere on the ladder: no sheet and no gutter (and none of their powers).
        if (full > 0f && _held[0] + _held[^1] + _held[_held.Length / 2] > 1e-3f)
        {
            float x = MathF.Max(0.3f, fromCrownMetres);
            float tau = RoadWaterLaw.EquilibriumSeconds(x, MathF.Max(_rain, _held[0]), t.ManningN, Drainage.CrossSlope);
            float sheet = RoadWaterLaw.SheetDepthMm(Through(tau), x, t.TextureDepthMm, Drainage.CrossSlope);
            water += sheet * full;
            if (float.IsFinite(fromKerbMetres) && halfWidthMetres > 0f)
            {
                float q = Drainage.RunoffCoefficient * Through(Drainage.GutterSeconds) / 3.6e6f * Drainage.InletSpacingMetres * halfWidthMetres;
                float spread = RoadWaterLaw.GutterSpreadMetres(q, Drainage);
                if (fromKerbMetres < spread) water = MathF.Max(water, held + (spread - MathF.Max(0f, fromKerbMetres)) * Drainage.CrossSlope * 1000f * full);
            }
        }
        return MathF.Max(water, puddleMm > 0f ? held + puddleMm : 0f);
    }

    // ── On the wire ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The whole state, for WorldStateUpdate.RoadWater: the rain, the evaporation, the ladder,
    /// the textures and the puddle.</summary>
    public float[] Save()
    {
        var a = new float[3 + _held.Length + _texture.Length];
        a[0] = _rain; a[1] = _evaporation; a[2] = _puddle;
        _held.CopyTo(a, 3);
        _texture.CopyTo(a, 3 + _held.Length);
        return a;
    }

    /// <summary>The state as a server sent it. A short or missing array (an older server) is dry.</summary>
    public void Load(float[]? a)
    {
        if (a == null || a.Length < 3 + _held.Length + _texture.Length) { Settle(0f, 0f); return; }
        _rain = a[0]; _evaporation = a[1]; _puddle = Sane(a[2]);
        for (int k = 0; k < _held.Length; k++) _held[k] = Sane(a[3 + k]);
        for (int s = 0; s < _texture.Length; s++) _texture[s] = Sane(a[3 + _held.Length + s]);
        _settled = true;
    }

    private static float Sane(float v) => float.IsFinite(v) ? MathF.Max(0f, v) : 0f;
}

/// <summary>
/// A carriageway as the puddles need it: the id its puddles are drawn from, the middle of the road in
/// order (x east, z north; y is not read) and its width kerb to kerb, metres. The host builds one from
/// its map's roads.
/// </summary>
public readonly record struct Carriageway(string Id, IReadOnlyList<Vector3> Centreline, float WidthMetres);

/// <summary>
/// A map's carriageways indexed by place, and the puddles along their kerbs: where on which road a
/// point is, how far from its crown and from its kerb, and how deep a puddle is there.
///
/// Puddles are where a kerb's gutter has a low spot: drawn once from each road's id, so every server
/// and client that has the same roads has the same puddles. Every <see cref="CellMetres"/> along each
/// kerb there is one with probability <see cref="PuddleChance"/>, 1.2-4 m long, reaching 0.6-1.8 m
/// into the road and 6-25 mm deep at its deepest when full. A puddle is a shallow bowl, depth below
/// its rim D (1 − r²) with r its normalised radius; holding water to a fraction F of its depth, the
/// water covers r² &lt; F and is D (F − r²) deep there — so a filling puddle spreads as it deepens.
/// </summary>
public sealed class PuddleField
{
    public const float CellMetres = 30f;
    public const float PuddleChance = 0.4f;
    private const float GridMetres = 16f;

    private readonly IReadOnlyList<Carriageway> _roads;
    private readonly float[][] _along;                     // cumulative length at each centreline point
    private readonly Dictionary<(int, int), List<(int Road, int Seg)>> _grid = new();
    private readonly List<Puddle>[] _puddles;

    public readonly record struct Puddle(int Road, int Side, float Along, float Length, float Reach, float DepthMm);

    public PuddleField(IReadOnlyList<Carriageway>? roads)
    {
        _roads = roads ?? Array.Empty<Carriageway>();
        _along = new float[_roads.Count][];
        _puddles = new List<Puddle>[_roads.Count];
        for (int r = 0; r < _roads.Count; r++)
        {
            var line = _roads[r].Centreline;
            var cum = new float[line.Count];
            for (int i = 1; i < line.Count; i++) cum[i] = cum[i - 1] + Flat(line[i] - line[i - 1]).Length();
            _along[r] = cum;
            float reach = _roads[r].WidthMetres * 0.5f + 1f;
            for (int i = 1; i < line.Count; i++) Index(r, i - 1, line[i - 1], line[i], reach);
            _puddles[r] = Draw(r, cum.Length > 0 ? cum[^1] : 0f);
        }
    }

    public IReadOnlyList<Puddle> PuddlesOn(int road) => road >= 0 && road < _puddles.Length ? _puddles[road] : Array.Empty<Puddle>();

    private List<Puddle> Draw(int r, float length)
    {
        var list = new List<Puddle>();
        if (_roads[r].WidthMetres <= 0f) return list;
        uint seed = Hash(_roads[r].Id);
        for (int side = -1; side <= 1; side += 2)
        {
            int cells = (int)(length / CellMetres);
            for (int c = 0; c < cells; c++)
            {
                uint h = Mix(seed ^ (uint)(c * 2 + (side > 0 ? 1 : 0)) * 0x9E3779B9u);
                if (U(ref h) >= PuddleChance) continue;
                float len = 1.2f + 2.8f * U(ref h);
                float at = c * CellMetres + len * 0.5f + (CellMetres - len) * U(ref h);
                float reach = 0.6f + 1.2f * U(ref h);
                float depth = 6f + 19f * U(ref h);
                list.Add(new Puddle(r, side, at, len, reach, depth));
            }
        }
        return list;
    }

    /// <summary>
    /// Where a point is on the roads: the road it is on (its index), how far along that road's
    /// centreline, and how far to the right of it (negative left). False off every carriageway.
    /// </summary>
    public bool Locate(Vector3 p, out int road, out float along, out float lateral)
    {
        road = -1; along = lateral = 0f;
        if (!_grid.TryGetValue(Cell(p), out var near)) return false;
        float best = float.MaxValue;
        foreach (var (r, s) in near)
        {
            var line = _roads[r].Centreline;
            Vector3 a = Flat(line[s]), b = Flat(line[s + 1]), q = Flat(p), ab = b - a;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-9f) continue;
            float t = Math.Clamp(Vector3.Dot(q - a, ab) / len2, 0f, 1f);
            var foot = a + ab * t;
            float off = Vector3.Distance(q, foot);
            if (off > _roads[r].WidthMetres * 0.5f || off >= best) continue;
            best = off;
            road = r;
            along = _along[r][s] + t * MathF.Sqrt(len2);
            // Right of a heading (dx, dz) is (dz, -dx), x east and z north (RoadNetwork.Offset).
            var dir = ab / MathF.Sqrt(len2);
            lateral = Vector3.Dot(q - foot, new Vector3(dir.Z, 0f, -dir.X));
        }
        return road >= 0;
    }

    /// <summary>The road at an index.</summary>
    public Carriageway Road(int index) => _roads[index];

    /// <summary>
    /// The water a puddle holds at this place on a road, mm, with the puddles <paramref name="fill"/>
    /// full (RoadWater.PuddleFill).
    /// </summary>
    public float PuddleMm(int road, float along, float lateral, float fill)
    {
        if (!(fill > 0f) || road < 0 || road >= _puddles.Length) return 0f;
        float half = _roads[road].WidthMetres * 0.5f;
        float fromKerb = half - MathF.Abs(lateral);
        int side = lateral >= 0f ? 1 : -1;
        float deepest = 0f;
        foreach (var pd in _puddles[road])
        {
            if (pd.Side != side) continue;
            float u = (along - pd.Along) / (0.5f * pd.Length);
            if (u * u >= fill) continue;
            float v = fromKerb / pd.Reach;
            float r2 = u * u + v * v;
            if (r2 >= fill) continue;
            deepest = MathF.Max(deepest, pd.DepthMm * (fill - r2));
        }
        return deepest;
    }

    /// <summary>
    /// The water under a wheel at <paramref name="p"/> on <paramref name="surface"/>: the road there
    /// (crown, kerb, any puddle), or the texture's own water off every carriageway.
    /// </summary>
    public float WaterAt(RoadWater water, Vector3 p, byte surface)
    {
        if (!Locate(p, out int road, out float along, out float lateral))
            return water.WaterMm(surface, 2f, float.PositiveInfinity, 0f);
        float half = _roads[road].WidthMetres * 0.5f;
        return water.WaterMm(surface, MathF.Abs(lateral), half - MathF.Abs(lateral), half, PuddleMm(road, along, lateral, water.PuddleFill));
    }

    private void Index(int road, int seg, Vector3 a, Vector3 b, float reach)
    {
        float minX = MathF.Min(a.X, b.X) - reach, maxX = MathF.Max(a.X, b.X) + reach;
        float minZ = MathF.Min(a.Z, b.Z) - reach, maxZ = MathF.Max(a.Z, b.Z) + reach;
        for (int x = (int)MathF.Floor(minX / GridMetres); x <= (int)MathF.Floor(maxX / GridMetres); x++)
        for (int z = (int)MathF.Floor(minZ / GridMetres); z <= (int)MathF.Floor(maxZ / GridMetres); z++)
        {
            if (!_grid.TryGetValue((x, z), out var list)) _grid[(x, z)] = list = new List<(int, int)>();
            list.Add((road, seg));
        }
    }

    private static (int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / GridMetres), (int)MathF.Floor(p.Z / GridMetres));
    private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    private static uint Hash(string s)
    {
        uint h = 2166136261u;
        foreach (char c in s) { h ^= c; h *= 16777619u; }
        return Mix(h);
    }

    private static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
        return x;
    }

    private static float U(ref uint h)
    {
        h = Mix(h + 0x9E3779B9u);
        return (h >> 8) * (1f / 16777216f);
    }
}
