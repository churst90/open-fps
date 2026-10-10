using OpenFPS.Client.AudioEngine.Core.Stove;
using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The gas hob (docs/GAS_HOB.md): its injectors against the manufacturers' tables, its state on the wire and
/// the interact key, and the hob itself: a knob turned to full lights after a tick or two and the cook lets
/// go within a second (or, with flame safety, once the thermocouple holds), a re-ignition module stops on its
/// own, a knob left low never lights, a slow light burns more gas, a flame turned off goes out and its safety
/// valve shuts later, and the spark's crack carries its energy above the presence region.
/// </summary>
public class GasHobTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _o;
    public GasHobTests(ITestOutputHelper o) => _o = o;

    private static float[] Run(GasHobSynth h, float seconds, Action<GasHobSynth, float>? each = null)
    {
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
        {
            if (each != null && i % 480 == 0) each(h, i / (float)Rate);
            x[i] = h.Next();
        }
        return x;
    }

    private static double Db(double pa) => 20 * Math.Log10(Math.Max(1e-12, pa) / 20e-6);

    private static double Leq(float[] x, float from, float to)
    {
        int a = (int)(from * Rate), b = (int)(to * Rate);
        double e = 0;
        for (int i = a; i < b; i++) e += (double)x[i] * x[i];
        return Db(Math.Sqrt(e / (b - a)));
    }

    [Fact]
    public void TheInjectorsPassTheGasTheManualsSay()
    {
        var hob = GasHobSpec.FourBurnerNatural;
        // Whirlpool AKT 300: 286 l/h for the rapid burner, 157 l/h for the semi-rapid, G20 at 20 mbar.
        Assert.InRange(hob.FullFlow(hob.Burners[0]) * 3.6e6f, 278f, 294f);
        Assert.InRange(hob.FullFlow(hob.Burners[2]) * 3.6e6f, 152f, 162f);
        // Gross ratings: 3.00, 1.65 and 1.00 kW.
        Assert.InRange(hob.RatedKw(hob.Burners[0]), 2.9f, 3.1f);
        Assert.InRange(hob.RatedKw(hob.Burners[2]), 1.6f, 1.7f);
        Assert.InRange(hob.RatedKw(hob.Burners[1]), 0.9f, 1.05f);
        // The propane set gives the same heat through smaller holes.
        var lpg = GasHobSpec.FourBurnerPropane;
        Assert.InRange(lpg.RatedKw(lpg.Burners[0]) / hob.RatedKw(hob.Burners[0]), 0.95f, 1.05f);
        Assert.True(lpg.Burners[0].InjectorMm < hob.Burners[0].InjectorMm);
    }

    [Fact]
    public void ASparkLightsOnlyWhatIsFlammable()
    {
        var gas = FuelGas.Methane;
        float stoich = gas.FractionAt(1f);
        Assert.InRange(stoich, 0.090f, 0.100f);
        Assert.True(gas.IgnitionEnergyMj(stoich) < 0.5f);
        Assert.True(gas.IgnitionEnergyMj(0.03f) == float.PositiveInfinity);
        Assert.True(gas.IgnitionEnergyMj(0.051f) < 15f, "a 15 mJ spark lights at the lean limit");
        Assert.Equal(0f, gas.BurningVelocity(0.2f));
        Assert.InRange(gas.BurningVelocity(gas.FractionAt(1.075f)), 0.36f, 0.38f);
    }

    [Fact]
    public void TheKeyCarriesTheSettingsAndWhenTheyChanged()
    {
        Assert.True(HobKey.TryParse("stove:hob4", out var bare));
        Assert.Equal("hob4", bare.Preset);
        Assert.False(bare.AnyOn);
        var k = new HobKey("hob4", "0000", "3000", 1234.5);
        Assert.True(HobKey.TryParse(k.Format(), out var back));
        Assert.Equal(k, back);
        Assert.Equal(3, back.Setting(0));
        Assert.True(back.AnyOn);
        Assert.False(HobKey.TryParse("stove:hob4/0000>300@1", out _));
        Assert.False(HobKey.TryParse("stove:hob4/0000>3009@1", out _));
        Assert.Equal("hob4", GasHobSpec.ByName(k.Format()).Burners.Length == 4 ? "hob4" : "");
    }

    [Fact]
    public void TheInteractKeyLightsEachBurnerInTurnThenTurnsThemAllOff()
    {
        string key = "stove:hob4";
        var lines = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            lines.Add(HobControls.Press(key, 100 + i, out key));
            _o.WriteLine($"{lines[^1]}  {key}");
        }
        Assert.Equal("You light the front left burner.", lines[0]);
        Assert.Equal("You light the back right burner.", lines[3]);
        Assert.Equal("You turn every burner off.", lines[4]);
        Assert.True(HobKey.TryParse(key, out var last));
        Assert.Equal("3333", last.From);
        Assert.Equal("0000", last.To);
        Assert.Equal(104.0, last.At, 2);
        Assert.Equal("You light the burner.", HobControls.Press("stove:hob1", 1, out _));
    }

    /// <summary>Runs a light of the first burner and returns when it caught and when each spark came, s.</summary>
    private static (float Caught, List<float> Sparks) Light(GasHobSynth h, float seconds)
    {
        h.Begin("0000", "3000", 0f);
        float caught = -1f;
        var sparks = new List<float>();
        int last = h.Sparks;
        for (int i = 0; i < (int)(seconds * Rate); i++)
        {
            h.Next();
            if (h.Sparks != last) { last = h.Sparks; sparks.Add(i / (float)Rate); }
            if (caught < 0f && h.IsLit(0)) caught = i / (float)Rate;
        }
        return (caught, sparks);
    }

    [Fact]
    public void TurnedToFullItLightsAfterATickOrTwoAndTheCookLetsGoWithinASecond()
    {
        // No flame safety: nothing needs the knob held once the flame has caught, so the cook lets go as
        // soon as they see it, and the module (which sparks only while a knob is in) stops.
        var h = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 7);
        var (caught, sparks) = Light(h, 9f);
        Assert.True(h.IsLit(0));
        Assert.False(h.IsLit(1));
        Assert.InRange(h.FailedSparksBeforeLight(0), 0, 2);
        Assert.InRange(h.LastLightUpJoules(0), 50f, 600f);
        Assert.False(h.HandBusy);
        int after = sparks.Count(t => t > caught + 0.005f);
        _o.WriteLine($"caught at {caught:F2} s; {sparks.Count} sparks, {after} after it, the last {sparks[^1] - caught:F2} s after");
        Assert.InRange(after, 1, 4);
        Assert.InRange(sparks[^1] - caught, 0.1f, 1.0f);
        // It stays lit when let go.
        Run(h, 2f);
        Assert.True(h.IsLit(0));
    }

    [Fact]
    public void WithFlameSafetyTheKnobIsHeldUntilTheThermocoupleHolds()
    {
        // The sparks go on while the knob is held, as they do on a European hob, until the thermocouple can
        // hold the gas on (3 s) and the cook's margin (1 s): Bosch asks for 4 s. Then the flame stays.
        var h = new GasHobSynth(GasHobSpec.FourBurnerFlameSafety, Rate, 7);
        var (caught, sparks) = Light(h, 9f);
        float tail = sparks[^1] - caught;
        _o.WriteLine($"caught at {caught:F2} s; {sparks.Count} sparks, the last {tail:F2} s after");
        Assert.InRange(tail, 3.3f, 4.5f);
        Assert.False(h.HandBusy);
        Run(h, 3f);
        Assert.True(h.IsLit(0), "the valve holds once the thermocouple has heated");
    }

    [Fact]
    public void AReignitionModuleStopsWhenItSensesTheFlame()
    {
        var h = new GasHobSynth(GasHobSpec.FourBurnerReignition, Rate, 7);
        var (caught, sparks) = Light(h, 6f);
        _o.WriteLine($"caught at {caught:F2} s; {sparks.Count} sparks, the last at {sparks[^1]:F2} s");
        Assert.True(h.IsLit(0));
        Assert.DoesNotContain(sparks, t => t > caught + 0.25f);
    }

    [Fact]
    public void TheSparksCrackCarriesItsEnergyAboveThePresenceRegion()
    {
        // The crack of a few-microsecond spark rises 6 dB an octave through hearing; recorded hob ticks sit
        // 14 dB lower at 2 kHz than at 8 kHz (median of fourteen, docs/GAS_HOB.md section 9). The old 30 µs
        // spark and its ringing cap put as much at 2 kHz as at 8. Nothing rings after it: the cap is not
        // struck, and the module's own tick is muffled under the hob.
        var h = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 7) { FlamePart = 0f, HissPart = 0f, LightUpPart = 0f, ClickPart = 0f };
        h.Begin("0000", "0000", 0f);
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Push, Burner = 1, Seconds = 1.2f });
        var x = Run(h, 1.2f);
        int at = Array.FindIndex(x, v => MathF.Abs(v) > 0.05f);
        Assert.True(at > 0, "a spark");
        double Band(int from, int n, double lo, double hi)
        {
            double e = 0;
            for (int k = (int)Math.Ceiling(lo * n / Rate); k <= (int)(hi * n / Rate); k++)
            {
                double re = 0, im = 0;
                for (int i = 0; i < n; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
                    re += x[from + i] * w * Math.Cos(2 * Math.PI * k * i / n);
                    im -= x[from + i] * w * Math.Sin(2 * Math.PI * k * i / n);
                }
                e += re * re + im * im;
            }
            return e;
        }
        int a = at - 48;
        double tilt = 10 * Math.Log10(Band(a, 512, 1414, 2828) / Band(a, 512, 5657, 11314));
        double ring = 10 * Math.Log10(Band(at + 480, 960, 1000, 12000) / Band(a, 960, 1000, 12000));
        _o.WriteLine($"2 kHz octave against 8 kHz {tilt:F1} dB; 10-30 ms after the spark against the spark {ring:F1} dB");
        Assert.True(tilt < -4.0, $"the 2 kHz octave is {tilt:F1} dB against 8 kHz");
        Assert.True(ring < -50.0, $"something rings after the spark: {ring:F1} dB");
    }

    [Fact]
    public void AKnobBarelyOpenNeverLights()
    {
        // A trickle (30°, 7 % of full): the ports' jets do not reach the electrode, and the gas drifts off
        // leaner than the lean limit. A hob is lit on full. (Turned to low it passes full on the way, and
        // lights there.)
        var h = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 3);
        h.Begin("0000", "0000", 0f);
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Push, Burner = 0, Seconds = 0.1f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = 0, Degrees = 30f, Seconds = 0.6f });
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Wait, Seconds = 8f });
        Run(h, 9f);
        Assert.False(h.IsLit(0));
        Assert.True(h.Sparks > 30);
        // Pushed on to full, the next spark or two light it.
        h.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = 0, Degrees = GasHobSpec.FullDegrees, Seconds = 0.3f });
        Run(h, 1.5f);
        Assert.True(h.IsLit(0));
    }

    [Fact]
    public void ASlowLightFailsWhileTheGasGathersAndBurnsMoreOfItAtOnce()
    {
        var quick = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 7);
        quick.Begin("0000", "3000", 0f);
        Run(quick, 6f);
        var slow = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 7);
        slow.Begin("0000", "0000", 0f);
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Push, Burner = 0, Seconds = 0.15f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = 0, Degrees = 36f, Seconds = 0.9f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Wait, Seconds = 2.6f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Turn, Burner = 0, Degrees = GasHobSpec.FullDegrees, Seconds = 0.4f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.WaitLit, Burner = 0, Hold = 3f, Seconds = 10f });
        slow.Enqueue(new GasHobSynth.HandAction { Act = GasHobSynth.Act.Release, Burner = 0, Seconds = 0.1f });
        Run(slow, 10f);
        Assert.True(slow.IsLit(0));
        Assert.True(slow.FailedSparksBeforeLight(0) >= 6, $"{slow.FailedSparksBeforeLight(0)} failed sparks");
        Assert.True(slow.LastLightUpJoules(0) > 2f * quick.LastLightUpJoules(0),
            $"slow {slow.LastLightUpJoules(0):F0} J against quick {quick.LastLightUpJoules(0):F0} J");
    }

    [Fact]
    public void TurnedOffTheFlameGoesOutAndTheSafetyValveShutsLater()
    {
        var h = new GasHobSynth(GasHobSpec.FourBurnerFlameSafety, Rate, 5);
        h.Begin("3000", "3000", 0f);
        var on = Run(h, 2f);
        Assert.True(h.IsLit(0));
        h.Change("3000", "0000");
        var off = Run(h, 3f);
        Assert.False(h.IsLit(0));
        Assert.True(Leq(off, 2f, 3f) < Leq(on, 0.5f, 2f) - 30, "out within a second");
        // The armature drops when the thermocouple has cooled: a click well after the flame.
        var later = Run(h, 25f);
        int loud = Array.FindIndex(later, v => Math.Abs(v) > 20e-6 * Math.Pow(10, 35 / 20.0));
        Assert.InRange(loud / (float)Rate, 8f, 22f);
        Assert.True(h.Idle);
        // A hob without flame safety has no valve to shut: nothing after the flame.
        var plain = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 5);
        plain.Begin("3000", "3000", 0f);
        Run(plain, 1f);
        plain.Change("3000", "0000");
        Run(plain, 3f);
        var quiet = Run(plain, 25f);
        Assert.DoesNotContain(quiet, v => Math.Abs(v) > 20e-6 * Math.Pow(10, 35 / 20.0));
    }

    [Fact]
    public void AVoiceMadeLateHearsTheHobAsItIs()
    {
        // Silent catch-up: a client arriving 30 s after a burner was lit hears it burning, the knob let go.
        var late = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 9);
        late.Begin("0000", "3000", 30f);
        Assert.True(late.IsLit(0));
        Assert.False(late.HandBusy);
        Assert.Equal(GasHobSpec.FullDegrees, late.KnobDegrees(0), 1);
        var x = Run(late, 1f);
        Assert.InRange(Leq(x, 0.2f, 1f), 40, 46);
        // Turned off a minute ago: nothing.
        var gone = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 9);
        gone.Begin("3000", "0000", 60f);
        Assert.False(gone.IsLit(0));
        Assert.True(gone.Idle);
    }

    [Fact]
    public void TheSameHobRendersTheSameAndAllocatesNothing()
    {
        var a = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 11);
        var b = new GasHobSynth(GasHobSpec.FourBurnerNatural, Rate, 11);
        a.Begin("0000", "3300", 0f);
        b.Begin("0000", "3300", 0f);
        Run(a, 0.5f); Run(b, 0.5f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        float diff = 0f;
        for (int i = 0; i < 8 * Rate; i++) diff = MathF.Max(diff, MathF.Abs(a.Next() - b.Next()));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0f, diff);
        Assert.Equal(0, allocated);
        Assert.True(a.IsLit(0));
    }

    [Fact]
    public void EveryBurnerOnFullIsTheDeclaredLevel()
    {
        foreach (var (name, spec) in new[] { ("hob4", GasHobSpec.FourBurnerNatural), ("hob4_propane", GasHobSpec.FourBurnerPropane), ("hob1", GasHobSpec.SingleBurner) })
        {
            var h = new GasHobSynth(spec, Rate, 13);
            string all = new('3', spec.Burners.Length);
            h.Begin(all, all, 0f);
            var x = Run(h, 6f);
            double leq = Leq(x, 1f, 6f);
            _o.WriteLine($"{name}: {leq:F1} dB against {spec.SourceLevelDb:F1} declared");
            Assert.InRange(leq, spec.SourceLevelDb - 1.0, spec.SourceLevelDb + 1.0);
        }
    }

    [Fact]
    public void TheVoiceTakesAKnobTurnedFromItsKey()
    {
        var spec = GasHobSpec.FourBurnerNatural;
        var voice = new StoveVoiceState(spec, new HobKey("hob4", "0000", "0000", WindField.Now() - 5).Format(), Rate, 3);
        var block = new float[1024];
        voice.SetKey(new HobKey("hob4", "0000", "3000", WindField.Now()).Format());
        for (int i = 0; i < 8 * Rate / block.Length; i++) voice.Render(block);
        Assert.True(voice.Hob.IsLit(0));
    }
}
