using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using OpenFPS.Common.Components;

namespace OpenFPS.Common;

/// <summary>Which kind of flash: one that reaches the ground, or one that stays in the cloud.</summary>
public enum FlashKind
{
    /// <summary>Cloud to ground: a near-vertical channel from the cloud's charge down to a point on the
    /// ground, re-used by several return strokes.</summary>
    CloudToGround,
    /// <summary>Intra-cloud: a mostly horizontal channel kilometres up, one discharge.</summary>
    IntraCloud,
}

/// <summary>
/// One lightning flash, as the server decides it and every client hears it. For a ground flash
/// <see cref="To"/> is the strike point and <see cref="From"/> the channel's top; the energy is per metre
/// on the first stroke. The channel's shape, branches and stroke times are drawn from
/// <see cref="Seed"/> (<see cref="LightningChannel.Build"/>), so two players together hear the same flash.
/// Metres in the map's frame, y up, the ground at y = 0.
/// </summary>
public readonly record struct LightningStrike(int Seed, FlashKind Kind, Vector3 From, Vector3 To,
                                              float EnergyPerMetre, int Strokes)
{
    /// <summary>What a strike is called on the wire, as a <see cref="TransientSound.SynthKey"/>.</summary>
    public const string KeyPrefix = "thunder:";

    /// <summary>The strike moved by <paramref name="offset"/>: the storm is drawn round a map's centre.</summary>
    public LightningStrike Offset(Vector3 offset) => this with { From = From + offset, To = To + offset };

    /// <summary>Where it reaches the ground for a ground flash; the middle of the channel for one in the cloud.</summary>
    public Vector3 Centre => Kind == FlashKind.CloudToGround ? To : (From + To) * 0.5f;

    public string Key()
        => string.Format(CultureInfo.InvariantCulture,
                         "thunder:{0}:{1}:{2:0.#}:{3:0.#}:{4:0.#}:{5:0.#}:{6:0.#}:{7:0.#}:{8:0}:{9}",
                         Kind == FlashKind.CloudToGround ? "cg" : "ic", Seed,
                         From.X, From.Y, From.Z, To.X, To.Y, To.Z, EnergyPerMetre, Strokes);

    public static bool TryParseKey(string? key, out LightningStrike strike)
    {
        strike = default;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var p = key[KeyPrefix.Length..].Split(':');
        if (p.Length != 10) return false;
        FlashKind kind;
        if (p[0] == "cg") kind = FlashKind.CloudToGround;
        else if (p[0] == "ic") kind = FlashKind.IntraCloud;
        else return false;
        var f = new float[8];
        if (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)) return false;
        for (int i = 0; i < 7; i++)
            if (!float.TryParse(p[2 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]) || !float.IsFinite(f[i]))
                return false;
        if (!int.TryParse(p[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out int strokes)) return false;
        strike = new LightningStrike(seed, kind, new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]),
                                     MathF.Max(1f, f[6]), Math.Clamp(strokes, 1, LightningPhysics.MaxStrokes));
        return true;
    }
}

/// <summary>
/// The numbers a lightning flash is made of, each from where it was measured. Data, not tuning: a
/// change here is a change to the physics, and the source should change with it.
/// </summary>
public static class LightningPhysics
{
    // ── The channel ──────────────────────────────────────────────────────────────────────────────

    /// <summary>One straight piece of channel, metres. Lacroix, Coulouvrat, Marchiano, Farges and
    /// Ripoll, "Acoustical energy of return strokes", Geophys. Res. Lett. 46 (2019), sec. 2.2, after
    /// LeVine and Gilson (1984): 8 m steps.</summary>
    public const float StepMetres = 8f;

    /// <summary>The mean absolute change of direction from one step to the next, degrees: Hill's
    /// stereophotographs of lightning (J. Geophys. Res. 73, 1968), 16.3 degrees, the figure Ribner and
    /// Roy (J. Acoust. Soc. Am. 72, 1982) and Lacroix et al. (2019) build their channels to.</summary>
    public const float MeanDeflectionDegrees = 16.3f;

