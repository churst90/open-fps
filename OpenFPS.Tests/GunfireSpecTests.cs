using OpenFPS.Client.AudioEngine.Core;
using OpenFPS.Common;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OpenFPS.Tests;

/// <summary>
/// The reports against the spec measured from the NIJ recordings (docs/GUNFIRE.md), read with the
/// same ruler the recordings were (<see cref="ReportMeasure"/>).
/// </summary>
public class GunfireSpecTests
{
    private const int Sr = WeaponSynth.SampleRate;

    /// <summary>
    /// Octave bands 125 Hz-16 kHz, dB re the loudest, of each gun's clean NIJ takes: the first 20 ms
    /// of every unclipped shot at 90, 130 and 180 degrees, 20 and 40 m, averaged per position and
    /// then over the six positions (`--gun-fit`, 2026-10-02).
    /// </summary>
    public static readonly TheoryData<string, float[]> Nij = new()
    {
        { "glock", new[] { -16.3f, -11.8f, -5.8f, 0.0f, -6.1f, -11.1f, -12.0f, -18.9f } },
        { "pistol", new[] { -14.6f, -10.5f, -5.1f, 0.0f, -2.0f, -10.4f, -10.6f, -17.3f } },
        { "ar15", new[] { -10.7f, -9.5f, -5.9f, 0.0f, -3.4f, -10.9f, -12.6f, -18.6f } },
        { "akm", new[] { -9.3f, -6.6f, -4.8f, -0.3f, 0.0f, -8.9f, -8.9f, -15.0f } },
        { "revolver357", new[] { -15.1f, -13.2f, -9.0f, 0.0f, -2.9f, -11.4f, -13.2f, -19.7f } },
    };

    /// <summary>The render carried 20 and 40 m over open ground, as the recordings were made, and
    /// measured the same way: bands averaged over four shots at each distance.</summary>
    private static float[] RenderedBands(WeaponDefinition w)
    {
        var all = new List<float[]>();
        foreach (float metres in new[] { 20f, 40f })
            for (int seed = 1; seed <= 4; seed++)
                all.Add(ReportMeasure.Measure(ReportMeasure.Propagate(Lead(WeaponSynth.MuzzleBlast(WeaponProfile.From(w), seed)), Sr, metres), Sr).BandsDb);
        var mean = Enumerable.Range(0, 8).Select(i => all.Average(b => b[i])).ToArray();
        float top = mean.Max();
        return mean.Select(v => v - top).ToArray();
    }

    private static float[] Lead(float[] x) => new float[Sr / 100].Concat(x).Concat(new float[Sr / 20]).ToArray();

    /// <summary>
    /// Each weapon's balance is its own gun's. Before 2026-10-02 the Glock was 6-12 dB too strong at
    /// 125-250 Hz and peaked at 2 kHz where every pistol recorded peaks at 1 kHz (8.1 dB rms off); now
    /// every gun is within 2.5 dB rms from 125 Hz to 8 kHz, no band of 125 Hz-4 kHz is more than
    /// 4.5 dB out, and the peak is where the recording's is. 16 kHz is left out: the renders are 5-7
    /// dB under the recordings there, which may be the recorder as much as the gun.
    /// </summary>
    [Theory]
    [MemberData(nameof(Nij))]
    public void EachWeaponsBandsAreItsOwnGunsInTheRecordings(string id, float[] nij)
    {
        var bands = RenderedBands(WeaponRegistry.Get(id)!);
        string Show() => $"{id}: rendered {string.Join(" ", bands.Select(b => b.ToString("F1")))}";
        float rms = MathF.Sqrt(Enumerable.Range(0, 7).Average(i => (bands[i] - nij[i]) * (bands[i] - nij[i])));
        Assert.True(rms <= 2.5f, $"{Show()}: {rms:F1} dB rms from the recordings");
        for (int i = 0; i < 6; i++)
            Assert.True(MathF.Abs(bands[i] - nij[i]) <= 4.5f,
                        $"{Show()}: {ReportMeasure.OctaveCentres[i]} Hz is {bands[i] - nij[i]:+0.0;-0.0} dB off");
        int peak = Array.IndexOf(bands, bands.Max());
        Assert.True(peak is 3 or 4, $"{Show()}: peaks at {ReportMeasure.OctaveCentres[peak]} Hz");
        // The pistols peak at 1 kHz and nowhere else, and are well down by 125-250 Hz.
        if (id is "glock" or "pistol" or "revolver357")
        {
            Assert.Equal(3, peak);
            Assert.True(bands[0] < -12f && bands[1] < -8f, Show());
        }
    }

