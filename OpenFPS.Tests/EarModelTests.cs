using System;
using OpenFPS.Common;
using OpenFPS.Common.Hearing;
using System.Numerics;
using OpenFPS.Client.AudioEngine.Core;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The ear model (docs/EAR_MODEL.md): ISO 532-1 loudness, ISO 226:2023 contours, the band analyser,
/// the loudness law in loudness units, and the compensation shelves. Checked against the standards'
/// own worked values where they publish them.
/// </summary>
[Collection(nameof(LevelCompressionSetting))]
public class EarModelTests
{
    private static T With<T>(float compression, Func<T> f)
    {
        float was = Loudness.DynamicRangeCompression;
        bool on = EarModel.Enabled;
        try { Loudness.DynamicRangeCompression = compression; EarModel.Enabled = true; return f(); }
        finally { Loudness.DynamicRangeCompression = was; EarModel.Enabled = on; }
    }

    // ── ISO 532-1 ────────────────────────────────────────────────────────────────────────────

    /// <summary>ISO 532-1:2017 annex B.2, test signal 1: a measured machinery spectrum in one-third
    /// octaves, free field. The standard's result is 83.296 sone; compliance is 5 %.</summary>
    [Fact]
    public void Iso532AnnexB2TestSignal1()
    {
        float[] levels = { -60, -60, 78, 79, 89, 72, 80, 89, 75, 87, 85, 79, 86, 80, 71, 70, 72, 71, 72, 74, 69, 65, 67, 77, 68, 58, 45, 30 };
        float n = ZwickerLoudness.Sones(levels);
        Assert.InRange(n, 83.296f * 0.99f, 83.296f * 1.01f);
    }

    /// <summary>One sone is the loudness of a 1 kHz tone at 40 dB; the method gives it within a few
    /// per cent from one-third-octave levels (the tone in its band alone).</summary>
    [Fact]
    public void AToneAt40DbIsAboutOneSone()
    {
        var levels = Silent();
        levels[16] = 40f;
        Assert.InRange(ZwickerLoudness.Sones(levels), 0.85f, 1.05f);
    }

    /// <summary>Ten phon is twice the loudness above 40 phon, by definition of the sone.</summary>
    [Fact]
    public void PhonsAndSonesAreInverse()
    {
        foreach (float p in new[] { 10f, 30f, 40f, 63f, 90f, 110f })
            Assert.Equal(p, ZwickerLoudness.Phons(ZwickerLoudness.SonesFromPhons(p)), 2);
        Assert.Equal(50f, ZwickerLoudness.Phons(2f), 3);
    }

    [Fact]
    public void LoudnessNeverFallsWithLevel()
    {
        foreach (var t in new[] { Timbre.Speech, Tone(1000f), BassHeavy() })
        {
            float prev = -1f;
            for (float l = -10f; l <= 130f; l += 0.5f)
            {
                float n = t.Sones(l);
                Assert.True(n >= prev - 1e-4f, $"{t}: {n} sone at {l} dB, {prev} at {l - 0.5f}");
                prev = n;
            }
        }
    }

    /// <summary>Loudness summation: broadband sound is much louder than a tone of the same level.
    /// Speech at 70 dB is about 83 phon; a 1 kHz tone at 70 dB about 68-70.</summary>
    [Fact]
    public void SpeechIsLouderThanAToneOfTheSameLevel()
    {
        Assert.InRange(Timbre.Speech.Phons(70f), 82f, 85f);
        Assert.InRange(Tone(1000f).Phons(70f), 66f, 71f);
        // ANSI S3.5's normal effort is 62.35 dB at a metre: the reference adds up to it.
        double total = 0;
        foreach (float b in Timbre.Speech.ShapeDb) total += Math.Pow(10, b / 10.0);
        Assert.Equal(0.0, 10 * Math.Log10(total), 2);
    }

    // ── ISO 226:2023 ─────────────────────────────────────────────────────────────────────────

    /// <summary>Formula (1) gives a 1 kHz tone its own level at every loudness level (the phon).</summary>
    [Fact]
    public void Iso226AtOneKilohertzIsTheIdentity()
    {
        for (float p = 20f; p <= 90f; p += 10f) Assert.Equal(p, EqualLoudness.SplAt(17, p), 2);
    }

    /// <summary>At 2.4 phon (the threshold at 1 kHz) formula (1) returns table 1's threshold row.</summary>
    [Fact]
    public void Iso226ThresholdRow()
    {
        for (int i = 0; i < 29; i++) Assert.Equal(EqualLoudness.ThresholdAt(i), EqualLoudness.SplAt(i, 2.4f), 2);
        Assert.Equal(78.1f, EqualLoudness.ThresholdAt(0), 3);    // 20 Hz: 0.4 dB under 2003's, from ISO 389-7:2019
    }