    /// <summary>The spread of the random turn at each step, degrees, chosen so the channel's measured
    /// mean deflection (homing included) comes out at <see cref="MeanDeflectionDegrees"/>. Checked by
    /// LightningTests.ChannelDeflectionIsHills.</summary>
    public const float TurnSigmaDegrees = 21.5f;

    /// <summary>How hard a step is pulled toward the channel's far end, as a share of a unit direction;
    /// without it the walk wanders off (Lacroix et al. 2019 bias theirs to the vertical for the same
    /// reason). They report about 8 km of channel for a 5 km drop; this gives 1.56 times the straight
    /// line (AudioLab --thunder).</summary>
    public const float HomingShare = 0.19f;

    /// <summary>Where a negative ground flash starts, metres up: the lower negative charge, about
    /// 5 km (Rakov, "Fundamentals of Lightning", 2016; Lacroix et al. 2019 start theirs at 5 km).</summary>
    public const float GroundFlashTopMinMetres = 4000f, GroundFlashTopMaxMetres = 6000f;

    /// <summary>How far the top of a ground flash's channel is from above its strike point, metres (one
    /// standard deviation, each horizontal axis). Channels lean; the stepped leader comes down a
    /// kilometre or two from where it set off.</summary>
    public const float GroundFlashLeanMetres = 1500f;

    /// <summary>
    /// An intra-cloud flash runs between the cloud's charge layers: it starts in the main negative
    /// charge, about 4-7 km up, climbs to the upper positive, about 8-10 km, and spreads in each layer
    /// (Rakov and Uman 2003, ch. 9; the acoustic reconstructions of Lacroix's thesis, 2018, sec. 3,
    /// show intra-cloud channels joining layers at about 4 and 8 km). Metres.
    /// </summary>
    public const float CloudFlashLowerMinMetres = 4500f, CloudFlashLowerMaxMetres = 7000f;
    public const float CloudFlashUpperMinMetres = 8000f, CloudFlashUpperMaxMetres = 10000f;

    /// <summary>How far apart an intra-cloud flash's ends are across the ground, metres, median and
    /// spread (natural log). Flashes run 5-20 km horizontally (MacGorman, Few and Teer, J. Geophys.
    /// Res. 86, 1981).</summary>
    public const float CloudFlashLengthMedianMetres = 6000f, CloudFlashLengthSigma = 0.5f;

    /// <summary>Branches on a ground flash's channel, mean count; on an in-cloud one, whose branches
    /// spread through the charge layers and are most of its length.</summary>
    public const float GroundBranchesMean = 3f, CloudBranchesMean = 8f;

    /// <summary>A branch's length, metres, median; natural-log spread. An in-cloud branch is longer.</summary>
    public const float BranchLengthMedianMetres = 500f, BranchLengthSigma = 0.6f;
    public const float CloudBranchLengthMedianMetres = 1500f;

    /// <summary>A branch's share of the main channel's energy per metre: it carries only the current the
    /// return stroke drains from it, energy taken to scale with current. Heard on the first stroke only;
    /// subsequent strokes follow the main channel (Rakov and Uman, "Lightning: Physics and Effects",
    /// 2003, ch. 4). The figure is a judgement, not a measurement.</summary>
    public const float BranchEnergyShare = 0.15f;

    // ── The strokes ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Strokes in a negative ground flash: about a fifth are single and the mean is three to
    /// five (Rakov and Uman 2003, ch. 4). A geometric count with this chance of stopping after each
    /// stroke has a mean of four and a quarter single.</summary>
    public const float StrokeStopChance = 0.25f;
    public const int MaxStrokes = 12;

    /// <summary>Time between strokes, seconds: geometric mean about 60 ms (Rakov and Uman 2003, ch. 4),
    /// spread as a log-normal.</summary>
    public const float InterstrokeMedianSeconds = 0.060f, InterstrokeSigma = 0.6f;

    /// <summary>A subsequent stroke's energy against the first's: the median peak current of a
    /// subsequent stroke (12 kA) over a first stroke's (30 kA) (Berger, Anderson and Kroninger, Electra
    /// 41, 1975), taking the energy a stroke leaves in each metre of channel to scale with its current.
    /// That scaling is an assumption.</summary>
    public const float SubsequentStrokeEnergyShare = 0.4f, SubsequentStrokeSigma = 0.5f;

