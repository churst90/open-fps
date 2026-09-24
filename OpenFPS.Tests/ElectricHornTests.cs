using System;
using System.Linq;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Signals;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The electric car horn, measured on the model and not asserted about the design: the note it
/// actually plays, the level it actually makes at a metre, how it swells in and how it rings out.
/// </summary>
public class ElectricHornTests
{
    private const int Sr = 44100;
    private readonly ITestOutputHelper _o;
    public ElectricHornTests(ITestOutputHelper o) => _o = o;

    public static TheoryData<string> Keys => new() { "disc_pair", "disc_single", "trumpet_pair", "moto_disc" };

    private static float[] Render(ElectricHorn h, float onSec, float offSec)
    {
        var buf = new float[(int)((onSec + offSec) * Sr)];
        int on = (int)(onSec * Sr);
        for (int i = 0; i < buf.Length; i++) { h.Blowing = i < on; h.Step(); buf[i] = h.Out; }
        return buf;
    }

    private static double Rms(float[] x, int from, int to)
    {
        double e = 0; for (int i = from; i < to; i++) e += x[i] * (double)x[i];
        return Math.Sqrt(e / Math.Max(1, to - from));
    }

    /// <summary>Amplitude of one frequency in a buffer (Goertzel, Hann-windowed).</summary>
    private static double Tone(float[] x, int from, int to, float hz)
    {
        double w = 2 * Math.PI * hz / Sr, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        int n = to - from;
        for (int i = 0; i < n; i++)
        {
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            double s = x[from + i] * win + c * s1 - s2; s2 = s1; s1 = s;
        }
        return Math.Sqrt(s1 * s1 + s2 * s2 - c * s1 * s2) / n;
    }

    [Theory, MemberData(nameof(Keys))]
    public void EachHornPlaysItsDeclaredNote(string key)
    {
        var spec = ElectricHornSpec.ByName(key);
        foreach (var l in new ElectricHorn(spec, Sr, 11).Describe()) _o.WriteLine(l);
        // Each unit of a set on its own: a pair's combined waveform has the two notes' common
        // subharmonic for a period, which is the pair and not either horn.
        foreach (var unit in spec.Units)
        {
            var one = spec with { Units = new[] { unit } };
            var buf = Render(new ElectricHorn(one, Sr, 11), 0.6f, 0f);
            var steady = buf.Skip((int)(0.2f * Sr)).Take((int)(0.3f * Sr)).ToArray();
            float hz = ElectricHorn.NoteHz(steady, Sr, unit.Hz);
            _o.WriteLine($"{key}: declared {unit.Hz:F0} Hz, measured {hz:F1} Hz");
            Assert.InRange(hz, unit.Hz * 0.98f, unit.Hz * 1.02f);
        }
    }

    [Theory, MemberData(nameof(Keys))]
    public void LevelAtOneMetreIsTheAnchor(string key)
    {
        var spec = ElectricHornSpec.ByName(key);
        var buf = Render(new ElectricHorn(spec, Sr, 11), 1.2f, 0f);
        double rms = Rms(buf, (int)(0.3f * Sr), (int)(1.1f * Sr));
        double db = 20 * Math.Log10(rms / 2e-5);
        _o.WriteLine($"{key}: {db:F1} dB at 1 m, anchor {spec.ReferenceDb:F0}");
        Assert.InRange(db, spec.ReferenceDb - 1.5, spec.ReferenceDb + 1.5);
    }

    /// <summary>
    /// Let go and it rings OUT, and then it is gone. The ring-out is deliberate: the relay lets go
    /// through its suppression diode (RelayBreakSeconds) and the buzzer strikes softer and then not
    /// at all before the diaphragm and tone disc ring down. The first version stopped dead in ten
    /// milliseconds and was called "staccato", so this asserts both ends: still audibly fading
    /// 10-20 ms after release (above -30 dB), well down by 50-60 ms (below -30 dB), and silent by
    /// 100-110 ms (below -60 dB). A horn that hangs on is a fault as much as one that clicks off.
    /// </summary>
    [Theory, MemberData(nameof(Keys))]
    public void ItRingsOutWhenTheButtonIsReleased(string key)
    {
        var spec = ElectricHornSpec.ByName(key);
        var buf = Render(new ElectricHorn(spec, Sr, 11), 0.6f, 0.2f);
        int rel = (int)(0.6f * Sr);
        double steady = Rms(buf, (int)(0.3f * Sr), rel);
        double Db(float fromMs, float toMs)
            => 20 * Math.Log10(Rms(buf, rel + (int)(fromMs / 1000f * Sr), rel + (int)(toMs / 1000f * Sr)) / steady + 1e-15);
        double early = Db(10, 20), mid = Db(50, 60), late = Db(100, 110);
        _o.WriteLine($"{key}: {early:F0} dB at 10-20 ms after release, {mid:F0} at 50-60, {late:F0} at 100-110");
        Assert.True(early > -30, $"{key} cut off at {early:F0} dB 10 ms after release: staccato, no ring-out");
        Assert.True(mid < -30, $"{key} still at {mid:F0} dB 50 ms after release");
        Assert.True(late < -60, $"{key} still sounding at {late:F0} dB 100 ms after release");
    }

