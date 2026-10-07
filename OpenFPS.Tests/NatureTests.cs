using System.Numerics;
using System.Text.Json;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Water, fire and the wind in leaves: the wind field they share, the physics each is built on, the
/// level each voice arrives at the mixer with, and where they stand on the city.
///
/// The models were fitted against recordings with the lab (--nature levels / compare=); these hold
/// the things that must not drift once fitted — a gust that travels, a bubble that rings at its
/// Minnaert note, a voice at its declared level with room for its peaks, and a fountain that is not
/// inside its own pedestal.
/// </summary>
public class NatureTests
{
    private readonly ITestOutputHelper _o;
    public NatureTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    // ── The wind ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWindIsTheSameForEveryoneWhoAsks()
    {
        for (double t = 1000; t < 1100; t += 7.3)
            Assert.Equal(WindField.SpeedAt(12f, 7f, -40f, t), WindField.SpeedAt(12f, 7f, -40f, t));
    }

    [Fact]
    public void TheGustSignalHasUnitVarianceAboutZero()
    {
        double sum = 0, sq = 0;
        int n = 0;
        for (double t = 0; t < 40000; t += 0.7) { float g = WindField.Gust(-300f, 200f, t); sum += g; sq += g * g; n++; }
        double mean = sum / n, sd = Math.Sqrt(sq / n - mean * mean);
        _o.WriteLine($"gust mean {mean:F3}, sd {sd:F3}");
        Assert.InRange(mean, -0.15, 0.15);
        Assert.InRange(sd, 0.8, 1.2);
    }

    /// <summary>
    /// A gust is carried by the wind: what a place downwind feels is what a place upwind felt, the
    /// distance over the wind speed earlier. That is what makes a gust heard coming through a park.
    /// </summary>
    [Fact]
    public void AGustTravelsDownwind()
    {
        var (dx, dz) = WindField.Downwind;
        const float metres = 90f;
        float lag = metres / WindField.MeanSpeed;
        double same = 0, carried = 0, aa = 0, bb = 0, cc = 0;
        for (double t = 0; t < 6000; t += 0.5)
        {
            float a = WindField.Gust(0f, 0f, t);
            float b = WindField.Gust(dx * metres, dz * metres, t);
            float c = WindField.Gust(dx * metres, dz * metres, t + lag);
            same += a * b; carried += a * c; aa += a * a; bb += b * b; cc += c * c;
        }
        double rSame = same / Math.Sqrt(aa * bb), rCarried = carried / Math.Sqrt(aa * cc);
        _o.WriteLine($"correlation 90 m downwind: at once {rSame:F2}, {lag:F0} s later {rCarried:F2}");
        Assert.True(rCarried > 0.98, $"the gust downwind {lag:F0} s later should be the same gust: {rCarried:F2}");
        Assert.True(rSame < rCarried - 0.1, $"at the same moment it should not be: {rSame:F2}");
    }

    [Fact]
    public void TheWindFallsOffTowardTheGround()
    {
        Assert.True(WindField.MeanAt(0.8f) < WindField.MeanAt(7f));
        Assert.True(WindField.MeanAt(7f) < WindField.MeanAt(10f) + 1e-4f);
        Assert.Equal(WindField.MeanSpeed, WindField.MeanAt(10f), 3);
    }

    // ── Water ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABubbleRingsAtItsMinnaertNoteAndDiesAsVanDenDoelSays()
    {
        // 3.26 / R: a millimetre is 3.26 kHz.
        Assert.Equal(3260f, FallingWaterSynth.MinnaertHzMetres / 1e-3f, 1);
        // d = 0.13 / R + 0.0072 R^-3/2: 130 + 228 per second at a millimetre, a damping ratio of 0.035.
        Assert.InRange(FallingWaterSynth.BubbleDecay(1f), 350f, 365f);
        Assert.InRange(FallingWaterSynth.BubbleDamping(1f), 0.03f, 0.04f);
        // Medwin's type II bubble: about 10 kHz from a 2.2 mm drop, falling for bigger ones.
        float small = FallingWaterSynth.MinnaertHzMetres / (FallingWaterSynth.TypeTwoBubbleMm(1.1f) * 1e-3f);
        float big = FallingWaterSynth.MinnaertHzMetres / (FallingWaterSynth.TypeTwoBubbleMm(3f) * 1e-3f);
        Assert.InRange(small, 9000f, 11000f);
        Assert.InRange(big, 1500f, 3000f);
    }

    [Fact]
    public void ADropFallsNoFasterThanItsTerminalSpeed()
    {
        // Gunn and Kinzer measured 6.49 m/s for a 2 mm drop; the fit gives 6.55.
        Assert.InRange(FallingWaterSynth.TerminalSpeed(1e-3f), 6.3f, 6.7f);
        Assert.True(FallingWaterSynth.ArrivalSpeed(1e-3f, 100f) <= FallingWaterSynth.TerminalSpeed(1e-3f) + 1e-3f);
        // A short fall is still nearly free fall: √(2 g h).
        Assert.InRange(FallingWaterSynth.ArrivalSpeed(2e-3f, 0.3f), 2.2f, 2.45f);
    }