    /// <summary>
    /// The energy the first return stroke leaves in each metre of channel, J/m, median and spread.
    /// Krider, Dawson and Uman (J. Geophys. Res. 73, 1968) put a return stroke at about 2.3e5 J/m; Few's
    /// worked example in Rakov and Uman (2003, ch. 11) uses 1e6. At 2.3e5, Few's peak frequency (see
    /// <see cref="PeakFrequencyHz"/>) is 141 Hz, against the 148 Hz Lacroix et al. (2019) get from a
    /// radiation-hydrodynamics simulation of the channel.
    /// </summary>
    public const float GroundFlashEnergyMedian = 2.3e5f, GroundFlashEnergySigma = 0.5f;

    /// <summary>An in-cloud flash's energy per metre against a ground flash's: Holmes, Brook, Krehbiel
    /// and McCrory (J. Geophys. Res. 76, 1971) found intra-cloud thunder about a third as energetic as
    /// ground-flash thunder.</summary>
    public const float CloudFlashEnergyShare = 1f / 3f;

    /// <summary>Standard pressure, Pa, the P0 in Few's relaxation radius.</summary>
    public const float AmbientPressurePa = 101325f;

    /// <summary>
    /// Few's peak frequency of thunder, Hz: f_m = 0.63 c0 (P0 / E_l)^1/2 (Few, J. Geophys. Res. 74, 1969;
    /// Rakov and Uman 2003, eq. 11.3), the relaxation radius R_c = (E_l / (pi P0))^1/2 as a frequency: more
    /// energy per metre shocks a bigger column of air and makes a longer wave.
    /// </summary>
    public static float PeakFrequencyHz(float energyPerMetre, float speedOfSound = 343f)
        => 0.63f * speedOfSound * MathF.Sqrt(AmbientPressurePa / MathF.Max(1f, energyPerMetre));

    /// <summary>
    /// How long one element's N-wave lasts, seconds, once the shock has become a weak one: the
    /// duration whose spectrum peaks at <see cref="PeakFrequencyHz"/>. An N-wave's spectrum peaks at
    /// 0.66 over its length: Lacroix (thesis, Sorbonne 2018, sec. 5.1.5) fits an N-wave of 4.47 ms to
    /// the 148 Hz wave of the channel simulation.
    /// </summary>
    public static float NWaveSeconds(float energyPerMetre, float speedOfSound = 343f)
        => NWavePeakProduct / PeakFrequencyHz(energyPerMetre, speedOfSound);

    public const float NWavePeakProduct = 0.66f;

    // ── Storms ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A thunderstorm cell's total flash rate at maturity, flashes per minute. An ordinary cell makes
    /// a few a minute; severe storms make tens (Rakov and Uman 2003, ch. 3; Williams, J. Geophys. Res.
    /// 90, 1985). The cell's rate rises and falls over its life (<see cref="CellActivity"/>), so the
    /// mean over a whole cell is half this.
    /// </summary>
    public const float StormCellPeakFlashesPerMinute = 4f;

    /// <summary>Heavy rain can be convective enough to flash now and then. The peak rate of a rain
    /// cell at full precipitation, flashes per minute, rising from nothing at
    /// <see cref="RainFlashThreshold"/>. A judgement: a rain shower is a cell that mostly did not
    /// make it to a thunderstorm.</summary>
    public const float RainCellPeakFlashesPerMinute = 0.3f;
    public const float RainFlashThreshold = 0.5f;

    /// <summary>Intra-cloud flashes per ground flash. Boccippio, Cummins, Christian and Goodman (Mon.
    /// Weather Rev. 129, 2001) found a mean of 2.94 over the United States; Rakov and Uman give about
    /// 3. So a quarter of flashes reach the ground.</summary>
    public const float CloudToGroundRatio = 2.94f;
    public static float GroundFlashShare => 1f / (1f + CloudToGroundRatio);