    /// <summary>
    /// It swells in over a couple of dozen milliseconds: the relay seats (RelayMakeSeconds) and the
    /// diaphragm swings short of the pole for its first cycles. Heard as instant, but not a click —
    /// the 2 ms step it used to be was the other half of "staccato". So -6 dB of the steady level
    /// is reached no sooner than 8 ms and no later than 30.
    /// </summary>
    [Theory, MemberData(nameof(Keys))]
    public void ItSwellsInQuickly(string key)
    {
        var spec = ElectricHornSpec.ByName(key);
        var buf = Render(new ElectricHorn(spec, Sr, 11), 0.6f, 0f);
        double steady = Rms(buf, (int)(0.3f * Sr), (int)(0.6f * Sr));
        int w = (int)(0.0025f * Sr);
        float reachedMs = float.MaxValue;
        for (int i = 0; i + w < buf.Length; i += w)
            if (Rms(buf, i, i + w) >= steady * 0.5) { reachedMs = (i + w) * 1000f / Sr; break; }
        _o.WriteLine($"{key}: -6 dB of steady level by {reachedMs:F1} ms");
        Assert.InRange(reachedMs, 8f, 30f);
    }

    [Theory]
    [InlineData("disc_pair")]
    [InlineData("trumpet_pair")]
    public void BothNotesOfAPairAreThere(string key)
    {
        var spec = ElectricHornSpec.ByName(key);
        Assert.Equal(2, spec.Units.Length);
        var buf = Render(new ElectricHorn(spec, Sr, 11), 0.8f, 0f);
        int a = (int)(0.3f * Sr), b = (int)(0.8f * Sr);
        float lo = spec.Units.Min(u => u.Hz), hi = spec.Units.Max(u => u.Hz);
        double atLo = Tone(buf, a, b, lo), atHi = Tone(buf, a, b, hi);
        // A frequency between them that is neither note nor a harmonic of either.
        double between = Tone(buf, a, b, 0.5f * (lo + hi));
        _o.WriteLine($"{key}: {lo:F0} Hz {20 * Math.Log10(atLo / between):F0} dB, {hi:F0} Hz {20 * Math.Log10(atHi / between):F0} dB above the gap at {0.5f * (lo + hi):F0} Hz");
        Assert.True(atLo > 10 * between, $"low note {lo} Hz missing");
        Assert.True(atHi > 10 * between, $"high note {hi} Hz missing");
        // ...and neither buries the other.
        Assert.InRange(20 * Math.Log10(atLo / atHi), -10, 10);
    }

    [Fact]
    public void UnknownPresetThrows()
        => Assert.Throws<ArgumentException>(() => ElectricHornSpec.ByName("klaxon"));

    [Fact]
    public void TheArmatureStrikesThePoleOnceACycle()
    {
        // The buzz is the strike. A unit that never reaches the pole is a doorbell; one that hits
        // twice a cycle chatters.
        foreach (var key in ElectricHornSpec.Presets.Keys)
        {
            var lines = new ElectricHorn(ElectricHornSpec.ByName(key), Sr, 11).Describe().ToList();
            foreach (var l in lines) _o.WriteLine(l);
            foreach (var l in lines.Where(l => l.Contains("strikes/cycle")))
            {
                int i = l.IndexOf("strikes/cycle ", StringComparison.Ordinal) + 14;
                float spc = float.Parse(l.Substring(i).Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(spc, 0.95f, 1.05f);
            }
        }
    }
}