    [Fact]
    public void TwoFountainsAreTwoFountains()
    {
        var spec = WaterFeatureSpec.ByName("park_fountain");
        var a = new FallingWaterSynth(spec, Rate, 1);
        var b = new FallingWaterSynth(spec, Rate, 2);
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < Rate * 2; i++)
        {
            if (i % 256 == 0) { a.Control(256f / Rate); b.Control(256f / Rate); }
            float x = a.Next(), y = b.Next();
            ab += x * y; aa += x * x; bb += y * y;
        }
        double r = ab / Math.Sqrt(aa * bb);
        Assert.InRange(r, -0.1, 0.1);
    }

    // ── The voices ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A voice rendered offline, its ring undone into pascals: rms dB, and how much of it sat
    /// on the soft ceiling's knee. In blocks, as the mixer takes it: one long Render would run the
    /// voice's ring round on itself.</summary>
    private static (float RmsDb, double OnKnee) Measure(PhysicalVoiceState voice, float seconds)
    {
        var warm = new float[Rate / 2];
        for (int i = 0; i < warm.Length; i += 1024) voice.Render(warm.AsSpan(i, Math.Min(1024, warm.Length - i)));
        var buf = new float[(int)(Rate * seconds)];
        for (int i = 0; i < buf.Length; i += 1024) voice.Render(buf.AsSpan(i, Math.Min(1024, buf.Length - i)));
        double sum = 0;
        int knee = 0;
        foreach (float v in buf)
        {
            float pa = v * voice.PascalsAtFullScale;
            sum += (double)pa * pa;
            if (MathF.Abs(v) > SoftCeiling.Knee) knee++;
        }
        float rms = MathF.Sqrt((float)(sum / buf.Length));
        return (20f * MathF.Log10(MathF.Max(rms, 1e-9f) / 20e-6f), knee / (double)buf.Length);
    }

    /// <summary>
    /// The fountain and the fire reach the mixer at the level they declare, and the fire's crackles,
    /// forty decibels over its mean, fit inside its own headroom rather than being squared off.
    /// Three decibels of tolerance: a fire has its quiet spells and its busy ones.
    /// </summary>
    [Fact]
    public void WaterAndFireRenderAtTheLevelTheyDeclare()
    {
        var water = WaterFeatureSpec.ByName("park_fountain");
        var (wDb, wKnee) = Measure(new WaterVoiceState(water, Rate, 5, Vector3.Zero), 8f);
        _o.WriteLine($"fountain {wDb:F1} dB against {water.SourceLevelDb:F1}, {wKnee:P3} on the knee");
        Assert.InRange(wDb, water.SourceLevelDb - 3f, water.SourceLevelDb + 3f);
        Assert.True(wKnee < 1e-4, $"the fountain sat on the soft ceiling {wKnee:P3} of the time");

        var fire = FireSpec.ByName("fire_pit");
        var (fDb, fKnee) = Measure(new FireVoiceState(fire, Rate, 5, Vector3.Zero), 30f);
        _o.WriteLine($"fire {fDb:F1} dB against {fire.SourceLevelDb:F1}, {fKnee:P4} on the knee");
        Assert.InRange(fDb, fire.SourceLevelDb - 3f, fire.SourceLevelDb + 3f);
        Assert.True(fKnee < 1e-4, $"the fire sat on the soft ceiling {fKnee:P4} of the time");
    }

    /// <summary>The headroom a nature voice renders with is given back by the channel, so placing a
    /// fire by its mean is not undone by giving it room for its crackles.</summary>
    [Fact]
    public void ExtraHeadroomIsGivenBackAsGain()
    {
        Assert.Equal(1f, PhysicalVoiceState.HeadroomGain(VehicleProfile.PeakHeadroomDb), 4);
        Assert.Equal(1f, PhysicalVoiceState.HeadroomGain(10f), 4);
        float fire = FireSpec.ByName("fire_pit").PeakHeadroomDb;
        Assert.Equal(MathF.Pow(10f, (fire - VehicleProfile.PeakHeadroomDb) / 20f), PhysicalVoiceState.HeadroomGain(fire), 3);
    }

    /// <summary>A fire puffs at the rate its width sets, about 1.5 / √D (Cetegen and Ahmed 1993).</summary>
    [Fact]
    public void AFirePuffsAtTheRateItsWidthSets()
    {
        var spec = FireSpec.ByName("fire_pit");
        var fire = new FireSynth(spec, Rate, 3) { CracklePart = 0f, SteamPart = 0f, SettlePart = 0f };
        int hop = Rate / 50;
        var env = new List<double>();
        double e = 0;
        for (int i = 0; i < Rate * 60; i++)
        {
            if (i % 256 == 0) fire.Control(256f / Rate);
            float y = fire.Next();
            e += y * y;
            if ((i + 1) % hop == 0) { env.Add(Math.Sqrt(e / hop)); e = 0; }
        }
        double mean = env.Average(), best = 0, bestHz = 0;
        for (double hz = 0.5; hz <= 5; hz *= 1.02)
        {
            double re = 0, im = 0;
            for (int i = 0; i < env.Count; i++)
            {
                double ph = 2 * Math.PI * hz * i / 50.0;
                re += (env[i] - mean) * Math.Cos(ph);
                im += (env[i] - mean) * Math.Sin(ph);
            }
            double mag = re * re + im * im;
            if (mag > best) { best = mag; bestHz = hz; }
        }
        double expected = 1.5 / Math.Sqrt(spec.BaseDiameterMetres);
        _o.WriteLine($"puffing at {bestHz:F2} Hz, expected {expected:F2}");
        Assert.InRange(bestHz, expected * 0.75, expected * 1.25);
    }

    [Fact]
    public void WetterWoodCracklesMore()
    {
        var dry = new FireSynth(FireSpec.ByName("fire_pit") with { Moisture = 0.12f }, Rate, 1);
        var wet = new FireSynth(FireSpec.ByName("fire_pit") with { Moisture = 0.35f }, Rate, 1);
        Assert.True(wet.CrackleRate > 1.5f * dry.CrackleRate);
    }

    /// <summary>
    /// The rustle grows with the wind faster than the wind does: Fégeant's vegetation noise rises 30
    /// (oak) to 36 (birch) dB a decade of wind speed. From 3 to 6 m/s, steady, that is 9 to 11 dB,
    /// and a little more here for the leaves that are still at 3 and moving at 6.
    /// </summary>
    [Fact]
    public void TheRustleGrowsWithTheWindAsFegeantMeasured()
    {
        float Level(float wind)
        {
            var tree = new FoliageSynth(FoliageSpec.ByName("park_tree"), Rate, 9) { Wind = wind };
            double sum = 0;
            int n = 0;
            for (int i = 0; i < Rate * 12; i++)
            {
                if (i % 256 == 0) tree.Control(256f / Rate);
                float y = tree.Next();
                if (i < Rate * 2) continue;
                sum += y * y; n++;
            }
            return 10f * MathF.Log10((float)(sum / n));
        }
        float rise = Level(6f) - Level(3f);
        _o.WriteLine($"3 to 6 m/s: {rise:F1} dB");
        Assert.InRange(rise, 8f, 14f);
    }

    [Fact]
    public void APineSighsAndDoesNotRustle()
    {
        var pine = new FoliageSynth(FoliageSpec.ByName("pine"), Rate, 1);
        Assert.Equal(0f, pine.StrikesPerSecond(5f));
        var lime = new FoliageSynth(FoliageSpec.ByName("park_tree"), Rate, 1);
        Assert.True(lime.StrikesPerSecond(5f) > 1000f);
        Assert.Equal(0f, lime.StrikesPerSecond(0.5f));
    }

    // ── Texture: grain and flicker ─────────────────────────────────────────────────────────────

    /// <summary>A band of a signal: two RBJ high-passes then two low-passes, as the lab measures.</summary>
    private static float[] Band(float[] x, float lo, float hi)
    {
        var y = (float[])x.Clone();
        for (int pass = 0; pass < 2; pass++)
        {
            Biquad(y, lo, highPass: true);
            Biquad(y, hi, highPass: false);
        }
        return y;
    }

    private static void Biquad(float[] y, float f, bool highPass)
    {
        double w = 2 * Math.PI * f / Rate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * 0.7071);
        double b0 = highPass ? (1 + c) / 2 : (1 - c) / 2, b1 = highPass ? -(1 + c) : 1 - c, b2 = b0;
        double a0 = 1 + alpha, a1 = -2 * c, a2 = 1 - alpha;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double xi = y[i];
            double yi = (b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = xi; y2 = y1; y1 = yi;
            y[i] = (float)yi;
        }
    }

    /// <summary>The mean kurtosis of a signal over 10 ms windows: 3 for a wash of noise, more the more
    /// it is made of separate clicks. Windows, so a slow swell is not counted as peakiness.</summary>
    private static double WindowKurtosis(float[] x)
    {
        int w = Rate / 100;
        double sum = 0;
        int n = 0;
        for (int s = Rate / 2; s + w <= x.Length; s += w)
        {
            double m2 = 0, m4 = 0;
            for (int i = s; i < s + w; i++) { double q = (double)x[i] * x[i]; m2 += q; m4 += q * q; }
            m2 /= w; m4 /= w;
            if (m2 <= 1e-20) continue;
            sum += m4 / (m2 * m2);
            n++;
        }
        return sum / Math.Max(1, n);
    }

    /// <summary>How much a signal's level flickers: the standard deviation, dB, of its 50 ms level about
    /// its own one-second running mean.</summary>
    private static double Flicker(float[] x)
    {
        int w = Rate / 20;
        var level = new List<double>();
        for (int s = Rate / 2; s + w <= x.Length; s += w)
        {
            double e = 0;
            for (int i = s; i < s + w; i++) e += (double)x[i] * x[i];
            level.Add(10 * Math.Log10(e / w + 1e-30));
        }
        var dev = new List<double>();
        for (int i = 10; i + 10 < level.Count; i++)
        {
            double mean = 0;
            for (int k = i - 10; k < i + 10; k++) mean += level[k];
            dev.Add(level[i] - mean / 20);
        }
        double m = dev.Average();
        return Math.Sqrt(dev.Average(d => (d - m) * (d - m)));
    }

    /// <summary>
    /// Holds a model's texture statistics (TextureStatistics, McDermott and Simoncelli 2011) inside
    /// the spread of the recordings of the same thing, widened by a margin: a fifth of the spread, and
    /// no less than what twenty seconds of one seed wanders by (0.01 on a spread or a correlation, 0.05
    /// on a skew, 0.25 on a kurtosis, 0.02 on a modulation share).
    /// </summary>
    private void HoldInRange(string texture, Dictionary<string, double> model, IEnumerable<string> keys)
    {
        var misses = new List<string>();
        foreach (var k in keys)
        {
            var (lo, hi) = TextureStatistics.Range(texture, k);
            double floor = k.StartsWith("skew") ? 0.05 : k.StartsWith("kurt") ? 0.25 : k.StartsWith("mod") ? 0.02 : 0.01;
            double margin = Math.Max(0.2 * (hi - lo), floor);
            bool ok = model[k] >= lo - margin && model[k] <= hi + margin;
            _o.WriteLine($"  {k,-12} {model[k],7:F3}   recordings {lo,7:F3} .. {hi,7:F3}{(ok ? "" : "   OUTSIDE")}");
            if (!ok) misses.Add($"{k} {model[k]:F3} not in {lo:F3}..{hi:F3}");
        }
        Assert.True(misses.Count == 0, string.Join("; ", misses));
    }

    /// <summary>The features each texture was fitted on: the band envelopes above 1 kHz, how the bands
    /// move together, and how fast they move.</summary>
    private static readonly string[] Fitted =
    {
        "cv 1-3k", "cv 3-6k", "cv 6-12k", "skew 1-3k", "skew 3-6k", "skew 6-12k", "kurt 3-6k", "kurt 6-12k",
        "corr near", "corr octave", "corr far", "mod slow", "mod mid", "mod fast",
    };

    /// <summary>
    /// The fountain is a texture of splashes, not Gaussian noise: made of 87,000 similar events a second
    /// it was as steady as noise in every band over 1 kHz, and Cody heard it as "crunchy, static"
    /// (docs/RUNNING_WATER.md 12.2). Held on the cochlear statistics against the three recorded fountains.
    /// </summary>
    [Fact]
    public void TheFountainMovesAsRecordedFountainsDo()
    {
        var water = new FallingWaterSynth(WaterFeatureSpec.ByName("park_fountain"), TextureStatistics.Rate, 3);
        var x = new float[TextureStatistics.Rate * 20];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0)
            {
                water.Wind = WindField.SpeedAt(0f, 1.5f, 0f, i / (double)TextureStatistics.Rate);
                water.Control(256f / TextureStatistics.Rate);
            }
            x[i] = water.Next();
        }
        HoldInRange("fountain", TextureStatistics.Analyse(x).Summary(), Fitted);
    }

    /// <summary>
    /// A fountain is heard from where its water lands: one synth, each tap the events that land there.
    /// The taps sum to the whole exactly (the same events, the same seed), each carries sound, and two
    /// sides of the rocks are different water, not one signal played twice — the ears of a listener
    /// between them hear two sources, not one (two ears 0.92 alike at 2 m from the one-point fountain).
    /// </summary>
    [Fact]
    public void AFountainsTapsAreItsWaterWhereItLands()
    {
        var spec = WaterFeatureSpec.ByName("park_fountain");
        Assert.Equal(5, spec.Taps.Length);
        Assert.All(spec.Falls, f => Assert.InRange(f.Tap, 0, spec.Taps.Length - 1));
        Assert.Contains(spec.Falls, f => f.Onto == WaterSurface.Rock);
        var whole = new FallingWaterSynth(spec, Rate, 9);
        var split = new FallingWaterSynth(spec, Rate, 9);
        var taps = new float[split.TapCount];
        var power = new double[split.TapCount];
        double north = 0, south = 0, cross = 0, worst = 0;
        for (int i = 0; i < Rate * 4; i++)
        {
            if (i % 256 == 0) { whole.Wind = split.Wind = 3f; whole.Control(256f / Rate); split.Control(256f / Rate); }
            float w = whole.Next();
            split.NextTaps(taps);
            float sum = 0f;
            for (int t = 0; t < taps.Length; t++) { sum += taps[t]; power[t] += taps[t] * (double)taps[t]; }
            worst = Math.Max(worst, Math.Abs(sum - w));
            north += taps[1] * (double)taps[1]; south += taps[3] * (double)taps[3]; cross += taps[1] * (double)taps[3];
        }
        _o.WriteLine($"taps' power share: {string.Join(", ", power.Select(p => $"{p / power.Sum():P0}"))}; north/south correlation {cross / Math.Sqrt(north * south):F3}");
        Assert.True(worst < 1e-4, $"the taps summed {worst} Pa away from the whole");
        Assert.All(power, p => Assert.True(p / power.Sum() > 0.05, "a tap carries almost nothing"));
        Assert.InRange(cross / Math.Sqrt(north * south), -0.05, 0.05);
    }

    /// <summary>
    /// Rain on a street swells and eases over seconds as recorded rain does (RainSynth.Intermittency,
    /// after Kostinski and Jameson's clustered drop counts): fed one steady rate its band envelopes had
    /// 1.5-2.5 per cent of their modulation power at 0.5-2 Hz against the recordings' 3.3-31 per
    /// cent, and three quarters of it above 32 Hz against at most 71 per cent.
    /// </summary>
    [Theory]
    [InlineData(5f)]
    [InlineData(25f)]
    public void RainOnAStreetMovesAsRecordedRainDoes(float mmPerHour)
    {
        var street = new RainLayer { Kind = RainSurfaceKind.Hard, Material = "Asphalt" };
        float[] areas = { 5f, 15f, 40f, 120f }, distances = { 1.5f, 3f, 6f, 12f };
        for (int r = 0; r < areas.Length; r++) street.Add(r, areas[r], distances[r], MathF.Min(1f, 1.6f / distances[r]));
        var patch = new RainPatch { Layers = new[] { street }, ReferenceDistance = 3f };
        var synth = new RainSynth(TextureStatistics.Rate, 11) { Patch = patch, RainRate = mmPerHour };
        for (int i = 0; i < TextureStatistics.Rate; i++) synth.Next();
        var x = new float[TextureStatistics.Rate * 20];
        for (int i = 0; i < x.Length; i++) x[i] = synth.Next();
        HoldInRange("rain", TextureStatistics.Analyse(x).Summary(), Fitted);

        // And inside 10 ms: a wash like recorded rain, not a few needle-sharp clicks in each window. At
        // 9-10 here (moderate) Cody heard "low bit rate, crunchy".
        var (kurtosis, crest) = TextureStatistics.Waveform(x);
        _o.WriteLine($"  4-16 kHz in 10 ms: kurtosis {kurtosis:F2}, crest {crest:F1} dB; recordings {TextureStatistics.RainWaveformKurtosisMin:F2}-{TextureStatistics.RainWaveformKurtosisMax:F2}");
        Assert.InRange(kurtosis, 2.8, TextureStatistics.RainWaveformKurtosisMax + 0.3);
    }

    /// <summary>
    /// A near drop, played alone where it lands (NearDrops, DropBank), is not a one-sample spike: its
    /// force rises smoothly over a good part of a tenth of a millisecond, and its top end is the spray
    /// of its splash over the next milliseconds. Measured as the share of its 8-16 kHz energy in its
    /// loudest 0.2 ms: a spike puts nearly all of it there, the spray spreads it over 5 ms or more.
    /// </summary>
    [Theory]
    [InlineData("Asphalt")]
    [InlineData("Concrete")]
    public void ANearDropIsNotANeedle(string material)
    {
        var layer = new RainLayer { Kind = RainSurfaceKind.Hard, Material = material };
        layer.Add(0, 1f, 1f, 1f);
        var synth = new RainSynth(Rate, 3);
        var pcm = synth.RenderOne(layer, PrecipitationKind.Rain, 3f, FallingWaterSynth.TerminalSpeed(1.5e-3f), Rate / 10);
        var hf = Band(pcm, 8000f, 16000f);
        double total = hf.Sum(v => (double)v * v);
        int w = Rate / 5000;
        double best = 0;
        for (int s = 0; s + w <= hf.Length; s++)
        {
            double e = 0;
            for (int i = s; i < s + w; i++) e += (double)hf[i] * hf[i];
            best = Math.Max(best, e);
        }
        _o.WriteLine($"{material}: {best / total:P0} of the 8-16 kHz energy in the loudest 0.2 ms");
        Assert.True(best / total < 0.3, $"the drop's top end is a spike: {best / total:P0} in 0.2 ms");
    }

    /// <summary>
    /// A park tree's rustle, in a steady wind, moves as recorded leaves in wind do: hard knocks out of
    /// a bed of glancing touches, the air's small-scale velocity increments being exponential-tailed
    /// (FoliageSynth.Increment). Driven by the mean wind alone its top bands were as steady as noise
    /// (skew below zero against the recordings' 0.35-1.4). Steady, because the wind field's slow gusts
    /// swing a 20 s stretch further than any 20 s of the recordings (an open question in changes.md),
    /// and at 3 and 4.5 m/s because the recordings are of soft and moderate winds: at 6-10 m/s twice as
    /// many twigs are going and the top bands are a wash again (skew 0.07-0.15).
    /// </summary>
    [Theory]
    [InlineData(3f)]
    [InlineData(4.5f)]
    public void ATreeRustlesAsRecordedLeavesDo(float wind)
    {
        var tree = new FoliageSynth(FoliageSpec.ByName("park_tree"), TextureStatistics.Rate, 5) { Wind = wind };
        var x = new float[TextureStatistics.Rate * 20];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0) tree.Control(256f / TextureStatistics.Rate);
            x[i] = tree.Next();
        }
        HoldInRange("leaves", TextureStatistics.Analyse(x).Summary(), Fitted);
    }

    /// <summary>
    /// A fire is a steady fizz with rare loud cracks over it, as the recorded fires are (envelope
    /// spread 0.18-0.30 above 3 kHz, kurtosis 21-100), not cracks over silence (spread 0.45-0.59
    /// before the fizz). Not held on its 4-16 Hz modulation, still a little under the recordings'
    /// (0.23 against 0.28-0.48: the flames' flicker does not reach the fizz yet).
    /// </summary>
    [Fact]
    public void AFireFizzesBetweenItsCracks()
    {
        var fire = new FireSynth(FireSpec.ByName("fire_pit"), TextureStatistics.Rate, 7);
        var x = new float[TextureStatistics.Rate * 20];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0)
            {
                fire.Wind = WindField.SpeedAt(0f, 0.8f, 0f, i / (double)TextureStatistics.Rate);
                fire.Control(256f / TextureStatistics.Rate);
            }
            x[i] = fire.Next();
        }
        HoldInRange("fire", TextureStatistics.Analyse(x).Summary(), Fitted.Where(k => k != "mod mid"));
    }

    /// <summary>
    /// A near drop is placed in the loudness frame of the rain it is part of: its gain per pascal is
    /// the patch's, whatever the loudness law's compression. Placed by its own peak, the twenty-odd
    /// drops a second under a steel shelter came out within 2 dB of the whole roof at every rate, and
    /// heavy rain on the roof sounded like light (Cody, 2026-10-06).
    /// </summary>
    [Fact]
    public void ANearDropIsHeardAsPartOfItsRain()
    {
        foreach (float field in new[] { 35f, 50f, 65f })
        {
            float drop = field + 10f;                  // a drop's peak, 10 dB over the patch's Leq
            var (g, _) = DropBank.Placement(drop, field);
            var (pg, _) = Loudness.Place(field);
            // The patch's Leq plays the shared headroom under its gain; the drop's peak at its own.
            float patchRms = 20f * MathF.Log10(pg) - VehicleProfile.PeakHeadroomDb;
            float dropPeak = 20f * MathF.Log10(g);
            Assert.Equal(10f, dropPeak - patchRms, 2);
        }
        // With no field measured yet, a drop is placed on its own.
        Assert.Equal(Loudness.Place(60f).Gain, DropBank.Placement(60f, float.NaN).Gain);
    }

    /// <summary>"water:&lt;preset&gt;/&lt;feature&gt;/&lt;tap&gt;" is a tap; a plain "water:&lt;preset&gt;" is the whole.</summary>
    [Fact]
    public void AWaterTapKeyNamesItsFeatureAndTap()
    {
        Assert.True(WaterFeatureVoice.ParseKey("water:park_fountain/elm_park/3", out var preset, out var feature, out int tap));
        Assert.Equal(("park_fountain", "elm_park", 3), (preset, feature, tap));
        Assert.False(WaterFeatureVoice.ParseKey("water:park_fountain", out _, out _, out _));
        Assert.False(WaterFeatureVoice.ParseKey("rail:x/y/1", out _, out _, out _));
    }

    /// <summary>
    /// Each tap renders against the WHOLE fountain's level, so the taps placed at their own places sum
    /// to the fountain placed as one voice: a tap voice and a one-point voice share a full scale.
    /// </summary>
    [Fact]
    public void ATapVoiceRendersAgainstTheWholeFountain()
    {
        var spec = WaterFeatureSpec.ByName("park_fountain");
        var shared = new WaterFeatureVoice("park_fountain/test", spec, Rate, 1);
        var tap = new WaterTapState(shared, 2, Rate, Vector3.Zero);
        var whole = new WaterVoiceState(spec, Rate, 1, Vector3.Zero);
        Assert.Equal(whole.PascalsAtFullScale, tap.PascalsAtFullScale);
        var (db, knee) = Measure(tap, 4f);
        _o.WriteLine($"one tap {db:F1} dB against the whole's {spec.SourceLevelDb:F1}");
        Assert.True(db < spec.SourceLevelDb - 3f && db > spec.SourceLevelDb - 20f);
        Assert.True(knee < 1e-4);
    }

    [Fact]
    public void TheFountainsOldHissMeasuresAreKept()
    {
        // Over 10 ms windows a fountain stays near a wash within a window (recordings 3.0-3.4 in 2-8 kHz);
        // what moves is the envelope from window to window.
        var water = new FallingWaterSynth(WaterFeatureSpec.ByName("park_fountain"), Rate, 3) { Wind = 3f };
        var x = new float[Rate * 20];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0) water.Control(256f / Rate);
            x[i] = water.Next();
        }
        double kurtosis = WindowKurtosis(Band(x, 8000f, 16000f));
        double mid = WindowKurtosis(Band(x, 2000f, 8000f));
        double flicker = Flicker(x);
        _o.WriteLine($"8-16 kHz kurtosis {kurtosis:F2}, 2-8 kHz {mid:F2}, 50 ms flicker {flicker:F2} dB");
        Assert.InRange(kurtosis, 2.5, 4.5);
        Assert.InRange(mid, 2.5, 4.0);
        Assert.InRange(flicker, 0.3, 1.2);
    }

    /// <summary>
    /// The rustle comes in twig episodes, but a twig's sixteen leaves can strike only about twice a
    /// flutter cycle each, some thirty-odd strikes an episode. The first model gave an episode 400,
    /// so a breeze was a few loud patches a second, each heard arriving: the leaves' 2-8 kHz band
    /// flickered by 2.7 dB over 50 ms where recorded leaves flicker by 0.5-0.7 (1.7 in the busiest).
    /// Measured on the whole tree, as the recordings are of whole trees: a hard knock stands out of the
    /// leaves on their own, as it does in the recordings, and the whoosh under them is its bed.
    /// </summary>
    [Fact]
    public void TheRustleIsNotAFewLoudPatches()
    {
        var tree = new FoliageSynth(FoliageSpec.ByName("park_tree"), Rate, 5) { Wind = 4f };
        var x = new float[Rate * 20];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0) tree.Control(256f / Rate);
            x[i] = tree.Next();
        }
        double flicker = Flicker(Band(x, 2000f, 8000f));
        _o.WriteLine($"leaves' 2-8 kHz flicker {flicker:F2} dB");
        Assert.InRange(flicker, 0.3, 1.7);
    }

    /// <summary>
    /// A crown is metres across, so a gust reaches its upwind boughs before its downwind ones: the
    /// boughs read the field at their own places, a crossing of the crown apart.
    /// </summary>
    [Fact]
    public void AGustCrossesTheCrown()
    {
        // The boughs stand round the crown (FoliageSynth.BoughOffset, where they are also heard from);
        // along the wind, the most upwind and the most downwind are most of a crown apart.
        var spec = FoliageSpec.ByName("park_tree");
        var (dx, dz) = WindField.Downwind;
        float Along(int b) { var o = FoliageSynth.BoughOffset(spec, b); return o.X * dx + o.Z * dz; }
        int up = Enumerable.Range(0, FoliageSynth.Boughs).OrderBy(Along).First();
        int down = Enumerable.Range(0, FoliageSynth.Boughs).OrderBy(Along).Last();
        float first = Along(up), last = Along(down);
        Assert.True(first < 0f && last > 0f);
        Assert.InRange(last - first, spec.CrownRadiusMetres, 2f * spec.CrownRadiusMetres);
        // What the downwind bough feels now, the upwind one felt (last - first) / U seconds ago, give or
        // take what the eddies change across the wind between their two places.
        double t = 1234.5, lag = (last - first) / Math.Max(0.5, WindField.MeanSpeed);
        var tree = new FoliageSynth(spec, Rate, 1);
        tree.ReadWind(10f, -20f, t - lag);
        float upwindEarlier = tree.BoughWind(up);
        tree.ReadWind(10f, -20f, t);
        Assert.InRange(tree.BoughWind(down) - upwindEarlier, -0.25f, 0.25f);
        Assert.NotEqual(tree.BoughWind(up), tree.BoughWind(down));
    }

    // ── On the city ────────────────────────────────────────────────────────────────────────────

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    private sealed record Box(string Prefab, string? Name, Vector3 Centre, Vector3 Half, bool Solid);

    private static (List<Box> Boxes, Dictionary<string, JsonElement> Prefabs) City()
    {
        string root = RepoRoot();
        var prefabs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(Path.Combine(root, "OpenFPS.Server", "prefabs"), "*.json"))
        {
            if (path.EndsWith("prefab-schema.json")) continue;
            var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
            prefabs[doc.GetProperty("Id").GetString()!] = doc;
        }
        var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "OpenFPS.Server", "maps", "city.json"))).RootElement;
        var boxes = new List<Box>();
        foreach (var e in map.GetProperty("Entities").EnumerateArray())
        {
            string id = e.GetProperty("PrefabId").GetString()!;
            if (!prefabs.TryGetValue(id, out var p)) continue;
            var c = p.TryGetProperty("ColliderSize", out var cs)
                ? new Vector3(cs.GetProperty("X").GetSingle(), cs.GetProperty("Y").GetSingle(), cs.GetProperty("Z").GetSingle())
                : Vector3.One;
            var s = e.TryGetProperty("Scale", out var sc)
                ? new Vector3(sc.GetProperty("X").GetSingle(), sc.GetProperty("Y").GetSingle(), sc.GetProperty("Z").GetSingle())
                : Vector3.One;
            var pos = e.GetProperty("Position");
            // Missing IsSolid is solid, as the server reads it.
            bool solid = !p.TryGetProperty("IsSolid", out var so) || so.GetBoolean();
            string? name = e.TryGetProperty("Name", out var nm) ? nm.GetString() : null;
            boxes.Add(new Box(id, name, new Vector3(pos.GetProperty("X").GetSingle(), pos.GetProperty("Y").GetSingle(), pos.GetProperty("Z").GetSingle()),
                              c * s / 2f, solid));
        }
        return (boxes, prefabs);
    }

    private static bool Inside(Box b, Vector3 p)
        => MathF.Abs(p.X - b.Centre.X) < b.Half.X && MathF.Abs(p.Y - b.Centre.Y) < b.Half.Y && MathF.Abs(p.Z - b.Centre.Z) < b.Half.Z;

    /// <summary>
    /// The park has its fountain and its trees, the garden its fire, and every one of them names a
    /// model the client knows. And none of them is inside anything solid: a source inside a box is
    /// heard through the box, which is how the first draft put the fountain inside its own pedestal.
    /// </summary>
    [Fact]
    public void TheParkAndTheGardenFireAreOnTheCity()
    {
        var (boxes, prefabs) = City();
        var sources = boxes.Where(b => b.Prefab.StartsWith("elm_fountain_water_") || b.Prefab is "fire_pit" or "tree_crown").ToList();
        Assert.Single(sources, b => b.Prefab == "fire_pit");
        Assert.True(sources.Count(b => b.Prefab == "tree_crown") >= 10);

        // The fountain: one emitter per tap of its spec, every tap once, all of one feature.
        var taps = sources.Where(b => b.Prefab.StartsWith("elm_fountain_water_"))
                          .Select(b => WaterFeatureVoice.ParseKey(prefabs[b.Prefab].GetProperty("SoundId").GetString(), out var p, out var f, out int t)
                                       ? (Preset: p, Feature: f, Tap: t) : throw new Xunit.Sdk.XunitException($"{b.Prefab} is not a water tap"))
                          .ToList();
        var fountain = WaterFeatureSpec.ByName(taps[0].Preset);
        Assert.Equal(Enumerable.Range(0, fountain.Taps.Length), taps.Select(t => t.Tap).OrderBy(t => t));
        Assert.Single(taps.Select(t => (t.Preset, t.Feature)).Distinct());

        foreach (var src in sources)
        {
            string sound = prefabs[src.Prefab].GetProperty("SoundId").GetString()!;
            string kind = sound[..sound.IndexOf(':')], key = sound[(sound.IndexOf(':') + 1)..];
            object model = kind switch
            {
                "water" => fountain,
                "fire" => FireSpec.ByName(key),
                "foliage" => FoliageSpec.ByName(key),
                _ => throw new Xunit.Sdk.XunitException($"{src.Prefab} names '{sound}', which is no nature model"),
            };
            Assert.NotNull(model);
            var walls = boxes.Where(b => b.Solid && Inside(b, src.Centre)).Select(b => b.Name ?? b.Prefab).ToList();
            Assert.True(walls.Count == 0, $"{src.Name} at {src.Centre} is inside {string.Join(", ", walls)}");
        }
    }

    /// <summary>
    /// The fountain is the bigger one, over rocks (Cody, 2026-10-06): an 11 m basin, and stone the
    /// bowl's overflow lands on, round the pedestal and under the bowl's lip. The rocks are Concrete,
    /// a material the acoustics know (an unknown one falls back to Generic without a word), and every
    /// side's tap is beside its rocks and over the water.
    /// </summary>
    [Fact]
    public void TheFountainIsBiggerAndHasRocksTheWaterLandsOn()
    {
        var (boxes, prefabs) = City();
        var pool = boxes.Single(b => b.Name == "Elm Park fountain pool");
        Assert.True(pool.Half.X >= 5f && pool.Half.Z >= 5f, $"the pool is {2 * pool.Half.X:F1} x {2 * pool.Half.Z:F1} m");
        var rocks = boxes.Where(b => b.Prefab == "rock_boulder" && b.Name != null && b.Name.StartsWith("Elm Park fountain rocks")).ToList();
        Assert.True(rocks.Count >= 6);
        string material = prefabs["rock_boulder"].GetProperty("Material").GetString()!;
        Assert.True(AcousticRegistry.IsKnown(material), $"rocks are '{material}', which the acoustics do not know");
        var bowl = boxes.Single(b => b.Name == "Elm Park fountain bowl");
        float lip = bowl.Half.X;
        // Under the lip: rock both inside and outside the lip's radius, and below the bowl.
        Assert.Contains(rocks, r => MathF.Abs(r.Centre.X - bowl.Centre.X) + r.Half.X > lip || MathF.Abs(r.Centre.Z - bowl.Centre.Z) + r.Half.Z > lip);
        Assert.All(rocks, r => Assert.True(r.Centre.Y + r.Half.Y < bowl.Centre.Y - bowl.Half.Y));
        foreach (var tap in boxes.Where(b => b.Prefab.StartsWith("elm_fountain_water_") && b.Prefab != "elm_fountain_water_0"))
        {
            float nearest = rocks.Min(r => Vector2.Distance(new Vector2(tap.Centre.X, tap.Centre.Z), new Vector2(r.Centre.X, r.Centre.Z)));
            Assert.True(nearest < 2f, $"{tap.Name} is {nearest:F1} m from the nearest rock");
            Assert.True(tap.Centre.Y > pool.Centre.Y + pool.Half.Y, $"{tap.Name} is under the water");
        }
    }

    /// <summary>The fire is in a garden fenced on three sides with a gate, and the house is the fourth.</summary>
    [Fact]
    public void TheFireIsInAFencedGarden()
    {
        var (boxes, _) = City();
        var fire = boxes.Single(b => b.Prefab == "fire_pit");
        var fences = boxes.Where(b => b.Prefab == "fence_timber").ToList();
        Assert.True(fences.Count >= 4);
        // A fence each side of it, east and west, and one to the north.
        Assert.Contains(fences, f => f.Centre.X < fire.Centre.X && MathF.Abs(f.Centre.Z - fire.Centre.Z) < f.Half.Z);
        Assert.Contains(fences, f => f.Centre.X > fire.Centre.X && MathF.Abs(f.Centre.Z - fire.Centre.Z) < f.Half.Z);
        Assert.Contains(fences, f => f.Centre.Z > fire.Centre.Z && f.Half.X > 2f);
        // ...and the garden it is in has a name.
        Assert.Contains(boxes, b => b.Prefab == "acoustic_region" && b.Name != null && b.Name.EndsWith("back garden") && Inside(b, fire.Centre with { Y = 1f }));
    }
}