    /// <summary>How long one cell lives, seconds: an ordinary cell's life is 30-60 minutes (Byers and
    /// Braham, "The Thunderstorm", 1949).</summary>
    public const float CellLifeMinSeconds = 30f * 60f, CellLifeMaxSeconds = 60f * 60f;

    /// <summary>How spread a cell's ground strikes are about its core, metres, one standard deviation
    /// per axis. Ground flashes cluster within a few kilometres of the precipitation core.</summary>
    public const float StrikeSpreadMetres = 3000f;

    /// <summary>The share of ground flashes that come out of the anvil downwind, 8-15 km from the core
    /// (the "bolt from the blue"). A judgement; they are rare and real.</summary>
    public const float AnvilStrikeShare = 0.03f;
    public const float AnvilStrikeMinMetres = 8000f, AnvilStrikeMaxMetres = 15000f;

    /// <summary>Where a new cell forms: this far upwind of the map's centre, metres, and up to
    /// <see cref="CellFormsAcrossMetres"/> to either side, so it drifts across or past the map.</summary>
    public const float CellFormsUpwindMinMetres = 8000f, CellFormsUpwindMaxMetres = 25000f;
    public const float CellFormsAcrossMetres = 10000f;

    /// <summary>A cell drifts with the ground wind (the only one the server has; the steering wind aloft
    /// is usually the same way and somewhat faster), held between these speeds, m/s.</summary>
    public const float CellDriftMinSpeed = 3f, CellDriftMaxSpeed = 20f;

    /// <summary>How far away a strike is still sent at all, metres: past this nothing of it is audible
    /// (thunder is seldom heard beyond about 25 km; Fleagle, J. Meteorol. 6, 1949).</summary>
    public const float SendRangeMetres = 40000f;

    /// <summary>A cell's flash rate over its life, 0..1: growing, mature, dissipating, as sin².</summary>
    public static float CellActivity(float age, float life)
    {
        if (life <= 0f || age < 0f || age > life) return 0f;
        float s = MathF.Sin(MathF.PI * age / life);
        return s * s;
    }

    /// <summary>The peak flash rate, flashes per second, of a cell in this weather; zero for none.</summary>
    public static float PeakFlashRate(WeatherType scenario, float precipitation)
    {
        if (scenario == WeatherType.Storm) return StormCellPeakFlashesPerMinute / 60f;
        if (scenario == WeatherType.Rain && precipitation > RainFlashThreshold)
            return RainCellPeakFlashesPerMinute / 60f * Math.Clamp((precipitation - RainFlashThreshold) / (1f - RainFlashThreshold), 0f, 1f);
        return 0f;
    }

    /// <summary>A normal deviate (Box-Muller), from the generator given, so it repeats with the seed.</summary>
    public static float Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    public static float LogNormal(Random rng, float median, float sigma) => median * MathF.Exp(sigma * Gaussian(rng));

    /// <summary>A Poisson count by inversion (small means only).</summary>
    public static int Poisson(Random rng, float mean)
    {
        double l = Math.Exp(-mean), p = 1.0;
        int k = 0;
        do { k++; p *= rng.NextDouble(); } while (p > l && k < 64);
        return k - 1;
    }
}

/// <summary>What the sky is doing, as a lightning schedule needs it.</summary>
public readonly record struct StormSky(WeatherType Scenario, float Precipitation, Vector3 Wind);

/// <summary>
/// A storm's flashes: one convective cell at a time, drifting with the wind across the map, flashing at a
/// rate that rises and falls over its life. Deterministic from its seed and the sky it is fed (the tests
/// hold it to that). Positions are relative to the map's centre, heights above the ground.
/// </summary>
public sealed class LightningSchedule
{
    private readonly Random _rng;

    /// <summary>The cell that is flashing, or none.</summary>
    public bool HasCell { get; private set; }
    /// <summary>Where the cell's core is, metres from the map's centre (y = 0).</summary>
    public Vector3 CellCentre { get; private set; }
    public float CellAge { get; private set; }
    public float CellLife { get; private set; }

    public LightningSchedule(int seed) { _rng = new Random(seed); }