    /// <summary>
    /// The .357 dies away more slowly than the 9 mm: in the recordings 1.4-1.5 ms to -10 dB side-on
    /// against the Glock's 1.0. Carried 20 m the same way, the render must be at least 0.15 ms slower
    /// and within 0.9-1.8 ms. Pooled over the six positions it renders 1.22 ms against the Glock's
    /// 0.88 (recorded 1.13 and 0.80).
    /// </summary>
    [Fact]
    public void TheMagnumDiesAwaySlowerThanTheNineMillimetre()
    {
        float Down10(WeaponDefinition w) => Enumerable.Range(1, 8).Average(s =>
            ReportMeasure.Measure(ReportMeasure.Propagate(Lead(WeaponSynth.MuzzleBlast(WeaponProfile.From(w), s)), Sr, 20f), Sr).Down10Ms);
        float magnum = Down10(WeaponRegistry.Revolver357), nine = Down10(WeaponRegistry.Glock);
        Assert.InRange(magnum, 0.9f, 1.8f);
        Assert.True(magnum >= nine + 0.15f, $".357 {magnum:F2} ms to -10 dB against the Glock's {nine:F2}");
    }

    /// <summary>
    /// The revolver blasts at its cylinder gap first: the muzzle blast arrives 0.3-0.5 ms after it,
    /// the bullet's time in the barrel. Found by correlating the revolver against its own muzzle blast
    /// rendered alone, and the gap's sound must be there before it.
    /// </summary>
    [Fact]
    public void TheCylinderGapLeadsTheMuzzleBlast()
    {
        var p = WeaponProfile.From(WeaponRegistry.Revolver357);
        Assert.InRange(p.GapLeadMs, 0.3f, 0.5f);
        var both = WeaponSynth.MuzzleBlast(p, 3);
        var muzzle = WeaponSynth.MuzzleBlast(p with { GapLevel = 0f }, 3);
        int best = 0; double bestC = double.MinValue;
        for (int lag = 0; lag < Sr / 1000; lag++)
        {
            double c = 0;
            for (int i = 0; i + lag < both.Length && i < muzzle.Length; i++) c += both[i + lag] * muzzle[i];
            if (c > bestC) { bestC = c; best = lag; }
        }
        float leadMs = best * 1000f / Sr;
        Assert.InRange(leadMs, 0.3f, 0.5f);
        float peak = both.Max(MathF.Abs);
        int onset = Array.FindIndex(both, v => MathF.Abs(v) > 0.1f * peak);
        Assert.True(onset * 1000f / Sr < leadMs - 0.25f, $"nothing before the muzzle blast: onset {onset * 1000f / Sr:F2} ms");
        // And nothing without a cylinder gets one.
        foreach (var w in WeaponRegistry.All.Where(w => w != WeaponRegistry.Revolver357))
            Assert.Equal(0f, w.CylinderGapLevel);
    }

    /// <summary>
    /// Every report carries the same energy (<see cref="WeaponSynth.ReportEnergyDb"/>), whatever its
    /// shape: none needs more than full scale to get there, and the reference is not set needlessly
    /// low (the sharpest report peaks above 0.85). Scaled to a peak instead, the shot carried 12 dB
    /// less than a clap at the same peak and a brighter gun came out quieter than a darker one.
    /// </summary>
    [Fact]
    public void EveryReportCarriesTheSameEnergy()
    {
        float sharpest = 0f;
        foreach (var w in WeaponRegistry.All)
            for (int seed = 1; seed <= 12; seed++)
            {
                var x = WeaponSynth.MuzzleBlast(WeaponProfile.From(w), seed);
                float peak = x.Max(MathF.Abs);
                sharpest = MathF.Max(sharpest, peak);
                Assert.True(peak < WeaponSynth.ReportPeakCeiling, $"{w.Id} seed {seed} peaks at {peak:F3}: held, not normalised");
                Assert.Equal(WeaponSynth.ReportEnergyDb, ReportMeasure.EnergyDb(x, Sr), 1);
            }
        Assert.True(sharpest > 0.85f, $"the loudest report peaks at only {sharpest:F2}: the energy could be higher");
    }

    /// <summary>
    /// A cartridge the loudness table does not know still plays (at the 7.62 level) but says so, once,
    /// naming the cartridge — it used to fall back to 159 dB in silence.
    /// </summary>
    [Fact]
    public void AnUnknownCartridgeIsWarnedAboutOnceByName()
    {
        var sink = new Capture();
        var before = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            string name = "test-cartridge-" + Guid.NewGuid().ToString("N");
            var odd = WeaponRegistry.Glock with { Cartridge = name };
            Assert.Equal(Loudness.Rifle762Db, Loudness.MuzzleBlastDb(odd));
            Assert.Equal(Loudness.Rifle762Db, Loudness.MuzzleBlastDb(odd));
            var mine = sink.Events.Where(e => e.RenderMessage().Contains(name)).ToList();
            Assert.Single(mine);
            Assert.Equal(LogEventLevel.Warning, mine[0].Level);
            Assert.Contains(name, Loudness.UnknownCartridges);
            // A known one says nothing.
            Assert.Equal(Loudness.Magnum357Db, Loudness.MuzzleBlastDb(WeaponRegistry.Revolver357));
            Assert.DoesNotContain(".357 Magnum", Loudness.UnknownCartridges);
        }
        finally
        {
            Log.Logger = before;
        }
    }

    private sealed class Capture : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        public IReadOnlyList<LogEvent> Events { get { lock (_events) return _events.ToList(); } }
        public void Emit(LogEvent e) { lock (_events) _events.Add(e); }
    }
}
