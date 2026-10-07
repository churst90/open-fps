using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Signals;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

public class SirenTests
{
    private readonly ITestOutputHelper _o;
    public SirenTests(ITestOutputHelper o) => _o = o;

    /// <summary>Each mode sweeps at the hardware's specified rate, counted from the oscillator's turning
    /// points: a yelp at the wrong rate is a different siren.</summary>
    [Fact]
    public void EachModeSweepsAtItsSpecifiedRate()
    {
        var spec = SirenSpec.Patrol100W;
        foreach (var (mode, expected) in new[]
        {
            (SirenMode.Wail, 60f / spec.WailSeconds),
            (SirenMode.Yelp, 60f / spec.YelpSeconds),
            (SirenMode.Phaser, 60f / spec.PhaserSeconds),
        })
        {
            float measured = SweepsPerMinute(spec, mode, seconds: 20f);
            _o.WriteLine($"{mode}: {measured:F0}/min against {expected:F0}");
            Assert.InRange(measured, expected * 0.9f, expected * 1.1f);
        }
    }

    /// <summary>The oscillator makes no partial that is not a harmonic of itself: aliases slide against
    /// the sweep and were heard as "stepping, not sweeping".</summary>
    [Fact]
    public void TheOscillatorDoesNotAlias()
    {
        var spec = SirenSpec.Patrol100W;
        // Hi-lo with both tones at the sweep's top: a constant frequency to measure.
        float hz = spec.SweepHighHz;
        var held = spec with { HiLoLowHz = hz, HiLoRatio = 1f, HiLoHoldSeconds = 600f };
        var siren = new ElectronicSiren(held, 44100f) { Mode = SirenMode.HiLo };
        for (int i = 0; i < 22050; i++) siren.Step();

        const int n = 44100;
        var x = new float[n];
        for (int i = 0; i < n; i++) { siren.Step(); x[i] = siren.Output; }

        double total = x.Sum(v => (double)v * v);
        Assert.True(total > 0, "the siren produced nothing");
        double harmonic = 0;
        for (int k = 1; k * hz < 44100 * 0.48f; k++)
            for (int off = -3; off <= 3; off++)
                harmonic += BinEnergy(x, k * hz + off, n);

        double offHarmonic = 100.0 * Math.Max(0.0, 1.0 - harmonic / total);
        _o.WriteLine($"off-harmonic energy: {offHarmonic:F3} %");
        Assert.True(offHarmonic < 1.0, $"{offHarmonic:F2} % of the siren is not a harmonic of itself");
    }

    /// <summary>
    /// The oscillator is a square, as the rotary chopper every electronic head imitates is: odd
    /// harmonics carry it (a sawtooth reads as brassy). The duty is a hair off a half, as a machined
    /// rotor's is, so a little even content remains.
    /// </summary>
    [Fact]
    public void TheOscillatorIsASquareNotASawtooth()
    {
        var spec = SirenSpec.Patrol100W;
        float hz = 900f;   // mid-sweep, so several harmonics fit under the driver's top
        var held = spec with { HiLoLowHz = hz, HiLoRatio = 1f, HiLoHoldSeconds = 600f };
        var siren = new ElectronicSiren(held, 44100f) { Mode = SirenMode.HiLo };
        for (int i = 0; i < 22050; i++) siren.Step();

        const int n = 44100;
        var x = new float[n];
        for (int i = 0; i < n; i++) { siren.Step(); x[i] = siren.Output; }

        // The 3rd against the 2nd, the 5th against the 4th.
        double h2 = BinEnergy(x, hz * 2, n), h3 = BinEnergy(x, hz * 3, n);
        double h4 = BinEnergy(x, hz * 4, n), h5 = BinEnergy(x, hz * 5, n);
        double oddOverEvenLow = 10 * Math.Log10(Math.Max(1e-20, h3) / Math.Max(1e-20, h2));
        double oddOverEvenHigh = 10 * Math.Log10(Math.Max(1e-20, h5) / Math.Max(1e-20, h4));
        _o.WriteLine($"3rd over 2nd: {oddOverEvenLow:F1} dB; 5th over 4th: {oddOverEvenHigh:F1} dB");

        // A sawtooth would be near -3.5 dB (the 1/n step).
        Assert.True(oddOverEvenLow > 12.0,
            $"the 3rd harmonic is only {oddOverEvenLow:F1} dB over the 2nd — this is a sawtooth, not a square");
        Assert.True(oddOverEvenHigh > 12.0,
            $"the 5th harmonic is only {oddOverEvenHigh:F1} dB over the 4th — this is a sawtooth, not a square");
    }

    /// <summary>The wail's declared bottom sits above the horn's flare cutoff, c / (pi D): below it the
    /// descent fades out instead of going down.</summary>
    [Fact]
    public void TheBottomOfTheWailIsAboveTheHornsCutoff()
    {
        foreach (var (name, make) in SirenSpec.Presets)
        {
            var spec = make();
            float bottom = spec.Name.Contains("two-tone", StringComparison.OrdinalIgnoreCase)
                ? spec.HiLoLowHz : spec.SweepLowHz;
            _o.WriteLine($"{name}: sweeps down to {bottom:F0} Hz against a {spec.FlareCutoffHz:F0} Hz cutoff");
            Assert.True(bottom > spec.FlareCutoffHz,
                $"{name} sweeps to {bottom:F0} Hz but its horn cuts off at {spec.FlareCutoffHz:F0} — "
              + "the bottom of the wail cannot radiate");
        }
    }