    /// <summary>
    /// Moves the storm on by <paramref name="dt"/> seconds and adds any flashes in that time to
    /// <paramref name="into"/>. A Poisson process at the cell's rate: per step the chance of a flash is
    /// 1 - exp(-rate dt), which is exact for steps far shorter than the gap between flashes.
    /// </summary>
    public void Advance(float dt, in StormSky sky, List<LightningStrike> into)
    {
        if (dt <= 0f) return;
        float peak = LightningPhysics.PeakFlashRate(sky.Scenario, sky.Precipitation);
        if (peak <= 0f) { HasCell = false; return; }

        if (!HasCell || CellAge >= CellLife) NewCell(sky.Wind);

        var drift = Drift(sky.Wind);
        CellCentre += drift * dt;
        CellAge += dt;

        float rate = peak * LightningPhysics.CellActivity(CellAge, CellLife);
        double chance = 1.0 - Math.Exp(-rate * dt);
        if (_rng.NextDouble() < chance) into.Add(NextStrike(sky.Wind));
    }

    private static Vector3 Drift(Vector3 wind)
    {
        var w = new Vector3(wind.X, 0f, wind.Z);
        float speed = w.Length();
        if (speed < 1e-3f) return new Vector3(LightningPhysics.CellDriftMinSpeed, 0f, 0f);
        return w / speed * Math.Clamp(speed, LightningPhysics.CellDriftMinSpeed, LightningPhysics.CellDriftMaxSpeed);
    }

    private void NewCell(Vector3 wind)
    {
        var dir = Vector3.Normalize(Drift(wind));
        var across = new Vector3(-dir.Z, 0f, dir.X);
        float upwind = Lerp(LightningPhysics.CellFormsUpwindMinMetres, LightningPhysics.CellFormsUpwindMaxMetres, (float)_rng.NextDouble());
        float side = Lerp(-LightningPhysics.CellFormsAcrossMetres, LightningPhysics.CellFormsAcrossMetres, (float)_rng.NextDouble());
        CellCentre = -dir * upwind + across * side;
        CellLife = Lerp(LightningPhysics.CellLifeMinSeconds, LightningPhysics.CellLifeMaxSeconds, (float)_rng.NextDouble());
        // Born young and upwind: CellActivity is near zero at first, so it flashes little until it nears.
        CellAge = 0f;
        HasCell = true;
    }