    /// <summary>Formula (2) inverts formula (1) at every frequency.</summary>
    [Fact]
    public void Iso226FormulaTwoInvertsFormulaOne()
    {
        for (int i = 0; i < 29; i++)
            foreach (float p in new[] { 20f, 40f, 60f, 80f, 90f })
                Assert.Equal(p, EqualLoudness.PhonAt(i, EqualLoudness.SplAt(i, p)), 2);
    }

    /// <summary>The 40-phon contour at a few frequencies, formula (1) with table 1 (dB SPL).</summary>
    [Theory]
    [InlineData(0, 99.7f)]     // 20 Hz
    [InlineData(5, 73.0f)]     // 63 Hz
    [InlineData(8, 60.4f)]     // 125 Hz
    [InlineData(14, 43.1f)]    // 500 Hz
    [InlineData(22, 35.5f)]    // 3.15 kHz
    [InlineData(27, 54.4f)]    // 10 kHz
    public void Iso226FortyPhonContour(int index, float expected)
        => Assert.InRange(EqualLoudness.SplAt(index, 40f), expected - 0.1f, expected + 0.1f);

    // ── The analyser ─────────────────────────────────────────────────────────────────────────

    /// <summary>ISO 532-1's test signal 3 is a 1 kHz tone at 60 dB, 4.019 sone through the standard's
    /// band filters. Measured here from samples, the tone leaks into its neighbouring bands as it does
    /// through those filters, and the loudness lands within the standard's 5 %.</summary>
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void AToneMeasuredFromSamplesMatchesTestSignal3(int rate)
    {
        float amp = 20e-6f * MathF.Pow(10f, 60f / 20f) * MathF.Sqrt(2f);
        var x = new float[rate * 2];
        for (int i = 0; i < x.Length; i++) x[i] = amp * MathF.Sin(2f * MathF.PI * 1000f * i / rate);
        var p = BandAnalyser.Measure(x, rate);
        var levels = new float[28];
        double total = 0;
        for (int b = 0; b < 28; b++) { levels[b] = (float)(10 * Math.Log10(p[b] / 4e-10 + 1e-30)); total += p[b]; }
        Assert.InRange(10 * Math.Log10(total / 4e-10), 59.5, 60.6);
        Assert.InRange(ZwickerLoudness.Sones(levels), 4.019f * 0.95f, 4.019f * 1.05f);
    }