    /// <summary>Every head makes its declared level (a legal one, 120 dB at ten feet): the model measures
    /// its own insertion loss and compensates; without it the patrol head was 4 dB under.</summary>
    [Fact]
    public void EveryHeadMakesWhatItDeclares()
    {
        foreach (var (name, make) in SirenSpec.Presets)
        {
            var spec = make();
            var mode = spec.Name.Contains("two-tone", StringComparison.OrdinalIgnoreCase)
                ? SirenMode.HiLo : SirenMode.Wail;
            var siren = new ElectronicSiren(spec, 44100f) { Mode = mode };
            for (int i = 0; i < 44100 / 2; i++) siren.Step();
            double sum = 0;
            int n = (int)(44100 * MathF.Max(1f, spec.WailSeconds));
            for (int i = 0; i < n; i++) { siren.Step(); sum += (double)siren.Output * siren.Output; }
            float db = 20f * MathF.Log10(MathF.Max(1e-9f, MathF.Sqrt((float)(sum / n))) / 20e-6f);
            _o.WriteLine($"{name}: {db:F1} dB against a declared {spec.SourceLevelDb:F1}");
            Assert.InRange(db - spec.SourceLevelDb, -1.5f, 1.5f);
        }
    }

    /// <summary>A patrol car lapping the city's downtown loop does not change siren mode every corner:
    /// read off instant deceleration it flipped between wail and yelp several times a lap.</summary>
    [Fact]
    public void ASirenDoesNotChangeItsMindEveryCorner()
    {
        var line = new RaceLine(RouteTrafficTests.DowntownTour(), 0f, 62f / 3.6f, 0.85f, 2.92f);

        var siren = new SirenController(seed: 9);
        const float dt = 1f / 30f;                 // the rate the audio system sees positions at
        float lap = 0f, travelled = 0f;
        int laps = 0;
        // Two laps, driven by the line's own speed limit at each point.
        while (laps < 2)
        {
            line.Sample(travelled, out _, out _, out float v);
            siren.Update(v, dt);
            travelled += v * dt;
            lap += v * dt;
            if (lap >= line.Length) { lap -= line.Length; laps++; }
        }
        _o.WriteLine($"{siren.Changes} mode changes over two laps of {line.Length:F0} m");
        // A handful over two laps is a crew working, dozens a fault; changes include off and on between calls.
        Assert.InRange(siren.Changes, 1, 14);
    }

    /// <summary>A parked car's head is off, and it goes off at once rather than after the hold.</summary>
    [Fact]
    public void AStoppedCarIsSilent()
    {
        var siren = new SirenController(seed: 3);
        // Run until it picks up a call, then check it goes quiet the moment it stops rolling.
        int guard = 0;
        while (siren.Mode == SirenMode.Off && guard++ < 30_000) siren.Update(18f, 1f / 30f);
        Assert.NotEqual(SirenMode.Off, siren.Mode);
        for (int i = 0; i < 30; i++) siren.Update(0f, 1f / 30f);
        Assert.Equal(SirenMode.Off, siren.Mode);
    }

    /// <summary>A patrol car is off a call most of the time: the head is 130 dB at a metre and the car 95,
    /// and an always-on siren made "police cars that have no engine".</summary>
    [Fact]
    public void APatrolCarIsMostlyNotOnACall()
    {
        const float dt = 1f / 30f;
        int sounding = 0, total = 0;
        var seen = new HashSet<SirenMode>();
        // Several cars, because the phase is seeded per vehicle and one car's shift is not a rate.
        for (int car = 0; car < 6; car++)
        {
            var siren = new SirenController(car * 37 + 5);
            for (int i = 0; i < (int)(900f / dt); i++)     // fifteen minutes each
            {
                siren.Update(16f, dt);
                if (siren.Mode != SirenMode.Off) { sounding++; seen.Add(siren.Mode); }
                total++;
            }
        }
        float share = 100f * sounding / total;
        _o.WriteLine($"siren sounding {share:F0} % of the time; modes seen: {string.Join(", ", seen)}");
        Assert.InRange(share, 15f, 55f);
        // More than one mode is heard.
        Assert.True(seen.Count >= 2, $"only ever heard {string.Join(", ", seen)}");
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static float SweepsPerMinute(SirenSpec spec, SirenMode mode, float seconds)
    {
        var siren = new ElectronicSiren(spec, 44100f) { Mode = mode };
        for (int i = 0; i < 11025; i++) siren.Step();
        int n = (int)(seconds * 44100), turns = 0, dir = 0;
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            siren.Step();
            float f = siren.Hz;
            int d = f > prev + 1e-5f ? 1 : f < prev - 1e-5f ? -1 : dir;
            if (dir != 0 && d != 0 && d != dir) turns++;
            dir = d; prev = f;
        }
        return turns / 2f / seconds * 60f;
    }

    private static double BinEnergy(float[] x, float hz, int n)
    {
        double w = 2 * Math.PI * hz / 44100.0, cw = 2 * Math.Cos(w);
        double g1 = 0, g2 = 0;
        foreach (float v in x) { double g0 = v + cw * g1 - g2; g2 = g1; g1 = g0; }
        double mag = Math.Sqrt(g1 * g1 + g2 * g2 - cw * g1 * g2) * 2.0 / n;
        return mag * mag / 2.0 * n;
    }
}