    /// <summary>The next flash from the cell as it is now.</summary>
    public LightningStrike NextStrike(Vector3 wind)
    {
        int seed = _rng.Next();
        bool ground = _rng.NextDouble() < LightningPhysics.GroundFlashShare;
        float energy = LightningPhysics.LogNormal(_rng, LightningPhysics.GroundFlashEnergyMedian, LightningPhysics.GroundFlashEnergySigma);
        if (ground)
        {
            Vector3 at;
            if (_rng.NextDouble() < LightningPhysics.AnvilStrikeShare)
            {
                var dir = Vector3.Normalize(Drift(wind));
                float r = Lerp(LightningPhysics.AnvilStrikeMinMetres, LightningPhysics.AnvilStrikeMaxMetres, (float)_rng.NextDouble());
                float side = LightningPhysics.Gaussian(_rng) * LightningPhysics.StrikeSpreadMetres;
                at = CellCentre + dir * r + new Vector3(-dir.Z, 0f, dir.X) * side;
            }
            else
                at = CellCentre + new Vector3(LightningPhysics.Gaussian(_rng), 0f, LightningPhysics.Gaussian(_rng)) * LightningPhysics.StrikeSpreadMetres;
            at.Y = 0f;
            float top = Lerp(LightningPhysics.GroundFlashTopMinMetres, LightningPhysics.GroundFlashTopMaxMetres, (float)_rng.NextDouble());
            var from = at + new Vector3(LightningPhysics.Gaussian(_rng) * LightningPhysics.GroundFlashLeanMetres, top,
                                        LightningPhysics.Gaussian(_rng) * LightningPhysics.GroundFlashLeanMetres);
            int strokes = 1;
            while (strokes < LightningPhysics.MaxStrokes && _rng.NextDouble() > LightningPhysics.StrokeStopChance) strokes++;
            return new LightningStrike(seed, FlashKind.CloudToGround, from, at, energy, strokes);
        }
        else
        {
            var mid = CellCentre + new Vector3(LightningPhysics.Gaussian(_rng), 0f, LightningPhysics.Gaussian(_rng)) * LightningPhysics.StrikeSpreadMetres;
            float length = Math.Clamp(LightningPhysics.LogNormal(_rng, LightningPhysics.CloudFlashLengthMedianMetres, LightningPhysics.CloudFlashLengthSigma), 2000f, 20000f);
            float a = (float)(_rng.NextDouble() * Math.PI * 2.0);
            var half = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * (length * 0.5f);
            // From the lower charge to the upper, the two ends a flash's length apart across the ground.
            var from = mid - half;
            from.Y = Lerp(LightningPhysics.CloudFlashLowerMinMetres, LightningPhysics.CloudFlashLowerMaxMetres, (float)_rng.NextDouble());
            var to = mid + half;
            to.Y = Lerp(LightningPhysics.CloudFlashUpperMinMetres, LightningPhysics.CloudFlashUpperMaxMetres, (float)_rng.NextDouble());
            return new LightningStrike(seed, FlashKind.IntraCloud, from, to, energy * LightningPhysics.CloudFlashEnergyShare, 1);
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// A flash's channel, branches and strokes, rebuilt from the strike alone so every client has the same:
/// a random walk of <see cref="LightningPhysics.StepMetres"/> steps turned about random axes (Ribner and
/// Roy 1982; Lacroix et al. 2019), pulled toward its far end. A ground flash is walked up from its strike
/// point, so the point on the ground is exact.
/// </summary>
public sealed class LightningChannel
{
    /// <summary>The paths, main channel first: each a polyline of points, metres.</summary>
    public List<Vector3[]> Paths { get; } = new();
    /// <summary>Each path's share of the main channel's energy per metre: 1 for the main channel.</summary>
    public List<float> EnergyShares { get; } = new();
    /// <summary>When each stroke happens, seconds after the first; and its pressure against the first
    /// stroke's (its energy share, to the 1/4 power: see Thunder).</summary>
    public float[] StrokeTimes { get; private set; } = new[] { 0f };
    public float[] StrokeEnergyShares { get; private set; } = new[] { 1f };

    public static LightningChannel Build(in LightningStrike strike)
    {
        var rng = new Random(strike.Seed);
        var c = new LightningChannel();
        bool ground = strike.Kind == FlashKind.CloudToGround;
        Vector3 start = ground ? strike.To : strike.From;
        Vector3 end = ground ? strike.From : strike.To;
        var main = Walk(rng, start, end, Vector3.Distance(start, end) * 3f, floor: ground ? strike.To.Y : float.NegativeInfinity, stopAtEnd: true);
        c.Paths.Add(main);
        c.EnergyShares.Add(1f);

        int branches = LightningPhysics.Poisson(rng, ground ? LightningPhysics.GroundBranchesMean : LightningPhysics.CloudBranchesMean);
        for (int b = 0; b < branches && main.Length > 8; b++)
        {
            // Ground-flash branches leave the upper part of the channel and head down and out, as the
            // stepped leader's did; cloud branches spread sideways.
            int at = ground ? rng.Next(main.Length * 3 / 10, main.Length - 1) : rng.Next(1, main.Length - 1);
            var from = main[at];
            float length = LightningPhysics.LogNormal(rng, ground ? LightningPhysics.BranchLengthMedianMetres : LightningPhysics.CloudBranchLengthMedianMetres,
                                                      LightningPhysics.BranchLengthSigma);
            var outward = new Vector3(LightningPhysics.Gaussian(rng), 0f, LightningPhysics.Gaussian(rng));
            if (outward.LengthSquared() < 1e-6f) outward = Vector3.UnitX;
            outward = Vector3.Normalize(outward);
            var dir = ground ? Vector3.Normalize(outward * 0.8f - Vector3.UnitY)
                             : Vector3.Normalize(outward + new Vector3(0f, LightningPhysics.Gaussian(rng) * 0.2f, 0f));
            var target = from + dir * length;
            float floor = ground ? strike.To.Y + 20f : float.NegativeInfinity;
            var path = Walk(rng, from, target, length * 1.5f, floor, stopAtEnd: true);
            if (path.Length < 2) continue;
            c.Paths.Add(path);
            c.EnergyShares.Add(LightningPhysics.BranchEnergyShare);
        }

        int n = Math.Clamp(strike.Strokes, 1, LightningPhysics.MaxStrokes);
        c.StrokeTimes = new float[n];
        c.StrokeEnergyShares = new float[n];
        c.StrokeEnergyShares[0] = 1f;
        for (int i = 1; i < n; i++)
        {
            c.StrokeTimes[i] = c.StrokeTimes[i - 1] + LightningPhysics.LogNormal(rng, LightningPhysics.InterstrokeMedianSeconds, LightningPhysics.InterstrokeSigma);
            c.StrokeEnergyShares[i] = Math.Clamp(LightningPhysics.LogNormal(rng, LightningPhysics.SubsequentStrokeEnergyShare, LightningPhysics.SubsequentStrokeSigma), 0.05f, 1.5f);
        }
        return c;
    }

    /// <summary>
    /// A tortuous walk from <paramref name="start"/> toward <paramref name="end"/>: each step turned
    /// from the last by a half-normal angle about a random axis, then pulled toward the end.
    /// </summary>
    private static Vector3[] Walk(Random rng, Vector3 start, Vector3 end, float maxLength, float floor, bool stopAtEnd)
    {
        var pts = new List<Vector3> { start };
        var p = start;
        var dir = end - start;
        dir = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.UnitY;
        float sigma = LightningPhysics.TurnSigmaDegrees * MathF.PI / 180f;
        float step = LightningPhysics.StepMetres;
        float walked = 0f;
        while (walked < maxLength)
        {
            // Turn: by |N(0, sigma)| about an axis at a uniformly random angle round the direction.
            float theta = MathF.Abs(LightningPhysics.Gaussian(rng)) * sigma;
            float phi = (float)(rng.NextDouble() * Math.PI * 2.0);
            var a = MathF.Abs(dir.Y) < 0.9f ? Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY)) : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitX));
            var b = Vector3.Cross(dir, a);
            var turned = dir * MathF.Cos(theta) + (a * MathF.Cos(phi) + b * MathF.Sin(phi)) * MathF.Sin(theta);
            var home = end - p;
            float left = home.Length();
            if (stopAtEnd && left <= step) { pts.Add(end); break; }
            if (left > 1e-3f) turned += home / left * LightningPhysics.HomingShare;
            dir = Vector3.Normalize(turned);
            var next = p + dir * step;
            if (next.Y < floor)
            {
                // Nothing goes below the ground: a step that would is turned back level.
                dir = Vector3.Normalize(new Vector3(dir.X, MathF.Abs(dir.Y) * 0.2f, dir.Z));
                next = p + dir * step;
                if (next.Y < floor) next.Y = floor;
            }
            p = next;
            pts.Add(p);
            walked += step;
        }
        return pts.ToArray();
    }

    /// <summary>Every path's length added up, metres.</summary>
    public float TotalLength()
    {
        float sum = 0f;
        foreach (var path in Paths)
            for (int i = 1; i < path.Length; i++) sum += Vector3.Distance(path[i - 1], path[i]);
        return sum;
    }

    /// <summary>The mean angle between successive steps along the main channel, degrees.</summary>
    public float MeanDeflectionDegrees()
    {
        var m = Paths[0];
        double sum = 0; int n = 0;
        for (int i = 2; i < m.Length; i++)
        {
            var a = m[i - 1] - m[i - 2]; var b = m[i] - m[i - 1];
            if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f) continue;
            float cos = Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f);
            sum += MathF.Acos(cos); n++;
        }
        return n == 0 ? 0f : (float)(sum / n * 180.0 / Math.PI);
    }
}