    /// <summary>White noise has equal power per hertz: a third-octave band's power grows with its
    /// width, three decibels an octave, in the decimated low bands and the full-rate ones alike.</summary>
    [Fact]
    public void WhiteNoiseRisesThreeDecibelsAnOctave()
    {
        const int rate = 48000;
        var rng = new Random(5);
        var x = new float[rate * 30];   // 30 s: a low band is a few bins wide, so it needs many windows
        for (int i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var p = BandAnalyser.Measure(x, rate);
        // 31.5 -> 63 -> 125 -> 250 (low, decimated) -> 500 -> 1k -> 2k -> 4k -> 8k (full rate)
        int[] octave = { 1, 4, 7, 10, 13, 16, 19, 22, 25 };
        for (int k = 1; k < octave.Length; k++)
        {
            double step = 10 * Math.Log10(p[octave[k]] / p[octave[k - 1]]);
            Assert.True(step is > 2.0 and < 4.0,
                $"{ZwickerLoudness.CentresHz[octave[k - 1]]} -> {ZwickerLoudness.CentresHz[octave[k]]} Hz: {step:F2} dB; bands "
                + string.Join(" ", Array.ConvertAll(p, v => (10 * Math.Log10(v)).ToString("F1"))));
        }
    }

    // ── The law ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpeechIsPlacedExactlyAsBefore()
    {
        With(0.45f, () =>
        {
            foreach (float l in new[] { 30f, 60f, 70f, 94f, 120f, 160f })
            {
                Assert.Equal(Loudness.Place(l), Loudness.Place(l, Timbre.Speech));
                Assert.Equal(0f, Loudness.TimbreCorrectionDb(l, Timbre.Speech));
            }
            return 0;
        });
    }

    /// <summary>
    /// The law in loudness: a source exactly as loud as a speech line plays exactly as loud as that
    /// line does, at any setting, whatever its spectrum and whichever way its level is declared. The
    /// line at the pivot (70 dB declared at its reference) is the old law's pivot.
    /// </summary>
    [Theory]
    [InlineData(0.3f, 70f)]
    [InlineData(0.45f, 70f)]
    [InlineData(0.45f, 85f)]
    [InlineData(0.7f, 60f)]
    [InlineData(1f, 70f)]
    public void AsLoudAsALineIsPlayedAsLoudAsTheLine(float compression, float lineAtReference)
    {
        With(compression, () =>
        {
            float listening = EarModel.ListeningLevelDb;
            EarModel.ListeningLevelDb = EarModel.DefaultListeningLevelDb;
            try
            {
                float r = Loudness.MinReferenceDistance, toMetre = 20f * MathF.Log10(r);
                var (lg, lr) = Loudness.Place(lineAtReference + toMetre, Timbre.Speech);
                float linePlayed = EarModel.PlayedAtEarDb(20f * MathF.Log10(lg * lr), Loudness.ReferenceRmsDbfs, lr, lr, 0f);
                float lineLoudness = Timbre.Speech.Phons(linePlayed);
                // Physical voices (their declared level is their RMS) and a recording 20 dB under its full scale.
                foreach (var t in new[] { Tone(1000f), BassHeavy(), BassHeavy().WithGatedRms(-20f) })
                {
                    float realAtReference = t.LevelForPhons(Timbre.Speech.Phons(lineAtReference + Loudness.ReferenceRmsDbfs));
                    float declared = realAtReference - t.RealOffsetDb + toMetre;
                    var (gain, reference) = Loudness.Place(declared, t);
                    Assert.Equal(r, reference, 2);
                    float played = EarModel.PlayedAtEarDb(20f * MathF.Log10(gain * reference), t.DigitalRmsDb, reference, reference, 0f);
                    Assert.InRange(t.Phons(played) - lineLoudness, -0.3f, 0.3f);
                }
            }
            finally { EarModel.ListeningLevelDb = listening; }
            return 0;
        });
    }

    /// <summary>The designed playback: a normal voice a metre away plays at its own 62.35 dB, at the
    /// default listening level and the shipped /levels.</summary>
    [Fact]
    public void ANormalVoiceAtArmsLengthPlaysAsLoudAsLife()
    {
        With(Loudness.DefaultCompression, () =>
        {
            float declared = Speech.LevelDb(Speech.NormalDb);
            var (g, r) = Loudness.Place(declared);
            float played = EarModel.PlayedAtEarDb(20f * MathF.Log10(g * r), Loudness.ReferenceRmsDbfs, r, 1f, 0f);
            Assert.Equal(Speech.NormalDb, played, 1);
            Assert.InRange(Loudness.DesignFullScaleDb, 100f, 101.5f);
            return 0;
        });
    }

    // ── The engine lift ──────────────────────────────────────────────────────────────────────

    /// <summary>The hatchback idles 38 dB under its declared 99.5: the unweighted law wanted 20.9 dB of
    /// lift and its cap took 0.9 off. With the model there is no cap, and an idle's bass earns more.</summary>
    [Fact]
    public void TheLiftIsTheLawWithoutACap()
    {
        With(Loudness.DefaultCompression, () =>
        {
            float capped = OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState.LiftDb(61.5f, 99.5f);
            Assert.Equal(20f, capped, 2);
            var speechLike = Timbre.FromBandLevels(Timbre.Speech.ShapeDb, "speech-shaped engine");
            float plain = OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState.LiftDb(61.5f, speechLike, 99.5f);
            Assert.InRange(plain, 20.5f, 23f);
            float bass = OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState.LiftDb(61.5f, BassHeavy(), 99.5f);
            Assert.True(bass > plain + 3f, $"an idle's bass: {bass:F1} dB against {plain:F1}");
            // Silence has no place in the law: the caller holds the lift.
            Assert.True(float.IsNaN(OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState.LiftDb(-30f, BassHeavy(), 99.5f)));
            // Off is the unweighted lift with its cap, exactly.
            EarModel.Enabled = false;
            Assert.Equal(capped, OpenFPS.Client.AudioEngine.Fmod.EngineVoiceState.LiftDb(61.5f, BassHeavy(), 99.5f));
            return 0;
        });
    }

    [Fact]
    public void GatedRmsLeavesOutTheSilence()
    {
        const int rate = 48000;
        var x = new float[rate * 2];
        // Half a second of a 0.1-amplitude sine (-23 dBFS RMS), then silence.
        for (int i = 0; i < rate / 2; i++) x[i] = 0.1f * MathF.Sin(2f * MathF.PI * 440f * i / rate);
        Assert.InRange(Timbre.GatedRms(x, rate), -23.2f, -22.8f);
        Assert.True(float.IsNaN(Timbre.GatedRms(new float[1000], rate)));
    }

    /// <summary>Quieter-sounding things are lifted more: a tone of the same level as speech is placed
    /// louder than the unweighted law put it, and everything keeps its order of loudness.</summary>
    [Fact]
    public void AToneIsLiftedAndOrderIsKept()
    {
        With(0.45f, () =>
        {
            var tone = Tone(1000f);
            Assert.InRange(Loudness.TimbreCorrectionDb(70f, tone), 6f, 11f);
            float prev = float.NegativeInfinity;
            for (float l = 30f; l <= 120f; l += 5f)
            {
                float placed = Loudness.PlacedDb(l, tone);
                Assert.True(placed > prev, $"{l} dB placed at {placed}, not above {prev}");
                prev = placed;
            }
            return 0;
        });
    }

    [Fact]
    public void OffIsTheUnweightedLaw()
    {
        bool was = EarModel.Enabled;
        try
        {
            EarModel.Enabled = false;
            Assert.Equal(Loudness.Place(70f), Loudness.Place(70f, Tone(500f)));
            Assert.Equal(0f, Loudness.TimbreCorrectionDb(70f, BassHeavy()));
        }
        finally { EarModel.Enabled = was; }
    }

    // ── Ranking by loudness ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The voice budget ranks by how loud a voice is to the ear, not by the gain the law plays it at
    /// (Cody, 2026-10-07). A 65 dB rumble at 25 Hz and a 60 dB 1 kHz tone, both physical voices at the
    /// same distance and path: the law plays the rumble about 21 dB up because the ear hears so little
    /// of it, and the old rank (the played gain) put it above the tone. Heard, the tone is far louder,
    /// and it ranks first.
    /// </summary>
    [Fact]
    public void ARumbleRanksBelowAQuieterToneAtTheSameDistance()
    {
        With(0.45f, () =>
        {
            var rumble = Tone(25f);
            var tone = Tone(1000f);
            try
            {
                OpenFPS.Client.AudioEngine.Core.EarTimbres.Set("test:rumble", rumble);
                OpenFPS.Client.AudioEngine.Core.EarTimbres.Set("test:tone", tone);
                float correction = Loudness.TimbreCorrectionDb(65f, rumble);
                Assert.True(correction > Loudness.TimbreCorrectionDb(60f, tone) + 5f,
                    $"the law lifts the rumble more than the tone: {correction:F1} dB");
                foreach (float d in new[] { 2f, 10f, 40f })
                {
                    float r = VoiceManager.Audibility(Voice("test:rumble", 65f, new Vector3(d, 0, 0)), d, false);
                    float t = VoiceManager.Audibility(Voice("test:tone", 60f, new Vector3(0, 0, d)), d, false);
                    Assert.True(r > 0f && t > 0f);
                    Assert.True(t > r * 4f, $"at {d} m the 60 dB tone ({20 * MathF.Log10(t):F1}) should rank well above the 65 dB rumble ({20 * MathF.Log10(r):F1})");
                    // The played gains were the other way round: what the old rank read.
                    float playedR = Loudness.RenderedGain(Voice("test:rumble", 65f, default).Volume, 1f, 500f, d) * MathF.Pow(10f, correction / 20f);
                    float playedT = Loudness.RenderedGain(Voice("test:tone", 60f, default).Volume, 1f, 500f, d) * MathF.Pow(10f, Loudness.TimbreCorrectionDb(60f, tone) / 20f);
                    Assert.True(playedR > playedT, "the law should play the rumble at more gain than the tone");
                }
            }
            finally { OpenFPS.Client.AudioEngine.Core.EarTimbres.Clear(); }
            return 0;
        });
    }

    /// <summary>
    /// A speech line ranks at exactly the gain it plays at, as it always did, and so does a recording
    /// not measured yet; louder is always higher, through the threshold of hearing, so voices too far
    /// off to hear still keep their order instead of tying at zero sone.
    /// </summary>
    [Fact]
    public void HeardGainIsThePlayedGainForSpeechAndAlwaysRisesWithLevel()
    {
        With(0.45f, () =>
        {
            foreach (float g in new[] { 1e-5f, 0.001f, 0.1f, 1f })
            {
                Assert.Equal(g, Loudness.HeardGain(g, Timbre.Speech, physical: false));
                Assert.Equal(g, Loudness.HeardGain(g, null, physical: false));
            }
            // A physical voice's RMS sits 12 dB nearer full scale than a speech line's: as loud as a
            // speech line played 12 dB higher.
            Assert.InRange(20f * MathF.Log10(Loudness.HeardGain(0.01f, null, physical: true) / 0.01f), 11.5f, 12.5f);
            foreach (var t in new[] { Tone(25f), Tone(1000f), BassHeavy(), Timbre.Speech })
            {
                float prev = 0f;
                for (float db = -140f; db <= 10f; db += 0.5f)
                {
                    float h = Loudness.HeardGain(MathF.Pow(10f, db / 20f), t, physical: true);
                    Assert.True(h > prev, $"{t} at {db} dB: {h} not above {prev}");
                    prev = h;
                }
            }
            return 0;
        });
    }

    [Fact]
    public void WithTheEarModelOffTheRankIsThePlayedGain()
    {
        bool was = EarModel.Enabled;
        try
        {
            EarModel.Enabled = false;
            Assert.Equal(0.01f, Loudness.HeardGain(0.01f, Tone(25f), physical: true));
        }
        finally { EarModel.Enabled = was; }
    }

    private static OpenFPS.Client.AudioEngine.Data.SpatialEmitter Voice(string key, float levelDb, Vector3 at)
    {
        var (gain, reference) = Loudness.Place(levelDb);
        return new OpenFPS.Client.AudioEngine.Data.SpatialEmitter
        {
            EntityId = 1, SoundId = key, PhysicalKey = key, IsSynth = true, Position = at,
            Mode = OpenFPS.Common.Components.PlaybackMode.LoopOne, Range = 500f, MinDistance = reference,
            Volume = gain, EarLevelDb = levelDb,
        };
    }

    // ── Compensation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoCompensationAtTheRealLevel()
    {
        var (lo, hi) = LoudnessCompensation.Shelves(65f, 65f);
        Assert.Equal(0f, lo, 3);
        Assert.Equal(0f, hi, 3);
    }

    /// <summary>Played 10 phon under its real level, the bass comes up about 4 dB (ISO 226:2023:
    /// +4.7 at 31.5 Hz, +3.7 at 63, +2.7 at 125) and the top a little; lifted, the reverse.</summary>
    [Fact]
    public void QuieterGetsBassBackLouderLosesIt()
    {
        var (lo, hi) = LoudnessCompensation.Shelves(80f, 70f);
        Assert.InRange(lo, 3.5f, 4.8f);
        Assert.InRange(hi, 0.8f, 2.4f);
        var (lo2, hi2) = LoudnessCompensation.Shelves(70f, 80f);
        Assert.Equal(-lo, lo2, 1);
        Assert.Equal(-hi, hi2, 1);
    }

    /// <summary>The two shelves follow the contour difference within 2 dB from 31.5 Hz to 12.5 kHz.</summary>
    [Theory]
    [InlineData(80f, 70f)]
    [InlineData(90f, 65f)]
    [InlineData(45f, 60f)]
    public void TheShelvesFitTheContours(float real, float played)
    {
        Span<float> target = stackalloc float[29];
        LoudnessCompensation.Target(real, played, target);
        var (lo, hi) = LoudnessCompensation.Shelves(real, played);
        var f = EqualLoudness.FrequenciesHz;
        for (int i = 2; i < 29; i++)
        {
            float got = LoudnessCompensation.ResponseDb(f[i], 48000f, lo, hi);
            Assert.True(MathF.Abs(got - target[i]) < 2.0f, $"{f[i]} Hz: {got:F1} dB against {target[i]:F1}");
        }
    }

    /// <summary>Near the threshold nothing is boosted toward it: below 20 phon the contours are not
    /// specified, and both levels are held there.</summary>
    [Fact]
    public void NothingBelowTwentyPhon()
    {
        var (lo, hi) = LoudnessCompensation.Shelves(15f, 3f);
        Assert.Equal(0f, lo, 3);
        Assert.Equal(0f, hi, 3);
        var (lo2, _) = LoudnessCompensation.Shelves(200f, 0f);
        Assert.True(lo2 <= LoudnessCompensation.MaxShelfDb);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static float[] Silent()
    {
        var l = new float[28];
        Array.Fill(l, -60f);
        return l;
    }

    private static Timbre Tone(float hz)
    {
        var l = new float[28];
        Array.Fill(l, -80f);
        l[Array.IndexOf(ZwickerLoudness.CentresHz.ToArray(), hz)] = 0f;
        return Timbre.FromBandLevels(l, $"tone {hz}");
    }

    /// <summary>An idling engine's kind of spectrum: nearly everything below 100 Hz.</summary>
    private static Timbre BassHeavy()
    {
        var l = new float[28];
        for (int b = 0; b < 28; b++) l[b] = b <= 6 ? 0f : -6f * (b - 6);
        return Timbre.FromBandLevels(l, "bass");
    }
}
